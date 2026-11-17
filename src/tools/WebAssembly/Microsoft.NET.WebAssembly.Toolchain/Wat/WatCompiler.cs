// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Compiles WebAssembly text format (WAT) to binary wasm modules.
/// </summary>
public static class WatCompiler
{
    /// <summary>Compile WAT text to wasm binary bytes.</summary>
    public static byte[] Compile(string watText, bool emitDebugInfo = false, string sourceFile = "<wat>")
    {
        var ms = new MemoryStream();
        Compile(watText, ms, emitDebugInfo, sourceFile);
        return ms.ToArray();
    }

    /// <summary>Compile WAT text and write the wasm binary directly to a stream.</summary>
    public static void Compile(string watText, Stream output, bool emitDebugInfo = false, string sourceFile = "<wat>")
    {
        var lexer = new WatLexer(watText);
        var tokens = lexer.TokenizeAll();
        var parser = new WatParser(tokens);
        var ast = parser.ParseModule();
        var resolver = new WatResolver(ast);
        var resolved = resolver.Resolve();
        var encoder = new WasmEncoder(resolved);
        encoder.Encode(output, emitDebugInfo, sourceFile);
    }

    /// <summary>Compile WAT from a <see cref="TextReader"/> to wasm binary bytes.</summary>
    public static byte[] Compile(TextReader reader, bool emitDebugInfo = false, string sourceFile = "<wat>") =>
        Compile(ReadAll(reader), emitDebugInfo, sourceFile);

    /// <summary>Compile WAT from a <see cref="TextReader"/> and write the wasm binary directly to a stream.</summary>
    public static void Compile(TextReader reader, Stream output, bool emitDebugInfo = false, string sourceFile = "<wat>") =>
        Compile(ReadAll(reader), output, emitDebugInfo, sourceFile);

    /// <summary>Compile WAT text to a WasmModule ready to instantiate.</summary>
    public static WasmModule CompileAndValidate(string watText)
    {
        var bytes = Compile(watText);
        return ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
    }

    /// <summary>Compile WAT from a <see cref="TextReader"/> to a WasmModule ready to instantiate.</summary>
    public static WasmModule CompileAndValidate(TextReader reader) => CompileAndValidate(ReadAll(reader));

    /// <summary>
    /// Analyze WAT source without throwing. Collects diagnostics and returns a partial
    /// AST where possible — suitable for LSP use on incomplete or erroneous code.
    /// </summary>
    public static WatCompileResult Analyze(TextReader reader) => Analyze(ReadAll(reader));

    /// <summary>
    /// Analyze WAT source without throwing. Collects diagnostics and returns a partial
    /// AST where possible — suitable for LSP use on incomplete or erroneous code.
    /// </summary>
    public static WatCompileResult Analyze(string source)
    {
        var diagnostics = new List<WatDiagnostic>();
        WatModule? module = null;
        WatSymbolTable? symbolTable = null;

        try
        {
            var tokens = new WatLexer(source).TokenizeAll();
            var parser = new WatParser(tokens, diagnostics);
            module = parser.ParseModule();

            if (diagnostics.Count == 0 && module != null)
            {
                var resolver = new WatResolver(module, diagnostics);
                resolver.Resolve();
                symbolTable = resolver.SymbolTable;
            }
        }
        catch (WatException ex)
        {
            diagnostics.Add(new WatDiagnostic(ex.Line, ex.Column, ex.Message));
        }
        catch (Exception ex)
        {
            diagnostics.Add(new WatDiagnostic(0, 0, ex.Message));
        }

        return new WatCompileResult(module, diagnostics, symbolTable);
    }

    private static string ReadAll(TextReader reader) =>
        (reader ?? throw new ArgumentNullException(nameof(reader))).ReadToEnd();
}

public sealed record WatCompileResult(WatModule? Module, IReadOnlyList<WatDiagnostic> Diagnostics, WatSymbolTable? SymbolTable = null);
