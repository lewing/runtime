// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from LLVM's llvm/include/llvm/Demangle/ItaniumDemangle.h
// (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141, LLVM 23), which is part of the LLVM Project,
// under the Apache License v2.0 with LLVM Exceptions. See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

using System.Globalization;

internal class ExpandedSpecialSubstitution : Node
{
    public readonly SpecialSubKind Ssk;

    protected ExpandedSpecialSubstitution(SpecialSubKind ssk, NodeKind kind) : base(kind) => Ssk = ssk;

    public ExpandedSpecialSubstitution(SpecialSubKind ssk) : this(ssk, NodeKind.ExpandedSpecialSubstitution)
    {
    }

    protected bool IsInstantiation => (uint)Ssk >= (uint)SpecialSubKind.String;

    protected string ExpandedBaseName => Ssk switch
    {
        SpecialSubKind.Allocator => "allocator",
        SpecialSubKind.BasicString => "basic_string",
        SpecialSubKind.String => "basic_string",
        SpecialSubKind.IStream => "basic_istream",
        SpecialSubKind.OStream => "basic_ostream",
        _ => "basic_iostream",
    };

    protected override string GetBaseNameCore() => ExpandedBaseName;

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("std::").Append(GetBaseName());
        if (IsInstantiation)
        {
            ob.Append("<char, std::char_traits<char>");
            if (Ssk == SpecialSubKind.String)
                ob.Append(", std::allocator<char>");
            ob.Append('>');
        }
    }
}

internal sealed class SpecialSubstitution(SpecialSubKind ssk) : ExpandedSpecialSubstitution(ssk, NodeKind.SpecialSubstitution)
{
    protected override string GetBaseNameCore()
    {
        string sv = ExpandedBaseName;
        return IsInstantiation ? sv["basic_".Length..] : sv;
    }

    public override void PrintLeft(DemangleOutput ob) => ob.Append("std::").Append(GetBaseName());
}

internal sealed class CtorDtorName(Node basename, bool isDtor, int variant) : Node(NodeKind.CtorDtorName)
{
    public int Variant => variant;

    public override void PrintLeft(DemangleOutput ob)
    {
        if (isDtor)
            ob.Append('~');
        ob.Append(basename.GetBaseName());
    }
}

internal sealed class DtorName(Node @base) : Node(NodeKind.DtorName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append('~');
        ob.PrintLeft(@base);
    }
}

internal sealed class UnnamedTypeName(string count) : Node(NodeKind.UnnamedTypeName)
{
    public override void PrintLeft(DemangleOutput ob) => ob.Append("'unnamed").Append(count).Append('\'');
}

internal sealed class ClosureTypeName(NodeArray templateParams, Node? requires1, NodeArray @params, Node? requires2, string count)
    : Node(NodeKind.ClosureTypeName)
{
    public void PrintDeclarator(DemangleOutput ob)
    {
        if (!templateParams.IsEmpty)
        {
            bool saved = ob.InsideTemplate;
            ob.InsideTemplate = true;
            ob.Append('<');
            templateParams.PrintWithComma(ob);
            ob.Append('>');
            ob.InsideTemplate = saved;
        }

        if (requires1 is not null)
        {
            ob.Append(" requires ");
            requires1.Print(ob);
            ob.Append(' ');
        }

        ob.PrintOpen();
        @params.PrintWithComma(ob);
        ob.PrintClose();
        if (requires2 is not null)
        {
            ob.Append(" requires ");
            requires2.Print(ob);
        }
    }

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("'lambda").Append(count).Append('\'');
        PrintDeclarator(ob);
    }
}

internal sealed class StructuredBindingName(NodeArray bindings) : Node(NodeKind.StructuredBindingName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintOpen('[');
        bindings.PrintWithComma(ob);
        ob.PrintClose(']');
    }
}

