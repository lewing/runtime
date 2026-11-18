// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using System.Collections.Generic;
using System.Linq;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Encodes a resolved WIT world as a <c>component-type</c> payload, porting wit-component's
/// <c>metadata::encode</c>, <c>encode_world</c> and <c>ValtypeEncoder</c>, so the bytes match
/// <c>wasm-tools component embed --only-custom</c> apart from the producers section, which is omitted.
/// </summary>
internal sealed class WitWorldEncoder
{
    private const byte EncodingVersion = 0x04;
    private const byte Utf8 = 0x00;

    private sealed class Scope
    {
        public List<CtDecl> Decls { get; } = [];
        public uint TypeCount { get; set; }

        public uint Add(CtDecl decl)
        {
            Decls.Add(decl);
            return ComponentTypeCodec.DefinesType(decl) ? TypeCount++ : TypeCount;
        }
    }

    private sealed class Maps
    {
        public Dictionary<WitTDef, uint> IdToIndex { get; } = [];
        public Dictionary<string, uint> DefToIndex { get; } = [];
        public Dictionary<string, uint> FuncTypes { get; } = [];
    }

    private readonly Scope _outer = new();
    private Scope? _instance;
    private Maps _maps = new();
    private Maps? _saved;
    private readonly Dictionary<WitIface, uint> _importMap = [];
    private readonly Dictionary<WitTDef, uint> _outerTypeMap = [];
    private uint _instances;
    private bool _importTypes;
    private WitIface? _interface;

