// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Wat;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

public class WasmLdCommandLineTests
{
    [Theory]
    [InlineData("--export=foo")]
    [InlineData("--export", "foo")]
    [InlineData("-export=foo")]
    public void Export_AcceptsAllSpellings(params string[] args)
    {
        var commandLine = WasmLdCommandLine.Parse(args);

        Assert.Equal(["foo"], commandLine.Options.Exports);
    }

    [Theory]
    [InlineData("-z", "stack-size=8192")]
    [InlineData("-zstack-size=8192")]
    [InlineData("--stack-size=8192")]
    [InlineData("--stack-size=0x2000")]
    public void StackSize_AcceptsAllSpellings(params string[] args)
    {
        Assert.Equal(8192u, WasmLdCommandLine.Parse(args).Options.StackSize);
    }

    [Fact]
    public void MemoryAndTableOptions_MapToLinkerOptions()
    {
        var options = WasmLdCommandLine.Parse(
        [
            "--import-memory", "--export-memory", "--shared-memory",
            "--initial-memory=131072", "--max-memory", "262144",
            "--global-base=1024", "--stack-first",
            "--table-base=4", "--export-table", "--growable-table",
            "--export-if-defined=maybe", "--allow-undefined", "--no-gc-sections",
        ]).Options;

        Assert.True(options.ImportMemory);
        Assert.True(options.ExportMemory);
        Assert.True(options.SharedMemory);
        Assert.Equal(131072, options.InitialMemory);
        Assert.Equal(262144, options.MaxMemory);
        Assert.Equal(1024u, options.GlobalBase);
        Assert.True(options.StackFirst);
        Assert.Equal(4u, options.TableBase);
        Assert.True(options.ExportTable);
        Assert.True(options.GrowableTable);
        Assert.Equal(["maybe"], options.ExportIfDefined);
        Assert.True(options.AllowUndefined);
        Assert.False(options.GcSections);
    }

    [Theory]
    [InlineData("_start")]
    [InlineData("main", "--entry=main")]
    [InlineData(null, "--no-entry")]
    [InlineData(null, "--entry=main", "--no-entry")]
    [InlineData("main", "--no-entry", "--entry", "main")]
    public void Entry_LastOptionWinsAndDefaultsToStart(string? expected, params string[] args)
    {
        Assert.Equal(expected, WasmLdCommandLine.Parse(args).Options.EntryPoint);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(true, "--stack-first")]
    [InlineData(false, "--no-stack-first")]
    [InlineData(false, "--stack-first", "--no-stack-first")]
    public void StackFirst_DefaultsToLinkerChoice(bool? expected, params string[] args)
    {
        Assert.Equal(expected, WasmLdCommandLine.Parse(args).Options.StackFirst);
    }

    [Theory]
    [InlineData(new[] { "a.o", "-o", "out/app.wasm" }, "app.wasm", true)]
    [InlineData(new[] { "a.o" }, "a.out", true)]
    [InlineData(new[] { "a.o", "-o", "app.wasm", "--soname=libapp.so" }, "libapp.so", true)]
    [InlineData(new[] { "a.o", "--soname", "libapp.so", "-o", "app.wasm", "--no-demangle" }, "libapp.so", false)]
    [InlineData(new[] { "a.o", "--no-demangle", "--demangle" }, "a.out", true)]
    public void NameSectionOptions_FollowWasmLd(string[] args, string moduleName, bool demangle)
    {
        var options = WasmLdCommandLine.Parse(args).Options;
        Assert.Equal(moduleName, options.ModuleName);
        Assert.Equal(demangle, options.Demangle);
    }

    [Fact]
    public void WholeArchive_AppliesToInputsInsideTheRegion()
    {
        var commandLine = WasmLdCommandLine.Parse(["a.a", "--whole-archive", "b.a", "c.o", "--no-whole-archive", "d.a"]);

        Assert.Equal(
            [new WasmLdInputFile("a.a", false), new WasmLdInputFile("b.a", true), new WasmLdInputFile("c.o", true), new WasmLdInputFile("d.a", false)],
            commandLine.Inputs);
    }