internal sealed class BinaryExpr(Node lhs, string infixOperator, Node rhs, Prec prec) : Node(NodeKind.BinaryExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        bool parenAll = !ob.IsInParensInTemplateArgs && (infixOperator == ">" || infixOperator == ">>");
        if (parenAll)
            ob.PrintOpen();
        bool isAssign = Precedence == Prec.Assign;
        lhs.PrintAsOperand(ob, isAssign ? Prec.OrIf : Precedence, !isAssign);
        if (infixOperator != ",")
            ob.Append(' ');
        ob.Append(infixOperator).Append(' ');
        rhs.PrintAsOperand(ob, Precedence, isAssign);
        if (parenAll)
            ob.PrintClose();
    }
}

internal sealed class ArraySubscriptExpr(Node op1, Node op2, Prec prec) : Node(NodeKind.ArraySubscriptExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        op1.PrintAsOperand(ob, Precedence);
        ob.PrintOpen('[');
        op2.PrintAsOperand(ob);
        ob.PrintClose(']');
    }
}

internal sealed class PostfixExpr(Node child, string @operator, Prec prec) : Node(NodeKind.PostfixExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        child.PrintAsOperand(ob, Precedence, true);
        ob.Append(@operator);
    }
}

internal sealed class ConditionalExpr(Node cond, Node then, Node @else, Prec prec) : Node(NodeKind.ConditionalExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        cond.PrintAsOperand(ob, Precedence);
        ob.Append(" ? ");
        then.PrintAsOperand(ob);
        ob.Append(" : ");
        @else.PrintAsOperand(ob, Prec.Assign, true);
    }
}

internal sealed class MemberExpr(Node lhs, string kind, Node rhs, Prec prec) : Node(NodeKind.MemberExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        lhs.PrintAsOperand(ob, Precedence, true);
        ob.Append(kind);
        rhs.PrintAsOperand(ob, Precedence, false);
    }
}

internal sealed class SubobjectExpr(Node type, Node subExpr, string offset, NodeArray unionSelectors, bool onePastTheEnd)
    : Node(NodeKind.SubobjectExpr)
{
    public NodeArray UnionSelectors => unionSelectors;
    public bool OnePastTheEnd => onePastTheEnd;

    public override void PrintLeft(DemangleOutput ob)
    {
        subExpr.Print(ob);
        ob.Append(".<");
        type.Print(ob);
        ob.Append(" at offset ");
        if (offset.Length == 0)
            ob.Append('0');
        else if (offset[0] == 'n')
            ob.Append('-').Append(offset.AsSpan(1));
        else
            ob.Append(offset);
        ob.Append('>');
    }
}

internal sealed class EnclosingExpr(string prefix, Node infix, Prec prec = Prec.Primary) : Node(NodeKind.EnclosingExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(prefix);
        ob.PrintOpen();
        infix.Print(ob);
        ob.PrintClose();
    }
}

internal sealed class CastExpr(string castKind, Node to, Node from, Prec prec) : Node(NodeKind.CastExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(castKind);
        bool saved = ob.InsideTemplate;
        ob.InsideTemplate = true;
        ob.Append('<');
        ob.PrintLeft(to);
        ob.Append('>');
        ob.InsideTemplate = saved;
        ob.PrintOpen();
        from.PrintAsOperand(ob);
        ob.PrintClose();
    }
}

internal sealed class SizeofParamPackExpr(Node pack) : Node(NodeKind.SizeofParamPackExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("sizeof...");
        ob.PrintOpen();
        new ParameterPackExpansion(pack).PrintLeft(ob);
        ob.PrintClose();
    }
}

internal sealed class CallExpr(Node callee, NodeArray args, bool isParen, Prec prec) : Node(NodeKind.CallExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (isParen)
            ob.PrintOpen();
        callee.Print(ob);
        if (isParen)
            ob.PrintClose();
        ob.PrintOpen();
        args.PrintWithComma(ob);
        ob.PrintClose();
    }
}

