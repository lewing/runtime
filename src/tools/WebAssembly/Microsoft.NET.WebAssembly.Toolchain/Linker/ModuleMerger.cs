// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>How <see cref="ModuleMerger"/> treats two inputs exporting the same name.</summary>
public enum ExportConflictMode
{
    /// <summary>Fail the merge (binaryen <c>wasm-merge</c>'s default).</summary>
    Error,

    /// <summary>Keep the first export and drop later ones (<c>--skip-export-conflicts</c>).</summary>
    Skip,

    /// <summary>Rename later exports to <c>name_N</c> (<c>--rename-export-conflicts</c>).</summary>
    Rename,
}

/// <summary>Options for <see cref="ModuleMerger"/>.</summary>
public sealed class ModuleMergeOptions
{
    public ExportConflictMode ExportConflicts { get; init; } = ExportConflictMode.Error;

    /// <summary>
    /// Drop functions, globals and tags (imported or defined) nothing reachable uses, as
    /// <c>wasm-merge</c>'s <c>remove-unused-module-elements</c> does. Imports satisfied by another
    /// input never survive either way.
    /// </summary>
    public bool RemoveUnused { get; init; } = true;

    /// <summary><c>wasm-merge -g</c>: emit a <c>name</c> section with the inputs' function names.</summary>
    /// <remarks>
    /// With or without this option the first input's DWARF (2–4) is kept, as wasm-merge keeps it: code is
    /// re-encoded with minimal LEB128s and unused functions are removed, so every code address in it is
    /// rewritten to follow its instruction, and addresses in removed functions become LLD's tombstones. DWARF
    /// that cannot be rewritten exactly (DWARF 5, split DWARF, or a global its expressions name, such as a frame
    /// base's <c>__stack_pointer</c>, that moved) is dropped, never kept stale, with a <see cref="Warning"/>; so is a
    /// <c>.debug_aranges</c> tuple that is neither a code range nor a variable's address (that section alone).
    /// </remarks>
    public bool DebugInfo { get; init; }

    /// <summary>Receives non-fatal diagnostics, such as the first input's DWARF being dropped.</summary>
    public Action<string>? Warning { get; init; }
}

/// <summary>
/// Merges fully linked core modules by name, replacing binaryen's <c>wasm-merge</c>: each input is
/// given a module name, an import from <c>(name, field)</c> is satisfied by the export
/// <c>field</c> of the input called <c>name</c>, and every function body is re-encoded into the
/// merged index spaces.
/// </summary>
/// <remarks>
/// Unlike <see cref="SectionMerger"/>, which relies on relocations to fix up code, inputs here are
/// final modules, so every instruction that names an index is rewritten. Atomic (0xFE) and
/// stack-switching (0xE0) instructions are not supported (#567). Neither are GC struct and array
/// types: type deduplication and the type-section writer handle function types only (#383), so an
/// input with one is rejected with a <see cref="LinkerException"/>.
/// </remarks>
public static class ModuleMerger
{
    private enum Kind
    {
        Func,
        Table,
        Memory,
        Global,
        Tag
    }

    private readonly record struct ItemRef(int Module, int Index);

    /// <summary>Merges <paramref name="inputs"/> (module, name) in order into one module.</summary>
    public static WasmModule Merge(IReadOnlyList<(WasmModule Module, string Name)> inputs, ModuleMergeOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (inputs.Count == 0)
            throw new LinkerException("wasm-merge needs at least one input module.");
        return new State(inputs, options ?? new ModuleMergeOptions()).Run();
    }

    /// <summary>Merges and encodes the result as a binary module.</summary>
    public static byte[] MergeToBinary(IReadOnlyList<(WasmModule Module, string Name)> inputs, ModuleMergeOptions? options = null) =>
        new SectionMerger().EmitBinary(Merge(inputs, options));

    /// <summary>The first function type with no parameters and no results, or -1. Struct and array types also have an empty compact signature.</summary>
    internal static int FindEmptyFunctionType(IReadOnlyList<TypeDefinition> definitions)
    {
        for (int i = 0; i < definitions.Count; i++)
        {
            if (definitions[i].CompositeType is CompositeType.Function { Parameters.Count: 0, Results.Count: 0 })
                return i;
        }

        return -1;
    }

    private sealed class State
    {
        private readonly IReadOnlyList<(WasmModule Module, string Name)> _inputs;
        private readonly ModuleMergeOptions _options;
        private readonly WasmModule[] _modules;
        private readonly IndexMap[] _maps;
        private readonly List<Import>[][] _importsByKind;
        private readonly Dictionary<(Kind, int, int), ItemRef> _resolved = [];
        private readonly Dictionary<(Kind, string, string), ItemRef> _exportTargets = [];
        private readonly List<(string Name, Kind Kind, ItemRef Target)> _exports = [];
        private readonly HashSet<(Kind, ItemRef)> _live = [];
        private readonly Queue<(Kind, ItemRef)> _work = new();
        private List<FuncType> _types = [];
        private readonly Dictionary<int, List<CodeAddressMap.Resized>> _resized = [];
        private List<TypeDefinition> _typeDefinitions = [];

        public State(IReadOnlyList<(WasmModule Module, string Name)> inputs, ModuleMergeOptions options)
        {
            _inputs = inputs;
            _options = options;
            _modules = [.. inputs.Select(i => i.Module)];
            _maps = [.. inputs.Select(_ => new IndexMap())];
            _importsByKind = new List<Import>[_modules.Length][];
            for (int m = 0; m < _modules.Length; m++)
            {
                var byKind = new List<Import>[5];
                for (int k = 0; k < 5; k++)
                    byKind[k] = [];
                foreach (var import in _modules[m].Imports)
                    byKind[(int)KindOf(import.Descriptor)].Add(import);
                _importsByKind[m] = byKind;
            }
        }

