// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Table type (§2.3.7) — currently only funcref is supported</summary>
public sealed record TableType(RefType ElementType, Limits Limits)
{
    /// <summary>True for a table64 table (i64 index type).</summary>
    public bool Is64 => Limits.Is64;
}

public enum RefType : byte
{
    FuncRef = 0x70,
    ExternRef = 0x6F,
}
