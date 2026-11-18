// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;

/// <summary>
/// A WIT text front end: parses packages, interfaces and worlds and resolves them the way wit-parser does, so
/// <see cref="WitWorldEncoder"/> can encode a world's component type byte-for-byte like wit-component.
/// </summary>
/// <remarks>
/// The resolved model follows wit-parser's observable orderings, which decide the encoding: an interface lists
/// its <c>use</c>d types first, then its type definitions in wit-parser's topological order; functions follow in
/// source order with resource functions at their resource; every value-position reference to a resource
/// (directly or through an alias) is an anonymous <c>own</c> handle; and a world's imports are elaborated with
/// the interfaces they depend on. Items gated by <c>@unstable</c> are left out, as without enabled features.
/// </remarks>
internal static class WitText
{
    /// <summary>Parses <paramref name="text"/> and resolves the world (the main package's only one unless named).</summary>
    public static WitResolvedWorld ResolveWorld(string text, string? worldName = null)
    {
        var files = new WitParser(text).ParseFile();
        return new WitResolver(files).Resolve(worldName);
    }
}

// ── Resolved model ─────────────────────────────────────────────────────────────

internal abstract record WitVal;
internal sealed record WitPrimitive(byte Code) : WitVal;
internal sealed record WitRef(WitTDef Def) : WitVal;

internal sealed class WitTDef(int id, string? name, object? owner, WitKind kind)
{
    public int Id { get; } = id;
    public string? Name { get; } = name;
    /// <summary><see cref="WitIface"/>, <see cref="WitWorldDef"/> or null for anonymous types.</summary>
    public object? Owner { get; } = owner;
    public WitKind Kind { get; set; } = kind;
}

internal abstract record WitKind;
internal sealed record WitRecordKind(List<(string Name, WitVal Type)> Fields) : WitKind;
internal sealed record WitVariantKind(List<(string Name, WitVal? Type)> Cases) : WitKind;
internal sealed record WitEnumKind(List<string> Cases) : WitKind;
internal sealed record WitFlagsKind(List<string> Flags) : WitKind;
internal sealed record WitTupleKind(List<WitVal> Types) : WitKind;
internal sealed record WitListKind(WitVal Element, uint? Length) : WitKind;
internal sealed record WitMapKind(WitVal Key, WitVal Value) : WitKind;
internal sealed record WitOptionKind(WitVal Element) : WitKind;
internal sealed record WitResultKind(WitVal? Ok, WitVal? Err) : WitKind;
internal sealed record WitFutureKind(WitVal? Element) : WitKind;
internal sealed record WitStreamKind(WitVal? Element) : WitKind;
internal sealed record WitResourceKind : WitKind;
internal sealed record WitOwnKind(WitTDef Resource) : WitKind;
internal sealed record WitBorrowKind(WitTDef Resource) : WitKind;
internal sealed record WitAliasKind(WitVal Target) : WitKind;

internal enum WitFunctionKind
{
    Freestanding,
    Method,
    Static,
    Constructor
}

internal sealed record WitFunction(
    string Name, bool Async, WitFunctionKind Kind, WitTDef? Resource, List<(string Name, WitVal Type)> Params, WitVal? Result);

internal sealed class WitPackageName(string ns, string name, string? version)
{
    public string Namespace { get; } = ns;
    public string Name { get; } = name;
    public string? Version { get; } = version;

    public string InterfaceId(string item) => $"{Namespace}:{Name}/{item}" + (Version is null ? "" : "@" + Version);

    public override string ToString() => $"{Namespace}:{Name}" + (Version is null ? "" : "@" + Version);
}

internal sealed class WitIface(string? name, WitPackageName package)
{
    public string? Name { get; } = name;
    public WitPackageName Package { get; } = package;
    public List<(string Name, WitTDef Def)> Types { get; } = [];
    public List<WitFunction> Functions { get; } = [];

    public string Id => Package.InterfaceId(Name ?? throw new InvalidOperationException("anonymous interface"));
}

internal sealed class WitWorldDef(string name, WitPackageName package)
{
    public string Name { get; } = name;
    public WitPackageName Package { get; } = package;
}

internal abstract record WitWorldItem;
internal sealed record WitWorldInterface(WitIface Interface) : WitWorldItem;
internal sealed record WitWorldFunction(WitFunction Function) : WitWorldItem;
internal sealed record WitWorldType(WitTDef Def) : WitWorldItem;

/// <summary>A world key: an interface (named by its id) or a plain kebab name.</summary>
internal sealed record WitWorldKey(string Name, WitIface? Interface);

internal sealed class WitResolvedWorld(WitWorldDef world, List<(WitWorldKey Key, WitWorldItem Item)> imports, List<(WitWorldKey Key, WitWorldItem Item)> exports)
{
    public WitWorldDef World { get; } = world;
    public List<(WitWorldKey Key, WitWorldItem Item)> Imports { get; } = imports;
    public List<(WitWorldKey Key, WitWorldItem Item)> Exports { get; } = exports;
}

// ── AST ────────────────────────────────────────────────────────────────────────

internal sealed record WitUsePath(string? Namespace, string? Package, string Name, string? Version)
{
    public bool IsLocal => Namespace is null;
}

internal abstract record WitAstType;
internal sealed record WitAstPrimitive(byte Code) : WitAstType;
internal sealed record WitAstName(string Name) : WitAstType;
internal sealed record WitAstList(WitAstType Element, uint? Length) : WitAstType;
internal sealed record WitAstMap(WitAstType Key, WitAstType Value) : WitAstType;
internal sealed record WitAstOption(WitAstType Element) : WitAstType;
internal sealed record WitAstResult(WitAstType? Ok, WitAstType? Err) : WitAstType;
internal sealed record WitAstTuple(List<WitAstType> Types) : WitAstType;
internal sealed record WitAstFuture(WitAstType? Element) : WitAstType;
internal sealed record WitAstStream(WitAstType? Element) : WitAstType;
internal sealed record WitAstOwn(string Resource) : WitAstType;
internal sealed record WitAstBorrow(string Resource) : WitAstType;

internal sealed record WitAstFunc(bool Async, List<(string Name, WitAstType Type)> Params, WitAstType? Result);

internal abstract record WitAstTypeBody;
internal sealed record WitAstAliasBody(WitAstType Target) : WitAstTypeBody;
internal sealed record WitAstRecordBody(List<(string Name, WitAstType Type)> Fields) : WitAstTypeBody;
internal sealed record WitAstVariantBody(List<(string Name, WitAstType? Type)> Cases) : WitAstTypeBody;
internal sealed record WitAstEnumBody(List<string> Cases) : WitAstTypeBody;
internal sealed record WitAstFlagsBody(List<string> Flags) : WitAstTypeBody;
internal sealed record WitAstResourceBody(List<WitAstResourceFunc> Funcs) : WitAstTypeBody;

