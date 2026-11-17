// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Memory type (§2.3.6)</summary>
public sealed record MemoryType(Limits Limits, bool Shared = false)
{
    /// <summary>True for a memory64 memory (i64 linear addressing; memarg offsets are u64).</summary>
    public bool Is64 => Limits.Is64;
}
