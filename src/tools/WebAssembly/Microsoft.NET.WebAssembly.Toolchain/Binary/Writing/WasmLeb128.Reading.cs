// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>The outcome of decoding one LEB128 value.</summary>
public enum Leb128Status : byte
{
    /// <summary>A complete, in-range value was decoded.</summary>
    Ok,

    /// <summary>The input ended before the final (non-continuation) byte.</summary>
    Truncated,

    /// <summary>The encoding is longer than the width allows (spec: "integer representation too long").</summary>
    TooLong,

    /// <summary>The final byte sets bits the width cannot hold (spec: "integer too large").</summary>
    Overflow,
}

public static partial class WasmLeb128
{
    /// <summary>The longest encoding of a 32-bit (or s33) value the core specification accepts.</summary>
    public const int MaxLength32 = 5;

    /// <summary>The longest encoding of a 64-bit value the core specification accepts.</summary>
    public const int MaxLength64 = 10;

    /// <summary>The smallest s33 value (heap and block types), -2^32.</summary>
    public const long S33MinValue = -1L << 32;

    /// <summary>The largest s33 value (heap and block types), 2^32 - 1.</summary>
    public const long S33MaxValue = (1L << 32) - 1;

    /// <summary>Whether <paramref name="value"/> fits an s33.</summary>
    public static bool IsS33(long value) => value is >= S33MinValue and <= S33MaxValue;

    // ── Core-specification decoders ──────────────────────────────────────────────
    //
    // These follow §5.2.2: an encoding may be padded up to ⌈N/7⌉ bytes, and the unused bits of
    // the last allowed byte must be zero (unsigned) or copies of the sign bit (signed). On any
    // status but Ok, value is 0 and length is the number of bytes examined.

    /// <summary>Decodes an unsigned 32-bit LEB128 from the start of <paramref name="source"/>.</summary>
    public static Leb128Status DecodeU32(ReadOnlySpan<byte> source, out uint value, out int length)
    {
        uint result = 0;
        for (int i = 0; i < MaxLength32; i++)
        {
            if (i >= source.Length)
                return Fail(Leb128Status.Truncated, i, out value, out length);

            byte b = source[i];
            // Byte 5 carries bits 28-31: bits 4-6 must be clear. A continuation bit is TooLong (after the loop).
            if (i == MaxLength32 - 1 && (b & 0x70) != 0)
                return Fail(Leb128Status.Overflow, i + 1, out value, out length);

            result |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                value = result;
                length = i + 1;
                return Leb128Status.Ok;
            }
        }

