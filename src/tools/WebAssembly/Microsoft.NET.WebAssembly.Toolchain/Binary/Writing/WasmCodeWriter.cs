// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>The immediate operands an opcode takes, which selects the <see cref="WasmCodeWriter"/> method that writes it.</summary>
public enum WasmImmediateShape : byte
{
    /// <summary>No immediates.</summary>
    None,
    /// <summary>One u32 index (local, global, function, label, tag, table, memory, data, element or type).</summary>
    Index,
    /// <summary>Two u32 indices (call_indirect type and table, memory.init data and memory, memory.copy, table.init, table.copy).</summary>
    IndexPair,
    /// <summary>An s32 constant.</summary>
    I32,
    /// <summary>An s64 constant.</summary>
    I64,
    /// <summary>An f32 constant.</summary>
    F32,
    /// <summary>An f64 constant.</summary>
    F64,
    /// <summary>A memarg (alignment, optional memory index, offset).</summary>
    MemArg,
    /// <summary>A memarg and a lane index.</summary>
    MemArgLane,
    /// <summary>A lane index.</summary>
    Lane,
    /// <summary>A 16-byte constant.</summary>
    V128,
    /// <summary>16 lane indices.</summary>
    Shuffle,
    /// <summary>A block type.</summary>
    Block,
    /// <summary>A label vector and a default label.</summary>
    BrTable,
    /// <summary>A block type and catch clauses.</summary>
    TryTable,
    /// <summary>A heap type.</summary>
    HeapType,
    /// <summary>A vector of value types.</summary>
    SelectTyped,
}

/// <summary>A run of locals of one type in a function body's local declarations.</summary>
/// <param name="Count">The number of locals.</param>
/// <param name="ValueType">Their type; a <see cref="ValType"/> converts implicitly. <c>default</c> is rejected.</param>
public readonly record struct WasmLocalGroup(uint Count, WasmValType ValueType);

/// <summary>The kind of a <c>try_table</c> catch clause.</summary>
public enum WasmCatchKind : byte
{
    /// <summary><c>catch tag label</c>.</summary>
    Catch = 0,

    /// <summary><c>catch_ref tag label</c>.</summary>
    CatchRef = 1,

    /// <summary><c>catch_all label</c>.</summary>
    CatchAll = 2,

    /// <summary><c>catch_all_ref label</c>.</summary>
    CatchAllRef = 3,
}

/// <summary>
/// A <c>try_table</c> catch clause, made by <see cref="Catch"/>, <see cref="CatchRef"/>, <see cref="CatchAll"/> or
/// <see cref="CatchAllRef"/>. <c>default</c> is not a clause, and <see cref="WasmCodeWriter.TryTable"/> rejects it.
/// </summary>
public readonly record struct WasmCatchClause
{
    private readonly bool _isSet;

    private WasmCatchClause(WasmCatchKind kind, uint tag, uint label, int tagWidth)
    {
        Kind = kind;
        Tag = tag;
        Label = label;
        TagWidth = tagWidth;
        _isSet = true;
    }

    /// <summary>The clause's kind.</summary>
    public WasmCatchKind Kind { get; }

    /// <summary>The caught tag's index; 0 for the <c>catch_all</c> kinds, which have none.</summary>
    public uint Tag { get; }

    /// <summary>The label the clause branches to.</summary>
    public uint Label { get; }

    /// <summary>0 for a minimal tag index, or the width it is padded to (5 for a relocated one); 0 for the <c>catch_all</c> kinds.</summary>
    public int TagWidth { get; }

    internal bool IsSet => _isSet;

    internal const string DefaultMessage =
        "default(WasmCatchClause) is not a catch clause; use Catch, CatchRef, CatchAll or CatchAllRef.";

    /// <summary><c>catch tag label</c>.</summary>
    /// <param name="tag">The tag index.</param>
    /// <param name="label">The label.</param>
    /// <param name="tagWidth">0 for a minimal tag index, or the width it is padded to (1–5).</param>
    public static WasmCatchClause Catch(uint tag, uint label, int tagWidth = 0) => Tagged(WasmCatchKind.Catch, tag, label, tagWidth);

    /// <summary><c>catch_ref tag label</c>.</summary>
    /// <param name="tag">The tag index.</param>
    /// <param name="label">The label.</param>
    /// <param name="tagWidth">0 for a minimal tag index, or the width it is padded to (1–5).</param>
    public static WasmCatchClause CatchRef(uint tag, uint label, int tagWidth = 0) => Tagged(WasmCatchKind.CatchRef, tag, label, tagWidth);

    /// <summary><c>catch_all label</c>.</summary>
    public static WasmCatchClause CatchAll(uint label) => new(WasmCatchKind.CatchAll, 0, label, 0);

    /// <summary><c>catch_all_ref label</c>.</summary>
    public static WasmCatchClause CatchAllRef(uint label) => new(WasmCatchKind.CatchAllRef, 0, label, 0);

    private static WasmCatchClause Tagged(WasmCatchKind kind, uint tag, uint label, int tagWidth)
    {
        WasmCodeWriter.CheckWidth(tag, tagWidth, WasmLeb128.MaxLength32, nameof(tagWidth));
        return new(kind, tag, label, tagWidth);
    }
}

