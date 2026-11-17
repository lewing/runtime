// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using Microsoft.NET.WebAssembly.Types;

/// <summary>A global variable definition (§2.5.7)</summary>
public sealed class WasmGlobal
{
    public required GlobalType Type { get; init; }
    public required byte[] InitExpr { get; init; }
}
