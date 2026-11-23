// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Debugging.Symbols;

internal enum FunctionBreakpointStatus
{
    /// <summary>Exactly one function matched (exactly, or as the only substring hit).</summary>
    Bound,
    /// <summary>Several distinct functions matched in the winning tier; nothing is armed.</summary>
    Ambiguous,
    /// <summary>No source names a matching function; nothing is armed.</summary>
    NotFound,
    /// <summary>The request itself is unusable (e.g. an empty name).</summary>
    Invalid,
}

/// <summary>
/// The outcome of resolving a by-name function breakpoint with the shared resolver (#540), plus the
/// one human-readable message for it. The DAP adapter and the Blazor debugger both render this, so
/// they bind the same function for the same name and explain a refusal the same way as the MCP
/// <c>wasm_debug_*</c> tools: exact (ordinal) before case-insensitive substring, a unique substring
/// hit binds but is labelled, several candidates are listed and never first-picked, and a miss names
/// every source that was consulted.
/// </summary>
internal sealed partial record FunctionBreakpointResolution(
    FunctionBreakpointStatus Status,
    string Query,
    SymbolCandidate? Match,
    ResolveResult? Result,
    string Message)
{
    /// <summary>Candidates listed in an ambiguity message; the rest are counted.</summary>
    public const int MaxListedCandidates = 16;

    /// <summary>The bound wasm function (owning core + <c>DebugIndex</c>); null unless <see cref="FunctionBreakpointStatus.Bound"/>.</summary>
    public WasmFunctionTarget? Target => Match?.Target as WasmFunctionTarget;

    public bool IsBound => Status == FunctionBreakpointStatus.Bound;

    /// <summary>The binding came from the substring tier, not an exact name (maintainer decision on #540).</summary>
    public bool IsSubstringMatch => IsBound && Match!.MatchedBy != MatchMode.Exact;

    /// <summary>The bound slot is a host import, not a wasm-defined function.</summary>
    public bool IsHostImport => Target is { IsWasmDefined: false };

    /// <summary>Resolves <paramref name="name"/> for a breakpoint: exact first, then a unique case-insensitive substring.</summary>
    public static FunctionBreakpointResolution Resolve(SymbolResolver resolver, string? name, string? coreModule = null)
    {
        if (string.IsNullOrWhiteSpace(name))
            return new(FunctionBreakpointStatus.Invalid, name ?? "", null, null, "Empty function name.");

        var result = resolver.Resolve(new NameQuery(name, MatchMode.Substring, coreModule));
        switch (result.Status)
        {
            case ResolveStatus.Unique when result.Match!.Target is WasmFunctionTarget:
                return new(FunctionBreakpointStatus.Bound, name, result.Match, result, DescribeBound(name, result.Match));
            case ResolveStatus.Unique:
                return new(FunctionBreakpointStatus.Invalid, name, null, result,
                    $"'{name}' names {result.Match!.Display.Text}, which has no wasm function to break on.");
            case ResolveStatus.Ambiguous:
                return new(FunctionBreakpointStatus.Ambiguous, name, null, result, DescribeAmbiguous(name, result));
            default:
                return new(FunctionBreakpointStatus.NotFound, name, null, result, DescribeNotFound(name, coreModule, result));
        }
    }

    public static string MatchKind(MatchMode mode) => mode switch
    {
        MatchMode.Exact => "exact",
        MatchMode.Glob => "glob",
        _ => "substring",
    };

    public static string SourceName(SymbolSource source) => source switch
    {
        SymbolSource.NameSection => "name_section",
        SymbolSource.NativeSymbolMap => "native_symbol_map",
        _ => source.ToString().ToLowerInvariant(),
    };

    /// <summary><c>func 3</c>, <c>core-a func 3</c>, or <c>func 0, host import</c>.</summary>
    public static string Location(SymbolTarget target) => target switch
    {
        WasmFunctionTarget { CoreModule: null } t => $"func {t.DebugIndex}{(t.IsWasmDefined ? "" : ", host import")}",
        WasmFunctionTarget t => $"{t.CoreModule} func {t.DebugIndex}{(t.IsWasmDefined ? "" : ", host import")}",
        ManagedMethodTarget m => $"{m.Assembly} 0x{m.MethodToken:X8}",
        _ => target.ToString() ?? "",
    };

    /// <summary><c>foo (func 3)</c>.</summary>
    public static string Describe(SymbolCandidate c) => $"{c.Display.Text} ({Location(c.Target)})";

    private static string DescribeBound(string query, SymbolCandidate c)
    {
        if (c.MatchedBy == MatchMode.Exact)
            return Describe(c);
        var via = c.MatchedNames[0].Name;
        return $"Substring match: '{query}' matched only {Describe(c)} via {SourceName(via.Source)} name '{via.Text}'. " +
            "Use the exact name to avoid relying on a substring.";
    }

    private static string DescribeAmbiguous(string query, ResolveResult r)
    {
        // Classify on EVERY candidate; only the listed ones are truncated.
        int cores = r.Candidates
            .Select(c => (c.Target as WasmFunctionTarget)?.CoreModule ?? "")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        string kind = MatchKind(r.Candidates[0].MatchedBy);
        var listed = r.Candidates.Take(MaxListedCandidates).Select(Describe);
        string more = r.Candidates.Count > MaxListedCandidates ? $", … and {r.Candidates.Count - MaxListedCandidates} more" : "";
        return $"'{query}' is ambiguous ({kind} match, {r.Candidates.Count} functions" +
            $"{(cores > 1 ? $" in {cores} core modules" : "")}): {string.Join(", ", listed)}{more}. " +
            "Nothing was armed; use one function's exact name.";
    }

    private static string DescribeNotFound(string query, string? coreModule, ResolveResult r)
    {
        var bad = r.Consulted
            .Where(s => s.State is ProviderState.Degenerate or ProviderState.Failed)
            .Select(s => $" {(s.Scope is null ? "" : s.Scope + " ")}{SourceName(s.Source)} is unusable: {s.Reason}.{(s.Hint is null ? "" : " " + s.Hint)}");
        return $"No function matches '{query}'{(coreModule is null ? "" : $" in {coreModule}")} " +
            $"(exact, then case-insensitive substring). Consulted: {r.Explain()}.{string.Concat(bad)}";
    }
}
