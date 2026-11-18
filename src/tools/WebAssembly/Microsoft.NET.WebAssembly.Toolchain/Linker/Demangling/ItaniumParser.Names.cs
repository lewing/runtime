// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from LLVM's llvm/include/llvm/Demangle/ItaniumDemangle.h (AbstractManglingParser)
// (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141, LLVM 23), which is part of the LLVM Project,
// under the Apache License v2.0 with LLVM Exceptions. See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

// SA1136 and CA1859: this file is a line-for-line port kept diffable against LLVM. Its compact enum lists mirror
// LLVM's enum declarations, and its parser methods return Node where LLVM returns Node*; one enum value per line,
// or return types narrowed to concrete node classes, would diverge from LLVM for no measurable gain.
#pragma warning disable SA1136, CA1859

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

internal sealed partial class ItaniumParser
{
    private readonly string _text;
    private int _first;
    private int _last;

    private readonly List<Node> _names = [];
    private readonly List<Node> _subs = [];
    private List<Node> _outerTemplateParams = [];
    private List<List<Node>?> _templateParams = [];
    private readonly List<ForwardTemplateReference> _forwardTemplateRefs = [];
    private bool _tryToParseTemplateArgs = true;
    private bool _permitForwardTemplateReferences;
    private bool _hasIncompleteTemplateParameterTracking;
    private int _parsingLambdaParamsAtLevel = -1;
    private readonly uint[] _numSyntheticTemplateParameters = new uint[3];

    public ItaniumParser(string text, int first, int last)
    {
        _text = text;
        _first = first;
        _last = last;
    }

    private sealed class NameState(ItaniumParser enclosing)
    {
        public bool CtorDtorConversion;
        public bool EndsWithTemplateArgs;
        public Qualifiers CVQualifiers = Qualifiers.None;
        public FunctionRefQual ReferenceQualifier = FunctionRefQual.None;
        public readonly int ForwardTemplateRefsBegin = enclosing._forwardTemplateRefs.Count;
        public bool HasExplicitObjectParameter;
    }

    /// <summary><c>ScopedTemplateParamList</c>: pushes a fresh list and trims back to the old depth on dispose.</summary>
    private readonly struct ScopedTemplateParamList : IDisposable
    {
        private readonly ItaniumParser _parser;
        private readonly int _oldCount;
        public readonly List<Node> Params;

        public ScopedTemplateParamList(ItaniumParser parser)
        {
            _parser = parser;
            _oldCount = parser._templateParams.Count;
            Params = [];
            parser._templateParams.Add(Params);
        }

        public void Dispose() => _parser._templateParams.RemoveRange(_oldCount, _parser._templateParams.Count - _oldCount);
    }

    /// <summary><c>SaveTemplateParams</c>: moves both template-parameter stacks aside for the scope.</summary>
    private readonly struct SaveTemplateParams : IDisposable
    {
        private readonly ItaniumParser _parser;
        private readonly List<List<Node>?> _oldParams;
        private readonly List<Node> _oldOuter;

        public SaveTemplateParams(ItaniumParser parser)
        {
            _parser = parser;
            _oldParams = parser._templateParams;
            _oldOuter = parser._outerTemplateParams;
            parser._templateParams = [];
            parser._outerTemplateParams = [];
        }

        public void Dispose()
        {
            _parser._templateParams = _oldParams;
            _parser._outerTemplateParams = _oldOuter;
        }
    }

    private bool ResolveForwardTemplateRefs(NameState state)
    {
        int i = state.ForwardTemplateRefsBegin;
        int e = _forwardTemplateRefs.Count;
        for (; i < e; ++i)
        {
            int index = _forwardTemplateRefs[i].Index;
            if (_templateParams.Count == 0 || _templateParams[0] is not { } first || index >= first.Count)
                return true;
            _forwardTemplateRefs[i].Ref = first[index];
        }

        _forwardTemplateRefs.RemoveRange(state.ForwardTemplateRefsBegin, _forwardTemplateRefs.Count - state.ForwardTemplateRefsBegin);
        return false;
    }

    // ── Cursor ────────────────────────────────────────────────────────────