internal sealed record WitAstResourceFunc(WitFunctionKind Kind, string Name, WitAstFunc Func);

internal abstract record WitAstItem;
internal sealed record WitAstUse(WitUsePath From, List<(string Name, string? As)> Names) : WitAstItem;
internal sealed record WitAstTypeDef(string Name, WitAstTypeBody Body) : WitAstItem;
internal sealed record WitAstFuncItem(string Name, WitAstFunc Func) : WitAstItem;
internal sealed record WitAstImportExport(bool Import, string? Name, WitUsePath? Path, WitAstFunc? Func, List<WitAstItem>? InlineInterface) : WitAstItem;
internal sealed record WitAstInclude(WitUsePath From, List<(string Name, string As)> With) : WitAstItem;

internal sealed record WitAstInterface(string Name, List<WitAstItem> Items);
internal sealed record WitAstWorld(string Name, List<WitAstItem> Items);

internal sealed class WitAstPackage(WitPackageName name)
{
    public WitPackageName Name { get; } = name;
    public List<WitAstInterface> Interfaces { get; } = [];
    public List<WitAstWorld> Worlds { get; } = [];
    public List<(WitUsePath Path, string As)> TopLevelUses { get; } = [];
}

// ── Parser ─────────────────────────────────────────────────────────────────────

internal sealed class WitParser(string text)
{
    private int _pos;
    private string? _peeked;

    public List<WitAstPackage> ParseFile()
    {
        var packages = new List<WitAstPackage>();
        WitAstPackage? main = null;
        bool sawNested = false;
        var pendingMain = new List<Action<WitAstPackage>>();
        while (true)
        {
            bool gated = SkipAttributes();
            string? token = Peek();
            if (token is null)
                break;
            switch (token)
            {
                case "package":
                {
                    Next();
                    var name = ParsePackageName();
                    if (Peek() == "{")
                    {
                        Next();
                        var nested = new WitAstPackage(name);
                        while (Peek() != "}")
                            ParsePackageItem(nested, SkipAttributes());
                        Next();
                        if (!gated)
                            packages.Add(nested);
                        sawNested = true;
                    }
                    else
                    {
                        Expect(";");
                        if (main is not null)
                            throw Error("more than one top-level package declaration");
                        if (sawNested)
                            throw Error("the top-level `package` declaration must come before nested packages");
                        main = new WitAstPackage(name);
                        packages.Insert(0, main);
                        foreach (var add in pendingMain)
                            add(main);
                    }

                    break;
                }

                default:
                    if (main is null)
                    {
                        // Items before the package declaration are not allowed in WIT; keep the error simple.
                        throw Error("a WIT file must start with a `package` declaration");
                    }

                    ParsePackageItem(main, gated);
                    break;
            }
        }

        if (main is null)
            throw Error("no `package` declaration");
        return packages;
    }

    private void ParsePackageItem(WitAstPackage package, bool gated)
    {
        string keyword = Next() ?? throw Error("unexpected end of input");
        switch (keyword)
        {
            case "interface":
            {
                string name = Identifier();
                var items = ParseInterfaceBody();
                if (!gated)
                    package.Interfaces.Add(new WitAstInterface(name, items));
                break;
            }

            case "world":
            {
                string name = Identifier();
                Expect("{");
                var items = new List<WitAstItem>();
                while (Peek() != "}")
                {
                    bool itemGated = SkipAttributes();
                    var item = ParseWorldItem();
                    if (!itemGated)
                        items.Add(item);
                }

                Next();
                if (!gated)
                    package.Worlds.Add(new WitAstWorld(name, items));
                break;
            }

            case "use":
            {
                var path = ParseUsePath();
                string alias = path.Name;
                if (Peek() == "as")
                {
                    Next();
                    alias = Identifier();
                }

                Expect(";");
                if (!gated)
                    package.TopLevelUses.Add((path, alias));
                break;
            }

            default:
                throw Error($"expected `interface`, `world`, `use` or `package`, found `{keyword}`");
        }
    }

    private List<WitAstItem> ParseInterfaceBody()
    {
        Expect("{");
        var items = new List<WitAstItem>();
        while (Peek() != "}")
        {
            bool gated = SkipAttributes();
            var item = ParseInterfaceItem();
            if (!gated && item is not null)
                items.Add(item);
        }

        Next();
        return items;
    }

    private WitAstItem? ParseInterfaceItem()
    {
        string token = Peek() ?? throw Error("unexpected end of input");
        switch (PeekIsEscaped() ? "" : token)
        {
            case "use":
                return ParseUse();
            case "type" or "record" or "variant" or "enum" or "flags" or "resource":
                return ParseTypeDef();
            default:
            {
                string name = Identifier();
                Expect(":");
                var func = ParseFuncType();
                Expect(";");
                return new WitAstFuncItem(name, func);
            }
        }
    }

    private WitAstItem ParseWorldItem()
    {
        string token = Peek() ?? throw Error("unexpected end of input");
        switch (PeekIsEscaped() ? "" : token)
        {
            case "use":
                return ParseUse();
            case "type" or "record" or "variant" or "enum" or "flags" or "resource":
                return ParseTypeDef();
            case "include":
            {
                Next();
                var path = ParseUsePath();
                var with = new List<(string, string)>();
                if (Peek() == "with")
                {
                    Next();
                    Expect("{");
                    while (Peek() != "}")
                    {
                        string from = Identifier();
                        Expect("as");
                        with.Add((from, Identifier()));
                        if (Peek() == ",")
                            Next();
                    }

                    Next();
                }
                else
                {
                    Expect(";");
                }

                return new WitAstInclude(path, with);
            }

            case "import" or "export":
            {
                Next();
                bool import = token == "import";
                string first = Identifier();
                if (Peek() == ":")
                {
                    // `name: func`, `name: interface { ... }`, or a package path `ns:pkg/iface`.
                    Next();
                    string second = PeekIdentifierText();
                    if (second is "func" or "async")
                    {
                        var func = ParseFuncType();
                        Expect(";");
                        return new WitAstImportExport(import, first, null, func, null);
                    }

                    if (second == "interface")
                    {
                        Next();
                        var items = ParseInterfaceBody();
                        if (Peek() == ";")
                            Next();
                        return new WitAstImportExport(import, first, null, null, items);
                    }

                    string package = Identifier();
                    Expect("/");
                    string name = Identifier();
                    string? version = TryVersion();
                    Expect(";");
                    return new WitAstImportExport(import, null, new WitUsePath(first, package, name, version), null, null);
                }

                Expect(";");
                return new WitAstImportExport(import, null, new WitUsePath(null, null, first, null), null, null);
            }

            default:
                throw Error($"unexpected `{token}` in a world");
        }
    }

