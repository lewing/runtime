// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>Disassembles a parsed <see cref="WasmModule"/> into WAT text format.</summary>
public static class WasmDisassembler
{
    /// <summary>Disassemble a parsed WasmModule to WAT text.</summary>
    public static string Disassemble(WasmModule module)
    {
        var writer = WatWriter.CreateStringWriter(out var sw);
        Disassemble(module, writer);
        writer.Flush();
        return sw.ToString();
    }

    /// <summary>Disassemble a parsed WasmModule, streaming output to a <see cref="WatWriter"/>.</summary>
    public static void Disassemble(WasmModule module, WatWriter writer)
    {
        var ctx = new DisassemblyContext(module);
        writer.WriteLine("(module");

        EmitTypes(writer, module);
        EmitImports(writer, module, ctx);
        EmitFunctions(writer, module, ctx);
        EmitTables(writer, module, ctx);
        EmitMemories(writer, module, ctx);
        EmitGlobals(writer, module, ctx);
        EmitExports(writer, module, ctx);
        EmitStart(writer, module, ctx);
        EmitElements(writer, module, ctx);
        EmitData(writer, module, ctx);

        writer.Write(')');
    }

    /// <summary>Disassemble a parsed WasmModule, streaming output to any <see cref="TextWriter"/>.</summary>
    public static void Disassemble(WasmModule module, TextWriter writer)
    {
        using var ww = new WatWriter(writer);
        Disassemble(module, ww);
    }

    /// <summary>Disassemble raw wasm bytes to WAT text.</summary>
    public static string Disassemble(byte[] bytes)
    {
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        return Disassemble(module);
    }

    /// <summary>Disassemble raw wasm bytes, streaming output to any <see cref="TextWriter"/>.</summary>
    public static void Disassemble(byte[] bytes, TextWriter writer)
    {
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        Disassemble(module, writer);
    }

    /// <summary>Disassemble a single function from a parsed WasmModule to WAT text.</summary>
    /// <param name="module">The parsed module.</param>
    /// <param name="exportFunctionIndex">The absolute function index (including imports).</param>
    public static string DisassembleFunction(WasmModule module, int exportFunctionIndex)
    {
        var ctx = new DisassemblyContext(module);
        var writer = WatWriter.CreateStringWriter(out var sw);

        int localIndex = exportFunctionIndex - ctx.ImportedFuncCount;
        if (localIndex < 0 || localIndex >= module.Functions.Count)
            throw new ArgumentOutOfRangeException(nameof(exportFunctionIndex),
                $"Function index {exportFunctionIndex} is out of range");

        var func = module.Functions[localIndex];
        var funcName = ctx.FuncNames.TryGetValue(exportFunctionIndex, out var n) ? $" ${n}" : "";
        var ft = module.Types[func.TypeIndex];

        writer.Write($"(func{funcName}");

        if (ft.Parameters.Length > 0)
        {
            writer.Write(" (param");
            foreach (var p in ft.Parameters)
                writer.Write($" {ValTypeName(p)}");
            writer.Write(')');
        }
        if (ft.Results.Length > 0)
        {
            writer.Write(" (result");
            foreach (var r in ft.Results)
                writer.Write($" {ValTypeName(r)}");
            writer.Write(')');
        }
        writer.WriteLine();

        if (func.Locals.Count > 0)
        {
            int li = 0;
            while (li < func.Locals.Count)
            {
                var lt = func.Locals[li];
                int count = 1;
                while (li + count < func.Locals.Count && func.Locals[li + count] == lt)
                    count++;
                writer.Write("  (local");
                for (int c = 0; c < count; c++)
                    writer.Write($" {ValTypeName(lt)}");
                writer.WriteLine(")");
                li += count;
            }
        }

        DisassembleBody(writer, func.Body, ctx);

        writer.Write(')');
        writer.Flush();
        return sw.ToString();
    }

    // ── Context ──────────────────────────────────────────────────────────

    private sealed class DisassemblyContext
    {
        public readonly WasmModule Module;
        public readonly int ImportedFuncCount;
        public readonly int ImportedTableCount;
        public readonly int ImportedMemCount;
        public readonly int ImportedGlobalCount;
        public readonly Dictionary<int, string> FuncNames = new();
        public readonly Dictionary<int, Dictionary<int, string>> LocalNames = new();

        // Reverse opcode table: byte(s) → (mnemonic, ImmediateKind)
        public readonly Dictionary<byte, (string Mnemonic, ImmediateKind Imm)> SingleByteOpcodes = new();
        public readonly Dictionary<uint, (string Mnemonic, ImmediateKind Imm)> PrefixedOpcodes = new();

        public DisassemblyContext(WasmModule module)
        {
            Module = module;
            foreach (var imp in module.Imports)
            {
                switch (imp.Descriptor)
                {
                    case ImportDescriptor.Func: ImportedFuncCount++; break;
                    case ImportDescriptor.Table: ImportedTableCount++; break;
                    case ImportDescriptor.Memory: ImportedMemCount++; break;
                    case ImportDescriptor.Global: ImportedGlobalCount++; break;
                }
            }

            BuildReverseOpcodeTable();
            ParseNameSection(module);
        }

        private void BuildReverseOpcodeTable()
        {
            // Use reflection-free approach: iterate the OpcodeTable by trying all known mnemonics.
            // Instead, we build by iterating the same BuildTable logic via TryGet on known mnemonics.
            // More practical: we iterate all entries by accessing the internal table.
            // Since OpcodeTable only exposes TryGet, we rebuild from known mnemonic list.
            // Actually, we'll just call into the existing table for every mnemonic we know about.
            // The simplest approach: iterate the dictionary via known mnemonic strings from the opcode table.
            // We use a different approach: build from the OpcodeTable entries.
            var allMnemonics = GetAllMnemonics();
            foreach (var mnemonic in allMnemonics)
            {
                if (!OpcodeTable.TryGet(mnemonic, out var entry)) continue;
                var opcodes = entry.Opcodes;
                if (opcodes.Length == 1)
                {
                    SingleByteOpcodes.TryAdd(opcodes[0], (mnemonic, entry.Immediate));
                }
                else if (opcodes.Length >= 2)
                {
                    // Prefixed: decode the sub-opcode as u32 LEB128 from opcodes[1..]
                    uint sub = DecodeLeb128(opcodes.AsSpan(1));
                    uint key = ((uint)opcodes[0] << 24) | sub;
                    PrefixedOpcodes.TryAdd(key, (mnemonic, entry.Immediate));
                }
            }
        }

