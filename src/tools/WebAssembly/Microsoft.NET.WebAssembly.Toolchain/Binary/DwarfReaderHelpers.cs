// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// The DWARF form and DIE reader every <c>.debug_info</c> reader shares: form widths (one table, for
/// any address and offset size), attribute values, string and indexed-address resolution, DIE parsing
/// and type-name resolution. Unit framing and headers are <see cref="DwarfUnitEnumerator"/> and
/// <see cref="DwarfUnitHeader"/>; abbreviations are <see cref="DwarfAbbrevParser"/>.
/// </summary>
internal static class DwarfReaderHelpers
{
    internal sealed class DwarfDIE
    {
        public int Offset;
        public ushort Tag;
        public bool HasChildren;
        public Dictionary<ushort, object> Attributes = new();
        public int Depth;

        /// <summary>
        /// Attributes read with <c>DW_FORM_ref_addr</c>: their value is a .debug_info SECTION offset,
        /// not a CU-relative one like every other reference form. Null when there are none.
        /// </summary>
        public List<ushort>? SectionRefAttributes;
    }

    /// <summary>Sections and per-unit bases needed to resolve string and indexed-address forms.</summary>
    /// <param name="strData">.debug_str bytes.</param>
    /// <param name="lineStrData">.debug_line_str bytes.</param>
    /// <param name="strOffsetsData">.debug_str_offsets bytes.</param>
    /// <param name="strOffsetsBase">The unit's <c>DW_AT_str_offsets_base</c>.</param>
    /// <param name="addrData">.debug_addr bytes (DWARF 5 indexed addresses).</param>
    /// <param name="addrBase">The CU's <c>DW_AT_addr_base</c>; negative when it has none.</param>
    /// <param name="resolveAddrx">
    /// When true, <c>DW_FORM_addrx*</c> values are resolved through <paramref name="addrData"/> at
    /// <paramref name="addrBase"/> and an unresolvable index yields no value — an index must never be
    /// mistaken for an address. When false (legacy callers that read no addresses), the raw index is
    /// returned.
    /// </param>
    internal readonly ref struct StringContext(ReadOnlySpan<byte> strData, ReadOnlySpan<byte> lineStrData,
        ReadOnlySpan<byte> strOffsetsData, long strOffsetsBase,
        ReadOnlySpan<byte> addrData = default, long addrBase = -1, bool resolveAddrx = false)
    {
        public readonly ReadOnlySpan<byte> StrData = strData;
        public readonly ReadOnlySpan<byte> LineStrData = lineStrData;
        public readonly ReadOnlySpan<byte> StrOffsetsData = strOffsetsData;
        public readonly long StrOffsetsBase = strOffsetsBase;
        public readonly ReadOnlySpan<byte> AddrData = addrData;
        public readonly long AddrBase = addrBase;
        public readonly bool ResolveAddrx = resolveAddrx;
    }

    /// <summary>
    /// Reads the per-unit bases a DWARF 5 unit's root DIE carries (<c>DW_AT_str_offsets_base</c>,
    /// <c>DW_AT_addr_base</c>) without materializing the DIE, so string and address forms in the
    /// unit can be resolved while it is parsed — clang writes them after the names that need them.
    /// Every unit root has its own bases: compile, partial, type (<c>-fdebug-types-section</c>) and
    /// skeleton units. Returns (0, -1) for DWARF &lt; 5 or when absent.
    /// </summary>
    internal static (long StrOffsetsBase, long AddrBase) ReadUnitBases(
        ReadOnlySpan<byte> data, int diePos, int cuEnd,
        Dictionary<uint, DwarfAbbrevEntry> abbrevTable, in DwarfFormContext format)
    {
        long strOffsetsBase = 0;
        long addrBase = -1;
        if (format.Version < 5 || diePos >= cuEnd)
            return (strOffsetsBase, addrBase);
        int peekPos = diePos;
        uint code = ReadULEB128(data, ref peekPos);
        if (code == 0 || !abbrevTable.TryGetValue(code, out var entry)
            || entry.Tag is not (DwTag.CompileUnit or DwTag.PartialUnit or DwTag.TypeUnit or DwTag.SkeletonUnit))
            return (strOffsetsBase, addrBase);
        foreach (var (attrName, attrForm, _) in entry.Attributes)
        {
            if (attrName == DwAt.StrOffsetsBase)
                strOffsetsBase = (long)ReadUnsignedFormValue(data, ref peekPos, attrForm, format);
            else if (attrName == DwAt.AddrBase)
                addrBase = (long)ReadUnsignedFormValue(data, ref peekPos, attrForm, format);
            else
                SkipFormValue(data, ref peekPos, attrForm, format);
        }
        return (strOffsetsBase, addrBase);
    }

