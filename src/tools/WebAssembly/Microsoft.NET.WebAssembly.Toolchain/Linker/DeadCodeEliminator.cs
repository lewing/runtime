// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Dead code elimination engine for the Wasm linker.
/// Builds a dependency graph from relocation data, performs BFS from root symbols,
/// and computes compaction maps for each index space.
/// </summary>
internal sealed class DeadCodeEliminator
{
    // adjacency: merged function index → merged function indices it calls
    private readonly Dictionary<uint, HashSet<uint>> _funcToFuncs = new();
    // merged function index → merged global indices it references
    private readonly Dictionary<uint, HashSet<uint>> _funcToGlobals = new();
    // merged function index → merged data segment indices it references
    private readonly Dictionary<uint, HashSet<uint>> _funcToData = new();
    // merged function index → merged tag indices it references
    private readonly Dictionary<uint, HashSet<uint>> _funcToTags = new();
    // merged element segment index → list of merged function indices it contains
    private readonly Dictionary<uint, List<uint>> _elemToFuncs = new();
    // merged global index → merged data segments it references
    private readonly Dictionary<uint, HashSet<uint>> _globalToData = new();

    public DceLiveSet ComputeLiveSets(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        SymbolTable symbolTable,
        uint mergedFuncImportCount,
        LinkerOptions options)
    {
        BuildGraph(inputs, indexMaps, symbolTable);

        var liveFunctions    = new HashSet<uint>();
        var liveGlobals      = new HashSet<uint>();
        var liveDataSegments = new HashSet<uint>();
        var liveElements     = new HashSet<uint>();
        var liveTags         = new HashSet<uint>();

        // Note: imports are NOT added as unconditional roots. They become live only
        // when BFS reaches them from explicit roots via relocation-derived edges.
        // This allows a no-exports build to produce a near-empty output module.

        // Collect roots from symbol table
        CollectRoots(symbolTable, options, liveFunctions, liveGlobals, liveDataSegments, liveTags);

        // Collect roots from init functions in each module
        for (int m = 0; m < inputs.Count; m++)
        {
            var linking = inputs[m].LinkingSection;
            if (linking is null) continue;
            foreach (var initFn in linking.InitFunctions)
            {
                if (initFn.SymbolIndex < (uint)linking.Symbols.Count)
                {
                    var sym = linking.Symbols[(int)initFn.SymbolIndex];
                    if (sym.Index.HasValue)
                    {
                        uint localIdx = checked((uint)sym.Index.Value);
                        if (indexMaps[m].Functions.TryGetValue(localIdx, out uint mergedIdx))
                            liveFunctions.Add(mergedIdx);
                    }
                }
            }
        }

        // Conservative fallback: plain wasm modules (no linking section, no reloc sections)
        // have no dependency graph data, so treat all their items as live roots.
        for (int m = 0; m < inputs.Count; m++)
        {
            if (inputs[m].LinkingSection is not null) continue;    // proper object file; handled below
            if (inputs[m].RelocationSections.Count > 0) continue; // has reloc data

            foreach (var (_, mergedIdx) in indexMaps[m].Functions) liveFunctions.Add(mergedIdx);
            foreach (var (_, mergedIdx) in indexMaps[m].Globals)   liveGlobals.Add(mergedIdx);
            foreach (var (_, mergedIdx) in indexMaps[m].DataSegments) liveDataSegments.Add(mergedIdx);
            foreach (var (_, mergedIdx) in indexMaps[m].Tags)     liveTags.Add(mergedIdx);
            foreach (var (_, mergedIdx) in indexMaps[m].Elements)  liveElements.Add(mergedIdx);
        }

        // Conservative fallback for relocatable objects with a linking section but no
        // relocation sections: functions are tracked via symbol cross-references, but
        // data segments and globals have no relocation edges connecting them to code.
        // Keep them all live so we don't silently drop side-effect data.
        for (int m = 0; m < inputs.Count; m++)
        {
            if (inputs[m].LinkingSection is null) continue;         // plain module; handled above
            if (inputs[m].RelocationSections.Count > 0) continue;  // has reloc data; graph is accurate

            foreach (var (_, mergedIdx) in indexMaps[m].Globals)   liveGlobals.Add(mergedIdx);
            foreach (var (_, mergedIdx) in indexMaps[m].DataSegments) liveDataSegments.Add(mergedIdx);
        }

        // BFS pass 1: propagate from explicit roots
        BfsFunctions(liveFunctions, liveGlobals, liveDataSegments, liveTags);

        // Conservative indirect-call handling: if any non-import function is live and
        // element segments exist, all element-segment members become roots.  When the
        // live set is empty (no exports / no entry point) we skip this to produce a
        // minimal output module, matching wasm-ld's behaviour.
        bool anyLiveUserCode = liveFunctions.Any(f => f >= mergedFuncImportCount);
        if (anyLiveUserCode && _elemToFuncs.Count > 0)
        {
            int prevCount = liveFunctions.Count;
            foreach (var (elemIdx, funcList) in _elemToFuncs)
            {
                liveElements.Add(elemIdx);
                foreach (var fi in funcList)
                    liveFunctions.Add(fi);
            }
            // BFS pass 2: propagate newly added element-segment roots
            if (liveFunctions.Count != prevCount)
                BfsFunctions(liveFunctions, liveGlobals, liveDataSegments, liveTags);
        }

        // Propagate: live globals → live data segments
        foreach (uint g in liveGlobals.ToArray())
        {
            if (_globalToData.TryGetValue(g, out var dataSet))
                foreach (var d in dataSet)
                    liveDataSegments.Add(d);
        }

        // Print removed items if requested
        if (options.PrintGcSections)
            PrintRemovedItems(inputs, indexMaps, liveFunctions);

        return new DceLiveSet
        {
            LiveFunctions    = liveFunctions,
            LiveGlobals      = liveGlobals,
            LiveDataSegments = liveDataSegments,
            LiveElements     = liveElements,
            LiveTags         = liveTags,
        };
    }

