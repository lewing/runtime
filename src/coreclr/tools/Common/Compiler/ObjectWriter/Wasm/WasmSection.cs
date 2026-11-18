// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
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
            WriteEntryCore(writer, new WasmModuleEntryWriter(writer.Buffer), entry);
            CompleteEntry();
        }

        protected abstract void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, TEntry entry);

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

        protected override void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, WasmImport entry) =>
            entry.Write(entries);
    }

    internal sealed class WasmFunctionSection : WasmSection<int>
    {
        public WasmFunctionSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Function, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, int typeIndex) =>
            entries.Function(checked((uint)typeIndex));
    }

    internal sealed class WasmGlobalSection : WasmSection<WasmGlobal>
    {
        public WasmGlobalSection(Stream stream, Utf8String name, int sectionIndex)
            : base(WasmSectionId.Global, stream, name, sectionIndex)
        {
        }

        protected override void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, WasmGlobal entry)
        {
            writer.EmitSymbolDefinition(new Utf8String(entry.Name));
            entry.Write(entries);
        }
    }

    internal readonly struct WasmExport
    {
        public string Name { get; }
        public WasmExternalKind Kind { get; }
        public int Index { get; }

        public WasmExport(string name, WasmExternalKind kind, int index)
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

        protected override void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, WasmExport entry) =>
            entries.Export(Encoding.UTF8.GetBytes(entry.Name), entry.Kind, checked((uint)entry.Index));
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

        protected override void WriteEntryCore(SectionWriter writer, WasmModuleEntryWriter entries, WasmElementSegment entry) =>
            entries.ActiveElementSegment(entry.OffsetExpr.Bytes, MemoryMarshal.Cast<int, uint>(entry.FunctionIndices.Span));
    }
}
