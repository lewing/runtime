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
    private readonly HashSet<uint> _allElements = new();
    private readonly Dictionary<uint, HashSet<uint>> _elemToFuncs = new();
    private readonly Dictionary<uint, HashSet<uint>> _elemToGlobals = new();
    private readonly Dictionary<uint, HashSet<uint>> _globalToFuncs = new();
    private readonly Dictionary<uint, HashSet<uint>> _globalToGlobals = new();
    private readonly Dictionary<uint, HashSet<uint>> _dataToGlobals = new();

    /// <summary>
    /// A permutation of the discovery passes, as indices into the array
    /// <see cref="ComputeLiveSets"/> declares. <see langword="null"/> keeps declaration order.
    /// </summary>
    /// <remarks>
    /// This is the instrument for the closure property, not a knob. Static roots are seeded before
    /// these passes; permuting the dependency-propagation rules proves their order is not load-bearing.
    /// </remarks>
    public IReadOnlyList<int>? DiscoveryPassOrder { get; init; }

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

        // Static semantic roots are seeded before closure. A fixed point propagates from roots; it
        // cannot invent a start function or an active segment that no pass ever added.
        SeedSemanticRoots(
            inputs,
            indexMaps,
            liveFunctions,
            liveGlobals,
            liveDataSegments,
            liveElements,
            liveTags);
        _ = CollectTableRoots(inputs, indexMaps, liveFunctions, liveGlobals);

        // ── Closure ──────────────────────────────────────────────────────────────────────────────
        //
        // Every dependency rule below can feed another, so they run to a fixed point. Root discovery
        // is intentionally not among them: the rejected implementation expected the loop to invent
        // an exported table's active segment and a start function, which no propagation rule could do.
        var passes = new Func<bool>[]
        {
            () => BfsFunctions(liveFunctions, liveGlobals, liveDataSegments, liveTags),
            () => CollectElementSegmentRoots(
                liveFunctions, liveGlobals, liveElements, mergedFuncImportCount),
            () => PropagateGlobalsToFunctions(liveGlobals, liveFunctions),
            () => PropagateGlobalsToGlobals(liveGlobals),
            () => PropagateDataToGlobals(liveDataSegments, liveGlobals),
        };

        RunToFixedPoint(Permute(passes));

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

    /// <summary>Applies <see cref="DiscoveryPassOrder"/>, refusing anything that is not a permutation.</summary>
    /// <remarks>
    /// A partial order would silently drop a discovery rule and the run would look like a pass. The
    /// check is affirmative — every index exactly once — so a malformed permutation is a loud failure
    /// rather than a quieter version of the bug this seam exists to rule out.
    /// </remarks>
    private Func<bool>[] Permute(Func<bool>[] passes)
    {
        if (DiscoveryPassOrder is not { } order)
            return passes;

        if (order.Count != passes.Length || order.Distinct().Count() != passes.Length ||
            order.Any(i => i < 0 || i >= passes.Length))
        {
            throw new LinkerException(
                $"DiscoveryPassOrder must be a permutation of the {passes.Length} discovery passes; got " +
                $"[{string.Join(", ", order)}].");
        }

        return [.. order.Select(i => passes[i])];
    }

    /// <summary>
    /// Runs every discovery rule until a whole sweep of them adds nothing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rules are monotone — each one only ever <em>adds</em> to a live set — and the sets are
    /// bounded by the merged index spaces, so the loop terminates. The iteration cap is therefore a
    /// contradiction detector rather than a safety valve: reaching it means some rule reported
    /// "changed" without changing anything, which would otherwise spin silently.
    /// </para>
    /// <para>
    /// Order is deliberately not load-bearing. <see cref="RunToFixedPoint"/> is handed the rules as
    /// an array precisely so a test can permute them.
    /// </para>
    /// </remarks>
    internal static int RunToFixedPoint(IReadOnlyList<Func<bool>> passes)
    {
        ArgumentNullException.ThrowIfNull(passes);

        // One round per item that could still be discovered, plus slack. Any real input converges in
        // a handful; a rule that lies about changing would not converge at all.
        const int MaxRounds = 10_000;

        for (int round = 1; round <= MaxRounds; round++)
        {
            bool changed = false;
            foreach (var pass in passes)
                changed |= pass();

            if (!changed)
                return round;
        }

        throw new LinkerException(
            $"Dead-code elimination did not reach a fixed point in {MaxRounds} rounds. The live sets only ever " +
            "grow and are bounded by the index spaces, so this means a discovery pass reported a change it did " +
            "not make.");
    }

    /// <summary>
    /// Propagates dependencies from live element segments. Once any non-import function is live,
    /// passive and declarative segments are conservatively retained too.
    /// </summary>
    /// <remarks>
    /// Active segments are already roots because they execute at instantiation. This gate controls
    /// only the conservative retention of passive and declarative segments that live code may reach
    /// indirectly, and is re-evaluated as the function closure grows.
    /// </remarks>
    private bool CollectElementSegmentRoots(
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals,
        HashSet<uint> liveElements,
        uint mergedFuncImportCount)
    {
        if (_allElements.Count == 0)
            return false;

        bool changed = false;
        if (liveFunctions.Any(f => f >= mergedFuncImportCount))
            foreach (uint element in _allElements)
                changed |= liveElements.Add(element);

        foreach (uint elemIdx in liveElements.ToArray())
        {
            if (_elemToFuncs.TryGetValue(elemIdx, out var functions))
                foreach (uint function in functions)
                    changed |= liveFunctions.Add(function);
            if (_elemToGlobals.TryGetValue(elemIdx, out var globals))
                foreach (uint global in globals)
                    changed |= liveGlobals.Add(global);
        }

        return changed;
    }

    private bool PropagateGlobalsToFunctions(HashSet<uint> liveGlobals, HashSet<uint> liveFunctions)
    {
        bool changed = false;
        foreach (uint g in liveGlobals)
            if (_globalToFuncs.TryGetValue(g, out var functions))
                foreach (uint function in functions)
                    changed |= liveFunctions.Add(function);
        return changed;
    }

    private bool PropagateGlobalsToGlobals(HashSet<uint> liveGlobals)
    {
        bool changed = false;
        foreach (uint g in liveGlobals.ToArray())
            if (_globalToGlobals.TryGetValue(g, out var globals))
                foreach (uint global in globals)
                    changed |= liveGlobals.Add(global);
        return changed;
    }

    private bool PropagateDataToGlobals(
        HashSet<uint> liveDataSegments, HashSet<uint> liveGlobals)
    {
        bool changed = false;
        foreach (uint data in liveDataSegments)
            if (_dataToGlobals.TryGetValue(data, out var globals))
                foreach (uint global in globals)
                    changed |= liveGlobals.Add(global);
        return changed;
    }

    private static void SeedSemanticRoots(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals,
        HashSet<uint> liveDataSegments,
        HashSet<uint> liveElements,
        HashSet<uint> liveTags)
    {
        for (int m = 0; m < inputs.Count; m++)
        {
            var module = inputs[m];
            var map = indexMaps[m];

            if (module.StartFunction is int start &&
                map.Functions.TryGetValue((uint)start, out uint mergedStart))
            {
                liveFunctions.Add(mergedStart);
            }

            foreach (var export in module.Exports.Values.Concat(module.ExportEntries))
            {
                switch (export.Descriptor)
                {
                    case ExportDescriptor.Func function
                        when map.Functions.TryGetValue((uint)function.FunctionIndex, out uint merged):
                        liveFunctions.Add(merged);
                        break;
                    case ExportDescriptor.Global global
                        when map.Globals.TryGetValue((uint)global.GlobalIndex, out uint merged):
                        liveGlobals.Add(merged);
                        break;
                    case ExportDescriptor.Tag tag
                        when map.Tags.TryGetValue((uint)tag.TagIndex, out uint merged):
                        liveTags.Add(merged);
                        break;
                }
            }

            for (int i = 0; i < module.Elements.Count; i++)
                if (module.Elements[i].Mode == ElementMode.Active &&
                    map.Elements.TryGetValue((uint)i, out uint merged))
                {
                    liveElements.Add(merged);
                }

            for (int i = 0; i < module.DataSegments.Count; i++)
                if (!module.DataSegments[i].IsPassive &&
                    map.DataSegments.TryGetValue((uint)i, out uint merged))
                {
                    liveDataSegments.Add(merged);
                }
        }
    }

    /// <summary>
    /// Roots everything a table needs, because no pass eliminates tables.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A table's initializer expression is a root in the strongest sense: the table survives the
    /// sweep unconditionally, so a <c>ref.func</c> inside it names a function that <em>will</em> be
    /// referenced by the output. Before this the graph never looked, the function was swept, and
    /// compaction moved a <em>different</em> live function into the index the expression still
    /// named — measured as an exported table whose slot 0 called the function returning 7 where the
    /// source called the one returning 42. The module validated; only the value was wrong
    /// (GitHub #383).
    /// </para>
    /// <para>
    /// Imported tables are walked too: a <c>tabletype</c> cannot carry an initializer, but its exact
    /// element type can name a defined type, and that type has to survive
    /// <see cref="CompactTypes"/>.
    /// </para>
    /// </remarks>
    private static bool CollectTableRoots(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals)
    {
        bool changed = false;
        for (int m = 0; m < inputs.Count; m++)
        {
            var map = indexMaps[m];
            foreach (var table in EnumerateTables(inputs[m]))
            {
                if (table.InitExpr.Length == 0)
                    continue;

                ConstExprScanner.Scan(table.InitExpr, (kind, value) =>
                {
                    switch (kind)
                    {
                        case ConstExprScanner.OperandKind.FunctionIndex
                            when map.Functions.TryGetValue(checked((uint)value), out uint merged):
                            changed |= liveFunctions.Add(merged);
                            break;
                        case ConstExprScanner.OperandKind.GlobalIndex
                            when map.Globals.TryGetValue(checked((uint)value), out uint merged):
                            changed |= liveGlobals.Add(merged);
                            break;
                    }
                });
            }
        }

        return changed;
    }

    /// <summary>Every table a module declares or imports, in local table index order.</summary>
    private static IEnumerable<TableType> EnumerateTables(WasmModule module)
    {
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Table table)
                yield return table.Type;

        foreach (var table in module.Tables)
            yield return table;
    }

    /// <summary>
    /// The exact content type of every global that will reach the output — imports, plus the
    /// definitions the sweep is keeping.
    /// </summary>
    /// <remarks>
    /// Global imports are unconditional: <c>MergeImports</c> copies a non-function, non-tag import
    /// through whatever the live set says. Definitions are filtered, because keeping the type a
    /// <em>swept</em> global named would defeat the sweep — and the merger never asks for it.
    /// </remarks>
    private static IEnumerable<Microsoft.NET.WebAssembly.Types.ValueType?> EnumerateGlobalTypes(
        WasmModule module, DceLiveSet liveSets, int moduleIndex)
    {
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Global global)
                yield return global.Type.ExactContentType;

        foreach (var global in EnumerateLiveGlobalDefinitions(module, liveSets, moduleIndex))
            yield return global.Type.ExactContentType;
    }

    /// <inheritdoc cref="EnumerateGlobalTypes"/>
    private static IEnumerable<WasmGlobal> EnumerateLiveGlobalDefinitions(
        WasmModule module, DceLiveSet liveSets, int moduleIndex)
    {
        uint localGlobalImports = 0;
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Global) localGlobalImports++;

        for (int j = 0; j < module.Globals.Count; j++)
        {
            if (liveSets.DeadGlobalsByModule?[moduleIndex].Contains(localGlobalImports + (uint)j) == true)
                continue;

            yield return module.Globals[j];
        }
    }

    public static void ComposeCompactionIntoIndexMaps(
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        DceLiveSet liveSets)
    {        // Before composing, record which local indices are dead per module
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
    /// <paramref name="mergedImports"/> and <paramref name="mergedTypeDefinitions"/> in-place to use
    /// the new compacted type indices.
    /// </summary>
    /// <remarks>
    /// <paramref name="mergedTypeDefinitions"/> is the exact type space that runs in lockstep with
    /// <paramref name="mergedTypes"/>; it is compacted in place alongside it when supplied.
    /// </remarks>
    public static List<FuncType> CompactTypes(
        List<FuncType> mergedTypes,
        DceLiveSet liveSets,
        IReadOnlyList<WasmModule> inputs,
        IndexMap[] indexMaps,
        List<Import> mergedImports,
        List<TypeDefinition>? mergedTypeDefinitions = null)
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

                foreach (var local in inputs[m].Functions[j].ExactLocals)
                {
                    if (local is Microsoft.NET.WebAssembly.Types.ValueType.Reference
                        {
                            HeapType: DefinedHeapType defined,
                        } &&
                        indexMaps[m].Types.TryGetValue(
                            checked((uint)defined.TypeIndex), out uint localType))
                    {
                        liveTypes.Add(localType);
                    }
                }
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

        // Keep types named by a table's element type, by a global's content type, or by a
        // `ref.null <typeidx>` / GC constructor inside a constant expression. No pass eliminates
        // tables, and a global that survived the sweep still carries its declared type — so these are
        // unconditional. Sweeping the sole type a typed table named left the table pointing at
        // whichever type compaction moved into that index — a different, structurally unrelated type,
        // in a module that still validated (GitHub #383).
        for (int m = 0; m < inputs.Count; m++)
        {
            var map = indexMaps[m];

            void KeepExact(Microsoft.NET.WebAssembly.Types.ValueType? exact)
            {
                if (exact is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined } &&
                    map.Types.TryGetValue((uint)defined.TypeIndex, out uint mergedTypeIdx))
                {
                    liveTypes.Add(mergedTypeIdx);
                }
            }

            void KeepConstExpr(byte[] expr)
            {
                if (expr.Length == 0)
                    return;

                ConstExprScanner.Scan(expr, (kind, value) =>
                {
                    bool namesType = kind switch
                    {
                        // `ref.null` carries an s33: non-negative is a defined type index.
                        ConstExprScanner.OperandKind.HeapType => value >= 0,

                        // A GC constructor's operand is always a defined type index.
                        ConstExprScanner.OperandKind.TypeIndex => true,
                        _ => false,
                    };

                    if (namesType && map.Types.TryGetValue(checked((uint)value), out uint mergedTypeIdx))
                        liveTypes.Add(mergedTypeIdx);
                });
            }

            foreach (var table in EnumerateTables(inputs[m]))
            {
                KeepExact(table.ExactElementType);
                KeepConstExpr(table.InitExpr);
            }

            foreach (var globalType in EnumerateGlobalTypes(inputs[m], liveSets, m))
                KeepExact(globalType);

            foreach (var global in EnumerateLiveGlobalDefinitions(inputs[m], liveSets, m))
                KeepConstExpr(global.InitExpr);

            for (int elementIndex = 0; elementIndex < inputs[m].Elements.Count; elementIndex++)
            {
                if (liveSets.DeadElementsByModule?[m].Contains((uint)elementIndex) == true)
                    continue;

                var element = inputs[m].Elements[elementIndex];
                KeepExact(element.ElementType);
                KeepConstExpr(element.OffsetExpr);
                foreach (byte[] expression in element.ElementExpressions)
                    KeepConstExpr(expression);
            }
        }

        // Close over the type space itself: a live definition that names another type keeps it live.
        // Without the closure a surviving type could reference a swept one, and the compaction below
        // would have no honest index to give it.
        if (mergedTypeDefinitions is not null)
            CloseOverTypeReferences(liveTypes, mergedTypeDefinitions);

        var typeCompaction = BuildCompactionMap(liveTypes);

        // Compose into IndexMaps.Types
        for (int m = 0; m < inputs.Count; m++)
            ComposeMap(indexMaps[m].Types, typeCompaction);

        // Rewrite the merged imports into the compacted type space.
        //
        // They were built in phase 3, before this compaction existed, so every type index they carry
        // still names the pre-compaction space. This used to update function and tag descriptors only,
        // which left a table import declared `(ref null (func (result i64)))` naming whichever type
        // compaction moved into that index — measured as a silent redirect onto a surviving `(func)`,
        // in a module `wasm-tools validate -f all` accepts (GitHub #383).
        //
        // Driven through the same walk the merge uses, so the two cannot fall out of step, and a type
        // with no mapping is refused rather than left at its old number: an unmapped index is still a
        // valid index into the compacted space and names an unrelated type. Imports the sweep is about
        // to drop are skipped — their types are the ones it removed, so demanding a mapping for them
        // would fail a correct link.
        // ToArray() first: ClassifyImports iterates mergedImports lazily and the loop writes back into
        // it, which throws "Collection was modified" on a List<T> enumerator.
        var classified = SectionMerger.ClassifyImports(mergedImports, liveSets).ToArray();
        for (int i = 0; i < classified.Length; i++)
        {
            var (import, survives) = classified[i];
            if (!survives)
                continue;

            mergedImports[i] = SectionMerger.RemapImportTypeIndices(import, typeIndex =>
                typeCompaction.TryGetValue(typeIndex, out uint compacted)
                    ? compacted
                    : throw new LinkerException(
                        $"Import '{import.ModuleName}.{import.Name}' names type index {typeIndex}, which " +
                        "dead-code elimination removed. Keeping the old number would point the import at " +
                        "an unrelated type in the compacted space, which still validates and means " +
                        "something else (GitHub #383)."));
        }

        // Build compacted types list
        var sortedLive = liveTypes.Order().ToArray();
        var result = new List<FuncType>(sortedLive.Length);
        foreach (var idx in sortedLive)
            if (idx < (uint)mergedTypes.Count)
                result.Add(mergedTypes[(int)idx]);

        if (mergedTypeDefinitions is not null)
        {
            var compactedDefinitions = new List<TypeDefinition>(sortedLive.Length);
            foreach (var idx in sortedLive)
            {
                if (idx >= (uint)mergedTypeDefinitions.Count)
                    continue;
                compactedDefinitions.Add(
                    RemapDefinitionIndices(mergedTypeDefinitions[(int)idx], typeCompaction));
            }

            mergedTypeDefinitions.Clear();
            mergedTypeDefinitions.AddRange(compactedDefinitions);
        }

        return result;
    }

    /// <summary>
    /// Adds to <paramref name="liveTypes"/> every type reachable from one already in it.
    /// </summary>
    /// <remarks>
    /// Supertypes, recursion-group anchors, operand and field types are all type indices a surviving
    /// definition names. Compaction renumbers the space, so a reference out of the live set has no
    /// correct destination — the choice is between keeping the target and emitting a wrong index,
    /// and this keeps the target.
    /// </remarks>
    private static void CloseOverTypeReferences(
        HashSet<uint> liveTypes, List<TypeDefinition> definitions)
    {
        var queue = new Queue<uint>(liveTypes);
        while (queue.Count > 0)
        {
            uint index = queue.Dequeue();
            if (index >= (uint)definitions.Count)
                continue;

            foreach (uint referenced in ReferencedTypeIndices(definitions[(int)index]))
                if (referenced < (uint)definitions.Count && liveTypes.Add(referenced))
                    queue.Enqueue(referenced);
        }
    }

    /// <inheritdoc cref="CloseOverTypeReferences"/>
    private static IEnumerable<uint> ReferencedTypeIndices(TypeDefinition definition)
    {
        foreach (int super in definition.SuperTypeIndices)
            if (super >= 0)
                yield return (uint)super;

        if (definition.RecGroup.StartTypeIndex >= 0)
            yield return (uint)definition.RecGroup.StartTypeIndex;

        switch (definition.CompositeType)
        {
            case CompositeType.Function function:
                foreach (var operand in function.Parameters.Concat(function.Results))
                    if (ReferencedTypeIndex(operand) is { } index)
                        yield return index;
                break;
            case CompositeType.Struct structure:
                foreach (var field in structure.Fields)
                    if (field.StorageType is StorageType.Value value && ReferencedTypeIndex(value.Type) is { } index)
                        yield return index;
                break;
            case CompositeType.Array array:
                if (array.Field.StorageType is StorageType.Value element &&
                    ReferencedTypeIndex(element.Type) is { } arrayIndex)
                {
                    yield return arrayIndex;
                }

                break;
            case CompositeType.Continuation continuation when continuation.FunctionTypeIndex >= 0:
                yield return (uint)continuation.FunctionTypeIndex;
                break;
        }
    }

    /// <inheritdoc cref="CloseOverTypeReferences"/>
    private static uint? ReferencedTypeIndex(Microsoft.NET.WebAssembly.Types.ValueType type) =>
        type is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined } && defined.TypeIndex >= 0
            ? (uint)defined.TypeIndex
            : null;

    /// <summary>
    /// Rewrites every type index a <see cref="TypeDefinition"/> names through
    /// <paramref name="compaction"/>.
    /// </summary>
    /// <remarks>
    /// An index with no entry names a type the compaction dropped. Leaving it alone was the original
    /// behaviour and it is a silent redirect: the old number is a valid index into the <em>new</em>
    /// space and names an unrelated type, so the definition comes out describing something else and
    /// the module still validates. <see cref="CloseOverTypeReferences"/> makes that unreachable for
    /// a well-formed space, so reaching it means the closure is incomplete — which is a linker bug
    /// worth a stack trace, not a fabricated index (GitHub #383).
    /// </remarks>
    private static TypeDefinition RemapDefinitionIndices(
        TypeDefinition definition, Dictionary<uint, uint> compaction)
    {
        int Map(int index)
        {
            if (index < 0)
                return index;
            if (compaction.TryGetValue((uint)index, out uint mapped))
                return checked((int)mapped);

            throw new LinkerException(
                $"Type definition {definition.Index} names type index {index}, which dead-code elimination " +
                "removed. Keeping the old number would point it at an unrelated type in the compacted " +
                "space, which still validates and means something else (GitHub #383).");
        }

        return new TypeDefinition(
            Map(definition.Index),
            definition.IsFinal,
            [.. definition.SuperTypeIndices.Select(Map)],
            RemapComposite(definition.CompositeType, Map),
            definition.RecGroup with { StartTypeIndex = Map(definition.RecGroup.StartTypeIndex) });
    }

    private static CompositeType RemapComposite(CompositeType composite, Func<int, int> map) => composite switch
    {
        CompositeType.Function f => new CompositeType.Function(
            [.. f.Parameters.Select(p => RemapValue(p, map))],
            [.. f.Results.Select(r => RemapValue(r, map))]),
        CompositeType.Struct s => new CompositeType.Struct([.. s.Fields.Select(field => RemapField(field, map))]),
        CompositeType.Array a => new CompositeType.Array(RemapField(a.Field, map)),
        CompositeType.Continuation c => new CompositeType.Continuation(map(c.FunctionTypeIndex)),
        _ => composite,
    };

    private static FieldType RemapField(FieldType field, Func<int, int> map) =>
        field.StorageType is StorageType.Value value
            ? field with { StorageType = new StorageType.Value(RemapValue(value.Type, map)) }
            : field;

    private static Microsoft.NET.WebAssembly.Types.ValueType RemapValue(Microsoft.NET.WebAssembly.Types.ValueType type, Func<int, int> map) =>
        type is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined } reference
            ? reference with { HeapType = new DefinedHeapType(map(defined.TypeIndex)) }
            : type;

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

            // Constant-expression carriers do not have relocation records. Record their
            // dependencies directly from the bytes the reader preserved.
            for (int j = 0; j < module.Elements.Count; j++)
            {
                if (!map.Elements.TryGetValue((uint)j, out uint elemMerged))
                    continue;
                _allElements.Add(elemMerged);
                var element = module.Elements[j];

                foreach (var funcIdx in element.FunctionIndices.Concat(element.DeclaredFunctionIndices))
                {
                    if (funcIdx >= 0 && map.Functions.TryGetValue((uint)funcIdx, out uint fMerged))
                        AddEdge(_elemToFuncs, elemMerged, fMerged);
                }

                ScanConstExprDependencies(
                    element.OffsetExpr,
                    map,
                    function => AddEdge(_elemToFuncs, elemMerged, function),
                    global => AddEdge(_elemToGlobals, elemMerged, global));
                foreach (byte[] expression in element.ElementExpressions)
                {
                    ScanConstExprDependencies(
                        expression,
                        map,
                        function => AddEdge(_elemToFuncs, elemMerged, function),
                        global => AddEdge(_elemToGlobals, elemMerged, global));
                }
            }

            for (int j = 0; j < module.Globals.Count; j++)
            {
                uint localIndex = globalImportCount + (uint)j;
                if (!map.Globals.TryGetValue(localIndex, out uint globalMerged))
                    continue;

                ScanConstExprDependencies(
                    module.Globals[j].InitExpr,
                    map,
                    function => AddEdge(_globalToFuncs, globalMerged, function),
                    global => AddEdge(_globalToGlobals, globalMerged, global));
            }

            for (int j = 0; j < module.DataSegments.Count; j++)
            {
                if (!map.DataSegments.TryGetValue((uint)j, out uint dataMerged))
                    continue;

                ScanConstExprDependencies(
                    module.DataSegments[j].OffsetExpr,
                    map,
                    function: static _ => { },
                    global => AddEdge(_dataToGlobals, dataMerged, global));
            }
        }
    }

    private static void ScanConstExprDependencies(
        byte[] expression,
        IndexMap map,
        Action<uint> function,
        Action<uint> global)
    {
        if (expression.Length == 0)
            return;

        ConstExprScanner.Scan(expression, (kind, value) =>
        {
            switch (kind)
            {
                case ConstExprScanner.OperandKind.FunctionIndex
                    when map.Functions.TryGetValue(checked((uint)value), out uint merged):
                    function(merged);
                    break;
                case ConstExprScanner.OperandKind.GlobalIndex
                    when map.Globals.TryGetValue(checked((uint)value), out uint merged):
                    global(merged);
                    break;
            }
        });
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
        var conditionalExports = options.ExportIfDefined is { Count: > 0 }
            ? new HashSet<string>(options.ExportIfDefined, StringComparer.Ordinal)
            : null;

        foreach (var sym in symbolTable.Symbols.Values)
        {
            if (sym.IsUndefined || !sym.MergedIndex.HasValue)
                continue;

            bool isRoot = options.ExportAll
                || (sym.Flags & SymbolFlags.NoStrip) != 0
                || (sym.Flags & SymbolFlags.Exported) != 0
                || (extraExports?.Contains(sym.Name) == true)
                || (conditionalExports?.Contains(sym.Name) == true);

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

    private bool BfsFunctions(
        HashSet<uint> liveFunctions,
        HashSet<uint> liveGlobals,
        HashSet<uint> liveDataSegments,
        HashSet<uint> liveTags)
    {
        bool changed = false;
        var queue = new Queue<uint>(liveFunctions);
        while (queue.Count > 0)
        {
            uint funcIdx = queue.Dequeue();

            if (_funcToFuncs.TryGetValue(funcIdx, out var callees))
            {
                foreach (uint callee in callees)
                {
                    if (liveFunctions.Add(callee))
                    {
                        changed = true;
                        queue.Enqueue(callee);
                    }
                }
            }

            if (_funcToGlobals.TryGetValue(funcIdx, out var globals))
                foreach (uint g in globals) changed |= liveGlobals.Add(g);

            if (_funcToData.TryGetValue(funcIdx, out var data))
                foreach (uint d in data) changed |= liveDataSegments.Add(d);

            if (_funcToTags.TryGetValue(funcIdx, out var tags))
                foreach (uint t in tags) changed |= liveTags.Add(t);
        }

        return changed;
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

    /// <remarks>
    /// <para>
    /// A key whose target did not survive is <em>removed</em>, not left pointing at its
    /// pre-compaction index. The old number is still a valid index into the compacted space and
    /// names an unrelated item, so leaving it turns every later lookup into a silent redirect that
    /// produces a valid module meaning something else. Removing it makes
    /// <c>SectionMerger.ResolveMappedIndex</c> throw instead, which converts an unclosed dependency
    /// from a wrong answer into a failure (GitHub #383).
    /// </para>
    /// Callers that legitimately walk dead items — the section merger's per-kind skips — consult
    /// <c>DceLiveSet.DeadXxxByModule</c> before the map, so they never reach a removed key. An empty
    /// compaction map therefore removes every key; it does not mean "skip composition".
    /// </remarks>
    internal static void ComposeMap(Dictionary<uint, uint> indexMap, Dictionary<uint, uint> compactionMap)
    {
        foreach (var key in indexMap.Keys.ToArray())
        {
            if (compactionMap.TryGetValue(indexMap[key], out uint compacted))
                indexMap[key] = compacted;
            else
                indexMap.Remove(key);
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
        AddEdge(_funcToFuncs, from, to);
    }

    private void AddGlobalEdge(uint from, uint globalTo)
    {
        AddEdge(_funcToGlobals, from, globalTo);
    }

    private static void AddEdge(
        Dictionary<uint, HashSet<uint>> graph, uint from, uint to)
    {
        if (!graph.TryGetValue(from, out var set))
            graph[from] = set = new HashSet<uint>();
        set.Add(to);
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
