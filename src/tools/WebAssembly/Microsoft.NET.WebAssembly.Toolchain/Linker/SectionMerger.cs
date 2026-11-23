// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

using System.Collections.Generic;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Merges Wasm sections from multiple input modules into a single output module and can
/// emit the merged result as a binary <c>.wasm</c>.
/// </summary>
public sealed class SectionMerger
{
    /// <summary>Tool name recorded in the <c>processed-by</c> field of the producers section.</summary>
    public const string ProducerName = "Microsoft.NET.WebAssembly.Toolchain";

    private const uint WasmPageSize = 65536;

    /// <summary>
    /// Merges and deduplicates all function types from the input modules using structural
    /// signature equality, populating each module's type-index map as a side effect.
    /// </summary>
    public List<FuncType> MergeTypes(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps)
    {
        ValidateInputs(inputs, indexMaps);

        var deduper = new FuncTypeDeduplicator();
        var mergedTypes = new List<FuncType>();

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            var module = inputs[moduleIndex];
            for (int typeIndex = 0; typeIndex < module.Types.Count; typeIndex++)
            {
                uint mergedIndex = (uint)deduper.GetOrAdd(module.Types[typeIndex], mergedTypes);
                indexMaps[moduleIndex].Types[(uint)typeIndex] = mergedIndex;
            }
        }

