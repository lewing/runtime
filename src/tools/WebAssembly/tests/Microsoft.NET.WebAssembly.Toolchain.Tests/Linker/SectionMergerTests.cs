// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

public class SectionMergerTests
{
    [Fact]
    public void Memory64_IsRefusedByTheWriterRatherThanNarrowed()
    {
        var module = new WasmModule { Memories = [new MemoryType(new Limits(2, null, Is64: true))] };

        var error = Assert.Throws<LinkerException>(() => new SectionMerger().EmitBinary(module));

        Assert.Contains("memory64", error.Message);
    }

    [Fact]
    public void Memory64Import_IsRefusedByTheWriterRatherThanNarrowed()
    {
        var module = new WasmModule
        {
            Imports = [new Import("env", "memory", new ImportDescriptor.Memory(new MemoryType(new Limits(2, null, Is64: true))))],
        };

        var error = Assert.Throws<LinkerException>(() => new SectionMerger().EmitBinary(module));

        Assert.Contains("memory64", error.Message);
    }

    [Fact]
    public void Table64Import_IsRefusedByTheWriterRatherThanNarrowed()
    {
        var module = new WasmModule
        {
            Imports = [new Import("env", "table", new ImportDescriptor.Table(new TableType(RefType.FuncRef, new Limits(2, null, Is64: true))))],
        };

        var error = Assert.Throws<LinkerException>(() => new SectionMerger().EmitBinary(module));

        Assert.Contains("table64", error.Message);
    }

    [Fact]
    public void OutOfRangeDefinedType_IsRefusedByTheWriter()
    {
        WasmModule module = ModuleReader.ReadModule(new MemoryStream(WatCompiler.Compile(
            "(module (type (func)) (table 1 funcref))")));
        module.Tables[0] = module.Tables[0] with
        {
            ExactElementType = new Microsoft.NET.WebAssembly.Types.ValueType.Reference(Nullability.Nullable, new DefinedHeapType(1)),
        };

        var error = Assert.Throws<LinkerException>(() => new SectionMerger().EmitBinary(module));

        Assert.Contains("type index 1", error.Message);
        Assert.Contains("outside the merged type space", error.Message);
    }

