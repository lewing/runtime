// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Self-resolves relocations of a single relocatable Wasm object in place, without
/// multi-object merging or cross-input symbol resolution.
/// <para>
/// Mirrors the dotnet/runtime WASM-R2R single-object link step: the object's own
/// symbols are the only inputs; index maps are identity (no remapping); data segment
/// base addresses are assigned from the supplied globalBase with natural alignment.
/// Padded 5-byte LEB128 slot widths are preserved exactly.
/// </para>
/// </summary>
public static class SingleObjectRelocator
{
    /// <summary>
    /// Applies all relocations from the module's <c>reloc.*</c> custom sections to
    /// a clone of <paramref name="inputBytes"/> and returns the patched bytes.
    /// </summary>
    /// <param name="inputBytes">Raw bytes of the relocatable <c>.wasm</c> / <c>.wasm.o</c> object.</param>
    /// <param name="module">Parsed module (must include linking and relocation sections).</param>
    /// <param name="globalBase">Byte address where data placement begins (typically 0 or 1024).</param>
    /// <param name="tableBase">Starting index for the indirect function table (typically 0 or 1).</param>
    /// <param name="verboseLog">Optional callback invoked once per applied relocation.</param>
    /// <returns>Byte-identical copy of <paramref name="inputBytes"/> with relocation slots patched.</returns>
    /// <exception cref="InvalidOperationException">Thrown when the module has no linking section.</exception>
    public static byte[] Relocate(
        byte[] inputBytes,
        WasmModule module,
        uint globalBase = 0,
        uint tableBase = 0,
        Action<string>? verboseLog = null)
    {
        ArgumentNullException.ThrowIfNull(inputBytes);
        ArgumentNullException.ThrowIfNull(module);

        if (module.LinkingSection is null)
            throw new InvalidOperationException(
                "Module has no linking custom section. Only relocatable object files (.wasm.o) can be relocated.");

        if (module.RelocationSections.Count == 0)
            return (byte[])inputBytes.Clone();

        // Scan raw bytes to locate each section's payload span.
        var sectionPayloads = ScanSectionPayloads(inputBytes);

        // Build identity index map and populate offsets from the raw binary.
        var indexMap = BuildIdentityIndexMap(module, sectionPayloads);

        // Apply table base to identity tables map when tableBase != 0.
        // R_WASM_TABLE_INDEX_* relocations resolve through the Tables map, so offsetting
        // element-slot indices requires mapping each table index to tableBase + index.
        if (tableBase != 0)
            AdjustTableBaseInMap(indexMap, tableBase);

        // Compute data segment base addresses from globalBase.
        var segmentBases = ComputeSegmentBases(module, globalBase);

        var symbols = new RelocSymbolTable(module.LinkingSection);
        var outputBytes = (byte[])inputBytes.Clone();

        foreach (var relocSection in module.RelocationSections)
        {
            uint sectionIdx = relocSection.TargetSectionIndex;
            if (sectionIdx >= (uint)sectionPayloads.Count)
                continue; // defensive: unknown target section index

            var (payloadStart, payloadLength) = sectionPayloads[(int)sectionIdx];
            var span = outputBytes.AsSpan((int)payloadStart, (int)payloadLength);

            Func<uint, uint>? siteAddress = relocSection.Name == "reloc.DATA"
                ? offset => DataSiteAddress(module, segmentBases, offset)
                : null;
            foreach (var reloc in relocSection.Entries)
            {
                verboseLog?.Invoke(FormatRelocLog(reloc, symbols));
                RelocationEngine.ApplyRelocation(span, reloc, indexMap, symbols, segmentBases, siteAddress);
            }
        }

        return outputBytes;
    }

    // ── Index map ────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds an identity <see cref="IndexMap"/> for a single module.
    /// Every old index maps to itself. Also populates <see cref="IndexMap.FunctionOffsets"/>
    /// from <see cref="WasmFunction.CodeSectionOffset"/> and
    /// <see cref="IndexMap.SectionOffsets"/> from <paramref name="sectionPayloads"/>.
    /// </summary>
    internal static IndexMap BuildIdentityIndexMap(
        WasmModule module,
        IReadOnlyList<(long PayloadStart, int PayloadLength)> sectionPayloads)
    {
        var map = new IndexMap();

        // ── Function index space (imports then locals) ─────────────────────────
        uint funcIdx = 0;
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Func)
                map.Functions[funcIdx] = funcIdx++;
        for (int i = 0; i < module.Functions.Count; i++)
        {
            map.Functions[funcIdx] = funcIdx;
            map.FunctionOffsets[funcIdx] = module.Functions[i].CodeSectionOffset;
            funcIdx++;
        }

