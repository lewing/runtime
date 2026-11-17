// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using Microsoft.NET.WebAssembly.Types;
using System.Runtime.CompilerServices;

/// <summary>
/// Lossless WebAssembly instruction identifier: primary opcode plus the full
/// prefix subopcode where applicable.
/// </summary>
internal readonly record struct OpcodeId(byte Opcode, uint SubOpcode = 0)
{
    public bool IsPrefixed => Opcode is 0xFB or 0xFC or 0xFD or 0xE0 or 0xFE;

    public override string ToString() => IsPrefixed ? $"0x{Opcode:X2}:0x{SubOpcode:X}" : $"0x{Opcode:X2}";
}

internal enum InstructionImmediateKind : byte
{
    None,
    BlockType,
    Index,
    TwoIndices,
    BrTable,
    SelectTypes,
    MemArg,
    I32,
    I64,
    F32,
    F64,
    HeapType,
    TryTable,
    SimdBytes,
    Gc,
    StackSwitching,
}

internal enum InstructionOpcodeBucket : byte
{
    Primary,
    Fc,
    FbGc,
    FdSimd,
    FdRelaxedSimd,
    E0StackSwitching,
}

/// <summary>
/// A decoded instruction with exact binary immediates. Span fields reference the
/// original function body and are valid until the cursor advances on another body.
/// </summary>
internal ref struct DecodedInstruction
{
    public OpcodeId OpcodeId;
    public int ByteOffset;
    public int ImmediateOffset;
    public InstructionImmediateKind ImmediateKind;
    public uint Index0;
    public uint Index1;
    public uint Index2;
    public uint Count;
    public ulong MemOffset;
    public long BlockType;
    public long HeapType0;
    public long HeapType1;
    public byte Flags;
    public ReadOnlySpan<byte> ImmediateBytes;
}

