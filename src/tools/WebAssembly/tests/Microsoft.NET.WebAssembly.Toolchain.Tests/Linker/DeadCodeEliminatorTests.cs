// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Tests for dead code elimination via WasmLinker.Link() with GcSections=true/false.
/// DCE triggers only when at least one input module has a LinkingSection.
/// </summary>
public class DeadCodeEliminatorTests
{
    private readonly WasmLinker _linker = new();

    // ── Survival / stripping ────────────────────────────────────────────────────

    [Fact]
    public void Dce_ExportedSymbol_FunctionSurvives()
    {
        var module = MakeFunctionModule("exported_func", exported: true);

        var merged = _linker.Link([module]);

        Assert.Single(merged.Functions);
    }

    [Fact]
    public void Dce_UnexportedSymbol_FunctionStripped()
    {
        var module = MakeFunctionModule("dead_func", exported: false);

        var merged = _linker.Link([module]);

        Assert.Empty(merged.Functions);
    }

    [Fact]
    public void Dce_NoExportsAnywhere_OutputHasNoFunctions()
    {
        var modA = MakeFunctionModule("a", exported: false);
        var modB = MakeFunctionModule("b", exported: false);

        var merged = _linker.Link([modA, modB]);

        Assert.Empty(merged.Functions);
    }

    // ── GcSections=false ─────────────────────────────────────────────────────────

    [Fact]
    public void Dce_GcSectionsFalse_EverythingPreserved()
    {
        var module = MakeFunctionModule("dead_func", exported: false);

        var merged = _linker.Link([module], new LinkerOptions { GcSections = false });

        Assert.Single(merged.Functions);
    }

    [Fact]
    public void Dce_GcSectionsFalse_MultipleModulesAllPreserved()
    {
        var modA = MakeFunctionModule("a", exported: false);
        var modB = MakeFunctionModule("b", exported: false);

        var merged = _linker.Link([modA, modB], new LinkerOptions { GcSections = false });

        Assert.Equal(2, merged.Functions.Count);
    }

    // ── Exports option ───────────────────────────────────────────────────────────

    [Fact]
    public void Dce_ExportsOption_FunctionRooted()
    {
        // The symbol does NOT have SymbolFlags.Exported but is named in Exports option.
        var module = MakeFunctionModule("opt_func", exported: false);

        var merged = _linker.Link(
            [module],
            new LinkerOptions { Exports = ["opt_func"] });

        Assert.Single(merged.Functions);
    }

    // ── NoStrip flag ─────────────────────────────────────────────────────────────

    [Fact]
    public void Dce_NoStripFlag_FunctionAlwaysSurvives()
    {
        var sym = new WasmSymbol
        {
            Type  = SymbolType.Function,
            Flags = SymbolFlags.NoStrip,  // no Exported, but NoStrip
            Name  = "nostrip_func",
            Index = 0,
        };
        var module = MakeFunctionModuleFromSymbol(sym);

        var merged = _linker.Link([module]);

        Assert.Single(merged.Functions);
    }

    // ── Data segment conservative fallback ──────────────────────────────────────

    [Fact]
    public void Dce_DataSegments_ConservativelyKeptWhenNoRelocSections()
    {
        // Module has linking section (triggers DCE), no reloc sections → data kept conservatively.
        var module = new WasmModule
        {
            Types     = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            Memories  = [new MemoryType(new Limits(1))],
            DataSegments =
            [
                new Data
                {
                    IsPassive = false,
                    MemoryIndex = 0,
                    OffsetExpr = [0x41, 0x00, 0x0B],
                    InitData = [0xAA, 0xBB],
                },
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    new WasmSymbol
                    {
                        Type  = SymbolType.Function,
                        Flags = SymbolFlags.None,  // NOT exported
                        Name  = "dead_func",
                        Index = 0,
                    },
                    new WasmSymbol
                    {
                        Type  = SymbolType.Data,
                        Flags = SymbolFlags.None,
                        Name  = ".data",
                        SegmentIndex = 0,
                    },
                ],
                SegmentInfos =
                [
                    new SegmentInfo { Name = ".data", Alignment = 0, Flags = SegmentFlags.None },
                ],
            },
        };

