// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using Microsoft.NET.WebAssembly.Binary.Writing;
using ValType = Microsoft.NET.WebAssembly.Types.ValType;

// Instructions for the stubs and constant expressions the object writers emit. The encoding is the WebAssembly
// toolchain's WasmCodeWriter; this layer only maps symbol operands to relocations.
namespace ILCompiler.ObjectWriter.WasmInstructions
{
    /// <summary>Encoded instructions, with relocations relative to the first byte.</summary>
    internal readonly struct WasmEncodedInstructions
    {
        public WasmEncodedInstructions(byte[] bytes, Relocation[] relocations)
        {
            Bytes = bytes;
            Relocations = relocations;
        }

        public byte[] Bytes { get; }
        public Relocation[] Relocations { get; }

        public static WasmEncodedInstructions Encode(ReadOnlySpan<WasmExpr> instructions, bool end = false)
        {
            ArrayBufferWriter<byte> output = new();
            WasmCodeWriter writer = new(output);
            List<Relocation> relocations = new();
            foreach (WasmExpr instruction in instructions)
            {
                instruction.Emit(writer, relocations);
            }
            if (end)
            {
                writer.End();
            }
            return new WasmEncodedInstructions(output.WrittenSpan.ToArray(), relocations.ToArray());
        }

        public int Encode(Span<byte> buffer)
        {
            Bytes.CopyTo(buffer);
            return Bytes.Length;
        }

        public int EncodeRelocations(Span<Relocation> buffer, int offset = 0)
        {
            for (int i = 0; i < Relocations.Length; i++)
            {
                Relocation r = Relocations[i];
                buffer[i] = new Relocation(r.RelocType, r.Offset + offset, r.Target);
            }
            return Relocations.Length;
        }
    }

    // Represents a Wasm function body in the code section: its size, locals and instructions.
    public class WasmFunctionBody : IWasmEncodable
    {
        public readonly WasmFuncType Signature;
        private readonly WasmEncodedInstructions _encoded;

        public WasmFunctionBody(WasmFuncType signature, WasmExpr[] instructions) : this(signature, Array.Empty<WasmValueType>(), instructions)
        {
        }

        public WasmFunctionBody(WasmFuncType signature, WasmValueType[] locals, WasmExpr[] instructions)
        {
            Signature = signature;

            ArrayBufferWriter<byte> output = new();
            WasmCodeWriter writer = new(output);
            writer.Locals(MemoryMarshal.Cast<WasmValueType, ValType>(locals.AsSpan()));
            List<Relocation> relocations = new();
            foreach (WasmExpr instruction in instructions)
            {
                instruction.Emit(writer, relocations);
            }
            writer.End();

            ReadOnlySpan<byte> body = output.WrittenSpan;
            byte[] bytes = new byte[WasmLeb128.GetULeb128Size((ulong)body.Length) + body.Length];
            int prefixLength = WasmLeb128.WriteULeb128(bytes, (ulong)body.Length);
            body.CopyTo(bytes.AsSpan(prefixLength));
            for (int i = 0; i < relocations.Count; i++)
            {
                Relocation r = relocations[i];
                relocations[i] = new Relocation(r.RelocType, r.Offset + prefixLength, r.Target);
            }
            _encoded = new WasmEncodedInstructions(bytes, relocations.ToArray());
        }

        public int EncodeSize() => _encoded.Bytes.Length;
        public int Encode(Span<byte> buffer) => _encoded.Encode(buffer);
        public int EncodeRelocationCount() => _encoded.Relocations.Length;
        public int EncodeRelocations(Span<Relocation> buffer) => _encoded.EncodeRelocations(buffer);
    }

    // A group of instructions forming a complete expression ending with 'end', such as a constant expression.
    public class WasmInstructionGroup : IWasmEncodable
    {
        private readonly WasmEncodedInstructions _encoded;

