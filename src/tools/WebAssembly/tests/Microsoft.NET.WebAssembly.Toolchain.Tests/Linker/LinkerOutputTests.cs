// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

/// <summary>How the linker writes its output (#600 P7): once, without intermediate copies, and validated only when asked.</summary>
public class LinkerOutputTests
{
    [Fact]
    public void EmitWritesTheModuleWithoutIntermediateCopies()
    {
        // #600 H5: on the 17 MB NesmWasm core, EmitBinary allocated 6.45x its output and WriteBinary 3.29x: a stream per
        // section, a locals array per function, then the whole module copied out of a MemoryStream. Now the output
        // array is the only module-sized allocation (1.06x here) and WriteBinary allocates 0.06x; one more copy of
        // the data alone (0.2x) fails both bounds.
        WasmModule module = CreateLargeModule();
        var merger = new SectionMerger();
        byte[] output = merger.EmitBinary(module);
        Assert.True(output.Length > 8_000_000, $"output {output.Length}");

        long emit = MeasureAllocation(() => merger.EmitBinary(module));
        Assert.True(emit < output.Length * 1.1, $"EmitBinary: {output.Length} B output, {emit} B allocated ({(double)emit / output.Length:F2}x)");

        long write = MeasureAllocation(() => merger.WriteBinary(Stream.Null, module));
        Assert.True(write < output.Length * 0.1, $"WriteBinary: {output.Length} B output, {write} B allocated ({(double)write / output.Length:F2}x)");

        var stream = new MemoryStream();
        merger.WriteBinary(stream, module);
        Assert.Equal(output, stream.ToArray());
    }

    [Fact]
    public void ValidateOutputDecidesWhetherTheOutputIsReadBack()
    {
        // The merge copies bodies verbatim, so a body that pops an empty stack survives into the output, and only
        // reading the output back can reject it.
        var linker = new WasmLinker();
        Assert.Throws<WasmValidationException>(() => linker.LinkToBinary([CreateInvalidModule()]));

        byte[] bytes = linker.LinkToBinary([CreateInvalidModule()], new LinkerOptions { ValidateOutput = false });
        WasmModule parsed = ModuleReader.ParseModuleForAnalysis(new MemoryStream(bytes));
        Assert.Equal([0x6A, 0x0B], parsed.Functions[0].Body);

        linker.LinkToBinary([new LinkerInput(CreateInvalidModule())], new LinkerOptions { ValidateOutput = false }, out WasmModule? unvalidated);
        Assert.Null(unvalidated);
        byte[] validBytes = linker.LinkToBinary([new LinkerInput(CreateValidModule())], null, out WasmModule? validated);
        Assert.NotNull(validated);
        Assert.Equal(validBytes, linker.LinkToBinary([new LinkerInput(CreateValidModule())], new LinkerOptions { ValidateOutput = false }, out _));
    }

    [Fact]
    public void LinkToWritesNothingUnlessTheLinkSucceeds()
    {
        var linker = new WasmLinker();
        var output = new MemoryStream();
        Assert.Throws<WasmValidationException>(() => linker.LinkTo(output, [new LinkerInput(CreateInvalidModule())]));
        Assert.Equal(0, output.Length);

        byte[] expected = linker.LinkToBinary([CreateValidModule()]);
        Assert.NotNull(linker.LinkTo(output, [new LinkerInput(CreateValidModule())]));
        Assert.Equal(expected, output.ToArray());

        var fromModules = new MemoryStream();
        Assert.NotNull(linker.LinkTo(fromModules, [CreateValidModule()]));
        Assert.Equal(expected, fromModules.ToArray());

        // Unvalidated output is streamed, so the destination need not be seekable.
        var streamed = new WriteOnlyStream();
        Assert.Null(linker.LinkTo(streamed, [new LinkerInput(CreateValidModule())], new LinkerOptions { ValidateOutput = false }));
        Assert.Equal(expected, streamed.Written.ToArray());
    }

