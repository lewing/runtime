// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

/// <summary>The outcome of a module validation pass — a collection of diagnostics.</summary>
public sealed class WasmValidationResult
{
    public static readonly WasmValidationResult Valid = new([]);

    public IReadOnlyList<WasmValidationDiagnostic> Diagnostics { get; }
    public bool IsValid => !Diagnostics.Any(d => d.Severity == WasmValidationSeverity.Error);

    public WasmValidationResult(IReadOnlyList<WasmValidationDiagnostic> diagnostics)
        => Diagnostics = diagnostics;
}
