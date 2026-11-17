// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Placeholder for the decoded-body cache that <see cref="Microsoft.NET.WebAssembly.Binary.ModuleReader"/> populates.
/// The toolchain has no interpreter, so the cached value is never consumed.
/// </summary>
public sealed partial class WasmFunction
{
    internal object? DecodedBody { get; set; }
}
