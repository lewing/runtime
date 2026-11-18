// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Binary;

public static partial class ModuleReader
{
    /// <summary>
    /// Reads only what a linker needs to decide whether to extract a relocatable object from an archive: its imports,
    /// how many memories it defines, its <c>linking</c> section, and how many <c>reloc.*</c> sections it has (one
    /// unnamed, empty <see cref="RelocationSection"/> each). Every other section is skipped unread, which is what makes a lazily linked archive cheap: most of its
    /// members are never extracted, and an extracted one is parsed in full with <see cref="ParseCoreModule(ReadOnlyMemory{byte}, ModuleReaderOptions?)"/>.
    /// </summary>
    internal static WasmModule ReadLinkingView(ReadOnlyMemory<byte> bytes)
    {
        ReadOnlySpan<byte> span = bytes.Span;
        if (span.Length < 8 || !span[..4].SequenceEqual(Magic))
            throw new WasmFormatException("Invalid WebAssembly magic number");
        if (!span[4..8].SequenceEqual(Version))
            throw new WasmFormatException("Unsupported WebAssembly version");

        var imports = new List<Import>();
        var memories = new List<MemoryType>();
        var relocationSections = new List<RelocationSection>();
        LinkingSection? linking = null;
        int position = 8;
        while (position < span.Length)
        {
            byte id = span[position++];
            uint size = WasmLeb128.ReadU32(span, ref position);
            if ((ulong)position + size > (ulong)span.Length)
                throw new WasmFormatException($"Section {id} extends past the end of the module.");
            ReadOnlyMemory<byte> content = bytes.Slice(position, (int)size);
            position += (int)size;
            switch ((WasmSectionId)id)
            {
                case WasmSectionId.Import:
                {
                    var reader = new WasmSpanReader(content.Span, position - size);
                    ReadImportSection(ref reader, imports);
                    break;
                }
                case WasmSectionId.Memory:
                {
                    int at = 0;
                    uint count = WasmLeb128.ReadU32(content.Span, ref at);
                    for (uint i = 0; i < count; i++)
                        memories.Add(new MemoryType(new Limits(0)));
                    break;
                }

                case WasmSectionId.Custom:
                {
                    int at = 0;
                    ReadOnlySpan<byte> payload = content.Span;
                    uint nameLength = WasmLeb128.ReadU32(payload, ref at);
                    if ((ulong)at + nameLength > (ulong)payload.Length)
                        throw new WasmFormatException("A custom section name extends past its section.");
                    ReadOnlySpan<byte> name = payload.Slice(at, (int)nameLength);
                    at += (int)nameLength;
                    if (name.SequenceEqual("linking"u8))
                        linking = ParseLinkingSection(payload[at..], position - size + at);
                    else if (name.StartsWith("reloc."u8))
                        relocationSections.Add(new RelocationSection());
                    break;
                }
            }
        }

        return new WasmModule
        {
            Imports = imports,
            Memories = memories,
            LinkingSection = linking,
            RelocationSections = relocationSections,
        };
    }
}
