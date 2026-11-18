// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// Rebuilds a module through the public <see cref="WasmModuleWriter"/> API: the linking and reloc.* sections are
/// re-encoded from their parsed models, padding sections are regenerated from the alignment, and every other
/// section's content is passed through unchanged with its original size-field width.
/// </summary>
internal static class ObjectReencoder
{
    internal readonly record struct RawSection(byte Id, int Start, int SizeWidth, int ContentStart, int ContentLength, string? Name, int PayloadStart)
    {
        public int End => ContentStart + ContentLength;
    }

    internal static List<RawSection> ReadSections(byte[] bytes)
    {
        var sections = new List<RawSection>();
        int at = 8;
        while (at < bytes.Length)
        {
            byte id = bytes[at];
            ulong size = ReadULeb(bytes, at + 1, out int width);
            int contentStart = at + 1 + width;
            string? name = null;
            int payloadStart = contentStart;
            if (id == 0)
            {
                ulong nameLength = ReadULeb(bytes, contentStart, out int nameWidth);
                name = Encoding.UTF8.GetString(bytes, contentStart + nameWidth, (int)nameLength);
                payloadStart = contentStart + nameWidth + (int)nameLength;
            }

            sections.Add(new RawSection(id, at, width, contentStart, checked((int)size), name, payloadStart));
            at = contentStart + (int)size;
        }

        return sections;
    }

    internal static byte[] Reencode(byte[] original, int paddingAlignment = 16)
    {
        List<RawSection> sections = ReadSections(original);
        var module = ModuleReader.ParseCoreModule(original);
        using var output = new MemoryStream();
        var writer = new WasmModuleWriter(output);
        for (int i = 0; i < sections.Count; i++)
        {
            RawSection section = sections[i];
            int width = section.SizeWidth == WasmLeb128.GetULeb128Size((ulong)section.ContentLength) ? 0 : section.SizeWidth;
            ReadOnlySpan<byte> content = original.AsSpan(section.ContentStart, section.ContentLength);
            ReadOnlySpan<byte> payload = original.AsSpan(section.PayloadStart, section.End - section.PayloadStart);
            byte[]? name = section.Name is null ? null : Encoding.UTF8.GetBytes(section.Name);
            int index;
            if (section.Id != 0)
            {
                if (section.Id == (byte)WasmSectionId.Data)
                {
                    index = writer.BeginSection(WasmSectionId.Data, section.ContentLength, width);
                    ReencodeDataSegments(writer, original, section);
                    writer.EndSection();
                }
                else if (section.Id == (byte)WasmSectionId.Code)
                {
                    ulong count = ReadULeb(original, section.ContentStart, out int countWidth);
                    using var bodies = new MemoryStream(original, section.ContentStart + countWidth, section.ContentLength - countWidth, writable: false);
                    index = writer.WriteVectorSection(WasmSectionId.Code, checked((uint)count), bodies, width);
                }
                else if (EntrySections.Contains((WasmSectionId)section.Id))
                {
                    var entries = new System.Buffers.ArrayBufferWriter<byte>(section.ContentLength);
                    ReencodeEntries((WasmSectionId)section.Id, original, section, entries);
                    index = writer.WriteSection((WasmSectionId)section.Id, entries.WrittenSpan, width);
                }
                else
                {
                    index = writer.WriteSection((WasmSectionId)section.Id, content, width);
                }
            }
            else if (section.Name == "linking")
            {
                // The first subsection's size field shows whether the producer pads them (LLVM) or not (runtime).
                ReadULeb(original, section.PayloadStart, out int versionWidth);
                ulong firstSize = ReadULeb(original, section.PayloadStart + versionWidth + 1, out int firstWidth);
                int subsectionWidth = firstWidth == WasmLeb128.GetULeb128Size(firstSize) ? 0 : firstWidth;
                index = writer.WriteCustomSection(name, LinkingSectionEncoder.From(module.LinkingSection!, subsectionWidth), width);
            }
            else if (section.Name!.StartsWith("reloc.", StringComparison.Ordinal))
            {
                RelocationSection model = module.RelocationSections.Single(r => r.Name == section.Name);
                index = writer.WriteCustomSection(name, RelocationSectionEncoder.From(model), width);
            }
            else if (section.Name.Length == 0 && !payload.ContainsAnyExcept((byte)0))
            {
                index = writer.WritePaddingSection(paddingAlignment);
            }
            else if (section.Name == "name")
            {
                index = ReencodeNameSection(writer, original, section, width);
            }
            else
            {
                index = writer.WriteCustomSection(name, payload, width);
            }

            Assert.Equal(i, index);
        }

        Assert.Equal(sections.Count, writer.NextSectionIndex);
        Assert.Equal(output.Length, writer.Position);
        return output.ToArray();
    }

