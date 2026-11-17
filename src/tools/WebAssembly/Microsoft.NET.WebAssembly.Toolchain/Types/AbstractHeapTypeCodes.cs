// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Types;

/// <summary>
/// The single mapping from an abstract heap type's 7-bit binary code to its
/// <see cref="HeapTypeKind"/>.
/// </summary>
/// <remarks>
/// <para>This table existed in three hand-maintained copies — the function-body validator, the
/// constant-expression validator, and the runtime cast classifier — and they diverged. The
/// runtime copy spelled <c>noexn</c> as <c>0x68</c>, which is actually <c>cont</c>, so it had no
/// arm for the real <c>noexn</c> code at all and a valid module could drive it into a host
/// exception.</para>
///
/// <para>Two mappings of one thing will diverge, so there is one mapping. Every consumer derives
/// from this table rather than restating it; adding a code here is the only edit a new abstract
/// heap type needs on the decode side, and
/// <c>GcExecutionTests.EveryRecognisedHeapTypeCode_HasARuntimeClassification</c> goes red if the
/// runtime does not gain a matching arm.</para>
///
/// <para>Codes are the low 7 bits of the s33 heap-type immediate. Both <c>0x68</c> and
/// <c>0x65</c> denote <c>cont</c>: the stack-switching proposal renumbered it and nesm accepts
/// the historical spelling.</para>
/// </remarks>
internal static class AbstractHeapTypeCodes
{
    public const byte Func = 0x70;
    public const byte Extern = 0x6F;
    public const byte Any = 0x6E;
    public const byte Eq = 0x6D;
    public const byte I31 = 0x6C;
    public const byte Struct = 0x6B;
    public const byte Array = 0x6A;
    public const byte None = 0x71;
    public const byte NoFunc = 0x73;
    public const byte NoExtern = 0x72;
    public const byte Exn = 0x69;
    public const byte NoExn = 0x74;
    public const byte Cont = 0x68;
    public const byte ContLegacy = 0x65;
    public const byte NoCont = 0x75;

    /// <summary>
    /// Maps a 7-bit abstract heap-type code to its kind, or returns <c>false</c> if the code is
    /// not an abstract heap type.
    /// </summary>
    public static bool TryFromCode(byte code, out HeapTypeKind kind)
    {
        switch (code)
        {
            case Func: kind = HeapTypeKind.Func; return true;
            case Extern: kind = HeapTypeKind.Extern; return true;
            case Any: kind = HeapTypeKind.Any; return true;
            case Eq: kind = HeapTypeKind.Eq; return true;
            case I31: kind = HeapTypeKind.I31; return true;
            case Struct: kind = HeapTypeKind.Struct; return true;
            case Array: kind = HeapTypeKind.Array; return true;
            case None: kind = HeapTypeKind.None; return true;
            case NoFunc: kind = HeapTypeKind.NoFunc; return true;
            case NoExtern: kind = HeapTypeKind.NoExtern; return true;
            case Exn: kind = HeapTypeKind.Exn; return true;
            case NoExn: kind = HeapTypeKind.NoExn; return true;
            case Cont:
            case ContLegacy: kind = HeapTypeKind.Cont; return true;
            case NoCont: kind = HeapTypeKind.NoCont; return true;
            default: kind = default; return false;
        }
    }

