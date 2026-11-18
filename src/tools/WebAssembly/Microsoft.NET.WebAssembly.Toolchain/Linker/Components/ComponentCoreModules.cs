// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Core-module surgery on component binaries: listing, replacing and unbundling the core modules a component
/// embeds. <see cref="Unbundle"/> reproduces <c>wasm-tools component unbundle</c> byte for byte on the components
/// wit-component and wasm-component-ld produce.
/// </summary>
public static class ComponentCoreModules
{
    /// <summary>The default <c>--threshold</c> of <c>wasm-tools component unbundle</c>: modules larger than this are extracted.</summary>
    public const int DefaultUnbundleThreshold = 10240;


    /// <summary>The result of <see cref="Unbundle"/>: the rewritten component and the extracted modules, in order.</summary>
    public sealed record UnbundleResult(byte[] Component, IReadOnlyList<byte[]> Modules);

    /// <summary>The bytes of each top-level core module, in section order.</summary>
    public static IReadOnlyList<byte[]> GetCoreModules(byte[] component)
    {
        RequireComponent(component);
        var modules = new List<byte[]>();
        foreach (var (id, start, end, _) in Sections(component))
        {
            if ((ComponentSectionId)id == ComponentSectionId.CoreModule)
                modules.Add(component[start..end]);
        }

        return modules;
    }

    /// <summary>
    /// Replaces the <paramref name="index"/>th top-level core module of <paramref name="component"/> with <paramref name="module"/>.
    /// </summary>
    /// <remarks>
    /// The replacement must be usable wherever the original was: it may export more and import less, but every
    /// export of the original must still exist with a compatible type, and each of its imports must be one the
    /// original had, with a type the original's import satisfies. Anything else is a <see cref="LinkerException"/>.
    /// </remarks>
    public static byte[] ReplaceCoreModule(byte[] component, int index, byte[] module)
    {
        RequireComponent(component);
        if (module.Length < 8 || module[0] != 0 || module[1] != (byte)'a' || module[2] != (byte)'s' || module[3] != (byte)'m'
            || module[4] != 1 || module[5] != 0 || module[6] != 0 || module[7] != 0)
            throw new LinkerException("the replacement is not a core WebAssembly module.");

        var w = new CtWriter();
        w.Bytes(component.AsSpan(0, 8));
        int current = 0;
        bool replaced = false;
        foreach (var (id, start, end, sectionStart) in Sections(component))
        {
            if ((ComponentSectionId)id == ComponentSectionId.CoreModule && current++ == index)
            {
                RequireCompatible(component[start..end], module, index);
                w.Byte((byte)ComponentSectionId.CoreModule);
                w.U32((uint)module.Length);
                w.Bytes(module);
                replaced = true;
                continue;
            }

            w.Bytes(component.AsSpan(sectionStart, end - sectionStart));
        }

        if (!replaced)
            throw new LinkerException($"the component has no core module {index}; it has {current}.");
        return w.ToArray();
    }

    private static void RequireCompatible(byte[] original, byte[] replacement, int index)
    {
        var before = ModuleInfo.Read(original);
        var after = ModuleInfo.Read(replacement);
        var errors = new List<string>();

        var originalImports = before.Imports.ToLookup(i => (Decode(i.Module), Decode(i.Field)), i => i.Entity);
        foreach (var (moduleBytes, fieldBytes, entity) in after.Imports)
        {
            string moduleName = Decode(moduleBytes), field = Decode(fieldBytes);
            // Whatever satisfied the original import must satisfy this one.
            if (!originalImports.Contains((moduleName, field)))
                errors.Add($"it imports '{moduleName}' '{field}', which core module {index} does not import");
            else if (!originalImports[(moduleName, field)].Any(o => IsSubtype(before, o, after, entity)))
                errors.Add($"its import '{moduleName}' '{field}' has a type the original import does not satisfy");
        }

        var replacementExports = after.Exports.ToDictionary(e => Decode(e.Name), e => e.Entity, StringComparer.Ordinal);
        foreach (var (nameBytes, entity) in before.Exports)
        {
            string name = Decode(nameBytes);
            if (!replacementExports.TryGetValue(name, out var export))
                errors.Add($"it does not export '{name}'");
            else if (!IsSubtype(after, export, before, entity))
                errors.Add($"its export '{name}' has a type incompatible with the original export");
        }

        if (errors.Count > 0)
        {
            throw new LinkerException(
                $"the replacement cannot stand in for core module {index}: " + string.Join("; ", errors) + ".");
        }

        // ModuleInfo has already validated the names; they are decoded only to key and report them.
        static string Decode(ReadOnlyMemory<byte> name) => Encoding.UTF8.GetString(name.Span);
    }