    internal static List<DwarfDIE> ParseDIEs(
        ReadOnlySpan<byte> data, int start, int end,
        Dictionary<uint, DwarfAbbrevEntry> abbrevTable,
        in DwarfFormContext format,
        in StringContext ctx,
        int cuStart,
        Func<ushort, bool>? keepTag = null)
    {
        var dies = new List<DwarfDIE>();
        int pos = start;
        int depth = 0;

        while (pos < end)
        {
            int dieOffset = pos - cuStart;
            uint abbrevCode = ReadULEB128(data, ref pos);
            if (abbrevCode == 0)
            {
                depth--;
                continue;
            }

            if (!abbrevTable.TryGetValue(abbrevCode, out var entry))
                break;

            // A filtered parse (keepTag) skips the attributes of every other DIE without
            // materializing them; depth is still tracked so kept DIEs keep their nesting.
            if (keepTag is not null && !keepTag(entry.Tag))
            {
                foreach (var (_, attrForm, _) in entry.Attributes)
                    SkipFormValue(data, ref pos, attrForm, format);
                if (entry.HasChildren)
                    depth++;
                continue;
            }

            var die = new DwarfDIE
            {
                Offset = dieOffset,
                Tag = entry.Tag,
                HasChildren = entry.HasChildren,
                Depth = depth
            };

            foreach (var (attrName, attrForm, implicitConst) in entry.Attributes)
            {
                // The effective form decides how a reference is interpreted, so look through
                // DW_FORM_indirect (whose operand is the real form) before recording provenance.
                uint effectiveForm = attrForm;
                for (int peek = pos; effectiveForm == (uint)DwForm.Indirect && peek < data.Length;)
                    effectiveForm = ReadULEB128(data, ref peek);
                var value = ReadAttributeValue(data, ref pos, attrForm, format, ctx, implicitConst);
                if (value is not null)
                    die.Attributes[attrName] = value;
                if (effectiveForm == (uint)DwForm.RefAddr)
                    (die.SectionRefAttributes ??= []).Add(attrName);
            }

            dies.Add(die);

            if (entry.HasChildren)
                depth++;
        }

        return dies;
    }

    /// <summary>
    /// Reads one attribute value: a <see cref="long"/> for constants, references, offsets and resolved
    /// addresses; a <see cref="string"/> for a resolved string; a <c>byte[]</c> for a block or expression;
    /// null for an indexed address that cannot be resolved.
    /// </summary>
    internal static object? ReadAttributeValue(
        ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format,
        in StringContext ctx, long implicitConst)
    {
        switch ((DwForm)form)
        {
            case DwForm.Addrx:
            case DwForm.Addrx1:
            case DwForm.Addrx2:
            case DwForm.Addrx3:
            case DwForm.Addrx4:
            case DwForm.GnuAddrIndex:
                return IndexedAddress((long)ReadFormRaw(data, ref pos, form, format), ctx, format.AddressSize);
            case DwForm.LlvmAddrxOffset:
            {
                long index = (long)ReadULEB128U64(data, ref pos);
                uint offset = ReadU32LE(data, pos);
                pos += 4;
                return IndexedAddress(index, ctx, format.AddressSize) is long address ? address + offset : null;
            }

            case DwForm.String:
                return ReadNullTerminatedAdvance(data, ref pos);

            case DwForm.Strp:
            case DwForm.LineStrp:
            case DwForm.Strx:
            case DwForm.Strx1:
            case DwForm.Strx2:
            case DwForm.Strx3:
            case DwForm.Strx4:
            case DwForm.GnuStrIndex:
                // An unresolvable string keeps its raw offset or index; an empty one does too.
                return TryReadString(data, ref pos, form, format, ctx, out var str, out ulong raw) && !str.IsEmpty
                    ? Encoding.UTF8.GetString(str)
                    : (long)raw;

            case DwForm.Sdata:
                return ReadSLEB128(data, ref pos);

            case DwForm.ImplicitConst:
                return implicitConst;

            // Block/exprloc forms — return raw bytes for location expression parsing
            case DwForm.Block1:
            case DwForm.Block2:
            case DwForm.Block4:
            case DwForm.Block:
            case DwForm.Exprloc:
            {
                int len = ReadBlockLength(data, ref pos, form);
                var block = data.Slice(pos, len).ToArray();
                pos += len;
                return block;
            }

            case DwForm.Data16:
                pos += 16;
                return 0L;

            case DwForm.Indirect:
                return ReadAttributeValue(data, ref pos, ReadIndirectForm(data, ref pos), format, ctx, 0);

            default:
                return (long)ReadFormRaw(data, ref pos, form, format);
        }
    }

