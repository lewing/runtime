// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using System.Collections.Generic;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Types;

/// <summary>A parsed WebAssembly module (§2.5)</summary>
public sealed class WasmModule
{
    public List<FuncType> Types { get; init; } = [];
    public List<TypeDefinition> TypeDefinitions { get; init; } = [];
    public List<Import> Imports { get; init; } = [];
    public List<WasmFunction> Functions { get; init; } = [];
    public List<TableType> Tables { get; init; } = [];
    public List<MemoryType> Memories { get; init; } = [];
    public List<WasmGlobal> Globals { get; init; } = [];
    public List<Tag> Tags { get; init; } = [];
    public OrderedDictionary<string, Export> Exports { get; init; } = [];
    public List<Export> ExportEntries { get; init; } = [];
    public int? StartFunction { get; set; }
    public List<Element> Elements { get; init; } = [];
    public List<Data> DataSegments { get; init; } = [];
    // internal set (not init) so DwarfSidecarMerger can atomically swap the whole
    // section list on a shared, already-loaded module — a single reference write is
    // safe for concurrent readers, unlike in-place RemoveAll/Add mutation.
    public List<CustomSection> CustomSections { get; internal set; } = [];
    public LinkingSection? LinkingSection { get; set; }
    public ProducersSection? ProducersSection { get; set; }
    public TargetFeaturesSection? TargetFeaturesSection { get; set; }
    public List<RelocationSection> RelocationSections { get; init; } = [];
    internal bool HasDataCountSection { get; init; }
    internal WasmFeatures EnabledFeatures { get; set; } = WasmFeatures.Default;

    /// <summary>
    /// Gets the canonical type space for this module, synthesizing standalone function definitions
    /// only when exact type metadata is wholly absent.
    /// </summary>
    /// <remarks>
    /// Public callers may construct a module from compact <see cref="FuncType"/> entries alone.
    /// In that case each compact entry is the complete declaration of a final function type in its
    /// own recursion group. A non-empty but incomplete exact list is different: it claims canonical
    /// provenance and is malformed, so it must never fall back to compact shape equality.
    /// </remarks>
    internal bool TryGetCanonicalTypeDefinitions(out IReadOnlyList<TypeDefinition> definitions)
    {
        if (TypeDefinitions.Count == Types.Count)
        {
            definitions = TypeDefinitions;
            return true;
        }

        if (TypeDefinitions.Count != 0)
        {
            definitions = Array.Empty<TypeDefinition>();
            return false;
        }

        definitions = Types
            .Select(static (type, index) => SynthesiseStandaloneFunctionType(type, index))
            .ToArray();
        return true;
    }

    internal static TypeDefinition SynthesiseStandaloneFunctionType(FuncType compact, int index) =>
        new(
            index,
            IsFinal: true,
            SuperTypeIndices: [],
            new CompositeType.Function(
                [.. compact.Parameters.Select(Microsoft.NET.WebAssembly.Types.ValueType.FromStorageValType)],
                [.. compact.Results.Select(Microsoft.NET.WebAssembly.Types.ValueType.FromStorageValType)]),
            new RecGroupIdentity(index, index, 1));

    /// <summary>
    /// Non-fatal diagnostics from recognized custom metadata sections. Empty when metadata is
    /// absent or valid. Strict reader mode throws instead of returning these diagnostics.
    /// </summary>
    public IReadOnlyList<WasmMetadataDiagnostic> MetadataDiagnostics { get; internal set; } =
        Array.Empty<WasmMetadataDiagnostic>();

    /// <summary>
    /// DWARF line programs parsed from .debug_line, populated only when
    /// <see cref="WasmFeatures.DebugInfo"/> is set in <see cref="Binary.ModuleReaderOptions.Features"/>.
    /// </summary>
    public IReadOnlyList<DwarfLineProgram>? DebugInfo { get; internal set; }
}
