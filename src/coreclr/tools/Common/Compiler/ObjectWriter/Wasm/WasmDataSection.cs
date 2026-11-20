// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ILCompiler.DependencyAnalysis;
using Internal.Text;
using Internal.TypeSystem;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    // WasmDataSection should be aligned to the maximum file alignment of its segments,
    // and each segment should be aligned to its own alignment within the section.
    internal sealed class WasmDataSection : IWasmSection
    {
        private readonly List<IWasmDataSegment> _segments;
        private readonly Dictionary<IWasmDataSegment, (int Index, int ContentOffset)> _segmentLayouts = new();
        private int _contentSize;

        public WasmDataSection(List<IWasmDataSegment> segments, Utf8String name)
        {
            _segments = segments;
            Name = name;
        }

        public Utf8String Name { get; }
        public WasmSectionId Type => WasmSectionId.Data;
        public int SegmentCount => _segments.Count;

        /// <summary>
        /// Gets the data segments in their serialized order.
        /// </summary>
        public IReadOnlyList<IWasmDataSegment> Segments => _segments;
        public int FileAlignment
        {
            get
            {
                int alignment = 1;
                foreach (IWasmDataSegment segment in _segments)
                {
                    alignment = Math.Max(alignment, segment.FileAlignment);
                }
                return alignment;
            }
        }

        public int ContentSize
        {
            get
            {
                AssignSegmentLayout();
                return _contentSize;
            }
        }

        /// <summary>
        /// Gets the index of <paramref name="segment"/> in the data section.
        /// </summary>
        public int GetSegmentIndex(IWasmDataSegment segment)
        {
            AssignSegmentLayout();
            return _segmentLayouts[segment].Index;
        }

        /// <summary>
        /// Gets the offset of <paramref name="segment"/>'s content relative to the data section payload.
        /// </summary>
        public int GetSegmentContentOffset(IWasmDataSegment segment)
        {
            AssignSegmentLayout();
            return _segmentLayouts[segment].ContentOffset;
        }

        // The section's size field is padded so that segment file offsets don't depend on the content size.
        // Webcil could use a minimal one, but in NativeAOT this is the patch site of a relocation.
        private const int SizeFieldWidth = Relocation.WASM_PADDED_RELOC_SIZE_32;
        private const int HeaderSize = 1 + SizeFieldWidth;

        public int Emit(WasmModuleWriter writer)
        {
            // The active segment memory offset expression may change the size of a segment, so the layout must be
            // assigned before calculating the total size of the data section.
            AssignSegmentLayout();
            int index = writer.BeginSection(WasmSectionId.Data, ContentSize, SizeFieldWidth);
            Span<byte> count = stackalloc byte[WasmLeb128.MaxLength];
            writer.Write(count.Slice(0, WasmLeb128.WriteULeb128(count, (ulong)_segments.Count)));
            foreach (IWasmDataSegment segment in _segments)
            {
                segment.EmitToStream(writer.ContentStream);
            }

            writer.EndSection();
            return index;
        }

        /// <summary>
        /// Assign the layout of segments within the data section, padding segments for file alignment, and placing
        /// active segments at the appropriate memory offsets.
        /// </summary>
        public void AssignSegmentLayout()
        {
            // The segment-count prefix makes the completed content size nonzero, even for an empty section.
            if (_contentSize != 0)
                return;

            // Assign sizes to ensure each segment's content is aligned to its FileAlignment.
            // The first should have no alignment requirements - this simplifies that the alignment of the data section itself.
            Debug.Assert(_segments.Count == 0 || _segments[0].FileAlignment == 1);
            int fileOffset = HeaderSize + WasmLeb128.GetULeb128Size((ulong)_segments.Count);
            for (int i = 1; i < _segments.Count; i++)
            {
                IWasmDataSegment segment = _segments[i];
                IWasmDataSegment previousSegment = _segments[i - 1];
                int previousSegmentEnd = fileOffset + previousSegment.HeaderSize + previousSegment.ContentSize;
                int contentStart = previousSegmentEnd + segment.HeaderSize;
                int padding = AlignmentHelper.AlignUp(contentStart, segment.FileAlignment) - contentStart;
                previousSegment.SetTrailingPadding(padding);
                // Use updated previous segment size as the file offset.
                fileOffset = fileOffset + previousSegment.HeaderSize + previousSegment.ContentSize;
                Debug.Assert((fileOffset + segment.HeaderSize) % segment.FileAlignment == 0);
            }

            int memoryOffset = 0;
            int contentOffset = WasmLeb128.GetULeb128Size((ulong)_segments.Count);
            for (int i = 0; i < _segments.Count; i++)
            {
                IWasmDataSegment segment = _segments[i];
                // Handle memory layout for active segments
                if (segment.SegmentType == WasmDataSegmentType.Active)
                {
                    IWasmActiveDataSegment activeSegment = (IWasmActiveDataSegment)segment;
                    int alignment = activeSegment.MemoryAlignment;
                    memoryOffset = AlignmentHelper.AlignUp(memoryOffset, alignment);
                    activeSegment.SetMemoryOffset(memoryOffset);
                    memoryOffset += activeSegment.ContentSize;
                }

                _segmentLayouts.Add(segment, (i, checked(contentOffset + segment.HeaderSize)));
                contentOffset = checked(contentOffset + segment.EncodeSize());
            }
            _contentSize = contentOffset;
        }
    }
}
