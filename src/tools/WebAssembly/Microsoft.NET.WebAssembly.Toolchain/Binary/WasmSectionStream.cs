// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Reads a WebAssembly binary from a <see cref="Stream"/> one section at a time: the outermost framing only. Each
/// section's payload is read once into a pooled buffer and handed to a <see cref="WasmSpanReader"/>, which does
/// all the parsing.
/// </summary>
/// <remarks>
/// <para>
/// The buffer is reused for the next section and returned to the pool on <see cref="Dispose"/>, so a span from
/// <see cref="ReadPayload"/> is valid only until the next read. Anything the caller keeps must be copied out.
/// </para>
/// <para>
/// A payload length is checked against the rest of a seekable stream before anything is rented for it. A
/// non-seekable stream has no remaining length to check, so a payload longer than
/// <see cref="UnknownLengthChunkSize"/> is read in growing chunks: a forged length cannot rent more than a small
/// multiple of the bytes actually present. Error messages match <see cref="WasmBinaryReader"/>'s.
/// </para>
/// </remarks>
internal sealed class WasmSectionStream : IDisposable
{
    /// <summary>The largest buffer rented up front for a payload whose length cannot be checked against the input.</summary>
    internal const int UnknownLengthChunkSize = 64 * 1024;

    private readonly Stream _stream;
    private readonly long? _length;
    private long _position;
    private byte[]? _buffer;

    /// <param name="stream">The stream, positioned at the first byte to read.</param>
    /// <param name="nonSeekableStart">The offset of that byte when <paramref name="stream"/> cannot report it.</param>
    public WasmSectionStream(Stream stream, long nonSeekableStart = 0)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        if (stream.CanSeek)
        {
            _position = stream.Position;
            _length = stream.Length;
        }
        else
        {
            _position = nonSeekableStart;
        }
    }

    /// <summary>The offset of the next byte.</summary>
    public long Position => _position;

    /// <summary>
    /// Reads the next byte, or returns <see langword="false"/> at the end of the input. A seekable stream ends at its
    /// length: a stream that delivers fewer bytes than its length claims is truncated, not ended.
    /// </summary>
    public bool TryReadByte(out byte value)
    {
        if (_length is { } length && _position >= length)
        {
            value = 0;
            return false;
        }

        int read = _stream.ReadByte();
        if (read < 0)
        {
            if (_length is not null)
                throw new WasmFormatException("Unexpected end of stream while reading byte", _position);
            value = 0;
            return false;
        }

        _position++;
        value = (byte)read;
        return true;
    }

    public byte ReadByte()
    {
        int read = _stream.ReadByte();
        if (read < 0)
            throw new WasmFormatException("Unexpected end of stream while reading byte", _position);
        _position++;
        return (byte)read;
    }

    /// <summary>Reads an unsigned 32-bit LEB128 (§5.2.2).</summary>
    public uint ReadU32()
    {
        long start = _position;
        byte first = ReadByte();
        if (first < 0x80)
            return first;

        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength32];
        encoded[0] = first;
        int length = 1;
        while ((encoded[length - 1] & 0x80) != 0 && length < encoded.Length)
            encoded[length++] = ReadByte();

        Leb128Status status = WasmLeb128.DecodeU32(encoded[..length], out uint value, out _);
        return status == Leb128Status.Ok
            ? value
            : throw new WasmFormatException(WasmLeb128.CreateMessage(status, WasmLeb128.Leb128Kind.U32), start);
    }

    /// <summary>Reads exactly <paramref name="destination"/>.Length bytes.</summary>
    public void ReadExactly(Span<byte> destination)
    {
        long start = _position;
        int read = ReadAvailable(destination);
        if (read < destination.Length)
            throw UnexpectedEnd((ulong)destination.Length, read, start);
    }

    /// <summary>
    /// Reads the next <paramref name="length"/> bytes into the pooled buffer and returns a reader over them. The
    /// reader is valid until the next call that reads from this stream.
    /// </summary>
    /// <exception cref="WasmFormatException">The input ends before <paramref name="length"/> bytes.</exception>
    public WasmSpanReader ReadPayload(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long start = _position;
        if (_length is { } total)
        {
            long remaining = total - _position;
            if ((ulong)length > (ulong)remaining)
                throw UnexpectedEnd((ulong)length, remaining, start);
        }
        else if (length > UnknownLengthChunkSize)
        {
            return new WasmSpanReader(ReadGrowing(length, start), start);
        }

        Span<byte> payload = Rent(length).AsSpan(0, length);
        ReadExactly(payload);
        return new WasmSpanReader(payload, start);
    }

    /// <summary>
    /// Reads the next <paramref name="length"/> bytes into a new array of exactly that length, for a payload the
    /// caller keeps. The length is checked, and failures reported, as <see cref="ReadPayload"/> does.
    /// </summary>
    /// <exception cref="WasmFormatException">The input ends before <paramref name="length"/> bytes.</exception>
    public byte[] ReadPayloadArray(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        long start = _position;
        if (_length is { } total)
        {
            long remaining = total - _position;
            if ((ulong)length > (ulong)remaining)
                throw UnexpectedEnd((ulong)length, remaining, start);
        }
        else if (length > UnknownLengthChunkSize)
        {
            return ReadGrowing(length, start).ToArray();
        }

        byte[] payload = new byte[length];
        ReadExactly(payload);
        return payload;
    }

    private ReadOnlySpan<byte> ReadGrowing(int length, long start)
    {
        int filled = 0;
        int capacity = UnknownLengthChunkSize;
        while (true)
        {
            byte[] buffer = Rent(capacity, preserve: filled);
            filled += ReadAvailable(buffer.AsSpan(filled, capacity - filled));
            if (filled < capacity)
                throw UnexpectedEnd((ulong)length, filled, start);
            if (filled == length)
                return buffer.AsSpan(0, length);
            capacity = (int)Math.Min(2L * capacity, length);
        }
    }

    /// <summary>Returns a pooled buffer of at least <paramref name="length"/> bytes, keeping its first <paramref name="preserve"/> bytes.</summary>
    private byte[] Rent(int length, int preserve = 0)
    {
        byte[]? buffer = _buffer;
        if (buffer is not null && buffer.Length >= length)
            return buffer;

        byte[] larger = ArrayPool<byte>.Shared.Rent(length);
        if (buffer is not null)
        {
            buffer.AsSpan(0, preserve).CopyTo(larger);
            ArrayPool<byte>.Shared.Return(buffer);
        }

        _buffer = larger;
        return larger;
    }

    /// <summary>Fills <paramref name="destination"/> until it is full or the stream ends; returns the bytes read.</summary>
    private int ReadAvailable(Span<byte> destination)
    {
        int total = 0;
        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read == 0)
                break;
            total += read;
        }

        _position += total;
        return total;
    }

    // Reported at the start of the bytes that could not be read, as WasmSpanReader reports it: the stream has
    // already moved past whatever part of them was present.
    private static WasmFormatException UnexpectedEnd(ulong expected, long available, long start)
        => new($"Unexpected end of stream: expected {expected} bytes but only {available} available", start);

    public void Dispose()
    {
        if (_buffer is { } buffer)
        {
            _buffer = null;
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
