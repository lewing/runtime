// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Toolchain.Tests.Linker;

using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;

/// <summary>
/// Tests for ArArchiveReader: parsing various archive formats from in-memory byte arrays.
/// </summary>
public class ArArchiveReaderTests
{
    // ── Basic round-trip ────────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_SimpleArchive_ReturnsMembersWithCorrectNamesAndData()
    {
        byte[] alpha = [0x01, 0x02, 0x03];
        byte[] beta  = [0xAA, 0xBB];

        byte[] archive = BuildArchive(("alpha.o", alpha), ("beta.o", beta));

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Equal(2, members.Count);
        Assert.Equal("alpha.o", members[0].Name);
        Assert.Equal(alpha, members[0].Data);
        Assert.Equal("beta.o", members[1].Name);
        Assert.Equal(beta, members[1].Data);
    }

    // ── Empty archive ───────────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_EmptyArchive_ReturnsEmptyList()
    {
        byte[] archive = BuildArchive(); // header only, no members

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Empty(members);
    }

    // ── Alignment padding ────────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_OddSizeMember_SkipsPaddingByte()
    {
        // A single member with an odd number of bytes; the archive should pad with '\n'.
        byte[] oddData = [0xDE, 0xAD, 0xBE]; // 3 bytes (odd)

        byte[] archive = BuildArchive(("odd.o", oddData));

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Single(members);
        Assert.Equal("odd.o", members[0].Name);
        Assert.Equal(oddData, members[0].Data);
    }

    // ── GNU/SysV long names ──────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_GnuLongNames_ResolvedFromStringTable()
    {
        // Build an archive with a GNU long-name string table (//) and a member
        // whose name is referenced by offset.
        string longName = "this_is_a_very_long_member_name.o";
        byte[] payload  = [0x11, 0x22, 0x33, 0x44];

        byte[] archive = BuildArchiveWithGnuLongName(longName, payload);

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Single(members);
        Assert.Equal(longName, members[0].Name);
        Assert.Equal(payload, members[0].Data);
    }

    // ── BSD extended names ───────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_BsdExtendedNames_ParsedCorrectly()
    {
        // BSD extended name: the header name field is "#1/N" and the first N bytes
        // of the payload contain the actual name (null-padded).
        string longName = "long_bsd_name.o";
        byte[] fileData = [0xFF, 0xFE];
        byte[] archive  = BuildArchiveWithBsdExtendedName(longName, fileData);

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Single(members);
        Assert.Equal(longName, members[0].Name);
        Assert.Equal(fileData, members[0].Data);
    }

    // ── Symbol table skipping ────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_SymbolTableEntry_Skipped()
    {
        // A "/" entry (GNU symbol table) should be silently skipped.
        byte[] symTable = [0x00, 0x00]; // dummy symbol table content
        byte[] real     = [0x42];

        byte[] archive = BuildArchiveWithSymbolTable(symTable, ("real.o", real));

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Single(members);
        Assert.Equal("real.o", members[0].Name);
    }

    // ── ReadWasmModules filter ───────────────────────────────────────────────────

    [Fact]
    public void ReadWasmModules_FiltersNonWasmMembers()
    {
        // One wasm module (starts with \0asm), one text file — only the wasm survives.
        byte[] wasmBytes = BuildTinyWasmModule();
        byte[] textBytes = Encoding.ASCII.GetBytes("not wasm at all\n");

        byte[] archive = BuildArchive(("module.wasm", wasmBytes), ("readme.txt", textBytes));

        using var stream = new MemoryStream(archive);
        var modules = ArArchiveReader.ReadWasmModules(stream);

        Assert.Single(modules);
        Assert.Equal("module.wasm", modules[0].Label);
    }

    // ── Invalid magic ────────────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_InvalidMagic_ThrowsInvalidDataException()
    {
        byte[] bad = Encoding.ASCII.GetBytes("!<arch> \n"); // wrong magic (space, not \n at pos 7)
        // Actually use truly wrong magic
        bad = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07];

        using var stream = new MemoryStream(bad);

