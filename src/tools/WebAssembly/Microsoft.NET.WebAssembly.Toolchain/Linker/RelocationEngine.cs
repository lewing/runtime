// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;

/// <summary>
/// Single-module symbol accessor used during relocation patching.
/// Wraps a <see cref="WasmSymbol"/> list (from one input module's linking section)
/// and provides bounds-checked index lookup.
/// </summary>
public sealed class RelocSymbolTable
{
    private readonly IReadOnlyList<WasmSymbol> _symbols;

    public RelocSymbolTable(IReadOnlyList<WasmSymbol> symbols)
    {
        ArgumentNullException.ThrowIfNull(symbols);
        _symbols = symbols;
    }

    public RelocSymbolTable(LinkingSection linkingSection)
        : this(linkingSection?.Symbols ?? throw new ArgumentNullException(nameof(linkingSection)))
    {
    }

    public int Count => _symbols.Count;
    public IReadOnlyList<WasmSymbol> Symbols => _symbols;

    public WasmSymbol this[uint index]
    {
        get
        {
            if (index >= (uint)_symbols.Count)
                throw new KeyNotFoundException(
                    $"Symbol index {index} is out of range for symbol table of size {_symbols.Count}.");
            return _symbols[(int)index];
        }
    }
}

public static class RelocationEngine
{
    /// <param name="sectionBytes">Payload of the section the relocations target.</param>
    /// <param name="relocations">Relocations whose offsets are relative to <paramref name="sectionBytes"/>.</param>
    /// <param name="indexMap">Maps object-local indices to output indices.</param>
    /// <param name="symbols">The object's symbol table.</param>
    /// <param name="segmentBaseAddresses">Linear-memory address of each data segment.</param>
    /// <param name="siteAddress">
    /// Maps a section offset to the linear-memory address of those bytes. Required only for
    /// <c>R_WASM_MEMORY_ADDR_LOCREL_I32</c>, which is relative to its own location.
    /// </param>
    public static void ApplyRelocations(
        byte[] sectionBytes,
        IReadOnlyList<Relocation> relocations,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses,
        Func<uint, uint>? siteAddress = null)
    {
        ArgumentNullException.ThrowIfNull(sectionBytes);
        ArgumentNullException.ThrowIfNull(relocations);
        ArgumentNullException.ThrowIfNull(indexMap);
        ArgumentNullException.ThrowIfNull(symbols);
        ArgumentNullException.ThrowIfNull(segmentBaseAddresses);

        var buffer = sectionBytes.AsSpan();

        foreach (var reloc in relocations)
        {
            ulong resolvedValue = ResolveRelocation(reloc, indexMap, symbols, segmentBaseAddresses, siteAddress);
            PatchAt(buffer, checked((int)reloc.Offset), reloc.Type, resolvedValue);
        }
    }

    /// <summary>
    /// Applies a single relocation to a <see cref="Span{T}"/> in place.
    /// Used by <see cref="SectionMerger"/> for per-relocation error isolation without
    /// allocating a single-element collection wrapper.
    /// </summary>
    internal static void ApplyRelocation(
        Span<byte> sectionBytes,
        Relocation reloc,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses,
        Func<uint, uint>? siteAddress = null)
    {
        ulong resolvedValue = ResolveRelocation(reloc, indexMap, symbols, segmentBaseAddresses, siteAddress);
        PatchAt(sectionBytes, checked((int)reloc.Offset), reloc.Type, resolvedValue);
    }

    /// <summary>
    /// Attempts to apply a single DWARF relocation in place without throwing.
    /// Returns <see langword="false"/> — and leaves bytes unchanged — when any required
    /// index map entry or symbol is missing (e.g., dead-code-eliminated symbols).
    /// Used by <see cref="SectionMerger"/> to avoid exception overhead on the hot DWARF-relocation path.
    /// </summary>
    internal static bool TryApplyRelocation(
        Span<byte> sectionBytes,
        Relocation reloc,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses)
    {
        if (!TryResolveRelocation(reloc, indexMap, symbols, segmentBaseAddresses, out uint resolvedValue))
            return false;

        PatchAt(sectionBytes, checked((int)reloc.Offset), reloc.Type, resolvedValue);
        return true;
    }