    /// <summary>Core-module external subtyping: <paramref name="actual"/> (of <paramref name="actualModule"/>) can be used as <paramref name="expected"/>.</summary>
    private static bool IsSubtype(ModuleInfo actualModule, ModuleInfo.Entity actual, ModuleInfo expectedModule, ModuleInfo.Entity expected)
    {
        if (actual.Kind != expected.Kind)
            return false;
        switch ((CoreSort)actual.Kind)
        {
            case CoreSort.Func:
            case CoreSort.Tag:
                return actualModule.Types[actual.TypeIndex].AsSpan().SequenceEqual(expectedModule.Types[expected.TypeIndex]);
            case CoreSort.Global:
                return actual.Raw.AsSpan().SequenceEqual(expected.Raw);
            case CoreSort.Table:
                // The element type is a reference type, one byte or a typed reference (0x63/0x64 and an s33 heap
                // type); tables are invariant in it, and the limits follow it.
                int actualLength = RefTypeLength(actual.Raw), expectedLength = RefTypeLength(expected.Raw);
                return actual.Raw.AsSpan(0, actualLength).SequenceEqual(expected.Raw.AsSpan(0, expectedLength)) &&
                    LimitsSubtype(actual.Raw, actualLength, expected.Raw, expectedLength);
            case CoreSort.Memory:
                return LimitsSubtype(actual.Raw, 0, expected.Raw, 0);
            default:
                return false;
        }
    }

    private static int RefTypeLength(byte[] raw)
    {
        if (raw[0] is not (0x63 or 0x64))
            return 1;
        var r = new CtReader(raw, 1, raw.Length);
        r.ReadS33();
        return r.Position;
    }

    private static bool LimitsSubtype(byte[] actual, int actualStart, byte[] expected, int expectedStart)
    {
        var a = ReadLimits(actual, actualStart);
        var e = ReadLimits(expected, expectedStart);
        if (a.Flags != e.Flags || a.PageSizeLog2 != e.PageSizeLog2 || a.Min < e.Min)
            return false;
        return e.Max is not { } max || (a.Max is { } actualMax && actualMax <= max);
    }

    private static (byte Flags, ulong Min, ulong? Max, uint? PageSizeLog2) ReadLimits(byte[] raw, int start)
    {
        var r = new CtReader(raw, start, raw.Length);
        byte flags = r.ReadByte();
        bool is64 = (flags & 0x04) != 0;
        ulong min = is64 ? r.ReadU64() : r.ReadU32();
        ulong? max = (flags & 0x01) != 0 ? (is64 ? r.ReadU64() : r.ReadU32()) : null;
        uint? pageSize = (flags & 0x08) != 0 ? r.ReadU32() : null;
        return ((byte)(flags & ~0x01), min, max, pageSize);
    }