    [Fact]
    public void DataInNonzeroMemory_PreservesItsIndexOnReencode()
    {
        // wasm-tools parse: (module (memory 1) (memory 1) (data (memory 1) (i32.const 0) "abc"))
        byte[] original = Convert.FromHexString("0061736d01000000050502000100010b0a01020141000b03616263");
        WasmModule module = ModuleReader.ReadModuleAndValidate(new MemoryStream(original));

        byte[] rewritten = new SectionMerger().EmitBinary(module);

        Assert.Equal(original, rewritten);
        WasmModule parsed = ModuleReader.ReadModuleAndValidate(new MemoryStream(rewritten));
        Assert.Equal(1, Assert.Single(parsed.DataSegments).MemoryIndex);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void InvalidEntryArgumentsAreReportedAsLinkerErrorsBeforeWriting(bool streaming)
    {
        var module = new WasmModule { Memories = [new MemoryType(new Limits((ulong)uint.MaxValue + 1))] };
        var merger = new SectionMerger();
        using var output = new MemoryStream();

        var error = Assert.Throws<LinkerException>(() =>
        {
            if (streaming)
                merger.WriteBinary(output, module);
            else
                merger.EmitBinary(module);
        });

        Assert.IsAssignableFrom<ArgumentException>(error.InnerException);
        Assert.Contains("32-bit limits", error.Message);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void MergeTypesAndImports_DeduplicatesAndBuildsIndexMaps()
    {
        var signature = new FuncType([ValType.I32], [ValType.I32]);
        var modules = new[]
        {
            new WasmModule
            {
                Types = [signature],
                Imports =
                [
                    new Import("env", "log", new ImportDescriptor.Func(0)),
                ],
            },
            new WasmModule
            {
                Types = [new FuncType([ValType.I32], [ValType.I32])],
                Imports =
                [
                    new Import("env", "log", new ImportDescriptor.Func(0)),
                ],
            },
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger = new SectionMerger();

        var mergedTypes = merger.MergeTypes(modules, indexMaps);
        var mergedImports = merger.MergeImports(modules, indexMaps);

        Assert.Single(mergedTypes);
        Assert.Single(mergedImports.Imports);
        Assert.Equal<uint>(0, indexMaps[0].Types[0]);
        Assert.Equal<uint>(0, indexMaps[1].Types[0]);
        Assert.Equal<uint>(0, indexMaps[0].Functions[0]);
        Assert.Equal<uint>(0, indexMaps[1].Functions[0]);

        var import = Assert.Single(mergedImports.Imports);
        var descriptor = Assert.IsType<ImportDescriptor.Func>(import.Descriptor);
        Assert.Equal(0, descriptor.TypeIndex);
    }

    [Fact]
    public void Link_SharedMemoryDataSegments_GetAlignedBaseOffsets_AndSymbolExports()
    {
        var linker = new WasmLinker();
        var left = CreateModule(
            exportName: "left",
            constantValue: 7,
            globalValue: 1,
            dataBytes: [1, 2, 3],
            importMemory: true,
            alignmentLog2: 0);
        var right = CreateModule(
            exportName: "right",
            constantValue: 9,
            globalValue: 2,
            dataBytes: [4, 5],
            importMemory: true,
            alignmentLog2: 2);

        var merged = linker.Link([left, right], new LinkerOptions { CoalesceSegments = false });
        var maps = linker.LastLinkIndexMaps!;

        Assert.Single(merged.Imports);
        Assert.True(merged.Exports.ContainsKey("left"));
        Assert.True(merged.Exports.ContainsKey("right"));
        Assert.Equal<uint>(0, maps[0].DataSegmentOffsets[0]);
        Assert.Equal<uint>(4, maps[1].DataSegmentOffsets[0]);
        Assert.Equal([0x41, 0x00, 0x0B], merged.DataSegments[0].OffsetExpr);
        Assert.Equal([0x41, 0x04, 0x0B], merged.DataSegments[1].OffsetExpr);
    }

    [Fact]
    public void LinkToBinary_RoundTripsMergedModuleWithCoreSections()
    {
        var linker = new WasmLinker();
        var first = CreateModule(
            exportName: "first",
            constantValue: 11,
            globalValue: 3,
            dataBytes: [0xAA],
            importMemory: false,
            alignmentLog2: 0);
        var second = CreateModule(
            exportName: "second",
            constantValue: 13,
            globalValue: 4,
            dataBytes: [0xBB, 0xCC],
            importMemory: false,
            alignmentLog2: 1);

        byte[] bytes = linker.LinkToBinary([first, second]);
        var parsed = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.Single(parsed.Types);
        Assert.Equal(2, parsed.Functions.Count);
        Assert.Equal(2, parsed.Globals.Count);
        Assert.Equal(2, parsed.Tables.Count);
        Assert.Equal(2, parsed.Memories.Count);
        Assert.Equal(2, parsed.Elements.Count);
        Assert.Equal(2, parsed.DataSegments.Count);
        Assert.True(parsed.Exports.ContainsKey("first"));
        Assert.True(parsed.Exports.ContainsKey("second"));
    }

    private static WasmModule CreateModule(
        string exportName,
        int constantValue,
        int globalValue,
        byte[] dataBytes,
        bool importMemory,
        uint alignmentLog2)
    {
        var module = new WasmModule
        {
            Types = [new FuncType([], [ValType.I32])],
            Functions =
            [
                new WasmFunction
                {
                    TypeIndex = 0,
                    Locals = [],
                    Body = [0x41, checked((byte)constantValue), 0x0B],
                },
            ],
            Tables = [new TableType(RefType.FuncRef, new Limits(1, 1))],
            Globals =
            [
                new WasmGlobal
                {
                    Type = new GlobalType(ValType.I32, Mutability.Const),
                    InitExpr = [0x41, checked((byte)globalValue), 0x0B],
                },
            ],
            Elements =
            [
                new Element
                {
                    TableIndex = 0,
                    OffsetExpr = [0x41, 0x00, 0x0B],
                    FunctionIndices = [0],
                },
            ],
            DataSegments =
            [
                new Data
                {
                    IsPassive = false,
                    MemoryIndex = 0,
                    OffsetExpr = [0x41, 0x00, 0x0B],
                    InitData = dataBytes,
                },
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                SegmentInfos =
                [
                    new SegmentInfo
                    {
                        Name = ".data",
                        Alignment = alignmentLog2,
                        Flags = SegmentFlags.None,
                    },
                ],
                Symbols =
                [
                    new WasmSymbol
                    {
                        Type = SymbolType.Function,
                        Flags = SymbolFlags.Exported,
                        Name = exportName,
                        Index = 0,
                    },
                ],
            },
        };

        if (importMemory)
        {
            module.Imports.Add(new Import(
                "env",
                "memory",
                new ImportDescriptor.Memory(new MemoryType(new Limits(1)))));
        }
        else
        {
            module.Memories.Add(new MemoryType(new Limits(1)));
        }

        return module;
    }

    // ── CoalesceDataSegments tests ──────────────────────────────────────────

    [Fact]
    public void CoalesceDataSegments_AdjacentSegments_MergedIntoOne()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0, [1, 2, 3]),
            MakeActiveSegment(3, [4, 5]),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        var seg = Assert.Single(result);
        Assert.Equal([1, 2, 3, 4, 5], seg.InitData);
        AssertOffset(seg, 0);
    }

    [Fact]
    public void CoalesceDataSegments_GapWithinThreshold_ZeroFilled()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0,  [0xAA, 0xBB]),
            MakeActiveSegment(10, [0xCC]),
        };

        var result = SectionMerger.CoalesceDataSegments(segments, gapThreshold: 32);

        var seg = Assert.Single(result);
        Assert.Equal(11, seg.InitData.Length);
        Assert.Equal(0xAA, seg.InitData[0]);
        Assert.Equal(0xBB, seg.InitData[1]);
        for (int i = 2; i < 10; i++)
            Assert.Equal(0, seg.InitData[i]);
        Assert.Equal(0xCC, seg.InitData[10]);
        AssertOffset(seg, 0);
    }

    [Fact]
    public void CoalesceDataSegments_GapExceedsThreshold_KeepsSeparate()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0,   [1, 2]),
            MakeActiveSegment(100, [3, 4]),
        };

        var result = SectionMerger.CoalesceDataSegments(segments, gapThreshold: 32);

        Assert.Equal(2, result.Count);
    }

    [Fact]
    public void CoalesceDataSegments_OverlappingSegments_LaterOverwrites()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0, [0xAA, 0xBB, 0xCC]),
            MakeActiveSegment(1, [0x11, 0x22]),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        var seg = Assert.Single(result);
        Assert.Equal([0xAA, 0x11, 0x22], seg.InitData);
        AssertOffset(seg, 0);
    }

    [Fact]
    public void CoalesceDataSegments_PassiveSegments_PassedThrough()
    {
        var segments = new List<Data>
        {
            new Data { IsPassive = true, MemoryIndex = -1, OffsetExpr = [], InitData = [0xFF] },
            MakeActiveSegment(0, [1, 2]),
            MakeActiveSegment(2, [3]),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        Assert.Equal(2, result.Count); // 1 active coalesced + 1 passive
        Assert.Single(result, r => r.IsPassive);
        var active = result.Single(r => !r.IsPassive);
        Assert.Equal([1, 2, 3], active.InitData);
    }

    [Fact]
    public void CoalesceDataSegments_MultipleMemories_GroupedSeparately()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0, [1, 2], memoryIndex: 0),
            MakeActiveSegment(0, [3, 4], memoryIndex: 1),
            MakeActiveSegment(2, [5],    memoryIndex: 0),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        Assert.Equal(2, result.Count);
        var mem0 = result.Single(s => s.MemoryIndex == 0);
        var mem1 = result.Single(s => s.MemoryIndex == 1);
        Assert.Equal([1, 2, 5], mem0.InitData);
        Assert.Equal([3, 4],    mem1.InitData);
    }

    [Fact]
    public void CoalesceDataSegments_Empty_ReturnsEmpty()
    {
        var result = SectionMerger.CoalesceDataSegments([]);
        Assert.Empty(result);
    }

    [Fact]
    public void Link_CoalesceSegments_DefaultOn_ReducesSegmentCount()
    {
        // Three adjacent active segments — coalescing should produce one.
        var module = new WasmModule
        {
            Memories = [new MemoryType(new Limits(1))],
            DataSegments =
            [
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x00, 0x0B], InitData = [1, 2] },
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x02, 0x0B], InitData = [3, 4] },
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x04, 0x0B], InitData = [5] },
            ],
        };

        var linker = new WasmLinker();
        var merged = linker.Link([module], new LinkerOptions { GcSections = false, CoalesceSegments = true });

        Assert.Single(merged.DataSegments);
        Assert.Equal([1, 2, 3, 4, 5], merged.DataSegments[0].InitData);
    }

    [Fact]
    public void Link_CoalesceSegments_Disabled_PreservesAllSegments()
    {
        var module = new WasmModule
        {
            Memories = [new MemoryType(new Limits(1))],
            DataSegments =
            [
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x00, 0x0B], InitData = [1, 2] },
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x02, 0x0B], InitData = [3, 4] },
            ],
        };

        var linker = new WasmLinker();
        var merged = linker.Link([module], new LinkerOptions { GcSections = false, CoalesceSegments = false });

        Assert.Equal(2, merged.DataSegments.Count);
    }

    [Fact]
    public void CoalesceDataSegments_RoAndRw_CoalescedSeparately()
    {
        var segments = new List<Data>
        {
            MakeActiveSegment(0,  [0xAA, 0xBB], isReadOnly: true),
            MakeActiveSegment(2,  [0xCC],        isReadOnly: true),
            MakeActiveSegment(10, [0x11, 0x22],  isReadOnly: false),
            MakeActiveSegment(12, [0x33],        isReadOnly: false),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        Assert.Equal(2, result.Count);
        var ro = result[0];
        var rw = result[1];
        Assert.True(ro.IsReadOnly);
        Assert.False(rw.IsReadOnly);
        Assert.Equal([0xAA, 0xBB, 0xCC], ro.InitData);
        Assert.Equal([0x11, 0x22, 0x33], rw.InitData);
    }

    [Fact]
    public void CoalesceDataSegments_RoBeforeRw_OutputOrdering()
    {
        // rw segment has a lower memory address than ro — output must still put ro first.
        var segments = new List<Data>
        {
            MakeActiveSegment(100, [0x11], isReadOnly: false),
            MakeActiveSegment(0,   [0xAA], isReadOnly: true),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        Assert.Equal(2, result.Count);
        Assert.True(result[0].IsReadOnly,  "ro segment should come first");
        Assert.False(result[1].IsReadOnly, "rw segment should come second");
    }

    [Fact]
    public void CoalesceDataSegments_RoAdjacentToRw_NotMerged()
    {
        // A ro and rw segment at adjacent addresses must NOT be merged together.
        var segments = new List<Data>
        {
            MakeActiveSegment(0, [1, 2, 3], isReadOnly: true),
            MakeActiveSegment(3, [4, 5],    isReadOnly: false),
        };

        var result = SectionMerger.CoalesceDataSegments(segments);

        Assert.Equal(2, result.Count);
        Assert.Single(result, r => r.IsReadOnly);
        Assert.Single(result, r => !r.IsReadOnly);
    }

    [Fact]
    public void MergeDataSegments_SetsIsReadOnly_FromLinkingSectionName()
    {
        // Build a minimal module with a linking section that names the segments.
        var module = new WasmModule
        {
            Memories = [new MemoryType(new Limits(1))],
            DataSegments =
            [
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x00, 0x0B], InitData = [1, 2] },
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x02, 0x0B], InitData = [3, 4] },
            ],
            LinkingSection = new Microsoft.NET.WebAssembly.Binary.Linking.LinkingSection
            {
                Version = 2,
                SegmentInfos =
                [
                    new Microsoft.NET.WebAssembly.Binary.Linking.SegmentInfo { Name = ".rodata.str", Alignment = 0, Flags = Microsoft.NET.WebAssembly.Binary.Linking.SegmentFlags.None },
                    new Microsoft.NET.WebAssembly.Binary.Linking.SegmentInfo { Name = ".data",       Alignment = 0, Flags = Microsoft.NET.WebAssembly.Binary.Linking.SegmentFlags.None },
                ],
            },
        };

        var linker = new WasmLinker();
        var merged = linker.Link([module], new LinkerOptions { GcSections = false, CoalesceSegments = true });

        // Coalescing should produce 2 segments: one ro, one rw.
        Assert.Equal(2, merged.DataSegments.Count);
        Assert.True(merged.DataSegments[0].IsReadOnly,  "first output segment should be read-only (.rodata)");
        Assert.False(merged.DataSegments[1].IsReadOnly, "second output segment should be read-write (.data)");
    }

    private static Data MakeActiveSegment(uint offset, byte[] data, int memoryIndex = 0, bool isReadOnly = false) =>
        new Data
        {
            IsPassive   = false,
            MemoryIndex = memoryIndex,
            OffsetExpr  = [0x41, .. EncodeSleb128(offset), 0x0B],
            InitData    = data,
            IsReadOnly  = isReadOnly,
        };

    private static byte[] EncodeSleb128(uint value)
    {
        var result = new List<byte>();
        int v = unchecked((int)value);
        bool more;
        do
        {
            byte b = (byte)(v & 0x7F);
            v >>= 7;
            more = !((v == 0 && (b & 0x40) == 0) || (v == -1 && (b & 0x40) != 0));
            result.Add(more ? (byte)(b | 0x80) : b);
        } while (more);
        return [.. result];
    }

    private static void AssertOffset(Data seg, uint expectedOffset)
    {
        Assert.Equal(0x41, seg.OffsetExpr[0]);
        Assert.Equal(0x0B, seg.OffsetExpr[^1]);
        // Decode the LEB128 value
        int decoded = 0, shift = 0, i = 1;
        byte b;
        do { b = seg.OffsetExpr[i++]; decoded |= (b & 0x7F) << shift; shift += 7; } while ((b & 0x80) != 0);
        Assert.Equal(expectedOffset, unchecked((uint)decoded));
    }
}
