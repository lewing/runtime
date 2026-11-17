// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>A DWARF struct/class type definition with its member layout.</summary>
public sealed record DwarfStructType(string Name, int ByteSize, IReadOnlyList<DwarfMember> Members);

/// <summary>A single member (field) within a DWARF struct/class type.</summary>
public sealed record DwarfMember(string Name, string TypeName, int ByteOffset, int ByteSize);

/// <summary>
/// Reads DWARF .debug_info to extract struct/class type definitions and their
/// member layouts. Supports DWARF versions 2–5 in the 32-bit format used by wasm32.
/// Designed for building debug lenses that map managed runtime state from linear memory.
/// </summary>
public static class DwarfTypeReader
{
    /// <summary>
    /// Search DWARF debug info for struct/class type definitions.
    /// </summary>
    /// <param name="module">The parsed wasm module containing DWARF custom sections.</param>
    /// <param name="namePattern">Optional case-insensitive substring filter on type names.</param>
    /// <param name="maxResults">Maximum number of types to return (default 50).</param>
    /// <returns>List of struct types found, filtered and capped at <paramref name="maxResults"/>.</returns>
    public static IReadOnlyList<DwarfStructType> ReadStructTypes(
        Microsoft.NET.WebAssembly.Module.WasmModule module, string? namePattern = null, int maxResults = 50)
    {
        ArgumentNullException.ThrowIfNull(module);

        var debugInfoSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_info");
        if (debugInfoSection is null)
            return [];

        var debugAbbrevSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_abbrev");
        var debugStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str");
        var debugLineStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_line_str");
        var debugStrOffsetsSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str_offsets");

        return ParseStructTypes(
            debugInfoSection.Content,
            debugAbbrevSection?.Content ?? [],
            debugStrSection?.Content,
            debugLineStrSection?.Content,
            debugStrOffsetsSection?.Content,
            namePattern,
            maxResults);
    }

    /// <summary>Core parsing entry point operating on raw section bytes.</summary>
    internal static IReadOnlyList<DwarfStructType> ParseStructTypes(
        byte[] infoData,
        byte[] abbrevData,
        byte[]? strData,
        byte[]? lineStrData,
        byte[]? strOffsetsData,
        string? namePattern,
        int maxResults)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrevData);
        var results = new List<DwarfStructType>();
        int pos = 0;

        while (pos < infoData.Length && results.Count < maxResults)
        {
            if (pos + 4 > infoData.Length) break;

            int cuStart = pos;
            uint unitLen = DwarfReaderHelpers.ReadU32LE(infoData, pos);
            if (unitLen == 0xFFFFFFFF) break; // 64-bit DWARF not supported

            int cuEnd = pos + 4 + (int)unitLen;
            if (cuEnd > infoData.Length) break;

            ushort version = DwarfReaderHelpers.ReadU16LE(infoData, pos + 4);
            if (version < 2 || version > 5) { pos = cuEnd; continue; }

            int abbrevOffset;
            byte addrSize;
            int diePos;
            if (version >= 5)
            {
                addrSize = infoData[pos + 7];
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 8);
                diePos = pos + 12;
            }
            else
            {
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 6);
                addrSize = infoData[pos + 10];
                diePos = pos + 11;
            }

            if (!abbrevTables.TryGetValue(abbrevOffset, out var abbrevTable))
            {
                pos = cuEnd;
                continue;
            }

            // Parse the CU DIE to extract DW_AT_str_offsets_base for DWARF 5 strx resolution
            int strOffsetsBase = 0;
            if (version >= 5 && diePos < cuEnd)
            {
                int peekPos = diePos;
                uint cuAbbrevCode = DwarfReaderHelpers.ReadULEB128(infoData, ref peekPos);
                if (cuAbbrevCode != 0 && abbrevTable.TryGetValue(cuAbbrevCode, out var cuEntry)
                    && cuEntry.Tag == 0x11 /* DW_TAG_compile_unit */)
                {
                    foreach (var (attrName, attrForm, implicitConst) in cuEntry.Attributes)
                    {
                        if (attrName == 0x72 /* DW_AT_str_offsets_base */)
                            strOffsetsBase = (int)DwarfReaderHelpers.ReadUnsignedFormValue(infoData, ref peekPos, attrForm, addrSize);
                        else
                            DwarfReaderHelpers.SkipFormValue(infoData, ref peekPos, attrForm, addrSize);
                    }
                }
            }

            var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, strOffsetsData, strOffsetsBase);
            var dies = DwarfReaderHelpers.ParseDIEs(infoData, diePos, cuEnd, abbrevTable, addrSize, ctx, cuStart);
            ExtractStructTypes(dies, results, namePattern, maxResults);

            pos = cuEnd;
        }

        return results;
    }

    private static void ExtractStructTypes(
        List<DwarfReaderHelpers.DwarfDIE> dies,
        List<DwarfStructType> results,
        string? namePattern,
        int maxResults)
    {
        var byOffset = new Dictionary<int, DwarfReaderHelpers.DwarfDIE>(dies.Count);
        foreach (var die in dies)
            byOffset[die.Offset] = die;

        for (int i = 0; i < dies.Count && results.Count < maxResults; i++)
        {
            var die = dies[i];
            if (die.Tag != DwarfReaderHelpers.DW_TAG_structure_type
                && die.Tag != DwarfReaderHelpers.DW_TAG_class_type
                && die.Tag != DwarfReaderHelpers.DW_TAG_union_type)
                continue;

            if (!die.Attributes.TryGetValue(DwarfReaderHelpers.DW_AT_name, out var nameObj))
                continue;
            string name = nameObj?.ToString() ?? "";
            if (string.IsNullOrEmpty(name))
                continue;

            if (namePattern is not null &&
                name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            int byteSize = 0;
            if (die.Attributes.TryGetValue(DwarfReaderHelpers.DW_AT_byte_size, out var sizeObj) && sizeObj is long sizeVal)
                byteSize = (int)sizeVal;

            var members = new List<DwarfMember>();
            for (int j = i + 1; j < dies.Count; j++)
            {
                var child = dies[j];
                if (child.Depth <= die.Depth) break;
                if (child.Depth != die.Depth + 1) continue;
                if (child.Tag != DwarfReaderHelpers.DW_TAG_member) continue;

                string memberName = child.Attributes.TryGetValue(DwarfReaderHelpers.DW_AT_name, out var mn)
                    ? mn?.ToString() ?? "(anon)" : "(anon)";

                int memberOffset = 0;
                if (child.Attributes.TryGetValue(DwarfReaderHelpers.DW_AT_data_member_location, out var locObj))
                    memberOffset = (int)DwarfReaderHelpers.GetLongValue(locObj);

                string typeName = DwarfReaderHelpers.ResolveTypeName(child, byOffset);
                int memberSize = DwarfReaderHelpers.ResolveTypeSize(child, byOffset);

                members.Add(new DwarfMember(memberName, typeName, memberOffset, memberSize));
            }

            results.Add(new DwarfStructType(name, byteSize, members));
        }
    }
}