    private static readonly HashSet<WasmSectionId> EntrySections =
    [
        WasmSectionId.Type, WasmSectionId.Import, WasmSectionId.Function, WasmSectionId.Table, WasmSectionId.Memory,
        WasmSectionId.Global, WasmSectionId.Export, WasmSectionId.Element, WasmSectionId.Tag,
    ];

    /// <summary>
    /// Rebuilds the entries of a type, import, function, table, memory, global, export, element or tag section through
    /// <see cref="WasmModuleEntryWriter"/>, keeping padded index widths; every entry shape a producer writes must be one
    /// the writer can reproduce.
    /// </summary>
    private static void ReencodeEntries(WasmSectionId id, byte[] original, RawSection section, System.Buffers.ArrayBufferWriter<byte> output)
    {
        var w = new WasmModuleEntryWriter(output);
        int at = section.ContentStart;
        uint count = Minimal(original, ref at, "entry count");
        w.Count(count);
        for (uint i = 0; i < count; i++)
        {
            switch (id)
            {
                case WasmSectionId.Type:
                {
                    Assert.True(original[at++] == 0x60, $"type {i} is not a function type");
                    ValType[] parameters = ValueTypes(original, ref at);
                    w.FuncType(parameters, ValueTypes(original, ref at));
                    break;
                }

                case WasmSectionId.Import:
                {
                    byte[] module = Name(original, ref at), name = Name(original, ref at);
                    var kind = (WasmExternalKind)original[at++];
                    switch (kind)
                    {
                        case WasmExternalKind.Function:
                        {
                            uint type = Index(original, ref at, out int typeWidth);
                            w.FunctionImport(module, name, type, typeWidth);
                            break;
                        }

                        case WasmExternalKind.Table:
                        {
                            var refType = (ValType)original[at++];
                            var (min, max, is64, shared) = Limits(original, ref at);
                            Assert.False(shared);
                            w.TableImport(module, name, refType, min, max, is64);
                            break;
                        }

                        case WasmExternalKind.Memory:
                        {
                            var (min, max, is64, shared) = Limits(original, ref at);
                            w.MemoryImport(module, name, min, max, is64, shared);
                            break;
                        }

                        case WasmExternalKind.Global:
                            w.GlobalImport(module, name, (ValType)original[at], (Mutability)original[at + 1]);
                            at += 2;
                            break;
                        case WasmExternalKind.Tag:
                        {
                            Assert.Equal(0, original[at++]);
                            uint type = Index(original, ref at, out int typeWidth);
                            w.TagImport(module, name, type, typeWidth);
                            break;
                        }

                        default:
                            Assert.Fail($"import {i}: kind {kind}");
                            break;
                    }

                    break;
                }

                case WasmSectionId.Function:
                {
                    uint type = Index(original, ref at, out int typeWidth);
                    w.Function(type, typeWidth);
                    break;
                }

                case WasmSectionId.Table:
                {
                    var refType = (ValType)original[at++];
                    var (min, max, is64, _) = Limits(original, ref at);
                    w.Table(refType, min, max, is64);
                    break;
                }

                case WasmSectionId.Memory:
                {
                    var (min, max, is64, shared) = Limits(original, ref at);
                    w.Memory(min, max, is64, shared);
                    break;
                }

                case WasmSectionId.Global:
                {
                    var type = (ValType)original[at];
                    var mutability = (Mutability)original[at + 1];
                    at += 2;
                    w.Global(type, mutability, ConstExpression(original, ref at));
                    break;
                }

                case WasmSectionId.Export:
                {
                    byte[] name = Name(original, ref at);
                    var kind = (WasmExternalKind)original[at++];
                    uint target = Index(original, ref at, out int targetWidth);
                    w.Export(name, kind, target, targetWidth);
                    break;
                }

                case WasmSectionId.Element:
                {
                    Assert.True(original[at++] == 0, $"element segment {i}: only active funcref segments (flag 0) are expected");
                    byte[] offset = ConstExpression(original, ref at);
                    uint functions = Minimal(original, ref at, "element count");
                    var indices = new uint[functions];
                    for (int f = 0; f < functions; f++)
                        indices[f] = Minimal(original, ref at, "function index");
                    w.ActiveElementSegment(offset, indices);
                    break;
                }

                case WasmSectionId.Tag:
                {
                    Assert.Equal(0, original[at++]);
                    uint type = Index(original, ref at, out int typeWidth);
                    w.Tag(type, typeWidth);
                    break;
                }
            }
        }

        Assert.Equal(section.End, at);
    }

