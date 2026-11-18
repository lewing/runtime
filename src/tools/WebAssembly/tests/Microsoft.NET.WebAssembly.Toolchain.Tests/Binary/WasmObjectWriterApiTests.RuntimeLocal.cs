// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;
using Microsoft.DotNet.XUnitExtensions;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

public sealed partial class WasmObjectWriterApiTests
{
    [ConditionalFact(typeof(WasmLdParityTests), nameof(WasmLdParityTests.IsWasiSdkAvailable))]
    public void ClangWasm64ObjectsReencodeByteIdentically()
    {
        string bin = WasmLdParityTests.WasiSdkBin!;
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        const string source = """
            int a = 5;
            char big[300] = {1};
            static const char s[] = "hello";
            const char* get(void) { return s; }
            """;
        string c = Path.Combine(dir, "reencode64.c");
        string obj = Path.Combine(dir, "reencode64.o");
        File.WriteAllText(c, source);
        WasmLdParityTests.Run(Path.Combine(bin, "clang"), ["--target=wasm64-unknown-unknown", "-O2", "-fdata-sections", "-c", c, "-o", obj]);
        byte[] original = File.ReadAllBytes(obj);

        // A 64-bit memory's segments are placed with i64.const offsets, which the re-encoder must rebuild.
        // big[] is the segment at offset 16: flags 0, i64.const 16, end, size 300.
        Assert.True(original.AsSpan().IndexOf(Convert.FromHexString("00" + "42" + "10" + "0b" + "ac02")) >= 0, "no i64.const data segment header");
        ObjectReencoder.AssertBytesEqual(original, ObjectReencoder.Reencode(original), "clang wasm64 object");
    }

    /// <summary>
    /// <c>int answer = 42; int get() { return answer; }</c>, laid out the way the runtime writer lays out an
    /// object: memory import, data count, code with a padded relocated operand, data, then linking and reloc.CODE.
    /// </summary>
    private static byte[] BuildAnswerObject() => BuildObject(writer =>
    {
        writer.WriteVectorSection(WasmSectionId.Type, 1, new MemoryStream(Convert.FromHexString("6000017f")));
        writer.WriteVectorSection(WasmSectionId.Import, 1, new MemoryStream(
            Convert.FromHexString("03" + Convert.ToHexString("env"u8) + "0f" + Convert.ToHexString("__linear_memory"u8) + "020000")));
        writer.WriteVectorSection(WasmSectionId.Function, 1, new MemoryStream([0x00]));
        writer.WriteSection(WasmSectionId.DataCount, [0x01]);

        // body: no locals; i32.const <padded MEMORY_ADDR_LEB>; i32.load; end — passed through as-is.
        byte[] body = new byte[12];
        body[0] = 0x00;
        body[1] = 0x41;
        WasmLeb128.WritePaddedULeb128(body.AsSpan(2, RelocationTypes.GetPatchSize(RelocationType.R_WASM_MEMORY_ADDR_LEB)), 0);
        body[7] = 0x28;
        body[8] = 0x02;
        body[9] = 0x00;
        body[10] = 0x0b;
        using var bodies = new MemoryStream();
        WasmLeb128.WriteULeb128(bodies, 11);
        bodies.Write(body, 0, 11);
        bodies.Position = 0;
        int codeIndex = writer.WriteVectorSection(WasmSectionId.Code, 1, bodies);

        writer.WriteVectorSection(WasmSectionId.Data, 1, new MemoryStream(Convert.FromHexString("00" + "41000b" + "04" + "2a000000")));

        var symbols = new SymbolTableEncoder();
        symbols.AddFunction(SymbolFlags.None, 0, "get"u8);
        int answer = symbols.AddData(SymbolFlags.None, "answer"u8, segmentIndex: 0, offset: 0, size: 4);
        var segments = new SegmentInfoEncoder();
        segments.Add(".data.answer"u8, alignmentLog2: 2, SegmentFlags.None);
        var linking = new LinkingSectionEncoder();
        linking.WriteSymbolTable(symbols);
        linking.WriteSegmentInfo(segments);
        writer.WriteCustomSection("linking"u8, linking);

        // The relocated operand starts after the vector count, the body size and the opcode bytes.
        var reloc = new RelocationSectionEncoder((uint)codeIndex);
        reloc.Add(RelocationType.R_WASM_MEMORY_ADDR_LEB, offset: 1 + 1 + 2, index: (uint)answer, addend: 0);
        writer.WriteCustomSection("reloc.CODE"u8, reloc);
    });
}