    /// <summary>
    /// Moves every core module larger than <paramref name="threshold"/> bytes out of the component, as
    /// <c>wasm-tools component unbundle</c> does: the component instead imports <c>unbundled-moduleN</c> with the
    /// module's type, and each extracted module's section becomes an outer alias of that import.
    /// </summary>
    /// <remarks>
    /// The imports go first, so every top-level core module and core type index shifts. That is rewritten in
    /// core-instance instantiations and component-instance arguments and exports, which covers wit-component's
    /// output. A component that refers to top-level core modules or core types anywhere else (a top-level core type
    /// section, core-module imports or exports, outer aliases of them, modules in nested components) is rejected
    /// with <see cref="NotSupportedException"/>.
    /// </remarks>
    public static UnbundleResult Unbundle(byte[] component, int threshold = DefaultUnbundleThreshold)
    {
        RequireComponent(component);
        var extracted = new List<byte[]>();
        foreach (var (id, start, end, _) in Sections(component))
        {
            if ((ComponentSectionId)id == ComponentSectionId.CoreModule && end - start > threshold)
                extracted.Add(component[start..end]);
            else if ((ComponentSectionId)id == ComponentSectionId.Component)
                CheckNested(component, start, end, depth: 1, threshold);
            else if ((ComponentSectionId)id is ComponentSectionId.CoreType)
                throw new NotSupportedException("unbundling a component with a top-level core type section is not supported.");
        }

        // wasm-tools writes both leading sections even when nothing is extracted.
        // Core types: function types interned across modules, then one module type per extracted module.
        var funcTypes = new Dictionary<string, uint>(StringComparer.Ordinal);
        var typeEntries = new List<byte[]>();
        var moduleTypeIndices = new List<uint>();
        foreach (byte[] module in extracted)
        {
            byte[] moduleType = ModuleType(module, funcTypes, typeEntries);
            moduleTypeIndices.Add((uint)typeEntries.Count);
            typeEntries.Add(moduleType);
        }

        uint extraModules = (uint)extracted.Count;
        uint extraTypes = (uint)typeEntries.Count;

        var w = new CtWriter();
        w.Bytes(component.AsSpan(0, 8));
        w.Section(ComponentSectionId.CoreType, s =>
        {
            s.U32((uint)typeEntries.Count);
            foreach (byte[] entry in typeEntries)
                s.Bytes(entry);
        });
        w.Section(ComponentSectionId.Import, s =>
        {
            s.U32(extraModules);
            for (int i = 0; i < extracted.Count; i++)
            {
                ComponentTypeCodec.WriteExternName(s, $"unbundled-module{i}");
                s.Byte((byte)ComponentExternKind.CoreModule);
                s.Byte((byte)CoreSort.Module);
                s.U32(moduleTypeIndices[i]);
            }
        });

        uint nextExtracted = 0;
        foreach (var (id, start, end, sectionStart) in Sections(component))
        {
            var r = new CtReader(component, start, end);
            switch ((ComponentSectionId)id)
            {
                case ComponentSectionId.CoreModule when end - start > threshold:
                    uint aliased = nextExtracted++;
                    w.Section(ComponentSectionId.Alias, s =>
                    {
                        s.U32(1);
                        s.Byte((byte)ComponentSort.Core);
                        s.Byte((byte)CoreSort.Module);
                        s.Byte((byte)ComponentAliasTarget.Outer);
                        s.U32(0);
                        s.U32(aliased);
                    });
                    break;
                case ComponentSectionId.CoreInstance:
                    w.Section((ComponentSectionId)id, s => RewriteCoreInstances(r, s, extraModules));
                    break;
                case ComponentSectionId.Instance:
                    w.Section((ComponentSectionId)id, s => RewriteInstances(r, s, extraModules));
                    break;
                case ComponentSectionId.Export:
                    w.Section((ComponentSectionId)id, s => RewriteExports(r, s, extraModules));
                    break;
                case ComponentSectionId.Alias:
                    CheckAliases(r, depth: 0);
                    w.Bytes(component.AsSpan(sectionStart, end - sectionStart));
                    break;
                case ComponentSectionId.Import:
                    CheckImports(r);
                    w.Bytes(component.AsSpan(sectionStart, end - sectionStart));
                    break;
                case ComponentSectionId.Custom when r.ReadNameBytes().SequenceEqual("component-name"u8):
                    int payload = r.Position;
                    w.Section(ComponentSectionId.Custom, s =>
                    {
                        s.Name("component-name"u8);
                        RewriteNames(new CtReader(component, payload, end), s, extraModules, extraTypes);
                    });
                    break;
                default:
                    w.Bytes(component.AsSpan(sectionStart, end - sectionStart));
                    break;
            }
        }

        return new UnbundleResult(w.ToArray(), extracted);
    }

    private static void RequireComponent(byte[] component)
    {
        if (component.Length < 8 || component[0] != 0 || component[1] != (byte)'a' || component[2] != (byte)'s' || component[3] != (byte)'m'
            || component[4] != 0x0D || component[5] != 0 || component[6] != 1 || component[7] != 0)
            throw new LinkerException("input is not a WebAssembly component.");
    }

