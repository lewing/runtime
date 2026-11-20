// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes data segment headers (core spec §5.5.12): the flags, the memory index and offset expression of an active
/// segment, and the content size, which may be padded so the header's length does not depend on the content's
/// (file-alignment layouts such as Webcil's need that). The content follows the header unchanged.
/// </summary>
public static class DataSegmentEncoder
{
    /// <summary>
    /// The longest offset expression the helpers here encode: an opcode, a 10-byte immediate (an <c>i64.const</c> for a
    /// 64-bit memory) and <c>end</c>.
    /// </summary>
    public const int MaxOffsetExpressionLength = 1 + WasmLeb128.MaxLength + 1;

    /// <summary>
    /// The longest header for an offset expression of at most <see cref="MaxOffsetExpressionLength"/> bytes: flags,
    /// memory index, expression and a 5-byte size.
    /// </summary>
    public const int MaxHeaderLength = 1 + WasmLeb128.MaxLength32 + MaxOffsetExpressionLength + WasmLeb128.MaxLength32;

    /// <summary>
    /// Encodes the offset expression <c>i32.const <paramref name="value"/> end</c> and returns its length.
    /// </summary>
    /// <param name="destination">At least <see cref="MaxOffsetExpressionLength"/> bytes.</param>
    /// <param name="value">The offset.</param>
    /// <param name="width">0 for a minimal immediate, or the width (1–5) it is padded to (5 for a relocated one).</param>
    public static int EncodeI32ConstExpression(Span<byte> destination, int value, int width = 0) =>
        EncodeExpression(destination, WasmOpcode.I32Const, value, width, WasmLeb128.MaxLength32, signed: true);

    /// <summary>
    /// Encodes the offset expression <c>i64.const <paramref name="value"/> end</c> of a segment in a 64-bit memory and
    /// returns its length.
    /// </summary>
    /// <param name="destination">At least <see cref="MaxOffsetExpressionLength"/> bytes.</param>
    /// <param name="value">The offset.</param>
    /// <param name="width">0 for a minimal immediate, or the width (1–10) it is padded to (10 for a relocated one).</param>
    public static int EncodeI64ConstExpression(Span<byte> destination, long value, int width = 0) =>
        EncodeExpression(destination, WasmOpcode.I64Const, value, width, WasmLeb128.MaxLength64, signed: true);

    /// <summary>Encodes the offset expression <c>global.get <paramref name="globalIndex"/> end</c> and returns its length.</summary>
    /// <param name="destination">At least <see cref="MaxOffsetExpressionLength"/> bytes.</param>
    /// <param name="globalIndex">The global holding the offset.</param>
    /// <param name="width">0 for a minimal immediate, or the width (1–5) it is padded to.</param>
    public static int EncodeGlobalGetExpression(Span<byte> destination, uint globalIndex, int width = 0) =>
        EncodeExpression(destination, WasmOpcode.GlobalGet, globalIndex, width, WasmLeb128.MaxLength32, signed: false);

    /// <summary>The length of a passive segment's header.</summary>
    public static int GetPassiveHeaderSize(long contentSize, int sizeFieldWidth = 0) =>
        1 + SizeFieldLength(contentSize, sizeFieldWidth);

    /// <summary>The length of an active segment's header.</summary>
    public static int GetActiveHeaderSize(ReadOnlySpan<byte> offsetExpression, long contentSize, int sizeFieldWidth = 0, uint memoryIndex = 0)
    {
        CheckExpression(offsetExpression, "An offset expression", nameof(offsetExpression));
        return 1 + (memoryIndex == 0 ? 0 : WasmLeb128.GetULeb128Size(memoryIndex)) + offsetExpression.Length +
            SizeFieldLength(contentSize, sizeFieldWidth);
    }

    /// <summary>Encodes a passive segment's header (flags 1 and the content size) and returns its length.</summary>
    /// <param name="destination">At least <see cref="GetPassiveHeaderSize"/> bytes.</param>
    /// <param name="contentSize">The number of content bytes that follow the header.</param>
    /// <param name="sizeFieldWidth">0 for a minimal size, or the width (1–5) it is padded to.</param>
    public static int EncodePassiveHeader(Span<byte> destination, long contentSize, int sizeFieldWidth = 0)
    {
        int sizeLength = SizeFieldLength(contentSize, sizeFieldWidth);
        destination[0] = 0x01;
        WriteSize(destination.Slice(1, sizeLength), contentSize, sizeFieldWidth);
        return 1 + sizeLength;
    }

