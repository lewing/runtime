// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests;

using Microsoft.NET.WebAssembly;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

public partial class ValidationTests
{
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
}
