// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Globalization;
using System.Text;

/// <summary>
/// Writes GNU <c>ar</c> archives as <c>llvm-ar</c> does in its default deterministic mode, including the symbol
/// index (<c>s</c>) that <c>wasm-ld</c> uses to extract members lazily. The output is byte-identical to
/// <c>llvm-ar -crs</c> for WebAssembly objects and other members.
/// </summary>
/// <remarks>
/// <para>
/// Layout (LLVM <c>ArchiveWriter.cpp</c>, <c>K_GNU</c>): the <c>!&lt;arch&gt;</c> magic; the <c>/</c> symbol table
/// (a big-endian count, one big-endian member offset per symbol, then the NUL-terminated names, NUL-padded to
/// even length); the <c>//</c> long-name table when a member name is 16 bytes or longer or contains <c>/</c>; then
/// each member with a 60-byte header (mtime, uid and gid 0, mode 644) and a <c>\n</c> pad byte
/// when its size is odd.
/// </para>
/// <para>
/// The symbol table lists, per member and in symbol-table order, every defined, non-local symbol of a WebAssembly
/// object's <c>linking</c> section. It is omitted when no member is a WebAssembly module; when some member is but
/// none defines a global symbol, LLVM writes an empty table with a three-byte placeholder name area, and so does
/// this writer. Linked modules (no <c>linking</c> section) contribute the symbols LLVM's <c>WasmObjectFile</c>
/// synthesizes for them: one per non-memory export, or, when the module has a <c>name</c> section and no
/// <c>dylink</c> section, its exported defined functions and its defined globals, by their names.
/// </para>
/// </remarks>
public static class ArArchiveWriter
{
    /// <summary>The mode <c>llvm-ar</c> records in deterministic mode (<c>0644</c>), whatever the file's permissions.</summary>
    public const int DeterministicMode = 0x1A4;

    /// <summary>An archive member: its name (without directories), contents and Unix permission bits.</summary>
    public sealed record Member(string Name, byte[] Data, int Mode = DeterministicMode);

    /// <summary>A member from a file, as <c>llvm-ar</c> records it: base name and contents.</summary>
    public static Member FromFile(string path) => new(Path.GetFileName(path), File.ReadAllBytes(path));

