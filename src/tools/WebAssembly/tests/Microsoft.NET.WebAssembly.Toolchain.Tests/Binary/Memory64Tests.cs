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

    // ── Linking is deliberately unsupported for wasm64: reject, don't truncate.

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
    /// Linking a module that <b>imports</b> a memory64 must be rejected as well as one that defines it.
    /// </summary>
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
}
