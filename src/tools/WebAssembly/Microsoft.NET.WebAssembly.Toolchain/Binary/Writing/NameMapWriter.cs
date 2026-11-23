// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Streams one name-map subsection of the <c>name</c> custom section (core spec appendix 7.4: function, global,
/// data segment and other index → name maps) into an open section of a <see cref="WasmModuleWriter"/>, or into an
/// <see cref="IBufferWriter{T}"/>, without buffering it. The caller sizes the map up front with
/// <see cref="GetEntrySize"/> and <see cref="GetSubsectionSize"/>, which is what lets a framework-sized map (tens of
/// megabytes) go straight to the output. Indices must be strictly ascending, as the format requires; that and the
/// declared count and size are enforced.
/// </summary>
/// <remarks>
/// A <c>name</c> section's other subsections are written with <see cref="ModuleNameWriter"/> (the module's name) and
/// <see cref="IndirectNameMapWriter"/> (local, label and field names). Subsections go in ascending ID order.
/// </remarks>
public sealed class NameMapWriter
{
    private readonly NameSink _output;
    private NameMapCursor _cursor;

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
    /// <exception cref="ArgumentOutOfRangeException">A count or size is negative, or the payload exceeds a u32 size.</exception>
    public NameMapWriter(WasmModuleWriter writer, NameSubsectionId id, int count, long entriesSize)
        : this(NameSink.For(writer), id, count, entriesSize)
    {
    }

    /// <summary>
    /// Writes the subsection header (ID, size and entry count) to <paramref name="output"/>, to build a <c>name</c>
    /// section's payload in memory. The entries' order, count and size are checked as for a module writer.
    /// </summary>
    /// <inheritdoc cref="NameMapWriter(WasmModuleWriter, NameSubsectionId, int, long)"/>
    public NameMapWriter(IBufferWriter<byte> output, NameSubsectionId id, int count, long entriesSize)
        : this(NameSink.For(output), id, count, entriesSize)
    {
    }

    private NameMapWriter(NameSink output, NameSubsectionId id, int count, long entriesSize)
    {
        if (!IsNameMap(id))
            throw new ArgumentException($"The {id} name subsection is not an index → name map.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(entriesSize);
        _output = output;
        _cursor = new NameMapCursor(count, entriesSize);
        output.WriteSubsectionHeader(id, count, entriesSize);
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

    /// <summary>Writes the next entry. A rejected entry writes nothing, and the map can go on.</summary>
    /// <exception cref="ArgumentException">The index is not greater than the previous one.</exception>
    /// <exception cref="InvalidOperationException">The entry exceeds the declared count or size.</exception>
    public void Write(uint index, ReadOnlySpan<byte> utf8Name)
    {
        long size = GetEntrySize(index, utf8Name.Length);
        _cursor.Check(index, size, "name map");
        _output.WriteNameEntry(index, utf8Name);
        _cursor.Advance(index, size);
    }

    /// <summary>Checks that the declared number of entries and bytes were written.</summary>
    /// <exception cref="InvalidOperationException">Fewer entries or bytes were written than declared.</exception>
    public void Complete() => _cursor.CheckComplete("name map");
}
