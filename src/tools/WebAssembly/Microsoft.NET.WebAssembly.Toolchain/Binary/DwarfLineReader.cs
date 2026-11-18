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
/// Parses DWARF .debug_line sections to produce source line ↔ code address tables.
/// Supports DWARF versions 2–5 in the 32-bit DWARF format used by wasm32.
/// </summary>
public static class DwarfLineReader
{
    /// <summary>
    /// Parses all line number programs from <paramref name="data"/> (.debug_line content).
    /// Returns one <see cref="DwarfLineProgram"/> per compilation unit header found.
    /// </summary>
    public static List<DwarfLineProgram> Parse(
        ReadOnlySpan<byte> data,
        byte[]? lineStrData = null,
        byte[]? strData = null)
    {
        var programs = new List<DwarfLineProgram>();
        int pos = 0;

        while (pos + 6 <= data.Length)
        {
            uint unitLen = ReadU32LE(data, pos); pos += 4;
            if (unitLen == 0xFFFF_FFFF) break; // 64-bit DWARF not supported

            int programEnd = pos + (int)unitLen;
            if (programEnd > data.Length) break;

            ushort version = ReadU16LE(data, pos); pos += 2;
            if (version < 2 || version > 5) { pos = programEnd; continue; }

            byte addrSize = 4; // wasm32: 4-byte addresses
            if (version >= 5)
            {
                addrSize = data[pos++];
                _ = data[pos++]; // segment_selector_size
            }

            uint headerLength = ReadU32LE(data, pos); pos += 4;
            int headerEnd = pos + (int)headerLength;

            byte minInsnLen = data[pos++];
            if (version >= 4)
                _ = data[pos++]; // maximum_operations_per_instruction
            bool defaultIsStmt = data[pos++] != 0;
            int lineBase = (sbyte)data[pos++];
            byte lineRange = data[pos++];
            byte opcodeBase = data[pos++];

            // Skip standard opcode lengths (opcodeBase - 1 bytes)
            var stdOpcodeLengths = new byte[opcodeBase - 1];
            for (int i = 0; i < opcodeBase - 1 && pos < headerEnd; i++)
                stdOpcodeLengths[i] = data[pos++];

            List<string> fileNames;
            if (version >= 5)
                fileNames = ParseDwarf5FilePaths(data, ref pos, lineStrData, strData);
            else
                fileNames = ParseDwarf4FilePaths(data, ref pos, headerEnd);

            // headerEnd = start of the line number program
            pos = headerEnd;
            var rows = RunStateMachine(
                data, ref pos, programEnd,
                minInsnLen, lineBase, lineRange, opcodeBase,
                stdOpcodeLengths, defaultIsStmt, addrSize, fileNames, version);

            programs.Add(new DwarfLineProgram(fileNames, rows));
            pos = programEnd;
        }

        return programs;
    }

    // ── DWARF 4 header parsing ─────────────────────────────────────────────

    private static List<string> ParseDwarf4FilePaths(ReadOnlySpan<byte> data, ref int pos, int headerEnd)
    {
        // include_directories: sequence of null-terminated strings, terminated by empty string
        var dirs = new List<string> { "" }; // index 0 = current dir (unused in DWARF4 spec)
        while (pos < headerEnd && data[pos] != 0)
        {
            dirs.Add(ReadNullTerminatedString(data, ref pos));
        }
        if (pos < headerEnd) pos++; // skip terminating null

        // file_names: name_nts, dir_index_uleb, mtime_uleb, flen_uleb; terminated by empty name
        var files = new List<string> { "" }; // index 0 placeholder; DWARF4 files are 1-based
        while (pos < headerEnd && data[pos] != 0)
        {
            string name = ReadNullTerminatedString(data, ref pos);
            uint dirIdx = DwarfReaderHelpers.ReadULEB128(data, ref pos);
            DwarfReaderHelpers.ReadULEB128(data, ref pos); // last modified time
            DwarfReaderHelpers.ReadULEB128(data, ref pos); // file length

            string dir = dirIdx < (uint)dirs.Count ? dirs[(int)dirIdx] : "";
            files.Add(dir.Length > 0 ? dir + "/" + name : name);
        }
        if (pos < headerEnd) pos++; // skip terminating null
        return files;
    }

    // ── DWARF 5 header parsing ─────────────────────────────────────────────

