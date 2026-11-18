// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Xunit;

/// <summary>
/// <see cref="ConstExpressionWalker"/> holds the one statement of which instructions may appear in a constant
/// expression (#600 P9). These tests check that statement against an independent table, and check that the walk
/// refuses a non-constant instruction before it decodes it.
/// </summary>
/// <remarks>
/// The expected set below is written out from the spec (core 3.0 §3.3.10 with extended-const and GC) rather than
/// derived from the walker: a table can be wrong, and a test that reads its answer from the code under test cannot
/// disagree with it. A corpus of valid modules cannot catch an over-accepting walker either, because it never
/// contains a non-constant instruction in a constant expression, so the sweeps cover every opcode.
/// </remarks>
public class ConstExpressionWalkerTests
{
    private static readonly Dictionary<OpcodeId, ConstOp> SpecConstantInstructions = new()
    {
        [new OpcodeId(0x0B)] = ConstOp.End,
        [new OpcodeId(0x23)] = ConstOp.GlobalGet,
        [new OpcodeId(0x41)] = ConstOp.I32Const,
        [new OpcodeId(0x42)] = ConstOp.I64Const,
        [new OpcodeId(0x43)] = ConstOp.F32Const,
        [new OpcodeId(0x44)] = ConstOp.F64Const,
        [new OpcodeId(0x6A)] = ConstOp.I32Add,
        [new OpcodeId(0x6B)] = ConstOp.I32Sub,
        [new OpcodeId(0x6C)] = ConstOp.I32Mul,
        [new OpcodeId(0x7C)] = ConstOp.I64Add,
        [new OpcodeId(0x7D)] = ConstOp.I64Sub,
        [new OpcodeId(0x7E)] = ConstOp.I64Mul,
        [new OpcodeId(0xD0)] = ConstOp.RefNull,
        [new OpcodeId(0xD2)] = ConstOp.RefFunc,
        [new OpcodeId(0xFD, 0x0C)] = ConstOp.V128Const,
        [new OpcodeId(0xFB, 0x00)] = ConstOp.StructNew,
        [new OpcodeId(0xFB, 0x01)] = ConstOp.StructNewDefault,
        [new OpcodeId(0xFB, 0x06)] = ConstOp.ArrayNew,
        [new OpcodeId(0xFB, 0x07)] = ConstOp.ArrayNewDefault,
        [new OpcodeId(0xFB, 0x08)] = ConstOp.ArrayNewFixed,
        [new OpcodeId(0xFB, 0x1A)] = ConstOp.AnyConvertExtern,
        [new OpcodeId(0xFB, 0x1B)] = ConstOp.ExternConvertAny,
        [new OpcodeId(0xFB, 0x1C)] = ConstOp.RefI31,
    };

    private static readonly byte[] Prefixes = [0xE0, 0xFB, 0xFC, 0xFD, 0xFE];

    [Fact]
    public void Classification_MatchesTheSpecConstantSet_OverEveryOpcodeAndSubOpcode()
    {
        var wrong = new List<string>();
        var seen = new HashSet<OpcodeId>();
        for (int b = 0; b <= 0xFF; b++)
        {
            bool prefixed = Array.IndexOf(Prefixes, (byte)b) >= 0;
            uint lastSub = prefixed ? 0x140u : 0u;
            for (uint sub = 0; sub <= lastSub; sub++)
            {
                var id = new OpcodeId((byte)b, sub);
                ConstOp expected = SpecConstantInstructions.GetValueOrDefault(id, ConstOp.NotConstant);
                ConstOp actual = ConstExpressionWalker.Classify((byte)b, sub);
                if (actual != expected)
                    wrong.Add($"{id}: expected {expected}, got {actual}");
                if (actual != ConstOp.NotConstant)
                    seen.Add(id);
            }
        }

        Assert.Empty(wrong);
        // Not vacuous: every constant instruction was reached by the sweep.
        Assert.Equal(SpecConstantInstructions.Count, seen.Count);
    }