    /// <summary>
    /// Reads a string-class attribute (<c>DW_FORM_string</c>, <c>strp</c>, <c>line_strp</c>, <c>strx*</c>)
    /// as its UTF-8 bytes, without decoding it. <paramref name="raw"/> is the section offset or string
    /// index. Returns false, with <paramref name="value"/> empty, when the string cannot be resolved (no
    /// section, an offset or index out of range). The cursor always moves past the attribute.
    /// </summary>
    internal static bool TryReadString(
        ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format, in StringContext ctx,
        out ReadOnlySpan<byte> value, out ulong raw)
    {
        if (form == (uint)DwForm.Indirect)
            form = ReadIndirectForm(data, ref pos);
        switch ((DwForm)form)
        {
            case DwForm.String:
            {
                int start = pos;
                int length = data[start..].IndexOf((byte)0);
                pos = length < 0 ? data.Length : start + length + 1;
                value = data[start..(length < 0 ? data.Length : start + length)];
                raw = 0;
                return true;
            }
            case DwForm.Strp:
                raw = ReadFormRaw(data, ref pos, form, format);
                return TrySliceCString(ctx.StrData, raw, out value);
            case DwForm.LineStrp:
                raw = ReadFormRaw(data, ref pos, form, format);
                return TrySliceCString(ctx.LineStrData, raw, out value);
            case DwForm.Strx:
            case DwForm.Strx1:
            case DwForm.Strx2:
            case DwForm.Strx3:
            case DwForm.Strx4:
            case DwForm.GnuStrIndex:
                raw = ReadFormRaw(data, ref pos, form, format);
                return TryResolveStrx(raw, ctx, format.OffsetSize, out value);
            default:
                SkipFormValue(data, ref pos, form, format);
                value = default;
                raw = 0;
                return false;
        }
    }

    /// <summary>Whether <paramref name="form"/> is a string class form <see cref="TryReadString"/> reads.</summary>
    internal static bool IsStringForm(uint form) => (DwForm)form is DwForm.String or DwForm.Strp or DwForm.LineStrp
        or DwForm.Strx or DwForm.Strx1 or DwForm.Strx2 or DwForm.Strx3 or DwForm.Strx4 or DwForm.GnuStrIndex;

    /// <summary>
    /// Extract a long value from block data. Handles DW_OP_plus_uconst for member locations.
    /// Used by DwarfTypeReader for DW_AT_data_member_location.
    /// </summary>
    internal static long ExtractBlockLongValue(ReadOnlySpan<byte> blockData)
    {
        if (blockData.Length >= 2 && blockData[0] == 0x23) // DW_OP_plus_uconst
        {
            int tmpPos = 1;
            return (long)ReadULEB128(blockData, ref tmpPos);
        }
        return 0L;
    }

    /// <summary>
    /// Get a long value from an attribute that may be a long or byte[] (block/exprloc).
    /// </summary>
    internal static long GetLongValue(object value)
    {
        if (value is long l) return l;
        if (value is byte[] block) return ExtractBlockLongValue(block);
        return 0L;
    }

    /// <summary>
    /// A <c>DW_FORM_addrx*</c> value: the address at <paramref name="index"/> in the unit's
    /// .debug_addr contribution when the context resolves indexed addresses; null when it cannot
    /// (no .debug_addr, no <c>DW_AT_addr_base</c>, index out of range); the raw index for legacy
    /// contexts.
    /// </summary>
    private static object? IndexedAddress(long index, in StringContext ctx, byte addrSize)
    {
        if (!ctx.ResolveAddrx)
            return index;
        if (ctx.AddrData.IsEmpty || ctx.AddrBase < 0 || (addrSize != 4 && addrSize != 8) || index < 0)
            return null;
        if (index > (ctx.AddrData.Length - ctx.AddrBase) / addrSize)
            return null;
        long at = ctx.AddrBase + index * addrSize;
        if (at + addrSize > ctx.AddrData.Length)
            return null;
        return addrSize == 8
            ? (long)ReadU64LE(ctx.AddrData, (int)at)
            : (long)ReadU32LE(ctx.AddrData, (int)at);
    }

