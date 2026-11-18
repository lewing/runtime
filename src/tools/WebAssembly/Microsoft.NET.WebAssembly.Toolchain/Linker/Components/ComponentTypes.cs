// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using System.Buffers;
using System.Text;
using System.Text.Unicode;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;

// A lossless model of the component-model type grammar (deftype, instance/component type decls), sufficient to
// read a `component-type` custom section, rewrite indices and write the result back out. Encodings follow
// the component model's Binary.md.

/// <summary>A component value type: a primitive (<see cref="Code"/> is its one-byte form, e.g. 0x73 = string) or a type index.</summary>
internal readonly record struct CtValType(bool IsPrimitive, uint Value)
{
    public static CtValType Prim(byte code) => new(true, code);
    public static CtValType Ref(uint index) => new(false, index);
    public byte Code => (byte)Value;
}

internal abstract record CtDefType;
internal sealed record CtPrimitive(byte Code) : CtDefType;
internal sealed record CtRecord(List<(string Name, CtValType Type)> Fields) : CtDefType;
internal sealed record CtVariant(List<(string Name, CtValType? Type)> Cases) : CtDefType;
internal sealed record CtList(CtValType Element, uint? FixedLength) : CtDefType;
internal sealed record CtTuple(List<CtValType> Elements) : CtDefType;
internal sealed record CtFlags(List<string> Names) : CtDefType;
internal sealed record CtEnum(List<string> Names) : CtDefType;
internal sealed record CtOption(CtValType Element) : CtDefType;
internal sealed record CtResult(CtValType? Ok, CtValType? Err) : CtDefType;
internal sealed record CtOwn(uint Resource) : CtDefType;
internal sealed record CtBorrow(uint Resource) : CtDefType;
internal sealed record CtStream(CtValType? Element) : CtDefType;
internal sealed record CtFuture(CtValType? Element) : CtDefType;
internal sealed record CtMap(CtValType Key, CtValType Value) : CtDefType;

/// <summary><c>functype</c>; <see cref="NamedResults"/> is the legacy <c>0x01 vec(labelvaltype)</c> result form.</summary>
internal sealed record CtFunc(bool Async, List<(string Name, CtValType Type)> Params, CtValType? Result,
    List<(string Name, CtValType Type)>? NamedResults) : CtDefType;

/// <summary><c>resourcetype</c> (only valid at component level); kept as its raw encoding after the opcode.</summary>
internal sealed record CtResource(byte Opcode, byte[] Raw) : CtDefType;
internal sealed record CtInstanceType(List<CtDecl> Decls) : CtDefType;
internal sealed record CtComponentType(List<CtDecl> Decls) : CtDefType;

internal abstract record CtDecl;
/// <summary>A <c>core:type</c> declaration, kept as raw bytes (it never references component indices).</summary>
internal sealed record CtCoreTypeDecl(byte[] Raw) : CtDecl;
internal sealed record CtTypeDecl(CtDefType Type) : CtDecl;
internal sealed record CtAliasDecl(CtSort Sort, CtAliasTarget Target) : CtDecl;
/// <summary>An import declaration; <see cref="RawName"/> keeps the encoded <c>nameattributes</c> for round-tripping.</summary>
internal sealed record CtImportDecl(string Name, CtExternDesc Desc, byte[]? RawName = null) : CtDecl;
internal sealed record CtExportDecl(string Name, CtExternDesc Desc, byte[]? RawName = null) : CtDecl;

/// <summary>A component sort; <see cref="CoreSort"/> is set (and <see cref="Kind"/> is <see cref="ComponentSort.Core"/>) for core sorts.</summary>
internal readonly record struct CtSort(ComponentSort Kind, CoreSort CoreSort = default)
{
    public static readonly CtSort TypeSort = new(ComponentSort.Type);
}

internal abstract record CtAliasTarget;
internal sealed record CtAliasExport(uint Instance, string Name) : CtAliasTarget;
internal sealed record CtAliasCoreExport(uint Instance, string Name) : CtAliasTarget;
internal sealed record CtAliasOuter(uint Count, uint Index) : CtAliasTarget;

