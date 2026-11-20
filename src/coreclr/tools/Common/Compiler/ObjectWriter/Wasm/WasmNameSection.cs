// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Linq;
using Internal.Text;
using Microsoft.NET.WebAssembly.Binary;
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
        private readonly WasmSymbol[] _functionSymbols;
        private readonly long _entriesSize;

        public WasmNameSection(IEnumerable<WasmSymbol> functionSymbols)
        {
            _functionSymbols = functionSymbols.ToArray();

            // Sized in a first pass so that the payload can be written straight to the output stream.
            // At framework scale the payload is tens of megabytes, so buffering it would put several
            // copies of it on the large object heap.
            foreach (WasmSymbol symbol in _functionSymbols)
            {
                _entriesSize += NameMapWriter.GetEntrySize((uint)symbol.Index, symbol.Name.Length);
            }
        }

        /// <summary>Number of functions named by this section.</summary>
        public int FunctionCount => _functionSymbols.Length;

        public WasmSectionId Type => WasmSectionId.Custom;

        public int Emit(WasmModuleWriter writer)
        {
            int index = writer.BeginCustomSection("name"u8, NameMapWriter.GetSubsectionSize(_functionSymbols.Length, _entriesSize));

            // The name map must be sorted by index and free of duplicates, which NameMapWriter checks. GetDefinitions
            // already orders by index.
            NameMapWriter functionNames = new(writer, NameSubsectionId.Function, _functionSymbols.Length, _entriesSize);
            foreach (WasmSymbol symbol in _functionSymbols)
            {
                functionNames.Write((uint)symbol.Index, symbol.Name.AsSpan());
            }

            functionNames.Complete();
            writer.EndSection();
            return index;
        }
    }
}
