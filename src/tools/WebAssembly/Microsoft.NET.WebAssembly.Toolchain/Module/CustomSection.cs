// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Custom section (§2.5.1.1)</summary>
public sealed record CustomSection(string Name, byte[] Content)
{
    /// <summary>
    /// Reader provenance used by metadata formats whose placement is constrained.
    /// Programmatically-created sections default to pre-code placement.
    /// </summary>
    internal bool AppearedAfterCode { get; init; }

    /// <summary>
    /// Zero-based index of this section among all sections of the binary it was read from, or -1. Section
    /// symbols and <c>reloc.*</c> sections in relocatable objects name their target by this index.
    /// </summary>
    internal int SectionIndex { get; init; } = -1;
}
