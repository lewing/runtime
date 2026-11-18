// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from LLVM's llvm/include/llvm/Demangle/ItaniumDemangle.h (AbstractManglingParser)
// (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141, LLVM 23), which is part of the LLVM Project,
// under the Apache License v2.0 with LLVM Exceptions. See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

internal sealed partial class ItaniumParser
{
    private Node? ParseType()
    {
        DemangleDepth.Enter();
        Node? result = ParseTypeCore();
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseTypeCore()
    {
        Node? result = null;
        switch (Look())
        {
            case 'r':
            case 'V':
            case 'K':
            {
                int afterQuals = 0;
                if (Look(afterQuals) == 'r') ++afterQuals;
                if (Look(afterQuals) == 'V') ++afterQuals;
                if (Look(afterQuals) == 'K') ++afterQuals;
                if (Look(afterQuals) == 'F' ||
                    (Look(afterQuals) == 'D' && Look(afterQuals + 1) is 'o' or 'O' or 'w' or 'x'))
                {
                    result = ParseFunctionType();
                    break;
                }

                result = ParseQualifiedType();
                break;
            }

            case 'U':
                result = ParseQualifiedType();
                break;
            case 'v': ++_first; return new NameType("void");
            case 'w': ++_first; return new NameType("wchar_t");
            case 'b': ++_first; return new NameType("bool");
            case 'c': ++_first; return new NameType("char");
            case 'a': ++_first; return new NameType("signed char");
            case 'h': ++_first; return new NameType("unsigned char");
            case 's': ++_first; return new NameType("short");
            case 't': ++_first; return new NameType("unsigned short");
            case 'i': ++_first; return new NameType("int");
            case 'j': ++_first; return new NameType("unsigned int");
            case 'l': ++_first; return new NameType("long");
            case 'm': ++_first; return new NameType("unsigned long");
            case 'x': ++_first; return new NameType("long long");
            case 'y': ++_first; return new NameType("unsigned long long");
            case 'n': ++_first; return new NameType("__int128");
            case 'o': ++_first; return new NameType("unsigned __int128");
            case 'f': ++_first; return new NameType("float");
            case 'd': ++_first; return new NameType("double");
            case 'e': ++_first; return new NameType("long double");
            case 'g': ++_first; return new NameType("__float128");
            case 'z': ++_first; return new NameType("...");
            case 'u':
            {
                ++_first;
                string res = ParseBareSourceName();
                if (res.Length == 0)
                    return null;
                if (ConsumeIf('I'))
                {
                    Node? baseType = ParseType();
                    if (baseType is null || !ConsumeIf('E'))
                        return null;
                    result = new TransformedType(res, baseType);
                }
                else
                {
                    result = new NameType(res);
                }

                break;
            }

            case 'D':
                switch (Look(1))
                {
                    case 'd': _first += 2; return new NameType("decimal64");
                    case 'e': _first += 2; return new NameType("decimal128");
                    case 'f': _first += 2; return new NameType("decimal32");
                    case 'h': _first += 2; return new NameType("half");
                    case 'F':
                    {
                        _first += 2;
                        if (ConsumeIf("16b"))
                            return new NameType("std::bfloat16_t");
                        Node dimensionNumber = new NameType(ParseNumber());
                        if (!ConsumeIf('_'))
                            return null;
                        return new BinaryFPType(dimensionNumber);
                    }

                    case 'A':
                    {
                        char c = Look(2);
                        _first += 3;
                        return FixedPoint("", "_Accum", c);
                    }

                    case 'R':
                    {
                        char c = Look(2);
                        _first += 3;
                        return FixedPoint("", "_Fract", c);
                    }

                    case 'S':
                    {
                        _first += 2;
                        if (Look() != 'D')
                            return null;
                        if (Look(1) == 'A')
                        {
                            char c = Look(2);
                            _first += 3;
                            return FixedPoint("_Sat ", "_Accum", c);
                        }

                        if (Look(1) == 'R')
                        {
                            char c = Look(2);
                            _first += 3;
                            return FixedPoint("_Sat ", "_Fract", c);
                        }

                        return null;
                    }

                    case 'B':
                    case 'U':
                    {
                        bool signed = Look(1) == 'B';
                        _first += 2;
                        Node? size = IsDigit(Look()) ? new NameType(ParseNumber()) : ParseExpr();
                        if (size is null || !ConsumeIf('_'))
                            return null;
                        result = new BitIntType(size, signed);
                        break;
                    }

                    case 'i': _first += 2; return new NameType("char32_t");
                    case 's': _first += 2; return new NameType("char16_t");
                    case 'u': _first += 2; return new NameType("char8_t");
                    case 'a': _first += 2; return new NameType("auto");
                    case 'c': _first += 2; return new NameType("decltype(auto)");
                    case 'k':
                    case 'K':
                    {
                        string kind = Look(1) == 'k' ? " auto" : " decltype(auto)";
                        _first += 2;
                        Node? constraint = ParseName();
                        return constraint is null ? null : new PostfixQualifiedType(constraint, kind);
                    }

                    case 'n': _first += 2; return new NameType("std::nullptr_t");
                    case 't':
                    case 'T':
                        result = ParseDecltype();
                        break;
                    case 'v':
                        result = ParseVectorType();
                        break;
                    case 'p':
                    {
                        _first += 2;
                        Node? child = ParseType();
                        if (child is null)
                            return null;
                        result = new ParameterPackExpansion(child);
                        break;
                    }

                    case 'y':
                    {
                        _first += 2;
                        Node? pattern = ParseType();
                        if (pattern is null)
                            return null;
                        Node? index = ParseExpr();
                        if (index is null)
                            return null;
                        result = new PackIndexing(pattern, index);
                        break;
                    }

                    case 'o':
                    case 'O':
                    case 'w':
                    case 'x':
                        result = ParseFunctionType();
                        break;
                }

                break;
            case 'F':
                result = ParseFunctionType();
                break;
            case 'A':
                result = ParseArrayType();
                break;
            case 'M':
                result = ParsePointerToMemberType();
                break;
            case 'T':
            {
                if (Look(1) is 's' or 'u' or 'e')
                {
                    result = ParseClassEnumType();
                    break;
                }

                result = ParseTemplateParam();
                if (result is null)
                    return null;
                if (_tryToParseTemplateArgs && Look() == 'I')
                {
                    _subs.Add(result);
                    Node? ta = ParseTemplateArgs();
                    if (ta is null)
                        return null;
                    result = new NameWithTemplateArgs(result, ta);
                }

                break;
            }

            case 'P':
            {
                ++_first;
                Node? ptr = ParseType();
                if (ptr is null)
                    return null;
                result = new PointerType(ptr);
                break;
            }

            case 'R':
            {
                ++_first;
                Node? r = ParseType();
                if (r is null)
                    return null;
                result = new ReferenceType(r, ReferenceKind.LValue);
                break;
            }

            case 'O':
            {
                ++_first;
                Node? r = ParseType();
                if (r is null)
                    return null;
                result = new ReferenceType(r, ReferenceKind.RValue);
                break;
            }

            case 'C':
            {
                ++_first;
                Node? p = ParseType();
                if (p is null)
                    return null;
                result = new PostfixQualifiedType(p, " complex");
                break;
            }

            case 'G':
            {
                ++_first;
                Node? p = ParseType();
                if (p is null)
                    return null;
                result = new PostfixQualifiedType(p, " imaginary");
                break;
            }

            case 'S' when Look(1) != 't':
            {
                bool isSubst = false;
                result = ParseUnscopedName(null, ref isSubst);
                if (result is null)
                    return null;
                if (Look() == 'I' && (!isSubst || _tryToParseTemplateArgs))
                {
                    if (!isSubst)
                        _subs.Add(result);
                    Node? ta = ParseTemplateArgs();
                    if (ta is null)
                        return null;
                    result = new NameWithTemplateArgs(result, ta);
                }
                else if (isSubst)
                {
                    return result;
                }

                break;
            }

            default:
                result = ParseClassEnumType();
                break;
        }

        if (result is not null)
            _subs.Add(result);
        return result;
    }

    private static NameType? FixedPoint(string sat, string kind, char c)
    {
        string? name = c switch
        {
            's' => "short ",
            't' => "unsigned short ",
            'i' => "",
            'j' => "unsigned ",
            'l' => "long ",
            'm' => "unsigned long ",
            _ => null,
        };
        return name is null ? null : new NameType(sat + name + kind);
    }

    private Node? ParsePrefixExpr(string kind, Prec prec)
    {
        Node? e = ParseExpr();
        return e is null ? null : new PrefixExpr(kind, e, prec);
    }

    private Node? ParseBinaryExpr(string kind, Prec prec)
    {
        Node? lhs = ParseExpr();
        if (lhs is null)
            return null;
        Node? rhs = ParseExpr();
        return rhs is null ? null : new BinaryExpr(lhs, kind, rhs, prec);
    }

    private Node? ParseIntegerLiteral(string lit)
    {
        string tmp = ParseNumber(true);
        if (tmp.Length != 0 && ConsumeIf('E'))
            return new IntegerLiteral(lit, tmp);
        return null;
    }

    private Qualifiers ParseCVQualifiers()
    {
        Qualifiers cvr = Qualifiers.None;
        if (ConsumeIf('r'))
            cvr |= Qualifiers.Restrict;
        if (ConsumeIf('V'))
            cvr |= Qualifiers.Volatile;
        if (ConsumeIf('K'))
            cvr |= Qualifiers.Const;
        return cvr;
    }

    private Node? ParseFunctionParam()
    {
        if (ConsumeIf("fpT"))
            return new NameType("this");
        if (ConsumeIf("fp"))
        {
            ParseCVQualifiers();
            string num = ParseNumber();
            return ConsumeIf('_') ? new FunctionParam(num) : null;
        }

        if (ConsumeIf("fL"))
        {
            if (ParseNumber().Length == 0)
                return null;
            if (!ConsumeIf('p'))
                return null;
            ParseCVQualifiers();
            string num = ParseNumber();
            return ConsumeIf('_') ? new FunctionParam(num) : null;
        }

        return null;
    }

    private Node? ParseExprPrimary()
    {
        if (!ConsumeIf('L'))
            return null;
        switch (Look())
        {
            case 'w': ++_first; return ParseIntegerLiteral("wchar_t");
            case 'b':
                if (ConsumeIf("b0E"))
                    return new BoolExpr(false);
                if (ConsumeIf("b1E"))
                    return new BoolExpr(true);
                return null;
            case 'c': ++_first; return ParseIntegerLiteral("char");
            case 'a': ++_first; return ParseIntegerLiteral("signed char");
            case 'h': ++_first; return ParseIntegerLiteral("unsigned char");
            case 's': ++_first; return ParseIntegerLiteral("short");
            case 't': ++_first; return ParseIntegerLiteral("unsigned short");
            case 'i': ++_first; return ParseIntegerLiteral("");
            case 'j': ++_first; return ParseIntegerLiteral("u");
            case 'l': ++_first; return ParseIntegerLiteral("l");
            case 'm': ++_first; return ParseIntegerLiteral("ul");
            case 'x': ++_first; return ParseIntegerLiteral("ll");
            case 'y': ++_first; return ParseIntegerLiteral("ull");
            case 'n': ++_first; return ParseIntegerLiteral("__int128");
            case 'o': ++_first; return ParseIntegerLiteral("unsigned __int128");
            case 'f': ++_first; return ParseFloatingLiteral(NodeKind.FloatLiteral);
            case 'd': ++_first; return ParseFloatingLiteral(NodeKind.DoubleLiteral);
            case 'e': ++_first; return ParseFloatingLiteral(NodeKind.LongDoubleLiteral);
            case '_':
                if (ConsumeIf("_Z"))
                {
                    Node? r = ParseEncoding();
                    if (r is not null && ConsumeIf('E'))
                        return r;
                }

                return null;
            case 'A':
            {
                Node? t = ParseType();
                if (t is null)
                    return null;
                return ConsumeIf('E') ? new StringLiteral(t) : null;
            }

            case 'D':
                if (ConsumeIf("Dn"))
                {
                    ConsumeIf('0');
                    if (ConsumeIf('E'))
                        return new NameType("nullptr");
                }

                return null;
            case 'T':
                return null;
            case 'U':
            {
                if (Look(1) != 'l')
                    return null;
                Node? t = ParseUnnamedTypeName(null);
                if (t is null || !ConsumeIf('E'))
                    return null;
                return new LambdaExpr(t);
            }

            default:
            {
                Node? t = ParseType();
                if (t is null)
                    return null;
                string n = ParseNumber(true);
                if (n.Length == 0 || !ConsumeIf('E'))
                    return null;
                return new EnumLiteral(t, n);
            }
        }
    }

    private Node? ParseBracedExpr()
    {
        DemangleDepth.Enter();
        Node? result = ParseBracedExprCore();
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseBracedExprCore()
    {
        if (Look() == 'd')
        {
            switch (Look(1))
            {
                case 'i':
                {
                    _first += 2;
                    Node? field = ParseSourceName(null);
                    if (field is null)
                        return null;
                    Node? init = ParseBracedExpr();
                    return init is null ? null : new BracedExpr(field, init, false);
                }

                case 'x':
                {
                    _first += 2;
                    Node? index = ParseExpr();
                    if (index is null)
                        return null;
                    Node? init = ParseBracedExpr();
                    return init is null ? null : new BracedExpr(index, init, true);
                }

                case 'X':
                {
                    _first += 2;
                    Node? rangeBegin = ParseExpr();
                    if (rangeBegin is null)
                        return null;
                    Node? rangeEnd = ParseExpr();
                    if (rangeEnd is null)
                        return null;
                    Node? init = ParseBracedExpr();
                    return init is null ? null : new BracedRangeExpr(rangeBegin, rangeEnd, init);
                }
            }
        }

        return ParseExpr();
    }

    private Node? ParseFoldExpr()
    {
        if (!ConsumeIf('f'))
            return null;
        bool isLeftFold = false, hasInitializer = false;
        switch (Look())
        {
            case 'L': isLeftFold = true; hasInitializer = true; break;
            case 'R': hasInitializer = true; break;
            case 'l': isLeftFold = true; break;
            case 'r': break;
            default: return null;
        }

        ++_first;
        OperatorInfo? op = ParseOperatorEncoding();
        if (op is null)
            return null;
        if (!(op.Kind == OIKind.Binary || (op.Kind == OIKind.Member && op.Name[^1] == '*')))
            return null;
        Node? pack = ParseExpr();
        if (pack is null)
            return null;
        Node? init = null;
        if (hasInitializer)
        {
            init = ParseExpr();
            if (init is null)
                return null;
        }

        if (isLeftFold && init is not null)
            (pack, init) = (init, pack);
        return new FoldExpr(isLeftFold, op.Symbol, pack, init);
    }

    private Node? ParsePointerToMemberConversionExpr(Prec prec)
    {
        Node? ty = ParseType();
        if (ty is null)
            return null;
        Node? expr = ParseExpr();
        if (expr is null)
            return null;
        string offset = ParseNumber(true);
        return ConsumeIf('E') ? new PointerToMemberConversionExpr(ty, expr, offset, prec) : null;
    }

    private Node? ParseSubobjectExpr()
    {
        Node? ty = ParseType();
        if (ty is null)
            return null;
        Node? expr = ParseExpr();
        if (expr is null)
            return null;
        string offset = ParseNumber(true);
        int selectorsBegin = _names.Count;
        while (ConsumeIf('_'))
            _names.Add(new NameType(ParseNumber()));
        bool onePastTheEnd = ConsumeIf('p');
        if (!ConsumeIf('E'))
            return null;
        return new SubobjectExpr(ty, expr, offset, PopTrailingNodeArray(selectorsBegin), onePastTheEnd);
    }

    private Node? ParseConstraintExpr()
    {
        bool saved = _hasIncompleteTemplateParameterTracking;
        _hasIncompleteTemplateParameterTracking = true;
        try
        {
            return ParseExpr();
        }
        finally
        {
            _hasIncompleteTemplateParameterTracking = saved;
        }
    }

    private Node? ParseRequiresExpr()
    {
        NodeArray @params = default;
        if (ConsumeIf("rQ"))
        {
            int paramsBegin = _names.Count;
            while (!ConsumeIf('_'))
            {
                Node? type = ParseType();
                if (type is null)
                    return null;
                _names.Add(type);
            }

            @params = PopTrailingNodeArray(paramsBegin);
        }
        else if (!ConsumeIf("rq"))
        {
            return null;
        }

        int reqsBegin = _names.Count;
        do
        {
            Node? constraint = null;
            if (ConsumeIf('X'))
            {
                Node? expr = ParseExpr();
                if (expr is null)
                    return null;
                bool isNoexcept = ConsumeIf('N');
                Node? typeReq = null;
                if (ConsumeIf('R'))
                {
                    typeReq = ParseName();
                    if (typeReq is null)
                        return null;
                }

                constraint = new ExprRequirement(expr, isNoexcept, typeReq);
            }
            else if (ConsumeIf('T'))
            {
                Node? type = ParseType();
                if (type is null)
                    return null;
                constraint = new TypeRequirement(type);
            }
            else if (ConsumeIf('Q'))
            {
                Node? nested = ParseExpr();
                if (nested is null)
                    return null;
                constraint = new NestedRequirement(nested);
            }

            if (constraint is null)
                return null;
            _names.Add(constraint);
        }
        while (!ConsumeIf('E'));
        return new RequiresExpr(@params, PopTrailingNodeArray(reqsBegin));
    }

    private Node? ParseExpr()
    {
        DemangleDepth.Enter();
        Node? result = ParseExprCore();
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseExprCore()
    {
        bool global = ConsumeIf("gs");

        if (ParseOperatorEncoding() is { } op)
        {
            string sym = op.Symbol;
            switch (op.Kind)
            {
                case OIKind.Binary:
                    return ParseBinaryExpr(sym, op.Prec);
                case OIKind.Prefix:
                    return ParsePrefixExpr(sym, op.Prec);
                case OIKind.Postfix:
                {
                    if (ConsumeIf('_'))
                        return ParsePrefixExpr(sym, op.Prec);
                    Node? ex = ParseExpr();
                    return ex is null ? null : new PostfixExpr(ex, sym, op.Prec);
                }

                case OIKind.Array:
                {
                    Node? @base = ParseExpr();
                    if (@base is null)
                        return null;
                    Node? index = ParseExpr();
                    return index is null ? null : new ArraySubscriptExpr(@base, index, op.Prec);
                }

                case OIKind.Member:
                {
                    Node? lhs = ParseExpr();
                    if (lhs is null)
                        return null;
                    Node? rhs = ParseExpr();
                    return rhs is null ? null : new MemberExpr(lhs, sym, rhs, op.Prec);
                }

                case OIKind.New:
                {
                    int exprs = _names.Count;
                    while (!ConsumeIf('_'))
                    {
                        Node? ex = ParseExpr();
                        if (ex is null)
                            return null;
                        _names.Add(ex);
                    }

                    NodeArray exprList = PopTrailingNodeArray(exprs);
                    Node? ty = ParseType();
                    if (ty is null)
                        return null;
                    bool haveInits = ConsumeIf("pi");
                    int initsBegin = _names.Count;
                    while (!ConsumeIf('E'))
                    {
                        if (!haveInits)
                            return null;
                        Node? init = ParseExpr();
                        if (init is null)
                            return null;
                        _names.Add(init);
                    }

                    NodeArray inits = PopTrailingNodeArray(initsBegin);
                    return new NewExpr(exprList, ty, inits, global, op.Flag, op.Prec);
                }

                case OIKind.Del:
                {
                    Node? ex = ParseExpr();
                    return ex is null ? null : new DeleteExpr(ex, global, op.Flag, op.Prec);
                }

                case OIKind.Call:
                {
                    Node? callee = ParseExpr();
                    if (callee is null)
                        return null;
                    int exprsBegin = _names.Count;
                    while (!ConsumeIf('E'))
                    {
                        Node? e = ParseExpr();
                        if (e is null)
                            return null;
                        _names.Add(e);
                    }

                    return new CallExpr(callee, PopTrailingNodeArray(exprsBegin), op.Flag, op.Prec);
                }

                case OIKind.CCast:
                {
                    Node? ty;
                    bool saved = _tryToParseTemplateArgs;
                    _tryToParseTemplateArgs = false;
                    try
                    {
                        ty = ParseType();
                    }
                    finally
                    {
                        _tryToParseTemplateArgs = saved;
                    }

                    if (ty is null)
                        return null;
                    int exprsBegin = _names.Count;
                    bool isMany = ConsumeIf('_');
                    while (!ConsumeIf('E'))
                    {
                        Node? e = ParseExpr();
                        if (e is null)
                            return null;
                        _names.Add(e);
                        if (!isMany)
                            break;
                    }

                    NodeArray exprs2 = PopTrailingNodeArray(exprsBegin);
                    if (!isMany && exprs2.Count != 1)
                        return null;
                    return new ConversionExpr(ty, exprs2, op.Prec);
                }

                case OIKind.Conditional:
                {
                    Node? cond = ParseExpr();
                    if (cond is null)
                        return null;
                    Node? lhs = ParseExpr();
                    if (lhs is null)
                        return null;
                    Node? rhs = ParseExpr();
                    return rhs is null ? null : new ConditionalExpr(cond, lhs, rhs, op.Prec);
                }

                case OIKind.NamedCast:
                {
                    Node? ty = ParseType();
                    if (ty is null)
                        return null;
                    Node? ex = ParseExpr();
                    return ex is null ? null : new CastExpr(sym, ty, ex, op.Prec);
                }

                case OIKind.OfIdOp:
                {
                    Node? arg = op.Flag ? ParseType() : ParseExpr();
                    return arg is null ? null : new EnclosingExpr(sym, arg, op.Prec);
                }

                default:
                    return null;
            }
        }

        if (NumLeft < 2)
            return null;

        if (Look() == 'L')
            return ParseExprPrimary();
        if (Look() == 'T')
            return ParseTemplateParam();
        if (Look() == 'f')
        {
            if (Look(1) == 'p' || (Look(1) == 'L' && IsDigit(Look(2))))
                return ParseFunctionParam();
            return ParseFoldExpr();
        }

        if (ConsumeIf("il"))
        {
            int initsBegin = _names.Count;
            while (!ConsumeIf('E'))
            {
                Node? e = ParseBracedExpr();
                if (e is null)
                    return null;
                _names.Add(e);
            }

            return new InitListExpr(null, PopTrailingNodeArray(initsBegin));
        }

        if (ConsumeIf("mc"))
            return ParsePointerToMemberConversionExpr(Prec.Unary);
        if (ConsumeIf("nx"))
        {
            Node? ex = ParseExpr();
            return ex is null ? null : new EnclosingExpr("noexcept ", ex, Prec.Unary);
        }

        if (Look() == 'r' && Look(1) is 'q' or 'Q')
            return ParseRequiresExpr();
        if (ConsumeIf("so"))
            return ParseSubobjectExpr();
        if (ConsumeIf("sp"))
        {
            Node? child = ParseExpr();
            return child is null ? null : new ParameterPackExpansion(child);
        }

        if (ConsumeIf("sy"))
        {
            Node? pattern = Look() == 'T' ? ParseTemplateParam() : ParseFunctionParam();
            if (pattern is null)
                return null;
            Node? index = ParseExpr();
            return index is null ? null : new PackIndexing(pattern, index);
        }

        if (ConsumeIf("sZ"))
        {
            if (Look() == 'T')
            {
                Node? r = ParseTemplateParam();
                return r is null ? null : new SizeofParamPackExpr(r);
            }

            Node? fp = ParseFunctionParam();
            return fp is null ? null : new EnclosingExpr("sizeof... ", fp);
        }

        if (ConsumeIf("sP"))
        {
            int argsBegin = _names.Count;
            while (!ConsumeIf('E'))
            {
                Node? arg = ParseTemplateArg();
                if (arg is null)
                    return null;
                _names.Add(arg);
            }

            return new EnclosingExpr("sizeof... ", new NodeArrayNode(PopTrailingNodeArray(argsBegin)));
        }

        if (ConsumeIf("tl"))
        {
            Node? ty = ParseType();
            if (ty is null)
                return null;
            int initsBegin = _names.Count;
            while (!ConsumeIf('E'))
            {
                Node? e = ParseBracedExpr();
                if (e is null)
                    return null;
                _names.Add(e);
            }

            return new InitListExpr(ty, PopTrailingNodeArray(initsBegin));
        }

        if (ConsumeIf("tr"))
            return new NameType("throw");
        if (ConsumeIf("tw"))
        {
            Node? ex = ParseExpr();
            return ex is null ? null : new ThrowExpr(ex);
        }

        if (ConsumeIf('u'))
        {
            Node? name = ParseSourceName(null);
            if (name is null)
                return null;
            bool isUuid = false;
            Node? uuid = null;
            if (name.GetBaseName() == "__uuidof")
            {
                if (ConsumeIf('t'))
                {
                    uuid = ParseType();
                    isUuid = true;
                }
                else if (ConsumeIf('z'))
                {
                    uuid = ParseExpr();
                    isUuid = true;
                }
            }

            int exprsBegin = _names.Count;
            if (isUuid)
            {
                if (uuid is null)
                    return null;
                _names.Add(uuid);
            }
            else
            {
                while (!ConsumeIf('E'))
                {
                    Node? e = ParseTemplateArg();
                    if (e is null)
                        return null;
                    _names.Add(e);
                }
            }

            return new CallExpr(name, PopTrailingNodeArray(exprsBegin), false, Prec.Postfix);
        }

        return ParseUnresolvedName(global);
    }

    /// <summary>Returns true on failure.</summary>
    private bool ParseCallOffset()
    {
        if (ConsumeIf('h'))
            return ParseNumber(true).Length == 0 || !ConsumeIf('_');
        if (ConsumeIf('v'))
            return ParseNumber(true).Length == 0 || !ConsumeIf('_') || ParseNumber(true).Length == 0 || !ConsumeIf('_');
        return true;
    }

    private Node? ParseSpecialName()
    {
        switch (Look())
        {
            case 'T':
                switch (Look(1))
                {
                    case 'A':
                    {
                        _first += 2;
                        Node? arg = ParseTemplateArg();
                        return arg is null ? null : new SpecialName("template parameter object for ", arg);
                    }

                    case 'V': return SpecialType("vtable for ");
                    case 'T': return SpecialType("VTT for ");
                    case 'I': return SpecialType("typeinfo for ");
                    case 'S': return SpecialType("typeinfo name for ");
                    case 'c':
                    {
                        _first += 2;
                        if (ParseCallOffset() || ParseCallOffset())
                            return null;
                        Node? encoding = ParseEncoding();
                        return encoding is null ? null : new SpecialName("covariant return thunk to ", encoding);
                    }

                    case 'C':
                    {
                        _first += 2;
                        Node? firstType = ParseType();
                        if (firstType is null)
                            return null;
                        if (ParseNumber(true).Length == 0 || !ConsumeIf('_'))
                            return null;
                        Node? secondType = ParseType();
                        return secondType is null ? null : new CtorVtableSpecialName(secondType, firstType);
                    }

                    case 'W': return SpecialNameOf("thread-local wrapper routine for ");
                    case 'H': return SpecialNameOf("thread-local initialization routine for ");
                    default:
                    {
                        ++_first;
                        bool isVirt = Look() == 'v';
                        if (ParseCallOffset())
                            return null;
                        Node? baseEncoding = ParseEncoding();
                        if (baseEncoding is null)
                            return null;
                        return new SpecialName(isVirt ? "virtual thunk to " : "non-virtual thunk to ", baseEncoding);
                    }
                }

            case 'G':
                switch (Look(1))
                {
                    case 'V': return SpecialNameOf("guard variable for ");
                    case 'R':
                    {
                        _first += 2;
                        Node? name = ParseName();
                        if (name is null)
                            return null;
                        bool parsedSeqId = !ParseSeqId(out _);
                        if (!ConsumeIf('_') && parsedSeqId)
                            return null;
                        return new SpecialName("reference temporary for ", name);
                    }

                    case 'I':
                    {
                        _first += 2;
                        ModuleName? module = null;
                        if (ParseModuleNameOpt(ref module))
                            return null;
                        if (module is null)
                            return null;
                        return new SpecialName("initializer for module ", module);
                    }
                }

                break;
        }

        return null;

        Node? SpecialType(string special)
        {
            _first += 2;
            Node? ty = ParseType();
            return ty is null ? null : new SpecialName(special, ty);
        }

        Node? SpecialNameOf(string special)
        {
            _first += 2;
            Node? name = ParseName();
            return name is null ? null : new SpecialName(special, name);
        }
    }

    private Node? ParseEncoding(bool parseParams = true)
    {
        DemangleDepth.Enter();
        Node? result = ParseEncodingCore(parseParams);
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseEncodingCore(bool parseParams)
    {
        using var saveTemplateParams = new SaveTemplateParams(this);

        if (Look() is 'G' or 'T')
            return ParseSpecialName();

        bool IsEndOfEncoding() => NumLeft == 0 || Look() is 'E' or '.' or '_';

        var nameInfo = new NameState(this);
        Node? name = ParseName(nameInfo);
        if (name is null)
            return null;
        if (ResolveForwardTemplateRefs(nameInfo))
            return null;
        if (IsEndOfEncoding())
            return name;

        if (!parseParams)
        {
            while (Consume() != '\0')
            {
            }

            return name;
        }

        Node? attrs = null;
        if (ConsumeIf("Ua9enable_ifI"))
        {
            int beforeArgs = _names.Count;
            while (!ConsumeIf('E'))
            {
                Node? arg = ParseTemplateArg();
                if (arg is null)
                    return null;
                _names.Add(arg);
            }

            attrs = new EnableIfAttr(PopTrailingNodeArray(beforeArgs));
        }

        Node? returnType = null;
        if (!nameInfo.CtorDtorConversion && nameInfo.EndsWithTemplateArgs)
        {
            returnType = ParseType();
            if (returnType is null)
                return null;
        }

        NodeArray @params = default;
        if (!ConsumeIf('v'))
        {
            int paramsBegin = _names.Count;
            do
            {
                Node? ty = ParseType();
                if (ty is null)
                    return null;
                bool isFirstParam = paramsBegin == _names.Count;
                if (nameInfo.HasExplicitObjectParameter && isFirstParam)
                    ty = new ExplicitObjectParameter(ty);
                _names.Add(ty);
            }
            while (!IsEndOfEncoding() && Look() != 'Q');
            @params = PopTrailingNodeArray(paramsBegin);
        }

        Node? requires = null;
        if (ConsumeIf('Q'))
        {
            requires = ParseConstraintExpr();
            if (requires is null)
                return null;
        }

        return new FunctionEncoding(returnType, name, @params, attrs, requires, nameInfo.CVQualifiers, nameInfo.ReferenceQualifier);
    }

    private Node? ParseFloatingLiteral(NodeKind kind)
    {
        int n = FloatLiteralNode.MangledSize(kind);
        if (NumLeft <= n)
            return null;
        string data = Slice(_first, _first + n);
        foreach (char c in data)
        {
            if (!(c is >= '0' and <= '9') && !(c is >= 'a' and <= 'f'))
                return null;
        }

        _first += n;
        return ConsumeIf('E') ? new FloatLiteralNode(kind, data) : null;
    }

    /// <summary>Returns true on failure. Like LLVM's, the id is a <c>size_t</c> that wraps around on overflow.</summary>
    private bool ParseSeqId(out ulong id)
    {
        id = 0;
        if (Look() is not (>= '0' and <= '9') && Look() is not (>= 'A' and <= 'Z'))
            return true;
        while (true)
        {
            if (Look() is >= '0' and <= '9')
                id = unchecked(id * 36 + (ulong)(Look() - '0'));
            else if (Look() is >= 'A' and <= 'Z')
                id = unchecked(id * 36 + (ulong)(Look() - 'A') + 10);
            else
                return false;

            ++_first;
        }
    }

    private Node? ParseSubstitution()
    {
        if (!ConsumeIf('S'))
            return null;

        if (Look() is >= 'a' and <= 'z')
        {
            SpecialSubKind kind;
            switch (Look())
            {
                case 'a': kind = SpecialSubKind.Allocator; break;
                case 'b': kind = SpecialSubKind.BasicString; break;
                case 'd': kind = SpecialSubKind.IOStream; break;
                case 'i': kind = SpecialSubKind.IStream; break;
                case 'o': kind = SpecialSubKind.OStream; break;
                case 's': kind = SpecialSubKind.String; break;
                default: return null;
            }

            ++_first;
            Node specialSub = new SpecialSubstitution(kind);
            Node? withTags = ParseAbiTags(specialSub);
            if (!ReferenceEquals(withTags, specialSub))
            {
                if (withTags is null)
                    return null;
                _subs.Add(withTags);
                specialSub = withTags;
            }

            return specialSub;
        }

        if (ConsumeIf('_'))
            return _subs.Count == 0 ? null : _subs[0];

        if (ParseSeqId(out ulong index))
            return null;
        index = unchecked(index + 1);
        if (!ConsumeIf('_') || index >= (ulong)_subs.Count)
            return null;
        return _subs[(int)index];
    }

    private Node? ParseTemplateParam()
    {
        int begin = _first;
        if (!ConsumeIf('T'))
            return null;

        ulong level = 0;
        if (ConsumeIf('L'))
        {
            if (ParsePositiveInteger(out level))
                return null;
            level = unchecked(level + 1);
            if (!ConsumeIf('_'))
                return null;
        }

        ulong index = 0;
        if (!ConsumeIf('_'))
        {
            if (ParsePositiveInteger(out index))
                return null;
            index = unchecked(index + 1);
            if (!ConsumeIf('_'))
                return null;
        }

        if (_hasIncompleteTemplateParameterTracking)
            return new NameType(Slice(begin, _first - 1));

        if (_permitForwardTemplateReferences && level == 0)
        {
            var forwardRef = new ForwardTemplateReference((int)Math.Min(index, int.MaxValue));
            _forwardTemplateRefs.Add(forwardRef);
            return forwardRef;
        }

        if (level >= (ulong)_templateParams.Count || _templateParams[(int)level] is not { } list || index >= (ulong)list.Count)
        {
            if (unchecked((ulong)_parsingLambdaParamsAtLevel) == level && level <= (ulong)_templateParams.Count)
            {
                if (level == (ulong)_templateParams.Count)
                    _templateParams.Add(null);
                return new NameType("auto");
            }

            return null;
        }

        return list[(int)index];
    }

    private Node? ParseTemplateParamDecl(List<Node>? @params)
    {
        DemangleDepth.Enter();
        Node? result = ParseTemplateParamDeclCore(@params);
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseTemplateParamDeclCore(List<Node>? @params)
    {
        Node InventTemplateParamName(TemplateParamKind kind)
        {
            uint index = _numSyntheticTemplateParameters[(int)kind]++;
            Node n = new SyntheticTemplateParamName(kind, index);
            @params?.Add(n);
            return n;
        }

        if (ConsumeIf("Ty"))
            return new TypeTemplateParamDecl(InventTemplateParamName(TemplateParamKind.Type));

        if (ConsumeIf("Tk"))
        {
            bool saved = _hasIncompleteTemplateParameterTracking;
            _hasIncompleteTemplateParameterTracking = true;
            try
            {
                Node? constraint = ParseName();
                if (constraint is null)
                    return null;
                Node name = InventTemplateParamName(TemplateParamKind.Type);
                return new ConstrainedTypeTemplateParamDecl(constraint, name);
            }
            finally
            {
                _hasIncompleteTemplateParameterTracking = saved;
            }
        }

        if (ConsumeIf("Tn"))
        {
            Node name = InventTemplateParamName(TemplateParamKind.NonType);
            Node? type = ParseType();
            return type is null ? null : new NonTypeTemplateParamDecl(name, type);
        }

        if (ConsumeIf("Tt"))
        {
            Node name = InventTemplateParamName(TemplateParamKind.Template);
            int paramsBegin = _names.Count;
            using var templateTemplateParamParams = new ScopedTemplateParamList(this);
            Node? requires = null;
            while (!ConsumeIf('E'))
            {
                Node? p = ParseTemplateParamDecl(templateTemplateParamParams.Params);
                if (p is null)
                    return null;
                _names.Add(p);
                if (ConsumeIf('Q'))
                {
                    requires = ParseConstraintExpr();
                    if (requires is null || !ConsumeIf('E'))
                        return null;
                    break;
                }
            }

            NodeArray innerParams = PopTrailingNodeArray(paramsBegin);
            return new TemplateTemplateParamDecl(name, innerParams, requires);
        }

        if (ConsumeIf("Tp"))
        {
            Node? p = ParseTemplateParamDecl(@params);
            return p is null ? null : new TemplateParamPackDecl(p);
        }

        return null;
    }

    private Node? ParseTemplateArg()
    {
        DemangleDepth.Enter();
        Node? result = ParseTemplateArgCore();
        DemangleDepth.Exit();
        return result;
    }

    private Node? ParseTemplateArgCore()
    {
        switch (Look())
        {
            case 'X':
            {
                ++_first;
                Node? arg = ParseExpr();
                if (arg is null || !ConsumeIf('E'))
                    return null;
                return arg;
            }

            case 'J':
            {
                ++_first;
                int argsBegin = _names.Count;
                while (!ConsumeIf('E'))
                {
                    Node? arg = ParseTemplateArg();
                    if (arg is null)
                        return null;
                    _names.Add(arg);
                }

                return new TemplateArgumentPack(PopTrailingNodeArray(argsBegin));
            }

            case 'L':
            {
                if (Look(1) == 'Z')
                {
                    _first += 2;
                    Node? arg = ParseEncoding();
                    if (arg is null || !ConsumeIf('E'))
                        return null;
                    return arg;
                }

                return ParseExprPrimary();
            }

            case 'T':
            {
                if (!IsTemplateParamDecl())
                    return ParseType();
                Node? param = ParseTemplateParamDecl(null);
                if (param is null)
                    return null;
                Node? arg = ParseTemplateArg();
                return arg is null ? null : new TemplateParamQualifiedArg(param, arg);
            }

            default:
                return ParseType();
        }
    }

    private Node? ParseTemplateArgs(bool tagTemplates = false)
    {
        if (!ConsumeIf('I'))
            return null;

        if (tagTemplates)
        {
            _templateParams.Clear();
            _templateParams.Add(_outerTemplateParams);
            _outerTemplateParams.Clear();
        }

        int argsBegin = _names.Count;
        Node? requires = null;
        while (!ConsumeIf('E'))
        {
            Node? arg = ParseTemplateArg();
            if (arg is null)
                return null;
            _names.Add(arg);
            if (tagTemplates)
            {
                Node tableEntry = arg;
                if (arg is TemplateParamQualifiedArg qualified)
                    tableEntry = qualified.Arg;
                if (arg is TemplateArgumentPack pack)
                    tableEntry = new ParameterPack(pack.Elements);
                _outerTemplateParams.Add(tableEntry);
            }

            if (ConsumeIf('Q'))
            {
                requires = ParseConstraintExpr();
                if (requires is null || !ConsumeIf('E'))
                    return null;
                break;
            }
        }

        return new TemplateArgs(PopTrailingNodeArray(argsBegin), requires);
    }

    public Node? Parse(bool parseParams = true)
    {
        bool allocToken = ConsumeIf("__alloc_token_");
        if (allocToken)
        {
            int saved = _first;
            if (ParseNumber().Length == 0 || !ConsumeIf('_'))
                _first = saved;
        }

        if (ConsumeIf("_Z") || ConsumeIf("__Z"))
        {
            Node? encoding = ParseEncoding(parseParams);
            if (encoding is null)
                return null;
            if (Look() == '.')
            {
                encoding = new DotSuffix(encoding, Slice(_first, _last));
                _first = _last;
            }

            if (allocToken)
                encoding = new DotSuffix(encoding, ".alloc_token");
            if (NumLeft != 0)
                return null;
            return encoding;
        }

        if (ConsumeIf("___Z") || ConsumeIf("____Z"))
        {
            Node? encoding = ParseEncoding(parseParams);
            if (encoding is null || !ConsumeIf("_block_invoke"))
                return null;
            bool requireNumber = ConsumeIf('_');
            if (ParseNumber().Length == 0 && requireNumber)
                return null;
            if (Look() == '.')
                _first = _last;
            if (NumLeft != 0)
                return null;
            return new SpecialName("invocation function for block in ", encoding);
        }

        Node? ty = ParseType();
        if (NumLeft != 0)
            return null;
        return ty;
    }

    /// <summary>LLVM's <c>parse_discriminator</c>.</summary>
    internal static int ParseDiscriminator(string text, int first, int last)
    {
        if (first != last)
        {
            if (text[first] == '_')
            {
                int t1 = first + 1;
                if (t1 != last)
                {
                    if (char.IsAsciiDigit(text[t1]))
                    {
                        first = t1 + 1;
                    }
                    else if (text[t1] == '_')
                    {
                        for (++t1; t1 != last && char.IsAsciiDigit(text[t1]); ++t1)
                        {
                        }

                        if (t1 != last && text[t1] == '_')
                            first = t1 + 1;
                    }
                }
            }
            else if (char.IsAsciiDigit(text[first]))
            {
                int t1 = first + 1;
                for (; t1 != last && char.IsAsciiDigit(text[t1]); ++t1)
                {
                }

                if (t1 == last)
                    first = last;
            }
        }

        return first;
    }
}
