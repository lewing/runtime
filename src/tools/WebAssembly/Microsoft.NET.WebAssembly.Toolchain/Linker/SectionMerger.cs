// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Merges Wasm sections from multiple input modules into a single output module and can
/// emit the merged result as a binary <c>.wasm</c>.
/// </summary>
public sealed class SectionMerger
{
    /// <summary>Tool name recorded in the <c>processed-by</c> field of the producers section.</summary>
    public const string ProducerName = "Microsoft.NET.WebAssembly.Toolchain";

    private const uint WasmPageSize = 65536;

    /// <summary>
    /// Merges and deduplicates all function types from the input modules using structural
    /// signature equality, populating each module's type-index map as a side effect.
    /// </summary>
    public List<FuncType> MergeTypes(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps)
    {
        ValidateInputs(inputs, indexMaps);

        var deduper = new FuncTypeDeduplicator();
        var mergedTypes = new List<FuncType>();

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            var module = inputs[moduleIndex];
            for (int typeIndex = 0; typeIndex < module.Types.Count; typeIndex++)
            {
                uint mergedIndex = (uint)deduper.GetOrAdd(module.Types[typeIndex], mergedTypes);
                indexMaps[moduleIndex].Types[(uint)typeIndex] = mergedIndex;
            }
        }

