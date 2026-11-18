// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

// Shared function-name resolver model (issue #540). One provider per name source, each recording
// where its names came from; one policy (SymbolResolver) for precedence, ambiguity and matching.
//
// Files in this folder that do not end in ".Runtime.cs" depend only on Microsoft.NET.WebAssembly.Module / Microsoft.NET.WebAssembly.Binary
// and are also compiled into Nesm.Toolchain (the disassembler consumes ModuleSymbols).

/// <summary>Where a name came from.</summary>
/// <remarks>
/// PR 1 implemented <see cref="Export"/>, <see cref="NameSection"/> and <see cref="Import"/>; PR 2
/// (#530) added <see cref="Dwarf"/>. The remaining sources are reserved so later providers slot in
/// without changing the policy: <see cref="NativeSymbolMap"/> and <see cref="R2R"/> (PR 3, in
/// Nesm.Cdp), <see cref="Cdac"/> (PR 5). When the R2R provider lands, its managed C# name is the
/// primary display name for R2R code and the mangled name-section name stays available as an alias
/// (lewing's decision on #540); <see cref="SymbolPolicy.DisplayPrecedence"/> already orders it so.
/// </remarks>
internal enum SymbolSource
{
    Export,
    NameSection,
    /// <summary>Import <c>module.field</c> names of function import slots.</summary>
    Import,
    Dwarf,
    Linking,
    NativeSymbolMap,
    R2R,
    Cdac,
    Synthetic,
}

internal enum NameForm
{
    Raw,
    Mangled,
    Demangled,
    Managed
}

internal sealed record SymbolName(string Text, SymbolSource Source, NameForm Form = NameForm.Raw);

/// <summary>What a name resolves to.</summary>
internal abstract record SymbolTarget;

/// <summary>
/// A function in a core module. <see cref="CoreModule"/> is the OWNING core (for a re-wrapped
/// cross-module import this is the defining core, not the importing one) and <see cref="DebugIndex"/>
/// is the index breakpoints are keyed on (<c>FuncRef.DebugIndex</c>). Two names that reach the same
/// function through different cores therefore deduplicate to one target. A host function
/// (<see cref="IsWasmDefined"/> false) has no owning wasm core: it is identified by the importing
/// core and its slot there. <see cref="CoreModule"/> is null only for an unnamed single module.
/// </summary>
internal sealed record WasmFunctionTarget(string? CoreModule, int DebugIndex, bool IsWasmDefined = true) : SymbolTarget
{
    public override string ToString() => CoreModule is null ? $"[{DebugIndex}]" : $"{CoreModule}[{DebugIndex}]";
}

/// <summary>
/// A managed method. Reserved for the R2R (PR 3) and cDAC (PR 5) providers: interpreted methods have
/// no wasm function, so <see cref="R2RBody"/> is null for them.
/// </summary>
internal sealed record ManagedMethodTarget(string Assembly, int MethodToken, WasmFunctionTarget? R2RBody) : SymbolTarget;

internal enum ProviderState
{
    /// <summary>The source exists and contributed names.</summary>
    Available,
    /// <summary>The module simply has no such source.</summary>
    Absent,
    /// <summary>The source may exist but has not been loaded (e.g. a sidecar not attached yet).</summary>
    NotLoaded,
    /// <summary>The source exists but is unusable or only partly usable (e.g. truncated data).</summary>
    Degenerate,
    /// <summary>Reading the source failed.</summary>
    Failed,
}

/// <summary>Per-provider status, used to explain a miss.</summary>
internal sealed record ProviderStatus(SymbolSource Source, string? Scope, ProviderState State, string? Reason, string? Hint = null)
{
    public override string ToString() =>
        $"{(Scope is null ? "" : Scope + " ")}{Source}: {State}{(Reason is null ? "" : $" \"{Reason}\"")}";
}

internal enum MatchMode
{
    /// <summary>Ordinal (case-sensitive) equality.</summary>
    Exact,
    /// <summary>Case-insensitive substring.</summary>
    Substring,
    /// <summary>Case-insensitive glob (<c>*</c> and <c>?</c>).</summary>
    Glob,
}

/// <param name="Text">The name or pattern.</param>
/// <param name="Mode">
/// How tier 2 matches. Tier 1 (exact, ordinal) always runs first; tier 2 runs only when tier 1 found
/// nothing. <see cref="MatchMode.Exact"/> disables tier 2.
/// </param>
/// <param name="CoreModule">Restrict to the scope with this name (case-insensitive).</param>
/// <param name="Sources">Restrict to these sources (null = all).</param>
internal sealed record NameQuery(
    string Text,
    MatchMode Mode = MatchMode.Substring,
    string? CoreModule = null,
    IReadOnlySet<SymbolSource>? Sources = null);

/// <summary>A name that matched, and the scope (core module) and function slot that supplied it.</summary>
internal sealed record SymbolMatch(SymbolName Name, string? Scope, int FunctionIndex);

/// <summary>
/// One distinct target. All names that matched it, from every source and scope, are carried
/// together: candidates are deduplicated by target, never by name.
/// </summary>
internal sealed record SymbolCandidate(
    SymbolTarget Target,
    IReadOnlyList<SymbolMatch> MatchedNames,
    MatchMode MatchedBy,
    DisplayName Display);

internal enum ResolveStatus
{
    Unique,
    Ambiguous,
    NotFound
}

internal sealed record ResolveResult(
    ResolveStatus Status,
    SymbolCandidate? Match,
    IReadOnlyList<SymbolCandidate> Candidates,
    IReadOnlyList<ProviderStatus> Consulted,
    IReadOnlyList<SymbolCandidate> NearMisses)
{
    /// <summary>A one-line explanation of a miss: every scope × source consulted, with its state.</summary>
    public string Explain() => string.Join("; ", Consulted.Select(s => s.ToString()));
}

/// <summary>The chosen display name plus every other name for the same function.</summary>
internal sealed record DisplayName(SymbolName Chosen, IReadOnlyList<SymbolName> Aliases)
{
    public string Text => Chosen.Text;
    public bool IsSynthetic => Chosen.Source == SymbolSource.Synthetic;
}

internal static class SymbolPolicy
{
    /// <summary>
    /// The single display precedence (#540 §2.3 rule 7): managed names before mangled wasm names.
    /// Sources with no provider yet simply never contribute.
    /// </summary>
    public static readonly IReadOnlyList<SymbolSource> DisplayPrecedence =
    [
        SymbolSource.Cdac,
        SymbolSource.R2R,
        SymbolSource.NameSection,
        SymbolSource.NativeSymbolMap,
        SymbolSource.Dwarf,
        SymbolSource.Export,
        SymbolSource.Linking,
        SymbolSource.Import,
        SymbolSource.Synthetic,
    ];

    public static int Rank(SymbolSource source)
    {
        for (int i = 0; i < DisplayPrecedence.Count; i++)
            if (DisplayPrecedence[i] == source) return i;
        return DisplayPrecedence.Count;
    }

    /// <summary>The one synthetic form (V8's).</summary>
    public static string SyntheticName(int functionIndex) => $"wasm-function[{functionIndex}]";

    public static DisplayName ChooseDisplayName(IEnumerable<SymbolName> names, int functionIndex)
    {
        var ordered = names
            .Distinct()
            .OrderBy(n => Rank(n.Source))
            .ToList();
        if (ordered.Count == 0)
            return new DisplayName(new SymbolName(SyntheticName(functionIndex), SymbolSource.Synthetic), []);
        return new DisplayName(ordered[0], ordered.Skip(1).ToList());
    }
}