        return Fail(Leb128Status.TooLong, MaxLength32, out value, out length);
    }

    /// <summary>Decodes a signed 32-bit LEB128 from the start of <paramref name="source"/>.</summary>
    public static Leb128Status DecodeS32(ReadOnlySpan<byte> source, out int value, out int length)
    {
        int result = 0;
        for (int i = 0; i < MaxLength32; i++)
        {
            if (i >= source.Length)
                return Fail(Leb128Status.Truncated, i, out value, out length);

            byte b = source[i];
            // Byte 5 carries bits 28-31; its bits 4-6 must copy the sign (bit 3).
            if (i == MaxLength32 - 1 && (b & 0x78) is not (0 or 0x78))
                return Fail(Leb128Status.Overflow, i + 1, out value, out length);

            int shift = 7 * i;
            result |= (b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                shift += 7;
                if (shift < 32 && (b & 0x40) != 0)
                    result |= -1 << shift;
                value = result;
                length = i + 1;
                return Leb128Status.Ok;
            }
        }

        return Fail(Leb128Status.TooLong, MaxLength32, out value, out length);
    }

    /// <summary>Decodes an unsigned 64-bit LEB128 from the start of <paramref name="source"/>.</summary>
    public static Leb128Status DecodeU64(ReadOnlySpan<byte> source, out ulong value, out int length)
    {
        ulong result = 0;
        for (int i = 0; i < MaxLength64; i++)
        {
            if (i >= source.Length)
                return Fail(Leb128Status.Truncated, i, out value, out length);

            byte b = source[i];
            // Byte 10 carries bit 63 only: bits 1-6 must be clear. A continuation bit is TooLong (after the loop).
            if (i == MaxLength64 - 1 && (b & 0x7E) != 0)
                return Fail(Leb128Status.Overflow, i + 1, out value, out length);

            result |= (ulong)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0)
            {
                value = result;
                length = i + 1;
                return Leb128Status.Ok;
            }
        }

        return Fail(Leb128Status.TooLong, MaxLength64, out value, out length);
    }

    /// <summary>Decodes a signed 64-bit LEB128 from the start of <paramref name="source"/>.</summary>
    public static Leb128Status DecodeS64(ReadOnlySpan<byte> source, out long value, out int length)
    {
        long result = 0;
        for (int i = 0; i < MaxLength64; i++)
        {
            if (i >= source.Length)
                return Fail(Leb128Status.Truncated, i, out value, out length);

            byte b = source[i];
            // Byte 10 carries bit 63 (bit 0); bits 1-6 must copy it.
            if (i == MaxLength64 - 1 && (b & 0x7F) is not (0 or 0x7F))
                return Fail(Leb128Status.Overflow, i + 1, out value, out length);

            int shift = 7 * i;
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                shift += 7;
                if (shift < 64 && (b & 0x40) != 0)
                    result |= -1L << shift;
                value = result;
                length = i + 1;
                return Leb128Status.Ok;
            }
        }

        return Fail(Leb128Status.TooLong, MaxLength64, out value, out length);
    }

    /// <summary>
    /// Decodes a signed 33-bit LEB128 (heap types and block types) from the start of
    /// <paramref name="source"/>. The value lies in [-2^32, 2^32 - 1].
    /// </summary>
    public static Leb128Status DecodeS33(ReadOnlySpan<byte> source, out long value, out int length)
    {
        long result = 0;
        for (int i = 0; i < MaxLength32; i++)
        {
            if (i >= source.Length)
                return Fail(Leb128Status.Truncated, i, out value, out length);

            byte b = source[i];
            // Byte 5 carries bits 28-32 (bit 32 is its bit 4); bits 5-6 must copy bit 32.
            if (i == MaxLength32 - 1 && (b & 0x70) is not (0 or 0x70))
                return Fail(Leb128Status.Overflow, i + 1, out value, out length);

            int shift = 7 * i;
            result |= (long)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                if ((b & 0x40) != 0)
                    result |= -1L << (shift + 7);
                value = result;
                length = i + 1;
                return Leb128Status.Ok;
            }
        }

        return Fail(Leb128Status.TooLong, MaxLength32, out value, out length);
    }

    // ── Position-based forms ─────────────────────────────────────────────────────

    /// <summary>Reads a u32 at <paramref name="position"/>, advancing it only on success.</summary>
    public static bool TryReadU32(ReadOnlySpan<byte> source, ref int position, out uint value)
        => Advance(DecodeU32(Rest(source, position), out value, out int length), ref position, length);

    /// <summary>Reads an s32 at <paramref name="position"/>, advancing it only on success.</summary>
    public static bool TryReadS32(ReadOnlySpan<byte> source, ref int position, out int value)
        => Advance(DecodeS32(Rest(source, position), out value, out int length), ref position, length);

    /// <summary>Reads a u64 at <paramref name="position"/>, advancing it only on success.</summary>
    public static bool TryReadU64(ReadOnlySpan<byte> source, ref int position, out ulong value)
        => Advance(DecodeU64(Rest(source, position), out value, out int length), ref position, length);

    /// <summary>Reads an s64 at <paramref name="position"/>, advancing it only on success.</summary>
    public static bool TryReadS64(ReadOnlySpan<byte> source, ref int position, out long value)
        => Advance(DecodeS64(Rest(source, position), out value, out int length), ref position, length);

    /// <summary>Reads an s33 at <paramref name="position"/>, advancing it only on success.</summary>
    public static bool TryReadS33(ReadOnlySpan<byte> source, ref int position, out long value)
        => Advance(DecodeS33(Rest(source, position), out value, out int length), ref position, length);

    /// <summary>Reads a u32 at <paramref name="position"/> and advances past it.</summary>
    /// <exception cref="WasmFormatException">The encoding is truncated, too long or out of range.</exception>
    public static uint ReadU32(ReadOnlySpan<byte> source, ref int position)
    {
        Leb128Status status = DecodeU32(Rest(source, position), out uint value, out int length);
        if (status != Leb128Status.Ok)
            throw CreateException(status, Leb128Kind.U32);
        position += length;
        return value;
    }

    /// <summary>Reads an s32 at <paramref name="position"/> and advances past it.</summary>
    /// <exception cref="WasmFormatException">The encoding is truncated, too long or out of range.</exception>
    public static int ReadS32(ReadOnlySpan<byte> source, ref int position)
    {
        Leb128Status status = DecodeS32(Rest(source, position), out int value, out int length);
        if (status != Leb128Status.Ok)
            throw CreateException(status, Leb128Kind.S32);
        position += length;
        return value;
    }

    /// <summary>Reads a u64 at <paramref name="position"/> and advances past it.</summary>
    /// <exception cref="WasmFormatException">The encoding is truncated, too long or out of range.</exception>
    public static ulong ReadU64(ReadOnlySpan<byte> source, ref int position)
    {
        Leb128Status status = DecodeU64(Rest(source, position), out ulong value, out int length);
        if (status != Leb128Status.Ok)
            throw CreateException(status, Leb128Kind.U64);
        position += length;
        return value;
    }

    /// <summary>Reads an s64 at <paramref name="position"/> and advances past it.</summary>
    /// <exception cref="WasmFormatException">The encoding is truncated, too long or out of range.</exception>
    public static long ReadS64(ReadOnlySpan<byte> source, ref int position)
    {
        Leb128Status status = DecodeS64(Rest(source, position), out long value, out int length);
        if (status != Leb128Status.Ok)
            throw CreateException(status, Leb128Kind.S64);
        position += length;
        return value;
    }

    /// <summary>Reads an s33 at <paramref name="position"/> and advances past it.</summary>
    /// <exception cref="WasmFormatException">The encoding is truncated, too long or out of range.</exception>
    public static long ReadS33(ReadOnlySpan<byte> source, ref int position)
    {
        Leb128Status status = DecodeS33(Rest(source, position), out long value, out int length);
        if (status != Leb128Status.Ok)
            throw CreateException(status, Leb128Kind.S33);
        position += length;
        return value;
    }

    /// <summary>
    /// Reads a u32 from <paramref name="stream"/>. Returns false when the stream ends early or the
    /// encoding is too long or out of range; the bytes examined are consumed either way.
    /// </summary>
    public static bool TryReadU32(Stream stream, out uint value) =>
        DecodeU32(stream, out value, out _) == Leb128Status.Ok;

    // ── Unbounded decoders (DWARF and other formats without a length limit) ─────────
    //
    // DWARF places no limit on a LEB128's length (a 2^64-1 DW_FORM_udata is ten bytes, and
    // producers may pad further). These consume every continuation byte, keep the low 64 bits
    // and never reject high bits. They are deliberately separate from the core-spec decoders:
    // wasm structure must never be read with them.

    /// <summary>
    /// Reads an unsigned LEB128 of any length at <paramref name="position"/>, keeping the low 64 bits.
    /// Returns false, leaving <paramref name="position"/> unchanged and <paramref name="value"/> 0,
    /// when the input ends before the final byte.
    /// </summary>
    public static bool TryReadUnboundedU64(ReadOnlySpan<byte> source, ref int position, out ulong value)
    {
        ulong result = 0;
        int shift = 0;
        for (int i = position; (uint)i < (uint)source.Length; i++)
        {
            byte b = source[i];
            if (shift < 64)
                result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
            {
                value = result;
                position = i + 1;
                return true;
            }

            shift += 7;
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Reads a signed LEB128 of any length at <paramref name="position"/>, keeping the low 64 bits
    /// (sign-extended from the final byte). Returns false, leaving <paramref name="position"/>
    /// unchanged and <paramref name="value"/> 0, when the input ends before the final byte.
    /// </summary>
    public static bool TryReadUnboundedS64(ReadOnlySpan<byte> source, ref int position, out long value)
    {
        long result = 0;
        int shift = 0;
        for (int i = position; (uint)i < (uint)source.Length; i++)
        {
            byte b = source[i];
            if (shift < 64)
                result |= (long)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0)
            {
                if (shift < 64 && (b & 0x40) != 0)
                    result |= -1L << shift;
                value = result;
                position = i + 1;
                return true;
            }
        }

        value = 0;
        return false;
    }

    /// <summary>
    /// Skips one LEB128 of any length (signed or unsigned) at <paramref name="position"/> without
    /// range checks. Returns false, leaving <paramref name="position"/> unchanged, when the input
    /// ends before the final byte. Use it only on input that is already validated, or that has no
    /// length limit.
    /// </summary>
    public static bool TrySkip(ReadOnlySpan<byte> source, ref int position)
    {
        for (int i = position; (uint)i < (uint)source.Length; i++)
        {
            if ((source[i] & 0x80) == 0)
            {
                position = i + 1;
                return true;
            }
        }

        return false;
    }

    /// <summary>Skips one LEB128 of any length at <paramref name="position"/>; see <see cref="TrySkip"/>.</summary>
    /// <exception cref="WasmFormatException">The input ends before the final byte.</exception>
    public static void Skip(ReadOnlySpan<byte> source, ref int position)
    {
        if (!TrySkip(source, ref position))
            throw new WasmFormatException("Unexpected end of data while skipping LEB128");
    }

    // ── Errors ────────────────────────────────────────────────────────────────

    /// <summary>The value widths the core-spec decoders read, for error messages.</summary>
    internal enum Leb128Kind : byte
    {
        U32,
        S32,
        U64,
        S64,
        S33
    }

    /// <summary>
    /// The exception the throwing readers raise for a failed decode. Stream readers that buffer an
    /// encoding and decode it with <c>Decode*</c> use it so every reader reports the same text.
    /// </summary>
    internal static WasmFormatException CreateException(Leb128Status status, Leb128Kind kind)
        => new(CreateMessage(status, kind));

    /// <summary>The message <see cref="CreateException"/> uses for a failed decode.</summary>
    internal static string CreateMessage(Leb128Status status, Leb128Kind kind)
    {
        (string signedness, string type, int maxBytes) = kind switch
        {
            Leb128Kind.U32 => ("Unsigned", "uint32", MaxLength32),
            Leb128Kind.S32 => ("Signed", "int32", MaxLength32),
            Leb128Kind.U64 => ("Unsigned", "uint64", MaxLength64),
            Leb128Kind.S64 => ("Signed", "int64", MaxLength64),
            _ => ("Signed", "s33", MaxLength32),
        };

        return status switch
        {
            // The unsigned readers always reported a continuation bit on the last allowed byte as overflow.
            Leb128Status.Overflow or Leb128Status.TooLong when kind is Leb128Kind.U32 or Leb128Kind.U64 =>
                $"{signedness} LEB128 value overflows {type}",
            Leb128Status.Overflow => $"{signedness} LEB128 value overflows {type}",
            Leb128Status.TooLong => $"{signedness} LEB128 encoding exceeds {maxBytes} bytes for {type}",
            Leb128Status.Truncated => $"Unexpected end of data while reading LEB128 {type}",
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Not a failure status."),
        };
    }

    private static ReadOnlySpan<byte> Rest(ReadOnlySpan<byte> source, int position)
        => (uint)position <= (uint)source.Length ? source[position..] : ReadOnlySpan<byte>.Empty;

    private static bool Advance(Leb128Status status, ref int position, int length)
    {
        if (status != Leb128Status.Ok)
            return false;
        position += length;
        return true;
    }

    private static Leb128Status Fail<T>(Leb128Status status, int examined, out T value, out int length)
        where T : struct
    {
        value = default;
        length = examined;
        return status;
    }
}
