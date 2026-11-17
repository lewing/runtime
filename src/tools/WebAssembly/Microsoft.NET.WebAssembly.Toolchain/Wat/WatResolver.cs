// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Resolves symbolic identifiers ($name) to numeric indices and builds
/// the type section from inline function signatures.
/// </summary>
public sealed class WatResolver
{
    private readonly WatModule _module;
    private readonly List<FuncType> _types = [];
    private readonly Dictionary<string, int> _typeNames = [];
    private readonly Dictionary<string, int> _funcNames = [];
    private readonly Dictionary<string, int> _memNames = [];
    private readonly Dictionary<string, int> _globalNames = [];
    private readonly Dictionary<string, int> _tagNames = [];
    private int _importFuncCount;
    private int _importTagCount;

    // Definition-site tracking for LSP
    private readonly Dictionary<string, SymbolInfo> _funcDefs = [];
    private readonly Dictionary<string, SymbolInfo> _globalDefs = [];
    private readonly Dictionary<string, SymbolInfo> _memDefs = [];
    private readonly Dictionary<string, SymbolInfo> _tagDefs = [];
    private readonly Dictionary<string, SymbolInfo> _typeDefs = [];
    private readonly Dictionary<int, Dictionary<string, SymbolInfo>> _localDefs = [];
    private readonly List<SymbolReference> _references = [];

    public WatSymbolTable? SymbolTable { get; private set; }

    private readonly List<WatDiagnostic>? _diagnostics;

    public WatResolver(WatModule module, List<WatDiagnostic>? diagnostics = null)
    {
        _module = module;
        _diagnostics = diagnostics;
    }

