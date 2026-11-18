// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from LLVM's llvm/include/llvm/Demangle/ItaniumDemangle.h and Utility.h
// (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141, LLVM 23), which is part of the LLVM Project,
// under the Apache License v2.0 with LLVM Exceptions. See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

// SA1136: the compact enum lists mirror LLVM's ItaniumNodes.def and enum declarations, one value per line
// would break that mapping. This file is a line-for-line port kept diffable against LLVM.
#pragma warning disable SA1136

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

using System.Text;

/// <summary>LLVM's <c>OutputBuffer</c>: the demangled text plus the state printing threads through it.</summary>
internal sealed class DemangleOutput
{
    private readonly StringBuilder _buffer = new();

    public uint CurrentPackIndex = uint.MaxValue;
    public uint CurrentPackMax = uint.MaxValue;
    public uint ParenDepth;
    public bool InsideTemplate;

    public bool IsInParensInTemplateArgs => ParenDepth > 0;
    public bool IsInsideTemplateArgs => InsideTemplate;

    public void PrintOpen(char open = '(')
    {
        if (IsInsideTemplateArgs)
            ParenDepth++;
        _buffer.Append(open);
    }

    public void PrintClose(char close = ')')
    {
        if (IsInsideTemplateArgs)
            ParenDepth--;
        _buffer.Append(close);
    }

    public DemangleOutput Append(string s)
    {
        _buffer.Append(s);
        return this;
    }

    public DemangleOutput Append(ReadOnlySpan<char> s)
    {
        _buffer.Append(s);
        return this;
    }

    public DemangleOutput Append(char c)
    {
        _buffer.Append(c);
        return this;
    }

    public DemangleOutput Append(long n)
    {
        _buffer.Append(System.Globalization.CultureInfo.InvariantCulture, $"{n}");
        return this;
    }

    public DemangleOutput AppendUnsigned(ulong n)
    {
        _buffer.Append(System.Globalization.CultureInfo.InvariantCulture, $"{n}");
        return this;
    }

    public void Prepend(string s) => _buffer.Insert(0, s);

    public void Insert(int position, string s) => _buffer.Insert(position, s);

    public int Position
    {
        get => _buffer.Length;
        set => _buffer.Length = value;
    }

    public char Back => _buffer[^1];

    public bool IsEmpty => _buffer.Length == 0;

    public char CharAt(int index) => _buffer[index];

    public string Substring(int start, int length) => _buffer.ToString(start, length);

    public void PrintLeft(Node n)
    {
        DemangleDepth.Enter();
        n.PrintLeft(this);
        DemangleDepth.Exit();
    }

    public void PrintRight(Node n)
    {
        DemangleDepth.Enter();
        n.PrintRight(this);
        DemangleDepth.Exit();
    }

    public override string ToString() => _buffer.ToString();
}

internal enum NodeKind : byte
{
    NodeArrayNode, DotSuffix, VendorExtQualType, QualType, ConversionOperatorType, PostfixQualifiedType,
    ElaboratedTypeSpefType, TransformedType, NameType, AbiTagAttr, EnableIfAttr, ObjCProtoName, PointerType,
    ReferenceType, PointerToMemberType, ArrayType, FunctionType, NoexceptSpec, DynamicExceptionSpec,
    FunctionEncoding, LiteralOperator, SpecialName, CtorVtableSpecialName, QualifiedName, NestedName,
    MemberLikeFriendName, LocalName, ModuleName, ModuleEntity, VectorType, PixelVectorType, BinaryFPType,
    BitIntType, SyntheticTemplateParamName, TemplateParamQualifiedArg, TypeTemplateParamDecl,
    ConstrainedTypeTemplateParamDecl, NonTypeTemplateParamDecl, TemplateTemplateParamDecl, TemplateParamPackDecl,
    ParameterPack, TemplateArgumentPack, ParameterPackExpansion, TemplateArgs, ForwardTemplateReference,
    NameWithTemplateArgs, GlobalQualifiedName, ExpandedSpecialSubstitution, SpecialSubstitution, CtorDtorName,
    DtorName, UnnamedTypeName, ClosureTypeName, StructuredBindingName, BinaryExpr, ArraySubscriptExpr,
    PostfixExpr, ConditionalExpr, MemberExpr, SubobjectExpr, EnclosingExpr, CastExpr, SizeofParamPackExpr,
    CallExpr, NewExpr, DeleteExpr, PrefixExpr, FunctionParam, ConversionExpr, PointerToMemberConversionExpr,
    InitListExpr, FoldExpr, ThrowExpr, BoolExpr, StringLiteral, LambdaExpr, EnumLiteral, IntegerLiteral,
    FloatLiteral, DoubleLiteral, LongDoubleLiteral, BracedExpr, BracedRangeExpr, RequiresExpr, ExprRequirement,
    TypeRequirement, NestedRequirement, ExplicitObjectParameter, PackIndexing,
}

