// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using System.Diagnostics;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Wat;

public partial class WasmDisassemblerTests
{
    private const string SparseNamedLocalsWat = """
        (module $m
          (func $f
            (param $p1 i32) (param i64) (param $"p 3" f32)
            (local $l1 f64) (local i32) (local $"l 3" i64)
            local.get $p1
            local.get 1
            local.get $"p 3"
            local.get $l1
            local.get 4
            local.get $"l 3"
            drop drop drop drop drop drop))
        """;

    [Fact]
    public void SimpleAdd_RoundTrips()
    {
        var wat = "(module (func (export \"add\") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        // The disassembled WAT should recompile
        var bytes2 = WatCompiler.Compile(disasm);
        var module2 = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes2));

        // And the second disassembly should match the first
        var disasm2 = WasmDisassembler.Disassemble(module2);
        Assert.Equal(disasm, disasm2);
    }

    [Fact]
    public void Memory_RoundTrips()
    {
        var wat = "(module (memory (export \"mem\") 1 256))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var bytes2 = WatCompiler.Compile(disasm);
        var module2 = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes2));
        Assert.Equal(1u, module2.Memories[0].Limits.Min);
        Assert.Equal(256u, module2.Memories[0].Limits.Max);
    }

    [Fact]
    public void GlobalConst_RoundTrips()
    {
        var wat = "(module (global (export \"g\") i32 (i32.const 42)))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.ValType.I32, module2.Globals[0].Type.ContentType);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.Mutability.Const, module2.Globals[0].Type.Mutability);
    }

    [Fact]
    public void GlobalMut_RoundTrips()
    {
        var wat = "(module (global (export \"g\") (mut i32) (i32.const 0)))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.Mutability.Var, module2.Globals[0].Type.Mutability);
    }

    [Fact]
    public void DataSegment_RoundTrips()
    {
        var wat = @"(module
          (memory 1)
          (data (i32.const 0) ""hello""))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Single(module2.DataSegments);
        Assert.Equal("hello"u8.ToArray(), module2.DataSegments[0].InitData);
    }

    [Fact]
    public void Import_RoundTrips()
    {
        var wat = @"(module
          (import ""env"" ""log"" (func (param i32))))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Single(module2.Imports);
        Assert.Equal("env", module2.Imports[0].ModuleName);
        Assert.Equal("log", module2.Imports[0].Name);
    }

    [Fact]
    public void RoundTrip_V128Locals()
    {
        var wat = "(module (func (local v128) v128.const i32x4 0 0 0 0 local.set 0))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);
        Assert.Contains("v128", disasm);
        var bytes2 = WatCompiler.Compile(disasm);
        Assert.Equal(bytes.Length, bytes2.Length);
    }

    // ── F4: module name emitted by WasmDisassembler ───────────────────────────

    /// <summary>
    /// F4: DisassemblyContext.ModuleName is populated by the name-section reader
    /// but WasmDisassembler.Disassemble writes "(module" unconditionally and never
    /// reads it. The module name must appear in the disassembly output.
    /// This test goes through the production WasmDisassembler path, NOT a private helper.
    /// </summary>
    [Fact]
    public void Disassemble_PreservesModuleName_ViaNameSection()
    {
        // Compile a module with a $foo binder (writes name-section subsection 0).
        var bytes = WatCompiler.Compile("(module $foo (func))");

        // WasmDisassembler reads the binary (including the name section) and disassembles.
        var disasm = WasmDisassembler.Disassemble(bytes);

        // The module name must appear in the disassembly.
        Assert.Contains("$foo", disasm);
    }

    [Fact]
    public void Disassemble_PreservesModuleName_SpaceInName_ViaAnnotation()
    {
        // Quoted identifiers preserve arbitrary non-empty names directly.
        var bytes = WatCompiler.Compile("""(module (@name "foo bar") (func))""");
        var disasm = WasmDisassembler.Disassemble(bytes);

        Assert.Contains("$\"foo bar\"", disasm);
        Assert.DoesNotContain("@name", disasm);
    }

    // ── #256: function name quoting ───────────────────────────────────────────

    /// <summary>
    /// A function name containing a space is not a valid WAT bare identifier.
    /// The disassembler must emit (@name "foo bar"), not $foo bar.
    /// Then the output must recompile cleanly (full round-trip).
    ///
    /// BREAK-PROOF: if the disassembler emits $foo bar, the recompile will
    /// throw a WatException (invalid identifier) and the round-trip Assert fails.
    /// </summary>
    [Fact]
    public void Disassemble_FuncName_WithSpace_UsesQuotedIdentifier()
    {
        var bytes = WatCompiler.Compile("""(module (func (@name "foo bar")))""");
        var disasm = WasmDisassembler.Disassemble(bytes);

        // Must NOT emit the bare unquoted form (which is invalid WAT)
        Assert.DoesNotContain("$foo bar", disasm);
        // Wasm 3.0 quoted identifiers are preferable to an annotation fallback.
        Assert.Contains("$\"foo bar\"", disasm);
        Assert.DoesNotContain("@name", disasm);
        // Must round-trip: the disassembly must be recompilable
        var bytes2 = WatCompiler.Compile(disasm);
        using var ms = new MemoryStream(bytes2);
        ModuleReader.ReadModuleAndValidate(ms);
    }

    /// <summary>
    /// A function name containing a double-quote must be escaped in the quoted identifier.
    /// BREAK-PROOF: without Escape() the raw quote would terminate the string early.
    /// </summary>
    [Fact]
    public void Disassemble_FuncName_WithQuote_EscapedInQuotedIdentifier()
    {
        var bytes = WatCompiler.Compile("""(module (func (@name "say \"hi\"")))""");
        var disasm = WasmDisassembler.Disassemble(bytes);

        Assert.Contains("$\"say \\\"hi\\\"\"", disasm);
        Assert.DoesNotContain("@name", disasm);
        // Must round-trip
        var bytes2 = WatCompiler.Compile(disasm);
        using var ms = new MemoryStream(bytes2);
        ModuleReader.ReadModuleAndValidate(ms);
    }

    /// <summary>
    /// A valid identifier must still use the $name form (no regression).
    /// </summary>
    [Fact]
    public void Disassemble_FuncName_ValidIdentifier_UsesDollarForm()
    {
        var bytes = WatCompiler.Compile("(module (func $foo))");
        var disasm = WasmDisassembler.Disassemble(bytes);

        Assert.Contains("$foo", disasm);
        Assert.DoesNotContain("@name", disasm);
    }

    /// <summary>
    /// An import with a valid $id name must still use the $name form in the import declaration
    /// (tests that EmitImports correctly looks up names from the FuncNames map).
    /// BREAK-PROOF: if the name lookup in EmitImports is broken, $importedFunc won't appear.
    /// </summary>
    [Fact]
    public void Disassemble_ImportedFuncName_ValidIdentifier_AppearsInOutput()
    {
        var bytes = WatCompiler.Compile("""(module (import "m" "f" (func $importedFunc (param i32))))""");
        var disasm = WasmDisassembler.Disassemble(bytes);

        Assert.Contains("$importedFunc", disasm);
    }

    [Fact]
    public void Disassemble_PreservesSparseParamAndLocalNames()
    {
        var bytes = WatCompiler.Compile(SparseNamedLocalsWat);

        var disasm = WasmDisassembler.Disassemble(bytes);

        AssertNameBindings(disasm);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        AssertNameBindings(WasmDisassembler.DisassembleFunction(module, exportFunctionIndex: 0));

        var roundTrippedBytes = WatCompiler.Compile(disasm);
        var originalNameSection = ReadNameSection(bytes);
        var roundTrippedNameSection = ReadNameSection(roundTrippedBytes);
        Assert.Equal(originalNameSection, roundTrippedNameSection);
    }

    [Fact]
    public void Disassemble_PreservesImportedParamNames()
    {
        var moduleBytes = WatCompiler.Compile(
            """(module (import "m" "f" (func (param i32 i64))))""");
        byte[] importedParamNameSection =
        [
            0x00, 0x0d,                         // custom section, 13-byte payload
            0x04, (byte)'n', (byte)'a', (byte)'m', (byte)'e',
            0x02, 0x06,                         // local-name subsection, 6-byte payload
            0x01, 0x00, 0x01,                   // function 0, one named local
            0x00, 0x01, (byte)'p',              // local 0 = "p"
        ];
        var bytes = moduleBytes.Concat(importedParamNameSection).ToArray();

        var disasm = WasmDisassembler.Disassemble(bytes);

        Assert.Contains("(param $p i32) (param i64)", disasm);
        WatCompiler.Compile(disasm);
    }

    private static void AssertNameBindings(string disassembly)
    {
        Assert.Contains("(param $p1 i32) (param i64) (param $\"p 3\" f32)", disassembly);
        Assert.Contains("(local $l1 f64)", disassembly);
        Assert.Contains("(local i32)", disassembly);
        Assert.Contains("(local $\"l 3\" i64)", disassembly);
        Assert.Contains("local.get $p1", disassembly);
        Assert.Contains("local.get 1", disassembly);
        Assert.Contains("local.get $\"p 3\"", disassembly);
        Assert.Contains("local.get $l1", disassembly);
        Assert.Contains("local.get 4", disassembly);
        Assert.Contains("local.get $\"l 3\"", disassembly);
    }

    private static byte[] ReadNameSection(byte[] bytes)
    {
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        return module.CustomSections.Single(section => section.Name == "name").Content;
    }
}
