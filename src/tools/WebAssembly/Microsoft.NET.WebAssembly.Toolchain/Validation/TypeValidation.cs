// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using ExactValueType = Microsoft.NET.WebAssembly.Types.ValueType;

/// <summary>Exact type-section validation for the GC snapshot implemented by wasm-tools 1.253.0.</summary>
public static class TypeDeclarationValidator
{
    public const string GcSnapshot = "wasm-tools 1.253.0 / upstream GC testsuite snapshot";

    public static WasmValidationResult Validate(WasmModule module)
        => Validate(module.TypeDefinitions);

    public static WasmValidationResult Validate(IReadOnlyList<TypeDefinition> types)
    {
        var diagnostics = new List<WasmValidationDiagnostic>();
        var subtype = new SubtypeRelation(types);

        for (int i = 0; i < types.Count; i++)
        {
            var definition = types[i];
            if (definition.Index != i)
            {
                Add(diagnostics, $"Type definition at slot {i} has inconsistent exact index {definition.Index}");
                continue;
            }

            foreach (int superIndex in definition.SuperTypeIndices)
            {
                if (superIndex < 0 || superIndex >= i)
                {
                    Add(diagnostics, $"Type {i} declares supertype {superIndex}, which is not visible at that point");
                    continue;
                }

                var supertype = types[superIndex];
                if (supertype.IsFinal)
                {
                    Add(diagnostics, $"Type {i} extends final supertype {superIndex}");
                    continue;
                }

                if (!CompositeMatches(definition.CompositeType, supertype.CompositeType, subtype))
                    Add(diagnostics, $"Type {i} is not a valid subtype of declared supertype {superIndex}");
            }
        }

        return diagnostics.Count == 0
            ? WasmValidationResult.Valid
            : new WasmValidationResult(diagnostics);
    }

    private static bool CompositeMatches(
        CompositeType subtype,
        CompositeType supertype,
        SubtypeRelation relation)
    {
        return (subtype, supertype) switch
        {
            (CompositeType.Function sub, CompositeType.Function sup) => FunctionMatches(sub, sup, relation),
            (CompositeType.Struct sub, CompositeType.Struct sup) => StructMatches(sub, sup, relation),
            (CompositeType.Array sub, CompositeType.Array sup) => FieldMatches(sub.Field, sup.Field, relation),
            (CompositeType.Continuation sub, CompositeType.Continuation sup) =>
                relation.AreEquivalentTypeIndices(sub.FunctionTypeIndex, sup.FunctionTypeIndex),
            _ => false,
        };
    }

    private static bool FunctionMatches(
        CompositeType.Function subtype,
        CompositeType.Function supertype,
        SubtypeRelation relation)
    {
        if (subtype.Parameters.Count != supertype.Parameters.Count ||
            subtype.Results.Count != supertype.Results.Count)
            return false;

        for (int i = 0; i < subtype.Parameters.Count; i++)
        {
            // Function parameters are contravariant.
            if (!relation.IsSubtype(supertype.Parameters[i], subtype.Parameters[i]))
                return false;
        }

        for (int i = 0; i < subtype.Results.Count; i++)
        {
            if (!relation.IsSubtype(subtype.Results[i], supertype.Results[i]))
                return false;
        }

        return true;
    }

    private static bool StructMatches(
        CompositeType.Struct subtype,
        CompositeType.Struct supertype,
        SubtypeRelation relation)
    {
        if (subtype.Fields.Count < supertype.Fields.Count)
            return false;

        for (int i = 0; i < supertype.Fields.Count; i++)
        {
            if (!FieldMatches(subtype.Fields[i], supertype.Fields[i], relation))
                return false;
        }

        return true;
    }

    private static bool FieldMatches(FieldType subtype, FieldType supertype, SubtypeRelation relation)
    {
        if (subtype.Mutability != supertype.Mutability)
            return false;

        return supertype.Mutability == Mutability.Var
            ? StorageEquals(subtype.StorageType, supertype.StorageType, relation)
            : StorageSubtype(subtype.StorageType, supertype.StorageType, relation);
    }