internal enum Cache : byte { Yes, No, Unknown }

internal enum Prec : byte
{
    Primary, Postfix, Unary, Cast, PtrMem, Multiplicative, Additive, Shift, Spaceship, Relational, Equality,
    And, Xor, Ior, AndIf, OrIf, Conditional, Assign, Comma, Default,
}

[System.Flags]
internal enum Qualifiers { None = 0, Const = 1, Volatile = 2, Restrict = 4 }

internal enum FunctionRefQual : byte { None, LValue, RValue }

internal enum ReferenceKind { LValue, RValue }

internal enum TemplateParamKind { Type, NonType, Template }

internal enum SpecialSubKind { Allocator, BasicString, String, IStream, OStream, IOStream }

internal abstract class Node
{
    public readonly NodeKind Kind;
    public readonly Prec Precedence;
    protected Cache RhsComponentCache;
    protected Cache ArrayCache;
    protected Cache FunctionCache;

    protected Node(NodeKind kind, Prec precedence = Prec.Primary, Cache rhs = Cache.No, Cache array = Cache.No, Cache function = Cache.No)
    {
        Kind = kind;
        Precedence = precedence;
        RhsComponentCache = rhs;
        ArrayCache = array;
        FunctionCache = function;
    }

    protected Node(NodeKind kind, Cache rhs, Cache array = Cache.No, Cache function = Cache.No)
        : this(kind, Prec.Primary, rhs, array, function)
    {
    }

    public Cache GetRhsComponentCache() => RhsComponentCache;
    public Cache GetArrayCache() => ArrayCache;
    public Cache GetFunctionCache() => FunctionCache;

    // Every recursion through the node graph passes one of these entry points, which count it (see DemangleDepth); the
    // virtuals they call are protected or reached only through them.
    public bool HasRhsComponent(DemangleOutput ob) =>
        RhsComponentCache != Cache.Unknown ? RhsComponentCache == Cache.Yes : Guarded(this, ob, static (n, o) => n.HasRhsComponentSlow(o));

    public bool HasArray(DemangleOutput ob) =>
        ArrayCache != Cache.Unknown ? ArrayCache == Cache.Yes : Guarded(this, ob, static (n, o) => n.HasArraySlow(o));

    public bool HasFunction(DemangleOutput ob) =>
        FunctionCache != Cache.Unknown ? FunctionCache == Cache.Yes : Guarded(this, ob, static (n, o) => n.HasFunctionSlow(o));

    public Node GetSyntaxNode(DemangleOutput ob) => Guarded(this, ob, static (n, o) => n.GetSyntaxNodeCore(o));

    public string GetBaseName()
    {
        DemangleDepth.Enter();
        string result = GetBaseNameCore();
        DemangleDepth.Exit();
        return result;
    }

    private static T Guarded<T>(Node node, DemangleOutput ob, Func<Node, DemangleOutput, T> body)
    {
        DemangleDepth.Enter();
        T result = body(node, ob);
        DemangleDepth.Exit();
        return result;
    }

    public virtual bool HasRhsComponentSlow(DemangleOutput ob) => false;
    public virtual bool HasArraySlow(DemangleOutput ob) => false;
    public virtual bool HasFunctionSlow(DemangleOutput ob) => false;
    protected virtual Node GetSyntaxNodeCore(DemangleOutput ob) => this;

    public void PrintAsOperand(DemangleOutput ob, Prec p = Prec.Default, bool strictlyWorse = false)
    {
        bool paren = (uint)Precedence >= (uint)p + (strictlyWorse ? 1u : 0u);
        if (paren)
            ob.PrintOpen();
        Print(ob);
        if (paren)
            ob.PrintClose();
    }

