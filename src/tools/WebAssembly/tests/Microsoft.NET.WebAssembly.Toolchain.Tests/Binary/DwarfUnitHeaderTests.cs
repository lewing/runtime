// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// The shared .debug_info unit header (#600 P10): every DWARF 2–5 header form, 32- and 64-bit DWARF,
/// and DWARF 5 unit types, read the same way by every reader.
/// </summary>
public class DwarfUnitHeaderTests
{
    // ── Builders ───────────────────────────────────────────────────────────────

    /// <summary>One abbreviation: code, tag, children flag, (attribute, form) pairs.</summary>
    private sealed record Abbrev(uint Code, ushort Tag, bool Children, params (ushort Attr, DwForm Form)[] Attrs);

    private static byte[] Abbrevs(params Abbrev[] entries)
    {
        var buf = new List<byte>();
        foreach (var e in entries)
        {
            Uleb(buf, e.Code);
            Uleb(buf, e.Tag);
            buf.Add(e.Children ? (byte)1 : (byte)0);
            foreach (var (attr, form) in e.Attrs)
            {
                Uleb(buf, attr);
                Uleb(buf, (uint)form);
            }
            buf.AddRange([0, 0]);
        }
        buf.Add(0);
        return [.. buf];
    }

    /// <summary>A DWARF 2–4 unit (32-bit, or 64-bit with the escape and 8-byte offsets).</summary>
    private static byte[] UnitV4(byte[] dies, bool dwarf64 = false, ushort version = 4, byte addrSize = 4)
    {
        var body = new List<byte>();
        U16(body, version);
        Offset(body, 0, dwarf64); // debug_abbrev_offset
        body.Add(addrSize);
        body.AddRange(dies);
        return WithLength(body, dwarf64);
    }

    /// <summary>A DWARF 5 unit of <paramref name="unitType"/> with its type-specific header fields.</summary>
    private static byte[] UnitV5(byte unitType, byte[] dies, bool dwarf64 = false, byte[]? extraHeader = null)
    {
        var body = new List<byte>();
        U16(body, 5);
        body.Add(unitType);
        body.Add(4); // address_size
        Offset(body, 0, dwarf64); // debug_abbrev_offset
        body.AddRange(extraHeader ?? []);
        body.AddRange(dies);
        return WithLength(body, dwarf64);
    }

    private static byte[] WithLength(List<byte> body, bool dwarf64)
    {
        var unit = new List<byte>();
        if (dwarf64)
        {
            U32(unit, DwarfUnitLength.Dwarf64Escape);
            U64(unit, (ulong)body.Count);
        }
        else
        {
            U32(unit, (uint)body.Count);
        }
        unit.AddRange(body);
        return [.. unit];
    }

    private static void Offset(List<byte> buf, ulong value, bool dwarf64)
    {
        if (dwarf64) U64(buf, value);
        else U32(buf, (uint)value);
    }

