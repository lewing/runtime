// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Function type: maps parameter types to result types (§2.3.3)</summary>
public sealed record FuncType(ValType[] Parameters, ValType[] Results)
{
    internal string LinkKey { get; init; } = "";

    /// <summary>
    /// Precomputed correct null tags for results that are defined heap types, or <c>null</c> if no
    /// result requires discrimination beyond the compact <see cref="ValType"/>.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ValueType.ToStorageValType"/> collapses every <c>DefinedHeapType</c> to
    /// <see cref="ValType.FuncRef"/> — it holds only a type index, not a type table.  A function
    /// declared <c>(result (ref null $ct))</c> and one declared <c>(result (ref null $ft))</c> both
    /// produce the same compact result type <c>FuncRef</c>, so the materialiser cannot distinguish
    /// them without an out-of-band signal.</para>
    ///
    /// <para>When non-<c>null</c>, <c>ResultNullTags[i]</c> is the <see cref="ValType"/> that a
    /// <em>null</em> value at result slot <c>i</c> should carry: <see cref="ValType.ContRef"/> for
    /// defined continuation types, <see cref="ValType.ExternRef"/> for defined struct/array types,
    /// and <see cref="ValType.FuncRef"/> for defined function types — matching the tag a live value
    /// of the same declared type already gets from payload-based classification in
    /// <c>MaterialiseFuncRefSlot</c>.</para>
    ///
    /// <para>Set by <c>ModuleReader.ResolveResultNullTags</c> after all types in the module's type
    /// section have been parsed, so the composite type of each referenced <c>DefinedHeapType</c> is
    /// known.  <c>null</c> for unbound host functions and types whose results do not include any
    /// defined heap type; an imported host function is rebound to the module's declared
    /// <see cref="FuncType"/> at instantiation.</para>
    /// </remarks>
    internal ValType[]? ResultNullTags { get; init; }

    /// <summary>True if any parameter is a reference type (not I32/I64/F32/F64).</summary>
    public bool HasRefParams => HasRef(Parameters);

    private static bool HasRef(ValType[] types)
    {
        foreach (var t in types)
        {
            if (t is not (ValType.I32 or ValType.I64 or ValType.F32 or ValType.F64 or ValType.V128))
                return true;
        }
        return false;
    }
}