    private static uint Minimal(byte[] bytes, ref int at, string what)
    {
        uint value = Index(bytes, ref at, out int width);
        Assert.True(width == 0, $"padded {what} at {at:#x}");
        return value;
    }

    // An index and its padded width (0 when minimal).
    private static uint Index(byte[] bytes, ref int at, out int width)
    {
        uint value = checked((uint)ReadULeb(bytes, at, out int length));
        at += length;
        width = length == WasmLeb128.GetULeb128Size(value) ? 0 : length;
        return value;
    }

    private static byte[] Name(byte[] bytes, ref int at)
    {
        int length = (int)Minimal(bytes, ref at, "name length");
        byte[] name = bytes[at..(at + length)];
        at += length;
        return name;
    }

    private static ValType[] ValueTypes(byte[] bytes, ref int at)
    {
        int count = (int)Minimal(bytes, ref at, "value type count");
        var types = new ValType[count];
        for (int i = 0; i < count; i++)
            types[i] = (ValType)bytes[at + i];
        at += count;
        return types;
    }

    private static (ulong Min, ulong? Max, bool Is64, bool Shared) Limits(byte[] bytes, ref int at)
    {
        byte flags = bytes[at++];
        Assert.True(flags <= 0x07, $"limits flags {flags:#x}");
        ulong min = ReadULeb(bytes, at, out int minWidth);
        Assert.Equal(WasmLeb128.GetULeb128Size(min), minWidth);
        at += minWidth;
        ulong? max = null;
        if ((flags & 1) != 0)
        {
            max = ReadULeb(bytes, at, out int maxWidth);
            Assert.Equal(WasmLeb128.GetULeb128Size(max.Value), maxWidth);
            at += maxWidth;
        }

        return (min, max, (flags & 4) != 0, (flags & 2) != 0);
    }

    // A constant expression up to and including its end (no nested blocks).
    private static byte[] ConstExpression(byte[] bytes, ref int at)
    {
        int start = at;
        var cursor = new InstructionCursor(bytes.AsSpan(at).ToArray(), WasmFeatures.All);
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            if (instruction.OpcodeId.Opcode == WasmOpcode.End && !instruction.OpcodeId.IsPrefixed)
            {
                at = start + cursor.Position;
                return bytes[start..at];
            }
        }

