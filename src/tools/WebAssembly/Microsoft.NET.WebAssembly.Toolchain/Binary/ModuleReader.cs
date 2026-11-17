// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Collections.Generic;
using System.Runtime.InteropServices;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;

/// <summary>
/// Parses a WebAssembly binary module (.wasm) into a <see cref="WasmModule"/>.
/// Implements the binary format described in the WebAssembly specification §5.5.
/// </summary>
public static partial class ModuleReader
{
    private static readonly byte[] Magic = [0x00, 0x61, 0x73, 0x6D];
    private static readonly byte[] Version = [0x01, 0x00, 0x00, 0x00];

    // Required section ordering (per spec §5.5.2).  Custom sections (id=0) may appear
    // anywhere.  Section 12 (data count) must appear between element (9) and code (10).
    // Map: section_id → required position in the ordered sequence.
    private static readonly int[] SectionOrder =
    //   id:  0   1   2   3   4   5   6   7   8   9  10  11  12  13
              [-1,  0,  2,  4,  6,  8, 10, 12, 14, 16, 20, 22, 18,  9];

    // Partial method: implemented in Execution/ModuleReader.Streaming.cs to wire
    // FunctionBodyDecoder.ValidateAndDecode into PopulateCodeEntry.
    static partial void DecodeBody(
        byte[] body,
        WasmModule decodeModule,
        WasmFunction function,
        bool hasDataCountSection,
        WasmFeatures features);

    // ── Typed entry points (when caller knows the format) ─────────────────────

    /// <summary>Parse and validate a core module. Throws <see cref="WasmComponentException"/> if component header detected.</summary>
    public static WasmModule ReadModule(Stream stream, ModuleReaderOptions? options = null)
    {
        options ??= ModuleReaderOptions.Default;
        var module = ParseCoreModule(stream, options);
        WasmModuleValidator.ValidateAndThrow(module, options.Features);
        return module;
    }

    /// <summary>
    /// Parses a WebAssembly module for analysis or transformation without applying runtime-module
    /// semantic validation.
    /// </summary>
    /// <remarks>
    /// The returned analytical model is not a successful runtime load. Passing it to
    /// <c>WasmRuntime.Instantiate</c> applies normal validation and rejects unresolved or otherwise
    /// invalid intermediate state. Use <see cref="ReadModule"/> for runtime-loadable modules.
    /// </remarks>
    public static WasmModule ParseModuleForAnalysis(Stream stream, ModuleReaderOptions? options = null)
        => ParseCoreModule(stream, options);

    /// <summary>Unchecked parser stage used by validation and non-runtime analytical tooling.</summary>
    internal static WasmModule ParseCoreModule(Stream stream, ModuleReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new WasmBinaryReader(stream);
        ValidateHeader(reader);
        return ReadCoreAfterHeader(reader, streamSections: true, options: options ?? ModuleReaderOptions.Default);
    }

    /// <summary>Read and validate a core module.</summary>
    public static WasmModule ReadModuleAndValidate(Stream stream, ModuleReaderOptions? options = null)
        => ReadModule(stream, options);

    // ── Internal: in-memory buffer for ArArchiveReader / ComponentReader ───────

    /// <summary>Parse a core module from an in-memory buffer. Internal only.</summary>
    internal static WasmModule ParseCoreModule(ReadOnlyMemory<byte> bytes, ModuleReaderOptions? options = null)
    {
        using var stream = MemoryMarshal.TryGetArray(bytes, out ArraySegment<byte> segment)
            ? new MemoryStream(segment.Array!, segment.Offset, segment.Count, writable: false, publiclyVisible: true)
            : new MemoryStream(bytes.ToArray(), writable: false);
        return ReadCore(new WasmBinaryReader(stream), options: options);
    }

    private static WasmModule ReadCore(WasmBinaryReader reader, bool streamSections = false, ModuleReaderOptions? options = null)
    {
        ValidateHeader(reader);
        return ReadCoreAfterHeader(reader, streamSections, options);
    }

    private static WasmModule ReadCoreAfterHeader(WasmBinaryReader reader, bool streamSections = false, ModuleReaderOptions? options = null)
    {
        var types = new List<FuncType>();
        var typeDefinitions = new List<TypeDefinition>();
        var imports = new List<Import>();
        var typeIndices = new List<int>();
        var tables = new List<TableType>();
        var memories = new List<MemoryType>();
        var globals = new List<WasmGlobal>();
        var exports = new OrderedDictionary<string, Export>();
        var exportEntries = new List<Export>();
        int? startFunction = null;
        var elements = new List<Element>();
        var functions = new List<WasmFunction>();
        var dataSegments = new List<Data>();
        var customSections = new List<CustomSection>();
        LinkingSection? linkingSection = null;
        ProducersSection? producersSection = null;
        TargetFeaturesSection? targetFeaturesSection = null;
        var relocationSections = new List<RelocationSection>();
        uint? dataCount = null;
        var tags = new List<Tag>();
        // decodeModule shares the section lists by reference so that body decoding (which runs
        // when the code section is reached, after imports/memories are parsed) can resolve
        // memory64 memarg offsets via Imports/Memories.
        var decodeModule = new WasmModule
        {
            Types = types,
            TypeDefinitions = typeDefinitions,
            Functions = functions,
            Imports = imports,
            Memories = memories,
        };
        var features = (options ?? ModuleReaderOptions.Default).Features;

        int lastSectionPosition = -1; // tracks ordering of non-custom sections
        bool codeSectionSeen = false;

        int sectionIndex = -1;
        while (reader.HasMore)
        {
            byte sectionId = reader.ReadByte();
            uint sectionSize = reader.ReadU32Leb128();
            long sectionStart = reader.Position;
            ValidateSectionOrder(sectionId, ref lastSectionPosition);
            sectionIndex++;
            int customSectionCount = customSections.Count;

            if (streamSections)
            {
                switch (sectionId)
                {
                    case 0:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadCustomSection(
                            sectionReader,
                            sectionSize,
                            customSections,
                            ref linkingSection,
                            ref producersSection,
                            ref targetFeaturesSection,
                            relocationSections,
                            codeSectionSeen);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 1:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadTypeSection(sectionReader, types, typeDefinitions);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 2:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadImportSection(sectionReader, imports);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 3:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadFunctionSection(sectionReader, typeIndices);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 4:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadTableSection(sectionReader, tables);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 5:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadMemorySection(sectionReader, memories);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 6:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadGlobalSection(sectionReader, globals);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 7:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadExportSection(sectionReader, exports, exportEntries);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 8:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        startFunction = (int)sectionReader.ReadU32Leb128();
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 9:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadElementSection(sectionReader, elements);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 10:
                        ReadCodeSectionStreaming(reader, sectionSize, functions, typeIndices, decodeModule, dataCount.HasValue, features);
                        codeSectionSeen = true;
                        break;
                    case 11:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadDataSection(sectionReader, dataSegments);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 12:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        dataCount = sectionReader.ReadU32Leb128();
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 13:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadTagSection(sectionReader, tags);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                }
            }
            else
            {
                switch (sectionId)
                {
                    case 0:
                        ReadCustomSection(
                            reader,
                            sectionSize,
                            customSections,
                            ref linkingSection,
                            ref producersSection,
                            ref targetFeaturesSection,
                            relocationSections,
                            codeSectionSeen);
                        break;
                    case 1:
                        ReadTypeSection(reader, types, typeDefinitions);
                        break;
                    case 2:
                        ReadImportSection(reader, imports);
                        break;
                    case 3:
                        ReadFunctionSection(reader, typeIndices);
                        break;
                    case 4:
                        ReadTableSection(reader, tables);
                        break;
                    case 5:
                        ReadMemorySection(reader, memories);
                        break;
                    case 6:
                        ReadGlobalSection(reader, globals);
                        break;
                    case 7:
                        ReadExportSection(reader, exports, exportEntries);
                        break;
                    case 8:
                        startFunction = (int)reader.ReadU32Leb128();
                        break;
                    case 9:
                        ReadElementSection(reader, elements);
                        break;
                    case 10:
                        ReadCodeSection(reader, functions, typeIndices, decodeModule, dataCount.HasValue, features);
                        codeSectionSeen = true;
                        break;
                    case 11:
                        ReadDataSection(reader, dataSegments, payloadBase: reader.Position);
                        break;
                    case 12:
                        dataCount = reader.ReadU32Leb128();
                        break;
                    case 13:
                        ReadTagSection(reader, tags);
                        break;
                }

                long expectedEnd = sectionStart + sectionSize;
                if (reader.Position != expectedEnd)
                    throw new WasmFormatException(
                        $"Section {sectionId} size mismatch: expected to end at {expectedEnd} but reader is at {reader.Position}");
            }

            if (customSections.Count > customSectionCount)
                customSections[^1] = customSections[^1] with { SectionIndex = sectionIndex };
        }

        // Validate data count section against actual data segments.
        ValidateStreamingRead(dataCount, dataSegments.Count, typeIndices.Count, functions.Count);

        // Enrich FuncTypes whose results include defined heap types with the correct null tag for
        // each such result slot.  Must run after the full type section is parsed so that every
        // DefinedHeapType reference can be resolved through typeDefinitions.
        ResolveResultNullTags(types, typeDefinitions);

        var module = CreateModule(
            types, typeDefinitions, imports, functions, tables, memories, globals, tags, exports, exportEntries,
            startFunction, elements, dataSegments, customSections, linkingSection, producersSection,
            targetFeaturesSection, relocationSections, dataCount.HasValue);
        module.EnabledFeatures = (options ?? ModuleReaderOptions.Default).Features;

        BranchHintMetadataReader.Apply(
            module,
            (options ?? ModuleReaderOptions.Default).MetadataValidation);

        if (options is not null)
            ParseDebugSections(module, options);

        return module;
    }

