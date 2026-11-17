// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using System.Text;

/// <summary>Options for <see cref="ComponentEncoder"/>.</summary>
public sealed class ComponentEncoderOptions
{
    /// <summary>
    /// Additional <c>component-type</c> payloads (component binaries encoding a WIT world, as produced by
    /// <c>wasm-tools component embed</c>), merged with those embedded in the core module.
    /// </summary>
    public IReadOnlyList<byte[]> ComponentTypes { get; init; } = [];

    /// <summary>Unify interface imports whose versions are semver-compatible (wasm-component-ld's default).</summary>
    public bool MergeImportsBasedOnSemver { get; init; } = true;

    /// <summary>
    /// Adapter modules (such as wasmtime's <c>wasi_snapshot_preview1.command.wasm</c>) that implement the core
    /// imports from the module named <see cref="ComponentAdapter.Name"/> on top of the component model. As with
    /// wit-component, an adapter is used only when the main module imports from it or its world exports functions.
    /// </summary>
    public IReadOnlyList<ComponentAdapter> Adapters { get; init; } = [];
}

/// <summary>An adapter module and the core module name its exports are imported under.</summary>
public sealed record ComponentAdapter(string Name, byte[] Module);

/// <summary>
/// Wraps a core WebAssembly module into a component, as <c>wasm-tools component new</c> (wit-component) does:
/// the world is read from the module's <c>component-type</c> custom sections, each core import of an interface
/// function is lowered with <c>canon lower</c> (through a shim table when it needs the module's own memory or
/// <c>cabi_realloc</c>), and each exported interface function is lifted with <c>canon lift</c>.
/// </summary>
/// <remarks>
/// Imports from an adapter module (<see cref="ComponentEncoderOptions.Adapters"/>, such as the
/// <c>wasi_snapshot_preview1</c> adapter) are satisfied by the adapter, reduced by <see cref="AdapterModule"/> to
/// what is used. Only what the WASI toolchains produce is supported: interface imports and exports (no world-level
/// functions or types) and imported resources (no resources defined by the component). Anything else is rejected
/// with a <see cref="LinkerException"/> or <see cref="NotSupportedException"/>.
/// </remarks>
public static class ComponentEncoder
{
    private const string ComponentTypeSection = "component-type";

    /// <summary>Returns whether <paramref name="module"/> carries a <c>component-type</c> custom section.</summary>
    public static bool HasComponentType(byte[] module) =>
        CoreModule.Parse(module).Customs.Any(c => IsComponentType(c.Name));

    public static byte[] Encode(byte[] coreModule, ComponentEncoderOptions? options = null)
    {
        options ??= new ComponentEncoderOptions();
        CoreModule core = CoreModule.Parse(coreModule);
        var world = new WitWorld();
        foreach (var custom in core.Customs.Where(c => IsComponentType(c.Name)))
            world.Add(coreModule[custom.PayloadStart..custom.End], options.MergeImportsBasedOnSemver);
        foreach (byte[] extra in options.ComponentTypes)
            world.Add(extra, options.MergeImportsBasedOnSemver);

        var units = new List<Unit> { new(MainModuleName, CoreModule.Parse(StripComponentTypes(core))) };
        foreach (ComponentAdapter adapter in options.Adapters)
        {
            if (adapter.Name == MainModuleName || units.Any(u => u.Name == adapter.Name))
                throw new LinkerException($"adapter name `{adapter.Name}` is used more than once.");
            CoreModule module = CoreModule.Parse(adapter.Module);
            var payloads = module.Customs.Where(c => IsComponentType(c.Name)).Select(c => adapter.Module[c.PayloadStart..c.End]).ToList();
            var adapterWorld = new WitWorld();
            foreach (byte[] payload in payloads)
                adapterWorld.Add(payload, options.MergeImportsBasedOnSemver);

            var importedFields = core.Imports.Where(i => i.Module == adapter.Name).Select(i => i.Field).ToList();
            var exportedFunctions = module.Exports.Keys.Where(name =>
            {
                int hash = name.IndexOf('#');
                return hash > 0 && adapterWorld.FindExport(WitWorld.ParseName(name[..hash], options.MergeImportsBasedOnSemver).Key) is not null;
            }).ToList();
            if (importedFields.Count == 0 && exportedFunctions.Count == 0)
                continue;

            foreach (byte[] payload in payloads)
                world.Add(payload, options.MergeImportsBasedOnSemver);
            var required = new HashSet<string>(importedFields, StringComparer.Ordinal);
            foreach (string name in exportedFunctions)
            {
                required.Add(name);
                if (module.Exports.ContainsKey("cabi_post_" + name))
                    required.Add("cabi_post_" + name);
            }

            foreach (string realloc in (string[])["cabi_import_realloc", "cabi_export_realloc"])
            {
                if (module.Exports.TryGetValue(realloc, out var e) && e.Kind == 0x00)
                    required.Add(realloc);
            }

            string? mainRealloc = (string[])["cabi_realloc_adapter", "cabi_realloc"] is var names
                ? names.FirstOrDefault(n => core.Exports.TryGetValue(n, out var e) && e.Kind == 0x00)
                : null;
            units.Add(new Unit(adapter.Name, CoreModule.Parse(AdapterModule.Minimize(adapter.Module, required, mainRealloc))));
        }

        return new Builder(units, world, options).Build();
    }

    private const string MainModuleName = "__main_module__";

    private static byte[] StripComponentTypes(CoreModule core)
    {
        var w = new CtWriter();
        w.Bytes(core.Bytes.AsSpan(0, 8));
        foreach (CoreModule.Section section in core.Sections)
        {
            if (section.Id == 0 && IsComponentType(section.Name))
                continue;
            w.Bytes(core.Bytes.AsSpan(section.Start, section.End - section.Start));
        }

        return w.ToArray();
    }

    /// <summary>A core module instantiated by the component: the main module or an adapter.</summary>
    private sealed class Unit(string name, CoreModule module)
    {
        public string Name { get; } = name;
        public CoreModule Module { get; } = module;
        public bool IsMain => Name == MainModuleName;
        public uint ModuleIndex { get; set; }
        public uint Instance { get; set; }
        public Dictionary<bool, uint> Reallocs { get; } = [];
        public override string ToString() => IsMain ? "the main module" : $"adapter `{Name}`";
    }

