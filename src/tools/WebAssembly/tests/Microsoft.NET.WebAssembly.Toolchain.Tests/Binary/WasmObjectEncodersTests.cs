// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// The remaining encoders dotnet/runtime's ObjectWriter needs (#592): data segment headers, name maps, LEB128 to an
/// <see cref="IBufferWriter{T}"/> and from a <see cref="Stream"/>. Expected bytes are written out from the format
/// (core spec §5.5.12, appendix 7.4) and from what runtime's <c>WasmDataSegmentEncoding</c>/<c>WasmNameSection</c>
/// emit, not derived from the encoders under test; <see cref="ObjectWriterReferenceTests"/> covers real outputs.
/// </summary>
public sealed class WasmObjectEncodersTests
{
    // ── Data segments ───────────────────────────────────────────────────────

    [Theory]
    // runtime's relocatable writer: flag 0, i32.const padded to 5 bytes, end, size padded to 5 bytes.
    [InlineData("i32", 16, 5, 3, 5, "00" + "41" + "9080808000" + "0b" + "8380808000")]
    [InlineData("i32", -1, 5, 0, 5, "00" + "41" + "ffffffff7f" + "0b" + "8080808000")]
    // Minimal encodings, as clang writes objects.
    [InlineData("i32", 1024, 0, 300, 0, "00" + "41" + "8008" + "0b" + "ac02")]
    // runtime's Webcil writer: the payload is placed at an imported base global.
    [InlineData("global", 1, 0, 200, 5, "00" + "23" + "01" + "0b" + "c881808000")]
    // A 64-bit memory: clang's wasm64 objects use i64.const (minimal), a relocated one is padded to 10 bytes.
    [InlineData("i64", 16, 0, 300, 0, "00" + "42" + "10" + "0b" + "ac02")]
    [InlineData("i64", -1, 10, 0, 5, "00" + "42" + "ffffffffffffffffff7f" + "0b" + "8080808000")]
    public void ActiveHeaderMatchesTheReferenceEncoding(string kind, int value, int valueWidth, int contentSize, int sizeFieldWidth, string expectedHex)
    {
        Span<byte> expression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        int expressionLength = kind switch
        {
            "i32" => DataSegmentEncoder.EncodeI32ConstExpression(expression, value, valueWidth),
            "i64" => DataSegmentEncoder.EncodeI64ConstExpression(expression, value, valueWidth),
            _ => DataSegmentEncoder.EncodeGlobalGetExpression(expression, (uint)value, valueWidth),
        };
        expression = expression[..expressionLength];

        byte[] expected = Convert.FromHexString(expectedHex);
        Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        int length = DataSegmentEncoder.EncodeActiveHeader(header, expression, contentSize, sizeFieldWidth);
        Assert.Equal(expected, header[..length].ToArray());
        Assert.Equal(expected.Length, DataSegmentEncoder.GetActiveHeaderSize(expression, contentSize, sizeFieldWidth));
    }

    [Theory]
    [InlineData(8, 5, "01" + "8880808000")]
    [InlineData(8, 0, "01" + "08")]
    [InlineData(200, 2, "01" + "c801")]
    public void PassiveHeaderMatchesTheReferenceEncoding(int contentSize, int sizeFieldWidth, string expectedHex)
    {
        byte[] expected = Convert.FromHexString(expectedHex);
        Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        int length = DataSegmentEncoder.EncodePassiveHeader(header, contentSize, sizeFieldWidth);
        Assert.Equal(expected, header[..length].ToArray());
        Assert.Equal(expected.Length, DataSegmentEncoder.GetPassiveHeaderSize(contentSize, sizeFieldWidth));
    }

    [Fact]
    public void ActiveHeaderForAnotherMemoryUsesFlagTwo()
    {
        Span<byte> expression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        expression = expression[..DataSegmentEncoder.EncodeI32ConstExpression(expression, 0)];
        Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        int length = DataSegmentEncoder.EncodeActiveHeader(header, expression, 1, memoryIndex: 1);
        Assert.Equal(Convert.FromHexString("02" + "01" + "4100" + "0b" + "01"), header[..length].ToArray());
    }

