// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;

using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.Wasm;
using ILCompiler.ObjectWriter.WasmInstructions;
using Internal.JitInterface;
using Internal.Text;
using Internal.TypeSystem;

namespace ILCompiler.PortableCallHelpers.WasmObject
{
    /// <summary>
    /// The wasm form of a <c>CallFunc_*</c> thunk the interpreter uses to call native code or a portable
    /// entry point: <c>void (PCODE pcode, int8_t* pArgs, int8_t* pRet)</c>. It matches the C++ that
    /// <see cref="InterpToNativeGenerator"/> writes, compiled for wasm32.
    /// </summary>
    internal sealed class InterpToNativeThunkNode : AssemblyStubNode, INodeWithTypeSignature
    {
        private const int InterpStackSlotSize = 8;
        private const int TerminateR2RStackWalk = 1;
        private const int PortableEntryPointFrameSize = 16;

        private static readonly WasmSignature s_thunkSignature = new(
            new WasmFuncType(new WasmResultType([WasmValueType.I32, WasmValueType.I32, WasmValueType.I32]), new WasmResultType([])),
            "viii");

        private readonly TypeSystemContext _context;
        private readonly List<string> _args;
        private readonly string _returnToken;
        private readonly bool _isPortableEntryPointCall;
        private readonly WasmTypeNode _targetType;
        private readonly ISymbolNode _stackPointer;

        public string Name { get; }

        public InterpToNativeThunkNode(NodeFactory factory, string signature)
        {
            _context = factory.TypeSystemContext;
            List<string> tokens = InteropSignature.ParseSignatureTokens(signature);
            _isPortableEntryPointCall = tokens.Count > 0 && tokens[^1] == "p";
            if (_isPortableEntryPointCall)
                tokens.RemoveAt(tokens.Count - 1);
            int asyncMarker = tokens.IndexOf("a");
            if (asyncMarker >= 0)
                tokens.RemoveAt(asyncMarker);

            _returnToken = tokens[0];
            _args = tokens.GetRange(1, tokens.Count - 1);
            Name = InterpToNativeGenerator.CallFuncName(_args, InteropSignature.TokenToNameType(_returnToken), _isPortableEntryPointCall);
            _targetType = factory.WasmTypeNode(new WasmSignature(TargetFuncType(), signature));
            _stackPointer = factory.GetWellKnownWasmGlobalSymbol(new Utf8String(WasmWellKnownGlobalSymbolNode.StackPointerName));
        }

        MethodSignature INodeWithTypeSignature.Signature => WasmLowering.RaiseSignature(s_thunkSignature, _context);
        bool INodeWithTypeSignature.IsUnmanagedCallersOnly => true;
        bool INodeWithTypeSignature.IsAsyncCall => false;
        bool INodeWithTypeSignature.HasGenericContextArg => false;

        protected override bool IsVisibleFromManagedCode => false;

        public override int ClassCode => 1340270131;

        public override void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb) => sb.Append(Name);

