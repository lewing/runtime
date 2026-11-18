// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Unicode;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Low-level reader for WebAssembly binary format primitives.
/// Handles LEB128 encoding, vectors, names, and raw bytes for both seekable and non-seekable streams.
/// </summary>
/// <remarks>
/// A compatibility layer for callers that read a stream one value at a time. <see cref="ModuleReader"/> does not
/// use it: it reads each section once and parses it from memory with <see cref="WasmSpanReader"/>, which reports
/// the same messages for the same bytes.
/// </remarks>
public sealed class WasmBinaryReader
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly Stream _stream;
    private readonly long? _length;
    private long _position;
    private bool _hasBufferedByte;
    private byte _bufferedByte;

    public WasmBinaryReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _stream = stream;
        if (stream.CanSeek)
        {
            _position = stream.Position;
            _length = stream.Length;
        }
    }

    /// <summary>Reads from <paramref name="reader"/>'s underlying stream.</summary>
    public WasmBinaryReader(BinaryReader reader)
        : this((reader ?? throw new ArgumentNullException(nameof(reader))).BaseStream)
    {
    }

    public long Position => _position;
    public long Length => _length ?? throw new NotSupportedException("Length is not available for this stream.");
    public bool HasMore => _hasBufferedByte || (_length.HasValue ? _position < _length.Value : TryBufferNextByte());

    /// <summary>Peek at the next byte without consuming it.</summary>
    public byte PeekByte()
    {
        if (_hasBufferedByte || TryBufferNextByte())
            return _bufferedByte;

        throw new WasmFormatException("Unexpected end of stream while peeking byte");
    }

    /// <summary>Read a single byte.</summary>
    public byte ReadByte()
    {
        if (_hasBufferedByte)
        {
            _hasBufferedByte = false;
            _position++;
            return _bufferedByte;
        }

        int value = _stream.ReadByte();
        if (value < 0)
            throw new WasmFormatException("Unexpected end of stream while reading byte");
        _position++;
        return (byte)value;
    }

    /// <summary>
    /// The largest buffer allocated up front for a read whose length cannot be checked against the input (a
    /// non-seekable stream); a longer read grows from here as data arrives, so a forged length cannot allocate more
    /// than a small multiple of the bytes actually present.
    /// </summary>
    private const int UnknownLengthChunkSize = 64 * 1024;

    /// <summary>Read exactly <paramref name="count"/> bytes.</summary>
    /// <exception cref="WasmFormatException">Fewer than <paramref name="count"/> bytes remain.</exception>
    public byte[] ReadBytes(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_length is { } total)
        {
            ThrowIfPastEnd((uint)count, total);
        }
        else if (count > UnknownLengthChunkSize)
        {
            return ReadBytesGrowing(count);
        }

        byte[] data = new byte[count];
        ReadInto(data);
        return data;
    }

    private byte[] ReadBytesGrowing(int count)
    {
        byte[] data = new byte[UnknownLengthChunkSize];
        int filled = 0;
        while (true)
        {
            filled += ReadAvailable(data.AsSpan(filled));
            if (filled < data.Length)
                throw UnexpectedEnd((ulong)count, filled);
            if (filled == count)
                return data;
            Array.Resize(ref data, (int)Math.Min(2L * data.Length, count));
        }
    }

    /// <summary>Rejects a read of <paramref name="length"/> bytes when fewer remain before <paramref name="total"/>.</summary>
    private void ThrowIfPastEnd(ulong length, long total)
    {
        long remaining = total - _position;
        if (length > (ulong)remaining)
            throw UnexpectedEnd(length, remaining);
    }

    private static WasmFormatException UnexpectedEnd(ulong expected, long available)
        => new($"Unexpected end of stream: expected {expected} bytes but only {available} available");

    /// <summary>Read exactly <paramref name="destination"/>.Length bytes into the provided span.</summary>
    public void ReadBytes(Span<byte> destination) => ReadInto(destination);

    /// <summary>Read an unsigned 32-bit LEB128 integer (§5.2.2).</summary>
    public uint ReadU32Leb128()
    {
        byte first = ReadByte();
        if (first < 0x80)
            return first;

        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength32];
        Leb128Status status = WasmLeb128.DecodeU32(ReadLeb128Bytes(first, encoded), out uint value, out _);
        return status == Leb128Status.Ok ? value : throw WasmLeb128.CreateException(status, WasmLeb128.Leb128Kind.U32);
    }

    /// <summary>Read a signed 32-bit LEB128 integer (§5.2.2).</summary>
    public int ReadS32Leb128()
    {
        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength32];
        Leb128Status status = WasmLeb128.DecodeS32(ReadLeb128Bytes(encoded), out int value, out _);
        return status == Leb128Status.Ok ? value : throw WasmLeb128.CreateException(status, WasmLeb128.Leb128Kind.S32);
    }

    /// <summary>Read an unsigned 64-bit LEB128 integer (§5.2.2).</summary>
    public ulong ReadU64Leb128()
    {
        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength64];
        Leb128Status status = WasmLeb128.DecodeU64(ReadLeb128Bytes(encoded), out ulong value, out _);
        return status == Leb128Status.Ok ? value : throw WasmLeb128.CreateException(status, WasmLeb128.Leb128Kind.U64);
    }

    /// <summary>Read a signed 64-bit LEB128 integer (§5.2.2).</summary>
    public long ReadS64Leb128()
    {
        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength64];
        Leb128Status status = WasmLeb128.DecodeS64(ReadLeb128Bytes(encoded), out long value, out _);
        return status == Leb128Status.Ok ? value : throw WasmLeb128.CreateException(status, WasmLeb128.Leb128Kind.S64);
    }

    /// <summary>
    /// Read a signed 33-bit LEB128 integer used for GC heap type encoding.
    /// Positive values are type indices; negative values are abstract heap type codes.
    /// </summary>
    public long ReadS33Leb128()
    {
        Span<byte> encoded = stackalloc byte[WasmLeb128.MaxLength32];
        Leb128Status status = WasmLeb128.DecodeS33(ReadLeb128Bytes(encoded), out long value, out _);
        return status == Leb128Status.Ok ? value : throw WasmLeb128.CreateException(status, WasmLeb128.Leb128Kind.S33);
    }

    /// <summary>
    /// Reads the bytes of one LEB128 into <paramref name="buffer"/>: up to and including the first byte
    /// without the continuation bit, or until the buffer is full. Decoding the result with a
    /// <see cref="WasmLeb128"/> <c>Decode*</c> method of the buffer's width then reports overflow or
    /// over-length exactly as a span reader would; a stream that ends early throws here.
    /// </summary>
    internal ReadOnlySpan<byte> ReadLeb128Bytes(Span<byte> buffer) => ReadLeb128Bytes(ReadByte(), buffer);

    private ReadOnlySpan<byte> ReadLeb128Bytes(byte first, Span<byte> buffer)
    {
        buffer[0] = first;
        int length = 1;
        while ((buffer[length - 1] & 0x80) != 0 && length < buffer.Length)
            buffer[length++] = ReadByte();
        return buffer[..length];
    }

    /// <summary>Read a Wasm vector (count + items) using a reader function.</summary>
    /// <remarks>Every element is assumed to take at least one byte; see <see cref="ReadVectorCount"/>.</remarks>
    public T[] ReadVector<T>(Func<T> readElement) => ReadVector(readElement, minElementSize: 1);

    /// <summary>
    /// Read a Wasm vector whose elements each take at least <paramref name="minElementSize"/> bytes, so a count the
    /// remaining input cannot hold is rejected before the array is allocated.
    /// </summary>
    internal T[] ReadVector<T>(Func<T> readElement, int minElementSize)
    {
        uint count = ReadVectorCount(minElementSize);
        if (_length is null && count > UnknownLengthVectorCapacity)
        {
            // No remaining length to check: grow with the elements actually read.
            var list = new List<T>(UnknownLengthVectorCapacity);
            for (uint i = 0; i < count; i++)
                list.Add(readElement());
            return list.ToArray();
        }

        var items = new T[count];
        for (uint i = 0; i < count; i++)
            items[i] = readElement();
        return items;
    }

    private const int UnknownLengthVectorCapacity = 1024;

    /// <summary>
    /// Read a vector count, rejecting one whose elements, at <paramref name="minElementSize"/> bytes each, cannot fit
    /// in the rest of the input (when its length is known). Callers that size an allocation by the count use this.
    /// </summary>
    internal uint ReadVectorCount(int minElementSize = 1)
    {
        uint count = ReadU32Leb128();
        if (_length is { } total && (ulong)count * (uint)minElementSize > (ulong)(total - _position))
        {
            throw new WasmFormatException(
                $"Unexpected end of stream: vector of {count} elements needs at least {(ulong)count * (uint)minElementSize} bytes but only {total - _position} available");
        }

        return count;
    }

    /// <summary>Read a Wasm vector count and return an iterator for zero-alloc foreach.</summary>
    public VectorIterator ReadVectorHeader()
    {
        uint count = ReadU32Leb128();
        return new VectorIterator(count);
    }

    /// <summary>
    /// Zero-alloc ref struct iterator for Wasm vectors. Use with foreach:
    /// foreach (var _ in reader.ReadVectorHeader()) { /* read one element */ }
    /// </summary>
    public ref struct VectorIterator
    {
        private uint _remaining;
        public VectorIterator(uint count) => _remaining = count;
        public VectorIterator GetEnumerator() => this;
        public bool MoveNext() => _remaining-- > 0;
        public uint Current => 0;
    }

    /// <summary>The message for a name (§5.2.4) whose bytes are not valid UTF-8.</summary>
    internal const string InvalidUtf8NameMessage = "Invalid UTF-8 encoding in name";

    /// <summary>Read a UTF-8 name (§5.2.4) — length-prefixed string.</summary>
    public string ReadName()
    {
        int length = ReadNameLength();
        Span<byte> buf = length <= 256 ? stackalloc byte[length] : new byte[length];
        ReadBytes(buf);
        return DecodeName(buf);
    }

    /// <summary>Decodes a name's bytes, rejecting invalid UTF-8 as <see cref="ReadName"/> does.</summary>
    internal static string DecodeName(ReadOnlySpan<byte> utf8)
    {
        try
        {
            return StrictUtf8.GetString(utf8);
        }
        catch (DecoderFallbackException ex)
        {
            throw new WasmFormatException(InvalidUtf8NameMessage, ex);
        }
    }

    /// <summary>
    /// Reads a name's length, rejecting one longer than the rest of the input (when its length is known) before any
    /// buffer of that size is allocated.
    /// </summary>
    private int ReadNameLength()
    {
        uint length = ReadU32Leb128();
        if (_length is { } total)
            ThrowIfPastEnd(length, total);
        if (length > Array.MaxLength)
            throw new WasmFormatException($"Name length {length} is too large");
        return (int)length;
    }

    /// <summary>
    /// Skip a UTF-8 name (§5.2.4) without decoding it. The bytes are still validated: a name that is not valid
    /// UTF-8 makes the binary malformed whether or not anyone reads it.
    /// </summary>
    public void SkipName()
    {
        int length = ReadNameLength();
        byte[]? rented = null;
        Span<byte> buf = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            ReadBytes(buf);
            if (!Utf8.IsValid(buf))
                throw new WasmFormatException(InvalidUtf8NameMessage);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Read a f32 (IEEE 754 little-endian).</summary>
    public float ReadF32()
    {
        Span<byte> buf = stackalloc byte[4];
        ReadBytes(buf);
        return BinaryPrimitives.ReadSingleLittleEndian(buf);
    }

    /// <summary>Read a f64 (IEEE 754 little-endian).</summary>
    public double ReadF64()
    {
        Span<byte> buf = stackalloc byte[8];
        ReadBytes(buf);
        return BinaryPrimitives.ReadDoubleLittleEndian(buf);
    }

    /// <summary>Read exactly <paramref name="destination"/>.Length bytes into the span.</summary>
    private void ReadInto(Span<byte> destination)
    {
        int read = ReadAvailable(destination);
        if (read < destination.Length)
            throw UnexpectedEnd((ulong)destination.Length, read);
    }

    /// <summary>Fills <paramref name="destination"/> until it is full or the stream ends; returns the bytes read.</summary>
    private int ReadAvailable(Span<byte> destination)
    {
        int total = 0;
        if (_hasBufferedByte && !destination.IsEmpty)
        {
            destination[0] = _bufferedByte;
            _hasBufferedByte = false;
            _position++;
            total = 1;
        }

        while (total < destination.Length)
        {
            int read = _stream.Read(destination[total..]);
            if (read == 0)
                break;
            total += read;
            _position += read;
        }

        return total;
    }

    private bool TryBufferNextByte()
    {
        if (_hasBufferedByte)
            return true;

        int value = _stream.ReadByte();
        if (value < 0)
            return false;

        _bufferedByte = (byte)value;
        _hasBufferedByte = true;
        return true;
    }
}
