// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

#if NET
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Microsoft.Build.Framework;
using Microsoft.NET.WebAssembly;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Linker.Components;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

namespace Microsoft.WebAssembly.Build.Tasks;

// In-process replacements for the native tools ComposeWasiReadyToRun runs, using src/tools/WebAssembly.
public sealed partial class ComposeWasiReadyToRun
{
    private static readonly ModuleReaderOptions s_allFeatures = new() { Features = WasmFeatures.All };

    // `wasm-tools component unbundle` writes the first core module above its size threshold as module0.
    private static string ExtractHostModuleManaged(string componentPath, string outputDirectory)
    {
        byte[] module = ComponentCoreModules.GetCoreModules(File.ReadAllBytes(componentPath))
            .FirstOrDefault(static m => m.Length > ComponentCoreModules.DefaultUnbundleThreshold)
            ?? throw new LogAsErrorException($"{componentPath} has no core module larger than {ComponentCoreModules.DefaultUnbundleThreshold} bytes.");
        string hostModule = Path.Combine(outputDirectory, "unbundled-module0.wasm");
        File.WriteAllBytes(hostModule, module);
        return hostModule;
    }

    // `wasm-tools parse` followed by `wasm-tools validate`.
    private static void CompileShimManaged(string shimWat, string shimPath)
    {
        byte[] shim = WatCompiler.Compile(shimWat, sourceFile: Path.ChangeExtension(shimPath, ".wat"));
        using (MemoryStream stream = new(shim, writable: false))
            ModuleReader.ReadModuleAndValidate(stream, s_allFeatures);
        File.WriteAllBytes(shimPath, shim);
    }

    // `wasm-merge -g <inputs as name pairs> -o <output>`.
    private void MergeManaged(IReadOnlyList<(string Path, string Name)> inputs, string outputPath)
    {
        List<(WasmModule Module, string Name)> modules = new(inputs.Count);
        foreach ((string path, string name) in inputs)
        {
            using FileStream stream = File.OpenRead(path);
            modules.Add((ModuleReader.ParseModuleForAnalysis(stream, s_allFeatures), name));
        }

        File.WriteAllBytes(outputPath, ModuleMerger.MergeToBinary(modules, new ModuleMergeOptions
        {
            DebugInfo = true,
            Warning = message => Log.LogMessage(MessageImportance.High, $"wasm merge: {message}"),
        }));
    }

    // `wasm-tools component` replace-module equivalent, which also checks the replacement fits the original's slot.
    private static void ReplaceHostModuleManaged(string componentPath, string modulePath, string outputPath) =>
        File.WriteAllBytes(outputPath, ComponentCoreModules.ReplaceCoreModule(
            File.ReadAllBytes(componentPath), 0, File.ReadAllBytes(modulePath)));

    private static bool IsManagedToolchainException(Exception ex) =>
        ex is LinkerException or WasmFormatException or WasmValidationException or WatException
            or NotSupportedException or FormatException or InvalidDataException;
}
#else
using System;
using System.Collections.Generic;

namespace Microsoft.WebAssembly.Build.Tasks;

// The managed toolchain targets .NET only; ValidateCompositionArguments rejects UseManagedTools before these run.
public sealed partial class ComposeWasiReadyToRun
{
    private static string ExtractHostModuleManaged(string componentPath, string outputDirectory) => throw new PlatformNotSupportedException();
    private static void CompileShimManaged(string shimWat, string shimPath) => throw new PlatformNotSupportedException();
#pragma warning disable CA1822 // Matches the instance method in the .NET build.
    private void MergeManaged(IReadOnlyList<(string Path, string Name)> inputs, string outputPath) => throw new PlatformNotSupportedException();
#pragma warning restore CA1822
    private static void ReplaceHostModuleManaged(string componentPath, string modulePath, string outputPath) => throw new PlatformNotSupportedException();
}
#endif
