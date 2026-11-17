// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using System.IO;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

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
/// The grammar this walk accepts is the one <c>Microsoft.NET.WebAssembly.Binary.ModuleReader.ReadInitExpr</c> decodes
/// and <c>Microsoft.NET.WebAssembly.Validation.ConstantExpressionValidator</c> admits, opcode for opcode. Refusing less
/// than the reader is over-acceptance; refusing <em>more</em> is a valid-input rejection, and that
/// is the shape the <c>0xFB</c> GC prefix took — <c>wasm-tools 1.253.0</c> validates
/// <c>(table 1 i31ref (ref.i31 (i32.const 1)))</c> and nesm read it back, but the linker then
/// refused to re-emit an instruction its own reader had just accepted (GitHub #383).
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
    public static byte[] Rewrite(byte[] expr, Func<OperandKind, long, long> map)
    {
        ArgumentNullException.ThrowIfNull(expr);
        ArgumentNullException.ThrowIfNull(map);

        if (expr.Length == 0)
            return [];

        using var output = new MemoryStream(expr.Length + 8);
        int offset = 0;
        while (offset < expr.Length)
        {
            byte opcode = expr[offset++];
            output.WriteByte(opcode);

            switch (opcode)
            {
                case 0x0B:
                    return output.ToArray();
                case 0x41:
                case 0x42:
                    CopyLebImmediate(expr, ref offset, output);
                    break;
                case 0xD0:
                {
                    int immediateStart = offset;
                    long original;
                    try
                    {
                        original = S33Leb128.Read(expr, ref offset);
                    }
                    catch (WasmFormatException ex)
                    {
                        throw new LinkerException(ex.Message, ex);
                    }

                    long rewritten = map(OperandKind.HeapType, original);
                    if (rewritten == original)
                    {
                        output.Write(expr, immediateStart, offset - immediateStart);
                    }
                    else
                    {
                        if (!S33Leb128.IsInRange(rewritten))
                            throw new LinkerException($"Heap type {rewritten} is outside the signed 33-bit range.");
                        WasmEncoder.WriteS33Leb(output, rewritten);
                    }
                    break;
                }
                case 0x43:
                    CopyFixed(expr, ref offset, output, 4, "f32.const");
                    break;
                case 0x44:
                    CopyFixed(expr, ref offset, output, 8, "f64.const");
                    break;
                case 0x23:
                    WasmEncoder.WriteU32Leb(
                        output,
                        ToIndex(map(OperandKind.GlobalIndex, ReadU32Leb(expr, ref offset))));
                    break;
                case 0xD2:
                    WasmEncoder.WriteU32Leb(
                        output,
                        ToIndex(map(OperandKind.FunctionIndex, ReadU32Leb(expr, ref offset))));
                    break;
                case 0xFD:
                {
                    // The reader admits exactly one SIMD constant expression, `v128.const`
                    // (sub-opcode 12), and copying the 16-byte payload after any other sub-opcode
                    // would resynchronise the walk on operand bytes. Refuse what the reader refuses.
                    uint simd = ReadU32Leb(expr, ref offset);
                    if (simd != 0x0C)
                    {
                        throw new LinkerException(
                            $"Constant-expression SIMD sub-opcode 0x{simd:X} is not v128.const (0x0C), which is the " +
                            "only SIMD instruction permitted in a constant expression.");
                    }

                    WasmEncoder.WriteU32Leb(output, simd);
                    CopyFixed(expr, ref offset, output, 16, "SIMD constant");
                    break;
                }

                case 0xFB:
                    RewriteGcConstructor(expr, ref offset, output, map);
                    break;
                case 0x6A:
                case 0x6B:
                case 0x6C:
                case 0x7C:
                case 0x7D:
                case 0x7E:
                    break;
                default:
                    throw new LinkerException(
                        $"Unsupported constant-expression opcode 0x{opcode:X2} while remapping merged sections.");
            }
        }

        // Falling out of the loop means the bytes ran out before `end`. Returning what has been
        // accumulated so far would hand back a shorter expression that is still structurally
        // plausible — a wrong answer rather than a refusal, and one no caller could detect.
        throw new LinkerException(
            "Constant expression ends without an `end` opcode (0x0B); the bytes run out first.");
    }

    /// <summary>
    /// Rewrites the immediates of a <c>0xFB</c>-prefixed GC constant expression instruction.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The sub-opcode set is the one <c>ModuleReader.ReadInitExpr</c> decodes and
    /// <c>ConstantExpressionValidator.ApplyGc</c> admits — <c>struct.new</c>,
    /// <c>struct.new_default</c>, <c>array.new</c>, <c>array.new_default</c>,
    /// <c>array.new_fixed</c>, <c>any.convert_extern</c>, <c>extern.convert_any</c> and
    /// <c>ref.i31</c>. Anything else is refused, matching the reader rather than widening past it.
    /// </para>
    /// <para>
    /// The linker rejecting the whole prefix was a valid-input rejection with an external witness:
    /// <c>wasm-tools 1.253.0</c> validates <c>(table 1 i31ref (ref.i31 (i32.const 1)))</c>, nesm's
    /// reader accepts it, and re-emission then failed on a supported opcode (GitHub #383).
    /// </para>
    /// </remarks>
    private static void RewriteGcConstructor(
        byte[] expr, ref int offset, Stream output, Func<OperandKind, long, long> map)
    {
        uint sub = ReadU32Leb(expr, ref offset);
        WasmEncoder.WriteU32Leb(output, sub);

        switch (sub)
        {
            // struct.new, struct.new_default, array.new, array.new_default — one typeidx.
            case 0x00:
            case 0x01:
            case 0x06:
            case 0x07:
                WasmEncoder.WriteU32Leb(
                    output,
                    ToIndex(map(OperandKind.TypeIndex, ReadU32Leb(expr, ref offset))));
                return;

            // array.new_fixed — a typeidx followed by an element COUNT, which is not an index and
            // must not be routed through the map.
            case 0x08:
                WasmEncoder.WriteU32Leb(
                    output,
                    ToIndex(map(OperandKind.TypeIndex, ReadU32Leb(expr, ref offset))));
                CopyLebImmediate(expr, ref offset, output);
                return;

            // any.convert_extern, extern.convert_any, ref.i31 — no immediates.
            case 0x1A:
            case 0x1B:
            case 0x1C:
                return;

            default:
                throw new LinkerException(
                    $"Unsupported constant-expression GC sub-opcode 0x{sub:X} while remapping merged sections.");
        }
    }

    private static uint ToIndex(long value) =>
        value is >= 0 and <= uint.MaxValue
            ? (uint)value
            : throw new LinkerException($"Constant-expression index {value} is outside the u32 index space.");

    private static void CopyFixed(byte[] expr, ref int offset, Stream output, int count, string what)
    {
        if (offset + count > expr.Length)
            throw new LinkerException($"Malformed {what} in const expression.");
        output.Write(expr, offset, count);
        offset += count;
    }

    private static void CopyLebImmediate(byte[] expr, ref int offset, Stream output)
    {
        while (offset < expr.Length)
        {
            byte b = expr[offset++];
            output.WriteByte(b);
            if ((b & 0x80) == 0)
                return;
        }

        throw new LinkerException("Truncated LEB128 immediate in const expression.");
    }

    private static uint ReadU32Leb(byte[] expr, ref int offset)
    {
        uint result = 0;
        int shift = 0;
        while (offset < expr.Length)
        {
            byte b = expr[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
            if (shift >= 35)
                break;
        }

        throw new LinkerException("Truncated u32 LEB128 immediate in const expression.");
    }

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
