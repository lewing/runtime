// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using Microsoft.NET.WebAssembly.Validation;
using Microsoft.NET.WebAssembly.Wat;

public partial class WatCompilerTests
{
    private static string LoadWat(string name) => File.ReadAllText(WasmAssert.TestAssetPath(name));

    // ── CompileFactorial ──

    [Fact]
    public void CompileFactorial()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("factorial.wat"));

        WasmAssert.ExportedFunction(module, "fac", [ValType.I32], [ValType.I32]);
    }

    [Fact]
    public void CompileFlatIfWithResult()
    {
        var wat = """
            (module
              (func (export "add") (param i32 i32) (result i32)
                local.get 0
                local.get 1
                i32.add)
              (func (export "factorial") (param i64) (result i64)
                local.get 0
                i64.const 1
                i64.le_s
                if (result i64)
                  i64.const 1
                else
                  local.get 0
                  local.get 0
                  i64.const 1
                  i64.sub
                  call 1
                  i64.mul
                end))
            """;

        var module = WatCompiler.CompileAndValidate(wat);

        WasmAssert.ExportedFunction(module, "add", [ValType.I32, ValType.I32], [ValType.I32]);
        WasmAssert.ExportedFunction(module, "factorial", [ValType.I64], [ValType.I64]);
    }

    // ── CompileMemory ──

    [Fact]
    public void CompileMemory()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("memory.wat"));

        WasmAssert.ExportedFunction(module, "store_and_load", [ValType.I32, ValType.I32], [ValType.I32]);
        Assert.NotEmpty(module.Memories);
    }

    // ── RoundTrip ──

    [Fact]
    public void RoundTrip_FactorialExportsMatch()
    {
        var wat = LoadWat("factorial.wat");
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        Assert.True(module.Exports.ContainsKey("fac"), "Should export 'fac'");
        Assert.True(module.Exports.ContainsKey("fib"), "Should export 'fib'");
        Assert.Equal(2, module.Exports.Count);
    }

    // ── Fibonacci via WAT compilation ──

    [Fact]
    public void CompileFibonacci()
    {
        var module = WatCompiler.CompileAndValidate(LoadWat("factorial.wat"));

        WasmAssert.ExportedFunction(module, "fib", [ValType.I32], [ValType.I32]);
    }

    // ── Comments are properly skipped ──

    [Fact]
    public void CommentsAreSkipped()
    {
        var wat = @"
(module
  ;; This is a line comment
  (func (export ""id"") (param i32) (result i32)
    (; block comment ;)
    local.get 0
  )
)";
        var module = WatCompiler.CompileAndValidate(wat);

        WasmAssert.ExportedFunction(module, "id", [ValType.I32], [ValType.I32]);
    }
}
