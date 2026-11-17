// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Exact semantic value type used by validation.</summary>
public abstract record ValueType
{
    public sealed record Number(NumberType Type) : ValueType;
    public sealed record Vector(VectorType Type) : ValueType;
    public sealed record Reference(Nullability Nullability, HeapType HeapType) : ValueType;

    /// <summary>
    /// The compact execution-representation tag for this exact type.
    /// </summary>
    /// <remarks>
    /// <para>The reference arms delegate to <see cref="AbstractHeapTypeCodes.StorageTagFor"/>,
    /// the repository's single heap-type-to-storage-tag authority, rather than restating it — the
    /// previous form ended in a bare <c>Reference =&gt;</c> arm that answered <c>ExternRef</c> for
    /// everything it had not named, which is a negated exclusion list: a new
    /// <see cref="HeapTypeKind"/> would have silently joined <c>extern</c> because the affirmative
    /// branch is the one nobody wrote (#406).</para>
    ///
    /// <para><c>DefinedHeapType</c> keeps its own arm and stays <see cref="ValType.FuncRef"/>. That
    /// is not a classification but an <em>erasure</em>: this method holds a type index and no type
    /// table, so it cannot tell a defined function type from a defined struct, array or
    /// continuation type. Any site that needs the distinction must resolve it where the table is in
    /// reach — see <see cref="AbstractHeapTypeCodes.StorageTagForDefinedType"/>.</para>
    /// </remarks>
    public ValType ToStorageValType() => this switch
    {
        Number { Type: NumberType.I32 } => ValType.I32,
        Number { Type: NumberType.I64 } => ValType.I64,
        Number { Type: NumberType.F32 } => ValType.F32,
        Number { Type: NumberType.F64 } => ValType.F64,
        Vector => ValType.V128,
        Reference { HeapType: AbstractHeapType abstractHeap } => AbstractHeapTypeCodes.StorageTagFor(abstractHeap.Kind),
        Reference { HeapType: DefinedHeapType } => ValType.FuncRef,
        Reference reference => throw new InvalidOperationException(
            $"Heap type {reference.HeapType.GetType().Name} has no execution storage tag."),
        _ => throw new InvalidOperationException($"Unknown value type {GetType().Name}"),
    };

    /// <summary>
    /// The reverse of <see cref="ToStorageValType"/>: the exact type a compact
    /// <see cref="ValType"/> denotes.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This direction is <b>lossless only because the input is already lossy</b>. The forward map
    /// erases <c>(ref null $t)</c>, <c>(ref func)</c> and every GC abstract type onto four reference
    /// codes, so the exact type recovered here is the widest one the compact code can mean — a
    /// nullable reference to the corresponding abstract heap type. Use it only where the compact
    /// type is the whole truth (a module built programmatically from <see cref="FuncType"/>s, which
    /// has no exact types to lose), never to "recover" a type that was erased.
    /// </para>
    /// <para>
    /// Kept next to the forward map so the pair is read together; the reference arm resolves through
    /// <see cref="AbstractHeapTypeCodes"/> rather than restating the code table a fifth time.
    /// </para>
    /// </remarks>
    internal static ValueType FromStorageValType(ValType type) => type switch
    {
        ValType.I32 => new Number(NumberType.I32),
        ValType.I64 => new Number(NumberType.I64),
        ValType.F32 => new Number(NumberType.F32),
        ValType.F64 => new Number(NumberType.F64),
        ValType.V128 => new Vector(VectorType.V128),
        _ => AbstractHeapTypeCodes.TryFromCode((byte)type, out var kind)
            ? new Reference(Nullability.Nullable, new AbstractHeapType(kind))
            : throw new InvalidOperationException($"Unknown storage value type 0x{(byte)type:X2}"),
    };

    public string Key => this switch
    {
        Number n => n.Type.ToString().ToLowerInvariant(),
        Vector => "v128",
        Reference r => $"{(r.Nullability == Nullability.Nullable ? "ref.null" : "ref")}:{r.HeapType.Key}",        _ => ToString() ?? "",
    };
}

public enum NumberType
{
    I32,
    I64,
    F32,
    F64,
}

public enum VectorType
{
    V128,
}

public enum Nullability
{
    NonNullable,
    Nullable,
}

public abstract record HeapType
{
    public abstract string Key { get; }
}

public sealed record AbstractHeapType(HeapTypeKind Kind) : HeapType
{
    public override string Key => Kind.ToString().ToLowerInvariant();
}

public sealed record DefinedHeapType(int TypeIndex) : HeapType
{
    public override string Key => $"type:{TypeIndex}";
}

public sealed record RecursionVariableHeapType(int RecGroupIndex, int MemberIndex) : HeapType
{
    public override string Key => $"rec:{RecGroupIndex}:{MemberIndex}";
}

public enum HeapTypeKind
{
    Any,
    Eq,
    I31,
    Struct,
    Array,
    None,
    Func,
    NoFunc,
    Extern,
    NoExtern,
    Exn,
    NoExn,
    Cont,
    NoCont,
}

public sealed record RecGroupIdentity(int Index, int StartTypeIndex, int Count);

public sealed record TypeDefinition(
    int Index,
    bool IsFinal,
    IReadOnlyList<int> SuperTypeIndices,
    CompositeType CompositeType,
    RecGroupIdentity RecGroup);

public abstract record CompositeType
{
    public sealed record Function(IReadOnlyList<ValueType> Parameters, IReadOnlyList<ValueType> Results) : CompositeType;
    public sealed record Struct(IReadOnlyList<FieldType> Fields) : CompositeType;
    public sealed record Array(FieldType Field) : CompositeType;
    public sealed record Continuation(int FunctionTypeIndex) : CompositeType;
}

public sealed record FieldType(StorageType StorageType, Mutability Mutability);

public abstract record StorageType
{
    public sealed record Value(ValueType Type) : StorageType;
    public sealed record Packed(PackedStorageType Type) : StorageType;
}

public enum PackedStorageType
{
    I8,
    I16,
}
