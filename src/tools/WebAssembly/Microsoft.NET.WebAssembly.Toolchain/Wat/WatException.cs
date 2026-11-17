// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

/// <summary>Exception thrown during WAT compilation.</summary>
public class WatException : Exception
{
    public int Line { get; }
    public int Column { get; }

    public WatException(string message, int line = 0, int column = 0) : base(message)
    {
        Line = line;
        Column = column;
    }

    public WatException(string message, Exception inner) : base(message, inner) { }
}
