// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Wat;

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>
/// Maps code-section offsets (the address space wasm DWARF uses) of one input module to the offsets of the same
/// instructions in a re-encoded output. Each input function is either kept, with its instruction offsets mapped
/// through the breakpoints its re-encoding reported, or removed.
/// </summary>
internal sealed class CodeAddressMap
{
    internal enum Outcome
    {
        Mapped,
        Removed,
        Special,
        Invalid
    }

    /// <summary>An instruction whose length changed: its old and new extent, relative to the body start.</summary>
    internal readonly record struct Resized(int OldStart, int OldEnd, int NewStart, int NewEnd);

    internal readonly record struct Function(
        uint OldStart, uint OldBody, uint OldEnd,
        bool Kept, uint NewStart, uint NewBody, uint NewEnd,
        IReadOnlyList<Resized> Instructions);

    private readonly List<Function> _functions;
    private readonly uint[] _starts;
    private readonly Dictionary<uint, int[]> _resizedStarts = [];

    /// <summary>Maps through <paramref name="functions"/>, which it sorts by old start and keeps.</summary>
    public CodeAddressMap(List<Function> functions)
    {
        functions.Sort(static (a, b) => a.OldStart.CompareTo(b.OldStart));
        _functions = functions;
        _starts = new uint[functions.Count];
        for (int i = 0; i < functions.Count; i++)
            _starts[i] = functions[i].OldStart;
    }

    /// <summary>
    /// The LLD tombstones and the pre-tombstone convention (0) name no code; they pass through unchanged.
    /// </summary>
    public static bool IsSpecial(ulong address) => address == 0 || address >= 0xFFFFFFFE;

    /// <summary>
    /// Maps <paramref name="address"/>: an instruction boundary (or a function's start or end) of a kept function
    /// maps exactly, and an address inside an instruction keeps its distance from that instruction's end; one in a
    /// removed function reports <see cref="Outcome.Removed"/>; anything outside every function is invalid.
    /// </summary>
    public Outcome Map(ulong address, out uint mapped)
    {
        mapped = (uint)address;
        if (IsSpecial(address))
            return Outcome.Special;
        if (address > uint.MaxValue)
            return Outcome.Invalid;

        int index = Array.BinarySearch(_starts, (uint)address);
        if (index < 0)
            index = ~index - 1;
        if (index < 0)
            return Outcome.Invalid;

        Function f = _functions[index];
        uint a = (uint)address;
        if (a > f.OldEnd)
            return Outcome.Invalid;
        if (!f.Kept)
            return Outcome.Removed;
        if (a == f.OldEnd)
        {
            mapped = f.NewEnd;
            return Outcome.Mapped;
        }

        if (a < f.OldBody)
        {
            // Only the function start is a meaningful address in the locals declarations, unless they kept their size.
            if (a == f.OldStart)
                mapped = f.NewStart;
            else if (f.OldBody - f.OldStart == f.NewBody - f.NewStart)
                mapped = f.NewStart + (a - f.OldStart);
            else
                return Outcome.Invalid;
            return Outcome.Mapped;
        }

        int offset = (int)(a - f.OldBody);
        int newOffset = offset;
        if (f.Instructions.Count > 0)
        {
            if (!_resizedStarts.TryGetValue(f.OldStart, out int[]? starts))
            {
                starts = new int[f.Instructions.Count];
                for (int j = 0; j < starts.Length; j++)
                    starts[j] = f.Instructions[j].OldStart;
                _resizedStarts[f.OldStart] = starts;
            }

            int i = Array.BinarySearch(starts, offset);
            if (i < 0)
                i = ~i - 1;
            if (i >= 0)
            {
                Resized r = f.Instructions[i];
                // The start of a resized instruction maps to its new start. An address inside one (LLVM ends some
                // operand-stack location ranges one byte before an instruction's end) keeps its distance from the
                // instruction's end, clamped to its start.
                newOffset = offset == r.OldStart
                    ? r.NewStart
                    : offset < r.OldEnd
                    ? Math.Max(r.NewStart, r.NewEnd - (r.OldEnd - offset))
                    : r.NewEnd + (offset - r.OldEnd);
            }
        }

        mapped = f.NewBody + (uint)newOffset;
        return Outcome.Mapped;
    }
}

/// <summary>
/// Rewrites the code addresses of DWARF 2–4 (32-bit, 4-byte addresses, as wasm32 compilers emit it) through a
/// <see cref="CodeAddressMap"/>: <c>DW_FORM_addr</c> attributes and <c>DW_AT_high_pc</c> lengths in
/// <c>.debug_info</c>, <c>.debug_ranges</c>, <c>.debug_loc</c> and <c>.debug_aranges</c> entries (patched in
/// place, so those sections keep their size), and <c>.debug_line</c> programs (re-encoded, with every
/// <c>DW_AT_stmt_list</c> following them). Addresses in removed functions become LLD's tombstones: -2 in
/// <c>.debug_ranges</c>/<c>.debug_loc</c>, -1 elsewhere. Anything it cannot rewrite exactly is a failure,
/// never a silent approximation, except a <c>.debug_aranges</c> tuple that is neither a code range of
/// <c>.debug_info</c> nor a variable's address: that drops the section alone (consumers rebuild it), with a warning.
/// </summary>
internal static class DwarfCodeAddressRewriter
{
    private const uint Tombstone = 0xFFFFFFFF;
    private const uint ListTombstone = 0xFFFFFFFE;

