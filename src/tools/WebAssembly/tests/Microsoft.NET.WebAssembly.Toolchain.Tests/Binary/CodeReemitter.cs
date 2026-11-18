// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// Re-emits function bodies through <see cref="WasmCodeWriter"/>: each instruction's opcode and boundaries come from
/// <see cref="InstructionCursor"/>, its immediates are parsed here from the original bytes (keeping every LEB128's
/// width), and the writer must reproduce the body byte for byte.
/// </summary>
internal static class CodeReemitter
{
    /// <summary>Re-emits one function body (locals and instructions, without its size prefix).</summary>
    public static byte[] Reemit(ReadOnlySpan<byte> body)
    {
        var output = new ArrayBufferWriter<byte>(body.Length);
        var writer = new WasmCodeWriter(output);
        int at = 0;
        uint groups = ReadU(body, ref at, out _);
        var locals = new WasmLocalGroup[groups];
        for (int i = 0; i < groups; i++)
        {
            uint count = ReadU(body, ref at, out int countWidth);
            Assert.True(countWidth == WasmLeb128.GetULeb128Size(count), "padded local counts are not expected");
            locals[i] = new WasmLocalGroup(count, (ValType)body[at++]);
        }

        writer.LocalGroups(locals);
        Assert.Equal(at, (int)writer.Position);

        byte[] code = body[at..].ToArray();
        var cursor = new InstructionCursor(code, WasmFeatures.All);
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            OpcodeId id = instruction.OpcodeId;
            var op = (WasmOp)(id.IsPrefixed ? ((uint)id.Opcode << 24) | id.SubOpcode : id.Opcode);
            // Immediates start after the opcode byte and, for a prefixed opcode, its LEB128 sub-opcode.
            int p = instruction.ByteOffset + 1;
            if (id.IsPrefixed)
                ReadU(code, ref p, out _);
            int end = cursor.Position;
            Emit(writer, op, code, ref p, at);
            Assert.True(p == end, $"{op} at {instruction.ByteOffset}: immediates parsed to {p}, instruction ends at {end}");
        }

