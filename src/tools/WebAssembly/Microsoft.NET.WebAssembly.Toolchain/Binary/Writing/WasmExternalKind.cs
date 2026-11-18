// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>The kind byte of an import or export descriptor (core spec §5.5.5, §5.5.10).</summary>
public enum WasmExternalKind : byte
{
    Function = 0x00,
    Table = 0x01,
    Memory = 0x02,
    Global = 0x03,
    Tag = 0x04,
}