    /// <summary>
    /// Reads a <c>--component-type</c> input: either a component-type payload itself (a component binary encoding
    /// a world) or a core module carrying <c>component-type</c> custom sections (as written by
    /// <c>wasm-tools component embed --only-custom</c>). WIT text is not supported.
    /// </summary>
    public static IReadOnlyList<byte[]> ReadComponentTypeFile(byte[] bytes)
    {
        if (bytes.Length >= 8 && bytes[0] == 0 && bytes[1] == (byte)'a' && bytes[2] == (byte)'s' && bytes[3] == (byte)'m')
        {
            if (bytes[4] == 0x0D)
                return [bytes];
            CoreModule module = CoreModule.Parse(bytes);
            var payloads = module.Customs.Where(c => IsComponentType(c.Name)).Select(c => bytes[c.PayloadStart..c.End]).ToList();
            if (payloads.Count == 0)
                throw new LinkerException("the component type module has no component-type custom section.");
            return payloads;
        }

        throw new NotSupportedException(
            "WIT text component types are not supported; encode the world first with `wasm-tools component embed --only-custom <wit> --world <world> ...`.");
    }

    private static bool IsComponentType(string name) =>
        name == ComponentTypeSection || name.StartsWith(ComponentTypeSection + ":", StringComparison.Ordinal);

    /// <summary>The parts of a core module the encoder needs: its sections, function types, imports and exports.</summary>
    internal sealed class CoreModule
    {
        public sealed record Section(byte Id, int Start, int PayloadStart, int End, string Name);
        public sealed record FuncType(byte[] Params, byte[] Results)
        {
            public bool Matches(CanonicalAbi.Lowering lowering) =>
                Params.AsSpan().SequenceEqual(lowering.Params) && Results.AsSpan().SequenceEqual(lowering.Results);

            public bool Equals(FuncType? other) =>
                other is not null && Params.AsSpan().SequenceEqual(other.Params) && Results.AsSpan().SequenceEqual(other.Results);

            public override int GetHashCode() => HashCode.Combine(Params.Length, Results.Length, Convert.ToHexString(Params), Convert.ToHexString(Results));

            public override string ToString() =>
                $"(func (param {string.Join(' ', Params.Select(Name))}) (result {string.Join(' ', Results.Select(Name))}))";

            private static string Name(byte b) => b switch
            {
                CanonicalAbi.I32 => "i32",
                CanonicalAbi.I64 => "i64",
                CanonicalAbi.F32 => "f32",
                CanonicalAbi.F64 => "f64",
                _ => $"0x{b:X2}",
            };
        }

        public sealed record Import(string Module, string Field, byte Kind, uint TypeIndex);

        public List<Section> Sections { get; } = [];
        public IEnumerable<Section> Customs => Sections.Where(s => s.Id == 0);
        public List<FuncType> Types { get; } = [];
        public List<Import> Imports { get; } = [];
        public Dictionary<string, (byte Kind, uint Index)> Exports { get; } = new(StringComparer.Ordinal);
        public byte[] Bytes { get; private init; } = [];

        public FuncType FunctionType(uint funcIndex)
        {
            var functionImports = Imports.Where(i => i.Kind == 0x00).ToList();
            if (funcIndex < functionImports.Count)
                return Types[(int)functionImports[(int)funcIndex].TypeIndex];
            Section functions = Sections.FirstOrDefault(s => s.Id == 3)
                ?? throw new LinkerException("module has no function section.");
            var r = new CtReader(Bytes, functions.PayloadStart, functions.End);
            uint count = r.ReadU32();
            uint target = funcIndex - (uint)functionImports.Count;
            for (uint i = 0; i < count; i++)
            {
                uint type = r.ReadU32();
                if (i == target)
                    return Types[(int)type];
            }

            throw new LinkerException($"function index {funcIndex} is out of range.");
        }

        public static CoreModule Parse(byte[] bytes)
        {
            if (bytes.Length < 8 || bytes[0] != 0 || bytes[1] != (byte)'a' || bytes[2] != (byte)'s' || bytes[3] != (byte)'m'
                || bytes[4] != 1 || bytes[5] != 0 || bytes[6] != 0 || bytes[7] != 0)
                throw new LinkerException("input is not a core WebAssembly module.");
            var module = new CoreModule { Bytes = bytes };
            var r = new CtReader(bytes) { Position = 8 };
            while (r.HasMore)
            {
                int start = r.Position;
                byte id = r.ReadByte();
                int size = checked((int)r.ReadU32());
                int payload = r.Position;
                CtReader body = r.Sub(size);
                string name = "";
                switch (id)
                {
                    case 0:
                        name = body.ReadName();
                        payload = body.Position;
                        break;
                    case 1:
                        module.ReadTypes(body);
                        break;
                    case 2:
                        module.ReadImports(body);
                        break;
                    case 7:
                        uint count = body.ReadU32();
                        for (uint i = 0; i < count; i++)
                        {
                            string export = body.ReadName();
                            byte kind = body.ReadByte();
                            module.Exports[export] = (kind, body.ReadU32());
                        }

                        break;
                }

                module.Sections.Add(new Section(id, start, payload, r.Position, name));
            }

            return module;
        }

        private void ReadTypes(CtReader r)
        {
            uint count = r.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                byte form = r.ReadByte();
                if (form != 0x60)
                    throw new NotSupportedException($"Core type form 0x{form:X2} is not supported in a component's main module.");
                Types.Add(new FuncType(ReadValTypes(r), ReadValTypes(r)));
            }
        }

        private static byte[] ReadValTypes(CtReader r)
        {
            uint count = r.ReadU32();
            var types = new byte[count];
            for (uint i = 0; i < count; i++)
            {
                types[i] = r.ReadByte();
                if (types[i] is 0x63 or 0x64)
                    throw new NotSupportedException("Typed function references are not supported in a component's main module.");
            }

            return types;
        }

