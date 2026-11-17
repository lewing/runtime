// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Whether a conditional branch is expected to be taken.</summary>
public enum BranchHintKind : byte
{
    Unlikely = 0,
    Likely = 1,
}

/// <summary>
/// Typed branch-hint metadata attached to a decoded instruction ordinal.
/// </summary>
/// <param name="InstructionOrdinal">
/// Zero-based instruction ordinal in the function expression, including structural instructions.
/// </param>
/// <param name="Kind">The branch likelihood.</param>
/// <param name="OriginalWireOffset">
/// Original byte offset from the beginning of the function locals-vector declaration, when the
/// hint came from a binary module. Writers recompute this value and never use it as authority.
/// </param>
public sealed record BranchHint(
    int InstructionOrdinal,
    BranchHintKind Kind,
    uint? OriginalWireOffset = null)
{
    public const string CustomSectionName = "metadata.code.branch_hint";
}
