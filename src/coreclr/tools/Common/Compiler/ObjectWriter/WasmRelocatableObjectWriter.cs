// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Diagnostics;
using System.Collections.Generic;
using System.IO;
using System.Numerics;
using ILCompiler.DependencyAnalysis;
using Internal.JitInterface;
using Internal.Text;
using Internal.TypeSystem.TypesDebugInfo;
using ILCompiler.DependencyAnalysis.Wasm;
using System.Linq;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;
using RelocationType = Microsoft.NET.WebAssembly.Binary.Linking.RelocationType;
using RelocationTypes = Microsoft.NET.WebAssembly.Binary.Linking.RelocationTypes;
using SegmentFlags = Microsoft.NET.WebAssembly.Binary.Linking.SegmentFlags;
using SymbolFlags = Microsoft.NET.WebAssembly.Binary.Linking.SymbolFlags;

namespace ILCompiler.ObjectWriter
{
    internal sealed partial class WasmRelocatableObjectWriter : WasmObjectWriter
    {
        private WasmDataSection _dataSection;
        private DwarfBuilder _dwarfBuilder;
        private SectionWriter _pdataSectionWriter;
        private SectionWriter _xdataSectionWriter;
        private uint _nextVirtualIP = 1; // Zero marks an invalid stack frame.
        private readonly Dictionary<Utf8String, uint> _methodStartVirtualIPs = new();
        private static readonly Utf8String RestoreContextTagName = new("__nativeaot_wasm_restore_context_tag");
        private static readonly ObjectNodeSection HydrationTargetSection = new(".bss.hydrated", SectionType.Uninitialized);


        private readonly Dictionary<Utf8String, int> _linkingSymbolIndices = new();
        private readonly Dictionary<int, List<SymbolicRelocation>> _relocations = new();
        private readonly Dictionary<int, long> _functionOffsets = new();

        public WasmRelocatableObjectWriter(NodeFactory factory, ObjectWritingOptions options, OutputInfoBuilder outputInfoBuilder = null) : base(factory, options, outputInfoBuilder)
        {
        }

        private protected override ObjectNodeSection GetEmitSection(ObjectNodeSection section)
        {
            if (section == ObjectNodeSection.HydrationTargetSection)
            {
                // wasm-ld omits zero-initialized segments whose names start with ".bss".
                return HydrationTargetSection;
            }

            if (section == ObjectNodeSection.TextSection ||
                section == ObjectNodeSection.ManagedCodeUnixContentSection ||
                section == ObjectNodeSection.ManagedCodeWindowsContentSection)
            {
                return ObjectNodeSection.WasmCodeSection;
            }

            return section;
        }

        private protected override void EmitSectionsAndLayout()
        {
            if (_options.HasFlag(ObjectWritingOptions.GenerateUnwindInfo))
            {
                _pdataSectionWriter.WriteLittleEndian<uint>(_nextVirtualIP);
                _pdataSectionWriter.WriteLittleEndian<uint>(0);
                _pdataSectionWriter.EmitSymbolDefinition(new Utf8String("__pdata_end"), global: true);
            }

            List<IWasmDataSegment> dataSegments = new();
            foreach (SectionDataEmitter section in _sections.Sections)
            {
                if (section is WasmDataSegmentEmitter dataSegment)
                {
                    dataSegments.Add(dataSegment);
                }
            }

            if (dataSegments.Count == 0)
            {
                return;
            }

            SectionWriter writer = GetOrCreateSection(WasmObjectNodeSection.DataCountSection);
            writer.WriteULEB128((ulong)dataSegments.Count);
            _dataSection = new WasmDataSection(dataSegments, new Utf8String("data"));
        }

