// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;

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
        ReadOnlySpan<byte> data, int diePos, int cuEnd, ushort version,
        Dictionary<uint, DwarfAbbrevEntry> abbrevTable, byte addrSize)
    {
        int strOffsetsBase = 0;
        long addrBase = -1;
        if (version < 5 || diePos >= cuEnd)
            return (strOffsetsBase, addrBase);
        int peekPos = diePos;
        uint code = ReadULEB128(data, ref peekPos);
        if (code == 0 || !abbrevTable.TryGetValue(code, out var entry) || entry.Tag != DwTag.CompileUnit)
            return (strOffsetsBase, addrBase);
        foreach (var (attrName, attrForm, _) in entry.Attributes)
        {
            if (attrName == DwAt.StrOffsetsBase)
                strOffsetsBase = (int)ReadUnsignedFormValue(data, ref peekPos, attrForm, addrSize);
            else if (attrName == DwAt.AddrBase)
                addrBase = ReadUnsignedFormValue(data, ref peekPos, attrForm, addrSize);
            else
                SkipFormValue(data, ref peekPos, attrForm, addrSize);
        }
        return (strOffsetsBase, addrBase);
    }

    internal static List<DwarfDIE> ParseDIEs(
        ReadOnlySpan<byte> data, int start, int end,
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
                uint effectiveForm = attrForm;
                for (int peek = pos; effectiveForm == (uint)DwForm.Indirect && peek < data.Length;)
                    effectiveForm = ReadULEB128(data, ref peek);
                var value = ReadAttributeValue(data, ref pos, attrForm, addrSize, ctx, implicitConst);
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

    internal static object? ReadAttributeValue(
        ReadOnlySpan<byte> data, ref int pos, uint form, byte addrSize,
        in StringContext ctx, long implicitConst)
    {
        switch ((DwForm)form)
        {
            case DwForm.Addr:
                var addr = addrSize == 8
                    ? (long)ReadU64LE(data, pos)
                    : (long)ReadU32LE(data, pos);
                pos += addrSize;
                return addr;

            case DwForm.Data1:
            case DwForm.Ref1:
            case DwForm.Flag:
                return (long)data[pos++];
            case DwForm.Addrx1:
                return IndexedAddress(data[pos++], ctx, addrSize);

            case DwForm.Data2:
            case DwForm.Ref2:
            {
                var v = (long)ReadU16LE(data, pos); pos += 2;
                return v;
            }
            case DwForm.Addrx2:
            {
                var v = (long)ReadU16LE(data, pos); pos += 2;
                return IndexedAddress(v, ctx, addrSize);
            }
            case DwForm.Addrx3:
            {
                var v = (long)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16));
                pos += 3;
                return IndexedAddress(v, ctx, addrSize);
            }

            case DwForm.Data4:
            case DwForm.Ref4:
            case DwForm.RefAddr: // 32-bit DWARF
            case DwForm.SecOffset:
            case DwForm.RefSup4:
            case DwForm.StrpSup:
            {
                var v = (long)ReadU32LE(data, pos); pos += 4;
                return v;
            }
            case DwForm.Addrx4:
            {
                var v = (long)ReadU32LE(data, pos); pos += 4;
                return IndexedAddress(v, ctx, addrSize);
            }

            case DwForm.Strp:
            {
                var offset = (int)ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(ctx.StrData, offset) ?? (object)(long)offset;
            }
            case DwForm.LineStrp:
            {
                var offset = (int)ReadU32LE(data, pos); pos += 4;
                return ReadStringFromSection(ctx.LineStrData, offset) ?? (object)(long)offset;
            }

            // DWARF 5 indexed strings
            case DwForm.Strx:
                return ResolveStrx((int)ReadULEB128(data, ref pos), ctx);
            case DwForm.Strx1:
                return ResolveStrx(data[pos++], ctx);
            case DwForm.Strx2:
            {
                int idx = ReadU16LE(data, pos); pos += 2;
                return ResolveStrx(idx, ctx);
            }
            case DwForm.Strx3:
            {
                int idx = data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16);
                pos += 3;
                return ResolveStrx(idx, ctx);
            }
            case DwForm.Strx4:
            {
                int idx = (int)ReadU32LE(data, pos); pos += 4;
                return ResolveStrx(idx, ctx);
            }

            case DwForm.Data8:
            case DwForm.Ref8:
            case DwForm.RefSig8:
            case DwForm.RefSup8:
            {
                var v = (long)ReadU64LE(data, pos); pos += 8;
                return v;
            }

            case DwForm.Data16:
                pos += 16;
                return (long)0;

            case DwForm.Sdata:
                return ReadSLEB128(data, ref pos);

            case DwForm.Addrx:
                return IndexedAddress(ReadULEB128(data, ref pos), ctx, addrSize);

            case DwForm.Udata:
            case DwForm.RefUdata:
            case DwForm.Loclistx:
            case DwForm.Rnglistx:
                return (long)ReadULEB128(data, ref pos);

            case DwForm.String:
                return ReadNullTerminatedAdvance(data, ref pos);

            case DwForm.FlagPresent:
                return (long)1;

            case DwForm.ImplicitConst:
                return implicitConst;

            // Block/exprloc forms — return raw bytes for location expression parsing
            case DwForm.Block1:
            {
                int len = data[pos++];
                var block = data.Slice(pos, len).ToArray();
                pos += len;
                return block;
            }
            case DwForm.Block2:
            {
                int len = ReadU16LE(data, pos); pos += 2;
                var block = data.Slice(pos, len).ToArray();
                pos += len;
                return block;
            }
            case DwForm.Block4:
            {
                int len = (int)ReadU32LE(data, pos); pos += 4;
                var block = data.Slice(pos, len).ToArray();
                pos += len;
                return block;
            }
            case DwForm.Block:
            case DwForm.Exprloc:
            {
                int len = (int)ReadULEB128(data, ref pos);
                var block = data.Slice(pos, len).ToArray();
                pos += len;
                return block;
            }

            case DwForm.Indirect:
            {
                uint actualForm = ReadULEB128(data, ref pos);
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

    // ── Binary helpers ──────────────────────────────────────────────────────────

    internal static uint ReadUnsignedFormValue(ReadOnlySpan<byte> data, ref int pos, uint form, byte addrSize)
    {
        switch ((DwForm)form)
        {
            case DwForm.Data1: return data[pos++];
            case DwForm.Data2: { var v = ReadU16LE(data, pos); pos += 2; return v; }
            case DwForm.Data4: { var v = ReadU32LE(data, pos); pos += 4; return v; }
            case DwForm.Data8: { var v = ReadU32LE(data, pos); pos += 8; return v; }
            case DwForm.Udata: return ReadULEB128(data, ref pos);
            case DwForm.SecOffset: { var v = ReadU32LE(data, pos); pos += 4; return v; }
            default:
                SkipFormValue(data, ref pos, form, addrSize);
                return 0;
        }
    }

    internal static uint ReadU32LE(ReadOnlySpan<byte> data, int pos) =>
        (uint)(data[pos] | (data[pos + 1] << 8) | (data[pos + 2] << 16) | (data[pos + 3] << 24));

    internal static ushort ReadU16LE(ReadOnlySpan<byte> data, int pos) =>
        (ushort)(data[pos] | (data[pos + 1] << 8));

    internal static ulong ReadU64LE(ReadOnlySpan<byte> data, int pos) =>
        (ulong)ReadU32LE(data, pos) | ((ulong)ReadU32LE(data, pos + 4) << 32);

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

    internal static string? ReadStringFromSection(byte[]? section, int offset)
    {
        if (section is null || offset < 0 || offset >= section.Length) return null;
        int end = offset;
        while (end < section.Length && section[end] != 0) end++;
        var str = Encoding.UTF8.GetString(section, offset, end - offset);
        return str.Length > 0 ? str : null;
    }

    internal static string ReadNullTerminatedAdvance(ReadOnlySpan<byte> data, ref int pos)
    {
        int start = pos;
        while (pos < data.Length && data[pos] != 0) pos++;
        var result = Encoding.UTF8.GetString(data[start..pos]);
        if (pos < data.Length) pos++;
        return result;
    }

    internal static void SkipFormValue(ReadOnlySpan<byte> data, ref int pos, uint form, byte addrSize)
    {
        switch ((DwForm)form)
        {
            case DwForm.Addr: pos += addrSize; break;
            case DwForm.Block2: pos += 2 + ReadU16LE(data, pos); break;
            case DwForm.Block4: pos += 4 + (int)ReadU32LE(data, pos); break;
            case DwForm.Data2: pos += 2; break;
            case DwForm.Data4: pos += 4; break;
            case DwForm.Data8: pos += 8; break;
            case DwForm.String:
                while (pos < data.Length && data[pos] != 0) pos++;
                if (pos < data.Length) pos++;
                break;
            case DwForm.Block:
                { int blockLen = (int)ReadULEB128(data, ref pos); pos += blockLen; }
                break;
            case DwForm.Block1: pos += 1 + data[pos]; break;
            case DwForm.Data1: pos += 1; break;
            case DwForm.Flag: pos += 1; break;
            case DwForm.Sdata: ReadSLEB128(data, ref pos); break;
            case DwForm.Strp: pos += 4; break;
            case DwForm.Udata: ReadULEB128(data, ref pos); break;
            case DwForm.RefAddr: pos += 4; break;
            case DwForm.Ref1: pos += 1; break;
            case DwForm.Ref2: pos += 2; break;
            case DwForm.Ref4: pos += 4; break;
            case DwForm.Ref8: pos += 8; break;
            case DwForm.RefUdata: ReadULEB128(data, ref pos); break;
            case DwForm.Indirect:
                SkipFormValue(data, ref pos, ReadULEB128(data, ref pos), addrSize);
                break;
            case DwForm.SecOffset: pos += 4; break;
            case DwForm.Exprloc:
                { int exprLen = (int)ReadULEB128(data, ref pos); pos += exprLen; }
                break;
            case DwForm.FlagPresent: break;
            case DwForm.Strx: ReadULEB128(data, ref pos); break;
            case DwForm.Addrx: ReadULEB128(data, ref pos); break;
            case DwForm.RefSup4: pos += 4; break;
            case DwForm.StrpSup: pos += 4; break;
            case DwForm.Data16: pos += 16; break;
            case DwForm.LineStrp: pos += 4; break;
            case DwForm.RefSig8: pos += 8; break;
            case DwForm.ImplicitConst: break;
            case DwForm.Loclistx: ReadULEB128(data, ref pos); break;
            case DwForm.Rnglistx: ReadULEB128(data, ref pos); break;
            case DwForm.RefSup8: pos += 8; break;
            case DwForm.Strx1: pos += 1; break;
            case DwForm.Strx2: pos += 2; break;
            case DwForm.Strx3: pos += 3; break;
            case DwForm.Strx4: pos += 4; break;
            case DwForm.Addrx1: pos += 1; break;
            case DwForm.Addrx2: pos += 2; break;
            case DwForm.Addrx3: pos += 3; break;
            case DwForm.Addrx4: pos += 4; break;
        }
    }
}
