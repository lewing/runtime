// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker;

/// <summary>Thrown when the linker encounters an unrecoverable error such as a duplicate strong symbol.</summary>
public sealed class LinkerException : Exception
{
    public LinkerException(string message) : base(message) { }
    public LinkerException(string message, Exception inner) : base(message, inner) { }
}