    /// <summary>
    /// The inverse of <see cref="TryFromCode"/>: the canonical binary code for a kind.
    /// </summary>
    /// <remarks>
    /// <para>Writers need this direction. Without it a writer restates the table — which is the
    /// three-diverging-copies failure this file exists to prevent, in the encode direction rather
    /// than the decode one.</para>
    ///
    /// <para><c>cont</c> has two accepted codes; the canonical one is <see cref="Cont"/> (0x68),
    /// which is what wasm-tools 1.253.0 emits for <c>contref</c>. <see cref="ContLegacy"/> is
    /// accepted on input only, so a module written by nesm never carries the historical spelling.
    /// Round-tripping the legacy byte therefore normalises it rather than preserving it — an
    /// intentional narrowing, since the two denote the same type.</para>
    /// </remarks>
    public static bool TryToCode(HeapTypeKind kind, out byte code)
    {
        switch (kind)
        {
            case HeapTypeKind.Func: code = Func; return true;
            case HeapTypeKind.Extern: code = Extern; return true;
            case HeapTypeKind.Any: code = Any; return true;
            case HeapTypeKind.Eq: code = Eq; return true;
            case HeapTypeKind.I31: code = I31; return true;
            case HeapTypeKind.Struct: code = Struct; return true;
            case HeapTypeKind.Array: code = Array; return true;
            case HeapTypeKind.None: code = None; return true;
            case HeapTypeKind.NoFunc: code = NoFunc; return true;
            case HeapTypeKind.NoExtern: code = NoExtern; return true;
            case HeapTypeKind.Exn: code = Exn; return true;
            case HeapTypeKind.NoExn: code = NoExn; return true;
            case HeapTypeKind.Cont: code = Cont; return true;
            case HeapTypeKind.NoCont: code = NoCont; return true;
            default: code = default; return false;
        }
    }

    /// <summary>
    /// The two block-type immediates that are <b>not</b> self-contained: <c>(ref null ht)</c>
    /// (0x63) and <c>(ref ht)</c> (0x64) are each followed by an s33 heap-type operand.
    /// </summary>
    /// <remarks>
    /// Every reader that walks past a block type must consume that operand. The switch
    /// interpreter's block-type reader and its pre-scan for matching <c>end</c> offsets both
    /// stopped after the first byte, so a <c>(block (result (ref i31)))</c> left the following
    /// <c>0x6C</c> to be read as an opcode — <c>i32.mul</c> — and the label table mapped the
    /// block to the wrong <c>end</c>. Held here so the three readers share one rule.
    /// </remarks>
    public const long RefNullBlockType = -0x1D;

    /// <inheritdoc cref="RefNullBlockType"/>
    public const long RefBlockType = -0x1C;

    /// <inheritdoc cref="RefNullBlockType"/>
    public static bool BlockTypeCarriesHeapType(long blockType) =>
        blockType is RefNullBlockType or RefBlockType;

    /// <summary>
    /// <see cref="BlockTypeCarriesHeapType"/> for a scanner that has the raw first byte rather
    /// than the decoded value. A one-byte signed LEB with bit 6 set encodes <c>b - 0x80</c>, so
    /// the byte is derived from the same constants instead of being restated as a literal.
    /// </summary>
    public static bool BlockTypeByteCarriesHeapType(byte b) =>
        b == (byte)(RefNullBlockType + 0x80) || b == (byte)(RefBlockType + 0x80);

