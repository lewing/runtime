// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Emits a valid WebAssembly binary module from a resolved WAT AST.
/// Section order: type(1), import(2), function(3), table(4), memory(5), global(6), export(7), start(8), element(9), code(10), data(11).
/// </summary>
public sealed class WasmEncoder
{
    private readonly ResolvedModule _module;
    private List<DwarfLineEntry>? _debugLineEntries;
    private List<(uint Offset, BranchHintKind Kind)>? _currentBranchHints;
    private bool _encodingFunctionBody;
    private long _currentBodyStart;
    private string _sourceFile = "<wat>";
    // Sections are size-prefixed, so each is built here first; one buffer is reused for every section.
    private readonly BufferWriterStream _section = new(allowInvalidModuleEntries: true);
    private MemoryStream? _expression;

    public WasmEncoder(ResolvedModule module)
    {
        _module = module;
    }

    public byte[] Encode(bool emitDebugInfo = false, string sourceFile = "<wat>")
    {
        var ms = new MemoryStream();
        Encode(ms, emitDebugInfo, sourceFile);
        return ms.ToArray();
    }

    public void Encode(Stream output, bool emitDebugInfo = false, string sourceFile = "<wat>")
    {
        if (emitDebugInfo)
        {
            _debugLineEntries = [];
            _sourceFile = sourceFile;
        }

        var writer = new WasmModuleWriter(output);

        // Section 1: Type
        if (_module.Types.Count > 0)
            WriteSection(writer, WasmSectionId.Type);

        // Section 2: Import
        if (_module.Imports.Count > 0)
            WriteSection(writer, WasmSectionId.Import);

        // Section 3: Function
        if (_module.Funcs.Count > 0)
            WriteSection(writer, WasmSectionId.Function);

        // Section 4: Table
        if (_module.Tables.Count > 0)
            WriteSection(writer, WasmSectionId.Table);

        // Section 5: Memory
        if (_module.Memories.Count > 0)
            WriteSection(writer, WasmSectionId.Memory);

        // Section 13: Tag (between memory and global per exception handling spec)
        if (_module.Tags.Count > 0)
            WriteSection(writer, WasmSectionId.Tag);

        // Section 6: Global
        if (_module.Globals.Count > 0)
            WriteSection(writer, WasmSectionId.Global);

        // Section 7: Export
        if (_module.Exports.Count > 0)
            WriteSection(writer, WasmSectionId.Export);

        // Section 8: Start
        if (_module.StartFunction.HasValue)
            WriteSection(writer, WasmSectionId.Start);

        // Section 9: Element
        if (_module.Elements.Count > 0)
            WriteSection(writer, WasmSectionId.Element);

        // Branch metadata is canonically emitted immediately before code. Function bodies are
        // encoded once, back to back into one buffer, so metadata offsets describe the exact bytes written.
        if (_module.Funcs.Count > 0)
        {
            using var bodies = new MemoryStream();
            List<EncodedWatFunction> encodedFunctions = EncodeFunctionBodies(bodies);
            if (HasBranchHints(encodedFunctions))
            {
                _section.SetLength(0);
                WriteBranchHintSection(_section, encodedFunctions);
                _section.Position = 0;
                writer.WriteCustomSection(BranchHintMetadataReader.SectionNameUtf8, _section);
            }
            WriteCodeSection(writer, bodies, encodedFunctions);
        }

        // Section 11: Data
        if (_module.DataSegments.Count > 0)
            WriteSection(writer, WasmSectionId.Data);

        // Custom section: .debug_line (emitted when source location info was collected)
        if (_debugLineEntries is { Count: > 0 })
            writer.WriteCustomSection(".debug_line"u8, DwarfLineWriter.Emit(_debugLineEntries));

        // Custom section: "name" — emitted when any $id or @name is present
        if (_module.ModuleName is not null || _module.FuncNameMap.Count > 0 || _module.LocalNameMap.Count > 0 || _module.TypeNameMap.Count > 0)
            WriteNameSection(writer);

        _debugLineEntries = null;
    }

