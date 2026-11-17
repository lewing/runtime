// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>WebAssembly value types (§2.3.1)</summary>
/// <remarks>
/// <para>
/// The numeric values are the <em>binary</em> encoding, not an arbitrary discriminant: every writer
/// in the WAT pipeline emits <c>(byte)</c> of this enum directly. So a wrong constant here is a
/// wrong byte in the output, and the module nesm produces is one no other tool will read.
/// </para>
/// <para>
/// <c>ContRef</c> was <c>0x65</c>, the pre-renumbering stack-switching code. Reading it is still
/// accepted — <see cref="AbstractHeapTypeCodes.ContLegacy"/>, and both
/// <c>ModuleReader.ReadValTypeInfo</c> arms list it — but writing it produced a binary that
/// <c>wasm-tools 1.253.0</c> rejects outright with "malformed reference type", so a
/// <c>contref</c> table or block result was nesm-only output. The constant is now taken from
/// <see cref="AbstractHeapTypeCodes"/>, which is the repository's single decode-side mapping, so
/// the two directions cannot drift again.
/// </para>
/// <para>
/// This is the <b>core</b> wasm type-code space. The component model's <c>0x65</c>
/// (<c>future</c>, <c>ComponentReader</c>) is a different space entirely and is untouched.
/// </para>
/// </remarks>
public enum ValType : byte
{
    I32     = 0x7F,
    I64     = 0x7E,
    F32     = 0x7D,
    F64     = 0x7C,
    V128    = 0x7B,
    FuncRef = AbstractHeapTypeCodes.Func,
    ExternRef = AbstractHeapTypeCodes.Extern,
    ExnRef = AbstractHeapTypeCodes.Exn,
    ContRef = AbstractHeapTypeCodes.Cont,
}
