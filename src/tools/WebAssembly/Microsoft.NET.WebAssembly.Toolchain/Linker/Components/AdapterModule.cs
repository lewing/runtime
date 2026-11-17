// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Linker.Components;

using Microsoft.NET.WebAssembly.Binary;

/// <summary>
/// Reduces a WASI adapter module (such as <c>wasi_snapshot_preview1.command.wasm</c>) to the exports a component
/// needs, as wit-component's <c>gc</c> pass does: unreachable functions and the imports only they use are removed,
/// and the adapter's <c>__stack_pointer</c> is given a stack, allocated through the main module's
/// <c>cabi_realloc</c> on first entry (when both it and the adapter's <c>allocation_state</c> global exist) or by a
/// start function backed by <c>memory.grow</c>.
/// </summary>
/// <remarks>
/// Types and globals are kept as they are, so only function indices are renumbered. Adapters have no tables in
/// use, element segments or data segments; anything else is rejected.
/// </remarks>
internal static class AdapterModule
{
    private const int PageSize = 65536;
    private const byte I32 = 0x7F;

    private sealed record FuncImport(string Module, string Name, uint Type);

    public static byte[] Minimize(byte[] adapter, IReadOnlySet<string> required, string? mainRealloc)
    {
        var sections = new List<(byte Id, byte[] Payload, string Name)>();
        var r = new CtReader(adapter) { Position = 8 };
        while (r.HasMore)
        {
            byte id = r.ReadByte();
            CtReader body = r.Sub(checked((int)r.ReadU32()));
            int start = body.Position;
            string name = id == 0 ? body.ReadName() : "";
            body.Position = id == 0 ? body.Position : start;
            var payload = adapter[body.Position..body.End];
            sections.Add((id, payload, name));
        }

        byte[]? Section(byte id) => sections.FirstOrDefault(s => s.Id == id).Payload;
        if (Section(9) is not null || Section(11) is { Length: > 1 })
            throw new NotSupportedException("adapter modules with element or data segments are not supported.");

        // ── Index spaces ──
        var types = new List<(byte[] Params, byte[] Results)>();
        if (Section(1) is { } typeSection)
        {
            var tr = new CtReader(typeSection);
            uint count = tr.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                if (tr.ReadByte() != 0x60)
                    throw new NotSupportedException("adapter modules may only define function types.");
                types.Add((ReadValTypes(tr), ReadValTypes(tr)));
            }
        }

