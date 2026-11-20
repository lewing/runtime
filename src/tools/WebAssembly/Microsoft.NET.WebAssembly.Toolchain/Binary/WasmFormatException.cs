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

    internal WasmFormatException(string message, long binaryOffset) : base(message)
        => BinaryOffset = binaryOffset;

    internal WasmFormatException(string message, Exception inner, long? binaryOffset) : base(message, inner)
        => BinaryOffset = binaryOffset;

    /// <summary>
    /// The offset in the input of the byte the reader could not accept, when the reader that failed knows it: for a
    /// module, the offset from the start of the module (or of the stream it was read from); <see langword="null"/>
    /// otherwise.
    /// </summary>
    public long? BinaryOffset { get; }
}
