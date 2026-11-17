// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Controls how <see cref="ModuleReader"/> parses and validates a module.
/// </summary>
public sealed class ModuleReaderOptions
{
    /// <summary>Shared instance with all defaults. Use instead of <c>new()</c> in hot paths.</summary>
    public static readonly ModuleReaderOptions Default = new();

    /// <summary>MVP-only options: no extension features permitted.</summary>
    public static readonly ModuleReaderOptions Mvp = new() { Features = WasmFeatures.Mvp };

    /// <summary>Feature flags controlling which Wasm proposals are accepted.</summary>
    public WasmFeatures Features { get; init; } = WasmFeatures.Default;

    /// <summary>
    /// Controls handling of recognized custom metadata. Tolerant mode preserves the raw custom
    /// section and records diagnostics without rejecting the core module. Strict mode throws a
    /// <see cref="WasmMetadataException"/> for the first metadata error.
    /// </summary>
    public WasmMetadataValidationMode MetadataValidation { get; init; } =
        WasmMetadataValidationMode.Tolerant;

    /// <summary>
    /// Capacity of the bounded channel used by the streaming read path.
    /// Defaults to 16.
    /// </summary>
    public int FunctionBodyChannelCapacity { get; init; } = 16;
}

/// <summary>Controls whether recognized custom metadata errors are fatal.</summary>
public enum WasmMetadataValidationMode
{
    Tolerant,
    Strict,
}
