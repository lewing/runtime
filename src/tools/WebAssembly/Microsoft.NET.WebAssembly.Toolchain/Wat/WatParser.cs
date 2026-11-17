// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

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
    /// <summary>Tracks the order of top-level declarations that may produce exports.</summary>
    public List<(DeclKind Kind, int Index)> DeclOrder { get; } = [];
    public List<WatTag> Tags { get; } = [];
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
    public List<WatParam> Params { get; } = [];
    public List<ValType> Results { get; } = [];
    // For memory imports
    public int MemMin { get; set; }
    public int? MemMax { get; set; }
    // For global imports
    public ValType GlobalType { get; set; }
    public bool GlobalMutable { get; set; }
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
}

public sealed class WatElem
{
    public SourceSpan Span { get; set; }
    public int TableIndex { get; set; }
    public WatInstruction? Offset { get; set; }
    public List<int> FuncIndices { get; } = [];
}

public sealed class WatTag
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
    public List<ValType> Params { get; } = [];
    public List<ValType> Results { get; } = [];
    public int? TypeIndex { get; set; }
}

public sealed class WatFunc
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
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
    public ValType Type { get; set; }
}

public sealed class WatLocal
{
    public SourceSpan Span { get; set; }
    public string? Name { get; set; }
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
        _tokens = tokens;
        _diagnostics = diagnostics;
    }

    public WatModule ParseModule()
    {
        var startToken = Peek();
        var module = new WatModule();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("module");

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
                            { var f = TryParseTopLevel(ParseFunc, savedPos); if (f != null) { module.DeclOrder.Add((DeclKind.Func, module.Funcs.Count)); module.Funcs.Add(f); } }
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
                    }
                }
                _pos = savedPos;
                SkipSExpr();
            }
            else
            {
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

        // (func (param ...) (result ...))
        Expect(WatTokenKind.LParen);
        ExpectKeyword("func");

        while (!IsRParen())
        {
            if (Peek().Kind == WatTokenKind.LParen)
            {
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

        // Optional name
        if (Peek().Kind == WatTokenKind.Identifier)
            func.Name = Consume().Value;

        // Inline exports: (export "name")
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); // (
            Consume(); // export
            var name = Expect(WatTokenKind.String);
            func.InlineExports.Add(new WatExport { Name = name.Value, Kind = ExportKind.Func });
            Expect(WatTokenKind.RParen);
        }

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

        // Params
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
        {
            var paramGroupStart = Peek();
            Consume(); // (
            Consume(); // param
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                var p = new WatParam { Name = Consume().Value, Type = ParseValType() };
                Expect(WatTokenKind.RParen);
                p.Span = SpanFrom(paramGroupStart);
                func.Params.Add(p);
                continue;
            }
            while (!IsRParen())
            {
                var typeStart = Peek();
                var p = new WatParam { Type = ParseValType() };
                p.Span = SpanFrom(typeStart);
                func.Params.Add(p);
            }
            Expect(WatTokenKind.RParen);
        }

        // Results
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "result")
        {
            Consume(); // (
            Consume(); // result
            while (!IsRParen())
                func.Results.Add(ParseValType());
            Expect(WatTokenKind.RParen);
        }

        // Locals
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "local")
        {
            var localGroupStart = Peek();
            Consume(); // (
            Consume(); // local
            if (Peek().Kind == WatTokenKind.Identifier)
            {
                var l = new WatLocal { Name = Consume().Value, Type = ParseValType() };
                Expect(WatTokenKind.RParen);
                l.Span = SpanFrom(localGroupStart);
                func.Locals.Add(l);
                continue;
            }
            while (!IsRParen())
            {
                var typeStart = Peek();
                var l = new WatLocal { Type = ParseValType() };
                l.Span = SpanFrom(typeStart);
                func.Locals.Add(l);
            }
            Expect(WatTokenKind.RParen);
        }

        // Body instructions
        while (!IsRParen())
            func.Body.Add(ParseInstruction());

        Expect(WatTokenKind.RParen);
        func.Span = SpanFrom(startToken);
        return func;
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
            while (Peek().Kind == WatTokenKind.LParen)
                instr.Operands.Add(ParseFoldedInstruction());
            Expect(WatTokenKind.RParen);
        }
        instr.Span = SpanFrom(startToken);
        return instr;
    }

    private WatInstruction ParseFoldedIf()
    {
        var instr = new WatInstruction { Mnemonic = "if" };

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
        while (Peek().Kind == WatTokenKind.LParen &&
               PeekAt(1).Value is not "then" and not "else")
        {
            instr.Operands.Add(ParseFoldedInstruction());
        }

        // (then ...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "then")
        {
            Consume(); // (
            Consume(); // then
            instr.ThenBody = [];
            while (!IsRParen())
                instr.ThenBody.Add(ParseInstruction());
            Expect(WatTokenKind.RParen);
        }

        // (else ...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "else")
        {
            Consume(); // (
            Consume(); // else
            instr.ElseBody = [];
            while (!IsRParen())
                instr.ElseBody.Add(ParseInstruction());
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

        // try_table catch handler clauses
        if (mnemonic == "try_table")
        {
            instr.CatchHandlers = [];
            while (Peek().Kind == WatTokenKind.LParen &&
                   PeekAt(1).Value is "catch" or "catch_ref" or "catch_all" or "catch_all_ref")
            {
                var handlerStart = Peek();
                Consume(); // (
                var catchKw = Consume().Value; // catch/catch_ref/catch_all/catch_all_ref
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

                // catch/catch_ref have a tag operand
                if (handler.CatchKind is 0x00 or 0x01)
                {
                    if (Peek().Kind == WatTokenKind.Identifier)
                        handler.TagName = Consume().Value;
                    else
                        handler.TagIndex = ParseInt32();
                }

                // All handlers have a label target
                if (Peek().Kind == WatTokenKind.Identifier)
                    handler.LabelName = Consume().Value;
                else
                    handler.LabelIndex = ParseInt32();

                Expect(WatTokenKind.RParen);
                handler.Span = SpanFrom(handlerStart);
                instr.CatchHandlers.Add(handler);
            }
        }

        // Body instructions
        instr.ThenBody = [];
        while (!IsRParen())
            instr.ThenBody.Add(ParseInstruction());

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

        // (do body...)
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "do")
        {
            Consume(); // (
            Consume(); // do
            instr.ThenBody = [];
            while (!IsRParen())
                instr.ThenBody.Add(ParseInstruction());
            Expect(WatTokenKind.RParen);
        }

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

            while (!IsRParen())
                clause.Body.Add(ParseInstruction());
            Expect(WatTokenKind.RParen);
            clause.Span = SpanFrom(clauseStart);
            instr.LegacyCatchClauses.Add(clause);
        }

        Expect(WatTokenKind.RParen);
        return instr;
    }

    private WatInstruction ParsePlainInstruction()
    {
        var tok = Expect(WatTokenKind.Keyword);
        var instr = new WatInstruction { Mnemonic = tok.Value };

        // Handle block/loop/if/try_table in flat form
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
                    while (!IsKeyword("else") && !IsKeyword("end"))
                        instr.ThenBody.Add(ParseInstruction());

                    if (IsKeyword("else"))
                    {
                        Consume();
                        instr.ElseBody = [];
                        while (!IsKeyword("end"))
                            instr.ElseBody.Add(ParseInstruction());
                    }

                    ExpectKeyword("end");
                    if (Peek().Kind == WatTokenKind.Identifier)
                        Consume();
                    instr.Span = SpanFrom(tok);
                    return instr;

                case "try":
                    ParsePlainLegacyTry(instr);
                    instr.Span = SpanFrom(tok);
                    return instr;

                default:
                    instr.ThenBody = [];
                    while (!IsKeyword("end"))
                        instr.ThenBody.Add(ParseInstruction());

                    ExpectKeyword("end");
                    if (Peek().Kind == WatTokenKind.Identifier)
                        Consume();
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
            if (IsKeyword("end"))
            {
                Consume();
                if (Peek().Kind == WatTokenKind.Identifier)
                    Consume();
                return;
            }

            if (IsKeyword("delegate"))
            {
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
                var clause = new WatLegacyCatchClause { IsCatchAll = IsKeyword("catch_all") };
                Consume();

                if (!clause.IsCatchAll)
                {
                    if (Peek().Kind == WatTokenKind.Identifier)
                        clause.TagName = Consume().Value;
                    else
                        clause.TagIndex = ParseInt32();
                }

                while (!IsKeyword("catch") && !IsKeyword("catch_all") && !IsKeyword("delegate") && !IsKeyword("end"))
                    clause.Body.Add(ParseInstruction());

                instr.LegacyCatchClauses.Add(clause);
                continue;
            }

            instr.ThenBody.Add(ParseInstruction());
        }
    }

    private bool IsKeyword(string value) =>
        Peek().Kind == WatTokenKind.Keyword && Peek().Value == value;

    private void ParseImmediates(WatInstruction instr)
    {
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
                    instr.FloatValue = ParseFloat64();
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
                var rtTok = Expect(WatTokenKind.Keyword);
                instr.Index = rtTok.Value switch
                {
                    "funcref" => 0x70,
                    "externref" => 0x6F,
                    "exnref" => 0x69,
                    "contref" => 0x65,
                    _ => throw Error($"Unknown reftype: {rtTok.Value}"),
                };
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
                instr.IntValue = int.Parse(tok.Value["align=".Length..]);
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
        // v128.const <shape> <values...>
        // Disassembler always emits: v128.const i32x4 <4 hex values>
        var shapeTok = Expect(WatTokenKind.Keyword);
        var shape = shapeTok.Value;
        var bytes = new byte[16];

        switch (shape)
        {
            case "i8x16":
                for (int i = 0; i < 16; i++)
                    bytes[i] = (byte)ParseInt32();
                break;
            case "i16x8":
                for (int i = 0; i < 8; i++)
                {
                    var val = (short)ParseInt32();
                    BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 2), val);
                }
                break;
            case "i32x4":
                for (int i = 0; i < 4; i++)
                {
                    var val = ParseInt32();
                    BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(i * 4), val);
                }
                break;
            case "i64x2":
                for (int i = 0; i < 2; i++)
                {
                    var val = ParseInt64();
                    BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(i * 8), val);
                }
                break;
            case "f32x4":
                for (int i = 0; i < 4; i++)
                {
                    var val = (float)ParseFloat64();
                    BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * 4), val);
                }
                break;
            case "f64x2":
                for (int i = 0; i < 2; i++)
                {
                    var val = ParseFloat64();
                    BinaryPrimitives.WriteDoubleLittleEndian(bytes.AsSpan(i * 8), val);
                }
                break;
            default:
                throw Error($"Unknown v128.const shape: {shape}");
        }

        instr.V128Bytes = bytes;
    }

    private void ParseSimdShuffle(WatInstruction instr)
    {
        // i8x16.shuffle: 16 lane indices (each 0-31)
        var bytes = new byte[16];
        for (int i = 0; i < 16; i++)
            bytes[i] = (byte)ParseInt32();
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

        // Inline exports
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); Consume();
            var name = Expect(WatTokenKind.String);
            mem.InlineExports.Add(new WatExport { Name = name.Value, Kind = ExportKind.Memory });
            Expect(WatTokenKind.RParen);
        }

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
        var moduleName = Expect(WatTokenKind.String).Value;
        var fieldName = Expect(WatTokenKind.String).Value;

        Expect(WatTokenKind.LParen);
        var kindTok = Expect(WatTokenKind.Keyword);

        var import = new WatImport { ModuleName = moduleName, FieldName = fieldName };

        if (kindTok.Value == "func")
        {
            import.Kind = ImportKind.Func;
            if (Peek().Kind == WatTokenKind.Identifier)
                import.FuncName = Consume().Value;

            // Params
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
            {
                Consume(); Consume();
                if (Peek().Kind == WatTokenKind.Identifier)
                {
                    import.Params.Add(new WatParam { Name = Consume().Value, Type = ParseValType() });
                }
                else
                {
                    while (!IsRParen())
                        import.Params.Add(new WatParam { Type = ParseValType() });
                }
                Expect(WatTokenKind.RParen);
            }

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
            import.MemMin = ParseInt32();
            if (!IsRParen())
                import.MemMax = ParseInt32();
        }
        else if (kindTok.Value == "global")
        {
            import.Kind = ImportKind.Global;
            // (global i32) or (global (mut i32))
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
        else if (kindTok.Value == "tag")
        {
            import.Kind = ImportKind.Tag;
            if (Peek().Kind == WatTokenKind.Identifier)
                import.FuncName = Consume().Value; // optional tag name

            // (param ...) clauses define the tag's type
            while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
            {
                Consume(); Consume();
                while (!IsRParen())
                    import.Params.Add(new WatParam { Type = ParseValType() });
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

        // (type N) reference
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "type")
        {
            Consume(); Consume();
            tag.TypeIndex = ParseInt32();
            Expect(WatTokenKind.RParen);
        }

        // (param ...) clauses
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "param")
        {
            Consume(); Consume();
            while (!IsRParen())
                tag.Params.Add(ParseValType());
            Expect(WatTokenKind.RParen);
        }

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

        // Inline exports
        while (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "export")
        {
            Consume(); Consume();
            var name = Expect(WatTokenKind.String);
            global.InlineExports.Add(new WatExport { Name = name.Value, Kind = ExportKind.Global });
            Expect(WatTokenKind.RParen);
        }

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

        // Offset expression: (i32.const N)
        if (Peek().Kind == WatTokenKind.LParen)
            data.Offset = ParseFoldedInstruction();

        // One or more string literals concatenated
        var bytes = new List<byte>();
        while (Peek().Kind == WatTokenKind.String)
        {
            var s = Consume().Value;
            foreach (var c in s)
                bytes.Add((byte)c);
        }
        data.Bytes = bytes.ToArray();

        Expect(WatTokenKind.RParen);
        data.Span = SpanFrom(startToken);
        return data;
    }

    private WatTable ParseTable()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("table");
        var table = new WatTable();

        // (table N funcref) or (table N N funcref)
        table.Min = (uint)ParseInt32();
        if (Peek().Kind == WatTokenKind.Integer)
        {
            table.Max = (uint)ParseInt32();
        }
        // Consume element type keyword (funcref/anyfunc)
        if (Peek().Kind == WatTokenKind.Keyword)
            Consume();

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

        // Optional (table N) for non-zero table index
        if (Peek().Kind == WatTokenKind.LParen && PeekAt(1).Value == "table")
        {
            Consume(); // (
            Consume(); // table
            elem.TableIndex = ParseInt32();
            Expect(WatTokenKind.RParen);
        }

        // Offset expression: (i32.const N)
        if (Peek().Kind == WatTokenKind.LParen)
            elem.Offset = ParseFoldedInstruction();

        // Optional "func" keyword before index list
        if (Peek().Kind == WatTokenKind.Keyword && Peek().Value == "func")
            Consume();

        // List of function indices
        while (Peek().Kind == WatTokenKind.Integer)
            elem.FuncIndices.Add(ParseInt32());

        Expect(WatTokenKind.RParen);
        elem.Span = SpanFrom(startToken);
        return elem;
    }

    private WatExport ParseExport()
    {
        var startToken = Peek();
        Expect(WatTokenKind.LParen);
        ExpectKeyword("export");
        var name = Expect(WatTokenKind.String);
        var export = new WatExport { Name = name.Value };

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
        value is "nan" or "inf" or "-inf" || value.StartsWith("nan:");

    private double ParseFloat64()
    {
        var tok = Consume();
        var text = tok.Value.Replace("_", "");

        if (text == "inf") return double.PositiveInfinity;
        if (text == "-inf") return double.NegativeInfinity;
        if (text == "nan") return double.NaN;
        if (text.StartsWith("nan:0x") || text.StartsWith("nan:0X"))
        {
            // NaN with custom payload — store as raw bits
            var hex = text[(text.IndexOf('x') + 1)..];
            ulong payload = Convert.ToUInt64(hex, 16);
            ulong bits = 0x7FF0000000000000uL | (payload & 0xFFFFFFFFFFFFFuL);
            return BitConverter.UInt64BitsToDouble(bits);
        }

        return double.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
    }

    internal static int ParseIntFromToken(WatToken tok)
    {
        var text = tok.Value.Replace("_", "");
        if (text.StartsWith("0x") || text.StartsWith("0X") ||
            text.StartsWith("-0x") || text.StartsWith("-0X") ||
            text.StartsWith("+0x") || text.StartsWith("+0X"))
        {
            bool negative = text[0] == '-';
            var hex = text[(text.IndexOf('x') + 1)..];
            uint val = Convert.ToUInt32(hex, 16);
            return negative ? -(int)val : (int)val;
        }
        return int.Parse(text);
    }

    internal static long ParseLongFromToken(WatToken tok)
    {
        var text = tok.Value.Replace("_", "");
        if (text.StartsWith("0x") || text.StartsWith("0X") ||
            text.StartsWith("-0x") || text.StartsWith("-0X") ||
            text.StartsWith("+0x") || text.StartsWith("+0X"))
        {
            bool negative = text[0] == '-';
            var hex = text[(text.IndexOf('x') + 1)..];
            ulong val = Convert.ToUInt64(hex, 16);
            return negative ? -(long)val : (long)val;
        }
        return long.Parse(text);
    }

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

    private WatToken ExpectKeyword(string value)
    {
        var tok = Expect(WatTokenKind.Keyword);
        if (tok.Value != value)
            throw Error($"Expected keyword '{value}', got '{tok.Value}'");
        return tok;
    }

    private void SkipSExpr()
    {
        if (Peek().Kind != WatTokenKind.LParen)
        {
            Consume();
            return;
        }
        Consume(); // (
        int depth = 1;
        while (depth > 0 && Peek().Kind != WatTokenKind.EOF)
        {
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
