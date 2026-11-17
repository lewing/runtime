// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Post-parse semantic validator.  Checks a parsed <see cref="WasmModule"/> against
/// the Wasm spec §3 validity rules.  Never throws — callers inspect the returned
/// <see cref="WasmValidationResult"/> or call <see cref="ValidateAndThrow"/>.
/// </summary>
public static class WasmModuleValidator
{
    /// <summary>
    /// Validates a parsed module.  Returns a result containing all diagnostics.
    /// </summary>
    public static WasmValidationResult Validate(WasmModule module)
    {
        var diagnostics = new List<WasmValidationDiagnostic>();

        // ── Build combined index-space counts ──────────────────────────────
        int importedFunctions = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        int importedTables    = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Table);
        int importedMemories  = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Memory);
        int importedGlobals   = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Global);

        int totalFunctions = importedFunctions + module.Functions.Count;
        int totalTables    = importedTables    + module.Tables.Count;
        int totalMemories  = importedMemories  + module.Memories.Count;
        int totalGlobals   = importedGlobals   + module.Globals.Count;

        // ── Limits (defined memories + tables) ────────────────────────────
        foreach (var mem in module.Memories)
        {
            CheckLimits(mem.Limits, diagnostics);
            if (!mem.Limits.Is64 && mem.Limits.Max is { } max && max > 65536)
                Add(diagnostics, WasmValidationCode.MemoryMaxExceedsLimit,
                    $"Memory max {max} exceeds 65536 pages");
        }
        foreach (var tbl in module.Tables)
            CheckLimits(tbl.Limits, diagnostics);

        // ── Limits (imported memories + tables) ───────────────────────────
        foreach (var imp in module.Imports)
        {
            if (imp.Descriptor is ImportDescriptor.Memory mImp)
            {
                CheckLimits(mImp.Type.Limits, diagnostics);
                if (!mImp.Type.Limits.Is64 && mImp.Type.Limits.Max is { } max && max > 65536)
                    Add(diagnostics, WasmValidationCode.MemoryMaxExceedsLimit,
                        $"Imported memory max {max} exceeds 65536 pages");
            }
            else if (imp.Descriptor is ImportDescriptor.Table tImp)
                CheckLimits(tImp.Type.Limits, diagnostics);
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
        }
        foreach (var imp in module.Imports)
        {
            if (imp.Descriptor is ImportDescriptor.Func fImp &&
                (fImp.TypeIndex < 0 || fImp.TypeIndex >= module.Types.Count))
                Add(diagnostics, WasmValidationCode.TypeIndexOutOfRange,
                    $"Imported function type index {fImp.TypeIndex} out of range [0, {module.Types.Count})");
        }

        // ── Export index bounds ───────────────────────────────────────────
        var exportNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var exp in module.Exports.Values)
        {
            if (!exportNames.Add(exp.Name))
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
        foreach (var elem in module.Elements)
        {
            if (elem.TableIndex < 0) continue; // passive/declarative segments have no table target

            if (elem.TableIndex >= totalTables)
                Add(diagnostics, WasmValidationCode.ElementTableIndexInvalid,
                    $"Element segment table index {elem.TableIndex} out of range [0, {totalTables})");

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

        // ── Global init expressions ───────────────────────────────────────
        for (int gi = 0; gi < module.Globals.Count; gi++)
            CheckGlobalInitExpr(module.Globals[gi], importedGlobals, gi, diagnostics);

        return diagnostics.Count == 0
            ? WasmValidationResult.Valid
            : new WasmValidationResult(diagnostics);
    }

    /// <summary>
    /// Validates and throws <see cref="WasmValidationException"/> if any errors exist.
    /// </summary>
    public static void ValidateAndThrow(WasmModule module)
    {
        var result = Validate(module);
        if (!result.IsValid)
            throw new WasmValidationException(
                result.Diagnostics.Where(d => d.Severity == WasmValidationSeverity.Error).ToList());
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static void CheckLimits(Limits limits, List<WasmValidationDiagnostic> diagnostics)
    {
        if (limits.Max is { } max && max < limits.Min)
            Add(diagnostics, WasmValidationCode.LimitsMaxLessThanMin,
                $"Limits max {max} < min {limits.Min}");
    }

    private static void CheckGlobalInitExpr(
        WasmGlobal global,
        int importedGlobals,
        int globalSectionIndex,
        List<WasmValidationDiagnostic> diagnostics)
    {
        var expr = global.InitExpr;
        if (expr.Length == 0)
        {
            Add(diagnostics, WasmValidationCode.InvalidGlobalInitExpr,
                "Global init expression is empty");
            return;
        }

        byte opcode = expr[0];
        bool valid = opcode switch
        {
            0x41 => expr.Length >= 2,   // i32.const
            0x42 => expr.Length >= 2,   // i64.const
            0x43 => expr.Length == 6,   // f32.const (4-byte literal + end)
            0x44 => expr.Length == 10,  // f64.const (8-byte literal + end)
            0x23 => expr.Length >= 2,   // global.get
            0xD0 => expr.Length >= 3,   // ref.null reftype
            0xD2 => expr.Length >= 2,   // ref.func funcidx
            _ => false,
        };

        if (!valid)
        {
            Add(diagnostics, WasmValidationCode.InvalidGlobalInitExpr,
                $"Global init expression uses invalid or unsupported opcode 0x{opcode:X2}");
            return;
        }

        // global.get in init expr: extended-const allows referencing any preceding immutable global.
        // A global at section index i may reference indices 0..importedGlobals+i-1.
        if (opcode == 0x23 && expr.Length >= 2)
        {
            int refIdx = expr[1];
            int availableGlobals = importedGlobals + globalSectionIndex;
            if (refIdx >= availableGlobals)
                Add(diagnostics, WasmValidationCode.InvalidGlobalInitExpr,
                    $"Global init expression global.get references global index {refIdx} which is not yet defined (available: {availableGlobals})");
        }
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

    private static void Add(
        List<WasmValidationDiagnostic> diagnostics,
        WasmValidationCode code,
        string message)
        => diagnostics.Add(new WasmValidationDiagnostic(code, message));
}
