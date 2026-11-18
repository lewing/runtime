// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// The instructions a constant expression may contain (§3.3.10, with extended-const and GC), plus
/// <see cref="End"/> and <see cref="NotConstant"/> for everything else.
/// </summary>
internal enum ConstOp : byte
{
    /// <summary>Any instruction a constant expression may not contain, including unknown and reserved opcodes.</summary>
    NotConstant,
    End,
    I32Const,
    I64Const,
    F32Const,
    F64Const,
    V128Const,
    GlobalGet,
    RefNull,
    RefFunc,
    I32Add,
    I32Sub,
    I32Mul,
    I64Add,
    I64Sub,
    I64Mul,
    StructNew,
    StructNewDefault,
    ArrayNew,
    ArrayNewDefault,
    ArrayNewFixed,
    AnyConvertExtern,
    ExternConvertAny,
    RefI31,
}

/// <summary>
/// The one walk over the constant-expression grammar (#600 P9). Every consumer of a constant expression — the module
/// reader, the validator, the evaluator that runs at instantiation, the linker's scan and rewrite, the disassembler
/// and the single-instruction matchers — reads it through this type and supplies only its own handling of each
/// instruction.
/// </summary>
/// <remarks>
/// <para>
/// Each instruction is classified before it is decoded. <see cref="Classify"/> looks only at the opcode and, for the
/// <c>0xFD</c> and <c>0xFB</c> prefixes, the sub-opcode; an instruction outside the grammar is reported as
/// <see cref="ConstOp.NotConstant"/> at its own offset, without decoding its immediates, so a non-constant
/// instruction is always reported as such and never as whatever is wrong with the bytes after it. A constant
/// instruction is then decoded by <see cref="InstructionCursor"/>, which owns the binary grammar of every immediate,
/// so the immediates here are exactly the ones a function body would have.
/// </para>
/// <para>
/// The walk stops at a <see cref="ConstOp.NotConstant"/> instruction: it does not know how long that instruction is,
/// so <see cref="MoveNext"/> throws <see cref="InvalidOperationException"/> if called again. It also stops, returning
/// <see langword="false"/>, when the bytes run out before <see cref="ConstOp.End"/>; what that means (a truncated
/// section, a malformed expression) is the caller's to say. A malformed immediate of a constant instruction, or a
/// prefix sub-opcode that cannot be read, throws <see cref="WasmFormatException"/> with the absolute offset of the
/// byte where decoding stopped.
/// </para>
/// <para>
/// Lives in <c>Microsoft.NET.WebAssembly.Binary</c> with no interpreter dependency, so the toolchain build (which compiles Binary,
/// Validation, Linker and Wat) shares it.
/// </para>
/// </remarks>
internal ref struct ConstExpressionWalker
{
    private const uint V128ConstSubOpcode = (uint)WasmOp.V128Const & 0x00FF_FFFF;

    private readonly ReadOnlySpan<byte> _code;
    private readonly long _baseOffset;
    private InstructionCursor _cursor;
    private bool _stopped;

    /// <param name="code">The expression, or the bytes it begins (the walk stops at its <c>end</c>).</param>
    /// <param name="features">Features the decoder requires of the prefixed instructions it decodes.</param>
    /// <param name="baseOffset">The absolute offset of <paramref name="code"/>'s first byte, for diagnostics.</param>
    public ConstExpressionWalker(ReadOnlySpan<byte> code, WasmFeatures features = WasmFeatures.All, long baseOffset = 0)
    {
        _code = code;
        _baseOffset = baseOffset;
        _cursor = new InstructionCursor(code, features);
        _stopped = false;
    }

    /// <summary>
    /// The offset of the next instruction; after <see cref="ConstOp.End"/>, the length of the expression, and after
    /// <see cref="ConstOp.NotConstant"/>, the offset of that instruction.
    /// </summary>
    public int Position => _cursor.Position;

    /// <summary>
    /// Reads the next instruction. Returns <see langword="false"/> when no bytes remain.
    /// </summary>
    /// <param name="op">The instruction, or <see cref="ConstOp.NotConstant"/>.</param>
    /// <param name="instruction">
    /// The decoded instruction. For <see cref="ConstOp.NotConstant"/> only <see cref="DecodedInstruction.OpcodeId"/>,
    /// <see cref="DecodedInstruction.ByteOffset"/> and <see cref="DecodedInstruction.ImmediateOffset"/> are set, the
    /// last with <see cref="InstructionCursor"/>'s meaning: the byte after the primary opcode, which for a prefixed
    /// instruction is its sub-opcode.
    /// </param>
    public bool MoveNext(out ConstOp op, out DecodedInstruction instruction)
    {
        if (_stopped)
            throw new InvalidOperationException("The constant-expression walk stopped at a non-constant instruction.");

        int start = _cursor.Position;
        if (start >= _code.Length)
        {
            op = ConstOp.NotConstant;
            instruction = default;
            return false;
        }

        byte opcode = _code[start];
        uint subOpcode = 0;
        if (IsPrefix(opcode))
            subOpcode = ReadSubOpcode(start + 1);

        op = Classify(opcode, subOpcode);
        if (op == ConstOp.NotConstant)
        {
            _stopped = true;
            instruction = default;
            instruction.OpcodeId = new OpcodeId(opcode, subOpcode);
            instruction.ByteOffset = start;
            instruction.ImmediateOffset = start + 1;
            return true;
        }

        Decode(out instruction);
        return true;
    }

    /// <summary>
    /// Reads a prefix's sub-opcode. One that cannot be read is malformed input whatever the prefix, and is reported
    /// with the decoder's message for it in a function body, at the sub-opcode.
    /// </summary>
    private readonly uint ReadSubOpcode(int position)
    {
        Leb128Status status = WasmLeb128.DecodeU32(_code[position..], out uint subOpcode, out _);
        if (status == Leb128Status.Ok)
            return subOpcode;
        throw new WasmFormatException(
            status == Leb128Status.Truncated
                ? "unexpected end of section or function"
                : WasmLeb128.CreateMessage(status, WasmLeb128.Leb128Kind.U32),
            _baseOffset + position);
    }

    private void Decode(out DecodedInstruction instruction)
    {
        try
        {
            _cursor.MoveNext(out instruction);
        }
        catch (WasmFormatException ex) when (ex.GetType() == typeof(WasmFormatException) && ex.BinaryOffset is null)
        {
            // Where the decoder stopped: the first byte of a malformed immediate, which is what the section reader
            // reported before the walk was shared.
            throw new WasmFormatException(ex.Message, ex, _baseOffset + _cursor.Position);
        }
    }

    private static bool IsPrefix(byte opcode) =>
        opcode is WasmOpcode.SimdPrefix or WasmOpcode.GcPrefix or WasmOpcode.MiscPrefix
            or WasmOpcode.AtomicPrefix or WasmOpcode.StackSwitchingPrefix;

    /// <summary>
    /// Classifies an instruction by its opcode and, for a prefixed one, its sub-opcode (pass 0 otherwise).
    /// </summary>
    /// <remarks>This is the one statement of the constant-expression grammar.</remarks>
    public static ConstOp Classify(byte opcode, uint subOpcode) => opcode switch
    {
        WasmOpcode.End => ConstOp.End,
        WasmOpcode.I32Const => ConstOp.I32Const,
        WasmOpcode.I64Const => ConstOp.I64Const,
        WasmOpcode.F32Const => ConstOp.F32Const,
        WasmOpcode.F64Const => ConstOp.F64Const,
        WasmOpcode.GlobalGet => ConstOp.GlobalGet,
        WasmOpcode.RefNull => ConstOp.RefNull,
        WasmOpcode.RefFunc => ConstOp.RefFunc,
        WasmOpcode.I32Add => ConstOp.I32Add,
        WasmOpcode.I32Sub => ConstOp.I32Sub,
        WasmOpcode.I32Mul => ConstOp.I32Mul,
        WasmOpcode.I64Add => ConstOp.I64Add,
        WasmOpcode.I64Sub => ConstOp.I64Sub,
        WasmOpcode.I64Mul => ConstOp.I64Mul,
        WasmOpcode.SimdPrefix => subOpcode == V128ConstSubOpcode ? ConstOp.V128Const : ConstOp.NotConstant,
        WasmOpcode.GcPrefix => subOpcode switch
        {
            WasmGcSubOpcode.StructNew => ConstOp.StructNew,
            WasmGcSubOpcode.StructNewDefault => ConstOp.StructNewDefault,
            WasmGcSubOpcode.ArrayNew => ConstOp.ArrayNew,
            WasmGcSubOpcode.ArrayNewDefault => ConstOp.ArrayNewDefault,
            WasmGcSubOpcode.ArrayNewFixed => ConstOp.ArrayNewFixed,
            WasmGcSubOpcode.AnyConvertExtern => ConstOp.AnyConvertExtern,
            WasmGcSubOpcode.ExternConvertAny => ConstOp.ExternConvertAny,
            WasmGcSubOpcode.RefI31 => ConstOp.RefI31,
            _ => ConstOp.NotConstant,
        },
        _ => ConstOp.NotConstant,
    };

    /// <summary>
    /// True when <paramref name="expression"/> is exactly one constant instruction followed by <c>end</c>, with
    /// nothing after it. Never throws: malformed bytes are simply not a single constant.
    /// </summary>
    /// <param name="expression">The whole expression, <c>end</c> included.</param>
    /// <param name="op">The instruction, or <see cref="ConstOp.NotConstant"/> when the result is false.</param>
    /// <param name="instruction">The decoded instruction, valid while <paramref name="expression"/> is.</param>
    public static bool TryReadSingle(ReadOnlySpan<byte> expression, out ConstOp op, out DecodedInstruction instruction)
    {
        var walker = new ConstExpressionWalker(expression);
        try
        {
            if (walker.MoveNext(out op, out instruction)
                && op is not (ConstOp.NotConstant or ConstOp.End)
                && walker.MoveNext(out ConstOp end, out _)
                && end == ConstOp.End
                && walker.Position == expression.Length)
            {
                return true;
            }
        }
        catch (WasmFormatException)
        {
        }

        op = ConstOp.NotConstant;
        instruction = default;
        return false;
    }

    /// <summary>
    /// True when <paramref name="expression"/> is exactly <c>i32.const</c> followed by <c>end</c>; see
    /// <see cref="TryReadSingle"/>.
    /// </summary>
    public static bool TryReadI32Const(ReadOnlySpan<byte> expression, out int value)
    {
        bool single = TryReadSingle(expression, out ConstOp op, out DecodedInstruction instruction) && op == ConstOp.I32Const;
        value = single ? unchecked((int)instruction.Index0) : 0;
        return single;
    }

    /// <summary>
    /// The length of the constant expression <paramref name="code"/> begins with, including its <c>end</c>, or -1 when
    /// it contains a non-constant instruction, is malformed, or runs out before <c>end</c>. Never throws.
    /// </summary>
    public static int TryMeasure(ReadOnlySpan<byte> code)
    {
        var walker = new ConstExpressionWalker(code);
        try
        {
            while (walker.MoveNext(out ConstOp op, out _))
            {
                if (op == ConstOp.End)
                    return walker.Position;
                if (op == ConstOp.NotConstant)
                    return -1;
            }
        }
        catch (WasmFormatException)
        {
        }

        return -1;
    }
}