    [Fact]
    public void DriverWritesTheOutputOnlyAfterTheLinkSucceeds()
    {
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string first = Path.Combine(dir, "first.wasm");
        File.WriteAllBytes(first, WasmLdCommandLineTests.CreateModuleBytes("first", 1));
        string output = Path.Combine(dir, "linked.wasm");

        // A failed link leaves an existing output alone.
        File.WriteAllBytes(output, [1, 2, 3]);
        Assert.Throws<LinkerException>(() => WasmLdDriver.Run(WasmLdCommandLine.Parse([first, "-o", output, "--no-entry", "--initial-memory=1000"])));
        Assert.Equal([1, 2, 3], File.ReadAllBytes(output));

        Assert.NotNull(WasmLdDriver.Run(WasmLdCommandLine.Parse([first, "-o", output, "--no-entry"])));
        byte[] validated = File.ReadAllBytes(output);

        WasmLdCommandLine unvalidated = WasmLdCommandLine.Parse([first, "-o", output, "--no-entry"]);
        unvalidated.Options.ValidateOutput = false;
        Assert.Null(WasmLdDriver.Run(unvalidated));
        Assert.Equal(validated, File.ReadAllBytes(output));
    }

    [ConditionalFact]
    public void DriverKeepsAnOutputItCannotOpen()
    {
        if (!OperatingSystem.IsWindows() && Environment.UserName == "root")
            throw new SkipTestException("root can write a read-only file.");

        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string first = Path.Combine(dir, "first.wasm");
        File.WriteAllBytes(first, WasmLdCommandLineTests.CreateModuleBytes("first", 1));
        string output = Path.Combine(dir, "linked.wasm");
        File.WriteAllBytes(output, [1, 2, 3]);
        File.SetAttributes(output, FileAttributes.ReadOnly);
        try
        {
            // The link succeeds; only opening the output fails, and the user's file is not deleted for it.
            Assert.Throws<UnauthorizedAccessException>(() => WasmLdDriver.Run(WasmLdCommandLine.Parse([first, "-o", output, "--no-entry"])));
            Assert.Equal([1, 2, 3], File.ReadAllBytes(output));
        }
        finally
        {
            File.SetAttributes(output, FileAttributes.Normal);
        }
    }

    [Fact]
    public void DriverReportsTheErrorFromOpeningTheOutput()
    {
        string dir = WasmLdCommandLineTests.CreateTempDirectory();
        string first = Path.Combine(dir, "first.wasm");
        File.WriteAllBytes(first, WasmLdCommandLineTests.CreateModuleBytes("first", 1));
        string output = Path.Combine(dir, "linked.wasm");
        Directory.CreateDirectory(output);

        Exception? expected = Record.Exception(() => new FileStream(output, FileMode.Create, FileAccess.Write, FileShare.None).Dispose());
        Assert.NotNull(expected);
        Exception actual = Assert.ThrowsAny<Exception>(() => WasmLdDriver.Run(WasmLdCommandLine.Parse([first, "-o", output, "--no-entry"])));
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(expected.Message, actual.Message);
        // On macOS deleting the directory fails with the same type and message, so the throwing frame tells them apart.
        Assert.DoesNotContain("System.IO.FileSystem.DeleteFile", actual.ToString(), StringComparison.Ordinal);
        Assert.True(Directory.Exists(output));
    }

    [Fact]
    public void DriverDeletesAnOutputItFailedToFinish()
    {
        string output = Path.Combine(WasmLdCommandLineTests.CreateTempDirectory(), "linked.wasm");
        var failure = new IOException("disk full");

        IOException thrown = Assert.Throws<IOException>(() => WasmLdDriver.WriteOutput(output, stream =>
        {
            stream.Write([0x00, 0x61, 0x73, 0x6D]);
            stream.Flush();
            throw failure;
        }));

        Assert.Same(failure, thrown);
        Assert.False(File.Exists(output));
    }

