// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Links relocatable WebAssembly object files (clang/LLVM <c>.o</c> files and ILCompiler objects) with the
/// semantics of <c>wasm-ld</c>: symbols are resolved by name, live code is found from the roots, every
/// <c>reloc.*</c> entry is applied, and the linker defines what objects only reference — linear memory, the
/// indirect function table, <c>__stack_pointer</c>, <c>__wasm_call_ctors</c> and the layout symbols.
/// </summary>
/// <remarks>
/// <para>
/// Unlike <see cref="SectionMerger"/>, which concatenates complete modules and keeps their imports, this path
/// treats an object's imports as placeholders: the memory import, <c>env.__indirect_function_table</c> and
/// <c>env.__stack_pointer</c> are replaced by definitions, and an undefined function becomes an import only when
/// nothing defines it and <see cref="LinkerOptions.AllowUndefined"/> is set.
/// </para>
/// <para>
/// Not yet implemented: shared memory, thread-local storage and position-independent code. Each is rejected with
/// a <see cref="LinkerException"/> rather than producing a module that would run incorrectly.
/// </para>
/// </remarks>
internal sealed class ObjectFileLinker
{
    private const uint PageSize = 65536;
    private const uint StackAlignment = 16;
    private const string IndirectFunctionTableName = "__indirect_function_table";
    private const string StackPointerName = "__stack_pointer";
    private const string CallCtorsName = "__wasm_call_ctors";

    private readonly LinkerOptions _options;
    private readonly List<ObjFile> _files = [];
    private readonly Queue<ObjFile> _pendingLoads = new();
    private readonly Dictionary<string, Sym> _globals = new(StringComparer.Ordinal);
    private readonly List<Sym> _globalOrder = [];
    private readonly Dictionary<string, ObjFile> _comdats = new(StringComparer.Ordinal);
    private readonly List<string> _undefinedErrors = [];

    private readonly List<FuncType> _types = [];
    private readonly Dictionary<FuncType, int> _typeIndex = new(FuncTypeSignatureComparer.Instance);

    private Sym? _stackPointer;
    private Sym? _indirectTable;
    private Sym? _callCtors;
    private bool _indirectTableNeeded;

    private ObjectFileLinker(LinkerOptions options) => _options = options;

    /// <summary>The per-input index maps of the last link (empty for archive members that were not loaded).</summary>
    public IndexMap[] IndexMaps { get; private set; } = [];

    /// <summary>The resolved global symbols of the last link.</summary>
    public SymbolTable SymbolTable { get; } = new();

    /// <summary>
    /// True when <paramref name="module"/> is recognisably a relocatable object: it has a version-2 <c>linking</c>
    /// section, defines no memory, and imports the placeholder memory every object carries — LLVM's
    /// <c>env.__linear_memory</c>, or any memory import alongside <c>reloc.*</c> sections (ILCompiler's
    /// <c>env.memory</c>). Anything else is merged as a complete module unless
    /// <see cref="LinkerOptions.InputKind"/> says otherwise.
    /// </summary>
    public static bool IsRelocatableObject(WasmModule module)
    {
        if (module.LinkingSection is not { Version: 2 } || module.Memories.Count != 0)
            return false;
        foreach (Import import in module.Imports)
        {
            if (import.Descriptor is ImportDescriptor.Memory &&
                (import.Name == "__linear_memory" || module.RelocationSections.Count > 0))
            {
                return true;
            }
        }
        return false;
    }

    public static (WasmModule Module, ObjectFileLinker Linker) Link(IReadOnlyList<LinkerInput> inputs, LinkerOptions options)
    {
        var linker = new ObjectFileLinker(options);
        return (linker.Run(inputs), linker);
    }

    // ── Model ─────────────────────────────────────────────────────────────────

    private enum Synthetic
    {
        None,
        StackPointer,
        IndirectTable,
        CallCtors,
        DataAddress,
    }

    /// <summary>A symbol: one shared instance per global name, or a private instance per local symbol.</summary>
    private sealed class Sym
    {
        public required string Name { get; init; }
        public required SymbolType Kind;
        public bool IsLocal { get; init; }

        // Definition.
        public ObjFile? DefFile;
        public WasmSymbol? Def;
        public Synthetic Synthetic;

        // References from undefined symbols. RefFile/RefIndex name the first import that refers to it.
        public bool Referenced;
        public bool AllRefsWeak = true;
        public ObjFile? RefFile;
        public uint RefIndex;

        public ObjFile? LazyFile;
        public bool Forced;
        public bool Live;

        // Output.
        public uint OutIndex;
        public bool HasOutIndex;
        public uint Address;
        public uint TableSlot;

        public bool IsDefined => DefFile is not null || Synthetic != Synthetic.None;
        public bool IsWeakDef => Def is not null && (Def.Flags & SymbolFlags.BindingWeak) != 0;
        public uint DefIndex => Kind switch
        {
            SymbolType.Data => Def!.SegmentIndex ?? throw new LinkerException($"Data symbol '{Name}' has no segment."),
            SymbolType.Section => Def!.SectionIndex ?? 0,
            _ => checked((uint)(Def!.Index ?? throw new LinkerException($"Symbol '{Name}' has no index."))),
        };
    }

    private sealed class ObjFile
    {
        public required int InputIndex { get; init; }
        public required string Name { get; init; }
        public required WasmModule Module { get; init; }
        public bool Loaded;

        public uint FuncImports, GlobalImports, TableImports, TagImports;
        public Import[] FuncImportList = [], GlobalImportList = [], TableImportList = [], TagImportList = [];
        public Sym[] Symbols = [];

        public Relocation[] CodeRelocs = [];
        public Relocation[] DataRelocs = [];
        public List<(string Name, Relocation[] Relocs)> CustomRelocs = [];

        public bool[] FuncLive = [], SegLive = [], GlobalLive = [], TagLive = [], TableLive = [];
        public HashSet<uint> DiscardedFuncs = [], DiscardedSegs = [], DiscardedGlobals = [];

        // Output placement.
        public int[] FuncOut = [];
        public int[] GlobalOut = [];
        public int[] TagOut = [];
        public int[] TableOut = [];
        public OutSegment?[] SegOut = [];
        public uint[] SegOffsetInOut = [];
        public uint[] SegAddress = [];

        public Dictionary<uint, string> FunctionExportNames = [];
    }

    private sealed class OutSegment
    {
        public required string Name { get; init; }
        public uint Alignment = 1;
        public uint Size;
        public uint Address;
        public bool IsBss => Name.StartsWith(".bss", StringComparison.Ordinal);
        public readonly List<(ObjFile File, int Segment)> Members = [];
        public byte[] Bytes = [];
    }

    // ── Driver ────────────────────────────────────────────────────────────────

    private WasmModule Run(IReadOnlyList<LinkerInput> inputs)
    {
        if (_options.SharedMemory)
            throw new LinkerException("--shared-memory is not supported yet when linking relocatable objects.");

        var all = new List<ObjFile>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            LinkerInput input = inputs[i];
            if (input.Module.LinkingSection is not { Version: 2 })
                throw new LinkerException($"Input '{input.Name ?? $"#{i}"}' is not a relocatable object (no version 2 'linking' section).");
            all.Add(new ObjFile { InputIndex = i, Name = input.Name ?? $"input #{i}", Module = input.Module });
        }

        // Command-line roots behave like undefined references: they extract archive members.
        if (_options.EntryPoint is { } entry)
            AddRootReference(entry);
        foreach (string name in _options.Exports)
            AddRootReference(name);

        for (int i = 0; i < all.Count; i++)
        {
            if (inputs[i].IsArchiveMember)
                AddLazy(all[i]);
            else
                Load(all[i]);
            DrainLoads();
        }

        DefineSynthetics();
        CheckRoots();
        MarkLive();
        if (_undefinedErrors.Count > 0)
            throw new LinkerException($"Undefined symbol(s): {string.Join(", ", _undefinedErrors.Distinct(StringComparer.Ordinal))}");

        return BuildOutput(inputs.Count);
    }

    private void AddRootReference(string name)
    {
        Sym sym = GetOrCreateGlobal(name, SymbolType.Function);
        sym.Forced = true;
    }

    private Sym GetOrCreateGlobal(string name, SymbolType kind)
    {
        if (!_globals.TryGetValue(name, out Sym? sym))
        {
            sym = new Sym { Name = name, Kind = kind };
            _globals[name] = sym;
            _globalOrder.Add(sym);
        }
        return sym;
    }

