// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Linking;

using System.Collections.Generic;

public sealed class RelocationSection
{
    public string Name { get; init; } = string.Empty;
    public uint TargetSectionIndex { get; init; }
    public List<Relocation> Entries { get; init; } = [];
}

public sealed class Relocation
{
    public RelocationType Type { get; init; }
    public uint Offset { get; init; }
    public uint Index { get; init; }
    public int Addend { get; init; }
}

public enum RelocationType : byte
{
    R_WASM_FUNCTION_INDEX_LEB = 0,
    R_WASM_TABLE_INDEX_SLEB = 1,
    R_WASM_TABLE_INDEX_I32 = 2,
    R_WASM_MEMORY_ADDR_LEB = 3,
    R_WASM_MEMORY_ADDR_SLEB = 4,
    R_WASM_MEMORY_ADDR_I32 = 5,
    R_WASM_TYPE_INDEX_LEB = 6,
    R_WASM_GLOBAL_INDEX_LEB = 7,
    R_WASM_FUNCTION_OFFSET_I32 = 8,
    R_WASM_SECTION_OFFSET_I32 = 9,
    R_WASM_TAG_INDEX_LEB = 10,
    R_WASM_EVENT_INDEX_LEB = R_WASM_TAG_INDEX_LEB,
    R_WASM_MEMORY_ADDR_REL_SLEB = 11,
    R_WASM_TABLE_INDEX_REL_SLEB = 12,
    R_WASM_GLOBAL_INDEX_I32 = 13,
    R_WASM_MEMORY_ADDR_LEB64 = 14,
    R_WASM_MEMORY_ADDR_SLEB64 = 15,
    R_WASM_MEMORY_ADDR_I64 = 16,
    R_WASM_MEMORY_ADDR_REL_SLEB64 = 17,
    R_WASM_TABLE_INDEX_SLEB64 = 18,
    R_WASM_TABLE_INDEX_I64 = 19,
    R_WASM_TABLE_NUMBER_LEB = 20,
    R_WASM_MEMORY_ADDR_TLS_SLEB = 21,
    R_WASM_FUNCTION_OFFSET_I64 = 22,
    R_WASM_MEMORY_ADDR_LOCREL_I32 = 23,
    R_WASM_TABLE_INDEX_REL_SLEB64 = 24,
    R_WASM_MEMORY_ADDR_TLS_SLEB64 = 25,
    R_WASM_FUNCTION_INDEX_I32 = 26,
}