    private static void Uleb(List<byte> buf, ulong value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            buf.Add(value != 0 ? (byte)(b | 0x80) : b);
        } while (value != 0);
    }

    private static void U16(List<byte> buf, ushort v) => buf.AddRange([(byte)v, (byte)(v >> 8)]);
    private static void U32(List<byte> buf, uint v) => buf.AddRange([(byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24)]);
    private static void U64(List<byte> buf, ulong v) { U32(buf, (uint)v); U32(buf, (uint)(v >> 32)); }

    private static void Str(List<byte> buf, string s)
    {
        buf.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
        buf.Add(0);
    }

    // .debug_str: "S\0f\0x\0"
    private static readonly byte[] StrSection = [(byte)'S', 0, (byte)'f', 0, (byte)'x', 0];
    private const int StrS = 0, StrF = 2, StrX = 4;

    /// <summary>
    /// A unit holding struct "S" (byte size 4), subprogram "f" at low_pc 3 with parameter "x" in wasm
    /// local 2. Every name is a DW_FORM_strp, so its width is the unit's offset size.
    /// </summary>
    private static (byte[] Abbrev, byte[] Dies) StrpUnitContent(bool dwarf64)
    {
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true),
            new Abbrev(2, DwTag.StructureType, false, (DwAt.Name, DwForm.Strp), (DwAt.ByteSize, DwForm.Data1)),
            new Abbrev(3, DwTag.Subprogram, true, (DwAt.Name, DwForm.Strp), (DwAt.LowPc, DwForm.Addr)),
            new Abbrev(4, DwTag.FormalParameter, false, (DwAt.Name, DwForm.Strp), (DwAt.Location, DwForm.Exprloc)));
        var dies = new List<byte> { 1, 2 };
        Offset(dies, StrS, dwarf64);
        dies.Add(4);
        dies.Add(3);
        Offset(dies, StrF, dwarf64);
        U32(dies, 3);
        dies.Add(4);
        Offset(dies, StrX, dwarf64);
        dies.AddRange([3, DwOp.WasmLocation, 0x00, 0x02]);
        dies.AddRange([0, 0]);
        return (abbrev, [.. dies]);
    }

    [Fact]
    public void StrpUnit_Dwarf32_IsReadByEveryReader()
    {
        // The builder the 64-bit cases reuse must itself parse in 32-bit form, or they prove nothing.
        var (abbrev, dies) = StrpUnitContent(dwarf64: false);
        byte[] info = UnitV4(dies);

        Assert.Equal("S", Assert.Single(DwarfTypeReader.ParseStructTypes(info, abbrev, StrSection, null, null, null, 50)).Name);
        var f = Assert.Single(DwarfSubprogramReader.Parse(info, abbrev, StrSection, null, null, null).Subprograms);
        Assert.Equal(("f", 3L), (f.Name, f.LowPc));
        var x = Assert.Single(DwarfVariableReader.ParseVariables(info, abbrev, StrSection, null, null, funcIndex: 3));
        Assert.Equal(("x", 2), (x.Name, x.WasmLocalIndex));
    }

    [Fact]
    public void Dwarf64Unit_IsReadByEveryReader_AndTheNextUnitStaysInStep()
    {
        // In 64-bit DWARF, DW_FORM_strp is 8 bytes. A 64-bit unit used to be skipped by every reader;
        // read with 4-byte offsets it would also desynchronise every attribute after the first name.
        var (abbrev, dies64) = StrpUnitContent(dwarf64: true);
        var (_, dies32) = StrpUnitContent(dwarf64: false);
        byte[] info = [.. UnitV4(dies64, dwarf64: true), .. UnitV4(dies32)];
        var errors = new List<DwarfFormatException>();

        var types = DwarfTypeReader.ParseStructTypes(info, abbrev, StrSection, null, null, null, 50, errors);
        Assert.Empty(errors);
        Assert.Equal(["S", "S"], types.Select(t => t.Name));
        Assert.All(types, t => Assert.Equal(4, t.ByteSize));

        var subprograms = DwarfSubprogramReader.Parse(info, abbrev, StrSection, null, null, null);
        Assert.Equal(2, subprograms.Units);
        Assert.Equal(0, subprograms.SkippedUnits);
        Assert.All(subprograms.Subprograms, s => Assert.Equal(("f", 3L), (s.Name, s.LowPc)));
        Assert.Equal(2, subprograms.Subprograms.Count);

        var x = Assert.Single(DwarfVariableReader.ParseVariables(UnitV4(dies64, dwarf64: true), abbrev, StrSection, null, null, funcIndex: 3));
        Assert.Equal(("x", 2), (x.Name, x.WasmLocalIndex));
    }

    [Fact]
    public void Dwarf5TypeUnit_HeaderFieldsAreSkipped_AndItsTypesRead()
    {
        // DW_UT_type adds type_signature (8) and type_offset (offset size) to the header. Read with
        // the compile-unit layout, the first DIE started inside the signature.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.TypeUnit, true),
            new Abbrev(2, DwTag.CompileUnit, true),
            new Abbrev(3, DwTag.StructureType, false, (DwAt.Name, DwForm.String), (DwAt.ByteSize, DwForm.Data1)));
        var typeDies = new List<byte> { 1, 3 };
        Str(typeDies, "T");
        typeDies.AddRange([8, 0]);
        var typeHeader = new List<byte>();
        U64(typeHeader, 0x7766_5544_3322_1101); // type_signature: its first byte is abbreviation 1
        U32(typeHeader, 25);                    // type_offset of the struct DIE
        var compileDies = new List<byte> { 2, 3 };
        Str(compileDies, "S");
        compileDies.AddRange([4, 0]);
        byte[] info = [.. UnitV5(DwUt.Type, [.. typeDies], extraHeader: [.. typeHeader]), .. UnitV5(DwUt.Compile, [.. compileDies])];
        var errors = new List<DwarfFormatException>();

        var types = DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50, errors);

        Assert.Empty(errors);
        Assert.Equal([("T", 8), ("S", 4)], types.Select(t => (t.Name, t.ByteSize)));
    }

    [Fact]
    public void Dwarf2RefAddr_IsAddressSized()
    {
        // In DWARF 2, DW_FORM_ref_addr is as wide as an address (8 here); from DWARF 3 it is offset-sized.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true),
            new Abbrev(2, DwTag.Subprogram, false, (DwAt.Name, DwForm.String)),
            new Abbrev(3, DwTag.Subprogram, false, (DwAt.AbstractOrigin, DwForm.RefAddr), (DwAt.LowPc, DwForm.Addr)));
        const int declOffset = 4 + 2 + 4 + 1 + 1; // unit header (11) + the compile-unit DIE's code
        var dies = new List<byte> { 1, 2 };
        Str(dies, "decl");
        dies.Add(3);
        U64(dies, declOffset);
        U64(dies, 0x40);
        dies.Add(0);
        byte[] info = UnitV4([.. dies], version: 2, addrSize: 8);

        var concrete = Assert.Single(DwarfSubprogramReader.Parse(info, abbrev, null, null, null, null).Subprograms);

        Assert.Equal(("decl", 0x40L), (concrete.Name, concrete.LowPc));
    }

    [Fact]
    public void FilteredParse_SkipsBlocksWithTheirLengthField()
    {
        // DwarfSubprogramReader skips every DIE but subprograms. Skipping a block or expression must
        // move past its length field as well as its content, or the next DIE is read out of step.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true),
            new Abbrev(2, DwTag.Variable, false,
                (DwAt.Location, DwForm.Exprloc), (DwAt.ConstValue, DwForm.Block1), (DwAt.Name, DwForm.Block2)),
            new Abbrev(3, DwTag.Subprogram, false, (DwAt.Name, DwForm.String), (DwAt.LowPc, DwForm.Addr)));
        var dies = new List<byte> { 1, 2 };
        dies.AddRange([5, DwOp.Addr, 0xFF, 0xFF, 0xFF, 0xFF]); // exprloc: DW_OP_addr 0xffffffff
        dies.AddRange([2, 3, 3]);                              // block1
        dies.AddRange([1, 0, 3]);                              // block2
        dies.Add(3);
        Str(dies, "f");
        U32(dies, 0x20);
        dies.Add(0);
        byte[] info = UnitV4([.. dies]);

        var f = Assert.Single(DwarfSubprogramReader.Parse(info, abbrev, null, null, null, null).Subprograms);

        Assert.Equal(("f", 0x20L), (f.Name, f.LowPc));
    }

    [Fact]
    public void UnknownForm_IsReportedNotSkippedAsZeroBytes()
    {
        // A form the reader cannot size used to be skipped as zero bytes, so the rest of the unit was read
        // out of step. It is a malformed unit: reported, and the next unit is still read.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true),
            new Abbrev(2, DwTag.StructureType, false, (DwAt.Name, DwForm.String), (DwAt.ByteSize, (DwForm)0x7F)),
            new Abbrev(3, DwTag.StructureType, false, (DwAt.Name, DwForm.String), (DwAt.ByteSize, DwForm.Data1)));
        var bad = new List<byte> { 1, 2 };
        Str(bad, "B");
        bad.AddRange([4, 0]);
        var good = new List<byte> { 1, 3 };
        Str(good, "S");
        good.AddRange([4, 0]);
        byte[] info = [.. UnitV4([.. bad]), .. UnitV4([.. good])];
        var errors = new List<DwarfFormatException>();

        var types = DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50, errors);

        Assert.Equal("S", Assert.Single(types).Name);
        Assert.Contains("form 0x7F", Assert.Single(errors).Message);
        var subprograms = DwarfSubprogramReader.Parse(info, abbrev, null, null, null, null);
        Assert.Equal((1, 1), (subprograms.Units, subprograms.SkippedUnits));
    }

    // ── Per-unit recovery in every .debug_info reader ────────────────────────────

    /// <summary>
    /// Abbreviations for the recovery cases: a subprogram with a parameter in a wasm local, and three
    /// variables that make a unit malformed in different ways.
    /// </summary>
    private static readonly byte[] RecoveryAbbrevs = Abbrevs(
        new Abbrev(1, DwTag.CompileUnit, true),
        new Abbrev(2, DwTag.Subprogram, true, (DwAt.Name, DwForm.String), (DwAt.LowPc, DwForm.Addr)),
        new Abbrev(3, DwTag.FormalParameter, false, (DwAt.Name, DwForm.String), (DwAt.Location, DwForm.Exprloc)),
        new Abbrev(4, DwTag.Variable, false, (DwAt.ByteSize, (DwForm)0x7F)),
        new Abbrev(5, DwTag.Variable, false, (DwAt.ConstValue, DwForm.Data16), (DwAt.Name, DwForm.String)),
        new Abbrev(6, DwTag.Subprogram, false, (DwAt.Name, DwForm.String), (DwAt.LowPc, DwForm.Addr)));

    /// <summary>A unit with subprogram <paramref name="name"/> at low_pc <paramref name="lowPc"/>, whose parameter is in wasm local <paramref name="local"/>.</summary>
    private static byte[] FunctionUnit(string name, byte lowPc, byte local)
    {
        var dies = new List<byte> { 1, 2 };
        Str(dies, name);
        U32(dies, lowPc);
        dies.Add(3);
        Str(dies, "p" + name);
        dies.AddRange([3, DwOp.WasmLocation, 0x00, local]);
        dies.AddRange([0, 0]);
        return UnitV4([.. dies]);
    }

    public static TheoryData<string> BadUnits => ["unknown_form", "truncated_addr", "data16_trunc"];

    private static byte[] BadUnit(string kind) => kind switch
    {
        // A form that cannot be sized.
        "unknown_form" => UnitV4([1, 4, 0x11, 0x22, 0]),
        // A subprogram whose 4-byte low_pc has one byte before the unit ends: a raw IndexOutOfRangeException.
        "truncated_addr" => UnitV4([1, 6, (byte)'b', 0, 0x01]),
        // A 16-byte constant that runs past the unit, then a string: a raw ArgumentOutOfRangeException.
        _ => UnitV4([1, 5, 0x01, 0x02]),
    };

    private static byte[] InfoWithBadMiddleUnit(string kind) =>
        [.. FunctionUnit("f1", 1, 0), .. BadUnit(kind), .. FunctionUnit("f3", 3, 2)];

    [Theory]
    [MemberData(nameof(BadUnits))]
    public void SubprogramReader_BadMiddleUnit_KeepsTheOtherUnits(string kind)
    {
        var index = DwarfSubprogramReader.Parse(InfoWithBadMiddleUnit(kind), RecoveryAbbrevs, null, null, null, null);

        Assert.Equal(["f1", "f3"], index.Subprograms.Select(s => s.Name));
        Assert.Equal((2, 1), (index.Units, index.SkippedUnits));
    }

    [Theory]
    [MemberData(nameof(BadUnits))]
    public void VariableReader_BadMiddleUnit_KeepsTheOtherUnits(string kind)
    {
        byte[] info = InfoWithBadMiddleUnit(kind);

        var variable = Assert.Single(DwarfVariableReader.ParseVariables(info, RecoveryAbbrevs, null, null, null, funcIndex: 3));
        Assert.Equal(("pf3", 2), (variable.Name, variable.WasmLocalIndex));

        var info2 = Assert.Single(DwarfVariableReader.ParseVariableInfos(info, RecoveryAbbrevs, null, null, null, funcIndex: 3).Variables);
        Assert.Equal(("pf3", 2L), (info2.Name, info2.LocationValue));
    }

    // ── DWARF 5 type units: strx names through the unit's own str_offsets_base ─────

    [Fact]
    public void Dwarf5TypeUnit_StrxNames_UseTheTypeUnitsStrOffsetsBase()
    {
        // clang -gdwarf-5 -fdebug-types-section: the DW_TAG_type_unit root carries DW_AT_str_offsets_base.
        // Resolved against base 0, every strx name in the unit shifted by the contribution header.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.TypeUnit, true, (DwAt.StrOffsetsBase, DwForm.SecOffset)),
            new Abbrev(2, DwTag.StructureType, true, (DwAt.Name, DwForm.Strx1), (DwAt.ByteSize, DwForm.Data1)),
            new Abbrev(3, DwTag.Member, false, (DwAt.Name, DwForm.Strx1), (DwAt.DataMemberLocation, DwForm.Data1)));
        byte[] str = [.. "junk\0Node\0next\0"u8];
        // .debug_str_offsets: unit_length 12, version 5, padding; then the offsets of "Node" (5) and "next" (10).
        byte[] strOffsets = [12, 0, 0, 0, 5, 0, 0, 0, 5, 0, 0, 0, 10, 0, 0, 0];
        var typeHeader = new List<byte>();
        U64(typeHeader, 0x1122_3344_5566_7788);
        U32(typeHeader, 0);
        byte[] dies = [1, 8, 0, 0, 0, 2, 0, 16, 3, 1, 8, 0, 0];
        byte[] info = UnitV5(DwUt.Type, dies, extraHeader: [.. typeHeader]);
        var errors = new List<DwarfFormatException>();

        var type = Assert.Single(DwarfTypeReader.ParseStructTypes(info, abbrev, str, null, strOffsets, null, 50, errors));

        Assert.Empty(errors);
        Assert.Equal(("Node", 16), (type.Name, type.ByteSize));
        var member = Assert.Single(type.Members);
        Assert.Equal(("next", 8), (member.Name, member.ByteOffset));
    }

    // ── Form widths: DWARF 5 Table 7.6 ─────────────────────────────────────────────

    /// <summary>
    /// The size of every fixed-size form (DWARF 5 §7.5.6, Table 7.6), in 32- and 64-bit DWARF with
    /// 4-byte addresses; -1 for a form whose size depends on its value. Written out independently of
    /// the reader's table.
    /// </summary>
    [Theory]
    [InlineData(0x01, 4, 4)]   // addr: address size
    [InlineData(0x03, -1, -1)] // block2
    [InlineData(0x04, -1, -1)] // block4
    [InlineData(0x05, 2, 2)]   // data2
    [InlineData(0x06, 4, 4)]   // data4
    [InlineData(0x07, 8, 8)]   // data8
    [InlineData(0x08, -1, -1)] // string
    [InlineData(0x09, -1, -1)] // block
    [InlineData(0x0a, -1, -1)] // block1
    [InlineData(0x0b, 1, 1)]   // data1
    [InlineData(0x0c, 1, 1)]   // flag
    [InlineData(0x0d, -1, -1)] // sdata
    [InlineData(0x0e, 4, 8)]   // strp: offset size
    [InlineData(0x0f, -1, -1)] // udata
    [InlineData(0x10, 4, 8)]   // ref_addr (DWARF 3+): offset size
    [InlineData(0x11, 1, 1)]   // ref1
    [InlineData(0x12, 2, 2)]   // ref2
    [InlineData(0x13, 4, 4)]   // ref4
    [InlineData(0x14, 8, 8)]   // ref8
    [InlineData(0x15, -1, -1)] // ref_udata
    [InlineData(0x16, -1, -1)] // indirect
    [InlineData(0x17, 4, 8)]   // sec_offset
    [InlineData(0x18, -1, -1)] // exprloc
    [InlineData(0x19, 0, 0)]   // flag_present
    [InlineData(0x1a, -1, -1)] // strx
    [InlineData(0x1b, -1, -1)] // addrx
    [InlineData(0x1c, 4, 4)]   // ref_sup4
    [InlineData(0x1d, 4, 8)]   // strp_sup
    [InlineData(0x1e, 16, 16)] // data16
    [InlineData(0x1f, 4, 8)]   // line_strp
    [InlineData(0x20, 8, 8)]   // ref_sig8
    [InlineData(0x21, 0, 0)]   // implicit_const: the value is in .debug_abbrev
    [InlineData(0x22, -1, -1)] // loclistx
    [InlineData(0x23, -1, -1)] // rnglistx
    [InlineData(0x24, 8, 8)]   // ref_sup8
    [InlineData(0x25, 1, 1)]   // strx1
    [InlineData(0x26, 2, 2)]   // strx2
    [InlineData(0x27, 3, 3)]   // strx3
    [InlineData(0x28, 4, 4)]   // strx4
    [InlineData(0x29, 1, 1)]   // addrx1
    [InlineData(0x2a, 2, 2)]   // addrx2
    [InlineData(0x2b, 3, 3)]   // addrx3
    [InlineData(0x2c, 4, 4)]   // addrx4
    [InlineData(0x1f01, -1, -1)] // GNU_addr_index
    [InlineData(0x1f02, -1, -1)] // GNU_str_index
    [InlineData(0x1f20, 4, 8)] // GNU_ref_alt: offset size
    [InlineData(0x1f21, 4, 8)] // GNU_strp_alt: offset size
    [InlineData(0x2001, -1, -1)] // LLVM_addrx_offset: ULEB index + 4-byte offset
    public void FixedFormSize_MatchesDwarf5Table76(uint form, int size32, int size64)
    {
        Assert.Equal(size32, DwarfReaderHelpers.FixedFormSize(form, new DwarfFormContext(4, 4, 5)));
        Assert.Equal(size64, DwarfReaderHelpers.FixedFormSize(form, new DwarfFormContext(4, 8, 5)));
    }

    [Fact]
    public void FixedFormSize_AddrAndDwarf2RefAddr_AreAddressSized()
    {
        Assert.Equal(8, DwarfReaderHelpers.FixedFormSize((uint)DwForm.Addr, new DwarfFormContext(8, 4, 4)));
        Assert.Equal(8, DwarfReaderHelpers.FixedFormSize((uint)DwForm.RefAddr, new DwarfFormContext(8, 4, 2)));
        Assert.Equal(4, DwarfReaderHelpers.FixedFormSize((uint)DwForm.RefAddr, new DwarfFormContext(8, 4, 3)));
    }

    [Fact]
    public void UnknownForm_HasNoSize()
    {
        Assert.Equal(-2, DwarfReaderHelpers.FixedFormSize(0x7F, new DwarfFormContext(4, 4, 5)));
    }

    [Fact]
    public void LlvmAddrxOffset_IsAnIndexedAddressPlusAnOffset()
    {
        // DW_FORM_LLVM_addrx_offset (LLVM): a ULEB128 .debug_addr index, then a 4-byte offset added to it.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true, (DwAt.AddrBase, DwForm.SecOffset)),
            new Abbrev(2, DwTag.Subprogram, false, (DwAt.Name, DwForm.String), (DwAt.LowPc, (DwForm)0x2001)));
        // .debug_addr: unit_length, version 5, address_size 4, segment_selector_size 0; then 0x100, 0x200.
        byte[] addr = [12, 0, 0, 0, 5, 0, 4, 0, 0x00, 0x01, 0, 0, 0x00, 0x02, 0, 0];
        var dies = new List<byte> { 1, 8, 0, 0, 0, 2 };
        Str(dies, "f");
        dies.Add(1);       // index 1
        U32(dies, 0x10);   // offset
        dies.Add(2);
        Str(dies, "g");
        dies.Add(0);
        U32(dies, 0x4);
        dies.Add(0);
        byte[] info = UnitV5(DwUt.Compile, [.. dies]);

        var subprograms = DwarfSubprogramReader.Parse(info, abbrev, null, null, null, addr).Subprograms;

        Assert.Equal([("f", 0x210L), ("g", 0x104L)], subprograms.Select(s => (s.Name, s.LowPc)));
    }

    [Fact]
    public void UnknownDwarf5UnitType_IsReported_AndTheNextUnitRead()
    {
        // DW_UT_lo_user: its header layout is unknown, so the unit cannot be read. It is reported, not
        // skipped silently, and the units after it are still read.
        byte[] abbrev = Abbrevs(
            new Abbrev(1, DwTag.CompileUnit, true, (DwAt.Name, DwForm.String)),
            new Abbrev(2, DwTag.StructureType, false, (DwAt.Name, DwForm.String), (DwAt.ByteSize, DwForm.Data1)));
        var dies = new List<byte> { 1 };
        Str(dies, "c");
        dies.Add(2);
        Str(dies, "S");
        dies.AddRange([4, 0]);
        byte[] info = [.. UnitV5(0x80, [0]), .. UnitV5(DwUt.Compile, [.. dies])];

        var typeErrors = new List<DwarfFormatException>();
        Assert.Equal("S", Assert.Single(DwarfTypeReader.ParseStructTypes(info, abbrev, null, null, null, null, 50, typeErrors)).Name);
        Assert.Contains("unit_type 0x80", Assert.Single(typeErrors).Message);

        var unitErrors = new List<DwarfFormatException>();
        Assert.Equal("c", Assert.Single(DwarfCompileUnitReader.Parse(info, abbrev, null, null, null, int.MaxValue, unitErrors)).Name);
        Assert.Contains("unit_type 0x80", Assert.Single(unitErrors).Message);
    }

    [Theory]
    [InlineData(DwUt.Compile, 0)]
    [InlineData(DwUt.Partial, 0)]
    [InlineData(DwUt.Type, 8 + 4)]
    [InlineData(DwUt.SplitType, 8 + 4)]
    [InlineData(DwUt.Skeleton, 8)]
    [InlineData(DwUt.SplitCompile, 8)]
    public void TryRead_Dwarf5UnitTypes_PlaceTheFirstDie(byte unitType, int extra)
    {
        byte[] info = UnitV5(unitType, [0], extraHeader: new byte[extra]);
        var units = new DwarfUnitEnumerator(info, ".debug_info");

        Assert.True(units.MoveNext());
        Assert.Null(units.Error);
        Assert.True(DwarfUnitHeader.TryRead(info, units.Current, ".debug_info", out var header));

        Assert.Equal(unitType, header.UnitType);
        Assert.Equal(info.Length - 1, header.DieStart);
        Assert.False(units.MoveNext());
    }

    [Fact]
    public void TryRead_Dwarf64_UsesEightByteOffsets()
    {
        byte[] info = UnitV5(DwUt.Type, [0], dwarf64: true, extraHeader: new byte[8 + 8]);
        var units = new DwarfUnitEnumerator(info, ".debug_info");
        Assert.True(units.MoveNext());

        Assert.True(DwarfUnitHeader.TryRead(info, units.Current, ".debug_info", out var header));

        Assert.True(header.Is64Bit);
        Assert.Equal(8, header.Format.OffsetSize);
        Assert.Equal(info.Length - 1, header.DieStart);
    }

    [Theory]
    [InlineData(4, 1, "too short to hold its version")]   // unit_length covers half the version
    [InlineData(5, 5, "runs past its end")]                // a type unit without its signature
    public void TryRead_TruncatedHeader_IsReported(ushort version, int bodyLength, string message)
    {
        var body = new List<byte>();
        U16(body, version);
        body.AddRange([DwUt.Type, 4, 0, 0, 0, 0]);
        byte[] info = WithLength(body.GetRange(0, bodyLength), dwarf64: false);
        var units = new DwarfUnitEnumerator(info, ".debug_info");
        Assert.True(units.MoveNext());

        var frame = units.Current;
        var ex = Assert.Throws<DwarfFormatException>(() => DwarfUnitHeader.TryRead(info, frame, ".debug_info", out _));

        Assert.Contains(message, ex.Message);
        Assert.Equal(0, ex.Offset);
    }
}
