// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>Function type: maps parameter types to result types (§2.3.3)</summary>
public sealed record FuncType(ValType[] Parameters, ValType[] Results)
{
    /// <summary>True if any parameter is a reference type (not I32/I64/F32/F64).</summary>
    public bool HasRefParams { get; } = HasRef(Parameters);

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
