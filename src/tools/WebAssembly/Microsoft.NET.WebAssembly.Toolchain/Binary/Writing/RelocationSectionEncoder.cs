// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary.Linking;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes the payload of a <c>reloc.*</c> custom section: the target section's index, the entry count and the
/// entries. Entries are appended one at a time, so a producer can emit them while it patches the target content.
/// </summary>
/// <remarks>
/// <para>The writing API uses three position bases:</para>
/// <list type="table">
/// <listheader><term>Base</term><description>Used by</description></listheader>
/// <item>
/// <term>Module offset (<see cref="long"/>): the byte's offset in the file.</term>
/// <description><see cref="WasmModuleWriter.Position"/>, which counts the writer's <c>startOffset</c>.</description>
/// </item>
/// <item>
/// <term>Writer-relative (<see cref="long"/>): bytes since the writer was created.</term>
/// <description>
/// <see cref="WasmCodeWriter.Position"/>, <see cref="WasmModuleEntryWriter.Position"/> and the positions their
/// methods return.
/// </description>
/// </item>
/// <item>
/// <term>Section-relative (<see cref="uint"/>): the byte's offset in the target section's content.</term>
/// <description>
/// The <c>offset</c> of <see cref="Add"/>. For a known section the content starts after the size field, so it
/// includes the vector count; for a custom section it starts after the name.
/// </description>
/// </item>
/// </list>
/// <para>
/// <see cref="SectionOffset"/> converts a writer-relative position to a section-relative offset. A
/// <see cref="WasmModuleEntryWriter"/> started at the section's content, with <see cref="WasmModuleEntryWriter.Count"/>
/// written first, already returns section-relative positions.
/// </para>
/// </remarks>
public sealed class RelocationSectionEncoder : IWasmSectionPayload
{
    private readonly SegmentedBuffer _entries = new();
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
        ArgumentNullException.ThrowIfNull(destination);
        Span<byte> prefix = stackalloc byte[2 * WasmLeb128.MaxLength32];
        int length = WasmLeb128.WriteULeb128(prefix, TargetSectionIndex);
        length += WasmLeb128.WriteULeb128(prefix[length..], (ulong)Count);
        destination.Write(prefix[..length]);
        _entries.CopyContentTo(destination);
    }

    /// <summary>
    /// Adds a relocation of the field at section-relative <paramref name="offset"/> (relative to the target section's
    /// content, so a code-section offset includes the function-count prefix, and a custom-section offset starts
    /// after its name) to symbol <paramref name="index"/>, or to a
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
        _entries.WriteULeb128(offset);
        _entries.WriteULeb128(index);
        if (hasAddend)
            _entries.WriteSLeb128(addend);
        _previousOffset = offset;
        Count++;
    }

    /// <summary>
    /// The section-relative offset, for <see cref="Add"/>, of a field at writer-relative <paramref name="position"/>
    /// of a writer whose first byte is <paramref name="writerStart"/> bytes into the target section's content. For a
    /// <see cref="WasmCodeWriter"/> writing one function body, that is the code section's body count, the bodies
    /// before it and its own size field.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// Either value is negative, or the offset does not fit in 32 bits, as <c>reloc.*</c> offsets must.
    /// </exception>
    public static uint SectionOffset(long writerStart, long position)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(writerStart);
        ArgumentOutOfRangeException.ThrowIfNegative(position);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(writerStart, uint.MaxValue);
        if (position > uint.MaxValue - writerStart)
            throw new ArgumentOutOfRangeException(nameof(position), position, $"Offset {writerStart} + {position} does not fit in 32 bits.");
        return (uint)(writerStart + position);
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
