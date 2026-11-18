// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>Subsection IDs of the <c>name</c> custom section (core spec appendix, extended-name-section proposal).</summary>
internal enum NameSubsectionId : byte
{
    Module = 0,
    Function = 1,
    Local = 2,
    Label = 3,
    Type = 4,
    Table = 5,
    Memory = 6,
    Global = 7,
    ElemSegment = 8,
    DataSegment = 9,
    Field = 10,
    Tag = 11,
}
