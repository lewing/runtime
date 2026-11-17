// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Linker;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

public class WasmArCommandLineTests
{
    // Each archive is written twice, so the second call exercises replace (r) or append (q) into an existing archive.
    [ConditionalTheory(typeof(WasmLdParityTests), nameof(WasmLdParityTests.IsWasiSdkAvailable))]
    [InlineData("-crs")]
    [InlineData("rcS")]
    [InlineData("qc")]
    [InlineData("crsD")]
    public void ArchiveMatchesLlvmAr(string operation)
    {
        string bin = WasmLdParityTests.WasiSdkBin!;
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string a = Compile(bin, dir, "a", "int a_value = 1; int a(void) { return a_value; }");
        string b = Compile(bin, dir, "b", "extern int a(void); int b(void) { return a() + 1; }");
        string expected = Path.Combine(dir, "expected.a");
        string actual = Path.Combine(dir, "actual.a");

        WasmLdParityTests.Run(Path.Combine(bin, "llvm-ar"), [operation, expected, a]);
        WasmLdParityTests.Run(Path.Combine(bin, "llvm-ar"), [operation, expected, a, b]);
        Assert.Empty(WasmArCommandLine.Run([operation, actual, a]));
        Assert.Empty(WasmArCommandLine.Run([operation, actual, a, b]));

        Assert.Equal(File.ReadAllBytes(expected), File.ReadAllBytes(actual));
    }

    [Theory]
    [InlineData("x")]
    [InlineData("rq")]
    [InlineData("c")]
    public void UnsupportedOperations_AreRejected(string operation)
    {
        string archive = Path.Combine(WasmLdCommandLineTests.CreateTempDirectory(), "out.a");
        Assert.Throws<WasmLdArgumentException>(() => WasmArCommandLine.Run([operation, archive]));
        Assert.False(File.Exists(archive));
    }

    private static string Compile(string bin, string dir, string name, string source)
    {
        string c = Path.Combine(dir, name + ".c");
        string o = Path.Combine(dir, name + ".o");
        File.WriteAllText(c, source);
        WasmLdParityTests.Run(Path.Combine(bin, "clang"), ["--target=wasm32-unknown-unknown", "-O2", "-c", c, "-o", o]);
        return o;
    }
}
