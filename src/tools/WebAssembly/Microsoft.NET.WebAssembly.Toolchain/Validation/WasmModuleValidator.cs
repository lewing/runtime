// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Post-parse semantic validator.  Checks a parsed <see cref="WasmModule"/> against
/// the Wasm spec §3 validity rules.  Never throws — callers inspect the returned
/// <see cref="WasmValidationResult"/> or call <see cref="ValidateAndThrow(WasmModule, WasmFeatures)"/>.
/// </summary>
public static class WasmModuleValidator
{
    /// <summary>
    /// Validates a parsed module.  Returns a result containing all diagnostics.
    /// </summary>
    public static WasmValidationResult Validate(
        WasmModule module,
        WasmFeatures features = WasmFeatures.Default)
        => Validate(module, features, prevalidatedBodies: null);

    /// <summary>
    /// <see cref="Validate(WasmModule, WasmFeatures)"/> for a module just decoded by a validating load, taking each
    /// function body's result from <paramref name="prevalidatedBodies"/> where the decoder recorded one.
    /// </summary>
    internal static WasmValidationResult Validate(
        WasmModule module,
        WasmFeatures features,
        FunctionBodyValidator.FusedBodyValidation? prevalidatedBodies)
        => Validate(
            module,
            features,
            validateTypeDeclarations: true,
            validateFunctionBodies: true,
            validateConstantExpressions: true,
            validateRemainingModuleHygiene: true,
            prevalidatedBodies);

    internal static WasmValidationResult ValidateForTests(
        WasmModule module,
        WasmFeatures features = WasmFeatures.Default,
        bool validateTypeDeclarations = false,
        bool validateFunctionBodies = false,
        bool validateConstantExpressions = true,
        bool validateRemainingModuleHygiene = true)
        => Validate(
            module,
            features,
            validateTypeDeclarations,
            validateFunctionBodies,
            validateConstantExpressions,
            validateRemainingModuleHygiene,
            prevalidatedBodies: null);

    private static WasmValidationResult Validate(
        WasmModule module,
        WasmFeatures features,
        bool validateTypeDeclarations,
        bool validateFunctionBodies,
        bool validateConstantExpressions,
        bool validateRemainingModuleHygiene,
        FunctionBodyValidator.FusedBodyValidation? prevalidatedBodies)
    {
        var diagnostics = new List<WasmValidationDiagnostic>();

        CheckTypeMetadataCoherence(module, diagnostics);

        if (validateTypeDeclarations)
            diagnostics.AddRange(TypeDeclarationValidator.Validate(module).Diagnostics);

        // ── Build combined index-space counts ──────────────────────────────
        int importedFunctions = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        int importedMemories  = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Memory);
        var tableTypes = module.Imports
            .Select(import => import.Descriptor)
            .OfType<ImportDescriptor.Table>()
            .Select(table => table.Type)
            .Concat(module.Tables)
            .ToArray();