    private static bool StorageSubtype(StorageType subtype, StorageType supertype, SubtypeRelation relation)
    {
        return (subtype, supertype) switch
        {
            (StorageType.Value sub, StorageType.Value sup) => relation.IsSubtype(sub.Type, sup.Type),
            (StorageType.Packed sub, StorageType.Packed sup) => sub.Type == sup.Type,
            _ => false,
        };
    }

    private static bool StorageEquals(StorageType left, StorageType right, SubtypeRelation relation)
    {
        return (left, right) switch
        {
            (StorageType.Value l, StorageType.Value r) => relation.AreEquivalent(l.Type, r.Type),
            (StorageType.Packed l, StorageType.Packed r) => l.Type == r.Type,
            _ => false,
        };
    }

    private static void Add(List<WasmValidationDiagnostic> diagnostics, string message)
        => diagnostics.Add(new WasmValidationDiagnostic(WasmValidationCode.InvalidSubtypeDeclaration, message));
}

public sealed class SubtypeRelation
{
    private readonly IReadOnlyList<TypeDefinition> _types;
    private readonly Dictionary<(ExactValueType Subtype, ExactValueType Supertype), bool> _valueMemo = [];
    private readonly Dictionary<(HeapType Subtype, HeapType Supertype), bool> _heapMemo = [];
    private readonly Dictionary<(int Left, int Right), bool> _equivalenceMemo = [];

    public SubtypeRelation(IReadOnlyList<TypeDefinition> types)
    {
        _types = types;
    }

    public bool AreEquivalent(ExactValueType left, ExactValueType right)
    {
        if (left == right)
            return true;

        return (left, right) switch
        {
            (ExactValueType.Reference l, ExactValueType.Reference r) =>
                l.Nullability == r.Nullability &&
                AreEquivalent(l.HeapType, r.HeapType),
            _ => false,
        };
    }

    public bool AreEquivalentTypeIndices(int leftIndex, int rightIndex)
        => AreEquivalentTypes(leftIndex, rightIndex, []);

    internal static bool AreEquivalentTypeIndices(
        IReadOnlyList<TypeDefinition> leftTypes,
        int leftIndex,
        IReadOnlyList<TypeDefinition> rightTypes,
        int rightIndex)
    {
        if ((uint)leftIndex >= (uint)leftTypes.Count ||
            (uint)rightIndex >= (uint)rightTypes.Count)
        {
            return false;
        }

        if (ReferenceEquals(leftTypes, rightTypes))
            return new SubtypeRelation(leftTypes).AreEquivalentTypeIndices(leftIndex, rightIndex);

        int rightOffset = leftTypes.Count;
        var combined = new List<TypeDefinition>(leftTypes);
        foreach (var definition in rightTypes)
            combined.Add(Rebase(definition, rightOffset));

        return new SubtypeRelation(combined).AreEquivalentTypeIndices(
            leftIndex, rightIndex + rightOffset);
    }

    private static TypeDefinition Rebase(TypeDefinition definition, int offset)
        => new(
            definition.Index + offset,
            definition.IsFinal,
            [.. definition.SuperTypeIndices.Select(index => index + offset)],
            Rebase(definition.CompositeType, offset),
            definition.RecGroup with { StartTypeIndex = definition.RecGroup.StartTypeIndex + offset });

    private static CompositeType Rebase(CompositeType composite, int offset)
        => composite switch
        {
            CompositeType.Function function => new CompositeType.Function(
                [.. function.Parameters.Select(type => Rebase(type, offset))],
                [.. function.Results.Select(type => Rebase(type, offset))]),
            CompositeType.Struct structure => new CompositeType.Struct(
                [.. structure.Fields.Select(field => Rebase(field, offset))]),
            CompositeType.Array array => new CompositeType.Array(Rebase(array.Field, offset)),
            CompositeType.Continuation continuation =>
                new CompositeType.Continuation(continuation.FunctionTypeIndex + offset),
            _ => composite,
        };

    private static FieldType Rebase(FieldType field, int offset)
        => field with
        {
            StorageType = field.StorageType switch
            {
                StorageType.Value value => new StorageType.Value(Rebase(value.Type, offset)),
                var packed => packed,
            },
        };

