// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// An out-of-line function body described by a <c>DW_TAG_subprogram</c> with a code address
/// (<c>DW_AT_low_pc</c>). Inlined instances (<c>DW_TAG_inlined_subroutine</c>), abstract
/// definitions and declarations have no body of their own and are not reported.
/// </summary>
/// <param name="Name">
/// <c>DW_AT_name</c>, taken from the DIE or, for a concrete instance or an out-of-class member
/// definition, from its <c>DW_AT_abstract_origin</c> / <c>DW_AT_specification</c>.
/// </param>
/// <param name="LinkageName"><c>DW_AT_linkage_name</c> (mangled), resolved the same way.</param>
/// <param name="LowPc">The resolved code address (code-section offset in LLVM's wasm DWARF).</param>
/// <param name="IsTombstone">
/// The address is a linker tombstone (all ones, or all ones minus one): the function was discarded
/// (e.g. <c>--gc-sections</c>) and has no body in this module. Normal, not a sign of broken DWARF.
/// </param>
internal readonly record struct DwarfSubprogram(string? Name, string? LinkageName, long LowPc, bool IsTombstone);

/// <summary>Every out-of-line subprogram in a module's DWARF.</summary>
/// <param name="HasDebugInfo">The module has a <c>.debug_info</c> section.</param>
/// <param name="Subprograms">Every subprogram with a code address, in .debug_info order.</param>
/// <param name="Units">Compile units read.</param>
/// <param name="SkippedUnits">Units that could not be read (unsupported version or unit type, missing abbreviations, or a malformed header, unit_length or DIE).</param>
internal sealed record DwarfSubprogramIndex(
    bool HasDebugInfo, IReadOnlyList<DwarfSubprogram> Subprograms, int Units, int SkippedUnits);

/// <summary>
/// Reads the <c>DW_TAG_subprogram</c> DIEs of a module's DWARF — names and code addresses only — with
/// the shared DIE parser (<see cref="DwarfReaderHelpers.ParseDIEs"/>) in its filtered mode, so the
/// attributes of every other DIE (types, variables, lexical blocks) are skipped, not materialized.
/// </summary>
internal static class DwarfSubprogramReader
{
    private const int MaxReferenceChain = 8;

    public static DwarfSubprogramIndex Read(Microsoft.NET.WebAssembly.Module.WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        byte[]? Section(string name) => module.CustomSections.FirstOrDefault(cs => cs.Name == name)?.Content;

        var info = Section(".debug_info");
        if (info is null)
            return new DwarfSubprogramIndex(false, [], 0, 0);
        return Parse(info, Section(".debug_abbrev") ?? [], Section(".debug_str"), Section(".debug_line_str"),
            Section(".debug_str_offsets"), Section(".debug_addr"));
    }