        private static uint DecodeLeb128(ReadOnlySpan<byte> bytes)
        {
            uint result = 0;
            int shift = 0;
            foreach (var b in bytes)
            {
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return result;
                shift += 7;
            }
            return result;
        }

        private void ParseNameSection(WasmModule module)
        {
            foreach (var cs in module.CustomSections)
            {
                if (cs.Name != "name") continue;
                try { ParseNameSectionContent(cs.Content); } catch { /* ignore malformed */ }
                break;
            }
        }

        private void ParseNameSectionContent(byte[] content)
        {
            using var stream = new MemoryStream(content);
            var reader = new WasmBinaryReader(stream);
            while (reader.HasMore)
            {
                byte subsectionId = reader.ReadByte();
                uint subsectionSize = reader.ReadU32Leb128();
                long start = reader.Position;

                switch (subsectionId)
                {
                    case 1: // function names
                        foreach (var _ in reader.ReadVectorHeader())
                        {
                            uint idx = reader.ReadU32Leb128();
                            string name = reader.ReadName();
                            FuncNames[(int)idx] = name;
                        }
                        break;
                    case 2: // local names
                        foreach (var _ in reader.ReadVectorHeader())
                        {
                            uint funcIdx = reader.ReadU32Leb128();
                            var locals = new Dictionary<int, string>();
                            foreach (var __ in reader.ReadVectorHeader())
                            {
                                uint localIdx = reader.ReadU32Leb128();
                                string name = reader.ReadName();
                                locals[(int)localIdx] = name;
                            }
                            LocalNames[(int)funcIdx] = locals;
                        }
                        break;
                    default:
                        // Skip unknown subsection
                        reader.ReadBytes((int)(subsectionSize - (reader.Position - start)));
                        break;
                }

                // Ensure we consumed exactly subsectionSize bytes
                long expected = start + subsectionSize;
                if (reader.Position < expected)
                    reader.ReadBytes((int)(expected - reader.Position));
            }
        }

        public string FuncName(int funcIdx)
        {
            if (FuncNames.TryGetValue(funcIdx, out var name))
                return $"${name}";
            return funcIdx.ToString();
        }

        public string FuncRef(int funcIdx)
        {
            if (FuncNames.TryGetValue(funcIdx, out var name))
                return $"$${name}";
            return funcIdx.ToString();
        }
    }

    // ── All known mnemonics (used to build reverse table) ────────────────

