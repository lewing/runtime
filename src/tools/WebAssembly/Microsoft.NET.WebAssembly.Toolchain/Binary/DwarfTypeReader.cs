// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>A DWARF struct/class type definition with its member layout.</summary>
public sealed record DwarfStructType(string Name, int ByteSize, IReadOnlyList<DwarfMember> Members);

/// <summary>A single member (field) within a DWARF struct/class type.</summary>
public sealed record DwarfMember(string Name, string TypeName, int ByteOffset, int ByteSize);

/// <summary>
/// Reads DWARF .debug_info to extract struct/class type definitions and their
/// member layouts. Supports DWARF versions 2–5, 32- and 64-bit, and DWARF 5 type units.
/// Designed for building debug lenses that map managed runtime state from linear memory.
/// </summary>
public static class DwarfTypeReader
{
    /// <summary>
    /// Search DWARF debug info for struct/class type definitions. Malformed units are skipped (their
    /// types are not returned); use the overload with <c>errors</c> to see them.
    /// </summary>
    /// <param name="module">The parsed wasm module containing DWARF custom sections.</param>
    /// <param name="namePattern">Optional case-insensitive substring filter on type names.</param>
    /// <param name="maxResults">Maximum number of types to return (default 50).</param>
    /// <returns>List of struct types found, filtered and capped at <paramref name="maxResults"/>.</returns>
    public static IReadOnlyList<DwarfStructType> ReadStructTypes(
        Microsoft.NET.WebAssembly.Module.WasmModule module, string? namePattern = null, int maxResults = 50)
        => ReadStructTypes(module, namePattern, maxResults, out _);

    /// <summary>
    /// Search DWARF debug info for struct/class type definitions, recovering per unit: a malformed
    /// unit is reported in <paramref name="errors"/> and skipped, and the units around it are still
    /// read. Only a bad <c>unit_length</c> ends the search.
    /// </summary>
    /// <param name="module">The parsed wasm module containing DWARF custom sections.</param>
    /// <param name="namePattern">Optional case-insensitive substring filter on type names.</param>
    /// <param name="maxResults">Maximum number of types to return.</param>
    /// <param name="errors">One error per malformed .debug_info unit; empty for well-formed DWARF.</param>
    public static IReadOnlyList<DwarfStructType> ReadStructTypes(
        Microsoft.NET.WebAssembly.Module.WasmModule module, string? namePattern, int maxResults,
        out IReadOnlyList<DwarfFormatException> errors)
    {
        ArgumentNullException.ThrowIfNull(module);

        var debugInfoSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_info");
        if (debugInfoSection is null)
        {
            errors = [];
            return [];
        }

        var debugAbbrevSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_abbrev");
        var debugStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str");
        var debugLineStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_line_str");
        var debugStrOffsetsSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str_offsets");

        var errorList = new List<DwarfFormatException>();
        var types = ParseStructTypes(
            debugInfoSection.Content,
            debugAbbrevSection?.Content ?? [],
            debugStrSection?.Content,
            debugLineStrSection?.Content,
            debugStrOffsetsSection?.Content,
            namePattern,
            maxResults,
            errorList);
        errors = errorList;
        return types;
    }