    /// <summary>
    /// Encodes an active segment's header and returns its length: flags 0 (memory 0) or 2 with the memory index,
    /// the offset expression exactly as given, and the content size.
    /// </summary>
    /// <param name="destination">At least <see cref="GetActiveHeaderSize"/> bytes.</param>
    /// <param name="offsetExpression">A constant expression including its <c>end</c>.</param>
    /// <param name="contentSize">The number of content bytes that follow the header.</param>
    /// <param name="sizeFieldWidth">0 for a minimal size, or the width (1–5) it is padded to.</param>
    /// <param name="memoryIndex">The memory the segment initializes.</param>
    public static int EncodeActiveHeader(Span<byte> destination, ReadOnlySpan<byte> offsetExpression, long contentSize, int sizeFieldWidth = 0, uint memoryIndex = 0)
    {
        CheckExpression(offsetExpression, "An offset expression", nameof(offsetExpression));
        int sizeLength = SizeFieldLength(contentSize, sizeFieldWidth);
        int length;
        if (memoryIndex == 0)
        {
            destination[0] = 0x00;
            length = 1;
        }
        else
        {
            destination[0] = 0x02;
            length = 1 + WasmLeb128.WriteULeb128(destination[1..], memoryIndex);
        }

        offsetExpression.CopyTo(destination[length..]);
        length += offsetExpression.Length;
        WriteSize(destination.Slice(length, sizeLength), contentSize, sizeFieldWidth);
        return length + sizeLength;
    }

    // what is the expression's kind as a sentence start ("An offset expression"); paramName is the caller's argument.
    internal static void CheckExpression(ReadOnlySpan<byte> expression, string what, string paramName)
    {
        if (expression.IsEmpty || expression[^1] != WasmOpcode.End)
            throw new ArgumentException($"{what} must end with 'end' (0x0B).", paramName);
    }

    private static int SizeFieldLength(long contentSize, int sizeFieldWidth)
    {
        if (contentSize is < 0 or > uint.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(contentSize), contentSize, "A data segment's size must fit in 32 bits.");
        if (sizeFieldWidth is < 0 or > WasmLeb128.MaxLength32)
            throw new ArgumentOutOfRangeException(nameof(sizeFieldWidth), sizeFieldWidth, "A size field is 1 to 5 bytes wide (0 for minimal).");
        int minimal = WasmLeb128.GetULeb128Size((ulong)contentSize);
        if (sizeFieldWidth != 0 && minimal > sizeFieldWidth)
            throw new ArgumentException($"A data segment of {contentSize} bytes does not fit a {sizeFieldWidth}-byte size field.", nameof(sizeFieldWidth));
        return sizeFieldWidth == 0 ? minimal : sizeFieldWidth;
    }

    private static void WriteSize(Span<byte> destination, long contentSize, int sizeFieldWidth)
    {
        if (sizeFieldWidth == 0)
            WasmLeb128.WriteULeb128(destination, (ulong)contentSize);
        else
            WasmLeb128.WritePaddedULeb128(destination, (ulong)contentSize);
    }

    // The immediate's width is limited by its type: 5 bytes for an i32.const or a global index, 10 for an i64.const.
    private static int EncodeExpression(Span<byte> destination, byte opcode, long value, int width, int maxWidth, bool signed)
    {
        if (width < 0 || width > maxWidth)
            throw new ArgumentOutOfRangeException(nameof(width), width, $"This immediate is 1 to {maxWidth} bytes wide (0 for minimal).");
        destination[0] = opcode;
        Span<byte> immediate = destination[1..];
        int length;
        if (width == 0)
        {
            length = signed ? WasmLeb128.WriteSLeb128(immediate, value) : WasmLeb128.WriteULeb128(immediate, (ulong)value);
        }
        else
        {
            if (signed)
                WasmLeb128.WritePaddedSLeb128(immediate[..width], value);
            else
                WasmLeb128.WritePaddedULeb128(immediate[..width], (ulong)value);
            length = width;
        }

        destination[1 + length] = WasmOpcode.End;
        return length + 2;
    }
}
