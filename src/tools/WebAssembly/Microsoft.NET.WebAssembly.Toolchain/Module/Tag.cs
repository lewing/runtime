// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>A tag declaration (§2.5.13) — used for exception handling.</summary>
/// <param name="TypeIndex">Index into the module's type section (must be a function type).</param>
public sealed record Tag(int TypeIndex);
