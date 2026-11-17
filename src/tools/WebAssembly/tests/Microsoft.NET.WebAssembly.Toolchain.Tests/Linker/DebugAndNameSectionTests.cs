// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Tests for DWARF debug section pass-through and name section emission in the managed linker.
/// </summary>
public class DebugAndNameSectionTests
{
    private readonly WasmLinker _linker = new();

    // ── Debug section pass-through ───────────────────────────────────────────────

    [Fact]
    public void Link_DebugSections_PassThroughFromSingleModule()
    {
        byte[] debugBytes = [0x01, 0x02, 0x03, 0x04, 0x05];
        var module = WatCompiler.CompileAndValidate(
            "(module (func (export \"f\") (result i32) (i32.const 1)))");
        module.CustomSections.Add(new CustomSection(".debug_info", debugBytes));

        var merged = _linker.Link([module]);

        var debugSection = merged.CustomSections.FirstOrDefault(s => s.Name == ".debug_info");
        Assert.NotNull(debugSection);
        Assert.Equal(debugBytes, debugSection.Content);
    }

    [Fact]
    public void Link_DebugSections_ConcatenatedFromMultipleModules()
    {
        byte[] debugA = [0xAA, 0xBB];
        byte[] debugB = [0xCC, 0xDD, 0xEE];

        var modA = WatCompiler.CompileAndValidate(
            "(module (func (export \"a\") (result i32) (i32.const 1)))");
        modA.CustomSections.Add(new CustomSection(".debug_info", debugA));

        var modB = WatCompiler.CompileAndValidate(
            "(module (func (export \"b\") (result i32) (i32.const 2)))");
        modB.CustomSections.Add(new CustomSection(".debug_info", debugB));

        var merged = _linker.Link([modA, modB]);

        var debugSection = merged.CustomSections.FirstOrDefault(s => s.Name == ".debug_info");
        Assert.NotNull(debugSection);
        // Combined = debugA + debugB
        Assert.Equal(debugA.Length + debugB.Length, debugSection.Content.Length);
        Assert.Equal(debugA, debugSection.Content[..debugA.Length]);
        Assert.Equal(debugB, debugSection.Content[debugA.Length..]);
    }

    [Fact]
    public void Link_DebugSections_MultipleDebugSectionsPassedThrough()
    {
        var module = WatCompiler.CompileAndValidate(
            "(module (func (export \"f\") (result i32) (i32.const 0)))");
        module.CustomSections.Add(new CustomSection(".debug_info", [0x01]));
        module.CustomSections.Add(new CustomSection(".debug_abbrev", [0x02, 0x03]));
        module.CustomSections.Add(new CustomSection(".debug_str", [0x04, 0x05, 0x06]));

        var merged = _linker.Link([module]);

        Assert.Contains(merged.CustomSections, s => s.Name == ".debug_info");
        Assert.Contains(merged.CustomSections, s => s.Name == ".debug_abbrev");
        Assert.Contains(merged.CustomSections, s => s.Name == ".debug_str");
    }

    [Fact]
    public void Link_StripDebug_RemovesAllDebugSections()
    {
        var module = WatCompiler.CompileAndValidate(
            "(module (func (export \"f\") (result i32) (i32.const 0)))");
        module.CustomSections.Add(new CustomSection(".debug_info", [0x01, 0x02]));
        module.CustomSections.Add(new CustomSection(".debug_line", [0x03, 0x04]));

        var merged = _linker.Link([module], new LinkerOptions { StripDebug = true });

        Assert.DoesNotContain(merged.CustomSections, s => s.Name.StartsWith(".debug_", StringComparison.Ordinal));
    }

    [Fact]
    public void LinkToBinary_DebugSections_SurviveRoundTrip()
    {
        byte[] debugBytes = [0x10, 0x20, 0x30];
        var module = WatCompiler.CompileAndValidate(
            "(module (func (export \"f\") (result i32) (i32.const 42)))");
        module.CustomSections.Add(new CustomSection(".debug_info", debugBytes));

        byte[] binary = _linker.LinkToBinary([module]);
        var reparsed = ModuleReader.ReadModule(new MemoryStream(binary));

        var debugSection = reparsed.CustomSections.FirstOrDefault(s => s.Name == ".debug_info");
        Assert.NotNull(debugSection);
        Assert.Equal(debugBytes, debugSection.Content);
    }

    // ── Name section ─────────────────────────────────────────────────────────────

    [Fact]
    public void Link_NameSection_EmittedWhenSymbolNamesPresent()
    {
        var module = MakeModuleWithExportedFunction("my_function");

        var merged = _linker.Link([module]);

        Assert.Contains(merged.CustomSections, s => s.Name == "name");
    }

