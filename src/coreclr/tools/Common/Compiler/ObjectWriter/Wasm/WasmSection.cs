// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ILCompiler.ObjectWriter.WasmInstructions;
using Internal.Text;
using Internal.TypeSystem;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    /// <summary>
    /// Interface for section-like objects that can be emitted to a wasm module stream.
    /// </summary>
    /// <summary>Content written inside a section: a data segment, or a Webcil section within the payload segment.</summary>
    internal interface IWasmEmittable
    {
        int EmitToStream(Stream outputFileStream);
        int EncodeSize();
    }

    /// <summary>A module section, framed and written by the WebAssembly toolchain's <see cref="WasmModuleWriter"/>.</summary>
    internal interface IWasmSection
    {
        WasmSectionId Type { get; }

        /// <summary>Writes the section and returns its file section index.</summary>
        int Emit(WasmModuleWriter writer);
    }

    internal abstract class SectionDataEmitter
    {
        public SectionDataEmitter(Stream stream, Utf8String name, int sectionIndex)
        {
            ContentReadStream = stream;
            SectionName = name;
            SectionIndex = sectionIndex;
        }

        public int SectionIndex { get; }
        public Utf8String SectionName { get; }
        public Stream ContentReadStream { get; set; }
    }

    internal class WasmSection : SectionDataEmitter, IWasmSection
    {
        public WasmSectionId Type { get; }

        // The bytes written ahead of the content stream, such as a vector section's entry count.
        internal virtual int ContentPrefixSize => 0;

        protected virtual int EncodeContentPrefix(Span<byte> destination) => 0;

        public virtual int Emit(WasmModuleWriter writer)
        {
            Span<byte> contentPrefix = stackalloc byte[ContentPrefixSize];
            int encodedSize = EncodeContentPrefix(contentPrefix);
            Debug.Assert(encodedSize == contentPrefix.Length);
            ContentReadStream.Position = 0;
            return writer.WriteSection(Type, ContentReadStream, contentPrefix);
        }

        public WasmSection(WasmSectionId type, Stream stream, Utf8String name, int sectionIndex) : base(stream, name, sectionIndex)
        {
            Type = type;
        }
    }

    internal sealed class WasmCustomSection : WasmSection
    {
        public WasmCustomSection(Stream stream, Utf8String customSectionName, int sectionIndex)
            : base(WasmSectionId.Custom, stream, customSectionName, sectionIndex)
        {
            Debug.Assert(stream is not null);
            Debug.Assert(!customSectionName.IsNull);
        }

        public override int Emit(WasmModuleWriter writer)
        {
            ContentReadStream.Position = 0;
            return writer.WriteCustomSection(SectionName.AsSpan(), ContentReadStream);
        }
    }

    internal abstract class WasmVectorSection : WasmSection
    {
        public int EntryCount { get; protected set; }

        internal override int ContentPrefixSize => WasmLeb128.GetULeb128Size((ulong)EntryCount);

        protected override int EncodeContentPrefix(Span<byte> destination) =>
            WasmLeb128.WriteULeb128(destination, (ulong)EntryCount);

        protected void CompleteEntry()
        {
            EntryCount++;
        }

        protected WasmVectorSection(WasmSectionId type, Stream stream, Utf8String name, int sectionIndex)
            : base(type, stream, name, sectionIndex)
        {
        }
    }

    // ObjectWriter writes directly to the section writer for the code and type sections without going through
    // the WasmSection abstraction.
    internal sealed class WasmExternallyCountedSection : WasmVectorSection
    {
        private bool _entryCountSet;

        public WasmExternallyCountedSection(WasmSectionId type, Stream stream, Utf8String name, int sectionIndex)
            : base(type, stream, name, sectionIndex)
        {
        }

        public override int Emit(WasmModuleWriter writer)
        {
            Debug.Assert(_entryCountSet);
            return base.Emit(writer);
        }

        public void SetEntryCount(int entryCount)
        {
            _entryCountSet = true;
            EntryCount = entryCount;
        }
    }

    internal abstract class WasmSection<TEntry> : WasmVectorSection
    {
        public void WriteEntry(SectionWriter writer, TEntry entry)
        {
            Debug.Assert(writer.SectionIndex == SectionIndex);
            WriteEntryCore(writer, entry);
            CompleteEntry();
        }

        protected abstract void WriteEntryCore(SectionWriter writer, TEntry entry);

        protected static void WriteEncodable<TEncodable>(SectionWriter writer, TEncodable entry)
            where TEncodable : IWasmEncodable
        {
            int encodeSize = entry.EncodeSize();
            int bytesWritten = entry.Encode(writer.Buffer.GetSpan(encodeSize));
            Debug.Assert(bytesWritten == encodeSize);
            writer.Buffer.Advance(bytesWritten);
        }

        protected WasmSection(WasmSectionId type, Stream stream, Utf8String name, int sectionIndex)
            : base(type, stream, name, sectionIndex)
        {
        }
    }

    internal sealed class WasmImportSection : WasmSection<WasmImport>
    {
        public WasmImportSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Import, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmImport entry)
        {
            writer.WriteUtf8WithLength(entry.Module);
            writer.WriteUtf8WithLength(entry.Name);
            writer.WriteByte((byte)entry.Kind);
            WriteEncodable(writer, entry);
        }
    }

    internal sealed class WasmFunctionSection : WasmSection<int>
    {
        public WasmFunctionSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Function, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, int typeIndex)
        {
            writer.WriteULEB128((ulong)typeIndex);
        }
    }

    internal sealed class WasmGlobalSection : WasmSection<WasmGlobal>
    {
        public WasmGlobalSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Global, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmGlobal entry)
        {
            writer.EmitSymbolDefinition(new Utf8String(entry.Name));
            WriteEncodable(writer, entry);
        }
    }

    internal readonly struct WasmExport
    {
        public string Name { get; }
        public WasmExportKind Kind { get; }
        public int Index { get; }

        public WasmExport(string name, WasmExportKind kind, int index)
        {
            Name = name;
            Kind = kind;
            Index = index;
        }
    }

    internal sealed class WasmExportSection : WasmSection<WasmExport>
    {
        public WasmExportSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Export, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmExport entry)
        {
            writer.WriteUtf8WithLength(entry.Name);
            writer.WriteByte((byte)entry.Kind);
            writer.WriteULEB128((ulong)entry.Index);
        }
    }

    /// <summary>
    /// An active <c>funcref</c> element segment targeting table 0, placed by <see cref="OffsetExpr"/>.
    /// The engine installs it at instantiation.
    /// </summary>
    internal readonly struct WasmElementSegment
    {
        public ReadOnlyMemory<int> FunctionIndices { get; }
        public WasmInstructionGroup OffsetExpr { get; }

        public WasmElementSegment(ReadOnlyMemory<int> functionIndices, WasmInstructionGroup offsetExpr)
        {
            FunctionIndices = functionIndices;
            OffsetExpr = offsetExpr;
        }
    }

    internal sealed class WasmElementSection : WasmSection<WasmElementSegment>
    {
        public WasmElementSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Element, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmElementSegment entry)
        {
            ReadOnlySpan<int> functionIndices = entry.FunctionIndices.Span;

            // Active element segment, table 0. Flag 0 implies both the table index and the
            // funcref element type, so no element-type byte follows.
            writer.WriteByte(0);
            WriteEncodable(writer, entry.OffsetExpr);

            writer.WriteULEB128((ulong)functionIndices.Length);
            foreach (int functionIndex in functionIndices)
            {
                writer.WriteULEB128((ulong)functionIndex);
            }
        }
    }
}
