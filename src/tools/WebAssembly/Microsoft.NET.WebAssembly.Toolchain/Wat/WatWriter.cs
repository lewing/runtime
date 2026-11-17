// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Text;

/// <summary>
/// A thin wrapper around <see cref="TextWriter"/> whose interpolated-string overloads
/// write each segment directly to the underlying writer — zero intermediate string allocations.
/// </summary>
public sealed class WatWriter : TextWriter
{
    private readonly TextWriter _inner;

    public WatWriter(TextWriter inner) => _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    public WatWriter(Stream stream) : this(new StreamWriter(stream)) { }
    public WatWriter(string path) : this(new StreamWriter(path)) { }

    /// <summary>Creates a <see cref="WatWriter"/> backed by a new <see cref="StringWriter"/>.</summary>
    public static WatWriter CreateStringWriter(out StringWriter stringWriter)
    {
        stringWriter = new StringWriter();
        return new WatWriter(stringWriter);
    }

    // ── TextWriter overrides ────────────────────────────────────────────

    public override Encoding Encoding => _inner.Encoding;
    public override void Write(char value) => _inner.Write(value);
    public override void Write(string? value) => _inner.Write(value);
    public override void Write(char[] buffer, int index, int count) => _inner.Write(buffer, index, count);
    public override void Write(ReadOnlySpan<char> buffer) => _inner.Write(buffer);
    public override void Write(int value) => _inner.Write(value);
    public override void Write(long value) => _inner.Write(value);
    public override void Write(uint value) => _inner.Write(value);
    public override void Write(float value) => _inner.Write(value);
    public override void Write(double value) => _inner.Write(value);
    public override void WriteLine() => _inner.WriteLine();
    public override void WriteLine(string? value) => _inner.WriteLine(value);
    public override void Flush() => _inner.Flush();
    public override IFormatProvider FormatProvider => _inner.FormatProvider;
    [AllowNull]
    public override string NewLine { get => _inner.NewLine; set => _inner.NewLine = value; }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _inner.Dispose();
        base.Dispose(disposing);
    }

    // ── Interpolated-string handler overloads ───────────────────────────────────

    public void Write([InterpolatedStringHandlerArgument("")] WatWriterInterpolatedStringHandler builder) { }

    public void WriteLine([InterpolatedStringHandlerArgument("")] WatWriterInterpolatedStringHandler builder)
    {
        _inner.WriteLine();
    }
}

/// <summary>
/// Compiler-recognised handler that forwards each interpolated-string segment
/// directly to the underlying <see cref="WatWriter"/>, avoiding any intermediate
/// string allocation.
/// </summary>
[InterpolatedStringHandler]
public ref struct WatWriterInterpolatedStringHandler
{
    private readonly WatWriter _writer;

    public WatWriterInterpolatedStringHandler(int literalLength, int formattedCount, WatWriter writer)
    {
        _writer = writer;
    }

    public void AppendLiteral(string s) => _writer.Write(s);

    public void AppendFormatted<T>(T value) => _writer.Write(value?.ToString());
    public void AppendFormatted<T>(T value, string? format) =>
        _writer.Write(value is IFormattable f ? f.ToString(format, null) : value?.ToString());

    // Primitive overloads to avoid boxing
    public void AppendFormatted(int value) => _writer.Write(value);
    public void AppendFormatted(long value) => _writer.Write(value);
    public void AppendFormatted(uint value) => _writer.Write(value);
    public void AppendFormatted(float value) => _writer.Write(value);
    public void AppendFormatted(double value) => _writer.Write(value);
    public void AppendFormatted(string? value) => _writer.Write(value);
    public void AppendFormatted(ReadOnlySpan<char> value) => _writer.Write(value);
}