    // Sections that hold no code address and no offset into a section this rewriter resizes.
    private static readonly HashSet<string> s_addressFree = new(StringComparer.Ordinal)
    {
        ".debug_str", ".debug_abbrev", ".debug_pubnames", ".debug_pubtypes", ".debug_gnu_pubnames",
        ".debug_gnu_pubtypes", ".debug_macinfo",
    };

    private sealed class DwarfRewriteException(string message) : Exception(message);

    /// <summary>The rewritten DWARF.</summary>
    /// <param name="Sections">The input sections in order, rewritten; null for a section that was dropped.</param>
    /// <param name="Globals">
    /// The wasm global indices its expressions name (<c>DW_OP_WASM_location</c> 0x01/0x03), which must not move; null
    /// when an expression could not be read, so any global may be named.
    /// </param>
    /// <param name="Warnings">Why a section was dropped.</param>
    internal sealed record Result(List<CustomSection?> Sections, IReadOnlySet<uint>? Globals, List<string> Warnings);

    // What the expressions of .debug_info and .debug_loc name: wasm globals, and data addresses (DW_OP_addr) per unit.
    private sealed class ExpressionFacts
    {
        public readonly HashSet<uint> Globals = [];
        public readonly HashSet<(uint Unit, uint Address)> DataAddresses = [];
        public bool Complete = true;
    }

    /// <summary>
    /// Rewrites <paramref name="sections"/> (all of a module's DWARF sections); returns the rewritten sections in
    /// the same order, or null with the reason they cannot be rewritten.
    /// </summary>
    public static Result? TryRewrite(IReadOnlyList<CustomSection> sections, CodeAddressMap map, out string? reason)
    {
        try
        {
            reason = null;
            return Rewrite(sections, map);
        }
        catch (Exception ex) when (ex is DwarfRewriteException or DwarfFormatException or ArgumentOutOfRangeException or IndexOutOfRangeException or OverflowException)
        {
            reason = ex is DwarfRewriteException ? ex.Message : $"its DWARF is malformed ({ex.Message})";
            return null;
        }
    }

    private static Result Rewrite(IReadOnlyList<CustomSection> sections, CodeAddressMap map)
    {
        var byName = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (var section in sections)
        {
            if (!s_addressFree.Contains(section.Name) &&
                section.Name is not (".debug_info" or ".debug_line" or ".debug_ranges" or ".debug_loc" or ".debug_aranges"))
            {
                throw new DwarfRewriteException($"its section '{section.Name}' is not supported by the DWARF rewriter");
            }

            if (!byName.TryAdd(section.Name, section.Content))
                throw new DwarfRewriteException($"it has two '{section.Name}' sections");
        }

        if (!byName.TryGetValue(".debug_info", out byte[]? info))
            throw new DwarfRewriteException("it has no .debug_info section");
        byName.TryGetValue(".debug_abbrev", out byte[]? abbrev);

        info = (byte[])info.Clone();
        var units = new List<UnitInfo>();
        var rangeRefs = new List<(uint Offset, int Unit)>();
        var locRefs = new List<(uint Offset, int Unit)>();
        var stmtListPatches = new List<(int Position, int Size, uint Offset)>();
        var codeRanges = new CodeRanges();
        var facts = new ExpressionFacts();
        var warnings = new List<string>();
        RewriteInfo(info, abbrev ?? throw new DwarfRewriteException("it has no .debug_abbrev section"), map, units, rangeRefs, locRefs, stmtListPatches, codeRanges, facts);

        var rewritten = new Dictionary<string, byte[]>(StringComparer.Ordinal) { [".debug_info"] = info };
        if (byName.TryGetValue(".debug_ranges", out byte[]? ranges))
            rewritten[".debug_ranges"] = RewriteLists(ranges, rangeRefs, units, map, codeRanges, null);
        else if (rangeRefs.Count > 0)
            throw new DwarfRewriteException("DW_AT_ranges refers to a missing .debug_ranges section");
        if (byName.TryGetValue(".debug_loc", out byte[]? loc))
            rewritten[".debug_loc"] = RewriteLists(loc, locRefs, units, map, null, facts);
        else if (locRefs.Count > 0)
            throw new DwarfRewriteException("a location list refers to a missing .debug_loc section");
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        if (byName.TryGetValue(".debug_aranges", out byte[]? aranges))
        {
            if (RewriteAranges(aranges, map, codeRanges, facts, out string? unclassified) is { } newAranges)
            {
                rewritten[".debug_aranges"] = newAranges;
            }
            else
            {
                dropped.Add(".debug_aranges");
                warnings.Add($".debug_aranges was dropped (consumers rebuild it from .debug_info): {unclassified}");
            }
        }
        if (byName.TryGetValue(".debug_line", out byte[]? line))
        {
            (byte[] newLine, Dictionary<uint, uint> offsets) = RewriteLine(line, map);
            rewritten[".debug_line"] = newLine;
            foreach (var (position, size, offset) in stmtListPatches)
            {
                if (!offsets.TryGetValue(offset, out uint newOffset))
                    throw new DwarfRewriteException($"DW_AT_stmt_list 0x{offset:x} is not the start of a line program");
                WriteUnsigned(info, position, size, newOffset);
            }
        }
        else if (stmtListPatches.Count > 0)
        {
            throw new DwarfRewriteException("DW_AT_stmt_list refers to a missing .debug_line section");
        }

        var result = new List<CustomSection?>(sections.Count);
        foreach (var section in sections)
        {
            result.Add(dropped.Contains(section.Name) ? null
                : rewritten.TryGetValue(section.Name, out byte[]? content) ? new CustomSection(section.Name, content)
                : section);
        }

        return new Result(result, facts.Complete ? facts.Globals : null, warnings);
    }

