// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

/// <summary>Opcode mnemonic to binary encoding mapping.</summary>
public enum ImmediateKind
{
    None,
    BlockType,
    LocalIdx,
    GlobalIdx,
    FuncIdx,
    I32,
    I64,
    F32,
    F64,
    MemArg,
    BrTable,
    MemIdx,
    CallIndirect,   // typeIdx (u32 LEB) + tableIdx (u32 LEB)
    TableIdx,       // table index (u32 LEB)
    RefType,        // reftype byte (0x70=funcref, 0x6F=externref)
    DataMemIdx,     // dataIdx (u32 LEB) + memIdx (u32 LEB)
    DataIdx,        // data segment index (u32 LEB)
    MemCopy,        // dstMemIdx (u32 LEB) + srcMemIdx (u32 LEB)
    ElemTableIdx,   // elemIdx (u32 LEB) + tableIdx (u32 LEB)
    ElemIdx,        // element segment index (u32 LEB)
    TableCopy,      // dstTableIdx (u32 LEB) + srcTableIdx (u32 LEB)
    V128,           // 16 raw bytes (v128.const)
    SimdLane,       // 1 byte lane index
    SimdShuffle,    // 16 bytes lane indices (i8x16.shuffle)
    MemArgLane,     // memarg (align + offset) + 1 byte lane index
    TagIdx,         // tag index (u32 LEB)
    LabelIdx,       // label depth (u32 LEB)
    TryTable,       // blocktype + handler vector
    TypeIdx,        // type index (u32 LEB)
    TypePair,       // typeidx + typeidx
    TypeTagPair,    // typeidx + tagidx
    ResumeTable,    // typeidx + handler vector of (tagidx, labelidx)
}

/// <summary>An entry in the opcode table: mnemonic → (opcode bytes, immediate kind).</summary>
public readonly record struct OpcodeEntry(byte[] Opcodes, ImmediateKind Immediate);

/// <summary>Table mapping WAT mnemonic strings to binary opcode info.</summary>
public static class OpcodeTable
{
    private static readonly Dictionary<string, OpcodeEntry> _table = BuildTable();

    public static bool TryGet(string mnemonic, out OpcodeEntry entry) =>
        _table.TryGetValue(mnemonic, out entry);

    public static OpcodeEntry Get(string mnemonic) =>
        _table.TryGetValue(mnemonic, out var entry)
            ? entry
            : throw new WatException($"Unknown opcode mnemonic: '{mnemonic}'");

    /// <summary>All registered mnemonic strings.</summary>
    public static IReadOnlyCollection<string> AllMnemonics => _table.Keys;

