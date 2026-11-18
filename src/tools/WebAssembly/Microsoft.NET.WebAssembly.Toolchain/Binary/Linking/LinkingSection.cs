// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Linking;

using System.Collections.Generic;

public sealed class LinkingSection
{
    /// <summary>The <c>linking</c> section version LLVM writes and this toolchain reads and writes.</summary>
    public const uint CurrentVersion = 2;

    public uint Version { get; init; }
    public List<WasmSymbol> Symbols { get; init; } = [];
    public List<SegmentInfo> SegmentInfos { get; init; } = [];
    public List<InitFunction> InitFunctions { get; init; } = [];
    public List<ComdatGroup> ComdatGroups { get; init; } = [];
}

public sealed class SegmentInfo
{
    public string Name { get; init; } = string.Empty;
    public uint Alignment { get; init; }
    public SegmentFlags Flags { get; init; }
}

public sealed class InitFunction
{
    public uint Priority { get; init; }
    public uint SymbolIndex { get; init; }
}

public sealed class ComdatGroup
{
    public string Name { get; init; } = string.Empty;
    public uint Flags { get; init; }
    public List<ComdatEntry> Entries { get; init; } = [];
}

/// <summary>A member of a COMDAT group: a function, data segment, global, tag, table or custom section, by index.</summary>
public readonly record struct ComdatEntry(ComdatKind Kind, uint Index);

/// <summary>
/// The flags of a COMDAT group. LLVM defines none and rejects any set bit, so <see cref="None"/> is the only value
/// the encoder writes.
/// </summary>
[Flags]
public enum ComdatFlags : uint
{
    None = 0,
}

[Flags]
public enum SegmentFlags : uint
{
    None = 0,
    Strings = 0x01,
    Tls = 0x02,
    Retain = 0x04,
}

public enum ComdatKind : byte
{
    Data = 0,
    Function = 1,
    Global = 2,
    Event = 3,
    Tag = Event,
    Table = 4,
    Section = 5,
}

/// <summary>Subsection IDs of the <c>linking</c> custom section (LLVM <c>WasmSubsectionType</c>).</summary>
public enum LinkingSubsectionId : byte
{
    /// <summary><c>WASM_SEGMENT_INFO</c>: name, alignment and flags of each data segment.</summary>
    SegmentInfo = 5,

    /// <summary><c>WASM_INIT_FUNCS</c>: constructors, by priority and symbol index.</summary>
    InitFunctions = 6,

    /// <summary><c>WASM_COMDAT_INFO</c>: COMDAT groups.</summary>
    ComdatInfo = 7,

    /// <summary><c>WASM_SYMBOL_TABLE</c>: the symbol table.</summary>
    SymbolTable = 8,
}
