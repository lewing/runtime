// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Global type (§2.3.8)</summary>
public sealed record GlobalType(ValType ContentType, Mutability Mutability)
{
    internal string ContentTypeKey { get; init; } = "";
    public ValueType? ExactContentType { get; init; }
}

public enum Mutability : byte
{
    Const = 0x00,
    Var = 0x01,
}
