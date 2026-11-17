// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

public class ModuleReaderTests
{
    private static readonly byte[] Header = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    private static byte[] BuildModule(params byte[][] sections)
    {
        int totalLength = Header.Length;
        foreach (var s in sections)
            totalLength += s.Length;
        var result = new byte[totalLength];
        Header.CopyTo(result, 0);
        int offset = Header.Length;
        foreach (var s in sections)
        {
            s.CopyTo(result, offset);
            offset += s.Length;
        }
        return result;
    }

    private static byte[] BuildSection(byte sectionId, params byte[][] payloadParts)
    {
        int payloadLength = 0;
        foreach (var part in payloadParts)
            payloadLength += part.Length;

        byte[] lengthBytes = EncodeU32((uint)payloadLength);
        var result = new byte[1 + lengthBytes.Length + payloadLength];
        result[0] = sectionId;
        lengthBytes.CopyTo(result, 1);

        int offset = 1 + lengthBytes.Length;
        foreach (var part in payloadParts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] BuildCustomSection(string name, params byte[][] contentParts)
    {
        var payloadParts = new byte[1 + contentParts.Length][];
        payloadParts[0] = EncodeName(name);
        Array.Copy(contentParts, 0, payloadParts, 1, contentParts.Length);
        return BuildSection(0, payloadParts);
    }

    private static byte[] BuildLinkingSubsection(byte subsectionType, params byte[][] payloadParts)
    {
        int payloadLength = 0;
        foreach (var part in payloadParts)
            payloadLength += part.Length;

        byte[] lengthBytes = EncodeU32((uint)payloadLength);
        var result = new byte[1 + lengthBytes.Length + payloadLength];
        result[0] = subsectionType;
        lengthBytes.CopyTo(result, 1);

        int offset = 1 + lengthBytes.Length;
        foreach (var part in payloadParts)
        {
            part.CopyTo(result, offset);
            offset += part.Length;
        }

        return result;
    }

    private static byte[] EncodeName(string value)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(value);
        byte[] lengthBytes = EncodeU32((uint)utf8.Length);
        var result = new byte[lengthBytes.Length + utf8.Length];
        lengthBytes.CopyTo(result, 0);
        utf8.CopyTo(result, lengthBytes.Length);
        return result;
    }