    /// <summary>Encodes <paramref name="members"/> as an archive, with a symbol index unless <paramref name="symbolTable"/> is false.</summary>
    public static byte[] Write(IReadOnlyList<Member> members, bool symbolTable = true)
    {
        // Long names, in member order, deduplicated.
        var longNames = new MemoryStream();
        var longNameOffsets = new Dictionary<string, int>(StringComparer.Ordinal);
        var headers = new List<byte[]>(members.Count);
        foreach (Member member in members)
        {
            string name;
            if (UsesLongName(member.Name))
            {
                if (!longNameOffsets.TryGetValue(member.Name, out int offset))
                {
                    offset = (int)longNames.Length;
                    longNameOffsets[member.Name] = offset;
                    longNames.Write(Encoding.UTF8.GetBytes(member.Name + "/\n"));
                }

                name = "/" + offset.ToString(CultureInfo.InvariantCulture);
            }
            else
            {
                name = member.Name + "/";
            }

            headers.Add(Header(name, member.Mode, member.Data.Length));
        }

        // Symbols per member.
        var symbolNames = new MemoryStream();
        var memberSymbols = new List<int>(members.Count);
        bool hasObject = false;
        if (symbolTable)
        {
            foreach (Member member in members)
            {
                IReadOnlyList<string>? symbols = GetArchiveSymbols(member.Data);
                hasObject |= symbols is not null;
                int count = 0;
                foreach (string symbol in symbols ?? [])
                {
                    symbolNames.Write(Encoding.UTF8.GetBytes(symbol));
                    symbolNames.WriteByte(0);
                    count++;
                }

                memberSymbols.Add(count);
            }

            // LLVM: "If there are no symbols, emit an empty symbol table, to satisfy Solaris tools".
            if (hasObject && symbolNames.Length == 0)
                symbolNames.Write([0, 0, 0]);
        }

        var output = new MemoryStream();
        output.Write("!<arch>\n"u8);

        bool writeSymbolTable = symbolTable && symbolNames.Length != 0;
        int numSymbols = memberSymbols.Sum();
        long symbolTableSize = 4 + 4L * numSymbols + symbolNames.Length;
        int symbolPad = (int)(symbolTableSize & 1);
        symbolTableSize += symbolPad;
        long longNamesSize = longNames.Length + (longNames.Length & 1);

        long membersOffset = 8;
        if (writeSymbolTable)
            membersOffset += 60 + symbolTableSize;
        if (longNames.Length != 0)
            membersOffset += 60 + longNamesSize;
        if (membersOffset + members.Sum(m => 60L + m.Data.Length + (m.Data.Length & 1)) > uint.MaxValue && writeSymbolTable)
            throw new NotSupportedException("archives needing a 64-bit symbol table (/SYM64/) are not supported.");

        if (writeSymbolTable)
        {
            output.Write(Header("/", 0, symbolTableSize));
            WriteBigEndian(output, (uint)numSymbols);
            long position = membersOffset;
            for (int i = 0; i < members.Count; i++)
            {
                for (int s = 0; s < memberSymbols[i]; s++)
                    WriteBigEndian(output, (uint)position);
                position += headers[i].Length + members[i].Data.Length + (members[i].Data.Length & 1);
            }

            output.Write(symbolNames.ToArray());
            for (int p = 0; p < symbolPad; p++)
                output.WriteByte(0);
        }

        if (longNames.Length != 0)
        {
            output.Write(Encoding.ASCII.GetBytes("//".PadRight(48)));
            output.Write(Encoding.ASCII.GetBytes(longNamesSize.ToString(CultureInfo.InvariantCulture).PadRight(10)));
            output.Write("`\n"u8);
            output.Write(longNames.ToArray());
            if ((longNames.Length & 1) != 0)
                output.WriteByte((byte)'\n');
        }

        for (int i = 0; i < members.Count; i++)
        {
            output.Write(headers[i]);
            output.Write(members[i].Data);
            if ((members[i].Data.Length & 1) != 0)
                output.WriteByte((byte)'\n');
        }

        return output.ToArray();
    }

    private static bool UsesLongName(string name) => Encoding.UTF8.GetByteCount(name) >= 16 || name.Contains('/');

    private static byte[] Header(string name, int mode, long size)
    {
        var header = new StringBuilder(60);
        header.Append(name.PadRight(16));
        header.Append("0".PadRight(12));
        header.Append("0".PadRight(6));
        header.Append("0".PadRight(6));
        header.Append(Convert.ToString(mode, 8).PadRight(8));
        header.Append(size.ToString(CultureInfo.InvariantCulture).PadRight(10));
        header.Append("`\n");
        byte[] bytes = Encoding.UTF8.GetBytes(header.ToString());
        if (bytes.Length != 60)
            throw new ArgumentException($"Archive member header for '{name}' does not fit in 60 bytes.");
        return bytes;
    }

    private static void WriteBigEndian(Stream stream, uint value)
    {
        Span<byte> bytes = stackalloc byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        stream.Write(bytes);
    }