        int totalFunctions = importedFunctions + module.Functions.Count;
        int totalTables    = tableTypes.Length;
        int totalMemories  = importedMemories  + module.Memories.Count;
        int totalGlobals   = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Global) + module.Globals.Count;

        // ── Limits (defined memories + tables) ────────────────────────────
        foreach (var mem in module.Memories)
        {
            CheckLimits(mem.Limits, diagnostics);
            if (validateRemainingModuleHygiene)
                CheckMemoryAbsoluteLimits(mem.Limits, diagnostics, "Memory");
        }
        foreach (var tbl in module.Tables)
        {
            CheckLimits(tbl.Limits, diagnostics);
            CheckTableAbsoluteLimits(tbl.Limits, diagnostics, "Table");
            if (validateRemainingModuleHygiene &&
                tbl.InitExpr.Length == 0 &&
                TableElementType(tbl) is Microsoft.NET.WebAssembly.Types.ValueType.Reference { Nullability: Nullability.NonNullable } elementType)
            {
                Add(diagnostics, WasmValidationCode.TableElementTypeNotDefaultable,
                    $"Table element type {elementType.Key} is not defaultable and requires an explicit initializer");
            }
        }

        // ── Limits (imported memories + tables) ───────────────────────────
        foreach (var imp in module.Imports)
        {
            if (imp.Descriptor is ImportDescriptor.Memory mImp)
            {
                CheckLimits(mImp.Type.Limits, diagnostics);
                if (validateRemainingModuleHygiene)
                    CheckMemoryAbsoluteLimits(mImp.Type.Limits, diagnostics, "Imported memory");
            }
            else if (imp.Descriptor is ImportDescriptor.Table tImp)
            {
                CheckLimits(tImp.Type.Limits, diagnostics);
                CheckTableAbsoluteLimits(tImp.Type.Limits, diagnostics, "Imported table");
            }
        }

        // ── MVP singleton constraints ─────────────────────────────────────
        // Multi-memory proposal (finalized): allow multiple memories.
        // Runtime allocates all declared memories; interpreter uses Memories[idx].
        if (totalTables > 1)
        {
            // Post-MVP reference-types proposal allows multiple tables; only warn, don't fail.
            // We support them at runtime, so remove the hard error here.
        }

        // ── Function type indices ─────────────────────────────────────────
        foreach (var func in module.Functions)
        {
            if (func.TypeIndex < 0 || func.TypeIndex >= module.Types.Count)
                Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                    $"Function type index {func.TypeIndex} out of range [0, {module.Types.Count})");
            else if (!IsFunctionType(module, func.TypeIndex))
                Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                    $"Function type index {func.TypeIndex} does not identify a function type");
        }
        foreach (var imp in module.Imports)
        {
            if (imp.Descriptor is ImportDescriptor.Func fImp &&
                (fImp.TypeIndex < 0 || fImp.TypeIndex >= module.Types.Count ||
                 !IsFunctionType(module, fImp.TypeIndex)))
            {
                Add(
                    diagnostics,
                    WasmValidationCode.TypeIndexOutOfRange,
                    $"Imported function type index {fImp.TypeIndex} does not identify a function type");
            }
        }

        bool allowStackSwitchingTagResults = features.HasFlag(WasmFeatures.StackSwitching);
        foreach (var tag in module.Tags)
            CheckTagType(module, tag.TypeIndex, "Tag", allowStackSwitchingTagResults, diagnostics);
        foreach (var import in module.Imports)
        {
            if (import.Descriptor is ImportDescriptor.Tag tag)
            {
                CheckTagType(
                    module,
                    tag.TypeIndex,
                    $"Imported tag '{import.ModuleName}.{import.Name}'",
                    allowStackSwitchingTagResults,
                    diagnostics);
            }
        }

        if (validateRemainingModuleHygiene)
            CheckDefinedHeapTypeReferences(module, diagnostics);

        // ── Export index bounds ───────────────────────────────────────────
        var exportNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exp in ExportEntries(module))
        {
            if (validateRemainingModuleHygiene && !exportNames.Add(exp.Name))
                Add(diagnostics, WasmValidationCode.DuplicateExportName,
                    $"Duplicate export name '{exp.Name}'");

            switch (exp.Descriptor)
            {
                case ExportDescriptor.Func f:
                    if (f.FunctionIndex < 0 || f.FunctionIndex >= totalFunctions)
                        Add(diagnostics, WasmValidationCode.FunctionIndexOutOfRange,
                            $"Export '{exp.Name}' function index {f.FunctionIndex} out of range [0, {totalFunctions})");
                    break;
                case ExportDescriptor.Table t:
                    if (t.TableIndex < 0 || t.TableIndex >= totalTables)
                        Add(diagnostics, WasmValidationCode.TableIndexOutOfRange,
                            $"Export '{exp.Name}' table index {t.TableIndex} out of range [0, {totalTables})");
                    break;
                case ExportDescriptor.Memory m:
                    if (m.MemoryIndex < 0 || m.MemoryIndex >= totalMemories)
                        Add(diagnostics, WasmValidationCode.MemoryIndexOutOfRange,
                            $"Export '{exp.Name}' memory index {m.MemoryIndex} out of range [0, {totalMemories})");
                    break;
                case ExportDescriptor.Global g:
                    if (g.GlobalIndex < 0 || g.GlobalIndex >= totalGlobals)
                        Add(diagnostics, WasmValidationCode.GlobalIndexOutOfRange,
                            $"Export '{exp.Name}' global index {g.GlobalIndex} out of range [0, {totalGlobals})");
                    break;
            }
        }

        // ── Start function ────────────────────────────────────────────────
        if (module.StartFunction.HasValue)
        {
            int startIdx = module.StartFunction.Value;
            if (startIdx < 0 || startIdx >= totalFunctions)
            {
                Add(diagnostics, WasmValidationCode.StartFunctionIndexOutOfRange,
                    $"Start function index {startIdx} out of range [0, {totalFunctions})");
            }
            else
            {
                int typeIdx = GetFunctionTypeIndex(module, startIdx, importedFunctions);
                if (typeIdx >= 0 && typeIdx < module.Types.Count)
                {
                    var funcType = module.Types[typeIdx];
                    if (funcType.Parameters.Length != 0 || funcType.Results.Length != 0)
                        Add(diagnostics, WasmValidationCode.StartFunctionTypeMismatch,
                            $"Start function must have type [] -> []; found {funcType.Parameters.Length} param(s) and {funcType.Results.Length} result(s)");
                }
            }
        }

        // ── Element segments ──────────────────────────────────────────────
        SubtypeRelation? subtype = validateRemainingModuleHygiene
            ? new SubtypeRelation(module.TypeDefinitions)
            : null;
        foreach (var elem in module.Elements)
        {
            if (elem.Mode != ElementMode.Active)
                continue;

            if (elem.TableIndex < 0 || elem.TableIndex >= totalTables)
            {
                Add(diagnostics, WasmValidationCode.ElementTableIndexInvalid,
                    $"Element segment table index {elem.TableIndex} out of range [0, {totalTables})");
            }
            else if (subtype is not null)
            {
                var tableElementType = TableElementType(tableTypes[elem.TableIndex]);
                if (!subtype.IsSubtype(elem.ElementType, tableElementType))
                {
                    Add(diagnostics, WasmValidationCode.ElementTableTypeMismatch,
                        $"Active element segment type {elem.ElementType.Key} is not assignable to table {elem.TableIndex} element type {tableElementType.Key}");
                }
            }

            foreach (int fi in elem.FunctionIndices)
            {
                if (fi != -1 && fi >= totalFunctions)
                    Add(diagnostics, WasmValidationCode.ElementFunctionIndexOutOfRange,
                        $"Element segment function index {fi} out of range [0, {totalFunctions})");
            }
        }

        // ── Data segments ─────────────────────────────────────────────────
        foreach (var data in module.DataSegments)
        {
            if (data.IsPassive) continue; // passive segments have no memory target
            if (data.MemoryIndex < 0 || data.MemoryIndex >= totalMemories)
                Add(diagnostics, WasmValidationCode.DataMemoryIndexInvalid,
                    $"Data segment memory index {data.MemoryIndex} out of range [0, {totalMemories})");
        }

        // ── Constant expressions ──────────────────────────────────────────
        if (validateConstantExpressions)
            ConstantExpressionValidator.ValidateModule(module, WasmFeatures.All, diagnostics);

        if (validateFunctionBodies)
            diagnostics.AddRange(FunctionBodyValidator.ValidateModule(module, WasmFeatures.All, prevalidatedBodies).Diagnostics);

        return diagnostics.Count == 0
            ? WasmValidationResult.Valid
            : new WasmValidationResult(diagnostics);
    }

    /// <summary>
    /// Validates and throws <see cref="WasmValidationException"/> if any errors exist.
    /// </summary>
    public static void ValidateAndThrow(
        WasmModule module,
        WasmFeatures features = WasmFeatures.Default)
        => ValidateAndThrow(module, features, prevalidatedBodies: null);

    internal static void ValidateAndThrow(
        WasmModule module,
        WasmFeatures features,
        FunctionBodyValidator.FusedBodyValidation? prevalidatedBodies)
    {
        var result = Validate(module, features, prevalidatedBodies);
        if (!result.IsValid)
            throw new WasmValidationException(
                result.Diagnostics.Where(d => d.Severity == WasmValidationSeverity.Error).ToList());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CheckTypeMetadataCoherence(
        WasmModule module,
        List<WasmValidationDiagnostic> diagnostics)
    {
        if (module.TypeDefinitions.Count != 0 &&
            module.Types.Count != module.TypeDefinitions.Count)
        {
            Add(
                diagnostics,
                WasmValidationCode.TypeMetadataMismatch,
                $"Compact and exact type metadata counts differ: {module.Types.Count} and {module.TypeDefinitions.Count}");
            return;
        }

        int pairedTypeCount = Math.Min(module.Types.Count, module.TypeDefinitions.Count);
        for (int i = 0; i < pairedTypeCount; i++)
        {
            var compact = module.Types[i];
            var definition = module.TypeDefinitions[i];
            bool matches = definition.CompositeType switch
            {
                CompositeType.Function function =>
                    compact.Parameters.SequenceEqual(function.Parameters.Select(type => type.ToStorageValType())) &&
                    compact.Results.SequenceEqual(function.Results.Select(type => type.ToStorageValType())),
                CompositeType.Continuation continuation =>
                    continuation.FunctionTypeIndex >= 0 &&
                    continuation.FunctionTypeIndex < module.Types.Count &&
                    continuation.FunctionTypeIndex < module.TypeDefinitions.Count &&
                    module.TypeDefinitions[continuation.FunctionTypeIndex].CompositeType is CompositeType.Function &&
                    compact.Parameters.SequenceEqual(module.Types[continuation.FunctionTypeIndex].Parameters) &&
                    compact.Results.SequenceEqual(module.Types[continuation.FunctionTypeIndex].Results),
                CompositeType.Struct or CompositeType.Array =>
                    compact.Parameters.Length == 0 && compact.Results.Length == 0,
                _ => false,
            };

            if (!matches)
            {
                Add(
                    diagnostics,
                    WasmValidationCode.TypeMetadataMismatch,
                    $"Compact type metadata at index {i} does not match its exact type definition");
            }
        }

        foreach (var function in module.Functions)
        {
            if (function.ExactLocals.Count == 0)
                continue;

            if (function.ExactLocals.Count != function.Locals.Count ||
                !function.ExactLocals.Select(type => type.ToStorageValType()).SequenceEqual(function.Locals))
            {
                Add(
                    diagnostics,
                    WasmValidationCode.TypeMetadataMismatch,
                    "Compact and exact local type metadata differ");
            }
        }

        foreach (var table in module.Tables)
            CheckTableTypeCoherence(table, "Table", diagnostics);
        foreach (var global in module.Globals)
            CheckGlobalTypeCoherence(global.Type, "Global", diagnostics);
        foreach (var import in module.Imports)
        {
            switch (import.Descriptor)
            {
                case ImportDescriptor.Table table:
                    CheckTableTypeCoherence(
                        table.Type,
                        $"Imported table '{import.ModuleName}.{import.Name}'",
                        diagnostics);
                    break;
                case ImportDescriptor.Global global:
                    CheckGlobalTypeCoherence(
                        global.Type,
                        $"Imported global '{import.ModuleName}.{import.Name}'",
                        diagnostics);
                    break;
            }
        }
    }

    private static void CheckTableTypeCoherence(
        TableType table,
        string owner,
        List<WasmValidationDiagnostic> diagnostics)
    {
        if (table.ExactElementType is null)
            return;

        bool matches = (table.ElementType, table.ExactElementType.ToStorageValType()) switch
        {
            (RefType.FuncRef, ValType.FuncRef) => true,
            (RefType.FuncRef, ValType.ExnRef) => true,
            (RefType.FuncRef, ValType.ContRef) => true,
            (RefType.ExternRef, ValType.ExternRef) => true,
            _ => false,
        };
        if (!matches)
        {
            Add(
                diagnostics,
                WasmValidationCode.TypeMetadataMismatch,
                $"{owner} compact and exact element types differ");
        }
    }

    private static void CheckGlobalTypeCoherence(
        GlobalType global,
        string owner,
        List<WasmValidationDiagnostic> diagnostics)
    {
        if (global.ExactContentType is not null &&
            global.ExactContentType.ToStorageValType() != global.ContentType)
        {
            Add(
                diagnostics,
                WasmValidationCode.TypeMetadataMismatch,
                $"{owner} compact and exact content types differ");
        }
    }

    private static bool IsFunctionType(WasmModule module, int typeIndex)
        => typeIndex >= 0 &&
           typeIndex < module.Types.Count &&
           (module.TypeDefinitions.Count == 0 ||
            typeIndex < module.TypeDefinitions.Count &&
            module.TypeDefinitions[typeIndex].CompositeType is CompositeType.Function);

    private static void CheckTagType(
        WasmModule module,
        int typeIndex,
        string owner,
        bool allowResults,
        List<WasmValidationDiagnostic> diagnostics)
    {
        if (!IsFunctionType(module, typeIndex))
        {
            Add(
                diagnostics,
                WasmValidationCode.TypeIndexOutOfRange,
                $"{owner} type index {typeIndex} does not identify a function type");
            return;
        }

        int resultCount = module.Types[typeIndex].Results.Length;
        if (!allowResults && resultCount != 0)
        {
            Add(
                diagnostics,
                WasmValidationCode.TagResultTypeNotEmpty,
                $"{owner} type must have an empty result type; found {resultCount} result(s)");
        }
    }

    private static void CheckLimits(Limits limits, List<WasmValidationDiagnostic> diagnostics)
    {
        if (limits.Max is { } max && max < limits.Min)
            Add(diagnostics, WasmValidationCode.LimitsMaxLessThanMin,
                $"Limits max {max} < min {limits.Min}");
    }

    private static IEnumerable<Export> ExportEntries(WasmModule module)
        => module.ExportEntries.Count == 0 ? module.Exports.Values : module.ExportEntries;

    private static void CheckMemoryAbsoluteLimits(
        Limits limits,
        List<WasmValidationDiagnostic> diagnostics,
        string context)
    {
        const ulong Memory32MaxPages = 65_536;
        const ulong Memory64MaxPages = 281_474_976_710_656;
        ulong absoluteMax = limits.Is64 ? Memory64MaxPages : Memory32MaxPages;
        string width = limits.Is64 ? "memory64" : "memory32";

        if (limits.Min > absoluteMax)
            Add(diagnostics, WasmValidationCode.MemoryMaxExceedsLimit,
                $"{context} min {limits.Min} exceeds {width} limit {absoluteMax} pages");
        if (limits.Max is { } max && max > absoluteMax)
            Add(diagnostics, WasmValidationCode.MemoryMaxExceedsLimit,
                $"{context} max {max} exceeds {width} limit {absoluteMax} pages");
    }

    // Table limits are decoded as u64 whatever the address type; a 32-bit table's must fit in 32 bits (§3.2.5).
    private static void CheckTableAbsoluteLimits(
        Limits limits,
        List<WasmValidationDiagnostic> diagnostics,
        string context)
    {
        if (limits.Is64)
            return;
        if (limits.Min > uint.MaxValue)
            Add(diagnostics, WasmValidationCode.TableSizeExceedsLimit,
                $"{context} min {limits.Min} exceeds the table32 limit {uint.MaxValue}");
        if (limits.Max is { } max && max > uint.MaxValue)
            Add(diagnostics, WasmValidationCode.TableSizeExceedsLimit,
                $"{context} max {max} exceeds the table32 limit {uint.MaxValue}");
    }

    private static void CheckDefinedHeapTypeReferences(
        WasmModule module,
        List<WasmValidationDiagnostic> diagnostics)
    {
        int typeCount = module.TypeDefinitions.Count;
        for (int i = 0; i < module.TypeDefinitions.Count; i++)
        {
            var definition = module.TypeDefinitions[i];
            int visibleExclusive = definition.RecGroup.StartTypeIndex + definition.RecGroup.Count;
            CheckCompositeTypeReferences(definition.CompositeType, diagnostics, typeCount, visibleExclusive, $"type {i}");
        }

        foreach (var table in module.Tables)
            CheckValueTypeReferences(table.ExactElementType, diagnostics, typeCount, typeCount, "table element type");
        foreach (var global in module.Globals)
            CheckValueTypeReferences(global.Type.ExactContentType, diagnostics, typeCount, typeCount, "global type");
        foreach (var function in module.Functions)
        {
            foreach (var local in function.ExactLocals)
                CheckValueTypeReferences(local, diagnostics, typeCount, typeCount, "local type");
        }
        foreach (var element in module.Elements)
            CheckValueTypeReferences(element.ElementType, diagnostics, typeCount, typeCount, "element segment type");
        foreach (var import in module.Imports)
        {
            switch (import.Descriptor)
            {
                case ImportDescriptor.Table table:
                    CheckValueTypeReferences(table.Type.ExactElementType, diagnostics, typeCount, typeCount, $"import '{import.ModuleName}.{import.Name}' table type");
                    break;
                case ImportDescriptor.Global global:
                    CheckValueTypeReferences(global.Type.ExactContentType, diagnostics, typeCount, typeCount, $"import '{import.ModuleName}.{import.Name}' global type");
                    break;
            }
        }
    }

    private static void CheckCompositeTypeReferences(
        CompositeType type,
        List<WasmValidationDiagnostic> diagnostics,
        int typeCount,
        int visibleExclusive,
        string context)
    {
        switch (type)
        {
            case CompositeType.Function function:
                foreach (var parameter in function.Parameters)
                    CheckValueTypeReferences(parameter, diagnostics, typeCount, visibleExclusive, $"{context} parameter");
                foreach (var result in function.Results)
                    CheckValueTypeReferences(result, diagnostics, typeCount, visibleExclusive, $"{context} result");
                break;
            case CompositeType.Struct structure:
                foreach (var field in structure.Fields)
                    CheckStorageTypeReferences(field.StorageType, diagnostics, typeCount, visibleExclusive, $"{context} field");
                break;
            case CompositeType.Array array:
                CheckStorageTypeReferences(array.Field.StorageType, diagnostics, typeCount, visibleExclusive, $"{context} array field");
                break;
            case CompositeType.Continuation continuation:
                if (continuation.FunctionTypeIndex < 0 || continuation.FunctionTypeIndex >= visibleExclusive)
                    Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                        $"{context} continuation function type index {continuation.FunctionTypeIndex} out of range [0, {visibleExclusive})");
                else if (continuation.FunctionTypeIndex >= typeCount)
                    Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                        $"{context} continuation function type index {continuation.FunctionTypeIndex} out of range [0, {typeCount})");
                break;
        }
    }

    private static void CheckStorageTypeReferences(
        StorageType storageType,
        List<WasmValidationDiagnostic> diagnostics,
        int typeCount,
        int visibleExclusive,
        string context)
    {
        if (storageType is StorageType.Value value)
            CheckValueTypeReferences(value.Type, diagnostics, typeCount, visibleExclusive, context);
    }

    private static void CheckValueTypeReferences(
        Microsoft.NET.WebAssembly.Types.ValueType? valueType,
        List<WasmValidationDiagnostic> diagnostics,
        int typeCount,
        int visibleExclusive,
        string context)
    {
        if (valueType is Microsoft.NET.WebAssembly.Types.ValueType.Reference { HeapType: DefinedHeapType defined })
            CheckDefinedHeapTypeIndex(defined.TypeIndex, diagnostics, typeCount, visibleExclusive, context);
    }

    private static void CheckDefinedHeapTypeIndex(
        int typeIndex,
        List<WasmValidationDiagnostic> diagnostics,
        int typeCount,
        int visibleExclusive,
        string context)
    {
        if (typeIndex < 0 || typeIndex >= visibleExclusive)
            Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                $"{context} references type index {typeIndex} out of range [0, {visibleExclusive})");
        else if (typeIndex >= typeCount)
            Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                $"{context} references type index {typeIndex} out of range [0, {typeCount})");
    }

    private static int GetFunctionTypeIndex(WasmModule module, int funcIndex, int importedFunctions)
    {
        if (funcIndex < importedFunctions)
        {
            var importedFuncs = module.Imports
                .Where(i => i.Descriptor is ImportDescriptor.Func)
                .ToList();
            if (funcIndex < importedFuncs.Count)
                return ((ImportDescriptor.Func)importedFuncs[funcIndex].Descriptor).TypeIndex;
            return -1;
        }

        int localIdx = funcIndex - importedFunctions;
        return localIdx < module.Functions.Count ? module.Functions[localIdx].TypeIndex : -1;
    }

    private static Microsoft.NET.WebAssembly.Types.ValueType TableElementType(TableType table)
        => table.ExactElementType ?? table.ElementType switch
        {
            RefType.ExternRef => new Microsoft.NET.WebAssembly.Types.ValueType.Reference(
                Nullability.Nullable,
                new AbstractHeapType(HeapTypeKind.Extern)),
            _ => new Microsoft.NET.WebAssembly.Types.ValueType.Reference(
                Nullability.Nullable,
                new AbstractHeapType(HeapTypeKind.Func)),
        };

    private static void Add(
        List<WasmValidationDiagnostic> diagnostics,
        WasmValidationCode code,
        string message)
        => diagnostics.Add(new WasmValidationDiagnostic(code, message));
}