    internal static DwarfSubprogramIndex Parse(
        ReadOnlySpan<byte> infoData, ReadOnlySpan<byte> abbrevData, ReadOnlySpan<byte> strData, ReadOnlySpan<byte> lineStrData,
        ReadOnlySpan<byte> strOffsetsData, ReadOnlySpan<byte> addrData)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrevData);
        // Every kept DIE by its .debug_info SECTION offset, so CU-relative and DW_FORM_ref_addr
        // references (cross-CU abstract origins, e.g. after LTO) resolve the same way.
        var bySectionOffset = new Dictionary<long, (DwarfReaderHelpers.DwarfDIE Die, int CuStart)>();
        var bodies = new List<(DwarfReaderHelpers.DwarfDIE Die, int CuStart, long LowPc, byte AddrSize)>();
        int units = 0, skipped = 0;
        var unitWalk = new DwarfUnitEnumerator(infoData, ".debug_info");

        while (unitWalk.MoveNext())
        {
            if (unitWalk.Error is not null) { skipped++; break; } // a bad unit_length leaves no next unit
            var frame = unitWalk.Current;
            DwarfUnitHeader header;
            try
            {
                if (!DwarfUnitHeader.TryRead(infoData, frame, ".debug_info", out header)) { skipped++; continue; }
            }
            catch (DwarfFormatException)
            {
                skipped++;
                continue;
            }

            // DW_UT_compile and DW_UT_partial hold function bodies; type and skeleton/split units do not.
            if (header.UnitType is not (DwUt.Compile or DwUt.Partial)
                || !header.TryGetAbbrevTable(abbrevTables, out var abbrevTable)) { skipped++; continue; }

            // A malformed unit is skipped and counted; the units around it are still read.
            var unit = infoData[..frame.UnitEnd];
            var format = header.Format;
            List<DwarfReaderHelpers.DwarfDIE> dies;
            try
            {
                var (strOffsetsBase, addrBase) = DwarfReaderHelpers.ReadUnitBases(unit, header.DieStart, unit.Length, abbrevTable, format);
                var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, strOffsetsData, strOffsetsBase,
                    addrData, addrBase, resolveAddrx: true);
                dies = DwarfReaderHelpers.ParseDIEs(unit, header.DieStart, unit.Length, abbrevTable, format, ctx, frame.UnitStart,
                    keepTag: static tag => tag is DwTag.Subprogram or DwTag.CompileUnit or DwTag.PartialUnit);
            }
            catch (Exception ex) when (ex is DwarfFormatException || DwarfReaderHelpers.IsRangeError(ex))
            {
                skipped++;
                continue;
            }
            units++;

            foreach (var die in dies)
            {
                bySectionOffset[(long)frame.UnitStart + die.Offset] = (die, frame.UnitStart);
                if (die.Tag == DwTag.Subprogram
                    && die.Attributes.TryGetValue(DwAt.LowPc, out var lowPcObj) && lowPcObj is long lowPc)
                    bodies.Add((die, frame.UnitStart, lowPc, header.AddressSize));
            }
        }

        var result = new List<DwarfSubprogram>(bodies.Count);
        foreach (var (die, cuStart, lowPc, addrSize) in bodies)
        {
            var (name, linkage) = ResolveNames(die, cuStart, bySectionOffset);
            result.Add(new DwarfSubprogram(name, linkage, lowPc, IsTombstone(lowPc, addrSize)));
        }
        return new DwarfSubprogramIndex(true, result, units, skipped);
    }

    /// <summary>
    /// wasm-ld (like lld) writes all-ones for the address of a discarded function in .debug_info
    /// and all-ones-minus-one in .debug_ranges / .debug_loc; LLVM's readers treat both as dead code.
    /// </summary>
    private static bool IsTombstone(long lowPc, byte addrSize)
    {
        ulong max = addrSize == 8 ? ulong.MaxValue : uint.MaxValue;
        ulong v = addrSize == 8 ? (ulong)lowPc : (uint)lowPc;
        return v == max || v == max - 1;
    }

    private static (string? Name, string? Linkage) ResolveNames(
        DwarfReaderHelpers.DwarfDIE die, int cuStart,
        Dictionary<long, (DwarfReaderHelpers.DwarfDIE Die, int CuStart)> bySectionOffset)
    {
        string? name = null, linkage = null;
        var current = die;
        int currentCu = cuStart;
        for (int hop = 0; hop <= MaxReferenceChain && current is not null; hop++)
        {
            name ??= StringAttr(current, DwAt.Name);
            linkage ??= StringAttr(current, DwAt.LinkageName) ?? StringAttr(current, DwAt.MipsLinkageName);
            if (name is not null && linkage is not null)
                break;

            var next = Reference(current, currentCu, DwAt.AbstractOrigin, bySectionOffset)
                ?? Reference(current, currentCu, DwAt.Specification, bySectionOffset);
            if (next is null)
                break;
            (current, currentCu) = next.Value;
        }
        return (name, linkage);
    }

    private static string? StringAttr(DwarfReaderHelpers.DwarfDIE die, ushort attr) =>
        die.Attributes.TryGetValue(attr, out var v) && v is string s && s.Length > 0 ? s : null;

    private static (DwarfReaderHelpers.DwarfDIE, int)? Reference(
        DwarfReaderHelpers.DwarfDIE die, int cuStart, ushort attr,
        Dictionary<long, (DwarfReaderHelpers.DwarfDIE Die, int CuStart)> bySectionOffset)
    {
        if (!die.Attributes.TryGetValue(attr, out var v) || v is not long offset)
            return null;
        long sectionOffset = die.SectionRefAttributes?.Contains(attr) == true ? offset : cuStart + offset;
        return bySectionOffset.TryGetValue(sectionOffset, out var target) ? (target.Die, target.CuStart) : null;
    }
}
