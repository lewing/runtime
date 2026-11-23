// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Binary;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Regression tests for memory64 / table64 (wasm64) binary parsing (GitHub #88).
///
/// These hand-assemble minimal core modules (bytes inline) so the coverage does not depend
/// on the multi-hundred-MB browser-wasm64 CoreCLR artifact that first surfaced the bug.
///
/// What wasm64 changes in the binary format and is exercised here:
///  • Limits flag bit 0x04 marks a 64-bit memory/table; min/max are u64 LEB128.
///  • load/store memarg <c>offset</c> is a u64 LEB128 (not u32) for a memory64 memory —
///    this is the site that overflowed <c>ReadU32Leb</c> on the real binary.
///  • active data segments in a memory64 module use an i64.const offset init expr.
///  • active element segments for a table64 table use an i64.const offset init expr.
/// </summary>
public class Memory64Tests
{
    private static readonly byte[] Header = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    private static byte[] EncodeU32(uint value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) next |= 0x80;
            bytes.Add(next);
        } while (value != 0);
        return [.. bytes];
    }

    private static byte[] EncodeU64(ulong value)
    {
        var bytes = new List<byte>();
        do
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) next |= 0x80;
            bytes.Add(next);
        } while (value != 0);
        return [.. bytes];
    }

    private static byte[] Section(byte id, params byte[][] parts)
    {
        var payload = parts.SelectMany(p => p).ToArray();
        return [id, .. EncodeU32((uint)payload.Length), .. payload];
    }

    private static byte[] Module(params byte[][] sections) =>
        [.. Header, .. sections.SelectMany(s => s)];

    /// <summary>
    /// A minimal but complete memory64 + table64 module that a 32-bit-only parser would reject:
    /// the i64.load memarg offset (2^32) overflows a u32 LEB, and the data/elem offsets are i64.const.
    /// </summary>
    [Fact]
    public void Wasm64_Module_ParsesWithoutOverflow()
    {
        const ulong bigOffset = 0x1_0000_0000; // 2^32 — does not fit in a u32 LEB128

        // type section: one type () -> ()
        var typeSec = Section(1, EncodeU32(1), [0x60, 0x00, 0x00]);

        // function section: one function of type 0
        var funcSec = Section(3, EncodeU32(1), [0x00]);

        // table section: one table64 (funcref, flag 0x05 = has-max + is64), min=1 max=2
        var tableSec = Section(4, EncodeU32(1), [0x70, 0x05], EncodeU64(1), EncodeU64(2));

        // memory section: one memory64 (flag 0x05 = has-max + is64), min=1 max=70000
        // (max > 65536 pages is legal for memory64 and must not be rejected)
        var memSec = Section(5, EncodeU32(1), [0x05], EncodeU64(1), EncodeU64(70000));

        // element section: one active segment for table64 with an i64.const offset, funcidx [0]
        var elemSec = Section(9, EncodeU32(1),
            [0x00, 0x42, 0x00, 0x0B],  // flag 0 (active table 0), i64.const 0, end
            EncodeU32(1), [0x00]);      // vec(funcidx) = [0]

        // code section: body = i64.const 0 ; i64.load align=3 offset=2^32 ; drop ; end
        var body = new List<byte>
        {
            0x00,                       // 0 local decls
            0x42, 0x00,                 // i64.const 0  (address)
            0x29, 0x03,                 // i64.load, align=3
        };
        body.AddRange(EncodeU64(bigOffset)); // u64 memarg offset
        body.Add(0x1A);                 // drop
        body.Add(0x0B);                 // end
        var codeSec = Section(10, EncodeU32(1), EncodeU32((uint)body.Count), [.. body]);

        // data section: one active segment (implicit memory 0) with an i64.const offset
        var dataSec = Section(11, EncodeU32(1),
            [0x00, 0x42, 0x00, 0x0B],  // flag 0 (active mem 0), i64.const 0, end
            EncodeU32(1), [0xAA]);      // 1 byte of data

        var bytes = Module(typeSec, funcSec, tableSec, memSec, elemSec, codeSec, dataSec);

        var module = ModuleReader.ReadModule(new MemoryStream(bytes));

        // memory64 limits read as u64 with Is64 set, and the >65536 page max preserved.
        Assert.Single(module.Memories);
        Assert.True(module.Memories[0].Is64);
        Assert.Equal(1UL, module.Memories[0].Limits.Min);
        Assert.Equal(70000UL, module.Memories[0].Limits.Max);

        // table64 limits read as u64 with Is64 set.
        Assert.Single(module.Tables);
        Assert.True(module.Tables[0].Is64);
        Assert.Equal(1UL, module.Tables[0].Limits.Min);
        Assert.Equal(2UL, module.Tables[0].Limits.Max);

        // Bodies decoded (implies the u64 memarg offset was accepted), data/elem present.
        Assert.Single(module.Functions);
        Assert.Single(module.DataSegments);
        Assert.Single(module.Elements);
    }

    /// <summary>
    /// A SIMD memory access (<c>v128.load</c>, 0xFD 0x00) against a memory64 memory encodes its memarg
    /// <c>offset</c> as a u64 LEB128. This exercises the SIMD sub-opcode memarg branch that reads the
    /// offset as u64 for a 64-bit memory — a different decode site than the scalar <c>i64.load</c> path.
    /// A 32-bit-only decoder would overflow <c>ReadU32Leb</c> on the 2^32 offset.
    /// </summary>
    [Fact]
    public void Wasm64_SimdLoad_Memarg64_ParsesWithoutOverflow()
    {
        const ulong bigOffset = 0x1_0000_0000; // 2^32 — does not fit in a u32 LEB128

        // type section: one type () -> ()
        var typeSec = Section(1, EncodeU32(1), [0x60, 0x00, 0x00]);

        // function section: one function of type 0
        var funcSec = Section(3, EncodeU32(1), [0x00]);

        // memory section: one memory64 (flag 0x04 = is64, no max), min=1
        var memSec = Section(5, EncodeU32(1), [0x04], EncodeU64(1));

        // code section: body = i64.const 0 ; v128.load align=0 offset=2^32 ; drop ; end
        var body = new List<byte>
        {
            0x00,                       // 0 local decls
            0x42, 0x00,                 // i64.const 0  (address)
            0xFD, 0x00,                 // v128.load (SIMD prefix 0xFD, sub-opcode 0x00)
            0x00,                       // align = 0
        };
        body.AddRange(EncodeU64(bigOffset)); // u64 memarg offset (memory64)
        body.Add(0x1A);                 // drop
        body.Add(0x0B);                 // end
        var codeSec = Section(10, EncodeU32(1), EncodeU32((uint)body.Count), [.. body]);

        var bytes = Module(typeSec, funcSec, memSec, codeSec);

        var module = ModuleReader.ReadModule(new MemoryStream(bytes));

        Assert.Single(module.Memories);
        Assert.True(module.Memories[0].Is64);
        // Body decoded successfully implies the u64 SIMD memarg offset was accepted.
        Assert.Single(module.Functions);
    }

    [Fact]
    public void Memory64_LimitsFlag_0x04_NoMax_IsRead()
    {
        // flag 0x04 = memory64, no max
        var memSec = Section(5, EncodeU32(1), [0x04], EncodeU64(3));
        var module = ModuleReader.ReadModule(new MemoryStream(Module(memSec)));

        Assert.Single(module.Memories);
        Assert.True(module.Memories[0].Is64);
        Assert.Equal(3UL, module.Memories[0].Limits.Min);
        Assert.Null(module.Memories[0].Limits.Max);
    }

    [Fact]
    public void Memory64_SharedPlus64_Flag_0x07_IsRead()
    {
        // flag 0x07 = has-max + shared + is64 (previously rejected as an unknown flag)
        var memSec = Section(5, EncodeU32(1), [0x07], EncodeU64(2), EncodeU64(4));
        var module = ModuleReader.ReadModule(new MemoryStream(Module(memSec)));

        Assert.Single(module.Memories);
        Assert.True(module.Memories[0].Is64);
        Assert.True(module.Memories[0].Shared);
        Assert.Equal(2UL, module.Memories[0].Limits.Min);
        Assert.Equal(4UL, module.Memories[0].Limits.Max);
    }

    [Fact]
    public void Memory32_LimitsAndMemarg_Unaffected()
    {
        // A plain 32-bit memory with a normal (u32) memarg offset still parses as before.
        var typeSec = Section(1, EncodeU32(1), [0x60, 0x00, 0x00]);
        var funcSec = Section(3, EncodeU32(1), [0x00]);
        var memSec = Section(5, EncodeU32(1), [0x01], EncodeU32(1), EncodeU32(4)); // flag 0x01: min,max

        var body = new List<byte>
        {
            0x00,             // no locals
            0x41, 0x00,       // i32.const 0 (address)
            0x28, 0x02,       // i32.load, align=2
            0x10,             // offset = 16 (u32 LEB, single byte)
            0x1A,             // drop
            0x0B,             // end
        };
        var codeSec = Section(10, EncodeU32(1), EncodeU32((uint)body.Count), [.. body]);

        var module = ModuleReader.ReadModule(new MemoryStream(Module(typeSec, funcSec, memSec, codeSec)));

        Assert.Single(module.Memories);
        Assert.False(module.Memories[0].Is64);
        Assert.Equal(1UL, module.Memories[0].Limits.Min);
        Assert.Equal(4UL, module.Memories[0].Limits.Max);
        Assert.Single(module.Functions);
    }

    /// <summary>
    /// Encodes a length-prefixed UTF-8 name (u32 LEB128 byte-length followed by the UTF-8 bytes),
    /// as used for the module/field names of an import entry.
    /// </summary>
    private static byte[] EncodeName(string name)
    {
        var utf8 = System.Text.Encoding.UTF8.GetBytes(name);
        return [.. EncodeU32((uint)utf8.Length), .. utf8];
    }

    /// <summary>
    /// Linking a wasm64 module must throw rather than silently emit a memory32 output (the limits
    /// writers only encode 32-bit flags and would drop Is64).
    /// </summary>
    [Fact]
    public void Memory64_Linking_ThrowsNotSupported()
    {
        // memory64 (flag 0x04), min = 3
        var memSec = Section(5, EncodeU32(1), [0x04], EncodeU64(3));
        var module = ModuleReader.ReadModule(new MemoryStream(Module(memSec)));

        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([module]));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    [Fact]
    public void Memory64_ImportedMemory_Linking_ThrowsNotSupported()
    {
        var importSec = Section(2, EncodeU32(1),
            EncodeName("env"), EncodeName("memory"),
            [0x02, 0x04], EncodeU64(1));
        var module = ModuleReader.ReadModule(new MemoryStream(Module(importSec)));

        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([module]));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    /// <summary>
    /// Limits decode as u64 for every address type, so an unvalidated linker input can carry a 32-bit memory whose
    /// limits do not fit in 32 bits. <c>ImportMemory</c> narrows them to u32 before the writer sees them, which
    /// silently linked a different, valid size; the linker must refuse the input instead.
    /// </summary>
    [Fact]
    public void Memory32_LimitsBeyondU32_ImportMemoryOption_ThrowsRatherThanTruncates()
    {
        var memSec = Section(5, EncodeU32(1), [0x00], EncodeU64(0x1_0000_0001));
        var module = ModuleReader.ParseModuleForAnalysis(new MemoryStream(Module(memSec)));

        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([module], new LinkerOptions { ImportMemory = true }));
        Assert.Contains("A 32-bit memory's limits must fit in 32 bits", ex.Message);
    }

    // ── Linker OPTION paths (ImportMemory / InitialMemory / ImportTable / GrowableTable) previously
    //    cast the limits to uint and rebuilt Limits WITHOUT Is64 — erasing the flag before the
    //    writer-level guards ran, so a wasm64 module silently linked as wasm32. Each must now reject.

    private static WasmModule DefinedMemory64Module()
    {
        // memory64 (flag 0x04), min = 1
        var memSec = Section(5, EncodeU32(1), [0x04], EncodeU64(1));
        return ModuleReader.ReadModule(new MemoryStream(Module(memSec)));
    }

    private static WasmModule DefinedTable64Module()
    {
        // table64 (funcref, flag 0x04 = is64 no-max), min = 1
        var tableSec = Section(4, EncodeU32(1), [0x70, 0x04], EncodeU64(1));
        return ModuleReader.ReadModule(new MemoryStream(Module(tableSec)));
    }

    [Fact]
    public void Memory64_Linking_ImportMemoryOption_ThrowsNotSupported()
    {
        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([DefinedMemory64Module()],
                new LinkerOptions { ImportMemory = true }));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    [Fact]
    public void Memory64_Linking_InitialMemoryOption_ThrowsNotSupported()
    {
        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([DefinedMemory64Module()],
                new LinkerOptions { InitialMemory = 65536 }));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    [Fact]
    public void Table64_Linking_ImportTableOption_ThrowsNotSupported()
    {
        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([DefinedTable64Module()],
                new LinkerOptions { ImportTable = true }));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    [Fact]
    public void Table64_Linking_GrowableTableOption_ThrowsNotSupported()
    {
        var ex = Assert.Throws<LinkerException>(
            () => new WasmLinker().LinkToBinary([DefinedTable64Module()],
                new LinkerOptions { GrowableTable = true }));
        Assert.Contains("wasm64", ex.Message);
        Assert.Contains("not yet supported", ex.Message);
    }

    /// <summary>
    /// A plain 32-bit memory module still links successfully through an option path — the wasm64
    /// rejection must not regress the wasm32 linker-option paths.
    /// </summary>
    [Fact]
    public void Memory32_Linking_WithOption_Unaffected()
    {
        // memory32 (flag 0x01 = has-max), min = 1, max = 4
        var memSec = Section(5, EncodeU32(1), [0x01], EncodeU32(1), EncodeU32(4));
        var module = ModuleReader.ReadModule(new MemoryStream(Module(memSec)));

        var bytes = new WasmLinker().LinkToBinary([module],
            new LinkerOptions { InitialMemory = 131072 });
        Assert.NotEmpty(bytes);
    }

    // ── Structural versions (no interpreter): dotnet/runtime runs these against its copy of the toolchain ──

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