    private WitAstUse ParseUse()
    {
        Expect("use");
        var path = ParseUsePath();
        Expect(".");
        Expect("{");
        var names = new List<(string, string?)>();
        while (Peek() != "}")
        {
            string name = Identifier();
            string? alias = null;
            if (Peek() == "as")
            {
                Next();
                alias = Identifier();
            }

            names.Add((name, alias));
            if (Peek() == ",")
                Next();
        }

        Next();
        Expect(";");
        return new WitAstUse(path, names);
    }

    private WitUsePath ParseUsePath()
    {
        string first = Identifier();
        if (Peek() != ":")
            return new WitUsePath(null, null, first, null);
        Next();
        string package = Identifier();
        Expect("/");
        string name = Identifier();
        return new WitUsePath(first, package, name, TryVersion());
    }

    private WitAstTypeDef ParseTypeDef()
    {
        string keyword = Next()!;
        string name = Identifier();
        switch (keyword)
        {
            case "type":
            {
                Expect("=");
                var target = ParseType();
                Expect(";");
                return new WitAstTypeDef(name, new WitAstAliasBody(target));
            }

            case "record":
            {
                var fields = new List<(string, WitAstType)>();
                Expect("{");
                while (Peek() != "}")
                {
                    SkipAttributes();
                    string field = Identifier();
                    Expect(":");
                    fields.Add((field, ParseType()));
                    if (Peek() == ",")
                        Next();
                }

                Next();
                return new WitAstTypeDef(name, new WitAstRecordBody(fields));
            }

            case "variant":
            {
                var cases = new List<(string, WitAstType?)>();
                Expect("{");
                while (Peek() != "}")
                {
                    SkipAttributes();
                    string @case = Identifier();
                    WitAstType? payload = null;
                    if (Peek() == "(")
                    {
                        Next();
                        payload = ParseType();
                        Expect(")");
                    }

                    cases.Add((@case, payload));
                    if (Peek() == ",")
                        Next();
                }

                Next();
                return new WitAstTypeDef(name, new WitAstVariantBody(cases));
            }

            case "enum" or "flags":
            {
                var names = new List<string>();
                Expect("{");
                while (Peek() != "}")
                {
                    SkipAttributes();
                    names.Add(Identifier());
                    if (Peek() == ",")
                        Next();
                }

                Next();
                return new WitAstTypeDef(name, keyword == "enum" ? new WitAstEnumBody(names) : new WitAstFlagsBody(names));
            }

            case "resource":
            {
                var funcs = new List<WitAstResourceFunc>();
                if (Peek() == ";")
                {
                    Next();
                    return new WitAstTypeDef(name, new WitAstResourceBody(funcs));
                }

                Expect("{");
                while (Peek() != "}")
                {
                    bool gated = SkipAttributes();
                    WitAstResourceFunc func;
                    if (Peek() == "constructor")
                    {
                        Next();
                        var parameters = ParseParams();
                        WitAstType? result = null;
                        if (Peek() == "->")
                        {
                            Next();
                            result = ParseType();
                        }

                        func = new WitAstResourceFunc(WitFunctionKind.Constructor, "constructor", new WitAstFunc(false, parameters, result));
                    }
                    else
                    {
                        string funcName = Identifier();
                        Expect(":");
                        bool isStatic = false;
                        if (Peek() == "static")
                        {
                            Next();
                            isStatic = true;
                        }

                        func = new WitAstResourceFunc(isStatic ? WitFunctionKind.Static : WitFunctionKind.Method, funcName, ParseFuncType());
                    }

                    Expect(";");
                    if (!gated)
                        funcs.Add(func);
                }

                Next();
                return new WitAstTypeDef(name, new WitAstResourceBody(funcs));
            }

            default:
                throw Error($"unexpected `{keyword}`");
        }
    }

    private WitAstFunc ParseFuncType()
    {
        bool isAsync = false;
        if (Peek() == "async")
        {
            Next();
            isAsync = true;
        }

        Expect("func");
        var parameters = ParseParams();
        WitAstType? result = null;
        if (Peek() == "->")
        {
            Next();
            result = ParseType();
        }

        return new WitAstFunc(isAsync, parameters, result);
    }

    private List<(string, WitAstType)> ParseParams()
    {
        Expect("(");
        var parameters = new List<(string, WitAstType)>();
        while (Peek() != ")")
        {
            string name = Identifier();
            Expect(":");
            parameters.Add((name, ParseType()));
            if (Peek() == ",")
                Next();
        }

        Next();
        return parameters;
    }

    private WitAstType ParseType()
    {
        bool escaped = PeekIsEscaped();
        string token = Next() ?? throw Error("expected a type");
        if (escaped)
            return new WitAstName(token);
        switch (token)
        {
            case "bool": return new WitAstPrimitive(0x7F);
            case "s8": return new WitAstPrimitive(0x7E);
            case "u8": return new WitAstPrimitive(0x7D);
            case "s16": return new WitAstPrimitive(0x7C);
            case "u16": return new WitAstPrimitive(0x7B);
            case "s32": return new WitAstPrimitive(0x7A);
            case "u32": return new WitAstPrimitive(0x79);
            case "s64": return new WitAstPrimitive(0x78);
            case "u64": return new WitAstPrimitive(0x77);
            case "f32": return new WitAstPrimitive(0x76);
            case "f64": return new WitAstPrimitive(0x75);
            case "char": return new WitAstPrimitive(0x74);
            case "string": return new WitAstPrimitive(0x73);
            case "error-context": return new WitAstPrimitive(0x64);
            case "list":
            {
                Expect("<");
                var element = ParseType();
                uint? length = null;
                if (Peek() == ",")
                {
                    Next();
                    string lengthToken = Next() ?? throw Error("expected a fixed list length");
                    if (!lengthToken.All(char.IsAsciiDigit))
                        throw Error($"expected a fixed list length, found `{lengthToken}`");
                    if (!uint.TryParse(lengthToken, NumberStyles.None, CultureInfo.InvariantCulture, out uint parsed))
                        throw Error($"fixed list length `{lengthToken}` is too large");
                    length = parsed;
                }

                Expect(">");
                return new WitAstList(element, length);
            }

            case "map":
            {
                Expect("<");
                var key = ParseType();
                Expect(",");
                var value = ParseType();
                Expect(">");
                return new WitAstMap(key, value);
            }

            case "option":
            {
                Expect("<");
                var element = ParseType();
                Expect(">");
                return new WitAstOption(element);
            }

            case "result":
            {
                if (Peek() != "<")
                    return new WitAstResult(null, null);
                Next();
                WitAstType? ok = null;
                if (Peek() == "_")
                    Next();
                else
                    ok = ParseType();
                WitAstType? err = null;
                if (Peek() == ",")
                {
                    Next();
                    err = ParseType();
                }

                Expect(">");
                return new WitAstResult(ok, err);
            }

            case "tuple":
            {
                Expect("<");
                var types = new List<WitAstType>();
                while (Peek() != ">")
                {
                    types.Add(ParseType());
                    if (Peek() == ",")
                        Next();
                }

                Next();
                return new WitAstTuple(types);
            }

            case "future" or "stream":
            {
                WitAstType? element = null;
                if (Peek() == "<")
                {
                    Next();
                    element = ParseType();
                    Expect(">");
                }

                return token == "future" ? new WitAstFuture(element) : new WitAstStream(element);
            }

            case "own" or "borrow":
            {
                Expect("<");
                string resource = Identifier();
                Expect(">");
                return token == "own" ? new WitAstOwn(resource) : new WitAstBorrow(resource);
            }

            default:
                if (!IsIdentifierStart(token[0]))
                    throw Error($"expected a type, found `{token}`");
                return new WitAstName(token);
        }
    }