    private sealed class UnitInfo
    {
        public uint Offset;
        public uint BaseOld;
        public int Version;
    }

    // The code ranges (unit offset, start, length) .debug_info and its range lists describe. .debug_aranges also
    // lists variables, whose data addresses share no address space with code, so only a tuple that matches one
    // of these is a code range.
    private sealed class CodeRanges : HashSet<(uint Unit, uint Start, uint Length)>;

    // ── .debug_info ──────────────────────────────────────────────────────────

    // The abbreviation table at `offset`, read with the shared parser. DW_FORM_implicit_const keeps its value in
    // the table, not in .debug_info; the rewriter only rewrites DWARF 2–4, where it does not exist.
    private static Dictionary<uint, DwarfAbbrevEntry> ReadAbbreviations(ReadOnlySpan<byte> abbrev, ulong offset)
    {
        if (offset >= (ulong)abbrev.Length)
            throw new DwarfRewriteException($"the abbreviation offset 0x{offset:x} is past the end of .debug_abbrev");
        int pos = (int)offset;
        var table = DwarfAbbrevParser.ParseTable(abbrev, ref pos);
        foreach (var entry in table.Values)
        {
            foreach (var (_, form, _) in entry.Attributes)
            {
                if (form == (uint)DwForm.ImplicitConst)
                    throw new DwarfRewriteException("DW_FORM_implicit_const (DWARF 5) is not supported");
            }
        }

        return table;
    }

    // Attributes whose sec_offset (DWARF 4) or data4 (DWARF 2/3) value is a location list.
    private static bool IsLocationListAttribute(uint name) =>
        name is DwAt.Location or DwAt.StringLength or DwAt.ReturnAddr or DwAt.DataMemberLocation or DwAt.FrameBase
            or DwAt.Segment or DwAt.StaticLink or DwAt.UseLocation or DwAt.VtableElemLocation;