/// <summary>
/// <c>externdesc</c>. For types, <see cref="SubResource"/> distinguishes <c>(sub resource)</c> from <c>(eq Index)</c>.
/// </summary>
internal sealed record CtExternDesc(ComponentExternKind Kind, uint Index, bool SubResource = false, byte[]? RawValueBound = null);

internal sealed class CtReader(byte[] bytes, int start, int end)
{
    private readonly byte[] _bytes = bytes;
    public int Position = start;
    public int End { get; } = end;
    public bool HasMore => Position < End;

    public CtReader(byte[] bytes) : this(bytes, 0, bytes.Length) { }

    public byte ReadByte() =>
        Position < End ? _bytes[Position++] : throw new FormatException("Unexpected end of component data.");

    public uint ReadU32()
    {
        Leb128Status status = WasmLeb128.DecodeU32(Remaining, out uint value, out int length);
        Position += status == Leb128Status.Ok ? length : throw LebException(status, "u32");
        return value;
    }

    public ulong ReadU64()
    {
        Leb128Status status = WasmLeb128.DecodeU64(Remaining, out ulong value, out int length);
        Position += status == Leb128Status.Ok ? length : throw LebException(status, "u64");
        return value;
    }

    public long ReadS33()
    {
        Leb128Status status = WasmLeb128.DecodeS33(Remaining, out long value, out int length);
        Position += status == Leb128Status.Ok ? length : throw LebException(status, "s33");
        return value;
    }

    /// <summary>Skips one LEB128 of any length without range checks.</summary>
    public void SkipLeb128()
    {
        if (!WasmLeb128.TrySkip(_bytes.AsSpan(0, End), ref Position))
            throw new FormatException("Unexpected end of component data.");
    }

    private ReadOnlySpan<byte> Remaining => _bytes.AsSpan(Position, End - Position);

    private static FormatException LebException(Leb128Status status, string type) => status switch
    {
        Leb128Status.Truncated => new FormatException("Unexpected end of component data."),
        Leb128Status.Overflow => new FormatException($"{type} LEB128 out of range."),
        _ => new FormatException($"{type} LEB128 too long."),
    };

    /// <summary>Reads a name and decodes it. Invalid UTF-8 is rejected, as the core and component readers do.</summary>
    public string ReadName() => Encoding.UTF8.GetString(ReadNameBytes());

    /// <summary>Reads a name as its validated UTF-8 bytes, for callers that only compare or copy it.</summary>
    public ReadOnlySpan<byte> ReadNameBytes() => ReadNameMemory().Span;

    /// <summary>Reads a name as its validated UTF-8 bytes, which stay valid as long as the underlying buffer.</summary>
    public ReadOnlyMemory<byte> ReadNameMemory()
    {
        uint length = ReadU32();
        if (length > (uint)(End - Position))
            throw new FormatException("Name extends past the end of the component data.");
        var name = new ReadOnlyMemory<byte>(_bytes, Position, (int)length);
        if (!Utf8.IsValid(name.Span))
            throw new FormatException(WasmBinaryReader.InvalidUtf8NameMessage);
        Position += (int)length;
        return name;
    }

    /// <summary>Skips a name. Its bytes are still validated; only the decode is skipped.</summary>
    public void SkipName() => ReadNameBytes();

    public byte[] Slice(int from) => _bytes[from..Position];

    /// <summary>The bytes from <paramref name="from"/> to the current position, without copying.</summary>
    public ReadOnlySpan<byte> SpanFrom(int from) => _bytes.AsSpan(from, Position - from);

    public CtReader Sub(int length)
    {
        if (length < 0 || Position + length > End)
            throw new FormatException("Section extends past the end of the component.");
        var sub = new CtReader(_bytes, Position, Position + length);
        Position += length;
        return sub;
    }
}

internal sealed class CtWriter
{
    private readonly MemoryStream _stream = new();

    public void Byte(byte value) => _stream.WriteByte(value);
    public void Bytes(ReadOnlySpan<byte> value) => _stream.Write(value);
    public int Length => (int)_stream.Length;
    public byte[] ToArray() => _stream.ToArray();