    private static ExactValueType Rebase(ExactValueType type, int offset)
        => type switch
        {
            ExactValueType.Reference { HeapType: DefinedHeapType defined } reference =>
                reference with { HeapType = new DefinedHeapType(defined.TypeIndex + offset) },
            _ => type,
        };

    private bool AreEquivalent(HeapType left, HeapType right)
    {
        if (left == right)
            return true;

        return (left, right) switch
        {
            (DefinedHeapType l, DefinedHeapType r) =>
                AreEquivalentTypeIndices(l.TypeIndex, r.TypeIndex),
            _ => false,
        };
    }

    public bool IsSubtype(ExactValueType subtype, ExactValueType supertype)
    {
        if (subtype == supertype)
            return true;
        if (_valueMemo.TryGetValue((subtype, supertype), out bool memoized))
            return memoized;

        bool result = ComputeValueSubtype(subtype, supertype);
        _valueMemo[(subtype, supertype)] = result;
        return result;
    }

    public bool IsSubtype(HeapType subtype, HeapType supertype)
    {
        if (subtype == supertype)
            return true;
        if (_heapMemo.TryGetValue((subtype, supertype), out bool memoized))
            return memoized;

        bool result = ComputeHeapSubtype(subtype, supertype);
        _heapMemo[(subtype, supertype)] = result;
        return result;
    }

    private bool ComputeValueSubtype(ExactValueType subtype, ExactValueType supertype)
    {
        return (subtype, supertype) switch
        {
            (ExactValueType.Number sub, ExactValueType.Number sup) => sub.Type == sup.Type,
            (ExactValueType.Vector sub, ExactValueType.Vector sup) => sub.Type == sup.Type,
            (ExactValueType.Reference sub, ExactValueType.Reference sup)
                => NullabilityMatches(sub.Nullability, sup.Nullability) &&
                   IsSubtype(sub.HeapType, sup.HeapType),
            _ => false,
        };
    }

    private static bool NullabilityMatches(Nullability subtype, Nullability supertype)
        => subtype == supertype || (subtype == Nullability.NonNullable && supertype == Nullability.Nullable);

    private bool ComputeHeapSubtype(HeapType subtype, HeapType supertype)
    {
        if (subtype is RecursionVariableHeapType || supertype is RecursionVariableHeapType)
            return subtype == supertype;

        if (subtype is AbstractHeapType subAbs && supertype is AbstractHeapType supAbs)
            return AbstractSubtype(subAbs.Kind, supAbs.Kind);

        if (subtype is DefinedHeapType subDef && supertype is DefinedHeapType supDef)
            return IsDeclaredSubtype(subDef.TypeIndex, supDef.TypeIndex) ||
                   AreEquivalentTypes(subDef.TypeIndex, supDef.TypeIndex, []);

        if (subtype is DefinedHeapType defined && supertype is AbstractHeapType abstractSuper)
            return DefinedBelowAbstract(defined.TypeIndex, abstractSuper.Kind);

        if (subtype is AbstractHeapType nullBottom && supertype is DefinedHeapType definedSuper)
            return NullBottomBelowDefined(nullBottom.Kind, definedSuper.TypeIndex);

        return false;
    }

    private bool IsDeclaredSubtype(int subtypeIndex, int supertypeIndex)
    {
        if (subtypeIndex == supertypeIndex || AreEquivalentTypes(subtypeIndex, supertypeIndex, []))
            return true;
        if (!IsKnownType(subtypeIndex) || !IsKnownType(supertypeIndex))
            return false;

        foreach (int declaredSuper in _types[subtypeIndex].SuperTypeIndices)
        {
            if (declaredSuper == supertypeIndex || IsDeclaredSubtype(declaredSuper, supertypeIndex))
                return true;
        }

        return false;
    }

