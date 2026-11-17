// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Diagnostics;
using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

/// <summary>
/// Links clang-produced object files with both wasi-sdk's wasm-ld and the managed linker (through the same
/// wasm-ld command line) and compares the results facet by facet. Requires the wasi-sdk from the shared wasm
/// tool cache; skipped when it is not present.
/// </summary>
public class WasmLdParityTests
{
    public enum Facet
    {
        Exports,
        FunctionImports,
        MemoryAndTable,
        StackPointer,
        ValidatesWithWasmTools,
    }

    private sealed record Scenario(string[] Args, params (string Name, string Source)[] Sources)
    {
        public bool ArchiveLastSource { get; init; }

        /// <summary>Compile for wasm32-wasip2 and link a WASI command against wasi-libc and compiler-rt, as NativeAOT does.</summary>
        public bool WasiLibc { get; init; }
    }

    private const string CrossUnitA = """
        extern int helper(int);
        static const char msg[] = "hello parity";
        int table_target(int x) { return x * 3; }
        int (*fptr)(int) = table_target;
        int counter = 5;
        __attribute__((export_name("add"))) int add(int a, int b) { return a + b + helper(counter); }
        __attribute__((export_name("call_ptr"))) int call_ptr(int v) { return fptr(v) + msg[v & 7]; }
        """;

    private const string CrossUnitB = """
        int buffer[64];
        int helper(int x) { buffer[x & 63] += x; return buffer[x & 63]; }
        int unused_fn(int x) { return x * 42; }
        """;

    private static readonly Dictionary<string, Scenario> s_scenarios = new()
    {
        ["cross-unit"] = new(["--no-entry"], ("a", CrossUnitA), ("b", CrossUnitB)),
        ["archive"] = new(["--no-entry"], ("a", CrossUnitA), ("b", CrossUnitB)) { ArchiveLastSource = true },
        ["export-flags"] = new(["--no-entry", "--export=helper", "--export-memory", "--export-table", "--growable-table"], ("a", CrossUnitA), ("b", CrossUnitB)),
        ["import-memory"] = new(["--no-entry", "--import-memory", "--initial-memory=131072", "--max-memory=262144", "-z", "stack-size=8192", "--stack-first"], ("a", CrossUnitA), ("b", CrossUnitB)),
        ["constructor"] = new(["--no-entry", "--allow-undefined", "--export=__wasm_call_ctors"], ("ctor", """
            extern int source(void);
            int initialized;
            __attribute__((constructor)) static void init(void) { initialized = source(); }
            __attribute__((export_name("get"))) int get(void) { return initialized; }
            """)),
        ["default-entry"] = new([], ("start", """
            int value;
            void _start(void) { value = 1; }
            """)),
        ["wasi-libc"] = new([], ("hello", """
            #include <stdio.h>
            #include <stdlib.h>
            int main(int argc, char** argv) { char* p = malloc(16); snprintf(p, 16, "%d", argc); puts(p); free(p); return 0; }
            """)) { WasiLibc = true },
        ["stack"] = new(["--no-entry"], ("sum", """
            extern void fill(int *p, int n);
            __attribute__((export_name("sum"))) int sum(int n) { int arr[32]; fill(arr, n & 31); int s = 0; for (int i = 0; i < (n & 31); i++) s += arr[i]; return s; }
            """), ("fill", """
            void fill(int *p, int n) { for (int i = 0; i < n; i++) p[i] = i * i; }
            """)),
    };

    // Divergences from wasm-ld that the managed linker does not handle yet. Each entry is skipped with its reason
    // rather than asserted, so the table doubles as the list of gaps to close before it can replace wasm-ld.
    private const string WasiLibcGap =
        "linking wasi-libc fails: TABLE_INDEX_REL_SLEB addend parsing, object classification, explicit-import symbols and __memory_base (Blazor-Playground/nesm#573)";

    private static readonly Dictionary<(string Scenario, Facet Facet), string> s_knownGaps = new()
    {
        [("wasi-libc", Facet.Exports)] = WasiLibcGap,
        [("wasi-libc", Facet.FunctionImports)] = WasiLibcGap,
        [("wasi-libc", Facet.MemoryAndTable)] = WasiLibcGap,
        [("wasi-libc", Facet.StackPointer)] = WasiLibcGap,
        [("wasi-libc", Facet.ValidatesWithWasmTools)] = WasiLibcGap,
    };

    public static IEnumerable<object[]> Cases() =>
        from scenario in s_scenarios.Keys
        from facet in Enum.GetValues<Facet>()
        select new object[] { scenario, facet };

    private static string? WasiSdkBin => ToolDirectory("WasiSdkPath", "bin");

    public static bool IsWasiSdkAvailable => WasiSdkBin is not null;

    [ConditionalTheory(typeof(WasmLdParityTests), nameof(IsWasiSdkAvailable))]
    [MemberData(nameof(Cases))]
    public void ManagedLinkerMatchesWasmLd(string scenarioName, Facet facet)
    {
        if (s_knownGaps.TryGetValue((scenarioName, facet), out string? gap))
            throw new SkipTestException($"Known gap: {gap}");

        (WasmModule expected, WasmModule actual, string actualPath) = LinkBoth(scenarioName);

        switch (facet)
        {
            case Facet.Exports:
                Assert.Equal(DescribeExports(expected), DescribeExports(actual));
                break;
            case Facet.FunctionImports:
                Assert.Equal(DescribeFunctionImports(expected), DescribeFunctionImports(actual));
                break;
            case Facet.MemoryAndTable:
                Assert.Equal(DescribeMemoryAndTable(expected), DescribeMemoryAndTable(actual));
                break;
            case Facet.StackPointer:
                Assert.Equal(DescribeStackPointer(expected), DescribeStackPointer(actual));
                break;
            case Facet.ValidatesWithWasmTools:
                string? wasmTools = ToolDirectory("WasmToolsPath", null) is string dir ? Path.Combine(dir, "wasm-tools") : null;
                if (wasmTools is null || !File.Exists(wasmTools))
                    throw new SkipTestException("wasm-tools is not available in the wasm tool cache.");
                Run(wasmTools, ["validate", "--features=all", actualPath]);
                break;
        }
    }

