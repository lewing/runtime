// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Text;

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
        uint result = 0;
        int shift = 0;

        for (int i = 0; i < 5; i++)
        {
            byte b = ReadByte();
            // For the 5th byte only the lowest 4 bits may be set (bits 28-31).
            if (i == 4 && (b & 0xF0) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint32");

            result |= (uint)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }

        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 5 bytes for uint32");
    }

    /// <summary>Read a signed 32-bit LEB128 integer (§5.2.2).</summary>
    public int ReadS32Leb128()
    {
        int result = 0;
        int shift = 0;
        byte b;

        for (int i = 0; i < 5; i++)
        {
            b = ReadByte();

            // 5th byte: bits 28-31 are value; bits 32-34 (bits 4-6 of byte) must equal
            // the sign bit (bit 3) — otherwise the value overflows a 32-bit signed integer.
            if (i == 4 && (b & 0x78) != 0 && (b & 0x78) != 0x78)
                throw new WasmFormatException("Signed LEB128 value overflows int32");

            result |= (int)(b & 0x7F) << shift;
            shift += 7;

            if ((b & 0x80) == 0)
            {
                // Sign extend if the sign bit of the last byte is set.
                if (shift < 32 && (b & 0x40) != 0)
                    result |= ~0 << shift;
                return result;
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for int32");
    }

    /// <summary>Read an unsigned 64-bit LEB128 integer (§5.2.2).</summary>
    public ulong ReadU64Leb128()
    {
        ulong result = 0;
        int shift = 0;

        for (int i = 0; i < 10; i++)
        {
            byte b = ReadByte();
            if (i == 9 && (b & 0xFE) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint64");

            result |= (ulong)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
                return result;

            shift += 7;
        }

        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 10 bytes for uint64");
    }

    /// <summary>Read a signed 64-bit LEB128 integer (§5.2.2).</summary>
    public long ReadS64Leb128()
    {
        long result = 0;
        int shift = 0;
        byte b;

        for (int i = 0; i < 10; i++)
        {
            b = ReadByte();

            // 10th byte: bit 63 of the value is bit 0 of this byte; bits 64-69 (bits 1-6)
            // must all equal bit 0 — otherwise the value overflows a 64-bit signed integer.
            if (i == 9 && (b & 0x7F) != 0 && (b & 0x7F) != 0x7F)
                throw new WasmFormatException("Signed LEB128 value overflows int64");

            result |= (long)(b & 0x7F) << shift;
            shift += 7;

            if ((b & 0x80) == 0)
            {
                // Sign extend if the sign bit of the last byte is set.
                if (shift < 64 && (b & 0x40) != 0)
                    result |= ~0L << shift;
                return result;
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 10 bytes for int64");
    }

    /// <summary>
    /// Read a signed 33-bit LEB128 integer used for GC heap type encoding.
    /// Positive values are type indices; negative values are abstract heap type codes.
    /// </summary>
    public long ReadS33Leb128()
    {
        Span<byte> encoded = stackalloc byte[S33Leb128.MaxBytes];
        int length = 0;
        for (; length < encoded.Length; length++)
        {
            byte current = ReadByte();
            encoded[length] = current;
            S33Leb128.ValidateTerminalByte(current, length);
            if ((current & 0x80) == 0)
            {
                int offset = 0;
                return S33Leb128.Read(encoded[..(length + 1)], ref offset);
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for s33");
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
        if (length <= 256)
        {
            Span<byte> buf = stackalloc byte[(int)length];
            ReadBytes(buf);
            try { return StrictUtf8.GetString(buf); }
            catch (DecoderFallbackException ex)
            { throw new WasmFormatException("Invalid UTF-8 encoding in name", ex); }
        }
        byte[] bytes = ReadBytes((int)length);
        try
        {
            return StrictUtf8.GetString(bytes);
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
