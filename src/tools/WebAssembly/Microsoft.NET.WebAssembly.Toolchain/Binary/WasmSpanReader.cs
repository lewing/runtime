// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Unicode;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// A forward-only cursor over WebAssembly binary bytes that are already in memory: a module, a section, a
/// subsection or a function body. It is the engine <see cref="ModuleReader"/> parses every section with.
/// </summary>
/// <remarks>
/// <para>
/// Every read is bounded by the span: a length or count the remaining bytes cannot hold is rejected before
/// anything is allocated for it, and <see cref="ReadBytes(int)"/> returns a slice rather than a copy. A caller
/// that keeps bytes beyond the life of the span (a function body, a data segment, custom section content) must
/// copy them, because the span may be a pooled buffer.
/// </para>
/// <para>
/// Failures throw <see cref="WasmFormatException"/> whose <see cref="WasmFormatException.BinaryOffset"/> is the
/// absolute offset of the byte that could not be read: <c>baseOffset</c> plus the position in the span. The
/// messages are the ones <see cref="WasmBinaryReader"/> reports for the same input.
/// </para>
/// </remarks>
internal ref struct WasmSpanReader
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly long _baseOffset;
    private int _position;

    /// <param name="data">The bytes to read.</param>
    /// <param name="baseOffset">The absolute offset of <paramref name="data"/>'s first byte, for diagnostics.</param>
    public WasmSpanReader(ReadOnlySpan<byte> data, long baseOffset = 0)
    {
        _data = data;
        _baseOffset = baseOffset;
        _position = 0;
    }

    /// <summary>The position in the span.</summary>
    public readonly int Position => _position;

    /// <summary>The length of the span.</summary>
    public readonly int Length => _data.Length;

    /// <summary>The bytes left to read.</summary>
    public readonly int Remaining => _data.Length - _position;

    public readonly bool HasMore => _position < _data.Length;

    /// <summary>The absolute offset of the next byte.</summary>
    public readonly long AbsolutePosition => _baseOffset + _position;

    /// <summary>The bytes not yet read, without advancing.</summary>
    public readonly ReadOnlySpan<byte> RemainingBytes => _data[_position..];

    /// <summary>The bytes read since <paramref name="start"/>, a position previously read from <see cref="Position"/>.</summary>
    public readonly ReadOnlySpan<byte> ConsumedSince(int start) => _data[start.._position];

    public readonly byte PeekByte()
    {
        if ((uint)_position < (uint)_data.Length)
            return _data[_position];
        throw Error("Unexpected end of stream while peeking byte", _position);
    }

    public byte ReadByte()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            _position = position + 1;
            return _data[position];
        }

        throw Error("Unexpected end of stream while reading byte", position);
    }

    /// <summary>Reads an unsigned 32-bit LEB128 (§5.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadU32()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            byte first = _data[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return first;
            }
        }

        return ReadU32Slow();
    }

    /// <summary>Reads a signed 32-bit LEB128 (§5.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public int ReadS32()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            byte first = _data[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return (first << 25) >> 25;
            }
        }

        return ReadS32Slow();
    }

    /// <summary>Reads an unsigned 64-bit LEB128 (§5.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public ulong ReadU64()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            byte first = _data[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return first;
            }
        }

        return ReadU64Slow();
    }

    /// <summary>Reads a signed 64-bit LEB128 (§5.2.2).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadS64()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            byte first = _data[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return (long)((first << 25) >> 25);
            }
        }

        return ReadS64Slow();
    }

    /// <summary>Reads a signed 33-bit LEB128: a heap or block type (§5.3.3, §5.4.1).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public long ReadS33()
    {
        int position = _position;
        if ((uint)position < (uint)_data.Length)
        {
            byte first = _data[position];
            if (first < 0x80)
            {
                _position = position + 1;
                return (long)((first << 25) >> 25);
            }
        }

        return ReadS33Slow();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private uint ReadU32Slow()
    {
        Leb128Status status = WasmLeb128.DecodeU32(_data[_position..], out uint value, out int length);
        Advance(status, length, WasmLeb128.Leb128Kind.U32);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private int ReadS32Slow()
    {
        Leb128Status status = WasmLeb128.DecodeS32(_data[_position..], out int value, out int length);
        Advance(status, length, WasmLeb128.Leb128Kind.S32);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private ulong ReadU64Slow()
    {
        Leb128Status status = WasmLeb128.DecodeU64(_data[_position..], out ulong value, out int length);
        Advance(status, length, WasmLeb128.Leb128Kind.U64);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long ReadS64Slow()
    {
        Leb128Status status = WasmLeb128.DecodeS64(_data[_position..], out long value, out int length);
        Advance(status, length, WasmLeb128.Leb128Kind.S64);
        return value;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private long ReadS33Slow()
    {
        Leb128Status status = WasmLeb128.DecodeS33(_data[_position..], out long value, out int length);
        Advance(status, length, WasmLeb128.Leb128Kind.S33);
        return value;
    }

    private void Advance(Leb128Status status, int length, WasmLeb128.Leb128Kind kind)
    {
        if (status == Leb128Status.Ok)
        {
            _position += length;
            return;
        }

        // A stream reader runs out of bytes before it can decode, so a truncated encoding reports the end of the
        // input, at the byte that is missing, exactly as WasmBinaryReader does.
        throw status == Leb128Status.Truncated
            ? Error("Unexpected end of stream while reading byte", _position + length)
            : Error(WasmLeb128.CreateMessage(status, kind), _position);
    }

    /// <summary>
    /// Reads a vector count, rejecting one whose elements, at <paramref name="minElementSize"/> bytes each, cannot
    /// fit in the remaining bytes. Callers that size an allocation by the count use this.
    /// </summary>
    public uint ReadVectorCount(int minElementSize = 1)
    {
        int start = _position;
        uint count = ReadU32();
        ulong needed = (ulong)count * (uint)minElementSize;
        if (needed > (ulong)Remaining)
        {
            throw Error(
                $"Unexpected end of stream: vector of {count} elements needs at least {needed} bytes but only {Remaining} available",
                start);
        }

        return count;
    }

    /// <summary>Returns the next <paramref name="length"/> bytes as a slice of the input, and advances past them.</summary>
    /// <exception cref="WasmFormatException">Fewer than <paramref name="length"/> bytes remain.</exception>
    public ReadOnlySpan<byte> ReadBytes(int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        return ReadBytes((uint)length);
    }

    /// <inheritdoc cref="ReadBytes(int)"/>
    public ReadOnlySpan<byte> ReadBytes(uint length)
    {
        int position = _position;
        if (length > (uint)(_data.Length - position))
            throw UnexpectedEnd(length);
        _position = position + (int)length;
        return _data.Slice(position, (int)length);
    }

    /// <summary>
    /// Returns a reader over the next <paramref name="length"/> bytes, which reports offsets in the same coordinates
    /// as this one, and advances past them.
    /// </summary>
    /// <exception cref="WasmFormatException">Fewer than <paramref name="length"/> bytes remain.</exception>
    public WasmSpanReader Sub(int length)
    {
        long baseOffset = AbsolutePosition;
        return new WasmSpanReader(ReadBytes(length), baseOffset);
    }

    /// <summary>Throws unless every byte has been read.</summary>
    public readonly void ExpectEnd(string context)
    {
        if (HasMore)
            throw Error($"Unexpected trailing bytes in {context}.", _position);
    }

    /// <summary>Reads a name's bytes (§5.2.4), rejecting a length past the end and bytes that are not UTF-8.</summary>
    public ReadOnlySpan<byte> ReadNameBytes()
    {
        uint length = ReadU32();
        int start = _position;
        ReadOnlySpan<byte> name = ReadBytes(length);
        if (!Utf8.IsValid(name))
            throw Error(WasmBinaryReader.InvalidUtf8NameMessage, start);
        return name;
    }

    /// <summary>Reads a name (§5.2.4).</summary>
    public string ReadName() => Encoding.UTF8.GetString(ReadNameBytes());

    /// <summary>Skips a name (§5.2.4); its bytes are still validated.</summary>
    public void SkipName() => ReadNameBytes();

    /// <summary>Reads an f32 (IEEE 754 little-endian).</summary>
    public float ReadF32() => BinaryPrimitives.ReadSingleLittleEndian(ReadBytes(4));

    /// <summary>Reads an f64 (IEEE 754 little-endian).</summary>
    public double ReadF64() => BinaryPrimitives.ReadDoubleLittleEndian(ReadBytes(8));

    private readonly WasmFormatException UnexpectedEnd(ulong expected)
        => Error($"Unexpected end of stream: expected {expected} bytes but only {Remaining} available", _position);

    private readonly WasmFormatException Error(string message, int position)
        => new(message, _baseOffset + position);
}