    public void Print(DemangleOutput ob)
    {
        DemangleDepth.Enter();
        ob.PrintLeft(this);
        if (RhsComponentCache != Cache.No)
            ob.PrintRight(this);
        DemangleDepth.Exit();
    }

    public virtual bool PrintInitListAsType(DemangleOutput ob, NodeArray elements) => false;

    protected virtual string GetBaseNameCore() => "";

    public abstract void PrintLeft(DemangleOutput ob);

    public virtual void PrintRight(DemangleOutput ob)
    {
    }
}

internal readonly struct NodeArray(Node[] elements)
{
    private readonly Node[]? _elements = elements;

    public int Count => _elements?.Length ?? 0;
    public bool IsEmpty => Count == 0;
    public Node this[int index] => _elements![index];
    public Node[] Items => _elements ?? [];

    public void PrintWithComma(DemangleOutput ob)
    {
        bool first = true;
        for (int i = 0; i < Count; i++)
        {
            int beforeComma = ob.Position;
            if (!first)
                ob.Append(", ");
            int afterComma = ob.Position;
            _elements![i].PrintAsOperand(ob, Prec.Comma);
            if (afterComma == ob.Position)
            {
                ob.Position = beforeComma;
                continue;
            }

            first = false;
        }
    }

    public bool PrintAsString(DemangleOutput ob) => DemangleNodeHelpers.PrintAsString(this, ob);
}

internal sealed class NodeArrayNode(NodeArray array) : Node(NodeKind.NodeArrayNode)
{
    public readonly NodeArray Array = array;
    public override void PrintLeft(DemangleOutput ob) => Array.PrintWithComma(ob);
}

internal sealed class DotSuffix(Node prefix, string suffix) : Node(NodeKind.DotSuffix)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        prefix.Print(ob);
        ob.Append(" (").Append(suffix).Append(")");
    }
}

internal sealed class VendorExtQualType(Node ty, string ext, Node? ta) : Node(NodeKind.VendorExtQualType)
{
    public Node Ty => ty;
    public string Ext => ext;
    public Node? TA => ta;

    public override void PrintLeft(DemangleOutput ob)
    {
        ty.Print(ob);
        ob.Append(' ').Append(ext);
        ta?.Print(ob);
    }
}

internal sealed class QualType : Node
{
    public readonly Qualifiers Quals;
    public readonly Node Child;

    public QualType(Node child, Qualifiers quals)
        : base(NodeKind.QualType, child.GetRhsComponentCache(), child.GetArrayCache(), child.GetFunctionCache())
    {
        Quals = quals;
        Child = child;
    }

    private void PrintQuals(DemangleOutput ob)
    {
        if ((Quals & Qualifiers.Const) != 0)
            ob.Append(" const");
        if ((Quals & Qualifiers.Volatile) != 0)
            ob.Append(" volatile");
        if ((Quals & Qualifiers.Restrict) != 0)
            ob.Append(" restrict");
    }

    public override bool HasRhsComponentSlow(DemangleOutput ob) => Child.HasRhsComponent(ob);
    public override bool HasArraySlow(DemangleOutput ob) => Child.HasArray(ob);
    public override bool HasFunctionSlow(DemangleOutput ob) => Child.HasFunction(ob);

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(Child);
        PrintQuals(ob);
    }

    public override void PrintRight(DemangleOutput ob) => ob.PrintRight(Child);
}

internal sealed class ConversionOperatorType(Node ty) : Node(NodeKind.ConversionOperatorType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("operator ");
        ty.Print(ob);
    }
}

internal sealed class PostfixQualifiedType(Node ty, string postfix) : Node(NodeKind.PostfixQualifiedType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(ty);
        ob.Append(postfix);
    }
}

internal sealed class NameType(string name) : Node(NodeKind.NameType)
{
    public string Name => name;
    protected override string GetBaseNameCore() => name;
    public override void PrintLeft(DemangleOutput ob) => ob.Append(name);
}

