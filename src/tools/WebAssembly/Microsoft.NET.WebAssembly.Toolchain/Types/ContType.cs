// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Continuation type for typed continuations (stack-switching proposal).
/// Wraps the resolved <see cref="FuncType"/> directly — corresponds to
/// <c>cont $ft</c> in the spec, where the referenced function type determines
/// the parameters expected when resumed and the results produced.
/// </summary>
public sealed record ContType(FuncType FuncType);