    private void AddLazy(ObjFile file)
    {
        foreach (WasmSymbol raw in file.Module.LinkingSection!.Symbols)
        {
            if (raw.Name is null || (raw.Flags & (SymbolFlags.BindingLocal | SymbolFlags.Undefined)) != 0)
                continue;
            if (raw.Type == SymbolType.Section)
                continue;

            if (_globals.TryGetValue(raw.Name, out Sym? existing))
            {
                if (existing.IsDefined || existing.LazyFile is not null)
                    continue;
                if (!existing.Referenced)
                    existing.Kind = raw.Type;
                existing.LazyFile = file;
                if ((existing.Referenced && !existing.AllRefsWeak) || existing.Forced)
                    _pendingLoads.Enqueue(file);
            }
            else
            {
                Sym sym = GetOrCreateGlobal(raw.Name, raw.Type);
                sym.LazyFile = file;
            }
        }
    }

    private void DrainLoads()
    {
        while (_pendingLoads.TryDequeue(out ObjFile? file))
            Load(file);
    }

    private void Load(ObjFile file)
    {
        if (file.Loaded)
            return;
        file.Loaded = true;
        _files.Add(file);

        WasmModule m = file.Module;
        if (m.Memories.Count > 0 || m.Imports.Count(static i => i.Descriptor is ImportDescriptor.Memory) > 1)
            throw new LinkerException($"{file.Name}: objects must not define memories or import more than one.");
        if (m.TypeDefinitions.Any(static t => t.CompositeType is not CompositeType.Function))
            throw new LinkerException($"{file.Name}: GC types are not supported when linking relocatable objects.");

        file.FuncImportList = [.. m.Imports.Where(static i => i.Descriptor is ImportDescriptor.Func)];
        file.GlobalImportList = [.. m.Imports.Where(static i => i.Descriptor is ImportDescriptor.Global)];
        file.TableImportList = [.. m.Imports.Where(static i => i.Descriptor is ImportDescriptor.Table)];
        file.TagImportList = [.. m.Imports.Where(static i => i.Descriptor is ImportDescriptor.Tag)];
        file.FuncImports = (uint)file.FuncImportList.Length;
        file.GlobalImports = (uint)file.GlobalImportList.Length;
        file.TableImports = (uint)file.TableImportList.Length;
        file.TagImports = (uint)file.TagImportList.Length;

        file.FuncLive = new bool[m.Functions.Count];
        file.SegLive = new bool[m.DataSegments.Count];
        file.GlobalLive = new bool[m.Globals.Count];
        file.TagLive = new bool[m.Tags.Count];
        file.TableLive = new bool[m.Tables.Count];

        foreach (RelocationSection section in m.RelocationSections)
        {
            Relocation[] sorted = [.. section.Entries.OrderBy(static r => r.Offset)];
            switch (section.Name)
            {
                case "reloc.CODE": file.CodeRelocs = sorted; break;
                case "reloc.DATA": file.DataRelocs = sorted; break;
                default:
                    if (section.Name.StartsWith("reloc.", StringComparison.Ordinal))
                        file.CustomRelocs.Add((section.Name["reloc.".Length..], sorted));
                    break;
            }
        }

        foreach (Export export in m.ExportEntries.Count > 0 ? m.ExportEntries : m.Exports.Values.ToList())
        {
            if (export.Descriptor is ExportDescriptor.Func f)
                file.FunctionExportNames.TryAdd((uint)f.FunctionIndex, export.Name);
        }

        LinkingSection linking = m.LinkingSection!;
        foreach (ComdatGroup group in linking.ComdatGroups)
        {
            if (_comdats.TryAdd(group.Name, file))
                continue;
            foreach (ComdatEntry entry in group.Entries)
            {
                switch (entry.Kind)
                {
                    case ComdatKind.Function: file.DiscardedFuncs.Add(entry.Index); break;
                    case ComdatKind.Data: file.DiscardedSegs.Add(entry.Index); break;
                    case ComdatKind.Global: file.DiscardedGlobals.Add(entry.Index); break;
                }
            }
        }

        foreach (SegmentInfo info in linking.SegmentInfos)
        {
            if ((info.Flags & SegmentFlags.Tls) != 0)
                throw new LinkerException($"{file.Name}: thread-local data segment '{info.Name}' is not supported yet.");
        }

        bool tableSymbolSeen = false;
        file.Symbols = new Sym[linking.Symbols.Count];
        for (int i = 0; i < linking.Symbols.Count; i++)
        {
            WasmSymbol raw = linking.Symbols[i];
            file.Symbols[i] = AddSymbol(file, raw);
            if (raw.Type == SymbolType.Table && file.Symbols[i].Name == IndirectFunctionTableName)
                tableSymbolSeen = true;
        }

        // Objects from before reference-types address the table without a table symbol.
        if (!tableSymbolSeen && file.TableImportList.Any(static t => t.Name == IndirectFunctionTableName))
            _indirectTableNeeded = true;
    }

    private Sym AddSymbol(ObjFile file, WasmSymbol raw)
    {
        bool undefined = (raw.Flags & SymbolFlags.Undefined) != 0;
        bool local = (raw.Flags & SymbolFlags.BindingLocal) != 0;
        bool weak = (raw.Flags & SymbolFlags.BindingWeak) != 0;
        bool discarded = !undefined && IsDiscarded(file, raw);

        if (raw.Type == SymbolType.Section)
            return new Sym { Name = raw.Name ?? "", Kind = raw.Type, IsLocal = true, DefFile = file, Def = raw };

        string name = raw.Name ?? ImportFieldName(file, raw) ?? throw new LinkerException($"{file.Name}: unnamed {raw.Type} symbol.");
        if (local)
        {
            if (undefined)
                throw new LinkerException($"{file.Name}: local symbol '{name}' is undefined.");
            return new Sym { Name = name, Kind = raw.Type, IsLocal = true, DefFile = discarded ? null : file, Def = discarded ? null : raw };
        }

        Sym sym = GetOrCreateGlobal(name, raw.Type);
        if (!sym.IsDefined && !sym.Referenced && sym.LazyFile is null)
            sym.Kind = raw.Type;
        if (sym.Kind != raw.Type && (sym.IsDefined || sym.Referenced))
            throw new LinkerException($"Symbol type mismatch for '{name}': {sym.Kind} in one input, {raw.Type} in {file.Name}.");

        if (undefined || discarded)
        {
            if (!sym.Referenced || sym.RefFile is null)
            {
                sym.RefFile = undefined ? file : sym.RefFile;
                sym.RefIndex = undefined && raw.Index is int index ? (uint)index : 0;
            }
            sym.Referenced = true;
            if (!weak || discarded)
                sym.AllRefsWeak = false;
            if (!sym.IsDefined && sym.LazyFile is { Loaded: false } lazy && !sym.AllRefsWeak)
                _pendingLoads.Enqueue(lazy);
            return sym;
        }

        if (!sym.IsDefined)
        {
            Define(sym, file, raw);
        }
        else if (!weak && sym.IsWeakDef)
        {
            Define(sym, file, raw);
        }
        else if (!weak && !sym.IsWeakDef)
        {
            throw new LinkerException($"Duplicate symbol: '{name}' is defined in {sym.DefFile!.Name} and {file.Name}.");
        }

        return sym;
    }

    /// <summary>
    /// An undefined symbol without <c>WASM_SYM_EXPLICIT_NAME</c> is named by the field of the import it refers to.
    /// </summary>
    private static string? ImportFieldName(ObjFile file, WasmSymbol raw)
    {
        if ((raw.Flags & SymbolFlags.Undefined) == 0 || raw.Index is not int index)
            return null;
        Import[] imports = raw.Type switch
        {
            SymbolType.Function => file.FuncImportList,
            SymbolType.Global => file.GlobalImportList,
            SymbolType.Table => file.TableImportList,
            SymbolType.Tag => file.TagImportList,
            _ => [],
        };
        return (uint)index < (uint)imports.Length ? imports[index].Name : null;
    }

    private static void Define(Sym sym, ObjFile file, WasmSymbol raw)
    {
        sym.DefFile = file;
        sym.Def = raw;
    }

    private static bool IsDiscarded(ObjFile file, WasmSymbol raw) => raw.Type switch
    {
        SymbolType.Function => raw.Index is int f && file.DiscardedFuncs.Contains((uint)f),
        SymbolType.Data => raw.SegmentIndex is uint s && file.DiscardedSegs.Contains(s),
        SymbolType.Global => raw.Index is int g && file.DiscardedGlobals.Contains((uint)g),
        _ => false,
    };

    // ── Synthetic symbols ─────────────────────────────────────────────────────

    private static readonly string[] s_dataAddressSymbols =
        ["__data_end", "__heap_base", "__heap_end", "__global_base", "__stack_low", "__stack_high", "__dso_handle"];

