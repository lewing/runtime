// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Low-level reader for WebAssembly binary format primitives.
/// Handles LEB128 encoding, vectors, names, and raw bytes for both seekable and non-seekable streams.
/// </summary>
public sealed class WasmBinaryReader
{
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly BinaryReader _reader;
    private readonly long? _length;
    private long _position;
    private bool _hasBufferedByte;
    private byte _bufferedByte;

    public WasmBinaryReader(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        _reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        if (stream.CanSeek)
        {
            _position = stream.Position;
            _length = stream.Length;
        }
    }

    public WasmBinaryReader(BinaryReader reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        _reader = reader;
        Stream stream = reader.BaseStream;
        if (stream.CanSeek)
        {
            _position = stream.Position;
            _length = stream.Length;
        }
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

        try
        {
            byte value = _reader.ReadByte();
            _position++;
            return value;
        }
        catch (EndOfStreamException ex)
        {
            throw new WasmFormatException("Unexpected end of stream while reading byte", ex);
        }
    }

    /// <summary>Read exactly <paramref name="count"/> bytes.</summary>
    public byte[] ReadBytes(int count)
    {
        byte[] data = new byte[count];
        ReadBytes(data);
        return data;
    }

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
    public T[] ReadVector<T>(Func<T> readElement)
    {
        uint count = ReadU32Leb128();
        var items = new T[count];
        for (uint i = 0; i < count; i++)
            items[i] = readElement();
        return items;
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

    /// <summary>Read a UTF-8 name (§5.2.4) — length-prefixed string.</summary>
    public string ReadName()
    {
        uint length = ReadU32Leb128();
        Span<byte> buf = length <= 256 ? stackalloc byte[(int)length] : new byte[(int)length];
        ReadBytes(buf);
        try
        {
            return StrictUtf8.GetString(buf);
        }
        catch (DecoderFallbackException ex)
        {
            throw new WasmFormatException("Invalid UTF-8 encoding in name", ex);
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
            int read = _reader.BaseStream.Read(destination[total..]);
            if (read == 0)
                throw new WasmFormatException(
                    $"Unexpected end of stream: expected {destination.Length} bytes but only {total} available");
            total += read;
            _position += read;
        }
    }

    private bool TryBufferNextByte()
    {
        if (_hasBufferedByte)
            return true;

        int value = _reader.BaseStream.ReadByte();
        if (value < 0)
            return false;

        _bufferedByte = (byte)value;
        _hasBufferedByte = true;
        return true;
    }
}
