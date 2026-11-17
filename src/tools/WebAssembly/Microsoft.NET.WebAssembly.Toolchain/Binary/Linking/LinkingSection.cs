// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Linking;

using System.Collections.Generic;

public sealed class LinkingSection
{
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

public sealed class ComdatEntry
{
    public ComdatKind Kind { get; init; }
    public uint Index { get; init; }
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