    private static (WasmModule Expected, WasmModule Actual, string ActualPath) LinkBoth(string scenarioName)
    {
        Scenario scenario = s_scenarios[scenarioName];
        string bin = WasiSdkBin!;
        string dir = WasmLdCommandLineTests.CreateTempDirectory();

        var inputs = new List<string>();
        foreach ((string name, string source) in scenario.Sources)
        {
            string c = Path.Combine(dir, name + ".c");
            string o = Path.Combine(dir, name + ".o");
            File.WriteAllText(c, source);
            Run(Path.Combine(bin, "clang"), [scenario.WasiLibc ? "--target=wasm32-wasip2" : "--target=wasm32-unknown-unknown", "-O2", "-c", c, "-o", o]);
            inputs.Add(o);
        }

        if (scenario.ArchiveLastSource)
        {
            string archive = Path.Combine(dir, "liblast.a");
            Run(Path.Combine(bin, "llvm-ar"), ["rcs", archive, inputs[^1]]);
            inputs[^1] = archive;
        }

        string[] args = scenario.Args;
        if (scenario.WasiLibc)
        {
            string sdk = Path.GetDirectoryName(bin)!;
            string libDir = Path.Combine(sdk, "share", "wasi-sysroot", "lib", "wasm32-wasip2");
            string builtins = Directory.GetFiles(Path.Combine(sdk, "lib", "clang"), "libclang_rt.builtins.a", SearchOption.AllDirectories)
                .First(p => p.Contains("wasm32-unknown-wasip2", StringComparison.Ordinal));
            inputs.Insert(0, Path.Combine(libDir, "crt1-command.o"));
            args = [.. args, $"-L{libDir}", "-lc", builtins];
        }

        string expectedPath = Path.Combine(dir, "wasm-ld.wasm");
        string actualPath = Path.Combine(dir, "managed.wasm");
        Run(Path.Combine(bin, "wasm-ld"), [.. args, .. inputs, "-o", expectedPath]);
        WasmModule actual = WasmLdDriver.Run(WasmLdCommandLine.Parse([.. args, .. inputs, "-o", actualPath]));

        return (ModuleReader.ReadModule(File.OpenRead(expectedPath)), actual, actualPath);
    }

    private static List<string> DescribeExports(WasmModule module) =>
        module.Exports.Values.Select(e => e.Descriptor switch
        {
            ExportDescriptor.Func f => $"func {e.Name}{Signature(WasmAssert.FunctionType(module, f.FunctionIndex))}",
            ExportDescriptor.Memory => $"memory {e.Name} ({(module.Imports.Any(i => i.Descriptor is ImportDescriptor.Memory) ? "imported" : "defined")})",
            ExportDescriptor.Table => $"table {e.Name}",
            ExportDescriptor.Global => $"global {e.Name}",
            _ => $"other {e.Name}",
        }).Order().ToList();

    private static List<string> DescribeFunctionImports(WasmModule module) =>
        module.Imports.Where(i => i.Descriptor is ImportDescriptor.Func)
            .Select(i => $"{i.ModuleName}.{i.Name}{Signature(module.Types[((ImportDescriptor.Func)i.Descriptor).TypeIndex])}")
            .Order().ToList();

    private static List<string> DescribeMemoryAndTable(WasmModule module)
    {
        var result = new List<string>();
        foreach (Import import in module.Imports)
        {
            if (import.Descriptor is ImportDescriptor.Memory m)
                result.Add($"imported memory {import.ModuleName}.{import.Name} {m.Type.Limits.Min}..{m.Type.Limits.Max}");
            else if (import.Descriptor is ImportDescriptor.Table)
                result.Add($"imported table {import.ModuleName}.{import.Name}");
        }
        foreach (MemoryType m in module.Memories)
            result.Add($"memory {m.Limits.Min}..{m.Limits.Max}");
        result.Add($"{module.Tables.Count} table(s)");
        return result;
    }

    private static string DescribeStackPointer(WasmModule module) =>
        module.Imports.Any(i => i.Name == "__stack_pointer") ? "imported __stack_pointer"
        : module.Globals.Any(g => g.Type is { ContentType: ValType.I32, Mutability: Mutability.Var }) ? "defined mutable i32 global"
        : "none";

    private static string Signature(FuncType type) =>
        $"({string.Join(",", type.Parameters)})->({string.Join(",", type.Results)})";

    private static string? ToolDirectory(string key, string? subdirectory)
    {
        if (AppContext.GetData($"Microsoft.NET.WebAssembly.Toolchain.Tests.{key}") is not string root || root.Length == 0)
            return null;
        string dir = subdirectory is null ? root : Path.Combine(root, subdirectory);
        return Directory.Exists(dir) ? dir : null;
    }

    private static void Run(string fileName, string[] args)
    {
        var startInfo = new ProcessStartInfo(fileName) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (string arg in args)
            startInfo.ArgumentList.Add(arg);

        using Process process = Process.Start(startInfo)!;
        string stderr = process.StandardError.ReadToEnd();
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"{Path.GetFileName(fileName)} {string.Join(" ", args)} failed with exit code {process.ExitCode}:{Environment.NewLine}{stderr}");
    }
}
