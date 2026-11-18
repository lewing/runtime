// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// <see cref="WasmModuleEntryWriter"/> (#597): the entries of the type, import, function, table, memory, global,
/// export, element and tag sections, as dotnet/runtime's ObjectWriter encodes them in <c>WasmNative.cs</c>,
/// <c>WasmTypes.cs</c> and <c>Wasm/WasmSection.cs</c>. Expected bytes are written out from the core spec (§5.3, §5.5)
/// and those encoders; <see cref="ObjectWriterReferenceTests"/> rebuilds real outputs through the writer.
/// </summary>
public sealed class WasmModuleEntryWriterTests
{
    private static (WasmModuleEntryWriter Writer, ArrayBufferWriter<byte> Output) NewWriter()
    {
        var output = new ArrayBufferWriter<byte>();
        return (new WasmModuleEntryWriter(output), output);
    }

    private static string Name(string text) =>
        Convert.ToHexString([(byte)text.Length]) + Convert.ToHexString(System.Text.Encoding.UTF8.GetBytes(text));

    [Fact]
    public void FunctionTypesMatchRuntimeEncoding()
    {
        var (w, output) = NewWriter();
        w.FuncType([ValType.I32, ValType.I32], [ValType.I32]);
        w.FuncType([], []);
        w.FuncType([ValType.I64, ValType.F32, ValType.F64, ValType.V128, ValType.FuncRef], [ValType.ExternRef, ValType.ExnRef]);
        Assert.Equal(Convert.FromHexString("6002" + "7f7f" + "01" + "7f" + "600000" + "6005" + "7e7d7c7b70" + "02" + "6f69"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void ImportsMatchRuntimeEncoding()
    {
        var (w, output) = NewWriter();
        Assert.Equal(7, w.FunctionImport("env"u8, "f"u8, 3));
        w.GlobalImport("env"u8, "__stack_pointer"u8, ValType.I32, Mutability.Var);
        // WasmTableImportType: funcref, min-only limits, min 1.
        w.TableImport("env"u8, "__indirect_function_table"u8, ValType.FuncRef, 1);
        // WasmMemoryImportType(HasMin, 0).
        w.MemoryImport("env"u8, "memory"u8, 0);
        // WasmTagImportType: exception attribute 0, then the type index.
        long tag = w.TagImport("env"u8, "t"u8, 2);
        Assert.Equal(Convert.FromHexString(
            Name("env") + Name("f") + "00" + "03" +
            Name("env") + Name("__stack_pointer") + "03" + "7f01" +
            Name("env") + Name("__indirect_function_table") + "01" + "70" + "0001" +
            Name("env") + Name("memory") + "02" + "0000" +
            Name("env") + Name("t") + "04" + "00" + "02"), output.WrittenSpan.ToArray());
        Assert.Equal(output.WrittenCount - 1, tag);
        Assert.Equal(output.WrittenCount, w.Position);
    }

    [Fact]
    public void PositionsAfterTheCountAreOffsetsIntoTheSectionContent()
    {
        var (w, output) = NewWriter();
        w.Count(200);
        Assert.Equal(2 + 7, w.FunctionImport("env"u8, "f"u8, 3, WasmLeb128.PaddedLength32));
        Assert.Equal(Convert.FromHexString("c801" + Name("env") + Name("f") + "00" + "8380808000"), output.WrittenSpan.ToArray());
        Assert.Equal(output.WrittenCount, w.Position);
    }

    [Fact]
    public void RelocatedIndicesArePaddedAndTheirPositionsReturned()
    {
        var (w, output) = NewWriter();
        Assert.Equal(7, w.FunctionImport("env"u8, "f"u8, 3, WasmLeb128.PaddedLength32));
        Assert.Equal(20, w.TagImport("env"u8, "t"u8, 0, WasmLeb128.PaddedLength32));
        Assert.Equal(25, w.Function(7, WasmLeb128.PaddedLength32));
        Assert.Equal(33, w.Export("g"u8, WasmExternalKind.Function, 9, WasmLeb128.PaddedLength32));
        Assert.Equal(Convert.FromHexString(
            Name("env") + Name("f") + "00" + "8380808000" +
            Name("env") + Name("t") + "04" + "00" + "8080808000" +
            "8780808000" +
            Name("g") + "00" + "8980808000"), output.WrittenSpan.ToArray());
    }

    [Theory]
    [InlineData(null, false, false, "00" + "05")]
    [InlineData(10UL, false, false, "01" + "05" + "0a")]
    [InlineData(10UL, false, true, "03" + "05" + "0a")]
    [InlineData(null, true, false, "04" + "05")]
    [InlineData(70000UL, true, true, "07" + "05" + "f0a204")]
    public void LimitsFollowTheMemory64AndThreadsFlags(ulong? max, bool is64, bool shared, string expectedLimits)
    {
        var (w, output) = NewWriter();
        w.Memory(5, max, is64, shared);
        Assert.Equal(Convert.FromHexString(expectedLimits), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void ImportLimitsKeepTheTable64AndSharedFlags()
    {
        var (w, output) = NewWriter();
        w.TableImport("env"u8, "t"u8, ValType.FuncRef, 1, 300, is64: true);
        w.MemoryImport("env"u8, "m"u8, 1, 16, shared: true);
        Assert.Equal(Convert.FromHexString(
            Name("env") + Name("t") + "01" + "70" + "05" + "01" + "ac02" +
            Name("env") + Name("m") + "02" + "03" + "01" + "10"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void SharedMemoryWithoutAMaximumIsRejectedBeforeWriting()
    {
        var (w, output) = NewWriter();
        Assert.Throws<ArgumentException>(() => w.Memory(1, null, shared: true));
        Assert.Throws<ArgumentException>(() => w.MemoryImport("env"u8, "m"u8, 1, null, shared: true));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void ErrorsNameTheOffendingArgument()
    {
        var (w, _) = NewWriter();
        Assert.Equal("max", Assert.Throws<ArgumentException>(() => w.Memory(1, 1UL << 32)).ParamName);
        Assert.Equal("min", Assert.Throws<ArgumentException>(() => w.Memory(1UL << 32)).ParamName);
        ArgumentException init = Assert.Throws<ArgumentException>(() => w.Global(ValType.I32, Mutability.Const, [0x41, 0x00]));
        Assert.Equal("initExpression", init.ParamName);
        Assert.StartsWith("An initializer expression", init.Message, StringComparison.Ordinal);
        Assert.Equal("offsetExpression", Assert.Throws<ArgumentException>(() => w.ActiveElementSegment([], [1])).ParamName);
    }

    [Fact]
    public void TablesGlobalsExportsElementsAndTags()
    {
        var (w, output) = NewWriter();
        w.Table(ValType.FuncRef, 3, 3);
        w.Table(ValType.ExternRef, 0, is64: true);
        w.Global(ValType.I32, Mutability.Const, [0x41, 0x80, 0x80, 0x04, 0x0B]);
        w.Global(ValType.I64, Mutability.Var, [0x42, 0x00, 0x0B]);
        w.Export("table"u8, WasmExternalKind.Table, 0);
        w.Export("memory"u8, WasmExternalKind.Memory, 0);
        w.Export("g"u8, WasmExternalKind.Global, 300);
        w.Export("e"u8, WasmExternalKind.Tag, 1);
        w.ActiveElementSegment([0x23, 0x02, 0x0B], [1, 2, 300]);
        w.Tag(4);
        Assert.Equal(Convert.FromHexString(
            "70" + "010303" + "6f" + "0400" +
            "7f00" + "418080040b" + "7e01" + "42000b" +
            Name("table") + "0100" + Name("memory") + "0200" + Name("g") + "03ac02" + Name("e") + "0401" +
            "00" + "23020b" + "03" + "01" + "02" + "ac02" +
            "0004"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void EntriesTakeAnyValueType()
    {
        WasmValType refNull0 = WasmValType.Ref(WasmHeapType.Concrete(0), nullable: true);
        WasmValType ref0 = WasmValType.Ref(WasmHeapType.Concrete(0), nullable: false);
        var (w, output) = NewWriter();
        w.FuncType([refNull0, ValType.I32], [WasmValType.Ref(WasmHeapType.Abstract(HeapTypeKind.Any), nullable: false)]);
        w.FuncType([ValType.I32, ValType.I64], []);
        w.GlobalImport("m"u8, "g"u8, ref0, Mutability.Const);
        w.TableImport("m"u8, "t"u8, ref0, 1);
        w.Table(refNull0, 2);
        // ref.func 1 as the initializer of a (ref $0) global.
        w.Global(ref0, Mutability.Var, [0xD2, 0x01, 0x0B]);
        Assert.Equal(Convert.FromHexString(
            "60" + "02" + "6300" + "7f" + "01" + "646e" +
            "60" + "02" + "7f7e" + "00" +
            Name("m") + Name("g") + "03" + "6400" + "00" +
            Name("m") + Name("t") + "01" + "6400" + "0001" +
            "6300" + "0002" +
            "6400" + "01" + "d2010b"), output.WrittenSpan.ToArray());
    }

    [Fact]
    public void GcTypesWrittenThroughTheEntryAndCodeWritersValidate()
    {
        // (type $s (struct)) (type (func (param (ref null $s)) (result (ref null $s))))
        // (func (type 1) (local (ref null $s)) local.get 0 local.tee 1)
        WasmValType refNullS = WasmValType.Ref(WasmHeapType.Concrete(0), nullable: true);
        var types = new ArrayBufferWriter<byte>();
        var typeEntries = new WasmModuleEntryWriter(types);
        typeEntries.Count(2);
        types.Write<byte>([0x5F, 0x00]);
        typeEntries.FuncType([refNullS], [refNullS]);
        var body = new ArrayBufferWriter<byte>();
        var code = new WasmCodeWriter(body);
        code.Locals([refNullS]);
        code.LocalGet(0);
        code.LocalTee(1);
        code.End();

        using var stream = new MemoryStream();
        var module = new WasmModuleWriter(stream);
        module.WriteSection(WasmSectionId.Type, types.WrittenSpan);
        module.WriteSection(WasmSectionId.Function, [0x01, 0x01]);
        module.WriteSection(WasmSectionId.Code, [0x01, (byte)body.WrittenCount, .. body.WrittenSpan]);
        Assert.Equal(Convert.FromHexString("01" + "016300" + "2000" + "2201" + "0b"), body.WrittenSpan.ToArray());
        ModuleReader.ReadModuleAndValidate(new MemoryStream(stream.ToArray()), new ModuleReaderOptions { Features = WasmFeatures.All });
    }

    [Fact]
    public void EntriesRejectWhatTheyCannotEncode()
    {
        var (w, output) = NewWriter();
        Assert.Throws<ArgumentException>(() => w.FuncType([(ValType)0x63], []));
        Assert.Throws<ArgumentException>(() => w.Global(ValType.I32, Mutability.Const, [0x41, 0x00]));
        Assert.Throws<ArgumentException>(() => w.Table(ValType.I32, 1));
        // A table of non-nullable references needs an initializer, which the plain table entry cannot carry.
        Assert.Throws<ArgumentException>("elementType", () => w.Table(WasmValType.Ref(WasmHeapType.Func, nullable: false), 1));
        Assert.Throws<ArgumentException>("elementType", () => w.TableImport("m"u8, "t"u8, default, 1));
        Assert.Throws<ArgumentException>("elementType", () => w.TableImport("m"u8, "t"u8, ValType.I32, 1));
        Assert.Throws<ArgumentException>("valueType", () => w.Global(default, Mutability.Const, [0x41, 0x00, 0x0B]));
        Assert.Throws<ArgumentException>("valueType", () => w.GlobalImport("m"u8, "g"u8, default, Mutability.Const));
        Assert.Throws<ArgumentException>("parameters", () => w.FuncType([WasmValType.I32, default], []));
        Assert.Throws<ArgumentException>("results", () => w.FuncType([], [default(WasmValType)]));
        Assert.Throws<ArgumentException>(() => w.Memory(1UL << 32));
        Assert.Throws<ArgumentException>(() => w.Memory(2, 1));
        Assert.Throws<ArgumentException>(() => w.FunctionImport("env"u8, "f"u8, 1u << 20, 2));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Export("x"u8, (WasmExternalKind)5, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.GlobalImport("env"u8, "g"u8, ValType.I32, (Mutability)2));
        Assert.Throws<ArgumentOutOfRangeException>(() => w.Global(ValType.I32, (Mutability)2, [0x41, 0x00, 0x0B]));
        Assert.Throws<ArgumentException>(() => w.ActiveElementSegment([], [1]));
        Assert.Equal(0, output.WrittenCount);
    }

    [Fact]
    public void ExternalKindsMatchTheReader()
    {
        // Every kind is read back as the descriptor of that kind.
        var (w, output) = NewWriter();
        w.Count(5);
        w.FunctionImport("m"u8, "f"u8, 0);
        w.TableImport("m"u8, "t"u8, ValType.FuncRef, 1, 4);
        w.MemoryImport("m"u8, "mem"u8, 1, 2);
        w.GlobalImport("m"u8, "g"u8, ValType.F32, Mutability.Const);
        w.TagImport("m"u8, "e"u8, 0);

        using var stream = new MemoryStream();
        var module = new WasmModuleWriter(stream);
        var types = new ArrayBufferWriter<byte>();
        new WasmModuleEntryWriter(types).FuncType([], []);
        module.WriteSection(WasmSectionId.Type, [0x01, .. types.WrittenSpan]);
        module.WriteSection(WasmSectionId.Import, output.WrittenSpan);
        WasmModule parsed = ModuleReader.ReadModuleAndValidate(new MemoryStream(stream.ToArray()), new ModuleReaderOptions { Features = WasmFeatures.All });

        Assert.Collection(parsed.Imports,
            i => Assert.IsType<ImportDescriptor.Func>(i.Descriptor),
            i => Assert.Equal((1UL, (ulong?)4UL), (((ImportDescriptor.Table)i.Descriptor).Type.Limits.Min, ((ImportDescriptor.Table)i.Descriptor).Type.Limits.Max)),
            i => Assert.Equal((1UL, (ulong?)2UL), (((ImportDescriptor.Memory)i.Descriptor).Type.Limits.Min, ((ImportDescriptor.Memory)i.Descriptor).Type.Limits.Max)),
            i => Assert.False(((ImportDescriptor.Global)i.Descriptor).Type.Mutability == Microsoft.NET.WebAssembly.Types.Mutability.Var),
            i => Assert.IsType<ImportDescriptor.Tag>(i.Descriptor));
    }
}