    /// <summary>Skips <c>@since</c>/<c>@deprecated</c>/<c>@unstable</c> gates; returns true for <c>@unstable</c>.</summary>
    private bool SkipAttributes()
    {
        bool unstable = false;
        while (Peek() == "@")
        {
            Next();
            string name = Identifier();
            if (name == "unstable")
                unstable = true;
            else if (name is not ("since" or "deprecated"))
                throw Error($"unsupported attribute `@{name}`");
            Expect("(");
            int depth = 1;
            while (depth > 0)
            {
                // Attribute arguments include versions (`version = 0.2.0`), lexed character by character.
                SkipTrivia();
                if (_peeked is not null)
                {
                    string t = Next()!;
                    if (t == "(") depth++;
                    else if (t == ")") depth--;
                    continue;
                }

                if (_pos >= text.Length)
                    throw Error("unterminated attribute");
                char c = text[_pos++];
                if (c == '(') depth++;
                else if (c == ')') depth--;
            }
        }

        return unstable;
    }

    private WitPackageName ParsePackageName()
    {
        string ns = Identifier();
        Expect(":");
        string name = Identifier();
        // Nested namespaces (`a:b:c`) and package paths are not part of the supported subset.
        return new WitPackageName(ns, name, TryVersion());
    }

    private string? TryVersion()
    {
        if (Peek() != "@")
            return null;
        Next();
        SkipTrivia();
        int start = _pos;
        while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) || text[_pos] is '.' or '-' or '+'))
            _pos++;
        // `ns:pkg/iface@1.2.3.{a, b}`: the `.` before the name list is not part of the version.
        while (_pos > start && text[_pos - 1] == '.')
            _pos--;
        if (_pos == start)
            throw Error("expected a version");
        return text[start.._pos];
    }

    private string Identifier()
    {
        string token = Next() ?? throw Error("expected an identifier");
        if (!IsIdentifierStart(token[0]))
            throw Error($"expected an identifier, found `{token}`");
        return token;
    }

    private void Expect(string expected)
    {
        string? token = Next();
        if (token != expected)
            throw Error($"expected `{expected}`, found `{token ?? "end of input"}`");
    }

    private bool _peekedEscaped;
    private bool _lastEscaped;

    private bool PeekIsEscaped()
    {
        Peek();
        return _peekedEscaped;
    }

    private string PeekIdentifierText() => PeekIsEscaped() ? "" : Peek() ?? "";

    private string? Peek()
    {
        if (_peeked is null)
        {
            _peeked = Lex();
            _peekedEscaped = _lastEscaped;
        }

        return _peeked;
    }

    private string? Next()
    {
        string? token = Peek();
        _peeked = null;
        return token;
    }

    private void SkipTrivia()
    {
        while (_pos < text.Length)
        {
            char c = text[_pos];
            if (char.IsWhiteSpace(c))
            {
                _pos++;
            }
            else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '/')
            {
                while (_pos < text.Length && text[_pos] != '\n')
                    _pos++;
            }
            else if (c == '/' && _pos + 1 < text.Length && text[_pos + 1] == '*')
            {
                int depth = 0;
                do
                {
                    if (_pos + 1 >= text.Length)
                        throw Error("unterminated block comment");
                    if (text[_pos] == '/' && text[_pos + 1] == '*') { depth++; _pos += 2; }
                    else if (text[_pos] == '*' && text[_pos + 1] == '/') { depth--; _pos += 2; }
                    else _pos++;
                }
                while (depth > 0);
            }
            else
            {
                break;
            }
        }
    }

    private string? Lex()
    {
        _lastEscaped = false;
        SkipTrivia();
        if (_pos >= text.Length)
            return null;
        char c = text[_pos];
        if (c == '-' && _pos + 1 < text.Length && text[_pos + 1] == '>')
        {
            _pos += 2;
            return "->";
        }

        if (c == '%')
        {
            _pos++;
            if (_pos >= text.Length || !IsIdentifierStart(text[_pos]))
                throw Error("expected an identifier after `%`");
            _lastEscaped = true;
            c = text[_pos];
        }

        if (IsIdentifierStart(c))
        {
            int start = _pos;
            while (_pos < text.Length && (char.IsAsciiLetterOrDigit(text[_pos]) ||
                   (text[_pos] == '-' && _pos + 1 < text.Length && char.IsAsciiLetterOrDigit(text[_pos + 1]))))
                _pos++;
            return text[start.._pos];
        }

        if (char.IsAsciiDigit(c))
        {
            int start = _pos;
            while (_pos < text.Length && char.IsAsciiDigit(text[_pos]))
                _pos++;
            return text[start.._pos];
        }

        _pos++;
        return c.ToString();
    }

    private static bool IsIdentifierStart(char c) => char.IsAsciiLetter(c);

    private LinkerException Error(string message)
    {
        int line = 1 + text.AsSpan(0, Math.Min(_pos, text.Length)).Count('\n');
        return new LinkerException($"WIT parse error at line {line}: {message}");
    }
}

// ── Resolver ───────────────────────────────────────────────────────────────────

internal sealed class WitResolver(List<WitAstPackage> packages)
{
    private int _nextId;
    private readonly Dictionary<(WitAstPackage, string), WitIface> _interfaces = [];
    private readonly Dictionary<WitIface, Dictionary<string, WitTDef>> _interfaceLookups = [];
    private readonly HashSet<(WitAstPackage, string)> _resolving = [];
    private readonly HashSet<(WitAstPackage, string)> _resolvingWorlds = [];
    private readonly Dictionary<WitPackageName, Dictionary<string, WitTDef>> _anonymous = [];
    private readonly Dictionary<WitPackageName, Dictionary<WitTDef, WitTDef>> _ownHandles = [];
    private readonly Dictionary<(WitAstPackage, string), WitResolvedWorld> _worlds = [];

    public WitResolvedWorld Resolve(string? worldName)
    {
        var main = packages[0];
        WitAstWorld world;
        if (worldName is not null)
        {
            world = main.Worlds.SingleOrDefault(w => w.Name == worldName)
                ?? throw new LinkerException($"WIT package {main.Name} has no world `{worldName}`.");
        }
        else
        {
            if (main.Worlds.Count != 1)
                throw new LinkerException($"WIT package {main.Name} has {main.Worlds.Count} worlds; name the world to use.");
            world = main.Worlds[0];
        }

        return ResolveWorldAst(main, world);
    }