/// <summary>
/// A block type: empty, one result of any value type, or a type index. <c>default</c> is <see cref="Empty"/>.
/// </summary>
public readonly struct WasmBlockType
{
    private readonly WasmValType _result;
    private readonly uint _typeIndex;
    private readonly bool _isTypeIndex;

    private WasmBlockType(WasmValType result, uint typeIndex, bool isTypeIndex)
    {
        _result = result;
        _typeIndex = typeIndex;
        _isTypeIndex = isTypeIndex;
    }

    /// <summary>No parameters and no results (<c>0x40</c>).</summary>
    public static WasmBlockType Empty => default;

    /// <summary>One result of type <paramref name="valueType"/>; a <see cref="ValType"/> converts implicitly.</summary>
    /// <exception cref="ArgumentException"><paramref name="valueType"/> is <c>default</c>.</exception>
    public static WasmBlockType Of(WasmValType valueType)
    {
        valueType.Check(nameof(valueType));
        return new(valueType, 0, isTypeIndex: false);
    }

    /// <summary>The function type <paramref name="typeIndex"/>.</summary>
    public static WasmBlockType TypeIndex(uint typeIndex) => new(default, typeIndex, isTypeIndex: true);

    // The empty block type is the byte 0x40; a value type is written as itself; a type index is an s33.
    internal int Encode(Span<byte> destination)
    {
        if (_isTypeIndex)
            return WasmLeb128.WriteSLeb128(destination, _typeIndex);
        if (!_result.IsSet)
        {
            destination[0] = EmptyCode;
            return 1;
        }

        return _result.Encode(destination);
    }

    private const byte EmptyCode = 0x40;
}

