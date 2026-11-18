// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Wat;

public partial class WasmDisassemblerTests
{
    private static void AssertSameFunctionBodies(Microsoft.NET.WebAssembly.Module.WasmModule expected, Microsoft.NET.WebAssembly.Module.WasmModule actual)
    {
        Assert.Equal(expected.Functions.Count, actual.Functions.Count);
        for (int i = 0; i < expected.Functions.Count; i++)
        {
            Assert.Equal(expected.Functions[i].TypeIndex, actual.Functions[i].TypeIndex);
            Assert.Equal(expected.Functions[i].Locals, actual.Functions[i].Locals);
            Assert.Equal(expected.Functions[i].Body, actual.Functions[i].Body);
        }
    }

    [Fact]
    public void SimpleAdd_ProducesCorrectModule()
    {
        var wat = "(module (func (export \"add\") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        // Recompile and compare
        var module2 = WatCompiler.CompileAndValidate(disasm);
        AssertSameFunctionBodies(module, module2);
    }

    [Fact]
    public void DisassembleBytes_Works()
    {
        var wat = "(module (func (export \"id\") (param i32) (result i32) local.get 0))";
        var bytes = WatCompiler.Compile(wat);
        var disasm = WasmDisassembler.Disassemble(bytes);

        // Should produce valid WAT that encodes the same function body
        var module = WatCompiler.CompileAndValidate(disasm);
        var original = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        AssertSameFunctionBodies(original, module);
    }

    [Fact]
    public void ControlFlow_RoundTrips()
    {
        var wat = @"(module
          (func (export ""max"") (param i32 i32) (result i32)
            (if (result i32) (i32.gt_s (local.get 0) (local.get 1))
              (then (local.get 0))
              (else (local.get 1)))))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        AssertSameFunctionBodies(module, module2);
    }

    [Fact]
    public void Locals_RoundTrips()
    {
        var wat = @"(module
          (func (export ""test"") (param i32) (result i32)
            (local i32 i64)
            local.get 0
            i32.const 1
            i32.add
            local.set 1
            local.get 1))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        AssertSameFunctionBodies(module, module2);
    }

    [Fact]
    public void BlockLoop_RoundTrips()
    {
        var wat = @"(module
          (func (export ""sum"") (param i32) (result i32)
            (local i32 i32)
            (local.set 1 (i32.const 0))
            (local.set 2 (i32.const 0))
            (block
              (loop
                (br_if 1 (i32.ge_s (local.get 2) (local.get 0)))
                (local.set 1 (i32.add (local.get 1) (local.get 2)))
                (local.set 2 (i32.add (local.get 2) (i32.const 1)))
                (br 0)))
            (local.get 1)))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        AssertSameFunctionBodies(module, module2);
    }

    [Fact]
    public void Factorial_RoundTrips()
    {
        var wat = @"(module
          (func $fac (export ""fac"") (param i64) (result i64)
            (if (result i64) (i64.eqz (local.get 0))
              (then (i64.const 1))
              (else
                (i64.mul
                  (local.get 0)
                  (call $fac (i64.sub (local.get 0) (i64.const 1))))))))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        AssertSameFunctionBodies(module, module2);
    }
}
