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

    /// <summary>
    /// Read all members from an ar archive stream.
    /// </summary>
    public static List<ArchiveMember> ReadMembers(Stream stream, string? sourcePath = null)
    {
        string context = sourcePath ?? "<archive>";

        Span<byte> magic = stackalloc byte[8];
        ReadExactly(stream, magic, $"Archive '{context}' ended before the global header.");
        if (!magic.SequenceEqual(ArMagicBytes))
            throw new InvalidDataException($"File '{context}' is not a valid ar archive.");

        var members = new List<ArchiveMember>();
        byte[]? longNameTable = null;
        Span<byte> header = stackalloc byte[60];

        while (stream.Position < stream.Length)
        {
            ReadExactly(stream, header, $"Archive '{context}' ended in the middle of a member header.");

            if (header[58] != (byte)'`' || header[59] != (byte)'\n')
                throw new InvalidDataException($"Archive '{context}' contains an invalid member header terminator.");

            int size = ParseArchiveNumber(header.Slice(48, 10), context, "size");
            if (size < 0 || stream.Length - stream.Position < size)
                throw new InvalidDataException($"Archive '{context}' member payload extends past end of file.");

            string rawName = Encoding.ASCII.GetString(header[..16]).Trim();
            byte[] payload = new byte[size];
            ReadExactly(stream, payload, $"Archive '{context}' ended in the middle of member '{rawName}'.");

            if ((stream.Position & 1) != 0 && stream.Position < stream.Length)
                stream.ReadByte();

            // GNU/SysV long-name table
            if (rawName == "//")
            {
                longNameTable = payload;
                continue;
            }

            // Symbol tables and metadata — skip
            if (rawName is "/" or "/SYM64/" or "__.SYMDEF" or "__.SYMDEF SORTED")
                continue;

            members.Add(ResolveArchiveMember(rawName, payload, longNameTable.AsSpan(), context));
        }

        return members;
    }

    /// <summary>
    /// Read only wasm members from an archive, parsing each as a <see cref="WasmModule"/>.
    /// Skips non-wasm members silently. Returns module + source label pairs.
    /// </summary>
    public static List<(WasmModule Module, string Label)> ReadWasmModules(
        Stream stream, string? sourcePath = null)
    {
        var result = new List<(WasmModule, string)>();

        foreach (var member in ReadMembers(stream, sourcePath))
        {
            if (member.Data.Length < 4 || !member.Data.AsSpan(0, 4).SequenceEqual(WasmMagicBytes))
                continue;

            var module = ModuleReader.ReadModule(member.Data.AsMemory());
            string label = sourcePath is not null ? $"{sourcePath}({member.Name})" : member.Name;
            result.Add((module, label));
        }

        return result;
    }

    private static ArchiveMember ResolveArchiveMember(
        string rawName,
        byte[] payload,
        ReadOnlySpan<byte> longNameTable,
        string context)
    {
        // BSD extended name: "#1/N" — name occupies first N bytes of payload
        if (rawName.StartsWith("#1/", StringComparison.Ordinal))
        {
            if (!int.TryParse(rawName[3..], NumberStyles.None, CultureInfo.InvariantCulture, out int nameLength) ||
                nameLength < 0 ||
                nameLength > payload.Length)
            {
                throw new InvalidDataException(
                    $"Archive '{context}' contains an invalid BSD member name length '{rawName}'.");
            }

            string name = Encoding.UTF8.GetString(payload.AsSpan(0, nameLength)).TrimEnd('\0');
            return new ArchiveMember(name, payload[nameLength..]);
        }

        // GNU/SysV long name: "/N" — name at offset N in the long-name table
        if (rawName.Length > 1 && rawName[0] == '/' &&
            int.TryParse(rawName[1..], NumberStyles.None, CultureInfo.InvariantCulture, out int offset))
        {
            if (offset < 0 || offset >= longNameTable.Length)
                throw new InvalidDataException(
                    $"Archive '{context}' contains an out-of-range long-name offset '{offset}'.");

            string name = ReadLongName(longNameTable[offset..]);
            return new ArchiveMember(name, payload);
        }

        return new ArchiveMember(rawName.TrimEnd('/', ' '), payload);
    }

    private static string ReadLongName(ReadOnlySpan<byte> longNameBytes)
    {
        int end = longNameBytes.IndexOf((byte)'/');
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