    public ResolvedModule Resolve()
    {
        // 1. Register explicit type definitions
        for (int i = 0; i < _module.Types.Count; i++)
        {
            var t = _module.Types[i];
            var ft = new FuncType([.. t.Params], [.. t.Results]);
            _types.Add(ft);
            if (t.Name != null)
            {
                _typeNames[t.Name] = i;
                _typeDefs[t.Name] = new SymbolInfo(i, t.Span);
            }
        }

        // 2. Count imported functions/tags and build name maps
        _importFuncCount = 0;
        _importTagCount = 0;
        foreach (var imp in _module.Imports)
        {
            if (imp.Kind == ImportKind.Func)
            {
                if (imp.FuncName != null)
                {
                    _funcNames[imp.FuncName] = _importFuncCount;
                    _funcDefs[imp.FuncName] = new SymbolInfo(_importFuncCount, imp.Span);
                }
                _importFuncCount++;
            }
            else if (imp.Kind == ImportKind.Tag)
            {
                if (imp.FuncName != null)
                {
                    _tagNames[imp.FuncName] = _importTagCount;
                    _tagDefs[imp.FuncName] = new SymbolInfo(_importTagCount, imp.Span);
                }
                _importTagCount++;
            }
        }

        // 3. Build function name map (module-defined funcs get indices N..M)
        for (int i = 0; i < _module.Funcs.Count; i++)
        {
            var f = _module.Funcs[i];
            if (f.Name != null)
            {
                _funcNames[f.Name] = i + _importFuncCount;
                _funcDefs[f.Name] = new SymbolInfo(i + _importFuncCount, f.Span);
            }
        }

        // 3b. Build tag name map (module-defined tags get indices after imported tags)
        for (int i = 0; i < _module.Tags.Count; i++)
        {
            var t = _module.Tags[i];
            if (t.Name != null)
            {
                _tagNames[t.Name] = i + _importTagCount;
                _tagDefs[t.Name] = new SymbolInfo(i + _importTagCount, t.Span);
            }
        }

        // 4. Build memory name map
        for (int i = 0; i < _module.Memories.Count; i++)
        {
            var m = _module.Memories[i];
            if (m.Name != null)
            {
                _memNames[m.Name] = i;
                _memDefs[m.Name] = new SymbolInfo(i, m.Span);
            }
        }

        // 5. Build global name map
        for (int i = 0; i < _module.Globals.Count; i++)
        {
            var g = _module.Globals[i];
            if (g.Name != null)
            {
                _globalNames[g.Name] = i;
                _globalDefs[g.Name] = new SymbolInfo(i, g.Span);
            }
        }

        // 6. Assign type indices to imported functions and tags
        var importTypeIndices = new List<int>();
        var importTagTypeIndices = new List<int>();
        foreach (var imp in _module.Imports)
        {
            if (imp.Kind == ImportKind.Func)
            {
                importTypeIndices.Add(ResolveImportTypeUse(imp, includeResults: true));
            }
            else if (imp.Kind == ImportKind.Tag)
            {
                importTagTypeIndices.Add(ResolveImportTypeUse(imp, includeResults: true));
            }
        }

        // 7. Assign type indices to module-defined functions
        var funcTypeIndices = new List<int>();
        foreach (var f in _module.Funcs)
        {
            if (f.TypeIndex.HasValue)
            {
                funcTypeIndices.Add(f.TypeIndex.Value);
            }
            else
            {
                var paramTypes = f.Params.Select(p => p.Type).ToArray();
                var resultTypes = f.Results.ToArray();
                int typeIdx = FindOrAddType(new FuncType(paramTypes, resultTypes));
                funcTypeIndices.Add(typeIdx);
            }
        }

        // 7b. Assign type indices to module-defined tags
        var tagTypeIndices = new List<int>();
        foreach (var t in _module.Tags)
        {
            if (t.TypeName is { } typeName)
            {
                tagTypeIndices.Add(ResolveTypeName(typeName));
            }
            else if (t.TypeIndex.HasValue)
            {
                tagTypeIndices.Add(t.TypeIndex.Value);
            }
            else
            {
                var paramTypes = t.Params.ToArray();
                var resultTypes = t.Results.ToArray();
                int typeIdx = FindOrAddType(new FuncType(paramTypes, resultTypes));
                tagTypeIndices.Add(typeIdx);
            }
        }

        // 8. Collect and resolve exports in source order
        var exports = CollectExportsInOrder();

        // 9. Resolve function bodies
        var resolvedFuncs = new List<ResolvedFunc>();
        for (int i = 0; i < _module.Funcs.Count; i++)
        {
            var f = _module.Funcs[i];
            var localMap = BuildLocalMap(f, i + _importFuncCount);
            var labelStack = new List<string?>();
            var body = new List<WatInstruction>();
            ResolveInstructions(f.Body, body, localMap, labelStack);
            resolvedFuncs.Add(new ResolvedFunc
            {
                TypeIndex = funcTypeIndices[i],
                Locals = f.Locals.Select(l => l.Type).ToList(),
                Body = body,
                Span = f.Span,
            });
        }

        // 10. Resolve memories
        var memories = _module.Memories.Select(m =>
            new ResolvedMemory { Min = m.Min, Max = m.Max }).ToList();

        // 11. Resolve imports
        var resolvedImports = new List<ResolvedImport>();
        int importFuncIdx = 0;
        int importTagIdx = 0;
        foreach (var imp in _module.Imports)
        {
            switch (imp.Kind)
            {
                case ImportKind.Func:
                    resolvedImports.Add(new ResolvedImport
                    {
                        ModuleName = imp.ModuleName,
                        FieldName = imp.FieldName,
                        Kind = ImportKind.Func,
                        TypeIndex = importTypeIndices[importFuncIdx++],
                    });
                    break;
                case ImportKind.Memory:
                    resolvedImports.Add(new ResolvedImport
                    {
                        ModuleName = imp.ModuleName,
                        FieldName = imp.FieldName,
                        Kind = ImportKind.Memory,
                        MemMin = imp.MemMin,
                        MemMax = imp.MemMax,
                    });
                    break;
                case ImportKind.Table:
                    resolvedImports.Add(new ResolvedImport
                    {
                        ModuleName = imp.ModuleName,
                        FieldName = imp.FieldName,
                        Kind = ImportKind.Table,
                        TableMin = imp.TableMin,
                        TableMax = imp.TableMax,
                        TableElementType = ResolveReferenceType(imp.TableElementType),
                    });
                    break;
                case ImportKind.Global:
                    resolvedImports.Add(new ResolvedImport
                    {
                        ModuleName = imp.ModuleName,
                        FieldName = imp.FieldName,
                        Kind = ImportKind.Global,
                        GlobalType = imp.GlobalType,
                        GlobalMutable = imp.GlobalMutable,
                    });
                    break;
                case ImportKind.Tag:
                    resolvedImports.Add(new ResolvedImport
                    {
                        ModuleName = imp.ModuleName,
                        FieldName = imp.FieldName,
                        Kind = ImportKind.Tag,
                        TypeIndex = importTagTypeIndices[importTagIdx++],
                    });
                    break;
            }
        }

        // 12. Resolve globals
        var resolvedGlobals = new List<ResolvedGlobal>();
        foreach (var g in _module.Globals)
        {
            var init = g.Init != null ? ResolveInstruction(g.Init, [], []) : null;
            resolvedGlobals.Add(new ResolvedGlobal
            {
                Type = g.Type,
                Mutable = g.Mutable,
                Init = init,
            });
        }

        // 13. Resolve data segments
        var resolvedData = new List<ResolvedData>();
        foreach (var d in _module.DataSegments)
        {
            var offset = d.Offset != null ? ResolveInstruction(d.Offset, [], []) : null;
            resolvedData.Add(new ResolvedData
            {
                Offset = offset,
                Bytes = d.Bytes,
            });
        }

        // 14. Resolve tables. The initializer is a constant expression like a global's, so it goes
        // through the same ResolveInstruction path — that is what turns `(ref.func $f)` into a
        // function index and `(global.get $g)` into a global index.
        var resolvedTables = _module.Tables.Select(t => new ResolvedTable
        {
            Min = t.Min,
            Max = t.Max,
            ElementType = ResolveReferenceType(t.ElementType),
            Init = t.Init != null ? ResolveInstruction(t.Init, [], []) : null,
        }).ToList();

        // 15. Resolve element segments
        var resolvedElems = new List<ResolvedElem>();
        foreach (var e in _module.Elements)
        {
            var offset = e.Offset != null ? ResolveInstruction(e.Offset, [], []) : null;
            var functionIndices = new List<int>(e.FuncIndices.Count);
            for (int i = 0; i < e.FuncIndices.Count; i++)
            {
                string? name = i < e.FuncNames.Count ? e.FuncNames[i] : null;
                functionIndices.Add(name is null ? e.FuncIndices[i] : ResolveFuncName(name));
            }

            resolvedElems.Add(new ResolvedElem
            {
                Mode = e.Mode,
                TableIndex = e.TableIndex,
                HasExplicitTable = e.HasExplicitTable,
                Offset = offset,
                FuncIndices = functionIndices,
                UsesExpressions = e.UsesExpressions,
                ElementType = ResolveReferenceType(e.ElementType),
                ElementExpressions =
                [
                    .. e.ElementExpressions.Select(expression =>
                    {
                        var resolved = new List<WatInstruction>(expression.Count);
                        ResolveInstructions(expression, resolved, [], []);
                        return resolved;
                    }),
                ],
            });
        }

        // 16. Resolve tags
        var resolvedTags = new List<ResolvedTag>();
        for (int i = 0; i < _module.Tags.Count; i++)
        {
            resolvedTags.Add(new ResolvedTag { TypeIndex = tagTypeIndices[i] });
        }

        SymbolTable = new WatSymbolTable
        {
            Functions = _funcDefs,
            Globals = _globalDefs,
            Memories = _memDefs,
            Tags = _tagDefs,
            Types = _typeDefs,
            Locals = _localDefs.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyDictionary<string, SymbolInfo>)kvp.Value),
            References = _references,
        };

        // Compute name section data: @name overrides $id; $id without @name emits by itself.
        string? moduleName = _module.AnnotationName
            ?? (_module.ModuleId is { } mid ? StripDollar(mid) : null);

        var funcNameMap = new Dictionary<int, string>();
        // Import functions occupy the low indices (0.._importFuncCount-1) and must appear first.
        int nameImportIdx = 0;
        foreach (var imp in _module.Imports)
        {
            if (imp.Kind == ImportKind.Func)
            {
                var name = imp.AnnotationName
                    ?? (imp.FuncName is { } fn ? StripDollar(fn) : null);
                if (name != null)
                    funcNameMap[nameImportIdx] = name;
                nameImportIdx++;
            }
        }
        for (int i = 0; i < _module.Funcs.Count; i++)
        {
            var f = _module.Funcs[i];
            var name = f.AnnotationName ?? (f.Name is { } fn ? StripDollar(fn) : null);
            if (name != null)
                funcNameMap[i + _importFuncCount] = name;
        }

        var typeNameMap = new Dictionary<int, string>();
        for (int i = 0; i < _module.Types.Count; i++)
        {
            if (_module.Types[i].Name is { } name)
                typeNameMap[i] = StripDollar(name);
        }

        var localNameMap = new Dictionary<int, Dictionary<int, string>>();
        for (int i = 0; i < _module.Funcs.Count; i++)
        {
            var f = _module.Funcs[i];
            int absIdx = i + _importFuncCount;
            Dictionary<int, string>? locals = null;
            int localIdx = 0;
            foreach (var p in f.Params)
            {
                var name = p.AnnotationName ?? (p.Name is { } pn ? StripDollar(pn) : null);
                if (name != null)
                {
                    locals ??= [];
                    locals[localIdx] = name;
                }
                localIdx++;
            }
            foreach (var l in f.Locals)
            {
                var name = l.AnnotationName ?? (l.Name is { } ln ? StripDollar(ln) : null);
                if (name != null)
                {
                    locals ??= [];
                    locals[localIdx] = name;
                }
                localIdx++;
            }
            if (locals != null)
                localNameMap[absIdx] = locals;
        }

        int? startFunction = null;
        if (_module.Start is { } start)
        {
            startFunction = start.Ref is { } name ? ResolveFuncName(name) : start.Index;
            if (start.Ref is { } referencedName)
            {
                _references.Add(new SymbolReference(
                    referencedName,
                    start.Span,
                    startFunction.Value,
                    SymbolKind.Function));
            }
        }

        return new ResolvedModule
        {
            Types = _types,
            Funcs = resolvedFuncs,
            Memories = memories,
            Exports = exports,
            Imports = resolvedImports,
            Globals = resolvedGlobals,
            DataSegments = resolvedData,
            Tables = resolvedTables,
            Elements = resolvedElems,
            Tags = resolvedTags,
            ImportFuncCount = _importFuncCount,
            ModuleName = moduleName,
            FuncNameMap = funcNameMap,
            TypeNameMap = typeNameMap,
            LocalNameMap = localNameMap,
            StartFunction = startFunction,
        };
    }

    private static string StripDollar(string id) =>
        id.Length > 0 && id[0] == '$' ? id[1..] : id;

    /// <summary>
    /// Collects exports in the order they appear in source,
    /// interleaving module-level exports and inline exports.
    /// </summary>
    private List<ResolvedExport> CollectExportsInOrder()
    {
        var exports = new List<ResolvedExport>();

        foreach (var (kind, index) in _module.DeclOrder)
        {
            switch (kind)
            {
                case DeclKind.Export:
                    exports.Add(ResolveExport(_module.Exports[index]));
                    break;

                case DeclKind.Memory:
                    var mem = _module.Memories[index];
                    foreach (var e in mem.InlineExports)
                    {
                        exports.Add(new ResolvedExport
                        {
                            Name = e.Name,
                            Kind = ExportKind.Memory,
                            Index = index,
                        });
                    }
                    break;

                case DeclKind.Func:
                    var func = _module.Funcs[index];
                    foreach (var e in func.InlineExports)
                    {
                        exports.Add(new ResolvedExport
                        {
                            Name = e.Name,
                            Kind = ExportKind.Func,
                            Index = index + _importFuncCount,
                        });
                    }
                    break;

                case DeclKind.Global:
                    var global = _module.Globals[index];
                    foreach (var e in global.InlineExports)
                    {
                        exports.Add(new ResolvedExport
                        {
                            Name = e.Name,
                            Kind = ExportKind.Global,
                            Index = index,
                        });
                    }
                    break;
            }
        }

        return exports;
    }

    private int ResolveImportTypeUse(WatImport import, bool includeResults)
    {
        var inlineType = new FuncType(
            import.Params.Select(parameter => parameter.Type).ToArray(),
            includeResults ? import.Results.ToArray() : []);
        if (!import.TypeIndex.HasValue && import.TypeName is null)
            return FindOrAddType(inlineType);

        int typeIndex = import.TypeName is { } typeName
            ? ResolveTypeName(typeName)
            : import.TypeIndex!.Value;
        if ((uint)typeIndex >= (uint)_types.Count)
            throw new WatException($"Imported function or tag refers to unknown type {typeIndex}.");

        if ((inlineType.Parameters.Length > 0 || inlineType.Results.Length > 0) &&
            (!_types[typeIndex].Parameters.AsSpan().SequenceEqual(inlineType.Parameters) ||
             !_types[typeIndex].Results.AsSpan().SequenceEqual(inlineType.Results)))
        {
            throw new WatException(
                $"Imported function or tag inline signature does not match type {typeIndex}.");
        }

        return typeIndex;
    }

    private int FindOrAddType(FuncType ft)
    {
        for (int i = 0; i < _types.Count; i++)
        {
            if (_types[i].Parameters.AsSpan().SequenceEqual(ft.Parameters) &&
                _types[i].Results.AsSpan().SequenceEqual(ft.Results))
                return i;
        }
        _types.Add(ft);
        return _types.Count - 1;
    }

    private Dictionary<string, int> BuildLocalMap(WatFunc f, int funcIndex)
    {
        var map = new Dictionary<string, int>();
        var defs = new Dictionary<string, SymbolInfo>();
        int idx = 0;
        foreach (var p in f.Params)
        {
            if (p.Name != null)
            {
                map[p.Name] = idx;
                defs[p.Name] = new SymbolInfo(idx, p.Span);
            }
            idx++;
        }
        foreach (var l in f.Locals)
        {
            if (l.Name != null)
            {
                map[l.Name] = idx;
                defs[l.Name] = new SymbolInfo(idx, l.Span);
            }
            idx++;
        }
        if (defs.Count > 0)
            _localDefs[funcIndex] = defs;
        return map;
    }

    private void ResolveInstructions(List<WatInstruction> src, List<WatInstruction> dst,
        Dictionary<string, int> localMap, List<string?> labelStack)
    {
        foreach (var instr in src)
        {
            var resolved = ResolveInstruction(instr, localMap, labelStack);
            dst.Add(resolved);
        }
    }

    private WatInstruction ResolveInstruction(WatInstruction instr, Dictionary<string, int> localMap,
        List<string?> labelStack)
    {
        // Carry every immediate the resolve pass does not rewrite. The copy lives on
        // WatInstruction, beside the fields it mirrors — see issue #419, where a hand-written
        // copy here silently dropped V128Bytes and LaneIndex.
        var r = instr.CreateResolvedShell();

        // Multi-value block results need a type index
        if (instr.BlockResultTypes is { Count: > 0 })
        {
            var ft = new FuncType([], instr.BlockResultTypes.ToArray());
            r.BlockTypeIndex = FindOrAddType(ft);
            r.BlockResultType = null;
        }

        // Resolve identifier references
        if (instr.IdValue != null && OpcodeTable.TryGet(instr.Mnemonic, out var entry))
        {
            int resolvedIdx;
            SymbolKind refKind;
            bool resolvesTypeTagPair = false;
            bool resolvesHeapType = false;
            switch (entry.Immediate)
            {
                case ImmediateKind.LocalIdx when instr.Mnemonic is "br" or "br_if":
                    resolvedIdx = ResolveLabelName(instr.IdValue, labelStack);
                    refKind = SymbolKind.Label;
                    break;
                case ImmediateKind.LocalIdx:
                    resolvedIdx = ResolveLocal(instr.IdValue, localMap);
                    refKind = SymbolKind.Local;
                    break;
                case ImmediateKind.FuncIdx:
                    resolvedIdx = ResolveFuncName(instr.IdValue);
                    refKind = SymbolKind.Function;
                    break;
                case ImmediateKind.GlobalIdx:
                    resolvedIdx = ResolveGlobalName(instr.IdValue);
                    refKind = SymbolKind.Global;
                    break;
                case ImmediateKind.TagIdx:
                    resolvedIdx = ResolveTagName(instr.IdValue);
                    refKind = SymbolKind.Tag;
                    break;
                case ImmediateKind.LabelIdx:
                    resolvedIdx = ResolveLabelName(instr.IdValue, labelStack);
                    refKind = SymbolKind.Label;
                    break;
                case ImmediateKind.TypeTagPair:
                    resolvedIdx = ResolveTagName(instr.IdValue);
                    refKind = SymbolKind.Tag;
                    resolvesTypeTagPair = true;
                    break;
                case ImmediateKind.RefType:
                    resolvedIdx = ResolveTypeName(instr.IdValue);
                    refKind = SymbolKind.Type;
                    resolvesHeapType = true;
                    break;
                default:
                    resolvedIdx = instr.Index ?? 0;
                    refKind = SymbolKind.Function;
                    break;
            }
            if (resolvesTypeTagPair || resolvesHeapType)
                r.IntValue = resolvedIdx;
            else
                r.Index = resolvedIdx;
            r.IdValue = null;
            if (entry.Immediate is not ImmediateKind.None)
                _references.Add(new SymbolReference(instr.IdValue, instr.Span, resolvedIdx, refKind));
        }

        // Resolve br_table labels
        if (instr.BrTableLabels != null && instr.BrTableIndices != null)
        {
            r.BrTableIndices = [];
            for (int i = 0; i < instr.BrTableLabels.Count; i++)
            {
                var label = instr.BrTableLabels[i];
                if (!string.IsNullOrEmpty(label))
                    r.BrTableIndices.Add(ResolveLabelName(label, labelStack));
                else
                    r.BrTableIndices.Add(instr.BrTableIndices[i]);
            }
        }

        // Resolve operands
        foreach (var op in instr.Operands)
            r.Operands.Add(ResolveInstruction(op, localMap, labelStack));

        if (instr.Mnemonic == "resume" && instr.CatchHandlers != null)
        {
            r.CatchHandlers = [];
            foreach (var handler in instr.CatchHandlers)
            {
                var rh = new WatCatchHandler { CatchKind = handler.CatchKind };
                if (handler.TagName != null)
                    rh.TagIndex = ResolveTagName(handler.TagName);
                else
                    rh.TagIndex = handler.TagIndex;

                if (handler.LabelName != null)
                    rh.LabelIndex = ResolveLabelName(handler.LabelName, labelStack);
                else
                    rh.LabelIndex = handler.LabelIndex;

                r.CatchHandlers.Add(rh);
            }
        }

        // Resolve then/else with label stack for block/loop/try_table
        if (instr.Mnemonic is "block" or "loop" or "try_table")
        {
            // Per the wasm spec, catch clause label indices in a try_table are relative to the
            // OUTER context (outside the try_table body). Resolve them BEFORE pushing the
            // try_table's own label so that label 0 = the first enclosing block, not the
            // try_table itself.
            if (instr.Mnemonic == "try_table" && instr.CatchHandlers != null)
            {
                r.CatchHandlers = [];
                foreach (var handler in instr.CatchHandlers)
                {
                    var rh = new WatCatchHandler { CatchKind = handler.CatchKind };

                    if (handler.TagName != null)
                        rh.TagIndex = ResolveTagName(handler.TagName);
                    else
                        rh.TagIndex = handler.TagIndex;

                    if (handler.LabelName != null)
                        rh.LabelIndex = ResolveLabelName(handler.LabelName, labelStack);
                    else
                        rh.LabelIndex = handler.LabelIndex;

                    r.CatchHandlers.Add(rh);
                }
            }

            // Push label onto stack (IdValue is the label name, or null for anonymous)
            labelStack.Add(instr.IdValue);

            if (instr.ThenBody != null)
            {
                r.ThenBody = [];
                ResolveInstructions(instr.ThenBody, r.ThenBody, localMap, labelStack);
            }

            labelStack.RemoveAt(labelStack.Count - 1);
        }
        else if (instr.Mnemonic == "try")
        {
            // Legacy try: push label scope, resolve do body and catch clauses
            labelStack.Add(instr.IdValue);

            if (instr.ThenBody != null)
            {
                r.ThenBody = [];
                ResolveInstructions(instr.ThenBody, r.ThenBody, localMap, labelStack);
            }

            // Resolve legacy catch clauses
            if (instr.LegacyCatchClauses != null)
            {
                r.LegacyCatchClauses = [];
                foreach (var clause in instr.LegacyCatchClauses)
                {
                    var rc = new WatLegacyCatchClause { IsCatchAll = clause.IsCatchAll };
                    if (clause.TagName != null)
                        rc.TagIndex = ResolveTagName(clause.TagName);
                    else
                        rc.TagIndex = clause.TagIndex;
                    ResolveInstructions(clause.Body, rc.Body, localMap, labelStack);
                    r.LegacyCatchClauses.Add(rc);
                }
            }

            // Resolve delegate label
            if (instr.DelegateLabelName != null)
                r.DelegateLabelIndex = ResolveLabelName(instr.DelegateLabelName, labelStack);
            else
                r.DelegateLabelIndex = instr.DelegateLabelIndex;

            labelStack.RemoveAt(labelStack.Count - 1);
        }
        else if (instr.Mnemonic == "if")
        {
            labelStack.Add(instr.IdValue);

            if (instr.ThenBody != null)
            {
                r.ThenBody = [];
                ResolveInstructions(instr.ThenBody, r.ThenBody, localMap, labelStack);
            }
            if (instr.ElseBody != null)
            {
                r.ElseBody = [];
                ResolveInstructions(instr.ElseBody, r.ElseBody, localMap, labelStack);
            }

            labelStack.RemoveAt(labelStack.Count - 1);
        }
        else
        {
            if (instr.ThenBody != null)
            {
                r.ThenBody = [];
                ResolveInstructions(instr.ThenBody, r.ThenBody, localMap, labelStack);
            }
            if (instr.ElseBody != null)
            {
                r.ElseBody = [];
                ResolveInstructions(instr.ElseBody, r.ElseBody, localMap, labelStack);
            }
        }

        return r;
    }

    private int ResolveLabelName(string name, List<string?> labelStack)
    {
        // Search from innermost (end of stack) to outermost (start)
        for (int i = labelStack.Count - 1; i >= 0; i--)
        {
            if (labelStack[i] == name)
                return labelStack.Count - 1 - i;
        }
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown label: {name}")); return 0; }
        throw new WatException($"Unknown label: {name}");
    }

    private int ResolveLocal(string name, Dictionary<string, int> localMap)
    {
        if (localMap.TryGetValue(name, out int idx))
            return idx;
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown local: {name}")); return 0; }
        throw new WatException($"Unknown local: {name}");
    }

    private int ResolveFuncName(string name)
    {
        if (_funcNames.TryGetValue(name, out int idx))
            return idx;
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown function: {name}")); return 0; }
        throw new WatException($"Unknown function: {name}");
    }

    private int ResolveTypeName(string name)
    {
        if (_typeNames.TryGetValue(name, out int idx))
            return idx;
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown type: {name}")); return 0; }
        throw new WatException($"Unknown type: {name}");
    }

    private Microsoft.NET.WebAssembly.Types.ValueType.Reference ResolveReferenceType(WatReferenceType type)
    {
        HeapType heapType = type.TypeName is { } name
            ? new DefinedHeapType(ResolveTypeName(name))
            : type.HeapType ?? throw new WatException("Reference type is missing its heap type.");

        if (heapType is DefinedHeapType defined &&
            (defined.TypeIndex < 0 || defined.TypeIndex >= _types.Count))
        {
            throw new WatException(
                $"Type index {defined.TypeIndex} is out of range for {_types.Count} defined type(s).");
        }

        return new Microsoft.NET.WebAssembly.Types.ValueType.Reference(type.Nullability, heapType);
    }

    private int ResolveGlobalName(string name)
    {
        if (_globalNames.TryGetValue(name, out int idx))
            return idx;
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown global: {name}")); return 0; }
        throw new WatException($"Unknown global: {name}");
    }

    private int ResolveTagName(string name)
    {
        if (_tagNames.TryGetValue(name, out int idx))
            return idx;
        if (_diagnostics != null) { _diagnostics.Add(new WatDiagnostic(0, 0, $"Unknown tag: {name}")); return 0; }
        throw new WatException($"Unknown tag: {name}");
    }

    private ResolvedExport ResolveExport(WatExport e)
    {
        int index = e.Index;
        if (e.Ref != null)
        {
            index = e.Kind switch
            {
                ExportKind.Func => ResolveFuncName(e.Ref),
                ExportKind.Memory => _memNames.GetValueOrDefault(e.Ref, 0),
                ExportKind.Global => ResolveGlobalName(e.Ref),
                _ => index,
            };
        }
        return new ResolvedExport { Name = e.Name, Kind = e.Kind, Index = index };
    }
}

