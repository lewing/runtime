// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// What decides the width of an attribute form in one DWARF unit: its address size, its offset size
/// (4 in 32-bit DWARF, 8 in 64-bit DWARF) and its version.
/// </summary>
internal readonly record struct DwarfFormContext(byte AddressSize, byte OffsetSize, ushort Version)
{
    /// <summary>32-bit DWARF with <paramref name="addressSize"/>-byte addresses.</summary>
    public static DwarfFormContext Dwarf32(byte addressSize, ushort version = 4) => new(addressSize, 4, version);

    /// <summary>
    /// The width of <c>DW_FORM_ref_addr</c>: an address in DWARF 2, an offset from DWARF 3 on (DWARF 3 §7.5.4).
    /// </summary>
    public int RefAddrSize => Version <= 2 ? AddressSize : OffsetSize;
}

/// <summary>
/// The extent of one unit in a DWARF section (<c>.debug_info</c>, <c>.debug_line</c>, …): its start, where
/// its header starts (just past <c>unit_length</c>), its end, and whether it is 64-bit DWARF.
/// </summary>
internal readonly record struct DwarfUnitFrame(int UnitStart, int HeaderStart, int UnitEnd, bool Is64Bit)
{
    /// <summary>The width of a section offset in the unit.</summary>
    public byte OffsetSize => Is64Bit ? (byte)8 : (byte)4;
}

/// <summary>
/// Walks the units of a DWARF section. Zero bytes after the last unit are padding and zero-length units
/// are skipped. A unit whose <c>unit_length</c> is reserved or runs past the section is reported once in
/// <see cref="Error"/>, and it ends the walk: there is no next unit to resume at. A unit's end is checked
/// against the section and is always past its length field, so the walk always advances, and every read
/// of a unit can be bounded by <see cref="DwarfUnitFrame.UnitEnd"/>.
/// </summary>
internal ref struct DwarfUnitEnumerator
{
    private readonly ReadOnlySpan<byte> _data;
    private readonly string _section;
    private int _pos;

    public DwarfUnitEnumerator(ReadOnlySpan<byte> data, string section)
    {
        _data = data;
        _section = section;
    }

    /// <summary>The current unit. When <see cref="Error"/> is set, only its <see cref="DwarfUnitFrame.UnitStart"/> is meaningful.</summary>
    public DwarfUnitFrame Current { get; private set; }

    /// <summary>The bad <c>unit_length</c> of the current unit; the walk ends after it.</summary>
    public DwarfFormatException? Error { get; private set; }

    public bool MoveNext()
    {
        Error = null;
        while (_pos < _data.Length)
        {
            if (_data[_pos..].IndexOfAnyExcept((byte)0) < 0)
                break;

            int unitStart = _pos;
            int unitEnd;
            bool is64Bit;
            try
            {
                unitEnd = DwarfReaderHelpers.ReadUnitLength(_data, ref _pos, _section, out is64Bit);
            }
            catch (DwarfFormatException ex)
            {
                Error = ex;
                Current = new DwarfUnitFrame(unitStart, unitStart, unitStart, false);
                _pos = _data.Length;
                return true;
            }

            int headerStart = _pos;
            _pos = unitEnd;
            if (unitEnd == headerStart)
                continue;
            Current = new DwarfUnitFrame(unitStart, headerStart, unitEnd, is64Bit);
            return true;
        }

        _pos = _data.Length;
        return false;
    }
}