    private static WasmModule CreateModule(
        List<FuncType> types,
        List<TypeDefinition> typeDefinitions,
        List<Import> imports,
        List<WasmFunction> functions,
        List<TableType> tables,
        List<MemoryType> memories,
        List<WasmGlobal> globals,
        List<Tag> tags,
        OrderedDictionary<string, Export> exports,
        List<Export> exportEntries,
        int? startFunction,
        List<Element> elements,
        List<Data> dataSegments,
        List<CustomSection> customSections,
        LinkingSection? linkingSection,
        ProducersSection? producersSection,
        TargetFeaturesSection? targetFeaturesSection,
        List<RelocationSection> relocationSections,
        bool hasDataCountSection)
    {
        return new WasmModule
        {
            Types = types,
            TypeDefinitions = typeDefinitions,
            Imports = imports,
            Functions = functions,
            Tables = tables,
            Memories = memories,
            Globals = globals,
            Tags = tags,
            Exports = exports,
            ExportEntries = exportEntries,
            StartFunction = startFunction,
            Elements = elements,
            DataSegments = dataSegments,
            CustomSections = customSections,
            LinkingSection = linkingSection,
            ProducersSection = producersSection,
            TargetFeaturesSection = targetFeaturesSection,
            RelocationSections = relocationSections,
            HasDataCountSection = hasDataCountSection,
        };
    }

    private static void ParseDebugSections(WasmModule module, ModuleReaderOptions options)
    {
        if (!options.Features.HasFlag(WasmFeatures.DebugInfo)) return;
        var debugLine    = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_line");
        var debugLineStr = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_line_str");
        var debugStr     = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_str");
        if (debugLine is not null)
            module.DebugInfo = DwarfLineReader.Parse(
                debugLine.Content,
                debugLineStr?.Content,
                debugStr?.Content);
    }

    private static void ValidateSectionOrder(byte sectionId, ref int lastSectionPosition)
    {
        if (sectionId > 13)
            throw new WasmFormatException($"Malformed section id: {sectionId}");

        if (sectionId == 0)
            return;

        int pos = SectionOrder[sectionId];
        if (pos <= lastSectionPosition)
        {
            throw new WasmFormatException(
                $"Unexpected content after last section: section {sectionId} out of order or duplicated");
        }

        lastSectionPosition = pos;
    }

    /// <summary>
    /// Enriches each <see cref="FuncType"/> in <paramref name="types"/> whose results include a
    /// <c>DefinedHeapType</c> with <see cref="FuncType.ResultNullTags"/>: the correct
    /// <see cref="ValType"/> that a <em>null</em> of each result slot should carry at the public
    /// boundary.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ValueType.ToStorageValType"/> maps every <c>DefinedHeapType</c> to
    /// <see cref="ValType.FuncRef"/> — it has no type table, so it cannot distinguish a defined
    /// continuation type from a defined function type.  The compact result type alone is therefore
    /// ambiguous for nulls: a live value already gets the right tag from payload-based
    /// classification in <c>MaterialiseFuncRefSlot</c>, but a null carries no payload.  This method
    /// resolves the ambiguity once, when the full type table is known, so the result-materialisation
    /// loop can pass the pre-resolved tag for each slot.</para>
    /// </remarks>
    private static void ResolveResultNullTags(List<FuncType> types, List<TypeDefinition> typeDefinitions)
    {
        int typeCount = Math.Min(types.Count, typeDefinitions.Count);
        for (int typeIdx = 0; typeIdx < typeCount; typeIdx++)
        {
            if (typeDefinitions[typeIdx].CompositeType is not CompositeType.Function funcComp)
                continue;

            bool hasDefinedType = false;
            foreach (var r in funcComp.Results)
            {
                if (r is ValueType.Reference { HeapType: DefinedHeapType })
                { hasDefinedType = true; break; }
            }
            if (!hasDefinedType) continue;

            var tags = new ValType[funcComp.Results.Count];
            for (int ri = 0; ri < funcComp.Results.Count; ri++)
            {
                // This runs before semantic validation, so a result may name a type index that
                // does not exist. That is a module defect the validator reports; enriching the
                // slot is meaningless either way, so the compact type stands.
                tags[ri] = funcComp.Results[ri] is ValueType.Reference { HeapType: DefinedHeapType { TypeIndex: var idx } }
                        && AbstractHeapTypeCodes.TryStorageTagForDefinedType(typeDefinitions, idx, out var definedTag)
                    ? definedTag
                    : funcComp.Results[ri].ToStorageValType();
            }
            types[typeIdx] = types[typeIdx] with { ResultNullTags = tags };
        }
    }

    private static void ValidateStreamingRead(uint? dataCount, int dataSegmentCount, int typeIndexCount, int functionCount)
    {
        if (dataCount.HasValue && dataCount.Value != (uint)dataSegmentCount)
        {
            throw new WasmFormatException(
                $"Data count section value ({dataCount.Value}) does not match data section count ({dataSegmentCount})");
        }

        if (typeIndexCount != functionCount)
        {
            throw new WasmFormatException(
                $"Function/code section count mismatch: {typeIndexCount} type indices vs {functionCount} code entries");
        }
    }

    private static void ReadNonCodeSection(
        WasmBinaryReader reader,
        byte sectionId,
        uint sectionSize,
        long sectionStart,
        List<FuncType> types,
        List<TypeDefinition> typeDefinitions,
        List<Import> imports,
        List<int> typeIndices,
        List<TableType> tables,
        List<MemoryType> memories,
        List<WasmGlobal> globals,
        OrderedDictionary<string, Export> exports,
        List<Export> exportEntries,
        ref int? startFunction,
        List<Element> elements,
        List<WasmFunction> functions,
        List<Data> dataSegments,
        List<CustomSection> customSections,
        ref LinkingSection? linkingSection,
        ref ProducersSection? producersSection,
        ref TargetFeaturesSection? targetFeaturesSection,
        List<RelocationSection> relocationSections,
        ref uint? dataCount,
        List<Tag> tags,
        bool createFunctionPlaceholders,
        bool appearedAfterCode)
    {
        switch (sectionId)
        {
            case 0:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadCustomSection(
                    sectionReader,
                    sectionSize,
                    customSections,
                    ref linkingSection,
                    ref producersSection,
                    ref targetFeaturesSection,
                    relocationSections,
                    appearedAfterCode);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 1:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadTypeSection(sectionReader, types, typeDefinitions);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 2:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadImportSection(sectionReader, imports);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 3:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                if (createFunctionPlaceholders)
                    ReadFunctionSectionWithPlaceholders(sectionReader, typeIndices, functions);
                else
                    ReadFunctionSection(sectionReader, typeIndices);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 4:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadTableSection(sectionReader, tables);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 5:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadMemorySection(sectionReader, memories);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 6:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadGlobalSection(sectionReader, globals);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 7:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadExportSection(sectionReader, exports, exportEntries);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 8:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                startFunction = (int)sectionReader.ReadU32Leb128();
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 9:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadElementSection(sectionReader, elements);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 10:
                throw new InvalidOperationException("Code section must be handled by the streaming pipeline");
            case 11:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadDataSection(sectionReader, dataSegments);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 12:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                dataCount = sectionReader.ReadU32Leb128();
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 13:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadTagSection(sectionReader, tags);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            default:
                throw new WasmFormatException($"Malformed section id: {sectionId}");
        }

        if (reader.Position != sectionStart + sectionSize)
        {
            throw new WasmFormatException(
                $"Section {sectionId} size mismatch: expected to end at {sectionStart + sectionSize} but reader is at {reader.Position}");
        }
    }

    private static WasmBinaryReader ReadSectionPayload(WasmBinaryReader reader, byte sectionId, uint sectionSize)
    {
        if (sectionSize > int.MaxValue)
            throw new WasmFormatException($"Section {sectionId} payload is too large ({sectionSize} bytes)");

        return new WasmBinaryReader(new MemoryStream(reader.ReadBytes((int)sectionSize), writable: false));
    }

