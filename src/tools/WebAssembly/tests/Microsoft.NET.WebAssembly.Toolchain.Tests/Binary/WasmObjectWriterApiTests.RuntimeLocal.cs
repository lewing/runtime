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
}
