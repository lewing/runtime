// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Generated from Microsoft.NET.WebAssembly.Wat.OpcodeTable (stack switching, 0xE0, excluded) by
// WasmCodeWriterTests.GenerateWasmOpSource. Do not edit; regenerate with
// NESM_REGENERATE_WASMOP=1 dotnet test tests/Microsoft.NET.WebAssembly.Toolchain.Tests --filter WasmOpSourceIsGeneratedFromTheOpcodeTable

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// WebAssembly instruction opcodes for <see cref="WasmCodeWriter"/>. A single-byte opcode is its value; a prefixed
/// one is <c>prefix &lt;&lt; 24 | sub-opcode</c> (the sub-opcode is written as a u32 LEB128), as dotnet/runtime's
/// <c>WasmExprKind</c> encodes them.
/// </summary>
public enum WasmOp : uint
{
    /// <summary><c>unreachable</c></summary>
    Unreachable = 0x0,
    /// <summary><c>nop</c></summary>
    Nop = 0x1,
    /// <summary><c>block</c></summary>
    Block = 0x2,
    /// <summary><c>loop</c></summary>
    Loop = 0x3,
    /// <summary><c>if</c></summary>
    If = 0x4,
    /// <summary><c>else</c></summary>
    Else = 0x5,
    /// <summary><c>try</c></summary>
    Try = 0x6,
    /// <summary><c>catch</c></summary>
    Catch = 0x7,
    /// <summary><c>throw</c></summary>
    Throw = 0x8,
    /// <summary><c>rethrow</c></summary>
    Rethrow = 0x9,
    /// <summary><c>throw_ref</c></summary>
    ThrowRef = 0xA,
    /// <summary><c>end</c></summary>
    End = 0xB,
    /// <summary><c>br</c></summary>
    Br = 0xC,
    /// <summary><c>br_if</c></summary>
    BrIf = 0xD,
    /// <summary><c>br_table</c></summary>
    BrTable = 0xE,
    /// <summary><c>return</c></summary>
    Return = 0xF,
    /// <summary><c>call</c></summary>
    Call = 0x10,
    /// <summary><c>call_indirect</c></summary>
    CallIndirect = 0x11,
    /// <summary><c>return_call</c></summary>
    ReturnCall = 0x12,
    /// <summary><c>return_call_indirect</c></summary>
    ReturnCallIndirect = 0x13,
    /// <summary><c>call_ref</c></summary>
    CallRef = 0x14,
    /// <summary><c>return_call_ref</c></summary>
    ReturnCallRef = 0x15,
    /// <summary><c>delegate</c></summary>
    Delegate = 0x18,
    /// <summary><c>catch_all</c></summary>
    CatchAll = 0x19,
    /// <summary><c>drop</c></summary>
    Drop = 0x1A,
    /// <summary><c>select</c></summary>
    Select = 0x1B,
    /// <summary><c>select (typed)</c></summary>
    SelectTyped = 0x1C,
    /// <summary><c>try_table</c></summary>
    TryTable = 0x1F,
    /// <summary><c>local.get</c></summary>
    LocalGet = 0x20,
    /// <summary><c>local.set</c></summary>
    LocalSet = 0x21,
    /// <summary><c>local.tee</c></summary>
    LocalTee = 0x22,
    /// <summary><c>global.get</c></summary>
    GlobalGet = 0x23,
    /// <summary><c>global.set</c></summary>
    GlobalSet = 0x24,
    /// <summary><c>table.get</c></summary>
    TableGet = 0x25,
    /// <summary><c>table.set</c></summary>
    TableSet = 0x26,
    /// <summary><c>i32.load</c></summary>
    I32Load = 0x28,
    /// <summary><c>i64.load</c></summary>
    I64Load = 0x29,
    /// <summary><c>f32.load</c></summary>
    F32Load = 0x2A,
    /// <summary><c>f64.load</c></summary>
    F64Load = 0x2B,
    /// <summary><c>i32.load8_s</c></summary>
    I32Load8S = 0x2C,
    /// <summary><c>i32.load8_u</c></summary>
    I32Load8U = 0x2D,
    /// <summary><c>i32.load16_s</c></summary>
    I32Load16S = 0x2E,
    /// <summary><c>i32.load16_u</c></summary>
    I32Load16U = 0x2F,
    /// <summary><c>i64.load8_s</c></summary>
    I64Load8S = 0x30,
    /// <summary><c>i64.load8_u</c></summary>
    I64Load8U = 0x31,
    /// <summary><c>i64.load16_s</c></summary>
    I64Load16S = 0x32,
    /// <summary><c>i64.load16_u</c></summary>
    I64Load16U = 0x33,
    /// <summary><c>i64.load32_s</c></summary>
    I64Load32S = 0x34,
    /// <summary><c>i64.load32_u</c></summary>
    I64Load32U = 0x35,
    /// <summary><c>i32.store</c></summary>
    I32Store = 0x36,
    /// <summary><c>i64.store</c></summary>
    I64Store = 0x37,
    /// <summary><c>f32.store</c></summary>
    F32Store = 0x38,
    /// <summary><c>f64.store</c></summary>
    F64Store = 0x39,
    /// <summary><c>i32.store8</c></summary>
    I32Store8 = 0x3A,
    /// <summary><c>i32.store16</c></summary>
    I32Store16 = 0x3B,
    /// <summary><c>i64.store8</c></summary>
    I64Store8 = 0x3C,
    /// <summary><c>i64.store16</c></summary>
    I64Store16 = 0x3D,
    /// <summary><c>i64.store32</c></summary>
    I64Store32 = 0x3E,
    /// <summary><c>memory.size</c></summary>
    MemorySize = 0x3F,
    /// <summary><c>memory.grow</c></summary>
    MemoryGrow = 0x40,
    /// <summary><c>i32.const</c></summary>
    I32Const = 0x41,
    /// <summary><c>i64.const</c></summary>
    I64Const = 0x42,
    /// <summary><c>f32.const</c></summary>
    F32Const = 0x43,
    /// <summary><c>f64.const</c></summary>
    F64Const = 0x44,
    /// <summary><c>i32.eqz</c></summary>
    I32Eqz = 0x45,
    /// <summary><c>i32.eq</c></summary>
    I32Eq = 0x46,
    /// <summary><c>i32.ne</c></summary>
    I32Ne = 0x47,
    /// <summary><c>i32.lt_s</c></summary>
    I32LtS = 0x48,
    /// <summary><c>i32.lt_u</c></summary>
    I32LtU = 0x49,
    /// <summary><c>i32.gt_s</c></summary>
    I32GtS = 0x4A,
    /// <summary><c>i32.gt_u</c></summary>
    I32GtU = 0x4B,
    /// <summary><c>i32.le_s</c></summary>
    I32LeS = 0x4C,
    /// <summary><c>i32.le_u</c></summary>
    I32LeU = 0x4D,
    /// <summary><c>i32.ge_s</c></summary>
    I32GeS = 0x4E,
    /// <summary><c>i32.ge_u</c></summary>
    I32GeU = 0x4F,
    /// <summary><c>i64.eqz</c></summary>
    I64Eqz = 0x50,
    /// <summary><c>i64.eq</c></summary>
    I64Eq = 0x51,
    /// <summary><c>i64.ne</c></summary>
    I64Ne = 0x52,
    /// <summary><c>i64.lt_s</c></summary>
    I64LtS = 0x53,
    /// <summary><c>i64.lt_u</c></summary>
    I64LtU = 0x54,
    /// <summary><c>i64.gt_s</c></summary>
    I64GtS = 0x55,
    /// <summary><c>i64.gt_u</c></summary>
    I64GtU = 0x56,
    /// <summary><c>i64.le_s</c></summary>
    I64LeS = 0x57,
    /// <summary><c>i64.le_u</c></summary>
    I64LeU = 0x58,
    /// <summary><c>i64.ge_s</c></summary>
    I64GeS = 0x59,
    /// <summary><c>i64.ge_u</c></summary>
    I64GeU = 0x5A,
    /// <summary><c>f32.eq</c></summary>
    F32Eq = 0x5B,
    /// <summary><c>f32.ne</c></summary>
    F32Ne = 0x5C,
    /// <summary><c>f32.lt</c></summary>
    F32Lt = 0x5D,
    /// <summary><c>f32.gt</c></summary>
    F32Gt = 0x5E,
    /// <summary><c>f32.le</c></summary>
    F32Le = 0x5F,
    /// <summary><c>f32.ge</c></summary>
    F32Ge = 0x60,
    /// <summary><c>f64.eq</c></summary>
    F64Eq = 0x61,
    /// <summary><c>f64.ne</c></summary>
    F64Ne = 0x62,
    /// <summary><c>f64.lt</c></summary>
    F64Lt = 0x63,
    /// <summary><c>f64.gt</c></summary>
    F64Gt = 0x64,
    /// <summary><c>f64.le</c></summary>
    F64Le = 0x65,
    /// <summary><c>f64.ge</c></summary>
    F64Ge = 0x66,
    /// <summary><c>i32.clz</c></summary>
    I32Clz = 0x67,
    /// <summary><c>i32.ctz</c></summary>
    I32Ctz = 0x68,
    /// <summary><c>i32.popcnt</c></summary>
    I32Popcnt = 0x69,
    /// <summary><c>i32.add</c></summary>
    I32Add = 0x6A,
    /// <summary><c>i32.sub</c></summary>
    I32Sub = 0x6B,
    /// <summary><c>i32.mul</c></summary>
    I32Mul = 0x6C,
    /// <summary><c>i32.div_s</c></summary>
    I32DivS = 0x6D,
    /// <summary><c>i32.div_u</c></summary>
    I32DivU = 0x6E,
    /// <summary><c>i32.rem_s</c></summary>
    I32RemS = 0x6F,
    /// <summary><c>i32.rem_u</c></summary>
    I32RemU = 0x70,
    /// <summary><c>i32.and</c></summary>
    I32And = 0x71,
    /// <summary><c>i32.or</c></summary>
    I32Or = 0x72,
    /// <summary><c>i32.xor</c></summary>
    I32Xor = 0x73,
    /// <summary><c>i32.shl</c></summary>
    I32Shl = 0x74,
    /// <summary><c>i32.shr_s</c></summary>
    I32ShrS = 0x75,
    /// <summary><c>i32.shr_u</c></summary>
    I32ShrU = 0x76,
    /// <summary><c>i32.rotl</c></summary>
    I32Rotl = 0x77,
    /// <summary><c>i32.rotr</c></summary>
    I32Rotr = 0x78,
    /// <summary><c>i64.clz</c></summary>
    I64Clz = 0x79,
    /// <summary><c>i64.ctz</c></summary>
    I64Ctz = 0x7A,
    /// <summary><c>i64.popcnt</c></summary>
    I64Popcnt = 0x7B,
    /// <summary><c>i64.add</c></summary>
    I64Add = 0x7C,
    /// <summary><c>i64.sub</c></summary>
    I64Sub = 0x7D,
    /// <summary><c>i64.mul</c></summary>
    I64Mul = 0x7E,
    /// <summary><c>i64.div_s</c></summary>
    I64DivS = 0x7F,
    /// <summary><c>i64.div_u</c></summary>
    I64DivU = 0x80,
    /// <summary><c>i64.rem_s</c></summary>
    I64RemS = 0x81,
    /// <summary><c>i64.rem_u</c></summary>
    I64RemU = 0x82,
    /// <summary><c>i64.and</c></summary>
    I64And = 0x83,
    /// <summary><c>i64.or</c></summary>
    I64Or = 0x84,
    /// <summary><c>i64.xor</c></summary>
    I64Xor = 0x85,
    /// <summary><c>i64.shl</c></summary>
    I64Shl = 0x86,
    /// <summary><c>i64.shr_s</c></summary>
    I64ShrS = 0x87,
    /// <summary><c>i64.shr_u</c></summary>
    I64ShrU = 0x88,
    /// <summary><c>i64.rotl</c></summary>
    I64Rotl = 0x89,
    /// <summary><c>i64.rotr</c></summary>
    I64Rotr = 0x8A,
    /// <summary><c>f32.abs</c></summary>
    F32Abs = 0x8B,
    /// <summary><c>f32.neg</c></summary>
    F32Neg = 0x8C,
    /// <summary><c>f32.ceil</c></summary>
    F32Ceil = 0x8D,
    /// <summary><c>f32.floor</c></summary>
    F32Floor = 0x8E,
    /// <summary><c>f32.trunc</c></summary>
    F32Trunc = 0x8F,
    /// <summary><c>f32.nearest</c></summary>
    F32Nearest = 0x90,
    /// <summary><c>f32.sqrt</c></summary>
    F32Sqrt = 0x91,
    /// <summary><c>f32.add</c></summary>
    F32Add = 0x92,
    /// <summary><c>f32.sub</c></summary>
    F32Sub = 0x93,
    /// <summary><c>f32.mul</c></summary>
    F32Mul = 0x94,
    /// <summary><c>f32.div</c></summary>
    F32Div = 0x95,
    /// <summary><c>f32.min</c></summary>
    F32Min = 0x96,
    /// <summary><c>f32.max</c></summary>
    F32Max = 0x97,
    /// <summary><c>f32.copysign</c></summary>
    F32Copysign = 0x98,
    /// <summary><c>f64.abs</c></summary>
    F64Abs = 0x99,
    /// <summary><c>f64.neg</c></summary>
    F64Neg = 0x9A,
    /// <summary><c>f64.ceil</c></summary>
    F64Ceil = 0x9B,
    /// <summary><c>f64.floor</c></summary>
    F64Floor = 0x9C,
    /// <summary><c>f64.trunc</c></summary>
    F64Trunc = 0x9D,
    /// <summary><c>f64.nearest</c></summary>
    F64Nearest = 0x9E,
    /// <summary><c>f64.sqrt</c></summary>
    F64Sqrt = 0x9F,
    /// <summary><c>f64.add</c></summary>
    F64Add = 0xA0,
    /// <summary><c>f64.sub</c></summary>
    F64Sub = 0xA1,
    /// <summary><c>f64.mul</c></summary>
    F64Mul = 0xA2,
    /// <summary><c>f64.div</c></summary>
    F64Div = 0xA3,
    /// <summary><c>f64.min</c></summary>
    F64Min = 0xA4,
    /// <summary><c>f64.max</c></summary>
    F64Max = 0xA5,
    /// <summary><c>f64.copysign</c></summary>
    F64Copysign = 0xA6,
    /// <summary><c>i32.wrap_i64</c></summary>
    I32WrapI64 = 0xA7,
    /// <summary><c>i32.trunc_f32_s</c></summary>
    I32TruncF32S = 0xA8,
    /// <summary><c>i32.trunc_f32_u</c></summary>
    I32TruncF32U = 0xA9,
    /// <summary><c>i32.trunc_f64_s</c></summary>
    I32TruncF64S = 0xAA,
    /// <summary><c>i32.trunc_f64_u</c></summary>
    I32TruncF64U = 0xAB,
    /// <summary><c>i64.extend_i32_s</c></summary>
    I64ExtendI32S = 0xAC,
    /// <summary><c>i64.extend_i32_u</c></summary>
    I64ExtendI32U = 0xAD,
    /// <summary><c>i64.trunc_f32_s</c></summary>
    I64TruncF32S = 0xAE,
    /// <summary><c>i64.trunc_f32_u</c></summary>
    I64TruncF32U = 0xAF,
    /// <summary><c>i64.trunc_f64_s</c></summary>
    I64TruncF64S = 0xB0,
    /// <summary><c>i64.trunc_f64_u</c></summary>
    I64TruncF64U = 0xB1,
    /// <summary><c>f32.convert_i32_s</c></summary>
    F32ConvertI32S = 0xB2,
    /// <summary><c>f32.convert_i32_u</c></summary>
    F32ConvertI32U = 0xB3,
    /// <summary><c>f32.convert_i64_s</c></summary>
    F32ConvertI64S = 0xB4,
    /// <summary><c>f32.convert_i64_u</c></summary>
    F32ConvertI64U = 0xB5,
    /// <summary><c>f32.demote_f64</c></summary>
    F32DemoteF64 = 0xB6,
    /// <summary><c>f64.convert_i32_s</c></summary>
    F64ConvertI32S = 0xB7,
    /// <summary><c>f64.convert_i32_u</c></summary>
    F64ConvertI32U = 0xB8,
    /// <summary><c>f64.convert_i64_s</c></summary>
    F64ConvertI64S = 0xB9,
    /// <summary><c>f64.convert_i64_u</c></summary>
    F64ConvertI64U = 0xBA,
    /// <summary><c>f64.promote_f32</c></summary>
    F64PromoteF32 = 0xBB,
    /// <summary><c>i32.reinterpret_f32</c></summary>
    I32ReinterpretF32 = 0xBC,
    /// <summary><c>i64.reinterpret_f64</c></summary>
    I64ReinterpretF64 = 0xBD,
    /// <summary><c>f32.reinterpret_i32</c></summary>
    F32ReinterpretI32 = 0xBE,
    /// <summary><c>f64.reinterpret_i64</c></summary>
    F64ReinterpretI64 = 0xBF,
    /// <summary><c>i32.extend8_s</c></summary>
    I32Extend8S = 0xC0,
    /// <summary><c>i32.extend16_s</c></summary>
    I32Extend16S = 0xC1,
    /// <summary><c>i64.extend8_s</c></summary>
    I64Extend8S = 0xC2,
    /// <summary><c>i64.extend16_s</c></summary>
    I64Extend16S = 0xC3,
    /// <summary><c>i64.extend32_s</c></summary>
    I64Extend32S = 0xC4,
    /// <summary><c>ref.null</c></summary>
    RefNull = 0xD0,
    /// <summary><c>ref.is_null</c></summary>
    RefIsNull = 0xD1,
    /// <summary><c>ref.func</c></summary>
    RefFunc = 0xD2,
    /// <summary><c>ref.eq</c></summary>
    RefEq = 0xD3,
    /// <summary><c>ref.as_non_null</c></summary>
    RefAsNonNull = 0xD4,
    /// <summary><c>br_on_null</c></summary>
    BrOnNull = 0xD5,
    /// <summary><c>br_on_non_null</c></summary>
    BrOnNonNull = 0xD6,
    /// <summary><c>i32.trunc_sat_f32_s</c></summary>
    I32TruncSatF32S = 0xFC000000,
    /// <summary><c>i32.trunc_sat_f32_u</c></summary>
    I32TruncSatF32U = 0xFC000001,
    /// <summary><c>i32.trunc_sat_f64_s</c></summary>
    I32TruncSatF64S = 0xFC000002,
    /// <summary><c>i32.trunc_sat_f64_u</c></summary>
    I32TruncSatF64U = 0xFC000003,
    /// <summary><c>i64.trunc_sat_f32_s</c></summary>
    I64TruncSatF32S = 0xFC000004,
    /// <summary><c>i64.trunc_sat_f32_u</c></summary>
    I64TruncSatF32U = 0xFC000005,
    /// <summary><c>i64.trunc_sat_f64_s</c></summary>
    I64TruncSatF64S = 0xFC000006,
    /// <summary><c>i64.trunc_sat_f64_u</c></summary>
    I64TruncSatF64U = 0xFC000007,
    /// <summary><c>memory.init</c></summary>
    MemoryInit = 0xFC000008,
    /// <summary><c>data.drop</c></summary>
    DataDrop = 0xFC000009,
    /// <summary><c>memory.copy</c></summary>
    MemoryCopy = 0xFC00000A,
    /// <summary><c>memory.fill</c></summary>
    MemoryFill = 0xFC00000B,
    /// <summary><c>table.init</c></summary>
    TableInit = 0xFC00000C,
    /// <summary><c>elem.drop</c></summary>
    ElemDrop = 0xFC00000D,
    /// <summary><c>table.copy</c></summary>
    TableCopy = 0xFC00000E,
    /// <summary><c>table.grow</c></summary>
    TableGrow = 0xFC00000F,
    /// <summary><c>table.size</c></summary>
    TableSize = 0xFC000010,
    /// <summary><c>table.fill</c></summary>
    TableFill = 0xFC000011,
    /// <summary><c>i64.add128</c></summary>
    I64Add128 = 0xFC000013,
    /// <summary><c>i64.sub128</c></summary>
    I64Sub128 = 0xFC000014,
    /// <summary><c>i64.mul_wide_s</c></summary>
    I64MulWideS = 0xFC000015,
    /// <summary><c>i64.mul_wide_u</c></summary>
    I64MulWideU = 0xFC000016,
    /// <summary><c>v128.load</c></summary>
    V128Load = 0xFD000000,
    /// <summary><c>v128.load8x8_s</c></summary>
    V128Load8x8S = 0xFD000001,
    /// <summary><c>v128.load8x8_u</c></summary>
    V128Load8x8U = 0xFD000002,
    /// <summary><c>v128.load16x4_s</c></summary>
    V128Load16x4S = 0xFD000003,
    /// <summary><c>v128.load16x4_u</c></summary>
    V128Load16x4U = 0xFD000004,
    /// <summary><c>v128.load32x2_s</c></summary>
    V128Load32x2S = 0xFD000005,
    /// <summary><c>v128.load32x2_u</c></summary>
    V128Load32x2U = 0xFD000006,
    /// <summary><c>v128.load8_splat</c></summary>
    V128Load8Splat = 0xFD000007,
    /// <summary><c>v128.load16_splat</c></summary>
    V128Load16Splat = 0xFD000008,
    /// <summary><c>v128.load32_splat</c></summary>
    V128Load32Splat = 0xFD000009,
    /// <summary><c>v128.load64_splat</c></summary>
    V128Load64Splat = 0xFD00000A,
    /// <summary><c>v128.store</c></summary>
    V128Store = 0xFD00000B,
    /// <summary><c>v128.const</c></summary>
    V128Const = 0xFD00000C,
    /// <summary><c>i8x16.shuffle</c></summary>
    I8x16Shuffle = 0xFD00000D,
    /// <summary><c>i8x16.swizzle</c></summary>
    I8x16Swizzle = 0xFD00000E,
    /// <summary><c>i8x16.splat</c></summary>
    I8x16Splat = 0xFD00000F,
    /// <summary><c>i16x8.splat</c></summary>
    I16x8Splat = 0xFD000010,
    /// <summary><c>i32x4.splat</c></summary>
    I32x4Splat = 0xFD000011,
    /// <summary><c>i64x2.splat</c></summary>
    I64x2Splat = 0xFD000012,
    /// <summary><c>f32x4.splat</c></summary>
    F32x4Splat = 0xFD000013,
    /// <summary><c>f64x2.splat</c></summary>
    F64x2Splat = 0xFD000014,
    /// <summary><c>i8x16.extract_lane_s</c></summary>
    I8x16ExtractLaneS = 0xFD000015,
    /// <summary><c>i8x16.extract_lane_u</c></summary>
    I8x16ExtractLaneU = 0xFD000016,
    /// <summary><c>i8x16.replace_lane</c></summary>
    I8x16ReplaceLane = 0xFD000017,
    /// <summary><c>i16x8.extract_lane_s</c></summary>
    I16x8ExtractLaneS = 0xFD000018,
    /// <summary><c>i16x8.extract_lane_u</c></summary>
    I16x8ExtractLaneU = 0xFD000019,
    /// <summary><c>i16x8.replace_lane</c></summary>
    I16x8ReplaceLane = 0xFD00001A,
    /// <summary><c>i32x4.extract_lane</c></summary>
    I32x4ExtractLane = 0xFD00001B,
    /// <summary><c>i32x4.replace_lane</c></summary>
    I32x4ReplaceLane = 0xFD00001C,
    /// <summary><c>i64x2.extract_lane</c></summary>
    I64x2ExtractLane = 0xFD00001D,
    /// <summary><c>i64x2.replace_lane</c></summary>
    I64x2ReplaceLane = 0xFD00001E,
    /// <summary><c>f32x4.extract_lane</c></summary>
    F32x4ExtractLane = 0xFD00001F,
    /// <summary><c>f32x4.replace_lane</c></summary>
    F32x4ReplaceLane = 0xFD000020,
    /// <summary><c>f64x2.extract_lane</c></summary>
    F64x2ExtractLane = 0xFD000021,
    /// <summary><c>f64x2.replace_lane</c></summary>
    F64x2ReplaceLane = 0xFD000022,
    /// <summary><c>i8x16.eq</c></summary>
    I8x16Eq = 0xFD000023,
    /// <summary><c>i8x16.ne</c></summary>
    I8x16Ne = 0xFD000024,
    /// <summary><c>i8x16.lt_s</c></summary>
    I8x16LtS = 0xFD000025,
    /// <summary><c>i8x16.lt_u</c></summary>
    I8x16LtU = 0xFD000026,
    /// <summary><c>i8x16.gt_s</c></summary>
    I8x16GtS = 0xFD000027,
    /// <summary><c>i8x16.gt_u</c></summary>
    I8x16GtU = 0xFD000028,
    /// <summary><c>i8x16.le_s</c></summary>
    I8x16LeS = 0xFD000029,
    /// <summary><c>i8x16.le_u</c></summary>
    I8x16LeU = 0xFD00002A,
    /// <summary><c>i8x16.ge_s</c></summary>
    I8x16GeS = 0xFD00002B,
    /// <summary><c>i8x16.ge_u</c></summary>
    I8x16GeU = 0xFD00002C,
    /// <summary><c>i16x8.eq</c></summary>
    I16x8Eq = 0xFD00002D,
    /// <summary><c>i16x8.ne</c></summary>
    I16x8Ne = 0xFD00002E,
    /// <summary><c>i16x8.lt_s</c></summary>
    I16x8LtS = 0xFD00002F,
    /// <summary><c>i16x8.lt_u</c></summary>
    I16x8LtU = 0xFD000030,
    /// <summary><c>i16x8.gt_s</c></summary>
    I16x8GtS = 0xFD000031,
    /// <summary><c>i16x8.gt_u</c></summary>
    I16x8GtU = 0xFD000032,
    /// <summary><c>i16x8.le_s</c></summary>
    I16x8LeS = 0xFD000033,
    /// <summary><c>i16x8.le_u</c></summary>
    I16x8LeU = 0xFD000034,
    /// <summary><c>i16x8.ge_s</c></summary>
    I16x8GeS = 0xFD000035,
    /// <summary><c>i16x8.ge_u</c></summary>
    I16x8GeU = 0xFD000036,
    /// <summary><c>i32x4.eq</c></summary>
    I32x4Eq = 0xFD000037,
    /// <summary><c>i32x4.ne</c></summary>
    I32x4Ne = 0xFD000038,
    /// <summary><c>i32x4.lt_s</c></summary>
    I32x4LtS = 0xFD000039,
    /// <summary><c>i32x4.lt_u</c></summary>
    I32x4LtU = 0xFD00003A,
    /// <summary><c>i32x4.gt_s</c></summary>
    I32x4GtS = 0xFD00003B,
    /// <summary><c>i32x4.gt_u</c></summary>
    I32x4GtU = 0xFD00003C,
    /// <summary><c>i32x4.le_s</c></summary>
    I32x4LeS = 0xFD00003D,
    /// <summary><c>i32x4.le_u</c></summary>
    I32x4LeU = 0xFD00003E,
    /// <summary><c>i32x4.ge_s</c></summary>
    I32x4GeS = 0xFD00003F,
    /// <summary><c>i32x4.ge_u</c></summary>
    I32x4GeU = 0xFD000040,
    /// <summary><c>f32x4.eq</c></summary>
    F32x4Eq = 0xFD000041,
    /// <summary><c>f32x4.ne</c></summary>
    F32x4Ne = 0xFD000042,
    /// <summary><c>f32x4.lt</c></summary>
    F32x4Lt = 0xFD000043,
    /// <summary><c>f32x4.gt</c></summary>
    F32x4Gt = 0xFD000044,
    /// <summary><c>f32x4.le</c></summary>
    F32x4Le = 0xFD000045,
    /// <summary><c>f32x4.ge</c></summary>
    F32x4Ge = 0xFD000046,
    /// <summary><c>f64x2.eq</c></summary>
    F64x2Eq = 0xFD000047,
    /// <summary><c>f64x2.ne</c></summary>
    F64x2Ne = 0xFD000048,
    /// <summary><c>f64x2.lt</c></summary>
    F64x2Lt = 0xFD000049,
    /// <summary><c>f64x2.gt</c></summary>
    F64x2Gt = 0xFD00004A,
    /// <summary><c>f64x2.le</c></summary>
    F64x2Le = 0xFD00004B,
    /// <summary><c>f64x2.ge</c></summary>
    F64x2Ge = 0xFD00004C,
    /// <summary><c>v128.not</c></summary>
    V128Not = 0xFD00004D,
    /// <summary><c>v128.and</c></summary>
    V128And = 0xFD00004E,
    /// <summary><c>v128.andnot</c></summary>
    V128Andnot = 0xFD00004F,
    /// <summary><c>v128.or</c></summary>
    V128Or = 0xFD000050,
    /// <summary><c>v128.xor</c></summary>
    V128Xor = 0xFD000051,
    /// <summary><c>v128.bitselect</c></summary>
    V128Bitselect = 0xFD000052,
    /// <summary><c>v128.any_true</c></summary>
    V128AnyTrue = 0xFD000053,
    /// <summary><c>v128.load8_lane</c></summary>
    V128Load8Lane = 0xFD000054,
    /// <summary><c>v128.load16_lane</c></summary>
    V128Load16Lane = 0xFD000055,
    /// <summary><c>v128.load32_lane</c></summary>
    V128Load32Lane = 0xFD000056,
    /// <summary><c>v128.load64_lane</c></summary>
    V128Load64Lane = 0xFD000057,
    /// <summary><c>v128.store8_lane</c></summary>
    V128Store8Lane = 0xFD000058,
    /// <summary><c>v128.store16_lane</c></summary>
    V128Store16Lane = 0xFD000059,
    /// <summary><c>v128.store32_lane</c></summary>
    V128Store32Lane = 0xFD00005A,
    /// <summary><c>v128.store64_lane</c></summary>
    V128Store64Lane = 0xFD00005B,
    /// <summary><c>v128.load32_zero</c></summary>
    V128Load32Zero = 0xFD00005C,
    /// <summary><c>v128.load64_zero</c></summary>
    V128Load64Zero = 0xFD00005D,
    /// <summary><c>f32x4.demote_f64x2_zero</c></summary>
    F32x4DemoteF64x2Zero = 0xFD00005E,
    /// <summary><c>f64x2.promote_low_f32x4</c></summary>
    F64x2PromoteLowF32x4 = 0xFD00005F,
    /// <summary><c>i8x16.abs</c></summary>
    I8x16Abs = 0xFD000060,
    /// <summary><c>i8x16.neg</c></summary>
    I8x16Neg = 0xFD000061,
    /// <summary><c>i8x16.popcnt</c></summary>
    I8x16Popcnt = 0xFD000062,
    /// <summary><c>i8x16.all_true</c></summary>
    I8x16AllTrue = 0xFD000063,
    /// <summary><c>i8x16.bitmask</c></summary>
    I8x16Bitmask = 0xFD000064,
    /// <summary><c>i8x16.narrow_i16x8_s</c></summary>
    I8x16NarrowI16x8S = 0xFD000065,
    /// <summary><c>i8x16.narrow_i16x8_u</c></summary>
    I8x16NarrowI16x8U = 0xFD000066,
    /// <summary><c>f32x4.ceil</c></summary>
    F32x4Ceil = 0xFD000067,
    /// <summary><c>f32x4.floor</c></summary>
    F32x4Floor = 0xFD000068,
    /// <summary><c>f32x4.trunc</c></summary>
    F32x4Trunc = 0xFD000069,
    /// <summary><c>f32x4.nearest</c></summary>
    F32x4Nearest = 0xFD00006A,
    /// <summary><c>i8x16.shl</c></summary>
    I8x16Shl = 0xFD00006B,
    /// <summary><c>i8x16.shr_s</c></summary>
    I8x16ShrS = 0xFD00006C,
    /// <summary><c>i8x16.shr_u</c></summary>
    I8x16ShrU = 0xFD00006D,
    /// <summary><c>i8x16.add</c></summary>
    I8x16Add = 0xFD00006E,
    /// <summary><c>i8x16.add_sat_s</c></summary>
    I8x16AddSatS = 0xFD00006F,
    /// <summary><c>i8x16.add_sat_u</c></summary>
    I8x16AddSatU = 0xFD000070,
    /// <summary><c>i8x16.sub</c></summary>
    I8x16Sub = 0xFD000071,
    /// <summary><c>i8x16.sub_sat_s</c></summary>
    I8x16SubSatS = 0xFD000072,
    /// <summary><c>i8x16.sub_sat_u</c></summary>
    I8x16SubSatU = 0xFD000073,
    /// <summary><c>f64x2.ceil</c></summary>
    F64x2Ceil = 0xFD000074,
    /// <summary><c>f64x2.floor</c></summary>
    F64x2Floor = 0xFD000075,
    /// <summary><c>i8x16.min_s</c></summary>
    I8x16MinS = 0xFD000076,
    /// <summary><c>i8x16.min_u</c></summary>
    I8x16MinU = 0xFD000077,
    /// <summary><c>i8x16.max_s</c></summary>
    I8x16MaxS = 0xFD000078,
    /// <summary><c>i8x16.max_u</c></summary>
    I8x16MaxU = 0xFD000079,
    /// <summary><c>f64x2.trunc</c></summary>
    F64x2Trunc = 0xFD00007A,
    /// <summary><c>i8x16.avgr_u</c></summary>
    I8x16AvgrU = 0xFD00007B,
    /// <summary><c>i16x8.extadd_pairwise_i8x16_s</c></summary>
    I16x8ExtaddPairwiseI8x16S = 0xFD00007C,
    /// <summary><c>i16x8.extadd_pairwise_i8x16_u</c></summary>
    I16x8ExtaddPairwiseI8x16U = 0xFD00007D,
    /// <summary><c>i32x4.extadd_pairwise_i16x8_s</c></summary>
    I32x4ExtaddPairwiseI16x8S = 0xFD00007E,
    /// <summary><c>i32x4.extadd_pairwise_i16x8_u</c></summary>
    I32x4ExtaddPairwiseI16x8U = 0xFD00007F,
    /// <summary><c>i16x8.abs</c></summary>
    I16x8Abs = 0xFD000080,
    /// <summary><c>i16x8.neg</c></summary>
    I16x8Neg = 0xFD000081,
    /// <summary><c>i16x8.q15mulr_sat_s</c></summary>
    I16x8Q15mulrSatS = 0xFD000082,
    /// <summary><c>i16x8.all_true</c></summary>
    I16x8AllTrue = 0xFD000083,
    /// <summary><c>i16x8.bitmask</c></summary>
    I16x8Bitmask = 0xFD000084,
    /// <summary><c>i16x8.narrow_i32x4_s</c></summary>
    I16x8NarrowI32x4S = 0xFD000085,
    /// <summary><c>i16x8.narrow_i32x4_u</c></summary>
    I16x8NarrowI32x4U = 0xFD000086,
    /// <summary><c>i16x8.extend_low_i8x16_s</c></summary>
    I16x8ExtendLowI8x16S = 0xFD000087,
    /// <summary><c>i16x8.extend_high_i8x16_s</c></summary>
    I16x8ExtendHighI8x16S = 0xFD000088,
    /// <summary><c>i16x8.extend_low_i8x16_u</c></summary>
    I16x8ExtendLowI8x16U = 0xFD000089,
    /// <summary><c>i16x8.extend_high_i8x16_u</c></summary>
    I16x8ExtendHighI8x16U = 0xFD00008A,
    /// <summary><c>i16x8.shl</c></summary>
    I16x8Shl = 0xFD00008B,
    /// <summary><c>i16x8.shr_s</c></summary>
    I16x8ShrS = 0xFD00008C,
    /// <summary><c>i16x8.shr_u</c></summary>
    I16x8ShrU = 0xFD00008D,
    /// <summary><c>i16x8.add</c></summary>
    I16x8Add = 0xFD00008E,
    /// <summary><c>i16x8.add_sat_s</c></summary>
    I16x8AddSatS = 0xFD00008F,
    /// <summary><c>i16x8.add_sat_u</c></summary>
    I16x8AddSatU = 0xFD000090,
    /// <summary><c>i16x8.sub</c></summary>
    I16x8Sub = 0xFD000091,
    /// <summary><c>i16x8.sub_sat_s</c></summary>
    I16x8SubSatS = 0xFD000092,
    /// <summary><c>i16x8.sub_sat_u</c></summary>
    I16x8SubSatU = 0xFD000093,
    /// <summary><c>f64x2.nearest</c></summary>
    F64x2Nearest = 0xFD000094,
    /// <summary><c>i16x8.mul</c></summary>
    I16x8Mul = 0xFD000095,
    /// <summary><c>i16x8.min_s</c></summary>
    I16x8MinS = 0xFD000096,
    /// <summary><c>i16x8.min_u</c></summary>
    I16x8MinU = 0xFD000097,
    /// <summary><c>i16x8.max_s</c></summary>
    I16x8MaxS = 0xFD000098,
    /// <summary><c>i16x8.max_u</c></summary>
    I16x8MaxU = 0xFD000099,
    /// <summary><c>i16x8.avgr_u</c></summary>
    I16x8AvgrU = 0xFD00009B,
    /// <summary><c>i16x8.extmul_low_i8x16_s</c></summary>
    I16x8ExtmulLowI8x16S = 0xFD00009C,
    /// <summary><c>i16x8.extmul_high_i8x16_s</c></summary>
    I16x8ExtmulHighI8x16S = 0xFD00009D,
    /// <summary><c>i16x8.extmul_low_i8x16_u</c></summary>
    I16x8ExtmulLowI8x16U = 0xFD00009E,
    /// <summary><c>i16x8.extmul_high_i8x16_u</c></summary>
    I16x8ExtmulHighI8x16U = 0xFD00009F,
    /// <summary><c>i32x4.abs</c></summary>
    I32x4Abs = 0xFD0000A0,
    /// <summary><c>i32x4.neg</c></summary>
    I32x4Neg = 0xFD0000A1,
    /// <summary><c>i32x4.all_true</c></summary>
    I32x4AllTrue = 0xFD0000A3,
    /// <summary><c>i32x4.bitmask</c></summary>
    I32x4Bitmask = 0xFD0000A4,
    /// <summary><c>i32x4.extend_low_i16x8_s</c></summary>
    I32x4ExtendLowI16x8S = 0xFD0000A7,
    /// <summary><c>i32x4.extend_high_i16x8_s</c></summary>
    I32x4ExtendHighI16x8S = 0xFD0000A8,
    /// <summary><c>i32x4.extend_low_i16x8_u</c></summary>
    I32x4ExtendLowI16x8U = 0xFD0000A9,
    /// <summary><c>i32x4.extend_high_i16x8_u</c></summary>
    I32x4ExtendHighI16x8U = 0xFD0000AA,
    /// <summary><c>i32x4.shl</c></summary>
    I32x4Shl = 0xFD0000AB,
    /// <summary><c>i32x4.shr_s</c></summary>
    I32x4ShrS = 0xFD0000AC,
    /// <summary><c>i32x4.shr_u</c></summary>
    I32x4ShrU = 0xFD0000AD,
    /// <summary><c>i32x4.add</c></summary>
    I32x4Add = 0xFD0000AE,
    /// <summary><c>i32x4.sub</c></summary>
    I32x4Sub = 0xFD0000B1,
    /// <summary><c>i32x4.mul</c></summary>
    I32x4Mul = 0xFD0000B5,
    /// <summary><c>i32x4.min_s</c></summary>
    I32x4MinS = 0xFD0000B6,
    /// <summary><c>i32x4.min_u</c></summary>
    I32x4MinU = 0xFD0000B7,
    /// <summary><c>i32x4.max_s</c></summary>
    I32x4MaxS = 0xFD0000B8,
    /// <summary><c>i32x4.max_u</c></summary>
    I32x4MaxU = 0xFD0000B9,
    /// <summary><c>i32x4.dot_i16x8_s</c></summary>
    I32x4DotI16x8S = 0xFD0000BA,
    /// <summary><c>i32x4.extmul_low_i16x8_s</c></summary>
    I32x4ExtmulLowI16x8S = 0xFD0000BC,
    /// <summary><c>i32x4.extmul_high_i16x8_s</c></summary>
    I32x4ExtmulHighI16x8S = 0xFD0000BD,
    /// <summary><c>i32x4.extmul_low_i16x8_u</c></summary>
    I32x4ExtmulLowI16x8U = 0xFD0000BE,
    /// <summary><c>i32x4.extmul_high_i16x8_u</c></summary>
    I32x4ExtmulHighI16x8U = 0xFD0000BF,
    /// <summary><c>i64x2.abs</c></summary>
    I64x2Abs = 0xFD0000C0,
    /// <summary><c>i64x2.neg</c></summary>
    I64x2Neg = 0xFD0000C1,
    /// <summary><c>i64x2.all_true</c></summary>
    I64x2AllTrue = 0xFD0000C3,
    /// <summary><c>i64x2.bitmask</c></summary>
    I64x2Bitmask = 0xFD0000C4,
    /// <summary><c>i64x2.extend_low_i32x4_s</c></summary>
    I64x2ExtendLowI32x4S = 0xFD0000C7,
    /// <summary><c>i64x2.extend_high_i32x4_s</c></summary>
    I64x2ExtendHighI32x4S = 0xFD0000C8,
    /// <summary><c>i64x2.extend_low_i32x4_u</c></summary>
    I64x2ExtendLowI32x4U = 0xFD0000C9,
    /// <summary><c>i64x2.extend_high_i32x4_u</c></summary>
    I64x2ExtendHighI32x4U = 0xFD0000CA,
    /// <summary><c>i64x2.shl</c></summary>
    I64x2Shl = 0xFD0000CB,
    /// <summary><c>i64x2.shr_s</c></summary>
    I64x2ShrS = 0xFD0000CC,
    /// <summary><c>i64x2.shr_u</c></summary>
    I64x2ShrU = 0xFD0000CD,
    /// <summary><c>i64x2.add</c></summary>
    I64x2Add = 0xFD0000CE,
    /// <summary><c>i64x2.sub</c></summary>
    I64x2Sub = 0xFD0000D1,
    /// <summary><c>i64x2.mul</c></summary>
    I64x2Mul = 0xFD0000D5,
    /// <summary><c>i64x2.eq</c></summary>
    I64x2Eq = 0xFD0000D6,
    /// <summary><c>i64x2.ne</c></summary>
    I64x2Ne = 0xFD0000D7,
    /// <summary><c>i64x2.lt_s</c></summary>
    I64x2LtS = 0xFD0000D8,
    /// <summary><c>i64x2.gt_s</c></summary>
    I64x2GtS = 0xFD0000D9,
    /// <summary><c>i64x2.le_s</c></summary>
    I64x2LeS = 0xFD0000DA,
    /// <summary><c>i64x2.ge_s</c></summary>
    I64x2GeS = 0xFD0000DB,
    /// <summary><c>i64x2.extmul_low_i32x4_s</c></summary>
    I64x2ExtmulLowI32x4S = 0xFD0000DC,
    /// <summary><c>i64x2.extmul_high_i32x4_s</c></summary>
    I64x2ExtmulHighI32x4S = 0xFD0000DD,
    /// <summary><c>i64x2.extmul_low_i32x4_u</c></summary>
    I64x2ExtmulLowI32x4U = 0xFD0000DE,
    /// <summary><c>i64x2.extmul_high_i32x4_u</c></summary>
    I64x2ExtmulHighI32x4U = 0xFD0000DF,
    /// <summary><c>f32x4.abs</c></summary>
    F32x4Abs = 0xFD0000E0,
    /// <summary><c>f32x4.neg</c></summary>
    F32x4Neg = 0xFD0000E1,
    /// <summary><c>f32x4.sqrt</c></summary>
    F32x4Sqrt = 0xFD0000E3,
    /// <summary><c>f32x4.add</c></summary>
    F32x4Add = 0xFD0000E4,
    /// <summary><c>f32x4.sub</c></summary>
    F32x4Sub = 0xFD0000E5,
    /// <summary><c>f32x4.mul</c></summary>
    F32x4Mul = 0xFD0000E6,
    /// <summary><c>f32x4.div</c></summary>
    F32x4Div = 0xFD0000E7,
    /// <summary><c>f32x4.min</c></summary>
    F32x4Min = 0xFD0000E8,
    /// <summary><c>f32x4.max</c></summary>
    F32x4Max = 0xFD0000E9,
    /// <summary><c>f32x4.pmin</c></summary>
    F32x4Pmin = 0xFD0000EA,
    /// <summary><c>f32x4.pmax</c></summary>
    F32x4Pmax = 0xFD0000EB,
    /// <summary><c>f64x2.abs</c></summary>
    F64x2Abs = 0xFD0000EC,
    /// <summary><c>f64x2.neg</c></summary>
    F64x2Neg = 0xFD0000ED,
    /// <summary><c>f64x2.sqrt</c></summary>
    F64x2Sqrt = 0xFD0000EF,
    /// <summary><c>f64x2.add</c></summary>
    F64x2Add = 0xFD0000F0,
    /// <summary><c>f64x2.sub</c></summary>
    F64x2Sub = 0xFD0000F1,
    /// <summary><c>f64x2.mul</c></summary>
    F64x2Mul = 0xFD0000F2,
    /// <summary><c>f64x2.div</c></summary>
    F64x2Div = 0xFD0000F3,
    /// <summary><c>f64x2.min</c></summary>
    F64x2Min = 0xFD0000F4,
    /// <summary><c>f64x2.max</c></summary>
    F64x2Max = 0xFD0000F5,
    /// <summary><c>f64x2.pmin</c></summary>
    F64x2Pmin = 0xFD0000F6,
    /// <summary><c>f64x2.pmax</c></summary>
    F64x2Pmax = 0xFD0000F7,
    /// <summary><c>i32x4.trunc_sat_f32x4_s</c></summary>
    I32x4TruncSatF32x4S = 0xFD0000F8,
    /// <summary><c>i32x4.trunc_sat_f32x4_u</c></summary>
    I32x4TruncSatF32x4U = 0xFD0000F9,
    /// <summary><c>f32x4.convert_i32x4_s</c></summary>
    F32x4ConvertI32x4S = 0xFD0000FA,
    /// <summary><c>f32x4.convert_i32x4_u</c></summary>
    F32x4ConvertI32x4U = 0xFD0000FB,
    /// <summary><c>i32x4.trunc_sat_f64x2_s_zero</c></summary>
    I32x4TruncSatF64x2SZero = 0xFD0000FC,
    /// <summary><c>i32x4.trunc_sat_f64x2_u_zero</c></summary>
    I32x4TruncSatF64x2UZero = 0xFD0000FD,
    /// <summary><c>f64x2.convert_low_i32x4_s</c></summary>
    F64x2ConvertLowI32x4S = 0xFD0000FE,
    /// <summary><c>f64x2.convert_low_i32x4_u</c></summary>
    F64x2ConvertLowI32x4U = 0xFD0000FF,
    /// <summary><c>i8x16.relaxed_swizzle</c></summary>
    I8x16RelaxedSwizzle = 0xFD000100,
    /// <summary><c>i32x4.relaxed_trunc_f32x4_s</c></summary>
    I32x4RelaxedTruncF32x4S = 0xFD000101,
    /// <summary><c>i32x4.relaxed_trunc_f32x4_u</c></summary>
    I32x4RelaxedTruncF32x4U = 0xFD000102,
    /// <summary><c>i32x4.relaxed_trunc_f64x2_s_zero</c></summary>
    I32x4RelaxedTruncF64x2SZero = 0xFD000103,
    /// <summary><c>i32x4.relaxed_trunc_f64x2_u_zero</c></summary>
    I32x4RelaxedTruncF64x2UZero = 0xFD000104,
    /// <summary><c>f32x4.relaxed_madd</c></summary>
    F32x4RelaxedMadd = 0xFD000105,
    /// <summary><c>f32x4.relaxed_nmadd</c></summary>
    F32x4RelaxedNmadd = 0xFD000106,
    /// <summary><c>f64x2.relaxed_madd</c></summary>
    F64x2RelaxedMadd = 0xFD000107,
    /// <summary><c>f64x2.relaxed_nmadd</c></summary>
    F64x2RelaxedNmadd = 0xFD000108,
    /// <summary><c>i8x16.relaxed_laneselect</c></summary>
    I8x16RelaxedLaneselect = 0xFD000109,
    /// <summary><c>i16x8.relaxed_laneselect</c></summary>
    I16x8RelaxedLaneselect = 0xFD00010A,
    /// <summary><c>i32x4.relaxed_laneselect</c></summary>
    I32x4RelaxedLaneselect = 0xFD00010B,
    /// <summary><c>i64x2.relaxed_laneselect</c></summary>
    I64x2RelaxedLaneselect = 0xFD00010C,
    /// <summary><c>f32x4.relaxed_min</c></summary>
    F32x4RelaxedMin = 0xFD00010D,
    /// <summary><c>f32x4.relaxed_max</c></summary>
    F32x4RelaxedMax = 0xFD00010E,
    /// <summary><c>f64x2.relaxed_min</c></summary>
    F64x2RelaxedMin = 0xFD00010F,
    /// <summary><c>f64x2.relaxed_max</c></summary>
    F64x2RelaxedMax = 0xFD000110,
    /// <summary><c>i16x8.relaxed_q15mulr_s</c></summary>
    I16x8RelaxedQ15mulrS = 0xFD000111,
    /// <summary><c>i16x8.relaxed_dot_i8x16_i7x16_s</c></summary>
    I16x8RelaxedDotI8x16I7x16S = 0xFD000112,
    /// <summary><c>i32x4.relaxed_dot_i8x16_i7x16_add_s</c></summary>
    I32x4RelaxedDotI8x16I7x16AddS = 0xFD000113,
}