    public void U32(uint value) => WasmLeb128.WriteULeb128(_stream, value);

    public void S33(long value) => WasmLeb128.WriteSLeb128(_stream, value);

    public void Name(string name)
    {
        int length = Encoding.UTF8.GetByteCount(name);
        U32((uint)length);
        byte[]? rented = null;
        Span<byte> utf8 = length <= 256 ? stackalloc byte[length] : (rented = ArrayPool<byte>.Shared.Rent(length)).AsSpan(0, length);
        try
        {
            Encoding.UTF8.GetBytes(name, utf8);
            Bytes(utf8);
        }
        finally
        {
            if (rented is not null)
                ArrayPool<byte>.Shared.Return(rented);
        }
    }

    /// <summary>Writes a name that is already UTF-8: a <c>u8</c> literal or bytes copied from a <see cref="CtReader"/>.</summary>
    public void Name(ReadOnlySpan<byte> utf8)
    {
        U32((uint)utf8.Length);
        Bytes(utf8);
    }

    /// <summary>Writes a section: id, size, and the body produced by <paramref name="body"/>.</summary>
    public void Section(WasmSectionId id, Action<CtWriter> body) => Section((byte)id, body);

    public void Section(ComponentSectionId id, Action<CtWriter> body) => Section((byte)id, body);

    /// <summary>Writes a section with a raw id; production code uses the typed overloads, byte-exact fixtures this one.</summary>
    public void Section(byte id, Action<CtWriter> body)
    {
        var inner = new CtWriter();
        body(inner);
        Byte(id);
        U32((uint)inner.Length);
        Bytes(inner.ToArray());
    }
}

internal static class ComponentTypeCodec
{
    private static bool IsPrimitiveCode(byte code) => code is >= 0x73 and <= 0x7F or 0x64;

    public static CtValType ReadValType(CtReader r)
    {
        long code = r.ReadS33();
        if (code >= 0)
            return CtValType.Ref((uint)code);
        byte b = (byte)(code & 0x7F);
        if (!IsPrimitiveCode(b))
            throw new FormatException($"Unknown component value type code 0x{b:X2}.");
        return CtValType.Prim(b);
    }

    public static void WriteValType(CtWriter w, CtValType type)
    {
        if (type.IsPrimitive)
            w.Byte(type.Code);
        else
            w.S33(type.Value);
    }

    private static CtValType? ReadOptionalValType(CtReader r) => r.ReadByte() switch
    {
        0x00 => null,
        0x01 => ReadValType(r),
        var b => throw new FormatException($"Invalid optional value type flag 0x{b:X2}."),
    };

    private static void WriteOptionalValType(CtWriter w, CtValType? type)
    {
        if (type is { } t)
        {
            w.Byte(0x01);
            WriteValType(w, t);
        }
        else
        {
            w.Byte(0x00);
        }
    }

    private static List<(string, CtValType)> ReadLabeledList(CtReader r)
    {
        uint count = r.ReadU32();
        var list = new List<(string, CtValType)>((int)count);
        for (uint i = 0; i < count; i++)
            list.Add((r.ReadName(), ReadValType(r)));
        return list;
    }

    private static void WriteLabeledList(CtWriter w, List<(string Name, CtValType Type)> list)
    {
        w.U32((uint)list.Count);
        foreach (var (name, type) in list)
        {
            w.Name(name);
            WriteValType(w, type);
        }
    }

    private static List<string> ReadNames(CtReader r)
    {
        uint count = r.ReadU32();
        var names = new List<string>((int)count);
        for (uint i = 0; i < count; i++)
            names.Add(r.ReadName());
        return names;
    }

    private static void WriteNames(CtWriter w, List<string> names)
    {
        w.U32((uint)names.Count);
        foreach (string name in names)
            w.Name(name);
    }