/// <summary>Parsed abbreviation entry for DWARF DIE decoding.</summary>
internal sealed class DwarfAbbrevEntry
{
    public ushort Tag;
    public bool HasChildren;
    public List<(ushort Name, ushort Form, long ImplicitConst)> Attributes = new();
}

/// <summary>Parses DWARF .debug_abbrev sections.</summary>
internal static class DwarfAbbrevParser
{
    public static Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>> Parse(byte[] data)
    {
        var tables = new Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>>();
        int pos = 0;
        int tableStart = 0;
        var currentTable = new Dictionary<uint, DwarfAbbrevEntry>();

        while (pos < data.Length)
        {
            uint code = ReadULEB128(data, ref pos);
            if (code == 0)
            {
                if (currentTable.Count > 0)
                    tables[tableStart] = currentTable;
                tableStart = pos;
                currentTable = new Dictionary<uint, DwarfAbbrevEntry>();
                continue;
            }

            var entry = new DwarfAbbrevEntry
            {
                Tag = (ushort)ReadULEB128(data, ref pos),
                HasChildren = pos < data.Length && data[pos++] == 1
            };

            while (pos < data.Length)
            {
                ushort attrName = (ushort)ReadULEB128(data, ref pos);
                ushort attrForm = (ushort)ReadULEB128(data, ref pos);
                if (attrName == 0 && attrForm == 0) break;

                long implicitConst = 0;
                if (attrForm == 0x21) // DW_FORM_implicit_const
                    implicitConst = ReadSLEB128(data, ref pos);

                entry.Attributes.Add((attrName, attrForm, implicitConst));
            }

            currentTable[code] = entry;
        }

        if (currentTable.Count > 0)
            tables[tableStart] = currentTable;

        return tables;
    }

    private static uint ReadULEB128(byte[] data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
            if (shift >= 35) break;
        }
        return result;
    }

    private static long ReadSLEB128(byte[] data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (pos >= data.Length) break;
            b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && shift > 0 && pos > 0 && (data[pos - 1] & 0x40) != 0)
            result |= -(1L << shift);
        return result;
    }
}
