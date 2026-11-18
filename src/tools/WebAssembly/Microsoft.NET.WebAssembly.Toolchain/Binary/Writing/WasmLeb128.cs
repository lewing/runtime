// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// LEB128 as the WebAssembly binary format and its object-file conventions use it: minimal encodings for
/// ordinary fields and padded (fixed-width) encodings for relocated fields and size fields that are patched later.
/// This is nesm's single LEB128 implementation; the readers (core-spec and unbounded) live in
/// <c>WasmLeb128.Reading.cs</c>. Don't add private copies.
/// </summary>
public static partial class WasmLeb128
{
    /// <summary>The longest LEB128 encoding of a 64-bit value.</summary>
    public const int MaxLength = 10;

    /// <summary>The width of a padded 32-bit field (a relocated <c>LEB</c>/<c>SLEB</c> operand or a patchable size).</summary>
    public const int PaddedLength32 = 5;

    /// <summary>The width of a padded 64-bit field (a relocated <c>LEB64</c>/<c>SLEB64</c> operand).</summary>
    public const int PaddedLength64 = 10;

    /// <summary>The length of the minimal unsigned LEB128 encoding of <paramref name="value"/>.</summary>
    public static int GetULeb128Size(ulong value)
    {
        int size = 1;
        while (value >= 0x80)
        {
            value >>= 7;
            size++;
        }

        return size;
    }

    /// <summary>The length of the minimal signed LEB128 encoding of <paramref name="value"/>.</summary>
    public static int GetSLeb128Size(long value)
    {
        int size = 0;
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            size++;
            if ((value == 0 && (b & 0x40) == 0) || (value == -1 && (b & 0x40) != 0))
                return size;
        }
    }

    /// <summary>Writes the minimal unsigned LEB128 encoding of <paramref name="value"/> and returns its length.</summary>
    public static int WriteULeb128(Span<byte> destination, ulong value)
    {
        int length = 0;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                b |= 0x80;
            destination[length++] = b;
        }
        while (value != 0);

        return length;
    }

    /// <summary>Writes the minimal signed LEB128 encoding of <paramref name="value"/> and returns its length.</summary>
    public static int WriteSLeb128(Span<byte> destination, long value)
    {
        int length = 0;
        while (true)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            bool done = (value == 0 && (b & 0x40) == 0) || (value == -1 && (b & 0x40) != 0);
            destination[length++] = done ? b : (byte)(b | 0x80);
            if (done)
                return length;
        }
    }

    /// <summary>
    /// Writes the minimal unsigned LEB128 encoding of <paramref name="value"/> to <paramref name="stream"/> and returns its length.
    /// </summary>
    public static int WriteULeb128(Stream stream, ulong value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        int length = WriteULeb128(buffer, value);
        stream.Write(buffer[..length]);
        return length;
    }

    /// <summary>
    /// Writes the minimal signed LEB128 encoding of <paramref name="value"/> to <paramref name="stream"/> and returns its length.
    /// </summary>
    public static int WriteSLeb128(Stream stream, long value)
    {
        Span<byte> buffer = stackalloc byte[MaxLength];
        int length = WriteSLeb128(buffer, value);
        stream.Write(buffer[..length]);
        return length;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with an unsigned LEB128 encoding of <paramref name="value"/> that is
    /// exactly <c>destination.Length</c> bytes long, padding with continuation bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The value's minimal encoding is longer than the destination.</exception>
    public static void WritePaddedULeb128(Span<byte> destination, ulong value)
    {
        if (destination.IsEmpty || GetULeb128Size(value) > destination.Length)
            throw new ArgumentException($"{value} does not fit in a {destination.Length}-byte LEB128 field.", nameof(destination));

        int last = destination.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            destination[i] = i < last ? (byte)(b | 0x80) : b;
        }
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with a signed LEB128 encoding of <paramref name="value"/> that is
    /// exactly <c>destination.Length</c> bytes long, padding with sign-extension continuation bytes.
    /// </summary>
    /// <exception cref="ArgumentException">The value's minimal encoding is longer than the destination.</exception>
    public static void WritePaddedSLeb128(Span<byte> destination, long value)
    {
        if (destination.IsEmpty || GetSLeb128Size(value) > destination.Length)
            throw new ArgumentException($"{value} does not fit in a {destination.Length}-byte LEB128 field.", nameof(destination));

        int last = destination.Length - 1;
        for (int i = 0; i <= last; i++)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            destination[i] = i < last ? (byte)(b | 0x80) : b;
        }
    }
}