/// <summary>
/// A heap type: an abstract one (<c>func</c>, <c>extern</c>, ...) or a type index. <c>default</c> is not a heap type,
/// and <see cref="WasmCodeWriter.RefNull"/> rejects it.
/// </summary>
public readonly struct WasmHeapType
{
    private readonly long _code;
    private readonly bool _isSet;

    private WasmHeapType(long code)
    {
        _code = code;
        _isSet = true;
    }

    /// <summary>The s33 encoding of the heap type.</summary>
    /// <exception cref="InvalidOperationException">This is <c>default(WasmHeapType)</c>.</exception>
    public long Code => _isSet ? _code : throw new InvalidOperationException(DefaultMessage);

    internal bool IsSet => _isSet;

    internal const string DefaultMessage =
        "default(WasmHeapType) is not a heap type; use Func, Extern, Exn, Abstract or Concrete.";

    /// <summary><c>func</c>.</summary>
    public static WasmHeapType Func => Abstract(HeapTypeKind.Func);

    /// <summary><c>extern</c>.</summary>
    public static WasmHeapType Extern => Abstract(HeapTypeKind.Extern);

    /// <summary><c>exn</c>.</summary>
    public static WasmHeapType Exn => Abstract(HeapTypeKind.Exn);

    /// <summary>The abstract heap type <paramref name="kind"/>.</summary>
    public static WasmHeapType Abstract(HeapTypeKind kind)
    {
        if (!AbstractHeapTypeCodes.TryToCode(kind, out byte code))
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Not an abstract heap type.");
        return new(WasmCodeWriter.SingleByteTypeCode(code));
    }

    /// <summary>The concrete heap type <paramref name="typeIndex"/>.</summary>
    public static WasmHeapType Concrete(uint typeIndex) => new(typeIndex);
}

/// <summary>
/// Writes WebAssembly function bodies (local declarations and instructions) to an <see cref="IBufferWriter{T}"/>.
/// Each method writes one instruction, checking that the opcode takes the immediates the method writes
/// (<see cref="WasmOps.GetImmediateShape"/>). LEB128 immediates are minimal unless a width is given; a relocatable
/// operand is written padded (5 bytes for 32-bit relocations) and its position is returned so the caller can record
/// a relocation against it. Positions are writer-relative: bytes since this writer was created, not offsets in the
/// module or the code section; <see cref="RelocationSectionEncoder.SectionOffset"/> converts them.
/// </summary>
/// <remarks>
/// Value types (locals, block results, typed <c>select</c>) are <see cref="WasmValType"/>s, so GC and function
/// references types such as <c>(ref null $t)</c> can be written; the <see cref="ValType"/> overloads write the
/// single-byte ones. Instructions are named for what they write, like
/// <c>System.Reflection.Metadata.Ecma335.InstructionEncoder</c>.
/// </remarks>
public sealed class WasmCodeWriter
{
    private readonly IBufferWriter<byte> _output;

    /// <summary>Creates a writer that appends to <paramref name="output"/>.</summary>
    public WasmCodeWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>
    /// The number of bytes this writer has written: the writer-relative position of the next instruction.
    /// </summary>
    public long Position { get; private set; }

    // ── Locals ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Writes the local declarations for locals of the types <paramref name="valueTypes"/>, in order, grouping
    /// consecutive locals of one type.
    /// </summary>
    public void Locals(ReadOnlySpan<WasmValType> valueTypes)
    {
        foreach (WasmValType type in valueTypes)
            type.Check(nameof(valueTypes));

        int groups = 0;
        for (int i = 0; i < valueTypes.Length; i++)
        {
            if (i == 0 || valueTypes[i] != valueTypes[i - 1])
                groups++;
        }

        WriteU((ulong)groups);
        for (int i = 0; i < valueTypes.Length;)
        {
            int start = i;
            while (i < valueTypes.Length && valueTypes[i] == valueTypes[start])
                i++;
            WriteU((ulong)(i - start));
            WriteValueType(valueTypes[start]);
        }
    }

    /// <summary>
    /// Writes the local declarations for locals of the single-byte value types <paramref name="valueTypes"/>, in
    /// order, grouping consecutive locals of one type.
    /// </summary>
    // Preferred where both apply (an empty or all-ValType collection expression), which would otherwise be ambiguous.
    [OverloadResolutionPriority(1)]
    public void Locals(ReadOnlySpan<ValType> valueTypes)
    {
        foreach (ValType type in valueTypes)
            CheckValueType(type, nameof(valueTypes));

        int groups = 0;
        for (int i = 0; i < valueTypes.Length; i++)
        {
            if (i == 0 || valueTypes[i] != valueTypes[i - 1])
                groups++;
        }

        WriteU((ulong)groups);
        for (int i = 0; i < valueTypes.Length;)
        {
            int start = i;
            while (i < valueTypes.Length && valueTypes[i] == valueTypes[start])
                i++;
            WriteU((ulong)(i - start));
            WriteByte((byte)valueTypes[start]);
        }
    }