internal sealed class BitIntType(Node size, bool signed) : Node(NodeKind.BitIntType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        if (!signed)
            ob.Append("unsigned ");
        ob.Append("_BitInt");
        ob.PrintOpen();
        size.PrintAsOperand(ob);
        ob.PrintClose();
    }
}

internal sealed class ElaboratedTypeSpefType(string kind, Node child) : Node(NodeKind.ElaboratedTypeSpefType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(kind).Append(' ');
        child.Print(ob);
    }
}

internal sealed class TransformedType(string transform, Node baseType) : Node(NodeKind.TransformedType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(transform).Append('(');
        baseType.Print(ob);
        ob.Append(')');
    }
}

internal sealed class AbiTagAttr : Node
{
    public readonly Node Base;
    public readonly string Tag;

    public AbiTagAttr(Node @base, string tag)
        : base(NodeKind.AbiTagAttr, @base.GetRhsComponentCache(), @base.GetArrayCache(), @base.GetFunctionCache())
    {
        Base = @base;
        Tag = tag;
    }

    protected override string GetBaseNameCore() => Base.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(Base);
        ob.Append("[abi:").Append(Tag).Append(']');
    }
}

internal sealed class EnableIfAttr(NodeArray conditions) : Node(NodeKind.EnableIfAttr)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(" [enable_if:");
        conditions.PrintWithComma(ob);
        ob.Append(']');
    }
}

internal sealed class ObjCProtoName(Node ty, string protocol) : Node(NodeKind.ObjCProtoName)
{
    public string Protocol => protocol;

    public bool IsObjCObject() => ty is NameType { Name: "objc_object" };

    public override void PrintLeft(DemangleOutput ob)
    {
        ty.Print(ob);
        ob.Append('<').Append(protocol).Append('>');
    }
}

internal sealed class PointerType(Node pointee) : Node(NodeKind.PointerType, pointee.GetRhsComponentCache())
{
    public Node Pointee => pointee;

    public override bool HasRhsComponentSlow(DemangleOutput ob) => pointee.HasRhsComponent(ob);

    public override void PrintLeft(DemangleOutput ob)
    {
        if (pointee is not ObjCProtoName proto || !proto.IsObjCObject())
        {
            ob.PrintLeft(pointee);
            if (pointee.HasArray(ob))
                ob.Append(' ');
            if (pointee.HasArray(ob) || pointee.HasFunction(ob))
                ob.Append('(');
            ob.Append('*');
        }
        else
        {
            ob.Append("id<").Append(proto.Protocol).Append('>');
        }
    }

    public override void PrintRight(DemangleOutput ob)
    {
        if (pointee is not ObjCProtoName proto || !proto.IsObjCObject())
        {
            if (pointee.HasArray(ob) || pointee.HasFunction(ob))
                ob.Append(')');
            ob.PrintRight(pointee);
        }
    }
}

internal sealed class ReferenceType(Node pointee, ReferenceKind rk) : Node(NodeKind.ReferenceType, pointee.GetRhsComponentCache())
{
    private bool _printing;

    public Node Pointee => pointee;
    public ReferenceKind Rk => rk;

    private (ReferenceKind Kind, Node? Pointee) Collapse(DemangleOutput ob)
    {
        var kind = rk;
        Node? current = pointee;
        var prev = new List<Node>();
        while (true)
        {
            Node sn = current!.GetSyntaxNode(ob);
            if (sn is not ReferenceType rt)
                break;
            current = rt.Pointee;
            kind = (ReferenceKind)Math.Min((int)kind, (int)rt.Rk);
            prev.Add(current);
            if (prev.Count > 1 && ReferenceEquals(current, prev[(prev.Count - 1) / 2]))
            {
                current = null;
                break;
            }
        }

        return (kind, current);
    }

    public override bool HasRhsComponentSlow(DemangleOutput ob) => pointee.HasRhsComponent(ob);

    public override void PrintLeft(DemangleOutput ob)
    {
        if (_printing)
            return;
        _printing = true;
        try
        {
            var (kind, collapsed) = Collapse(ob);
            if (collapsed is null)
                return;
            ob.PrintLeft(collapsed);
            if (collapsed.HasArray(ob))
                ob.Append(' ');
            if (collapsed.HasArray(ob) || collapsed.HasFunction(ob))
                ob.Append('(');
            ob.Append(kind == ReferenceKind.LValue ? "&" : "&&");
        }
        finally
        {
            _printing = false;
        }
    }

