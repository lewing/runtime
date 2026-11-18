// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// The public relocatable-object writing API (#581). Expected bytes come from the encoding dotnet/runtime's
/// <c>WasmRelocatableObjectWriter</c> produces and from LLVM's object writer, not from the encoders under test.
/// </summary>
public sealed partial class WasmObjectWriterApiTests
{
    // ── LEB128 ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0UL, "00")]
    [InlineData(127UL, "7f")]
    [InlineData(128UL, "8001")]
    [InlineData(624485UL, "e58e26")]
    [InlineData(uint.MaxValue, "ffffffff0f")]
    [InlineData(ulong.MaxValue, "ffffffffffffffffff01")]
    public void ULeb128MatchesTheReferenceEncoding(ulong value, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength];
        int written = WasmLeb128.WriteULeb128(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());
        Assert.Equal(expected.Length, WasmLeb128.GetULeb128Size(value));

        using var stream = new MemoryStream();
        WasmLeb128.WriteULeb128(stream, value);
        Assert.Equal(expected, stream.ToArray());
    }

    [Theory]
    [InlineData(0L, "00")]
    [InlineData(63L, "3f")]
    [InlineData(64L, "c000")]
    [InlineData(-1L, "7f")]
    [InlineData(-64L, "40")]
    [InlineData(-65L, "bf7f")]
    [InlineData(-123456L, "c0bb78")]
    [InlineData(long.MinValue, "8080808080808080807f")]
    public void SLeb128MatchesTheReferenceEncoding(long value, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength];
        int written = WasmLeb128.WriteSLeb128(buffer, value);
        Assert.Equal(expected, buffer[..written].ToArray());
        Assert.Equal(expected.Length, WasmLeb128.GetSLeb128Size(value));

        using var stream = new MemoryStream();
        WasmLeb128.WriteSLeb128(stream, value);
        Assert.Equal(expected, stream.ToArray());
    }

    [Theory]
    [InlineData(0UL, 5, "8080808000")]
    [InlineData(3UL, 5, "8380808000")]
    [InlineData(624485UL, 5, "e58ea68000")]
    [InlineData(uint.MaxValue, 5, "ffffffff0f")]
    [InlineData(127UL, 2, "ff00")]
    [InlineData(1UL, 10, "81808080808080808000")]
    public void PaddedULeb128FillsTheDestination(ulong value, int width, string expectedHex)
    {
        byte[] buffer = new byte[width];
        WasmLeb128.WritePaddedULeb128(buffer, value);
        Assert.Equal(Convert.FromHexString(expectedHex), buffer);
    }

    [Theory]
    [InlineData(0L, 5, "8080808000")]
    [InlineData(1L, 5, "8180808000")]
    [InlineData(-1L, 5, "ffffffff7f")]
    [InlineData(-65L, 5, "bfffffff7f")]
    [InlineData(int.MinValue, 5, "8080808078")]
    [InlineData(-1L, 10, "ffffffffffffffffff7f")]
    [InlineData(long.MinValue, 10, "8080808080808080807f")]
    public void PaddedSLeb128FillsTheDestination(long value, int width, string expectedHex)
    {
        byte[] buffer = new byte[width];
        WasmLeb128.WritePaddedSLeb128(buffer, value);
        Assert.Equal(Convert.FromHexString(expectedHex), buffer);
    }

    [Fact]
    public void PaddedLeb128RejectsAValueWiderThanTheDestination()
    {
        Assert.Throws<ArgumentException>(() => WasmLeb128.WritePaddedULeb128(new byte[2], 1UL << 14));
        Assert.Throws<ArgumentException>(() => WasmLeb128.WritePaddedSLeb128(new byte[1], 64));
        Assert.Throws<ArgumentException>(() => WasmLeb128.WritePaddedULeb128(Span<byte>.Empty, 0));
    }

    // ── Relocation addend rule ──────────────────────────────────────────────

    // llvm::wasm::relocTypeHasAddend (llvm/lib/BinaryFormat/Wasm.cpp), written out independently.
    private static readonly HashSet<RelocationType> s_llvmTypesWithAddend =
    [
        RelocationType.R_WASM_MEMORY_ADDR_LEB,
        RelocationType.R_WASM_MEMORY_ADDR_LEB64,
        RelocationType.R_WASM_MEMORY_ADDR_SLEB,
        RelocationType.R_WASM_MEMORY_ADDR_SLEB64,
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB,
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64,
        RelocationType.R_WASM_MEMORY_ADDR_I32,
        RelocationType.R_WASM_MEMORY_ADDR_I64,
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB,
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64,
        RelocationType.R_WASM_FUNCTION_OFFSET_I32,
        RelocationType.R_WASM_FUNCTION_OFFSET_I64,
        RelocationType.R_WASM_SECTION_OFFSET_I32,
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32,
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64,
    ];

    [Fact]
    public void HasAddendMatchesLlvmForEveryRelocationType()
    {
        var types = Enum.GetValues<RelocationType>().Distinct().ToList();
        Assert.Equal(28, types.Count);
        foreach (RelocationType type in types)
            Assert.True(s_llvmTypesWithAddend.Contains(type) == RelocationTypes.HasAddend(type), type.ToString());
    }

    [Theory]
    [InlineData(RelocationType.R_WASM_FUNCTION_INDEX_LEB, 5)]
    [InlineData(RelocationType.R_WASM_TABLE_INDEX_SLEB, 5)]
    [InlineData(RelocationType.R_WASM_TABLE_NUMBER_LEB, 5)]
    [InlineData(RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB, 5)]
    [InlineData(RelocationType.R_WASM_MEMORY_ADDR_LEB64, 10)]
    [InlineData(RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64, 10)]
    [InlineData(RelocationType.R_WASM_TABLE_INDEX_I32, 4)]
    [InlineData(RelocationType.R_WASM_FUNCTION_INDEX_I32, 4)]
    [InlineData(RelocationType.R_WASM_GLOBAL_INDEX_I32, 4)]
    [InlineData(RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32, 4)]
    [InlineData(RelocationType.R_WASM_TABLE_INDEX_I64, 8)]
    [InlineData(RelocationType.R_WASM_FUNCTION_OFFSET_I64, 8)]
    [InlineData(RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64, 8)]
    public void PatchSizeIsTheWidthOfTheRelocatedField(RelocationType type, int size) =>
        Assert.Equal(size, RelocationTypes.GetPatchSize(type));

    // ── reloc.* ─────────────────────────────────────────────────────────────

    [Fact]
    public void RelocationSectionEncodesTargetCountAndEntries()
    {
        var reloc = new RelocationSectionEncoder(targetSectionIndex: 3);
        reloc.Add(RelocationType.R_WASM_FUNCTION_INDEX_LEB, offset: 5, index: 1);
        reloc.Add(RelocationType.R_WASM_TYPE_INDEX_LEB, offset: 9, index: 0);
        reloc.Add(RelocationType.R_WASM_SECTION_OFFSET_I32, offset: 12, index: 2, addend: 0);
        reloc.Add(RelocationType.R_WASM_MEMORY_ADDR_SLEB, offset: 0x80, index: 4, addend: -1);

        Assert.Equal(4, reloc.Count);
        Assert.Equal(Convert.FromHexString("0304" + "000501" + "060900" + "090c02" + "00" + "04800104" + "7f"), Payload(reloc));
    }

    [Fact]
    public void RelocationWithoutAnAddendFieldRejectsANonzeroAddend()
    {
        var reloc = new RelocationSectionEncoder(0);
        Assert.Throws<ArgumentException>(() => reloc.Add(RelocationType.R_WASM_TABLE_INDEX_SLEB, 0, 0, addend: 1));
        Assert.Throws<ArgumentException>(() => reloc.Add(RelocationType.R_WASM_FUNCTION_INDEX_LEB, 0, 0, addend: -1));
        Assert.Equal(0, reloc.Count);
        reloc.Add(RelocationType.R_WASM_TABLE_INDEX_SLEB, 7, 2);
        Assert.Equal(Convert.FromHexString("0001" + "010702"), Payload(reloc));
    }

    [Fact]
    public void RelocationOffsetsMustNotGoBackwards()
    {
        // LLVM's reader (and so wasm-ld) rejects "relocations not in offset order"; equal offsets are accepted.
        var reloc = new RelocationSectionEncoder(0);
        reloc.Add(RelocationType.R_WASM_FUNCTION_INDEX_LEB, offset: 8, index: 0);
        reloc.Add(RelocationType.R_WASM_TABLE_INDEX_I32, offset: 8, index: 1);
        Assert.Throws<ArgumentException>(() => reloc.Add(RelocationType.R_WASM_FUNCTION_INDEX_LEB, offset: 7, index: 2));
        Assert.Equal(2, reloc.Count);
        reloc.Add(RelocationType.R_WASM_FUNCTION_INDEX_LEB, offset: 9, index: 3);
        Assert.Equal(Convert.FromHexString("0003" + "000800" + "020801" + "000903"), Payload(reloc));
    }

    [Fact]
    public void RelocationEncoderRejectsAnUnknownType()
    {
        var reloc = new RelocationSectionEncoder(0);
        Assert.Throws<ArgumentOutOfRangeException>(() => reloc.Add((RelocationType)28, offset: 0, index: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => reloc.Add((RelocationType)0xff, offset: 0, index: 0));
        Assert.Equal(0, reloc.Count);
        Assert.Equal(Convert.FromHexString("0000"), Payload(reloc));
    }

    // ── linking ─────────────────────────────────────────────────────────────

    [Fact]
    public void LinkingSectionMatchesTheRuntimeWriterEncoding()
    {
        var symbols = new SymbolTableEncoder();
        Assert.Equal(0, symbols.AddFunction(SymbolFlags.Undefined, 0));
        Assert.Equal(1, symbols.AddFunction(SymbolFlags.Undefined | SymbolFlags.ExplicitName, 1, "f"u8));
        Assert.Equal(2, symbols.AddFunction(SymbolFlags.Exported, 2, "g"u8));
        Assert.Equal(3, symbols.AddGlobal(SymbolFlags.Undefined, 0));
        Assert.Equal(4, symbols.AddData(SymbolFlags.None, "d"u8, segmentIndex: 0, offset: 4, size: 8));
        Assert.Equal(5, symbols.AddUndefinedData(SymbolFlags.Undefined, "u"u8));
        Assert.Equal(6, symbols.AddSection(SymbolFlags.BindingLocal, 7));
        Assert.Equal(7, symbols.AddTable(SymbolFlags.Undefined | SymbolFlags.ExplicitName, 0, "t"u8));
        Assert.Equal(8, symbols.AddTag(SymbolFlags.Undefined | SymbolFlags.ExplicitName, 0, "e"u8));

        var segments = new SegmentInfoEncoder();
        segments.Add(".data.x"u8, alignmentLog2: 2, SegmentFlags.None);

        var linking = new LinkingSectionEncoder();
        linking.WriteSymbolTable(symbols);
        linking.WriteSegmentInfo(segments);

        string symtab = "09" + "001000" + "0050010166" + "0020020167" + "021000" + "0100016400" + "0408" + "01100175" + "030207"
            + "0550000174" + "0450000165";
        string seginfo = "01" + "07" + Convert.ToHexString(".data.x"u8) + "0200";
        byte[] expected = Convert.FromHexString("02" + "08" + (symtab.Length / 2).ToString("x2") + symtab + "05" + (seginfo.Length / 2).ToString("x2") + seginfo);
        Assert.Equal(expected, Payload(linking));
        Assert.Equal(2u, LinkingSectionEncoder.Version);
        Assert.Equal(8, (int)LinkingSubsectionId.SymbolTable);
        Assert.Equal(5, (int)LinkingSubsectionId.SegmentInfo);
        Assert.Equal(6, (int)LinkingSubsectionId.InitFunctions);
        Assert.Equal(7, (int)LinkingSubsectionId.ComdatInfo);
    }

    [Fact]
    public void SymbolNameRuleIsEnforcedNotSilentlyApplied()
    {
        var symbols = new SymbolTableEncoder();
        // An import without an explicit name carries no name field; passing one would be silently dropped.
        Assert.Throws<ArgumentException>(() => symbols.AddFunction(SymbolFlags.Undefined, 0, "x"u8));
        // A defined data symbol must not be flagged undefined, and an undefined one must be.
        Assert.Throws<ArgumentException>(() => symbols.AddData(SymbolFlags.Undefined, "d"u8, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => symbols.AddUndefinedData(SymbolFlags.None, "d"u8));
        // Wherever the format writes a name it must be a real one, as LLVM's and runtime's writers always emit;
        // an omitted name on a defined symbol would otherwise become an empty one.
        Assert.Throws<ArgumentException>(() => symbols.AddFunction(SymbolFlags.None, 0));
        Assert.Throws<ArgumentException>(() => symbols.AddGlobal(SymbolFlags.BindingLocal, 0, default));
        Assert.Throws<ArgumentException>(() => symbols.AddTable(SymbolFlags.Undefined | SymbolFlags.ExplicitName, 0));
        Assert.Throws<ArgumentException>(() => symbols.AddData(SymbolFlags.None, ""u8, 0, 0, 0));
        Assert.Throws<ArgumentException>(() => symbols.AddUndefinedData(SymbolFlags.Undefined, ""u8));
        Assert.Throws<ArgumentException>(() => symbols.Add(new WasmSymbol { Type = SymbolType.Tag, Flags = SymbolFlags.None, Index = 0, Name = "" }));
        Assert.Equal(0, symbols.Count);
    }

    [Fact]
    public void InitFunctionsAndComdatsUseTheLlvmLayout()
    {
        var init = new InitFunctionsEncoder();
        init.Add(priority: 65535, symbolIndex: 3);
        var comdats = new ComdatInfoEncoder();
        comdats.Add("c"u8, flags: 0, [(ComdatKind.Function, 2), (ComdatKind.Data, 1)]);

        var linking = new LinkingSectionEncoder();
        linking.WriteInitFunctions(init);
        linking.WriteComdatInfo(comdats);
        Assert.Equal(Convert.FromHexString("02" + "0605" + "01ffff0303" + "0709" + "0101630002" + "0102" + "0001"), Payload(linking));

        // LLVM pads every subsection size field to five bytes.
        var padded = new LinkingSectionEncoder();
        padded.WriteInitFunctions(init, sizeFieldWidth: 5);
        Assert.Equal(Convert.FromHexString("02" + "06" + "8580808000" + "01ffff0303"), Payload(padded));
    }

    [Fact]
    public void LinkingModelRoundTripsThroughTheReader()
    {
        var model = new LinkingSection
        {
            Version = 2,
            Symbols =
            [
                new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.Undefined, Index = 0 },
                new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.VisibilityHidden, Index = 1, Name = "f" },
                new WasmSymbol { Type = SymbolType.Data, Flags = SymbolFlags.None, Name = "d", SegmentIndex = 0, Offset = 0, Size = 4 },
                new WasmSymbol { Type = SymbolType.Data, Flags = SymbolFlags.Undefined, Name = "u" },
                new WasmSymbol { Type = SymbolType.Section, Flags = SymbolFlags.BindingLocal, SectionIndex = 5 },
                // Longer than the encoder's stack scratch, so its UTF-8 bytes come from a pooled buffer.
                new WasmSymbol { Type = SymbolType.Global, Flags = SymbolFlags.BindingLocal, Index = 0, Name = new string('n', 300) + "é" },
            ],
            SegmentInfos = [new SegmentInfo { Name = ".data.d", Alignment = 2 }],
            InitFunctions = [new InitFunction { Priority = 1, SymbolIndex = 1 }],
            ComdatGroups = [new ComdatGroup { Name = "g", Entries = [new ComdatEntry { Kind = ComdatKind.Function, Index = 1 }] }],
        };

        byte[] bytes = BuildObject(writer => writer.WriteCustomSection("linking"u8, LinkingSectionEncoder.From(model)));
        LinkingSection parsed = ModuleReader.ParseCoreModule(bytes).LinkingSection!;
        Assert.Equal(model.Symbols.Select(Describe), parsed.Symbols.Select(Describe));
        Assert.Equal(".data.d/2", string.Join(",", parsed.SegmentInfos.Select(s => $"{s.Name}/{s.Alignment}")));
        Assert.Equal("1:1", string.Join(",", parsed.InitFunctions.Select(i => $"{i.Priority}:{i.SymbolIndex}")));
        Assert.Equal("g:Function1", string.Join(",", parsed.ComdatGroups.Select(g => $"{g.Name}:{string.Join("", g.Entries.Select(e => $"{e.Kind}{e.Index}"))}")));

        static string Describe(WasmSymbol s) => $"{s.Type} {s.Flags} {s.Name} {s.Index} {s.SegmentIndex} {s.Offset} {s.Size} {s.SectionIndex}";
    }

    // ── Module framing ──────────────────────────────────────────────────────

    [Fact]
    public void WriterFramesSectionsAndReportsTheirIndices()
    {
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output);
        Assert.Equal(8, writer.Position);
        Assert.Equal(0, writer.WriteSection(WasmSectionId.Type, Convert.FromHexString("01600000")));
        Assert.Equal(1, writer.WriteSection(WasmSectionId.Function, Convert.FromHexString("0100"), sizeFieldWidth: 5));
        using (var bodies = new MemoryStream(Convert.FromHexString("ff" + "02000b")) { Position = 1 })
            Assert.Equal(2, writer.WriteVectorSection(WasmSectionId.Code, 1, bodies));
        Assert.Equal(3, writer.WriteCustomSection("x"u8, Convert.FromHexString("aa")));
        Assert.Equal(4, writer.NextSectionIndex);

        byte[] expected = Convert.FromHexString(
            "0061736d01000000" + "0104" + "01600000" + "03" + "8280808000" + "0100" + "0a04" + "01" + "02000b" + "0003" + "0178" + "aa");
        Assert.Equal(expected, output.ToArray());
        Assert.Equal(expected.Length, writer.Position);
        ModuleReader.ReadModuleAndValidate(new MemoryStream(expected));
    }

    [Fact]
    public void StreamedSectionsNeverSeekTheOutput()
    {
        using var sink = new MemoryStream();
        using var output = new ForwardOnlyStream(sink);
        var writer = new WasmModuleWriter(output);
        Assert.Equal(0, writer.BeginCustomSection("name"u8, payloadSize: 3, sizeFieldWidth: 5));
        writer.Write([0x01]);
        WasmLeb128.WriteULeb128(writer.ContentStream, 0);
        writer.ContentStream.WriteByte(0);
        writer.EndSection();
        Assert.Equal(1, writer.BeginSection(WasmSectionId.Data, contentSize: 1));
        writer.Write([0x00]);
        writer.EndSection();
        Assert.Equal(Convert.FromHexString("0061736d01000000" + "00" + "8880808000" + "046e616d65" + "010000" + "0b0100"), sink.ToArray());
    }

    [Fact]
    public void WriterRejectsContentThatDoesNotMatchTheDeclaredSize()
    {
        var writer = new WasmModuleWriter(new MemoryStream());
        writer.BeginSection(WasmSectionId.Type, contentSize: 3);
        writer.Write([1, 2]);
        Assert.Throws<InvalidOperationException>(() => writer.BeginSection(WasmSectionId.Function, 0));
        Assert.Throws<InvalidOperationException>(writer.EndSection);
        Assert.Throws<InvalidOperationException>(() => writer.Write([3, 4]));

        var writer2 = new WasmModuleWriter(new MemoryStream());
        Assert.Throws<ArgumentException>(() => writer2.BeginSection(WasmSectionId.Type, contentSize: 128, sizeFieldWidth: 1));
        Assert.Throws<InvalidOperationException>(writer2.EndSection);
    }

    [Fact]
    public void WriterCanContinueAfterAHeaderWrittenByTheCaller()
    {
        using var output = new MemoryStream();
        output.Write(WasmModuleWriter.Header);
        var writer = new WasmModuleWriter(output, writeHeader: false, startOffset: output.Position);
        Assert.Equal(8, writer.Position);
        Assert.Equal(0, writer.WriteSection(WasmSectionId.DataCount, [0x00]));
        Assert.Equal(Convert.FromHexString("0061736d01000000" + "0c0100"), output.ToArray());
    }

    [Fact]
    public void WriterContinuesSectionNumberingAfterSectionsWrittenByTheCaller()
    {
        using var output = new MemoryStream();
        output.Write(WasmModuleWriter.Header);
        output.Write([0x01, 0x01, 0x00]); // the caller's own empty type section: file index 0
        var writer = new WasmModuleWriter(output, writeHeader: false, startOffset: output.Position, startSectionIndex: 1);
        Assert.Equal(1, writer.NextSectionIndex);
        Assert.Equal(1, writer.WriteSection(WasmSectionId.Function, [0x00]));
        Assert.Equal(2, writer.NextSectionIndex);
        ModuleReader.ReadModuleAndValidate(new MemoryStream(output.ToArray()));
        Assert.Throws<ArgumentOutOfRangeException>(() => new WasmModuleWriter(Stream.Null, startSectionIndex: -1));
    }

    [Fact]
    public void WritingFromAStreamDoesNotAllocateABufferPerCall()
    {
        const int Calls = 1000;
        var sources = new MemoryStream[Calls + 1];
        for (int i = 0; i < sources.Length; i++)
            sources[i] = new MemoryStream([(byte)i], writable: false);

        var writer = new WasmModuleWriter(Stream.Null, writeHeader: false);
        writer.BeginSection(WasmSectionId.Code, sources.Length);
        writer.Write(sources[0]);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 1; i <= Calls; i++)
            writer.Write(sources[i]);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        writer.EndSection();

        // A per-call copy buffer (80 KiB each) allocated ~80 MB here; allow incidental bytes, never a buffer per call.
        Assert.True(allocated < Calls * 64, $"{Calls} Write(Stream) calls allocated {allocated} bytes");
    }

    // ── Webcil alignment padding ────────────────────────────────────────────

    [Theory]
    [InlineData(8, 16, "0006" + "000000000000")]                           // 8 bytes of padding
    [InlineData(15, 16, "000f" + "000000000000000000000000000000")]         // 1 byte is too small for a section: add one alignment
    [InlineData(16, 16, "")]                                                 // already aligned
    [InlineData(9, 1, "")]                                                   // alignment 1 never pads
    public void PaddingSectionMatchesTheRuntimeLayout(int offset, int alignment, string expectedHex)
    {
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false, startOffset: offset);
        int index = writer.WritePaddingSection(alignment);
        Assert.Equal(Convert.FromHexString(expectedHex), output.ToArray());
        Assert.Equal(expectedHex.Length == 0 ? -1 : 0, index);
        Assert.Equal(0, writer.Position % alignment);
    }

    [Fact]
    public void PaddingSectionPadsItsSizeFieldWhenTheMinimalOneDoesNotFit()
    {
        // 130 bytes of padding: a one-byte size field leaves a 128-byte payload whose size needs two bytes.
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false, startOffset: 126);
        Assert.Equal(130, WasmModuleWriter.GetPaddingSectionSize(256, 126));
        writer.WritePaddingSection(256);
        byte[] bytes = output.ToArray();
        Assert.Equal(130, bytes.Length);
        Assert.Equal(Convert.FromHexString("00ff00"), bytes[..3]);
        Assert.All(bytes[3..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void PaddingSectionAlignsEveryOffset()
    {
        int padded = 0, aligned = 0;
        foreach (int alignment in (int[])[2, 4, 8, 16, 32, 256, 4096])
        {
            for (int offset = 8; offset < 8 + 2 * alignment; offset++)
            {
                using var output = new MemoryStream();
                var writer = new WasmModuleWriter(output, writeHeader: false, startOffset: offset);
                writer.WritePaddingSection(alignment);
                byte[] bytes = output.ToArray();
                Assert.True(writer.Position % alignment == 0, $"offset {offset}, alignment {alignment}");
                Assert.Equal(WasmModuleWriter.GetPaddingSectionSize(alignment, offset), bytes.Length);
                if (bytes.Length == 0)
                {
                    aligned++;
                    continue;
                }

                padded++;
                Assert.True(bytes.Length >= 3, $"offset {offset}, alignment {alignment}");
                // A custom section with an empty name and an all-zero payload, sized exactly to the padding.
                ulong size = ObjectReencoder.ReadULeb(bytes, 1, out int width);
                Assert.Equal(0, bytes[0]);
                Assert.Equal(bytes.Length, 1 + width + (int)size);
                Assert.All(bytes[(1 + width)..], b => Assert.Equal(0, b));
            }
        }

        // 7 alignments over two periods each: two aligned offsets per alignment, the rest padded.
        Assert.Equal(14, aligned);
        Assert.Equal(2 * (2 + 4 + 8 + 16 + 32 + 256 + 4096) - 14, padded);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static byte[] Payload(IWasmSectionPayload payload)
    {
        using var stream = new MemoryStream();
        payload.WriteTo(stream);
        Assert.Equal(stream.Length, payload.Length);
        return stream.ToArray();
    }

    private static byte[] BuildObject(Action<WasmModuleWriter> write)
    {
        using var output = new MemoryStream();
        write(new WasmModuleWriter(output));
        return output.ToArray();
    }

    private sealed class ForwardOnlyStream(Stream inner) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => inner.Write(buffer, offset, count);
        public override void Write(ReadOnlySpan<byte> buffer) => inner.Write(buffer);
    }
}