    /// <summary>Writes the local declarations exactly as the groups given.</summary>
    public void LocalGroups(ReadOnlySpan<WasmLocalGroup> groups)
    {
        foreach (WasmLocalGroup group in groups)
            group.ValueType.Check(nameof(groups));

        WriteU((ulong)groups.Length);
        foreach (WasmLocalGroup group in groups)
        {
            WriteU(group.Count);
            WriteValueType(group.ValueType);
        }
    }

    // ── Instructions by immediate shape ──────────────────────────────────────

    /// <summary>Writes an instruction without immediates.</summary>
    public void Emit(WasmOp op)
    {
        Check(op, WasmImmediateShape.None);
        WriteOpcode(op);
    }

    /// <summary>
    /// Writes an instruction with one index immediate (local, global, function, label, tag, table, memory, data,
    /// element or type) and returns the index's position.
    /// </summary>
    /// <param name="op">The instruction.</param>
    /// <param name="index">The index.</param>
    /// <param name="width">0 for a minimal LEB128, or the width it is padded to (1–5).</param>
    public long Index(WasmOp op, uint index, int width = 0)
    {
        Check(op, WasmImmediateShape.Index);
        CheckWidth(index, width, WasmLeb128.MaxLength32);
        WriteOpcode(op);
        return WriteU(index, width);
    }

    /// <summary>
    /// Writes an instruction with two index immediates (<c>call_indirect</c> type and table, <c>memory.init</c> data
    /// and memory, <c>memory.copy</c>, <c>table.init</c>, <c>table.copy</c>) and returns the first index's position.
    /// </summary>
    /// <param name="op">The instruction.</param>
    /// <param name="first">The first index.</param>
    /// <param name="second">The second index.</param>
    /// <param name="firstWidth">0 for a minimal LEB128, or the width the first index is padded to (1–5).</param>
    /// <param name="secondWidth">0 for a minimal LEB128, or the width the second index is padded to (1–5).</param>
    public long Indices(WasmOp op, uint first, uint second, int firstWidth = 0, int secondWidth = 0) =>
        Indices(op, first, second, firstWidth, secondWidth, out _);

    /// <summary>
    /// Writes an instruction with two index immediates and returns both positions, for relocating the second one
    /// too (the table of <c>call_indirect</c>, <c>R_WASM_TABLE_NUMBER_LEB</c>).
    /// </summary>
    /// <param name="op">The instruction.</param>
    /// <param name="first">The first index.</param>
    /// <param name="second">The second index.</param>
    /// <param name="firstWidth">0 for a minimal LEB128, or the width the first index is padded to (1–5).</param>
    /// <param name="secondWidth">0 for a minimal LEB128, or the width the second index is padded to (1–5).</param>
    /// <param name="secondPosition">The second index's position.</param>
    /// <returns>The first index's position.</returns>
    public long Indices(WasmOp op, uint first, uint second, int firstWidth, int secondWidth, out long secondPosition)
    {
        Check(op, WasmImmediateShape.IndexPair);
        CheckWidth(first, firstWidth, WasmLeb128.MaxLength32);
        CheckWidth(second, secondWidth, WasmLeb128.MaxLength32);
        WriteOpcode(op);
        long position = WriteU(first, firstWidth);
        secondPosition = WriteU(second, secondWidth);
        return position;
    }

    /// <summary>Writes <c>i32.const</c> and returns the constant's position.</summary>
    /// <param name="value">The constant.</param>
    /// <param name="width">0 for a minimal LEB128, or the width it is padded to (1–5).</param>
    public long I32Const(int value, int width = 0)
    {
        CheckSignedWidth(value, width, WasmLeb128.MaxLength32);
        WriteOpcode(WasmOp.I32Const);
        return WriteS(value, width);
    }

