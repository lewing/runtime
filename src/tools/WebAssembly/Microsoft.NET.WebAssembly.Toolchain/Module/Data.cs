// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Data segment for memory initialization (§2.5.9)</summary>
public sealed class Data
{
    /// <summary>True for passive segments (no associated memory offset).</summary>
    public required bool IsPassive { get; init; }

    /// <summary>Target memory index (active segments only; -1 for passive).</summary>
    public required int MemoryIndex { get; init; }

    /// <summary>Constant-expression offset (active segments only; empty for passive).</summary>
    public required byte[] OffsetExpr { get; init; }

    public required byte[] InitData { get; init; }

    /// <summary>
    /// True if this segment originates from a read-only section (e.g. <c>.rodata</c>).
    /// Used by the coalescer to keep ro and rw segments in separate output groups.
    /// </summary>
    public bool IsReadOnly { get; init; }

    /// <summary>
    /// Byte offset of <see cref="InitData"/>[0] within the data section payload when read from a binary.
    /// <c>reloc.DATA</c> offsets in relocatable objects are relative to the same payload.
    /// </summary>
    internal uint SectionPayloadOffset { get; init; }
}
