// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Streams one indirect name-map subsection of the <c>name</c> custom section: an index → name map for each of a
/// strictly ascending set of outer indices, such as each function's local names (subsection 2), labels (3) or each
/// type's field names (10). Like <see cref="NameMapWriter"/>, it is sized up front and enforces the declared counts,
/// sizes and the order of both the outer and the inner indices.
/// </summary>
/// <remarks>
/// Size each inner map with <see cref="NameMapWriter.GetEntrySize"/>, each outer entry with <see cref="GetEntrySize"/>
/// and the subsection with <see cref="GetSubsectionSize"/>. Then call <see cref="BeginMap"/> for each outer entry and
/// <see cref="Write"/> for each of its names, and <see cref="Complete"/> at the end.
/// </remarks>
public sealed class IndirectNameMapWriter
{
    private readonly NameSink _output;
    private NameMapCursor _outer;
    private NameMapCursor _inner;
    private bool _hasInner;

    /// <summary>
    /// Writes the subsection header (ID, size and outer entry count) into <paramref name="writer"/>'s open section.
    /// </summary>
    /// <param name="writer">A writer with an open section, typically begun with <c>BeginCustomSection("name"u8, …)</c>.</param>
    /// <param name="id">The subsection: local, label or field names.</param>
    /// <param name="count">The number of outer entries that will follow.</param>
    /// <param name="entriesSize">The total of <see cref="GetEntrySize"/> over those entries.</param>
    /// <exception cref="ArgumentException"><paramref name="id"/> is not an indirect name map.</exception>
    /// <exception cref="ArgumentOutOfRangeException">A count or size is negative, or the payload exceeds a u32 size.</exception>
    public IndirectNameMapWriter(WasmModuleWriter writer, NameSubsectionId id, int count, long entriesSize)
        : this(NameSink.For(writer), id, count, entriesSize)
    {
    }

    /// <summary>Writes the subsection header (ID, size and outer entry count) to <paramref name="output"/>.</summary>
    /// <inheritdoc cref="IndirectNameMapWriter(WasmModuleWriter, NameSubsectionId, int, long)"/>
    public IndirectNameMapWriter(IBufferWriter<byte> output, NameSubsectionId id, int count, long entriesSize)
        : this(NameSink.For(output), id, count, entriesSize)
    {
    }

    private IndirectNameMapWriter(NameSink output, NameSubsectionId id, int count, long entriesSize)
    {
        if (id is not (NameSubsectionId.Local or NameSubsectionId.Label or NameSubsectionId.Field))
            throw new ArgumentException($"The {id} name subsection is not an indirect name map.", nameof(id));
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(entriesSize);
        _output = output;
        _outer = new NameMapCursor(count, entriesSize);
        _inner = new NameMapCursor(0, 0);
        output.WriteSubsectionHeader(id, count, entriesSize);
    }

    /// <summary>
    /// The encoded size of one outer entry: the index, then an inner map of <paramref name="count"/> names whose
    /// <see cref="NameMapWriter.GetEntrySize"/> total is <paramref name="entriesSize"/>.
    /// </summary>
    public static long GetEntrySize(uint index, int count, long entriesSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfNegative(entriesSize);
        return WasmLeb128.GetULeb128Size(index) + WasmLeb128.GetULeb128Size((ulong)count) + entriesSize;
    }

    /// <summary>The encoded size of a whole subsection (ID, size, count and entries).</summary>
    public static long GetSubsectionSize(int count, long entriesSize) => NameMapWriter.GetSubsectionSize(count, entriesSize);

    /// <summary>
    /// Begins the next outer entry: writes its index and the count of the inner map that follows. A rejected entry
    /// writes nothing.
    /// </summary>
    /// <param name="index">The outer index, such as a function index.</param>
    /// <param name="count">The number of names in the inner map.</param>
    /// <param name="entriesSize">The total of <see cref="NameMapWriter.GetEntrySize"/> over those names.</param>
    /// <exception cref="ArgumentException">The index is not greater than the previous outer index.</exception>
    /// <exception cref="InvalidOperationException">
    /// The previous inner map is incomplete, or the entry exceeds the declared count or size.
    /// </exception>
    public void BeginMap(uint index, int count, long entriesSize)
    {
        long size = GetEntrySize(index, count, entriesSize);
        _inner.CheckComplete("inner name map");
        _outer.Check(index, size, "indirect name map");
        Span<byte> prefix = stackalloc byte[2 * WasmLeb128.MaxLength];
        int length = WasmLeb128.WriteULeb128(prefix, index);
        length += WasmLeb128.WriteULeb128(prefix[length..], (ulong)count);
        _output.Write(prefix[..length]);
        _outer.Advance(index, size);
        _inner = new NameMapCursor(count, entriesSize);
        _hasInner = true;
    }

    /// <summary>Writes the next name of the current inner map. A rejected entry writes nothing.</summary>
    /// <exception cref="ArgumentException">The index is not greater than the previous one in this inner map.</exception>
    /// <exception cref="InvalidOperationException">No inner map has begun, or the entry exceeds its declared count or size.</exception>
    public void Write(uint index, ReadOnlySpan<byte> utf8Name)
    {
        if (!_hasInner)
            throw new InvalidOperationException("Call BeginMap first to begin an inner name map.");
        long size = NameMapWriter.GetEntrySize(index, utf8Name.Length);
        _inner.Check(index, size, "inner name map");
        _output.WriteNameEntry(index, utf8Name);
        _inner.Advance(index, size);
    }

    /// <summary>Checks that the declared number of outer and inner entries and bytes were written.</summary>
    /// <exception cref="InvalidOperationException">Fewer entries or bytes were written than declared.</exception>
    public void Complete()
    {
        _inner.CheckComplete("inner name map");
        _outer.CheckComplete("indirect name map");
    }
}