        public WasmModule Run()
        {
            var merger = new SectionMerger();
            _types = merger.MergeTypes(_modules, _maps);
            _typeDefinitions = merger.MergeTypeDefinitions(_modules, _maps, _types.Count);

            CollectExports();
            CheckFusedTypes();
            MarkLive();

            var imports = AssignImports();
            var functionOrder = AssignDefined(Kind.Func);
            var tableOrder = AssignDefined(Kind.Table);
            var memoryOrder = AssignDefined(Kind.Memory);
            var tagOrder = AssignDefined(Kind.Tag);
            var globalOrder = OrderGlobals(AssignDefinedList(Kind.Global));
            int globalBase = imports.Count(i => i.Descriptor is ImportDescriptor.Global);
            for (int i = 0; i < globalOrder.Count; i++)
                Assign(Kind.Global, globalOrder[i], (uint)(globalBase + i));
            FillReferenceMaps();
            AssignSegments();

            var functions = new List<WasmFunction>(functionOrder.Count + 1);
            foreach (var r in functionOrder)
                functions.Add(RemapFunction(r));

            var starts = new List<uint>();
            for (int m = 0; m < _modules.Length; m++)
            {
                if (_modules[m].StartFunction is { } start)
                    starts.Add(_maps[m].Functions[(uint)start]);
            }

            int? startFunction = null;
            int functionImportCount = imports.Count(i => i.Descriptor is ImportDescriptor.Func);
            if (starts.Count == 1)
            {
                startFunction = (int)starts[0];
            }
            else if (starts.Count > 1)
            {
                functions.Add(CombinedStart(starts));
                startFunction = functionImportCount + functions.Count - 1;
            }

            var module = new WasmModule
            {
                Types = _types,
                TypeDefinitions = _typeDefinitions,
                Imports = imports,
                Functions = functions,
                Tables = [.. tableOrder.Select(RemapTable)],
                Memories = [.. memoryOrder.Select(r => _modules[r.Module].Memories[r.Index - ImportCount(r.Module, Kind.Memory)])],
                Globals = [.. globalOrder.Select(RemapGlobal)],
                Tags = [.. tagOrder.Select(r => new Tag((int)MapType(r.Module, _modules[r.Module].Tags[r.Index - ImportCount(r.Module, Kind.Tag)].TypeIndex)))],
                Elements = [.. Enumerable.Range(0, _modules.Length).SelectMany(m => _modules[m].Elements.Select(e => RemapElement(m, e)))],
                DataSegments = [.. Enumerable.Range(0, _modules.Length).SelectMany(m => _modules[m].DataSegments.Select(d => RemapData(m, d)))],
                StartFunction = startFunction,
                HasDataCountSection = _modules.Any(m => m.HasDataCountSection),
            };

            foreach (var (name, kind, target) in _exports)
            {
                uint index = MergedIndex(kind, target);
                module.Exports[name] = new Export(name, kind switch
                {
                    Kind.Func => new ExportDescriptor.Func((int)index),
                    Kind.Table => new ExportDescriptor.Table((int)index),
                    Kind.Memory => new ExportDescriptor.Memory((int)index),
                    Kind.Global => new ExportDescriptor.Global((int)index),
                    Kind.Tag => new ExportDescriptor.Tag((int)index),
                    _ => throw new InvalidOperationException(),
                });
            }

            module.ProducersSection = _modules[0].ProducersSection;
            var debugSections = new List<CustomSection>();
            foreach (var section in _modules[0].CustomSections)
            {
                if (IsDebugSection(section.Name))
                    debugSections.Add(section);
            }

            List<CustomSection?>? dwarf = null;
            if (debugSections.Count > 0)
            {
                CodeAddressMap? map = BuildCodeAddressMap(functions, functionOrder, out string? reason);
                var rewritten = map is null ? null : DwarfCodeAddressRewriter.TryRewrite(debugSections, map, out reason);
                int mergedGlobals = module.Globals.Count;
                foreach (var import in module.Imports)
                {
                    if (import.Descriptor is ImportDescriptor.Global)
                        mergedGlobals++;
                }

                if (rewritten is not null && FirstModuleGlobalMoved(rewritten.Globals, mergedGlobals) is { } moved)
                {
                    reason = moved;
                    rewritten = null;
                }

                if (rewritten is null)
                {
                    _options.Warning?.Invoke(
                        $"the DWARF of input '{_inputs[0].Name}' was dropped: {reason}, so it cannot be made to describe the merged code.");
                }
                else
                {
                    dwarf = rewritten.Sections;
                    foreach (string warning in rewritten.Warnings)
                        _options.Warning?.Invoke($"in the DWARF of input '{_inputs[0].Name}', {warning}.");
                }
            }

            int nextDebug = 0;
            foreach (var section in _modules[0].CustomSections)
            {
                if (IsDebugSection(section.Name))
                {
                    if (dwarf?[nextDebug] is { } rewrittenSection)
                        module.CustomSections.Add(rewrittenSection);
                    nextDebug++;
                }
                else if (!IsRegeneratedSection(section.Name))
                {
                    module.CustomSections.Add(section);
                }
            }

            // LLVM tools require name before producers and target_features (the writer puts producers right
            // before target_features).
            if (_options.DebugInfo && BuildNameSection() is { } names)
            {
                int features = module.CustomSections.FindIndex(c => c.Name == "target_features");
                module.CustomSections.Insert(features < 0 ? module.CustomSections.Count : features, names);
            }

            return module;
        }

        // ── Custom sections ───────────────────────────────────────────────────

        private static bool IsDebugSection(string name) =>
            name.StartsWith(".debug_", StringComparison.Ordinal) || name is "external_debug_info";

        // Sections the merge rebuilds or that no longer describe the output.
        private static bool IsRegeneratedSection(string name) =>
            name is "name" or "producers" or "linking" or "sourceMappingURL" or BranchHintMetadataReader.SectionName ||
            name.StartsWith("reloc.", StringComparison.Ordinal);

        // DWARF names globals by index (a frame base names __stack_pointer), which the rewriter does not remap:
        // those it names (every global, when it could not read an expression) must keep their indices. One that was
        // removed may only leave its index unused (evaluating it fails rather than reading another global).
        private string? FirstModuleGlobalMoved(IReadOnlySet<uint>? named, int mergedGlobals)
        {
            if (named is null)
            {
                for (int i = 0; i < ImportCount(0, Kind.Global) + _modules[0].Globals.Count; i++)
                {
                    if (!_maps[0].Globals.TryGetValue((uint)i, out uint index) ? i < mergedGlobals : index != i)
                        return $"its global {i} moved or was replaced (an expression it could not read may name it)";
                }

                return null;
            }

            // In index order, so the reason names the lowest one.
            uint[] sorted = new uint[named.Count];
            int n = 0;
            foreach (uint i in named)
                sorted[n++] = i;
            Array.Sort(sorted);
            foreach (uint i in sorted)
            {
                if (!_maps[0].Globals.TryGetValue(i, out uint index))
                {
                    if (i < mergedGlobals)
                        return $"its global {i}, which it names, was removed and another global took its index";
                }
                else if (index != i)
                    return $"its global {i}, which it names, moved to {index}";
            }

            return null;
        }

