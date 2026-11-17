// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Tests for import merging in SectionMerger.MergeImports().
/// Covers memory/table limit merging, function deduplication, and incompatibility errors.
/// </summary>
public class ImportMergingTests
{
    // ── Memory limit merging ─────────────────────────────────────────────────────

    [Fact]
    public void MergeImports_SameMemory_DifferentMinLimits_TakesMax()
    {
        // Module A imports memory with min=1, Module B imports same with min=4.
        // Merged result must have min=4 (max of the two minimums).
        var modA = new WasmModule
        {
            Types   = [new FuncType([], [])],
            Imports = [new Import("env", "memory", new ImportDescriptor.Memory(new MemoryType(new Limits(1))))],
        };
        var modB = new WasmModule
        {
            Types   = [new FuncType([], [])],
            Imports = [new Import("env", "memory", new ImportDescriptor.Memory(new MemoryType(new Limits(4))))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);
        var result = merger.MergeImports([modA, modB], indexMaps);

        Assert.Single(result.Imports);
        var memImport = Assert.IsType<ImportDescriptor.Memory>(result.Imports[0].Descriptor);
        Assert.Equal(4u, memImport.Type.Limits.Min);
    }

    [Fact]
    public void MergeImports_SameMemory_BothHaveMax_TakesMaxOfMaxes()
    {
        var modA = new WasmModule
        {
            Imports = [new Import("env", "memory",
                new ImportDescriptor.Memory(new MemoryType(new Limits(1, 4))))],
        };
        var modB = new WasmModule
        {
            Imports = [new Import("env", "memory",
                new ImportDescriptor.Memory(new MemoryType(new Limits(2, 8))))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        var result    = merger.MergeImports([modA, modB], indexMaps);

        var mem = Assert.IsType<ImportDescriptor.Memory>(result.Imports[0].Descriptor);
        Assert.Equal(2u, mem.Type.Limits.Min);
        Assert.Equal(8u, mem.Type.Limits.Max);
    }

    // ── Function import deduplication ────────────────────────────────────────────

    [Fact]
    public void MergeImports_IdenticalFunctionImports_DeduplicatedToOne()
    {
        // Both modules import "env"."log" (i32) → (void).
        var sig = new FuncType([ValType.I32], []);
        var modA = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "log", new ImportDescriptor.Func(0))],
        };
        var modB = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "log", new ImportDescriptor.Func(0))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);
        var result = merger.MergeImports([modA, modB], indexMaps);

        Assert.Single(result.Imports);
        Assert.Equal(1u, result.FunctionImportCount);
    }

    [Fact]
    public void MergeImports_DistinctFunctionImports_BothPresent()
    {
        var sig = new FuncType([ValType.F64], [ValType.F64]);
        var modA = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "sin", new ImportDescriptor.Func(0))],
        };
        var modB = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "cos", new ImportDescriptor.Func(0))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);
        var result = merger.MergeImports([modA, modB], indexMaps);

        Assert.Equal(2, result.Imports.Count);
        Assert.Equal(2u, result.FunctionImportCount);
    }

    // ── Table limit merging ──────────────────────────────────────────────────────

    [Fact]
    public void MergeImports_SameTable_DifferentMinLimits_TakesMax()
    {
        var modA = new WasmModule
        {
            Imports = [new Import("env", "table",
                new ImportDescriptor.Table(new TableType(RefType.FuncRef, new Limits(2))))],
        };
        var modB = new WasmModule
        {
            Imports = [new Import("env", "table",
                new ImportDescriptor.Table(new TableType(RefType.FuncRef, new Limits(5))))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        var result    = merger.MergeImports([modA, modB], indexMaps);

        var tbl = Assert.IsType<ImportDescriptor.Table>(result.Imports[0].Descriptor);
        Assert.Equal(5u, tbl.Type.Limits.Min);
    }

    // ── Incompatible imports ─────────────────────────────────────────────────────

    [Fact]
    public void MergeImports_IncompatibleFunctionTypes_ThrowsLinkerException()
    {
        // Two modules import "env"."func" but with DIFFERENT signatures.
        var modA = new WasmModule
        {
            Types   = [new FuncType([ValType.I32], [])],
            Imports = [new Import("env", "func", new ImportDescriptor.Func(0))],
        };
        var modB = new WasmModule
        {
            Types   = [new FuncType([ValType.I64], [])],
            Imports = [new Import("env", "func", new ImportDescriptor.Func(0))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);

        Assert.Throws<LinkerException>(() => merger.MergeImports([modA, modB], indexMaps));
    }

    // ── Index map correctness ─────────────────────────────────────────────────────

    [Fact]
    public void MergeImports_SharedImport_BothModulesMappedToSameIndex()
    {
        // Two modules share "env"."malloc"; both should map to function import index 0.
        var sig = new FuncType([ValType.I32], [ValType.I32]);
        var modA = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "malloc", new ImportDescriptor.Func(0))],
        };
        var modB = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "malloc", new ImportDescriptor.Func(0))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);
        merger.MergeImports([modA, modB], indexMaps);

        // Both modules' local function index 0 (the import) should map to merged index 0.
        Assert.Equal(0u, indexMaps[0].Functions[0]);
        Assert.Equal(0u, indexMaps[1].Functions[0]);
    }

    // ── Mixed import kinds ───────────────────────────────────────────────────────

    [Fact]
    public void MergeImports_MixedKinds_CountsAreIndependent()
    {
        // One function import + one memory import from module A.
        // Same function import from module B.
        var sig = new FuncType([], []);
        var modA = new WasmModule
        {
            Types   = [sig],
            Imports =
            [
                new Import("env", "fn",     new ImportDescriptor.Func(0)),
                new Import("env", "memory", new ImportDescriptor.Memory(new MemoryType(new Limits(1)))),
            ],
        };
        var modB = new WasmModule
        {
            Types   = [sig],
            Imports = [new Import("env", "fn", new ImportDescriptor.Func(0))],
        };

        var indexMaps = new[] { new IndexMap(), new IndexMap() };
        var merger    = new SectionMerger();
        merger.MergeTypes([modA, modB], indexMaps);
        var result = merger.MergeImports([modA, modB], indexMaps);

        Assert.Equal(2, result.Imports.Count);
        Assert.Equal(1u, result.FunctionImportCount);
        Assert.Equal(1u, result.MemoryImportCount);
    }
}
