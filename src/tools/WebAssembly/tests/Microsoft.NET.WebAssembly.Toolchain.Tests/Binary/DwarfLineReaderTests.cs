// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Adversarial .debug_line inputs (#600 H1, M5b). Every length and count in a line program is
/// attacker-chosen: the reader must reject a bad one with <see cref="DwarfFormatException"/>, never
/// loop, wrap, stop silently, or crash with a raw runtime exception.
/// </summary>
public class DwarfLineReaderTests
{
    // Standard opcode lengths for opcode_base 13 (DWARF 4 §6.2.5.3).
    private static readonly byte[] StdOpcodeLengths = [0, 1, 1, 1, 1, 0, 0, 0, 1, 0, 0, 1];

    // include_directories: none; file_names: "a.c" (dir 0, mtime 0, length 0); terminator.
    private static readonly byte[] DefaultTables = [0, (byte)'a', (byte)'.', (byte)'c', 0, 0, 0, 0, 0];

    /// <summary>A DWARF 4 line program unit; each knob defaults to a valid value.</summary>
    internal static byte[] Dwarf4Unit(
        byte[] program,
        byte lineRange = 14,
        byte opcodeBase = 13,
        byte[]? tables = null,
        uint? headerLength = null)
    {
        var header = new List<byte> { 1, 1, 1, unchecked((byte)-5), lineRange, opcodeBase };
        for (int i = 0; i < opcodeBase - 1; i++)
            header.Add(i < StdOpcodeLengths.Length ? StdOpcodeLengths[i] : (byte)0);
        header.AddRange(tables ?? DefaultTables);

        var unit = new List<byte>();
        WriteU32(unit, (uint)(2 + 4 + header.Count + program.Length));
        WriteU16(unit, 4);
        WriteU32(unit, headerLength ?? (uint)header.Count);
        unit.AddRange(header);
        unit.AddRange(program);
        return [.. unit];
    }

    /// <summary>
    /// A DWARF 5 line program unit: <paramref name="tables"/> are the raw directory and file tables
    /// that follow standard_opcode_lengths.
    /// </summary>
    private static byte[] Dwarf5Unit(byte[] tables, byte[]? program = null)
    {
        var header = new List<byte> { 1, 1, 1, unchecked((byte)-5), 14, 13 };
        header.AddRange(StdOpcodeLengths);
        header.AddRange(tables);
        program ??= [];

        var unit = new List<byte>();
        WriteU32(unit, (uint)(2 + 2 + 4 + header.Count + program.Length));
        WriteU16(unit, 5);
        unit.Add(4); // address_size
        unit.Add(0); // segment_selector_size
        WriteU32(unit, (uint)header.Count);
        unit.AddRange(header);
        unit.AddRange(program);
        return [.. unit];
    }

    /// <summary>
    /// DWARF 5 tables whose directory table has <paramref name="dirFormats"/> entry formats (each
    /// DW_LNCT_path / DW_FORM_string) and declares <paramref name="dirCount"/> entries, followed by
    /// one directory's worth of bytes and an empty file table.
    /// </summary>
    private static byte[] Dwarf5UnitWithDirectoryCount(byte dirFormats, ulong dirCount)
    {
        var tables = new List<byte> { dirFormats };
        for (int i = 0; i < dirFormats; i++)
        {
            tables.Add((byte)DwLnct.Path);
            tables.Add((byte)DwForm.String);
        }
        WriteUleb(tables, dirCount);
        tables.AddRange([(byte)'d', 0]);
        tables.Add(0); // file_name_entry_format_count
        tables.Add(0); // file_names_count
        return Dwarf5Unit([.. tables]);
    }

