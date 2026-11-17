// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Block type for structured control instructions (§2.3.3)</summary>
public abstract record BlockType
{
    public sealed record Empty : BlockType;
    public sealed record Value(ValType Type) : BlockType;
    public sealed record FuncIndex(int TypeIndex) : BlockType;
}