    private static void ValidateSectionConsumption(byte sectionId, uint sectionSize, WasmBinaryReader reader)
    {
        if (reader.Position != sectionSize)
        {
            throw new WasmFormatException(
                $"Section {sectionId} size mismatch: declared {sectionSize} bytes but consumed {reader.Position}");
        }
    }

    private static void ValidateHeader(WasmBinaryReader reader)
    {
        Span<byte> magic = stackalloc byte[4];
        reader.ReadBytes(magic);
        if (!magic.SequenceEqual(Magic))
            throw new WasmFormatException("Invalid WebAssembly magic number");

        Span<byte> version = stackalloc byte[4];
        reader.ReadBytes(version);
        // Component Model version: 0x0D 0x00 0x01 0x00
        if (version[0] == 0x0D && version[1] == 0x00 && version[2] == 0x01 && version[3] == 0x00)
            throw new WasmComponentException();
        if (!version.SequenceEqual(Version))
            throw new WasmFormatException($"Unsupported WebAssembly version");
    }

    // Placeholder for non-function type entries (GC struct/array/continuation types).
    // Stored at the correct index so that function type indices remain valid.
    private static readonly FuncType NonFuncTypePlaceholder = new([], []);

    private static void ReadTypeSection(
        WasmBinaryReader reader,
        List<FuncType> types,
        List<TypeDefinition> typeDefinitions)
    {
        int recGroupIndex = typeDefinitions.Count == 0 ? 0 : typeDefinitions[^1].RecGroup.Index + 1;
        foreach (var _ in reader.ReadVectorHeader())
        {
            byte tag = reader.PeekByte();
            if (tag == 0x4E) // rec group — wraps multiple sub/composite types
            {
                reader.ReadByte(); // consume 0x4E
                uint count = reader.ReadU32Leb128();
                var group = new RecGroupIdentity(recGroupIndex++, types.Count, checked((int)count));
                for (uint i = 0; i < count; i++)
                    AddType(types, typeDefinitions, ReadSubtypeOrComposite(reader, types, group));
            }
            else
            {
                var group = new RecGroupIdentity(recGroupIndex++, types.Count, 1);
                AddType(types, typeDefinitions, ReadSubtypeOrComposite(reader, types, group));
            }
        }

        static void AddType(
            List<FuncType> types,
            List<TypeDefinition> typeDefinitions,
            TypeParseResult result)
        {
            var type = result.CompactType;
            if (type.LinkKey.StartsWith("sub[", StringComparison.Ordinal))
                type = type with { LinkKey = $"{type.LinkKey}@type{types.Count}" };
            types.Add(type);
            typeDefinitions.Add(result.ToDefinition(types.Count - 1));
        }
    }

    /// <summary>
    /// Reads a subtype wrapper (0x50 open, 0x4F final) or a bare composite type.
    /// Returns a FuncType for function types, or a placeholder for struct/array types.
    /// </summary>
    private static TypeParseResult ReadSubtypeOrComposite(
        WasmBinaryReader reader,
        List<FuncType> existingTypes,
        RecGroupIdentity recGroup)
    {
        byte tag = reader.ReadByte();
        string? subtypePrefix = null;
        bool isFinal = true;
        int[] supertypes = [];

        // Sub-type wrappers: 0x50 = sub (open), 0x4F = sub final
        if (tag is 0x50 or 0x4F)
        {
            isFinal = tag == 0x4F;
            uint supertypeCount = reader.ReadU32Leb128();
            supertypes = new int[supertypeCount];
            for (uint i = 0; i < supertypeCount; i++)
                supertypes[i] = checked((int)reader.ReadU32Leb128());
            subtypePrefix = $"sub[{(isFinal ? "final" : "open")}:{string.Join(",", supertypes)}]:";
            tag = reader.ReadByte(); // inner composite type tag
        }

        var result = tag switch
        {
            0x60 => ReadFuncType(reader),
            0x5D => ReadContType(reader, existingTypes),
            0x5F => ReadStructType(reader),
            0x5E => ReadArrayType(reader),
            _ => throw new WasmFormatException($"Expected composite type tag (0x60/0x5D/0x5F/0x5E), got 0x{tag:X2}"),
        };
        var compactType = result.CompactType;
        if (subtypePrefix is not null)
            compactType = compactType with { LinkKey = subtypePrefix + compactType.LinkKey };

        return new TypeParseResult(compactType, result.CompositeType, isFinal, supertypes, recGroup);
    }

    private static TypeParseResult ReadFuncType(WasmBinaryReader reader)
    {
        var parameters = reader.ReadVector(() => ReadValTypeInfo(reader));
        var results = reader.ReadVector(() => ReadValTypeInfo(reader));
        var compact = new FuncType(parameters.Select(p => p.Type).ToArray(), results.Select(r => r.Type).ToArray())
        {
            LinkKey = $"({string.Join(",", parameters.Select(p => p.Key))})->({string.Join(",", results.Select(r => r.Key))})",
        };
        var composite = new CompositeType.Function(
            parameters.Select(p => p.ExactType).ToArray(),
            results.Select(r => r.ExactType).ToArray());
        return new TypeParseResult(compact, composite);
    }

    private static TypeParseResult ReadContType(WasmBinaryReader reader, List<FuncType> existingTypes)
    {
        int funcTypeIndex = checked((int)reader.ReadU32Leb128());
        if (funcTypeIndex >= (uint)existingTypes.Count)
            throw new WasmFormatException($"Continuation type refers to invalid function type index {funcTypeIndex}");
        return new TypeParseResult(
            existingTypes[funcTypeIndex],
            new CompositeType.Continuation(funcTypeIndex));
    }

    private static TypeParseResult ReadStructType(WasmBinaryReader reader)
    {
        uint fieldCount = reader.ReadU32Leb128();
        var fields = new FieldType[fieldCount];
        for (uint i = 0; i < fieldCount; i++)
            fields[i] = ReadFieldType(reader);
        return new TypeParseResult(NonFuncTypePlaceholder, new CompositeType.Struct(fields));
    }

    private static TypeParseResult ReadArrayType(WasmBinaryReader reader)
    {
        var field = ReadFieldType(reader);
        return new TypeParseResult(NonFuncTypePlaceholder, new CompositeType.Array(field));
    }

    /// <summary>Reads a GC field type: storage type + mutability byte.</summary>
    private static FieldType ReadFieldType(WasmBinaryReader reader)
    {
        var storageType = ReadStorageType(reader);
        byte encodedMutability = reader.ReadByte();
        var mutability = encodedMutability switch
        {
            0x00 => Mutability.Const,
            0x01 => Mutability.Var,
            _ => throw new WasmFormatException($"Invalid field mutability byte: 0x{encodedMutability:X2}"),
        };
        return new FieldType(storageType, mutability);
    }

    /// <summary>Reads a GC storage type: valtype | i8 (0x78) | i16 (0x77).</summary>
    private static StorageType ReadStorageType(WasmBinaryReader reader)
    {
        byte b = reader.PeekByte();
        if (b is 0x78 or 0x77) // packed types: i8, i16
        {
            reader.ReadByte();
            return new StorageType.Packed(b == 0x78 ? PackedStorageType.I8 : PackedStorageType.I16);
        }
        return new StorageType.Value(ReadValTypeInfo(reader).ExactType);
    }

    /// <summary>
    /// Reads a value type, handling both single-byte types (i32, i64, funcref, etc.)
    /// and multi-byte GC reference types (ref null $type, ref $type, abstract heap types).
    /// </summary>
    private static ValType ReadValType(WasmBinaryReader reader)
        => ReadValTypeInfo(reader).Type;

    private static Microsoft.NET.WebAssembly.Types.ValueType ReadExactValType(WasmBinaryReader reader)
        => ReadValTypeInfo(reader).ExactType;

    private readonly record struct ValTypeInfo(ValType Type, string Key, Microsoft.NET.WebAssembly.Types.ValueType ExactType);