    private static string[] GetAllMnemonics() =>
    [
        // Control
        "unreachable", "nop", "block", "loop", "if", "else",
        "try", "catch", "throw", "rethrow", "throw_ref",
        "end", "br", "br_if", "br_table", "return", "call",
        "delegate", "catch_all", "try_table", "cont.new", "cont.bind", "resume", "suspend", "switch",
        "call_indirect", "return_call", "return_call_indirect",
        // Parametric
        "drop", "select",
        // Local/Global
        "local.get", "local.set", "local.tee", "global.get", "global.set",
        // Table
        "table.get", "table.set",
        // Memory load
        "i32.load", "i64.load", "f32.load", "f64.load",
        "i32.load8_s", "i32.load8_u", "i32.load16_s", "i32.load16_u",
        "i64.load8_s", "i64.load8_u", "i64.load16_s", "i64.load16_u",
        "i64.load32_s", "i64.load32_u",
        // Memory store
        "i32.store", "i64.store", "f32.store", "f64.store",
        "i32.store8", "i32.store16", "i64.store8", "i64.store16", "i64.store32",
        // Memory size/grow
        "memory.size", "memory.grow",
        // Constants
        "i32.const", "i64.const", "f32.const", "f64.const",
        // i32 comparison
        "i32.eqz", "i32.eq", "i32.ne", "i32.lt_s", "i32.lt_u",
        "i32.gt_s", "i32.gt_u", "i32.le_s", "i32.le_u", "i32.ge_s", "i32.ge_u",
        // i64 comparison
        "i64.eqz", "i64.eq", "i64.ne", "i64.lt_s", "i64.lt_u",
        "i64.gt_s", "i64.gt_u", "i64.le_s", "i64.le_u", "i64.ge_s", "i64.ge_u",
        // f32 comparison
        "f32.eq", "f32.ne", "f32.lt", "f32.gt", "f32.le", "f32.ge",
        // f64 comparison
        "f64.eq", "f64.ne", "f64.lt", "f64.gt", "f64.le", "f64.ge",
        // i32 arithmetic
        "i32.clz", "i32.ctz", "i32.popcnt",
        "i32.add", "i32.sub", "i32.mul", "i32.div_s", "i32.div_u",
        "i32.rem_s", "i32.rem_u", "i32.and", "i32.or", "i32.xor",
        "i32.shl", "i32.shr_s", "i32.shr_u", "i32.rotl", "i32.rotr",
        // i64 arithmetic
        "i64.clz", "i64.ctz", "i64.popcnt",
        "i64.add", "i64.sub", "i64.mul", "i64.div_s", "i64.div_u",
        "i64.rem_s", "i64.rem_u", "i64.and", "i64.or", "i64.xor",
        "i64.shl", "i64.shr_s", "i64.shr_u", "i64.rotl", "i64.rotr",
        // f32 arithmetic
        "f32.abs", "f32.neg", "f32.ceil", "f32.floor", "f32.trunc", "f32.nearest", "f32.sqrt",
        "f32.add", "f32.sub", "f32.mul", "f32.div", "f32.min", "f32.max", "f32.copysign",
        // f64 arithmetic
        "f64.abs", "f64.neg", "f64.ceil", "f64.floor", "f64.trunc", "f64.nearest", "f64.sqrt",
        "f64.add", "f64.sub", "f64.mul", "f64.div", "f64.min", "f64.max", "f64.copysign",
        // Conversions
        "i32.wrap_i64",
        "i32.trunc_f32_s", "i32.trunc_f32_u", "i32.trunc_f64_s", "i32.trunc_f64_u",
        "i64.extend_i32_s", "i64.extend_i32_u",
        "i64.trunc_f32_s", "i64.trunc_f32_u", "i64.trunc_f64_s", "i64.trunc_f64_u",
        "f32.convert_i32_s", "f32.convert_i32_u", "f32.convert_i64_s", "f32.convert_i64_u",
        "f32.demote_f64",
        "f64.convert_i32_s", "f64.convert_i32_u", "f64.convert_i64_s", "f64.convert_i64_u",
        "f64.promote_f32",
        "i32.reinterpret_f32", "i64.reinterpret_f64", "f32.reinterpret_i32", "f64.reinterpret_i64",
        // Sign extension
        "i32.extend8_s", "i32.extend16_s", "i64.extend8_s", "i64.extend16_s", "i64.extend32_s",
        // Reference types
        "ref.null", "ref.is_null", "ref.func",
        // 0xFC prefix
        "i32.trunc_sat_f32_s", "i32.trunc_sat_f32_u", "i32.trunc_sat_f64_s", "i32.trunc_sat_f64_u",
        "i64.trunc_sat_f32_s", "i64.trunc_sat_f32_u", "i64.trunc_sat_f64_s", "i64.trunc_sat_f64_u",
        "memory.init", "data.drop", "memory.copy", "memory.fill",
        "table.init", "elem.drop", "table.copy", "table.grow", "table.size", "table.fill",
        // SIMD
        "v128.load", "v128.load8x8_s", "v128.load8x8_u", "v128.load16x4_s", "v128.load16x4_u",
        "v128.load32x2_s", "v128.load32x2_u", "v128.load8_splat", "v128.load16_splat",
        "v128.load32_splat", "v128.load64_splat", "v128.store",
        "v128.const", "i8x16.shuffle",
        "i8x16.swizzle", "i8x16.splat", "i16x8.splat", "i32x4.splat", "i64x2.splat",
        "f32x4.splat", "f64x2.splat",
        "i8x16.extract_lane_s", "i8x16.extract_lane_u", "i8x16.replace_lane",
        "i16x8.extract_lane_s", "i16x8.extract_lane_u", "i16x8.replace_lane",
        "i32x4.extract_lane", "i32x4.replace_lane",
        "i64x2.extract_lane", "i64x2.replace_lane",
        "f32x4.extract_lane", "f32x4.replace_lane",
        "f64x2.extract_lane", "f64x2.replace_lane",
        // i8x16
        "i8x16.eq", "i8x16.ne", "i8x16.lt_s", "i8x16.lt_u", "i8x16.gt_s", "i8x16.gt_u",
        "i8x16.le_s", "i8x16.le_u", "i8x16.ge_s", "i8x16.ge_u",
        // i16x8
        "i16x8.eq", "i16x8.ne", "i16x8.lt_s", "i16x8.lt_u", "i16x8.gt_s", "i16x8.gt_u",
        "i16x8.le_s", "i16x8.le_u", "i16x8.ge_s", "i16x8.ge_u",
        // i32x4
        "i32x4.eq", "i32x4.ne", "i32x4.lt_s", "i32x4.lt_u", "i32x4.gt_s", "i32x4.gt_u",
        "i32x4.le_s", "i32x4.le_u", "i32x4.ge_s", "i32x4.ge_u",
        // f32x4
        "f32x4.eq", "f32x4.ne", "f32x4.lt", "f32x4.gt", "f32x4.le", "f32x4.ge",
        // f64x2
        "f64x2.eq", "f64x2.ne", "f64x2.lt", "f64x2.gt", "f64x2.le", "f64x2.ge",
        // v128 bitwise
        "v128.not", "v128.and", "v128.andnot", "v128.or", "v128.xor", "v128.bitselect", "v128.any_true",
        // v128 load/store lane
        "v128.load8_lane", "v128.load16_lane", "v128.load32_lane", "v128.load64_lane",
        "v128.store8_lane", "v128.store16_lane", "v128.store32_lane", "v128.store64_lane",
        // v128 load zero
        "v128.load32_zero", "v128.load64_zero",
        // f32x4/f64x2 conversions
        "f32x4.demote_f64x2_zero", "f64x2.promote_low_f32x4",
        // i8x16 unary/narrow/shift
        "i8x16.abs", "i8x16.neg", "i8x16.popcnt", "i8x16.all_true", "i8x16.bitmask",
        "i8x16.narrow_i16x8_s", "i8x16.narrow_i16x8_u",
        "f32x4.ceil", "f32x4.floor", "f32x4.trunc", "f32x4.nearest",
        "i8x16.shl", "i8x16.shr_s", "i8x16.shr_u",
        "i8x16.add", "i8x16.add_sat_s", "i8x16.add_sat_u",
        "i8x16.sub", "i8x16.sub_sat_s", "i8x16.sub_sat_u",
        "f64x2.ceil", "f64x2.floor",
        "i8x16.min_s", "i8x16.min_u", "i8x16.max_s", "i8x16.max_u",
        "f64x2.trunc", "i8x16.avgr_u",
        // Pairwise extension adds
        "i16x8.extadd_pairwise_i8x16_s", "i16x8.extadd_pairwise_i8x16_u",
        "i32x4.extadd_pairwise_i16x8_s", "i32x4.extadd_pairwise_i16x8_u",
        // i16x8
        "i16x8.abs", "i16x8.neg", "i16x8.q15mulr_sat_s", "i16x8.all_true", "i16x8.bitmask",
        "i16x8.narrow_i32x4_s", "i16x8.narrow_i32x4_u",
        "i16x8.extend_low_i8x16_s", "i16x8.extend_high_i8x16_s",
        "i16x8.extend_low_i8x16_u", "i16x8.extend_high_i8x16_u",
        "i16x8.shl", "i16x8.shr_s", "i16x8.shr_u",
        "i16x8.add", "i16x8.add_sat_s", "i16x8.add_sat_u",
        "i16x8.sub", "i16x8.sub_sat_s", "i16x8.sub_sat_u",
        "f64x2.nearest",
        "i16x8.mul", "i16x8.min_s", "i16x8.min_u", "i16x8.max_s", "i16x8.max_u",
        "i16x8.avgr_u",
        "i16x8.extmul_low_i8x16_s", "i16x8.extmul_high_i8x16_s",
        "i16x8.extmul_low_i8x16_u", "i16x8.extmul_high_i8x16_u",
        // i32x4
        "i32x4.abs", "i32x4.neg", "i32x4.all_true", "i32x4.bitmask",
        "i32x4.extend_low_i16x8_s", "i32x4.extend_high_i16x8_s",
        "i32x4.extend_low_i16x8_u", "i32x4.extend_high_i16x8_u",
        "i32x4.shl", "i32x4.shr_s", "i32x4.shr_u",
        "i32x4.add", "i32x4.sub", "i32x4.mul",
        "i32x4.min_s", "i32x4.min_u", "i32x4.max_s", "i32x4.max_u",
        "i32x4.dot_i16x8_s",
        "i64x2.extmul_high_i32x4_u",
        "i32x4.extmul_low_i16x8_s", "i32x4.extmul_high_i16x8_s",
        "i32x4.extmul_low_i16x8_u", "i32x4.extmul_high_i16x8_u",
        // i64x2
        "i64x2.abs", "i64x2.neg", "i64x2.all_true", "i64x2.bitmask",
        "i64x2.extend_low_i32x4_s", "i64x2.extend_high_i32x4_s",
        "i64x2.extend_low_i32x4_u", "i64x2.extend_high_i32x4_u",
        "i64x2.shl", "i64x2.shr_s", "i64x2.shr_u",
        "i64x2.add", "i64x2.sub",
        "i64x2.extmul_low_i32x4_s", "i64x2.extmul_high_i32x4_s",
        "i64x2.extmul_low_i32x4_u",
        "i64x2.mul", "i64x2.eq", "i64x2.ne", "i64x2.lt_s", "i64x2.gt_s", "i64x2.le_s", "i64x2.ge_s",
        // f32x4 arithmetic
        "f32x4.abs", "f32x4.neg", "f32x4.sqrt",
        "f32x4.add", "f32x4.sub", "f32x4.mul", "f32x4.div",
        "f32x4.min", "f32x4.max", "f32x4.pmin", "f32x4.pmax",
        // f64x2 arithmetic
        "f64x2.abs", "f64x2.neg", "f64x2.sqrt",
        "f64x2.add", "f64x2.sub", "f64x2.mul", "f64x2.div",
        "f64x2.min", "f64x2.max", "f64x2.pmin", "f64x2.pmax",
        // SIMD conversions
        "i32x4.trunc_sat_f32x4_s", "i32x4.trunc_sat_f32x4_u",
        "f32x4.convert_i32x4_s", "f32x4.convert_i32x4_u",
        "i32x4.trunc_sat_f64x2_s_zero", "i32x4.trunc_sat_f64x2_u_zero",
        "f64x2.convert_low_i32x4_s", "f64x2.convert_low_i32x4_u",
        // Relaxed SIMD (0xFD 0x100–0x113)
        "i8x16.relaxed_swizzle",
        "i32x4.relaxed_trunc_f32x4_s", "i32x4.relaxed_trunc_f32x4_u",
        "i32x4.relaxed_trunc_f64x2_s_zero", "i32x4.relaxed_trunc_f64x2_u_zero",
        "f32x4.relaxed_madd", "f32x4.relaxed_nmadd",
        "f64x2.relaxed_madd", "f64x2.relaxed_nmadd",
        "i8x16.relaxed_laneselect", "i16x8.relaxed_laneselect",
        "i32x4.relaxed_laneselect", "i64x2.relaxed_laneselect",
        "f32x4.relaxed_min", "f32x4.relaxed_max",
        "f64x2.relaxed_min", "f64x2.relaxed_max",
        "i16x8.relaxed_q15mulr_s",
        "i16x8.relaxed_dot_i8x16_i7x16_s",
        "i32x4.relaxed_dot_i8x16_i7x16_add_s",
    ];