        public WasmInstructionGroup(WasmExpr[] wasmExprs)
        {
            _encoded = WasmEncodedInstructions.Encode(wasmExprs, end: true);
        }

        /// <summary>The encoded expression, including its <c>end</c>.</summary>
        public ReadOnlySpan<byte> Bytes => _encoded.Bytes;

        public int EncodeSize() => _encoded.Bytes.Length;
        public int Encode(Span<byte> buffer) => _encoded.Encode(buffer);
        public int EncodeRelocationCount() => _encoded.Relocations.Length;
        public int EncodeRelocations(Span<Relocation> buffer) => _encoded.EncodeRelocations(buffer);
    }

    public abstract class WasmExpr
    {
        private protected WasmExpr(WasmOp op)
        {
            Op = op;
        }

        public WasmOp Op { get; }

        internal abstract void Emit(WasmCodeWriter writer, List<Relocation> relocations);

        private protected static void AddRelocation(List<Relocation> relocations, RelocType type, long position, ISymbolNode target) =>
            relocations.Add(new Relocation(type, checked((int)position), target));
    }

    internal sealed class WasmOpExpr : WasmExpr
    {
        public WasmOpExpr(WasmOp op) : base(op)
        {
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations) => writer.Emit(Op);
    }

    internal sealed class WasmIndexExpr : WasmExpr
    {
        private readonly uint _index;

        public WasmIndexExpr(WasmOp op, uint index) : base(op)
        {
            _index = index;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations) => writer.Index(Op, _index);
    }

    internal sealed class WasmIndexPairExpr : WasmExpr
    {
        private readonly uint _first;
        private readonly uint _second;

        public WasmIndexPairExpr(WasmOp op, uint first, uint second) : base(op)
        {
            _first = first;
            _second = second;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations) => writer.Indices(Op, _first, _second);
    }

    internal sealed class WasmConstExpr : WasmExpr
    {
        private readonly long _value;

        public WasmConstExpr(WasmOp op, long value) : base(op)
        {
            Debug.Assert(op is WasmOp.I32Const or WasmOp.I64Const);
            _value = value;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations)
        {
            if (Op == WasmOp.I32Const)
            {
                writer.I32Const(checked((int)_value));
            }
            else
            {
                writer.I64Const(_value);
            }
        }
    }

    // An instruction whose only immediate is a relocated symbol: a call or global index, or an i32.const address.
    internal sealed class WasmLEBConstantReloc : WasmExpr
    {
        private readonly ISymbolNode _symbol;
        private readonly RelocType _relocType;

        public WasmLEBConstantReloc(WasmOp op, ISymbolNode symbol, RelocType relocType) : base(op)
        {
            _symbol = symbol;
            _relocType = relocType;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations)
        {
            int width = Relocation.GetSize(_relocType);
            long position = Op == WasmOp.I32Const ? writer.I32Const(0, width) : writer.Index(Op, 0, width);
            AddRelocation(relocations, _relocType, position, _symbol);
        }
    }

    internal sealed class WasmIndirectCallInstruction : WasmExpr
    {
        private readonly ISymbolNode _type;
        private readonly uint _tableIndex;

        public WasmIndirectCallInstruction(WasmOp op, ISymbolNode type, uint tableIndex) : base(op)
        {
            _type = type;
            _tableIndex = tableIndex;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations)
        {
            long position = writer.Indices(Op, 0, _tableIndex, Relocation.GetSize(RelocType.WASM_TYPE_INDEX_LEB));
            AddRelocation(relocations, RelocType.WASM_TYPE_INDEX_LEB, position, _type);
        }
    }

    internal sealed class WasmMemoryArgInstruction : WasmExpr
    {
        private readonly uint _alignLog2;
        private readonly ulong _offset;
        private readonly ISymbolNode _offsetSymbol;

