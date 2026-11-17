// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Tests for DWARF variable/parameter parsing and wasm local index mapping.
/// </summary>
public class DwarfVariableReaderTests
{
    // ── Helper: Build DWARF sections ─────────────────────────────────────────────

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
            WriteUleb128(buf, 0); WriteUleb128(buf, 0);
        }
        buf.Add(0);
        return buf.ToArray();
    }

    private record AbbrevDef(uint Code, uint Tag, bool HasChildren, (uint Attr, uint Form)[] Attrs);

    private static byte[] BuildInfoSection_D4(byte[] dieBytes, int abbrevOffset = 0, byte addrSize = 4)
    {
        int headerPayload = 2 + 4 + 1 + dieBytes.Length;
        var buf = new List<byte>();
        WriteU32LE(buf, (uint)headerPayload);
        WriteU16LE(buf, 4);
        WriteU32LE(buf, (uint)abbrevOffset);
        buf.Add(addrSize);
        buf.AddRange(dieBytes);
        return buf.ToArray();
    }

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

    // ── Test: Basic subprogram with parameter and variable ─────────────────────

    [Fact]
    public void Subprogram_WithParameterAndVariable_ParsesCorrectly()
    {
        // Abbrev table:
        //   1: DW_TAG_compile_unit (has children) — DW_AT_name(string)
        //   2: DW_TAG_base_type (no children) — DW_AT_name(string), DW_AT_byte_size(data1)
        //   3: DW_TAG_subprogram (has children) — DW_AT_name(string), DW_AT_low_pc(addr), DW_AT_type(ref4)
        //   4: DW_TAG_formal_parameter (no children) — DW_AT_name(string), DW_AT_type(ref4), DW_AT_location(exprloc)
        //   5: DW_TAG_variable (no children) — DW_AT_name(string), DW_AT_type(ref4), DW_AT_location(exprloc)
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),                                      // compile_unit: name(string)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),                        // base_type: name(string), byte_size(data1)
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01), (0x49, 0x13)]),          // subprogram: name(string), low_pc(addr), type(ref4)
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)]),          // formal_parameter: name(string), type(ref4), location(exprloc)
            new AbbrevDef(5, 0x34, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])           // variable: name(string), type(ref4), location(exprloc)
        );

        var dies = new List<byte>();

        // CU DIE (abbrev 1)
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        // base_type "int" (abbrev 2)
        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        // subprogram "fibonacci" at func_index=5 (abbrev 3)
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "fibonacci");
        WriteU32LE(dies, 5);                         // DW_AT_low_pc = 5 (func index)
        WriteU32LE(dies, (uint)intTypeOffset);       // DW_AT_type → int

        // formal_parameter "n" at wasm local 0 (abbrev 4)
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "n");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);     // DW_OP_WASM_location local 0

        // variable "a" at wasm local 3 (abbrev 5)
        WriteUleb128(dies, 5);
        WriteInlineString(dies, "a");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 3);     // DW_OP_WASM_location local 3

        // variable "b" at wasm local 5 (abbrev 5)
        WriteUleb128(dies, 5);
        WriteInlineString(dies, "b");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 5);     // DW_OP_WASM_location local 5

        dies.Add(0); // end subprogram children
        dies.Add(0); // end CU children

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Parse variables for function index 5
        var vars = DwarfVariableReader.ParseVariables(infoData, abbrevData, null, null, null, 5);

        Assert.Equal(3, vars.Count);

        Assert.Equal("n", vars[0].Name);
        Assert.Equal("int", vars[0].TypeName);
        Assert.Equal(0, vars[0].WasmLocalIndex);
        Assert.True(vars[0].IsParameter);

        Assert.Equal("a", vars[1].Name);
        Assert.Equal("int", vars[1].TypeName);
        Assert.Equal(3, vars[1].WasmLocalIndex);
        Assert.False(vars[1].IsParameter);

        Assert.Equal("b", vars[2].Name);
        Assert.Equal("int", vars[2].TypeName);
        Assert.Equal(5, vars[2].WasmLocalIndex);
        Assert.False(vars[2].IsParameter);
    }

    // ── Test: Non-matching function returns empty ───────────────────────────────

    [Fact]
    public void NonMatchingFuncIndex_ReturnsEmpty()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "foo");
        WriteU32LE(dies, 10); // func index 10

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "x");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Ask for func index 99 — should not match
        var vars = DwarfVariableReader.ParseVariables(infoData, abbrevData, null, null, null, 99);
        Assert.Empty(vars);
    }

    // ── Test: DW_OP_WASM_location parsing ──────────────────────────────────────

    [Fact]
    public void ParseWasmLocation_LocalIndex()
    {
        // DW_OP_WASM_location 0x00 <uleb128 index>
        var loc = new byte[] { 0xED, 0x00, 42 };
        Assert.Equal(42, DwarfVariableReader.ParseWasmLocation(loc));
    }

    [Fact]
    public void ParseWasmLocation_GlobalIndex_ReturnsNegative()
    {
        // DW_OP_WASM_location 0x01 = global — not a local, return -1
        var loc = new byte[] { 0xED, 0x01, 5 };
        Assert.Equal(-1, DwarfVariableReader.ParseWasmLocation(loc));
    }

    [Fact]
    public void ParseWasmLocation_TooShort_ReturnsNegative()
    {
        Assert.Equal(-1, DwarfVariableReader.ParseWasmLocation([]));
        Assert.Equal(-1, DwarfVariableReader.ParseWasmLocation([0xED]));
        Assert.Equal(-1, DwarfVariableReader.ParseWasmLocation([0xED, 0x00])); // WASM_location needs 3+ bytes
    }

    [Fact]
    public void ParseWasmLocation_NotWasmOp_ReturnsNegative()
    {
        // Some other opcode, not 0xED
        var loc = new byte[] { 0x50, 0x00, 0x00 };
        Assert.Equal(-1, DwarfVariableReader.ParseWasmLocation(loc));
    }

    [Fact]
    public void ParseWasmLocation_LargeUleb128Index()
    {
        // DW_OP_WASM_location local 300 (ULEB128: 0xAC 0x02)
        var loc = new byte[] { 0xED, 0x00, 0xAC, 0x02 };
        Assert.Equal(300, DwarfVariableReader.ParseWasmLocation(loc));
    }

    // ── Test: No debug sections returns empty ──────────────────────────────────

    [Fact]
    public void NoDebugSections_ReturnsEmpty()
    {
        var module = new WasmModule();
        var result = DwarfVariableReader.ReadVariables(module, 0);
        Assert.Empty(result);
    }

    // ── Test: Multiple subprograms, correct one matched ────────────────────────

    [Fact]
    public void MultipleSubprograms_MatchesCorrectFunction()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)]),
            new AbbrevDef(5, 0x34, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        // subprogram "add" at func_index=1
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "add");
        WriteU32LE(dies, 1);

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "x");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);

        dies.Add(0); // end add children

        // subprogram "mul" at func_index=2
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "mul");
        WriteU32LE(dies, 2);

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "a");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);

        WriteUleb128(dies, 5);
        WriteInlineString(dies, "result");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 1);

        dies.Add(0); // end mul children
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Get variables for "mul" (func index 2)
        var vars = DwarfVariableReader.ParseVariables(infoData, abbrevData, null, null, null, 2);
        Assert.Equal(2, vars.Count);
        Assert.Equal("a", vars[0].Name);
        Assert.True(vars[0].IsParameter);
        Assert.Equal("result", vars[1].Name);
        Assert.False(vars[1].IsParameter);

        // Get variables for "add" (func index 1)
        var addVars = DwarfVariableReader.ParseVariables(infoData, abbrevData, null, null, null, 1);
        Assert.Single(addVars);
        Assert.Equal("x", addVars[0].Name);
    }

    // ── Test: strp form names ──────────────────────────────────────────────────

    [Fact]
    public void Strp_ResolvesVariableNames()
    {
        var strData = BuildStrSection("test.c", "int", "compute", "val", "acc");
        // Offsets: test.c=0, int=7, compute=11, val=19, acc=23

        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x0e)]),                                      // compile_unit: name(strp)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x0e), (0x0b, 0x0b)]),                        // base_type: name(strp), byte_size(data1)
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x0e), (0x11, 0x01)]),                        // subprogram: name(strp), low_pc(addr)
            new AbbrevDef(4, 0x05, false, [(0x03, 0x0e), (0x49, 0x13), (0x02, 0x18)]),          // formal_parameter
            new AbbrevDef(5, 0x34, false, [(0x03, 0x0e), (0x49, 0x13), (0x02, 0x18)])           // variable
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteU32LE(dies, 0); // strp → "test.c"

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteU32LE(dies, 7); // strp → "int"
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteU32LE(dies, 11); // strp → "compute"
        WriteU32LE(dies, 3); // func index 3

        WriteUleb128(dies, 4);
        WriteU32LE(dies, 19); // strp → "val"
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);

        WriteUleb128(dies, 5);
        WriteU32LE(dies, 23); // strp → "acc"
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 2);

        dies.Add(0); // end subprogram
        dies.Add(0); // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());

        var vars = DwarfVariableReader.ParseVariables(infoData, abbrevData, strData, null, null, 3);
        Assert.Equal(2, vars.Count);
        Assert.Equal("val", vars[0].Name);
        Assert.Equal("int", vars[0].TypeName);
        Assert.Equal("acc", vars[1].Name);
    }

    // ── Test: Real fibonacci.wasm integration ──────────────────────────────────

    // ── Real-wasm fixtures: robust location resolution ─────────────────────────
    //
    // Provenance of the two fixtures (tests/samples/dwarf/*.wasm), both built from
    // the trivial source `int add_probe(int a, int b) { return a + b; }`
    // (spikes/CdpPhase0/sample/add_probe.c) with the wasi-sdk clang toolchain:
    //
    //   add_probe_O1.wasm
    //     clang --target=wasm32-unknown-unknown -O1 -g -nostdlib \
    //           -Wl,--no-entry -Wl,--export=add_probe -Wl,--no-gc-sections
    //     DWARF: DW_AT_low_pc = 0x0 (UNRELOCATED — the minimal --no-entry link
    //     leaves it at zero); subprogram "add_probe" is func index 0; params
    //     a/b are DW_OP_WASM_location(Local) 0/1; frame_base is a stack-value
    //     (DW_OP_WASM_location kind 0x3), i.e. not a simple wasm local.
    //
    //   add_probe_O0.wasm
    //     clang --target=wasm32-unknown-unknown -O0 -g -nostdlib \
    //           -Wl,--no-entry -Wl,--export=add_probe -Wl,--no-gc-sections
    //     DWARF: DW_AT_low_pc = 0x5 (RELOCATED — resolves via the primary
    //     range-match path); the -O0 link adds __wasm_call_ctors so "add_probe"
    //     is func index 1; frame_base = DW_OP_WASM_location(Local) 2 (the C shadow
    //     stack pointer); params a/b are spilled to the shadow stack as
    //     DW_OP_fbreg +12 / +8.

    private static Module.WasmModule LoadModule(string logicalName)
    {
        var asm = System.Reflection.Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ModuleReader.ReadModule(new MemoryStream(ms.ToArray()));
    }

    // (a) Unrelocated DW_AT_low_pc fallback: the primary range-match finds nothing
    // (low_pc=0 matches no code range), and the additive fallback resolves the
    // function's variables by treating low_pc as a direct function index.
    [Fact]
    public void UnrelocatedLowPc_Fallback_ResolvesVariables()
    {
        var module = LoadModule("dwarf/add_probe_O1.wasm");

        var vars = DwarfVariableReader.ReadVariables(module, 0);

        Assert.Equal(2, vars.Count);
        Assert.Equal("a", vars[0].Name);
        Assert.Equal("int", vars[0].TypeName);
        Assert.Equal(0, vars[0].WasmLocalIndex);
        Assert.True(vars[0].IsParameter);
        Assert.Equal("b", vars[1].Name);
        Assert.Equal(1, vars[1].WasmLocalIndex);
        Assert.True(vars[1].IsParameter);
    }

    // (b) ReadVariableInfos classifies an -O1 param that lives in a wasm local
    // (DW_OP_WASM_location) as WasmLocal, carrying the local index.
    [Fact]
    public void ReadVariableInfos_O1Param_ClassifiedAsWasmLocal()
    {
        var module = LoadModule("dwarf/add_probe_O1.wasm");

        var info = DwarfVariableReader.ReadVariableInfos(module, 0);

        Assert.Equal(2, info.Variables.Count);

        var a = info.Variables[0];
        Assert.Equal("a", a.Name);
        Assert.Equal("int", a.TypeName);
        Assert.True(a.IsParameter);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, a.LocationKind);
        Assert.Equal(0, a.LocationValue);

        var b = info.Variables[1];
        Assert.Equal("b", b.Name);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, b.LocationKind);
        Assert.Equal(1, b.LocationValue);
    }

    // (b) ReadVariableInfos classifies an -O0 shadow-stack-spilled param
    // (DW_OP_fbreg) as FrameBaseOffset and exposes the subprogram's frame-base
    // wasm local, so a debugger can read frame_base_local + offset from linear
    // memory. Also serves as the primary (relocated) range-match regression guard
    // for the ReadVariableInfos path.
    [Fact]
    public void ReadVariableInfos_O0SpilledParam_ClassifiedAsFrameBaseOffset()
    {
        var module = LoadModule("dwarf/add_probe_O0.wasm");

        // add_probe is func index 1 at -O0 (func 0 is __wasm_call_ctors).
        var info = DwarfVariableReader.ReadVariableInfos(module, 1);

        Assert.Equal(2, info.FrameBaseLocalIndex);
        Assert.Equal(2, info.Variables.Count);

        var a = info.Variables[0];
        Assert.Equal("a", a.Name);
        Assert.Equal("int", a.TypeName);
        Assert.True(a.IsParameter);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.FrameBaseOffset, a.LocationKind);
        Assert.Equal(12, a.LocationValue);

        var b = info.Variables[1];
        Assert.Equal("b", b.Name);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.FrameBaseOffset, b.LocationKind);
        Assert.Equal(8, b.LocationValue);
    }

    // (c) Additivity guard: when the primary range-match succeeds (low_pc falls
    // inside a code range), resolution goes through the primary path unchanged —
    // proven by using a range whose absIndex (7) differs from low_pc (150). The
    // fallback only fires when the primary path returns empty and would key off
    // low_pc==funcIndex, so a hit at funcIndex=7 could only come from the primary
    // path. Behavior is identical to pre-change for relocated modules.
    [Fact]
    public void PrimaryRangeMatch_StillResolves_FallbackIsAdditive()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "compute");
        WriteU32LE(dies, 150);                       // DW_AT_low_pc = 150 (a code offset)

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "p");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // Code range [100,200) maps to absolute func index 7; low_pc=150 is inside it.
        var funcRanges = new List<(long, long, int)> { (100, 200, 7) };

        var vars = DwarfVariableReader.ParseVariables(
            infoData, abbrevData, null, null, null, 7, funcRanges);

        Assert.Single(vars);
        Assert.Equal("p", vars[0].Name);
        Assert.Equal(0, vars[0].WasmLocalIndex);

        // A non-matching function index still yields nothing (no spurious fallback hit).
        var none = DwarfVariableReader.ParseVariables(
            infoData, abbrevData, null, null, null, 8, funcRanges);
        Assert.Empty(none);
    }

    // Classifier unit tests: exercise every DwarfVarLocationKind branch of
    // ParseLocationExpression deterministically, including the MemoryAddress
    // (DW_OP_addr) case for which no clean in-function fixture exists.
    [Fact]
    public void ParseLocationExpression_ClassifiesAllKinds()
    {
        // DW_OP_WASM_location(Local) index 5 → WasmLocal(5)
        var wasmLocal = DwarfVariableReader.ParseLocationExpression([0xED, 0x00, 5]);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, wasmLocal.Kind);
        Assert.Equal(5L, wasmLocal.Value);

        // Real clang -O1 emits DW_OP_WASM_location followed by a DW_OP_stack_value
        // (0x9F) terminator — that trailing value marker must be tolerated. (item C)
        var wasmLocalStackValue = DwarfVariableReader.ParseLocationExpression([0xED, 0x00, 5, 0x9F]);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, wasmLocalStackValue.Kind);
        Assert.Equal(5L, wasmLocalStackValue.Value);

        // DW_OP_fbreg +12 (SLEB128 0x0C) → FrameBaseOffset(12)
        var fbregPos = DwarfVariableReader.ParseLocationExpression([0x91, 0x0C]);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.FrameBaseOffset, fbregPos.Kind);
        Assert.Equal(12L, fbregPos.Value);

        // DW_OP_fbreg -4 (SLEB128 0x7C) → FrameBaseOffset(-4)
        var fbregNeg = DwarfVariableReader.ParseLocationExpression([0x91, 0x7C]);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.FrameBaseOffset, fbregNeg.Kind);
        Assert.Equal(-4L, fbregNeg.Value);

        // DW_OP_addr 0x00000400 (addrSize 4 default) → MemoryAddress(0x400)
        var addr = DwarfVariableReader.ParseLocationExpression([0x03, 0x00, 0x04, 0x00, 0x00]);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.MemoryAddress, addr.Kind);
        Assert.Equal(0x400L, addr.Value);

        // DW_OP_addr with addrSize 8 (wasm64) reads a full 64-bit address. (item D)
        var addr64 = DwarfVariableReader.ParseLocationExpression(
            [0x03, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00], addrSize: 8);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.MemoryAddress, addr64.Kind);
        Assert.Equal(0x1_00000000L, addr64.Value);

        // Empty / unrecognized → Unknown
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([]).Kind);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0x50, 0x00]).Kind);

        // ── Completeness / robustness (item C) ──────────────────────────────
        // Bare DW_OP_fbreg with no SLEB operand must NOT be a confident
        // FrameBaseOffset(0); a truncated operand → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0x91]).Kind);

        // DW_OP_WASM_location with a truncated ULEB (high bit set, no continuation) → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0xED, 0x00, 0x80]).Kind);

        // DW_OP_WASM_location kind byte present but no index bytes → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0xED, 0x00]).Kind);

        // A complete WASM_location followed by an unrelated trailing opcode
        // (not DW_OP_stack_value) is a multi-op expression we don't model → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0xED, 0x00, 5, 0xAB]).Kind);

        // DW_OP_fbreg followed by DW_OP_stack_value means the value IS frame_base+X
        // (computed/implicit), NOT stored at that address — must NOT be a confident
        // FrameBaseOffset. Only the WasmLocal case tolerates a trailing stack_value.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0x91, 0x0C, 0x9F]).Kind);

        // DW_OP_addr followed by DW_OP_stack_value → value == addr (implicit), not
        // stored AT addr → must NOT be a confident MemoryAddress. → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0x03, 0x00, 0x04, 0x00, 0x00, 0x9F]).Kind);

        // DW_OP_addr with fewer than addrSize operand bytes → Unknown. (item C/D)
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0x03, 0x00, 0x04]).Kind);

        // A non-Local wasm-location kind (e.g. Global) is not modeled → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ParseLocationExpression([0xED, 0x01, 0x00]).Kind);
    }

    // ── #530: the low_pc-as-index fallback must not fire on zeroed DWARF ─────────
    // wasm-opt without -g leaves EVERY subprogram at DW_AT_low_pc 0, outside every code
    // range. The #141 fallback reads low_pc as a function index, so on such DWARF it took
    // the FIRST subprogram at 0 and attached its variables to function 0 — an arbitrary
    // function's variables, reported with confidence. Two subprograms sharing the value
    // prove it is not an index; the fallback must then yield nothing. (The single-subprogram
    // case, UnrelocatedLowPc_Fallback_ResolvesVariables, still resolves.)
    [Fact]
    public void Fallback_RefusesLowPcSharedByManySubprograms_ZeroedDwarf()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "zeroed.c");
        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);
        foreach (var (fn, param) in new[] { ("first", "pf"), ("second", "ps") })
        {
            WriteUleb128(dies, 3);
            WriteInlineString(dies, fn);
            WriteU32LE(dies, 0);                     // DW_AT_low_pc tombstoned to 0
            WriteUleb128(dies, 4);
            WriteInlineString(dies, param);
            WriteU32LE(dies, (uint)intTypeOffset);
            WriteWasmLocationExprloc(dies, 0x00, 0); // a real wasm local, so a wrong match is visible
            dies.Add(0);
        }
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var funcRanges = new List<(long, long, int)> { (100, 200, 0), (200, 300, 1) };

        Assert.Empty(DwarfVariableReader.ParseVariables(infoData, abbrevData, null, null, null, 0, funcRanges));
        var infos = DwarfVariableReader.ParseVariableInfos(infoData, abbrevData, null, null, null, 0, funcRanges);
        Assert.Empty(infos.Variables);
        Assert.Equal(-1, infos.FrameBaseLocalIndex);
    }

    // The real #530 build: wasm-opt zeroed all 130 subprogram addresses. No function of the
    // module may receive variables from that DWARF.
    [Fact]
    public void ZeroedDwarf_RealWasmOptBuild_AttachesNoVariablesToAnyFunction()
    {
        var module = LoadModule("dwarf/sq_O1_wasmopt.wasm");
        int imports = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        Assert.NotEmpty(module.Functions);
        Assert.Contains(module.CustomSections, cs => cs.Name == ".debug_info");
        for (int f = 0; f < imports + module.Functions.Count; f++)
        {
            Assert.Empty(DwarfVariableReader.ReadVariables(module, f));
            Assert.Empty(DwarfVariableReader.ReadVariableInfos(module, f).Variables);
        }
    }

    // DWARF 5 code addresses are DW_FORM_addrx indices into .debug_addr. Read as raw
    // addresses, sq's index (1) is a code offset inside the FIRST function's entry, so sq's
    // parameter was attached to that function. Resolved through .debug_addr, sq's parameter
    // lands on sq and on nothing else. The expected index comes from the DWARF 4 build's name
    // section — an independent source.
    [Fact]
    public void Dwarf5_AddrxLowPc_ResolvesThroughDebugAddr()
    {
        var named = LoadModule("dwarf/sq_O0.wasm");
        int sq = Microsoft.NET.WebAssembly.Debugging.Symbols.ModuleSymbols.For(named).NameSection.FunctionNames
            .Single(kv => kv.Value == "sq").Key;
        var module = LoadModule("dwarf/sq_O0_dwarf5_noname.wasm");
        int imports = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);

        var info = DwarfVariableReader.ReadVariableInfos(module, sq);
        var x = Assert.Single(info.Variables);
        Assert.Equal("x", x.Name);
        Assert.True(x.IsParameter);
        var owners = Enumerable.Range(0, imports + module.Functions.Count)
            .Where(f => DwarfVariableReader.ReadVariableInfos(module, f).Variables.Any(v => v.Name == "x"))
            .ToList();
        Assert.Equal([sq], owners);
    }

    // ── Fix A/B: fallback must not steal another function's variables ───────────
    // Reviewer-identified blocker: when a subprogram RANGE-MATCHES the requested
    // funcIndex but has zero mappable wasm-local vars, the old code fell through to
    // the low_pc-as-index fallback and attached a DIFFERENT subprogram whose raw
    // low_pc value numerically collides with funcIndex. Subprogram A range-matches
    // funcIndex 7 (via code range [100,200)) but its only param is an fbreg spill
    // (no wasm local); subprogram B has low_pc==7 with a real wasm local. Reading
    // funcIndex 7 must return A's result (empty for the WasmLocal-only ParseVariables
    // path; A's own fbreg param for the classifying ParseVariableInfos path) — never
    // B's variables. This test FAILS on pre-fix code and PASSES with fix A.
    [Fact]
    public void Fallback_DoesNotStealOtherFunctionsVars_OnRangeMatchWithNoLocals()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        // Subprogram A: range-matches funcIndex 7 (low_pc 150 ∈ [100,200)), but its
        // only param is a frame-base spill — no wasm local for ParseVariables to keep.
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "funcA");
        WriteU32LE(dies, 150);
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "pa");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteExprloc(dies, [0x91, 0x08]);            // DW_OP_fbreg +8
        dies.Add(0);                                 // end A children

        // Subprogram B: low_pc==7 collides numerically with funcIndex 7, and it DOES
        // have a wasm local. 7 is outside every code range, so it is only reachable
        // through the (now-guarded) fallback.
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "funcB");
        WriteU32LE(dies, 7);
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "pb");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);     // DW_OP_WASM_location local 0
        dies.Add(0);                                 // end B children

        dies.Add(0);                                 // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var funcRanges = new List<(long, long, int)> { (100, 200, 7) };

        // ParseVariables (WasmLocal-only): A matched by range and contributes no
        // wasm-local vars → EMPTY. Must NOT be B's "pb".
        var vars = DwarfVariableReader.ParseVariables(
            infoData, abbrevData, null, null, null, 7, funcRanges);
        Assert.Empty(vars);

        // ParseVariableInfos (classifying): A matched by range and owns "pa" (an
        // fbreg spill) → returns A's own variable, never B's.
        var infos = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges);
        Assert.Single(infos.Variables);
        Assert.Equal("pa", infos.Variables[0].Name);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.FrameBaseOffset, infos.Variables[0].LocationKind);
    }

    // ── Fix (item 3): ReadVariableInfos ZERO-variable range-match is authoritative ─
    // The prior collision test proved authoritativeness for ParseVariables, but for
    // ParseVariableInfos function A owned a valid fbreg var — so it never exercised a
    // truly ZERO-variable range-match for that API. Here A range-matches funcIndex 7
    // with NO children at all; B (low_pc==7) has vars. ParseVariableInfos(7) must
    // return an EMPTY DwarfFunctionVariables (A's own result), never B's variables.
    [Fact]
    public void ReadVariableInfos_ZeroVarRangeMatch_IsAuthoritative_NotFallback()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)])
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        // Subprogram A: range-matches funcIndex 7 (low_pc 150 ∈ [100,200)) with ZERO
        // child variables/params at all.
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "funcA");
        WriteU32LE(dies, 150);
        dies.Add(0);                                 // end A children (no vars)

        // Subprogram B: low_pc==7 collides numerically with funcIndex 7 and DOES have
        // a var; only reachable via the (guarded) fallback.
        WriteUleb128(dies, 3);
        WriteInlineString(dies, "funcB");
        WriteU32LE(dies, 7);
        WriteUleb128(dies, 4);
        WriteInlineString(dies, "pb");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 0);     // DW_OP_WASM_location local 0
        dies.Add(0);                                 // end B children

        dies.Add(0);                                 // end CU

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var funcRanges = new List<(long, long, int)> { (100, 200, 7) };

        var infos = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges);
        Assert.Empty(infos.Variables);
    }

    // ── Loclist (DWARF<=4 .debug_loc) resolver tests ────────────────────────────
    // Deterministic synthetic .debug_loc bytes — never rely on a compiler emitting a
    // location list. addrSize 4 (wasm32) unless noted.

    private const byte AddrSize4 = 4;

    /// <summary>Append a location-list LOCATION entry [begin,end) + exprloc bytes (addr4).</summary>
    private static void WriteLoclistEntry4(List<byte> buf, uint begin, uint end, byte[] expr)
    {
        WriteU32LE(buf, begin);
        WriteU32LE(buf, end);
        WriteU16LE(buf, (ushort)expr.Length);
        buf.AddRange(expr);
    }

    /// <summary>Append a base-address-selection entry (0xFFFFFFFF, newBase) (addr4).</summary>
    private static void WriteLoclistBaseSelect4(List<byte> buf, uint newBase)
    {
        WriteU32LE(buf, 0xFFFFFFFF);
        WriteU32LE(buf, newBase);
    }

    /// <summary>Append the end-of-list terminator (0,0) (addr4).</summary>
    private static void WriteLoclistEnd4(List<byte> buf)
    {
        WriteU32LE(buf, 0);
        WriteU32LE(buf, 0);
    }

    private static byte[] WasmLocalExpr(byte index) => [0xED, 0x00, index]; // DW_OP_WASM_location Local index

    [Fact]
    public void ResolveLoclist_TwoEntries_SelectsByQueryPc()
    {
        // Entry 1: [0x10,0x20) → wasm local 3; Entry 2: [0x20,0x30) → wasm local 7.
        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x10, 0x20, WasmLocalExpr(3));
        WriteLoclistEntry4(loc, 0x20, 0x30, WasmLocalExpr(7));
        WriteLoclistEnd4(loc);
        var data = loc.ToArray();

        var e1 = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x18, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, e1.Kind);
        Assert.Equal(3L, e1.Value);

        var e2 = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x28, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, e2.Kind);
        Assert.Equal(7L, e2.Value);

        // PC outside every entry's range → runs off the end (end-of-list) → Unknown.
        var outside = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x40, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, outside.Kind);

        // PC just below the first range is also uncovered → Unknown.
        var below = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x0F, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, below.Kind);
    }

    [Fact]
    public void ResolveLoclist_NonZeroBaseAddress_ShiftsRanges()
    {
        // With base=0x100 the entry [0x00,0x10) covers [0x100,0x110).
        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x00, 0x10, WasmLocalExpr(2));
        WriteLoclistEnd4(loc);
        var data = loc.ToArray();

        var hit = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0x100, queryPc: 0x108, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, hit.Kind);
        Assert.Equal(2L, hit.Value);

        // The raw (unbased) PC 0x08 is NOT covered once the base is applied.
        var miss = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0x100, queryPc: 0x08, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, miss.Kind);
    }

    [Fact]
    public void ResolveLoclist_BaseAddressSelectionEntry_ShiftsSubsequentRanges()
    {
        // A base-address-selection entry sets base=0x1000; the following entry
        // [0x20,0x40) then covers [0x1020,0x1040).
        var loc = new List<byte>();
        WriteLoclistBaseSelect4(loc, 0x1000);
        WriteLoclistEntry4(loc, 0x20, 0x40, WasmLocalExpr(9));
        WriteLoclistEnd4(loc);
        var data = loc.ToArray();

        var hit = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x1030, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, hit.Kind);
        Assert.Equal(9L, hit.Value);

        // Before the base shift is applied, 0x30 is not covered.
        var miss = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x30, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, miss.Kind);
    }

    [Fact]
    public void ResolveLoclist_EndOfListTerminates()
    {
        // Single entry then (0,0). A PC past the covered range must terminate at the
        // end-of-list marker and report Unknown (not read past it).
        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x10, 0x20, WasmLocalExpr(1));
        WriteLoclistEnd4(loc);
        // Trailing garbage after the terminator must never be interpreted.
        WriteU32LE(loc, 0xDEADBEEF);
        var data = loc.ToArray();

        var res = DwarfVariableReader.ResolveLoclistLocation(data, 0, baseAddress: 0, queryPc: 0x50, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, res.Kind);
    }

    [Fact]
    public void ResolveLoclist_MalformedInputs_YieldUnknown_NoThrow()
    {
        var good = new List<byte>();
        WriteLoclistEntry4(good, 0x10, 0x20, WasmLocalExpr(3));
        WriteLoclistEnd4(good);
        var data = good.ToArray();

        // null / empty section.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation(null, 0, 0, 0x18, AddrSize4).Kind);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation([], 0, 0, 0x18, AddrSize4).Kind);

        // Offset out of range (negative and past end).
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation(data, -1, 0, 0x18, AddrSize4).Kind);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation(data, data.Length, 0, 0x18, AddrSize4).Kind);

        // Truncated header (only 3 bytes — cannot read begin+end).
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation([0x00, 0x00, 0x00], 0, 0, 0x18, AddrSize4).Kind);

        // A covering entry whose declared expr length runs off the end → Unknown.
        var truncatedExpr = new List<byte>();
        WriteU32LE(truncatedExpr, 0x10);
        WriteU32LE(truncatedExpr, 0x20);
        WriteU16LE(truncatedExpr, 0x00FF); // claims 255 expr bytes …
        truncatedExpr.Add(0xED);           // … but only 1 present.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation(truncatedExpr.ToArray(), 0, 0, 0x18, AddrSize4).Kind);

        // Unsupported address size → Unknown.
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown,
            DwarfVariableReader.ResolveLoclistLocation(data, 0, 0, 0x18, addrSize: 2).Kind);
    }

    [Fact]
    public void ResolveLoclist_CoveringEntryWithUnsupportedExpr_YieldsUnknown()
    {
        // The entry covers the PC, but its expression is a form ParseLocationExpression
        // does not classify (a bare, unrecognized opcode). Coverage found, kind Unknown.
        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x00, 0x100, [0x50, 0x00]); // DW_OP_reg0 … (not modeled)
        WriteLoclistEnd4(loc);

        var res = DwarfVariableReader.ResolveLoclistLocation(loc.ToArray(), 0, 0, 0x08, AddrSize4);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, res.Kind);
    }

    // ── Integration: DW_AT_location as a .debug_loc offset via ParseVariableInfos ──
    // A formal_parameter whose DW_AT_location uses DW_FORM_sec_offset (0x17) is a
    // NUMERIC offset into .debug_loc, not an inline expression. ParseVariableInfos
    // must resolve it against the query PC (defaulting to the subprogram's low_pc).
    [Fact]
    public void ParseVariableInfos_LoclistParam_ResolvesAtEntryPc_AndByExplicitPc()
    {
        // subprogram low_pc = 5 (synthetic index); its param's DW_AT_location is a
        // sec_offset (=0) into .debug_loc. Entry 1 [0x00,0x08) → local 4 covers the
        // default entry PC (5); entry 2 [0x08,0x20) → local 9.
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x17)]) // location(sec_offset)
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "frame");
        WriteU32LE(dies, 5); // low_pc = index 5 → default query PC

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "p");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteU32LE(dies, 0);                          // DW_AT_location = .debug_loc offset 0

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());

        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x00, 0x08, WasmLocalExpr(4));
        WriteLoclistEntry4(loc, 0x08, 0x20, WasmLocalExpr(9));
        WriteLoclistEnd4(loc);
        var locData = loc.ToArray();

        // Default query PC = subprogram low_pc (5) → first entry → local 4.
        var atEntry = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 5, funcRanges: null, locData: locData);
        Assert.Single(atEntry.Variables);
        Assert.Equal("p", atEntry.Variables[0].Name);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atEntry.Variables[0].LocationKind);
        Assert.Equal(4L, atEntry.Variables[0].LocationValue);

        // Explicit query PC 0x10 selects the second entry → local 9.
        var atPc = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 5, funcRanges: null, locData: locData, queryCodeOffset: 0x10);
        Assert.Single(atPc.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atPc.Variables[0].LocationKind);
        Assert.Equal(9L, atPc.Variables[0].LocationValue);

        // A query PC covered by no entry → Unknown (variable still surfaced).
        var uncovered = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 5, funcRanges: null, locData: locData, queryCodeOffset: 0x100);
        Assert.Single(uncovered.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, uncovered.Variables[0].LocationKind);

        // No .debug_loc section at all → the numeric location cannot resolve → Unknown.
        var noLoc = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 5, funcRanges: null, locData: null);
        Assert.Single(noLoc.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, noLoc.Variables[0].LocationKind);
    }

    // Regression: an inline exprloc DW_AT_location still classifies via the unchanged
    // path when a .debug_loc section is ALSO present (the loclist path must not
    // hijack byte[] locations).
    [Fact]
    public void ParseVariableInfos_InlineExprloc_StillResolves_WithLocSectionPresent()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08)]),
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x18)]) // location(exprloc)
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "compute");
        WriteU32LE(dies, 3);

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "v");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteWasmLocationExprloc(dies, 0x00, 6);      // inline DW_OP_WASM_location local 6

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());

        // A bogus .debug_loc that would misresolve if the inline path were bypassed.
        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0x00, 0x100, WasmLocalExpr(99));
        WriteLoclistEnd4(loc);

        var infos = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 3, funcRanges: null, locData: loc.ToArray());
        Assert.Single(infos.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, infos.Variables[0].LocationKind);
        Assert.Equal(6L, infos.Variables[0].LocationValue); // inline local 6, NOT loclist local 99
    }

    // ── Address-space integration: real funcRanges + non-zero CU base ────────────
    // These exercise the REAL match path (funcRanges != null), which the resolver-unit
    // and synthetic (funcRanges:null) tests bypass. They prove the query PC and loclist
    // ranges are reconciled into ONE space (the CU/code-section space), per round-2.

    // PRIMARY (relocated) match: subprogram DW_AT_low_pc is a real code PC (150) inside
    // the matched function's range [100,200); CU DW_AT_low_pc = 100 is the loclist base.
    // Entry1 [50,60) → DWARF PC [150,160) covers the entry-PC default (150); entry2
    // [70,80) → [170,180) is reached only with an explicit PC (175).
    [Fact]
    public void ParseVariableInfos_Primary_RealRanges_NonZeroCuBase_ResolvesInCodeSpace()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08), (0x11, 0x01)]),                 // compile_unit: name, low_pc(addr)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),                 // base_type
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),                 // subprogram: name, low_pc(addr)
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x17)])    // formal_parameter: name, type, location(sec_offset)
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");
        WriteU32LE(dies, 100);                       // CU DW_AT_low_pc = 100 (loclist base)

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "frame");
        WriteU32LE(dies, 150);                       // subprogram DW_AT_low_pc = 150 (real code PC in [100,200))

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "p");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteU32LE(dies, 0);                         // DW_AT_location = .debug_loc offset 0

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var funcRanges = new List<(long, long, int)> { (100, 200, 7) };

        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 50, 60, WasmLocalExpr(4)); // base 100 → DWARF PC [150,160)
        WriteLoclistEntry4(loc, 70, 80, WasmLocalExpr(9)); // base 100 → DWARF PC [170,180)
        WriteLoclistEnd4(loc);
        var locData = loc.ToArray();

        // Default entry PC (subprogram low_pc = 150) → entry1 → local 4.
        var atEntry = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges, locData);
        Assert.Single(atEntry.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atEntry.Variables[0].LocationKind);
        Assert.Equal(4L, atEntry.Variables[0].LocationValue);

        // Explicit code-section PC 175 (covers entry2 but NOT entry1) → local 9.
        var atPc = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges, locData, queryCodeOffset: 175);
        Assert.Single(atPc.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atPc.Variables[0].LocationKind);
        Assert.Equal(9L, atPc.Variables[0].LocationValue);
    }

    // FALLBACK (#141 unrelocated) match: subprogram DW_AT_low_pc = 7 is a FUNCTION INDEX
    // (outside the code range [100,200)), CU low_pc = 0. The fix anchors loclist
    // resolution at the matched function's code-section START (100), NOT the index (7).
    // Entry1 [0,5) → code range [100,105) covers the entry default (100); under the OLD
    // buggy default (query = lowPc = 7, base = cuBaseLowPc = 0 → covers [0,5), 7 excluded)
    // this resolved as Unknown. So a WasmLocal result here PROVES the round-2 fix.
    [Fact]
    public void ParseVariableInfos_Fallback_UnrelocatedLowPc_AnchorsAtCodeStart_NotIndex()
    {
        var abbrevData = BuildAbbrevSection(
            new AbbrevDef(1, 0x11, true,  [(0x03, 0x08), (0x11, 0x01)]),                 // compile_unit: name, low_pc(addr)
            new AbbrevDef(2, 0x24, false, [(0x03, 0x08), (0x0b, 0x0b)]),                 // base_type
            new AbbrevDef(3, 0x2e, true,  [(0x03, 0x08), (0x11, 0x01)]),                 // subprogram: name, low_pc(addr)
            new AbbrevDef(4, 0x05, false, [(0x03, 0x08), (0x49, 0x13), (0x02, 0x17)])    // formal_parameter: name, type, location(sec_offset)
        );

        var dies = new List<byte>();
        WriteUleb128(dies, 1);
        WriteInlineString(dies, "test.c");
        WriteU32LE(dies, 0);                         // CU DW_AT_low_pc = 0 (unrelocated)

        int intTypeOffset = 11 + dies.Count;
        WriteUleb128(dies, 2);
        WriteInlineString(dies, "int");
        dies.Add(4);

        WriteUleb128(dies, 3);
        WriteInlineString(dies, "frame");
        WriteU32LE(dies, 7);                         // subprogram DW_AT_low_pc = 7 (INDEX, outside [100,200))

        WriteUleb128(dies, 4);
        WriteInlineString(dies, "p");
        WriteU32LE(dies, (uint)intTypeOffset);
        WriteU32LE(dies, 0);                         // DW_AT_location = .debug_loc offset 0

        dies.Add(0);
        dies.Add(0);

        var infoData = BuildInfoSection_D4(dies.ToArray());
        var funcRanges = new List<(long, long, int)> { (100, 200, 7) };

        var loc = new List<byte>();
        WriteLoclistEntry4(loc, 0, 5, WasmLocalExpr(4));   // base 100 → [100,105); excludes OLD query 7
        WriteLoclistEntry4(loc, 50, 60, WasmLocalExpr(9)); // base 100 → [150,160)
        WriteLoclistEnd4(loc);
        var locData = loc.ToArray();

        // Default: fallback anchors at code start 100 → entry1 [100,105) covers 100 → local 4.
        // (Old behavior would have compared index 7 against base-0 ranges → Unknown.)
        var atEntry = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges, locData);
        Assert.Single(atEntry.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atEntry.Variables[0].LocationKind);
        Assert.Equal(4L, atEntry.Variables[0].LocationValue);

        // Explicit code-section PC 155 (a real paused offset) → entry2 [150,160) → local 9.
        var atPc = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges, locData, queryCodeOffset: 155);
        Assert.Single(atPc.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.WasmLocal, atPc.Variables[0].LocationKind);
        Assert.Equal(9L, atPc.Variables[0].LocationValue);

        // A code-section PC covered by no entry → Unknown (honest, no mixed-space guess).
        var uncovered = DwarfVariableReader.ParseVariableInfos(
            infoData, abbrevData, null, null, null, 7, funcRanges, locData, queryCodeOffset: 300);
        Assert.Single(uncovered.Variables);
        Assert.Equal(DwarfVariableReader.DwarfVarLocationKind.Unknown, uncovered.Variables[0].LocationKind);
    }

    // ── Binary helpers ─────────────────────────────────────────────────────────

    /// <summary>Write a DW_OP_WASM_location exprloc (DW_FORM_exprloc format).</summary>
    private static void WriteWasmLocationExprloc(List<byte> buf, byte kind, uint index)
    {
        // Build the expression bytes: DW_OP_WASM_location(0xED) + kind + ULEB128(index)
        var expr = new List<byte>();
        expr.Add(0xED); // DW_OP_WASM_location
        expr.Add(kind);
        WriteUleb128(expr, index);

        // exprloc: ULEB128 length followed by expression bytes
        WriteUleb128(buf, (uint)expr.Count);
        buf.AddRange(expr);
    }

    /// <summary>Write an arbitrary exprloc (DW_FORM_exprloc): ULEB128 length + raw bytes.</summary>
    private static void WriteExprloc(List<byte> buf, byte[] expr)
    {
        WriteUleb128(buf, (uint)expr.Length);
        buf.AddRange(expr);
    }

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

    private static void WriteInlineString(List<byte> buf, string s)
    {
        buf.AddRange(System.Text.Encoding.UTF8.GetBytes(s));
        buf.Add(0);
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
}
