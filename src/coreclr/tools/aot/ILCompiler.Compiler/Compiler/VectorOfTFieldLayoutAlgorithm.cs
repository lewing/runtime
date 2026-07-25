// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;

using Internal.TypeSystem;

using Debug = System.Diagnostics.Debug;

namespace ILCompiler
{
    /// <summary>
    /// Represents an algorithm that computes field layout for the SIMD Vector&lt;T&gt; type
    /// depending on the target details.
    /// </summary>
    public class VectorOfTFieldLayoutAlgorithm : FieldLayoutAlgorithm
    {
        private readonly FieldLayoutAlgorithm _fallbackAlgorithm;

        public VectorOfTFieldLayoutAlgorithm(FieldLayoutAlgorithm fallbackAlgorithm)
        {
            _fallbackAlgorithm = fallbackAlgorithm;
        }

        public override ComputedInstanceFieldLayout ComputeInstanceLayout(DefType defType, InstanceLayoutKind layoutKind)
        {
            TargetDetails targetDetails = defType.Context.Target;

            ComputedInstanceFieldLayout layoutFromMetadata = _fallbackAlgorithm.ComputeInstanceLayout(defType, layoutKind);
            layoutFromMetadata.IsVectorTOrHasVectorTFields = true;

            LayoutInt instanceFieldSize;

            if (targetDetails.MaximumSimdVectorLength == SimdVectorLength.Vector128Bit)
            {
                instanceFieldSize = new LayoutInt(16);
            }
            else if (targetDetails.MaximumSimdVectorLength == SimdVectorLength.Vector256Bit)
            {
                instanceFieldSize = new LayoutInt(32);
            }
            else if (targetDetails.MaximumSimdVectorLength == SimdVectorLength.Vector512Bit)
            {
                instanceFieldSize = new LayoutInt(64);
            }
            else
            {
                Debug.Assert(targetDetails.MaximumSimdVectorLength == SimdVectorLength.None);

                // Vector<T> keeps its metadata size here, but its alignment still follows the same
                // capped rule the runtime applies, so the two engines agree on targets that report
                // no SIMD vector length (for example LoongArch64 and RiscV64).
                layoutFromMetadata.FieldAlignment = new LayoutInt(
                    VectorFieldLayoutAlgorithm.GetVectorAlignment(layoutFromMetadata.FieldSize.AsInt, targetDetails.Architecture));
                return layoutFromMetadata;
            }

            return new ComputedInstanceFieldLayout
            {
                ByteCountUnaligned = instanceFieldSize,
                ByteCountAlignment = layoutFromMetadata.ByteCountAlignment,
                // Vector<T> is passed like the matching fixed-size intrinsic vector, so its alignment
                // tracks its size the same way (capped by what the target ABI supports) rather than the
                // 8-byte alignment its two-UInt64 metadata layout would otherwise yield.
                FieldAlignment = new LayoutInt(VectorFieldLayoutAlgorithm.GetVectorAlignment(instanceFieldSize.AsInt, targetDetails.Architecture)),
                FieldSize = instanceFieldSize,
                Offsets = layoutFromMetadata.Offsets,
                IsVectorTOrHasVectorTFields = true,
            };
        }

        public override unsafe ComputedStaticFieldLayout ComputeStaticFieldLayout(DefType defType, StaticLayoutKind layoutKind)
        {
            return _fallbackAlgorithm.ComputeStaticFieldLayout(defType, layoutKind);
        }

        public override bool ComputeContainsGCPointers(DefType type)
        {
            return false;
        }

        public override bool ComputeContainsByRefs(DefType type)
        {
            return false;
        }

        public override ValueTypeShapeCharacteristics ComputeValueTypeShapeCharacteristics(DefType type)
        {
            if (type.Context.Target.Architecture == TargetArchitecture.ARM64 &&
                            VectorFieldLayoutAlgorithm.IsSupportedVectorBaseType(type.Instantiation[0]))
            {
                return type.InstanceFieldSize.AsInt switch
                {
                    8 => ValueTypeShapeCharacteristics.Vector64Aggregate,
                    16 => ValueTypeShapeCharacteristics.Vector128Aggregate,
                    _ => ValueTypeShapeCharacteristics.None
                };
            }

            return _fallbackAlgorithm.ComputeValueTypeShapeCharacteristics(type);
        }

        public override bool ComputeIsUnsafeValueType(DefType type)
        {
            return false;
        }

        public static bool IsVectorOfTType(DefType type)
        {
            return type.IsIntrinsic && type.Namespace == "System.Numerics"u8 && type.Name == "Vector`1"u8;
        }
    }
}