    private static void RewriteInfo(
        Span<byte> info, ReadOnlySpan<byte> abbrev, CodeAddressMap map, List<UnitInfo> units,
        List<(uint, int)> rangeRefs, List<(uint, int)> locRefs, List<(int, int, uint)> stmtListPatches, CodeRanges codeRanges,
        ExpressionFacts facts)
    {
        var abbrevCache = new Dictionary<ulong, Dictionary<uint, DwarfAbbrevEntry>>();
        var walk = new DwarfUnitEnumerator(info, ".debug_info");
        while (walk.MoveNext())
        {
            if (walk.Error is { } lengthError)
                throw lengthError;
            var frame = walk.Current;
            if (frame.Is64Bit)
                throw new DwarfRewriteException("64-bit DWARF is not supported");
            if (!DwarfUnitHeader.TryRead(info, frame, ".debug_info", out var header) || header.Version > 4)
                throw new DwarfRewriteException($"DWARF version {header.Version} is not supported (only 2 to 4)");
            if (header.AddressSize != 4)
                throw new DwarfRewriteException($"DWARF address size {header.AddressSize} is not supported");
            if (!abbrevCache.TryGetValue(header.AbbrevOffset, out var abbreviations))
                abbrevCache[header.AbbrevOffset] = abbreviations = ReadAbbreviations(abbrev, header.AbbrevOffset);

            int unitStart = frame.UnitStart;
            int unitEnd = frame.UnitEnd;
            int version = header.Version;
            var format = header.Format;
            // Values are read bounded by the unit, so a malformed unit cannot read into the next one.
            ReadOnlySpan<byte> unitData = info[..unitEnd];
            int pos = header.DieStart;

            var unit = new UnitInfo { Offset = (uint)unitStart, Version = version };
            int unitIndex = units.Count;
            units.Add(unit);
            bool first = true;
            while (pos < unitEnd)
            {
                ulong code = DwarfReaderHelpers.ReadULEB128U64(unitData, ref pos);
                if (code == 0)
                    continue;
                if (code > uint.MaxValue || !abbreviations.TryGetValue((uint)code, out var abbreviation))
                    throw new DwarfRewriteException($"abbreviation {code} of the unit at 0x{unitStart:x} is not defined");

                ulong? lowPcOld = null;
                ulong? highPcOld = null;
                (int Position, uint Form, int Width, ulong Length)? highPcLength = null;
                foreach (var spec in abbreviation.Attributes)
                {
                    uint form = spec.Form;
                    while (form == (uint)DwForm.Indirect)
                        form = (uint)DwarfReaderHelpers.ReadULEB128U64(unitData, ref pos);
                    int valueStart = pos;
                    ulong value = ReadForm(unitData, ref pos, form, format);
                    // exprloc is always an expression; before DWARF 4, a block is one for a location attribute.
                    if ((DwForm)form == DwForm.Exprloc || ((DwForm)form is DwForm.Block2 or DwForm.Block4 or DwForm.Block or DwForm.Block1 && IsLocationListAttribute(spec.Name)))
                        ScanExpression(info, BlockContentStart(info, valueStart, form), pos, unit.Offset, facts);
                    switch ((DwForm)form)
                    {
                        case DwForm.Addr:
                        {
                            if (spec.Name == DwAt.LowPc)
                                lowPcOld = value;
                            else if (spec.Name == DwAt.HighPc)
                                highPcOld = value;
                            switch (map.Map(value, out uint mapped))
                            {
                                case CodeAddressMap.Outcome.Mapped:
                                    WriteUnsigned(info, valueStart, 4, mapped);
                                    break;
                                case CodeAddressMap.Outcome.Removed:
                                    WriteUnsigned(info, valueStart, 4, Tombstone);
                                    break;
                                case CodeAddressMap.Outcome.Invalid:
                                    throw new DwarfRewriteException($"attribute 0x{spec.Name:x} at .debug_info+0x{valueStart:x} names 0x{value:x}, which is not an instruction boundary");
                            }

                            break;
                        }

                        case DwForm.Data2 or DwForm.Data4 or DwForm.Data8 or DwForm.Data1 or DwForm.Udata when spec.Name == DwAt.HighPc: // high_pc as a length
                            highPcLength = (valueStart, form, pos - valueStart, value);
                            break;
                        case DwForm.Data4 or DwForm.SecOffset when spec.Name == DwAt.Ranges:
                            rangeRefs.Add(((uint)value, unitIndex));
                            break;
                        case DwForm.Data4 or DwForm.SecOffset when spec.Name == DwAt.StmtList:
                            stmtListPatches.Add((valueStart, 4, (uint)value));
                            break;
                        case DwForm.SecOffset when IsLocationListAttribute(spec.Name):
                        case DwForm.Data4 when version < 4 && IsLocationListAttribute(spec.Name):
                            locRefs.Add(((uint)value, unitIndex));
                            break;
                        case DwForm.SecOffset when spec.Name is not (DwAt.MacroInfo or DwAt.GnuMacros): // address-free
                            throw new DwarfRewriteException($"attribute 0x{spec.Name:x} with DW_FORM_sec_offset is not supported");
                    }
                }

                if (lowPcOld is { } start && (highPcOld ?? (highPcLength is { } hl ? start + hl.Length : null)) is { } stop && stop > start)
                    codeRanges.Add((unit.Offset, (uint)start, (uint)(stop - start)));

                if (highPcLength is { } h && lowPcOld is { } low)
                {
                    if (map.Map(low, out uint newLow) == CodeAddressMap.Outcome.Mapped)
                    {
                        if (map.Map(low + h.Length, out uint newHigh) != CodeAddressMap.Outcome.Mapped || newHigh < newLow)
                            throw new DwarfRewriteException($"DW_AT_high_pc at .debug_info+0x{h.Position:x} does not end in the function it starts in");
                        WriteFormValue(info, h.Position, h.Form, h.Width, newHigh - newLow);
                    }
                }

                if (first)
                {
                    unit.BaseOld = (uint)(lowPcOld ?? 0);
                    first = false;
                }
            }
        }
    }

    // Reads one attribute value with the shared form reader. Only the forms the rewriter knows to hold no code
    // address it cannot find (or one it rewrites) are accepted; anything else drops the DWARF.
    private static ulong ReadForm(ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format)
    {
        if ((DwForm)form is not (DwForm.Addr or DwForm.Block2 or DwForm.Block4 or DwForm.Data2 or DwForm.Data4
            or DwForm.Data8 or DwForm.String or DwForm.Block or DwForm.Exprloc or DwForm.Block1 or DwForm.Data1
            or DwForm.Flag or DwForm.Sdata or DwForm.Strp or DwForm.Udata or DwForm.RefAddr or DwForm.Ref1
            or DwForm.Ref2 or DwForm.Ref4 or DwForm.Ref8 or DwForm.RefSig8 or DwForm.RefUdata or DwForm.SecOffset
            or DwForm.FlagPresent or DwForm.GnuRefAlt or DwForm.GnuStrpAlt))
        {
            throw new DwarfRewriteException($"DW_FORM 0x{form:x} (DWARF {format.Version}) is not supported");
        }

        ulong value = DwarfReaderHelpers.ReadFormRaw(data, ref pos, form, format);
        if (form == (uint)DwForm.String && data[pos - 1] != 0)
            throw new DwarfRewriteException($"the string at .debug_info+0x{pos:x} is not terminated before the end of its unit");
        return value;
    }