    /// <summary>
    /// Runs <paramref name="parse"/> off the test thread and requires it to be rejected with
    /// <see cref="DwarfFormatException"/> within 5 s, so a regression to looping fails instead of
    /// hanging the run.
    /// </summary>
    internal static async Task<DwarfFormatException> AssertRejectedPromptlyAsync(Action parse)
    {
        var ct = TestContext.Current.CancellationToken;
        // A dedicated thread, so the 5 s clock measures the parse, not thread-pool queueing.
        var work = Task.Factory.StartNew(parse, ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var first = await Task.WhenAny(work, Task.Delay(TimeSpan.FromSeconds(5), ct));
        Assert.True(first == work, "the DWARF reader did not return within 5 s: it is looping on malformed input");
        return await Assert.ThrowsAsync<DwarfFormatException>(() => work);
    }

    [Fact]
    public void TestUnitBuilder_ProducesAValidProgram()
    {
        // The builder the adversarial cases mutate must itself parse; otherwise they prove nothing.
        var program = Assert.Single(DwarfLineReader.Parse(Dwarf4Unit([DwLns.Copy])));
        Assert.Equal(["", "a.c"], program.FileNames);
        var row = Assert.Single(program.Rows);
        Assert.Equal("a.c", row.FileName);
        Assert.Equal(1, row.Line);
    }

    [Fact(Timeout = 30_000)]
    public async Task Parse_ReservedUnitLengthThatWrapsBackToTheStart_FailsFast()
    {
        // #600 H1: unit_length 0xFFFFFFF6 made `pos + (int)unitLen` wrap back to 0; the unsupported
        // version of the first unit then sent the reader back to the start, forever.
        byte[] data = [0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0xF6, 0xFF, 0xFF, 0xFF, 0x00, 0x00];

        var ex = await AssertRejectedPromptlyAsync(() => DwarfLineReader.Parse(data));
        Assert.Contains("0xFFFFFFF6", ex.Message);
    }

    [Fact]
    public void Parse_UnitLongerThanTheSection_IsReportedNotDropped()
    {
        var unit = Dwarf4Unit([DwLns.Copy]);
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(unit.AsSpan(0, unit.Length - 1)));
        Assert.Contains("unit_length", ex.Message);
    }

    [Theory]
    [InlineData(0xFFFF_FFF0u)] // wraps headerEnd below the header
    [InlineData(0x7FFF_FFFFu)] // overflows int when added to the position
    [InlineData(1000u)]        // simply longer than the unit
    public void Parse_HeaderLengthPastTheUnit_Throws(uint headerLength)
    {
        var ex = Assert.Throws<DwarfFormatException>(() =>
            DwarfLineReader.Parse(Dwarf4Unit([DwLns.Copy], headerLength: headerLength)));
        Assert.Contains("header_length", ex.Message);
    }

    [Fact(Timeout = 30_000)]
    public async Task Parse_ExtendedOpcodeLengthThatRewinds_FailsFast()
    {
        // DW_LNS extended opcode, length 0xFFFFFFFA: as an int that is -6, which sent the cursor
        // back to the opcode's own first byte, forever.
        byte[] program = [0x00, 0xFA, 0xFF, 0xFF, 0xFF, 0x0F, 0x80];

        var ex = await AssertRejectedPromptlyAsync(() => DwarfLineReader.Parse(Dwarf4Unit(program)));
        Assert.Contains("extended opcode", ex.Message);
    }