    /// <summary>
    /// The "name" custom section: subsections 0/1/2/4 in ascending order, each present only when it has names. It is
    /// sized first and then streamed to the output.
    /// </summary>
    private void WriteNameSection(WasmModuleWriter writer)
    {
        int[] functions = SortedKeys(_module.FuncNameMap);
        int[] types = SortedKeys(_module.TypeNameMap);
        int[] localFunctions = SortedKeys(_module.LocalNameMap);
        var locals = new int[localFunctions.Length][];
        var localSizes = new long[localFunctions.Length];

        long payload = 0;
        if (_module.ModuleName is { } moduleName)
            payload += ModuleNameWriter.GetSubsectionSize(System.Text.Encoding.UTF8.GetByteCount(moduleName));
        long functionEntries = NameMapEntriesSize(_module.FuncNameMap, functions);
        if (functions.Length > 0)
            payload += NameMapWriter.GetSubsectionSize(functions.Length, functionEntries);
        long localEntries = 0;
        for (int i = 0; i < localFunctions.Length; i++)
        {
            Dictionary<int, string> map = _module.LocalNameMap[localFunctions[i]];
            locals[i] = SortedKeys(map);
            localSizes[i] = NameMapEntriesSize(map, locals[i]);
            localEntries += IndirectNameMapWriter.GetEntrySize((uint)localFunctions[i], locals[i].Length, localSizes[i]);
        }
        if (localFunctions.Length > 0)
            payload += IndirectNameMapWriter.GetSubsectionSize(localFunctions.Length, localEntries);
        long typeEntries = NameMapEntriesSize(_module.TypeNameMap, types);
        if (types.Length > 0)
            payload += NameMapWriter.GetSubsectionSize(types.Length, typeEntries);

        writer.BeginCustomSection("name"u8, payload);
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        if (_module.ModuleName is { } name)
        {
            using var utf8 = new Utf8Scratch(name, scratch);
            ModuleNameWriter.Write(writer, utf8.Bytes);
        }

        if (functions.Length > 0)
            WriteNameMap(new NameMapWriter(writer, NameSubsectionId.Function, functions.Length, functionEntries), _module.FuncNameMap, functions);

        if (localFunctions.Length > 0)
        {
            var map = new IndirectNameMapWriter(writer, NameSubsectionId.Local, localFunctions.Length, localEntries);
            for (int i = 0; i < localFunctions.Length; i++)
            {
                Dictionary<int, string> names = _module.LocalNameMap[localFunctions[i]];
                map.BeginMap((uint)localFunctions[i], locals[i].Length, localSizes[i]);
                foreach (int index in locals[i])
                {
                    using var utf8 = new Utf8Scratch(names[index], scratch);
                    map.Write((uint)index, utf8.Bytes);
                }
            }
            map.Complete();
        }

        if (types.Length > 0)
            WriteNameMap(new NameMapWriter(writer, NameSubsectionId.Type, types.Length, typeEntries), _module.TypeNameMap, types);
        writer.EndSection();
    }

    private static long NameMapEntriesSize(Dictionary<int, string> names, int[] indices)
    {
        long size = 0;
        foreach (int index in indices)
            size += NameMapWriter.GetEntrySize((uint)index, System.Text.Encoding.UTF8.GetByteCount(names[index]));
        return size;
    }

    private static void WriteNameMap(NameMapWriter map, Dictionary<int, string> names, int[] indices)
    {
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        foreach (int index in indices)
        {
            using var utf8 = new Utf8Scratch(names[index], scratch);
            map.Write((uint)index, utf8.Bytes);
        }
        map.Complete();
    }

    private static int[] SortedKeys<T>(Dictionary<int, T> map)
    {
        var keys = new int[map.Count];
        map.Keys.CopyTo(keys, 0);
        Array.Sort(keys);
        return keys;
    }

    /// <summary>Builds a section's content in the reused buffer and writes it to the module.</summary>
    private void WriteSection(WasmModuleWriter output, WasmSectionId sectionId)
    {
        _section.SetLength(0);
        switch (sectionId)
        {
            case WasmSectionId.Type: WriteTypeSection(_section); break;
            case WasmSectionId.Import: WriteImportSection(_section); break;
            case WasmSectionId.Function: WriteFunctionSection(_section); break;
            case WasmSectionId.Table: WriteTableSection(_section); break;
            case WasmSectionId.Memory: WriteMemorySection(_section); break;
            case WasmSectionId.Tag: WriteTagSection(_section); break;
            case WasmSectionId.Global: WriteGlobalSection(_section); break;
            case WasmSectionId.Export: WriteExportSection(_section); break;
            case WasmSectionId.Start:
                WriteU32Leb(_section, (uint)(_module.StartFunction ?? throw new InvalidOperationException("The module has no start function.")));
                break;
            case WasmSectionId.Element: WriteElementSection(_section); break;
            case WasmSectionId.Data: WriteDataSection(_section); break;
            default: throw new InvalidOperationException($"Unexpected WAT section {sectionId}.");
        }
        _section.Position = 0;
        output.WriteSection(sectionId, _section);
    }

