// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// A read-only stream that limits reads to a fixed number of bytes from the
/// current position of the underlying stream. Does not own or dispose the parent.
/// </summary>
internal sealed class BoundedStream : Stream
{
    private readonly Stream _parent;
    private long _remaining;

    public BoundedStream(Stream parent, long length)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        _parent = parent;
        _remaining = length;
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
        => Read(buffer.AsSpan(offset, Math.Min(count, (int)Math.Min(_remaining, int.MaxValue))));

    public override int Read(Span<byte> buffer)
    {
        if (_remaining <= 0) return 0;
        int toRead = (int)Math.Min(buffer.Length, _remaining);
        int read = _parent.Read(buffer[..toRead]);
        _remaining -= read;
        return read;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>Bytes remaining before the bound is reached.</summary>
    internal long Remaining => _remaining;

    /// <summary>True when all bounded bytes have been consumed (or the parent ended early).</summary>
    internal bool IsExhausted => _remaining <= 0;
}
