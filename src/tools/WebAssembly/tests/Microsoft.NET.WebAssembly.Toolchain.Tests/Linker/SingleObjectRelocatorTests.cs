// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Linker;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Tests for <see cref="SingleObjectRelocator"/> — single-object self-resolution of
/// relocatable wasm object files (.wasm.o) without cross-module merging.
/// </summary>
public class SingleObjectRelocatorTests
{
    // ── Wasm binary builder helpers ──────────────────────────────────────────────

    private static readonly byte[] WasmHeader = [0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00];

    private static byte[] BuildModule(params byte[][] sections)
    {
        int total = WasmHeader.Length + sections.Sum(static s => s.Length);
        var buf = new byte[total];
        WasmHeader.CopyTo(buf, 0);
        int pos = WasmHeader.Length;
        foreach (var s in sections)
        {
            s.CopyTo(buf, pos);
            pos += s.Length;
        }
        return buf;
    }

    private static byte[] Section(byte id, byte[] payload)
    {
        byte[] sizeLeb = U32Leb(checked((uint)payload.Length));
        var buf = new byte[1 + sizeLeb.Length + payload.Length];
        buf[0] = id;
        sizeLeb.CopyTo(buf, 1);
        payload.CopyTo(buf, 1 + sizeLeb.Length);
        return buf;
    }

    private static byte[] CustomSection(string name, byte[] payload)
    {
        byte[] namePart = Name(name);
        byte[] combined = new byte[namePart.Length + payload.Length];
        namePart.CopyTo(combined, 0);
        payload.CopyTo(combined, namePart.Length);
        return Section(0, combined);
    }

    private static byte[] U32Leb(uint value)
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

    private static byte[] S32Leb(int value)
    {
        var bytes = new List<byte>();
        bool more = true;
        while (more)
        {
            byte next = (byte)(value & 0x7F);
            value >>= 7;
            bool signBit = (next & 0x40) != 0;
            more = !((value == 0 && !signBit) || (value == -1 && signBit));
            if (more) next |= 0x80;
            bytes.Add(next);
        }
        return [.. bytes];
    }

    private static byte[] Name(string value)
    {
        byte[] utf8 = Encoding.UTF8.GetBytes(value);
        return [.. U32Leb((uint)utf8.Length), .. utf8];
    }

    // 5-byte padded unsigned LEB128 (slot reserved for relocation)
    private static byte[] PaddedU32Leb(uint value) =>
    [
        (byte)((value & 0x7Fu) | 0x80u),
        (byte)(((value >> 7) & 0x7Fu) | 0x80u),
        (byte)(((value >> 14) & 0x7Fu) | 0x80u),
        (byte)(((value >> 21) & 0x7Fu) | 0x80u),
        (byte)((value >> 28) & 0x7Fu),
    ];

    // ── Module construction ──────────────────────────────────────────────────────

