// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// An <see cref="IBufferWriter{T}"/> over an array computed to be exactly the size of what will be written, so
/// content sized up front is built once, in its final array. It never grows: a request for more than the remaining
/// space throws, so it suits writers that ask for exactly what they write (the name subsection writers), not ones that
/// reserve a maximum length.
/// </summary>
internal sealed class ExactArrayBufferWriter : IBufferWriter<byte>
{
    private readonly byte[] _buffer;
    private int _written;

    public ExactArrayBufferWriter(long size)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(size);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(size, Array.MaxLength);
        _buffer = new byte[(int)size];
    }

    /// <summary>The array being filled.</summary>
    public byte[] Buffer => _buffer;

    public void Advance(int count)
    {
        if ((uint)count > (uint)(_buffer.Length - _written))
            throw new InvalidOperationException($"Advancing {count} bytes overruns the buffer's remaining {_buffer.Length - _written} bytes.");
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => _buffer.AsMemory(Reserve(sizeHint));

    public Span<byte> GetSpan(int sizeHint = 0) => _buffer.AsSpan(Reserve(sizeHint));

    /// <summary>Returns the array, checking that it was filled exactly.</summary>
    public byte[] ToArray()
    {
        if (_written != _buffer.Length)
            throw new InvalidOperationException($"{_written} of the buffer's {_buffer.Length} computed bytes were written.");
        return _buffer;
    }

    private int Reserve(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (sizeHint == 0)
            sizeHint = 1;
        if (sizeHint > _buffer.Length - _written)
            throw new InvalidOperationException($"{sizeHint} bytes were requested, but only {_buffer.Length - _written} of the computed size remain.");
        return _written;
    }
}
