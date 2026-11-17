// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;
using Microsoft.WebAssembly.Build.Tasks;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Tasks;

/// <summary>
/// Compares the managed replacements used by the WebAssembly SDK targets with the native tools they replace
/// (llvm-size from the wasi-sdk, wasm-opt from binaryen), when those tools are in the wasm tool cache.
/// </summary>
public class ManagedToolReplacementTests
{
    private const string Source = """
        static const char greeting[] = "hello managed toolchain";
        int counters[16] = { 1, 2, 3 };
        int zeroed[64];
        __attribute__((export_name("get"))) int get(int i) { zeroed[i & 63] += counters[i & 15]; return greeting[i & 7] + zeroed[i & 63]; }
        """;

    public static bool IsWasiSdkAvailable => WasmLdParityTests.IsWasiSdkAvailable;

    [ConditionalFact(typeof(ManagedToolReplacementTests), nameof(IsWasiSdkAvailable))]
    public void DataSectionSizes_MatchLlvmSize()
    {
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string bin = WasmLdParityTests.WasiSdkBin!;
        string withData = Compile(bin, dir, "data", Source);
        string withoutData = Compile(bin, dir, "nodata", "int f(int x) { return x + 1; }");

        string llvmSize = WasmLdParityTests.Run(Path.Combine(bin, "llvm-size"), ["-d", "--format=sysv", withData, withoutData]);
        // The SDK targets read the "DATA <size> 0" rows of llvm-size's output.
        List<string> expected = llvmSize.Split('\n')
            .Where(l => l.StartsWith("DATA ", StringComparison.Ordinal))
            .Select(l => l.Split(' ', StringSplitOptions.RemoveEmptyEntries)[1])
            .ToList();

        var task = new WasmGetDataSectionSizes { BuildEngine = new NullBuildEngine(), ObjectFiles = [new TaskItem(withData), new TaskItem(withoutData)] };

        Assert.True(task.Execute());
        Assert.NotEmpty(expected);
        Assert.Equal(expected, task.DataSectionSizes.Select(i => i.ItemSpec));
        Assert.Equal(withData, task.DataSectionSizes[0].GetMetadata("ObjectFile"));
    }

    [ConditionalFact(typeof(ManagedToolReplacementTests), nameof(IsWasiSdkAvailable))]
    public void StripDebugInfo_RemovesTheSameSectionsAsWasmOptStripDwarf()
    {
        string? binaryen = WasmLdParityTests.ToolDirectory("BinaryenPath", "bin");
        string? wasmOpt = binaryen is null ? null : Path.Combine(binaryen, "wasm-opt");
        if (wasmOpt is null || !File.Exists(wasmOpt))
            throw new SkipTestException("wasm-opt is not available in the wasm tool cache.");

        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string bin = WasmLdParityTests.WasiSdkBin!;
        string obj = Compile(bin, dir, "debug", Source, "-g");
        string linked = Path.Combine(dir, "linked.wasm");
        WasmLdParityTests.Run(Path.Combine(bin, "wasm-ld"), ["--no-entry", obj, "-o", linked]);

        string expectedPath = Path.Combine(dir, "wasm-opt.wasm");
        WasmLdParityTests.Run(wasmOpt, ["--enable-bulk-memory", "--strip-dwarf", linked, "-o", expectedPath]);
        string actualPath = Path.Combine(dir, "managed.wasm");
        var task = new WasmStripDebugInfo { BuildEngine = new NullBuildEngine(), InputFile = linked, OutputFile = actualPath };

        Assert.True(task.Execute());
        Assert.Contains(WasmSections.Read(linked), s => s.Name == ".debug_info");
        Assert.Equal(CustomSectionNames(expectedPath), CustomSectionNames(actualPath));
        Assert.Equal(
            WasmSections.Read(linked).Where(s => s.Id != WasmSectionInfo.CustomSectionId).Select(s => (s.Id, s.PayloadSize)),
            WasmSections.Read(actualPath).Where(s => s.Id != WasmSectionInfo.CustomSectionId).Select(s => (s.Id, s.PayloadSize)));
        WasmAssert.Valid(File.ReadAllBytes(actualPath));
    }

    [Fact]
    public void StripDebugInfo_InPlaceKeepsOtherSectionsByteForByte()
    {
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string path = Path.Combine(dir, "probe.wasm");
        File.Copy(WasmAssert.TestAssetPath(Path.Combine("dwarf", "add_probe_O0.wasm")), path);
        byte[] original = File.ReadAllBytes(path);
        List<WasmSectionInfo> before = WasmSections.Read(path);

        var task = new WasmStripDebugInfo { BuildEngine = new NullBuildEngine(), InputFile = path };

        Assert.True(task.Execute());
        List<WasmSectionInfo> after = WasmSections.Read(path);
        List<WasmSectionInfo> kept = before.Where(s => s.Name is null || (s.Name != "name" && !s.Name.StartsWith(".debug_", StringComparison.Ordinal))).ToList();
        Assert.True(kept.Count < before.Count);
        Assert.Equal(kept.Select(s => (s.Id, s.Name, s.PayloadSize)), after.Select(s => (s.Id, s.Name, s.PayloadSize)));

        byte[] stripped = File.ReadAllBytes(path);
        for (int i = 0; i < kept.Count; i++)
        {
            Assert.Equal(
                original.AsSpan((int)kept[i].PayloadOffset, (int)kept[i].PayloadSize).ToArray(),
                stripped.AsSpan((int)after[i].PayloadOffset, (int)after[i].PayloadSize).ToArray());
        }
    }

    private static List<string> CustomSectionNames(string path) =>
        WasmSections.Read(path).Where(s => s.Id == WasmSectionInfo.CustomSectionId).Select(s => s.Name!).Order().ToList();

    private static string Compile(string bin, string dir, string name, string source, params string[] extraArgs)
    {
        string c = Path.Combine(dir, name + ".c");
        string o = Path.Combine(dir, name + ".o");
        File.WriteAllText(c, source);
        WasmLdParityTests.Run(Path.Combine(bin, "clang"), ["--target=wasm32-unknown-unknown", "-O2", .. extraArgs, "-c", c, "-o", o]);
        return o;
    }

    private sealed class NullBuildEngine : IBuildEngine
    {
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.proj";
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => throw new InvalidOperationException(e.Message);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;
    }
}