    public static CtDefType ReadDefType(CtReader r)
    {
        byte op = r.ReadByte();
        switch ((ComponentTypeCode)op)
        {
            case ComponentTypeCode.Func:
            case ComponentTypeCode.AsyncFunc:
            {
                var parameters = ReadLabeledList(r);
                byte resultKind = r.ReadByte();
                return resultKind switch
                {
                    0x00 => new CtFunc(op == (byte)ComponentTypeCode.AsyncFunc, parameters, ReadValType(r), null),
                    0x01 => ReadNamedResults(r, op == (byte)ComponentTypeCode.AsyncFunc, parameters),
                    _ => throw new FormatException($"Unknown functype result kind 0x{resultKind:X2}."),
                };
            }
            case ComponentTypeCode.Component:
                return new CtComponentType(ReadDecls(r, allowImports: true));
            case ComponentTypeCode.Instance:
                return new CtInstanceType(ReadDecls(r, allowImports: false));
            case ComponentTypeCode.Resource:
            case ComponentTypeCode.AsyncResource:
            {
                int start = r.Position;
                ReadValType(r);
                if (op == (byte)ComponentTypeCode.AsyncResource)
                    r.ReadU32();
                if (r.ReadByte() == 0x01)
                    r.ReadU32();
                return new CtResource(op, r.Slice(start));
            }
            default:
                return ReadDefValType(r, op);
        }
    }

    private static CtFunc ReadNamedResults(CtReader r, bool isAsync, List<(string, CtValType)> parameters)
    {
        var named = ReadLabeledList(r);
        return new CtFunc(isAsync, parameters, null, named.Count == 0 ? null : named);
    }

    private static CtDefType ReadDefValType(CtReader r, byte op)
    {
        if (IsPrimitiveCode(op))
            return new CtPrimitive(op);
        switch ((ComponentTypeCode)op)
        {
            case ComponentTypeCode.Record: return new CtRecord(ReadLabeledList(r));
            case ComponentTypeCode.Variant:
            {
                uint count = r.ReadU32();
                var cases = new List<(string, CtValType?)>((int)count);
                for (uint i = 0; i < count; i++)
                {
                    string name = r.ReadName();
                    CtValType? payload = ReadOptionalValType(r);
                    if (r.ReadByte() != 0x00)
                        throw new FormatException("Variant case `refines` is not supported.");
                    cases.Add((name, payload));
                }

                return new CtVariant(cases);
            }
            case ComponentTypeCode.List: return new CtList(ReadValType(r), null);
            case ComponentTypeCode.FixedList:
            {
                CtValType element = ReadValType(r);
                return new CtList(element, r.ReadU32());
            }
            case ComponentTypeCode.Tuple:
            {
                uint count = r.ReadU32();
                var elements = new List<CtValType>((int)count);
                for (uint i = 0; i < count; i++)
                    elements.Add(ReadValType(r));
                return new CtTuple(elements);
            }
            case ComponentTypeCode.Flags: return new CtFlags(ReadNames(r));
            case ComponentTypeCode.Enum: return new CtEnum(ReadNames(r));
            case ComponentTypeCode.Option: return new CtOption(ReadValType(r));
            case ComponentTypeCode.Result:
            {
                CtValType? ok = ReadOptionalValType(r);
                return new CtResult(ok, ReadOptionalValType(r));
            }
            case ComponentTypeCode.Own: return new CtOwn(r.ReadU32());
            case ComponentTypeCode.Borrow: return new CtBorrow(r.ReadU32());
            case ComponentTypeCode.Stream: return new CtStream(ReadOptionalValType(r));
            case ComponentTypeCode.Future: return new CtFuture(ReadOptionalValType(r));
            case ComponentTypeCode.Map:
            {
                CtValType key = ReadValType(r);
                return new CtMap(key, ReadValType(r));
            }
            default:
                throw new FormatException($"Unknown component type opcode 0x{op:X2}.");
        }
    }