    private static void WriteFormValue(Span<byte> data, int position, uint form, int width, uint value)
    {
        switch ((DwForm)form)
        {
            case DwForm.Data1 when value <= byte.MaxValue: data[position] = (byte)value; break;
            case DwForm.Data2 when value <= ushort.MaxValue: BinaryPrimitives.WriteUInt16LittleEndian(data[position..], (ushort)value); break;
            case DwForm.Data4: WriteUnsigned(data, position, 4, value); break;
            case DwForm.Data8: BinaryPrimitives.WriteUInt64LittleEndian(data[position..], value); break;
            case DwForm.Udata when value < 1UL << (7 * width):
                WasmLeb128.WritePaddedULeb128(data.Slice(position, width), value);
                break;
            default:
                throw new DwarfRewriteException($"a DW_AT_high_pc length of {value} does not fit its form 0x{form:x}");
        }
    }

    private static int BlockContentStart(ReadOnlySpan<byte> data, int valueStart, uint form)
    {
        int pos = valueStart;
        switch ((DwForm)form)
        {
            case DwForm.Block1: pos += 1; break;
            case DwForm.Block2: pos += 2; break;
            case DwForm.Block4: pos += 4; break;
            default: DwarfReaderHelpers.ReadULEB128U64(data, ref pos); break; // block, exprloc
        }

        return pos;
    }

    /// <summary>
    /// Records the wasm globals (<c>DW_OP_WASM_location</c> 0x01/0x03) and data addresses (<c>DW_OP_addr</c>) a DWARF
    /// expression names. An operation it does not know makes the facts incomplete: any global may then be named.
    /// </summary>
    private static void ScanExpression(ReadOnlySpan<byte> data, int pos, int end, uint unitOffset, ExpressionFacts facts)
    {
        while (pos < end)
        {
            byte op = data[pos++];
            switch (op)
            {
                case DwOp.Addr:
                    facts.DataAddresses.Add((unitOffset, ReadU32(data, ref pos)));
                    break;
                case DwOp.WasmLocation: // local (0), global (1, LEB; 3, u32) or operand stack (2)
                {
                    byte kind = data[pos++];
                    uint index = kind == 3 ? ReadU32(data, ref pos) : (uint)DwarfReaderHelpers.ReadULEB128U64(data, ref pos);
                    if (kind is 1 or 3)
                        facts.Globals.Add(index);
                    else if (kind is not (0 or 2))
                    {
                        facts.Complete = false;
                        return;
                    }

                    break;
                }

                case DwOp.Const1u or DwOp.Const1s or DwOp.Pick or DwOp.DerefSize or DwOp.XderefSize: pos += 1; break;
                case DwOp.Const2u or DwOp.Const2s or DwOp.Bra or DwOp.Skip or DwOp.Call2: pos += 2; break;
                case DwOp.Const4u or DwOp.Const4s or DwOp.Call4 or DwOp.CallRef: pos += 4; break;
                case DwOp.Const8u or DwOp.Const8s: pos += 8; break;
                case DwOp.Constu or DwOp.PlusUconst or DwOp.Regx or DwOp.Piece: DwarfReaderHelpers.ReadULEB128U64(data, ref pos); break;
                case DwOp.Consts or DwOp.Fbreg or (>= DwOp.Breg0 and <= DwOp.Breg31): DwarfReaderHelpers.ReadSLEB128(data, ref pos); break;
                case DwOp.Bregx: DwarfReaderHelpers.ReadULEB128U64(data, ref pos); DwarfReaderHelpers.ReadSLEB128(data, ref pos); break;
                case DwOp.BitPiece: DwarfReaderHelpers.ReadULEB128U64(data, ref pos); DwarfReaderHelpers.ReadULEB128U64(data, ref pos); break;
                case DwOp.ImplicitValue: { int n = checked((int)DwarfReaderHelpers.ReadULEB128U64(data, ref pos)); pos = checked(pos + n); break; }
                case DwOp.EntryValue or DwOp.GnuEntryValue: // a nested expression
                {
                    int n = checked((int)DwarfReaderHelpers.ReadULEB128U64(data, ref pos));
                    ScanExpression(data, pos, checked(pos + n), unitOffset, facts);
                    pos += n;
                    break;
                }

                case DwOp.Deref or DwOp.Dup or DwOp.Drop or DwOp.Over or (>= DwOp.Swap and <= DwOp.Plus) or
                    (>= DwOp.Shl and <= DwOp.Xor) or (>= DwOp.Eq and <= DwOp.Ne) or (>= DwOp.Lit0 and <= DwOp.Reg31) or
                    DwOp.Nop or DwOp.PushObjectAddress or DwOp.FormTlsAddress or DwOp.CallFrameCfa or DwOp.StackValue or
                    DwOp.GnuPushTlsAddress:
                    break; // no operands
                default:
                    facts.Complete = false;
                    return;
            }
        }
    }

    // ── .debug_ranges / .debug_loc ───────────────────────────────────────────

