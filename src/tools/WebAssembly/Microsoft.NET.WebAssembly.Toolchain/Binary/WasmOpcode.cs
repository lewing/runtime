// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// WebAssembly opcode bytes: the prefix bytes of multi-byte instructions and the single-byte opcodes that code
/// outside the interpreters' dispatch switches matches by value (control flow, calls, locals, constants).
/// Constants rather than an enum, because they are compared with raw instruction bytes everywhere.
/// </summary>
internal static class WasmOpcode
{
    public const byte Unreachable = 0x00;
    public const byte Nop = 0x01;
    public const byte Block = 0x02;
    public const byte Loop = 0x03;
    public const byte If = 0x04;
    public const byte Else = 0x05;
    public const byte Try = 0x06;
    public const byte Catch = 0x07;
    public const byte Throw = 0x08;
    public const byte Rethrow = 0x09;
    public const byte ThrowRef = 0x0A;
    public const byte End = 0x0B;
    public const byte Br = 0x0C;
    public const byte BrIf = 0x0D;
    public const byte BrTable = 0x0E;
    public const byte Return = 0x0F;
    public const byte Call = 0x10;
    public const byte CallIndirect = 0x11;
    public const byte ReturnCall = 0x12;
    public const byte ReturnCallIndirect = 0x13;
    public const byte CallRef = 0x14;
    public const byte ReturnCallRef = 0x15;
    public const byte Delegate = 0x18;
    public const byte CatchAll = 0x19;
    public const byte Drop = 0x1A;
    public const byte Select = 0x1B;
    public const byte SelectTyped = 0x1C;
    public const byte TryTable = 0x1F;
    public const byte LocalGet = 0x20;
    public const byte LocalSet = 0x21;
    public const byte LocalTee = 0x22;
    public const byte GlobalGet = 0x23;
    public const byte GlobalSet = 0x24;
    public const byte I32Const = 0x41;
    public const byte I64Const = 0x42;
    public const byte F32Const = 0x43;
    public const byte F64Const = 0x44;
    public const byte I32Add = 0x6A;
    public const byte I32Sub = 0x6B;
    public const byte I32Mul = 0x6C;
    public const byte I64Add = 0x7C;
    public const byte I64Sub = 0x7D;
    public const byte I64Mul = 0x7E;
    public const byte RefNull = 0xD0;
    public const byte RefIsNull = 0xD1;
    public const byte RefFunc = 0xD2;
    public const byte BrOnNull = 0xD5;
    public const byte BrOnNonNull = 0xD6;

    /// <summary>Stack-switching instructions (<c>cont.*</c>, <c>resume</c>, <c>suspend</c>, ...), as nesm decodes them.</summary>
    public const byte StackSwitchingPrefix = 0xE0;

    /// <summary>GC instructions (<c>struct.*</c>, <c>array.*</c>, <c>ref.test</c>, ...).</summary>
    public const byte GcPrefix = 0xFB;

    /// <summary>Saturating truncation, bulk memory and table instructions.</summary>
    public const byte MiscPrefix = 0xFC;

    /// <summary>SIMD (and relaxed SIMD) instructions.</summary>
    public const byte SimdPrefix = 0xFD;

    /// <summary>Threads (atomic) instructions.</summary>
    public const byte AtomicPrefix = 0xFE;
}
