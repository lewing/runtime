// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Text;

/// <summary>One row emitted by the DWARF line number state machine.</summary>
public sealed record DwarfLineRow(
    uint Address,
    string FileName,
    int Line,
    int Column,
    bool IsStatement,
    bool PrologueEnd,
    bool EndSequence);

/// <summary>Result of parsing one DWARF line number program (compilation unit).</summary>
public sealed class DwarfLineProgram
{
    public IReadOnlyList<string> FileNames { get; }
    public IReadOnlyList<DwarfLineRow> Rows { get; }

    public DwarfLineProgram(IReadOnlyList<string> fileNames, IReadOnlyList<DwarfLineRow> rows)
    {
        FileNames = fileNames;
        Rows = rows;
    }
}

/// <summary>
/// The line programs of a <c>.debug_line</c> section, and the errors of the units that could not be
/// read. A malformed unit is reported and skipped; the units around it are still read.
/// </summary>
public sealed class DwarfLineParseResult
{
    public DwarfLineParseResult(IReadOnlyList<DwarfLineProgram> programs, IReadOnlyList<DwarfFormatException> errors)
    {
        Programs = programs;
        Errors = errors;
    }

    /// <summary>One program per readable unit, in section order.</summary>
    public IReadOnlyList<DwarfLineProgram> Programs { get; }

    /// <summary>One error per malformed unit, in section order. Empty for well-formed DWARF.</summary>
    public IReadOnlyList<DwarfFormatException> Errors { get; }
}

/// <summary>
/// Parses DWARF .debug_line sections to produce source line ↔ code address tables.
/// Supports DWARF versions 2–5 in the 32-bit DWARF format used by wasm32.
/// </summary>
public static class DwarfLineReader
{
    private const string Section = ".debug_line";

    /// <summary>
    /// Parses all line number programs from <paramref name="data"/> (.debug_line content), and
    /// throws on the first malformed unit. Use <see cref="ParseUnits"/> to keep the good units.
    /// </summary>
    /// <exception cref="DwarfFormatException">A unit is malformed.</exception>
    public static List<DwarfLineProgram> Parse(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> lineStrData = default,
        ReadOnlySpan<byte> strData = default)
    {
        var result = ParseUnits(data, lineStrData, strData);
        if (result.Errors.Count != 0)
            throw result.Errors[0];
        return (List<DwarfLineProgram>)result.Programs;
    }

    /// <summary>
    /// Parses every line number program in <paramref name="data"/> (.debug_line content), one per
    /// unit, recovering per unit. A unit that is malformed inside is reported in
    /// <see cref="DwarfLineParseResult.Errors"/> and skipped: its end is already known, so the next
    /// unit is still read. Only a bad <c>unit_length</c> ends the parse, because it leaves no next
    /// unit to resume at. Units with an unsupported version, 64-bit DWARF units, zero-length units
    /// and trailing zero padding are skipped without an error.
    /// </summary>
    public static DwarfLineParseResult ParseUnits(
        ReadOnlySpan<byte> data,
        ReadOnlySpan<byte> lineStrData = default,
        ReadOnlySpan<byte> strData = default)
    {
        var programs = new List<DwarfLineProgram>();
        List<DwarfFormatException>? errors = null;
        var units = new DwarfUnitEnumerator(data, Section);

        while (units.MoveNext())
        {
            if (units.Error is { } lengthError)
            {
                (errors ??= []).Add(lengthError);
                break;
            }

            var frame = units.Current;
            if (frame.Is64Bit) // 64-bit DWARF line tables are not supported
                continue;
            try
            {
                if (ReadUnit(data[..frame.UnitEnd], frame.HeaderStart, frame.UnitStart, lineStrData, strData) is { } program)
                    programs.Add(program);
            }
            catch (DwarfFormatException ex)
            {
                (errors ??= []).Add(ex);
            }
        }

        return new DwarfLineParseResult(programs, errors is null ? [] : errors);
    }

