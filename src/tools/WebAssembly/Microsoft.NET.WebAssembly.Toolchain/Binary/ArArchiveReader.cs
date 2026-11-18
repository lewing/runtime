// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Reads members from a standard Unix ar archive (.a file).
/// Supports BSD and GNU/SysV long-name extensions.
/// </summary>
public static class ArArchiveReader
{
    /// <summary>An individual member extracted from an ar archive.</summary>
    public readonly record struct ArchiveMember(string Name, byte[] Data);

    private static ReadOnlySpan<byte> ArMagicBytes => "!<arch>\n"u8;
    private static ReadOnlySpan<byte> WasmMagicBytes => "\0asm"u8;

    /// <summary>Where a member's contents lie in an archive stream.</summary>
    internal readonly record struct MemberLayout(string Name, long Offset, int Length);

    /// <summary>
    /// Read all members from an ar archive stream.
    /// </summary>
    public static List<ArchiveMember> ReadMembers(Stream stream, string? sourcePath = null)
    {
        var members = new List<ArchiveMember>();
        foreach (MemberLayout member in ReadMemberLayouts(stream, sourcePath))
        {
            stream.Position = member.Offset;
            byte[] data = new byte[member.Length];
            ReadExactly(stream, data, $"Archive '{sourcePath ?? "<archive>"}' ended in the middle of member '{member.Name}'.");
            members.Add(new ArchiveMember(member.Name, data));
        }

        return members;
    }

    /// <summary>
    /// Reads a seekable archive stream's member headers (and its long-name table) without reading members'
    /// contents: each member's name and where its contents lie. Symbol tables are skipped. The stream may be
    /// repositioned between members.
    /// </summary>
    internal static IEnumerable<MemberLayout> ReadMemberLayouts(Stream stream, string? sourcePath = null)
    {
        string context = sourcePath ?? "<archive>";
        byte[] header = new byte[60];
        ReadExactly(stream, header.AsSpan(0, 8), $"Archive '{context}' ended before the global header.");
        if (!header.AsSpan(0, 8).SequenceEqual(ArMagicBytes))
            throw new InvalidDataException($"File '{context}' is not a valid ar archive.");

        byte[]? longNameTable = null;
        long next = stream.Position;
        while (next < stream.Length)
        {
            stream.Position = next;
            ReadExactly(stream, header, $"Archive '{context}' ended in the middle of a member header.");

            if (header[58] != (byte)'`' || header[59] != (byte)'\n')
                throw new InvalidDataException($"Archive '{context}' contains an invalid member header terminator.");

            int size = ParseArchiveNumber(header.AsSpan(48, 10), context, "size");
            long payload = stream.Position;
            if (size < 0 || stream.Length - payload < size)
                throw new InvalidDataException($"Archive '{context}' member payload extends past end of file.");

            string rawName = Encoding.ASCII.GetString(header, 0, 16).Trim();
            long end = payload + size;
            next = end + (end & 1);

            // GNU/SysV long-name table
            if (rawName == "//")
            {
                longNameTable = new byte[size];
                ReadExactly(stream, longNameTable, $"Archive '{context}' ended in the middle of member '{rawName}'.");
                continue;
            }

            // Symbol tables and metadata — skip
            if (rawName is "/" or "/SYM64/" or "__.SYMDEF" or "__.SYMDEF SORTED")
                continue;

            // BSD extended name: "#1/N" — name occupies first N bytes of payload
            if (rawName.StartsWith("#1/", StringComparison.Ordinal))
            {
                if (!int.TryParse(rawName[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int nameLength) ||
                    nameLength < 0 ||
                    nameLength > size)
                {
                    throw new InvalidDataException(
                        $"Archive '{context}' contains an invalid BSD member name length '{rawName}'.");
                }

                byte[] name = new byte[nameLength];
                ReadExactly(stream, name, $"Archive '{context}' ended in the middle of member '{rawName}'.");
                yield return new MemberLayout(Encoding.UTF8.GetString(name).TrimEnd('\0'), payload + nameLength, size - nameLength);
                continue;
            }

            yield return new MemberLayout(ResolveName(rawName, longNameTable, context), payload, size);
        }
    }

    /// <summary>
    /// Read only wasm members from an archive, parsing each as a <see cref="WasmModule"/>.
    /// Skips non-wasm members silently. Returns module + source label pairs.
    /// </summary>
    public static List<(WasmModule Module, string Label)> ReadWasmModules(
        Stream stream, string? sourcePath = null)
    {
        var result = new List<(WasmModule, string)>();
        foreach ((ReadOnlyMemory<byte> bytes, string label) in ReadWasmMembers(stream, sourcePath))
            result.Add((ModuleReader.ParseCoreModule(bytes), label));
        return result;
    }

    /// <summary>
    /// The contents and labels of every WebAssembly member, unparsed (for reading members lazily). The archive is
    /// read into one buffer and each member is a slice of it, so its members are neither copied nor, being one
    /// large object, moved by the garbage collector while a link holds them.
    /// </summary>
    public static List<(ReadOnlyMemory<byte> Bytes, string Label)> ReadWasmMembers(Stream stream, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        byte[] archive = new byte[checked((int)(stream.Length - stream.Position))];
        ReadExactly(stream, archive, $"Archive '{sourcePath ?? "<archive>"}' could not be read.");

        var result = new List<(ReadOnlyMemory<byte>, string)>();
        using var view = new MemoryStream(archive, writable: false);
        foreach (MemberLayout member in ReadMemberLayouts(view, sourcePath))
        {
            ReadOnlyMemory<byte> data = archive.AsMemory((int)member.Offset, member.Length);
            if (data.Length < 4 || !data.Span[..4].SequenceEqual(WasmMagicBytes))
                continue;
            result.Add((data, sourcePath is not null ? $"{sourcePath}({member.Name})" : member.Name));
        }

        return result;
    }

    // A GNU/SysV member name: "/N" is at offset N in the long-name table; a short name ends in '/'.
    private static string ResolveName(string rawName, byte[]? longNameTable, string context)
    {
        if (rawName.Length > 1 && rawName[0] == '/' &&
            int.TryParse(rawName[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        {
            ReadOnlySpan<byte> table = longNameTable;
            if (offset < 0 || offset >= table.Length)
                throw new InvalidDataException(
                    $"Archive '{context}' contains an out-of-range long-name offset '{offset}'.");

            return ReadLongName(table[offset..]);
        }

        return rawName.TrimEnd('/', ' ');
    }

    private static string ReadLongName(ReadOnlySpan<byte> longNameBytes)
    {
        // GNU entries end with "/\n"; a name may itself contain '/' (llvm-ar puts such names in the table).
        int end = longNameBytes.IndexOf("/\n"u8);
        if (end < 0) end = longNameBytes.IndexOf((byte)'\n');
        if (end < 0) end = longNameBytes.Length;

        return Encoding.UTF8.GetString(longNameBytes[..end]).TrimEnd('\0');
    }

    private static int ParseArchiveNumber(ReadOnlySpan<byte> valueBytes, string context, string fieldName)
    {
        string value = Encoding.ASCII.GetString(valueBytes).Trim();
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int parsedValue))
            throw new InvalidDataException($"Archive '{context}' contains an invalid {fieldName} value '{value}'.");

        return parsedValue;
    }

    private static void ReadExactly(Stream stream, Span<byte> buffer, string errorMessage)
    {
        int totalRead = 0;
        while (totalRead < buffer.Length)
        {
            int bytesRead = stream.Read(buffer[totalRead..]);
            if (bytesRead == 0)
                throw new InvalidDataException(errorMessage);

            totalRead += bytesRead;
        }
    }
}
