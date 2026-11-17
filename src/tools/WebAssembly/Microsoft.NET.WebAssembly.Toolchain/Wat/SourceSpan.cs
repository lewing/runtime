// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

/// <summary>Source location span for WAT AST nodes — prerequisite for LSP features.</summary>
public readonly record struct SourceSpan(int StartLine, int StartCol, int EndLine, int EndCol)
{
    public static readonly SourceSpan None;
}
