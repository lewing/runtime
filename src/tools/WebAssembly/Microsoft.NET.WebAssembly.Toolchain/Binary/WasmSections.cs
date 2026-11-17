// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>A top-level section of a WebAssembly binary.</summary>
/// <param name="Id">Section id (0 for custom sections).</param>
/// <param name="Name">Custom section name, or <see langword="null"/> for known sections.</param>
/// <param name="PayloadOffset">File offset of the section payload (after the id and size).</param>
/// <param name="PayloadSize">Size of the payload in bytes, including a custom section's name.</param>
public readonly record struct WasmSectionInfo(byte Id, string? Name, long PayloadOffset, uint PayloadSize)
{
    public const byte CustomSectionId = 0;
    public const byte DataSectionId = 11;
}

/// <summary>
/// Section-level operations on WebAssembly binaries that don't decode section contents, so they stream
/// arbitrarily large modules and leave every retained byte untouched.
/// </summary>
public static class WasmSections
{
    private static ReadOnlySpan<byte> Header => [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    /// <summary>Lists the sections of a module or relocatable object.</summary>
    /// <exception cref="WasmFormatException">The input isn't a WebAssembly module or is truncated.</exception>
    public static List<WasmSectionInfo> Read(Stream stream)
    {
        var sections = new List<WasmSectionInfo>();
        ReadHeader(stream);
        while (TryReadSection(stream, out WasmSectionInfo section))
        {
            sections.Add(section);
            Skip(stream, section.PayloadOffset + section.PayloadSize);
        }
        return sections;
    }

    /// <summary>Lists the sections of the module at <paramref name="path"/>.</summary>
    public static List<WasmSectionInfo> Read(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Read(stream);
    }

    /// <summary>
    /// Copies <paramref name="input"/> to <paramref name="output"/>, omitting sections for which
    /// <paramref name="remove"/> returns <see langword="true"/>. Retained sections are copied byte for byte.
    /// </summary>
    /// <returns>The number of sections removed.</returns>
    public static int CopyWithout(Stream input, Stream output, Func<WasmSectionInfo, bool> remove)
    {
        ReadHeader(input);
        output.Write(Header);

        int removed = 0;
        long sectionStart = input.Position;
        while (TryReadSection(input, out WasmSectionInfo section))
        {
            long sectionEnd = section.PayloadOffset + section.PayloadSize;
            if (remove(section))
            {
                removed++;
                Skip(input, sectionEnd);
            }
            else
            {
                input.Position = sectionStart;
                CopyBytes(input, output, sectionEnd - sectionStart);
            }
            sectionStart = sectionEnd;
        }
        return removed;
    }

    /// <summary>
    /// Removes the <c>name</c> section and/or DWARF (<c>.debug_*</c>) custom sections, matching what
    /// <c>wasm-opt --strip-dwarf</c> without <c>-g</c> removes. Other custom sections (for example
    /// <c>producers</c>, <c>build_id</c>, <c>target_features</c>) are kept. Unlike wasm-opt, code is not re-encoded.
    /// </summary>
    /// <remarks><paramref name="outputPath"/> may equal <paramref name="inputPath"/>.</remarks>
    /// <returns>The number of sections removed.</returns>
    public static int StripDebugInfo(string inputPath, string outputPath, bool stripDwarf = true, bool stripNames = true)
    {
        string tempPath = outputPath + ".tmp";
        int removed;
        using (FileStream input = File.OpenRead(inputPath))
        using (FileStream output = File.Create(tempPath))
        {
            removed = CopyWithout(input, output, s => s.Id == WasmSectionInfo.CustomSectionId &&
                ((stripDwarf && s.Name!.StartsWith(".debug_", StringComparison.Ordinal)) || (stripNames && s.Name == "name")));
        }
        File.Move(tempPath, outputPath, overwrite: true);
        return removed;
    }

    private static void ReadHeader(Stream stream)
    {
        Span<byte> header = stackalloc byte[8];
        if (stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false) != header.Length || !header.SequenceEqual(Header))
            throw new WasmFormatException("Not a WebAssembly binary (bad magic or version).");
    }

    private static bool TryReadSection(Stream stream, out WasmSectionInfo section)
    {
        int id = stream.ReadByte();
        if (id < 0)
        {
            section = default;
            return false;
        }

        uint size = ReadU32(stream);
        long payloadOffset = stream.Position;
        string? name = null;
        if (id == WasmSectionInfo.CustomSectionId)
        {
            uint nameLength = ReadU32(stream);
            if (nameLength > size)
                throw new WasmFormatException($"Custom section name at offset {payloadOffset} overruns its section.");
            byte[] nameBytes = new byte[nameLength];
            stream.ReadExactly(nameBytes);
            name = Encoding.UTF8.GetString(nameBytes);
        }

        if (payloadOffset + size > stream.Length)
            throw new WasmFormatException($"Section {id} at offset {payloadOffset} overruns the file.");

        section = new WasmSectionInfo((byte)id, name, payloadOffset, size);
        return true;
    }

    private static uint ReadU32(Stream stream)
    {
        uint result = 0;
        for (int shift = 0; shift < 35; shift += 7)
        {
            int b = stream.ReadByte();
            if (b < 0)
                throw new WasmFormatException("Unexpected end of file in LEB128 value.");
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
        }
        throw new WasmFormatException("LEB128 value is too long.");
    }

    private static void Skip(Stream stream, long position) => stream.Position = position;

    private static void CopyBytes(Stream input, Stream output, long count)
    {
        byte[] buffer = new byte[81920];
        while (count > 0)
        {
            int read = input.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (read == 0)
                throw new WasmFormatException("Unexpected end of file.");
            output.Write(buffer, 0, read);
            count -= read;
        }
    }
}