        private protected override void EmitObjectFile(Stream outputFileStream)
        {
            FinalizeSectionEntryCounts();
            _dataSection?.AssignSegmentLayout();

            List<IWasmSection> sections = new();
            foreach (int index in SectionEmitOrder)
            {
                if (_sections[index] is IWasmSection section)
                {
                    sections.Add(section);
                }
            }

            int dataSectionIndex = sections.Count;
            if (_dataSection is not null)
            {
                sections.Add(_dataSection);
            }

            foreach (SectionDataEmitter section in _sections.Sections)
            {
                if (section is WasmCustomSection customSection)
                {
                    sections.Add(customSection);
                }
            }

            Dictionary<int, int> fileSectionIndices = new();
            for (int i = 0; i < sections.Count; i++)
            {
                if (sections[i] is SectionDataEmitter section)
                {
                    fileSectionIndices.Add(section.SectionIndex, i);
                }
            }

            LinkingSectionEncoder linking = CreateLinkingSection(fileSectionIndices);
            Dictionary<int, (byte[] Name, RelocationSectionEncoder Encoder)> relocationSections = new();
            foreach ((int sectionIndex, List<SymbolicRelocation> relocations) in _relocations)
            {
                SectionDataEmitter section = _sections[sectionIndex];
                (int contentOffset, int targetSectionIndex, string name) = section switch
                {
                    WasmCustomSection customSection => (
                        0,
                        fileSectionIndices[sectionIndex],
                        $"reloc.{customSection.SectionName}"),
                    WasmSection wasmSection => (
                        wasmSection.ContentPrefixSize,
                        fileSectionIndices[sectionIndex],
                        "reloc.CODE"),
                    WasmDataSegmentEmitter segment => (
                        _dataSection.GetSegmentContentOffset(segment),
                        dataSectionIndex,
                        "reloc.DATA"),
                    _ => throw new UnreachableException(),
                };
                if (!relocationSections.TryGetValue(targetSectionIndex, out var relocationSection))
                {
                    relocationSection = (Encoding.UTF8.GetBytes(name), new RelocationSectionEncoder((uint)targetSectionIndex));
                    relocationSections.Add(targetSectionIndex, relocationSection);
                }
                WriteRelocationEntries(section, contentOffset, relocations, relocationSection.Encoder);
            }

            WasmModuleWriter writer = new(outputFileStream);
            foreach (IWasmSection section in sections)
            {
                int fileSectionIndex = section.Emit(writer);
                Debug.Assert(section is not SectionDataEmitter emitter || fileSectionIndices[emitter.SectionIndex] == fileSectionIndex);
            }

            writer.WriteCustomSection("linking"u8, linking);
            foreach ((byte[] name, RelocationSectionEncoder encoder) in relocationSections.Values)
            {
                if (encoder.Count != 0)
                {
                    writer.WriteCustomSection(name, encoder);
                }
            }
        }

        private LinkingSectionEncoder CreateLinkingSection(Dictionary<int, int> fileSectionIndices)
        {
            SymbolTableEncoder symbols = new();

            foreach (WasmIndexSpace indexSpace in new[] { WasmIndexSpace.Function, WasmIndexSpace.Global, WasmIndexSpace.Table, WasmIndexSpace.Tag })
            {
                foreach (WasmSymbol symbol in _wasmSymbolManager.GetDefinitions(indexSpace))
                {
                    AddIndexedSymbol(symbols, symbol);
                }
            }

            foreach ((Utf8String name, SymbolDefinition definition) in _definedSymbols)
            {
                if (_wasmSymbolManager.TryGetSymbol(name, out WasmSymbol symbol) && symbol.IndexSpace == WasmIndexSpace.Function)
                {
                    if (!_linkingSymbolIndices.ContainsKey(name))
                    {
                        AddIndexedSymbol(symbols, symbol);
                    }
                    continue;
                }

                if (_sections[definition.SectionIndex] is not WasmDataSegmentEmitter segment)
                {
                    continue;
                }

                AddSymbolIndex(name, symbols.AddData(
                    SymbolFlags.None,
                    name.AsSpan(),
                    (uint)_dataSection.GetSegmentIndex(segment),
                    checked((ulong)definition.Value),
                    (ulong)definition.Size));
            }

            foreach (Utf8String name in GetUndefinedSymbols())
            {
                if (!_linkingSymbolIndices.ContainsKey(name))
                {
                    AddSymbolIndex(name, symbols.AddUndefinedData(SymbolFlags.Undefined, name.AsSpan()));
                }
            }

            foreach (SectionDataEmitter section in _sections.Sections)
            {
                if (section is WasmCustomSection)
                {
                    AddSymbolIndex(section.SectionName, symbols.AddSection(SymbolFlags.BindingLocal, (uint)fileSectionIndices[section.SectionIndex]));
                }
            }

            // Subsections use minimal size fields; LLVM pads them to 5 bytes.
            LinkingSectionEncoder linking = new();
            linking.WriteSymbolTable(symbols);

            if (_dataSection is not null)
            {
                SegmentInfoEncoder segments = new();
                foreach (WasmDataSegmentEmitter segment in _dataSection.Segments)
                {
                    segments.Add(segment.SectionName.AsSpan(), (uint)BitOperations.Log2((uint)segment.MemoryAlignment), SegmentFlags.None);
                }
                linking.WriteSegmentInfo(segments);
            }

            return linking;
        }

