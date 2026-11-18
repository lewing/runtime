// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Linking;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes the payload of a <c>linking</c> custom section (LLVM's tool-conventions Linking.md): the version
/// followed by the subsections in the order they are written. Write it with
/// <see cref="WasmModuleWriter.WriteCustomSection(ReadOnlySpan{byte}, IWasmSectionPayload, int)"/> under the
/// name <c>linking</c>, after the sections its symbols and segments refer to.
/// </summary>
public sealed class LinkingSectionEncoder : IWasmSectionPayload
{
    /// <summary>The linking metadata version this encoder writes.</summary>
    public const uint Version = LinkingSection.CurrentVersion;

    private readonly MemoryStream _buffer = new();

    public LinkingSectionEncoder()
    {
        WasmLeb128.WriteULeb128(_buffer, Version);
    }

    /// <inheritdoc />
    public long Length => _buffer.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination) => _buffer.WriteTo(destination);

    /// <summary>Appends a <see cref="LinkingSubsectionId.SymbolTable"/> subsection.</summary>
    public void WriteSymbolTable(SymbolTableEncoder symbols, int sizeFieldWidth = 0) => WriteSubsection(LinkingSubsectionId.SymbolTable, symbols, sizeFieldWidth);

    /// <summary>Appends a <see cref="LinkingSubsectionId.SegmentInfo"/> subsection.</summary>
    public void WriteSegmentInfo(SegmentInfoEncoder segments, int sizeFieldWidth = 0) => WriteSubsection(LinkingSubsectionId.SegmentInfo, segments, sizeFieldWidth);

    /// <summary>Appends a <see cref="LinkingSubsectionId.InitFunctions"/> subsection.</summary>
    public void WriteInitFunctions(InitFunctionsEncoder initFunctions, int sizeFieldWidth = 0) => WriteSubsection(LinkingSubsectionId.InitFunctions, initFunctions, sizeFieldWidth);

    /// <summary>Appends a <see cref="LinkingSubsectionId.ComdatInfo"/> subsection.</summary>
    public void WriteComdatInfo(ComdatInfoEncoder comdats, int sizeFieldWidth = 0) => WriteSubsection(LinkingSubsectionId.ComdatInfo, comdats, sizeFieldWidth);

    /// <summary>Appends a subsection with the given ID and payload.</summary>
    /// <param name="id">The subsection ID.</param>
    /// <param name="payload">The subsection payload.</param>
    /// <param name="sizeFieldWidth">
    /// 0 for a minimal size field, or the width (1–5) of a padded one; LLVM's object writer pads every
    /// subsection size to 5 bytes.
    /// </param>
    public void WriteSubsection(LinkingSubsectionId id, IWasmSectionPayload payload, int sizeFieldWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        long start = _buffer.Length;
        WriteSubsectionHeader(id, payload.Length, sizeFieldWidth);
        long payloadStart = _buffer.Length;
        payload.WriteTo(_buffer);
        if (_buffer.Length - payloadStart != payload.Length)
        {
            _buffer.SetLength(start);
            throw new ArgumentException("The payload wrote a different number of bytes than its Length.", nameof(payload));
        }
    }

    /// <summary>Appends a subsection with the given ID and payload.</summary>
    /// <param name="id">The subsection ID.</param>
    /// <param name="payload">The subsection payload.</param>
    /// <param name="sizeFieldWidth">0 for a minimal size field, or the width (1–5) of a padded one.</param>
    public void WriteSubsection(LinkingSubsectionId id, ReadOnlySpan<byte> payload, int sizeFieldWidth = 0)
    {
        WriteSubsectionHeader(id, payload.Length, sizeFieldWidth);
        _buffer.Write(payload);
    }

    private void WriteSubsectionHeader(LinkingSubsectionId id, long payloadLength, int sizeFieldWidth)
    {
        // A subsection header has the same shape as a section header: an ID byte and a size field.
        Span<byte> header = stackalloc byte[6];
        int length = WasmModuleWriter.EncodeSectionHeader(header, (WasmSectionId)id, payloadLength, sizeFieldWidth);
        _buffer.Write(header[..length]);
    }

    /// <summary>
    /// Encodes <paramref name="model"/> as LLVM does: the symbol table, segment info, init functions and COMDAT
    /// subsections, in that order, each only when non-empty.
    /// </summary>
    /// <param name="model">The linking section, as <see cref="ModuleReader"/> models it.</param>
    /// <param name="subsectionSizeFieldWidth">
    /// The width of every subsection's size field: 0 for minimal (dotnet/runtime's writer), 5 for LLVM's.
    /// </param>
    public static LinkingSectionEncoder From(LinkingSection model, int subsectionSizeFieldWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Version != Version)
            throw new ArgumentException($"Linking section version {model.Version} is not supported; only {Version} is.", nameof(model));

        var encoder = new LinkingSectionEncoder();
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        if (model.Symbols.Count > 0)
        {
            var symbols = new SymbolTableEncoder();
            foreach (WasmSymbol symbol in model.Symbols)
                symbols.Add(symbol);
            encoder.WriteSymbolTable(symbols, subsectionSizeFieldWidth);
        }

        if (model.SegmentInfos.Count > 0)
        {
            var segments = new SegmentInfoEncoder();
            foreach (SegmentInfo segment in model.SegmentInfos)
            {
                using var name = new Utf8Scratch(segment.Name, scratch);
                segments.Add(name.Bytes, segment.Alignment, segment.Flags);
            }

            encoder.WriteSegmentInfo(segments, subsectionSizeFieldWidth);
        }

        if (model.InitFunctions.Count > 0)
        {
            var initFunctions = new InitFunctionsEncoder();
            foreach (InitFunction initFunction in model.InitFunctions)
                initFunctions.Add(initFunction.Priority, initFunction.SymbolIndex);
            encoder.WriteInitFunctions(initFunctions, subsectionSizeFieldWidth);
        }

        if (model.ComdatGroups.Count > 0)
        {
            var comdats = new ComdatInfoEncoder();
            foreach (ComdatGroup group in model.ComdatGroups)
            {
                using var name = new Utf8Scratch(group.Name, scratch);
                comdats.Add(name.Bytes, group.Flags, group.Entries);
            }

            encoder.WriteComdatInfo(comdats, subsectionSizeFieldWidth);
        }

        return encoder;
    }
}

