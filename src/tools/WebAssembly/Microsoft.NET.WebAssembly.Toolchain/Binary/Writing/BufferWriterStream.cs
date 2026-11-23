// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>A seekable scratch stream that also accepts <see cref="IBufferWriter{T}"/> producers.</summary>
internal sealed class BufferWriterStream : MemoryStream, IBufferWriter<byte>
{
    private int _reservationStart = -1;
    private int _reservationLength;

    public BufferWriterStream(bool allowInvalidModuleEntries) =>
        Entries = new WasmModuleEntryWriter(this, allowInvalidModuleEntries);

    public WasmModuleEntryWriter Entries { get; }

    public void Advance(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (_reservationStart < 0 || Position != _reservationStart)
            throw new InvalidOperationException("Reserve a buffer before advancing, without moving the stream position.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _reservationLength);
        base.Write(GetBuffer(), _reservationStart, count);
        _reservationStart = -1;
        _reservationLength = 0;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        if (Position != Length)
        {
            base.Write(buffer);
            return;
        }
        int start = Reserve(buffer.Length);
        buffer.CopyTo(GetBuffer().AsSpan(start));
        Advance(buffer.Length);
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        int start = Reserve(sizeHint);
        return GetBuffer().AsMemory(start);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        int start = Reserve(sizeHint);
        return GetBuffer().AsSpan(start);
    }

    private int Reserve(int sizeHint)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
        if (Position != Length)
            throw new InvalidOperationException("The buffer writer must append at the end of the stream.");
        long required = Position + Math.Max(1, sizeHint);
        if (required > Array.MaxLength)
            throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (required > Capacity)
            Capacity = (int)Math.Min(Array.MaxLength, Math.Max(required, Math.Max(256L, Capacity * 2L)));
        _reservationStart = (int)Position;
        _reservationLength = Capacity - _reservationStart;
        return _reservationStart;
    }
}