    public static void ComposeCompactionIntoIndexMaps(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        DceLiveSet liveSets)
    {
        // Before composing, record which local indices are dead per module
        var deadFuncsByModule = new List<HashSet<uint>>(inputs.Count);
        var deadGlobalsByModule = new List<HashSet<uint>>(inputs.Count);
        var deadDataSegsByModule = new List<HashSet<uint>>(inputs.Count);
        var deadTagsByModule = new List<HashSet<uint>>(inputs.Count);
        var deadElementsByModule = new List<HashSet<uint>>(inputs.Count);

        for (int m = 0; m < inputs.Count; m++)
        {
            var deadFuncs = new HashSet<uint>();
            foreach (var (localIdx, mergedIdx) in indexMaps[m].Functions)
                if (!liveSets.LiveFunctions!.Contains(mergedIdx))
                    deadFuncs.Add(localIdx);
            deadFuncsByModule.Add(deadFuncs);

            var deadGlobals = new HashSet<uint>();
            foreach (var (localIdx, mergedIdx) in indexMaps[m].Globals)
                if (!liveSets.LiveGlobals!.Contains(mergedIdx))
                    deadGlobals.Add(localIdx);
            deadGlobalsByModule.Add(deadGlobals);

            var deadDataSegs = new HashSet<uint>();
            foreach (var (localIdx, mergedIdx) in indexMaps[m].DataSegments)
                if (!liveSets.LiveDataSegments!.Contains(mergedIdx))
                    deadDataSegs.Add(localIdx);
            deadDataSegsByModule.Add(deadDataSegs);

            var deadTags = new HashSet<uint>();
            foreach (var (localIdx, mergedIdx) in indexMaps[m].Tags)
                if (!liveSets.LiveTags!.Contains(mergedIdx))
                    deadTags.Add(localIdx);
            deadTagsByModule.Add(deadTags);

            var deadElements = new HashSet<uint>();
            foreach (var (localIdx, mergedIdx) in indexMaps[m].Elements)
                if (!liveSets.LiveElements!.Contains(mergedIdx))
                    deadElements.Add(localIdx);
            deadElementsByModule.Add(deadElements);
        }

        liveSets.DeadFunctionsByModule = deadFuncsByModule;
        liveSets.DeadGlobalsByModule = deadGlobalsByModule;
        liveSets.DeadDataSegsByModule = deadDataSegsByModule;
        liveSets.DeadTagsByModule = deadTagsByModule;
        liveSets.DeadElementsByModule = deadElementsByModule;

        // Now compose compaction maps into IndexMaps
        var funcCompaction = BuildCompactionMap(liveSets.LiveFunctions);
        var globalCompaction = BuildCompactionMap(liveSets.LiveGlobals);
        var dataCompaction = BuildCompactionMap(liveSets.LiveDataSegments);
        var tagCompaction = BuildCompactionMap(liveSets.LiveTags);
        var elemCompaction = BuildCompactionMap(liveSets.LiveElements);

        for (int m = 0; m < inputs.Count; m++)
        {
            ComposeMap(indexMaps[m].Functions,    funcCompaction);
            ComposeMap(indexMaps[m].Globals,      globalCompaction);
            ComposeMap(indexMaps[m].DataSegments, dataCompaction);
            ComposeMap(indexMaps[m].Tags,         tagCompaction);
            ComposeMap(indexMaps[m].Elements,     elemCompaction);
        }
    }

