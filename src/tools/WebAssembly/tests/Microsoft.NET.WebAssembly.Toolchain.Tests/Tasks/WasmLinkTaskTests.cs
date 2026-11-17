// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;
using Microsoft.WebAssembly.Build.Tasks;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Tasks;

public class WasmLinkTaskTests
{
    [Fact]
    public void Execute_LinksModulesAndArchiveInProc()
    {
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string first = Path.Combine(dir, "first.o");
        File.WriteAllBytes(first, WasmLdCommandLineTests.CreateModuleBytes("first", 1));
        string archive = Path.Combine(dir, "libsample.a");
        File.WriteAllBytes(archive, WasmLdCommandLineTests.CreateArchive(("left.o", WasmLdCommandLineTests.CreateModuleBytes("left", 2))));
        string output = Path.Combine(dir, "linked.wasm");

        var engine = new TestBuildEngine();
        var task = new WasmLink
        {
            BuildEngine = engine,
            InputFiles = [new TaskItem(first), new TaskItem(archive)],
            OutputFile = output,
        };

        Assert.True(task.Execute(), string.Join(Environment.NewLine, engine.Errors));
        Assert.Equal(["first", "left"], WasmAssert.Valid(File.ReadAllBytes(output)).Exports.Keys.Order());
    }

    [Fact]
    public void Properties_MapToTheSameOptionsAsWasmLdFlags()
    {
        var task = new WasmLink
        {
            InputFiles = [new TaskItem("a.o")],
            OutputFile = "out.wasm",
            EntryPoint = "_start",
            ExportedSymbols = ["foo", "bar"],
            ExportIfDefinedSymbols = ["maybe"],
            AllowUndefined = true,
            GcSections = false,
            ImportMemory = true,
            StackFirst = true,
            InitialMemory = "131072",
            StackSize = "8192",
            TableBase = "4",
            ExtraArguments = ["--export-table"],
        };

        LinkerOptions fromTask = WasmLdCommandLine.Parse(task.BuildArguments()).Options;
        LinkerOptions fromFlags = WasmLdCommandLine.Parse(
            ["--entry=_start", "--export=foo", "--export=bar", "--export-if-defined=maybe", "--allow-undefined", "--no-gc-sections",
             "--import-memory", "--stack-first", "--initial-memory=131072", "-z", "stack-size=8192", "--table-base=4", "--export-table", "a.o", "-o", "out.wasm"]).Options;

        Assert.Equivalent(fromFlags, fromTask, strict: true);
    }

    [Fact]
    public void Execute_ReportsInvalidArgumentsAsErrors()
    {
        var engine = new TestBuildEngine();
        var task = new WasmLink
        {
            BuildEngine = engine,
            InputFiles = [new TaskItem("a.o")],
            OutputFile = "out.wasm",
            ExtraArguments = ["--component-type=x.wit"],
        };

        Assert.False(task.Execute());
        Assert.Single(engine.Errors);
    }

    private sealed class TestBuildEngine : IBuildEngine
    {
        public List<string> Errors { get; } = [];
        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => "test.proj";
        public void LogCustomEvent(CustomBuildEventArgs e) { }
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogMessageEvent(BuildMessageEventArgs e) { }
        public void LogWarningEvent(BuildWarningEventArgs e) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => true;
    }
}
