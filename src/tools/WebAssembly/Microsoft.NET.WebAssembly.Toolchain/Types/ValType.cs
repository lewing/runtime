// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>WebAssembly value types (§2.3.1)</summary>
public enum ValType : byte
{
    I32     = 0x7F,
    I64     = 0x7E,
    F32     = 0x7D,
    F64     = 0x7C,
    V128    = 0x7B,
    FuncRef = 0x70,
    ExternRef = 0x6F,
    ExnRef = 0x69,
    ContRef = 0x65,
}
