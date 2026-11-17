// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

// Managed-name providers (#540 PR 5, #538 Phase 1). They hold names computed elsewhere (the static R2R
// index, the live runtime's metadata through the cDAC) and give them to the one resolver policy, so a C#
// method name resolves — exact first, unique substring second, several candidates never first-match —
// exactly like every other name.

/// <summary>
/// Managed C# names of ReadyToRun-compiled function bodies, from the module image's static R2R index
/// (MethodDefEntryPoints chain). Bound to function slots of the R2R module, so a managed name resolves to
/// the R2R body like any wasm name; under <see cref="SymbolPolicy.DisplayPrecedence"/> it becomes the
/// display name and the mangled name-section name stays an alias (lewing's decision on #540).
/// </summary>
/// <param name="index">Function index → managed method name, or null when the module has no usable index.</param>
/// <param name="indexStatus">Why the index is missing or empty (e.g. <c>not_r2r</c>, <c>parse_failed</c>).</param>
internal sealed class R2RSymbolProvider(Func<IReadOnlyDictionary<int, string>?> index, Func<string> indexStatus) : IndexedSymbolProvider
{
    public override SymbolSource Source => SymbolSource.R2R;

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        var map = index();
        string status = indexStatus();
        if (map is null || map.Count == 0)
        {
            return status switch
            {
                "not_r2r" => ([], StatusOf(ProviderState.Absent, "not a ReadyToRun image")),
                "resolved_empty" => ([], StatusOf(ProviderState.Absent, "R2R image with no method entry points")),
                _ => ([], StatusOf(ProviderState.Failed, $"R2R managed-name index unavailable: {status}")),
            };
        }
        var entries = map
            .OrderBy(kv => kv.Key)
            .Select(kv => new SymbolEntry(kv.Key, new SymbolName(kv.Value, SymbolSource.R2R, NameForm.Managed)))
            .ToList();
        // A non-empty index whose element-table walk did not fully resolve is partial: its names are
        // real, but a method missing from it is the index's fault, not the name's.
        return status == "resolved"
            ? (entries, StatusOf(ProviderState.Available, $"{entries.Count} R2R managed names"))
            : (entries, StatusOf(ProviderState.Degenerate, $"{entries.Count} R2R managed names; index partial: {status}",
                "some R2R bodies may be unnamed; break on them by func_index or byte_offset"));
    }
}

/// <summary>One managed method a <see cref="ManagedMethodSymbolProvider"/> names.</summary>
/// <param name="Names">Every name of the method (e.g. <c>Ns.Type.Method</c> and <c>Ns.Type.Method(int)</c>).</param>
/// <param name="Target">The method (assembly + MethodDef token).</param>
internal sealed record ManagedMethodNames(IReadOnlyList<string> Names, ManagedMethodTarget Target);

/// <summary>
/// Managed method names of one loaded assembly, read from the live runtime's metadata (the cDAC
/// <c>ILoader</c>/<c>IEcmaMetadata</c> contracts). Every MethodDef is named — interpreted, R2R and not
/// yet compiled alike — because a breakpoint on a method that has not run yet must still resolve. The
/// entries carry explicit <see cref="ManagedMethodTarget"/>s: an interpreted method has no wasm
/// function, so the scope binds no slots.
/// </summary>
internal sealed class ManagedMethodSymbolProvider(Func<(IReadOnlyList<ManagedMethodNames> Methods, ProviderStatus Status)> load)
    : IndexedSymbolProvider
{
    public override SymbolSource Source => SymbolSource.Cdac;

    protected override (IReadOnlyList<SymbolEntry>, ProviderStatus) Load()
    {
        var (methods, status) = load();
        var entries = new List<SymbolEntry>();
        foreach (var m in methods)
            foreach (var n in m.Names)
                entries.Add(new SymbolEntry(-1, new SymbolName(n, SymbolSource.Cdac, NameForm.Managed), m.Target));
        return (entries, status with { Source = Source });
    }

    /// <summary>
    /// A scope for one assembly: named by the assembly's simple name (so <see cref="NameQuery.CoreModule"/>
    /// filters by assembly), with no function slots.
    /// </summary>
    public static SymbolScope ScopeFor(string assembly, ManagedMethodSymbolProvider provider) =>
        new(assembly, [provider], static _ => null, functionCount: 0, importedFunctionCount: 0);
}