    [Fact]
    public void Parse_ExtendedOpcodeLengthPastTheProgram_IsReportedNotDropped()
    {
        byte[] program = [DwLns.Copy, 0x00, 0x10, DwLne.EndSequence];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit(program)));
        Assert.Contains("extended opcode", ex.Message);
    }

    [Fact]
    public void Parse_ZeroLineRange_IsAFormatErrorNotADivideByZero()
    {
        byte[] program = [0x20]; // a special opcode
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit(program, lineRange: 0)));
        Assert.Contains("line_range", ex.Message);
    }

    [Fact]
    public void Parse_ZeroOpcodeBase_IsAFormatError()
    {
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit([DwLns.Copy], opcodeBase: 0)));
        Assert.Contains("opcode_base", ex.Message);
    }

    [Fact]
    public void Parse_StandardOpcodeLengthsPastTheHeader_Throw()
    {
        // Patch opcode_base (unit_length 4 + version 2 + header_length 4 + 5 header bytes) to 255:
        // it then claims 254 standard_opcode_lengths, and the header holds far fewer.
        var unit = Dwarf4Unit([DwLns.Copy]);
        Assert.Equal(13, unit[15]);
        unit[15] = 255;
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(unit));
        Assert.Contains("standard_opcode_lengths", ex.Message);
    }

    [Fact]
    public void Parse_UnterminatedIncludeDirectories_IsReportedNotDropped()
    {
        // The directory string runs to the end of the header without a terminator.
        byte[] tables = [(byte)'d', (byte)'i', (byte)'r'];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit([DwLns.Copy], tables: tables)));
        Assert.Contains("is not terminated", ex.Message);
    }

    [Theory]
    [InlineData((byte)1, 0xFFFF_FFFFUL, "more than")]          // wrapped to -1 as an int list capacity
    [InlineData((byte)1, 1000UL, "more than")]                 // more entries than the header has bytes
    [InlineData((byte)0, 0x7FFF_FFFFUL, "no entry format")]    // zero-byte entries: an unbounded loop and allocation
    [InlineData((byte)0, 1UL, "no entry format")]              // zero-byte entries, a count that fits
    public void Parse_Dwarf5DirectoryCountThatCannotFit_Throws(byte dirFormats, ulong dirCount, string reason)
    {
        var ex = Assert.Throws<DwarfFormatException>(() =>
            DwarfLineReader.Parse(Dwarf5UnitWithDirectoryCount(dirFormats, dirCount)));
        Assert.Contains("directories", ex.Message);
        Assert.Contains(reason, ex.Message);
    }

    [Fact]
    public void Parse_Dwarf5DirectoryCountThatFits_Parses()
    {
        // The same builder with a count that fits must parse, so the rejections above are about the count.
        var program = Assert.Single(DwarfLineReader.Parse(Dwarf5UnitWithDirectoryCount(1, 1)));
        Assert.Empty(program.Rows);
    }

    [Fact]
    public void Parse_UnsupportedVersionAnd64BitUnits_AreSkippedAndLaterUnitsParse()
    {
        var data = new List<byte>();
        // A 64-bit DWARF unit (escape + 8-byte length) holding only its version.
        WriteU32(data, 0xFFFF_FFFF);
        WriteU32(data, 2);
        WriteU32(data, 0);
        WriteU16(data, 4);
        // A version 6 unit.
        WriteU32(data, 2);
        WriteU16(data, 6);
        data.AddRange(Dwarf4Unit([DwLns.Copy]));

        var program = Assert.Single(DwarfLineReader.Parse(data.ToArray()));
        Assert.Single(program.Rows);
    }

    // ── Per-unit recovery ──────────────────────────────────────────────────

    // A program whose extended opcode claims 16 bytes and has 1: malformed inside its unit.
    internal static readonly byte[] BadExtendedOpcodeProgram = [0x00, 0x10, DwLne.EndSequence];

    // Offset of the line program within a Dwarf4Unit with the default tables:
    // unit_length 4 + version 2 + header_length 4 + header (6 fields + 12 lengths + 9 table bytes).
    private const int Dwarf4ProgramOffset = 4 + 2 + 4 + 6 + 12 + 9;

    [Fact]
    public void ParseUnits_GoodUnitThenMalformedUnit_KeepsTheGoodUnitAndReportsTheBadOne()
    {
        byte[] good = Dwarf4Unit([DwLns.Copy]);
        byte[] data = [.. good, .. Dwarf4Unit(BadExtendedOpcodeProgram)];

        var result = DwarfLineReader.ParseUnits(data);

        Assert.Single(Assert.Single(result.Programs).Rows);
        var error = Assert.Single(result.Errors);
        Assert.Equal(".debug_line", error.Section);
        Assert.Equal(good.Length + Dwarf4ProgramOffset, error.Offset); // section offset of the bad opcode
        Assert.Contains("extended opcode", error.Message);
        Assert.StartsWith(".debug_line: ", error.Message);

        // Parse is the strict form: the same input throws that error.
        Assert.Equal(error.Message, Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(data)).Message);
    }

    [Fact]
    public void ParseUnits_MalformedUnitThenGoodUnit_ResumesAtTheMalformedUnitsEnd()
    {
        byte[] data = [.. Dwarf4Unit(BadExtendedOpcodeProgram), .. Dwarf4Unit([DwLns.Copy, DwLns.Copy])];

        var result = DwarfLineReader.ParseUnits(data);

        Assert.Equal(2, Assert.Single(result.Programs).Rows.Count);
        Assert.Single(result.Errors);
    }

    [Fact]
    public void ParseUnits_TwoUnits_EachRunsOnlyItsOwnProgram()
    {
        // Each state machine must stop at its unit's end, not run on into the next unit's bytes.
        byte[] data = [.. Dwarf4Unit([DwLns.Copy]), .. Dwarf4Unit([DwLns.Copy, DwLns.Copy])];

        var result = DwarfLineReader.ParseUnits(data);

        Assert.Empty(result.Errors);
        Assert.Equal([1, 2], result.Programs.Select(p => p.Rows.Count));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(7)]
    public void ParseUnits_TrailingZeroPadding_IsNotAnError(int padding)
    {
        byte[] data = [.. Dwarf4Unit([DwLns.Copy]), .. new byte[padding]];

        var result = DwarfLineReader.ParseUnits(data);

        Assert.Single(Assert.Single(result.Programs).Rows);
        Assert.Empty(result.Errors);
    }

    [Fact]
    public void ParseUnits_ZeroLengthUnitBeforeAValidUnit_IsSkipped()
    {
        byte[] data = [0, 0, 0, 0, .. Dwarf4Unit([DwLns.Copy])];

        var result = DwarfLineReader.ParseUnits(data);

        Assert.Single(Assert.Single(result.Programs).Rows);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData(new byte[] { 0xF6, 0xFF, 0xFF, 0xFF, 0x00, 0x00 }, "reserved unit_length")]
    [InlineData(new byte[] { 0x01, 0x02 }, "4-byte unit_length")]
    public void ParseUnits_BadUnitLengthAfterAGoodUnit_StopsAndKeepsTheGoodUnit(byte[] tail, string reason)
    {
        var result = DwarfLineReader.ParseUnits([.. Dwarf4Unit([DwLns.Copy]), .. tail]);

        Assert.Single(result.Programs);
        Assert.Contains(reason, Assert.Single(result.Errors).Message);
    }

    // ── Guards, each with its own message (#604 review) ────────────────────

    [Fact]
    public void Parse_UnitTooShortForItsVersion_Throws()
    {
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse([0x01, 0x00, 0x00, 0x00, 0x04]));
        Assert.Contains("too short to hold its version", ex.Message);
    }

    [Fact]
    public void Parse_FixedFieldPastTheHeader_IsAFormatErrorNotARuntimeException()
    {
        // header_length 2 holds minimum_instruction_length and maximum_operations_per_instruction,
        // but not default_is_stmt and the fields after it.
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit([DwLns.Copy], headerLength: 2)));
        Assert.Contains("runs past the end of its unit or header", ex.Message);
        Assert.IsType<IndexOutOfRangeException>(ex.InnerException);
    }

    [Fact]
    public void Parse_TruncatedFixedAdvancePc_Throws()
    {
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit([DwLns.FixedAdvancePc, 0x01])));
        Assert.Contains("DW_LNS_fixed_advance_pc", ex.Message);
    }

    [Fact]
    public void Parse_SetAddressWithLengthTwo_Throws()
    {
        // The opcode holds one address byte; the bytes after it belong to the next opcodes.
        byte[] program = [0x00, 0x02, DwLne.SetAddress, 0x10, DwLns.Copy, DwLns.Copy, DwLns.Copy];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit(program)));
        Assert.Contains("DW_LNE_set_address", ex.Message);
    }

    [Fact]
    public void Parse_DefineFileStringPastItsOpcode_Throws()
    {
        // DW_LNE_define_file of length 2 holds "a" but not its terminator; the bytes after it are
        // not part of the opcode.
        byte[] program = [0x00, 0x02, DwLne.DefineFile, (byte)'a', (byte)'b', 0, 0, 0, 0];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit(program)));
        Assert.Contains("is not terminated", ex.Message);
    }

    [Fact]
    public void Parse_TruncatedLeb128_Throws()
    {
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf4Unit([DwLns.AdvancePc, 0x80])));
        Assert.Contains("DW_LNS_advance_pc operand LEB128", ex.Message);
        Assert.Contains("truncated", ex.Message);
    }

    [Fact]
    public void Parse_Data16PastTheHeader_Throws()
    {
        // One file entry whose DW_LNCT_MD5 is DW_FORM_data16, with 4 bytes left in the header: the
        // count fits, the entry does not.
        byte[] tables =
        [
            0, 0,                                   // no directory formats, no directories
            1, 5, (byte)DwForm.Data16,              // file format: DW_LNCT_MD5 / data16
            1,                                      // one file
            1, 2, 3, 4,
        ];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf5Unit(tables)));
        Assert.Contains("run past its header", ex.Message);
    }

    [Fact]
    public void Parse_FormNotAllowedInAnEntry_Throws()
    {
        byte[] tables = [1, (byte)DwLnct.Path, (byte)DwForm.Ref4, 1, 0, 0, 0, 0, 0, 0];
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfLineReader.Parse(Dwarf5Unit(tables)));
        Assert.Contains("not valid in a line table header", ex.Message);
    }

    [Fact]
    public void Parse_StrpSupPath_IsSkippedAsFourBytes()
    {
        // DW_FORM_strp_sup names the supplementary file's string section, which nesm does not
        // load; the path is unknown but the table is still read exactly.
        byte[] tables = [1, (byte)DwLnct.Path, (byte)DwForm.StrpSup, 1, 0x10, 0, 0, 0, 0, 0];
        var program = Assert.Single(DwarfLineReader.Parse(Dwarf5Unit(tables, [DwLns.Copy])));
        Assert.Single(program.Rows);
    }

    [Fact(Timeout = 30_000)]
    public async Task ModuleReader_MalformedDebugLine_IsAMetadataDiagnosticNotALoadFailure()
    {
        byte[] wasm = ModuleWithCustomSection(".debug_line",
            [0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0xF6, 0xFF, 0xFF, 0xFF, 0x00, 0x00]);

        var ct = TestContext.Current.CancellationToken;
        var read = Task.Run(() => ModuleReader.ReadModule(
            new MemoryStream(wasm), new ModuleReaderOptions { Features = WasmFeatures.All }), ct);
        Assert.True(await Task.WhenAny(read, Task.Delay(TimeSpan.FromSeconds(5), ct)) == read,
            "reading the module did not return within 5 s");
        var module = await read;

        Assert.Empty(module.DebugInfo!);
        var diagnostic = Assert.Single(module.MetadataDiagnostics);
        Assert.Equal(".debug_line", diagnostic.SectionName);
        Assert.Equal(WasmMetadataErrorKind.Malformed, diagnostic.Kind);
        Assert.Contains("0xFFFFFFF6", diagnostic.Message);

        var strict = Assert.Throws<WasmMetadataException>(() => ModuleReader.ReadModule(
            new MemoryStream(wasm),
            new ModuleReaderOptions { Features = WasmFeatures.All, MetadataValidation = WasmMetadataValidationMode.Strict }));
        Assert.Equal(".debug_line", strict.Diagnostic.SectionName);
    }

    [Fact]
    public void ModuleReader_KeepsTheGoodUnitsAndReportsEachMalformedUnit()
    {
        byte[] debugLine =
        [
            .. Dwarf4Unit(BadExtendedOpcodeProgram),
            .. Dwarf4Unit([DwLns.Copy]),
            .. Dwarf4Unit([DwLns.FixedAdvancePc, 0x01]),
        ];
        byte[] wasm = ModuleWithCustomSection(".debug_line", debugLine);

        var module = ModuleReader.ReadModule(new MemoryStream(wasm), new ModuleReaderOptions { Features = WasmFeatures.All });

        Assert.Single(Assert.Single(module.DebugInfo!).Rows);
        Assert.Equal(2, module.MetadataDiagnostics.Count);
        Assert.All(module.MetadataDiagnostics, d => Assert.Equal(".debug_line", d.SectionName));
        Assert.Contains("extended opcode", module.MetadataDiagnostics[0].Message);
        Assert.Contains("DW_LNS_fixed_advance_pc", module.MetadataDiagnostics[1].Message);

        var strict = Assert.Throws<WasmMetadataException>(() => ModuleReader.ReadModule(
            new MemoryStream(wasm),
            new ModuleReaderOptions { Features = WasmFeatures.All, MetadataValidation = WasmMetadataValidationMode.Strict }));
        var inner = Assert.IsType<DwarfFormatException>(strict.InnerException);
        Assert.Equal(Dwarf4ProgramOffset, inner.Offset);
        Assert.StartsWith(".debug_line: the extended opcode", strict.Message);
    }

    internal static byte[] ModuleWithCustomSection(params (string Name, byte[] Content)[] sections)
    {
        var wasm = new List<byte> { 0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00 };
        foreach (var (name, content) in sections)
        {
            var body = new List<byte>();
            WriteUleb(body, (ulong)name.Length);
            body.AddRange(System.Text.Encoding.UTF8.GetBytes(name));
            body.AddRange(content);
            wasm.Add(0); // custom section
            WriteUleb(wasm, (ulong)body.Count);
            wasm.AddRange(body);
        }
        return [.. wasm];
    }

    private static byte[] ModuleWithCustomSection(string name, byte[] content) =>
        ModuleWithCustomSection((name, content));

    private static void WriteU32(List<byte> buf, uint value)
    {
        buf.Add((byte)value);
        buf.Add((byte)(value >> 8));
        buf.Add((byte)(value >> 16));
        buf.Add((byte)(value >> 24));
    }

    private static void WriteU16(List<byte> buf, ushort value)
    {
        buf.Add((byte)value);
        buf.Add((byte)(value >> 8));
    }

    private static void WriteUleb(List<byte> buf, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            buf.Add(b);
        }
        while (value != 0);
    }
}
