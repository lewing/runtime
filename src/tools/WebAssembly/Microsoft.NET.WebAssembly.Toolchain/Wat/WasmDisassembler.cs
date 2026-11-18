// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using ExactValueType = Microsoft.NET.WebAssembly.Types.ValueType;

/// <summary>Disassembles a parsed <see cref="WasmModule"/> into WAT text format.</summary>
public static class WasmDisassembler
{
    internal readonly record struct InstructionBoundary(
        OpcodeId OpcodeId,
        int ByteOffset,
        int ImmediateOffset,
        int EndOffset);

    internal sealed record OmittedInstruction(
        int Pc,
        OpcodeId OpcodeId,
        int ByteOffsetStart,
        int ByteOffsetEnd,
        string Reason);

    internal sealed record FunctionBodyWindow(
        string Preview,
        int RenderedInstructions,
        int CoveredInstructionStart,
        int CoveredInstructionEnd,
        OmittedInstruction? Omitted,
        int? PreviousInstructionStart);

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

        // Emit the module header, including the name if the name section provided one.
        if (ctx.ModuleName != null)
        {
            if (ctx.ModuleName.Length == 0)
                writer.WriteLine($"(module (@name \"{Escape(ctx.ModuleName)}\")");
            else
                writer.WriteLine($"(module {FormatIdentifier(ctx.ModuleName)}");
        }
        else
        {
            writer.WriteLine("(module");
        }

