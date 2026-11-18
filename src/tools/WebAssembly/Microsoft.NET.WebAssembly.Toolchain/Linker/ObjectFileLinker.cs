// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
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
/// Not yet implemented: shared memory (and with it multithreaded TLS) and position-independent output. Each is rejected with
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

    // Name-section names of synthetic functions and globals (never mutated).
    private static readonly byte[] s_callCtorsUtf8 = "__wasm_call_ctors"u8.ToArray();
    private static readonly byte[] s_initMemoryUtf8 = "__wasm_init_memory"u8.ToArray();
    private static readonly byte[] s_stackPointerUtf8 = "__stack_pointer"u8.ToArray();
    private static readonly byte[] s_mainUtf8 = "main"u8.ToArray();
    private const string MemoryBaseName = "__memory_base";
    private const string TableBaseName = "__table_base";
    private const string TlsBaseName = "__tls_base";

    private readonly LinkerOptions _options;
    private readonly List<ObjFile> _files = [];
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
        MemoryBase,
        TableBase,
        TlsBase,
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

        // An undefined symbol with an explicit import module/name (WASM_SYM_EXPLICIT_NAME) is always imported.
        public bool ExplicitImport;

        // Index of the internal global that replaces this symbol's GOT.mem/GOT.func import, when PIC code
        // addresses it through the GOT.
        public uint GotIndex;
        public bool HasGot;

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

        // For a segment merged into a string table: each piece's offset in the segment and in the merged chunk.
        public MergedChunk?[] SegMerged = [];
        public uint[][] PieceInput = [];
        public uint[][] PieceOutput = [];

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

        // Layout order: a member, or a merged chunk standing where its first member was.
        public readonly List<object> Parts = [];
        public byte[] Bytes = [];
    }

    /// <summary>
    /// wasm-ld's SyntheticMergedChunk: the deduplicated, tail-merged strings of every mergeable member (STRINGS,
    /// byte-aligned) of one output segment.
    /// </summary>
    private sealed class MergedChunk
    {
        public required SegmentFlags Flags { get; init; }
        public readonly List<(ObjFile File, int Segment)> Members = [];
        public byte[] Bytes = [];
        public uint Offset;
    }

    // ── Driver ────────────────────────────────────────────────────────────────

    private WasmModule Run(IReadOnlyList<LinkerInput> inputs)
    {
        if (_options.SharedMemory)
            throw new LinkerException("--shared-memory is not supported yet when linking relocatable objects.");
        if (_options.CompressRelocations && !_options.StripDebug && !_options.StripAll)
            throw new LinkerException("--compress-relocations is incompatible with output debug information. Pass --strip-debug or --strip-all.");

        var all = new List<ObjFile>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            LinkerInput input = inputs[i];
            if (input.Module.LinkingSection is not { Version: 2 })
                throw new LinkerException($"Input '{input.Name ?? $"#{i}"}' is not a relocatable object (no version 2 'linking' section).");
            all.Add(new ObjFile { InputIndex = i, Name = input.Name ?? $"input #{i}", Module = input.Module });
        }

        // As in wasm-ld, an archive member is extracted the moment a strong undefined reference meets its lazy
        // symbol, recursively, so output order is a depth-first walk of the extraction graph.
        for (int i = 0; i < all.Count; i++)
        {
            if (inputs[i].IsArchiveMember)
                AddLazy(all[i]);
            else
                Load(all[i]);
        }

        // Command-line roots are resolved after every input has been added, and can extract members too.
        if (_options.EntryPoint is { } entry)
            AddRootReference(entry);
        foreach (string name in _options.Exports)
            AddRootReference(name);

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
        if (!sym.IsDefined && sym.LazyFile is { Loaded: false } lazy)
            Load(lazy);
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
                if (existing.Referenced && !existing.AllRefsWeak)
                {
                    Load(file);
                    return;
                }
            }
            else
            {
                Sym sym = GetOrCreateGlobal(raw.Name, raw.Type);
                sym.LazyFile = file;
            }
        }
    }

    private void Load(ObjFile file)
    {
        if (file.Loaded)
            return;
        file.Loaded = true;

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

        // Appended after its symbols are processed, so members it extracts come first (post-order, as wasm-ld).
        _files.Add(file);
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
            if (undefined && (raw.Flags & SymbolFlags.ExplicitName) != 0 && raw.Type is SymbolType.Function or SymbolType.Global or SymbolType.Tag)
                sym.ExplicitImport = true;
            // A COMDAT-discarded definition becomes an undefined reference with its own binding, as in wasm-ld;
            // being weak (inline functions are), it extracts no archive member.
            if (!weak)
                sym.AllRefsWeak = false;
            if (!sym.IsDefined && sym.LazyFile is { Loaded: false } lazy && !sym.AllRefsWeak)
                Load(lazy);
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
        ["__data_end", "__heap_base", "__heap_end", "__global_base", "__stack_low", "__stack_high", "__dso_handle", "__wasm_first_page_end"];

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

        // PIC objects (wasi-libc is built -fPIC) address memory and the table relative to these globals. In a
        // non-PIC link they are constants: memory starts at 0 and the table at --table-base.
        foreach ((string name, Synthetic kind) in (ReadOnlySpan<(string, Synthetic)>)[(MemoryBaseName, Synthetic.MemoryBase), (TableBaseName, Synthetic.TableBase)])
        {
            if (_globals.TryGetValue(name, out Sym? sym) && !sym.IsDefined)
                sym.Synthetic = sym.Kind == SymbolType.Data ? Synthetic.DataAddress : kind;
        }

        // Thread-local accesses read __tls_base. Without shared memory wasm-ld lowers TLS to ordinary data and
        // defines __tls_base as an immutable global holding the address of .tdata (LLD 23; see MemoryLayout.TlsBase).
        if (_globals.TryGetValue(TlsBaseName, out Sym? tlsBase) && !tlsBase.IsDefined && tlsBase.Kind == SymbolType.Global)
            tlsBase.Synthetic = Synthetic.TlsBase;
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
        if (sym.ExplicitImport)
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
            if (IsTableIndexRelocation(reloc.Type) || IsGotRelocation(file, reloc) && file.Symbols[reloc.Index].Kind == SymbolType.Function)
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
        // Name-section names as wasm-ld writes them: UTF-8 bytes (a demangled name can hold bytes that are not UTF-8).
        var functionNames = new List<byte[]?>();
        var globals = new List<WasmGlobal>();
        var globalNames = new List<byte[]?>();
        var tables = new List<TableType>();
        var tags = new List<Tag>();

        bool tableLive = _indirectTable!.Live;

        // Type order follows wasm-ld: signatures named by type relocations, then imported functions, then
        // defined functions in output order.
        // Like wasm-ld's relocation scan, types are registered in first-use order.
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncLive.Length; f++)
            {
                if (!file.FuncLive[f])
                    continue;
                WasmFunction fn = file.Module.Functions[f];
                foreach (Relocation reloc in RelocsInRange(file.CodeRelocs, fn.BodyCodeSectionOffset, (uint)fn.Body.Length))
                {
                    if (reloc.Type == RelocationType.R_WASM_TYPE_INDEX_LEB)
                        TypeIndexOf(file.Module.Types[(int)reloc.Index]);
                }
            }
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
            if (sym.AllRefsWeak && !sym.Forced && !sym.ExplicitImport)
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
                    functionNames.Add(DisplayName(sym.Name));
                    break;
                }
                case SymbolType.Global:
                {
                    Import import = refFile.GlobalImportList[sym.RefIndex];
                    imports.Add(import);
                    sym.OutIndex = globalImportCount++;
                    sym.HasOutIndex = true;
                    globalNames.Add(DisplayName(sym.Name));
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
            functionNames.Add(s_callCtorsUtf8);
        }

        // Synthetic functions come first in wasm-ld's creation order: __wasm_call_ctors, undefined-weak stubs,
        // then __wasm_init_memory.
        var stubSlots = new List<(Sym Sym, FuncType Type, int Slot)>();
        foreach (Sym stub in weakStubs)
        {
            Import import = stub.RefFile!.FuncImportList[stub.RefIndex];
            stub.OutIndex = nextFunc++;
            stub.HasOutIndex = true;
            stubSlots.Add((stub, stub.RefFile.Module.Types[((ImportDescriptor.Func)import.Descriptor).TypeIndex], functions.Count));
            functions.Add(null!);
            functionNames.Add(Concat("undefined_weak:"u8, DisplayName(stub.Name)));
        }
        if (zeroBssAtStart)
        {
            initMemorySlot = functions.Count;
            functions.Add(null!);
            functionNames.Add(s_initMemoryUtf8);
            nextFunc++;
        }

        foreach (ObjFile file in _files)
        {
            file.FuncOut = new int[file.Module.Functions.Count];
            for (int f = 0; f < file.FuncOut.Length; f++)
                file.FuncOut[f] = file.FuncLive[f] ? (int)nextFunc++ : -1;
        }
        if (callCtorsSlot is not null)
            TypeIndexOf(s_voidType);
        foreach ((_, FuncType stubType, _) in stubSlots)
            TypeIndexOf(stubType);
        if (initMemorySlot is not null)
            TypeIndexOf(s_voidType);
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] >= 0)
                    TypeIndexOf(file.Module.Types[file.Module.Functions[f].TypeIndex]);
            }
        }

        // ── Globals ──
        uint nextGlobal = globalImportCount;
        if (_stackPointer!.Synthetic == Synthetic.StackPointer && _stackPointer.Live)
        {
            _stackPointer.OutIndex = nextGlobal++;
            _stackPointer.HasOutIndex = true;
            globals.Add(new WasmGlobal { Type = new GlobalType(ValType.I32, Mutability.Var), InitExpr = ConstI32(layout.StackPointer) });
            globalNames.Add(s_stackPointerUtf8);
        }
        // wasm-ld (LLD 23+) creates these in a fixed order after __stack_pointer.
        foreach (string baseName in (ReadOnlySpan<string>)[MemoryBaseName, TableBaseName, TlsBaseName])
        {
            if (_globals.TryGetValue(baseName, out Sym? sym) && sym.Live && sym.Synthetic is Synthetic.MemoryBase or Synthetic.TableBase or Synthetic.TlsBase)
            {
                sym.OutIndex = nextGlobal++;
                sym.HasOutIndex = true;
                uint value = sym.Synthetic switch
                {
                    Synthetic.TableBase => _options.TableBase ?? 1,
                    Synthetic.TlsBase => layout.TlsBase,
                    _ => 0,
                };
                globals.Add(new WasmGlobal { Type = new GlobalType(ValType.I32, Mutability.Const), InitExpr = ConstI32(value) });
                globalNames.Add(System.Text.Encoding.UTF8.GetBytes(sym.Name));
            }
        }
        foreach (ObjFile file in _files)
        {
            string?[] definedGlobalNames = FirstDefiningNames(file, SymbolType.Global, file.GlobalImports, file.Module.Globals.Count);
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
                globalNames.Add(DisplayName(definedGlobalNames[g]));
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
                        sym.Address = AddressIn(file, (int)index, sym.Def!.Offset ?? 0);
                        // As wasm-ld's getVA: a TLS symbol's address is its offset from __tls_base (the .tdata start).
                        if ((sym.Def.Flags & SymbolFlags.Tls) != 0)
                            sym.Address -= file.SegOut[index]!.Address;
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

        // ── GOT: PIC code loads addresses from GOT.mem/GOT.func globals; a static link makes them constants ──
        foreach (ObjFile file in _files)
        {
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] < 0)
                    continue;
                WasmFunction fn = file.Module.Functions[f];
                foreach (Relocation reloc in RelocsInRange(file.CodeRelocs, fn.BodyCodeSectionOffset, (uint)fn.Body.Length))
                {
                    if (!IsGotRelocation(file, reloc))
                        continue;
                    Sym sym = file.Symbols[reloc.Index];
                    if (sym.HasGot)
                        continue;
                    uint value = sym.Kind == SymbolType.Function ? sym.TableSlot : (uint)DataAddress(sym, new Relocation());
                    sym.GotIndex = nextGlobal++;
                    sym.HasGot = true;
                    globals.Add(new WasmGlobal { Type = new GlobalType(ValType.I32, Mutability.Const), InitExpr = ConstI32(value) });
                    globalNames.Add(Concat(sym.Kind == SymbolType.Function ? "GOT.func.internal."u8 : "GOT.data.internal."u8, DisplayName(sym.Name)));
                }
            }
        }

        // ── Function bodies ──
        foreach (ObjFile file in _files)
        {
            string?[] definedFunctionNames = FirstDefiningNames(file, SymbolType.Function, file.FuncImports, file.FuncOut.Length);
            for (int f = 0; f < file.FuncOut.Length; f++)
            {
                if (file.FuncOut[f] < 0)
                    continue;
                WasmFunction source = file.Module.Functions[f];
                byte[] body = [.. source.Body];
                ArraySegment<Relocation> bodyRelocs = RelocsInRange(file.CodeRelocs, source.BodyCodeSectionOffset, (uint)source.Body.Length);
                foreach (Relocation reloc in bodyRelocs)
                    Apply(file, reloc, body, checked((int)(reloc.Offset - source.BodyCodeSectionOffset)), site: null);
                if (_options.CompressRelocations)
                    body = CompressRelocations(body, bodyRelocs, source.BodyCodeSectionOffset);

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
                functionNames.Add(DisplayName(definedFunctionNames[f]));
            }
        }
        foreach ((_, FuncType type, int slot) in stubSlots)
            functions[slot] = new WasmFunction { TypeIndex = TypeIndexOf(type), Locals = [], Body = [0x00, 0x0B] };
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
            foreach (MergedChunk merged in segment.Parts.OfType<MergedChunk>())
                merged.Bytes.CopyTo(segment.Bytes, (int)merged.Offset);
            foreach ((ObjFile file, int s) in segment.Members)
            {
                Data data = file.Module.DataSegments[s];
                if (file.SegMerged[s] is not null)
                {
                    if (RelocsInRange(file.DataRelocs, data.SectionPayloadOffset, (uint)data.InitData.Length).Count > 0)
                        throw new LinkerException($"{file.Name}: relocations in mergeable string segment {s} are not supported.");
                    continue;
                }
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
                    // wasm-ld's name section skips these address globals (GlobalSection::dataAddressGlobals).
                    globalNames.Add(null);
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
        customSections.AddRange(BuildDebugSections(functions, funcImportCount));
        if (!_options.NoNameSection && !_options.StripAll)
        {
            var segmentNames = new List<byte[]?>(emitted.Count);
            foreach (OutSegment segment in emitted)
                segmentNames.Add(System.Text.Encoding.UTF8.GetBytes(segment.Name));
            customSections.Add(BuildNameSection(_options.ModuleName, functionNames, globalNames, segmentNames));
        }
        if (!_options.StripAll && BuildTargetFeatures() is { } targetFeatures)
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
            ProducersSection = _options.StripAll ? null : MergeProducers(),
        };
    }

    /// <summary>
    /// wasm-ld's producers section: per field (written as <c>language</c>, <c>processed-by</c>, <c>sdk</c>), the first
    /// version seen for each name across the objects in link order wins, and the linker adds no entry of its own.
    /// </summary>
    private ProducersSection? MergeProducers()
    {
        ReadOnlySpan<string> fieldOrder = ["language", "processed-by", "sdk"];
        var fields = new List<(string Name, string Version)>[fieldOrder.Length];
        for (int i = 0; i < fields.Length; i++)
            fields[i] = [];
        foreach (ObjFile file in _files)
        {
            if (file.Module.ProducersSection is not { } producers)
                continue;
            for (int i = 0; i < fieldOrder.Length; i++)
            {
                if (!producers.Fields.TryGetValue(fieldOrder[i], out var values))
                    continue;
                List<(string Name, string Version)> merged = fields[i];
                foreach (var entry in values)
                {
                    if (!ContainsName(merged, entry.Name))
                        merged.Add(entry);
                }
            }
        }

        var result = new ProducersSection();
        for (int i = 0; i < fieldOrder.Length; i++)
        {
            if (fields[i].Count > 0)
                result.Fields[fieldOrder[i]] = fields[i];
        }

        return result.Fields.Count == 0 ? null : result;

        static bool ContainsName(List<(string Name, string Version)> entries, string name)
        {
            foreach (var entry in entries)
            {
                if (entry.Name == name)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// The names of the file's defined functions or globals (by index past the imports) as LLVM's object reader gives
    /// them: the first symbol in the symbol table that defines each, whatever its binding (so a static alias declared
    /// first names a function its global alias also defines), and regardless of resolution (a discarded COMDAT copy
    /// kept by --no-gc-sections keeps its own name).
    /// </summary>
    private static string?[] FirstDefiningNames(ObjFile file, SymbolType type, uint imports, int count)
    {
        var names = new string?[count];
        var named = new bool[count];
        foreach (WasmSymbol sym in file.Module.LinkingSection!.Symbols)
        {
            if (sym.Type != type || (sym.Flags & SymbolFlags.Undefined) != 0 || sym.Index is not int symbolIndex)
                continue;
            long index = (long)symbolIndex - imports;
            if (index < 0 || index >= count || named[index])
                continue;
            named[index] = true;
            names[index] = sym.Name;
        }

        return names;
    }

    /// <summary>
    /// wasm-ld's <c>maybeDemangleSymbol</c>, as the UTF-8 it writes: <c>__main_argc_argv</c> (the mangled
    /// <c>main(argc, argv)</c>) reads as <c>main</c>, and other names are demangled unless <c>--no-demangle</c>.
    /// </summary>
    [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(name))]
    private byte[]? DisplayName(string? name) => name switch
    {
        null => null,
        "__main_argc_argv" => s_mainUtf8,
        _ => _options.Demangle ? Demangling.Demangler.DemangleToUtf8(name) : System.Text.Encoding.UTF8.GetBytes(name),
    };

    private static byte[] Concat(ReadOnlySpan<byte> prefix, byte[] name)
    {
        var result = new byte[prefix.Length + name.Length];
        prefix.CopyTo(result);
        name.CopyTo(result.AsSpan(prefix.Length));
        return result;
    }

    private readonly Dictionary<uint, uint> _tableSlotByFunction = [];

    private void AssignTableSlots(ObjFile file, ArraySegment<Relocation> relocs, List<int> slots, uint tableBase)
    {
        foreach (Relocation reloc in relocs)
        {
            if (!IsTableIndexRelocation(reloc.Type) && !(IsGotRelocation(file, reloc) && file.Symbols[reloc.Index].Kind == SymbolType.Function))
                continue;

            Sym sym = file.Symbols[reloc.Index];
            if (sym.TableSlot != 0 || !sym.HasOutIndex)
                continue;
            if (sym.Kind != SymbolType.Function)
                throw new LinkerException($"{file.Name}: table index relocation against non-function symbol '{sym.Name}'.");
            if (!sym.IsDefined && sym.AllRefsWeak && !sym.Forced && !sym.ExplicitImport)
                continue;

            // Slots belong to functions, not symbols: aliases (a C++ D1/D2 destructor pair) share one entry.
            if (!_tableSlotByFunction.TryGetValue(sym.OutIndex, out uint slot))
            {
                slot = tableBase + (uint)slots.Count;
                _tableSlotByFunction[sym.OutIndex] = slot;
                slots.Add((int)sym.OutIndex);
            }
            sym.TableSlot = slot;
        }
    }

    /// <summary>A global-index relocation against a function or data symbol addresses it through the GOT.</summary>
    private static bool IsGotRelocation(ObjFile file, Relocation reloc) =>
        reloc.Type is RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_GLOBAL_INDEX_I32 &&
        file.Symbols[reloc.Index].Kind is SymbolType.Function or SymbolType.Data;

    /// <summary>
    /// Re-encodes every relocated LEB128 operand at its minimal width, as <c>wasm-ld --compress-relocations</c>
    /// does. Fixed-width (I32/I64) relocations are copied unchanged.
    /// </summary>
    private static byte[] CompressRelocations(byte[] body, ArraySegment<Relocation> relocs, uint bodyStart)
    {
        using var output = new MemoryStream(body.Length);
        int copied = 0;
        foreach (Relocation reloc in relocs)
        {
            (int width, bool signed) = reloc.Type switch
            {
                RelocationType.R_WASM_FUNCTION_INDEX_LEB or RelocationType.R_WASM_TYPE_INDEX_LEB
                    or RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_TAG_INDEX_LEB
                    or RelocationType.R_WASM_TABLE_NUMBER_LEB or RelocationType.R_WASM_MEMORY_ADDR_LEB => (5, false),
                RelocationType.R_WASM_TABLE_INDEX_SLEB or RelocationType.R_WASM_TABLE_INDEX_REL_SLEB
                    or RelocationType.R_WASM_MEMORY_ADDR_SLEB or RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB
                    or RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB => (5, true),
                RelocationType.R_WASM_MEMORY_ADDR_LEB64 => (10, false),
                RelocationType.R_WASM_TABLE_INDEX_SLEB64 or RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64
                    or RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64
                    or RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64 => (10, true),
                _ => (0, false),
            };
            if (width == 0)
                continue;

            int at = checked((int)(reloc.Offset - bodyStart));
            output.Write(body, copied, at - copied);
            ulong value = 0;
            for (int i = 0; i < width; i++)
                value |= (ulong)(body[at + i] & 0x7F) << (7 * i);
            if (signed)
            {
                int bits = 7 * width;
                long signedValue = bits < 64 ? (long)(value << (64 - bits)) >> (64 - bits) : (long)value;
                WasmLeb128.WriteSLeb128(output, signedValue);
            }
            else
            {
                WriteU64Leb(output, value);
            }
            copied = at + width;
        }
        output.Write(body, copied, body.Length - copied);
        return output.ToArray();

        static void WriteU64Leb(Stream stream, ulong value)
        {
            do
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                stream.WriteByte(value == 0 ? b : (byte)(b | 0x80));
            }
            while (value != 0);
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
            RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_GLOBAL_INDEX_I32 when IsGotRelocation(file, reloc)
                => sym!.HasGot ? sym.GotIndex : throw new LinkerException($"{file.Name}: no GOT entry for '{sym.Name}'."),
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
            RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB or RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64
                => sym!.HasOutIndex && sym.Kind == SymbolType.Data
                    ? DataAddress(sym, reloc)
                    : throw new LinkerException($"{file.Name}: relocation {reloc.Type} cannot be used against an undefined symbol '{sym.Name}'."),
            RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32
                => unchecked(DataAddress(sym!, reloc) - (site ?? throw new LinkerException($"{file.Name}: R_WASM_MEMORY_ADDR_LOCREL_I32 outside a data segment."))) & 0xFFFF_FFFF,
            RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64
                => unchecked(DataAddress(sym!, reloc) - (site ?? throw new LinkerException($"{file.Name}: R_WASM_MEMORY_ADDR_LOCREL_I64 outside a data segment."))),

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
            case RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB:
                RelocationEngine.WritePaddedLeb128S32(buffer, at, unchecked((int)(uint)value));
                break;
            case RelocationType.R_WASM_MEMORY_ADDR_LEB64:
                WasmLeb128.WritePaddedULeb128(buffer.AsSpan(at, WasmLeb128.PaddedLength64), value);
                break;
            case RelocationType.R_WASM_TABLE_INDEX_SLEB64:
            case RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64:
                WasmLeb128.WritePaddedSLeb128(buffer.AsSpan(at, WasmLeb128.PaddedLength64), unchecked((long)value));
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
            case RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64:
                BinaryPrimitives.WriteUInt64LittleEndian(buffer.AsSpan(at, 8), value);
                break;
            default:
                throw new LinkerException($"Relocation type {type} cannot be patched.");
        }
    }

    // ── Memory layout ─────────────────────────────────────────────────────────

    private sealed class MemoryLayout
    {
        public uint StackPointer, StackLow, StackHigh, GlobalBase, DataEnd, HeapBase, InitialPages, TableBase;

        /// <summary>
        /// The address of .tdata, or the start of data when there is no TLS data (LLD 23; LLD 22 and earlier used 0).
        /// </summary>
        public uint TlsBase;
        public ulong? MaxPages;

        public uint AddressOf(string name) => name switch
        {
            "__data_end" => DataEnd,
            "__heap_base" => HeapBase,
            // wasm-ld: the end of the initial memory, regardless of --max-memory (#577).
            "__heap_end" => unchecked((uint)((ulong)InitialPages * PageSize)),
            "__global_base" => GlobalBase,
            "__stack_low" => StackLow,
            "__stack_high" => StackHigh,
            "__dso_handle" => GlobalBase, // wasm-ld: the start of data
            "__wasm_first_page_end" => PageSize, // wasi-libc's sbrk and getpagesize use it as the page size
            "__memory_base" => 0,
            "__table_base" => TableBase,
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
            file.SegMerged = new MergedChunk?[count];
            file.PieceInput = new uint[count][];
            file.PieceOutput = new uint[count][];
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
                file.SegOut[s] = outSegment;
                outSegment.Alignment = Math.Max(outSegment.Alignment, alignment);
                outSegment.Members.Add((file, s));

                SegmentFlags flags = s < infos.Count ? infos[s].Flags : SegmentFlags.None;
                if ((flags & SegmentFlags.Strings) != 0 && alignment == 1 && data.InitData.Length > 0)
                {
                    MergedChunk? merged = outSegment.Parts.OfType<MergedChunk>().FirstOrDefault(m => m.Flags == flags);
                    if (merged is null)
                    {
                        merged = new MergedChunk { Flags = flags };
                        outSegment.Parts.Add(merged);
                    }
                    merged.Members.Add((file, s));
                    file.SegMerged[s] = merged;
                }
                else
                {
                    outSegment.Parts.Add((file, s));
                }
            }
        }

        foreach (OutSegment outSegment in ordered)
        {
            foreach (MergedChunk merged in outSegment.Parts.OfType<MergedChunk>())
                MergeStrings(merged);

            uint size = 0;
            foreach (object part in outSegment.Parts)
            {
                if (part is MergedChunk merged)
                {
                    merged.Offset = size;
                    foreach ((ObjFile file, int s) in merged.Members)
                        file.SegOffsetInOut[s] = size;
                    size = checked(size + (uint)merged.Bytes.Length);
                    continue;
                }
                (ObjFile f, int seg) = ((ObjFile, int))part;
                List<SegmentInfo> infos = f.Module.LinkingSection!.SegmentInfos;
                size = AlignUp(size, 1u << (int)(seg < infos.Count ? infos[seg].Alignment : 0));
                f.SegOffsetInOut[seg] = size;
                size = checked(size + (uint)f.Module.DataSegments[seg].InitData.Length);
            }
            outSegment.Size = size;
        }

        List<OutSegment> segments = [.. ordered.OrderBy(static s => SegmentRank(s.Name))];

        layout = new MemoryLayout { TableBase = _options.TableBase ?? 1 };
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

        layout.GlobalBase = layout.TlsBase = ptr;
        foreach (OutSegment segment in segments)
        {
            ptr = AlignUp(ptr, segment.Alignment);
            segment.Address = ptr;
            if (segment.Name == ".tdata")
                layout.TlsBase = ptr;
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

    /// <summary>
    /// Splits each member into NUL-terminated pieces, deduplicates them and lays them out as LLVM's
    /// <c>StringTableBuilder::finalize()</c> does: sorted by reversed content, a string that is a suffix of the
    /// previous one sharing its tail.
    /// </summary>
    private static void MergeStrings(MergedChunk merged)
    {
        var datas = new List<(byte[] Data, string Owner)>();
        foreach ((ObjFile file, int s) in merged.Members)
            datas.Add((file.Module.DataSegments[s].InitData, $"{file.Name}: mergeable segment {s}"));
        var pieces = MergeStringPieces(datas, out merged.Bytes);
        for (int i = 0; i < merged.Members.Count; i++)
        {
            (ObjFile file, int s) = merged.Members[i];
            file.PieceInput[s] = pieces[i].Inputs;
            file.PieceOutput[s] = pieces[i].Outputs;
        }
    }

    /// <summary>
    /// The core of wasm-ld's string merging (<c>SyntheticMergedChunk</c>): returns the merged bytes and, for each
    /// input, the start of every piece and where that piece now lives.
    /// </summary>
    private static List<(uint[] Inputs, uint[] Outputs)> MergeStringPieces(List<(byte[] Data, string Owner)> inputs, out byte[] bytes)
    {
        var distinct = new Dictionary<string, uint>(StringComparer.Ordinal);
        var starts = new List<uint[]>(inputs.Count);
        foreach ((byte[] data, string owner) in inputs)
        {
            var pieceStarts = new List<uint>();
            for (int start = 0; start < data.Length;)
            {
                int end = Array.IndexOf(data, (byte)0, start);
                if (end < 0)
                    throw new LinkerException($"{owner}: string is not null terminated.");
                pieceStarts.Add((uint)start);
                distinct.TryAdd(Latin1(data.AsSpan(start, end + 1 - start)), 0);
                start = end + 1;
            }
            starts.Add([.. pieceStarts]);
        }

        // Latin-1 maps bytes to chars one-to-one, so ordinal string comparison is byte comparison.
        string[] strings = [.. distinct.Keys];
        Array.Sort(strings, static (a, b) =>
        {
            for (int k = 0; ; k++)
            {
                int ca = k < a.Length ? a[a.Length - 1 - k] : -1;
                int cb = k < b.Length ? b[b.Length - 1 - k] : -1;
                if (ca != cb)
                    return cb.CompareTo(ca);
                if (ca == -1)
                    return 0;
            }
        });

        var content = new System.Text.StringBuilder();
        string previous = "";
        foreach (string str in strings)
        {
            if (previous.EndsWith(str, StringComparison.Ordinal))
            {
                distinct[str] = (uint)(content.Length - str.Length);
                continue;
            }
            distinct[str] = (uint)content.Length;
            content.Append(str);
            previous = str;
        }
        bytes = System.Text.Encoding.Latin1.GetBytes(content.ToString());

        var result = new List<(uint[] Inputs, uint[] Outputs)>(inputs.Count);
        for (int i = 0; i < inputs.Count; i++)
        {
            byte[] data = inputs[i].Data;
            uint[] pieceStarts = starts[i];
            var outputs = new uint[pieceStarts.Length];
            for (int p = 0; p < pieceStarts.Length; p++)
            {
                int start = (int)pieceStarts[p];
                int end = Array.IndexOf(data, (byte)0, start);
                outputs[p] = distinct[Latin1(data.AsSpan(start, end + 1 - start))];
            }
            result.Add((pieceStarts, outputs));
        }

        return result;

        static string Latin1(ReadOnlySpan<byte> bytes) => System.Text.Encoding.Latin1.GetString(bytes);
    }

    /// <summary>The output offset of <paramref name="offset"/> within a merged input, through its piece map.</summary>
    private static uint MergedOffset((uint[] Inputs, uint[] Outputs) pieces, uint offset)
    {
        int index = Array.BinarySearch(pieces.Inputs, offset);
        if (index < 0)
            index = ~index - 1;
        if (index < 0)
            throw new LinkerException($"offset {offset} is outside a merged string section.");
        return pieces.Outputs[index] + (offset - pieces.Inputs[index]);
    }

    /// <summary>Linear-memory address of <paramref name="offset"/> within input data segment <paramref name="segment"/>.</summary>
    private static uint AddressIn(ObjFile file, int segment, uint offset)
    {
        if (file.SegMerged[segment] is null)
            return file.SegAddress[segment] + offset;

        uint[] inputs = file.PieceInput[segment];
        int index = Array.BinarySearch(inputs, offset);
        if (index < 0)
            index = ~index - 1;
        return file.SegAddress[segment] + file.PieceOutput[segment][index] + (offset - inputs[index]);
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

    // wasm-ld (LLD 23) orders output segments .rodata, .data, others, .tdata, .bss; LLD 22 and earlier put .tdata first.
    private static int SegmentRank(string name) =>
        name.StartsWith(".rodata", StringComparison.Ordinal) ? 0
        : name.StartsWith(".data", StringComparison.Ordinal) ? 1
        : name.StartsWith(".tdata", StringComparison.Ordinal) ? 3
        : name.StartsWith(".bss", StringComparison.Ordinal) ? 4
        : 2;

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
            body.WriteByte(WasmOpcode.Call);
            WasmLeb128.WriteULeb128(body, sym.OutIndex);
            FuncType type = FunctionTypeOf(sym);
            if (type.Parameters.Length != 0)
                throw new LinkerException($"Constructor '{sym.Name}' takes parameters.");
            for (int i = 0; i < type.Results.Length; i++)
                body.WriteByte(WasmOpcode.Drop);
        }
        body.WriteByte(WasmOpcode.End);
        return new WasmFunction { TypeIndex = TypeIndexOf(s_voidType), Locals = [], Body = body.ToArray() };
    }

    private static FuncType FunctionTypeOf(Sym sym)
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
            body.WriteByte(WasmOpcode.I32Const);
            WasmLeb128.WriteSLeb128(body, unchecked((int)segment.Address));
            body.WriteByte(WasmOpcode.I32Const);
            body.WriteByte(0x00);
            body.WriteByte(WasmOpcode.I32Const);
            WasmLeb128.WriteSLeb128(body, unchecked((int)segment.Size));
            body.Write([WasmOpcode.MiscPrefix, 0x0B, 0x00]); // memory.fill, memory 0
        }
        body.WriteByte(WasmOpcode.End);
        return new WasmFunction { TypeIndex = TypeIndexOf(s_voidType), Locals = [], Body = body.ToArray() };
    }

    private static byte[] ConstI32(uint value)
    {
        using var stream = new MemoryStream(7);
        stream.WriteByte(WasmOpcode.I32Const);
        WasmLeb128.WriteSLeb128(stream, unchecked((int)value));
        stream.WriteByte(WasmOpcode.End);
        return stream.ToArray();
    }

    // ── Debug, name and bookkeeping ───────────────────────────────────────────

    /// <summary>
    /// Custom sections wasm-ld concatenates into the output: DWARF (unless stripped) and any section it does not
    /// interpret itself, such as wit-bindgen's <c>component-type:*</c> sections that wasm-component-ld consumes.
    /// </summary>
    private bool IsPassThroughSection(string name) =>
        name.StartsWith(".debug_", StringComparison.Ordinal)
            ? !_options.StripDebug && !_options.StripAll
            : name is not ("linking" or "name" or "producers" or "target_features" or "dylink" or "dylink.0" or BranchHintMetadataReader.SectionName)
              && !name.StartsWith("reloc.", StringComparison.Ordinal);

    private List<CustomSection> BuildDebugSections(List<WasmFunction> functions, uint funcImportCount)
    {
        var names = new List<string>();
        foreach (ObjFile file in _files)
        {
            foreach (CustomSection section in file.Module.CustomSections)
            {
                if (IsPassThroughSection(section.Name) && !names.Contains(section.Name))
                    names.Add(section.Name);
            }
        }
        if (names.Count == 0)
            return [];

        // Function code offsets as DWARF sees them: the locals vector of each body in the output code section.
        IReadOnlyList<SectionMerger.EncodedFunctionBody> encoded = SectionMerger.EncodeFunctionBodies(functions, null);
        var codeOffsets = new uint[encoded.Count];
        uint offset = (uint)WasmLeb128.GetULeb128Size((uint)encoded.Count);
        for (int i = 0; i < encoded.Count; i++)
        {
            uint length = (uint)encoded[i].Bytes.Length;
            codeOffsets[i] = offset + (uint)WasmLeb128.GetULeb128Size(length);
            offset += (uint)WasmLeb128.GetULeb128Size(length) + length;
        }

        // Each file's contribution offset within each combined section. String sections are merged instead (as
        // wasm-ld's SyntheticMergedChunk), and offsets into them go through each file's piece map.
        var contributions = new Dictionary<(ObjFile File, string Section), uint>();
        var merged = new Dictionary<(ObjFile File, string Section), (uint[] Inputs, uint[] Outputs)>();
        var result = new List<CustomSection>();
        foreach (string name in names)
        {
            var members = _files
                .Select(file => (File: file, Section: file.Module.CustomSections.FirstOrDefault(c => c.Name == name)))
                .Where(m => m.Section is not null)
                .ToList();
            if (IsMergedStringSection(name))
            {
                var pieces = MergeStringPieces(members.Select(m => (m.Section!.Content, $"{m.File.Name}: section {name}")).ToList(), out byte[] mergedBytes);
                for (int i = 0; i < members.Count; i++)
                {
                    contributions[(members[i].File, name)] = 0;
                    merged[(members[i].File, name)] = pieces[i];
                }
                result.Add(new CustomSection(name, mergedBytes));
                continue;
            }

            using var combined = new MemoryStream();
            foreach (var (file, section) in members)
            {
                contributions[(file, name)] = (uint)combined.Length;
                combined.Write(section!.Content);
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
                    ulong? value = DebugValue(file, reloc, codeOffsets, funcImportCount, contributions, merged);
                    Patch(bytes, at, reloc.Type, value ?? Tombstone(name, reloc.Type));
                }
            }
        }

        return result;
    }

    private static ulong? DebugValue(
        ObjFile file,
        Relocation reloc,
        uint[] codeOffsets,
        uint funcImportCount,
        Dictionary<(ObjFile File, string Section), uint> contributions,
        Dictionary<(ObjFile File, string Section), (uint[] Inputs, uint[] Outputs)> merged)
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
                if (merged.TryGetValue((file, sectionName), out var pieces))
                    return MergedOffset(pieces, checked((uint)reloc.Addend));
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

    // wasm-ld merges these by name (lld/wasm/InputFiles.cpp shouldMerge), as long as -O is not 0.
    private static bool IsMergedStringSection(string name) => name is ".debug_str" or ".debug_str.dwo" or ".debug_line_str";

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
        WasmLeb128.WriteULeb128(content, (uint)used.Count);
        foreach (string name in used)
        {
            content.WriteByte((byte)'+');
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(name);
            WasmLeb128.WriteULeb128(content, (uint)utf8.Length);
            content.Write(utf8);
        }
        return new CustomSection("target_features", content.ToArray());
    }

    private static CustomSection BuildNameSection(string? moduleName, List<byte[]?> functionNames, List<byte[]?> globalNames, List<byte[]?> segmentNames)
    {
        using var content = new MemoryStream();
        if (moduleName is not null)
        {
            // wasm-ld always writes the module name subsection first (--soname, or else the output file's name).
            byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(moduleName);
            using var body = new MemoryStream();
            WasmLeb128.WriteULeb128(body, (uint)utf8.Length);
            body.Write(utf8);
            content.WriteByte((byte)NameSubsectionId.Module);
            WasmLeb128.WriteULeb128(content, (uint)body.Length);
            body.Position = 0;
            body.CopyTo(content);
        }

        WriteNameMap(content, NameSubsectionId.Function, functionNames);
        WriteNameMap(content, NameSubsectionId.Global, globalNames);
        WriteNameMap(content, NameSubsectionId.DataSegment, segmentNames);
        return new CustomSection("name", content.ToArray());
    }

    private static void WriteNameMap(Stream output, NameSubsectionId subsection, List<byte[]?> names)
    {
        int count = 0;
        foreach (byte[]? name in names)
        {
            if (name is not null)
                count++;
        }

        if (count == 0)
            return;
        using var body = new MemoryStream();
        WasmLeb128.WriteULeb128(body, (uint)count);
        for (int i = 0; i < names.Count; i++)
        {
            if (names[i] is not byte[] name)
                continue;
            WasmLeb128.WriteULeb128(body, (uint)i);
            WasmLeb128.WriteULeb128(body, (uint)name.Length);
            body.Write(name);
        }
        output.WriteByte((byte)subsection);
        WasmLeb128.WriteULeb128(output, (uint)body.Length);
        body.Position = 0;
        body.CopyTo(output);
    }

    private void PrintRemoved()
    {
        foreach (ObjFile file in _files)
        {
            string?[] definedFunctionNames = FirstDefiningNames(file, SymbolType.Function, file.FuncImports, file.FuncLive.Length);
            for (int f = 0; f < file.FuncLive.Length; f++)
            {
                if (!file.FuncLive[f])
                    Console.Error.WriteLine($"removing unused section {file.Name}:(function {definedFunctionNames[f] ?? f.ToString(System.Globalization.CultureInfo.InvariantCulture)})");
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