    public static void WriteDefType(CtWriter w, CtDefType type)
    {
        switch (type)
        {
            case CtPrimitive p: w.Byte(p.Code); break;
            case CtRecord rec: w.Byte((byte)ComponentTypeCode.Record); WriteLabeledList(w, rec.Fields); break;
            case CtVariant v:
                w.Byte((byte)ComponentTypeCode.Variant);
                w.U32((uint)v.Cases.Count);
                foreach (var (name, payload) in v.Cases)
                {
                    w.Name(name);
                    WriteOptionalValType(w, payload);
                    w.Byte(0x00);
                }

                break;
            case CtList { FixedLength: null } list: w.Byte(0x70); WriteValType(w, list.Element); break;
            case CtList list:
                w.Byte((byte)ComponentTypeCode.FixedList);
                WriteValType(w, list.Element);
                w.U32(list.FixedLength!.Value);
                break;
            case CtTuple t:
                w.Byte((byte)ComponentTypeCode.Tuple);
                w.U32((uint)t.Elements.Count);
                foreach (CtValType e in t.Elements)
                    WriteValType(w, e);
                break;
            case CtFlags f: w.Byte((byte)ComponentTypeCode.Flags); WriteNames(w, f.Names); break;
            case CtEnum e: w.Byte((byte)ComponentTypeCode.Enum); WriteNames(w, e.Names); break;
            case CtOption o: w.Byte((byte)ComponentTypeCode.Option); WriteValType(w, o.Element); break;
            case CtResult res: w.Byte((byte)ComponentTypeCode.Result); WriteOptionalValType(w, res.Ok); WriteOptionalValType(w, res.Err); break;
            case CtOwn own: w.Byte((byte)ComponentTypeCode.Own); w.U32(own.Resource); break;
            case CtBorrow borrow: w.Byte((byte)ComponentTypeCode.Borrow); w.U32(borrow.Resource); break;
            case CtStream s: w.Byte((byte)ComponentTypeCode.Stream); WriteOptionalValType(w, s.Element); break;
            case CtFuture f: w.Byte((byte)ComponentTypeCode.Future); WriteOptionalValType(w, f.Element); break;
            case CtMap m: w.Byte((byte)ComponentTypeCode.Map); WriteValType(w, m.Key); WriteValType(w, m.Value); break;
            case CtFunc fn:
                w.Byte((byte)(fn.Async ? ComponentTypeCode.AsyncFunc : ComponentTypeCode.Func));
                WriteLabeledList(w, fn.Params);
                if (fn.NamedResults is { } named)
                {
                    w.Byte(0x01);
                    WriteLabeledList(w, named);
                }
                else if (fn.Result is { } result)
                {
                    w.Byte(0x00);
                    WriteValType(w, result);
                }
                else
                {
                    w.Byte(0x01);
                    w.Byte(0x00);
                }

                break;
            case CtResource res: w.Byte(res.Opcode); w.Bytes(res.Raw); break;
            case CtInstanceType inst: w.Byte((byte)ComponentTypeCode.Instance); WriteDecls(w, inst.Decls); break;
            case CtComponentType comp: w.Byte((byte)ComponentTypeCode.Component); WriteDecls(w, comp.Decls); break;
            default: throw new InvalidOperationException($"Unhandled component type {type.GetType().Name}.");
        }
    }

    public static List<CtDecl> ReadDecls(CtReader r, bool allowImports)
    {
        uint count = r.ReadU32();
        var decls = new List<CtDecl>((int)count);
        for (uint i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            decls.Add((ComponentDeclKind)kind switch
            {
                ComponentDeclKind.CoreType => new CtCoreTypeDecl(ReadRawCoreType(r)),
                ComponentDeclKind.Type => new CtTypeDecl(ReadDefType(r)),
                ComponentDeclKind.Alias => ReadAlias(r),
                ComponentDeclKind.Import when allowImports => ReadImportDecl(r),
                ComponentDeclKind.Export => ReadExportDecl(r),
                _ => throw new FormatException($"Unknown component type declaration 0x{kind:X2}."),
            });
        }

        return decls;
    }

    public static void WriteDecls(CtWriter w, List<CtDecl> decls)
    {
        w.U32((uint)decls.Count);
        foreach (CtDecl decl in decls)
        {
            switch (decl)
            {
                case CtCoreTypeDecl core: w.Byte((byte)ComponentDeclKind.CoreType); w.Bytes(core.Raw); break;
                case CtTypeDecl t: w.Byte((byte)ComponentDeclKind.Type); WriteDefType(w, t.Type); break;
                case CtAliasDecl a: w.Byte((byte)ComponentDeclKind.Alias); WriteAlias(w, a); break;
                case CtImportDecl i: w.Byte((byte)ComponentDeclKind.Import); WriteExternName(w, i.Name, i.RawName); WriteExternDesc(w, i.Desc); break;
                case CtExportDecl e: w.Byte((byte)ComponentDeclKind.Export); WriteExternName(w, e.Name, e.RawName); WriteExternDesc(w, e.Desc); break;
                default: throw new InvalidOperationException($"Unhandled declaration {decl.GetType().Name}.");
            }
        }
    }

