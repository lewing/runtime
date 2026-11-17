// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Text;

/// <summary>
/// Shared DWARF parsing infrastructure used by DwarfTypeReader and DwarfVariableReader.
/// Handles DIE parsing, attribute reading, string resolution, and type name resolution.
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
    internal readonly struct StringContext(byte[]? strData, byte[]? lineStrData, byte[]? strOffsetsData, int strOffsetsBase,
        byte[]? addrData = null, long addrBase = -1, bool resolveAddrx = false)
    {
        public readonly byte[]? StrData = strData;
        public readonly byte[]? LineStrData = lineStrData;
        public readonly byte[]? StrOffsetsData = strOffsetsData;
        public readonly int StrOffsetsBase = strOffsetsBase;
        public readonly byte[]? AddrData = addrData;
        public readonly long AddrBase = addrBase;
        public readonly bool ResolveAddrx = resolveAddrx;
    }

    /// <summary>
    /// Reads the per-unit bases a DWARF 5 compile unit DIE carries (<c>DW_AT_str_offsets_base</c>,
    /// <c>DW_AT_addr_base</c>) without materializing the DIE, so string and address forms in the
    /// unit can be resolved while it is parsed. Returns (0, -1) for DWARF &lt; 5 or when absent.
    /// </summary>
    internal static (int StrOffsetsBase, long AddrBase) ReadUnitBases(
        byte[] data, int diePos, int cuEnd, ushort version,
        Dictionary<uint, DwarfAbbrevEntry> abbrevTable, byte addrSize)
    {
        int strOffsetsBase = 0;
        long addrBase = -1;
        if (version < 5 || diePos >= cuEnd)
            return (strOffsetsBase, addrBase);
        int peekPos = diePos;
        uint code = ReadULEB128(data, ref peekPos);
        if (code == 0 || !abbrevTable.TryGetValue(code, out var entry) || entry.Tag != 0x11 /* DW_TAG_compile_unit */)
            return (strOffsetsBase, addrBase);
        foreach (var (attrName, attrForm, _) in entry.Attributes)
        {
            if (attrName == 0x72 /* DW_AT_str_offsets_base */)
                strOffsetsBase = (int)ReadUnsignedFormValue(data, ref peekPos, attrForm, addrSize);
            else if (attrName == 0x73 /* DW_AT_addr_base */)
                addrBase = ReadUnsignedFormValue(data, ref peekPos, attrForm, addrSize);
            else
                SkipFormValue(data, ref peekPos, attrForm, addrSize);
        }
        return (strOffsetsBase, addrBase);
    }

    // DWARF tag constants (shared)
    internal const ushort DW_TAG_array_type = 0x01;
    internal const ushort DW_TAG_class_type = 0x02;
    internal const ushort DW_TAG_enumeration_type = 0x04;
    internal const ushort DW_TAG_member = 0x0d;
    internal const ushort DW_TAG_pointer_type = 0x0f;
    internal const ushort DW_TAG_reference_type = 0x10;
    internal const ushort DW_TAG_structure_type = 0x13;
    internal const ushort DW_TAG_subroutine_type = 0x15;
    internal const ushort DW_TAG_typedef = 0x16;
    internal const ushort DW_TAG_union_type = 0x17;
    internal const ushort DW_TAG_base_type = 0x24;
    internal const ushort DW_TAG_const_type = 0x26;
    internal const ushort DW_TAG_volatile_type = 0x35;
    internal const ushort DW_TAG_restrict_type = 0x37;

    // DWARF attribute constants (shared)
    internal const ushort DW_AT_name = 0x03;
    internal const ushort DW_AT_byte_size = 0x0b;
    internal const ushort DW_AT_data_member_location = 0x38;
    internal const ushort DW_AT_type = 0x49;

    internal static List<DwarfDIE> ParseDIEs(
        byte[] data, int start, int end,
        Dictionary<uint, DwarfAbbrevEntry> abbrevTable,
        byte addrSize,
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
                    SkipFormValue(data, ref pos, attrForm, addrSize);
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
                ushort effectiveForm = attrForm;
                for (int peek = pos; effectiveForm == 0x16 /* DW_FORM_indirect */ && peek < data.Length;)
                    effectiveForm = (ushort)ReadULEB128(data, ref peek);
                var value = ReadAttributeValue(data, ref pos, attrForm, addrSize, ctx, implicitConst);
                if (value is not null)
                    die.Attributes[attrName] = value;
                if (effectiveForm == 0x10 /* DW_FORM_ref_addr */)
                    (die.SectionRefAttributes ??= []).Add(attrName);
            }

            dies.Add(die);

            if (entry.HasChildren)
                depth++;
        }

        return dies;
    }

    internal static object? ReadAttributeValue(
        byte[] data, ref int pos, ushort form, byte addrSize,
        in StringContext ctx, long implicitConst)
    {
        switch (form)
        {
            case 0x01: // DW_FORM_addr
                var addr = addrSize == 8
                    ? (long)ReadU64LE(data, pos)
                    : (long)ReadU32LE(data, pos);
                pos += addrSize;
                return addr;

            case 0x0b: // DW_FORM_data1
            case 0x11: // DW_FORM_ref1
            case 0x0c: // DW_FORM_flag
                return (long)data[pos++];
            case 0x29: // DW_FORM_addrx1
                return IndexedAddress(data[pos++], ctx, addrSize);

            case 0x05: // DW_FORM_data2
            case 0x12: // DW_FORM_ref2
            {
                var v = (long)ReadU16LE(data, pos); pos += 2;
                return v;
            }
            case 0x2a: // DW_FORM_addrx2
            {
                var v = (long)ReadU16LE(data, pos); pos += 2;
                return IndexedAddress(v, ctx, addrSize);
            }
            case 0x2b: // DW_FORM_addrx3
            {
                var v = (long)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
                pos += 3;
                return IndexedAddress(v, ctx, addrSize);
            }

            case 0x06: // DW_FORM_data4
            case 0x13: // DW_FORM_ref4
            case 0x10: // DW_FORM_ref_addr (32-bit DWARF)
            case 0x17: // DW_FORM_sec_offset
            case 0x1c: // DW_FORM_ref_sup4
            case 0x1d: // DW_FORM_strp_sup
            {
                var v = (long)ReadU32LE(data, pos); pos += 4;
                return v;
            }
            case 0x2c: // DW_FORM_addrx4
            {
                var v = (long)ReadU32LE(data, pos); pos += 4;
                return IndexedAddress(v, ctx, addrSize);
            }

            case 0x0e: // DW_FORM_strp
            {
                var offset = (int)ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(ctx.StrData, offset) ?? (object)(long)offset;
            }
            case 0x1f: // DW_FORM_line_strp
            {
                var offset = (int)ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(ctx.LineStrData, offset) ?? (object)(long)offset;
            }

            // DWARF 5 indexed strings
            case 0x1a: // DW_FORM_strx
                return ResolveStrx((int)ReadULEB128(data, ref pos), ctx);
            case 0x25: // DW_FORM_strx1
                return ResolveStrx(data[pos++], ctx);
            case 0x26: // DW_FORM_strx2
            {
                int idx = ReadU16LE(data, pos); pos += 2;
                return ResolveStrx(idx, ctx);
            }
            case 0x27: // DW_FORM_strx3
            {
                int idx = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                pos += 3;
                return ResolveStrx(idx, ctx);
            }
            case 0x28: // DW_FORM_strx4
            {
                int idx = (int)ReadU32LE(data, pos); pos += 4;
                return ResolveStrx(idx, ctx);
            }

            case 0x07: // DW_FORM_data8
            case 0x14: // DW_FORM_ref8
            case 0x20: // DW_FORM_ref_sig8
            case 0x24: // DW_FORM_ref_sup8
            {
                var v = (long)ReadU64LE(data, pos); pos += 8;
                return v;
            }

            case 0x1e: // DW_FORM_data16
                pos += 16;
                return (long)0;

            case 0x0d: // DW_FORM_sdata
                return ReadSLEB128(data, ref pos);

            case 0x1b: // DW_FORM_addrx
                return IndexedAddress(ReadULEB128(data, ref pos), ctx, addrSize);

            case 0x0f: // DW_FORM_udata
            case 0x15: // DW_FORM_ref_udata
            case 0x22: // DW_FORM_loclistx
            case 0x23: // DW_FORM_rnglistx
                return (long)ReadULEB128(data, ref pos);

            case 0x08: // DW_FORM_string
                return ReadNullTerminatedAdvance(data, ref pos);

            case 0x19: // DW_FORM_flag_present
                return (long)1;

            case 0x21: // DW_FORM_implicit_const
                return implicitConst;

            // Block/exprloc forms — return raw bytes for location expression parsing
            case 0x0a: // DW_FORM_block1
            {
                int len = data[pos++];
                var block = new byte[len];
                Array.Copy(data, pos, block, 0, len);
                pos += len;
                return block;
            }
            case 0x03: // DW_FORM_block2
            {
                int len = ReadU16LE(data, pos); pos += 2;
                var block = new byte[len];
                Array.Copy(data, pos, block, 0, len);
                pos += len;
                return block;
            }
            case 0x04: // DW_FORM_block4
            {
                int len = (int)ReadU32LE(data, pos); pos += 4;
                var block = new byte[len];
                Array.Copy(data, pos, block, 0, len);
                pos += len;
                return block;
            }
            case 0x09: // DW_FORM_block
            case 0x18: // DW_FORM_exprloc
            {
                int len = (int)ReadULEB128(data, ref pos);
                var block = new byte[len];
                Array.Copy(data, pos, block, 0, len);
                pos += len;
                return block;
            }

            case 0x16: // DW_FORM_indirect
            {
                ushort actualForm = (ushort)ReadULEB128(data, ref pos);
                return ReadAttributeValue(data, ref pos, actualForm, addrSize, ctx, 0);
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Extract a long value from block data. Handles DW_OP_plus_uconst for member locations.
    /// Used by DwarfTypeReader for DW_AT_data_member_location.
    /// </summary>
    internal static long ExtractBlockLongValue(byte[] blockData)
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
        if (ctx.AddrData is null || ctx.AddrBase < 0 || (addrSize != 4 && addrSize != 8))
            return null;
        long at = ctx.AddrBase + index * addrSize;
        if (at < 0 || at + addrSize > ctx.AddrData.Length)
            return null;
        return addrSize == 8
            ? (long)ReadU64LE(ctx.AddrData, (int)at)
            : (long)ReadU32LE(ctx.AddrData, (int)at);
    }

    private static object ResolveStrx(int index, in StringContext ctx)
    {
        if (ctx.StrOffsetsData is not null && ctx.StrData is not null)
        {
            int offsetPos = ctx.StrOffsetsBase + index * 4;
            if (offsetPos >= 0 && offsetPos + 4 <= ctx.StrOffsetsData.Length)
            {
                int strOffset = (int)ReadU32LE(ctx.StrOffsetsData, offsetPos);
                var resolved = ReadStringFromSection(ctx.StrData, strOffset);
                if (resolved is not null) return resolved;
            }
        }
        return (long)index;
    }

    // ── Type resolution ─────────────────────────────────────────────────────────

    internal static string ResolveTypeName(DwarfDIE die, Dictionary<int, DwarfDIE> byOffset)
    {
        if (!die.Attributes.TryGetValue(DW_AT_type, out var typeRef) || typeRef is not long offset)
            return "(unknown)";

        int maxDepth = 20;
        var current = (int)offset;
        var suffix = "";

        for (int d = 0; d < maxDepth; d++)
        {
            if (!byOffset.TryGetValue(current, out var typeDie))
                return "(unknown)" + suffix;

            if (typeDie.Tag is DW_TAG_base_type or DW_TAG_structure_type or DW_TAG_class_type
                or DW_TAG_union_type or DW_TAG_enumeration_type or DW_TAG_typedef
                or DW_TAG_subroutine_type)
            {
                if (typeDie.Attributes.TryGetValue(DW_AT_name, out var n))
                    return n?.ToString() + suffix ?? "(unknown)" + suffix;
                if (typeDie.Tag == DW_TAG_subroutine_type)
                    return "(func)" + suffix;
                return "(unnamed)" + suffix;
            }

            if (typeDie.Tag == DW_TAG_pointer_type)
            {
                suffix = "*" + suffix;
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DW_TAG_reference_type)
            {
                suffix = "&" + suffix;
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DW_TAG_const_type)
            {
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "const void" + suffix;
                var innerName = ResolveTypeNameFromOffset((int)nextOff, byOffset, maxDepth - d - 1);
                return "const " + innerName + suffix;
            }

            if (typeDie.Tag == DW_TAG_volatile_type)
            {
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "volatile void" + suffix;
                var innerName = ResolveTypeNameFromOffset((int)nextOff, byOffset, maxDepth - d - 1);
                return "volatile " + innerName + suffix;
            }

            if (typeDie.Tag == DW_TAG_restrict_type)
            {
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "restrict void" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Tag == DW_TAG_array_type)
            {
                suffix += "[]";
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return "(unknown)" + suffix;
                current = (int)nextOff;
                continue;
            }

            if (typeDie.Attributes.TryGetValue(DW_AT_name, out var fallbackName))
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
            Attributes = new() { [DW_AT_type] = (long)offset }
        };
        return ResolveTypeName(tmp, byOffset);
    }

    internal static int ResolveTypeSize(DwarfDIE die, Dictionary<int, DwarfDIE> byOffset)
    {
        if (!die.Attributes.TryGetValue(DW_AT_type, out var typeRef) || typeRef is not long offset)
            return 0;

        int maxDepth = 20;
        var current = (int)offset;

        for (int d = 0; d < maxDepth; d++)
        {
            if (!byOffset.TryGetValue(current, out var typeDie))
                return 0;

            if (typeDie.Attributes.TryGetValue(DW_AT_byte_size, out var sizeObj) && sizeObj is long sizeVal)
                return (int)sizeVal;

            if (typeDie.Tag is DW_TAG_pointer_type or DW_TAG_reference_type)
                return 4;

            if (typeDie.Tag is DW_TAG_const_type or DW_TAG_volatile_type
                or DW_TAG_restrict_type or DW_TAG_typedef)
            {
                if (!typeDie.Attributes.TryGetValue(DW_AT_type, out var nextRef) || nextRef is not long nextOff)
                    return 0;
                current = (int)nextOff;
                continue;
            }

            return 0;
        }

        return 0;
    }

    // ── Binary helpers ──────────────────────────────────────────────────────────

    internal static uint ReadUnsignedFormValue(byte[] data, ref int pos, ushort form, byte addrSize)
    {
        switch (form)
        {
            case 0x0b: return data[pos++];
            case 0x05: { var v = ReadU16LE(data, pos); pos += 2; return v; }
            case 0x06: { var v = ReadU32LE(data, pos); pos += 4; return v; }
            case 0x07: { var v = ReadU32LE(data, pos); pos += 8; return v; }
            case 0x0f: return ReadULEB128(data, ref pos);
            case 0x17: { var v = ReadU32LE(data, pos); pos += 4; return v; }
            default:
                SkipFormValue(data, ref pos, form, addrSize);
                return 0;
        }
    }

    internal static uint ReadU32LE(byte[] data, int pos) =>
        (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));

    internal static ushort ReadU16LE(byte[] data, int pos) =>
        (ushort)(data[pos] | (data[pos + 1] << 8));

    internal static ulong ReadU64LE(byte[] data, int pos) =>
        (ulong)ReadU32LE(data, pos) | ((ulong)ReadU32LE(data, pos + 4) << 32);

    internal static uint ReadULEB128(byte[] data, ref int pos)
    {
        uint result = 0;
        int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            // Bits beyond 32 are dropped, but every continuation byte is still consumed: a 64-bit
            // value (e.g. a DW_FORM_udata constant of 2^64-1, ten bytes) must not leave the cursor
            // mid-number, or every DIE after it decodes as garbage.
            if (shift < 32)
                result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    internal static long ReadSLEB128(byte[] data, ref int pos)
    {
        long result = 0;
        int shift = 0;
        byte b;
        do
        {
            if (pos >= data.Length) break;
            b = data[pos++];
            result |= (long)(b & 0x7F) << shift;
            shift += 7;
        } while ((b & 0x80) != 0);

        if (shift < 64 && shift > 0 && pos > 0 && (data[pos - 1] & 0x40) != 0)
            result |= -(1L << shift);
        return result;
    }

    internal static string? ReadStringFromSection(byte[]? section, int offset)
    {
        if (section is null || offset < 0 || offset >= section.Length) return null;
        int end = offset;
        while (end < section.Length && section[end] != 0) end++;
        var str = Encoding.UTF8.GetString(section, offset, end - offset);
        return str.Length > 0 ? str : null;
    }

    internal static string ReadNullTerminatedAdvance(byte[] data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        var result = Encoding.UTF8.GetString(data, start, pos - start);
        if (pos < data.Length) pos++;
        return result;
    }

    internal static void SkipFormValue(byte[] data, ref int pos, ushort form, byte addrSize)
    {
        switch (form)
        {
            case 0x01: pos += addrSize; break;
            case 0x03: pos += 2 + ReadU16LE(data, pos); break;
            case 0x04: pos += 4 + (int)ReadU32LE(data, pos); break;
            case 0x05: pos += 2; break;
            case 0x06: pos += 4; break;
            case 0x07: pos += 8; break;
            case 0x08:
                while (pos < data.Length && data[pos] != 0) pos++;
                if (pos < data.Length) pos++;
                break;
            case 0x09:
                { int blockLen = (int)ReadULEB128(data, ref pos); pos += blockLen; }
                break;
            case 0x0a: pos += 1 + data[pos]; break;
            case 0x0b: pos += 1; break;
            case 0x0c: pos += 1; break;
            case 0x0d: ReadSLEB128(data, ref pos); break;
            case 0x0e: pos += 4; break;
            case 0x0f: ReadULEB128(data, ref pos); break;
            case 0x10: pos += 4; break;
            case 0x11: pos += 1; break;
            case 0x12: pos += 2; break;
            case 0x13: pos += 4; break;
            case 0x14: pos += 8; break;
            case 0x15: ReadULEB128(data, ref pos); break;
            case 0x16:
                SkipFormValue(data, ref pos, (ushort)ReadULEB128(data, ref pos), addrSize);
                break;
            case 0x17: pos += 4; break;
            case 0x18:
                { int exprLen = (int)ReadULEB128(data, ref pos); pos += exprLen; }
                break;
            case 0x19: break;
            case 0x1a: ReadULEB128(data, ref pos); break;
            case 0x1b: ReadULEB128(data, ref pos); break;
            case 0x1c: pos += 4; break;
            case 0x1d: pos += 4; break;
            case 0x1e: pos += 16; break;
            case 0x1f: pos += 4; break;
            case 0x20: pos += 8; break;
            case 0x21: break;
            case 0x22: ReadULEB128(data, ref pos); break;
            case 0x23: ReadULEB128(data, ref pos); break;
            case 0x24: pos += 8; break;
            case 0x25: pos += 1; break;
            case 0x26: pos += 2; break;
            case 0x27: pos += 3; break;
            case 0x28: pos += 4; break;
            case 0x29: pos += 1; break;
            case 0x2a: pos += 2; break;
            case 0x2b: pos += 3; break;
            case 0x2c: pos += 4; break;
        }
    }
}
