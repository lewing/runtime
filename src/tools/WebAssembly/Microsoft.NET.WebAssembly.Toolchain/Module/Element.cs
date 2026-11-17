// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Element segment for table initialization (§2.5.8)</summary>
public sealed class Element
{
    public required int TableIndex { get; init; }
    public required byte[] OffsetExpr { get; init; }
    public required List<int> FunctionIndices { get; init; }
}
