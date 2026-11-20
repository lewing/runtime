// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using Internal.Text;
using Internal.TypeSystem.TypesDebugInfo;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    internal sealed partial class WasmRelocatableObjectWriter
    {
        private DwarfBuilder _dwarfBuilder;
        private SectionWriter _pdataSectionWriter;
        private SectionWriter _xdataSectionWriter;
        private uint _nextVirtualIP = 1; // Zero marks an invalid stack frame.

        partial void WriteRuntimeGlobalImports()
        {
            WriteImport(new WasmImport("env", WasmWellKnownGlobalSymbolNode.AsyncContinuationName, import: new WasmGlobalImportType(WasmValueType.I32, WasmMutabilityType.Mut)));
        }

        partial void WriteRuntimeTagImports()
        {
            int tagType = RegisterSignature(new WasmFuncType(new([]), new([])));
            WriteImport(new WasmImport("env", RestoreContextTagName.ToString(), new WasmTagImportType(tagType)));
        }

        partial void EmitUnwindTableEnd()
        {
            _pdataSectionWriter.WriteLittleEndian<uint>(_nextVirtualIP);
            _pdataSectionWriter.WriteLittleEndian<uint>(0);
            _pdataSectionWriter.EmitSymbolDefinition(new Utf8String("__pdata_end"), global: true);
        }

        private protected override ITypesDebugInfoWriter CreateDebugInfoBuilder()
        {
            return _dwarfBuilder = new DwarfBuilder(
                _nodeFactory.NameMangler,
                _nodeFactory.Target,
                _options.HasFlag(ObjectWritingOptions.UseDwarf5));
        }

        private protected override void EmitDebugFunctionInfo(
            uint methodTypeIndex,
            Utf8String methodDisplayName,
            Utf8String methodName,
            SymbolDefinition methodSymbol,
            INodeWithDebugInfo debugNode)
        {
            EmitDebugMethodInfo(methodTypeIndex, methodName, methodSymbol, debugNode);
        }

        private protected override void EmitDebugThunkInfo(
            Utf8String methodName,
            SymbolDefinition methodSymbol,
            INodeWithDebugInfo debugNode)
        {
            EmitDebugMethodInfo(0, methodName, methodSymbol, debugNode);
        }

        private void EmitDebugMethodInfo(
            uint methodTypeIndex,
            Utf8String methodName,
            SymbolDefinition methodSymbol,
            INodeWithDebugInfo debugNode)
        {
            // RyuJIT includes a five-byte padded body-size field in each function's frame extent.
            const int FunctionBodySizePrefixSize = 5;
            FrameInfo[] frameInfos = ((INodeWithCodeInfo)debugNode).FrameInfos;
            List<NativeSequencePoint> sequencePoints = new(debugNode.GetNativeSequencePoints());
            int sequencePointIndex = 0;
            for (int functionOrdinal = 0; functionOrdinal < frameInfos.Length; functionOrdinal++)
            {
                FrameInfo frameInfo = frameInfos[functionOrdinal];
                int bodyStart = checked(frameInfo.StartOffset + FunctionBodySizePrefixSize);
                int bodyEnd = frameInfo.EndOffset;
                int bodySize = bodyEnd - bodyStart;
                Debug.Assert(bodySize > 0);
                Utf8String functionName = functionOrdinal == 0 ? methodName : GetFuncletName(methodName, functionOrdinal - 1);
                int functionIndex = _wasmSymbolManager.GetSymbol(functionName).Index;
                // Folded methods share the same function index and body offset.
                _functionOffsets[functionIndex] = checked(methodSymbol.Value + bodyStart);
                _dwarfBuilder.EmitSectionInfo(functionName, (ulong)bodySize);
                // Wasm variable locations are not described yet.
                _dwarfBuilder.EmitSubprogramInfo(methodName, functionName, 0, bodySize, methodTypeIndex, [], [], []);

                List<NativeSequencePoint> functionPoints = new();
                while (sequencePointIndex < sequencePoints.Count && sequencePoints[sequencePointIndex].NativeOffset < bodyEnd)
                {
                    NativeSequencePoint point = sequencePoints[sequencePointIndex++];
                    Debug.Assert(point.NativeOffset >= frameInfo.StartOffset);
                    // JIT offsets include the body length; DWARF function offsets start at the locals vector.
                    functionPoints.Add(new NativeSequencePoint(
                        Math.Max(0, point.NativeOffset - bodyStart),
                        point.FileName,
                        point.LineNumber,
                        point.ColNumber));
                }
                if (functionPoints.Count > 0)
                {
                    _dwarfBuilder.EmitLineInfo(
                        functionIndex,
                        functionName,
                        0,
                        functionPoints,
                        bodySize);
                }
            }
        }

        private protected override void EmitDebugSections(IDictionary<Utf8String, SymbolDefinition> definedSymbols)
        {
            bool useDwarf5 = _dwarfBuilder.UseDwarf5;
            SectionWriter info = GetOrCreateSection(new ObjectNodeSection(".debug_info", SectionType.Debug));
            SectionWriter strings = GetOrCreateSection(new ObjectNodeSection(".debug_str", SectionType.Debug));
            SectionWriter abbrev = GetOrCreateSection(new ObjectNodeSection(".debug_abbrev", SectionType.Debug));
            SectionWriter loc = GetOrCreateSection(new ObjectNodeSection(useDwarf5 ? ".debug_loclists" : ".debug_loc", SectionType.Debug));
            SectionWriter ranges = GetOrCreateSection(new ObjectNodeSection(useDwarf5 ? ".debug_rnglists" : ".debug_ranges", SectionType.Debug));
            SectionWriter lines = GetOrCreateSection(new ObjectNodeSection(".debug_line", SectionType.Debug));
            SectionWriter aranges = GetOrCreateSection(new ObjectNodeSection(".debug_aranges", SectionType.Debug));
            if (useDwarf5)
            {
                _dwarfBuilder.WriteAddressTable(GetOrCreateSection(new ObjectNodeSection(".debug_addr", SectionType.Debug)));
            }
            _dwarfBuilder.Write(info, strings, abbrev, loc, ranges, lines, aranges, _ => (default, 0));
        }

        private protected override void EmitUnwindInfo(
            SectionWriter sectionWriter,
            INodeWithCodeInfo nodeWithCodeInfo,
            Utf8String currentSymbolName)
        {
            if (nodeWithCodeInfo.FrameInfos is not FrameInfo[] frames || frames.Length == 0)
            {
                return;
            }

            // Bit 31 is reserved by the managed stack-trace resolver for StackTraceHidden.
            _methodStartVirtualIPs.Add(currentSymbolName, checked((uint)(int)_nextVirtualIP));
            Span<byte> indexBuffer = stackalloc byte[16];
            for (int i = 0; i < frames.Length; i++)
            {
                FrameInfo frame = frames[i];
                int position = 0;
                uint frameSize = WasmLeb128.ReadU32(frame.BlobData, ref position);
                Debug.Assert(frameSize % _nodeFactory.Target.PointerSize == 0);
                uint virtualSize = WasmLeb128.ReadU32(frame.BlobData, ref position);
                Debug.Assert(virtualSize != 0);
                uint virtualStart = _nextVirtualIP;
                _nextVirtualIP = checked(virtualStart + virtualSize);

                _utf8StringBuilder.Clear()
                    .Append("_unwind"u8)
                    .Append(FormatUtf8Int(indexBuffer, i))
                    .Append(currentSymbolName);
                Utf8String unwindSymbolName = _utf8StringBuilder.ToUtf8String();
                _xdataSectionWriter.EmitSymbolDefinition(unwindSymbolName);
                _xdataSectionWriter.WriteUnsigned(frameSize / (uint)_nodeFactory.Target.PointerSize);
                FrameInfoFlags flags = frame.Flags;
                if (i == 0 && nodeWithCodeInfo.EHInfo is not null)
                    flags |= FrameInfoFlags.HasEHInfo;
                _xdataSectionWriter.WriteByte((byte)flags);

                if (i == 0)
                {
                    if (nodeWithCodeInfo.EHInfo is not null)
                    {
                        _xdataSectionWriter.EmitSymbolReference(RelocType.IMAGE_REL_BASED_HIGHLOW, GetMangledName(nodeWithCodeInfo.EHInfo));
                    }
                    _xdataSectionWriter.Write(nodeWithCodeInfo.GCInfo);
                }
                else
                {
                    _xdataSectionWriter.EmitSymbolReference(RelocType.IMAGE_REL_BASED_HIGHLOW, GetFuncletName(currentSymbolName, i - 1));
                }

                // Keep in sync with WasmRuntimeFunction in the NativeAOT code manager.
                _pdataSectionWriter.WriteLittleEndian<uint>(virtualStart);
                _pdataSectionWriter.EmitSymbolReference(RelocType.IMAGE_REL_BASED_HIGHLOW, unwindSymbolName);
            }
        }

        private protected override void CreateEhSections()
        {
            _pdataSectionWriter = GetOrCreateSection(new ObjectNodeSection(".pdata", SectionType.ReadOnly));
            _pdataSectionWriter.EmitAlignment(sizeof(uint));
            _pdataSectionWriter.EmitSymbolDefinition(new Utf8String("__pdata_start"), global: true);
            _xdataSectionWriter = GetOrCreateSection(new ObjectNodeSection(".xdata", SectionType.ReadOnly));
        }
    }
}
