// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Renders an exact (<see cref="ValueType"/>) type in WAT-like text for diagnostics.
///
/// <para>The compact <see cref="ValType"/> enum cannot express nullability or defined-type
/// identity, so a message built from it can report <c>expected FuncRef, got FuncRef</c> for a
/// genuine mismatch — a diagnostic that names no difference is worse than none, because it
/// sends the reader looking in the wrong place. This formatter prints the state the compact
/// form erases: the <c>null</c> flag, the heap type, and — when the defining module's type
/// section is supplied — the structure behind a defined type index, since two modules'
/// index 0 are unrelated types that would otherwise render identically.</para>
/// </summary>
internal static class ExactTypeFormatter
{
    /// <summary>Default expansion depth for defined heap types.</summary>
    private const int DefaultDepth = 2;

    /// <summary>
    /// Expansion depths tried in order when two renderings collide. A difference that exists
    /// below the printed depth becomes visible simply by printing further, so the cheap default
    /// is tried first and the deep pass only pays for itself on a collision.
    /// </summary>
    private static readonly int[] ExpansionDepths = [DefaultDepth, 6, 32];

    /// <summary>
    /// Maximum number of defined-type expansions in one rendering. Depth alone does not bound
    /// cost: a type whose members each name several other defined types fans out exponentially,
    /// so a deep pass without a node budget could produce a megabyte-long exception message.
    /// The budget is per rendering, so both sides of a comparison get the same allowance.
    /// </summary>
    private const int NodeBudget = 256;

    public static string Describe(ValueType type, IReadOnlyList<TypeDefinition>? types = null, int depth = DefaultDepth)
        => Describe(type, types, depth, new Expansion(NodeBudget));

    /// <summary>
    /// Renders two types that are known to be <em>different</em>, guaranteeing the two strings
    /// differ. A caller reporting a mismatch must never emit "expected X, got X", which says the
    /// check disagrees with itself and sends the reader looking in the wrong place.
    ///
    /// <para>The pair is re-rendered at increasing depth until it separates. When it still does
    /// not — the difference may live in recursion-group membership, which no amount of depth
    /// prints, or below the expansion bound — each side is annotated with which side it is and
    /// with its recursion-group extent. Both are facts read from the arguments. This method does
    /// <em>not</em> attribute the collision to a cause, because it cannot tell which of those two
    /// it is; naming one would be a guess printed as a finding.</para>
    /// </summary>
    public static (string Declared, string Supplied) DescribeDistinct(
        ValueType declared,
        IReadOnlyList<TypeDefinition>? declaredTypes,
        ValueType supplied,
        IReadOnlyList<TypeDefinition>? suppliedTypes)
    {
        string d = "", s = "";
        foreach (int depth in ExpansionDepths)
        {
            d = Describe(declared, declaredTypes, depth);
            s = Describe(supplied, suppliedTypes, depth);
            if (!string.Equals(d, s, StringComparison.Ordinal))
                return (d, s);
        }

        return (d + Unshown("declared", declared, declaredTypes),
                s + Unshown("supplied", supplied, suppliedTypes));
    }

    /// <summary>
    /// The clause appended to a rendering that collided with the other side at every depth. The
    /// side word is what makes the two strings differ; it is accurate because the caller supplies
    /// the declared type first and the supplied type second.
    /// </summary>
    private static string Unshown(string side, ValueType type, IReadOnlyList<TypeDefinition>? types)
        => $" [{side} type{RecGroupFact(type, types)}; " +
           "not equivalent, but the expansion shown is identical on both sides]";

    /// <summary>
    /// The recursion group of the type's own defined head, when it has one and the defining type
    /// section is known. Recursion-group extent participates in type identity but never appears
    /// in the structural rendering, so it is genuinely unshown state — and it is read from the
    /// data rather than inferred.
    /// </summary>
    private static string RecGroupFact(ValueType type, IReadOnlyList<TypeDefinition>? types)
    {
        if (type is not ValueType.Reference { HeapType: DefinedHeapType d } ||
            types is null || (uint)d.TypeIndex >= (uint)types.Count)
        {
            return "";
        }

        var group = types[d.TypeIndex].RecGroup;
        return $", recursion group of {group.Count} starting at ${group.StartTypeIndex}";
    }