/// <summary>
/// Shared lossless instruction cursor for module reading, execution projection,
/// and future semantic validation. It owns only binary grammar and immediate
/// shape; index-space meaning and operand typing stay outside this type.
/// </summary>
internal ref struct InstructionCursor
{
    private readonly ReadOnlySpan<byte> _code;
    private readonly WasmFeatures _features;
    private int _pc;

    public InstructionCursor(ReadOnlySpan<byte> code, WasmFeatures features = WasmFeatures.Default)
    {
        _code = code;
        _features = features;
        _pc = 0;
    }

    public int Position => _pc;

    public bool MoveNext(out DecodedInstruction instruction)
    {
        if (_pc >= _code.Length)
        {
            instruction = default;
            return false;
        }

        int start = _pc;
        // Only `result` is zero-initialised here; the out parameter is written once on the
        // success path below. Zeroing both cost an extra ~88-byte clear per instruction,
        // which is measurable at ~2.5M instructions per module load.
        var result = default(DecodedInstruction);
        try
        {
            byte op = _code[_pc++];
            result.OpcodeId = new OpcodeId(op);
            result.ByteOffset = start;
            result.ImmediateOffset = _pc;

            switch (op)
            {
                case 0x00:
                case 0x01:
                case 0x05:
                case 0x0B:
                case 0x0F:
                case 0x19:
                case 0x1A:
                case 0x1B:
                case 0xD1:
                case 0xD3:
                case 0xD4:
                case >= 0x45 and <= 0xC4:
                    break;

                case 0x0A:
                    RequireFeature(WasmFeatures.ExceptionHandling, "throw_ref opcode");
                    break;

                case 0x02:
                case 0x03:
                case 0x04:
                    result.ImmediateKind = InstructionImmediateKind.BlockType;
                    ReadBlockType(ref result);
                    break;

                case 0x06:
                    RequireFeature(WasmFeatures.ExceptionHandling, "try opcode");
                    result.ImmediateKind = InstructionImmediateKind.BlockType;
                    ReadBlockType(ref result);
                    break;

                case 0x07:
                case 0x08:
                case 0x09:
                    RequireFeature(WasmFeatures.ExceptionHandling, "exception instruction");
                    result.ImmediateKind = InstructionImmediateKind.Index;
                    result.Index0 = ReadU32Leb();
                    break;

                case 0x18:
                    RequireFeature(WasmFeatures.ExceptionHandling, "delegate opcode");
                    result.ImmediateKind = InstructionImmediateKind.Index;
                    result.Index0 = ReadU32Leb();
                    break;

                case 0x1F:
                    RequireFeature(WasmFeatures.ExceptionHandling, "try_table opcode");
                    DecodeTryTable(ref result);
                    break;

                case 0x0C:
                case 0x0D:
                case 0x10:
                case 0x14:
                case 0x20:
                case 0x21:
                case 0x22:
                case 0x23:
                case 0x24:
                case 0x25:
                case 0x26:
                case 0x3F:
                case 0x40:
                case 0xD2:
                case 0xD5:
                case 0xD6:
                    result.ImmediateKind = InstructionImmediateKind.Index;
                    result.Index0 = ReadU32Leb();
                    break;

                case 0x12:
                case 0x15:
                    RequireFeature(WasmFeatures.TailCalls, "return_call opcode");
                    result.ImmediateKind = InstructionImmediateKind.Index;
                    result.Index0 = ReadU32Leb();
                    break;

                case 0x11:
                    result.ImmediateKind = InstructionImmediateKind.TwoIndices;
                    result.Index0 = ReadU32Leb();
                    result.Index1 = ReadU32Leb();
                    break;

                case 0x13:
                    RequireFeature(WasmFeatures.TailCalls, "return_call_indirect opcode");
                    result.ImmediateKind = InstructionImmediateKind.TwoIndices;
                    result.Index0 = ReadU32Leb();
                    result.Index1 = ReadU32Leb();
                    break;

                case 0x0E:
                    DecodeBrTable(ref result);
                    break;

                case 0x1C:
                    DecodeSelectTypes(ref result);
                    break;

                case >= 0x28 and <= 0x3E:
                    result.ImmediateKind = InstructionImmediateKind.MemArg;
                    ReadMemArg(ref result);
                    break;

                case 0x41:
                    result.ImmediateKind = InstructionImmediateKind.I32;
                    result.Index0 = unchecked((uint)ReadS32Leb());
                    break;

                case 0x42:
                    result.ImmediateKind = InstructionImmediateKind.I64;
                    result.MemOffset = unchecked((ulong)ReadS64Leb());
                    break;

                case 0x43:
                    result.ImmediateKind = InstructionImmediateKind.F32;
                    result.Index0 = ReadRawU32();
                    break;

                case 0x44:
                    result.ImmediateKind = InstructionImmediateKind.F64;
                    result.MemOffset = unchecked((ulong)ReadRawI64());
                    break;

                case 0xD0:
                    result.ImmediateKind = InstructionImmediateKind.HeapType;
                    result.HeapType0 = ReadS33Leb();
                    break;

                case 0xFC:
                    DecodeFc(ref result);
                    break;

                case 0xFD:
                    RequireFeature(WasmFeatures.Simd, "SIMD instruction (0xFD prefix)");
                    DecodeFd(ref result);
                    break;

                case 0xFB:
                    RequireFeature(WasmFeatures.Gc, "GC instruction (0xFB prefix)");
                    DecodeFb(ref result);
                    break;

                case 0xE0:
                    RequireFeature(WasmFeatures.StackSwitching, "stack-switching instruction (0xE0 prefix)");
                    DecodeE0(ref result);
                    break;

                case 0xFE:
                    RequireFeature(WasmFeatures.Threads, "atomic/threads instruction (0xFE prefix)");
                    // Not a malformed binary: the feature is enabled and the encoding may be
                    // perfectly well-formed — nesm simply cannot decode it. A plain
                    // WasmFormatException here would satisfy assert_malformed/assert_invalid
                    // and count the gap as conformance. See IWasmUnimplemented and #385.
                    throw new WasmUnimplementedFormatException(
                        "atomic (0xFE) instruction decoding",
                        "the threads feature is enabled but nesm cannot decode atomic instructions");

                case 0xFF:
                    throw new WasmFormatException("illegal opcode ff");

                default:
                    throw new WasmFormatException($"Unknown opcode: 0x{op:X2}");
            }

            result.ImmediateBytes = _code[result.ImmediateOffset.._pc];
            instruction = result;
            return true;
        }
        catch (IndexOutOfRangeException ex)
        {
            throw new WasmFormatException("unexpected end of section or function", ex);
        }
    }

    public static bool IsKnownSimdSubOpcode(uint subOpcode)
    {
        if (subOpcode <= 0x22)
            return true;
        if (subOpcode is >= 0x23 and <= 0x5D)
            return true;
        if (subOpcode is >= 0x5E and <= 0x9F && subOpcode != 0x9A)
            return true;
        if (subOpcode is >= 0xA0 and <= 0xBF)
            return subOpcode is not 0xA2 and not 0xA5 and not 0xA6 and not 0xAF
                and not 0xB0 and not 0xB2 and not 0xB3 and not 0xB4 and not 0xBB;
        if (subOpcode is >= 0xC0 and <= 0xDF)
            return subOpcode is not 0xC2 and not 0xC5 and not 0xC6 and not 0xCF
                and not 0xD0 and not 0xD2 and not 0xD3 and not 0xD4;
        if (subOpcode is >= 0xE0 and <= 0xEB)
            return subOpcode != 0xE2;
        if (subOpcode is >= 0xEC and <= 0xF7)
            return subOpcode != 0xEE;
        return subOpcode is >= 0xF8 and <= 0xFF;
    }

    public static bool IsKnownRelaxedSimdSubOpcode(uint subOpcode) => subOpcode is >= 0x100 and <= 0x113;

    public static bool IsKnownGcSubOpcode(uint subOpcode) => subOpcode <= 0x1E;

    public static bool IsKnownFcSubOpcode(uint subOpcode) =>
        subOpcode <= 17 || subOpcode is >= 19 and <= 22;

    public static bool IsKnownE0SubOpcode(uint subOpcode) => subOpcode <= 4;

    private void DecodeFc(ref DecodedInstruction instruction)
    {
        uint sub = ReadU32Leb();
        instruction.OpcodeId = new OpcodeId(0xFC, sub);
        if (!IsKnownFcSubOpcode(sub))
            throw new WasmFormatException($"Unknown 0xFC sub-opcode: {sub}");

        if (sub is >= 8 and <= 17)
            RequireFeature(WasmFeatures.BulkMemory, $"bulk-memory op (0xFC:{sub})");
        if (sub is >= 19 and <= 22)
            RequireFeature(WasmFeatures.WideArithmetic, $"wide-arithmetic op (0xFC:{sub})");

        switch (sub)
        {
            case <= 7:
                break;
            case 8:
            case 12:
                instruction.ImmediateKind = InstructionImmediateKind.TwoIndices;
                instruction.Index0 = ReadU32Leb();
                instruction.Index1 = ReadU32Leb();
                break;
            case 10:
            case 14:
                instruction.ImmediateKind = InstructionImmediateKind.TwoIndices;
                instruction.Index0 = ReadU32Leb();
                instruction.Index1 = ReadU32Leb();
                break;
            case 9:
            case 11:
            case 13:
            case 15:
            case 16:
            case 17:
                instruction.ImmediateKind = InstructionImmediateKind.Index;
                instruction.Index0 = ReadU32Leb();
                break;
        }
    }

    private void DecodeFd(ref DecodedInstruction instruction)
    {
        uint sub = ReadU32Leb();
        instruction.OpcodeId = new OpcodeId(0xFD, sub);
        instruction.Index2 = (uint)_pc;

        if (IsKnownRelaxedSimdSubOpcode(sub))
        {
            RequireFeature(WasmFeatures.RelaxedSimd, $"Relaxed SIMD instruction (0xFD 0x{sub:X})");
            return;
        }

        if (!IsKnownSimdSubOpcode(sub))
            throw new WasmFormatException($"Unknown 0xFD sub-opcode: {sub}");

        instruction.ImmediateKind = InstructionImmediateKind.SimdBytes;

        if (sub <= 0x0B || sub == 0x5C || sub == 0x5D)
        {
            instruction.ImmediateKind = InstructionImmediateKind.MemArg;
            ReadMemArg(ref instruction);
            return;
        }

        switch (sub)
        {
            case 0x0C:
            case 0x0D:
                EnsureAvailable(16);
                _pc += 16;
                break;
            case >= 0x15 and <= 0x22:
                instruction.Flags = _code[_pc++];
                break;
            case >= 0x54 and <= 0x5B:
                ReadMemArg(ref instruction);
                instruction.Flags = _code[_pc++];
                break;
        }
    }

    private void DecodeFb(ref DecodedInstruction instruction)
    {
        uint sub = ReadU32Leb();
        instruction.OpcodeId = new OpcodeId(0xFB, sub);
        instruction.ImmediateKind = InstructionImmediateKind.Gc;
        if (!IsKnownGcSubOpcode(sub))
            throw new WasmFormatException($"Unknown 0xFB sub-opcode: {sub}");

        switch (sub)
        {
            case 0x00:
            case 0x01:
            case 0x06:
            case 0x07:
            case 0x0B:
            case 0x0C:
            case 0x0D:
            case 0x0E:
            case 0x10:
                instruction.Index0 = ReadU32Leb();
                break;
            case 0x02:
            case 0x03:
            case 0x04:
            case 0x05:
            case 0x08:
            case 0x09:
            case 0x0A:
            case 0x11:
            case 0x12:
            case 0x13:
                instruction.Index0 = ReadU32Leb();
                instruction.Index1 = ReadU32Leb();
                break;
            case 0x14:
            case 0x15:
            case 0x16:
            case 0x17:
                instruction.HeapType0 = ReadS33Leb();
                break;
            case 0x18:
            case 0x19:
                instruction.Flags = _code[_pc++];
                instruction.Index0 = ReadU32Leb();
                instruction.HeapType0 = ReadS33Leb();
                instruction.HeapType1 = ReadS33Leb();
                break;
        }
    }

    private void DecodeE0(ref DecodedInstruction instruction)
    {
        uint sub = ReadU32Leb();
        instruction.OpcodeId = new OpcodeId(0xE0, sub);
        instruction.ImmediateKind = InstructionImmediateKind.StackSwitching;
        if (!IsKnownE0SubOpcode(sub))
            throw new WasmFormatException($"Unknown 0xE0 sub-opcode: {sub}");

        switch (sub)
        {
            case 0x00:
            case 0x03:
                instruction.Index0 = ReadU32Leb();
                break;
            case 0x01:
            case 0x04:
                instruction.Index0 = ReadU32Leb();
                instruction.Index1 = ReadU32Leb();
                break;
            case 0x02:
                instruction.Index0 = ReadU32Leb();
                DecodeHandlerVector(ref instruction, hasTagIndex: true);
                break;
        }
    }

    private void DecodeTryTable(ref DecodedInstruction instruction)
    {
        instruction.ImmediateKind = InstructionImmediateKind.TryTable;
        ReadBlockType(ref instruction);
        DecodeHandlerVector(ref instruction, hasTagIndex: false);
    }

    private void DecodeHandlerVector(ref DecodedInstruction instruction, bool hasTagIndex)
    {
        instruction.Count = ReadU32Leb();
        for (uint h = 0; h < instruction.Count; h++)
        {
            if (hasTagIndex)
            {
                ReadU32Leb();
                ReadU32Leb();
                continue;
            }

            byte catchKind = _code[_pc++];
            if (catchKind > 0x03)
                throw new WasmFormatException($"Unknown try_table catch kind: 0x{catchKind:X2}");
            if (catchKind is 0x00 or 0x01)
                ReadU32Leb();
            ReadU32Leb();
        }
    }

    private void DecodeBrTable(ref DecodedInstruction instruction)
    {
        instruction.ImmediateKind = InstructionImmediateKind.BrTable;
        instruction.Count = ReadU32Leb();
        for (uint i = 0; i <= instruction.Count; i++)
            ReadU32Leb();
    }

    private void DecodeSelectTypes(ref DecodedInstruction instruction)
    {
        instruction.ImmediateKind = InstructionImmediateKind.SelectTypes;
        instruction.Count = ReadU32Leb();
        for (uint i = 0; i < instruction.Count; i++)
            ReadValueType();
    }

    private void ReadBlockType(ref DecodedInstruction instruction)
    {
        instruction.BlockType = ReadS32Leb();
        if (AbstractHeapTypeCodes.BlockTypeCarriesHeapType(instruction.BlockType))
            instruction.HeapType0 = ReadS33Leb();
    }

    private void ReadValueType()
    {
        byte vt = _code[_pc++];
        if (vt is 0x63 or 0x64)
            ReadS33Leb();
    }

    private void ReadMemArg(ref DecodedInstruction instruction)
    {
        uint flags = ReadU32Leb();
        if ((flags & ~0x7Fu) != 0)
            throw new WasmFormatException($"Malformed memory-operation flags: 0x{flags:X}");
        instruction.Index0 = flags & 0x3F;
        if ((flags & 0x40) != 0)
            instruction.Index1 = ReadU32Leb();
        instruction.MemOffset = ReadU64Leb();
    }

    private void RequireFeature(WasmFeatures feature, string context)
    {
        if (!_features.HasFlag(feature))
            throw new WasmFeatureException(feature, context);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private uint ReadU32Leb()
    {
        byte b = _code[_pc++];
        if ((b & 0x80) == 0)
            return b;
        return ReadU32LebSlow(b);
    }

    private uint ReadU32LebSlow(byte first)
    {
        uint result = (uint)(first & 0x7F);
        int shift = 7;
        for (int i = 1; i < 5; i++)
        {
            byte b = _code[_pc++];
            if (i == 4 && (b & 0xF0) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint32");
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }

        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 5 bytes for uint32");
    }

    private ulong ReadU64Leb()
    {
        ulong result = 0;
        int shift = 0;
        for (int i = 0; i < 10; i++)
        {
            byte b = _code[_pc++];
            if (i == 9 && (b & 0xFE) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint64");
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }

        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 10 bytes for uint64");
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int ReadS32Leb()
    {
        byte b = _code[_pc++];
        if ((b & 0x80) == 0)
            return (b & 0x40) != 0 ? (int)(b | 0xFFFF_FF80u) : b;
        return ReadS32LebSlow(b);
    }

    private int ReadS32LebSlow(byte first)
    {
        int result = first & 0x7F;
        int shift = 7;
        byte b = first;
        for (int i = 1; i < 5; i++)
        {
            b = _code[_pc++];
            if (i == 4 && (b & 0x78) != 0 && (b & 0x78) != 0x78)
                throw new WasmFormatException("Signed LEB128 value overflows int32");
            result |= (b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (shift < 32 && (b & 0x40) != 0)
                    result |= ~0 << shift;
                return result;
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for int32");
    }

    private long ReadS33Leb()
    {
        return S33Leb128.Read(_code, ref _pc);
    }

    private long ReadS64Leb()
    {
        byte b = _code[_pc++];
        if ((b & 0x80) == 0)
            return (b & 0x40) != 0 ? (long)(b | 0xFFFF_FFFF_FFFF_FF80uL) : b;
        return ReadS64LebSlow(b);
    }

    private long ReadS64LebSlow(byte first)
    {
        long result = first & 0x7F;
        int shift = 7;
        byte b = first;
        for (int i = 1; i < 10; i++)
        {
            b = _code[_pc++];
            if (i == 9 && (b & 0x7F) != 0 && (b & 0x7F) != 0x7F)
                throw new WasmFormatException("Signed LEB128 value overflows int64");
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (shift < 64 && (b & 0x40) != 0)
                    result |= ~0L << shift;
                return result;
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 10 bytes for int64");
    }

    private uint ReadRawU32()
    {
        EnsureAvailable(4);
        uint value = BitConverter.ToUInt32(_code[_pc..(_pc + 4)]);
        _pc += 4;
        return value;
    }

    private long ReadRawI64()
    {
        EnsureAvailable(8);
        long value = BitConverter.ToInt64(_code[_pc..(_pc + 8)]);
        _pc += 8;
        return value;
    }

    private void EnsureAvailable(int count)
    {
        if (_pc + count > _code.Length)
            throw new WasmFormatException("unexpected end of section or function");
    }
}

internal static class InstructionDecodeCoverage
{
    public static IReadOnlyList<OpcodeId> GetOpcodeIds(InstructionOpcodeBucket bucket)
    {
        List<OpcodeId> result = [];
        switch (bucket)
        {
            case InstructionOpcodeBucket.Primary:
                AddPrimary(result);
                break;
            case InstructionOpcodeBucket.Fc:
                for (uint sub = 0; sub <= 17; sub++) result.Add(new OpcodeId(0xFC, sub));
                for (uint sub = 19; sub <= 22; sub++) result.Add(new OpcodeId(0xFC, sub));
                break;
            case InstructionOpcodeBucket.FbGc:
                for (uint sub = 0; sub <= 0x1E; sub++) result.Add(new OpcodeId(0xFB, sub));
                break;
            case InstructionOpcodeBucket.FdSimd:
                for (uint sub = 0; sub <= 0xFF; sub++)
                    if (InstructionCursor.IsKnownSimdSubOpcode(sub))
                        result.Add(new OpcodeId(0xFD, sub));
                break;
            case InstructionOpcodeBucket.FdRelaxedSimd:
                for (uint sub = 0x100; sub <= 0x113; sub++) result.Add(new OpcodeId(0xFD, sub));
                break;
            case InstructionOpcodeBucket.E0StackSwitching:
                for (uint sub = 0; sub <= 4; sub++) result.Add(new OpcodeId(0xE0, sub));
                break;
        }

        return result;
    }

    private static void AddPrimary(List<OpcodeId> result)
    {
        byte[] singular =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x18, 0x19,
            0x1A, 0x1B, 0x1C, 0x1F, 0x20, 0x21, 0x22, 0x23,
            0x24, 0x25, 0x26, 0x3F, 0x40, 0xD0, 0xD1, 0xD2,
            0xD3, 0xD4, 0xD5, 0xD6
        ];
        foreach (byte op in singular)
            result.Add(new OpcodeId(op));
        for (byte op = 0x28; op <= 0x3E; op++)
            result.Add(new OpcodeId(op));
        for (byte op = 0x41; op <= 0xC4; op++)
            result.Add(new OpcodeId(op));
    }
}