// ── Resolved AST (indices instead of names) ──

public sealed class ResolvedModule
{
    public required List<FuncType> Types { get; init; }
    public required List<ResolvedFunc> Funcs { get; init; }
    public required List<ResolvedMemory> Memories { get; init; }
    public required List<ResolvedExport> Exports { get; init; }
    public required List<ResolvedImport> Imports { get; init; }
    public required List<ResolvedGlobal> Globals { get; init; }
    public required List<ResolvedData> DataSegments { get; init; }
    public required List<ResolvedTable> Tables { get; init; }
    public required List<ResolvedElem> Elements { get; init; }
    public required List<ResolvedTag> Tags { get; init; }
    public int ImportFuncCount { get; init; }
    /// <summary>Module name for the name section (from @name or $id, @name wins).</summary>
    public string? ModuleName { get; init; }
    /// <summary>Function name map for the name section: absolute func index → name string.</summary>
    public Dictionary<int, string> FuncNameMap { get; init; } = [];
    /// <summary>Type name map for name-section subsection 4: type index → name string.</summary>
    public Dictionary<int, string> TypeNameMap { get; init; } = [];
    /// <summary>Local name map for the name section: absolute func index → (local index → name string).</summary>
    public Dictionary<int, Dictionary<int, string>> LocalNameMap { get; init; } = [];
    public int? StartFunction { get; init; }
}