    private void DefineSynthetics()
    {
        _stackPointer = GetOrCreateGlobal(StackPointerName, SymbolType.Global);
        if (!_stackPointer.IsDefined)
        {
            _stackPointer.Synthetic = Synthetic.StackPointer;
            _stackPointer.Kind = SymbolType.Global;
        }

        if (_globals.TryGetValue(IndirectFunctionTableName, out Sym? table) && !table.IsDefined)
        {
            table.Synthetic = Synthetic.IndirectTable;
            _indirectTable = table;
        }
        else if (table is null)
        {
            _indirectTable = new Sym { Name = IndirectFunctionTableName, Kind = SymbolType.Table, Synthetic = Synthetic.IndirectTable };
        }
        else
        {
            throw new LinkerException($"Defining '{IndirectFunctionTableName}' in an object is not supported.");
        }

        // Without --gc-sections every chunk is kept, and wasm-ld then always emits __wasm_call_ctors.
        if (!_options.GcSections)
            GetOrCreateGlobal(CallCtorsName, SymbolType.Function);
        if (_globals.TryGetValue(CallCtorsName, out Sym? ctors) && !ctors.IsDefined)
        {
            ctors.Synthetic = Synthetic.CallCtors;
            _callCtors = ctors;
        }

        foreach (string name in s_dataAddressSymbols)
        {
            if (_globals.TryGetValue(name, out Sym? sym) && !sym.IsDefined && sym.Kind == SymbolType.Data)
                sym.Synthetic = Synthetic.DataAddress;
        }
    }

    private void CheckRoots()
    {
        if (_options.EntryPoint is { } entry)
        {
            Sym sym = _globals[entry];
            if (!sym.IsDefined || sym.Kind != SymbolType.Function)
                throw new LinkerException($"Entry symbol not defined (pass --no-entry to export an entry-less module): {entry}");
        }

        foreach (string name in _options.Exports)
        {
            if (!_globals[name].IsDefined && !_options.AllowUndefined)
                throw new LinkerException($"Symbol exported via --export not found: {name}");
        }
    }

    // ── Liveness ──────────────────────────────────────────────────────────────

    private readonly Stack<Action> _work = new();

    private void MarkLive()
    {
        bool gc = _options.GcSections;

        // As in wasm-ld (LLD 23+), __stack_pointer is defined only when it is live: referenced by live code,
        // exported, or kept because --no-gc-sections keeps everything.
        if (!gc || _options.ExportAll)
            MarkSymbol(_stackPointer!);
        if (!gc && _callCtors is not null)
            MarkSymbol(_callCtors);
        if (_options.EntryPoint is { } entry)
            MarkSymbol(_globals[entry]);
        foreach (string name in _options.Exports)
            MarkSymbol(_globals[name]);
        foreach (string name in _options.ExportIfDefined)
        {
            if (_globals.TryGetValue(name, out Sym? sym) && sym.IsDefined)
            {
                sym.Forced = true;
                MarkSymbol(sym);
            }
        }
        if (_options.ExportTable || _options.ImportTable || _options.GrowableTable || _indirectTableNeeded)
            MarkSymbol(_indirectTable!);

        foreach (ObjFile file in _files)
        {
            foreach (Sym sym in file.Symbols)
            {
                if (sym.Kind == SymbolType.Section)
                    continue;
                WasmSymbol? raw = sym.Def;
                bool root = !gc
                    || (_options.ExportAll && sym.IsDefined && !sym.IsLocal)
                    || (sym.DefFile == file && raw is not null && (raw.Flags & (SymbolFlags.Exported | SymbolFlags.NoStrip)) != 0);
                if (root)
                    MarkSymbol(sym);
            }

            // Undefined no_strip references (the table symbol of every object that uses call_indirect).
            foreach ((WasmSymbol raw, Sym sym) in file.Module.LinkingSection!.Symbols.Zip(file.Symbols))
            {
                if ((raw.Flags & (SymbolFlags.Undefined | SymbolFlags.NoStrip)) == (SymbolFlags.Undefined | SymbolFlags.NoStrip))
                    MarkSymbol(sym);
            }

            for (int s = 0; s < file.Module.LinkingSection!.SegmentInfos.Count; s++)
            {
                if (!gc || (file.Module.LinkingSection.SegmentInfos[s].Flags & SegmentFlags.Retain) != 0)
                    MarkSegment(file, s);
            }

            if (!gc)
            {
                for (int f = 0; f < file.Module.Functions.Count; f++)
                    MarkFunction(file, f);
                for (int s = 0; s < file.Module.DataSegments.Count; s++)
                    MarkSegment(file, s);
            }
        }

        while (_work.TryPop(out Action? action))
            action();
    }

    private void MarkSymbol(Sym sym)
    {
        if (sym.Live)
            return;
        sym.Live = true;

        if (sym.DefFile is ObjFile file)
        {
            uint index = sym.DefIndex;
            switch (sym.Kind)
            {
                case SymbolType.Function:
                    MarkFunction(file, checked((int)(index - file.FuncImports)));
                    break;
                case SymbolType.Data:
                    MarkSegment(file, checked((int)index));
                    break;
                case SymbolType.Global:
                    file.GlobalLive[index - file.GlobalImports] = true;
                    break;
                case SymbolType.Tag:
                    file.TagLive[index - file.TagImports] = true;
                    break;
                case SymbolType.Table:
                    file.TableLive[index - file.TableImports] = true;
                    break;
            }
            return;
        }

        if (sym.Synthetic == Synthetic.CallCtors)
        {
            foreach (ObjFile f in _files)
            {
                foreach (InitFunction init in f.Module.LinkingSection!.InitFunctions)
                    MarkSymbol(f.Symbols[init.SymbolIndex]);
            }
            return;
        }

        if (sym.Synthetic != Synthetic.None)
            return;

        // Undefined.
        if (sym.AllRefsWeak && !sym.Forced)
            return;
        if (_options.AllowUndefined && sym.Kind is SymbolType.Function or SymbolType.Global or SymbolType.Table or SymbolType.Tag or SymbolType.Data)
            return;
        _undefinedErrors.Add(sym.Name);
    }

    // Without --gc-sections wasm-ld keeps COMDAT-discarded chunks too; only their symbols resolve elsewhere.
    private void MarkFunction(ObjFile file, int defined)
    {
        if (file.FuncLive[defined] || (_options.GcSections && file.DiscardedFuncs.Contains(file.FuncImports + (uint)defined)))
            return;
        file.FuncLive[defined] = true;
        WasmFunction function = file.Module.Functions[defined];
        _work.Push(() => MarkRelocationTargets(file, file.CodeRelocs, function.BodyCodeSectionOffset, (uint)function.Body.Length));
    }

    private void MarkSegment(ObjFile file, int segment)
    {
        if (file.SegLive[segment] || (_options.GcSections && file.DiscardedSegs.Contains((uint)segment)))
            return;
        file.SegLive[segment] = true;
        Data data = file.Module.DataSegments[segment];
        _work.Push(() => MarkRelocationTargets(file, file.DataRelocs, data.SectionPayloadOffset, (uint)data.InitData.Length));
    }

    private void MarkRelocationTargets(ObjFile file, Relocation[] relocs, uint start, uint length)
    {
        foreach (Relocation reloc in RelocsInRange(relocs, start, length))
        {
            if (reloc.Type != RelocationType.R_WASM_TYPE_INDEX_LEB)
                MarkSymbol(file.Symbols[reloc.Index]);

            // Taking a function's address needs the indirect function table even when no object names it.
            if (IsTableIndexRelocation(reloc.Type))
                MarkSymbol(_indirectTable!);
        }
    }

    private static ArraySegment<Relocation> RelocsInRange(Relocation[] relocs, uint start, uint length)
    {
        int lo = LowerBound(relocs, start);
        int hi = LowerBound(relocs, start + length);
        return new ArraySegment<Relocation>(relocs, lo, hi - lo);
    }

