// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// Live item sets in the merged index space, produced by dead code elimination.
/// A null DceLiveSet means DCE was disabled.
/// </summary>
public sealed class DceLiveSet
{
    // Original merged-index live sets (before compaction)
    public HashSet<uint>? LiveFunctions    { get; init; }
    public HashSet<uint>? LiveGlobals      { get; init; }
    public HashSet<uint>? LiveDataSegments { get; init; }
    public HashSet<uint>? LiveElements     { get; init; }
    public HashSet<uint>? LiveTags         { get; init; }

    // Per-module local indices that are dead (populated during ComposeCompactionIntoIndexMaps)
    internal List<HashSet<uint>>? DeadFunctionsByModule  { get; set; }
    internal List<HashSet<uint>>? DeadGlobalsByModule    { get; set; }
    internal List<HashSet<uint>>? DeadDataSegsByModule   { get; set; }
    internal List<HashSet<uint>>? DeadTagsByModule       { get; set; }
    internal List<HashSet<uint>>? DeadElementsByModule   { get; set; }
}