    /// <summary>
    /// Parses the one unit at <paramref name="offset"/> in <paramref name="data"/> (a whole
    /// .debug_line section, so error offsets are section offsets). Returns null for a 64-bit or
    /// unsupported-version unit.
    /// </summary>
    /// <exception cref="DwarfFormatException">The unit is malformed.</exception>
    internal static DwarfLineProgram? ParseUnitAt(
        ReadOnlySpan<byte> data, int offset, ReadOnlySpan<byte> lineStrData = default, ReadOnlySpan<byte> strData = default)
    {
        int pos = offset;
        int programEnd = DwarfReaderHelpers.ReadUnitLength(data, ref pos, Section, out bool is64Bit);
        return is64Bit ? null : ReadUnit(data[..programEnd], pos, offset, lineStrData, strData);
    }

    /// <summary>
    /// Reads one unit. Every read is bounded by <paramref name="unit"/>, which ends at the unit's
    /// end, so a unit cannot read into the next one; a fixed-size field that runs past it is
    /// reported, not thrown as a runtime exception.
    /// </summary>
    private static DwarfLineProgram? ReadUnit(
        ReadOnlySpan<byte> unit, int pos, int unitStart, ReadOnlySpan<byte> lineStrData, ReadOnlySpan<byte> strData)
    {
        try
        {
            return ParseUnit(unit, pos, unitStart, lineStrData, strData);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new DwarfFormatException(Section, unitStart,
                $"a field of the line program at 0x{unitStart:X} runs past the end of its unit or header", ex);
        }
    }

    /// <summary>
    /// Parses one unit. <paramref name="unit"/> ends at the unit's end; <paramref name="pos"/> is just
    /// past its unit_length. Returns null for an unsupported version.
    /// </summary>
    private static DwarfLineProgram? ParseUnit(
        ReadOnlySpan<byte> unit, int pos, int unitStart, ReadOnlySpan<byte> lineStrData, ReadOnlySpan<byte> strData)
    {
        int programEnd = unit.Length;
        if (programEnd - pos < 2)
            throw new DwarfFormatException(Section, unitStart, $"the unit at 0x{unitStart:X} is too short to hold its version");
        ushort version = DwarfReaderHelpers.ReadU16LE(unit, pos); pos += 2;
        if (version < 2 || version > 5)
            return null;

        byte addrSize = 4; // wasm32: 4-byte addresses
        if (version >= 5)
        {
            addrSize = unit[pos++];
            _ = unit[pos++]; // segment_selector_size
        }

        int headerLengthPos = pos;
        uint headerLength = DwarfReaderHelpers.ReadU32LE(unit, pos); pos += 4;
        if (headerLength > (uint)(programEnd - pos))
            throw new DwarfFormatException(Section, headerLengthPos,
                $"the unit at 0x{unitStart:X} has header_length {headerLength}, but only {programEnd - pos} bytes of the unit follow it");
        int headerEnd = pos + (int)headerLength;
        var header = unit[..headerEnd];

        byte minInsnLen = header[pos++];
        if (version >= 4)
            _ = header[pos++]; // maximum_operations_per_instruction
        bool defaultIsStmt = header[pos++] != 0;
        int lineBase = (sbyte)header[pos++];
        byte lineRange = header[pos++];
        int opcodeBasePos = pos;
        byte opcodeBase = header[pos++];
        if (opcodeBase == 0)
            throw new DwarfFormatException(Section, opcodeBasePos, $"the unit at 0x{unitStart:X} has opcode_base 0");

        if (opcodeBase - 1 > headerEnd - pos)
            throw new DwarfFormatException(Section, opcodeBasePos,
                $"the unit at 0x{unitStart:X} has {opcodeBase - 1} standard_opcode_lengths, which run past its header");
        var stdOpcodeLengths = header.Slice(pos, opcodeBase - 1);
        pos += opcodeBase - 1;

        List<string> fileNames = version >= 5
            ? ParseDwarf5FilePaths(header, ref pos, lineStrData, strData)
            : ParseDwarf4FilePaths(header, ref pos);
        if (pos > headerEnd)
            throw new DwarfFormatException(Section, headerEnd, $"the file tables of the unit at 0x{unitStart:X} run past its header");

        // headerEnd = start of the line number program
        pos = headerEnd;
        var rows = RunStateMachine(
            unit, ref pos,
            minInsnLen, lineBase, lineRange, opcodeBase,
            stdOpcodeLengths, defaultIsStmt, addrSize, fileNames, version);

        return new DwarfLineProgram(fileNames, rows);
    }

