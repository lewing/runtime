// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>An interface (component instance type) imported or exported by a WIT world.</summary>
internal sealed class WitInterface
{
    public required string Name { get; init; }
    public required string Key { get; init; }
    public required SemVersion? Version { get; init; }
    public required bool IsExport { get; init; }

    /// <summary>
    /// The instance type's declarations. Outer aliases (references to another interface's types) are replaced by
    /// <see cref="CtAliasSymbol"/> so that interfaces from different worlds can be merged without index remapping.
    /// </summary>
    public required List<CtDecl> Decls { get; init; }

    private Dictionary<uint, int>? _typeDecls;
    private Dictionary<string, int>? _exports;

    /// <summary>Maps each local type index to the declaration that defines it.</summary>
    public Dictionary<uint, int> TypeDecls => _typeDecls ??= BuildTypeDecls();

    /// <summary>Maps each export name (function or type) to its declaration.</summary>
    public Dictionary<string, int> Exports => _exports ??= Decls
        .Select((d, i) => (d, i)).Where(x => x.d is CtExportDecl)
        .ToDictionary(x => ((CtExportDecl)x.d).Name, x => x.i, StringComparer.Ordinal);

    private Dictionary<uint, int> BuildTypeDecls()
    {
        var map = new Dictionary<uint, int>();
        uint next = 0;
        for (int i = 0; i < Decls.Count; i++)
        {
            if (ComponentTypeCodec.DefinesType(Decls[i]))
                map[next++] = i;
        }

        return map;
    }

    public CtFunc? FindFunction(string name, out int declIndex)
    {
        if (Exports.TryGetValue(name, out declIndex) && Decls[declIndex] is CtExportDecl { Desc.Kind: ComponentExternKind.Func } export)
            return Resolve(export.Desc.Index) as CtFunc;
        declIndex = -1;
        return null;
    }

    private CtDefType? Resolve(uint index) =>
        TypeDecls.TryGetValue(index, out int d) && Decls[d] is CtTypeDecl t ? t.Type : null;

    public override string ToString() => Name;
}

/// <summary>A reference to the type exported as <see cref="Name"/> by the interface keyed <see cref="InterfaceKey"/>.</summary>
internal sealed record CtAliasSymbol(string InterfaceKey, bool FromExport, string Name) : CtAliasTarget;

/// <summary>A semantic version, as used by WIT package names.</summary>
internal readonly record struct SemVersion(ulong Major, ulong Minor, ulong Patch, string Pre) : IComparable<SemVersion>
{
    public static SemVersion? TryParse(string text)
    {
        string core = text;
        string pre = "";
        int build = core.IndexOf('+');
        if (build >= 0)
            core = core[..build];
        int dash = core.IndexOf('-');
        if (dash >= 0)
        {
            pre = core[(dash + 1)..];
            core = core[..dash];
        }

        string[] parts = core.Split('.');
        return parts.Length == 3 && ulong.TryParse(parts[0], out ulong major) && ulong.TryParse(parts[1], out ulong minor)
            && ulong.TryParse(parts[2], out ulong patch)
            ? new SemVersion(major, minor, patch, pre)
            : null;
    }

    public int CompareTo(SemVersion other)
    {
        int c = Major.CompareTo(other.Major);
        if (c == 0)
            c = Minor.CompareTo(other.Minor);
        if (c == 0)
            c = Patch.CompareTo(other.Patch);
        if (c == 0)
            c = (Pre.Length == 0, other.Pre.Length == 0) switch
            {
                (true, false) => 1,
                (false, true) => -1,
                _ => string.CompareOrdinal(Pre, other.Pre),
            };
        return c;
    }

    /// <summary>
    /// The semver-compatibility track used to unify interface versions, as wit-component does: <c>1.2.3</c> is
    /// on track <c>1</c>, <c>0.2.6</c> on track <c>0.2</c>, and <c>0.0.1</c> or prereleases only match exactly.
    /// </summary>
    public string Track => Pre.Length != 0 ? $"{Major}.{Minor}.{Patch}-{Pre}"
        : Major != 0 ? $"{Major}"
        : Minor != 0 ? $"0.{Minor}"
        : $"0.0.{Patch}";

    public override string ToString() => Pre.Length == 0 ? $"{Major}.{Minor}.{Patch}" : $"{Major}.{Minor}.{Patch}-{Pre}";
}

/// <summary>The world described by one or more <c>component-type</c> custom sections, merged.</summary>
internal sealed class WitWorld
{
    public List<WitInterface> Imports { get; } = [];
    public List<WitInterface> Exports { get; } = [];
    public byte StringEncoding { get; private set; }

    /// <summary>Splits <c>ns:pkg/iface@1.2.3</c> into its unversioned name and version, and forms its merge key.</summary>
    public static (string Key, SemVersion? Version) ParseName(string name, bool mergeBySemver)
    {
        int at = name.LastIndexOf('@');
        if (at < 0)
            return (name, null);
        SemVersion? version = SemVersion.TryParse(name[(at + 1)..]);
        if (version is null || !mergeBySemver)
            return (name, version);
        return ($"{name[..at]}@{version.Value.Track}", version);
    }

    public WitInterface? FindImport(string key) => Imports.FirstOrDefault(i => i.Key == key);
    public WitInterface? FindExport(string key) => Exports.FirstOrDefault(i => i.Key == key);

    public WitInterface Lookup(CtAliasSymbol symbol) =>
        (symbol.FromExport ? FindExport(symbol.InterfaceKey) : FindImport(symbol.InterfaceKey))
        ?? throw new LinkerException($"component type refers to unknown interface `{symbol.InterfaceKey}`.");

