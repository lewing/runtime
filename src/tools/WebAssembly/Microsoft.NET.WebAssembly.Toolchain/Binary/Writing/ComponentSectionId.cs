// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>Component-model section IDs (component model Binary.md). Distinct from core <see cref="WasmSectionId"/>.</summary>
public enum ComponentSectionId : byte
{
    Custom = 0,
    CoreModule = 1,
    CoreInstance = 2,
    CoreType = 3,
    Component = 4,
    Instance = 5,
    Alias = 6,
    Type = 7,
    Canon = 8,
    Start = 9,
    Import = 10,
    Export = 11,
}