        public WasmMemoryArgInstruction(WasmOp op, uint align, ulong offset, ISymbolNode offsetSymbol = null) : base(op)
        {
            Debug.Assert(align is 1 or 2 or 4 or 8 or 16);
            _alignLog2 = (uint)System.Numerics.BitOperations.Log2(align);
            _offset = offset;
            _offsetSymbol = offsetSymbol;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations)
        {
            if (_offsetSymbol is null)
            {
                writer.MemoryAccess(Op, _alignLog2, _offset);
                return;
            }

            const RelocType type = RelocType.WASM_MEMORY_ADDR_REL_LEB;
            long position = writer.MemoryAccess(Op, _alignLog2, 0, offsetWidth: Relocation.GetSize(type));
            AddRelocation(relocations, type, position, _offsetSymbol);
        }
    }

    internal sealed class WasmRefNullExpr : WasmExpr
    {
        private readonly WasmHeapType _heapType;

        public WasmRefNullExpr(WasmHeapType heapType) : base(WasmOp.RefNull)
        {
            _heapType = heapType;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations) => writer.RefNull(_heapType);
    }

    internal sealed class WasmBlockStartExpr : WasmExpr
    {
        private readonly WasmBlockType _blockType;

        public WasmBlockStartExpr(WasmOp op, WasmBlockType blockType) : base(op)
        {
            _blockType = blockType;
        }

        internal override void Emit(WasmCodeWriter writer, List<Relocation> relocations) => writer.Block(Op, _blockType);
    }

    // ************************************************
    // Simple DSL wrapper for creating Wasm expressions
    // ************************************************
    internal static class Local
    {
        public static WasmExpr Get(int index) => new WasmIndexExpr(WasmOp.LocalGet, checked((uint)index));
        public static WasmExpr Set(int index) => new WasmIndexExpr(WasmOp.LocalSet, checked((uint)index));
        public static WasmExpr Tee(int index) => new WasmIndexExpr(WasmOp.LocalTee, checked((uint)index));
    }

    internal static class Global
    {
        public static WasmExpr Get(int index) => new WasmIndexExpr(WasmOp.GlobalGet, checked((uint)index));
        public static WasmExpr Set(int index) => new WasmIndexExpr(WasmOp.GlobalSet, checked((uint)index));
    }

    internal static class I32
    {
        public static WasmExpr Const(long value) => new WasmConstExpr(WasmOp.I32Const, checked((int)value));
        public static WasmExpr ConstRVA(ISymbolNode symbolNode) => new WasmLEBConstantReloc(WasmOp.I32Const, symbolNode, RelocType.WASM_MEMORY_ADDR_REL_SLEB);

        public static WasmExpr Add => new WasmOpExpr(WasmOp.I32Add);
        public static WasmExpr Sub => new WasmOpExpr(WasmOp.I32Sub);
        public static WasmExpr Eqz => new WasmOpExpr(WasmOp.I32Eqz);
        public static WasmExpr Ge_s => new WasmOpExpr(WasmOp.I32GeS);
        public static WasmExpr Load(ulong offset) => new WasmMemoryArgInstruction(WasmOp.I32Load, 4, offset);
        public static WasmExpr Load16_u(ulong offset) => new WasmMemoryArgInstruction(WasmOp.I32Load16U, 2, offset);
        public static WasmExpr LoadWithRVAOffset(ISymbolNode symbolNode) => new WasmMemoryArgInstruction(WasmOp.I32Load, 4, 0, symbolNode);
        public static WasmExpr Store(ulong offset) => new WasmMemoryArgInstruction(WasmOp.I32Store, 4, offset);
    }

    internal static class I64
    {
        public static WasmExpr Const(long value) => new WasmConstExpr(WasmOp.I64Const, value);
        public static WasmExpr Load(ulong offset) => new WasmMemoryArgInstruction(WasmOp.I64Load, 8, offset);
        public static WasmExpr Store(ulong offset) => new WasmMemoryArgInstruction(WasmOp.I64Store, 8, offset);
    }

