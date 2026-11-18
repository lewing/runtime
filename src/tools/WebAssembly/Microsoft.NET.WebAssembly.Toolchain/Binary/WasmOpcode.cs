// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// WebAssembly opcode bytes: the prefix bytes of multi-byte instructions and the single-byte opcodes that code
/// outside the interpreters' dispatch switches matches by value (control flow, calls, locals, constants).
/// Constants rather than an enum, because they are compared with raw instruction bytes everywhere. The opcodes are
/// <see cref="WasmOp"/>'s values, so there is one definition; the prefixes are bytes no <see cref="WasmOp"/> names alone.
/// </summary>
internal static class WasmOpcode
{
    public const byte Unreachable = (byte)WasmOp.Unreachable;
    public const byte Nop = (byte)WasmOp.Nop;
    public const byte Block = (byte)WasmOp.Block;
    public const byte Loop = (byte)WasmOp.Loop;
    public const byte If = (byte)WasmOp.If;
    public const byte Else = (byte)WasmOp.Else;
    public const byte Try = (byte)WasmOp.Try;
    public const byte Catch = (byte)WasmOp.Catch;
    public const byte Throw = (byte)WasmOp.Throw;
    public const byte Rethrow = (byte)WasmOp.Rethrow;
    public const byte ThrowRef = (byte)WasmOp.ThrowRef;
    public const byte End = (byte)WasmOp.End;
    public const byte Br = (byte)WasmOp.Br;
    public const byte BrIf = (byte)WasmOp.BrIf;
    public const byte BrTable = (byte)WasmOp.BrTable;
    public const byte Return = (byte)WasmOp.Return;
    public const byte Call = (byte)WasmOp.Call;
    public const byte CallIndirect = (byte)WasmOp.CallIndirect;
    public const byte ReturnCall = (byte)WasmOp.ReturnCall;
    public const byte ReturnCallIndirect = (byte)WasmOp.ReturnCallIndirect;
    public const byte CallRef = (byte)WasmOp.CallRef;
    public const byte ReturnCallRef = (byte)WasmOp.ReturnCallRef;
    public const byte Delegate = (byte)WasmOp.Delegate;
    public const byte CatchAll = (byte)WasmOp.CatchAll;
    public const byte Drop = (byte)WasmOp.Drop;
    public const byte Select = (byte)WasmOp.Select;
    public const byte SelectTyped = (byte)WasmOp.SelectTyped;
    public const byte TryTable = (byte)WasmOp.TryTable;
    public const byte LocalGet = (byte)WasmOp.LocalGet;
    public const byte LocalSet = (byte)WasmOp.LocalSet;
    public const byte LocalTee = (byte)WasmOp.LocalTee;
    public const byte GlobalGet = (byte)WasmOp.GlobalGet;
    public const byte GlobalSet = (byte)WasmOp.GlobalSet;
    public const byte I32Const = (byte)WasmOp.I32Const;
    public const byte I64Const = (byte)WasmOp.I64Const;
    public const byte F32Const = (byte)WasmOp.F32Const;
    public const byte F64Const = (byte)WasmOp.F64Const;
    public const byte I32Add = (byte)WasmOp.I32Add;
    public const byte I32Sub = (byte)WasmOp.I32Sub;
    public const byte I32Mul = (byte)WasmOp.I32Mul;
    public const byte I64Add = (byte)WasmOp.I64Add;
    public const byte I64Sub = (byte)WasmOp.I64Sub;
    public const byte I64Mul = (byte)WasmOp.I64Mul;
    public const byte RefNull = (byte)WasmOp.RefNull;
    public const byte RefIsNull = (byte)WasmOp.RefIsNull;
    public const byte RefFunc = (byte)WasmOp.RefFunc;
    public const byte BrOnNull = (byte)WasmOp.BrOnNull;
    public const byte BrOnNonNull = (byte)WasmOp.BrOnNonNull;

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
