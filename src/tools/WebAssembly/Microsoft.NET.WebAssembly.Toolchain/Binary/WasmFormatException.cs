// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Exception thrown when a WebAssembly binary module contains invalid or unexpected data.
/// </summary>
public class WasmFormatException : Exception
{
    public WasmFormatException(string message) : base(message) { }
    public WasmFormatException(string message, Exception inner) : base(message, inner) { }
}