        var merged = _linker.Link([module]);

        // Function stripped (no export), but data segment kept (conservative fallback)
        Assert.Empty(merged.Functions);
        Assert.Single(merged.DataSegments);
    }

    // ── Element segment handling ─────────────────────────────────────────────────

    [Fact]
    public void Dce_ElementSegmentMembersKeptLiveWhenAnyCodeLive()
    {
        // Two functions: func 0 (exported root), func 1 (not exported but in element segment).
        // When any live non-import code exists, element members become roots.
        var module = new WasmModule
        {
            Types     = [new FuncType([], [])],
            Functions =
            [
                new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] },
                new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] },
            ],
            Tables = [new TableType(RefType.FuncRef, new Limits(2, 2))],
            Elements =
            [
                new Element
                {
                    TableIndex = 0,
                    OffsetExpr = [0x41, 0x00, 0x0B],
                    FunctionIndices = [1], // func 1 is in the table
                },
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.Exported, Name = "root", Index = 0 },
                    new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.None,     Name = "via_table", Index = 1 },
                ],
            },
        };

        var merged = _linker.Link([module]);

        // Both functions must survive: root because it's exported; via_table because the element segment pins it.
        Assert.Equal(2, merged.Functions.Count);
    }

    // ── InitFunctions as roots ───────────────────────────────────────────────────

    [Fact]
    public void Dce_InitFunction_IsAlwaysRoot()
    {
        // Function is NOT exported via symbol flags but IS listed as an InitFunction.
        var sym = new WasmSymbol
        {
            Type  = SymbolType.Function,
            Flags = SymbolFlags.None, // not exported
            Name  = "ctor",
            Index = 0,
        };

        var module = new WasmModule
        {
            Types     = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols = [sym],
                InitFunctions = [new InitFunction { Priority = 65535, SymbolIndex = 0 }],
            },
        };

        var merged = _linker.Link([module]);

        Assert.Single(merged.Functions);
    }

    // ── Transitive liveness via relocation data ──────────────────────────────────

    [Fact]
    public void Dce_TransitiveLiveness_CalleeKeptWhenCallerIsRoot()
    {
        // Construct a module with:
        //   func 0 (exported root): body contains `call 1` instruction
        //   func 1 (not exported): the callee
        // Plus a reloc.CODE section with a R_WASM_FUNCTION_INDEX_LEB entry that
        // records the dependency from func 0 → func 1.
        //
        // WAT: (module (func (call 1)) (func))
        // Binary code section payload layout (count=2):
        //   count  = 0x02                         @ offset 0
        //   entry0: size=0x04 body=[00 10 01 0B]  starts @ offset 1
        //   entry1: size=0x02 body=[00 0B]        starts @ offset 6
        // So CodeSectionOffset[0]=1, CodeSectionOffset[1]=6
        // The call operand (func index byte) is at:
        //   1 (entry0 offset) + 1 (size byte) + 1 (locals byte) + 1 (call opcode) = 4

        // Build function bodies that match the layout above
        // We use ModuleReader.Read on compiled WAT to get proper CodeSectionOffset values.
        byte[] watBytes = WatCompiler.Compile(
            """
            (module
              (func (call 1))
              (func))
            """);
        var parsed = Microsoft.NET.WebAssembly.Binary.ModuleReader.ReadModule(new MemoryStream(watBytes));

        // Attach a linking section: sym 0 = exported func 0, sym 1 = unexported func 1
        parsed.LinkingSection = new LinkingSection
        {
            Version = 2,
            Symbols =
            [
                new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.Exported, Name = "root",   Index = 0 },
                new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.None,     Name = "callee", Index = 1 },
            ],
        };

        // Add a reloc.CODE section: func 0 at code offset 4 references symbol 1 (callee)
        // The reloc.Offset = 4 (the call operand position in the code section)
        parsed.RelocationSections.Add(new RelocationSection
        {
            Name = "reloc.CODE",
            Entries =
            [
                new Relocation
                {
                    Type   = RelocationType.R_WASM_FUNCTION_INDEX_LEB,
                    Offset = 4,    // call operand within code section payload (see comment above)
                    Index  = 1,    // symbol 1 = callee
                },
            ],
        });

        var merged = _linker.Link([parsed]);

        // Both root (exported) and callee (reachable via relocation edge) must survive.
        Assert.Equal(2, merged.Functions.Count);
    }

    // ── Issue #8: live function import type index survives type compaction ───────

    [Fact]
    public void Dce_LiveFunctionImport_TypeIndexRemainsValidAfterCompaction()
    {
        // Regression test for issue #8.
        //
        // Two function imports share a module: env.dead_fn (dead, merged func index 0) and
        // env.live_fn (live, merged func index 1).  After DCE compaction, live_fn shifts from
        // pre-compaction merged index 1 to compacted index 0.
        //
        // Bug: CompactTypes compared the post-compaction index (0) against the pre-compaction
        // LiveFunctions set {1}, concluded live_fn was dead, omitted its type from liveTypes,
        // left the type_idx stale and out of range after the type table was compacted.
        //
        // Fix: use DeadFunctionsByModule (computed before ComposeCompactionIntoIndexMaps ran)
        // to decide liveness, not the already-compacted IndexMap.Functions values.

        var sigDeadFn = new FuncType([], []);           // type 0 — only dead import uses this
        var sigLiveFn = new FuncType([ValType.I32], []); // type 1 — only live import uses this

        var module = new WasmModule
        {
            Types   = [sigDeadFn, sigLiveFn],
            Imports =
            [
                new Import("env", "dead_fn", new ImportDescriptor.Func(0)), // merged func idx 0
                new Import("env", "live_fn", new ImportDescriptor.Func(1)), // merged func idx 1
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    // live_fn: exported (not Undefined) → CollectRoots roots it at merged func idx 1
                    new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.Exported, Name = "live_fn",  Index = 1 },
                    // dead_fn: not exported, not NoStrip → stripped by DCE
                    new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.None,     Name = "dead_fn",  Index = 0 },
                ],
            },
        };

        var merged = _linker.Link([module], new LinkerOptions { AllowUndefined = true });

        // live_fn must be the only import; its type_idx must address a valid type slot.
        Assert.Single(merged.Imports);
        Assert.Equal("live_fn", merged.Imports[0].Name);
        var funcDesc = Assert.IsType<ImportDescriptor.Func>(merged.Imports[0].Descriptor);
        Assert.True(funcDesc.TypeIndex < merged.Types.Count,
            $"import type_idx {funcDesc.TypeIndex} is out of range [0, {merged.Types.Count}) — " +
            "live function import type was incorrectly dropped by type compaction (issue #8)");
        Assert.Equal(sigLiveFn, merged.Types[funcDesc.TypeIndex]);
    }

    // ── Issue #10: dead tag import filtered symmetrically with function imports ───

    [Fact]
    public void Dce_DeadTagImport_FilteredSymmetricWithFunctionImport()
    {
        // Regression test for the tag-import variant of issue #8.
        //
        // dead_exc: tag import with a unique type, not referenced by any live code.
        // live_fn:  function import with a unique type, exported (rooted by DCE).
        //
        // After DCE, CompactTypes correctly drops dead_exc's type. Without the
        // FilterImports fix, dead_exc would survive with a stale type_idx pointing
        // to the wrong type (or out-of-range) after type compaction. With the fix,
        // dead_exc is filtered symmetrically with dead function imports.

        var sigDeadTag = new FuncType([ValType.F32], []);   // type 0 — unique to dead tag
        var sigLiveFn  = new FuncType([ValType.I32], []);   // type 1 — unique to live fn import

        var module = new WasmModule
        {
            Types   = [sigDeadTag, sigLiveFn],
            Imports =
            [
                new Import("env", "dead_exc", new ImportDescriptor.Tag(0)),  // local tag idx 0
                new Import("env", "live_fn",  new ImportDescriptor.Func(1)), // local func idx 0
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    // live_fn: exported → CollectRoots roots it at merged func idx 0
                    new WasmSymbol { Type = SymbolType.Function, Flags = SymbolFlags.Exported, Name = "live_fn",  Index = 0 },
                    // dead_exc: not exported, not NoStrip → tag remains unreachable (no live fn refs it)
                    new WasmSymbol { Type = SymbolType.Tag,      Flags = SymbolFlags.None,     Name = "dead_exc", Index = 0 },
                ],
            },
        };

        var merged = _linker.Link([module], new LinkerOptions { AllowUndefined = true });

        // dead_exc must be stripped; only live_fn survives.
        Assert.Single(merged.Imports);
        Assert.Equal("live_fn", merged.Imports[0].Name);
        var funcDesc = Assert.IsType<ImportDescriptor.Func>(merged.Imports[0].Descriptor);
        Assert.True(funcDesc.TypeIndex < merged.Types.Count,
            $"import type_idx {funcDesc.TypeIndex} is out of range [0, {merged.Types.Count}) — " +
            "live function import type was incorrectly affected by dead tag import (issue #10 regression)");
        Assert.Equal(sigLiveFn, merged.Types[funcDesc.TypeIndex]);
    }

    [Fact]
    public void Dce_ExportedTagImport_NotReferencedByLiveFunction_Survives()
    {
        // Exported tag imports must be rooted directly by CollectRoots, not only through
        // relocation edges from live functions. Otherwise the symmetric import filter
        // would drop an exported-but-otherwise-unused tag.

        var sigExportedTag = new FuncType([ValType.I32], []);

        var module = new WasmModule
        {
            Types   = [sigExportedTag],
            Imports =
            [
                new Import("env", "exported_exc", new ImportDescriptor.Tag(0)),
            ],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols =
                [
                    new WasmSymbol { Type = SymbolType.Tag, Flags = SymbolFlags.Exported, Name = "exported_exc", Index = 0 },
                ],
            },
        };

        var merged = _linker.Link([module], new LinkerOptions { AllowUndefined = true });

        var import = Assert.Single(merged.Imports);
        Assert.Equal("exported_exc", import.Name);
        var tagDesc = Assert.IsType<ImportDescriptor.Tag>(import.Descriptor);
        Assert.True(tagDesc.TypeIndex < merged.Types.Count,
            $"tag import type_idx {tagDesc.TypeIndex} is out of range [0, {merged.Types.Count}) — " +
            "exported tag import type was incorrectly dropped by DCE");
        Assert.Equal(sigExportedTag, merged.Types[tagDesc.TypeIndex]);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    private static WasmModule MakeFunctionModule(string symbolName, bool exported)
    {
        var sym = new WasmSymbol
        {
            Type  = SymbolType.Function,
            Flags = exported ? SymbolFlags.Exported : SymbolFlags.None,
            Name  = symbolName,
            Index = 0,
        };
        return MakeFunctionModuleFromSymbol(sym);
    }

    private static WasmModule MakeFunctionModuleFromSymbol(WasmSymbol sym)
    {
        return new WasmModule
        {
            Types     = [new FuncType([], [])],
            Functions = [new WasmFunction { TypeIndex = 0, Locals = [], Body = [0x0B] }],
            LinkingSection = new LinkingSection
            {
                Version = 2,
                Symbols = [sym],
            },
        };
    }
}
