// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

using System.Globalization;
using System.Text;
using Microsoft.NET.WebAssembly.Binary.Linking;
using Microsoft.NET.WebAssembly.Binary.Writing;

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
        var (size, write) = Prepare(members, symbolTable);
        byte[] archive = new byte[checked((int)size)];
        write(new MemoryStream(archive));
        return archive;
    }

    /// <summary>
    /// Writes <paramref name="members"/> to <paramref name="output"/> as an archive, with a symbol index unless
    /// <paramref name="symbolTable"/> is false. Every member is checked before anything is written.
    /// </summary>
    public static void Write(Stream output, IReadOnlyList<Member> members, bool symbolTable = true)
    {
        ArgumentNullException.ThrowIfNull(output);
        Prepare(members, symbolTable).Write(output);
    }

    /// <summary>
    /// Writes the archive to <paramref name="path"/>, streaming it; the file is opened (and an existing one
    /// replaced) only once every member has been checked.
    /// </summary>
    internal static void WriteFile(string path, IReadOnlyList<Member> members, bool symbolTable = true)
    {
        var (_, write) = Prepare(members, symbolTable);
        using var output = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        write(output);
    }

    // Lays the archive out (and checks every member): its exact size, and how to write it.
    private static (long Size, Action<Stream> Write) Prepare(IReadOnlyList<Member> members, bool symbolTable)
    {
        ArgumentNullException.ThrowIfNull(members);
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
        var symbolRanges = new List<Range>();
        bool hasObject = false;
        if (symbolTable)
        {
            foreach (Member member in members)
            {
                symbolRanges.Clear();
                bool isModule = TryCollectArchiveSymbols(member.Data, symbolRanges);
                hasObject |= isModule;
                foreach (Range symbol in symbolRanges)
                {
                    symbolNames.Write(member.Data.AsSpan(symbol));
                    symbolNames.WriteByte(0);
                }

                memberSymbols.Add(symbolRanges.Count);
            }

            // LLVM: "If there are no symbols, emit an empty symbol table, to satisfy Solaris tools".
            if (hasObject && symbolNames.Length == 0)
                symbolNames.Write([0, 0, 0]);
        }

        bool writeSymbolTable = symbolTable && symbolNames.Length != 0;
        int numSymbols = 0;
        foreach (int count in memberSymbols)
            numSymbols += count;
        long symbolTableSize = 4 + 4L * numSymbols + symbolNames.Length;
        int symbolPad = (int)(symbolTableSize & 1);
        symbolTableSize += symbolPad;
        long longNamesSize = longNames.Length + (longNames.Length & 1);

        long membersOffset = 8;
        if (writeSymbolTable)
            membersOffset += 60 + symbolTableSize;
        if (longNames.Length != 0)
            membersOffset += 60 + longNamesSize;
        long archiveSize = membersOffset;
        foreach (Member member in members)
            archiveSize += 60L + member.Data.Length + (member.Data.Length & 1);
        if (archiveSize > uint.MaxValue && writeSymbolTable)
            throw new NotSupportedException("archives needing a 64-bit symbol table (/SYM64/) are not supported.");

        return (archiveSize, output =>
        {
            output.Write("!<arch>\n"u8);

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

                output.Write(symbolNames.GetBuffer(), 0, (int)symbolNames.Length);
                for (int p = 0; p < symbolPad; p++)
                    output.WriteByte(0);
            }

            if (longNames.Length != 0)
            {
                output.Write(Encoding.ASCII.GetBytes("//".PadRight(48)));
                output.Write(Encoding.ASCII.GetBytes(longNamesSize.ToString(CultureInfo.InvariantCulture).PadRight(10)));
                output.Write("`\n"u8);
                output.Write(longNames.GetBuffer(), 0, (int)longNames.Length);
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
        });
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
    public static IReadOnlyList<string>? GetArchiveSymbols(ReadOnlySpan<byte> data)
    {
        var ranges = new List<Range>();
        if (!TryCollectArchiveSymbols(data, ranges))
            return null;

        var symbols = new List<string>(ranges.Count);
        foreach (Range range in ranges)
            symbols.Add(Encoding.UTF8.GetString(data[range]));
        return symbols;
    }

    // Collects each symbol name as a range of data: llvm-ar writes the name bytes as they are.
    private static bool TryCollectArchiveSymbols(ReadOnlySpan<byte> data, List<Range> symbols)
    {
        if (data.Length < 8 || !data[..8].SequenceEqual("\0asm\u0001\0\0\0"u8))
            return false;

        bool hasLinking = false, hasDylink = false;
        uint importedFunctions = 0, importedGlobals = 0, definedFunctions = 0, definedGlobals = 0;
        var exportedFunctions = new HashSet<uint>();
        int position = 8;
        while (position < data.Length)
        {
            byte id = data[position++];
            int size = checked((int)WasmLeb128.ReadU32(data, ref position));
            if (size > data.Length - position)
                throw new InvalidDataException("WebAssembly section extends past the end of the member.");
            int start = position;
            ReadOnlySpan<byte> payload = data.Slice(start, size);
            position = start + size;
            int at = 0;
            switch ((WasmSectionId)id)
            {
                case WasmSectionId.Import:
                    CountImports(payload, ref importedFunctions, ref importedGlobals);
                    break;
                case WasmSectionId.Function:
                    definedFunctions = WasmLeb128.ReadU32(payload, ref at);
                    break;
                case WasmSectionId.Global:
                    definedGlobals = WasmLeb128.ReadU32(payload, ref at);
                    break;
                case WasmSectionId.Export:
                    // Every export but a memory becomes a defined, global symbol.
                    uint exports = WasmLeb128.ReadU32(payload, ref at);
                    for (uint i = 0; i < exports; i++)
                    {
                        Range name = ReadName(payload, ref at);
                        var kind = (CoreSort)payload[at++];
                        uint index = WasmLeb128.ReadU32(payload, ref at);
                        if (kind == CoreSort.Func && index >= importedFunctions)
                            exportedFunctions.Add(index);
                        if (kind != CoreSort.Memory)
                            symbols.Add(Offset(name, start));
                    }

                    break;
                case WasmSectionId.Custom:
                    ReadOnlySpan<byte> section = payload[ReadName(payload, ref at)];
                    if (section.SequenceEqual("linking"u8))
                    {
                        hasLinking = true;
                        ReadLinkingSymbols(payload[at..], start + at, symbols);
                    }
                    else if (section.SequenceEqual("dylink"u8) || section.SequenceEqual("dylink.0"u8))
                    {
                        hasDylink = true;
                    }
                    else if (section.SequenceEqual("name"u8) && !hasLinking && !hasDylink)
                    {
                        // The name section supersedes the export symbols.
                        symbols.Clear();
                        ReadNameSymbols(payload[at..], start + at, symbols, importedFunctions, importedFunctions + definedFunctions,
                            importedGlobals, importedGlobals + definedGlobals, exportedFunctions);
                    }

                    break;
            }
        }

        return true;
    }

    private static void CountImports(ReadOnlySpan<byte> payload, ref uint functions, ref uint globals)
    {
        int position = 0;
        uint count = WasmLeb128.ReadU32(payload, ref position);
        for (uint i = 0; i < count; i++)
        {
            ReadName(payload, ref position);
            ReadName(payload, ref position);
            var kind = (CoreSort)payload[position++];
            switch (kind)
            {
                case CoreSort.Func:
                    functions++;
                    WasmLeb128.ReadU32(payload, ref position);
                    break;
                case CoreSort.Table:
                    SkipValueType(payload, ref position);
                    SkipLimits(payload, ref position);
                    break;
                case CoreSort.Memory:
                    SkipLimits(payload, ref position);
                    break;
                case CoreSort.Global:
                    globals++;
                    SkipValueType(payload, ref position);
                    position++;
                    break;
                case CoreSort.Tag:
                    position++;
                    WasmLeb128.ReadU32(payload, ref position);
                    break;
                default:
                    throw new InvalidDataException($"Unknown WebAssembly import kind 0x{(byte)kind:X2}.");
            }
        }
    }

    private static void SkipValueType(ReadOnlySpan<byte> data, ref int position)
    {
        byte type = data[position++];
        if (type is 0x63 or 0x64)
            WasmLeb128.ReadS33(data, ref position);
    }

    private static void SkipLimits(ReadOnlySpan<byte> data, ref int position)
    {
        byte flags = data[position++];
        WasmLeb128.ReadU64(data, ref position);
        if ((flags & 0x01) != 0)
            WasmLeb128.ReadU64(data, ref position);
        if ((flags & 0x08) != 0)
            WasmLeb128.ReadU32(data, ref position);
    }

    // LLVM: a named defined function is global only when exported; a named defined global is always global;
    // named data segments are local. Imported (undefined) names are never indexed.
    private static void ReadNameSymbols(ReadOnlySpan<byte> payload, int payloadOffset, List<Range> symbols,
        uint importedFunctions, uint functionCount, uint importedGlobals, uint globalCount, HashSet<uint> exportedFunctions)
    {
        int position = 0;
        while (position < payload.Length)
        {
            var type = (NameSubsectionId)payload[position++];
            int size = checked((int)WasmLeb128.ReadU32(payload, ref position));
            if (size > payload.Length - position)
                throw new InvalidDataException("WebAssembly name subsection extends past the end of its section.");
            int subStart = position;
            position = subStart + size;
            if (type is not (NameSubsectionId.Function or NameSubsectionId.Global or NameSubsectionId.DataSegment))
                continue;

            ReadOnlySpan<byte> sub = payload.Slice(subStart, size);
            int at = 0;
            var seen = new HashSet<uint>();
            uint count = WasmLeb128.ReadU32(sub, ref at);
            for (uint i = 0; i < count; i++)
            {
                uint index = WasmLeb128.ReadU32(sub, ref at);
                Range name = ReadName(sub, ref at);
                if (!seen.Add(index))
                    throw new InvalidDataException($"WebAssembly name section names {(type == NameSubsectionId.Function ? "function" : type == NameSubsectionId.Global ? "global" : "segment")} {index} more than once.");
                bool empty = name.End.Value == name.Start.Value;
                if (type == NameSubsectionId.Function)
                {
                    if (index >= functionCount || empty)
                        throw new InvalidDataException($"Invalid function name entry {index} in the WebAssembly name section.");
                    if (index >= importedFunctions && exportedFunctions.Contains(index))
                        symbols.Add(Offset(name, payloadOffset + subStart));
                }
                else if (type == NameSubsectionId.Global)
                {
                    if (index >= globalCount || empty)
                        throw new InvalidDataException($"Invalid global name entry {index} in the WebAssembly name section.");
                    if (index >= importedGlobals)
                        symbols.Add(Offset(name, payloadOffset + subStart));
                }
            }
        }
    }

    private static void ReadLinkingSymbols(ReadOnlySpan<byte> payload, int payloadOffset, List<Range> symbols)
    {
        int position = 0;
        WasmLeb128.ReadU32(payload, ref position); // version
        while (position < payload.Length)
        {
            var type = (LinkingSubsectionId)payload[position++];
            int size = checked((int)WasmLeb128.ReadU32(payload, ref position));
            if (size > payload.Length - position)
                throw new InvalidDataException("WebAssembly linking subsection extends past the end of its section.");
            int subStart = position;
            position = subStart + size;
            if (type != LinkingSubsectionId.SymbolTable)
                continue;

            ReadOnlySpan<byte> sub = payload.Slice(subStart, size);
            int at = 0;
            uint count = WasmLeb128.ReadU32(sub, ref at);
            symbols.Clear(); // LLVM: the symbol table replaces any symbols derived from the export section
            for (uint i = 0; i < count; i++)
            {
                var kind = (SymbolType)sub[at++];
                var flags = (SymbolFlags)WasmLeb128.ReadU32(sub, ref at);
                bool defined = (flags & SymbolFlags.Undefined) == 0;
                Range? name = null;
                switch (kind)
                {
                    case SymbolType.Function:
                    case SymbolType.Global:
                    case SymbolType.Tag:
                    case SymbolType.Table:
                        WasmLeb128.ReadU32(sub, ref at);
                        if (defined || (flags & SymbolFlags.ExplicitName) != 0)
                            name = ReadName(sub, ref at);
                        break;
                    case SymbolType.Data:
                        name = ReadName(sub, ref at);
                        if (defined)
                        {
                            WasmLeb128.ReadU32(sub, ref at);
                            WasmLeb128.ReadU64(sub, ref at);
                            WasmLeb128.ReadU64(sub, ref at);
                        }

                        break;
                    case SymbolType.Section:
                        WasmLeb128.ReadU32(sub, ref at);
                        break;
                    default:
                        throw new InvalidDataException($"Unknown WebAssembly symbol kind {(byte)kind}.");
                }

                if (name is { } n && defined && (flags & SymbolFlags.BindingLocal) == 0)
                    symbols.Add(Offset(n, payloadOffset + subStart));
            }

            return;
        }
    }

    // A name: its byte range within data, after the length prefix.
    private static Range ReadName(ReadOnlySpan<byte> data, ref int position)
    {
        int length = checked((int)WasmLeb128.ReadU32(data, ref position));
        if (length > data.Length - position)
            throw new InvalidDataException("WebAssembly name extends past the end of its section.");
        var name = new Range(position, position + length);
        position += length;
        return name;
    }

    private static Range Offset(Range range, int by) => new(range.Start.Value + by, range.End.Value + by);
}
