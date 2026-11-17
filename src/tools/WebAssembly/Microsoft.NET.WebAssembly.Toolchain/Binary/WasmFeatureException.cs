// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Thrown when a WebAssembly binary uses an opcode or feature that is not enabled
/// in the current <see cref="WasmFeatures"/> set.
/// </summary>
public sealed class WasmFeatureException : WasmFormatException
{
    /// <summary>The feature flag that must be enabled for the failing opcode.</summary>
    public WasmFeatures RequiredFeature { get; }

    public WasmFeatureException(WasmFeatures feature, string opcodeName)
        : base($"Opcode '{opcodeName}' requires the '{feature}' feature, which is disabled.")
        => RequiredFeature = feature;
}