    [Fact]
    public void Link_NameSection_ContainsFunctionNames()
    {
        var module = MakeModuleWithExportedFunction("my_function");

        byte[] binary = _linker.LinkToBinary([module]);
        var reparsed = ModuleReader.ReadModule(new MemoryStream(binary));

        var nameSection = reparsed.CustomSections.FirstOrDefault(s => s.Name == "name");
        Assert.NotNull(nameSection);
        string sectionContent = System.Text.Encoding.UTF8.GetString(nameSection.Content);
        Assert.Contains("my_function", sectionContent);
    }

    [Fact]
    public void Link_NoNameSection_SuppressesNameSection()
    {
        var module = MakeModuleWithExportedFunction("my_function");

        var merged = _linker.Link([module], new LinkerOptions { NoNameSection = true });

        Assert.DoesNotContain(merged.CustomSections, s => s.Name == "name");
    }

    [Fact]
    public void Link_NameSection_AbsentWhenNoSymbolNames()
    {
        // A module with no linking section has no named symbols.
        var module = WatCompiler.CompileAndValidate(
            "(module (func (export \"f\") (result i32) (i32.const 0)))");
        // Ensure no linking section (WAT compiler doesn't produce one)
        Assert.Null(module.LinkingSection);

        var merged = _linker.Link([module]);

        Assert.DoesNotContain(merged.CustomSections, s => s.Name == "name");
    }

    [Fact]
    public void Link_NameSection_IndicesMatchMergedFunctions()
    {
        // Build two modules each with one named function symbol.
        var modA = MakeModuleWithExportedFunction("func_a");
        var modB = MakeModuleWithExportedFunction("func_b");

        byte[] binary = _linker.LinkToBinary([modA, modB]);
        var reparsed = ModuleReader.ReadModule(new MemoryStream(binary));

        var nameSection = reparsed.CustomSections.FirstOrDefault(s => s.Name == "name");
        Assert.NotNull(nameSection);

        // Parse subsection 1 (function names) to verify indices are valid.
        using var ms = new System.IO.MemoryStream(nameSection.Content);
        var reader = new Microsoft.NET.WebAssembly.Binary.WasmBinaryReader(ms);

        byte subsectionId = reader.ReadByte();
        Assert.Equal(1, subsectionId); // function names subsection

        uint subsectionLen = reader.ReadU32Leb128();
        Assert.True(subsectionLen > 0);

        uint count = reader.ReadU32Leb128();
        Assert.Equal(2u, count); // two named functions

        for (uint i = 0; i < count; i++)
        {
            uint funcIdx = reader.ReadU32Leb128();
            string name = reader.ReadName();
            Assert.True(funcIdx < 2, $"Function index {funcIdx} out of range for merged module with 2 functions");
            Assert.True(name is "func_a" or "func_b", $"Unexpected function name '{name}'");
        }
    }

    // ── Debug + name section interaction ────────────────────────────────────────

    [Fact]
    public void Link_DebugAndNameSections_BothPresent()
    {
        var module = MakeModuleWithExportedFunction("my_func");
        module.CustomSections.Add(new CustomSection(".debug_info", [0xDE, 0xAD]));

        var merged = _linker.Link([module]);

        Assert.Contains(merged.CustomSections, s => s.Name == ".debug_info");
        Assert.Contains(merged.CustomSections, s => s.Name == "name");
    }

    [Fact]
    public void Link_StripDebugKeepsNameSection()
    {
        var module = MakeModuleWithExportedFunction("keep_me");
        module.CustomSections.Add(new CustomSection(".debug_info", [0x01]));

        var merged = _linker.Link([module], new LinkerOptions { StripDebug = true });

        Assert.DoesNotContain(merged.CustomSections, s => s.Name.StartsWith(".debug_", StringComparison.Ordinal));
        Assert.Contains(merged.CustomSections, s => s.Name == "name");
    }

    // ── Debug section relocations ────────────────────────────────────────────────