    // .debug_ranges (codeRanges receives its ranges) or .debug_loc (facts receives what its expressions name).
    private static byte[] RewriteLists(ReadOnlySpan<byte> original, List<(uint Offset, int Unit)> refs, List<UnitInfo> units, CodeAddressMap map, CodeRanges? codeRanges, ExpressionFacts? facts)
    {
        byte[] data = original.ToArray();
        var bases = new Dictionary<uint, uint>();
        foreach (var (offset, unit) in refs)
        {
            uint baseOld = units[unit].BaseOld;
            if (bases.TryGetValue(offset, out uint existing))
            {
                if (existing != baseOld)
                    throw new DwarfRewriteException($"list 0x{offset:x} is used by units with different base addresses");
                continue;
            }

            bases[offset] = baseOld;
            RewriteList(data, original, (int)offset, baseOld, map, codeRanges, facts, units[unit].Offset);
        }

        return data;
    }

    private static void RewriteList(Span<byte> data, ReadOnlySpan<byte> original, int pos, uint baseOld, CodeAddressMap map, CodeRanges? codeRanges, ExpressionFacts? facts, uint unitOffset)
    {
        uint baseNew = baseOld;
        bool baseRemoved = false;
        if (baseOld != 0)
            (baseNew, baseRemoved) = MapBase(baseOld, map);

        while (true)
        {
            int entry = pos;
            uint begin = ReadU32(original, ref pos);
            uint end = ReadU32(original, ref pos);
            if (begin == 0 && end == 0)
                return;
            if (begin == 0xFFFFFFFF)
            {
                baseOld = end;
                (baseNew, baseRemoved) = MapBase(end, map);
                WriteUnsigned(data, entry + 4, 4, baseNew);
                continue;
            }

            if (facts is not null)
            {
                int expressionLength = ReadU16(original, ref pos);
                ScanExpression(original, pos, checked(pos + expressionLength), unitOffset, facts);
                pos += expressionLength;
            }
            if (baseRemoved || (CodeAddressMap.IsSpecial(begin) && CodeAddressMap.IsSpecial(end)))
                continue;

            ulong absBegin = (ulong)baseOld + begin, absEnd = (ulong)baseOld + end;
            if (absEnd > absBegin)
                codeRanges?.Add((unitOffset, (uint)absBegin, (uint)(absEnd - absBegin)));
            var outcome = map.Map(absBegin, out uint newBegin);
            if (outcome == CodeAddressMap.Outcome.Special)
                continue;
            if (outcome == CodeAddressMap.Outcome.Removed)
            {
                WriteUnsigned(data, entry, 4, ListTombstone);
                WriteUnsigned(data, entry + 4, 4, ListTombstone);
                continue;
            }

            if (outcome != CodeAddressMap.Outcome.Mapped || map.Map(absEnd, out uint newEnd) != CodeAddressMap.Outcome.Mapped || newEnd < newBegin)
                throw new DwarfRewriteException($"the list entry at 0x{entry:x} [0x{absBegin:x}, 0x{absEnd:x}) does not lie within one function");
            uint relBegin = newBegin - baseNew, relEnd = newEnd - baseNew;
            if ((relBegin == 0 && relEnd == 0) || relBegin == 0xFFFFFFFF)
                throw new DwarfRewriteException($"the list entry at 0x{entry:x} would become a terminator or base selection");
            WriteUnsigned(data, entry, 4, relBegin);
            WriteUnsigned(data, entry + 4, 4, relEnd);
        }
    }

    private static (uint New, bool Removed) MapBase(uint baseOld, CodeAddressMap map) => map.Map(baseOld, out uint mapped) switch
    {
        CodeAddressMap.Outcome.Mapped or CodeAddressMap.Outcome.Special => (mapped, false),
        CodeAddressMap.Outcome.Removed => (ListTombstone, true),
        _ => throw new DwarfRewriteException($"base address 0x{baseOld:x} is not an instruction boundary"),
    };

    // ── .debug_aranges ───────────────────────────────────────────────────────

    // Null (with the reason) when a tuple is neither a code range of .debug_info nor a variable's data address.
    private static byte[]? RewriteAranges(ReadOnlySpan<byte> original, CodeAddressMap map, CodeRanges codeRanges, ExpressionFacts facts, out string? unclassified)
    {
        unclassified = null;
        byte[] data = original.ToArray();
        int pos = 0;
        while (pos < original.Length)
        {
            int setStart = pos;
            int setEnd = ReadUnitLength32(original, ref pos, ".debug_aranges");
            pos += 2; // version
            uint unitOffset = ReadU32(original, ref pos);
            int addressSize = original[pos++];
            int segmentSize = original[pos++];
            if (addressSize != 4 || segmentSize != 0)
                throw new DwarfRewriteException($".debug_aranges address size {addressSize}/segment size {segmentSize} is not supported");
            pos = setStart + ((pos - setStart + 7) & ~7);
            while (pos + 8 <= setEnd)
            {
                int tuple = pos;
                uint address = ReadU32(original, ref pos);
                uint length = ReadU32(original, ref pos);
                if (address == 0 && length == 0)
                    break;
                if (!codeRanges.Contains((unitOffset, address, length)))
                {
                    if (CodeAddressMap.IsSpecial(address) || facts.DataAddresses.Contains((unitOffset, address)))
                        continue;
                    unclassified = $"its range [0x{address:x}, +0x{length:x}) at .debug_aranges+0x{tuple:x} is neither a code range of .debug_info nor a variable's address";
                    return null;
                }
                switch (map.Map(address, out uint newAddress))
                {
                    case CodeAddressMap.Outcome.Mapped:
                        if (map.Map((ulong)address + length, out uint newEnd) != CodeAddressMap.Outcome.Mapped || newEnd < newAddress)
                            throw new DwarfRewriteException($"the address range at .debug_aranges+0x{tuple:x} does not lie within one function");
                        WriteUnsigned(data, tuple, 4, newAddress);
                        WriteUnsigned(data, tuple + 4, 4, newEnd - newAddress);
                        break;
                    case CodeAddressMap.Outcome.Removed:
                        WriteUnsigned(data, tuple, 4, Tombstone);
                        break;
                    case CodeAddressMap.Outcome.Invalid:
                        throw new DwarfRewriteException($"the address range at .debug_aranges+0x{tuple:x} starts at 0x{address:x}, which is not an instruction boundary");
                }
            }

            pos = setEnd;
        }

        return data;
    }