    public override void PrintRight(DemangleOutput ob)
    {
        if (_printing)
            return;
        _printing = true;
        try
        {
            var (_, collapsed) = Collapse(ob);
            if (collapsed is null)
                return;
            if (collapsed.HasArray(ob) || collapsed.HasFunction(ob))
                ob.Append(')');
            ob.PrintRight(collapsed);
        }
        finally
        {
            _printing = false;
        }
    }
}

internal sealed class PointerToMemberType(Node classType, Node memberType)
    : Node(NodeKind.PointerToMemberType, memberType.GetRhsComponentCache())
{
    public override bool HasRhsComponentSlow(DemangleOutput ob) => memberType.HasRhsComponent(ob);

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(memberType);
        if (memberType.HasArray(ob) || memberType.HasFunction(ob))
            ob.Append('(');
        else
            ob.Append(' ');
        classType.Print(ob);
        ob.Append("::*");
    }

    public override void PrintRight(DemangleOutput ob)
    {
        if (memberType.HasArray(ob) || memberType.HasFunction(ob))
            ob.Append(')');
        ob.PrintRight(memberType);
    }
}

internal sealed class ArrayType(Node @base, Node? dimension) : Node(NodeKind.ArrayType, Cache.Yes, Cache.Yes)
{
    public override bool HasRhsComponentSlow(DemangleOutput ob) => true;
    public override bool HasArraySlow(DemangleOutput ob) => true;
    public override void PrintLeft(DemangleOutput ob) => ob.PrintLeft(@base);

    public override void PrintRight(DemangleOutput ob)
    {
        if (ob.Back != ']')
            ob.Append(' ');
        ob.Append('[');
        dimension?.Print(ob);
        ob.Append(']');
        ob.PrintRight(@base);
    }

    public override bool PrintInitListAsType(DemangleOutput ob, NodeArray elements) =>
        @base is NameType { Name: "char" } && elements.PrintAsString(ob);
}

internal sealed class FunctionType(Node ret, NodeArray @params, Qualifiers cvQuals, FunctionRefQual refQual, Node? exceptionSpec)
    : Node(NodeKind.FunctionType, Cache.Yes, Cache.No, Cache.Yes)
{
    public override bool HasRhsComponentSlow(DemangleOutput ob) => true;
    public override bool HasFunctionSlow(DemangleOutput ob) => true;

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(ret);
        ob.Append(' ');
    }

    public override void PrintRight(DemangleOutput ob)
    {
        ob.PrintOpen();
        @params.PrintWithComma(ob);
        ob.PrintClose();
        ob.PrintRight(ret);
        DemangleNodeHelpers.PrintCvRef(ob, cvQuals, refQual);
        if (exceptionSpec is not null)
        {
            ob.Append(' ');
            exceptionSpec.Print(ob);
        }
    }
}

internal sealed class NoexceptSpec(Node e) : Node(NodeKind.NoexceptSpec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("noexcept");
        ob.PrintOpen();
        e.PrintAsOperand(ob);
        ob.PrintClose();
    }
}

internal sealed class DynamicExceptionSpec(NodeArray types) : Node(NodeKind.DynamicExceptionSpec)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("throw");
        ob.PrintOpen();
        types.PrintWithComma(ob);
        ob.PrintClose();
    }
}

internal sealed class ExplicitObjectParameter(Node @base) : Node(NodeKind.ExplicitObjectParameter)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("this ");
        @base.Print(ob);
    }
}

internal sealed class FunctionEncoding(Node? ret, Node name, NodeArray @params, Node? attrs, Node? requires, Qualifiers cvQuals, FunctionRefQual refQual)
    : Node(NodeKind.FunctionEncoding, Cache.Yes, Cache.No, Cache.Yes)
{
    public Node Name => name;

    public override bool HasRhsComponentSlow(DemangleOutput ob) => true;
    public override bool HasFunctionSlow(DemangleOutput ob) => true;

    public override void PrintLeft(DemangleOutput ob)
    {
        if (ret is not null)
        {
            ob.PrintLeft(ret);
            if (!ret.HasRhsComponent(ob))
                ob.Append(' ');
        }

        name.Print(ob);
    }

    public override void PrintRight(DemangleOutput ob)
    {
        ob.PrintOpen();
        @params.PrintWithComma(ob);
        ob.PrintClose();
        if (ret is not null)
            ob.PrintRight(ret);
        DemangleNodeHelpers.PrintCvRef(ob, cvQuals, refQual);
        attrs?.Print(ob);
        if (requires is not null)
        {
            ob.Append(" requires ");
            requires.Print(ob);
        }
    }
}

