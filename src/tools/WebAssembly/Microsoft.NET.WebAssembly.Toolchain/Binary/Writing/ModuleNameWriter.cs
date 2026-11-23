// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Writes the module name subsection (ID 0) of the <c>name</c> custom section: the module's single name. It comes
/// first in the section, before every <see cref="NameMapWriter"/> or <see cref="IndirectNameMapWriter"/> subsection.
/// </summary>
public static class ModuleNameWriter
{
    /// <summary>The encoded size of the whole subsection (ID, size and name), to add up into the section's size.</summary>
    public static long GetSubsectionSize(int utf8NameLength)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(utf8NameLength);
        long payload = WasmLeb128.GetULeb128Size((ulong)utf8NameLength) + utf8NameLength;
        return 1 + WasmLeb128.GetULeb128Size((ulong)payload) + payload;
    }

    /// <summary>Writes the subsection into <paramref name="writer"/>'s open section.</summary>
    public static void Write(WasmModuleWriter writer, ReadOnlySpan<byte> utf8Name) => Write(NameSink.For(writer), utf8Name);

    /// <summary>Writes the subsection to <paramref name="output"/>.</summary>
    public static void Write(IBufferWriter<byte> output, ReadOnlySpan<byte> utf8Name) => Write(NameSink.For(output), utf8Name);

    private static void Write(NameSink output, ReadOnlySpan<byte> utf8Name)
    {
        Span<byte> header = stackalloc byte[1 + 2 * WasmLeb128.MaxLength];
        header[0] = (byte)NameSubsectionId.Module;
        int length = 1 + WasmLeb128.WriteULeb128(header[1..], (ulong)(WasmLeb128.GetULeb128Size((ulong)utf8Name.Length) + utf8Name.Length));
        length += WasmLeb128.WriteULeb128(header[length..], (ulong)utf8Name.Length);
        output.Write(header[..length]);
        output.Write(utf8Name);
    }
}
