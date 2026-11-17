// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

public enum DiagnosticSeverity
{
    Error,
    Warning,
    Info,
}

public sealed record WatDiagnostic(
    int Line,
    int Column,
    string Message,
    DiagnosticSeverity Severity = DiagnosticSeverity.Error);
