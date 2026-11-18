// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Buffers;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// The single walk over the constant-expression grammar used by the linker.
/// </summary>
/// <remarks>
/// <para>
/// Three passes need to look inside a constant expression: dead-code elimination has to know which
/// functions, globals and types it names so they survive the sweep; the merge has to rewrite those
/// indices into the merged spaces; and the writer has to normalise the abstract heap-type codes it
/// carries. Written three times, those walks disagree — the writer normalised a legacy <c>cont</c>
/// code in a table's element type but not in the <c>ref.null</c> sitting beside it, and emitted a
/// module wasm-tools rejects while nesm's own reader accepted it (GitHub #383).
/// </para>
/// <para>
/// So there is one walk. <see cref="Scan"/> is defined in terms of <see cref="Rewrite"/> rather
/// than repeating the opcode table, which means a grammar gap cannot be present in one direction
/// and absent in the other. An unknown opcode is refused in both directions: copying it through is
/// how a writer emits bytes no validator accepts while reporting success.
/// </para>
/// <para>
/// The grammar is not restated here: the instructions come from
/// <see cref="ConstExpressionWalker"/>, which the reader, the validator, the evaluator and the
/// disassembler also walk (#600 P9), so this walk accepts exactly what the reader decodes. It used
/// to carry its own opcode table, and that is how it once refused the whole <c>0xFB</c> GC prefix
/// — <c>wasm-tools 1.253.0</c> validates <c>(table 1 i31ref (ref.i31 (i32.const 1)))</c> and nesm
/// read it back, but the linker then refused to re-emit an instruction its own reader had just
/// accepted (GitHub #383).
/// </para>
/// </remarks>
internal static class ConstExprScanner
{
    /// <summary>What an operand immediate names.</summary>
    internal enum OperandKind
    {
        /// <summary><c>ref.func</c> — a function index.</summary>
        FunctionIndex,

        /// <summary><c>global.get</c> — a global index.</summary>
        GlobalIndex,

        /// <summary>
        /// <c>ref.null</c> — an s33 heap type. Non-negative values are defined type indices;
        /// negative values are abstract codes in their low seven bits.
        /// </summary>
        HeapType,

        /// <summary>
        /// The defined-type index a GC constructor names — <c>struct.new $t</c>,
        /// <c>struct.new_default $t</c>, <c>array.new $t</c>, <c>array.new_default $t</c>,
        /// <c>array.new_fixed $t n</c>.
        /// </summary>
        /// <remarks>
        /// Always a <c>typeidx</c>, never an s33 heap type, so it is a separate kind from
        /// <see cref="HeapType"/>: a caller that normalises abstract codes must not be handed one of
        /// these, and a caller that resolves type indices must not have to re-derive the sign
        /// convention. <c>array.new_fixed</c>'s <em>second</em> immediate is an element count, not an
        /// index, and is copied through untouched.
        /// </remarks>
        TypeIndex,
    }

    /// <summary>Visits every index-carrying operand without changing the expression.</summary>
    public static void Scan(byte[] expr, Action<OperandKind, long> visit)
    {
        ArgumentNullException.ThrowIfNull(visit);
        _ = Rewrite(expr, (kind, value) =>
        {
            visit(kind, value);
            return value;
        });
    }

    /// <summary>
    /// Rewrites every index-carrying operand through <paramref name="map"/>, copying the rest of the
    /// expression through unchanged.
    /// </summary>
    /// <remarks>
    /// The instructions come from <see cref="ConstExpressionWalker"/>, which owns the grammar; this method owns only
    /// the encoding policy. Indices and sub-opcodes are written at their minimal width; constants, the
    /// <c>array.new_fixed</c> count and an unchanged <c>ref.null</c> heap type are copied byte for byte.
    /// </remarks>
    public static byte[] Rewrite(byte[] expr, Func<OperandKind, long, long> map)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(map);

        if (expr.Length == 0)
            return [];

