// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary.Linking;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes the payload of a <c>reloc.*</c> custom section: the target section's index, the entry count and the
/// entries. Entries are appended one at a time, so a producer can emit them while it patches the target content.
/// </summary>
public sealed class RelocationSectionEncoder : IWasmSectionPayload
{
    private readonly MemoryStream _entries = new();
    private uint _previousOffset;

    /// <summary>Creates an encoder for relocations against the section at file index <paramref name="targetSectionIndex"/>.</summary>
    public RelocationSectionEncoder(uint targetSectionIndex)
    {
        TargetSectionIndex = targetSectionIndex;
    }

    /// <summary>The file index of the section the relocations apply to.</summary>
    public uint TargetSectionIndex { get; }

    /// <summary>The number of relocations added.</summary>
    public int Count { get; private set; }

    /// <inheritdoc />
    public long Length =>
        WasmLeb128.GetULeb128Size(TargetSectionIndex) + WasmLeb128.GetULeb128Size((ulong)Count) + _entries.Length;

    /// <inheritdoc />
    public void WriteTo(Stream destination)
    {
        WasmLeb128.WriteULeb128(destination, TargetSectionIndex);
        WasmLeb128.WriteULeb128(destination, (ulong)Count);
        _entries.WriteTo(destination);
    }

    /// <summary>
    /// Adds a relocation of the field at <paramref name="offset"/> (relative to the target section's content,
    /// so a code-section offset includes the function-count prefix) to symbol <paramref name="index"/>, or to a
    /// type index for <see cref="RelocationType.R_WASM_TYPE_INDEX_LEB"/>. Relocations must be added in
    /// ascending offset order (equal offsets are allowed), which LLVM's reader and wasm-ld require.
    /// </summary>
    /// <param name="type">The relocation type; an unknown type throws.</param>
    /// <param name="offset">
    /// The relocated field's offset in the target section's content; an offset below the previous one throws.
    /// </param>
    /// <param name="index">The symbol index, or the type index for <see cref="RelocationType.R_WASM_TYPE_INDEX_LEB"/>.</param>
    /// <param name="addend">
    /// Written only for types that carry an addend (<see cref="RelocationTypes.HasAddend"/>); a nonzero addend
    /// for any other type cannot be represented and throws.
    /// </param>
    public void Add(RelocationType type, uint offset, uint index, long addend = 0)
    {
        // GetPatchSize throws for a type outside the known set.
        RelocationTypes.GetPatchSize(type);
        bool hasAddend = RelocationTypes.HasAddend(type);
        if (!hasAddend && addend != 0)
            throw new ArgumentException($"{type} has no addend field, so addend {addend} cannot be encoded.", nameof(addend));
        if (Count > 0 && offset < _previousOffset)
            throw new ArgumentException($"Relocation offset {offset} is below the previous offset {_previousOffset}; relocations must be in offset order.", nameof(offset));

        _entries.WriteByte((byte)type);
        WasmLeb128.WriteULeb128(_entries, offset);
        WasmLeb128.WriteULeb128(_entries, index);
        if (hasAddend)
            WasmLeb128.WriteSLeb128(_entries, addend);
        _previousOffset = offset;
        Count++;
    }

    /// <summary>Encodes <paramref name="model"/> as <see cref="ModuleReader"/> models it.</summary>
    public static RelocationSectionEncoder From(RelocationSection model)
    {
        ArgumentNullException.ThrowIfNull(model);
        var encoder = new RelocationSectionEncoder(model.TargetSectionIndex);
        foreach (Relocation relocation in model.Entries)
            encoder.Add(relocation.Type, relocation.Offset, relocation.Index, relocation.Addend);
        return encoder;
    }
}
