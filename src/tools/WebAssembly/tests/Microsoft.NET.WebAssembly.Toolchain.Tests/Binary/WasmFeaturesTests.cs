// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using System.IO;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Tests for <see cref="WasmFeatures"/> flag gating in <see cref="ModuleReader"/>.
/// </summary>
public class WasmFeaturesTests
{
    private static readonly byte[] WasmHeader = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    private static byte[] BuildModule(params byte[][] sections)
    {
        int total = WasmHeader.Length + sections.Sum(s => s.Length);
        var result = new byte[total];
        WasmHeader.CopyTo(result, 0);
        int offset = WasmHeader.Length;
        foreach (var s in sections) { s.CopyTo(result, offset); offset += s.Length; }
        return result;
    }

    private static byte[] BuildSection(byte id, params byte[][] parts)
    {
        int payloadLen = parts.Sum(p => p.Length);
        var leb = U32Leb((uint)payloadLen);
        var result = new byte[1 + leb.Length + payloadLen];
        result[0] = id;
        leb.CopyTo(result, 1);
        int off = 1 + leb.Length;
        foreach (var p in parts) { p.CopyTo(result, off); off += p.Length; }
        return result;
    }

    private static byte[] U32Leb(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            bytes.Add(b);
        } while (value != 0);
        return [.. bytes];
    }

    // Type section: 1 type — () -> ()
    private static readonly byte[] TypeSection = BuildSection(1, [0x01, 0x60, 0x00, 0x00]);

    // Function section: 1 function, type index 0
    private static readonly byte[] FunctionSection = BuildSection(3, [0x01, 0x00]);

    // Builds a code section containing a single function body with given instructions.
    // The body has no locals; the end (0x0B) opcode is appended automatically.
    private static byte[] CodeSection(params byte[] instructions)
    {
        byte[] body = [0x00, .. instructions, 0x0B];
        byte[] bodyWithLen = [.. U32Leb((uint)body.Length), .. body];
        return BuildSection(10, U32Leb(1), bodyWithLen);
    }

    private static WasmModule ReadWithFeatures(byte[] module, WasmFeatures features)
    {
        var opts = new ModuleReaderOptions { Features = features };
        return ModuleReader.ReadModule(new MemoryStream(module), opts);
    }

    // ── MVP rejection tests ───────────────────────────────────────────────────

    [Fact]
    public void MvpOnly_RejectsSIMD()
    {
        // 0xFD 0x0E = i8x16.swizzle — SIMD prefix, no immediates
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.Simd, ex.RequiredFeature);
        Assert.Contains("0xFD", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MvpOnly_RejectsExceptionHandling_Try()
    {
        // 0x06 = try (legacy), block type 0x40 (empty), 0x0B end-try
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x06, 0x40, 0x0B));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.ExceptionHandling, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsExceptionHandling_TryTable()
    {
        // 0x1F = try_table; block type 0x40 (empty); 0 catch handlers; 0x0B end-try_table
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x1F, 0x40, 0x00, 0x0B));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.ExceptionHandling, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsGC()
    {
        // 0xFB 0x00 = struct.new_default — GC prefix
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFB, 0x00, 0x00));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.Gc, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsTailCall()
    {
        // 0x12 = return_call; immediate: function index 0
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x12, 0x00));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.TailCalls, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsBulkMemory()
    {
        // Build a module with data count + data section (required for memory.init)
        // 0xFC 0x0A = memory.copy — BulkMemory but doesn't need data count section
        // memory.copy (FC:10) reads two u32 memory indices
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFC, 0x0A, 0x00, 0x00));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.BulkMemory, ex.RequiredFeature);
    }

    // ── Default accepts all extensions ───────────────────────────────────────

    [Fact]
    public void DefaultFeatures_AcceptsSIMD()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
    }

    [Fact]
    public void DefaultFeatures_AcceptsExceptionHandling()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x06, 0x40, 0x0B));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
    }

    [Fact]
    public void DefaultFeatures_AcceptsTailCall()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x12, 0x00));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
    }

    // ── Selective feature enabling ────────────────────────────────────────────

    [Fact]
    public void SimdFeatureAlone_AllowsSimd_RejectsGC()
    {
        byte[] simdModule = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));
        byte[] gcModule = BuildModule(TypeSection, FunctionSection, CodeSection(0xFB, 0x00, 0x00));

        // SIMD alone accepts SIMD
        var result = ReadWithFeatures(simdModule, WasmFeatures.Simd);
        Assert.Single(result.Functions);

        // SIMD alone rejects GC
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(gcModule, WasmFeatures.Simd));
        Assert.Equal(WasmFeatures.Gc, ex.RequiredFeature);
    }

    [Fact]
    public void GcFeatureAlone_AllowsGC_RejectsSimd()
    {
        byte[] gcModule = BuildModule(TypeSection, FunctionSection, CodeSection(0xFB, 0x00, 0x00));
        byte[] simdModule = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));

        var result = ReadWithFeatures(gcModule, WasmFeatures.Gc);
        Assert.Single(result.Functions);

        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(simdModule, WasmFeatures.Gc));
        Assert.Equal(WasmFeatures.Simd, ex.RequiredFeature);
    }

    // ── WasmFeatureException carries RequiredFeature ──────────────────────────

    [Fact]
    public void WasmFeatureException_CarriesRequiredFeature_Simd()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.Simd, ex.RequiredFeature);
        Assert.IsAssignableFrom<WasmFormatException>(ex); // is a subtype
    }

    [Fact]
    public void WasmFeatureException_CarriesRequiredFeature_ExceptionHandling()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x06, 0x40, 0x0B));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.ExceptionHandling, ex.RequiredFeature);
        Assert.Contains("ExceptionHandling", ex.Message);
    }

    // ── ModuleReaderOptions defaults ──────────────────────────────────────────

    [Fact]
    public void DefaultOptions_HasDefaultFeatures()
    {
        Assert.Equal(WasmFeatures.Default, ModuleReaderOptions.Default.Features);
    }

    [Fact]
    public void MvpOptions_HasNoFeatures()
    {
        Assert.Equal(WasmFeatures.Mvp, ModuleReaderOptions.Mvp.Features);
        Assert.Equal(WasmFeatures.None, ModuleReaderOptions.Mvp.Features);
    }

    [Fact]
    public void NullOptions_TreatedAsDefault()
    {
        // ReadModule(stream, null) should behave the same as Default
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFD, 0x0E));
        var result = ModuleReader.ReadModule(new MemoryStream(module), null);
        Assert.Single(result.Functions);
    }
}
