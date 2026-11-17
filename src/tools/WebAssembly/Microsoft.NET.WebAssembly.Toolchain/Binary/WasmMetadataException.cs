// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Raised by strict metadata validation. Core binary envelope errors continue to use
/// <see cref="WasmFormatException"/>.
/// </summary>
public sealed class WasmMetadataException : Exception
{
    public WasmMetadataDiagnostic Diagnostic { get; }

    public WasmMetadataException(WasmMetadataDiagnostic diagnostic)
        : base($"{diagnostic.SectionName}: {diagnostic.Message}")
    {
        Diagnostic = diagnostic;
    }
}
