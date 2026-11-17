// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;

public sealed class RelocationEngineTests
{
    [Fact]
    public void WritePaddedLeb128U32_WritesExactFiveByteEncoding()
    {
        byte[] buffer = new byte[5];

        RelocationEngine.WritePaddedLeb128U32(buffer, 0, 624485);

        Assert.Equal(new byte[] { 0xE5, 0x8E, 0xA6, 0x80, 0x00 }, buffer);
    }

    [Fact]
    public void WritePaddedLeb128S32_WritesExactFiveByteEncoding()
    {
        byte[] buffer = new byte[5];

        RelocationEngine.WritePaddedLeb128S32(buffer, 0, -1);

        Assert.Equal(new byte[] { 0xFF, 0xFF, 0xFF, 0xFF, 0x7F }, buffer);
    }

    [Fact]
    public void ApplyRelocations_PatchesSupportedRelocationKindsInPlace()
    {
        byte[] sectionBytes = new byte[60];
        var relocations = new List<Relocation>
        {
            new() { Type = RelocationType.R_WASM_FUNCTION_INDEX_LEB, Offset = 0, Index = 0 },
            new() { Type = RelocationType.R_WASM_TABLE_INDEX_SLEB, Offset = 5, Index = 1 },
            new() { Type = RelocationType.R_WASM_TABLE_INDEX_I32, Offset = 10, Index = 1 },
            new() { Type = RelocationType.R_WASM_MEMORY_ADDR_LEB, Offset = 14, Index = 2, Addend = 4 },
            new() { Type = RelocationType.R_WASM_MEMORY_ADDR_SLEB, Offset = 19, Index = 2, Addend = -8 },
            new() { Type = RelocationType.R_WASM_MEMORY_ADDR_I32, Offset = 24, Index = 2, Addend = 12 },
            new() { Type = RelocationType.R_WASM_TYPE_INDEX_LEB, Offset = 28, Index = 3 },
            new() { Type = RelocationType.R_WASM_GLOBAL_INDEX_LEB, Offset = 33, Index = 3 },
            new() { Type = RelocationType.R_WASM_GLOBAL_INDEX_I32, Offset = 38, Index = 3 },
            new() { Type = RelocationType.R_WASM_FUNCTION_OFFSET_I32, Offset = 42, Index = 0, Addend = 6 },
            new() { Type = RelocationType.R_WASM_SECTION_OFFSET_I32, Offset = 46, Index = 4, Addend = 8 },
            new() { Type = RelocationType.R_WASM_TAG_INDEX_LEB, Offset = 50, Index = 5 },
            new() { Type = RelocationType.R_WASM_FUNCTION_INDEX_I32, Offset = 55, Index = 0 },
        };

        var indexMap = new IndexMap();
        indexMap.Functions[2] = 9;
        indexMap.Tables[1] = 42;
        indexMap.Types[3] = 7;
        indexMap.Globals[4] = 11;
        indexMap.Tags[6] = 13;
        indexMap.FunctionOffsets[2] = 0x1234;
        indexMap.SectionOffsets[11] = 0x2345;

        var symbols = new RelocSymbolTable(
        [
            new WasmSymbol { Type = SymbolType.Function, Name = "func", Index = 2 },
            new WasmSymbol { Type = SymbolType.Table, Name = "table-slot", Index = 1 },
            new WasmSymbol { Type = SymbolType.Data, Name = "data", SegmentIndex = 7, Offset = 20, Size = 4 },
            new WasmSymbol { Type = SymbolType.Global, Name = "global", Index = 4 },
            new WasmSymbol { Type = SymbolType.Section, Name = ".debug_info", SectionIndex = 11 },
            new WasmSymbol { Type = SymbolType.Tag, Name = "tag", Index = 6 },
        ]);

        RelocationEngine.ApplyRelocations(
            sectionBytes,
            relocations,
            indexMap,
            symbols,
            new Dictionary<uint, uint> { [7] = 1000 });

        Assert.Equal(9u, ReadU32Leb(sectionBytes, 0, out int functionBytes));
        Assert.Equal(5, functionBytes);

        Assert.Equal(42, ReadS32Leb(sectionBytes, 5, out int tableSlebBytes));
        Assert.Equal(5, tableSlebBytes);
        Assert.Equal(42u, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(10, 4)));

        Assert.Equal(1024u, ReadU32Leb(sectionBytes, 14, out int memoryLebBytes));
        Assert.Equal(5, memoryLebBytes);
        Assert.Equal(1012, ReadS32Leb(sectionBytes, 19, out int memorySlebBytes));
        Assert.Equal(5, memorySlebBytes);
        Assert.Equal(1032u, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(24, 4)));

        Assert.Equal(7u, ReadU32Leb(sectionBytes, 28, out _));
        Assert.Equal(11u, ReadU32Leb(sectionBytes, 33, out _));
        Assert.Equal(11u, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(38, 4)));

        Assert.Equal(0x123Au, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(42, 4)));
        Assert.Equal(0x234Du, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(46, 4)));
        Assert.Equal(13u, ReadU32Leb(sectionBytes, 50, out _));
        Assert.Equal(9u, BinaryPrimitives.ReadUInt32LittleEndian(sectionBytes.AsSpan(55, 4)));
    }

    private static uint ReadU32Leb(byte[] buffer, int offset, out int bytesRead)
    {
        uint result = 0;
        int shift = 0;
        int index = offset;

        while (true)
        {
            byte b = buffer[index++];
            result |= (uint)(b & 0x7F) << shift;

            if ((b & 0x80) == 0)
            {
                bytesRead = index - offset;
                return result;
            }

            shift += 7;
        }
    }

    private static int ReadS32Leb(byte[] buffer, int offset, out int bytesRead)
    {
        int result = 0;
        int shift = 0;
        int index = offset;
        byte b;

        do
        {
            b = buffer[index++];
            result |= (b & 0x7F) << shift;
            shift += 7;
        }
        while ((b & 0x80) != 0);

        if (shift < 32 && (b & 0x40) != 0)
        {
            result |= -1 << shift;
        }

        bytesRead = index - offset;
        return result;
    }
}