        private void ReadImports(CtReader r)
        {
            uint count = r.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                string module = r.ReadName();
                string field = r.ReadName();
                byte kind = r.ReadByte();
                uint typeIndex = 0;
                switch (kind)
                {
                    case 0x00: typeIndex = r.ReadU32(); break;
                    case 0x01: r.ReadByte(); SkipLimits(r); break;
                    case 0x02: SkipLimits(r); break;
                    case 0x03: r.ReadByte(); r.ReadByte(); break;
                    case 0x04: r.ReadByte(); r.ReadU32(); break;
                    default: throw new LinkerException($"unknown import kind 0x{kind:X2}.");
                }

                Imports.Add(new Import(module, field, kind, typeIndex));
            }
        }

        private static void SkipLimits(CtReader r)
        {
            byte flags = r.ReadByte();
            r.ReadU32();
            if ((flags & 0x01) != 0)
                r.ReadU32();
        }
    }

    private sealed class Builder(List<Unit> units, WitWorld world, ComponentEncoderOptions options)
    {
        private const string ResourceDropPrefix = "[resource-drop]";

        private readonly CanonicalAbi _abi = new(world);
        private readonly CtWriter _out = new();
        private readonly Dictionary<WitInterface, HashSet<int>> _live = [];
        private readonly Dictionary<WitInterface, uint> _instanceIndex = [];
        private readonly Dictionary<(WitInterface, string), uint> _typeAliases = [];
        private readonly Dictionary<(WitInterface, string), uint> _funcAliases = [];
        private readonly Dictionary<(WitInterface, uint), uint> _liftTypes = [];
        private uint _types, _funcs, _instances, _coreModules, _coreInstances, _coreFuncs, _coreMemories;
        private Unit Main => units[0];
        private WitWorld World => world;

        private enum ImportKind { Lowered, ResourceDrop, FromAdapter, MainExport, Memory }

        private sealed record ImportPlan(Unit Owner, CoreModule.Import Import, ImportKind Kind,
            WitInterface? Interface = null, CtFunc? Func = null, CanonicalAbi.Lowering? Lowering = null, Unit? Adapter = null)
        {
            /// <summary>Lowered through the shim table: the callee needs memory or realloc, or lives in an adapter instantiated later.</summary>
            public bool Indirect => Kind == ImportKind.FromAdapter || Lowering is { Memory: true } or { Realloc: true };
            public CoreModule.FuncType CoreType => Owner.Module.Types[(int)Import.TypeIndex];
        }

        private sealed record ExportPlan(string Name, CtFunc Func, Unit Owner, string CoreName, CanonicalAbi.Lowering Lifting);

        public byte[] Build()
        {
            List<ImportPlan> plans = units.SelectMany(PlanImports).ToList();
            var exports = PlanExports();
            ComputeLiveness(plans, exports);

            _out.Bytes([0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);
            var emitted = new HashSet<WitInterface>();
            foreach (WitInterface iface in world.Imports.Where(_live.ContainsKey))
                EmitImport(iface, emitted);

            foreach (Unit unit in units)
            {
                unit.ModuleIndex = _coreModules++;
                _out.Section(1, w => w.Bytes(unit.Module.Bytes));
            }

            EmitCoreImportsAndInstantiate(plans);
            EmitExports(exports);
            return _out.ToArray();
        }

        // ── Planning ──────────────────────────────────────────────────────────

        private List<ImportPlan> PlanImports(Unit unit)
        {
            var plans = new List<ImportPlan>();
            foreach (CoreModule.Import import in unit.Module.Imports)
            {
                if (!unit.IsMain && import.Module == "env" && import.Kind == 0x02 && import.Field == "memory")
                {
                    plans.Add(new ImportPlan(unit, import, ImportKind.Memory));
                    continue;
                }

                if (import.Kind != 0x00)
                    throw new LinkerException(
                        $"{unit} imports a non-function `{import.Module}::{import.Field}`; components cannot import core memories, tables or globals.");
                if (!unit.IsMain && import.Module == MainModuleName)
                {
                    if (!Main.Module.Exports.TryGetValue(import.Field, out var export) || export.Kind != 0x00)
                        throw new LinkerException($"{unit} requires the main module to export function `{import.Field}`.");
                    CheckSignature(unit, import, Main.Module.FunctionType(export.Index), "the main module's export");
                    plans.Add(new ImportPlan(unit, import, ImportKind.MainExport));
                    continue;
                }

                if (units.FirstOrDefault(u => !u.IsMain && u.Name == import.Module) is { } adapter)
                {
                    if (!unit.IsMain)
                        throw new NotSupportedException($"{unit} imports from adapter `{adapter.Name}`; adapters importing adapters are not supported.");
                    if (!adapter.Module.Exports.TryGetValue(import.Field, out var export) || export.Kind != 0x00)
                        throw new LinkerException($"adapter `{adapter.Name}` does not export function `{import.Field}`.");
                    CheckSignature(unit, import, adapter.Module.FunctionType(export.Index), "the adapter's export");
                    plans.Add(new ImportPlan(unit, import, ImportKind.FromAdapter, Adapter: adapter));
                    continue;
                }

                if (import.Module == "wasi_snapshot_preview1")
                    throw new NotSupportedException(
                        $"`wasi_snapshot_preview1::{import.Field}` needs the WASI preview1 adapter; pass one (e.g. `--adapt wasi_snapshot_preview1=wasi_snapshot_preview1.command.wasm`).");
                var (key, _) = WitWorld.ParseName(import.Module, options.MergeImportsBasedOnSemver);
                WitInterface iface = world.FindImport(key)
                    ?? throw new LinkerException(
                        $"{unit} requires an import interface named `{import.Module}` (for `{import.Field}`), which the world does not import.");
                if (import.Field.StartsWith(ResourceDropPrefix, StringComparison.Ordinal))
                {
                    string resource = import.Field[ResourceDropPrefix.Length..];
                    if (!iface.Exports.TryGetValue(resource, out int d) || iface.Decls[d] is not CtExportDecl { Desc.Kind: CtExternDesc.Type })
                        throw new LinkerException($"interface `{iface.Name}` has no resource `{resource}` for `{import.Field}`.");
                    CheckSignature(unit, import, new CoreModule.FuncType([CanonicalAbi.I32], []), "the canonical ABI lowering");
                    plans.Add(new ImportPlan(unit, import, ImportKind.ResourceDrop, iface));
                    continue;
                }

                CtFunc func = iface.FindFunction(import.Field, out _)
                    ?? throw new LinkerException($"interface `{iface.Name}` has no function `{import.Field}`.");
                if (func.Async)
                    throw new NotSupportedException($"async function `{iface.Name}#{import.Field}` is not supported.");
                CanonicalAbi.Lowering lowering = _abi.Flatten(iface, func, lift: false);
                CheckSignature(unit, import, new CoreModule.FuncType(lowering.Params, lowering.Results), "the canonical ABI lowering");
                plans.Add(new ImportPlan(unit, import, ImportKind.Lowered, iface, func, lowering));
            }

            return plans;
        }

        private static void CheckSignature(Unit unit, CoreModule.Import import, CoreModule.FuncType expected, string what)
        {
            CoreModule.FuncType actual = unit.Module.Types[(int)import.TypeIndex];
            if (!actual.Equals(expected))
                throw new LinkerException(
                    $"type mismatch for function `{import.Field}` imported from `{import.Module}`: {unit} declares {actual} but {what} is {expected}.");
        }

        private List<(WitInterface Interface, List<ExportPlan> Funcs)> PlanExports()
        {
            var plans = new List<(WitInterface, List<ExportPlan>)>();
            var byKey = new Dictionary<string, (Unit Unit, string Name)>(StringComparer.Ordinal);
            foreach (Unit unit in units)
            {
                foreach (string name in unit.Module.Exports.Keys)
                {
                    int hash = name.IndexOf('#');
                    if (hash <= 0)
                        continue;
                    string key = $"{WitWorld.ParseName(name[..hash], options.MergeImportsBasedOnSemver).Key}#{name[(hash + 1)..]}";
                    if (byKey.TryGetValue(key, out var other))
                        throw new LinkerException($"`{name}` is exported by both {other.Unit} and {unit}.");
                    byKey[key] = (unit, name);
                }
            }

            foreach (WitInterface iface in world.Exports)
            {
                var funcs = new List<ExportPlan>();
                foreach (CtDecl decl in iface.Decls)
                {
                    if (decl is not CtExportDecl export)
                        continue;
                    if (export.Desc.Kind == CtExternDesc.Type && export.Desc.SubResource)
                        throw new NotSupportedException($"exported interface `{iface.Name}` defines resource `{export.Name}`; exported resources are not supported yet.");
                    if (export.Desc.Kind != CtExternDesc.Func)
                        continue;
                    CtFunc func = iface.FindFunction(export.Name, out _)
                        ?? throw new LinkerException($"`{iface.Name}#{export.Name}` is not a function type.");
                    if (func.Async)
                        throw new NotSupportedException($"async export `{iface.Name}#{export.Name}` is not supported.");
                    if (!byKey.TryGetValue($"{iface.Key}#{export.Name}", out var owner))
                        throw new LinkerException($"module does not export required function `{iface.Name}#{export.Name}`.");
                    CanonicalAbi.Lowering lifting = _abi.Flatten(iface, func, lift: true);
                    var (kind, index) = owner.Unit.Module.Exports[owner.Name];
                    CoreModule.FuncType actual = kind == 0x00 ? owner.Unit.Module.FunctionType(index) : throw new LinkerException($"export `{owner.Name}` is not a function.");
                    if (!actual.Matches(lifting))
                        throw new LinkerException(
                            $"type mismatch for exported function `{owner.Name}`: {owner.Unit} declares {actual} but the canonical ABI lifting is {new CoreModule.FuncType(lifting.Params, lifting.Results)}.");
                    funcs.Add(new ExportPlan(export.Name, func, owner.Unit, owner.Name, lifting));
                }

                plans.Add((iface, funcs));
            }

            return plans;
        }

        // ── Liveness: keep only the interfaces, functions and types the module uses ──

        private void ComputeLiveness(List<ImportPlan> imports, List<(WitInterface Interface, List<ExportPlan> Funcs)> exports)
        {
            var work = new Stack<(WitInterface, int)>();
            void MarkDecl(WitInterface iface, int decl)
            {
                if (!_live.TryGetValue(iface, out var set))
                    _live[iface] = set = [];
                if (set.Add(decl))
                    work.Push((iface, decl));
            }

            void MarkType(WitInterface iface, uint index) => MarkDecl(iface, iface.TypeDecls[index]);

            foreach (ImportPlan plan in imports)
            {
                if (plan.Interface is not { } iface)
                    continue;
                string name = plan.Kind == ImportKind.ResourceDrop ? plan.Import.Field[ResourceDropPrefix.Length..] : plan.Import.Field;
                MarkDecl(iface, iface.Exports[name]);
            }

            foreach (var (iface, _) in exports)
            {
                for (int i = 0; i < iface.Decls.Count; i++)
                    MarkDecl(iface, i);
            }

            while (work.Count > 0)
            {
                var (iface, index) = work.Pop();
                switch (iface.Decls[index])
                {
                    case CtTypeDecl t:
                        foreach (uint referenced in ComponentTypeCodec.ReferencedTypes(t.Type))
                            MarkType(iface, referenced);
                        break;
                    case CtAliasDecl { Target: CtAliasSymbol symbol }:
                        WitInterface target = world.Lookup(symbol);
                        MarkDecl(target, target.Exports[symbol.Name]);
                        break;
                    case CtExportDecl { Desc.Kind: CtExternDesc.Type, Desc.SubResource: false } e:
                        MarkType(iface, e.Desc.Index);
                        break;
                    case CtExportDecl { Desc.Kind: CtExternDesc.Func } e:
                        MarkType(iface, e.Desc.Index);
                        break;
                }
            }
        }

        // ── Imports ───────────────────────────────────────────────────────────

        private void EmitImport(WitInterface iface, HashSet<WitInterface> emitted)
        {
            if (!emitted.Add(iface))
                return;
            List<int> live = [.. _live[iface].Order()];
            foreach (int d in live)
            {
                if (iface.Decls[d] is CtAliasDecl { Target: CtAliasSymbol symbol })
                {
                    WitInterface dependency = world.Lookup(symbol);
                    if (dependency.IsExport)
                        throw new NotSupportedException($"imported interface `{iface.Name}` uses types from exported interface `{dependency.Name}`.");
                    EmitImport(dependency, emitted);
                }
            }

            foreach (int d in live)
            {
                if (iface.Decls[d] is CtAliasDecl { Target: CtAliasSymbol symbol })
                    TypeAlias(world.Lookup(symbol), symbol.Name);
            }

            // Rebuild the instance type with only its live declarations, renumbering its local type indices.
            var remap = new Dictionary<uint, uint>();
            var decls = new List<CtDecl>();
            uint oldIndex = 0, newIndex = 0;
            for (int d = 0; d < iface.Decls.Count; d++)
            {
                CtDecl decl = iface.Decls[d];
                bool definesType = ComponentTypeCodec.DefinesType(decl);
                if (!live.Contains(d))
                {
                    if (definesType)
                        oldIndex++;
                    continue;
                }

                uint Map(uint i) => remap.TryGetValue(i, out uint m) ? m : throw new LinkerException($"interface `{iface.Name}` refers to a dead type {i}.");
                decls.Add(decl switch
                {
                    CtTypeDecl t => new CtTypeDecl(ComponentTypeCodec.RemapTypes(t.Type, Map)),
                    CtAliasDecl { Target: CtAliasSymbol symbol } => new CtAliasDecl(CtSort.TypeSort, new CtAliasOuter(1, _typeAliases[(world.Lookup(symbol), symbol.Name)])),
                    CtExportDecl { Desc.Kind: CtExternDesc.Type or CtExternDesc.Func, Desc.SubResource: false } e => e with { Desc = e.Desc with { Index = Map(e.Desc.Index) } },
                    _ => decl,
                });
                if (definesType)
                    remap[oldIndex++] = newIndex++;
            }

            uint type = _types++;
            _out.Section(7, w =>
            {
                w.U32(1);
                ComponentTypeCodec.WriteDefType(w, new CtInstanceType(decls));
            });
            _out.Section(10, w =>
            {
                w.U32(1);
                ComponentTypeCodec.WriteExternName(w, iface.Name);
                ComponentTypeCodec.WriteExternDesc(w, new CtExternDesc(CtExternDesc.Instance, type));
            });
            _instanceIndex[iface] = _instances++;
        }

        private uint TypeAlias(WitInterface iface, string name)
        {
            if (_typeAliases.TryGetValue((iface, name), out uint index))
                return index;
            index = _typeAliases[(iface, name)] = _types++;
            EmitAlias(new CtAliasDecl(CtSort.TypeSort, new CtAliasExport(_instanceIndex[iface], name)));
            return index;
        }

        private uint FuncAlias(WitInterface iface, string name)
        {
            if (_funcAliases.TryGetValue((iface, name), out uint index))
                return index;
            index = _funcAliases[(iface, name)] = _funcs++;
            EmitAlias(new CtAliasDecl(new CtSort(CtSort.Func), new CtAliasExport(_instanceIndex[iface], name)));
            return index;
        }

        private uint CoreExportAlias(uint instance, string name, byte coreSort)
        {
            EmitAlias(new CtAliasDecl(new CtSort(CtSort.Core, coreSort), new CtAliasCoreExport(instance, name)));
            return coreSort switch
            {
                CtSort.CoreFunc => _coreFuncs++,
                CtSort.CoreMemory => _coreMemories++,
                _ => throw new InvalidOperationException(),
            };
        }

        private void EmitAlias(CtAliasDecl alias) => _out.Section(6, w =>
        {
            w.U32(1);
            ComponentTypeCodec.WriteAlias(w, alias);
        });

        // ── Core instances: lower the imports, instantiate the main module, patch the shim table ──

        private void EmitCoreImportsAndInstantiate(List<ImportPlan> plans)
        {
            List<ImportPlan> indirect = plans.Where(p => p.Indirect).ToList();
            uint shimInstance = 0;
            var shimTypes = new List<CoreModule.FuncType>();
            if (indirect.Count > 0)
            {
                foreach (ImportPlan p in indirect)
                {
                    if (!shimTypes.Contains(p.CoreType))
                        shimTypes.Add(p.CoreType);
                }

                uint shimModule = _coreModules++;
                byte[] shim = BuildShimModule(indirect, shimTypes);
                _out.Section(1, w => w.Bytes(shim));
                shimInstance = _coreInstances++;
                _out.Section(2, w =>
                {
                    w.U32(1);
                    w.Byte(0x00);
                    w.U32(shimModule);
                    w.U32(0);
                });
            }

            foreach (Unit unit in units)
            {
                var moduleOrder = new List<string>();
                foreach (ImportPlan p in plans.Where(p => p.Owner == unit))
                {
                    if (!moduleOrder.Contains(p.Import.Module))
                        moduleOrder.Add(p.Import.Module);
                }

                var instanceArgs = new List<(string Name, uint Instance)>();
                foreach (string module in moduleOrder)
                {
                    var exports = new List<(string Name, byte Sort, uint Index)>();
                    foreach (ImportPlan p in plans.Where(p => p.Owner == unit && p.Import.Module == module))
                    {
                        switch (p.Kind)
                        {
                            case ImportKind.Memory:
                                exports.Add((p.Import.Field, CtSort.CoreMemory, MainMemory()));
                                continue;
                            case ImportKind.MainExport:
                                exports.Add((p.Import.Field, CtSort.CoreFunc, CoreExportAlias(Main.Instance, p.Import.Field, CtSort.CoreFunc)));
                                continue;
                            case ImportKind.ResourceDrop:
                                uint resource = TypeAlias(p.Interface!, p.Import.Field[ResourceDropPrefix.Length..]);
                                exports.Add((p.Import.Field, CtSort.CoreFunc, Canon(w =>
                                {
                                    w.Byte(0x03);
                                    w.U32(resource);
                                })));
                                continue;
                        }

                        if (p.Indirect)
                        {
                            exports.Add((p.Import.Field, CtSort.CoreFunc,
                                CoreExportAlias(shimInstance, indirect.IndexOf(p).ToString(System.Globalization.CultureInfo.InvariantCulture), CtSort.CoreFunc)));
                            continue;
                        }

                        uint target = FuncAlias(p.Interface!, p.Import.Field);
                        exports.Add((p.Import.Field, CtSort.CoreFunc, Canon(w =>
                        {
                            w.Byte(0x01);
                            w.Byte(0x00);
                            w.U32(target);
                            WriteOptions(w, p.Lowering!, 0, null, null);
                        })));
                    }

                    instanceArgs.Add((module, CoreInstanceFromExports(exports)));
                }

                unit.Instance = _coreInstances++;
                _out.Section(2, w =>
                {
                    w.U32(1);
                    w.Byte(0x00);
                    w.U32(unit.ModuleIndex);
                    w.U32((uint)instanceArgs.Count);
                    foreach (var (name, instance) in instanceArgs)
                    {
                        w.Name(name);
                        w.Byte(0x12);
                        w.U32(instance);
                    }
                });
            }

            if (indirect.Count == 0)
                return;

            var actualFuncs = new List<(string, byte, uint)>();
            for (int i = 0; i < indirect.Count; i++)
            {
                ImportPlan p = indirect[i];
                uint func;
                if (p.Kind == ImportKind.FromAdapter)
                {
                    func = CoreExportAlias(p.Adapter!.Instance, p.Import.Field, CtSort.CoreFunc);
                }
                else
                {
                    uint memory = MainMemory();
                    uint target = FuncAlias(p.Interface!, p.Import.Field);
                    uint? realloc = p.Lowering!.Realloc ? Realloc(p.Owner, import: true) : null;
                    func = Canon(w =>
                    {
                        w.Byte(0x01);
                        w.Byte(0x00);
                        w.U32(target);
                        WriteOptions(w, p.Lowering, memory, realloc, null);
                    });
                }

                actualFuncs.Add((i.ToString(System.Globalization.CultureInfo.InvariantCulture), CtSort.CoreFunc, func));
            }

            uint actual = CoreInstanceFromExports(actualFuncs);
            uint fixupModule = _coreModules++;
            byte[] fixup = BuildFixupModule(indirect, shimTypes);
            _out.Section(1, w => w.Bytes(fixup));
            _coreInstances++;
            _out.Section(2, w =>
            {
                w.U32(1);
                w.Byte(0x00);
                w.U32(fixupModule);
                w.U32(2);
                w.Name("actual");
                w.Byte(0x12);
                w.U32(actual);
                w.Name("shim");
                w.Byte(0x12);
                w.U32(shimInstance);
            });
        }

        private uint? _memory;

        private uint MainMemory()
        {
            if (_memory is { } m)
                return m;
            if (!Main.Module.Exports.TryGetValue("memory", out var export) || export.Kind != 0x02)
                throw new LinkerException("module does not export a memory named `memory`, which the canonical ABI needs.");
            _memory = CoreExportAlias(Main.Instance, "memory", CtSort.CoreMemory);
            return _memory.Value;
        }

        /// <summary>The realloc of <paramref name="unit"/> used to lower imports into it or to lift its exports.</summary>
        private uint Realloc(Unit unit, bool import)
        {
            if (unit.Reallocs.TryGetValue(import, out uint r))
                return r;
            string specific = import ? "cabi_import_realloc" : "cabi_export_realloc";
            string name = unit.Module.Exports.ContainsKey(specific) ? specific : "cabi_realloc";
            if (!unit.Module.Exports.TryGetValue(name, out var export) || export.Kind != 0x00)
                throw new LinkerException($"{unit} does not export `cabi_realloc`, which the canonical ABI needs.");
            return unit.Reallocs[import] = CoreExportAlias(unit.Instance, name, CtSort.CoreFunc);
        }

        private void WriteOptions(CtWriter w, CanonicalAbi.Lowering lowering, uint memory, uint? realloc, uint? postReturn)
        {
            var opts = new List<(byte, uint?)>();
            if (lowering.Strings)
                opts.Add((world.StringEncoding switch { 1 => (byte)0x01, 2 => (byte)0x02, _ => (byte)0x00 }, null));
            if (lowering.Memory)
                opts.Add((0x03, memory));
            if (realloc is { } r)
                opts.Add((0x04, r));
            if (postReturn is { } p)
                opts.Add((0x05, p));
            w.U32((uint)opts.Count);
            foreach (var (code, value) in opts)
            {
                w.Byte(code);
                if (value is { } v)
                    w.U32(v);
            }
        }

        private uint Canon(Action<CtWriter> body)
        {
            _out.Section(8, w =>
            {
                w.U32(1);
                body(w);
            });
            return _coreFuncs++;
        }

        private uint CoreInstanceFromExports(IEnumerable<(string Name, byte Sort, uint Index)> exports)
        {
            var list = exports.ToList();
            _out.Section(2, w =>
            {
                w.U32(1);
                w.Byte(0x01);
                w.U32((uint)list.Count);
                foreach (var (name, sort, index) in list)
                {
                    w.Name(name);
                    w.Byte(sort);
                    w.U32(index);
                }
            });
            return _coreInstances++;
        }

        // The shim exports one stub per import that needs the main module's memory or realloc: each forwards its
        // arguments through a funcref table, which the fixup module fills once the main instance exists.
        private static byte[] BuildShimModule(List<ImportPlan> indirect, List<CoreModule.FuncType> types)
        {
            var w = new CtWriter();
            w.Bytes([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);
            WriteTypeSection(w, types);
            uint n = (uint)indirect.Count;
            w.Section(3, s =>
            {
                s.U32(n);
                foreach (ImportPlan p in indirect)
                    s.U32((uint)types.IndexOf(TypeOf(p)));
            });
            w.Section(4, s =>
            {
                s.U32(1);
                s.Byte(0x70);
                s.Byte(0x01);
                s.U32(n);
                s.U32(n);
            });
            w.Section(7, s =>
            {
                s.U32(n + 1);
                for (uint i = 0; i < n; i++)
                {
                    s.Name(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    s.Byte(0x00);
                    s.U32(i);
                }

                s.Name("$imports");
                s.Byte(0x01);
                s.U32(0);
            });
            w.Section(10, s =>
            {
                s.U32(n);
                for (int i = 0; i < indirect.Count; i++)
                {
                    CoreModule.FuncType type = TypeOf(indirect[i]);
                    var body = new CtWriter();
                    body.U32(0);
                    for (uint p = 0; p < type.Params.Length; p++)
                    {
                        body.Byte(0x20);
                        body.U32(p);
                    }

                    body.Byte(0x41);
                    body.S33(i);
                    body.Byte(0x11);
                    body.U32((uint)types.IndexOf(type));
                    body.U32(0);
                    body.Byte(0x0B);
                    s.U32((uint)body.Length);
                    s.Bytes(body.ToArray());
                }
            });
            WriteNameSection(w, indirect.Select(p => p.Kind == ImportKind.FromAdapter
                ? $"adapt-{p.Import.Module}-{p.Import.Field}"
                : $"indirect-{p.Import.Module}-{p.Import.Field}"));
            return w.ToArray();
        }

        private static byte[] BuildFixupModule(List<ImportPlan> indirect, List<CoreModule.FuncType> types)
        {
            var w = new CtWriter();
            w.Bytes([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);
            WriteTypeSection(w, types);
            uint n = (uint)indirect.Count;
            w.Section(2, s =>
            {
                s.U32(n + 1);
                for (int i = 0; i < indirect.Count; i++)
                {
                    s.Name("actual");
                    s.Name(i.ToString(System.Globalization.CultureInfo.InvariantCulture));
                    s.Byte(0x00);
                    s.U32((uint)types.IndexOf(TypeOf(indirect[i])));
                }

                s.Name("shim");
                s.Name("$imports");
                s.Byte(0x01);
                s.Byte(0x70);
                s.Byte(0x01);
                s.U32(n);
                s.U32(n);
            });
            w.Section(9, s =>
            {
                s.U32(1);
                s.Byte(0x00);
                s.Bytes([0x41, 0x00, 0x0B]);
                s.U32(n);
                for (uint i = 0; i < n; i++)
                    s.U32(i);
            });
            return w.ToArray();
        }

        private static CoreModule.FuncType TypeOf(ImportPlan p) => p.CoreType;

        private static void WriteTypeSection(CtWriter w, List<CoreModule.FuncType> types) => w.Section(1, s =>
        {
            s.U32((uint)types.Count);
            foreach (CoreModule.FuncType t in types)
            {
                s.Byte(0x60);
                s.U32((uint)t.Params.Length);
                s.Bytes(t.Params);
                s.U32((uint)t.Results.Length);
                s.Bytes(t.Results);
            }
        });

        private static void WriteNameSection(CtWriter w, IEnumerable<string> functionNames) => w.Section(0, s =>
        {
            s.Name("name");
            var names = functionNames.ToList();
            var sub = new CtWriter();
            sub.U32((uint)names.Count);
            for (int i = 0; i < names.Count; i++)
            {
                sub.U32((uint)i);
                sub.Name(names[i]);
            }

            s.Byte(0x01);
            s.U32((uint)sub.Length);
            s.Bytes(sub.ToArray());
        });

        // ── Exports: lift each function, wrap the interface in a shim component, export its instance ──

        private uint _components;

        private void EmitExports(List<(WitInterface Interface, List<ExportPlan> Funcs)> exports)
        {
            foreach (var (iface, funcs) in exports)
            {
                var lifted = new List<(string Name, uint Func, uint TypeIndex)>();
                foreach (var (name, _, owner, coreName, lifting) in funcs)
                {
                    iface.FindFunction(name, out int d);
                    uint typeIndex = ((CtExportDecl)iface.Decls[d]).Desc.Index;
                    uint funcType = LiftType(iface, typeIndex);
                    uint coreFunc = CoreExportAlias(owner.Instance, coreName, CtSort.CoreFunc);
                    uint memory = lifting.Memory ? MainMemory() : 0;
                    uint? realloc = lifting.Realloc ? Realloc(owner, import: false) : null;
                    uint? postReturn = owner.Module.Exports.TryGetValue($"cabi_post_{coreName}", out var post) && post.Kind == 0x00
                        ? CoreExportAlias(owner.Instance, $"cabi_post_{coreName}", CtSort.CoreFunc)
                        : null;
                    _out.Section(8, w =>
                    {
                        w.U32(1);
                        w.Byte(0x00);
                        w.Byte(0x00);
                        w.U32(coreFunc);
                        WriteOptions(w, lifting, memory, realloc, postReturn);
                        w.U32(funcType);
                    });
                    lifted.Add((name, _funcs++, typeIndex));
                }

                // Building the shim may alias or lift outer types, so it must finish before its section is written.
                var shim = new ExportShim(this, iface);
                byte[] component = shim.Build(lifted);
                uint componentIndex = _components++;
                _out.Section(4, w => w.Bytes(component));
                _out.Section(5, w =>
                {
                    w.U32(1);
                    w.Byte(0x00);
                    w.U32(componentIndex);
                    w.U32((uint)shim.Args.Count);
                    foreach (var (name, sort, index) in shim.Args)
                    {
                        w.Name(name);
                        w.Byte(sort);
                        w.U32(index);
                    }
                });
                uint instance = _instances++;
                _out.Section(11, w =>
                {
                    w.U32(1);
                    ComponentTypeCodec.WriteExternName(w, iface.Name);
                    w.Byte(CtSort.Instance);
                    w.U32(instance);
                    w.Byte(0x00);
                });
                // An export adds an entry to its sort's index space.
                _instances++;
            }
        }

        /// <summary>
        /// The component wit-component calls <c>&lt;interface&gt;-shim-component</c>: it imports the interface's named types
        /// and the lifted functions, then re-exports them so the functions' signatures refer to the interface's own
        /// type names. Exporting the lifted functions directly would give a <c>use</c>d type two names, and the
        /// result would not decode to WIT.
        /// </summary>
        private sealed class ExportShim(Builder outer, WitInterface iface)
        {
            private readonly CtWriter _body = new();
            private readonly HashSet<string> _names = new(StringComparer.Ordinal);
            private readonly Dictionary<(WitInterface, string), uint> _imported = [];
            private readonly Dictionary<string, uint> _exported = new(StringComparer.Ordinal);
            private readonly Dictionary<(WitInterface, uint, bool), uint> _anonymous = [];
            private uint _types, _funcs;

            /// <summary>Instantiation arguments: import name, outer sort and outer index.</summary>
            public List<(string Name, byte Sort, uint Index)> Args { get; } = [];

            public byte[] Build(List<(string Name, uint Func, uint TypeIndex)> lifted)
            {
                var imports = new List<uint>();
                foreach (var (name, func, typeIndex) in lifted)
                {
                    uint type = Type(iface, typeIndex, exported: false);
                    string importName = Import($"import-func-{name}", new CtExternDesc(CtExternDesc.Func, type));
                    Args.Add((importName, CtSort.Func, func));
                    imports.Add(_funcs++);
                }

                foreach (CtDecl decl in iface.Decls)
                {
                    if (decl is not CtExportDecl { Desc.Kind: CtExternDesc.Type } e)
                        continue;
                    Export(e.Name, CtSort.Type, Imported(iface, e.Name), null);
                    _exported[e.Name] = _types++;
                }

                for (int i = 0; i < lifted.Count; i++)
                {
                    uint type = Type(iface, lifted[i].TypeIndex, exported: true);
                    Export(lifted[i].Name, CtSort.Func, imports[i], new CtExternDesc(CtExternDesc.Func, type));
                    _funcs++;
                }

                var w = new CtWriter();
                w.Bytes([0x00, 0x61, 0x73, 0x6D, 0x0D, 0x00, 0x01, 0x00]);
                w.Bytes(_body.ToArray());
                return w.ToArray();
            }

            /// <summary>
            /// A type of <paramref name="scope"/> inside the shim: named types resolve to their import, or to the shim's
            /// own export when <paramref name="exported"/> (the signatures the shim exports); others are copied.
            /// </summary>
            private uint Type(WitInterface scope, uint index, bool exported)
            {
                switch (scope.Decls[scope.TypeDecls[index]])
                {
                    case CtAliasDecl { Target: CtAliasSymbol symbol }:
                        return Imported(outer.World.Lookup(symbol), symbol.Name);
                    case CtExportDecl { Desc.Kind: CtExternDesc.Type } e:
                        return exported && scope == iface && _exported.TryGetValue(e.Name, out uint own) ? own : Imported(scope, e.Name);
                    case CtTypeDecl t:
                        if (_anonymous.TryGetValue((scope, index, exported), out uint copy))
                            return copy;
                        CtDefType def = ComponentTypeCodec.RemapTypes(t.Type, i => Type(scope, i, exported));
                        _body.Section(7, w =>
                        {
                            w.U32(1);
                            ComponentTypeCodec.WriteDefType(w, def);
                        });
                        return _anonymous[(scope, index, exported)] = _types++;
                    default:
                        throw new NotSupportedException($"type {index} of interface `{scope.Name}` cannot be exported.");
                }
            }

            /// <summary>Imports the type <paramref name="owner"/> exports as <paramref name="name"/>.</summary>
            private uint Imported(WitInterface owner, string name)
            {
                if (_imported.TryGetValue((owner, name), out uint index))
                    return index;
                var e = (CtExportDecl)owner.Decls[owner.Exports[name]];
                CtExternDesc bound;
                uint outerType;
                if (e.Desc.SubResource)
                {
                    if (owner.IsExport)
                        throw new NotSupportedException($"exported interface `{owner.Name}` defines resource `{name}`; exported resources are not supported yet.");
                    bound = new CtExternDesc(CtExternDesc.Type, 0, SubResource: true);
                    outerType = outer.TypeAlias(owner, name);
                }
                else
                {
                    switch (owner.Decls[owner.TypeDecls[e.Desc.Index]])
                    {
                        // `use other.{name}`: the same type, under the name its defining interface gives it.
                        case CtAliasDecl { Target: CtAliasSymbol symbol }:
                            return _imported[(owner, name)] = Imported(outer.World.Lookup(symbol), symbol.Name);
                        case CtExportDecl other:
                            bound = new CtExternDesc(CtExternDesc.Type, Imported(owner, other.Name));
                            break;
                        default:
                            bound = new CtExternDesc(CtExternDesc.Type, Type(owner, e.Desc.Index, exported: false));
                            break;
                    }

                    outerType = owner.IsExport ? outer.LiftType(owner, e.Desc.Index) : outer.TypeAlias(owner, name);
                }

                string importName = Import($"import-type-{name}", bound);
                Args.Add((importName, CtSort.Type, outerType));
                return _imported[(owner, name)] = _types++;
            }

            private string Import(string baseName, CtExternDesc desc)
            {
                string name = baseName;
                for (int i = 0; !_names.Add(name); i++)
                    name = baseName + i.ToString(System.Globalization.CultureInfo.InvariantCulture);
                _body.Section(10, w =>
                {
                    w.U32(1);
                    ComponentTypeCodec.WriteExternName(w, name);
                    ComponentTypeCodec.WriteExternDesc(w, desc);
                });
                return name;
            }

            private void Export(string name, byte sort, uint index, CtExternDesc? ascription) => _body.Section(11, w =>
            {
                w.U32(1);
                ComponentTypeCodec.WriteExternName(w, name);
                w.Byte(sort);
                w.U32(index);
                if (ascription is null)
                {
                    w.Byte(0x00);
                }
                else
                {
                    w.Byte(0x01);
                    ComponentTypeCodec.WriteExternDesc(w, ascription);
                }
            });
        }

        /// <summary>Copies a type from an exported interface's scope to the component's type index space.</summary>
        private uint LiftType(WitInterface iface, uint index)
        {
            if (_liftTypes.TryGetValue((iface, index), out uint top))
                return top;
            CtDecl decl = iface.Decls[iface.TypeDecls[index]];
            switch (decl)
            {
                case CtAliasDecl { Target: CtAliasSymbol symbol }:
                    WitInterface owner = world.Lookup(symbol);
                    if (owner.IsExport)
                        throw new NotSupportedException($"exported interface `{iface.Name}` uses types from exported interface `{owner.Name}`.");
                    top = TypeAlias(owner, symbol.Name);
                    break;
                // The shim component gives the type its name; the outer component only needs its structure.
                case CtExportDecl { Desc.Kind: CtExternDesc.Type, Desc.SubResource: false } e:
                    top = LiftType(iface, e.Desc.Index);
                    break;
                case CtTypeDecl t:
                    CtDefType def = ComponentTypeCodec.RemapTypes(t.Type, i => LiftType(iface, i));
                    top = _types++;
                    _out.Section(7, w =>
                    {
                        w.U32(1);
                        ComponentTypeCodec.WriteDefType(w, def);
                    });
                    break;
                default:
                    throw new NotSupportedException($"type {index} of exported interface `{iface.Name}` cannot be lifted.");
            }

            _liftTypes[(iface, index)] = top;
            return top;
        }
    }
}
