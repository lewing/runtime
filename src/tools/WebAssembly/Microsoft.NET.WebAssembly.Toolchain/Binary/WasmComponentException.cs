// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Exception thrown when a WebAssembly Component Model binary is detected.
/// Components have a different header version than core modules and require
/// a different runtime (or conversion via wasm-tools).
/// </summary>
public class WasmComponentException : WasmFormatException
{
    public WasmComponentException()
        : base("This is a WebAssembly Component (WASI Preview 2). nesm currently supports core modules (WASI Preview 1). Use wasm-tools component lower to convert to a core module.")
    {
    }
}