        private void AddSymbolIndex(Utf8String name, int index)
        {
            Debug.Assert(index == _linkingSymbolIndices.Count);
            _linkingSymbolIndices.Add(name, index);
        }

        private void AddIndexedSymbol(SymbolTableEncoder symbols, WasmSymbol symbol)
        {
            SymbolFlags flags = symbol.IsImport ? SymbolFlags.Undefined : SymbolFlags.None;
            if (symbol.HasExplicitName)
            {
                flags |= SymbolFlags.ExplicitName;
            }
            if (!symbol.IsImport && _definedSymbols.TryGetValue(symbol.Name, out SymbolDefinition definition) && definition.Global)
            {
                flags |= SymbolFlags.Exported;
            }

            // Imports without an explicit name take the import's name, so the symbol table omits it.
            ReadOnlySpan<byte> name = !symbol.IsImport || symbol.HasExplicitName ? symbol.Name.AsSpan() : default;
            uint index = (uint)symbol.Index;
            AddSymbolIndex(symbol.Name, symbol.IndexSpace switch
            {
                WasmIndexSpace.Function => symbols.AddFunction(flags, index, name),
                WasmIndexSpace.Global => symbols.AddGlobal(flags, index, name),
                WasmIndexSpace.Table => symbols.AddTable(flags, index, name),
                WasmIndexSpace.Tag => symbols.AddTag(flags, index, name),
                _ => throw new UnreachableException(),
            });
        }

        private protected override void EmitRelocations(int sectionIndex, List<SymbolicRelocation> relocationList)
        {
            if (relocationList.Count > 0)
            {
                _relocations.Add(sectionIndex, relocationList);
            }
        }