    /// <summary>
    /// The single authority for the <em>execution storage tag</em> a reference of a given abstract
    /// heap type carries: the <see cref="ValType"/> that a <c>ref.null</c> of that type
    /// materialises under at every boundary able to observe a tag.
    /// </summary>
    /// <remarks>
    /// <para>This judgement was written four times and the copies disagreed (#406). The switch
    /// interpreter's <c>ref.null</c> tagged every GC abstract heap type <c>FuncRef</c>; the
    /// constant-expression evaluator tagged the same types <c>ExternRef</c> from a second switch
    /// spelled in raw hex; <see cref="ValueType.ToStorageValType"/> arrived at <c>ExternRef</c>
    /// through a bare <c>Reference =&gt;</c> fallthrough — a <b>negated exclusion list</b>, so a new
    /// heap-type kind would have joined <c>extern</c> in silence; and the table-dispatch decoder
    /// discarded the answer entirely. The constant-expression answer is the one that survives to an
    /// embedder — a global cell keeps the tag of the value that initialised it and coerces every
    /// later store to that tag — so it is the behaviour retained here, and the other three derive
    /// from it.</para>
    ///
    /// <para><b>This is a representation map, not a type judgement.</b> <see cref="ValType"/> has
    /// four reference codes and WebAssembly has five reference hierarchies, so the GC hierarchy
    /// rooted at <c>any</c> has no code of its own and borrows <c>ExternRef</c>. That does not make
    /// an <c>anyref</c> an <c>externref</c>: no subtyping, casting, or import-matching decision may
    /// be taken from a <see cref="ValType"/>. <c>GcOps.IsRuntimeSubtype</c> answers hierarchy
    /// questions from the runtime value's class and is deliberately independent of this table —
    /// one copy's job is to check the other, and an oracle that delegates to the thing it checks
    /// cannot disagree with it.</para>
    ///
    /// <para>The switch is <b>total</b> over <see cref="HeapTypeKind"/> and throws on a kind it does
    /// not name, so a newly added abstract heap type fails loudly at the first null of that type
    /// rather than being absorbed by whichever arm happens to be last.</para>
    /// </remarks>
    public static ValType StorageTagFor(HeapTypeKind kind) => kind switch
    {
        HeapTypeKind.Func or HeapTypeKind.NoFunc         => ValType.FuncRef,
        HeapTypeKind.Extern or HeapTypeKind.NoExtern     => ValType.ExternRef,
        HeapTypeKind.Exn or HeapTypeKind.NoExn           => ValType.ExnRef,
        HeapTypeKind.Cont or HeapTypeKind.NoCont         => ValType.ContRef,
        HeapTypeKind.Any or HeapTypeKind.Eq or HeapTypeKind.I31
            or HeapTypeKind.Struct or HeapTypeKind.Array
            or HeapTypeKind.None                         => ValType.ExternRef,
        _ => throw new InvalidOperationException(
            $"Abstract heap type {kind} has no execution storage tag."),
    };

    /// <summary>
    /// <see cref="StorageTagFor(HeapTypeKind)"/> for a caller holding the raw 7-bit binary code
    /// rather than a decoded kind.
    /// </summary>
    /// <remarks>
    /// Resolves through <see cref="TryFromCode"/> rather than matching codes directly, so the two
    /// <c>ref.null</c> decoders share both the code table and the tag table. A code the table does
    /// not recognise is a malformed immediate that validation rejects; reaching here with one is a
    /// decoder defect and says so instead of quietly yielding a funcref null.
    /// </remarks>
    public static ValType StorageTagForCode(byte code) =>
        TryFromCode(code, out var kind)
            ? StorageTagFor(kind)
            : throw new InvalidOperationException(
                $"0x{code:X2} is not an abstract heap type and has no execution storage tag.");

    /// <summary>
    /// The storage tag for a <em>defined</em> heap type — the one classification that needs the
    /// defining module's type table, and so the boundary at which it must be resolved.
    /// </summary>
    /// <remarks>
    /// <para><see cref="ValueType.ToStorageValType"/> collapses every <c>DefinedHeapType</c> to
    /// <see cref="ValType.FuncRef"/> because it holds a type index and no type table. That erasure
    /// is structural and cannot be fixed at the type site; it has to be resolved wherever the table
    /// <em>is</em> in reach. Two sites had grown their own copy of this resolution
    /// (<c>WasmRuntime.NullForTypeIndex</c> and <c>ModuleReader.DefinedTypeNullTag</c>); both now
    /// call here, as do the two <c>ref.null</c> decoders.</para>
    ///
    /// <para>When no type table is available at all the index cannot be classified and
    /// <c>FuncRef</c> is retained as the pre-GC default. That is a genuine widening, not a
    /// judgement: a caller that can supply the table must, or a defined struct/array null silently
    /// becomes a funcref null. An index that is out of range <em>with</em> a table supplied is a
    /// different thing — a malformed immediate that validation should already have rejected — and
    /// throws rather than widening. Use <see cref="TryStorageTagForDefinedType"/> from a caller
    /// that legitimately runs before validation. The composite switch is total, so a new composite
    /// kind fails loudly too.</para>
    /// </remarks>
    public static ValType StorageTagForDefinedType(IReadOnlyList<TypeDefinition>? types, long typeIndex)
    {
        if (types is null)
            return ValType.FuncRef;

        return TryStorageTagForDefinedType(types, typeIndex, out var tag)
            ? tag
            : throw new InvalidOperationException(
                $"Defined type index {typeIndex} is out of range (type table has {types.Count} entries) " +
                "and has no execution storage tag.");
    }