    /// <summary>
    /// Returns a filtered and compacted merged types list. Also rewrites IndexMap.Types
    /// so that local type indices now map to compacted type indices, and updates
    /// <paramref name="mergedImports"/> in-place to use the new compacted type indices.
    /// </summary>
    public static List<FuncType> CompactTypes(
        List<FuncType> mergedTypes,
        DceLiveSet liveSets,
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        List<Import> mergedImports)
    {
        var liveTypes = new HashSet<uint>();

        // Keep types used by LIVE function/tag imports only.
        // Use DeadFunctionsByModule / DeadTagsByModule (computed before ComposeCompactionIntoIndexMaps
        // ran) rather than re-deriving liveness from indexMaps[m].Functions, which has already been
        // rewritten to compacted indices. Comparing compacted indices against the pre-compaction
        // LiveFunctions set gives false negatives when dead imports precede live ones (issue #8).
        for (int m = 0; m < inputs.Count; m++)
        {
            uint localFuncImportIdx = 0;
            uint localTagImportIdx = 0;
            foreach (var import in inputs[m].Imports)
            {
                if (import.Descriptor is ImportDescriptor.Func func)
                {
                    if (liveSets.DeadFunctionsByModule?[m].Contains(localFuncImportIdx) != true &&
                        indexMaps[m].Types.TryGetValue((uint)func.TypeIndex, out uint mergedTypeIdx))
                    {
                        liveTypes.Add(mergedTypeIdx);
                    }
                    localFuncImportIdx++;
                }
                else if (import.Descriptor is ImportDescriptor.Tag tag)
                {
                    if (liveSets.DeadTagsByModule?[m].Contains(localTagImportIdx) != true &&
                        indexMaps[m].Types.TryGetValue((uint)tag.TypeIndex, out uint mergedTypeIdx))
                    {
                        liveTypes.Add(mergedTypeIdx);
                    }
                    localTagImportIdx++;
                }
            }
        }

        // Collect type indices from live functions
        for (int m = 0; m < inputs.Count; m++)
        {
            uint localFuncImports = 0;
            foreach (var import in inputs[m].Imports)
                if (import.Descriptor is ImportDescriptor.Func) localFuncImports++;

            for (int j = 0; j < inputs[m].Functions.Count; j++)
            {
                uint localFuncIdx = localFuncImports + (uint)j;
                if (liveSets.DeadFunctionsByModule?[m].Contains(localFuncIdx) == true)
                    continue;

                uint localTypeIdx = (uint)inputs[m].Functions[j].TypeIndex;
                if (indexMaps[m].Types.TryGetValue(localTypeIdx, out uint mergedTypeIdx))
                    liveTypes.Add(mergedTypeIdx);
            }
        }

        // Keep types used by live tags
        for (int m = 0; m < inputs.Count; m++)
        {
            uint localTagImports = 0;
            foreach (var import in inputs[m].Imports)
                if (import.Descriptor is ImportDescriptor.Tag) localTagImports++;

            for (int j = 0; j < inputs[m].Tags.Count; j++)
            {
                uint localTagIdx = localTagImports + (uint)j;
                if (liveSets.DeadTagsByModule?[m].Contains(localTagIdx) == true)
                    continue;
                var tag = inputs[m].Tags[j];
                uint localTypeIdx = (uint)tag.TypeIndex;
                if (indexMaps[m].Types.TryGetValue(localTypeIdx, out uint mergedTypeIdx))
                    liveTypes.Add(mergedTypeIdx);
            }
        }

        var typeCompaction = BuildCompactionMap(liveTypes);

        // Compose into IndexMaps.Types
        for (int m = 0; m < inputs.Count; m++)
            ComposeMap(indexMaps[m].Types, typeCompaction);

        // Update mergedImports type indices (they were built before compaction)
        for (int i = 0; i < mergedImports.Count; i++)
        {
            if (mergedImports[i].Descriptor is ImportDescriptor.Func func &&
                typeCompaction.TryGetValue((uint)func.TypeIndex, out uint newFuncTypeIdx))
            {
                mergedImports[i] = mergedImports[i] with
                {
                    Descriptor = new ImportDescriptor.Func(checked((int)newFuncTypeIdx))
                };
            }
            else if (mergedImports[i].Descriptor is ImportDescriptor.Tag tag &&
                     typeCompaction.TryGetValue((uint)tag.TypeIndex, out uint newTagTypeIdx))
            {
                mergedImports[i] = mergedImports[i] with
                {
                    Descriptor = new ImportDescriptor.Tag(checked((int)newTagTypeIdx))
                };
            }
        }

        // Build compacted types list
        var sortedLive = liveTypes.Order().ToArray();
        var result = new List<FuncType>(sortedLive.Length);
        foreach (var idx in sortedLive)
            if (idx < (uint)mergedTypes.Count)
                result.Add(mergedTypes[(int)idx]);

        return result;
    }