    /// <summary>
    /// The string at <paramref name="index"/> in the unit's .debug_str_offsets contribution, whose entries
    /// are <paramref name="offsetSize"/> bytes (8 in 64-bit DWARF).
    /// </summary>
    private static bool TryResolveStrx(ulong index, in StringContext ctx, byte offsetSize, out ReadOnlySpan<byte> value)
    {
        value = default;
        long offsets = ctx.StrOffsetsData.Length;
        if (ctx.StrData.IsEmpty || ctx.StrOffsetsBase < 0 || ctx.StrOffsetsBase > offsets
            || index > (ulong)((offsets - ctx.StrOffsetsBase) / offsetSize))
            return false;
        int offsetPos = (int)(ctx.StrOffsetsBase + (long)index * offsetSize);
        if (offsetPos + offsetSize > offsets)
            return false;
        ulong strOffset = offsetSize == 8 ? ReadU64LE(ctx.StrOffsetsData, offsetPos) : ReadU32LE(ctx.StrOffsetsData, offsetPos);
        return TrySliceCString(ctx.StrData, strOffset, out value);
    }

    // ── Type resolution ─────────────────────────────────────────────────────────

    internal static string ResolveTypeName(DwarfDIE die, Dictionary<int, DwarfDIE> byOffset)
    {
        if (!die.Attributes.TryGetValue(DwAt.Type, out var typeRef) || typeRef is not long offset)
            return "(unknown)";

        int maxDepth = 20;
        var current = (int)offset;
        var suffix = "";

        for (int d = 0; d < maxDepth; d++)
        {
            if (!byOffset.TryGetValue(current, out var typeDie))
                return "(unknown)" + suffix;

            if (typeDie.Tag is DwTag.BaseType or DwTag.StructureType or DwTag.ClassType
                or DwTag.UnionType or DwTag.EnumerationType or DwTag.Typedef
                or DwTag.SubroutineType)
            {
                if (typeDie.Attributes.TryGetValue(DwAt.Name, out var n))
                    return n?.ToString() + suffix ?? "(unknown)" + suffix;
                if (typeDie.Tag == DwTag.SubroutineType)
                    return "(func)" + suffix;
                return "(unnamed)" + suffix;
            }

            if (typeDie.Tag == DwTag.PointerType)
            {
                suffix = "*" + suffix;
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DwTag.ReferenceType)
            {
                suffix = "&" + suffix;
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DwTag.ConstType)
            {
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "const void" + suffix;
                var innerName = ResolveTypeNameFromOffset((int)nextOff, byOffset, maxDepth - d - 1);
                return "const " + innerName + suffix;
            }

            if (typeDie.Tag == DwTag.VolatileType)
            {
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "volatile void" + suffix;
                var innerName = ResolveTypeNameFromOffset((int)nextOff, byOffset, maxDepth - d - 1);
                return "volatile " + innerName + suffix;
            }

            if (typeDie.Tag == DwTag.RestrictType)
            {
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "restrict void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DwTag.ArrayType)
            {
                suffix += "[]";
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return "(unknown)" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Attributes.TryGetValue(DwAt.Name, out var fallbackName))
                return fallbackName?.ToString() + suffix ?? "(unknown)" + suffix;

            return "(unknown)" + suffix;
        }

        return "(unknown)";
    }

    internal static string ResolveTypeNameFromOffset(int offset, Dictionary<int, DwarfDIE> byOffset, int maxDepth)
    {
        if (maxDepth <= 0 || !byOffset.TryGetValue(offset, out _))
            return "(unknown)";

        var tmp = new DwarfDIE
        {
            Offset = -1,
            Tag = 0,
            Attributes = new() { [DwAt.Type] = (long)offset }
        };
        return ResolveTypeName(tmp, byOffset);
    }

    internal static int ResolveTypeSize(DwarfDIE die, Dictionary<int, DwarfDIE> byOffset)
    {
        if (!die.Attributes.TryGetValue(DwAt.Type, out var typeRef) || typeRef is not long offset)
            return 0;

        int maxDepth = 20;
        var current = (int)offset;

        for (int d = 0; d < maxDepth; d++)
        {
            if (!byOffset.TryGetValue(current, out var typeDie))
                return 0;

            if (typeDie.Attributes.TryGetValue(DwAt.ByteSize, out var sizeObj) && sizeObj is long sizeVal)
                return (int)sizeVal;

            if (typeDie.Tag is DwTag.PointerType or DwTag.ReferenceType)
                return 4;

            if (typeDie.Tag is DwTag.ConstType or DwTag.VolatileType
                or DwTag.RestrictType or DwTag.Typedef)
            {
                if (!typeDie.Attributes.TryGetValue(DwAt.Type, out var nextRef) || nextRef is not long nextOff)
                    return 0;
                current = (int)nextOff;
                continue;
            }

            return 0;
        }

        return 0;
    }

    // ── Forms ───────────────────────────────────────────────────────────────────

    /// <summary>A form whose width depends on its encoded value: LEB128, string, block or indirect.</summary>
    private const int VariableWidth = -1;

    /// <summary>A form this reader does not know, so it cannot be sized.</summary>
    private const int UnknownWidth = -2;

    /// <summary>
    /// The width of a fixed-size form in a unit with <paramref name="format"/>: the one table of form
    /// widths. <see cref="VariableWidth"/> for LEB128, string, block and indirect forms;
    /// <see cref="UnknownWidth"/> for a form this reader does not know.
    /// </summary>
    internal static int FixedFormSize(uint form, in DwarfFormContext format) => (DwForm)form switch
    {
        DwForm.Addr => format.AddressSize,
        DwForm.Data1 or DwForm.Ref1 or DwForm.Flag or DwForm.Strx1 or DwForm.Addrx1 => 1,
        DwForm.Data2 or DwForm.Ref2 or DwForm.Strx2 or DwForm.Addrx2 => 2,
        DwForm.Strx3 or DwForm.Addrx3 => 3,
        DwForm.Data4 or DwForm.Ref4 or DwForm.RefSup4 or DwForm.Strx4 or DwForm.Addrx4 => 4,
        DwForm.Data8 or DwForm.Ref8 or DwForm.RefSig8 or DwForm.RefSup8 => 8,
        DwForm.Data16 => 16,
        DwForm.Strp or DwForm.LineStrp or DwForm.SecOffset or DwForm.StrpSup
            or DwForm.GnuRefAlt or DwForm.GnuStrpAlt => format.OffsetSize,
        DwForm.RefAddr => format.RefAddrSize,
        DwForm.FlagPresent or DwForm.ImplicitConst => 0,
        DwForm.Udata or DwForm.Sdata or DwForm.RefUdata or DwForm.Strx or DwForm.Addrx or DwForm.Loclistx
            or DwForm.Rnglistx or DwForm.GnuAddrIndex or DwForm.GnuStrIndex or DwForm.String or DwForm.Block
            or DwForm.Block1 or DwForm.Block2 or DwForm.Block4 or DwForm.Exprloc or DwForm.Indirect
            or DwForm.LlvmAddrxOffset => VariableWidth,
        _ => UnknownWidth,
    };

    /// <summary>
    /// Reads one attribute of <paramref name="form"/> as an unsigned number and moves past it: constants,
    /// references, section offsets and addresses are their value (<c>sdata</c> its two's complement);
    /// <c>strx</c>, <c>addrx</c>, <c>loclistx</c> and <c>rnglistx</c> are the index; <c>flag_present</c> is 1.
    /// Strings, blocks and 16-byte constants are skipped and read as 0.
    /// </summary>
    /// <exception cref="DwarfFormatException">The form is unknown, so it cannot be sized, or a block runs past the data.</exception>
    internal static ulong ReadFormRaw(ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format)
    {
        int size = FixedFormSize(form, format);
        if (size >= 0)
        {
            ulong value = size switch
            {
                0 => form == (uint)DwForm.FlagPresent ? 1UL : 0UL,
                1 => data[pos],
                2 => ReadU16LE(data, pos),
                4 => ReadU32LE(data, pos),
                8 => ReadU64LE(data, pos),
                16 => 0,
                _ => ReadUnsignedLE(data, pos, size),
            };
            pos += size;
            return value;
        }

        switch ((DwForm)form)
        {
            case DwForm.Sdata:
                return (ulong)ReadSLEB128(data, ref pos);
            case DwForm.Udata:
            case DwForm.RefUdata:
            case DwForm.Strx:
            case DwForm.Addrx:
            case DwForm.Loclistx:
            case DwForm.Rnglistx:
            case DwForm.GnuAddrIndex:
            case DwForm.GnuStrIndex:
                return ReadULEB128U64(data, ref pos);
            case DwForm.LlvmAddrxOffset:
            {
                // The index; the 4-byte offset that follows is skipped.
                ulong index = ReadULEB128U64(data, ref pos);
                pos += 4;
                return index;
            }
            case DwForm.Indirect:
                return ReadFormRaw(data, ref pos, ReadIndirectForm(data, ref pos), format);
            default:
                SkipFormValue(data, ref pos, form, format);
                return 0;
        }
    }

    /// <summary>Moves past one attribute of <paramref name="form"/>.</summary>
    /// <exception cref="DwarfFormatException">The form is unknown, so it cannot be sized, or a block runs past the data.</exception>
    internal static void SkipFormValue(ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format)
    {
        int size = FixedFormSize(form, format);
        if (size >= 0)
        {
            pos += size;
            return;
        }

        switch ((DwForm)form)
        {
            case DwForm.Sdata:
                ReadSLEB128(data, ref pos);
                break;
            case DwForm.String:
            {
                int length = data[pos..].IndexOf((byte)0);
                pos = length < 0 ? data.Length : pos + length + 1;
                break;
            }
            case DwForm.Block1:
            case DwForm.Block2:
            case DwForm.Block4:
            case DwForm.Block:
            case DwForm.Exprloc:
            {
                // Read the length first: it moves the cursor past the length field.
                int length = ReadBlockLength(data, ref pos, form);
                pos += length;
                break;
            }
            case DwForm.LlvmAddrxOffset:
                ReadULEB128U64(data, ref pos);
                pos += 4;
                break;
            case DwForm.Indirect:
                SkipFormValue(data, ref pos, ReadIndirectForm(data, ref pos), format);
                break;
            case DwForm.Udata:
            case DwForm.RefUdata:
            case DwForm.Strx:
            case DwForm.Addrx:
            case DwForm.Loclistx:
            case DwForm.Rnglistx:
            case DwForm.GnuAddrIndex:
            case DwForm.GnuStrIndex:
                ReadULEB128U64(data, ref pos);
                break;
            default:
                // An unknown form cannot be sized; skipping it as zero bytes would read the rest of the
                // unit out of step.
                throw new DwarfFormatException(InfoSection, pos, $"form 0x{form:X} at 0x{pos:X} is not a DWARF form this reader can size");
        }
    }

    /// <summary>
    /// A constant (<c>data1/2/4/8</c>, <c>udata</c>) or <c>sec_offset</c> attribute; any other form is
    /// skipped and reads as 0.
    /// </summary>
    internal static ulong ReadUnsignedFormValue(ReadOnlySpan<byte> data, ref int pos, uint form, in DwarfFormContext format)
    {
        if ((DwForm)form is DwForm.Data1 or DwForm.Data2 or DwForm.Data4 or DwForm.Data8 or DwForm.Udata or DwForm.SecOffset)
            return ReadFormRaw(data, ref pos, form, format);
        SkipFormValue(data, ref pos, form, format);
        return 0;
    }

    /// <summary>Reads a block form's length, leaves the cursor at its content and checks the content fits.</summary>
    private static int ReadBlockLength(ReadOnlySpan<byte> data, ref int pos, uint form)
    {
        int fieldSize = (DwForm)form switch { DwForm.Block1 => 1, DwForm.Block2 => 2, DwForm.Block4 => 4, _ => 0 };
        ulong length;
        if (fieldSize == 0)
        {
            length = ReadULEB128U64(data, ref pos);
        }
        else
        {
            length = ReadFixedLength(data, pos, fieldSize);
            pos += fieldSize;
        }
        return CheckedBlockLength(data, pos, length, InfoSection);
    }

    /// <summary>Wraps a runtime range exception from reading a unit's fields in the unit's <see cref="DwarfFormatException"/>.</summary>
    internal static DwarfFormatException UnitOverrun(string section, int unitStart, Exception inner) =>
        new(section, unitStart, $"a field of the unit at 0x{unitStart:X} runs past the end of the unit", inner);

    /// <summary>A runtime range exception: a fixed-size field read past the end of its unit.</summary>
    internal static bool IsRangeError(Exception ex) => ex is IndexOutOfRangeException or ArgumentOutOfRangeException;

    // ── Binary helpers ──────────────────────────────────────────────────────────

    internal static uint ReadU32LE(ReadOnlySpan<byte> data, int pos) =>
        (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));