    private static IEnumerable<(byte Id, int Start, int End, int SectionStart)> Sections(byte[] bytes, int start = 8, int? end = null)
    {
        int limit = end ?? bytes.Length;
        var r = new CtReader(bytes, start, limit);
        while (r.HasMore)
        {
            int sectionStart = r.Position;
            byte id = r.ReadByte();
            int size = r.ReadLength();
            int payload = r.Position;
            r.Sub(size);
            yield return (id, payload, payload + size, sectionStart);
        }
    }

    // ── Index rewriting ───────────────────────────────────────────────────────

    private static void RewriteCoreInstances(CtReader r, CtWriter w, uint extraModules)
    {
        uint count = r.ReadU32();
        w.U32(count);
        for (uint i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            w.Byte(kind);
            if (kind == (byte)InstanceExprKind.Instantiate)
            {
                w.U32(r.ReadU32() + extraModules);
                uint args = r.ReadU32();
                w.U32(args);
                for (uint a = 0; a < args; a++)
                {
                    w.Name(r.ReadNameBytes());
                    w.Byte(r.ReadByte());
                    w.U32(r.ReadU32());
                }
            }
            else if (kind == (byte)InstanceExprKind.FromExports)
            {
                uint exports = r.ReadU32();
                w.U32(exports);
                for (uint e = 0; e < exports; e++)
                {
                    w.Name(r.ReadNameBytes());
                    w.Byte(r.ReadByte());
                    w.U32(r.ReadU32());
                }
            }
            else
            {
                throw new FormatException($"Unknown core instance kind 0x{kind:X2}.");
            }
        }
    }

    private static void RewriteInstances(CtReader r, CtWriter w, uint extraModules)
    {
        uint count = r.ReadU32();
        w.U32(count);
        for (uint i = 0; i < count; i++)
        {
            byte kind = r.ReadByte();
            w.Byte(kind);
            if (kind == (byte)InstanceExprKind.Instantiate)
            {
                w.U32(r.ReadU32());
                uint args = r.ReadU32();
                w.U32(args);
                for (uint a = 0; a < args; a++)
                {
                    w.Name(r.ReadNameBytes());
                    RewriteSortIndex(r, w, extraModules);
                }
            }
            else if (kind == (byte)InstanceExprKind.FromExports)
            {
                uint exports = r.ReadU32();
                w.U32(exports);
                for (uint e = 0; e < exports; e++)
                {
                    CopyNameAttributes(r, w);
                    RewriteSortIndex(r, w, extraModules);
                }
            }
            else
            {
                throw new FormatException($"Unknown instance kind 0x{kind:X2}.");
            }
        }
    }

    private static void RewriteExports(CtReader r, CtWriter w, uint extraModules)
    {
        uint count = r.ReadU32();
        w.U32(count);
        for (uint i = 0; i < count; i++)
        {
            CopyNameAttributes(r, w);
            RewriteSortIndex(r, w, extraModules);
            byte hasType = r.ReadByte();
            w.Byte(hasType);
            if (hasType == 0x01)
            {
                CtExternDesc desc = ComponentTypeCodec.ReadExternDesc(r);
                if (desc.Kind == ComponentExternKind.CoreModule)
                    throw new NotSupportedException("unbundling a component that ascribes a core module type to an export is not supported.");
                ComponentTypeCodec.WriteExternDesc(w, desc);
            }
        }
    }

    // The component name section names core modules and core types by index, so those shift too.
    private static void RewriteNames(CtReader r, CtWriter w, uint extraModules, uint extraTypes)
    {
        while (r.HasMore)
        {
            byte id = r.ReadByte();
            CtReader sub = r.Sub(r.ReadLength());
            int start = sub.Position;
            if (id != 0x01)
            {
                sub.Position = sub.End;
                byte[] raw = sub.Slice(start);
                w.Byte(id);
                w.U32((uint)raw.Length);
                w.Bytes(raw);
                continue;
            }

            CtSort sort = ComponentTypeCodec.ReadSort(sub);
            uint shift = sort.Kind == ComponentSort.Core ? sort.CoreSort switch { CoreSort.Module => extraModules, CoreSort.Type => extraTypes, _ => 0u } : 0u;
            var body = new CtWriter();
            ComponentTypeCodec.WriteSort(body, sort);
            uint count = sub.ReadU32();
            body.U32(count);
            for (uint i = 0; i < count; i++)
            {
                body.U32(sub.ReadU32() + shift);
                body.Name(sub.ReadNameBytes());
            }

            w.Byte(id);
            w.U32((uint)body.Length);
            w.Bytes(body.ToArray());
        }
    }