    /// <summary>
    /// Core parsing entry point operating on raw section bytes. Malformed units are added to
    /// <paramref name="errors"/> and skipped; with no list, the first one is thrown.
    /// </summary>
    /// <exception cref="DwarfFormatException">A unit is malformed and <paramref name="errors"/> is null.</exception>
    internal static IReadOnlyList<DwarfStructType> ParseStructTypes(
        ReadOnlySpan<byte> infoData,
        ReadOnlySpan<byte> abbrevData,
        ReadOnlySpan<byte> strData,
        ReadOnlySpan<byte> lineStrData,
        ReadOnlySpan<byte> strOffsetsData,
        string? namePattern,
        int maxResults,
        List<DwarfFormatException>? errors = null)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrevData);
        var results = new List<DwarfStructType>();
        var units = new DwarfUnitEnumerator(infoData, InfoSection);

        while (results.Count < maxResults && units.MoveNext())
        {
            if (units.Error is { } lengthError)
            {
                if (errors is null)
                    throw lengthError;
                errors.Add(lengthError);
                break;
            }

            List<DwarfReaderHelpers.DwarfDIE>? dies;
            try
            {
                dies = ReadUnitDies(infoData, units.Current, abbrevTables, strData, lineStrData, strOffsetsData);
            }
            catch (DwarfFormatException ex) when (errors is not null)
            {
                errors.Add(ex);
                continue;
            }

            if (dies is not null)
                ExtractStructTypes(dies, results, namePattern, maxResults);
        }

        return results;
    }

    private const string InfoSection = ".debug_info";

    /// <summary>
    /// Reads one unit's DIEs; null when the unit has an unsupported version or unit type, or unknown
    /// abbreviations. Every read is bounded by the unit's end, so a malformed unit cannot read into the
    /// next one, and a fixed-size field past the end is reported, not thrown as a runtime exception.
    /// </summary>
    private static List<DwarfReaderHelpers.DwarfDIE>? ReadUnitDies(
        ReadOnlySpan<byte> infoData,
        in DwarfUnitFrame frame,
        Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>> abbrevTables,
        ReadOnlySpan<byte> strData,
        ReadOnlySpan<byte> lineStrData,
        ReadOnlySpan<byte> strOffsetsData)
    {
        try
        {
            if (!DwarfUnitHeader.TryRead(infoData, frame, InfoSection, out var header)
                || !header.TryGetAbbrevTable(abbrevTables, out var abbrevTable))
                return null;

            var unit = infoData[..frame.UnitEnd];
            var format = header.Format;
            var (strOffsetsBase, _) = DwarfReaderHelpers.ReadUnitBases(unit, header.DieStart, unit.Length, abbrevTable, format);
            var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, strOffsetsData, strOffsetsBase);
            return DwarfReaderHelpers.ParseDIEs(unit, header.DieStart, unit.Length, abbrevTable, format, ctx, frame.UnitStart);
        }
        catch (Exception ex) when (DwarfReaderHelpers.IsRangeError(ex))
        {
            throw DwarfReaderHelpers.UnitOverrun(InfoSection, frame.UnitStart, ex);
        }
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
            if (die.Tag != DwTag.StructureType
                && die.Tag != DwTag.ClassType
                && die.Tag != DwTag.UnionType)
                continue;

            if (!die.Attributes.TryGetValue(DwAt.Name, out var nameObj))
                continue;
            string name = nameObj?.ToString() ?? "";
            if (string.IsNullOrEmpty(name))
                continue;

            if (namePattern is not null &&
                name.IndexOf(namePattern, StringComparison.OrdinalIgnoreCase) < 0)
                continue;

            int byteSize = 0;
            if (die.Attributes.TryGetValue(DwAt.ByteSize, out var sizeObj) && sizeObj is long sizeVal)
                byteSize = (int)sizeVal;

            var members = new List<DwarfMember>();
            for (int j = i + 1; j < dies.Count; j++)
            {
                var child = dies[j];
                if (child.Depth <= die.Depth) break;
                if (child.Depth != die.Depth + 1) continue;
                if (child.Tag != DwTag.Member) continue;

                string memberName = child.Attributes.TryGetValue(DwAt.Name, out var mn)
                    ? mn?.ToString() ?? "(anon)" : "(anon)";

                int memberOffset = 0;
                if (child.Attributes.TryGetValue(DwAt.DataMemberLocation, out var locObj))
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
    public List<(ushort Name, uint Form, long ImplicitConst)> Attributes = new();
}

/// <summary>Parses DWARF .debug_abbrev sections.</summary>
internal static class DwarfAbbrevParser
{
    /// <summary>Every abbreviation table in <paramref name="data"/>, by its section offset.</summary>
    public static Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>> Parse(ReadOnlySpan<byte> data)
    {
        var tables = new Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>>();
        int pos = 0;
        while (pos < data.Length)
        {
            int tableStart = pos;
            var table = ParseTable(data, ref pos);
            if (table.Count > 0)
                tables[tableStart] = table;
        }
        return tables;
    }

    /// <summary>
    /// The abbreviation table at <paramref name="pos"/>: entries up to its terminating 0 code (or the end
    /// of the data), after which <paramref name="pos"/> is left.
    /// </summary>
    public static Dictionary<uint, DwarfAbbrevEntry> ParseTable(ReadOnlySpan<byte> data, ref int pos)
    {
        var table = new Dictionary<uint, DwarfAbbrevEntry>();
        while (pos < data.Length)
        {
            uint code = DwarfReaderHelpers.ReadULEB128(data, ref pos);
            if (code == 0)
                break;

            var entry = new DwarfAbbrevEntry
            {
                Tag = (ushort)DwarfReaderHelpers.ReadULEB128(data, ref pos),
                HasChildren = pos < data.Length && data[pos++] == 1
            };

            while (pos < data.Length)
            {
                ushort attrName = (ushort)DwarfReaderHelpers.ReadULEB128(data, ref pos);
                uint attrForm = DwarfReaderHelpers.ReadULEB128(data, ref pos);
                if (attrName == 0 && attrForm == 0) break;

                long implicitConst = 0;
                if (attrForm == (uint)DwForm.ImplicitConst)
                    implicitConst = DwarfReaderHelpers.ReadSLEB128(data, ref pos);

                entry.Attributes.Add((attrName, attrForm, implicitConst));
            }

            table[code] = entry;
        }
        return table;
    }
}
