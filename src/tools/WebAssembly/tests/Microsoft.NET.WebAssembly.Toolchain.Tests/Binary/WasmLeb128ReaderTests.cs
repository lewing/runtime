// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Xunit;

/// <summary>
/// Boundary and adversarial cases for the shared LEB128 readers in <see cref="WasmLeb128"/>. The
/// core-spec decoders must reject every encoding a correct producer never emits, so most cases here
/// are inputs that a corpus of valid modules cannot contain.
/// </summary>
public class WasmLeb128ReaderTests
{
    private static byte[] Hex(string hex) => Convert.FromHexString(hex.Replace(" ", ""));

    // ── u32 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("00", 0u, 1)]
    [InlineData("7F", 127u, 1)]
    [InlineData("80 01", 128u, 2)]
    [InlineData("FF FF FF FF 0F", uint.MaxValue, 5)]
    [InlineData("80 00", 0u, 2)]
    [InlineData("80 80 80 80 00", 0u, 5)]
    [InlineData("FF 80 80 80 00", 127u, 5)]
    public void U32_Valid(string hex, uint expected, int expectedLength)
    {
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeU32(Hex(hex), out uint value, out int length));
        Assert.Equal(expected, value);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData("FF FF FF FF 1F", Leb128Status.Overflow)]      // bit 32
    [InlineData("80 80 80 80 10", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 40", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 00", Leb128Status.TooLong)]    // fifth byte continues
    [InlineData("80 80 80 80 8F 00", Leb128Status.TooLong)]
    [InlineData("80 80 80 80 F0 00", Leb128Status.Overflow)]   // value bits are checked first
    [InlineData("", Leb128Status.Truncated)]
    [InlineData("80", Leb128Status.Truncated)]
    [InlineData("FF FF FF FF", Leb128Status.Truncated)]
    public void U32_Rejected(string hex, Leb128Status expected)
    {
        Assert.Equal(expected, WasmLeb128.DecodeU32(Hex(hex), out uint value, out _));
        Assert.Equal(0u, value);
    }

    [Fact]
    public void U32_FifthByte_AcceptsExactlyTheLowNibble()
    {
        for (int b = 0; b < 256; b++)
        {
            byte[] encoded = [0x80, 0x80, 0x80, 0x80, (byte)b, 0x00];
            Leb128Status status = WasmLeb128.DecodeU32(encoded, out uint value, out _);
            Leb128Status expected = b < 0x10 ? Leb128Status.Ok
                : (b & 0x70) != 0 ? Leb128Status.Overflow
                : Leb128Status.TooLong;
            Assert.Equal(expected, status);
            if (b < 0x10)
                Assert.Equal((uint)b << 28, value);
        }
    }

    // ── s32 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("00", 0, 1)]
    [InlineData("3F", 63, 1)]
    [InlineData("40", -64, 1)]
    [InlineData("7F", -1, 1)]
    [InlineData("C0 00", 64, 2)]
    [InlineData("BF 7F", -65, 2)]
    [InlineData("FF FF FF FF 07", int.MaxValue, 5)]
    [InlineData("80 80 80 80 78", int.MinValue, 5)]
    [InlineData("FF FF FF FF 7F", -1, 5)]
    [InlineData("80 80 80 80 00", 0, 5)]
    [InlineData("C0 FF 7F", -64, 3)]
    public void S32_Valid(string hex, int expected, int expectedLength)
    {
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS32(Hex(hex), out int value, out int length));
        Assert.Equal(expected, value);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData("FF FF FF FF 0F", Leb128Status.Overflow)]      // 2^32 - 1 is not an int32
    [InlineData("80 80 80 80 70", Leb128Status.Overflow)]      // sign copies disagree with bit 31
    [InlineData("FF FF FF FF 4F", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 00", Leb128Status.TooLong)]
    [InlineData("FF FF FF FF FF 7F", Leb128Status.TooLong)]
    [InlineData("", Leb128Status.Truncated)]
    [InlineData("FF FF", Leb128Status.Truncated)]
    public void S32_Rejected(string hex, Leb128Status expected)
    {
        Assert.Equal(expected, WasmLeb128.DecodeS32(Hex(hex), out int value, out _));
        Assert.Equal(0, value);
    }

    [Fact]
    public void S32_FifthByte_AcceptsExactlySignConsistentValues()
    {
        for (int b = 0; b < 0x80; b++)
        {
            byte[] encoded = [0x80, 0x80, 0x80, 0x80, (byte)b];
            bool valid = b is <= 0x07 or >= 0x78;
            Assert.Equal(valid ? Leb128Status.Ok : Leb128Status.Overflow, WasmLeb128.DecodeS32(encoded, out _, out _));
        }
    }

    // ── u64 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FF FF FF FF FF FF FF FF FF 01", ulong.MaxValue, 10)]
    [InlineData("80 80 80 80 80 80 80 80 80 00", 0ul, 10)]
    [InlineData("80 80 80 80 10", 1ul << 32, 5)]
    public void U64_Valid(string hex, ulong expected, int expectedLength)
    {
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeU64(Hex(hex), out ulong value, out int length));
        Assert.Equal(expected, value);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData("FF FF FF FF FF FF FF FF FF 02", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 80 80 80 80 80 00", Leb128Status.TooLong)]
    [InlineData("FF FF FF FF FF FF FF FF FF 81 00", Leb128Status.TooLong)]
    [InlineData("FF FF FF FF FF FF FF FF FF FF 00", Leb128Status.Overflow)]
    [InlineData("FF FF FF FF FF FF FF FF FF", Leb128Status.Truncated)]
    public void U64_Rejected(string hex, Leb128Status expected)
        => Assert.Equal(expected, WasmLeb128.DecodeU64(Hex(hex), out _, out _));

    [Fact]
    public void U64_TenthByte_AcceptsOnlyBit63()
    {
        for (int b = 0; b < 256; b++)
        {
            byte[] encoded = [0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, 0x80, (byte)b, 0x00];
            Leb128Status expected = b <= 0x01 ? Leb128Status.Ok
                : (b & 0x7E) != 0 ? Leb128Status.Overflow
                : Leb128Status.TooLong;
            Assert.Equal(expected, WasmLeb128.DecodeU64(encoded, out _, out _));
        }
    }

    // ── s64 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("FF FF FF FF FF FF FF FF FF 00", long.MaxValue, 10)]
    [InlineData("80 80 80 80 80 80 80 80 80 7F", long.MinValue, 10)]
    [InlineData("FF FF FF FF FF FF FF FF FF 7F", -1L, 10)]
    [InlineData("40", -64L, 1)]
    [InlineData("BF 7F", -65L, 2)]
    [InlineData("FF FF FF FF 0F", 0xFFFF_FFFFL, 5)]
    public void S64_Valid(string hex, long expected, int expectedLength)
    {
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS64(Hex(hex), out long value, out int length));
        Assert.Equal(expected, value);
        Assert.Equal(expectedLength, length);
    }

    [Theory]
    [InlineData("FF FF FF FF FF FF FF FF FF 01", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 80 80 80 80 7E", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 80 80 80 80 80 00", Leb128Status.TooLong)]
    [InlineData("80 80 80", Leb128Status.Truncated)]
    public void S64_Rejected(string hex, Leb128Status expected)
        => Assert.Equal(expected, WasmLeb128.DecodeS64(Hex(hex), out _, out _));

    // ── s33 ─────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("80 80 80 80 70", -4_294_967_296L)]
    [InlineData("FF FF FF FF 0F", 4_294_967_295L)]
    [InlineData("40", -64L)]
    [InlineData("70", -16L)]
    [InlineData("80 80 80 80 00", 0L)]
    public void S33_Valid(string hex, long expected)
    {
        Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS33(Hex(hex), out long value, out _));
        Assert.Equal(expected, value);
    }

    [Theory]
    [InlineData("FF FF FF FF 1F", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 40", Leb128Status.Overflow)]
    [InlineData("80 80 80 80 80 00", Leb128Status.TooLong)]
    [InlineData("FF", Leb128Status.Truncated)]
    public void S33_Rejected(string hex, Leb128Status expected)
        => Assert.Equal(expected, WasmLeb128.DecodeS33(Hex(hex), out _, out _));

    // ── Position-based and throwing forms ─────────────────────────────────────────

    [Fact]
    public void TryRead_AdvancesOnlyOnSuccess()
    {
        byte[] data = Hex("AA E5 8E 26 80 80");
        int position = 1;
        Assert.True(WasmLeb128.TryReadU32(data, ref position, out uint value));
        Assert.Equal(624485u, value);
        Assert.Equal(4, position);

        Assert.False(WasmLeb128.TryReadU32(data, ref position, out _));
        Assert.Equal(4, position);

        position = data.Length + 3;
        Assert.False(WasmLeb128.TryReadS64(data, ref position, out _));
        Assert.Equal(data.Length + 3, position);
    }

    [Theory]
    [InlineData("FF FF FF FF 1F", "overflows uint32")]
    [InlineData("80 80 80 80 80 00", "overflows uint32")]   // TooLong keeps the reader's original text
    [InlineData("80 80", "end of data")]
    public void ReadU32_ThrowsWasmFormatException(string hex, string messagePart)
    {
        byte[] data = Hex(hex);
        int position = 0;
        var ex = Assert.Throws<WasmFormatException>(() => WasmLeb128.ReadU32(data, ref position));
        Assert.Contains(messagePart, ex.Message);
    }

    [Fact]
    public void ReadU64_TooLong_KeepsTheOverflowMessage()
    {
        byte[] data = Hex("80 80 80 80 80 80 80 80 80 80 00");
        int position = 0;
        var ex = Assert.Throws<WasmFormatException>(() => WasmLeb128.ReadU64(data, ref position));
        Assert.Equal("Unsigned LEB128 value overflows uint64", ex.Message);
    }

    [Fact]
    public void ReadS32_TooLong_Throws()
    {
        byte[] data = Hex("80 80 80 80 80 00");
        int position = 0;
        var ex = Assert.Throws<WasmFormatException>(() => WasmLeb128.ReadS32(data, ref position));
        Assert.Contains("exceeds 5 bytes", ex.Message);
    }

    // ── Unbounded (DWARF) ─────────────────────────────────────────────────────

    [Fact]
    public void Unbounded_ConsumesEveryContinuationByte()
    {
        // 2^64-1 padded to 13 bytes: the core reader refuses it, the unbounded reader keeps the
        // low 64 bits and leaves the cursor after the last byte.
        byte[] data = [.. Hex("FF FF FF FF FF FF FF FF FF 81 80 80 00"), 0x2A];
        Assert.NotEqual(Leb128Status.Ok, WasmLeb128.DecodeU64(data, out _, out _));

        int position = 0;
        Assert.True(WasmLeb128.TryReadUnboundedU64(data, ref position, out ulong value));
        Assert.Equal(ulong.MaxValue, value);
        Assert.Equal(13, position);
        Assert.Equal(0x2A, data[position]);
    }

    [Theory]
    [InlineData("7F", -1L, 1)]
    [InlineData("FF FF FF FF FF FF FF FF FF FF FF FF 7F", -1L, 13)]
    [InlineData("80 80 80 80 80 80 80 80 80 80 80 00", 0L, 12)]
    [InlineData("C0 BB 78", -123456L, 3)]
    [InlineData("FF FF FF FF FF FF FF FF FF 00", long.MaxValue, 10)]
    [InlineData("80 80 80 80 80 80 80 80 80 80 01", 0L, 11)]    // 2^70: a wrapped shift would add 2^6
    [InlineData("80 80 80 80 80 80 80 80 80 80 40", 0L, 11)]    // -2^76: a wrapped sign extension would set bits 13-63
    public void UnboundedS64_SignExtendsAndConsumesEverything(string hex, long expected, int expectedLength)
    {
        int position = 0;
        Assert.True(WasmLeb128.TryReadUnboundedS64(Hex(hex), ref position, out long value));
        Assert.Equal(expected, value);
        Assert.Equal(expectedLength, position);
    }

    [Theory]
    [InlineData("80 80 80 80 80 80 80 80 80 80 01", 11)]       // 2^70: a wrapped shift would give 2^6
    [InlineData("80 80 80 80 80 80 80 80 80 80 80 80 80 80 80 80 80 80 01", 19)] // 2^126: would give 2^62
    public void UnboundedU64_DropsBitsAbove63(string hex, int expectedLength)
    {
        int position = 0;
        Assert.True(WasmLeb128.TryReadUnboundedU64(Hex(hex), ref position, out ulong value));
        Assert.Equal(0ul, value);
        Assert.Equal(expectedLength, position);
    }

    [Theory]
    [InlineData("")]
    [InlineData("80")]
    [InlineData("FF FF FF FF FF FF FF FF FF FF FF FF")]
    public void Unbounded_TruncatedInput_ReturnsFalseWithoutMoving(string hex)
    {
        byte[] data = Hex(hex);
        int position = 0;
        Assert.False(WasmLeb128.TryReadUnboundedU64(data, ref position, out ulong u));
        Assert.False(WasmLeb128.TryReadUnboundedS64(data, ref position, out long s));
        Assert.False(WasmLeb128.TrySkip(data, ref position));
        Assert.Equal(0, position);
        Assert.Equal(0ul, u);
        Assert.Equal(0L, s);
        Assert.Throws<WasmFormatException>(() => WasmLeb128.Skip(data, ref position));
    }

    [Fact]
    public void TrySkip_StopsAfterTheFinalByte()
    {
        byte[] data = Hex("80 80 80 80 80 80 80 80 80 80 80 01 05");
        int position = 0;
        Assert.True(WasmLeb128.TrySkip(data, ref position));
        Assert.Equal(12, position);
    }

    // ── Agreement with the writers ────────────────────────────────────────────

    [Fact]
    public void WriterOutput_DecodesToTheSameValue()
    {
        var random = new Random(587);
        Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength];
        Span<byte> padded32 = stackalloc byte[WasmLeb128.PaddedLength32];
        Span<byte> padded64 = stackalloc byte[WasmLeb128.PaddedLength64];
        for (int i = 0; i < 2000; i++)
        {
            int shift = random.Next(64);
            long s = random.NextInt64() >> shift;
            ulong u = (ulong)random.NextInt64() >> shift;

            int n = WasmLeb128.WriteSLeb128(buffer, s);
            Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS64(buffer[..n], out long s64, out int length));
            Assert.Equal((s, n), (s64, length));

            n = WasmLeb128.WriteULeb128(buffer, u);
            Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeU64(buffer[..n], out ulong u64, out length));
            Assert.Equal((u, n), (u64, length));

            WasmLeb128.WritePaddedSLeb128(padded64, s);
            Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS64(padded64, out s64, out _));
            Assert.Equal(s, s64);

            int s32 = (int)s;
            WasmLeb128.WritePaddedSLeb128(padded32, s32);
            Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeS32(padded32, out int s32Read, out _));
            Assert.Equal(s32, s32Read);

            uint u32 = (uint)u;
            WasmLeb128.WritePaddedULeb128(padded32, u32);
            Assert.Equal(Leb128Status.Ok, WasmLeb128.DecodeU32(padded32, out uint u32Read, out _));
            Assert.Equal(u32, u32Read);
        }
    }
}