    internal static ushort ReadU16LE(ReadOnlySpan<byte> data, int pos) =>
        (ushort)(data[pos] | (data[pos + 1] << 8));

    internal static ulong ReadU64LE(ReadOnlySpan<byte> data, int pos) =>
        (ulong)ReadU32LE(data, pos) | ((ulong)ReadU32LE(data, pos + 4) << 32);

    /// <summary>A little-endian unsigned value of 1–8 bytes (a 3-byte <c>strx3</c>, an unusual address size).</summary>
    private static ulong ReadUnsignedLE(ReadOnlySpan<byte> data, int pos, int size)
    {
        if (size > 8)
            throw new DwarfFormatException(InfoSection, pos, $"a {size}-byte value at 0x{pos:X} is wider than 8 bytes");
        ulong value = 0;
        for (int i = size - 1; i >= 0; i--)
            value = (value << 8) | data[pos + i];
        return value;
    }

    // DWARF LEB128s have no length limit. These read with WasmLeb128's unbounded decoders: every
    // continuation byte is consumed (a ten-byte DW_FORM_udata of 2^64-1 must not leave the cursor
    // mid-number, or every DIE after it decodes as garbage) and bits beyond the result width are
    // dropped. Truncated input moves the cursor to the end and reads as 0.

    /// <summary>An unsigned LEB128 truncated to 32 bits.</summary>
    internal static uint ReadULEB128(ReadOnlySpan<byte> data, ref int pos) => (uint)ReadULEB128U64(data, ref pos);