    // ── Section emitters ─────────────────────────────────────────────────

    private static void EmitTypes(WatWriter writer, WasmModule module)
    {
        for (int i = 0; i < module.Types.Count; i++)
        {
            var ft = module.Types[i];
            writer.Write($"  (type (;{i};) (func");
            if (ft.Parameters.Length > 0)
            {
                writer.Write(" (param");
                foreach (var p in ft.Parameters)
                    writer.Write($" {ValTypeName(p)}");
                writer.Write(')');
            }
            if (ft.Results.Length > 0)
            {
                writer.Write(" (result");
                foreach (var r in ft.Results)
                    writer.Write($" {ValTypeName(r)}");
                writer.Write(')');
            }
            writer.WriteLine("))");
        }
    }

    private static void EmitImports(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        int funcIdx = 0, tableIdx = 0, memIdx = 0, globalIdx = 0;
        foreach (var imp in module.Imports)
        {
            writer.Write($"  (import \"{Escape(imp.ModuleName)}\" \"{Escape(imp.Name)}\" ");
            switch (imp.Descriptor)
            {
                case ImportDescriptor.Func f:
                    var name = ctx.FuncNames.TryGetValue(funcIdx, out var n) ? $" ${n}" : "";
                    writer.Write($"(func{name}");
                    // Emit inline params/results (parser doesn't support (type N) in imports)
                    var ft = module.Types[f.TypeIndex];
                    if (ft.Parameters.Length > 0)
                    {
                        writer.Write(" (param");
                        foreach (var p in ft.Parameters)
                            writer.Write($" {ValTypeName(p)}");
                        writer.Write(')');
                    }
                    if (ft.Results.Length > 0)
                    {
                        writer.Write(" (result");
                        foreach (var r in ft.Results)
                            writer.Write($" {ValTypeName(r)}");
                        writer.Write(')');
                    }
                    writer.Write(')');
                    funcIdx++;
                    break;
                case ImportDescriptor.Table t:
                    writer.Write($"(table {tableIdx} {FormatLimits(t.Type.Limits)} {RefTypeName(t.Type.ElementType)})");
                    tableIdx++;
                    break;
                case ImportDescriptor.Memory m:
                    writer.Write($"(memory {memIdx} {FormatLimits(m.Type.Limits)})");
                    memIdx++;
                    break;
                case ImportDescriptor.Global g:
                    writer.Write($"(global {globalIdx} {FormatGlobalType(g.Type)})");
                    globalIdx++;
                    break;
                case ImportDescriptor.Tag tag:
                    writer.Write($"(tag (type {tag.TypeIndex}))");
                    break;
            }
            writer.WriteLine(")");
        }
    }

