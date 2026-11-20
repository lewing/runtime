// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;

using ILCompiler.DependencyAnalysis;
using Internal.Text;

namespace ILCompiler.PortableCallHelpers.WasmObject
{
    /// <summary>
    /// A named blob of data in the call-helper object, with pointer-sized references to other symbols: a C
    /// global such as a lookup table, a string, or a zero-initialized buffer.
    /// </summary>
    internal sealed class CallHelperDataNode : ObjectNode, ISymbolDefinitionNode
    {
        // Named as clang names them, so the linker merges them with the rest of the program's data.
        public static readonly ObjectNodeSection ReadOnlyData = new(".rodata.callhelpers", SectionType.ReadOnly);
        public static readonly ObjectNodeSection Data = new(".data.callhelpers", SectionType.Writeable);

        private readonly string _name;
        private readonly ObjectNodeSection _section;
        private readonly int _alignment;
        private readonly byte[] _data;
        private readonly (int Offset, ISymbolNode Target)[] _references;

        public CallHelperDataNode(string name, ObjectNodeSection section, int alignment, byte[] data, params (int Offset, ISymbolNode Target)[] references)
        {
            _name = name;
            _section = section;
            _alignment = alignment;
            _data = data;
            _references = references;
        }

        public int Offset => 0;
        public override bool IsShareable => false;
        public override bool StaticDependenciesAreComputed => true;
        public override int ClassCode => 1340270132;

        public override ObjectNodeSection GetSection(NodeFactory factory) => _section;

        public void AppendMangledName(NameMangler nameMangler, Utf8StringBuilder sb) => sb.Append(_name);

        protected override string GetName(NodeFactory factory) => _name;

        public override int CompareToImpl(ISortableNode other, CompilerComparer comparer) =>
            string.CompareOrdinal(_name, ((CallHelperDataNode)other)._name);

        public override ObjectData GetData(NodeFactory factory, bool relocsOnly = false)
        {
            var relocs = new Relocation[_references.Length];
            for (int i = 0; i < relocs.Length; i++)
                relocs[i] = new Relocation(RelocType.IMAGE_REL_BASED_HIGHLOW, _references[i].Offset, _references[i].Target);

            return new ObjectData(_data, relocs, _alignment, [this]);
        }

        /// <summary>A NUL-terminated string, as C emits a string literal.</summary>
        public static CallHelperDataNode String(string name, string value)
        {
            byte[] data = new byte[System.Text.Encoding.UTF8.GetByteCount(value) + 1];
            System.Text.Encoding.UTF8.GetBytes(value, data);
            return new CallHelperDataNode(name, ReadOnlyData, 1, data);
        }

        /// <summary>A little-endian <c>uint32_t</c>, such as a <c>size_t</c> element count on wasm32.</summary>
        public static CallHelperDataNode UInt32(string name, ObjectNodeSection section, uint value)
        {
            byte[] data = BitConverter.GetBytes(value);
            if (!BitConverter.IsLittleEndian)
                Array.Reverse(data);
            return new CallHelperDataNode(name, section, 4, data);
        }

        /// <summary>An array of <c>{ const char* key; void* value; }</c> pairs, as callhelpers.hpp declares them.</summary>
        public static CallHelperDataNode PointerPairTable(string name, IReadOnlyList<(ISymbolNode First, ISymbolNode Second)> entries)
        {
            const int PointerSize = CallHelperLayout.PointerSize;
            var references = new List<(int, ISymbolNode)>(entries.Count * 2);
            for (int i = 0; i < entries.Count; i++)
            {
                references.Add((i * CallHelperLayout.KeyValueSize + CallHelperLayout.KeyOffset, entries[i].First));
                references.Add((i * CallHelperLayout.KeyValueSize + CallHelperLayout.ValueOffset, entries[i].Second));
            }

            return new CallHelperDataNode(name, ReadOnlyData, PointerSize, new byte[entries.Count * CallHelperLayout.KeyValueSize], references.ToArray());
        }
    }
}
