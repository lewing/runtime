// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>Where the name subsection writers write: an open section of a module writer, or a buffer writer.</summary>
internal readonly struct NameSink
{
    private readonly WasmModuleWriter? _module;
    private readonly IBufferWriter<byte>? _buffer;

    private NameSink(WasmModuleWriter? module, IBufferWriter<byte>? buffer)
    {
        _module = module;
        _buffer = buffer;
    }

    public static NameSink For(WasmModuleWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        return new NameSink(writer, null);
    }

    public static NameSink For(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        return new NameSink(null, output);
    }

    public void Write(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;
        if (_module is not null)
        {
            _module.Write(bytes);
            return;
        }

        bytes.CopyTo(_buffer!.GetSpan(bytes.Length));
        _buffer.Advance(bytes.Length);
    }

    public void WriteSubsectionHeader(NameSubsectionId id, int count, long entriesSize)
    {
        int countSize = WasmLeb128.GetULeb128Size((ulong)count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(entriesSize, uint.MaxValue - countSize);
        long payload = countSize + entriesSize;
        Span<byte> header = stackalloc byte[1 + 2 * WasmLeb128.MaxLength];
        header[0] = (byte)id;
        int length = 1 + WasmLeb128.WriteULeb128(header[1..], checked((ulong)payload));
        length += WasmLeb128.WriteULeb128(header[length..], (ulong)count);
        Write(header[..length]);
    }

    public void WriteNameEntry(uint index, ReadOnlySpan<byte> utf8Name)
    {
        Span<byte> prefix = stackalloc byte[2 * WasmLeb128.MaxLength];
        int length = WasmLeb128.WriteULeb128(prefix, index);
        length += WasmLeb128.WriteULeb128(prefix[length..], (ulong)utf8Name.Length);
        Write(prefix[..length]);
        Write(utf8Name);
    }
}

/// <summary>The declared count and size of a name map, and the entries written so far.</summary>
internal struct NameMapCursor(int count, long entriesSize)
{
    private readonly int _count = count;
    private long _remaining = entriesSize;
    private int _written;
    private long _previous = -1;

    // Checked before anything is written, so that a rejected entry leaves the map usable.
    public readonly void Check(uint index, long size, string what)
    {
        if (index <= _previous)
            throw new ArgumentException($"Name map indices must be strictly ascending: {index} follows {_previous}.", nameof(index));
        if (_written == _count)
            throw new InvalidOperationException($"The {what} was declared with {_count} entries.");
        if (size > _remaining)
            throw new InvalidOperationException($"The entry for index {index} exceeds the {what}'s declared size by {size - _remaining} bytes.");
    }

    public void Advance(uint index, long size)
    {
        _remaining -= size;
        _previous = index;
        _written++;
    }

    public readonly void CheckComplete(string what)
    {
        if (_written != _count || _remaining != 0)
            throw new InvalidOperationException($"The {what} was declared with {_count} entries; {_written} were written, {_remaining} declared bytes remain.");
    }
}
