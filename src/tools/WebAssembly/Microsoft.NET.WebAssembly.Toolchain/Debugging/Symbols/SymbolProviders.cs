// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Runtime.CompilerServices;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

/// <summary>One name, attached to a function slot of the providing module (or to an explicit target).</summary>
/// <param name="FunctionIndex">Absolute function index in the providing module (imports first).</param>
/// <param name="Name">The name and its source.</param>
/// <param name="Target">
/// Set only by providers whose names do not map to a function slot (e.g. a future cDAC provider
/// naming an interpreted method). Null means "bind <paramref name="FunctionIndex"/> through the scope".
/// </param>
internal readonly record struct SymbolEntry(int FunctionIndex, SymbolName Name, SymbolTarget? Target = null);

/// <summary>
/// One name source. Providers are independent: none may derive its names from another
/// ("deduplicate data, not judgement", #540 §2.3 rule 8). They parse lazily and are cached per module.
/// </summary>
internal interface ISymbolProvider
{
    SymbolSource Source { get; }

    /// <summary>State of this source for the module. Computing it may parse. Scope is left null.</summary>
    ProviderStatus Status { get; }

    IReadOnlyList<SymbolEntry> Entries { get; }

    /// <summary>All names this source gives function slot <paramref name="functionIndex"/>.</summary>
    IReadOnlyList<SymbolName> NamesFor(int functionIndex);

    /// <summary>Entries whose name equals <paramref name="text"/> ordinally.</summary>
    IReadOnlyList<SymbolEntry> LookupExact(string text);

    /// <summary>
    /// All names this source gives an explicit <paramref name="target"/> (entries whose
    /// <see cref="SymbolEntry.Target"/> is set, e.g. a managed method with no function slot).
    /// </summary>
    IReadOnlyList<SymbolName> NamesForTarget(SymbolTarget target) => [];
}

/// <summary>Base for providers backed by an in-memory entry list, with lazily built indices.</summary>
internal abstract class IndexedSymbolProvider : ISymbolProvider
{
    private readonly Lazy<(IReadOnlyList<SymbolEntry> Entries, ProviderStatus Status)> _load;
    private readonly Lazy<Dictionary<int, List<SymbolName>>> _byIndex;
    private readonly Lazy<Dictionary<string, List<SymbolEntry>>> _byName;
    private readonly Lazy<Dictionary<SymbolTarget, List<SymbolName>>> _byTarget;

