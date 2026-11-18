// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;

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
                if (section.Id == (byte)WasmSectionId.Code)
                {
                    ulong count = ReadULeb(original, section.ContentStart, out int countWidth);
                    using var bodies = new MemoryStream(original, section.ContentStart + countWidth, section.ContentLength - countWidth, writable: false);
                    index = writer.WriteVectorSection(WasmSectionId.Code, checked((uint)count), bodies, width);
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
                // The streaming path: the header is written up front and the payload follows in pieces.
                index = writer.BeginCustomSection(name, payload.Length, width);
                writer.Write(payload[..(payload.Length / 2)]);
                writer.Write(payload[(payload.Length / 2)..]);
                writer.EndSection();
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
