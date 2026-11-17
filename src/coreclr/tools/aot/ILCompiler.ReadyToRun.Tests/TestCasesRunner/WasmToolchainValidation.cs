// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.IO;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Validation;
using Xunit;

namespace ILCompiler.ReadyToRun.Tests.TestCasesRunner;

/// <summary>
/// Reads and validates WebAssembly output with the managed WebAssembly toolchain, which shares no code with
/// crossgen2's object writer.
/// </summary>
internal static class WasmToolchainValidation
{
    public static void AssertValid(string path)
    {
        try
        {
            using FileStream stream = File.OpenRead(path);
            ModuleReader.ReadModuleAndValidate(stream);
        }
        catch (Exception ex) when (ex is WasmFormatException or WasmValidationException)
        {
            Assert.Fail($"'{path}' is not a valid WebAssembly module: {ex.Message}");
        }
    }
}
