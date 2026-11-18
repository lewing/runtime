// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using Microsoft.NET.WebAssembly.Binary;

/// <summary>Canonical ABI flattening of component function types (component model CanonicalABI.md).</summary>
internal sealed class CanonicalAbi(WitWorld world)
{
    public const byte I32 = 0x7F, I64 = 0x7E, F32 = 0x7D, F64 = 0x7C;
    private const int MaxFlatParams = 16;
    private const int MaxFlatResults = 1;

    private abstract record Resolved;
    private sealed record ResolvedDef(WitInterface Scope, CtDefType Type) : Resolved;
    private sealed record ResolvedResource : Resolved;

    private Resolved Resolve(WitInterface scope, uint index)
    {
        for (int guard = 0; guard < 1000; guard++)
        {
            if (!scope.TypeDecls.TryGetValue(index, out int d))
                throw new LinkerException($"type index {index} is out of range in interface `{scope.Name}`.");
            switch (scope.Decls[d])
            {
                case CtTypeDecl t:
                    return new ResolvedDef(scope, t.Type);
                case CtAliasDecl { Target: CtAliasSymbol symbol }:
                    WitInterface target = world.Lookup(symbol);
                    if (!target.Exports.TryGetValue(symbol.Name, out int e) || target.Decls[e] is not CtExportDecl { Desc.Kind: ComponentExternKind.Type } export)
                        throw new LinkerException($"interface `{target.Name}` does not export a type named `{symbol.Name}`.");
                    if (export.Desc.SubResource)
                        return new ResolvedResource();
                    scope = target;
                    index = export.Desc.Index;
                    break;
                case CtExportDecl { Desc.Kind: ComponentExternKind.Type } local:
                    if (local.Desc.SubResource)
                        return new ResolvedResource();
                    index = local.Desc.Index;
                    break;
                default:
                    throw new LinkerException($"type index {index} in interface `{scope.Name}` is not a value type.");
            }
        }

        throw new LinkerException($"type alias cycle in interface `{scope.Name}`.");
    }

    public void Flatten(WitInterface scope, CtValType type, List<byte> output)
    {
        if (type.IsPrimitive)
        {
            output.AddRange(FlattenPrimitive(type.Code));
            return;
        }

        switch (Resolve(scope, type.Value))
        {
            case ResolvedResource:
                output.Add(I32);
                break;
            case ResolvedDef { Type: var def, Scope: var s }:
                FlattenDef(s, def, output);
                break;
        }
    }

    private static byte[] FlattenPrimitive(byte code) => code switch
    {
        0x78 or 0x77 => [I64],
        0x76 => [F32],
        0x75 => [F64],
        0x73 => [I32, I32],
        _ => [I32],
    };

    private void FlattenDef(WitInterface scope, CtDefType def, List<byte> output)
    {
        switch (def)
        {
            case CtPrimitive p:
                output.AddRange(FlattenPrimitive(p.Code));
                break;
            case CtRecord r:
                foreach (var field in r.Fields)
                    Flatten(scope, field.Type, output);
                break;
            case CtTuple t:
                foreach (CtValType e in t.Elements)
                    Flatten(scope, e, output);
                break;
            case CtList { FixedLength: { } length } fixedList:
                for (uint i = 0; i < length; i++)
                    Flatten(scope, fixedList.Element, output);
                break;
            case CtList or CtMap:
                output.Add(I32);
                output.Add(I32);
                break;
            case CtFlags f:
                for (int i = 0; i < (f.Names.Count + 31) / 32; i++)
                    output.Add(I32);
                break;
            case CtEnum or CtOwn or CtBorrow or CtStream or CtFuture:
                output.Add(I32);
                break;
            case CtVariant v:
                FlattenVariant(scope, v.Cases.Select(c => c.Type), output);
                break;
            case CtOption o:
                FlattenVariant(scope, [null, o.Element], output);
                break;
            case CtResult r:
                FlattenVariant(scope, [r.Ok, r.Err], output);
                break;
            default:
                throw new LinkerException($"type {def.GetType().Name} cannot be passed by value.");
        }
    }