    // ── DWARF 4 header parsing ─────────────────────────────────────────────

    /// <summary>Reads the DWARF 2–4 directory and file tables; <paramref name="header"/> ends at the header's end.</summary>
    private static List<string> ParseDwarf4FilePaths(ReadOnlySpan<byte> header, ref int pos)
    {
        // include_directories: sequence of null-terminated strings, terminated by empty string
        var dirs = new List<string> { "" }; // index 0 = current dir (unused in DWARF4 spec)
        while (header[pos] != 0)
            dirs.Add(ReadNullTerminatedString(header, ref pos));
        pos++; // skip terminating null

        // file_names: name_nts, dir_index_uleb, mtime_uleb, flen_uleb; terminated by empty name
        var files = new List<string> { "" }; // index 0 placeholder; DWARF4 files are 1-based
        while (header[pos] != 0)
        {
            string name = ReadNullTerminatedString(header, ref pos);
            ulong dirIdx = ReadULEB128(header, ref pos, "file directory index");
            ReadULEB128(header, ref pos, "file modification time");
            ReadULEB128(header, ref pos, "file length");

            string dir = dirIdx < (ulong)dirs.Count ? dirs[(int)dirIdx] : "";
            files.Add(dir.Length > 0 ? dir + "/" + name : name);
        }
        pos++; // skip terminating null
        return files;
    }

    // ── DWARF 5 header parsing ─────────────────────────────────────────────

    /// <summary>Reads the DWARF 5 directory and file tables; <paramref name="header"/> ends at the header's end.</summary>
    private static List<string> ParseDwarf5FilePaths(
        ReadOnlySpan<byte> header, ref int pos,
        ReadOnlySpan<byte> lineStrData, ReadOnlySpan<byte> strData)
    {
        // Directories
        var dirFmts = ReadEntryFormats(header, ref pos);
        int dirCount = ReadEntryCount(header, ref pos, dirFmts.Length, "directories");
        var dirs = new List<string>(dirCount);
        for (int i = 0; i < dirCount; i++)
        {
            string dirPath = "";
            foreach (var (ct, form) in dirFmts)
            {
                if (ct == DwLnct.Path)
                    dirPath = ReadStringByForm(header, ref pos, form, lineStrData, strData) ?? "";
                else
                    SkipFormValue(header, ref pos, form);
            }
            dirs.Add(dirPath);
        }

        // File names
        var fileFmts = ReadEntryFormats(header, ref pos);
        int fileCount = ReadEntryCount(header, ref pos, fileFmts.Length, "file names");
        var files = new List<string>(fileCount);
        for (int i = 0; i < fileCount; i++)
        {
            string filePath = "";
            uint dirIdx = 0;
            bool hasDirIdx = false;
            foreach (var (ct, form) in fileFmts)
            {
                if (ct == DwLnct.Path)
                    filePath = ReadStringByForm(header, ref pos, form, lineStrData, strData) ?? "";
                else if (ct == DwLnct.DirectoryIndex)
                {
                    dirIdx = ReadUnsignedByForm(header, ref pos, form);
                    hasDirIdx = true;
                }
                else
                    SkipFormValue(header, ref pos, form);
            }

            if (hasDirIdx && dirIdx < (uint)dirs.Count)
            {
                string dir = dirs[(int)dirIdx];
                filePath = dir.Length > 0 && !filePath.StartsWith('/') ? dir + "/" + filePath : filePath;
            }
            files.Add(filePath);
        }
        return files;
    }