internal sealed class NewExpr(NodeArray exprList, Node type, NodeArray initList, bool isGlobal, bool isArray, Prec prec)
    : Node(NodeKind.NewExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (isGlobal)
            ob.Append("::");
        ob.Append("new");
        if (isArray)
            ob.Append("[]");
        if (!exprList.IsEmpty)
        {
            ob.PrintOpen();
            exprList.PrintWithComma(ob);
            ob.PrintClose();
        }

        ob.Append(' ');
        type.Print(ob);
        if (!initList.IsEmpty)
        {
            ob.PrintOpen();
            initList.PrintWithComma(ob);
            ob.PrintClose();
        }
    }
}

internal sealed class DeleteExpr(Node op, bool isGlobal, bool isArray, Prec prec) : Node(NodeKind.DeleteExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (isGlobal)
            ob.Append("::");
        ob.Append("delete");
        if (isArray)
            ob.Append("[]");
        ob.Append(' ');
        op.Print(ob);
    }
}

internal sealed class PrefixExpr(string prefix, Node child, Prec prec) : Node(NodeKind.PrefixExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(prefix);
        child.PrintAsOperand(ob, Precedence);
    }
}

internal sealed class FunctionParam(string number) : Node(NodeKind.FunctionParam)
{
    public override void PrintLeft(DemangleOutput ob) => ob.Append("fp").Append(number);
}

internal sealed class ConversionExpr(Node type, NodeArray expressions, Prec prec) : Node(NodeKind.ConversionExpr, prec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintOpen();
        type.Print(ob);
        ob.PrintClose();
        ob.PrintOpen();
        expressions.PrintWithComma(ob);
        ob.PrintClose();
    }
}

internal sealed class PointerToMemberConversionExpr(Node type, Node subExpr, string offset, Prec prec)
    : Node(NodeKind.PointerToMemberConversionExpr, prec)
{
    public string Offset => offset;

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintOpen();
        type.Print(ob);
        ob.PrintClose();
        ob.PrintOpen();
        subExpr.Print(ob);
        ob.PrintClose();
    }
}

internal sealed class InitListExpr(Node? ty, NodeArray inits) : Node(NodeKind.InitListExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (ty is not null)
        {
            if (ty.PrintInitListAsType(ob, inits))
                return;
            ty.Print(ob);
        }

        ob.Append('{');
        inits.PrintWithComma(ob);
        ob.Append('}');
    }
}

internal sealed class BracedExpr(Node elem, Node init, bool isArray) : Node(NodeKind.BracedExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (isArray)
        {
            ob.Append('[');
            elem.Print(ob);
            ob.Append(']');
        }
        else
        {
            ob.Append('.');
            elem.Print(ob);
        }

        if (init.Kind != NodeKind.BracedExpr && init.Kind != NodeKind.BracedRangeExpr)
            ob.Append(" = ");
        init.Print(ob);
    }
}

internal sealed class BracedRangeExpr(Node first, Node last, Node init) : Node(NodeKind.BracedRangeExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append('[');
        first.Print(ob);
        ob.Append(" ... ");
        last.Print(ob);
        ob.Append(']');
        if (init.Kind != NodeKind.BracedExpr && init.Kind != NodeKind.BracedRangeExpr)
            ob.Append(" = ");
        init.Print(ob);
    }
}

internal sealed class FoldExpr(bool isLeftFold, string operatorName, Node pack, Node? init) : Node(NodeKind.FoldExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        void PrintPack()
        {
            ob.PrintOpen();
            new ParameterPackExpansion(pack).Print(ob);
            ob.PrintClose();
        }

        ob.PrintOpen();
        if (!isLeftFold || init is not null)
        {
            if (isLeftFold)
                init!.PrintAsOperand(ob, Prec.Cast, true);
            else
                PrintPack();
            ob.Append(' ').Append(operatorName).Append(' ');
        }

        ob.Append("...");
        if (isLeftFold || init is not null)
        {
            ob.Append(' ').Append(operatorName).Append(' ');
            if (isLeftFold)
                PrintPack();
            else
                init!.PrintAsOperand(ob, Prec.Cast, true);
        }

        ob.PrintClose();
    }
}