    /// <summary>
    /// Writes a <see cref="uint"/> as exactly five bytes of padded unsigned LEB128.
    /// Relocatable wasm32 object files reserve this fixed-width encoding so linkers can patch in place.
    /// </summary>
    public static void WritePaddedLeb128U32(Span<byte> buf, int offset, uint value)
    {
        EnsureWritable(buf, offset, 5);

        for (int i = 0; i < 4; i++)
        {
            buf[offset + i] = (byte)((value & 0x7Fu) | 0x80u);
            value >>= 7;
        }

        buf[offset + 4] = (byte)(value & 0x7Fu);
    }

    /// <summary>
    /// Writes an <see cref="int"/> as exactly five bytes of padded signed LEB128.
    /// </summary>
    public static void WritePaddedLeb128S32(Span<byte> buf, int offset, int value)
    {
        EnsureWritable(buf, offset, 5);

        for (int i = 0; i < 4; i++)
        {
            buf[offset + i] = (byte)((value & 0x7F) | 0x80);
            value >>= 7;
        }

        buf[offset + 4] = (byte)(value & 0x7F);
    }

    private static ulong ResolveRelocation(
        Relocation reloc,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses,
        Func<uint, uint>? siteAddress) => reloc.Type switch
        {
            RelocationType.R_WASM_FUNCTION_INDEX_LEB or RelocationType.R_WASM_FUNCTION_INDEX_I32
                => ResolveMappedSymbolIndex(reloc.Index, indexMap.Functions, symbols, SymbolType.Function, "function"),

            RelocationType.R_WASM_TABLE_INDEX_SLEB or RelocationType.R_WASM_TABLE_INDEX_I32 or RelocationType.R_WASM_TABLE_INDEX_I64
                => ResolveTableIndex(reloc.Index, indexMap.Tables, symbols),

            // In a non-PIC link __memory_base is 0, so the base-relative form is the absolute address.
            RelocationType.R_WASM_MEMORY_ADDR_LEB or RelocationType.R_WASM_MEMORY_ADDR_SLEB or RelocationType.R_WASM_MEMORY_ADDR_I32
                or RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB or RelocationType.R_WASM_MEMORY_ADDR_I64
                => ResolveMemoryAddress(reloc, symbols, segmentBaseAddresses),

            RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32
                => unchecked(ResolveMemoryAddress(reloc, symbols, segmentBaseAddresses) - (siteAddress ?? throw new InvalidOperationException(
                    "R_WASM_MEMORY_ADDR_LOCREL_I32 is relative to its own address; a site-address mapping is required."))(reloc.Offset)),

            RelocationType.R_WASM_TYPE_INDEX_LEB
                => ResolveMappedIndex(reloc.Index, indexMap.Types, "type"),

            RelocationType.R_WASM_GLOBAL_INDEX_LEB or RelocationType.R_WASM_GLOBAL_INDEX_I32
                => ResolveMappedSymbolIndex(reloc.Index, indexMap.Globals, symbols, SymbolType.Global, "global"),

            RelocationType.R_WASM_FUNCTION_OFFSET_I32
                => Addend(ResolveMappedSymbolIndex(reloc.Index, indexMap.FunctionOffsets, symbols, SymbolType.Function, "function offset"), reloc.Addend),

            RelocationType.R_WASM_SECTION_OFFSET_I32
                => ResolveSectionOffset(reloc, indexMap, symbols),

            RelocationType.R_WASM_TAG_INDEX_LEB
                => ResolveMappedSymbolIndex(reloc.Index, indexMap.Tags, symbols, SymbolType.Tag, "tag"),

            _ => throw new NotSupportedException($"Relocation type '{reloc.Type}' is not yet supported by the relocation engine."),
        };

