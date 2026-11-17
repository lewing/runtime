// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests;

using Microsoft.NET.WebAssembly;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

/// <summary>
/// Validation tests — assert that module loading and module validation throw
/// <see cref="WasmValidationException"/> for semantically invalid Wasm modules,
/// and <see cref="WasmFormatException"/> for binary format errors.
/// </summary>
public class ValidationTests
{
    // ── Wasm binary header: magic + version 1 ─────────────────────────────────
    private static readonly byte[] Header = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    [Fact]
    public void Validate_ModuleObject_RejectsInvalidLimits()
    {
        var invalidModule = new WasmModule
        {
            Memories = [new MemoryType(new Limits(2, 1))],
        };

        var exception = Assert.Throws<WasmValidationException>(
            () => WasmModuleValidator.ValidateAndThrow(invalidModule, invalidModule.EnabledFeatures));
        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.LimitsMaxLessThanMin);
    }

    [Fact]
    public void Validate_ModuleObject_RejectsDivergentTypeMetadata()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (func))
            """);
        var module = ModuleReader.ReadModule(new MemoryStream(bytes));
        module.Types[0] = new FuncType([ValType.I32], []);

        var exception = Assert.Throws<WasmValidationException>(
            () => WasmModuleValidator.ValidateAndThrow(module, module.EnabledFeatures));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TypeMetadataMismatch);
    }

    [Fact]
    public void Validate_ModuleObject_RejectsDivergentExactLocalMetadata()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (func (local i32)))
            """);
        var module = ModuleReader.ReadModule(new MemoryStream(bytes));
        module.Functions[0].ExactLocals[0] = new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.I64);

        var exception = Assert.Throws<WasmValidationException>(
            () => WasmModuleValidator.ValidateAndThrow(module, module.EnabledFeatures));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TypeMetadataMismatch);
    }

    [Fact]
    public void Validate_ModuleObject_RejectsFunctionUsingNonFunctionType()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (func))
            """);
        var module = ModuleReader.ReadModule(new MemoryStream(bytes));
        module.TypeDefinitions[0] = module.TypeDefinitions[0] with
        {
            CompositeType = new CompositeType.Struct([]),
        };

        var exception = Assert.Throws<WasmValidationException>(
            () => WasmModuleValidator.ValidateAndThrow(module, module.EnabledFeatures));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TypeIndexOutOfRange);
    }

    [Fact]
    public void Validate_ModuleObject_RejectsTagUsingInvalidType()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (tag))
            """);
        var module = ModuleReader.ReadModule(new MemoryStream(bytes));
        module.Tags[0] = new Tag(1);

        var exception = Assert.Throws<WasmValidationException>(
            () => WasmModuleValidator.ValidateAndThrow(module, module.EnabledFeatures));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TypeIndexOutOfRange);
    }

    [Theory]
    [InlineData("(tag (type $t))", 1, "Tag type must have an empty result type; found 1 result(s)")]
    [InlineData("(tag (type $t))", 2, "Tag type must have an empty result type; found 2 result(s)")]
    [InlineData("(import \"host\" \"tag\" (tag (type $t)))", 1, "Imported tag 'host.tag' type must have an empty result type; found 1 result(s)")]
    [InlineData("(import \"host\" \"tag\" (tag (type $t)))", 2, "Imported tag 'host.tag' type must have an empty result type; found 2 result(s)")]
    public void TagTypeUse_WithResults_IsRejectedByTagResultRule(
        string tagDeclaration,
        int resultCount,
        string expectedMessage)
    {
        string results = string.Join(' ', Enumerable.Repeat("i32", resultCount));
        byte[] bytes = WatCompiler.Compile($"""
            (module
              (type $t (func (result {results})))
              {tagDeclaration})
            """);

        var exception = Assert.Throws<WasmValidationException>(
            () => ModuleReader.ReadModule(new MemoryStream(bytes)));

        var diagnostic = Assert.Single(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TagResultTypeNotEmpty);
        Assert.Equal(expectedMessage, diagnostic.Message);
        Assert.DoesNotContain(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TypeIndexOutOfRange);
    }

    [Theory]
    [InlineData("(tag (result i32))", "Tag type must have an empty result type; found 1 result(s)")]
    [InlineData("(import \"host\" \"tag\" (tag (result i32)))", "Imported tag 'host.tag' type must have an empty result type; found 1 result(s)")]
    public void InlineTagResult_ReachesSemanticTagResultRule(
        string tagDeclaration,
        string expectedMessage)
    {
        byte[] bytes = WatCompiler.Compile($"(module {tagDeclaration})");

        var exception = Assert.Throws<WasmValidationException>(
            () => ModuleReader.ReadModule(new MemoryStream(bytes)));

        var diagnostic = Assert.Single(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.TagResultTypeNotEmpty);
        Assert.Equal(expectedMessage, diagnostic.Message);
    }

    [Theory]
    [InlineData("")]
    [InlineData("i32")]
    [InlineData("i32 i64 externref")]
    public void TagTypeUse_WithAnyParameterCount_Validates(string parameters)
    {
        string parameterClause = parameters.Length == 0 ? "" : $"(param {parameters})";
        var module = WatCompiler.CompileAndValidate($"""
            (module
              (type $defined (func {parameterClause}))
              (type $imported (func {parameterClause}))
              (import "host" "tag" (tag (type 1)))
              (tag (type $defined)))
            """);

        Assert.Single(module.Tags);
        Assert.Single(module.Imports, import => import.Descriptor is ImportDescriptor.Tag);
        Assert.All(module.Types, type => Assert.Empty(type.Results));
    }

    [Fact]
    public void StackSwitchingFeature_AllowsResultfulTagType()
    {
        byte[] bytes = WatCompiler.Compile("""
            (module
              (type $suspender (func (param i32) (result i64)))
              (tag (type $suspender)))
            """);

        var module = ModuleReader.ReadModule(
            new MemoryStream(bytes),
            new ModuleReaderOptions { Features = WasmFeatures.All });
        WasmModuleValidator.ValidateAndThrow(module, module.EnabledFeatures);

        Assert.Single(module.Tags);
        Assert.Single(module.Types[0].Results);
    }

    private static byte[] BuildModule(params byte[][] sections)
    {
        int total = Header.Length + sections.Sum(s => s.Length);
        var result = new byte[total];
        Header.CopyTo(result, 0);
        int offset = Header.Length;
        foreach (var s in sections)
        {
            s.CopyTo(result, offset);
            offset += s.Length;
        }
        return result;
    }

    // ── Reusable section building blocks ─────────────────────────────────────

    // Type section: 1 type  () -> ()
    private static readonly byte[] TypeSection_EmptySig =
    [
        0x01,                   // section id: type
        0x04,                   // section size
        0x01,                   // 1 type
        0x60,                   // func type
        0x00,                   // 0 params
        0x00,                   // 0 results
    ];

    // Type section: 1 type  (i32) -> ()
    private static readonly byte[] TypeSection_I32Param =
    [
        0x01,                   // section id: type
        0x05,                   // section size
        0x01,                   // 1 type
        0x60,                   // func type
        0x01, 0x7F,             // 1 param: i32
        0x00,                   // 0 results
    ];

    // Function section: 1 function, type index 0
    private static readonly byte[] FuncSection_Type0 =
    [
        0x03,                   // section id: function
        0x02,                   // section size
        0x01,                   // 1 function
        0x00,                   // type index 0
    ];

    // Code section: 1 entry with empty body (no locals, end)
    private static readonly byte[] CodeSection_OneEmpty =
    [
        0x0A,                   // section id: code
        0x04,                   // section size
        0x01,                   // 1 code entry
        0x02,                   // body size = 2
        0x00,                   // no locals
        0x0B,                   // end
    ];

    // Table section: 1 funcref table, min=1, no max
    private static readonly byte[] TableSection_OneValid =
    [
        0x04,                   // section id: table
        0x04,                   // section size
        0x01,                   // 1 table
        0x70,                   // funcref
        0x00,                   // limits: min only
        0x01,                   // min = 1
    ];

    // ── 1. Invalid type index ──────────────────────────────────────────────────

    [Fact]
    public void InvalidTypeIndex_FunctionReferencesNonExistentType_ThrowsWasmFormatException()
    {
        // Type section has only type 0; function section references type index 1.
        byte[] funcSection =
        [
            0x03,               // section id: function
            0x02,               // section size
            0x01,               // 1 function
            0x01,               // type index 1 — out of bounds!
        ];

        var bytes = BuildModule(TypeSection_EmptySig, funcSection, CodeSection_OneEmpty);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 2. Multiple memories (MVP allows at most one) ──────────────────────────

    [Fact]
    public void MultipleMemories_AcceptedWithMultiMemoryProposal()
    {
        byte[] memorySection =
        [
            0x05,               // section id: memory
            0x05,               // section size
            0x02,               // 2 memories — allowed by multi-memory proposal
            0x00, 0x01,         // memory 0: min=1, no max
            0x00, 0x01,         // memory 1: min=1, no max
        ];

        var bytes = BuildModule(memorySection);

        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        Assert.Equal(2, module.Memories.Count);
    }

    // ── 3. Memory limits: min > max ────────────────────────────────────────────

    [Fact]
    public void MemoryLimits_MinGreaterThanMax_ThrowsWasmFormatException()
    {
        byte[] memorySection =
        [
            0x05,               // section id: memory
            0x04,               // section size
            0x01,               // 1 memory
            0x01,               // limits flag: has max
            0x05,               // min = 5
            0x03,               // max = 3  — invalid!
        ];

        var bytes = BuildModule(memorySection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 4. Table limits: min > max ─────────────────────────────────────────────

    [Fact]
    public void TableLimits_MinGreaterThanMax_ThrowsWasmFormatException()
    {
        byte[] tableSection =
        [
            0x04,               // section id: table
            0x05,               // section size
            0x01,               // 1 table
            0x70,               // funcref
            0x01,               // limits flag: has max
            0x05,               // min = 5
            0x03,               // max = 3  — invalid!
        ];

        var bytes = BuildModule(tableSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 5. Duplicate export names ──────────────────────────────────────────────

    [Fact]
    public void DuplicateExports_SameNameExportedTwice_ThrowsWasmFormatException()
    {
        // Two exports both named "foo" pointing at function index 0.
        // "foo" = 0x66 0x6F 0x6F
        byte[] exportSection =
        [
            0x07,                                               // section id: export
            0x0D,                                               // section size = 13
            0x02,                                               // 2 exports
            0x03, 0x66, 0x6F, 0x6F, 0x00, 0x00,               // "foo", func, index 0
            0x03, 0x66, 0x6F, 0x6F, 0x00, 0x00,               // "foo", func, index 0  — duplicate!
        ];

        var bytes = BuildModule(exportSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 6. Start function: non-existent function index ────────────────────────

    [Fact]
    public void StartFunction_NonExistentFunctionIndex_ThrowsWasmFormatException()
    {
        // No functions defined, but start section references function index 5.
        byte[] startSection =
        [
            0x08,               // section id: start
            0x01,               // section size
            0x05,               // function index 5 — out of bounds!
        ];

        var bytes = BuildModule(startSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 7. Start function: wrong signature (must be [] -> []) ─────────────────

    [Fact]
    public void StartFunction_WrongSignature_ThrowsWasmFormatException()
    {
        // Function 0 has type (i32) -> (); start must be [] -> [].
        byte[] startSection =
        [
            0x08,               // section id: start
            0x01,               // section size
            0x00,               // function index 0 — wrong signature!
        ];

        var bytes = BuildModule(TypeSection_I32Param, FuncSection_Type0, startSection, CodeSection_OneEmpty);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 8. Element segment: out-of-bounds table index ─────────────────────────

    [Fact]
    public void ElementSegment_OutOfBoundsTableIndex_ThrowsWasmFormatException()
    {
        // No tables defined; element segment references table index 5.
        // Use post-MVP element segment format: flags=2 (active, explicit tableidx).
        byte[] elementSection =
        [
            0x09,               // section id: element
            0x08,               // section size (8 bytes)
            0x01,               // 1 element
            0x02,               // flags=2: active, explicit tableidx
            0x05,               // tableidx=5 — out of bounds!
            0x41, 0x00, 0x0B,   // offset: i32.const 0, end
            0x00,               // elemkind=0 (funcref)
            0x00,               // 0 function indices
        ];

        var bytes = BuildModule(elementSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 9. Element segment: out-of-bounds function index ─────────────────────

    [Fact]
    public void ElementSegment_OutOfBoundsFunctionIndex_ThrowsWasmFormatException()
    {
        // Table 0 exists; no functions defined; element references function index 5.
        byte[] elementSection =
        [
            0x09,               // section id: element
            0x07,               // section size
            0x01,               // 1 element
            0x00,               // table index 0 — valid
            0x41, 0x00, 0x0B,   // offset: i32.const 0, end
            0x01,               // 1 function index
            0x05,               // function index 5 — out of bounds!
        ];

        var bytes = BuildModule(TableSection_OneValid, elementSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 10. Data segment: out-of-bounds memory index ──────────────────────────

    [Fact]
    public void DataSegment_OutOfBoundsMemoryIndex_ThrowsWasmFormatException()
    {
        // No memories defined; active data segment (flag=2) explicitly references memory index 1.
        byte[] dataSection =
        [
            0x0B,               // section id: data
            0x07,               // section size = 7 bytes
            0x01,               // 1 data segment
            0x02,               // flag = 2 (active, explicit memory index)
            0x01,               // memory index 1 — out of bounds!
            0x41, 0x00, 0x0B,   // offset: i32.const 0, end
            0x00,               // 0 data bytes
        ];

        var bytes = BuildModule(dataSection);

        Assert.Throws<WasmValidationException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 11. Function / code section count mismatch ────────────────────────────
    // (Already implemented — serves as a regression guard and pattern anchor.)

    [Fact]
    public void FunctionCodeCountMismatch_ThrowsWasmFormatException()
    {
        // Function section declares 2 functions but code section has only 1 body.
        byte[] funcSection =
        [
            0x03,               // section id: function
            0x03,               // section size
            0x02,               // 2 functions
            0x00,               // type index 0
            0x00,               // type index 0
        ];

        var bytes = BuildModule(TypeSection_EmptySig, funcSection, CodeSection_OneEmpty);

        // Format error (parse-level): WasmFormatException propagates through ReadAndValidate.
        Assert.Throws<WasmFormatException>(() => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes)));
    }

    // ── 12. Operand/local type checking ────────────────────────────────────────
    // This module stores an i64 value into an i32-declared local via local.tee —
    // ill-typed per the spec. A conforming validator rejects it with
    // "type mismatch: expected i32, found i64".
    //
    // Type section: 1 type  () -> i32
    private static readonly byte[] TypeSection_ResultI32 =
    [
        0x01,                   // section id: type
        0x05,                   // section size
        0x01,                   // 1 type
        0x60,                   // func type
        0x00,                   // 0 params
        0x01, 0x7F,             // 1 result: i32
    ];

    // Type section: 1 type  () -> i64
    private static readonly byte[] TypeSection_ResultI64 =
    [
        0x01,                   // section id: type
        0x05,                   // section size
        0x01,                   // 1 type
        0x60,                   // func type
        0x00,                   // 0 params
        0x01, 0x7E,             // 1 result: i64
    ];

    // Code section: locals [i32, i64]; (i64.mul of two i64.const) tee'd into the
    // local at <teeIndex>, dropped, then that local is reloaded and returned.
    private static byte[] CodeSection_I64MulTeeInto(byte teeIndex)
    {
        byte[] body =
        [
            0x02, 0x01, 0x7F, 0x01, 0x7E,   // 2 local groups: 1×i32 (local 0), 1×i64 (local 1)
            0x42, 0x06,                     // i64.const 6
            0x42, 0x07,                     // i64.const 7
            0x7E,                           // i64.mul   -> i64 on stack
            0x22, teeIndex,                 // local.tee <teeIndex>
            0x1A,                           // drop
            0x20, teeIndex,                 // local.get <teeIndex>
            0x0B,                           // end
        ];
        return [0x0A, (byte)(body.Length + 2), 0x01, (byte)body.Length, .. body];
    }

    [Fact]
    public void IllTypedLocalTee_I64StoredIntoI32Local_RejectedByPublicModuleLoad()
    {
        // local.tee 0 stores the i64 product into i32-declared local 0 — ill-typed.
        // wasmtime rejects this exact module ("expected i32, found i64").
        var bytes = BuildModule(TypeSection_ResultI32, FuncSection_Type0, CodeSection_I64MulTeeInto(0));

        var exception = Assert.Throws<WasmValidationException>(
            () => ModuleReader.ReadModule(new MemoryStream(bytes)));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.FunctionBodyTypeMismatch);
    }

    [Fact]
    public void WellTypedLocalTee_I64StoredIntoI64Local_Validates()
    {
        // Identical, except the i64 product is tee'd into the i64-declared local 1.
        var bytes = BuildModule(TypeSection_ResultI64, FuncSection_Type0, CodeSection_I64MulTeeInto(1));

        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.Single(module.Functions);
    }

    [Fact]
    public void LegacyRethrow_LabelDepthCountsAllEnclosingLabels()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (tag $e)
              (func
                (try
                  (do (throw $e))
                  (catch $e
                    (block
                      (rethrow 1)
                    )
                  )
                )
              )
            )
            """);

        var module = ModuleReader.ReadModule(new MemoryStream(bytes));

        Assert.Single(module.Functions);
    }

    [Fact]
    public void LegacyRethrow_LabelMustIdentifyCatchFrame()
    {
        var bytes = WatCompiler.Compile("""
            (module
              (tag $e)
              (func
                (try
                  (do (throw $e))
                  (catch $e
                    (block
                      (rethrow 0)
                    )
                  )
                )
              )
            )
            """);

        var exception = Assert.Throws<WasmValidationException>(
            () => ModuleReader.ReadModule(new MemoryStream(bytes)));

        Assert.Contains(
            exception.Diagnostics,
            diagnostic => diagnostic.Code == WasmValidationCode.FunctionBodyUnknownLabel);
    }
}
