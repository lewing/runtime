// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Runtime-only members of nesm's test class, adapted to run here. nesm-sync replaces nesm's members of the
// same name with these on every sync (see src/tools/WebAssembly/README.md).

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

public partial class Memory64Tests
{
    /// <summary>
    /// A memory64 declaring a 2^48-page maximum (<c>memory64.wast:9</c>) must keep the declared
    /// maximum untruncated rather than overflowing or clamping it to a 32-bit value.
    /// </summary>
    [Fact]
    public void Memory64_LargeDeclaredMax_ParsesUntruncated()
    {
        const ulong largeMax = 0x1_0000_0000_0000UL;
        // flag 0x05 = has-max + is64
        var memSec = Section(5, EncodeU32(1), [0x05], EncodeU64(0), EncodeU64(largeMax));
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(Module(memSec)));

        var memory = Assert.Single(module.Memories);
        Assert.True(memory.Is64);
        Assert.Equal(0UL, memory.Limits.Min);
        Assert.Equal(largeMax, memory.Limits.Max);
    }

    /// <summary>
    /// A table64 declaring a 2^48-entry maximum must keep the declared maximum untruncated.
    /// </summary>
    [Fact]
    public void Table64_LargeDeclaredMax_ParsesUntruncated()
    {
        const ulong largeMax = 0x1_0000_0000_0000UL;
        // table64 (funcref, flag 0x05 = has-max + is64), min=0, max=2^48
        var tableSec = Section(4, EncodeU32(1), [0x70, 0x05], EncodeU64(0), EncodeU64(largeMax));
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(Module(tableSec)));

        var table = Assert.Single(module.Tables);
        Assert.True(table.Limits.Is64);
        Assert.Equal(0UL, table.Limits.Min);
        Assert.Equal(largeMax, table.Limits.Max);
    }

    /// <summary>
    /// In a two-memory module (memory32 idx=0, memory64 idx=1), <c>memory.size 1</c> must be typed
    /// by memory 1 and produce an i64; typing it by memory 0 would yield an i32 and fail validation.
    /// </summary>
    [Fact]
    public void MemorySize_MultiMemory_Index1ValidatesAsI64()
    {
        // (memory 1) (memory i64 2)
        // (func (export "size1") (result i64) memory.size 1)
        var typeSec   = Section(1, EncodeU32(1), [0x60, 0x00, 0x01, 0x7E]); // () -> i64
        var funcSec   = Section(3, EncodeU32(1), [0x00]);
        // two memories: memory32 min=1, memory64 min=2
        var memSec    = Section(5, EncodeU32(2),
            [0x00], EncodeU32(1),       // memory32, min=1, no max
            [0x04], EncodeU64(2));      // memory64, min=2, no max
        var exportSec = Section(7, EncodeU32(1), [.. EncodeName("size1"), 0x00, .. EncodeU32(0)]);
        // body: memory.size 1 (0x3F 0x01), end
        byte[] body   = [0x00, 0x3F, 0x01, 0x0B];
        var codeSec   = Section(10, EncodeU32(1), EncodeU32((uint)body.Length), body);
        var bytes     = Module(typeSec, funcSec, memSec, exportSec, codeSec);

        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        WasmAssert.ExportedFunction(module, "size1", [], [ValType.I64]);
        Assert.Equal(2, module.Memories.Count);
        Assert.True(module.Memories[1].Is64);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // Multi-memory memarg (index 1) load/store
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Loads and stores whose multi-memory memarg selects memory 1 (memory64) must be typed
    /// against memory 1 (i64 addresses), and the u64 memarg offset must decode.
    /// <para>
    /// The multi-memory memarg binary format sets bit 6 of the alignment byte and appends a
    /// memory-index LEB128 (<c>[0x40, memidx, offset]</c>).
    /// </para>
    /// <para>
    /// <b>Signed LEB128 caution:</b> <c>i64.const</c> uses signed LEB128. Byte <c>0x42</c> has
    /// bit 6 set → sign-extends to -62. Operand values here use values ≤ 63 with bit 6 = 0
    /// (e.g. 1, 5, 42) to ensure positive immediates.
    /// </para>
    /// </summary>
    [Fact]
    public void MultiMemory_LoadStore_Index1_Validates()
    {
        // Type section: () -> i32, () -> i64, () -> ()
        var typeSec = Section(1, EncodeU32(3),
            [0x60, 0x00, 0x01, 0x7F],   // type 0: () -> i32
            [0x60, 0x00, 0x01, 0x7E],   // type 1: () -> i64
            [0x60, 0x00, 0x00]);         // type 2: () -> ()
        var funcSec = Section(3, EncodeU32(3), [0x00, 0x01, 0x02]);

        // Two memories: index 0 = memory32(1 page), index 1 = memory64(1 page).
        var memSec = Section(5, EncodeU32(2),
            [0x00], EncodeU32(1),        // memory32, min=1
            [0x04], EncodeU64(1));       // memory64, min=1

        var exportSec = Section(7, EncodeU32(3),
            [.. EncodeName("check_mem0"),  0x00, .. EncodeU32(0)],
            [.. EncodeName("check_mem1"),  0x00, .. EncodeU32(1)],
            [.. EncodeName("trap_offset"), 0x00, .. EncodeU32(2)]);

        // For t.store, the wasm stack order is [addr (bottom), value (top)] → [].
        // Execution: pop value first, then MemEA pops address.
        // All immediate values use bits 6 = 0 to avoid signed-LEB128 sign-extension.
        //   0x05 = 5 (addr), bit 6 = 0 → +5.
        //   0x01 = 1 (val),  bit 6 = 0 → +1.
        //   Multi-memory memarg for mem1: 0x40 (flags, bit 6 set) 0x01 (memidx=1) 0x00 (offset=0).

        // Function 0: i64.store8 1 → mem1[5]; return i32.load8_u from mem0[5].
        // Correct: 0 (store routed to mem1, mem0[5] untouched).
        // Mutation: 1 (store misrouted to mem0, mem0[5]=1).
        byte[] body0 = [
            0x00,                        // no locals
            0x42, 0x05,                  // i64.const 5   (address in mem1)
            0x42, 0x01,                  // i64.const 1   (value)
            0x3C, 0x40, 0x01, 0x00,      // i64.store8 (mem1, offset=0)
            0x41, 0x05,                  // i32.const 5   (address in mem0)
            0x2D, 0x00, 0x00,            // i32.load8_u (mem0, offset=0)
            0x0B                         // end
        ];

        // Function 1: i64.store8 1 → mem1[5]; return i64.load8_u from mem1[5].
        // Correct: 1.
        byte[] body1 = [
            0x00,
            0x42, 0x05,                  // i64.const 5 (address)
            0x42, 0x01,                  // i64.const 1 (value)
            0x3C, 0x40, 0x01, 0x00,      // i64.store8 (mem1, offset=0)
            0x42, 0x05,                  // i64.const 5 (address for load)
            0x31, 0x40, 0x01, 0x00,      // i64.load8_u (mem1, offset=0)
            0x0B
        ];

        // Function 2: address=0, memarg offset=2^32 → ea=2^32 > 64 KiB → OOB trap.
        // 5-byte u64 LEB128 for 2^32 = 0x100000000: [0x80, 0x80, 0x80, 0x80, 0x10].
        byte[] body2 = [
            0x00,
            0x42, 0x00,                               // i64.const 0 (address)
            0x42, 0x01,                               // i64.const 1 (value)
            0x3C, 0x40, 0x01, 0x80, 0x80, 0x80, 0x80, 0x10,  // i64.store8 (mem1, offset=2^32)
            0x0B
        ];

        var codeSec = Section(10, EncodeU32(3),
            EncodeU32((uint)body0.Length), body0,
            EncodeU32((uint)body1.Length), body1,
            EncodeU32((uint)body2.Length), body2);

        var bytes  = Module(typeSec, funcSec, memSec, exportSec, codeSec);
        var module = ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        WasmAssert.ExportedFunction(module, "check_mem0", [], [ValType.I32]);
        WasmAssert.ExportedFunction(module, "check_mem1", [], [ValType.I64]);
        WasmAssert.ExportedFunction(module, "trap_offset", [], []);
        Assert.Equal(body2[1..], WasmAssert.ExportedFunctionBody(module, "trap_offset").Body);
    }

    // ────────────────────────────────────────────────────────────────────────────
    // memory.copy length-type four-case characterization
    // ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Spec rule for <c>memory.copy</c>: <c>n</c> is i64 only when BOTH src and dst are memory64;
    /// otherwise i32.  Expected values are written <b>explicitly</b> here and not derived from
    /// either the validator or the executor, so this test serves as an independent oracle.
    /// <para>
    /// Correct-type module validates.  Wrong-type module (other bool for n) fails
    /// <c>ReadModuleAndValidate</c> with <see cref="WasmValidationException"/>.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(false, false, false)]  // dst=m32, src=m32 → n is i32 (not i64)
    [InlineData(false, true,  false)]  // dst=m32, src=m64 → n is i32 (not i64)
    [InlineData(true,  false, false)]  // dst=m64, src=m32 → n is i32 (not i64)
    [InlineData(true,  true,  true)]   // dst=m64, src=m64 → n is i64 (not i32)
    public void MemoryCopy_LengthType_FourCases_ExplicitExpectedValues(bool dstIs64, bool srcIs64, bool nIsI64)
    {
        // Build a two-memory module.  For the same-type cases we use a single memory (index 0 == index 0).
        byte[] BuildModule(bool useCorrectNType)
        {
            bool actualNIs64 = useCorrectNType ? nIsI64 : !nIsI64;

            // Memory section: two memories, or one if both have the same type.
            byte[] memSec = (dstIs64, srcIs64) switch
            {
                (false, false) => Section(5, EncodeU32(1), [0x00], EncodeU32(1)),   // m32
                (true,  true)  => Section(5, EncodeU32(1), [0x04], EncodeU64(1)),   // m64
                (false, true)  => Section(5, EncodeU32(2),                           // m32, m64
                    [0x00], EncodeU32(1), [0x04], EncodeU64(1)),
                (true,  false) => Section(5, EncodeU32(2),                           // m64, m32
                    [0x04], EncodeU64(1), [0x00], EncodeU32(1)),
            };
            uint dstIdx = 0;
            uint srcIdx = (dstIs64 != srcIs64) ? 1u : 0u; // index 1 when types differ

            // Stack order for memory.copy: push dst addr, then src addr, then n.
            var body = new List<byte> { 0x00 }; // no locals
            // dst address
            if (dstIs64) { body.Add(0x42); body.Add(0x00); } // i64.const 0
            else         { body.Add(0x41); body.Add(0x00); } // i32.const 0
            // src address
            if (srcIs64) { body.Add(0x42); body.Add(0x00); } // i64.const 0
            else         { body.Add(0x41); body.Add(0x00); } // i32.const 0
            // n (width determined by actualNIs64)
            if (actualNIs64) { body.Add(0x42); body.Add(0x00); } // i64.const 0
            else             { body.Add(0x41); body.Add(0x00); } // i32.const 0
            // memory.copy dstIdx srcIdx: 0xFC 0x0A dstIdx srcIdx
            body.AddRange([0xFC, 0x0A, .. EncodeU32(dstIdx), .. EncodeU32(srcIdx)]);
            body.Add(0x0B); // end

            var typeSec   = Section(1, EncodeU32(1), [0x60, 0x00, 0x00]);   // () -> ()
            var funcSec   = Section(3, EncodeU32(1), [0x00]);
            var exportSec = Section(7, EncodeU32(1), [.. EncodeName("copy"), 0x00, .. EncodeU32(0)]);
            var codeSec   = Section(10, EncodeU32(1), EncodeU32((uint)body.Count), [.. body]);
            return Module(typeSec, funcSec, memSec, exportSec, codeSec);
        }

        // Correct-type module must validate.
        var bytes  = BuildModule(useCorrectNType: true);
        ModuleReader.ReadModuleAndValidate(new MemoryStream(bytes));

        // Wrong-type module (n has the opposite signedness) must fail validation.
        var wrong = BuildModule(useCorrectNType: false);
        Assert.Throws<Microsoft.NET.WebAssembly.Validation.WasmValidationException>(
            () => ModuleReader.ReadModuleAndValidate(new MemoryStream(wrong)));
    }
}
