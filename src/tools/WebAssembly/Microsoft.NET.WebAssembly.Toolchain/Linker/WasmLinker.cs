// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>Options that control linking behaviour.</summary>
public sealed class LinkerOptions
{
    /// <summary>
    /// Name of the module entry-point function (e.g. <c>"_start"</c> or
    /// <c>"__wasm_call_ctors"</c>). When non-null the linker sets the merged
    /// module's start-function to the function with this export name.
    /// </summary>
    public string? EntryPoint { get; init; }

    /// <summary>
    /// When <c>true</c> the linker does not error on unresolved symbols;
    /// they become imports in the output module.
    /// </summary>
    public bool AllowUndefined { get; init; }

    /// <summary>
    /// Additional symbol names that must be exported even if they carry no
    /// <c>WASM_SYM_EXPORTED</c> flag. Can be combined with <see cref="EntryPoint"/>.
    /// </summary>
    public List<string> Exports { get; set; } = new();

    /// <summary>
    /// When <c>true</c>, all symbols (functions, globals, memories, tables) from all
    /// input modules are exported. Overrides individual <see cref="Exports"/> entries.
    /// </summary>
    public bool ExportAll { get; set; }

    /// <summary>Enable dead code elimination (default: true).</summary>
    public bool GcSections { get; init; } = true;

    /// <summary>
    /// When <c>true</c>, adjacent active data segments targeting the same memory are coalesced
    /// into fewer, larger segments. Small gaps (up to <see cref="CoalesceGapThreshold"/> bytes)
    /// are zero-filled rather than emitting a new segment header. Default: <c>true</c>.
    /// </summary>
    public bool CoalesceSegments { get; init; } = true;

    /// <summary>
    /// Maximum gap in bytes between two adjacent active data segments that will be zero-filled
    /// rather than emitting a new segment. Default: 32 bytes.
    /// </summary>
    public int CoalesceGapThreshold { get; init; } = 32;

    /// <summary>Print removed item names to stderr.</summary>
    public bool PrintGcSections { get; init; }

    /// <summary>
    /// When <c>true</c>, strips all <c>.debug_*</c> custom sections from the output.
    /// Default: <c>false</c> (pass through debug sections if present).
    /// </summary>
    public bool StripDebug { get; init; }

    /// <summary>
    /// When <c>true</c>, suppresses emission of the <c>name</c> custom section.
    /// Default: <c>false</c> (emit name section when symbol names are available).
    /// </summary>
    public bool NoNameSection { get; init; }

    /// <summary>
    /// When <c>true</c>, the output module imports its linear memory instead of defining it locally.
    /// </summary>
    public bool ImportMemory { get; init; }

    /// <summary>Module name for the imported memory (default: <c>"env"</c>).</summary>
    public string ImportMemoryModule { get; init; } = "env";

    /// <summary>Field name for the imported memory (default: <c>"memory"</c>).</summary>
    public string ImportMemoryName { get; init; } = "memory";

    /// <summary>
    /// When <c>true</c>, the output module exports its linear memory.
    /// </summary>
    public bool ExportMemory { get; init; }

    /// <summary>Export name for the memory (default: <c>"memory"</c>).</summary>
    public string ExportMemoryName { get; init; } = "memory";

    /// <summary>
    /// Override the initial (minimum) memory size in bytes. Must be a multiple of 65536 (wasm page size).
    /// </summary>
    public long? InitialMemory { get; init; }

    /// <summary>
    /// Override the maximum memory size in bytes. Must be a multiple of 65536 (wasm page size).
    /// </summary>
    public long? MaxMemory { get; init; }

    /// <summary>
    /// When <c>true</c>, the memory is marked as shared (requires <see cref="MaxMemory"/> and <see cref="ImportMemory"/>).
    /// </summary>
    public bool SharedMemory { get; init; }

    /// <summary>Byte address where data placement begins. Typical dotnet value: 1024 (null-guard page).</summary>
    public uint? GlobalBase { get; init; }

    /// <summary>When true, stack is placed before data: [GlobalBase..GlobalBase+StackSize) = stack, then data follows.</summary>
    public bool StackFirst { get; init; }

    /// <summary>Shadow stack size in bytes. Default: 65536 (64 KiB). Dotnet SDK typically uses 8 MB.</summary>
    public uint StackSize { get; init; } = 65536;

    /// <summary>
    /// When <c>true</c>, the output module exports the indirect call table as
    /// <c>__indirect_function_table</c> (table index 0).
    /// </summary>
    public bool ExportTable { get; init; }

    /// <summary>
    /// When <c>true</c>, the indirect call table (table 0) is emitted with no
    /// maximum limit so it can grow via <c>table.grow</c>.
    /// </summary>
    public bool GrowableTable { get; init; }