    // ── .debug_line ──────────────────────────────────────────────────────────

    private static (byte[] Data, Dictionary<uint, uint> Offsets) RewriteLine(ReadOnlySpan<byte> original, CodeAddressMap map)
    {
        // Programs are re-encoded into one buffer, then assembled with their headers into an exactly sized section.
        var programs = new MemoryStream(original.Length);
        var units = new List<(int UnitStart, int HeaderBytes, int ProgramStart, int ProgramLength)>();
        int pos = 0;
        while (pos < original.Length)
        {
            int unitStart = pos;
            int unitEnd = ReadUnitLength32(original, ref pos, ".debug_line");
            int version = ReadU16(original, ref pos);
            if (version is < 2 or > 4)
                throw new DwarfRewriteException($"line table version {version} is not supported (only 2 to 4)");
            uint headerLength = ReadU32(original, ref pos);
            int programStart = checked(pos + (int)headerLength);
            int minimumInstructionLength = original[pos++];
            if (version >= 4)
                pos++; // maximum_operations_per_instruction
            pos++; // default_is_stmt
            int lineBase = (sbyte)original[pos++];
            int lineRange = original[pos++];
            int opcodeBase = original[pos++];
            if (minimumInstructionLength != 1 || lineRange == 0 || opcodeBase == 0)
                throw new DwarfRewriteException($"the line table at 0x{unitStart:x} has minimum_instruction_length {minimumInstructionLength}, line_range {lineRange}, opcode_base {opcodeBase}");
            ReadOnlySpan<byte> standardLengths = original.Slice(pos, opcodeBase - 1);

            int newProgramStart = checked((int)programs.Length);
            RewriteLineProgram(original, programStart, unitEnd, programs, map, lineBase, lineRange, opcodeBase, standardLengths, unitStart);
            units.Add((unitStart, programStart - (unitStart + 4), newProgramStart, checked((int)programs.Length - newProgramStart)));
            pos = unitEnd;
        }

        int total = 0;
        foreach (var unit in units)
            total = checked(total + 4 + unit.HeaderBytes + unit.ProgramLength);
        byte[] result = new byte[total];
        var offsets = new Dictionary<uint, uint>(units.Count);
        ReadOnlySpan<byte> encoded = programs.GetBuffer().AsSpan(0, (int)programs.Length);
        int at = 0;
        foreach (var (unitStart, headerBytes, programStart, programLength) in units)
        {
            offsets[(uint)unitStart] = (uint)at;
            BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(at), checked((uint)(headerBytes + programLength)));
            original.Slice(unitStart + 4, headerBytes).CopyTo(result.AsSpan(at + 4));
            encoded.Slice(programStart, programLength).CopyTo(result.AsSpan(at + 4 + headerBytes));
            at += 4 + headerBytes + programLength;
        }