    private bool AreEquivalentTypes(int leftIndex, int rightIndex, HashSet<(int Left, int Right)> active)
    {
        if (leftIndex == rightIndex)
            return true;
        if (!IsKnownType(leftIndex) || !IsKnownType(rightIndex))
            return false;

        var leftGroup = _types[leftIndex].RecGroup;
        var rightGroup = _types[rightIndex].RecGroup;
        int leftProjection = leftIndex - leftGroup.StartTypeIndex;
        int rightProjection = rightIndex - rightGroup.StartTypeIndex;
        if (leftProjection != rightProjection || leftGroup.Count != rightGroup.Count)
            return false;

        var key = leftIndex < rightIndex ? (leftIndex, rightIndex) : (rightIndex, leftIndex);
        if (_equivalenceMemo.TryGetValue(key, out bool memoized))
            return memoized;
        if (!active.Add(key))
            return true;

        bool result = true;
        for (int i = 0; i < leftGroup.Count && result; i++)
        {
            var left = _types[leftGroup.StartTypeIndex + i];
            var right = _types[rightGroup.StartTypeIndex + i];
            result =
                left.IsFinal == right.IsFinal &&
                EquivalentTypeIndexList(left.SuperTypeIndices, right.SuperTypeIndices, leftGroup, rightGroup, active) &&
                EquivalentComposite(left.CompositeType, right.CompositeType, leftGroup, rightGroup, active);
        }

        active.Remove(key);
        _equivalenceMemo[key] = result;
        return result;
    }