    /// <summary>A 64-bit unsigned LEB128 (bits beyond 64 are dropped; every continuation byte is consumed).</summary>
    internal static ulong ReadULEB128U64(ReadOnlySpan<byte> data, ref int pos)
    {
        if (!WasmLeb128.TryReadUnboundedU64(data, ref pos, out ulong value))
            pos = Math.Max(pos, data.Length);
        return value;
    }

    /// <summary>A 64-bit signed LEB128 (bits beyond 64 are dropped; every continuation byte is consumed).</summary>
    internal static long ReadSLEB128(ReadOnlySpan<byte> data, ref int pos)
    {
        if (!WasmLeb128.TryReadUnboundedS64(data, ref pos, out long value))
            pos = Math.Max(pos, data.Length);
        return value;
    }

    /// <summary>
    /// An unsigned LEB128 that must be complete: a truncated value is malformed DWARF
    /// (<see cref="DwarfFormatException"/>), never a silent 0 at the end of the data.
    /// </summary>
    internal static ulong ReadULEB128Checked(ReadOnlySpan<byte> data, ref int pos, string section, string what)
    {
        int start = pos;
        if (!WasmLeb128.TryReadUnboundedU64(data, ref pos, out ulong value))
            throw new DwarfFormatException(section, start, $"the {what} LEB128 at 0x{start:X} is truncated");
        return value;
    }