    /// <summary>
    /// Builds a minimal relocatable wasm object with:
    /// <list type="bullet">
    ///   <item>A mutable i32 global (index 0) — referenced by <c>R_WASM_GLOBAL_INDEX_LEB</c></item>
    ///   <item>A 4-byte data segment — referenced by <c>R_WASM_MEMORY_ADDR_LEB</c></item>
    ///   <item>A function body with padded 5-byte LEB128 slots for both</item>
    ///   <item>Linking + reloc.CODE custom sections</item>
    /// </list>
    /// Section layout (used to verify TargetSectionIndex values):
    /// <c>type(0) func(1) memory(2) global(3) code(4) data(5) linking(6) reloc.CODE(7)</c>
    /// </summary>
    private static byte[] BuildRelocatableObject()
    {
        // ── type section (section index 0) ────────────────────────────────────
        // One type: () -> ()
        byte[] typeSection = Section(1, [
            0x01,               // count = 1
            0x60, 0x00, 0x00,   // func () -> ()
        ]);

        // ── function section (section index 1) ────────────────────────────────
        byte[] functionSection = Section(3, [
            0x01, 0x00,         // one function, type index 0
        ]);

        // ── memory section (section index 2) ──────────────────────────────────
        byte[] memorySection = Section(5, [
            0x01,               // count = 1
            0x00, 0x01,         // flags=0 (no-max), min=1 page
        ]);

        // ── global section (section index 3) ──────────────────────────────────
        // One mutable i32 global initialized to 0
        byte[] globalSection = Section(6, [
            0x01,               // count = 1
            0x7F, 0x01,         // valtype i32, mutable
            0x41, 0x00, 0x0B,   // i32.const 0; end
        ]);

        // ── code section (section index 4) ────────────────────────────────────
        // Function body:
        //   locals_count = 0  (offset 2 in payload)
        //   global.get + padded LEB32 (5 bytes)   → reloc offset 4 (global.get=0x23 at offset 3)
        //   i32.const + padded LEB32 (5 bytes)    → reloc offset 10 (0x41 at offset 9)
        //   drop (0x1A)
        //   end (0x0B)
        // body = 1+1+5+1+5+1+1 = 15 bytes
        // payload: [count=1] [body_size=15] [body...]
        byte[] codePayload = [
            0x01,                           // func count = 1
            0x0F,                           // body size = 15
            0x00,                           // locals count = 0
            0x23,                           // global.get
            .. PaddedU32Leb(0),             // padded LEB slot (5 bytes): global index placeholder
            0x41,                           // i32.const
            .. PaddedU32Leb(0),             // padded LEB slot (5 bytes): memory addr placeholder
            0x1A,                           // drop
            0x0B,                           // end
        ];
        byte[] codeSection = Section(10, codePayload);

        // ── data section (section index 5) ────────────────────────────────────
        // One active segment at memory[0] offset 0, data = [0x01,0x02,0x03,0x04]
        byte[] dataSection = Section(11, [
            0x01,               // count = 1
            0x00,               // segment flags: active, memory 0
            0x41, 0x00, 0x0B,   // offset expr: i32.const 0; end
            0x04,               // data length = 4
            0x01, 0x02, 0x03, 0x04,
        ]);

        // ── linking custom section (section index 6) ──────────────────────────
        // Subsection 8 = WASM_SYMBOL_TABLE; subsection 5 = WASM_SEGMENT_INFO
        //
        // Symbol[0]: kind=2 (SYMTAB_GLOBAL), flags=0, global index=0, name="__stack_pointer"
        // Symbol[1]: kind=1 (SYMTAB_DATA), flags=0, name="my_data", segment=0, offset=0, size=4
        byte[] symTablePayload = [
            0x02,               // symbol count = 2
            // Symbol[0]: global __stack_pointer
            0x02,               // kind = SYMTAB_GLOBAL (SymbolType.Global = 2)
            0x00,               // flags = 0
            0x00,               // global index = 0
            .. Name("__stack_pointer"),
            // Symbol[1]: data my_data
            0x01,               // kind = SYMTAB_DATA (SymbolType.Data = 1, NOT 4)
            0x00,               // flags = 0
            .. Name("my_data"),
            0x00,               // segment index = 0
            0x00,               // offset within segment = 0
            0x04,               // size = 4
        ];
        byte[] symTableSubsection = SubSection(8, symTablePayload);

        byte[] segInfoPayload = [
            0x01,               // count = 1
            .. Name(".data"),   // segment name
            0x03,               // alignment = p2align 3 → 8 bytes
            0x00,               // flags = 0
        ];
        byte[] segInfoSubsection = SubSection(5, segInfoPayload);

        byte[] linkingPayload = [
            0x02,               // linking version = 2
            .. symTableSubsection,
            .. segInfoSubsection,
        ];
        byte[] linkingSection = CustomSection("linking", linkingPayload);

        // ── reloc.CODE custom section (section index 7) ───────────────────────
        // TargetSectionIndex = 4 (code section at index 4 in the module)
        // Entry[0]: R_WASM_GLOBAL_INDEX_LEB=7, offset=4, sym=0
        // Entry[1]: R_WASM_MEMORY_ADDR_LEB=3, offset=10, sym=1, addend=0
        byte[] relocCodePayload = [
            .. U32Leb(4),       // target section index = 4 (code section)
            0x02,               // entry count = 2
            // Entry 0: global index LEB reloc
            0x07,               // R_WASM_GLOBAL_INDEX_LEB
            .. U32Leb(4),       // offset in code section payload = 4
            .. U32Leb(0),       // symbol index = 0 (__stack_pointer)
            // Entry 1: memory addr LEB reloc
            0x03,               // R_WASM_MEMORY_ADDR_LEB
            .. U32Leb(10),      // offset in code section payload = 10
            .. U32Leb(1),       // symbol index = 1 (my_data)
            .. S32Leb(0),       // addend = 0
        ];
        byte[] relocCodeSection = CustomSection("reloc.CODE", relocCodePayload);

        return BuildModule(
            typeSection,
            functionSection,
            memorySection,
            globalSection,
            codeSection,
            dataSection,
            linkingSection,
            relocCodeSection);
    }