        protected override string GetName(NodeFactory factory) => Name;

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer) =>
            string.CompareOrdinal(Name, ((InterpToNativeThunkNode)other).Name);

        protected override void ComputeNonRelocationBasedDependencies(DependencySink sink, NodeFactory factory)
        {
            base.ComputeNonRelocationBasedDependencies(sink, factory);
            sink.Add(_targetType, "call_indirect type of an interpreter-to-native thunk");
            sink.Add(factory.WasmTypeNode(this), "type of an interpreter-to-native thunk");
        }

        // A struct returned by value travels through a hidden pointer, except a 1-byte struct, which clang
        // returns as an i32 because the generated C declares it as a single-element array.
        private bool ReturnsThroughPointer => _returnToken[0] is 'S' or 'A' && InteropSignature.GetStructSize(_returnToken) > 1;

        private WasmValueType? DirectReturnType => _returnToken[0] switch
        {
            'v' => null,
            'S' or 'A' => ReturnsThroughPointer ? null : WasmValueType.I32,
            _ => ToValueType(_returnToken),
        };

        private WasmFuncType TargetFuncType()
        {
            List<WasmValueType> parameters = [];
            if (ReturnsThroughPointer)
                parameters.Add(WasmValueType.I32);
            if (_isPortableEntryPointCall)
                parameters.Add(WasmValueType.I32);
            foreach (string token in _args)
                parameters.Add(ToValueType(token));
            if (_isPortableEntryPointCall)
                parameters.Add(WasmValueType.I32);

            return new WasmFuncType(
                new WasmResultType(parameters.ToArray()),
                new WasmResultType(DirectReturnType is WasmValueType result ? [result] : []));
        }

        private static WasmValueType ToValueType(string token)
        {
            InteropSignature.TokenToNativeType(token); // Rejects multi-slot and unknown tokens with the generator's errors.
            return token[0] switch
            {
                'i' or 'T' or 'S' or 'A' => WasmValueType.I32,
                'l' => WasmValueType.I64,
                'f' => WasmValueType.F32,
                'd' => WasmValueType.F64,
                _ => throw new InvalidSignatureCharException(token[0]),
            };
        }

        protected override void EmitCode(NodeFactory factory, ref WasmEmitter instructionEncoder, bool relocsOnly)
        {
            const int Pcode = 0;
            const int PArgs = 1;
            const int PRet = 2;
            const int Frame = 3;

            List<WasmExpr> body = [];
            if (_isPortableEntryPointCall)
            {
                // alignas(16) int framePointer = TERMINATE_R2R_STACK_WALK; on the shadow stack.
                body.Add(Global.Get(_stackPointer));
                body.Add(I32.Const(PortableEntryPointFrameSize));
                body.Add(I32.Sub);
                body.Add(Local.Tee(Frame));
                body.Add(Global.Set(_stackPointer));
                body.Add(Local.Get(Frame));
                body.Add(I32.Const(TerminateR2RStackWalk));
                body.Add(I32.Store(0));
            }

            WasmValueType? directReturn = DirectReturnType;
            if (directReturn is not null || ReturnsThroughPointer)
                body.Add(Local.Get(PRet));
            if (_isPortableEntryPointCall)
                body.Add(Local.Get(Frame));

            int slot = 0;
            foreach (string token in _args)
            {
                if (token[0] == 'A')
                    slot = (slot + 1) & ~1;

                int offset = slot * InterpStackSlotSize;
                body.Add(Local.Get(PArgs));
                if (token[0] is 'S' or 'A')
                {
                    if (offset != 0)
                    {
                        body.Add(I32.Const(offset));
                        body.Add(I32.Add);
                    }
                }
                else
                {
                    body.Add(Memory.Load(ToValueType(token), (ulong)offset));
                }

                slot += InteropSignature.TokenToSlotCount(token);
            }

            if (_isPortableEntryPointCall)
            {
                // The portable entry point is the callee's last argument, and holds its function index.
                body.Add(Local.Get(Pcode));
                body.Add(Local.Get(Pcode));
                body.Add(I32.Load(0));
            }
            else
            {
                body.Add(Local.Get(Pcode));
            }

            body.Add(ControlFlow.CallIndirect(_targetType, 0));

            if (directReturn is WasmValueType result)
            {
                bool singleByteStruct = _returnToken[0] is 'S' or 'A';
                body.Add(singleByteStruct ? I32.Store8(0) : Memory.Store(result, 0));
            }

            if (_isPortableEntryPointCall)
            {
                body.Add(Local.Get(Frame));
                body.Add(I32.Const(PortableEntryPointFrameSize));
                body.Add(I32.Add);
                body.Add(Global.Set(_stackPointer));
            }

            Debug.Assert(!instructionEncoder.Is64Bit);
            instructionEncoder.FunctionBody = new WasmFunctionBody(
                s_thunkSignature.FuncType,
                _isPortableEntryPointCall ? [WasmValueType.I32] : [],
                body.ToArray());
        }

        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X64.X64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.X86.X86Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM.ARMEmitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.ARM64.ARM64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.LoongArch64.LoongArch64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
        protected override void EmitCode(NodeFactory factory, ref DependencyAnalysis.RiscV64.RiscV64Emitter instructionEncoder, bool relocsOnly) => throw new NotSupportedException();
    }
}
