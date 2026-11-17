// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// Per-input-module index remapping tables. Maps module-local (old) indices to merged output (new) indices
/// for each of the six Wasm index spaces: types, functions, globals, tables, memories, and tags.
/// A separate IndexMap instance is created for each input module during linking.
/// </summary>
public sealed class IndexMap
{
    /// <summary>Maps old type index → merged type index (after signature deduplication).</summary>
    public Dictionary<uint, uint> Types { get; } = new();

    /// <summary>
    /// Maps old function index → merged function index. Covers both imported
    /// functions (0..numImports-1) and locally-defined functions.
    /// </summary>
    public Dictionary<uint, uint> Functions { get; } = new();

    /// <summary>Maps old global index → merged global index.</summary>
    public Dictionary<uint, uint> Globals { get; } = new();

    /// <summary>Maps old table index → merged table index.</summary>
    public Dictionary<uint, uint> Tables { get; } = new();

    /// <summary>Maps old memory index → merged memory index.</summary>
    public Dictionary<uint, uint> Memories { get; } = new();

    /// <summary>Maps old tag index → merged tag index.</summary>
    public Dictionary<uint, uint> Tags { get; } = new();

    /// <summary>Maps old function-offset (for DWARF) → merged function-offset. Used by <c>R_WASM_FUNCTION_OFFSET_I32</c>.</summary>
    public Dictionary<uint, uint> FunctionOffsets { get; } = new();

    /// <summary>Maps old section offset → merged section offset. Used by <c>R_WASM_SECTION_OFFSET_I32</c>.</summary>
    public Dictionary<uint, uint> SectionOffsets { get; } = new();

    /// <summary>Maps old data segment index → merged data segment index.</summary>
    public Dictionary<uint, uint> DataSegments { get; } = new();

    /// <summary>Maps old element segment index → merged element segment index.</summary>
    public Dictionary<uint, uint> Elements { get; } = new();

    /// <summary>
    /// Maps old data segment index → final linear-memory base offset after section merging.
    /// Used by <c>R_WASM_MEMORY_ADDR_*</c> relocation patching.
    /// </summary>
    public Dictionary<uint, uint> DataSegmentOffsets { get; } = new();
}
