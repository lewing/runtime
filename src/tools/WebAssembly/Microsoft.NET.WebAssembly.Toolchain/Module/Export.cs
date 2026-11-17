// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>An export entry (§2.5.6)</summary>
public sealed record Export(string Name, ExportDescriptor Descriptor);

public abstract record ExportDescriptor
{
    public sealed record Func(int FunctionIndex) : ExportDescriptor;
    public sealed record Table(int TableIndex) : ExportDescriptor;
    public sealed record Memory(int MemoryIndex) : ExportDescriptor;
    public sealed record Global(int GlobalIndex) : ExportDescriptor;
    public sealed record Tag(int TagIndex) : ExportDescriptor;
}
