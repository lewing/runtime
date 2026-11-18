// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;

public partial class ModuleReaderTests
{
    [Fact]
    public void RealWasmFiles_ExposeParsedProducerMetadataAndTargetFeatures()
    {
        string[] sampleNames = ["add.wasm", "math.wasm"];
        Assert.NotEmpty(sampleNames);
        foreach (string sampleName in sampleNames)
        {
            var module = ModuleReader.ReadModule(new MemoryStream(File.ReadAllBytes(WasmAssert.TestAssetPath(Path.Combine("c", sampleName)))));
            var producers = Assert.IsType<ProducersSection>(module.ProducersSection);
            var processedBy = Assert.Single(producers.Fields["processed-by"]);
            Assert.Equal("clang", processedBy.Name);
            Assert.Contains("19.1.0", processedBy.Version, StringComparison.Ordinal);

            var targetFeatures = Assert.IsType<TargetFeaturesSection>(module.TargetFeaturesSection);
            Assert.Contains(targetFeatures.Features, feature => feature is ('+', "mutable-globals"));
            Assert.Contains(targetFeatures.Features, feature => feature is ('+', "multivalue"));
        }
    }
}