    private static void RewriteSortIndex(CtReader r, CtWriter w, uint extraModules)
    {
        CtSort sort = ComponentTypeCodec.ReadSort(r);
        uint index = r.ReadU32();
        if (sort.Kind == ComponentSort.Core && sort.CoreSort == CoreSort.Module)
            index += extraModules;
        else if (sort.Kind == ComponentSort.Core && sort.CoreSort == CoreSort.Type)
            throw new NotSupportedException("unbundling a component that passes core types between instances is not supported.");
        ComponentTypeCodec.WriteSort(w, sort);
        w.U32(index);
    }

    private static void CopyNameAttributes(CtReader r, CtWriter w)
    {
        int start = r.Position;
        ComponentTypeCodec.SkipExternName(r);
        w.Bytes(r.SpanFrom(start));
    }

    private static void CheckImports(CtReader r)
    {
        uint count = r.ReadU32();
        for (uint i = 0; i < count; i++)
        {
            ComponentTypeCodec.SkipExternName(r);
            if (ComponentTypeCodec.ReadExternDesc(r).Kind == ComponentExternKind.CoreModule)
                throw new NotSupportedException("unbundling a component that imports core modules is not supported.");
        }
    }

    // An outer alias at `depth` with count == depth names a top-level item, whose index would shift.
    private static void CheckAliases(CtReader r, uint depth)
    {
        uint count = r.ReadU32();
        for (uint i = 0; i < count; i++)
        {
            CtAliasDecl alias = ComponentTypeCodec.ReadAlias(r);
            if (alias is { Sort.Kind: ComponentSort.Core, Sort.CoreSort: CoreSort.Module or CoreSort.Type, Target: CtAliasOuter outer } && outer.Count == depth)
                throw new NotSupportedException("unbundling a component with outer aliases of top-level core modules or core types is not supported.");
        }
    }

    private static void CheckNested(byte[] bytes, int start, int end, uint depth, int threshold)
    {
        foreach (var (id, s, e, _) in Sections(bytes, start + 8, end))
        {
            switch ((ComponentSectionId)id)
            {
                case ComponentSectionId.CoreModule when e - s > threshold:
                    throw new NotSupportedException("unbundling core modules embedded in nested components is not supported.");
                case ComponentSectionId.Component:
                    CheckNested(bytes, s, e, depth + 1, threshold);
                    break;
                case ComponentSectionId.Alias:
                    CheckAliases(new CtReader(bytes, s, e), depth);
                    break;
            }
        }
    }

    // ── Module types ──────────────────────────────────────────────────────────

    /// <summary>
    /// The module type of <paramref name="module"/>: its imports, then its exports, each function or tag type aliased
    /// from the component's core type section the first time the module type uses it.
    /// </summary>
    private static byte[] ModuleType(byte[] module, Dictionary<string, uint> funcTypes, List<byte[]> typeEntries)
    {
        var info = ModuleInfo.Read(module);
        var decls = new List<byte[]>();
        var local = new Dictionary<int, uint>();
        uint localTypes = 0;

        uint TypeIndex(int moduleType)
        {
            if (local.TryGetValue(moduleType, out uint index))
                return index;
            byte[] entry = info.Types[moduleType];
            string key = Convert.ToHexString(entry);
            if (!funcTypes.TryGetValue(key, out uint outer))
            {
                outer = funcTypes[key] = (uint)typeEntries.Count;
                typeEntries.Add(entry);
            }

            var alias = new CtWriter();
            alias.Byte((byte)CoreModuleDeclKind.Alias);
            alias.Byte((byte)CoreSort.Type);
            alias.Byte(0x01); // core:aliastarget outer
            alias.U32(1);
            alias.U32(outer);
            decls.Add(alias.ToArray());
            return local[moduleType] = localTypes++;
        }

        byte[] EntityType(ModuleInfo.Entity entity)
        {
            var w = new CtWriter();
            w.Byte(entity.Kind);
            switch ((CoreSort)entity.Kind)
            {
                case CoreSort.Func:
                    w.U32(TypeIndex(entity.TypeIndex));
                    break;
                case CoreSort.Tag:
                    w.Byte(0x00);
                    w.U32(TypeIndex(entity.TypeIndex));
                    break;
                default:
                    w.Bytes(entity.Raw);
                    break;
            }

            return w.ToArray();
        }

        foreach (var (moduleName, field, entity) in info.Imports)
        {
            byte[] type = EntityType(entity);
            var d = new CtWriter();
            d.Byte((byte)CoreModuleDeclKind.Import);
            d.Name(moduleName.Span);
            d.Name(field.Span);
            d.Bytes(type);
            decls.Add(d.ToArray());
        }

        foreach (var (name, entity) in info.Exports)
        {
            byte[] type = EntityType(entity);
            var d = new CtWriter();
            d.Byte((byte)CoreModuleDeclKind.Export);
            d.Name(name.Span);
            d.Bytes(type);
            decls.Add(d.ToArray());
        }

        var result = new CtWriter();
        result.Byte(0x50);
        result.U32((uint)decls.Count);
        foreach (byte[] decl in decls)
            result.Bytes(decl);
        return result.ToArray();
    }