    private static Dictionary<string, OpcodeEntry> BuildTable()
    {
        var t = new Dictionary<string, OpcodeEntry>();

        void Add(string name, byte opcode, ImmediateKind imm = ImmediateKind.None) =>
            t[name] = new OpcodeEntry([opcode], imm);

        // Control
        Add("unreachable",  0x00);
        Add("nop",          0x01);
        Add("block",        0x02, ImmediateKind.BlockType);
        Add("loop",         0x03, ImmediateKind.BlockType);
        Add("if",           0x04, ImmediateKind.BlockType);
        Add("else",         0x05);
        // Exception handling
        Add("try",          0x06, ImmediateKind.BlockType);    // legacy try
        Add("catch",        0x07, ImmediateKind.TagIdx);       // legacy catch
        Add("throw",        0x08, ImmediateKind.TagIdx);       // throw
        Add("rethrow",      0x09, ImmediateKind.LabelIdx);     // legacy rethrow
        Add("throw_ref",    0x0A);                              // throw_ref
        Add("end",          0x0B);
        Add("br",           0x0C, ImmediateKind.LocalIdx);  // label index
        Add("br_if",        0x0D, ImmediateKind.LocalIdx);  // label index
        Add("br_table",     0x0E, ImmediateKind.BrTable);   // label table
        Add("return",       0x0F);
        Add("call",         0x10, ImmediateKind.FuncIdx);
        // More exception handling
        Add("delegate",    0x18, ImmediateKind.LabelIdx);     // legacy delegate
        Add("catch_all",   0x19);                              // legacy catch_all
        Add("try_table",   0x1F, ImmediateKind.TryTable);     // try_table

        // Parametric
        Add("drop",         0x1A);
        Add("select",       0x1B);

        // Local/Global
        Add("local.get",    0x20, ImmediateKind.LocalIdx);
        Add("local.set",    0x21, ImmediateKind.LocalIdx);
        Add("local.tee",    0x22, ImmediateKind.LocalIdx);
        Add("global.get",   0x23, ImmediateKind.GlobalIdx);
        Add("global.set",   0x24, ImmediateKind.GlobalIdx);

        // Memory load
        Add("i32.load",     0x28, ImmediateKind.MemArg);
        Add("i64.load",     0x29, ImmediateKind.MemArg);
        Add("f32.load",     0x2A, ImmediateKind.MemArg);
        Add("f64.load",     0x2B, ImmediateKind.MemArg);
        Add("i32.load8_s",  0x2C, ImmediateKind.MemArg);
        Add("i32.load8_u",  0x2D, ImmediateKind.MemArg);
        Add("i32.load16_s", 0x2E, ImmediateKind.MemArg);
        Add("i32.load16_u", 0x2F, ImmediateKind.MemArg);
        Add("i64.load8_s",  0x30, ImmediateKind.MemArg);
        Add("i64.load8_u",  0x31, ImmediateKind.MemArg);
        Add("i64.load16_s", 0x32, ImmediateKind.MemArg);
        Add("i64.load16_u", 0x33, ImmediateKind.MemArg);
        Add("i64.load32_s", 0x34, ImmediateKind.MemArg);
        Add("i64.load32_u", 0x35, ImmediateKind.MemArg);

        // Memory store
        Add("i32.store",    0x36, ImmediateKind.MemArg);
        Add("i64.store",    0x37, ImmediateKind.MemArg);
        Add("f32.store",    0x38, ImmediateKind.MemArg);
        Add("f64.store",    0x39, ImmediateKind.MemArg);
        Add("i32.store8",   0x3A, ImmediateKind.MemArg);
        Add("i32.store16",  0x3B, ImmediateKind.MemArg);
        Add("i64.store8",   0x3C, ImmediateKind.MemArg);
        Add("i64.store16",  0x3D, ImmediateKind.MemArg);
        Add("i64.store32",  0x3E, ImmediateKind.MemArg);

        // Memory size/grow
        Add("memory.size",  0x3F, ImmediateKind.MemIdx);
        Add("memory.grow",  0x40, ImmediateKind.MemIdx);

        // Constants
        Add("i32.const",    0x41, ImmediateKind.I32);
        Add("i64.const",    0x42, ImmediateKind.I64);
        Add("f32.const",    0x43, ImmediateKind.F32);
        Add("f64.const",    0x44, ImmediateKind.F64);

        // i32 comparison
        Add("i32.eqz",     0x45);
        Add("i32.eq",      0x46);
        Add("i32.ne",      0x47);
        Add("i32.lt_s",    0x48);
        Add("i32.lt_u",    0x49);
        Add("i32.gt_s",    0x4A);
        Add("i32.gt_u",    0x4B);
        Add("i32.le_s",    0x4C);
        Add("i32.le_u",    0x4D);
        Add("i32.ge_s",    0x4E);
        Add("i32.ge_u",    0x4F);

        // i64 comparison
        Add("i64.eqz",     0x50);
        Add("i64.eq",      0x51);
        Add("i64.ne",      0x52);
        Add("i64.lt_s",    0x53);
        Add("i64.lt_u",    0x54);
        Add("i64.gt_s",    0x55);
        Add("i64.gt_u",    0x56);
        Add("i64.le_s",    0x57);
        Add("i64.le_u",    0x58);
        Add("i64.ge_s",    0x59);
        Add("i64.ge_u",    0x5A);

        // f32 comparison
        Add("f32.eq",      0x5B);
        Add("f32.ne",      0x5C);
        Add("f32.lt",      0x5D);
        Add("f32.gt",      0x5E);
        Add("f32.le",      0x5F);
        Add("f32.ge",      0x60);

        // f64 comparison
        Add("f64.eq",      0x61);
        Add("f64.ne",      0x62);
        Add("f64.lt",      0x63);
        Add("f64.gt",      0x64);
        Add("f64.le",      0x65);
        Add("f64.ge",      0x66);

        // i32 arithmetic
        Add("i32.clz",     0x67);
        Add("i32.ctz",     0x68);
        Add("i32.popcnt",  0x69);
        Add("i32.add",     0x6A);
        Add("i32.sub",     0x6B);
        Add("i32.mul",     0x6C);
        Add("i32.div_s",   0x6D);
        Add("i32.div_u",   0x6E);
        Add("i32.rem_s",   0x6F);
        Add("i32.rem_u",   0x70);
        Add("i32.and",     0x71);
        Add("i32.or",      0x72);
        Add("i32.xor",     0x73);
        Add("i32.shl",     0x74);
        Add("i32.shr_s",   0x75);
        Add("i32.shr_u",   0x76);
        Add("i32.rotl",    0x77);
        Add("i32.rotr",    0x78);

        // i64 arithmetic
        Add("i64.clz",     0x79);
        Add("i64.ctz",     0x7A);
        Add("i64.popcnt",  0x7B);
        Add("i64.add",     0x7C);
        Add("i64.sub",     0x7D);
        Add("i64.mul",     0x7E);
        Add("i64.div_s",   0x7F);
        Add("i64.div_u",   0x80);
        Add("i64.rem_s",   0x81);
        Add("i64.rem_u",   0x82);
        Add("i64.and",     0x83);
        Add("i64.or",      0x84);
        Add("i64.xor",     0x85);
        Add("i64.shl",     0x86);
        Add("i64.shr_s",   0x87);
        Add("i64.shr_u",   0x88);
        Add("i64.rotl",    0x89);
        Add("i64.rotr",    0x8A);

        // f32 arithmetic
        Add("f32.abs",      0x8B);
        Add("f32.neg",      0x8C);
        Add("f32.ceil",     0x8D);
        Add("f32.floor",    0x8E);
        Add("f32.trunc",    0x8F);
        Add("f32.nearest",  0x90);
        Add("f32.sqrt",     0x91);
        Add("f32.add",      0x92);
        Add("f32.sub",      0x93);
        Add("f32.mul",      0x94);
        Add("f32.div",      0x95);
        Add("f32.min",      0x96);
        Add("f32.max",      0x97);
        Add("f32.copysign",  0x98);

        // f64 arithmetic
        Add("f64.abs",      0x99);
        Add("f64.neg",      0x9A);
        Add("f64.ceil",     0x9B);
        Add("f64.floor",    0x9C);
        Add("f64.trunc",    0x9D);
        Add("f64.nearest",  0x9E);
        Add("f64.sqrt",     0x9F);
        Add("f64.add",      0xA0);
        Add("f64.sub",      0xA1);
        Add("f64.mul",      0xA2);
        Add("f64.div",      0xA3);
        Add("f64.min",      0xA4);
        Add("f64.max",      0xA5);
        Add("f64.copysign",  0xA6);

        // Conversions
        Add("i32.wrap_i64",        0xA7);
        Add("i32.trunc_f32_s",     0xA8);
        Add("i32.trunc_f32_u",     0xA9);
        Add("i32.trunc_f64_s",     0xAA);
        Add("i32.trunc_f64_u",     0xAB);
        Add("i64.extend_i32_s",    0xAC);
        Add("i64.extend_i32_u",    0xAD);
        Add("i64.trunc_f32_s",     0xAE);
        Add("i64.trunc_f32_u",     0xAF);
        Add("i64.trunc_f64_s",     0xB0);
        Add("i64.trunc_f64_u",     0xB1);
        Add("f32.convert_i32_s",   0xB2);
        Add("f32.convert_i32_u",   0xB3);
        Add("f32.convert_i64_s",   0xB4);
        Add("f32.convert_i64_u",   0xB5);
        Add("f32.demote_f64",      0xB6);
        Add("f64.convert_i32_s",   0xB7);
        Add("f64.convert_i32_u",   0xB8);
        Add("f64.convert_i64_s",   0xB9);
        Add("f64.convert_i64_u",   0xBA);
        Add("f64.promote_f32",     0xBB);
        Add("i32.reinterpret_f32", 0xBC);
        Add("i64.reinterpret_f64", 0xBD);
        Add("f32.reinterpret_i32", 0xBE);
        Add("f64.reinterpret_i64", 0xBF);

        // Sign extension
        Add("i32.extend8_s",  0xC0);
        Add("i32.extend16_s", 0xC1);
        Add("i64.extend8_s",  0xC2);
        Add("i64.extend16_s", 0xC3);
        Add("i64.extend32_s", 0xC4);

        // ── Missing simple opcodes ───────────────────────────────────────────

        // call_indirect / return_call / return_call_indirect
        Add("call_indirect",        0x11, ImmediateKind.CallIndirect);
        Add("return_call",          0x12, ImmediateKind.FuncIdx);
        Add("return_call_indirect", 0x13, ImmediateKind.CallIndirect);

        // Table get/set
        Add("table.get", 0x25, ImmediateKind.TableIdx);
        Add("table.set", 0x26, ImmediateKind.TableIdx);

        // Reference types
        Add("ref.null",    0xD0, ImmediateKind.RefType);
        Add("ref.is_null", 0xD1);
        Add("ref.func",    0xD2, ImmediateKind.FuncIdx);

        // ── 0xE0 prefix: stack-switching ────────────────────────────────────

        void AddE0(string name, byte sub, ImmediateKind imm = ImmediateKind.None) =>
            t[name] = new OpcodeEntry([0xE0, sub], imm);

        AddE0("cont.new",  0x00, ImmediateKind.TypeIdx);
        AddE0("cont.bind", 0x01, ImmediateKind.TypePair);
        AddE0("resume",    0x02, ImmediateKind.ResumeTable);
        AddE0("suspend",   0x03, ImmediateKind.TagIdx);
        AddE0("switch",    0x04, ImmediateKind.TypeTagPair);

        // ── 0xFC prefix: saturating truncation + bulk memory/table ops ───────

        void AddFC(string name, uint sub, ImmediateKind imm = ImmediateKind.None) =>
            t[name] = new OpcodeEntry([0xFC, (byte)sub], imm);

        // Saturating float-to-int conversions (sub 0-7)
        AddFC("i32.trunc_sat_f32_s", 0);
        AddFC("i32.trunc_sat_f32_u", 1);
        AddFC("i32.trunc_sat_f64_s", 2);
        AddFC("i32.trunc_sat_f64_u", 3);
        AddFC("i64.trunc_sat_f32_s", 4);
        AddFC("i64.trunc_sat_f32_u", 5);
        AddFC("i64.trunc_sat_f64_s", 6);
        AddFC("i64.trunc_sat_f64_u", 7);

        // Bulk memory operations (sub 8-11)
        AddFC("memory.init", 8,  ImmediateKind.DataMemIdx);
        AddFC("data.drop",   9,  ImmediateKind.DataIdx);
        AddFC("memory.copy", 10, ImmediateKind.MemCopy);
        AddFC("memory.fill", 11, ImmediateKind.MemIdx);

        // Bulk table operations (sub 12-17)
        AddFC("table.init", 12, ImmediateKind.ElemTableIdx);
        AddFC("elem.drop",  13, ImmediateKind.ElemIdx);
        AddFC("table.copy", 14, ImmediateKind.TableCopy);
        AddFC("table.grow", 15, ImmediateKind.TableIdx);
        AddFC("table.size", 16, ImmediateKind.TableIdx);
        AddFC("table.fill", 17, ImmediateKind.TableIdx);

        // ── 0xFD prefix: SIMD (128-bit vector) operations ───────────────────

        void AddFD(string name, uint sub, ImmediateKind imm = ImmediateKind.None)
        {
            byte[] opcodes = sub < 128
                ? [0xFD, (byte)sub]
                : [0xFD, (byte)((sub & 0x7F) | 0x80), (byte)(sub >> 7)];
            t[name] = new OpcodeEntry(opcodes, imm);
        }

        // v128 memory operations
        AddFD("v128.load",          0x00, ImmediateKind.MemArg);
        AddFD("v128.load8x8_s",    0x01, ImmediateKind.MemArg);
        AddFD("v128.load8x8_u",    0x02, ImmediateKind.MemArg);
        AddFD("v128.load16x4_s",   0x03, ImmediateKind.MemArg);
        AddFD("v128.load16x4_u",   0x04, ImmediateKind.MemArg);
        AddFD("v128.load32x2_s",   0x05, ImmediateKind.MemArg);
        AddFD("v128.load32x2_u",   0x06, ImmediateKind.MemArg);
        AddFD("v128.load8_splat",  0x07, ImmediateKind.MemArg);
        AddFD("v128.load16_splat", 0x08, ImmediateKind.MemArg);
        AddFD("v128.load32_splat", 0x09, ImmediateKind.MemArg);
        AddFD("v128.load64_splat", 0x0A, ImmediateKind.MemArg);
        AddFD("v128.store",        0x0B, ImmediateKind.MemArg);

        // v128.const and shuffle
        AddFD("v128.const",      0x0C, ImmediateKind.V128);
        AddFD("i8x16.shuffle",   0x0D, ImmediateKind.SimdShuffle);

        // Swizzle + splats
        AddFD("i8x16.swizzle", 0x0E);
        AddFD("i8x16.splat",  0x0F);
        AddFD("i16x8.splat",  0x10);
        AddFD("i32x4.splat",  0x11);
        AddFD("i64x2.splat",  0x12);
        AddFD("f32x4.splat",  0x13);
        AddFD("f64x2.splat",  0x14);

        // Lane extract/replace (1 byte lane index)
        AddFD("i8x16.extract_lane_s", 0x15, ImmediateKind.SimdLane);
        AddFD("i8x16.extract_lane_u", 0x16, ImmediateKind.SimdLane);
        AddFD("i8x16.replace_lane",   0x17, ImmediateKind.SimdLane);
        AddFD("i16x8.extract_lane_s", 0x18, ImmediateKind.SimdLane);
        AddFD("i16x8.extract_lane_u", 0x19, ImmediateKind.SimdLane);
        AddFD("i16x8.replace_lane",   0x1A, ImmediateKind.SimdLane);
        AddFD("i32x4.extract_lane",   0x1B, ImmediateKind.SimdLane);
        AddFD("i32x4.replace_lane",   0x1C, ImmediateKind.SimdLane);
        AddFD("i64x2.extract_lane",   0x1D, ImmediateKind.SimdLane);
        AddFD("i64x2.replace_lane",   0x1E, ImmediateKind.SimdLane);
        AddFD("f32x4.extract_lane",   0x1F, ImmediateKind.SimdLane);
        AddFD("f32x4.replace_lane",   0x20, ImmediateKind.SimdLane);
        AddFD("f64x2.extract_lane",   0x21, ImmediateKind.SimdLane);
        AddFD("f64x2.replace_lane",   0x22, ImmediateKind.SimdLane);

        // i8x16 comparison
        AddFD("i8x16.eq",   0x23);
        AddFD("i8x16.ne",   0x24);
        AddFD("i8x16.lt_s", 0x25);
        AddFD("i8x16.lt_u", 0x26);
        AddFD("i8x16.gt_s", 0x27);
        AddFD("i8x16.gt_u", 0x28);
        AddFD("i8x16.le_s", 0x29);
        AddFD("i8x16.le_u", 0x2A);
        AddFD("i8x16.ge_s", 0x2B);
        AddFD("i8x16.ge_u", 0x2C);

        // i16x8 comparison
        AddFD("i16x8.eq",   0x2D);
        AddFD("i16x8.ne",   0x2E);
        AddFD("i16x8.lt_s", 0x2F);
        AddFD("i16x8.lt_u", 0x30);
        AddFD("i16x8.gt_s", 0x31);
        AddFD("i16x8.gt_u", 0x32);
        AddFD("i16x8.le_s", 0x33);
        AddFD("i16x8.le_u", 0x34);
        AddFD("i16x8.ge_s", 0x35);
        AddFD("i16x8.ge_u", 0x36);

        // i32x4 comparison
        AddFD("i32x4.eq",   0x37);
        AddFD("i32x4.ne",   0x38);
        AddFD("i32x4.lt_s", 0x39);
        AddFD("i32x4.lt_u", 0x3A);
        AddFD("i32x4.gt_s", 0x3B);
        AddFD("i32x4.gt_u", 0x3C);
        AddFD("i32x4.le_s", 0x3D);
        AddFD("i32x4.le_u", 0x3E);
        AddFD("i32x4.ge_s", 0x3F);
        AddFD("i32x4.ge_u", 0x40);

        // f32x4 comparison
        AddFD("f32x4.eq", 0x41);
        AddFD("f32x4.ne", 0x42);
        AddFD("f32x4.lt", 0x43);
        AddFD("f32x4.gt", 0x44);
        AddFD("f32x4.le", 0x45);
        AddFD("f32x4.ge", 0x46);

        // f64x2 comparison
        AddFD("f64x2.eq", 0x47);
        AddFD("f64x2.ne", 0x48);
        AddFD("f64x2.lt", 0x49);
        AddFD("f64x2.gt", 0x4A);
        AddFD("f64x2.le", 0x4B);
        AddFD("f64x2.ge", 0x4C);

        // v128 bitwise
        AddFD("v128.not",       0x4D);
        AddFD("v128.and",       0x4E);
        AddFD("v128.andnot",    0x4F);
        AddFD("v128.or",        0x50);
        AddFD("v128.xor",       0x51);
        AddFD("v128.bitselect", 0x52);
        AddFD("v128.any_true",  0x53);

        // v128 load/store lane (memarg + 1 byte lane)
        AddFD("v128.load8_lane",   0x54, ImmediateKind.MemArgLane);
        AddFD("v128.load16_lane",  0x55, ImmediateKind.MemArgLane);
        AddFD("v128.load32_lane",  0x56, ImmediateKind.MemArgLane);
        AddFD("v128.load64_lane",  0x57, ImmediateKind.MemArgLane);
        AddFD("v128.store8_lane",  0x58, ImmediateKind.MemArgLane);
        AddFD("v128.store16_lane", 0x59, ImmediateKind.MemArgLane);
        AddFD("v128.store32_lane", 0x5A, ImmediateKind.MemArgLane);
        AddFD("v128.store64_lane", 0x5B, ImmediateKind.MemArgLane);

        // v128 load zero
        AddFD("v128.load32_zero", 0x5C, ImmediateKind.MemArg);
        AddFD("v128.load64_zero", 0x5D, ImmediateKind.MemArg);

        // f32x4/f64x2 conversions
        AddFD("f32x4.demote_f64x2_zero",  0x5E);
        AddFD("f64x2.promote_low_f32x4",  0x5F);

        // i8x16 unary
        AddFD("i8x16.abs",             0x60);
        AddFD("i8x16.neg",             0x61);
        AddFD("i8x16.popcnt",          0x62);
        AddFD("i8x16.all_true",        0x63);
        AddFD("i8x16.bitmask",         0x64);
        AddFD("i8x16.narrow_i16x8_s",  0x65);
        AddFD("i8x16.narrow_i16x8_u",  0x66);

        // f32x4 rounding (placed in i8x16 range per spec)
        AddFD("f32x4.ceil",    0x67);
        AddFD("f32x4.floor",   0x68);
        AddFD("f32x4.trunc",   0x69);
        AddFD("f32x4.nearest", 0x6A);

        // i8x16 shift/arithmetic
        AddFD("i8x16.shl",       0x6B);
        AddFD("i8x16.shr_s",     0x6C);
        AddFD("i8x16.shr_u",     0x6D);
        AddFD("i8x16.add",       0x6E);
        AddFD("i8x16.add_sat_s", 0x6F);
        AddFD("i8x16.add_sat_u", 0x70);
        AddFD("i8x16.sub",       0x71);
        AddFD("i8x16.sub_sat_s", 0x72);
        AddFD("i8x16.sub_sat_u", 0x73);

        // f64x2 rounding (interspersed per spec)
        AddFD("f64x2.ceil",  0x74);
        AddFD("f64x2.floor", 0x75);

        // i8x16 min/max
        AddFD("i8x16.min_s", 0x76);
        AddFD("i8x16.min_u", 0x77);
        AddFD("i8x16.max_s", 0x78);
        AddFD("i8x16.max_u", 0x79);

        // f64x2 rounding cont.
        AddFD("f64x2.trunc", 0x7A);

        // i8x16 avgr
        AddFD("i8x16.avgr_u", 0x7B);

        // Pairwise extension adds
        AddFD("i16x8.extadd_pairwise_i8x16_s", 0x7C);
        AddFD("i16x8.extadd_pairwise_i8x16_u", 0x7D);
        AddFD("i32x4.extadd_pairwise_i16x8_s", 0x7E);
        AddFD("i32x4.extadd_pairwise_i16x8_u", 0x7F);

        // i16x8 unary/narrowing/extend
        AddFD("i16x8.abs",                 0x80);
        AddFD("i16x8.neg",                 0x81);
        AddFD("i16x8.q15mulr_sat_s",       0x82);
        AddFD("i16x8.all_true",            0x83);
        AddFD("i16x8.bitmask",             0x84);
        AddFD("i16x8.narrow_i32x4_s",      0x85);
        AddFD("i16x8.narrow_i32x4_u",      0x86);
        AddFD("i16x8.extend_low_i8x16_s",  0x87);
        AddFD("i16x8.extend_high_i8x16_s", 0x88);
        AddFD("i16x8.extend_low_i8x16_u",  0x89);
        AddFD("i16x8.extend_high_i8x16_u", 0x8A);

        // i16x8 shift/arithmetic
        AddFD("i16x8.shl",       0x8B);
        AddFD("i16x8.shr_s",     0x8C);
        AddFD("i16x8.shr_u",     0x8D);
        AddFD("i16x8.add",       0x8E);
        AddFD("i16x8.add_sat_s", 0x8F);
        AddFD("i16x8.add_sat_u", 0x90);
        AddFD("i16x8.sub",       0x91);
        AddFD("i16x8.sub_sat_s", 0x92);
        AddFD("i16x8.sub_sat_u", 0x93);

        // f64x2 nearest (placed in i16x8 range per spec)
        AddFD("f64x2.nearest", 0x94);

        // i16x8 arithmetic cont.
        AddFD("i16x8.mul",   0x95);
        AddFD("i16x8.min_s", 0x96);
        AddFD("i16x8.min_u", 0x97);
        AddFD("i16x8.max_s", 0x98);
        AddFD("i16x8.max_u", 0x99);
        // 0x9A is unused in spec
        AddFD("i16x8.avgr_u", 0x9B);

        // i16x8 extended multiply
        AddFD("i16x8.extmul_low_i8x16_s",  0x9C);
        AddFD("i16x8.extmul_high_i8x16_s", 0x9D);
        AddFD("i16x8.extmul_low_i8x16_u",  0x9E);
        AddFD("i16x8.extmul_high_i8x16_u", 0x9F);

        // i32x4 unary
        AddFD("i32x4.abs",      0xA0);
        AddFD("i32x4.neg",      0xA1);
        // 0xA2 is unused in spec
        AddFD("i32x4.all_true", 0xA3);
        AddFD("i32x4.bitmask",  0xA4);
        // 0xA5-0xA6 are unused in spec

        // i32x4 extend
        AddFD("i32x4.extend_low_i16x8_s",  0xA7);
        AddFD("i32x4.extend_high_i16x8_s", 0xA8);
        AddFD("i32x4.extend_low_i16x8_u",  0xA9);
        AddFD("i32x4.extend_high_i16x8_u", 0xAA);

        // i32x4 shift/arithmetic
        AddFD("i32x4.shl",   0xAB);
        AddFD("i32x4.shr_s", 0xAC);
        AddFD("i32x4.shr_u", 0xAD);
        AddFD("i32x4.add",   0xAE);
        // 0xAF-0xB0 are unused in spec
        AddFD("i32x4.sub",   0xB1);
        // 0xB2-0xB4 are unused in spec
        AddFD("i32x4.mul",   0xB5);
        AddFD("i32x4.min_s", 0xB6);
        AddFD("i32x4.min_u", 0xB7);
        AddFD("i32x4.max_s", 0xB8);
        AddFD("i32x4.max_u", 0xB9);
        AddFD("i32x4.dot_i16x8_s", 0xBA);

        // i64x2.extmul_high_i32x4_u – see below at 0xDC-0xDF

        // i32x4 extended multiply
        AddFD("i32x4.extmul_low_i16x8_s",  0xBC);
        AddFD("i32x4.extmul_high_i16x8_s", 0xBD);
        AddFD("i32x4.extmul_low_i16x8_u",  0xBE);
        AddFD("i32x4.extmul_high_i16x8_u", 0xBF);

        // i64x2 unary
        AddFD("i64x2.abs",      0xC0);
        AddFD("i64x2.neg",      0xC1);
        // 0xC2 is unused in spec
        AddFD("i64x2.all_true", 0xC3);
        AddFD("i64x2.bitmask",  0xC4);
        // 0xC5-0xC6 are unused in spec

        // i64x2 extend
        AddFD("i64x2.extend_low_i32x4_s",  0xC7);
        AddFD("i64x2.extend_high_i32x4_s", 0xC8);
        AddFD("i64x2.extend_low_i32x4_u",  0xC9);
        AddFD("i64x2.extend_high_i32x4_u", 0xCA);

        // i64x2 shift/arithmetic
        AddFD("i64x2.shl",   0xCB);
        AddFD("i64x2.shr_s", 0xCC);
        AddFD("i64x2.shr_u", 0xCD);
        AddFD("i64x2.add",   0xCE);
        // 0xCF-0xD0 are unused in spec
        AddFD("i64x2.sub",   0xD1);

        // i64x2 extended multiply
        AddFD("i64x2.extmul_low_i32x4_s",  0xDC);
        AddFD("i64x2.extmul_high_i32x4_s", 0xDD);
        AddFD("i64x2.extmul_low_i32x4_u",  0xDE);
        AddFD("i64x2.extmul_high_i32x4_u", 0xDF);

        // i64x2 arithmetic + comparison
        AddFD("i64x2.mul",  0xD5);
        AddFD("i64x2.eq",   0xD6);
        AddFD("i64x2.ne",   0xD7);
        AddFD("i64x2.lt_s", 0xD8);
        AddFD("i64x2.gt_s", 0xD9);
        AddFD("i64x2.le_s", 0xDA);
        AddFD("i64x2.ge_s", 0xDB);

        // f32x4 arithmetic
        AddFD("f32x4.abs",  0xE0);
        AddFD("f32x4.neg",  0xE1);
        // 0xE2 is unused in spec
        AddFD("f32x4.sqrt", 0xE3);
        AddFD("f32x4.add",  0xE4);
        AddFD("f32x4.sub",  0xE5);
        AddFD("f32x4.mul",  0xE6);
        AddFD("f32x4.div",  0xE7);
        AddFD("f32x4.min",  0xE8);
        AddFD("f32x4.max",  0xE9);
        AddFD("f32x4.pmin", 0xEA);
        AddFD("f32x4.pmax", 0xEB);

        // f64x2 arithmetic
        AddFD("f64x2.abs",  0xEC);
        AddFD("f64x2.neg",  0xED);
        // 0xEE is unused in spec
        AddFD("f64x2.sqrt", 0xEF);
        AddFD("f64x2.add",  0xF0);
        AddFD("f64x2.sub",  0xF1);
        AddFD("f64x2.mul",  0xF2);
        AddFD("f64x2.div",  0xF3);
        AddFD("f64x2.min",  0xF4);
        AddFD("f64x2.max",  0xF5);
        AddFD("f64x2.pmin", 0xF6);
        AddFD("f64x2.pmax", 0xF7);

        // SIMD conversions
        AddFD("i32x4.trunc_sat_f32x4_s",      0xF8);
        AddFD("i32x4.trunc_sat_f32x4_u",      0xF9);
        AddFD("f32x4.convert_i32x4_s",        0xFA);
        AddFD("f32x4.convert_i32x4_u",        0xFB);
        AddFD("i32x4.trunc_sat_f64x2_s_zero", 0xFC);
        AddFD("i32x4.trunc_sat_f64x2_u_zero", 0xFD);
        AddFD("f64x2.convert_low_i32x4_s",    0xFE);
        AddFD("f64x2.convert_low_i32x4_u",    0xFF);

        // Relaxed SIMD (0xFD 0x100–0x113)
        AddFD("i8x16.relaxed_swizzle",                  0x100);
        AddFD("i32x4.relaxed_trunc_f32x4_s",            0x101);
        AddFD("i32x4.relaxed_trunc_f32x4_u",            0x102);
        AddFD("i32x4.relaxed_trunc_f64x2_s_zero",       0x103);
        AddFD("i32x4.relaxed_trunc_f64x2_u_zero",       0x104);
        AddFD("f32x4.relaxed_madd",                     0x105);
        AddFD("f32x4.relaxed_nmadd",                    0x106);
        AddFD("f64x2.relaxed_madd",                     0x107);
        AddFD("f64x2.relaxed_nmadd",                    0x108);
        AddFD("i8x16.relaxed_laneselect",               0x109);
        AddFD("i16x8.relaxed_laneselect",               0x10A);
        AddFD("i32x4.relaxed_laneselect",               0x10B);
        AddFD("i64x2.relaxed_laneselect",               0x10C);
        AddFD("f32x4.relaxed_min",                      0x10D);
        AddFD("f32x4.relaxed_max",                      0x10E);
        AddFD("f64x2.relaxed_min",                      0x10F);
        AddFD("f64x2.relaxed_max",                      0x110);
        AddFD("i16x8.relaxed_q15mulr_s",                0x111);
        AddFD("i16x8.relaxed_dot_i8x16_i7x16_s",        0x112);
        AddFD("i32x4.relaxed_dot_i8x16_i7x16_add_s",   0x113);

        return t;
    }

