// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Collections.Generic;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;
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

    /// <summary>
    /// Implementation limit on a function's locals, parameters included, as in V8 and wasmtime. The spec only bounds
    /// the total by 2^32-1, which a 30-byte body can declare; expanding that many would exhaust memory.
    /// </summary>
    internal const int MaxFunctionLocals = 50_000;

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
        return ReadCoreFromStream(stream, includesHeader: true, options ?? ModuleReaderOptions.Default);
    }

    /// <summary>Read and validate a core module.</summary>
    public static WasmModule ReadModuleAndValidate(Stream stream, ModuleReaderOptions? options = null)
        => ReadModule(stream, options);

    // ── Internal: in-memory buffer for ArArchiveReader / ComponentReader ───────

    /// <summary>Parse a core module from an in-memory buffer. Internal only.</summary>
    internal static WasmModule ParseCoreModule(ReadOnlyMemory<byte> bytes, ModuleReaderOptions? options = null)
        => ReadCoreInPlace(bytes.Span, baseOffset: 0, includesHeader: true, options);

    /// <summary>
    /// Parses a core module from <paramref name="stream"/>, reading each section once. A <see cref="MemoryStream"/>
    /// whose buffer is exposed is parsed in place, without copying; any other stream is read a section at a time
    /// into a pooled buffer.
    /// </summary>
    /// <param name="stream">The stream, positioned at the module.</param>
    /// <param name="includesHeader">Whether the stream starts at the 8-byte header, or just after it.</param>
    /// <param name="options">The reader options; <see langword="null"/> skips the debug sections.</param>
    private static WasmModule ReadCoreFromStream(Stream stream, bool includesHeader, ModuleReaderOptions? options)
    {
        if (stream.GetType() == typeof(MemoryStream) && ((MemoryStream)stream).TryGetBuffer(out ArraySegment<byte> buffer)
            && stream.Position <= buffer.Count)
        {
            int start = (int)stream.Position;
            var parsed = ReadCoreInPlace(buffer.AsSpan(start), start, includesHeader, options);
            stream.Position = buffer.Count;
            return parsed;
        }

        using var framing = new WasmSectionStream(stream, nonSeekableStart: includesHeader ? 0 : 8);
        if (includesHeader)
            ValidateHeader(framing);
        var module = new CoreModuleSections(options);
        while (framing.TryReadByte(out byte sectionId))
        {
            uint sectionSize = framing.ReadU32();
            ValidateSectionOrder(sectionId, ref module.LastSectionPosition);
            var section = framing.ReadPayload(SectionLength(sectionId, sectionSize));
            ReadSection(module, sectionId, sectionSize, ref section);
        }

        return FinishCore(module, options);
    }

    /// <summary>Parses a core module whose bytes are all in memory.</summary>
    private static WasmModule ReadCoreInPlace(ReadOnlySpan<byte> bytes, long baseOffset, bool includesHeader, ModuleReaderOptions? options)
    {
        var reader = new WasmSpanReader(bytes, baseOffset);
        if (includesHeader)
            ValidateHeader(ref reader);
        var module = new CoreModuleSections(options);
        while (reader.HasMore)
        {
            byte sectionId = reader.ReadByte();
            uint sectionSize = reader.ReadU32();
            ValidateSectionOrder(sectionId, ref module.LastSectionPosition);
            var section = reader.Sub(SectionLength(sectionId, sectionSize));
            ReadSection(module, sectionId, sectionSize, ref section);
        }

        return FinishCore(module, options);
    }

    private static WasmModule FinishCore(CoreModuleSections sections, ModuleReaderOptions? options)
    {
        // Validate data count section against actual data segments.
        ValidateStreamingRead(sections.DataCount, sections.DataSegments.Count, sections.TypeIndices.Count, sections.Functions.Count);

        // Enrich FuncTypes whose results include defined heap types with the correct null tag for
        // each such result slot.  Must run after the full type section is parsed so that every
        // DefinedHeapType reference can be resolved through typeDefinitions.
        ResolveResultNullTags(sections.Types, sections.TypeDefinitions);

        var module = sections.CreateModule();
        module.EnabledFeatures = sections.Features;

        BranchHintMetadataReader.Apply(
            module,
            (options ?? ModuleReaderOptions.Default).MetadataValidation);

        if (options is not null)
            ParseDebugSections(module, options);

        return module;
    }

    /// <summary>
    /// The sections of one core module as they are parsed: the lists the model is built from, and the state the
    /// ordering and count checks need.
    /// </summary>
    private sealed class CoreModuleSections
    {
        public readonly List<FuncType> Types = [];
        public readonly List<TypeDefinition> TypeDefinitions = [];
        public readonly List<Import> Imports = [];
        public readonly List<int> TypeIndices = [];
        public readonly List<TableType> Tables = [];
        public readonly List<MemoryType> Memories = [];
        public readonly List<WasmGlobal> Globals = [];
        public readonly OrderedDictionary<string, Export> Exports = new();
        public readonly List<Export> ExportEntries = [];
        public readonly List<Element> Elements = [];
        public readonly List<WasmFunction> Functions = [];
        public readonly List<Data> DataSegments = [];
        public readonly List<CustomSection> CustomSections = [];
        public readonly List<RelocationSection> RelocationSections = [];
        public readonly List<Tag> Tags = [];
        public int? StartFunction;
        public LinkingSection? LinkingSection;
        public ProducersSection? ProducersSection;
        public TargetFeaturesSection? TargetFeaturesSection;
        public uint? DataCount;
        public int LastSectionPosition = -1;
        public int SectionIndex = -1;
        public bool CodeSectionSeen;

        /// <summary>
        /// Shares the section lists by reference so that body decoding (which runs when the code section is
        /// reached, after imports/memories are parsed) can resolve memory64 memarg offsets via Imports/Memories.
        /// </summary>
        public readonly WasmModule DecodeModule;

        public readonly WasmFeatures Features;

        /// <summary>
        /// The asynchronous streaming reader's shape: the function section creates the functions the code section
        /// fills in as each body arrives, and custom sections do not record their section index.
        /// </summary>
        public readonly bool Streaming;

        public CoreModuleSections(ModuleReaderOptions? options, bool streaming = false)
        {
            Features = (options ?? ModuleReaderOptions.Default).Features;
            Streaming = streaming;
            DecodeModule = new WasmModule
            {
                Types = Types,
                TypeDefinitions = TypeDefinitions,
                Functions = Functions,
                Imports = Imports,
                Memories = Memories,
            };
        }

        public WasmModule CreateModule() => new()
        {
            Types = Types,
            TypeDefinitions = TypeDefinitions,
            Imports = Imports,
            Functions = Functions,
            Tables = Tables,
            Memories = Memories,
            Globals = Globals,
            Tags = Tags,
            Exports = Exports,
            ExportEntries = ExportEntries,
            StartFunction = StartFunction,
            Elements = Elements,
            DataSegments = DataSegments,
            CustomSections = CustomSections,
            LinkingSection = LinkingSection,
            ProducersSection = ProducersSection,
            TargetFeaturesSection = TargetFeaturesSection,
            RelocationSections = RelocationSections,
            HasDataCountSection = DataCount.HasValue,
        };
    }

    private static void ParseDebugSections(WasmModule module, ModuleReaderOptions options)
    {
        if (!options.Features.HasFlag(WasmFeatures.DebugInfo)) return;
        var debugLine    = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_line");
        var debugLineStr = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_line_str");
        var debugStr     = module.CustomSections.FirstOrDefault(s => s.Name == ".debug_str");
        if (debugLine is null)
            return;

        // DWARF is custom-section metadata: malformed debug info never rejects the module. The
        // reader recovers per unit, so the good units are kept and each bad one is reported like
        // other recognized metadata (strict mode throws instead).
        var result = DwarfLineReader.ParseUnits(
            debugLine.Content,
            debugLineStr?.Content,
            debugStr?.Content);
        if (result.Errors.Count != 0)
        {
            var diagnostics = new List<WasmMetadataDiagnostic>(module.MetadataDiagnostics.Count + result.Errors.Count);
            diagnostics.AddRange(module.MetadataDiagnostics);
            foreach (var error in result.Errors)
            {
                var diagnostic = new WasmMetadataDiagnostic(error.Section, WasmMetadataErrorKind.Malformed, error.Detail);
                if (options.MetadataValidation == WasmMetadataValidationMode.Strict)
                    throw new WasmMetadataException(diagnostic, error);
                diagnostics.Add(diagnostic);
            }
            module.MetadataDiagnostics = diagnostics;
        }
        module.DebugInfo = result.Programs;
    }

    private static void ValidateSectionOrder(byte sectionId, ref int lastSectionPosition)
    {
        if (sectionId > (byte)WasmSectionId.Tag)
            throw new WasmFormatException($"Malformed section id: {sectionId}");

        if (sectionId == (byte)WasmSectionId.Custom)
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

    /// <summary>The length of a section's payload, rejecting a declared size no input could hold.</summary>
    private static int SectionLength(byte sectionId, uint sectionSize)
        => sectionSize <= int.MaxValue
            ? (int)sectionSize
            : throw new WasmFormatException($"Section {sectionId} payload is too large ({sectionSize} bytes)");

    /// <summary>
    /// Parses one section's payload into <paramref name="module"/>. This is the only section switch: every entry
    /// point frames the sections its own way and hands each payload here.
    /// </summary>
    private static void ReadSection(CoreModuleSections module, byte sectionId, uint sectionSize, ref WasmSpanReader section)
    {
        module.SectionIndex++;
        int customSectionCount = module.CustomSections.Count;

        switch ((WasmSectionId)sectionId)
        {
            case WasmSectionId.Custom:
                ReadCustomSection(
                    ref section,
                    module.CustomSections,
                    ref module.LinkingSection,
                    ref module.ProducersSection,
                    ref module.TargetFeaturesSection,
                    module.RelocationSections,
                    module.CodeSectionSeen);
                break;
            case WasmSectionId.Type:
                ReadTypeSection(ref section, module.Types, module.TypeDefinitions);
                break;
            case WasmSectionId.Import:
                ReadImportSection(ref section, module.Imports);
                break;
            case WasmSectionId.Function:
                if (module.Streaming)
                    ReadFunctionSectionWithPlaceholders(ref section, module.TypeIndices, module.Functions);
                else
                    ReadFunctionSection(ref section, module.TypeIndices);
                break;
            case WasmSectionId.Table:
                ReadTableSection(ref section, module.Tables);
                break;
            case WasmSectionId.Memory:
                ReadMemorySection(ref section, module.Memories);
                break;
            case WasmSectionId.Global:
                ReadGlobalSection(ref section, module.Globals);
                break;
            case WasmSectionId.Export:
                ReadExportSection(ref section, module.Exports, module.ExportEntries);
                break;
            case WasmSectionId.Start:
                module.StartFunction = (int)section.ReadU32();
                break;
            case WasmSectionId.Element:
                ReadElementSection(ref section, module.Elements);
                break;
            case WasmSectionId.Code:
                ReadCodeSection(ref section, module.Functions, module.TypeIndices, module.DecodeModule, module.DataCount.HasValue, module.Features);
                module.CodeSectionSeen = true;
                break;
            case WasmSectionId.Data:
                ReadDataSection(ref section, module.DataSegments);
                break;
            case WasmSectionId.DataCount:
                module.DataCount = section.ReadU32();
                break;
            case WasmSectionId.Tag:
                ReadTagSection(ref section, module.Tags);
                break;
            default:
                throw new WasmFormatException($"Malformed section id: {sectionId}");
        }

        if (section.HasMore)
        {
            throw new WasmFormatException(
                $"Section {sectionId} size mismatch: declared {sectionSize} bytes but consumed {section.Position}",
                section.AbsolutePosition);
        }

        if (!module.Streaming && module.CustomSections.Count > customSectionCount)
            module.CustomSections[^1] = module.CustomSections[^1] with { SectionIndex = module.SectionIndex };
    }

    private static void ValidateHeader(ref WasmSpanReader reader)
    {
        ValidateMagic(reader.ReadBytes(4));
        ValidateVersion(reader.ReadBytes(4));
    }

    private static void ValidateHeader(WasmSectionStream stream)
    {
        Span<byte> field = stackalloc byte[4];
        stream.ReadExactly(field);
        ValidateMagic(field);
        stream.ReadExactly(field);
        ValidateVersion(field);
    }

    private static void ValidateMagic(ReadOnlySpan<byte> magic)
    {
        if (!magic.SequenceEqual(Magic))
            throw new WasmFormatException("Invalid WebAssembly magic number");
    }

    private static void ValidateVersion(ReadOnlySpan<byte> version)
    {
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
        ref WasmSpanReader reader,
        List<FuncType> types,
        List<TypeDefinition> typeDefinitions)
    {
        int recGroupIndex = typeDefinitions.Count == 0 ? 0 : typeDefinitions[^1].RecGroup.Index + 1;
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            byte tag = reader.PeekByte();
            if (tag == 0x4E) // rec group — wraps multiple sub/composite types
            {
                reader.ReadByte(); // consume 0x4E
                uint count = reader.ReadVectorCount();
                var group = new RecGroupIdentity(recGroupIndex++, types.Count, ToInt32(count, "Rec group size"));
                for (uint i = 0; i < count; i++)
                    AddType(types, typeDefinitions, ReadSubtypeOrComposite(ref reader, types, group));
            }
            else
            {
                var group = new RecGroupIdentity(recGroupIndex++, types.Count, 1);
                AddType(types, typeDefinitions, ReadSubtypeOrComposite(ref reader, types, group));
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
        ref WasmSpanReader reader,
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
            uint supertypeCount = reader.ReadVectorCount();
            supertypes = new int[supertypeCount];
            for (uint i = 0; i < supertypeCount; i++)
                supertypes[i] = ToInt32(reader.ReadU32(), "Supertype index");
            subtypePrefix = $"sub[{(isFinal ? "final" : "open")}:{string.Join(",", supertypes)}]:";
            tag = reader.ReadByte(); // inner composite type tag
        }

        var result = (CompositeTypeCode)tag switch
        {
            CompositeTypeCode.Func => ReadFuncType(ref reader),
            CompositeTypeCode.Cont => ReadContType(ref reader, existingTypes),
            CompositeTypeCode.Struct => ReadStructType(ref reader),
            CompositeTypeCode.Array => ReadArrayType(ref reader),
            _ => throw new WasmFormatException($"Expected composite type tag (0x60/0x5D/0x5F/0x5E), got 0x{tag:X2}"),
        };
        var compactType = result.CompactType;
        if (subtypePrefix is not null)
            compactType = compactType with { LinkKey = subtypePrefix + compactType.LinkKey };

        return new TypeParseResult(compactType, result.CompositeType, isFinal, supertypes, recGroup);
    }

    private static TypeParseResult ReadFuncType(ref WasmSpanReader reader)
    {
        ReadResultType(ref reader, out ValType[] parameters, out string[] parameterKeys, out Microsoft.NET.WebAssembly.Types.ValueType[] exactParameters);
        ReadResultType(ref reader, out ValType[] results, out string[] resultKeys, out Microsoft.NET.WebAssembly.Types.ValueType[] exactResults);
        var compact = new FuncType(parameters, results)
        {
            LinkKey = $"({string.Join(",", parameterKeys)})->({string.Join(",", resultKeys)})",
        };
        return new TypeParseResult(compact, new CompositeType.Function(exactParameters, exactResults));
    }

    /// <summary>Reads a vector of value types (a <c>resulttype</c>, §5.3.5).</summary>
    private static void ReadResultType(
        ref WasmSpanReader reader,
        out ValType[] types,
        out string[] keys,
        out Microsoft.NET.WebAssembly.Types.ValueType[] exactTypes)
    {
        uint count = reader.ReadVectorCount();
        if (count == 0)
        {
            types = [];
            keys = [];
            exactTypes = [];
            return;
        }

        types = new ValType[count];
        keys = new string[count];
        exactTypes = new Microsoft.NET.WebAssembly.Types.ValueType[count];
        for (int i = 0; i < types.Length; i++)
        {
            var info = ReadValTypeInfo(ref reader);
            types[i] = info.Type;
            keys[i] = info.Key;
            exactTypes[i] = info.ExactType;
        }
    }

    private static TypeParseResult ReadContType(ref WasmSpanReader reader, List<FuncType> existingTypes)
    {
        uint funcTypeIndex = reader.ReadU32();
        if (funcTypeIndex >= (uint)existingTypes.Count)
            throw new WasmFormatException($"Continuation type refers to invalid function type index {funcTypeIndex}");
        return new TypeParseResult(
            existingTypes[(int)funcTypeIndex],
            new CompositeType.Continuation((int)funcTypeIndex));
    }

    private static TypeParseResult ReadStructType(ref WasmSpanReader reader)
    {
        uint fieldCount = reader.ReadVectorCount(minElementSize: 2); // storage type + mutability
        var fields = new FieldType[fieldCount];
        for (uint i = 0; i < fieldCount; i++)
            fields[i] = ReadFieldType(ref reader);
        return new TypeParseResult(NonFuncTypePlaceholder, new CompositeType.Struct(fields));
    }

    private static TypeParseResult ReadArrayType(ref WasmSpanReader reader)
    {
        var field = ReadFieldType(ref reader);
        return new TypeParseResult(NonFuncTypePlaceholder, new CompositeType.Array(field));
    }

    /// <summary>Reads a GC field type: storage type + mutability byte.</summary>
    private static FieldType ReadFieldType(ref WasmSpanReader reader)
    {
        var storageType = ReadStorageType(ref reader);
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
    private static StorageType ReadStorageType(ref WasmSpanReader reader)
    {
        byte b = reader.PeekByte();
        if (b is 0x78 or 0x77) // packed types: i8, i16
        {
            reader.ReadByte();
            return new StorageType.Packed(b == 0x78 ? PackedStorageType.I8 : PackedStorageType.I16);
        }
        return new StorageType.Value(ReadValTypeInfo(ref reader).ExactType);
    }

    /// <summary>
    /// Reads a value type, handling both single-byte types (i32, i64, funcref, etc.)
    /// and multi-byte GC reference types (ref null $type, ref $type, abstract heap types).
    /// </summary>
    private static ValType ReadValType(ref WasmSpanReader reader)
        => ReadValTypeInfo(ref reader).Type;

    private static Microsoft.NET.WebAssembly.Types.ValueType ReadExactValType(ref WasmSpanReader reader)
        => ReadValTypeInfo(ref reader).ExactType;

    private readonly record struct ValTypeInfo(ValType Type, string Key, Microsoft.NET.WebAssembly.Types.ValueType ExactType);

    private static ValTypeInfo ReadValTypeInfo(ref WasmSpanReader reader)
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
            0x63 or 0x64 => ReadRefWithHeapType(ref reader, b),
            _ => throw new WasmFormatException($"Unknown value type: 0x{b:X2}"),
        };
    }

    /// <summary>
    /// Reads the heap type following a 0x63 (ref null) or 0x64 (ref) prefix.
    /// Heap types are encoded as s33 LEB128: positive = type index, negative = abstract heap type.
    /// </summary>
    private static ValTypeInfo ReadRefWithHeapType(ref WasmSpanReader reader, byte refByte)
    {
        long heapType = reader.ReadS33();
        var nullability = refByte == 0x63 ? Nullability.Nullable : Nullability.NonNullable;
        if (heapType >= 0)
        {
            return ValInfo(new Microsoft.NET.WebAssembly.Types.ValueType.Reference(nullability, new DefinedHeapType(ToInt32((uint)heapType, "Heap type index"))));
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

    private static void ReadImportSection(ref WasmSpanReader reader, List<Import> imports)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            string moduleName = reader.ReadName();
            string name = reader.ReadName();
            byte kind = reader.ReadByte();
            ImportDescriptor descriptor = (WasmExternalKind)kind switch
            {
                WasmExternalKind.Function => new ImportDescriptor.Func((int)reader.ReadU32()),
                WasmExternalKind.Table => new ImportDescriptor.Table(ReadTableImportType(ref reader)),
                WasmExternalKind.Memory => new ImportDescriptor.Memory(ReadMemoryType(ref reader)),
                WasmExternalKind.Global => new ImportDescriptor.Global(ReadGlobalType(ref reader)),
                WasmExternalKind.Tag => new ImportDescriptor.Tag(ReadTagType(ref reader)),
                _ => throw new WasmFormatException($"Unknown import descriptor kind: 0x{kind:X2}"),
            };
            imports.Add(new Import(moduleName, name, descriptor));
        }
    }

    private static void ReadFunctionSection(ref WasmSpanReader reader, List<int> typeIndices)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
            typeIndices.Add((int)reader.ReadU32());
    }

    private static void ReadFunctionSectionWithPlaceholders(
        ref WasmSpanReader reader,
        List<int> typeIndices,
        List<WasmFunction> functions)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            int typeIndex = (int)reader.ReadU32();
            typeIndices.Add(typeIndex);
            functions.Add(new WasmFunction
            {
                TypeIndex = typeIndex,
                Locals = [],
                Body = [],
            });
        }
    }

    private static void ReadTableSection(ref WasmSpanReader reader, List<TableType> tables)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
            tables.Add(ReadTableDefinition(ref reader));
    }

    private static void ReadMemorySection(ref WasmSpanReader reader, List<MemoryType> memories)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
            memories.Add(ReadMemoryType(ref reader));
    }

    private static void ReadGlobalSection(ref WasmSpanReader reader, List<WasmGlobal> globals)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            var type = ReadGlobalType(ref reader);
            byte[] initExpr = ReadInitExpr(ref reader);
            globals.Add(new WasmGlobal { Type = type, InitExpr = initExpr });
        }
    }

    private static void ReadTagSection(ref WasmSpanReader reader, List<Tag> tags)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
            tags.Add(new Tag(ReadTagType(ref reader)));
    }

    private static int ReadTagType(ref WasmSpanReader reader)
    {
        byte attribute = reader.ReadByte();
        if (attribute != 0x00)
            throw new WasmFormatException($"Invalid tag attribute: 0x{attribute:X2}");
        return (int)reader.ReadU32();
    }

    private static void ReadExportSection(
        ref WasmSpanReader reader,
        OrderedDictionary<string, Export> exports,
        List<Export> exportEntries)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            string name = reader.ReadName();
            byte kind = reader.ReadByte();
            ExportDescriptor descriptor = (WasmExternalKind)kind switch
            {
                WasmExternalKind.Function => new ExportDescriptor.Func((int)reader.ReadU32()),
                WasmExternalKind.Table => new ExportDescriptor.Table((int)reader.ReadU32()),
                WasmExternalKind.Memory => new ExportDescriptor.Memory((int)reader.ReadU32()),
                WasmExternalKind.Global => new ExportDescriptor.Global((int)reader.ReadU32()),
                WasmExternalKind.Tag => new ExportDescriptor.Tag((int)reader.ReadU32()),
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
    private static void ReadElementSection(ref WasmSpanReader reader, List<Element> elements)
    {
        // Flags 0-3: `ref func` (non-nullable). Flag 4: `funcref` (nullable). Not interchangeable.
        var refFuncType = new Microsoft.NET.WebAssembly.Types.ValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.Func));
        var funcrefType = new Microsoft.NET.WebAssembly.Types.ValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            uint flags = reader.ReadU32();
            switch (flags)
            {
                case (uint)ElementSegmentKind.ActiveFunctions:
                {
                    // Active, implicit tableidx=0, `ref func`, vec<funcidx>
                    byte[] offsetExpr = ReadInitExpr(ref reader);
                    var funcIndices = ReadFunctionIndices(ref reader);
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Active,
                        TableIndex = 0,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = funcIndices,
                        ElementType = refFuncType,
                    });
                    break;
                }
                case (uint)ElementSegmentKind.PassiveFunctions:
                {
                    // Passive, elemkind byte, vec<funcidx> — no table/offset
                    ReadElemKind(ref reader);
                    var funcIndices = ReadFunctionIndices(ref reader);
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Passive,
                        TableIndex = -1, // passive: no target table
                        OffsetExpr = [],
                        FunctionIndices = funcIndices,
                        ElementType = refFuncType,
                    });
                    break;
                }
                case (uint)ElementSegmentKind.ActiveTableFunctions:
                {
                    // Active, explicit tableidx, offset, elemkind byte, vec<funcidx>
                    int tableIndex = (int)reader.ReadU32();
                    byte[] offsetExpr = ReadInitExpr(ref reader);
                    ReadElemKind(ref reader);
                    var funcIndices = ReadFunctionIndices(ref reader);
                    elements.Add(new Element
                    {
                        Mode = ElementMode.Active,
                        TableIndex = tableIndex,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = funcIndices,
                        HasExplicitTableIndex = true,
                        ElementType = refFuncType,
                    });
                    break;
                }
                case (uint)ElementSegmentKind.DeclarativeFunctions:
                {
                    // Declarative, elemkind byte, vec<funcidx>
                    ReadElemKind(ref reader);
                    var funcIndices = ReadFunctionIndices(ref reader);
                    elements.Add(new Element { Mode = ElementMode.Declarative, TableIndex = -1, OffsetExpr = [], FunctionIndices = [], DeclaredFunctionIndices = funcIndices, ElementType = refFuncType });
                    break;
                }
                case (uint)ElementSegmentKind.ActiveExpressions:
                {
                    // Active, implicit tableidx=0, `funcref` (nullable — unlike flag 0), vec<expr>
                    byte[] offsetExpr = ReadInitExpr(ref reader);
                    var expressions = ReadElementExpressions(ref reader);
                    var funcIndices = ElementExpressionFunctionIndices(expressions);
                    elements.Add(new Element { Mode = ElementMode.Active, TableIndex = 0, OffsetExpr = offsetExpr, FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, ElementType = funcrefType });
                    break;
                }
                case (uint)ElementSegmentKind.PassiveExpressions:
                {
                    // Passive, reftype, vec<expr>
                    var elementType = ReadExactRefType(ref reader);
                    var expressions = ReadElementExpressions(ref reader);
                    var funcIndices = ElementExpressionFunctionIndices(expressions);
                    elements.Add(new Element { Mode = ElementMode.Passive, TableIndex = -1, OffsetExpr = [], FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, ElementType = elementType });
                    break;
                }
                case (uint)ElementSegmentKind.ActiveTableExpressions:
                {
                    // Active, explicit tableidx, offset, reftype, vec<expr>
                    int tableIndex = (int)reader.ReadU32();
                    byte[] offsetExpr = ReadInitExpr(ref reader);
                    var elementType = ReadExactRefType(ref reader);
                    var expressions = ReadElementExpressions(ref reader);
                    var funcIndices = ElementExpressionFunctionIndices(expressions);
                    elements.Add(new Element { Mode = ElementMode.Active, TableIndex = tableIndex, OffsetExpr = offsetExpr, FunctionIndices = funcIndices, ElementExpressions = expressions, UsesExpressions = true, HasExplicitTableIndex = true, ElementType = elementType });
                    break;
                }
                case (uint)ElementSegmentKind.DeclarativeExpressions:
                {
                    // Declarative, reftype, vec<expr>
                    var elementType = ReadExactRefType(ref reader);
                    var expressions = ReadElementExpressions(ref reader);
                    var funcIndices = ElementExpressionFunctionIndices(expressions);
                    elements.Add(new Element { Mode = ElementMode.Declarative, TableIndex = -1, OffsetExpr = [], FunctionIndices = [], DeclaredFunctionIndices = DeclaredFunctionIndices(funcIndices), ElementExpressions = expressions, UsesExpressions = true, ElementType = elementType });
                    break;
                }
                default:
                    throw new WasmFormatException($"Unsupported element segment type: {flags}");
            }
        }
    }

    private static List<int> ReadFunctionIndices(ref WasmSpanReader reader)
    {
        uint count = reader.ReadVectorCount();
        var indices = new List<int>((int)count);
        for (uint i = 0; i < count; i++)
            indices.Add((int)reader.ReadU32());
        return indices;
    }

    private static List<byte[]> ReadElementExpressions(ref WasmSpanReader reader)
    {
        uint count = reader.ReadVectorCount();
        var expressions = new List<byte[]>((int)count);
        for (uint i = 0; i < count; i++)
            expressions.Add(ReadInitExpr(ref reader));
        return expressions;
    }

    private static List<int> ElementExpressionFunctionIndices(List<byte[]> expressions)
    {
        var indices = new List<int>(expressions.Count);
        foreach (byte[] expression in expressions)
            indices.Add(ReadElemExprFunctionIndex(expression));
        return indices;
    }

    /// <summary>The function indices a declarative segment declares: those of its <c>ref.func</c> expressions.</summary>
    private static List<int> DeclaredFunctionIndices(List<int> expressionFunctionIndices)
    {
        var declared = new List<int>(expressionFunctionIndices.Count);
        foreach (int index in expressionFunctionIndices)
        {
            if (index >= 0)
                declared.Add(index);
        }

        return declared;
    }

    private static void ReadCodeSection(
        ref WasmSpanReader reader,
        List<WasmFunction> functions,
        List<int> typeIndices,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        int functionIndex = functions.Count;
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            if ((uint)functionIndex >= (uint)typeIndices.Count)
                throw new WasmFormatException("Function/code section count mismatch: too many code entries");

            uint funcOffset = (uint)reader.Position;
            uint bodySize = reader.ReadU32();
            uint bodyStartOffset = (uint)reader.Position;
            var body = reader.Sub(CodeEntryLength(bodySize));
            var func = new WasmFunction
            {
                TypeIndex = typeIndices[functionIndex],
                Locals = [],
                Body = [],
            };
            PopulateCodeEntry(ref body, bodySize, func, decodeModule, hasDataCountSection, features);
            func.CodeSectionOffset = funcOffset;
            func.BodyCodeSectionOffset = bodyStartOffset + (bodySize - (uint)func.Body.Length);
            func.LocalsCodeSectionOffset = bodyStartOffset;
            functions.Add(func);
            functionIndex++;
        }
    }

    private static int CodeEntryLength(uint bodySize)
        => bodySize <= int.MaxValue
            ? (int)bodySize
            : throw new WasmFormatException($"Function body payload is too large ({bodySize} bytes)");

    /// <summary>
    /// Reads one code entry's locals and body from <paramref name="reader"/>, which spans exactly the entry's
    /// <paramref name="bodySize"/> bytes, into <paramref name="function"/>, and decodes the body.
    /// </summary>
    private static void PopulateCodeEntry(
        ref WasmSpanReader reader,
        uint bodySize,
        WasmFunction function,
        WasmModule decodeModule,
        bool hasDataCountSection,
        WasmFeatures features = WasmFeatures.Default)
    {
        // Each declaration is at least a count byte and a type byte.
        uint declarationCount = reader.ReadVectorCount(minElementSize: 2);
        var localDecls = new (uint Count, ValType Type, Microsoft.NET.WebAssembly.Types.ValueType ExactType)[declarationCount];
        for (int i = 0; i < localDecls.Length; i++)
        {
            uint count = reader.ReadU32();
            var exactType = ReadExactValType(ref reader);
            localDecls[i] = (count, exactType.ToStorageValType(), exactType);
        }

        ulong totalLocals = 0;
        foreach (var (count, _, _) in localDecls)
        {
            totalLocals += count;
            if (totalLocals > uint.MaxValue)
                throw new WasmFormatException("Too many locals: total count exceeds 2^32-1");
        }

        // Checked before the locals are expanded: one 30-byte declaration can name 2^32-1 of them.
        int parameterCount = (uint)function.TypeIndex < (uint)decodeModule.Types.Count
            ? decodeModule.Types[function.TypeIndex].Parameters.Length
            : 0;
        if (totalLocals + (ulong)parameterCount > MaxFunctionLocals)
        {
            throw new WasmFormatException(
                $"Too many locals: {parameterCount} parameter(s) and {totalLocals} declared local(s) exceed the implementation limit of {MaxFunctionLocals}");
        }

        var locals = new List<ValType>((int)totalLocals);
        var exactLocals = new List<Microsoft.NET.WebAssembly.Types.ValueType>((int)totalLocals);
        foreach (var (count, type, exactType) in localDecls)
        {
            for (uint i = 0; i < count; i++)
            {
                locals.Add(type);
                exactLocals.Add(exactType);
            }
        }

        // The model owns its body: the reader may be over a pooled buffer.
        byte[] body = reader.ReadBytes(reader.Remaining).ToArray();
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

    private static void ReadDataSection(ref WasmSpanReader reader, List<Data> dataSegments)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            uint flag = reader.ReadU32();
            bool isPassive = flag switch
            {
                0 or 2 => false,
                1 => true,
                _ => throw new WasmFormatException($"Unsupported data segment flag: {flag}"),
            };
            int memoryIndex = flag switch { 0 => 0, 1 => -1, _ => (int)reader.ReadU32() };
            byte[] offsetExpr = isPassive ? [] : ReadInitExpr(ref reader);
            int length = ToInt32(reader.ReadU32(), "Data segment length");
            uint payloadOffset = (uint)reader.Position;
            dataSegments.Add(new Data
            {
                IsPassive = isPassive,
                MemoryIndex = memoryIndex,
                OffsetExpr = offsetExpr,
                InitData = reader.ReadBytes(length).ToArray(),
                SectionPayloadOffset = payloadOffset,
            });
        }
    }

    private static void ReadCustomSection(
        ref WasmSpanReader reader,
        List<CustomSection> customSections,
        ref LinkingSection? linkingSection,
        ref ProducersSection? producersSection,
        ref TargetFeaturesSection? targetFeaturesSection,
        List<RelocationSection> relocationSections,
        bool appearedAfterCode)
    {
        ReadOnlySpan<byte> nameBytes = reader.ReadNameBytes();
        string name = Encoding.UTF8.GetString(nameBytes);
        long contentOffset = reader.AbsolutePosition;
        ReadOnlySpan<byte> content = reader.ReadBytes(reader.Remaining);
        customSections.Add(new CustomSection(name, content.IsEmpty ? [] : content.ToArray())
        {
            AppearedAfterCode = appearedAfterCode,
        });

        try
        {
            if (nameBytes.SequenceEqual("linking"u8))
            {
                linkingSection = ParseLinkingSection(content, contentOffset);
            }
            else if (nameBytes.SequenceEqual("producers"u8))
            {
                producersSection = ParseProducersSection(new WasmSpanReader(content, contentOffset));
            }
            else if (nameBytes.SequenceEqual("target_features"u8))
            {
                targetFeaturesSection = ParseTargetFeaturesSection(new WasmSpanReader(content, contentOffset));
            }
            else if (nameBytes.StartsWith("reloc."u8))
            {
                relocationSections.Add(ParseRelocationSection(name, content));
            }
        }
        catch (WasmFormatException ex)
        {
            throw new WasmFormatException($"Failed to parse custom section '{name}': {ex.Message}", ex, ex.BinaryOffset);
        }
    }

    private static ProducersSection ParseProducersSection(WasmSpanReader reader)
    {
        uint fieldCount = reader.ReadU32();
        var fields = new Dictionary<string, List<(string Name, string Version)>>(StringComparer.Ordinal);

        for (uint i = 0; i < fieldCount; i++)
        {
            string fieldName = reader.ReadName();
            uint valueCount = reader.ReadU32();

            if (!fields.TryGetValue(fieldName, out var values))
            {
                values = [];
                fields[fieldName] = values;
            }

            for (uint j = 0; j < valueCount; j++)
                values.Add((reader.ReadName(), reader.ReadName()));
        }

        reader.ExpectEnd("producers section");

        return new ProducersSection
        {
            Fields = fields,
        };
    }

    private static TargetFeaturesSection ParseTargetFeaturesSection(WasmSpanReader reader)
    {
        // Each entry is at least a prefix byte and a name length.
        uint count = reader.ReadVectorCount(minElementSize: 2);
        var features = new List<(char Prefix, string Name)>((int)count);

        for (uint i = 0; i < count; i++)
        {
            char prefix = (char)reader.ReadByte();
            if (prefix is not ('+' or '-' or '='))
                throw new WasmFormatException($"target_features entry {i} uses unsupported prefix '{prefix}'.");

            features.Add((prefix, reader.ReadName()));
        }

        reader.ExpectEnd("target_features section");

        return new TargetFeaturesSection
        {
            Features = features,
        };
    }

    // Subsections are read in place: neither the section nor its subsections are copied.
    private static LinkingSection ParseLinkingSection(ReadOnlySpan<byte> span, long baseOffset)
    {
        int position = 0;
        uint version = WasmLeb128.ReadU32(span, ref position);
        if (version != LinkingSection.CurrentVersion)
            throw new WasmFormatException($"Unsupported linking section version: {version}");

        var symbols = new List<WasmSymbol>();
        var segmentInfos = new List<SegmentInfo>();
        var initFunctions = new List<InitFunction>();
        var comdatGroups = new List<ComdatGroup>();

        while (position < span.Length)
        {
            byte subsectionType = span[position++];
            uint payloadLength = WasmLeb128.ReadU32(span, ref position);
            if ((ulong)position + payloadLength > (ulong)span.Length)
                throw new WasmFormatException($"Linking subsection {subsectionType} extends past the end of the linking section.");
            var payloadReader = new WasmSpanReader(span.Slice(position, (int)payloadLength), baseOffset + position);
            position += (int)payloadLength;

            switch (subsectionType)
            {
                case (byte)LinkingSubsectionId.SegmentInfo:
                    ReadSegmentInfoSubsection(ref payloadReader, segmentInfos);
                    break;
                case (byte)LinkingSubsectionId.InitFunctions:
                    ReadInitFunctionsSubsection(ref payloadReader, initFunctions);
                    break;
                case (byte)LinkingSubsectionId.ComdatInfo:
                    ReadComdatInfoSubsection(ref payloadReader, comdatGroups);
                    break;
                case (byte)LinkingSubsectionId.SymbolTable:
                    ReadSymbolTableSubsection(ref payloadReader, symbols);
                    break;
            }

            payloadReader.ExpectEnd($"linking subsection {subsectionType}");
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

    private static void ReadSegmentInfoSubsection(ref WasmSpanReader reader, List<SegmentInfo> segmentInfos)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            segmentInfos.Add(new SegmentInfo
            {
                Name = reader.ReadName(),
                Alignment = reader.ReadU32(),
                Flags = (SegmentFlags)reader.ReadU32(),
            });
        }
    }

    private static void ReadInitFunctionsSubsection(ref WasmSpanReader reader, List<InitFunction> initFunctions)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            initFunctions.Add(new InitFunction
            {
                Priority = reader.ReadU32(),
                SymbolIndex = reader.ReadU32(),
            });
        }
    }

    private static void ReadComdatInfoSubsection(ref WasmSpanReader reader, List<ComdatGroup> comdatGroups)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            string name = reader.ReadName();
            uint flags = reader.ReadU32();
            var entries = new List<ComdatEntry>();
            for (uint remainingEntries = reader.ReadU32(); remainingEntries > 0; remainingEntries--)
            {
                entries.Add(new ComdatEntry
                {
                    Kind = (ComdatKind)reader.ReadByte(),
                    Index = reader.ReadU32(),
                });
            }

            comdatGroups.Add(new ComdatGroup
            {
                Name = name,
                Flags = flags,
                Entries = entries,
            });
        }
    }

    private static void ReadSymbolTableSubsection(ref WasmSpanReader reader, List<WasmSymbol> symbols)
    {
        for (uint remaining = reader.ReadU32(); remaining > 0; remaining--)
        {
            SymbolType type = (SymbolType)reader.ReadByte();
            SymbolFlags flags = (SymbolFlags)reader.ReadU32();

            switch (type)
            {
                case SymbolType.Function:
                case SymbolType.Global:
                case SymbolType.Event:
                case SymbolType.Table:
                {
                    uint index = reader.ReadU32();
                    symbols.Add(new WasmSymbol
                    {
                        Type = type,
                        Flags = flags,
                        Index = ToInt32(index, "Symbol index"),
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
                        segmentIndex = reader.ReadU32();
                        offset = reader.ReadU32();
                        size = reader.ReadU32();
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
                        SectionIndex = reader.ReadU32(),
                    });
                    break;
                default:
                    throw new WasmFormatException($"Unsupported linking symbol type: {(byte)type}");
            }
        }
    }

    private static RelocationSection ParseRelocationSection(string name, ReadOnlySpan<byte> span)
    {
        // Decoded from the span: relocation sections hold a large share of an object's entries, and the
        // stream reader's per-byte reads dominated linking.
        int position = 0;
        uint targetSectionIndex = WasmLeb128.ReadU32(span, ref position);
        uint count = WasmLeb128.ReadU32(span, ref position);
        // Each entry is at least three bytes, which bounds the capacity a malformed count can request.
        var entries = new List<Relocation>((int)Math.Min(count, (uint)(span.Length - position) / 3));
        for (uint i = 0; i < count; i++)
        {
            if (position >= span.Length)
                throw new WasmFormatException($"Unexpected end of relocation section '{name}'.");
            RelocationType type = (RelocationType)span[position++];
            uint offset = WasmLeb128.ReadU32(span, ref position);
            uint index = WasmLeb128.ReadU32(span, ref position);
            // LLVM writes every addend as an SLEB128 of int64; wasm32 addends fit in 32 bits.
            int addend = RelocationTypes.HasAddend(type)
                ? ToAddend(WasmLeb128.ReadS64(span, ref position))
                : 0;

            entries.Add(new Relocation
            {
                Type = type,
                Offset = offset,
                Index = index,
                Addend = addend,
            });
        }

        if (position != span.Length)
            throw new WasmFormatException($"Unexpected trailing bytes in relocation section '{name}'.");

        return new RelocationSection
        {
            Name = name,
            TargetSectionIndex = targetSectionIndex,
            Entries = entries,
        };
    }

    private static bool SymbolHasInlineName(SymbolFlags flags)
        => !flags.HasFlag(SymbolFlags.Undefined) || flags.HasFlag(SymbolFlags.ExplicitName);

    private static int ToAddend(long addend)
        => addend is >= int.MinValue and <= int.MaxValue
            ? (int)addend
            : throw new WasmFormatException($"Relocation addend {addend} does not fit in 32 bits");

    /// <summary>Converts a decoded length, count or index to <see cref="int"/>, rejecting one that does not fit.</summary>
    private static int ToInt32(ulong value, string what)
        => value <= int.MaxValue ? (int)value : throw new WasmFormatException($"{what} {value} is too large");

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
    private static TableType ReadTableImportType(ref WasmSpanReader reader)
    {
        if (reader.PeekByte() is 0x40)
        {
            throw new WasmFormatException(
                "Malformed table import: the 0x40 initializer encoding is only valid for a table " +
                "definition in the table section, not for an import descriptor.");
        }

        return ReadBareTableType(ref reader);
    }

    /// <summary>
    /// Reads a table-section entry: either a bare <c>tabletype</c> or the extended
    /// <c>0x40 0x00 reftype limits expr</c> form carrying an initializer.
    /// </summary>
    private static TableType ReadTableDefinition(ref WasmSpanReader reader)
    {
        if (reader.PeekByte() is not 0x40)
            return ReadBareTableType(ref reader);

        // Table with an initializer expression: 0x40 0x00 reftype limits expr.
        // The second byte is reserved and MUST be zero. Consuming it unchecked accepts
        // `0x40 0x01 …`, which wasm-tools 1.253.0 rejects as "invalid table encoding" — an
        // over-acceptance no corpus of correct modules could find, because a correct module
        // never sets the reserved byte. The affirmative check is the one nobody wrote.
        reader.ReadByte(); // consume 0x40
        byte reserved = reader.ReadByte();
        if (reserved != 0x00)
            throw new WasmFormatException($"Invalid table encoding: reserved byte after 0x40 must be 0x00, got 0x{reserved:X2}");

        var valType = ReadValTypeInfo(ref reader);
        var limits = ReadLimits(ref reader);
        return new TableType(ValTypeToRefType(valType.Type), limits)
        {
            ElementTypeKey = valType.Key,
            ExactElementType = valType.ExactType,
            InitExpr = ReadInitExpr(ref reader),
        };
    }

    private static TableType ReadBareTableType(ref WasmSpanReader reader)
    {
        var vt = ReadValTypeInfo(ref reader);
        var tableLimits = ReadLimits(ref reader);
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

    private static Limits ReadLimits(ref WasmSpanReader reader)
    {
        var flag = (WasmLimitsFlags)reader.ReadByte();
        // Limits flag bits (memory64 + threads proposals): a max is present, shared (threads), and 64-bit
        // (memory64/table64, whose min/max are u64 LEB128). Valid combinations are 0x00–0x07; anything else is malformed.
        if ((flag & ~WasmLimitsFlags.All) != 0)
            throw new WasmFormatException($"Invalid limits flag: 0x{(byte)flag:X2}");

        bool hasMax = (flag & WasmLimitsFlags.HasMaximum) != 0;
        bool is64 = (flag & WasmLimitsFlags.Is64) != 0;

        // For 64-bit memories/tables the bounds are u64 LEB128; otherwise u32.
        ulong min = is64 ? reader.ReadU64() : reader.ReadU32();
        ulong? max = hasMax
            ? (is64 ? reader.ReadU64() : reader.ReadU32())
            : null;

        return new Limits(min, max, is64);
    }

    private static MemoryType ReadMemoryType(ref WasmSpanReader reader)
    {
        var flag = (WasmLimitsFlags)reader.PeekByte();
        bool shared = (flag & WasmLimitsFlags.Shared) != 0;
        var limits = ReadLimits(ref reader);
        return new MemoryType(limits, shared);
    }

    private static GlobalType ReadGlobalType(ref WasmSpanReader reader)
    {
        var contentType = ReadValTypeInfo(ref reader);
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
    /// Reads an init expression (§5.4.6) through the shared <see cref="ConstExpressionWalker"/>, and returns a copy of
    /// its bytes, <c>end</c> included.
    /// </summary>
    /// <remarks>
    /// The walk refuses an instruction outside the constant-expression grammar before decoding it, so the messages
    /// below name the instruction rather than whatever follows it. A malformed immediate of a constant instruction is
    /// reported by the instruction decoder, as it is in a function body; bytes that run out before <c>end</c> are
    /// reported by the section reader, at the missing byte.
    /// </remarks>
    private static byte[] ReadInitExpr(ref WasmSpanReader reader)
    {
        var walker = new ConstExpressionWalker(reader.RemainingBytes, WasmFeatures.All, reader.AbsolutePosition);
        while (walker.MoveNext(out ConstOp op, out DecodedInstruction instruction))
        {
            if (op == ConstOp.End)
                return reader.ReadBytes(walker.Position).ToArray();
            if (op == ConstOp.NotConstant)
                throw new WasmFormatException(NotConstantInitExprMessage(instruction.OpcodeId), reader.AbsolutePosition + instruction.ByteOffset);
        }

        reader.ReadBytes(walker.Position);
        reader.ReadByte();
        throw new WasmFormatException("Init expression has no end opcode.", reader.AbsolutePosition);
    }

    private static string NotConstantInitExprMessage(OpcodeId opcode) => opcode.Opcode switch
    {
        WasmOpcode.SimdPrefix => $"Invalid SIMD init expression sub-opcode: 0x{opcode.SubOpcode:X}",
        WasmOpcode.GcPrefix => $"Invalid GC init expression sub-opcode: 0x{opcode.SubOpcode:X}",
        _ => $"Invalid init expression opcode: 0x{opcode.Opcode:X2}",
    };

    private static int ReadElemExprFunctionIndex(byte[] expr)
    {
        var walker = new ConstExpressionWalker(expr);
        if (!walker.MoveNext(out ConstOp op, out DecodedInstruction instruction))
            return -1;
        return op switch
        {
            ConstOp.RefFunc => unchecked((int)instruction.Index0),
            ConstOp.GlobalGet => unchecked(-((int)instruction.Index0 + 2)),
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
    private static void ReadElemKind(ref WasmSpanReader reader)
    {
        // Deliberately unvalidated — see remarks. Spec-correct behaviour is:
        //   if (elemKind != 0x00) throw new WasmFormatException(...);
        _ = reader.ReadByte();
    }

    /// <summary>
    /// Reads and validates a reference type in an element segment.
    /// Handles standard ref types and GC typed references.
    /// </summary>
    private static Microsoft.NET.WebAssembly.Types.ValueType ReadExactRefType(ref WasmSpanReader reader)
    {
        var vt = ReadValTypeInfo(ref reader);
        if (vt.ExactType is not Microsoft.NET.WebAssembly.Types.ValueType.Reference)
            throw new WasmFormatException("malformed reference type");
        return vt.ExactType;
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
                        throw new WasmFormatException(instruction.OpcodeId.Opcode == WasmOpcode.Catch
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

            if (instruction.OpcodeId.Opcode == WasmOpcode.MiscPrefix && instruction.OpcodeId.SubOpcode is 8 or 9 && !hasDataCountSection)
                throw new WasmFormatException("data count section required");
        }

        throw new WasmFormatException("END opcode expected in function body");
    }
}
