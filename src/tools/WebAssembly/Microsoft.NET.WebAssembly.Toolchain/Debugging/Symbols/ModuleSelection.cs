// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

/// <summary>Which name source a <see cref="ModuleSelector"/> searches (#625).</summary>
internal enum ModuleSelectorKind
{
    /// <summary>An export name.</summary>
    Export,
    /// <summary>A name-section function name.</summary>
    Function,
    /// <summary>A managed C# method name from the module's R2R index.</summary>
    ManagedMethod,
}

/// <summary>
/// Names a module by something it contains rather than by where it came from (#625): under node every
/// wasm module is <c>wasm://wasm/&lt;hash&gt;</c>, so a file-name filter cannot pick one, but the module's
/// own exports, name section and R2R managed names can.
/// </summary>
internal sealed record ModuleSelector(ModuleSelectorKind Kind, string Name)
{
    private static readonly IReadOnlySet<SymbolSource> s_export = new HashSet<SymbolSource> { SymbolSource.Export };
    private static readonly IReadOnlySet<SymbolSource> s_function = new HashSet<SymbolSource> { SymbolSource.NameSection };
    private static readonly IReadOnlySet<SymbolSource> s_managed = new HashSet<SymbolSource> { SymbolSource.R2R };

    /// <summary>
    /// The resolver query: the selector's source only, functions with a body only, exact first, then
    /// case-insensitive substring.
    /// </summary>
    public NameQuery Query => new(Name, MatchMode.Substring, Sources: Kind switch
    {
        ModuleSelectorKind.Export => s_export,
        ModuleSelectorKind.Function => s_function,
        _ => s_managed,
    }, DefinedOnly: true);

    public override string ToString() => Kind switch
    {
        ModuleSelectorKind.Export => $"export '{Name}'",
        ModuleSelectorKind.Function => $"name-section function '{Name}'",
        _ => $"managed method '{Name}'",
    };
}

/// <summary>One module the selector matched, with the functions in it that matched.</summary>
/// <param name="Module">The module's identity (see <see cref="ModuleSelection.Select(IReadOnlyList{SymbolScope}, ModuleSelector, Func{string, string})"/>).</param>
/// <param name="Scope">The scope the first match came from (one of possibly several instances).</param>
/// <param name="Functions">Distinct matched functions with a body in that module.</param>
internal sealed record ModuleMatch(string Module, string? Scope, IReadOnlyList<SymbolCandidate> Functions);

/// <param name="Status">One module (<see cref="ResolveStatus.Unique"/>), several, or none.</param>
/// <param name="Selected">The module when unique.</param>
/// <param name="Modules">Every matched module, in scope order.</param>
/// <param name="Resolution">The underlying resolver result (consulted sources, near misses).</param>
internal sealed record ModuleSelectionResult(
    ResolveStatus Status,
    ModuleMatch? Selected,
    IReadOnlyList<ModuleMatch> Modules,
    ResolveResult Resolution)
{
    /// <summary>How the match was made: exact (ordinal) beats case-insensitive substring across all modules.</summary>
    public MatchMode? MatchedBy => Modules.Count > 0 ? Modules[0].Functions[0].MatchedBy : null;

    /// <summary>The matched function when the selected module has exactly one; null otherwise.</summary>
    public SymbolCandidate? Function => Selected is { Functions.Count: 1 } m ? m.Functions[0] : null;

    /// <summary>
    /// Providers of the query's sources that could not be searched in full — failed, degenerate (e.g. a
    /// partial R2R index) or not loaded. A name missing from one of them is the provider's fault, not
    /// the module's, so a decision that rules a module out by such a miss is not proven (#631 review).
    /// </summary>
    public IReadOnlyList<ProviderStatus> Unsearched
    {
        get
        {
            List<ProviderStatus>? list = null;
            foreach (var status in Resolution.Consulted)
            {
                if (status.State is ProviderState.Failed or ProviderState.Degenerate or ProviderState.NotLoaded)
                    (list ??= []).Add(status);
            }
            return list ?? (IReadOnlyList<ProviderStatus>)[];
        }
    }
}

/// <summary>
/// Chooses a module by content with the one resolver policy (#625, #540): a name is resolved across
/// every scope at once — exact (ordinal) before case-insensitive substring, deduplicated by target, never
/// first-match — and the matches are then grouped by module. One module is a selection even when several
/// of its functions match; several modules are ambiguous and list their candidates. A
/// <see cref="ModuleSelector"/> counts only functions with a body (an import slot names the module that
/// imports a function, not the one that has it), filtered inside the resolver before the tiers. The
/// native debugger's <c>wasm_debug_break</c> uses the same policy with its own query (all sources, import
/// slots included: it arms host-import breakpoints), so CDP and native decide one way.
/// </summary>
internal static class ModuleSelection
{
    /// <param name="scopes">One scope per module instance to consider.</param>
    /// <param name="selector">What to look for.</param>
    /// <param name="moduleOf">
    /// Maps a scope name to the module's identity. Instances of the same module (the same bytes loaded
    /// twice) share an identity, so they are one selection, not an ambiguity.
    /// </param>
    public static ModuleSelectionResult Select(
        IReadOnlyList<SymbolScope> scopes, ModuleSelector selector, Func<string?, string> moduleOf)
        => Select(new SymbolResolver(scopes), selector.Query, moduleOf);

    /// <summary>The same decision over a prepared resolver and any query.</summary>
    public static ModuleSelectionResult Select(
        SymbolResolver resolver, NameQuery query, Func<string?, string> moduleOf)
    {
        var resolution = resolver.Resolve(query);
        var order = new List<string>();
        var byModule = new Dictionary<string, (string? Scope, List<SymbolCandidate> Functions)>(StringComparer.Ordinal);
        foreach (var candidate in resolution.Candidates)
        {
            // A host import (no body) belongs to the core that imports it; a target with no wasm slot to
            // no core. Only a DefinedOnly query could have dropped them, and it already has.
            string? scope = candidate.Target is WasmFunctionTarget target ? target.CoreModule : null;
            string module = moduleOf(scope);
            if (!byModule.TryGetValue(module, out var entry))
            {
                entry = (scope, []);
                byModule[module] = entry;
                order.Add(module);
            }
            entry.Functions.Add(candidate);
        }

        var modules = new List<ModuleMatch>(order.Count);
        foreach (string module in order)
        {
            var (scope, functions) = byModule[module];
            modules.Add(new ModuleMatch(module, scope, functions));
        }
        return modules.Count switch
        {
            0 => new ModuleSelectionResult(ResolveStatus.NotFound, null, modules, resolution),
            1 => new ModuleSelectionResult(ResolveStatus.Unique, modules[0], modules, resolution),
            _ => new ModuleSelectionResult(ResolveStatus.Ambiguous, null, modules, resolution),
        };
    }
}