    private WitTDef NewDef(string? name, object? owner, WitKind kind) => new(_nextId++, name, owner, kind);

    // ── Lookup ─────────────────────────────────────────────────────────────

    private (WitAstPackage Package, string Name) FindInterface(WitAstPackage from, WitUsePath path)
    {
        if (path.IsLocal)
        {
            if (from.Interfaces.Any(i => i.Name == path.Name))
                return (from, path.Name);
            if (from.TopLevelUses.FirstOrDefault(u => u.As == path.Name) is { Path: not null } use)
                return FindInterface(from, use.Path);
            throw new LinkerException($"WIT interface `{path.Name}` is not defined in package {from.Name}.");
        }

        var candidates = packages.Where(p => p.Name.Namespace == path.Namespace && p.Name.Name == path.Package &&
            p.Name.Version == path.Version).ToList();
        if (candidates.Count != 1)
        {
            throw new LinkerException(candidates.Count == 0
                ? $"WIT package {path.Namespace}:{path.Package}{(path.Version is null ? "" : "@" + path.Version)} is not defined in this file."
                : $"WIT package {path.Namespace}:{path.Package} is ambiguous; give a version.");
        }

        var package = candidates[0];
        RejectOwnPackage(from, package, path);
        if (!package.Interfaces.Any(i => i.Name == path.Name))
            throw new LinkerException($"WIT package {package.Name} has no interface `{path.Name}`.");
        return (package, path.Name);
    }

    /// <summary>A package naming itself by its full name is a dependency cycle in wit-parser.</summary>
    private static void RejectOwnPackage(WitAstPackage from, WitAstPackage package, WitUsePath path)
    {
        if (package == from)
        {
            throw new LinkerException(
                $"WIT `{path.Namespace}:{path.Package}/{path.Name}` in package {from.Name} refers to its own package; use `{path.Name}`.");
        }
    }

    private WitIface ResolveInterface(WitAstPackage package, string name)
    {
        if (_interfaces.TryGetValue((package, name), out var done))
            return done;
        if (!_resolving.Add((package, name)))
            throw new LinkerException($"WIT interface {package.Name.InterfaceId(name)} depends on itself.");

        var ast = package.Interfaces.Single(i => i.Name == name);
        var iface = new WitIface(name, package.Name);
        ResolveInterfaceBody(package, iface, ast.Items);
        _interfaces[(package, name)] = iface;
        _resolving.Remove((package, name));
        return iface;
    }

    private void ResolveInterfaceBody(WitAstPackage package, WitIface iface, List<WitAstItem> items)
    {
        var lookup = ResolveTypes(package, iface, items);
        foreach (var (typeName, def) in lookup)
            iface.Types.Add((typeName, def));
        _interfaceLookups[iface] = lookup;

        // Types and functions share an interface's namespace.
        var names = new HashSet<string>(lookup.Keys);
        void AddFunction(WitFunction function)
        {
            if (!names.Add(function.Name))
                throw new LinkerException($"WIT name `{function.Name}` is defined more than once.");
            iface.Functions.Add(function);
        }

        foreach (var item in items)
        {
            switch (item)
            {
                case WitAstFuncItem f:
                    AddFunction(ResolveFunction(package, lookup, f.Name, f.Func, WitFunctionKind.Freestanding, null));
                    break;
                case WitAstTypeDef { Body: WitAstResourceBody resource } def:
                    foreach (var func in resource.Funcs)
                        AddFunction(ResolveResourceFunction(package, lookup, def.Name, func));
                    break;
            }
        }
    }

    /// <summary>wit-parser's <c>resolve_types</c>: <c>use</c>d names first, then definitions in its topological order.</summary>
    private Dictionary<string, WitTDef> ResolveTypes(WitAstPackage package, object owner, List<WitAstItem> items)
    {
        var lookup = new Dictionary<string, WitTDef>();
        var order = new List<string>();
        void Define(string name, WitTDef def)
        {
            if (!lookup.TryAdd(name, def))
                throw new LinkerException($"WIT name `{name}` is defined more than once.");
            order.Add(name);
        }

        foreach (var use in items.OfType<WitAstUse>())
        {
            var (fromPackage, fromName) = FindInterface(package, use.From);
            var from = ResolveInterface(fromPackage, fromName);
            var fromLookup = _interfaceLookups[from];
            foreach (var (name, alias) in use.Names)
            {
                if (!fromLookup.TryGetValue(name, out var target))
                    throw new LinkerException($"WIT interface {from.Id} has no type `{name}`.");
                Define(alias ?? name, NewDef(alias ?? name, owner, new WitAliasKind(new WitRef(target))));
            }
        }

        var deps = new List<(string Name, List<string> Deps, WitAstTypeDef? Def)>();
        foreach (var item in items)
        {
            switch (item)
            {
                case WitAstTypeDef def:
                    if (deps.Any(d => d.Name == def.Name))
                        throw new LinkerException($"WIT name `{def.Name}` is defined more than once.");
                    var list = new List<string>();
                    CollectDeps(def.Body, list);
                    deps.Add((def.Name, list, def));
                    break;
                case WitAstUse use:
                    foreach (var (name, alias) in use.Names)
                    {
                        string key = alias ?? name;
                        int existing = deps.FindIndex(d => d.Name == key);
                        if (existing >= 0)
                            deps[existing] = (key, [], null);
                        else
                            deps.Add((key, [], null));
                    }

                    break;
            }
        }

        foreach (int index in TopoSort(deps.Select(d => (d.Name, d.Deps)).ToList()))
        {
            if (deps[index].Def is not { } def)
                continue;
            var typeDef = NewDef(def.Name, owner, new WitResourceKind());
            typeDef.Kind = ResolveTypeBody(package, lookup, def.Body);
            Define(def.Name, typeDef);
        }

        // `lookup` must iterate in definition order.
        var ordered = new Dictionary<string, WitTDef>();
        foreach (string name in order)
            ordered[name] = lookup[name];
        return ordered;
    }