    /// <summary>
    /// The symbols <c>llvm-ar</c> indexes for a member, in order: the defined, non-local symbols of a WebAssembly
    /// object's <c>linking</c> section, or for a linked module the symbols LLVM synthesizes from its exports or
    /// <c>name</c> section. Returns <see langword="null"/> for a member that is not a WebAssembly module.
    /// </summary>
    /// <remarks>Mirrors LLVM's <c>WasmObjectFile</c>, which builds one symbol list section by section.</remarks>
    public static IReadOnlyList<string>? GetArchiveSymbols(byte[] data)
    {
        if (data.Length < 8 || data[0] != 0 || data[1] != (byte)'a' || data[2] != (byte)'s' || data[3] != (byte)'m'
            || data[4] != 1 || data[5] != 0 || data[6] != 0 || data[7] != 0)
            return null;

        var symbols = new List<string>();
        bool hasLinking = false, hasDylink = false;
        uint importedFunctions = 0, importedGlobals = 0, definedFunctions = 0, definedGlobals = 0;
        var exportedFunctions = new HashSet<uint>();
        int position = 8;
        while (position < data.Length)
        {
            byte id = data[position++];
            int size = checked((int)ReadU32(data, ref position));
            int end = checked(position + size);
            if (end > data.Length)
                throw new InvalidDataException("WebAssembly section extends past the end of the member.");
            int at = position;
            switch (id)
            {
                case 2:
                    CountImports(data, at, ref importedFunctions, ref importedGlobals);
                    break;
                case 3:
                    definedFunctions = ReadU32(data, ref at);
                    break;
                case 6:
                    definedGlobals = ReadU32(data, ref at);
                    break;
                case 7:
                    // Every export but a memory becomes a defined, global symbol.
                    uint exports = ReadU32(data, ref at);
                    for (uint i = 0; i < exports; i++)
                    {
                        string name = ReadName(data, ref at);
                        byte kind = data[at++];
                        uint index = ReadU32(data, ref at);
                        if (kind == 0 && index >= importedFunctions)
                            exportedFunctions.Add(index);
                        if (kind != 2)
                            symbols.Add(name);
                    }

                    break;
                case 0:
                    string section = ReadName(data, ref at);
                    if (section == "linking")
                    {
                        hasLinking = true;
                        ReadLinkingSymbols(data, at, end, symbols);
                    }
                    else if (section is "dylink" or "dylink.0")
                    {
                        hasDylink = true;
                    }
                    else if (section == "name" && !hasLinking && !hasDylink)
                    {
                        // The name section supersedes the export symbols.
                        symbols.Clear();
                        ReadNameSymbols(data, at, end, symbols, importedFunctions, importedFunctions + definedFunctions,
                            importedGlobals, importedGlobals + definedGlobals, exportedFunctions);
                    }

                    break;
            }

            position = end;
        }

        return symbols;
    }

    private static void CountImports(byte[] data, int position, ref uint functions, ref uint globals)
    {
        uint count = ReadU32(data, ref position);
        for (uint i = 0; i < count; i++)
        {
            ReadName(data, ref position);
            ReadName(data, ref position);
            byte kind = data[position++];
            switch (kind)
            {
                case 0x00:
                    functions++;
                    ReadU32(data, ref position);
                    break;
                case 0x01:
                    SkipValueType(data, ref position);
                    SkipLimits(data, ref position);
                    break;
                case 0x02:
                    SkipLimits(data, ref position);
                    break;
                case 0x03:
                    globals++;
                    SkipValueType(data, ref position);
                    position++;
                    break;
                case 0x04:
                    position++;
                    ReadU32(data, ref position);
                    break;
                default:
                    throw new InvalidDataException($"Unknown WebAssembly import kind 0x{kind:X2}.");
            }
        }
    }

    private static void SkipValueType(byte[] data, ref int position)
    {
        byte type = data[position++];
        if (type is 0x63 or 0x64)
            ReadU64(data, ref position); // heap type (s33); only its length matters here
    }

    private static void SkipLimits(byte[] data, ref int position)
    {
        byte flags = data[position++];
        ReadU64(data, ref position);
        if ((flags & 0x01) != 0)
            ReadU64(data, ref position);
        if ((flags & 0x08) != 0)
            ReadU32(data, ref position);
    }