    private static List<string> ParseDwarf5FilePaths(
        ReadOnlySpan<byte> data, ref int pos,
        byte[]? lineStrData, byte[]? strData)
    {
        // Directories
        byte dirFmtCount = data[pos++];
        var dirFmts = new (uint ContentType, uint Form)[dirFmtCount];
        for (int i = 0; i < dirFmtCount; i++)
            dirFmts[i] = (DwarfReaderHelpers.ReadULEB128(data, ref pos), DwarfReaderHelpers.ReadULEB128(data, ref pos));

        uint dirCount = DwarfReaderHelpers.ReadULEB128(data, ref pos);
        var dirs = new List<string>((int)dirCount);
        for (uint i = 0; i < dirCount; i++)
        {
            string dirPath = "";
            foreach (var (ct, form) in dirFmts)
            {
                if (ct == DwLnct.Path)
                    dirPath = ReadStringByForm(data, ref pos, form, lineStrData, strData) ?? "";
                else
                    SkipFormValue(data, ref pos, form, addrSize: 4);
            }
            dirs.Add(dirPath);
        }

        // File names
        byte fileFmtCount = data[pos++];
        var fileFmts = new (uint ContentType, uint Form)[fileFmtCount];
        for (int i = 0; i < fileFmtCount; i++)
            fileFmts[i] = (DwarfReaderHelpers.ReadULEB128(data, ref pos), DwarfReaderHelpers.ReadULEB128(data, ref pos));

        uint fileCount = DwarfReaderHelpers.ReadULEB128(data, ref pos);
        var files = new List<string>((int)fileCount);
        for (uint i = 0; i < fileCount; i++)
        {
            string filePath = "";
            uint dirIdx = 0;
            bool hasDirIdx = false;
            foreach (var (ct, form) in fileFmts)
            {
                if (ct == DwLnct.Path)
                    filePath = ReadStringByForm(data, ref pos, form, lineStrData, strData) ?? "";
                else if (ct == DwLnct.DirectoryIndex)
                {
                    dirIdx = ReadUnsignedByForm(data, ref pos, form);
                    hasDirIdx = true;
                }
                else
                    SkipFormValue(data, ref pos, form, addrSize: 4);
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

    // ── State machine ──────────────────────────────────────────────────────

    private static List<DwarfLineRow> RunStateMachine(
        ReadOnlySpan<byte> data, ref int pos, int programEnd,
        byte minInsnLen, int lineBase, byte lineRange, byte opcodeBase,
        byte[] stdOpcodeLengths, bool defaultIsStmt, byte addrSize,
        List<string> fileNames, int version)
    {
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
            byte op = data[pos++];

            if (op >= opcodeBase)
            {
                // Special opcode
                int adj = op - opcodeBase;
                address += (uint)((adj / lineRange) * minInsnLen);
                line += lineBase + (adj % lineRange);
                EmitRow();
            }
            else if (op == 0)
            {
                // Extended opcode
                int extLen = (int)DwarfReaderHelpers.ReadULEB128(data, ref pos);
                int extEnd = pos + extLen;
                if (extLen == 0 || extEnd > programEnd) { pos = extEnd; continue; }

                byte extOp = data[pos++];
                switch (extOp)
                {
                    case DwLne.EndSequence:
                        EmitRow(isEnd: true);
                        address = 0; fileIdx = version >= 5 ? 0u : 1u;
                        line = 1; column = 0; isStmt = defaultIsStmt; prologueEnd = false;
                        break;

                    case DwLne.SetAddress:
                        address = addrSize == 8
                            ? (uint)ReadU64LE(data, pos)
                            : ReadU32LE(data, pos);
                        pos += addrSize;
                        break;

                    case DwLne.DefineFile: // DWARF4 only
                        if (version < 5)
                        {
                            string name = ReadNullTerminatedString(data, ref pos);
                            uint dirIdx = DwarfReaderHelpers.ReadULEB128(data, ref pos);
                            DwarfReaderHelpers.ReadULEB128(data, ref pos); // mtime
                            DwarfReaderHelpers.ReadULEB128(data, ref pos); // size
                            fileNames.Add(dirIdx < (uint)fileNames.Count
                                ? fileNames[(int)dirIdx] + "/" + name : name);
                        }
                        break;

                    case DwLne.SetDiscriminator:
                        DwarfReaderHelpers.ReadULEB128(data, ref pos);
                        break;

                    default:
                        pos = extEnd; // skip unknown extended op
                        break;
                }
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
                        address += DwarfReaderHelpers.ReadULEB128(data, ref pos) * minInsnLen;
                        break;

                    case DwLns.AdvanceLine:
                        line += (int)DwarfReaderHelpers.ReadSLEB128(data, ref pos);
                        break;

                    case DwLns.SetFile:
                        fileIdx = DwarfReaderHelpers.ReadULEB128(data, ref pos);
                        break;

                    case DwLns.SetColumn:
                        column = (int)DwarfReaderHelpers.ReadULEB128(data, ref pos);
                        break;

                    case DwLns.NegateStmt:
                        isStmt = !isStmt;
                        break;

                    case DwLns.SetBasicBlock:
                        break;

                    case DwLns.ConstAddPc:
                        address += (uint)(((255 - opcodeBase) / lineRange) * minInsnLen);
                        break;

                    case DwLns.FixedAdvancePc:
                        address += ReadU16LE(data, pos); pos += 2;
                        break;

                    case DwLns.SetPrologueEnd:
                        prologueEnd = true;
                        break;

                    case DwLns.SetEpilogueBegin:
                        break; // no state needed

                    case DwLns.SetIsa:
                        DwarfReaderHelpers.ReadULEB128(data, ref pos);
                        break;

                    default:
                        // Unknown standard opcode: skip operands per stdOpcodeLengths
                        int opsIdx = op - 1;
                        int nOps = opsIdx < stdOpcodeLengths.Length ? stdOpcodeLengths[opsIdx] : 0;
                        for (int i = 0; i < nOps; i++)
                            DwarfReaderHelpers.ReadULEB128(data, ref pos);
                        break;
                }
            }
        }

        return rows;
    }

    // ── Binary helpers ─────────────────────────────────────────────────────

    private static uint ReadU32LE(ReadOnlySpan<byte> data, int pos) =>
        (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));

