// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Linking;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes the payload of a <c>linking</c> custom section (LLVM's tool-conventions Linking.md): the version
/// followed by the subsections in the order they are written. Write it with
/// <see cref="WasmModuleWriter.WriteCustomSection(ReadOnlySpan{byte}, IWasmSectionPayload, int)"/> under the
/// name <c>linking</c>, after the sections its symbols and segments refer to.
/// </summary>
/// <remarks>
/// The subsection encoders of this namespace (<see cref="SymbolTableEncoder"/>, <see cref="SegmentInfoEncoder"/>,
/// <see cref="InitFunctionsEncoder"/> and <see cref="ComdatInfoEncoder"/>) are referenced, not copied: each payload
/// is written once, straight to the destination of <see cref="WriteTo"/>. Writing one into a linking section
/// completes it, and adding to it afterwards throws <see cref="InvalidOperationException"/>. Any other
/// <see cref="IWasmSectionPayload"/> is copied when it is written in.
/// </remarks>
public sealed class LinkingSectionEncoder : IWasmSectionPayload
{
    /// <summary>The linking metadata version this encoder writes.</summary>
    public const uint Version = LinkingSection.CurrentVersion;

    private readonly List<Subsection> _subsections = [];
    private long _length = WasmLeb128.GetULeb128Size(Version);

    /// <inheritdoc />
    public long Length => _length;

    /// <inheritdoc />
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Span<byte> header = stackalloc byte[1 + WasmLeb128.MaxLength32];
        destination.Write(header[..WasmLeb128.WriteULeb128(header, Version)]);
        foreach (Subsection subsection in _subsections)
        {
            destination.Write(header[..EncodeHeader(header, subsection.Id, subsection.Length, subsection.SizeFieldWidth)]);
            subsection.Payload.WriteTo(destination);
        }
    }

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
    /// <param name="payload">
    /// The subsection payload. One of this namespace's subsection encoders is referenced and completed; any other
    /// payload is copied now.
    /// </param>
    /// <param name="sizeFieldWidth">
    /// 0 for a minimal size field, or the width (1–5) of a padded one; LLVM's object writer pads every
    /// subsection size to 5 bytes.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The payload does not fit the size field, or wrote a different number of bytes than its
    /// <see cref="IWasmSectionPayload.Length"/>. Nothing is appended.
    /// </exception>
    public void WriteSubsection(LinkingSubsectionId id, IWasmSectionPayload payload, int sizeFieldWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        // Checked before the payload is completed or copied, so a rejected subsection leaves both unchanged.
        Span<byte> header = stackalloc byte[1 + WasmLeb128.MaxLength32];
        EncodeHeader(header, id, payload.Length, sizeFieldWidth);
        if (payload is LinkingSubsectionEncoder encoder)
        {
            Add(id, encoder.Complete(), sizeFieldWidth);
            return;
        }

        var copy = new SegmentedBuffer();
        payload.WriteTo(copy);
        if (copy.Length != payload.Length)
            throw new ArgumentException("The payload wrote a different number of bytes than its Length.", nameof(payload));
        Add(id, new BufferedPayload(copy), sizeFieldWidth);
    }

    /// <summary>Appends a subsection with the given ID and payload, which is copied.</summary>
    /// <param name="id">The subsection ID.</param>
    /// <param name="payload">The subsection payload.</param>
    /// <param name="sizeFieldWidth">0 for a minimal size field, or the width (1–5) of a padded one.</param>
    public void WriteSubsection(LinkingSubsectionId id, ReadOnlySpan<byte> payload, int sizeFieldWidth = 0)
    {
        // Checked before copying, so a payload that cannot be framed is not buffered for nothing.
        Span<byte> header = stackalloc byte[1 + WasmLeb128.MaxLength32];
        EncodeHeader(header, id, payload.Length, sizeFieldWidth);
        var copy = new SegmentedBuffer();
        copy.Write(payload);
        Add(id, new BufferedPayload(copy), sizeFieldWidth);
    }

    private void Add(LinkingSubsectionId id, IWasmSectionPayload payload, int sizeFieldWidth)
    {
        long length = payload.Length;
        Span<byte> header = stackalloc byte[1 + WasmLeb128.MaxLength32];
        int headerLength = EncodeHeader(header, id, length, sizeFieldWidth);
        _subsections.Add(new Subsection(id, payload, length, sizeFieldWidth));
        _length += headerLength + length;
    }

    // A subsection header has the same shape as a section header: an ID byte and a size field.
    private static int EncodeHeader(Span<byte> destination, LinkingSubsectionId id, long payloadLength, int sizeFieldWidth) =>
        WasmModuleWriter.EncodeSectionHeader(destination, (WasmSectionId)id, payloadLength, sizeFieldWidth);

    private readonly record struct Subsection(LinkingSubsectionId Id, IWasmSectionPayload Payload, long Length, int SizeFieldWidth);

    private sealed class BufferedPayload(SegmentedBuffer buffer) : IWasmSectionPayload
    {
        public long Length => buffer.Length;

        public void WriteTo(Stream destination) => buffer.CopyContentTo(destination);
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
                if (group.Flags != (uint)ComdatFlags.None)
                    throw new ArgumentOutOfRangeException(nameof(model), group.Flags, $"COMDAT group '{group.Name}' has flags; LLVM defines none and rejects any set bit.");
                using var name = new Utf8Scratch(group.Name, scratch);
                comdats.Add(name.Bytes, ComdatFlags.None, CollectionsMarshal.AsSpan(group.Entries));
            }

            encoder.WriteComdatInfo(comdats, subsectionSizeFieldWidth);
        }

        return encoder;
    }
}