        /// <summary>
        /// Maps the first input's code addresses to the merged module's: each kept function to its place in the
        /// final encoding (instruction offsets through the breakpoints recorded when its body was re-encoded),
        /// each removed function to nothing.
        /// </summary>
        private CodeAddressMap? BuildCodeAddressMap(List<WasmFunction> functions, List<ItemRef> functionOrder, out string? reason)
        {
            reason = null;
            var first = _modules[0];
            int imported = ImportCount(0, Kind.Func);
            var position = new Dictionary<int, int>();
            for (int j = 0; j < functionOrder.Count; j++)
            {
                if (functionOrder[j].Module == 0)
                    position[functionOrder[j].Index - imported] = j;
            }

            var newLayout = new (uint Start, uint Body, uint End)[functions.Count];
            uint offset = (uint)WasmLeb128.GetULeb128Size((uint)functions.Count);
            var scratch = new MemoryStream();
            for (int j = 0; j < functions.Count; j++)
            {
                uint size = (uint)SectionMerger.EncodedFunctionBodyLength(functions[j], _typeDefinitions, j, scratch);
                uint start = offset + (uint)WasmLeb128.GetULeb128Size(size);
                uint body = start + size - (uint)functions[j].Body.Length - (EndImplied(functions[j].Body) ? 1u : 0u);
                newLayout[j] = (start, body, start + size);
                offset = start + size;
            }

            var mapped = new List<CodeAddressMap.Function>(first.Functions.Count);
            for (int i = 0; i < first.Functions.Count; i++)
            {
                var original = first.Functions[i];
                if (original.LocalsCodeSectionOffset == 0 || original.BodyCodeSectionOffset == 0)
                {
                    reason = "its code offsets are unknown (the module was not read from a binary)";
                    return null;
                }

                uint oldEnd = original.BodyCodeSectionOffset + (uint)original.Body.Length + (EndImplied(original.Body) ? 1u : 0u);
                if (position.TryGetValue(i, out int j))
                {
                    mapped.Add(new CodeAddressMap.Function(
                        original.LocalsCodeSectionOffset, original.BodyCodeSectionOffset, oldEnd,
                        true, newLayout[j].Start, newLayout[j].Body, newLayout[j].End, _resized.TryGetValue(i, out var r) ? r : Array.Empty<CodeAddressMap.Resized>()));
                }
                else
                {
                    mapped.Add(new CodeAddressMap.Function(
                        original.LocalsCodeSectionOffset, original.BodyCodeSectionOffset, oldEnd, false, 0, 0, 0, []));
                }
            }

            return new CodeAddressMap(mapped);
        }

        private static bool EndImplied(byte[] body) => body.Length == 0 || body[^1] != WasmOpcode.End;

        // ── Exports and import fusion ─────────────────────────────────────────

        private void CollectExports()
        {
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int m = 0; m < _modules.Length; m++)
            {
                foreach (var export in _modules[m].Exports.Values)
                {
                    Kind kind = KindOf(export.Descriptor);
                    var target = new ItemRef(m, IndexOf(export.Descriptor));
                    string name = export.Name;
                    if (!used.Add(name))
                    {
                        switch (_options.ExportConflicts)
                        {
                            case ExportConflictMode.Error:
                                throw new LinkerException(
                                    $"Export name conflict: {name} (consider --rename-export-conflicts or --skip-export-conflicts)");
                            case ExportConflictMode.Skip:
                                continue;
                            default:
                                // binaryen's Names::getValidExportName: root_N, N starting at the export count.
                                for (int n = _exports.Count; ; n++)
                                {
                                    string candidate = $"{name}_{n}";
                                    if (used.Add(candidate))
                                    {
                                        name = candidate;
                                        break;
                                    }
                                }

                                break;
                        }
                    }

                    _exports.Add((name, kind, target));
                    // A later input with the same module name shadows an earlier one's export.
                    _exportTargets[(kind, _inputs[m].Name, export.Name)] = target;
                }
            }
        }

        private int ImportCount(int module, Kind kind) => _importsByKind[module][(int)kind].Count;

        private ItemRef Resolve(Kind kind, ItemRef item) => Resolve(kind, item, depth: 0);

        private ItemRef Resolve(Kind kind, ItemRef item, int depth)
        {
            if (item.Index >= ImportCount(item.Module, kind))
                return item;
            if (_resolved.TryGetValue((kind, item.Module, item.Index), out var cached))
                return cached;

            var import = _importsByKind[item.Module][(int)kind][item.Index];
            ItemRef result = item;
            if (depth <= _modules.Length * 4 &&
                _exportTargets.TryGetValue((kind, import.ModuleName, import.Name), out var target) &&
                target != item)
            {
                result = Resolve(kind, target, depth + 1);
            }

            _resolved[(kind, item.Module, item.Index)] = result;
            return result;
        }

        private void CheckFusedTypes()
        {
            var errors = new List<string>();
            for (int m = 0; m < _modules.Length; m++)
            {
                for (int k = 0; k < 5; k++)
                {
                    var kind = (Kind)k;
                    var imports = _importsByKind[m][k];
                    for (int i = 0; i < imports.Count; i++)
                    {
                        var target = Resolve(kind, new ItemRef(m, i));
                        if (target == new ItemRef(m, i))
                            continue;
                        if (Mismatch(kind, m, imports[i], target) is { } why)
                        {
                            errors.Add(
                                $"Type mismatch when importing {(kind == Kind.Func ? "function" : kind.ToString().ToLowerInvariant())} {imports[i].Name} " +
                                $"from module {imports[i].ModuleName}: {why}");
                        }
                    }
                }
            }

            if (errors.Count > 0)
                throw new LinkerException("import/export mismatches:\n" + string.Join("\n", errors));
        }

        private string? Mismatch(Kind kind, int module, Import import, ItemRef target)
        {
            switch (kind)
            {
                case Kind.Func:
                {
                    uint expected = MapType(module, ((ImportDescriptor.Func)import.Descriptor).TypeIndex);
                    uint actual = MapType(target.Module, FunctionTypeIndex(target));
                    return expected == actual ? null : $"type {Describe(actual)} is not {Describe(expected)}";
                }

                case Kind.Tag:
                {
                    uint expected = MapType(module, ((ImportDescriptor.Tag)import.Descriptor).TypeIndex);
                    uint actual = MapType(target.Module, TagTypeIndex(target));
                    return expected == actual ? null : $"type {Describe(actual)} is not {Describe(expected)}";
                }

                case Kind.Global:
                {
                    var expected = ((ImportDescriptor.Global)import.Descriptor).Type;
                    var actual = GlobalTypeOf(target);
                    if (expected.Mutability != actual.Mutability)
                        return "mutability should match";
                    string expectedKey = RebasedKey(module, expected.ExactContentType) ?? expected.ContentType.ToString();
                    string actualKey = RebasedKey(target.Module, actual.ExactContentType) ?? actual.ContentType.ToString();
                    return expectedKey == actualKey ? null : $"export type {actualKey} is different from import type {expectedKey}";
                }

                case Kind.Table:
                {
                    var expected = ((ImportDescriptor.Table)import.Descriptor).Type;
                    var actual = TableTypeOf(target);
                    string expectedKey = RebasedKey(module, expected.ExactElementType) ?? expected.ElementType.ToString();
                    string actualKey = RebasedKey(target.Module, actual.ExactElementType) ?? actual.ElementType.ToString();
                    if (expectedKey != actualKey)
                        return $"export type {actualKey} is different from import type {expectedKey}";
                    if (expected.Is64 != actual.Is64)
                        return "index type should match";
                    return LimitMismatch(actual.Limits, expected.Limits);
                }

                case Kind.Memory:
                {
                    var expected = ((ImportDescriptor.Memory)import.Descriptor).Type;
                    var actual = MemoryTypeOf(target);
                    if (expected.Is64 != actual.Is64)
                        return "index type should match";
                    if (expected.Shared != actual.Shared)
                        return "shared flag should match";
                    return LimitMismatch(actual.Limits, expected.Limits);
                }
            }

            return null;
        }

