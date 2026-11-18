// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Tests for DWARF .debug_info struct type parsing.
/// Builds synthetic DWARF sections to exercise the parser without real wasm files.
/// </summary>
public class DwarfTypeReaderTests
{
    // ── Helper: Build DWARF sections ─────────────────────────────────────────────

    /// <summary>Builds a synthetic .debug_abbrev section.</summary>
    private static byte[] BuildAbbrevSection(params AbbrevDef[] defs)
    {
        var buf = new List<byte>();
        foreach (var def in defs)
        {
            WriteUleb128(buf, def.Code);
            WriteUleb128(buf, def.Tag);
            buf.Add(def.HasChildren ? (byte)1 : (byte)0);
            foreach (var (attr, form) in def.Attrs)
            {
                WriteUleb128(buf, attr);
                WriteUleb128(buf, form);
            }
            WriteUleb128(buf, 0); WriteUleb128(buf, 0); // attr/form terminator
        }
        buf.Add(0); // table terminator
        return buf.ToArray();
    }

    private record AbbrevDef(uint Code, uint Tag, bool HasChildren, (uint Attr, uint Form)[] Attrs);

    /// <summary>
    /// Builds a synthetic DWARF 4 .debug_info section for a single compilation unit
    /// containing the given DIE bytes.
    /// </summary>
    private static byte[] BuildInfoSection_D4(byte[] dieBytes, int abbrevOffset = 0, byte addrSize = 4)
    {
        // DWARF 4 CU header: unit_length(4), version(2), abbrev_offset(4), addr_size(1) = 11 bytes
        int headerPayload = 2 + 4 + 1 + dieBytes.Length; // version + abbrev + addr_size + dies
        var buf = new List<byte>();
        WriteU32LE(buf, (uint)headerPayload);
        WriteU16LE(buf, 4); // version
        WriteU32LE(buf, (uint)abbrevOffset);
        buf.Add(addrSize);
        buf.AddRange(dieBytes);
        return buf.ToArray();
    }

    /// <summary>
    /// Builds a synthetic DWARF 5 .debug_info section for a single compilation unit.
    /// </summary>
    private static byte[] BuildInfoSection_D5(byte[] dieBytes, int abbrevOffset = 0, byte addrSize = 4)
    {
        // DWARF 5: unit_length(4), version(2), unit_type(1), addr_size(1), abbrev_offset(4) = 12 bytes
        int headerPayload = 2 + 1 + 1 + 4 + dieBytes.Length;
        var buf = new List<byte>();
        WriteU32LE(buf, (uint)headerPayload);
        WriteU16LE(buf, 5); // version
        buf.Add(0x01); // DW_UT_compile
        buf.Add(addrSize);
        WriteU32LE(buf, (uint)abbrevOffset);
        buf.AddRange(dieBytes);
        return buf.ToArray();
    }

    /// <summary>Builds a .debug_str section from null-terminated strings.</summary>
    private static byte[] BuildStrSection(params string[] strings)
    {
        var buf = new List<byte>();
        foreach (var s in strings)
        {
            buf.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
            buf.Add(0);
        }
        return buf.ToArray();
    }

    // ── Test: Basic DWARF 4 struct with members ──────────────────────────────────