    /// <summary>Adds the world encoded in a <c>component-type</c> custom section payload.</summary>
    public void Add(byte[] payload, bool mergeBySemver)
    {
        var r = new CtReader(payload);
        if (payload.Length < 8 || payload[0] != 0 || payload[1] != (byte)'a' || payload[2] != (byte)'s' || payload[3] != (byte)'m'
            || payload[4] != 0x0D || payload[5] != 0 || payload[6] != 1 || payload[7] != 0)
            throw new LinkerException("component-type section does not contain a component.");
        r.Position = 8;
        var types = new List<CtDefType>();
        CtComponentType? world = null;
        while (r.HasMore)
        {
            byte id = r.ReadByte();
            CtReader section = r.Sub(r.ReadLength());
            switch ((ComponentSectionId)id)
            {
                case ComponentSectionId.Custom:
                    if (section.ReadNameBytes().SequenceEqual("wit-component-encoding"u8) && section.HasMore)
                    {
                        section.ReadByte();
                        if (section.HasMore)
                            StringEncoding = section.ReadByte();
                    }

                    break;
                case ComponentSectionId.Type:
                    uint count = section.ReadU32();
                    for (uint i = 0; i < count; i++)
                        types.Add(ComponentTypeCodec.ReadDefType(section));
                    break;
                case ComponentSectionId.Export:
                    // A world encoded as `(component (type (component <world>)) (export "pkg/world" (component (type 0))))`
                    // lives in a type definition; a bare export section is not expected here.
                    break;
            }
        }

        foreach (CtDefType type in types)
        {
            if (type is not CtComponentType outer)
                continue;
            var outerTypes = new List<CtDefType?>();
            foreach (CtDecl decl in outer.Decls)
            {
                switch (decl)
                {
                    case CtTypeDecl t:
                        outerTypes.Add(t.Type);
                        break;
                    case CtExportDecl { Desc.Kind: ComponentExternKind.Component } e
                        when e.Desc.Index < outerTypes.Count && outerTypes[(int)e.Desc.Index] is CtComponentType w:
                        world = w;
                        break;
                    default:
                        if (ComponentTypeCodec.DefinesType(decl))
                            outerTypes.Add(null);
                        break;
                }
            }
        }

        if (world is null)
            throw new LinkerException("component-type section does not declare a world.");
        AddWorld(world, mergeBySemver);
    }

    private void AddWorld(CtComponentType world, bool mergeBySemver)
    {
        // Each entry is a world-level type: a definition, or a symbol for a type aliased from an interface.
        var worldTypes = new List<object?>();
        var instances = new List<WitInterface>();
        foreach (CtDecl decl in world.Decls)
        {
            switch (decl)
            {
                case CtTypeDecl t:
                    worldTypes.Add(t.Type);
                    break;
                case CtAliasDecl { Sort.Kind: ComponentSort.Type, Target: CtAliasExport e }:
                    WitInterface owner = instances[(int)e.Instance];
                    worldTypes.Add(new CtAliasSymbol(owner.Key, owner.IsExport, e.Name));
                    break;
                case CtImportDecl { Desc.Kind: ComponentExternKind.Instance } import:
                    instances.Add(Merge(Imports, Normalize(import.Name, import.Desc.Index, worldTypes, false, mergeBySemver)));
                    break;
                case CtExportDecl { Desc.Kind: ComponentExternKind.Instance } export:
                    instances.Add(Merge(Exports, Normalize(export.Name, export.Desc.Index, worldTypes, true, mergeBySemver)));
                    break;
                default:
                    if (ComponentTypeCodec.DefinesType(decl))
                        worldTypes.Add(null);
                    if (ComponentTypeCodec.DefinesInstance(decl))
                        throw new NotSupportedException("Component worlds with aliased instances are not supported.");
                    break;
            }
        }
    }

    private static WitInterface Normalize(string name, uint typeIndex, List<object?> worldTypes, bool isExport, bool mergeBySemver)
    {
        if (typeIndex >= worldTypes.Count || worldTypes[(int)typeIndex] is not CtInstanceType instance)
            throw new LinkerException($"world {(isExport ? "export" : "import")} `{name}` is not an instance type.");
        var decls = new List<CtDecl>(instance.Decls.Count);
        foreach (CtDecl decl in instance.Decls)
        {
            if (decl is CtAliasDecl { Target: CtAliasOuter outer } alias)
            {
                if (outer.Count != 1 || alias.Sort.Kind != ComponentSort.Type || outer.Index >= worldTypes.Count
                    || worldTypes[(int)outer.Index] is not CtAliasSymbol symbol)
                    throw new NotSupportedException(
                        $"Interface `{name}` refers to a world-level type; only types from other interfaces are supported.");
                decls.Add(new CtAliasDecl(CtSort.TypeSort, symbol));
            }
            else
            {
                decls.Add(decl);
            }
        }

        var (key, version) = ParseName(name, mergeBySemver);
        return new WitInterface { Name = name, Key = key, Version = version, IsExport = isExport, Decls = decls };
    }

    private static WitInterface Merge(List<WitInterface> list, WitInterface candidate)
    {
        int existing = list.FindIndex(i => i.Key == candidate.Key);
        if (existing < 0)
        {
            list.Add(candidate);
            return candidate;
        }

        WitInterface current = list[existing];
        if (candidate.Version is { } v && current.Version is { } c && v.CompareTo(c) > 0)
        {
            list[existing] = candidate;
            return candidate;
        }

        return current;
    }
}
