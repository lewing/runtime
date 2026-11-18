// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

// Core module binary encodings (core spec binary format, types and modules). Section ids live in WasmSectionId, external kinds in
// Writing.WasmExternalKind (public, for producers), and value/heap type codes in ValType and AbstractHeapTypeCodes.

/// <summary>The leading byte of a composite type in the type section.</summary>
internal enum CompositeTypeCode : byte
{
    Func = 0x60,
    Struct = 0x5F,
    Array = 0x5E,
    Cont = 0x5D,
}

/// <summary>The flags byte that starts a table or memory type's limits (with memory64 and threads).</summary>
[Flags]
internal enum WasmLimitsFlags : byte
{
    None = 0x00,
    HasMaximum = 0x01,
    Shared = 0x02,
    Is64 = 0x04,
    All = HasMaximum | Shared | Is64,
}

/// <summary>An element segment's flags: its mode, whether the table index is explicit, and whether it holds
/// function indices (with an <c>elemkind</c>) or expressions (with a reference type).</summary>
internal enum ElementSegmentKind : byte
{
    ActiveFunctions = 0,
    PassiveFunctions = 1,
    ActiveTableFunctions = 2,
    DeclarativeFunctions = 3,
    ActiveExpressions = 4,
    PassiveExpressions = 5,
    ActiveTableExpressions = 6,
    DeclarativeExpressions = 7,
}
