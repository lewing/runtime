// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

public partial class WatIntegrationTests
{
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

    [Fact]
    public void Link_ValidProduct_AnalyticalModelAndSerializedProductBothValidate()
    {
        var first = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "first") (param i32) (result i32)
                (local i32)
                (local.set 1 (local.get 0))
                (local.get 1)))
            """);
        var second = WatCompiler.CompileAndValidate(
            """
            (module
              (func (export "second") (param i32) (result i32)
                (local.get 0)))
            """);

        var linked = _linker.Link([first, second]);

        // Compact metadata now makes the analytical model directly validatable despite retaining
        // linker-only state that is absent from the serialized product.
        Assert.Single(linked.Types);
        // The merged module carries an exact type-definition space in lockstep with its compact one.
        // It used to come out empty, and that emptiness was not free: every writer-side guard that
        // consults TypeDefinitions then passed on a hole rather than on a type, which is how a
        // struct-typed table reached the output as a function-typed one (GitHub #383).
        Assert.Equal(linked.Types.Count, linked.TypeDefinitions.Count);
        Assert.Equal(
            linked.Types[0].Parameters,
            ((CompositeType.Function)linked.TypeDefinitions[0].CompositeType)
                .Parameters.Select(p => p.ToStorageValType()).ToArray());
        Assert.Equal(
            linked.Types[0].Results,
            ((CompositeType.Function)linked.TypeDefinitions[0].CompositeType)
                .Results.Select(r => r.ToStorageValType()).ToArray());
        Assert.Equal(
            [new Microsoft.NET.WebAssembly.Types.ValueType.Number(NumberType.I32)],
            linked.Functions[0].ExactLocals);
        var directResult = WasmModuleValidator.Validate(linked);
        Assert.True(directResult.IsValid, FormatDiagnostics(directResult));

        byte[] shippedBinary = new SectionMerger().EmitBinary(linked);
        var shippedModule = ModuleReader.ReadModuleAndValidate(new MemoryStream(shippedBinary));
        AssertSemanticValidatorsAccept(shippedModule);
        Assert.Equal(first.Functions[0].Body, WasmAssert.ExportedFunctionBody(shippedModule, "first").Body);
    }

    private static void AssertSemanticValidatorsAccept(WasmModule module)
    {
        var typeResult = TypeDeclarationValidator.Validate(module);
        Assert.True(typeResult.IsValid, FormatDiagnostics(typeResult));

        var bodyResult = FunctionBodyValidator.ValidateModule(module, WasmFeatures.All);
        Assert.True(bodyResult.IsValid, FormatDiagnostics(bodyResult));

        var moduleResult = WasmModuleValidator.Validate(module);
        Assert.True(moduleResult.IsValid, FormatDiagnostics(moduleResult));
    }

    private static string FormatDiagnostics(WasmValidationResult result)
        => string.Join(Environment.NewLine, result.Diagnostics.Select(static diagnostic => diagnostic.Message));
}