internal sealed class LiteralOperator(Node opName) : Node(NodeKind.LiteralOperator)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("operator\"\" ");
        opName.Print(ob);
    }
}

internal sealed class SpecialName(string special, Node child) : Node(NodeKind.SpecialName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(special);
        child.Print(ob);
    }
}

internal sealed class CtorVtableSpecialName(Node firstType, Node secondType) : Node(NodeKind.CtorVtableSpecialName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("construction vtable for ");
        firstType.Print(ob);
        ob.Append("-in-");
        secondType.Print(ob);
    }
}

internal sealed class NestedName(Node qual, Node name) : Node(NodeKind.NestedName)
{
    public Node Qual => qual;
    public Node Name => name;
    protected override string GetBaseNameCore() => name.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        qual.Print(ob);
        ob.Append("::");
        name.Print(ob);
    }
}

internal sealed class MemberLikeFriendName(Node qual, Node name) : Node(NodeKind.MemberLikeFriendName)
{
    protected override string GetBaseNameCore() => name.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        qual.Print(ob);
        ob.Append("::friend ");
        name.Print(ob);
    }
}

internal sealed class ModuleName(ModuleName? parent, Node name, bool isPartition = false) : Node(NodeKind.ModuleName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        parent?.Print(ob);
        if (parent is not null || isPartition)
            ob.Append(isPartition ? ':' : '.');
        name.Print(ob);
    }
}

internal sealed class ModuleEntity(ModuleName module, Node name) : Node(NodeKind.ModuleEntity)
{
    public Node Name => name;
    protected override string GetBaseNameCore() => name.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        name.Print(ob);
        ob.Append('@');
        module.Print(ob);
    }
}

internal sealed class LocalName(Node encoding, Node entity) : Node(NodeKind.LocalName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        encoding.Print(ob);
        ob.Append("::");
        entity.Print(ob);
    }
}

internal sealed class QualifiedName(Node qualifier, Node name) : Node(NodeKind.QualifiedName)
{
    protected override string GetBaseNameCore() => name.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        qualifier.Print(ob);
        ob.Append("::");
        name.Print(ob);
    }
}

internal sealed class VectorType(Node baseType, Node? dimension) : Node(NodeKind.VectorType)
{
    public Node BaseType => baseType;
    public Node? Dimension => dimension;

    public override void PrintLeft(DemangleOutput ob)
    {
        baseType.Print(ob);
        ob.Append(" vector[");
        dimension?.Print(ob);
        ob.Append(']');
    }
}

internal sealed class PixelVectorType(Node dimension) : Node(NodeKind.PixelVectorType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("pixel vector[");
        dimension.Print(ob);
        ob.Append(']');
    }
}

internal sealed class BinaryFPType(Node dimension) : Node(NodeKind.BinaryFPType)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("_Float");
        dimension.Print(ob);
    }
}

internal sealed class SyntheticTemplateParamName(TemplateParamKind kind, uint index) : Node(NodeKind.SyntheticTemplateParamName)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append(kind switch { TemplateParamKind.Type => "$T", TemplateParamKind.NonType => "$N", _ => "$TT" });
        if (index > 0)
            ob.AppendUnsigned(index - 1);
    }
}

internal sealed class TemplateParamQualifiedArg(Node param, Node arg) : Node(NodeKind.TemplateParamQualifiedArg)
{
    public Node Param => param;
    public Node Arg => arg;
    public override void PrintLeft(DemangleOutput ob) => arg.Print(ob);
}

internal sealed class TypeTemplateParamDecl(Node name) : Node(NodeKind.TypeTemplateParamDecl, Cache.Yes)
{
    public override void PrintLeft(DemangleOutput ob) => ob.Append("typename ");
    public override void PrintRight(DemangleOutput ob) => name.Print(ob);
}