/// <summary>Opcode properties.</summary>
public static class WasmOps
{
    /// <summary>The immediates <paramref name="op"/> takes.</summary>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not a defined opcode.</exception>
    public static WasmImmediateShape GetImmediateShape(WasmOp op) => op switch
    {
        WasmOp.Unreachable or WasmOp.Nop or WasmOp.Else or WasmOp.ThrowRef or WasmOp.End or WasmOp.Return or
            WasmOp.CatchAll or WasmOp.Drop or WasmOp.Select or WasmOp.I32Eqz or WasmOp.I32Eq or WasmOp.I32Ne or
            WasmOp.I32LtS or WasmOp.I32LtU or WasmOp.I32GtS or WasmOp.I32GtU or WasmOp.I32LeS or WasmOp.I32LeU or
            WasmOp.I32GeS or WasmOp.I32GeU or WasmOp.I64Eqz or WasmOp.I64Eq or WasmOp.I64Ne or WasmOp.I64LtS or
            WasmOp.I64LtU or WasmOp.I64GtS or WasmOp.I64GtU or WasmOp.I64LeS or WasmOp.I64LeU or WasmOp.I64GeS or
            WasmOp.I64GeU or WasmOp.F32Eq or WasmOp.F32Ne or WasmOp.F32Lt or WasmOp.F32Gt or WasmOp.F32Le or
            WasmOp.F32Ge or WasmOp.F64Eq or WasmOp.F64Ne or WasmOp.F64Lt or WasmOp.F64Gt or WasmOp.F64Le or
            WasmOp.F64Ge or WasmOp.I32Clz or WasmOp.I32Ctz or WasmOp.I32Popcnt or WasmOp.I32Add or WasmOp.I32Sub or
            WasmOp.I32Mul or WasmOp.I32DivS or WasmOp.I32DivU or WasmOp.I32RemS or WasmOp.I32RemU or WasmOp.I32And or
            WasmOp.I32Or or WasmOp.I32Xor or WasmOp.I32Shl or WasmOp.I32ShrS or WasmOp.I32ShrU or WasmOp.I32Rotl or
            WasmOp.I32Rotr or WasmOp.I64Clz or WasmOp.I64Ctz or WasmOp.I64Popcnt or WasmOp.I64Add or WasmOp.I64Sub or
            WasmOp.I64Mul or WasmOp.I64DivS or WasmOp.I64DivU or WasmOp.I64RemS or WasmOp.I64RemU or WasmOp.I64And or
            WasmOp.I64Or or WasmOp.I64Xor or WasmOp.I64Shl or WasmOp.I64ShrS or WasmOp.I64ShrU or WasmOp.I64Rotl or
            WasmOp.I64Rotr or WasmOp.F32Abs or WasmOp.F32Neg or WasmOp.F32Ceil or WasmOp.F32Floor or WasmOp.F32Trunc or
            WasmOp.F32Nearest or WasmOp.F32Sqrt or WasmOp.F32Add or WasmOp.F32Sub or WasmOp.F32Mul or WasmOp.F32Div or
            WasmOp.F32Min or WasmOp.F32Max or WasmOp.F32Copysign or WasmOp.F64Abs or WasmOp.F64Neg or WasmOp.F64Ceil or
            WasmOp.F64Floor or WasmOp.F64Trunc or WasmOp.F64Nearest or WasmOp.F64Sqrt or WasmOp.F64Add or
            WasmOp.F64Sub or WasmOp.F64Mul or WasmOp.F64Div or WasmOp.F64Min or WasmOp.F64Max or WasmOp.F64Copysign or
            WasmOp.I32WrapI64 or WasmOp.I32TruncF32S or WasmOp.I32TruncF32U or WasmOp.I32TruncF64S or
            WasmOp.I32TruncF64U or WasmOp.I64ExtendI32S or WasmOp.I64ExtendI32U or WasmOp.I64TruncF32S or
            WasmOp.I64TruncF32U or WasmOp.I64TruncF64S or WasmOp.I64TruncF64U or WasmOp.F32ConvertI32S or
            WasmOp.F32ConvertI32U or WasmOp.F32ConvertI64S or WasmOp.F32ConvertI64U or WasmOp.F32DemoteF64 or
            WasmOp.F64ConvertI32S or WasmOp.F64ConvertI32U or WasmOp.F64ConvertI64S or WasmOp.F64ConvertI64U or
            WasmOp.F64PromoteF32 or WasmOp.I32ReinterpretF32 or WasmOp.I64ReinterpretF64 or WasmOp.F32ReinterpretI32 or
            WasmOp.F64ReinterpretI64 or WasmOp.I32Extend8S or WasmOp.I32Extend16S or WasmOp.I64Extend8S or
            WasmOp.I64Extend16S or WasmOp.I64Extend32S or WasmOp.RefIsNull or WasmOp.RefEq or WasmOp.RefAsNonNull or
            WasmOp.I32TruncSatF32S or WasmOp.I32TruncSatF32U or WasmOp.I32TruncSatF64S or WasmOp.I32TruncSatF64U or
            WasmOp.I64TruncSatF32S or WasmOp.I64TruncSatF32U or WasmOp.I64TruncSatF64S or WasmOp.I64TruncSatF64U or
            WasmOp.I64Add128 or WasmOp.I64Sub128 or WasmOp.I64MulWideS or WasmOp.I64MulWideU or WasmOp.I8x16Swizzle or
            WasmOp.I8x16Splat or WasmOp.I16x8Splat or WasmOp.I32x4Splat or WasmOp.I64x2Splat or WasmOp.F32x4Splat or
            WasmOp.F64x2Splat or WasmOp.I8x16Eq or WasmOp.I8x16Ne or WasmOp.I8x16LtS or WasmOp.I8x16LtU or
            WasmOp.I8x16GtS or WasmOp.I8x16GtU or WasmOp.I8x16LeS or WasmOp.I8x16LeU or WasmOp.I8x16GeS or
            WasmOp.I8x16GeU or WasmOp.I16x8Eq or WasmOp.I16x8Ne or WasmOp.I16x8LtS or WasmOp.I16x8LtU or
            WasmOp.I16x8GtS or WasmOp.I16x8GtU or WasmOp.I16x8LeS or WasmOp.I16x8LeU or WasmOp.I16x8GeS or
            WasmOp.I16x8GeU or WasmOp.I32x4Eq or WasmOp.I32x4Ne or WasmOp.I32x4LtS or WasmOp.I32x4LtU or
            WasmOp.I32x4GtS or WasmOp.I32x4GtU or WasmOp.I32x4LeS or WasmOp.I32x4LeU or WasmOp.I32x4GeS or
            WasmOp.I32x4GeU or WasmOp.F32x4Eq or WasmOp.F32x4Ne or WasmOp.F32x4Lt or WasmOp.F32x4Gt or WasmOp.F32x4Le or
            WasmOp.F32x4Ge or WasmOp.F64x2Eq or WasmOp.F64x2Ne or WasmOp.F64x2Lt or WasmOp.F64x2Gt or WasmOp.F64x2Le or
            WasmOp.F64x2Ge or WasmOp.V128Not or WasmOp.V128And or WasmOp.V128Andnot or WasmOp.V128Or or
            WasmOp.V128Xor or WasmOp.V128Bitselect or WasmOp.V128AnyTrue or WasmOp.F32x4DemoteF64x2Zero or
            WasmOp.F64x2PromoteLowF32x4 or WasmOp.I8x16Abs or WasmOp.I8x16Neg or WasmOp.I8x16Popcnt or
            WasmOp.I8x16AllTrue or WasmOp.I8x16Bitmask or WasmOp.I8x16NarrowI16x8S or WasmOp.I8x16NarrowI16x8U or
            WasmOp.F32x4Ceil or WasmOp.F32x4Floor or WasmOp.F32x4Trunc or WasmOp.F32x4Nearest or WasmOp.I8x16Shl or
            WasmOp.I8x16ShrS or WasmOp.I8x16ShrU or WasmOp.I8x16Add or WasmOp.I8x16AddSatS or WasmOp.I8x16AddSatU or
            WasmOp.I8x16Sub or WasmOp.I8x16SubSatS or WasmOp.I8x16SubSatU or WasmOp.F64x2Ceil or WasmOp.F64x2Floor or
            WasmOp.I8x16MinS or WasmOp.I8x16MinU or WasmOp.I8x16MaxS or WasmOp.I8x16MaxU or WasmOp.F64x2Trunc or
            WasmOp.I8x16AvgrU or WasmOp.I16x8ExtaddPairwiseI8x16S or WasmOp.I16x8ExtaddPairwiseI8x16U or
            WasmOp.I32x4ExtaddPairwiseI16x8S or WasmOp.I32x4ExtaddPairwiseI16x8U or WasmOp.I16x8Abs or
            WasmOp.I16x8Neg or WasmOp.I16x8Q15mulrSatS or WasmOp.I16x8AllTrue or WasmOp.I16x8Bitmask or
            WasmOp.I16x8NarrowI32x4S or WasmOp.I16x8NarrowI32x4U or WasmOp.I16x8ExtendLowI8x16S or
            WasmOp.I16x8ExtendHighI8x16S or WasmOp.I16x8ExtendLowI8x16U or WasmOp.I16x8ExtendHighI8x16U or
            WasmOp.I16x8Shl or WasmOp.I16x8ShrS or WasmOp.I16x8ShrU or WasmOp.I16x8Add or WasmOp.I16x8AddSatS or
            WasmOp.I16x8AddSatU or WasmOp.I16x8Sub or WasmOp.I16x8SubSatS or WasmOp.I16x8SubSatU or
            WasmOp.F64x2Nearest or WasmOp.I16x8Mul or WasmOp.I16x8MinS or WasmOp.I16x8MinU or WasmOp.I16x8MaxS or
            WasmOp.I16x8MaxU or WasmOp.I16x8AvgrU or WasmOp.I16x8ExtmulLowI8x16S or WasmOp.I16x8ExtmulHighI8x16S or
            WasmOp.I16x8ExtmulLowI8x16U or WasmOp.I16x8ExtmulHighI8x16U or WasmOp.I32x4Abs or WasmOp.I32x4Neg or
            WasmOp.I32x4AllTrue or WasmOp.I32x4Bitmask or WasmOp.I32x4ExtendLowI16x8S or WasmOp.I32x4ExtendHighI16x8S or
            WasmOp.I32x4ExtendLowI16x8U or WasmOp.I32x4ExtendHighI16x8U or WasmOp.I32x4Shl or WasmOp.I32x4ShrS or
            WasmOp.I32x4ShrU or WasmOp.I32x4Add or WasmOp.I32x4Sub or WasmOp.I32x4Mul or WasmOp.I32x4MinS or
            WasmOp.I32x4MinU or WasmOp.I32x4MaxS or WasmOp.I32x4MaxU or WasmOp.I32x4DotI16x8S or
            WasmOp.I32x4ExtmulLowI16x8S or WasmOp.I32x4ExtmulHighI16x8S or WasmOp.I32x4ExtmulLowI16x8U or
            WasmOp.I32x4ExtmulHighI16x8U or WasmOp.I64x2Abs or WasmOp.I64x2Neg or WasmOp.I64x2AllTrue or
            WasmOp.I64x2Bitmask or WasmOp.I64x2ExtendLowI32x4S or WasmOp.I64x2ExtendHighI32x4S or
            WasmOp.I64x2ExtendLowI32x4U or WasmOp.I64x2ExtendHighI32x4U or WasmOp.I64x2Shl or WasmOp.I64x2ShrS or
            WasmOp.I64x2ShrU or WasmOp.I64x2Add or WasmOp.I64x2Sub or WasmOp.I64x2Mul or WasmOp.I64x2Eq or
            WasmOp.I64x2Ne or WasmOp.I64x2LtS or WasmOp.I64x2GtS or WasmOp.I64x2LeS or WasmOp.I64x2GeS or
            WasmOp.I64x2ExtmulLowI32x4S or WasmOp.I64x2ExtmulHighI32x4S or WasmOp.I64x2ExtmulLowI32x4U or
            WasmOp.I64x2ExtmulHighI32x4U or WasmOp.F32x4Abs or WasmOp.F32x4Neg or WasmOp.F32x4Sqrt or WasmOp.F32x4Add or
            WasmOp.F32x4Sub or WasmOp.F32x4Mul or WasmOp.F32x4Div or WasmOp.F32x4Min or WasmOp.F32x4Max or
            WasmOp.F32x4Pmin or WasmOp.F32x4Pmax or WasmOp.F64x2Abs or WasmOp.F64x2Neg or WasmOp.F64x2Sqrt or
            WasmOp.F64x2Add or WasmOp.F64x2Sub or WasmOp.F64x2Mul or WasmOp.F64x2Div or WasmOp.F64x2Min or
            WasmOp.F64x2Max or WasmOp.F64x2Pmin or WasmOp.F64x2Pmax or WasmOp.I32x4TruncSatF32x4S or
            WasmOp.I32x4TruncSatF32x4U or WasmOp.F32x4ConvertI32x4S or WasmOp.F32x4ConvertI32x4U or
            WasmOp.I32x4TruncSatF64x2SZero or WasmOp.I32x4TruncSatF64x2UZero or WasmOp.F64x2ConvertLowI32x4S or
            WasmOp.F64x2ConvertLowI32x4U or WasmOp.I8x16RelaxedSwizzle or WasmOp.I32x4RelaxedTruncF32x4S or
            WasmOp.I32x4RelaxedTruncF32x4U or WasmOp.I32x4RelaxedTruncF64x2SZero or
            WasmOp.I32x4RelaxedTruncF64x2UZero or WasmOp.F32x4RelaxedMadd or WasmOp.F32x4RelaxedNmadd or
            WasmOp.F64x2RelaxedMadd or WasmOp.F64x2RelaxedNmadd or WasmOp.I8x16RelaxedLaneselect or
            WasmOp.I16x8RelaxedLaneselect or WasmOp.I32x4RelaxedLaneselect or WasmOp.I64x2RelaxedLaneselect or
            WasmOp.F32x4RelaxedMin or WasmOp.F32x4RelaxedMax or WasmOp.F64x2RelaxedMin or WasmOp.F64x2RelaxedMax or
            WasmOp.I16x8RelaxedQ15mulrS or WasmOp.I16x8RelaxedDotI8x16I7x16S or WasmOp.I32x4RelaxedDotI8x16I7x16AddS => WasmImmediateShape.None,
        WasmOp.Catch or WasmOp.Throw or WasmOp.Rethrow or WasmOp.Br or WasmOp.BrIf or WasmOp.Call or
            WasmOp.ReturnCall or WasmOp.CallRef or WasmOp.ReturnCallRef or WasmOp.Delegate or WasmOp.LocalGet or
            WasmOp.LocalSet or WasmOp.LocalTee or WasmOp.GlobalGet or WasmOp.GlobalSet or WasmOp.TableGet or
            WasmOp.TableSet or WasmOp.MemorySize or WasmOp.MemoryGrow or WasmOp.RefFunc or WasmOp.BrOnNull or
            WasmOp.BrOnNonNull or WasmOp.DataDrop or WasmOp.MemoryFill or WasmOp.ElemDrop or WasmOp.TableGrow or
            WasmOp.TableSize or WasmOp.TableFill => WasmImmediateShape.Index,
        WasmOp.CallIndirect or WasmOp.ReturnCallIndirect or WasmOp.MemoryInit or WasmOp.MemoryCopy or
            WasmOp.TableInit or WasmOp.TableCopy => WasmImmediateShape.IndexPair,
        WasmOp.I32Const => WasmImmediateShape.I32,
        WasmOp.I64Const => WasmImmediateShape.I64,
        WasmOp.F32Const => WasmImmediateShape.F32,
        WasmOp.F64Const => WasmImmediateShape.F64,
        WasmOp.I32Load or WasmOp.I64Load or WasmOp.F32Load or WasmOp.F64Load or WasmOp.I32Load8S or WasmOp.I32Load8U or
            WasmOp.I32Load16S or WasmOp.I32Load16U or WasmOp.I64Load8S or WasmOp.I64Load8U or WasmOp.I64Load16S or
            WasmOp.I64Load16U or WasmOp.I64Load32S or WasmOp.I64Load32U or WasmOp.I32Store or WasmOp.I64Store or
            WasmOp.F32Store or WasmOp.F64Store or WasmOp.I32Store8 or WasmOp.I32Store16 or WasmOp.I64Store8 or
            WasmOp.I64Store16 or WasmOp.I64Store32 or WasmOp.V128Load or WasmOp.V128Load8x8S or WasmOp.V128Load8x8U or
            WasmOp.V128Load16x4S or WasmOp.V128Load16x4U or WasmOp.V128Load32x2S or WasmOp.V128Load32x2U or
            WasmOp.V128Load8Splat or WasmOp.V128Load16Splat or WasmOp.V128Load32Splat or WasmOp.V128Load64Splat or
            WasmOp.V128Store or WasmOp.V128Load32Zero or WasmOp.V128Load64Zero => WasmImmediateShape.MemArg,
        WasmOp.V128Load8Lane or WasmOp.V128Load16Lane or WasmOp.V128Load32Lane or WasmOp.V128Load64Lane or
            WasmOp.V128Store8Lane or WasmOp.V128Store16Lane or WasmOp.V128Store32Lane or WasmOp.V128Store64Lane => WasmImmediateShape.MemArgLane,
        WasmOp.I8x16ExtractLaneS or WasmOp.I8x16ExtractLaneU or WasmOp.I8x16ReplaceLane or WasmOp.I16x8ExtractLaneS or
            WasmOp.I16x8ExtractLaneU or WasmOp.I16x8ReplaceLane or WasmOp.I32x4ExtractLane or WasmOp.I32x4ReplaceLane or
            WasmOp.I64x2ExtractLane or WasmOp.I64x2ReplaceLane or WasmOp.F32x4ExtractLane or WasmOp.F32x4ReplaceLane or
            WasmOp.F64x2ExtractLane or WasmOp.F64x2ReplaceLane => WasmImmediateShape.Lane,
        WasmOp.V128Const => WasmImmediateShape.V128,
        WasmOp.I8x16Shuffle => WasmImmediateShape.Shuffle,
        WasmOp.Block or WasmOp.Loop or WasmOp.If or WasmOp.Try => WasmImmediateShape.Block,
        WasmOp.BrTable => WasmImmediateShape.BrTable,
        WasmOp.TryTable => WasmImmediateShape.TryTable,
        WasmOp.RefNull => WasmImmediateShape.HeapType,
        WasmOp.SelectTyped => WasmImmediateShape.SelectTyped,
        _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not a defined opcode."),
    };
}