    [Fact]
    public void Link_DebugSection_WithMemoryAddrRelocation_PatchedCorrectly()
    {
        // Build a module with a .debug_info section that has a memory-address relocation.
        // The relocation references a data symbol at offset 0 within data segment 0.
        // After merging, the data segment should be at offset 0 in linear memory.
        var funcType = new FuncType([], []);
        byte[] debugBytes = new byte[5]; // 5-byte padded LEB128 slot
        // Write an initial value of 0xFFFFFFFF (will be overwritten by reloc)
        debugBytes[0] = 0xFF; debugBytes[1] = 0xFF; debugBytes[2] = 0xFF;
        debugBytes[3] = 0xFF; debugBytes[4] = 0x0F;

        var dataSymbol = new WasmSymbol
        {
            Type = SymbolType.Data,
            Flags = SymbolFlags.None,
            Name = "my_data",
            SegmentIndex = 0,
            Offset = 0,
            Size = 4,
        };

        var linking = new LinkingSection
        {
            Version = 2,
            Symbols = [dataSymbol],
            SegmentInfos = [new SegmentInfo { Name = ".data", Alignment = 0, Flags = 0 }],
        };

        var reloc = new RelocationSection
        {
            Name = "reloc..debug_info",
            TargetSectionIndex = 5,
            Entries =
            [
                new Relocation
                {
                    Type = RelocationType.R_WASM_MEMORY_ADDR_LEB,
                    Offset = 0,
                    Index = 0,
                    Addend = 0,
                },
            ],
        };

        var module = new WasmModule
        {
            Types = [funcType],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            Memories = [new MemoryType(new Limits(1, null))],
            DataSegments =
            [
                new Data
                {
                    IsPassive = false,
                    MemoryIndex = 0,
                    OffsetExpr = [0x41, 0x00, 0x0B],
                    InitData = [0x01, 0x02, 0x03, 0x04],
                }
            ],
            LinkingSection = linking,
            RelocationSections = [reloc],
            CustomSections = [new CustomSection(".debug_info", debugBytes)],
        };
        module.CustomSections.Clear();
        module.CustomSections.Add(new CustomSection(".debug_info", debugBytes));

        var merged = _linker.Link([module], new LinkerOptions { AllowUndefined = true, GcSections = false });

        var debugSection = merged.CustomSections.FirstOrDefault(s => s.Name == ".debug_info");
        Assert.NotNull(debugSection);
        Assert.Equal(5, debugSection.Content.Length);

        // The memory address of data segment 0 at offset 0 should have been patched in.
        // Data segment 0 is placed at linear memory offset 0.
        // Read the LEB128 value from the patched bytes.
        uint patchedValue = ReadLeb128U32(debugSection.Content, 0);
        Assert.Equal(0u, patchedValue); // segment base 0 + symbol offset 0 + addend 0
    }

    // ── libSystem.Native.a integration test ─────────────────────────────────────

    [Fact]
    [Trait("Category", "Integration")]
    public void Link_LibSystemNative_ProducesDebugAndNameSections()
    {
        string libPath = System.IO.Path.Combine(
            System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile),
            ".dotnet", "packs",
            "Microsoft.NETCore.App.Runtime.Mono.browser-wasm",
            "10.0.5", "runtimes", "browser-wasm", "native", "libSystem.Native.a");

        if (!System.IO.File.Exists(libPath))
        {
            // Skip if the runtime pack is not installed on this machine.
            return;
        }

        using var archiveStream = System.IO.File.OpenRead(libPath);
        var modules = ArArchiveReader.ReadWasmModules(archiveStream, libPath)
            .Select(static t => t.Module)
            .ToList();

        Assert.NotEmpty(modules);

        var options = new LinkerOptions { AllowUndefined = true, GcSections = false };
        var merged = _linker.Link(modules, options);

        // Must have at least one .debug_* section
        var debugSections = merged.CustomSections
            .Where(s => s.Name.StartsWith(".debug_", StringComparison.Ordinal))
            .ToList();
        Assert.NotEmpty(debugSections);

        // Each input debug section is expected to be non-trivially large (sum of inputs)
        int totalInputDebugInfo = modules
            .SelectMany(m => m.CustomSections)
            .Where(s => s.Name == ".debug_info")
            .Sum(s => s.Content.Length);

        if (totalInputDebugInfo > 0)
        {
            var outputDebugInfo = merged.CustomSections.FirstOrDefault(s => s.Name == ".debug_info");
            Assert.NotNull(outputDebugInfo);
            Assert.Equal(totalInputDebugInfo, outputDebugInfo.Content.Length);
        }

        // Name section should also be present (many symbols in libSystem.Native)
        Assert.Contains(merged.CustomSections, s => s.Name == "name");
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a module with one function that has a linking-section symbol with the given name,
    /// so that the name section will include it.
    /// </summary>
    private static WasmModule MakeModuleWithExportedFunction(string symbolName)
    {
        var funcSym = new WasmSymbol
        {
            Type = SymbolType.Function,
            Flags = SymbolFlags.Exported,
            Name = symbolName,
            Index = 0,
        };

        var linking = new LinkingSection
        {
            Version = 2,
            Symbols = [funcSym],
        };

        return new WasmModule
        {
            Types = [new FuncType([], [ValType.I32])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x41, 0x00, 0x0B] }],
            Exports = new System.Collections.Generic.OrderedDictionary<string, Export>
            {
                [symbolName] = new Export(symbolName, new ExportDescriptor.Func(0)),
            },
            LinkingSection = linking,
        };
    }

    private static uint ReadLeb128U32(byte[] bytes, int offset)
    {
        uint result = 0;
        int shift = 0;
        while (true)
        {
            byte b = bytes[offset++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            shift += 7;
        }
    }
}
