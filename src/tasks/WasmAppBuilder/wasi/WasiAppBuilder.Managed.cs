// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.WebAssembly.Build.Tasks;

public partial class WasiAppBuilder
{
    private const int PointerSize = 4; // wasm32

    // Writes the runtimeconfig.cpp declarations as a relocatable wasm32 object, so the relink needs no C++ compiler:
    //   const char* const g_runtimeconfig_keys[] = { "key", ..., nullptr };
    //   const char* const g_runtimeconfig_values[] = { "value", ..., nullptr };
    //   const size_t g_runtimeconfig_property_count = N;
    // The strings share one segment, which the pointer arrays address through a local symbol and an addend.
    private static void WriteRuntimeConfigPropertiesObject(string path, KeyValuePair<string, string>[] properties)
    {
        var strings = new MemoryStream();
        var keyOffsets = new int[properties.Length];
        var valueOffsets = new int[properties.Length];
        for (int i = 0; i < properties.Length; i++)
        {
            keyOffsets[i] = AppendString(strings, properties[i].Key);
            valueOffsets[i] = AppendString(strings, properties[i].Value);
        }

        byte[] count = new byte[PointerSize];
        BinaryPrimitives.WriteUInt32LittleEndian(count, (uint)properties.Length);

        Segment[] segments =
        [
            new(".rodata..L.str", 0, strings.ToArray(), [], null),
            new(".rodata.g_runtimeconfig_keys", 2, new byte[(properties.Length + 1) * PointerSize], keyOffsets, "g_runtimeconfig_keys"),
            new(".rodata.g_runtimeconfig_values", 2, new byte[(properties.Length + 1) * PointerSize], valueOffsets, "g_runtimeconfig_values"),
            new(".rodata.g_runtimeconfig_property_count", 2, count, [], "g_runtimeconfig_property_count"),
        ];

        // Lay the segments out one after another, as clang does; the linker places them, so this only has to fit.
        var addresses = new int[segments.Length];
        int end = 0;
        for (int i = 0; i < segments.Length; i++)
        {
            int alignment = 1 << segments[i].AlignmentLog2;
            end = (end + alignment - 1) & ~(alignment - 1);
            addresses[i] = end;
            end += segments[i].Content.Length;
        }

        // Data section content, and the section-relative offsets of the pointers in it.
        const int StringsSymbol = 0;
        var data = new MemoryStream();
        var relocations = new List<(uint Offset, long Addend)>();
        Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        Span<byte> offsetExpression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        WriteULeb128(data, (uint)segments.Length);
        for (int i = 0; i < segments.Length; i++)
        {
            Segment segment = segments[i];
            int expressionLength = DataSegmentEncoder.EncodeI32ConstExpression(offsetExpression, addresses[i]);
            data.Write(header[..DataSegmentEncoder.EncodeActiveHeader(header, offsetExpression[..expressionLength], segment.Content.Length)]);
            long contentStart = data.Position;
            for (int p = 0; p < segment.StringOffsets.Length; p++)
                relocations.Add((checked((uint)(contentStart + p * PointerSize)), segment.StringOffsets[p]));
            data.Write(segment.Content);
        }

        var symbols = new SymbolTableEncoder();
        symbols.AddData(SymbolFlags.BindingLocal, ".L.str"u8, 0, 0, (ulong)segments[0].Content.Length);
        for (int i = 1; i < segments.Length; i++)
            symbols.AddData(SymbolFlags.VisibilityHidden, Encoding.UTF8.GetBytes(segments[i].Symbol!), (uint)i, 0, (ulong)segments[i].Content.Length);

        var segmentInfo = new SegmentInfoEncoder();
        foreach (Segment segment in segments)
            segmentInfo.Add(Encoding.UTF8.GetBytes(segment.Name), (uint)segment.AlignmentLog2, SegmentFlags.None);

        var linking = new LinkingSectionEncoder();
        linking.WriteSymbolTable(symbols);
        linking.WriteSegmentInfo(segmentInfo);

        var imports = new ArrayBufferWriter<byte>();
        var importEntries = new WasmModuleEntryWriter(imports);
        importEntries.Count(1);
        importEntries.MemoryImport("env"u8, "__linear_memory"u8, (ulong)Math.Max(1, (end + 0xFFFF) >> 16));

        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
            Directory.CreateDirectory(directory);

        using FileStream output = File.Create(path);
        var module = new WasmModuleWriter(output);
        module.WriteSection(WasmSectionId.Import, imports.WrittenSpan);
        int dataSectionIndex = module.WriteSection(WasmSectionId.Data, data.ToArray());

        var reloc = new RelocationSectionEncoder((uint)dataSectionIndex);
        foreach ((uint offset, long addend) in relocations)
            reloc.Add(RelocationType.R_WASM_MEMORY_ADDR_I32, offset, StringsSymbol, addend);

        module.WriteCustomSection("linking"u8, linking);
        module.WriteCustomSection("reloc.DATA"u8, reloc);
    }

    private static int AppendString(MemoryStream strings, string value)
    {
        int offset = checked((int)strings.Position);
        strings.Write(Encoding.UTF8.GetBytes(value));
        strings.WriteByte(0);
        return offset;
    }

    private static void WriteULeb128(Stream stream, uint value)
    {
        Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength32];
        stream.Write(buffer[..WasmLeb128.WriteULeb128(buffer, value)]);
    }

    // A data segment; StringOffsets are the string-segment offsets its leading pointers address, and Symbol the global
    // that names the whole segment.
    private sealed record Segment(string Name, int AlignmentLog2, byte[] Content, int[] StringOffsets, string? Symbol);
}
#endif
