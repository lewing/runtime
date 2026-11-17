// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Element segment for table initialization (§2.5.8)</summary>
public sealed class Element
{
    public ElementMode Mode { get; init; } = ElementMode.Active;
    public required int TableIndex { get; init; }
    public required byte[] OffsetExpr { get; init; }
    public required List<int> FunctionIndices { get; init; }
    public List<byte[]> ElementExpressions { get; init; } = [];
    public List<int> DeclaredFunctionIndices { get; init; } = [];
    /// <summary>True for the expression-vector encodings (flags 4–7), including an empty vector.</summary>
    public bool UsesExpressions { get; init; }
    /// <summary>True when an active segment encoded its table index explicitly (flags 2 or 6).</summary>
    public bool HasExplicitTableIndex { get; init; }
    public Microsoft.NET.WebAssembly.Types.ValueType ElementType { get; init; } =
        new Microsoft.NET.WebAssembly.Types.ValueType.Reference(
            Microsoft.NET.WebAssembly.Types.Nullability.Nullable,
            new Microsoft.NET.WebAssembly.Types.AbstractHeapType(Microsoft.NET.WebAssembly.Types.HeapTypeKind.Func));
}

public enum ElementMode
{
    Active,
    Passive,
    Declarative,
}
