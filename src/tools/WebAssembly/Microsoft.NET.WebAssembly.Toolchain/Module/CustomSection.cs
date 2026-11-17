// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

/// <summary>Custom section (§2.5.1.1)</summary>
public sealed record CustomSection(string Name, byte[] Content);