        private static string? LimitMismatch(Limits export, Limits import)
        {
            if (export.Min < import.Min)
                return $"minimal size {export.Min} is smaller than expected minimal size {import.Min}";
            if (import.Max is { } max)
            {
                if (export.Max is not { } exportMax)
                    return "expecting a bounded export but it is unbounded";
                if (exportMax > max)
                    return $"maximal size {exportMax} is larger than expected maximal size {max}";
            }

            return null;
        }

        private string? RebasedKey(int module, Microsoft.NET.WebAssembly.Types.ValueType? exact) =>
            SectionMerger.RemapExactRefType(exact, t => MapType(module, (int)t))?.Key;

        private string Describe(uint mergedType) =>
            mergedType < _types.Count
                ? $"(func (param {string.Join(" ", _types[(int)mergedType].Parameters)}) (result {string.Join(" ", _types[(int)mergedType].Results)}))"
                : mergedType.ToString();

        // ── Liveness ──────────────────────────────────────────────────────────

        private void MarkLive()
        {
            if (!_options.RemoveUnused)
            {
                for (int m = 0; m < _modules.Length; m++)
                {
                    for (int k = 0; k < 5; k++)
                    {
                        var kind = (Kind)k;
                        int total = ImportCount(m, kind) + DefinedCount(m, kind);
                        for (int i = 0; i < total; i++)
                            Mark(kind, new ItemRef(m, i));
                    }
                }

                Drain();
                return;
            }

            foreach (var (_, kind, target) in _exports)
                Mark(kind, target);

            for (int m = 0; m < _modules.Length; m++)
            {
                var module = _modules[m];
                if (module.StartFunction is { } start)
                    Mark(Kind.Func, new ItemRef(m, start));

                // Memories and tables are kept: segments initialize them and they cost nothing to keep.
                for (int i = 0; i < ImportCount(m, Kind.Memory) + module.Memories.Count; i++)
                    Mark(Kind.Memory, new ItemRef(m, i));
                for (int i = 0; i < ImportCount(m, Kind.Table) + module.Tables.Count; i++)
                    Mark(Kind.Table, new ItemRef(m, i));

                foreach (var element in module.Elements)
                {
                    ScanConstExpr(m, element.OffsetExpr);
                    foreach (int f in element.FunctionIndices)
                        Mark(Kind.Func, new ItemRef(m, f));
                    foreach (int f in element.DeclaredFunctionIndices)
                        Mark(Kind.Func, new ItemRef(m, f));
                    foreach (var expr in element.ElementExpressions)
                        ScanConstExpr(m, expr);
                }

                foreach (var data in module.DataSegments)
                    ScanConstExpr(m, data.OffsetExpr);
            }

            Drain();
        }

        private void Mark(Kind kind, ItemRef item)
        {
            if (_live.Add((kind, item)))
                _work.Enqueue((kind, item));
        }

        private void Drain()
        {
            while (_work.TryDequeue(out var entry))
            {
                var (kind, item) = entry;
                var canonical = Resolve(kind, item);
                if (canonical != item)
                {
                    Mark(kind, canonical);
                    continue;
                }

                int defined = item.Index - ImportCount(item.Module, kind);
                if (defined < 0)
                    continue;

                var module = _modules[item.Module];
                switch (kind)
                {
                    case Kind.Func:
                        BodyRewriter.Rewrite(module.Functions[defined].Body, (space, index) =>
                        {
                            MarkReference(item.Module, space, index);
                            return index;
                        });
                        break;
                    case Kind.Global:
                        ScanConstExpr(item.Module, module.Globals[defined].InitExpr);
                        break;
                    case Kind.Table:
                        if (module.Tables[defined].InitExpr.Length > 0)
                            ScanConstExpr(item.Module, module.Tables[defined].InitExpr);
                        break;
                }
            }
        }

        private void MarkReference(int module, IndexSpace space, uint index)
        {
            switch (space)
            {
                case IndexSpace.Function: Mark(Kind.Func, new ItemRef(module, (int)index)); break;
                case IndexSpace.Global: Mark(Kind.Global, new ItemRef(module, (int)index)); break;
                case IndexSpace.Tag: Mark(Kind.Tag, new ItemRef(module, (int)index)); break;
                case IndexSpace.Table: Mark(Kind.Table, new ItemRef(module, (int)index)); break;
                case IndexSpace.Memory: Mark(Kind.Memory, new ItemRef(module, (int)index)); break;
            }
        }

        private void ScanConstExpr(int module, byte[] expr) =>
            ConstExprScanner.Scan(expr, (kind, value) =>
            {
                if (kind == ConstExprScanner.OperandKind.FunctionIndex)
                    Mark(Kind.Func, new ItemRef(module, (int)value));
                else if (kind == ConstExprScanner.OperandKind.GlobalIndex)
                    Mark(Kind.Global, new ItemRef(module, (int)value));
            });

        private bool IsLiveRepresentative(Kind kind, ItemRef item) =>
            _live.Contains((kind, item)) && Resolve(kind, item) == item;

        // ── Index assignment ──────────────────────────────────────────────────

        private readonly Dictionary<(Kind, ItemRef), uint> _assigned = [];

        private void Assign(Kind kind, ItemRef item, uint index) => _assigned[(kind, item)] = index;

        private List<Import> AssignImports()
        {
            var imports = new List<Import>();
            var next = new uint[5];
            for (int m = 0; m < _modules.Length; m++)
            {
                var position = new int[5];
                foreach (var import in _modules[m].Imports)
                {
                    var kind = KindOf(import.Descriptor);
                    var item = new ItemRef(m, position[(int)kind]++);
                    if (!IsLiveRepresentative(kind, item))
                        continue;
                    Assign(kind, item, next[(int)kind]++);
                    imports.Add(RemapImport(m, import));
                }
            }

            return imports;
        }