    // LLVM: a named defined function is global only when exported; a named defined global is always global;
    // named data segments are local. Imported (undefined) names are never indexed.
    private static void ReadNameSymbols(byte[] data, int position, int end, List<string> symbols,
        uint importedFunctions, uint functionCount, uint importedGlobals, uint globalCount, HashSet<uint> exportedFunctions)
    {
        while (position < end)
        {
            byte type = data[position++];
            int size = checked((int)ReadU32(data, ref position));
            int subEnd = checked(position + size);
            if (type is not (1 or 7 or 9))
            {
                position = subEnd;
                continue;
            }

            var seen = new HashSet<uint>();
            uint count = ReadU32(data, ref position);
            for (uint i = 0; i < count; i++)
            {
                uint index = ReadU32(data, ref position);
                string name = ReadName(data, ref position);
                if (!seen.Add(index))
                    throw new InvalidDataException($"WebAssembly name section names {(type == 1 ? "function" : type == 7 ? "global" : "segment")} {index} more than once.");
                if (type == 1)
                {
                    if (index >= functionCount || name.Length == 0)
                        throw new InvalidDataException($"Invalid function name entry {index} in the WebAssembly name section.");
                    if (index >= importedFunctions && exportedFunctions.Contains(index))
                        symbols.Add(name);
                }
                else if (type == 7)
                {
                    if (index >= globalCount || name.Length == 0)
                        throw new InvalidDataException($"Invalid global name entry {index} in the WebAssembly name section.");
                    if (index >= importedGlobals)
                        symbols.Add(name);
                }
            }

            position = subEnd;
        }
    }

    private const uint SymbolBindingLocal = 0x02;
    private const uint SymbolUndefined = 0x10;
    private const uint SymbolExplicitName = 0x40;

    private static void ReadLinkingSymbols(byte[] data, int position, int end, List<string> symbols)
    {
        ReadU32(data, ref position); // version
        while (position < end)
        {
            byte type = data[position++];
            int size = checked((int)ReadU32(data, ref position));
            int subEnd = checked(position + size);
            if (type != 8)
            {
                position = subEnd;
                continue;
            }

            uint count = ReadU32(data, ref position);
            symbols.Clear(); // LLVM: the symbol table replaces any symbols derived from the export section
            for (uint i = 0; i < count; i++)
            {
                byte kind = data[position++];
                uint flags = ReadU32(data, ref position);
                bool defined = (flags & SymbolUndefined) == 0;
                string? name = null;
                switch (kind)
                {
                    case 0: // function
                    case 2: // global
                    case 4: // tag
                    case 5: // table
                        ReadU32(data, ref position);
                        if (defined || (flags & SymbolExplicitName) != 0)
                            name = ReadName(data, ref position);
                        break;
                    case 1: // data
                        name = ReadName(data, ref position);
                        if (defined)
                        {
                            ReadU32(data, ref position);
                            ReadU64(data, ref position);
                            ReadU64(data, ref position);
                        }

                        break;
                    case 3: // section
                        ReadU32(data, ref position);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown WebAssembly symbol kind {kind}.");
                }

                if (name is not null && defined && (flags & SymbolBindingLocal) == 0)
                    symbols.Add(name);
            }

            return;
        }
    }

    private static uint ReadU32(byte[] data, ref int position)
    {
        uint result = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[position++];
            result |= (uint)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            if (shift >= 28)
                throw new InvalidDataException("u32 LEB128 too long.");
        }
    }

    private static ulong ReadU64(byte[] data, ref int position)
    {
        ulong result = 0;
        for (int shift = 0; ; shift += 7)
        {
            byte b = data[position++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0)
                return result;
            if (shift >= 63)
                throw new InvalidDataException("u64 LEB128 too long.");
        }
    }

    private static string ReadName(byte[] data, ref int position)
    {
        int length = checked((int)ReadU32(data, ref position));
        string name = Encoding.UTF8.GetString(data, position, length);
        position += length;
        return name;
    }
}