/// <summary>
/// A linking subsection encoder: a count-prefixed vector of encoded entries, completed (made read-only) when it is
/// written into a <see cref="LinkingSectionEncoder"/>, which then refers to it instead of copying it.
/// </summary>
public abstract class LinkingSubsectionEncoder : IWasmSectionPayload
{
    private bool _completed;

    private protected LinkingSubsectionEncoder()
    {
    }

    /// <summary>The number of entries added.</summary>
    public int Count { get; private set; }

    /// <summary>
    /// Whether the encoder has been written into a <see cref="LinkingSectionEncoder"/>; adding to it then throws
    /// <see cref="InvalidOperationException"/>.
    /// </summary>
    public bool IsCompleted => _completed;

    /// <inheritdoc />
    public long Length => WasmLeb128.GetULeb128Size((ulong)Count) + Entries.Length;

    private protected SegmentedBuffer Entries { get; } = new();

    /// <inheritdoc />
    public void WriteTo(Stream destination)
    {
        ArgumentNullException.ThrowIfNull(destination);
        Span<byte> count = stackalloc byte[WasmLeb128.MaxLength32];
        destination.Write(count[..WasmLeb128.WriteULeb128(count, (ulong)Count)]);
        Entries.CopyContentTo(destination);
    }

    internal LinkingSubsectionEncoder Complete()
    {
        _completed = true;
        return this;
    }

    // Called before validating an entry, so a completed encoder rejects every addition the same way.
    private protected void EnsureNotCompleted()
    {
        if (_completed)
            throw new InvalidOperationException($"This {GetType().Name} has been written into a linking section, which refers to it; it can no longer change.");
    }

    private protected int CompleteEntry() => Count++;

