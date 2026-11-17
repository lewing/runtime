// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Collections.Generic;
using System.Text;

/// <summary>One source-line entry to encode into a DWARF .debug_line section.</summary>
public readonly record struct DwarfLineEntry(
    uint Address,
    string FileName,
    int Line,
    int Column,
    bool IsStatement,
    bool PrologueEnd);

/// <summary>
/// Emits a DWARF v4 32-bit .debug_line section from source line entries.
/// The output can be round-tripped through <see cref="DwarfLineReader.Parse"/>.
/// </summary>
public static class DwarfLineWriter
{
    // Header constants
    private const int LineBase = -5;
    private const int LineRange = 14;
    private const byte OpcodeBase = 13;
    private const bool DefaultIsStmt = true;

    // Standard opcode lengths for opcodes 1–12 (DWARF spec §6.2.5.3)
    private static readonly byte[] StdOpcodeLengths =
        [0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1];

    // DW_LNS standard opcodes used in output
    private const byte DW_LNS_copy             = 1;
    private const byte DW_LNS_advance_pc       = 2;
    private const byte DW_LNS_advance_line     = 3;
    private const byte DW_LNS_set_file         = 4;
    private const byte DW_LNS_set_column       = 5;
    private const byte DW_LNS_negate_stmt      = 6;
    private const byte DW_LNS_set_prologue_end = 10;

    // DW_LNE extended opcodes
    private const byte DW_LNE_end_sequence = 1;
    private const byte DW_LNE_set_address  = 2;

    /// <summary>
    /// Emits a DWARF v4 .debug_line section from the given source line entries.
    /// The output can be round-tripped through <see cref="DwarfLineReader.Parse"/>.
    /// </summary>
    public static byte[] Emit(IReadOnlyList<DwarfLineEntry> entries)
    {
        // Build 1-based file name table; index 0 is the placeholder empty string.
        var fileToIndex = new Dictionary<string, uint>(StringComparer.Ordinal);
        var fileNames = new List<string> { "" }; // index 0 = placeholder (DWARF4 1-based)
        foreach (var entry in entries)
        {
            if (!fileToIndex.ContainsKey(entry.FileName))
            {
                fileToIndex[entry.FileName] = (uint)fileNames.Count;
                fileNames.Add(entry.FileName);
            }
        }

        // Build header body (the bytes counted by header_length)
        var headerBody = new List<byte>();
        headerBody.Add(1);                          // minimum_instruction_length
        headerBody.Add(1);                          // maximum_operations_per_instruction (DWARF4)
        headerBody.Add(DefaultIsStmt ? (byte)1 : (byte)0); // default_is_stmt
        headerBody.Add(unchecked((byte)LineBase));  // line_base = -5
        headerBody.Add(LineRange);                  // line_range = 14
        headerBody.Add(OpcodeBase);                 // opcode_base = 13
        headerBody.AddRange(StdOpcodeLengths);      // 12 bytes for opcodes 1–12

        // include_directories: null byte (no directories)
        headerBody.Add(0);

        // file_names table: one entry per unique file, terminated by a null byte
        for (int i = 1; i < fileNames.Count; i++)
        {
            headerBody.AddRange(Encoding.UTF8.GetBytes(fileNames[i]));
            headerBody.Add(0);              // null-terminated name
            WriteUleb128(headerBody, 0);    // dir_index
            WriteUleb128(headerBody, 0);    // mtime
            WriteUleb128(headerBody, 0);    // size
        }
        headerBody.Add(0); // file_names terminator

        // Build line number program
        var program = new List<byte>();
        EmitLineProgram(program, entries, fileToIndex);

        // Assemble final section:
        //   unit_length (4)  — bytes after this field
        //   version     (2)
        //   header_length(4) — bytes after this field to start of program
        //   headerBody
        //   program
        uint headerLength = (uint)headerBody.Count;
        // version(2) + header_length(4) + headerBody + program
        uint unitLength = 2u + 4u + headerLength + (uint)program.Count;

        var section = new List<byte>(4 + 2 + 4 + headerBody.Count + program.Count);
        WriteU32LE(section, unitLength);
        WriteU16LE(section, 4);              // DWARF version 4
        WriteU32LE(section, headerLength);
        section.AddRange(headerBody);
        section.AddRange(program);

        return section.ToArray();
    }

