// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>WebAssembly section IDs (core spec §5.5.2).</summary>
public enum WasmSectionId : byte
{
    Custom = 0,
    Type = 1,
    Import = 2,
    Function = 3,
    Table = 4,
    Memory = 5,
    Global = 6,
    Export = 7,
    Start = 8,
    Element = 9,
    Code = 10,
    Data = 11,
    DataCount = 12,
    Tag = 13,
}

/// <summary>
/// Section content whose length is known before it is written, so that a <see cref="WasmModuleWriter"/> can
/// frame it without buffering.
/// </summary>
public interface IWasmSectionPayload
{
    /// <summary>The number of bytes <see cref="WriteTo"/> writes.</summary>
    long Length { get; }

    /// <summary>Writes exactly <see cref="Length"/> bytes to <paramref name="destination"/>.</summary>
    void WriteTo(Stream destination);
}
