// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

public class WasmBinaryReaderTests
{
    private static WasmBinaryReader ReaderFrom(params byte[] data)
        => new WasmBinaryReader(new MemoryStream(data));

    #region ReadByte / ReadBytes

    [Fact]
    public void ReadByte_ReturnsByte()
    {
        var reader = ReaderFrom(0x42);
        Assert.Equal(0x42, reader.ReadByte());
    }

    [Fact]
    public void ReadByte_AtEndOfStream_ThrowsWasmFormatException()
    {
        var reader = ReaderFrom();
        Assert.Throws<WasmFormatException>(() => reader.ReadByte());
    }

    [Fact]
    public void ReadBytes_ReturnsExactBytes()
    {
        var reader = ReaderFrom(0x01, 0x02, 0x03);
        var result = reader.ReadBytes(3);
        Assert.Equal(new byte[] { 0x01, 0x02, 0x03 }, result);
    }

    [Fact]
    public void ReadBytes_InsufficientData_ThrowsWasmFormatException()
    {
        var reader = ReaderFrom(0x01);
        Assert.Throws<WasmFormatException>(() => reader.ReadBytes(5));
    }

    #endregion

    #region ReadU32Leb128

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0u)]
    [InlineData(new byte[] { 0x01 }, 1u)]
    [InlineData(new byte[] { 0x7F }, 127u)]
    public void ReadU32Leb128_SingleByte(byte[] data, uint expected)
    {
        var reader = ReaderFrom(data);
        Assert.Equal(expected, reader.ReadU32Leb128());
    }

    [Theory]
    [InlineData(new byte[] { 0x80, 0x01 }, 128u)]
    [InlineData(new byte[] { 0xE5, 0x8E, 0x26 }, 624485u)]
    public void ReadU32Leb128_MultiByte(byte[] data, uint expected)
    {
        var reader = ReaderFrom(data);
        Assert.Equal(expected, reader.ReadU32Leb128());
    }

    [Fact]
    public void ReadU32Leb128_MaxValue()
    {
        // 0xFFFFFFFF encoded in LEB128: 5 bytes all with continuation except last
        var reader = ReaderFrom(0xFF, 0xFF, 0xFF, 0xFF, 0x0F);
        Assert.Equal(uint.MaxValue, reader.ReadU32Leb128());
    }

    [Fact]
    public void ReadU32Leb128_Overflow_ThrowsWasmFormatException()
    {
        // 5th byte has bits set above the low nibble → overflow
        var reader = ReaderFrom(0xFF, 0xFF, 0xFF, 0xFF, 0x1F);
        Assert.Throws<WasmFormatException>(() => reader.ReadU32Leb128());
    }

    [Fact]
    public void ReadU32Leb128_TooManyBytes_ThrowsWasmFormatException()
    {
        // 6 continuation bytes → exceeds 5-byte limit
        var reader = ReaderFrom(0x80, 0x80, 0x80, 0x80, 0x80, 0x01);
        Assert.Throws<WasmFormatException>(() => reader.ReadU32Leb128());
    }

    [Fact]
    public void ReadU32Leb128_UnexpectedEof_ThrowsWasmFormatException()
    {
        // Continuation bit set but no more bytes
        var reader = ReaderFrom(0x80);
        Assert.Throws<WasmFormatException>(() => reader.ReadU32Leb128());
    }

    #endregion

    #region ReadS32Leb128

    [Theory]
    [InlineData(new byte[] { 0x00 }, 0)]
    [InlineData(new byte[] { 0x01 }, 1)]
    [InlineData(new byte[] { 0x3F }, 63)]
    public void ReadS32Leb128_PositiveValues(byte[] data, int expected)
    {
        var reader = ReaderFrom(data);
        Assert.Equal(expected, reader.ReadS32Leb128());
    }

    [Fact]
    public void ReadS32Leb128_NegativeOne()
    {
        // -1 in signed LEB128
        var reader = ReaderFrom(0x7F);
        Assert.Equal(-1, reader.ReadS32Leb128());
    }

    [Fact]
    public void ReadS32Leb128_NegativeValue()
    {
        // -123456 = 0xFFFE1DC0
        // Signed LEB128: 0xC0, 0xBB, 0x78
        var reader = ReaderFrom(0xC0, 0xBB, 0x78);
        Assert.Equal(-123456, reader.ReadS32Leb128());
    }

    [Fact]
    public void ReadS32Leb128_MinValue()
    {
        // int.MinValue = -2147483648 = 0x80000000
        // Signed LEB128: 0x80, 0x80, 0x80, 0x80, 0x78
        var reader = ReaderFrom(0x80, 0x80, 0x80, 0x80, 0x78);
        Assert.Equal(int.MinValue, reader.ReadS32Leb128());
    }

    [Fact]
    public void ReadS32Leb128_MaxValue()
    {
        // int.MaxValue = 2147483647 = 0x7FFFFFFF
        // Signed LEB128: 0xFF, 0xFF, 0xFF, 0xFF, 0x07
        var reader = ReaderFrom(0xFF, 0xFF, 0xFF, 0xFF, 0x07);
        Assert.Equal(int.MaxValue, reader.ReadS32Leb128());
    }

    #endregion

    #region ReadS64Leb128

    [Fact]
    public void ReadS64Leb128_PositiveValue()
    {
        // 624485 in signed LEB128: 0xE5, 0x8E, 0x26
        var reader = ReaderFrom(0xE5, 0x8E, 0x26);
        Assert.Equal(624485L, reader.ReadS64Leb128());
    }

    [Fact]
    public void ReadS64Leb128_NegativeOne()
    {
        var reader = ReaderFrom(0x7F);
        Assert.Equal(-1L, reader.ReadS64Leb128());
    }

    [Fact]
    public void ReadS64Leb128_LargePositive()
    {
        // long.MaxValue = 9223372036854775807
        // LEB128: 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00
        var reader = ReaderFrom(0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00);
        Assert.Equal(long.MaxValue, reader.ReadS64Leb128());
    }

    [Fact]
    public void ReadS64Leb128_LargeNegative()
    {
        // long.MinValue = -9223372036854775808
        // LEB128: 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x7F
        var reader = ReaderFrom(0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x7F);
        Assert.Equal(long.MinValue, reader.ReadS64Leb128());
    }

    #endregion

    #region ReadName

    [Fact]
    public void ReadName_AsciiString()
    {
        // Length 5, then "hello"
        var reader = ReaderFrom(0x05, 0x68, 0x65, 0x6C, 0x6C, 0x6F);
        Assert.Equal("hello", reader.ReadName());
    }

    [Fact]
    public void ReadName_EmptyString()
    {
        var reader = ReaderFrom(0x00);
        Assert.Equal("", reader.ReadName());
    }

    [Fact]
    public void ReadName_Utf8Multibyte()
    {
        // "café" in UTF-8: 63 61 66 C3 A9 (5 bytes)
        var reader = ReaderFrom(0x05, 0x63, 0x61, 0x66, 0xC3, 0xA9);
        Assert.Equal("caf\u00e9", reader.ReadName());
    }

    [Fact]
    public void ReadName_UnexpectedEof_ThrowsWasmFormatException()
    {
        // Says 10 bytes but only 2 available
        var reader = ReaderFrom(0x0A, 0x68, 0x69);
        Assert.Throws<WasmFormatException>(() => reader.ReadName());
    }

    #endregion

    #region ReadVector

    [Fact]
    public void ReadVector_Empty()
    {
        // Count = 0
        var reader = ReaderFrom(0x00);
        var result = reader.ReadVector(() => reader.ReadU32Leb128());
        Assert.Empty(result);
    }

    [Fact]
    public void ReadVector_OfU32Values()
    {
        // Count = 3, values: 1, 2, 3
        var reader = ReaderFrom(0x03, 0x01, 0x02, 0x03);
        var result = reader.ReadVector(() => reader.ReadU32Leb128());
        Assert.Equal(new uint[] { 1, 2, 3 }, result);
    }

    [Fact]
    public void ReadVector_OfNames()
    {
        // Count = 2, "ab", "cd"
        var reader = ReaderFrom(0x02, 0x02, 0x61, 0x62, 0x02, 0x63, 0x64);
        var result = reader.ReadVector(() => reader.ReadName());
        Assert.Equal(new[] { "ab", "cd" }, result);
    }

    #endregion

    #region ReadF32 / ReadF64

    [Fact]
    public void ReadF32_ReadsIeee754()
    {
        byte[] bytes = BitConverter.GetBytes(3.14f);
        var reader = ReaderFrom(bytes);
        Assert.Equal(3.14f, reader.ReadF32());
    }

    [Fact]
    public void ReadF32_Zero()
    {
        var reader = ReaderFrom(0x00, 0x00, 0x00, 0x00);
        Assert.Equal(0.0f, reader.ReadF32());
    }

    [Fact]
    public void ReadF32_NegativeInfinity()
    {
        byte[] bytes = BitConverter.GetBytes(float.NegativeInfinity);
        var reader = ReaderFrom(bytes);
        Assert.Equal(float.NegativeInfinity, reader.ReadF32());
    }

    [Fact]
    public void ReadF32_UnexpectedEof_ThrowsWasmFormatException()
    {
        var reader = ReaderFrom(0x00, 0x00);
        Assert.Throws<WasmFormatException>(() => reader.ReadF32());
    }

    [Fact]
    public void ReadF64_ReadsIeee754()
    {
        byte[] bytes = BitConverter.GetBytes(3.14159265358979);
        var reader = ReaderFrom(bytes);
        Assert.Equal(3.14159265358979, reader.ReadF64());
    }

    [Fact]
    public void ReadF64_Zero()
    {
        var reader = ReaderFrom(0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00);
        Assert.Equal(0.0, reader.ReadF64());
    }

    [Fact]
    public void ReadF64_UnexpectedEof_ThrowsWasmFormatException()
    {
        var reader = ReaderFrom(0x00, 0x00, 0x00);
        Assert.Throws<WasmFormatException>(() => reader.ReadF64());
    }

    #endregion

    #region Position / HasMore

    [Fact]
    public void Position_TracksReads()
    {
        var reader = ReaderFrom(0x01, 0x02, 0x03);
        Assert.Equal(0, reader.Position);
        reader.ReadByte();
        Assert.Equal(1, reader.Position);
        reader.ReadBytes(2);
        Assert.Equal(3, reader.Position);
    }

    [Fact]
    public void HasMore_ReturnsTrueWhenDataRemains()
    {
        var reader = ReaderFrom(0x01, 0x02);
        Assert.True(reader.HasMore);
        reader.ReadByte();
        Assert.True(reader.HasMore);
        reader.ReadByte();
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void PeekByte_OnNonSeekableStream_DoesNotConsumeData()
    {
        using var stream = new NonSeekableReadStream([0xAA, 0xBB]);
        var reader = new WasmBinaryReader(stream);

        Assert.Equal(0xAA, reader.PeekByte());
        Assert.Equal(0xAA, reader.ReadByte());
        Assert.Equal(0xBB, reader.ReadByte());
        Assert.False(reader.HasMore);
    }

    [Fact]
    public void HasMore_OnNonSeekableStream_TracksBufferedByte()
    {
        using var stream = new NonSeekableReadStream([0x10]);
        var reader = new WasmBinaryReader(stream);

        Assert.True(reader.HasMore);
        Assert.Equal(0, reader.Position);
        Assert.Equal(0x10, reader.ReadByte());
        Assert.Equal(1, reader.Position);
        Assert.False(reader.HasMore);
    }

    #endregion

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