    /// <summary>
    /// <see cref="StorageTagForDefinedType"/> for a caller that runs before the module has been
    /// validated, where an out-of-range index is a defect the caller reports separately rather
    /// than an invariant violation.
    /// </summary>
    public static bool TryStorageTagForDefinedType(
        IReadOnlyList<TypeDefinition>? types, long typeIndex, out ValType tag)
    {
        if (types is null || (ulong)typeIndex >= (ulong)types.Count)
        {
            tag = ValType.FuncRef;
            return false;
        }

        var composite = types[(int)typeIndex].CompositeType;
        tag = composite switch
        {
            CompositeType.Struct or CompositeType.Array => ValType.ExternRef,
            CompositeType.Continuation                  => ValType.ContRef,
            CompositeType.Function                      => ValType.FuncRef,
            _ => throw new InvalidOperationException(
                $"Composite type {composite.GetType().Name} has no execution storage tag."),
        };
        return true;
    }

    /// <summary>
    /// The storage tag for a <c>ref.null</c> heap-type immediate: the s33 form both interpreters
    /// and the constant-expression evaluator read off the instruction stream.
    /// </summary>
    /// <remarks>
    /// Non-negative immediates are defined type indices and are resolved against
    /// <paramref name="types"/>; negative immediates carry an abstract code in their low 7 bits.
    /// This is the entry point every <c>ref.null</c> site uses, so the s33-versus-byte distinction
    /// and the defined-versus-abstract split are each made once.
    /// </remarks>
    public static ValType StorageTagForHeapTypeImmediate(long heapType, IReadOnlyList<TypeDefinition>? types) =>
        heapType >= 0
            ? StorageTagForDefinedType(types, heapType)
            : StorageTagForCode((byte)(heapType & 0x7F));

    /// <summary>
    /// Decodes a signed heap-type immediate: non-negative values are defined type indices,
    /// negative values are abstract codes in their low 7 bits.
    /// </summary>
    /// <remarks>
    /// <para>An unrecognised negative code used to fall back to <see cref="HeapTypeKind.Any"/>,
    /// which is <b>fail-open</b>: both validators decode heap types through here, so an invalid
    /// immediate was silently read as <c>anyref</c> and the enclosing module could then validate.
    /// A code the table does not name is a malformed encoding and is reported as one — the same
    /// treatment <c>FunctionBodyValidator.ReadValueType</c> already gives an unknown value-type
    /// byte one layer up (#406).</para>
    ///
    /// <para>Use <see cref="TryFromSigned"/> where the caller wants to report the failure in its
    /// own vocabulary rather than propagate this.</para>
    /// </remarks>
    public static HeapType FromSigned(long heapType) =>
        TryFromSigned(heapType, out var heap)
            ? heap
            : throw new Microsoft.NET.WebAssembly.Binary.WasmFormatException(
                $"Unknown heap type 0x{(byte)(heapType & 0x7F):X2} (s33 {heapType})");

    /// <inheritdoc cref="FromSigned"/>
    public static bool TryFromSigned(long heapType, out HeapType heap)
    {
        if (heapType >= 0)
        {
            heap = new DefinedHeapType(checked((int)heapType));
            return true;
        }

        if (TryFromCode((byte)(heapType & 0x7F), out var kind))
        {
            heap = new AbstractHeapType(kind);
            return true;
        }

        heap = new AbstractHeapType(HeapTypeKind.Any);
        return false;
    }
}