    private static void PatchAt(Span<byte> sectionBytes, int offset, RelocationType type, ulong resolvedValue)
    {
        switch (type)
        {
            case RelocationType.R_WASM_TABLE_INDEX_I64:
            case RelocationType.R_WASM_MEMORY_ADDR_I64:
                EnsureWritable(sectionBytes, offset, sizeof(ulong));
                BinaryPrimitives.WriteUInt64LittleEndian(sectionBytes[offset..(offset + sizeof(ulong))], resolvedValue);
                return;
        }

        uint value32 = unchecked((uint)resolvedValue);
        switch (type)
        {
            case RelocationType.R_WASM_FUNCTION_INDEX_LEB:
            case RelocationType.R_WASM_MEMORY_ADDR_LEB:
            case RelocationType.R_WASM_TYPE_INDEX_LEB:
            case RelocationType.R_WASM_GLOBAL_INDEX_LEB:
            case RelocationType.R_WASM_TAG_INDEX_LEB:
                WritePaddedLeb128U32(sectionBytes, offset, value32);
                break;

            case RelocationType.R_WASM_TABLE_INDEX_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB:
                WritePaddedLeb128S32(sectionBytes, offset, unchecked((int)value32));
                break;

            case RelocationType.R_WASM_TABLE_INDEX_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_I32:
            case RelocationType.R_WASM_GLOBAL_INDEX_I32:
            case RelocationType.R_WASM_FUNCTION_OFFSET_I32:
            case RelocationType.R_WASM_SECTION_OFFSET_I32:
            case RelocationType.R_WASM_FUNCTION_INDEX_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_LOCREL_I32:
                EnsureWritable(sectionBytes, offset, sizeof(uint));
                BinaryPrimitives.WriteUInt32LittleEndian(sectionBytes[offset..(offset + sizeof(uint))], value32);
                break;

            default:
                throw new NotSupportedException($"Relocation type '{type}' is not yet supported by the relocation patcher.");
        }
    }