    private static void EmitFunctions(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Functions.Count; i++)
        {
            var func = module.Functions[i];
            int absIdx = ctx.ImportedFuncCount + i;
            var funcName = ctx.FuncNames.TryGetValue(absIdx, out var n) ? $" ${n}" : "";
            var ft = module.Types[func.TypeIndex];

            writer.Write($"  (func{funcName} (type {func.TypeIndex})");

            // Params
            if (ft.Parameters.Length > 0)
            {
                writer.Write(" (param");
                foreach (var p in ft.Parameters)
                    writer.Write($" {ValTypeName(p)}");
                writer.Write(')');
            }
            // Results
            if (ft.Results.Length > 0)
            {
                writer.Write(" (result");
                foreach (var r in ft.Results)
                    writer.Write($" {ValTypeName(r)}");
                writer.Write(')');
            }
            writer.WriteLine();

            // Locals
            if (func.Locals.Count > 0)
            {
                // Group consecutive locals of same type
                int li = 0;
                while (li < func.Locals.Count)
                {
                    var lt = func.Locals[li];
                    int count = 1;
                    while (li + count < func.Locals.Count && func.Locals[li + count] == lt)
                        count++;
                    writer.Write("    (local");
                    for (int c = 0; c < count; c++)
                        writer.Write($" {ValTypeName(lt)}");
                    writer.WriteLine(")");
                    li += count;
                }
            }

            // Body instructions
            DisassembleBody(writer, func.Body, ctx);

            writer.WriteLine("  )");
        }
    }

    private static void EmitTables(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Tables.Count; i++)
        {
            int absIdx = ctx.ImportedTableCount + i;
            var t = module.Tables[i];
            writer.WriteLine($"  (table (;{absIdx};) {FormatLimits(t.Limits)} {RefTypeName(t.ElementType)})");
        }
    }

    private static void EmitMemories(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Memories.Count; i++)
        {
            int absIdx = ctx.ImportedMemCount + i;
            writer.WriteLine($"  (memory (;{absIdx};) {FormatLimits(module.Memories[i].Limits)})");
        }
    }

    private static void EmitGlobals(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Globals.Count; i++)
        {
            int absIdx = ctx.ImportedGlobalCount + i;
            var g = module.Globals[i];
            writer.Write($"  (global (;{absIdx};) {FormatGlobalType(g.Type)} (");
            DisassembleInitExpr(writer, g.InitExpr, ctx);
            writer.WriteLine("))");
        }
    }

    private static void EmitExports(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        foreach (var (name, export) in module.Exports)
        {
            writer.Write($"  (export \"{Escape(name)}\" ");
            switch (export.Descriptor)
            {
                case ExportDescriptor.Func f:
                    var fn = ctx.FuncNames.TryGetValue(f.FunctionIndex, out var n) ? $"${n}" : f.FunctionIndex.ToString();
                    writer.Write($"(func {fn})");
                    break;
                case ExportDescriptor.Table t:
                    writer.Write($"(table {t.TableIndex})");
                    break;
                case ExportDescriptor.Memory m:
                    writer.Write($"(memory {m.MemoryIndex})");
                    break;
                case ExportDescriptor.Global g:
                    writer.Write($"(global {g.GlobalIndex})");
                    break;
                case ExportDescriptor.Tag t:
                    writer.Write($"(tag {t.TagIndex})");
                    break;
            }
            writer.WriteLine(")");
        }
    }

    private static void EmitStart(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        if (module.StartFunction.HasValue)
        {
            var fn = ctx.FuncNames.TryGetValue(module.StartFunction.Value, out var n) ? $"${n}" : module.StartFunction.Value.ToString();
            writer.WriteLine($"  (start {fn})");
        }
    }

    private static void EmitElements(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Elements.Count; i++)
        {
            var elem = module.Elements[i];
            if (elem.TableIndex >= 0 && elem.OffsetExpr.Length > 0)
            {
                // Active element segment
                writer.Write($"  (elem (;{i};) ");
                if (elem.TableIndex != 0)
                    writer.Write($"(table {elem.TableIndex}) ");
                writer.Write('(');
                DisassembleInitExpr(writer, elem.OffsetExpr, ctx);
                writer.Write(") func");
                foreach (var funcIdx in elem.FunctionIndices)
                {
                    var fn = ctx.FuncNames.TryGetValue(funcIdx, out var n) ? $" ${n}" : $" {funcIdx}";
                    writer.Write(fn);
                }
                writer.WriteLine(")");
            }
            else
            {
                // Passive element segment
                writer.Write($"  (elem (;{i};) func");
                foreach (var funcIdx in elem.FunctionIndices)
                {
                    if (funcIdx >= 0)
                    {
                        var fn = ctx.FuncNames.TryGetValue(funcIdx, out var n) ? $" ${n}" : $" {funcIdx}";
                        writer.Write(fn);
                    }
                }
                writer.WriteLine(")");
            }
        }
    }

    private static void EmitData(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.DataSegments.Count; i++)
        {
            var data = module.DataSegments[i];
            if (data.IsPassive)
            {
                writer.Write($"  (data (;{i};) \"{EscapeDataString(data.InitData)}\")");
            }
            else
            {
                writer.Write($"  (data (;{i};) ");
                if (data.MemoryIndex != 0)
                    writer.Write($"(memory {data.MemoryIndex}) ");
                writer.Write('(');
                DisassembleInitExpr(writer, data.OffsetExpr, ctx);
                writer.Write($") \"{EscapeDataString(data.InitData)}\")");
            }
            writer.WriteLine();
        }
    }

    // ── Init expression disassembly ──────────────────────────────────────

    private static void DisassembleInitExpr(WatWriter writer, byte[] expr, DisassemblyContext ctx)
    {
        int pos = 0;
        bool first = true;
        while (pos < expr.Length)
        {
            byte op = expr[pos++];
            if (op == 0x0B) break; // END

            if (!first) writer.Write(' ');
            first = false;

            switch (op)
            {
                case 0x41: // i32.const
                    int i32val = ReadS32Leb128(expr, ref pos);
                    writer.Write($"i32.const {i32val}");
                    break;
                case 0x42: // i64.const
                    long i64val = ReadS64Leb128(expr, ref pos);
                    writer.Write($"i64.const {i64val}");
                    break;
                case 0x43: // f32.const
                    float f32val = BinaryPrimitives.ReadSingleLittleEndian(expr.AsSpan(pos));
                    pos += 4;
                    writer.Write($"f32.const {FormatFloat(f32val)}");
                    break;
                case 0x44: // f64.const
                    double f64val = BinaryPrimitives.ReadDoubleLittleEndian(expr.AsSpan(pos));
                    pos += 8;
                    writer.Write($"f64.const {FormatDouble(f64val)}");
                    break;
                case 0x23: // global.get
                    uint gidx = ReadU32Leb128(expr, ref pos);
                    writer.Write($"global.get {gidx}");
                    break;
                case 0xD0: // ref.null
                    byte refByte = expr[pos++];
                    writer.Write($"ref.null {RefTypeNameFromByte(refByte)}");
                    break;
                case 0xD2: // ref.func
                    uint fidx = ReadU32Leb128(expr, ref pos);
                    var fn = ctx.FuncNames.TryGetValue((int)fidx, out var n) ? $"${n}" : fidx.ToString();
                    writer.Write($"ref.func {fn}");
                    break;
                case 0xFD: // v128.const
                    ReadU32Leb128(expr, ref pos); // sub-opcode (12)
                    writer.Write("v128.const i32x4");
                    for (int j = 0; j < 4; j++)
                    {
                        int v = BinaryPrimitives.ReadInt32LittleEndian(expr.AsSpan(pos));
                        pos += 4;
                        writer.Write($" 0x{(uint)v:x8}");
                    }
                    break;
                // Extended constants
                case 0x6A: writer.Write("i32.add"); break;
                case 0x6B: writer.Write("i32.sub"); break;
                case 0x6C: writer.Write("i32.mul"); break;
                case 0x7C: writer.Write("i64.add"); break;
                case 0x7D: writer.Write("i64.sub"); break;
                case 0x7E: writer.Write("i64.mul"); break;
                default:
                    writer.Write($"<unknown 0x{op:X2}>");
                    break;
            }
        }
    }

    // ── Instruction body disassembly ─────────────────────────────────────

    private static void DisassembleBody(WatWriter writer, byte[] body, DisassemblyContext ctx)
    {
        int pos = 0;
        int indent = 2; // base indent for function body
        // Track block kinds for proper folded-style closing
        var blockStack = new Stack<string>();

        while (pos < body.Length)
        {
            byte opByte = body[pos++];

            // END — close the current block with )
            if (opByte == 0x0B)
            {
                if (pos >= body.Length)
                    break; // final function-level end — don't emit
                var block = blockStack.Count > 0 ? blockStack.Pop() : "block";
                if (block == "if-then")
                {
                    // Close (then and (if — no else branch
                    indent--;
                    Indent(writer, indent); writer.WriteLine(")");
                    indent--;
                    Indent(writer, indent); writer.WriteLine(")");
                }
                else if (block == "if-else")
                {
                    // Close (else and (if
                    indent--;
                    Indent(writer, indent); writer.WriteLine(")");
                    indent--;
                    Indent(writer, indent); writer.WriteLine(")");
                }
                else
                {
                    // Close (block, (loop, (try_table, etc.
                    indent--;
                    Indent(writer, indent); writer.WriteLine(")");
                }
                continue;
            }

            // ELSE — close (then, open (else
            if (opByte == 0x05)
            {
                if (blockStack.Count > 0) blockStack.Pop(); // pop "if-then"
                indent--;
                Indent(writer, indent); writer.WriteLine(")");
                Indent(writer, indent); writer.WriteLine("(else");
                indent++;
                blockStack.Push("if-else");
                continue;
            }

            // Block-opening: block, loop
            if (opByte is 0x02 or 0x03)
            {
                string mnemonic = opByte == 0x02 ? "block" : "loop";
                Indent(writer, indent);
                writer.Write($"({mnemonic}");
                EmitBlockType(writer, body, ref pos);
                writer.WriteLine();
                indent++;
                blockStack.Push(mnemonic);
                continue;
            }

            // If
            if (opByte == 0x04)
            {
                Indent(writer, indent);
                writer.Write("(if");
                EmitBlockType(writer, body, ref pos);
                writer.WriteLine();
                indent++;
                Indent(writer, indent);
                writer.WriteLine("(then");
                indent++;
                blockStack.Push("if-then");
                continue;
            }

            // try (legacy)
            if (opByte == 0x06)
            {
                Indent(writer, indent);
                writer.Write("(block");  // emit as block for safety
                EmitBlockType(writer, body, ref pos);
                writer.WriteLine();
                indent++;
                blockStack.Push("block");
                continue;
            }

            // try_table
            if (opByte == 0x1F)
            {
                Indent(writer, indent);
                writer.Write("(try_table");
                EmitTryTableImmediate(writer, body, ref pos);
                writer.WriteLine();
                indent++;
                blockStack.Push("try_table");
                continue;
            }

            // Prefixed opcodes
            if (opByte is 0xE0 or 0xFC or 0xFD)
            {
                uint sub = ReadU32Leb128(body, ref pos);
                uint key = ((uint)opByte << 24) | sub;
                if (!ctx.PrefixedOpcodes.TryGetValue(key, out var prefixedEntry))
                {
                    Indent(writer, indent);
                    writer.WriteLine($"<unknown 0x{opByte:X2} {sub}>");
                    continue;
                }
                Indent(writer, indent);
                writer.Write(prefixedEntry.Mnemonic);
                EmitImmediate(writer, body, ref pos, prefixedEntry.Imm, prefixedEntry.Mnemonic, ctx);
                writer.WriteLine();
                continue;
            }

            if (!ctx.SingleByteOpcodes.TryGetValue(opByte, out var entry))
            {
                Indent(writer, indent);
                writer.WriteLine($"<unknown 0x{opByte:X2}>");
                continue;
            }

            Indent(writer, indent);
            writer.Write(entry.Mnemonic);
            EmitImmediate(writer, body, ref pos, entry.Imm, entry.Mnemonic, ctx);
            writer.WriteLine();
        }
    }

    private static void EmitTryTableImmediate(WatWriter writer, byte[] body, ref int pos)
    {
        EmitBlockType(writer, body, ref pos);
        uint handlerCount = ReadU32Leb128(body, ref pos);
        for (uint i = 0; i < handlerCount; i++)
        {
            byte kind = body[pos++];
            switch (kind)
            {
                case 0:
                {
                    uint tagIdx = ReadU32Leb128(body, ref pos);
                    uint label = ReadU32Leb128(body, ref pos);
                    writer.Write($" (catch {tagIdx} {label})");
                    break;
                }
                case 1:
                {
                    uint tagIdx = ReadU32Leb128(body, ref pos);
                    uint label = ReadU32Leb128(body, ref pos);
                    writer.Write($" (catch_ref {tagIdx} {label})");
                    break;
                }
                case 2:
                {
                    uint label = ReadU32Leb128(body, ref pos);
                    writer.Write($" (catch_all {label})");
                    break;
                }
                case 3:
                {
                    uint label = ReadU32Leb128(body, ref pos);
                    writer.Write($" (catch_all_ref {label})");
                    break;
                }
            }
        }
    }

    private static void EmitImmediate(WatWriter writer, byte[] body, ref int pos,
        ImmediateKind imm, string mnemonic, DisassemblyContext ctx)
    {
        switch (imm)
        {
            case ImmediateKind.None:
                break;

            case ImmediateKind.BlockType:
                EmitBlockType(writer, body, ref pos);
                break;

            case ImmediateKind.LocalIdx:
            case ImmediateKind.GlobalIdx:
            case ImmediateKind.FuncIdx:
            case ImmediateKind.TableIdx:
            case ImmediateKind.TagIdx:
            case ImmediateKind.LabelIdx:
            case ImmediateKind.DataIdx:
            case ImmediateKind.ElemIdx:
            case ImmediateKind.MemIdx:
            case ImmediateKind.TypeIdx:
            {
                uint idx = ReadU32Leb128(body, ref pos);
                if (imm == ImmediateKind.FuncIdx && ctx.FuncNames.TryGetValue((int)idx, out var n))
                    writer.Write($" ${n}");
                else
                    writer.Write($" {idx}");
                break;
            }

            case ImmediateKind.I32:
            {
                int val = ReadS32Leb128(body, ref pos);
                writer.Write($" {val}");
                break;
            }

            case ImmediateKind.I64:
            {
                long val = ReadS64Leb128(body, ref pos);
                writer.Write($" {val}");
                break;
            }

            case ImmediateKind.F32:
            {
                float val = BinaryPrimitives.ReadSingleLittleEndian(body.AsSpan(pos));
                pos += 4;
                writer.Write($" {FormatFloat(val)}");
                break;
            }

            case ImmediateKind.F64:
            {
                double val = BinaryPrimitives.ReadDoubleLittleEndian(body.AsSpan(pos));
                pos += 8;
                writer.Write($" {FormatDouble(val)}");
                break;
            }

            case ImmediateKind.MemArg:
            {
                uint align = ReadU32Leb128(body, ref pos);
                uint offset = ReadU32Leb128(body, ref pos);
                var natAlign = OpcodeTable.NaturalAlignment(mnemonic);
                if (offset != 0)
                    writer.Write($" offset={offset}");
                if (align != natAlign)
                    writer.Write($" align={1u << (int)align}");
                break;
            }

            case ImmediateKind.BrTable:
            {
                uint count = ReadU32Leb128(body, ref pos);
                for (uint i = 0; i < count; i++)
                {
                    uint label = ReadU32Leb128(body, ref pos);
                    writer.Write($" {label}");
                }
                uint defaultLabel = ReadU32Leb128(body, ref pos);
                writer.Write($" {defaultLabel}");
                break;
            }

            case ImmediateKind.CallIndirect:
            {
                uint typeIdx = ReadU32Leb128(body, ref pos);
                uint tableIdx = ReadU32Leb128(body, ref pos);
                writer.Write($" (type {typeIdx})");
                if (tableIdx != 0)
                    writer.Write($" {tableIdx}");
                break;
            }

            case ImmediateKind.TypePair:
            {
                uint first = ReadU32Leb128(body, ref pos);
                uint second = ReadU32Leb128(body, ref pos);
                writer.Write($" {first} {second}");
                break;
            }

            case ImmediateKind.TypeTagPair:
            {
                uint typeIdx = ReadU32Leb128(body, ref pos);
                uint tagIdx = ReadU32Leb128(body, ref pos);
                writer.Write($" {typeIdx} {tagIdx}");
                break;
            }

            case ImmediateKind.ResumeTable:
            {
                uint typeIdx = ReadU32Leb128(body, ref pos);
                uint handlerCount = ReadU32Leb128(body, ref pos);
                writer.Write($" {typeIdx}");
                for (uint i = 0; i < handlerCount; i++)
                {
                    uint tagIdx = ReadU32Leb128(body, ref pos);
                    uint labelIdx = ReadU32Leb128(body, ref pos);
                    writer.Write($" (on {tagIdx} {labelIdx})");
                }
                break;
            }

            case ImmediateKind.V128:
            {
                writer.Write(" i32x4");
                for (int i = 0; i < 4; i++)
                {
                    int val = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
                    pos += 4;
                    writer.Write($" 0x{(uint)val:x8}");
                }
                break;
            }

            case ImmediateKind.SimdLane:
            {
                byte lane = body[pos++];
                writer.Write($" {lane}");
                break;
            }

            case ImmediateKind.SimdShuffle:
            {
                for (int i = 0; i < 16; i++)
                    writer.Write($" {body[pos++]}");
                break;
            }

            case ImmediateKind.MemArgLane:
            {
                uint align = ReadU32Leb128(body, ref pos);
                uint offset = ReadU32Leb128(body, ref pos);
                byte lane = body[pos++];
                var natAlign = OpcodeTable.NaturalAlignment(mnemonic);
                if (offset != 0)
                    writer.Write($" offset={offset}");
                if (align != natAlign)
                    writer.Write($" align={1u << (int)align}");
                writer.Write($" {lane}");
                break;
            }

            case ImmediateKind.RefType:
            {
                byte rt = body[pos++];
                writer.Write($" {RefTypeNameFromByte(rt)}");
                break;
            }

            case ImmediateKind.DataMemIdx:
            {
                uint dataIdx = ReadU32Leb128(body, ref pos);
                uint memIdx = ReadU32Leb128(body, ref pos);
                writer.Write($" {dataIdx}");
                if (memIdx != 0) writer.Write($" {memIdx}");
                break;
            }

            case ImmediateKind.MemCopy:
            {
                uint dst = ReadU32Leb128(body, ref pos);
                uint src = ReadU32Leb128(body, ref pos);
                if (dst != 0 || src != 0)
                    writer.Write($" {dst} {src}");
                break;
            }

            case ImmediateKind.ElemTableIdx:
            {
                uint elemIdx = ReadU32Leb128(body, ref pos);
                uint tableIdx = ReadU32Leb128(body, ref pos);
                writer.Write($" {elemIdx} {tableIdx}");
                break;
            }

            case ImmediateKind.TableCopy:
            {
                uint dst = ReadU32Leb128(body, ref pos);
                uint src = ReadU32Leb128(body, ref pos);
                writer.Write($" {dst} {src}");
                break;
            }

            case ImmediateKind.TryTable:
                // Handled by EmitTryTableImmediate in DisassembleBody
                break;
        }
    }

    private static void EmitBlockType(WatWriter writer, byte[] body, ref int pos)
    {
        byte b = body[pos];
        if (b == 0x40)
        {
            // void block — no output needed
            pos++;
        }
        else if (b == (byte)ValType.ContRef || (b >= 0x6F && b <= 0x7F))
        {
            // value type
            pos++;
            writer.Write($" (result {ValTypeName((ValType)b)})");
        }
        else
        {
            // type index (s33 LEB)
            long typeIdx = ReadS33Leb128(body, ref pos);
            writer.Write($" (type {typeIdx})");
        }
    }

    // ── LEB128 readers (from byte array) ─────────────────────────────────

    private static uint ReadU32Leb128(byte[] data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return result;
            shift += 7;
        }
        return result;
    }

    private static int ReadS32Leb128(byte[] data, ref int pos)
    {
        int result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[pos++];
            result |= (int)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 32 && (b & 0x40) != 0)
            result |= ~0 << shift;
        return result;
    }

    private static long ReadS64Leb128(byte[] data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
            result |= ~0L << shift;
        return result;
    }

    private static long ReadS33Leb128(byte[] data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
            result |= ~0L << shift;
        return result;
    }

    // ── Formatting helpers ───────────────────────────────────────────────

    private static string ValTypeName(ValType vt) => vt switch
    {
        ValType.I32 => "i32",
        ValType.I64 => "i64",
        ValType.F32 => "f32",
        ValType.F64 => "f64",
        ValType.V128 => "v128",
        ValType.FuncRef => "funcref",
        ValType.ExternRef => "externref",
        ValType.ExnRef => "exnref",
        ValType.ContRef => "contref",
        _ => $"<unknown 0x{(byte)vt:X2}>",
    };

    private static string RefTypeName(RefType rt) => rt switch
    {
        RefType.FuncRef => "funcref",
        RefType.ExternRef => "externref",
        _ => $"<unknown 0x{(byte)rt:X2}>",
    };

    private static string RefTypeNameFromByte(byte b) => b switch
    {
        0x70 => "funcref",
        0x6F => "externref",
        0x69 => "exnref",
        0x65 => "contref",
        _ => $"0x{b:X2}",
    };

    private static string FormatLimits(Limits limits) =>
        limits.Max.HasValue ? $"{limits.Min} {limits.Max.Value}" : $"{limits.Min}";

    private static string FormatGlobalType(GlobalType gt) =>
        gt.Mutability == Mutability.Var
            ? $"(mut {ValTypeName(gt.ContentType)})"
            : ValTypeName(gt.ContentType);

    private static string FormatFloat(float val)
    {
        if (float.IsNaN(val))
        {
            uint bits = BitConverter.SingleToUInt32Bits(val);
            return (bits & 0x80000000u) != 0
                ? $"nan:0x{bits & 0x7FFFFFu:x6}"
                : $"nan:0x{bits & 0x7FFFFFu:x6}";
        }
        if (float.IsPositiveInfinity(val)) return "inf";
        if (float.IsNegativeInfinity(val)) return "-inf";
        // Use round-trip format
        string s = val.ToString("G9", CultureInfo.InvariantCulture);
        // Ensure it has a decimal point
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
            s += ".0";
        return s;
    }

    private static string FormatDouble(double val)
    {
        if (double.IsNaN(val))
        {
            ulong bits = BitConverter.DoubleToUInt64Bits(val);
            return $"nan:0x{bits & 0xFFFFFFFFFFFFFuL:x13}";
        }
        if (double.IsPositiveInfinity(val)) return "inf";
        if (double.IsNegativeInfinity(val)) return "-inf";
        string s = val.ToString("G17", CultureInfo.InvariantCulture);
        if (!s.Contains('.') && !s.Contains('E') && !s.Contains('e'))
            s += ".0";
        return s;
    }

    private static string Escape(string s) =>
        s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", "\\n").Replace("\r", "\\r").Replace("\t", "\\t");

    private static string EscapeDataString(byte[] data)
    {
        var sb = new StringBuilder(data.Length * 2);
        foreach (byte b in data)
        {
            if (b >= 0x20 && b < 0x7F && b != '"' && b != '\\')
                sb.Append((char)b);
            else
                sb.Append($"\\{b:x2}");
        }
        return sb.ToString();
    }

    private static void Indent(WatWriter writer, int level)
    {
        for (int i = 0; i < level; i++)
            writer.Write("  ");
    }
}