    /// <summary>Writes <c>i64.const</c> and returns the constant's position.</summary>
    /// <param name="value">The constant.</param>
    /// <param name="width">0 for a minimal LEB128, or the width it is padded to (1–10).</param>
    public long I64Const(long value, int width = 0)
    {
        CheckSignedWidth(value, width, WasmLeb128.MaxLength64);
        WriteOpcode(WasmOp.I64Const);
        return WriteS(value, width);
    }

    /// <summary>Writes <c>f32.const</c> with the exact bits of <paramref name="value"/>.</summary>
    public void F32Const(float value)
    {
        WriteOpcode(WasmOp.F32Const);
        Span<byte> bytes = _output.GetSpan(4);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, BitConverter.SingleToUInt32Bits(value));
        Advance(4);
    }

    /// <summary>Writes <c>f64.const</c> with the exact bits of <paramref name="value"/>.</summary>
    public void F64Const(double value)
    {
        WriteOpcode(WasmOp.F64Const);
        Span<byte> bytes = _output.GetSpan(8);
        BinaryPrimitives.WriteUInt64LittleEndian(bytes, BitConverter.DoubleToUInt64Bits(value));
        Advance(8);
    }

    /// <summary>Writes a load or store and returns the offset's position.</summary>
    /// <param name="op">The instruction.</param>
    /// <param name="alignLog2">The alignment as a power of two (0–63; 2 for a 4-byte access).</param>
    /// <param name="offset">The offset.</param>
    /// <param name="memoryIndex">The memory; a nonzero index sets the multi-memory flag.</param>
    /// <param name="offsetWidth">0 for a minimal LEB128, or the width the offset is padded to (1–10).</param>
    public long MemoryAccess(WasmOp op, uint alignLog2, ulong offset, uint memoryIndex = 0, int offsetWidth = 0)
    {
        Check(op, WasmImmediateShape.MemArg);
        return WriteMemArg(op, alignLog2, offset, memoryIndex, offsetWidth);
    }

    /// <summary>Writes a SIMD lane load or store and returns the offset's position.</summary>
    /// <param name="op">The instruction.</param>
    /// <param name="alignLog2">The alignment as a power of two.</param>
    /// <param name="offset">The offset.</param>
    /// <param name="lane">The lane index.</param>
    /// <param name="memoryIndex">The memory; a nonzero index sets the multi-memory flag.</param>
    /// <param name="offsetWidth">0 for a minimal LEB128, or the width the offset is padded to (1–10).</param>
    public long MemoryAccessLane(WasmOp op, uint alignLog2, ulong offset, byte lane, uint memoryIndex = 0, int offsetWidth = 0)
    {
        Check(op, WasmImmediateShape.MemArgLane);
        CheckLane(op, lane);
        long position = WriteMemArg(op, alignLog2, offset, memoryIndex, offsetWidth);
        WriteByte(lane);
        return position;
    }

    /// <summary>Writes a SIMD lane instruction (<c>extract_lane</c>, <c>replace_lane</c>).</summary>
    public void Lane(WasmOp op, byte lane)
    {
        Check(op, WasmImmediateShape.Lane);
        CheckLane(op, lane);
        WriteOpcode(op);
        WriteByte(lane);
    }

    /// <summary>Writes <c>v128.const</c> with the 16 bytes <paramref name="value"/>.</summary>
    public void V128Const(ReadOnlySpan<byte> value)
    {
        Check16(value);
        WriteOpcode(WasmOp.V128Const);
        WriteBytes(value);
    }

    /// <summary>Writes <c>i8x16.shuffle</c> with the 16 lane indices <paramref name="lanes"/>.</summary>
    public void I8x16Shuffle(ReadOnlySpan<byte> lanes)
    {
        Check16(lanes);
        WriteOpcode(WasmOp.I8x16Shuffle);
        WriteBytes(lanes);
    }

    /// <summary>Writes a structured instruction with a block type (<c>block</c>, <c>loop</c>, <c>if</c>, <c>try</c>).</summary>
    public void Block(WasmOp op, WasmBlockType type)
    {
        Check(op, WasmImmediateShape.Block);
        WriteOpcode(op);
        WriteBlockType(type);
    }

    /// <summary>Writes <c>br_table</c>.</summary>
    public void BrTable(ReadOnlySpan<uint> labels, uint defaultLabel)
    {
        WriteOpcode(WasmOp.BrTable);
        WriteU((ulong)labels.Length);
        foreach (uint label in labels)
            WriteU(label);
        WriteU(defaultLabel);
    }

    /// <summary>Writes <c>try_table</c>.</summary>
    /// <param name="type">The block type.</param>
    /// <param name="clauses">The catch clauses.</param>
    /// <param name="tagPositions">
    /// Empty, or one element per clause that receives the position of its tag index (-1 for the <c>catch_all</c> kinds),
    /// for relocating tags.
    /// </param>
    public void TryTable(WasmBlockType type, ReadOnlySpan<WasmCatchClause> clauses, Span<long> tagPositions = default)
    {
        if (!tagPositions.IsEmpty && tagPositions.Length != clauses.Length)
            throw new ArgumentException("Pass one tag position per clause, or none.", nameof(tagPositions));
        foreach (WasmCatchClause clause in clauses)
        {
            if (!clause.IsSet)
                throw new ArgumentException(WasmCatchClause.DefaultMessage, nameof(clauses));
        }

        WriteOpcode(WasmOp.TryTable);
        WriteBlockType(type);
        WriteU((ulong)clauses.Length);
        for (int i = 0; i < clauses.Length; i++)
        {
            WasmCatchClause clause = clauses[i];
            WriteByte((byte)clause.Kind);
            long tagPosition = clause.Kind is WasmCatchKind.Catch or WasmCatchKind.CatchRef ? WriteU(clause.Tag, clause.TagWidth) : -1;
            if (!tagPositions.IsEmpty)
                tagPositions[i] = tagPosition;
            WriteU(clause.Label);
        }
    }

    /// <summary>Writes <c>ref.null</c>.</summary>
    public void RefNull(WasmHeapType heapType)
    {
        if (!heapType.IsSet)
            throw new ArgumentException(WasmHeapType.DefaultMessage, nameof(heapType));
        WriteOpcode(WasmOp.RefNull);
        WriteS(heapType.Code, 0);
    }

    /// <summary>Writes a typed <c>select</c> with the value types <paramref name="valueTypes"/>.</summary>
    public void SelectTyped(ReadOnlySpan<WasmValType> valueTypes)
    {
        foreach (WasmValType type in valueTypes)
            type.Check(nameof(valueTypes));
        WriteOpcode(WasmOp.SelectTyped);
        WriteU((ulong)valueTypes.Length);
        foreach (WasmValType type in valueTypes)
            WriteValueType(type);
    }

    /// <summary>Writes a typed <c>select</c> with the single-byte value types <paramref name="valueTypes"/>.</summary>
    // Preferred where both apply (an empty or all-ValType collection expression), which would otherwise be ambiguous.
    [OverloadResolutionPriority(1)]
    public void SelectTyped(ReadOnlySpan<ValType> valueTypes)
    {
        foreach (ValType type in valueTypes)
            CheckValueType(type, nameof(valueTypes));
        WriteOpcode(WasmOp.SelectTyped);
        WriteU((ulong)valueTypes.Length);
        WriteBytes(MemoryMarshal.AsBytes(valueTypes));
    }

    // ── Conveniences ─────────────────────────────────────────────────────────

    /// <summary>Writes <c>end</c>.</summary>
    public void End() => Emit(WasmOp.End);

    /// <summary>Writes <c>local.get</c>.</summary>
    public void LocalGet(uint index) => Index(WasmOp.LocalGet, index);

    /// <summary>Writes <c>local.set</c>.</summary>
    public void LocalSet(uint index) => Index(WasmOp.LocalSet, index);

    /// <summary>Writes <c>local.tee</c>.</summary>
    public void LocalTee(uint index) => Index(WasmOp.LocalTee, index);

    /// <summary>Writes <c>global.get</c> and returns the index's position.</summary>
    public long GlobalGet(uint index, int width = 0) => Index(WasmOp.GlobalGet, index, width);

    /// <summary>Writes <c>global.set</c> and returns the index's position.</summary>
    public long GlobalSet(uint index, int width = 0) => Index(WasmOp.GlobalSet, index, width);

    /// <summary>Writes <c>call</c> and returns the function index's position.</summary>
    public long Call(uint function, int width = 0) => Index(WasmOp.Call, function, width);

    /// <summary>Writes <c>call_indirect</c> and returns the type index's position.</summary>
    public long CallIndirect(uint typeIndex, uint table = 0, int typeWidth = 0) => Indices(WasmOp.CallIndirect, typeIndex, table, typeWidth);

    /// <summary>
    /// Writes <c>call_indirect</c> and returns the type index's position and, in <paramref name="tablePosition"/>, the
    /// table index's, for a relocated table (<c>R_WASM_TABLE_NUMBER_LEB</c>).
    /// </summary>
    public long CallIndirect(uint typeIndex, uint table, int typeWidth, int tableWidth, out long tablePosition) =>
        Indices(WasmOp.CallIndirect, typeIndex, table, typeWidth, tableWidth, out tablePosition);

    // ── Encoding ─────────────────────────────────────────────────────────────

    // A multi-memory memarg sets this bit of the alignment field and follows it with the memory index.
    private const uint MemArgMemoryIndexFlag = 0x40;

    // Single-byte value types: numeric, vector and the abstract reference shorthands.
    internal static void CheckValueType(ValType type, string paramName = "type")
    {
        byte code = (byte)type;
        if (type is not (>= ValType.V128 and <= ValType.I32) &&
            (code == AbstractHeapTypeCodes.ContLegacy || !AbstractHeapTypeCodes.TryFromCode(code, out _)))
        {
            throw new ArgumentException($"0x{code:X2} is not a single-byte value type.", paramName);
        }
    }

    // A single-byte type code (0x40–0x7F) is the s7, and so the s33, code - 0x80.
    internal static long SingleByteTypeCode(byte code) => code - 0x80L;

    private static void CheckLane(WasmOp op, byte lane)
    {
        int lanes = op switch
        {
            WasmOp.I8x16ExtractLaneS or WasmOp.I8x16ExtractLaneU or WasmOp.I8x16ReplaceLane or
                WasmOp.V128Load8Lane or WasmOp.V128Store8Lane => 16,
            WasmOp.I16x8ExtractLaneS or WasmOp.I16x8ExtractLaneU or WasmOp.I16x8ReplaceLane or
                WasmOp.V128Load16Lane or WasmOp.V128Store16Lane => 8,
            WasmOp.I32x4ExtractLane or WasmOp.I32x4ReplaceLane or WasmOp.F32x4ExtractLane or WasmOp.F32x4ReplaceLane or
                WasmOp.V128Load32Lane or WasmOp.V128Store32Lane => 4,
            WasmOp.I64x2ExtractLane or WasmOp.I64x2ReplaceLane or WasmOp.F64x2ExtractLane or WasmOp.F64x2ReplaceLane or
                WasmOp.V128Load64Lane or WasmOp.V128Store64Lane => 2,
            _ => throw new ArgumentException($"{op} has no lane immediate.", nameof(op)),
        };
        if (lane >= lanes)
            throw new ArgumentOutOfRangeException(nameof(lane), lane, $"{op} has {lanes} lanes.");
    }

    private static void Check(WasmOp op, WasmImmediateShape shape)
    {
        WasmImmediateShape actual = WasmOps.GetImmediateShape(op);
        if (actual != shape)
            throw new ArgumentException($"{op} takes {actual} immediates, not {shape}.", nameof(op));
    }

    internal static void CheckWidth(ulong value, int width, int maxWidth, string paramName = "width")
    {
        if (width < 0 || width > maxWidth)
            throw new ArgumentOutOfRangeException(paramName, width, $"A width is 1 to {maxWidth} bytes (0 for minimal).");
        if (width != 0 && WasmLeb128.GetULeb128Size(value) > width)
            throw new ArgumentException($"{value} does not fit a {width}-byte LEB128.", paramName);
    }

    private static void CheckSignedWidth(long value, int width, int maxWidth)
    {
        if (width < 0 || width > maxWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"A width is 1 to {maxWidth} bytes (0 for minimal).");
        if (width != 0 && WasmLeb128.GetSLeb128Size(value) > width)
            throw new ArgumentException($"{value} does not fit a {width}-byte LEB128.", nameof(width));
    }

    private static void Check16(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length != 16)
            throw new ArgumentException($"Expected 16 bytes, got {bytes.Length}.", nameof(bytes));
    }

    private long WriteMemArg(WasmOp op, uint alignLog2, ulong offset, uint memoryIndex, int offsetWidth)
    {
        if (alignLog2 >= MemArgMemoryIndexFlag)
            throw new ArgumentOutOfRangeException(nameof(alignLog2), alignLog2, "Alignment exponents are 0 to 63.");
        CheckWidth(offset, offsetWidth, WasmLeb128.MaxLength64);
        WriteOpcode(op);
        if (memoryIndex == 0)
        {
            WriteU(alignLog2);
        }
        else
        {
            WriteU(alignLog2 | MemArgMemoryIndexFlag);
            WriteU(memoryIndex);
        }

        return WriteU(offset, offsetWidth);
    }

    private void WriteOpcode(WasmOp op)
    {
        uint value = (uint)op;
        if (value <= 0xFF)
        {
            WriteByte((byte)value);
        }
        else
        {
            WriteByte((byte)(value >> 24));
            WriteU(value & 0xFFFFFF);
        }
    }

    private long WriteU(ulong value, int width = 0)
    {
        long position = Position;
        Span<byte> span = _output.GetSpan(WasmLeb128.MaxLength);
        int length;
        if (width == 0)
        {
            length = WasmLeb128.WriteULeb128(span, value);
        }
        else
        {
            WasmLeb128.WritePaddedULeb128(span[..width], value);
            length = width;
        }

        Advance(length);
        return position;
    }

    private long WriteS(long value, int width)
    {
        long position = Position;
        Span<byte> span = _output.GetSpan(WasmLeb128.MaxLength);
        int length;
        if (width == 0)
        {
            length = WasmLeb128.WriteSLeb128(span, value);
        }
        else
        {
            WasmLeb128.WritePaddedSLeb128(span[..width], value);
            length = width;
        }

        Advance(length);
        return position;
    }

    private void WriteByte(byte value)
    {
        _output.GetSpan(1)[0] = value;
        Advance(1);
    }

    private void WriteValueType(WasmValType type) =>
        Advance(type.Encode(_output.GetSpan(1 + WasmLeb128.MaxLength)));

    private void WriteBlockType(WasmBlockType type) =>
        Advance(type.Encode(_output.GetSpan(1 + WasmLeb128.MaxLength)));

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_output.GetSpan(bytes.Length));
        Advance(bytes.Length);
    }

    private void Advance(int count)
    {
        _output.Advance(count);
        Position += count;
    }
}