    /// <summary>wit-parser's toposort: always emit the earliest-declared item whose dependencies are done.</summary>
    private static List<int> TopoSort(List<(string Name, List<string> Deps)> items)
    {
        var index = new Dictionary<string, int>();
        for (int i = 0; i < items.Count; i++)
            index[items[i].Name] = i;
        var remaining = new int[items.Count];
        var reverse = new List<int>[items.Count];
        for (int i = 0; i < items.Count; i++)
            reverse[i] = [];
        for (int i = 0; i < items.Count; i++)
        {
            remaining[i] = items[i].Deps.Count;
            foreach (string dep in items[i].Deps)
            {
                if (!index.TryGetValue(dep, out int j))
                    throw new LinkerException($"WIT type `{dep}` is not defined.");
                reverse[j].Add(i);
            }
        }

        var ready = new SortedSet<int>();
        for (int i = 0; i < items.Count; i++)
        {
            if (remaining[i] == 0)
                ready.Add(i);
        }

        var order = new List<int>(items.Count);
        while (ready.Count > 0)
        {
            int next = ready.Min;
            ready.Remove(next);
            order.Add(next);
            foreach (int dependent in reverse[next])
            {
                if (--remaining[dependent] == 0)
                    ready.Add(dependent);
            }
        }

        if (order.Count != items.Count)
            throw new LinkerException("WIT types form a cycle.");
        return order;
    }

    private static void CollectDeps(WitAstTypeBody body, List<string> deps)
    {
        switch (body)
        {
            case WitAstAliasBody a: CollectDeps(a.Target, deps); break;
            case WitAstRecordBody r: foreach (var (_, t) in r.Fields) CollectDeps(t, deps); break;
            case WitAstVariantBody v: foreach (var (_, t) in v.Cases) if (t is not null) CollectDeps(t, deps); break;
        }
    }

    private static void CollectDeps(WitAstType type, List<string> deps)
    {
        switch (type)
        {
            case WitAstName n: deps.Add(n.Name); break;
            case WitAstOwn o: deps.Add(o.Resource); break;
            case WitAstBorrow b: deps.Add(b.Resource); break;
            case WitAstList l: CollectDeps(l.Element, deps); break;
            case WitAstOption o: CollectDeps(o.Element, deps); break;
            case WitAstMap m: CollectDeps(m.Key, deps); CollectDeps(m.Value, deps); break;
            case WitAstResult r:
                if (r.Ok is not null) CollectDeps(r.Ok, deps);
                if (r.Err is not null) CollectDeps(r.Err, deps);
                break;
            case WitAstTuple t: foreach (var e in t.Types) CollectDeps(e, deps); break;
            case WitAstFuture { Element: { } f }: CollectDeps(f, deps); break;
            case WitAstStream { Element: { } s }: CollectDeps(s, deps); break;
        }
    }

    private WitKind ResolveTypeBody(WitAstPackage package, Dictionary<string, WitTDef> lookup, WitAstTypeBody body) => body switch
    {
        WitAstAliasBody a => ResolveKind(package, lookup, a.Target),
        WitAstRecordBody r => new WitRecordKind([.. r.Fields.Select(f => (f.Name, ResolveType(package, lookup, f.Type)))]),
        WitAstVariantBody v => new WitVariantKind([.. v.Cases.Select(c => (c.Name, c.Type is null ? null : ResolveType(package, lookup, c.Type)))]),
        WitAstEnumBody e => new WitEnumKind(e.Cases),
        WitAstFlagsBody f => new WitFlagsKind(f.Flags),
        WitAstResourceBody => new WitResourceKind(),
        _ => throw new InvalidOperationException(),
    };

    /// <summary>
    /// The kind a type expression defines (wit-parser's <c>resolve_type_def</c>): a named <c>type x = list&lt;u8&gt;</c>
    /// is itself a list, and <c>type x = y</c> keeps naming <c>y</c> without becoming an <c>own</c> handle.
    /// </summary>
    private WitKind ResolveKind(WitAstPackage package, Dictionary<string, WitTDef> lookup, WitAstType type)
    {
        switch (type)
        {
            case WitAstPrimitive p: return new WitAliasKind(new WitPrimitive(p.Code));
            case WitAstName n: return new WitAliasKind(new WitRef(Lookup(lookup, n.Name)));
            case WitAstOwn o: return new WitOwnKind(RequireResource(lookup, o.Resource));
            case WitAstBorrow b: return new WitBorrowKind(RequireResource(lookup, b.Resource));
            case WitAstList l: return new WitListKind(ResolveType(package, lookup, l.Element), l.Length);
            case WitAstMap m: return new WitMapKind(ResolveType(package, lookup, m.Key), ResolveType(package, lookup, m.Value));
            case WitAstOption o: return new WitOptionKind(ResolveType(package, lookup, o.Element));
            case WitAstResult r:
                return new WitResultKind(
                    r.Ok is null ? null : ResolveType(package, lookup, r.Ok),
                    r.Err is null ? null : ResolveType(package, lookup, r.Err));
            case WitAstTuple t: return new WitTupleKind([.. t.Types.Select(e => ResolveType(package, lookup, e))]);
            case WitAstFuture f: return new WitFutureKind(f.Element is null ? null : ResolveType(package, lookup, f.Element));
            case WitAstStream s: return new WitStreamKind(s.Element is null ? null : ResolveType(package, lookup, s.Element));
            default: throw new InvalidOperationException();
        }
    }

    private static WitTDef RequireResource(Dictionary<string, WitTDef> lookup, string name)
    {
        var def = Lookup(lookup, name);
        return IsResource(def) ? def : throw new LinkerException($"WIT type `{name}` is not a resource.");
    }

    private static WitTDef Lookup(Dictionary<string, WitTDef> lookup, string name) =>
        lookup.TryGetValue(name, out var def) ? def : throw new LinkerException($"WIT type `{name}` is not defined.");

    private static bool IsResource(WitTDef def)
    {
        while (true)
        {
            switch (def.Kind)
            {
                case WitResourceKind: return true;
                case WitAliasKind { Target: WitRef r }: def = r.Def; break;
                default: return false;
            }
        }
    }

    /// <summary>A value-position type (wit-parser's <c>resolve_type</c> + <c>update_ty</c>): anonymous kinds are interned, and references to resources become anonymous <c>own</c> handles.</summary>
    private WitVal ResolveType(WitAstPackage package, Dictionary<string, WitTDef> lookup, WitAstType type) => type switch
    {
        WitAstPrimitive p => new WitPrimitive(p.Code),
        WitAstName n => Value(package, Lookup(lookup, n.Name)),
        WitAstOwn o => Value(package, RequireResource(lookup, o.Resource)),
        _ => Anonymous(package, ResolveKind(package, lookup, type)),
    };

    private WitRef Value(WitAstPackage package, WitTDef def)
    {
        if (!IsResource(def))
            return new WitRef(def);
        var handles = Cache(_ownHandles, package.Name);
        if (!handles.TryGetValue(def, out var handle))
            handles[def] = handle = NewDef(null, null, new WitOwnKind(def));
        return new WitRef(handle);
    }

    private WitRef Anonymous(WitAstPackage package, WitKind kind)
    {
        var interned = Cache(_anonymous, package.Name);
        string key = WitKindKey.Of(kind);
        if (!interned.TryGetValue(key, out var def))
            interned[key] = def = NewDef(null, null, kind);
        return new WitRef(def);
    }

