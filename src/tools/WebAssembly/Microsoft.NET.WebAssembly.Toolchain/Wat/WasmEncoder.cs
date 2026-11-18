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
    private string _sourceFile = "<wat>";

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
        // Magic + version
        output.Write([0x00, 0x61, 0x73, 0x6D]); // \0asm
        output.Write([0x01, 0x00, 0x00, 0x00]); // version 1

        // Section 1: Type
        if (_module.Types.Count > 0)
            WriteSection(output, WasmSectionId.Type, WriteTypeSection);

        // Section 2: Import
        if (_module.Imports.Count > 0)
            WriteSection(output, WasmSectionId.Import, WriteImportSection);

        // Section 3: Function
        if (_module.Funcs.Count > 0)
            WriteSection(output, WasmSectionId.Function, WriteFunctionSection);

        // Section 4: Table
        if (_module.Tables.Count > 0)
            WriteSection(output, WasmSectionId.Table, WriteTableSection);

        // Section 5: Memory
        if (_module.Memories.Count > 0)
            WriteSection(output, WasmSectionId.Memory, WriteMemorySection);

        // Section 13: Tag (between memory and global per exception handling spec)
        if (_module.Tags.Count > 0)
            WriteSection(output, WasmSectionId.Tag, WriteTagSection);

        // Section 6: Global
        if (_module.Globals.Count > 0)
            WriteSection(output, WasmSectionId.Global, WriteGlobalSection);

        // Section 7: Export
        if (_module.Exports.Count > 0)
            WriteSection(output, WasmSectionId.Export, WriteExportSection);

        // Section 8: Start
        if (_module.StartFunction.HasValue)
            WriteSection(output, WasmSectionId.Start, s => WriteU32Leb(s, (uint)_module.StartFunction.Value));

        // Section 9: Element
        if (_module.Elements.Count > 0)
            WriteSection(output, WasmSectionId.Element, WriteElementSection);

        // Branch metadata is canonically emitted immediately before code. Function bodies are
        // encoded once into temporary buffers so metadata offsets describe the exact bytes written.
        if (_module.Funcs.Count > 0)
        {
            IReadOnlyList<EncodedWatFunction> encodedFunctions = EncodeFunctionBodies();
            if (encodedFunctions.Any(static function => function.BranchHints.Count != 0))
            {
                WriteCustomSection(
                    output,
                    BranchHintMetadataReader.SectionName,
                    BuildBranchHintSection(encodedFunctions));
            }
            WriteSection(output, WasmSectionId.Code, s => WriteCodeSection(s, encodedFunctions));
        }

        // Section 11: Data
        if (_module.DataSegments.Count > 0)
            WriteSection(output, WasmSectionId.Data, WriteDataSection);

        // Custom section: .debug_line (emitted when source location info was collected)
        if (_debugLineEntries is { Count: > 0 })
        {
            var debugBytes = DwarfLineWriter.Emit(_debugLineEntries);
            WriteCustomSection(output, ".debug_line", debugBytes);
        }

        // Custom section: "name" — emitted when any $id or @name is present
        var nameSection = BuildNameSection();
        if (nameSection.Length > 0)
            WriteCustomSection(output, "name", nameSection);

        _debugLineEntries = null;
    }

    /// <summary>
    /// Builds the "name" custom section content (subsections 0/1/2/4 in ascending order).
    /// Returns an empty array if there are no names to emit — no-name modules encode identically to before.
    /// </summary>
    private byte[] BuildNameSection()
    {
        var ms = new MemoryStream();

        // Subsection 0: module name
        if (_module.ModuleName is { } moduleName)
        {
            var sub = new MemoryStream();
            WriteName(sub, moduleName);
            ms.WriteByte((byte)NameSubsectionId.Module);
            WriteU32Leb(ms, (uint)sub.Length);
            sub.WriteTo(ms);
        }

        // Subsection 1: function names namemap
        if (_module.FuncNameMap.Count > 0)
        {
            var sub = new MemoryStream();
            var sorted = _module.FuncNameMap.OrderBy(kv => kv.Key).ToList();
            WriteU32Leb(sub, (uint)sorted.Count);
            foreach (var (idx, name) in sorted)
            {
                WriteU32Leb(sub, (uint)idx);
                WriteName(sub, name);
            }
            ms.WriteByte((byte)NameSubsectionId.Function);
            WriteU32Leb(ms, (uint)sub.Length);
            sub.WriteTo(ms);
        }

        // Subsection 2: local names indirect-namemap
        if (_module.LocalNameMap.Count > 0)
        {
            var sub = new MemoryStream();
            var sortedFuncs = _module.LocalNameMap.OrderBy(kv => kv.Key).ToList();
            WriteU32Leb(sub, (uint)sortedFuncs.Count);
            foreach (var (funcIdx, localMap) in sortedFuncs)
            {
                WriteU32Leb(sub, (uint)funcIdx);
                var sortedLocals = localMap.OrderBy(kv => kv.Key).ToList();
                WriteU32Leb(sub, (uint)sortedLocals.Count);
                foreach (var (localIdx, name) in sortedLocals)
                {
                    WriteU32Leb(sub, (uint)localIdx);
                    WriteName(sub, name);
                }
            }
            ms.WriteByte((byte)NameSubsectionId.Local);
            WriteU32Leb(ms, (uint)sub.Length);
            sub.WriteTo(ms);
        }

        // Subsection 4: type names.
        if (_module.TypeNameMap.Count > 0)
        {
            var sub = new MemoryStream();
            var sorted = _module.TypeNameMap.OrderBy(kv => kv.Key).ToList();
            WriteU32Leb(sub, (uint)sorted.Count);
            foreach (var (idx, name) in sorted)
            {
                WriteU32Leb(sub, (uint)idx);
                WriteName(sub, name);
            }
            ms.WriteByte((byte)NameSubsectionId.Type);
            WriteU32Leb(ms, (uint)sub.Length);
            sub.WriteTo(ms);
        }

        return ms.ToArray();
    }

    private static void WriteCustomSection(Stream output, string name, byte[] content)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var sectionBody = new MemoryStream();
        WriteU32Leb(sectionBody, (uint)nameBytes.Length);
        sectionBody.Write(nameBytes);
        sectionBody.Write(content);
        var bodyBytes = sectionBody.ToArray();
        output.WriteByte((byte)WasmSectionId.Custom);
        WriteU32Leb(output, (uint)bodyBytes.Length);
        output.Write(bodyBytes);
    }

    private static void WriteSection(Stream output, WasmSectionId sectionId, Action<Stream> writer)
    {
        using var content = new MemoryStream();
        writer(content);
        output.WriteByte((byte)sectionId);
        WriteU32Leb(output, (uint)content.Length);
        output.Write(content.GetBuffer(), 0, (int)content.Length);
    }

    private void WriteTypeSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Types.Count);
        foreach (var ft in _module.Types)
        {
            s.WriteByte(0x60); // functype tag
            WriteU32Leb(s, (uint)ft.Parameters.Length);
            foreach (var p in ft.Parameters)
                s.WriteByte((byte)p);
            WriteU32Leb(s, (uint)ft.Results.Length);
            foreach (var r in ft.Results)
                s.WriteByte((byte)r);
        }
    }

    private void WriteImportSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Imports.Count);
        foreach (var imp in _module.Imports)
        {
            WriteName(s, imp.ModuleName);
            WriteName(s, imp.FieldName);
            s.WriteByte(imp.Kind switch
            {
                ImportKind.Func => 0x00,
                ImportKind.Table => 0x01,
                ImportKind.Memory => 0x02,
                ImportKind.Global => 0x03,
                ImportKind.Tag => 0x04,
                _ => throw new WatException($"Unknown import kind: {imp.Kind}"),
            });
            if (imp.Kind == ImportKind.Func)
            {
                WriteU32Leb(s, (uint)imp.TypeIndex);
            }
            else if (imp.Kind == ImportKind.Table)
            {
                WriteReferenceType(s, imp.TableElementType);
                WriteLimits(s, imp.TableMin, imp.TableMax);
            }
            else if (imp.Kind == ImportKind.Memory)
            {
                if (imp.MemMax.HasValue)
                {
                    s.WriteByte(0x01); // has max
                    WriteU32Leb(s, (uint)imp.MemMin);
                    WriteU32Leb(s, (uint)imp.MemMax.Value);
                }
                else
                {
                    s.WriteByte(0x00); // no max
                    WriteU32Leb(s, (uint)imp.MemMin);
                }
            }
            else if (imp.Kind == ImportKind.Global)
            {
                s.WriteByte((byte)imp.GlobalType);
                s.WriteByte(imp.GlobalMutable ? (byte)0x01 : (byte)0x00);
            }
            else if (imp.Kind == ImportKind.Tag)
            {
                s.WriteByte(0x00); // attribute: exception
                WriteU32Leb(s, (uint)imp.TypeIndex);
            }
        }
    }

    private void WriteFunctionSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Funcs.Count);
        foreach (var f in _module.Funcs)
            WriteU32Leb(s, (uint)f.TypeIndex);
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
        WriteU32Leb(s, (uint)_module.Tables.Count);
        foreach (var t in _module.Tables)
        {
            if (t.Init is not null)
            {
                s.WriteByte(0x40);
                s.WriteByte(0x00);
                WriteReferenceType(s, t.ElementType);
                WriteLimits(s, t.Min, t.Max);
                WriteInstruction(s, t.Init);
                s.WriteByte(0x0B); // end
            }
            else
            {
                WriteReferenceType(s, t.ElementType);
                WriteLimits(s, t.Min, t.Max);
            }
        }
    }

    private static void WriteLimits(Stream s, uint min, uint? max)
    {
        s.WriteByte(max.HasValue ? (byte)0x01 : (byte)0x00);
        WriteU32Leb(s, min);
        if (max.HasValue)
            WriteU32Leb(s, max.Value);
    }

    private static void WriteReferenceType(Stream stream, Microsoft.NET.WebAssembly.Types.ValueType.Reference type)
    {
        if (type.HeapType is AbstractHeapType abstractType)
        {
            if (!AbstractHeapTypeCodes.TryToCode(abstractType.Kind, out byte code))
                throw new WatException($"Unsupported abstract heap type {abstractType.Kind}.");
            if (type.Nullability == Nullability.Nullable)
            {
                stream.WriteByte(code);
                return;
            }

            stream.WriteByte(0x64);
            WriteS33Leb(stream, code - 0x80);
            return;
        }

        if (type.HeapType is DefinedHeapType defined)
        {
            stream.WriteByte(type.Nullability == Nullability.Nullable ? (byte)0x63 : (byte)0x64);
            WriteS33Leb(stream, defined.TypeIndex);
            return;
        }

        throw new WatException(
            $"The WAT encoder cannot serialize heap type '{type.HeapType.Key}'.");
    }

    private void WriteMemorySection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Memories.Count);
        foreach (var m in _module.Memories)
        {
            if (m.Max.HasValue)
            {
                s.WriteByte(0x01); // has max
                WriteU32Leb(s, m.Min);
                WriteU32Leb(s, m.Max.Value);
            }
            else
            {
                s.WriteByte(0x00); // no max
                WriteU32Leb(s, m.Min);
            }
        }
    }

    private void WriteTagSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Tags.Count);
        foreach (var tag in _module.Tags)
        {
            s.WriteByte(0x00); // attribute: exception
            WriteU32Leb(s, (uint)tag.TypeIndex);
        }
    }

    private void WriteGlobalSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Globals.Count);
        foreach (var g in _module.Globals)
        {
            s.WriteByte((byte)g.Type);
            s.WriteByte(g.Mutable ? (byte)0x01 : (byte)0x00);
            // Init expression
            if (g.Init != null)
                WriteInstruction(s, g.Init);
            s.WriteByte(0x0B); // end
        }
    }

    private void WriteExportSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Exports.Count);
        foreach (var e in _module.Exports)
        {
            WriteName(s, e.Name);
            s.WriteByte(e.Kind switch
            {
                ExportKind.Func => 0x00,
                ExportKind.Table => 0x01,
                ExportKind.Memory => 0x02,
                ExportKind.Global => 0x03,
                _ => throw new WatException($"Unknown export kind: {e.Kind}"),
            });
            WriteU32Leb(s, (uint)e.Index);
        }
    }

    private void WriteElementSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Elements.Count);
        for (int elementIndex = 0; elementIndex < _module.Elements.Count; elementIndex++)
        {
            var element = _module.Elements[elementIndex];
            bool explicitTable = element.HasExplicitTable || element.TableIndex != 0 ||
                (element.UsesExpressions && !IsNullableAbstractFunc(element.ElementType));
            uint flags = element.Mode switch
            {
                WatElemMode.Active when element.UsesExpressions => explicitTable ? 6u : 4u,
                WatElemMode.Passive when element.UsesExpressions => 5u,
                WatElemMode.Declarative when element.UsesExpressions => 7u,
                WatElemMode.Active => explicitTable ? 2u : 0u,
                WatElemMode.Passive => 1u,
                WatElemMode.Declarative => 3u,
                _ => throw new WatException(
                    $"Element segment {elementIndex} has unknown mode {element.Mode}."),
            };

            WriteU32Leb(s, flags);
            if (element.Mode == WatElemMode.Active)
            {
                if (element.TableIndex < 0)
                    throw new WatException($"Active element segment {elementIndex} has no table index.");
                if (explicitTable)
                    WriteU32Leb(s, (uint)element.TableIndex);
                if (element.Offset is null)
                    throw new WatException($"Active element segment {elementIndex} has no offset.");
                WriteInstruction(s, element.Offset);
                s.WriteByte(0x0B);
            }

            if (element.UsesExpressions)
            {
                if (flags != 4)
                    WriteReferenceType(s, element.ElementType);
                WriteU32Leb(s, (uint)element.ElementExpressions.Count);
                foreach (var expression in element.ElementExpressions)
                {
                    WriteInstructions(s, expression);
                    s.WriteByte(0x0B);
                }
                continue;
            }

            if (flags != 0)
                s.WriteByte(0x00);
            WriteU32Leb(s, (uint)element.FuncIndices.Count);
            foreach (int functionIndex in element.FuncIndices)
            {
                if (functionIndex < 0)
                    throw new WatException(
                        $"Element segment {elementIndex} has negative function index {functionIndex}.");
                WriteU32Leb(s, (uint)functionIndex);
            }
        }
    }

    private static bool IsNullableAbstractFunc(Microsoft.NET.WebAssembly.Types.ValueType.Reference type) =>
        type is
        {
            Nullability: Nullability.Nullable,
            HeapType: AbstractHeapType { Kind: HeapTypeKind.Func },
        };

    private sealed record EncodedWatFunction(
        byte[] Bytes,
        SourceSpan Span,
        IReadOnlyList<(uint Offset, BranchHintKind Kind)> BranchHints);

    private IReadOnlyList<EncodedWatFunction> EncodeFunctionBodies()
    {
        var encoded = new List<EncodedWatFunction>(_module.Funcs.Count);
        foreach (ResolvedFunc function in _module.Funcs)
        {
            using var body = new MemoryStream();
            WriteLocals(body, function.Locals);
            _currentBranchHints = [];
            WriteInstructions(body, function.Body);
            body.WriteByte(0x0B);
            encoded.Add(new EncodedWatFunction(
                body.ToArray(),
                function.Span,
                _currentBranchHints));
            _currentBranchHints = null;
        }
        return encoded;
    }

    private byte[] BuildBranchHintSection(IReadOnlyList<EncodedWatFunction> functions)
    {
        using var content = new MemoryStream();
        int hintedFunctionCount = functions.Count(
            static function => function.BranchHints.Count != 0);
        WriteU32Leb(content, checked((uint)hintedFunctionCount));
        for (int i = 0; i < functions.Count; i++)
        {
            IReadOnlyList<(uint Offset, BranchHintKind Kind)> hints = functions[i].BranchHints;
            if (hints.Count == 0)
                continue;

            WriteU32Leb(content, checked((uint)(_module.ImportFuncCount + i)));
            WriteU32Leb(content, checked((uint)hints.Count));
            foreach ((uint offset, BranchHintKind kind) in hints)
            {
                WriteU32Leb(content, offset);
                WriteU32Leb(content, 1);
                content.WriteByte((byte)kind);
            }
        }
        return content.ToArray();
    }

    private void WriteCodeSection(Stream s, IReadOnlyList<EncodedWatFunction> functions)
    {
        WriteU32Leb(s, (uint)functions.Count);
        foreach (EncodedWatFunction function in functions)
        {
            WriteU32Leb(s, (uint)function.Bytes.Length);

            if (_debugLineEntries != null && function.Span != SourceSpan.None)
            {
                // s.Position is now the byte offset of the first byte of this function body
                // within the code section content — used as the DWARF address.
                _debugLineEntries.Add(new DwarfLineEntry(
                    Address: (uint)s.Position,
                    FileName: _sourceFile,
                    Line: function.Span.StartLine,
                    Column: function.Span.StartCol,
                    IsStatement: true,
                    PrologueEnd: true));
            }

            s.Write(function.Bytes);
        }
    }

    private void WriteDataSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.DataSegments.Count);
        foreach (var d in _module.DataSegments)
        {
            if (d.Offset is null)
            {
                s.WriteByte(0x01); // passive segment
            }
            else
            {
                s.WriteByte(0x00); // active segment, implicit memory 0
                WriteInstruction(s, d.Offset);
                s.WriteByte(0x0B); // end of init expr
            }
            // Data bytes
            WriteU32Leb(s, (uint)d.Bytes.Length);
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
            if (_currentBranchHints is null)
                throw new WatException("@metadata.code.branch_hint annotation is only valid in a function body");
            if (instr.Mnemonic is not "if" and not "br_if")
                throw new WatException("@metadata.code.branch_hint annotation: invalid target");
            _currentBranchHints.Add((checked((uint)s.Position), branchHint));
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

    private static void WriteName(Stream s, string name)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(name);
        WriteU32Leb(s, (uint)bytes.Length);
        s.Write(bytes);
    }
}