    private static byte[] EncodeU32(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0)
                next |= 0x80;
            bytes.Add(next);
        } while (value != 0);

        return [.. bytes];
    }

    private static byte[] EncodeS32(int value)
    {
        var bytes = new List<byte>();
        bool more = true;
        while (more)
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            bool signBitSet = (next & 0x40) != 0;
            more = !((value == 0 && !signBitSet) || (value == -1 && signBitSet));
            if (more)
                next |= 0x80;
            bytes.Add(next);
        }

        return [.. bytes];
    }

    [Fact]
    public void MinimalValidModule_ReturnsEmptyModule()
    {
        var module = ModuleReader.ReadModule(new MemoryStream(Header));

        Assert.Empty(module.Types);
        Assert.Empty(module.Imports);
        Assert.Empty(module.Functions);
        Assert.Empty(module.Tables);
        Assert.Empty(module.Memories);
        Assert.Empty(module.Globals);
        Assert.Empty(module.Exports);
        Assert.Null(module.StartFunction);
        Assert.Empty(module.Elements);
        Assert.Empty(module.DataSegments);
        Assert.Empty(module.CustomSections);
    }

    [Fact]
    public void MinimalValidModule_FromNonSeekableStream_ReturnsEmptyModule()
    {
        using var stream = new NonSeekableReadStream(Header);

        var module = ModuleReader.ReadModule(stream);

        Assert.Empty(module.Functions);
        Assert.Empty(module.Exports);
    }

    [Fact]
    public void StreamReader_ParsesSameModuleAsByteArrayPath()
    {
        byte[] typeSection = [
            0x01,                   // section id: type
            0x04,                   // section size
            0x01,                   // one type
            0x60, 0x00, 0x00,      // func () -> ()
        ];

        byte[] functionSection = [
            0x03,                   // section id: function
            0x03,                   // section size
            0x02,                   // two functions
            0x00, 0x00,            // both use type 0
        ];

        byte[] exportSection = [
            0x07,                   // section id: export
            0x0F,                   // section size
            0x02,                   // two exports
            0x04, 0x69, 0x6E, 0x63, 0x31, 0x00, 0x00, // "inc1" -> func 0
            0x04, 0x63, 0x6F, 0x6E, 0x73, 0x00, 0x01, // "cons" -> func 1
        ];

        byte[] customSection = [
            0x00,                   // section id: custom
            0x07,                   // section size
            0x04, 0x6D, 0x65, 0x74, 0x61, // name "meta"
            0xDE, 0xAD,            // custom payload
        ];

        byte[] codeSection = [
            0x0A,                   // section id: code
            0x0D,                   // section size
            0x02,                   // two code entries
            0x04,                   // body size
            0x00,                   // no locals
            0x41, 0x01, 0x0B,      // i32.const 1; end
            0x06,                   // body size
            0x01,                   // one local decl
            0x02, 0x7F,            // two i32 locals
            0x41, 0x07, 0x0B,      // i32.const 7; end
        ];

        byte[] bytes = BuildModule(typeSection, functionSection, exportSection, customSection, codeSection);

        var fromBytes = ModuleReader.ReadModule(new MemoryStream(bytes));
        using var stream = new NonSeekableReadStream(bytes);
        var fromStream = ModuleReader.ReadModule(stream);

        AssertEquivalent(fromBytes, fromStream);
        Assert.Equal([ValType.I32, ValType.I32], fromStream.Functions[1].Locals);
        Assert.Equal([0x41, 0x07, 0x0B], fromStream.Functions[1].Body);
    }

    [Fact]
    public void LinkingAndRelocationCustomSections_AreParsed()
    {
        byte[] dataSection = BuildSection(
            11,
            EncodeU32(1),
            EncodeU32(1),
            EncodeU32(1),
            [0x2A]);

        byte[] segmentInfoSubsection = BuildLinkingSubsection(
            5,
            EncodeU32(1),
            EncodeName(".rodata"),
            EncodeU32(2),
            EncodeU32((uint)(SegmentFlags.Strings | SegmentFlags.Retain)));

        byte[] symbolTableSubsection = BuildLinkingSubsection(
            8,
            EncodeU32(4),
            [(byte)SymbolType.Function], EncodeU32(0), EncodeU32(0), EncodeName("func0"),
            [(byte)SymbolType.Function], EncodeU32((uint)(SymbolFlags.Undefined | SymbolFlags.ExplicitName)), EncodeU32(0), EncodeName("puts"),
            [(byte)SymbolType.Data], EncodeU32(0), EncodeName("data0"), EncodeU32(0), EncodeU32(0), EncodeU32(1),
            [(byte)SymbolType.Section], EncodeU32(0), EncodeU32(11));

        byte[] initFuncsSubsection = BuildLinkingSubsection(
            6,
            EncodeU32(1),
            EncodeU32(100),
            EncodeU32(0));

        byte[] comdatInfoSubsection = BuildLinkingSubsection(
            7,
            EncodeU32(1),
            EncodeName("group0"),
            EncodeU32(0),
            EncodeU32(2),
            [(byte)ComdatKind.Data], EncodeU32(0),
            [(byte)ComdatKind.Section], EncodeU32(11));

        byte[] linkingSection = BuildCustomSection(
            "linking",
            EncodeU32(2),
            segmentInfoSubsection,
            symbolTableSubsection,
            initFuncsSubsection,
            comdatInfoSubsection);

        byte[] relocationSection = BuildCustomSection(
            "reloc.DATA",
            EncodeU32(11),
            EncodeU32(2),
            [(byte)RelocationType.R_WASM_MEMORY_ADDR_LEB], EncodeU32(0), EncodeU32(2), EncodeS32(4),
            [(byte)RelocationType.R_WASM_FUNCTION_INDEX_LEB], EncodeU32(5), EncodeU32(0));

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(dataSection, linkingSection, relocationSection)));

        var linking = Assert.IsType<LinkingSection>(module.LinkingSection);
        Assert.Equal(2u, linking.Version);

        var segment = Assert.Single(linking.SegmentInfos);
        Assert.Equal(".rodata", segment.Name);
        Assert.Equal(2u, segment.Alignment);
        Assert.Equal(SegmentFlags.Strings | SegmentFlags.Retain, segment.Flags);

        Assert.Equal(4, linking.Symbols.Count);
        Assert.Equal("func0", linking.Symbols[0].Name);
        Assert.Equal(0, linking.Symbols[0].Index);
        Assert.Equal(SymbolFlags.Undefined | SymbolFlags.ExplicitName, linking.Symbols[1].Flags);
        Assert.Equal("puts", linking.Symbols[1].Name);
        Assert.Equal("data0", linking.Symbols[2].Name);
        Assert.Equal(0u, linking.Symbols[2].SegmentIndex);
        Assert.Equal(1u, linking.Symbols[2].Size);
        Assert.Equal(11u, linking.Symbols[3].SectionIndex);

        var initFunc = Assert.Single(linking.InitFunctions);
        Assert.Equal(100u, initFunc.Priority);
        Assert.Equal(0u, initFunc.SymbolIndex);

        var comdat = Assert.Single(linking.ComdatGroups);
        Assert.Equal("group0", comdat.Name);
        Assert.Equal(2, comdat.Entries.Count);
        Assert.Equal(ComdatKind.Data, comdat.Entries[0].Kind);
        Assert.Equal(ComdatKind.Section, comdat.Entries[1].Kind);

        var relocation = Assert.Single(module.RelocationSections);
        Assert.Equal("reloc.DATA", relocation.Name);
        Assert.Equal(11u, relocation.TargetSectionIndex);
        Assert.Equal(2, relocation.Entries.Count);
        Assert.Equal(RelocationType.R_WASM_MEMORY_ADDR_LEB, relocation.Entries[0].Type);
        Assert.Equal(4, relocation.Entries[0].Addend);
        Assert.Equal(RelocationType.R_WASM_FUNCTION_INDEX_LEB, relocation.Entries[1].Type);
        Assert.Equal(0, relocation.Entries[1].Addend);
    }

    [Fact]
    public void ProducersAndTargetFeaturesCustomSections_AreParsed()
    {
        byte[] producersSection = BuildCustomSection(
            "producers",
            EncodeU32(2),
            EncodeName("language"),
            EncodeU32(1),
            EncodeName("C"),
            EncodeName("17"),
            EncodeName("processed-by"),
            EncodeU32(2),
            EncodeName("clang"),
            EncodeName("18.0.0"),
            EncodeName("wasm-ld"),
            EncodeName("18.0.0"));

        byte[] targetFeaturesSection = BuildCustomSection(
            "target_features",
            EncodeU32(4),
            [(byte)'+'], EncodeName("mutable-globals"),
            [(byte)'+'], EncodeName("sign-ext"),
            [(byte)'='], EncodeName("bulk-memory"),
            [(byte)'-'], EncodeName("simd128"));

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(producersSection, targetFeaturesSection)));

        var producers = Assert.IsType<ProducersSection>(module.ProducersSection);
        Assert.Collection(
            producers.Fields["language"],
            value =>
            {
                Assert.Equal("C", value.Name);
                Assert.Equal("17", value.Version);
            });
        Assert.Collection(
            producers.Fields["processed-by"],
            value =>
            {
                Assert.Equal("clang", value.Name);
                Assert.Equal("18.0.0", value.Version);
            },
            value =>
            {
                Assert.Equal("wasm-ld", value.Name);
                Assert.Equal("18.0.0", value.Version);
            });

        var targetFeatures = Assert.IsType<TargetFeaturesSection>(module.TargetFeaturesSection);
        List<(char Prefix, string Name)> expectedFeatures =
            [('+', "mutable-globals"), ('+', "sign-ext"), ('=', "bulk-memory"), ('-', "simd128")];
        Assert.Equal(expectedFeatures, targetFeatures.Features);
    }

    [Fact]
    public void RealWasmFiles_ExposeParsedProducerMetadataAndTargetFeatures()
    {
        foreach (string sampleName in new[] { "add.wasm", "math.wasm" })
        {
            var module = ModuleReader.ReadModule(new MemoryStream(File.ReadAllBytes(WasmAssert.TestAssetPath(Path.Combine("c", sampleName)))));
            var producers = Assert.IsType<ProducersSection>(module.ProducersSection);
            var processedBy = Assert.Single(producers.Fields["processed-by"]);
            Assert.Equal("clang", processedBy.Name);
            Assert.Contains("19.1.0", processedBy.Version, StringComparison.Ordinal);

            var targetFeatures = Assert.IsType<TargetFeaturesSection>(module.TargetFeaturesSection);
            Assert.Contains(targetFeatures.Features, feature => feature is ('+', "mutable-globals"));
            Assert.Contains(targetFeatures.Features, feature => feature is ('+', "multivalue"));
        }
    }

    [Fact]
    public void TypeSection_ParsesFuncType()
    {
        // Type section: one func type (i32, i32) -> (i32)
        byte[] typeSection = [
            0x01,                   // section id: type
            0x07,                   // section size
            0x01,                   // one type
            0x60,                   // func type tag
            0x02, 0x7F, 0x7F,      // params: 2x i32
            0x01, 0x7F,            // results: 1x i32
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection)));

        Assert.Single(module.Types);
        var funcType = module.Types[0];
        Assert.Equal([ValType.I32, ValType.I32], funcType.Parameters);
        Assert.Equal([ValType.I32], funcType.Results);
    }

    [Fact]
    public void FunctionAndCodeSections_ParsesFunction()
    {
        // Type section: (i32, i32) -> (i32)
        byte[] typeSection = [
            0x01, 0x07,
            0x01, 0x60, 0x02, 0x7F, 0x7F, 0x01, 0x7F,
        ];

        // Function section: one function referencing type 0
        byte[] funcSection = [0x03, 0x02, 0x01, 0x00];

        // Code section: local.get 0, local.get 1, i32.add, end
        byte[] codeSection = [
            0x0A,                   // section id: code
            0x09,                   // section size
            0x01,                   // one code entry
            0x07,                   // body size
            0x00,                   // no locals
            0x20, 0x00,             // local.get 0
            0x20, 0x01,             // local.get 1
            0x6A,                   // i32.add
            0x0B,                   // end
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection, funcSection, codeSection)));

        Assert.Single(module.Functions);
        var func = module.Functions[0];
        Assert.Equal(0, func.TypeIndex);
        Assert.Empty(func.Locals);
        Assert.Equal([0x20, 0x00, 0x20, 0x01, 0x6A, 0x0B], func.Body);
    }

    [Fact]
    public void ExportSection_ParsesFunctionExport()
    {
        byte[] exportSection = [
            0x07,                           // section id: export
            0x07,                           // section size
            0x01,                           // one export
            0x03, 0x61, 0x64, 0x64,        // name "add"
            0x00,                           // export kind: func
            0x00,                           // function index 0
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(exportSection)));

        Assert.Single(module.Exports);
        var export = module.Exports.GetAt(0).Value;
        Assert.Equal("add", export.Name);
        var funcDesc = Assert.IsType<ExportDescriptor.Func>(export.Descriptor);
        Assert.Equal(0, funcDesc.FunctionIndex);
    }

    [Fact]
    public void ImportSection_ParsesFunctionImport()
    {
        byte[] importSection = [
            0x02,                                       // section id: import
            0x0B,                                       // section size
            0x01,                                       // one import
            0x03, 0x65, 0x6E, 0x76,                    // module name "env"
            0x03, 0x6C, 0x6F, 0x67,                    // field name "log"
            0x00,                                       // import kind: func
            0x00,                                       // type index 0
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(importSection)));

        Assert.Single(module.Imports);
        var import = module.Imports[0];
        Assert.Equal("env", import.ModuleName);
        Assert.Equal("log", import.Name);
        var funcDesc = Assert.IsType<ImportDescriptor.Func>(import.Descriptor);
        Assert.Equal(0, funcDesc.TypeIndex);
    }

    [Fact]
    public void MemorySection_ParsesMemory()
    {
        byte[] memorySection = [
            0x05,       // section id: memory
            0x03,       // section size
            0x01,       // one memory
            0x00,       // limits flag: min only
            0x01,       // min = 1
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(memorySection)));

        Assert.Single(module.Memories);
        var mem = module.Memories[0];
        Assert.Equal(1u, mem.Limits.Min);
        Assert.Null(mem.Limits.Max);
    }

    [Fact]
    public void GlobalSection_ParsesMutableGlobal()
    {
        byte[] globalSection = [
            0x06,                   // section id: global
            0x06,                   // section size
            0x01,                   // one global
            0x7F,                   // content type: i32
            0x01,                   // mutability: var
            0x41, 0x2A,             // i32.const 42
            0x0B,                   // end
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(globalSection)));

        Assert.Single(module.Globals);
        var global = module.Globals[0];
        Assert.Equal(ValType.I32, global.Type.ContentType);
        Assert.Equal(Mutability.Var, global.Type.Mutability);
        Assert.Equal([0x41, 0x2A, 0x0B], global.InitExpr);
    }

    [Fact]
    public void FullAddModule_ParsesCorrectly()
    {
        byte[] addModule = [
            0x00, 0x61, 0x73, 0x6D, // magic
            0x01, 0x00, 0x00, 0x00, // version 1
            // Type section (id=1)
            0x01,                   // section id
            0x07,                   // section size
            0x01,                   // one type
            0x60,                   // func type tag
            0x02, 0x7F, 0x7F,      // params: 2x i32
            0x01, 0x7F,            // results: 1x i32
            // Function section (id=3)
            0x03,                   // section id
            0x02,                   // section size
            0x01,                   // one function
            0x00,                   // type index 0
            // Export section (id=7)
            0x07,                   // section id
            0x07,                   // section size
            0x01,                   // one export
            0x03, 0x61, 0x64, 0x64, // name "add"
            0x00,                   // export kind: func
            0x00,                   // function index 0
            // Code section (id=10)
            0x0A,                   // section id
            0x09,                   // section size
            0x01,                   // one code entry
            0x07,                   // body size
            0x00,                   // no locals
            0x20, 0x00,             // local.get 0
            0x20, 0x01,             // local.get 1
            0x6A,                   // i32.add
            0x0B,                   // end
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(addModule));

        // Types
        Assert.Single(module.Types);
        Assert.Equal([ValType.I32, ValType.I32], module.Types[0].Parameters);
        Assert.Equal([ValType.I32], module.Types[0].Results);

        // Functions
        Assert.Single(module.Functions);
        Assert.Equal(0, module.Functions[0].TypeIndex);
        Assert.Empty(module.Functions[0].Locals);
        Assert.Equal([0x20, 0x00, 0x20, 0x01, 0x6A, 0x0B], module.Functions[0].Body);

        // Exports
        Assert.Single(module.Exports);
        Assert.Equal("add", module.Exports.GetAt(0).Value.Name);
        var desc = Assert.IsType<ExportDescriptor.Func>(module.Exports.GetAt(0).Value.Descriptor);
        Assert.Equal(0, desc.FunctionIndex);
    }

    [Fact]
    public void InvalidMagic_ThrowsWasmFormatException()
    {
        byte[] badMagic = [0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00];

        Assert.Throws<WasmFormatException>(() => ModuleReader.ReadModule(new MemoryStream(badMagic)));
    }

    [Fact]
    public void InvalidVersion_ThrowsWasmFormatException()
    {
        byte[] badVersion = [0x00, 0x61, 0x73, 0x6D, 0x02, 0x00, 0x00, 0x00];

        Assert.Throws<WasmFormatException>(() => ModuleReader.ReadModule(new MemoryStream(badVersion)));
    }

    [Fact]
    public void ComponentModelBinary_ThrowsWasmComponentException()
    {
        // Component Model header: magic \0asm + version \0d\00\01\00 (layer 1)
        byte[] componentBinary = [0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00];

        var ex = Assert.Throws<WasmComponentException>(() => ModuleReader.ReadModule(new MemoryStream(componentBinary)));
        Assert.Contains("WASI Preview 2", ex.Message);
        Assert.Contains("wasm-tools component lower", ex.Message);
    }

    [Fact]
    public void TypeSection_ParsesRecGroup_WithStructAndFunc()
    {
        // Type section: rec group containing struct + func, then array type
        // rec(struct(i32 mut, f64 const), func(i32) -> (i32)), array(i32 mut)
        byte[] typeSection = [
            0x01,                   // section id: type
            0x11,                   // section size: 17
            0x02,                   // 2 top-level entries
            // Entry 0: rec group
            0x4E,                   // rec
            0x02,                   // 2 types in group
            // struct type: 2 fields
            0x5F,                   // struct
            0x02,                   // 2 fields
            0x7F, 0x01,            // field 0: i32, mutable
            0x7C, 0x00,            // field 1: f64, const
            // func type: (i32) -> (i32)
            0x60,                   // func
            0x01, 0x7F,            // 1 param: i32
            0x01, 0x7F,            // 1 result: i32
            // Entry 1: array type (i32 mutable)
            0x5E,                   // array
            0x7F, 0x01,            // i32, mutable
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection)));

        Assert.Equal(3, module.Types.Count);
        // Type[0] = struct placeholder (empty params/results)
        Assert.Empty(module.Types[0].Parameters);
        Assert.Empty(module.Types[0].Results);
        // Type[1] = func type (i32) -> (i32)
        Assert.Equal([ValType.I32], module.Types[1].Parameters);
        Assert.Equal([ValType.I32], module.Types[1].Results);
        // Type[2] = array placeholder
        Assert.Empty(module.Types[2].Parameters);
    }

    [Fact]
    public void TypeSection_ParsesSubType()
    {
        // Type section: sub final wrapping a func type
        byte[] typeSection = [
            0x01,                   // section id: type
            0x08,                   // section size
            0x01,                   // 1 type entry
            // sub final with 0 supertypes, containing func type
            0x4F,                   // sub final
            0x00,                   // 0 supertypes
            0x60,                   // func
            0x01, 0x7F,            // 1 param: i32
            0x01, 0x7F,            // 1 result: i32
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection)));

        Assert.Single(module.Types);
        Assert.Equal([ValType.I32], module.Types[0].Parameters);
        Assert.Equal([ValType.I32], module.Types[0].Results);
    }

    [Fact]
    public void TypeSection_ParsesGcRefTypes_InFuncSignature()
    {
        // Type section: func type with (ref null 0) param and externref result
        byte[] typeSection = [
            0x01,                   // section id: type
            0x0A,                   // section size: 10
            0x02,                   // 2 type entries
            // Type 0: func () -> ()
            0x60, 0x00, 0x00,
            // Type 1: func (ref null 0) -> (externref)
            0x60,
            0x01, 0x63, 0x00,      // 1 param: ref null type[0] (encoded as 0x63 + s33)
            0x01, 0x6F,            // 1 result: externref
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection)));

        Assert.Equal(2, module.Types.Count);
        // Type[1] should have funcref (mapped from ref null $type) and externref
        Assert.Equal(ValType.FuncRef, module.Types[1].Parameters[0]);
        Assert.Equal(ValType.ExternRef, module.Types[1].Results[0]);
    }

    [Fact]
    public void TypeSection_ParsesPackedFieldTypes()
    {
        // Type section: struct with i8 and i16 packed fields
        byte[] typeSection = [
            0x01,                   // section id: type
            0x07,                   // section size: 7
            0x01,                   // 1 type entry
            // struct type: 2 packed fields
            0x5F,                   // struct
            0x02,                   // 2 fields
            0x78, 0x01,            // field 0: i8, mutable
            0x77, 0x00,            // field 1: i16, const
        ];

        var module = ModuleReader.ReadModule(new MemoryStream(BuildModule(typeSection)));

        Assert.Single(module.Types);
        // Struct stored as placeholder
        Assert.Empty(module.Types[0].Parameters);
    }

    private sealed class NonSeekableReadStream(byte[] bytes) : Stream
    {
        private readonly MemoryStream _inner = new(bytes, writable: false);

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override void Flush() => _inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => _inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    private static void AssertEquivalent(WasmModule expected, WasmModule actual)
    {
        Assert.Equal(expected.Types.Count, actual.Types.Count);
        for (int i = 0; i < expected.Types.Count; i++)
        {
            Assert.Equal(expected.Types[i].Parameters, actual.Types[i].Parameters);
            Assert.Equal(expected.Types[i].Results, actual.Types[i].Results);
        }

        Assert.Equal(expected.Imports.Count, actual.Imports.Count);
        Assert.Equal(expected.Functions.Count, actual.Functions.Count);
        for (int i = 0; i < expected.Functions.Count; i++)
        {
            Assert.Equal(expected.Functions[i].TypeIndex, actual.Functions[i].TypeIndex);
            Assert.Equal(expected.Functions[i].Locals, actual.Functions[i].Locals);
            Assert.Equal(expected.Functions[i].Body, actual.Functions[i].Body);
        }

        Assert.Equal(expected.Exports.Count, actual.Exports.Count);
        for (int i = 0; i < expected.Exports.Count; i++)
        {
            var expectedExport = expected.Exports.GetAt(i).Value;
            var actualExport = actual.Exports.GetAt(i).Value;
            Assert.Equal(expectedExport.Name, actualExport.Name);
            Assert.Equal(expectedExport.Descriptor.GetType(), actualExport.Descriptor.GetType());
        }

        Assert.Equal(expected.CustomSections.Count, actual.CustomSections.Count);
        for (int i = 0; i < expected.CustomSections.Count; i++)
        {
            Assert.Equal(expected.CustomSections[i].Name, actual.CustomSections[i].Name);
            Assert.Equal(expected.CustomSections[i].Content, actual.CustomSections[i].Content);
        }

        Assert.Equal(expected.LinkingSection?.Version, actual.LinkingSection?.Version);
        Assert.Equal(expected.ProducersSection is null, actual.ProducersSection is null);
        if (expected.ProducersSection is not null && actual.ProducersSection is not null)
        {
            Assert.Equal(expected.ProducersSection.Fields.Count, actual.ProducersSection.Fields.Count);
            foreach (var (fieldName, expectedValues) in expected.ProducersSection.Fields)
            {
                var actualValues = Assert.Contains(fieldName, actual.ProducersSection.Fields);
                Assert.Equal(expectedValues, actualValues);
            }
        }

        List<(char Prefix, string Name)> expectedFeatures = expected.TargetFeaturesSection?.Features ?? [];
        List<(char Prefix, string Name)> actualFeatures = actual.TargetFeaturesSection?.Features ?? [];
        Assert.Equal(expectedFeatures, actualFeatures);
        Assert.Equal(expected.RelocationSections.Count, actual.RelocationSections.Count);
    }
}
