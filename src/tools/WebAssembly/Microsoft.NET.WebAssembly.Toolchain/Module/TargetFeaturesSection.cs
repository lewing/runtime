// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Module;

using System.Collections.Generic;

public sealed class TargetFeaturesSection
{
    public List<(char Prefix, string Name)> Features { get; init; } = [];
}