    private NodeArray PopTrailingNodeArray(int fromPosition)
    {
        int count = _names.Count - fromPosition;
        if (count == 0)
            return new NodeArray([]);
        var items = new Node[count];
        _names.CopyTo(fromPosition, items, 0, count);
        _names.RemoveRange(fromPosition, count);
        return new NodeArray(items);
    }

    private bool ConsumeIf(string s)
    {
        if (_last - _first >= s.Length && string.CompareOrdinal(_text, _first, s, 0, s.Length) == 0)
        {
            _first += s.Length;
            return true;
        }

        return false;
    }

    private bool ConsumeIf(char c)
    {
        if (_first != _last && _text[_first] == c)
        {
            ++_first;
            return true;
        }

        return false;
    }

    private char Consume() => _first != _last ? _text[_first++] : '\0';

    private char Look(int lookahead = 0) => _last - _first <= lookahead ? '\0' : _text[_first + lookahead];

    private int NumLeft => _last - _first;

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private bool IsTemplateParamDecl() => Look() == 'T' && "yptnk".Contains(Look(1), StringComparison.Ordinal) && Look(1) != '\0';

    private string Slice(int start, int end) => _text[start..end];

    // ── Names ─────────────────────────────────────────────────────────────

    private Node? ParseName(NameState? state = null)
    {
        DemangleDepth.Enter();
        Node? result = ParseNameCore(state);
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseNameCore(NameState? state)
    {
        if (Look() == 'N')
            return ParseNestedName(state);
        if (Look() == 'Z')
            return ParseLocalName(state);

        bool isSubst = false;
        Node? result = ParseUnscopedName(state, ref isSubst);
        if (result is null)
            return null;

        if (Look() == 'I')
        {
            if (!isSubst)
                _subs.Add(result);
            Node? ta = ParseTemplateArgs(state is not null);
            if (ta is null)
                return null;
            if (state is not null)
                state.EndsWithTemplateArgs = true;
            result = new NameWithTemplateArgs(result, ta);
        }
        else if (isSubst)
        {
            return null;
        }

        return result;
    }

    private Node? ParseLocalName(NameState? state)
    {
        if (!ConsumeIf('Z'))
            return null;
        Node? encoding = ParseEncoding();
        if (encoding is null || !ConsumeIf('E'))
            return null;

        if (ConsumeIf('s'))
        {
            _first = ParseDiscriminator(_text, _first, _last);
            return new LocalName(encoding, new NameType("string literal"));
        }

        using var saveTemplateParams = new SaveTemplateParams(this);

        if (ConsumeIf('d'))
        {
            ParseNumber(true);
            if (!ConsumeIf('_'))
                return null;
            Node? n = ParseName(state);
            return n is null ? null : new LocalName(encoding, n);
        }

        Node? entity = ParseName(state);
        if (entity is null)
            return null;
        _first = ParseDiscriminator(_text, _first, _last);
        return new LocalName(encoding, entity);
    }

    private Node? ParseUnscopedName(NameState? state, ref bool isSubst)
    {
        Node? std = null;
        if (ConsumeIf("St"))
            std = new NameType("std");

        Node? res = null;
        ModuleName? module = null;
        if (Look() == 'S')
        {
            Node? s = ParseSubstitution();
            if (s is null)
                return null;
            if (s is ModuleName m)
            {
                module = m;
            }
            else if (std is null)
            {
                res = s;
                isSubst = true;
            }
            else
            {
                return null;
            }
        }

        if (res is null || std is not null)
            res = ParseUnqualifiedName(state, std, module);
        return res;
    }

    private Node? ParseUnqualifiedName(NameState? state, Node? scope, ModuleName? module)
    {
        if (ParseModuleNameOpt(ref module))
            return null;
        bool isMemberLikeFriend = scope is not null && ConsumeIf('F');
        ConsumeIf('L');
        Node? result;
        if (Look() is >= '1' and <= '9')
        {
            result = ParseSourceName(state);
        }
        else if (Look() == 'U')
        {
            result = ParseUnnamedTypeName(state);
        }
        else if (ConsumeIf("DC"))
        {
            int bindingsBegin = _names.Count;
            do
            {
                Node? binding = ParseSourceName(state);
                if (binding is null)
                    return null;
                _names.Add(binding);
            }
            while (!ConsumeIf('E'));
            result = new StructuredBindingName(PopTrailingNodeArray(bindingsBegin));
        }
        else if (Look() is 'C' or 'D')
        {
            if (scope is null || module is not null)
                return null;
            result = ParseCtorDtorName(ref scope, state);
        }
        else
        {
            result = ParseOperatorName(state);
        }

        if (result is not null && module is not null)
            result = new ModuleEntity(module, result);
        if (result is not null)
            result = ParseAbiTags(result);
        if (result is not null && isMemberLikeFriend)
            result = new MemberLikeFriendName(scope!, result);
        else if (result is not null && scope is not null)
            result = new NestedName(scope, result);
        return result;
    }

    private bool ParseModuleNameOpt(ref ModuleName? module)
    {
        while (ConsumeIf('W'))
        {
            bool isPartition = ConsumeIf('P');
            Node? sub = ParseSourceName(null);
            if (sub is null)
                return true;
            module = new ModuleName(module, sub, isPartition);
            _subs.Add(module);
        }

        return false;
    }

    private Node? ParseUnnamedTypeName(NameState? state)
    {
        if (state is not null)
            _templateParams.Clear();

        if (ConsumeIf("Ut"))
        {
            string count = ParseNumber();
            return ConsumeIf('_') ? new UnnamedTypeName(count) : null;
        }

        if (ConsumeIf("Ul"))
        {
            int savedLevel = _parsingLambdaParamsAtLevel;
            _parsingLambdaParamsAtLevel = _templateParams.Count;
            try
            {
                using var lambdaTemplateParams = new ScopedTemplateParamList(this);
                int paramsBegin = _names.Count;
                while (IsTemplateParamDecl())
                {
                    Node? t = ParseTemplateParamDecl(lambdaTemplateParams.Params);
                    if (t is null)
                        return null;
                    _names.Add(t);
                }

                NodeArray tempParams = PopTrailingNodeArray(paramsBegin);
                if (tempParams.IsEmpty)
                    _templateParams.RemoveAt(_templateParams.Count - 1);

                Node? requires1 = null;
                if (ConsumeIf('Q'))
                {
                    requires1 = ParseConstraintExpr();
                    if (requires1 is null)
                        return null;
                }

                if (!ConsumeIf("v"))
                {
                    do
                    {
                        Node? p = ParseType();
                        if (p is null)
                            return null;
                        _names.Add(p);
                    }
                    while (Look() != 'E' && Look() != 'Q');
                }

                NodeArray @params = PopTrailingNodeArray(paramsBegin);
                Node? requires2 = null;
                if (ConsumeIf('Q'))
                {
                    requires2 = ParseConstraintExpr();
                    if (requires2 is null)
                        return null;
                }

                if (!ConsumeIf('E'))
                    return null;
                string count = ParseNumber();
                if (!ConsumeIf('_'))
                    return null;
                return new ClosureTypeName(tempParams, requires1, @params, requires2, count);
            }
            finally
            {
                _parsingLambdaParamsAtLevel = savedLevel;
            }
        }

        if (ConsumeIf("Ub"))
        {
            ParseNumber();
            return ConsumeIf('_') ? new NameType("'block-literal'") : null;
        }

        return null;
    }

    private Node? ParseSourceName(NameState? state)
    {
        _ = state;
        if (ParsePositiveInteger(out ulong length))
            return null;
        if ((ulong)NumLeft < length || length == 0)
            return null;
        string name = Slice(_first, _first + (int)length);
        _first += (int)length;
        if (name.StartsWith("_GLOBAL__N", StringComparison.Ordinal))
            return new NameType("(anonymous namespace)");
        return new NameType(name);
    }

    // ── Operators ─────────────────────────────────────────────────────────

    internal enum OIKind : byte { Prefix, Postfix, Binary, Array, Member, New, Del, Call, CCast, Conditional, NameOnly, NamedCast, OfIdOp, Unnameable = NamedCast }

    internal sealed record OperatorInfo(string Enc, OIKind Kind, bool Flag, Prec Prec, string Name)
    {
        // The name without "operator" and one following space, computed once for the static table.
        public string Symbol { get; } = Kind < OIKind.Unnameable ? SymbolOf(Name) : Name;

        private static string SymbolOf(string name)
        {
            ReadOnlySpan<char> res = name.AsSpan("operator".Length);
            if (res.StartsWith(' '))
                res = res[1..];
            return res.ToString();
        }
    }

    private static readonly OperatorInfo[] Ops =
    [
        new("aN", OIKind.Binary, false, Prec.Assign, "operator&="),
        new("aS", OIKind.Binary, false, Prec.Assign, "operator="),
        new("aa", OIKind.Binary, false, Prec.AndIf, "operator&&"),
        new("ad", OIKind.Prefix, false, Prec.Unary, "operator&"),
        new("an", OIKind.Binary, false, Prec.And, "operator&"),
        new("at", OIKind.OfIdOp, true, Prec.Unary, "alignof "),
        new("aw", OIKind.NameOnly, false, Prec.Primary, "operator co_await"),
        new("az", OIKind.OfIdOp, false, Prec.Unary, "alignof "),
        new("cc", OIKind.NamedCast, false, Prec.Postfix, "const_cast"),
        new("cl", OIKind.Call, false, Prec.Postfix, "operator()"),
        new("cm", OIKind.Binary, false, Prec.Comma, "operator,"),
        new("co", OIKind.Prefix, false, Prec.Unary, "operator~"),
        new("cp", OIKind.Call, true, Prec.Postfix, "operator()"),
        new("cv", OIKind.CCast, false, Prec.Cast, "operator"),
        new("dV", OIKind.Binary, false, Prec.Assign, "operator/="),
        new("da", OIKind.Del, true, Prec.Unary, "operator delete[]"),
        new("dc", OIKind.NamedCast, false, Prec.Postfix, "dynamic_cast"),
        new("de", OIKind.Prefix, false, Prec.Unary, "operator*"),
        new("dl", OIKind.Del, false, Prec.Unary, "operator delete"),
        new("ds", OIKind.Member, false, Prec.PtrMem, "operator.*"),
        new("dt", OIKind.Member, false, Prec.Postfix, "operator."),
        new("dv", OIKind.Binary, false, Prec.Assign, "operator/"),
        new("eO", OIKind.Binary, false, Prec.Assign, "operator^="),
        new("eo", OIKind.Binary, false, Prec.Xor, "operator^"),
        new("eq", OIKind.Binary, false, Prec.Equality, "operator=="),
        new("ge", OIKind.Binary, false, Prec.Relational, "operator>="),
        new("gt", OIKind.Binary, false, Prec.Relational, "operator>"),
        new("ix", OIKind.Array, false, Prec.Postfix, "operator[]"),
        new("lS", OIKind.Binary, false, Prec.Assign, "operator<<="),
        new("le", OIKind.Binary, false, Prec.Relational, "operator<="),
        new("ls", OIKind.Binary, false, Prec.Shift, "operator<<"),
        new("lt", OIKind.Binary, false, Prec.Relational, "operator<"),
        new("mI", OIKind.Binary, false, Prec.Assign, "operator-="),
        new("mL", OIKind.Binary, false, Prec.Assign, "operator*="),
        new("mi", OIKind.Binary, false, Prec.Additive, "operator-"),
        new("ml", OIKind.Binary, false, Prec.Multiplicative, "operator*"),
        new("mm", OIKind.Postfix, false, Prec.Postfix, "operator--"),
        new("na", OIKind.New, true, Prec.Unary, "operator new[]"),
        new("ne", OIKind.Binary, false, Prec.Equality, "operator!="),
        new("ng", OIKind.Prefix, false, Prec.Unary, "operator-"),
        new("nt", OIKind.Prefix, false, Prec.Unary, "operator!"),
        new("nw", OIKind.New, false, Prec.Unary, "operator new"),
        new("oR", OIKind.Binary, false, Prec.Assign, "operator|="),
        new("oo", OIKind.Binary, false, Prec.OrIf, "operator||"),
        new("or", OIKind.Binary, false, Prec.Ior, "operator|"),
        new("pL", OIKind.Binary, false, Prec.Assign, "operator+="),
        new("pl", OIKind.Binary, false, Prec.Additive, "operator+"),
        new("pm", OIKind.Member, true, Prec.PtrMem, "operator->*"),
        new("pp", OIKind.Postfix, false, Prec.Postfix, "operator++"),
        new("ps", OIKind.Prefix, false, Prec.Unary, "operator+"),
        new("pt", OIKind.Member, true, Prec.Postfix, "operator->"),
        new("qu", OIKind.Conditional, false, Prec.Conditional, "operator?"),
        new("rM", OIKind.Binary, false, Prec.Assign, "operator%="),
        new("rS", OIKind.Binary, false, Prec.Assign, "operator>>="),
        new("rc", OIKind.NamedCast, false, Prec.Postfix, "reinterpret_cast"),
        new("rm", OIKind.Binary, false, Prec.Multiplicative, "operator%"),
        new("rs", OIKind.Binary, false, Prec.Shift, "operator>>"),
        new("sc", OIKind.NamedCast, false, Prec.Postfix, "static_cast"),
        new("ss", OIKind.Binary, false, Prec.Spaceship, "operator<=>"),
        new("st", OIKind.OfIdOp, true, Prec.Unary, "sizeof "),
        new("sz", OIKind.OfIdOp, false, Prec.Unary, "sizeof "),
        new("te", OIKind.OfIdOp, false, Prec.Postfix, "typeid "),
        new("ti", OIKind.OfIdOp, true, Prec.Postfix, "typeid "),
    ];

    private static bool OpLess(OperatorInfo op, char c0, char c1) => op.Enc[0] < c0 || (op.Enc[0] == c0 && op.Enc[1] < c1);

    private OperatorInfo? ParseOperatorEncoding()
    {
        if (NumLeft < 2)
            return null;
        char c0 = _text[_first], c1 = _text[_first + 1];
        int lower = 0, upper = Ops.Length - 1;
        while (upper != lower)
        {
            int middle = (upper + lower) / 2;
            if (OpLess(Ops[middle], c0, c1))
                lower = middle + 1;
            else
                upper = middle;
        }

        if (Ops[lower].Enc[0] != c0 || Ops[lower].Enc[1] != c1)
            return null;
        _first += 2;
        return Ops[lower];
    }

    private Node? ParseOperatorName(NameState? state)
    {
        if (ParseOperatorEncoding() is { } op)
        {
            if (op.Kind == OIKind.CCast)
            {
                bool savedTry = _tryToParseTemplateArgs, savedPermit = _permitForwardTemplateReferences;
                _tryToParseTemplateArgs = false;
                _permitForwardTemplateReferences = _permitForwardTemplateReferences || state is not null;
                try
                {
                    Node? ty = ParseType();
                    if (ty is null)
                        return null;
                    if (state is not null)
                        state.CtorDtorConversion = true;
                    return new ConversionOperatorType(ty);
                }
                finally
                {
                    _tryToParseTemplateArgs = savedTry;
                    _permitForwardTemplateReferences = savedPermit;
                }
            }

            if (op.Kind >= OIKind.Unnameable)
                return null;
            if (op.Kind == OIKind.Member && !op.Flag)
                return null;
            return new NameType(op.Name);
        }

        if (ConsumeIf("li"))
        {
            Node? sn = ParseSourceName(state);
            return sn is null ? null : new LiteralOperator(sn);
        }

        if (ConsumeIf('v'))
        {
            if (Look() is >= '0' and <= '9')
            {
                _first++;
                Node? sn = ParseSourceName(state);
                return sn is null ? null : new ConversionOperatorType(sn);
            }

            return null;
        }

        return null;
    }

    private Node? ParseCtorDtorName(ref Node soFar, NameState? state)
    {
        if (soFar is SpecialSubstitution ss)
            soFar = new ExpandedSpecialSubstitution(ss.Ssk);

        if (ConsumeIf('C'))
        {
            bool isInherited = ConsumeIf('I');
            if (Look() is not ('1' or '2' or '3' or '4' or '5'))
                return null;
            int variant = Look() - '0';
            ++_first;
            if (state is not null)
                state.CtorDtorConversion = true;
            if (isInherited && ParseName(state) is null)
                return null;
            return new CtorDtorName(soFar, false, variant);
        }

        if (Look() == 'D' && Look(1) is '0' or '1' or '2' or '4' or '5')
        {
            int variant = Look(1) - '0';
            _first += 2;
            if (state is not null)
                state.CtorDtorConversion = true;
            return new CtorDtorName(soFar, true, variant);
        }

        return null;
    }

    private Node? ParseNestedName(NameState? state)
    {
        if (!ConsumeIf('N'))
            return null;

        if (!ConsumeIf('H'))
        {
            Qualifiers cvTmp = ParseCVQualifiers();
            if (state is not null)
                state.CVQualifiers = cvTmp;
            if (ConsumeIf('O'))
            {
                if (state is not null)
                    state.ReferenceQualifier = FunctionRefQual.RValue;
            }
            else if (ConsumeIf('R'))
            {
                if (state is not null)
                    state.ReferenceQualifier = FunctionRefQual.LValue;
            }
            else if (state is not null)
            {
                state.ReferenceQualifier = FunctionRefQual.None;
            }
        }
        else if (state is not null)
        {
            state.HasExplicitObjectParameter = true;
        }

        Node? soFar = null;
        while (!ConsumeIf('E'))
        {
            if (state is not null)
                state.EndsWithTemplateArgs = false;

            if (Look() == 'T')
            {
                if (soFar is not null)
                    return null;
                soFar = ParseTemplateParam();
            }
            else if (Look() == 'I')
            {
                if (soFar is null)
                    return null;
                Node? ta = ParseTemplateArgs(state is not null);
                if (ta is null)
                    return null;
                if (soFar is NameWithTemplateArgs)
                    return null;
                if (state is not null)
                    state.EndsWithTemplateArgs = true;
                soFar = new NameWithTemplateArgs(soFar, ta);
            }
            else if (Look() == 'D' && Look(1) is 't' or 'T')
            {
                if (soFar is not null)
                    return null;
                soFar = ParseDecltype();
            }
            else
            {
                ModuleName? module = null;
                if (Look() == 'S')
                {
                    Node? s;
                    if (Look(1) == 't')
                    {
                        _first += 2;
                        s = new NameType("std");
                    }
                    else
                    {
                        s = ParseSubstitution();
                    }

                    if (s is null)
                        return null;
                    if (s is ModuleName m)
                    {
                        module = m;
                    }
                    else if (soFar is not null)
                    {
                        return null;
                    }
                    else
                    {
                        soFar = s;
                        continue;
                    }
                }

                soFar = ParseUnqualifiedName(state, soFar, module);
            }

            if (soFar is null)
                return null;
            _subs.Add(soFar);
            ConsumeIf('M');
        }

        if (soFar is null || _subs.Count == 0)
            return null;
        _subs.RemoveAt(_subs.Count - 1);
        return soFar;
    }

    private Node? ParseSimpleId()
    {
        Node? sn = ParseSourceName(null);
        if (sn is null)
            return null;
        if (Look() == 'I')
        {
            Node? ta = ParseTemplateArgs();
            return ta is null ? null : new NameWithTemplateArgs(sn, ta);
        }

        return sn;
    }

    private Node? ParseDestructorName()
    {
        Node? result = IsDigit(Look()) ? ParseSimpleId() : ParseUnresolvedType();
        return result is null ? null : new DtorName(result);
    }

    private Node? ParseUnresolvedType()
    {
        if (Look() == 'T')
        {
            Node? tp = ParseTemplateParam();
            if (tp is null)
                return null;
            _subs.Add(tp);
            return tp;
        }

        if (Look() == 'D')
        {
            Node? dt = ParseDecltype();
            if (dt is null)
                return null;
            _subs.Add(dt);
            return dt;
        }

        return ParseSubstitution();
    }

    private Node? ParseBaseUnresolvedName()
    {
        if (IsDigit(Look()))
            return ParseSimpleId();
        if (ConsumeIf("dn"))
            return ParseDestructorName();
        ConsumeIf("on");
        Node? oper = ParseOperatorName(null);
        if (oper is null)
            return null;
        if (Look() == 'I')
        {
            Node? ta = ParseTemplateArgs();
            return ta is null ? null : new NameWithTemplateArgs(oper, ta);
        }

        return oper;
    }

    private Node? ParseUnresolvedName(bool global)
    {
        Node? soFar = null;
        if (ConsumeIf("srN"))
        {
            soFar = ParseUnresolvedType();
            if (soFar is null)
                return null;
            if (Look() == 'I')
            {
                Node? ta = ParseTemplateArgs();
                if (ta is null)
                    return null;
                soFar = new NameWithTemplateArgs(soFar, ta);
            }

            while (!ConsumeIf('E'))
            {
                Node? qual = ParseSimpleId();
                if (qual is null)
                    return null;
                soFar = new QualifiedName(soFar, qual);
            }

            Node? @base = ParseBaseUnresolvedName();
            return @base is null ? null : new QualifiedName(soFar, @base);
        }

        if (!ConsumeIf("sr"))
        {
            soFar = ParseBaseUnresolvedName();
            if (soFar is null)
                return null;
            if (global)
                soFar = new GlobalQualifiedName(soFar);
            return soFar;
        }

        if (IsDigit(Look()))
        {
            do
            {
                Node? qual = ParseSimpleId();
                if (qual is null)
                    return null;
                if (soFar is not null)
                    soFar = new QualifiedName(soFar, qual);
                else if (global)
                    soFar = new GlobalQualifiedName(qual);
                else
                    soFar = qual;
            }
            while (!ConsumeIf('E'));
        }
        else
        {
            soFar = ParseUnresolvedType();
            if (soFar is null)
                return null;
            if (Look() == 'I')
            {
                Node? ta = ParseTemplateArgs();
                if (ta is null)
                    return null;
                soFar = new NameWithTemplateArgs(soFar, ta);
            }
        }

        Node? base2 = ParseBaseUnresolvedName();
        return base2 is null ? null : new QualifiedName(soFar!, base2);
    }

    private Node? ParseAbiTags(Node n)
    {
        while (ConsumeIf('B'))
        {
            string sn = ParseBareSourceName();
            if (sn.Length == 0)
                return null;
            n = new AbiTagAttr(n, sn);
        }

        return n;
    }

    private string ParseNumber(bool allowNegative = false)
    {
        int tmp = _first;
        if (allowNegative)
            ConsumeIf('n');
        if (NumLeft == 0 || !IsDigit(_text[_first]))
            return "";
        while (NumLeft != 0 && IsDigit(_text[_first]))
            ++_first;
        return Slice(tmp, _first);
    }

    /// <summary>Returns true on failure. Like LLVM's, the value is a <c>size_t</c> that wraps around on overflow.</summary>
    private bool ParsePositiveInteger(out ulong value)
    {
        value = 0;
        if (Look() is < '0' or > '9')
            return true;
        while (Look() is >= '0' and <= '9')
            value = unchecked(value * 10 + (ulong)(Consume() - '0'));
        return false;
    }

    private string ParseBareSourceName()
    {
        if (ParsePositiveInteger(out ulong length) || (ulong)NumLeft < length)
            return "";
        string r = Slice(_first, _first + (int)length);
        _first += (int)length;
        return r;
    }

    // ── Types ─────────────────────────────────────────────────────────────

    private Node? ParseFunctionType()
    {
        Qualifiers cvQuals = ParseCVQualifiers();
        Node? exceptionSpec = null;
        if (ConsumeIf("Do"))
        {
            exceptionSpec = new NameType("noexcept");
        }
        else if (ConsumeIf("DO"))
        {
            Node? e = ParseExpr();
            if (e is null || !ConsumeIf('E'))
                return null;
            exceptionSpec = new NoexceptSpec(e);
        }
        else if (ConsumeIf("Dw"))
        {
            int specsBegin = _names.Count;
            while (!ConsumeIf('E'))
            {
                Node? t = ParseType();
                if (t is null)
                    return null;
                _names.Add(t);
            }

            exceptionSpec = new DynamicExceptionSpec(PopTrailingNodeArray(specsBegin));
        }

        ConsumeIf("Dx");
        if (!ConsumeIf('F'))
            return null;
        ConsumeIf('Y');
        Node? returnType = ParseType();
        if (returnType is null)
            return null;

        FunctionRefQual referenceQualifier = FunctionRefQual.None;
        int paramsBegin = _names.Count;
        while (true)
        {
            if (ConsumeIf('E'))
                break;
            if (ConsumeIf('v'))
                continue;
            if (ConsumeIf("RE"))
            {
                referenceQualifier = FunctionRefQual.LValue;
                break;
            }

            if (ConsumeIf("OE"))
            {
                referenceQualifier = FunctionRefQual.RValue;
                break;
            }

            Node? t = ParseType();
            if (t is null)
                return null;
            _names.Add(t);
        }

        NodeArray @params = PopTrailingNodeArray(paramsBegin);
        return new FunctionType(returnType, @params, cvQuals, referenceQualifier, exceptionSpec);
    }

    private Node? ParseVectorType()
    {
        if (!ConsumeIf("Dv"))
            return null;
        if (Look() is >= '1' and <= '9')
        {
            Node dimensionNumber = new NameType(ParseNumber());
            if (!ConsumeIf('_'))
                return null;
            if (ConsumeIf('p'))
                return new PixelVectorType(dimensionNumber);
            Node? elemType = ParseType();
            return elemType is null ? null : new VectorType(elemType, dimensionNumber);
        }

        if (!ConsumeIf('_'))
        {
            Node? dimExpr = ParseExpr();
            if (dimExpr is null || !ConsumeIf('_'))
                return null;
            Node? elemType = ParseType();
            return elemType is null ? null : new VectorType(elemType, dimExpr);
        }

        Node? elem = ParseType();
        return elem is null ? null : new VectorType(elem, null);
    }

    private Node? ParseDecltype()
    {
        if (!ConsumeIf('D'))
            return null;
        if (!ConsumeIf('t') && !ConsumeIf('T'))
            return null;
        Node? e = ParseExpr();
        if (e is null || !ConsumeIf('E'))
            return null;
        return new EnclosingExpr("decltype", e);
    }

    private Node? ParseArrayType()
    {
        if (!ConsumeIf('A'))
            return null;
        Node? dimension = null;
        if (IsDigit(Look()))
        {
            dimension = new NameType(ParseNumber());
            if (!ConsumeIf('_'))
                return null;
        }
        else if (!ConsumeIf('_'))
        {
            Node? dimExpr = ParseExpr();
            if (dimExpr is null || !ConsumeIf('_'))
                return null;
            dimension = dimExpr;
        }

        Node? ty = ParseType();
        return ty is null ? null : new ArrayType(ty, dimension);
    }

    private Node? ParsePointerToMemberType()
    {
        if (!ConsumeIf('M'))
            return null;
        Node? classType = ParseType();
        if (classType is null)
            return null;
        Node? memberType = ParseType();
        return memberType is null ? null : new PointerToMemberType(classType, memberType);
    }

    private Node? ParseClassEnumType()
    {
        string elabSpef = "";
        if (ConsumeIf("Ts"))
            elabSpef = "struct";
        else if (ConsumeIf("Tu"))
            elabSpef = "union";
        else if (ConsumeIf("Te"))
            elabSpef = "enum";

        Node? name = ParseName();
        if (name is null)
            return null;
        return elabSpef.Length != 0 ? new ElaboratedTypeSpefType(elabSpef, name) : name;
    }

    private Node? ParseQualifiedType()
    {
        DemangleDepth.Enter();
        Node? result = ParseQualifiedTypeCore();
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseQualifiedTypeCore()
    {
        if (ConsumeIf('U'))
        {
            int qualStart = _first;
            string qual = ParseBareSourceName();
            if (qual.Length == 0)
                return null;

            if (qual.StartsWith("objcproto", StringComparison.Ordinal))
            {
                int protoStart = _first - qual.Length + "objcproto".Length;
                _ = qualStart;
                string proto;
                int savedFirst = _first, savedLast = _last;
                _first = protoStart;
                _last = savedFirst;
                try
                {
                    proto = ParseBareSourceName();
                }
                finally
                {
                    _first = savedFirst;
                    _last = savedLast;
                }

                if (proto.Length == 0)
                    return null;
                Node? child = ParseQualifiedType();
                return child is null ? null : new ObjCProtoName(child, proto);
            }

            Node? ta = null;
            if (Look() == 'I')
            {
                ta = ParseTemplateArgs();
                if (ta is null)
                    return null;
            }

            Node? child2 = ParseQualifiedType();
            return child2 is null ? null : new VendorExtQualType(child2, qual, ta);
        }

        Qualifiers quals = ParseCVQualifiers();
        Node? ty = ParseType();
        if (ty is null)
            return null;
        if (quals != Qualifiers.None)
            ty = new QualType(ty, quals);
        return ty;
    }
}
