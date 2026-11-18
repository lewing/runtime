// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests;

// nesm's tests use xunit v3; runtime runs xunit v2, which has no TestContext. Synced tests only read its cancellation token.
internal sealed class TestContext
{
    public static TestContext Current { get; } = new();

    public CancellationToken CancellationToken => CancellationToken.None;
}