    // A core:type inside an instance/component type is a moduletype (or a core functype); it never refers to
    // component indices, so it is carried as raw bytes.
    private static byte[] ReadRawCoreType(CtReader r)
    {
        int start = r.Position;
        byte op = r.ReadByte();
        switch (op)
        {
            case (byte)CompositeTypeCode.Func:
                SkipCoreValTypes(r);
                SkipCoreValTypes(r);
                break;
            case 0x50:
                uint count = r.ReadU32();
                for (uint i = 0; i < count; i++)
                {
                    byte kind = r.ReadByte();
                    switch ((CoreModuleDeclKind)kind)
                    {
                        case CoreModuleDeclKind.Import: r.SkipName(); r.SkipName(); SkipCoreImportDesc(r); break;
                        case CoreModuleDeclKind.Type: ReadRawCoreType(r); break;
                        case CoreModuleDeclKind.Alias: r.ReadByte(); r.ReadByte(); r.ReadU32(); r.ReadU32(); break;
                        case CoreModuleDeclKind.Export: r.SkipName(); SkipCoreImportDesc(r); break;
                        default: throw new FormatException($"Unknown core module type declaration 0x{kind:X2}.");
                    }
                }

                break;
            default:
                throw new FormatException($"Unsupported core type 0x{op:X2} in a component type.");
        }

        return r.Slice(start);
    }

    private static void SkipCoreValTypes(CtReader r)
    {
        uint count = r.ReadU32();
        for (uint i = 0; i < count; i++)
            SkipCoreValType(r);
    }

    private static void SkipCoreValType(CtReader r)
    {
        byte b = r.ReadByte();
        if (b is 0x63 or 0x64)
            r.ReadS33();
    }

    private static void SkipCoreImportDesc(CtReader r)
    {
        byte kind = r.ReadByte();
        switch ((CoreSort)kind)
        {
            case CoreSort.Func: r.ReadU32(); break;
            case CoreSort.Table: SkipCoreValType(r); SkipLimits(r); break;
            case CoreSort.Memory: SkipLimits(r); break;
            case CoreSort.Global: SkipCoreValType(r); r.ReadByte(); break;
            case CoreSort.Tag: r.ReadByte(); r.ReadU32(); break;
            default: throw new FormatException($"Unknown core import kind 0x{kind:X2}.");
        }
    }

    private static void SkipLimits(CtReader r)
    {
        byte flags = r.ReadByte();
        r.ReadU32();
        if ((flags & 0x01) != 0)
            r.ReadU32();
    }

    public static CtSort ReadSort(CtReader r)
    {
        var kind = (ComponentSort)r.ReadByte();
        return kind == ComponentSort.Core ? new CtSort(kind, (CoreSort)r.ReadByte()) : new CtSort(kind);
    }

    public static void WriteSort(CtWriter w, CtSort sort)
    {
        w.Byte((byte)sort.Kind);
        if (sort.Kind == ComponentSort.Core)
            w.Byte((byte)sort.CoreSort);
    }

    public static CtAliasDecl ReadAlias(CtReader r)
    {
        CtSort sort = ReadSort(r);
        byte target = r.ReadByte();
        return new CtAliasDecl(sort, (ComponentAliasTarget)target switch
        {
            ComponentAliasTarget.Export => new CtAliasExport(r.ReadU32(), r.ReadName()),
            ComponentAliasTarget.CoreExport => new CtAliasCoreExport(r.ReadU32(), r.ReadName()),
            ComponentAliasTarget.Outer => new CtAliasOuter(r.ReadU32(), r.ReadU32()),
            _ => throw new FormatException($"Unknown alias target 0x{target:X2}."),
        });
    }