        return mergedTypes;
    }

    /// <summary>
    /// Merges imports across all input modules, deduping entries with the same module/name/kind
    /// and compatible descriptor. Per-kind import index maps are written into <paramref name="indexMaps"/>.
    /// </summary>
    public ImportMergeResult MergeImports(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps)
    {
        ValidateInputs(inputs, indexMaps);

        var mergedImports = new List<Import>();
        var mergedEntries = new Dictionary<ImportKey, MergedImportEntry>();
        var importShapes = new Dictionary<(string ModuleName, string Name, Type Kind), ImportDescriptor>();

        uint mergedFunctionImportCount = 0;
        uint mergedGlobalImportCount = 0;
        uint mergedTableImportCount = 0;
        uint mergedMemoryImportCount = 0;
        uint mergedTagImportCount = 0;

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint functionImportIndex = 0;
            uint globalImportIndex = 0;
            uint tableImportIndex = 0;
            uint memoryImportIndex = 0;
            uint tagImportIndex = 0;

            foreach (var import in inputs[moduleIndex].Imports)
            {
                Import remappedImport = RemapImport(import, indexMaps[moduleIndex]);
                var shapeKey = (remappedImport.ModuleName, remappedImport.Name, remappedImport.Descriptor.GetType());
                if (importShapes.TryGetValue(shapeKey, out var existingDescriptor))
                {
                    var merged = MergeImportDescriptors(existingDescriptor, remappedImport.Descriptor,
                        remappedImport.ModuleName, remappedImport.Name);
                    importShapes[shapeKey] = merged;
                    // Update the already-emitted import with the merged descriptor
                    var existingKey = new ImportKey(remappedImport.ModuleName, remappedImport.Name, existingDescriptor);
                    if (mergedEntries.TryGetValue(existingKey, out var existingEntry))
                    {
                        mergedImports[existingEntry.SectionIndex] = new Import(
                            remappedImport.ModuleName, remappedImport.Name, merged);
                        // Re-key if the descriptor changed
                        if (!Equals(existingDescriptor, merged))
                        {
                            mergedEntries.Remove(existingKey);
                            mergedEntries[new ImportKey(remappedImport.ModuleName, remappedImport.Name, merged)] = existingEntry;
                        }
                    }
                    remappedImport = new Import(remappedImport.ModuleName, remappedImport.Name, merged);
                }
                else
                {
                    importShapes[shapeKey] = remappedImport.Descriptor;
                }

                var key = new ImportKey(remappedImport.ModuleName, remappedImport.Name, remappedImport.Descriptor);
                if (!mergedEntries.TryGetValue(key, out var mergedEntry))
                {
                    uint kindIndex = remappedImport.Descriptor switch
                    {
                        ImportDescriptor.Func => mergedFunctionImportCount++,
                        ImportDescriptor.Global => mergedGlobalImportCount++,
                        ImportDescriptor.Table => mergedTableImportCount++,
                        ImportDescriptor.Memory => mergedMemoryImportCount++,
                        ImportDescriptor.Tag => mergedTagImportCount++,
                        _ => throw new LinkerException($"Unsupported import descriptor type '{remappedImport.Descriptor.GetType().Name}'."),
                    };

                    mergedEntry = new MergedImportEntry(mergedImports.Count, kindIndex);
                    mergedEntries[key] = mergedEntry;
                    mergedImports.Add(remappedImport);
                }

                switch (remappedImport.Descriptor)
                {
                    case ImportDescriptor.Func:
                        indexMaps[moduleIndex].Functions[functionImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Global:
                        indexMaps[moduleIndex].Globals[globalImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Table:
                        indexMaps[moduleIndex].Tables[tableImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Memory:
                        indexMaps[moduleIndex].Memories[memoryImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Tag:
                        indexMaps[moduleIndex].Tags[tagImportIndex++] = mergedEntry.KindIndex;
                        break;
                }
            }
        }

        return new ImportMergeResult
        {
            Imports = mergedImports,
            FunctionImportCount = mergedFunctionImportCount,
            GlobalImportCount = mergedGlobalImportCount,
            TableImportCount = mergedTableImportCount,
            MemoryImportCount = mergedMemoryImportCount,
            TagImportCount = mergedTagImportCount,
        };
    }

    /// <summary>
    /// Builds the merged output module from already-merged type/import sections and populated index maps.
    /// </summary>
    public WasmModule BuildMergedModule(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        IReadOnlyList<FuncType> mergedTypes,
        IReadOnlyList<Import> mergedImports,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols = null,
        LinkerOptions? options = null,
        DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);
        ArgumentNullException.ThrowIfNull(mergedTypes);
        ArgumentNullException.ThrowIfNull(mergedImports);

        options ??= new LinkerOptions();

        var imports = FilterImports(mergedImports, liveSets);
        var functions = MergeFunctions(inputs, indexMaps, liveSets);
        var globals = MergeGlobals(inputs, indexMaps, liveSets);
        var tables = MergeTables(inputs);
        var memories = MergeMemories(inputs);

        // wasm64 (memory64/table64) linking is not yet supported (#88): reject up front — before any
        // capacity expansion or linker-option rewriting (ApplyMemory/TableOptions) that casts limits
        // to uint and reconstructs Limits without Is64, which would erase the flag before the
        // writer-level guards ever run. Covers imported AND defined memories/tables.
        RejectWasm64Linking(imports, memories, tables);

        var tags = MergeTags(inputs, indexMaps, liveSets);
        var dataSegments = MergeDataSegments(inputs, indexMaps, liveSets, options);
        if (options.CoalesceSegments)
            dataSegments = CoalesceDataSegments(dataSegments, options.CoalesceGapThreshold);
        var elements = MergeElements(inputs, indexMaps, liveSets);

        // ── TableBase: offset element segments for table 0 ─────────────────────────
        if (options.TableBase.HasValue)
            ApplyTableBase(elements, options.TableBase.Value);

        EnsureMemoryCapacity(imports, memories, dataSegments);
        EnsureTableCapacity(imports, tables, elements);

        ApplyMemoryOptions(imports, memories, options);
        ApplyTableOptions(imports, tables, options);

        var exports = MergeExports(inputs, indexMaps, resolvedSymbols, options);

        if (options.ExportMemory)
        {
            // Memory index 0 — whether imported or local
            if (!exports.Values.Any(e => e.Descriptor is ExportDescriptor.Memory m && m.MemoryIndex == 0
                && e.Name == options.ExportMemoryName))
            {
                exports[options.ExportMemoryName] = new Export(options.ExportMemoryName, new ExportDescriptor.Memory(0));
            }
        }

        if (options.ExportTable)
        {
            const string tableName = "__indirect_function_table";
            if (!exports.Values.Any(e => e.Descriptor is ExportDescriptor.Table t && t.TableIndex == 0
                && e.Name == tableName))
            {
                exports[tableName] = new Export(tableName, new ExportDescriptor.Table(0));
            }
        }

        int? startFunction = ResolveStartFunction(inputs, indexMaps, exports, options);

        PatchLayoutGlobals(globals, dataSegments, imports, resolvedSymbols, options);

        // ── Custom sections: debug info + name ─────────────────────────────────────
        var customSections = new List<CustomSection>();

        if (!options.StripDebug)
            customSections.AddRange(MergeDebugSections(inputs, indexMaps));

        if (!options.NoNameSection)
        {
            var nameSection = BuildNameSection(resolvedSymbols);
            if (nameSection is not null)
                customSections.Add(nameSection);
        }

        // ── Producers section ──────────────────────────────────────────────────────
        var producers = MergeProducers(inputs);

        return new WasmModule
        {
            Types = [.. mergedTypes],
            Imports = imports,
            Functions = functions,
            Globals = globals,
            Tables = tables,
            Memories = memories,
            Tags = tags,
            DataSegments = dataSegments,
            Elements = elements,
            Exports = exports,
            StartFunction = startFunction,
            CustomSections = customSections,
            ProducersSection = producers,
        };
    }

    /// <summary>Concatenates all function section entries, remapping type indices and cloning bodies.</summary>
    public List<WasmFunction> MergeFunctions(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Functions.Count;
        var result = new List<WasmFunction>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localFuncImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Func) localFuncImports++;

            for (int j = 0; j < inputs[moduleIndex].Functions.Count; j++)
            {
                uint localIdx = localFuncImports + (uint)j;
                if (liveSets?.DeadFunctionsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var function = inputs[moduleIndex].Functions[j];
                result.Add(new WasmFunction
                {
                    TypeIndex = checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Types, (uint)function.TypeIndex, "type")),
                    Locals = [.. function.Locals],
                    Body = [.. function.Body],
                });
            }
        }

        return result;
    }

    /// <summary>Concatenates all globals, cloning init-expression bytecode.</summary>
    public List<WasmGlobal> MergeGlobals(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap>? indexMaps = null, DceLiveSet? liveSets = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (indexMaps is not null)
            ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Globals.Count;
        var result = new List<WasmGlobal>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localGlobalImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Global) localGlobalImports++;

            for (int j = 0; j < inputs[moduleIndex].Globals.Count; j++)
            {
                uint localIdx = localGlobalImports + (uint)j;
                if (liveSets?.DeadGlobalsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var global = inputs[moduleIndex].Globals[j];
                result.Add(new WasmGlobal
                {
                    Type = global.Type,
                    InitExpr = indexMaps is null
                        ? [.. global.InitExpr]
                        : RemapConstExprIndices(global.InitExpr, indexMaps[moduleIndex]),
                });
            }
        }

        return result;
    }

    /// <summary>Concatenates all table declarations.</summary>
    public List<TableType> MergeTables(IReadOnlyList<WasmModule> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Tables.Count;
        var result = new List<TableType>(totalCount);
        foreach (var module in inputs)
            result.AddRange(module.Tables);
        return result;
    }

    /// <summary>Concatenates all memory declarations.</summary>
    public List<MemoryType> MergeMemories(IReadOnlyList<WasmModule> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Memories.Count;
        var result = new List<MemoryType>(totalCount);
        foreach (var module in inputs)
            result.AddRange(module.Memories);
        return result;
    }

    /// <summary>Concatenates all tag entries, remapping their type indices.</summary>
    public List<Tag> MergeTags(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Tags.Count;
        var result = new List<Tag>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localTagImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Tag) localTagImports++;

            for (int j = 0; j < inputs[moduleIndex].Tags.Count; j++)
            {
                uint localIdx = localTagImports + (uint)j;
                if (liveSets?.DeadTagsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var tag = inputs[moduleIndex].Tags[j];
                result.Add(new Tag(
                    checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Types, (uint)tag.TypeIndex, "type"))));
            }
        }

        return result;
    }

    /// <summary>
    /// Concatenates data segments, laying active segments out sequentially in linear memory and
    /// recording each input segment's base offset in <see cref="IndexMap.DataSegmentOffsets"/>.
    /// </summary>
    public List<Data> MergeDataSegments(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null, LinkerOptions? options = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalDataCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalDataCount += inputs[i].DataSegments.Count;
        var result = new List<Data>(totalDataCount);
        var memoryCursors = new Dictionary<int, uint>();
        uint dataStart = ComputeDataStart(options);

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            var module = inputs[moduleIndex];
            for (int segmentIndex = 0; segmentIndex < module.DataSegments.Count; segmentIndex++)
            {
                if (liveSets?.DeadDataSegsByModule?[moduleIndex].Contains((uint)segmentIndex) == true)
                    continue;

                var segment = module.DataSegments[segmentIndex];
                if (segment.IsPassive)
                {
                    result.Add(new Data
                    {
                        IsPassive = true,
                        MemoryIndex = -1,
                        OffsetExpr = [.. segment.OffsetExpr],
                        InitData = [.. segment.InitData],
                    });
                    continue;
                }

                if (segment.MemoryIndex < 0)
                    throw new LinkerException($"Active data segment {segmentIndex} in module {moduleIndex} does not reference a valid memory index.");

                int mappedMemoryIndex = checked((int)ResolveMappedIndex(
                    indexMaps[moduleIndex].Memories, (uint)segment.MemoryIndex, "memory"));
                uint alignment = GetSegmentAlignment(module, segmentIndex);
                uint nextOffset = memoryCursors.TryGetValue(mappedMemoryIndex, out var existingOffset)
                    ? existingOffset
                    : (mappedMemoryIndex == 0 ? dataStart : 0u);
                uint baseOffset = AlignUp(nextOffset, alignment);

                indexMaps[moduleIndex].DataSegmentOffsets[(uint)segmentIndex] = baseOffset;
                memoryCursors[mappedMemoryIndex] = checked(baseOffset + (uint)segment.InitData.Length);

                result.Add(new Data
                {
                    IsPassive = false,
                    MemoryIndex = mappedMemoryIndex,
                    OffsetExpr = BuildI32ConstExpr(baseOffset),
                    InitData = [.. segment.InitData],
                    IsReadOnly = IsReadOnlySegment(module, segmentIndex),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Coalesces adjacent or overlapping active data segments targeting the same memory into
    /// fewer, larger segments. Passive segments are passed through unchanged.
    /// Gaps of at most <paramref name="gapThreshold"/> bytes between segments are zero-filled
    /// rather than emitting a new segment header.
    /// </summary>
    public static List<Data> CoalesceDataSegments(List<Data> segments, int gapThreshold = 32)
    {
        if (segments.Count <= 1)
            return segments;

        var result = new List<Data>(segments.Count);

        // Separate passive segments — they have no offset and cannot be coalesced.
        var passive = new List<Data>();
        var active = new List<Data>();
        foreach (var seg in segments)
        {
            if (seg.IsPassive)
                passive.Add(seg);
            else
                active.Add(seg);
        }

        // Group active segments by (memory index, isReadOnly).
        var byGroup = new Dictionary<(int MemoryIndex, bool IsReadOnly), List<Data>>();
        foreach (var seg in active)
        {
            var key = (seg.MemoryIndex, seg.IsReadOnly);
            if (!byGroup.TryGetValue(key, out var list))
            {
                list = [];
                byGroup[key] = list;
            }
            list.Add(seg);
        }

        // Collect coalesced output per group, then sort: ro before rw, then by memory index.
        var coalescedGroups = new List<(int MemoryIndex, bool IsReadOnly, List<Data> Segments, List<Data> Undecodable)>();
        foreach (var ((memIdx, isRo), segs) in byGroup)
        {
            // Sort by offset. Segments whose offsets cannot be decoded go last (unchanged).
            var decoded = new List<(uint Offset, Data Seg)>(segs.Count);
            var undecodable = new List<Data>();
            foreach (var seg in segs)
            {
                if (TryReadConstI32Expr(seg.OffsetExpr, out uint off))
                    decoded.Add((off, seg));
                else
                    undecodable.Add(seg);
            }

            decoded.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

            // Walk sorted list, merging adjacent/overlapping/close segments.
            var coalesced = new List<Data>();
            int i = 0;
            while (i < decoded.Count)
            {
                uint currentStart = decoded[i].Offset;
                uint currentEnd   = currentStart + (uint)decoded[i].Seg.InitData.Length;
                using var builder = new MemoryStream(decoded[i].Seg.InitData.Length);
                builder.Write(decoded[i].Seg.InitData);

                int j = i + 1;
                while (j < decoded.Count)
                {
                    uint nextStart = decoded[j].Offset;
                    uint nextEnd   = nextStart + (uint)decoded[j].Seg.InitData.Length;

                    long gap = (long)nextStart - (long)currentEnd;
                    if (gap < 0)
                    {
                        // Overlapping: next segment starts inside the current buffer.
                        // Seek to the overlap point and overwrite; extend if it runs past currentEnd.
                        long bufOffset = (long)nextStart - (long)currentStart;
                        builder.Seek(bufOffset, SeekOrigin.Begin);
                        builder.Write(decoded[j].Seg.InitData);
                        builder.Seek(0, SeekOrigin.End);
                        if (nextEnd > currentEnd)
                            currentEnd = nextEnd;
                    }
                    else if (gap <= gapThreshold)
                    {
                        // Adjacent or close: zero-fill the gap and append.
                        for (long g = 0; g < gap; g++)
                            builder.WriteByte(0);
                        builder.Write(decoded[j].Seg.InitData);
                        currentEnd = nextEnd;
                    }
                    else
                    {
                        break;
                    }
                    j++;
                }

                coalesced.Add(new Data
                {
                    IsPassive   = false,
                    MemoryIndex = memIdx,
                    OffsetExpr  = BuildI32ConstExpr(currentStart),
                    InitData    = builder.ToArray(),
                    IsReadOnly  = isRo,
                });
                i = j;
            }

            coalescedGroups.Add((memIdx, isRo, coalesced, undecodable));
        }

        // Emit read-only segments first (matching wasm-ld output order), then read-write.
        coalescedGroups.Sort(static (a, b) =>
        {
            int roComp = b.IsReadOnly.CompareTo(a.IsReadOnly); // true sorts before false
            return roComp != 0 ? roComp : a.MemoryIndex.CompareTo(b.MemoryIndex);
        });

        foreach (var (_, _, segs, undecodable) in coalescedGroups)
        {
            result.AddRange(segs);
            result.AddRange(undecodable);
        }

        result.AddRange(passive);
        return result;
    }

    /// <summary>Concatenates element segments, remapping table and function indices.</summary>
    public List<Element> MergeElements(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Elements.Count;
        var result = new List<Element>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            for (int j = 0; j < inputs[moduleIndex].Elements.Count; j++)
            {
                if (liveSets?.DeadElementsByModule?[moduleIndex].Contains((uint)j) == true)
                    continue;

                var element = inputs[moduleIndex].Elements[j];
                var remappedIndices = new List<int>(element.FunctionIndices.Count);
                foreach (var functionIndex in element.FunctionIndices)
                    remappedIndices.Add(checked((int)ResolveMappedIndex(
                        indexMaps[moduleIndex].Functions, (uint)functionIndex, "function")));

                int remappedTableIndex = element.TableIndex >= 0
                    ? checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Tables, (uint)element.TableIndex, "table"))
                    : element.TableIndex;

                result.Add(new Element
                {
                    TableIndex = remappedTableIndex,
                    OffsetExpr = RemapConstExprIndices(element.OffsetExpr, indexMaps[moduleIndex]),
                    FunctionIndices = remappedIndices,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the final export section from resolved symbols that carry the export flag,
    /// then preserves any explicit Wasm exports from the input modules that are not already present.
    /// </summary>
    public OrderedDictionary<string, Export> MergeExports(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols,
        LinkerOptions options)
    {
        ValidateInputs(inputs, indexMaps);
        ArgumentNullException.ThrowIfNull(options);

        var result = new OrderedDictionary<string, Export>();
        var extraExports = options.Exports is { Count: > 0 }
            ? new HashSet<string>(options.Exports, StringComparer.Ordinal)
            : null;

        // Merge ExportIfDefined names that exist in the resolved symbol table
        if (options.ExportIfDefined is { Count: > 0 } && resolvedSymbols is not null)
        {
            foreach (var name in options.ExportIfDefined)
            {
                if (resolvedSymbols.TryGetValue(name, out var sym) &&
                    !sym.IsUndefined &&
                    sym.MergedIndex.HasValue &&
                    TryCreateExportFromSymbol(sym, out var export))
                {
                    result.TryAdd(name, export);
                }
            }
        }

        if (resolvedSymbols is not null)
        {
            foreach (var symbol in resolvedSymbols.Values)
            {
                if (!ShouldExportSymbol(symbol, extraExports, options.ExportAll) ||
                    !TryCreateExportFromSymbol(symbol, out var export))
                {
                    continue;
                }

                result.TryAdd(symbol.Name, export);
            }
        }

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            foreach (var (name, export) in inputs[moduleIndex].Exports)
            {
                if (result.ContainsKey(name))
                    continue;

                result[name] = new Export(name, RemapExportDescriptor(export.Descriptor, indexMaps[moduleIndex]));
            }
        }

        return result;
    }

    /// <summary>Encodes a merged <see cref="WasmModule"/> into a valid WebAssembly binary.</summary>
    public byte[] EmitBinary(WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);

        using var stream = new MemoryStream();
        WriteBinary(stream, module);
        return stream.ToArray();
    }

    /// <summary>Writes a merged <see cref="WasmModule"/> to the supplied stream as a valid WebAssembly binary.</summary>
    public void WriteBinary(Stream output, WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(module);

        output.Write([0x00, 0x61, 0x73, 0x6D]);
        output.Write([0x01, 0x00, 0x00, 0x00]);

        if (module.Types.Count > 0)
            WriteSection(output, 1, s => WriteTypeSection(s, module.Types));
        if (module.Imports.Count > 0)
            WriteSection(output, 2, s => WriteImportSection(s, module.Imports));
        if (module.Functions.Count > 0)
            WriteSection(output, 3, s => WriteFunctionSection(s, module.Functions));
        if (module.Tables.Count > 0)
            WriteSection(output, 4, s => WriteTableSection(s, module.Tables));
        if (module.Memories.Count > 0)
            WriteSection(output, 5, s => WriteMemorySection(s, module.Memories));
        if (module.Tags.Count > 0)
            WriteSection(output, 13, s => WriteTagSection(s, module.Tags));
        if (module.Globals.Count > 0)
            WriteSection(output, 6, s => WriteGlobalSection(s, module.Globals));
        if (module.Exports.Count > 0)
            WriteSection(output, 7, s => WriteExportSection(s, module.Exports));
        if (module.StartFunction.HasValue)
            WriteSection(output, 8, s => WasmEncoder.WriteU32Leb(s, (uint)module.StartFunction.Value));
        if (module.Elements.Count > 0)
            WriteSection(output, 9, s => WriteElementSection(s, module.Elements));
        if (module.DataSegments.Any(static segment => segment.IsPassive))
            WriteSection(output, 12, s => WasmEncoder.WriteU32Leb(s, (uint)module.DataSegments.Count));
        if (module.Functions.Count > 0)
            WriteSection(output, 10, s => WriteCodeSection(s, module.Functions));
        if (module.DataSegments.Count > 0)
            WriteSection(output, 11, s => WriteDataSection(s, module.DataSegments));

        foreach (var customSection in module.CustomSections)
            WriteSection(output, 0, s => WriteCustomSection(s, customSection));

        if (module.ProducersSection is { Fields.Count: > 0 } producers)
            WriteSection(output, 0, s => WriteProducersSection(s, producers));
    }

    /// <summary>
    /// Collects all <c>.debug_*</c> custom sections from the input modules, applies DWARF relocations
    /// where possible using the already-populated index maps, and returns the concatenated sections.
    /// </summary>
    private static List<CustomSection> MergeDebugSections(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps)
    {
        // Collect unique debug section names, preserving first-seen order.
        var debugSectionNames = new List<string>();
        var debugSectionNameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in inputs)
        {
            foreach (var cs in module.CustomSections)
            {
                if (cs.Name.StartsWith(".debug_", StringComparison.Ordinal) &&
                    debugSectionNameSet.Add(cs.Name))
                {
                    debugSectionNames.Add(cs.Name);
                }
            }
        }

        if (debugSectionNames.Count == 0)
            return [];

        // Compute per-module contribution start offsets within each combined debug section.
        // moduleDebugOffsets[moduleIndex][sectionName] = byte offset in the combined output.
        var moduleDebugOffsets = new Dictionary<int, Dictionary<string, uint>>();
        var combinedDebugLengths = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var sectionName in debugSectionNames)
        {
            uint runningOffset = 0;
            for (int m = 0; m < inputs.Count; m++)
            {
                var content = GetDebugSectionContent(inputs[m], sectionName);
                if (content is { Length: > 0 })
                {
                    if (!moduleDebugOffsets.TryGetValue(m, out var moduleMap))
                        moduleDebugOffsets[m] = moduleMap = new Dictionary<string, uint>(StringComparer.Ordinal);

                    moduleMap[sectionName] = runningOffset;
                    runningOffset = checked(runningOffset + (uint)content.Length);
                }
            }
            combinedDebugLengths[sectionName] = runningOffset;
        }

        // Populate SectionOffsets in each module's IndexMap for DWARF cross-section references.
        // reloc..debug_X has TargetSectionIndex = the local section index of .debug_X.
        // We map that index → module's contribution start in the combined .debug_X.
        for (int m = 0; m < inputs.Count; m++)
        {
            if (!moduleDebugOffsets.TryGetValue(m, out var moduleOffsetMap))
                continue;

            foreach (var relocSection in inputs[m].RelocationSections)
            {
                if (!relocSection.Name.StartsWith("reloc.", StringComparison.Ordinal))
                    continue;

                string targetName = relocSection.Name["reloc.".Length..];
                if (!targetName.StartsWith(".debug_", StringComparison.Ordinal))
                    continue;

                if (moduleOffsetMap.TryGetValue(targetName, out uint startOffset))
                    indexMaps[m].SectionOffsets[relocSection.TargetSectionIndex] = startOffset;
            }
        }

        // Pre-build per-module relocation dictionaries and symbol tables once; avoids
        // a FirstOrDefault scan and a RelocSymbolTable allocation on every (module × section) pair.
        var perModuleRelocMap = new Dictionary<string, RelocationSection>?[inputs.Count];
        var perModuleSymbols = new RelocSymbolTable?[inputs.Count];
        for (int m = 0; m < inputs.Count; m++)
        {
            if (inputs[m].RelocationSections.Count > 0)
                perModuleRelocMap[m] = inputs[m].RelocationSections
                    .ToDictionary(r => r.Name, StringComparer.Ordinal);
            if (inputs[m].LinkingSection is not null)
                perModuleSymbols[m] = new RelocSymbolTable(inputs[m].LinkingSection!);
        }

        // Build each combined debug section, applying DWARF relocations per module.
        var result = new List<CustomSection>(debugSectionNames.Count);

        foreach (var sectionName in debugSectionNames)
        {
            uint totalLength = combinedDebugLengths[sectionName];
            if (totalLength == 0)
                continue;

            var combined = new byte[totalLength];
            uint writePos = 0;

            for (int m = 0; m < inputs.Count; m++)
            {
                var content = GetDebugSectionContent(inputs[m], sectionName);
                if (content is null || content.Length == 0)
                    continue;

                content.CopyTo(combined, (int)writePos);

                // Apply DWARF relocations that target this section.
                if (perModuleRelocMap[m]?.TryGetValue("reloc." + sectionName, out var relocSection) == true
                    && perModuleSymbols[m] is { } symbols)
                {
                    ApplyDebugRelocations(
                        combined, (int)writePos, content.Length,
                        relocSection!.Entries, indexMaps[m], symbols,
                        indexMaps[m].DataSegmentOffsets);
                }

                writePos += (uint)content.Length;
            }

            result.Add(new CustomSection(sectionName, combined));
        }

        return result;
    }

    /// <summary>
    /// Applies DWARF relocations to <paramref name="combinedBytes"/> at
    /// [<paramref name="sectionStart"/>, <paramref name="sectionStart"/> + <paramref name="sectionLength"/>).
    /// Relocations that require unavailable maps (e.g., function-body offsets or dead-code-eliminated
    /// symbols) are skipped without throwing — the DWARF bytes are left at their object-file values.
    /// </summary>
    private static void ApplyDebugRelocations(
        byte[] combinedBytes,
        int sectionStart,
        int sectionLength,
        IReadOnlyList<Relocation> relocations,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses)
    {
        // Patch in-place via span — no intermediate buffer allocation or Array.Copy needed.
        // The span is a view of the already-written module slice; reloc offsets are 0-based from sectionStart.
        var span = combinedBytes.AsSpan(sectionStart, sectionLength);

        foreach (var reloc in relocations)
        {
            // R_WASM_FUNCTION_OFFSET_I32 requires code-section layout analysis; skip.
            if (reloc.Type is RelocationType.R_WASM_FUNCTION_OFFSET_I32)
                continue;

            // TryApplyRelocation returns false (no throw) when a symbol or map entry is missing,
            // which is the common case after DCE removes dead functions/globals.
            RelocationEngine.TryApplyRelocation(span, reloc, indexMap, symbols, segmentBaseAddresses);
        }
    }

    private static byte[]? GetDebugSectionContent(WasmModule module, string sectionName)
    {
        foreach (var cs in module.CustomSections)
        {
            if (string.Equals(cs.Name, sectionName, StringComparison.Ordinal))
                return cs.Content;
        }
        return null;
    }

    /// <summary>
    /// Builds a WebAssembly <c>name</c> custom section containing function name subsection (id=1)
    /// from the resolved symbol table.  Returns <c>null</c> when no named function symbols are available.
    /// </summary>
    private static CustomSection? BuildNameSection(IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols)
    {
        if (resolvedSymbols is null)
            return null;

        var namedFunctions = new List<(uint Index, string Name)>();
        foreach (var sym in resolvedSymbols.Values)
        {
            if (sym.Kind == Binary.Linking.SymbolType.Function &&
                sym.MergedIndex.HasValue &&
                !string.IsNullOrEmpty(sym.Name))
            {
                namedFunctions.Add((sym.MergedIndex.Value, sym.Name));
            }
        }

        if (namedFunctions.Count == 0)
            return null;

        namedFunctions.Sort(static (a, b) => a.Index.CompareTo(b.Index));

        using var subsection = new MemoryStream();
        WasmEncoder.WriteU32Leb(subsection, (uint)namedFunctions.Count);
        foreach (var (index, name) in namedFunctions)
        {
            WasmEncoder.WriteU32Leb(subsection, index);
            WriteName(subsection, name);
        }

        using var content = new MemoryStream();
        content.WriteByte(1); // subsection id: function names
        WasmEncoder.WriteU32Leb(content, (uint)subsection.Length);
        content.Write(subsection.GetBuffer(), 0, (int)subsection.Length);

        return new CustomSection("name", content.ToArray());
    }

    private static void WriteCustomSection(Stream stream, CustomSection section)
    {
        WriteName(stream, section.Name);
        stream.Write(section.Content);
    }

    /// <summary>
    /// Merges producers sections from all input modules and appends a <c>processed-by: <see cref="ProducerName"/></c> entry.
    /// </summary>
    internal static ProducersSection MergeProducers(IReadOnlyList<WasmModule> inputs)
    {
        // Collect all fields, dedup entries by (name, version)
        var merged = new Dictionary<string, List<(string Name, string Version)>>(StringComparer.Ordinal);

        foreach (var module in inputs)
        {
            if (module.ProducersSection is not { Fields.Count: > 0 } producers)
                continue;

            foreach (var (fieldName, values) in producers.Fields)
            {
                if (!merged.TryGetValue(fieldName, out var list))
                {
                    list = [];
                    merged[fieldName] = list;
                }

                foreach (var entry in values)
                {
                    if (!list.Contains(entry))
                        list.Add(entry);
                }
            }
        }

        // Always add our processed-by entry last
        if (!merged.TryGetValue("processed-by", out var processedBy))
        {
            processedBy = [];
            merged["processed-by"] = processedBy;
        }

        string version = typeof(SectionMerger).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var toolEntry = (ProducerName, version);
        // Remove any existing entry for this tool so ours is always last
        processedBy.RemoveAll(e => e.Name == ProducerName);
        processedBy.Add(toolEntry);

        return new ProducersSection { Fields = merged };
    }

    /// <summary>Writes a producers custom section to the stream.</summary>
    private static void WriteProducersSection(Stream stream, ProducersSection producers)
    {
        WriteName(stream, "producers");

        WasmEncoder.WriteU32Leb(stream, (uint)producers.Fields.Count);
        foreach (var (fieldName, values) in producers.Fields)
        {
            WriteName(stream, fieldName);
            WasmEncoder.WriteU32Leb(stream, (uint)values.Count);
            foreach (var (name, version) in values)
            {
                WriteName(stream, name);
                WriteName(stream, version);
            }
        }
    }

    private static void ValidateInputs(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(indexMaps);

        if (inputs.Count != indexMaps.Count)
            throw new ArgumentException("The number of input modules must match the number of index maps.");
    }

    /// <summary>
    /// Returns a filtered list of imports. Function imports whose merged index is not in
    /// <paramref name="liveSets"/>.LiveFunctions and tag imports whose merged index is not in
    /// <paramref name="liveSets"/>.LiveTags are omitted; all other imports are kept.
    /// When <paramref name="liveSets"/> is null the full list is returned unchanged.
    /// </summary>
    private static List<Import> FilterImports(IReadOnlyList<Import> mergedImports, DceLiveSet? liveSets)
    {
        if (liveSets?.LiveFunctions is null)
            return [.. mergedImports];

        var result = new List<Import>(mergedImports.Count);
        uint funcImportIdx = 0;
        uint tagImportIdx  = 0;
        foreach (var import in mergedImports)
        {
            if (import.Descriptor is ImportDescriptor.Func)
            {
                if (liveSets.LiveFunctions.Contains(funcImportIdx))
                    result.Add(import);
                funcImportIdx++;
            }
            else if (import.Descriptor is ImportDescriptor.Tag)
            {
                if (liveSets.LiveTags is null || liveSets.LiveTags.Contains(tagImportIdx))
                    result.Add(import);
                tagImportIdx++;
            }
            else
            {
                result.Add(import);
            }
        }
        return result;
    }

    private static Import RemapImport(Import import, IndexMap indexMap) =>
        import.Descriptor switch
        {
            ImportDescriptor.Func func => import with
            {
                Descriptor = new ImportDescriptor.Func(
                    checked((int)ResolveMappedIndex(indexMap.Types, (uint)func.TypeIndex, "type"))),
            },
            ImportDescriptor.Tag tag => import with
            {
                Descriptor = new ImportDescriptor.Tag(
                    checked((int)ResolveMappedIndex(indexMap.Types, (uint)tag.TypeIndex, "type"))),
            },
            _ => import,
        };

    /// <summary>
    /// Merges two import descriptors for the same (module, name) pair.
    /// Memory: takes max of minimums, most permissive max.
    /// Table: takes max of minimums, most permissive max.
    /// Others: must be identical.
    /// </summary>
    private static ImportDescriptor MergeImportDescriptors(
        ImportDescriptor existing, ImportDescriptor incoming,
        string moduleName, string name)
    {
        if (Equals(existing, incoming))
            return existing;

        return (existing, incoming) switch
        {
            (ImportDescriptor.Memory memA, ImportDescriptor.Memory memB) =>
                new ImportDescriptor.Memory(new MemoryType(MergeLimits(memA.Type.Limits, memB.Type.Limits))),
            (ImportDescriptor.Table tabA, ImportDescriptor.Table tabB)
                when tabA.Type.ElementType == tabB.Type.ElementType =>
                new ImportDescriptor.Table(new TableType(
                    tabA.Type.ElementType, MergeLimits(tabA.Type.Limits, tabB.Type.Limits))),
            _ => throw new LinkerException(
                $"Import '{moduleName}.{name}' was seen with incompatible {GetImportKindName(existing)} signatures."),
        };
    }

    /// <summary>
    /// Rejects any wasm64 (memory64/table64) memory or table — imported or defined — before merge
    /// output. Linking of wasm64 is not yet supported (#88); rejecting here guarantees no later site
    /// (capacity expansion, ApplyMemory/TableOptions, or the section writers) truncates a u64 limit
    /// to u32 or reconstructs a Limits without Is64.
    /// </summary>
    private static void RejectWasm64Linking(
        IReadOnlyList<Import> imports,
        IReadOnlyList<MemoryType> memories,
        IReadOnlyList<TableType> tables)
    {
        foreach (var import in imports)
        {
            switch (import.Descriptor)
            {
                case ImportDescriptor.Memory { Type.Is64: true }:
                    throw new LinkerException(Wasm64Guard.NotSupportedMessage("memory64", "linking"));
                case ImportDescriptor.Table { Type.Is64: true }:
                    throw new LinkerException(Wasm64Guard.NotSupportedMessage("table64", "linking"));
            }
        }

        foreach (var memory in memories)
            if (memory.Is64)
                throw new LinkerException(Wasm64Guard.NotSupportedMessage("memory64", "linking"));

        foreach (var table in tables)
            if (table.Is64)
                throw new LinkerException(Wasm64Guard.NotSupportedMessage("table64", "linking"));
    }

    private static Limits MergeLimits(Limits a, Limits b)
    {
        if (a.Is64 || b.Is64)
            throw new LinkerException(
                Wasm64Guard.NotSupportedMessage("memory64/table64", "linking"));
        uint min = Math.Max((uint)a.Min, (uint)b.Min);
        uint? max = ((uint?)a.Max, (uint?)b.Max) switch
        {
            (null, _) => null,
            (_, null) => null,
            (uint ma, uint mb) => Math.Max(ma, mb),
        };
        if (max.HasValue)
            max = Math.Max(max.Value, min);
        return new Limits(min, max);
    }

    private static string GetImportKindName(ImportDescriptor descriptor) =>
        descriptor switch
        {
            ImportDescriptor.Func => "function",
            ImportDescriptor.Global => "global",
            ImportDescriptor.Table => "table",
            ImportDescriptor.Memory => "memory",
            ImportDescriptor.Tag => "tag",
            _ => "import",
        };

    private static bool ShouldExportSymbol(ResolvedSymbol symbol, HashSet<string>? extraExports, bool exportAll)
    {
        if (symbol.IsUndefined ||
            string.IsNullOrEmpty(symbol.Name) ||
            !symbol.MergedIndex.HasValue)
        {
            return false;
        }

        if (exportAll)
            return true;

        if ((symbol.Flags & (SymbolFlags.BindingLocal | SymbolFlags.VisibilityHidden)) != 0)
            return false;

        if ((symbol.Flags & SymbolFlags.Exported) != 0)
            return true;

        return extraExports?.Contains(symbol.Name) == true;
    }

    private static bool TryCreateExportFromSymbol(ResolvedSymbol symbol, out Export export)
    {
        export = default!;

        if (!symbol.MergedIndex.HasValue)
            return false;

        ExportDescriptor? descriptor = symbol.Kind switch
        {
            SymbolType.Function => new ExportDescriptor.Func(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Global => new ExportDescriptor.Global(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Table => new ExportDescriptor.Table(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Event or SymbolType.Tag => new ExportDescriptor.Tag(checked((int)symbol.MergedIndex.Value)),
            _ => null,
        };

        if (descriptor is null)
            return false;

        export = new Export(symbol.Name, descriptor);
        return true;
    }

    private static int? ResolveStartFunction(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        OrderedDictionary<string, Export> mergedExports,
        LinkerOptions options)
    {
        if (options.EntryPoint is not null &&
            mergedExports.TryGetValue(options.EntryPoint, out var entryPointExport) &&
            entryPointExport.Descriptor is ExportDescriptor.Func entryPointFunction)
        {
            return entryPointFunction.FunctionIndex;
        }

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            if (inputs[moduleIndex].StartFunction is int startFunction)
            {
                return checked((int)ResolveMappedIndex(
                    indexMaps[moduleIndex].Functions, (uint)startFunction, "function"));
            }
        }

        return null;
    }

    private static ExportDescriptor RemapExportDescriptor(ExportDescriptor descriptor, IndexMap indexMap) =>
        descriptor switch
        {
            ExportDescriptor.Func function => new ExportDescriptor.Func(
                checked((int)ResolveMappedIndex(indexMap.Functions, (uint)function.FunctionIndex, "function"))),
            ExportDescriptor.Global global => new ExportDescriptor.Global(
                checked((int)ResolveMappedIndex(indexMap.Globals, (uint)global.GlobalIndex, "global"))),
            ExportDescriptor.Table table => new ExportDescriptor.Table(
                checked((int)ResolveMappedIndex(indexMap.Tables, (uint)table.TableIndex, "table"))),
            ExportDescriptor.Memory memory => new ExportDescriptor.Memory(
                checked((int)ResolveMappedIndex(indexMap.Memories, (uint)memory.MemoryIndex, "memory"))),
            ExportDescriptor.Tag tag => new ExportDescriptor.Tag(
                checked((int)ResolveMappedIndex(indexMap.Tags, (uint)tag.TagIndex, "tag"))),
            _ => descriptor,
        };

    private static uint ResolveMappedIndex(Dictionary<uint, uint> map, uint oldIndex, string kind)
    {
        if (map.TryGetValue(oldIndex, out uint mappedIndex))
            return mappedIndex;

        throw new KeyNotFoundException($"No mapped {kind} index was found for original index {oldIndex}.");
    }

    private static uint GetSegmentAlignment(WasmModule module, int segmentIndex)
    {
        if (module.LinkingSection?.SegmentInfos is not { Count: > 0 } segmentInfos ||
            segmentIndex >= segmentInfos.Count)
        {
            return 1;
        }

        uint log2Alignment = segmentInfos[segmentIndex].Alignment;
        if (log2Alignment >= 31)
            return 1u << 31;

        return 1u << (int)log2Alignment;
    }

    private static bool IsReadOnlySegment(WasmModule module, int segmentIndex)
    {
        if (module.LinkingSection?.SegmentInfos is not { Count: > 0 } segmentInfos ||
            segmentIndex >= segmentInfos.Count)
        {
            return false;
        }

        return segmentInfos[segmentIndex].Name.StartsWith(".rodata", StringComparison.Ordinal);
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1)
            return value;

        uint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static byte[] BuildI32ConstExpr(uint value)
    {
        // i32.const(1 byte) + signed LEB128 value(≤5 bytes) + end(1 byte) = max 7 bytes
        Span<byte> buf = stackalloc byte[7];
        buf[0] = 0x41; // i32.const
        int lebLen = WriteS32LebToSpan(buf[1..], unchecked((int)value));
        buf[1 + lebLen] = 0x0B; // end
        return buf[..(2 + lebLen)].ToArray();
    }

    private static int WriteS32LebToSpan(Span<byte> dest, int value)
    {
        int pos = 0;
        bool more;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            more = !((value == 0 && (b & 0x40) == 0) || (value == -1 && (b & 0x40) != 0));
            dest[pos++] = more ? (byte)(b | 0x80) : b;
        } while (more);
        return pos;
    }

    private static uint ComputeDataStart(LinkerOptions? options)
    {
        if (options is null) return 0;
        uint globalBase = options.GlobalBase ?? 0;
        if (options.StackFirst)
            return globalBase + options.StackSize;
        return globalBase;
    }

    private static void PatchLayoutGlobals(
        List<WasmGlobal> globals,
        List<Data> dataSegments,
        List<Import> mergedImports,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols,
        LinkerOptions options)
    {
        bool layoutActive = options.GlobalBase.HasValue || options.StackFirst;
        if (!layoutActive || resolvedSymbols is null)
            return;

        uint globalBase = options.GlobalBase ?? 0;
        uint stackSize = options.StackSize;

        // Compute dataEnd: max(offset + data length) across all memory-0 active segments
        uint dataEnd = ComputeDataStart(options);
        foreach (var seg in dataSegments)
        {
            if (seg.IsPassive || seg.MemoryIndex != 0)
                continue;
            if (!TryReadConstI32Expr(seg.OffsetExpr, out uint offset))
                continue;
            uint end = checked(offset + (uint)seg.InitData.Length);
            if (end > dataEnd)
                dataEnd = end;
        }

        // Compute stackPointerInit and heapBase
        uint stackPointerInit;
        uint heapBase;
        if (options.StackFirst)
        {
            stackPointerInit = globalBase + stackSize; // stack grows down from top of stack region
            heapBase = dataEnd;                        // heap starts right after data
        }
        else
        {
            uint alignedDataEnd = AlignUp(dataEnd, 16);
            stackPointerInit = alignedDataEnd + stackSize;
            heapBase = stackPointerInit;               // heap starts after stack
        }

        // Count global imports to translate merged index → globals list index
        uint globalImportCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Global) globalImportCount++;

        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__stack_pointer", stackPointerInit);
        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__data_end", dataEnd);
        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__heap_base", heapBase);
    }

    private static void PatchGlobal(
        List<WasmGlobal> globals,
        IReadOnlyDictionary<string, ResolvedSymbol> resolvedSymbols,
        uint globalImportCount,
        string symbolName,
        uint value)
    {
        if (!resolvedSymbols.TryGetValue(symbolName, out var sym) ||
            sym.Kind != SymbolType.Global ||
            !sym.MergedIndex.HasValue)
            return;

        uint mergedIdx = sym.MergedIndex.Value;
        if (mergedIdx < globalImportCount)
            return; // imported global — don't patch

        int localIdx = (int)(mergedIdx - globalImportCount);
        if ((uint)localIdx >= (uint)globals.Count)
            return;

        globals[localIdx] = new WasmGlobal
        {
            Type = globals[localIdx].Type,
            InitExpr = BuildI32ConstExpr(value),
        };
    }

    private static void EnsureMemoryCapacity(
        List<Import> mergedImports,
        List<MemoryType> mergedMemories,
        IReadOnlyList<Data> dataSegments)
    {
        var requiredPages = new Dictionary<int, uint>();
        foreach (var segment in dataSegments)
        {
            if (segment.IsPassive ||
                segment.MemoryIndex < 0 ||
                !TryReadConstI32Expr(segment.OffsetExpr, out uint offset))
            {
                continue;
            }

            uint end = checked(offset + (uint)segment.InitData.Length);
            uint pages = end == 0 ? 0u : ((end - 1) / WasmPageSize) + 1;

            if (requiredPages.TryGetValue(segment.MemoryIndex, out var existing))
                requiredPages[segment.MemoryIndex] = Math.Max(existing, pages);
            else
                requiredPages[segment.MemoryIndex] = pages;
        }

        int importedMemoryCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Memory) importedMemoryCount++;
        foreach (var (memoryIndex, pages) in requiredPages)
        {
            if (memoryIndex < importedMemoryCount)
            {
                UpdateImportedMemoryLimit(mergedImports, memoryIndex, pages);
            }
            else
            {
                int localIndex = memoryIndex - importedMemoryCount;
                if ((uint)localIndex >= (uint)mergedMemories.Count)
                    continue;

                mergedMemories[localIndex] = ExpandMemory(mergedMemories[localIndex], pages);
            }
        }
    }

    private static void UpdateImportedMemoryLimit(List<Import> mergedImports, int memoryIndex, uint requiredPages)
    {
        int seen = 0;
        for (int importIndex = 0; importIndex < mergedImports.Count; importIndex++)
        {
            if (mergedImports[importIndex].Descriptor is not ImportDescriptor.Memory memory)
                continue;

            if (seen == memoryIndex)
            {
                mergedImports[importIndex] = mergedImports[importIndex] with
                {
                    Descriptor = new ImportDescriptor.Memory(ExpandMemory(memory.Type, requiredPages)),
                };
                return;
            }

            seen++;
        }
    }

    private static MemoryType ExpandMemory(MemoryType memoryType, uint requiredPages)
    {
        uint min = Math.Max((uint)memoryType.Limits.Min, requiredPages);
        uint? max = memoryType.Limits.Max.HasValue
            ? Math.Max((uint)memoryType.Limits.Max.Value, min)
            : null;
        return new MemoryType(new Limits(min, max), memoryType.Shared);
    }

    private static void ApplyMemoryOptions(
        List<Import> mergedImports,
        List<MemoryType> mergedMemories,
        LinkerOptions options)
    {
        bool hasOverride = options.ImportMemory || options.InitialMemory.HasValue ||
                           options.MaxMemory.HasValue || options.SharedMemory;
        if (!hasOverride)
            return;

        // Compute override pages
        uint? initialPages = options.InitialMemory.HasValue
            ? (uint)(options.InitialMemory.Value / WasmPageSize)
            : null;
        uint? maxPages = options.MaxMemory.HasValue
            ? (uint)(options.MaxMemory.Value / WasmPageSize)
            : null;

        if (options.ImportMemory)
        {
            // Determine the current min from existing memory (local or imported)
            uint currentMin = 0;
            uint? currentMax = null;
            bool currentShared = false;

            // Check existing imported memories
            int existingImportIndex = -1;
            for (int i = 0; i < mergedImports.Count; i++)
            {
                if (mergedImports[i].Descriptor is ImportDescriptor.Memory mem &&
                    mergedImports[i].ModuleName == options.ImportMemoryModule &&
                    mergedImports[i].Name == options.ImportMemoryName)
                {
                    existingImportIndex = i;
                    currentMin = (uint)mem.Type.Limits.Min;
                    currentMax = (uint?)mem.Type.Limits.Max;
                    currentShared = mem.Type.Shared;
                    break;
                }
            }

            // Collect min from local memories before removing them
            if (mergedMemories.Count > 0)
            {
                currentMin = Math.Max(currentMin, (uint)mergedMemories[0].Limits.Min);
                if (mergedMemories[0].Limits.Max is { } mergedMax64)
                {
                    uint mergedMax = (uint)mergedMax64;
                    currentMax = currentMax.HasValue
                        ? Math.Max(currentMax.Value, mergedMax)
                        : mergedMax;
                }
                currentShared = currentShared || mergedMemories[0].Shared;
            }

            // Remove all local memories
            mergedMemories.Clear();

            // Apply overrides
            uint min = initialPages ?? currentMin;
            if (initialPages.HasValue && currentMin > initialPages.Value)
                throw new LinkerException(
                    $"--initial-memory too small: data segments require at least {currentMin * WasmPageSize} bytes ({currentMin} pages).");

            uint? max = maxPages ?? currentMax;
            bool shared = options.SharedMemory || currentShared;

            var memType = new MemoryType(new Limits(min, max), shared);

            if (existingImportIndex >= 0)
            {
                mergedImports[existingImportIndex] = mergedImports[existingImportIndex] with
                {
                    Descriptor = new ImportDescriptor.Memory(memType),
                };
            }
            else
            {
                // Insert memory import at the end of existing imports (before any non-import items)
                var import = new Import(options.ImportMemoryModule, options.ImportMemoryName,
                    new ImportDescriptor.Memory(memType));
                mergedImports.Add(import);
            }
        }
        else
        {
            // Apply overrides to local memory or imported memory
            int importedMemCount = 0;
            for (int i = 0; i < mergedImports.Count; i++)
                if (mergedImports[i].Descriptor is ImportDescriptor.Memory) importedMemCount++;

            if (importedMemCount > 0)
            {
                // Apply to first imported memory
                for (int i = 0; i < mergedImports.Count; i++)
                {
                    if (mergedImports[i].Descriptor is not ImportDescriptor.Memory mem)
                        continue;

                    uint min = initialPages ?? (uint)mem.Type.Limits.Min;
                    if (initialPages.HasValue && mem.Type.Limits.Min > initialPages.Value)
                        throw new LinkerException(
                            $"--initial-memory too small: data segments require at least {mem.Type.Limits.Min * WasmPageSize} bytes ({mem.Type.Limits.Min} pages).");

                    uint? max = maxPages ?? (uint?)mem.Type.Limits.Max;
                    bool shared = options.SharedMemory || mem.Type.Shared;

                    mergedImports[i] = mergedImports[i] with
                    {
                        Descriptor = new ImportDescriptor.Memory(new MemoryType(new Limits(min, max), shared)),
                    };
                    break;
                }
            }
            else if (mergedMemories.Count > 0)
            {
                // Apply to first local memory
                var existing = mergedMemories[0];
                uint min = initialPages ?? (uint)existing.Limits.Min;
                if (initialPages.HasValue && existing.Limits.Min > initialPages.Value)
                    throw new LinkerException(
                        $"--initial-memory too small: data segments require at least {existing.Limits.Min * WasmPageSize} bytes ({existing.Limits.Min} pages).");

                uint? max = maxPages ?? (uint?)existing.Limits.Max;
                bool shared = options.SharedMemory || existing.Shared;

                mergedMemories[0] = new MemoryType(new Limits(min, max), shared);
            }
        }
    }

    /// <summary>
    /// Applies table-related linker options: <c>--import-table</c> and <c>--growable-table</c>.
    /// </summary>
    private static void ApplyTableOptions(
        List<Import> mergedImports,
        List<TableType> mergedTables,
        LinkerOptions options)
    {
        if (options.ImportTable)
        {
            // Determine current table 0 limits (local or imported)
            uint currentMin = 0;
            uint? currentMax = null;
            RefType elemType = RefType.FuncRef;

            int existingImportIndex = -1;
            for (int i = 0; i < mergedImports.Count; i++)
            {
                if (mergedImports[i].Descriptor is ImportDescriptor.Table tab &&
                    mergedImports[i].ModuleName == options.ImportTableModule &&
                    mergedImports[i].Name == options.ImportTableName)
                {
                    existingImportIndex = i;
                    currentMin = (uint)tab.Type.Limits.Min;
                    currentMax = (uint?)tab.Type.Limits.Max;
                    elemType = tab.Type.ElementType;
                    break;
                }
            }

            if (mergedTables.Count > 0)
            {
                currentMin = Math.Max(currentMin, (uint)mergedTables[0].Limits.Min);
                if (mergedTables[0].Limits.Max is { } mergedMax64)
                {
                    uint mergedMax = (uint)mergedMax64;
                    currentMax = currentMax.HasValue
                        ? Math.Max(currentMax.Value, mergedMax)
                        : mergedMax;
                }
                elemType = mergedTables[0].ElementType;
                // Remove the first local table (it becomes an import)
                mergedTables.RemoveAt(0);
            }

            uint? max = options.GrowableTable ? null : currentMax;
            var tableType = new TableType(elemType, new Limits(currentMin, max));

            if (existingImportIndex >= 0)
            {
                mergedImports[existingImportIndex] = mergedImports[existingImportIndex] with
                {
                    Descriptor = new ImportDescriptor.Table(tableType),
                };
            }
            else
            {
                mergedImports.Add(new Import(options.ImportTableModule, options.ImportTableName,
                    new ImportDescriptor.Table(tableType)));
            }
        }
        else if (options.GrowableTable && mergedTables.Count > 0)
        {
            // Remove max bound from table 0 to allow growth
            var t = mergedTables[0];
            mergedTables[0] = new TableType(t.ElementType, new Limits(t.Limits.Min, null));
        }
    }

    /// <summary>
    /// Offsets element segment expressions for table 0 by <paramref name="tableBase"/>.
    /// </summary>
    private static void ApplyTableBase(List<Element> elements, uint tableBase)
    {
        if (tableBase == 0)
            return;

        for (int i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            if (elem.TableIndex < 0)
                continue;

            if (!TryReadConstI32Expr(elem.OffsetExpr, out uint currentOffset))
                continue;

            uint newOffset = checked(currentOffset + tableBase);
            elements[i] = new Element
            {
                TableIndex = elem.TableIndex,
                OffsetExpr = MakeConstI32Expr(newOffset),
                FunctionIndices = elem.FunctionIndices,
            };
        }
    }

    /// <summary>Creates a const i32 expression: <c>(i32.const N) end</c>.</summary>
    private static byte[] MakeConstI32Expr(uint value)
    {
        using var ms = new MemoryStream(8);
        ms.WriteByte(0x41); // i32.const
        WasmEncoder.WriteS32Leb(ms, unchecked((int)value));
        ms.WriteByte(0x0B); // end
        return ms.ToArray();
    }

    private static void EnsureTableCapacity(
        List<Import> mergedImports,
        List<TableType> mergedTables,
        IReadOnlyList<Element> elements)
    {
        var requiredEntries = new Dictionary<int, uint>();
        foreach (var element in elements)
        {
            if (element.TableIndex < 0 ||
                !TryReadConstI32Expr(element.OffsetExpr, out uint offset))
            {
                continue;
            }

            uint required = checked(offset + (uint)element.FunctionIndices.Count);
            if (requiredEntries.TryGetValue(element.TableIndex, out var existing))
                requiredEntries[element.TableIndex] = Math.Max(existing, required);
            else
                requiredEntries[element.TableIndex] = required;
        }

        int importedTableCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Table) importedTableCount++;
        foreach (var (tableIndex, required) in requiredEntries)
        {
            if (tableIndex < importedTableCount)
            {
                UpdateImportedTableLimit(mergedImports, tableIndex, required);
            }
            else
            {
                int localIndex = tableIndex - importedTableCount;
                if ((uint)localIndex >= (uint)mergedTables.Count)
                    continue;

                mergedTables[localIndex] = ExpandTable(mergedTables[localIndex], required);
            }
        }
    }

    private static void UpdateImportedTableLimit(List<Import> mergedImports, int tableIndex, uint requiredEntries)
    {
        int seen = 0;
        for (int importIndex = 0; importIndex < mergedImports.Count; importIndex++)
        {
            if (mergedImports[importIndex].Descriptor is not ImportDescriptor.Table table)
                continue;

            if (seen == tableIndex)
            {
                mergedImports[importIndex] = mergedImports[importIndex] with
                {
                    Descriptor = new ImportDescriptor.Table(ExpandTable(table.Type, requiredEntries)),
                };
                return;
            }

            seen++;
        }
    }

    private static TableType ExpandTable(TableType tableType, uint requiredEntries)
    {
        uint min = Math.Max((uint)tableType.Limits.Min, requiredEntries);
        uint? max = tableType.Limits.Max.HasValue
            ? Math.Max((uint)tableType.Limits.Max.Value, min)
            : null;
        return new TableType(tableType.ElementType, new Limits(min, max));
    }

    private static bool TryReadConstI32Expr(ReadOnlySpan<byte> expr, out uint value)
    {
        value = 0;
        if (expr.Length < 3 || expr[0] != 0x41)
            return false;

        int offset = 1;
        int shift = 0;
        int decoded = 0;
        byte current;

        do
        {
            if ((uint)offset >= (uint)expr.Length)
                return false;

            current = expr[offset++];
            decoded |= (current & 0x7F) << shift;
            shift += 7;
        } while ((current & 0x80) != 0 && shift < 35);

        if (offset != expr.Length - 1 || expr[^1] != 0x0B || decoded < 0)
            return false;

        value = unchecked((uint)decoded);
        return true;
    }

    private static byte[] RemapConstExprIndices(byte[] expr, IndexMap indexMap)
    {
        if (expr.Length == 0)
            return [];

        using var output = new MemoryStream(expr.Length + 8);
        int offset = 0;
        while (offset < expr.Length)
        {
            byte opcode = expr[offset++];
            output.WriteByte(opcode);

            switch (opcode)
            {
                case 0x0B:
                    return output.ToArray();
                case 0x41:
                case 0x42:
                case 0xD0:
                    CopyLebImmediate(expr, ref offset, output);
                    break;
                case 0x43:
                    if (offset + 4 > expr.Length)
                        throw new LinkerException("Malformed f32.const in const expression.");
                    output.Write(expr, offset, 4);
                    offset += 4;
                    break;
                case 0x44:
                    if (offset + 8 > expr.Length)
                        throw new LinkerException("Malformed f64.const in const expression.");
                    output.Write(expr, offset, 8);
                    offset += 8;
                    break;
                case 0x23:
                {
                    uint oldGlobalIndex = ReadU32Leb(expr, ref offset);
                    uint remappedGlobalIndex = ResolveMappedIndex(indexMap.Globals, oldGlobalIndex, "global");
                    WasmEncoder.WriteU32Leb(output, remappedGlobalIndex);
                    break;
                }
                case 0xD2:
                {
                    uint oldFunctionIndex = ReadU32Leb(expr, ref offset);
                    uint remappedFunctionIndex = ResolveMappedIndex(indexMap.Functions, oldFunctionIndex, "function");
                    WasmEncoder.WriteU32Leb(output, remappedFunctionIndex);
                    break;
                }
                case 0xFD:
                    CopyLebImmediate(expr, ref offset, output);
                    if (offset + 16 > expr.Length)
                        throw new LinkerException("Malformed SIMD constant in const expression.");
                    output.Write(expr, offset, 16);
                    offset += 16;
                    break;
                case 0x6A:
                case 0x6B:
                case 0x6C:
                case 0x7C:
                case 0x7D:
                case 0x7E:
                    break;
                default:
                    throw new LinkerException(
                        $"Unsupported constant-expression opcode 0x{opcode:X2} while remapping merged sections.");
            }
        }

        return output.ToArray();
    }

    private static void CopyLebImmediate(byte[] expr, ref int offset, Stream output)
    {
        while (true)
        {
            if ((uint)offset >= (uint)expr.Length)
                throw new LinkerException("Truncated LEB128 immediate in const expression.");

            byte current = expr[offset++];
            output.WriteByte(current);
            if ((current & 0x80) == 0)
                return;
        }
    }

    private static uint ReadU32Leb(byte[] expr, ref int offset)
    {
        uint value = 0;
        int shift = 0;
        while (true)
        {
            if ((uint)offset >= (uint)expr.Length)
                throw new LinkerException("Truncated u32 LEB128 immediate in const expression.");

            byte current = expr[offset++];
            value |= (uint)(current & 0x7F) << shift;
            if ((current & 0x80) == 0)
                return value;

            shift += 7;
            if (shift >= 35)
                throw new LinkerException("Invalid u32 LEB128 immediate in const expression.");
        }
    }

    private static void WriteSection(Stream output, byte sectionId, Action<Stream> writeContent)
    {
        using var content = new MemoryStream();
        writeContent(content);

        output.WriteByte(sectionId);
        WasmEncoder.WriteU32Leb(output, (uint)content.Length);
        // GetBuffer() returns the internal buffer directly; avoids the extra copy that ToArray() would make.
        output.Write(content.GetBuffer(), 0, (int)content.Length);
    }

    private static void WriteTypeSection(Stream stream, List<FuncType> types)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)types.Count);
        foreach (var type in types)
        {
            stream.WriteByte(0x60);
            WriteValTypes(stream, type.Parameters);
            WriteValTypes(stream, type.Results);
        }
    }

    private static void WriteValTypes(Stream stream, ValType[] types)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)types.Length);
        foreach (var type in types)
            stream.WriteByte((byte)type);
    }

    private static void WriteImportSection(Stream stream, List<Import> imports)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)imports.Count);
        foreach (var import in imports)
        {
            WriteName(stream, import.ModuleName);
            WriteName(stream, import.Name);

            switch (import.Descriptor)
            {
                case ImportDescriptor.Func function:
                    stream.WriteByte(0x00);
                    WasmEncoder.WriteU32Leb(stream, (uint)function.TypeIndex);
                    break;
                case ImportDescriptor.Table table:
                    stream.WriteByte(0x01);
                    WriteTableType(stream, table.Type);
                    break;
                case ImportDescriptor.Memory memory:
                    stream.WriteByte(0x02);
                    WriteMemoryLimits(stream, memory.Type.Limits, memory.Type.Shared);
                    break;
                case ImportDescriptor.Global global:
                    stream.WriteByte(0x03);
                    WriteGlobalType(stream, global.Type);
                    break;
                case ImportDescriptor.Tag tag:
                    stream.WriteByte(0x04);
                    stream.WriteByte(0x00);
                    WasmEncoder.WriteU32Leb(stream, (uint)tag.TypeIndex);
                    break;
                default:
                    throw new LinkerException($"Unsupported import descriptor type '{import.Descriptor.GetType().Name}'.");
            }
        }
    }

    private static void WriteFunctionSection(Stream stream, List<WasmFunction> functions)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)functions.Count);
        foreach (var function in functions)
            WasmEncoder.WriteU32Leb(stream, (uint)function.TypeIndex);
    }

    private static void WriteTableSection(Stream stream, List<TableType> tables)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)tables.Count);
        foreach (var table in tables)
            WriteTableType(stream, table);
    }

    private static void WriteMemorySection(Stream stream, List<MemoryType> memories)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)memories.Count);
        foreach (var memory in memories)
            WriteMemoryLimits(stream, memory.Limits, memory.Shared);
    }

    private static void WriteTagSection(Stream stream, List<Tag> tags)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)tags.Count);
        foreach (var tag in tags)
        {
            stream.WriteByte(0x00);
            WasmEncoder.WriteU32Leb(stream, (uint)tag.TypeIndex);
        }
    }

    private static void WriteGlobalSection(Stream stream, List<WasmGlobal> globals)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)globals.Count);
        foreach (var global in globals)
        {
            WriteGlobalType(stream, global.Type);
            stream.Write(global.InitExpr);
        }
    }

    private static void WriteExportSection(Stream stream, OrderedDictionary<string, Export> exports)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)exports.Count);
        foreach (var (name, export) in exports)
        {
            WriteName(stream, name);
            switch (export.Descriptor)
            {
                case ExportDescriptor.Func function:
                    stream.WriteByte(0x00);
                    WasmEncoder.WriteU32Leb(stream, (uint)function.FunctionIndex);
                    break;
                case ExportDescriptor.Table table:
                    stream.WriteByte(0x01);
                    WasmEncoder.WriteU32Leb(stream, (uint)table.TableIndex);
                    break;
                case ExportDescriptor.Memory memory:
                    stream.WriteByte(0x02);
                    WasmEncoder.WriteU32Leb(stream, (uint)memory.MemoryIndex);
                    break;
                case ExportDescriptor.Global global:
                    stream.WriteByte(0x03);
                    WasmEncoder.WriteU32Leb(stream, (uint)global.GlobalIndex);
                    break;
                case ExportDescriptor.Tag tag:
                    stream.WriteByte(0x04);
                    WasmEncoder.WriteU32Leb(stream, (uint)tag.TagIndex);
                    break;
                default:
                    throw new LinkerException($"Unsupported export descriptor type '{export.Descriptor.GetType().Name}'.");
            }
        }
    }

    private static void WriteElementSection(Stream stream, List<Element> elements)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)elements.Count);
        foreach (var element in elements)
        {
            if (element.TableIndex < 0)
            {
                WasmEncoder.WriteU32Leb(stream, 1);
                stream.WriteByte(0x00);
            }
            else if (element.TableIndex == 0)
            {
                WasmEncoder.WriteU32Leb(stream, 0);
                stream.Write(GetActiveOffsetExpr(element.OffsetExpr));
            }
            else
            {
                WasmEncoder.WriteU32Leb(stream, 2);
                WasmEncoder.WriteU32Leb(stream, (uint)element.TableIndex);
                stream.Write(GetActiveOffsetExpr(element.OffsetExpr));
                stream.WriteByte(0x00);
            }

            WasmEncoder.WriteU32Leb(stream, (uint)element.FunctionIndices.Count);
            foreach (var functionIndex in element.FunctionIndices)
                WasmEncoder.WriteU32Leb(stream, (uint)functionIndex);
        }
    }

    private static byte[] GetActiveOffsetExpr(byte[] offsetExpr) =>
        offsetExpr.Length == 0 ? BuildI32ConstExpr(0) : offsetExpr;

    private static void WriteCodeSection(Stream stream, List<WasmFunction> functions)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)functions.Count);
        foreach (var function in functions)
        {
            using var bodyStream = new MemoryStream(function.Body.Length + 16);
            WriteLocals(bodyStream, function.Locals);
            bodyStream.Write(function.Body);
            if (function.Body.Length == 0 || function.Body[^1] != 0x0B)
                bodyStream.WriteByte(0x0B);

            // GetBuffer() avoids the extra copy that ToArray() would make.
            WasmEncoder.WriteU32Leb(stream, (uint)bodyStream.Length);
            stream.Write(bodyStream.GetBuffer(), 0, (int)bodyStream.Length);
        }
    }

    private static void WriteLocals(Stream stream, List<ValType> locals)
    {
        if (locals.Count == 0)
        {
            WasmEncoder.WriteU32Leb(stream, 0);
            return;
        }

        var groups = new List<(uint Count, ValType Type)>();
        ValType current = locals[0];
        uint count = 1;
        for (int index = 1; index < locals.Count; index++)
        {
            if (locals[index] == current)
            {
                count++;
                continue;
            }

            groups.Add((count, current));
            current = locals[index];
            count = 1;
        }

        groups.Add((count, current));
        WasmEncoder.WriteU32Leb(stream, (uint)groups.Count);
        foreach (var (groupCount, type) in groups)
        {
            WasmEncoder.WriteU32Leb(stream, groupCount);
            stream.WriteByte((byte)type);
        }
    }

    private static void WriteDataSection(Stream stream, List<Data> dataSegments)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)dataSegments.Count);
        foreach (var segment in dataSegments)
        {
            if (segment.IsPassive)
            {
                WasmEncoder.WriteU32Leb(stream, 1);
            }
            else if (segment.MemoryIndex == 0)
            {
                WasmEncoder.WriteU32Leb(stream, 0);
                stream.Write(GetActiveOffsetExpr(segment.OffsetExpr));
            }
            else
            {
                WasmEncoder.WriteU32Leb(stream, 2);
                WasmEncoder.WriteU32Leb(stream, (uint)segment.MemoryIndex);
                stream.Write(GetActiveOffsetExpr(segment.OffsetExpr));
            }

            WasmEncoder.WriteU32Leb(stream, (uint)segment.InitData.Length);
            stream.Write(segment.InitData);
        }
    }

    private static void WriteTableType(Stream stream, TableType tableType)
    {
        stream.WriteByte((byte)tableType.ElementType);
        WriteLimits(stream, tableType.Limits);
    }

    private static void WriteGlobalType(Stream stream, GlobalType globalType)
    {
        stream.WriteByte((byte)globalType.ContentType);
        stream.WriteByte((byte)globalType.Mutability);
    }

    private static void WriteLimits(Stream stream, Limits limits)
    {
        if (limits.Is64)
            throw new LinkerException(
                Wasm64Guard.NotSupportedMessage("table64", "linking"));
        if (limits.Max.HasValue)
        {
            stream.WriteByte(0x01);
            WasmEncoder.WriteU32Leb(stream, (uint)limits.Min);
            WasmEncoder.WriteU32Leb(stream, (uint)limits.Max.Value);
        }
        else
        {
            stream.WriteByte(0x00);
            WasmEncoder.WriteU32Leb(stream, (uint)limits.Min);
        }
    }

    private static void WriteMemoryLimits(Stream stream, Limits limits, bool shared)
    {
        if (limits.Is64)
            throw new LinkerException(
                Wasm64Guard.NotSupportedMessage("memory64", "linking"));
        byte flag = 0x00;
        if (limits.Max.HasValue) flag |= 0x01;
        if (shared) flag |= 0x02;
        stream.WriteByte(flag);
        WasmEncoder.WriteU32Leb(stream, (uint)limits.Min);
        if (limits.Max.HasValue)
            WasmEncoder.WriteU32Leb(stream, (uint)limits.Max.Value);
    }

    private static void WriteName(Stream stream, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WasmEncoder.WriteU32Leb(stream, (uint)byteCount);
        if (byteCount <= 256)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(value, buf);
            stream.Write(buf);
        }
        else
        {
            stream.Write(Encoding.UTF8.GetBytes(value));
        }
    }

    private readonly record struct ImportKey(string ModuleName, string Name, ImportDescriptor Descriptor);
    private readonly record struct MergedImportEntry(int SectionIndex, uint KindIndex);
}

/// <summary>Result of import-section merging, including the merged section entries and per-kind import counts.</summary>
public sealed class ImportMergeResult
{
    public required List<Import> Imports { get; init; }
    public required uint FunctionImportCount { get; init; }
    public required uint GlobalImportCount { get; init; }
    public required uint TableImportCount { get; init; }
    public required uint MemoryImportCount { get; init; }
    public required uint TagImportCount { get; init; }
}
