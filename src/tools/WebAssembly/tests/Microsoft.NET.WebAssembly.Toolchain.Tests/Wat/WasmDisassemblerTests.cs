// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Wat;

public class WasmDisassemblerTests
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
    public void SimpleAdd_RoundTrips()
    {
        var wat = "(module (func (export \"add\") (param i32 i32) (result i32) local.get 0 local.get 1 i32.add))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        // The disassembled WAT should recompile
        var bytes2 = WatCompiler.Compile(disasm);
        var module2 = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes2));

        // And the second disassembly should match the first
        var disasm2 = WasmDisassembler.Disassemble(module2);
        Assert.Equal(disasm, disasm2);
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
    public void Memory_RoundTrips()
    {
        var wat = "(module (memory (export \"mem\") 1 256))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var bytes2 = WatCompiler.Compile(disasm);
        var module2 = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes2));
        Assert.Equal(1u, module2.Memories[0].Limits.Min);
        Assert.Equal(256u, module2.Memories[0].Limits.Max);
    }

    [Fact]
    public void GlobalConst_RoundTrips()
    {
        var wat = "(module (global (export \"g\") i32 (i32.const 42)))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.ValType.I32, module2.Globals[0].Type.ContentType);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.Mutability.Const, module2.Globals[0].Type.Mutability);
    }

    [Fact]
    public void GlobalMut_RoundTrips()
    {
        var wat = "(module (global (export \"g\") (mut i32) (i32.const 0)))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Equal(Microsoft.NET.WebAssembly.Types.Mutability.Var, module2.Globals[0].Type.Mutability);
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
    public void DataSegment_RoundTrips()
    {
        var wat = @"(module
          (memory 1)
          (data (i32.const 0) ""hello""))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Single(module2.DataSegments);
        Assert.Equal("hello"u8.ToArray(), module2.DataSegments[0].InitData);
    }

    [Fact]
    public void Import_RoundTrips()
    {
        var wat = @"(module
          (import ""env"" ""log"" (func (param i32))))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);

        var module2 = WatCompiler.CompileAndValidate(disasm);
        Assert.Single(module2.Imports);
        Assert.Equal("env", module2.Imports[0].ModuleName);
        Assert.Equal("log", module2.Imports[0].Name);
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

    [Fact]
    public void RoundTrip_V128Locals()
    {
        var wat = "(module (func (local v128) v128.const i32x4 0 0 0 0 local.set 0))";
        var bytes = WatCompiler.Compile(wat);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));
        var disasm = WasmDisassembler.Disassemble(module);
        Assert.Contains("v128", disasm);
        var bytes2 = WatCompiler.Compile(disasm);
        Assert.Equal(bytes.Length, bytes2.Length);
    }
}