    public static void WriteAlias(CtWriter w, CtAliasDecl alias)
    {
        WriteSort(w, alias.Sort);
        switch (alias.Target)
        {
            case CtAliasExport e: w.Byte((byte)ComponentAliasTarget.Export); w.U32(e.Instance); w.Name(e.Name); break;
            case CtAliasCoreExport c: w.Byte((byte)ComponentAliasTarget.CoreExport); w.U32(c.Instance); w.Name(c.Name); break;
            case CtAliasOuter o: w.Byte((byte)ComponentAliasTarget.Outer); w.U32(o.Count); w.U32(o.Index); break;
            default: throw new InvalidOperationException($"Unhandled alias target {alias.Target.GetType().Name}.");
        }
    }

    private static CtImportDecl ReadImportDecl(CtReader r)
    {
        int start = r.Position;
        string name = ReadExternName(r);
        byte[] raw = r.Slice(start);
        return new CtImportDecl(name, ReadExternDesc(r), raw);
    }

    private static CtExportDecl ReadExportDecl(CtReader r)
    {
        int start = r.Position;
        string name = ReadExternName(r);
        byte[] raw = r.Slice(start);
        return new CtExportDecl(name, ReadExternDesc(r), raw);
    }

    public static string ReadExternName(CtReader r) => Encoding.UTF8.GetString(ReadExternNameBytes(r));

    /// <summary>Skips an <c>importname'</c>/<c>exportname'</c>, validating every name in it.</summary>
    public static void SkipExternName(CtReader r) => ReadExternNameBytes(r);

    private static ReadOnlySpan<byte> ReadExternNameBytes(CtReader r)
    {
        byte prefix = r.ReadByte();
        ReadOnlySpan<byte> name = r.ReadNameBytes();
        if (prefix == 0x02)
        {
            uint options = r.ReadU32();
            for (uint i = 0; i < options; i++)
            {
                r.ReadByte();
                r.SkipName();
            }
        }
        else if (prefix > 0x02)
        {
            throw new FormatException($"Unknown import/export name prefix 0x{prefix:X2}.");
        }

        return name;
    }

    public static void WriteExternName(CtWriter w, string name, byte[]? raw = null)
    {
        if (raw is not null)
        {
            w.Bytes(raw);
            return;
        }

        w.Byte(0x00);
        w.Name(name);
    }

    public static CtExternDesc ReadExternDesc(CtReader r)
    {
        var kind = (ComponentExternKind)r.ReadByte();
        switch (kind)
        {
            case ComponentExternKind.CoreModule:
                if (r.ReadByte() != (byte)CoreSort.Module)
                    throw new FormatException("Core extern descriptors other than modules are not valid here.");
                return new CtExternDesc(kind, r.ReadU32());
            case ComponentExternKind.Type:
                return (TypeBoundKind)r.ReadByte() switch
                {
                    TypeBoundKind.Eq => new CtExternDesc(kind, r.ReadU32()),
                    TypeBoundKind.SubResource => new CtExternDesc(kind, 0, SubResource: true),
                    var b => throw new FormatException($"Unknown type bound 0x{(byte)b:X2}."),
                };
            case ComponentExternKind.Value:
            {
                int start = r.Position;
                if ((ValueBoundKind)r.ReadByte() == ValueBoundKind.Value)
                    r.ReadU32();
                else
                    ReadValType(r);
                return new CtExternDesc(kind, 0, RawValueBound: r.Slice(start));
            }
            case ComponentExternKind.Func:
            case ComponentExternKind.Component:
            case ComponentExternKind.Instance:
                return new CtExternDesc(kind, r.ReadU32());
            default:
                throw new FormatException($"Unknown externdesc 0x{(byte)kind:X2}.");
        }
    }

