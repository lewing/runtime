// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using ILCompiler.DependencyAnalysis;
using ILCompiler.DependencyAnalysis.ReadyToRun;
using ILCompiler.DependencyAnalysisFramework;
using ILCompiler.ObjectWriter;
using ILCompiler.DependencyAnalysis.Wasm;
using ILCompiler.ObjectWriter.WasmInstructions;
using ILCompiler.Win32Resources;
using Internal.IL;
using Internal.JitInterface;
using Internal.ReadyToRunConstants;
using Internal.Text;
using Internal.TypeSystem;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.PortableCallHelpers.WasmObject
{
    /// <summary>
    /// Writes the interop helpers as a relocatable wasm object instead of C++, through the same object
    /// writer ILC uses, so linking them needs no C compiler.
    /// </summary>
    internal static class CallHelpersObjectEmitter
    {
        public const string FileName = "callhelpers.o";

        public static void Emit(
            ReadyToRunCompilerContext context,
            ReadyToRunCompilationModuleGroupBase compilationGroup,
            string outputPath,
            IReadOnlyDictionary<string, MethodDesc> interpToNativeSignatures,
            PInvokeTableGenerator pinvokeGenerator,
            PInvokeTableGenerator.PInvokeTable pinvokeTable,
            List<PInvokeCallback> callbacks,
            uint? r2rImageSize,
            Logger logger)
        {
            var factory = new NodeFactory(
                context,
                compilationGroup,
                profileDataManager: null,
                new NativeAotNameMangler { CompilationUnitPrefix = new Utf8String("callhelpers") },
                corHeaderNode: null,
                debugDirectoryNode: null,
                new ResourceData((Internal.TypeSystem.Ecma.EcmaModule)context.SystemModule, static (_, _, _) => false),
                flags: default,
                new NodeFactoryOptimizationFlags(),
                ReadyToRunContainerFormat.Wasm,
                imageBase: 0,
                associatedModule: null,
                genericCycleDepthCutoff: -1,
                genericCycleBreadthCutoff: -1);

            var analyzer = new DependencyAnalyzer<NoLogStrategy<NodeFactory>, NodeFactory>(
                factory, new SortableDependencyNode.ObjectNodeComparer(CompilerComparer.Instance));
            // Callbacks are validated first, as the C++ generator does, so both report the same error first.
            var interop = new PInvokeTableEmitter(factory, analyzer, pinvokeGenerator);
            interop.AddReverseThunks(callbacks);
            interop.Add(pinvokeTable, context.Target);
            AddInterpToNativeThunks(factory, analyzer, interpToNativeSignatures);
            if (r2rImageSize is uint size)
                AddR2RImageBuffer(factory, analyzer, size);

            analyzer.ComputeMarkedNodes();
            factory.SetMarkingComplete();

            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
            using FileStream stream = File.Create(outputPath);
            new WasmRelocatableObjectWriter(factory, ObjectWritingOptions.None)
                .EmitObject(stream, analyzer.MarkedNodeList, dumper: null, logger);
        }

        // InterpToNativeGenerator: the CallFunc_* thunks and g_portableCallHelperThunks, which maps
        // "M" + signature to each one.
        private static void AddInterpToNativeThunks(NodeFactory factory, DependencyAnalyzerBase<NodeFactory> analyzer, IReadOnlyDictionary<string, MethodDesc> signatures)
        {
            string[] sorted = signatures.Keys.ToArray();
            Array.Sort(sorted, StringComparer.Ordinal);

            var entries = new List<(ISymbolNode, ISymbolNode)>(sorted.Length);
            var thunks = new Dictionary<string, InterpToNativeThunkNode>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Length; i++)
            {
                InterpToNativeThunkNode thunk;
                try
                {
                    thunk = new InterpToNativeThunkNode(factory, sorted[i]);
                }
                catch (Exception e) when (e is InvalidSignatureCharException or LogAsErrorException)
                {
                    throw InterpToNativeGenerator.ThunkError(sorted[i], signatures[sorted[i]], e);
                }

                // Signatures that differ only in an async marker share one thunk, as the C++ does.
                if (thunks.TryGetValue(thunk.Name, out InterpToNativeThunkNode existing))
                    thunk = existing;
                else
                    thunks.Add(thunk.Name, thunk);

                entries.Add((CallHelperDataNode.String($"__portable_callhelper_key_{i}", "M" + sorted[i]), thunk));
            }

            analyzer.AddRoot(CallHelperDataNode.PointerPairTable("g_portableCallHelperThunks", entries), "interpreter-to-native thunk table");
            analyzer.AddRoot(CallHelperDataNode.UInt32("g_portableCallHelperThunksCount", CallHelperDataNode.ReadOnlyData, (uint)entries.Count), "interpreter-to-native thunk count");
        }

        // The buffer the ReadyToRun composition copies the composite image into, and the export that
        // reports its size (r2r-image-buffer.cpp in the C++ build).
        private static void AddR2RImageBuffer(NodeFactory factory, DependencyAnalyzerBase<NodeFactory> analyzer, uint size)
        {
            analyzer.AddRoot(
                new CallHelperDataNode("g_wasi_r2r_image", new ObjectNodeSection(".bss.g_wasi_r2r_image", SectionType.Uninitialized), 16, new byte[size]),
                "ReadyToRun image buffer");
            analyzer.AddRoot(
                CallHelperDataNode.UInt32("g_wasi_r2r_image_cap", CallHelperDataNode.Data, size),
                "ReadyToRun image buffer capacity");

            var capacity = new CallHelperFunctionNode(
                factory.TypeSystemContext,
                "__wasi_r2r_image_cap",
                new WasmFuncType(new WasmResultType([]), new WasmResultType([WasmValueType.I32])),
                [],
                [I32.Const(unchecked((int)size))]);
            factory.AddExport(capacity, new Utf8String("wasi_r2r_image_cap"));
            analyzer.AddRoot(capacity, "ReadyToRun image buffer capacity export");
        }
    }

    /// <summary>
    /// The P/Invoke and runtime helper tables of callhelpers-pinvoke.cpp, and the functions the runtime looks
    /// entry points up with: <c>callhelpers_pinvoke_override</c> and <c>ResolveRuntimeHelperEntrypoint</c>.
    /// </summary>
    internal sealed class PInvokeTableEmitter(NodeFactory factory, DependencyAnalyzerBase<NodeFactory> analyzer, PInvokeTableGenerator generator)
    {
        private const int PointerSize = CallHelperLayout.PointerSize;
        private const int LibraryTableEntrySize = CallHelperLayout.LibraryTableEntrySize;
        private const int EntrySize = CallHelperLayout.KeyValueSize;

        private readonly Dictionary<string, CallHelperDataNode> _strings = new(StringComparer.Ordinal);
        private readonly Dictionary<string, ExternFunctionSymbolNode> _functions = new(StringComparer.Ordinal);

        // A function the linker binds by its symbol name, like a C declaration without import attributes.
        private static readonly LinkageMetadata s_boundBySymbol = new(default);

        private static readonly WasmFuncType s_resolveType = new(
            new WasmResultType([WasmValueType.I32, WasmValueType.I32, WasmValueType.I32]), new WasmResultType([WasmValueType.I32]));

        public void Add(PInvokeTableGenerator.PInvokeTable table, TargetDetails target)
        {
            TypeSystemContext context = factory.TypeSystemContext;
            ExternFunctionSymbolNode strcmp = Function("strcmp", new WasmFuncType(
                new WasmResultType([WasmValueType.I32, WasmValueType.I32]), new WasmResultType([WasmValueType.I32])), s_boundBySymbol);

            // minipal_resolve_dllimport: the method of the entry named `name` in `entries`, or null.
            var resolve = new CallHelperFunctionNode(context, "__callhelpers_resolve_entry", s_resolveType, [WasmValueType.I32],
            [
                Block.Start(WasmBlockType.Empty),
                Block.Loop(WasmBlockType.Empty),
                Local.Get(3), Local.Get(1), I32.Ge_u, ControlFlow.BrIf(1),
                Local.Get(2), Local.Get(0), Local.Get(3), I32.Const(EntrySize), I32.Mul, I32.Add, I32.Load(0),
                ControlFlow.Call(strcmp), I32.Eqz,
                Block.If(WasmBlockType.Empty),
                Local.Get(0), Local.Get(3), I32.Const(EntrySize), I32.Mul, I32.Add, I32.Load(CallHelperLayout.ValueOffset), ControlFlow.Return,
                Block.End,
                Local.Get(3), I32.Const(1), I32.Add, Local.Set(3),
                ControlFlow.Br(0),
                Block.End,
                Block.End,
                I32.Const(0),
            ]);

            var libraries = new List<(ISymbolNode Name, ISymbolNode Entries, int Count)>();
            foreach ((string library, List<IGrouping<string, PInvokeInfo>> entries) in table.Libraries)
            {
                var pairs = new List<(ISymbolNode, ISymbolNode)>(entries.Count);
                foreach (IGrouping<string, PInvokeInfo> entry in entries)
                {
                    PInvokeInfo pinvoke = entry.First();
                    pairs.Add((String(entry.Key), Native(pinvoke)));
                }

                libraries.Add((String(library), CallHelperDataNode.PointerPairTable($"__callhelpers_pinvoke_{libraries.Count}", pairs), pairs.Count));
            }

            WasmExpr[] overrideBody = [I32.Const(0)];
            if (libraries.Count > 0)
            {
                byte[] data = new byte[libraries.Count * LibraryTableEntrySize];
                var references = new List<(int, ISymbolNode)>();
                for (int i = 0; i < libraries.Count; i++)
                {
                    int offset = i * LibraryTableEntrySize;
                    references.Add((offset, libraries[i].Name));
                    references.Add((offset + PointerSize, libraries[i].Entries));
                    BitConverter.TryWriteBytes(data.AsSpan(offset + 2 * PointerSize), libraries[i].Count);
                }

                var tables = new CallHelperDataNode("__callhelpers_pinvoke_tables", CallHelperDataNode.ReadOnlyData, PointerSize, data, references.ToArray());

                // Params: library_name (0), entry_point_name (1). Locals: i (2), &tables[i] (3).
                overrideBody =
                [
                    Block.Start(WasmBlockType.Empty),
                    Block.Loop(WasmBlockType.Empty),
                    Local.Get(2), I32.Const(libraries.Count), I32.Ge_u, ControlFlow.BrIf(1),
                    I32.ConstAddress(tables), Local.Get(2), I32.Const(LibraryTableEntrySize), I32.Mul, I32.Add, Local.Set(3),
                    Local.Get(0), Local.Get(3), I32.Load(0), ControlFlow.Call(strcmp), I32.Eqz,
                    Block.If(WasmBlockType.Empty),
                    Local.Get(3), I32.Load(PointerSize), Local.Get(3), I32.Load(2 * PointerSize), Local.Get(1),
                    ControlFlow.Call(resolve), ControlFlow.Return,
                    Block.End,
                    Local.Get(2), I32.Const(1), I32.Add, Local.Set(2),
                    ControlFlow.Br(0),
                    Block.End,
                    Block.End,
                    I32.Const(0),
                ];
            }

            analyzer.AddRoot(new CallHelperFunctionNode(context, "_Z28callhelpers_pinvoke_overridePKcS0_",
                new WasmFuncType(new WasmResultType([WasmValueType.I32, WasmValueType.I32]), new WasmResultType([WasmValueType.I32])),
                [WasmValueType.I32, WasmValueType.I32], overrideBody), "P/Invoke table lookup");

            WasmExpr[] helperBody = [I32.Const(0)];
            if (table.RuntimeHelpers.Length > 0)
            {
                var pairs = new List<(ISymbolNode, ISymbolNode)>(table.RuntimeHelpers.Length);
                foreach (PInvokeInfo helper in table.RuntimeHelpers)
                    pairs.Add((String(helper.EntryPoint), RuntimeHelper(helper, target)));

                CallHelperDataNode helpers = CallHelperDataNode.PointerPairTable("__callhelpers_runtime_helpers", pairs);
                helperBody = [I32.ConstAddress(helpers), I32.Const(pairs.Count), Local.Get(0), ControlFlow.Call(resolve)];
            }

            analyzer.AddRoot(new CallHelperFunctionNode(context, "_Z30ResolveRuntimeHelperEntrypointPKc",
                new WasmFuncType(new WasmResultType([WasmValueType.I32]), new WasmResultType([WasmValueType.I32])),
                [], helperBody), "runtime helper lookup");
        }

        /// <summary>
        /// callhelpers-reverse.cpp: a native wrapper for each [UnmanagedCallersOnly] method, which looks its
        /// MethodDesc up on first use and runs it in the interpreter, and g_ReverseThunks, which maps the
        /// runtime's key for each method to its wrapper.
        /// </summary>
        public void AddReverseThunks(List<PInvokeCallback> callbacks)
        {
            const int SlotSize = 8; // each argument travels in an int64_t
            const int ReverseThunkEntrySize = CallHelperLayout.ReverseThunkEntrySize;

            TypeSystemContext context = factory.TypeSystemContext;
            PInvokeTableGenerator.PrepareCallbacks(callbacks);

            ExternFunctionSymbolNode lookup = Function("LookupUnmanagedCallersOnlyMethodByName", new WasmFuncType(
                new WasmResultType([WasmValueType.I32, WasmValueType.I32, WasmValueType.I32]), new WasmResultType([])), s_boundBySymbol);
            ExternFunctionSymbolNode execute = Function("ExecuteInterpretedMethodFromUnmanaged", new WasmFuncType(
                new WasmResultType([WasmValueType.I32, WasmValueType.I32, WasmValueType.I32, WasmValueType.I32, WasmValueType.I32]), new WasmResultType([WasmValueType.I32])), s_boundBySymbol);
            ISymbolNode stackPointer = factory.GetWellKnownWasmGlobalSymbol(new Utf8String(WasmWellKnownGlobalSymbolNode.StackPointerName));

            var table = new List<(uint Hash, ISymbolNode Source, ISymbolNode Target, ISymbolNode EntryPoint)>(callbacks.Count);
            foreach (PInvokeCallback cb in callbacks)
            {
                (string[] parameterCTypes, string returnCType, bool returnsViaBuffer, bool hasResult) = PInvokeTableGenerator.NativeCallbackSignature(cb);
                WasmFuncType type = NativeType(returnCType, returnsViaBuffer ? ["void *", .. parameterCTypes] : [.. parameterCTypes]);
                int parameterCount = type.Params.Types.Length;
                int firstArgument = returnsViaBuffer ? 1 : 0;
                int frame = parameterCount; // local: the frame holding args[] and result
                int r2r = parameterCount + 1; // local: the cached R2R entry point (exports only)
                int nativeCode = parameterCount + (cb.IsExport ? 2 : 1); // local: native code the interpreter reports

                var methodDesc = new CallHelperDataNode($"MD_{cb.EntrySymbol}", CallHelperDataNode.Data, PointerSize, new byte[PointerSize]);
                var wrapper = new CallHelperFunctionNode(context, $"Call_{cb.EntrySymbol}", type,
                    cb.IsExport ? [WasmValueType.I32, WasmValueType.I32, WasmValueType.I32] : [WasmValueType.I32, WasmValueType.I32]);
                WasmTypeNode wrapperType = factory.WasmTypeNode(new WasmSignature(type, CallHelperFunctionNode.SignatureString(type)));
                CallHelperDataNode r2rCode = cb.IsExport
                    ? new CallHelperDataNode($"R2RCode_{cb.EntrySymbol}", CallHelperDataNode.Data, PointerSize, BitConverter.GetBytes(-1))
                    : null;

                List<WasmExpr> body =
                [
                    // Lazy lookup of the MethodDesc, for the function export scenario.
                    I32.ConstAddress(methodDesc), I32.Load(0), I32.Eqz,
                    Block.If(WasmBlockType.Empty),
                    I32.ConstAddress(String($"{cb.TypeFullName}, {cb.AssemblyName}")), I32.ConstAddress(String(cb.MethodName)), I32.ConstAddress(methodDesc),
                    ControlFlow.Call(lookup),
                    Block.End,
                ];

                if (cb.IsExport)
                {
                    // Prefer the R2R native entry point when the callback was compiled (partial R2R); resolve once.
                    ExternFunctionSymbolNode getR2RCode = Function("GetR2RNativeCodeForUnmanagedCallersOnly", new WasmFuncType(
                        new WasmResultType([WasmValueType.I32]), new WasmResultType([WasmValueType.I32])), s_boundBySymbol);
                    body.AddRange(
                    [
                        I32.ConstAddress(r2rCode), I32.Load(0), Local.Tee(r2r), I32.Const(-1), I32.Eq,
                        Block.If(WasmBlockType.Empty),
                        I32.ConstAddress(methodDesc), I32.Load(0), ControlFlow.Call(getR2RCode), Local.Set(r2r),
                        I32.ConstAddress(r2rCode), Local.Get(r2r), I32.Store(0),
                        Block.End,
                        Local.Get(r2r),
                        Block.If(WasmBlockType.Empty),
                    ]);
                    for (int i = 0; i < parameterCount; i++)
                        body.Add(Local.Get(i));
                    body.AddRange([Local.Get(r2r), ControlFlow.CallIndirect(wrapperType, 0), ControlFlow.Return, Block.End]);
                }

                // int64_t args[N] and the result, on the shadow stack.
                int argumentBytes = cb.Parameters.Length * SlotSize;
                int frameSize = (argumentBytes + (hasResult ? SlotSize : 0) + 15) & ~15;
                if (frameSize != 0)
                    body.AddRange([Global.Get(stackPointer), I32.Const(frameSize), I32.Sub, Local.Tee(frame), Global.Set(stackPointer)]);

                for (int i = 0; i < parameterCTypes.Length; i++)
                {
                    body.Add(Local.Get(frame));
                    body.Add(Local.Get(firstArgument + i));
                    // A cast widens integers; a float slot carries the value's bits, and the rest stays zero.
                    switch (parameterCTypes[i])
                    {
                        case "int32_t": body.Add(I64.ExtendI32_s); break;
                        case "uint32_t" or "void *" or "uintptr_t": body.Add(I64.ExtendI32_u); break;
                        case "float": body.AddRange([I32.ReinterpretF32, I64.ExtendI32_u]); break;
                        case "double": body.Add(I64.ReinterpretF64); break;
                        case "int64_t" or "uint64_t": break;
                        default: throw new InvalidOperationException($"No slot conversion for the C type '{parameterCTypes[i]}'.");
                    }

                    body.Add(I64.Store((ulong)(i * SlotSize)));
                }

                body.AddRange([I32.ConstAddress(methodDesc), I32.Load(0)]);
                body.Add(argumentBytes != 0 ? Local.Get(frame) : I32.Const(0));
                body.Add(I32.Const(argumentBytes));
                if (returnsViaBuffer)
                    body.Add(Local.Get(0));
                else if (hasResult)
                    body.AddRange([Local.Get(frame), I32.Const(argumentBytes), I32.Add]);
                else
                    body.Add(I32.Const(0));
                body.Add(I32.ConstAddress(wrapper)); // (PCODE)&Call_X
                body.AddRange([ControlFlow.Call(execute), Local.Set(nativeCode)]);

                if (hasResult)
                    body.AddRange([Local.Get(frame), Memory.Load(type.Returns.Types[0], (ulong)argumentBytes)]);
                if (frameSize != 0)
                    body.AddRange([Local.Get(frame), I32.Const(frameSize), I32.Add, Global.Set(stackPointer)]);

                // The method gained native code (delayed R2R activation) since this thunk was handed out, so the
                // interpreter didn't run it: call that code instead, and refresh an export's cached entry point.
                body.AddRange([Local.Get(nativeCode), Block.If(WasmBlockType.Empty)]);
                if (r2rCode is not null)
                    body.AddRange([I32.ConstAddress(r2rCode), Local.Get(nativeCode), I32.Store(0)]);
                for (int i = 0; i < parameterCount; i++)
                    body.Add(Local.Get(i));
                body.AddRange([Local.Get(nativeCode), ControlFlow.CallIndirect(wrapperType, 0), ControlFlow.Return, Block.End]);

                wrapper.Body = body.ToArray();

                if (cb.IsExport)
                {
                    // extern "C" RET EntryPoint(params) { return Call_X(params); }
                    var export = new List<WasmExpr>();
                    for (int i = 0; i < parameterCount; i++)
                        export.Add(Local.Get(i));
                    export.Add(ControlFlow.Call(wrapper));
                    analyzer.AddRoot(new CallHelperFunctionNode(context, cb.EntryPoint, type, [], export.ToArray()), "exported [UnmanagedCallersOnly] method");
                }

                table.Add((PInvokeTableGenerator.ReverseThunkHash(cb), String(cb.Key), methodDesc, wrapper));
            }

            byte[] data = new byte[Math.Max(table.Count, 1) * ReverseThunkEntrySize];
            var references = new List<(int, ISymbolNode)>();
            for (int i = 0; i < table.Count; i++)
            {
                int offset = i * ReverseThunkEntrySize;
                BitConverter.TryWriteBytes(data.AsSpan(offset + CallHelperLayout.ReverseThunkHashOffset), table[i].Hash);
                references.Add((offset + CallHelperLayout.ReverseThunkSourceOffset, table[i].Source));
                references.Add((offset + CallHelperLayout.ReverseThunkTargetOffset, table[i].Target));
                references.Add((offset + CallHelperLayout.ReverseThunkEntryPointOffset, table[i].EntryPoint));
            }

            analyzer.AddRoot(new CallHelperDataNode("g_ReverseThunks", CallHelperDataNode.ReadOnlyData, PointerSize, data, references.ToArray()), "reverse thunk table");
            analyzer.AddRoot(CallHelperDataNode.UInt32("g_ReverseThunksCount", CallHelperDataNode.ReadOnlyData, (uint)table.Count), "reverse thunk count");
        }

        private ISymbolNode Native(PInvokeInfo pinvoke)
        {
            (string returnType, List<string> parameterTypes) = PInvokeTableGenerator.NativeSignature(pinvoke);
            LinkageMetadata linkage = pinvoke.WasmLinkage
                ? new WasmLinkageMetadata(new Utf8String(pinvoke.EntryPoint), new Utf8String(pinvoke.Module))
                : s_boundBySymbol;
            return Function(generator.NativeSymbol(pinvoke), NativeType(returnType, parameterTypes), linkage);
        }

        private ISymbolNode RuntimeHelper(PInvokeInfo helper, TargetDetails target)
        {
            // string.h's memmove and memset on WASI; bulk-memory wrappers on browser.
            if (helper.EntryPoint is "memmove" or "memset")
            {
                string symbol = target.OperatingSystem == TargetOS.Browser ? $"QCall_{helper.EntryPoint}" : helper.EntryPoint;
                return Function(symbol, s_resolveType, s_boundBySymbol);
            }

            return Native(helper);
        }

        private ExternFunctionSymbolNode Function(string symbol, WasmFuncType type, LinkageMetadata linkage)
        {
            if (!_functions.TryGetValue(symbol, out ExternFunctionSymbolNode node))
            {
                MethodSignature signature = WasmLowering.RaiseSignature(
                    new WasmSignature(type, CallHelperFunctionNode.SignatureString(type)), factory.TypeSystemContext);
                node = new ExternFunctionSymbolNode(new Utf8String(symbol), signature, isUnmanagedCallersOnly: true, isAsyncCall: false, hasGenericContextArg: false, linkage);
                _functions.Add(symbol, node);
            }

            return node;
        }

        private CallHelperDataNode String(string value)
        {
            if (!_strings.TryGetValue(value, out CallHelperDataNode node))
            {
                node = CallHelperDataNode.String($"__callhelpers_str_{_strings.Count}", value);
                _strings.Add(value, node);
            }

            return node;
        }

        // The wasm type clang gives a function declared with these C types.
        private static WasmFuncType NativeType(string returnType, List<string> parameterTypes)
        {
            WasmValueType[] parameters = new WasmValueType[parameterTypes.Count];
            for (int i = 0; i < parameters.Length; i++)
                parameters[i] = ValueType(parameterTypes[i]) ?? throw new InvalidOperationException($"A 'void' parameter in a native declaration.");

            return new WasmFuncType(
                new WasmResultType(parameters),
                new WasmResultType(ValueType(returnType) is WasmValueType result ? [result] : []));

            static WasmValueType? ValueType(string cType) => cType switch
            {
                "void" => null,
                "float" => WasmValueType.F32,
                "double" => WasmValueType.F64,
                "int64_t" or "uint64_t" => WasmValueType.I64,
                "int32_t" or "uint32_t" or "void *" or "uintptr_t" => WasmValueType.I32,
                _ => throw new InvalidOperationException($"No wasm type for the C type '{cType}'."),
            };
        }
    }
}
