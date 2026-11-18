// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Text;

/// <summary>
/// A compile unit's summary: its <c>DW_AT_name</c>, <c>DW_AT_language</c> code and <c>DW_AT_producer</c>,
/// and the <c>DW_AT_name</c> of every subprogram in it.
/// </summary>
internal sealed record DwarfCompileUnitSummary(string? Name, ushort? Language, string? Producer, List<string> Functions);

/// <summary>
/// Summarizes the compile units of a module's <c>.debug_info</c> (the <c>wasm_dwarf_info</c> tool) with the
/// shared unit, abbreviation and form readers. Only the strings it returns are decoded; every other
/// attribute is skipped. Recovers per unit, like <see cref="DwarfTypeReader"/>: a malformed unit is
/// reported and skipped, and only a bad <c>unit_length</c> ends the read.
/// </summary>
internal static class DwarfCompileUnitReader
{
    private const string InfoSection = ".debug_info";

    /// <summary>
    /// Up to <paramref name="maxUnits"/> compile units of <paramref name="module"/>, in section order;
    /// malformed units are added to <paramref name="errors"/>. Null when the module has no .debug_info.
    /// </summary>
    public static List<DwarfCompileUnitSummary>? Read(Module.WasmModule module, int maxUnits, List<DwarfFormatException> errors)
    {
        ArgumentNullException.ThrowIfNull(module);
        byte[]? info = null, abbrev = null, str = null, lineStr = null, strOffsets = null;
        foreach (var section in module.CustomSections)
        {
            switch (section.Name)
            {
                case ".debug_info": info ??= section.Content; break;
                case ".debug_abbrev": abbrev ??= section.Content; break;
                case ".debug_str": str ??= section.Content; break;
                case ".debug_line_str": lineStr ??= section.Content; break;
                case ".debug_str_offsets": strOffsets ??= section.Content; break;
            }
        }
        return info is null ? null : Parse(info, abbrev, str, lineStr, strOffsets, maxUnits, errors);
    }

    internal static List<DwarfCompileUnitSummary> Parse(
        ReadOnlySpan<byte> info, ReadOnlySpan<byte> abbrev, ReadOnlySpan<byte> str, ReadOnlySpan<byte> lineStr,
        ReadOnlySpan<byte> strOffsets, int maxUnits, List<DwarfFormatException> errors)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrev);
        var result = new List<DwarfCompileUnitSummary>();
        var units = new DwarfUnitEnumerator(info, InfoSection);
        while (result.Count < maxUnits && units.MoveNext())
        {
            if (units.Error is { } lengthError)
            {
                errors.Add(lengthError);
                break;
            }

            try
            {
                if (ReadUnit(info, units.Current, abbrevTables, str, lineStr, strOffsets) is { } unit)
                    result.Add(unit);
            }
            catch (DwarfFormatException ex)
            {
                errors.Add(ex);
            }
        }
        return result;
    }

    /// <summary>One unit's summary; null when it is not a readable compile unit.</summary>
    private static DwarfCompileUnitSummary? ReadUnit(
        ReadOnlySpan<byte> info, in DwarfUnitFrame frame,
        Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>> abbrevTables,
        ReadOnlySpan<byte> str, ReadOnlySpan<byte> lineStr, ReadOnlySpan<byte> strOffsets)
    {
        try
        {
            if (!DwarfUnitHeader.TryRead(info, frame, InfoSection, out var header)
                || !header.TryGetAbbrevTable(abbrevTables, out var table))
                return null;

            // Every read is bounded by the unit, so a malformed unit cannot read into the next one.
            var unit = info[..frame.UnitEnd];
            var format = header.Format;
            int pos = header.DieStart;
            uint cuCode = DwarfReaderHelpers.ReadULEB128(unit, ref pos);
            if (cuCode == 0 || !table.TryGetValue(cuCode, out var cuEntry) || cuEntry.Tag != DwTag.CompileUnit)
                return null;

            var (strOffsetsBase, _) = DwarfReaderHelpers.ReadUnitBases(unit, header.DieStart, unit.Length, table, format);
            var ctx = new DwarfReaderHelpers.StringContext(str, lineStr, strOffsets, strOffsetsBase);

            string? name = null, producer = null;
            ushort? language = null;
            foreach (var (attr, form, _) in cuEntry.Attributes)
            {
                if ((attr is DwAt.Name or DwAt.Producer) && DwarfReaderHelpers.IsStringForm(form))
                {
                    string? value = ReadName(unit, ref pos, form, format, ctx);
                    if (attr == DwAt.Name)
                        name = value;
                    else
                        producer = value;
                }
                else if (attr == DwAt.Language && (DwForm)form is DwForm.Data1 or DwForm.Data2 or DwForm.Udata)
                {
                    language = (ushort)DwarfReaderHelpers.ReadFormRaw(unit, ref pos, form, format);
                }
                else
                {
                    DwarfReaderHelpers.SkipFormValue(unit, ref pos, form, format);
                }
            }

            var functions = new List<string>();
            if (cuEntry.HasChildren)
            {
                int depth = 1;
                while (pos < unit.Length && depth > 0)
                {
                    uint code = DwarfReaderHelpers.ReadULEB128(unit, ref pos);
                    if (code == 0)
                    {
                        depth--;
                        continue;
                    }
                    if (!table.TryGetValue(code, out var entry))
                        break;

                    bool isSubprogram = entry.Tag == DwTag.Subprogram;
                    string? functionName = null;
                    foreach (var (attr, form, _) in entry.Attributes)
                    {
                        if (isSubprogram && attr == DwAt.Name && DwarfReaderHelpers.IsStringForm(form))
                            functionName = ReadName(unit, ref pos, form, format, ctx);
                        else
                            DwarfReaderHelpers.SkipFormValue(unit, ref pos, form, format);
                    }
                    if (functionName is not null)
                        functions.Add(functionName);
                    if (entry.HasChildren)
                        depth++;
                }
            }

            return new DwarfCompileUnitSummary(name, language, producer, functions);
        }
        catch (Exception ex) when (DwarfReaderHelpers.IsRangeError(ex))
        {
            throw DwarfReaderHelpers.UnitOverrun(InfoSection, frame.UnitStart, ex);
        }
    }

    /// <summary>A string attribute, decoded; null when it is empty or cannot be resolved.</summary>
    private static string? ReadName(
        ReadOnlySpan<byte> unit, ref int pos, uint form, in DwarfFormContext format, in DwarfReaderHelpers.StringContext ctx) =>
        DwarfReaderHelpers.TryReadString(unit, ref pos, form, format, ctx, out var value, out _) && !value.IsEmpty
            ? Encoding.UTF8.GetString(value)
            : null;
}
