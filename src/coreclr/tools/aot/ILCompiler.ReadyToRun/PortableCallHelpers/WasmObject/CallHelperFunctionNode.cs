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
    /// <summary>A native function of the call-helper object, given its wasm signature, locals and body.</summary>
    internal sealed class CallHelperFunctionNode : AssemblyStubNode, INodeWithTypeSignature
    {
        private readonly TypeSystemContext _context;
        private readonly string _name;
        private readonly WasmSignature _signature;
        private readonly WasmValueType[] _locals;
        private readonly WasmExpr[] _body;

        public CallHelperFunctionNode(TypeSystemContext context, string name, WasmFuncType type, WasmValueType[] locals, WasmExpr[] body)
        {
            _context = context;
            _name = name;
            _signature = new WasmSignature(type, SignatureString(type));
            _locals = locals;
            _body = body;
        }

        /// <summary>The signature string <see cref="WasmLowering.RaiseSignature"/> reads for a native function of <paramref name="type"/>.</summary>
        public static string SignatureString(WasmFuncType type)
        {
            var chars = new char[type.Params.Types.Length + 1];
            chars[0] = type.Returns.Types.Length == 0 ? 'v' : WasmLowering.WasmValueTypeToSigChar(type.Returns.Types[0]);
            for (int i = 0; i < type.Params.Types.Length; i++)
                chars[i + 1] = WasmLowering.WasmValueTypeToSigChar(type.Params.Types[i]);
            return new string(chars);
        }

        MethodSignature INodeWithTypeSignature.Signature => WasmLowering.RaiseSignature(_signature, _context);
        bool INodeWithTypeSignature.IsUnmanagedCallersOnly => true;
        bool INodeWithTypeSignature.IsAsyncCall => false;
        bool INodeWithTypeSignature.HasGenericContextArg => false;

        protected override bool IsVisibleFromManagedCode => false;

        public override int ClassCode => 1340270133;

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb) => sb.Append(_name);

        protected override string GetName(NodeFactory factory) => _name;

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer) =>
            string.CompareOrdinal(_name, ((CallHelperFunctionNode)other)._name);

        protected override void ComputeNonRelocationBasedDependencies(DependencySink sink, NodeFactory factory)
        {
            base.ComputeNonRelocationBasedDependencies(sink, factory);
            sink.Add(factory.WasmTypeNode(this), "type of a call-helper function");
        }

        protected override void EmitCode(NodeFactory factory, ref WasmEmitter instructionEncoder, bool relocsOnly)
        {
            instructionEncoder.FunctionBody = new WasmFunctionBody(_signature.FuncType, _locals, _body);
        }

        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X64.X64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X86.X86Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM.ARMEmitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM64.ARM64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.LoongArch64.LoongArch64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.RiscV64.RiscV64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
    }
}