    [Fact]
    public void MaxMemory_AcceptsFullWasm32AddressSpace()
    {
        Assert.Equal(4294967296, WasmLdCommandLine.Parse(["--max-memory=4294967296"]).Options.MaxMemory);
    }

    [Fact]
    public void StripAllAndCompressRelocations_MapToLinkerOptions()
    {
        var options = WasmLdCommandLine.Parse(["-s", "--compress-relocations"]).Options;

        Assert.True(options.StripAll);
        Assert.True(options.CompressRelocations);
    }

    [Fact]
    public void InputsAndOutput_PreserveOrder()
    {
        var commandLine = WasmLdCommandLine.Parse(["a.o", "-o", "out.wasm", "b.o", "--whole-archive", "c.a", "--no-whole-archive"]);

        Assert.Equal("out.wasm", commandLine.OutputPath);
        Assert.Equal(["a.o", "b.o", "c.a"], commandLine.InputFiles);
    }

    [Fact]
    public void IgnoredOptions_AreReportedAsWarnings()
    {
        var commandLine = WasmLdCommandLine.Parse(["--fatal-warnings", "--error-limit=0"]);

        Assert.Single(commandLine.Warnings);
    }

    [Theory]
    [InlineData("--component-type=foo.wit")]
    [InlineData("--shared")]
    [InlineData("-z", "noexecstack")]
    [InlineData("-m", "wasm64")]
    [InlineData("--export")]
    [InlineData("--initial-memory=lots")]
    public void UnsupportedOrMalformedOptions_Throw(params string[] args)
    {
        Assert.Throws<WasmLdArgumentException>(() => WasmLdCommandLine.Parse(args));
    }

    [Fact]
    public void ResponseFile_IsTokenizedLikeWasmLd()
    {
        string dir = CreateTempDirectory();
        string rsp = Path.Combine(dir, "link.rsp");
        File.WriteAllText(rsp, "--export=a \"--export=b c\"\n'd.o' e\\ f.o\n  --stack-first\n");

        var commandLine = WasmLdCommandLine.Parse(["--rsp-quoting=posix", $"@{rsp}", "-o", "x.wasm"]);

        Assert.Equal(["a", "b c"], commandLine.Options.Exports);
        Assert.Equal(["d.o", "e f.o"], commandLine.InputFiles);
        Assert.True(commandLine.Options.StackFirst);
    }

    [Fact]
    public void ResponseFile_QuotingStyleMayBeASeparateArgumentAndFilesNest()
    {
        string dir = CreateTempDirectory();
        string inner = Path.Combine(dir, "inner.rsp");
        File.WriteAllText(inner, "\"C:\\x y\\a.o\"");
        string outer = Path.Combine(dir, "outer.rsp");
        File.WriteAllText(outer, $"b.o @{inner}");

        Assert.Equal(["b.o", "C:\\x y\\a.o"], WasmLdCommandLine.Parse(["--rsp-quoting", "windows", $"@{outer}"]).InputFiles);
        Assert.Throws<WasmLdArgumentException>(() => WasmLdCommandLine.Parse(["--rsp-quoting=cmd", $"@{outer}"]));
    }

    /// <summary>A response file that includes itself is an error (as LLVM's "recursive expansion"), not a stack overflow.</summary>
    [Fact]
    public void ResponseFile_RecursionIsAnErrorButRepetitionIsNot()
    {
        string dir = CreateTempDirectory();
        string self = Path.Combine(dir, "self.rsp");
        File.WriteAllText(self, $"a.o @{self}");
        string first = Path.Combine(dir, "first.rsp"), second = Path.Combine(dir, "second.rsp");
        File.WriteAllText(first, $"b.o @{second}");
        File.WriteAllText(second, $"c.o @{first}");
        string leaf = Path.Combine(dir, "leaf.rsp");
        File.WriteAllText(leaf, "d.o");
        string twice = Path.Combine(dir, "twice.rsp");
        File.WriteAllText(twice, $"@{leaf} @{leaf}");

        Assert.Contains("recursive", Assert.Throws<WasmLdArgumentException>(() => WasmLdCommandLine.Parse([$"@{self}"])).Message);
        Assert.Contains("recursive", Assert.Throws<WasmLdArgumentException>(() => WasmLdCommandLine.Parse([$"@{first}"])).Message);
        Assert.Equal(["d.o", "d.o"], WasmLdCommandLine.Parse([$"@{twice}"]).InputFiles);
    }

