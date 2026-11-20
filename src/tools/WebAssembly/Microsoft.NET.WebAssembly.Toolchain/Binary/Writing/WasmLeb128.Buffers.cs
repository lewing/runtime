// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

public static partial class WasmLeb128
{
    // ── IBufferWriter<byte> ──────────────────────────────────────────────────────

    /// <summary>Writes the minimal unsigned LEB128 encoding of <paramref name="value"/> and returns its length.</summary>
    public static int WriteULeb128(IBufferWriter<byte> writer, ulong value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int length = WriteULeb128(writer.GetSpan(MaxLength), value);
        writer.Advance(length);
        return length;
    }

    /// <summary>Writes the minimal signed LEB128 encoding of <paramref name="value"/> and returns its length.</summary>
    public static int WriteSLeb128(IBufferWriter<byte> writer, long value)
    {
        ArgumentNullException.ThrowIfNull(writer);
        int length = WriteSLeb128(writer.GetSpan(MaxLength), value);
        writer.Advance(length);
        return length;
    }

    /// <summary>
    /// Writes an unsigned LEB128 encoding of <paramref name="value"/> padded to exactly <paramref name="width"/>
    /// bytes, and returns <paramref name="width"/>. Nothing is written when the value does not fit.
    /// </summary>
    /// <exception cref="ArgumentException">The value's minimal encoding is longer than <paramref name="width"/>.</exception>
    public static int WritePaddedULeb128(IBufferWriter<byte> writer, ulong value, int width)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        WritePaddedULeb128(writer.GetSpan(width)[..width], value);
        writer.Advance(width);
        return width;
    }

    /// <summary>
    /// Writes a signed LEB128 encoding of <paramref name="value"/> padded to exactly <paramref name="width"/>
    /// bytes, and returns <paramref name="width"/>. Nothing is written when the value does not fit.
    /// </summary>
    /// <exception cref="ArgumentException">The value's minimal encoding is longer than <paramref name="width"/>.</exception>
    public static int WritePaddedSLeb128(IBufferWriter<byte> writer, long value, int width)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        WritePaddedSLeb128(writer.GetSpan(width)[..width], value);
        writer.Advance(width);
        return width;
    }

    // ── Stream readers ───────────────────────────────────────────────────────────
    //
    // These read one encoding byte by byte (at most the width's maximum length) and decode it with
    // the core-spec decoders, so a stream is held to the same rules as a span. A status of
    // Truncated with length 0 means the stream ended before the value, which a caller reading a
    // sequence of values can treat as its end.

    /// <summary>Reads an unsigned 32-bit LEB128 from <paramref name="stream"/>; <paramref name="length"/> bytes are consumed.</summary>
    public static Leb128Status DecodeU32(Stream stream, out uint value, out int length)
    {
        Span<byte> encoded = stackalloc byte[MaxLength32];
        Leb128Status status = Fill(stream, encoded, out length);
        value = 0;
        return status != Leb128Status.Ok ? status : DecodeU32(encoded[..length], out value, out _);
    }

    /// <summary>Reads a signed 32-bit LEB128 from <paramref name="stream"/>; <paramref name="length"/> bytes are consumed.</summary>
    public static Leb128Status DecodeS32(Stream stream, out int value, out int length)
    {
        Span<byte> encoded = stackalloc byte[MaxLength32];
        Leb128Status status = Fill(stream, encoded, out length);
        value = 0;
        return status != Leb128Status.Ok ? status : DecodeS32(encoded[..length], out value, out _);
    }

    /// <summary>Reads an unsigned 64-bit LEB128 from <paramref name="stream"/>; <paramref name="length"/> bytes are consumed.</summary>
    public static Leb128Status DecodeU64(Stream stream, out ulong value, out int length)
    {
        Span<byte> encoded = stackalloc byte[MaxLength64];
        Leb128Status status = Fill(stream, encoded, out length);
        value = 0;
        return status != Leb128Status.Ok ? status : DecodeU64(encoded[..length], out value, out _);
    }

    /// <summary>Reads a signed 64-bit LEB128 from <paramref name="stream"/>; <paramref name="length"/> bytes are consumed.</summary>
    public static Leb128Status DecodeS64(Stream stream, out long value, out int length)
    {
        Span<byte> encoded = stackalloc byte[MaxLength64];
        Leb128Status status = Fill(stream, encoded, out length);
        value = 0;
        return status != Leb128Status.Ok ? status : DecodeS64(encoded[..length], out value, out _);
    }

    /// <summary>Reads an unsigned 32-bit LEB128 from <paramref name="stream"/>.</summary>
    /// <exception cref="WasmFormatException">The stream ends early, or the encoding is too long or out of range.</exception>
    public static uint ReadU32(Stream stream) =>
        Check(DecodeU32(stream, out uint value, out _), Leb128Kind.U32) ? value : 0;

    /// <summary>Reads a signed 32-bit LEB128 from <paramref name="stream"/>.</summary>
    /// <exception cref="WasmFormatException">The stream ends early, or the encoding is too long or out of range.</exception>
    public static int ReadS32(Stream stream) =>
        Check(DecodeS32(stream, out int value, out _), Leb128Kind.S32) ? value : 0;

    /// <summary>Reads an unsigned 64-bit LEB128 from <paramref name="stream"/>.</summary>
    /// <exception cref="WasmFormatException">The stream ends early, or the encoding is too long or out of range.</exception>
    public static ulong ReadU64(Stream stream) =>
        Check(DecodeU64(stream, out ulong value, out _), Leb128Kind.U64) ? value : 0;

    /// <summary>Reads a signed 64-bit LEB128 from <paramref name="stream"/>.</summary>
    /// <exception cref="WasmFormatException">The stream ends early, or the encoding is too long or out of range.</exception>
    public static long ReadS64(Stream stream) =>
        Check(DecodeS64(stream, out long value, out _), Leb128Kind.S64) ? value : 0;

    private static bool Check(Leb128Status status, Leb128Kind kind) =>
        status == Leb128Status.Ok ? true : throw CreateException(status, kind);

    // Reads up to and including the first byte without a continuation bit, or until the buffer is full
    // (the decoder then reports TooLong or Overflow).
    private static Leb128Status Fill(Stream stream, Span<byte> encoded, out int length)
    {
        ArgumentNullException.ThrowIfNull(stream);
        length = 0;
        while (length < encoded.Length)
        {
            int b = stream.ReadByte();
            if (b < 0)
                return Leb128Status.Truncated;
            encoded[length++] = (byte)b;
            if ((b & 0x80) == 0)
                break;
        }

        return Leb128Status.Ok;
    }
}