    private static void EmitLineProgram(
        List<byte> buf,
        IReadOnlyList<DwarfLineEntry> entries,
        Dictionary<string, uint> fileToIndex)
    {
        // State machine initial state (matches DwarfLineReader's reset)
        uint curAddress = 0;
        uint curFileIdx = 1;   // DWARF4 starts at file index 1
        int  curLine    = 1;
        int  curColumn  = 0;
        bool curIsStmt  = DefaultIsStmt;
        bool firstRow   = true;

        foreach (var entry in entries)
        {
            // Emit is_stmt toggle if needed
            if (entry.IsStatement != curIsStmt)
            {
                buf.Add(DW_LNS_negate_stmt);
                curIsStmt = !curIsStmt;
            }

            // Emit file change
            uint fileIdx = fileToIndex[entry.FileName];
            if (fileIdx != curFileIdx)
            {
                buf.Add(DW_LNS_set_file);
                WriteUleb128(buf, fileIdx);
                curFileIdx = fileIdx;
            }

            // Emit column change
            if (entry.Column != curColumn)
            {
                buf.Add(DW_LNS_set_column);
                WriteUleb128(buf, (uint)entry.Column);
                curColumn = entry.Column;
            }

            // Emit prologue_end marker
            if (entry.PrologueEnd)
                buf.Add(DW_LNS_set_prologue_end);

            // Resolve address delta — use DW_LNE_set_address for the first row
            // or whenever the address moves backward (non-monotonic).
            uint addrDelta;
            if (firstRow || entry.Address < curAddress)
            {
                EmitSetAddress(buf, entry.Address);
                curAddress = entry.Address;
                addrDelta  = 0;
                firstRow   = false;
            }
            else
            {
                addrDelta = entry.Address - curAddress;
            }

            int lineDelta = entry.Line - curLine;

            // Try a special opcode that simultaneously advances address and line.
            if (lineDelta >= LineBase && lineDelta < LineBase + LineRange)
            {
                int special = (lineDelta - LineBase) + (LineRange * (int)addrDelta) + OpcodeBase;
                if (special >= OpcodeBase && special <= 255)
                {
                    buf.Add((byte)special);
                    curAddress = entry.Address;
                    curLine    = entry.Line;
                    continue;
                }
            }

            // Special opcode doesn't fit; advance the PC separately first.
            if (addrDelta > 0)
            {
                buf.Add(DW_LNS_advance_pc);
                WriteUleb128(buf, addrDelta);
                curAddress = entry.Address;
            }

            // Retry special with zero address advance.
            if (lineDelta >= LineBase && lineDelta < LineBase + LineRange)
            {
                int special = (lineDelta - LineBase) + OpcodeBase; // addrDelta == 0
                buf.Add((byte)special);
                curLine = entry.Line;
            }
            else
            {
                // Line delta is out of special-opcode range; use standard opcodes.
                buf.Add(DW_LNS_advance_line);
                WriteSleb128(buf, lineDelta);
                curLine = entry.Line;
                buf.Add(DW_LNS_copy);
            }
        }

        // End the sequence.
        buf.Add(0);                        // extended opcode marker
        WriteUleb128(buf, 1);              // operand length = 1
        buf.Add(DW_LNE_end_sequence);
    }

    /// Emits DW_LNE_set_address for a 4-byte (wasm32) address.
    private static void EmitSetAddress(List<byte> buf, uint address)
    {
        buf.Add(0);                        // extended opcode marker
        WriteUleb128(buf, 5);              // length: 1 (extOp) + 4 (address)
        buf.Add(DW_LNE_set_address);
        WriteU32LE(buf, address);
    }

    // ── LEB128 helpers ────────────────────────────────────────────────────────

    private static void WriteUleb128(List<byte> buf, uint value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            buf.Add(b);
        } while (value != 0);
    }

    private static void WriteSleb128(List<byte> buf, int value)
    {
        bool more;
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            more = !((value == 0 && (b & 0x40) == 0) || (value == -1 && (b & 0x40) != 0));
            if (more) b |= 0x80;
            buf.Add(b);
        } while (more);
    }

    // ── Little-endian write helpers ───────────────────────────────────────────

    private static void WriteU32LE(List<byte> buf, uint value)
    {
        buf.Add((byte) (value        & 0xFF));
        buf.Add((byte)((value >>  8) & 0xFF));
        buf.Add((byte)((value >> 16) & 0xFF));
        buf.Add((byte)((value >> 24) & 0xFF));
    }

    private static void WriteU16LE(List<byte> buf, ushort value)
    {
        buf.Add((byte) (value       & 0xFF));
        buf.Add((byte)((value >> 8) & 0xFF));
    }
}