    /// <summary>The <c>component-type</c> payload for <paramref name="world"/>.</summary>
    public static byte[] EncodePayload(WitResolvedWorld world)
    {
        var worldType = new WitWorldEncoder().EncodeWorld(world);
        var outer = new List<CtDecl>
        {
            new CtTypeDecl(new CtComponentType(worldType)),
            new CtExportDecl(world.World.Package.InterfaceId(world.World.Name), new CtExternDesc(ComponentExternKind.Component, 0)),
        };

        var w = new CtWriter();
        w.Bytes([0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);
        w.Section(ComponentSectionId.Custom, s =>
        {
            s.Name("wit-component-encoding");
            s.Bytes([EncodingVersion, Utf8]);
        });
        w.Section(ComponentSectionId.Type, s =>
        {
            s.U32(1);
            ComponentTypeCodec.WriteDefType(s, new CtComponentType(outer));
        });
        w.Section(ComponentSectionId.Export, s =>
        {
            s.U32(1);
            ComponentTypeCodec.WriteExternName(s, world.World.Name);
            s.Byte((byte)ComponentSort.Type);
            s.U32(0);
            s.Byte(0x00);
        });
        return w.ToArray();
    }

    private List<CtDecl> EncodeWorld(WitResolvedWorld world)
    {
        foreach (var (key, item) in world.Imports)
        {
            CtExternDesc desc;
            switch (item)
            {
                case WitWorldInterface i:
                    _interface = i.Interface;
                    desc = new CtExternDesc(ComponentExternKind.Instance, EncodeInstance(i.Interface));
                    break;
                case WitWorldFunction f:
                    _interface = null;
                    desc = new CtExternDesc(ComponentExternKind.Func, EncodeFuncType(f.Function));
                    break;
                case WitWorldType t:
                    _interface = null;
                    _importTypes = true;
                    EncodeValType(new WitRef(t.Def));
                    _importTypes = false;
                    continue;
                default:
                    throw new InvalidOperationException();
            }

            _outer.Add(new CtImportDecl(key.Name, desc));
        }

        foreach (var (key, item) in world.Exports)
        {
            CtExternDesc desc = item switch
            {
                WitWorldInterface i => EncodeExportedInstance(i.Interface),
                WitWorldFunction f => EncodeExportedFunc(f.Function),
                _ => throw new InvalidOperationException(),
            };
            _outer.Add(new CtExportDecl(key.Name, desc));
        }

        return _outer.Decls;
    }

    private CtExternDesc EncodeExportedInstance(WitIface iface)
    {
        _interface = iface;
        return new CtExternDesc(ComponentExternKind.Instance, EncodeInstance(iface));
    }

    private CtExternDesc EncodeExportedFunc(WitFunction function)
    {
        _interface = null;
        return new CtExternDesc(ComponentExternKind.Func, EncodeFuncType(function));
    }

    private uint EncodeInstance(WitIface iface)
    {
        _saved = _maps;
        _maps = new Maps();
        _instance = new Scope();

        var typeOrder = new Dictionary<WitTDef, int>();
        foreach (var (_, def) in iface.Types)
        {
            EncodeValType(new WitRef(def));
            typeOrder.TryAdd(def, typeOrder.Count);
        }

        // Resource functions first, in the order their resources were encoded; freestanding ones last (stable).
        var functions = iface.Functions
            .Select((f, i) => (f, i))
            .OrderBy(p => p.f.Resource is { } r ? typeOrder[r] : typeOrder.Count)
            .ThenBy(p => p.i)
            .Select(p => p.f);
        foreach (var function in functions)
        {
            uint type = EncodeFuncType(function);
            _instance.Add(new CtExportDecl(function.Name, new CtExternDesc(ComponentExternKind.Func, type)));
        }

        var instance = _instance;
        _instance = null;
        _maps = _saved;
        _saved = null;

        uint index = _outer.Add(new CtTypeDecl(new CtInstanceType(instance.Decls)));
        _importMap[iface] = _instances++;
        return index;
    }

    private Scope Current => _instance ?? _outer;

    private uint DefineType(CtDefType type) => Current.Add(new CtTypeDecl(type));

    private uint EncodeFuncType(WitFunction function)
    {
        string key = (function.Async ? "async(" : "(") +
            string.Join(",", function.Params.Select(p => p.Name + ":" + WitKindKey.Of(p.Type))) + ")->" + WitKindKey.Of(function.Result);
        if (_maps.FuncTypes.TryGetValue(key, out uint existing))
            return existing;

        var parameters = function.Params.Select(p => (p.Name, EncodeValType(p.Type))).ToList();
        CtValType? result = function.Result is null ? null : EncodeValType(function.Result);
        uint index = DefineType(new CtFunc(function.Async, parameters, result, null));
        _maps.FuncTypes.Add(key, index);
        return index;
    }

    private CtValType EncodeValType(WitVal type)
    {
        if (type is WitPrimitive p)
            return CtValType.Prim(p.Code);

        var def = ((WitRef)type).Def;
        if (Lookup(def) is { } found)
            return CtValType.Ref(found);

        if (def.Owner is WitIface owner && owner != _interface)
        {
            uint imported = ImportType(owner, def);
            Insert(def, imported);
            return CtValType.Ref(imported);
        }

        CtValType encoded;
        switch (def.Kind)
        {
            case WitRecordKind r:
            {
                var fields = r.Fields.Select(f => (f.Name, EncodeValType(f.Type))).ToList();
                encoded = CtValType.Ref(DefineType(new CtRecord(fields)));
                break;
            }

            case WitTupleKind t:
            {
                var types = t.Types.Select(EncodeValType).ToList();
                encoded = CtValType.Ref(DefineType(new CtTuple(types)));
                break;
            }

            case WitFlagsKind f:
                encoded = CtValType.Ref(DefineType(new CtFlags([.. f.Flags])));
                break;
            case WitVariantKind v:
            {
                var cases = v.Cases.Select(c => (c.Name, c.Type is null ? (CtValType?)null : EncodeValType(c.Type))).ToList();
                encoded = CtValType.Ref(DefineType(new CtVariant(cases)));
                break;
            }

            case WitOptionKind o:
            {
                var element = EncodeValType(o.Element);
                encoded = CtValType.Ref(DefineType(new CtOption(element)));
                break;
            }

            case WitResultKind r:
            {
                CtValType? ok = r.Ok is null ? null : EncodeValType(r.Ok);
                CtValType? err = r.Err is null ? null : EncodeValType(r.Err);
                encoded = CtValType.Ref(DefineType(new CtResult(ok, err)));
                break;
            }

            case WitEnumKind e:
                encoded = CtValType.Ref(DefineType(new CtEnum([.. e.Cases])));
                break;
            case WitListKind l:
            {
                var element = EncodeValType(l.Element);
                encoded = CtValType.Ref(DefineType(new CtList(element, l.Length)));
                break;
            }

            case WitMapKind m:
            {
                var key = EncodeValType(m.Key);
                var value = EncodeValType(m.Value);
                encoded = CtValType.Ref(DefineType(new CtMap(key, value)));
                break;
            }

            case WitAliasKind a:
                encoded = EncodeValType(a.Target);
                break;
            case WitFutureKind f:
            {
                CtValType? element = f.Element is null ? null : EncodeValType(f.Element);
                encoded = CtValType.Ref(DefineType(new CtFuture(element)));
                break;
            }

            case WitStreamKind s:
            {
                CtValType? element = s.Element is null ? null : EncodeValType(s.Element);
                encoded = CtValType.Ref(DefineType(new CtStream(element)));
                break;
            }

            case WitResourceKind:
            {
                uint index = ExportResource(def.Name!);
                _maps.IdToIndex[def] = index;
                return CtValType.Ref(index);
            }

            case WitOwnKind o:
            {
                uint resource = EncodeValType(new WitRef(o.Resource)).Value;
                encoded = CtValType.Ref(DefineType(new CtOwn(resource)));
                break;
            }

            case WitBorrowKind b:
            {
                uint resource = EncodeValType(new WitRef(b.Resource)).Value;
                encoded = CtValType.Ref(DefineType(new CtBorrow(resource)));
                break;
            }

            default:
                throw new InvalidOperationException($"Unhandled WIT kind {def.Kind.GetType().Name}.");
        }

        if (def.Name is { } name)
        {
            uint index = encoded.IsPrimitive ? DefineType(new CtPrimitive((byte)encoded.Value)) : encoded.Value;
            encoded = CtValType.Ref(ExportType(index, name) ?? index);
        }

        if (!encoded.IsPrimitive)
            Insert(def, encoded.Value);
        return encoded;
    }

    private uint? Lookup(WitTDef def)
    {
        if (_maps.IdToIndex.TryGetValue(def, out uint index))
            return index;
        if (def.Name is null && _maps.DefToIndex.TryGetValue(WitKindKey.Of(def.Kind), out index))
            return index;
        return null;
    }

    private void Insert(WitTDef def, uint index)
    {
        _maps.IdToIndex[def] = index;
        if (def.Name is null)
            _maps.DefToIndex[WitKindKey.Of(def.Kind)] = index;
    }

    private uint? ExportType(uint index, string name)
    {
        var desc = new CtExternDesc(ComponentExternKind.Type, index);
        if (_instance is { } instance)
            return instance.Add(new CtExportDecl(name, desc));
        return _outer.Add(_importTypes ? new CtImportDecl(name, desc) : new CtExportDecl(name, desc));
    }

    private uint ExportResource(string name)
    {
        var desc = new CtExternDesc(ComponentExternKind.Type, 0, SubResource: true);
        if (_instance is { } instance)
            return instance.Add(new CtExportDecl(name, desc));
        return _outer.Add(_importTypes ? new CtImportDecl(name, desc) : new CtExportDecl(name, desc));
    }

    private uint ImportType(WitIface owner, WitTDef def)
    {
        if (!_importMap.TryGetValue(owner, out uint instance))
            throw new LinkerException($"WIT type `{def.Name}` comes from {owner.Id}, which the world does not import first.");
        if (!_outerTypeMap.TryGetValue(def, out uint outerIndex))
        {
            outerIndex = _outer.Add(new CtAliasDecl(CtSort.TypeSort, new CtAliasExport(instance, def.Name!)));
            _outerTypeMap[def] = outerIndex;
        }

        return _instance is { } scope
            ? scope.Add(new CtAliasDecl(CtSort.TypeSort, new CtAliasOuter(1, outerIndex)))
            : outerIndex;
    }
}