        private List<ItemRef> AssignDefinedList(Kind kind)
        {
            var list = new List<ItemRef>();
            for (int m = 0; m < _modules.Length; m++)
            {
                int imported = ImportCount(m, kind);
                for (int d = 0; d < DefinedCount(m, kind); d++)
                {
                    var item = new ItemRef(m, imported + d);
                    if (IsLiveRepresentative(kind, item))
                        list.Add(item);
                }
            }

            return list;
        }

        private List<ItemRef> AssignDefined(Kind kind)
        {
            var list = AssignDefinedList(kind);
            uint next = (uint)_assigned.Keys.Count(k => k.Item1 == kind);
            foreach (var item in list)
                Assign(kind, item, next++);
            return list;
        }

        /// <summary>
        /// Orders defined globals so every initializer reads only globals before it. Fusing an import
        /// into a later input's definition can otherwise leave a forward reference.
        /// </summary>
        private List<ItemRef> OrderGlobals(List<ItemRef> globals)
        {
            var ordered = new List<ItemRef>(globals.Count);
            var state = new Dictionary<ItemRef, int>();
            var definedSet = globals.ToHashSet();

            void Visit(ItemRef global)
            {
                if (state.TryGetValue(global, out int s))
                {
                    if (s == 1)
                        throw new LinkerException("Merged global initializers form a cycle.");
                    return;
                }

                state[global] = 1;
                var init = _modules[global.Module].Globals[global.Index - ImportCount(global.Module, Kind.Global)].InitExpr;
                ConstExprScanner.Scan(init, (kind, value) =>
                {
                    if (kind != ConstExprScanner.OperandKind.GlobalIndex)
                        return;
                    var dependency = Resolve(Kind.Global, new ItemRef(global.Module, (int)value));
                    if (definedSet.Contains(dependency))
                        Visit(dependency);
                });
                state[global] = 2;
                ordered.Add(global);
            }

            foreach (var global in globals)
                Visit(global);
            return ordered;
        }

        private void FillReferenceMaps()
        {
            for (int m = 0; m < _modules.Length; m++)
            {
                for (int k = 0; k < 5; k++)
                {
                    var kind = (Kind)k;
                    var map = MapFor(m, kind);
                    int total = ImportCount(m, kind) + DefinedCount(m, kind);
                    for (int i = 0; i < total; i++)
                    {
                        var canonical = Resolve(kind, new ItemRef(m, i));
                        if (_assigned.TryGetValue((kind, canonical), out uint index))
                            map[(uint)i] = index;
                    }
                }
            }
        }

        private void AssignSegments()
        {
            uint data = 0, elements = 0;
            for (int m = 0; m < _modules.Length; m++)
            {
                for (int i = 0; i < _modules[m].DataSegments.Count; i++)
                    _maps[m].DataSegments[(uint)i] = data++;
                for (int i = 0; i < _modules[m].Elements.Count; i++)
                    _maps[m].Elements[(uint)i] = elements++;
            }
        }

        private uint MergedIndex(Kind kind, ItemRef item) =>
            _assigned.TryGetValue((kind, Resolve(kind, item)), out uint index)
                ? index
                : throw new LinkerException($"Merged {kind} {item.Index} of input {item.Module} was not kept.");

        private Dictionary<uint, uint> MapFor(int module, Kind kind) => kind switch
        {
            Kind.Func => _maps[module].Functions,
            Kind.Table => _maps[module].Tables,
            Kind.Memory => _maps[module].Memories,
            Kind.Global => _maps[module].Globals,
            Kind.Tag => _maps[module].Tags,
            _ => throw new InvalidOperationException(),
        };

        // ── Output construction ───────────────────────────────────────────────

        private uint MapType(int module, int typeIndex) => _maps[module].Types[(uint)typeIndex];

        private Import RemapImport(int module, Import import)
        {
            var remapped = SectionMerger.RemapImportTypeIndices(import, t => MapType(module, (int)t));
            if (remapped.Descriptor is ImportDescriptor.Table { Type.InitExpr.Length: > 0 } table)
            {
                remapped = remapped with
                {
                    Descriptor = new ImportDescriptor.Table(table.Type with
                    {
                        InitExpr = SectionMerger.RemapConstExprIndices(table.Type.InitExpr, _maps[module]),
                    }),
                };
            }

            return remapped;
        }

        private WasmFunction RemapFunction(ItemRef item)
        {
            int m = item.Module;
            var function = _modules[m].Functions[item.Index - ImportCount(m, Kind.Func)];
            var map = _maps[m];
            List<CodeAddressMap.Resized>? resized = m == 0 ? [] : null;
            var remapped = new WasmFunction
            {
                TypeIndex = (int)MapType(m, function.TypeIndex),
                Locals = [.. function.Locals],
                ExactLocals = [.. function.ExactLocals.Select(l => SectionMerger.RemapExactRefType(l, t => MapType(m, (int)t))!)],
                Body = BodyRewriter.Rewrite(function.Body, (space, index) => space switch
                {
                    IndexSpace.Type => map.Types[index],
                    IndexSpace.Function => map.Functions[index],
                    IndexSpace.Table => map.Tables[index],
                    IndexSpace.Memory => map.Memories[index],
                    IndexSpace.Global => map.Globals[index],
                    IndexSpace.Tag => map.Tags[index],
                    IndexSpace.Data => map.DataSegments[index],
                    IndexSpace.Element => map.Elements[index],
                    _ => throw new InvalidOperationException(),
                }, compact: true, resized),
            };
            remapped.CopyBranchHintsFrom(function);
            if (resized is { Count: > 0 })
                _resized[item.Index - ImportCount(m, Kind.Func)] = resized;
            return remapped;
        }

        private WasmFunction CombinedStart(List<uint> starts)
        {
            int typeIndex = FindEmptyFunctionType(_typeDefinitions);
            if (typeIndex < 0)
            {
                typeIndex = _types.Count;
                var type = new FuncType([], []);
                _types.Add(type);
                _typeDefinitions.Add(WasmModule.SynthesiseStandaloneFunctionType(type, typeIndex));
            }

            using var body = new MemoryStream();
            foreach (uint start in starts)
            {
                body.WriteByte(0x10);
                WasmEncoder.WriteU32Leb(body, start);
            }

            body.WriteByte(0x0B);
            return new WasmFunction { TypeIndex = typeIndex, Locals = [], Body = body.ToArray() };
        }

        private TableType RemapTable(ItemRef item)
        {
            var table = _modules[item.Module].Tables[item.Index - ImportCount(item.Module, Kind.Table)];
            var remapped = SectionMerger.RemapTableTypeIndices(table, t => MapType(item.Module, (int)t));
            return table.InitExpr.Length == 0
                ? remapped
                : remapped with { InitExpr = SectionMerger.RemapConstExprIndices(table.InitExpr, _maps[item.Module]) };
        }

