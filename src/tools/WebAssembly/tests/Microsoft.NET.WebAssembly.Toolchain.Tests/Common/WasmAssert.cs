// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Toolchain.Tests;

/// <summary>
/// Structural assertions used in place of execution: the toolchain has no interpreter, so
/// tests verify that produced modules validate and expose the expected shape.
/// </summary>
internal static class WasmAssert
{
    public static WasmModule Valid(byte[] bytes) => ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

    public static FuncType ExportedFunction(WasmModule module, string name, ValType[] parameters, ValType[] results)
    {
        Assert.True(module.Exports.TryGetValue(name, out Export? export), $"Missing export '{name}'");
        var func = Assert.IsType<ExportDescriptor.Func>(export!.Descriptor);
        FuncType type = FunctionType(module, func.FunctionIndex);
        Assert.Equal(parameters, type.Parameters);
        Assert.Equal(results, type.Results);
        return type;
    }

    public static WasmFunction ExportedFunctionBody(WasmModule module, string name)
    {
        Assert.True(module.Exports.TryGetValue(name, out Export? export), $"Missing export '{name}'");
        var func = Assert.IsType<ExportDescriptor.Func>(export!.Descriptor);
        int funcImports = module.Imports.Count(i => i.Descriptor is ImportDescriptor.Func);
        Assert.True(func.FunctionIndex >= funcImports, $"Export '{name}' refers to an imported function");
        return module.Functions[func.FunctionIndex - funcImports];
    }

    public static FuncType FunctionType(WasmModule module, int functionIndex)
    {
        var funcImports = module.Imports.Where(i => i.Descriptor is ImportDescriptor.Func).ToList();
        int typeIndex = functionIndex < funcImports.Count
            ? ((ImportDescriptor.Func)funcImports[functionIndex].Descriptor).TypeIndex
            : module.Functions[functionIndex - funcImports.Count].TypeIndex;
        return module.Types[typeIndex];
    }

    public static string TestAssetPath(string name) => Path.Combine(AppContext.BaseDirectory, "TestAssets", name);
}
