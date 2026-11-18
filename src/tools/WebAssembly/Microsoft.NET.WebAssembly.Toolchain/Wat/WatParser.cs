// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using Microsoft.NET.WebAssembly.Module;
using System.Buffers.Binary;
using Microsoft.NET.WebAssembly.Types;

/// <summary>AST nodes produced by the WAT parser.</summary>
public sealed class WatModule
{
    public SourceSpan Span { get; set; }
    public List<WatFunc> Funcs { get; } = [];
    public List<WatMemory> Memories { get; } = [];
    public List<WatExport> Exports { get; } = [];
    public List<WatFuncType> Types { get; } = [];
    public List<WatImport> Imports { get; } = [];
    public List<WatGlobal> Globals { get; } = [];
    public List<WatData> DataSegments { get; } = [];
    public List<WatTable> Tables { get; } = [];
    public List<WatElem> Elements { get; } = [];
    public WatStart? Start { get; set; }
    /// <summary>Tracks the order of top-level declarations that may produce exports.</summary>
    public List<(DeclKind Kind, int Index)> DeclOrder { get; } = [];
    public List<WatTag> Tags { get; } = [];
    /// <summary>Module $identifier (includes leading $, e.g. "$foo"). Used for WAT resolution and as fallback name-section source.</summary>
    public string? ModuleId { get; set; }
    /// <summary>@name annotation override. Takes precedence over ModuleId for name section output.</summary>
    public string? AnnotationName { get; set; }
}

public enum DeclKind
{
    Func,
    Memory,
    Export,
    Import,
    Global,
    Data,
    Table,
    Elem,
    Tag,
}

public sealed class WatImport
{
    public SourceSpan Span { get; set; }
    public required string ModuleName { get; set; }
    public required string FieldName { get; set; }
    public ImportKind Kind { get; set; }
    // For func imports
    public string? FuncName { get; set; }
    public string? AnnotationName { get; set; }
    public int? TypeIndex { get; set; }
    public string? TypeName { get; set; }
    public List<WatParam> Params { get; } = [];
    public List<ValType> Results { get; } = [];
    // For memory imports
    public int MemMin { get; set; }
    public int? MemMax { get; set; }
    // For global imports
    public ValType GlobalType { get; set; }
    public bool GlobalMutable { get; set; }
    // For table imports
    public uint TableMin { get; set; }
    public uint? TableMax { get; set; }
    public WatReferenceType TableElementType { get; set; } = WatReferenceType.FuncRef;
}

public enum ImportKind
{
    Func,
    Memory,
    Table,
    Global,
    Tag,
}

public sealed class WatGlobal
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    public ValType Type { get; set; }
    public bool Mutable { get; set; }
    public WatInstruction? Init { get; set; }
    public List<WatExport> InlineExports { get; } = [];
}

public sealed class WatData
{
    public SourceSpan Span { get; set; }
    public WatInstruction? Offset { get; set; }
    public byte[] Bytes { get; set; } = [];
}

public sealed class WatTable
{
    public SourceSpan Span { get; set; }
    public uint Min { get; set; }
    public uint? Max { get; set; }

    public WatReferenceType ElementType { get; set; } = WatReferenceType.FuncRef;
    public ValType ElemType
    {
        get => ElementType.HeapType is { } heapType
            ? new Microsoft.NET.WebAssembly.Types.ValueType.Reference(ElementType.Nullability, heapType).ToStorageValType()
            : ValType.FuncRef;
        set
        {
            var exact = (Microsoft.NET.WebAssembly.Types.ValueType.Reference)
                Microsoft.NET.WebAssembly.Types.ValueType.FromStorageValType(value);
            ElementType = new WatReferenceType(exact.Nullability, exact.HeapType);
        }
    }

    /// <summary>
    /// Optional initializer constant expression — <c>(table 3 funcref (ref.func $f))</c>. When
    /// present the encoder must emit the extended <c>0x40 0x00</c> table form (GitHub #383).
    /// </summary>
    public WatInstruction? Init { get; set; }
}

public sealed class WatElem
{
    public SourceSpan Span { get; set; }
    public int TableIndex { get; set; }
    public bool HasExplicitTable { get; set; }
    public WatElemMode Mode { get; set; }
    public WatInstruction? Offset { get; set; }
    public List<int> FuncIndices { get; } = [];
    public List<string?> FuncNames { get; } = [];
    public bool UsesExpressions { get; set; }
    public WatReferenceType ElementType { get; set; } = WatReferenceType.FuncRef;
    public List<List<WatInstruction>> ElementExpressions { get; } = [];
}

public enum WatElemMode
{
    Active,
    Passive,
    Declarative
}

public sealed record WatReferenceType(
    Nullability Nullability,
    HeapType? HeapType,
    string? TypeName = null)
{
    public static WatReferenceType FuncRef { get; } =
        new(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func));
}

public sealed class WatStart
{
    public SourceSpan Span { get; set; }
    public int Index { get; set; }
    public string? Ref { get; set; }
}

public sealed class WatTag
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    public List<ValType> Params { get; } = [];
    public List<ValType> Results { get; } = [];
    public int? TypeIndex { get; set; }
    public string? TypeName { get; set; }
}

public sealed class WatFunc
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    /// <summary>@name annotation override for name section output.</summary>
    public string? AnnotationName { get; set; }
    public List<WatParam> Params { get; } = [];
    public List<ValType> Results { get; } = [];
    public List<WatLocal> Locals { get; } = [];
    public List<WatInstruction> Body { get; } = [];
    public List<WatExport> InlineExports { get; } = [];
    public int? TypeIndex { get; set; }
}

public sealed class WatParam
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    /// <summary>@name annotation override for name section output.</summary>
    public string? AnnotationName { get; set; }
    public ValType Type { get; set; }
}

public sealed class WatLocal
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    /// <summary>@name annotation override for name section output.</summary>
    public string? AnnotationName { get; set; }
    public ValType Type { get; set; }
}

public sealed class WatExport
{
    public SourceSpan Span { get; set; }
    public required string Name { get; set; }
    public ExportKind Kind { get; set; }
    public string? Ref { get; set; }
    public int Index { get; set; } = -1;
}

public enum ExportKind
{
    Func,
    Memory,
    Table,
    Global,
}

public sealed class WatMemory
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    public uint Min { get; set; }
    public uint? Max { get; set; }
    public List<WatExport> InlineExports { get; } = [];
}

public sealed class WatFuncType
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    public List<ValType> Params { get; } = [];
    public List<ValType> Results { get; } = [];
}

public sealed class WatInstruction
{
    public SourceSpan Span { get; set; }
    public required string Mnemonic { get; set; }
    public List<WatInstruction> Operands { get; } = [];
    // Immediates
    public long? IntValue { get; set; }
    public double? FloatValue { get; set; }
    public string? IdValue { get; set; }
    public int? Index { get; set; }
    // Block type for block/loop/if
    public ValType? BlockResultType { get; set; }
    public int? BlockTypeIndex { get; set; }
    /// <summary>Multi-value block results (when more than one result type).</summary>
    public List<ValType>? BlockResultTypes { get; set; }
    // then/else for if
    public List<WatInstruction>? ThenBody { get; set; }
    public List<WatInstruction>? ElseBody { get; set; }
    // br_table labels
    public List<string>? BrTableLabels { get; set; }
    public List<int>? BrTableIndices { get; set; }
    // SIMD immediates
    public byte[]? V128Bytes { get; set; }
    public int? LaneIndex { get; set; }
    // try_table catch handlers
    public List<WatCatchHandler>? CatchHandlers { get; set; }
    // Legacy try: list of (catch $tag body...) / (catch_all body...) clauses
    public List<WatLegacyCatchClause>? LegacyCatchClauses { get; set; }
    // Legacy try: delegate label depth
    public int? DelegateLabelIndex { get; set; }
    public string? DelegateLabelName { get; set; }
    /// <summary>Branch metadata attached to this instruction during WAT compilation.</summary>
    public BranchHintKind? BranchHint { get; set; }

    /// <summary>
    /// Names of the properties above that <see cref="CreateResolvedShell"/> deliberately does not
    /// carry across, because <c>WatResolver</c> recomputes them from the symbolic form.
    /// Every other property must survive the copy verbatim.
    /// </summary>
    /// <remarks>
    /// This list exists so <c>WatInstructionResolvedShellTests</c> can prove, by reflection, that a
    /// newly added immediate is either copied or consciously excluded. Issue #419 was a field added
    /// to this class (<see cref="V128Bytes"/>) that the resolver's hand-written copy never learnt
    /// about, so every <c>v128.const</c> silently encoded as sixteen zero bytes.
    /// </remarks>
    internal static readonly string[] ResolverRecomputedMembers =
    [
        nameof(Operands),           // resolved recursively into the new instruction
        nameof(BlockResultTypes),   // folded into BlockTypeIndex
        nameof(ThenBody),           // resolved recursively
        nameof(ElseBody),           // resolved recursively
        nameof(BrTableLabels),      // resolved into BrTableIndices
        nameof(CatchHandlers),      // tag/label names resolved to indices
        nameof(LegacyCatchClauses), // bodies resolved recursively
        nameof(DelegateLabelName),  // resolved into DelegateLabelIndex
    ];

    /// <summary>
    /// Creates the target instruction for a resolve pass, carrying every immediate that resolution
    /// does not rewrite. Kept next to the field declarations on purpose: a copy that lives beside
    /// the fields it mirrors is far harder to leave behind than one in another file.
    /// </summary>
    internal WatInstruction CreateResolvedShell() => new()
    {
        Span = Span,
        Mnemonic = Mnemonic,
        IntValue = IntValue,
        FloatValue = FloatValue,
        IdValue = IdValue,
        Index = Index,
        BlockResultType = BlockResultType,
        BlockTypeIndex = BlockTypeIndex,
        BrTableIndices = BrTableIndices,
        V128Bytes = V128Bytes is null ? null : (byte[])V128Bytes.Clone(),
        LaneIndex = LaneIndex,
        DelegateLabelIndex = DelegateLabelIndex,
        BranchHint = BranchHint,
    };
}

/// <summary>A catch handler clause for try_table.</summary>
public sealed class WatCatchHandler
{
    public SourceSpan Span { get; set; }
    /// <summary>0x00=catch, 0x01=catch_ref, 0x02=catch_all, 0x03=catch_all_ref</summary>
    public byte CatchKind { get; set; }
    /// <summary>Tag index or name (for catch/catch_ref).</summary>
    public int TagIndex { get; set; } = -1;
    public string? TagName { get; set; }
    /// <summary>Label index or name (branch target).</summary>
    public int LabelIndex { get; set; } = -1;
    public string? LabelName { get; set; }
}

/// <summary>A catch/catch_all clause in a legacy (try (do ...) (catch ...) ...) block.</summary>
public sealed class WatLegacyCatchClause
{
    public SourceSpan Span { get; set; }
    /// <summary>true = catch_all, false = catch with specific tag.</summary>
    public bool IsCatchAll { get; set; }
    /// <summary>Tag index (for catch, not catch_all).</summary>
    public int TagIndex { get; set; } = -1;
    public string? TagName { get; set; }
    /// <summary>Body instructions inside the catch/catch_all clause.</summary>
    public List<WatInstruction> Body { get; } = [];
}

/// <summary>Recursive descent parser for the WebAssembly text format.</summary>
public sealed class WatParser
{
    private readonly List<WatToken> _tokens;
    private int _pos;
    private readonly List<WatDiagnostic>? _diagnostics;

    public WatParser(List<WatToken> tokens, List<WatDiagnostic>? diagnostics = null)
    {
        _tokens = RemoveGenericAnnotations(tokens);
        _diagnostics = diagnostics;
    }