        private unsafe void WriteRelocationEntries(SectionDataEmitter section, int contentOffset, List<SymbolicRelocation> relocs, RelocationSectionEncoder encoder)
        {
            using Stream originalStream = section.ContentReadStream;
            MemoryStream sectionStream = new((int)originalStream.Length);
            originalStream.Position = 0;
            originalStream.CopyTo(sectionStream);
            byte[] relocScratchBuffer = new byte[Relocation.MaxSize];
            WasmSection codeSection = _sections.GetSection<WasmSection>(ObjectNodeSection.WasmCodeSection.Name);
            int codeSectionPrefixSize = codeSection.ContentPrefixSize;

            foreach (SymbolicRelocation reloc in relocs)
            {
                int size = Relocation.GetSize(reloc.Type);

                sectionStream.Position = reloc.Offset;
                sectionStream.ReadExactly(relocScratchBuffer.AsSpan(0, size));
                fixed (byte* pData = relocScratchBuffer)
                {
                    long addend = reloc.Addend + Relocation.ReadValue(reloc.Type, pData);
                    if (reloc.Type is RelocType.WASM_METHOD_RELATIVE_VIRTUAL_IP_I32)
                    {
                        Relocation.WriteValue(reloc.Type, pData, checked(_methodStartVirtualIPs[reloc.SymbolName] + addend));
                        sectionStream.Position = reloc.Offset;
                        sectionStream.Write(relocScratchBuffer.AsSpan(0, size));
                        continue;
                    }
                    if (reloc.Type is RelocType.WASM_METHOD_FRAME_IDENTITY_SLEB)
                    {
                        Debug.Assert(addend == 0);
                        // Check that the method start fits alongside the frame-identity tag, then encode its i32 bits.
                        addend = unchecked((int)checked(_methodStartVirtualIPs[reloc.SymbolName] * 2 + 1));
                        Relocation.WriteValue(reloc.Type, pData, addend);
                        sectionStream.Position = reloc.Offset;
                        sectionStream.Write(relocScratchBuffer.AsSpan(0, size));
                        continue;
                    }

                    bool isFunction = _wasmSymbolManager.TryGetSymbol(reloc.SymbolName, out WasmSymbol symbol) &&
                        symbol.IndexSpace == WasmIndexSpace.Function;
                    bool isSection = _definedSymbols.TryGetValue(reloc.SymbolName, out SymbolDefinition definition) &&
                        _sections[definition.SectionIndex] is WasmCustomSection;
                    RelocationType type = reloc.Type switch
                    {
                        RelocType.WASM_FUNCTION_INDEX_LEB => RelocationType.R_WASM_FUNCTION_INDEX_LEB,
                        RelocType.WASM_TYPE_INDEX_LEB => RelocationType.R_WASM_TYPE_INDEX_LEB,
                        RelocType.WASM_GLOBAL_INDEX_LEB => RelocationType.R_WASM_GLOBAL_INDEX_LEB,
                        RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB => RelocationType.R_WASM_TAG_INDEX_LEB,
                        RelocType.WASM_TABLE_INDEX_SLEB => RelocationType.R_WASM_TABLE_INDEX_SLEB,
                        RelocType.WASM_TABLE_INDEX_I32 => RelocationType.R_WASM_TABLE_INDEX_I32,
                        RelocType.WASM_TABLE_INDEX_I64 => RelocationType.R_WASM_TABLE_INDEX_I64,
                        RelocType.WASM_MEMORY_ADDR_LEB => RelocationType.R_WASM_MEMORY_ADDR_LEB,
                        RelocType.WASM_MEMORY_ADDR_SLEB when isFunction => RelocationType.R_WASM_TABLE_INDEX_SLEB,
                        RelocType.WASM_MEMORY_ADDR_SLEB => RelocationType.R_WASM_MEMORY_ADDR_SLEB,
                        RelocType.WASM_MEMORY_ADDR_REL_SLEB when isFunction => RelocationType.R_WASM_TABLE_INDEX_SLEB,
                        RelocType.WASM_MEMORY_ADDR_REL_SLEB => RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB,
                        RelocType.IMAGE_REL_BASED_HIGHLOW when isFunction && section is WasmCustomSection => RelocationType.R_WASM_FUNCTION_OFFSET_I32,
                        RelocType.IMAGE_REL_BASED_HIGHLOW when isSection => RelocationType.R_WASM_SECTION_OFFSET_I32,
                        RelocType.IMAGE_REL_BASED_HIGHLOW when isFunction => RelocationType.R_WASM_TABLE_INDEX_I32,
                        RelocType.IMAGE_REL_BASED_HIGHLOW => RelocationType.R_WASM_MEMORY_ADDR_I32,
                        RelocType.IMAGE_REL_BASED_DIR64 => RelocationType.R_WASM_MEMORY_ADDR_I64,
                        RelocType.IMAGE_REL_BASED_RELPTR32 => RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32,
                        _ => throw new NotSupportedException($"Relocation type {reloc.Type} for symbol '{reloc.SymbolName}' in section {section.SectionName} not yet implemented"),
                    };
                    Utf8String symbolName = reloc.SymbolName;
                    if (reloc.Type == RelocType.WASM_CLR_RESTORE_CONTEXT_EXCEPTION_TAG_LEB)
                    {
                        symbolName = RestoreContextTagName;
                        symbol = _wasmSymbolManager.GetSymbol(symbolName);
                    }
                    if (isFunction && type != RelocationType.R_WASM_FUNCTION_OFFSET_I32 && addend != 0)
                    {
                        // The JIT encodes funclet references as parent + ordinal, but the linker can reorder table entries.
                        Utf8String funcletName = GetFuncletName(symbolName, checked((int)addend - 1));
                        symbol = _wasmSymbolManager.GetSymbol(funcletName);
                        Debug.Assert(symbol.IndexSpace == WasmIndexSpace.Function);
                        symbolName = funcletName;
                        addend = 0;
                    }
                    if (!RelocationTypes.HasAddend(type) && addend != 0)
                    {
                        throw new NotSupportedException($"Nonzero addend for {reloc.Type} relocation to '{reloc.SymbolName}'");
                    }

                    // The index is the symbol index, or the type index for R_WASM_TYPE_INDEX_LEB.
                    encoder.Add(
                        type,
                        checked((uint)(contentOffset + reloc.Offset)),
                        (uint)(type == RelocationType.R_WASM_TYPE_INDEX_LEB ? symbol.Index : _linkingSymbolIndices[symbolName]),
                        addend);

                    long value = type switch
                    {
                        RelocationType.R_WASM_FUNCTION_INDEX_LEB or RelocationType.R_WASM_TYPE_INDEX_LEB or RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_TAG_INDEX_LEB => symbol.Index,
                        RelocationType.R_WASM_FUNCTION_OFFSET_I32 => codeSectionPrefixSize + _functionOffsets[symbol.Index] + addend,
                        RelocationType.R_WASM_SECTION_OFFSET_I32 => addend,
                        _ => 0,
                    };
                    Relocation.WriteValue(reloc.Type, pData, value);
                    sectionStream.Position = reloc.Offset;
                    sectionStream.Write(relocScratchBuffer.AsSpan(0, size));
                }
            }

            section.ContentReadStream = sectionStream;
        }