        return (result, offsets);
    }

    private static void RewriteLineProgram(
        ReadOnlySpan<byte> code, int pos, int end, MemoryStream output, CodeAddressMap map,
        int lineBase, int lineRange, int opcodeBase, ReadOnlySpan<byte> standardLengths, int unitStart)
    {
        bool mapped = false; // true while the sequence's addresses are being mapped; false passes it through
        Span<byte> value = stackalloc byte[4];
        ulong oldAddress = 0;
        ulong newAddress = 0;

        while (pos < end)
        {
            int opStart = pos;
            byte op = code[pos++];
            if (op >= opcodeBase)
            {
                int adjusted = op - opcodeBase;
                ulong addressAdvance = (ulong)(adjusted / lineRange);
                int lineAdvance = lineBase + adjusted % lineRange;
                if (!mapped)
                {
                    output.WriteByte(op);
                    continue;
                }

                oldAddress += addressAdvance;
                ulong delta = AdvanceTo(oldAddress, ref newAddress, map, unitStart);
                ulong fused = (ulong)(lineAdvance - lineBase) + (ulong)lineRange * delta + (ulong)opcodeBase;
                if (fused <= 255)
                {
                    output.WriteByte((byte)fused);
                }
                else
                {
                    output.WriteByte(DwLns.AdvancePc);
                    WasmEncoder.WriteU32Leb(output, checked((uint)delta));
                    output.WriteByte((byte)(lineAdvance - lineBase + opcodeBase));
                }

                continue;
            }

            if (op == 0)
            {
                ulong length = DwarfReaderHelpers.ReadULEB128U64(code, ref pos);
                int extendedEnd = checked(pos + (int)length);
                byte sub = code[pos];
                if (sub == DwLne.EndSequence)
                {
                    if (mapped)
                        EmitAdvance(output, AdvanceTo(oldAddress, ref newAddress, map, unitStart));
                    output.Write(code.Slice(opStart, extendedEnd - opStart));
                    mapped = false;
                    oldAddress = newAddress = 0;
                }
                else if (sub == DwLne.SetAddress)
                {
                    if (length != 5)
                        throw new DwarfRewriteException($"DW_LNE_set_address in the line table at 0x{unitStart:x} has a {length - 1}-byte address");
                    uint address = BinaryPrimitives.ReadUInt32LittleEndian(code[(pos + 1)..]);
                    output.Write(code.Slice(opStart, pos + 1 - opStart));
                    switch (map.Map(address, out uint newValue))
                    {
                        case CodeAddressMap.Outcome.Mapped:
                            mapped = true;
                            oldAddress = address;
                            newAddress = newValue;
                            break;
                        case CodeAddressMap.Outcome.Removed:
                            mapped = false;
                            newValue = Tombstone;
                            break;
                        case CodeAddressMap.Outcome.Special:
                            mapped = false;
                            break;
                        default:
                            throw new DwarfRewriteException($"DW_LNE_set_address 0x{address:x} in the line table at 0x{unitStart:x} is not an instruction boundary");
                    }

                    BinaryPrimitives.WriteUInt32LittleEndian(value, newValue);
                    output.Write(value);
                }
                else
                {
                    output.Write(code.Slice(opStart, extendedEnd - opStart));
                }

                pos = extendedEnd;
                continue;
            }

            switch (op)
            {
                case DwLns.Copy:
                    if (mapped)
                        EmitAdvance(output, AdvanceTo(oldAddress, ref newAddress, map, unitStart));
                    output.WriteByte(op);
                    break;
                case DwLns.AdvancePc:
                {
                    ulong advance = DwarfReaderHelpers.ReadULEB128U64(code, ref pos);
                    if (mapped)
                        oldAddress += advance;
                    else
                        output.Write(code.Slice(opStart, pos - opStart));
                    break;
                }

                case DwLns.ConstAddPc:
                    if (mapped)
                        oldAddress += (ulong)((255 - opcodeBase) / lineRange);
                    else
                        output.WriteByte(op);
                    break;
                case DwLns.FixedAdvancePc:
                {
                    ulong advance = (ulong)ReadU16(code, ref pos);
                    if (mapped)
                        oldAddress += advance;
                    else
                        output.Write(code.Slice(opStart, pos - opStart));
                    break;
                }

                default:
                    for (int i = 0; i < standardLengths[op - 1]; i++)
                        DwarfReaderHelpers.ReadULEB128U64(code, ref pos);
                    output.Write(code.Slice(opStart, pos - opStart));
                    break;
            }
        }
    }

    private static ulong AdvanceTo(ulong oldAddress, ref ulong newAddress, CodeAddressMap map, int unitStart)
    {
        if (map.Map(oldAddress, out uint target) != CodeAddressMap.Outcome.Mapped || target < newAddress)
            throw new DwarfRewriteException($"a row at 0x{oldAddress:x} in the line table at 0x{unitStart:x} does not follow its sequence's function");
        ulong delta = target - newAddress;
        newAddress = target;
        return delta;
    }

    private static void EmitAdvance(MemoryStream output, ulong delta)
    {
        if (delta == 0)
            return;
        output.WriteByte(DwLns.AdvancePc);
        WasmEncoder.WriteU32Leb(output, checked((uint)delta));
    }

    // ── Primitives ───────────────────────────────────────────────────────────

    // A unit's unit_length, with the shared checks (reserved values, a unit past the section); returns the unit's end.
    private static int ReadUnitLength32(ReadOnlySpan<byte> data, ref int pos, string section)
    {
        int end = DwarfReaderHelpers.ReadUnitLength(data, ref pos, section, out bool is64Bit);
        if (is64Bit)
            throw new DwarfRewriteException("64-bit DWARF is not supported");
        return end;
    }

    private static uint ReadU32(ReadOnlySpan<byte> data, ref int pos)
    {
        uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.Slice(pos, 4));
        pos += 4;
        return value;
    }

    private static int ReadU16(ReadOnlySpan<byte> data, ref int pos)
    {
        int value = BinaryPrimitives.ReadUInt16LittleEndian(data.Slice(pos, 2));
        pos += 2;
        return value;
    }

    private static void WriteUnsigned(Span<byte> data, int position, int size, uint value)
    {
        if (size != 4)
            throw new DwarfRewriteException($"a {size}-byte field cannot be patched");
        BinaryPrimitives.WriteUInt32LittleEndian(data.Slice(position, 4), value);
    }
}
