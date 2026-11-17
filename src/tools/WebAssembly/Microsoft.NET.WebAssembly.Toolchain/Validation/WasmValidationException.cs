// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

/// <summary>
/// Thrown by <see cref="WasmModuleValidator.ValidateAndThrow"/> when the module
/// violates one or more Wasm spec rules.  Distinct from <c>WasmFormatException</c>
/// which covers unreadable/malformed bytes.
/// </summary>
public sealed class WasmValidationException : Exception
{
    public IReadOnlyList<WasmValidationDiagnostic> Diagnostics { get; }

    public WasmValidationException(IReadOnlyList<WasmValidationDiagnostic> diagnostics)
        : base(FormatMessage(diagnostics))
        => Diagnostics = diagnostics;

    private static string FormatMessage(IReadOnlyList<WasmValidationDiagnostic> d)
        => d.Count > 0
            ? $"WebAssembly module validation failed with {d.Count} error(s): {d[0].Message}"
            : "WebAssembly module validation failed";
}