    /// <summary>
    /// When <c>true</c>, the output module imports the indirect call table from the host
    /// instead of defining it locally (same pattern as <see cref="ImportMemory"/>).
    /// </summary>
    public bool ImportTable { get; init; }

    /// <summary>Module name for the imported table (default: <c>"env"</c>).</summary>
    public string ImportTableModule { get; init; } = "env";

    /// <summary>Field name for the imported table (default: <c>"__indirect_function_table"</c>).</summary>
    public string ImportTableName { get; init; } = "__indirect_function_table";

    /// <summary>
    /// Starting index for the indirect function table. When set, element segment
    /// offsets for table 0 are adjusted by this base value. Typical value: 1
    /// (index 0 reserved as a null sentinel).
    /// </summary>
    public uint? TableBase { get; init; }

    /// <summary>
    /// Symbol names that are exported only if they exist in the resolved symbol table.
    /// Missing symbols are silently skipped (unlike <see cref="Exports"/> which always export).
    /// </summary>
    public List<string> ExportIfDefined { get; set; } = new();
}

/// <summary>
/// Managed WebAssembly static linker.  Merges N input <see cref="WasmModule"/> objects
/// (typically parsed from <c>.wasm.o</c> object files) into a single, self-contained
/// output module.
///
/// <para>
/// Linking phases (matching wasm-ld's responsibility map from the tool-conventions spec):
/// <list type="number">
///   <item>Type deduplication — identical function signatures are merged into one type entry.</item>
///   <item>Symbol resolution — strong/weak/undefined rules applied across all modules.</item>
///   <item>Import deduplication — imports sharing the same (module, name) pair are unified.</item>
///   <item>Index remapping — per-module <see cref="IndexMap"/>s are populated for every index space.</item>
///   <item>Section merging — types, imports, functions, globals, tables, memories, tags, and data segments are concatenated in spec order.</item>
///   <item>Export collection — exported symbols flow into the output export section.</item>
/// </list>
/// </para>
///
/// <para>
/// Function bodies are copied verbatim.  Relocation patching (applying
/// <c>reloc.CODE</c> / <c>reloc.DATA</c> entries using the populated IndexMaps)
/// is a separate pass and is not performed here.
/// </para>
/// </summary>
public sealed class WasmLinker
{
    /// <summary>
    /// Links the supplied input module streams into a merged output module.
    /// </summary>
    /// <param name="inputStreams">Input module streams in link order.</param>
    /// <param name="options">Optional linker configuration.</param>
    /// <returns>A new <see cref="WasmModule"/> containing the merged content of all input modules.</returns>
    public WasmModule Link(IEnumerable<Stream> inputStreams, LinkerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputStreams);
        var modules = new List<WasmModule>();
        foreach (var stream in inputStreams)
            modules.Add(ModuleReader.ReadModule(stream));
        return Link(modules, options);
    }

    /// <summary>
    /// Links the supplied input modules into a merged output module.
    /// </summary>
    /// <param name="inputs">Input modules in link order.</param>
    /// <param name="options">Optional linker configuration.</param>
    /// <returns>
    /// A new <see cref="WasmModule"/> whose sections contain the merged content of
    /// all input modules.  Per-module <see cref="IndexMap"/>s are available via
    /// <see cref="LastLinkIndexMaps"/> after the call returns.
    /// </returns>
    public WasmModule Link(IReadOnlyList<WasmModule> inputs, LinkerOptions? options = null)
    {
        options ??= new LinkerOptions();
        ValidateMemoryOptions(options);
        var sectionMerger = new SectionMerger();

        var indexMaps = new IndexMap[inputs.Count];
        for (int i = 0; i < inputs.Count; i++)
            indexMaps[i] = new IndexMap();

        // ── Phase 1: Type deduplication ────────────────────────────────────────────
        var mergedTypes = sectionMerger.MergeTypes(inputs, indexMaps);

        // ── Phase 2: Symbol resolution ─────────────────────────────────────────────
        var symbolTable = new SymbolTable();
        for (int m = 0; m < inputs.Count; m++)
            symbolTable.AddModule(m, inputs[m]);
        symbolTable.ResolveUndefined();

        if (!options.AllowUndefined && symbolTable.UnresolvedSymbols.Count > 0)
            throw new LinkerException(
                $"Unresolved symbol(s): {string.Join(", ", symbolTable.UnresolvedSymbols)}");

        // ── Phase 3: Import deduplication + import-space index maps ───────────────
        // Imports are indexed separately per index space (functions start at 0 in
        // the function space, globals at 0 in the global space, etc.).
        var mergedImportResult = sectionMerger.MergeImports(inputs, indexMaps);
        var mergedImports = mergedImportResult.Imports;
        uint mergedFuncImportCount = mergedImportResult.FunctionImportCount;
        uint mergedGlobalImportCount = mergedImportResult.GlobalImportCount;
        uint mergedTableImportCount = mergedImportResult.TableImportCount;
        uint mergedMemImportCount = mergedImportResult.MemoryImportCount;
        uint mergedTagImportCount = mergedImportResult.TagImportCount;

        // ── Phase 4: Local-definition index assignments ────────────────────────────
        // After imports, locally-defined items are appended.  The merged index for
        // a locally-defined item is: importCount + runningOffset.

        uint nextFuncIdx   = mergedFuncImportCount;
        uint nextGlobalIdx = mergedGlobalImportCount;
        uint nextTableIdx  = mergedTableImportCount;
        uint nextMemIdx    = mergedMemImportCount;
        uint nextTagIdx    = mergedTagImportCount;
        uint nextDataIdx   = 0;

        for (int m = 0; m < inputs.Count; m++)
        {
            var module = inputs[m];

            // Count this module's imports per kind to know where local items start.
            uint localFuncImports   = 0;
            uint localGlobalImports = 0;
            uint localTableImports  = 0;
            uint localMemImports    = 0;
            uint localTagImports    = 0;

            foreach (var import in module.Imports)
            {
                switch (import.Descriptor)
                {
                    case ImportDescriptor.Func:   localFuncImports++;   break;
                    case ImportDescriptor.Global: localGlobalImports++; break;
                    case ImportDescriptor.Table:  localTableImports++;  break;
                    case ImportDescriptor.Memory: localMemImports++;    break;
                    case ImportDescriptor.Tag:    localTagImports++;    break;
                }
            }

            // Local functions: local index space offset = localFuncImports + j
            for (int j = 0; j < module.Functions.Count; j++)
                indexMaps[m].Functions[localFuncImports + (uint)j] = nextFuncIdx++;

            // Local globals
            for (int j = 0; j < module.Globals.Count; j++)
                indexMaps[m].Globals[localGlobalImports + (uint)j] = nextGlobalIdx++;

            // Local tables
            for (int j = 0; j < module.Tables.Count; j++)
                indexMaps[m].Tables[localTableImports + (uint)j] = nextTableIdx++;

            // Local memories
            for (int j = 0; j < module.Memories.Count; j++)
                indexMaps[m].Memories[localMemImports + (uint)j] = nextMemIdx++;

            // Local tags
            for (int j = 0; j < module.Tags.Count; j++)
                indexMaps[m].Tags[localTagImports + (uint)j] = nextTagIdx++;

            // Data segments
            for (int j = 0; j < module.DataSegments.Count; j++)
                indexMaps[m].DataSegments[(uint)j] = nextDataIdx++;
        }

        // Element segments
        uint nextElemIdx = 0;
        for (int m = 0; m < inputs.Count; m++)
        {
            for (int j = 0; j < inputs[m].Elements.Count; j++)
                indexMaps[m].Elements[(uint)j] = nextElemIdx++;
        }

        // Update symbol table with merged indices now that index maps are complete.
        UpdateSymbolMergedIndices(symbolTable, indexMaps);

        // ── Phase 4.5: Dead code elimination ──────────────────────────────────────
        DceLiveSet? liveSets = null;
        if (options.GcSections && inputs.Any(static m => m.LinkingSection is not null))
        {
            var dce = new DeadCodeEliminator();
            liveSets = dce.ComputeLiveSets(inputs, indexMaps, symbolTable, mergedFuncImportCount, options);
            DeadCodeEliminator.ComposeCompactionIntoIndexMaps(inputs, indexMaps, liveSets);
            UpdateSymbolMergedIndices(symbolTable, indexMaps);
            mergedTypes = DeadCodeEliminator.CompactTypes(mergedTypes, liveSets, inputs, indexMaps, mergedImports);
        }

        // ── Phase 5: Build merged sections ────────────────────────────────────────
        var mergedModule = sectionMerger.BuildMergedModule(
            inputs,
            indexMaps,
            mergedTypes,
            mergedImports,
            symbolTable.Symbols,
            options,
            liveSets);

        LastLinkIndexMaps = indexMaps;
        LastSymbolTable   = symbolTable;

        return mergedModule;
    }

    /// <summary>Links the supplied input modules and immediately emits the merged output as a binary <c>.wasm</c>.</summary>
    public byte[] LinkToBinary(IReadOnlyList<WasmModule> inputs, LinkerOptions? options = null)
    {
        var mergedModule = Link(inputs, options);
        return new SectionMerger().EmitBinary(mergedModule);
    }

    /// <summary>Index maps from the most recent <see cref="Link(IReadOnlyList{WasmModule}, LinkerOptions?)"/> call. Useful for relocation patching.</summary>
    public IReadOnlyList<IndexMap>? LastLinkIndexMaps { get; private set; }

    /// <summary>Symbol table from the most recent <see cref="Link(IReadOnlyList{WasmModule}, LinkerOptions?)"/> call.</summary>
    public SymbolTable? LastSymbolTable { get; private set; }

    private static void UpdateSymbolMergedIndices(
        SymbolTable symTable,
        IndexMap[] indexMaps)
    {
        foreach (var sym in symTable.Symbols.Values)
        {
            if (!sym.DefiningModuleIndex.HasValue || !sym.LocalIndex.HasValue)
                continue;

            var map = indexMaps[sym.DefiningModuleIndex.Value];
            sym.MergedIndex = sym.Kind switch
            {
                Binary.Linking.SymbolType.Function => map.Functions.GetValueOrDefault(sym.LocalIndex.Value),
                Binary.Linking.SymbolType.Global   => map.Globals.GetValueOrDefault(sym.LocalIndex.Value),
                Binary.Linking.SymbolType.Table    => map.Tables.GetValueOrDefault(sym.LocalIndex.Value),
                Binary.Linking.SymbolType.Event    => map.Tags.GetValueOrDefault(sym.LocalIndex.Value),
                Binary.Linking.SymbolType.Data     => map.DataSegments.GetValueOrDefault(sym.LocalIndex.Value),
                _ => sym.LocalIndex,
            };
        }
    }

    private const long MaxMemoryBytes = 4L * 1024 * 1024 * 1024; // 4 GiB

    private static void ValidateMemoryOptions(LinkerOptions options)
    {
        if (options.InitialMemory.HasValue)
        {
            if (options.InitialMemory.Value % 65536 != 0)
                throw new LinkerException("--initial-memory must be a multiple of 65536 (wasm page size).");
            if (options.InitialMemory.Value < 0 || options.InitialMemory.Value > MaxMemoryBytes)
                throw new LinkerException($"--initial-memory must be between 0 and {MaxMemoryBytes} bytes (4 GiB).");
        }

        if (options.MaxMemory.HasValue)
        {
            if (options.MaxMemory.Value % 65536 != 0)
                throw new LinkerException("--max-memory must be a multiple of 65536 (wasm page size).");
            if (options.MaxMemory.Value < 0 || options.MaxMemory.Value > MaxMemoryBytes)
                throw new LinkerException($"--max-memory must be between 0 and {MaxMemoryBytes} bytes (4 GiB).");
        }

        if (options.InitialMemory.HasValue && options.MaxMemory.HasValue &&
            options.InitialMemory.Value > options.MaxMemory.Value)
        {
            throw new LinkerException("--initial-memory cannot exceed --max-memory.");
        }

        if (options.SharedMemory && !options.MaxMemory.HasValue)
            throw new LinkerException("--shared-memory requires --max-memory.");

        if (options.SharedMemory && !options.ImportMemory)
            throw new LinkerException("--shared-memory requires --import-memory.");
    }
}

