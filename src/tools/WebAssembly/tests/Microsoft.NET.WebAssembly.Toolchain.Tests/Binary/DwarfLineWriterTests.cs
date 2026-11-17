// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

// ────────────────────────────────────────────────────────────────────────────
// DwarfLineWriterTests.cs
//
// Round-trip tests for DWARF .debug_line emission.
//
// Each test:
//   1. Builds a list of DwarfLineEntry values
//   2. Calls DwarfLineWriter.Emit() to produce raw .debug_line bytes
//   3. Calls DwarfLineReader.Parse() to read them back
//   4. Asserts the round-tripped rows match the original entries
//
// These tests depend on:
//   • DwarfLineWriter / DwarfLineEntry — Morpheus
//   • WatCompileOptions.EmitDebugInfo  — Trinity (integration test only)
//
// The file compiles against those types; individual tests are expected to fail
// until the dependent code lands.
// ────────────────────────────────────────────────────────────────────────────

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Wat;

public class DwarfLineWriterTests
{
    // ── helpers ───────────────────────────────────────────────────────────────

    /// <summary>
    /// Returns only the non-end-sequence rows, mirroring how callers typically
    /// consume parsed DWARF line info.
    /// </summary>
    private static IReadOnlyList<DwarfLineRow> DataRows(DwarfLineProgram program) =>
        program.Rows.Where(r => !r.EndSequence).ToList();

    private static void AssertRow(
        DwarfLineRow row,
        uint address,
        string fileName,
        int line,
        int column,
        bool isStatement,
        bool prologueEnd)
    {
        Assert.Equal(address,    row.Address);
        Assert.Equal(fileName,   row.FileName);
        Assert.Equal(line,       row.Line);
        Assert.Equal(column,     row.Column);
        Assert.Equal(isStatement, row.IsStatement);
        Assert.Equal(prologueEnd, row.PrologueEnd);
    }

    // ── 1. SingleFile_SingleFunction ──────────────────────────────────────────