        // An instruction of n >= 2 bytes grows by at most four bytes per LEB it re-encodes (to the five-byte maximum),
        // so no instruction more than triples; `end` is copied.
        byte[] buffer = ArrayPool<byte>.Shared.Rent(checked((expr.Length * 3) + 16));
        try
        {
            Span<byte> output = buffer;
            int written = 0;
            var walker = new ConstExpressionWalker(expr);
            try
            {
                while (walker.MoveNext(out ConstOp op, out DecodedInstruction instruction))
                {
                    if (op == ConstOp.NotConstant)
                        throw NotConstant(instruction.OpcodeId);

                    byte opcode = instruction.OpcodeId.Opcode;
                    output[written++] = opcode;
                    ReadOnlySpan<byte> immediates = instruction.ImmediateBytes;
                    switch (op)
                    {
                        case ConstOp.End:
                            return output[..written].ToArray();
                        case ConstOp.I32Const:
                        case ConstOp.I64Const:
                        case ConstOp.F32Const:
                        case ConstOp.F64Const:
                            immediates.CopyTo(output[written..]);
                            written += immediates.Length;
                            break;
                        case ConstOp.RefNull:
                        {
                            long original = instruction.HeapType0;
                            long rewritten = map(OperandKind.HeapType, original);
                            if (rewritten == original)
                            {
                                immediates.CopyTo(output[written..]);
                                written += immediates.Length;
                            }
                            else
                            {
                                if (!WasmLeb128.IsS33(rewritten))
                                    throw new LinkerException($"Heap type {rewritten} is outside the signed 33-bit range.");
                                written += WasmLeb128.WriteSLeb128(output[written..], rewritten);
                            }

                            break;
                        }

                        case ConstOp.GlobalGet:
                            written += WasmLeb128.WriteULeb128(output[written..], ToIndex(map(OperandKind.GlobalIndex, instruction.Index0)));
                            break;
                        case ConstOp.RefFunc:
                            written += WasmLeb128.WriteULeb128(output[written..], ToIndex(map(OperandKind.FunctionIndex, instruction.Index0)));
                            break;
                        case ConstOp.V128Const:
                            written += WasmLeb128.WriteULeb128(output[written..], instruction.OpcodeId.SubOpcode);
                            immediates[^16..].CopyTo(output[written..]);
                            written += 16;
                            break;
                        case ConstOp.StructNew:
                        case ConstOp.StructNewDefault:
                        case ConstOp.ArrayNew:
                        case ConstOp.ArrayNewDefault:
                            written += WasmLeb128.WriteULeb128(output[written..], instruction.OpcodeId.SubOpcode);
                            written += WasmLeb128.WriteULeb128(output[written..], ToIndex(map(OperandKind.TypeIndex, instruction.Index0)));
                            break;
                        case ConstOp.ArrayNewFixed:
                        {
                            // A typeidx followed by an element COUNT, which is not an index and must not be routed
                            // through the map: it is copied as it was written.
                            written += WasmLeb128.WriteULeb128(output[written..], instruction.OpcodeId.SubOpcode);
                            written += WasmLeb128.WriteULeb128(output[written..], ToIndex(map(OperandKind.TypeIndex, instruction.Index0)));
                            int countStart = 0;
                            WasmLeb128.TrySkip(immediates, ref countStart);
                            WasmLeb128.TrySkip(immediates, ref countStart);
                            ReadOnlySpan<byte> count = immediates[countStart..];
                            count.CopyTo(output[written..]);
                            written += count.Length;
                            break;
                        }

                        case ConstOp.AnyConvertExtern:
                        case ConstOp.ExternConvertAny:
                        case ConstOp.RefI31:
                            written += WasmLeb128.WriteULeb128(output[written..], instruction.OpcodeId.SubOpcode);
                            break;
                        case ConstOp.I32Add:
                        case ConstOp.I32Sub:
                        case ConstOp.I32Mul:
                        case ConstOp.I64Add:
                        case ConstOp.I64Sub:
                        case ConstOp.I64Mul:
                            break;
                        default:
                            throw new LinkerException($"Constant-expression instruction {op} has no rewrite policy.");
                    }
                }
            }
            catch (WasmFormatException ex)
            {
                throw new LinkerException($"Malformed constant expression: {ex.Message}", ex);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        // Falling out of the loop means the bytes ran out before `end`. Returning what has been
        // accumulated so far would hand back a shorter expression that is still structurally
        // plausible — a wrong answer rather than a refusal, and one no caller could detect.
        throw new LinkerException(
            "Constant expression ends without an `end` opcode (0x0B); the bytes run out first.");
    }

    private static LinkerException NotConstant(OpcodeId opcode) => opcode.Opcode switch
    {
        // The reader admits exactly one SIMD constant expression, `v128.const`; anything else is refused rather than
        // copied, because copying it would emit bytes no validator accepts.
        WasmOpcode.SimdPrefix => new LinkerException(
            $"Constant-expression SIMD sub-opcode 0x{opcode.SubOpcode:X} is not v128.const (0x0C), which is the " +
            "only SIMD instruction permitted in a constant expression."),
        WasmOpcode.GcPrefix => new LinkerException(
            $"Unsupported constant-expression GC sub-opcode 0x{opcode.SubOpcode:X} while remapping merged sections."),
        _ => new LinkerException(
            $"Unsupported constant-expression opcode 0x{opcode.Opcode:X2} while remapping merged sections."),
    };

    private static uint ToIndex(long value) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new LinkerException($"Constant-expression index {value} is outside the u32 index space.");

    /// <summary>
    /// Maps a negative s33 heap-type immediate onto the canonical code for the kind it denotes.
    /// </summary>
    /// <remarks>
    /// Derived from <see cref="AbstractHeapTypeCodes"/> rather than restating which codes are legacy,
    /// so the expression and the type positions around it cannot end up on different policies. An
    /// unrecognised code is refused: the reader would not have produced it, so writing it out would
    /// emit a module no validator accepts while reporting success.
    /// </remarks>
    public static long CanonicalAbstractHeapType(long heapType)
    {
        if (heapType >= 0)
            return heapType;

        byte raw = (byte)(heapType & 0x7F);
        if (!AbstractHeapTypeCodes.TryFromCode(raw, out var kind) ||
            !AbstractHeapTypeCodes.TryToCode(kind, out byte canonical))
        {
            throw new LinkerException(
                $"ref.null names abstract heap type code 0x{raw:X2}, which is not a heap type this writer " +
                "recognises. Copying it through would emit a module that no validator accepts (GitHub #383).");
        }

        return canonical - 0x80;
    }
}