        var funcImports = new List<FuncImport>();
        var otherImports = new List<byte[]>();
        if (Section(2) is { } importSection)
        {
            var ir = new CtReader(importSection);
            uint count = ir.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                int start = ir.Position;
                string module = ir.ReadName();
                string field = ir.ReadName();
                byte kind = ir.ReadByte();
                if (kind == 0x00)
                {
                    funcImports.Add(new FuncImport(module, field, ir.ReadU32()));
                    continue;
                }

                SkipImportDesc(ir, kind);
                otherImports.Add(ir.Slice(start));
            }
        }

        var localTypes = new List<uint>();
        if (Section(3) is { } funcSection)
        {
            var fr = new CtReader(funcSection);
            uint count = fr.ReadU32();
            for (uint i = 0; i < count; i++)
                localTypes.Add(fr.ReadU32());
        }

        var bodies = new List<byte[]>();
        if (Section(10) is { } codeSection)
        {
            var cr = new CtReader(codeSection);
            uint count = cr.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                int size = checked((int)cr.ReadU32());
                CtReader b = cr.Sub(size);
                bodies.Add(codeSection[b.Position..b.End]);
            }
        }

        var exports = new List<(string Name, byte Kind, uint Index)>();
        if (Section(7) is { } exportSection)
        {
            var er = new CtReader(exportSection);
            uint count = er.ReadU32();
            for (uint i = 0; i < count; i++)
                exports.Add((er.ReadName(), er.ReadByte(), er.ReadU32()));
        }

        foreach (string name in required)
        {
            if (!exports.Any(e => e.Name == name))
                throw new LinkerException($"adapter module does not export `{name}`.");
        }

        exports.RemoveAll(e => !required.Contains(e.Name));
        if (exports.Any(e => e.Kind is not (0x00 or 0x03)))
            throw new NotSupportedException("adapter modules may only export functions and globals.");
        if (Section(8) is not null)
            throw new NotSupportedException("adapter modules with a start function are not supported.");

        var (stackPointer, allocationState) = FindStackGlobals(sections.FirstOrDefault(s => s.Id == 0 && s.Name == "name").Payload);

        // ── Liveness ──
        int importCount = funcImports.Count;
        var live = new HashSet<uint>();
        var work = new Stack<uint>();
        void Mark(uint f)
        {
            if (live.Add(f))
                work.Push(f);
        }

        foreach (var e in exports.Where(e => e.Kind == 0x00))
            Mark(e.Index);
        while (work.Count > 0)
        {
            uint f = work.Pop();
            if (f < importCount)
                continue;
            foreach (uint callee in ReferencedFunctions(bodies[(int)(f - importCount)]))
                Mark(callee);
        }

        static bool IsMainRealloc(FuncImport i) => i.Module == "__main_module__" && i.Name is "cabi_realloc" or "canonical_abi_realloc";

        // ── New function index space ──
        // Imports (the main module's realloc, renamed, only when it exists); a realloc import the main module
        // cannot satisfy becomes a local memory.grow implementation instead.
        var map = new Dictionary<uint, uint>();
        var newImports = new List<FuncImport>();
        uint? reallocImportOld = null;
        for (int i = 0; i < importCount; i++)
        {
            if (!live.Contains((uint)i))
                continue;
            FuncImport import = funcImports[i];
            if (IsMainRealloc(import))
            {
                reallocImportOld = (uint)i;
                if (mainRealloc is null)
                    continue;
                import = import with { Name = mainRealloc };
            }

            map[(uint)i] = (uint)newImports.Count;
            newImports.Add(import);
        }

        uint? realloc = reallocImportOld is { } ri && mainRealloc is not null ? map[ri] : null;
        int reallocType = -1;
        int TypeIndex(byte[] p, byte[] res)
        {
            int index = types.FindIndex(t => t.Params.AsSpan().SequenceEqual(p) && t.Results.AsSpan().SequenceEqual(res));
            if (index < 0)
            {
                types.Add((p, res));
                index = types.Count - 1;
            }

            return index;
        }

        if (stackPointer is not null && mainRealloc is not null && realloc is null)
        {
            reallocType = TypeIndex([I32, I32, I32, I32], [I32]);
            realloc = (uint)newImports.Count;
            newImports.Add(new FuncImport("__main_module__", mainRealloc, (uint)reallocType));
        }

        // Synthesized bodies already use new indices and are not remapped.
        var newLocals = new List<(uint Type, byte[]? Body, bool Exported, bool Synthesized)>();
        var exportedFuncs = exports.Where(e => e.Kind == 0x00).Select(e => e.Index).ToHashSet();
        uint next = (uint)newImports.Count;
        for (int i = 0; i < bodies.Count; i++)
        {
            uint old = (uint)(importCount + i);
            if (!live.Contains(old))
                continue;
            map[old] = next++;
            newLocals.Add((localTypes[i], bodies[i], exportedFuncs.Contains(old), false));
        }

        // The adapter imports the main module's realloc but the main module has none: implement it locally.
        if (reallocImportOld is { } oldRealloc && mainRealloc is null)
        {
            map[oldRealloc] = next++;
            newLocals.Add(((uint)TypeIndex([I32, I32, I32, I32], [I32]), ReallocViaMemoryGrow(), false, true));
            realloc = map[oldRealloc];
        }

        bool lazy = stackPointer is not null && allocationState is not null && mainRealloc is not null;
        uint? allocateStack = null;
        if (stackPointer is not null)
        {
            if (realloc is null)
            {
                realloc = next++;
                newLocals.Add(((uint)TypeIndex([I32, I32, I32, I32], [I32]), ReallocViaMemoryGrow(), false, true));
            }

            allocateStack = next++;
            newLocals.Add(((uint)TypeIndex([], []), AllocateStack(realloc.Value, stackPointer.Value, allocationState), false, true));
        }

        // ── Encode ──
        var w = new CtWriter();
        w.Bytes([0x00, 0x61, 0x73, 0x6D, 0x01, 0x00, 0x00, 0x00]);
        w.Section(1, s =>
        {
            s.U32((uint)types.Count);
            foreach (var (p, res) in types)
            {
                s.Byte(0x60);
                s.U32((uint)p.Length);
                s.Bytes(p);
                s.U32((uint)res.Length);
                s.Bytes(res);
            }
        });
        w.Section(2, s =>
        {
            s.U32((uint)(otherImports.Count + newImports.Count));
            foreach (byte[] other in otherImports)
                s.Bytes(other);
            foreach (FuncImport import in newImports)
            {
                s.Name(import.Module);
                s.Name(import.Name);
                s.Byte(0x00);
                s.U32(import.Type);
            }
        });
        w.Section(3, s =>
        {
            s.U32((uint)newLocals.Count);
            foreach (var local in newLocals)
                s.U32(local.Type);
        });
        if (Section(4) is { } tables)
            w.Section(4, s => s.Bytes(tables));
        if (Section(5) is { } memories)
            w.Section(5, s => s.Bytes(memories));
        if (Section(6) is { } globals)
            w.Section(6, s => s.Bytes(globals));
        w.Section(7, s =>
        {
            s.U32((uint)exports.Count);
            foreach (var (name, kind, index) in exports)
            {
                s.Name(name);
                s.Byte(kind);
                s.U32(kind == 0x00 ? map[index] : index);
            }
        });
        if (allocateStack is { } startFunction && !lazy)
            w.Section(8, s => s.U32(startFunction));
        w.Section(10, s =>
        {
            s.U32((uint)newLocals.Count);
            foreach (var (_, body, exported, synthesized) in newLocals)
            {
                byte[] rewritten = synthesized ? body! : RemapBody(body!, map, lazy && exported ? allocateStack : null);
                s.U32((uint)rewritten.Length);
                s.Bytes(rewritten);
            }
        });
        foreach (var custom in sections.Where(s => s.Id == 0 && s.Name == "producers"))
        {
            w.Section(0, s =>
            {
                s.Name(custom.Name);
                s.Bytes(custom.Payload);
            });
        }

        return w.ToArray();
    }

    private static byte[] ReadValTypes(CtReader r)
    {
        uint count = r.ReadU32();
        var types = new byte[count];
        for (uint i = 0; i < count; i++)
        {
            types[i] = r.ReadByte();
            if (types[i] is 0x63 or 0x64)
                throw new NotSupportedException("adapter modules with typed references are not supported.");
        }

        return types;
    }

    private static void SkipImportDesc(CtReader r, byte kind)
    {
        switch (kind)
        {
            case 0x01: r.ReadByte(); SkipLimits(r); break;
            case 0x02: SkipLimits(r); break;
            case 0x03: r.ReadByte(); r.ReadByte(); break;
            case 0x04: r.ReadByte(); r.ReadU32(); break;
            default: throw new LinkerException($"unknown import kind 0x{kind:X2}.");
        }
    }

    private static void SkipLimits(CtReader r)
    {
        byte flags = r.ReadByte();
        r.ReadU32();
        if ((flags & 0x01) != 0)
            r.ReadU32();
    }

    /// <summary>Finds the mutable globals the name section calls <c>__stack_pointer</c> and <c>allocation_state</c>.</summary>
    private static (uint? StackPointer, uint? AllocationState) FindStackGlobals(byte[]? names)
    {
        uint? sp = null, state = null;
        if (names is null)
            return (sp, state);
        var r = new CtReader(names);
        while (r.HasMore)
        {
            byte id = r.ReadByte();
            CtReader sub = r.Sub(checked((int)r.ReadU32()));
            if (id != 7)
                continue;
            uint count = sub.ReadU32();
            for (uint i = 0; i < count; i++)
            {
                uint index = sub.ReadU32();
                string name = sub.ReadName();
                if (name == "__stack_pointer")
                    sp = index;
                else if (name == "allocation_state")
                    state = index;
            }
        }

        return (sp, state);
    }

    private static int SkipLocals(byte[] body)
    {
        var r = new CtReader(body);
        uint groups = r.ReadU32();
        for (uint i = 0; i < groups; i++)
        {
            r.ReadU32();
            byte type = r.ReadByte();
            if (type is 0x63 or 0x64)
                r.ReadS33();
        }

        return r.Position;
    }

    private static bool IsFunctionReference(byte opcode) => opcode is 0x10 or 0x12 or 0xD2;

    private static List<uint> ReferencedFunctions(byte[] body)
    {
        var result = new List<uint>();
        var cursor = new InstructionCursor(body.AsSpan(SkipLocals(body)), WasmFeatures.All);
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            if (!instruction.OpcodeId.IsPrefixed && IsFunctionReference(instruction.OpcodeId.Opcode))
                result.Add(instruction.Index0);
        }

        return result;
    }

    private static byte[] RemapBody(byte[] body, Dictionary<uint, uint> map, uint? prologueCall)
    {
        int codeStart = SkipLocals(body);
        var w = new CtWriter();
        w.Bytes(body.AsSpan(0, codeStart));
        if (prologueCall is { } call)
        {
            w.Byte(0x10);
            w.U32(call);
        }

        ReadOnlySpan<byte> code = body.AsSpan(codeStart);
        var cursor = new InstructionCursor(code, WasmFeatures.All);
        int copied = 0;
        while (cursor.MoveNext(out DecodedInstruction instruction))
        {
            if (instruction.OpcodeId.IsPrefixed || !IsFunctionReference(instruction.OpcodeId.Opcode))
                continue;
            w.Bytes(code[copied..instruction.ByteOffset]);
            w.Byte(instruction.OpcodeId.Opcode);
            w.U32(map[instruction.Index0]);
            copied = cursor.Position;
        }

        w.Bytes(code[copied..]);
        return w.ToArray();
    }

    // wit-component's `realloc_via_memory_grow`: only fresh page-sized allocations are supported.
    private static byte[] ReallocViaMemoryGrow()
    {
        var w = new CtWriter();
        w.U32(1);
        w.U32(1);
        w.Byte(I32);
        void AssertNotEqual(int constant, uint local)
        {
            w.Byte(0x41);
            w.S33(constant);
            w.Byte(0x20);
            w.U32(local);
            w.Byte(0x47);
            w.Bytes([0x04, 0x40, 0x00, 0x0B]);
        }

        AssertNotEqual(0, 0);
        AssertNotEqual(0, 1);
        AssertNotEqual(PageSize, 3);
        w.Bytes([0x41, 0x01, 0x40, 0x00, 0x22, 0x04]);
        w.Bytes([0x41, 0x7F, 0x46, 0x04, 0x40, 0x00, 0x0B]);
        w.Bytes([0x20, 0x04, 0x41, 0x10, 0x74, 0x0B]);
        return w.ToArray();
    }

    // wit-component's `allocate_stack_via_realloc`: one 64 KiB stack, guarded by `allocation_state` when present.
    private static byte[] AllocateStack(uint realloc, uint stackPointer, uint? allocationState)
    {
        var w = new CtWriter();
        w.U32(0);
        if (allocationState is { } state)
        {
            w.Byte(0x23);
            w.U32(state);
            w.Bytes([0x41, 0x00, 0x46, 0x04, 0x40, 0x41, 0x01, 0x24]);
            w.U32(state);
        }

        w.Bytes([0x41, 0x00, 0x41, 0x00, 0x41, 0x08, 0x41]);
        w.S33(PageSize);
        w.Byte(0x10);
        w.U32(realloc);
        w.Byte(0x41);
        w.S33(PageSize);
        w.Byte(0x6A);
        w.Byte(0x24);
        w.U32(stackPointer);
        if (allocationState is { } done)
        {
            w.Bytes([0x41, 0x02, 0x24]);
            w.U32(done);
            w.Byte(0x0B);
        }

        w.Byte(0x0B);
        return w.ToArray();
    }
}
