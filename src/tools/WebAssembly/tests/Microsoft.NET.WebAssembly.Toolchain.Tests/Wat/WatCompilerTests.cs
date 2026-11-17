// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

public class WatCompilerTests
{
    private static string LoadWat(string name) => File.ReadAllText(WasmAssert.TestAssetPath(name));

    // ── CompileFactorial ──

    [Fact]
    public void CompileFactorial()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("factorial.wat"));

        WasmAssert.ExportedFunction(module, "fac", [ValType.I32], [ValType.I32]);
    }

    [Fact]
    public void CompileFlatIfWithResult()
    {
        var wat = """
            (module
              (func (export "add") (param i32 i32) (result i32)
                local.get 0
                local.get 1
                i32.add)
              (func (export "factorial") (param i64) (result i64)
                local.get 0
                i64.const 1
                i64.le_s
                if (result i64)
                  i64.const 1
                else
                  local.get 0
                  local.get 0
                  i64.const 1
                  i64.sub
                  call 1
                  i64.mul
                end))
            """;

        var module = WatCompiler.CompileAndValidate(wat);

        WasmAssert.ExportedFunction(module, "add", [ValType.I32, ValType.I32], [ValType.I32]);
        WasmAssert.ExportedFunction(module, "factorial", [ValType.I64], [ValType.I64]);
    }

    // ── CompileMemory ──

    [Fact]
    public void CompileMemory()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("memory.wat"));

        WasmAssert.ExportedFunction(module, "store_and_load", [ValType.I32, ValType.I32], [ValType.I32]);
        Assert.NotEmpty(module.Memories);
    }

    // ── RoundTrip ──

    [Fact]
    public void RoundTrip_FactorialExportsMatch()
    {
        var wat = LoadWat("factorial.wat");
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.True(module.Exports.ContainsKey("fac"), "Should export 'fac'");
        Assert.True(module.Exports.ContainsKey("fib"), "Should export 'fib'");
        Assert.Equal(2, module.Exports.Count);
    }

    // ── LexerBasics ──

    [Fact]
    public void LexerBasics_TokenizesCorrectly()
    {
        var source = "(module (func $add (param i32 i32) (result i32) (i32.add (local.get 0) (local.get 1))))";
        var lexer = new WatLexer(source);
        var tokens = lexer.TokenizeAll();

        // Verify key tokens
        Assert.Equal(WatTokenKind.LParen, tokens[0].Kind);          // (
        Assert.Equal(WatTokenKind.Keyword, tokens[1].Kind);          // module
        Assert.Equal("module", tokens[1].Value);
        Assert.Equal(WatTokenKind.LParen, tokens[2].Kind);          // (
        Assert.Equal(WatTokenKind.Keyword, tokens[3].Kind);          // func
        Assert.Equal(WatTokenKind.Identifier, tokens[4].Kind);      // $add
        Assert.Equal("$add", tokens[4].Value);
        Assert.Equal(WatTokenKind.LParen, tokens[5].Kind);          // (
        Assert.Equal(WatTokenKind.Keyword, tokens[6].Kind);          // param
        Assert.Equal(WatTokenKind.Keyword, tokens[7].Kind);          // i32
        Assert.Equal(WatTokenKind.Keyword, tokens[8].Kind);          // i32
        Assert.Equal(WatTokenKind.RParen, tokens[9].Kind);          // )

        // Should end with EOF
        Assert.Equal(WatTokenKind.EOF, tokens[^1].Kind);
    }

    // ── EmptyModule ──

    [Fact]
    public void EmptyModule_ProducesValidWasm()
    {
        var bytes = WatCompiler.Compile("(module)");

        // Valid wasm header: 8 bytes magic + version
        Assert.True(bytes.Length >= 8);
        Assert.Equal(0x00, bytes[0]);
        Assert.Equal(0x61, bytes[1]);
        Assert.Equal(0x73, bytes[2]);
        Assert.Equal(0x6D, bytes[3]);
        Assert.Equal(0x01, bytes[4]);
        Assert.Equal(0x00, bytes[5]);
        Assert.Equal(0x00, bytes[6]);
        Assert.Equal(0x00, bytes[7]);

        // Should pass validation
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        Assert.Empty(module.Functions);
        Assert.Empty(module.Exports);
    }

    // ── Fibonacci via WAT compilation ──

    [Fact]
    public void CompileFibonacci()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("factorial.wat"));

        WasmAssert.ExportedFunction(module, "fib", [ValType.I32], [ValType.I32]);
    }

    // ── Comments are properly skipped ──

    [Fact]
    public void CommentsAreSkipped()
    {
        var wat = @"
(module
  ;; This is a line comment
  (func (export ""id"") (param i32) (result i32)
    (; block comment ;)
    local.get 0
  )
)";
        var module = WatCompiler.CompileAndValidate(wat);

        WasmAssert.ExportedFunction(module, "id", [ValType.I32], [ValType.I32]);
    }

    // ── Analyze: diagnostics, no throw ──

    [Fact]
    public void Analyze_IncompleteModule_ReturnsDiagnosticsWithoutThrowing()
    {
        var result = WatCompiler.Analyze("(module (func");
        Assert.True(result.Diagnostics.Count > 0, "Expected at least one diagnostic");
        // Must not throw — just having diagnostics is sufficient
    }

    [Fact]
    public void Analyze_ValidModule_NoDiagnostics()
    {
        var result = WatCompiler.Analyze("(module)");
        Assert.Empty(result.Diagnostics);
        Assert.NotNull(result.Module);
    }

    [Fact]
    public void Analyze_UnknownFunc_ReturnsDiagnostic()
    {
        var result = WatCompiler.Analyze("(module (func (result i32) (call $missing)))");
        Assert.True(result.Diagnostics.Count > 0);
    }

    // ── TextReader overloads ──

    [Fact]
    public void Compile_FromTextReader()
    {
        var wat = "(module (func (export \"add\") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))";
        using var reader = new StringReader(wat);
        var bytes = WatCompiler.Compile(reader);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        Assert.Single(module.Functions);
    }
}
