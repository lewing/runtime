// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

public partial class WatCompilerTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(7)]
    public void CompileMemory_RejectsMalformedAlignment(int alignment)
    {
        string wat = $"(module (memory 1) (func (drop (i32.load align={alignment} (i32.const 0)))))";

        Assert.Throws<WatException>(() => WatCompiler.Compile(wat));
    }

    [Fact]
    public void CompileMemory_AlignmentEncodingCoversExplicitAndDefaultNatural()
    {
        const string wat = "(module (memory 1) (func (drop (i32.load align=8 (i32.const 0)))))";

        byte[] bytes = WatCompiler.Compile(wat);

        Assert.NotEmpty(bytes);

        foreach (var mnemonic in MemoryLoadStoreMnemonics())
            AssertDefaultAlignmentValidates(mnemonic);
    }

    private static IEnumerable<string> MemoryLoadStoreMnemonics()
        => OpcodeTable.AllMnemonics
            .Select(mnemonic => (mnemonic, entry: OpcodeTable.Get(mnemonic)))
            .Where(item => item.entry.Immediate is ImmediateKind.MemArg or ImmediateKind.MemArgLane)
            .Where(item =>
            {
                var operation = item.mnemonic[(item.mnemonic.IndexOf('.') + 1)..];
                return operation.StartsWith("load", StringComparison.Ordinal)
                    || operation.StartsWith("store", StringComparison.Ordinal);
            })
            .OrderBy(item => item.mnemonic, StringComparer.Ordinal)
            .Select(item => item.mnemonic);

    private static void AssertDefaultAlignmentValidates(string mnemonic)
    {
        string wat = BuildMemoryLoadStoreModule(mnemonic);

        byte[] bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModule(
            new MemoryStream(bytes),
            new ModuleReaderOptions { Features = WasmFeatures.All });
        var result = FunctionBodyValidator.ValidateModule(module, WasmFeatures.All);

        Assert.True(
            result.IsValid,
            $"{mnemonic}:{Environment.NewLine}{string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message))}");
    }

    private static string BuildMemoryLoadStoreModule(string mnemonic)
    {
        var operation = mnemonic[(mnemonic.IndexOf('.') + 1)..];
        var isStore = operation.StartsWith("store", StringComparison.Ordinal);
        var isLane = OpcodeTable.Get(mnemonic).Immediate == ImmediateKind.MemArgLane;

        var body = isStore
            ? StoreBody(mnemonic, isLane)
            : LoadBody(mnemonic, isLane);

        return $$"""
            (module
              (memory 1)
              (func
            {{body}}
              ))
            """;
    }

    private static string LoadBody(string mnemonic, bool isLane)
    {
        if (isLane)
        {
            return $$"""
                    i32.const 0
                    v128.const i32x4 0 0 0 0
                    {{mnemonic}} 0
                    drop
            """;
        }

        return $$"""
                i32.const 0
                {{mnemonic}}
                drop
        """;
    }

    private static string StoreBody(string mnemonic, bool isLane)
    {
        var value = (
            mnemonic.StartsWith("i64.", StringComparison.Ordinal),
            mnemonic.StartsWith("f32.", StringComparison.Ordinal),
            mnemonic.StartsWith("f64.", StringComparison.Ordinal),
            mnemonic.StartsWith("v128.", StringComparison.Ordinal)) switch
        {
            (true, _, _, _) => "i64.const 0",
            (_, true, _, _) => "f32.const 0",
            (_, _, true, _) => "f64.const 0",
            (_, _, _, true) => "v128.const i32x4 0 0 0 0",
            _ => "i32.const 0",
        };

        return $$"""
                i32.const 0
                {{value}}
                {{mnemonic}}{{(isLane ? " 0" : "")}}
        """;
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

    [Fact]
    public void Compile_RejectsMultipleStartDeclarations()
    {
        const string wat = "(module (func $a) (func $b) (start $a) (start $b))";

        Assert.Throws<WatException>(() => WatCompiler.Compile(wat));
    }

    [Theory]
    [InlineData("(module (func $main) (start $main))")]
    [InlineData("(module (func) (start 0))")]
    public void Compile_PreservesStartDeclaration(string wat)
    {
        byte[] bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.Equal(0, module.StartFunction);
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