    private static List<WatToken> RemoveGenericAnnotations(List<WatToken> tokens)
    {
        var filtered = new List<WatToken>(tokens.Count);
        for (int i = 0; i < tokens.Count;)
        {
            if (i + 1 < tokens.Count
                && tokens[i].Kind == WatTokenKind.LParen
                && tokens[i + 1].Kind == WatTokenKind.Keyword
                && tokens[i + 1].Value.StartsWith('@')
                && (tokens[i].Line != tokens[i + 1].Line
                    || tokens[i].Column + 1 != tokens[i + 1].Column))
            {
                var at = tokens[i + 1];
                throw new WatException(
                    "Parse error: annotation identifier must be adjacent to '('",
                    at.Line,
                    at.Column);
            }

            if (!TryGetGenericAnnotationEnd(tokens, i, out int end))
            {
                filtered.Add(tokens[i++]);
                continue;
            }

            if (IsBranchHintAnnotation(tokens, i))
            {
                // Keep the recognized header so the parser can attach or reject it, but
                // continue walking its contents to remove nested generic annotations.
                filtered.Add(tokens[i++]); // (
                WatToken annotationId = tokens[i++];
                filtered.Add(annotationId);
                if (annotationId.Value == "@")
                    filtered.Add(tokens[i++]); // quoted annotation identifier
                continue;
            }

            i = end;
        }
        return filtered;
    }

    private static bool IsBranchHintAnnotation(List<WatToken> tokens, int start)
    {
        if (start + 1 >= tokens.Count)
            return false;

        WatToken at = tokens[start + 1];
        if (at.Value == "@metadata.code.branch_hint")
            return true;

        return at.Value == "@"
            && start + 2 < tokens.Count
            && tokens[start + 2].Kind == WatTokenKind.String
            && tokens[start + 2].Value == "metadata.code.branch_hint";
    }

