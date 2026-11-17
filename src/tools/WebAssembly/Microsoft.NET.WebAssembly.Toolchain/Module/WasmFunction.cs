// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using Microsoft.NET.WebAssembly.Types;

/// <summary>A function defined in the module</summary>
public sealed partial class WasmFunction
{
    public required int TypeIndex { get; init; }
    public required List<ValType> Locals { get; set; }
    public required byte[] Body { get; set; }

    /// <summary>
    /// Byte offset of this function's entry (size varint position) within the code section payload.
    /// Used by DCE to map relocation offsets to function indices.
    /// </summary>
    internal uint CodeSectionOffset { get; set; }

    /// <summary>
    /// Byte offset of the first expression byte (<see cref="Body"/>[0]) within the code section payload.
    /// Used to map DWARF .debug_line addresses to instruction indices.
    /// </summary>
    internal uint BodyCodeSectionOffset { get; set; }
}
