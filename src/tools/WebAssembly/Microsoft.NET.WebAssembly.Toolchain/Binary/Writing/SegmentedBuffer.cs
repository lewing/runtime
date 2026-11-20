// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// An append-only, write-only byte buffer made of chunks that are never copied or reallocated, so encoded content
/// is written once into the buffer and once to its destination. Chunks grow from 256 bytes to 64 KiB, which keeps
/// them off the large object heap and bounds the unused tail to one chunk.
/// </summary>
/// <remarks>
/// It is a <see cref="Stream"/> so that an arbitrary <see cref="IWasmSectionPayload"/> can be copied into it; the
/// encoders use its LEB128 methods, which write straight into the current chunk.
/// </remarks>
internal sealed class SegmentedBuffer : Stream
{
    private const int FirstChunkSize = 256;
    private const int MaxChunkSize = 64 * 1024;

    private readonly List<(byte[] Chunk, int Count)> _sealed = [];
    private byte[] _current = [];
    private int _used;
    private long _sealedLength;

    public override long Length => _sealedLength + _used;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Position
    {
        get => Length;
        set => throw new NotSupportedException();
    }

    public override void WriteByte(byte value)
    {
        if (_used == _current.Length)
            NextChunk(1);
        _current[_used++] = value;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        while (!buffer.IsEmpty)
        {
            if (_used == _current.Length)
                NextChunk(buffer.Length);
            int count = Math.Min(buffer.Length, _current.Length - _used);
            buffer[..count].CopyTo(_current.AsSpan(_used));
            _used += count;
            buffer = buffer[count..];
        }
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    // Not `_used += Write(Reserve(...))`: that reads _used before Reserve may move to a new chunk and reset it.
    public void WriteULeb128(ulong value)
    {
        int length = WasmLeb128.WriteULeb128(Reserve(WasmLeb128.MaxLength), value);
        _used += length;
    }

    public void WriteSLeb128(long value)
    {
        int length = WasmLeb128.WriteSLeb128(Reserve(WasmLeb128.MaxLength), value);
        _used += length;
    }

    /// <summary>Writes the content to <paramref name="destination"/>, one write per chunk.</summary>
    public void CopyContentTo(Stream destination)
    {
        foreach ((byte[] chunk, int count) in _sealed)
            destination.Write(chunk, 0, count);
        destination.Write(_current, 0, _used);
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // A contiguous span of at least `size` bytes at the end of the content; the caller advances _used.
    private Span<byte> Reserve(int size)
    {
        if (_current.Length - _used < size)
            NextChunk(size);
        return _current.AsSpan(_used);
    }

    private void NextChunk(int minimumSize)
    {
        if (_used > 0)
        {
            _sealed.Add((_current, _used));
            _sealedLength += _used;
        }

        int size = _current.Length == 0 ? FirstChunkSize : Math.Min(_current.Length * 2, MaxChunkSize);
        _current = GC.AllocateUninitializedArray<byte>(Math.Max(size, Math.Min(minimumSize, MaxChunkSize)));
        _used = 0;
    }
}
