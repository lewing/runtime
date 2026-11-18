// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// <see cref="WasmCodeWriter"/>, the instruction encoder dotnet/runtime's ObjectWriter and JIT stubs need (#570).
/// Expected bytes are written out from the format and from runtime's <c>WasmInstructions.cs</c>; opcode encodings
/// are checked against <see cref="OpcodeTable"/>; real code is re-emitted byte for byte by <see cref="CodeReemitter"/>.
/// </summary>
public sealed partial class WasmCodeWriterTests
{
    private static (WasmCodeWriter Writer, ArrayBufferWriter<byte> Output) NewWriter()
    {
        var output = new ArrayBufferWriter<byte>();
        return (new WasmCodeWriter(output), output);
    }

    // ── runtime's stub encodings ────────────────────────────────────────────

    [Fact]
    public void RelocatableOperandsArePaddedAndTheirOffsetsReturned()
    {
        var (w, output) = NewWriter();
        // WasmEncodableSymbol: FUNCTION_INDEX_LEB and TYPE_INDEX_LEB as padded ULEBs, MEMORY_ADDR_REL_SLEB as a padded SLEB,
        // MEMORY_ADDR_REL_LEB as a memarg offset.
        Assert.Equal(1, w.Call(0, WasmLeb128.PaddedLength32));
        Assert.Equal(7, w.CallIndirect(0, 0, WasmLeb128.PaddedLength32));
        Assert.Equal(14, w.I32Const(0, WasmLeb128.PaddedLength32));
        Assert.Equal(21, w.MemoryAccess(WasmOp.I32Load, 2, 0, offsetWidth: WasmLeb128.PaddedLength32));
        Assert.Equal(27, w.Index(WasmOp.ReturnCall, 0, WasmLeb128.PaddedLength32));
        Assert.Equal(33, w.GlobalGet(3, WasmLeb128.PaddedLength32));
        // try_table tags are relocated too (R_WASM_TAG_INDEX_LEB), as the JIT writes them.
        Span<long> tags = stackalloc long[2];
        w.TryTable(WasmBlockType.Empty, [new(WasmCatchKind.Catch, 0, 1, WasmLeb128.PaddedLength32), new(WasmCatchKind.CatchAll, 0, 2)], tags);
        Assert.Equal([42L, -1L], tags.ToArray());
        Assert.Equal(50, w.Position);
        // A relocated call_indirect table (R_WASM_TABLE_NUMBER_LEB), as clang writes it with reference types.
        Assert.Equal(51, w.CallIndirect(2, 1, WasmLeb128.PaddedLength32, WasmLeb128.PaddedLength32, out long tablePosition));
        Assert.Equal(56, tablePosition);
        Assert.Equal(62, w.Indices(WasmOp.ReturnCallIndirect, 2, 1, 0, WasmLeb128.PaddedLength32, out tablePosition));
        Assert.Equal(63, tablePosition);
        Assert.Equal(Convert.FromHexString(
            "10" + "8080808000" +
            "11" + "8080808000" + "00" +
            "41" + "8080808000" +
            "28" + "02" + "8080808000" +
            "12" + "8080808000" +
            "23" + "8380808000" +
            "1f40" + "02" + "00" + "8080808000" + "01" + "02" + "02" +
            "11" + "8280808000" + "8180808000" +
            "13" + "02" + "8180808000"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void StubInstructionsMatchRuntimeEncodings()
    {
        var (w, output) = NewWriter();
        w.LocalGet(0);
        w.LocalTee(300);
        w.I32Const(-1);
        w.I64Const(long.MinValue);
        w.Emit(WasmOp.I32Add);
        w.MemoryAccess(WasmOp.I32Load16U, 1, 6);
        w.MemoryAccess(WasmOp.I64Store, 3, 8);
        w.MemoryAccess(WasmOp.V128Load, 4, 16);
        w.MemoryAccess(WasmOp.V128Store, 4, 16);
        w.Indices(WasmOp.MemoryCopy, 0, 0);
        w.Index(WasmOp.MemoryFill, 0);
        w.Indices(WasmOp.MemoryInit, 3, 0);
        w.Indices(WasmOp.TableInit, 1, 0);
        w.Index(WasmOp.TableGrow, 0);
        w.RefNull(WasmHeapType.Func);
        w.Block(WasmOp.If, WasmBlockType.Of(ValType.I32));
        w.Emit(WasmOp.Unreachable);
        w.End();
        Assert.Equal(Convert.FromHexString(
            "2000" + "22ac02" + "417f" + "42" + "8080808080808080807f" + "6a" +
            "2f" + "01" + "06" + "37" + "03" + "08" + "fd00" + "04" + "10" + "fd0b" + "04" + "10" +
            "fc0a0000" + "fc0b00" + "fc080300" + "fc0c0100" + "fc0f00" + "d070" + "047f" + "00" + "0b"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void LocalsRunLengthEncodeLikeRuntime()
    {
        var (w, output) = NewWriter();
        w.Locals([ValType.I32, ValType.I32, ValType.I64, ValType.I32]);
        Assert.Equal(Convert.FromHexString("03" + "027f" + "017e" + "017f"), output.WrittenSpan.ToArray());

        var (empty, emptyOutput) = NewWriter();
        empty.Locals([]);
        Assert.Equal([0x00], emptyOutput.WrittenSpan.ToArray());
    }

    [Fact]
    public void StructuredAndSimdImmediates()
    {
        var (w, output) = NewWriter();
        w.Block(WasmOp.Block, WasmBlockType.Empty);
        w.Block(WasmOp.Loop, WasmBlockType.TypeIndex(200));
        w.BrTable([0, 1, 300], 2);
        w.TryTable(WasmBlockType.Of(ValType.ExnRef), [new(WasmCatchKind.Catch, 1, 0), new(WasmCatchKind.CatchAllRef, 0, 2)]);
        w.MemoryAccess(WasmOp.I32Load, 2, 8, memoryIndex: 1);
        w.MemoryAccessLane(WasmOp.V128Load32Lane, 2, 4, 3);
        w.Lane(WasmOp.I32x4ExtractLane, 1);
        w.V128Const(Enumerable.Range(0, 16).Select(i => (byte)i).ToArray());
        w.I8x16Shuffle(Enumerable.Range(16, 16).Select(i => (byte)i).ToArray());
        w.SelectTyped([ValType.I64]);
        w.RefNull(WasmHeapType.Concrete(5));
        w.F32Const(1.5f);
        w.F64Const(-2.0);
        Assert.Equal(Convert.FromHexString(
            "0240" + "03c801" + "0e03" + "0001ac02" + "02" + "1f69" + "02" + "000100" + "0302" +
            "28" + "42" + "01" + "08" + "fd56" + "02" + "04" + "03" + "fd1b" + "01" +
            "fd0c" + "000102030405060708090a0b0c0d0e0f" + "fd0d" + "101112131415161718191a1b1c1d1e1f" +
            "1c017e" + "d005" + "430000c03f" + "44" + "00000000000000c0"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void EmittersRejectOpcodesOfAnotherShapeAndInvalidOperands()
    {
        var (w, output) = NewWriter();
        Assert.Throws<ArgumentException>(() => w.Emit(WasmOp.Call));
        Assert.Throws<ArgumentException>(() => w.Index(WasmOp.I32Add, 1));
        Assert.Throws<ArgumentException>(() => w.MemoryAccess(WasmOp.LocalGet, 0, 0));
        Assert.Throws<ArgumentException>(() => w.Block(WasmOp.End, WasmBlockType.Empty));
        Assert.Throws<ArgumentException>(() => w.Index(WasmOp.Call, 1u << 20, width: 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Index(WasmOp.Call, 1, width: 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Emit((WasmOp)0xFF));
        Assert.Throws<ArgumentException>(() => w.V128Const(new byte[15]));
        Assert.Throws<ArgumentException>(() => w.Locals([(ValType)0x63]));
        Assert.Throws<ArgumentException>(() => w.Locals([(ValType)AbstractHeapTypeCodes.ContLegacy]));
        Assert.Throws<ArgumentException>(() => WasmBlockType.Of((ValType)0x40));
        Assert.Throws<ArgumentOutOfRangeException>(() => WasmHeapType.Abstract((HeapTypeKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.MemoryAccess(WasmOp.I32Load, 64, 0));
        Assert.Throws<ArgumentException>(() => w.TryTable(WasmBlockType.Empty, [new(WasmCatchKind.Catch, 1u << 20, 0, TagWidth: 2)]));
        Assert.Throws<ArgumentException>(() => w.TryTable(WasmBlockType.Empty, [new(WasmCatchKind.CatchAll, 0, 0)], new long[2]));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void AbstractHeapTypesHaveTheirSpecCodes()
    {
        var (w, output) = NewWriter();
        w.RefNull(WasmHeapType.Func);
        w.RefNull(WasmHeapType.Extern);
        w.RefNull(WasmHeapType.Exn);
        Assert.Equal(Convert.FromHexString("d070" + "d06f" + "d069"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void DefaultBlockTypeIsEmptyAndDefaultHeapTypeIsRejected()
    {
        var (w, output) = NewWriter();
        w.Block(WasmOp.Block, default);
        w.TryTable(default, []);
        Assert.Equal(Convert.FromHexString("0240" + "1f4000"), output.WrittenSpan.ToArray());

        var (rejected, rejectedOutput) = NewWriter();
        Assert.Throws<ArgumentException>(() => rejected.RefNull(default));
        Assert.Equal(0, rejectedOutput.WrittenCount);
    }

    [Fact]
    public void LaneIndicesAreRangeChecked()
    {
        var (w, output) = NewWriter();
        w.Lane(WasmOp.I8x16ExtractLaneS, 15);
        w.Lane(WasmOp.F64x2ReplaceLane, 1);
        w.MemoryAccessLane(WasmOp.V128Load8Lane, 0, 0, 15);
        w.MemoryAccessLane(WasmOp.V128Store64Lane, 3, 0, 1);
        Assert.Equal(Convert.FromHexString("fd150f" + "fd2201" + "fd5400000f" + "fd5b030001"), output.WrittenSpan.ToArray());

        var (rejected, rejectedOutput) = NewWriter();
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Lane(WasmOp.I8x16ExtractLaneS, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Lane(WasmOp.I16x8ReplaceLane, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Lane(WasmOp.F32x4ExtractLane, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.Lane(WasmOp.I64x2ExtractLane, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.MemoryAccessLane(WasmOp.V128Load16Lane, 1, 0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.MemoryAccessLane(WasmOp.V128Store32Lane, 2, 0, 4));
        Assert.Throws<ArgumentOutOfRangeException>(() => rejected.MemoryAccessLane(WasmOp.V128Load64Lane, 3, 0, 2));
        Assert.Equal(0, rejectedOutput.WrittenCount);
    }

    // ── Opcodes ─────────────────────────────────────────────────────────────

    /// <summary>Every opcode of <see cref="OpcodeTable"/> (except stack switching) is a <see cref="WasmOp"/> with the same encoding.</summary>
    [Fact]
    public void EveryOpcodeMatchesTheWatOpcodeTable()
    {
        int compared = 0;
        foreach (string mnemonic in OpcodeTable.AllMnemonics)
        {
            OpcodeEntry entry = OpcodeTable.Get(mnemonic);
            if (entry.Opcodes[0] == 0xE0)
                continue;
            string name = PascalCase(mnemonic);
            Assert.True(Enum.TryParse(name, out WasmOp op), $"{mnemonic} has no WasmOp {name}");
            Assert.True(Enum.IsDefined(op), name);

            var (w, output) = NewWriter();
            EmitWithZeroImmediates(w, op);
            Assert.True(output.WrittenSpan.StartsWith(entry.Opcodes), $"{mnemonic}: {Convert.ToHexString(output.WrittenSpan)} does not start with {Convert.ToHexString(entry.Opcodes)}");
            Assert.Equal(ExpectedShape(entry.Immediate), WasmOps.GetImmediateShape(op));
            compared++;
        }

        // Typed select is not in the WAT table (it shares the select mnemonic).
        Assert.Equal(compared + 1, Enum.GetValues<WasmOp>().Length);
        Assert.True(compared > 470, $"only {compared} opcodes compared");
    }

    /// <summary>The source of <c>WasmOp.cs</c>: every <see cref="OpcodeTable"/> opcode except stack switching, plus typed <c>select</c>.</summary>
    private static string GenerateWasmOpSource()
    {
        var ops = new List<(uint Value, string Name, string Mnemonic, WasmImmediateShape Shape)>
        {
            // Typed select shares the select mnemonic, so it is not in the WAT table.
            (0x1C, "SelectTyped", "select (typed)", WasmImmediateShape.SelectTyped),
        };
        foreach (string mnemonic in OpcodeTable.AllMnemonics)
        {
            OpcodeEntry entry = OpcodeTable.Get(mnemonic);
            if (entry.Opcodes[0] == WasmOpcode.StackSwitchingPrefix)
                continue;
            uint value = entry.Opcodes[0];
            if (entry.Opcodes.Length > 1)
            {
                int at = 1;
                value = value << 24 | WasmLeb128.ReadU32(entry.Opcodes, ref at);
                Assert.Equal(entry.Opcodes.Length, at);
            }

            ops.Add((value, PascalCase(mnemonic), mnemonic, ExpectedShape(entry.Immediate)));
        }

        ops.Sort((a, b) => a.Value.CompareTo(b.Value));
        var s = new StringBuilder();
        s.Append("""
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

            """);
        foreach (var op in ops)
            s.Append(CultureInfo.InvariantCulture, $"    /// <summary><c>{op.Mnemonic}</c></summary>\n    {op.Name} = 0x{op.Value:X},\n");
        s.Append("""
            }

            /// <summary>Opcode properties.</summary>
            public static class WasmOps
            {
                /// <summary>The immediates <paramref name="op"/> takes.</summary>
                /// <exception cref="ArgumentOutOfRangeException"><paramref name="op"/> is not a defined opcode.</exception>
                public static WasmImmediateShape GetImmediateShape(WasmOp op) => op switch
                {

            """);
        foreach (WasmImmediateShape shape in Enum.GetValues<WasmImmediateShape>())
        {
            var line = new StringBuilder("        ");
            bool first = true;
            foreach (var op in ops)
            {
                if (op.Shape != shape)
                    continue;
                string item = (first ? "" : " or ") + "WasmOp." + op.Name;
                if (!first && line.Length + item.Length + 3 > 120)
                {
                    s.Append(line).Append(" or\n");
                    line.Clear().Append("            ");
                    item = item[4..];
                }

                line.Append(item);
                first = false;
            }

            Assert.False(first, $"no opcode has shape {shape}");
            s.Append(line).Append(CultureInfo.InvariantCulture, $" => WasmImmediateShape.{shape},\n");
        }

        s.Append("""
                    _ => throw new ArgumentOutOfRangeException(nameof(op), op, "Not a defined opcode."),
                };
            }

            """);
        return s.ToString();
    }

    private static string PascalCase(string mnemonic) =>
        string.Concat(mnemonic.Split('.', '_').Select(part => char.ToUpperInvariant(part[0]) + part[1..]));

    private static WasmImmediateShape ExpectedShape(ImmediateKind kind) => kind switch
    {
        ImmediateKind.None => WasmImmediateShape.None,
        ImmediateKind.LocalIdx or ImmediateKind.GlobalIdx or ImmediateKind.FuncIdx or ImmediateKind.LabelIdx or ImmediateKind.TagIdx
            or ImmediateKind.TableIdx or ImmediateKind.MemIdx or ImmediateKind.DataIdx or ImmediateKind.ElemIdx or ImmediateKind.TypeIdx => WasmImmediateShape.Index,
        ImmediateKind.CallIndirect or ImmediateKind.DataMemIdx or ImmediateKind.MemCopy or ImmediateKind.ElemTableIdx or ImmediateKind.TableCopy => WasmImmediateShape.IndexPair,
        ImmediateKind.I32 => WasmImmediateShape.I32,
        ImmediateKind.I64 => WasmImmediateShape.I64,
        ImmediateKind.F32 => WasmImmediateShape.F32,
        ImmediateKind.F64 => WasmImmediateShape.F64,
        ImmediateKind.MemArg => WasmImmediateShape.MemArg,
        ImmediateKind.MemArgLane => WasmImmediateShape.MemArgLane,
        ImmediateKind.SimdLane => WasmImmediateShape.Lane,
        ImmediateKind.V128 => WasmImmediateShape.V128,
        ImmediateKind.SimdShuffle => WasmImmediateShape.Shuffle,
        ImmediateKind.BlockType => WasmImmediateShape.Block,
        ImmediateKind.BrTable => WasmImmediateShape.BrTable,
        ImmediateKind.TryTable => WasmImmediateShape.TryTable,
        ImmediateKind.RefType => WasmImmediateShape.HeapType,
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };

    private static void EmitWithZeroImmediates(WasmCodeWriter w, WasmOp op)
    {
        switch (WasmOps.GetImmediateShape(op))
        {
            case WasmImmediateShape.None: w.Emit(op); break;
            case WasmImmediateShape.Index: w.Index(op, 0); break;
            case WasmImmediateShape.IndexPair: w.Indices(op, 0, 0); break;
            case WasmImmediateShape.I32: w.I32Const(0); break;
            case WasmImmediateShape.I64: w.I64Const(0); break;
            case WasmImmediateShape.F32: w.F32Const(0); break;
            case WasmImmediateShape.F64: w.F64Const(0); break;
            case WasmImmediateShape.MemArg: w.MemoryAccess(op, 0, 0); break;
            case WasmImmediateShape.MemArgLane: w.MemoryAccessLane(op, 0, 0, 0); break;
            case WasmImmediateShape.Lane: w.Lane(op, 0); break;
            case WasmImmediateShape.V128: w.V128Const(new byte[16]); break;
            case WasmImmediateShape.Shuffle: w.I8x16Shuffle(new byte[16]); break;
            case WasmImmediateShape.Block: w.Block(op, WasmBlockType.Empty); break;
            case WasmImmediateShape.BrTable: w.BrTable([], 0); break;
            case WasmImmediateShape.TryTable: w.TryTable(WasmBlockType.Empty, []); break;
            case WasmImmediateShape.HeapType: w.RefNull(WasmHeapType.Func); break;
            case WasmImmediateShape.SelectTyped: w.SelectTyped([ValType.I32]); break;
        }
    }

    /// <summary>
    /// Every code body of dotnet/runtime's ILCompiler objects and crossgen2 Webcil images (set <c>NESM_OBJCMP_DIR</c> to an
    /// <c>artifacts/objcmp/&lt;tag&gt;/out</c> directory) re-emits byte for byte.
    /// </summary>
    [ConditionalTheory]
    [MemberData(nameof(ObjectWriterReferenceTests.References), MemberType = typeof(ObjectWriterReferenceTests))]
    public void RuntimeCodeReemitsByteIdentically(string relativePath)
    {
        if (relativePath.Length == 0)
            throw new SkipTestException("Set NESM_OBJCMP_DIR to a dotnet/runtime artifacts/objcmp/<tag>/out directory.");

        byte[] module = File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("NESM_OBJCMP_DIR")!, relativePath));
        List<byte[]> bodies = CodeReemitter.FunctionBodies(module);
        // Component stubs (composite members, System.Runtime facade) have only a couple of bodies; the rest have thousands.
        Assert.NotEmpty(bodies);
        for (int i = 0; i < bodies.Count; i++)
        {
            byte[] reemitted = CodeReemitter.Reemit(bodies[i]);
            if (!bodies[i].AsSpan().SequenceEqual(reemitted))
                ObjectReencoder.AssertBytesEqual(bodies[i], reemitted, $"{relativePath} body {i}");
        }
    }

    private static HashSet<WasmImmediateShape> Shapes(byte[] body)
    {
        int at = 0;
        uint groups = WasmLeb128.ReadU32(body, ref at);
        for (int i = 0; i < groups; i++)
        {
            WasmLeb128.ReadU32(body, ref at);
            at++;
        }

        var shapes = new HashSet<WasmImmediateShape>();
        var cursor = new InstructionCursor(body[at..], WasmFeatures.All);
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            OpcodeId id = instruction.OpcodeId;
            shapes.Add(WasmOps.GetImmediateShape((WasmOp)(id.IsPrefixed ? ((uint)id.Opcode << 24) | id.SubOpcode : id.Opcode)));
        }

        return shapes;
    }
}