    private static uint ResolveMappedSymbolIndex(
        uint symbolTableIndex,
        IDictionary<uint, uint> map,
        RelocSymbolTable symbols,
        SymbolType expectedType,
        string kind)
    {
        WasmSymbol symbol = symbols[symbolTableIndex];

        if ((symbol.Flags & SymbolFlags.Undefined) != 0)
            throw new InvalidOperationException(
                $"Cannot resolve {kind} relocation against undefined symbol '{symbol.Name ?? $"#{symbolTableIndex}"}'.");

        if (symbol.Type != expectedType)
            throw new InvalidOperationException(
                $"Symbol '{symbol.Name ?? $"#{symbolTableIndex}"}' is of type {symbol.Type}, not {expectedType}, so it cannot satisfy a {kind} relocation.");

        if (!symbol.Index.HasValue)
            throw new InvalidOperationException(
                $"Symbol '{symbol.Name ?? $"#{symbolTableIndex}"}' does not carry a {kind} index.");

        return ResolveMappedIndex(checked((uint)symbol.Index.Value), map, kind);
    }

    private static uint ResolveTableIndex(uint symbolTableIndex, IDictionary<uint, uint> map, RelocSymbolTable symbols)
    {
        WasmSymbol symbol = symbols[symbolTableIndex];

        if ((symbol.Flags & SymbolFlags.Undefined) != 0)
            return 0;

        if (symbol.Type is not SymbolType.Table and not SymbolType.Function)
            throw new InvalidOperationException(
                $"Symbol '{symbol.Name ?? $"#{symbolTableIndex}"}' is of type {symbol.Type}, not Table or Function, so it cannot satisfy a table relocation.");

        if (!symbol.Index.HasValue)
            throw new InvalidOperationException(
                $"Symbol '{symbol.Name ?? $"#{symbolTableIndex}"}' does not carry a table/function index.");

        return ResolveMappedIndex(checked((uint)symbol.Index.Value), map, "table");
    }

    private static uint ResolveMemoryAddress(
        Relocation reloc,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses)
    {
        WasmSymbol symbol = symbols[reloc.Index];

        if ((symbol.Flags & SymbolFlags.Undefined) != 0)
            return Addend(0, reloc.Addend);

        uint symbolOffset = symbol.Offset ?? 0;

        if ((symbol.Flags & SymbolFlags.Absolute) != 0)
            return Addend(symbolOffset, reloc.Addend);

        if (!symbol.SegmentIndex.HasValue)
            throw new InvalidOperationException(
                $"Data symbol '{symbol.Name ?? $"#{reloc.Index}"}' does not have a segment index required for a memory relocation.");

        if (!segmentBaseAddresses.TryGetValue(symbol.SegmentIndex.Value, out uint segmentBase))
            throw new KeyNotFoundException(
                $"No base address was provided for data segment {symbol.SegmentIndex.Value} needed by symbol '{symbol.Name ?? $"#{reloc.Index}"}'.");

        return Addend(segmentBase + symbolOffset, reloc.Addend);
    }

    private static uint ResolveSectionOffset(Relocation reloc, IndexMap indexMap, RelocSymbolTable symbols)
    {
        WasmSymbol symbol = symbols[reloc.Index];

        if (symbol.Type != SymbolType.Section)
            throw new InvalidOperationException(
                $"Symbol '{symbol.Name ?? $"#{reloc.Index}"}' is of type {symbol.Type}, not Section, so it cannot satisfy a section-offset relocation.");

        if (!symbol.SectionIndex.HasValue)
            throw new InvalidOperationException(
                $"Section symbol '{symbol.Name ?? $"#{reloc.Index}"}' does not carry a section index.");

        uint baseOffset = ResolveMappedIndex(symbol.SectionIndex.Value, indexMap.SectionOffsets, "section offset");
        return Addend(baseOffset, reloc.Addend);
    }

    private static uint ResolveMappedIndex(uint oldIndex, IDictionary<uint, uint> map, string kind)
    {
        if (map.TryGetValue(oldIndex, out uint mapped))
            return mapped;

        throw new KeyNotFoundException(
            $"No mapped {kind} value was provided for original index {oldIndex}.");
    }

    private static uint Addend(uint baseValue, int addend) => unchecked((uint)((long)baseValue + addend));

    private static void EnsureWritable(Span<byte> buffer, int offset, int width)
    {
        if (offset < 0 || offset > buffer.Length - width)
            throw new ArgumentOutOfRangeException(nameof(offset),
                $"Need {width} writable bytes at offset {offset}, but buffer length is {buffer.Length}.");
    }

    // ── Non-throwing resolution path (used by SectionMerger DWARF relocation loop) ──────────────

    private static bool TryResolveRelocation(
        Relocation reloc,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses,
        out uint result)
    {
        result = 0;
        switch (reloc.Type)
        {
            case RelocationType.R_WASM_FUNCTION_INDEX_LEB:
            case RelocationType.R_WASM_FUNCTION_INDEX_I32:
                return TryResolveMappedSymbolIndex(reloc.Index, indexMap.Functions, symbols, SymbolType.Function, out result);

            case RelocationType.R_WASM_TABLE_INDEX_SLEB:
            case RelocationType.R_WASM_TABLE_INDEX_I32:
            case RelocationType.R_WASM_TABLE_INDEX_I64:
                return TryResolveTableIndex(reloc.Index, indexMap.Tables, symbols, out result);

            case RelocationType.R_WASM_MEMORY_ADDR_LEB:
            case RelocationType.R_WASM_MEMORY_ADDR_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_I32:
            case RelocationType.R_WASM_MEMORY_ADDR_REL_SLEB:
            case RelocationType.R_WASM_MEMORY_ADDR_I64:
                return TryResolveMemoryAddress(reloc, symbols, segmentBaseAddresses, out result);

            case RelocationType.R_WASM_TYPE_INDEX_LEB:
                return TryResolveMappedIndex(reloc.Index, indexMap.Types, out result);

            case RelocationType.R_WASM_GLOBAL_INDEX_LEB:
            case RelocationType.R_WASM_GLOBAL_INDEX_I32:
                return TryResolveMappedSymbolIndex(reloc.Index, indexMap.Globals, symbols, SymbolType.Global, out result);

            case RelocationType.R_WASM_FUNCTION_OFFSET_I32:
                if (!TryResolveMappedSymbolIndex(reloc.Index, indexMap.FunctionOffsets, symbols, SymbolType.Function, out result))
                    return false;
                result = Addend(result, reloc.Addend);
                return true;

            case RelocationType.R_WASM_SECTION_OFFSET_I32:
                return TryResolveSectionOffset(reloc, indexMap, symbols, out result);

            case RelocationType.R_WASM_TAG_INDEX_LEB:
                return TryResolveMappedSymbolIndex(reloc.Index, indexMap.Tags, symbols, SymbolType.Tag, out result);

            default:
                return false;  // unsupported type — leave bytes unchanged
        }
    }

    private static bool TryResolveMappedSymbolIndex(
        uint symbolTableIndex,
        IDictionary<uint, uint> map,
        RelocSymbolTable symbols,
        SymbolType expectedType,
        out uint result)
    {
        result = 0;
        if (symbolTableIndex >= (uint)symbols.Count) return false;
        WasmSymbol symbol = symbols.Symbols[(int)symbolTableIndex];
        if ((symbol.Flags & SymbolFlags.Undefined) != 0) return false;
        if (symbol.Type != expectedType) return false;
        if (!symbol.Index.HasValue) return false;
        return TryResolveMappedIndex(checked((uint)symbol.Index.Value), map, out result);
    }

    private static bool TryResolveTableIndex(
        uint symbolTableIndex,
        IDictionary<uint, uint> map,
        RelocSymbolTable symbols,
        out uint result)
    {
        result = 0;
        if (symbolTableIndex >= (uint)symbols.Count) return false;
        WasmSymbol symbol = symbols.Symbols[(int)symbolTableIndex];
        if ((symbol.Flags & SymbolFlags.Undefined) != 0) { result = 0; return true; }
        if (symbol.Type is not SymbolType.Table and not SymbolType.Function) return false;
        if (!symbol.Index.HasValue) return false;
        return TryResolveMappedIndex(checked((uint)symbol.Index.Value), map, out result);
    }

    private static bool TryResolveMemoryAddress(
        Relocation reloc,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses,
        out uint result)
    {
        result = 0;
        if (reloc.Index >= (uint)symbols.Count) return false;
        WasmSymbol symbol = symbols.Symbols[(int)reloc.Index];
        if ((symbol.Flags & SymbolFlags.Undefined) != 0) { result = Addend(0, reloc.Addend); return true; }
        uint symbolOffset = symbol.Offset ?? 0;
        if ((symbol.Flags & SymbolFlags.Absolute) != 0) { result = Addend(symbolOffset, reloc.Addend); return true; }
        if (!symbol.SegmentIndex.HasValue) return false;
        if (!segmentBaseAddresses.TryGetValue(symbol.SegmentIndex.Value, out uint segmentBase)) return false;
        result = Addend(segmentBase + symbolOffset, reloc.Addend);
        return true;
    }

    private static bool TryResolveSectionOffset(
        Relocation reloc,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        out uint result)
    {
        result = 0;
        if (reloc.Index >= (uint)symbols.Count) return false;
        WasmSymbol symbol = symbols.Symbols[(int)reloc.Index];
        if (symbol.Type != SymbolType.Section) return false;
        if (!symbol.SectionIndex.HasValue) return false;
        if (!TryResolveMappedIndex(symbol.SectionIndex.Value, indexMap.SectionOffsets, out uint baseOffset)) return false;
        result = Addend(baseOffset, reloc.Addend);
        return true;
    }

    private static bool TryResolveMappedIndex(uint oldIndex, IDictionary<uint, uint> map, out uint mapped) =>
        map.TryGetValue(oldIndex, out mapped);
}
