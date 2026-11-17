// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

/// <summary>A single validation finding with code, message, and severity.</summary>
public sealed record WasmValidationDiagnostic(
    WasmValidationCode Code,
    string Message,
    WasmValidationSeverity Severity = WasmValidationSeverity.Error);

public enum WasmValidationSeverity
{
    Warning,
    Error,
}