    private static (uint ContentType, uint Form)[] ReadEntryFormats(ReadOnlySpan<byte> header, ref int pos)
    {
        byte count = header[pos++];
        var formats = new (uint ContentType, uint Form)[count];
        for (int i = 0; i < count; i++)
        {
            uint contentType = (uint)ReadULEB128(header, ref pos, "entry content type");
            uint form = (uint)ReadULEB128(header, ref pos, "entry form");
            formats[i] = (contentType, form);
        }
        return formats;
    }

    /// <summary>
    /// Reads a DWARF 5 directory or file count and checks it against the header. Every form a
    /// table entry can use takes at least one byte (<see cref="SkipFormValue"/> refuses the others),
    /// so a count larger than the bytes left in the header cannot be right, and checking it bounds
    /// the loop and the allocation by the header's size.
    /// </summary>
    private static int ReadEntryCount(ReadOnlySpan<byte> header, ref int pos, int formatCount, string table)
    {
        int countPos = pos;
        ulong count = ReadULEB128(header, ref pos, $"{table} count");
        if (count == 0)
            return 0;
        if (formatCount == 0)
            throw new DwarfFormatException(Section, countPos, $"the {table} table at 0x{countPos:X} has {count} entries but no entry format");
        if (count > (ulong)(header.Length - pos))
            throw new DwarfFormatException(Section, countPos,
                $"the {table} table at 0x{countPos:X} has {count} entries, more than the {header.Length - pos} bytes left in its header");
        return (int)count;
    }

    // ── State machine ──────────────────────────────────────────────────────