/// <summary>A count-prefixed vector of already encoded entries.</summary>
internal sealed class VectorPayload
{
    private readonly MemoryStream _entries = new();

    public int Count { get; private set; }

    public MemoryStream Entries => _entries;

    public long Length => WasmLeb128.GetULeb128Size((ulong)Count) + _entries.Length;

    public int Complete() => Count++;

    public void WriteTo(Stream destination)
    {
        WasmLeb128.WriteULeb128(destination, (ulong)Count);
        _entries.WriteTo(destination);
    }

    public void WriteName(ReadOnlySpan<byte> utf8Name)
    {
        WasmLeb128.WriteULeb128(_entries, (ulong)utf8Name.Length);
        _entries.Write(utf8Name);
    }
}

/// <summary>
/// Encodes a <see cref="LinkingSubsectionId.SymbolTable"/> subsection one symbol at a time. Each Add method
/// returns the symbol's index, which relocations refer to.
/// </summary>
/// <remarks>
/// The format's name rule is enforced rather than silently applied: a function, global, table or tag symbol
/// carries a name unless it is undefined without <see cref="SymbolFlags.ExplicitName"/>, and passing a name
/// that would not be written throws. Wherever a name is written it must be non-empty, as LLVM's and
/// dotnet/runtime's writers always produce, so an omitted name never silently becomes an empty one.
/// </remarks>
public sealed class SymbolTableEncoder : IWasmSectionPayload
{
    private readonly VectorPayload _vector = new();

    /// <summary>The number of symbols added.</summary>
    public int Count => _vector.Count;

