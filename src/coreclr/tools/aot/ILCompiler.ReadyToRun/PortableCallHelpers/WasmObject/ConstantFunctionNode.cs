// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using ILCompiler.ObjectWriter.WasmInstructions;
using Internal.JitInterface;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.PortableCallHelpers.WasmObject
{
    /// <summary>A native <c>uint32_t name(void)</c> that returns a constant.</summary>
    internal sealed class ConstantFunctionNode : AssemblyStubNode, INodeWithTypeSignature
    {
        private static readonly WasmSignature s_signature = new(
            new WasmFuncType(new WasmResultType([]), new WasmResultType([WasmValueType.I32])),
            "i");

        private readonly TypeSystemContext _context;
        private readonly string _name;
        private readonly uint _value;

        public ConstantFunctionNode(TypeSystemContext context, string name, uint value)
        {
            _context = context;
            _name = name;
            _value = value;
        }

        MethodSignature INodeWithTypeSignature.Signature => WasmLowering.RaiseSignature(s_signature, _context);
        bool INodeWithTypeSignature.IsUnmanagedCallersOnly => true;
        bool INodeWithTypeSignature.IsAsyncCall => false;
        bool INodeWithTypeSignature.HasGenericContextArg => false;

        protected override bool IsVisibleFromManagedCode => false;

        public override int ClassCode => 1340270133;

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb) => sb.Append(_name);

        protected override string GetName(NodeFactory factory) => _name;

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer) =>
            string.CompareOrdinal(_name, ((ConstantFunctionNode)other)._name);

        protected override void ComputeNonRelocationBasedDependencies(DependencySink sink, NodeFactory factory)
        {
            base.ComputeNonRelocationBasedDependencies(sink, factory);
            sink.Add(factory.WasmTypeNode(this), "type of a constant function");
        }

        protected override void EmitCode(NodeFactory factory, ref WasmEmitter instructionEncoder, bool relocsOnly)
        {
            instructionEncoder.FunctionBody = new WasmFunctionBody(s_signature.FuncType, [], [I32.Const(unchecked((int)_value))]);
        }

        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X64.X64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X86.X86Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM.ARMEmitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM64.ARM64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.LoongArch64.LoongArch64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.RiscV64.RiscV64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
    }
}
