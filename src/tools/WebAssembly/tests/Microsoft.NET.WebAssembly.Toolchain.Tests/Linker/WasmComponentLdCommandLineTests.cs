// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Linker;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

public class WasmComponentLdCommandLineTests
{
    [Fact]
    public void ComponentOptions_AreSeparatedFromWasmLdOptions()
    {
        var commandLine = WasmComponentLdCommandLine.Parse(
        [
            "--component", "-m", "wasm32", "--wasm-ld-path", "/sdk/bin/wasm-ld", "--component-type=world.wasm",
            "--adapt", "wasi_snapshot_preview1=adapter.wasm", "--adapt=other.reactor.wasm", "--wasi-adapter", "reactor",
            "--export=foo", "a.o", "-o", "out.wasm",
        ]);

        Assert.True(commandLine.EmitComponent);
        Assert.Equal(["world.wasm"], commandLine.ComponentTypeFiles);
        Assert.Equal([("wasi_snapshot_preview1", "adapter.wasm"), ("other", "other.reactor.wasm")], commandLine.Adapters);
        Assert.Equal("reactor", commandLine.WasiAdapter);
        Assert.Equal(["foo"], commandLine.WasmLd.Options.Exports);
        Assert.Equal(["a.o"], commandLine.WasmLd.InputFiles);
        Assert.Equal("out.wasm", commandLine.WasmLd.OutputPath);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false, "--component")]
    [InlineData(true, true)]
    [InlineData(false, true, "--skip-wit-component")]
    public void EmitComponent_FollowsDefaultAndFlags(bool expected, bool emitByDefault, params string[] args)
    {
        Assert.Equal(expected, WasmComponentLdCommandLine.Parse(args, emitByDefault).EmitComponent);
    }

    [Fact]
    public void InvalidWasiAdapter_Throws()
    {
        Assert.Throws<WasmLdArgumentException>(() => WasmComponentLdCommandLine.Parse(["--wasi-adapter=bogus"]));
    }

    [ConditionalFact(typeof(WasmLdParityTests), nameof(WasmLdParityTests.IsWasiSdkAvailable))]
    public void WasiCommand_LinksToAValidComponentLikeWasmComponentLd()
    {
        string bin = WasmLdParityTests.WasiSdkBin!;
        string? wasmToolsDir = WasmLdParityTests.ToolDirectory("WasmToolsPath", null);
        string wasmTools = wasmToolsDir is null ? "" : Path.Combine(wasmToolsDir, "wasm-tools");
        if (!File.Exists(wasmTools))
            throw new SkipTestException("wasm-tools is not available in the wasm tool cache.");

        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string source = Path.Combine(dir, "hello.c");
        File.WriteAllText(source, "#include <stdio.h>\nint main(void) { puts(\"hello\"); return 0; }\n");
        string obj = Path.Combine(dir, "hello.o");
        WasmLdParityTests.Run(Path.Combine(bin, "clang"), ["--target=wasm32-wasip2", "-O2", "-c", source, "-o", obj]);

        // wasm-component-ld, driven by clang, is the reference.
        string expected = Path.Combine(dir, "wasm-component-ld.wasm");
        WasmLdParityTests.Run(Path.Combine(bin, "clang"), ["--target=wasm32-wasip2", obj, "-o", expected]);

        string sdk = Path.GetDirectoryName(bin)!;
        string libDir = Path.Combine(sdk, "share", "wasi-sysroot", "lib", "wasm32-wasip2");
        string builtins = Directory.GetFiles(Path.Combine(sdk, "lib", "clang"), "libclang_rt.builtins.a", SearchOption.AllDirectories)
            .First(p => p.Contains("wasm32-unknown-wasip2", StringComparison.Ordinal));
        string actual = Path.Combine(dir, "managed.wasm");
        WasmComponentLdCommandLine.Parse(
            ["--component", Path.Combine(libDir, "crt1-command.o"), obj, $"-L{libDir}", "-lc", builtins, "-o", actual]).Run();

        Assert.Equal(WasmSectionKinds(expected), WasmSectionKinds(actual));
        WasmLdParityTests.Run(wasmTools, ["validate", "--features=all", actual]);
        Assert.Equal(
            NormalizeWit(WasmLdParityTests.Run(wasmTools, ["component", "wit", expected])),
            NormalizeWit(WasmLdParityTests.Run(wasmTools, ["component", "wit", actual])));
    }

    // A component starts with the component-model preamble rather than a core module's.
    private static byte[] WasmSectionKinds(string path) => File.ReadAllBytes(path)[4..8];

    // The managed encoder may order interface members differently from wit-component; compare as sets per block.
    private static List<string> NormalizeWit(string wit) =>
        wit.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).Order(StringComparer.Ordinal).ToList();
}