/// <summary>
/// Deduplicates <see cref="FuncType"/> entries by signature equality.
/// Returns indices into a shared merged-types list.
/// </summary>
internal sealed class FuncTypeDeduplicator
{
    private readonly Dictionary<FuncType, int> _index =
        new(FuncTypeSignatureComparer.Instance);

    /// <summary>
    /// Returns the index of <paramref name="type"/> in <paramref name="merged"/>,
    /// appending a new entry if this signature has not been seen before.
    /// </summary>
    public int GetOrAdd(FuncType type, List<FuncType> merged)
    {
        if (!_index.TryGetValue(type, out int idx))
        {
            idx = merged.Count;
            merged.Add(type);
            _index[type] = idx;
        }
        return idx;
    }
}

/// <summary>Structural equality comparer for <see cref="FuncType"/> based on parameter and result arrays.</summary>
internal sealed class FuncTypeSignatureComparer : IEqualityComparer<FuncType>
{
    public static readonly FuncTypeSignatureComparer Instance = new();

    public bool Equals(FuncType? x, FuncType? y)
    {
        if (ReferenceEquals(x, y))   return true;
        if (x is null || y is null)  return false;
        return x.Parameters.AsSpan().SequenceEqual(y.Parameters) &&
               x.Results.AsSpan().SequenceEqual(y.Results);
    }

    public int GetHashCode(FuncType t)
    {
        var hc = default(HashCode);
        foreach (var p in t.Parameters) hc.Add(p);
        hc.Add(0xFF); // separator
        foreach (var r in t.Results) hc.Add(r);
        return hc.ToHashCode();
    }
}