    private void BuildGraph(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        SymbolTable symbolTable)
    {
        for (int m = 0; m < inputs.Count; m++)
        {
            var module = inputs[m];
            var map = indexMaps[m];

            uint funcImportCount = 0;
            uint globalImportCount = 0;
            uint tagImportCount = 0;
            foreach (var import in module.Imports)
            {
                switch (import.Descriptor)
                {
                    case ImportDescriptor.Func:   funcImportCount++;   break;
                    case ImportDescriptor.Global: globalImportCount++; break;
                    case ImportDescriptor.Tag:    tagImportCount++;    break;
                }
            }

            // Build sorted array of (CodeSectionOffset, mergedFuncIndex) for binary search
            var funcOffsets = new (uint offset, uint mergedIdx)[module.Functions.Count];
            for (int j = 0; j < module.Functions.Count; j++)
            {
                uint localIdx = funcImportCount + (uint)j;
                map.Functions.TryGetValue(localIdx, out uint mergedIdx);
                funcOffsets[j] = (module.Functions[j].CodeSectionOffset, mergedIdx);
            }

            // Process reloc.CODE section
            var codeReloc = module.RelocationSections.FirstOrDefault(
                static r => r.Name == "reloc.CODE");
            if (codeReloc is not null)
            {
                foreach (var reloc in codeReloc.Entries)
                {
                    uint callerMergedIdx = FindFunctionContaining(funcOffsets, reloc.Offset);
                    if (callerMergedIdx == uint.MaxValue) continue;

                    ProcessRelocation(reloc, module, map, callerMergedIdx, symbolTable);
                }
            }

            // Element segments: track which functions they reference
            for (int j = 0; j < module.Elements.Count; j++)
            {
                if (!map.Elements.TryGetValue((uint)j, out uint elemMerged)) continue;
                if (!_elemToFuncs.ContainsKey(elemMerged))
                    _elemToFuncs[elemMerged] = new List<uint>();

                foreach (var funcIdx in module.Elements[j].FunctionIndices)
                {
                    if (funcIdx >= 0 && map.Functions.TryGetValue((uint)funcIdx, out uint fMerged))
                        _elemToFuncs[elemMerged].Add(fMerged);
                }
            }
        }
    }

