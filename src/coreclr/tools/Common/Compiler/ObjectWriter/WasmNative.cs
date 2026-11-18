// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Text;
using System.Diagnostics;
using System.Linq;
using System.Collections.Generic;
using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using ILCompiler.ObjectWriter.WasmInstructions;
using ValType = Microsoft.NET.WebAssembly.Types.ValType;
using Mutability = Microsoft.NET.WebAssembly.Types.Mutability;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    public static class PlaceholderValues
    {
        // Wasm function signature for (func (params i32) (result i32))
        public static WasmFuncType CreateWasmFunc_i32_i32()
        {
            return new WasmFuncType(
                paramTypes: new([WasmValueType.I32]),
                returnTypes: new([WasmValueType.I32])
            );
        }
    }

    /// <summary>The descriptor of an import; the WebAssembly toolchain's <see cref="WasmModuleEntryWriter"/> encodes it.</summary>
    public abstract class WasmImportType
    {
        public readonly WasmExternalKind Kind;

        private protected WasmImportType(WasmExternalKind kind)
        {
            Kind = kind;
        }

        internal abstract void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name);
    }

    internal sealed class WasmFunctionImportType : WasmImportType
    {
        private readonly int _typeIndex;

        public WasmFunctionImportType(int typeIndex) : base(WasmExternalKind.Function)
        {
            _typeIndex = typeIndex;
        }

        internal override void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name) =>
            writer.FunctionImport(module, name, checked((uint)_typeIndex));
    }

    public class WasmGlobalImportType : WasmImportType
    {
        private readonly WasmValueType _valueType;
        private readonly WasmMutabilityType _mutability;

        public WasmGlobalImportType(WasmValueType valueType, WasmMutabilityType mutability) : base(WasmExternalKind.Global)
        {
            _valueType = valueType;
            _mutability = mutability;
        }

        internal override void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name) =>
            writer.GlobalImport(module, name, (ValType)_valueType, (Mutability)_mutability);
    }

    /// <summary>A <c>funcref</c> table with room for at least one entry.</summary>
    public class WasmTableImportType : WasmImportType
    {
        public WasmTableImportType() : base(WasmExternalKind.Table)
        {
        }

        internal override void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name) =>
            writer.TableImport(module, name, ValType.FuncRef, min: 1);
    }

    public class WasmMemoryImportType : WasmImportType
    {
        private readonly uint _min;
        private readonly uint? _max;

        public WasmMemoryImportType(uint min, uint? max = null) : base(WasmExternalKind.Memory)
        {
            _min = min;
            _max = max;
        }

        internal override void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name) =>
            writer.MemoryImport(module, name, _min, _max);
    }

    /// <summary>An exception tag whose parameters are the function type <c>typeIndex</c>.</summary>
    public class WasmTagImportType : WasmImportType
    {
        private readonly int _typeIndex;

        public WasmTagImportType(int typeIndex) : base(WasmExternalKind.Tag)
        {
            _typeIndex = typeIndex;
        }

        internal override void Write(WasmModuleEntryWriter writer, ReadOnlySpan<byte> module, ReadOnlySpan<byte> name) =>
            writer.TagImport(module, name, checked((uint)_typeIndex));
    }

    public class WasmImport
    {
        public readonly string Module;
        public readonly string Name;
        public WasmExternalKind Kind => Import.Kind;
        public readonly int? Index;
        public readonly WasmImportType Import;

        public WasmImport(string module, string name, WasmImportType import, int? index = null)
        {
            Module = module;
            Name = name;
            Import = import;
            Index = index;
        }

        internal void Write(WasmModuleEntryWriter writer) =>
            Import.Write(writer, Encoding.UTF8.GetBytes(Module), Encoding.UTF8.GetBytes(Name));
    }

    public class WasmGlobal
    {
        public readonly int Index;
        public readonly string Name;
        private readonly WasmValueType _valueType;
        private readonly WasmMutabilityType _mutability;
        private readonly WasmInstructionGroup _initExpr;

        public WasmGlobal(int index, string name, WasmValueType valueType, WasmMutabilityType mutability, WasmInstructionGroup initExpr)
        {
            Index = index;
            Name = name;
            _valueType = valueType;
            _mutability = mutability;
            _initExpr = initExpr;
        }

        internal void Write(WasmModuleEntryWriter writer) =>
            writer.Global((ValType)_valueType, (Mutability)_mutability, _initExpr.Bytes);
    }
}
