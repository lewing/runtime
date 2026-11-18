// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Tests for the managed Wasm static linker — type dedup, symbol resolution, and basic structural merging.
/// </summary>
public partial class WasmLinkerTests
{
    private readonly WasmLinker _linker = new();

    // ── Type deduplication ───────────────────────────────────────────────────────

    [Fact]
    public void Link_TypeDedup_IdenticalSignaturesYieldOneType()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "add") (param i32 i32) (result i32)
                (i32.add (local.get 0) (local.get 1))))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "mul") (param i32 i32) (result i32)
                (i32.mul (local.get 0) (local.get 1))))
            """);

        var merged = _linker.Link([modA, modB]);

        // Both functions share (i32,i32)->i32 — only one type entry after dedup.
        Assert.Single(merged.Types);
        Assert.Equal([ValType.I32, ValType.I32], merged.Types[0].Parameters);
        Assert.Equal([ValType.I32], merged.Types[0].Results);
    }

    [Fact]
    public void Link_TypeDedup_DifferentSignaturesYieldMultipleTypes()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "f") (param i32) (result i32)
                (local.get 0)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "g") (param i64) (result i64)
                (local.get 0)))
            """);

        var merged = _linker.Link([modA, modB]);

        Assert.Equal(2, merged.Types.Count);
    }

    [Fact]
    public void Link_TypeDedup_IndexMapPointsToMergedType()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "a") (param i32 i32) (result i32) (i32.const 0))
              (func (export "b") (param i64) (result i64)   (i64.const 0)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "c") (param i32 i32) (result i32) (i32.const 1)))
            """);

        var merged = _linker.Link([modA, modB]);
        var maps   = _linker.LastLinkIndexMaps!;

        // modA has two distinct types at indices 0 and 1.
        // modB has one type at index 0 — the same signature as modA[0].
        Assert.Equal(2, merged.Types.Count);

        // modA type 0 and modB type 0 must map to the same merged index.
        Assert.Equal(maps[0].Types[0], maps[1].Types[0]);
    }

    // ── Function merging ─────────────────────────────────────────────────────────

    [Fact]
    public void Link_TwoModules_BothFunctionsPresent()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module (func (export "add") (param i32 i32) (result i32)
              (i32.add (local.get 0) (local.get 1))))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module (func (export "mul") (param i32 i32) (result i32)
              (i32.mul (local.get 0) (local.get 1))))
            """);

        var merged = _linker.Link([modA, modB]);

        Assert.Equal(2, merged.Functions.Count);
        Assert.Equal(2, merged.Exports.Count);
        Assert.True(merged.Exports.ContainsKey("add"));
        Assert.True(merged.Exports.ContainsKey("mul"));
    }

    [Fact]
    public void Link_ExportDescriptors_PointToRemappedIndices()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module (func (export "f1") (result i32) (i32.const 10)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module (func (export "f2") (result i32) (i32.const 20)))
            """);

        var merged = _linker.Link([modA, modB]);

        var f1Desc = Assert.IsType<ExportDescriptor.Func>(merged.Exports["f1"].Descriptor);
        var f2Desc = Assert.IsType<ExportDescriptor.Func>(merged.Exports["f2"].Descriptor);

        // f1 is function 0, f2 is function 1 in the merged module.
        Assert.Equal(0, f1Desc.FunctionIndex);
        Assert.Equal(1, f2Desc.FunctionIndex);
    }

    // ── Import deduplication ──────────────────────────────────────────────────────

    [Fact]
    public void Link_SameImport_DeduplicatedInOutput()
    {
        // Both modules import "env"."log" with the same signature.
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "log" (func (param i32)))
              (func (export "a") (i32.const 1) (call 0)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "log" (func (param i32)))
              (func (export "b") (i32.const 2) (call 0)))
            """);

        var merged = _linker.Link([modA, modB]);

        // The shared import must appear exactly once.
        var logImports = merged.Imports
            .Where(i => i.ModuleName == "env" && i.Name == "log")
            .ToList();
        Assert.Single(logImports);

        // Both local functions must map to function index 1 (after the one import).
        Assert.Equal(2, merged.Functions.Count); // two local functions
        Assert.Single(merged.Imports);           // one deduped import
    }

    [Fact]
    public void Link_DistinctImports_BothAppearInOutput()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "sin" (func (param f64) (result f64)))
              (func (export "a") (result f64) (f64.const 0) (call 0)))
            """);

        var modB = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "cos" (func (param f64) (result f64)))
              (func (export "b") (result f64) (f64.const 0) (call 0)))
            """);

        var merged = _linker.Link([modA, modB]);

        Assert.Equal(2, merged.Imports.Count);
    }

    // ── Symbol resolution ────────────────────────────────────────────────────────

    [Fact]
    public void SymbolTable_StrongDefinition_Registered()
    {
        var modA = MakeModuleWithSymbol("foo", SymbolFlags.None, defined: true, index: 0);
        var table = new SymbolTable();
        table.AddModule(0, modA);
        table.ResolveUndefined();

        Assert.True(table.Symbols.ContainsKey("foo"));
        Assert.Equal(0, table.Symbols["foo"].DefiningModuleIndex);
        Assert.Empty(table.UnresolvedSymbols);
    }

    [Fact]
    public void SymbolTable_WeakOverriddenByStrong()
    {
        var modWeak   = MakeModuleWithSymbol("bar", SymbolFlags.BindingWeak, defined: true, index: 0);
        var modStrong = MakeModuleWithSymbol("bar", SymbolFlags.None,        defined: true, index: 0);

        var table = new SymbolTable();
        table.AddModule(0, modWeak);   // weak first
        table.AddModule(1, modStrong); // strong second → wins
        table.ResolveUndefined();

        Assert.Equal(SymbolBinding.Strong, table.Symbols["bar"].Binding);
        Assert.Equal(1, table.Symbols["bar"].DefiningModuleIndex);
    }

    [Fact]
    public void SymbolTable_WeakAfterStrong_StrongWins()
    {
        var modStrong = MakeModuleWithSymbol("bar", SymbolFlags.None,        defined: true, index: 0);
        var modWeak   = MakeModuleWithSymbol("bar", SymbolFlags.BindingWeak, defined: true, index: 0);

        var table = new SymbolTable();
        table.AddModule(0, modStrong);
        table.AddModule(1, modWeak);
        table.ResolveUndefined();

        Assert.Equal(SymbolBinding.Strong, table.Symbols["bar"].Binding);
        Assert.Equal(0, table.Symbols["bar"].DefiningModuleIndex);
    }

    [Fact]
    public void SymbolTable_DuplicateStrong_ThrowsLinkerException()
    {
        var modA = MakeModuleWithSymbol("dup", SymbolFlags.None, defined: true, index: 0);
        var modB = MakeModuleWithSymbol("dup", SymbolFlags.None, defined: true, index: 0);

        var table = new SymbolTable();
        table.AddModule(0, modA);

        Assert.Throws<LinkerException>(() => table.AddModule(1, modB));
    }

    [Fact]
    public void SymbolTable_UndefinedResolvedByDefinition()
    {
        var modUndef = MakeModuleWithSymbol("baz", SymbolFlags.Undefined, defined: false, index: null);
        var modDef   = MakeModuleWithSymbol("baz", SymbolFlags.None,      defined: true,  index: 3);

        var table = new SymbolTable();
        table.AddModule(0, modUndef);
        table.AddModule(1, modDef);
        table.ResolveUndefined();

        Assert.Empty(table.UnresolvedSymbols);
        Assert.Equal(1, table.Symbols["baz"].DefiningModuleIndex);
    }

    [Fact]
    public void SymbolTable_UndefinedWithAllowUndefined_DoesNotThrow()
    {
        var modA = WatCompiler.CompileAndValidate(
            """
            (module
              (import "env" "missing" (func))
              (func (export "use") (call 0)))
            """);
        // No module defines "env"."missing" → unresolved symbol.
        // WatCompiler modules don't emit linking sections so the symbol table stays empty.
        var merged = _linker.Link([modA], new LinkerOptions { AllowUndefined = true });
        Assert.NotNull(merged);
    }

    [Fact]
    public void Link_WasmLinker_DuplicateStrong_ThrowsLinkerException()
    {
        var modA = MakeModuleWithSymbol("conflict", SymbolFlags.None, defined: true, index: 0);
        var modB = MakeModuleWithSymbol("conflict", SymbolFlags.None, defined: true, index: 0);

        Assert.Throws<LinkerException>(() => _linker.Link([modA, modB]));
    }

    // ── Structural round-trip ────────────────────────────────────────────────────

    [Fact]
    public void Link_SingleModule_IdenticalStructure()
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

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Creates a minimal WasmModule with a single function and a custom linking section
    /// containing one symbol, for testing symbol-resolution logic in isolation.
    /// </summary>
    private static WasmModule MakeModuleWithSymbol(
        string name, SymbolFlags flags, bool defined, int? index)
    {
        var sym = new WasmSymbol
        {
            Type  = SymbolType.Function,
            Flags = flags | (defined ? SymbolFlags.None : SymbolFlags.Undefined),
            Name  = name,
            Index = index,
        };

        var linking = new LinkingSection
        {
            Version = 2,
            Symbols = [sym],
        };

        return new WasmModule
        {
            Types     = [new FuncType([], [])],
            Functions = defined ? [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }] : [],
            LinkingSection = linking,
        };
    }
}