    /// <summary>A signed LEB128 that must be complete; see <see cref="ReadULEB128Checked"/>.</summary>
    internal static long ReadSLEB128Checked(ReadOnlySpan<byte> data, ref int pos, string section, string what)
    {
        int start = pos;
        if (!WasmLeb128.TryReadUnboundedS64(data, ref pos, out long value))
            throw new DwarfFormatException(section, start, $"the {what} LEB128 at 0x{start:X} is truncated");
        return value;
    }

    /// <summary>
    /// Reads a unit's initial length (32-bit, or 64-bit after the <c>0xffffffff</c> escape) at
    /// <paramref name="pos"/>, leaves <paramref name="pos"/> after it, and returns the offset just
    /// past the unit. The end is computed in 64 bits and checked against the data, so it is always
    /// after the length field and never past <paramref name="data"/>: a loop that resumes at it
    /// always advances. Reserved lengths (<c>0xfffffff0</c>–<c>0xfffffffe</c>) and a unit that
    /// runs past the data throw <see cref="DwarfFormatException"/>. Such an error leaves no next
    /// unit to resume at, so it ends a parse; an error inside a unit does not.
    /// </summary>
    internal static int ReadUnitLength(ReadOnlySpan<byte> data, ref int pos, string section, out bool is64Bit)
    {
        int unitStart = pos;
        if (pos < 0 || data.Length - pos < 4)
            throw new DwarfFormatException(section, unitStart, $"the unit at 0x{unitStart:X} is truncated: its 4-byte unit_length does not fit in the {data.Length - unitStart} remaining bytes");
        uint length32 = ReadU32LE(data, pos);
        pos += 4;
        ulong length;
        if (length32 == DwarfUnitLength.Dwarf64Escape)
        {
            if (data.Length - pos < 8)
                throw new DwarfFormatException(section, unitStart, $"the 64-bit DWARF unit at 0x{unitStart:X} is truncated: its 8-byte unit_length does not fit");
            length = ReadU64LE(data, pos);
            pos += 8;
            is64Bit = true;
        }
        else if (length32 >= DwarfUnitLength.ReservedMin)
        {
            throw new DwarfFormatException(section, unitStart, $"the unit at 0x{unitStart:X} has the reserved unit_length 0x{length32:X8}");
        }
        else
        {
            length = length32;
            is64Bit = false;
        }

        if (length > (ulong)(data.Length - pos))
            throw new DwarfFormatException(section, unitStart, $"the unit at 0x{unitStart:X} has unit_length {length}, but only {data.Length - pos} bytes follow it");
        return pos + (int)length;
    }