    private static Dictionary<TKey, TValue> Cache<TKey, TValue>(Dictionary<WitPackageName, Dictionary<TKey, TValue>> caches, WitPackageName package)
        where TKey : notnull
    {
        if (!caches.TryGetValue(package, out var cache))
            caches[package] = cache = [];
        return cache;
    }

    private WitFunction ResolveFunction(
        WitAstPackage package, Dictionary<string, WitTDef> lookup, string name, WitAstFunc func, WitFunctionKind kind, WitTDef? resource)
    {
        var parameters = new List<(string, WitVal)>();
        if (kind == WitFunctionKind.Method)
            parameters.Add(("self", Anonymous(package, new WitBorrowKind(resource!))));
        foreach (var (paramName, paramType) in func.Params)
        {
            if (parameters.Any(p => p.Item1 == paramName))
                throw new LinkerException($"WIT parameter `{paramName}` is defined more than once.");
            parameters.Add((paramName, ResolveType(package, lookup, paramType)));
        }

        WitVal? result = kind == WitFunctionKind.Constructor && func.Result is null
            ? Value(package, resource!)
            : func.Result is null ? null : ResolveType(package, lookup, func.Result);
        if (result is not null && HasBorrow(result))
            throw new LinkerException($"WIT function `{name}` returns a type which contains a `borrow<T>`, which is not supported.");
        return new WitFunction(name, func.Async, kind, resource, parameters, result);
    }

    /// <summary>wit-parser's <c>type_has_borrow</c>.</summary>
    private static bool HasBorrow(WitVal? type) => type is WitRef { Def: var def } && def.Kind switch
    {
        WitBorrowKind => true,
        WitAliasKind a => HasBorrow(a.Target),
        WitRecordKind r => r.Fields.Any(f => HasBorrow(f.Type)),
        WitVariantKind v => v.Cases.Any(c => HasBorrow(c.Type)),
        WitTupleKind t => t.Types.Any(HasBorrow),
        WitListKind l => HasBorrow(l.Element),
        WitOptionKind o => HasBorrow(o.Element),
        WitMapKind m => HasBorrow(m.Key) || HasBorrow(m.Value),
        WitResultKind r => HasBorrow(r.Ok) || HasBorrow(r.Err),
        WitFutureKind f => HasBorrow(f.Element),
        WitStreamKind s => HasBorrow(s.Element),
        _ => false,
    };

    private WitFunction ResolveResourceFunction(WitAstPackage package, Dictionary<string, WitTDef> lookup, string resourceName, WitAstResourceFunc func)
    {
        var resource = lookup[resourceName];
        string name = func.Kind switch
        {
            WitFunctionKind.Constructor => $"[constructor]{resourceName}",
            WitFunctionKind.Static => $"[static]{resourceName}.{func.Name}",
            _ => $"[method]{resourceName}.{func.Name}",
        };
        return ResolveFunction(package, lookup, name, func.Func, func.Kind, resource);
    }

    // ── Worlds ─────────────────────────────────────────────────────────────

    private WitResolvedWorld ResolveWorldAst(WitAstPackage package, WitAstWorld ast)
    {
        if (_worlds.TryGetValue((package, ast.Name), out var done))
            return done;
        if (!_resolvingWorlds.Add((package, ast.Name)))
            throw new LinkerException($"WIT world `{ast.Name}` includes itself.");

        var world = new WitWorldDef(ast.Name, package.Name);
        var imports = new List<(WitWorldKey Key, WitWorldItem Item)>();
        var exports = new List<(WitWorldKey Key, WitWorldItem Item)>();

        var lookup = ResolveTypes(package, world, ast.Items);
        foreach (var (name, def) in lookup)
            imports.Add((new WitWorldKey(name, null), new WitWorldType(def)));

        foreach (var astItem in ast.Items)
        {
            // A world-level resource's functions are world imports, in source order (wit-parser's resolve_world).
            if (astItem is WitAstTypeDef { Body: WitAstResourceBody resource } resourceDef)
            {
                foreach (var func in resource.Funcs)
                {
                    var function = ResolveResourceFunction(package, lookup, resourceDef.Name, func);
                    if (imports.Any(e => e.Key.Name == function.Name))
                        throw new LinkerException($"WIT world `{ast.Name}` imports `{function.Name}` more than once.");
                    imports.Add((new WitWorldKey(function.Name, null), new WitWorldFunction(function)));
                }

                continue;
            }

            if (astItem is not WitAstImportExport item)
                continue;
            var target = item.Import ? imports : exports;
            (WitWorldKey, WitWorldItem) entry;
            if (item.Path is { } path)
            {
                var (p, n) = FindInterface(package, path);
                var iface = ResolveInterface(p, n);
                entry = (new WitWorldKey(iface.Id, iface), new WitWorldInterface(iface));
            }
            else if (item.Func is { } func)
            {
                entry = (new WitWorldKey(item.Name!, null), new WitWorldFunction(ResolveFunction(package, lookup, item.Name!, func, WitFunctionKind.Freestanding, null)));
            }
            else
            {
                var iface = new WitIface(null, package.Name);
                ResolveInterfaceBody(package, iface, item.InlineInterface!);
                entry = (new WitWorldKey(item.Name!, null), new WitWorldInterface(iface));
            }

            if (target.Any(e => e.Key.Name == entry.Item1.Name))
                throw new LinkerException($"WIT world `{ast.Name}` {(item.Import ? "imports" : "exports")} `{entry.Item1.Name}` more than once.");
            target.Add(entry);
        }

        foreach (var include in ast.Items.OfType<WitAstInclude>())
        {
            var (includePackage, includeWorld) = FindWorld(package, include.From);
            var included = ResolveWorldAst(includePackage, includeWorld);
            Include(included.Imports, imports, include.With);
            Include(included.Exports, exports, include.With);
        }

        var resolved = Elaborate(world, imports, exports);
        _worlds[(package, ast.Name)] = resolved;
        _resolvingWorlds.Remove((package, ast.Name));
        return resolved;
    }

    private static void Include(
        List<(WitWorldKey Key, WitWorldItem Item)> from, List<(WitWorldKey Key, WitWorldItem Item)> into, List<(string Name, string As)> with)
    {
        foreach (var (key, item) in from)
        {
            if (key.Interface is not null)
            {
                if (!into.Any(e => e.Key.Name == key.Name))
                    into.Add((key, item));
                continue;
            }

            string name = with.FirstOrDefault(w => w.Name == key.Name).As ?? key.Name;
            if (into.Any(e => e.Key.Name == name))
                throw new LinkerException($"WIT include brings in `{name}`, which the world already has.");
            var renamed = item is WitWorldFunction f ? new WitWorldFunction(f.Function with { Name = name }) : item;
            into.Add((new WitWorldKey(name, null), renamed));
        }
    }

