// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

public partial class WasmLinkerTests
{
    [Fact]
    public void Link_TwoModules_MergedModulePreservesFunctionBodies()
    {
        // A structurally merged module (no cross-module calls, no relocations) must emit a valid
        // binary whose exported functions keep their original bodies.
        var modA = WatCompiler.CompileAndValidate(
            """
            (module (func (export "double") (param i32) (result i32)
              (i32.mul (local.get 0) (i32.const 2))))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module (func (export "negate") (param i32) (result i32)
              (i32.sub (i32.const 0) (local.get 0))))
            """);

        var merged = WasmAssert.Valid(_linker.LinkToBinary([modA, modB]));

        Assert.Equal(modA.Functions[0].Body, WasmAssert.ExportedFunctionBody(merged, "double").Body);
        Assert.Equal(modB.Functions[0].Body, WasmAssert.ExportedFunctionBody(merged, "negate").Body);
    }
}
