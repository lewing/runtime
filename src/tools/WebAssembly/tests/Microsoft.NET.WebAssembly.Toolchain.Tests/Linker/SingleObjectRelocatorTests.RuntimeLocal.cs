// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using System.Reflection;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Validation;

public partial class SingleObjectRelocatorTests
{
    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public void ParseModuleForAnalysis_AllowsTransformAndEmitButRuntimeLoadStillRejectsIt()
    {
        byte[] objBytes = BuildRelocatableObject();
        using var objectStream = new MemoryStream(objBytes, writable: false);

        Assert.NotNull(typeof(ModuleReader).GetMethod(
            nameof(ModuleReader.ParseModuleForAnalysis),
            BindingFlags.Public | BindingFlags.Static));
        var module = ModuleReader.ParseModuleForAnalysis(objectStream);

        Assert.NotNull(module.LinkingSection);

        var data = module.DataSegments[0];
        module.DataSegments[0] = new Data
        {
            IsPassive = data.IsPassive,
            MemoryIndex = data.MemoryIndex,
            OffsetExpr = data.OffsetExpr,
            InitData = [.. data.InitData, 0xFF],
            IsReadOnly = data.IsReadOnly,
        };
        byte[] transformedBytes = new SectionMerger().EmitBinary(module);
        Assert.NotEmpty(transformedBytes);

        using var loadStream = new MemoryStream(transformedBytes, writable: false);
        var loadException = Assert.Throws<WasmValidationException>(
            () => ModuleReader.ReadModule(loadStream));
        Assert.Contains(
            loadException.Diagnostics,
            static diagnostic => diagnostic.Code == WasmValidationCode.FunctionBodyTypeMismatch);
    }
}