    /// <summary>
    /// Checks that a block of <paramref name="length"/> bytes starting at <paramref name="pos"/>
    /// lies within <paramref name="data"/> and returns the length. A block length is attacker-chosen
    /// and must never move a cursor backwards or past the data.
    /// </summary>
    internal static int CheckedBlockLength(ReadOnlySpan<byte> data, int pos, ulong length, string section)
    {
        if (pos > data.Length || length > (ulong)(data.Length - pos))
            throw new DwarfFormatException(section, pos, $"a {length}-byte block at 0x{pos:X} runs past the end of its data ({Math.Max(0, data.Length - pos)} bytes remain)");
        return (int)length;
    }

    /// <summary>The section every DIE attribute is read from (<see cref="ReadAttributeValue"/>, <see cref="SkipFormValue"/>).</summary>
    private const string InfoSection = ".debug_info";

    /// <summary>Reads a 1-, 2- or 4-byte block length that must lie within the data.</summary>
    private static ulong ReadFixedLength(ReadOnlySpan<byte> data, int pos, int size)
    {
        if (pos < 0 || pos > data.Length - size)
            throw new DwarfFormatException(InfoSection, pos, $"the {size}-byte block length at 0x{pos:X} runs past the end of its data");
        return size switch
        {
            1 => data[pos],
            2 => ReadU16LE(data, pos),
            _ => ReadU32LE(data, pos),
        };
    }

    /// <summary>
    /// Reads the form a <c>DW_FORM_indirect</c> names, looking through chained indirections
    /// iteratively so a run of them cannot recurse once per byte.
    /// </summary>
    private static uint ReadIndirectForm(ReadOnlySpan<byte> data, ref int pos)
    {
        uint form;
        do
        {
            form = ReadULEB128(data, ref pos);
        }
        while (form == (uint)DwForm.Indirect && pos < data.Length);
        return form == (uint)DwForm.Indirect ? 0 : form;
    }

    /// <summary>
    /// The NUL-terminated string at <paramref name="offset"/> in <paramref name="section"/> (.debug_str,
    /// .debug_line_str), without its terminator; an unterminated string runs to the end of the section.
    /// False when the offset is past the section.
    /// </summary>
    internal static bool TrySliceCString(ReadOnlySpan<byte> section, ulong offset, out ReadOnlySpan<byte> value)
    {
        if (offset >= (ulong)section.Length)
        {
            value = default;
            return false;
        }
        value = section[(int)offset..];
        int length = value.IndexOf((byte)0);
        if (length >= 0)
            value = value[..length];
        return true;
    }

    internal static string ReadNullTerminatedAdvance(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        int length = data[start..].IndexOf((byte)0);
        pos = length < 0 ? data.Length : start + length + 1;
        return Encoding.UTF8.GetString(data[start..(length < 0 ? data.Length : start + length)]);
    }
}
