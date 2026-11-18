// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.IO;
using ILCompiler.ObjectWriter.WasmInstructions;
using Internal.TypeSystem;
using Microsoft.NET.WebAssembly.Webcil;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    /// <summary>
    /// The active data segment of Webcil modules that contains the Webcil payload composed of WebcilSections.
    /// </summary>
    /// <remarks>
    /// The segment is placed by an offset expression that reads a host-supplied base-address global, so
    /// the engine copies the payload into the host's allocation at instantiation.
    /// </remarks>
    internal sealed class WebcilPayloadDataSegment : IWasmActiveDataSegment
    {
        private readonly WebcilHeader _header;
        private readonly WebcilSection[] _sections;
        private readonly int _alignment;
        private readonly WasmInstructionGroup _initExpr;
        private int _paddingBytesCount;

        public WebcilPayloadDataSegment(
            WebcilHeader header,
            WebcilSection[] sections,
            WasmInstructionGroup initExpr)
        {
            _header = header;
            _sections = sections;
            _initExpr = initExpr;
            _alignment = WebCilObjectWriter.WebcilSectionAlignment;
            foreach (WebcilSection section in sections)
            {
                _alignment = Math.Max(_alignment, section.Alignment);
            }
        }

        public int HeaderSize => WasmDataSegmentEncoding.GetActiveHeaderSize(_initExpr.Bytes);

        public int FileAlignment => _alignment;

        public int MemoryAlignment => _alignment;

        private int RawContentSize
        {
            get
            {
                int size = WebcilEncoder.HeaderEncodeSize(WebcilVersion.Version1);
                size += _sections.Length * WebcilEncoder.SectionHeaderEncodeSize();
                if (_sections.Length == 0)
                {
                    return AlignmentHelper.AlignUp(size, WebCilObjectWriter.WebcilSectionAlignment);
                }

                WebcilSection lastSection = _sections[_sections.Length - 1];
                Debug.Assert(!lastSection.Header.Equals(default(WebcilSectionHeader)));
                return checked((int)(lastSection.Header.PointerToRawData + lastSection.Header.SizeOfRawData));
            }
        }

        public int ContentSize => checked(RawContentSize + _paddingBytesCount);

        public WasmDataSegmentType SegmentType => WasmDataSegmentType.Active;

        public int EncodeSize() => HeaderSize + ContentSize;

        public int EmitToStream(Stream outputFileStream)
        {
            WasmDataSegmentEncoding.EmitActiveHeader(outputFileStream, _initExpr.Bytes, ContentSize);

            // Offsets are tracked here rather than read from the stream, which may not be seekable.
            long payloadOffset = WebcilEncoder.EmitHeader(_header, outputFileStream);

            foreach (WebcilSection section in _sections)
            {
                payloadOffset += WebcilEncoder.EncodeSectionHeader(section.Header, outputFileStream);
            }

            foreach (WebcilSection section in _sections)
            {
                long sectionStart = section.Header.PointerToRawData;
                Debug.Assert(payloadOffset <= sectionStart);
                WasmDataSegmentEncoding.EmitPadding(outputFileStream, (int)(sectionStart - payloadOffset));
                payloadOffset = sectionStart + section.EmitToStream(outputFileStream);
            }

            Debug.Assert(payloadOffset <= RawContentSize);
            WasmDataSegmentEncoding.EmitPadding(outputFileStream, (int)(RawContentSize - payloadOffset));
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
        }

        public int GetMemoryAddressOfOffset(int offsetInSegment) =>
            throw new NotSupportedException("Active WebCIL payload segments use an imported base-address global.");
    }
}