internal sealed class ThrowExpr(Node op) : Node(NodeKind.ThrowExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("throw ");
        op.Print(ob);
    }
}

internal sealed class BoolExpr(bool value) : Node(NodeKind.BoolExpr)
{
    public override void PrintLeft(DemangleOutput ob) => ob.Append(value ? "true" : "false");
}

internal sealed class StringLiteral(Node type) : Node(NodeKind.StringLiteral)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("\"<");
        type.Print(ob);
        ob.Append(">\"");
    }
}

internal sealed class LambdaExpr(Node type) : Node(NodeKind.LambdaExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("[]");
        if (type is ClosureTypeName closure)
            closure.PrintDeclarator(ob);
        ob.Append("{...}");
    }
}

internal sealed class EnumLiteral(Node ty, string integer) : Node(NodeKind.EnumLiteral)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintOpen();
        ty.Print(ob);
        ob.PrintClose();
        if (integer[0] == 'n')
            ob.Append('-').Append(integer.AsSpan(1));
        else
            ob.Append(integer);
    }
}

internal sealed class IntegerLiteral(string type, string value) : Node(NodeKind.IntegerLiteral)
{
    public string Value => value;

    public override void PrintLeft(DemangleOutput ob)
    {
        if (type.Length > 3)
        {
            ob.PrintOpen();
            ob.Append(type);
            ob.PrintClose();
        }

        if (value[0] == 'n')
            ob.Append('-').Append(value.AsSpan(1));
        else
            ob.Append(value);
        if (type.Length <= 3)
            ob.Append(type);
    }
}

internal sealed class RequiresExpr(NodeArray parameters, NodeArray requirements) : Node(NodeKind.RequiresExpr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("requires");
        if (!parameters.IsEmpty)
        {
            ob.Append(' ');
            ob.PrintOpen();
            parameters.PrintWithComma(ob);
            ob.PrintClose();
        }

        ob.Append(' ');
        ob.PrintOpen('{');
        foreach (Node req in requirements.Items)
            req.Print(ob);
        ob.Append(' ');
        ob.PrintClose('}');
    }
}

internal sealed class ExprRequirement(Node expr, bool isNoexcept, Node? typeConstraint) : Node(NodeKind.ExprRequirement)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(' ');
        if (isNoexcept || typeConstraint is not null)
            ob.PrintOpen('{');
        expr.Print(ob);
        if (isNoexcept || typeConstraint is not null)
            ob.PrintClose('}');
        if (isNoexcept)
            ob.Append(" noexcept");
        if (typeConstraint is not null)
        {
            ob.Append(" -> ");
            typeConstraint.Print(ob);
        }

        ob.Append(';');
    }
}

internal sealed class TypeRequirement(Node type) : Node(NodeKind.TypeRequirement)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(" typename ");
        type.Print(ob);
        ob.Append(';');
    }
}

internal sealed class NestedRequirement(Node constraint) : Node(NodeKind.NestedRequirement)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(" requires ");
        constraint.Print(ob);
        ob.Append(';');
    }
}

/// <summary>
/// <c>FloatLiteralImpl</c>: the mangled hex digits are the value's bytes, printed with the host C library's
/// <c>%a</c> (hexadecimal floating point) as LLVM does through <c>snprintf</c>.
/// </summary>
internal sealed class FloatLiteralNode(NodeKind kind, string contents) : Node(kind)
{
    /// <summary>Hex digits in the mangling: 8 for float, 16 for double; long double follows the host (16 on LLD's arm64 hosts).</summary>
    public static int MangledSize(NodeKind kind) => kind == NodeKind.FloatLiteral ? 8 : 16;