    /// <summary>
    /// A non-constant instruction stops the walk where it starts, and is not decoded: a truncated or reserved
    /// immediate after it does not turn the answer into a decode error.
    /// </summary>
    [Theory]
    [InlineData("local.get with a truncated index", new byte[] { 0x20, 0x80 }, 0, 0x20, 0u)]
    [InlineData("global.set", new byte[] { 0x24, 0x00, 0x0B }, 0, 0x24, 0u)]
    [InlineData("ref.is_null after ref.null", new byte[] { 0xD0, 0x70, 0xD1, 0x0B }, 2, 0xD1, 0u)]
    [InlineData("memory.size", new byte[] { 0x3F, 0x00, 0x0B }, 0, 0x3F, 0u)]
    [InlineData("i32.div_s after two operands", new byte[] { 0x41, 0x01, 0x41, 0x02, 0x6D, 0x0B }, 4, 0x6D, 0u)]
    [InlineData("array.new_data", new byte[] { 0xFB, 0x09, 0x00, 0x00, 0x0B }, 0, 0xFB, 9u)]
    [InlineData("i8x16.shuffle", new byte[] { 0xFD, 0x0D, 0x00 }, 0, 0xFD, 0x0Du)]
    [InlineData("memory.init (0xFC prefix)", new byte[] { 0xFC, 0x08, 0x00, 0x00, 0x0B }, 0, 0xFC, 8u)]
    [InlineData("unknown GC sub-opcode", new byte[] { 0xFB, 0x7F, 0x0B }, 0, 0xFB, 0x7Fu)]
    [InlineData("reserved opcode 0x27", new byte[] { 0x27, 0x0B }, 0, 0x27, 0u)]
    public void NonConstantInstruction_StopsTheWalkBeforeDecoding(
        string name, byte[] expression, int position, byte opcode, uint subOpcode)
    {
        _ = name;
        var walker = new ConstExpressionWalker(expression);
        ConstOp op;
        DecodedInstruction instruction;
        do
        {
            Assert.True(walker.MoveNext(out op, out instruction));
        }
        while (op != ConstOp.NotConstant);

        Assert.Equal(position, walker.Position);
        Assert.Equal(position, instruction.ByteOffset);
        Assert.Equal(new OpcodeId(opcode, subOpcode), instruction.OpcodeId);

        // ImmediateOffset means what InstructionCursor means by it: the byte after the primary opcode, which for a
        // prefixed instruction is its sub-opcode.
        Assert.Equal(position + 1, instruction.ImmediateOffset);
    }

    [Fact]
    public void AfterANonConstantInstruction_TheWalkRefusesToContinue()
    {
        var walker = new ConstExpressionWalker([0x20, 0x00, 0x0B]);
        Assert.True(walker.MoveNext(out ConstOp op, out _));
        Assert.Equal(ConstOp.NotConstant, op);

        bool threw = false;
        try
        {
            walker.MoveNext(out _, out _);
        }
        catch (InvalidOperationException)
        {
            threw = true;
        }

        Assert.True(threw);
    }

    [Fact]
    public void ConstantInstructionsAreDecodedWithTheirImmediates()
    {
        byte[] expression = [0x23, 0x05, 0x41, 0x7F, 0x6A, 0xFB, 0x08, 0x03, 0x02, 0x0B];
        var walker = new ConstExpressionWalker(expression);
        var ops = new List<(ConstOp, uint, uint)>();
        while (walker.MoveNext(out ConstOp op, out DecodedInstruction instruction))
        {
            ops.Add((op, instruction.Index0, instruction.Index1));
            if (op == ConstOp.End)
                break;
        }

        Assert.Equal(
            [
                (ConstOp.GlobalGet, 5u, 0u),
                (ConstOp.I32Const, unchecked((uint)-1), 0u),
                (ConstOp.I32Add, 0u, 0u),
                (ConstOp.ArrayNewFixed, 3u, 2u),
                (ConstOp.End, 0u, 0u),
            ],
            ops);
        Assert.Equal(expression.Length, walker.Position);
    }

    [Fact]
    public void BytesRunningOutBeforeEnd_EndTheWalkWithoutAnError()
    {
        var walker = new ConstExpressionWalker([0x41, 0x01]);
        Assert.True(walker.MoveNext(out ConstOp op, out _));
        Assert.Equal(ConstOp.I32Const, op);
        Assert.False(walker.MoveNext(out _, out _));
        Assert.Equal(2, walker.Position);
    }

