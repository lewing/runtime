// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Xunit;

namespace VectorTAlignmentTests
{
    public class Program
    {
        private struct ByteThenVector
        {
            public byte B;
            public Vector<int> V;
        }

        private struct NestedByteThenVector
        {
            public byte B;
            public ByteThenVector Inner;
        }

        private struct ByteThenTwoVectors
        {
            public byte B;
            public Vector<int> V1;
            public Vector<int> V2;
        }

        /// <summary>
        /// Vector&lt;T&gt; is passed like the matching fixed-size vector, so its field alignment is its
        /// (ISA-determined) size capped by the largest vector alignment the target ABI supports.
        /// The caps are restated here rather than derived from the product code so that a change to
        /// the rule has to be made deliberately in both places.
        /// </summary>
        private static int ExpectedAlignment()
        {
            int cap = RuntimeInformation.ProcessArchitecture switch
            {
                // The Procedure Call Standard for ARM aligns __m128 at 8 and defines no larger vector.
                Architecture.Arm => 8,
                Architecture.Armv6 => 8,
                Architecture.Arm64 => 16,
                Architecture.LoongArch64 => 16,
                Architecture.RiscV64 => 16,
                // x86/x64 align each vector to its own size, up to __m512. Wasm uses the same cap:
                // Vector<T> is a v128 there, so it lands at 16 regardless.
                _ => 64,
            };

            return Math.Min(Unsafe.SizeOf<Vector<int>>(), cap);
        }

        private static int FieldOffset<TOuter, TField>(ref TOuter outer, ref TField field)
        {
            return (int)(nint)Unsafe.ByteOffset(
                ref Unsafe.As<TOuter, byte>(ref outer),
                ref Unsafe.As<TField, byte>(ref field));
        }

        [Fact]
        public static int TestEntryPoint()
        {
            int size = Unsafe.SizeOf<Vector<int>>();
            int align = ExpectedAlignment();

            // Vector<T> as a field: the preceding byte must be padded out to the vector's alignment.
            ByteThenVector single = default;
            Assert.Equal(align, FieldOffset(ref single, ref single.V));
            Assert.Equal(align + size, Unsafe.SizeOf<ByteThenVector>());

            // The alignment must propagate through a containing struct, not just the immediate one.
            NestedByteThenVector nested = default;
            Assert.Equal(align, FieldOffset(ref nested, ref nested.Inner));
            Assert.Equal(align * 2, FieldOffset(ref nested, ref nested.Inner.V));

            // Consecutive vectors are packed at their own size once the first one is aligned.
            ByteThenTwoVectors two = default;
            Assert.Equal(align, FieldOffset(ref two, ref two.V1));
            Assert.Equal(align + size, FieldOffset(ref two, ref two.V2));

            // Array elements must be strided by the vector's size, and must round-trip their values.
            Vector<int>[] array = new Vector<int>[4];
            Assert.Equal(size, FieldOffset(ref array[0], ref array[1]));

            for (int i = 0; i < array.Length; i++)
            {
                array[i] = new Vector<int>(i + 1);
            }

            for (int i = 0; i < array.Length; i++)
            {
                Assert.Equal(new Vector<int>(i + 1), array[i]);
            }

            // Boxing must round-trip, both directly and when the vector is nested in a struct.
            object boxedVector = new Vector<int>(42);
            Assert.Equal(new Vector<int>(42), (Vector<int>)boxedVector);

            object boxedStruct = new ByteThenVector { B = 7, V = new Vector<int>(5) };
            ByteThenVector unboxed = (ByteThenVector)boxedStruct;
            Assert.Equal(7, unboxed.B);
            Assert.Equal(new Vector<int>(5), unboxed.V);

            return 100;
        }
    }
}