    protected IndexedSymbolProvider()
    {
        _load = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
        _byIndex = new(() =>
        {
            var map = new Dictionary<int, List<SymbolName>>();
            foreach (var e in Entries)
            {
                if (!map.TryGetValue(e.FunctionIndex, out var list))
                    map[e.FunctionIndex] = list = [];
                list.Add(e.Name);
            }
            return map;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _byName = new(() =>
        {
            var map = new Dictionary<string, List<SymbolEntry>>(StringComparer.Ordinal);
            foreach (var e in Entries)
            {
                if (!map.TryGetValue(e.Name.Text, out var list))
                    map[e.Name.Text] = list = [];
                list.Add(e);
            }
            return map;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
        _byTarget = new(() =>
        {
            var map = new Dictionary<SymbolTarget, List<SymbolName>>();
            foreach (var e in Entries)
            {
                if (e.Target is null) continue;
                if (!map.TryGetValue(e.Target, out var list))
                    map[e.Target] = list = [];
                list.Add(e.Name);
            }
            return map;
        }, LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public abstract SymbolSource Source { get; }
    public ProviderStatus Status => _load.Value.Status;
    public IReadOnlyList<SymbolEntry> Entries => _load.Value.Entries;

    public IReadOnlyList<SymbolName> NamesFor(int functionIndex) =>
        _byIndex.Value.TryGetValue(functionIndex, out var names) ? names : [];

    public IReadOnlyList<SymbolEntry> LookupExact(string text) =>
        _byName.Value.TryGetValue(text, out var entries) ? entries : [];

    public IReadOnlyList<SymbolName> NamesForTarget(SymbolTarget target) =>
        _byTarget.Value.TryGetValue(target, out var names) ? names : [];

    protected abstract (IReadOnlyList<SymbolEntry> Entries, ProviderStatus Status) Load();

    protected ProviderStatus StatusOf(ProviderState state, string? reason, string? hint = null) =>
        new(Source, null, state, reason, hint);
}

/// <summary>Function names from the <c>name</c> custom section (subsection 1).</summary>
internal sealed class NameSectionSymbolProvider(WasmModule module) : IndexedSymbolProvider
{
    private readonly Lazy<WasmNameSectionReader.NameSectionContents> _contents =
        new(() => WasmNameSectionReader.Read(module), LazyThreadSafetyMode.ExecutionAndPublication);

    public override SymbolSource Source => SymbolSource.NameSection;

    /// <summary>The parsed section (module name, ordered function names, local names).</summary>
    public WasmNameSectionReader.NameSectionContents Contents => _contents.Value;

    /// <summary>Absolute function index → name, as parsed (last entry wins for a repeated index).</summary>
    public IReadOnlyDictionary<int, string> FunctionNames => Contents.FunctionNames;

    public bool TryGetName(int functionIndex, out string name) =>
        Contents.FunctionNames.TryGetValue(functionIndex, out name!);

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        var c = Contents;
        var entries = c.FunctionNames
            .OrderBy(kv => kv.Key)
            .Select(kv => new SymbolEntry(kv.Key, new SymbolName(kv.Value, SymbolSource.NameSection)))
            .ToList();

        ProviderStatus status;
        if (!c.Present)
            status = StatusOf(ProviderState.Absent, "no 'name' custom section", "the module was built or post-processed with names stripped");
        else if (c.Malformation is not null)
            status = StatusOf(entries.Count > 0 ? ProviderState.Degenerate : ProviderState.Failed,
                $"{entries.Count} function names read; {c.Malformation}");
        else if (!c.HasFunctionNamesSubsection)
            status = StatusOf(ProviderState.Absent, "'name' section has no function-names subsection");
        else
            status = StatusOf(ProviderState.Available, $"{entries.Count} function names");
        return (entries, status);
    }
}

/// <summary>Function export names.</summary>
internal sealed class ExportSymbolProvider(WasmModule module) : IndexedSymbolProvider
{
    public override SymbolSource Source => SymbolSource.Export;

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        var entries = new List<SymbolEntry>();
        foreach (var (exportName, export) in module.Exports)
        {
            if (export.Descriptor is ExportDescriptor.Func f)
                entries.Add(new SymbolEntry(f.FunctionIndex, new SymbolName(exportName, SymbolSource.Export)));
        }
        var status = entries.Count > 0
            ? StatusOf(ProviderState.Available, $"{entries.Count} function exports")
            : StatusOf(ProviderState.Absent, "module exports no functions");
        return (entries, status);
    }
}

/// <summary>
/// <c>module.field</c> names of function import slots — the form the runtime gives host functions
/// (<c>WasmRuntime</c>: <c>hostFunc.Name ??= $"{module}.{field}"</c>).
/// </summary>
internal sealed class ImportSymbolProvider(WasmModule module) : IndexedSymbolProvider
{
    public override SymbolSource Source => SymbolSource.Import;

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        var entries = new List<SymbolEntry>();
        int funcIndex = 0;
        foreach (var import in module.Imports)
        {
            if (import.Descriptor is ImportDescriptor.Func)
                entries.Add(new SymbolEntry(funcIndex++, new SymbolName($"{import.ModuleName}.{import.Name}", SymbolSource.Import)));
        }
        var status = entries.Count > 0
            ? StatusOf(ProviderState.Available, $"{entries.Count} function imports")
            : StatusOf(ProviderState.Absent, "module imports no functions");
        return (entries, status);
    }
}

/// <summary>
/// Per-module symbol providers, built lazily and cached per <see cref="WasmModule"/> (a reload parses a
/// new module, so it gets a new entry). The cache also self-invalidates when the module's <c>name</c>
/// section, <c>.debug_info</c> section (e.g. a DWARF sidecar is attached), export map or import list
/// object is replaced, and <see cref="Invalidate"/> drops it explicitly (hot-swap). Each provider
/// parses only on first use: building this object reads nothing.
/// </summary>
internal sealed class ModuleSymbols
{
    private static readonly ConditionalWeakTable<WasmModule, ModuleSymbols> s_cache = new();

    private readonly CustomSection? _nameSectionIdentity;
    private readonly CustomSection? _debugInfoIdentity;
    private readonly object _exportsIdentity;
    private readonly object _importsIdentity;

    private ModuleSymbols(WasmModule module)
    {
        Module = module;
        _nameSectionIdentity = FindSection(module, "name");
        _debugInfoIdentity = FindSection(module, ".debug_info");
        _exportsIdentity = module.Exports;
        _importsIdentity = module.Imports;
        NameSection = new NameSectionSymbolProvider(module);
        Exports = new ExportSymbolProvider(module);
        Imports = new ImportSymbolProvider(module);
        Dwarf = new DwarfSymbolProvider(module);
        Providers = [NameSection, Exports, Imports, Dwarf];
    }

    public WasmModule Module { get; }
    public NameSectionSymbolProvider NameSection { get; }
    public ExportSymbolProvider Exports { get; }
    public ImportSymbolProvider Imports { get; }
    public DwarfSymbolProvider Dwarf { get; }

    /// <summary>Every provider for this module, in no particular order (precedence is the policy's job).</summary>
    public IReadOnlyList<ISymbolProvider> Providers { get; }

    public static ModuleSymbols For(WasmModule module)
    {
        if (s_cache.TryGetValue(module, out var cached) && !cached.IsStale(module))
            return cached;
        var fresh = new ModuleSymbols(module);
        s_cache.AddOrUpdate(module, fresh);
        return fresh;
    }

    /// <summary>Drops the cached providers for <paramref name="module"/>; the next use re-parses.</summary>
    public static void Invalidate(WasmModule module) => s_cache.Remove(module);

    /// <summary>
    /// Every name of function slot <paramref name="functionIndex"/>, from every source, or only from
    /// <paramref name="sources"/> (so a caller that needs cheap sources does not parse DWARF).
    /// </summary>
    public IEnumerable<SymbolName> NamesFor(int functionIndex, IReadOnlySet<SymbolSource>? sources = null) =>
        Providers
            .Where(p => sources is null || sources.Contains(p.Source))
            .SelectMany(p => p.NamesFor(functionIndex));

    /// <summary>Display name of a function slot under <see cref="SymbolPolicy.DisplayPrecedence"/>.</summary>
    public DisplayName GetDisplayName(int functionIndex, IReadOnlySet<SymbolSource>? sources = null) =>
        SymbolPolicy.ChooseDisplayName(NamesFor(functionIndex, sources), functionIndex);

    private bool IsStale(WasmModule module) =>
        !ReferenceEquals(_nameSectionIdentity, FindSection(module, "name"))
        || !ReferenceEquals(_debugInfoIdentity, FindSection(module, ".debug_info"))
        || !ReferenceEquals(_exportsIdentity, module.Exports)
        || !ReferenceEquals(_importsIdentity, module.Imports);

    private static CustomSection? FindSection(WasmModule module, string name)
    {
        foreach (var cs in module.CustomSections)
            if (cs.Name == name) return cs;
        return null;
    }
}