    /// <summary>
    /// A malformed immediate is reported where the instruction decoder stopped, in absolute coordinates: for an
    /// over-long or unused-bit LEB that is the LEB's first byte, as <c>WasmSpanReader</c> reported it before the walk
    /// was shared.
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0x41, 0x80 }, "unexpected end of section or function", 101)]
    [InlineData(new byte[] { 0x44, 0x00, 0x00 }, "unexpected end of section or function", 101)]
    [InlineData(new byte[] { 0xFD, 0x0C, 0x00 }, "unexpected end of section or function", 102)]
    [InlineData(new byte[] { 0xFB }, "unexpected end of section or function", 101)]
    [InlineData(new byte[] { 0x41, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B }, null, 101)]
    [InlineData(new byte[] { 0x42, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B }, null, 101)]
    [InlineData(new byte[] { 0x23, 0xFF, 0xFF, 0xFF, 0xFF, 0x7F, 0x0B }, null, 101)]
    public void MalformedConstantInstruction_ReportsWhereTheDecoderStopped(byte[] expression, string? message, long offset)
    {
        WasmFormatException? ex = null;
        try
        {
            var walker = new ConstExpressionWalker(expression, baseOffset: 100);
            walker.MoveNext(out _, out _);
        }
        catch (WasmFormatException caught)
        {
            ex = caught;
        }

        Assert.NotNull(ex);
        Assert.IsType<WasmFormatException>(ex);
        Assert.Equal(message ?? CursorMessage(expression), ex.Message);
        Assert.Equal(offset, ex.BinaryOffset);
    }

    /// <summary>
    /// <c>binary-leb128.wast</c>'s over-long <c>i32.const</c> in a global initializer: the opcode is at 13 and the
    /// LEB at 14. The module reader reported 14 before the walk was shared and must still.
    /// </summary>
    [Fact]
    public void SpecBinaryLeb128_44_ReportsTheOffsetOfTheOverlongLeb()
    {
        byte[] module =
        [
            0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00,
            0x06, 0x0B, 0x01, 0x7F, 0x00,
            0x41, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B,
        ];

        var ex = Assert.ThrowsAny<WasmFormatException>(() => ModuleReader.ReadModule(new MemoryStream(module)));

        Assert.Equal(14, ex.BinaryOffset);
    }

    /// <summary>
    /// A prefix whose sub-opcode cannot be read is malformed input, and is reported as the decoder reports it in a
    /// function body, for every prefix — not as "sub-opcode 0 is not constant".
    /// </summary>
    [Theory]
    [InlineData(new byte[] { 0xFC, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B })]
    [InlineData(new byte[] { 0xFE, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B })]
    [InlineData(new byte[] { 0xE0, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B })]
    [InlineData(new byte[] { 0xFB, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B })]
    [InlineData(new byte[] { 0xFD, 0x80, 0x80, 0x80, 0x80, 0x80, 0x00, 0x0B })]
    [InlineData(new byte[] { 0xFC, 0x80 })]
    public void UnreadableSubOpcode_IsReportedAsTheDecoderReportsIt(byte[] expression)
    {
        WasmFormatException? ex = null;
        try
        {
            var walker = new ConstExpressionWalker(expression, baseOffset: 100);
            walker.MoveNext(out _, out _);
        }
        catch (WasmFormatException caught)
        {
            ex = caught;
        }

        Assert.NotNull(ex);
        Assert.Equal(SubOpcodeMessage(expression), ex.Message);
        Assert.Equal(101, ex.BinaryOffset);
    }

    private static string CursorMessage(byte[] code)
    {
        try
        {
            var cursor = new InstructionCursor(code, WasmFeatures.All);
            cursor.MoveNext(out _);
        }
        catch (WasmFormatException ex)
        {
            return ex.Message;
        }

        throw new InvalidOperationException("The decoder accepted the instruction.");
    }

    /// <summary>The decoder's message for the sub-opcode LEB alone, as read by a prefix it decodes (0xFC).</summary>
    private static string SubOpcodeMessage(byte[] code) => CursorMessage([0xFC, .. code.AsSpan(1)]);

    [Theory]
    [InlineData(new byte[] { 0x41, 0x05, 0x0B }, true, (int)ConstOp.I32Const)]
    [InlineData(new byte[] { 0x42, 0x81, 0x80, 0x00, 0x0B }, true, (int)ConstOp.I64Const)]
    [InlineData(new byte[] { 0xD2, 0x03, 0x0B }, true, (int)ConstOp.RefFunc)]
    [InlineData(new byte[] { 0x41, 0x05 }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { 0x41, 0x05, 0x0B, 0x0B }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { 0x41, 0x01, 0x41, 0x02, 0x6A, 0x0B }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { 0x20, 0x00, 0x0B }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { 0x41, 0x80 }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { 0x0B }, false, (int)ConstOp.NotConstant)]
    [InlineData(new byte[] { }, false, (int)ConstOp.NotConstant)]
    public void TryReadSingle_AcceptsExactlyOneConstantInstructionThenEnd(byte[] expression, bool single, int expected)
    {
        Assert.Equal(single, ConstExpressionWalker.TryReadSingle(expression, out ConstOp op, out _));
        Assert.Equal((ConstOp)expected, op);
    }
}