        // COMDAT groups are not supported yet.
        private protected override bool UsesSubsectionsViaSymbols => true;

        private protected override SectionDataEmitter CreateDataSection(
            ObjectNodeSection section,
            int sectionIndex,
            Stream sectionStream)
        {
            return new WasmDataSegmentEmitter(
                sectionStream,
                new Utf8String(section.Name),
                sectionIndex);
        }

        protected internal override void UpdateSectionAlignment(int sectionIndex, int alignment)
        {
            if (_sections[sectionIndex] is WasmDataSegmentEmitter dataSegment)
            {
                dataSegment.UpdateAlignment(alignment);
            }
        }
        private protected override void WriteGlobalSection()
        {
        }

        private protected override void WriteImports()
        {
            WriteImport(new WasmImport("env", WasmWellKnownGlobalSymbolNode.StackPointerName, import: new WasmGlobalImportType(WasmValueType.I32, WasmMutabilityType.Mut)));
            WriteImport(new WasmImport("env", WasmWellKnownGlobalSymbolNode.AsyncContinuationName, import: new WasmGlobalImportType(WasmValueType.I32, WasmMutabilityType.Mut)));
            WriteImport(new WasmImport("env", "__indirect_function_table", new WasmTableImportType()));
            WriteImport(new WasmImport("env", "memory", new WasmMemoryImportType(WasmLimitType.HasMin, 0)));
            int tagType = RegisterSignature(new WasmFuncType(new([]), new([])));
            WriteImport(new WasmImport("env", RestoreContextTagName.ToString(), new WasmTagImportType(tagType)));
        }

        private protected override void WriteExports()
        {
            WasmSection codeSection = GetOrCreateSection<WasmSection>(ObjectNodeSection.WasmCodeSection, out _);
            int codeSectionIndex = codeSection.SectionIndex;
            foreach (var symbol in _definedSymbols)
            {
                // We only export methods for now
                if (!symbol.Value.Global || symbol.Value.SectionIndex != codeSectionIndex)
                    continue;

                WasmSymbol methodEntry = _wasmSymbolManager.GetSymbol(symbol.Key);
                Debug.Assert(methodEntry.IndexSpace == WasmIndexSpace.Function);
                WriteFunctionExport(symbol.Key.ToString(), methodEntry.Index);
            }
        }

        private protected override void WriteElements()
        {
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