/// <summary>
/// The header of a <c>.debug_info</c> unit, in any of its DWARF 2–5 forms, 32- or 64-bit (DWARF 5 §7.5.1):
/// <list type="bullet">
/// <item>DWARF 2–4: <c>version</c>, <c>debug_abbrev_offset</c>, <c>address_size</c>.</item>
/// <item>DWARF 5: <c>version</c>, <c>unit_type</c>, <c>address_size</c>, <c>debug_abbrev_offset</c>, then
/// <c>type_signature</c> and <c>type_offset</c> (type units) or <c>dwo_id</c> (skeleton and split compile units).</item>
/// </list>
/// </summary>
internal readonly record struct DwarfUnitHeader
{
    public DwarfUnitFrame Frame { get; init; }

    public ushort Version { get; init; }

    /// <summary>The DWARF 5 <c>unit_type</c> (<see cref="DwUt"/>); <see cref="DwUt.Compile"/> before DWARF 5.</summary>
    public byte UnitType { get; init; }

    public byte AddressSize { get; init; }

    /// <summary>The unit's <c>debug_abbrev_offset</c>.</summary>
    public ulong AbbrevOffset { get; init; }

    /// <summary>The <c>type_signature</c> of a type unit.</summary>
    public ulong TypeSignature { get; init; }

    /// <summary>The <c>type_offset</c> of a type unit, relative to the unit.</summary>
    public ulong TypeOffset { get; init; }

    /// <summary>The <c>dwo_id</c> of a skeleton or split compile unit.</summary>
    public ulong DwoId { get; init; }

    /// <summary>The section offset of the unit's first DIE.</summary>
    public int DieStart { get; init; }

    public int UnitStart => Frame.UnitStart;

    public int UnitEnd => Frame.UnitEnd;

    public bool Is64Bit => Frame.Is64Bit;

    /// <summary>The form widths of the unit's attributes.</summary>
    public DwarfFormContext Format => new(AddressSize, Frame.OffsetSize, Version);

    /// <summary>
    /// Reads the header of the unit in <paramref name="frame"/> (from a <see cref="DwarfUnitEnumerator"/>
    /// over <paramref name="data"/>). Returns false, with only <see cref="Version"/> set, for a version
    /// other than 2–5: like other DWARF consumers, readers skip such a unit.
    /// </summary>
    /// <exception cref="DwarfFormatException">
    /// The header does not fit in the unit, or it is a DWARF 5 unit type whose header layout is not known
    /// (e.g. a vendor type), so its first DIE cannot be found.
    /// </exception>
    public static bool TryRead(ReadOnlySpan<byte> data, in DwarfUnitFrame frame, string section, out DwarfUnitHeader header)
    {
        var unit = data[..frame.UnitEnd];
        int pos = frame.HeaderStart;
        if (unit.Length - pos < 2)
            throw new DwarfFormatException(section, frame.UnitStart, $"the unit at 0x{frame.UnitStart:X} is too short to hold its version");
        ushort version = DwarfReaderHelpers.ReadU16LE(unit, pos);
        pos += 2;
        header = new DwarfUnitHeader { Frame = frame, Version = version, UnitType = DwUt.Compile };
        if (version is < 2 or > 5)
            return false;

        int offsetSize = frame.OffsetSize;
        if (version < 5)
        {
            if (unit.Length - pos < offsetSize + 1)
                throw HeaderPastEnd(section, frame);
            header = header with
            {
                AbbrevOffset = ReadOffset(unit, pos, offsetSize),
                AddressSize = unit[pos + offsetSize],
                DieStart = pos + offsetSize + 1,
            };
            return true;
        }

        if (unit.Length - pos < 2 + offsetSize)
            throw HeaderPastEnd(section, frame);
        byte unitType = unit[pos];
        header = header with { UnitType = unitType };
        int extra = unitType switch
        {
            DwUt.Compile or DwUt.Partial => 0,
            DwUt.Type or DwUt.SplitType => 8 + offsetSize,
            DwUt.Skeleton or DwUt.SplitCompile => 8,
            _ => -1,
        };
        if (extra < 0)
            throw new DwarfFormatException(section, frame.UnitStart,
                $"the unit at 0x{frame.UnitStart:X} has unit_type 0x{unitType:X2}, whose header layout is not known");

        int fixedEnd = pos + 2 + offsetSize;
        if (unit.Length - fixedEnd < extra)
            throw HeaderPastEnd(section, frame);
        header = header with
        {
            AddressSize = unit[pos + 1],
            AbbrevOffset = ReadOffset(unit, pos + 2, offsetSize),
            DieStart = fixedEnd + extra,
        };
        if (unitType is DwUt.Type or DwUt.SplitType)
            header = header with { TypeSignature = DwarfReaderHelpers.ReadU64LE(unit, fixedEnd), TypeOffset = ReadOffset(unit, fixedEnd + 8, offsetSize) };
        else if (extra == 8)
            header = header with { DwoId = DwarfReaderHelpers.ReadU64LE(unit, fixedEnd) };
        return true;
    }

    /// <summary>The unit's abbreviation table in <paramref name="tables"/> (from <see cref="DwarfAbbrevParser.Parse"/>).</summary>
    public bool TryGetAbbrevTable(
        Dictionary<int, Dictionary<uint, DwarfAbbrevEntry>> tables, [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out Dictionary<uint, DwarfAbbrevEntry>? table)
    {
        table = null;
        return AbbrevOffset <= int.MaxValue && tables.TryGetValue((int)AbbrevOffset, out table);
    }

    private static ulong ReadOffset(ReadOnlySpan<byte> data, int pos, int offsetSize) =>
        offsetSize == 8 ? DwarfReaderHelpers.ReadU64LE(data, pos) : DwarfReaderHelpers.ReadU32LE(data, pos);

    private static DwarfFormatException HeaderPastEnd(string section, in DwarfUnitFrame frame) =>
        new(section, frame.UnitStart, $"the header of the unit at 0x{frame.UnitStart:X} runs past its end");
}
