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

    /// <summary>Read a core module. Throws <see cref="WasmComponentException"/> if component header detected.</summary>
    public static WasmModule ReadModule(Stream stream, ModuleReaderOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        var reader = new WasmBinaryReader(stream);
        ValidateHeader(reader);
        return ReadCoreAfterHeader(reader, streamSections: true, options: options ?? ModuleReaderOptions.Default);
    }

    /// <summary>Read and validate a core module.</summary>
    public static WasmModule ReadModuleAndValidate(Stream stream, ModuleReaderOptions? options = null)
    {
        var module = ReadModule(stream, options);
        WasmModuleValidator.ValidateAndThrow(module);
        return module;
    }

    // ── Internal: in-memory buffer for ArArchiveReader / ComponentReader ───────

    /// <summary>Parse a core module from an in-memory buffer. Internal only.</summary>
    internal static WasmModule ReadModule(ReadOnlyMemory<byte> bytes, ModuleReaderOptions? options = null)
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
        var imports = new List<Import>();
        var typeIndices = new List<int>();
        var tables = new List<TableType>();
        var memories = new List<MemoryType>();
        var globals = new List<WasmGlobal>();
        var exports = new OrderedDictionary<string, Export>();
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
        var decodeModule = new WasmModule { Types = types, Functions = functions, Imports = imports, Memories = memories };
        var features = (options ?? ModuleReaderOptions.Default).Features;

        int lastSectionPosition = -1; // tracks ordering of non-custom sections

        while (reader.HasMore)
        {
            byte sectionId = reader.ReadByte();
            uint sectionSize = reader.ReadU32Leb128();
            long sectionStart = reader.Position;
            ValidateSectionOrder(sectionId, ref lastSectionPosition);

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
                            relocationSections);
                        ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                        break;
                    }
                    case 1:
                    {
                        var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                        ReadTypeSection(sectionReader, types);
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
                        ReadExportSection(sectionReader, exports);
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
                            relocationSections);
                        break;
                    case 1:
                        ReadTypeSection(reader, types);
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
                        ReadExportSection(reader, exports);
                        break;
                    case 8:
                        startFunction = (int)reader.ReadU32Leb128();
                        break;
                    case 9:
                        ReadElementSection(reader, elements);
                        break;
                    case 10:
                        ReadCodeSection(reader, functions, typeIndices, decodeModule, dataCount.HasValue, features);
                        break;
                    case 11:
                        ReadDataSection(reader, dataSegments);
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
        }

        // Validate data count section against actual data segments.
        ValidateStreamingRead(dataCount, dataSegments.Count, typeIndices.Count, functions.Count);

        var module = CreateModule(
            types, imports, functions, tables, memories, globals, tags, exports,
            startFunction, elements, dataSegments, customSections, linkingSection, producersSection,
            targetFeaturesSection, relocationSections, dataCount.HasValue);

        if (options is not null)
            ParseDebugSections(module, options);

        return module;
    }

    private static WasmModule CreateModule(
        List<FuncType> types,
        List<Import> imports,
        List<WasmFunction> functions,
        List<TableType> tables,
        List<MemoryType> memories,
        List<WasmGlobal> globals,
        List<Tag> tags,
        OrderedDictionary<string, Export> exports,
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
            Imports = imports,
            Functions = functions,
            Tables = tables,
            Memories = memories,
            Globals = globals,
            Tags = tags,
            Exports = exports,
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
        List<Import> imports,
        List<int> typeIndices,
        List<TableType> tables,
        List<MemoryType> memories,
        List<WasmGlobal> globals,
        OrderedDictionary<string, Export> exports,
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
        bool createFunctionPlaceholders)
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
                    relocationSections);
                ValidateSectionConsumption(sectionId, sectionSize, sectionReader);
                break;
            }
            case 1:
            {
                var sectionReader = ReadSectionPayload(reader, sectionId, sectionSize);
                ReadTypeSection(sectionReader, types);
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
                ReadExportSection(sectionReader, exports);
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

    // Placeholder for non-function type entries (GC struct/array types).
    // Stored at the correct index so that function type indices remain valid.
    private static readonly FuncType NonFuncTypePlaceholder = new([], []);

    private static void ReadTypeSection(WasmBinaryReader reader, List<FuncType> types)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            byte tag = reader.PeekByte();
            if (tag == 0x4E) // rec group — wraps multiple sub/composite types
            {
                reader.ReadByte(); // consume 0x4E
                uint count = reader.ReadU32Leb128();
                for (uint i = 0; i < count; i++)
                    types.Add(ReadSubtypeOrComposite(reader, types));
            }
            else
            {
                types.Add(ReadSubtypeOrComposite(reader, types));
            }
        }
    }

    /// <summary>
    /// Reads a subtype wrapper (0x50 open, 0x4F final) or a bare composite type.
    /// Returns a FuncType for function types, or a placeholder for struct/array types.
    /// </summary>
    private static FuncType ReadSubtypeOrComposite(WasmBinaryReader reader, List<FuncType> existingTypes)
    {
        byte tag = reader.ReadByte();

        // Sub-type wrappers: 0x50 = sub (open), 0x4F = sub final
        if (tag is 0x50 or 0x4F)
        {
            uint supertypeCount = reader.ReadU32Leb128();
            for (uint i = 0; i < supertypeCount; i++)
                reader.ReadU32Leb128(); // supertype index
            tag = reader.ReadByte(); // inner composite type tag
        }

        return tag switch
        {
            0x60 => ReadFuncType(reader),
            0x5D => ReadContType(reader, existingTypes),
            0x5F => ReadStructType(reader),
            0x5E => ReadArrayType(reader),
            _ => throw new WasmFormatException($"Expected composite type tag (0x60/0x5D/0x5F/0x5E), got 0x{tag:X2}"),
        };
    }

    private static FuncType ReadFuncType(WasmBinaryReader reader)
    {
        var parameters = reader.ReadVector(() => ReadValType(reader));
        var results = reader.ReadVector(() => ReadValType(reader));
        return new FuncType(parameters, results);
    }

    private static FuncType ReadContType(WasmBinaryReader reader, List<FuncType> existingTypes)
    {
        uint funcTypeIndex = reader.ReadU32Leb128();
        if (funcTypeIndex >= (uint)existingTypes.Count)
            throw new WasmFormatException($"Continuation type refers to invalid function type index {funcTypeIndex}");
        return existingTypes[(int)funcTypeIndex];
    }

    private static FuncType ReadStructType(WasmBinaryReader reader)
    {
        uint fieldCount = reader.ReadU32Leb128();
        for (uint i = 0; i < fieldCount; i++)
            ReadFieldType(reader);
        return NonFuncTypePlaceholder;
    }

    private static FuncType ReadArrayType(WasmBinaryReader reader)
    {
        ReadFieldType(reader);
        return NonFuncTypePlaceholder;
    }

    /// <summary>Reads a GC field type: storage type + mutability byte.</summary>
    private static void ReadFieldType(WasmBinaryReader reader)
    {
        ReadStorageType(reader);
        reader.ReadByte(); // mutability
    }

    /// <summary>Reads a GC storage type: valtype | i8 (0x78) | i16 (0x77).</summary>
    private static void ReadStorageType(WasmBinaryReader reader)
    {
        byte b = reader.PeekByte();
        if (b is 0x78 or 0x77) // packed types: i8, i16
        {
            reader.ReadByte();
            return;
        }
        ReadValType(reader);
    }

    /// <summary>
    /// Reads a value type, handling both single-byte types (i32, i64, funcref, etc.)
    /// and multi-byte GC reference types (ref null $type, ref $type, abstract heap types).
    /// </summary>
    private static ValType ReadValType(WasmBinaryReader reader)
    {
        byte b = reader.ReadByte();
        return b switch
        {
            // Standard numeric types
            0x7F => ValType.I32,
            0x7E => ValType.I64,
            0x7D => ValType.F32,
            0x7C => ValType.F64,
            0x7B => ValType.V128,
            // Standard reference types
            0x70 => ValType.FuncRef,
            0x6F => ValType.ExternRef,
            0x69 => ValType.ExnRef,
            0x65 => ValType.ContRef,
            // GC abstract reference types — treat as externref for storage
            0x6E or 0x71 or 0x72 or 0x6D or  // noextern, nofunc, none, noexn
            0x73 or 0x74 or 0x6C or          // struct, array, i31
            0x6B or 0x6A                      // any, i31ref
                => ValType.ExternRef,
            // Typed references: (ref null ht) / (ref ht) — consume heap type
            0x63 or 0x64 => ReadRefWithHeapType(reader),
            _ => throw new WasmFormatException($"Unknown value type: 0x{b:X2}"),
        };
    }

    /// <summary>
    /// Reads the heap type following a 0x63 (ref null) or 0x64 (ref) prefix.
    /// Heap types are encoded as s33 LEB128: positive = type index, negative = abstract heap type.
    /// </summary>
    private static ValType ReadRefWithHeapType(WasmBinaryReader reader)
    {
        long heapType = reader.ReadS33Leb128();
        if (heapType >= 0)
        {
            // Type index reference — could be func, struct, or array type
            // Treat as funcref if we're in a context that expects function references
            return ValType.FuncRef;
        }
        // Abstract heap type — map known codes to our ValType
        byte code = (byte)(heapType & 0x7F);
        return code switch
        {
            0x70 => ValType.FuncRef,   // func
            0x6F => ValType.ExternRef, // extern
            0x69 => ValType.ExnRef,    // exn
            0x65 => ValType.ContRef,   // cont
            _ => ValType.ExternRef,    // all other GC heap types
        };
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
                0x01 => new ImportDescriptor.Table(ReadTableType(reader)),
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
            tables.Add(ReadTableType(reader));
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

    private static void ReadExportSection(WasmBinaryReader reader, OrderedDictionary<string, Export> exports)
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
            exports.TryAdd(name, new Export(name, descriptor));
        }
    }

    private static void ReadElementSection(WasmBinaryReader reader, List<Element> elements)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            uint flags = reader.ReadU32Leb128();
            switch (flags)
            {
                case 0:
                {
                    // Active, implicit tableidx=0, funcref, vec<funcidx>
                    byte[] offsetExpr = ReadInitExpr(reader);
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        TableIndex = 0,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = new List<int>(funcIndices),
                    });
                    break;
                }
                case 1:
                {
                    // Passive, elemkind byte, vec<funcidx> — no table/offset
                    reader.ReadByte(); // elemkind (0 = funcref)
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        TableIndex = -1, // passive: no target table
                        OffsetExpr = [],
                        FunctionIndices = new List<int>(funcIndices),
                    });
                    break;
                }
                case 2:
                {
                    // Active, explicit tableidx, offset, elemkind byte, vec<funcidx>
                    int tableIndex = (int)reader.ReadU32Leb128();
                    byte[] offsetExpr = ReadInitExpr(reader);
                    reader.ReadByte(); // elemkind (0 = funcref)
                    var funcIndices = reader.ReadVector(() => (int)reader.ReadU32Leb128());
                    elements.Add(new Element
                    {
                        TableIndex = tableIndex,
                        OffsetExpr = offsetExpr,
                        FunctionIndices = new List<int>(funcIndices),
                    });
                    break;
                }
                case 3:
                {
                    // Declarative, elemkind byte, vec<funcidx>
                    reader.ReadByte(); // elemkind
                    foreach (var __ in reader.ReadVectorHeader())
                        reader.ReadU32Leb128();
                    elements.Add(new Element { TableIndex = -1, OffsetExpr = [], FunctionIndices = [] });
                    break;
                }
                case 4:
                {
                    // Active, implicit tableidx=0, funcref, vec<expr>
                    byte[] offsetExpr = ReadInitExpr(reader);
                    var funcIndices = new List<int>(reader.ReadVector(() => ReadElemExprFuncIndex(reader)));
                    elements.Add(new Element { TableIndex = 0, OffsetExpr = offsetExpr, FunctionIndices = funcIndices });
                    break;
                }
                case 5:
                {
                    // Passive, reftype, vec<expr>
                    ReadAndValidateRefType(reader);
                    var funcIndices = new List<int>(reader.ReadVector(() => ReadElemExprFuncIndex(reader)));
                    elements.Add(new Element { TableIndex = -1, OffsetExpr = [], FunctionIndices = funcIndices });
                    break;
                }
                case 6:
                {
                    // Active, explicit tableidx, offset, reftype, vec<expr>
                    int tableIndex = (int)reader.ReadU32Leb128();
                    byte[] offsetExpr = ReadInitExpr(reader);
                    ReadAndValidateRefType(reader);
                    var funcIndices = new List<int>(reader.ReadVector(() => ReadElemExprFuncIndex(reader)));
                    elements.Add(new Element { TableIndex = tableIndex, OffsetExpr = offsetExpr, FunctionIndices = funcIndices });
                    break;
                }
                case 7:
                {
                    // Declarative, reftype, vec<expr>
                    ReadAndValidateRefType(reader);
                    foreach (var __ in reader.ReadVectorHeader())
                        ReadElemExprFuncIndex(reader);
                    elements.Add(new Element { TableIndex = -1, OffsetExpr = [], FunctionIndices = [] });
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
            var type = ReadValType(reader);
            return (count, type);
        });

        ulong totalLocals = 0;
        foreach (var (count, _) in localDecls)
        {
            totalLocals += count;
            if (totalLocals > uint.MaxValue)
                throw new WasmFormatException("Too many locals: total count exceeds 2^32-1");
        }

        var locals = new List<ValType>();
        foreach (var (count, type) in localDecls)
        {
            for (uint i = 0; i < count; i++)
                locals.Add(type);
        }

        long remaining = (long)bodySize - (reader.Position - bodyStart);
        if (remaining < 0)
            throw new WasmFormatException("Code entry body size underflow");

        byte[] body = reader.ReadBytes(checked((int)remaining));
        function.Locals = locals;
        function.Body = body;
        DecodeBody(body, decodeModule, function, hasDataCountSection, features);
        if (function.DecodedBody is null)
            ValidateCodeBody(body, hasDataCountSection, features);
    }

    private static void ReadDataSection(WasmBinaryReader reader, List<Data> dataSegments)
    {
        foreach (var _ in reader.ReadVectorHeader())
        {
            uint flag = reader.ReadU32Leb128();
            dataSegments.Add(flag switch
            {
                0 => // active, implicit memory index 0
                    new Data
                    {
                        IsPassive = false, MemoryIndex = 0,
                        OffsetExpr = ReadInitExpr(reader),
                        InitData = reader.ReadBytes((int)reader.ReadU32Leb128()),
                    },
                1 => // passive — no memory index or offset
                    new Data
                    {
                        IsPassive = true, MemoryIndex = -1, OffsetExpr = [],
                        InitData = reader.ReadBytes((int)reader.ReadU32Leb128()),
                    },
                2 => // active, explicit memory index
                    new Data
                    {
                        IsPassive = false, MemoryIndex = (int)reader.ReadU32Leb128(),
                        OffsetExpr = ReadInitExpr(reader),
                        InitData = reader.ReadBytes((int)reader.ReadU32Leb128()),
                    },
                _ => throw new WasmFormatException($"Unsupported data segment flag: {flag}"),
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
        List<RelocationSection> relocationSections)
    {
        long nameStart = reader.Position;
        string name = reader.ReadName();
        int nameBytes = checked((int)(reader.Position - nameStart));
        int contentLength = checked((int)sectionSize - nameBytes);
        if (contentLength < 0)
            throw new WasmFormatException($"Custom section '{name}' has an invalid payload length.");

        byte[] content = contentLength > 0 ? reader.ReadBytes(contentLength) : [];
        customSections.Add(new CustomSection(name, content));

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
            int addend = RelocationTypeRequiresAddend(type) ? reader.ReadS32Leb128() : 0;

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

    private static bool RelocationTypeRequiresAddend(RelocationType type) => type switch
    {
        RelocationType.R_WASM_MEMORY_ADDR_LEB or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_I32 or
        RelocationType.R_WASM_FUNCTION_OFFSET_I32 or
        RelocationType.R_WASM_SECTION_OFFSET_I32 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or
        RelocationType.R_WASM_TABLE_INDEX_REL_SLEB or
        RelocationType.R_WASM_MEMORY_ADDR_LEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB or
        RelocationType.R_WASM_FUNCTION_OFFSET_I64 or
        RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32 or
        RelocationType.R_WASM_TABLE_INDEX_REL_SLEB64 or
        RelocationType.R_WASM_MEMORY_ADDR_TLS_SLEB64 => true,
        _ => false,
    };

    private static void ValidateReaderConsumed(WasmBinaryReader reader, string context)
    {
        if (reader.HasMore)
            throw new WasmFormatException($"Unexpected trailing bytes in {context}.");
    }

    private static TableType ReadTableType(WasmBinaryReader reader)
    {
        byte refByte = reader.PeekByte();
        RefType elementType;
        if (refByte is 0x40)
        {
            // GC table with init expression: 0x40 0x00 reftype limits expr
            reader.ReadByte(); // consume 0x40
            reader.ReadByte(); // consume 0x00
            var valType = ReadValType(reader);
            elementType = ValTypeToRefType(valType);
            var limits = ReadLimits(reader);
            ReadInitExpr(reader); // init expression
            return new TableType(elementType, limits);
        }
        var vt = ReadValType(reader);
        elementType = ValTypeToRefType(vt);
        var tableLimits = ReadLimits(reader);
        return new TableType(elementType, tableLimits);
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
        var contentType = ReadValType(reader);
        var mutability = (Mutability)reader.ReadByte();
        return new GlobalType(contentType, mutability);
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
                    ReadAndCollectU32Leb128(reader, bytes);
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
                    ReadAndCollectU32Leb128(reader, bytes); // sub-opcode
                    // GC init expr sub-opcodes that may appear:
                    // struct.new (0), struct.new_default (1), array.new (6),
                    // array.new_default (7), array.new_fixed (8),
                    // ref.i31 (28), any.convert_extern (26), extern.convert_any (27)
                    // For now, consume based on known sub-opcodes or skip gracefully
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

    /// <summary>
    /// Reads an element expression (used in segment types 4-7) and returns the function index,
    /// or -1 for a null reference. Consumes the opcode, immediates, and trailing END byte.
    /// </summary>
    private static int ReadElemExprFuncIndex(WasmBinaryReader reader)
    {
        byte op = reader.ReadByte();
        int funcIdx;
        switch (op)
        {
            case 0xD2: // ref.func funcidx
                funcIdx = (int)reader.ReadU32Leb128();
                break;
            case 0xD0: // ref.null heaptype
                reader.ReadS33Leb128(); // heap type (s33 for GC)
                funcIdx = -1;
                break;
            case 0x23: // global.get globalidx — funcref resolved at instantiation time
                // Encode as -(globalIdx + 2) so it's distinguishable from -1 (ref.null)
                funcIdx = -((int)reader.ReadU32Leb128() + 2);
                break;
            default:
                throw new WasmFormatException($"Unsupported opcode 0x{op:X2} in element expression");
        }
        byte end = reader.ReadByte();
        if (end != 0x0B)
            throw new WasmFormatException($"Expected END (0x0B) after element expression, got 0x{end:X2}");
        return funcIdx;
    }

    /// <summary>
    /// Reads and validates a reference type in an element segment.
    /// Handles standard ref types and GC typed references.
    /// </summary>
    private static void ReadAndValidateRefType(WasmBinaryReader reader)
    {
        var vt = ReadValType(reader);
        if (vt is ValType.I32 or ValType.I64 or ValType.F32 or ValType.F64 or ValType.V128)
            throw new WasmFormatException("malformed reference type");
    }

    private static void ReadAndCollectU32Leb128(WasmBinaryReader reader, List<byte> bytes)
    {
        for (int i = 0; i < 5; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            if (i == 4 && (b & 0xF0) != 0)
                throw new WasmFormatException("Unsigned LEB128 value overflows uint32");
            if ((b & 0x80) == 0) return;
        }
        throw new WasmFormatException("Unsigned LEB128 encoding exceeds 5 bytes for uint32");
    }

    /// <summary>Collects the raw bytes of a heap type (s33 LEB128) into a byte list.</summary>
    private static void ReadAndCollectHeapType(WasmBinaryReader reader, List<byte> bytes)
    {
        // Heap types are s33 LEB128 — up to 5 bytes
        for (int i = 0; i < 5; i++)
        {
            byte b = reader.ReadByte();
            bytes.Add(b);
            if ((b & 0x80) == 0) return;
        }
        throw new WasmFormatException("Heap type LEB128 encoding exceeds 5 bytes");
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
    /// Validates a function body byte array: verifies all LEB128 values are well-formed,
    /// rejects opcode 0xFF (illegal), and ensures the body ends with END (0x0B).
    /// </summary>
    private static void ValidateCodeBody(byte[] body, bool hasDataCountSection, WasmFeatures features)
    {
        var ms = new MemoryStream(body);
        var r = new WasmBinaryReader(ms);
        byte term = ValidateExpression(r, hasDataCountSection, features);
        if (term != 0x0B)
            throw new WasmFormatException("END opcode expected in function body");
        if (r.HasMore)
            throw new WasmFormatException("Unexpected bytes after function body END opcode");
    }

    /// <summary>
    /// Parses a sequence of instructions until END (0x0B) or ELSE (0x05).
    /// Returns the terminating opcode.
    /// </summary>
    private static byte ValidateExpression(WasmBinaryReader r, bool hasDataCountSection, WasmFeatures features)
    {
        while (true)
        {
            if (!r.HasMore)
                throw new WasmFormatException("unexpected end of section or function");

            byte op = r.ReadByte();

            if (op == 0x0B) return 0x0B; // end
            if (op == 0x05)               // else — only valid inside an if
                return 0x05;

            switch (op)
            {
                case 0xFF:
                    throw new WasmFormatException("illegal opcode ff");

                // No-immediate ops
                case 0x00: case 0x01:           // unreachable, nop
                case 0x0F:                       // return
                case 0x1A: case 0x1B:           // drop, select (untyped)
                    break;
                case 0x0A:                       // throw_ref (no immediates)
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "throw_ref opcode");
                    break;

                // throw — tag index
                case 0x08:
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "throw opcode");
                    r.ReadU32Leb128(); break;

                // try_table — blocktype + handler vector + nested expression
                case 0x1F:
                {
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "try_table opcode");
                    r.ReadS32Leb128(); // blocktype
                    uint handlerCount = r.ReadU32Leb128();
                    for (uint h = 0; h < handlerCount; h++)
                    {
                        byte catchKind = r.ReadByte();
                        if (catchKind is 0x00 or 0x01) // catch/catch_ref have tag index
                            r.ReadU32Leb128();
                        r.ReadU32Leb128(); // label depth
                    }
                    byte t = ValidateExpression(r, hasDataCountSection, features);
                    if (t != 0x0B)
                        throw new WasmFormatException("Expected END after try_table");
                    break;
                }

                // Block / loop — blocktype (s32 LEB128) + nested expression
                case 0x02: case 0x03:
                {
                    r.ReadS32Leb128(); // blocktype
                    byte t = ValidateExpression(r, hasDataCountSection, features);
                    if (t != 0x0B)
                        throw new WasmFormatException("Expected END after block/loop");
                    break;
                }

                // If — blocktype + then-arm + optional else-arm
                case 0x04:
                {
                    r.ReadS32Leb128(); // blocktype
                    byte t = ValidateExpression(r, hasDataCountSection, features);
                    if (t == 0x05)
                        t = ValidateExpression(r, hasDataCountSection, features);
                    if (t != 0x0B)
                        throw new WasmFormatException("Expected END after if");
                    break;
                }

                // Branch
                case 0x0C: case 0x0D:   // br, br_if
                    r.ReadU32Leb128();
                    break;
                case 0x0E:               // br_table
                {
                    uint n = r.ReadU32Leb128();
                    for (uint i = 0; i <= n; i++) r.ReadU32Leb128(); // n targets + default
                    break;
                }

                // Call
                case 0x10:
                    r.ReadU32Leb128(); break;
                case 0x11:
                    r.ReadU32Leb128(); r.ReadU32Leb128(); break; // call_indirect: typeidx + tableidx
                case 0x12:
                    RequireFeature(features, WasmFeatures.TailCalls, "return_call opcode");
                    r.ReadU32Leb128(); break; // return_call funcidx
                case 0x13:
                    RequireFeature(features, WasmFeatures.TailCalls, "return_call_indirect opcode");
                    r.ReadU32Leb128(); r.ReadU32Leb128(); break; // return_call_indirect: typeidx + tableidx
                case 0x14:
                    r.ReadU32Leb128(); break; // call_ref: type index
                case 0x15:
                    RequireFeature(features, WasmFeatures.TailCalls, "return_call_ref opcode");
                    r.ReadU32Leb128(); break;

                // Select with explicit types (Wasm 2.0)
                case 0x1C:
                {
                    uint n = r.ReadU32Leb128();
                    for (uint i = 0; i < n; i++) ReadValType(r); // valtypes (may be multi-byte GC refs)
                    break;
                }

                // Variable instructions
                case 0x20: case 0x21: case 0x22: // local.get/set/tee
                case 0x23: case 0x24:             // global.get/set
                case 0x25: case 0x26:             // table.get/set
                    r.ReadU32Leb128();
                    break;

                // Memory load/store: memarg
                case >= 0x28 and <= 0x3E:
                    SkipMemArg(r);
                    break;

                // memory.size / memory.grow: memory index (u32 LEB128, 0x00 for default)
                case 0x3F: case 0x40:
                    r.ReadU32Leb128();
                    break;

                // Constants
                case 0x41: r.ReadS32Leb128(); break; // i32.const
                case 0x42: r.ReadS64Leb128(); break; // i64.const
                case 0x43: r.ReadBytes(4); break;     // f32.const
                case 0x44: r.ReadBytes(8); break;     // f64.const

                // Numeric ops 0x45–0xC4: no immediates
                case >= 0x45 and <= 0xC4:
                    break;

                // Ref instructions
                case 0xD0: r.ReadS33Leb128(); break; // ref.null heaptype (s33 for GC)
                case 0xD1:            break;    // ref.is_null
                case 0xD2: r.ReadU32Leb128(); break; // ref.func
                case 0xD3:            break;    // ref.as_non_null
                case 0xD4: case 0xD5:           // br_on_null / br_on_non_null
                    r.ReadU32Leb128(); break;

                // Prefixed instruction set 0xFC
                case 0xFC:
                {
                    uint subOp = r.ReadU32Leb128();
                    if (subOp is >= 8 and <= 17)
                        RequireFeature(features, WasmFeatures.BulkMemory, $"bulk-memory op (0xFC:{subOp})");
                    switch (subOp)
                    {
                        case 0: case 1: case 2: case 3:
                        case 4: case 5: case 6: case 7:
                            break; // trunc_sat variants — no further immediates
                        case 8:  // memory.init — requires data count section
                            if (!hasDataCountSection)
                                throw new WasmFormatException("data count section required");
                            r.ReadU32Leb128(); r.ReadByte();  break;
                        case 9:  // data.drop — requires data count section
                            if (!hasDataCountSection)
                                throw new WasmFormatException("data count section required");
                            r.ReadU32Leb128();                break;
                        case 10: r.ReadByte(); r.ReadByte();        break; // memory.copy
                        case 11: r.ReadByte();                      break; // memory.fill
                        case 12: r.ReadU32Leb128(); r.ReadU32Leb128(); break; // table.init
                        case 13: r.ReadU32Leb128();                break; // elem.drop
                        case 14: r.ReadU32Leb128(); r.ReadU32Leb128(); break; // table.copy
                        case 15: case 16: case 17: r.ReadU32Leb128(); break; // table.grow/size/fill
                        default:
                            throw new WasmFormatException($"Unknown 0xFC sub-opcode: {subOp}");
                    }
                    break;
                }

                // SIMD prefix (0xFD)
                case 0xFD:
                {
                    RequireFeature(features, WasmFeatures.Simd, "SIMD instruction (0xFD prefix)");
                    uint subOp = r.ReadU32Leb128();
                    if (subOp is >= 0x100 and <= 0x113)
                        RequireFeature(features, WasmFeatures.RelaxedSimd, $"Relaxed SIMD instruction (0xFD 0x{subOp:X})");
                    SkipSimdImmediate(r, subOp);
                    break;
                }

                // GC prefix (0xFB)
                case 0xFB:
                {
                    RequireFeature(features, WasmFeatures.Gc, "GC instruction (0xFB prefix)");
                    uint subOp = r.ReadU32Leb128();
                    SkipGcImmediate(r, subOp);
                    break;
                }

                // Legacy exception handling
                case 0x06: // try (legacy) — blocktype + nested expression ending with catch/catch_all/delegate/end
                {
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "try opcode");
                    r.ReadS32Leb128(); // blocktype
                    byte t = ValidateExpression(r, hasDataCountSection, features);
                    // After try body: expect catch (0x07), catch_all (0x19), delegate (0x18), or end (0x0B)
                    while (t == 0x07 || t == 0x19)
                    {
                        // catch body ends at another catch/catch_all/end
                        t = ValidateExpression(r, hasDataCountSection, features);
                    }
                    // t should be 0x0B (end) or 0x18 (delegate handled during expression scan)
                    if (t != 0x0B)
                        throw new WasmFormatException("Expected END after try block");
                    break;
                }
                case 0x07: // catch (legacy) — tag index; acts like end/else separator
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "catch opcode");
                    r.ReadU32Leb128();
                    return 0x07;
                case 0x09: // rethrow (legacy) — label depth
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "rethrow opcode");
                    r.ReadU32Leb128();
                    break;
                case 0x18: // delegate (legacy) — label depth; terminates try
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "delegate opcode");
                    r.ReadU32Leb128();
                    return 0x0B; // acts as end
                case 0x19: // catch_all (legacy) — no immediates; acts like catch separator
                    RequireFeature(features, WasmFeatures.ExceptionHandling, "catch_all opcode");
                    return 0x19;

                // Stack-switching prefix
                case 0xE0:
                    RequireFeature(features, WasmFeatures.StackSwitching, "stack-switching instruction (0xE0 prefix)");
                    SkipStackSwitchingImmediate(r);
                    break;

                // Atomic/thread prefix
                case 0xFE:
                    RequireFeature(features, WasmFeatures.Threads, "atomic/threads instruction (0xFE prefix)");
                    throw new WasmFormatException("Thread instructions are not supported by the binary validator yet");

                default:
                    throw new WasmFormatException($"Unknown opcode: 0x{op:X2}");
            }
        }
    }

    private static void RequireFeature(WasmFeatures enabled, WasmFeatures required, string context)
    {
        if (!enabled.HasFlag(required))
            throw new WasmFeatureException(required, context);
    }

    private static void SkipStackSwitchingImmediate(WasmBinaryReader r)
    {
        uint subOp = r.ReadU32Leb128();
        switch (subOp)
        {
            case 0x00:
                r.ReadU32Leb128();
                break;
            case 0x01:
            case 0x04:
                r.ReadU32Leb128();
                r.ReadU32Leb128();
                break;
            case 0x02:
            {
                r.ReadU32Leb128();
                uint handlerCount = r.ReadU32Leb128();
                for (uint i = 0; i < handlerCount; i++)
                {
                    r.ReadU32Leb128();
                    r.ReadU32Leb128();
                }
                break;
            }
            case 0x03:
                r.ReadU32Leb128();
                break;
            default:
                throw new WasmFormatException($"Unknown 0xE0 sub-opcode: {subOp}");
        }
    }

    /// <summary>
    /// Skips a memarg: alignment (u32), an optional memory index when bit 6 of the alignment is set
    /// (multi-memory), and the offset. The offset is read as u64 because memory64 offsets may exceed
    /// 32 bits; the reader does not track which memory an instruction targets.
    /// </summary>
    private static void SkipMemArg(WasmBinaryReader r)
    {
        uint align = r.ReadU32Leb128();
        if ((align & 0x40) != 0)
            r.ReadU32Leb128();
        r.ReadU64Leb128();
    }

    /// <summary>
    /// Skips the immediates for a SIMD instruction (0xFD prefix).
    /// </summary>
    private static void SkipSimdImmediate(WasmBinaryReader r, uint subOp)
    {
        switch (subOp)
        {
            // v128.load variants — memarg (align, offset)
            case 0x00: // v128.load
            case 0x01: case 0x02: case 0x03: case 0x04: // load extend variants
            case 0x05: case 0x06: // load extend variants
            case 0x07: case 0x08: case 0x09: case 0x0A: // load splat variants
            case 0x0B: // v128.store
            case 0x5C: case 0x5D: // load_zero variants
                SkipMemArg(r);
                break;

            // v128.const — 16 raw bytes
            case 0x0C:
                r.ReadBytes(16);
                break;

            // i8x16.shuffle — 16 lane index bytes
            case 0x0D:
                r.ReadBytes(16);
                break;

            // extract_lane / replace_lane — 1 lane index byte
            case 0x15: case 0x16: case 0x17: // i8x16 extract/replace
            case 0x18: case 0x19: case 0x1A: // i16x8 extract/replace
            case 0x1B: case 0x1C: // i32x4 extract/replace
            case 0x1D: case 0x1E: // i64x2 extract/replace
            case 0x1F: case 0x20: // f32x4 extract/replace
            case 0x21: case 0x22: // f64x2 extract/replace
                r.ReadByte();
                break;

            // load_lane / store_lane — memarg + lane index
            case 0x54: case 0x55: case 0x56: case 0x57: // load lane
            case 0x58: case 0x59: case 0x5A: case 0x5B: // store lane
                SkipMemArg(r);
                r.ReadByte(); // lane
                break;

            // All other SIMD ops have no immediates beyond the sub-opcode
            default:
                break;
        }
    }

    /// <summary>
    /// Skips the immediates for a GC instruction (0xFB prefix).
    /// </summary>
    private static void SkipGcImmediate(WasmBinaryReader r, uint subOp)
    {
        switch (subOp)
        {
            // struct.new typeidx
            case 0x00: r.ReadU32Leb128(); break;
            // struct.new_default typeidx
            case 0x01: r.ReadU32Leb128(); break;
            // struct.get typeidx fieldidx
            case 0x02: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // struct.get_s typeidx fieldidx
            case 0x03: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // struct.get_u typeidx fieldidx
            case 0x04: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // struct.set typeidx fieldidx
            case 0x05: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.new typeidx
            case 0x06: r.ReadU32Leb128(); break;
            // array.new_default typeidx
            case 0x07: r.ReadU32Leb128(); break;
            // array.new_fixed typeidx length
            case 0x08: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.new_data typeidx dataidx
            case 0x09: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.new_elem typeidx elemidx
            case 0x0A: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.get typeidx
            case 0x0B: r.ReadU32Leb128(); break;
            // array.get_s typeidx
            case 0x0C: r.ReadU32Leb128(); break;
            // array.get_u typeidx
            case 0x0D: r.ReadU32Leb128(); break;
            // array.set typeidx
            case 0x0E: r.ReadU32Leb128(); break;
            // array.len — no immediates
            case 0x0F: break;
            // array.fill typeidx
            case 0x10: r.ReadU32Leb128(); break;
            // array.copy typeidx typeidx
            case 0x11: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.init_data typeidx dataidx
            case 0x12: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // array.init_elem typeidx elemidx
            case 0x13: r.ReadU32Leb128(); r.ReadU32Leb128(); break;
            // ref.test (null?) heaptype
            case 0x14: r.ReadS33Leb128(); break;
            // ref.test null heaptype
            case 0x15: r.ReadS33Leb128(); break;
            // ref.cast (null?) heaptype
            case 0x16: r.ReadS33Leb128(); break;
            // ref.cast null heaptype
            case 0x17: r.ReadS33Leb128(); break;
            // br_on_cast flags labelidx heaptype heaptype
            case 0x18: r.ReadByte(); r.ReadU32Leb128(); r.ReadS33Leb128(); r.ReadS33Leb128(); break;
            // br_on_cast_fail flags labelidx heaptype heaptype
            case 0x19: r.ReadByte(); r.ReadU32Leb128(); r.ReadS33Leb128(); r.ReadS33Leb128(); break;
            // any.convert_extern — no immediates
            case 0x1A: break;
            // extern.convert_any — no immediates
            case 0x1B: break;
            // ref.i31 — no immediates
            case 0x1C: break;
            // i31.get_s — no immediates
            case 0x1D: break;
            // i31.get_u — no immediates
            case 0x1E: break;
            // All other GC ops — assume no immediates
            default: break;
        }
    }
}
