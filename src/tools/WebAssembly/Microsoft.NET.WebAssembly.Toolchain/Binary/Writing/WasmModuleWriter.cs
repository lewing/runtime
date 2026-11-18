// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Writes a WebAssembly module (or relocatable object) to a stream one section at a time. Nothing is buffered:
/// each section's size is declared up front, its header is written immediately, and its content is copied
/// straight to the output, so the output stream is never sought. Function bodies and every other content are
/// written exactly as given; the writer never re-encodes them, never reorders sections and never adds any
/// (data count, producers, linking and reloc.* sections are all the caller's to write, in the caller's order).
/// </summary>
/// <remarks>
/// Section indices are file positions (0 for the first section after the header), the numbering the linking
/// section and reloc.* sections refer to. Every Begin/Write method returns the index of the section it starts.
/// </remarks>
public sealed class WasmModuleWriter
{
    private const int MaxSizeFieldWidth = 5;
    private const int MinimumPaddingSectionSize = 3;

    private readonly Stream _output;
    private long _sectionEnd = -1;

    /// <summary>Creates a writer that writes to <paramref name="output"/>.</summary>
    /// <param name="output">The destination. It only needs to be writable.</param>
    /// <param name="writeHeader">Whether to write the module header (<see cref="Header"/>) first.</param>
    /// <param name="startOffset">
    /// The module offset of the first byte this writer writes; use it when the caller has already written the
    /// header or other sections itself. Offsets matter for alignment padding.
    /// </param>
    /// <param name="startSectionIndex">
    /// The index of the first section this writer writes: the number of sections the caller has already written
    /// itself, so that the indices this writer returns are the file indices relocations and section symbols use.
    /// </param>
    public WasmModuleWriter(Stream output, bool writeHeader = true, long startOffset = 0, int startSectionIndex = 0)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentOutOfRangeException.ThrowIfNegative(startOffset);
        ArgumentOutOfRangeException.ThrowIfNegative(startSectionIndex);
        _output = output;
        Position = startOffset;
        NextSectionIndex = startSectionIndex;
        ContentStream = new SectionContentStream(this);
        if (writeHeader)
            WriteRaw(Header);
    }

    /// <summary>The module header: the <c>\0asm</c> magic and version 1.</summary>
    public static ReadOnlySpan<byte> Header => [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    /// <summary>The module offset of the next byte to be written.</summary>
    public long Position { get; private set; }

    /// <summary>The index the next section will have.</summary>
    public int NextSectionIndex { get; private set; }

    /// <summary>Whether a section has been begun and not yet ended.</summary>
    public bool IsSectionOpen => _sectionEnd >= 0;

    /// <summary>
    /// A write-only stream over the open section's content, for callers that encode content directly to a
    /// stream. Writing beyond the declared content size throws.
    /// </summary>
    public Stream ContentStream { get; }

    /// <summary>
    /// Writes the header of a section with <paramref name="contentSize"/> bytes of content, which must follow
    /// through <see cref="Write(ReadOnlySpan{byte})"/>, <see cref="Write(Stream)"/> or <see cref="ContentStream"/>
    /// before <see cref="EndSection"/>.
    /// </summary>
    /// <param name="id">The section ID.</param>
    /// <param name="contentSize">The number of content bytes that will follow the header.</param>
    /// <param name="sizeFieldWidth">
    /// 0 for a minimal size field, or the width (1–5) of a padded one, as relocatable objects and
    /// alignment-sensitive layouts use.
    /// </param>
    /// <returns>The section's index.</returns>
    public int BeginSection(WasmSectionId id, long contentSize, int sizeFieldWidth = 0)
    {
        EnsureNoOpenSection();
        Span<byte> header = stackalloc byte[1 + MaxSizeFieldWidth];
        int headerLength = EncodeSectionHeader(header, id, contentSize, sizeFieldWidth);
        WriteRaw(header[..headerLength]);
        _sectionEnd = Position + contentSize;
        return NextSectionIndex++;
    }

    /// <summary>
    /// Writes the header and name of a custom section whose payload (the bytes after the name) is
    /// <paramref name="payloadSize"/> bytes long; the payload must follow before <see cref="EndSection"/>.
    /// </summary>
    /// <returns>The section's index.</returns>
    public int BeginCustomSection(ReadOnlySpan<byte> utf8Name, long payloadSize, int sizeFieldWidth = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(payloadSize);
        long contentSize = WasmLeb128.GetULeb128Size((ulong)utf8Name.Length) + utf8Name.Length + payloadSize;
        int index = BeginSection(WasmSectionId.Custom, contentSize, sizeFieldWidth);
        Span<byte> nameLength = stackalloc byte[WasmLeb128.MaxLength];
        Write(nameLength[..WasmLeb128.WriteULeb128(nameLength, (ulong)utf8Name.Length)]);
        Write(utf8Name);
        return index;
    }

    /// <summary>Writes content of the open section.</summary>
    public void Write(ReadOnlySpan<byte> content)
    {
        if (!IsSectionOpen)
            throw new InvalidOperationException("No section is open; call BeginSection first.");
        if (Position + content.Length > _sectionEnd)
            throw new InvalidOperationException($"Writing {content.Length} bytes would overrun the section's declared size by {Position + content.Length - _sectionEnd} bytes.");
        WriteRaw(content);
    }

    /// <summary>Copies <paramref name="source"/> from its current position to its end into the open section.</summary>
    public void Write(Stream source)
    {
        ArgumentNullException.ThrowIfNull(source);
        // A MemoryStream source writes its buffer directly; other streams copy through a pooled buffer.
        source.CopyTo(ContentStream);
    }

    /// <summary>Ends the open section, checking that exactly its declared content was written.</summary>
    public void EndSection()
    {
        if (!IsSectionOpen)
            throw new InvalidOperationException("No section is open.");
        if (Position != _sectionEnd)
            throw new InvalidOperationException($"The section's declared size is {_sectionEnd - Position} bytes more than its content.");
        _sectionEnd = -1;
    }

    /// <summary>Writes a section whose content is <paramref name="content"/>.</summary>
    /// <returns>The section's index.</returns>
    public int WriteSection(WasmSectionId id, ReadOnlySpan<byte> content, int sizeFieldWidth = 0)
    {
        int index = BeginSection(id, content.Length, sizeFieldWidth);
        Write(content);
        EndSection();
        return index;
    }

    /// <summary>
    /// Writes a section whose content is <paramref name="contentPrefix"/> followed by <paramref name="content"/>
    /// from its current position to its end. <paramref name="content"/> must be seekable so its length is known.
    /// </summary>
    /// <returns>The section's index.</returns>
    public int WriteSection(WasmSectionId id, Stream content, ReadOnlySpan<byte> contentPrefix = default, int sizeFieldWidth = 0)
    {
        long length = RemainingLength(content);
        int index = BeginSection(id, contentPrefix.Length + length, sizeFieldWidth);
        Write(contentPrefix);
        Write(content);
        EndSection();
        return index;
    }

    /// <summary>Writes a section whose content is supplied by <paramref name="payload"/>.</summary>
    /// <returns>The section's index.</returns>
    public int WriteSection(WasmSectionId id, IWasmSectionPayload payload, int sizeFieldWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        int index = BeginSection(id, payload.Length, sizeFieldWidth);
        payload.WriteTo(ContentStream);
        EndSection();
        return index;
    }

    /// <summary>
    /// Writes a vector section: the entry count <paramref name="count"/> followed by the already encoded
    /// entries in <paramref name="entries"/> (for the code section, the function bodies exactly as given).
    /// </summary>
    /// <returns>The section's index.</returns>
    public int WriteVectorSection(WasmSectionId id, uint count, Stream entries, int sizeFieldWidth = 0)
    {
        Span<byte> prefix = stackalloc byte[WasmLeb128.MaxLength];
        return WriteSection(id, entries, prefix[..WasmLeb128.WriteULeb128(prefix, count)], sizeFieldWidth);
    }

    /// <summary>Writes a custom section named <paramref name="utf8Name"/> with payload <paramref name="payload"/>.</summary>
    /// <returns>The section's index.</returns>
    public int WriteCustomSection(ReadOnlySpan<byte> utf8Name, ReadOnlySpan<byte> payload, int sizeFieldWidth = 0)
    {
        int index = BeginCustomSection(utf8Name, payload.Length, sizeFieldWidth);
        Write(payload);
        EndSection();
        return index;
    }

    /// <summary>
    /// Writes a custom section whose payload is <paramref name="payloadPrefix"/> followed by
    /// <paramref name="payload"/> from its current position to its end.
    /// </summary>
    /// <returns>The section's index.</returns>
    public int WriteCustomSection(ReadOnlySpan<byte> utf8Name, Stream payload, ReadOnlySpan<byte> payloadPrefix = default, int sizeFieldWidth = 0)
    {
        long length = RemainingLength(payload);
        int index = BeginCustomSection(utf8Name, payloadPrefix.Length + length, sizeFieldWidth);
        Write(payloadPrefix);
        Write(payload);
        EndSection();
        return index;
    }

    /// <summary>Writes a custom section whose payload is supplied by <paramref name="payload"/>.</summary>
    /// <returns>The section's index.</returns>
    public int WriteCustomSection(ReadOnlySpan<byte> utf8Name, IWasmSectionPayload payload, int sizeFieldWidth = 0)
    {
        ArgumentNullException.ThrowIfNull(payload);
        int index = BeginCustomSection(utf8Name, payload.Length, sizeFieldWidth);
        payload.WriteTo(ContentStream);
        EndSection();
        return index;
    }

    /// <summary>
    /// Pads the module so that the next section starts at a multiple of <paramref name="followingSectionAlignment"/>,
    /// by writing an unnamed custom section of zeros (at least 3 bytes, adding one alignment unit when less is
    /// needed). This is the layout dotnet/runtime's Webcil writer uses to align its payload segment in the file.
    /// </summary>
    /// <returns>The padding section's index, or -1 when no padding is needed.</returns>
    public int WritePaddingSection(int followingSectionAlignment)
    {
        EnsureNoOpenSection();
        int size = GetPaddingSectionSize(followingSectionAlignment, Position);
        if (size == 0)
            return -1;

        int sizeFieldWidth = 1;
        int payloadLength = 0;
        for (; sizeFieldWidth <= MaxSizeFieldWidth; sizeFieldWidth++)
        {
            payloadLength = size - 1 - sizeFieldWidth;
            if (WasmLeb128.GetULeb128Size((ulong)payloadLength) <= sizeFieldWidth)
                break;
        }

        // The payload's first zero byte reads as an empty name, so the section is a valid unnamed custom section.
        int index = BeginSection(WasmSectionId.Custom, payloadLength, sizeFieldWidth);
        Span<byte> zeros = stackalloc byte[256];
        zeros.Clear();
        for (int remaining = payloadLength; remaining > 0; remaining -= zeros.Length)
            Write(zeros[..Math.Min(remaining, zeros.Length)]);
        EndSection();
        return index;
    }

    /// <summary>
    /// The size of the padding section <see cref="WritePaddingSection"/> writes at module offset
    /// <paramref name="offset"/>: 0 when the offset is already aligned.
    /// </summary>
    public static int GetPaddingSectionSize(int followingSectionAlignment, long offset)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(followingSectionAlignment);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (followingSectionAlignment <= 1 || offset % followingSectionAlignment == 0)
            return 0;

        int padding = (int)(followingSectionAlignment - offset % followingSectionAlignment);
        return padding < MinimumPaddingSectionSize ? padding + followingSectionAlignment : padding;
    }

    /// <summary>
    /// Encodes a section header (ID and size field) into <paramref name="destination"/> and returns its length.
    /// </summary>
    /// <param name="destination">At least 6 bytes.</param>
    /// <param name="id">The section ID.</param>
    /// <param name="contentSize">The section's content size.</param>
    /// <param name="sizeFieldWidth">0 for a minimal size field, or the width (1–5) of a padded one.</param>
    public static int EncodeSectionHeader(Span<byte> destination, WasmSectionId id, long contentSize, int sizeFieldWidth = 0)
    {
        if (contentSize is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(contentSize), contentSize, "A section's size must fit in 32 bits.");
        if (sizeFieldWidth is < 0 or > MaxSizeFieldWidth)
            throw new ArgumentOutOfRangeException(nameof(sizeFieldWidth), sizeFieldWidth, "A section size field is 1 to 5 bytes wide (0 for minimal).");
        if (sizeFieldWidth != 0 && WasmLeb128.GetULeb128Size((ulong)contentSize) > sizeFieldWidth)
            throw new ArgumentException($"A section of {contentSize} bytes does not fit a {sizeFieldWidth}-byte size field.", nameof(sizeFieldWidth));

        destination[0] = (byte)id;
        if (sizeFieldWidth == 0)
            return 1 + WasmLeb128.WriteULeb128(destination[1..], (ulong)contentSize);

        WasmLeb128.WritePaddedULeb128(destination.Slice(1, sizeFieldWidth), (ulong)contentSize);
        return 1 + sizeFieldWidth;
    }

    private void EnsureNoOpenSection()
    {
        if (IsSectionOpen)
            throw new InvalidOperationException("The previous section is still open; call EndSection first.");
    }

    private void WriteRaw(ReadOnlySpan<byte> bytes)
    {
        _output.Write(bytes);
        Position += bytes.Length;
    }

    private static long RemainingLength(Stream stream)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (!stream.CanSeek)
            throw new ArgumentException("The content stream must be seekable so that its length is known; use BeginSection with a declared size for other streams.", nameof(stream));
        return stream.Length - stream.Position;
    }

    private sealed class SectionContentStream(WasmModuleWriter writer) : Stream
    {
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => writer.Write(buffer.AsSpan(offset, count));
        public override void Write(ReadOnlySpan<byte> buffer) => writer.Write(buffer);
        public override void WriteByte(byte value) => writer.Write([value]);
    }
}