    public override void PrintLeft(DemangleOutput ob)
    {
        int n = MangledSize(Kind);
        if (contents.Length < n)
            return;
        ulong bits = ulong.Parse(contents.AsSpan(0, n), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture);
        string text = Kind == NodeKind.FloatLiteral
            ? DemangleNodeHelpers.HexFloat(BitConverter.UInt32BitsToSingle((uint)bits), 23, 8, (uint)bits) + "f"
            : DemangleNodeHelpers.HexFloat(BitConverter.UInt64BitsToDouble(bits), 52, 11, bits) + (Kind == NodeKind.LongDoubleLiteral ? "L" : "");
        ob.Append(text);
    }
}

internal static partial class DemangleNodeHelpers
{
    public static bool PrintAsString(NodeArray elements, DemangleOutput ob)
    {
        int start = ob.Position;
        bool Fail()
        {
            ob.Position = start;
            return false;
        }

        ob.Append('"');
        bool lastWasNumericEscape = false;
        foreach (Node element in elements.Items)
        {
            if (element is not IntegerLiteral literal)
                return Fail();
            int value = 0;
            foreach (char c in literal.Value)
            {
                if (c < '0' || c > '9' || value > 25)
                    return Fail();
                value = value * 10 + (c - '0');
            }

            if (value > 255)
                return Fail();
            if (lastWasNumericEscape && (value is >= '0' and <= '9' || value is >= 'a' and <= 'f' || value is >= 'A' and <= 'F'))
                ob.Append("\"\"");
            lastWasNumericEscape = false;
            switch (value)
            {
                case '\a': ob.Append("\\a"); break;
                case '\b': ob.Append("\\b"); break;
                case '\f': ob.Append("\\f"); break;
                case '\n': ob.Append("\\n"); break;
                case '\r': ob.Append("\\r"); break;
                case '\t': ob.Append("\\t"); break;
                case '\v': ob.Append("\\v"); break;
                case '"': ob.Append("\\\""); break;
                case '\\': ob.Append("\\\\"); break;
                default:
                    if (value < 32 || value == 127)
                    {
                        const string Hex = "0123456789ABCDEF";
                        ob.Append('\\');
                        if (value > 7)
                            ob.Append('x');
                        if (value >= 16)
                            ob.Append(Hex[value >> 4]);
                        ob.Append(Hex[value & 0xF]);
                        lastWasNumericEscape = true;
                        break;
                    }

                    ob.Append((char)value);
                    break;
            }
        }

        ob.Append('"');
        return true;
    }

    /// <summary>C's <c>%a</c> for an IEEE binary value: <c>0x1.8p+1</c>, trailing zero hex digits dropped.</summary>
    public static string HexFloat(double value, int mantissaBits, int exponentBits, ulong bits)
    {
        bool negative = (bits >> (mantissaBits + exponentBits)) != 0;
        int bias = (1 << (exponentBits - 1)) - 1;
        long exponentField = (long)((bits >> mantissaBits) & ((1UL << exponentBits) - 1));
        ulong mantissa = bits & ((1UL << mantissaBits) - 1);
        string sign = negative ? "-" : "";
        if (exponentField == (1L << exponentBits) - 1)
            return sign + (mantissa == 0 ? "inf" : "nan");
        if (exponentField == 0 && mantissa == 0)
            return sign + "0x0p+0";

        // Widen to the 52-bit double layout so the hex digits group by 4 bits.
        int shift = 52 - mantissaBits;
        ulong m = mantissa << shift;
        long exponent;
        string lead;
        if (exponentField == 0)
        {
            // Subnormal: printed as 0x0.xxxp-bias+1, as glibc and Apple's libc do.
            lead = "0";
            exponent = 1 - bias;
        }
        else
        {
            lead = "1";
            exponent = exponentField - bias;
        }

        string digits = m.ToString("x13", CultureInfo.InvariantCulture).TrimEnd('0');
        _ = value;
        return sign + "0x" + lead + (digits.Length > 0 ? "." + digits : "") + "p" + (exponent >= 0 ? "+" : "") +
            exponent.ToString(CultureInfo.InvariantCulture);
    }
}
