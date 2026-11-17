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
    public List<Import> Imports { get; init; } = [];
    public List<WasmFunction> Functions { get; init; } = [];
    public List<TableType> Tables { get; init; } = [];
    public List<MemoryType> Memories { get; init; } = [];
    public List<WasmGlobal> Globals { get; init; } = [];
    public List<Tag> Tags { get; init; } = [];
    public OrderedDictionary<string, Export> Exports { get; init; } = [];
    public int? StartFunction { get; set; }
    public List<Element> Elements { get; init; } = [];
    public List<Data> DataSegments { get; init; } = [];
    public List<CustomSection> CustomSections { get; init; } = [];
    public LinkingSection? LinkingSection { get; set; }
    public ProducersSection? ProducersSection { get; set; }
    public TargetFeaturesSection? TargetFeaturesSection { get; set; }
    public List<RelocationSection> RelocationSections { get; init; } = [];
    internal bool HasDataCountSection { get; init; }

    /// <summary>
    /// DWARF line programs parsed from .debug_line, populated only when
    /// <see cref="WasmFeatures.DebugInfo"/> is set in <see cref="Binary.ModuleReaderOptions.Features"/>.
    /// </summary>
    public IReadOnlyList<DwarfLineProgram>? DebugInfo { get; internal set; }
}