    /// <summary>The entity types of a core module's imports and exports, with raw encodings of non-function types.</summary>
    private sealed class ModuleInfo
    {
        public sealed record Entity(byte Kind, int TypeIndex, byte[] Raw);

        public List<byte[]> Types { get; } = [];
        public List<(ReadOnlyMemory<byte> Module, ReadOnlyMemory<byte> Field, Entity Entity)> Imports { get; } = [];
        public List<(ReadOnlyMemory<byte> Name, Entity Entity)> Exports { get; } = [];

        public static ModuleInfo Read(byte[] module)
        {
            if (module.Length < 8 || module[4] != 1)
                throw new LinkerException("an embedded core module is not a WebAssembly module.");
            var info = new ModuleInfo();
            var funcs = new List<int>();
            var tables = new List<byte[]>();
            var memories = new List<byte[]>();
            var globals = new List<byte[]>();
            var tags = new List<int>();
            var exports = new List<(ReadOnlyMemory<byte> Name, byte Kind, uint Index)>();
            foreach (var (id, start, end, _) in Sections(module))
            {
                var r = new CtReader(module, start, end);
                switch ((WasmSectionId)id)
                {
                    case WasmSectionId.Type:
                        uint typeCount = r.ReadU32();
                        for (uint i = 0; i < typeCount; i++)
                        {
                            int typeStart = r.Position;
                            if (r.ReadByte() != (byte)CompositeTypeCode.Func)
                                throw new NotSupportedException("core modules with GC or recursive types are not supported (unbundle, replace-module).");
                            SkipValTypes(r);
                            SkipValTypes(r);
                            info.Types.Add(r.Slice(typeStart));
                        }

                        break;
                    case WasmSectionId.Import:
                        uint importCount = r.ReadU32();
                        for (uint i = 0; i < importCount; i++)
                        {
                            ReadOnlyMemory<byte> moduleName = r.ReadNameMemory();
                            ReadOnlyMemory<byte> field = r.ReadNameMemory();
                            byte kind = r.ReadByte();
                            int rawStart = r.Position;
                            Entity entity;
                            switch ((CoreSort)kind)
                            {
                                case CoreSort.Func:
                                    entity = new Entity(kind, checked((int)r.ReadU32()), []);
                                    funcs.Add(entity.TypeIndex);
                                    break;
                                case CoreSort.Table:
                                    SkipTableType(r);
                                    entity = new Entity(kind, 0, r.Slice(rawStart));
                                    tables.Add(entity.Raw);
                                    break;
                                case CoreSort.Memory:
                                    SkipLimits(r);
                                    entity = new Entity(kind, 0, r.Slice(rawStart));
                                    memories.Add(entity.Raw);
                                    break;
                                case CoreSort.Global:
                                    SkipValType(r);
                                    r.ReadByte();
                                    entity = new Entity(kind, 0, r.Slice(rawStart));
                                    globals.Add(entity.Raw);
                                    break;
                                case CoreSort.Tag:
                                    r.ReadByte();
                                    entity = new Entity(kind, checked((int)r.ReadU32()), []);
                                    tags.Add(entity.TypeIndex);
                                    break;
                                default:
                                    throw new FormatException($"Unknown import kind 0x{kind:X2}.");
                            }

                            info.Imports.Add((moduleName, field, entity));
                        }

                        break;
                    case WasmSectionId.Function:
                        uint funcCount = r.ReadU32();
                        for (uint i = 0; i < funcCount; i++)
                            funcs.Add(checked((int)r.ReadU32()));
                        break;
                    case WasmSectionId.Table:
                        uint tableCount = r.ReadU32();
                        for (uint i = 0; i < tableCount; i++)
                        {
                            if (r.Position < r.End && module[r.Position] == 0x40)
                                throw new NotSupportedException("core modules with initialized tables are not supported (unbundle, replace-module).");
                            int tableStart = r.Position;
                            SkipTableType(r);
                            tables.Add(r.Slice(tableStart));
                        }

                        break;
                    case WasmSectionId.Memory:
                        uint memoryCount = r.ReadU32();
                        for (uint i = 0; i < memoryCount; i++)
                        {
                            int memoryStart = r.Position;
                            SkipLimits(r);
                            memories.Add(r.Slice(memoryStart));
                        }

                        break;
                    case WasmSectionId.Global:
                        uint globalCount = r.ReadU32();
                        for (uint i = 0; i < globalCount; i++)
                        {
                            int globalStart = r.Position;
                            SkipValType(r);
                            r.ReadByte();
                            globals.Add(r.Slice(globalStart));
                            SkipConstExpr(module, r);
                        }

                        break;
                    case WasmSectionId.Export:
                        uint exportCount = r.ReadU32();
                        for (uint i = 0; i < exportCount; i++)
                            exports.Add((r.ReadNameMemory(), r.ReadByte(), r.ReadU32()));
                        break;
                    case WasmSectionId.Tag:
                        uint tagCount = r.ReadU32();
                        for (uint i = 0; i < tagCount; i++)
                        {
                            r.ReadByte();
                            tags.Add(checked((int)r.ReadU32()));
                        }

                        break;
                }
            }

            foreach (var (name, kind, index) in exports)
            {
                info.Exports.Add((name, (CoreSort)kind switch
                {
                    CoreSort.Func => new Entity(kind, funcs[(int)index], []),
                    CoreSort.Table => new Entity(kind, 0, tables[(int)index]),
                    CoreSort.Memory => new Entity(kind, 0, memories[(int)index]),
                    CoreSort.Global => new Entity(kind, 0, globals[(int)index]),
                    CoreSort.Tag => new Entity(kind, tags[(int)index], []),
                    _ => throw new FormatException($"Unknown export kind 0x{kind:X2}."),
                }));
            }

            return info;
        }

