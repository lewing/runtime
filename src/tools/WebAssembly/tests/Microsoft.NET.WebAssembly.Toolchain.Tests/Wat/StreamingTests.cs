// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using System.Reflection;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Streaming round-trip tests that exercise the TextWriter/Stream-based APIs
/// without materializing giant strings or byte arrays at the caller level.
/// </summary>
public class StreamingTests
{
    private static byte[] LoadSample(string logicalName)
    {
        var asm = Assembly.GetExecutingAssembly();
        using var stream = asm.GetManifestResourceStream(logicalName)
            ?? throw new InvalidOperationException($"Embedded resource '{logicalName}' not found.");
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ── 1. StreamingDisassemble_ToFile ────────────────────────────────────

    [Theory]
    [InlineData("calculator.wasm")]
    [InlineData("sorting.wasm")]
    public void StreamingDisassemble_ToFile(string sampleName)
    {
        var bytes = LoadSample(sampleName);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        var tempPath = Path.GetTempFileName();
        try
        {
            // Disassemble to a temp file via TextWriter
            using (var writer = new StreamWriter(tempPath))
                WasmDisassembler.Disassemble(module, writer);

            // Read back the WAT and verify it compiles
            var wat = File.ReadAllText(tempPath);
            Assert.StartsWith("(module", wat);

            var recompiled = WatCompiler.CompileAndValidate(wat);
            Assert.Equal(module.Functions.Count, recompiled.Functions.Count);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── 2. StreamingEncode_ToFile ─────────────────────────────────────────

    [Theory]
    [InlineData("calculator.wasm")]
    [InlineData("sorting.wasm")]
    public void StreamingEncode_ToFile(string sampleName)
    {
        var bytes = LoadSample(sampleName);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var wat = WasmDisassembler.Disassemble(module);

        // Compile WAT to wasm, writing directly to a temp file
        var tempPath = Path.GetTempFileName();
        try
        {
            using (var fs = new FileStream(tempPath, FileMode.Create))
                WatCompiler.Compile(wat, fs);

            // Read back and validate
            using var readStream = File.OpenRead(tempPath);
            var roundTripped = ModuleReader.ReadModuleAndValidate(readStream);
            Assert.Equal(module.Functions.Count, roundTripped.Functions.Count);
        }
        finally
        {
            File.Delete(tempPath);
        }
    }

    // ── 3. StreamingRoundTrip_FileToFile ──────────────────────────────────

    [Theory]
    [InlineData("calculator.wasm")]
    [InlineData("sorting.wasm")]
    public void StreamingRoundTrip_FileToFile(string sampleName)
    {
        var bytes = LoadSample(sampleName);
        var original = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        var watPath = Path.GetTempFileName();
        var wasmPath = Path.GetTempFileName();
        try
        {
            // Step 1: Disassemble .wasm → temp WAT file (streaming)
            using (var writer = new StreamWriter(watPath))
                WasmDisassembler.Disassemble(original, writer);

            // Step 2: Read WAT file and compile → temp .wasm file (streaming)
            var wat = File.ReadAllText(watPath);
            using (var fs = new FileStream(wasmPath, FileMode.Create))
                WatCompiler.Compile(wat, fs);

            // Step 3: Read back and compare function counts
            using var readStream = File.OpenRead(wasmPath);
            var roundTripped = ModuleReader.ReadModuleAndValidate(readStream);
            Assert.Equal(original.Functions.Count, roundTripped.Functions.Count);
            Assert.Equal(original.Exports.Count, roundTripped.Exports.Count);
        }
        finally
        {
            File.Delete(watPath);
            File.Delete(wasmPath);
        }
    }

    // ── 4. StreamingDisassemble_ToStringWriter ────────────────────────────

    [Theory]
    [InlineData("calculator.wasm")]
    [InlineData("sorting.wasm")]
    public void StreamingDisassemble_ToStringWriter(string sampleName)
    {
        var bytes = LoadSample(sampleName);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        // String overload (allocating)
        var expected = WasmDisassembler.Disassemble(module);

        // TextWriter overload (streaming)
        var sw = new StringWriter();
        WasmDisassembler.Disassemble(module, sw);
        var actual = sw.ToString();

        Assert.Equal(expected, actual);
    }

    // ── 5. StreamingEncode_MatchesByteArray ───────────────────────────────

    [Theory]
    [InlineData("calculator.wasm")]
    [InlineData("sorting.wasm")]
    public void StreamingEncode_MatchesByteArray(string sampleName)
    {
        var bytes = LoadSample(sampleName);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var wat = WasmDisassembler.Disassemble(module);

        // byte[] overload
        var expectedBytes = WatCompiler.Compile(wat);

        // Stream overload
        var ms = new MemoryStream();
        WatCompiler.Compile(wat, ms);
        var actualBytes = ms.ToArray();

        Assert.Equal(expectedBytes, actualBytes);
    }

    // ── 6. WatWriter_InterpolatedStringHandler ───────────────────────────

    [Fact]
    public void WatWriter_InterpolatedStringHandler_Ints()
    {
        var sw = new StringWriter();
        using var writer = new WatWriter(sw);

        int value = 42;
        writer.Write($"i32.const {value}");
        writer.Flush();

        Assert.Equal("i32.const 42", sw.ToString());
    }

    [Fact]
    public void WatWriter_InterpolatedStringHandler_Strings()
    {
        var sw = new StringWriter();
        using var writer = new WatWriter(sw);

        string name = "add";
        writer.Write($"(export \"{name}\")");
        writer.Flush();

        Assert.Equal("(export \"add\")", sw.ToString());
    }

    [Fact]
    public void WatWriter_InterpolatedStringHandler_Mixed()
    {
        var sw = new StringWriter();
        using var writer = new WatWriter(sw);

        string funcName = "test";
        int paramCount = 3;
        double floatVal = 1.5;
        writer.Write($"(func ${funcName} (param {paramCount}) (f64.const {floatVal}))");
        writer.Flush();

        Assert.Equal($"(func $test (param 3) (f64.const {1.5}))", sw.ToString());
    }

    [Fact]
    public void WatWriter_InterpolatedStringHandler_WriteLine()
    {
        var sw = new StringWriter();
        using var writer = new WatWriter(sw);

        int idx = 7;
        writer.WriteLine($"local.get {idx}");
        writer.Flush();

        Assert.Equal($"local.get 7{Environment.NewLine}", sw.ToString());
    }

    // ── Large-file streaming test ────────────────────────────────────────
}
