// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.DotNet.XUnitExtensions;
using Microsoft.NET.WebAssembly.Linker;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

/// <summary>
/// Real dotnet/runtime links captured from wasm-component-ld's wasm-ld invocation, linked through
/// <see cref="WasmLdCommandLine"/> and <see cref="WasmLdDriver"/> exactly as runtime does, and compared byte for byte with
/// wasm-ld 23's output: every section, including the name section (module name, demangled C++ names) and producers.
/// </summary>
/// <remarks>
/// A capture directory holds <c>ld-args.template</c> (one argument per line, <c>$here</c> standing for the directory)
/// and the reference output. Set <c>NESM_LINKCAP_DIR</c> to the NativeAOT capture (<c>ld.wasm</c>) and
/// <c>NESM_HOSTCAP_DIR</c> to the CoreCLR WASI host capture (<c>ld.core.wasm</c>).
/// </remarks>
public sealed class CaptureParityTests
{
    [ConditionalTheory]
    [InlineData("NESM_LINKCAP_DIR", "ld.wasm")]
    [InlineData("NESM_HOSTCAP_DIR", "ld.core.wasm")]
    public void LinkIsByteIdenticalToWasmLd(string variable, string referenceName)
    {
        string? dir = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "ld-args.template")) || !File.Exists(Path.Combine(dir, referenceName)))
            throw new SkipTestException($"{variable} does not point at a link capture with ld-args.template and {referenceName}.");

        string[] args = File.ReadAllLines(Path.Combine(dir, "ld-args.template"))
            .Where(static line => line.Length > 0)
            .Select(line => line.Replace("$here", dir, StringComparison.Ordinal))
            .ToArray();
        // wasm-ld names the module after its output file, so link to a file named like the reference.
        string output = Path.Combine(ExternalTool.CreateTempDirectory(), referenceName);
        WasmLdDriver.Run(WasmLdCommandLine.Parse([.. args, "-o", output]));

        byte[] expected = File.ReadAllBytes(Path.Combine(dir, referenceName));
        byte[] actual = File.ReadAllBytes(output);
        int firstDifference = expected.AsSpan().CommonPrefixLength(actual);
        Assert.True(expected.AsSpan().SequenceEqual(actual),
            $"{variable}: {actual.Length} bytes against wasm-ld's {expected.Length}; first difference at offset 0x{firstDifference:X}.");
    }
}
