// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

/// <summary>
/// Rebuilds real dotnet/runtime ObjectWriter outputs (ILCompiler relocatable objects and crossgen2 Webcil images,
/// as written by runtime's <c>artifacts/objcmp/run.sh</c>) through <see cref="WasmModuleWriter"/> and requires
/// identical bytes. Point <c>NESM_OBJCMP_DIR</c> at an objcmp output directory to run it.
/// </summary>
public sealed class ObjectWriterReferenceTests
{
    // WebCilObjectWriter.WebcilSectionAlignment: the Webcil payload segment is aligned to it in the file.
    private const int WebcilSectionAlignment = 16;

    public static TheoryData<string> References()
    {
        var data = new TheoryData<string>();
        string? dir = Environment.GetEnvironmentVariable("NESM_OBJCMP_DIR");
        if (dir is null || !Directory.Exists(dir))
        {
            data.Add("");
            return data;
        }

        foreach (string path in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories)
            .Where(p => p.EndsWith(".o", StringComparison.Ordinal) || p.EndsWith(".wasm", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetRelativePath(dir, path));
        }

        return data;
    }

    [ConditionalTheory]
    [MemberData(nameof(References))]
    public void RuntimeObjectWriterOutputRebuildsByteIdentically(string relativePath)
    {
        if (relativePath.Length == 0)
            throw new SkipTestException("Set NESM_OBJCMP_DIR to a dotnet/runtime artifacts/objcmp/<tag>/out directory.");

        string path = Path.Combine(Environment.GetEnvironmentVariable("NESM_OBJCMP_DIR")!, relativePath);
        byte[] original = File.ReadAllBytes(path);
        Assert.True(ObjectReencoder.ReadSections(original).Count > 3, $"{relativePath} has too few sections to be a writer output");
        ObjectReencoder.AssertBytesEqual(original, ObjectReencoder.Reencode(original, WebcilSectionAlignment), relativePath);
    }

    [ConditionalFact]
    public void ReferenceDirectoryIsNotSilentlyEmpty()
    {
        string? dir = Environment.GetEnvironmentVariable("NESM_OBJCMP_DIR");
        if (dir is null)
            throw new SkipTestException("Set NESM_OBJCMP_DIR to a dotnet/runtime artifacts/objcmp/<tag>/out directory.");

        Assert.True(Directory.Exists(dir), $"NESM_OBJCMP_DIR '{dir}' does not exist");
        string[] objects = Directory.GetFiles(dir, "*.o", SearchOption.AllDirectories);
        string[] images = Directory.GetFiles(dir, "*.wasm", SearchOption.AllDirectories);
        Assert.NotEmpty(objects);
        Assert.NotEmpty(images);
        Assert.Contains(images, image => ObjectReencoder.ReadSections(File.ReadAllBytes(image))
            .Any(s => s.Id == 0 && s.Name == "" && s.ContentLength > 0));
    }
}