    /// <summary>
    /// Runs the line program from <paramref name="pos"/> to the end of <paramref name="data"/>, which
    /// ends at the unit's end. Every opcode consumes at least one byte and no length moves the
    /// cursor backwards, so the loop always advances.
    /// </summary>
    private static List<DwarfLineRow> RunStateMachine(
        ReadOnlySpan<byte> data, ref int pos,
        byte minInsnLen, int lineBase, byte lineRange, byte opcodeBase,
        ReadOnlySpan<byte> stdOpcodeLengths, bool defaultIsStmt, byte addrSize,
        List<string> fileNames, int version)
    {
        int programEnd = data.Length;
        uint address = 0;
        uint fileIdx = version >= 5 ? 0u : 1u; // DWARF5: 0-based; DWARF4: 1-based
        int line = 1;
        int column = 0;
        bool isStmt = defaultIsStmt;
        bool prologueEnd = false;

        var rows = new List<DwarfLineRow>();

        void EmitRow(bool isEnd = false)
        {
            string fileName = "";
            if (fileIdx < (uint)fileNames.Count)
                fileName = fileNames[(int)fileIdx];
            rows.Add(new DwarfLineRow(address, fileName, line, column, isStmt, prologueEnd, isEnd));
            prologueEnd = false;
        }

        while (pos < programEnd)
        {
            int opStart = pos;
            byte op = data[pos++];

            if (op >= opcodeBase)
            {
                // Special opcode
                int adj = op - opcodeBase;
                address += (uint)((adj / NonZeroLineRange(lineRange, opStart)) * minInsnLen);
                line += lineBase + (adj % lineRange);
                EmitRow();
            }
            else if (op == 0)
            {
                // Extended opcode
                ulong extLen = ReadULEB128(data, ref pos, "extended opcode length");
                if (extLen == 0) continue;
                if (extLen > (ulong)(programEnd - pos))
                    throw new DwarfFormatException(Section, opStart,
                        $"the extended opcode at 0x{opStart:X} has length {extLen}, but only {programEnd - pos} bytes of its line program follow");
                int extEnd = pos + (int)extLen;
                var ext = data[..extEnd];

                byte extOp = ext[pos++];
                switch (extOp)
                {
                    case DwLne.EndSequence:
                        EmitRow(isEnd: true);
                        address = 0; fileIdx = version >= 5 ? 0u : 1u;
                        line = 1; column = 0; isStmt = defaultIsStmt; prologueEnd = false;
                        break;

                    case DwLne.SetAddress:
                    {
                        int addressBytes = addrSize == 8 ? 8 : 4;
                        if (extEnd - pos < addressBytes)
                            throw new DwarfFormatException(Section, opStart,
                                $"DW_LNE_set_address at 0x{opStart:X} has length {extLen}, too short for a {addressBytes}-byte address");
                        address = addressBytes == 8
                            ? (uint)DwarfReaderHelpers.ReadU64LE(ext, pos)
                            : DwarfReaderHelpers.ReadU32LE(ext, pos);
                        break;
                    }

                    case DwLne.DefineFile: // DWARF4 only
                        if (version < 5)
                        {
                            string name = ReadNullTerminatedString(ext, ref pos);
                            ulong dirIdx = ReadULEB128(ext, ref pos, "DW_LNE_define_file directory index");
                            ReadULEB128(ext, ref pos, "DW_LNE_define_file modification time");
                            ReadULEB128(ext, ref pos, "DW_LNE_define_file length");
                            fileNames.Add(dirIdx < (ulong)fileNames.Count
                                ? fileNames[(int)dirIdx] + "/" + name : name);
                        }
                        break;

                    case DwLne.SetDiscriminator:
                        ReadULEB128(ext, ref pos, "DW_LNE_set_discriminator operand");
                        break;

                    default:
                        break; // unknown extended op: skipped by its length below
                }

                pos = extEnd;
            }
            else
            {
                // Standard opcode
                switch (op)
                {
                    case DwLns.Copy:
                        EmitRow();
                        break;

                    case DwLns.AdvancePc:
                        address += (uint)ReadULEB128(data, ref pos, "DW_LNS_advance_pc operand") * minInsnLen;
                        break;

                    case DwLns.AdvanceLine:
                        line += (int)DwarfReaderHelpers.ReadSLEB128Checked(data, ref pos, Section, "DW_LNS_advance_line operand");
                        break;

                    case DwLns.SetFile:
                        fileIdx = (uint)ReadULEB128(data, ref pos, "DW_LNS_set_file operand");
                        break;

                    case DwLns.SetColumn:
                        column = (int)(uint)ReadULEB128(data, ref pos, "DW_LNS_set_column operand");
                        break;

                    case DwLns.NegateStmt:
                        isStmt = !isStmt;
                        break;

                    case DwLns.SetBasicBlock:
                        break;

                    case DwLns.ConstAddPc:
                        address += (uint)(((255 - opcodeBase) / NonZeroLineRange(lineRange, opStart)) * minInsnLen);
                        break;

                    case DwLns.FixedAdvancePc:
                        if (programEnd - pos < 2)
                            throw new DwarfFormatException(Section, opStart,
                                $"DW_LNS_fixed_advance_pc at 0x{opStart:X} needs a 2-byte operand, but its line program ends");
                        address += DwarfReaderHelpers.ReadU16LE(data, pos); pos += 2;
                        break;

                    case DwLns.SetPrologueEnd:
                        prologueEnd = true;
                        break;

                    case DwLns.SetEpilogueBegin:
                        break; // no state needed

                    case DwLns.SetIsa:
                        ReadULEB128(data, ref pos, "DW_LNS_set_isa operand");
                        break;

                    default:
                        // Unknown standard opcode: skip operands per stdOpcodeLengths
                        int opsIdx = op - 1;
                        int nOps = opsIdx < stdOpcodeLengths.Length ? stdOpcodeLengths[opsIdx] : 0;
                        for (int i = 0; i < nOps; i++)
                            ReadULEB128(data, ref pos, "standard opcode operand");
                        break;
                }
            }
        }

        return rows;
    }

    private static int NonZeroLineRange(byte lineRange, int opStart) => lineRange != 0
        ? lineRange
        : throw new DwarfFormatException(Section, opStart, $"the opcode at 0x{opStart:X} needs line_range, which is 0");

    private static ulong ReadULEB128(ReadOnlySpan<byte> data, ref int pos, string what) =>
        DwarfReaderHelpers.ReadULEB128Checked(data, ref pos, Section, what);