    private static bool TryGetGenericAnnotationEnd(List<WatToken> tokens, int start, out int end)
    {
        end = start;
        if (start + 1 >= tokens.Count || tokens[start].Kind != WatTokenKind.LParen)
            return false;

        var open = tokens[start];
        var at = tokens[start + 1];
        if (at.Kind != WatTokenKind.Keyword
            || open.Line != at.Line
            || open.Column + 1 != at.Column)
            return false;

        int headerLength;
        bool isName;
        if (at.Value.Length > 1 && at.Value[0] == '@')
        {
            headerLength = 2;
            isName = at.Value == "@name";
        }
        else if (at.Value == "@" && start + 2 < tokens.Count)
        {
            var id = tokens[start + 2];
            if (id.Kind != WatTokenKind.String
                || id.Value.Length == 0
                || at.Line != id.Line
                || at.Column + 1 != id.Column)
                return false;
            headerLength = 3;
            isName = id.Value == "name";
        }
        else
        {
            return false;
        }

        if (isName)
            return false;

        int depth = 0;
        for (int i = start; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == WatTokenKind.LParen)
                depth++;
            else if (tokens[i].Kind == WatTokenKind.RParen && --depth == 0)
            {
                end = i + 1;
                return end >= start + headerLength;
            }
            else if (tokens[i].Kind == WatTokenKind.EOF)
                return false;
        }
        return false;
    }

    public WatModule ParseModule()
    {
        var startToken = Peek();
        var module = new WatModule();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("module");

        // Optional module $id
        if (Peek().Kind == WatTokenKind.Identifier)
            module.ModuleId = Consume().Value;

        // @name at the module head (before any members) — captures the name-section override.
        // @name after members is a parse error; we track this by capturing only here.
        module.AnnotationName = ConsumeNameAnnotation("module");

        while (!IsRParen())
        {
            if (Peek().Kind == WatTokenKind.LParen)
            {
                int savedPos = _pos;
                Consume(); // (
                var kw = Peek();

                if (kw.Kind == WatTokenKind.Keyword)
                {
                    switch (kw.Value)
                    {
                        case "func":
                            _pos = savedPos;
                            if (IsInlineFuncImport())
                            {
                                var import = TryParseTopLevel(ParseInlineFuncImport, savedPos);
                                if (import != null)
                                {
                                    module.DeclOrder.Add((DeclKind.Import, module.Imports.Count));
                                    module.Imports.Add(import);
                                }
                            }
                            else
                            {
                                var f = TryParseTopLevel(ParseFunc, savedPos);
                                if (f != null)
                                {
                                    module.DeclOrder.Add((DeclKind.Func, module.Funcs.Count));
                                    module.Funcs.Add(f);
                                }
                            }
                            continue;
                        case "memory":
                            _pos = savedPos;
                            { var m = TryParseTopLevel(ParseMemory, savedPos); if (m != null) { module.DeclOrder.Add((DeclKind.Memory, module.Memories.Count)); module.Memories.Add(m); } }
                            continue;
                        case "export":
                            _pos = savedPos;
                            { var e = TryParseTopLevel(ParseExport, savedPos); if (e != null) { module.DeclOrder.Add((DeclKind.Export, module.Exports.Count)); module.Exports.Add(e); } }
                            continue;
                        case "type":
                            _pos = savedPos;
                            { var t = TryParseTopLevel(ParseType, savedPos); if (t != null) module.Types.Add(t); }
                            continue;
                        case "import":
                            _pos = savedPos;
                            { var i = TryParseTopLevel(ParseImport, savedPos); if (i != null) { module.DeclOrder.Add((DeclKind.Import, module.Imports.Count)); module.Imports.Add(i); } }
                            continue;
                        case "global":
                            _pos = savedPos;
                            { var g = TryParseTopLevel(ParseGlobal, savedPos); if (g != null) { module.DeclOrder.Add((DeclKind.Global, module.Globals.Count)); module.Globals.Add(g); } }
                            continue;
                        case "data":
                            _pos = savedPos;
                            { var d = TryParseTopLevel(ParseData, savedPos); if (d != null) { module.DeclOrder.Add((DeclKind.Data, module.DataSegments.Count)); module.DataSegments.Add(d); } }
                            continue;
                        case "table":
                            _pos = savedPos;
                            { var t = TryParseTopLevel(ParseTable, savedPos); if (t != null) { module.DeclOrder.Add((DeclKind.Table, module.Tables.Count)); module.Tables.Add(t); } }
                            continue;
                        case "elem":
                            _pos = savedPos;
                            { var el = TryParseTopLevel(ParseElem, savedPos); if (el != null) { module.DeclOrder.Add((DeclKind.Elem, module.Elements.Count)); module.Elements.Add(el); } }
                            continue;
                        case "tag":
                            _pos = savedPos;
                            { var tg = TryParseTopLevel(ParseTag, savedPos); if (tg != null) { module.DeclOrder.Add((DeclKind.Tag, module.Tags.Count)); module.Tags.Add(tg); } }
                            continue;
                        case "start":
                            _pos = savedPos;
                            if (module.Start != null)
                            {
                                var token = Peek();
                                var error = new WatException(
                                    "Parse error: multiple start declarations",
                                    token.Line,
                                    token.Column);
                                if (_diagnostics == null)
                                    throw error;
                                _diagnostics.Add(new WatDiagnostic(error.Line, error.Column, error.Message));
                                SkipSExpr();
                            }
                            else
                            {
                                module.Start = TryParseTopLevel(ParseStart, savedPos);
                            }
                            continue;
                    }
                }
                _pos = savedPos;
                if (IsAnnotationHeader())
                {
                    if (IsBranchHintAnnotation())
                        throw Error("@metadata.code.branch_hint annotation: not in a function");

                    // Check for misplaced @name — both standard and quoted form.
                    bool isNameAnnotation = PeekAt(1).Value == "@name"
                        || (PeekAt(1).Value == "@" && _pos + 2 < _tokens.Count
                            && PeekAt(2).Kind == WatTokenKind.String && PeekAt(2).Value == "name");
                    if (isNameAnnotation)
                        throw Error("@name annotation: misplaced after module members");
                    // Other annotation at module level — skip silently.
                    SkipSExpr();
                }
                else if (kw.Kind == WatTokenKind.Keyword && kw.Value.Length > 0 && kw.Value[0] == '@')
                {
                    // Malformed annotation: (@) or (@ x) — treat as parse error
                    throw Error($"malformed annotation '{kw.Value}' at {kw.Line}:{kw.Column}");
                }
                else
                {
                    // Unknown module field — skip
                    SkipSExpr();
                }
            }
            else
            {
                var tok = Peek();
                if (tok.Kind == WatTokenKind.Keyword && tok.Value.Length > 0 && tok.Value[0] == '@')
                    throw Error($"annotation '{tok.Value}' without parentheses");
                Consume();
            }
        }

        module.Span = SpanFrom(startToken);
        if (_diagnostics != null)
        {
            // In analyze mode: soft-close — don't throw on missing or mismatched paren
            if (Peek().Kind == WatTokenKind.RParen)
                Consume();
            else if (Peek().Kind != WatTokenKind.EOF)
                _diagnostics.Add(new WatDiagnostic(Peek().Line, Peek().Column,
                    $"Parse error: Expected ')', got '{Peek().Value}'"));
            return module;
        }
        Expect(WatTokenKind.RParen);
        if (Peek().Kind != WatTokenKind.EOF)
            throw Error($"Unexpected token after module: {Peek().Value}");
        return module;
    }

    private WatFuncType ParseType()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("type");
        var ft = new WatFuncType();

        if (Peek().Kind == WatTokenKind.Identifier)
            ft.Name = Consume().Value;

        ConsumeNameAnnotation("type");
        // (func (param ...) (result ...))
        Expect(WatTokenKind.LParen);
        ExpectKeyword("func");

        while (!IsRParen())
        {
            if (Peek().Kind == WatTokenKind.LParen)
            {
                if (IsAnnotationHeader())
                {
                    SkipAnnotations();
                    continue;
                }
                Consume();
                var kw = Peek().Value;
                if (kw == "param")
                {
                    Consume();
                    while (!IsRParen())
                    {
                        if (Peek().Kind == WatTokenKind.Identifier)
                            Consume(); // skip name
                        ft.Params.Add(ParseValType());
                    }
                    Expect(WatTokenKind.RParen);
                }
                else if (kw == "result")
                {
                    Consume();
                    while (!IsRParen())
                        ft.Results.Add(ParseValType());
                    Expect(WatTokenKind.RParen);
                }
                else
                {
                    SkipToRParen();
                }
            }
            else
            {
                Consume();
            }
        }

        Expect(WatTokenKind.RParen); // close func
        Expect(WatTokenKind.RParen); // close type
        ft.Span = SpanFrom(startToken);
        return ft;
    }

    private WatFunc ParseFunc()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("func");
        var func = new WatFunc();

        if (Peek().Kind == WatTokenKind.Identifier)
            func.Name = Consume().Value;

        func.AnnotationName = ConsumeNameAnnotation("function");
        // Inline exports: (export "name")
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); // (
            Consume(); // export
            var name = ExpectUtf8String();
            func.InlineExports.Add(new WatExport { Name = name, Kind = ExportKind.Func });
            Expect(WatTokenKind.RParen);
        }

        // @name is also valid after inline exports (before the type use).
        // Merge with any earlier head annotation, preserving duplicate detection.
        var postExportAnnot = ConsumeNameAnnotation("function");
        if (func.AnnotationName != null && postExportAnnot != null)
            throw Error("@name annotation: multiple function names");
        func.AnnotationName ??= postExportAnnot;

        SkipNonBranchAnnotations();
        // Optional type use: (type $name) or (type 0)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "type")
        {
            Consume(); // (
            Consume(); // type
            if (Peek().Kind == WatTokenKind.Identifier)
                func.TypeIndex = -1; // resolved later
            else if (Peek().Kind == WatTokenKind.Integer)
                func.TypeIndex = ParseInt32();
            else
                Consume();
            Expect(WatTokenKind.RParen);
        }

        SkipNonBranchAnnotations();
        // Params
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
        {
            var paramGroupStart = Peek();
            Consume(); // (
            Consume(); // param
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                // Named form: (param $id [@name "str"] type)
                var p = new WatParam { Name = Consume().Value };
                p.AnnotationName = ConsumeNameAnnotation("local");
                p.Type = ParseValType();
                Expect(WatTokenKind.RParen);
                p.Span = SpanFrom(paramGroupStart);
                func.Params.Add(p);
                continue;
            }
            while (!IsRParen())
            {
                // Anonymous form — may have @name before the type for a single-param annotation
                var typeStart = Peek();
                string? annotName = ConsumeNameAnnotation("local");
                if (IsRParen() && annotName != null)
                    throw Error("@name annotation: type expected");
                if (IsRParen())
                    break;
                var p = new WatParam { AnnotationName = annotName, Type = ParseValType() };
                p.Span = SpanFrom(typeStart);
                func.Params.Add(p);
            }
            Expect(WatTokenKind.RParen);
            SkipNonBranchAnnotations();
        }

        SkipNonBranchAnnotations();
        // Results
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); // (
            Consume(); // result
            while (!IsRParen())
            {
                SkipAnnotations();
                if (!IsRParen())
                    func.Results.Add(ParseValType());
            }
            Expect(WatTokenKind.RParen);
            SkipNonBranchAnnotations();
        }

        SkipNonBranchAnnotations();
        // Locals
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "local")
        {
            var localGroupStart = Peek();
            Consume(); // (
            Consume(); // local
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                // Named form: (local $id [@name "str"] type)
                var l = new WatLocal { Name = Consume().Value };
                l.AnnotationName = ConsumeNameAnnotation("local");
                l.Type = ParseValType();
                Expect(WatTokenKind.RParen);
                l.Span = SpanFrom(localGroupStart);
                func.Locals.Add(l);
                continue;
            }
            while (!IsRParen())
            {
                // Anonymous form — may have @name before the type
                var typeStart = Peek();
                string? annotName = ConsumeNameAnnotation("local");
                if (IsRParen() && annotName != null)
                    throw Error("@name annotation: type expected");
                if (IsRParen())
                    break;
                var l = new WatLocal { AnnotationName = annotName, Type = ParseValType() };
                l.Span = SpanFrom(typeStart);
                func.Locals.Add(l);
            }
            Expect(WatTokenKind.RParen);
        }

        // Body instructions
        ParseBodyItems(func.Body);

        Expect(WatTokenKind.RParen);
        func.Span = SpanFrom(startToken);
        return func;
    }

    private bool IsInlineFuncImport()
    {
        int lookahead = _pos + 2; // ( func
        if (lookahead < _tokens.Count && _tokens[lookahead].Kind == WatTokenKind.Identifier)
            lookahead++;
        return lookahead + 1 < _tokens.Count
            && _tokens[lookahead].Kind == WatTokenKind.LParen
            && _tokens[lookahead + 1].Kind == WatTokenKind.Keyword
            && _tokens[lookahead + 1].Value == "import";
    }

    private WatImport ParseInlineFuncImport()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("func");

        string? name = Peek().Kind == WatTokenKind.Identifier ? Consume().Value : null;
        string? annotationName = ConsumeNameAnnotation("imported function");

        Expect(WatTokenKind.LParen);
        ExpectKeyword("import");
        string moduleName = ExpectUtf8String();
        string fieldName = ExpectUtf8String();
        Expect(WatTokenKind.RParen);

        var import = new WatImport
        {
            Kind = ImportKind.Func,
            ModuleName = moduleName,
            FieldName = fieldName,
            FuncName = name,
            AnnotationName = annotationName,
        };

        ParseOptionalImportTypeUse(import);

        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
        {
            Consume();
            Consume();
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                var param = new WatParam { Name = Consume().Value };
                param.AnnotationName = ConsumeNameAnnotation("local");
                param.Type = ParseValType();
                import.Params.Add(param);
            }
            else
            {
                while (!IsRParen())
                    import.Params.Add(new WatParam { Type = ParseValType() });
            }
            Expect(WatTokenKind.RParen);
        }

        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume();
            Consume();
            while (!IsRParen())
                import.Results.Add(ParseValType());
            Expect(WatTokenKind.RParen);
        }

        Expect(WatTokenKind.RParen);
        import.Span = SpanFrom(startToken);
        return import;
    }

    private void ParseOptionalImportTypeUse(WatImport import)
    {
        if (Peek().Kind != WatTokenKind.LParen || PeekAt(1).Value != "type")
            return;

        Consume();
        Consume();
        if (Peek().Kind == WatTokenKind.Identifier)
            import.TypeName = Consume().Value;
        else if (Peek().Kind == WatTokenKind.Integer)
            import.TypeIndex = ParseInt32();
        else
            throw Error("Expected type identifier or index in imported function or tag");
        Expect(WatTokenKind.RParen);
    }

    private WatInstruction ParseInstruction()
    {
        // Folded S-expression form: (opcode operands...)
        if (Peek().Kind == WatTokenKind.LParen)
            return ParseFoldedInstruction();

        // Flat instruction
        return ParsePlainInstruction();
    }

    private WatInstruction ParseFoldedInstruction()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        var kw = Expect(WatTokenKind.Keyword);
        var mnemonic = kw.Value;

        WatInstruction instr;
        if (mnemonic == "if")
            instr = ParseFoldedIf();
        else if (mnemonic == "try")
            instr = ParseFoldedLegacyTry();
        else if (mnemonic is "block" or "loop" or "try_table")
            instr = ParseFoldedBlock(mnemonic);
        else
        {
            instr = new WatInstruction { Mnemonic = mnemonic };
            ParseImmediates(instr);
            while (IsAnnotationHeader() || Peek().Kind == WatTokenKind.LParen)
            {
                BranchHintKind? branchHint = ConsumeBodyAnnotations();
                if (Peek().Kind != WatTokenKind.LParen)
                {
                    if (branchHint.HasValue)
                        throw Error("@metadata.code.branch_hint annotation: no following instruction");
                    break;
                }
                instr.Operands.Add(ParseInstructionWithBranchHint(branchHint));
            }
            Expect(WatTokenKind.RParen);
        }
        instr.Span = SpanFrom(startToken);
        return instr;
    }

    private WatInstruction ParseFoldedIf()
    {
        var instr = new WatInstruction { Mnemonic = "if" };

        ConsumeNameAnnotation("label");
        // Optional block type: (result i32)
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); // (
            Consume(); // result
            if (!IsRParen())
                instr.BlockResultType = ParseValType();
            Expect(WatTokenKind.RParen);
        }

        // Condition (folded expression before then/else)
        while (IsAnnotationHeader() ||
               (Peek().Kind == WatTokenKind.LParen &&
                PeekAt(1).Value is not "then" and not "else"))
        {
            BranchHintKind? branchHint = ConsumeBodyAnnotations();
            if (Peek().Kind != WatTokenKind.LParen ||
                PeekAt(1).Value is "then" or "else")
            {
                if (branchHint.HasValue)
                    throw Error("@metadata.code.branch_hint annotation: no following instruction");
                break;
            }
            instr.Operands.Add(ParseInstructionWithBranchHint(branchHint));
        }

        SkipAnnotations();
        // (then ...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "then")
        {
            Consume(); // (
            Consume(); // then
            instr.ThenBody = [];
            ParseBodyItems(instr.ThenBody);
            Expect(WatTokenKind.RParen);
        }

        SkipAnnotations();
        // (else ...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "else")
        {
            Consume(); // (
            Consume(); // else
            instr.ElseBody = [];
            ParseBodyItems(instr.ElseBody);
            Expect(WatTokenKind.RParen);
        }

        Expect(WatTokenKind.RParen);
        return instr;
    }

    private WatInstruction ParseFoldedBlock(string mnemonic)
    {
        var instr = new WatInstruction { Mnemonic = mnemonic };

        // Optional label
        if (Peek().Kind == WatTokenKind.Identifier)
            instr.IdValue = Consume().Value;

        ConsumeNameAnnotation("label");
        // Optional block type: (result i32) or (result i32 i32)
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); Consume();
            var types = new List<ValType>();
            while (!IsRParen())
                types.Add(ParseValType());
            if (types.Count == 1 && instr.BlockResultTypes == null)
                instr.BlockResultType = types[0];
            else
            {
                instr.BlockResultTypes ??= [];
                if (instr.BlockResultType != null)
                {
                    instr.BlockResultTypes.Add(instr.BlockResultType.Value);
                    instr.BlockResultType = null;
                }
                instr.BlockResultTypes.AddRange(types);
            }
            Expect(WatTokenKind.RParen);
        }

        if (mnemonic == "try_table")
            ParseTryTableCatchHandlers(instr);

        // Body instructions
        instr.ThenBody = [];
        ParseBodyItems(instr.ThenBody);

        Expect(WatTokenKind.RParen);
        return instr;
    }

    /// <summary>
    /// Parse legacy folded try: (try [label] [blocktype] (do body...) (catch $tag body...)* [(catch_all body...)] )
    /// or (try [label] [blocktype] (do body...) (delegate depth) )
    /// </summary>
    private WatInstruction ParseFoldedLegacyTry()
    {
        var instr = new WatInstruction { Mnemonic = "try" };

        // Optional label
        if (Peek().Kind == WatTokenKind.Identifier)
            instr.IdValue = Consume().Value;

        ConsumeNameAnnotation("label");
        // Optional block type: (result i32) or (result i32 i32)
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); Consume();
            var types = new List<ValType>();
            while (!IsRParen())
                types.Add(ParseValType());
            if (types.Count == 1 && instr.BlockResultTypes == null)
                instr.BlockResultType = types[0];
            else
            {
                instr.BlockResultTypes ??= [];
                if (instr.BlockResultType != null)
                {
                    instr.BlockResultTypes.Add(instr.BlockResultType.Value);
                    instr.BlockResultType = null;
                }
                instr.BlockResultTypes.AddRange(types);
            }
            Expect(WatTokenKind.RParen);
        }

        SkipAnnotations();
        // (do body...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "do")
        {
            Consume(); // (
            Consume(); // do
            instr.ThenBody = [];
            ParseBodyItems(instr.ThenBody);
            Expect(WatTokenKind.RParen);
        }

        SkipAnnotations();
        // (catch $tag body...) or (catch_all body...) or (delegate depth)
        instr.LegacyCatchClauses = [];
        while (Peek().Kind == WatTokenKind.LParen &&
               PeekAt(1).Value is "catch" or "catch_all" or "delegate")
        {
            var clauseStart = Peek();
            Consume(); // (
            var kw = Consume().Value;

            if (kw == "delegate")
            {
                // (delegate depth)
                if (Peek().Kind == WatTokenKind.Identifier)
                    instr.DelegateLabelName = Consume().Value;
                else if (Peek().Kind == WatTokenKind.Integer)
                    instr.DelegateLabelIndex = ParseInt32();
                else
                    instr.DelegateLabelIndex = 0;
                Expect(WatTokenKind.RParen);
                break; // delegate terminates the try
            }

            var clause = new WatLegacyCatchClause();
            if (kw == "catch_all")
            {
                clause.IsCatchAll = true;
            }
            else
            {
                // catch $tag
                if (Peek().Kind == WatTokenKind.Identifier)
                    clause.TagName = Consume().Value;
                else
                    clause.TagIndex = ParseInt32();
            }

            ParseBodyItems(clause.Body);
            Expect(WatTokenKind.RParen);
            clause.Span = SpanFrom(clauseStart);
            instr.LegacyCatchClauses.Add(clause);
            SkipAnnotations();
        }

        Expect(WatTokenKind.RParen);
        return instr;
    }

    private WatInstruction ParsePlainInstruction()
    {
        var tok = Expect(WatTokenKind.Keyword);
        var instr = new WatInstruction { Mnemonic = tok.Value };

        // Handle structured instructions in flat form.
        if (tok.Value is "block" or "loop" or "if" or "try" or "try_table")
        {
            // Optional label
            if (Peek().Kind == WatTokenKind.Identifier)
                instr.IdValue = Consume().Value;
            ParseBlockResultTypes(instr);

            switch (tok.Value)
            {
                case "if":
                    instr.ThenBody = [];
                    while (true)
                    {
                        BranchHintKind? branchHint = ConsumeBodyAnnotations();
                        if (IsKeyword("else") || IsKeyword("end"))
                        {
                            RequireFollowingInstruction(branchHint);
                            break;
                        }
                        instr.ThenBody.Add(ParseInstructionWithBranchHint(branchHint));
                    }

                    if (IsKeyword("else"))
                    {
                        Consume();
                        ConsumeMatchingControlLabel(instr, "else");
                        instr.ElseBody = [];
                        while (true)
                        {
                            BranchHintKind? branchHint = ConsumeBodyAnnotations();
                            if (IsKeyword("end"))
                            {
                                RequireFollowingInstruction(branchHint);
                                break;
                            }
                            instr.ElseBody.Add(ParseInstructionWithBranchHint(branchHint));
                        }
                    }
                    ExpectKeyword("end");
                    ConsumeMatchingControlLabel(instr, "end");
                    instr.Span = SpanFrom(tok);
                    return instr;

                case "try":
                    ParsePlainLegacyTry(instr);
                    instr.Span = SpanFrom(tok);
                    return instr;

                default:
                    if (tok.Value == "try_table")
                        ParseTryTableCatchHandlers(instr);
                    instr.ThenBody = [];
                    while (true)
                    {
                        BranchHintKind? branchHint = ConsumeBodyAnnotations();
                        if (IsKeyword("end"))
                        {
                            RequireFollowingInstruction(branchHint);
                            break;
                        }
                        instr.ThenBody.Add(ParseInstructionWithBranchHint(branchHint));
                    }
                    ExpectKeyword("end");
                    ConsumeMatchingControlLabel(instr, "end");
                    instr.Span = SpanFrom(tok);
                    return instr;
            }
        }

        ParseImmediates(instr);
        instr.Span = SpanFrom(tok);
        return instr;
    }

    private void ParseBlockResultTypes(WatInstruction instr)
    {
        ConsumeNameAnnotation("label");
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); // (
            Consume(); // result
            var types = new List<ValType>();
            while (!IsRParen())
                types.Add(ParseValType());

            if (types.Count == 1 && instr.BlockResultTypes == null)
            {
                instr.BlockResultType = types[0];
            }
            else
            {
                instr.BlockResultTypes ??= [];
                if (instr.BlockResultType is { } existingType)
                {
                    instr.BlockResultTypes.Add(existingType);
                    instr.BlockResultType = null;
                }

                instr.BlockResultTypes.AddRange(types);
            }

            Expect(WatTokenKind.RParen);
        }
    }

    private void ParsePlainLegacyTry(WatInstruction instr)
    {
        instr.ThenBody = [];
        instr.LegacyCatchClauses = [];

        while (true)
        {
            BranchHintKind? branchHint = ConsumeBodyAnnotations();
            if (IsKeyword("end"))
            {
                RequireFollowingInstruction(branchHint);
                Consume();
                ConsumeMatchingControlLabel(instr, "end");
                return;
            }

            if (IsKeyword("delegate"))
            {
                RequireFollowingInstruction(branchHint);
                Consume();
                if (Peek().Kind == WatTokenKind.Identifier)
                    instr.DelegateLabelName = Consume().Value;
                else if (Peek().Kind == WatTokenKind.Integer)
                    instr.DelegateLabelIndex = ParseInt32();
                else
                    instr.DelegateLabelIndex = 0;
                return;
            }

            if (IsKeyword("catch") || IsKeyword("catch_all"))
            {
                RequireFollowingInstruction(branchHint);
                var clause = new WatLegacyCatchClause { IsCatchAll = IsKeyword("catch_all") };
                Consume();

                if (!clause.IsCatchAll)
                {
                    if (Peek().Kind == WatTokenKind.Identifier)
                        clause.TagName = Consume().Value;
                    else
                        clause.TagIndex = ParseInt32();
                }

                while (true)
                {
                    BranchHintKind? clauseHint = ConsumeBodyAnnotations();
                    if (IsKeyword("catch") || IsKeyword("catch_all") ||
                        IsKeyword("delegate") || IsKeyword("end"))
                    {
                        RequireFollowingInstruction(clauseHint);
                        break;
                    }
                    clause.Body.Add(ParseInstructionWithBranchHint(clauseHint));
                }

                instr.LegacyCatchClauses.Add(clause);
                continue;
            }

            instr.ThenBody.Add(ParseInstructionWithBranchHint(branchHint));
        }
    }

    private void ParseTryTableCatchHandlers(WatInstruction instr)
    {
        instr.CatchHandlers = [];
        while (IsAnnotationHeader() && !IsBranchHintAnnotation())
            SkipBodyAnnotation();
        while (Peek().Kind == WatTokenKind.LParen &&
               PeekAt(1).Value is "catch" or "catch_ref" or "catch_all" or "catch_all_ref")
        {
            var handlerStart = Peek();
            Consume(); // (
            var catchKw = Consume().Value;
            var handler = new WatCatchHandler
            {
                CatchKind = catchKw switch
                {
                    "catch" => 0x00,
                    "catch_ref" => 0x01,
                    "catch_all" => 0x02,
                    "catch_all_ref" => 0x03,
                    _ => throw Error($"Unknown catch kind: {catchKw}"),
                }
            };

            if (handler.CatchKind is 0x00 or 0x01)
            {
                if (Peek().Kind == WatTokenKind.Identifier)
                    handler.TagName = Consume().Value;
                else
                    handler.TagIndex = ParseInt32();
            }

            if (Peek().Kind == WatTokenKind.Identifier)
                handler.LabelName = Consume().Value;
            else
                handler.LabelIndex = ParseInt32();

            Expect(WatTokenKind.RParen);
            handler.Span = SpanFrom(handlerStart);
            instr.CatchHandlers.Add(handler);
            while (IsAnnotationHeader() && !IsBranchHintAnnotation())
                SkipBodyAnnotation();
        }
    }

    private void ConsumeMatchingControlLabel(WatInstruction instr, string delimiter)
    {
        while (IsAnnotationHeader() && !IsBranchHintAnnotation())
            SkipBodyAnnotation();

        if (Peek().Kind != WatTokenKind.Identifier)
            return;

        WatToken occurrence = Peek();
        if (occurrence.Value != instr.IdValue)
        {
            throw new WatException(
                $"Mismatching label '{occurrence.Value}' after '{delimiter}'",
                occurrence.Line,
                occurrence.Column);
        }

        Consume();
    }

    private bool IsKeyword(string value) =>
        Peek().Kind == WatTokenKind.Keyword && Peek().Value == value;

    private void ParseImmediates(WatInstruction instr)
    {
        // Annotations between an opcode and its immediates are legal generically,
        // but @name is a binder annotation and has no valid position here.
        while (IsAnnotationHeader() && !IsBranchHintAnnotation())
            SkipBodyAnnotation();
        if (!OpcodeTable.TryGet(instr.Mnemonic, out var entry))
            return;

        switch (entry.Immediate)
        {
            case ImmediateKind.I32:
            case ImmediateKind.I64:
                if (Peek().Kind == WatTokenKind.Integer)
                    instr.IntValue = ParseInt64();
                break;

            case ImmediateKind.F32:
            case ImmediateKind.F64:
                if (Peek().Kind is WatTokenKind.Float or WatTokenKind.Integer
                    || (Peek().Kind == WatTokenKind.Keyword && IsFloatKeyword(Peek().Value)))
                    instr.FloatValue = entry.Immediate == ImmediateKind.F32
                        ? BitConverter.UInt32BitsToSingle((uint)ParseFloatLaneBits(32))
                        : ParseFloat64();
                break;

            case ImmediateKind.LocalIdx:
            case ImmediateKind.GlobalIdx:
            case ImmediateKind.FuncIdx:
                if (Peek().Kind == WatTokenKind.Identifier)
                    instr.IdValue = Consume().Value;
                else if (Peek().Kind == WatTokenKind.Integer)
                    instr.Index = ParseInt32();
                break;

            case ImmediateKind.TypeIdx:
                instr.Index = ParseInstructionTypeIndex();
                break;

            case ImmediateKind.TypePair:
                instr.Index = ParseInstructionTypeIndex();
                instr.IntValue = ParseInstructionTypeIndex();
                break;

            case ImmediateKind.TypeTagPair:
                instr.Index = ParseInstructionTypeIndex();
                if (Peek().Kind == WatTokenKind.Identifier)
                    instr.IdValue = Consume().Value;
                else
                    instr.IntValue = ParseInt32();
                break;

            case ImmediateKind.MemArg:
                ParseMemArg(instr);
                break;

            case ImmediateKind.MemIdx:
                // memory.size/memory.grow: implicit 0
                if (Peek().Kind == WatTokenKind.Integer)
                    instr.Index = ParseInt32();
                else
                    instr.Index = 0;
                break;

            case ImmediateKind.BrTable:
                ParseBrTable(instr);
                break;

            case ImmediateKind.CallIndirect:
                // call_indirect (type N) or call_indirect N
                if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "type")
                {
                    Consume(); // (
                    Consume(); // type
                    instr.Index = ParseInt32();
                    Expect(WatTokenKind.RParen);
                }
                else if (Peek().Kind == WatTokenKind.Integer)
                {
                    instr.Index = ParseInt32();
                }
                instr.IntValue = 0; // table index (default table 0)
                break;

            case ImmediateKind.ResumeTable:
                instr.Index = ParseInstructionTypeIndex();
                instr.CatchHandlers = [];
                while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "on")
                {
                    var handlerStart = Peek();
                    Consume(); // (
                    Consume(); // on
                    var handler = new WatCatchHandler { CatchKind = 0x00 };
                    if (Peek().Kind == WatTokenKind.Identifier)
                        handler.TagName = Consume().Value;
                    else
                        handler.TagIndex = ParseInt32();

                    if (Peek().Kind == WatTokenKind.Identifier)
                        handler.LabelName = Consume().Value;
                    else
                        handler.LabelIndex = ParseInt32();

                    Expect(WatTokenKind.RParen);
                    handler.Span = SpanFrom(handlerStart);
                    instr.CatchHandlers.Add(handler);
                }
                break;

            case ImmediateKind.TagIdx:
            case ImmediateKind.LabelIdx:
            case ImmediateKind.TableIdx:
            case ImmediateKind.DataIdx:
            case ImmediateKind.ElemIdx:
                if (Peek().Kind == WatTokenKind.Identifier)
                    instr.IdValue = Consume().Value;
                else if (Peek().Kind == WatTokenKind.Integer)
                    instr.Index = ParseInt32();
                break;

            case ImmediateKind.RefType:
            {
                var (heapType, typeName) = ParseHeapType("ref.null");
                if (typeName is not null)
                {
                    instr.IdValue = typeName;
                }
                else
                {
                    instr.IntValue = heapType switch
                    {
                        DefinedHeapType defined => defined.TypeIndex,
                        AbstractHeapType abstractType => SignedHeapType(abstractType.Kind),
                        _ => throw Error("ref.null requires an abstract or defined heap type"),
                    };
                }
                break;
            }

            case ImmediateKind.DataMemIdx:
                instr.Index = ParseInt32();       // data segment index
                if (Peek().Kind == WatTokenKind.Integer)
                    instr.IntValue = ParseInt32(); // memory index (optional, default 0)
                else
                    instr.IntValue = 0;
                break;

            case ImmediateKind.MemCopy:
                if (Peek().Kind == WatTokenKind.Integer)
                {
                    instr.Index = ParseInt32();    // dst memory index
                    instr.IntValue = ParseInt32(); // src memory index
                }
                else
                {
                    instr.Index = 0;
                    instr.IntValue = 0;
                }
                break;

            case ImmediateKind.ElemTableIdx:
                instr.Index = ParseInt32();        // element segment index
                instr.IntValue = ParseInt32();     // table index
                break;

            case ImmediateKind.TableCopy:
                instr.Index = ParseInt32();        // dst table index
                instr.IntValue = ParseInt32();     // src table index
                break;

            case ImmediateKind.V128:
                ParseV128Const(instr);
                break;

            case ImmediateKind.SimdLane:
                instr.Index = ParseInt32();
                break;

            case ImmediateKind.SimdShuffle:
                ParseSimdShuffle(instr);
                break;

            case ImmediateKind.MemArgLane:
                ParseMemArg(instr);
                instr.LaneIndex = ParseInt32();
                break;

            case ImmediateKind.TryTable:
                // Immediates are parsed in ParseFoldedBlock/ParsePlainInstruction
                break;
        }
    }

    private int ParseInstructionTypeIndex()
    {
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "type")
        {
            Consume(); // (
            Consume(); // type
            int idx = ParseInt32();
            Expect(WatTokenKind.RParen);
            return idx;
        }

        return ParseInt32();
    }

    private void ParseMemArg(WatInstruction instr)
    {
        // MemArg: optional offset=N align=N
        // defaults: offset=0, align=natural
        instr.Index = 0; // offset
        instr.IntValue = -1; // alignment (use natural if -1)

        while (Peek().Kind == WatTokenKind.Keyword)
        {
            var tok = Peek();
            if (tok.Value.StartsWith("offset="))
            {
                Consume();
                instr.Index = int.Parse(tok.Value["offset=".Length..]);
            }
            else if (tok.Value.StartsWith("align="))
            {
                Consume();
                int alignment = int.Parse(tok.Value["align=".Length..]);
                if (alignment <= 0 || (alignment & (alignment - 1)) != 0)
                {
                    throw new WatException(
                        $"Parse error: alignment must be a positive power of two, got {alignment}",
                        tok.Line,
                        tok.Column);
                }
                instr.IntValue = alignment;
            }
            else
            {
                break;
            }
        }
    }

    private void ParseBrTable(WatInstruction instr)
    {
        // br_table takes a list of label indices/names, the last one is the default
        instr.BrTableLabels = [];
        instr.BrTableIndices = [];

        while (Peek().Kind == WatTokenKind.Identifier || Peek().Kind == WatTokenKind.Integer)
        {
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                instr.BrTableLabels.Add(Consume().Value);
                instr.BrTableIndices.Add(-1); // resolved later
            }
            else
            {
                instr.BrTableLabels.Add("");
                instr.BrTableIndices.Add(ParseInt32());
            }
        }
    }

    private void ParseV128Const(WatInstruction instr)
    {
        // v128.const <shape> <lane literals...>
        //
        // Every shape writes exactly 16 bytes, little-endian, lane 0 first. Integer lanes accept
        // the full signed OR unsigned range for their width and are rejected outside it; float
        // lanes are parsed at their own width (never via double) so f32 NaN payloads and
        // round-to-nearest survive. See issue #419.
        var shapeTok = Expect(WatTokenKind.Keyword);
        var shape = shapeTok.Value;
        var bytes = new byte[16];

        switch (shape)
        {
            case "i8x16":
                for (int i = 0; i < 16; i++)
                    bytes[i] = (byte)ParseIntegerLaneBits(8);
                break;
            case "i16x8":
                for (int i = 0; i < 8; i++)
                    BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2), (ushort)ParseIntegerLaneBits(16));
                break;
            case "i32x4":
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)ParseIntegerLaneBits(32));
                break;
            case "i64x2":
                for (int i = 0; i < 2; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), ParseIntegerLaneBits(64));
                break;
            case "f32x4":
                for (int i = 0; i < 4; i++)
                    BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(i * 4), (uint)ParseFloatLaneBits(32));
                break;
            case "f64x2":
                for (int i = 0; i < 2; i++)
                    BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(i * 8), ParseFloatLaneBits(64));
                break;
            default:
                throw Error($"Unknown v128.const shape: {shape}");
        }

        instr.V128Bytes = bytes;
    }

    /// <summary>Consumes one integer lane literal and returns its <paramref name="bits"/>-wide two's-complement value.</summary>
    private ulong ParseIntegerLaneBits(int bits)
    {
        var tok = Expect(WatTokenKind.Integer);
        try
        {
            return ParseIntegerLiteralBits(tok.Value, bits);
        }
        catch (WatException ex)
        {
            throw Error(ex.Message);
        }
    }

    /// <summary>Consumes one float lane literal and returns its raw IEEE-754 bit pattern.</summary>
    private ulong ParseFloatLaneBits(int bits)
    {
        var tok = Consume();
        try
        {
            return ParseFloatLiteralBits(tok.Value, bits);
        }
        catch (WatException ex)
        {
            throw Error(ex.Message);
        }
    }

    private void ParseSimdShuffle(WatInstruction instr)
    {
        // i8x16.shuffle: 16 lane indices (each 0-31)
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++)
        {
            var tok = Expect(WatTokenKind.Integer);
            ulong v;
            try
            {
                v = ParseIntegerLiteralBits(tok.Value, 8);
            }
            catch (WatException ex)
            {
                throw Error(ex.Message);
            }
            if (v > 31)
                throw Error($"i8x16.shuffle lane index out of range (expected 0..31): {tok.Value}");
            bytes[i] = (byte)v;
        }
        instr.V128Bytes = bytes;
    }

    private WatMemory ParseMemory()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("memory");
        var mem = new WatMemory();

        if (Peek().Kind == WatTokenKind.Identifier)
            mem.Name = Consume().Value;

        ConsumeNameAnnotation("memory");
        // Inline exports
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); Consume();
            var name = ExpectUtf8String();
            mem.InlineExports.Add(new WatExport { Name = name, Kind = ExportKind.Memory });
            Expect(WatTokenKind.RParen);
        }

        SkipAnnotations();
        // min [max]
        if (Peek().Kind == WatTokenKind.Integer)
        {
            mem.Min = (uint)ParseInt32();
            if (Peek().Kind == WatTokenKind.Integer)
                mem.Max = (uint)ParseInt32();
        }

        Expect(WatTokenKind.RParen);
        mem.Span = SpanFrom(startToken);
        return mem;
    }

    private WatImport ParseImport()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("import");
        SkipAnnotations();
        var moduleName = ExpectUtf8String();
        SkipAnnotations();
        var fieldName = ExpectUtf8String();
        SkipAnnotations();
        Expect(WatTokenKind.LParen);
        var kindTok = Expect(WatTokenKind.Keyword);

        var import = new WatImport { ModuleName = moduleName, FieldName = fieldName };

        if (kindTok.Value == "func")
        {
            import.Kind = ImportKind.Func;
            if (Peek().Kind == WatTokenKind.Identifier)
                import.FuncName = Consume().Value;

            import.AnnotationName = ConsumeNameAnnotation("imported function");
            ParseOptionalImportTypeUse(import);
            // Params
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
            {
                Consume(); Consume();
                if (Peek().Kind == WatTokenKind.Identifier)
                {
                    var param = new WatParam { Name = Consume().Value };
                    param.AnnotationName = ConsumeNameAnnotation("local");
                    param.Type = ParseValType();
                    import.Params.Add(param);
                }
                else
                {
                    while (!IsRParen())
                    {
                        string? annotationName = ConsumeNameAnnotation("local");
                        if (IsRParen() && annotationName != null)
                            throw Error("@name annotation: type expected");
                        if (IsRParen())
                            break;
                        import.Params.Add(new WatParam
                        {
                            AnnotationName = annotationName,
                            Type = ParseValType(),
                        });
                    }
                }
                Expect(WatTokenKind.RParen);
                SkipAnnotations();
            }

            SkipAnnotations();
            // Results
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
            {
                Consume(); Consume();
                while (!IsRParen())
                    import.Results.Add(ParseValType());
                Expect(WatTokenKind.RParen);
            }
        }
        else if (kindTok.Value == "memory")
        {
            import.Kind = ImportKind.Memory;
            SkipAnnotations();
            import.MemMin = ParseInt32();
            if (!IsRParen())
                import.MemMax = ParseInt32();
        }
        else if (kindTok.Value == "global")
        {
            import.Kind = ImportKind.Global;
            // (global i32) or (global (mut i32))
            SkipAnnotations();
            if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "mut")
            {
                Consume(); Consume(); // ( mut
                import.GlobalType = ParseValType();
                import.GlobalMutable = true;
                Expect(WatTokenKind.RParen);
            }
            else
            {
                import.GlobalType = ParseValType();
                import.GlobalMutable = false;
            }
        }
        else if (kindTok.Value == "table")
        {
            import.Kind = ImportKind.Table;
            SkipAnnotations();
            if (Peek().Kind == WatTokenKind.Identifier) Consume(); // optional $id
            SkipAnnotations();
            import.TableMin = checked((uint)ParseInt32());
            if (Peek().Kind == WatTokenKind.Integer)
                import.TableMax = checked((uint)ParseInt32());
            import.TableElementType = ParseReferenceType("table import");
        }
        else if (kindTok.Value == "tag")
        {
            import.Kind = ImportKind.Tag;
            SkipAnnotations();
            if (Peek().Kind == WatTokenKind.Identifier)
                import.FuncName = Consume().Value; // optional tag name

            SkipAnnotations();
            ParseOptionalImportTypeUse(import);
            // (param ...) clauses define the tag's type
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
            {
                Consume(); Consume();
                while (!IsRParen())
                    import.Params.Add(new WatParam { Type = ParseValType() });
                Expect(WatTokenKind.RParen);
            }
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
            {
                Consume(); Consume();
                while (!IsRParen())
                    import.Results.Add(ParseValType());
                Expect(WatTokenKind.RParen);
            }
        }
        else
        {
            // Skip unsupported import kinds
            while (!IsRParen())
                Consume();
        }

        Expect(WatTokenKind.RParen); // close (func ...)
        Expect(WatTokenKind.RParen); // close (import ...)
        import.Span = SpanFrom(startToken);
        return import;
    }

    /// <summary>Parse (tag $name (param i32) ...)</summary>
    private WatTag ParseTag()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("tag");
        var tag = new WatTag();

        if (Peek().Kind == WatTokenKind.Identifier)
            tag.Name = Consume().Value;

        ConsumeNameAnnotation("tag");
        // (type $name) or (type N) reference
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "type")
        {
            Consume(); Consume();
            if (Peek().Kind == WatTokenKind.Identifier)
                tag.TypeName = Consume().Value;
            else if (Peek().Kind == WatTokenKind.Integer)
                tag.TypeIndex = ParseInt32();
            else
                throw Error("Expected type identifier or index in tag");
            Expect(WatTokenKind.RParen);
        }

        // (param ...) clauses
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
        {
            Consume(); Consume();
            while (!IsRParen())
            {
                string? annotationName = ConsumeNameAnnotation("local");
                if (IsRParen() && annotationName != null)
                    throw Error("@name annotation: type expected");
                if (IsRParen())
                    break;
                tag.Params.Add(ParseValType());
            }
            Expect(WatTokenKind.RParen);
            SkipAnnotations();
        }

        SkipAnnotations();
        // (result ...) clauses (tags shouldn't have results per spec, but parse to be safe)
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); Consume();
            while (!IsRParen())
                tag.Results.Add(ParseValType());
            Expect(WatTokenKind.RParen);
        }

        Expect(WatTokenKind.RParen);
        tag.Span = SpanFrom(startToken);
        return tag;
    }

    private WatGlobal ParseGlobal()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("global");
        var global = new WatGlobal();

        if (Peek().Kind == WatTokenKind.Identifier)
            global.Name = Consume().Value;

        ConsumeNameAnnotation("global");
        // Inline exports
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); Consume();
            var name = ExpectUtf8String();
            global.InlineExports.Add(new WatExport { Name = name, Kind = ExportKind.Global });
            Expect(WatTokenKind.RParen);
        }

        SkipAnnotations();
        // Type: either (mut i32) or i32
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "mut")
        {
            Consume(); Consume();
            global.Mutable = true;
            global.Type = ParseValType();
            Expect(WatTokenKind.RParen);
        }
        else
        {
            global.Type = ParseValType();
        }

        SkipAnnotations();
        // Init expression: (i32.const N) etc.
        if (Peek().Kind == WatTokenKind.LParen)
            global.Init = ParseFoldedInstruction();

        Expect(WatTokenKind.RParen);
        global.Span = SpanFrom(startToken);
        return global;
    }

    private WatData ParseData()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("data");
        var data = new WatData();

        if (Peek().Kind == WatTokenKind.Identifier)
            Consume();
        ConsumeNameAnnotation("data segment");
        // Offset expression: (i32.const N)
        if (Peek().Kind == WatTokenKind.LParen)
            data.Offset = ParseFoldedInstruction();

        // One or more string literals concatenated
        var bytes = new List<byte>();
        while (Peek().Kind == WatTokenKind.String)
        {
            var token = Consume();
            bytes.AddRange(GetStringBytes(token));
        }
        data.Bytes = bytes.ToArray();

        Expect(WatTokenKind.RParen);
        data.Span = SpanFrom(startToken);
        return data;
    }

    /// <summary>
    /// Parses a table definition: <c>(table id? limits reftype constexpr?)</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The trailing constant expression is the table's initializer — every slot is allocated holding
    /// its value (<c>alloctable(tabletype, ref)</c>) before the element segments run. The spec corpus
    /// spells it <c>(table $t2 10 funcref (ref.func $dummy))</c> (table.wast:100) and
    /// <c>(table 0 funcref (ref.null func))</c> (table.wast:19); wasm-tools 1.253.0's printer emits
    /// the unfolded abbreviation <c>(table $t 3 funcref ref.func $f)</c>, so both are accepted here.
    /// </para>
    /// <para>
    /// The element type used to be consumed and thrown away, with the encoder hard-coding
    /// <c>funcref</c>. It has to be retained now: the extended encoding carries the reftype ahead of
    /// the limits, so an <c>externref</c> table with an initializer would otherwise be emitted with a
    /// <c>funcref</c> element type and fail validation against its own initializer.
    /// </para>
    /// <para>
    /// It is also <b>required</b>, and required to be a reference type. <c>tabletype ::= limits
    /// reftype</c> has no optional part, so <c>(table 1)</c> is not a table with a defaulted element
    /// type — it is not a table. Defaulting it to <c>funcref</c> silently accepted text
    /// <c>wasm-tools parse</c> rejects, and once the element type reached the encoder unchecked,
    /// <c>(table 1 i32)</c> emitted <c>0x7F</c> as a reference type (GitHub #383).
    /// </para>
    /// <para>
    /// <c>anyfunc</c> is <b>not</b> accepted. It is the pre-2.0 spelling of <c>funcref</c>, is not in
    /// the current grammar, and <c>wasm-tools 1.253.0</c> rejects it — so admitting it produced text
    /// that compiles in nesm and in no other tool. That is the over-acceptance failure mode: it never
    /// fails here, only later, in someone else's toolchain.
    /// </para>
    /// </remarks>
    private WatTable ParseTable()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("table");
        var table = new WatTable();

        // (table N funcref) or (table N N funcref) or (table $id N funcref)
        if (Peek().Kind == WatTokenKind.Identifier) Consume(); // optional $id
        ConsumeNameAnnotation("table");

        // `(table funcref (elem ...))` is the inline-element abbreviation, which nesm does not
        // implement. Named here so it fails with its own name rather than as "expected Integer".
        if (Peek().Kind == WatTokenKind.Keyword)
            throw Error("Inline table element segments — (table reftype (elem ...)) — are not supported; write the table and (elem ...) separately");

        table.Min = (uint)ParseInt32();
        if (Peek().Kind == WatTokenKind.Integer)
        {
            table.Max = (uint)ParseInt32();
        }

        if (Peek().Kind is not (WatTokenKind.Keyword or WatTokenKind.LParen))
            throw Error("Table declaration is missing its element type; the grammar is (table id? limits reftype constexpr?)");

        table.ElementType = ParseReferenceType("table");

        // Initializer constant expression, folded `(ref.func $f)` or unfolded `ref.func $f`.
        if (Peek().Kind is WatTokenKind.LParen or WatTokenKind.Keyword)
            table.Init = ParseInstruction();

        Expect(WatTokenKind.RParen);
        table.Span = SpanFrom(startToken);
        return table;
    }

    private WatElem ParseElem()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("elem");
        var elem = new WatElem();

        if (Peek().Kind == WatTokenKind.Identifier)
            Consume();
        ConsumeNameAnnotation("element segment");
        // Declarative segment: (elem declare ...)
        if (Peek().Kind == WatTokenKind.Keyword && Peek().Value == "declare")
        {
            elem.Mode = WatElemMode.Declarative;
            Consume();
        }

        // Optional (table N) for non-zero table index
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "table")
        {
            Consume(); // (
            Consume(); // table
            elem.HasExplicitTable = true;
            elem.TableIndex = ParseInt32();
            Expect(WatTokenKind.RParen);
        }

        // Any non-declarative segment with an offset is active. Without one it is passive.
        if (elem.Mode != WatElemMode.Declarative &&
            Peek().Kind == WatTokenKind.LParen &&
            PeekAt(1).Value != "ref")
        {
            elem.Offset = ParseFoldedInstruction();
            elem.Mode = WatElemMode.Active;
        }
        else if (elem.Mode != WatElemMode.Declarative)
        {
            if (elem.HasExplicitTable)
                throw Error("An active element segment with an explicit table requires an offset expression");
            elem.Mode = WatElemMode.Passive;
            elem.TableIndex = -1;
        }

        // `func vec(funcidx)` is the flags 0–3 form.
        bool functionIndexForm =
            (Peek().Kind == WatTokenKind.Keyword && Peek().Value == "func") ||
            Peek().Kind is WatTokenKind.Integer or WatTokenKind.Identifier or WatTokenKind.RParen;
        if (functionIndexForm)
        {
            if (Peek().Kind == WatTokenKind.Keyword)
                Consume();
            while (Peek().Kind is WatTokenKind.Integer or WatTokenKind.Identifier)
            {
                if (Peek().Kind == WatTokenKind.Identifier)
                {
                    elem.FuncIndices.Add(-1);
                    elem.FuncNames.Add(Consume().Value);
                }
                else
                {
                    elem.FuncIndices.Add(ParseInt32());
                    elem.FuncNames.Add(null);
                }
            }
        }
        else
        {
            // `reftype vec(expr)` is the flags 4–7 form. The marker is independent of vector
            // length, so an empty vector must still take this branch.
            elem.UsesExpressions = true;
            elem.ElementType = ParseReferenceType("element segment");
            while (Peek().Kind == WatTokenKind.LParen)
            {
                if (PeekAt(1).Value == "item")
                {
                    Consume();
                    Consume();
                    var instructions = new List<WatInstruction>();
                    ParseBodyItems(instructions);
                    Expect(WatTokenKind.RParen);
                    elem.ElementExpressions.Add(instructions);
                }
                else
                {
                    elem.ElementExpressions.Add([ParseFoldedInstruction()]);
                }
            }
        }

        Expect(WatTokenKind.RParen);
        elem.Span = SpanFrom(startToken);
        return elem;
    }

    private WatExport ParseExport()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("export");
        SkipAnnotations();
        var exportName = ExpectUtf8String();
        var export = new WatExport { Name = exportName };

        SkipAnnotations();
        Expect(WatTokenKind.LParen);
        var kindTok = Expect(WatTokenKind.Keyword);
        export.Kind = kindTok.Value switch
        {
            "func" => ExportKind.Func,
            "memory" => ExportKind.Memory,
            "table" => ExportKind.Table,
            "global" => ExportKind.Global,
            _ => throw Error($"Unknown export kind: {kindTok.Value}"),
        };

        if (Peek().Kind == WatTokenKind.Identifier)
            export.Ref = Consume().Value;
        else if (Peek().Kind == WatTokenKind.Integer)
            export.Index = ParseInt32();

        Expect(WatTokenKind.RParen);
        Expect(WatTokenKind.RParen);
        export.Span = SpanFrom(startToken);
        return export;
    }

    private WatStart ParseStart()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("start");
        var start = new WatStart();
        if (Peek().Kind == WatTokenKind.Identifier)
            start.Ref = Consume().Value;
        else if (Peek().Kind == WatTokenKind.Integer)
            start.Index = ParseInt32();
        else
            throw Error("Expected function identifier or index in start declaration");
        Expect(WatTokenKind.RParen);
        start.Span = SpanFrom(startToken);
        return start;
    }

    private ValType ParseValType()
    {
        var tok = Expect(WatTokenKind.Keyword);
        return tok.Value switch
        {
            "i32" => ValType.I32,
            "i64" => ValType.I64,
            "f32" => ValType.F32,
            "f64" => ValType.F64,
            "v128" => ValType.V128,
            "funcref" => ValType.FuncRef,
            "externref" => ValType.ExternRef,
            "exnref" => ValType.ExnRef,
            "contref" => ValType.ContRef,
            _ => throw Error($"Unknown value type: {tok.Value}"),
        };
    }

    private WatReferenceType ParseReferenceType(string context)
    {
        if (Peek().Kind == WatTokenKind.LParen)
        {
            Consume();
            ExpectKeyword("ref");
            bool nullable = Peek().Kind == WatTokenKind.Keyword && Peek().Value == "null";
            if (nullable)
                Consume();
            var (heapType, typeName) = ParseHeapType(context);
            Expect(WatTokenKind.RParen);
            return new WatReferenceType(
                nullable ? Nullability.Nullable : Nullability.NonNullable,
                heapType,
                typeName);
        }

        var token = Expect(WatTokenKind.Keyword);
        if (token.Value == "anyfunc")
        {
            throw Error("'anyfunc' is the pre-2.0 spelling of 'funcref' and is not in the " +
                "current grammar; wasm-tools 1.253.0 rejects it. Write 'funcref'.");
        }

        if (!TryParseReferenceTypeAbbreviation(token.Value, out HeapTypeKind kind))
            throw Error($"{context} element type must be a reference type, got '{token.Value}'");
        return new WatReferenceType(Nullability.Nullable, new AbstractHeapType(kind));
    }

    private (HeapType? HeapType, string? TypeName) ParseHeapType(string context)
    {
        if (Peek().Kind == WatTokenKind.Identifier)
            return (null, Consume().Value);

        if (Peek().Kind == WatTokenKind.Integer)
        {
            int typeIndex = ParseInt32();
            if (typeIndex < 0)
                throw Error($"{context} defined heap type index must be non-negative, got {typeIndex}");
            return (new DefinedHeapType(typeIndex), null);
        }

        var token = Expect(WatTokenKind.Keyword);
        if (TryParseHeapTypeKeyword(token.Value, out HeapTypeKind kind))
            return (new AbstractHeapType(kind), null);

        if (TryParseReferenceTypeAbbreviation(token.Value, out _))
        {
            throw Error(
                $"'{token.Value}' is a reference type; {context} requires a heap type such as " +
                $"'{ReferenceTypeToHeapTypeExample(token.Value)}'.");
        }

        throw Error($"Unknown heap type: {token.Value}");
    }

    private static bool TryParseHeapTypeKeyword(string text, out HeapTypeKind kind)
    {
        switch (text)
        {
            case "func": kind = HeapTypeKind.Func; return true;
            case "extern": kind = HeapTypeKind.Extern; return true;
            case "any": kind = HeapTypeKind.Any; return true;
            case "eq": kind = HeapTypeKind.Eq; return true;
            case "i31": kind = HeapTypeKind.I31; return true;
            case "struct": kind = HeapTypeKind.Struct; return true;
            case "array": kind = HeapTypeKind.Array; return true;
            case "none": kind = HeapTypeKind.None; return true;
            case "nofunc": kind = HeapTypeKind.NoFunc; return true;
            case "noextern": kind = HeapTypeKind.NoExtern; return true;
            case "exn": kind = HeapTypeKind.Exn; return true;
            case "noexn": kind = HeapTypeKind.NoExn; return true;
            case "cont": kind = HeapTypeKind.Cont; return true;
            case "nocont": kind = HeapTypeKind.NoCont; return true;
            default: kind = default; return false;
        }
    }

    private static bool TryParseReferenceTypeAbbreviation(string text, out HeapTypeKind kind)
    {
        switch (text)
        {
            case "funcref": kind = HeapTypeKind.Func; return true;
            case "externref": kind = HeapTypeKind.Extern; return true;
            case "anyref": kind = HeapTypeKind.Any; return true;
            case "eqref": kind = HeapTypeKind.Eq; return true;
            case "i31ref": kind = HeapTypeKind.I31; return true;
            case "structref": kind = HeapTypeKind.Struct; return true;
            case "arrayref": kind = HeapTypeKind.Array; return true;
            case "nullref": kind = HeapTypeKind.None; return true;
            case "nullfuncref": kind = HeapTypeKind.NoFunc; return true;
            case "nullexternref": kind = HeapTypeKind.NoExtern; return true;
            case "exnref": kind = HeapTypeKind.Exn; return true;
            case "nullexnref": kind = HeapTypeKind.NoExn; return true;
            case "contref": kind = HeapTypeKind.Cont; return true;
            case "nullcontref": kind = HeapTypeKind.NoCont; return true;
            default: kind = default; return false;
        }
    }

    private static string ReferenceTypeToHeapTypeExample(string referenceType) => referenceType switch
    {
        "funcref" => "func",
        "externref" => "extern",
        "anyref" => "any",
        "eqref" => "eq",
        "i31ref" => "i31",
        "structref" => "struct",
        "arrayref" => "array",
        "nullref" => "none",
        "nullfuncref" => "nofunc",
        "nullexternref" => "noextern",
        "exnref" => "exn",
        "nullexnref" => "noexn",
        "contref" => "cont",
        "nullcontref" => "nocont",
        _ => referenceType,
    };

    private static long SignedHeapType(HeapTypeKind kind)
    {
        if (!AbstractHeapTypeCodes.TryToCode(kind, out byte code))
            throw new WatException($"No binary heap-type code exists for {kind}.");
        return code - 0x80;
    }

    private int ParseInt32()
    {
        var tok = Expect(WatTokenKind.Integer);
        return ParseIntFromToken(tok);
    }

    private long ParseInt64()
    {
        var tok = Expect(WatTokenKind.Integer);
        return ParseLongFromToken(tok);
    }

    private static bool IsFloatKeyword(string value) =>
        value is "nan" or "inf" or "-inf" or "+inf" or "-nan" or "+nan" ||
        value.StartsWith("nan:") || value.StartsWith("-nan:") || value.StartsWith("+nan:");

    private double ParseFloat64()
    {
        var tok = Consume();
        try
        {
            return BitConverter.UInt64BitsToDouble(ParseFloatLiteralBits(tok.Value, 64));
        }
        catch (WatException ex)
        {
            throw Error(ex.Message);
        }
    }

    // ── Numeric literal grammar (shared by scalar consts, v128 lanes and shuffle masks) ──

    /// <summary>
    /// Parses a WAT integer literal into its <paramref name="bits"/>-wide two's-complement bit
    /// pattern. Accepts decimal and <c>0x</c> hexadecimal, an optional leading sign, and <c>_</c>
    /// digit separators. A value outside both the signed and unsigned range for the width is
    /// rejected rather than silently truncated.
    /// </summary>
    internal static ulong ParseIntegerLiteralBits(string literal, int bits)
    {
        var text = literal.Replace("_", "");
        if (text.Length == 0)
            throw new WatException("Empty integer literal");

        bool negative = false;
        if (text[0] is '+' or '-')
        {
            negative = text[0] == '-';
            text = text[1..];
        }

        ulong magnitude;
        try
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            {
                var hex = text[2..];
                if (hex.Length == 0)
                    throw new WatException($"Malformed hexadecimal literal: {literal}");
                magnitude = Convert.ToUInt64(hex, 16);
            }
            else
            {
                magnitude = ulong.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException)
        {
            throw new WatException($"Invalid i{bits} literal: {literal}");
        }

        ulong widthMask = bits == 64 ? ulong.MaxValue : (1uL << bits) - 1;
        ulong signedMax = bits == 64 ? long.MaxValue : (1uL << (bits - 1)) - 1;

        if (negative)
        {
            // Negative literals must fit the signed range: -2^(bits-1) .. -1.
            if (magnitude > signedMax + 1)
                throw new WatException($"Invalid i{bits} literal: constant out of range: {literal}");
            return (0uL - magnitude) & widthMask;
        }

        // Non-negative literals may use the full unsigned range (u32/u64 spellings are legal WAT).
        if (magnitude > widthMask)
            throw new WatException($"Invalid i{bits} literal: constant out of range: {literal}");
        return magnitude;
    }

    /// <summary>
    /// Parses a WAT floating-point literal into its raw IEEE-754 bit pattern at the requested
    /// width (32 or 64). Handles <c>inf</c>, <c>nan</c>, <c>nan:0x…</c> payloads, hexadecimal
    /// floats and decimal floats, each with an optional sign. Parsing at the target width matters:
    /// routing an f32 literal through a double loses NaN payloads and can double-round.
    /// </summary>
    internal static ulong ParseFloatLiteralBits(string literal, int bits)
    {
        int mantBits = bits == 32 ? 23 : 52;
        int expBits = bits == 32 ? 8 : 11;
        ulong expMask = ((1uL << expBits) - 1) << mantBits;
        ulong mantMask = (1uL << mantBits) - 1;

        var text = literal.Replace("_", "");
        if (text.Length == 0)
            throw new WatException("Empty float literal");

        ulong sign = 0;
        if (text[0] is '+' or '-')
        {
            if (text[0] == '-')
                sign = 1uL << (bits - 1);
            text = text[1..];
        }

        if (text == "inf")
            return sign | expMask;
        if (text == "nan")
            return sign | expMask | (1uL << (mantBits - 1)); // canonical quiet NaN
        if (text.StartsWith("nan:0x", StringComparison.OrdinalIgnoreCase))
        {
            var hex = text[6..];
            ulong payload;
            try
            {
                payload = Convert.ToUInt64(hex, 16);
            }
            catch (Exception ex) when (ex is OverflowException or FormatException or ArgumentException)
            {
                throw new WatException($"Invalid NaN payload: {literal}");
            }
            if (payload == 0 || payload > mantMask)
                throw new WatException($"NaN payload out of range for f{bits}: {literal}");
            return sign | expMask | payload;
        }

        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return sign | ParseHexFloatMagnitudeBits(text[2..], literal, bits);

        try
        {
            // Parse at the target width: float.Parse is correctly rounded for f32, so this avoids
            // the double-rounding a (float)double.Parse(...) narrowing would introduce.
            ulong magnitude = bits == 32
                ? BitConverter.SingleToUInt32Bits(float.Parse(text, System.Globalization.CultureInfo.InvariantCulture))
                : BitConverter.DoubleToUInt64Bits(double.Parse(text, System.Globalization.CultureInfo.InvariantCulture));
            return sign | magnitude;
        }
        catch (FormatException)
        {
            throw new WatException($"Invalid f{bits} literal: {literal}");
        }
    }

    /// <summary>
    /// Converts the unsigned magnitude of a hexadecimal float (the text after <c>0x</c>) to its
    /// IEEE-754 bit pattern, rounding to nearest-even. .NET cannot parse hex floats, and the WAT
    /// spec relies on them for exact bit patterns, so the conversion is done here.
    /// </summary>
    private static ulong ParseHexFloatMagnitudeBits(string body, string literal, int bits)
    {
        int mantBits = bits == 32 ? 23 : 52;
        int expBits = bits == 32 ? 8 : 11;
        int maxBiasedExp = (1 << expBits) - 1;
        int bias = (1 << (expBits - 1)) - 1;
        ulong infinity = ((ulong)maxBiasedExp) << mantBits;

        int pIdx = body.IndexOfAny(['p', 'P']);
        string mantissaText = pIdx >= 0 ? body[..pIdx] : body;
        int binExp = 0;
        if (pIdx >= 0)
        {
            var expText = body[(pIdx + 1)..];
            if (expText.StartsWith('+'))
                expText = expText[1..];
            if (!int.TryParse(expText, System.Globalization.NumberStyles.AllowLeadingSign,
                    System.Globalization.CultureInfo.InvariantCulture, out binExp))
                throw new WatException($"Invalid hexadecimal float exponent: {literal}");
        }

        int dot = mantissaText.IndexOf('.');
        string digits;
        if (dot >= 0)
        {
            digits = string.Concat(mantissaText.AsSpan(0, dot), mantissaText.AsSpan(dot + 1));
            binExp -= 4 * (mantissaText.Length - dot - 1);
        }
        else
        {
            digits = mantissaText;
        }

        if (digits.Length == 0)
            throw new WatException($"Invalid hexadecimal float literal: {literal}");

        int first = 0;
        while (first < digits.Length && digits[first] == '0')
            first++;

        UInt128 sig = UInt128.Zero;
        bool sticky = false;
        for (int i = first; i < digits.Length; i++)
        {
            int d = HexDigitValue(digits[i]);
            if (d < 0)
                throw new WatException($"Invalid hexadecimal float literal: {literal}");
            if (i - first < 30)
            {
                sig = (sig << 4) | (UInt128)(uint)d;
            }
            else
            {
                // Beyond the precision we can hold: the dropped digits only contribute a sticky bit.
                binExp += 4;
                sticky |= d != 0;
            }
        }

        if (sig == UInt128.Zero)
            return 0; // ±0 (sign applied by the caller)

        int msb = 127;
        while ((sig >> msb) == UInt128.Zero)
            msb--;

        int shift = msb - mantBits;
        int biasedExp = binExp + msb + bias;
        if (biasedExp <= 0)
        {
            shift += 1 - biasedExp;
            biasedExp = 0;
        }

        ulong mant;
        if (shift > 0)
        {
            if (shift > 127)
                return 0; // underflows past the smallest subnormal
            UInt128 dropped = sig & ((UInt128.One << shift) - 1);
            mant = (ulong)(sig >> shift);
            UInt128 half = UInt128.One << (shift - 1);
            bool roundBit = (dropped & half) != UInt128.Zero;
            bool restNonZero = (dropped & (half - 1)) != UInt128.Zero || sticky;
            if (roundBit && (restNonZero || (mant & 1) != 0))
                mant++;
        }
        else
        {
            mant = (ulong)(sig << -shift);
        }

        ulong mantMask = (1uL << mantBits) - 1;
        if (biasedExp == 0)
        {
            // Rounding may have carried a subnormal up into the smallest normal.
            if (mant > mantMask)
                biasedExp = 1;
        }
        else if (mant > (mantMask << 1) + 1)
        {
            mant >>= 1;
            biasedExp++;
        }

        if (biasedExp >= maxBiasedExp)
            return infinity;

        return ((ulong)biasedExp << mantBits) | (mant & mantMask);
    }

    private static int HexDigitValue(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'a' and <= 'f' => c - 'a' + 10,
        >= 'A' and <= 'F' => c - 'A' + 10,
        _ => -1,
    };

    internal static int ParseIntFromToken(WatToken tok) =>
        unchecked((int)ParseIntegerLiteralBits(tok.Value, 32));

    internal static long ParseLongFromToken(WatToken tok) =>
        unchecked((long)ParseIntegerLiteralBits(tok.Value, 64));

    // ── Helpers ──

    private WatToken Peek() => _pos < _tokens.Count ? _tokens[_pos] : new WatToken(WatTokenKind.EOF, "", 0, 0);
    private WatToken PeekAt(int offset)
    {
        int idx = _pos + offset;
        return idx < _tokens.Count ? _tokens[idx] : new WatToken(WatTokenKind.EOF, "", 0, 0);
    }

    private WatToken _lastToken;

    private WatToken Consume()
    {
        var tok = _tokens[_pos++];
        _lastToken = tok;
        return tok;
    }

    private SourceSpan SpanFrom(WatToken start) =>
        new(start.Line, start.Column, _lastToken.Line, _lastToken.Column + _lastToken.Value.Length);

    private bool IsRParen() => Peek().Kind == WatTokenKind.RParen || Peek().Kind == WatTokenKind.EOF;

    private WatToken Expect(WatTokenKind kind)
    {
        var tok = Peek();
        if (tok.Kind != kind)
            throw Error($"Expected {kind}, got {tok.Kind}({tok.Value}) at line {tok.Line}:{tok.Column}");
        return Consume();
    }

    private static readonly System.Text.UTF8Encoding s_utf8Strict =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private static byte[] GetStringBytes(WatToken token) =>
        token.StringBytes ?? System.Text.Encoding.UTF8.GetBytes(token.Value);

    private string ExpectUtf8String()
    {
        var token = Expect(WatTokenKind.String);
        try
        {
            return s_utf8Strict.GetString(GetStringBytes(token));
        }
        catch (System.Text.DecoderFallbackException ex)
        {
            throw new WatException(
                $"Parse error: name is not valid UTF-8: {ex.Message}",
                token.Line,
                token.Column);
        }
    }

    private WatToken ExpectKeyword(string value)
    {
        var tok = Expect(WatTokenKind.Keyword);
        if (tok.Value != value)
            throw Error($"Expected keyword '{value}', got '{tok.Value}'");
        return tok;
    }

    private bool IsAnnotationHeader()
    {
        if (Peek().Kind != WatTokenKind.LParen) return false;
        if (_pos + 1 >= _tokens.Count) return false;
        var at = PeekAt(1);
        if (at.Kind != WatTokenKind.Keyword) return false;
        // Adjacency: ( must be immediately followed by @ with no whitespace
        if (Peek().Line != at.Line || Peek().Column + 1 != at.Column) return false;

        // Standard form: @name, @foo, etc. — keyword starting with @ of length > 1
        if (at.Value.Length > 1 && at.Value[0] == '@') return true;

        // Quoted form: (@"str" ...) — lexer yields Keyword("@") then String("str")
        // The @ and " must also be adjacent (no space between them).
        if (at.Value == "@" && _pos + 2 < _tokens.Count)
        {
            var str = PeekAt(2);
            if (str.Kind == WatTokenKind.String
                && str.Value.Length > 0      // non-empty quoted annotation id
                && at.Line == str.Line
                && at.Column + 1 == str.Column)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Consumes all pending annotations, capturing the @name value if present.
    /// Raises a parse error if @name appears more than once.
    /// <paramref name="context"/> is used in the error message (e.g. "module", "function", "local").
    /// </summary>
    private string? ConsumeNameAnnotation(string context)
    {
        string? name = null;
        while (IsAnnotationHeader())
        {
            if (IsBranchHintAnnotation())
                break;

            // Standard form: (@name "x")
            bool isNameStd = PeekAt(1).Value == "@name";
            // Quoted form: (@"name" "x") — lexer yields Keyword("@") then String("name")
            bool isNameQuoted = PeekAt(1).Value == "@"
                && _pos + 2 < _tokens.Count
                && PeekAt(2).Kind == WatTokenKind.String
                && PeekAt(2).Value == "name";

            if (isNameStd || isNameQuoted)
            {
                if (name != null)
                    throw Error($"@name annotation: multiple {context} names");
                Consume(); // (
                if (isNameQuoted)
                {
                    Consume(); // @
                    Consume(); // "name" (the annotation id string)
                }
                else
                {
                    Consume(); // @name
                }
                if (Peek().Kind != WatTokenKind.String)
                    throw Error("@name annotation: string expected");
                name = ExpectUtf8String();
                if (!IsRParen())
                    throw Error("@name annotation: unexpected token");
                Expect(WatTokenKind.RParen);
            }
            else
            {
                SkipSExpr();
            }
        }
        return name;
    }

    private void SkipNonBranchAnnotations()
    {
        while (IsAnnotationHeader() && !IsBranchHintAnnotation())
        {
            if (IsNameAnnotation())
                throw Error("@name annotation: misplaced");
            SkipSExpr();
        }
    }

    private void SkipAnnotations()
    {
        while (IsAnnotationHeader())
        {
            if (IsNameAnnotation())
                throw Error("@name annotation: misplaced");
            if (IsBranchHintAnnotation())
                throw Error("@metadata.code.branch_hint annotation: invalid placement");
            SkipSExpr();
        }
    }

    /// <summary>
    /// True when the lookahead is an @name annotation (standard or quoted form).
    /// Caller must have already established IsAnnotationHeader() before calling.
    /// </summary>
    private bool IsNameAnnotation()
    {
        // Standard form: (@name ...)
        if (PeekAt(1).Value == "@name") return true;
        // Quoted form: (@"name" ...) — lexer yields Keyword("@") then String("name")
        if (PeekAt(1).Value == "@" && _pos + 2 < _tokens.Count
            && PeekAt(2).Kind == WatTokenKind.String && PeekAt(2).Value == "name")
            return true;
        return false;
    }

    private bool IsBranchHintAnnotation() =>
        PeekAt(1).Value == "@metadata.code.branch_hint"
        || (PeekAt(1).Value == "@"
            && _pos + 2 < _tokens.Count
            && PeekAt(2).Kind == WatTokenKind.String
            && PeekAt(2).Value == "metadata.code.branch_hint");

    /// <summary>
    /// Skip one annotation in a body/instruction context.
    /// Raises a parse error if the annotation is @name (standard or quoted),
    /// because @name attaches to binders, not instructions.
    /// </summary>
    private void SkipBodyAnnotation()
    {
        if (IsNameAnnotation())
            throw Error("@name annotation: misplaced in function body");
        if (IsBranchHintAnnotation())
            throw Error("@metadata.code.branch_hint annotation: invalid placement");
        SkipSExpr();
    }

    private BranchHintKind? ConsumeBodyAnnotations()
    {
        BranchHintKind? branchHint = null;
        while (IsAnnotationHeader())
        {
            if (!IsBranchHintAnnotation())
            {
                SkipBodyAnnotation();
                continue;
            }

            if (branchHint.HasValue)
                throw Error("@metadata.code.branch_hint annotation: duplicate annotation");

            Consume(); // (
            if (Peek().Value == "@")
            {
                Consume(); // @
                Consume(); // "metadata.code.branch_hint"
            }
            else
            {
                Consume(); // @metadata.code.branch_hint
            }

            if (Peek().Kind != WatTokenKind.String)
                throw Error("@metadata.code.branch_hint annotation: byte string expected");

            WatToken payload = Consume();
            byte[] bytes = GetStringBytes(payload);
            if (bytes.Length != 1)
                throw Error("@metadata.code.branch_hint annotation: payload size must be 1");
            if (bytes[0] > 1)
                throw Error("@metadata.code.branch_hint annotation: value must be 0 or 1");
            if (!IsRParen())
                throw Error("@metadata.code.branch_hint annotation: unexpected token");
            Expect(WatTokenKind.RParen);
            branchHint = (BranchHintKind)bytes[0];
        }
        return branchHint;
    }

    private WatInstruction ParseInstructionWithBranchHint(BranchHintKind? branchHint)
    {
        if (branchHint.HasValue &&
            Peek().Kind is not WatTokenKind.LParen and not WatTokenKind.Keyword)
            throw Error("@metadata.code.branch_hint annotation: no following instruction");

        WatInstruction instruction = ParseInstruction();
        if (branchHint.HasValue)
        {
            if (instruction.Mnemonic is not "if" and not "br_if")
                throw Error("@metadata.code.branch_hint annotation: invalid target");
            instruction.BranchHint = branchHint;
        }
        return instruction;
    }

    private void RequireFollowingInstruction(BranchHintKind? branchHint)
    {
        if (branchHint.HasValue)
            throw Error("@metadata.code.branch_hint annotation: no following instruction");
    }

    /// <summary>
    /// Parse body instructions. Unknown annotations retain their zero-semantic-effect behavior;
    /// branch-hint annotations attach to the next instruction.
    /// </summary>
    private void ParseBodyItems(List<WatInstruction> body)
    {
        while (!IsRParen())
        {
            BranchHintKind? branchHint = ConsumeBodyAnnotations();
            if (IsRParen())
            {
                RequireFollowingInstruction(branchHint);
                break;
            }
            body.Add(ParseInstructionWithBranchHint(branchHint));
        }
    }

    private void SkipSExpr()
    {
        if (IsAnnotationHeader() && IsNameAnnotation())
            throw Error("@name annotation: misplaced");
        if (Peek().Kind != WatTokenKind.LParen)
        {
            Consume();
            return;
        }
        Consume(); // (
        int depth = 1;
        while (depth > 0 && Peek().Kind != WatTokenKind.EOF)
        {
            if (IsAnnotationHeader() && IsNameAnnotation())
                throw Error("@name annotation: misplaced");
            if (Peek().Kind == WatTokenKind.LParen) depth++;
            else if (Peek().Kind == WatTokenKind.RParen) depth--;
            Consume();
        }
    }

    private void SkipToRParen()
    {
        int depth = 1;
        while (depth > 0 && Peek().Kind != WatTokenKind.EOF)
        {
            if (IsAnnotationHeader() && IsNameAnnotation())
                throw Error("@name annotation: misplaced");
            if (Peek().Kind == WatTokenKind.LParen) depth++;
            else if (Peek().Kind == WatTokenKind.RParen) depth--;
            if (depth > 0) Consume();
        }
        if (Peek().Kind == WatTokenKind.RParen)
            Consume();
    }

    private WatException Error(string message)
    {
        var tok = Peek();
        return new WatException($"Parse error: {message}", tok.Line, tok.Column);
    }

    /// <summary>
    /// In analyze mode, calls <paramref name="parse"/>, catches any <see cref="WatException"/>,
    /// records a diagnostic, resets position to <paramref name="savedPos"/>, and skips the
    /// malformed s-expression.  In normal mode simply delegates.
    /// </summary>
    private T? TryParseTopLevel<T>(Func<T> parse, int savedPos) where T : class
    {
        if (_diagnostics == null)
            return parse();
        try
        {
            return parse();
        }
        catch (WatException ex)
        {
            _diagnostics.Add(new WatDiagnostic(ex.Line, ex.Column, ex.Message));
            _pos = savedPos;
            SkipSExpr();
            return null;
        }
    }
}
