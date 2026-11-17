// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

public sealed class WatSymbolTable
{
    // Name → (index, definition span)
    public IReadOnlyDictionary<string, SymbolInfo> Functions { get; init; } = new Dictionary<string, SymbolInfo>();
    public IReadOnlyDictionary<string, SymbolInfo> Globals { get; init; } = new Dictionary<string, SymbolInfo>();
    public IReadOnlyDictionary<string, SymbolInfo> Memories { get; init; } = new Dictionary<string, SymbolInfo>();
    public IReadOnlyDictionary<string, SymbolInfo> Tables { get; init; } = new Dictionary<string, SymbolInfo>();
    public IReadOnlyDictionary<string, SymbolInfo> Tags { get; init; } = new Dictionary<string, SymbolInfo>();
    public IReadOnlyDictionary<string, SymbolInfo> Types { get; init; } = new Dictionary<string, SymbolInfo>();

    // Per-function local symbol tables (indexed by function index)
    public IReadOnlyDictionary<int, IReadOnlyDictionary<string, SymbolInfo>> Locals { get; init; }
        = new Dictionary<int, IReadOnlyDictionary<string, SymbolInfo>>();

    // All reference use-sites collected during resolution
    public IReadOnlyList<SymbolReference> References { get; init; } = [];
}

public readonly record struct SymbolInfo(int Index, SourceSpan Span);

public readonly record struct SymbolReference(
    string Name,
    SourceSpan UseSpan,
    int ResolvedIndex,
    SymbolKind Kind);

public enum SymbolKind
{
    Function,
    Global,
    Memory,
    Table,
    Tag,
    Type,
    Local,
    Label,
}