    [Fact]
    public void HeadersRejectWhatTheyCannotEncode()
    {
        byte[] header = new byte[DataSegmentEncoder.MaxHeaderLength];
        // An offset expression must be terminated, or the size would be read as part of it.
        Assert.Throws<ArgumentException>(() => DataSegmentEncoder.EncodeActiveHeader(header, [0x41, 0x00], 1));
        Assert.Throws<ArgumentException>(() => DataSegmentEncoder.EncodeActiveHeader(header, [], 1));
        Assert.Throws<ArgumentException>(() => DataSegmentEncoder.EncodePassiveHeader(header, 128, sizeFieldWidth: 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSegmentEncoder.EncodePassiveHeader(header, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSegmentEncoder.EncodePassiveHeader(header, 1, sizeFieldWidth: 6));
        Assert.Throws<ArgumentException>(() => DataSegmentEncoder.EncodeI32ConstExpression(new byte[16], 1 << 20, width: 2));
    }

    [Fact]
    public void OffsetImmediatesAreNoWiderThanTheirType()
    {
        // An i32.const or a global index is at most 5 bytes (core spec §5.2.2); a 6-byte one is a malformed module.
        byte[] expression = new byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSegmentEncoder.EncodeI32ConstExpression(expression, 64, width: 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSegmentEncoder.EncodeGlobalGetExpression(expression, 1, width: 6));
        Assert.Throws<ArgumentOutOfRangeException>(() => DataSegmentEncoder.EncodeI64ConstExpression(expression, 64, width: 11));
        Assert.Equal(DataSegmentEncoder.MaxOffsetExpressionLength, DataSegmentEncoder.EncodeI64ConstExpression(expression, 64, width: 10));
    }

    [Fact]
    public void SegmentsWrittenThroughTheEncoderParse()
    {
        using var data = new MemoryStream();
        Span<byte> buffer = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        Span<byte> expression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        WasmLeb128.WriteULeb128(data, 2);
        int expressionLength = DataSegmentEncoder.EncodeI32ConstExpression(expression, 64, WasmLeb128.PaddedLength32);
        data.Write(buffer[..DataSegmentEncoder.EncodeActiveHeader(buffer, expression[..expressionLength], 3, WasmLeb128.PaddedLength32)]);
        data.Write([1, 2, 3]);
        data.Write(buffer[..DataSegmentEncoder.EncodePassiveHeader(buffer, 2, WasmLeb128.PaddedLength32)]);
        data.Write([4, 5]);

        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output);
        writer.WriteSection(WasmSectionId.Memory, Convert.FromHexString("010001"));
        writer.WriteSection(WasmSectionId.DataCount, [0x02]);
        writer.WriteSection(WasmSectionId.Data, data.ToArray(), sizeFieldWidth: 5);
        WasmModule module = ModuleReader.ReadModuleAndValidate(new MemoryStream(output.ToArray()));

        Assert.Equal(2, module.DataSegments.Count);
        Assert.Equal([1, 2, 3], module.DataSegments[0].InitData);
        Assert.Equal(Convert.FromHexString("41" + "c080808000" + "0b"), module.DataSegments[0].OffsetExpr);
        Assert.Equal([4, 5], module.DataSegments[1].InitData);
    }

    // ── Name maps ───────────────────────────────────────────────────────────

    [Fact]
    public void NameSubsectionIdsArePublic()
    {
        Assert.Equal(0, (int)NameSubsectionId.Module);
        Assert.Equal(1, (int)NameSubsectionId.Function);
        Assert.Equal(2, (int)NameSubsectionId.Local);
        Assert.Equal(7, (int)NameSubsectionId.Global);
        Assert.Equal(9, (int)NameSubsectionId.DataSegment);
    }

    [Fact]
    public void NameMapStreamsTheRuntimeLayout()
    {
        (uint Index, byte[] Name)[] names = [(0, "f"u8.ToArray()), (3, "main"u8.ToArray()), (200, "g"u8.ToArray())];
        long entries = names.Sum(n => NameMapWriter.GetEntrySize(n.Index, n.Name.Length));
        long subsection = NameMapWriter.GetSubsectionSize(names.Length, entries);

        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false);
        writer.BeginCustomSection("name"u8, subsection);
        var map = new NameMapWriter(writer, NameSubsectionId.Function, names.Length, entries);
        foreach ((uint index, byte[] name) in names)
            map.Write(index, name);
        map.Complete();
        writer.EndSection();

        string payload = "01" + "0e" + "03" + "0001" + "66" + "0304" + "6d61696e" + "c80101" + "67";
        Assert.Equal(Convert.FromHexString("00" + "15" + "04" + "6e616d65" + payload), output.ToArray());
        Assert.Equal(payload.Length / 2, subsection);
    }

