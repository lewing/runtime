// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Tests for <see cref="WasmModule.StartFunction"/> settability (issue #117).
/// Verifies that the property is mutable post-construction and round-trips through EmitBinary.
/// </summary>
public class WasmModuleStartFunctionTests
{
    [Fact]
    public void StartFunction_CanBeSetAfterConstruction()
    {
        var module = new WasmModule();
        module.StartFunction = 0;
        Assert.Equal(0, module.StartFunction);
        module.StartFunction = null;
        Assert.Null(module.StartFunction);
    }

    [Fact]
    public void StartFunction_RoundTripsViaEmitBinary()
    {
        // Build a module with one no-arg/no-result function so func index 0 is valid as start.
        var module = new WasmModule
        {
            Types = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
        };
        module.StartFunction = 0;

        var merger = new SectionMerger();
        byte[] binary = merger.EmitBinary(module);

        var parsed = ModuleReader.ReadModule(new MemoryStream(binary));
        Assert.Equal(0, parsed.StartFunction);
    }

    [Fact]
    public void StartFunction_Null_EmitsNoStartSection()
    {
        var module = new WasmModule
        {
            Types = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
        };
        // StartFunction is null — no start section expected.

        var merger = new SectionMerger();
        byte[] binary = merger.EmitBinary(module);

        var parsed = ModuleReader.ReadModule(new MemoryStream(binary));
        Assert.Null(parsed.StartFunction);
    }
}