    private void ProcessRelocation(
        Relocation reloc,
        WasmModule module,
        IndexMap map,
        uint callerMergedIdx,
        SymbolTable symbolTable)
    {
        var linking = module.LinkingSection;
        if (linking is null) return;

        if (reloc.Index >= (uint)linking.Symbols.Count) return;
        var sym = linking.Symbols[(int)reloc.Index];

        switch (reloc.Type)
        {
            case RelocationType.R_WASM_FUNCTION_INDEX_LEB:
            case RelocationType.R_WASM_FUNCTION_INDEX_I32:
            {
                if (!sym.Index.HasValue) break;
                uint localIdx = checked((uint)sym.Index.Value);
                if ((sym.Flags & SymbolFlags.Undefined) != 0)
                {
                    if (sym.Name is not null &&
                        symbolTable.Symbols.TryGetValue(sym.Name, out var resolved) &&
                        resolved.MergedIndex.HasValue)
                    {
                        AddFuncEdge(callerMergedIdx, resolved.MergedIndex.Value);
                    }
                }
                else if (map.Functions.TryGetValue(localIdx, out uint targetMerged))
                {
                    AddFuncEdge(callerMergedIdx, targetMerged);
                }
                break;
            }

            case RelocationType.R_WASM_TABLE_INDEX_SLEB:
            case RelocationType.R_WASM_TABLE_INDEX_I32:
            case RelocationType.R_WASM_TABLE_INDEX_REL_SLEB:
            {
                if (!sym.Index.HasValue) break;
                uint localIdx = checked((uint)sym.Index.Value);
                if ((sym.Flags & SymbolFlags.Undefined) != 0)
                {
                    if (sym.Name is not null &&
                        symbolTable.Symbols.TryGetValue(sym.Name, out var resolved) &&
                        resolved.MergedIndex.HasValue)
                    {
                        AddFuncEdge(callerMergedIdx, resolved.MergedIndex.Value);
                    }
                }
                else if (map.Functions.TryGetValue(localIdx, out uint targetMerged))
                {
                    AddFuncEdge(callerMergedIdx, targetMerged);
                }
                break;
            }

            case RelocationType.R_WASM_GLOBAL_INDEX_LEB:
            case RelocationType.R_WASM_GLOBAL_INDEX_I32:
            {
                if (!sym.Index.HasValue) break;
                uint localIdx = checked((uint)sym.Index.Value);
                if ((sym.Flags & SymbolFlags.Undefined) != 0)
                {
                    if (sym.Name is not null &&
                        symbolTable.Symbols.TryGetValue(sym.Name, out var resolved) &&
                        resolved.MergedIndex.HasValue)
                    {
                        AddGlobalEdge(callerMergedIdx, resolved.MergedIndex.Value);
                    }
                }
                else if (map.Globals.TryGetValue(localIdx, out uint globalMerged))
                {
                    AddGlobalEdge(callerMergedIdx, globalMerged);
                }
                break;
            }

            case RelocationType.R_WASM_MEMORY_ADDR_LEB:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_LEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_I64:
            {
                if (sym.Type == SymbolType.Data)
                {
                    uint? segIdx = sym.SegmentIndex;
                    if (!segIdx.HasValue && sym.Index.HasValue)
                        segIdx = (uint)sym.Index.Value;

                    if (segIdx.HasValue && map.DataSegments.TryGetValue(segIdx.Value, out uint dataMerged))
                        AddDataEdge(callerMergedIdx, dataMerged);
                }
                break;
            }

            case RelocationType.R_WASM_TAG_INDEX_LEB:
            {
                if (!sym.Index.HasValue) break;
                uint localIdx = checked((uint)sym.Index.Value);
                if (map.Tags.TryGetValue(localIdx, out uint tagMerged))
                    AddTagEdge(callerMergedIdx, tagMerged);
                break;
            }
        }
    }

    private static void CollectRoots(
        SymbolTable symbolTable,
        LinkerOptions options,
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals,
        HashSet<uint> liveDataSegments,
        HashSet<uint> liveTags)
    {
        var extraExports = options.Exports is { Count: > 0 }
            ? new HashSet<string>(options.Exports, StringComparer.Ordinal)
            : null;

        foreach (var sym in symbolTable.Symbols.Values)
        {
            if (sym.IsUndefined || !sym.MergedIndex.HasValue)
                continue;

            bool isRoot = options.ExportAll
                || (sym.Flags & SymbolFlags.NoStrip) != 0
                || (sym.Flags & SymbolFlags.Exported) != 0
                || (extraExports?.Contains(sym.Name) == true);

            if (!isRoot) continue;

            switch (sym.Kind)
            {
                case SymbolType.Function: liveFunctions.Add(sym.MergedIndex.Value);    break;
                case SymbolType.Global:   liveGlobals.Add(sym.MergedIndex.Value);      break;
                case SymbolType.Data:     liveDataSegments.Add(sym.MergedIndex.Value); break;
                case SymbolType.Event:    liveTags.Add(sym.MergedIndex.Value);         break;
            }
        }

        // __wasm_call_ctors is always a root
        if (symbolTable.Symbols.TryGetValue("__wasm_call_ctors", out var ctors) &&
            ctors.MergedIndex.HasValue)
        {
            liveFunctions.Add(ctors.MergedIndex.Value);
        }

        // EntryPoint is a root
        if (options.EntryPoint is not null &&
            symbolTable.Symbols.TryGetValue(options.EntryPoint, out var ep) &&
            ep.MergedIndex.HasValue)
        {
            liveFunctions.Add(ep.MergedIndex.Value);
        }
    }