public sealed class ResolvedFunc
{
    public required int TypeIndex { get; init; }
    public required List<ValType> Locals { get; init; }
    public required List<WatInstruction> Body { get; init; }
    public SourceSpan Span { get; init; }
}

public sealed class ResolvedMemory
{
    public required uint Min { get; init; }
    public uint? Max { get; init; }
}

public sealed class ResolvedExport
{
    public required string Name { get; init; }
    public required ExportKind Kind { get; init; }
    public required int Index { get; init; }
}

public sealed class ResolvedImport
{
    public required string ModuleName { get; init; }
    public required string FieldName { get; init; }
    public required ImportKind Kind { get; init; }
    public int TypeIndex { get; init; }
    // For memory imports
    public int MemMin { get; init; }
    public int? MemMax { get; init; }
    // For table imports
    public uint TableMin { get; init; }
    public uint? TableMax { get; init; }
    public Microsoft.NET.WebAssembly.Types.ValueType.Reference TableElementType { get; init; } =
        new(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));
    // For global imports
    public ValType GlobalType { get; init; }
    public bool GlobalMutable { get; init; }
}

public sealed class ResolvedGlobal
{
    public required ValType Type { get; init; }
    public required bool Mutable { get; init; }
    public WatInstruction? Init { get; init; }
}

