// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.IO.Enumeration;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

/// <summary>The one name matcher every tier and every tool uses.</summary>
internal static class SymbolMatcher
{
    public static bool Matches(MatchMode mode, string pattern, string text) => mode switch
    {
        MatchMode.Exact => string.Equals(text, pattern, StringComparison.Ordinal),
        MatchMode.Substring => text.Contains(pattern, StringComparison.OrdinalIgnoreCase),
        MatchMode.Glob => FileSystemName.MatchesSimpleExpression(pattern, text, ignoreCase: true),
        _ => false,
    };
}

/// <summary>
/// One searchable unit: a core module (WASI/component debugging) or, later, a CDP script. It owns the
/// providers for that module and binds a provider's function slot to a <see cref="SymbolTarget"/>.
/// </summary>
internal sealed partial class SymbolScope
{
    private readonly Func<int, SymbolTarget?> _bind;

    /// <param name="name">Scope label (core module name).</param>
    /// <param name="providers">Name providers for the scope's module.</param>
    /// <param name="bind">Maps a function slot to its target (null when the slot does not exist).</param>
    /// <param name="functionCount">Number of function slots (imports first).</param>
    /// <param name="importedFunctionCount">Number of leading import slots.</param>
    public SymbolScope(string? name, IReadOnlyList<ISymbolProvider> providers, Func<int, SymbolTarget?> bind,
        int functionCount, int importedFunctionCount)
    {
        Name = name;
        Providers = providers;
        _bind = bind;
        FunctionCount = functionCount;
        ImportedFunctionCount = importedFunctionCount;
    }

    public int FunctionCount { get; }

    /// <summary>Leading import slots: the only slots that can bind to a function owned by another scope.</summary>
    public int ImportedFunctionCount { get; }

    /// <summary>Scope label (the core module name); null for an unnamed single module.</summary>
    public string? Name { get; }

    public IReadOnlyList<ISymbolProvider> Providers { get; }

    public SymbolTarget? Bind(int functionIndex) => _bind(functionIndex);

    /// <summary>A scope over a module alone (no instance): slot N is function N of this module.</summary>
    public static SymbolScope ForModule(WasmModule module, string? name = null)
    {
        int importCount = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        int total = importCount + module.Functions.Count;
        return new SymbolScope(name, ModuleSymbols.For(module).Providers, i =>
            (uint)i < (uint)total ? new WasmFunctionTarget(name, i, IsWasmDefined: i >= importCount) : null,
            total, importCount);
    }
}

/// <summary>
/// The shared function-name resolver (#540). One policy for every tool:
/// <list type="number">
/// <item>Tier 1: exact ordinal match across every scope and source. Tier 2 (the query's mode,
/// case-insensitive) runs only when tier 1 found nothing, so an exact name in one core is never made
/// ambiguous by a longer name in another.</item>
/// <item>Candidates are deduplicated by target, never by name: the same function named by several
/// sources (or reached through several cores) is one candidate carrying every matching name; the same
/// text naming different functions is several candidates.</item>
/// <item>One distinct target is <see cref="ResolveStatus.Unique"/>; several are
/// <see cref="ResolveStatus.Ambiguous"/>. Never first-match.</item>
/// </list>
/// </summary>
internal sealed class SymbolResolver(IReadOnlyList<SymbolScope> scopes)
{
    private const int MaxNearMisses = 5;

    public IReadOnlyList<SymbolScope> Scopes { get; } = scopes;

    public ResolveResult Resolve(NameQuery query)
    {
        var consulted = Consulted(query);
        var exact = Collect(query, MatchMode.Exact, int.MaxValue);
        if (exact.Count > 0)
            return Classify(exact, consulted);

        if (query.Mode != MatchMode.Exact)
        {
            var tier2 = Collect(query, query.Mode, int.MaxValue);
            if (tier2.Count > 0)
                return Classify(tier2, consulted);
            return new ResolveResult(ResolveStatus.NotFound, null, [], consulted, []);
        }

        // Exact query missed: offer case-insensitive / substring near misses, never as a match.
        var near = Collect(query, MatchMode.Substring, MaxNearMisses);
        return new ResolveResult(ResolveStatus.NotFound, null, [], consulted, near);
    }

    /// <summary>
    /// Every target matching <paramref name="query"/> (its mode, not tiered), exact hits first,
    /// up to <paramref name="limit"/> distinct targets.
    /// </summary>
    public IReadOnlyList<SymbolCandidate> Search(NameQuery query, int limit = int.MaxValue)
    {
        if (limit <= 0) return [];
        var exact = query.Mode == MatchMode.Exact ? [] : Collect(query, MatchMode.Exact, limit);
        int stopAfter = (int)Math.Min(int.MaxValue, (long)limit + exact.Count);
        var all = Collect(query, query.Mode, int.MaxValue, stopAfter);
        var seen = new HashSet<SymbolTarget>(exact.Select(c => c.Target));
        var result = new List<SymbolCandidate>(exact);
        foreach (var c in all)
        {
            if (result.Count >= limit) break;
            if (seen.Add(c.Target)) result.Add(c);
        }
        return result;
    }

