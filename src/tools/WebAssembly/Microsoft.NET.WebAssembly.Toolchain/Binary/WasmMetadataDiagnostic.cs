// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>Classification used by non-fatal custom metadata diagnostics.</summary>
public enum WasmMetadataErrorKind
{
    Malformed,
    Invalid,
}

/// <summary>A structured diagnostic produced while reading recognized WebAssembly metadata.</summary>
public sealed record WasmMetadataDiagnostic(
    string SectionName,
    WasmMetadataErrorKind Kind,
    string Message,
    uint? FunctionIndex = null,
    uint? InstructionOffset = null);