        private WasmGlobal RemapGlobal(ItemRef item)
        {
            var global = _modules[item.Module].Globals[item.Index - ImportCount(item.Module, Kind.Global)];
            return new WasmGlobal
            {
                Type = SectionMerger.RemapGlobalTypeIndices(global.Type, t => MapType(item.Module, (int)t)),
                InitExpr = SectionMerger.RemapConstExprIndices(global.InitExpr, _maps[item.Module]),
            };
        }

        private Element RemapElement(int m, Element element)
        {
            var map = _maps[m];
            return new Element
            {
                Mode = element.Mode,
                TableIndex = element.Mode == ElementMode.Active ? (int)map.Tables[(uint)element.TableIndex] : element.TableIndex,
                HasExplicitTableIndex = element.HasExplicitTableIndex,
                OffsetExpr = element.OffsetExpr.Length == 0 ? element.OffsetExpr : SectionMerger.RemapConstExprIndices(element.OffsetExpr, map),
                FunctionIndices = [.. element.FunctionIndices.Select(f => (int)map.Functions[(uint)f])],
                DeclaredFunctionIndices = [.. element.DeclaredFunctionIndices.Select(f => (int)map.Functions[(uint)f])],
                ElementExpressions = [.. element.ElementExpressions.Select(e => SectionMerger.RemapConstExprIndices(e, map))],
                UsesExpressions = element.UsesExpressions,
                ElementType = SectionMerger.RemapExactRefType(element.ElementType, t => MapType(m, (int)t))!,
            };
        }

        private Data RemapData(int m, Data data) => new()
        {
            IsPassive = data.IsPassive,
            MemoryIndex = data.IsPassive ? data.MemoryIndex : (int)_maps[m].Memories[(uint)data.MemoryIndex],
            OffsetExpr = data.OffsetExpr.Length == 0 ? data.OffsetExpr : SectionMerger.RemapConstExprIndices(data.OffsetExpr, _maps[m]),
            InitData = data.InitData,
            IsReadOnly = data.IsReadOnly,
        };

        private CustomSection? BuildNameSection()
        {
            var names = new SortedDictionary<uint, string>();
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int m = 0; m < _modules.Length; m++)
            {
                Dictionary<int, string> moduleNames;
                try
                {
                    moduleNames = WasmNameSectionReader.ParseFunctionNames(_modules[m]);
                }
                catch (WasmFormatException)
                {
                    continue;
                }

                foreach (var (local, name) in moduleNames.OrderBy(p => p.Key))
                {
                    var item = new ItemRef(m, local);
                    if (!IsLiveRepresentative(Kind.Func, item) || !_assigned.TryGetValue((Kind.Func, item), out uint index))
                        continue;
                    names[index] = Unique(name);
                }
            }

            if (names.Count == 0)
                return null;

            using var subsection = new MemoryStream();
            WasmEncoder.WriteU32Leb(subsection, (uint)names.Count);
            foreach (var (index, name) in names)
            {
                WasmEncoder.WriteU32Leb(subsection, index);
                byte[] utf8 = Encoding.UTF8.GetBytes(name);
                WasmEncoder.WriteU32Leb(subsection, (uint)utf8.Length);
                subsection.Write(utf8);
            }

            using var content = new MemoryStream();
            content.WriteByte(1);
            WasmEncoder.WriteU32Leb(content, (uint)subsection.Length);
            content.Write(subsection.GetBuffer(), 0, (int)subsection.Length);
            return new CustomSection("name", content.ToArray());

            string Unique(string name)
            {
                if (used.Add(name))
                    return name;
                for (int n = used.Count; ; n++)
                {
                    string candidate = $"{name}_{n}";
                    if (used.Add(candidate))
                        return candidate;
                }
            }
        }

        // ── Per-kind helpers ──────────────────────────────────────────────────

        private int DefinedCount(int m, Kind kind) => kind switch
        {
            Kind.Func => _modules[m].Functions.Count,
            Kind.Table => _modules[m].Tables.Count,
            Kind.Memory => _modules[m].Memories.Count,
            Kind.Global => _modules[m].Globals.Count,
            Kind.Tag => _modules[m].Tags.Count,
            _ => 0,
        };

        private int FunctionTypeIndex(ItemRef item)
        {
            int imported = ImportCount(item.Module, Kind.Func);
            return item.Index < imported
                ? ((ImportDescriptor.Func)_importsByKind[item.Module][(int)Kind.Func][item.Index].Descriptor).TypeIndex
                : _modules[item.Module].Functions[item.Index - imported].TypeIndex;
        }

        private int TagTypeIndex(ItemRef item)
        {
            int imported = ImportCount(item.Module, Kind.Tag);
            return item.Index < imported
                ? ((ImportDescriptor.Tag)_importsByKind[item.Module][(int)Kind.Tag][item.Index].Descriptor).TypeIndex
                : _modules[item.Module].Tags[item.Index - imported].TypeIndex;
        }

        private GlobalType GlobalTypeOf(ItemRef item)
        {
            int imported = ImportCount(item.Module, Kind.Global);
            return item.Index < imported
                ? ((ImportDescriptor.Global)_importsByKind[item.Module][(int)Kind.Global][item.Index].Descriptor).Type
                : _modules[item.Module].Globals[item.Index - imported].Type;
        }

        private TableType TableTypeOf(ItemRef item)
        {
            int imported = ImportCount(item.Module, Kind.Table);
            return item.Index < imported
                ? ((ImportDescriptor.Table)_importsByKind[item.Module][(int)Kind.Table][item.Index].Descriptor).Type
                : _modules[item.Module].Tables[item.Index - imported];
        }

        private MemoryType MemoryTypeOf(ItemRef item)
        {
            int imported = ImportCount(item.Module, Kind.Memory);
            return item.Index < imported
                ? ((ImportDescriptor.Memory)_importsByKind[item.Module][(int)Kind.Memory][item.Index].Descriptor).Type
                : _modules[item.Module].Memories[item.Index - imported];
        }
    }

    private static Kind KindOf(ImportDescriptor descriptor) => descriptor switch
    {
        ImportDescriptor.Func => Kind.Func,
        ImportDescriptor.Table => Kind.Table,
        ImportDescriptor.Memory => Kind.Memory,
        ImportDescriptor.Global => Kind.Global,
        ImportDescriptor.Tag => Kind.Tag,
        _ => throw new LinkerException($"Unsupported import kind {descriptor.GetType().Name}."),
    };

    private static Kind KindOf(ExportDescriptor descriptor) => descriptor switch
    {
        ExportDescriptor.Func => Kind.Func,
        ExportDescriptor.Table => Kind.Table,
        ExportDescriptor.Memory => Kind.Memory,
        ExportDescriptor.Global => Kind.Global,
        ExportDescriptor.Tag => Kind.Tag,
        _ => throw new LinkerException($"Unsupported export kind {descriptor.GetType().Name}."),
    };

    private static int IndexOf(ExportDescriptor descriptor) => descriptor switch
    {
        ExportDescriptor.Func f => f.FunctionIndex,
        ExportDescriptor.Table t => t.TableIndex,
        ExportDescriptor.Memory m => m.MemoryIndex,
        ExportDescriptor.Global g => g.GlobalIndex,
        ExportDescriptor.Tag t => t.TagIndex,
        _ => throw new LinkerException($"Unsupported export kind {descriptor.GetType().Name}."),
    };
}

