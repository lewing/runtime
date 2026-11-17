// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Linking;

[Flags]
public enum SymbolFlags : uint
{
    None = 0,
    BindingWeak = 0x01,
    BindingLocal = 0x02,
    VisibilityHidden = 0x04,
    Undefined = 0x10,
    Exported = 0x20,
    ExplicitName = 0x40,
    NoStrip = 0x80,
    Tls = 0x100,
    Absolute = 0x200,
}

public enum SymbolType : byte
{
    Function = 0,
    Data = 1,
    Global = 2,
    Section = 3,
    Event = 4,
    Tag = Event,
    Table = 5,
}

public sealed class WasmSymbol
{
    public SymbolType Type { get; init; }
    public SymbolFlags Flags { get; init; }
    public string? Name { get; init; }
    public int? Index { get; init; }
    public uint? Offset { get; init; }
    public uint? Size { get; init; }
    public uint? SegmentIndex { get; init; }
    public uint? SectionIndex { get; init; }
}