        // ── Global index space ─────────────────────────────────────────────────
        uint globalIdx = 0;
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Global)
                map.Globals[globalIdx] = globalIdx++;
        for (int i = 0; i < module.Globals.Count; i++)
            map.Globals[globalIdx] = globalIdx++;

        // ── Type index space ───────────────────────────────────────────────────
        for (uint t = 0; t < (uint)module.Types.Count; t++)
            map.Types[t] = t;

        // ── Table index space ──────────────────────────────────────────────────
        uint tableIdx = 0;
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Table)
                map.Tables[tableIdx] = tableIdx++;
        for (int i = 0; i < module.Tables.Count; i++)
            map.Tables[tableIdx] = tableIdx++;

        // ── Tag index space ────────────────────────────────────────────────────
        uint tagIdx = 0;
        foreach (var import in module.Imports)
            if (import.Descriptor is ImportDescriptor.Tag)
                map.Tags[tagIdx] = tagIdx++;
        for (int i = 0; i < module.Tags.Count; i++)
            map.Tags[tagIdx] = tagIdx++;

        // ── Data segment index space ───────────────────────────────────────────
        for (uint d = 0; d < (uint)module.DataSegments.Count; d++)
            map.DataSegments[d] = d;

        // ── Section offsets (used by R_WASM_SECTION_OFFSET_I32) ───────────────
        for (int s = 0; s < sectionPayloads.Count; s++)
            map.SectionOffsets[(uint)s] = (uint)sectionPayloads[s].PayloadStart;

        return map;
    }

    // ── Segment bases ────────────────────────────────────────────────────────────

    /// <summary>
    /// Computes data segment base addresses starting at <paramref name="globalBase"/>.
    /// Segments are laid out sequentially with natural alignment (power-of-two,
    /// derived from the linking section's segment alignment metadata when present,
    /// otherwise defaulting to 1).
    /// </summary>
    internal static Dictionary<uint, uint> ComputeSegmentBases(WasmModule module, uint globalBase)
    {
        var bases = new Dictionary<uint, uint>();
        uint cursor = globalBase;

        for (int i = 0; i < module.DataSegments.Count; i++)
        {
            var seg = module.DataSegments[i];
            if (seg.IsPassive)
                continue;

            uint alignment = GetSegmentAlignment(module, i);
            cursor = AlignUp(cursor, alignment);
            bases[(uint)i] = cursor;
            cursor += (uint)seg.InitData.Length;
        }

        return bases;
    }

    /// <summary>Linear-memory address of the byte at <paramref name="offset"/> in the data section payload.</summary>
    private static uint DataSiteAddress(WasmModule module, Dictionary<uint, uint> segmentBases, uint offset)
    {
        for (int i = 0; i < module.DataSegments.Count; i++)
        {
            var segment = module.DataSegments[i];
            uint start = segment.SectionPayloadOffset;
            if (offset >= start && offset < start + (uint)segment.InitData.Length && segmentBases.TryGetValue((uint)i, out uint baseAddress))
                return baseAddress + (offset - start);
        }

        throw new InvalidOperationException($"Data relocation offset {offset} is not inside an active data segment.");
    }

    private static uint GetSegmentAlignment(WasmModule module, int segmentIndex)
    {
        if (module.LinkingSection?.SegmentInfos is not { Count: > 0 } segmentInfos ||
            segmentIndex >= segmentInfos.Count)
        {
            return 1;
        }

        uint log2Alignment = segmentInfos[segmentIndex].Alignment;
        if (log2Alignment >= 31)
            return 1u << 31;

        return 1u << (int)log2Alignment;
    }

    private static uint AlignUp(uint value, uint align)
    {
        if (align <= 1) return value;
        uint remainder = value % align;
        return remainder == 0 ? value : checked(value + align - remainder);
    }

    // ── Table base ───────────────────────────────────────────────────────────────

    private static void AdjustTableBaseInMap(IndexMap map, uint tableBase)
    {
        // For TABLE_INDEX relocations the resolved value comes from the Tables map.
        // With a non-zero tableBase, shift each table entry by tableBase so the
        // emitted element offset reflects the actual table slot assignment.
        // NOTE: this only applies to table entries in the tables index space, not
        // the table index itself. In practice the Tables dict holds the logical
        // function-table slot index, which is tableBase + local slot.
        foreach (var key in map.Tables.Keys.ToArray())
            map.Tables[key] = map.Tables[key] + tableBase;
    }

    // ── Binary scanner ───────────────────────────────────────────────────────────

    /// <summary>
    /// Scans a Wasm binary and returns the byte offset and length of each section's
    /// payload (the bytes after the section-size LEB128).
    /// Index <c>i</c> corresponds to the <c>i</c>-th section encountered in the binary,
    /// matching the <see cref="RelocationSection.TargetSectionIndex"/> convention.
    /// </summary>
    internal static List<(long PayloadStart, int PayloadLength)> ScanSectionPayloads(byte[] bytes)
    {
        var sections = new List<(long, int)>();
        int pos = 8; // skip 4-byte magic + 4-byte version

        while (pos < bytes.Length)
        {
            if (pos >= bytes.Length) break;
            pos++; // consume section id byte

            // Decode u32 LEB128 section size
            uint sectionSize = 0;
            int shift = 0;
            byte b;
            do
            {
                if (pos >= bytes.Length)
                    throw new InvalidOperationException("Unexpected end of binary while reading section size.");
                b = bytes[pos++];
                sectionSize |= (uint)(b & 0x7F) << shift;
                shift += 7;
            }
            while ((b & 0x80) != 0);

            if (pos + (long)sectionSize > bytes.Length)
                throw new InvalidOperationException(
                    $"Section payload (size {sectionSize} at offset {pos}) runs past the end of the binary ({bytes.Length} bytes).");

            sections.Add((pos, (int)sectionSize));
            pos += (int)sectionSize;
        }

        return sections;
    }

    // ── Verbose logging ──────────────────────────────────────────────────────────

    private static string FormatRelocLog(Relocation reloc, RelocSymbolTable symbols)
    {
        string symName = reloc.Index < (uint)symbols.Count
            ? (symbols[reloc.Index].Name ?? $"#{reloc.Index}")
            : $"#{reloc.Index}";
        string addend = reloc.Addend != 0 ? $"{reloc.Addend:+#;-#}" : "";
        return $"  {reloc.Type,-32} offset=0x{reloc.Offset:X4}  sym={symName}{addend}";
    }
}
