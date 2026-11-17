// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// End-to-end integration tests that compile WAT modules, link them with WasmLinker,
/// and (where applicable) execute the result through the interpreter.
/// </summary>
public class WatIntegrationTests
{
    private readonly WasmLinker _linker = new();

    // ── Basic linking ────────────────────────────────────────────────────────────

    [Fact]
    public void Link_TwoWatModules_MergedModuleContainsBothExports()
    {
        var math = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "add") (param i32 i32) (result i32)
                (i32.add (local.get 0) (local.get 1))))
            """);

        var util = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "square") (param i32) (result i32)
                (i32.mul (local.get 0) (local.get 0))))
            """);

        var merged = _linker.Link([math, util]);

        Assert.True(merged.Exports.ContainsKey("add"));
        Assert.True(merged.Exports.ContainsKey("square"));
        Assert.Equal(2, merged.Functions.Count);
    }

    [Fact]
    public void Link_TwoWatModules_MergedModulePreservesFunctionBodies()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "triple") (param i32) (result i32)
                (i32.mul (local.get 0) (i32.const 3))))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "inc") (param i32) (result i32)
                (i32.add (local.get 0) (i32.const 1))))
            """);

        var merged = WasmAssert.Valid(_linker.LinkToBinary([modA, modB]));

        Assert.Equal(modA.Functions[0].Body, WasmAssert.ExportedFunctionBody(merged, "triple").Body);
        Assert.Equal(modB.Functions[0].Body, WasmAssert.ExportedFunctionBody(merged, "inc").Body);
    }

    // ── Shared import deduplication ──────────────────────────────────────────────

    [Fact]
    public void Link_SharedImport_DeduplicatedAndBothFunctionsRoute()
    {
        // Both modules import "env"."get_val" (→ i32); both should share one import slot.
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "get_val" (func (result i32)))
              (func (export "a_val") (result i32) (call 0)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "get_val" (func (result i32)))
              (func (export "b_val") (result i32) (call 0)))
            """);

        var merged = _linker.Link([modA, modB]);

        // One import, two local functions
        Assert.Single(merged.Imports);
        Assert.Equal(2, merged.Functions.Count);
        Assert.Equal("env",     merged.Imports[0].ModuleName);
        Assert.Equal("get_val", merged.Imports[0].Name);
    }

    // ── Binary round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void LinkToBinary_MergedModuleRoundTripsCorrectly()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "half") (param i32) (result i32)
                (i32.div_u (local.get 0) (i32.const 2))))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "double") (param i32) (result i32)
                (i32.mul (local.get 0) (i32.const 2))))
            """);

        byte[] bytes  = _linker.LinkToBinary([modA, modB]);
        var reparsed  = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.True(reparsed.Exports.ContainsKey("half"));
        Assert.True(reparsed.Exports.ContainsKey("double"));
        Assert.Equal(2, reparsed.Functions.Count);
        Assert.Single(reparsed.Types); // both share the same (i32)→(i32) signature
    }

    // ── GcSections=true with plain WAT (no linking section) ─────────────────────

    [Fact]
    public void Link_GcSectionsTrue_PlainWatModule_AllItemsPreserved()
    {
        // Plain WAT modules have no linking section, so DCE treats everything as live.
        var mod = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "visible")  (result i32) (i32.const 1))
              (func (export "also_visible") (result i32) (i32.const 2)))
            """);

        var merged = _linker.Link([mod], new LinkerOptions { GcSections = true });

        Assert.Equal(2, merged.Functions.Count);
        Assert.True(merged.Exports.ContainsKey("visible"));
        Assert.True(merged.Exports.ContainsKey("also_visible"));
    }

    // ── Type deduplication under linking ────────────────────────────────────────

    [Fact]
    public void Link_MultipleWatModules_TypesDeduplicatedAcrossAll()
    {
        // All three modules have (i32)→(i32); after linking there should be exactly one type.
        var modules = Enumerable.Range(0, 3).Select(i =>
            WatCompiler.CompileAndValidate(
                $$"""
                (module
                  (func (export "fn{{i}}") (param i32) (result i32)
                    (i32.const {{i}})))
                """)).ToList();

        var merged = _linker.Link(modules);

        Assert.Single(merged.Types);
        Assert.Equal(3, merged.Functions.Count);
    }

    // ── Single-module identity ────────────────────────────────────────────────────

    [Fact]
    public void Link_SingleModule_StructurallyIdentical()
    {
        var mod = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "id") (param i32) (result i32) (local.get 0))
              (func (export "zero") (result i32) (i32.const 0)))
            """);

        var merged = _linker.Link([mod]);

        Assert.Equal(mod.Types.Count,     merged.Types.Count);
        Assert.Equal(mod.Functions.Count, merged.Functions.Count);
        Assert.Equal(mod.Exports.Count,   merged.Exports.Count);
    }

    // ── DCE + WAT: mix of plain and object-file modules ──────────────────────────

    [Fact]
    public void Link_DceWithLinkedModuleAndPlainWat_PlainWatFunctionsAlwaysLive()
    {
        // Object-file module (with linking section): function NOT exported → will be stripped.
        var objectMod = new WasmModule
        {
            Types     = [new Microsoft.NET.WebAssembly.Types.FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    new WasmSymbol
                    {
                        Type  = SymbolType.Function,
                        Flags = SymbolFlags.None, // NOT exported
                        Name  = "dead_obj_func",
                        Index = 0,
                    },
                ],
            },
        };

        // Plain WAT module: no linking section → treated as all-live.
        var plainMod = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "plain_fn") (result i32) (i32.const 99)))
            """);

        var merged = _linker.Link([objectMod, plainMod]);

        // Object module's unexported function should be stripped; plain module's function kept.
        Assert.Single(merged.Functions);
        Assert.True(merged.Exports.ContainsKey("plain_fn"));
    }
}