        return output.WrittenSpan.ToArray();
    }

    // Every position the writer returns must be its operand's offset in the original body (code starts at codeStart).
    private static void Emit(WasmCodeWriter writer, WasmOp op, byte[] code, ref int p, int codeStart)
    {
        switch (WasmOps.GetImmediateShape(op))
        {
            case WasmImmediateShape.None:
                writer.Emit(op);
                break;
            case WasmImmediateShape.Index:
            {
                int position = codeStart + p;
                uint index = ReadU(code, ref p, out int width);
                Assert.Equal(position, writer.Index(op, index, Padded(width, index)));
                break;
            }

            case WasmImmediateShape.IndexPair:
            {
                int firstPosition = codeStart + p;
                uint first = ReadU(code, ref p, out int firstWidth);
                int secondPosition = codeStart + p;
                uint second = ReadU(code, ref p, out int secondWidth);
                Assert.Equal(firstPosition, writer.Indices(op, first, second, Padded(firstWidth, first), Padded(secondWidth, second), out long written));
                Assert.Equal(secondPosition, written);
                break;
            }

            case WasmImmediateShape.I32:
            {
                int position = codeStart + p;
                long value = ReadS(code, ref p, out int width);
                Assert.Equal(position, writer.I32Const((int)value, width == WasmLeb128.GetSLeb128Size(value) ? 0 : width));
                break;
            }

            case WasmImmediateShape.I64:
            {
                int position = codeStart + p;
                long value = ReadS(code, ref p, out int width);
                Assert.Equal(position, writer.I64Const(value, width == WasmLeb128.GetSLeb128Size(value) ? 0 : width));
                break;
            }

            case WasmImmediateShape.F32:
                writer.F32Const(BitConverter.UInt32BitsToSingle(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(p))));
                p += 4;
                break;
            case WasmImmediateShape.F64:
                writer.F64Const(BitConverter.UInt64BitsToDouble(BinaryPrimitives.ReadUInt64LittleEndian(code.AsSpan(p))));
                p += 8;
                break;
            case WasmImmediateShape.MemArg or WasmImmediateShape.MemArgLane:
            {
                uint flags = ReadU(code, ref p, out int flagsWidth);
                Assert.True(flagsWidth == 1, "padded memarg alignments are not expected");
                uint memory = (flags & 0x40) != 0 ? ReadU(code, ref p, out _) : 0;
                int position = codeStart + p;
                ulong offset = ReadU64(code, ref p, out int offsetWidth);
                int width = offsetWidth == WasmLeb128.GetULeb128Size(offset) ? 0 : offsetWidth;
                if (WasmOps.GetImmediateShape(op) == WasmImmediateShape.MemArg)
                    Assert.Equal(position, writer.MemoryAccess(op, flags & ~0x40u, offset, memory, width));
                else
                    Assert.Equal(position, writer.MemoryAccessLane(op, flags & ~0x40u, offset, code[p++], memory, width));
                break;
            }

            case WasmImmediateShape.Lane:
                writer.Lane(op, code[p++]);
                break;
            case WasmImmediateShape.V128:
                writer.V128Const(code.AsSpan(p, 16));
                p += 16;
                break;
            case WasmImmediateShape.Shuffle:
                writer.I8x16Shuffle(code.AsSpan(p, 16));
                p += 16;
                break;
            case WasmImmediateShape.Block:
                writer.Block(op, ReadBlockType(code, ref p));
                break;
            case WasmImmediateShape.BrTable:
            {
                uint count = ReadU(code, ref p, out _);
                var labels = new uint[count];
                for (int i = 0; i < count; i++)
                    labels[i] = ReadU(code, ref p, out _);
                writer.BrTable(labels, ReadU(code, ref p, out _));
                break;
            }

            case WasmImmediateShape.TryTable:
            {
                WasmBlockType type = ReadBlockType(code, ref p);
                uint count = ReadU(code, ref p, out _);
                var clauses = new WasmCatchClause[count];
                var tagPositions = new long[count];
                for (int i = 0; i < count; i++)
                {
                    var kind = (WasmCatchKind)code[p++];
                    tagPositions[i] = kind is WasmCatchKind.Catch or WasmCatchKind.CatchRef ? codeStart + p : -1;
                    int tagWidth = 0;
                    uint tag = kind is WasmCatchKind.Catch or WasmCatchKind.CatchRef ? ReadU(code, ref p, out tagWidth) : 0;
                    clauses[i] = new WasmCatchClause(kind, tag, ReadU(code, ref p, out _), Padded(tagWidth, tag));
                }

                var written = new long[count];
                writer.TryTable(type, clauses, written);
                Assert.Equal(tagPositions, written);
                break;
            }

            case WasmImmediateShape.HeapType:
            {
                long heapType = ReadS(code, ref p, out _);
                Assert.True(heapType >= 0 || AbstractHeapTypeCodes.TryFromCode((byte)(heapType & 0x7F), out _), $"heap type {heapType}");
                AbstractHeapTypeCodes.TryFromCode((byte)(heapType & 0x7F), out HeapTypeKind kind);
                writer.RefNull(heapType >= 0 ? WasmHeapType.Concrete((uint)heapType) : WasmHeapType.Abstract(kind));
                break;
            }

            case WasmImmediateShape.SelectTyped:
            {
                uint count = ReadU(code, ref p, out _);
                writer.SelectTyped(MemoryMarshal.Cast<byte, ValType>(code.AsSpan(p, (int)count)));
                p += (int)count;
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled shape for {op}.");
        }
    }

    private static WasmBlockType ReadBlockType(byte[] code, ref int p)
    {
        long value = ReadS(code, ref p, out _);
        return value switch
        {
            -64 => WasmBlockType.Empty,
            < 0 => WasmBlockType.Of((ValType)(byte)(value & 0x7F)),
            _ => WasmBlockType.TypeIndex((uint)value),
        };
    }

    private static int Padded(int width, ulong value) => width == WasmLeb128.GetULeb128Size(value) ? 0 : width;

    private static uint ReadU(ReadOnlySpan<byte> code, ref int p, out int width) => checked((uint)ReadU64(code, ref p, out width));

    private static ulong ReadU64(ReadOnlySpan<byte> code, ref int p, out int width)
    {
        int start = p;
        ulong value = 0;
        int shift = 0;
        byte b;
        do
        {
            b = code[p++];
            value |= (ulong)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        width = p - start;
        return value;
    }

    private static long ReadS(ReadOnlySpan<byte> code, ref int p, out int width)
    {
        int start = p;
        long value = 0;
        int shift = 0;
        byte b;
        do
        {
            b = code[p++];
            if (shift < 64)
                value |= (long)(b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);
        if (shift < 64 && (b & 0x40) != 0)
            value |= -1L << shift;
        width = p - start;
        return value;
    }

    /// <summary>The function bodies (without size prefixes) of a module's code section.</summary>
    public static List<byte[]> FunctionBodies(byte[] module)
    {
        var bodies = new List<byte[]>();
        foreach (ObjectReencoder.RawSection section in ObjectReencoder.ReadSections(module))
        {
            if (section.Id != (byte)WasmSectionId.Code)
                continue;
            int at = section.ContentStart;
            ulong count = ObjectReencoder.ReadULeb(module, at, out int countWidth);
            at += countWidth;
            for (ulong i = 0; i < count; i++)
            {
                int size = (int)ObjectReencoder.ReadULeb(module, at, out int sizeWidth);
                at += sizeWidth;
                bodies.Add(module[at..(at + size)]);
                at += size;
            }
        }

        return bodies;
    }
}
