// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using Microsoft.NET.WebAssembly.Types;

/// <summary>An import entry (§2.5.5)</summary>
public sealed record Import(string ModuleName, string Name, ImportDescriptor Descriptor);

public abstract record ImportDescriptor
{
    public sealed record Func(int TypeIndex) : ImportDescriptor;
    public sealed record Table(TableType Type) : ImportDescriptor;
    public sealed record Memory(MemoryType Type) : ImportDescriptor;
    public sealed record Global(GlobalType Type) : ImportDescriptor;
    public sealed record Tag(int TypeIndex) : ImportDescriptor;
}