public sealed class ResolvedData
{
    public WatInstruction? Offset { get; init; }
    public required byte[] Bytes { get; init; }
}

public sealed class ResolvedTable
{
    private Microsoft.NET.WebAssembly.Types.ValueType.Reference _elementType =
        new(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));

    public required uint Min { get; init; }
    public uint? Max { get; init; }
    public Microsoft.NET.WebAssembly.Types.ValueType.Reference ElementType
    {
        get => _elementType;
        init => _elementType = value;
    }
    public ValType ElemType
    {
        get => _elementType.ToStorageValType();
        init => _elementType = (Microsoft.NET.WebAssembly.Types.ValueType.Reference)
            Microsoft.NET.WebAssembly.Types.ValueType.FromStorageValType(value);
    }

    /// <summary>Resolved initializer constant expression, or null for a default-initialized table.</summary>
    public WatInstruction? Init { get; init; }
}

public sealed class ResolvedElem
{
    public required WatElemMode Mode { get; init; }
    public required int TableIndex { get; init; }
    public required bool HasExplicitTable { get; init; }
    public WatInstruction? Offset { get; init; }
    public required List<int> FuncIndices { get; init; }
    public bool UsesExpressions { get; init; }
    public Microsoft.NET.WebAssembly.Types.ValueType.Reference ElementType { get; init; } =
        new(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));
    public List<List<WatInstruction>> ElementExpressions { get; init; } = [];
}

public sealed class ResolvedTag
{
    public required int TypeIndex { get; init; }
}
