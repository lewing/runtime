// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using Microsoft.NET.WebAssembly.Types;

/// <summary>A function defined in the module</summary>
public sealed partial class WasmFunction
{
    private byte[] _body = [];
    private readonly Dictionary<int, BranchHint> _branchHints = [];

    public required int TypeIndex { get; init; }
    public required List<ValType> Locals { get; set; }
    public List<Microsoft.NET.WebAssembly.Types.ValueType> ExactLocals { get; set; } = [];
    public required byte[] Body
    {
        get => _body;
        set
        {
            _body = value;
            _branchHints.Clear();
            OriginalLocalsByteLength = 0;
            OnBodyChanged();
        }
    }

    partial void OnBodyChanged();

    /// <summary>
    /// Branch hints keyed by zero-based decoded instruction ordinal. Replacing <see cref="Body"/>
    /// invalidates this collection.
    /// </summary>
    public IReadOnlyDictionary<int, BranchHint> BranchHints => _branchHints;

    /// <summary>Attach or replace a branch hint for an instruction ordinal.</summary>
    public void SetBranchHint(int instructionOrdinal, BranchHintKind kind)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(instructionOrdinal);
        if (kind is not BranchHintKind.Unlikely and not BranchHintKind.Likely)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Branch hint must be unlikely or likely.");
        _branchHints[instructionOrdinal] = new BranchHint(instructionOrdinal, kind);
    }

    /// <summary>Remove all branch hints from this function.</summary>
    public void ClearBranchHints() => _branchHints.Clear();

    internal void ReplaceBranchHints(IEnumerable<BranchHint> hints)
    {
        _branchHints.Clear();
        foreach (var hint in hints)
        {
            if (hint.InstructionOrdinal < 0)
                throw new ArgumentException(
                    $"Negative branch-hint instruction ordinal {hint.InstructionOrdinal}.",
                    nameof(hints));
            if (hint.Kind is not BranchHintKind.Unlikely and not BranchHintKind.Likely)
                throw new ArgumentException($"Invalid branch-hint kind {hint.Kind}.", nameof(hints));
            if (!_branchHints.TryAdd(hint.InstructionOrdinal, hint))
                throw new ArgumentException(
                    $"Duplicate branch-hint instruction ordinal {hint.InstructionOrdinal}.",
                    nameof(hints));
        }
    }

    internal void CopyBranchHintsFrom(WasmFunction source) =>
        ReplaceBranchHints(source._branchHints.Values);

    /// <summary>
    /// Original encoded locals-vector length. It is provenance for metadata decoding only and is
    /// cleared whenever <see cref="Body"/> changes.
    /// </summary>
    internal uint OriginalLocalsByteLength { get; set; }

    /// <summary>
    /// Byte offset of this function's entry (size varint position) within the code section payload.
    /// Used by DCE to map relocation offsets to function indices.
    /// </summary>
    internal uint CodeSectionOffset { get; set; }

    /// <summary>
    /// Byte offset of the first expression byte (<see cref="Body"/>[0]) within the code section payload.
    /// Used to map DWARF .debug_line addresses to instruction indices.
    /// </summary>
    internal uint BodyCodeSectionOffset { get; set; }

    /// <summary>
    /// Byte offset of this function's locals vector (the first byte after the size varint) within
    /// the code section payload — the address LLVM's wasm DWARF gives the function as
    /// <c>DW_AT_low_pc</c>. Set only when the function is read from a binary (0 otherwise, which no
    /// real function can have: the code section starts with its count varint), and, unlike
    /// <see cref="OriginalLocalsByteLength"/>, not cleared when <see cref="Body"/> is replaced: a
    /// hot-swap changes the body, not the binary layout DWARF describes.
    /// </summary>
    internal uint LocalsCodeSectionOffset { get; set; }
}