internal sealed class ConstrainedTypeTemplateParamDecl(Node constraint, Node name) : Node(NodeKind.ConstrainedTypeTemplateParamDecl, Cache.Yes)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        constraint.Print(ob);
        ob.Append(' ');
    }

    public override void PrintRight(DemangleOutput ob) => name.Print(ob);
}

internal sealed class NonTypeTemplateParamDecl(Node name, Node type) : Node(NodeKind.NonTypeTemplateParamDecl, Cache.Yes)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(type);
        if (!type.HasRhsComponent(ob))
            ob.Append(' ');
    }

    public override void PrintRight(DemangleOutput ob)
    {
        name.Print(ob);
        ob.PrintRight(type);
    }
}

internal sealed class TemplateTemplateParamDecl(Node name, NodeArray @params, Node? requires) : Node(NodeKind.TemplateTemplateParamDecl, Cache.Yes)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        bool saved = ob.InsideTemplate;
        ob.InsideTemplate = true;
        ob.Append("template<");
        @params.PrintWithComma(ob);
        ob.Append("> typename ");
        ob.InsideTemplate = saved;
    }

    public override void PrintRight(DemangleOutput ob)
    {
        name.Print(ob);
        if (requires is not null)
        {
            ob.Append(" requires ");
            requires.Print(ob);
        }
    }
}

internal sealed class TemplateParamPackDecl(Node param) : Node(NodeKind.TemplateParamPackDecl, Cache.Yes)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintLeft(param);
        ob.Append("...");
    }

    public override void PrintRight(DemangleOutput ob) => ob.PrintRight(param);
}

internal sealed class ParameterPack : Node
{
    public readonly NodeArray Data;

    public ParameterPack(NodeArray data) : base(NodeKind.ParameterPack)
    {
        Data = data;
        bool noArray = true, noFunction = true, noRhs = true;
        foreach (Node p in data.Items)
        {
            noArray &= p.GetArrayCache() == Cache.No;
            noFunction &= p.GetFunctionCache() == Cache.No;
            noRhs &= p.GetRhsComponentCache() == Cache.No;
        }

        ArrayCache = noArray ? Cache.No : Cache.Unknown;
        FunctionCache = noFunction ? Cache.No : Cache.Unknown;
        RhsComponentCache = noRhs ? Cache.No : Cache.Unknown;
    }

    private void InitializePackExpansion(DemangleOutput ob)
    {
        if (ob.CurrentPackMax == uint.MaxValue)
        {
            ob.CurrentPackMax = (uint)Data.Count;
            ob.CurrentPackIndex = 0;
        }
    }

    private Node? Current(DemangleOutput ob)
    {
        InitializePackExpansion(ob);
        uint index = ob.CurrentPackIndex;
        return index < Data.Count ? Data[(int)index] : null;
    }

    public override bool HasRhsComponentSlow(DemangleOutput ob) => Current(ob)?.HasRhsComponent(ob) == true;
    public override bool HasArraySlow(DemangleOutput ob) => Current(ob)?.HasArray(ob) == true;
    public override bool HasFunctionSlow(DemangleOutput ob) => Current(ob)?.HasFunction(ob) == true;
    protected override Node GetSyntaxNodeCore(DemangleOutput ob) => Current(ob)?.GetSyntaxNode(ob) ?? this;

    public override void PrintLeft(DemangleOutput ob)
    {
        if (Current(ob) is { } node)
            ob.PrintLeft(node);
    }

    public override void PrintRight(DemangleOutput ob)
    {
        if (Current(ob) is { } node)
            ob.PrintRight(node);
    }
}

internal sealed class TemplateArgumentPack(NodeArray elements) : Node(NodeKind.TemplateArgumentPack)
{
    public NodeArray Elements => elements;
    public override void PrintLeft(DemangleOutput ob) => elements.PrintWithComma(ob);
}

internal sealed class ParameterPackExpansion(Node child) : Node(NodeKind.ParameterPackExpansion)
{
    public Node Child => child;