    private static string Describe(
        ValueType type,
        IReadOnlyList<TypeDefinition>? types,
        int depth,
        Expansion expanding)
        => type switch
        {
            ValueType.Number n => n.Type.ToString().ToLowerInvariant(),
            ValueType.Vector => "v128",
            ValueType.Reference r =>
                $"(ref{(r.Nullability == Nullability.Nullable ? " null" : "")} " +
                $"{DescribeHeap(r.HeapType, types, depth, expanding)})",
            _ => type.ToString() ?? "?",
        };

    private static string DescribeHeap(
        HeapType heap,
        IReadOnlyList<TypeDefinition>? types,
        int depth,
        Expansion expanding)
    {
        switch (heap)
        {
            case AbstractHeapType a:
                return a.Kind.ToString().ToLowerInvariant();

            case RecursionVariableHeapType rec:
                return $"rec{rec.RecGroupIndex}.{rec.MemberIndex}";

            case DefinedHeapType d:
            {
                string label = $"${d.TypeIndex}";
                if (types is null || depth <= 0 ||
                    (uint)d.TypeIndex >= (uint)types.Count ||
                    !expanding.TryEnter(d.TypeIndex))
                {
                    return label;
                }

                try
                {
                    return $"{label} {DescribeDefinition(types[d.TypeIndex], types, depth - 1, expanding)}";
                }
                finally
                {
                    expanding.Leave(d.TypeIndex);
                }
            }

            default:
                return heap.Key;
        }
    }

    private static string DescribeDefinition(
        TypeDefinition definition,
        IReadOnlyList<TypeDefinition> types,
        int depth,
        Expansion expanding)
    {
        string composite = DescribeComposite(definition.CompositeType, types, depth, expanding);

        // Finality and declared supertypes are part of a defined type's identity, so two
        // otherwise identical composites must not render the same when they differ here.
        if (definition.IsFinal && definition.SuperTypeIndices.Count == 0)
            return composite;

        string supers = definition.SuperTypeIndices.Count == 0
            ? ""
            : " " + string.Join(" ", definition.SuperTypeIndices.Select(i => $"${i}"));
        return $"(sub{(definition.IsFinal ? " final" : "")}{supers} {composite})";
    }

    private static string DescribeComposite(
        CompositeType composite,
        IReadOnlyList<TypeDefinition> types,
        int depth,
        Expansion expanding)
        => composite switch
        {
            CompositeType.Function f =>
                $"(func{Params(f.Parameters, types, depth, expanding)}{Results(f.Results, types, depth, expanding)})",
            CompositeType.Struct s =>
                $"(struct{string.Concat(s.Fields.Select(field => " " + DescribeField(field, types, depth, expanding)))})",
            CompositeType.Array a =>
                $"(array {DescribeField(a.Field, types, depth, expanding)})",
            CompositeType.Continuation c => $"(cont ${c.FunctionTypeIndex})",
            _ => composite.ToString() ?? "?",
        };

    private static string Params(
        IReadOnlyList<ValueType> values,
        IReadOnlyList<TypeDefinition> types,
        int depth,
        Expansion expanding)
        => values.Count == 0
            ? ""
            : $" (param{string.Concat(values.Select(v => " " + Describe(v, types, depth, expanding)))})";

    private static string Results(
        IReadOnlyList<ValueType> values,
        IReadOnlyList<TypeDefinition> types,
        int depth,
        Expansion expanding)
        => values.Count == 0
            ? ""
            : $" (result{string.Concat(values.Select(v => " " + Describe(v, types, depth, expanding)))})";

    private static string DescribeField(
        FieldType field,
        IReadOnlyList<TypeDefinition> types,
        int depth,
        Expansion expanding)
    {
        string storage = field.StorageType switch
        {
            StorageType.Value v => Describe(v.Type, types, depth, expanding),
            StorageType.Packed p => p.Type.ToString().ToLowerInvariant(),
            _ => "?",
        };
        return field.Mutability == Mutability.Var ? $"(mut {storage})" : storage;
    }

    /// <summary>
    /// State carried through one rendering: the defined types currently on the expansion path,
    /// which breaks cycles, and the remaining node budget, which bounds fan-out. The two are kept
    /// together because either alone is insufficient — the path set stops a type recursing into
    /// itself but not a wide acyclic graph re-expanded down every path.
    /// </summary>
    private sealed class Expansion(int budget)
    {
        private readonly HashSet<int> _onPath = [];
        private int _remaining = budget;

        public bool TryEnter(int typeIndex)
        {
            if (_remaining <= 0 || !_onPath.Add(typeIndex))
                return false;

            _remaining--;
            return true;
        }

        public void Leave(int typeIndex) => _onPath.Remove(typeIndex);
    }
}