    [Fact]
    public void ResponseFile_WindowsQuotingKeepsBackslashes()
    {
        string dir = CreateTempDirectory();
        string rsp = Path.Combine(dir, "link.rsp");
        File.WriteAllText(rsp, "\"C:\\work dir\\a.o\" C:\\obj\\b.o \"say \\\"hi\\\"\"");

        var commandLine = WasmLdCommandLine.Parse(["--rsp-quoting=windows", $"@{rsp}"]);

        Assert.Equal(["C:\\work dir\\a.o", "C:\\obj\\b.o", "say \"hi\""], commandLine.InputFiles);
    }

    [Fact]
    public void LibrarySearch_ResolvesFromLibraryPaths()
    {
        string dir = CreateTempDirectory();
        string lib = Path.Combine(dir, "libfoo.a");
        File.WriteAllBytes(lib, []);

        Assert.Equal([lib], WasmLdCommandLine.Parse([$"-L{dir}", "-lfoo"]).InputFiles);
        Assert.Equal([lib], WasmLdCommandLine.Parse(["-L", dir, "-l", "foo"]).InputFiles);
        Assert.Throws<WasmLdArgumentException>(() => WasmLdCommandLine.Parse([$"-L{dir}", "-lbar"]));
    }

    [Fact]
    public void Driver_LinksModulesAndArchiveMembers()
    {
        string dir = CreateTempDirectory();
        string first = Path.Combine(dir, "first.wasm");
        File.WriteAllBytes(first, CreateModuleBytes("first", 1));
        string archive = Path.Combine(dir, "libsample.a");
        File.WriteAllBytes(archive, CreateArchive(("left.o", CreateModuleBytes("left", 2)), ("right.obj", CreateModuleBytes("right", 3))));
        string output = Path.Combine(dir, "out", "linked.wasm");

        var messages = new List<string>();
        WasmModule? result = WasmLdDriver.Run(WasmLdCommandLine.Parse([first, archive, "-o", output, "--no-entry", "--verbose"]), messages.Add);

        Assert.True(File.Exists(output));
        Assert.NotNull(result);
        Assert.Equal(["first", "left", "right"], result.Exports.Keys.Order());
        Assert.Equal(4, messages.Count);
    }

    [Fact]
    public void Driver_RequiresOutputAndInputs()
    {
        Assert.Throws<WasmLdArgumentException>(() => WasmLdDriver.Run(WasmLdCommandLine.Parse(["a.o"])));
        Assert.Throws<WasmLdArgumentException>(() => WasmLdDriver.Run(WasmLdCommandLine.Parse(["-o", "a.wasm"])));
    }

    internal static byte[] CreateModuleBytes(string exportName, int value) =>
        new SectionMerger().EmitBinary(WatCompiler.CompileAndValidate(
            $$"""
            (module
              (func (export "{{exportName}}") (result i32)
                (i32.const {{value}})))
            """));

    internal static byte[] CreateArchive(params (string Name, byte[] Data)[] members)
    {
        using var stream = new MemoryStream();
        WriteAscii(stream, "!<arch>\n");

        foreach ((string name, byte[] data) in members)
        {
            WriteAscii(stream, $"{name}/".PadRight(16));
            WriteAscii(stream, "0".PadRight(12));
            WriteAscii(stream, "0".PadRight(6));
            WriteAscii(stream, "0".PadRight(6));
            WriteAscii(stream, "100644".PadRight(8));
            WriteAscii(stream, data.Length.ToString(CultureInfo.InvariantCulture).PadRight(10));
            WriteAscii(stream, "`\n");
            stream.Write(data);
            if ((data.Length & 1) != 0)
                stream.WriteByte((byte)'\n');
        }

        return stream.ToArray();
    }

    internal static string CreateTempDirectory()
    {
        string dir = Path.Combine(Path.GetTempPath(), "wasm-link-tests", Path.GetRandomFileName());
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteAscii(Stream stream, string value) => stream.Write(Encoding.ASCII.GetBytes(value));
}