    private void WriteTypeSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Types.Count);
        foreach (var ft in _module.Types)
            entries.FuncType(ft.Parameters, ft.Results);
    }

    private void WriteImportSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Imports.Count);
        Span<byte> moduleScratch = stackalloc byte[Utf8Scratch.StackSize];
        Span<byte> nameScratch = stackalloc byte[Utf8Scratch.StackSize];
        foreach (var imp in _module.Imports)
        {
            using var module = new Utf8Scratch(imp.ModuleName, moduleScratch);
            using var name = new Utf8Scratch(imp.FieldName, nameScratch);
            switch (imp.Kind)
            {
                case ImportKind.Func:
                    entries.FunctionImport(module.Bytes, name.Bytes, (uint)imp.TypeIndex);
                    break;
                case ImportKind.Table:
                    entries.TableImport(module.Bytes, name.Bytes, GetReferenceType(imp.TableElementType), imp.TableMin, imp.TableMax);
                    break;
                case ImportKind.Memory:
                    entries.MemoryImport(module.Bytes, name.Bytes, (uint)imp.MemMin,
                        imp.MemMax.HasValue ? (uint)imp.MemMax.Value : null);
                    break;
                case ImportKind.Global:
                    entries.GlobalImport(module.Bytes, name.Bytes, imp.GlobalType, imp.GlobalMutable ? Mutability.Var : Mutability.Const);
                    break;
                case ImportKind.Tag:
                    entries.TagImport(module.Bytes, name.Bytes, (uint)imp.TypeIndex);
                    break;
                default:
                    throw new WatException($"Unknown import kind: {imp.Kind}");
            }
        }
    }

    private void WriteFunctionSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Funcs.Count);
        foreach (var f in _module.Funcs)
            entries.Function((uint)f.TypeIndex);
    }

    /// <summary>
    /// Writes the table section, using the extended encoding <c>0x40 0x00 reftype limits expr</c>
    /// for any table that carries an initializer.
    /// </summary>
    /// <remarks>
    /// The element type used to be hard-coded to <c>0x70</c> here, which silently retyped an
    /// <c>externref</c> table as <c>funcref</c>. It is written from the parsed type now, which the
    /// initializer form requires anyway: the reftype precedes the limits inside the <c>0x40</c> form,
    /// and a mistyped element would reject the module's own initializer at validation (GitHub #383).
    /// </remarks>
    private void WriteTableSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Tables.Count);
        foreach (var t in _module.Tables)
        {
            WasmValType type = GetReferenceType(t.ElementType);
            if (t.Init is not null)
                entries.Table(type, t.Min, t.Max, false, EncodeExpression(t.Init));
            else
                entries.Table(type, t.Min, t.Max);
        }
    }

    private ReadOnlySpan<byte> EncodeExpression(WatInstruction? instruction)
    {
        MemoryStream expression = _expression ??= new MemoryStream();
        expression.SetLength(0);
        if (instruction is not null)
            WriteInstruction(expression, instruction);
        expression.WriteByte(WasmOpcode.End);
        return expression.GetBuffer().AsSpan(0, (int)expression.Length);
    }

    private static WasmValType GetReferenceType(Microsoft.NET.WebAssembly.Types.ValueType.Reference type) =>
        WasmValType.TryFromModel(type, out WasmValType result)
            ? result
            : throw new WatException($"The WAT encoder cannot serialize heap type '{type.HeapType.Key}'.");

    private void WriteMemorySection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Memories.Count);
        foreach (var m in _module.Memories)
            entries.Memory(m.Min, m.Max);
    }

    private void WriteTagSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Tags.Count);
        foreach (var tag in _module.Tags)
            entries.Tag((uint)tag.TypeIndex);
    }

    private void WriteGlobalSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Globals.Count);
        foreach (var g in _module.Globals)
            entries.Global(g.Type, g.Mutable ? Mutability.Var : Mutability.Const, EncodeExpression(g.Init));
    }

    private void WriteExportSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Exports.Count);
        Span<byte> scratch = stackalloc byte[Utf8Scratch.StackSize];
        foreach (var e in _module.Exports)
        {
            using var name = new Utf8Scratch(e.Name, scratch);
            WasmExternalKind kind = e.Kind switch
            {
                ExportKind.Func => WasmExternalKind.Function,
                ExportKind.Table => WasmExternalKind.Table,
                ExportKind.Memory => WasmExternalKind.Memory,
                ExportKind.Global => WasmExternalKind.Global,
                _ => throw new WatException($"Unknown export kind: {e.Kind}"),
            };
            entries.Export(name.Bytes, kind, (uint)e.Index);
        }
    }

    private void WriteElementSection(Stream s)
    {
        WasmModuleEntryWriter entries = ((BufferWriterStream)s).Entries;
        entries.Count((uint)_module.Elements.Count);
        for (int elementIndex = 0; elementIndex < _module.Elements.Count; elementIndex++)
        {
            var element = _module.Elements[elementIndex];
            bool explicitTable = element.HasExplicitTable || element.TableIndex != 0 ||
                (element.UsesExpressions && !IsNullableAbstractFunc(element.ElementType));
            ElementSegmentKind kind = element.Mode switch
            {
                WatElemMode.Active when element.UsesExpressions => explicitTable ? ElementSegmentKind.ActiveTableExpressions : ElementSegmentKind.ActiveExpressions,
                WatElemMode.Passive when element.UsesExpressions => ElementSegmentKind.PassiveExpressions,
                WatElemMode.Declarative when element.UsesExpressions => ElementSegmentKind.DeclarativeExpressions,
                WatElemMode.Active => explicitTable ? ElementSegmentKind.ActiveTableFunctions : ElementSegmentKind.ActiveFunctions,
                WatElemMode.Passive => ElementSegmentKind.PassiveFunctions,
                WatElemMode.Declarative => ElementSegmentKind.DeclarativeFunctions,
                _ => throw new WatException(
                    $"Element segment {elementIndex} has unknown mode {element.Mode}."),
            };

            uint tableIndex = 0;
            if (element.Mode == WatElemMode.Active)
            {
                if (element.TableIndex < 0)
                    throw new WatException($"Active element segment {elementIndex} has no table index.");
                if (explicitTable)
                    tableIndex = (uint)element.TableIndex;
                if (element.Offset is null)
                    throw new WatException($"Active element segment {elementIndex} has no offset.");
            }

            if (element.UsesExpressions)
            {
                int count = element.ElementExpressions.Count;
                int[] offsets = System.Buffers.ArrayPool<int>.Shared.Rent(count + 1);
                ReadOnlyMemory<byte>[] expressions = System.Buffers.ArrayPool<ReadOnlyMemory<byte>>.Shared.Rent(count);
                try
                {
                    MemoryStream scratch = _expression ??= new MemoryStream();
                    scratch.SetLength(0);
                    if (element.Mode == WatElemMode.Active)
                    {
                        WriteInstruction(scratch, element.Offset!);
                        scratch.WriteByte(WasmOpcode.End);
                    }
                    int offsetLength = (int)scratch.Length;
                    for (int i = 0; i < count; i++)
                    {
                        offsets[i] = (int)scratch.Length;
                        WriteInstructions(scratch, element.ElementExpressions[i]);
                        scratch.WriteByte(WasmOpcode.End);
                    }
                    offsets[count] = (int)scratch.Length;
                    byte[] buffer = scratch.GetBuffer();
                    for (int i = 0; i < count; i++)
                        expressions[i] = buffer.AsMemory(offsets[i], offsets[i + 1] - offsets[i]);
                    entries.ElementSegment(kind, tableIndex, buffer.AsSpan(0, offsetLength),
                        GetReferenceType(element.ElementType), expressions.AsSpan(0, count));
                }
                finally
                {
                    System.Buffers.ArrayPool<int>.Shared.Return(offsets);
                    System.Buffers.ArrayPool<ReadOnlyMemory<byte>>.Shared.Return(expressions, clearArray: true);
                }
                continue;
            }

            foreach (int functionIndex in element.FuncIndices)
            {
                if (functionIndex < 0)
                    throw new WatException(
                        $"Element segment {elementIndex} has negative function index {functionIndex}.");
            }
            ReadOnlySpan<byte> offset = element.Mode == WatElemMode.Active ? EncodeExpression(element.Offset) : default;
            ReadOnlySpan<int> indices = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(element.FuncIndices);
            entries.ElementSegment(kind, tableIndex, offset, System.Runtime.InteropServices.MemoryMarshal.Cast<int, uint>(indices));
        }
    }

    private static bool IsNullableAbstractFunc(Microsoft.NET.WebAssembly.Types.ValueType.Reference type) =>
        type is
        {
            Nullability: Nullability.Nullable,
            HeapType: AbstractHeapType { Kind: HeapTypeKind.Func },
        };

    /// <summary>A function body in the shared bodies buffer, with its branch hints at body-relative offsets.</summary>
    private readonly record struct EncodedWatFunction(
        int Start,
        int Length,
        SourceSpan Span,
        IReadOnlyList<(uint Offset, BranchHintKind Kind)> BranchHints);

    /// <summary>Encodes every function body, back to back, into <paramref name="bodies"/>.</summary>
    private List<EncodedWatFunction> EncodeFunctionBodies(MemoryStream bodies)
    {
        var encoded = new List<EncodedWatFunction>(_module.Funcs.Count);
        foreach (ResolvedFunc function in _module.Funcs)
        {
            _currentBodyStart = bodies.Position;
            WriteLocals(bodies, function.Locals);
            _currentBranchHints = null;
            _encodingFunctionBody = true;
            try
            {
                WriteInstructions(bodies, function.Body);
            }
            finally
            {
                _encodingFunctionBody = false;
            }
            bodies.WriteByte(0x0B);
            encoded.Add(new EncodedWatFunction(
                checked((int)_currentBodyStart),
                checked((int)(bodies.Position - _currentBodyStart)),
                function.Span,
                _currentBranchHints is { } hints ? hints : Array.Empty<(uint, BranchHintKind)>()));
            _currentBranchHints = null;
        }
        return encoded;
    }

    private static bool HasBranchHints(List<EncodedWatFunction> functions)
    {
        foreach (EncodedWatFunction function in functions)
        {
            if (function.BranchHints.Count != 0)
                return true;
        }
        return false;
    }

    private void WriteBranchHintSection(Stream s, List<EncodedWatFunction> functions)
    {
        int hintedFunctionCount = 0;
        foreach (EncodedWatFunction function in functions)
        {
            if (function.BranchHints.Count != 0)
                hintedFunctionCount++;
        }

        WriteU32Leb(s, checked((uint)hintedFunctionCount));
        for (int i = 0; i < functions.Count; i++)
        {
            IReadOnlyList<(uint Offset, BranchHintKind Kind)> hints = functions[i].BranchHints;
            if (hints.Count == 0)
                continue;

            WriteU32Leb(s, checked((uint)(_module.ImportFuncCount + i)));
            WriteU32Leb(s, checked((uint)hints.Count));
            foreach ((uint offset, BranchHintKind kind) in hints)
            {
                WriteU32Leb(s, offset);
                WriteU32Leb(s, 1);
                s.WriteByte((byte)kind);
            }
        }
    }

    /// <summary>
    /// Writes the code section straight from <paramref name="bodies"/>: its size is known from the body lengths, so
    /// the bodies are not copied into a section buffer first.
    /// </summary>
    private void WriteCodeSection(WasmModuleWriter writer, MemoryStream bodies, List<EncodedWatFunction> functions)
    {
        uint count = (uint)functions.Count;
        long size = WasmLeb128.GetULeb128Size(count);
        foreach (EncodedWatFunction function in functions)
            size += WasmLeb128.GetULeb128Size((uint)function.Length) + (long)function.Length;

        writer.BeginSection(WasmSectionId.Code, size);
        Span<byte> leb = stackalloc byte[WasmLeb128.MaxLength];
        writer.Write(leb[..WasmLeb128.WriteULeb128(leb, count)]);
        // Offset within the section content, as DWARF addresses in wasm are code-section relative.
        uint offset = (uint)WasmLeb128.GetULeb128Size(count);
        ReadOnlySpan<byte> buffer = bodies.GetBuffer();
        foreach (EncodedWatFunction function in functions)
        {
            int sizeLength = WasmLeb128.WriteULeb128(leb, (uint)function.Length);
            writer.Write(leb[..sizeLength]);
            offset += (uint)sizeLength;

            if (_debugLineEntries != null && function.Span != SourceSpan.None)
            {
                _debugLineEntries.Add(new DwarfLineEntry(
                    Address: offset,
                    FileName: _sourceFile,
                    Line: function.Span.StartLine,
                    Column: function.Span.StartCol,
                    IsStatement: true,
                    PrologueEnd: true));
            }

            writer.Write(buffer.Slice(function.Start, function.Length));
            offset += (uint)function.Length;
        }
        writer.EndSection();
    }

    private void WriteDataSection(Stream s)
    {
        var buffer = (BufferWriterStream)s;
        buffer.Entries.Count((uint)_module.DataSegments.Count);
        foreach (var d in _module.DataSegments)
        {
            if (d.Offset is null)
            {
                int size = DataSegmentEncoder.GetPassiveHeaderSize(d.Bytes.Length);
                buffer.Advance(DataSegmentEncoder.EncodePassiveHeader(buffer.GetSpan(size), d.Bytes.Length));
            }
            else
            {
                ReadOnlySpan<byte> offset = EncodeExpression(d.Offset);
                int size = DataSegmentEncoder.GetActiveHeaderSize(offset, d.Bytes.Length);
                buffer.Advance(DataSegmentEncoder.EncodeActiveHeader(buffer.GetSpan(size), offset, d.Bytes.Length));
            }
            s.Write(d.Bytes);
        }
    }

    private static void WriteLocals(Stream s, List<ValType> locals)
    {
        if (locals.Count == 0)
        {
            WriteU32Leb(s, 0);
            return;
        }

        // Compress: group consecutive same-type locals
        var groups = new List<(uint Count, ValType Type)>();
        ValType current = locals[0];
        uint count = 1;
        for (int i = 1; i < locals.Count; i++)
        {
            if (locals[i] == current)
            {
                count++;
            }
            else
            {
                groups.Add((count, current));
                current = locals[i];
                count = 1;
            }
        }
        groups.Add((count, current));

        WriteU32Leb(s, (uint)groups.Count);
        foreach (var (c, t) in groups)
        {
            WriteU32Leb(s, c);
            s.WriteByte((byte)t);
        }
    }

    private void WriteInstructions(Stream s, List<WatInstruction> instrs)
    {
        foreach (var instr in instrs)
            WriteInstruction(s, instr);
    }

    private void WriteInstruction(Stream s, WatInstruction instr)
    {
        // Folded S-expressions: emit operands first (they produce values on the stack)
        foreach (var op in instr.Operands)
            WriteInstruction(s, op);

        if (instr.BranchHint is { } branchHint)
        {
            if (!_encodingFunctionBody)
                throw new WatException("@metadata.code.branch_hint annotation is only valid in a function body");
            if (instr.Mnemonic is not "if" and not "br_if")
                throw new WatException("@metadata.code.branch_hint annotation: invalid target");
            (_currentBranchHints ??= []).Add((checked((uint)(s.Position - _currentBodyStart)), branchHint));
        }

        // Special handling for block-like instructions
        if (instr.Mnemonic is "block" or "loop")
        {
            WriteBlockLikeInstruction(s, instr);
            return;
        }

        if (instr.Mnemonic == "try_table")
        {
            WriteTryTableInstruction(s, instr);
            return;
        }

        if (instr.Mnemonic == "try")
        {
            WriteLegacyTryInstruction(s, instr);
            return;
        }

        if (instr.Mnemonic == "if")
        {
            WriteIfInstruction(s, instr);
            return;
        }

        if (instr.Mnemonic is "then" or "else")
            return; // handled by if

        var entry = OpcodeTable.Get(instr.Mnemonic);

        // Write opcode byte(s)
        s.Write(entry.Opcodes);

        // Write immediates
        switch (entry.Immediate)
        {
            case ImmediateKind.I32:
                WriteS32Leb(s, (int)(instr.IntValue ?? 0));
                break;

            case ImmediateKind.I64:
                WriteS64Leb(s, instr.IntValue ?? 0);
                break;

            case ImmediateKind.F32:
                WriteF32(s, (float)(instr.FloatValue ?? 0));
                break;

            case ImmediateKind.F64:
                WriteF64(s, instr.FloatValue ?? 0);
                break;

            case ImmediateKind.LocalIdx:
            case ImmediateKind.GlobalIdx:
            case ImmediateKind.FuncIdx:
            case ImmediateKind.TypeIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                break;

            case ImmediateKind.MemArg:
                WriteMemArg(s, instr);
                break;

            case ImmediateKind.MemIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                break;

            case ImmediateKind.BrTable:
                WriteBrTable(s, instr);
                break;

            case ImmediateKind.CallIndirect:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));      // type index
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));   // table index
                break;

            case ImmediateKind.TypePair:
            case ImmediateKind.TypeTagPair:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));
                break;

            case ImmediateKind.ResumeTable:
            {
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                var handlers = instr.CatchHandlers ?? [];
                WriteU32Leb(s, (uint)handlers.Count);
                foreach (var h in handlers)
                {
                    WriteU32Leb(s, (uint)h.TagIndex);
                    WriteU32Leb(s, (uint)h.LabelIndex);
                }
                break;
            }

            case ImmediateKind.TableIdx:
            case ImmediateKind.DataIdx:
            case ImmediateKind.ElemIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                break;

            case ImmediateKind.RefType:
                WriteS33Leb(s, instr.IntValue ?? -16); // default heap type: func
                break;

            case ImmediateKind.DataMemIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));      // data segment index
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));   // memory index
                break;

            case ImmediateKind.MemCopy:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));      // dst memory index
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));   // src memory index
                break;

            case ImmediateKind.ElemTableIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));      // element segment index
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));   // table index
                break;

            case ImmediateKind.TableCopy:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));      // dst table index
                WriteU32Leb(s, (uint)(instr.IntValue ?? 0));   // src table index
                break;

            case ImmediateKind.V128:
                // 16 raw bytes for v128.const
                s.Write(RequireV128Bytes(instr));
                break;

            case ImmediateKind.SimdLane:
                s.WriteByte((byte)RequireImmediate(instr.Index, instr, "lane index"));
                break;

            case ImmediateKind.SimdShuffle:
                // 16 lane bytes for i8x16.shuffle
                s.Write(RequireV128Bytes(instr));
                break;

            case ImmediateKind.MemArgLane:
                WriteMemArg(s, instr);
                s.WriteByte((byte)RequireImmediate(instr.LaneIndex, instr, "lane index"));
                break;

            case ImmediateKind.TagIdx:
            case ImmediateKind.LabelIdx:
                WriteU32Leb(s, (uint)(instr.Index ?? 0));
                break;

            case ImmediateKind.TryTable:
                // Immediates handled by WriteTryTableInstruction
                break;
        }
    }

    /// <summary>
    /// Returns the 16 raw immediate bytes, or throws. Substituting zeros here would turn a
    /// dropped immediate into a valid-but-wrong module — exactly how issue #419 stayed hidden:
    /// the resolver lost <c>V128Bytes</c> and every <c>v128.const</c> silently became zero.
    /// Only <see cref="WatParser"/> ever sets this, so a miss is a bug, not bad input.
    /// </summary>
    private static byte[] RequireV128Bytes(WatInstruction instr) =>
        instr.V128Bytes is { Length: 16 } bytes
            ? bytes
            : throw new WatException(
                $"Internal error: '{instr.Mnemonic}' reached the encoder without its 16-byte " +
                $"immediate (got {(instr.V128Bytes is null ? "null" : instr.V128Bytes.Length + " bytes")}). " +
                "Refusing to emit zeros in its place.");

    /// <summary>
    /// Returns a required scalar immediate, or throws rather than defaulting to zero.
    /// See <see cref="RequireV128Bytes"/> for why silent defaults are not acceptable here.
    /// </summary>
    private static int RequireImmediate(int? value, WatInstruction instr, string what) =>
        value ?? throw new WatException(
            $"Internal error: '{instr.Mnemonic}' reached the encoder without its {what}. " +
            "Refusing to emit zero in its place.");

    private void WriteTryTableInstruction(Stream s, WatInstruction instr)
    {
        s.WriteByte(0x1F); // try_table opcode
        WriteBlockType(s, instr);

        // Write catch handler vector
        var handlers = instr.CatchHandlers ?? [];
        WriteU32Leb(s, (uint)handlers.Count);
        foreach (var h in handlers)
        {
            s.WriteByte(h.CatchKind);
            if (h.CatchKind is 0x00 or 0x01) // catch/catch_ref have tag index
                WriteU32Leb(s, (uint)h.TagIndex);
            WriteU32Leb(s, (uint)h.LabelIndex);
        }

        // Body
        if (instr.ThenBody != null)
            WriteInstructions(s, instr.ThenBody);

        s.WriteByte(0x0B); // end
    }

    private void WriteLegacyTryInstruction(Stream s, WatInstruction instr)
    {
        s.WriteByte(0x06); // try opcode
        WriteBlockType(s, instr);

        // Do body (try body)
        if (instr.ThenBody != null)
            WriteInstructions(s, instr.ThenBody);

        // Delegate terminates the try block instead of end
        if (instr.DelegateLabelIndex.HasValue || instr.DelegateLabelName != null)
        {
            s.WriteByte(0x18); // delegate
            WriteU32Leb(s, (uint)(instr.DelegateLabelIndex ?? 0));
            return; // no end after delegate
        }

        // Catch / catch_all clauses
        if (instr.LegacyCatchClauses != null)
        {
            foreach (var clause in instr.LegacyCatchClauses)
            {
                if (clause.IsCatchAll)
                {
                    s.WriteByte(0x19); // catch_all
                }
                else
                {
                    s.WriteByte(0x07); // catch
                    WriteU32Leb(s, (uint)clause.TagIndex);
                }
                WriteInstructions(s, clause.Body);
            }
        }

        s.WriteByte(0x0B); // end
    }

    private void WriteBlockLikeInstruction(Stream s, WatInstruction instr)
    {
        var entry = OpcodeTable.Get(instr.Mnemonic);
        s.Write(entry.Opcodes);
        WriteBlockType(s, instr);

        if (instr.ThenBody != null)
            WriteInstructions(s, instr.ThenBody);

        s.WriteByte(0x0B); // end
    }

    private void WriteIfInstruction(Stream s, WatInstruction instr)
    {
        s.WriteByte(0x04); // if opcode
        WriteBlockType(s, instr);

        // Then branch
        if (instr.ThenBody != null)
            WriteInstructions(s, instr.ThenBody);

        // Else branch
        if (instr.ElseBody != null && instr.ElseBody.Count > 0)
        {
            s.WriteByte(0x05); // else
            WriteInstructions(s, instr.ElseBody);
        }

        s.WriteByte(0x0B); // end
    }

    private static void WriteBlockType(Stream s, WatInstruction instr)
    {
        if (instr.BlockTypeIndex.HasValue)
        {
            WriteS33Leb(s, instr.BlockTypeIndex.Value);
        }
        else if (instr.BlockResultType.HasValue)
        {
            s.WriteByte((byte)instr.BlockResultType.Value);
        }
        else
        {
            s.WriteByte(0x40); // empty block type
        }
    }

    private static void WriteMemArg(Stream s, WatInstruction instr)
    {
        uint alignment;
        if (instr.IntValue.HasValue && instr.IntValue.Value >= 0)
        {
            // Explicit alignment — store as log2
            uint a = (uint)instr.IntValue.Value;
            alignment = 0;
            while (a > 1) { a >>= 1; alignment++; }
        }
        else
        {
            // Natural alignment
            alignment = OpcodeTable.NaturalAlignment(instr.Mnemonic);
        }

        uint offset = (uint)(instr.Index ?? 0);
        WriteU32Leb(s, alignment);
        WriteU32Leb(s, offset);
    }

    private static void WriteBrTable(Stream s, WatInstruction instr)
    {
        if (instr.BrTableIndices == null || instr.BrTableIndices.Count == 0)
        {
            WriteU32Leb(s, 0);
            WriteU32Leb(s, 0);
            return;
        }

        // Last entry is the default label, preceding entries are the table
        int count = instr.BrTableIndices.Count - 1;
        WriteU32Leb(s, (uint)count);
        for (int i = 0; i < count; i++)
            WriteU32Leb(s, (uint)instr.BrTableIndices[i]);
        WriteU32Leb(s, (uint)instr.BrTableIndices[^1]); // default
    }

    // ── LEB128 encoding ──

    internal static void WriteU32Leb(Stream s, uint value) => WasmLeb128.WriteULeb128(s, value);

    internal static void WriteS32Leb(Stream s, int value) => WasmLeb128.WriteSLeb128(s, value);

    internal static void WriteS64Leb(Stream s, long value) => WasmLeb128.WriteSLeb128(s, value);

    internal static void WriteS33Leb(Stream stream, long value)
    {
        if (!WasmLeb128.IsS33(value))
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value is outside the signed 33-bit range.");
        WriteS64Leb(stream, value);
    }

    private static void WriteF32(Stream s, float value)
    {
        s.Write(BitConverter.GetBytes(value));
    }

    private static void WriteF64(Stream s, double value)
    {
        s.Write(BitConverter.GetBytes(value));
    }

}
