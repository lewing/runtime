// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using ILCompiler.DependencyAnalysis;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    internal enum WasmDataSegmentType : byte
    {
        // (data list(byte) (active offset-expr))
        // Active segments are loaded by the wasm runtime into linear memory at the specified offset.
        Active = 0,
        // (data list(byte) passive)
        // Passive segments are not loaded into linear memory by the wasm runtime, but can be loaded by the program at runtime using the `memory.init` instruction.
        Passive = 1,
        // (data list(byte) (active memidx offset-expr))
        // ActiveMemorySpecified sections are loaded by the wasm runtime into linear memory at the specified offset, but also specify a memory index to load into.
        // We do not create or read any segments of this type.
        // ActiveMemorySpecified = 2
    }

    /// <summary>
    /// Interface for a data segment in a WASM data section.
    /// These members help ensure the Data section can properly align the segment content in the WASM module.
    /// </summary>
    internal interface IWasmDataSegment : IWasmEmittable
    {
        /// <summary>
        /// The size of the header of the segment. Alignment of the segments expects this to be constant for a given
        /// segment, so it should not depend on the content size.
        /// </summary>
        int HeaderSize { get; }

        /// <summary>
        /// The size of the content of the segment.
        /// </summary>
        int ContentSize { get; }

        /// <summary>
        /// The required alignment of the segment content in the WASM module.
        /// </summary>
        int FileAlignment { get; }

        WasmDataSegmentType SegmentType => WasmDataSegmentType.Passive;

        /// <summary>
        /// Sets the number of padding bytes to emit after the segment content. This is used to ensure that the next
        /// segment is aligned properly.
        /// </summary>
        void SetTrailingPadding(int trailingBytesCount);

        /// <summary>
        /// Gets the offset of the segment in linear memory when loaded.
        /// For passive segments, returns <paramref name="offsetInSegment"/>
        /// </summary>
        int GetMemoryAddressOfOffset(int offsetInSegment);
    }

    internal interface IWasmActiveDataSegment : IWasmDataSegment
    {
        WasmDataSegmentType IWasmDataSegment.SegmentType => WasmDataSegmentType.Active;

        /// <summary>
        /// The required alignment of the segment content in linear memory when loaded.
        /// </summary>
        int MemoryAlignment { get; }

        /// <summary>
        /// For active segments, sets the offset of the segment in linear memory. For passive segments, this is a no-op.
        /// </summary>
        void SetMemoryOffset(int offset);
    }

    internal static class WasmDataSegmentEncoding
    {
        // Segment size fields are padded so that a header's length doesn't depend on its content size, which the
        // file alignment of segments needs. In NativeAOT objects the size is also a relocation site.
        private const int SizeFieldWidth = Relocation.WASM_PADDED_RELOC_SIZE_32;

        public static int GetPassiveHeaderSize() => DataSegmentEncoder.GetPassiveHeaderSize(0, SizeFieldWidth);

        public static int GetActiveHeaderSize(ReadOnlySpan<byte> offsetExpression) =>
            DataSegmentEncoder.GetActiveHeaderSize(offsetExpression, 0, SizeFieldWidth);

        public static void EmitPassiveHeader(Stream outputFileStream, int contentSize)
        {
            Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
            outputFileStream.Write(header.Slice(0, DataSegmentEncoder.EncodePassiveHeader(header, contentSize, SizeFieldWidth)));
        }

        public static void EmitActiveHeader(Stream outputFileStream, ReadOnlySpan<byte> offsetExpression, int contentSize)
        {
            Span<byte> header = stackalloc byte[DataSegmentEncoder.MaxHeaderLength];
            outputFileStream.Write(header.Slice(0, DataSegmentEncoder.EncodeActiveHeader(header, offsetExpression, contentSize, SizeFieldWidth)));
        }

        public static void EmitPadding(Stream outputFileStream, int padding)
        {
            if (padding == 0)
                return;

            Span<byte> paddingBytes = stackalloc byte[Math.Min(padding, 256)];
            paddingBytes.Clear();
            while (padding > 0)
            {
                int paddingSize = Math.Min(padding, paddingBytes.Length);
                outputFileStream.Write(paddingBytes.Slice(0, paddingSize));
                padding -= paddingSize;
            }
        }
    }
}
