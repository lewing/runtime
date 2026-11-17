// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>A DWARF variable or parameter mapped to a wasm local index.</summary>
public sealed record DwarfVariable(string Name, string TypeName, int WasmLocalIndex, bool IsParameter);

/// <summary>Wasm-specific DWARF location kinds from DW_OP_WASM_location (0xED).</summary>
public enum WasmLocationKind : byte
{
    Local = 0x00,
    Global = 0x01,
    OperandStack = 0x02,
    I32Const = 0x03,
}

/// <summary>
/// Reads DWARF .debug_info to extract variable/parameter entries for a given wasm function.
/// Parses DW_TAG_subprogram, DW_TAG_formal_parameter, and DW_TAG_variable DIEs,
/// resolving DW_OP_WASM_location (0xED) to map names to wasm local indices.
/// </summary>
public static class DwarfVariableReader
{
    // DWARF tags
    private const ushort DW_TAG_compile_unit = 0x11;
    private const ushort DW_TAG_subprogram = 0x2e;
    private const ushort DW_TAG_formal_parameter = 0x05;
    private const ushort DW_TAG_variable = 0x34;

    // DWARF attributes
    private const ushort DW_AT_name = 0x03;
    private const ushort DW_AT_low_pc = 0x11;
    private const ushort DW_AT_location = 0x02;

    // Wasm DWARF extension
    private const byte DW_OP_WASM_location = 0xED;

    /// <summary>
    /// Read DWARF variable/parameter entries for a given function.
    /// Returns variable-to-local mappings for the function at the given index.
    /// </summary>
    public static IReadOnlyList<DwarfVariable> ReadVariables(
        Microsoft.NET.WebAssembly.Module.WasmModule module, int funcIndex)
    {
        ArgumentNullException.ThrowIfNull(module);

        var debugInfoSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_info");
        if (debugInfoSection is null)
            return [];

        var debugAbbrevSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_abbrev");
        var debugStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str");
        var debugLineStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_line_str");
        var debugStrOffsetsSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str_offsets");
        var debugAddrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_addr");

        // Build address-range → absolute function index mapping
        // DW_AT_low_pc in LLVM wasm DWARF is a code section byte offset
        int importedFuncCount = module.Imports.Count(i => i.Descriptor is Microsoft.NET.WebAssembly.Module.ImportDescriptor.Func);
        var funcRanges = new List<(long startOffset, long endOffset, int absIndex)>();
        for (int i = 0; i < module.Functions.Count; i++)
        {
            long start = module.Functions[i].CodeSectionOffset;
            long end = i + 1 < module.Functions.Count
                ? module.Functions[i + 1].CodeSectionOffset
                // Bound the last function by its real code extent (body end), not
                // long.MaxValue: with imports, absolute func indices exceed code-section
                // offsets, and an open-ended last range would wrongly report a legit
                // low_pc-as-index fallback candidate as "inside" code and suppress it.
                : module.Functions[i].BodyCodeSectionOffset + module.Functions[i].Body.Length;
            funcRanges.Add((start, end, importedFuncCount + i));
        }

        return ParseVariables(
            debugInfoSection.Content,
            debugAbbrevSection?.Content ?? [],
            debugStrSection?.Content,
            debugLineStrSection?.Content,
            debugStrOffsetsSection?.Content,
            funcIndex,
            funcRanges,
            debugAddrSection?.Content);
    }