/// <summary>An index space an instruction immediate names.</summary>
internal enum IndexSpace
{
    Type,
    Function,
    Table,
    Memory,
    Global,
    Tag,
    Data,
    Element
}

/// <summary>
/// Re-encodes a function body (instructions only, no locals) with every index immediate passed
/// through a mapping. Labels and locals are left alone; everything else that names a module index
/// space — including block types, <c>try_table</c> catch tags, typed <c>select</c>, memory
/// indices in memargs and GC type immediates — is rewritten.
/// </summary>
/// <remarks>
/// By default a rewritten immediate keeps its original LEB128 width whenever the new value fits, so a body
/// whose indices only move (wasm-ld pads relocated indices to five bytes) keeps its exact length. In compact
/// mode every LEB128 immediate (indices, labels, locals, constants, memargs) is written at its minimal width,
/// as binaryen writes code, and the old→new offset breakpoints are reported so DWARF can follow the code.
/// </remarks>
internal static class BodyRewriter
{
    public static byte[] Rewrite(byte[] body, Func<IndexSpace, uint, uint> map) => Rewrite(body, map, compact: false, resized: null);

    /// <summary>
    /// Rewrites <paramref name="body"/>. When <paramref name="resized"/> is given, it receives the old and new
    /// extent of every instruction whose length changed, in order; offsets between them shift by the
    /// accumulated difference.
    /// </summary>
    public static byte[] Rewrite(byte[] body, Func<IndexSpace, uint, uint> map, bool compact, List<CodeAddressMap.Resized>? resized)
    {
        var output = new MemoryStream(body.Length + 16);
        var cursor = new InstructionCursor(body, WasmFeatures.All);
        var encoded = new MemoryStream();
        int copied = 0;
        int delta = 0;
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            encoded.SetLength(0);
            var reader = new ImmediateReader(body, instruction.ImmediateOffset);
            var writer = new Writer(encoded, body, instruction.ByteOffset, instruction.ImmediateOffset, compact);
            if (!Encode(instruction, ref reader, ref writer, map, compact))
                continue;

            output.Write(body, copied, instruction.ByteOffset - copied);
            int newStart = (int)output.Length;
            output.Write(encoded.GetBuffer(), 0, (int)encoded.Length);
            copied = cursor.Position;
            int newDelta = (int)output.Length - copied;
            if (newDelta != delta)
            {
                delta = newDelta;
                resized?.Add(new CodeAddressMap.Resized(instruction.ByteOffset, copied, newStart, (int)output.Length));
            }
        }