    [Fact]
    public void Dwarf4_SimpleStruct_ParsesNameSizeAndMembers()
    {
        // Abbrev table:
        //   1: DW_TAG_compile_unit (has children) — DW_AT_name(string)
        //   2: DW_TAG_base_type (no children) — DW_AT_name(string), DW_AT_byte_size(data1)
        //   3: DW_TAG_structure_type (has children) — DW_AT_name(string), DW_AT_byte_size(data1)
        //   4: DW_TAG_member (no children) — DW_AT_name(string), DW_AT_type(ref4), DW_AT_data_member_location(data1)
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),                           // compile_unit: name(string)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),             // base_type: name(string), byte_size(data1)
            new AbbrevDef(3, 0x13, true,  [(0x03, 0x08), (0x0b, 0x0b)]),             // structure_type: name(string), byte_size(data1)
            new AbbrevDef(4, 0x0d, false, [(0x03, 0x08), (0x49, 0x13), (0x38, 0x0b)]) // member: name(string), type(ref4), data_member_location(data1)
        );

        // DIE tree:
        //   CU "test.c"
        //     base_type "int" size=4     (at CU-relative offset calculated during parsing)
        //     struct "Point" size=8
        //       member "x" type=→int offset=0
        //       member "y" type=→int offset=4
        //     null (end struct children)
        //   null (end CU children)
        var dies = new List<byte>();

        // CU DIE (abbrev 1): name = "test.c"
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        // Track offset where base_type "int" DIE starts (CU-relative)
        // CU header is 11 bytes (DWARF 4), so the first DIE after header is at offset 11
        // CU DIE takes: 1 (abbrev) + 7 (string "test.c\0") = 8 bytes
        // So base_type starts at CU-relative offset 11 + 8 = 19

        // base_type "int" (abbrev 2)
        int intTypeOffset = 11 + dies.Count; // CU-relative offset of this DIE
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4); // byte_size = 4

        // structure_type "Point" (abbrev 3)
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "Point");
        dies.Add(8); // byte_size = 8

        // member "x" (abbrev 4)
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "x");
        WriteU32LE(dies, (uint)intTypeOffset); // type ref to "int"
        dies.Add(0); // data_member_location = 0

        // member "y" (abbrev 4)
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "y");
        WriteU32LE(dies, (uint)intTypeOffset); // type ref to "int"
        dies.Add(4); // data_member_location = 4

        dies.Add(0); // null terminator (end of struct children)
        dies.Add(0); // null terminator (end of CU children)

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Parse
        var results = DwarfTypeReader.ParseStructTypes(
            infoData, abbrevData, null, null, null, null, 50);

        Assert.Single(results);
        var point = results[0];
        Assert.Equal("Point", point.Name);
        Assert.Equal(8, point.ByteSize);
        Assert.Equal(2, point.Members.Count);

        Assert.Equal("x", point.Members[0].Name);
        Assert.Equal("int", point.Members[0].TypeName);
        Assert.Equal(0, point.Members[0].ByteOffset);
        Assert.Equal(4, point.Members[0].ByteSize);

        Assert.Equal("y", point.Members[1].Name);
        Assert.Equal("int", point.Members[1].TypeName);
        Assert.Equal(4, point.Members[1].ByteOffset);
        Assert.Equal(4, point.Members[1].ByteSize);
    }

    // ── Test: 64-bit udata values do not desynchronise the DIE stream ────────────

    /// <summary>
    /// A DW_FORM_udata constant of 2^64-1 (libc++'s numeric_limits members) is a ten-byte ULEB128.
    /// The reader used to stop after five bytes, leaving the cursor mid-number, so every later DIE in
    /// the unit decoded as garbage: on corerun this hid InterpThreadContext's members and
    /// InterpExecMethod itself (#538). The struct after the constant must still parse.
    /// </summary>
    [Fact]
    public void Dwarf4_TenByteUdata_DoesNotDesyncLaterDies()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),                            // compile_unit: name(string)
            new AbbrevDef(2, 0x34, false, [(0x03, 0x08), (0x1c, 0x0f)]),              // variable: name(string), const_value(udata)
            new AbbrevDef(3, 0x13, true,  [(0x03, 0x08), (0x0b, 0x0b)]),              // structure_type: name, byte_size(data1)
            new AbbrevDef(4, 0x0d, false, [(0x03, 0x08), (0x38, 0x0b)]));             // member: name, data_member_location(data1)

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "limits.cpp");
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "__max");
        dies.AddRange([0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01]); // 2^64-1
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "After");
        dies.Add(8);
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "field");
        dies.Add(4);
        dies.Add(0);
        dies.Add(0);

        var results = DwarfTypeReader.ParseStructTypes(
            BuildInfoSection_D4(dies.ToArray()), abbrevData, null, null, null, null, 50);

        var after = Assert.Single(results);
        Assert.Equal("After", after.Name);
        Assert.Equal(4, Assert.Single(after.Members).ByteOffset);
    }

    // ── Test: strp form (DWARF 4 with .debug_str) ───────────────────────────────

    [Fact]
    public void Dwarf4_Strp_ResolvesNamesFromDebugStr()
    {
        // .debug_str: "test.c\0Point\0width\0height\0int\0"
        var strData = BuildStrSection("test.c", "Point", "width", "height", "int");
        // Offsets: test.c=0, Point=7, width=13, height=19, int=26

        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x0e)]),                           // compile_unit: name(strp)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x0e), (0x0b, 0x0b)]),             // base_type: name(strp), byte_size(data1)
            new AbbrevDef(3, 0x13, true,  [(0x03, 0x0e), (0x0b, 0x0b)]),             // structure_type: name(strp), byte_size(data1)
            new AbbrevDef(4, 0x0d, false, [(0x03, 0x0e), (0x49, 0x13), (0x38, 0x0b)]) // member: name(strp), type(ref4), loc(data1)
        );

        var dies = new List<byte>();
        // CU
        WriteUleb128(dies, 1);
        WriteU32LE(dies, 0); // strp offset → "test.c"

        // base_type "int"
        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteU32LE(dies, 26); // strp → "int"
        dies.Add(4);

        // struct "Point"
        WriteUleb128(dies, 3);
        WriteU32LE(dies, 7); // strp → "Point"
        dies.Add(12);

        // member "width"
        WriteUleb128(dies, 4);
        WriteU32LE(dies, 13); // strp → "width"
        WriteU32LE(dies, (uint)intTypeOffset);
        dies.Add(0);

        // member "height"
        WriteUleb128(dies, 4);
        WriteU32LE(dies, 19); // strp → "height"
        WriteU32LE(dies, (uint)intTypeOffset);
        dies.Add(4);

        dies.Add(0); // end struct
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());

        var results = DwarfTypeReader.ParseStructTypes(
            infoData, abbrevData, strData, null, null, null, 50);

        Assert.Single(results);
        Assert.Equal("Point", results[0].Name);
        Assert.Equal(12, results[0].ByteSize);
        Assert.Equal("width", results[0].Members[0].Name);
        Assert.Equal("height", results[0].Members[1].Name);
    }

    // ── Test: DWARF 5 with strx forms ───────────────────────────────────────────

    [Fact]
    public void Dwarf5_Strx_ResolvesStringsThroughStrOffsets()
    {
        // .debug_str: "test.c\0Rect\0left\0top\0float\0"
        var strData = BuildStrSection("test.c", "Rect", "left", "top", "float");
        // Offsets: test.c=0, Rect=7, left=12, top=17, float=21

        // .debug_str_offsets: header(8 bytes for DWARF 5) + 5 entries
        // DWARF 5 str_offsets has: length(4), version(2), padding(2), then 4-byte offsets
        var strOffsets = new List<byte>();
        WriteU32LE(strOffsets, 5 * 4 + 4); // length (covers version + padding + entries)
        WriteU16LE(strOffsets, 5);          // version
        WriteU16LE(strOffsets, 0);          // padding
        // entries (at byte offset 8 = strOffsetsBase)
        WriteU32LE(strOffsets, 0);  // [0] → "test.c"
        WriteU32LE(strOffsets, 7);  // [1] → "Rect"
        WriteU32LE(strOffsets, 12); // [2] → "left"
        WriteU32LE(strOffsets, 17); // [3] → "top"
        WriteU32LE(strOffsets, 21); // [4] → "float"
        var strOffsetsData = strOffsets.ToArray();

        // Abbrev table: uses strx1 for names
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x25), (0x72, 0x17)]),             // compile_unit: name(strx1), str_offsets_base(sec_offset)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x25), (0x0b, 0x0b)]),             // base_type: name(strx1), byte_size(data1)
            new AbbrevDef(3, 0x13, true,  [(0x03, 0x25), (0x0b, 0x0b)]),             // structure_type: name(strx1), byte_size(data1)
            new AbbrevDef(4, 0x0d, false, [(0x03, 0x25), (0x49, 0x13), (0x38, 0x0b)]) // member: name(strx1), type(ref4), loc(data1)
        );

        var dies = new List<byte>();
        // CU DIE (abbrev 1)
        WriteUleb128(dies, 1);
        dies.Add(0); // strx1 index 0 → "test.c"
        WriteU32LE(dies, 8); // str_offsets_base = 8 (after header)

        // base_type "float"
        int floatOffset = 12 + dies.Count; // DWARF 5 CU header is 12 bytes
        WriteUleb128(dies, 2);
        dies.Add(4); // strx1 index 4 → "float"
        dies.Add(4); // byte_size = 4

        // struct "Rect"
        WriteUleb128(dies, 3);
        dies.Add(1); // strx1 index 1 → "Rect"
        dies.Add(8); // byte_size = 8

        // member "left"
        WriteUleb128(dies, 4);
        dies.Add(2); // strx1 index 2 → "left"
        WriteU32LE(dies, (uint)floatOffset);
        dies.Add(0);

        // member "top"
        WriteUleb128(dies, 4);
        dies.Add(3); // strx1 index 3 → "top"
        WriteU32LE(dies, (uint)floatOffset);
        dies.Add(4);

        dies.Add(0); // end struct
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D5(dies.ToArray());

        var results = DwarfTypeReader.ParseStructTypes(
            infoData, abbrevData, strData, null, strOffsetsData, null, 50);

        Assert.Single(results);
        var rect = results[0];
        Assert.Equal("Rect", rect.Name);
        Assert.Equal(8, rect.ByteSize);
        Assert.Equal(2, rect.Members.Count);
        Assert.Equal("left", rect.Members[0].Name);
        Assert.Equal("float", rect.Members[0].TypeName);
        Assert.Equal(0, rect.Members[0].ByteOffset);
        Assert.Equal("top", rect.Members[1].Name);
        Assert.Equal("float", rect.Members[1].TypeName);
        Assert.Equal(4, rect.Members[1].ByteOffset);
    }

    // ── Test: Name pattern filtering ─────────────────────────────────────────────

    [Fact]
    public void NamePattern_FiltersResultsCaseInsensitive()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),              // compile_unit
            new AbbrevDef(2, 0x13, false, [(0x03, 0x08), (0x0b, 0x0b)]) // structure_type (no children)
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        // Two structs
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "MyPoint");
        dies.Add(8);

        WriteUleb128(dies, 2);
        WriteInlineString(dies, "MyRect");
        dies.Add(16);

        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Match "rect" (case-insensitive)
        var results = DwarfTypeReader.ParseStructTypes(infoData, abbrevData, null, null, null, "rect", 50);
        Assert.Single(results);
        Assert.Equal("MyRect", results[0].Name);

        // No pattern → both
        var all = DwarfTypeReader.ParseStructTypes(infoData, abbrevData, null, null, null, null, 50);
        Assert.Equal(2, all.Count);
    }

    // ── Test: maxResults limits output ───────────────────────────────────────────

    [Fact]
    public void MaxResults_LimitsOutput()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x13, false, [(0x03, 0x08), (0x0b, 0x0b)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        for (int i = 0; i < 10; i++)
        {
            WriteUleb128(dies, 2);
            WriteInlineString(dies, $"Struct{i}");
            dies.Add(4);
        }
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());

        var results = DwarfTypeReader.ParseStructTypes(infoData, abbrevData, null, null, null, null, 3);
        Assert.Equal(3, results.Count);
    }

    // ── Test: Pointer type resolution ────────────────────────────────────────────

    [Fact]
    public void PointerType_ResolvedCorrectly()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),                           // compile_unit
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),             // base_type
            new AbbrevDef(3, 0x0f, false, [(0x49, 0x13)]),                           // pointer_type: type(ref4) — no name/size
            new AbbrevDef(4, 0x13, true,  [(0x03, 0x08), (0x0b, 0x0b)]),             // structure_type
            new AbbrevDef(5, 0x0d, false, [(0x03, 0x08), (0x49, 0x13), (0x38, 0x0b)]) // member
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        // base_type "char"
        int charOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "char");
        dies.Add(1);

        // pointer_type → char
        int charPtrOffset = 11 + dies.Count;
        WriteUleb128(dies, 3);
        WriteU32LE(dies, (uint)charOffset);

        // struct "Node"
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "Node");
        dies.Add(8);

        // member "data" → char*
        WriteUleb128(dies, 5);
        WriteInlineString(dies, "data");
        WriteU32LE(dies, (uint)charPtrOffset);
        dies.Add(0);

        // member "value" → char (1 byte)
        WriteUleb128(dies, 5);
        WriteInlineString(dies, "value");
        WriteU32LE(dies, (uint)charOffset);
        dies.Add(4);

        dies.Add(0); // end struct
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var results = DwarfTypeReader.ParseStructTypes(infoData, abbrevData, null, null, null, null, 50);

        Assert.Single(results);
        var node = results[0];
        Assert.Equal("Node", node.Name);

        Assert.Equal("data", node.Members[0].Name);
        Assert.Equal("char*", node.Members[0].TypeName);
        Assert.Equal(4, node.Members[0].ByteSize); // wasm32 pointer

        Assert.Equal("value", node.Members[1].Name);
        Assert.Equal("char", node.Members[1].TypeName);
        Assert.Equal(1, node.Members[1].ByteSize);
    }

    // ── Test: No debug sections returns empty ────────────────────────────────────

    [Fact]
    public void NoDebugSections_ReturnsEmpty()
    {
        var module = new WasmModule();
        var result = DwarfTypeReader.ReadStructTypes(module);
        Assert.Empty(result);
    }

    // ── Test: Module with only debug_info but no debug_abbrev returns empty ──────

    [Fact]
    public void DebugInfoWithoutAbbrev_ReturnsEmpty()
    {
        // A well-formed DWARF 4 unit header (length 7, version 4, abbrev offset 0, address size 4)
        // with no .debug_abbrev to resolve it against.
        var module = new WasmModule
        {
            CustomSections = { new CustomSection(".debug_info", [0x07, 0x00, 0x00, 0x00, 0x04, 0x00, 0x00, 0x00, 0x00, 0x00, 0x04]) }
        };
        var result = DwarfTypeReader.ReadStructTypes(module);
        Assert.Empty(result);
    }

    // ── Test: DW_AT_data_member_location via exprloc (DW_OP_plus_uconst) ────────

    [Fact]
    public void Exprloc_MemberLocation_Parsed()
    {
        // exprloc form (0x18) with DW_OP_plus_uconst
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),                           // compile_unit
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),             // base_type
            new AbbrevDef(3, 0x13, true,  [(0x03, 0x08), (0x0b, 0x0b)]),             // structure_type
            new AbbrevDef(4, 0x0d, false, [(0x03, 0x08), (0x49, 0x13), (0x38, 0x18)]) // member with exprloc
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "Pair");
        dies.Add(8);

        // member "a" at offset 0 via exprloc
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "a");
        WriteU32LE(dies, (uint)intOffset);
        // exprloc: length=1+1=2, DW_OP_plus_uconst(0x23), uleb128(0)
        WriteUleb128(dies, 2); // exprloc length
        dies.Add(0x23); // DW_OP_plus_uconst
        WriteUleb128(dies, 0);

        // member "b" at offset 4 via exprloc
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "b");
        WriteU32LE(dies, (uint)intOffset);
        WriteUleb128(dies, 2);
        dies.Add(0x23);
        WriteUleb128(dies, 4);

        dies.Add(0); // end struct
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var results = DwarfTypeReader.ParseStructTypes(infoData, abbrevData, null, null, null, null, 50);

        Assert.Single(results);
        Assert.Equal("Pair", results[0].Name);
        Assert.Equal(0, results[0].Members[0].ByteOffset);
        Assert.Equal(4, results[0].Members[1].ByteOffset);
    }

    // ── Adversarial lengths (#600 H1, M5b) ──────────────────────────────────────

    [Fact(Timeout = 30_000)]
    public async Task ParseStructTypes_ReservedUnitLengthThatWrapsBackToItself_FailsFast()
    {
        // unit_length 0xFFFFFFFC made `pos + 4 + (int)unitLen` equal the unit's own start; the
        // unsupported version (0) then resumed there, forever.
        byte[] info = [0xFC, 0xFF, 0xFF, 0xFF, 0x00, 0x00];

        var ex = await DwarfLineReaderTests.AssertRejectedPromptlyAsync(
            () => DwarfTypeReader.ParseStructTypes(info, [0], null, null, null, null, 50));
        Assert.Contains("0xFFFFFFFC", ex.Message);
    }

    [Theory]
    [InlineData(0xFFFF_FFEFu)] // the largest non-reserved length: wraps negative as an int
    [InlineData(1000u)]
    public void ParseStructTypes_UnitLongerThanTheSection_IsReportedNotDropped(uint unitLength)
    {
        var info = new List<byte>();
        WriteU32LE(info, unitLength);
        WriteU16LE(info, 4);
        WriteU32LE(info, 0);
        info.Add(4);

        var ex = Assert.Throws<DwarfFormatException>(() =>
            DwarfTypeReader.ParseStructTypes(info.ToArray(), [0], null, null, null, null, 50));
        Assert.Contains("unit_length", ex.Message);
    }

    [Fact]
    public void ParseStructTypes_HeaderPastTheUnit_Throws()
    {
        // A DWARF 4 unit holding its version but not the rest of its 7-byte header.
        var info = new List<byte>();
        WriteU32LE(info, 3);
        WriteU16LE(info, 4);
        info.Add(0);

        var ex = Assert.Throws<DwarfFormatException>(() =>
            DwarfTypeReader.ParseStructTypes(info.ToArray(), [0], null, null, null, null, 50));
        Assert.Contains("header of the unit", ex.Message);
    }

    [Fact]
    public void ParseStructTypes_FixedSizeFormPastTheUnitEnd_IsAFormatErrorNotARuntimeException()
    {
        // The struct's DW_FORM_data4 byte size has 2 of its 4 bytes before the unit ends.
        var abbrev = BuildAbbrevSection(
            new AbbrevDef(1, DwTag.CompileUnit, true, []),
            new AbbrevDef(2, DwTag.StructureType, false, [(DwAt.ByteSize, (uint)DwForm.Data4)]));
        byte[] info = BuildInfoSection_D4([1, 2, 4, 0]);

        var ex = Assert.Throws<DwarfFormatException>(() =>
            DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50));
        Assert.Contains("runs past the end of the unit", ex.Message);
        Assert.IsType<IndexOutOfRangeException>(ex.InnerException);
    }

    /// <summary>A unit with one struct "S" (4 bytes, no members), and its abbreviations.</summary>
    private static (byte[] Info, byte[] Abbrev) UnitWithOneStruct()
    {
        var abbrev = BuildAbbrevSection(
            new AbbrevDef(1, DwTag.CompileUnit, true, []),
            new AbbrevDef(2, DwTag.StructureType, false, [(DwAt.Name, (uint)DwForm.String), (DwAt.ByteSize, (uint)DwForm.Data1)]));
        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "S");
        dies.Add(4);
        dies.Add(0);
        return (BuildInfoSection_D4(dies.ToArray()), abbrev);
    }

    [Theory]
    [InlineData(new byte[] { 1, 2, 3 }, 1)] // a truncated unit_length: reported
    [InlineData(new byte[] { 0, 0, 0 }, 0)] // zero padding: not an error
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, 0)]
    public void ParseStructTypes_BytesAfterTheLastUnit_KeepTheUnitsTypes(byte[] tail, int expectedErrors)
    {
        var (info, abbrev) = UnitWithOneStruct();
        byte[] data = [.. info, .. tail];
        var errors = new List<DwarfFormatException>();

        var types = DwarfTypeReader.ParseStructTypes(data, abbrev, null, null, null, null, 50, errors);

        Assert.Equal("S", Assert.Single(types).Name);
        Assert.Equal(expectedErrors, errors.Count);
        if (expectedErrors != 0)
        {
            Assert.Equal(".debug_info", errors[0].Section);
            Assert.Equal(info.Length, errors[0].Offset);
        }
    }

    [Fact]
    public void ReadStructTypes_MalformedUnitBeforeAGoodUnit_KeepsTheGoodUnitsTypes()
    {
        // UnitWithRewindingBlock's abbreviations (codes 1 and 2) plus a struct at code 3, so one
        // table serves both units.
        var (bad, _) = UnitWithRewindingBlock();
        var abbrev = BuildAbbrevSection(
            new AbbrevDef(1, DwTag.CompileUnit, true, [(DwAt.Name, (uint)DwForm.String)]),
            new AbbrevDef(2, DwTag.Variable, false, [(DwAt.Location, (uint)DwForm.Block)]),
            new AbbrevDef(3, DwTag.StructureType, false, [(DwAt.Name, (uint)DwForm.String), (DwAt.ByteSize, (uint)DwForm.Data1)]));
        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "c");
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "S");
        dies.Add(4);
        dies.Add(0);
        byte[] info = [.. bad, .. BuildInfoSection_D4(dies.ToArray())];
        var module = new WasmModule
        {
            CustomSections = { new CustomSection(".debug_info", info), new CustomSection(".debug_abbrev", abbrev) }
        };

        var types = DwarfTypeReader.ReadStructTypes(module, null, 50, out var errors);

        Assert.Equal("S", Assert.Single(types).Name);
        Assert.Contains("block", Assert.Single(errors).Message);
        Assert.Equal("S", Assert.Single(DwarfTypeReader.ReadStructTypes(module)).Name);
    }

    [Fact(Timeout = 60_000)]
    public async Task SharedDieParser_LongIndirectFormChain_DoesNotRecursePerLink()
    {
        // 50,000 chained DW_FORM_indirect before the real form. Read recursively, one frame per
        // link overflowed the stack and killed the process. The struct's name is read with
        // ReadAttributeValue (DwarfTypeReader) and skipped with SkipFormValue (DwarfSubprogramReader,
        // which keeps only subprograms); the subprogram after it proves the cursor ended exactly.
        const int chain = 50_000;
        var abbrev = BuildAbbrevSection(
            new AbbrevDef(1, DwTag.CompileUnit, true, []),
            new AbbrevDef(2, DwTag.StructureType, false, [(DwAt.Name, (uint)DwForm.Indirect), (DwAt.ByteSize, (uint)DwForm.Data1)]),
            new AbbrevDef(3, DwTag.Subprogram, false, [(DwAt.Name, (uint)DwForm.String), (DwAt.LowPc, (uint)DwForm.Addr)]));
        var dies = new List<byte>(chain + 32);
        WriteUleb128(dies, 1);
        WriteUleb128(dies, 2);
        for (int i = 0; i < chain; i++)
            WriteUleb128(dies, (uint)DwForm.Indirect);
        WriteUleb128(dies, (uint)DwForm.String);
        WriteInlineString(dies, "S");
        dies.Add(4);
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "f");
        WriteU32LE(dies, 0x10);
        dies.Add(0);
        var info = BuildInfoSection_D4(dies.ToArray());

        // Dedicated threads with the default 1 MB stack, not the test thread's.
        var types = await Task.Factory.StartNew(
            () => DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.Equal("S", Assert.Single(types).Name);

        var subprograms = await Task.Factory.StartNew(
            () => DwarfSubprogramReader.Parse(info, abbrev, null, null, null, null),
            CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        var f = Assert.Single(subprograms.Subprograms);
        Assert.Equal("f", f.Name);
        Assert.Equal(0x10, f.LowPc);
    }

    /// <summary>
    /// A compile unit whose second DIE has a DW_FORM_block of length 0xFFFFFFFA: as an int that is
    /// -6, which moved the shared form skipper back to the DIE's own abbreviation code.
    /// </summary>
    private static (byte[] Info, byte[] Abbrev) UnitWithRewindingBlock()
    {
        var abbrev = BuildAbbrevSection(
            new AbbrevDef(1, DwTag.CompileUnit, true, [(DwAt.Name, (uint)DwForm.String)]),
            new AbbrevDef(2, DwTag.Variable, false, [(DwAt.Location, (uint)DwForm.Block)]));
        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "c");
        WriteUleb128(dies, 2);
        WriteUleb128(dies, 0xFFFF_FFFA);
        dies.AddRange([0, 0, 0, 0, 0, 0]);
        return (BuildInfoSection_D4(dies.ToArray()), abbrev);
    }

    [Fact(Timeout = 30_000)]
    public async Task SharedDieParser_BlockLengthThatRewinds_FailsFast()
    {
        var (info, abbrev) = UnitWithRewindingBlock();

        // The filtered parse skips the variable's attributes with SkipFormValue (it looped); it now
        // returns promptly and counts the malformed unit as skipped. The full parse reads them with
        // ReadAttributeValue (it threw a raw range exception).
        var ct = CancellationToken.None;
        var filtered = Task.Factory.StartNew(
            () => DwarfSubprogramReader.Parse(info, abbrev, null, null, null, null),
            ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        Assert.True(await Task.WhenAny(filtered, Task.Delay(TimeSpan.FromSeconds(5), ct)) == filtered,
            "the filtered DIE parse did not return within 5 s: it is looping on malformed input");
        Assert.Equal((0, 1), ((await filtered).Units, (await filtered).SkippedUnits));
        var read = await DwarfLineReaderTests.AssertRejectedPromptlyAsync(
            () => DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50));
        Assert.Contains("block", read.Message);
    }

    // ── Encoding helpers ─────────────────────────────────────────────────────────

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

    private static void WriteU32LE(List<byte> buf, uint value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)((value >> 8) & 0xFF));
        buf.Add((byte)((value >> 16) & 0xFF));
        buf.Add((byte)((value >> 24) & 0xFF));
    }

    private static void WriteU16LE(List<byte> buf, ushort value)
    {
        buf.Add((byte)(value & 0xFF));
        buf.Add((byte)((value >> 8) & 0xFF));
    }

    private static void WriteInlineString(List<byte> buf, string s)
    {
        buf.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
        buf.Add(0); // null terminator
    }
}