    private static ValTypeInfo ReadValTypeInfo(WasmBinaryReader reader)
    {
        byte b = reader.ReadByte();
        return b switch
        {
            // Standard numeric types
            0x7F => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.I32)),
            0x7E => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.I64)),
            0x7D => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.F32)),
            0x7C => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.F64)),
            0x7B => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Vector(VectorType.V128)),
            // Standard reference types
            0x70 => RefInfo(Nullability.Nullable, HeapTypeKind.Func),
            0x6F => RefInfo(Nullability.Nullable, HeapTypeKind.Extern),
            0x69 => RefInfo(Nullability.Nullable, HeapTypeKind.Exn),
            0x68 or 0x65 => RefInfo(Nullability.Nullable, HeapTypeKind.Cont),
            // GC abstract reference types in nullable abbreviation form.
            0x6E => RefInfo(Nullability.Nullable, HeapTypeKind.Any),
            0x6D => RefInfo(Nullability.Nullable, HeapTypeKind.Eq),
            0x6C => RefInfo(Nullability.Nullable, HeapTypeKind.I31),
            0x6B => RefInfo(Nullability.Nullable, HeapTypeKind.Struct),
            0x6A => RefInfo(Nullability.Nullable, HeapTypeKind.Array),
            0x71 => RefInfo(Nullability.Nullable, HeapTypeKind.None),
            0x73 => RefInfo(Nullability.Nullable, HeapTypeKind.NoFunc),
            0x72 => RefInfo(Nullability.Nullable, HeapTypeKind.NoExtern),
            0x74 => RefInfo(Nullability.Nullable, HeapTypeKind.NoExn),
            0x75 => RefInfo(Nullability.Nullable, HeapTypeKind.NoCont),
            // Typed references: (ref null ht) / (ref ht) — consume heap type
            0x63 or 0x64 => ReadRefWithHeapType(reader, b),
            _ => throw new WasmFormatException($"Unknown value type: 0x{b:X2}"),
        };
    }

    /// <summary>
    /// Reads the heap type following a 0x63 (ref null) or 0x64 (ref) prefix.
    /// Heap types are encoded as s33 LEB128: positive = type index, negative = abstract heap type.
    /// </summary>
    private static ValTypeInfo ReadRefWithHeapType(WasmBinaryReader reader, byte refByte)
    {
        long heapType = reader.ReadS33Leb128();
        var nullability = refByte == 0x63 ? Nullability.Nullable : Nullability.NonNullable;
        if (heapType >= 0)
        {
            return ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Reference(nullability, new DefinedHeapType(checked((int)heapType))));
        }

        byte code = (byte)(heapType & 0x7F);
        return code switch
        {
            0x70 => RefInfo(nullability, HeapTypeKind.Func),
            0x6F => RefInfo(nullability, HeapTypeKind.Extern),
            0x6E => RefInfo(nullability, HeapTypeKind.Any),
            0x6D => RefInfo(nullability, HeapTypeKind.Eq),
            0x6C => RefInfo(nullability, HeapTypeKind.I31),
            0x6B => RefInfo(nullability, HeapTypeKind.Struct),
            0x6A => RefInfo(nullability, HeapTypeKind.Array),
            0x71 => RefInfo(nullability, HeapTypeKind.None),
            0x73 => RefInfo(nullability, HeapTypeKind.NoFunc),
            0x72 => RefInfo(nullability, HeapTypeKind.NoExtern),
            0x69 => RefInfo(nullability, HeapTypeKind.Exn),
            0x74 => RefInfo(nullability, HeapTypeKind.NoExn),
            0x68 or 0x65 => RefInfo(nullability, HeapTypeKind.Cont),
            0x75 => RefInfo(nullability, HeapTypeKind.NoCont),
            _ => throw new WasmFormatException($"Unknown heap type: 0x{code:X2}"),
        };
    }

    private static ValTypeInfo RefInfo(Nullability nullability, HeapTypeKind kind)
        => ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Reference(nullability, new AbstractHeapType(kind)));

    private static ValTypeInfo ValInfo(Microsoft.NET.WebAssembly.Types.ValueType exactType)
        => new(exactType.ToStorageValType(), exactType.Key, exactType);

    private sealed record TypeParseResult(
        FuncType CompactType,
        CompositeType CompositeType,
        bool IsFinal = true,
        IReadOnlyList<int>? SuperTypeIndices = null,
        RecGroupIdentity? RecGroup = null)
    {
        public TypeDefinition ToDefinition(int index) => new(
            index,
            IsFinal,
            SuperTypeIndices ?? [],
            CompositeType,
            RecGroup ?? new RecGroupIdentity(index, index, 1));
    }

    private static void ReadImportSection(WasmBinaryReader reader, List<Import> imports)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            string moduleName = reader.ReadName();
            string name = reader.ReadName();
            byte kind = reader.ReadByte();
            ImportDescriptor descriptor = kind switch
            {
                0x00 => new ImportDescriptor.Func((int)reader.ReadU32Leb128()),
                0x01 => new ImportDescriptor.Table(ReadTableImportType(reader)),
                0x02 => new ImportDescriptor.Memory(ReadMemoryType(reader)),
                0x03 => new ImportDescriptor.Global(ReadGlobalType(reader)),
                0x04 => new ImportDescriptor.Tag(ReadTagType(reader)),
                _ => throw new WasmFormatException($"Unknown import descriptor kind: 0x{kind:X2}"),
            };
            imports.Add(new Import(moduleName, name, descriptor));
        }
    }

    private static void ReadFunctionSection(WasmBinaryReader reader, List<int> typeIndices)
    {
        foreach (var _ in reader.ReadVectorHeader())
            typeIndices.Add((int)reader.ReadU32Leb128());
    }

    private static void ReadFunctionSectionWithPlaceholders(
        WasmBinaryReader reader,
        List<int> typeIndices,
        List<WasmFunction> functions)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            int typeIndex = (int)reader.ReadU32Leb128();
            typeIndices.Add(typeIndex);
            functions.Add(new WasmFunction
            {
                TypeIndex = typeIndex,
                Locals = [],
                Body = [],
            });
        }
    }

    private static void ReadTableSection(WasmBinaryReader reader, List<TableType> tables)
    {
        foreach (var _ in reader.ReadVectorHeader())
            tables.Add(ReadTableDefinition(reader));
    }

    private static void ReadMemorySection(WasmBinaryReader reader, List<MemoryType> memories)
    {
        foreach (var _ in reader.ReadVectorHeader())
            memories.Add(ReadMemoryType(reader));
    }

    private static void ReadGlobalSection(WasmBinaryReader reader, List<WasmGlobal> globals)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            var type = ReadGlobalType(reader);
            byte[] initExpr = ReadInitExpr(reader);
            globals.Add(new WasmGlobal { Type = type, InitExpr = initExpr });
        }
    }

    private static void ReadTagSection(WasmBinaryReader reader, List<Tag> tags)
    {
        foreach (var _ in reader.ReadVectorHeader())
            tags.Add(new Tag(ReadTagType(reader)));
    }

    private static int ReadTagType(WasmBinaryReader reader)
    {
        byte attribute = reader.ReadByte();
        if (attribute != 0x00)
            throw new WasmFormatException($"Invalid tag attribute: 0x{attribute:X2}");
        return (int)reader.ReadU32Leb128();
    }

    private static void ReadExportSection(
        WasmBinaryReader reader,
        OrderedDictionary<string, Export> exports,
        List<Export> exportEntries)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            string name = reader.ReadName();
            byte kind = reader.ReadByte();
            ExportDescriptor descriptor = kind switch
            {
                0x00 => new ExportDescriptor.Func((int)reader.ReadU32Leb128()),
                0x01 => new ExportDescriptor.Table((int)reader.ReadU32Leb128()),
                0x02 => new ExportDescriptor.Memory((int)reader.ReadU32Leb128()),
                0x03 => new ExportDescriptor.Global((int)reader.ReadU32Leb128()),
                0x04 => new ExportDescriptor.Tag((int)reader.ReadU32Leb128()),
                _ => throw new WasmFormatException($"Unknown export descriptor kind: 0x{kind:X2}"),
            };
            var export = new Export(name, descriptor);
            exportEntries.Add(export);
            exports.TryAdd(name, export);
        }
    }

    /// <summary>
    /// Decodes the element section (§5.5.12).
    ///
    /// The eight forms do not all agree on element type, and the disagreement is load-bearing.
    /// Per the current core spec — since function-references merged — the four <c>vec(funcidx)</c>
    /// forms (flags 0–3) carry the <b>non-nullable</b> type <c>ref func</c>: flag 0 states it
    /// literally, and flags 1–3 derive it from <c>elemkind ::= 0x00 ⇒ ref func</c>. Only flag 4,
    /// the implicit-table <c>vec(expr)</c> form, is <b>nullable</b> <c>funcref</c>; flags 5–7 read
    /// an explicit reftype.
    ///
    /// The asymmetry between flag 0 and flag 4 is real, not a transcription slip, and is what
    /// makes a `(ref func)` table initialisable by `(elem (i32.const 3) $g)` but not by
    /// `(elem (i32.const 3) funcref (ref.func $g))`. Confirmed against wasm-tools 1.253.0, which
    /// accepts the former and rejects the latter with
    /// "invalid element type `funcref` for table type `(ref func)`". See #411.
    /// </summary>
    private static void ReadElementSection(WasmBinaryReader reader, List<Element> elements)
    {
        // Flags 0-3: `ref func` (non-nullable). Flag 4: `funcref` (nullable). Not interchangeable.
        var refFuncType = new Microsoft.NET.WebAssembly.Types.ValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.Func));
        var funcrefType = new Microsoft.NET.WebAssembly.Types.ValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));
        foreach (var _ in reader.ReadVectorHeader())
        {
            uint flags = reader.ReadU32Leb128();
            switch (flags)
            {
                case 0:
                {
                    // Active, implicit tableidx=0, `ref func`, vec<funcidx>
                    byte[] offsetExpr = ReadInitExpr(reader);
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Active,
                        TableIndex = 0,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = new List<int>(funcIndices),
                        ElementType = refFuncType,
                    });
                    break;
                }
                case 1:
                {
                    // Passive, elemkind byte, vec<funcidx> — no table/offset
                    ReadElemKind(reader);
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Passive,
                        TableIndex = -1, // passive: no target table
                        OffsetExpr = [],
                        FunctionIndices = new List<int>(funcIndices),
                        ElementType = refFuncType,
                    });
                    break;
                }
                case 2:
                {
                    // Active, explicit tableidx, offset, elemkind byte, vec<funcidx>
                    int tableIndex = (int)reader.ReadU32Leb128();
                    byte[] offsetExpr = ReadInitExpr(reader);
                    ReadElemKind(reader);
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Active,
                        TableIndex = tableIndex,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = new List<int>(funcIndices),
                        HasExplicitTableIndex = true,
                        ElementType = refFuncType,
                    });
                    break;
                }
                case 3:
                {
                    // Declarative, elemkind byte, vec<funcidx>
                    ReadElemKind(reader);
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element { Mode = ElementMode.Declarative, TableIndex = -1, OffsetExpr = [], FunctionIndices = [], DeclaredFunctionIndices = new List<int>(funcIndices), ElementType = refFuncType });
                    break;
                }
                case 4:
                {
                    // Active, implicit tableidx=0, `funcref` (nullable — unlike flag 0), vec<expr>
                    byte[] offsetExpr = ReadInitExpr(reader);
                    var expressions = reader.ReadVector(() => ReadInitExpr(reader)).ToList();
                    var funcIndices = expressions.Select(ReadElemExprFunctionIndex).ToList();
                    elements.Add(new Element { Mode = ElementMode.Active, TableIndex = 0, OffsetExpr = offsetExpr, FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, ElementType = funcrefType });
                    break;
                }
                case 5:
                {
                    // Passive, reftype, vec<expr>
                    var elementType = ReadExactRefType(reader);
                    var expressions = reader.ReadVector(() => ReadInitExpr(reader)).ToList();
                    var funcIndices = expressions.Select(ReadElemExprFunctionIndex).ToList();
                    elements.Add(new Element { Mode = ElementMode.Passive, TableIndex = -1, OffsetExpr = [], FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, ElementType = elementType });
                    break;
                }
                case 6:
                {
                    // Active, explicit tableidx, offset, reftype, vec<expr>
                    int tableIndex = (int)reader.ReadU32Leb128();
                    byte[] offsetExpr = ReadInitExpr(reader);
                    var elementType = ReadExactRefType(reader);
                    var expressions = reader.ReadVector(() => ReadInitExpr(reader)).ToList();
                    var funcIndices = expressions.Select(ReadElemExprFunctionIndex).ToList();
                    elements.Add(new Element { Mode = ElementMode.Active, TableIndex = tableIndex, OffsetExpr = offsetExpr, FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, HasExplicitTableIndex = true, ElementType = elementType });
                    break;
                }
                case 7:
                {
                    // Declarative, reftype, vec<expr>
                    var elementType = ReadExactRefType(reader);
                    var expressions = reader.ReadVector(() => ReadInitExpr(reader)).ToList();
                    var funcIndices = expressions.Select(ReadElemExprFunctionIndex).ToList();
                    elements.Add(new Element { Mode = ElementMode.Declarative, TableIndex = -1, OffsetExpr = [], FunctionIndices = [], DeclaredFunctionIndices = funcIndices.Where(index => index >= 0).ToList(), ElementExpressions = expressions, UsesExpressions = true, ElementType = elementType });
                    break;
                }
                default:
                    throw new WasmFormatException($"Unsupported element segment type: {flags}");
            }
        }
    }

    private static void ReadCodeSection(
        WasmBinaryReader reader,
        List<WasmFunction> functions,
        List<int> typeIndices,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        int functionIndex = functions.Count;
        long sectionContentStart = reader.Position;
        foreach (var _ in reader.ReadVectorHeader())
        {
            if ((uint)functionIndex >= (uint)typeIndices.Count)
                throw new WasmFormatException("Function/code section count mismatch: too many code entries");

            uint funcOffset = checked((uint)(reader.Position - sectionContentStart));
            uint bodySize = reader.ReadU32Leb128();
            uint bodyStartOffset = checked((uint)(reader.Position - sectionContentStart));
            var func = ReadCodeEntry(reader, bodySize, typeIndices[functionIndex], decodeModule, hasDataCountSection, features);
            func.CodeSectionOffset = funcOffset;
            func.BodyCodeSectionOffset = bodyStartOffset + (bodySize - (uint)func.Body.Length);
            func.LocalsCodeSectionOffset = bodyStartOffset;
            functions.Add(func);
            functionIndex++;
        }
    }

    private static void ReadCodeSectionStreaming(
        WasmBinaryReader reader,
        uint sectionSize,
        List<WasmFunction> functions,
        List<int> typeIndices,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        // Streaming path: keep only a single function body buffered at any point.
        long expectedEnd = reader.Position + sectionSize;
        int functionIndex = functions.Count;
        long sectionContentStart = reader.Position;

        foreach (var _ in reader.ReadVectorHeader())
        {
            if ((uint)functionIndex >= (uint)typeIndices.Count)
                throw new WasmFormatException("Function/code section count mismatch: too many code entries");

            uint funcOffset = checked((uint)(reader.Position - sectionContentStart));
            uint bodySize = reader.ReadU32Leb128();
            uint bodyStartOffset = checked((uint)(reader.Position - sectionContentStart));
            if (bodySize > int.MaxValue)
                throw new WasmFormatException($"Function body payload is too large ({bodySize} bytes)");

            byte[] bodyBytes = reader.ReadBytes((int)bodySize);
            using var bodyStream = new MemoryStream(bodyBytes, writable: false);
            var bodyReader = new WasmBinaryReader(bodyStream);
            var func = ReadCodeEntry(bodyReader, bodySize, typeIndices[functionIndex], decodeModule, hasDataCountSection, features);
            func.CodeSectionOffset = funcOffset;
            func.BodyCodeSectionOffset = bodyStartOffset + (bodySize - (uint)func.Body.Length);
            func.LocalsCodeSectionOffset = bodyStartOffset;
            functions.Add(func);
            ValidateSectionConsumption(10, bodySize, bodyReader);
            functionIndex++;
        }

        if (reader.Position != expectedEnd)
        {
            throw new WasmFormatException(
                $"Section 10 size mismatch: expected to end at {expectedEnd} but reader is at {reader.Position}");
        }
    }

    private static WasmFunction ReadCodeEntry(
        WasmBinaryReader reader,
        uint bodySize,
        int typeIndex,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        var function = new WasmFunction
        {
            TypeIndex = typeIndex,
            Locals = [],
            Body = [],
        };
        PopulateCodeEntry(reader, bodySize, function, decodeModule, hasDataCountSection, features);
        return function;
    }

    private static void PopulateCodeEntry(
        WasmBinaryReader reader,
        uint bodySize,
        WasmFunction function,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        long bodyStart = reader.Position;

        var localDecls = reader.ReadVector(() =>
        {
            uint count = reader.ReadU32Leb128();
            var exactType = ReadExactValType(reader);
            var type = exactType.ToStorageValType();
            return (count, type, exactType);
        });

        ulong totalLocals = 0;
        foreach (var (count, _, _) in localDecls)
        {
            totalLocals += count;
            if (totalLocals > uint.MaxValue)
                throw new WasmFormatException("Too many locals: total count exceeds 2^32-1");
        }

        var locals = new List<ValType>();
        var exactLocals = new List<Microsoft.NET.WebAssembly.Types.ValueType>();
        foreach (var (count, type, exactType) in localDecls)
        {
            for (uint i = 0; i < count; i++)
            {
                locals.Add(type);
                exactLocals.Add(exactType);
            }
        }

        long remaining = (long)bodySize - (reader.Position - bodyStart);
        if (remaining < 0)
            throw new WasmFormatException("Code entry body size underflow");

        byte[] body = reader.ReadBytes(checked((int)remaining));
        function.Locals = locals;
        function.ExactLocals = exactLocals;
        function.Body = body;
        function.OriginalLocalsByteLength =
            checked((uint)(bodySize - (uint)body.Length));
        DecodeBody(body, decodeModule, function, hasDataCountSection, features);
        if (function.DecodedBody is null)
            // Naming trap: despite "Validate", this is a PARSE-stage activity. It walks the
            // instruction grammar and throws WasmFormatException (not WasmValidationException)
            // for unknown opcodes, bad LEBs, and truncated immediates. Semantic validation (§3)
            // is a separate, later stage run by WasmModuleValidator. The distinction is
            // load-bearing: ParseModuleForAnalysis survives a semantically-invalid module but
            // still rejects one whose bytes do not decode.
            ValidateCodeBody(body, hasDataCountSection, features);
    }

    private static void ReadDataSection(WasmBinaryReader reader, List<Data> dataSegments, long payloadBase = 0)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            uint flag = reader.ReadU32Leb128();
            bool isPassive = flag switch
            {
                0 or 2 => false,
                1 => true,
                _ => throw new WasmFormatException($"Unsupported data segment flag: {flag}"),
            };
            int memoryIndex = flag switch { 0 => 0, 1 => -1, _ => (int)reader.ReadU32Leb128() };
            byte[] offsetExpr = isPassive ? [] : ReadInitExpr(reader);
            int length = (int)reader.ReadU32Leb128();
            uint payloadOffset = checked((uint)(reader.Position - payloadBase));
            dataSegments.Add(new Data
            {
                IsPassive = isPassive,
                MemoryIndex = memoryIndex,
                OffsetExpr = offsetExpr,
                InitData = reader.ReadBytes(length),
                SectionPayloadOffset = payloadOffset,
            });
        }
    }

    private static void ReadCustomSection(
        WasmBinaryReader reader,
        uint sectionSize,
        List<CustomSection> customSections,
        ref LinkingSection? linkingSection,
        ref ProducersSection? producersSection,
        ref TargetFeaturesSection? targetFeaturesSection,
        List<RelocationSection> relocationSections,
        bool appearedAfterCode)
    {
        long nameStart = reader.Position;
        string name = reader.ReadName();
        int nameBytes = checked((int)(reader.Position - nameStart));
        int contentLength = checked((int)sectionSize - nameBytes);
        if (contentLength < 0)
            throw new WasmFormatException($"Custom section '{name}' has an invalid payload length.");

        byte[] content = contentLength > 0 ? reader.ReadBytes(contentLength) : [];
        customSections.Add(new CustomSection(name, content)
        {
            AppearedAfterCode = appearedAfterCode,
        });

        try
        {
            if (string.Equals(name, "linking", StringComparison.Ordinal))
            {
                linkingSection = ParseLinkingSection(content);
            }
            else if (string.Equals(name, "producers", StringComparison.Ordinal))
            {
                producersSection = ParseProducersSection(content);
            }
            else if (string.Equals(name, "target_features", StringComparison.Ordinal))
            {
                targetFeaturesSection = ParseTargetFeaturesSection(content);
            }
            else if (name.StartsWith("reloc.", StringComparison.Ordinal))
            {
                relocationSections.Add(ParseRelocationSection(name, content));
            }
        }
        catch (WasmFormatException ex)
        {
            throw new WasmFormatException($"Failed to parse custom section '{name}': {ex.Message}", ex);
        }
    }

    private static ProducersSection ParseProducersSection(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var reader = new WasmBinaryReader(stream);
        uint fieldCount = reader.ReadU32Leb128();
        var fields = new Dictionary<string, List<(string Name, string Version)>>(StringComparer.Ordinal);

        for (uint i = 0; i < fieldCount; i++)
        {
            string fieldName = reader.ReadName();
            uint valueCount = reader.ReadU32Leb128();

            if (!fields.TryGetValue(fieldName, out var values))
            {
                values = [];
                fields[fieldName] = values;
            }

            for (uint j = 0; j < valueCount; j++)
                values.Add((reader.ReadName(), reader.ReadName()));
        }

        ValidateReaderConsumed(reader, "producers section");

        return new ProducersSection
        {
            Fields = fields,
        };
    }

    private static TargetFeaturesSection ParseTargetFeaturesSection(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var reader = new WasmBinaryReader(stream);
        uint count = reader.ReadU32Leb128();
        var features = new List<(char Prefix, string Name)>(checked((int)count));

        for (uint i = 0; i < count; i++)
        {
            char prefix = (char)reader.ReadByte();
            if (prefix is not ('+' or '-' or '='))
                throw new WasmFormatException($"target_features entry {i} uses unsupported prefix '{prefix}'.");

            features.Add((prefix, reader.ReadName()));
        }

        ValidateReaderConsumed(reader, "target_features section");

        return new TargetFeaturesSection
        {
            Features = features,
        };
    }

    private static LinkingSection ParseLinkingSection(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var reader = new WasmBinaryReader(stream);

        uint version = reader.ReadU32Leb128();
        if (version != 2)
            throw new WasmFormatException($"Unsupported linking section version: {version}");

        var symbols = new List<WasmSymbol>();
        var segmentInfos = new List<SegmentInfo>();
        var initFunctions = new List<InitFunction>();
        var comdatGroups = new List<ComdatGroup>();

        while (reader.HasMore)
        {
            byte subsectionType = reader.ReadByte();
            uint payloadLength = reader.ReadU32Leb128();
            byte[] payload = reader.ReadBytes(checked((int)payloadLength));

            using var payloadStream = new MemoryStream(payload, writable: false);
            var payloadReader = new WasmBinaryReader(payloadStream);

            switch (subsectionType)
            {
                case 5:
                    segmentInfos.AddRange(ReadSegmentInfoSubsection(payloadReader));
                    break;
                case 6:
                    initFunctions.AddRange(ReadInitFunctionsSubsection(payloadReader));
                    break;
                case 7:
                    comdatGroups.AddRange(ReadComdatInfoSubsection(payloadReader));
                    break;
                case 8:
                    symbols.AddRange(ReadSymbolTableSubsection(payloadReader));
                    break;
            }

            ValidateReaderConsumed(payloadReader, $"linking subsection {subsectionType}");
        }

        return new LinkingSection
        {
            Version = version,
            Symbols = symbols,
            SegmentInfos = segmentInfos,
            InitFunctions = initFunctions,
            ComdatGroups = comdatGroups,
        };
    }

    private static List<SegmentInfo> ReadSegmentInfoSubsection(WasmBinaryReader reader)
    {
        var segmentInfos = new List<SegmentInfo>();
        foreach (var _ in reader.ReadVectorHeader())
        {
            segmentInfos.Add(new SegmentInfo
            {
                Name = reader.ReadName(),
                Alignment = reader.ReadU32Leb128(),
                Flags = (SegmentFlags)reader.ReadU32Leb128(),
            });
        }

        return segmentInfos;
    }

    private static List<InitFunction> ReadInitFunctionsSubsection(WasmBinaryReader reader)
    {
        var initFunctions = new List<InitFunction>();
        foreach (var _ in reader.ReadVectorHeader())
        {
            initFunctions.Add(new InitFunction
            {
                Priority = reader.ReadU32Leb128(),
                SymbolIndex = reader.ReadU32Leb128(),
            });
        }

        return initFunctions;
    }

    private static List<ComdatGroup> ReadComdatInfoSubsection(WasmBinaryReader reader)
    {
        var comdatGroups = new List<ComdatGroup>();
        foreach (var _ in reader.ReadVectorHeader())
        {
            string name = reader.ReadName();
            uint flags = reader.ReadU32Leb128();
            var entries = new List<ComdatEntry>();
            foreach (var __ in reader.ReadVectorHeader())
            {
                entries.Add(new ComdatEntry
                {
                    Kind = (ComdatKind)reader.ReadByte(),
                    Index = reader.ReadU32Leb128(),
                });
            }

            comdatGroups.Add(new ComdatGroup
            {
                Name = name,
                Flags = flags,
                Entries = entries,
            });
        }

        return comdatGroups;
    }

    private static List<WasmSymbol> ReadSymbolTableSubsection(WasmBinaryReader reader)
    {
        var symbols = new List<WasmSymbol>();
        foreach (var _ in reader.ReadVectorHeader())
        {
            SymbolType type = (SymbolType)reader.ReadByte();
            SymbolFlags flags = (SymbolFlags)reader.ReadU32Leb128();

            switch (type)
            {
                case SymbolType.Function:
                case SymbolType.Global:
                case SymbolType.Event:
                case SymbolType.Table:
                {
                    uint index = reader.ReadU32Leb128();
                    symbols.Add(new WasmSymbol
                    {
                        Type = type,
                        Flags = flags,
                        Index = checked((int)index),
                        Name = SymbolHasInlineName(flags) ? reader.ReadName() : null,
                    });
                    break;
                }
                case SymbolType.Data:
                {
                    string name = reader.ReadName();
                    uint? segmentIndex = null;
                    uint? offset = null;
                    uint? size = null;
                    if (!flags.HasFlag(SymbolFlags.Undefined))
                    {
                        segmentIndex = reader.ReadU32Leb128();
                        offset = reader.ReadU32Leb128();
                        size = reader.ReadU32Leb128();
                    }

                    symbols.Add(new WasmSymbol
                    {
                        Type = type,
                        Flags = flags,
                        Name = name,
                        SegmentIndex = segmentIndex,
                        Offset = offset,
                        Size = size,
                    });
                    break;
                }
                case SymbolType.Section:
                    symbols.Add(new WasmSymbol
                    {
                        Type = type,
                        Flags = flags,
                        SectionIndex = reader.ReadU32Leb128(),
                    });
                    break;
                default:
                    throw new WasmFormatException($"Unsupported linking symbol type: {(byte)type}");
            }
        }

        return symbols;
    }

    private static RelocationSection ParseRelocationSection(string name, byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        var reader = new WasmBinaryReader(stream);

        uint targetSectionIndex = reader.ReadU32Leb128();
        var entries = new List<Relocation>();
        foreach (var _ in reader.ReadVectorHeader())
        {
            RelocationType type = (RelocationType)reader.ReadByte();
            uint offset = reader.ReadU32Leb128();
            uint index = reader.ReadU32Leb128();
            // LLVM writes every addend as an SLEB128 of int64; wasm32 addends fit in 32 bits.
            int addend = RelocationTypeRequiresAddend(type)
                ? checked((int)reader.ReadS64Leb128())
                : 0;

            entries.Add(new Relocation
            {
                Type = type,
                Offset = offset,
                Index = index,
                Addend = addend,
            });
        }

        ValidateReaderConsumed(reader, $"relocation section '{name}'");

        return new RelocationSection
        {
            Name = name,
            TargetSectionIndex = targetSectionIndex,
            Entries = entries,
        };
    }

    private static bool SymbolHasInlineName(SymbolFlags flags)
        => !flags.HasFlag(SymbolFlags.Undefined) || flags.HasFlag(SymbolFlags.ExplicitName);

    // Mirrors llvm::wasm::relocTypeHasAddend (llvm/lib/BinaryFormat/Wasm.cpp). Table-index relocations,
    // including the REL forms, carry no addend.
    private static bool RelocationTypeRequiresAddend(RelocationType type) => type switch
    {
        RelocationType.R_WASM_MEMORY_ADDR_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_I32 or
        RelocationType.R_WASM_FUNCTION_OFFSET_I32 or
        RelocationType.R_WASM_SECTION_OFFSET_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_LEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB or
        RelocationType.R_WASM_FUNCTION_OFFSET_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64 => true,
        _ => false,
    };

    private static void ValidateReaderConsumed(WasmBinaryReader reader, string context)
    {
        if (reader.HasMore)
            throw new WasmFormatException($"Unexpected trailing bytes in {context}.");
    }

    /// <summary>
    /// Reads a bare <c>tabletype</c> (§5.3.9) — element type followed by limits. This is the shape
    /// an <em>import descriptor</em> uses.
    /// </summary>
    /// <remarks>
    /// The extended <c>0x40 0x00 reftype limits expr</c> encoding belongs to the <c>table</c>
    /// production in the table section, not to <c>tabletype</c>, so it must be refused here. Reading
    /// both productions with one function let a module import a table <em>with an initializer</em>,
    /// which is meaningless — the importer does not allocate the table — and which
    /// <c>wasm-tools 1.253.0</c> rejects as "malformed reference type". nesm accepted it and
    /// instantiated the module (GitHub #383).
    /// </remarks>
    private static TableType ReadTableImportType(WasmBinaryReader reader)
    {
        if (reader.PeekByte() is 0x40)
        {
            throw new WasmFormatException(
                "Malformed table import: the 0x40 initializer encoding is only valid for a table " +
                "definition in the table section, not for an import descriptor.");
        }

        return ReadBareTableType(reader);
    }

    /// <summary>
    /// Reads a table-section entry: either a bare <c>tabletype</c> or the extended
    /// <c>0x40 0x00 reftype limits expr</c> form carrying an initializer.
    /// </summary>
    private static TableType ReadTableDefinition(WasmBinaryReader reader)
    {
        if (reader.PeekByte() is not 0x40)
            return ReadBareTableType(reader);

        // Table with an initializer expression: 0x40 0x00 reftype limits expr.
        // The second byte is reserved and MUST be zero. Consuming it unchecked accepts
        // `0x40 0x01 …`, which wasm-tools 1.253.0 rejects as "invalid table encoding" — an
        // over-acceptance no corpus of correct modules could find, because a correct module
        // never sets the reserved byte. The affirmative check is the one nobody wrote.
        reader.ReadByte(); // consume 0x40
        byte reserved = reader.ReadByte();
        if (reserved != 0x00)
            throw new WasmFormatException($"Invalid table encoding: reserved byte after 0x40 must be 0x00, got 0x{reserved:X2}");

        var valType = ReadValTypeInfo(reader);
        var limits = ReadLimits(reader);
        return new TableType(ValTypeToRefType(valType.Type), limits)
        {
            ElementTypeKey = valType.Key,
            ExactElementType = valType.ExactType,
            InitExpr = ReadInitExpr(reader),
        };
    }

    private static TableType ReadBareTableType(WasmBinaryReader reader)
    {
        var vt = ReadValTypeInfo(reader);
        var tableLimits = ReadLimits(reader);
        return new TableType(ValTypeToRefType(vt.Type), tableLimits)
        {
            ElementTypeKey = vt.Key,
            ExactElementType = vt.ExactType,
        };
    }

    /// <summary>Maps a ValType (which may be a GC ref type) to a RefType for table storage.</summary>
    private static RefType ValTypeToRefType(ValType vt) => vt switch
    {
        ValType.FuncRef => RefType.FuncRef,
        ValType.ExternRef => RefType.ExternRef,
        _ => RefType.FuncRef, // GC typed references default to funcref for table ops
    };

    private static Limits ReadLimits(WasmBinaryReader reader)
    {
        byte flag = reader.ReadByte();
        // Limits flag bits (memory64 + threads proposals):
        //   bit0 (0x01) = a max is present
        //   bit1 (0x02) = shared (threads proposal)
        //   bit2 (0x04) = 64-bit (memory64/table64) — min/max are u64 LEB128
        // Valid combinations are 0x00–0x07; anything else is malformed.
        if ((flag & ~0x07) != 0)
            throw new WasmFormatException($"Invalid limits flag: 0x{flag:X2}");

        bool hasMax = (flag & 0x01) != 0;
        bool is64 = (flag & 0x04) != 0;

        // For 64-bit memories/tables the bounds are u64 LEB128; otherwise u32.
        ulong min = is64 ? reader.ReadU64Leb128() : reader.ReadU32Leb128();
        ulong? max = hasMax
            ? (is64 ? reader.ReadU64Leb128() : reader.ReadU32Leb128())
            : null;

        return new Limits(min, max, is64);
    }

    private static MemoryType ReadMemoryType(WasmBinaryReader reader)
    {
        byte flag = reader.PeekByte();
        bool shared = (flag & 0x02) != 0;
        var limits = ReadLimits(reader);
        return new MemoryType(limits, shared);
    }

    private static GlobalType ReadGlobalType(WasmBinaryReader reader)
    {
        var contentType = ReadValTypeInfo(reader);
        byte encodedMutability = reader.ReadByte();
        var mutability = encodedMutability switch
        {
            0x00 => Mutability.Const,
            0x01 => Mutability.Var,
            _ => throw new WasmFormatException(
                $"Invalid global mutability byte: 0x{encodedMutability:X2}"),
        };
        return new GlobalType(contentType.Type, mutability)
        {
            ContentTypeKey = contentType.Key,
            ExactContentType = contentType.ExactType,
        };
    }

    /// <summary>
    /// Read and validate an init expression (§5.4.6).  Parses the opcode and its
    /// immediate(s) properly so that LEB128 overflow is detected, then expects 0x0B (end).
    /// Returns the raw bytes consumed (opcode + immediate + end).
    /// </summary>
    private static byte[] ReadInitExpr(WasmBinaryReader reader)
    {
        // Read a sequence of constant-expression opcodes until END (0x0B).
        // Supports extended-const proposal: i32/i64 add/sub/mul in init expressions.
        var bytes = new List<byte>();

        while (true)
        {
            byte opcode = reader.ReadByte();
            bytes.Add(opcode);

            switch (opcode)
            {
                case 0x0B: // END
                    return bytes.ToArray();
                case 0x41: // i32.const  — s32 LEB128
                    ReadAndCollectS32Leb128(reader, bytes);
                    break;
                case 0x42: // i64.const  — s64 LEB128
                    ReadAndCollectS64Leb128(reader, bytes);
                    break;
                case 0x43: // f32.const  — 4 raw bytes
                    bytes.AddRange(reader.ReadBytes(4));
                    break;
                case 0x44: // f64.const  — 8 raw bytes
                    bytes.AddRange(reader.ReadBytes(8));
                    break;
                case 0x23: // global.get — u32 LEB128 index
                    ReadAndCollectU32Leb128(reader, bytes);
                    break;
                case 0xD0: // ref.null — heap type (s33 LEB128 for GC, single byte for MVP)
                    ReadAndCollectHeapType(reader, bytes);
                    break;
                case 0xD2: // ref.func — u32 LEB128 funcidx
                    ReadAndCollectU32Leb128(reader, bytes);
                    break;
                case 0xFD: // SIMD prefix — v128.const (sub-opcode 12)
                    uint simdSubOpcode = ReadAndCollectU32Leb128(reader, bytes);
                    if (simdSubOpcode != 0x0C)
                        throw new WasmFormatException($"Invalid SIMD init expression sub-opcode: 0x{simdSubOpcode:X}");
                    bytes.AddRange(reader.ReadBytes(16));
                    break;
                // Extended constant expressions — no immediates
                case 0x6A: // i32.add
                case 0x6B: // i32.sub
                case 0x6C: // i32.mul
                case 0x7C: // i64.add
                case 0x7D: // i64.sub
                case 0x7E: // i64.mul
                    break;
                // GC init expression opcodes
                case 0xFB: // GC prefix
                {
                    uint gcSubOpcode = ReadAndCollectU32Leb128(reader, bytes);
                    switch (gcSubOpcode)
                    {
                        // struct.new, struct.new_default, array.new, array.new_default
                        case 0x00:
                        case 0x01:
                        case 0x06:
                        case 0x07:
                            ReadAndCollectU32Leb128(reader, bytes);
                            break;
                        case 0x08: // array.new_fixed
                            ReadAndCollectU32Leb128(reader, bytes);
                            ReadAndCollectU32Leb128(reader, bytes);
                            break;
                        // any.convert_extern, extern.convert_any, ref.i31
                        case 0x1A:
                        case 0x1B:
                        case 0x1C:
                            break;
                        default:
                            throw new WasmFormatException($"Invalid GC init expression sub-opcode: 0x{gcSubOpcode:X}");
                    }
                    break;
                }
                default:
                    throw new WasmFormatException($"Invalid init expression opcode: 0x{opcode:X2}");
            }
        }

        throw new WasmFormatException("Init expression missing END opcode.");
    }

    // Helpers that read a LEB128 value AND collect the raw bytes into a list.
    // They mirror the logic in WasmBinaryReader so overflow/length errors propagate correctly.

    private static int ReadElemExprFunctionIndex(byte[] expr)
    {
        var cursor = new InstructionCursor(expr, WasmFeatures.All);
        if (!cursor.MoveNext(out var instruction))
            return -1;
        return instruction.OpcodeId.Opcode switch
        {
            0xD2 => unchecked((int)instruction.Index0),
            0xD0 => -1,
            0x23 => unchecked(-((int)instruction.Index0 + 2)),
            _ => -1,
        };
    }

    /// <summary>
    /// Reads the <c>elemkind</c> byte of an element segment (flags 1, 2, 3).
    /// </summary>
    /// <remarks>
    /// The byte is read and discarded. The grammar admits exactly one production —
    /// <c>elemkind ::= 0x00 ⇒ ref func</c> — so nesm is over-accepting here: wasm-tools 1.253.0
    /// rejects any other byte with "only the function external type is supported in elem segment".
    ///
    /// Rejecting it was tried while auditing all eight forms for #411 and deliberately backed out:
    /// 32 existing tests across TypedFuncRefTests, StackSwitchingTests and RefClassificationTests
    /// encode a declarative segment as flag 3 followed by <c>0x70</c> (the <i>reftype</i> for
    /// funcref) rather than flag 3 followed by <c>0x00</c>, or flag 7 followed by <c>0x70</c>.
    /// Those fixtures are themselves malformed and only parse because this byte is ignored.
    /// Tightening the decoder therefore means rewriting them, which is a separate change from the
    /// element-type fix #411 asks for. Kept as a named helper so the over-acceptance has one
    /// obvious site rather than three bare <c>ReadByte()</c> calls, and so the follow-up has
    /// somewhere to land.
    /// </remarks>
    private static void ReadElemKind(WasmBinaryReader reader)
    {
        // Deliberately unvalidated — see remarks. Spec-correct behaviour is:
        //   if (elemKind != 0x00) throw new WasmFormatException(...);
        _ = reader.ReadByte();
    }

    /// <summary>
    /// Reads and validates a reference type in an element segment.
    /// Handles standard ref types and GC typed references.
    /// </summary>
    private static Microsoft.NET.WebAssembly.Types.ValueType ReadExactRefType(WasmBinaryReader reader)
    {
        var vt = ReadValTypeInfo(reader);
        if (vt.ExactType is not Microsoft.NET.WebAssembly.Types.ValueType.Reference)
            throw new WasmFormatException("malformed reference type");
        return vt.ExactType;
    }

    private static uint ReadAndCollectU32Leb128(WasmBinaryReader reader, List<byte> bytes)
    {
        uint result = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            if (i == 4 && (b & 0xF0) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint32");
            result |= (uint)(b & 0x7F) << (7 * i);
            if ((b & 0x80) == 0) return result;
        }
        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 5 bytes for uint32");
    }

    /// <summary>Collects the raw bytes of a heap type (s33 LEB128) into a byte list.</summary>
    private static void ReadAndCollectHeapType(WasmBinaryReader reader, List<byte> bytes)
    {
        for (int i = 0; i < S33Leb128.MaxBytes; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            S33Leb128.ValidateTerminalByte(b, i);
            if ((b & 0x80) == 0)
                return;
        }
        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for s33");
    }

    private static void ReadAndCollectS32Leb128(WasmBinaryReader reader, List<byte> bytes)
    {
        for (int i = 0; i < 5; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            if (i == 4 && (b & 0x78) != 0 && (b & 0x78) != 0x78)
                throw new WasmFormatException("Signed LEB128 value overflows int32");
            if ((b & 0x80) == 0) return;
            if (i == 4)
                throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for int32");
        }
        throw new WasmFormatException("Signed LEB128 encoding exceeds 5 bytes for int32");
    }

    private static void ReadAndCollectS64Leb128(WasmBinaryReader reader, List<byte> bytes)
    {
        for (int i = 0; i < 10; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            if (i == 9 && (b & 0x7F) != 0 && (b & 0x7F) != 0x7F)
                throw new WasmFormatException("Signed LEB128 value overflows int64");
            if ((b & 0x80) == 0) return;
            if (i == 9)
                throw new WasmFormatException("Signed LEB128 encoding exceeds 10 bytes for int64");
        }
        throw new WasmFormatException("Signed LEB128 encoding exceeds 10 bytes for int64");
    }

    // ── Code body validation ────────────────────────────────────────────────────────────

    /// <summary>
    /// Validates the binary grammar of a function body using the shared instruction cursor,
    /// including well-formed immediates, known prefix sub-opcodes, and structural termination.
    /// </summary>
    private static void ValidateCodeBody(byte[] body, bool hasDataCountSection, WasmFeatures features)
    {
        var cursor = new InstructionCursor(body, features);
        var stack = new List<(byte opcode, bool isIf, bool isTry)>(4);

        while (cursor.MoveNext(out var instruction))
        {
            switch (instruction.OpcodeId.Opcode)
            {
                case 0x02:
                case 0x03:
                case 0x1F:
                    stack.Add((instruction.OpcodeId.Opcode, isIf: false, isTry: false));
                    break;
                case 0x04:
                    stack.Add((instruction.OpcodeId.Opcode, isIf: true, isTry: false));
                    break;
                case 0x06:
                    stack.Add((instruction.OpcodeId.Opcode, isIf: false, isTry: true));
                    break;
                case 0x05:
                    if (stack.Count == 0 || !stack[^1].isIf)
                        throw new WasmFormatException("else outside of if");
                    break;
                case 0x07:
                case 0x19:
                    if (stack.Count == 0 || !stack[^1].isTry)
                        throw new WasmFormatException(instruction.OpcodeId.Opcode == 0x07
                            ? "catch outside of try block"
                            : "catch_all outside of try block");
                    break;
                case 0x18:
                    if (stack.Count == 0 || !stack[^1].isTry)
                        throw new WasmFormatException("delegate without matching try");
                    stack.RemoveAt(stack.Count - 1);
                    break;
                case 0x0B:
                    if (stack.Count == 0)
                    {
                        if (cursor.Position != body.Length)
                            throw new WasmFormatException("Unexpected bytes after function body END opcode");
                        return;
                    }
                    stack.RemoveAt(stack.Count - 1);
                    break;
            }

            if (instruction.OpcodeId.Opcode == 0xFC && instruction.OpcodeId.SubOpcode is 8 or 9 && !hasDataCountSection)
                throw new WasmFormatException("data count section required");
        }

        throw new WasmFormatException("END opcode expected in function body");
    }
}