    private protected void WriteName(ReadOnlySpan<byte> utf8Name)
    {
        Entries.WriteULeb128((ulong)utf8Name.Length);
        Entries.Write(utf8Name);
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
public sealed class SymbolTableEncoder : LinkingSubsectionEncoder
{
    /// <summary>Adds a function symbol for function <paramref name="index"/>.</summary>
    public int AddFunction(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Function, flags, index, utf8Name);

    /// <summary>Adds a global symbol for global <paramref name="index"/>.</summary>
    public int AddGlobal(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Global, flags, index, utf8Name);

    /// <summary>Adds a table symbol for table <paramref name="index"/>.</summary>
    public int AddTable(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Table, flags, index, utf8Name);

    /// <summary>Adds a tag symbol for tag <paramref name="index"/>.</summary>
    public int AddTag(SymbolFlags flags, uint index, ReadOnlySpan<byte> utf8Name = default) => AddIndexed(SymbolType.Tag, flags, index, utf8Name);

    /// <summary>
    /// Adds a defined data symbol: <paramref name="size"/> bytes at <paramref name="offset"/>, which is relative to
    /// the start of data segment <paramref name="segmentIndex"/>'s content.
    /// </summary>
    public int AddData(SymbolFlags flags, ReadOnlySpan<byte> utf8Name, uint segmentIndex, ulong offset, ulong size)
    {
        EnsureNotCompleted();
        if (IsUndefined(flags))
            throw new ArgumentException("A defined data symbol cannot be flagged Undefined; use AddUndefinedData.", nameof(flags));
        RequireName(utf8Name);

        Entries.WriteByte((byte)SymbolType.Data);
        Entries.WriteULeb128((uint)flags);
        WriteName(utf8Name);
        Entries.WriteULeb128(segmentIndex);
        Entries.WriteULeb128(offset);
        Entries.WriteULeb128(size);
        return CompleteEntry();
    }

    /// <summary>Adds an undefined data symbol; <paramref name="flags"/> must include <see cref="SymbolFlags.Undefined"/>.</summary>
    public int AddUndefinedData(SymbolFlags flags, ReadOnlySpan<byte> utf8Name)
    {
        EnsureNotCompleted();
        if (!IsUndefined(flags))
            throw new ArgumentException("An undefined data symbol must be flagged Undefined.", nameof(flags));
        RequireName(utf8Name);

        Entries.WriteByte((byte)SymbolType.Data);
        Entries.WriteULeb128((uint)flags);
        WriteName(utf8Name);
        return CompleteEntry();
    }

    /// <summary>Adds a section symbol for the section at file index <paramref name="sectionIndex"/>.</summary>
    public int AddSection(SymbolFlags flags, uint sectionIndex)
    {
        EnsureNotCompleted();
        Entries.WriteByte((byte)SymbolType.Section);
        Entries.WriteULeb128((uint)flags);
        Entries.WriteULeb128(sectionIndex);
        return CompleteEntry();
    }

    /// <summary>Adds the symbol <paramref name="symbol"/> as <see cref="ModuleReader"/> models it.</summary>
    public int Add(WasmSymbol symbol)
    {
        EnsureNotCompleted();
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
                int index = Required(symbol.Index, "Index");
                if (index < 0)
                    throw new ArgumentException($"The symbol's Index {index} is negative.", nameof(symbol));
                return AddIndexed(symbol.Type, symbol.Flags, (uint)index, name);
            case SymbolType.Data when IsUndefined(symbol.Flags):
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
        EnsureNotCompleted();
        bool hasName = HasName(flags);
        if (!hasName && !utf8Name.IsEmpty)
            throw new ArgumentException("An undefined symbol without ExplicitName has no name field; pass no name or set ExplicitName.", nameof(utf8Name));
        if (hasName)
            RequireName(utf8Name);

        Entries.WriteByte((byte)kind);
        Entries.WriteULeb128((uint)flags);
        Entries.WriteULeb128(index);
        if (hasName)
            WriteName(utf8Name);
        return CompleteEntry();
    }

    // Bitwise tests, not Enum.HasFlag, which boxes both operands in unoptimized code and so allocates per symbol.
    private static bool HasName(SymbolFlags flags) =>
        !IsUndefined(flags) || (flags & SymbolFlags.ExplicitName) != 0;

    private static bool IsUndefined(SymbolFlags flags) => (flags & SymbolFlags.Undefined) != 0;

    private static void RequireName(ReadOnlySpan<byte> utf8Name)
    {
        if (utf8Name.IsEmpty)
            throw new ArgumentException("This symbol's name is written to the symbol table and must not be empty.", nameof(utf8Name));
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.SegmentInfo"/> subsection, one entry per data segment in order.</summary>
public sealed class SegmentInfoEncoder : LinkingSubsectionEncoder
{
    /// <summary>Adds the next segment's name, alignment (as a power of two) and flags.</summary>
    public int Add(ReadOnlySpan<byte> utf8Name, uint alignmentLog2, SegmentFlags flags)
    {
        EnsureNotCompleted();
        WriteName(utf8Name);
        Entries.WriteULeb128(alignmentLog2);
        Entries.WriteULeb128((uint)flags);
        return CompleteEntry();
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.InitFunctions"/> subsection (constructors).</summary>
public sealed class InitFunctionsEncoder : LinkingSubsectionEncoder
{
    /// <summary>Adds the function symbol <paramref name="symbolIndex"/> as a constructor of the given priority.</summary>
    public int Add(uint priority, uint symbolIndex)
    {
        EnsureNotCompleted();
        Entries.WriteULeb128(priority);
        Entries.WriteULeb128(symbolIndex);
        return CompleteEntry();
    }
}

/// <summary>Encodes a <see cref="LinkingSubsectionId.ComdatInfo"/> subsection.</summary>
public sealed class ComdatInfoEncoder : LinkingSubsectionEncoder
{
    /// <summary>Adds a COMDAT group and its members (kind and index of each function, data segment or section).</summary>
    /// <param name="utf8Name">The group's name.</param>
    /// <param name="flags">The group's flags; LLVM defines none, so anything but <see cref="ComdatFlags.None"/> throws.</param>
    /// <param name="entries">The group's members; a kind <see cref="ComdatKind"/> does not define throws.</param>
    /// <returns>The group's index.</returns>
    public int Add(ReadOnlySpan<byte> utf8Name, ComdatFlags flags, ReadOnlySpan<ComdatEntry> entries)
    {
        EnsureNotCompleted();
        if (flags != ComdatFlags.None)
            throw new ArgumentOutOfRangeException(nameof(flags), flags, "LLVM defines no COMDAT flags and rejects any set bit.");
        foreach (ComdatEntry entry in entries)
        {
            if (entry.Kind > ComdatKind.Section)
                throw new ArgumentOutOfRangeException(nameof(entries), entry.Kind, "Unknown COMDAT entry kind; LLVM's reader rejects it.");
        }

        WriteName(utf8Name);
        Entries.WriteULeb128((uint)flags);
        Entries.WriteULeb128((ulong)entries.Length);
        foreach (ComdatEntry entry in entries)
            WriteEntry(entry.Kind, entry.Index);
        return CompleteEntry();
    }

    private void WriteEntry(ComdatKind kind, uint index)
    {
        Entries.WriteByte((byte)kind);
        Entries.WriteULeb128(index);
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
