// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using System.Numerics;
using ILCompiler.DependencyAnalysis;
using Internal.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    /// <summary>
    /// A SectionDataEmitter for an ObjectNodeSection emits into a single WASM data segment.
    /// </summary>
    internal sealed class WasmDataSegmentEmitter : SectionDataEmitter, IWasmActiveDataSegment
    {
        private int _alignment = 1;
        private int _memoryOffset;
        private int _paddingBytesCount;

        public WasmDataSegmentEmitter(
            Stream contents,
            Utf8String name,
            int sectionIndex)
            : base(contents, name, sectionIndex)
        {
        }

        public int FileAlignment => 1;
        public int HeaderSize
        {
            get
            {
                Span<byte> offsetExpression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
                return WasmDataSegmentEncoding.GetActiveHeaderSize(EncodeMemoryOffsetExpression(offsetExpression));
            }
        }
        public int ContentSize => checked((int)ContentReadStream.Length + _paddingBytesCount);
        public int MemoryAlignment => _alignment;
        public WasmDataSegmentType SegmentType => WasmDataSegmentType.Active;

        public void UpdateAlignment(int alignment)
        {
            Debug.Assert(BitOperations.IsPow2(alignment));
            _alignment = Math.Max(_alignment, alignment);
        }

        public int EncodeSize() => HeaderSize + ContentSize;

        public int EmitToStream(Stream outputFileStream)
        {
            Span<byte> offsetExpression = stackalloc byte[DataSegmentEncoder.MaxOffsetExpressionLength];
            WasmDataSegmentEncoding.EmitActiveHeader(outputFileStream, EncodeMemoryOffsetExpression(offsetExpression), ContentSize);

            ContentReadStream.Position = 0;
            ContentReadStream.CopyTo(outputFileStream);
            WasmDataSegmentEncoding.EmitPadding(outputFileStream, _paddingBytesCount);

            return EncodeSize();
        }

        public void SetTrailingPadding(int trailingBytesCount)
        {
            Debug.Assert(trailingBytesCount >= 0);
            _paddingBytesCount = trailingBytesCount;
        }

        public void SetMemoryOffset(int offset)
        {
            _memoryOffset = offset;
        }

        // The offset is padded so the header's length doesn't depend on where the segment lands.
        private ReadOnlySpan<byte> EncodeMemoryOffsetExpression(Span<byte> destination) =>
            destination.Slice(0, DataSegmentEncoder.EncodeI32ConstExpression(destination, _memoryOffset, Relocation.WASM_PADDED_RELOC_SIZE_32));

        public int GetMemoryAddressOfOffset(int offsetInSegment)
        {
            Debug.Assert(offsetInSegment >= 0 && offsetInSegment <= ContentReadStream.Length);
            return _memoryOffset + offsetInSegment;
        }
    }
}
