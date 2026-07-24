// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    /// <summary>
    /// Represents an algorithm that computes field layout for intrinsic vector types (Vector64/Vector128/Vector256).
    /// </summary>
    public class VectorFieldLayoutAlgorithm : FieldLayoutAlgorithm
    {
        private readonly FieldLayoutAlgorithm _fallbackAlgorithm;

        public VectorFieldLayoutAlgorithm(FieldLayoutAlgorithm fallbackAlgorithm)
        {
            _fallbackAlgorithm = fallbackAlgorithm;
        }

        public override ComputedInstanceFieldLayout ComputeInstanceLayout(DefType defType, InstanceLayoutKind layoutKind)
        {
            Debug.Assert(IsVectorType(defType));

            int size;

            if (defType.Name == "Vector64`1"u8)
            {
                size = 8;
            }
            else if (defType.Name == "Vector128`1"u8)
            {
                size = 16;
            }
            else if (defType.Name == "Vector256`1"u8)
            {
                size = 32;
            }
            else
            {
                Debug.Assert(defType.Name == "Vector512`1"u8);
                size = 64;
            }

            LayoutInt alignment = new LayoutInt(GetVectorAlignment(size, defType.Context.Target.Architecture));

            ComputedInstanceFieldLayout layoutFromMetadata = _fallbackAlgorithm.ComputeInstanceLayout(defType, layoutKind);

            return new ComputedInstanceFieldLayout
            {
                ByteCountUnaligned = layoutFromMetadata.ByteCountUnaligned,
                ByteCountAlignment = layoutFromMetadata.ByteCountAlignment,
                FieldAlignment = alignment,
                FieldSize = layoutFromMetadata.FieldSize,
                Offsets = layoutFromMetadata.Offsets,
                LayoutAbiStable = true
            };
        }

        /// <summary>
        /// The alignment of a SIMD vector is its size, capped by the maximum vector alignment the
        /// target ABI supports. This single rule reproduces the historical per-type/per-architecture
        /// values and mirrors the runtime's GetSimdVectorAlignment in methodtablebuilder.cpp.
        /// </summary>
        internal static int GetVectorAlignment(int size, TargetArchitecture architecture)
        {
            int cap = architecture switch
            {
                TargetArchitecture.ARM => 8,          // PCS aligns __m128 at 8; defines no larger vector
                TargetArchitecture.ARM64 => 16,       // PCS (with SVE): 16-byte aligned
                TargetArchitecture.LoongArch64 => 16, // TODO: revisit when LoongArch64 intrinsics land
                TargetArchitecture.RiscV64 => 16,     // TODO: revisit when RISC-V intrinsics land
                TargetArchitecture.Wasm32 => 16,      // single 16-byte v128
                _ => 64,                              // x86 / x64: alignment == size
            };

            return Math.Min(size, cap);
        }

        public override ComputedStaticFieldLayout ComputeStaticFieldLayout(DefType defType, StaticLayoutKind layoutKind)
        {
            return _fallbackAlgorithm.ComputeStaticFieldLayout(defType, layoutKind);
        }

        public override bool ComputeContainsGCPointers(DefType type)
        {
            Debug.Assert(!_fallbackAlgorithm.ComputeContainsGCPointers(type));
            return false;
        }

        public override bool ComputeContainsByRefs(DefType type)
        {
            Debug.Assert(!_fallbackAlgorithm.ComputeContainsByRefs(type));
            return false;
        }

        public override bool ComputeIsUnsafeValueType(DefType type)
        {
            Debug.Assert(!_fallbackAlgorithm.ComputeIsUnsafeValueType(type));
            return false;
        }

        public override ValueTypeShapeCharacteristics ComputeValueTypeShapeCharacteristics(DefType type)
        {
            if (type.Context.Target.Architecture == TargetArchitecture.ARM64 &&
                IsSupportedVectorBaseType(type.Instantiation[0]))
            {
                return type.InstanceFieldSize.AsInt switch
                {
                    8 => ValueTypeShapeCharacteristics.Vector64Aggregate,
                    16 => ValueTypeShapeCharacteristics.Vector128Aggregate,
                    32 => ValueTypeShapeCharacteristics.Vector128Aggregate,
                    64 => ValueTypeShapeCharacteristics.Vector128Aggregate,
                    _ => ValueTypeShapeCharacteristics.None
                };
            }
            return ValueTypeShapeCharacteristics.None;
        }

        public static bool IsVectorType(DefType type)
        {
            return type.IsIntrinsic &&
                type.Namespace == "System.Runtime.Intrinsics"u8 &&
                (type.Name == "Vector64`1"u8 ||
                 type.Name == "Vector128`1"u8 ||
                 type.Name == "Vector256`1"u8 ||
                 type.Name == "Vector512`1"u8);
        }

        /// <summary>
        /// Determines whether <paramref name="elementType"/> is supported as the base (element)
        /// type of an intrinsic vector, mirroring the set the JIT recognizes as a SIMD base type.
        /// </summary>
        public static bool IsSupportedVectorBaseType(TypeDesc elementType)
        {
            return elementType.IsPrimitiveNumeric;
        }
    }
}