    private static byte[] SubSection(byte kind, byte[] payload)
    {
        byte[] sizeLeb = U32Leb(checked((uint)payload.Length));
        return [kind, .. sizeLeb, .. payload];
    }

    // ── Tests ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Relocate_GlobalIndexLeb_PatchesCorrectlyAndPreservesFiveByteWidth()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));

        // globalBase=0, tableBase=0 → global[0] → index 0 (identity, padded LEB stays 0x80 0x80 0x80 0x80 0x00)
        byte[] result = SingleObjectRelocator.Relocate(objBytes, module, globalBase: 0);

        // Scan the code section payload to find the patched global.get argument.
        // code section is at section index 4; find it in result bytes.
        var sectionPayloads = SingleObjectRelocator.ScanSectionPayloads(result);
        var (codePayloadStart, codePayloadLen) = sectionPayloads[4]; // code section = index 4

        // Verify padded LEB at offset 4 within code payload (after count=1 + body_size=1 + locals=1 + opcode=1)
        byte[] codePatch = result[(int)codePayloadStart..((int)codePayloadStart + (int)codePayloadLen)];
        // At payload offset 4: should be 5-byte padded LEB32 encoding of 0 = 0x80 0x80 0x80 0x80 0x00
        Assert.Equal(0x80, codePatch[4]);
        Assert.Equal(0x80, codePatch[5]);
        Assert.Equal(0x80, codePatch[6]);
        Assert.Equal(0x80, codePatch[7]);
        Assert.Equal(0x00, codePatch[8]);
    }

    [Fact]
    public void Relocate_MemoryAddrLeb_ResolvesToGlobalBasePlusOffset()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));

        const uint globalBase = 1024;
        byte[] result = SingleObjectRelocator.Relocate(objBytes, module, globalBase: globalBase);

        // Find code section payload (section index 4)
        var sectionPayloads = SingleObjectRelocator.ScanSectionPayloads(result);
        var (codePayloadStart, _) = sectionPayloads[4];
        byte[] codePatch = result.AsSpan((int)codePayloadStart).ToArray();

        // At payload offset 10: 5-byte padded LEB32 for globalBase + 0 (offset=0, addend=0) = 1024
        uint patchedAddr = ReadPaddedU32Leb(codePatch, 10);
        Assert.Equal(globalBase, patchedAddr);

        // Verify still 5 bytes (the continuation bits indicate 5-byte encoding)
        Assert.Equal(0x80, codePatch[10] & 0x80); // byte 0: continuation bit set
        Assert.Equal(0x80, codePatch[11] & 0x80); // byte 1: continuation bit set
        Assert.Equal(0x80, codePatch[12] & 0x80); // byte 2: continuation bit set
        Assert.Equal(0x80, codePatch[13] & 0x80); // byte 3: continuation bit set
        Assert.Equal(0x00, codePatch[14] & 0x80); // byte 4: no continuation bit
    }

    [Fact]
    public void Relocate_Deterministic_IdenticalInputProducesIdenticalOutput()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));

        byte[] result1 = SingleObjectRelocator.Relocate(objBytes, module, globalBase: 512);
        byte[] result2 = SingleObjectRelocator.Relocate(objBytes, module, globalBase: 512);

        Assert.Equal(result1, result2);
    }

    [Fact]
    public void Relocate_DifferentGlobalBases_ProduceDifferentAddressValues()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));

        byte[] result1024 = SingleObjectRelocator.Relocate(objBytes, module, globalBase: 1024);
        byte[] result2048 = SingleObjectRelocator.Relocate(objBytes, module, globalBase: 2048);

        // The two results should differ in the memory address patch slot
        Assert.NotEqual(result1024, result2048);
    }

    [Fact]
    public void Relocate_NoLinkingSection_ThrowsInvalidOperationException()
    {
        // Build a minimal non-relocatable wasm module (no linking section)
        var plainModuleBytes = BuildModule(
            Section(1, [0x01, 0x60, 0x00, 0x00]),   // type: () -> ()
            Section(3, [0x01, 0x00]),                 // function: type 0
            Section(10, [0x01, 0x02, 0x00, 0x0B])    // code: count=1, body=2, locals=0, end
        );
        var module = ModuleReader.ReadModule(new MemoryStream(plainModuleBytes));

        var ex = Assert.Throws<InvalidOperationException>(
            () => SingleObjectRelocator.Relocate(plainModuleBytes, module));

        Assert.Contains("linking", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Relocate_NoRelocationSections_ReturnsByteIdenticalCopy()
    {
        // A module with a linking section but no reloc.* sections — nothing to patch.
        byte[] linkingPayload = [
            0x02,   // version = 2
            // Empty symbol table subsection
            0x08,   // subsection = WASM_SYMBOL_TABLE
            0x01,   // payload length = 1
            0x00,   // symbol count = 0
        ];
        var noRelocBytes = BuildModule(
            Section(1, [0x01, 0x60, 0x00, 0x00]),
            Section(3, [0x01, 0x00]),
            Section(10, [0x01, 0x02, 0x00, 0x0B]),
            CustomSection("linking", linkingPayload)
        );
        var module = ModuleReader.ReadModule(new MemoryStream(noRelocBytes));

        byte[] result = SingleObjectRelocator.Relocate(noRelocBytes, module);

        Assert.Equal(noRelocBytes, result);
    }

    [Fact]
    public void ScanSectionPayloads_ReturnsCorrectPayloadBoundaries()
    {
        // A module with two sections: type (4 bytes payload) and function (2 bytes payload).
        byte[] typeSection = Section(1, [0x01, 0x60, 0x00, 0x00]); // 4 bytes payload
        byte[] funcSection = Section(3, [0x01, 0x00]);               // 2 bytes payload
        byte[] moduleBytes = BuildModule(typeSection, funcSection);

        var payloads = SingleObjectRelocator.ScanSectionPayloads(moduleBytes);

        Assert.Equal(2, payloads.Count);
        Assert.Equal(4, payloads[0].PayloadLength);
        Assert.Equal(2, payloads[1].PayloadLength);
        // Verify section 0 payload starts after: header(8) + sectionId(1) + sizeBytes
        // type section size = 4 → 1 byte LEB
        Assert.Equal(8 + 1 + 1, (int)payloads[0].PayloadStart);
    }

    [Fact]
    public void BuildIdentityIndexMap_AllIndicesMapToThemselves()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));
        var sectionPayloads = SingleObjectRelocator.ScanSectionPayloads(objBytes);

        var map = SingleObjectRelocator.BuildIdentityIndexMap(module, sectionPayloads);

        // global 0 → 0 (identity)
        Assert.True(map.Globals.ContainsKey(0));
        Assert.Equal<uint>(0, map.Globals[0]);

        // type 0 → 0 (identity)
        Assert.True(map.Types.ContainsKey(0));
        Assert.Equal<uint>(0, map.Types[0]);

        // function 0 → 0 (identity; no imports)
        Assert.True(map.Functions.ContainsKey(0));
        Assert.Equal<uint>(0, map.Functions[0]);
    }

    [Fact]
    public void ComputeSegmentBases_GlobalBase_SegmentPlacedAtGlobalBase()
    {
        byte[] objBytes = BuildRelocatableObject();
        var module = ModuleReader.ReadModule(new MemoryStream(objBytes));

        var bases = SingleObjectRelocator.ComputeSegmentBases(module, globalBase: 512);

        // Segment 0 at base + alignment: alignment is p2align=3 → 8 bytes.
        // 512 is already aligned to 8, so base = 512.
        Assert.True(bases.ContainsKey(0));
        Assert.Equal<uint>(512, bases[0]);
    }

    // ── Decode helpers ───────────────────────────────────────────────────────────

    private static uint ReadPaddedU32Leb(byte[] buf, int offset)
    {
        uint result = 0;
        int shift = 0;
        for (int i = 0; i < 5; i++)
        {
            byte b = buf[offset + i];
            result |= (uint)(b & 0x7F) << shift;
            shift += 7;
            if ((b & 0x80) == 0) break;
        }
        return result;
    }
}