    internal static class F32
    {
        public static WasmExpr Load(ulong offset) => new WasmMemoryArgInstruction(WasmOp.F32Load, 4, offset);
        public static WasmExpr Store(ulong offset) => new WasmMemoryArgInstruction(WasmOp.F32Store, 4, offset);
    }

    internal static class F64
    {
        public static WasmExpr Load(ulong offset) => new WasmMemoryArgInstruction(WasmOp.F64Load, 8, offset);
        public static WasmExpr Store(ulong offset) => new WasmMemoryArgInstruction(WasmOp.F64Store, 8, offset);
    }

    internal static class V128
    {
        public static WasmExpr Load(ulong offset) => new WasmMemoryArgInstruction(WasmOp.V128Load, 16, offset);
        public static WasmExpr Store(ulong offset) => new WasmMemoryArgInstruction(WasmOp.V128Store, 16, offset);
    }

    internal static class Memory
    {
        public static WasmExpr Copy(int dstMemoryIndex = 0, int srcMemoryIndex = 0) =>
            new WasmIndexPairExpr(WasmOp.MemoryCopy, checked((uint)dstMemoryIndex), checked((uint)srcMemoryIndex));

        public static WasmExpr Fill(int memoryIndex = 0) => new WasmIndexExpr(WasmOp.MemoryFill, checked((uint)memoryIndex));

        public static WasmExpr Init(int dataSegmentIndex, int memoryIndex = 0) =>
            new WasmIndexPairExpr(WasmOp.MemoryInit, checked((uint)dataSegmentIndex), checked((uint)memoryIndex));

        public static WasmExpr Load(WasmValueType type, ulong offset) => type switch
        {
            WasmValueType.I32 => I32.Load(offset),
            WasmValueType.I64 => I64.Load(offset),
            WasmValueType.F32 => F32.Load(offset),
            WasmValueType.F64 => F64.Load(offset),
            WasmValueType.V128 => V128.Load(offset),
            _ => throw new UnreachableException(),
        };

        public static WasmExpr Store(WasmValueType type, ulong offset) => type switch
        {
            WasmValueType.I32 => I32.Store(offset),
            WasmValueType.I64 => I64.Store(offset),
            WasmValueType.F32 => F32.Store(offset),
            WasmValueType.F64 => F64.Store(offset),
            WasmValueType.V128 => V128.Store(offset),
            _ => throw new UnreachableException(),
        };
    }

    internal static class ControlFlow
    {
        public static WasmExpr Unreachable => new WasmOpExpr(WasmOp.Unreachable);
        public static WasmExpr Call(ISymbolNode target) => new WasmLEBConstantReloc(WasmOp.Call, target, RelocType.WASM_FUNCTION_INDEX_LEB);
        public static WasmExpr ReturnCall(ISymbolNode target) => new WasmLEBConstantReloc(WasmOp.ReturnCall, target, RelocType.WASM_FUNCTION_INDEX_LEB);
        public static WasmExpr CallIndirect(ISymbolNode funcType, uint tableIndex) => new WasmIndirectCallInstruction(WasmOp.CallIndirect, funcType, tableIndex);
    }

    internal static class Table
    {
        public static WasmExpr Grow(uint tableIndex) => new WasmIndexExpr(WasmOp.TableGrow, tableIndex);
        public static WasmExpr Init(int elemSegmentIndex, int tableIndex = 0) =>
            new WasmIndexPairExpr(WasmOp.TableInit, checked((uint)elemSegmentIndex), checked((uint)tableIndex));
    }

    internal static class Ref
    {
        public static WasmExpr NullFuncRef => new WasmRefNullExpr(WasmHeapType.Func);
    }

    internal static class Block
    {
        public static WasmExpr If(WasmBlockType blockType) => new WasmBlockStartExpr(WasmOp.If, blockType);
        public static WasmExpr End => new WasmOpExpr(WasmOp.End);
    }
}
