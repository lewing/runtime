// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

using Microsoft.DotNet.XUnitExtensions;
using System.Buffers;
using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

public sealed partial class WasmCodeWriterTests
{
    // ── Validation and execution ────────────────────────────────────────────

    [Fact]
    public void WrittenCodeValidates()
    {
        // f(x): a br_table switch over x (0 → 10, 1 → 20, else → x * 3 computed in a v128 lane).
        var (w, body) = NewWriter();
        w.Locals([ValType.V128]);
        w.Block(WasmOp.Block, WasmBlockType.Empty);
        w.Block(WasmOp.Block, WasmBlockType.Empty);
        w.Block(WasmOp.Block, WasmBlockType.Empty);
        w.LocalGet(0);
        w.BrTable([0, 1], 2);
        w.End();
        w.I32Const(10);
        w.Emit(WasmOp.Return);
        w.End();
        w.I32Const(20);
        w.Emit(WasmOp.Return);
        w.End();
        w.LocalGet(0);
        w.Emit(WasmOp.I32x4Splat);
        w.LocalTee(1);
        w.LocalGet(1);
        w.Emit(WasmOp.I32x4Add);
        w.LocalGet(1);
        w.Emit(WasmOp.I32x4Add);
        w.Lane(WasmOp.I32x4ExtractLane, 3);
        w.End();

        using var stream = new MemoryStream();
        var module = new WasmModuleWriter(stream);
        module.WriteSection(WasmSectionId.Type, Convert.FromHexString("0160017f017f"));
        module.WriteSection(WasmSectionId.Function, Convert.FromHexString("0100"));
        module.WriteSection(WasmSectionId.Export, Convert.FromHexString("01" + "0166" + "0000"));
        using var code = new MemoryStream();
        WasmLeb128.WriteULeb128(code, (ulong)body.WrittenCount);
        code.Write(body.WrittenSpan);
        code.Position = 0;
        module.WriteVectorSection(WasmSectionId.Code, 1, code);
        byte[] bytes = stream.ToArray();

        ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes), new ModuleReaderOptions { Features = WasmFeatures.All });
        Assert.Equal(body.WrittenSpan.ToArray(), CodeReemitter.Reemit(body.WrittenSpan));
    }
}
