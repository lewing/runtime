// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Internal.Text;
using Microsoft.NET.WebAssembly.Binary.Writing;

namespace ILCompiler.ObjectWriter
{
    /// <summary>
    /// The <c>name</c> custom section, carrying the module's function names.
    /// </summary>
    /// <remarks>
    /// Function names would otherwise have to be carried by the export section, which does not scale:
    /// exports count towards the engine's effective-type-size limit, and a framework-sized composite
    /// exceeds it. A custom section is ignored by engines, counts towards no limit, and can be
    /// stripped when size matters, so it is the only one of the three properties - loadable at scale,
    /// named, and small - that does not cost the other two.
    /// </remarks>
    internal sealed class WasmNameSection : IWasmSection
    {
        private const byte FunctionNameSubsectionId = 1;
        private static ReadOnlySpan<byte> SectionName => "name"u8;

        private readonly WasmSymbol[] _functionSymbols;
        private readonly long _subsectionSize;

        public WasmNameSection(IEnumerable<WasmSymbol> functionSymbols)
        {
            _functionSymbols = functionSymbols.ToArray();

            // Sized in a first pass so that the payload can be written straight to the output stream.
            // At framework scale the payload is tens of megabytes, so buffering it would put several
            // copies of it on the large object heap.
            long nameMapSize = 0;
            foreach (WasmSymbol symbol in _functionSymbols)
            {
                int nameLength = symbol.Name.Length;
                nameMapSize += WasmLeb128.GetULeb128Size((ulong)symbol.Index)
                    + WasmLeb128.GetULeb128Size((ulong)nameLength)
                    + nameLength;
            }

            _subsectionSize = WasmLeb128.GetULeb128Size((ulong)_functionSymbols.Length) + nameMapSize;
        }

        /// <summary>Number of functions named by this section.</summary>
        public int FunctionCount => _functionSymbols.Length;

        public WasmSectionId Type => WasmSectionId.Custom;

        public int Emit(WasmModuleWriter writer)
        {
            long payloadSize = 1 + WasmLeb128.GetULeb128Size((ulong)_subsectionSize) + _subsectionSize;
            int index = writer.BeginCustomSection(SectionName, payloadSize);
            Span<byte> buffer = stackalloc byte[WasmLeb128.MaxLength];
            buffer[0] = FunctionNameSubsectionId;
            writer.Write(buffer.Slice(0, 1));
            WriteULeb128(writer, buffer, (ulong)_subsectionSize);
            WriteULeb128(writer, buffer, (ulong)_functionSymbols.Length);

            // The name map must be sorted by index and free of duplicates. GetDefinitions already
            // orders by index, so this only asserts rather than re-sorting a very large sequence.
            int previousIndex = -1;
            foreach (WasmSymbol symbol in _functionSymbols)
            {
                Debug.Assert(symbol.Index > previousIndex, "Function name map must be sorted by index and duplicate-free");
                previousIndex = symbol.Index;

                WriteULeb128(writer, buffer, (ulong)symbol.Index);
                ReadOnlySpan<byte> name = symbol.Name.AsSpan();
                WriteULeb128(writer, buffer, (ulong)name.Length);
                writer.Write(name);
            }

            writer.EndSection();
            return index;
        }

        private static void WriteULeb128(WasmModuleWriter writer, Span<byte> buffer, ulong value) =>
            writer.Write(buffer.Slice(0, WasmLeb128.WriteULeb128(buffer, value)));
    }
}