    public static void WriteExternDesc(CtWriter w, CtExternDesc desc)
    {
        w.Byte((byte)desc.Kind);
        switch (desc.Kind)
        {
            case ComponentExternKind.CoreModule: w.Byte((byte)CoreSort.Module); w.U32(desc.Index); break;
            case ComponentExternKind.Type:
                if (desc.SubResource)
                {
                    w.Byte((byte)TypeBoundKind.SubResource);
                }
                else
                {
                    w.Byte((byte)TypeBoundKind.Eq);
                    w.U32(desc.Index);
                }

                break;
            case ComponentExternKind.Value: w.Bytes(desc.RawValueBound!); break;
            default: w.U32(desc.Index); break;
        }
    }

    /// <summary>Whether the declaration adds an entry to the type index space of its instance/component type.</summary>
    public static bool DefinesType(CtDecl decl) => decl switch
    {
        CtTypeDecl => true,
        CtAliasDecl a => a.Sort.Kind == ComponentSort.Type,
        CtImportDecl i => i.Desc.Kind == ComponentExternKind.Type,
        CtExportDecl e => e.Desc.Kind == ComponentExternKind.Type,
        _ => false,
    };

    /// <summary>Whether the declaration adds an entry to the component-instance index space.</summary>
    public static bool DefinesInstance(CtDecl decl) => decl switch
    {
        CtAliasDecl a => a.Sort.Kind == ComponentSort.Instance,
        CtImportDecl i => i.Desc.Kind == ComponentExternKind.Instance,
        CtExportDecl e => e.Desc.Kind == ComponentExternKind.Instance,
        _ => false,
    };

    /// <summary>The type indices a definition references (nested instance/component scopes are not entered).</summary>
    public static IEnumerable<uint> ReferencedTypes(CtDefType type)
    {
        static IEnumerable<uint> Of(CtValType? t) => t is { IsPrimitive: false } v ? [v.Value] : [];
        return type switch
        {
            CtRecord r => r.Fields.SelectMany(f => Of(f.Type)),
            CtVariant v => v.Cases.SelectMany(c => Of(c.Type)),
            CtList l => Of(l.Element),
            CtTuple t => t.Elements.SelectMany(e => Of(e)),
            CtOption o => Of(o.Element),
            CtResult r => Of(r.Ok).Concat(Of(r.Err)),
            CtOwn o => [o.Resource],
            CtBorrow b => [b.Resource],
            CtStream s => Of(s.Element),
            CtFuture f => Of(f.Element),
            CtMap m => Of(m.Key).Concat(Of(m.Value)),
            CtFunc f => f.Params.SelectMany(p => Of(p.Type)).Concat(Of(f.Result))
                .Concat(f.NamedResults?.SelectMany(n => Of(n.Type)) ?? []),
            _ => [],
        };
    }

    /// <summary>Rewrites every type index a definition references through <paramref name="map"/>.</summary>
    public static CtDefType RemapTypes(CtDefType type, Func<uint, uint> map)
    {
        CtValType M(CtValType t) => t.IsPrimitive ? t : CtValType.Ref(map(t.Value));
        CtValType? MO(CtValType? t) => t is { } v ? M(v) : null;
        List<(string, CtValType)> ML(List<(string Name, CtValType Type)> l) => l.Select(x => (x.Name, M(x.Type))).ToList();
        return type switch
        {
            CtRecord r => new CtRecord(ML(r.Fields)),
            CtVariant v => new CtVariant(v.Cases.Select(c => (c.Name, MO(c.Type))).ToList()),
            CtList l => l with { Element = M(l.Element) },
            CtTuple t => new CtTuple(t.Elements.Select(M).ToList()),
            CtOption o => new CtOption(M(o.Element)),
            CtResult r => new CtResult(MO(r.Ok), MO(r.Err)),
            CtOwn o => new CtOwn(map(o.Resource)),
            CtBorrow b => new CtBorrow(map(b.Resource)),
            CtStream s => new CtStream(MO(s.Element)),
            CtFuture f => new CtFuture(MO(f.Element)),
            CtMap m => new CtMap(M(m.Key), M(m.Value)),
            CtFunc f => f with { Params = ML(f.Params), Result = MO(f.Result), NamedResults = f.NamedResults is { } n ? ML(n) : null },
            CtInstanceType or CtComponentType => throw new NotSupportedException("Nested instance/component types cannot be remapped."),
            _ => type,
        };
    }
}