    [Theory]
    [InlineData(NameSubsectionId.Module)]    // a single name
    [InlineData(NameSubsectionId.Local)]     // index → name map maps
    [InlineData(NameSubsectionId.Label)]
    [InlineData(NameSubsectionId.Field)]
    public void NameMapRejectsSubsectionsThatAreNotNameMaps(NameSubsectionId id)
    {
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false);
        writer.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(1, NameMapWriter.GetEntrySize(0, 1)));
        long before = output.Length;
        Assert.Throws<ArgumentException>(() => new NameMapWriter(writer, id, 1, NameMapWriter.GetEntrySize(0, 1)));
        Assert.Equal(before, output.Length);
    }

    [Theory]
    [InlineData(NameSubsectionId.Function)]
    [InlineData(NameSubsectionId.Type)]
    [InlineData(NameSubsectionId.Table)]
    [InlineData(NameSubsectionId.Memory)]
    [InlineData(NameSubsectionId.Global)]
    [InlineData(NameSubsectionId.ElemSegment)]
    [InlineData(NameSubsectionId.DataSegment)]
    [InlineData(NameSubsectionId.Tag)]
    public void NameMapAcceptsEveryFlatNameMap(NameSubsectionId id)
    {
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false);
        long entries = NameMapWriter.GetEntrySize(0, 1);
        writer.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(1, entries));
        var map = new NameMapWriter(writer, id, 1, entries);
        map.Write(0, "x"u8);
        map.Complete();
        writer.EndSection();
        Assert.Equal((byte)id, output.ToArray()[^6]);
    }

    [Fact]
    public void NameMapEnforcesOrderAndCount()
    {
        var writer = new WasmModuleWriter(new MemoryStream(), writeHeader: false);
        long entries = NameMapWriter.GetEntrySize(5, 1) + NameMapWriter.GetEntrySize(4, 1);
        writer.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(2, entries));
        var map = new NameMapWriter(writer, NameSubsectionId.Function, 2, entries);
        map.Write(5, "a"u8);
        // Name maps must be sorted by index and duplicate-free. An index out of order is a bad argument, as a
        // relocation offset out of order is; an entry beyond the declared count or size is a call the map's state
        // does not allow.
        Assert.Throws<ArgumentException>("index", () => map.Write(4, "b"u8));
        Assert.Throws<ArgumentException>("index", () => map.Write(5, "b"u8));
        Assert.Throws<InvalidOperationException>(map.Complete);
        map.Write(6, "b"u8);
        Assert.Throws<InvalidOperationException>(() => map.Write(7, "c"u8));
        map.Complete();
        writer.EndSection();

        // An entry larger than the map's declared size is caught by the map, before any of it is written. The section
        // has room to spare, so the module writer's own overrun check cannot be what throws.
        using var sized = new MemoryStream();
        var roomy = new WasmModuleWriter(sized, writeHeader: false);
        roomy.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(1, 3) + 64);
        var tooBig = new NameMapWriter(roomy, NameSubsectionId.Function, 1, 3);
        long before = sized.Length;
        Assert.Throws<InvalidOperationException>(() => tooBig.Write(0, "long"u8));
        Assert.Equal(before, sized.Length);
    }

    [Fact]
    public void NameMapRoundTripsThroughTheReader()
    {
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output);
        writer.WriteSection(WasmSectionId.Type, Convert.FromHexString("01600000"));
        writer.WriteSection(WasmSectionId.Function, Convert.FromHexString("020000"));
        writer.WriteSection(WasmSectionId.Code, Convert.FromHexString("02" + "02000b" + "02000b"));
        long entries = NameMapWriter.GetEntrySize(0, 5) + NameMapWriter.GetEntrySize(1, 6);
        writer.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(2, entries));
        var map = new NameMapWriter(writer, NameSubsectionId.Function, 2, entries);
        map.Write(0, "first"u8);
        map.Write(1, "second"u8);
        map.Complete();
        writer.EndSection();

        WasmModule module = ModuleReader.ReadModuleAndValidate(new MemoryStream(output.ToArray()));
        Assert.Equal(["0:first", "1:second"], WasmNameSectionReader.ParseFunctionNames(module).OrderBy(p => p.Key).Select(p => $"{p.Key}:{p.Value}"));
    }

    [Fact]
    public void NameMapWritesTheSameBytesToABufferWriter()
    {
        long entries = NameMapWriter.GetEntrySize(0, 1) + NameMapWriter.GetEntrySize(300, 4);
        var buffer = new ArrayBufferWriter<byte>();
        var map = new NameMapWriter(buffer, NameSubsectionId.Global, 2, entries);
        map.Write(0, "g"u8);
        map.Write(300, "heap"u8);
        map.Complete();

        Assert.Equal(Convert.FromHexString("07" + "0b" + "02" + "0001" + "67" + "ac0204" + "68656170"), buffer.WrittenSpan.ToArray());
        Assert.Equal(NameMapWriter.GetSubsectionSize(2, entries), buffer.WrittenCount);
    }

    [Fact]
    public void NameMapOverABufferWriterChecksBeforeWriting()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>("id", () => new NameMapWriter(buffer, NameSubsectionId.Local, 1, NameMapWriter.GetEntrySize(0, 1)));
        Assert.Equal(0, buffer.WrittenCount);

        // A buffer writer has no declared section size to catch an overrun, so the map's own checks are all there is.
        var map = new NameMapWriter(buffer, NameSubsectionId.Function, 1, NameMapWriter.GetEntrySize(0, 1));
        int header = buffer.WrittenCount;
        Assert.Throws<InvalidOperationException>(() => map.Write(0, "long"u8));
        Assert.Equal(header, buffer.WrittenCount);
        map.Write(0, "x"u8);
        Assert.Throws<InvalidOperationException>(() => map.Write(1, "y"u8));
        Assert.Equal(header + NameMapWriter.GetEntrySize(0, 1), buffer.WrittenCount);
        map.Complete();
    }

    [Theory]
    [InlineData(0, (long)uint.MaxValue)]
    [InlineData(128, (long)uint.MaxValue - 1)]
    [InlineData(1, long.MaxValue)]
    public void NameMapsRejectAnOversizedSubsectionBeforeWriting(int count, long entriesSize)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentOutOfRangeException>("entriesSize",
            () => new NameMapWriter(buffer, NameSubsectionId.Function, count, entriesSize));
        Assert.Throws<ArgumentOutOfRangeException>("entriesSize",
            () => new IndirectNameMapWriter(buffer, NameSubsectionId.Local, count, entriesSize));
        Assert.Equal(0, buffer.WrittenCount);
    }

    [Fact]
    public void NameMapsAcceptTheMaximumU32PayloadSize()
    {
        var flat = new ArrayBufferWriter<byte>();
        _ = new NameMapWriter(flat, NameSubsectionId.Function, 1, (long)uint.MaxValue - 1);
        Assert.Equal(Convert.FromHexString("01ffffffff0f01"), flat.WrittenSpan.ToArray());

        var indirect = new ArrayBufferWriter<byte>();
        _ = new IndirectNameMapWriter(indirect, NameSubsectionId.Local, 1, (long)uint.MaxValue - 1);
        Assert.Equal(Convert.FromHexString("02ffffffff0f01"), indirect.WrittenSpan.ToArray());
    }

    [Fact]
    public void ModuleNameWritesSubsectionZero()
    {
        var buffer = new ArrayBufferWriter<byte>();
        ModuleNameWriter.Write(buffer, "mod"u8);
        Assert.Equal(Convert.FromHexString("00" + "04" + "03" + "6d6f64"), buffer.WrittenSpan.ToArray());
        Assert.Equal(ModuleNameWriter.GetSubsectionSize(3), buffer.WrittenCount);

        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output, writeHeader: false);
        writer.BeginCustomSection("name"u8, ModuleNameWriter.GetSubsectionSize(3));
        ModuleNameWriter.Write(writer, "mod"u8);
        writer.EndSection();
        Assert.Equal(Convert.FromHexString("00" + "0b" + "04" + "6e616d65" + "00" + "04" + "03" + "6d6f64"), output.ToArray());
    }

    [Fact]
    public void IndirectNameMapWritesLocalNames()
    {
        // Function 0 names locals 0 and 2; function 5 names local 1.
        long f0 = NameMapWriter.GetEntrySize(0, 1) + NameMapWriter.GetEntrySize(2, 2);
        long f5 = NameMapWriter.GetEntrySize(1, 1);
        long entries = IndirectNameMapWriter.GetEntrySize(0, 2, f0) + IndirectNameMapWriter.GetEntrySize(5, 1, f5);
        var buffer = new ArrayBufferWriter<byte>();
        var map = new IndirectNameMapWriter(buffer, NameSubsectionId.Local, 2, entries);
        map.BeginMap(0, 2, f0);
        map.Write(0, "a"u8);
        map.Write(2, "bc"u8);
        map.BeginMap(5, 1, f5);
        map.Write(1, "d"u8);
        map.Complete();

        string payload = "02" + "00" + "02" + "0001" + "61" + "0202" + "6263" + "05" + "01" + "0101" + "64";
        Assert.Equal(Convert.FromHexString("02" + (payload.Length / 2).ToString("x2") + payload), buffer.WrittenSpan.ToArray());
        Assert.Equal(IndirectNameMapWriter.GetSubsectionSize(2, entries), buffer.WrittenCount);

        var names = WasmNameSectionReader.Read(buffer.WrittenSpan);
        Assert.Null(names.Malformation);
        Assert.Equal("bc", names.LocalNames[0][2]);
        Assert.Equal("d", names.LocalNames[5][1]);
    }

    [Theory]
    [InlineData(NameSubsectionId.Module)]
    [InlineData(NameSubsectionId.Function)]
    [InlineData(NameSubsectionId.Type)]
    [InlineData(NameSubsectionId.Tag)]
    public void IndirectNameMapRejectsSubsectionsThatAreNotIndirect(NameSubsectionId id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>("id", () => new IndirectNameMapWriter(buffer, id, 0, 0));
        Assert.Equal(0, buffer.WrittenCount);
    }

    [Theory]
    [InlineData(NameSubsectionId.Local)]
    [InlineData(NameSubsectionId.Label)]
    [InlineData(NameSubsectionId.Field)]
    public void IndirectNameMapAcceptsEveryIndirectMap(NameSubsectionId id)
    {
        var buffer = new ArrayBufferWriter<byte>();
        var map = new IndirectNameMapWriter(buffer, id, 0, 0);
        map.Complete();
        Assert.Equal([(byte)id, 1, 0], buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void IndirectNameMapEnforcesOrderCountsAndSizes()
    {
        long inner = NameMapWriter.GetEntrySize(0, 1) + NameMapWriter.GetEntrySize(1, 1);
        long entries = IndirectNameMapWriter.GetEntrySize(3, 2, inner) + IndirectNameMapWriter.GetEntrySize(4, 1, NameMapWriter.GetEntrySize(0, 1));
        var buffer = new ArrayBufferWriter<byte>();
        var map = new IndirectNameMapWriter(buffer, NameSubsectionId.Local, 2, entries);

        // Every rejected call writes nothing.
        int written = buffer.WrittenCount;
        Assert.Contains("BeginMap first", Assert.Throws<InvalidOperationException>(() => map.Write(0, "a"u8)).Message);
        Assert.Throws<InvalidOperationException>(() => map.BeginMap(3, 2, entries));
        Assert.Equal(written, buffer.WrittenCount);

        map.BeginMap(3, 2, inner);
        map.Write(1, "a"u8);
        written = buffer.WrittenCount;
        Assert.Throws<ArgumentException>("index", () => map.Write(1, "b"u8));
        Assert.Throws<ArgumentException>("index", () => map.Write(0, "b"u8));
        // The inner map still owes an entry, so neither the next map nor the end may come yet.
        Assert.Throws<InvalidOperationException>(() => map.BeginMap(4, 1, NameMapWriter.GetEntrySize(0, 1)));
        Assert.Throws<InvalidOperationException>(map.Complete);
        Assert.Equal(written, buffer.WrittenCount);
        map.Write(2, "b"u8);

        Assert.Throws<ArgumentException>("index", () => map.BeginMap(3, 1, NameMapWriter.GetEntrySize(0, 1)));
        map.BeginMap(4, 1, NameMapWriter.GetEntrySize(0, 1));
        Assert.Throws<InvalidOperationException>(() => map.Write(0, "ab"u8));
        map.Write(0, "c"u8);
        Assert.Throws<InvalidOperationException>(() => map.Write(1, "d"u8));
        Assert.Throws<InvalidOperationException>(() => map.BeginMap(5, 0, 0));
        map.Complete();
    }

    [Fact]
    public void ExactBufferRejectsEmptyReservationsWhenFull()
    {
        var buffer = new ExactArrayBufferWriter(1);
        buffer.GetSpan()[0] = 1;
        Assert.Equal(1, buffer.GetSpan().Length);
        buffer.Advance(1);
        Assert.Throws<InvalidOperationException>(() => buffer.GetSpan());
        Assert.Throws<InvalidOperationException>(() => buffer.GetMemory());
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetSpan(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => buffer.GetMemory(-1));
        Assert.Equal([1], buffer.ToArray());
    }

    [Theory]
    [InlineData(-1)]
    [InlineData((long)int.MaxValue)]
    [InlineData(long.MaxValue)]
    public void ExactBufferRejectsUnallocatableSizes(long size)
    {
        Assert.Throws<ArgumentOutOfRangeException>("size", () => new ExactArrayBufferWriter(size));
    }

    // ── LEB128 and IBufferWriter / Stream ───────────────────────────────────

    [Theory]
    [InlineData(0UL)]
    [InlineData(127UL)]
    [InlineData(128UL)]
    [InlineData(uint.MaxValue)]
    [InlineData(ulong.MaxValue)]
    public void BufferWriterOverloadsMatchTheSpanEncoders(ulong value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Equal(WasmLeb128.GetULeb128Size(value), WasmLeb128.WriteULeb128(buffer, value));
        Assert.Equal(WasmLeb128.GetSLeb128Size((long)value), WasmLeb128.WriteSLeb128(buffer, (long)value));
        Assert.Equal(WasmLeb128.PaddedLength64, WasmLeb128.WritePaddedULeb128(buffer, value, WasmLeb128.PaddedLength64));
        Assert.Equal(WasmLeb128.PaddedLength64, WasmLeb128.WritePaddedSLeb128(buffer, (long)value, WasmLeb128.PaddedLength64));

        byte[] expected = new byte[4 * WasmLeb128.MaxLength];
        int length = WasmLeb128.WriteULeb128(expected, value);
        length += WasmLeb128.WriteSLeb128(expected.AsSpan(length), (long)value);
        WasmLeb128.WritePaddedULeb128(expected.AsSpan(length, WasmLeb128.PaddedLength64), value);
        length += WasmLeb128.PaddedLength64;
        WasmLeb128.WritePaddedSLeb128(expected.AsSpan(length, WasmLeb128.PaddedLength64), (long)value);
        length += WasmLeb128.PaddedLength64;
        Assert.Equal(expected[..length], buffer.WrittenSpan.ToArray());
    }

    [Fact]
    public void PaddedBufferWriterOverloadRejectsAValueWiderThanTheField()
    {
        var buffer = new ArrayBufferWriter<byte>();
        Assert.Throws<ArgumentException>(() => WasmLeb128.WritePaddedULeb128(buffer, 1UL << 14, 2));
        Assert.Equal(0, buffer.WrittenCount);
    }

    [Theory]
    [InlineData("e58e26", 624485UL, 3)]
    [InlineData("8380808000", 3UL, 5)]   // runtime's padded code-blob sizes
    [InlineData("ffffffff0f", uint.MaxValue, 5)]
    public void StreamReadersConsumeOneValue(string hex, ulong expected, int length)
    {
        byte[] bytes = [.. Convert.FromHexString(hex), 0x2A];
        using var stream = new MemoryStream(bytes);
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeU32(stream, out uint value, out int read));
        Assert.Equal((uint)expected, value);
        Assert.Equal(length, read);
        Assert.Equal(length, stream.Position);

        stream.Position = 0;
        Assert.Equal(expected, WasmLeb128.ReadU64(stream));
        Assert.Equal(0x2A, stream.ReadByte());
        stream.Position = 0;
        Assert.Equal((uint)expected, WasmLeb128.ReadU32(stream));
    }

    [Fact]
    public void StreamReadersTellTheEndOfTheStreamFromATruncatedValue()
    {
        // Runtime's code-blob parser loops until the stream ends between values.
        using var empty = new MemoryStream([]);
        Assert.Equal(Leb128Status.Truncated, WasmLeb128.DecodeU64(empty, out _, out int none));
        Assert.Equal(0, none);

        using var truncated = new MemoryStream([0x80, 0x80]);
        Assert.Equal(Leb128Status.Truncated, WasmLeb128.DecodeU64(truncated, out _, out int some));
        Assert.Equal(2, some);
        truncated.Position = 0;
        Assert.Throws<WasmFormatException>(() => WasmLeb128.ReadU32(truncated));

        // Byte 5 of a u32 sets bits beyond bit 31.
        using var overflow = new MemoryStream(Convert.FromHexString("ffffffff1f"));
        Assert.Equal(Leb128Status.Overflow, WasmLeb128.DecodeU32(overflow, out _, out _));
        overflow.Position = 0;
        Assert.Throws<WasmFormatException>(() => WasmLeb128.ReadU32(overflow));

        // Signed values, sign-extended from the last byte.
        using var signed = new MemoryStream(Convert.FromHexString("c0bb78" + "7f"));
        Assert.Equal(-123456L, WasmLeb128.ReadS64(signed));
        Assert.Equal(-1, WasmLeb128.ReadS32(signed));
    }

    [Theory]
    // u32: five continuation bytes; the sixth is never read.
    [InlineData("8080808080" + "00", 32, 5)]
    // u64: ten continuation bytes; the eleventh is never read.
    [InlineData("80808080808080808080" + "00", 64, 10)]
    public void StreamReadersReportAnEncodingLongerThanTheWidthAsTooLong(string hex, int bits, int examined)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        Leb128Status status = bits == 32
            ? WasmLeb128.DecodeU32(stream, out _, out int length)
            : WasmLeb128.DecodeU64(stream, out _, out length);
        Assert.Equal(Leb128Status.TooLong, status);
        Assert.Equal(examined, length);
        Assert.Equal(examined, stream.Position);

        stream.Position = 0;
        Assert.Throws<WasmFormatException>(() => bits == 32 ? WasmLeb128.ReadU32(stream) : (long)WasmLeb128.ReadU64(stream));
    }

    [Theory]
    [InlineData("e58e26" + "2a", true, 624485u, 3)]
    [InlineData("8080", false, 0u, 2)]                // truncated
    [InlineData("8080808080" + "00", false, 0u, 5)]   // too long
    [InlineData("ffffffff1f", false, 0u, 5)]          // out of range
    public void TryReadU32FromAStreamKeepsItsContract(string hex, bool ok, uint expected, int consumed)
    {
        using var stream = new MemoryStream(Convert.FromHexString(hex));
        Assert.Equal(ok, WasmLeb128.TryReadU32(stream, out uint value));
        Assert.Equal(expected, value);
        Assert.Equal(consumed, stream.Position);
    }
}