    /// <summary>Reads a string that must be terminated within <paramref name="data"/> (its header or opcode).</summary>
    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data, ref int pos)
    {
        int length = data[pos..].IndexOf((byte)0);
        if (length < 0)
            throw new DwarfFormatException(Section, pos, $"the string at 0x{pos:X} is not terminated before the end of its header or opcode");
        var s = Encoding.UTF8.GetString(data.Slice(pos, length));
        pos += length + 1;
        return s;
    }

    private static string? ReadStringByForm(
        ReadOnlySpan<byte> data, ref int pos, uint form,
        ReadOnlySpan<byte> lineStrData, ReadOnlySpan<byte> strData)
    {
        switch ((DwForm)form)
        {
            case DwForm.String:
                return ReadNullTerminatedString(data, ref pos);
            case DwForm.Strp:
            case DwForm.LineStrp:
                var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, default, 0);
                return DwarfReaderHelpers.TryReadString(data, ref pos, form, HeaderFormat, ctx, out var value, out _)
                    ? Encoding.UTF8.GetString(value)
                    : null;
            default:
                SkipFormValue(data, ref pos, form);
                return null;
        }
    }

    /// <summary>
    /// Line table headers are 32-bit DWARF (64-bit units are skipped) with wasm32's 4-byte addresses.
    /// </summary>
    private static readonly DwarfFormContext HeaderFormat = DwarfFormContext.Dwarf32(addressSize: 4, version: 5);

    private static uint ReadUnsignedByForm(ReadOnlySpan<byte> data, ref int pos, uint form)
    {
        switch ((DwForm)form)
        {
            case DwForm.Data1: return data[pos++];
            case DwForm.Data2: { var v = DwarfReaderHelpers.ReadU16LE(data, pos); pos += 2; return v; }
            case DwForm.Data4: { var v = DwarfReaderHelpers.ReadU32LE(data, pos); pos += 4; return v; }
            case DwForm.Udata: return (uint)ReadULEB128(data, ref pos, "DW_FORM_udata");
            default: SkipFormValue(data, ref pos, form); return 0;
        }
    }

    /// <summary>
    /// Skips a directory or file entry field. Only the forms DWARF 5 allows there are accepted; each
    /// takes at least one byte, which <see cref="ReadEntryCount"/> relies on. Any other form cannot be
    /// sized, so it is refused rather than skipped as zero bytes. Fixed-size widths come from the shared
    /// form table; variable-size fields must be complete.
    /// </summary>
    private static void SkipFormValue(ReadOnlySpan<byte> data, ref int pos, uint form)
    {
        switch ((DwForm)form)
        {
            case DwForm.String: ReadNullTerminatedString(data, ref pos); break;
            case DwForm.Block:
            {
                ulong length = ReadULEB128(data, ref pos, "DW_FORM_block length");
                pos += DwarfReaderHelpers.CheckedBlockLength(data, pos, length, Section);
                break;
            }
            case DwForm.Udata: ReadULEB128(data, ref pos, "DW_FORM_udata"); break;
            case DwForm.Strx: ReadULEB128(data, ref pos, "DW_FORM_strx"); break;
            case DwForm.Addrx: ReadULEB128(data, ref pos, "DW_FORM_addrx"); break;
            case DwForm.Addr:
            case DwForm.Data1:
            case DwForm.Data2:
            case DwForm.Data4:
            case DwForm.Data8:
            case DwForm.Data16:
            case DwForm.Strp:
            case DwForm.StrpSup:
            case DwForm.LineStrp:
            case DwForm.Strx1:
            case DwForm.Strx2:
            case DwForm.Strx4:
            case DwForm.Addrx1:
            case DwForm.Addrx2:
            case DwForm.Addrx4:
                pos += DwarfReaderHelpers.FixedFormSize(form, HeaderFormat);
                break;
            default:
                throw new DwarfFormatException(Section, pos, $"form 0x{form:X} at 0x{pos:X} is not valid in a line table header");
        }
    }
}