    /// <summary>Core parsing entry point operating on raw section bytes.</summary>
    /// <param name="infoData">Raw .debug_info section bytes.</param>
    /// <param name="abbrevData">Raw .debug_abbrev section bytes.</param>
    /// <param name="strData">Optional .debug_str section bytes.</param>
    /// <param name="lineStrData">Optional .debug_line_str section bytes.</param>
    /// <param name="strOffsetsData">Optional .debug_str_offsets section bytes.</param>
    /// <param name="funcIndex">The absolute function index to match.</param>
    /// <param name="funcRanges">
    /// Maps DW_AT_low_pc address ranges to absolute function indices.
    /// When null, DW_AT_low_pc is treated directly as a function index (synthetic DWARF).
    /// </param>
    /// <param name="addrData">Optional .debug_addr section bytes (DWARF 5 <c>DW_FORM_addrx</c>).</param>
    internal static IReadOnlyList<DwarfVariable> ParseVariables(
        byte[] infoData,
        byte[] abbrevData,
        byte[]? strData,
        byte[]? lineStrData,
        byte[]? strOffsetsData,
        int funcIndex,
        List<(long startOffset, long endOffset, int absIndex)>? funcRanges = null,
        byte[]? addrData = null)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrevData);
        int pos = 0;

        // Fix A: run a COMPLETE primary pass over every CU before considering the
        // unrelocated-low_pc fallback. A subprogram that RANGE-MATCHES the requested
        // function is authoritative — even if it has zero mappable wasm-local
        // variables — so we must not conflate "matched, no vars" with "no match" and
        // then wrongly attach a different function's variables. The fallback result
        // is only remembered here and returned after the whole loop confirms that no
        // subprogram range-matched anywhere.
        List<DwarfVariable>? fallbackResult = null;
        int fallbackCandidates = 0;

        while (pos < infoData.Length)
        {
            // Fix F: validate the CU header incrementally before indexing into it.
            if (pos + 6 > infoData.Length) break; // unit_length(4) + version(2)

            int cuStart = pos;
            uint unitLen = DwarfReaderHelpers.ReadU32LE(infoData, pos);
            if (unitLen == 0xFFFFFFFF) break; // 64-bit DWARF not supported

            int cuEnd = pos + 4 + (int)unitLen;
            if (cuEnd > infoData.Length || cuEnd < pos + 6) break;

            ushort version = DwarfReaderHelpers.ReadU16LE(infoData, pos + 4);
            if (version < 2 || version > 5) { pos = cuEnd; continue; }

            // Minimum header sizes: v2–v4 need abbrev_offset(4)+addr_size(1) after
            // version (→ pos+11); v5 adds unit_type(1) before them (→ pos+12).
            int headerEnd = version >= 5 ? pos + 12 : pos + 11;
            if (headerEnd > cuEnd || headerEnd > infoData.Length) { pos = cuEnd; continue; }

            int abbrevOffset;
            byte addrSize;
            int diePos;
            if (version >= 5)
            {
                addrSize = infoData[pos + 7];
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 8);
                diePos = pos + 12;
            }
            else
            {
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 6);
                addrSize = infoData[pos + 10];
                diePos = pos + 11;
            }

            if (!abbrevTables.TryGetValue(abbrevOffset, out var abbrevTable))
            {
                pos = cuEnd;
                continue;
            }

            // DWARF 5 per-unit bases: string offsets and indexed (DW_FORM_addrx) addresses.
            var (strOffsetsBase, addrBase) = DwarfReaderHelpers.ReadUnitBases(infoData, diePos, cuEnd, version, abbrevTable, addrSize);
            var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, strOffsetsData, strOffsetsBase,
                addrData, addrBase, resolveAddrx: true);
            var dies = DwarfReaderHelpers.ParseDIEs(infoData, diePos, cuEnd, abbrevTable, addrSize, ctx, cuStart);

            // Primary pass for this CU. A range-match (or, when funcRanges is null,
            // the synthetic low_pc-as-index match) is authoritative — return it even
            // if the variable list is empty.
            var primary = ExtractVariablesForFunction(dies, funcIndex, funcRanges, fallback: false);
            if (primary is not null)
                return primary;

            // Fallback for unrelocated DWARF (e.g. minimal wasm32-unknown-unknown
            // --no-entry links): DW_AT_low_pc is left at 0 and matches no code range.
            // Only remembered (never returned early) so the primary pass can complete
            // across all CUs first. Guarded to low_pc values that lie OUTSIDE every
            // known code range, so a low_pc that numerically collides with a real
            // function index cannot masquerade as that function — and only returned when
            // exactly one subprogram claims the index (see CountIndexFallbackCandidates):
            // wasm-opt-zeroed DWARF puts every subprogram at 0 (#530).
            if (funcRanges is not null)
            {
                fallbackCandidates += CountIndexFallbackCandidates(dies, funcIndex, funcRanges);
                if (fallbackResult is null)
                {
                    var fb = ExtractVariablesForFunction(dies, funcIndex, funcRanges, fallback: true);
                    if (fb is not null)
                        fallbackResult = fb;
                }
            }

            pos = cuEnd;
        }

        return fallbackCandidates == 1 ? fallbackResult ?? [] : [];
    }

    /// <summary>
    /// Extract the variables of the subprogram matching <paramref name="funcIndex"/>
    /// within a single CU. Returns null when NO subprogram matched (so the caller can
    /// distinguish "matched with no mappable vars" from "no match"); otherwise the
    /// (possibly empty) variable list.
    /// </summary>
    private static List<DwarfVariable>? ExtractVariablesForFunction(
        List<DwarfReaderHelpers.DwarfDIE> dies, int funcIndex,
        List<(long startOffset, long endOffset, int absIndex)>? funcRanges,
        bool fallback)
    {
        // Build offset→DIE lookup for type resolution
        var byOffset = new Dictionary<int, DwarfReaderHelpers.DwarfDIE>(dies.Count);
        foreach (var die in dies)
            byOffset[die.Offset] = die;

        for (int i = 0; i < dies.Count; i++)
        {
            var die = dies[i];
            if (die.Tag != DW_TAG_subprogram)
                continue;

            // Match by DW_AT_low_pc — resolve through range map if available
            if (!die.Attributes.TryGetValue(DW_AT_low_pc, out var lowPcObj) || lowPcObj is not long lowPc)
                continue;

            if (!SubprogramMatches(lowPc, funcIndex, funcRanges, fallback).Matched)
                continue;

            // Found matching subprogram — collect children
            var variables = new List<DwarfVariable>();
            for (int j = i + 1; j < dies.Count; j++)
            {
                var child = dies[j];
                if (child.Depth <= die.Depth) break;
                if (child.Depth != die.Depth + 1) continue;

                bool isParam = child.Tag == DW_TAG_formal_parameter;
                bool isVar = child.Tag == DW_TAG_variable;
                if (!isParam && !isVar) continue;

                string? name = child.Attributes.TryGetValue(DW_AT_name, out var nameObj)
                    ? nameObj?.ToString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                // Parse DW_AT_location for DW_OP_WASM_location
                int wasmLocalIndex = -1;
                if (child.Attributes.TryGetValue(DW_AT_location, out var locObj) && locObj is byte[] locBytes)
                {
                    wasmLocalIndex = ParseWasmLocation(locBytes);
                }

                if (wasmLocalIndex < 0) continue; // Skip variables without wasm local mapping

                string typeName = DwarfReaderHelpers.ResolveTypeName(child, byOffset);

                variables.Add(new DwarfVariable(name!, typeName, wasmLocalIndex, isParam));
            }

            return variables;
        }

        return null;
    }

    /// <summary>
    /// The outcome of matching a subprogram DIE to a requested function index. Carries
    /// enough address-space metadata for the caller to resolve PC-varying (location-list)
    /// locations honestly:
    /// <list type="bullet">
    /// <item><see cref="Matched"/> — did this subprogram correspond to the requested index?</item>
    /// <item><see cref="IsFallback"/> — was the match via the #141 unrelocated low_pc-as-index
    /// heuristic (true) rather than a real code-range hit (false)? In the fallback case the
    /// subprogram's DW_AT_low_pc is a FUNCTION INDEX, not a code PC.</item>
    /// <item><see cref="CodeStartOffset"/> — the matched function's code-section start offset
    /// (from the range map). Meaningful for primary/fallback matches; 0 for synthetic DWARF
    /// (no range map).</item>
    /// </list>
    /// </summary>
    private readonly record struct SubprogramMatch(bool Matched, bool IsFallback, long CodeStartOffset);

    /// <summary>
    /// Does a subprogram with DW_AT_low_pc <paramref name="lowPc"/> correspond to
    /// the requested absolute function index?
    /// <list type="bullet">
    /// <item>When <paramref name="funcRanges"/> is null: synthetic DWARF — low_pc is
    /// treated directly as the function index.</item>
    /// <item>Primary (fallback=false): resolve low_pc through the code-range map.</item>
    /// <item>Fallback (fallback=true): low_pc-as-index, but ONLY when low_pc lies
    /// OUTSIDE every known code range, so an unrelocated low_pc cannot collide with a
    /// real function whose range happens to contain that numeric value.</item>
    /// </list>
    /// </summary>
    private static SubprogramMatch SubprogramMatches(
        long lowPc, int funcIndex,
        List<(long startOffset, long endOffset, int absIndex)>? funcRanges,
        bool fallback)
    {
        if (funcRanges is null)
            // synthetic DWARF: low_pc is the index; no code-section space exists.
            return new SubprogramMatch((int)lowPc == funcIndex, IsFallback: false, CodeStartOffset: 0);

        if (!fallback)
        {
            for (int r = funcRanges.Count - 1; r >= 0; r--)
                if (lowPc >= funcRanges[r].startOffset && lowPc < funcRanges[r].endOffset)
                    return new SubprogramMatch(
                        funcRanges[r].absIndex == funcIndex, IsFallback: false, funcRanges[r].startOffset);
            return new SubprogramMatch(false, IsFallback: false, 0);
        }

        // Fallback: low_pc-as-index, only if it is outside all real code ranges.
        if ((int)lowPc != funcIndex)
            return new SubprogramMatch(false, IsFallback: true, 0);
        for (int r = 0; r < funcRanges.Count; r++)
            if (lowPc >= funcRanges[r].startOffset && lowPc < funcRanges[r].endOffset)
                return new SubprogramMatch(false, IsFallback: true, 0);

        // Matched via the unrelocated fallback. Anchor loclist resolution at the
        // matched function's real code-section start (its DW_AT_low_pc is an index,
        // not a PC, so it cannot serve as the loclist base or the entry query PC).
        long codeStart = 0;
        for (int r = 0; r < funcRanges.Count; r++)
            if (funcRanges[r].absIndex == funcIndex) { codeStart = funcRanges[r].startOffset; break; }
        return new SubprogramMatch(true, IsFallback: true, codeStart);
    }

    /// <summary>
    /// How many subprograms in a CU claim <paramref name="funcIndex"/> through the unrelocated
    /// low_pc-as-index fallback (low_pc == index, outside every code range). The fallback is only
    /// trusted when exactly ONE subprogram in the whole DWARF does: if several share the value it is
    /// not an index at all — it is the tombstone wasm-opt leaves when it zeroes every code address
    /// (#530), and taking the first would attach an arbitrary function's variables to function 0.
    /// </summary>
    private static int CountIndexFallbackCandidates(
        List<DwarfReaderHelpers.DwarfDIE> dies, int funcIndex,
        List<(long startOffset, long endOffset, int absIndex)> funcRanges)
    {
        int count = 0;
        foreach (var die in dies)
        {
            if (die.Tag != DW_TAG_subprogram) continue;
            if (!die.Attributes.TryGetValue(DW_AT_low_pc, out var lowPcObj) || lowPcObj is not long lowPc) continue;
            if (SubprogramMatches(lowPc, funcIndex, funcRanges, fallback: true).Matched)
                count++;
        }
        return count;
    }

    // ── Additive: richer variable-location resolution ────────────────────────
    // The legacy ReadVariables path only surfaces variables whose DW_AT_location
    // is an inline DW_OP_WASM_location(Local) expression (register-allocated wasm
    // locals). Optimized (-O1) and unoptimized (-O0) clang builds routinely place
    // a variable elsewhere: on the shadow stack via DW_OP_fbreg (a frame-base +
    // offset in LINEAR MEMORY) or at a fixed address via DW_OP_addr. ReadVariableInfos
    // classifies these three forms (WasmLocal / FrameBaseOffset / MemoryAddress) and
    // exposes the containing subprogram's frame-base wasm local, so a debugger can
    // read a variable that lives in linear memory (read the frame-base local, add the
    // offset, load the bytes). Any location form that is not one of these three
    // supported opcodes — including DW_OP_WASM_location kinds other than Local (Global,
    // OperandStack, I32Const), location-list references, or expressions with
    // unrecognized trailing opcodes — is reported as Unknown rather than guessed.
    // Strictly additive: new public types + method; the legacy ReadVariables /
    // DwarfVariable API is untouched.

    private const ushort DW_AT_frame_base = 0x40;
    private const byte DW_OP_addr = 0x03;
    private const byte DW_OP_fbreg = 0x91;
    private const byte DW_OP_stack_value = 0x9F;

    /// <summary>How a DWARF variable is located.</summary>
    public enum DwarfVarLocationKind
    {
        /// <summary>Unresolved / unsupported location form (e.g. a location list offset).</summary>
        Unknown = 0,
        /// <summary>DW_OP_WASM_location(Local): the value is a wasm local; LocationValue is the local index.</summary>
        WasmLocal,
        /// <summary>DW_OP_fbreg: the value lives in linear memory at frame_base + LocationValue (signed).</summary>
        FrameBaseOffset,
        /// <summary>DW_OP_addr: the value lives at fixed linear-memory address LocationValue.</summary>
        MemoryAddress,
    }

    /// <summary>A DWARF variable with its resolved location kind + value.</summary>
    public sealed record DwarfVariableInfo(
        string Name, string TypeName, bool IsParameter,
        DwarfVarLocationKind LocationKind, long LocationValue);

    /// <summary>
    /// A function's variables with resolved locations, plus the subprogram's
    /// frame-base wasm local index (from DW_AT_frame_base = DW_OP_WASM_location(Local)).
    /// FrameBaseLocalIndex is -1 if the frame base is not a simple wasm local.
    /// </summary>
    public sealed record DwarfFunctionVariables(
        int FrameBaseLocalIndex, IReadOnlyList<DwarfVariableInfo> Variables);

    /// <summary>
    /// Read variable/parameter entries for a function with full location detail
    /// (wasm local, frame-base offset, or fixed address) and the frame-base local.
    /// </summary>
    /// <param name="module">The wasm module.</param>
    /// <param name="funcIndex">The absolute function index to resolve.</param>
    /// <param name="queryCodeOffset">
    /// Code-section-relative PC at which to resolve PC-varying (DWARF location-list)
    /// locations. Variables whose DW_AT_location is an inline expression are
    /// unaffected. When null, PC-varying locations are resolved at the subprogram's
    /// entry PC (its DW_AT_low_pc / code-section offset).
    /// <para>
    /// HONESTY / best-effort: an optimized parameter may not be live at function
    /// entry — the compiler can leave it in a register that is only spilled after the
    /// prologue, or move it between locals across the body. Resolving at the default
    /// entry PC is therefore best-effort; a caller that holds a REAL paused PC (e.g. a
    /// CDP breakpoint's code-section offset) should pass it explicitly as
    /// <paramref name="queryCodeOffset"/> for a correct point-in-time answer.
    /// </para>
    /// <para>
    /// SPACE: <paramref name="queryCodeOffset"/> is a CODE-SECTION byte offset (the
    /// same space as <see cref="Microsoft.NET.WebAssembly.Module.WasmModule"/> function code offsets), NOT
    /// a nesm-interpreter instruction index. Any future interpreter-path wiring must
    /// convert its instruction-index frame.PC to a code-section byte offset first.
    /// </para>
    /// </param>
    public static DwarfFunctionVariables ReadVariableInfos(
        Microsoft.NET.WebAssembly.Module.WasmModule module, int funcIndex, long? queryCodeOffset = null)
    {
        ArgumentNullException.ThrowIfNull(module);

        var debugInfoSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_info");
        if (debugInfoSection is null)
            return new DwarfFunctionVariables(-1, []);

        var debugAbbrevSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_abbrev");
        var debugStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str");
        var debugLineStrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_line_str");
        var debugStrOffsetsSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_str_offsets");
        // DWARF <=4 PC-varying locations live in .debug_loc (a location LIST). DWARF 5
        // uses .debug_loclists instead — not yet implemented; such references fall
        // through to Unknown, matching pre-change behavior.
        var debugLocSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_loc");
        var debugAddrSection = module.CustomSections.FirstOrDefault(cs => cs.Name == ".debug_addr");

        int importedFuncCount = module.Imports.Count(i => i.Descriptor is Microsoft.NET.WebAssembly.Module.ImportDescriptor.Func);
        var funcRanges = new List<(long startOffset, long endOffset, int absIndex)>();
        for (int i = 0; i < module.Functions.Count; i++)
        {
            long start = module.Functions[i].CodeSectionOffset;
            long end = i + 1 < module.Functions.Count
                ? module.Functions[i + 1].CodeSectionOffset
                // Bound the last function by its real code extent (body end), not
                // long.MaxValue — see ReadVariables for the imports rationale.
                : module.Functions[i].BodyCodeSectionOffset + module.Functions[i].Body.Length;
            funcRanges.Add((start, end, importedFuncCount + i));
        }

        return ParseVariableInfos(
            debugInfoSection.Content,
            debugAbbrevSection?.Content ?? [],
            debugStrSection?.Content,
            debugLineStrSection?.Content,
            debugStrOffsetsSection?.Content,
            funcIndex,
            funcRanges,
            debugLocSection?.Content,
            queryCodeOffset,
            debugAddrSection?.Content);
    }

    internal static DwarfFunctionVariables ParseVariableInfos(
        byte[] infoData, byte[] abbrevData,
        byte[]? strData, byte[]? lineStrData, byte[]? strOffsetsData,
        int funcIndex, List<(long startOffset, long endOffset, int absIndex)>? funcRanges = null,
        byte[]? locData = null, long? queryCodeOffset = null, byte[]? addrData = null)
    {
        var abbrevTables = DwarfAbbrevParser.Parse(abbrevData);
        int pos = 0;

        // Fix A: mirror the ReadVariables path — a subprogram that RANGE-MATCHES the
        // requested function is authoritative even with zero classifiable variables;
        // the low_pc-as-index fallback is only accepted after a complete primary pass
        // over every CU confirms no range match anywhere.
        DwarfFunctionVariables? fallbackResult = null;
        int fallbackCandidates = 0;

        while (pos < infoData.Length)
        {
            // Fix F: validate the CU header incrementally before indexing into it.
            if (pos + 6 > infoData.Length) break; // unit_length(4) + version(2)

            int cuStart = pos;
            uint unitLen = DwarfReaderHelpers.ReadU32LE(infoData, pos);
            if (unitLen == 0xFFFFFFFF) break;

            int cuEnd = pos + 4 + (int)unitLen;
            if (cuEnd > infoData.Length || cuEnd < pos + 6) break;

            ushort version = DwarfReaderHelpers.ReadU16LE(infoData, pos + 4);
            if (version < 2 || version > 5) { pos = cuEnd; continue; }

            int headerEnd = version >= 5 ? pos + 12 : pos + 11;
            if (headerEnd > cuEnd || headerEnd > infoData.Length) { pos = cuEnd; continue; }

            int abbrevOffset;
            byte addrSize;
            int diePos;
            if (version >= 5)
            {
                addrSize = infoData[pos + 7];
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 8);
                diePos = pos + 12;
            }
            else
            {
                abbrevOffset = (int)DwarfReaderHelpers.ReadU32LE(infoData, pos + 6);
                addrSize = infoData[pos + 10];
                diePos = pos + 11;
            }

            if (!abbrevTables.TryGetValue(abbrevOffset, out var abbrevTable))
            {
                pos = cuEnd;
                continue;
            }

            // DWARF 5 per-unit bases: string offsets and indexed (DW_FORM_addrx) addresses.
            var (strOffsetsBase, addrBase) = DwarfReaderHelpers.ReadUnitBases(infoData, diePos, cuEnd, version, abbrevTable, addrSize);
            var ctx = new DwarfReaderHelpers.StringContext(strData, lineStrData, strOffsetsData, strOffsetsBase,
                addrData, addrBase, resolveAddrx: true);
            var dies = DwarfReaderHelpers.ParseDIEs(infoData, diePos, cuEnd, abbrevTable, addrSize, ctx, cuStart);

            // Default base address for this CU's location lists: the compile_unit's
            // DW_AT_low_pc (DWARF 5.7.7). Often 0 in unrelocated wasm modules, in
            // which case loclist begin/end offsets are code-section-relative.
            long cuBaseLowPc = 0;
            foreach (var d in dies)
            {
                if (d.Tag != DW_TAG_compile_unit) continue;
                if (d.Attributes.TryGetValue(DW_AT_low_pc, out var cuLowObj) && cuLowObj is long cuLow)
                    cuBaseLowPc = cuLow;
                break;
            }

            var primary = ExtractVariableInfosForFunction(
                dies, funcIndex, funcRanges, addrSize, fallback: false, locData, cuBaseLowPc, queryCodeOffset);
            if (primary is not null)
                return primary;

            if (funcRanges is not null)
            {
                fallbackCandidates += CountIndexFallbackCandidates(dies, funcIndex, funcRanges);
                if (fallbackResult is null)
                {
                    var fb = ExtractVariableInfosForFunction(
                        dies, funcIndex, funcRanges, addrSize, fallback: true, locData, cuBaseLowPc, queryCodeOffset);
                    if (fb is not null)
                        fallbackResult = fb;
                }
            }

            pos = cuEnd;
        }

        return fallbackCandidates == 1 && fallbackResult is not null
            ? fallbackResult
            : new DwarfFunctionVariables(-1, []);
    }

    private static DwarfFunctionVariables? ExtractVariableInfosForFunction(
        List<DwarfReaderHelpers.DwarfDIE> dies, int funcIndex,
        List<(long startOffset, long endOffset, int absIndex)>? funcRanges,
        byte addrSize, bool fallback,
        byte[]? locData, long cuBaseLowPc, long? queryCodeOffset)
    {
        var byOffset = new Dictionary<int, DwarfReaderHelpers.DwarfDIE>(dies.Count);
        foreach (var die in dies)
            byOffset[die.Offset] = die;

        for (int i = 0; i < dies.Count; i++)
        {
            var die = dies[i];
            if (die.Tag != DW_TAG_subprogram)
                continue;
            if (!die.Attributes.TryGetValue(DW_AT_low_pc, out var lowPcObj) || lowPcObj is not long lowPc)
                continue;

            var match = SubprogramMatches(lowPc, funcIndex, funcRanges, fallback);
            if (!match.Matched)
                continue;

            int frameBaseLocal = -1;
            if (die.Attributes.TryGetValue(DW_AT_frame_base, out var fbObj) && fbObj is byte[] fbBytes)
                frameBaseLocal = ParseWasmLocation(fbBytes); // -1 if not DW_OP_WASM_location(Local)

            // Address-space selection for location-list (.debug_loc) resolution. Loclist
            // entry ranges are [loclistBase + begin, loclistBase + end); the query PC MUST
            // live in the SAME space or coverage tests are meaningless.
            //  • Primary (relocated) / synthetic: DW_AT_low_pc is a real PC in the same
            //    space as the CU's DW_AT_low_pc base and any caller-supplied code-section
            //    offset — so loclistBase = cuBaseLowPc and the entry-PC default = lowPc.
            //  • Fallback (#141 unrelocated match): DW_AT_low_pc is a FUNCTION INDEX, not a
            //    PC, and cuBaseLowPc is unrelocated (≈0). The loclist ranges are effectively
            //    function-relative, so anchor them at the function's real code-section start.
            //    Both the entry-PC default and any caller queryCodeOffset are then code-
            //    section byte offsets in that one space — never the index-space low_pc.
            long loclistBase;
            long defaultQueryPc;
            if (match.IsFallback)
            {
                loclistBase = match.CodeStartOffset;
                defaultQueryPc = match.CodeStartOffset;
            }
            else
            {
                loclistBase = cuBaseLowPc;
                defaultQueryPc = lowPc;
            }
            long effectiveQueryPc = queryCodeOffset ?? defaultQueryPc;

            var variables = new List<DwarfVariableInfo>();
            for (int j = i + 1; j < dies.Count; j++)
            {
                var child = dies[j];
                if (child.Depth <= die.Depth) break;
                if (child.Depth != die.Depth + 1) continue;

                bool isParam = child.Tag == DW_TAG_formal_parameter;
                bool isVar = child.Tag == DW_TAG_variable;
                if (!isParam && !isVar) continue;

                string? name = child.Attributes.TryGetValue(DW_AT_name, out var nameObj)
                    ? nameObj?.ToString() : null;
                if (string.IsNullOrEmpty(name)) continue;

                // DW_AT_location is EITHER an inline single-location expression
                // (byte[] from an exprloc/block form) OR a NUMERIC offset (from
                // DW_FORM_sec_offset/data4/data8) into .debug_loc — a location LIST.
                DwarfVarLocationKind kind = DwarfVarLocationKind.Unknown;
                long value = 0L;
                if (child.Attributes.TryGetValue(DW_AT_location, out var locObj))
                {
                    if (locObj is byte[] locBytes)
                        (kind, value) = ParseLocationExpression(locBytes, addrSize);
                    else if (locObj is long loclistOffset)
                        (kind, value) = ResolveLoclistLocation(
                            locData, loclistOffset, loclistBase, effectiveQueryPc, addrSize);
                }

                string typeName = DwarfReaderHelpers.ResolveTypeName(child, byOffset);
                variables.Add(new DwarfVariableInfo(name!, typeName, isParam, kind, value));
            }

            return new DwarfFunctionVariables(frameBaseLocal, variables);
        }

        return null;
    }

    /// <summary>
    /// Classify a DWARF single-location expression as a wasm local, a frame-base
    /// offset, or a fixed memory address. The WHOLE expression must be consumed
    /// (an optional trailing DW_OP_stack_value terminator is permitted); a truncated
    /// operand or any unrecognized trailing opcode yields Unknown rather than a
    /// confident-but-wrong classification. <paramref name="addrSize"/> is the CU
    /// address size, so DW_OP_addr is read as 8 bytes on wasm64.
    /// </summary>
    internal static (DwarfVarLocationKind Kind, long Value) ParseLocationExpression(byte[] loc, byte addrSize = 4)
    {
        if (loc.Length == 0) return (DwarfVarLocationKind.Unknown, 0);

        int p = 0;
        byte op = loc[p++];
        DwarfVarLocationKind kind;
        long value;

        switch (op)
        {
            case DW_OP_WASM_location:
            {
                if (p >= loc.Length) return (DwarfVarLocationKind.Unknown, 0);
                var wkind = (WasmLocationKind)loc[p++];
                if (wkind != WasmLocationKind.Local) return (DwarfVarLocationKind.Unknown, 0);
                if (!TryReadULeb128(loc, ref p, out ulong idx)) return (DwarfVarLocationKind.Unknown, 0);
                kind = DwarfVarLocationKind.WasmLocal;
                value = (long)idx;
                break;
            }

            case DW_OP_fbreg:
            {
                if (!TryReadSleb128(loc, ref p, out long off)) return (DwarfVarLocationKind.Unknown, 0);
                kind = DwarfVarLocationKind.FrameBaseOffset;
                value = off;
                break;
            }

            case DW_OP_addr:
            {
                if (p + addrSize > loc.Length) return (DwarfVarLocationKind.Unknown, 0);
                if (addrSize == 8) { value = (long)DwarfReaderHelpers.ReadU64LE(loc, p); p += 8; }
                else if (addrSize == 4) { value = DwarfReaderHelpers.ReadU32LE(loc, p); p += 4; }
                else return (DwarfVarLocationKind.Unknown, 0);
                kind = DwarfVarLocationKind.MemoryAddress;
                break;
            }

            default:
                return (DwarfVarLocationKind.Unknown, 0);
        }

        // A trailing DW_OP_stack_value marks the value as computed/implicit (the
        // value IS the operand, not stored at that address). Real clang emits it
        // after DW_OP_WASM_location(Local N) for wasm locals, so tolerate exactly
        // one there. For DW_OP_fbreg / DW_OP_addr a trailing stack_value flips the
        // meaning to "value == frame_base+X / == addr" (not stored AT that address),
        // which we don't model as FrameBaseOffset/MemoryAddress — leave it as the
        // leftover byte so the completeness check below yields Unknown.
        if (p < loc.Length && loc[p] == DW_OP_stack_value && kind == DwarfVarLocationKind.WasmLocal)
            p++;

        // The whole expression must be consumed. Anything left over means a
        // multi-operation expression we do not model — do not guess.
        return p == loc.Length ? (kind, value) : (DwarfVarLocationKind.Unknown, 0);
    }

    /// <summary>
    /// Resolve a DWARF (&lt;= 4) <c>.debug_loc</c> location LIST to the single-location
    /// expression that covers <paramref name="queryPc"/>, then classify it via
    /// <see cref="ParseLocationExpression"/>.
    /// <para>
    /// The list starts at <paramref name="loclistOffset"/> in <paramref name="locData"/>.
    /// Each entry is <c>[begin(addrSize), end(addrSize)]</c> followed by:
    /// <list type="bullet">
    /// <item><c>begin==0 &amp;&amp; end==0</c> → END of list.</item>
    /// <item><c>begin == max-for-addrSize</c> (0xFFFFFFFF for addr4) → BASE ADDRESS
    ///   SELECTION: <c>end</c> becomes the new base; continue.</item>
    /// <item>otherwise → LOCATION entry: a 2-byte length N then N expression bytes,
    ///   applying to PCs in <c>[base+begin, base+end)</c>.</item>
    /// </list>
    /// The initial base is <paramref name="baseAddress"/> (the containing CU's
    /// <c>DW_AT_low_pc</c>). Any missing section, out-of-range offset, truncated entry,
    /// malformed length, or PC covered by no entry yields <see cref="DwarfVarLocationKind.Unknown"/>
    /// — never a throw on adversarial input. Only DWARF 32-bit format and address
    /// sizes 4 (wasm32) and 8 (defensive) are supported; anything else → Unknown.
    /// DWARF 5 <c>.debug_loclists</c> is a distinct format and is NOT handled here.
    /// </para>
    /// </summary>
    internal static (DwarfVarLocationKind Kind, long Value) ResolveLoclistLocation(
        byte[]? locData, long loclistOffset, long baseAddress, long queryPc, byte addrSize)
    {
        if (locData is null || locData.Length == 0) return (DwarfVarLocationKind.Unknown, 0);
        if (addrSize != 4 && addrSize != 8) return (DwarfVarLocationKind.Unknown, 0);
        if (loclistOffset < 0 || loclistOffset >= locData.Length) return (DwarfVarLocationKind.Unknown, 0);

        // The "large" begin value that flags a base-address selection entry.
        long maxAddr = addrSize == 8 ? -1L /* 0xFFFF...FF */ : 0xFFFFFFFFL;

        long baseAddr = baseAddress;
        int p = (int)loclistOffset;

        // p strictly advances by at least 2*addrSize each iteration and is bounded by
        // locData.Length, so this loop always terminates even on malformed input.
        while (true)
        {
            if (p + 2 * addrSize > locData.Length) return (DwarfVarLocationKind.Unknown, 0);
            long begin = ReadAddr(locData, ref p, addrSize);
            long end = ReadAddr(locData, ref p, addrSize);

            if (begin == 0 && end == 0) return (DwarfVarLocationKind.Unknown, 0); // end of list

            if (begin == maxAddr)
            {
                baseAddr = end; // base address selection
                continue;
            }

            if (p + 2 > locData.Length) return (DwarfVarLocationKind.Unknown, 0);
            int n = DwarfReaderHelpers.ReadU16LE(locData, p);
            p += 2;
            if (n < 0 || p + n > locData.Length) return (DwarfVarLocationKind.Unknown, 0);

            long lo = baseAddr + begin;
            long hi = baseAddr + end;
            if (queryPc >= lo && queryPc < hi)
            {
                var expr = new byte[n];
                Array.Copy(locData, p, expr, 0, n);
                return ParseLocationExpression(expr, addrSize);
            }

            p += n; // not covered — advance past this entry's expression
        }
    }

    /// <summary>Read an unsigned little-endian address of <paramref name="addrSize"/> bytes as a long.</summary>
    private static long ReadAddr(byte[] d, ref int p, byte addrSize)
    {
        long v = addrSize == 8 ? (long)DwarfReaderHelpers.ReadU64LE(d, p) : DwarfReaderHelpers.ReadU32LE(d, p);
        p += addrSize;
        return v;
    }

    /// <summary>Read a ULEB128, returning false if the operand is truncated (runs off the end).</summary>
    private static bool TryReadULeb128(byte[] d, ref int p, out ulong value)
    {
        value = 0;
        int shift = 0;
        while (true)
        {
            if (p >= d.Length) return false;
            byte b = d[p++];
            value |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) return true;
            shift += 7;
            if (shift >= 64) return false;
        }
    }

    /// <summary>Read a SLEB128, returning false if the operand is truncated (runs off the end).</summary>
    private static bool TryReadSleb128(byte[] d, ref int p, out long value)
    {
        value = 0;
        int shift = 0;
        byte b;
        do
        {
            if (p >= d.Length) return false;
            b = d[p++];
            value |= (long)(b & 0x7F) << shift;
            shift += 7;
            if (shift > 64) return false;
        } while ((b & 0x80) != 0);

        if (shift < 64 && (b & 0x40) != 0)
            value |= -(1L << shift);
        return true;
    }

    /// <summary>
    /// Parse a DWARF location expression to extract a wasm local index.
    /// Handles DW_OP_WASM_location (0xED) for direct local mapping.
    /// Returns the wasm local index, or -1 if not a recognized wasm local location.
    /// </summary>
    internal static int ParseWasmLocation(byte[] locBytes)
    {
        if (locBytes.Length < 3) return -1;

        if (locBytes[0] != DW_OP_WASM_location) return -1;

        var kind = (WasmLocationKind)locBytes[1];
        if (kind != WasmLocationKind.Local) return -1;

        int pos = 2;
        uint index = DwarfReaderHelpers.ReadULEB128(locBytes, ref pos);
        return (int)index;
    }
}