        Assert.Throws<InvalidDataException>(() => ArArchiveReader.ReadMembers(stream));
    }

    // ── Multi-member order ───────────────────────────────────────────────────────

    [Fact]
    public void ReadMembers_ThreeMembers_OrderPreserved()
    {
        byte[] a = [0x01];
        byte[] b = [0x02, 0x03];
        byte[] c = [0x04, 0x05, 0x06];

        byte[] archive = BuildArchive(("a.o", a), ("b.o", b), ("c.o", c));

        using var stream = new MemoryStream(archive);
        var members = ArArchiveReader.ReadMembers(stream);

        Assert.Equal(3, members.Count);
        Assert.Equal("a.o", members[0].Name);
        Assert.Equal("b.o", members[1].Name);
        Assert.Equal("c.o", members[2].Name);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// Builds a standard GNU ar archive with the given (name, data) members.
    /// Names must be ≤ 15 characters (short names only).
    /// </summary>
    private static byte[] BuildArchive(params (string Name, byte[] Data)[] members)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, "!<arch>\n");

        foreach (var (name, data) in members)
        {
            string headerName = name.Length <= 15 ? $"{name}/" : name;
            WriteArHeader(ms, headerName.PadRight(16), data.Length);
            ms.Write(data);
            if ((data.Length & 1) != 0)
                ms.WriteByte((byte)'\n');
        }

        return ms.ToArray();
    }

    /// <summary>Builds an archive that includes a GNU "/" symbol table entry before a real member.</summary>
    private static byte[] BuildArchiveWithSymbolTable(byte[] symTable, params (string Name, byte[] Data)[] members)
    {
        using var ms = new MemoryStream();
        WriteAscii(ms, "!<arch>\n");

        // Write GNU symbol table (name="/")
        WriteArHeader(ms, "/".PadRight(16), symTable.Length);
        ms.Write(symTable);
        if ((symTable.Length & 1) != 0)
            ms.WriteByte((byte)'\n');

        foreach (var (name, data) in members)
        {
            WriteArHeader(ms, $"{name}/".PadRight(16), data.Length);
            ms.Write(data);
            if ((data.Length & 1) != 0)
                ms.WriteByte((byte)'\n');
        }

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an archive with a GNU/SysV long-name string table ("//") and one member
    /// whose name is looked up in that table.
    /// </summary>
    private static byte[] BuildArchiveWithGnuLongName(string memberName, byte[] memberData)
    {
        // The name table format: each name is terminated by "/"  then "\n"
        string nameTableEntry = memberName + "/\n";
        byte[] nameTable = Encoding.ASCII.GetBytes(nameTableEntry);

        using var ms = new MemoryStream();
        WriteAscii(ms, "!<arch>\n");

        // Write the "//" string table
        WriteArHeader(ms, "//".PadRight(16), nameTable.Length);
        ms.Write(nameTable);
        if ((nameTable.Length & 1) != 0)
            ms.WriteByte((byte)'\n');

        // Write the member with name "/0" (offset 0 into the name table)
        WriteArHeader(ms, "/0".PadRight(16), memberData.Length);
        ms.Write(memberData);
        if ((memberData.Length & 1) != 0)
            ms.WriteByte((byte)'\n');

        return ms.ToArray();
    }

    /// <summary>
    /// Builds an archive with a BSD-style extended name member.
    /// The header name field is "#1/N" and the first N bytes of the payload hold the name.
    /// </summary>
    private static byte[] BuildArchiveWithBsdExtendedName(string memberName, byte[] fileData)
    {
        byte[] nameBytes = Encoding.UTF8.GetBytes(memberName);
        // Pad name to even length with null bytes (BSD convention)
        if ((nameBytes.Length & 1) != 0)
            nameBytes = [.. nameBytes, 0x00];

        byte[] payload = [.. nameBytes, .. fileData];
        string headerName = $"#1/{nameBytes.Length}";

        using var ms = new MemoryStream();
        WriteAscii(ms, "!<arch>\n");
        WriteArHeader(ms, headerName.PadRight(16), payload.Length);
        ms.Write(payload);
        if ((payload.Length & 1) != 0)
            ms.WriteByte((byte)'\n');

        return ms.ToArray();
    }

    private static void WriteArHeader(Stream ms, string name16, int dataSize)
    {
        WriteAscii(ms, name16);                                                  // 16 bytes
        WriteAscii(ms, "0".PadRight(12));                                        // date  12 bytes
        WriteAscii(ms, "0".PadRight(6));                                         // uid    6 bytes
        WriteAscii(ms, "0".PadRight(6));                                         // gid    6 bytes
        WriteAscii(ms, "100644".PadRight(8));                                    // mode   8 bytes
        WriteAscii(ms, dataSize.ToString(CultureInfo.InvariantCulture).PadRight(10)); // size 10 bytes
        WriteAscii(ms, "`\n");                                                   // terminator 2 bytes
    }

    private static void WriteAscii(Stream ms, string value) =>
        ms.Write(Encoding.ASCII.GetBytes(value));

    /// <summary>Builds a minimal valid wasm module binary (just magic + version).</summary>
    private static byte[] BuildTinyWasmModule()
    {
        // Use the WatCompiler to get a real valid wasm module
        return Microsoft.NET.WebAssembly.Wat.WatCompiler.Compile("(module)");
    }
}