        private static void SkipValTypes(CtReader r)
        {
            uint count = r.ReadU32();
            for (uint i = 0; i < count; i++)
                SkipValType(r);
        }

        // Types are copied into the component as raw bytes, which is exact for abstract heap types such as binaryen's
        // (ref exn); a concrete (ref $t) names a module type index the component's core type space does not share.
        private static void SkipValType(CtReader r)
        {
            byte b = r.ReadByte();
            if (b is 0x63 or 0x64 && r.ReadS33() >= 0)
                throw new NotSupportedException("core modules with concrete typed references, (ref $t), are not supported (unbundle, replace-module).");
        }

        private static void SkipTableType(CtReader r)
        {
            SkipValType(r);
            SkipLimits(r);
        }

        private static void SkipLimits(CtReader r)
        {
            byte flags = r.ReadByte();
            if ((flags & 0x04) != 0)
            {
                r.SkipLeb128();
                if ((flags & 0x01) != 0)
                    r.SkipLeb128();
            }
            else
            {
                r.ReadU32();
                if ((flags & 0x01) != 0)
                    r.ReadU32();
            }

            if ((flags & 0x08) != 0)
                r.ReadU32();
        }

        private static void SkipConstExpr(byte[] module, CtReader r)
        {
            var cursor = new InstructionCursor(module.AsSpan(r.Position, r.End - r.Position), WasmFeatures.All);
            while (cursor.MoveNext(out DecodedInstruction instruction))
            {
                if (!instruction.OpcodeId.IsPrefixed && instruction.OpcodeId.Opcode == WasmOpcode.End)
                {
                    r.Position += cursor.Position;
                    return;
                }
            }

            throw new FormatException("unterminated constant expression.");
        }
    }
}