        return mergedTypes;
    }

    /// <summary>
    /// Builds the merged <b>type-definition</b> space that runs in lockstep with
    /// <see cref="MergeTypes"/>'s compact output: entry <c>i</c> is the exact
    /// <see cref="TypeDefinition"/> of merged type <c>i</c>, with every type index it names rewritten
    /// through <paramref name="indexMaps"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Why this has to exist.</b> <see cref="MergeTypes"/> produces <see cref="FuncType"/>s, which
    /// are <em>compact</em>: every reference operand is erased to <c>funcref</c> or <c>externref</c>.
    /// A merged module built from those alone carries no exact type information at all, so
    /// <c>WasmModule.TypeDefinitions</c> came out empty — and every downstream guard that consults it
    /// silently passed. A struct-typed table survived the merge, was written as
    /// <c>(ref null 0)</c> against a type section that emits function types only, and re-read as a
    /// <em>function</em>-typed table. The output still validated, in nesm and in
    /// <c>wasm-tools validate -f all</c>, because type erasure only ever widens (GitHub #383).
    /// </para>
    /// <para>
    /// <b>Conflation is detected, not assumed away.</b> The deduplicator's key is the compact
    /// signature, so <c>(func (param (ref null $a)))</c> and <c>(func (param (ref null $b)))</c> hash
    /// to one entry however different <c>$a</c> and <c>$b</c> are. If two inputs land on one merged
    /// index with definitions that are not the same type, the merged space would be a lie and every
    /// canonical-equivalence answer computed over it would inherit the lie — so it throws instead.
    /// This is the affirmative check: it can fail, and it fails loudly.
    /// </para>
    /// <para>
    /// Returns a complete space: a merged slot claimed only by modules that record no type
    /// definitions is reconstituted from its compact signature, which is the whole truth for such a
    /// module. Leaving holes would make every guard that consults the space pass vacuously.
    /// </para>
    /// </remarks>
    public List<TypeDefinition> MergeTypeDefinitions(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        int mergedTypeCount)
    {
        ValidateInputs(inputs, indexMaps);

        var merged = new TypeDefinition?[mergedTypeCount];
        var compactFallback = new FuncType?[mergedTypeCount];
        var provenance = new (int Module, int TypeIndex)[mergedTypeCount];

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            var module = inputs[moduleIndex];
            var definitions = module.TypeDefinitions;
            var typeMap = indexMaps[moduleIndex].Types;

            if (definitions.Count != 0 && definitions.Count != module.Types.Count)
            {
                throw new LinkerException(
                    $"Input module {moduleIndex} has {module.Types.Count} compact types but " +
                    $"{definitions.Count} exact type definitions; its canonical provenance is malformed.");
            }

            for (int typeIndex = 0; typeIndex < module.Types.Count; typeIndex++)
            {
                if (!typeMap.TryGetValue((uint)typeIndex, out uint mergedIndex))
                    continue;
                if (mergedIndex >= (uint)mergedTypeCount)
                {
                    throw new LinkerException(
                        $"Type index map for input module {moduleIndex} sends local type {typeIndex} to merged " +
                        $"type {mergedIndex}, which is outside the merged type space of {mergedTypeCount} entries.");
                }

                if (definitions.Count == 0)
                {
                    // A module built programmatically from FuncTypes records no exact types. Its
                    // compact signature is then the whole truth. The standalone definition is
                    // cross-checked against any exact claimant and used only if none exists.
                    var compactType = module.Types[typeIndex];
                    var synthetic = WasmModule.SynthesiseStandaloneFunctionType(compactType, (int)mergedIndex);
                    if (merged[mergedIndex] is { } exact && !SameType(exact, synthetic))
                    {
                        var (om, ot) = provenance[mergedIndex];
                        throw new LinkerException(
                            $"Type deduplication conflated a compact-only standalone function type with a " +
                            $"different canonical type at merged index {mergedIndex}: input module {om} type {ot} " +
                            $"is '{Describe(exact)}' but input module {moduleIndex} type {typeIndex} is " +
                            $"'{Describe(synthetic)}'.");
                    }

                    compactFallback[mergedIndex] ??= compactType;
                    continue;
                }

                var rebased = RemapTypeDefinition(definitions[typeIndex], (int)mergedIndex, typeMap, moduleIndex);

                if (compactFallback[mergedIndex] is { } fallbackType)
                {
                    var synthetic = WasmModule.SynthesiseStandaloneFunctionType(fallbackType, (int)mergedIndex);
                    if (!SameType(synthetic, rebased))
                    {
                        throw new LinkerException(
                            $"Type deduplication conflated a compact-only standalone function type with a " +
                            $"different canonical type at merged index {mergedIndex}: input module {moduleIndex} " +
                            $"type {typeIndex} is '{Describe(rebased)}' but the compact-only type is " +
                            $"'{Describe(synthetic)}'.");
                    }
                }

                if (merged[mergedIndex] is { } existing)
                {
                    if (!SameType(existing, rebased))
                    {
                        var (om, ot) = provenance[mergedIndex];
                        throw new LinkerException(
                            $"Type deduplication conflated two different types at merged index {mergedIndex}: " +
                            $"input module {om} type {ot} is '{Describe(existing)}' but input module {moduleIndex} " +
                            $"type {typeIndex} is '{Describe(rebased)}'. Their compact signatures are equal because " +
                            "reference operands erase to funcref/externref, but the types are not the same type.");
                    }

                    continue;
                }

                merged[mergedIndex] = rebased;
                provenance[mergedIndex] = (moduleIndex, typeIndex);
            }
        }

        var result = new List<TypeDefinition>(mergedTypeCount);
        for (int i = 0; i < mergedTypeCount; i++)
        {
            if (merged[i] is { } definition)
            {
                result.Add(definition);
                continue;
            }

            if (compactFallback[i] is not { } compact)
            {
                throw new LinkerException(
                    $"Merged type {i} has no input type. The merged type list and the type index maps disagree, " +
                    "so no exact type could be resolved against this space.");
            }

            result.Add(WasmModule.SynthesiseStandaloneFunctionType(compact, i));
        }

        return result;
    }

    /// <summary>
    /// Canonical-shape equality for two <see cref="TypeDefinition"/>s already rebased into the same
    /// space. <c>RecGroup.Index</c> is a per-module ordinal that no type rule reads, so it is
    /// excluded; everything the subtype relation consults is compared.
    /// </summary>
    /// <remarks>
    /// The composite types are compared <b>structurally</b>. <c>CompositeType.Function</c> is a
    /// record whose members are <c>IReadOnlyList&lt;ValueType&gt;</c>, and record equality compares a
    /// list member by reference — so <c>==</c> reports two separately-read copies of the identical
    /// signature as different, and every type would look conflated. A comparison that answers "not
    /// equal" for equal inputs is as wrong as one that answers "equal" for different ones; it just
    /// fails in the direction that gets noticed.
    /// </remarks>
    private static bool SameType(TypeDefinition a, TypeDefinition b) =>
        a.IsFinal == b.IsFinal &&
        a.SuperTypeIndices.SequenceEqual(b.SuperTypeIndices) &&
        SameComposite(a.CompositeType, b.CompositeType) &&
        a.RecGroup.Count == b.RecGroup.Count &&
        a.RecGroup.StartTypeIndex == b.RecGroup.StartTypeIndex;

    private static bool SameComposite(CompositeType a, CompositeType b) => (a, b) switch
    {
        (CompositeType.Function fa, CompositeType.Function fb) =>
            fa.Parameters.SequenceEqual(fb.Parameters) && fa.Results.SequenceEqual(fb.Results),
        (CompositeType.Struct sa, CompositeType.Struct sb) => sa.Fields.SequenceEqual(sb.Fields),
        (CompositeType.Array aa, CompositeType.Array ab) => aa.Field == ab.Field,
        (CompositeType.Continuation ca, CompositeType.Continuation cb) =>
            ca.FunctionTypeIndex == cb.FunctionTypeIndex,
        _ => false,
    };

    private static string Describe(TypeDefinition definition) =>
        DescribeComposite(definition.CompositeType) +
        (definition.IsFinal ? "" : " (open)") +
        (definition.SuperTypeIndices.Count > 0 ? $" <: {string.Join(",", definition.SuperTypeIndices)}" : "");

    private static string DescribeComposite(CompositeType composite) => composite switch
    {
        CompositeType.Function f =>
            $"(func (param {string.Join(" ", f.Parameters.Select(p => p.Key))}) " +
            $"(result {string.Join(" ", f.Results.Select(r => r.Key))}))",
        CompositeType.Struct s => $"(struct {s.Fields.Count} field(s))",
        CompositeType.Array => "(array)",
        CompositeType.Continuation c => $"(cont {c.FunctionTypeIndex})",
        _ => composite.GetType().Name.ToLowerInvariant(),
    };

    /// <summary>
    /// Rewrites every type index a <see cref="TypeDefinition"/> names — its own index, its supertypes,
    /// its recursion group's start, and every <see cref="DefinedHeapType"/> reachable through its
    /// composite type — through <paramref name="typeMap"/>.
    /// </summary>
    private static TypeDefinition RemapTypeDefinition(
        TypeDefinition definition,
        int mergedIndex,
        Dictionary<uint, uint> typeMap,
        int moduleIndex)
    {
        int Map(int index) => (uint)index < uint.MaxValue && typeMap.TryGetValue((uint)index, out uint mapped)
            ? checked((int)mapped)
            : throw new LinkerException(
                $"Type definition in input module {moduleIndex} names type index {index}, which has no merged mapping.");

        return new TypeDefinition(
            mergedIndex,
            definition.IsFinal,
            [.. definition.SuperTypeIndices.Select(Map)],
            RemapCompositeType(definition.CompositeType, Map),
            definition.RecGroup with { StartTypeIndex = Map(definition.RecGroup.StartTypeIndex) });
    }

    private static CompositeType RemapCompositeType(CompositeType composite, Func<int, int> map) =>
        composite switch
        {
            CompositeType.Function f => new CompositeType.Function(
                [.. f.Parameters.Select(p => RemapValueType(p, map))],
                [.. f.Results.Select(r => RemapValueType(r, map))]),
            CompositeType.Struct s => new CompositeType.Struct([.. s.Fields.Select(field => RemapFieldType(field, map))]),
            CompositeType.Array a => new CompositeType.Array(RemapFieldType(a.Field, map)),
            CompositeType.Continuation c => new CompositeType.Continuation(map(c.FunctionTypeIndex)),
            _ => composite,
        };

    private static FieldType RemapFieldType(FieldType field, Func<int, int> map) =>
        field.StorageType is StorageType.Value value
            ? field with { StorageType = new StorageType.Value(RemapValueType(value.Type, map)) }
            : field;

    private static Microsoft.NET.WebAssembly.Types.ValueType RemapValueType(Microsoft.NET.WebAssembly.Types.ValueType type, Func<int, int> map) =>
        type is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined } reference
            ? reference with { HeapType = new DefinedHeapType(map(defined.TypeIndex)) }
            : type;

    /// <summary>
    /// Merges imports across all input modules, deduping entries with the same module/name/kind
    /// and compatible descriptor. Per-kind import index maps are written into <paramref name="indexMaps"/>.
    /// </summary>
    /// <remarks>
    /// Requires <see cref="MergeTypes"/> to have run first: every descriptor that names a type index —
    /// a function's or tag's <c>TypeIndex</c>, and a table's exact element type — is rebased through
    /// <paramref name="indexMaps"/> before it is compared or emitted.
    /// </remarks>
    public ImportMergeResult MergeImports(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        IReadOnlyList<TypeDefinition>? mergedTypeDefinitions = null)
    {
        ValidateInputs(inputs, indexMaps);

        // The merged type-definition space that table element types are compared in. Built from the
        // same index maps MergeTypes populated, so it names exactly the indices the rebased
        // descriptors do. Callers that already built it pass it in rather than paying twice.
        if (mergedTypeDefinitions is null)
        {
            int mergedTypeCount = 0;
            foreach (var map in indexMaps)
                foreach (uint mergedIndex in map.Types.Values)
                    mergedTypeCount = Math.Max(mergedTypeCount, checked((int)mergedIndex) + 1);
            mergedTypeDefinitions = MergeTypeDefinitions(inputs, indexMaps, mergedTypeCount);
        }

        var mergedRelation = mergedTypeDefinitions.Count > 0 ? new SubtypeRelation(mergedTypeDefinitions) : null;

        var mergedImports = new List<Import>();
        var mergedEntries = new Dictionary<ImportKey, MergedImportEntry>();
        var importShapes = new Dictionary<(string ModuleName, string Name, Type Kind), ImportDescriptor>();
        var tagImportOrigins = new Dictionary<(string ModuleName, string Name, Type Kind), (int ModuleIndex, int TypeIndex)>();

        uint mergedFunctionImportCount = 0;
        uint mergedGlobalImportCount = 0;
        uint mergedTableImportCount = 0;
        uint mergedMemoryImportCount = 0;
        uint mergedTagImportCount = 0;

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint functionImportIndex = 0;
            uint globalImportIndex = 0;
            uint tableImportIndex = 0;
            uint memoryImportIndex = 0;
            uint tagImportIndex = 0;

            foreach (var import in inputs[moduleIndex].Imports)
            {
                Import remappedImport = RemapImport(import, indexMaps[moduleIndex]);
                var shapeKey = (remappedImport.ModuleName, remappedImport.Name, remappedImport.Descriptor.GetType());
                if (import.Descriptor is ImportDescriptor.Tag sourceTag)
                {
                    if (tagImportOrigins.TryGetValue(shapeKey, out var origin))
                    {
                        bool originResolved = inputs[origin.ModuleIndex]
                            .TryGetCanonicalTypeDefinitions(out var originTypes);
                        bool currentResolved = inputs[moduleIndex]
                            .TryGetCanonicalTypeDefinitions(out var currentTypes);
                        if (!originResolved ||
                            !currentResolved ||
                            !SubtypeRelation.AreEquivalentTypeIndices(
                                originTypes,
                                origin.TypeIndex,
                                currentTypes,
                                sourceTag.TypeIndex))
                        {
                            throw new LinkerException(
                                $"Import '{remappedImport.ModuleName}.{remappedImport.Name}' was seen with incompatible tag signatures.");
                        }
                    }

                    tagImportOrigins.TryAdd(shapeKey, (moduleIndex, sourceTag.TypeIndex));
                }

                if (importShapes.TryGetValue(shapeKey, out var existingDescriptor))
                {
                    var merged = MergeImportDescriptors(existingDescriptor, remappedImport.Descriptor,
                        remappedImport.ModuleName, remappedImport.Name, mergedRelation);
                    importShapes[shapeKey] = merged;
                    // Update the already-emitted import with the merged descriptor
                    var existingKey = new ImportKey(remappedImport.ModuleName, remappedImport.Name, existingDescriptor);
                    if (mergedEntries.TryGetValue(existingKey, out var existingEntry))
                    {
                        mergedImports[existingEntry.SectionIndex] = new Import(
                            remappedImport.ModuleName, remappedImport.Name, merged);
                        // Re-key if the descriptor changed
                        if (!Equals(existingDescriptor, merged))
                        {
                            mergedEntries.Remove(existingKey);
                            mergedEntries[new ImportKey(remappedImport.ModuleName, remappedImport.Name, merged)] = existingEntry;
                        }
                    }
                    remappedImport = new Import(remappedImport.ModuleName, remappedImport.Name, merged);
                }
                else
                {
                    importShapes[shapeKey] = remappedImport.Descriptor;
                }

                var key = new ImportKey(remappedImport.ModuleName, remappedImport.Name, remappedImport.Descriptor);
                if (!mergedEntries.TryGetValue(key, out var mergedEntry))
                {
                    uint kindIndex = remappedImport.Descriptor switch
                    {
                        ImportDescriptor.Func => mergedFunctionImportCount++,
                        ImportDescriptor.Global => mergedGlobalImportCount++,
                        ImportDescriptor.Table => mergedTableImportCount++,
                        ImportDescriptor.Memory => mergedMemoryImportCount++,
                        ImportDescriptor.Tag => mergedTagImportCount++,
                        _ => throw new LinkerException($"Unsupported import descriptor type '{remappedImport.Descriptor.GetType().Name}'."),
                    };

                    mergedEntry = new MergedImportEntry(mergedImports.Count, kindIndex);
                    mergedEntries[key] = mergedEntry;
                    mergedImports.Add(remappedImport);
                }

                switch (remappedImport.Descriptor)
                {
                    case ImportDescriptor.Func:
                        indexMaps[moduleIndex].Functions[functionImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Global:
                        indexMaps[moduleIndex].Globals[globalImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Table:
                        indexMaps[moduleIndex].Tables[tableImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Memory:
                        indexMaps[moduleIndex].Memories[memoryImportIndex++] = mergedEntry.KindIndex;
                        break;
                    case ImportDescriptor.Tag:
                        indexMaps[moduleIndex].Tags[tagImportIndex++] = mergedEntry.KindIndex;
                        break;
                }
            }
        }

        return new ImportMergeResult
        {
            Imports = mergedImports,
            FunctionImportCount = mergedFunctionImportCount,
            GlobalImportCount = mergedGlobalImportCount,
            TableImportCount = mergedTableImportCount,
            MemoryImportCount = mergedMemoryImportCount,
            TagImportCount = mergedTagImportCount,
        };
    }

    /// <summary>
    /// Builds the merged output module from already-merged type/import sections and populated index maps.
    /// </summary>
    public WasmModule BuildMergedModule(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        IReadOnlyList<FuncType> mergedTypes,
        IReadOnlyList<Import> mergedImports,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols = null,
        LinkerOptions? options = null,
        DceLiveSet? liveSets = null,
        IReadOnlyList<TypeDefinition>? mergedTypeDefinitions = null)
    {
        ValidateInputs(inputs, indexMaps);
        ArgumentNullException.ThrowIfNull(mergedTypes);
        ArgumentNullException.ThrowIfNull(mergedImports);

        options ??= new LinkerOptions();

        var imports = FilterImports(mergedImports, liveSets);
        var functions = MergeFunctions(inputs, indexMaps, liveSets);
        var globals = MergeGlobals(inputs, indexMaps, liveSets);
        var tables = MergeTables(inputs, indexMaps);
        var memories = MergeMemories(inputs);

        // The exact type space that runs alongside mergedTypes. Without it the merged module reports
        // an empty TypeDefinitions list, and every writer-side guard that consults it — including the
        // one that refuses to emit a table element type naming a struct — passes vacuously on an
        // empty list rather than on a correct one (GitHub #383).
        //
        // The caller that ran DCE passes the exact space it compacted alongside
        // mergedTypes; only the no-DCE path rebuilds it here, where the maps are still exact.
        var typeDefinitions = mergedTypeDefinitions is not null
            ? [.. mergedTypeDefinitions]
            : MergeTypeDefinitions(inputs, indexMaps, mergedTypes.Count);

        // wasm64 (memory64/table64) linking is not yet supported (#88): reject up front — before any
        // capacity expansion or linker-option rewriting (ApplyMemory/TableOptions) that casts limits
        // to uint and reconstructs Limits without Is64, which would erase the flag before the
        // writer-level guards ever run. Covers imported AND defined memories/tables.
        RejectWasm64Linking(imports, memories, tables);

        var tags = MergeTags(inputs, indexMaps, liveSets);
        var dataSegments = MergeDataSegments(inputs, indexMaps, liveSets, options);
        if (options.CoalesceSegments)
            dataSegments = CoalesceDataSegments(dataSegments, options.CoalesceGapThreshold);
        var elements = MergeElements(inputs, indexMaps, liveSets);

        // ── TableBase: offset element segments for table 0 ─────────────────────────
        if (options.TableBase.HasValue)
            ApplyTableBase(elements, options.TableBase.Value);

        EnsureMemoryCapacity(imports, memories, dataSegments);
        EnsureTableCapacity(imports, tables, elements);

        ApplyMemoryOptions(imports, memories, options);
        ApplyTableOptions(imports, tables, options);

        var exports = MergeExports(inputs, indexMaps, resolvedSymbols, options);

        if (options.ExportMemory)
        {
            // Memory index 0 — whether imported or local
            if (!exports.Values.Any(e => e.Descriptor is ExportDescriptor.Memory m && m.MemoryIndex == 0
                && e.Name == options.ExportMemoryName))
            {
                exports[options.ExportMemoryName] = new Export(options.ExportMemoryName, new ExportDescriptor.Memory(0));
            }
        }

        if (options.ExportTable)
        {
            const string tableName = "__indirect_function_table";
            if (!exports.Values.Any(e => e.Descriptor is ExportDescriptor.Table t && t.TableIndex == 0
                && e.Name == tableName))
            {
                exports[tableName] = new Export(tableName, new ExportDescriptor.Table(0));
            }
        }

        int? startFunction = ResolveStartFunction(inputs, indexMaps, exports, options);

        PatchLayoutGlobals(globals, dataSegments, imports, resolvedSymbols, options);

        // ── Custom sections: debug info + name ─────────────────────────────────────
        var customSections = new List<CustomSection>();

        if (!options.StripDebug && !options.StripAll)
            customSections.AddRange(MergeDebugSections(inputs, indexMaps));

        if (!options.NoNameSection && !options.StripAll)
        {
            var nameSection = BuildNameSection(resolvedSymbols);
            if (nameSection is not null)
                customSections.Add(nameSection);
        }

        // ── Producers section ──────────────────────────────────────────────────────
        var producers = options.StripAll ? null : MergeProducers(inputs);

        return new WasmModule
        {
            Types = [.. mergedTypes],
            TypeDefinitions = typeDefinitions,
            Imports = imports,
            Functions = functions,
            Globals = globals,
            Tables = tables,
            Memories = memories,
            Tags = tags,
            DataSegments = dataSegments,
            Elements = elements,
            Exports = exports,
            StartFunction = startFunction,
            CustomSections = customSections,
            ProducersSection = producers,
        };
    }

    /// <summary>Concatenates all function section entries, remapping type indices and cloning bodies.</summary>
    public List<WasmFunction> MergeFunctions(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Functions.Count;
        var result = new List<WasmFunction>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localFuncImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Func) localFuncImports++;

            for (int j = 0; j < inputs[moduleIndex].Functions.Count; j++)
            {
                uint localIdx = localFuncImports + (uint)j;
                if (liveSets?.DeadFunctionsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var function = inputs[moduleIndex].Functions[j];
                var mergedFunction = new WasmFunction
                {
                    TypeIndex = checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Types, (uint)function.TypeIndex, "type")),
                    Locals = [.. function.Locals],
                    ExactLocals =
                    [
                        .. function.ExactLocals.Select(local =>
                            RemapExactRefType(
                                local,
                                typeIndex => ResolveMappedIndex(
                                    indexMaps[moduleIndex].Types, typeIndex, "type"))!),
                    ],
                    Body = [.. function.Body],
                };
                mergedFunction.CopyBranchHintsFrom(function);
                result.Add(mergedFunction);
            }
        }

        return result;
    }

    /// <summary>Concatenates all globals, cloning init-expression bytecode.</summary>
    /// <remarks>
    /// A global's <em>content type</em> is a type index whenever it is a typed reference —
    /// <c>(global (ref null $t))</c> — and <c>WriteGlobalType</c> emits it verbatim. Copying
    /// <c>global.Type</c> through unchanged left that index naming whichever type occupies the slot
    /// after the merge and after dead-code elimination compacts the space: the same silent retyping
    /// as a table's element type, one index space over (GitHub #383).
    /// </remarks>
    public List<WasmGlobal> MergeGlobals(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap>? indexMaps = null, DceLiveSet? liveSets = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        if (indexMaps is not null)
            ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Globals.Count;
        var result = new List<WasmGlobal>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localGlobalImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Global) localGlobalImports++;

            for (int j = 0; j < inputs[moduleIndex].Globals.Count; j++)
            {
                uint localIdx = localGlobalImports + (uint)j;
                if (liveSets?.DeadGlobalsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var global = inputs[moduleIndex].Globals[j];
                result.Add(new WasmGlobal
                {
                    Type = indexMaps is null
                        ? global.Type
                        : RemapGlobalTypeIndices(
                            global.Type,
                            typeIndex => ResolveMappedIndex(indexMaps[moduleIndex].Types, typeIndex, "type")),
                    InitExpr = indexMaps is null
                        ? [.. global.InitExpr]
                        : RemapConstExprIndices(global.InitExpr, indexMaps[moduleIndex]),
                });
            }
        }

        return result;
    }

    /// <summary>Concatenates all table declarations, rebased into the merged index spaces.</summary>
    /// <remarks>
    /// <para>
    /// A table declaration names the merged index spaces in <em>two</em> places, and both have to be
    /// rewritten or the output means something other than the input.
    /// </para>
    /// <para>
    /// The <b>initializer expression</b> is a constant expression exactly like a global's, so a
    /// <c>ref.func</c>, <c>global.get</c> or <c>ref.null &lt;typeidx&gt;</c> that still names the input
    /// module's index fills the table from the wrong module once they are concatenated. Before
    /// GitHub #383 the writer dropped these expressions entirely, which hid the omission; now that
    /// the extended form is emitted, an unremapped index is a wrong answer rather than a missing one.
    /// </para>
    /// <para>
    /// The <b>element type</b> does too, whenever it is a defined reference such as
    /// <c>(ref null $t)</c>. That one is easy to miss because it does not live in a byte array and is
    /// not an "expression" — but a <see cref="DefinedHeapType"/> is a type index, the writer emits it
    /// verbatim, and an unrebased index names whichever type happens to sit there after the merge.
    /// The result still validates, because a wrong-but-existing type index is not a structural error.
    /// </para>
    /// </remarks>
    public List<TableType> MergeTables(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap>? indexMaps = null)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Tables.Count;
        var result = new List<TableType>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            foreach (var table in inputs[moduleIndex].Tables)
                result.Add(indexMaps is null ? table : RebaseTable(table, indexMaps[moduleIndex]));
        }

        return result;
    }

    /// <summary>Rewrites one table's element type and initializer into the merged index spaces.</summary>
    private static TableType RebaseTable(TableType table, IndexMap indexMap)
    {
        var rebased = RemapTableTypeIndices(
            table, typeIndex => ResolveMappedIndex(indexMap.Types, typeIndex, "type"));
        byte[] rebasedInit = table.InitExpr.Length == 0
            ? table.InitExpr
            : RemapConstExprIndices(table.InitExpr, indexMap);

        if (ReferenceEquals(rebased, table) && ReferenceEquals(rebasedInit, table.InitExpr))
            return table;

        return rebased with { InitExpr = rebasedInit };
    }

    /// <summary>
    /// Rebases a <see cref="DefinedHeapType"/> inside an exact reference type through the merged type
    /// space, returning the input unchanged when it names no type index.
    /// </summary>
    internal static Microsoft.NET.WebAssembly.Types.ValueType? RemapExactRefType(
        Microsoft.NET.WebAssembly.Types.ValueType? exactType, Func<uint, uint> mapType)
    {
        if (exactType is not Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined } reference)
            return exactType;

        uint remapped = mapType((uint)defined.TypeIndex);
        return remapped == (uint)defined.TypeIndex
            ? exactType
            : reference with { HeapType = new DefinedHeapType(checked((int)remapped)) };
    }

    /// <summary>Concatenates all memory declarations.</summary>
    public List<MemoryType> MergeMemories(IReadOnlyList<WasmModule> inputs)
    {
        ArgumentNullException.ThrowIfNull(inputs);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Memories.Count;
        var result = new List<MemoryType>(totalCount);
        foreach (var module in inputs)
            result.AddRange(module.Memories);
        return result;
    }

    /// <summary>Concatenates all tag entries, remapping their type indices.</summary>
    public List<Tag> MergeTags(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Tags.Count;
        var result = new List<Tag>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            uint localTagImports = 0;
            foreach (var import in inputs[moduleIndex].Imports)
                if (import.Descriptor is ImportDescriptor.Tag) localTagImports++;

            for (int j = 0; j < inputs[moduleIndex].Tags.Count; j++)
            {
                uint localIdx = localTagImports + (uint)j;
                if (liveSets?.DeadTagsByModule?[moduleIndex].Contains(localIdx) == true)
                    continue;

                var tag = inputs[moduleIndex].Tags[j];
                result.Add(new Tag(
                    checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Types, (uint)tag.TypeIndex, "type"))));
            }
        }

        return result;
    }

    /// <summary>
    /// Concatenates data segments, laying active segments out sequentially in linear memory and
    /// recording each input segment's base offset in <see cref="IndexMap.DataSegmentOffsets"/>.
    /// </summary>
    public List<Data> MergeDataSegments(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null, LinkerOptions? options = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalDataCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalDataCount += inputs[i].DataSegments.Count;
        var result = new List<Data>(totalDataCount);
        var memoryCursors = new Dictionary<int, uint>();
        uint dataStart = ComputeDataStart(options);

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            var module = inputs[moduleIndex];
            for (int segmentIndex = 0; segmentIndex < module.DataSegments.Count; segmentIndex++)
            {
                if (liveSets?.DeadDataSegsByModule?[moduleIndex].Contains((uint)segmentIndex) == true)
                    continue;

                var segment = module.DataSegments[segmentIndex];
                if (segment.IsPassive)
                {
                    result.Add(new Data
                    {
                        IsPassive = true,
                        MemoryIndex = -1,
                        OffsetExpr = [.. segment.OffsetExpr],
                        InitData = [.. segment.InitData],
                    });
                    continue;
                }

                if (segment.MemoryIndex < 0)
                    throw new LinkerException($"Active data segment {segmentIndex} in module {moduleIndex} does not reference a valid memory index.");

                int mappedMemoryIndex = checked((int)ResolveMappedIndex(
                    indexMaps[moduleIndex].Memories, (uint)segment.MemoryIndex, "memory"));
                uint alignment = GetSegmentAlignment(module, segmentIndex);
                uint nextOffset = memoryCursors.TryGetValue(mappedMemoryIndex, out var existingOffset)
                    ? existingOffset
                    : (mappedMemoryIndex == 0 ? dataStart : 0u);
                uint baseOffset = AlignUp(nextOffset, alignment);

                indexMaps[moduleIndex].DataSegmentOffsets[(uint)segmentIndex] = baseOffset;
                memoryCursors[mappedMemoryIndex] = checked(baseOffset + (uint)segment.InitData.Length);

                result.Add(new Data
                {
                    IsPassive = false,
                    MemoryIndex = mappedMemoryIndex,
                    OffsetExpr = BuildI32ConstExpr(baseOffset),
                    InitData = [.. segment.InitData],
                    IsReadOnly = IsReadOnlySegment(module, segmentIndex),
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Coalesces adjacent or overlapping active data segments targeting the same memory into
    /// fewer, larger segments. Passive segments are passed through unchanged.
    /// Gaps of at most <paramref name="gapThreshold"/> bytes between segments are zero-filled
    /// rather than emitting a new segment header.
    /// </summary>
    public static List<Data> CoalesceDataSegments(List<Data> segments, int gapThreshold = 32)
    {
        if (segments.Count <= 1)
            return segments;

        var result = new List<Data>(segments.Count);

        // Separate passive segments — they have no offset and cannot be coalesced.
        var passive = new List<Data>();
        var active = new List<Data>();
        foreach (var seg in segments)
        {
            if (seg.IsPassive)
                passive.Add(seg);
            else
                active.Add(seg);
        }

        // Group active segments by (memory index, isReadOnly).
        var byGroup = new Dictionary<(int MemoryIndex, bool IsReadOnly), List<Data>>();
        foreach (var seg in active)
        {
            var key = (seg.MemoryIndex, seg.IsReadOnly);
            if (!byGroup.TryGetValue(key, out var list))
            {
                list = [];
                byGroup[key] = list;
            }
            list.Add(seg);
        }

        // Collect coalesced output per group, then sort: ro before rw, then by memory index.
        var coalescedGroups = new List<(int MemoryIndex, bool IsReadOnly, List<Data> Segments, List<Data> Undecodable)>();
        foreach (var ((memIdx, isRo), segs) in byGroup)
        {
            // Sort by offset. Segments whose offsets cannot be decoded go last (unchanged).
            var decoded = new List<(uint Offset, Data Seg)>(segs.Count);
            var undecodable = new List<Data>();
            foreach (var seg in segs)
            {
                if (TryReadConstI32Expr(seg.OffsetExpr, out uint off))
                    decoded.Add((off, seg));
                else
                    undecodable.Add(seg);
            }

            decoded.Sort(static (a, b) => a.Offset.CompareTo(b.Offset));

            // Walk sorted list, merging adjacent/overlapping/close segments.
            var coalesced = new List<Data>();
            int i = 0;
            while (i < decoded.Count)
            {
                uint currentStart = decoded[i].Offset;
                uint currentEnd   = currentStart + (uint)decoded[i].Seg.InitData.Length;
                using var builder = new MemoryStream(decoded[i].Seg.InitData.Length);
                builder.Write(decoded[i].Seg.InitData);

                int j = i + 1;
                while (j < decoded.Count)
                {
                    uint nextStart = decoded[j].Offset;
                    uint nextEnd   = nextStart + (uint)decoded[j].Seg.InitData.Length;

                    long gap = (long)nextStart - (long)currentEnd;
                    if (gap < 0)
                    {
                        // Overlapping: next segment starts inside the current buffer.
                        // Seek to the overlap point and overwrite; extend if it runs past currentEnd.
                        long bufOffset = (long)nextStart - (long)currentStart;
                        builder.Seek(bufOffset, SeekOrigin.Begin);
                        builder.Write(decoded[j].Seg.InitData);
                        builder.Seek(0, SeekOrigin.End);
                        if (nextEnd > currentEnd)
                            currentEnd = nextEnd;
                    }
                    else if (gap <= gapThreshold)
                    {
                        // Adjacent or close: zero-fill the gap and append.
                        for (long g = 0; g < gap; g++)
                            builder.WriteByte(0);
                        builder.Write(decoded[j].Seg.InitData);
                        currentEnd = nextEnd;
                    }
                    else
                    {
                        break;
                    }
                    j++;
                }

                coalesced.Add(new Data
                {
                    IsPassive   = false,
                    MemoryIndex = memIdx,
                    OffsetExpr  = BuildI32ConstExpr(currentStart),
                    InitData    = builder.ToArray(),
                    IsReadOnly  = isRo,
                });
                i = j;
            }

            coalescedGroups.Add((memIdx, isRo, coalesced, undecodable));
        }

        // Emit read-only segments first (matching wasm-ld output order), then read-write.
        coalescedGroups.Sort(static (a, b) =>
        {
            int roComp = b.IsReadOnly.CompareTo(a.IsReadOnly); // true sorts before false
            return roComp != 0 ? roComp : a.MemoryIndex.CompareTo(b.MemoryIndex);
        });

        foreach (var (_, _, segs, undecodable) in coalescedGroups)
        {
            result.AddRange(segs);
            result.AddRange(undecodable);
        }

        result.AddRange(passive);
        return result;
    }

    /// <summary>
    /// Concatenates element segments, preserving their binary form while remapping every index in
    /// table targets, element types, offsets, funcidx vectors, and expression vectors.
    /// </summary>
    public List<Element> MergeElements(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps, DceLiveSet? liveSets = null)
    {
        ValidateInputs(inputs, indexMaps);

        int totalCount = 0;
        for (int i = 0; i < inputs.Count; i++) totalCount += inputs[i].Elements.Count;
        var result = new List<Element>(totalCount);
        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            for (int j = 0; j < inputs[moduleIndex].Elements.Count; j++)
            {
                if (liveSets?.DeadElementsByModule?[moduleIndex].Contains((uint)j) == true)
                    continue;

                var element = inputs[moduleIndex].Elements[j];
                var remappedIndices = new List<int>(element.FunctionIndices.Count);
                for (int k = 0; k < element.FunctionIndices.Count; k++)
                {
                    int functionIndex = element.FunctionIndices[k];
                    if (functionIndex < 0)
                    {
                        if (functionIndex == -1)
                        {
                            remappedIndices.Add(functionIndex);
                        }
                        else
                        {
                            int globalIndex = -(functionIndex + 2);
                            uint remappedGlobal = ResolveMappedIndex(
                                indexMaps[moduleIndex].Globals, (uint)globalIndex, "global");
                            remappedIndices.Add(checked(-(int)remappedGlobal - 2));
                        }
                        continue;
                    }

                    remappedIndices.Add(checked((int)ResolveMappedIndex(
                        indexMaps[moduleIndex].Functions, (uint)functionIndex, "function")));
                }

                var remappedDeclarations = element.DeclaredFunctionIndices
                    .Select(functionIndex => functionIndex < 0
                        ? functionIndex
                        : checked((int)ResolveMappedIndex(
                            indexMaps[moduleIndex].Functions, (uint)functionIndex, "function")))
                    .ToList();

                var remappedExpressions = element.ElementExpressions
                    .Select(expression => RemapConstExprIndices(expression, indexMaps[moduleIndex]))
                    .ToList();

                int remappedTableIndex = element.TableIndex >= 0
                    ? checked((int)ResolveMappedIndex(indexMaps[moduleIndex].Tables, (uint)element.TableIndex, "table"))
                    : element.TableIndex;

                result.Add(new Element
                {
                    Mode = element.Mode,
                    TableIndex = remappedTableIndex,
                    OffsetExpr = RemapConstExprIndices(element.OffsetExpr, indexMaps[moduleIndex]),
                    FunctionIndices = remappedIndices,
                    DeclaredFunctionIndices = remappedDeclarations,
                    ElementExpressions = remappedExpressions,
                    UsesExpressions = element.UsesExpressions || element.ElementExpressions.Count > 0,
                    HasExplicitTableIndex = element.HasExplicitTableIndex,
                    ElementType = RemapExactRefType(
                        element.ElementType,
                        typeIndex => ResolveMappedIndex(indexMaps[moduleIndex].Types, typeIndex, "type"))!,
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Builds the final export section from resolved symbols that carry the export flag,
    /// then preserves any explicit Wasm exports from the input modules that are not already present.
    /// </summary>
    public OrderedDictionary<string, Export> MergeExports(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols,
        LinkerOptions options)
    {
        ValidateInputs(inputs, indexMaps);
        ArgumentNullException.ThrowIfNull(options);

        var result = new OrderedDictionary<string, Export>();
        var extraExports = options.Exports is { Count: > 0 }
            ? new HashSet<string>(options.Exports, StringComparer.Ordinal)
            : null;

        // Merge ExportIfDefined names that exist in the resolved symbol table
        if (options.ExportIfDefined is { Count: > 0 } && resolvedSymbols is not null)
        {
            foreach (var name in options.ExportIfDefined)
            {
                if (resolvedSymbols.TryGetValue(name, out var sym) &&
                    !sym.IsUndefined &&
                    sym.MergedIndex.HasValue &&
                    TryCreateExportFromSymbol(sym, out var export))
                {
                    result.TryAdd(name, export);
                }
            }
        }

        if (resolvedSymbols is not null)
        {
            foreach (var symbol in resolvedSymbols.Values)
            {
                if (!ShouldExportSymbol(symbol, extraExports, options.ExportAll) ||
                    !TryCreateExportFromSymbol(symbol, out var export))
                {
                    continue;
                }

                result.TryAdd(symbol.Name, export);
            }
        }

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            foreach (var (name, export) in inputs[moduleIndex].Exports)
            {
                if (result.ContainsKey(name))
                    continue;

                result[name] = new Export(name, RemapExportDescriptor(export.Descriptor, indexMaps[moduleIndex]));
            }
        }

        return result;
    }

    /// <summary>
    /// Encodes a merged <see cref="WasmModule"/> as a WebAssembly binary without applying semantic
    /// validation. This is a producer boundary; validate the bytes before runtime loading.
    /// </summary>
    /// <remarks>The output array is the only module-sized allocation: it is sized exactly and written once.</remarks>
    public byte[] EmitBinary(WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        return PrepareBinary(module).ToArray();
    }

    /// <summary>
    /// Writes a merged <see cref="WasmModule"/> as a WebAssembly binary without applying semantic
    /// validation. This is a producer boundary; validate the bytes before runtime loading.
    /// </summary>
    /// <remarks>
    /// The module is encoded before anything is written, so an encoding error leaves <paramref name="output"/>
    /// untouched. Code, data and custom-section content is then copied from the model straight to
    /// <paramref name="output"/>, which only needs to be writable.
    /// </remarks>
    public void WriteBinary(Stream output, WasmModule module)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(module);
        PrepareBinary(module).WriteTo(output);
    }

    /// <summary>
    /// Encodes everything about <paramref name="module"/> except its bulk content (function bodies, data and custom
    /// section payloads), which is written later straight from the model. Every encoding error is raised here.
    /// </summary>
    internal static EncodedModule PrepareBinary(WasmModule module)
    {
        try
        {
            return new(module);
        }
        catch (ArgumentException ex)
        {
            throw new LinkerException($"Cannot encode module entries: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// A module ready to be written: its small sections encoded, every function's locals encoded and the size of
    /// each section known, so the output's <see cref="Length"/> is exact before a byte is written.
    /// </summary>
    internal sealed class EncodedModule
    {
        private readonly WasmModule _module;

        // Small sections, function locals, data segment headers and custom section names, back to back; the
        // ranges below index into it. Its order is not the output order.
        private readonly BufferWriterStream _encoded = new(allowInvalidModuleEntries: true);
        private readonly List<(WasmSectionId Id, int Start, int Length)> _sectionsBeforeCode;

        // Function i's body up to its instructions (size, then locals) is _encoded[_functionPrefixes[i].Start..End].
        private readonly FunctionPrefix[] _functionPrefixes = [];
        private readonly long _codeSize;

        // Data segment i's header (mode, memory, offset and length) is _encoded[_dataOffsets[i].._dataOffsets[i + 1]].
        private readonly int[] _dataOffsets = [];
        private readonly long _dataSize;

        // Custom section i's UTF-8 name is _encoded[_customNameOffsets[i].._customNameOffsets[i + 1]]; a branch-hint
        // section from the model has an empty range and is not written (the code section's hints replace it).
        private readonly int[] _customNameOffsets = [];
        private readonly (int Start, int Length) _producers;
        private readonly int _producersBefore;

        public EncodedModule(WasmModule module)
        {
            _module = module;
            BufferWriterStream e = _encoded;
            List<TypeDefinition> typeDefinitions = module.TypeDefinitions;
            bool hasPassiveData = HasPassiveSegment(module.DataSegments);
            int sectionCount = (module.Types.Count > 0 ? 1 : 0) +
                (module.Imports.Count > 0 ? 1 : 0) +
                (module.Functions.Count > 0 ? 1 : 0) +
                (module.Tables.Count > 0 ? 1 : 0) +
                (module.Memories.Count > 0 ? 1 : 0) +
                (module.Tags.Count > 0 ? 1 : 0) +
                (module.Globals.Count > 0 ? 1 : 0) +
                (module.Exports.Count > 0 ? 1 : 0) +
                (module.StartFunction.HasValue ? 1 : 0) +
                (module.Elements.Count > 0 ? 1 : 0) +
                (hasPassiveData ? 1 : 0);
            _sectionsBeforeCode = new(sectionCount);

            if (module.Types.Count > 0)
            {
                int start = (int)e.Length;
                WriteTypeSection(e, module.Types, typeDefinitions);
                AddSection(WasmSectionId.Type, start);
            }
            if (module.Imports.Count > 0)
            {
                int start = (int)e.Length;
                WriteImportSection(e, module.Imports, typeDefinitions);
                AddSection(WasmSectionId.Import, start);
            }
            if (module.Functions.Count > 0)
            {
                int start = (int)e.Length;
                WriteFunctionSection(e, module.Functions);
                AddSection(WasmSectionId.Function, start);
            }
            if (module.Tables.Count > 0)
            {
                int start = (int)e.Length;
                WriteTableSection(e, module.Tables, typeDefinitions);
                AddSection(WasmSectionId.Table, start);
            }
            if (module.Memories.Count > 0)
            {
                int start = (int)e.Length;
                WriteMemorySection(e, module.Memories);
                AddSection(WasmSectionId.Memory, start);
            }
            if (module.Tags.Count > 0)
            {
                int start = (int)e.Length;
                WriteTagSection(e, module.Tags);
                AddSection(WasmSectionId.Tag, start);
            }
            if (module.Globals.Count > 0)
            {
                int start = (int)e.Length;
                WriteGlobalSection(e, module.Globals, typeDefinitions);
                AddSection(WasmSectionId.Global, start);
            }
            if (module.Exports.Count > 0)
            {
                int start = (int)e.Length;
                WriteExportSection(e, module.Exports);
                AddSection(WasmSectionId.Export, start);
            }
            if (module.StartFunction.HasValue)
            {
                int start = (int)e.Length;
                WasmEncoder.WriteU32Leb(e, (uint)module.StartFunction.Value);
                AddSection(WasmSectionId.Start, start);
            }
            if (module.Elements.Count > 0)
            {
                int start = (int)e.Length;
                WriteElementSection(e, module.Elements, typeDefinitions);
                AddSection(WasmSectionId.Element, start);
            }
            if (hasPassiveData)
            {
                int start = (int)e.Length;
                WasmEncoder.WriteU32Leb(e, (uint)module.DataSegments.Count);
                AddSection(WasmSectionId.DataCount, start);
            }

            List<WasmFunction> functions = module.Functions;
            if (functions.Count > 0)
            {
                _functionPrefixes = new FunctionPrefix[functions.Count];
                _codeSize = WasmLeb128.GetULeb128Size((ulong)functions.Count);
                bool hasBranchHints = false;
                ReadOnlySpan<byte> sizeRoom = [0, 0, 0, 0, 0];
                for (int i = 0; i < functions.Count; i++)
                {
                    // The body's size precedes its locals but depends on them, so room for the longest size is
                    // reserved and the size is written right-aligned into it once the locals are encoded.
                    WasmFunction function = functions[i];
                    e.Write(sizeRoom);
                    int localsStart = (int)e.Length;
                    WriteLocals(e, function, typeDefinitions, i);
                    uint size = checked((uint)(e.Length - localsStart + function.Body.Length + (AppendsEnd(function) ? 1 : 0)));
                    int sizeLength = WasmLeb128.GetULeb128Size(size);
                    WasmLeb128.WriteULeb128(e.GetBuffer().AsSpan(localsStart - sizeLength, sizeLength), size);
                    _functionPrefixes[i] = new FunctionPrefix(localsStart - sizeLength, localsStart, (int)e.Length);
                    _codeSize += sizeLength + (long)size;
                    hasBranchHints |= function.BranchHints.Count != 0;
                }

                if (hasBranchHints)
                {
                    int start = (int)e.Length;
                    WriteBranchHintSection(e, functions, _functionPrefixes, CountFunctionImports(module.Imports));
                    AddSection(WasmSectionId.Custom, start);
                }
            }

            List<Data> segments = module.DataSegments;
            if (segments.Count > 0)
            {
                _dataOffsets = new int[segments.Count + 1];
                _dataSize = WasmLeb128.GetULeb128Size((ulong)segments.Count);
                for (int i = 0; i < segments.Count; i++)
                {
                    _dataOffsets[i] = (int)e.Length;
                    WriteDataSegmentHeader(e, segments[i], i);
                    _dataSize += e.Length - _dataOffsets[i] + segments[i].InitData.Length;
                }
                _dataOffsets[segments.Count] = (int)e.Length;
            }

            // LLVM tools require producers to precede target_features.
            List<CustomSection> customSections = module.CustomSections;
            _producersBefore = customSections.Count;
            _customNameOffsets = new int[customSections.Count + 1];
            Span<byte> name = stackalloc byte[256];
            for (int i = 0; i < customSections.Count; i++)
            {
                _customNameOffsets[i] = (int)e.Length;
                string sectionName = customSections[i].Name;
                if (string.Equals(sectionName, BranchHintMetadataReader.SectionName, StringComparison.Ordinal))
                    continue;
                if (_producersBefore == customSections.Count && string.Equals(sectionName, "target_features", StringComparison.Ordinal))
                    _producersBefore = i;
                int byteCount = Encoding.UTF8.GetByteCount(sectionName);
                if (byteCount <= name.Length)
                    e.Write(name[..Encoding.UTF8.GetBytes(sectionName, name)]);
                else
                    e.Write(Encoding.UTF8.GetBytes(sectionName));
            }
            _customNameOffsets[customSections.Count] = (int)e.Length;

            if (module.ProducersSection is { Fields.Count: > 0 } producers)
            {
                int start = (int)e.Length;
                WriteProducersSection(e, producers);
                _producers = (start, (int)e.Length - start);
            }

            Length = ComputeLength();
        }

        /// <summary>The exact number of bytes <see cref="WriteTo"/> writes.</summary>
        public long Length { get; }

        /// <summary>Writes the module, which must be <see cref="Length"/> bytes long, to a new array.</summary>
        public byte[] ToArray()
        {
            if (Length > Array.MaxLength)
                throw new LinkerException($"The linked module is {Length} bytes, more than an array can hold.");
            var bytes = new byte[Length];
            using var stream = new MemoryStream(bytes);
            WriteTo(stream);
            return bytes;
        }

        /// <summary>Writes the module to <paramref name="output"/>, which only needs to be writable.</summary>
        public void WriteTo(Stream output)
        {
            var writer = new WasmModuleWriter(output);
            ReadOnlySpan<byte> encoded = _encoded.GetBuffer().AsSpan(0, (int)_encoded.Length);
            foreach ((WasmSectionId id, int start, int length) in _sectionsBeforeCode)
                writer.WriteSection(id, encoded.Slice(start, length));

            List<WasmFunction> functions = _module.Functions;
            if (functions.Count > 0)
            {
                writer.BeginSection(WasmSectionId.Code, _codeSize);
                WriteLeb(writer, (ulong)functions.Count);
                for (int i = 0; i < functions.Count; i++)
                {
                    WasmFunction function = functions[i];
                    writer.Write(encoded[_functionPrefixes[i].Start.._functionPrefixes[i].End]);
                    writer.Write(function.Body);
                    if (AppendsEnd(function))
                        writer.Write([WasmOpcode.End]);
                }
                writer.EndSection();
            }

            List<Data> segments = _module.DataSegments;
            if (segments.Count > 0)
            {
                writer.BeginSection(WasmSectionId.Data, _dataSize);
                WriteLeb(writer, (ulong)segments.Count);
                for (int i = 0; i < segments.Count; i++)
                {
                    writer.Write(encoded[_dataOffsets[i].._dataOffsets[i + 1]]);
                    writer.Write(segments[i].InitData);
                }
                writer.EndSection();
            }

            List<CustomSection> customSections = _module.CustomSections;
            for (int i = 0; i < customSections.Count; i++)
            {
                if (i == _producersBefore)
                    WriteProducers(writer, encoded);
                if (IsSkipped(i))
                    continue;
                writer.BeginCustomSection(encoded[_customNameOffsets[i].._customNameOffsets[i + 1]], customSections[i].Content.Length);
                writer.Write(customSections[i].Content);
                writer.EndSection();
            }
            if (_producersBefore == customSections.Count)
                WriteProducers(writer, encoded);

            if (writer.Position != Length)
                throw new InvalidOperationException($"Wrote {writer.Position} bytes of a module computed to be {Length} bytes long.");
        }

        private void WriteProducers(WasmModuleWriter writer, ReadOnlySpan<byte> encoded)
        {
            if (_producers.Length != 0)
                writer.WriteSection(WasmSectionId.Custom, encoded.Slice(_producers.Start, _producers.Length));
        }

        // Also checks that every section's size fits the format's u32, so that no section header can fail once
        // writing has begun.
        private long ComputeLength()
        {
            long length = WasmModuleWriter.Header.Length;
            foreach ((_, _, int sectionLength) in _sectionsBeforeCode)
                length += SectionSize(sectionLength);
            if (_module.Functions.Count > 0)
                length += SectionSize(_codeSize, "code section");
            if (_module.DataSegments.Count > 0)
                length += SectionSize(_dataSize, "data section");
            List<CustomSection> customSections = _module.CustomSections;
            for (int i = 0; i < customSections.Count; i++)
            {
                if (IsSkipped(i))
                    continue;
                int nameLength = _customNameOffsets[i + 1] - _customNameOffsets[i];
                length += SectionSize(WasmLeb128.GetULeb128Size((ulong)nameLength) + nameLength + (long)customSections[i].Content.Length);
            }
            if (_producers.Length != 0)
                length += SectionSize(_producers.Length);
            return length;

            // Sections encoded into the buffer are smaller than an array, so only the code, data and custom sections
            // (whose content stays in the model) can be too large.
            static long SectionSize(long contentSize, string? section = null)
            {
                if (contentSize > uint.MaxValue)
                {
                    throw new LinkerException(
                        $"The {section ?? "custom section"} is {contentSize} bytes, more than a section can hold ({uint.MaxValue}).");
                }
                return 1 + WasmLeb128.GetULeb128Size((ulong)contentSize) + contentSize;
            }
        }

        // A branch-hint section in the model is replaced by the one encoded from the functions' hints.
        private bool IsSkipped(int customSectionIndex) =>
            _customNameOffsets[customSectionIndex] == _customNameOffsets[customSectionIndex + 1]
            && string.Equals(_module.CustomSections[customSectionIndex].Name, BranchHintMetadataReader.SectionName, StringComparison.Ordinal);

        private void AddSection(WasmSectionId id, int start) =>
            _sectionsBeforeCode.Add((id, start, (int)_encoded.Length - start));

        private static void WriteLeb(WasmModuleWriter writer, ulong value)
        {
            Span<byte> leb = stackalloc byte[WasmLeb128.MaxLength];
            writer.Write(leb[..WasmLeb128.WriteULeb128(leb, value)]);
        }
    }

    /// <summary>
    /// Where a function body's encoded size and locals are in <see cref="EncodedModule"/>'s buffer: the size at
    /// <see cref="Start"/>, the locals from <see cref="Locals"/> to <see cref="End"/>.
    /// </summary>
    private readonly record struct FunctionPrefix(int Start, int Locals, int End)
    {
        /// <summary>The length of the locals, which is where the function's instructions start.</summary>
        public int LocalsLength => End - Locals;
    }

    private static bool HasPassiveSegment(List<Data> segments)
    {
        foreach (Data segment in segments)
        {
            if (segment.IsPassive)
                return true;
        }
        return false;
    }

    private static bool AppendsEnd(WasmFunction function) =>
        function.Body.Length == 0 || function.Body[^1] != WasmOpcode.End;

    /// <summary>
    /// Collects all <c>.debug_*</c> custom sections from the input modules, applies DWARF relocations
    /// where possible using the already-populated index maps, and returns the concatenated sections.
    /// </summary>
    private static List<CustomSection> MergeDebugSections(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps)
    {
        // Collect unique debug section names, preserving first-seen order.
        var debugSectionNames = new List<string>();
        var debugSectionNameSet = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in inputs)
        {
            foreach (var cs in module.CustomSections)
            {
                if (cs.Name.StartsWith(".debug_", StringComparison.Ordinal) &&
                    debugSectionNameSet.Add(cs.Name))
                {
                    debugSectionNames.Add(cs.Name);
                }
            }
        }

        if (debugSectionNames.Count == 0)
            return [];

        // Compute per-module contribution start offsets within each combined debug section.
        // moduleDebugOffsets[moduleIndex][sectionName] = byte offset in the combined output.
        var moduleDebugOffsets = new Dictionary<int, Dictionary<string, uint>>();
        var combinedDebugLengths = new Dictionary<string, uint>(StringComparer.Ordinal);

        foreach (var sectionName in debugSectionNames)
        {
            uint runningOffset = 0;
            for (int m = 0; m < inputs.Count; m++)
            {
                var content = GetDebugSectionContent(inputs[m], sectionName);
                if (content is { Length: > 0 })
                {
                    if (!moduleDebugOffsets.TryGetValue(m, out var moduleMap))
                        moduleDebugOffsets[m] = moduleMap = new Dictionary<string, uint>(StringComparer.Ordinal);

                    moduleMap[sectionName] = runningOffset;
                    runningOffset = checked(runningOffset + (uint)content.Length);
                }
            }
            combinedDebugLengths[sectionName] = runningOffset;
        }

        // Populate SectionOffsets in each module's IndexMap for DWARF cross-section references.
        // reloc..debug_X has TargetSectionIndex = the local section index of .debug_X.
        // We map that index → module's contribution start in the combined .debug_X.
        for (int m = 0; m < inputs.Count; m++)
        {
            if (!moduleDebugOffsets.TryGetValue(m, out var moduleOffsetMap))
                continue;

            foreach (var relocSection in inputs[m].RelocationSections)
            {
                if (!relocSection.Name.StartsWith("reloc.", StringComparison.Ordinal))
                    continue;

                string targetName = relocSection.Name["reloc.".Length..];
                if (!targetName.StartsWith(".debug_", StringComparison.Ordinal))
                    continue;

                if (moduleOffsetMap.TryGetValue(targetName, out uint startOffset))
                    indexMaps[m].SectionOffsets[relocSection.TargetSectionIndex] = startOffset;
            }
        }

        // Pre-build per-module relocation dictionaries and symbol tables once; avoids
        // a FirstOrDefault scan and a RelocSymbolTable allocation on every (module × section) pair.
        var perModuleRelocMap = new Dictionary<string, RelocationSection>?[inputs.Count];
        var perModuleSymbols = new RelocSymbolTable?[inputs.Count];
        for (int m = 0; m < inputs.Count; m++)
        {
            if (inputs[m].RelocationSections.Count > 0)
                perModuleRelocMap[m] = inputs[m].RelocationSections
                    .ToDictionary(r => r.Name, StringComparer.Ordinal);
            if (inputs[m].LinkingSection is not null)
                perModuleSymbols[m] = new RelocSymbolTable(inputs[m].LinkingSection!);
        }

        // Build each combined debug section, applying DWARF relocations per module.
        var result = new List<CustomSection>(debugSectionNames.Count);

        foreach (var sectionName in debugSectionNames)
        {
            uint totalLength = combinedDebugLengths[sectionName];
            if (totalLength == 0)
                continue;

            var combined = new byte[totalLength];
            uint writePos = 0;

            for (int m = 0; m < inputs.Count; m++)
            {
                var content = GetDebugSectionContent(inputs[m], sectionName);
                if (content is null || content.Length == 0)
                    continue;

                content.CopyTo(combined, (int)writePos);

                // Apply DWARF relocations that target this section.
                if (perModuleRelocMap[m]?.TryGetValue("reloc." + sectionName, out var relocSection) == true
                    && perModuleSymbols[m] is { } symbols)
                {
                    ApplyDebugRelocations(
                        combined, (int)writePos, content.Length,
                        relocSection!.Entries, indexMaps[m], symbols,
                        indexMaps[m].DataSegmentOffsets);
                }

                writePos += (uint)content.Length;
            }

            result.Add(new CustomSection(sectionName, combined));
        }

        return result;
    }

    /// <summary>
    /// Applies DWARF relocations to <paramref name="combinedBytes"/> at
    /// [<paramref name="sectionStart"/>, <paramref name="sectionStart"/> + <paramref name="sectionLength"/>).
    /// Relocations that require unavailable maps (e.g., function-body offsets or dead-code-eliminated
    /// symbols) are skipped without throwing — the DWARF bytes are left at their object-file values.
    /// </summary>
    private static void ApplyDebugRelocations(
        byte[] combinedBytes,
        int sectionStart,
        int sectionLength,
        IReadOnlyList<Relocation> relocations,
        IndexMap indexMap,
        RelocSymbolTable symbols,
        IReadOnlyDictionary<uint, uint> segmentBaseAddresses)
    {
        // Patch in-place via span — no intermediate buffer allocation or Array.Copy needed.
        // The span is a view of the already-written module slice; reloc offsets are 0-based from sectionStart.
        var span = combinedBytes.AsSpan(sectionStart, sectionLength);

        foreach (var reloc in relocations)
        {
            // R_WASM_FUNCTION_OFFSET_I32 requires code-section layout analysis; skip.
            if (reloc.Type is RelocationType.R_WASM_FUNCTION_OFFSET_I32)
                continue;

            // TryApplyRelocation returns false (no throw) when a symbol or map entry is missing,
            // which is the common case after DCE removes dead functions/globals.
            RelocationEngine.TryApplyRelocation(span, reloc, indexMap, symbols, segmentBaseAddresses);
        }
    }

    private static byte[]? GetDebugSectionContent(WasmModule module, string sectionName)
    {
        foreach (var cs in module.CustomSections)
        {
            if (string.Equals(cs.Name, sectionName, StringComparison.Ordinal))
                return cs.Content;
        }
        return null;
    }

    /// <summary>
    /// Builds a WebAssembly <c>name</c> custom section containing function name subsection (id=1)
    /// from the resolved symbol table.  Returns <c>null</c> when no named function symbols are available.
    /// </summary>
    private static CustomSection? BuildNameSection(IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols)
    {
        if (resolvedSymbols is null)
            return null;

        var namedFunctions = new List<(uint Index, string Name, int Order)>();
        foreach (var sym in resolvedSymbols.Values)
        {
            if (sym.Kind == Binary.Linking.SymbolType.Function &&
                sym.MergedIndex.HasValue &&
                !string.IsNullOrEmpty(sym.Name))
            {
                namedFunctions.Add((sym.MergedIndex.Value, sym.Name, namedFunctions.Count));
            }
        }

        if (namedFunctions.Count == 0)
            return null;

        // Keep the first symbol-table name for aliases, as wasm-ld does. List.Sort is not stable.
        namedFunctions.Sort(static (a, b) => a.Index != b.Index ? a.Index.CompareTo(b.Index) : a.Order.CompareTo(b.Order));
        int count = 0;
        long entriesSize = 0;
        for (int i = 0; i < namedFunctions.Count; i++)
        {
            if (i > 0 && namedFunctions[i].Index == namedFunctions[i - 1].Index)
                continue;
            count++;
            entriesSize += NameMapWriter.GetEntrySize(namedFunctions[i].Index, Encoding.UTF8.GetByteCount(namedFunctions[i].Name));
        }

        var content = new ExactArrayBufferWriter(NameMapWriter.GetSubsectionSize(count, entriesSize));
        var map = new NameMapWriter(content, NameSubsectionId.Function, count, entriesSize);
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        for (int i = 0; i < namedFunctions.Count; i++)
        {
            if (i > 0 && namedFunctions[i].Index == namedFunctions[i - 1].Index)
                continue;
            using var name = new Utf8Scratch(namedFunctions[i].Name, scratch);
            map.Write(namedFunctions[i].Index, name.Bytes);
        }
        map.Complete();

        return new CustomSection("name", content.ToArray());
    }

    /// <summary>
    /// Merges producers sections from all input modules and appends a <c>processed-by: <see cref="ProducerName"/></c> entry.
    /// </summary>
    internal static ProducersSection MergeProducers(IReadOnlyList<WasmModule> inputs)
    {
        // Collect all fields, dedup entries by (name, version)
        var merged = new Dictionary<string, List<(string Name, string Version)>>(StringComparer.Ordinal);

        foreach (var module in inputs)
        {
            if (module.ProducersSection is not { Fields.Count: > 0 } producers)
                continue;

            foreach (var (fieldName, values) in producers.Fields)
            {
                if (!merged.TryGetValue(fieldName, out var list))
                {
                    list = [];
                    merged[fieldName] = list;
                }

                foreach (var entry in values)
                {
                    if (!list.Contains(entry))
                        list.Add(entry);
                }
            }
        }

        // Always add our processed-by entry last
        if (!merged.TryGetValue("processed-by", out var processedBy))
        {
            processedBy = [];
            merged["processed-by"] = processedBy;
        }

        string version = typeof(SectionMerger).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var toolEntry = (ProducerName, version);
        // Remove any existing entry for this tool so ours is always last
        processedBy.RemoveAll(e => e.Name == ProducerName);
        processedBy.Add(toolEntry);

        return new ProducersSection { Fields = merged };
    }

    private static void WriteProducersSection(Stream stream, ProducersSection producers)
    {
        WriteName(stream, "producers"u8);

        WasmEncoder.WriteU32Leb(stream, (uint)producers.Fields.Count);
        foreach (var (fieldName, values) in producers.Fields)
        {
            WriteName(stream, fieldName);
            WasmEncoder.WriteU32Leb(stream, (uint)values.Count);
            foreach (var (name, version) in values)
            {
                WriteName(stream, name);
                WriteName(stream, version);
            }
        }
    }

    private static void ValidateInputs(IReadOnlyList<WasmModule> inputs, IReadOnlyList<IndexMap> indexMaps)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(indexMaps);

        if (inputs.Count != indexMaps.Count)
            throw new ArgumentException("The number of input modules must match the number of index maps.");
    }

    /// <summary>
    /// Returns a filtered list of imports. Function imports whose merged index is not in
    /// <paramref name="liveSets"/>.LiveFunctions and tag imports whose merged index is not in
    /// <paramref name="liveSets"/>.LiveTags are omitted; all other imports are kept.
    /// When <paramref name="liveSets"/> is null the full list is returned unchanged.
    /// </summary>
    private static List<Import> FilterImports(IReadOnlyList<Import> mergedImports, DceLiveSet? liveSets)
    {
        var result = new List<Import>(mergedImports.Count);
        foreach (var (import, survives) in ClassifyImports(mergedImports, liveSets))
            if (survives)
                result.Add(import);
        return result;
    }

    /// <summary>
    /// Pairs each merged import with whether the sweep will keep it.
    /// </summary>
    /// <remarks>
    /// Two passes need this answer: <see cref="FilterImports"/>, which drops the dead ones, and
    /// <c>DeadCodeEliminator.CompactTypes</c>, which must not try to rewrite a dead import's type
    /// index into the compacted space — that type is exactly the one the sweep removed, so the
    /// rewrite would refuse and fail a link that is in fact correct. Asked in two places the two
    /// answers drift; asked here they cannot.
    /// </remarks>
    internal static IEnumerable<(Import Import, bool Survives)> ClassifyImports(
        IReadOnlyList<Import> mergedImports, DceLiveSet? liveSets)
    {
        ArgumentNullException.ThrowIfNull(mergedImports);

        uint funcImportIdx = 0;
        uint tagImportIdx = 0;
        foreach (var import in mergedImports)
        {
            if (liveSets?.LiveFunctions is null)
            {
                yield return (import, true);
                continue;
            }

            if (import.Descriptor is ImportDescriptor.Func)
            {
                yield return (import, liveSets.LiveFunctions.Contains(funcImportIdx));
                funcImportIdx++;
            }
            else if (import.Descriptor is ImportDescriptor.Tag)
            {
                yield return (import, liveSets.LiveTags is null || liveSets.LiveTags.Contains(tagImportIdx));
                tagImportIdx++;
            }
            else
            {
                yield return (import, true);
            }
        }
    }

    /// <summary>
    /// Rewrites every type index an import descriptor names into the merged type space.
    /// </summary>
    /// <remarks>
    /// The <c>_ =&gt; import</c> arm used to swallow tables. A table import's element type is a type
    /// index whenever it is a typed reference — <c>(ref null $t)</c> — and leaving it at the input
    /// module's index means it names whichever type happens to occupy that slot after the merge. That
    /// is not a link error, because a wrong-but-existing index is structurally valid; it is a silent
    /// retyping (GitHub #383). There is no <c>_</c> arm any more: see
    /// <see cref="RemapImportTypeIndices"/>.
    /// </remarks>
    private static Import RemapImport(Import import, IndexMap indexMap)
    {
        var remapped = RemapImportTypeIndices(
            import, typeIndex => ResolveMappedIndex(indexMap.Types, typeIndex, "type"));

        // A table import cannot carry an initializer — the reader refuses the 0x40 form in an import
        // descriptor — but a programmatically built module can still hold one, and it names the input
        // module's function and global spaces. Rebase it rather than emit indices from another module.
        if (remapped.Descriptor is ImportDescriptor.Table { Type.InitExpr.Length: > 0 } table)
        {
            remapped = remapped with
            {
                Descriptor = new ImportDescriptor.Table(
                    table.Type with { InitExpr = RemapConstExprIndices(table.Type.InitExpr, indexMap) }),
            };
        }

        return remapped;
    }

    /// <summary>
    /// The single walk over the type indices an <see cref="Import"/> names, driven by a caller-supplied
    /// mapping.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two passes have to rewrite these: the merge, which moves each input module's type space into
    /// the merged one, and dead-code elimination, which compacts the merged space afterwards. Written
    /// twice they disagreed — the merge rebased a table import's element type and the compaction did
    /// not, so a table import declared <c>(ref null (func (result i64)))</c> came out naming the
    /// surviving <c>(func)</c> at the same index. <c>wasm-tools validate -f all</c> returns 0 on that
    /// module: a wrong-but-existing type index is not a structural error (GitHub #383).
    /// </para>
    /// <para>
    /// Every descriptor kind is named affirmatively and there is no <c>_</c> arm. A classifier written
    /// as "everything that is not a func or a tag carries no type index" defaults to acceptance and
    /// widens silently the moment a kind is added — which is precisely how tables, and then globals,
    /// came to be skipped here. A new descriptor kind now fails to compile.
    /// </para>
    /// </remarks>
    internal static Import RemapImportTypeIndices(Import import, Func<uint, uint> mapType)
    {
        ArgumentNullException.ThrowIfNull(import);
        ArgumentNullException.ThrowIfNull(mapType);

        return import.Descriptor switch
        {
            ImportDescriptor.Func func => import with
            {
                Descriptor = new ImportDescriptor.Func(checked((int)mapType((uint)func.TypeIndex))),
            },
            ImportDescriptor.Tag tag => import with
            {
                Descriptor = new ImportDescriptor.Tag(checked((int)mapType((uint)tag.TypeIndex))),
            },
            ImportDescriptor.Table table => import with
            {
                Descriptor = new ImportDescriptor.Table(RemapTableTypeIndices(table.Type, mapType)),
            },
            ImportDescriptor.Global global => import with
            {
                Descriptor = new ImportDescriptor.Global(RemapGlobalTypeIndices(global.Type, mapType)),
            },

            // A memory type is limits and a shared flag. It names no type index — stated as its own
            // arm so it is a decision on the record rather than a default.
            ImportDescriptor.Memory => import,

            _ => throw new LinkerException(
                $"Import '{import.ModuleName}.{import.Name}' has descriptor kind " +
                $"{import.Descriptor.GetType().Name}, which this linker does not know how to rebase into the " +
                "merged type space. Copying it through would emit whatever type index the input module " +
                "happened to use (GitHub #383)."),
        };
    }

    /// <summary>Rewrites a table type's exact element type — and its cached key — through <paramref name="mapType"/>.</summary>
    internal static TableType RemapTableTypeIndices(TableType type, Func<uint, uint> mapType)
    {
        var remapped = RemapExactRefType(type.ExactElementType, mapType);
        if (ReferenceEquals(remapped, type.ExactElementType))
            return type;

        return type with
        {
            ExactElementType = remapped,
            // ElementTypeKey is a cached rendering of ExactElementType, so it has to move with it or
            // the two disagree and every consumer that compares keys sees the pre-remap index.
            ElementTypeKey = remapped is null ? type.ElementTypeKey : remapped.Key,
        };
    }

    /// <summary>Rewrites a global type's exact content type — and its cached key — through <paramref name="mapType"/>.</summary>
    internal static GlobalType RemapGlobalTypeIndices(GlobalType type, Func<uint, uint> mapType)
    {
        var remapped = RemapExactRefType(type.ExactContentType, mapType);
        if (ReferenceEquals(remapped, type.ExactContentType))
            return type;

        return type with
        {
            ExactContentType = remapped,
            ContentTypeKey = remapped is null ? type.ContentTypeKey : remapped.Key,
        };
    }

    /// <summary>
    /// Merges two import descriptors for the same (module, name) pair.
    /// Memory: takes max of minimums, most permissive max.
    /// Table: element types must be the same type; limits take max of minimums, most permissive max.
    /// Others: must be identical.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The table arm used to compare <c>tabA.Type.ElementType == tabB.Type.ElementType</c> — the
    /// compact <see cref="RefType"/>, which has two members. Every typed reference collapses into
    /// one of them, so <c>(ref null $i32returning)</c> and <c>(ref null $i64returning)</c> both read
    /// as <c>FuncRef</c> and two imports of genuinely different tables were merged into one. It then
    /// rebuilt the descriptor with <c>new TableType(...)</c>, dropping the exact element type on the
    /// way out, so nothing downstream could notice either (GitHub #383).
    /// </para>
    /// <para>
    /// Element types are <b>invariant</b> — §4.5.6 requires <c>et1 = et2</c> for tables, because a
    /// table is read at its element type and written at it — so this is canonical equivalence, not
    /// subtyping. Both descriptors have already been rebased into the merged type space by
    /// <see cref="RemapImport"/>, which is what makes the question answerable at all: an index from
    /// one input module means nothing in another's space.
    /// </para>
    /// </remarks>
    private static ImportDescriptor MergeImportDescriptors(
        ImportDescriptor existing, ImportDescriptor incoming,
        string moduleName, string name,
        SubtypeRelation? mergedRelation)
    {
        if (Equals(existing, incoming))
            return existing;

        if (existing is ImportDescriptor.Table tabA && incoming is ImportDescriptor.Table tabB)
        {
            if (!TableElementTypesAreSame(tabA.Type, tabB.Type, mergedRelation))
            {
                throw new LinkerException(
                    $"Import '{moduleName}.{name}' was seen as a table with two different element types: " +
                    $"{DescribeElementType(tabA.Type)} and {DescribeElementType(tabB.Type)}. Table element types " +
                    "are invariant, so these are different tables under one name.");
            }

            // `with` on whichever side records an exact element type, not a fresh TableType: the
            // exact type and the element type key have to survive, or the merged import is compact
            // again and the whole comparison above becomes unreachable for anything downstream.
            // Preferring the exact side also makes the result independent of argument order — the
            // compact fallback used to hand back `tabA`, so `compact + exact` and `exact + compact`
            // produced different merged descriptors from the same pair (GitHub #383).
            var carrier = tabA.Type.ExactElementType is not null ? tabA.Type : tabB.Type;
            return new ImportDescriptor.Table(carrier with
            {
                Limits = MergeLimits(tabA.Type.Limits, tabB.Type.Limits),
            });
        }

        return (existing, incoming) switch
        {
            (ImportDescriptor.Memory memA, ImportDescriptor.Memory memB) =>
                new ImportDescriptor.Memory(new MemoryType(MergeLimits(memA.Type.Limits, memB.Type.Limits))),
            _ => throw new LinkerException(
                $"Import '{moduleName}.{name}' was seen with incompatible {GetImportKindName(existing)} signatures."),
        };
    }

    /// <summary>
    /// Canonical equivalence of two table element types already rebased into the merged type space.
    /// </summary>
    /// <remarks>
    /// When either side records no exact type the module was built programmatically and carries no
    /// type space; the compact <see cref="RefType"/> is then all there is, and it is exact for the
    /// two types it can express. A defined index with no merged type space to resolve it in is
    /// refused rather than guessed at — that is the case where an answer would be a coincidence.
    /// <para>
    /// <b>Why the relation and not the rebased index.</b> Both sides have been rebased, so within a
    /// merged space that <see cref="SameType"/> has checked for conflation, "same index" and
    /// "canonically equivalent" happen to agree for defined types — compact erasure only widens, so
    /// two canonically equivalent types have identical compact signatures and the deduplicator has
    /// already put them in one slot. That agreement is a consequence of the guard, not a property of
    /// index comparison, and it stops holding the moment this space admits recursion groups or
    /// subtype declarations. It also never covered nullability: <c>(ref null $t)</c> and
    /// <c>(ref $t)</c> name the same index and are different types, which is what
    /// <c>MergedTypeSpaceTests.TableImportsDifferingOnlyInNullability_AreRefused</c> pins.
    /// </para>
    /// </remarks>
    private static bool TableElementTypesAreSame(TableType a, TableType b, SubtypeRelation? mergedRelation)
    {
        if (a.ExactElementType is null && b.ExactElementType is null)
        {
            // Neither module records a type space, so the compact RefType is the whole truth on both
            // sides and it is exact for the two types it can express.
            return a.ElementType == b.ElementType;
        }

        if (a.ExactElementType is not { } exactA || b.ExactElementType is not { } exactB)
        {
            // Exactly one side is compact. `funcref` and `(ref null $t)` both render as
            // RefType.FuncRef, so comparing the compact codes here accepted two genuinely different
            // tables and then kept whichever exact type happened to be first — making the merged
            // result depend on argument order (GitHub #383). The compact side carries no type-space
            // provenance, so it can only be equal to the exact side when the compact code *is* that
            // type: `funcref` is exactly `(ref null func)` and nothing else.
            var compactOnly = a.ExactElementType is null ? a : b;
            var exactOnly = a.ExactElementType is null ? b.ExactElementType! : a.ExactElementType!;
            return Microsoft.NET.WebAssembly.Types.ValueType.FromStorageValType((ValType)compactOnly.ElementType) == exactOnly;
        }

        bool namesDefinedType =
            exactA is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType } ||
            exactB is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType };

        if (!namesDefinedType)
            return exactA == exactB;

        if (mergedRelation is null)
        {
            throw new LinkerException(
                $"A table import's element type names a defined type ({DescribeElementType(a)} / " +
                $"{DescribeElementType(b)}) but the inputs record no type definitions, so there is no type space " +
                "to compare them in.");
        }

        return mergedRelation.AreEquivalent(exactA, exactB);
    }

    private static string DescribeElementType(TableType type) =>
        type.ExactElementType?.Key ?? type.ElementType.ToString();

    /// <summary>
    /// Rejects any wasm64 (memory64/table64) memory or table — imported or defined — before merge
    /// output. Linking of wasm64 is not yet supported (#88); rejecting here guarantees no later site
    /// (capacity expansion, ApplyMemory/TableOptions, or the section writers) truncates a u64 limit
    /// to u32 or reconstructs a Limits without Is64.
    /// </summary>
    private static void RejectWasm64Linking(
        IReadOnlyList<Import> imports,
        IReadOnlyList<MemoryType> memories,
        IReadOnlyList<TableType> tables)
    {
        foreach (var import in imports)
        {
            switch (import.Descriptor)
            {
                case ImportDescriptor.Memory { Type.Is64: true }:
                    throw new LinkerException(Wasm64Guard.NotSupportedMessage("memory64", "linking"));
                case ImportDescriptor.Table { Type.Is64: true }:
                    throw new LinkerException(Wasm64Guard.NotSupportedMessage("table64", "linking"));
                case ImportDescriptor.Memory memory:
                    Require32BitRange(memory.Type.Limits, "memory");
                    break;
                case ImportDescriptor.Table table:
                    Require32BitRange(table.Type.Limits, "table");
                    break;
            }
        }

        foreach (var memory in memories)
        {
            if (memory.Is64)
                throw new LinkerException(Wasm64Guard.NotSupportedMessage("memory64", "linking"));
            Require32BitRange(memory.Limits, "memory");
        }

        foreach (var table in tables)
        {
            if (table.Is64)
                throw new LinkerException(Wasm64Guard.NotSupportedMessage("table64", "linking"));
            Require32BitRange(table.Limits, "table");
        }
    }

    /// <summary>
    /// Limits are read as u64 for every memory and table, and a 32-bit one that does not fit is invalid, not
    /// malformed. Linker inputs are not validated, so refuse it here rather than truncate it.
    /// </summary>
    private static void Require32BitRange(Limits limits, string kind)
    {
        if (limits.Min > uint.MaxValue || limits.Max > uint.MaxValue)
            throw new LinkerException($"A 32-bit {kind}'s limits must fit in 32 bits (min {limits.Min}, max {limits.Max?.ToString() ?? "none"})");
    }

    private static Limits MergeLimits(Limits a, Limits b)
    {
        if (a.Is64 || b.Is64)
            throw new LinkerException(
                Wasm64Guard.NotSupportedMessage("memory64/table64", "linking"));
        uint min = Math.Max((uint)a.Min, (uint)b.Min);
        uint? max = ((uint?)a.Max, (uint?)b.Max) switch
        {
            (null, _) => null,
            (_, null) => null,
            (uint ma, uint mb) => Math.Max(ma, mb),
        };
        if (max.HasValue)
            max = Math.Max(max.Value, min);
        return new Limits(min, max);
    }

    private static string GetImportKindName(ImportDescriptor descriptor) =>
        descriptor switch
        {
            ImportDescriptor.Func => "function",
            ImportDescriptor.Global => "global",
            ImportDescriptor.Table => "table",
            ImportDescriptor.Memory => "memory",
            ImportDescriptor.Tag => "tag",
            _ => "import",
        };

    private static bool ShouldExportSymbol(ResolvedSymbol symbol, HashSet<string>? extraExports, bool exportAll)
    {
        if (symbol.IsUndefined ||
            string.IsNullOrEmpty(symbol.Name) ||
            !symbol.MergedIndex.HasValue)
        {
            return false;
        }

        if (exportAll)
            return true;

        if ((symbol.Flags & (SymbolFlags.BindingLocal | SymbolFlags.VisibilityHidden)) != 0)
            return false;

        if ((symbol.Flags & SymbolFlags.Exported) != 0)
            return true;

        return extraExports?.Contains(symbol.Name) == true;
    }

    private static bool TryCreateExportFromSymbol(ResolvedSymbol symbol, out Export export)
    {
        export = default!;

        if (!symbol.MergedIndex.HasValue)
            return false;

        ExportDescriptor? descriptor = symbol.Kind switch
        {
            SymbolType.Function => new ExportDescriptor.Func(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Global => new ExportDescriptor.Global(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Table => new ExportDescriptor.Table(checked((int)symbol.MergedIndex.Value)),
            SymbolType.Event or SymbolType.Tag => new ExportDescriptor.Tag(checked((int)symbol.MergedIndex.Value)),
            _ => null,
        };

        if (descriptor is null)
            return false;

        export = new Export(symbol.Name, descriptor);
        return true;
    }

    private static int? ResolveStartFunction(
        IReadOnlyList<WasmModule> inputs,
        IReadOnlyList<IndexMap> indexMaps,
        OrderedDictionary<string, Export> mergedExports,
        LinkerOptions options)
    {
        if (options.EntryPoint is not null &&
            mergedExports.TryGetValue(options.EntryPoint, out var entryPointExport) &&
            entryPointExport.Descriptor is ExportDescriptor.Func entryPointFunction)
        {
            return entryPointFunction.FunctionIndex;
        }

        for (int moduleIndex = 0; moduleIndex < inputs.Count; moduleIndex++)
        {
            if (inputs[moduleIndex].StartFunction is int startFunction)
            {
                return checked((int)ResolveMappedIndex(
                    indexMaps[moduleIndex].Functions, (uint)startFunction, "function"));
            }
        }

        return null;
    }

    private static ExportDescriptor RemapExportDescriptor(ExportDescriptor descriptor, IndexMap indexMap) =>
        descriptor switch
        {
            ExportDescriptor.Func function => new ExportDescriptor.Func(
                checked((int)ResolveMappedIndex(indexMap.Functions, (uint)function.FunctionIndex, "function"))),
            ExportDescriptor.Global global => new ExportDescriptor.Global(
                checked((int)ResolveMappedIndex(indexMap.Globals, (uint)global.GlobalIndex, "global"))),
            ExportDescriptor.Table table => new ExportDescriptor.Table(
                checked((int)ResolveMappedIndex(indexMap.Tables, (uint)table.TableIndex, "table"))),
            ExportDescriptor.Memory memory => new ExportDescriptor.Memory(
                checked((int)ResolveMappedIndex(indexMap.Memories, (uint)memory.MemoryIndex, "memory"))),
            ExportDescriptor.Tag tag => new ExportDescriptor.Tag(
                checked((int)ResolveMappedIndex(indexMap.Tags, (uint)tag.TagIndex, "tag"))),
            _ => descriptor,
        };

    private static uint ResolveMappedIndex(Dictionary<uint, uint> map, uint oldIndex, string kind)
    {
        if (map.TryGetValue(oldIndex, out uint mappedIndex))
            return mappedIndex;

        throw new KeyNotFoundException($"No mapped {kind} index was found for original index {oldIndex}.");
    }

    private static uint GetSegmentAlignment(WasmModule module, int segmentIndex)
    {
        if (module.LinkingSection?.SegmentInfos is not { Count: > 0 } segmentInfos ||
            segmentIndex >= segmentInfos.Count)
        {
            return 1;
        }

        uint log2Alignment = segmentInfos[segmentIndex].Alignment;
        if (log2Alignment >= 31)
            return 1u << 31;

        return 1u << (int)log2Alignment;
    }

    private static bool IsReadOnlySegment(WasmModule module, int segmentIndex)
    {
        if (module.LinkingSection?.SegmentInfos is not { Count: > 0 } segmentInfos ||
            segmentIndex >= segmentInfos.Count)
        {
            return false;
        }

        return segmentInfos[segmentIndex].Name.StartsWith(".rodata", StringComparison.Ordinal);
    }

    private static uint AlignUp(uint value, uint alignment)
    {
        if (alignment <= 1)
            return value;

        uint remainder = value % alignment;
        return remainder == 0 ? value : checked(value + alignment - remainder);
    }

    private static byte[] BuildI32ConstExpr(uint value)
    {
        // i32.const(1 byte) + signed LEB128 value(≤5 bytes) + end(1 byte) = max 7 bytes
        Span<byte> buf = stackalloc byte[7];
        buf[0] = 0x41; // i32.const
        int lebLen = WasmLeb128.WriteSLeb128(buf[1..], unchecked((int)value));
        buf[1 + lebLen] = 0x0B; // end
        return buf[..(2 + lebLen)].ToArray();
    }

    private static uint ComputeDataStart(LinkerOptions? options)
    {
        if (options is null) return 0;
        uint globalBase = options.GlobalBase ?? 0;
        if (options.StackFirst == true)
            return globalBase + options.StackSize;
        return globalBase;
    }

    private static void PatchLayoutGlobals(
        List<WasmGlobal> globals,
        List<Data> dataSegments,
        List<Import> mergedImports,
        IReadOnlyDictionary<string, ResolvedSymbol>? resolvedSymbols,
        LinkerOptions options)
    {
        bool layoutActive = options.GlobalBase.HasValue || options.StackFirst == true;
        if (!layoutActive || resolvedSymbols is null)
            return;

        uint globalBase = options.GlobalBase ?? 0;
        uint stackSize = options.StackSize;

        // Compute dataEnd: max(offset + data length) across all memory-0 active segments
        uint dataEnd = ComputeDataStart(options);
        foreach (var seg in dataSegments)
        {
            if (seg.IsPassive || seg.MemoryIndex != 0)
                continue;
            if (!TryReadConstI32Expr(seg.OffsetExpr, out uint offset))
                continue;
            uint end = checked(offset + (uint)seg.InitData.Length);
            if (end > dataEnd)
                dataEnd = end;
        }

        // Compute stackPointerInit and heapBase
        uint stackPointerInit;
        uint heapBase;
        if (options.StackFirst == true)
        {
            stackPointerInit = globalBase + stackSize; // stack grows down from top of stack region
            heapBase = dataEnd;                        // heap starts right after data
        }
        else
        {
            uint alignedDataEnd = AlignUp(dataEnd, 16);
            stackPointerInit = alignedDataEnd + stackSize;
            heapBase = stackPointerInit;               // heap starts after stack
        }

        // Count global imports to translate merged index → globals list index
        uint globalImportCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Global) globalImportCount++;

        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__stack_pointer", stackPointerInit);
        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__data_end", dataEnd);
        PatchGlobal(globals, resolvedSymbols, globalImportCount, "__heap_base", heapBase);
    }

    private static void PatchGlobal(
        List<WasmGlobal> globals,
        IReadOnlyDictionary<string, ResolvedSymbol> resolvedSymbols,
        uint globalImportCount,
        string symbolName,
        uint value)
    {
        if (!resolvedSymbols.TryGetValue(symbolName, out var sym) ||
            sym.Kind != SymbolType.Global ||
            !sym.MergedIndex.HasValue)
            return;

        uint mergedIdx = sym.MergedIndex.Value;
        if (mergedIdx < globalImportCount)
            return; // imported global — don't patch

        int localIdx = (int)(mergedIdx - globalImportCount);
        if ((uint)localIdx >= (uint)globals.Count)
            return;

        globals[localIdx] = new WasmGlobal
        {
            Type = globals[localIdx].Type,
            InitExpr = BuildI32ConstExpr(value),
        };
    }

    private static void EnsureMemoryCapacity(
        List<Import> mergedImports,
        List<MemoryType> mergedMemories,
        IReadOnlyList<Data> dataSegments)
    {
        var requiredPages = new Dictionary<int, uint>();
        foreach (var segment in dataSegments)
        {
            if (segment.IsPassive ||
                segment.MemoryIndex < 0 ||
                !TryReadConstI32Expr(segment.OffsetExpr, out uint offset))
            {
                continue;
            }

            uint end = checked(offset + (uint)segment.InitData.Length);
            uint pages = end == 0 ? 0u : ((end - 1) / WasmPageSize) + 1;

            if (requiredPages.TryGetValue(segment.MemoryIndex, out var existing))
                requiredPages[segment.MemoryIndex] = Math.Max(existing, pages);
            else
                requiredPages[segment.MemoryIndex] = pages;
        }

        int importedMemoryCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Memory) importedMemoryCount++;
        foreach (var (memoryIndex, pages) in requiredPages)
        {
            if (memoryIndex < importedMemoryCount)
            {
                UpdateImportedMemoryLimit(mergedImports, memoryIndex, pages);
            }
            else
            {
                int localIndex = memoryIndex - importedMemoryCount;
                if ((uint)localIndex >= (uint)mergedMemories.Count)
                    continue;

                mergedMemories[localIndex] = ExpandMemory(mergedMemories[localIndex], pages);
            }
        }
    }

    private static void UpdateImportedMemoryLimit(List<Import> mergedImports, int memoryIndex, uint requiredPages)
    {
        int seen = 0;
        for (int importIndex = 0; importIndex < mergedImports.Count; importIndex++)
        {
            if (mergedImports[importIndex].Descriptor is not ImportDescriptor.Memory memory)
                continue;

            if (seen == memoryIndex)
            {
                mergedImports[importIndex] = mergedImports[importIndex] with
                {
                    Descriptor = new ImportDescriptor.Memory(ExpandMemory(memory.Type, requiredPages)),
                };
                return;
            }

            seen++;
        }
    }

    private static MemoryType ExpandMemory(MemoryType memoryType, uint requiredPages)
    {
        uint min = Math.Max((uint)memoryType.Limits.Min, requiredPages);
        uint? max = memoryType.Limits.Max.HasValue
            ? Math.Max((uint)memoryType.Limits.Max.Value, min)
            : null;
        return new MemoryType(new Limits(min, max), memoryType.Shared);
    }

    private static void ApplyMemoryOptions(
        List<Import> mergedImports,
        List<MemoryType> mergedMemories,
        LinkerOptions options)
    {
        bool hasOverride = options.ImportMemory || options.InitialMemory.HasValue ||
                           options.MaxMemory.HasValue || options.SharedMemory;
        if (!hasOverride)
            return;

        // Compute override pages
        uint? initialPages = options.InitialMemory.HasValue
            ? (uint)(options.InitialMemory.Value / WasmPageSize)
            : null;
        uint? maxPages = options.MaxMemory.HasValue
            ? (uint)(options.MaxMemory.Value / WasmPageSize)
            : null;

        if (options.ImportMemory)
        {
            // Determine the current min from existing memory (local or imported)
            uint currentMin = 0;
            uint? currentMax = null;
            bool currentShared = false;

            // Check existing imported memories
            int existingImportIndex = -1;
            for (int i = 0; i < mergedImports.Count; i++)
            {
                if (mergedImports[i].Descriptor is ImportDescriptor.Memory mem &&
                    mergedImports[i].ModuleName == options.ImportMemoryModule &&
                    mergedImports[i].Name == options.ImportMemoryName)
                {
                    existingImportIndex = i;
                    currentMin = (uint)mem.Type.Limits.Min;
                    currentMax = (uint?)mem.Type.Limits.Max;
                    currentShared = mem.Type.Shared;
                    break;
                }
            }

            // Collect min from local memories before removing them
            if (mergedMemories.Count > 0)
            {
                currentMin = Math.Max(currentMin, (uint)mergedMemories[0].Limits.Min);
                if (mergedMemories[0].Limits.Max is { } mergedMax64)
                {
                    uint mergedMax = (uint)mergedMax64;
                    currentMax = currentMax.HasValue
                        ? Math.Max(currentMax.Value, mergedMax)
                        : mergedMax;
                }
                currentShared = currentShared || mergedMemories[0].Shared;
            }

            // Remove all local memories
            mergedMemories.Clear();

            // Apply overrides
            uint min = initialPages ?? currentMin;
            if (initialPages.HasValue && currentMin > initialPages.Value)
                throw new LinkerException(
                    $"--initial-memory too small: data segments require at least {currentMin * WasmPageSize} bytes ({currentMin} pages).");

            uint? max = maxPages ?? currentMax;
            bool shared = options.SharedMemory || currentShared;

            var memType = new MemoryType(new Limits(min, max), shared);

            if (existingImportIndex >= 0)
            {
                mergedImports[existingImportIndex] = mergedImports[existingImportIndex] with
                {
                    Descriptor = new ImportDescriptor.Memory(memType),
                };
            }
            else
            {
                // Insert memory import at the end of existing imports (before any non-import items)
                var import = new Import(options.ImportMemoryModule, options.ImportMemoryName,
                    new ImportDescriptor.Memory(memType));
                mergedImports.Add(import);
            }
        }
        else
        {
            // Apply overrides to local memory or imported memory
            int importedMemCount = 0;
            for (int i = 0; i < mergedImports.Count; i++)
                if (mergedImports[i].Descriptor is ImportDescriptor.Memory) importedMemCount++;

            if (importedMemCount > 0)
            {
                // Apply to first imported memory
                for (int i = 0; i < mergedImports.Count; i++)
                {
                    if (mergedImports[i].Descriptor is not ImportDescriptor.Memory mem)
                        continue;

                    uint min = initialPages ?? (uint)mem.Type.Limits.Min;
                    if (initialPages.HasValue && mem.Type.Limits.Min > initialPages.Value)
                        throw new LinkerException(
                            $"--initial-memory too small: data segments require at least {mem.Type.Limits.Min * WasmPageSize} bytes ({mem.Type.Limits.Min} pages).");

                    uint? max = maxPages ?? (uint?)mem.Type.Limits.Max;
                    bool shared = options.SharedMemory || mem.Type.Shared;

                    mergedImports[i] = mergedImports[i] with
                    {
                        Descriptor = new ImportDescriptor.Memory(new MemoryType(new Limits(min, max), shared)),
                    };
                    break;
                }
            }
            else if (mergedMemories.Count > 0)
            {
                // Apply to first local memory
                var existing = mergedMemories[0];
                uint min = initialPages ?? (uint)existing.Limits.Min;
                if (initialPages.HasValue && existing.Limits.Min > initialPages.Value)
                    throw new LinkerException(
                        $"--initial-memory too small: data segments require at least {existing.Limits.Min * WasmPageSize} bytes ({existing.Limits.Min} pages).");

                uint? max = maxPages ?? (uint?)existing.Limits.Max;
                bool shared = options.SharedMemory || existing.Shared;

                mergedMemories[0] = new MemoryType(new Limits(min, max), shared);
            }
        }
    }

    /// <summary>
    /// Applies table-related linker options: <c>--import-table</c> and <c>--growable-table</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>--import-table</c> demotes the merged module's first defined table to an import. That
    /// rewrite has to carry every part of the table type the import form can express — the exact
    /// element type in particular, because <see cref="TableType.ElementType"/> alone collapses
    /// <c>(ref null $t)</c> to <c>funcref</c> and the demoted import then requires a strictly weaker
    /// table than the module was written against.
    /// </para>
    /// <para>
    /// The initializer expression is the one part it <em>cannot</em> carry: it belongs to the
    /// <c>table</c> production, not to <c>tabletype</c>, and the importer does not allocate the table
    /// at all — the host supplies it, already filled. Silently dropping it produces a module whose
    /// slots are whatever the host handed over instead of the declared value, which is a different
    /// program that still links. There is no legal import-form equivalent, so the transform refuses
    /// (GitHub #383).
    /// </para>
    /// </remarks>
    private static void ApplyTableOptions(
        List<Import> mergedImports,
        List<TableType> mergedTables,
        LinkerOptions options)
    {
        if (options.ImportTable)
        {
            // Determine current table 0 limits (local or imported)
            uint currentMin = 0;
            uint? currentMax = null;
            TableType? shape = null;

            int existingImportIndex = -1;
            for (int i = 0; i < mergedImports.Count; i++)
            {
                if (mergedImports[i].Descriptor is ImportDescriptor.Table tab &&
                    mergedImports[i].ModuleName == options.ImportTableModule &&
                    mergedImports[i].Name == options.ImportTableName)
                {
                    existingImportIndex = i;
                    currentMin = (uint)tab.Type.Limits.Min;
                    currentMax = (uint?)tab.Type.Limits.Max;
                    shape = tab.Type;
                    break;
                }
            }

            if (mergedTables.Count > 0)
            {
                var local = mergedTables[0];
                if (local.InitExpr.Length > 0)
                {
                    throw new LinkerException(
                        "--import-table cannot be applied to a table that declares an initializer expression: an " +
                        "import descriptor is a bare tabletype and has nowhere to put it, and the imported table is " +
                        "supplied already-allocated by the host. Dropping the initializer would silently change " +
                        "which functions the table holds (GitHub #383).");
                }

                if (shape is not null && !TableElementTypesAreSame(shape, local, null))
                {
                    throw new LinkerException(
                        $"--import-table found an existing import '{options.ImportTableModule}.{options.ImportTableName}' " +
                        $"with element type {DescribeElementType(shape)} but the local table 0 has element type " +
                        $"{DescribeElementType(local)}. Overwriting one with the other would retype the table.");
                }

                currentMin = Math.Max(currentMin, (uint)local.Limits.Min);
                if (local.Limits.Max is { } mergedMax64)
                {
                    uint mergedMax = (uint)mergedMax64;
                    currentMax = currentMax.HasValue
                        ? Math.Max(currentMax.Value, mergedMax)
                        : mergedMax;
                }

                shape = local;
                // Remove the first local table (it becomes an import)
                mergedTables.RemoveAt(0);
            }

            uint? max = options.GrowableTable ? null : currentMax;
            // `with` on the surviving shape rather than a fresh TableType: ExactElementType and
            // ElementTypeKey are legal on an import descriptor and are dropped by the constructor.
            var tableType = shape is null
                ? new TableType(RefType.FuncRef, new Limits(currentMin, max))
                : shape with { Limits = new Limits(currentMin, max) };

            if (existingImportIndex >= 0)
            {
                mergedImports[existingImportIndex] = mergedImports[existingImportIndex] with
                {
                    Descriptor = new ImportDescriptor.Table(tableType),
                };
            }
            else
            {
                mergedImports.Add(new Import(options.ImportTableModule, options.ImportTableName,
                    new ImportDescriptor.Table(tableType)));
            }
        }
        else if (options.GrowableTable && mergedTables.Count > 0)
        {
            // Remove max bound from table 0 to allow growth. `with` rather than a fresh TableType so
            // the initializer expression and exact element type survive the rewrite (#383).
            var t = mergedTables[0];
            mergedTables[0] = t with { Limits = new Limits(t.Limits.Min, null) };
        }
    }

    /// <summary>
    /// Offsets element segment expressions for table 0 by <paramref name="tableBase"/>.
    /// </summary>
    private static void ApplyTableBase(List<Element> elements, uint tableBase)
    {
        if (tableBase == 0)
            return;

        for (int i = 0; i < elements.Count; i++)
        {
            var elem = elements[i];
            if (elem.TableIndex < 0)
                continue;

            if (!TryReadConstI32Expr(elem.OffsetExpr, out uint currentOffset))
                continue;

            uint newOffset = checked(currentOffset + tableBase);
            elements[i] = new Element
            {
                Mode = elem.Mode,
                TableIndex = elem.TableIndex,
                OffsetExpr = MakeConstI32Expr(newOffset),
                FunctionIndices = elem.FunctionIndices,
                DeclaredFunctionIndices = elem.DeclaredFunctionIndices,
                ElementExpressions = elem.ElementExpressions,
                UsesExpressions = elem.UsesExpressions,
                HasExplicitTableIndex = elem.HasExplicitTableIndex,
                ElementType = elem.ElementType,
            };
        }
    }

    /// <summary>Creates a const i32 expression: <c>(i32.const N) end</c>.</summary>
    private static byte[] MakeConstI32Expr(uint value)
    {
        using var ms = new MemoryStream(8);
        ms.WriteByte(0x41); // i32.const
        WasmEncoder.WriteS32Leb(ms, unchecked((int)value));
        ms.WriteByte(0x0B); // end
        return ms.ToArray();
    }

    private static void EnsureTableCapacity(
        List<Import> mergedImports,
        List<TableType> mergedTables,
        IReadOnlyList<Element> elements)
    {
        var requiredEntries = new Dictionary<int, uint>();
        foreach (var element in elements)
        {
            if (element.TableIndex < 0 ||
                !TryReadConstI32Expr(element.OffsetExpr, out uint offset))
            {
                continue;
            }

            uint required = checked(offset + (uint)element.FunctionIndices.Count);
            if (requiredEntries.TryGetValue(element.TableIndex, out var existing))
                requiredEntries[element.TableIndex] = Math.Max(existing, required);
            else
                requiredEntries[element.TableIndex] = required;
        }

        int importedTableCount = 0;
        foreach (var import in mergedImports)
            if (import.Descriptor is ImportDescriptor.Table) importedTableCount++;
        foreach (var (tableIndex, required) in requiredEntries)
        {
            if (tableIndex < importedTableCount)
            {
                UpdateImportedTableLimit(mergedImports, tableIndex, required);
            }
            else
            {
                int localIndex = tableIndex - importedTableCount;
                if ((uint)localIndex >= (uint)mergedTables.Count)
                    continue;

                mergedTables[localIndex] = ExpandTable(mergedTables[localIndex], required);
            }
        }
    }

    private static void UpdateImportedTableLimit(List<Import> mergedImports, int tableIndex, uint requiredEntries)
    {
        int seen = 0;
        for (int importIndex = 0; importIndex < mergedImports.Count; importIndex++)
        {
            if (mergedImports[importIndex].Descriptor is not ImportDescriptor.Table table)
                continue;

            if (seen == tableIndex)
            {
                mergedImports[importIndex] = mergedImports[importIndex] with
                {
                    Descriptor = new ImportDescriptor.Table(ExpandTable(table.Type, requiredEntries)),
                };
                return;
            }

            seen++;
        }
    }

    /// <summary>
    /// Widens a table's limits to hold at least <paramref name="requiredEntries"/> elements.
    /// </summary>
    /// <remarks>
    /// This reconstructs the record, so every field that is not part of the limits has to be carried
    /// across explicitly — <c>with</c> does that; <c>new TableType(...)</c> silently resets
    /// <see cref="TableType.InitExpr"/>, <c>ElementTypeKey</c> and <c>ExactElementType</c> to their
    /// defaults. Dropping the initializer here is invisible in a diff and produces a table of nulls
    /// rather than an error (GitHub #383).
    /// </remarks>
    private static TableType ExpandTable(TableType tableType, uint requiredEntries)
    {
        uint min = Math.Max((uint)tableType.Limits.Min, requiredEntries);
        uint? max = tableType.Limits.Max.HasValue
            ? Math.Max((uint)tableType.Limits.Max.Value, min)
            : null;
        return tableType with { Limits = new Limits(min, max) };
    }

    private static bool TryReadConstI32Expr(ReadOnlySpan<byte> expr, out uint value)
    {
        bool single = ConstExpressionWalker.TryReadSingle(expr, out ConstOp op, out DecodedInstruction instruction)
            && op == ConstOp.I32Const
            && unchecked((int)instruction.Index0) >= 0;
        value = single ? instruction.Index0 : 0;
        return single;
    }

    /// <summary>
    /// Normalises a constant expression for output without changing any index: the write-time half
    /// of <see cref="RemapConstExprIndices"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// nesm's reader accepts two spellings of <c>cont</c> — <c>0x68</c> and the historical
    /// <c>0x65</c> — and <see cref="AbstractHeapTypeCodes.TryToCode"/> normalises every writer that
    /// asks it onto the canonical one. Table element types went through it; the initializer sitting
    /// next to them did not, because it is a byte array the writer copied verbatim. So a legacy
    /// <c>(table 2 (ref null cont) (ref.null cont))</c> came out with its type normalised to
    /// <c>0x68</c> and its <c>ref.null</c> still reading <c>0x65</c>, and wasm-tools rejects that
    /// output as an invalid abstract heap type — nesm's own reader accepts it, so only an external
    /// oracle can see it (GitHub #383).
    /// </para>
    /// <para>
    /// One normalisation policy, one place. An expression carrying an opcode this walker does not
    /// know is refused rather than copied, because copying is precisely how the two policies came to
    /// disagree.
    /// </para>
    /// </remarks>
    private static byte[] NormalizeConstExpr(byte[] expr, string site)
    {
        try
        {
            return ConstExprScanner.Rewrite(expr, static (kind, value) => kind switch
            {
                ConstExprScanner.OperandKind.HeapType => ConstExprScanner.CanonicalAbstractHeapType(value),

                // Indices are not normalised — there is one spelling of an index. Named affirmatively
                // rather than left to a `_` arm so a new operand kind fails to compile here instead of
                // being silently copied, which is the defect this walk exists to prevent.
                ConstExprScanner.OperandKind.FunctionIndex or
                ConstExprScanner.OperandKind.GlobalIndex or
                ConstExprScanner.OperandKind.TypeIndex => value,

                _ => throw new LinkerException(
                    $"Constant-expression operand kind {kind} has no write-time normalisation policy."),
            });
        }
        catch (LinkerException ex)
        {
            throw new LinkerException($"Cannot write {site}: {ex.Message}", ex);
        }
    }

    internal static byte[] RemapConstExprIndices(byte[] expr, IndexMap indexMap) =>
        ConstExprScanner.Rewrite(expr, (kind, value) => kind switch
        {
            ConstExprScanner.OperandKind.FunctionIndex =>
                ResolveMappedIndex(indexMap.Functions, checked((uint)value), "function"),
            ConstExprScanner.OperandKind.GlobalIndex =>
                ResolveMappedIndex(indexMap.Globals, checked((uint)value), "global"),

            // A GC constructor's operand is always a defined type index, never an abstract code.
            ConstExprScanner.OperandKind.TypeIndex =>
                ResolveMappedIndex(indexMap.Types, checked((uint)value), "type"),

            // `ref.null`: non-negative names a defined type the merge moves; negative is an abstract
            // code, which is normalised here as well as at write time so both producers agree.
            ConstExprScanner.OperandKind.HeapType => value >= 0
                ? ResolveMappedIndex(indexMap.Types, checked((uint)value), "type")
                : ConstExprScanner.CanonicalAbstractHeapType(value),

            _ => throw new LinkerException(
                $"Constant-expression operand kind {kind} has no merge-time remapping policy."),
        });

    /// <summary>
    /// Writes the type section as a flat list of <c>0x60</c> function types, refusing any type
    /// definition that shape cannot carry.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This writer has no encoding for a recursion group, a subtype wrapper, a struct, an array, a
    /// continuation, or a function type with a typed-reference operand. Emitting the compact
    /// <see cref="FuncType"/> anyway does not fail — it produces a <em>different, valid</em> module.
    /// A struct type came out as <c>(func)</c>, and a table declared <c>(ref null $s)</c> then named
    /// that fabricated function type; the result passed nesm's validator and
    /// <c>wasm-tools validate -f all</c>, because every erasure here widens (GitHub #383).
    /// </para>
    /// <para>
    /// So the check is on the exact <see cref="TypeDefinition"/> list, not on the compact one that
    /// already lost the evidence. When the module records no type definitions it was built
    /// programmatically from <see cref="FuncType"/>s, which are function types by construction and
    /// have nothing to check.
    /// </para>
    /// </remarks>
    private static void WriteTypeSection(
        BufferWriterStream stream,
        List<FuncType> types,
        List<TypeDefinition>? typeDefinitions = null)
    {
        bool exact = typeDefinitions is { Count: > 0 };
        if (exact)
        {
            if (typeDefinitions!.Count != types.Count)
            {
                throw new LinkerException(
                    $"The exact type space holds {typeDefinitions.Count} definitions but the compact type list " +
                    $"holds {types.Count}. The two run in lockstep — entry i of one describes entry i of the " +
                    "other — so a length mismatch means some type would be written from the wrong definition " +
                    "(GitHub #383).");
            }

            for (int i = 0; i < typeDefinitions.Count; i++)
                RequireWritableTypeDefinition(typeDefinitions[i], i);
        }

        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)types.Count);
        Span<WasmValType> scratch = stackalloc WasmValType[128];
        for (int i = 0; i < types.Count; i++)
        {
            if (exact && typeDefinitions![i].CompositeType is CompositeType.Function function)
            {
                int parameterCount = function.Parameters.Count;
                int length = checked(parameterCount + function.Results.Count);
                WasmValType[]? rented = length > scratch.Length ? System.Buffers.ArrayPool<WasmValType>.Shared.Rent(length) : null;
                try
                {
                    Span<WasmValType> values = rented is null ? scratch[..length] : rented.AsSpan(0, length);
                    for (int j = 0; j < parameterCount; j++)
                        values[j] = GetExactValueType(function.Parameters[j], typeDefinitions, $"type {i} parameter {j}");
                    for (int j = 0; j < function.Results.Count; j++)
                        values[parameterCount + j] = GetExactValueType(function.Results[j], typeDefinitions, $"type {i} result {j}");
                    entries.FuncType(values[..parameterCount], values[parameterCount..]);
                }
                finally
                {
                    if (rented is not null)
                        System.Buffers.ArrayPool<WasmValType>.Shared.Return(rented);
                }
            }
            else
            {
                entries.FuncType(types[i].Parameters, types[i].Results);
            }
        }
    }

    /// <inheritdoc cref="WriteTypeSection"/>
    private static void RequireWritableTypeDefinition(TypeDefinition definition, int index)
    {
        if (definition.CompositeType is not CompositeType.Function function)
        {
            throw new LinkerException(
                $"Type {index} is a {definition.CompositeType.GetType().Name.ToLowerInvariant()} type. This writer's " +
                "type section emits function types only, so emitting it would produce a module that validates and " +
                "means something else (GitHub #383; type-section support for the GC types is a separate gap).");
        }

        if (!definition.IsFinal || definition.SuperTypeIndices.Count > 0)
        {
            throw new LinkerException(
                $"Type {index} declares a subtype relationship (final={definition.IsFinal}, " +
                $"supertypes=[{string.Join(",", definition.SuperTypeIndices)}]). This writer emits bare 0x60 " +
                "function types, which would silently drop it and change the module's subtyping.");
        }

        if (definition.RecGroup.Count != 1)
        {
            throw new LinkerException(
                $"Type {index} belongs to a recursion group of {definition.RecGroup.Count} members. This writer " +
                "emits each type standalone, which would break the group's mutual references.");
        }

        CheckOperands(function.Parameters, index);
        CheckOperands(function.Results, index);

        static void CheckOperands(IReadOnlyList<Microsoft.NET.WebAssembly.Types.ValueType> operands, int index)
        {
            for (int i = 0; i < operands.Count; i++)
            {
                if (operands[i] is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType } reference)
                {
                    throw new LinkerException(
                        $"Type {index} has operand '{reference.Key}', a typed reference. The compact value type this " +
                        "writer emits erases it to funcref, which widens the signature rather than failing.");
                }
            }
        }
    }

    /// <summary>
    /// Writes one value type in the exact spelling the module declared, rather than the compact
    /// <see cref="ValType"/> byte it collapses to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The compact byte is a <em>widening</em>: <see cref="ValType.FuncRef"/> is <c>(ref null func)</c>,
    /// so writing it for a declared <c>(ref func)</c> makes a non-nullable operand nullable. wasm-tools
    /// accepted <c>(func (param (ref func)))</c>, nesm re-emitted it as <c>(func (param funcref))</c>,
    /// and the result validated — because the erasure only ever widens, so no validator on either side
    /// can see it (GitHub #383). Every widening in this family has that shape, which is why the tests
    /// for it compare bytes rather than re-validating.
    /// </para>
    /// <para>
    /// This is the one encoder for reference types in the writer. Parameters, results, global content
    /// types, locals and table element types all reach it, so the nullability and abstract-code rules
    /// cannot drift between them — the divergence that
    /// <see cref="AbstractHeapTypeCodes"/> exists to prevent on the decode side.
    /// </para>
    /// </remarks>
    private static void WriteExactValueType(
        Stream stream,
        Microsoft.NET.WebAssembly.Types.ValueType exact,
        IReadOnlyList<TypeDefinition>? typeDefinitions,
        string site)
    {
        WasmValType type = GetExactValueType(exact, typeDefinitions, site);
        Span<byte> encoded = stackalloc byte[1 + WasmLeb128.MaxLength];
        stream.Write(encoded[..type.Encode(encoded)]);
    }

    private static WasmValType GetExactValueType(
        Microsoft.NET.WebAssembly.Types.ValueType exact,
        IReadOnlyList<TypeDefinition>? typeDefinitions,
        string site)
    {
        if (exact is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined })
            RequireWritableDefinedType(defined.TypeIndex, typeDefinitions, site);
        if (WasmValType.TryFromModel(exact, out WasmValType type))
            return type;
        throw new LinkerException(
            $"Cannot write {site}: '{exact.Key}' is a value type this writer has no encoding for. " +
            "Emitting a compact byte instead would silently change the type (GitHub #383).");
    }

    private static void WriteImportSection(
        BufferWriterStream stream,
        List<Import> imports,
        IReadOnlyList<TypeDefinition>? typeDefinitions = null)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)imports.Count);
        Span<byte> moduleScratch = stackalloc byte[Utf8Scratch.StackSize];
        Span<byte> nameScratch = stackalloc byte[Utf8Scratch.StackSize];
        foreach (var import in imports)
        {
            using var module = new Utf8Scratch(import.ModuleName, moduleScratch);
            using var name = new Utf8Scratch(import.Name, nameScratch);
            switch (import.Descriptor)
            {
                case ImportDescriptor.Func function:
                    entries.FunctionImport(module.Bytes, name.Bytes, (uint)function.TypeIndex);
                    break;
                case ImportDescriptor.Table table:
                    Require32BitLimits(table.Type.Limits, "table64");
                    entries.TableImport(module.Bytes, name.Bytes, GetTableElementType(table.Type, typeDefinitions),
                        table.Type.Limits.Min, table.Type.Limits.Max);
                    break;
                case ImportDescriptor.Memory memory:
                    Require32BitLimits(memory.Type.Limits, "memory64");
                    entries.MemoryImport(module.Bytes, name.Bytes, memory.Type.Limits.Min, memory.Type.Limits.Max,
                        shared: memory.Type.Shared);
                    break;
                case ImportDescriptor.Global global:
                    entries.GlobalImport(module.Bytes, name.Bytes,
                        GetGlobalContentType(global.Type, typeDefinitions, "imported global content type"),
                        global.Type.Mutability);
                    break;
                case ImportDescriptor.Tag tag:
                    entries.TagImport(module.Bytes, name.Bytes, (uint)tag.TypeIndex);
                    break;
                default:
                    throw new LinkerException($"Unsupported import descriptor type '{import.Descriptor.GetType().Name}'.");
            }
        }
    }

    private static void WriteFunctionSection(BufferWriterStream stream, List<WasmFunction> functions)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)functions.Count);
        foreach (var function in functions)
            entries.Function((uint)function.TypeIndex);
    }

    private static void WriteTableSection(
        BufferWriterStream stream,
        List<TableType> tables,
        IReadOnlyList<TypeDefinition>? typeDefinitions = null)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)tables.Count);
        foreach (var table in tables)
        {
            Require32BitLimits(table.Limits, "table64");
            WasmValType type = GetTableElementType(table, typeDefinitions);
            if (table.InitExpr.Length == 0)
                entries.Table(type, table.Limits.Min, table.Limits.Max);
            else
                entries.Table(type, table.Limits.Min, table.Limits.Max, false,
                    NormalizeConstExpr(table.InitExpr, "table initializer"));
        }
    }

    private static void WriteMemorySection(BufferWriterStream stream, List<MemoryType> memories)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)memories.Count);
        foreach (var memory in memories)
        {
            Require32BitLimits(memory.Limits, "memory64");
            entries.Memory(memory.Limits.Min, memory.Limits.Max, shared: memory.Shared);
        }
    }

    private static void WriteTagSection(BufferWriterStream stream, List<Tag> tags)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)tags.Count);
        foreach (var tag in tags)
            entries.Tag((uint)tag.TypeIndex);
    }

    private static void WriteGlobalSection(
        BufferWriterStream stream,
        List<WasmGlobal> globals,
        IReadOnlyList<TypeDefinition>? typeDefinitions)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)globals.Count);
        for (int i = 0; i < globals.Count; i++)
        {
            var global = globals[i];
            entries.Global(GetGlobalContentType(global.Type, typeDefinitions, $"global {i} content type"),
                global.Type.Mutability, NormalizeConstExpr(global.InitExpr, $"global {i} initializer"));
        }
    }

    private static void WriteExportSection(BufferWriterStream stream, OrderedDictionary<string, Export> exports)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)exports.Count);
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        foreach (var (name, export) in exports)
        {
            using var utf8 = new Utf8Scratch(name, scratch);
            (WasmExternalKind kind, uint index) = export.Descriptor switch
            {
                ExportDescriptor.Func function => (WasmExternalKind.Function, (uint)function.FunctionIndex),
                ExportDescriptor.Table table => (WasmExternalKind.Table, (uint)table.TableIndex),
                ExportDescriptor.Memory memory => (WasmExternalKind.Memory, (uint)memory.MemoryIndex),
                ExportDescriptor.Global global => (WasmExternalKind.Global, (uint)global.GlobalIndex),
                ExportDescriptor.Tag tag => (WasmExternalKind.Tag, (uint)tag.TagIndex),
                _ => throw new LinkerException($"Unsupported export descriptor type '{export.Descriptor.GetType().Name}'."),
            };
            entries.Export(utf8.Bytes, kind, index);
        }
    }

    private static void WriteElementSection(
        BufferWriterStream stream,
        List<Element> elements,
        IReadOnlyList<TypeDefinition>? typeDefinitions)
    {
        WasmModuleEntryWriter entries = stream.Entries;
        entries.Count((uint)elements.Count);
        for (int i = 0; i < elements.Count; i++)
        {
            var element = elements[i];
            bool expressionForm = element.UsesExpressions || element.ElementExpressions.Count > 0;
            bool implicitExpressionType = IsNullableAbstractFunc(element.ElementType);
            bool explicitTable = element.HasExplicitTableIndex || element.TableIndex != 0 ||
                (expressionForm && !implicitExpressionType);

            ElementSegmentKind kind = element.Mode switch
            {
                ElementMode.Active when expressionForm => explicitTable ? ElementSegmentKind.ActiveTableExpressions : ElementSegmentKind.ActiveExpressions,
                ElementMode.Passive when expressionForm => ElementSegmentKind.PassiveExpressions,
                ElementMode.Declarative when expressionForm => ElementSegmentKind.DeclarativeExpressions,
                ElementMode.Active => explicitTable ? ElementSegmentKind.ActiveTableFunctions : ElementSegmentKind.ActiveFunctions,
                ElementMode.Passive => ElementSegmentKind.PassiveFunctions,
                ElementMode.Declarative => ElementSegmentKind.DeclarativeFunctions,
                _ => throw new LinkerException($"Element segment {i} has unknown mode {element.Mode}."),
            };

            uint tableIndex = 0;
            ReadOnlySpan<byte> offset = default;
            if (element.Mode == ElementMode.Active)
            {
                if (element.TableIndex < 0)
                    throw new LinkerException($"Active element segment {i} has no table index.");
                if (explicitTable)
                    tableIndex = (uint)element.TableIndex;
                offset = GetActiveOffsetExpr(element.OffsetExpr, $"element segment {i} offset");
            }

            if (expressionForm)
            {
                if (element.ElementType is not Microsoft.NET.WebAssembly.Types.ValueType.Reference reference)
                    throw new LinkerException($"Element segment {i} has non-reference type '{element.ElementType.Key}'.");
                WasmValType type = GetExactValueType(reference, typeDefinitions, $"element segment {i} type");
                int count = element.ElementExpressions.Count;
                ReadOnlyMemory<byte>[] expressions = System.Buffers.ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(count);
                try
                {
                    for (int j = 0; j < count; j++)
                        expressions[j] = NormalizeConstExpr(element.ElementExpressions[j], $"element segment {i} expression {j}");
                    entries.ElementSegment(kind, tableIndex, offset, type, expressions.AsSpan(0, count));
                }
                finally
                {
                    System.Buffers.ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(expressions, clearArray: true);
                }
                continue;
            }

            List<int> functionIndices =
                element.Mode == ElementMode.Declarative && element.DeclaredFunctionIndices.Count > 0
                    ? element.DeclaredFunctionIndices
                    : element.FunctionIndices;
            for (int j = 0; j < functionIndices.Count; j++)
            {
                if (functionIndices[j] < 0)
                    throw new LinkerException(
                        $"Element segment {i} funcidx entry {j} is negative ({functionIndices[j]}).");
            }
            ReadOnlySpan<int> indices = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(functionIndices);
            entries.ElementSegment(kind, tableIndex, offset, System.Runtime.InteropServices.MemoryMarshal.Cast<int, uint>(indices));
        }
    }

    private static bool IsNullableAbstractFunc(Microsoft.NET.WebAssembly.Types.ValueType type) =>
        type is Microsoft.NET.WebAssembly.Types.ValueType.Reference
        {
            Nullability: Nullability.Nullable,
            HeapType: AbstractHeapType { Kind: HeapTypeKind.Func },
        };

    private static byte[] GetActiveOffsetExpr(byte[] offsetExpr, string site) =>
        offsetExpr.Length == 0 ? BuildI32ConstExpr(0) : NormalizeConstExpr(offsetExpr, site);

    /// <summary>The size of each function body as <see cref="EncodedModule"/> writes it, without encoding the instructions.</summary>
    internal static uint[] GetEncodedFunctionBodySizes(IReadOnlyList<WasmFunction> functions, IReadOnlyList<TypeDefinition>? typeDefinitions)
    {
        var sizes = new uint[functions.Count];
        using var locals = new MemoryStream();
        for (int i = 0; i < functions.Count; i++)
        {
            WasmFunction function = functions[i];
            locals.SetLength(0);
            WriteLocals(locals, function, typeDefinitions, i);
            sizes[i] = checked((uint)(locals.Length + function.Body.Length + (AppendsEnd(function) ? 1 : 0)));
        }

        return sizes;
    }

    /// <summary>
    /// The length <see cref="EncodedModule"/> encodes <paramref name="function"/> (function <paramref name="index"/>)
    /// to, measured without copying its body; <paramref name="scratch"/> receives its locals.
    /// </summary>
    internal static int EncodedFunctionBodyLength(WasmFunction function, IReadOnlyList<TypeDefinition>? typeDefinitions, int index, MemoryStream scratch)
    {
        scratch.SetLength(0);
        WriteLocals(scratch, function, typeDefinitions, index);
        return checked((int)scratch.Length + function.Body.Length + (AppendsEnd(function) ? 1 : 0));
    }

    private static IReadOnlyList<(uint Offset, BranchHintKind Kind)> ResolveBranchHintOffsets(
        WasmFunction function,
        uint instructionBase,
        int localFunctionIndex)
    {
        if (function.BranchHints.Count == 0)
            return Array.Empty<(uint Offset, BranchHintKind Kind)>();

        var resolved = new List<(uint Offset, BranchHintKind Kind)>(function.BranchHints.Count);
        var cursor = new InstructionCursor(function.Body, WasmFeatures.All);
        int ordinal = 0;
        while (cursor.MoveNext(out var instruction))
        {
            if (function.BranchHints.TryGetValue(ordinal, out BranchHint? hint))
            {
                byte opcode = instruction.OpcodeId.Opcode;
                if (opcode is not WasmOpcode.If and not WasmOpcode.BrIf)
                {
                    throw new LinkerException(
                        $"Function {localFunctionIndex} branch hint at instruction {ordinal} " +
                        $"targets opcode 0x{opcode:X2}, not if or br_if.");
                }

                resolved.Add((
                    checked(instructionBase + (uint)instruction.ByteOffset),
                    hint.Kind));
            }
            ordinal++;
        }

        if (resolved.Count != function.BranchHints.Count)
        {
            int missingOrdinal = function.BranchHints.Keys
                .Order()
                .First(key => key >= ordinal);
            throw new LinkerException(
                $"Function {localFunctionIndex} branch hint references missing instruction ordinal {missingOrdinal}.");
        }

        return resolved;
    }

    private static uint CountFunctionImports(IReadOnlyList<Import> imports)
    {
        uint count = 0;
        foreach (Import import in imports)
        {
            if (import.Descriptor is ImportDescriptor.Func)
                count++;
        }
        return count;
    }

    /// <summary>
    /// Writes the branch-hint custom section (name included) for <paramref name="functions"/>, whose instructions
    /// start after their encoded locals.
    /// </summary>
    private static void WriteBranchHintSection(
        Stream stream,
        List<WasmFunction> functions,
        FunctionPrefix[] prefixes,
        uint importedFunctionCount)
    {
        WriteName(stream, BranchHintMetadataReader.SectionName);
        uint hintedFunctionCount = 0;
        foreach (WasmFunction function in functions)
        {
            if (function.BranchHints.Count != 0)
                hintedFunctionCount++;
        }
        WasmEncoder.WriteU32Leb(stream, hintedFunctionCount);

        for (int i = 0; i < functions.Count; i++)
        {
            if (functions[i].BranchHints.Count == 0)
                continue;

            uint instructionBase = (uint)prefixes[i].LocalsLength;
            IReadOnlyList<(uint Offset, BranchHintKind Kind)> hints = ResolveBranchHintOffsets(functions[i], instructionBase, i);
            WasmEncoder.WriteU32Leb(stream, checked(importedFunctionCount + (uint)i));
            WasmEncoder.WriteU32Leb(stream, checked((uint)hints.Count));
            foreach ((uint offset, BranchHintKind kind) in hints)
            {
                WasmEncoder.WriteU32Leb(stream, offset);
                WasmEncoder.WriteU32Leb(stream, 1);
                stream.WriteByte((byte)kind);
            }
        }
    }

    /// <summary>
    /// Writes a function's local declarations, using the exact locals when the module recorded them.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Run-length grouping is done over the <em>encoded</em> bytes rather than over
    /// <see cref="WasmFunction.Locals"/>, because the compact list is the lossy one: a
    /// <c>(ref func)</c> local and a <c>funcref</c> local are both <see cref="ValType.FuncRef"/>
    /// there, so grouping by it would merge two different declarations into one run and then emit
    /// whichever spelling came first.
    /// </para>
    /// <para>
    /// <see cref="WasmFunction.ExactLocals"/> is used only when it covers the compact list exactly.
    /// A partial list would silently apply exact spellings to a prefix and compact ones to the rest,
    /// which is a worse failure than not using it at all (GitHub #383).
    /// </para>
    /// </remarks>
    private static void WriteLocals(
        Stream stream,
        WasmFunction function,
        IReadOnlyList<TypeDefinition>? typeDefinitions,
        int functionIndex)
    {
        int count = function.Locals.Count;
        if (count == 0)
        {
            WasmEncoder.WriteU32Leb(stream, 0);
            return;
        }

        // Locals are written as runs of equal encodings, so the run count is counted first.
        var encodings = new LocalEncodings(function, typeDefinitions, functionIndex);
        uint groupCount = 1;
        for (int i = 1; i < count; i++)
        {
            if (!encodings.Equal(i - 1, i))
                groupCount++;
        }

        WasmEncoder.WriteU32Leb(stream, groupCount);
        for (int i = 0; i < count;)
        {
            int start = i++;
            while (i < count && encodings.Equal(start, i))
                i++;
            WasmEncoder.WriteU32Leb(stream, (uint)(i - start));
            encodings.Write(stream, start);
        }
    }

    /// <summary>
    /// Compares and writes a function's local types as they are encoded. A local without an exact type, or with a
    /// numeric or vector one (nearly every local), is a single byte and is compared without encoding; a reference
    /// local is encoded into a scratch buffer, which only a function with one allocates.
    /// </summary>
    private struct LocalEncodings(WasmFunction function, IReadOnlyList<TypeDefinition>? typeDefinitions, int functionIndex)
    {
        private readonly bool _exact = function.ExactLocals.Count == function.Locals.Count;
        private MemoryStream? _scratch;

        public bool Equal(int a, int b)
        {
            if (TryGetByte(a, out byte left) && TryGetByte(b, out byte right))
                return left == right;

            _scratch ??= new MemoryStream();
            _scratch.SetLength(0);
            Write(_scratch, a);
            int length = (int)_scratch.Length;
            Write(_scratch, b);
            ReadOnlySpan<byte> encoded = _scratch.GetBuffer().AsSpan(0, (int)_scratch.Length);
            return encoded[..length].SequenceEqual(encoded[length..]);
        }

        public readonly void Write(Stream stream, int index)
        {
            if (TryGetByte(index, out byte value))
                stream.WriteByte(value);
            else
                WriteExactValueType(stream, function.ExactLocals[index], typeDefinitions, $"function {functionIndex} local {index}");
        }

        private readonly bool TryGetByte(int index, out byte value)
        {
            if (!_exact)
            {
                value = (byte)function.Locals[index];
                return true;
            }

            if (function.ExactLocals[index] is Microsoft.NET.WebAssembly.Types.ValueType.Number or Microsoft.NET.WebAssembly.Types.ValueType.Vector)
            {
                value = (byte)function.ExactLocals[index].ToStorageValType();
                return true;
            }

            value = 0;
            return false;
        }
    }

    /// <summary>Writes data segment <paramref name="index"/> up to its content: mode, memory, offset and length.</summary>
    private static void WriteDataSegmentHeader(BufferWriterStream stream, Data segment, int index)
    {
        if (segment.IsPassive)
        {
            int size = DataSegmentEncoder.GetPassiveHeaderSize(segment.InitData.Length);
            int written = DataSegmentEncoder.EncodePassiveHeader(stream.GetSpan(size), segment.InitData.Length);
            stream.Advance(written);
        }
        else
        {
            ReadOnlySpan<byte> offset = GetActiveOffsetExpr(segment.OffsetExpr, $"data segment {index} offset");
            int size = DataSegmentEncoder.GetActiveHeaderSize(offset, segment.InitData.Length, memoryIndex: (uint)segment.MemoryIndex);
            int written = DataSegmentEncoder.EncodeActiveHeader(stream.GetSpan(size), offset, segment.InitData.Length,
                memoryIndex: (uint)segment.MemoryIndex);
            stream.Advance(written);
        }
    }

    /// <summary>
    /// Writes a table's element type from its exact reference type, spelling every reference type
    /// the format can express rather than the two <see cref="RefType"/> can hold.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="TableType.ElementType"/> alone is lossy in a way that is invisible downstream:
    /// <see cref="RefType"/> has exactly two members, so <c>(ref null eq)</c>, <c>(ref null $t)</c>
    /// and <c>(ref func)</c> all collapse into <c>funcref</c> or <c>externref</c>. Every such erasure
    /// only ever <em>widens</em> the type, so before initializers were emitted the mangled module
    /// still passed nesm's validator and <c>wasm-tools validate -f all</c> — which is why a
    /// "re-emit then re-validate" assertion is structurally incapable of catching this, and why the
    /// tests assert bytes instead. With an initializer present the erasure stops being silent and
    /// starts producing modules that fail to re-read, because the surviving expression no longer
    /// matches the widened element type (GitHub #383).
    /// </para>
    /// <para>
    /// Every abstract heap type has a nullable shorthand byte, so the nullable spellings still
    /// round-trip through one byte and existing output is unchanged. Non-nullable abstract types and
    /// defined types need the long <c>0x63</c>/<c>0x64 heaptype</c> form.
    /// </para>
    /// <para>
    /// <b>Defined types are guarded rather than trusted.</b> This writer's type section emits only
    /// <c>0x60</c> function types — it has no representation for a recursion group, a struct or an
    /// array — so an index naming any of those would survive into the output pointing at a
    /// fabricated func type. That is a silent wrong answer, and worse than the widening it replaces,
    /// so it throws instead. Function types survive at their original indices, which is exactly what
    /// makes the sound case sound.
    /// </para>
    /// </remarks>
    private static WasmValType GetTableElementType(
        TableType tableType,
        IReadOnlyList<TypeDefinition>? typeDefinitions)
    {
        if (tableType.ExactElementType is not Microsoft.NET.WebAssembly.Types.ValueType.Reference reference)
            return (ValType)tableType.ElementType;

        return GetExactValueType(reference, typeDefinitions, "table element type");
    }

    /// <summary>
    /// Rejects a defined-type element index this writer cannot honestly carry.
    /// </summary>
    /// <remarks>
    /// <see cref="WriteTypeSection"/> emits <c>module.Types</c> as <c>0x60</c> function types and
    /// has no <see cref="TypeDefinition"/> support at all, so a struct, array, continuation or
    /// recursion-group member at <paramref name="typeIndex"/> is already gone by the time the table
    /// is written. Emitting the index anyway produces a module that validates and means something
    /// else. When no type definitions are supplied the module was built programmatically and
    /// <c>Types</c> is a function-type list by construction, so there is nothing to check.
    /// </remarks>
    private static void RequireWritableDefinedType(
        int typeIndex,
        IReadOnlyList<TypeDefinition>? typeDefinitions,
        string site)
    {
        if (typeDefinitions is null || typeDefinitions.Count == 0)
            return;

        if ((uint)typeIndex >= (uint)typeDefinitions.Count)
        {
            throw new LinkerException(
                $"{site} names type index {typeIndex}, which is outside the merged type space " +
                $"of {typeDefinitions.Count} entries.");
        }

        var definition = typeDefinitions[typeIndex];
        if (definition.CompositeType is not CompositeType.Function)
        {
            throw new LinkerException(
                $"{site} names type index {typeIndex}, which is a " +
                $"{definition.CompositeType.GetType().Name.ToLowerInvariant()} type. This writer's type section " +
                "emits function types only, so the index would survive into the output pointing at a " +
                "fabricated function type (GitHub #383; the missing type-section support is a separate gap).");
        }
    }

    private static WasmValType GetGlobalContentType(
        GlobalType globalType,
        IReadOnlyList<TypeDefinition>? typeDefinitions = null,
        string site = "global content type")
    {
        if (globalType.ExactContentType is { } exact)
            return GetExactValueType(exact, typeDefinitions, site);
        return globalType.ContentType;
    }

    private static void Require32BitLimits(Limits limits, string feature)
    {
        if (limits.Is64)
            throw new LinkerException(Wasm64Guard.NotSupportedMessage(feature, "linking"));
    }

    /// <summary>Writes a name that is already UTF-8, such as a <c>u8</c> literal.</summary>
    private static void WriteName(Stream stream, ReadOnlySpan<byte> utf8)
    {
        WasmEncoder.WriteU32Leb(stream, (uint)utf8.Length);
        stream.Write(utf8);
    }

    private static void WriteName(Stream stream, string value)
    {
        int byteCount = Encoding.UTF8.GetByteCount(value);
        WasmEncoder.WriteU32Leb(stream, (uint)byteCount);
        if (byteCount <= 256)
        {
            Span<byte> buf = stackalloc byte[byteCount];
            Encoding.UTF8.GetBytes(value, buf);
            stream.Write(buf);
        }
        else
        {
            stream.Write(Encoding.UTF8.GetBytes(value));
        }
    }

    private readonly record struct ImportKey(string ModuleName, string Name, ImportDescriptor Descriptor);
    private readonly record struct MergedImportEntry(int SectionIndex, uint KindIndex);
}

/// <summary>Result of import-section merging, including the merged section entries and per-kind import counts.</summary>
public sealed class ImportMergeResult
{
    public required List<Import> Imports { get; init; }
    public required uint FunctionImportCount { get; init; }
    public required uint GlobalImportCount { get; init; }
    public required uint TableImportCount { get; init; }
    public required uint MemoryImportCount { get; init; }
    public required uint TagImportCount { get; init; }
}
