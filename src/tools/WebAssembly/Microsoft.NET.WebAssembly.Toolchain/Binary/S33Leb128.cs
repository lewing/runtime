// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>Shared decoder rules for the signed 33-bit LEB128 used by heap types.</summary>
internal static class S33Leb128
{
    internal const int MaxBytes = 5;
    internal const long MinValue = -1L << 32;
    internal const long MaxValue = (1L << 32) - 1;

    internal static long Read(ReadOnlySpan<byte> data, ref int offset)
    {
        long result = 0;
        int shift = 0;

        for (int i = 0; i < MaxBytes; i++)
        {
            if ((uint)offset >= (uint)data.Length)
                throw new WasmFormatException("Unexpected end of s33 LEB128 encoding");

            byte current = data[offset++];
            ValidateTerminalByte(current, i);
            result |= (long)(current & 0x7F) << shift;
            shift += 7;

            if ((current & 0x80) == 0)
            {
                if ((current & 0x40) != 0)
                    result |= -1L << shift;
                return result;
            }
        }

        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for s33");
    }

    internal static void ValidateTerminalByte(byte value, int byteIndex)
    {
        if (byteIndex != MaxBytes - 1)
            return;

        // The fifth byte contributes bits 28..32. Bits 33 and 34 must be copies of bit 32.
        int signAndUnused = value & 0x70;
        if (signAndUnused is not (0x00 or 0x70))
            throw new WasmFormatException("Signed LEB128 value overflows s33");
    }

    internal static bool IsInRange(long value) => value is >= MinValue and <= MaxValue;
}