        if (copied == 0)
            return body;
        output.Write(body, copied, body.Length - copied);
        return output.ToArray();
    }

    /// <summary>Writes the re-encoded instruction; returns false when it has nothing to rewrite.</summary>
    private static bool Encode(DecodedInstruction instruction, ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map, bool compact)
    {
        var id = instruction.OpcodeId;
        byte op = id.Opcode;
        if (!id.IsPrefixed)
        {
            if (compact && op is WasmOpcode.Br or WasmOpcode.BrIf or WasmOpcode.BrTable or WasmOpcode.Rethrow or WasmOpcode.Delegate or WasmOpcode.LocalGet or WasmOpcode.LocalSet or WasmOpcode.LocalTee or WasmOpcode.I32Const or WasmOpcode.I64Const or WasmOpcode.BrOnNull or WasmOpcode.BrOnNonNull)
            {
                w.Opcode();
                switch (op)
                {
                    case 0x0E:
                    {
                        uint count = Copy(ref r, ref w);
                        for (uint i = 0; i <= count; i++)
                            Copy(ref r, ref w);
                        break;
                    }

                    case 0x41 or 0x42:
                        w.Signed(r.S64(out _), 0);
                        break;
                    default:
                        Copy(ref r, ref w);
                        break;
                }

                return true;
            }

            w.Opcode();
            switch (op)
            {
                case 0x02 or 0x03 or 0x04 or 0x06:
                    BlockType(ref r, ref w, map);
                    return true;
                case 0x1F:
                {
                    BlockType(ref r, ref w, map);
                    uint count = Copy(ref r, ref w);
                    for (uint i = 0; i < count; i++)
                    {
                        byte kind = r.Byte();
                        w.Byte(kind);
                        if (kind is 0x00 or 0x01)
                            Index(ref r, ref w, map, IndexSpace.Tag);
                        Copy(ref r, ref w);
                    }

                    return true;
                }

                case 0x07 or 0x08:
                    Index(ref r, ref w, map, IndexSpace.Tag);
                    return true;
                case 0x10 or 0x12 or 0xD2:
                    Index(ref r, ref w, map, IndexSpace.Function);
                    return true;
                case 0x14 or 0x15:
                    Index(ref r, ref w, map, IndexSpace.Type);
                    return true;
                case 0x11 or 0x13:
                    Index(ref r, ref w, map, IndexSpace.Type);
                    Index(ref r, ref w, map, IndexSpace.Table);
                    return true;
                case 0x23 or 0x24:
                    Index(ref r, ref w, map, IndexSpace.Global);
                    return true;
                case 0x25 or 0x26:
                    Index(ref r, ref w, map, IndexSpace.Table);
                    return true;
                case 0x3F or 0x40:
                    Index(ref r, ref w, map, IndexSpace.Memory);
                    return true;
                case >= 0x28 and <= 0x3E:
                    MemArg(ref r, ref w, map);
                    return true;
                case 0x1C:
                {
                    uint count = Copy(ref r, ref w);
                    for (uint i = 0; i < count; i++)
                        ValueType(ref r, ref w, map);
                    return true;
                }

                case 0xD0:
                    HeapType(ref r, ref w, map);
                    return true;
                default:
                    return false;
            }
        }

        uint sub = id.SubOpcode;
        switch (op)
        {
            case WasmOpcode.MiscPrefix:
                switch (sub)
                {
                    case 8:
                        return Prefixed(ref r, ref w, map, IndexSpace.Data, IndexSpace.Memory);
                    case 9:
                        return Prefixed(ref r, ref w, map, IndexSpace.Data);
                    case 10:
                        return Prefixed(ref r, ref w, map, IndexSpace.Memory, IndexSpace.Memory);
                    case 11:
                        return Prefixed(ref r, ref w, map, IndexSpace.Memory);
                    case 12:
                        return Prefixed(ref r, ref w, map, IndexSpace.Element, IndexSpace.Table);
                    case 13:
                        return Prefixed(ref r, ref w, map, IndexSpace.Element);
                    case 14:
                        return Prefixed(ref r, ref w, map, IndexSpace.Table, IndexSpace.Table);
                    case 15 or 16 or 17:
                        return Prefixed(ref r, ref w, map, IndexSpace.Table);
                    default:
                        return false;
                }

            case WasmOpcode.SimdPrefix:
            {
                bool memarg = sub <= 0x0B || sub is 0x5C or 0x5D;
                bool laneMemarg = sub is >= 0x54 and <= 0x5B;
                if (!memarg && !laneMemarg)
                    return false;
                w.Opcode();
                Copy(ref r, ref w);
                MemArg(ref r, ref w, map);
                if (laneMemarg)
                    w.Byte(r.Byte());
                return true;
            }

            case WasmOpcode.GcPrefix:
            {
                w.Opcode();
                Copy(ref r, ref w);
                switch (sub)
                {
                    case 0x00 or 0x01 or 0x06 or 0x07 or 0x0B or 0x0C or 0x0D or 0x0E or 0x10:
                        Index(ref r, ref w, map, IndexSpace.Type);
                        break;
                    case 0x02 or 0x03 or 0x04 or 0x05 or 0x08:
                        Index(ref r, ref w, map, IndexSpace.Type);
                        Copy(ref r, ref w);
                        break;
                    case 0x09 or 0x12:
                        Index(ref r, ref w, map, IndexSpace.Type);
                        Index(ref r, ref w, map, IndexSpace.Data);
                        break;
                    case 0x0A or 0x13:
                        Index(ref r, ref w, map, IndexSpace.Type);
                        Index(ref r, ref w, map, IndexSpace.Element);
                        break;
                    case 0x11:
                        Index(ref r, ref w, map, IndexSpace.Type);
                        Index(ref r, ref w, map, IndexSpace.Type);
                        break;
                    case 0x14 or 0x15 or 0x16 or 0x17:
                        HeapType(ref r, ref w, map);
                        break;
                    case 0x18 or 0x19:
                        w.Byte(r.Byte());
                        Copy(ref r, ref w);
                        HeapType(ref r, ref w, map);
                        HeapType(ref r, ref w, map);
                        break;
                }

                return true;
            }

            case WasmOpcode.StackSwitchingPrefix:
                throw new LinkerException(
                    $"Merging stack-switching instruction 0xE0:{sub:X} is not supported.");

            default:
                return false;
        }
    }

    private static bool Prefixed(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map, params IndexSpace[] spaces)
    {
        w.Opcode();
        Copy(ref r, ref w);
        foreach (var space in spaces)
            Index(ref r, ref w, map, space);
        return true;
    }

    private static void Index(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map, IndexSpace space)
    {
        ulong value = r.U64(out int width);
        w.Unsigned(map(space, (uint)value), width);
    }

    private static uint Copy(ref ImmediateReader r, ref Writer w)
    {
        ulong value = r.U64(out int width);
        w.Unsigned(value, width);
        return (uint)value;
    }

    private static void MemArg(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map)
    {
        // Without flag 0x40 a memarg implicitly names memory 0, which may move too.
        uint flags = (uint)r.U64(out int flagsWidth);
        int memoryWidth = 0;
        uint memory = map(IndexSpace.Memory, (flags & 0x40) != 0 ? (uint)r.U64(out memoryWidth) : 0);
        if (memory == 0)
        {
            w.Unsigned(flags & ~0x40u, flagsWidth);
        }
        else
        {
            w.Unsigned(flags | 0x40, flagsWidth);
            w.Unsigned(memory, memoryWidth);
        }

        ulong offset = r.U64(out int offsetWidth);
        w.Unsigned(offset, offsetWidth);
    }

    private static void BlockType(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map)
    {
        long blockType = r.S33(out int width);
        if (blockType >= 0)
        {
            w.Signed(map(IndexSpace.Type, (uint)blockType), width);
            return;
        }

        w.Signed(blockType, width);
        if (AbstractHeapTypeCodes.BlockTypeCarriesHeapType(blockType))
            HeapType(ref r, ref w, map);
    }

    private static void ValueType(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map)
    {
        byte type = r.Byte();
        w.Byte(type);
        if (type is 0x63 or 0x64)
            HeapType(ref r, ref w, map);
    }

    private static void HeapType(ref ImmediateReader r, ref Writer w, Func<IndexSpace, uint, uint> map)
    {
        long heapType = r.S33(out int width);
        w.Signed(heapType >= 0 ? map(IndexSpace.Type, (uint)heapType) : heapType, width);
    }

    private ref struct ImmediateReader(byte[] code, int position)
    {
        private int _pc = position;

        public byte Byte() => code[_pc++];

        public ulong U64(out int width)
        {
            int start = _pc;
            ulong result = DwarfReaderHelpers.ReadULEB128U64(code, ref _pc);
            width = _pc - start;
            return result;
        }

        public long S64(out int width)
        {
            int start = _pc;
            long result = DwarfReaderHelpers.ReadSLEB128(code, ref _pc);
            width = _pc - start;
            return result;
        }

        public long S33(out int width)
        {
            int start = _pc;
            long value = WasmLeb128.ReadS33(code, ref _pc);
            width = _pc - start;
            return value;
        }
    }

    /// <summary>Writes an instruction, padding each LEB128 to its original width when the value fits (unless compact).</summary>
    private ref struct Writer(MemoryStream output, byte[] code, int opcodeStart, int immediateStart, bool compact)
    {
        public readonly void Opcode() => output.Write(code, opcodeStart, immediateStart - opcodeStart);

        public readonly void Byte(byte value) => output.WriteByte(value);

        public readonly void Unsigned(ulong value, int width)
        {
            int minimum = 1;
            for (ulong v = value >> 7; v != 0; v >>= 7)
                minimum++;
            int bytes = compact ? minimum : Math.Max(minimum, width);
            for (int i = 0; i < bytes; i++)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                output.WriteByte(i < bytes - 1 ? (byte)(b | 0x80) : b);
            }
        }

        public readonly void Signed(long value, int width)
        {
            int minimum = 1;
            for (long v = value; v is < -64 or > 63; v >>= 7)
                minimum++;
            int bytes = compact ? minimum : Math.Max(minimum, width);
            for (int i = 0; i < bytes; i++)
            {
                byte b = (byte)(value & 0x7F);
                value >>= 7;
                output.WriteByte(i < bytes - 1 ? (byte)(b | 0x80) : b);
            }
        }
    }
}
