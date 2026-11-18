// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Streams one name-map subsection of the <c>name</c> custom section (core spec appendix 7.4: function, global,
/// data segment and other index → name maps) into an open section of a <see cref="WasmModuleWriter"/>, without
/// buffering it. The caller sizes the map up front with <see cref="GetEntrySize"/> and
/// <see cref="GetSubsectionSize"/>, which is what lets a framework-sized map (tens of megabytes) go straight to the
/// output. Indices must be strictly ascending, as the format requires; that and the declared count and size are
/// enforced.
/// </summary>
public sealed class NameMapWriter
{
    private readonly WasmModuleWriter _writer;
    private readonly int _count;
    private long _remaining;
    private int _written;
    private long _previous = -1;

    /// <summary>
    /// Writes the subsection header (ID, size and entry count) into <paramref name="writer"/>'s open section.
    /// </summary>
    /// <param name="writer">A writer with an open section, typically begun with <c>BeginCustomSection("name"u8, …)</c>.</param>
    /// <param name="id">
    /// The subsection: one whose content is a flat index → name map (function, type, table, memory, global, element
    /// segment, data segment or tag). Module (a single name) and local, label and field (index → name map maps) are not.
    /// </param>
    /// <param name="count">The number of entries that will follow.</param>
    /// <param name="entriesSize">The total of <see cref="GetEntrySize"/> over those entries.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not a flat name map.</exception>
    public NameMapWriter(WasmModuleWriter writer, NameSubsectionId id, int count, long entriesSize)
    {
        ArgumentNullException.ThrowIfNull(writer);
        if (!IsNameMap(id))
            throw new ArgumentException($"The {id} name subsection is not an index → name map.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(entriesSize);
        _writer = writer;
        _count = count;
        _remaining = entriesSize;

        long payload = WasmLeb128.GetULeb128Size((ulong)count) + entriesSize;
        Span<byte> header = stackalloc byte[1 + 2 * WasmLeb128.MaxLength];
        header[0] = (byte)id;
        int length = 1 + WasmLeb128.WriteULeb128(header[1..], checked((ulong)payload));
        length += WasmLeb128.WriteULeb128(header[length..], (ulong)count);
        writer.Write(header[..length]);
    }

    // Listed rather than excluded, so a subsection ID added later is rejected until its layout is known.
    private static bool IsNameMap(NameSubsectionId id) => id is NameSubsectionId.Function or NameSubsectionId.Type or
        NameSubsectionId.Table or NameSubsectionId.Memory or NameSubsectionId.Global or NameSubsectionId.ElemSegment or
        NameSubsectionId.DataSegment or NameSubsectionId.Tag;

    /// <summary>The encoded size of one entry: the index, the name's length and the name.</summary>
    public static long GetEntrySize(uint index, int utf8NameLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf8NameLength);
        return WasmLeb128.GetULeb128Size(index) + WasmLeb128.GetULeb128Size((ulong)utf8NameLength) + utf8NameLength;
    }

    /// <summary>
    /// The encoded size of a whole subsection (ID, size, count and entries), to add up into the <c>name</c> section's
    /// payload size.
    /// </summary>
    public static long GetSubsectionSize(int count, long entriesSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(entriesSize);
        long payload = WasmLeb128.GetULeb128Size((ulong)count) + entriesSize;
        return 1 + WasmLeb128.GetULeb128Size((ulong)payload) + payload;
    }

    /// <summary>Writes the next entry.</summary>
    /// <exception cref="InvalidOperationException">
    /// The index is not greater than the previous one, or the entry exceeds the declared count or size.
    /// </exception>
    public void Write(uint index, ReadOnlySpan<byte> utf8Name)
    {
        if (index <= _previous)
            throw new InvalidOperationException($"Name map indices must be strictly ascending: {index} follows {_previous}.");
        if (_written == _count)
            throw new InvalidOperationException($"The name map was declared with {_count} entries.");
        long size = GetEntrySize(index, utf8Name.Length);
        if (size > _remaining)
            throw new InvalidOperationException($"The entry for index {index} exceeds the name map's declared size by {size - _remaining} bytes.");

        Span<byte> prefix = stackalloc byte[2 * WasmLeb128.MaxLength];
        int length = WasmLeb128.WriteULeb128(prefix, index);
        length += WasmLeb128.WriteULeb128(prefix[length..], (ulong)utf8Name.Length);
        _writer.Write(prefix[..length]);
        _writer.Write(utf8Name);
        _remaining -= size;
        _previous = index;
        _written++;
    }

    /// <summary>Checks that the declared number of entries and bytes were written.</summary>
    /// <exception cref="InvalidOperationException">Fewer entries or bytes were written than declared.</exception>
    public void Complete()
    {
        if (_written != _count || _remaining != 0)
            throw new InvalidOperationException($"The name map was declared with {_count} entries; {_written} were written, {_remaining} declared bytes remain.");
    }
}
