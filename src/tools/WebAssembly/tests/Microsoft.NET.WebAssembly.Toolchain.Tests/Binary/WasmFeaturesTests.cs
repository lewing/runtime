// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using System.IO;
using Microsoft.NET.WebAssembly;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;

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

    private static readonly byte[] V128Zero = [0xFD, 0x0C, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];
    private static readonly byte[] ValidSimdInstructions = [.. V128Zero, .. V128Zero, 0xFD, 0x0E, 0x1A];
    private static readonly byte[] ValidGcInstructions = [0x41, 0x00, 0xFB, 0x1C, 0x1A];
    private static readonly byte[] ValidTryTableInstructions = [0x1F, 0x40, 0x00, 0x0B];
    private static readonly byte[] ValidTailCallInstructions = [0x12, 0x00];
    private static readonly byte[] ValidWideArithmeticInstructions =
    [
        0x00,
        0xFC, 0x13, 0x1A, 0x1A,
        0xFC, 0x14, 0x1A, 0x1A,
        0xFC, 0x15, 0x1A, 0x1A,
        0xFC, 0x16, 0x1A, 0x1A,
    ];

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

    private static void AssertFunctionBodiesValidate(WasmModule module)
    {
        var result = FunctionBodyValidator.ValidateModule(module, WasmFeatures.All);
        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Diagnostics.Select(d => d.Message)));
    }

    // ── MVP rejection tests ───────────────────────────────────────────────────

    [Fact]
    public void MvpOnly_RejectsSIMD()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));
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
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidTryTableInstructions));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.ExceptionHandling, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsGC()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidGcInstructions));
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, WasmFeatures.Mvp));
        Assert.Equal(WasmFeatures.Gc, ex.RequiredFeature);
    }

    [Fact]
    public void MvpOnly_RejectsTailCall()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidTailCallInstructions));
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

    [Fact]
    public void WideArithmetic_DefaultAndFeatureAloneAcceptAllInstructions()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidWideArithmeticInstructions));

        foreach (WasmFeatures features in new[] { WasmFeatures.Default, WasmFeatures.WideArithmetic })
        {
            var result = ReadWithFeatures(module, features);
            Assert.Single(result.Functions);
            AssertFunctionBodiesValidate(result);
        }
    }

    [Fact]
    public void WideArithmetic_MvpAndExplicitOptOutRejectAllInstructions()
    {
        foreach (byte subOpcode in new byte[] { 19, 20, 21, 22 })
        {
            byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0x00, 0xFC, subOpcode));
            foreach (WasmFeatures features in new[] { WasmFeatures.Mvp, WasmFeatures.Default & ~WasmFeatures.WideArithmetic })
            {
                var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(module, features));
                Assert.Equal(WasmFeatures.WideArithmetic, ex.RequiredFeature);
            }
        }
    }

    [Fact]
    public void WideArithmeticOptOut_DoesNotRelabelAssignedUnsupportedMemoryDiscard()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFC, 0x12));

        var ex = Assert.Throws<WasmFormatException>(
            () => ReadWithFeatures(module, WasmFeatures.Default & ~WasmFeatures.WideArithmetic));

        Assert.Contains("Unknown 0xFC sub-opcode: 18", ex.Message, StringComparison.Ordinal);
    }

    // ── Default accepts all extensions ───────────────────────────────────────

    [Fact]
    public void DefaultFeatures_AcceptsSIMD()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);
    }

    [Fact]
    public void DefaultFeatures_AcceptsExceptionHandling()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidTryTableInstructions));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);
    }

    [Fact]
    public void DefaultFeatures_AcceptsTailCall()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidTailCallInstructions));
        var result = ReadWithFeatures(module, WasmFeatures.Default);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);
    }

    [Fact]
    public void DefaultFeatures_RejectsThreads()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFE, 0x00));

        Assert.False(WasmFeatures.Default.HasFlag(WasmFeatures.Threads));
        var ex = Assert.Throws<WasmFeatureException>(
            () => ReadWithFeatures(module, WasmFeatures.Default));
        Assert.Equal(WasmFeatures.Threads, ex.RequiredFeature);
    }

    /// <summary>
    /// With threads DISABLED the decoder exits at the RequireFeature gate, so
    /// <see cref="DefaultFeatures_RejectsThreads"/> never reaches the line below it — the
    /// <see cref="WasmUnimplementedFormatException"/> that classifies the 0xFE gap as nesm's own
    /// capability signal rather than a malformed binary was therefore unexecuted by any test.
    /// That matters because a plain <c>WasmFormatException</c> here would satisfy
    /// <c>assert_malformed</c> / <c>assert_invalid</c> and count the absence of atomics as
    /// conformance to them (#385) — and a revert to that shape would have gone unnoticed.
    ///
    /// Enabling the feature walks past the gate and lands on the throw, so this pins the
    /// classification itself.
    /// </summary>
    [Fact]
    public void ThreadsEnabled_AtomicDecodeReportsUnimplemented_NotMalformed()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(0xFE, 0x00));
        var features = WasmFeatures.Default | WasmFeatures.Threads;

        Assert.True(features.HasFlag(WasmFeatures.Threads));

        // Not WasmFeatureException: past the gate. Not a bare WasmFormatException: this is the
        // whole point — Assert.Throws is exact-type, so a revert to WasmFormatException fails here.
        var ex = Assert.Throws<WasmUnimplementedFormatException>(() => ReadWithFeatures(module, features));

        // The carrier is what the spec runners key on, so assert the interface, not just the type.
        var carrier = Assert.IsAssignableFrom<IWasmUnimplemented>(ex);
        Assert.Equal("atomic (0xFE) instruction decoding", carrier.Feature);

        // No issue is filed for the atomics gap; the throw passes no trackingIssue. Pinned so that
        // filing one is a deliberate edit here rather than a silent drift.
        Assert.Equal(string.Empty, carrier.TrackingIssue);

        // The decode contract is preserved — callers typed against WasmFormatException still catch it.
        Assert.IsAssignableFrom<WasmFormatException>(ex);

        Assert.Contains("nesm has not implemented", ex.Message, StringComparison.Ordinal);
        Assert.Contains("atomic (0xFE) instruction decoding", ex.Message, StringComparison.Ordinal);
    }

    // ── Selective feature enabling ────────────────────────────────────────────

    [Fact]
    public void SimdFeatureAlone_AllowsSimd_RejectsGC()
    {
        byte[] simdModule = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));
        byte[] gcModule = BuildModule(TypeSection, FunctionSection, CodeSection(ValidGcInstructions));

        // SIMD alone accepts SIMD
        var result = ReadWithFeatures(simdModule, WasmFeatures.Simd);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);

        // SIMD alone rejects GC
        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(gcModule, WasmFeatures.Simd));
        Assert.Equal(WasmFeatures.Gc, ex.RequiredFeature);
    }

    [Fact]
    public void GcFeatureAlone_AllowsGC_RejectsSimd()
    {
        byte[] gcModule = BuildModule(TypeSection, FunctionSection, CodeSection(ValidGcInstructions));
        byte[] simdModule = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));

        var result = ReadWithFeatures(gcModule, WasmFeatures.Gc);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);

        var ex = Assert.Throws<WasmFeatureException>(() => ReadWithFeatures(simdModule, WasmFeatures.Gc));
        Assert.Equal(WasmFeatures.Simd, ex.RequiredFeature);
    }

    // ── WasmFeatureException carries RequiredFeature ──────────────────────────

    [Fact]
    public void WasmFeatureException_CarriesRequiredFeature_Simd()
    {
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));
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
        byte[] module = BuildModule(TypeSection, FunctionSection, CodeSection(ValidSimdInstructions));
        var result = ModuleReader.ReadModule(new MemoryStream(module), null);
        Assert.Single(result.Functions);
        AssertFunctionBodiesValidate(result);
    }
}