    private void FlattenVariant(WitInterface scope, IEnumerable<CtValType?> cases, List<byte> output)
    {
        var joined = new List<byte>();
        foreach (CtValType? c in cases)
        {
            if (c is not { } payload)
                continue;
            var flat = new List<byte>();
            Flatten(scope, payload, flat);
            for (int i = 0; i < flat.Count; i++)
            {
                if (i < joined.Count)
                    joined[i] = Join(joined[i], flat[i]);
                else
                    joined.Add(flat[i]);
            }
        }

        output.Add(I32);
        output.AddRange(joined);
    }

    private static byte Join(byte a, byte b) => a == b ? a : (a, b) is (I32, F32) or (F32, I32) ? I32 : I64;

    /// <summary>Whether a value of this type is stored in linear memory (it contains a string or list).</summary>
    public bool NeedsMemory(WitInterface scope, CtValType type, HashSet<(WitInterface, uint)>? visiting = null)
    {
        if (type.IsPrimitive)
            return type.Code == 0x73;
        visiting ??= [];
        if (!visiting.Add((scope, type.Value)))
            return false;
        if (Resolve(scope, type.Value) is not ResolvedDef { Type: var def, Scope: var s })
            return false;
        return def switch
        {
            CtList { FixedLength: null } or CtMap => true,
            CtList l => NeedsMemory(s, l.Element, visiting),
            CtRecord r => r.Fields.Any(f => NeedsMemory(s, f.Type, visiting)),
            CtTuple t => t.Elements.Any(e => NeedsMemory(s, e, visiting)),
            CtVariant v => v.Cases.Any(c => c.Type is { } p && NeedsMemory(s, p, visiting)),
            CtOption o => NeedsMemory(s, o.Element, visiting),
            CtResult r => (r.Ok is { } ok && NeedsMemory(s, ok, visiting)) || (r.Err is { } err && NeedsMemory(s, err, visiting)),
            CtPrimitive p => p.Code == 0x73,
            _ => false,
        };
    }

    public bool ContainsString(WitInterface scope, CtValType type, HashSet<(WitInterface, uint)>? visiting = null)
    {
        if (type.IsPrimitive)
            return type.Code == 0x73;
        visiting ??= [];
        if (!visiting.Add((scope, type.Value)))
            return false;
        if (Resolve(scope, type.Value) is not ResolvedDef { Type: var def, Scope: var s })
            return false;
        return def switch
        {
            CtPrimitive p => p.Code == 0x73,
            _ => ComponentTypeCodec.ReferencedTypes(def).Any(i => ContainsString(s, CtValType.Ref(i), visiting)),
        };
    }

    /// <summary>The lowered or lifted core signature of a component function, and the canonical options it needs.</summary>
    public sealed record Lowering(byte[] Params, byte[] Results, bool Memory, bool Realloc, bool Strings);

    public Lowering Flatten(WitInterface scope, CtFunc func, bool lift)
    {
        var parameters = new List<byte>();
        foreach (var p in func.Params)
            Flatten(scope, p.Type, parameters);
        var results = new List<byte>();
        IEnumerable<CtValType> resultTypes = func.NamedResults?.Select(n => n.Type) ?? (func.Result is { } r ? [r] : []);
        foreach (CtValType t in resultTypes)
            Flatten(scope, t, results);

        bool paramsInMemory = func.Params.Any(p => NeedsMemory(scope, p.Type));
        bool resultsInMemory = resultTypes.Any(t => NeedsMemory(scope, t));
        bool memory = paramsInMemory || resultsInMemory;
        bool realloc = lift ? paramsInMemory : resultsInMemory;
        if (parameters.Count > MaxFlatParams)
        {
            parameters = [I32];
            memory = true;
            if (lift)
                realloc = true;
        }

        if (results.Count > MaxFlatResults)
        {
            memory = true;
            if (lift)
            {
                results = [I32];
            }
            else
            {
                parameters.Add(I32);
                results = [];
            }
        }

        bool strings = func.Params.Any(p => ContainsString(scope, p.Type)) || resultTypes.Any(t => ContainsString(scope, t));
        return new Lowering([.. parameters], [.. results], memory, realloc, strings);
    }
}