    /// <summary>
    /// Streams every function target once, in owning-scope then index order, with every name it has:
    /// its owning slot's names plus the names other scopes give the import slots that bind to it.
    /// Lazy, so a consumer that stops early (a capped search) never materializes the rest; only the
    /// import slots of every scope are pre-scanned. A target whose owner is not among the scopes is
    /// yielded at the first importing slot.
    /// </summary>
    public IEnumerable<(SymbolTarget Target, SymbolScope Scope, int FunctionIndex, IReadOnlyList<SymbolMatch> Names)> EnumerateTargets()
    {
        var importAliases = new Dictionary<SymbolTarget, List<SymbolMatch>>();
        var importOnly = new List<(SymbolTarget Target, SymbolScope Scope, int Index)>();
        foreach (var scope in Scopes)
        {
            for (int i = 0; i < scope.ImportedFunctionCount; i++)
            {
                if (scope.Bind(i) is not WasmFunctionTarget t || IsOwnSlot(scope, t, i)) continue;
                if (!importAliases.TryGetValue(t, out var aliases))
                {
                    importAliases[t] = aliases = [];
                    if (!Scopes.Any(s => OwnsTarget(s, t)))
                        importOnly.Add((t, scope, i));
                }
                foreach (var provider in scope.Providers)
                    foreach (var n in provider.NamesFor(i))
                        aliases.Add(new SymbolMatch(n, scope.Name, i));
            }
        }

        foreach (var scope in Scopes)
        {
            for (int i = 0; i < scope.FunctionCount; i++)
            {
                if (scope.Bind(i) is not { } target) continue;
                if (target is WasmFunctionTarget wf && !IsOwnSlot(scope, wf, i)) continue;
                var names = new List<SymbolMatch>();
                foreach (var provider in scope.Providers)
                    foreach (var n in provider.NamesFor(i))
                        names.Add(new SymbolMatch(n, scope.Name, i));
                if (importAliases.TryGetValue(target, out var aliases))
                    names.AddRange(aliases);
                yield return (target, scope, i, names);
            }
        }

        foreach (var (target, scope, index) in importOnly)
            yield return (target, scope, index, importAliases[target]);
    }

    private static bool IsOwnSlot(SymbolScope scope, WasmFunctionTarget t, int index) =>
        t.DebugIndex == index && string.Equals(t.CoreModule, scope.Name, StringComparison.OrdinalIgnoreCase);

    private static bool OwnsTarget(SymbolScope scope, WasmFunctionTarget t) =>
        string.Equals(t.CoreModule, scope.Name, StringComparison.OrdinalIgnoreCase)
        && Equals(scope.Bind(t.DebugIndex), t);

    /// <summary>Display name of <paramref name="target"/>, from its owning scope's providers.</summary>
    public DisplayName GetDisplayName(SymbolTarget target) => DisplayFor(target, []);

    /// <summary>Status of every provider in every scope the query covers.</summary>
    public IReadOnlyList<ProviderStatus> Consulted(NameQuery query)
    {
        var list = new List<ProviderStatus>();
        foreach (var scope in ScopesFor(query))
            foreach (var provider in ProvidersFor(scope, query))
                list.Add(provider.Status with { Scope = scope.Name });
        return list;
    }

    private ResolveResult Classify(List<SymbolCandidate> candidates, IReadOnlyList<ProviderStatus> consulted) =>
        candidates.Count == 1
            ? new ResolveResult(ResolveStatus.Unique, candidates[0], candidates, consulted, [])
            : new ResolveResult(ResolveStatus.Ambiguous, null, candidates, consulted, []);

    private List<SymbolCandidate> Collect(NameQuery query, MatchMode mode, int limit, int stopAfter = int.MaxValue)
    {
        var order = new List<SymbolTarget>();
        var matches = new Dictionary<SymbolTarget, List<SymbolMatch>>();

        foreach (var scope in ScopesFor(query))
        {
            foreach (var provider in ProvidersFor(scope, query))
            {
                IEnumerable<SymbolEntry> hits = mode == MatchMode.Exact
                    ? provider.LookupExact(query.Text)
                    : provider.Entries.Where(e => SymbolMatcher.Matches(mode, query.Text, e.Name.Text));
                foreach (var entry in hits)
                {
                    var target = entry.Target ?? scope.Bind(entry.FunctionIndex);
                    if (target is null) continue;
                    if (!matches.TryGetValue(target, out var list))
                    {
                        if (order.Count >= stopAfter) continue;
                        matches[target] = list = [];
                        order.Add(target);
                    }
                    list.Add(new SymbolMatch(entry.Name, scope.Name, entry.FunctionIndex));
                }
            }
        }

        return order
            .Take(limit)
            .Select(t => new SymbolCandidate(t, matches[t], mode, DisplayFor(t, matches[t])))
            .ToList();
    }

    private DisplayName DisplayFor(SymbolTarget target, IReadOnlyList<SymbolMatch> matched)
    {
        var names = new List<SymbolName>();
        int index = -1;
        if (target is WasmFunctionTarget wf)
        {
            index = wf.DebugIndex;
            foreach (var scope in Scopes)
            {
                if (!string.Equals(scope.Name, wf.CoreModule, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Equals(scope.Bind(wf.DebugIndex), target)) continue;
                foreach (var provider in scope.Providers)
                    names.AddRange(provider.NamesFor(wf.DebugIndex));
                break;
            }
        }
        else
        {
            // A target with no function slot (a managed method) is named by every entry that targets it
            // explicitly, so its display and aliases do not depend on which of its names a query matched.
            foreach (var scope in Scopes)
                foreach (var provider in scope.Providers)
                    names.AddRange(provider.NamesForTarget(target));
        }
        // Names the target was matched by (e.g. through an importing core) are aliases too.
        names.AddRange(matched.Select(m => m.Name));
        return SymbolPolicy.ChooseDisplayName(names, index);
    }

    private IEnumerable<SymbolScope> ScopesFor(NameQuery query) =>
        query.CoreModule is null
            ? Scopes
            : Scopes.Where(s => string.Equals(s.Name, query.CoreModule, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<ISymbolProvider> ProvidersFor(SymbolScope scope, NameQuery query) =>
        query.Sources is null ? scope.Providers : scope.Providers.Where(p => query.Sources.Contains(p.Source));
}