    /// <inheritdoc />
    public long Length => _vector.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination) => _vector.WriteTo(destination);

    /// <summary>Adds a function symbol for function <paramref name="index"/>.</summary>
    public int AddFunction(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Function, flags, index, utf8Name);

    /// <summary>Adds a global symbol for global <paramref name="index"/>.</summary>
    public int AddGlobal(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Global, flags, index, utf8Name);

    /// <summary>Adds a table symbol for table <paramref name="index"/>.</summary>
    public int AddTable(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Table, flags, index, utf8Name);

    /// <summary>Adds a tag symbol for tag <paramref name="index"/>.</summary>
    public int AddTag(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Tag, flags, index, utf8Name);

    /// <summary>Adds a defined data symbol: <paramref name="size"/> bytes at <paramref name="offset"/> in a segment.</summary>
    public int AddData(SymbolFlags flags, ReadOnlySpan<byte> utf8Name, uint segmentIndex, ulong offset, ulong size)
    {
        if (flags.HasFlag(SymbolFlags.Undefined))
            throw new ArgumentException("A defined data symbol cannot be flagged Undefined; use AddUndefinedData.", nameof(flags));
        RequireName(utf8Name);

        MemoryStream entries = _vector.Entries;
        entries.WriteByte((byte)SymbolType.Data);
        WasmLeb128.WriteULeb128(entries, (uint)flags);
        _vector.WriteName(utf8Name);
        WasmLeb128.WriteULeb128(entries, segmentIndex);
        WasmLeb128.WriteULeb128(entries, offset);
        WasmLeb128.WriteULeb128(entries, size);
        return _vector.Complete();
    }

    /// <summary>Adds an undefined data symbol; <paramref name="flags"/> must include <see cref="SymbolFlags.Undefined"/>.</summary>
    public int AddUndefinedData(SymbolFlags flags, ReadOnlySpan<byte> utf8Name)
    {
        if (!flags.HasFlag(SymbolFlags.Undefined))
            throw new ArgumentException("An undefined data symbol must be flagged Undefined.", nameof(flags));
        RequireName(utf8Name);

        _vector.Entries.WriteByte((byte)SymbolType.Data);
        WasmLeb128.WriteULeb128(_vector.Entries, (uint)flags);
        _vector.WriteName(utf8Name);
        return _vector.Complete();
    }

    /// <summary>Adds a section symbol for the section at file index <paramref name="sectionIndex"/>.</summary>
    public int AddSection(SymbolFlags flags, uint sectionIndex)
    {
        _vector.Entries.WriteByte((byte)SymbolType.Section);
        WasmLeb128.WriteULeb128(_vector.Entries, (uint)flags);
        WasmLeb128.WriteULeb128(_vector.Entries, sectionIndex);
        return _vector.Complete();
    }

    /// <summary>Adds the symbol <paramref name="symbol"/> as <see cref="ModuleReader"/> models it.</summary>
    public int Add(WasmSymbol symbol)
    {
        ArgumentNullException.ThrowIfNull(symbol);
        using var utf8 = new Utf8Scratch(symbol.Name, stackalloc byte[Utf8Scratch.StackSize]);
        ReadOnlySpan<byte> name = utf8.Bytes;
        switch (symbol.Type)
        {
            case SymbolType.Function:
            case SymbolType.Global:
            case SymbolType.Table:
            case SymbolType.Tag:
                if (HasName(symbol.Flags) && symbol.Name is null)
                    throw new ArgumentException($"{symbol.Type} symbol {symbol.Index} needs a name.", nameof(symbol));
                return AddIndexed(symbol.Type, symbol.Flags, checked((uint)Required(symbol.Index, "Index")), name);
            case SymbolType.Data when symbol.Flags.HasFlag(SymbolFlags.Undefined):
                return AddUndefinedData(symbol.Flags, name);
            case SymbolType.Data:
                return AddData(symbol.Flags, name, Required(symbol.SegmentIndex, "SegmentIndex"), Required(symbol.Offset, "Offset"), Required(symbol.Size, "Size"));
            case SymbolType.Section:
                return AddSection(symbol.Flags, Required(symbol.SectionIndex, "SectionIndex"));
            default:
                throw new ArgumentException($"Unknown symbol type {symbol.Type}.", nameof(symbol));
        }

        static T Required<T>(T? value, string field) where T : struct =>
            value ?? throw new ArgumentException($"The symbol's {field} is required.", nameof(symbol));
    }

    private int AddIndexed(SymbolType kind, SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name)
    {
        bool hasName = HasName(flags);
        if (!hasName && !utf8Name.IsEmpty)
            throw new ArgumentException("An undefined symbol without ExplicitName has no name field; pass no name or set ExplicitName.", nameof(utf8Name));
        if (hasName)
            RequireName(utf8Name);

        _vector.Entries.WriteByte((byte)kind);
        WasmLeb128.WriteULeb128(_vector.Entries, (uint)flags);
        WasmLeb128.WriteULeb128(_vector.Entries, index);
        if (hasName)
            _vector.WriteName(utf8Name);
        return _vector.Complete();
    }

    private static bool HasName(SymbolFlags flags) =>
        !flags.HasFlag(SymbolFlags.Undefined) || flags.HasFlag(SymbolFlags.ExplicitName);

    private static void RequireName(ReadOnlySpan<byte> utf8Name)
    {
        if (utf8Name.IsEmpty)
            throw new ArgumentException("This symbol's name is written to the symbol table and must not be empty.", nameof(utf8Name));
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.SegmentInfo"/> subsection, one entry per data segment in order.</summary>
public sealed class SegmentInfoEncoder : IWasmSectionPayload
{
    private readonly VectorPayload _vector = new();

    /// <summary>The number of segments added.</summary>
    public int Count => _vector.Count;

    /// <inheritdoc />
    public long Length => _vector.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination) => _vector.WriteTo(destination);

    /// <summary>Adds the next segment's name, alignment (as a power of two) and flags.</summary>
    public int Add(ReadOnlySpan<byte> utf8Name, uint alignmentLog2, SegmentFlags flags)
    {
        _vector.WriteName(utf8Name);
        WasmLeb128.WriteULeb128(_vector.Entries, alignmentLog2);
        WasmLeb128.WriteULeb128(_vector.Entries, (uint)flags);
        return _vector.Complete();
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.InitFunctions"/> subsection (constructors).</summary>
public sealed class InitFunctionsEncoder : IWasmSectionPayload
{
    private readonly VectorPayload _vector = new();

    /// <summary>The number of init functions added.</summary>
    public int Count => _vector.Count;

    /// <inheritdoc />
    public long Length => _vector.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination) => _vector.WriteTo(destination);

    /// <summary>Adds the function symbol <paramref name="symbolIndex"/> as a constructor of the given priority.</summary>
    public int Add(uint priority, uint symbolIndex)
    {
        WasmLeb128.WriteULeb128(_vector.Entries, priority);
        WasmLeb128.WriteULeb128(_vector.Entries, symbolIndex);
        return _vector.Complete();
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.ComdatInfo"/> subsection.</summary>
public sealed class ComdatInfoEncoder : IWasmSectionPayload
{
    private readonly VectorPayload _vector = new();

    /// <summary>The number of COMDAT groups added.</summary>
    public int Count => _vector.Count;

    /// <inheritdoc />
    public long Length => _vector.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination) => _vector.WriteTo(destination);

    /// <summary>Adds a COMDAT group and its members (kind and index of each function, data segment or section).</summary>
    public int Add(ReadOnlySpan<byte> utf8Name, uint flags, ReadOnlySpan<(ComdatKind Kind, uint Index)> entries)
    {
        WriteGroupHeader(utf8Name, flags, entries.Length);
        foreach ((ComdatKind kind, uint index) in entries)
            WriteEntry(kind, index);
        return _vector.Complete();
    }

    internal int Add(ReadOnlySpan<byte> utf8Name, uint flags, List<ComdatEntry> entries)
    {
        WriteGroupHeader(utf8Name, flags, entries.Count);
        foreach (ComdatEntry entry in entries)
            WriteEntry(entry.Kind, entry.Index);
        return _vector.Complete();
    }

    private void WriteGroupHeader(ReadOnlySpan<byte> utf8Name, uint flags, int entryCount)
    {
        _vector.WriteName(utf8Name);
        WasmLeb128.WriteULeb128(_vector.Entries, flags);
        WasmLeb128.WriteULeb128(_vector.Entries, (ulong)entryCount);
    }

    private void WriteEntry(ComdatKind kind, uint index)
    {
        _vector.Entries.WriteByte((byte)kind);
        WasmLeb128.WriteULeb128(_vector.Entries, index);
    }
}

/// <summary>
/// The UTF-8 encoding of a model string in a caller-supplied stack buffer, or a pooled one when it does not fit,
/// so encoding the reader models does not allocate an array per name.
/// </summary>
internal ref struct Utf8Scratch
{
    public const int StackSize = 256;

    private byte[]? _rented;

    public Utf8Scratch(string? value, Span<byte> stackBuffer)
    {
        if (string.IsNullOrEmpty(value))
        {
            Bytes = default;
            return;
        }

        int max = Encoding.UTF8.GetMaxByteCount(value.Length);
        Span<byte> buffer = max <= stackBuffer.Length ? stackBuffer : (_rented = ArrayPool<byte>.Shared.Rent(max));
        Bytes = buffer[..Encoding.UTF8.GetBytes(value, buffer)];
    }

    public ReadOnlySpan<byte> Bytes { get; }

    public void Dispose()
    {
        if (_rented is not null)
        {
            ArrayPool<byte>.Shared.Return(_rented);
            _rented = null;
        }
    }
}