    [Fact]
    public void AnEncodingErrorWritesNothing()
    {
        // Main wrote the header and the sections before the data section (36 bytes) before rejecting the offset.
        var module = new WasmModule
        {
            Types = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            Memories = [new MemoryType(new Limits(1))],
            DataSegments = [new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0xFF, 0x0B], InitData = [1] }],
        };
        module.Exports["f"] = new Export("f", new ExportDescriptor.Func(0));

        var output = new MemoryStream();
        Assert.Throws<LinkerException>(() => new SectionMerger().WriteBinary(output, module));
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void ASectionTooLargeForTheFormatWritesNothing()
    {
        // 4,300 functions sharing one 1 MB body make a code section over 4 GiB without allocating it.
        var body = new byte[1 << 20];
        body[^1] = 0x0B;
        var module = new WasmModule { Types = [new FuncType([], [])], Functions = new List<WasmFunction>(4300) };
        for (int i = 0; i < 4300; i++)
            module.Functions.Add(new WasmFunction { TypeIndex = 0, Locals = [], Body = body });

        var output = new MemoryStream();
        LinkerException error = Assert.Throws<LinkerException>(() => new SectionMerger().WriteBinary(output, module));
        Assert.Contains("code section", error.Message, StringComparison.Ordinal);
        Assert.Equal(0, output.Length);
    }

    [Fact]
    public void DataCountIsWrittenOnlyForPassiveSegments()
    {
        byte[] Emit(bool passive) => new SectionMerger().EmitBinary(new WasmModule
        {
            DataSegments = [new Data { IsPassive = passive, MemoryIndex = passive ? -1 : 0, OffsetExpr = passive ? [] : [0x41, 0x00, 0x0B], InitData = [0xAA] }],
        });

        // Header, data count (12) of 1 segment, then the data section (11): one passive segment holding 0xAA.
        Assert.Equal([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00, 0x0C, 0x01, 0x01, 0x0B, 0x04, 0x01, 0x01, 0x01, 0xAA], Emit(passive: true));
        // An active segment needs no data count: just the data section, with mode 0 and its offset.
        Assert.Equal([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00, 0x0B, 0x07, 0x01, 0x00, 0x41, 0x00, 0x0B, 0x01, 0xAA], Emit(passive: false));
    }

    // A body that pops an empty stack: `i32.add; end`.
    private static WasmModule CreateInvalidModule() => CreateModule([0x6A, 0x0B]);

    private static WasmModule CreateValidModule() => CreateModule([0x41, 0x2A, 0x0B]);

    private static WasmModule CreateModule(byte[] body)
    {
        var module = new WasmModule
        {
            Types = [new FuncType([], [ValType.I32])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = body }],
        };
        module.Exports["f"] = new Export("f", new ExportDescriptor.Func(0));
        return module;
    }

    private sealed class WriteOnlyStream : Stream
    {
        public MemoryStream Written { get; } = new();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => Written.Write(buffer, offset, count);
    }

    // A warm-up run first, so first-call and unoptimized-code costs (type loading, tier-0 and OSR) are not counted.
    private static long MeasureAllocation(Action action)
    {
        action();
        long before = GC.GetAllocatedBytesForCurrentThread();
        action();
        return GC.GetAllocatedBytesForCurrentThread() - before;
    }

    /// <summary>
    /// About 10 MB shaped like a real link: 20,000 functions with locals (4 MB of code), 2 MB of data and 4 MB of
    /// DWARF-sized custom content.
    /// </summary>
    private static WasmModule CreateLargeModule()
    {
        const int Functions = 20_000;
        var body = new byte[200];
        for (int i = 0; i + 2 < body.Length; i += 3)
        {
            body[i] = 0x41; // i32.const 0
            body[i + 1] = 0x00;
            body[i + 2] = 0x1A; // drop
        }
        body[^2] = 0x01; // nop
        body[^1] = 0x0B; // end

        List<ValType> locals = [ValType.I32, ValType.I32, ValType.I64, ValType.F64];
        var module = new WasmModule
        {
            Types = [new FuncType([], [])],
            Functions = new List<WasmFunction>(Functions),
            Memories = [new MemoryType(new Limits(64))],
            DataSegments =
            [
                new Data { IsPassive = false, MemoryIndex = 0, OffsetExpr = [0x41, 0x80, 0x08, 0x0B], InitData = new byte[1_500_000] },
                new Data { IsPassive = true, MemoryIndex = -1, OffsetExpr = [], InitData = new byte[500_000] },
            ],
        };
        for (int i = 0; i < Functions; i++)
            module.Functions.Add(new WasmFunction { TypeIndex = 0, Locals = locals, Body = body });
        module.Exports["f"] = new Export("f", new ExportDescriptor.Func(0));
        module.CustomSections.Add(new CustomSection(".debug_info", new byte[3_000_000]));
        module.CustomSections.Add(new CustomSection(".debug_line", new byte[1_000_000]));
        return module;
    }
}