        Assert.Fail($"unterminated constant expression at {start:#x}");
        return [];
    }

    /// <summary>
    /// Rebuilds every data segment header through <see cref="DataSegmentEncoder"/>, keeping each LEB128's width,
    /// and copies the contents.
    /// </summary>
    private static void ReencodeDataSegments(WasmModuleWriter writer, byte[] original, RawSection section)
    {
        int at = section.ContentStart;
        ulong count = ReadULeb(original, at, out int countWidth);
        WriteULebAtWidth(writer, count, countWidth);
        at += countWidth;
        Span<byte> expression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
        Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
        for (ulong i = 0; i < count; i++)
        {
            ulong flags = ReadULeb(original, at, out int flagsWidth);
            Assert.True(flagsWidth == 1 && flags <= 2, $"segment {i}: flags {flags} ({flagsWidth} bytes)");
            at += flagsWidth;
            uint memory = 0;
            if (flags == 2)
            {
                memory = (uint)ReadULeb(original, at, out int memoryWidth);
                Assert.Equal(WasmLeb128.GetULeb128Size(memory), memoryWidth);
                at += memoryWidth;
            }

            int expressionLength = 0;
            if (flags != 1)
            {
                byte opcode = original[at];
                ulong immediate = ReadULeb(original, at + 1, out int immediateWidth);
                Assert.True(original[at + 1 + immediateWidth] == WasmOpcode.End, $"segment {i}: offset expression is not a single instruction");
                if (opcode == WasmOpcode.I32Const)
                {
                    int value = (int)SignExtend(immediate, immediateWidth);
                    expressionLength = DataSegmentEncoder.EncodeI32ConstExpression(expression, value,
                        immediateWidth == WasmLeb128.GetSLeb128Size(value) ? 0 : immediateWidth);
                }
                else if (opcode == WasmOpcode.I64Const)
                {
                    long value = SignExtend(immediate, immediateWidth);
                    expressionLength = DataSegmentEncoder.EncodeI64ConstExpression(expression, value,
                        immediateWidth == WasmLeb128.GetSLeb128Size(value) ? 0 : immediateWidth);
                }
                else
                {
                    Assert.Equal(WasmOpcode.GlobalGet, opcode);
                    expressionLength = DataSegmentEncoder.EncodeGlobalGetExpression(expression, (uint)immediate,
                        immediateWidth == WasmLeb128.GetULeb128Size(immediate) ? 0 : immediateWidth);
                }

                at += 1 + immediateWidth + 1;
            }

            ulong size = ReadULeb(original, at, out int sizeWidth);
            at += sizeWidth;
            int sizeFieldWidth = sizeWidth == WasmLeb128.GetULeb128Size(size) ? 0 : sizeWidth;
            int headerLength = flags == 1
                ? DataSegmentEncoder.EncodePassiveHeader(header, (long)size, sizeFieldWidth)
                : DataSegmentEncoder.EncodeActiveHeader(header, expression[..expressionLength], (long)size, sizeFieldWidth, memory);
            writer.Write(header[..headerLength]);
            writer.Write(original.AsSpan(at, (int)size));
            at += (int)size;
        }

        Assert.Equal(section.End, at);
    }

    /// <summary>Rebuilds the name section, streaming each index → name map through <see cref="NameMapWriter"/>.</summary>
    private static int ReencodeNameSection(WasmModuleWriter writer, byte[] original, RawSection section, int width)
    {
        var subsections = new List<(byte Id, int Start, int End, List<(uint Index, int NameStart, int NameLength)>? Map)>();
        int at = section.PayloadStart;
        while (at < section.End)
        {
            byte id = original[at];
            ulong size = ReadULeb(original, at + 1, out int sizeWidth);
            int start = at, end = at + 1 + sizeWidth + (int)size;
            List<(uint, int, int)>? map = null;
            if (id is (byte)NameSubsectionId.Function or (byte)NameSubsectionId.Global or (byte)NameSubsectionId.DataSegment)
            {
                map = [];
                int p = at + 1 + sizeWidth;
                ulong count = ReadULeb(original, p, out int countWidth);
                p += countWidth;
                for (ulong i = 0; i < count; i++)
                {
                    uint entry = (uint)ReadULeb(original, p, out int indexWidth);
                    p += indexWidth;
                    int nameLength = (int)ReadULeb(original, p, out int nameWidth);
                    p += nameWidth;
                    map.Add((entry, p, nameLength));
                    p += nameLength;
                }

                Assert.Equal(end, p);
            }

            subsections.Add((id, start, end, map));
            at = end;
        }

        long payload = subsections.Sum(s => s.Map is null
            ? s.End - s.Start
            : NameMapWriter.GetSubsectionSize(s.Map.Count, s.Map.Sum(e => NameMapWriter.GetEntrySize(e.Index, e.NameLength))));
        int index = writer.BeginCustomSection("name"u8, payload, width);
        foreach (var (id, start, end, map) in subsections)
        {
            if (map is null)
            {
                writer.Write(original.AsSpan(start, end - start));
                continue;
            }

            var names = new NameMapWriter(writer, (NameSubsectionId)id, map.Count, map.Sum(e => NameMapWriter.GetEntrySize(e.Index, e.NameLength)));
            foreach (var (entryIndex, nameStart, nameLength) in map)
                names.Write(entryIndex, original.AsSpan(nameStart, nameLength));
            names.Complete();
        }

        writer.EndSection();
        return index;
    }

    private static void WriteULebAtWidth(WasmModuleWriter writer, ulong value, int width)
    {
        Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength];
        if (width == WasmLeb128.GetULeb128Size(value))
        {
            writer.Write(buffer[..WasmLeb128.WriteULeb128(buffer, value)]);
        }
        else
        {
            WasmLeb128.WritePaddedULeb128(buffer[..width], value);
            writer.Write(buffer[..width]);
        }
    }

    private static long SignExtend(ulong value, int width)
    {
        int bits = 7 * width;
        return bits >= 64 ? (long)value : (long)(value << (64 - bits)) >> (64 - bits);
    }

    internal static ulong ReadULeb(byte[] bytes, int at, out int width)
    {
        ulong value = 0;
        int shift = 0;
        width = 0;
        while (true)
        {
            byte b = bytes[at + width++];
            value |= (ulong)(b & 0x7F) << shift;
            shift += 7;
            if (b < 0x80)
                return value;
        }
    }

    internal static void AssertBytesEqual(byte[] expected, byte[] actual, string label)
    {
        int length = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < length; i++)
        {
            if (expected[i] != actual[i])
                Assert.Fail($"{label}: first difference at offset {i:#x} (expected {expected[i]:x2}, actual {actual[i]:x2}); sizes {expected.Length} vs {actual.Length}");
        }

        Assert.True(expected.Length == actual.Length, $"{label}: sizes differ ({expected.Length} vs {actual.Length})");
    }
}