    [Fact]
    public void SingleFile_SingleFunction()
    {
        var entries = new[]
        {
            new DwarfLineEntry(Address: 0, FileName: "test.wat", Line: 1, Column: 0,
                               IsStatement: true, PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        var row = Assert.Single(rows);
        AssertRow(row, 0, "test.wat", 1, 0, isStatement: true, prologueEnd: false);
    }

    // ── 2. SingleFile_MultipleFunctions ──────────────────────────────────────

    [Fact]
    public void SingleFile_MultipleFunctions()
    {
        var entries = new[]
        {
            new DwarfLineEntry(0,   "add.wat", 1, 0, IsStatement: true,  PrologueEnd: false),
            new DwarfLineEntry(4,   "add.wat", 2, 0, IsStatement: true,  PrologueEnd: false),
            new DwarfLineEntry(8,   "add.wat", 3, 0, IsStatement: true,  PrologueEnd: false),
            new DwarfLineEntry(12,  "add.wat", 4, 0, IsStatement: false, PrologueEnd: false),
            new DwarfLineEntry(20,  "add.wat", 5, 0, IsStatement: true,  PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        Assert.Equal(entries.Length, rows.Count);

        uint[] expectedAddresses = [0, 4, 8, 12, 20];
        int[]  expectedLines     = [1, 2, 3, 4, 5];

        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(expectedAddresses[i], rows[i].Address);
            Assert.Equal(expectedLines[i],     rows[i].Line);
            Assert.Equal("add.wat",            rows[i].FileName);
        }
    }

    // ── 3. MultipleFiles ─────────────────────────────────────────────────────

    [Fact]
    public void MultipleFiles()
    {
        var entries = new[]
        {
            new DwarfLineEntry(0,  "a.wat", 1, 0, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(4,  "a.wat", 2, 0, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(8,  "b.wat", 1, 0, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(12, "b.wat", 3, 0, IsStatement: true, PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        Assert.Equal(4, rows.Count);

        Assert.Equal("a.wat", rows[0].FileName);
        Assert.Equal(1,       rows[0].Line);

        Assert.Equal("a.wat", rows[1].FileName);
        Assert.Equal(2,       rows[1].Line);

        Assert.Equal("b.wat", rows[2].FileName);
        Assert.Equal(1,       rows[2].Line);

        Assert.Equal("b.wat", rows[3].FileName);
        Assert.Equal(3,       rows[3].Line);
    }

    // ── 4. PrologueEnd_And_IsStatement_Flags ─────────────────────────────────

    [Fact]
    public void PrologueEnd_And_IsStatement_Flags()
    {
        var entries = new[]
        {
            // PrologueEnd=true marks the first real statement after the prologue
            new DwarfLineEntry(0,  "flags.wat", 1, 0, IsStatement: true,  PrologueEnd: true),
            new DwarfLineEntry(4,  "flags.wat", 2, 0, IsStatement: true,  PrologueEnd: false),
            new DwarfLineEntry(8,  "flags.wat", 3, 0, IsStatement: false, PrologueEnd: false),
            new DwarfLineEntry(12, "flags.wat", 4, 0, IsStatement: false, PrologueEnd: true),
            new DwarfLineEntry(16, "flags.wat", 5, 0, IsStatement: true,  PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        Assert.Equal(entries.Length, rows.Count);

        for (int i = 0; i < entries.Length; i++)
        {
            Assert.Equal(entries[i].IsStatement,  rows[i].IsStatement);
            Assert.Equal(entries[i].PrologueEnd,  rows[i].PrologueEnd);
        }
    }

    // ── 5. ColumnInfo ─────────────────────────────────────────────────────────

    [Fact]
    public void ColumnInfo()
    {
        var entries = new[]
        {
            new DwarfLineEntry(0,  "col.wat", 1, 0,  IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(4,  "col.wat", 2, 5,  IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(8,  "col.wat", 3, 10, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(12, "col.wat", 4, 42, IsStatement: true, PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        Assert.Equal(entries.Length, rows.Count);

        int[] expectedColumns = [0, 5, 10, 42];
        for (int i = 0; i < entries.Length; i++)
            Assert.Equal(expectedColumns[i], rows[i].Column);
    }

    // ── 6. LargeAddressGaps ───────────────────────────────────────────────────

    /// <summary>
    /// Exercises DW_LNS_advance_pc and DW_LNE_set_address with address jumps
    /// that exceed what a single special opcode can encode.
    /// </summary>
    [Fact]
    public void LargeAddressGaps()
    {
        var entries = new[]
        {
            new DwarfLineEntry(0,     "gaps.wat", 1,  0, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(1000,  "gaps.wat", 10, 0, IsStatement: true, PrologueEnd: false),
            new DwarfLineEntry(50000, "gaps.wat", 50, 0, IsStatement: true, PrologueEnd: false),
        };

        var bytes    = DwarfLineWriter.Emit(entries);
        var programs = DwarfLineReader.Parse(bytes);

        var prog = Assert.Single(programs);
        var rows = DataRows(prog);

        Assert.Equal(3, rows.Count);

        Assert.Equal(0u,     rows[0].Address);
        Assert.Equal(1,      rows[0].Line);

        Assert.Equal(1000u,  rows[1].Address);
        Assert.Equal(10,     rows[1].Line);

        Assert.Equal(50000u, rows[2].Address);
        Assert.Equal(50,     rows[2].Line);
    }

    // ── 7. WatCompile_EmitsDebugLine (integration) ───────────────────────────

    /// <summary>
    /// Compiles a WAT module with debug info enabled (Trinity's encoder changes),
    /// verifies a .debug_line custom section is present, and confirms that
    /// DwarfLineReader can parse at least one entry from it.
    ///
    /// This test depends on Trinity's WatCompileOptions.EmitDebugInfo support
    /// landing in WasmEncoder.
    /// </summary>
    [Fact]
    public void WatCompile_EmitsDebugLine()
    {
        const string wat = """
            (module
              (func $add (export "add") (param i32 i32) (result i32)
                local.get 0
                local.get 1
                i32.add))
            """;

        var bytes  = WatCompiler.Compile(wat, emitDebugInfo: true, sourceFile: "add.wat");
        var module = Microsoft.NET.WebAssembly.Binary.ModuleReader.ReadModule(new MemoryStream(bytes));

        var debugLineSection = module.CustomSections
            .FirstOrDefault(s => s.Name == ".debug_line");

        Assert.NotNull(debugLineSection);

        var programs = DwarfLineReader.Parse(debugLineSection.Content);
        Assert.NotEmpty(programs);

        var allRows = programs.SelectMany(p => p.Rows).ToList();
        Assert.NotEmpty(allRows);
    }
}