        EmitTypes(writer, module, ctx);
        EmitImports(writer, module, ctx);
        EmitFunctions(writer, module, ctx);
        EmitTables(writer, module, ctx);
        EmitMemories(writer, module, ctx);
        EmitGlobals(writer, module, ctx);
        EmitTags(writer, module, ctx);
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
        var writer = WatWriter.CreateStringWriter(out var sw);
        DisassembleFunction(module, exportFunctionIndex, writer, inspectionMode: false);
        writer.Flush();
        return sw.ToString();
    }

    internal static long MeasureDisassembledFunctionSizeBytes(
        WasmModule module,
        int exportFunctionIndex)
    {
        using var counter = new Utf8CountingTextWriter();
        using var writer = new WatWriter(counter);
        DisassembleFunction(module, exportFunctionIndex, writer, inspectionMode: true);
        writer.Flush();
        return counter.ByteCount;
    }

    private static void DisassembleFunction(
        WasmModule module,
        int exportFunctionIndex,
        WatWriter writer,
        bool inspectionMode)
    {
        var ctx = new DisassemblyContext(module);

        int localIndex = exportFunctionIndex - ctx.ImportedFuncCount;
        if (localIndex < 0 || localIndex >= module.Functions.Count)
            throw new ArgumentOutOfRangeException(nameof(exportFunctionIndex),
                $"Function index {exportFunctionIndex} is out of range");

        var func = module.Functions[localIndex];
        var funcName = ctx.FuncNames.TryGetValue(exportFunctionIndex, out var n) ? $" {FormatDefName(n)}" : "";
        var ft = ExactFunctionType(module, func.TypeIndex, ctx);

        writer.Write($"(func{funcName}");

        ctx.LocalNames.TryGetValue(exportFunctionIndex, out var localNames);
        EmitParameters(writer, ft.Parameters, localNames, ctx);
        EmitResults(writer, ft.Results, ctx);
        writer.WriteLine();

        EmitLocals(
            writer,
            ExactLocals(func),
            ft.Parameters.Count,
            localNames,
            ctx,
            indent: 1);

        DisassembleBody(
            writer,
            func.Body,
            func.BranchHints,
            ctx,
            exportFunctionIndex,
            inspectionMode: inspectionMode);

        writer.Write(')');
    }

    /// <summary>Disassemble an instruction-aligned window from one defined function body.</summary>
    /// <param name="module">The parsed module.</param>
    /// <param name="exportFunctionIndex">The absolute function index (including imports).</param>
    /// <param name="instructions">Decoded boundaries shared with the caller's window selection.</param>
    /// <param name="startInstruction">First instruction PC to include.</param>
    /// <param name="endInstructionExclusive">Instruction PC after the last instruction to include.</param>
    /// <param name="maxCharacters">Maximum preview characters to materialize.</param>
    /// <param name="previousInstructionLimit">Maximum preceding instructions considered for backward pagination.</param>
    /// <param name="requiredInstruction">Instruction PC that must be included or reported as omitted.</param>
    /// <param name="measureCharacters">Optional output-specific character measurement.</param>
    internal static FunctionBodyWindow DisassembleFunctionBodyWindow(
        WasmModule module,
        int exportFunctionIndex,
        IReadOnlyList<InstructionBoundary> instructions,
        int startInstruction,
        int endInstructionExclusive,
        int maxCharacters,
        int previousInstructionLimit,
        int? requiredInstruction = null,
        Func<string, int>? measureCharacters = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(startInstruction);
        ArgumentOutOfRangeException.ThrowIfLessThan(endInstructionExclusive, startInstruction);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(endInstructionExclusive, instructions.Count);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxCharacters);
        ArgumentOutOfRangeException.ThrowIfNegative(previousInstructionLimit);
        if (requiredInstruction is int required &&
            (required < 0 ||
             required >= instructions.Count ||
             required < startInstruction ||
             required >= endInstructionExclusive))
            throw new ArgumentOutOfRangeException(nameof(requiredInstruction));

        var ctx = new DisassemblyContext(module);
        int localIndex = exportFunctionIndex - ctx.ImportedFuncCount;
        if (localIndex < 0 || localIndex >= module.Functions.Count)
            throw new ArgumentOutOfRangeException(nameof(exportFunctionIndex),
                $"Function index {exportFunctionIndex} is out of range");

        var func = module.Functions[localIndex];
        return DisassembleBodyWindow(
            func.Body,
            func.BranchHints,
            ctx,
            exportFunctionIndex,
            instructions,
            startInstruction,
            endInstructionExclusive,
            maxCharacters,
            previousInstructionLimit,
            requiredInstruction,
            measureCharacters ?? (static text => text.Length));
    }

    internal static IReadOnlyList<InstructionBoundary> DecodeFunctionBodyInstructions(byte[] body)
    {
        var instructions = new List<InstructionBoundary>();
        var cursor = new InstructionCursor(body, WasmFeatures.All);
        while (cursor.MoveNext(out var instruction))
        {
            bool isImplicitFunctionEnd =
                instruction.OpcodeId.Opcode == WasmOpcode.End &&
                cursor.Position == body.Length;
            if (isImplicitFunctionEnd)
                break;

            instructions.Add(new InstructionBoundary(
                instruction.OpcodeId,
                instruction.ByteOffset,
                instruction.ImmediateOffset,
                cursor.Position));
        }
        return instructions;
    }

    internal static string GetInstructionMnemonic(OpcodeId opcodeId)
    {
        foreach (string mnemonic in GetAllMnemonics())
        {
            if (!OpcodeTable.TryGet(mnemonic, out var entry) || entry.Opcodes.Length == 0)
                continue;
            if (entry.Opcodes[0] != opcodeId.Opcode)
                continue;
            if (entry.Opcodes.Length == 1 && !opcodeId.IsPrefixed)
                return mnemonic;
            if (entry.Opcodes.Length > 1 &&
                opcodeId.IsPrefixed &&
                DecodeOpcodeSubImmediate(entry.Opcodes.AsSpan(1)) == opcodeId.SubOpcode)
            {
                return mnemonic;
            }
        }
        return opcodeId.ToString();
    }

    private static uint DecodeOpcodeSubImmediate(ReadOnlySpan<byte> bytes)
    {
        int position = 0;
        return WasmLeb128.ReadU32(bytes, ref position);
    }

    // ── Context ──────────────────────────────────────────────────────────

    private sealed class DisassemblyContext
    {
        public readonly WasmModule Module;
        public readonly int ImportedFuncCount;
        public readonly int ImportedTableCount;
        public readonly int ImportedMemCount;
        public readonly int ImportedGlobalCount;
        public readonly int ImportedTagCount;
        public readonly Dictionary<int, string> FuncNames;
        public readonly Dictionary<int, string> TypeNames = [];
        public readonly Dictionary<int, string> TypeBindingNames = [];
        public readonly Dictionary<int, Dictionary<int, string>> LocalNames;
        public readonly Dictionary<int, TypeDefinition> ExactTypeDefinitions = [];
        public readonly Dictionary<int, RecGroupIdentity> RecGroups = [];
        public string? ModuleName;

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
                    case ImportDescriptor.Tag: ImportedTagCount++; break;
                }
            }

            IndexExactTypeMetadata(module);
            BuildReverseOpcodeTable();
            // Function ids come from the name section only: they become WAT $ids, which must
            // round-trip, so the resolver's export/import display fallbacks do not apply here.
            var nameSection = Microsoft.NET.WebAssembly.Debugging.Symbols.ModuleSymbols.For(module).NameSection.Contents;
            FuncNames = new Dictionary<int, string>(nameSection.FunctionNames);
            LocalNames = nameSection.LocalNames.ToDictionary(kv => kv.Key, kv => new Dictionary<int, string>(kv.Value));
            ParseNameSection(module);
            BuildTypeBindingNames();
        }

        private void IndexExactTypeMetadata(WasmModule module)
        {
            foreach (TypeDefinition definition in module.TypeDefinitions)
            {
                if ((uint)definition.Index >= (uint)module.Types.Count)
                {
                    throw new WatException(
                        $"Exact type definition index {definition.Index} is outside the module's " +
                        $"{module.Types.Count}-entry type index space.");
                }
                if (!ExactTypeDefinitions.TryAdd(definition.Index, definition))
                    throw new WatException($"Exact type definition index {definition.Index} is duplicated.");

                RecGroupIdentity group = definition.RecGroup;
                if (group.Count <= 0 ||
                    group.StartTypeIndex < 0 ||
                    group.StartTypeIndex > module.Types.Count - group.Count ||
                    definition.Index < group.StartTypeIndex ||
                    definition.Index >= group.StartTypeIndex + group.Count)
                {
                    throw new WatException(
                        $"Exact type definition {definition.Index} has invalid recursion group " +
                        $"{group.Index} [{group.StartTypeIndex}, " +
                        $"{group.StartTypeIndex + group.Count}).");
                }

                if (RecGroups.TryGetValue(group.Index, out RecGroupIdentity? existing) &&
                    existing != group)
                {
                    throw new WatException(
                        $"Recursion group {group.Index} has conflicting extents " +
                        $"[{existing.StartTypeIndex}, {existing.StartTypeIndex + existing.Count}) and " +
                        $"[{group.StartTypeIndex}, {group.StartTypeIndex + group.Count}).");
                }
                RecGroups[group.Index] = group;
            }

            foreach (RecGroupIdentity group in RecGroups.Values)
            {
                for (int index = group.StartTypeIndex;
                     index < group.StartTypeIndex + group.Count;
                     index++)
                {
                    if (!ExactTypeDefinitions.TryGetValue(index, out TypeDefinition? member) ||
                        member.RecGroup != group)
                    {
                        throw new WatException(
                            $"Recursion group {group.Index} requires exact type definition {index}; " +
                            "compact fallback cannot represent only part of a recursion group.");
                    }
                }
            }
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
                    int subPosition = 1;
                    uint sub = WasmLeb128.ReadU32(opcodes, ref subPosition);
                    uint key = ((uint)opcodes[0] << 24) | sub;
                    PrefixedOpcodes.TryAdd(key, (mnemonic, entry.Immediate));
                }
            }
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

                switch ((NameSubsectionId)subsectionId)
                {
                    case NameSubsectionId.Module:
                        ModuleName = reader.ReadName();
                        break;
                    case NameSubsectionId.Function:
                    case NameSubsectionId.Local:
                        // Parsed by WasmNameSectionReader for all consumers.
                        break;
                    case NameSubsectionId.Type:
                    {
                        uint count = reader.ReadU32Leb128();
                        for (uint i = 0; i < count; i++)
                            TypeNames[(int)reader.ReadU32Leb128()] = reader.ReadName();
                        break;
                    }
                    default:
                        // Subsections 3+ are not modeled; their references remain numeric in WAT.
                        reader.ReadBytes((int)(subsectionSize - (reader.Position - start)));
                        break;
                }

                // Ensure we consumed exactly subsectionSize bytes
                long expected = start + subsectionSize;
                if (reader.Position < expected)
                    reader.ReadBytes((int)(expected - reader.Position));
            }
        }

        private void BuildTypeBindingNames()
        {
            var counts = TypeNames.Values
                .Where(static name => name.Length > 0)
                .GroupBy(static name => name, StringComparer.Ordinal)
                .ToDictionary(
                    static group => group.Key,
                    static group => group.Count(),
                    StringComparer.Ordinal);
            var used = new HashSet<string>(
                counts.Where(static entry => entry.Value == 1).Select(static entry => entry.Key),
                StringComparer.Ordinal);

            foreach ((int typeIndex, string name) in TypeNames.OrderBy(static entry => entry.Key))
            {
                if (name.Length == 0)
                    continue;
                if (counts[name] == 1)
                {
                    TypeBindingNames[typeIndex] = name;
                    continue;
                }

                string candidate = $"__nesm_type_{typeIndex}";
                while (!used.Add(candidate))
                    candidate += "_";
                TypeBindingNames[typeIndex] = candidate;
            }
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
        "call_indirect", "return_call", "return_call_indirect", "call_ref", "return_call_ref",
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
        "i64.add128", "i64.sub128", "i64.mul_wide_s", "i64.mul_wide_u",
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

    private static void EmitTypes(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Types.Count;)
        {
            if (ctx.ExactTypeDefinitions.TryGetValue(i, out TypeDefinition? definition) &&
                definition.RecGroup.Count > 1)
            {
                RecGroupIdentity group = definition.RecGroup;
                if (group.StartTypeIndex != i)
                {
                    throw new WatException(
                        $"Recursion group {group.Index} starts at type {group.StartTypeIndex}, " +
                        $"but its first encountered member is type {i}.");
                }

                writer.WriteLine("  (rec");
                for (int member = 0; member < group.Count; member++)
                    EmitTypeDefinition(writer, module, i + member, ctx, indent: 2);
                writer.WriteLine("  )");
                i += group.Count;
                continue;
            }

            EmitTypeDefinition(writer, module, i, ctx, indent: 1);
            i++;
        }
    }

    private static void EmitTypeDefinition(
        WatWriter writer,
        WasmModule module,
        int typeIndex,
        DisassemblyContext context,
        int indent)
    {
        Indent(writer, indent);
        string name;
        if (!context.TypeNames.TryGetValue(typeIndex, out string? typeName))
        {
            name = $" (;{typeIndex};)";
        }
        else if (typeName.Length == 0)
        {
            name = """ (@name "")""";
        }
        else
        {
            string binding = context.TypeBindingNames[typeIndex];
            name = binding == typeName
                ? $" {FormatIdentifier(binding)}"
                : $" {FormatIdentifier(binding)} (@name \"{Escape(typeName)}\")";
        }
        writer.Write($"(type{name} ");

        if (context.ExactTypeDefinitions.TryGetValue(typeIndex, out TypeDefinition? definition))
        {
            writer.Write(TypeDefinitionText(definition, context));
        }
        else
        {
            // A programmatically-built module can carry only FuncType. In that model the compact
            // bytes are the entire declaration, so their nullable abstract meanings are exact.
            FuncType compact = module.Types[typeIndex];
            writer.Write(FunctionTypeText(
                compact.Parameters.Select(ExactValueType.FromStorageValType).ToArray(),
                compact.Results.Select(ExactValueType.FromStorageValType).ToArray(),
                context));
        }

        writer.WriteLine(")");
    }

    private static string TypeDefinitionText(
        TypeDefinition definition,
        DisassemblyContext context)
    {
        string composite = CompositeTypeText(definition.CompositeType, context);
        if (definition.IsFinal && definition.SuperTypeIndices.Count == 0)
            return composite;

        var text = new StringBuilder("(sub");
        if (definition.IsFinal)
            text.Append(" final");
        foreach (int superTypeIndex in definition.SuperTypeIndices)
            text.Append(' ').Append(TypeReferenceText(superTypeIndex, context));
        return text.Append(' ').Append(composite).Append(')').ToString();
    }

    private static string CompositeTypeText(
        CompositeType composite,
        DisassemblyContext context)
    {
        return composite switch
        {
            CompositeType.Function function =>
                FunctionTypeText(function.Parameters, function.Results, context),
            CompositeType.Struct structure =>
                $"(struct{string.Concat(structure.Fields.Select(
                    field => $" (field {FieldTypeText(field, context)})"))})",
            CompositeType.Array array =>
                $"(array {FieldTypeText(array.Field, context)})",
            CompositeType.Continuation continuation =>
                $"(cont {TypeReferenceText(continuation.FunctionTypeIndex, context)})",
            _ => throw new NotSupportedException(
                $"The WAT disassembler cannot represent composite type '{composite.GetType().Name}'."),
        };
    }

    private static string FunctionTypeText(
        IReadOnlyList<ExactValueType> parameters,
        IReadOnlyList<ExactValueType> results,
        DisassemblyContext context)
    {
        var text = new StringBuilder("(func");
        AppendValueTypeGroup(text, "param", parameters, context);
        AppendValueTypeGroup(text, "result", results, context);
        return text.Append(')').ToString();
    }

    private static void AppendValueTypeGroup(
        StringBuilder text,
        string keyword,
        IReadOnlyList<ExactValueType> types,
        DisassemblyContext context)
    {
        if (types.Count == 0)
            return;

        text.Append(" (").Append(keyword);
        foreach (ExactValueType type in types)
            text.Append(' ').Append(ExactValueTypeText(type, context));
        text.Append(')');
    }

    private static string FieldTypeText(FieldType field, DisassemblyContext context)
    {
        string storageType = field.StorageType switch
        {
            StorageType.Value value => ExactValueTypeText(value.Type, context),
            StorageType.Packed { Type: PackedStorageType.I8 } => "i8",
            StorageType.Packed { Type: PackedStorageType.I16 } => "i16",
            _ => throw new NotSupportedException(
                $"The WAT disassembler cannot represent storage type " +
                $"'{field.StorageType.GetType().Name}'."),
        };
        return field.Mutability == Mutability.Var ? $"(mut {storageType})" : storageType;
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
                    var name = ctx.FuncNames.TryGetValue(funcIdx, out var n)
                        ? $" {FormatDefName(n)}" : "";
                    writer.Write($"(func{name} (type {f.TypeIndex})");
                    var ft = ExactFunctionType(module, f.TypeIndex, ctx);
                    ctx.LocalNames.TryGetValue(funcIdx, out var localNames);
                    EmitParameters(writer, ft.Parameters, localNames, ctx);
                    EmitResults(writer, ft.Results, ctx);
                    writer.Write(')');
                    funcIdx++;
                    break;
                case ImportDescriptor.Table t:
                    writer.Write(
                        $"(table (;{tableIdx};) {FormatLimits(t.Type.Limits)} " +
                        $"{ReferenceTypeText(ExactTableElementType(t.Type), ctx)})");
                    tableIdx++;
                    break;
                case ImportDescriptor.Memory m:
                    writer.Write($"(memory (;{memIdx};) {FormatLimits(m.Type.Limits)})");
                    memIdx++;
                    break;
                case ImportDescriptor.Global g:
                    writer.Write($"(global (;{globalIdx};) {FormatGlobalType(g.Type, ctx)})");
                    globalIdx++;
                    break;
                case ImportDescriptor.Tag tag:
                    _ = ExactFunctionType(module, tag.TypeIndex, ctx);
                    writer.Write($"(tag (type {tag.TypeIndex}))");
                    break;
            }
            writer.WriteLine(")");
        }
    }

    private static void EmitParameters(
        WatWriter writer,
        IReadOnlyList<ExactValueType> parameters,
        IReadOnlyDictionary<int, string>? localNames,
        DisassemblyContext context)
    {
        if (parameters.Count == 0)
            return;

        bool hasParamNames = localNames?.Keys.Any(idx => idx >= 0 && idx < parameters.Count) == true;
        if (!hasParamNames)
        {
            writer.Write(" (param");
            foreach (var parameter in parameters)
                writer.Write($" {ExactValueTypeText(parameter, context)}");
            writer.Write(')');
            return;
        }

        for (int i = 0; i < parameters.Count; i++)
        {
            writer.Write(" (param");
            if (localNames!.TryGetValue(i, out var name))
                writer.Write($" {FormatDefName(name)}");
            writer.Write($" {ExactValueTypeText(parameters[i], context)})");
        }
    }

    private static void EmitResults(
        WatWriter writer,
        IReadOnlyList<ExactValueType> results,
        DisassemblyContext context)
    {
        if (results.Count == 0)
            return;

        writer.Write(" (result");
        foreach (ExactValueType result in results)
            writer.Write($" {ExactValueTypeText(result, context)}");
        writer.Write(')');
    }

    private static void EmitLocals(
        WatWriter writer,
        IReadOnlyList<ExactValueType> locals,
        int parameterCount,
        IReadOnlyDictionary<int, string>? localNames,
        DisassemblyContext context,
        int indent)
    {
        if (locals.Count == 0)
            return;

        bool hasLocalNames = localNames?.Keys.Any(
            idx => idx >= parameterCount && idx < parameterCount + locals.Count) == true;
        if (hasLocalNames)
        {
            for (int i = 0; i < locals.Count; i++)
            {
                Indent(writer, indent);
                writer.Write("(local");
                if (localNames!.TryGetValue(parameterCount + i, out var name))
                    writer.Write($" {FormatDefName(name)}");
                writer.WriteLine($" {ExactValueTypeText(locals[i], context)})");
            }
            return;
        }

        // Group consecutive unnamed locals of the same type.
        int localIndex = 0;
        while (localIndex < locals.Count)
        {
            var localType = locals[localIndex];
            int count = 1;
            while (localIndex + count < locals.Count && locals[localIndex + count] == localType)
                count++;
            Indent(writer, indent);
            writer.Write("(local");
            for (int i = 0; i < count; i++)
                writer.Write($" {ExactValueTypeText(localType, context)}");
            writer.WriteLine(")");
            localIndex += count;
        }
    }

    private static void EmitFunctions(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Functions.Count; i++)
        {
            var func = module.Functions[i];
            int absIdx = ctx.ImportedFuncCount + i;
            var funcName = ctx.FuncNames.TryGetValue(absIdx, out var n)
                ? $" {FormatDefName(n)}" : "";
            var ft = ExactFunctionType(module, func.TypeIndex, ctx);

            writer.Write($"  (func{funcName} (type {func.TypeIndex})");

            // Params
            ctx.LocalNames.TryGetValue(absIdx, out var localNames);
            EmitParameters(writer, ft.Parameters, localNames, ctx);
            // Results
            EmitResults(writer, ft.Results, ctx);
            writer.WriteLine();

            // Locals
            EmitLocals(
                writer,
                ExactLocals(func),
                ft.Parameters.Count,
                localNames,
                ctx,
                indent: 2);

            // Body instructions
            DisassembleBody(writer, func.Body, func.BranchHints, ctx, absIdx);

            writer.WriteLine("  )");
        }
    }

    private static CompositeType.Function ExactFunctionType(
        WasmModule module,
        int typeIndex,
        DisassemblyContext context)
    {
        if ((uint)typeIndex >= (uint)module.Types.Count)
            throw new WatException($"Function refers to type index {typeIndex}, which is out of range.");

        if (context.ExactTypeDefinitions.TryGetValue(typeIndex, out TypeDefinition? definition))
        {
            return definition.CompositeType as CompositeType.Function ??
                throw new WatException(
                    $"Function refers to type index {typeIndex}, whose exact definition is " +
                    $"{definition.CompositeType.GetType().Name}, not a function type.");
        }

        FuncType compact = module.Types[typeIndex];
        return new CompositeType.Function(
            compact.Parameters.Select(ExactValueType.FromStorageValType).ToArray(),
            compact.Results.Select(ExactValueType.FromStorageValType).ToArray());
    }

    private static IReadOnlyList<ExactValueType> ExactLocals(WasmFunction function)
    {
        if (function.ExactLocals.Count == function.Locals.Count)
            return function.ExactLocals;

        if (function.ExactLocals.Count != 0)
        {
            throw new WatException(
                $"Function has {function.Locals.Count} compact locals but " +
                $"{function.ExactLocals.Count} exact locals; refusing a partial exact rendering.");
        }

        return function.Locals.Select(ExactValueType.FromStorageValType).ToArray();
    }

    /// <remarks>
    /// Table initializers and exact reference types are both part of the table declaration. Omitting
    /// either produces valid-looking text for a different module.
    /// </remarks>
    private static void EmitTables(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Tables.Count; i++)
        {
            int absIdx = ctx.ImportedTableCount + i;
            var t = module.Tables[i];
            writer.Write(
                $"  (table (;{absIdx};) {FormatLimits(t.Limits)} " +
                $"{ReferenceTypeText(ExactTableElementType(t), ctx)}");
            if (t.InitExpr.Length > 0)
            {
                writer.Write(" (");
                DisassembleInitExpr(writer, t.InitExpr, ctx, $"table {absIdx} initializer");
                writer.Write(')');
            }
            writer.WriteLine(")");
        }
    }

    private static ExactValueType.Reference ExactTableElementType(TableType tableType)
    {
        if (tableType.ExactElementType is ExactValueType.Reference exact)
            return exact;
        if (tableType.ExactElementType is not null)
        {
            throw new WatException(
                $"Table exact element type '{tableType.ExactElementType.Key}' is not a reference type.");
        }

        return new ExactValueType.Reference(
            Nullability.Nullable,
            new AbstractHeapType(
                tableType.ElementType == RefType.ExternRef
                    ? HeapTypeKind.Extern
                    : HeapTypeKind.Func));
    }

    private static bool IsNullableAbstractFunc(Microsoft.NET.WebAssembly.Types.ValueType type) =>
        type is Microsoft.NET.WebAssembly.Types.ValueType.Reference
        {
            Nullability: Nullability.Nullable,
            HeapType: AbstractHeapType { Kind: HeapTypeKind.Func },
        };

    private static string ReferenceTypeText(
        ExactValueType type,
        DisassemblyContext context)
    {
        if (type is not Microsoft.NET.WebAssembly.Types.ValueType.Reference reference)
            throw new WatException($"Element type '{type.Key}' is not a reference type.");

        return reference.HeapType switch
        {
            DefinedHeapType defined =>
                reference.Nullability == Nullability.Nullable
                    ? $"(ref null {TypeReferenceText(defined.TypeIndex, context)})"
                    : $"(ref {TypeReferenceText(defined.TypeIndex, context)})",
            AbstractHeapType abstractType =>
                reference.Nullability == Nullability.Nullable
                    ? AbstractRefTypeName(abstractType.Kind)
                    : $"(ref {HeapTypeName(abstractType.Kind)})",
            RecursionVariableHeapType recursionVariable =>
                reference.Nullability == Nullability.Nullable
                    ? $"(ref null {RecursionVariableTypeReferenceText(recursionVariable, context)})"
                    : $"(ref {RecursionVariableTypeReferenceText(recursionVariable, context)})",
            _ => throw new NotSupportedException(
                $"The WAT disassembler cannot represent heap type '{reference.HeapType.Key}'."),
        };
    }

    private static string ExactValueTypeText(
        ExactValueType type,
        DisassemblyContext context) =>
        type switch
        {
            ExactValueType.Number { Type: NumberType.I32 } => "i32",
            ExactValueType.Number { Type: NumberType.I64 } => "i64",
            ExactValueType.Number { Type: NumberType.F32 } => "f32",
            ExactValueType.Number { Type: NumberType.F64 } => "f64",
            ExactValueType.Vector { Type: VectorType.V128 } => "v128",
            ExactValueType.Reference => ReferenceTypeText(type, context),
            _ => throw new NotSupportedException(
                $"The WAT disassembler cannot represent exact value type '{type.Key}'."),
        };

    private static string RecursionVariableTypeReferenceText(
        RecursionVariableHeapType recursionVariable,
        DisassemblyContext context)
    {
        if (!context.RecGroups.TryGetValue(
                recursionVariable.RecGroupIndex,
                out RecGroupIdentity? group))
        {
            throw new WatException(
                $"Recursion variable {recursionVariable.Key} refers to unknown recursion group " +
                $"{recursionVariable.RecGroupIndex}.");
        }
        if ((uint)recursionVariable.MemberIndex >= (uint)group.Count)
        {
            throw new WatException(
                $"Recursion variable {recursionVariable.Key} refers to member " +
                $"{recursionVariable.MemberIndex} of {group.Count}-member recursion group " +
                $"{group.Index}.");
        }

        return TypeReferenceText(group.StartTypeIndex + recursionVariable.MemberIndex, context);
    }

    private static string TypeReferenceText(int typeIndex, DisassemblyContext context)
    {
        if ((uint)typeIndex >= (uint)context.Module.Types.Count)
        {
            throw new WatException(
                $"Exact type refers to type index {typeIndex}, which is outside the module's " +
                $"{context.Module.Types.Count}-entry type index space.");
        }

        return context.TypeBindingNames.TryGetValue(typeIndex, out string? name)
            ? FormatIdentifier(name)
            : typeIndex.ToString(CultureInfo.InvariantCulture);
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
            writer.Write($"  (global (;{absIdx};) {FormatGlobalType(g.Type, ctx)} (");
            DisassembleInitExpr(writer, g.InitExpr, ctx, $"global {absIdx} initializer");
            writer.WriteLine("))");
        }
    }

    private static void EmitTags(WatWriter writer, WasmModule module, DisassemblyContext context)
    {
        for (int i = 0; i < module.Tags.Count; i++)
        {
            int absoluteIndex = context.ImportedTagCount + i;
            int typeIndex = module.Tags[i].TypeIndex;
            _ = ExactFunctionType(module, typeIndex, context);
            writer.WriteLine(
                $"  (tag (;{absoluteIndex};) " +
                $"(type {typeIndex}))");
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
                    var fn = ctx.FuncNames.TryGetValue(f.FunctionIndex, out var n)
                        ? FormatRefName(n, f.FunctionIndex) : f.FunctionIndex.ToString();
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
            var fn = ctx.FuncNames.TryGetValue(module.StartFunction.Value, out var n)
                ? FormatRefName(n, module.StartFunction.Value) : module.StartFunction.Value.ToString();
            writer.WriteLine($"  (start {fn})");
        }
    }

    private static void EmitElements(WatWriter writer, WasmModule module, DisassemblyContext ctx)
    {
        for (int i = 0; i < module.Elements.Count; i++)
        {
            var element = module.Elements[i];
            writer.Write($"  (elem (;{i};) ");

            switch (element.Mode)
            {
                case ElementMode.Active:
                {
                    if (element.TableIndex < 0)
                        throw new WatException($"Element segment {i} is active but has no table index.");
                    if (element.HasExplicitTableIndex || element.TableIndex != 0)
                        writer.Write($"(table {element.TableIndex}) ");
                    if (element.OffsetExpr.Length == 0)
                        throw new WatException($"Element segment {i} is active but has no offset expression.");
                    writer.Write('(');
                    DisassembleInitExpr(writer, element.OffsetExpr, ctx, $"element segment {i} offset");
                    writer.Write(") ");
                    break;
                }
                case ElementMode.Passive:
                    break;
                case ElementMode.Declarative:
                    writer.Write("declare ");
                    break;
                default:
                    throw new WatException($"Element segment {i} has unknown mode {element.Mode}.");
            }

            if (element.UsesExpressions)
            {
                if (element.Mode == ElementMode.Active &&
                    !element.HasExplicitTableIndex &&
                    element.TableIndex == 0 &&
                    !IsNullableAbstractFunc(element.ElementType))
                {
                    throw new WatException(
                        $"Element segment {i} uses the implicit-table expression encoding (flag 4) " +
                        $"but has element type '{element.ElementType.Key}', which flag 4 cannot encode.");
                }

                writer.Write(ReferenceTypeText(element.ElementType, ctx));
                for (int expressionIndex = 0;
                     expressionIndex < element.ElementExpressions.Count;
                     expressionIndex++)
                {
                    writer.Write(" (");
                    DisassembleInitExpr(
                        writer,
                        element.ElementExpressions[expressionIndex],
                        ctx,
                        $"element segment {i} expression {expressionIndex}");
                    writer.Write(')');
                }
                writer.WriteLine(")");
                continue;
            }

            writer.Write("func");
            IReadOnlyList<int> functionIndices =
                element.Mode == ElementMode.Declarative
                    ? element.DeclaredFunctionIndices
                    : element.FunctionIndices;
            foreach (int functionIndex in functionIndices)
            {
                if (functionIndex < 0)
                {
                    throw new WatException(
                        $"Element segment {i} funcidx entry is negative ({functionIndex}); " +
                        "it cannot be represented by the funcidx text form.");
                }

                string function = ctx.FuncNames.TryGetValue(functionIndex, out string? name)
                    ? FormatRefName(name, functionIndex)
                    : functionIndex.ToString(CultureInfo.InvariantCulture);
                writer.Write($" {function}");
            }
            writer.WriteLine(")");
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
                DisassembleInitExpr(writer, data.OffsetExpr, ctx, $"data segment {i} offset");
                writer.Write($") \"{EscapeDataString(data.InitData)}\")");
            }
            writer.WriteLine();
        }
    }

    // ── Init expression disassembly ──────────────────────────────────────

    private static void DisassembleInitExpr(
        WatWriter writer,
        byte[] expression,
        DisassemblyContext context,
        string site)
    {
        writer.Write(FormatConstantExpression(expression, context, site));
    }

    private static string FormatConstantExpression(
        byte[] expression,
        DisassemblyContext context,
        string site)
    {
        var values = new List<string>();
        var walker = new ConstExpressionWalker(expression);
        try
        {
            while (walker.MoveNext(out ConstOp op, out var instruction))
            {
                switch (op)
                {
                    case ConstOp.End:
                        if (walker.Position != expression.Length)
                            throw new WatException(
                                $"Cannot disassemble {site}: bytes follow the constant-expression end.");
                        if (values.Count != 1)
                        {
                            throw new WatException(
                                $"Cannot disassemble {site}: constant expression leaves {values.Count} " +
                                "values; WAT requires exactly one.");
                        }
                        return values[0];
                    case ConstOp.GlobalGet:
                        values.Add($"global.get {instruction.Index0}");
                        break;
                    case ConstOp.I32Const:
                        values.Add($"i32.const {unchecked((int)instruction.Index0)}");
                        break;
                    case ConstOp.I64Const:
                        values.Add($"i64.const {unchecked((long)instruction.MemOffset)}");
                        break;
                    case ConstOp.F32Const:
                        values.Add(
                            $"f32.const {FormatFloat(BitConverter.UInt32BitsToSingle(instruction.Index0))}");
                        break;
                    case ConstOp.F64Const:
                        values.Add(
                            $"f64.const {FormatDouble(BitConverter.UInt64BitsToDouble(instruction.MemOffset))}");
                        break;
                    case ConstOp.I32Add:
                        FoldBinary(values, "i32.add", site);
                        break;
                    case ConstOp.I32Sub:
                        FoldBinary(values, "i32.sub", site);
                        break;
                    case ConstOp.I32Mul:
                        FoldBinary(values, "i32.mul", site);
                        break;
                    case ConstOp.I64Add:
                        FoldBinary(values, "i64.add", site);
                        break;
                    case ConstOp.I64Sub:
                        FoldBinary(values, "i64.sub", site);
                        break;
                    case ConstOp.I64Mul:
                        FoldBinary(values, "i64.mul", site);
                        break;
                    case ConstOp.RefNull:
                        values.Add($"ref.null {HeapTypeText(instruction.HeapType0, context)}");
                        break;
                    case ConstOp.RefFunc:
                    {
                        int functionIndex = checked((int)instruction.Index0);
                        string function = context.FuncNames.TryGetValue(functionIndex, out string? name)
                            ? FormatRefName(name, functionIndex)
                            : functionIndex.ToString(CultureInfo.InvariantCulture);
                        values.Add($"ref.func {function}");
                        break;
                    }
                    case ConstOp.V128Const:
                    {
                        ReadOnlySpan<byte> bytes = instruction.ImmediateBytes[^16..];
                        var text = new StringBuilder("v128.const i32x4");
                        for (int lane = 0; lane < 4; lane++)
                        {
                            uint value = BinaryPrimitives.ReadUInt32LittleEndian(bytes[(lane * 4)..]);
                            text.Append(CultureInfo.InvariantCulture, $" 0x{value:x8}");
                        }
                        values.Add(text.ToString());
                        break;
                    }
                    case ConstOp.StructNew:
                    case ConstOp.StructNewDefault:
                    case ConstOp.ArrayNew:
                    case ConstOp.ArrayNewDefault:
                    case ConstOp.ArrayNewFixed:
                    case ConstOp.AnyConvertExtern:
                    case ConstOp.ExternConvertAny:
                    case ConstOp.RefI31:
                    {
                        string name = GcConstantExpressionName(op);
                        throw new NotSupportedException(
                            $"Cannot disassemble {site}: {name} ({instruction.OpcodeId}) is a valid " +
                            "binary constant-expression instruction, but nesm's WAT compiler does not " +
                            "support GC instructions. Refusing to emit text that cannot round-trip.");
                    }
                    default:
                        throw new WatException(
                            $"Cannot disassemble {site}: opcode {instruction.OpcodeId} is not supported " +
                            "in a constant expression; refusing to emit lossy text.");
                }
            }
        }
        catch (WasmFormatException ex)
        {
            throw new WatException(
                $"Cannot disassemble {site}: malformed constant expression: {ex.Message}", ex);
        }

        throw new WatException(
            $"Cannot disassemble {site}: constant expression is missing an end opcode.");
    }

    private static void FoldBinary(List<string> values, string mnemonic, string site)
    {
        if (values.Count < 2)
        {
            throw new WatException(
                $"Cannot disassemble {site}: {mnemonic} has fewer than two operands in the " +
                "constant expression.");
        }

        string right = values[^1];
        string left = values[^2];
        values.RemoveRange(values.Count - 2, 2);
        values.Add($"{mnemonic} ({left}) ({right})");
    }

    private static string GcConstantExpressionName(ConstOp op) => op switch
    {
        ConstOp.StructNew => "struct.new",
        ConstOp.StructNewDefault => "struct.new_default",
        ConstOp.ArrayNew => "array.new",
        ConstOp.ArrayNewDefault => "array.new_default",
        ConstOp.ArrayNewFixed => "array.new_fixed",
        ConstOp.AnyConvertExtern => "any.convert_extern",
        ConstOp.ExternConvertAny => "extern.convert_any",
        ConstOp.RefI31 => "ref.i31",
        _ => op.ToString(),
    };

    // ── Instruction body disassembly ─────────────────────────────────────

    private static void DisassembleBody(
        WatWriter writer,
        byte[] body,
        IReadOnlyDictionary<int, BranchHint> branchHints,
        DisassemblyContext ctx,
        int funcIdx,
        int startInstruction = 0,
        int endInstructionExclusive = int.MaxValue,
        bool inspectionMode = false)
    {
        var instructions = DecodeFunctionBodyInstructions(body);
        int indent = 2; // base indent for function body
        var blockStack = new Stack<string>();
        var sink = new WatWriter(TextWriter.Null);

        for (int instructionOrdinal = 0; instructionOrdinal < instructions.Count; instructionOrdinal++)
        {
            var output = instructionOrdinal >= startInstruction && instructionOrdinal < endInstructionExclusive
                ? writer
                : sink;
            RenderBodyInstruction(
                output,
                body,
                instructions[instructionOrdinal],
                instructionOrdinal,
                branchHints,
                ctx,
                funcIdx,
                ref indent,
                blockStack,
                inspectionMode);
        }
    }

    private static FunctionBodyWindow DisassembleBodyWindow(
        byte[] body,
        IReadOnlyDictionary<int, BranchHint> branchHints,
        DisassemblyContext ctx,
        int funcIdx,
        IReadOnlyList<InstructionBoundary> instructions,
        int startInstruction,
        int endInstructionExclusive,
        int maxCharacters,
        int previousInstructionLimit,
        int? requiredInstruction,
        Func<string, int> measureCharacters)
    {
        int indent = 2;
        var blockStack = new Stack<string>();
        var sink = new WatWriter(TextWriter.Null);
        int measurementStart = Math.Max(
            startInstruction - previousInstructionLimit,
            0);
        var measuredInstructions = new List<(int Pc, string Text, int Characters, bool Oversized)>(
            endInstructionExclusive - measurementStart);

        for (int instructionOrdinal = 0;
             instructionOrdinal < endInstructionExclusive;
             instructionOrdinal++)
        {
            InstructionBoundary instruction = instructions[instructionOrdinal];
            if (instructionOrdinal < measurementStart)
            {
                RenderBodyInstruction(
                    sink,
                    body,
                    instruction,
                    instructionOrdinal,
                    branchHints,
                    ctx,
                    funcIdx,
                    ref indent,
                    blockStack,
                    inspectionMode: true);
                continue;
            }

            using var measuredText = new CharacterLimitTextWriter(maxCharacters);
            using var measuredWriter = new WatWriter(measuredText);
            RenderBodyInstruction(
                measuredWriter,
                body,
                instruction,
                instructionOrdinal,
                branchHints,
                ctx,
                funcIdx,
                ref indent,
                blockStack,
                inspectionMode: true);
            measuredWriter.Flush();
            string text = measuredText.ToString();
            int characters = measureCharacters(text);
            measuredInstructions.Add((
                instructionOrdinal,
                text,
                characters,
                measuredText.Exceeded || characters > maxCharacters));
        }

        (int Pc, string Text, int Characters, bool Oversized) Measured(int pc)
            => measuredInstructions[pc - measurementStart];

        var preview = new StringBuilder(Math.Min(maxCharacters, 1024));
        int previewCharacters = 0;
        int renderedInstructions = 0;
        int coveredInstructionStart = startInstruction;
        int coveredInstructionEnd = startInstruction;
        OmittedInstruction? omitted = null;

        if (requiredInstruction is int anchorPc)
        {
            var anchor = Measured(anchorPc);
            coveredInstructionStart = anchorPc;
            coveredInstructionEnd = anchorPc + 1;

            if (anchor.Oversized)
            {
                InstructionBoundary instruction = instructions[anchorPc];
                omitted = new OmittedInstruction(
                    anchorPc,
                    instruction.OpcodeId,
                    instruction.ByteOffset,
                    instruction.EndOffset,
                    $"Rendered instruction exceeds the {maxCharacters}-character preview budget.");
            }
            else
            {
                int selectedCharacters = anchor.Characters;
                bool precedingOpen = true;
                bool followingOpen = true;
                int distance = 1;

                while (precedingOpen || followingOpen)
                {
                    int precedingPc = anchorPc - distance;
                    if (precedingOpen && precedingPc >= startInstruction)
                    {
                        var preceding = Measured(precedingPc);
                        if (!preceding.Oversized &&
                            selectedCharacters + preceding.Characters <= maxCharacters)
                        {
                            coveredInstructionStart = precedingPc;
                            selectedCharacters += preceding.Characters;
                        }
                        else
                        {
                            precedingOpen = false;
                        }
                    }
                    else
                    {
                        precedingOpen = false;
                    }

                    int followingPc = anchorPc + distance;
                    if (followingOpen && followingPc < endInstructionExclusive)
                    {
                        var following = Measured(followingPc);
                        if (!following.Oversized &&
                            selectedCharacters + following.Characters <= maxCharacters)
                        {
                            coveredInstructionEnd = followingPc + 1;
                            selectedCharacters += following.Characters;
                        }
                        else
                        {
                            followingOpen = false;
                        }
                    }
                    else
                    {
                        followingOpen = false;
                    }

                    distance++;
                }

                for (int pc = coveredInstructionStart; pc < coveredInstructionEnd; pc++)
                    preview.Append(Measured(pc).Text);
                renderedInstructions = coveredInstructionEnd - coveredInstructionStart;
            }
        }
        else
        {
            for (int instructionOrdinal = startInstruction;
                 instructionOrdinal < endInstructionExclusive;
                 instructionOrdinal++)
            {
                var measured = Measured(instructionOrdinal);
                if (measured.Oversized)
                {
                    InstructionBoundary instruction = instructions[instructionOrdinal];
                    omitted = new OmittedInstruction(
                        instructionOrdinal,
                        instruction.OpcodeId,
                        instruction.ByteOffset,
                        instruction.EndOffset,
                        $"Rendered instruction exceeds the {maxCharacters}-character preview budget.");
                    coveredInstructionEnd = instructionOrdinal + 1;
                    break;
                }

                if (previewCharacters + measured.Characters > maxCharacters)
                {
                    coveredInstructionEnd = instructionOrdinal;
                    break;
                }

                preview.Append(measured.Text);
                previewCharacters += measured.Characters;
                renderedInstructions++;
                coveredInstructionEnd = instructionOrdinal + 1;
            }
        }

        int? previousInstructionStart = null;
        int previousCharacters = 0;
        int previousCandidateStart = Math.Max(
            coveredInstructionStart - previousInstructionLimit,
            0);
        for (int pc = coveredInstructionStart - 1; pc >= previousCandidateStart; pc--)
        {
            var previous = Measured(pc);
            if (previous.Oversized)
            {
                previousInstructionStart ??= pc;
                break;
            }
            if (previousCharacters + previous.Characters > maxCharacters)
                break;

            previousCharacters += previous.Characters;
            previousInstructionStart = pc;
        }

        return new FunctionBodyWindow(
            preview.ToString(),
            renderedInstructions,
            coveredInstructionStart,
            coveredInstructionEnd,
            omitted,
            previousInstructionStart);
    }

    private static void RenderBodyInstruction(
        WatWriter output,
        byte[] body,
        InstructionBoundary instruction,
        int instructionOrdinal,
        IReadOnlyDictionary<int, BranchHint> branchHints,
        DisassemblyContext ctx,
        int funcIdx,
        ref int indent,
        Stack<string> blockStack,
        bool inspectionMode)
    {
        byte opByte = instruction.OpcodeId.Opcode;
        int pos = instruction.ImmediateOffset;

        if (branchHints.TryGetValue(instructionOrdinal, out BranchHint? branchHint))
        {
            if (opByte is not WasmOpcode.If and not WasmOpcode.BrIf)
            {
                throw new WatException(
                    $"Branch hint at instruction {instructionOrdinal} targets opcode " +
                    $"0x{opByte:X2}, not if or br_if.");
            }
            Indent(output, indent);
            output.WriteLine(
                $"(@metadata.code.branch_hint \"\\0{(byte)branchHint.Kind}\")");
        }

        // END — close the current block with )
        if (opByte == WasmOpcode.End)
        {
            var block = blockStack.Count > 0 ? blockStack.Pop() : "block";
            if (block == "if-then")
            {
                indent--;
                Indent(output, indent); output.WriteLine(")");
                indent--;
                Indent(output, indent); output.WriteLine(")");
            }
            else if (block == "if-else")
            {
                indent--;
                Indent(output, indent); output.WriteLine(")");
                indent--;
                Indent(output, indent); output.WriteLine(")");
            }
            else
            {
                indent--;
                Indent(output, indent); output.WriteLine(")");
            }
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // ELSE — close (then, open (else
        if (opByte == WasmOpcode.Else)
        {
            if (blockStack.Count > 0) blockStack.Pop();
            indent--;
            Indent(output, indent); output.WriteLine(")");
            Indent(output, indent); output.WriteLine("(else");
            indent++;
            blockStack.Push("if-else");
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // Block-opening: block, loop
        if (opByte is WasmOpcode.Block or WasmOpcode.Loop)
        {
            string mnemonic = opByte == WasmOpcode.Block ? "block" : "loop";
            Indent(output, indent);
            output.Write($"({mnemonic}");
            EmitBlockType(output, body, ref pos, ctx);
            output.WriteLine();
            indent++;
            blockStack.Push(mnemonic);
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // If
        if (opByte == WasmOpcode.If)
        {
            Indent(output, indent);
            output.Write("(if");
            EmitBlockType(output, body, ref pos, ctx);
            output.WriteLine();
            indent++;
            Indent(output, indent);
            output.WriteLine("(then");
            indent++;
            blockStack.Push("if-then");
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // try (legacy)
        if (opByte == WasmOpcode.Try)
        {
            Indent(output, indent);
            output.Write("(block");
            EmitBlockType(output, body, ref pos, ctx);
            output.WriteLine();
            indent++;
            blockStack.Push("block");
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // try_table
        if (opByte == WasmOpcode.TryTable)
        {
            Indent(output, indent);
            output.Write("(try_table");
            EmitTryTableImmediate(output, body, ref pos, ctx);
            output.WriteLine();
            indent++;
            blockStack.Push("try_table");
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        // select t*
        if (opByte == 0x1C)
        {
            Indent(output, indent);
            output.Write("select");
            EmitSelectTypes(output, body, ref pos, ctx);
            output.WriteLine();
            VerifyInstructionEnd(instruction, pos);
            return;
        }

        if (instruction.OpcodeId.IsPrefixed)
        {
            uint encodedSubOpcode = WasmLeb128.ReadU32(body, ref pos);
            if (encodedSubOpcode != instruction.OpcodeId.SubOpcode)
            {
                throw new WatException(
                    $"Instruction boundary recorded sub-opcode 0x{instruction.OpcodeId.SubOpcode:X}, " +
                    $"but body bytes encode 0x{encodedSubOpcode:X}.");
            }
            uint key = ((uint)opByte << 24) | instruction.OpcodeId.SubOpcode;
            if (ctx.PrefixedOpcodes.TryGetValue(key, out var prefixedEntry))
            {
                Indent(output, indent);
                output.Write(prefixedEntry.Mnemonic);
                EmitImmediate(output, body, ref pos, prefixedEntry.Imm, prefixedEntry.Mnemonic, ctx, funcIdx);
                output.WriteLine();
                VerifyInstructionEnd(instruction, pos);
                return;
            }

            if (opByte == WasmOpcode.GcPrefix && inspectionMode)
            {
                Indent(output, indent);
                EmitGcInstruction(output, body, ref pos, instruction.OpcodeId.SubOpcode, ctx);
                output.WriteLine();
                VerifyInstructionEnd(instruction, pos);
                return;
            }

            throw new WatException(
                $"Cannot disassemble instruction {instructionOrdinal} at body bytes " +
                $"[{instruction.ByteOffset}, {instruction.EndOffset}): unsupported opcode " +
                $"{instruction.OpcodeId}.");
        }

        if (!ctx.SingleByteOpcodes.TryGetValue(opByte, out var entry))
        {
            throw new WatException(
                $"Cannot disassemble instruction {instructionOrdinal} at body bytes " +
                $"[{instruction.ByteOffset}, {instruction.EndOffset}): unsupported opcode " +
                $"{instruction.OpcodeId}.");
        }

        Indent(output, indent);
        output.Write(entry.Mnemonic);
        EmitImmediate(output, body, ref pos, entry.Imm, entry.Mnemonic, ctx, funcIdx);
        output.WriteLine();
        VerifyInstructionEnd(instruction, pos);
    }

    private static void VerifyInstructionEnd(InstructionBoundary instruction, int renderedEnd)
    {
        if (renderedEnd != instruction.EndOffset)
        {
            throw new WatException(
                $"Instruction renderer for {instruction.OpcodeId} consumed body bytes " +
                $"[{instruction.ByteOffset}, {renderedEnd}), but the binary decoder selected " +
                $"[{instruction.ByteOffset}, {instruction.EndOffset}).");
        }
    }

    private static void EmitSelectTypes(
        WatWriter writer,
        byte[] body,
        ref int pos,
        DisassemblyContext context)
    {
        uint count = WasmLeb128.ReadU32(body, ref pos);
        writer.Write(" (result");
        for (uint i = 0; i < count; i++)
        {
            byte valueType = body[pos++];
            if (valueType is 0x63 or 0x64)
            {
                long encodedHeapType = WasmLeb128.ReadS33(body, ref pos);
                var exact = new ExactValueType.Reference(
                    valueType == 0x63 ? Nullability.Nullable : Nullability.NonNullable,
                    ExactHeapType(encodedHeapType));
                writer.Write($" {ExactValueTypeText(exact, context)}");
            }
            else if (IsSingleByteValType(valueType))
            {
                writer.Write(
                    $" {ExactValueTypeText(ExactValueType.FromStorageValType((ValType)valueType), context)}");
            }
            else
            {
                throw new WatException($"Invalid select value type 0x{valueType:X2}.");
            }
        }
        writer.Write(')');
    }

    private static void EmitGcInstruction(
        WatWriter writer,
        byte[] body,
        ref int pos,
        uint subOpcode,
        DisassemblyContext context)
    {
        string mnemonic = subOpcode switch
        {
            0x00 => "struct.new",
            0x01 => "struct.new_default",
            0x02 => "struct.get",
            0x03 => "struct.get_s",
            0x04 => "struct.get_u",
            0x05 => "struct.set",
            0x06 => "array.new",
            0x07 => "array.new_default",
            0x08 => "array.new_fixed",
            0x09 => "array.new_data",
            0x0A => "array.new_elem",
            0x0B => "array.get",
            0x0C => "array.get_s",
            0x0D => "array.get_u",
            0x0E => "array.set",
            0x0F => "array.len",
            0x10 => "array.fill",
            0x11 => "array.copy",
            0x12 => "array.init_data",
            0x13 => "array.init_elem",
            0x14 => "ref.test",
            0x15 => "ref.test",
            0x16 => "ref.cast",
            0x17 => "ref.cast",
            0x18 => "br_on_cast",
            0x19 => "br_on_cast_fail",
            0x1A => "any.convert_extern",
            0x1B => "extern.convert_any",
            0x1C => "ref.i31",
            0x1D => "i31.get_s",
            0x1E => "i31.get_u",
            _ => throw new WatException($"Unknown GC sub-opcode 0x{subOpcode:X}.")
        };
        writer.Write(mnemonic);

        switch (subOpcode)
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
                writer.Write($" {WasmLeb128.ReadU32(body, ref pos)}");
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
                writer.Write(
                    $" {WasmLeb128.ReadU32(body, ref pos)} {WasmLeb128.ReadU32(body, ref pos)}");
                break;
            case 0x14:
            case 0x15:
            case 0x16:
            case 0x17:
            {
                bool nullable = subOpcode is 0x15 or 0x17;
                string heapType = HeapTypeText(WasmLeb128.ReadS33(body, ref pos), context);
                writer.Write(nullable ? $" (ref null {heapType})" : $" (ref {heapType})");
                break;
            }
            case 0x18:
            case 0x19:
            {
                byte flags = body[pos++];
                uint label = WasmLeb128.ReadU32(body, ref pos);
                string sourceHeapType = HeapTypeText(WasmLeb128.ReadS33(body, ref pos), context);
                string targetHeapType = HeapTypeText(WasmLeb128.ReadS33(body, ref pos), context);
                string sourceNull = (flags & 0x01) != 0 ? " null" : "";
                string targetNull = (flags & 0x02) != 0 ? " null" : "";
                writer.Write(
                    $" {label} (ref{sourceNull} {sourceHeapType}) (ref{targetNull} {targetHeapType})");
                break;
            }
        }
    }

    private sealed class CharacterLimitTextWriter(int limit) : TextWriter
    {
        private readonly StringBuilder _text = new(Math.Min(limit, 1024));

        public override Encoding Encoding => Encoding.UTF8;
        public bool Exceeded { get; private set; }

        public override void Write(char value)
        {
            if (_text.Length < limit)
                _text.Append(value);
            else
                Exceeded = true;
        }

        public override void Write(string? value)
        {
            if (string.IsNullOrEmpty(value))
                return;

            int remaining = limit - _text.Length;
            if (value.Length <= remaining)
            {
                _text.Append(value);
                return;
            }

            if (remaining > 0)
                _text.Append(value.AsSpan(0, remaining));
            Exceeded = true;
        }

        public override void Write(ReadOnlySpan<char> buffer)
        {
            int remaining = limit - _text.Length;
            if (buffer.Length <= remaining)
            {
                _text.Append(buffer);
                return;
            }

            if (remaining > 0)
                _text.Append(buffer[..remaining]);
            Exceeded = true;
        }

        public override string ToString() => _text.ToString();
    }

    private sealed class Utf8CountingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
        public long ByteCount { get; private set; }

        public override void Write(char value)
        {
            Span<char> buffer = stackalloc char[1];
            buffer[0] = value;
            ByteCount += Encoding.UTF8.GetByteCount(buffer);
        }

        public override void Write(string? value)
        {
            if (value is not null)
                ByteCount += Encoding.UTF8.GetByteCount(value);
        }

        public override void Write(ReadOnlySpan<char> buffer) =>
            ByteCount += Encoding.UTF8.GetByteCount(buffer);
    }

    private static void EmitTryTableImmediate(
        WatWriter writer,
        byte[] body,
        ref int pos,
        DisassemblyContext context)
    {
        EmitBlockType(writer, body, ref pos, context);
        uint handlerCount = WasmLeb128.ReadU32(body, ref pos);
        for (uint i = 0; i < handlerCount; i++)
        {
            byte kind = body[pos++];
            switch (kind)
            {
                case 0:
                {
                    uint tagIdx = WasmLeb128.ReadU32(body, ref pos);
                    uint label = WasmLeb128.ReadU32(body, ref pos);
                    writer.Write($" (catch {tagIdx} {label})");
                    break;
                }
                case 1:
                {
                    uint tagIdx = WasmLeb128.ReadU32(body, ref pos);
                    uint label = WasmLeb128.ReadU32(body, ref pos);
                    writer.Write($" (catch_ref {tagIdx} {label})");
                    break;
                }
                case 2:
                {
                    uint label = WasmLeb128.ReadU32(body, ref pos);
                    writer.Write($" (catch_all {label})");
                    break;
                }
                case 3:
                {
                    uint label = WasmLeb128.ReadU32(body, ref pos);
                    writer.Write($" (catch_all_ref {label})");
                    break;
                }
            }
        }

    }

    private static void EmitImmediate(WatWriter writer, byte[] body, ref int pos,
        ImmediateKind imm, string mnemonic, DisassemblyContext ctx, int funcIdx)
    {
        switch (imm)
        {
            case ImmediateKind.None:
                break;

            case ImmediateKind.BlockType:
                EmitBlockType(writer, body, ref pos, ctx);
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
                uint idx = WasmLeb128.ReadU32(body, ref pos);
                if (imm == ImmediateKind.FuncIdx && ctx.FuncNames.TryGetValue((int)idx, out var n))
                    writer.Write($" {FormatRefName(n, (int)idx)}");
                else if (imm == ImmediateKind.LocalIdx
                    && ctx.LocalNames.TryGetValue(funcIdx, out var localNames)
                    && localNames.TryGetValue((int)idx, out var localName))
                    writer.Write($" {FormatRefName(localName, (int)idx)}");
                else
                    writer.Write($" {idx}");
                break;
            }

            case ImmediateKind.I32:
            {
                int val = WasmLeb128.ReadS32(body, ref pos);
                writer.Write($" {val}");
                break;
            }

            case ImmediateKind.I64:
            {
                long val = WasmLeb128.ReadS64(body, ref pos);
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
                uint align = WasmLeb128.ReadU32(body, ref pos);
                uint offset = WasmLeb128.ReadU32(body, ref pos);
                var natAlign = OpcodeTable.NaturalAlignment(mnemonic);
                if (offset != 0)
                    writer.Write($" offset={offset}");
                if (align != natAlign)
                    writer.Write($" align={1u << (int)align}");
                break;
            }

            case ImmediateKind.BrTable:
            {
                uint count = WasmLeb128.ReadU32(body, ref pos);
                for (uint i = 0; i < count; i++)
                {
                    uint label = WasmLeb128.ReadU32(body, ref pos);
                    writer.Write($" {label}");
                }
                uint defaultLabel = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {defaultLabel}");
                break;
            }

            case ImmediateKind.CallIndirect:
            {
                uint typeIdx = WasmLeb128.ReadU32(body, ref pos);
                uint tableIdx = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" (type {typeIdx})");
                if (tableIdx != 0)
                    writer.Write($" {tableIdx}");
                break;
            }

            case ImmediateKind.TypePair:
            {
                uint first = WasmLeb128.ReadU32(body, ref pos);
                uint second = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {first} {second}");
                break;
            }

            case ImmediateKind.TypeTagPair:
            {
                uint typeIdx = WasmLeb128.ReadU32(body, ref pos);
                uint tagIdx = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {typeIdx} {tagIdx}");
                break;
            }

            case ImmediateKind.ResumeTable:
            {
                uint typeIdx = WasmLeb128.ReadU32(body, ref pos);
                uint handlerCount = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {typeIdx}");
                for (uint i = 0; i < handlerCount; i++)
                {
                    uint tagIdx = WasmLeb128.ReadU32(body, ref pos);
                    uint labelIdx = WasmLeb128.ReadU32(body, ref pos);
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
                uint align = WasmLeb128.ReadU32(body, ref pos);
                uint offset = WasmLeb128.ReadU32(body, ref pos);
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
                // `ref.null` is the only instruction with this immediate, and it takes a heap type
                // encoded as an s33 — a defined type index when non-negative.
                writer.Write($" {HeapTypeText(WasmLeb128.ReadS33(body, ref pos), ctx)}");
                break;
            }

            case ImmediateKind.DataMemIdx:
            {
                uint dataIdx = WasmLeb128.ReadU32(body, ref pos);
                uint memIdx = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {dataIdx}");
                if (memIdx != 0) writer.Write($" {memIdx}");
                break;
            }

            case ImmediateKind.MemCopy:
            {
                uint dst = WasmLeb128.ReadU32(body, ref pos);
                uint src = WasmLeb128.ReadU32(body, ref pos);
                if (dst != 0 || src != 0)
                    writer.Write($" {dst} {src}");
                break;
            }

            case ImmediateKind.ElemTableIdx:
            {
                uint elemIdx = WasmLeb128.ReadU32(body, ref pos);
                uint tableIdx = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {elemIdx} {tableIdx}");
                break;
            }

            case ImmediateKind.TableCopy:
            {
                uint dst = WasmLeb128.ReadU32(body, ref pos);
                uint src = WasmLeb128.ReadU32(body, ref pos);
                writer.Write($" {dst} {src}");
                break;
            }

            case ImmediateKind.TryTable:
                // Handled by EmitTryTableImmediate in DisassembleBody
                break;
        }
    }

    private static void EmitBlockType(
        WatWriter writer,
        byte[] body,
        ref int pos,
        DisassemblyContext context)
    {
        byte b = body[pos];
        if (b == 0x40)
        {
            // void block — no output needed
            pos++;
        }
        else if (IsSingleByteValType(b))
        {
            // value type
            pos++;
            writer.Write(
                $" (result {ExactValueTypeText(ExactValueType.FromStorageValType((ValType)b), context)})");
        }
        else if (b is 0x63 or 0x64)
        {
            pos++;
            long encodedHeapType = WasmLeb128.ReadS33(body, ref pos);
            HeapType heapType = ExactHeapType(encodedHeapType);
            var exact = new ExactValueType.Reference(
                b == 0x63 ? Nullability.Nullable : Nullability.NonNullable,
                heapType);
            writer.Write($" (result {ExactValueTypeText(exact, context)})");
        }
        else
        {
            // type index (s33 LEB)
            long typeIdx = WasmLeb128.ReadS33(body, ref pos);
            if (typeIdx < 0 || typeIdx > int.MaxValue)
                throw new WatException($"Invalid block type index {typeIdx}.");
            writer.Write($" (type {TypeReferenceText((int)typeIdx, context)})");
        }
    }

    private static HeapType ExactHeapType(long encodedHeapType)
    {
        if (encodedHeapType >= 0)
            return new DefinedHeapType(checked((int)encodedHeapType));

        byte code = (byte)(encodedHeapType & 0x7F);
        if (AbstractHeapTypeCodes.TryFromCode(code, out HeapTypeKind kind))
            return new AbstractHeapType(kind);
        throw new WatException($"Unknown abstract heap-type code 0x{code:X2}.");
    }

    /// <summary>
    /// True when <paramref name="b"/> is a complete one-byte value type — a number/vector type, or
    /// the nullable shorthand for an abstract heap type.
    /// </summary>
    /// <remarks>
    /// The predicate this replaces was <c>b == 0x65 || (b &gt;= 0x6F &amp;&amp; b &lt;= 0x7F)</c>, a
    /// range that happens to cover <c>funcref</c>/<c>externref</c> and the numeric types and misses
    /// every other abstract heap type: <c>exnref</c> (0x69), <c>arrayref</c> (0x6A) …
    /// <c>i31ref</c> (0x6C), and the renumbered <c>contref</c> (0x68). A missed code is not a
    /// cosmetic problem — the byte was then handed to <c>ReadS33Leb128</c> and printed as a type
    /// index, so <c>(block (result exnref))</c> disassembled to <c>(type -23)</c>. Derived from
    /// <see cref="AbstractHeapTypeCodes"/> so a new abstract type is one edit, in one file.
    /// </remarks>
    private static bool IsSingleByteValType(byte b) =>
        b is 0x7B or 0x7C or 0x7D or 0x7E or 0x7F || AbstractHeapTypeCodes.TryFromCode(b, out _);

    // ── Formatting helpers ───────────────────────────────────────────────

    /// <summary>
    /// Spells a <c>ref.null</c> operand as the <em>heap</em> type the spec requires — <c>func</c>,
    /// not <c>funcref</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The two are the same byte but not the same text: wasm-tools 1.253.0 rejects
    /// <c>(ref.null funcref)</c> outright, so the reftype spelling made every disassembled
    /// <c>ref.null</c> unusable outside nesm. It surfaced with #383 because the canonical null table
    /// initializer — <c>(table 0 funcref (ref.null func))</c>, table.wast:19 — is the shape this
    /// helper is now asked to print.
    /// </para>
    /// <para>
    /// The operand is an <b>s33</b>, not a byte: non-negative values are defined type indices. Both
    /// call sites used to consume a single byte and print it as <c>0x00</c> when it was an index,
    /// which is neither valid text nor recoverable, and truncated any index above 127.
    /// </para>
    /// </remarks>
    private static string HeapTypeText(long heapType, DisassemblyContext context)
    {
        if (heapType >= 0)
            return TypeReferenceText(checked((int)heapType), context);

        return AbstractHeapTypeCodes.TryFromCode((byte)(heapType & 0x7F), out var kind)
            ? HeapTypeName(kind)
            : throw new WatException(
                $"Unknown abstract heap-type code 0x{(byte)(heapType & 0x7F):X2}.");
    }

    /// <summary>The spec's heap-type keyword — <c>func</c>, <c>extern</c>, <c>eq</c>, …</summary>
    private static string HeapTypeName(HeapTypeKind kind) => kind switch
    {
        HeapTypeKind.Func => "func",
        HeapTypeKind.Extern => "extern",
        HeapTypeKind.Any => "any",
        HeapTypeKind.Eq => "eq",
        HeapTypeKind.I31 => "i31",
        HeapTypeKind.Struct => "struct",
        HeapTypeKind.Array => "array",
        HeapTypeKind.None => "none",
        HeapTypeKind.NoFunc => "nofunc",
        HeapTypeKind.NoExtern => "noextern",
        HeapTypeKind.Exn => "exn",
        HeapTypeKind.NoExn => "noexn",
        HeapTypeKind.Cont => "cont",
        HeapTypeKind.NoCont => "nocont",
        _ => kind.ToString().ToLowerInvariant(),
    };

    /// <summary>The nullable reference-type abbreviation — <c>funcref</c>, <c>eqref</c>, …</summary>
    private static string AbstractRefTypeName(HeapTypeKind kind) => kind switch
    {
        HeapTypeKind.None => "nullref",
        HeapTypeKind.NoFunc => "nullfuncref",
        HeapTypeKind.NoExtern => "nullexternref",
        HeapTypeKind.NoExn => "nullexnref",
        HeapTypeKind.NoCont => "nullcontref",
        _ => HeapTypeName(kind) + "ref",
    };

    private static string FormatLimits(Limits limits) =>
        limits.Max.HasValue ? $"{limits.Min} {limits.Max.Value}" : $"{limits.Min}";

    private static string FormatGlobalType(
        GlobalType globalType,
        DisassemblyContext context)
    {
        ExactValueType exact =
            globalType.ExactContentType ??
            ExactValueType.FromStorageValType(globalType.ContentType);
        string text = ExactValueTypeText(exact, context);
        return globalType.Mutability == Mutability.Var ? $"(mut {text})" : text;
    }

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

    private static string Escape(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"':  sb.Append("\\\""); break;
                case '\n': sb.Append("\\n");  break;
                case '\r': sb.Append("\\r");  break;
                case '\t': sb.Append("\\t");  break;
                default:
                    if (c < ' ')
                        sb.Append($"\\{(byte)c:x2}");
                    else
                        sb.Append(c);
                    break;
            }
        }
        return sb.ToString();
    }

    /// <summary>Returns true when <paramref name="name"/> is a valid WAT bare identifier.</summary>
    private static bool IsValidIdentifier(string name) =>
        name.Length > 0 && name.All(WatLexer.IsBareIdentifierChar);

    /// <summary>Formats a non-empty name using the shortest standardized identifier syntax.</summary>
    private static string FormatIdentifier(string name) =>
        IsValidIdentifier(name) ? $"${name}" : $"$\"{Escape(name)}\"";

    /// <summary>Formats a name for a definition site; empty debug names require @name.</summary>
    private static string FormatDefName(string name) =>
        name.Length > 0 ? FormatIdentifier(name) : """(@name "")""";

    /// <summary>Formats a name for a reference site; empty names fall back to the numeric index.</summary>
    private static string FormatRefName(string name, int fallbackIdx) =>
        name.Length > 0 ? FormatIdentifier(name) : fallbackIdx.ToString();

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