    private static int LowerBound(Relocation[] relocs, uint offset)
    {
        int lo = 0, hi = relocs.Length;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (relocs[mid].Offset < offset)
                lo = mid + 1;
            else
                hi = mid;
        }
        return lo;
    }

    // ── Output ────────────────────────────────────────────────────────────────

    private int TypeIndexOf(FuncType type)
    {
        if (!_typeIndex.TryGetValue(type, out int index))
        {
            index = _types.Count;
            _types.Add(type);
            _typeIndex[type] = index;
        }
        return index;
    }

    private static readonly FuncType s_voidType = new([], []);

    private WasmModule BuildOutput(int inputCount)
    {
        var imports = new List<Import>();
        var functions = new List<WasmFunction>();
        var functionNames = new List<string?>();
        var globals = new List<WasmGlobal>();
        var globalNames = new List<string?>();
        var tables = new List<TableType>();
        var tags = new List<Tag>();

        bool tableLive = _indirectTable!.Live;

        // Type order follows wasm-ld: signatures named by type relocations, then imported functions, then
        // defined functions in output order.
        foreach (ObjFile file in _files)
        {
            var used = new SortedSet<uint>();
            for (int f = 0; f < file.FuncLive.Length; f++)
            {
                if (!file.FuncLive[f])
                    continue;
                WasmFunction fn = file.Module.Functions[f];
                foreach (Relocation reloc in RelocsInRange(file.CodeRelocs, fn.BodyCodeSectionOffset, (uint)fn.Body.Length))
                {
                    if (reloc.Type == RelocationType.R_WASM_TYPE_INDEX_LEB)
                        used.Add(reloc.Index);
                }
            }
            foreach (uint type in used)
                TypeIndexOf(file.Module.Types[(int)type]);
        }

        // ── Imports ──
        uint funcImportCount = 0, globalImportCount = 0, tableImportCount = 0, tagImportCount = 0;
        if (_options.ImportMemory)
            imports.Add(new Import(_options.ImportMemoryModule, _options.ImportMemoryName, new ImportDescriptor.Memory(new MemoryType(new Limits(0)))));
        if (tableLive && _options.ImportTable)
        {
            imports.Add(new Import(_options.ImportTableModule, _options.ImportTableName, new ImportDescriptor.Table(new TableType(RefType.FuncRef, new Limits(0)))));
            _indirectTable.OutIndex = tableImportCount++;
            _indirectTable.HasOutIndex = true;
        }

        // An undefined weak function that live code calls directly gets a trapping stub, as in wasm-ld; one that
        // is only address-taken gets table index 0.
        var calledDirectly = new HashSet<Sym>();
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncLive.Length; f++)
            {
                if (!file.FuncLive[f])
                    continue;
                WasmFunction fn = file.Module.Functions[f];
                foreach (Relocation reloc in RelocsInRange(file.CodeRelocs, fn.BodyCodeSectionOffset, (uint)fn.Body.Length))
                {
                    if (reloc.Type is RelocationType.R_WASM_FUNCTION_INDEX_LEB or RelocationType.R_WASM_FUNCTION_INDEX_I32)
                        calledDirectly.Add(file.Symbols[reloc.Index]);
                }
            }
        }

        var weakStubs = new List<Sym>();
        foreach (Sym sym in _globalOrder)
        {
            if (sym.IsDefined || !sym.Live)
                continue;
            if (sym.AllRefsWeak && !sym.Forced)
            {
                if (sym.Kind == SymbolType.Function && sym.RefFile is not null && calledDirectly.Contains(sym))
                    weakStubs.Add(sym);
                continue;
            }
            if (sym.RefFile is not ObjFile refFile)
                continue;

            switch (sym.Kind)
            {
                case SymbolType.Function:
                {
                    Import import = refFile.FuncImportList[sym.RefIndex];
                    FuncType type = refFile.Module.Types[((ImportDescriptor.Func)import.Descriptor).TypeIndex];
                    imports.Add(new Import(import.ModuleName, import.Name, new ImportDescriptor.Func(TypeIndexOf(type))));
                    sym.OutIndex = funcImportCount++;
                    sym.HasOutIndex = true;
                    functionNames.Add(sym.Name);
                    break;
                }
                case SymbolType.Global:
                {
                    Import import = refFile.GlobalImportList[sym.RefIndex];
                    imports.Add(import);
                    sym.OutIndex = globalImportCount++;
                    sym.HasOutIndex = true;
                    globalNames.Add(sym.Name);
                    break;
                }
                case SymbolType.Table:
                {
                    imports.Add(refFile.TableImportList[sym.RefIndex]);
                    sym.OutIndex = tableImportCount++;
                    sym.HasOutIndex = true;
                    break;
                }
                case SymbolType.Tag:
                {
                    Import import = refFile.TagImportList[sym.RefIndex];
                    FuncType type = refFile.Module.Types[((ImportDescriptor.Tag)import.Descriptor).TypeIndex];
                    imports.Add(new Import(import.ModuleName, import.Name, new ImportDescriptor.Tag(TypeIndexOf(type))));
                    sym.OutIndex = tagImportCount++;
                    sym.HasOutIndex = true;
                    break;
                }
            }
        }

        // ── Data layout (needed by __wasm_init_memory and every memory relocation) ──
        List<OutSegment> segments = LayoutData(out MemoryLayout layout);
        bool zeroBssAtStart = _options.ImportMemory && segments.Any(static s => s.IsBss && s.Size > 0) && HasFeature("bulk-memory");

        // ── Function index space ──
        uint nextFunc = funcImportCount;
        int? callCtorsSlot = null, initMemorySlot = null;
        if (_callCtors is { Live: true })
        {
            _callCtors.OutIndex = nextFunc++;
            _callCtors.HasOutIndex = true;
            callCtorsSlot = functions.Count;
            functions.Add(null!);
            functionNames.Add(CallCtorsName);
        }
        if (zeroBssAtStart)
        {
            initMemorySlot = functions.Count;
            functions.Add(null!);
            functionNames.Add("__wasm_init_memory");
            nextFunc++;
        }

        foreach (ObjFile file in _files)
        {
            file.FuncOut = new int[file.Module.Functions.Count];
            for (int f = 0; f < file.FuncOut.Length; f++)
                file.FuncOut[f] = file.FuncLive[f] ? (int)nextFunc++ : -1;
        }
        var stubSlots = new List<(Sym Sym, FuncType Type)>();
        foreach (Sym stub in weakStubs)
        {
            Import import = stub.RefFile!.FuncImportList[stub.RefIndex];
            stub.OutIndex = nextFunc++;
            stub.HasOutIndex = true;
            stubSlots.Add((stub, stub.RefFile.Module.Types[((ImportDescriptor.Func)import.Descriptor).TypeIndex]));
        }

        if (callCtorsSlot is not null || initMemorySlot is not null)
            TypeIndexOf(s_voidType);
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] >= 0)
                    TypeIndexOf(file.Module.Types[file.Module.Functions[f].TypeIndex]);
            }
        }
        foreach ((_, FuncType stubType) in stubSlots)
            TypeIndexOf(stubType);

        // ── Globals ──
        uint nextGlobal = globalImportCount;
        if (_stackPointer!.Synthetic == Synthetic.StackPointer && _stackPointer.Live)
        {
            _stackPointer.OutIndex = nextGlobal++;
            _stackPointer.HasOutIndex = true;
            globals.Add(new WasmGlobal { Type = new GlobalType(ValType.I32, Mutability.Var), InitExpr = ConstI32(layout.StackPointer) });
            globalNames.Add(StackPointerName);
        }
        foreach (ObjFile file in _files)
        {
            file.GlobalOut = new int[file.Module.Globals.Count];
            for (int g = 0; g < file.GlobalOut.Length; g++)
            {
                if (!file.GlobalLive[g])
                {
                    file.GlobalOut[g] = -1;
                    continue;
                }
                file.GlobalOut[g] = (int)nextGlobal++;
                WasmGlobal global = file.Module.Globals[g];
                globals.Add(new WasmGlobal { Type = global.Type, InitExpr = [.. global.InitExpr] });
                globalNames.Add(file.Symbols.FirstOrDefault(s => s.Kind == SymbolType.Global && s.DefFile == file && s.DefIndex == file.GlobalImports + (uint)g)?.Name);
            }
        }

        // ── Tables ──
        uint nextTable = tableImportCount;
        if (tableLive && !_options.ImportTable)
        {
            _indirectTable.OutIndex = nextTable++;
            _indirectTable.HasOutIndex = true;
            tables.Add(null!);
        }
        foreach (ObjFile file in _files)
        {
            file.TableOut = new int[file.Module.Tables.Count];
            for (int t = 0; t < file.TableOut.Length; t++)
            {
                if (!file.TableLive[t])
                {
                    file.TableOut[t] = -1;
                    continue;
                }
                file.TableOut[t] = (int)nextTable++;
                tables.Add(file.Module.Tables[t]);
            }
        }

        // ── Tags ──
        uint nextTag = tagImportCount;
        foreach (ObjFile file in _files)
        {
            file.TagOut = new int[file.Module.Tags.Count];
            for (int t = 0; t < file.TagOut.Length; t++)
            {
                if (!file.TagLive[t])
                {
                    file.TagOut[t] = -1;
                    continue;
                }
                file.TagOut[t] = (int)nextTag++;
                tags.Add(new Tag(TypeIndexOf(file.Module.Types[file.Module.Tags[t].TypeIndex])));
            }
        }

        // Resolve defined symbols' output indices and addresses.
        foreach (ObjFile file in _files)
        {
            foreach (Sym sym in file.Symbols)
            {
                if (sym.DefFile != file || sym.HasOutIndex)
                    continue;
                uint index = sym.Kind == SymbolType.Section ? 0 : sym.DefIndex;
                switch (sym.Kind)
                {
                    case SymbolType.Function when file.FuncOut[index - file.FuncImports] >= 0:
                        sym.OutIndex = (uint)file.FuncOut[index - file.FuncImports];
                        sym.HasOutIndex = true;
                        break;
                    case SymbolType.Global when file.GlobalOut[index - file.GlobalImports] >= 0:
                        sym.OutIndex = (uint)file.GlobalOut[index - file.GlobalImports];
                        sym.HasOutIndex = true;
                        break;
                    case SymbolType.Tag when file.TagOut[index - file.TagImports] >= 0:
                        sym.OutIndex = (uint)file.TagOut[index - file.TagImports];
                        sym.HasOutIndex = true;
                        break;
                    case SymbolType.Table when file.TableOut[index - file.TableImports] >= 0:
                        sym.OutIndex = (uint)file.TableOut[index - file.TableImports];
                        sym.HasOutIndex = true;
                        break;
                    case SymbolType.Data when file.SegOut[index] is not null:
                        sym.Address = file.SegAddress[index] + (sym.Def!.Offset ?? 0);
                        sym.HasOutIndex = true;
                        break;
                }
            }
        }
        foreach (Sym sym in _globalOrder)
        {
            if (sym.Synthetic == Synthetic.DataAddress)
            {
                sym.Address = layout.AddressOf(sym.Name);
                sym.HasOutIndex = true;
            }
        }

        // ── Indirect function table slots, in relocation order ──
        var tableSlots = new List<int>();
        uint tableBase = _options.TableBase ?? 1;
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] < 0)
                    continue;
                WasmFunction fn = file.Module.Functions[f];
                AssignTableSlots(file, RelocsInRange(file.CodeRelocs, fn.BodyCodeSectionOffset, (uint)fn.Body.Length), tableSlots, tableBase);
            }
            for (int s = 0; s < file.SegLive.Length; s++)
            {
                if (file.SegOut[s] is null)
                    continue;
                Data data = file.Module.DataSegments[s];
                AssignTableSlots(file, RelocsInRange(file.DataRelocs, data.SectionPayloadOffset, (uint)data.InitData.Length), tableSlots, tableBase);
            }
        }
        if (tableSlots.Count > 0 && !tableLive)
            throw new LinkerException("Internal error: address-taken functions without an indirect function table.");

        // ── Function bodies ──
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] < 0)
                    continue;
                WasmFunction source = file.Module.Functions[f];
                byte[] body = [.. source.Body];
                foreach (Relocation reloc in RelocsInRange(file.CodeRelocs, source.BodyCodeSectionOffset, (uint)source.Body.Length))
                    Apply(file, reloc, body, checked((int)(reloc.Offset - source.BodyCodeSectionOffset)), site: null);

                if (source.ExactLocals.Any(static l => l is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType }))
                    throw new LinkerException($"{file.Name}: function locals of defined reference types are not supported.");
                var function = new WasmFunction
                {
                    TypeIndex = TypeIndexOf(file.Module.Types[source.TypeIndex]),
                    Locals = [.. source.Locals],
                    ExactLocals = [.. source.ExactLocals],
                    Body = body,
                };
                function.CopyBranchHintsFrom(source);
                functions.Add(function);
                functionNames.Add(FunctionName(file, file.FuncImports + (uint)f));
            }
        }
        foreach ((Sym stub, FuncType type) in stubSlots)
        {
            functions.Add(new WasmFunction { TypeIndex = TypeIndexOf(type), Locals = [], Body = [0x00, 0x0B] });
            functionNames.Add("undefined_weak:" + stub.Name);
        }
        if (callCtorsSlot is int ctorsSlot)
            functions[ctorsSlot] = BuildCallCtors();
        if (initMemorySlot is int initSlot)
            functions[initSlot] = BuildInitMemory(segments);

        // ── Data segment bytes ──
        foreach (OutSegment segment in segments)
        {
            if (segment.IsBss && !(_options.ImportMemory && !zeroBssAtStart))
                continue;
            segment.Bytes = new byte[segment.Size];
            foreach ((ObjFile file, int s) in segment.Members)
            {
                Data data = file.Module.DataSegments[s];
                int start = checked((int)file.SegOffsetInOut[s]);
                data.InitData.CopyTo(segment.Bytes, start);
                foreach (Relocation reloc in RelocsInRange(file.DataRelocs, data.SectionPayloadOffset, (uint)data.InitData.Length))
                {
                    int at = start + checked((int)(reloc.Offset - data.SectionPayloadOffset));
                    Apply(file, reloc, segment.Bytes, at, site: segment.Address + (uint)at);
                }
            }
        }

        // ── Memory and table definitions ──
        var memories = new List<MemoryType>();
        var memoryType = new MemoryType(new Limits(layout.InitialPages, layout.MaxPages));
        if (_options.ImportMemory)
            imports[0] = imports[0] with { Descriptor = new ImportDescriptor.Memory(memoryType) };
        else
            memories.Add(memoryType);

        if (tableLive)
        {
            ulong size = tableBase + (uint)tableSlots.Count;
            var tableType = new TableType(RefType.FuncRef, new Limits(size, _options.GrowableTable ? null : size));
            if (_options.ImportTable)
            {
                int at = imports.FindIndex(static i => i.Descriptor is ImportDescriptor.Table);
                imports[at] = imports[at] with { Descriptor = new ImportDescriptor.Table(tableType) };
            }
            else
            {
                tables[(int)_indirectTable.OutIndex - (int)tableImportCount] = tableType;
            }
        }

        var elements = new List<Element>();
        if (tableSlots.Count > 0)
        {
            elements.Add(new Element
            {
                TableIndex = (int)_indirectTable.OutIndex,
                OffsetExpr = ConstI32(tableBase),
                FunctionIndices = tableSlots,
            });
        }

        // ── Exports ──
        var exports = new OrderedDictionary<string, Export>(StringComparer.Ordinal);
        if (!_options.ImportMemory || _options.ExportMemory)
            exports[_options.ExportMemoryName] = new Export(_options.ExportMemoryName, new ExportDescriptor.Memory(0));
        if (_options.ExportTable && tableLive)
            exports[IndirectFunctionTableName] = new Export(IndirectFunctionTableName, new ExportDescriptor.Table((int)_indirectTable.OutIndex));

        var forced = new HashSet<string>(_options.Exports, StringComparer.Ordinal);
        if (_options.EntryPoint is { } entry)
            forced.Add(entry);
        foreach (string name in _options.ExportIfDefined)
            forced.Add(name);

        foreach (Sym sym in _globalOrder)
        {
            if (!sym.Live || !sym.IsDefined || !sym.HasOutIndex)
                continue;
            bool flagged = sym.Def is { } def && (def.Flags & SymbolFlags.Exported) != 0;
            bool all = _options.ExportAll && sym.Kind is not SymbolType.Section;
            if (!flagged && !all && !forced.Contains(sym.Name))
                continue;

            string exportName = sym.Kind == SymbolType.Function && sym.DefFile is ObjFile defFile
                && defFile.FunctionExportNames.TryGetValue(sym.DefIndex, out string? explicitName)
                    ? explicitName
                    : sym.Name;
            switch (sym.Kind)
            {
                case SymbolType.Function:
                    exports[exportName] = new Export(exportName, new ExportDescriptor.Func((int)sym.OutIndex));
                    break;
                case SymbolType.Global:
                    exports[exportName] = new Export(exportName, new ExportDescriptor.Global((int)sym.OutIndex));
                    break;
                case SymbolType.Tag:
                    exports[exportName] = new Export(exportName, new ExportDescriptor.Tag((int)sym.OutIndex));
                    break;
                case SymbolType.Table:
                    exports[exportName] = new Export(exportName, new ExportDescriptor.Table((int)sym.OutIndex));
                    break;
                case SymbolType.Data:
                    globals.Add(new WasmGlobal { Type = new GlobalType(ValType.I32, Mutability.Const), InitExpr = ConstI32(sym.Address) });
                    globalNames.Add(sym.Name);
                    exports[exportName] = new Export(exportName, new ExportDescriptor.Global((int)nextGlobal++));
                    break;
            }
        }

        List<OutSegment> emitted = [.. segments.Where(static s => s.Bytes.Length > 0)];
        var dataSegments = emitted
            .Select(static s => new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = ConstI32(s.Address), InitData = s.Bytes, IsReadOnly = s.Name.StartsWith(".rodata", StringComparison.Ordinal) })
            .ToList();

        // ── Custom sections ──
        var customSections = new List<CustomSection>();
        if (!_options.StripDebug)
            customSections.AddRange(BuildDebugSections(functions, funcImportCount));
        if (!_options.NoNameSection)
            customSections.Add(BuildNameSection(functionNames, globalNames, [.. emitted.Select(static s => s.Name)]));
        if (BuildTargetFeatures() is { } targetFeatures)
            customSections.Add(targetFeatures);

        if (_options.PrintGcSections)
            PrintRemoved();

        PublishMaps(inputCount);

        return new WasmModule
        {
            Types = [.. _types],
            Imports = imports,
            Functions = functions,
            Tables = tables,
            Memories = memories,
            Globals = globals,
            Tags = tags,
            Exports = exports,
            StartFunction = initMemorySlot is int init ? (int)funcImportCount + init : null,
            Elements = elements,
            DataSegments = dataSegments,
            CustomSections = customSections,
            ProducersSection = SectionMerger.MergeProducers([.. _files.Select(static f => f.Module)]),
        };
    }

    private string? FunctionName(ObjFile file, uint functionIndex)
    {
        string? local = null;
        foreach (Sym sym in file.Symbols)
        {
            if (sym.Kind != SymbolType.Function || sym.DefFile != file || sym.DefIndex != functionIndex)
                continue;
            if (!sym.IsLocal)
                return sym.Name;
            local ??= sym.Name;
        }
        return local;
    }

    private void AssignTableSlots(ObjFile file, ArraySegment<Relocation> relocs, List<int> slots, uint tableBase)
    {
        foreach (Relocation reloc in relocs)
        {
            if (!IsTableIndexRelocation(reloc.Type))
                continue;

            Sym sym = file.Symbols[reloc.Index];
            if (sym.TableSlot != 0 || !sym.HasOutIndex)
                continue;
            if (sym.Kind != SymbolType.Function)
                throw new LinkerException($"{file.Name}: table index relocation against non-function symbol '{sym.Name}'.");
            if (!sym.IsDefined && sym.AllRefsWeak && !sym.Forced)
                continue;
            sym.TableSlot = tableBase + (uint)slots.Count;
            slots.Add((int)sym.OutIndex);
        }
    }

    private static bool IsTableIndexRelocation(RelocationType type) =>
        type is RelocationType.R_WASM_TABLE_INDEX_SLEB or RelocationType.R_WASM_TABLE_INDEX_I32
            or RelocationType.R_WASM_TABLE_INDEX_SLEB64 or RelocationType.R_WASM_TABLE_INDEX_I64
            or RelocationType.R_WASM_TABLE_INDEX_REL_SLEB or RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64;

    // ── Relocation application ────────────────────────────────────────────────

    // site: linear-memory address of the patched bytes for data relocations; null in code.
    private void Apply(ObjFile file, Relocation reloc, byte[] buffer, int at, uint? site)
    {
        Sym? sym = reloc.Type == RelocationType.R_WASM_TYPE_INDEX_LEB ? null : file.Symbols[reloc.Index];
        ulong value = reloc.Type switch
        {
            RelocationType.R_WASM_TYPE_INDEX_LEB
                => (uint)TypeIndexOf(file.Module.Types[(int)reloc.Index]),

            RelocationType.R_WASM_FUNCTION_INDEX_LEB or RelocationType.R_WASM_FUNCTION_INDEX_I32
                => OutIndex(sym!, file),
            RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_GLOBAL_INDEX_I32
                => OutIndex(sym!, file),
            RelocationType.R_WASM_TAG_INDEX_LEB or RelocationType.R_WASM_TABLE_NUMBER_LEB
                => OutIndex(sym!, file),

            RelocationType.R_WASM_TABLE_INDEX_SLEB or RelocationType.R_WASM_TABLE_INDEX_I32
                or RelocationType.R_WASM_TABLE_INDEX_SLEB64 or RelocationType.R_WASM_TABLE_INDEX_I64
                => sym!.TableSlot,
            RelocationType.R_WASM_TABLE_INDEX_REL_SLEB or RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64
                => sym!.TableSlot == 0 ? 0 : sym.TableSlot - (_options.TableBase ?? 1),

            RelocationType.R_WASM_MEMORY_ADDR_LEB or RelocationType.R_WASM_MEMORY_ADDR_SLEB
                or RelocationType.R_WASM_MEMORY_ADDR_I32 or RelocationType.R_WASM_MEMORY_ADDR_LEB64
                or RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or RelocationType.R_WASM_MEMORY_ADDR_I64
                or RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64
                => DataAddress(sym!, reloc),
            RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32
                => unchecked(DataAddress(sym!, reloc) - (site ?? throw new LinkerException($"{file.Name}: R_WASM_MEMORY_ADDR_LOCREL_I32 outside a data segment."))),

            _ => throw new LinkerException($"{file.Name}: relocation type {reloc.Type} is not supported in code or data."),
        };
        Patch(buffer, at, reloc.Type, value);
    }

    private static uint OutIndex(Sym sym, ObjFile file)
    {
        if (!sym.HasOutIndex)
            throw new LinkerException($"{file.Name}: reference to '{sym.Name}', which has no definition in the output.");
        return sym.OutIndex;
    }

    private static ulong DataAddress(Sym sym, Relocation reloc)
    {
        if (sym.Kind != SymbolType.Data)
            throw new LinkerException($"Memory relocation against non-data symbol '{sym.Name}'.");
        uint address = sym.HasOutIndex ? sym.Address : 0;
        return unchecked((ulong)((long)address + reloc.Addend)) & 0xFFFF_FFFF;
    }

    internal static void Patch(byte[] buffer, int at, RelocationType type, ulong value)
    {
        switch (type)
        {
            case RelocationType.R_WASM_FUNCTION_INDEX_LEB:
            case RelocationType.R_WASM_TYPE_INDEX_LEB:
            case RelocationType.R_WASM_GLOBAL_INDEX_LEB:
            case RelocationType.R_WASM_TAG_INDEX_LEB:
            case RelocationType.R_WASM_TABLE_NUMBER_LEB:
            case RelocationType.R_WASM_MEMORY_ADDR_LEB:
                RelocationEngine.WritePaddedLeb128U32(buffer, at, checked((uint)value));
                break;
            case RelocationType.R_WASM_TABLE_INDEX_SLEB:
            case RelocationType.R_WASM_TABLE_INDEX_REL_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB:
                RelocationEngine.WritePaddedLeb128S32(buffer, at, unchecked((int)(uint)value));
                break;
            case RelocationType.R_WASM_MEMORY_ADDR_LEB64:
                WritePaddedLeb(buffer, at, value, signed: false);
                break;
            case RelocationType.R_WASM_TABLE_INDEX_SLEB64:
            case RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64:
                WritePaddedLeb(buffer, at, value, signed: true);
                break;
            case RelocationType.R_WASM_TABLE_INDEX_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_I32:
            case RelocationType.R_WASM_GLOBAL_INDEX_I32:
            case RelocationType.R_WASM_FUNCTION_INDEX_I32:
            case RelocationType.R_WASM_FUNCTION_OFFSET_I32:
            case RelocationType.R_WASM_SECTION_OFFSET_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32:
                BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(at, 4), unchecked((uint)value));
                break;
            case RelocationType.R_WASM_TABLE_INDEX_I64:
            case RelocationType.R_WASM_MEMORY_ADDR_I64:
            case RelocationType.R_WASM_FUNCTION_OFFSET_I64:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(at, 8), value);
                break;
            default:
                throw new LinkerException($"Relocation type {type} cannot be patched.");
        }
    }

    /// <summary>Writes a ten-byte padded (S)LEB128, the width the 64-bit relocation types reserve.</summary>
    private static void WritePaddedLeb(byte[] buffer, int at, ulong value, bool signed)
    {
        for (int i = 0; i < 9; i++)
        {
            buffer[at + i] = (byte)((value & 0x7F) | 0x80);
            value = signed ? (ulong)((long)value >> 7) : value >> 7;
        }
        buffer[at + 9] = (byte)(value & 0x7F);
    }

    // ── Memory layout ─────────────────────────────────────────────────────────

    private sealed class MemoryLayout
    {
        public uint StackPointer, StackLow, StackHigh, GlobalBase, DataEnd, HeapBase, InitialPages;
        public ulong? MaxPages;

        public uint AddressOf(string name) => name switch
        {
            "__data_end" => DataEnd,
            "__heap_base" => HeapBase,
            "__heap_end" => checked((uint)Math.Min((MaxPages ?? InitialPages) * PageSize, uint.MaxValue)),
            "__global_base" => GlobalBase,
            "__stack_low" => StackLow,
            "__stack_high" => StackHigh,
            "__dso_handle" => 0,
            _ => throw new LinkerException($"Unknown linker-defined symbol '{name}'."),
        };
    }

    private List<OutSegment> LayoutData(out MemoryLayout layout)
    {
        var byName = new Dictionary<string, OutSegment>(StringComparer.Ordinal);
        var ordered = new List<OutSegment>();
        foreach (ObjFile file in _files)
        {
            int count = file.Module.DataSegments.Count;
            file.SegOut = new OutSegment?[count];
            file.SegOffsetInOut = new uint[count];
            file.SegAddress = new uint[count];
            List<SegmentInfo> infos = file.Module.LinkingSection!.SegmentInfos;
            for (int s = 0; s < count; s++)
            {
                if (!file.SegLive[s])
                    continue;
                Data data = file.Module.DataSegments[s];
                if (data.IsPassive)
                    throw new LinkerException($"{file.Name}: passive data segments are not supported yet.");
                string inputName = s < infos.Count ? infos[s].Name : ".data";
                uint alignment = 1u << (int)(s < infos.Count ? infos[s].Alignment : 0);
                string outName = OutputSegmentName(inputName);
                if (!byName.TryGetValue(outName, out OutSegment? outSegment))
                {
                    outSegment = new OutSegment { Name = outName };
                    byName[outName] = outSegment;
                    ordered.Add(outSegment);
                }
                outSegment.Size = AlignUp(outSegment.Size, alignment);
                file.SegOut[s] = outSegment;
                file.SegOffsetInOut[s] = outSegment.Size;
                outSegment.Size = checked(outSegment.Size + (uint)data.InitData.Length);
                outSegment.Alignment = Math.Max(outSegment.Alignment, alignment);
                outSegment.Members.Add((file, s));
            }
        }

        List<OutSegment> segments = [.. ordered.OrderBy(static s => SegmentRank(s.Name))];

        layout = new MemoryLayout();
        bool stackFirst = _options.StackFirst ?? true;
        if (_options.StackSize % StackAlignment != 0)
            throw new LinkerException($"Stack size must be {StackAlignment}-byte aligned.");

        uint ptr = 0;
        if (stackFirst)
        {
            layout.StackLow = 0;
            ptr = layout.StackHigh = layout.StackPointer = _options.StackSize;
            if (_options.GlobalBase is uint globalBase)
            {
                if (globalBase < ptr)
                    throw new LinkerException("--global-base cannot be less than stack size when --stack-first is used.");
                ptr = globalBase;
            }
        }
        else
        {
            ptr = _options.GlobalBase ?? 1024;
        }

        layout.GlobalBase = ptr;
        foreach (OutSegment segment in segments)
        {
            ptr = AlignUp(ptr, segment.Alignment);
            segment.Address = ptr;
            ptr = checked(ptr + segment.Size);
            foreach ((ObjFile file, int s) in segment.Members)
                file.SegAddress[s] = segment.Address + file.SegOffsetInOut[s];
        }
        layout.DataEnd = ptr;

        if (!stackFirst)
        {
            layout.StackLow = AlignUp(ptr, StackAlignment);
            ptr = layout.StackHigh = layout.StackPointer = checked(layout.StackLow + _options.StackSize);
        }

        layout.HeapBase = AlignUp(ptr, StackAlignment);
        ulong neededPages = ((ulong)layout.HeapBase + PageSize - 1) / PageSize;
        if (_options.InitialMemory is long initial)
        {
            if ((ulong)initial < (ulong)layout.HeapBase)
                throw new LinkerException($"Initial memory too small, {layout.HeapBase} bytes needed.");
            neededPages = (ulong)initial / PageSize;
        }
        layout.InitialPages = checked((uint)neededPages);
        if (_options.MaxMemory is long max)
        {
            if ((ulong)max / PageSize < neededPages)
                throw new LinkerException($"Maximum memory too small, {neededPages * PageSize} bytes needed.");
            layout.MaxPages = (ulong)max / PageSize;
        }

        return segments;
    }

    private static string OutputSegmentName(string name)
    {
        foreach (string prefix in (ReadOnlySpan<string>)[".text", ".rodata", ".data", ".bss", ".tdata", ".tbss"])
        {
            if (name == prefix || name.StartsWith(prefix + ".", StringComparison.Ordinal))
                return prefix == ".tbss" ? ".tdata" : prefix;
        }
        return name;
    }

    private static int SegmentRank(string name) =>
        name.StartsWith(".tdata", StringComparison.Ordinal) ? 0
        : name.StartsWith(".rodata", StringComparison.Ordinal) ? 1
        : name.StartsWith(".data", StringComparison.Ordinal) ? 2
        : name.StartsWith(".bss", StringComparison.Ordinal) ? 4
        : 3;

    private static uint AlignUp(uint value, uint alignment) => checked((value + alignment - 1) & ~(alignment - 1));

    private bool HasFeature(string feature) =>
        _files.Any(f => f.Module.TargetFeaturesSection?.Features.Any(x => x.Prefix == '+' && x.Name == feature) == true);

    // ── Synthetic functions ───────────────────────────────────────────────────

    private WasmFunction BuildCallCtors()
    {
        var inits = new List<(uint Priority, Sym Sym)>();
        foreach (ObjFile file in _files)
        {
            foreach (InitFunction init in file.Module.LinkingSection!.InitFunctions)
                inits.Add((init.Priority, file.Symbols[init.SymbolIndex]));
        }

        using var body = new MemoryStream();
        foreach ((_, Sym sym) in inits.OrderBy(static i => i.Priority))
        {
            if (!sym.HasOutIndex)
                continue;
            body.WriteByte(0x10);
            WasmEncoder.WriteU32Leb(body, sym.OutIndex);
            FuncType type = FunctionTypeOf(sym);
            if (type.Parameters.Length != 0)
                throw new LinkerException($"Constructor '{sym.Name}' takes parameters.");
            for (int i = 0; i < type.Results.Length; i++)
                body.WriteByte(0x1A);
        }
        body.WriteByte(0x0B);
        return new WasmFunction { TypeIndex = TypeIndexOf(s_voidType), Locals = [], Body = body.ToArray() };
    }

    private FuncType FunctionTypeOf(Sym sym)
    {
        if (sym.DefFile is ObjFile file)
            return file.Module.Types[file.Module.Functions[(int)(sym.DefIndex - file.FuncImports)].TypeIndex];
        Import import = sym.RefFile!.FuncImportList[sym.RefIndex];
        return sym.RefFile.Module.Types[((ImportDescriptor.Func)import.Descriptor).TypeIndex];
    }

    private WasmFunction BuildInitMemory(List<OutSegment> segments)
    {
        using var body = new MemoryStream();
        foreach (OutSegment segment in segments)
        {
            if (!segment.IsBss || segment.Size == 0)
                continue;
            body.WriteByte(0x41);
            WasmEncoder.WriteS32Leb(body, unchecked((int)segment.Address));
            body.WriteByte(0x41);
            body.WriteByte(0x00);
            body.WriteByte(0x41);
            WasmEncoder.WriteS32Leb(body, unchecked((int)segment.Size));
            body.Write([0xFC, 0x0B, 0x00]);
        }
        body.WriteByte(0x0B);
        return new WasmFunction { TypeIndex = TypeIndexOf(s_voidType), Locals = [], Body = body.ToArray() };
    }

    private static byte[] ConstI32(uint value)
    {
        using var stream = new MemoryStream(7);
        stream.WriteByte(0x41);
        WasmEncoder.WriteS32Leb(stream, unchecked((int)value));
        stream.WriteByte(0x0B);
        return stream.ToArray();
    }

    // ── Debug, name and bookkeeping ───────────────────────────────────────────

    private List<CustomSection> BuildDebugSections(List<WasmFunction> functions, uint funcImportCount)
    {
        var names = new List<string>();
        foreach (ObjFile file in _files)
        {
            foreach (CustomSection section in file.Module.CustomSections)
            {
                if (section.Name.StartsWith(".debug_", StringComparison.Ordinal) && !names.Contains(section.Name))
                    names.Add(section.Name);
            }
        }
        if (names.Count == 0)
            return [];

        // Function code offsets as DWARF sees them: the locals vector of each body in the output code section.
        IReadOnlyList<SectionMerger.EncodedFunctionBody> encoded = SectionMerger.EncodeFunctionBodies(functions, null);
        var codeOffsets = new uint[encoded.Count];
        uint offset = LebLength((uint)encoded.Count);
        for (int i = 0; i < encoded.Count; i++)
        {
            uint length = (uint)encoded[i].Bytes.Length;
            codeOffsets[i] = offset + LebLength(length);
            offset += LebLength(length) + length;
        }

        // Each file's contribution offset within each combined section.
        var contributions = new Dictionary<(ObjFile File, string Section), uint>();
        var result = new List<CustomSection>();
        foreach (string name in names)
        {
            using var combined = new MemoryStream();
            foreach (ObjFile file in _files)
            {
                CustomSection? section = file.Module.CustomSections.FirstOrDefault(c => c.Name == name);
                if (section is null)
                    continue;
                contributions[(file, name)] = (uint)combined.Length;
                combined.Write(section.Content);
            }
            result.Add(new CustomSection(name, combined.ToArray()));
        }

        for (int n = 0; n < names.Count; n++)
        {
            string name = names[n];
            byte[] bytes = result[n].Content;
            foreach (ObjFile file in _files)
            {
                if (!contributions.TryGetValue((file, name), out uint start))
                    continue;
                (string _, Relocation[] relocs) = file.CustomRelocs.FirstOrDefault(c => c.Name == name);
                if (relocs is null)
                    continue;
                foreach (Relocation reloc in relocs)
                {
                    int at = checked((int)(start + reloc.Offset));
                    ulong? value = DebugValue(file, reloc, codeOffsets, funcImportCount, contributions);
                    Patch(bytes, at, reloc.Type, value ?? Tombstone(name, reloc.Type));
                }
            }
        }

        return result;
    }

    private ulong? DebugValue(
        ObjFile file,
        Relocation reloc,
        uint[] codeOffsets,
        uint funcImportCount,
        Dictionary<(ObjFile File, string Section), uint> contributions)
    {
        Sym sym = file.Symbols[reloc.Index];
        switch (reloc.Type)
        {
            case RelocationType.R_WASM_FUNCTION_OFFSET_I32:
            case RelocationType.R_WASM_FUNCTION_OFFSET_I64:
                if (!sym.HasOutIndex || sym.OutIndex < funcImportCount || sym.DefFile is null)
                    return null;
                return (ulong)((long)codeOffsets[sym.OutIndex - funcImportCount] + reloc.Addend);
            case RelocationType.R_WASM_SECTION_OFFSET_I32:
                if (sym.Def?.SectionIndex is not uint sectionIndex)
                    return null;
                // A section symbol names the target section of a reloc..debug_* section or the section itself.
                string? sectionName = FindSectionName(file, sectionIndex);
                if (sectionName is null || !contributions.TryGetValue((file, sectionName), out uint start))
                    return null;
                return (ulong)((long)start + reloc.Addend);
            case RelocationType.R_WASM_MEMORY_ADDR_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_I64:
                return sym.HasOutIndex && sym.Kind == SymbolType.Data ? DataAddress(sym, reloc) : null;
            case RelocationType.R_WASM_FUNCTION_INDEX_I32:
            case RelocationType.R_WASM_FUNCTION_INDEX_LEB:
            case RelocationType.R_WASM_GLOBAL_INDEX_I32:
            case RelocationType.R_WASM_GLOBAL_INDEX_LEB:
                return sym.HasOutIndex ? sym.OutIndex : null;
            default:
                return null;
        }
    }

    private static string? FindSectionName(ObjFile file, uint sectionIndex) =>
        file.Module.CustomSections.FirstOrDefault(c => c.SectionIndex == (int)sectionIndex)?.Name;

    /// <summary>The value wasm-ld writes for a debug reference to discarded code or data.</summary>
    private static ulong Tombstone(string section, RelocationType type)
    {
        bool wide = type is RelocationType.R_WASM_FUNCTION_OFFSET_I64 or RelocationType.R_WASM_MEMORY_ADDR_I64;
        ulong value = section is ".debug_ranges" or ".debug_loc" ? unchecked((ulong)-2L) : unchecked((ulong)-1L);
        return wide ? value : value & 0xFFFF_FFFF;
    }

    /// <summary>The union of the inputs' used (<c>+</c>) features, as wasm-ld records it.</summary>
    private CustomSection? BuildTargetFeatures()
    {
        var used = new SortedSet<string>(StringComparer.Ordinal);
        foreach (ObjFile file in _files)
        {
            foreach ((char prefix, string name) in file.Module.TargetFeaturesSection?.Features ?? [])
            {
                if (prefix == '+')
                    used.Add(name);
            }
        }
        if (used.Count == 0)
            return null;

        foreach (ObjFile file in _files)
        {
            foreach ((char prefix, string name) in file.Module.TargetFeaturesSection?.Features ?? [])
            {
                if (prefix == '-' && used.Contains(name))
                    throw new LinkerException($"{file.Name}: feature '{name}' is disallowed by this object but used by another input.");
            }
        }

        using var content = new MemoryStream();
        WasmEncoder.WriteU32Leb(content, (uint)used.Count);
        foreach (string name in used)
        {
            content.WriteByte((byte)'+');
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
            WasmEncoder.WriteU32Leb(content, (uint)utf8.Length);
            content.Write(utf8);
        }
        return new CustomSection("target_features", content.ToArray());
    }

    private static uint LebLength(uint value)
    {
        uint length = 1;
        while ((value >>= 7) != 0)
            length++;
        return length;
    }

    private static CustomSection BuildNameSection(List<string?> functionNames, List<string?> globalNames, List<string> segmentNames)
    {
        using var content = new MemoryStream();
        WriteNameMap(content, 1, functionNames);
        WriteNameMap(content, 7, globalNames);
        WriteNameMap(content, 9, [.. segmentNames]);
        return new CustomSection("name", content.ToArray());
    }

    private static void WriteNameMap(Stream output, byte subsection, List<string?> names)
    {
        int count = names.Count(static n => n is not null);
        if (count == 0)
            return;
        using var body = new MemoryStream();
        WasmEncoder.WriteU32Leb(body, (uint)count);
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] is not string name)
                continue;
            WasmEncoder.WriteU32Leb(body, (uint)i);
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
            WasmEncoder.WriteU32Leb(body, (uint)utf8.Length);
            body.Write(utf8);
        }
        output.WriteByte(subsection);
        WasmEncoder.WriteU32Leb(output, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(output);
    }

    private void PrintRemoved()
    {
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncLive.Length; f++)
            {
                if (!file.FuncLive[f])
                    Console.Error.WriteLine($"removing unused section {file.Name}:(function {FunctionName(file, file.FuncImports + (uint)f) ?? f.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
            }
            List<SegmentInfo> infos = file.Module.LinkingSection!.SegmentInfos;
            for (int s = 0; s < file.SegLive.Length; s++)
            {
                if (!file.SegLive[s])
                    Console.Error.WriteLine($"removing unused section {file.Name}:({(s < infos.Count ? infos[s].Name : s.ToString(System.Globalization.CultureInfo.InvariantCulture))})");
            }
        }
    }

    private void PublishMaps(int inputCount)
    {
        IndexMaps = new IndexMap[inputCount];
        for (int i = 0; i < inputCount; i++)
            IndexMaps[i] = new IndexMap();

        foreach (ObjFile file in _files)
        {
            IndexMap map = IndexMaps[file.InputIndex];
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] >= 0)
                    map.Functions[file.FuncImports + (uint)f] = (uint)file.FuncOut[f];
            }
            for (int g = 0; g < file.GlobalOut.Length; g++)
            {
                if (file.GlobalOut[g] >= 0)
                    map.Globals[file.GlobalImports + (uint)g] = (uint)file.GlobalOut[g];
            }
            for (int s = 0; s < file.SegAddress.Length; s++)
            {
                if (file.SegOut[s] is not null)
                    map.DataSegmentOffsets[(uint)s] = file.SegAddress[s];
            }
            foreach ((WasmSymbol raw, Sym sym) in file.Module.LinkingSection!.Symbols.Zip(file.Symbols))
            {
                if ((raw.Flags & SymbolFlags.Undefined) == 0 || !sym.HasOutIndex || raw.Index is not int index)
                    continue;
                if (raw.Type == SymbolType.Function)
                    map.Functions[(uint)index] = sym.OutIndex;
                else if (raw.Type == SymbolType.Global)
                    map.Globals[(uint)index] = sym.OutIndex;
            }
        }

        foreach (Sym sym in _globalOrder)
        {
            SymbolTable.Symbols[sym.Name] = new ResolvedSymbol
            {
                Name = sym.Name,
                Kind = sym.Kind,
                Binding = sym.IsWeakDef ? SymbolBinding.Weak : SymbolBinding.Strong,
                Flags = sym.Def?.Flags ?? SymbolFlags.Undefined,
                DefiningModuleIndex = sym.DefFile?.InputIndex,
                LocalIndex = sym.Def is null ? null : sym.DefIndex,
                MergedIndex = sym.HasOutIndex ? (sym.Kind == SymbolType.Data ? sym.Address : sym.OutIndex) : null,
            };
            if (!sym.IsDefined && sym.Referenced)
                SymbolTable.UnresolvedSymbols.Add(sym.Name);
        }
    }
}