    private bool EquivalentTypeIndexList(
        IReadOnlyList<int> left,
        IReadOnlyList<int> right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!EquivalentTypeIndex(left[i], right[i], leftGroup, rightGroup, active))
                return false;
        }

        return true;
    }

    private bool EquivalentTypeIndex(
        int left,
        int right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        bool leftIsLocal = IsInGroup(left, leftGroup);
        bool rightIsLocal = IsInGroup(right, rightGroup);
        if (leftIsLocal || rightIsLocal)
        {
            return leftIsLocal &&
                   rightIsLocal &&
                   left - leftGroup.StartTypeIndex == right - rightGroup.StartTypeIndex;
        }

        return AreEquivalentTypes(left, right, active);
    }

    private static bool IsInGroup(int typeIndex, RecGroupIdentity group)
        => typeIndex >= group.StartTypeIndex &&
           typeIndex < group.StartTypeIndex + group.Count;

    private bool EquivalentComposite(
        CompositeType left,
        CompositeType right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        return (left, right) switch
        {
            (CompositeType.Function l, CompositeType.Function r) =>
                EquivalentValueList(l.Parameters, r.Parameters, leftGroup, rightGroup, active) &&
                EquivalentValueList(l.Results, r.Results, leftGroup, rightGroup, active),
            (CompositeType.Struct l, CompositeType.Struct r) =>
                EquivalentFieldList(l.Fields, r.Fields, leftGroup, rightGroup, active),
            (CompositeType.Array l, CompositeType.Array r) =>
                EquivalentField(l.Field, r.Field, leftGroup, rightGroup, active),
            (CompositeType.Continuation l, CompositeType.Continuation r) =>
                EquivalentTypeIndex(l.FunctionTypeIndex, r.FunctionTypeIndex, leftGroup, rightGroup, active),
            _ => false,
        };
    }

    private bool EquivalentValueList(
        IReadOnlyList<ExactValueType> left,
        IReadOnlyList<ExactValueType> right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!EquivalentValue(left[i], right[i], leftGroup, rightGroup, active))
                return false;
        }

        return true;
    }

    private bool EquivalentFieldList(
        IReadOnlyList<FieldType> left,
        IReadOnlyList<FieldType> right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        if (left.Count != right.Count)
            return false;

        for (int i = 0; i < left.Count; i++)
        {
            if (!EquivalentField(left[i], right[i], leftGroup, rightGroup, active))
                return false;
        }

        return true;
    }

    private bool EquivalentField(
        FieldType left,
        FieldType right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
        => left.Mutability == right.Mutability &&
           EquivalentStorage(left.StorageType, right.StorageType, leftGroup, rightGroup, active);

    private bool EquivalentStorage(
        StorageType left,
        StorageType right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        return (left, right) switch
        {
            (StorageType.Value l, StorageType.Value r) =>
                EquivalentValue(l.Type, r.Type, leftGroup, rightGroup, active),
            (StorageType.Packed l, StorageType.Packed r) => l.Type == r.Type,
            _ => false,
        };
    }

    private bool EquivalentValue(
        ExactValueType left,
        ExactValueType right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        return (left, right) switch
        {
            (ExactValueType.Number l, ExactValueType.Number r) => l.Type == r.Type,
            (ExactValueType.Vector l, ExactValueType.Vector r) => l.Type == r.Type,
            (ExactValueType.Reference l, ExactValueType.Reference r) =>
                l.Nullability == r.Nullability &&
                EquivalentHeap(l.HeapType, r.HeapType, leftGroup, rightGroup, active),
            _ => false,
        };
    }

    private bool EquivalentHeap(
        HeapType left,
        HeapType right,
        RecGroupIdentity leftGroup,
        RecGroupIdentity rightGroup,
        HashSet<(int Left, int Right)> active)
    {
        return (left, right) switch
        {
            (DefinedHeapType l, DefinedHeapType r) =>
                EquivalentTypeIndex(l.TypeIndex, r.TypeIndex, leftGroup, rightGroup, active),
            (AbstractHeapType l, AbstractHeapType r) => l.Kind == r.Kind,
            (RecursionVariableHeapType l, RecursionVariableHeapType r) => l == r,
            _ => false,
        };
    }

    private bool DefinedBelowAbstract(int typeIndex, HeapTypeKind abstractKind)
    {
        if (!IsKnownType(typeIndex))
            return false;

        if (CompositeBelowAbstract(_types[typeIndex].CompositeType, abstractKind))
            return true;

        foreach (int superIndex in _types[typeIndex].SuperTypeIndices)
        {
            if (DefinedBelowAbstract(superIndex, abstractKind))
                return true;
        }

        return false;
    }

    private static bool CompositeBelowAbstract(CompositeType composite, HeapTypeKind abstractKind)
    {
        return composite switch
        {
            CompositeType.Function => abstractKind == HeapTypeKind.Func,
            CompositeType.Struct => abstractKind is HeapTypeKind.Struct or HeapTypeKind.Eq or HeapTypeKind.Any,
            CompositeType.Array => abstractKind is HeapTypeKind.Array or HeapTypeKind.Eq or HeapTypeKind.Any,
            CompositeType.Continuation => abstractKind == HeapTypeKind.Cont,
            _ => false,
        };
    }

    private static bool AbstractSubtype(HeapTypeKind subtype, HeapTypeKind supertype)
    {
        if (subtype == supertype)
            return true;

        return subtype switch
        {
            HeapTypeKind.None => supertype is HeapTypeKind.Struct or HeapTypeKind.Array or HeapTypeKind.I31 or HeapTypeKind.Eq or HeapTypeKind.Any,
            HeapTypeKind.Struct or HeapTypeKind.Array or HeapTypeKind.I31
                => supertype is HeapTypeKind.Eq or HeapTypeKind.Any,
            HeapTypeKind.Eq => supertype == HeapTypeKind.Any,
            HeapTypeKind.NoFunc => supertype == HeapTypeKind.Func,
            HeapTypeKind.NoExtern => supertype == HeapTypeKind.Extern,
            HeapTypeKind.NoExn => supertype == HeapTypeKind.Exn,
            HeapTypeKind.NoCont => supertype == HeapTypeKind.Cont,
            _ => false,
        };
    }

    private bool IsKnownType(int index) => index >= 0 && index < _types.Count;

    private bool NullBottomBelowDefined(HeapTypeKind subtype, int supertypeIndex)
    {
        if (!IsKnownType(supertypeIndex))
            return false;

        return (_types[supertypeIndex].CompositeType, subtype) switch
        {
            (CompositeType.Function, HeapTypeKind.NoFunc) => true,
            (CompositeType.Struct, HeapTypeKind.None) => true,
            (CompositeType.Array, HeapTypeKind.None) => true,
            (CompositeType.Continuation, HeapTypeKind.NoCont) => true,
            _ => false,
        };
    }
}