    private void BfsFunctions(
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals,
        HashSet<uint> liveDataSegments,
        HashSet<uint> liveTags)
    {
        var queue = new Queue<uint>(liveFunctions);
        while (queue.Count > 0)
        {
            uint funcIdx = queue.Dequeue();

            if (_funcToFuncs.TryGetValue(funcIdx, out var callees))
            {
                foreach (uint callee in callees)
                {
                    if (liveFunctions.Add(callee))
                        queue.Enqueue(callee);
                }
            }

            if (_funcToGlobals.TryGetValue(funcIdx, out var globals))
                foreach (uint g in globals) liveGlobals.Add(g);

            if (_funcToData.TryGetValue(funcIdx, out var data))
                foreach (uint d in data) liveDataSegments.Add(d);

            if (_funcToTags.TryGetValue(funcIdx, out var tags))
                foreach (uint t in tags) liveTags.Add(t);
        }
    }

    private static Dictionary<uint, uint> BuildCompactionMap(HashSet<uint>? liveSet)
    {
        if (liveSet is null) return new Dictionary<uint, uint>();
        var sorted = liveSet.Order().ToArray();
        var map = new Dictionary<uint, uint>(sorted.Length);
        for (uint i = 0; i < sorted.Length; i++)
            map[sorted[i]] = i;
        return map;
    }

    private static void ComposeMap(Dictionary<uint, uint> indexMap, Dictionary<uint, uint> compactionMap)
    {
        if (compactionMap.Count == 0) return;
        foreach (var key in indexMap.Keys.ToArray())
        {
            if (compactionMap.TryGetValue(indexMap[key], out uint compacted))
                indexMap[key] = compacted;
            // else: dead — leave stale (SectionMerger skips via DeadXxxByModule)
        }
    }

    private static uint FindFunctionContaining((uint offset, uint mergedIdx)[] funcOffsets, uint relocOffset)
    {
        if (funcOffsets.Length == 0) return uint.MaxValue;

        int lo = 0, hi = funcOffsets.Length - 1, best = -1;
        while (lo <= hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (funcOffsets[mid].offset <= relocOffset)
            {
                best = mid;
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return best >= 0 ? funcOffsets[best].mergedIdx : uint.MaxValue;
    }

    private void AddFuncEdge(uint from, uint to)
    {
        if (!_funcToFuncs.TryGetValue(from, out var set))
            _funcToFuncs[from] = set = new HashSet<uint>();
        set.Add(to);
    }

    private void AddGlobalEdge(uint from, uint globalTo)
    {
        if (!_funcToGlobals.TryGetValue(from, out var set))
            _funcToGlobals[from] = set = new HashSet<uint>();
        set.Add(globalTo);
    }

    private void AddDataEdge(uint from, uint dataTo)
    {
        if (!_funcToData.TryGetValue(from, out var set))
            _funcToData[from] = set = new HashSet<uint>();
        set.Add(dataTo);
    }

    private void AddTagEdge(uint from, uint tagTo)
    {
        if (!_funcToTags.TryGetValue(from, out var set))
            _funcToTags[from] = set = new HashSet<uint>();
        set.Add(tagTo);
    }

    private static void PrintRemovedItems(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        HashSet<uint> liveFunctions)
    {
        var funcNames = new Dictionary<uint, string>();
        for (int m = 0; m < inputs.Count; m++)
        {
            var linking = inputs[m].LinkingSection;
            if (linking is null) continue;
            foreach (var sym in linking.Symbols)
            {
                if (sym.Type == SymbolType.Function && sym.Index.HasValue && sym.Name is not null)
                {
                    uint localIdx = checked((uint)sym.Index.Value);
                    if (indexMaps[m].Functions.TryGetValue(localIdx, out uint mergedIdx))
                        funcNames.TryAdd(mergedIdx, sym.Name);
                }
            }
        }

        foreach (var (mergedIdx, name) in funcNames.OrderBy(static kv => kv.Key))
        {
            if (!liveFunctions.Contains(mergedIdx))
                Console.Error.WriteLine($"removing unused section '.text.{name}'");
        }
    }
}
