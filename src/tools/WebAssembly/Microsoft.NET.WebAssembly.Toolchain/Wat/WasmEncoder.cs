// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Types;

/// <summary>
/// Emits a valid WebAssembly binary module from a resolved WAT AST.
/// Section order: type(1), import(2), function(3), table(4), memory(5), global(6), export(7), element(9), code(10), data(11).
/// </summary>
public sealed class WasmEncoder
{
    private readonly ResolvedModule _module;
    private List<DwarfLineEntry>? _debugLineEntries;
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
            WriteSection(output, 1, WriteTypeSection);

        // Section 2: Import
        if (_module.Imports.Count > 0)
            WriteSection(output, 2, WriteImportSection);

        // Section 3: Function
        if (_module.Funcs.Count > 0)
            WriteSection(output, 3, WriteFunctionSection);

        // Section 4: Table
        if (_module.Tables.Count > 0)
            WriteSection(output, 4, WriteTableSection);

        // Section 5: Memory
        if (_module.Memories.Count > 0)
            WriteSection(output, 5, WriteMemorySection);

        // Section 13: Tag (between memory and global per exception handling spec)
        if (_module.Tags.Count > 0)
            WriteSection(output, 13, WriteTagSection);

        // Section 6: Global
        if (_module.Globals.Count > 0)
            WriteSection(output, 6, WriteGlobalSection);

        // Section 7: Export
        if (_module.Exports.Count > 0)
            WriteSection(output, 7, WriteExportSection);

        // Section 9: Element
        if (_module.Elements.Count > 0)
            WriteSection(output, 9, WriteElementSection);

        // Section 10: Code
        if (_module.Funcs.Count > 0)
            WriteSection(output, 10, WriteCodeSection);

        // Section 11: Data
        if (_module.DataSegments.Count > 0)
            WriteSection(output, 11, WriteDataSection);

        // Custom section: .debug_line (emitted when source location info was collected)
        if (_debugLineEntries is { Count: > 0 })
        {
            var debugBytes = DwarfLineWriter.Emit(_debugLineEntries);
            WriteCustomSection(output, ".debug_line", debugBytes);
        }

        _debugLineEntries = null;
    }

    private static void WriteCustomSection(Stream output, string name, byte[] content)
    {
        var nameBytes = System.Text.Encoding.UTF8.GetBytes(name);
        var sectionBody = new MemoryStream();
        WriteU32Leb(sectionBody, (uint)nameBytes.Length);
        sectionBody.Write(nameBytes);
        sectionBody.Write(content);
        var bodyBytes = sectionBody.ToArray();
        output.WriteByte(0); // section ID 0 = custom
        WriteU32Leb(output, (uint)bodyBytes.Length);
        output.Write(bodyBytes);
    }

    private static void WriteSection(Stream output, byte sectionId, Action<Stream> writer)
    {
        var content = new MemoryStream();
        writer(content);
        var bytes = content.ToArray();
        output.WriteByte(sectionId);
        WriteU32Leb(output, (uint)bytes.Length);
        output.Write(bytes);
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

    private void WriteTableSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Tables.Count);
        foreach (var t in _module.Tables)
        {
            s.WriteByte(0x70); // funcref element type
            if (t.Max.HasValue)
            {
                s.WriteByte(0x01); // has max
                WriteU32Leb(s, t.Min);
                WriteU32Leb(s, t.Max.Value);
            }
            else
            {
                s.WriteByte(0x00); // no max
                WriteU32Leb(s, t.Min);
            }
        }
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
        foreach (var e in _module.Elements)
        {
            s.WriteByte(0x00); // active segment, implicit table 0
            // Offset expression
            if (e.Offset != null)
            {
                WriteInstruction(s, e.Offset);
            }
            s.WriteByte(0x0B); // end of init expression
            // Function indices
            WriteU32Leb(s, (uint)e.FuncIndices.Count);
            foreach (var fi in e.FuncIndices)
                WriteU32Leb(s, (uint)fi);
        }
    }

    private void WriteCodeSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.Funcs.Count);
        foreach (var f in _module.Funcs)
        {
            var body = new MemoryStream();
            WriteLocals(body, f.Locals);
            WriteInstructions(body, f.Body);
            body.WriteByte(0x0B); // end

            var bodyBytes = body.ToArray();
            WriteU32Leb(s, (uint)bodyBytes.Length);

            if (_debugLineEntries != null && f.Span != SourceSpan.None)
            {
                // s.Position is now the byte offset of the first byte of this function body
                // within the code section content — used as the DWARF address.
                _debugLineEntries.Add(new DwarfLineEntry(
                    Address: (uint)s.Position,
                    FileName: _sourceFile,
                    Line: f.Span.StartLine,
                    Column: f.Span.StartCol,
                    IsStatement: true,
                    PrologueEnd: true));
            }

            s.Write(bodyBytes);
        }
    }

    private void WriteDataSection(Stream s)
    {
        WriteU32Leb(s, (uint)_module.DataSegments.Count);
        foreach (var d in _module.DataSegments)
        {
            // Active data segment: flags = 0, memidx implicit 0
            s.WriteByte(0x00);
            // Offset expression
            if (d.Offset != null)
                WriteInstruction(s, d.Offset);
            s.WriteByte(0x0B); // end of init expr
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
                s.WriteByte((byte)(instr.Index ?? 0x70)); // default funcref
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
                if (instr.V128Bytes is { Length: 16 } v128)
                    s.Write(v128);
                else
                    s.Write(new byte[16]);
                break;

            case ImmediateKind.SimdLane:
                s.WriteByte((byte)(instr.Index ?? 0));
                break;

            case ImmediateKind.SimdShuffle:
                // 16 lane bytes for i8x16.shuffle
                if (instr.V128Bytes is { Length: 16 } shuf)
                    s.Write(shuf);
                else
                    s.Write(new byte[16]);
                break;

            case ImmediateKind.MemArgLane:
                WriteMemArg(s, instr);
                s.WriteByte((byte)(instr.LaneIndex ?? 0));
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
            WriteS32Leb(s, instr.BlockTypeIndex.Value);
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

    internal static void WriteU32Leb(Stream s, uint value)
    {
        do
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if (value != 0) b |= 0x80;
            s.WriteByte(b);
        } while (value != 0);
    }

    internal static void WriteS32Leb(Stream s, int value)
    {
        bool more = true;
        while (more)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if ((value == 0 && (b & 0x40) == 0) ||
                (value == -1 && (b & 0x40) != 0))
                more = false;
            else
                b |= 0x80;
            s.WriteByte(b);
        }
    }

    internal static void WriteS64Leb(Stream s, long value)
    {
        bool more = true;
        while (more)
        {
            byte b = (byte)(value & 0x7F);
            value >>= 7;
            if ((value == 0 && (b & 0x40) == 0) ||
                (value == -1 && (b & 0x40) != 0))
                more = false;
            else
                b |= 0x80;
            s.WriteByte(b);
        }
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
