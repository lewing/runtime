// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Linking;

/// <summary>Properties of relocation types that every producer and consumer of relocatable objects must agree on.</summary>
public static class RelocationTypes
{
    /// <summary>
    /// Whether a relocation entry of <paramref name="type"/> carries an SLEB128 addend field. Mirrors
    /// <c>llvm::wasm::relocTypeHasAddend</c>; table-index relocations, including the REL forms, carry none.
    /// </summary>
    public static bool HasAddend(RelocationType type) => type switch
    {
        RelocationType.R_WASM_MEMORY_ADDR_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_I32 or
        RelocationType.R_WASM_FUNCTION_OFFSET_I32 or
        RelocationType.R_WASM_SECTION_OFFSET_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_LEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB or
        RelocationType.R_WASM_FUNCTION_OFFSET_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64 => true,
        _ => false,
    };

    /// <summary>
    /// The size in bytes of the field a relocation of <paramref name="type"/> patches: a padded LEB128 of 5 or 10
    /// bytes, or a little-endian integer of 4 or 8 bytes.
    /// </summary>
    public static int GetPatchSize(RelocationType type) => type switch
    {
        RelocationType.R_WASM_FUNCTION_INDEX_LEB or
        RelocationType.R_WASM_TABLE_INDEX_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB or
        RelocationType.R_WASM_TYPE_INDEX_LEB or
        RelocationType.R_WASM_GLOBAL_INDEX_LEB or
        RelocationType.R_WASM_TAG_INDEX_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or
        RelocationType.R_WASM_TABLE_INDEX_REL_SLEB or
        RelocationType.R_WASM_TABLE_NUMBER_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB => 5,

        RelocationType.R_WASM_MEMORY_ADDR_LEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64 or
        RelocationType.R_WASM_TABLE_INDEX_SLEB64 or
        RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64 => 10,

        RelocationType.R_WASM_TABLE_INDEX_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_I32 or
        RelocationType.R_WASM_FUNCTION_OFFSET_I32 or
        RelocationType.R_WASM_SECTION_OFFSET_I32 or
        RelocationType.R_WASM_GLOBAL_INDEX_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32 or
        RelocationType.R_WASM_FUNCTION_INDEX_I32 => 4,

        RelocationType.R_WASM_MEMORY_ADDR_I64 or
        RelocationType.R_WASM_TABLE_INDEX_I64 or
        RelocationType.R_WASM_FUNCTION_OFFSET_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64 => 8,

        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown relocation type."),
    };
}