    private static ushort ReadU16LE(ReadOnlySpan<byte> data, int pos) =>
        (ushort)(data[pos] | (data[pos + 1] << 8));

    private static ulong ReadU64LE(ReadOnlySpan<byte> data, int pos) =>
        (ulong)ReadU32LE(data, pos) | ((ulong)ReadU32LE(data, pos + 4) << 32);

    private static string ReadNullTerminatedString(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        var s = Encoding.UTF8.GetString(data.Slice(start, pos - start));
        if (pos < data.Length) pos++; // skip null
        return s;
    }

    private static string? ReadStringByForm(
        ReadOnlySpan<byte> data, ref int pos, uint form,
        byte[]? lineStrData, byte[]? strData)
    {
        switch ((DwForm)form)
        {
            case DwForm.String:
                return ReadNullTerminatedString(data, ref pos);

            case DwForm.Strp:
            {
                uint offset = ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(strData, (int)offset);
            }

            case DwForm.LineStrp:
            {
                uint offset = ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(lineStrData, (int)offset);
            }

            default:
                SkipFormValue(data, ref pos, form, addrSize: 4);
                return null;
        }
    }

    private static string? ReadStringFromSection(byte[]? section, int offset)
    {
        if (section is null || offset < 0 || offset >= section.Length) return null;
        int end = offset;
        while (end < section.Length && section[end] != 0) end++;
        return Encoding.UTF8.GetString(section, offset, end - offset);
    }

    private static uint ReadUnsignedByForm(ReadOnlySpan<byte> data, ref int pos, uint form)
    {
        switch ((DwForm)form)
        {
            case DwForm.Data1: return data[pos++];
            case DwForm.Data2: { var v = ReadU16LE(data, pos); pos += 2; return v; }
            case DwForm.Data4: { var v = ReadU32LE(data, pos); pos += 4; return v; }
            case DwForm.Udata: return DwarfReaderHelpers.ReadULEB128(data, ref pos);
            default: SkipFormValue(data, ref pos, form, addrSize: 4); return 0;
        }
    }

    private static void SkipFormValue(ReadOnlySpan<byte> data, ref int pos, uint form, byte addrSize)
    {
        switch ((DwForm)form)
        {
            case DwForm.Addr: pos += addrSize; break;
            case DwForm.Data2: pos += 2; break;
            case DwForm.Data4: pos += 4; break;
            case DwForm.Data8: pos += 8; break;
            case DwForm.String: while (pos < data.Length && data[pos] != 0) pos++; if (pos < data.Length) pos++; break;
            case DwForm.Block: { int length = (int)DwarfReaderHelpers.ReadULEB128(data, ref pos); pos += length; break; }
            case DwForm.Data1: pos += 1; break;
            case DwForm.Strp: pos += 4; break;
            case DwForm.Udata: DwarfReaderHelpers.ReadULEB128(data, ref pos); break;
            case DwForm.Strx: DwarfReaderHelpers.ReadULEB128(data, ref pos); break;
            case DwForm.Addrx: DwarfReaderHelpers.ReadULEB128(data, ref pos); break;
            case DwForm.Data16: pos += 16; break;
            case DwForm.LineStrp: pos += 4; break;
            case DwForm.Strx1: pos += 1; break;
            case DwForm.Strx2: pos += 2; break;
            case DwForm.Strx4: pos += 4; break;
            case DwForm.Addrx1: pos += 1; break;
            case DwForm.Addrx2: pos += 2; break;
            case DwForm.Addrx4: pos += 4; break;
            default: break;                                  // unknown: stop
        }
    }
}