    public override void PrintLeft(DemangleOutput ob)
    {
        uint savedIndex = ob.CurrentPackIndex, savedMax = ob.CurrentPackMax;
        ob.CurrentPackIndex = uint.MaxValue;
        ob.CurrentPackMax = uint.MaxValue;
        try
        {
            int streamPos = ob.Position;
            child.Print(ob);
            if (ob.CurrentPackMax == uint.MaxValue)
            {
                ob.Append("...");
                return;
            }

            if (ob.CurrentPackMax == 0)
            {
                ob.Position = streamPos;
                return;
            }

            for (uint i = 1, e = ob.CurrentPackMax; i < e; ++i)
            {
                ob.Append(", ");
                ob.CurrentPackIndex = i;
                child.Print(ob);
            }
        }
        finally
        {
            ob.CurrentPackIndex = savedIndex;
            ob.CurrentPackMax = savedMax;
        }
    }
}

internal sealed class PackIndexing(Node pattern, Node index) : Node(NodeKind.PackIndexing)
{
    public override void PrintLeft(DemangleOutput ob)
    {
        ob.PrintOpen('(');
        new ParameterPackExpansion(pattern).PrintLeft(ob);
        ob.PrintClose(')');
        ob.PrintOpen('[');
        ob.PrintLeft(index);
        ob.PrintClose(']');
    }
}

internal sealed class TemplateArgs(NodeArray @params, Node? requires) : Node(NodeKind.TemplateArgs)
{
    public NodeArray Params => @params;
    public Node? Requires => requires;

    public override void PrintLeft(DemangleOutput ob)
    {
        bool saved = ob.InsideTemplate;
        ob.InsideTemplate = true;
        ob.Append('<');
        @params.PrintWithComma(ob);
        ob.Append('>');
        ob.InsideTemplate = saved;
    }
}

internal sealed class ForwardTemplateReference(int index) : Node(NodeKind.ForwardTemplateReference, Cache.Unknown, Cache.Unknown, Cache.Unknown)
{
    public readonly int Index = index;
    public Node? Ref;
    private bool _printing;

    private T Guard<T>(T whenPrinting, Func<T> body)
    {
        if (_printing)
            return whenPrinting;
        _printing = true;
        try
        {
            return body();
        }
        finally
        {
            _printing = false;
        }
    }

    public override bool HasRhsComponentSlow(DemangleOutput ob) => Guard(false, () => Ref!.HasRhsComponent(ob));
    public override bool HasArraySlow(DemangleOutput ob) => Guard(false, () => Ref!.HasArray(ob));
    public override bool HasFunctionSlow(DemangleOutput ob) => Guard(false, () => Ref!.HasFunction(ob));
    protected override Node GetSyntaxNodeCore(DemangleOutput ob) => Guard<Node>(this, () => Ref!.GetSyntaxNode(ob));

    public override void PrintLeft(DemangleOutput ob) => Guard(0, () =>
    {
        ob.PrintLeft(Ref!);
        return 0;
    });

    public override void PrintRight(DemangleOutput ob) => Guard(0, () =>
    {
        ob.PrintRight(Ref!);
        return 0;
    });
}

internal sealed class NameWithTemplateArgs(Node name, Node templateArgs) : Node(NodeKind.NameWithTemplateArgs)
{
    public Node Name => name;
    public Node TemplateArgs => templateArgs;
    protected override string GetBaseNameCore() => name.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        name.Print(ob);
        templateArgs.Print(ob);
    }
}

internal sealed class GlobalQualifiedName(Node child) : Node(NodeKind.GlobalQualifiedName)
{
    protected override string GetBaseNameCore() => child.GetBaseName();

    public override void PrintLeft(DemangleOutput ob)
    {
        ob.Append("::");
        child.Print(ob);
    }
}

internal static partial class DemangleNodeHelpers
{
    public static void PrintCvRef(DemangleOutput ob, Qualifiers cvQuals, FunctionRefQual refQual)
    {
        if ((cvQuals & Qualifiers.Const) != 0)
            ob.Append(" const");
        if ((cvQuals & Qualifiers.Volatile) != 0)
            ob.Append(" volatile");
        if ((cvQuals & Qualifiers.Restrict) != 0)
            ob.Append(" restrict");
        if (refQual == FunctionRefQual.LValue)
            ob.Append(" &");
        else if (refQual == FunctionRefQual.RValue)
            ob.Append(" &&");
    }
}