    /// <summary>Return the natural alignment (log2) for a memory instruction mnemonic.</summary>
    public static uint NaturalAlignment(string mnemonic)
    {
        if (mnemonic.StartsWith("v128."))
        {
            // v128.load / v128.store (full 128-bit) → 16 bytes → log2 = 4
            if (mnemonic is "v128.load" or "v128.store") return 4;
            // loadNNxM patterns (e.g. load8x8, load16x4, load32x2) → all load 8 bytes
            if (mnemonic.Contains("x8") || mnemonic.Contains("x4") || mnemonic.Contains("x2")) return 3;
            // lane/splat/zero: data size from the number in the name
            if (mnemonic.Contains("64")) return 3; // 8 bytes
            if (mnemonic.Contains("32")) return 2; // 4 bytes
            if (mnemonic.Contains("16")) return 1; // 2 bytes
            if (mnemonic.Contains('8'))  return 0; // 1 byte
            return 4; // default for v128
        }
        if (mnemonic.Contains('8'))  return 0; // 1 byte
        if (mnemonic.Contains("16")) return 1; // 2 bytes
        if (mnemonic.StartsWith("i32") || mnemonic.StartsWith("f32")) return 2; // 4 bytes
        if (mnemonic.StartsWith("i64") || mnemonic.StartsWith("f64")) return 3; // 8 bytes
        return 0;
    }
}
