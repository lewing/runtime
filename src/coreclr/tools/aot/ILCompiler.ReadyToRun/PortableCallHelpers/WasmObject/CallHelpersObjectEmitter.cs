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
using ILCompiler.Win32Resources;
using Internal.ReadyToRunConstants;
using Internal.Text;

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
            IReadOnlyCollection<string> interpToNativeSignatures,
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
            AddInterpToNativeThunks(factory, analyzer, interpToNativeSignatures);
            if (r2rImageSize is uint size)
                AddR2RImageBuffer(factory, analyzer, size);

            analyzer.ComputeMarkedNodes();
            factory.SetMarkingComplete();

            using FileStream stream = File.Create(outputPath);
            new WasmRelocatableObjectWriter(factory, ObjectWritingOptions.None)
                .EmitObject(stream, analyzer.MarkedNodeList, dumper: null, logger);
        }

        // InterpToNativeGenerator: the CallFunc_* thunks and g_portableCallHelperThunks, which maps
        // "M" + signature to each one.
        private static void AddInterpToNativeThunks(NodeFactory factory, DependencyAnalyzerBase<NodeFactory> analyzer, IReadOnlyCollection<string> signatures)
        {
            string[] sorted = signatures.ToArray();
            Array.Sort(sorted, StringComparer.Ordinal);

            var entries = new List<(ISymbolNode, ISymbolNode)>(sorted.Length);
            var thunks = new Dictionary<string, InterpToNativeThunkNode>(StringComparer.Ordinal);
            for (int i = 0; i < sorted.Length; i++)
            {
                var thunk = new InterpToNativeThunkNode(factory, sorted[i]);
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

            var capacity = new ConstantFunctionNode(factory.TypeSystemContext, "__wasi_r2r_image_cap", size);
            factory.AddExport(capacity, new Utf8String("wasi_r2r_image_cap"));
            analyzer.AddRoot(capacity, "ReadyToRun image buffer capacity export");
        }
    }
}