    private (WitAstPackage, WitAstWorld) FindWorld(WitAstPackage from, WitUsePath path)
    {
        var package = path.IsLocal
            ? from
            : packages.SingleOrDefault(p => p.Name.Namespace == path.Namespace && p.Name.Name == path.Package &&
                p.Name.Version == path.Version)
              ?? throw new LinkerException(
                  $"WIT package {path.Namespace}:{path.Package}{(path.Version is null ? "" : "@" + path.Version)} is not defined in this file.");
        if (!path.IsLocal)
            RejectOwnPackage(from, package, path);
        var world = package.Worlds.SingleOrDefault(w => w.Name == path.Name)
            ?? throw new LinkerException($"WIT package {package.Name} has no world `{path.Name}`.");
        return (package, world);
    }

    private static WitIface? TypeInterfaceDep(WitTDef def) =>
        def.Kind is WitAliasKind { Target: WitRef { Def: var target } } && target.Owner != def.Owner && target.Owner is WitIface owner
            ? owner
            : null;

    private static IEnumerable<WitIface> DirectDeps(WitIface iface) =>
        iface.Types.Select(t => TypeInterfaceDep(t.Def)).OfType<WitIface>();

    /// <summary>wit-parser's <c>elaborate_world</c>: imports gain the interfaces they depend on, exports' dependencies are imported.</summary>
    private static WitResolvedWorld Elaborate(
        WitWorldDef world, List<(WitWorldKey Key, WitWorldItem Item)> imports, List<(WitWorldKey Key, WitWorldItem Item)> exports)
    {
        int SortKey(WitWorldItem item) => item switch
        {
            WitWorldInterface => 0,
            WitWorldType { Def: var def } => def.Kind is WitAliasKind { Target: WitRef { Def: var t } } && t.Owner != def.Owner ? 1 : 2,
            WitWorldFunction { Function: var f } => f.Resource is null ? 3 : 4,
            _ => 5,
        };

        var newImports = new List<(WitWorldKey Key, WitWorldItem Item)>();
        void ImportInterface(WitWorldKey key, WitIface iface)
        {
            if (newImports.Any(e => e.Key.Name == key.Name))
                return;
            foreach (var dep in DirectDeps(iface))
                ImportInterface(new WitWorldKey(dep.Id, dep), dep);
            newImports.Add((key, new WitWorldInterface(iface)));
        }

        foreach (var (key, item) in imports.OrderBy(e => SortKey(e.Item)))
        {
            switch (item)
            {
                case WitWorldInterface i:
                    ImportInterface(key, i.Interface);
                    break;
                case WitWorldType t:
                    if (TypeInterfaceDep(t.Def) is { } dep)
                        ImportInterface(new WitWorldKey(dep.Id, dep), dep);
                    newImports.Add((key, item));
                    break;
                default:
                    newImports.Add((key, item));
                    break;
            }
        }

        var newExports = new List<(WitWorldKey Key, WitWorldItem Item)>();
        var exportInterfaces = new List<(WitWorldKey Key, WitWorldItem Item)>();
        foreach (var entry in exports)
        {
            if (entry.Item is WitWorldInterface)
                exportInterfaces.Add(entry);
            else
                newExports.Add(entry);
        }

        var requiredImports = new HashSet<WitIface>();
        bool AddExport(WitWorldKey key, WitIface iface, bool add)
        {
            if (newExports.Any(e => e.Key.Name == key.Name))
                return add;
            if (!add && requiredImports.Contains(iface))
                return true;
            foreach (var dep in DirectDeps(iface))
            {
                var depKey = new WitWorldKey(dep.Id, dep);
                if (!AddExport(depKey, dep, add && exportInterfaces.Any(e => e.Key.Name == depKey.Name)))
                    return false;
            }

            if (add)
            {
                if (requiredImports.Contains(iface))
                    return false;
                newExports.Add((key, new WitWorldInterface(iface)));
            }
            else
            {
                requiredImports.Add(iface);
                if (!newImports.Any(e => e.Key.Name == key.Name))
                    newImports.Add((key, new WitWorldInterface(iface)));
            }

            return true;
        }

        foreach (var (key, item) in exportInterfaces)
        {
            if (!AddExport(key, ((WitWorldInterface)item).Interface, true))
                throw new LinkerException($"WIT world `{world.Name}` exports `{key.Name}`, whose dependencies would have to be both imported and exported.");
        }

        return new WitResolvedWorld(world, [.. newImports.OrderBy(e => SortKey(e.Item))], newExports);
    }
}

/// <summary>Structural keys for anonymous types: nested named types compare by identity, as wit-parser's ids do.</summary>
internal static class WitKindKey
{
    public static string Of(WitKind kind)
    {
        var sb = new StringBuilder();
        Append(sb, kind);
        return sb.ToString();
    }

    public static string Of(WitVal? type) => type switch
    {
        null => "_",
        WitPrimitive p => "p" + p.Code.ToString(CultureInfo.InvariantCulture),
        WitRef r => "#" + r.Def.Id.ToString(CultureInfo.InvariantCulture),
        _ => throw new InvalidOperationException(),
    };

    private static void Append(StringBuilder sb, WitKind kind)
    {
        switch (kind)
        {
            case WitListKind l: sb.Append("list(").Append(Of(l.Element)).Append(',').Append(l.Length).Append(')'); break;
            case WitMapKind m: sb.Append("map(").Append(Of(m.Key)).Append(',').Append(Of(m.Value)).Append(')'); break;
            case WitOptionKind o: sb.Append("option(").Append(Of(o.Element)).Append(')'); break;
            case WitResultKind r: sb.Append("result(").Append(Of(r.Ok)).Append(',').Append(Of(r.Err)).Append(')'); break;
            case WitTupleKind t: sb.Append("tuple(").AppendJoin(',', t.Types.Select(Of)).Append(')'); break;
            case WitFutureKind f: sb.Append("future(").Append(Of(f.Element)).Append(')'); break;
            case WitStreamKind s: sb.Append("stream(").Append(Of(s.Element)).Append(')'); break;
            case WitOwnKind o: sb.Append("own(#").Append(o.Resource.Id).Append(')'); break;
            case WitBorrowKind b: sb.Append("borrow(#").Append(b.Resource.Id).Append(')'); break;
            case WitRecordKind r: sb.Append("record(").AppendJoin(',', r.Fields.Select(f => f.Name + ":" + Of(f.Type))).Append(')'); break;
            case WitVariantKind v: sb.Append("variant(").AppendJoin(',', v.Cases.Select(c => c.Name + ":" + Of(c.Type))).Append(')'); break;
            case WitEnumKind e: sb.Append("enum(").AppendJoin(',', e.Cases).Append(')'); break;
            case WitFlagsKind f: sb.Append("flags(").AppendJoin(',', f.Flags).Append(')'); break;
            case WitAliasKind a: sb.Append("type(").Append(Of(a.Target)).Append(')'); break;
            case WitResourceKind: sb.Append("resource"); break;
            default: throw new InvalidOperationException();
        }
    }
}
