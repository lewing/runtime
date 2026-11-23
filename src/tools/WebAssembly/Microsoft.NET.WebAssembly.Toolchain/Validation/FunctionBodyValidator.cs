// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Binary.Writing;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using ExactValueType = Microsoft.NET.WebAssembly.Types.ValueType;

/// <summary>
/// Direct implementation of the WebAssembly function-body operand/control stack
/// algorithm. PR 3 intentionally exposes this as an internal test surface only;
/// public module loading is not wired to it until the exhaustive validator lands.
/// </summary>
internal static class FunctionBodyValidator
{
    // Stack-switching validation follows WebAssembly/stack-switching@c7be390539e5fc10669c88cadc4fa4b32fba1d47.
    // The function-type fallback preserves nesm's current interpreter encoding, where continuation
    // type indices are projected through the compact FuncType table until activation fixes the model.
    private static readonly ExactValueType I32 = new ExactValueType.Number(NumberType.I32);
    private static readonly ExactValueType I64 = new ExactValueType.Number(NumberType.I64);
    private static readonly ExactValueType F32 = new ExactValueType.Number(NumberType.F32);
    private static readonly ExactValueType F64 = new ExactValueType.Number(NumberType.F64);
    private static readonly ExactValueType V128 = new ExactValueType.Vector(VectorType.V128);
    private static readonly ExactValueType ExnRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Exn));
    private static readonly ExactValueType NonNullableExnRef = new ExactValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.Exn));
    private static readonly ExactValueType EqRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Eq));
    private static readonly ExactValueType AnyRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Any));
    private static readonly ExactValueType ExternRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Extern));
    private static readonly ExactValueType I31Ref = new ExactValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.I31));
    private static readonly ExactValueType NullableI31Ref = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.I31));
    private static readonly ExactValueType ArrayRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Array));
    private static readonly IReadOnlyDictionary<byte, SignatureRule> NumericRules = BuildNumericRules();
    private static readonly IReadOnlyDictionary<uint, SignatureRule> FcSignatureRules = BuildFcSignatureRules();
    private static readonly IReadOnlySet<uint> GcRuleOpcodes = new HashSet<uint>(Enumerable.Range(0, 0x1F).Select(i => (uint)i));
    private static readonly IReadOnlyDictionary<uint, SignatureRule> SimdSignatureRules = BuildSimdSignatureRules();
    private static readonly IReadOnlyDictionary<uint, SignatureRule> RelaxedSimdSignatureRules = BuildRelaxedSimdSignatureRules();
    private static readonly IReadOnlySet<uint> SimdMemoryRules = new HashSet<uint>(
        Enumerable.Range(0x00, 0x0C).Select(i => (uint)i)
            .Concat(Enumerable.Range(0x54, 0x0A).Select(i => (uint)i)));

    // Dense lookup projections of the dictionaries above. They are built *from* those
    // dictionaries, so the accepted key set is identical by construction; only the probe
    // cost differs (array index instead of an interface-dispatched hash lookup).
    private static readonly SignatureRule?[] NumericRuleTable = BuildRuleTable(NumericRules);
    private static readonly SignatureRule?[] FcRuleTable = BuildRuleTable(FcSignatureRules);
    private static readonly SignatureRule?[] SimdRuleTable = BuildRuleTable(SimdSignatureRules);
    private static readonly SignatureRule?[] RelaxedSimdRuleTable = BuildRuleTable(RelaxedSimdSignatureRules);

    private static SignatureRule?[] BuildRuleTable<TKey>(IReadOnlyDictionary<TKey, SignatureRule> rules)
        where TKey : struct, IConvertible
    {
        uint max = 0;
        foreach (var key in rules.Keys)
            max = Math.Max(max, key.ToUInt32(null));
        var table = new SignatureRule?[max + 1];
        foreach (var pair in rules)
            table[pair.Key.ToUInt32(null)] = pair.Value;
        return table;
    }

    private static SignatureRule? LookupRule(SignatureRule?[] table, uint key)
        => key < (uint)table.Length ? table[key] : null;

    internal static Nullability GcCastNullability(uint subOpcode)
        => subOpcode switch
        {
            0x14 => Nullability.NonNullable,
            0x15 => Nullability.Nullable,
            0x16 => Nullability.NonNullable,
            0x17 => Nullability.Nullable,
            _ => throw new ArgumentOutOfRangeException(nameof(subOpcode)),
        };

    /// <summary>
    /// Array-probe equivalent of the dictionary indexer, including its
    /// <see cref="KeyNotFoundException"/> on a miss.
    /// </summary>
    /// <remarks>
    /// Deliberately non-generic in the key type. An earlier generic form converted the probe
    /// key with <c>Convert.ChangeType</c>, which can throw on its own — an
    /// <see cref="OverflowException"/> for a key above 255 against a <c>byte</c>-keyed
    /// dictionary — <i>before</i> the dictionary is ever consulted. That made the
    /// "same exception as the indexer" guarantee true only by convention, holding for exactly
    /// as long as every call site happened to be <c>uint</c>-keyed. Fixing the key type at
    /// <c>uint</c> deletes the conversion, so the miss path is the indexer's throw and nothing
    /// else. The one <c>byte</c>-keyed table (numeric rules) is probed through
    /// <see cref="LookupRule"/>, which carries its own explicit miss diagnostic and needs no
    /// dictionary fallback.
    /// </remarks>
    private static SignatureRule RequireRule(
        SignatureRule?[] table, IReadOnlyDictionary<uint, SignatureRule> fallback, uint key)
        => LookupRule(table, key) ?? fallback[key];

    public static WasmValidationResult ValidateModule(WasmModule module, WasmFeatures features = WasmFeatures.Default)
        => ValidateModule(module, features, prevalidated: null);

    /// <summary>
    /// Validates every function body, taking the result for each body <paramref name="prevalidated"/> recorded while
    /// the module was decoded, and walking only the bodies it could not decide.
    /// </summary>
    internal static WasmValidationResult ValidateModule(WasmModule module, WasmFeatures features, FusedBodyValidation? prevalidated)
    {
        if (prevalidated is not null && prevalidated.FunctionCount != module.Functions.Count)
            prevalidated = null;

        var diagnostics = new List<WasmValidationDiagnostic>();
        ModuleValidationContext? context = null;
        FunctionValidator? validator = null;
        for (int definedIndex = 0; definedIndex < module.Functions.Count; definedIndex++)
        {
            if (prevalidated is null || !prevalidated.TryGetResult(definedIndex, out var diagnostic))
            {
                validator ??= new FunctionValidator(context ??= new ModuleValidationContext(module), features);
                diagnostic = validator.Validate(definedIndex);
            }

            if (diagnostic is not null)
                diagnostics.Add(diagnostic);
        }

        return diagnostics.Count == 0
            ? WasmValidationResult.Valid
            : new WasmValidationResult(diagnostics);
    }

    /// <summary>
    /// The static operand-stack types of a function body immediately before its
    /// <paramref name="insnIndex"/>-th instruction (the debugger's decoded-instruction PC), bottom
    /// first; <c>null</c> entries are polymorphic. Returns <c>null</c> when the types cannot be
    /// derived — callers must then report the slots as untyped rather than guess.
    /// </summary>
    internal static ExactValueType?[]? OperandTypesBefore(WasmModule module, WasmFunction function, byte[] body, int insnIndex)
    {
        int definedIndex = module.Functions.IndexOf(function);
        if (definedIndex < 0 || insnIndex < 0)
            return null;
        var validator = new FunctionValidator(new ModuleValidationContext(module), WasmFeatures.All);
        return validator.OperandTypesBefore(definedIndex, body, insnIndex);
    }

    internal static IReadOnlyCollection<OpcodeId> ImplementedOpcodeRules(InstructionOpcodeBucket bucket)
    {
        return bucket switch
        {
            InstructionOpcodeBucket.Primary => PrimaryRuleOpcodes()
                .Concat(NumericRules.Keys.Select(op => new OpcodeId(op)))
                .OrderBy(id => id.Opcode).ThenBy(id => id.SubOpcode).ToArray(),
            InstructionOpcodeBucket.Fc => FcSignatureRules.Keys.Select(sub => new OpcodeId(WasmOpcode.MiscPrefix, sub))
                .Concat(Enumerable.Range(8, 10).Select(sub => new OpcodeId(WasmOpcode.MiscPrefix, (uint)sub)))
                .OrderBy(id => id.SubOpcode).ToArray(),
            InstructionOpcodeBucket.FbGc => GcRuleOpcodes.Select(sub => new OpcodeId(WasmOpcode.GcPrefix, sub))
                .OrderBy(id => id.SubOpcode).ToArray(),
            InstructionOpcodeBucket.FdSimd => SimdSignatureRules.Keys.Select(sub => new OpcodeId(WasmOpcode.SimdPrefix, sub))
                .Concat(SimdMemoryRules.Select(sub => new OpcodeId(WasmOpcode.SimdPrefix, sub)))
                .OrderBy(id => id.SubOpcode).ToArray(),
            InstructionOpcodeBucket.FdRelaxedSimd => RelaxedSimdSignatureRules.Keys.Select(sub => new OpcodeId(WasmOpcode.SimdPrefix, sub))
                .OrderBy(id => id.SubOpcode).ToArray(),
            InstructionOpcodeBucket.E0StackSwitching => Enumerable.Range(0, 5)
                .Select(sub => new OpcodeId(0xE0, (uint)sub)).ToArray(),
            _ => Array.Empty<OpcodeId>(),
        };
    }

    private sealed class ModuleValidationContext
    {
        private readonly WasmModule _module;
        private readonly List<int> _functionTypeIndices = [];
        private readonly List<int> _tagTypeIndices = [];
        private readonly List<MemoryType> _memoryTypes = [];
        private readonly List<TableType> _tableTypes = [];
        private readonly List<GlobalType> _globalTypes = [];
        private readonly HashSet<int> _declaredFunctionReferences = [];
        private readonly FunctionSignature?[] _functionSignatures;

        private readonly int _definedFunctionCount;
        private readonly uint? _dataCount;
        private readonly bool _deferDataIndices;

        public ModuleValidationContext(WasmModule module)
            : this(module, definedFunctionTypeIndices: null, dataCount: null)
        {
        }

        /// <summary>
        /// A context for validating bodies while the code section is decoded, when the function section's type
        /// indices are known but <see cref="WasmModule.Functions"/> is still being filled in, and the data section
        /// has not been read: data indices are checked against the data count section, which must agree with the
        /// data section once it is read. Without one, a data index is not decided here (see
        /// <see cref="DeferredBodyException"/>).
        /// </summary>
        public ModuleValidationContext(WasmModule module, IReadOnlyList<int>? definedFunctionTypeIndices, uint? dataCount)
        {
            _module = module;
            _dataCount = dataCount;
            _deferDataIndices = definedFunctionTypeIndices is not null && dataCount is null;
            _definedFunctionCount = definedFunctionTypeIndices?.Count ?? module.Functions.Count;
            _functionSignatures = new FunctionSignature?[module.Types.Count];
            for (int typeIndex = 0; typeIndex < _functionSignatures.Length; typeIndex++)
            {
                if (module.TypeDefinitions.Count == 0)
                {
                    var compact = module.Types[typeIndex];
                    _functionSignatures[typeIndex] = new FunctionSignature(
                        compact.Parameters.Select(FromStorage).ToArray(),
                        compact.Results.Select(FromStorage).ToArray());
                }
                else if (typeIndex < module.TypeDefinitions.Count &&
                         module.TypeDefinitions[typeIndex].CompositeType is CompositeType.Function function)
                {
                    _functionSignatures[typeIndex] =
                        new FunctionSignature(function.Parameters.ToArray(), function.Results.ToArray());
                }
            }

            SubtypeRelation = new SubtypeRelation(module.TypeDefinitions);
            foreach (var import in module.Imports)
            {
                switch (import.Descriptor)
                {
                    case ImportDescriptor.Func func:
                        _functionTypeIndices.Add(func.TypeIndex);
                        break;
                    case ImportDescriptor.Memory memory:
                        _memoryTypes.Add(memory.Type);
                        break;
                    case ImportDescriptor.Table table:
                        _tableTypes.Add(table.Type);
                        break;
                    case ImportDescriptor.Global global:
                        _globalTypes.Add(global.Type);
                        break;
                    case ImportDescriptor.Tag tag:
                        _tagTypeIndices.Add(tag.TypeIndex);
                        break;
                }
            }

            if (definedFunctionTypeIndices is not null)
            {
                foreach (int typeIndex in definedFunctionTypeIndices)
                    _functionTypeIndices.Add(typeIndex);
            }
            else
            {
                foreach (var function in module.Functions)
                    _functionTypeIndices.Add(function.TypeIndex);
            }

            foreach (var memory in module.Memories)
                _memoryTypes.Add(memory);
            foreach (var table in module.Tables)
                _tableTypes.Add(table);
            foreach (var global in module.Globals)
                _globalTypes.Add(global.Type);
            foreach (var tag in module.Tags)
                _tagTypeIndices.Add(tag.TypeIndex);

            foreach (var export in module.Exports.Values)
            {
                if (export.Descriptor is ExportDescriptor.Func function && IsFunctionIndex(function.FunctionIndex))
                    _declaredFunctionReferences.Add(function.FunctionIndex);
            }

            foreach (var element in module.Elements)
            {
                var declarations = element.DeclaredFunctionIndices.Count != 0 ? element.DeclaredFunctionIndices : element.FunctionIndices;
                foreach (int functionIndex in declarations)
                {
                    if (IsFunctionIndex(functionIndex))
                        _declaredFunctionReferences.Add(functionIndex);
                }
            }

            foreach (var global in module.Globals)
            {
                if (TryReadRefFuncIndex(global.InitExpr, out int functionIndex) && IsFunctionIndex(functionIndex))
                    _declaredFunctionReferences.Add(functionIndex);
            }
        }

        public int ImportedFunctionCount => _functionTypeIndices.Count - _definedFunctionCount;

        public List<TypeDefinition> TypeDefinitions => _module.TypeDefinitions;

        public SubtypeRelation SubtypeRelation { get; }

        public WasmFunction DefinedFunction(int definedIndex) => _module.Functions[definedIndex];

        public MemoryType? Memory(uint index) => index < _memoryTypes.Count ? _memoryTypes[(int)index] : null;

        public TableType? Table(uint index) => index < _tableTypes.Count ? _tableTypes[(int)index] : null;

        public GlobalType? Global(uint index) => index < _globalTypes.Count ? _globalTypes[(int)index] : null;

        public bool HasDataSegment(uint index)
        {
            if (_dataCount is { } count)
                return index < count;
            if (_deferDataIndices)
                throw new DeferredBodyException();
            return index < (uint)_module.DataSegments.Count;
        }

        public Element? ElementSegment(uint index) => index < _module.Elements.Count ? _module.Elements[(int)index] : null;

        public FunctionSignature? FunctionTypeByAbsoluteIndex(uint index)
        {
            if (index >= _functionTypeIndices.Count)
                return null;
            return FunctionTypeByTypeIndex(_functionTypeIndices[(int)index]);
        }

        public int? FunctionTypeIndexByAbsoluteIndex(uint index)
            => index < _functionTypeIndices.Count ? _functionTypeIndices[(int)index] : null;

        public bool IsDeclaredFunctionReference(uint index)
            => index <= int.MaxValue && _declaredFunctionReferences.Contains((int)index);

        public FunctionSignature? FunctionTypeByTypeIndex(int typeIndex)
        {
            return typeIndex >= 0 && typeIndex < _functionSignatures.Length
                ? _functionSignatures[typeIndex]
                : null;
        }

        public FunctionSignature? FunctionTypeByTypeIndex(uint typeIndex)
            => typeIndex > int.MaxValue ? null : FunctionTypeByTypeIndex((int)typeIndex);

        public FunctionSignature? TagType(uint index)
        {
            if (index >= _tagTypeIndices.Count)
                return null;
            return FunctionTypeByTypeIndex(_tagTypeIndices[(int)index]);
        }

        public BlockSignature? BlockSignature(DecodedInstruction instruction)
        {
            long blockType = instruction.BlockType;
            if (blockType == -0x40)
                return new BlockSignature([], []);
            if (blockType >= 0)
            {
                var function = FunctionTypeByTypeIndex(checked((int)blockType));
                return function is null ? null : new BlockSignature(function.Parameters, function.Results);
            }

            ExactValueType? type = blockType switch
            {
                -0x1D => new ExactValueType.Reference(Nullability.Nullable, HeapTypeFromSigned(instruction.HeapType0)),
                -0x1C => new ExactValueType.Reference(Nullability.NonNullable, HeapTypeFromSigned(instruction.HeapType0)),
                _ => ValueTypeFromSignedByte(blockType),
            };
            return type is null || !IsValidValueType(type) ? null : new BlockSignature([], [type]);
        }

        private bool IsValidValueType(ExactValueType type)
            => type is not ExactValueType.Reference reference ||
               reference.HeapType is not DefinedHeapType defined ||
               (defined.TypeIndex >= 0 && defined.TypeIndex < _module.TypeDefinitions.Count);

        private bool IsFunctionIndex(int index)
            => index >= 0 && index < _functionTypeIndices.Count;

        private static bool TryReadRefFuncIndex(ReadOnlySpan<byte> expression, out int functionIndex)
        {
            functionIndex = -1;
            if (expression.Length < 3 || expression[0] != WasmOpcode.RefFunc)
                return false;

            int offset = 1;
            uint value = WasmLeb128.ReadU32(expression, ref offset);
            if (offset >= expression.Length || expression[offset] != 0x0B || value > int.MaxValue)
                return false;

            functionIndex = (int)value;
            return true;
        }
    }

    private sealed class FunctionValidator
    {
        private readonly ModuleValidationContext _context;
        private readonly WasmFeatures _features;
        private readonly SubtypeRelation _subtype;
        private readonly List<StackValue> _values = [];
        private readonly List<ControlFrame> _controls = [];
        private readonly List<ControlFrame> _controlFramePool = [];
        private readonly List<int> _initStack = [];
        private readonly List<ExactValueType> _locals = [];
        private bool[] _initialized = [];

        private int _absoluteFunctionIndex;
        private WasmFunction _function = null!;
        private int _controlFramePoolCount;
        private int _lastOffset;
        private OpcodeId? _lastOpcode;

        public FunctionValidator(ModuleValidationContext context, WasmFeatures features)
        {
            _context = context;
            _features = features;
            _subtype = context.SubtypeRelation;
        }

        private void Initialize(int definedFunctionIndex)
            => Initialize(definedFunctionIndex, _context.DefinedFunction(definedFunctionIndex));

        internal void Initialize(int definedFunctionIndex, WasmFunction function)
        {
            _absoluteFunctionIndex = _context.ImportedFunctionCount + definedFunctionIndex;
            _function = function;
            _values.Clear();
            _controls.Clear();
            _controlFramePoolCount = 0;
            _initStack.Clear();
            _locals.Clear();
            _lastOffset = 0;
            _lastOpcode = null;
            var signature = _context.FunctionTypeByTypeIndex(_function.TypeIndex)
                ?? new FunctionSignature([], []);
            _locals.AddRange(signature.Parameters);
            if (_function.ExactLocals.Count == _function.Locals.Count)
            {
                _locals.AddRange(_function.ExactLocals);
            }
            else
            {
                foreach (var local in _function.Locals)
                    _locals.Add(FromStorage(local));
            }

            if (_initialized.Length < _locals.Count)
                Array.Resize(ref _initialized, _locals.Count);
            Array.Clear(_initialized);
            for (int i = 0; i < signature.Parameters.Length; i++)
                _initialized[i] = true;
            for (int i = signature.Parameters.Length; i < _locals.Count; i++)
            {
                if (IsDefaultable(_locals[i]))
                    _initialized[i] = true;
            }

            _controls.Add(RentControlFrame(
                ControlKind.Function,
                startTypes: [],
                endTypes: signature.Results,
                branchTypes: signature.Results,
                valueHeight: 0,
                initHeight: _initStack.Count));
        }

        /// <summary>
        /// Validates <paramref name="body"/> up to (not including) its <paramref name="insnIndex"/>-th
        /// instruction and returns the static operand stack at that point, bottom first. A
        /// polymorphic (post-<c>unreachable</c>) slot is <c>null</c>. Returns <c>null</c> when the
        /// prefix does not validate or has fewer instructions than requested.
        /// </summary>
        public ExactValueType?[]? OperandTypesBefore(int definedFunctionIndex, byte[] body, int insnIndex)
        {
            Initialize(definedFunctionIndex);
            try
            {
                var cursor = new InstructionCursor(body, _features);
                for (int applied = 0; applied < insnIndex; applied++)
                {
                    if (_controls.Count == 0 || !cursor.MoveNext(out var instruction))
                        return null;
                    _lastOffset = instruction.ByteOffset;
                    _lastOpcode = instruction.OpcodeId;
                    Apply(instruction);
                }

                var types = new ExactValueType?[_values.Count];
                for (int i = 0; i < types.Length; i++)
                    types[i] = _values[i].Type;
                return types;
            }
            catch (ValidationFailure)
            {
                return null;
            }
        }

        public WasmValidationDiagnostic? Validate(int definedFunctionIndex)
        {
            Initialize(definedFunctionIndex);
            try
            {
                var cursor = new InstructionCursor(_function.Body, _features);
                while (cursor.MoveNext(out var instruction))
                {
                    if (Step(instruction, cursor.Position))
                        return null;
                }

                throw MissingFinalEnd();
            }
            catch (ValidationFailure failure)
            {
                return ToDiagnostic(failure);
            }
        }

        /// <summary>
        /// Applies one instruction; <paramref name="nextPosition"/> is where the next one starts. Returns true once the
        /// function's own <c>end</c> has been applied, which must be the body's last byte.
        /// </summary>
        internal bool Step(DecodedInstruction instruction, int nextPosition)
        {
            _lastOffset = instruction.ByteOffset;
            _lastOpcode = instruction.OpcodeId;
            Apply(instruction);

            if (_controls.Count != 0)
                return false;
            if (nextPosition != _function.Body.Length)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "Unexpected bytes after function end");
            return true;
        }

        internal static ValidationFailure MissingFinalEnd()
            => Failure(WasmValidationCode.FunctionBodyControlMismatch, "Function body is missing final end");

        internal WasmValidationDiagnostic ToDiagnostic(ValidationFailure failure)
            => new(failure.Code,
                $"function {_absoluteFunctionIndex} offset 0x{_lastOffset:X}: {_lastOpcode?.ToString() ?? ""}: {failure.Message}");

        private void Apply(DecodedInstruction instruction)
        {
            switch (instruction.OpcodeId.Opcode)
            {
                case 0x00:
                    MarkUnreachable();
                    break;
                case 0x01:
                    break;
                case 0x02:
                    EnterBlock(ControlKind.Block, instruction);
                    break;
                case 0x03:
                    EnterBlock(ControlKind.Loop, instruction);
                    break;
                case 0x04:
                    Pop(I32);
                    EnterBlock(ControlKind.If, instruction);
                    break;
                case 0x05:
                    BeginElse();
                    break;
                case 0x06:
                    EnterBlock(ControlKind.Try, instruction);
                    break;
                case 0x07:
                    BeginCatch(instruction.Index0);
                    break;
                case 0x08:
                    Throw(instruction.Index0);
                    break;
                case 0x09:
                    Rethrow(instruction.Index0);
                    break;
                case 0x0A:
                    ThrowRef();
                    break;
                case 0x18:
                    Delegate(instruction.Index0);
                    break;
                case 0x1F:
                    EnterTryTable(instruction);
                    break;
                case 0x19:
                    BeginCatchAll();
                    break;
                case 0x0B:
                    EndControl();
                    break;
                case 0x0C:
                    Branch(instruction.Index0);
                    break;
                case 0x0D:
                    BranchIf(instruction.Index0);
                    break;
                case 0x0E:
                    BranchTable(instruction);
                    break;
                case 0x0F:
                    Return();
                    break;
                case 0x1A:
                    Drop();
                    break;
                case 0x1B:
                    SelectUntyped();
                    break;
                case 0x1C:
                    SelectTyped(instruction);
                    break;
                case 0x10:
                    Call(instruction.Index0, tail: false);
                    break;
                case 0x11:
                    CallIndirect(instruction.Index0, instruction.Index1, tail: false);
                    break;
                case 0x12:
                    Call(instruction.Index0, tail: true);
                    break;
                case 0x13:
                    CallIndirect(instruction.Index0, instruction.Index1, tail: true);
                    break;
                case 0x14:
                    CallRef(instruction.Index0, tail: false);
                    break;
                case 0x15:
                    CallRef(instruction.Index0, tail: true);
                    break;
                case 0x20:
                    LocalGet(instruction.Index0);
                    break;
                case 0x21:
                    LocalSet(instruction.Index0, tee: false);
                    break;
                case 0x22:
                    LocalSet(instruction.Index0, tee: true);
                    break;
                case 0x23:
                    GlobalGet(instruction.Index0);
                    break;
                case 0x24:
                    GlobalSet(instruction.Index0);
                    break;
                case 0x25:
                    TableGet(instruction.Index0);
                    break;
                case 0x26:
                    TableSet(instruction.Index0);
                    break;
                case >= 0x28 and <= 0x35:
                    ScalarLoad(instruction);
                    break;
                case >= 0x36 and <= 0x3E:
                    ScalarStore(instruction);
                    break;
                case 0x3F:
                    MemorySize(instruction.Index0);
                    break;
                case 0x40:
                    MemoryGrow(instruction.Index0);
                    break;
                case 0x41:
                    Push(I32);
                    break;
                case 0x42:
                    Push(I64);
                    break;
                case 0x43:
                    Push(F32);
                    break;
                case 0x44:
                    Push(F64);
                    break;
                case WasmOpcode.MiscPrefix:
                    ApplyFc(instruction);
                    break;
                case WasmOpcode.GcPrefix:
                    ApplyFb(instruction);
                    break;
                case WasmOpcode.SimdPrefix:
                    ApplyFd(instruction);
                    break;
                case 0xE0:
                    ApplyE0(instruction);
                    break;
                case 0xD0:
                    Push(CastType(instruction.HeapType0, Nullability.Nullable));
                    break;
                case 0xD1:
                    RefIsNull();
                    break;
                case 0xD2:
                    RefFunc(instruction.Index0);
                    break;
                case 0xD3:
                    RefEq();
                    break;
                case 0xD4:
                    RefAsNonNull();
                    break;
                case 0xD5:
                    BrOnNull(instruction.Index0);
                    break;
                case 0xD6:
                    BrOnNonNull(instruction.Index0);
                    break;
                case >= 0x45 and <= 0xC4:
                    ApplyNumeric(instruction.OpcodeId.Opcode);
                    break;
                default:
                    throw Missing(instruction);
            }
        }

        private void ApplyFc(DecodedInstruction instruction)
        {
            switch (instruction.OpcodeId.SubOpcode)
            {
                case <= 7:
                case >= 19 and <= 22:
                    ApplySignature(RequireRule(FcRuleTable, FcSignatureRules, instruction.OpcodeId.SubOpcode));
                    return;
                case 8:
                    MemoryInit(instruction.Index0, instruction.Index1);
                    return;
                case 9:
                    DataDrop(instruction.Index0);
                    return;
                case 10:
                    MemoryCopy(instruction.Index0, instruction.Index1);
                    return;
                case 11:
                    MemoryFill(instruction.Index0);
                    return;
                case 12:
                    TableInit(instruction.Index0, instruction.Index1);
                    return;
                case 13:
                    ElemDrop(instruction.Index0);
                    return;
                case 14:
                    TableCopy(instruction.Index0, instruction.Index1);
                    return;
                case 15:
                    TableGrow(instruction.Index0);
                    return;
                case 16:
                    TableSize(instruction.Index0);
                    return;
                case 17:
                    TableFill(instruction.Index0);
                    return;
                default:
                    throw Missing(instruction);
            }
        }

        private void ApplyFb(DecodedInstruction instruction)
        {
            switch (instruction.OpcodeId.SubOpcode)
            {
                case 0x00:
                    StructNew(instruction.Index0, defaultValues: false);
                    return;
                case 0x01:
                    StructNew(instruction.Index0, defaultValues: true);
                    return;
                case 0x02:
                    StructGet(instruction.Index0, instruction.Index1, signed: null);
                    return;
                case 0x03:
                    StructGet(instruction.Index0, instruction.Index1, signed: true);
                    return;
                case 0x04:
                    StructGet(instruction.Index0, instruction.Index1, signed: false);
                    return;
                case 0x05:
                    StructSet(instruction.Index0, instruction.Index1);
                    return;
                case 0x06:
                    ArrayNew(instruction.Index0, defaultValues: false);
                    return;
                case 0x07:
                    ArrayNew(instruction.Index0, defaultValues: true);
                    return;
                case 0x08:
                    ArrayNewFixed(instruction.Index0, instruction.Index1);
                    return;
                case 0x09:
                    ArrayNewData(instruction.Index0, instruction.Index1);
                    return;
                case 0x0A:
                    ArrayNewElem(instruction.Index0, instruction.Index1);
                    return;
                case 0x0B:
                    ArrayGet(instruction.Index0, signed: null);
                    return;
                case 0x0C:
                    ArrayGet(instruction.Index0, signed: true);
                    return;
                case 0x0D:
                    ArrayGet(instruction.Index0, signed: false);
                    return;
                case 0x0E:
                    ArraySet(instruction.Index0);
                    return;
                case 0x0F:
                    ArrayLen();
                    return;
                case 0x10:
                    ArrayFill(instruction.Index0);
                    return;
                case 0x11:
                    ArrayCopy(instruction.Index0, instruction.Index1);
                    return;
                case 0x12:
                    ArrayInitData(instruction.Index0, instruction.Index1);
                    return;
                case 0x13:
                    ArrayInitElem(instruction.Index0, instruction.Index1);
                    return;
                case 0x14:
                    RefTest(instruction.HeapType0, GcCastNullability(instruction.OpcodeId.SubOpcode));
                    return;
                case 0x15:
                    RefTest(instruction.HeapType0, GcCastNullability(instruction.OpcodeId.SubOpcode));
                    return;
                case 0x16:
                    RefCast(instruction.HeapType0, GcCastNullability(instruction.OpcodeId.SubOpcode));
                    return;
                case 0x17:
                    RefCast(instruction.HeapType0, GcCastNullability(instruction.OpcodeId.SubOpcode));
                    return;
                case 0x18:
                    BrOnCast(instruction, fail: false);
                    return;
                case 0x19:
                    BrOnCast(instruction, fail: true);
                    return;
                case 0x1A:
                    AnyConvertExtern();
                    return;
                case 0x1B:
                    ExternConvertAny();
                    return;
                case 0x1C:
                    RefI31();
                    return;
                case 0x1D:
                case 0x1E:
                    I31Get();
                    return;
                default:
                    throw Missing(instruction);
            }
        }

        private void ApplyFd(DecodedInstruction instruction)
        {
            uint sub = instruction.OpcodeId.SubOpcode;
            if (sub <= 0xFF)
            {
                ApplySimd(instruction, sub);
                return;
            }

            var relaxedRule = LookupRule(RelaxedSimdRuleTable, sub);
            if (relaxedRule is null)
                throw Missing(instruction);
            ApplySignature(relaxedRule);
        }

        private void ApplyE0(DecodedInstruction instruction)
        {
            switch (instruction.OpcodeId.SubOpcode)
            {
                case 0x00:
                    ContNew(instruction.Index0);
                    return;
                case 0x01:
                    ContBind(instruction.Index0, instruction.Index1);
                    return;
                case 0x02:
                    Resume(instruction);
                    return;
                case 0x03:
                    Suspend(instruction.Index0);
                    return;
                case 0x04:
                    Switch(instruction.Index0, instruction.Index1);
                    return;
                default:
                    throw Missing(instruction);
            }
        }

        private void ApplySimd(DecodedInstruction instruction, uint sub)
        {
            switch (sub)
            {
                case <= 0x0A:
                case 0x5C:
                case 0x5D:
                    SimdLoad(instruction, sub);
                    return;
                case 0x0B:
                    SimdStore(instruction, sub);
                    return;
                case 0x0D:
                    ValidateShuffle(instruction);
                    ApplySignature(RequireRule(SimdRuleTable, SimdSignatureRules, sub));
                    return;
                case >= 0x15 and <= 0x22:
                    ValidateLane(instruction.Flags, LaneCount(sub));
                    ApplySignature(RequireRule(SimdRuleTable, SimdSignatureRules, sub));
                    return;
                case >= 0x54 and <= 0x57:
                    SimdLoadLane(instruction, sub);
                    return;
                case >= 0x58 and <= 0x5B:
                    SimdStoreLane(instruction, sub);
                    return;
                default:
                    var rule = LookupRule(SimdRuleTable, sub);
                    if (rule is null)
                        throw Missing(instruction);
                    ApplySignature(rule);
                    return;
            }
        }

        private void EnterBlock(ControlKind kind, DecodedInstruction instruction)
        {
            var signature = _context.BlockSignature(instruction)
                ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Block type {instruction.BlockType} is not a valid function type or value type");
            PopMany(signature.Inputs);
            PushControl(kind, signature);
        }

        private void PushControl(ControlKind kind, BlockSignature signature)
        {
            int valueHeight = _values.Count;
            int initHeight = _initStack.Count;
            var branchTypes = kind == ControlKind.Loop ? signature.Inputs : signature.Outputs;
            _controls.Add(RentControlFrame(kind, signature.Inputs, signature.Outputs, branchTypes, valueHeight, initHeight));
            PushMany(signature.Inputs);
        }

        private ControlFrame RentControlFrame(
            ControlKind kind,
            ExactValueType[] startTypes,
            ExactValueType[] endTypes,
            ExactValueType[] branchTypes,
            int valueHeight,
            int initHeight)
        {
            ControlFrame frame;
            if (_controlFramePoolCount == _controlFramePool.Count)
            {
                frame = new ControlFrame();
                _controlFramePool.Add(frame);
            }
            else
            {
                frame = _controlFramePool[_controlFramePoolCount];
            }

            _controlFramePoolCount++;
            frame.Reset(kind, startTypes, endTypes, branchTypes, valueHeight, initHeight);
            return frame;
        }

        private void ReleaseControlFrame()
        {
            _controlFramePoolCount--;
            _controls.RemoveAt(_controls.Count - 1);
        }

        private void BeginElse()
        {
            var frame = Current;
            if (frame.Kind != ControlKind.If || frame.HadElse)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "else outside of an if then-arm");

            CompleteArm(frame);
            ResetToFrameEntry(frame);
            frame.Unreachable = false;
            frame.HadElse = true;
            PushMany(frame.StartTypes);
        }

        private void BeginCatch(uint tagIndex)
        {
            var frame = Current;
            if (frame.Kind != ControlKind.Try)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "catch outside of a try");
            if (frame.HadCatchAll)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "catch after catch_all");
            var tag = ExceptionTagType(tagIndex);

            CompleteArm(frame);
            ResetToFrameEntry(frame);
            frame.Unreachable = false;
            frame.HadCatch = true;
            frame.CanRethrow = true;
            PushMany(tag.Parameters);
        }

        private void BeginCatchAll()
        {
            var frame = Current;
            if (frame.Kind != ControlKind.Try)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "catch_all outside of a try");
            if (frame.HadCatchAll)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "duplicate catch_all");

            CompleteArm(frame);
            ResetToFrameEntry(frame);
            frame.Unreachable = false;
            frame.HadCatch = true;
            frame.HadCatchAll = true;
            frame.CanRethrow = true;
        }

        private void EndControl()
        {
            var frame = Current;
            if (frame.Kind == ControlKind.If && !frame.HadElse &&
                (frame.StartTypes.Length != frame.EndTypes.Length ||
                 !frame.StartTypes.Zip(frame.EndTypes).All(pair => _subtype.IsSubtype(pair.First, pair.Second))))
            {
                throw Failure(
                    WasmValidationCode.FunctionBodyControlMismatch,
                    "if without else requires matching parameter and result types");
            }

            CompleteArm(frame);
            ReleaseControlFrame();
            ResetInitialization(frame.InitHeight);

            if (frame.Kind != ControlKind.Function)
                PushMany(frame.EndTypes);
        }

        private void Delegate(uint depth)
        {
            var frame = Current;
            if (frame.Kind != ControlKind.Try)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "delegate outside of a try");
            if (frame.HadCatch)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "delegate after catch");
            ValidateDelegateDepth(depth);

            CompleteArm(frame);
            ReleaseControlFrame();
            ResetInitialization(frame.InitHeight);
            PushMany(frame.EndTypes);
        }

        private void CompleteArm(ControlFrame frame)
        {
            PopMany(frame.EndTypes);
            if (_values.Count != frame.ValueHeight)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Control arm leaves {_values.Count - frame.ValueHeight} extra operand(s)");
        }

        private void Branch(uint depth)
        {
            var target = Target(depth);
            PopMany(target.BranchTypes);
            MarkUnreachable();
        }

        private void BranchIf(uint depth)
        {
            Pop(I32);
            var target = Target(depth);
            PopMany(target.BranchTypes);
            for (int i = 0; i < target.BranchTypes.Length; i++)
                Push(target.BranchTypes[i]);
        }

        private void BranchTable(DecodedInstruction instruction)
        {
            var labels = DecodeBrTableLabels(instruction.ImmediateBytes);
            if (labels.Length == 0)
                throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "br_table missing default label");

            Pop(I32);
            var defaultTarget = Target(labels[^1]);
            for (int i = 0; i < labels.Length - 1; i++)
            {
                var target = Target(labels[i]);
                if (target.BranchTypes.Length != defaultTarget.BranchTypes.Length)
                {
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"br_table target {labels[i]} label types differ from default target {labels[^1]}");
                }
            }

            var stack = _values.ToArray();
            foreach (uint label in labels)
            {
                var target = Target(label);
                try
                {
                    PopMany(target.BranchTypes);
                }
                catch (ValidationFailure failure) when (
                    failure.Code is WasmValidationCode.FunctionBodyStackUnderflow or
                        WasmValidationCode.FunctionBodyTypeMismatch)
                {
                    throw Failure(
                        WasmValidationCode.FunctionBodyTypeMismatch,
                        $"br_table payload does not satisfy target {label}: {failure.Message}");
                }
                _values.Clear();
                _values.AddRange(stack);
            }
            MarkUnreachable();
        }

        private void Return()
        {
            var function = _controls[0];
            PopMany(function.EndTypes);
            MarkUnreachable();
        }

        private void Throw(uint tagIndex)
        {
            var tag = ExceptionTagType(tagIndex);
            PopMany(tag.Parameters);
            MarkUnreachable();
        }

        private void ThrowRef()
        {
            Pop(ExnRef);
            MarkUnreachable();
        }

        private void Rethrow(uint depth)
        {
            if (depth >= _controls.Count || !_controls[_controls.Count - 1 - (int)depth].CanRethrow)
                throw Failure(WasmValidationCode.FunctionBodyUnknownLabel, $"Rethrow label depth {depth} is out of range");
            MarkUnreachable();
        }

        private void EnterTryTable(DecodedInstruction instruction)
        {
            ValidateTryTableHandlers(instruction);
            EnterBlock(ControlKind.TryTable, instruction);
        }

        private void Call(uint functionIndex, bool tail)
        {
            var signature = _context.FunctionTypeByAbsoluteIndex(functionIndex)
                ?? throw Failure(WasmValidationCode.FunctionIndexOutOfRange, $"Function index {functionIndex} is out of range");
            PopMany(signature.Parameters);
            FinishCall(signature, tail);
        }

        private void CallIndirect(uint typeIndex, uint tableIndex, bool tail)
        {
            var signature = _context.FunctionTypeByTypeIndex(typeIndex)
                ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {typeIndex} is not a valid function type");
            var table = Table(tableIndex);
            var elementType = TableElementType(table);
            if (!IsFunctionReferenceType(elementType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Table {tableIndex} element type {elementType.Key} is not a function reference type");

            Pop(IndexType(table));
            PopMany(signature.Parameters);
            FinishCall(signature, tail);
        }

        private void CallRef(uint typeIndex, bool tail)
        {
            var signature = _context.FunctionTypeByTypeIndex(typeIndex)
                ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {typeIndex} is not a valid function type");
            Pop(new ExactValueType.Reference(Nullability.Nullable, new DefinedHeapType((int)typeIndex)));
            PopMany(signature.Parameters);
            FinishCall(signature, tail);
        }

        private void ContNew(uint contTypeIndex)
        {
            var continuation = ContinuationSignature(contTypeIndex);
            var functionReference = PopReference();
            if (!functionReference.IsBottom)
            {
                if (!IsFunctionReferenceType(functionReference.Type!))
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"cont.new operand {functionReference.Type!.Key} is not a function reference");
                if (functionReference.Type is ExactValueType.Reference { HeapType: DefinedHeapType defined })
                {
                    var function = _context.FunctionTypeByTypeIndex(defined.TypeIndex)
                        ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {defined.TypeIndex} is not a valid function type");
                    EnsureSameSignature(function, continuation, "cont.new function reference");
                }
            }

            Push(ContinuationResultType(contTypeIndex));
        }

        private void ContBind(uint sourceTypeIndex, uint destinationTypeIndex)
        {
            var source = ContinuationSignature(sourceTypeIndex);
            var destination = ContinuationSignature(destinationTypeIndex);
            if (!SameTypes(source.Results, destination.Results))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    "cont.bind requires source and destination continuation result types to match");

            int bindCount = source.Parameters.Length - destination.Parameters.Length;
            if (bindCount < 0 || !SameTypes(source.Parameters.AsSpan(bindCount..), destination.Parameters))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    "cont.bind requires destination parameters to be a suffix of source parameters");

            PopContinuationReference(sourceTypeIndex);
            PopMany(source.Parameters[..bindCount]);
            Push(ContinuationResultType(destinationTypeIndex));
        }

        private void Resume(DecodedInstruction instruction)
        {
            var continuation = ContinuationSignature(instruction.Index0);
            ValidateResumeHandlers(instruction, continuation);
            PopContinuationReference(instruction.Index0);
            PopMany(continuation.Parameters);
            PushMany(continuation.Results);
        }

        private void Suspend(uint tagIndex)
        {
            var tag = StackSwitchingTagType(tagIndex);
            PopMany(tag.Parameters);
            PushMany(tag.Results);
        }

        private void Switch(uint contTypeIndex, uint tagIndex)
        {
            var continuation = ContinuationSignature(contTypeIndex);
            var tag = StackSwitchingTagType(tagIndex);

            if (continuation.Parameters.Length > 0 &&
                TryDefinedContinuationParameter(continuation.Parameters[^1], out var targetContinuationTypeIndex))
            {
                if (tag.Parameters.Length != 0)
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        "switch requires a tag with no parameter payload");

                var targetContinuation = ContinuationSignature(targetContinuationTypeIndex);
                if (!SameTypes(continuation.Results, tag.Results) ||
                    !SameTypes(tag.Results, targetContinuation.Results))
                {
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        "switch tag results must match the source and target continuation results");
                }

                PopContinuationReference(contTypeIndex);
                PopMany(continuation.Parameters[..^1]);
                PushMany(targetContinuation.Parameters);
                return;
            }

            // Legacy nesm stack-switching tests predate concrete continuation identity in the
            // compact function type path and model switch payloads through abstract contref.
            bool passesCapturedContinuation = continuation.Parameters.Length == tag.Parameters.Length + 1;
            if (!passesCapturedContinuation && continuation.Parameters.Length != tag.Parameters.Length)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    "switch continuation parameters must match the tag payload, optionally followed by a continuation reference");
            for (int i = 0; i < tag.Parameters.Length; i++)
            {
                if (continuation.Parameters[i] != tag.Parameters[i])
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"switch tag payload type {tag.Parameters[i].Key} does not match continuation parameter {continuation.Parameters[i].Key}");
            }
            if (passesCapturedContinuation && !IsContinuationReferenceType(continuation.Parameters[^1]))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    "switch continuation's final parameter must be a continuation reference");

            PopContinuationReference(contTypeIndex);
            PopMany(tag.Parameters);
            PushMany(tag.Parameters);
            if (passesCapturedContinuation)
                Push(new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Cont)));
        }

        private void FinishCall(FunctionSignature signature, bool tail)
        {
            if (tail)
            {
                var callerResults = _controls[0].EndTypes;
                if (!ResultTypesCompatible(signature.Results, callerResults))
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "Tail-call results are not compatible with caller results");
                MarkUnreachable();
                return;
            }

            PushMany(signature.Results);
        }

        private void Drop()
        {
            _ = Pop();
        }

        private void SelectUntyped()
        {
            Pop(I32);
            var right = Pop();
            var left = Pop();
            var selected = MergeSelectTypes(left, right);
            if (!selected.IsBottom && !IsUntypedSelectOperand(selected.Type!))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "Untyped select operands must have the same numeric or vector type");
            Push(selected);
        }

        private void SelectTyped(DecodedInstruction instruction)
        {
            var declaredTypes = DecodeSelectTypes(instruction.ImmediateBytes);
            if (declaredTypes.Length != 1)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, $"Typed select declares {declaredTypes.Length} result types; exactly one is required");

            var type = declaredTypes[0];
            EnsureValidValueType(type);
            Pop(I32);
            Pop(type);
            Pop(type);
            Push(type);
        }

        private void LocalGet(uint index)
        {
            if (index >= _locals.Count)
                throw Failure(WasmValidationCode.FunctionBodyUnknownLocal, $"Local index {index} is out of range");
            if (!_initialized[(int)index])
                throw Failure(WasmValidationCode.FunctionBodyUninitializedLocal, $"Local index {index} is not definitely initialized");
            Push(_locals[(int)index]);
        }

        private void LocalSet(uint index, bool tee)
        {
            if (index >= _locals.Count)
                throw Failure(WasmValidationCode.FunctionBodyUnknownLocal, $"Local index {index} is out of range");
            var type = _locals[(int)index];
            Pop(type);
            MarkInitialized((int)index);
            if (tee)
                Push(type);
        }

        private void GlobalGet(uint index)
        {
            Push(GlobalContentType(Global(index)));
        }

        private void GlobalSet(uint index)
        {
            var global = Global(index);
            if (global.Mutability != Mutability.Var)
                throw Failure(WasmValidationCode.FunctionBodyImmutableGlobal, $"Global index {index} is immutable");
            Pop(GlobalContentType(global));
        }

        private void TableGet(uint index)
        {
            var table = Table(index);
            Pop(IndexType(table));
            Push(TableElementType(table));
        }

        private void TableSet(uint index)
        {
            var table = Table(index);
            Pop(TableElementType(table));
            Pop(IndexType(table));
        }

        private void RefIsNull()
        {
            _ = PopReference();
            Push(I32);
        }

        private void RefEq()
        {
            Pop(EqRef);
            Pop(EqRef);
            Push(I32);
        }

        private void RefFunc(uint functionIndex)
        {
            int typeIndex = _context.FunctionTypeIndexByAbsoluteIndex(functionIndex)
                ?? throw Failure(WasmValidationCode.FunctionIndexOutOfRange, $"Function index {functionIndex} is out of range");
            if (!_context.IsDeclaredFunctionReference(functionIndex))
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, $"Function index {functionIndex} is not declared for ref.func");
            Push(new ExactValueType.Reference(Nullability.NonNullable, new DefinedHeapType(typeIndex)));
        }

        private void RefAsNonNull()
        {
            var value = PopReference();
            if (value.IsBottom)
            {
                Push(StackValue.Bottom);
                return;
            }

            var reference = (ExactValueType.Reference)value.Type!;
            Push(new ExactValueType.Reference(Nullability.NonNullable, reference.HeapType));
        }

        private void BrOnNull(uint depth)
        {
            var value = PopReference();
            var target = Target(depth);
            PopMany(target.BranchTypes);
            PushMany(target.BranchTypes);

            if (value.IsBottom)
            {
                Push(StackValue.Bottom);
                return;
            }

            var reference = (ExactValueType.Reference)value.Type!;
            Push(new ExactValueType.Reference(Nullability.NonNullable, reference.HeapType));
        }

        private void BrOnNonNull(uint depth)
        {
            var value = PopReference();
            var target = Target(depth);
            if (target.BranchTypes.Length == 0 || target.BranchTypes[^1] is not ExactValueType.Reference targetReference)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "br_on_non_null target must accept a non-null reference payload");

            if (!value.IsBottom)
            {
                var sourceReference = (ExactValueType.Reference)value.Type!;
                var nonNullSource = new ExactValueType.Reference(Nullability.NonNullable, sourceReference.HeapType);
                if (!_subtype.IsSubtype(nonNullSource, target.BranchTypes[^1]))
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"br_on_non_null reference payload {nonNullSource.Key} is not assignable to label type {targetReference.Key}");
            }

            var prefixTypes = target.BranchTypes[..^1];
            PopMany(prefixTypes);
            PushMany(prefixTypes);
        }

        private void StructNew(uint typeIndex, bool defaultValues)
        {
            var fields = StructFields(typeIndex);
            if (defaultValues)
            {
                EnsureDefaultable(fields);
            }
            else
            {
                for (int i = fields.Count - 1; i >= 0; i--)
                    Pop(StorageOperandType(fields[i].StorageType));
            }

            Push(NonNullableDefinedRef(typeIndex));
        }

        private void StructGet(uint typeIndex, uint fieldIndex, bool? signed)
        {
            var field = StructField(typeIndex, fieldIndex);
            ValidatePackedAccess(field.StorageType, signed);
            Pop(NullableDefinedRef(typeIndex));
            Push(StorageResultType(field.StorageType, signed));
        }

        private void StructSet(uint typeIndex, uint fieldIndex)
        {
            var field = StructField(typeIndex, fieldIndex);
            if (field.Mutability != Mutability.Var)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, $"Struct field {fieldIndex} of type {typeIndex} is immutable");
            Pop(StorageOperandType(field.StorageType));
            Pop(NullableDefinedRef(typeIndex));
        }

        private void ArrayNew(uint typeIndex, bool defaultValues)
        {
            var field = ArrayField(typeIndex);
            // The length is the last operand pushed, so it comes off first. Popping the element
            // value first only happens to work when the element type is i32.
            Pop(I32);
            if (defaultValues)
                EnsureDefaultable(field);
            else
                Pop(StorageOperandType(field.StorageType));
            Push(NonNullableDefinedRef(typeIndex));
        }

        private void ArrayNewFixed(uint typeIndex, uint count)
        {
            var field = ArrayField(typeIndex);
            for (uint i = 0; i < count; i++)
                Pop(StorageOperandType(field.StorageType));
            Push(NonNullableDefinedRef(typeIndex));
        }

        private void ArrayNewData(uint typeIndex, uint dataIndex)
        {
            var field = ArrayField(typeIndex);
            RequireDataSegment(dataIndex);
            EnsurePackedArrayForData(field.StorageType, typeIndex);
            Pop(I32);
            Pop(I32);
            Push(NonNullableDefinedRef(typeIndex));
        }

        private void ArrayNewElem(uint typeIndex, uint elementIndex)
        {
            var field = ArrayField(typeIndex);
            var element = ElementSegment(elementIndex);
            EnsureElementAssignable(element, field.StorageType, typeIndex);
            Pop(I32);
            Pop(I32);
            Push(NonNullableDefinedRef(typeIndex));
        }

        private void ArrayGet(uint typeIndex, bool? signed)
        {
            var field = ArrayField(typeIndex);
            ValidatePackedAccess(field.StorageType, signed);
            Pop(I32);
            Pop(NullableDefinedRef(typeIndex));
            Push(StorageResultType(field.StorageType, signed));
        }

        private void ArraySet(uint typeIndex)
        {
            var field = ArrayField(typeIndex);
            EnsureMutableArray(field, typeIndex);
            Pop(StorageOperandType(field.StorageType));
            Pop(I32);
            Pop(NullableDefinedRef(typeIndex));
        }

        private void ArrayLen()
        {
            Pop(ArrayRef);
            Push(I32);
        }

        private void ArrayFill(uint typeIndex)
        {
            var field = ArrayField(typeIndex);
            EnsureMutableArray(field, typeIndex);
            Pop(I32);
            Pop(StorageOperandType(field.StorageType));
            Pop(I32);
            Pop(NullableDefinedRef(typeIndex));
        }

        private void ArrayCopy(uint destinationTypeIndex, uint sourceTypeIndex)
        {
            var destination = ArrayField(destinationTypeIndex);
            var source = ArrayField(sourceTypeIndex);
            EnsureMutableArray(destination, destinationTypeIndex);
            if (!StorageAssignable(source.StorageType, destination.StorageType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Array type {sourceTypeIndex} element storage is not assignable to array type {destinationTypeIndex}");

            Pop(I32);
            Pop(I32);
            Pop(NullableDefinedRef(sourceTypeIndex));
            Pop(I32);
            Pop(NullableDefinedRef(destinationTypeIndex));
        }

        private void ArrayInitData(uint typeIndex, uint dataIndex)
        {
            var field = ArrayField(typeIndex);
            RequireDataSegment(dataIndex);
            EnsureMutableArray(field, typeIndex);
            EnsurePackedArrayForData(field.StorageType, typeIndex);
            Pop(I32);
            Pop(I32);
            Pop(I32);
            Pop(NullableDefinedRef(typeIndex));
        }

        private void ArrayInitElem(uint typeIndex, uint elementIndex)
        {
            var field = ArrayField(typeIndex);
            var element = ElementSegment(elementIndex);
            EnsureMutableArray(field, typeIndex);
            EnsureElementAssignable(element, field.StorageType, typeIndex);
            Pop(I32);
            Pop(I32);
            Pop(I32);
            Pop(NullableDefinedRef(typeIndex));
        }

        private void RefTest(long heapType, Nullability nullability)
        {
            _ = CastType(heapType, nullability);
            PopReference();
            Push(I32);
        }

        private void RefCast(long heapType, Nullability nullability)
        {
            var target = CastType(heapType, nullability);
            var source = PopReference();
            Push(source.IsBottom ? StackValue.Bottom : new StackValue(target));
        }

        private void BrOnCast(DecodedInstruction instruction, bool fail)
        {
            var sourceType = CastType(instruction.HeapType0, (instruction.Flags & 0x01) != 0 ? Nullability.Nullable : Nullability.NonNullable);
            var targetType = CastType(instruction.HeapType1, (instruction.Flags & 0x02) != 0 ? Nullability.Nullable : Nullability.NonNullable);
            ValidateCastRelation(targetType, sourceType);

            var source = PopReference();
            // The OPERAND must match rt1, i.e. operand <: rt1. Asking the reverse rejected every
            // br_on_cast whose static operand type was narrower than the declared source — which
            // is the normal case, and the whole point of declaring rt1.
            if (!source.IsBottom && !_subtype.IsSubtype(source.Type!, sourceType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Cast operand {source.Type!.Key} is not assignable to cast source {sourceType.Key}");
            var target = Target(instruction.Index0);
            if (target.BranchTypes.Length == 0)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "cast branch target must accept a reference payload");

            // rt1 \ rt2 — rt1's heap type, nullable only if rt1 is nullable and rt2 is not. When
            // rt2 admits null the null value always goes the other way, so the remaining branch
            // provably cannot see one. br_on_cast.wast's "null-diff" needs exactly this: it
            // fall-throughs into a block typed (ref any), non-nullable, from an (ref null any)
            // source.
            var difference = CastDifference(sourceType, targetType);

            var branchPayload = fail ? difference : targetType;
            if (!_subtype.IsSubtype(branchPayload, target.BranchTypes[^1]))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Cast branch payload {branchPayload.Key} is not assignable to label type {target.BranchTypes[^1].Key}");

            var prefixTypes = target.BranchTypes[..^1];
            PopMany(prefixTypes);
            PushMany(prefixTypes);

            if (source.IsBottom)
            {
                Push(StackValue.Bottom);
                return;
            }

            Push(fail ? targetType : difference);
        }

        /// <summary>The reference-type difference <c>rt1 \ rt2</c> from the GC proposal.</summary>
        private static ExactValueType CastDifference(ExactValueType source, ExactValueType target)
            => source is ExactValueType.Reference s
               && target is ExactValueType.Reference { Nullability: Nullability.Nullable }
                ? new ExactValueType.Reference(Nullability.NonNullable, s.HeapType)
                : source;

        private void AnyConvertExtern()
        {
            Pop(ExternRef);
            Push(AnyRef);
        }

        private void ExternConvertAny()
        {
            Pop(AnyRef);
            Push(ExternRef);
        }

        private void RefI31()
        {
            Pop(I32);
            Push(I31Ref);
        }

        private void I31Get()
        {
            Pop(NullableI31Ref);
            Push(I32);
        }

        private void ScalarLoad(DecodedInstruction instruction)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, ScalarMemoryNaturalAlignment(instruction.OpcodeId.Opcode));
            Pop(IndexType(memory));
            Push(ScalarLoadResultType(instruction.OpcodeId.Opcode));
        }

        private void ScalarStore(DecodedInstruction instruction)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, ScalarMemoryNaturalAlignment(instruction.OpcodeId.Opcode));
            Pop(ScalarStoreValueType(instruction.OpcodeId.Opcode));
            Pop(IndexType(memory));
        }

        private void MemoryInit(uint dataIndex, uint memoryIndex)
        {
            RequireDataSegment(dataIndex);
            var memory = Memory(memoryIndex);

            Pop(I32);
            Pop(I32);
            Pop(IndexType(memory));
        }

        private void DataDrop(uint dataIndex)
        {
            RequireDataSegment(dataIndex);
        }

        private void MemoryCopy(uint destinationIndex, uint sourceIndex)
        {
            var destination = Memory(destinationIndex);
            var source = Memory(sourceIndex);
            Pop(CopyLengthType(destination.Is64, source.Is64));
            Pop(IndexType(source));
            Pop(IndexType(destination));
        }

        private void MemoryFill(uint memoryIndex)
        {
            var memory = Memory(memoryIndex);
            Pop(IndexType(memory));
            Pop(I32);
            Pop(IndexType(memory));
        }

        private void MemorySize(uint memoryIndex)
        {
            Push(IndexType(Memory(memoryIndex)));
        }

        private void MemoryGrow(uint memoryIndex)
        {
            var memory = Memory(memoryIndex);
            Pop(IndexType(memory));
            Push(IndexType(memory));
        }

        private void TableInit(uint elementIndex, uint tableIndex)
        {
            var table = Table(tableIndex);
            var element = ElementSegment(elementIndex);
            var tableType = TableElementType(table);
            if (!_subtype.IsSubtype(element.ElementType, tableType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Element segment {elementIndex} type {element.ElementType.Key} is not assignable to table {tableIndex} element type {tableType.Key}");

            Pop(I32);
            Pop(I32);
            Pop(IndexType(table));
        }

        private void ElemDrop(uint elementIndex)
        {
            _ = ElementSegment(elementIndex);
        }

        private void TableCopy(uint destinationIndex, uint sourceIndex)
        {
            var destination = Table(destinationIndex);
            var source = Table(sourceIndex);
            var destinationElementType = TableElementType(destination);
            var sourceElementType = TableElementType(source);
            if (!_subtype.IsSubtype(sourceElementType, destinationElementType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Source table {sourceIndex} element type {sourceElementType.Key} is not assignable to destination table {destinationIndex} element type {destinationElementType.Key}");

            Pop(CopyLengthType(destination.Is64, source.Is64));
            Pop(IndexType(source));
            Pop(IndexType(destination));
        }

        private void TableGrow(uint tableIndex)
        {
            var table = Table(tableIndex);
            var indexType = IndexType(table);
            Pop(indexType);
            Pop(TableElementType(table));
            Push(indexType);
        }

        private void TableSize(uint tableIndex)
        {
            Push(IndexType(Table(tableIndex)));
        }

        private void TableFill(uint tableIndex)
        {
            var table = Table(tableIndex);
            Pop(IndexType(table));
            Pop(TableElementType(table));
            Pop(IndexType(table));
        }

        private void SimdLoad(DecodedInstruction instruction, uint subOpcode)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, SimdNaturalAlignment(subOpcode));
            Pop(IndexType(memory));
            Push(V128);
        }

        private void SimdStore(DecodedInstruction instruction, uint subOpcode)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, SimdNaturalAlignment(subOpcode));
            Pop(V128);
            Pop(IndexType(memory));
        }

        private void SimdLoadLane(DecodedInstruction instruction, uint subOpcode)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, SimdNaturalAlignment(subOpcode));
            ValidateLane(instruction.Flags, LaneCount(subOpcode));
            Pop(V128);
            Pop(IndexType(memory));
            Push(V128);
        }

        private void SimdStoreLane(DecodedInstruction instruction, uint subOpcode)
        {
            var memory = Memory(instruction.Index1);
            ValidateMemArg(instruction, memory, SimdNaturalAlignment(subOpcode));
            ValidateLane(instruction.Flags, LaneCount(subOpcode));
            Pop(V128);
            Pop(IndexType(memory));
        }

        private MemoryType Memory(uint index)
            => _context.Memory(index)
               ?? throw Failure(WasmValidationCode.MemoryIndexOutOfRange, $"Memory index {index} is out of range");

        private TableType Table(uint index)
            => _context.Table(index)
               ?? throw Failure(WasmValidationCode.TableIndexOutOfRange, $"Table index {index} is out of range");

        private GlobalType Global(uint index)
            => _context.Global(index)
               ?? throw Failure(WasmValidationCode.GlobalIndexOutOfRange, $"Global index {index} is out of range");

        private void RequireDataSegment(uint index)
        {
            if (!_context.HasDataSegment(index))
                throw Failure(WasmValidationCode.DataMemoryIndexInvalid, $"Data segment index {index} is out of range");
        }

        private Element ElementSegment(uint index)
            => _context.ElementSegment(index)
               ?? throw Failure(WasmValidationCode.ElementFunctionIndexOutOfRange, $"Element segment index {index} is out of range");

        private static ExactValueType IndexType(MemoryType memory) => memory.Is64 ? I64 : I32;

        private static ExactValueType IndexType(TableType table) => table.Is64 ? I64 : I32;

        private static ExactValueType CopyLengthType(bool destinationIs64, bool sourceIs64)
            => destinationIs64 && sourceIs64 ? I64 : I32;

        private static ExactValueType TableElementType(TableType table)
            => table.ExactElementType ?? table.ElementType switch
            {
                RefType.ExternRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Extern)),
                _ => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func)),
            };

        private static ExactValueType GlobalContentType(GlobalType global)
            => global.ExactContentType ?? FromStorage(global.ContentType);

        private IReadOnlyList<FieldType> StructFields(uint typeIndex)
        {
            var definition = TypeDefinition(typeIndex);
            if (definition.CompositeType is not CompositeType.Struct structure)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {typeIndex} is not a struct type");
            return structure.Fields;
        }

        private FieldType StructField(uint typeIndex, uint fieldIndex)
        {
            var fields = StructFields(typeIndex);
            if (fieldIndex >= fields.Count)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate,
                    $"Struct field index {fieldIndex} is out of range for type {typeIndex}");
            return fields[(int)fieldIndex];
        }

        private FieldType ArrayField(uint typeIndex)
        {
            var definition = TypeDefinition(typeIndex);
            if (definition.CompositeType is not CompositeType.Array array)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {typeIndex} is not an array type");
            return array.Field;
        }

        private TypeDefinition TypeDefinition(uint index)
        {
            if (index >= _context.TypeDefinitions.Count)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {index} is out of range");
            return _context.TypeDefinitions[(int)index];
        }

        private FunctionSignature ContinuationSignature(uint typeIndex)
        {
            var definition = TypeDefinition(typeIndex);
            if (definition.CompositeType is CompositeType.Continuation continuation)
            {
                return _context.FunctionTypeByTypeIndex(continuation.FunctionTypeIndex)
                    ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange,
                        $"Continuation type {typeIndex} references invalid function type {continuation.FunctionTypeIndex}");
            }

            if (definition.CompositeType is CompositeType.Function)
                return _context.FunctionTypeByTypeIndex(typeIndex)
                    ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange,
                        $"Type index {typeIndex} is not a function type");

            throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Type index {typeIndex} is not a continuation or function type");
        }

        private ExactValueType.Reference ContinuationResultType(uint typeIndex)
            => TypeDefinition(typeIndex).CompositeType is CompositeType.Continuation
                ? NonNullableDefinedRef(typeIndex)
                : new ExactValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.Cont));

        private FunctionSignature StackSwitchingTagType(uint tagIndex)
            => _context.TagType(tagIndex)
               ?? throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Tag index {tagIndex} is out of range");

        private FunctionSignature ExceptionTagType(uint tagIndex)
        {
            var tag = StackSwitchingTagType(tagIndex);
            if (tag.Results.Length != 0)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"Tag index {tagIndex} is not an exception tag type");
            return tag;
        }

        private void PopContinuationReference(uint typeIndex)
        {
            var value = PopReference();
            if (value.IsBottom)
                return;
            if (!IsContinuationReferenceType(value.Type!))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Expected continuation reference, found {value.Type!.Key}");

            if (TypeDefinition(typeIndex).CompositeType is not CompositeType.Continuation)
                return;

            var expected = new ExactValueType.Reference(Nullability.Nullable, new DefinedHeapType(checked((int)typeIndex)));
            if (value.Type is ExactValueType.Reference { HeapType: DefinedHeapType } && !_subtype.IsSubtype(value.Type, expected))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Expected continuation type {expected.Key}, found {value.Type.Key}");
        }

        private bool TryDefinedContinuationParameter(ExactValueType type, out uint typeIndex)
        {
            if (type is ExactValueType.Reference { HeapType: DefinedHeapType defined } &&
                defined.TypeIndex >= 0 &&
                defined.TypeIndex < _context.TypeDefinitions.Count &&
                _context.TypeDefinitions[defined.TypeIndex].CompositeType is CompositeType.Continuation)
            {
                typeIndex = (uint)defined.TypeIndex;
                return true;
            }

            typeIndex = 0;
            return false;
        }

        private void EnsureSameSignature(FunctionSignature actual, FunctionSignature expected, string context)
        {
            if (!SameTypes(actual.Parameters, expected.Parameters) || !SameTypes(actual.Results, expected.Results))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"{context} type does not match continuation signature");
        }

        private ExactValueType.Reference CastType(long heapType, Nullability nullability)
        {
            var heap = HeapTypeFromSigned(heapType);
            if (heap is DefinedHeapType defined)
                _ = TypeDefinition((uint)defined.TypeIndex);
            return new ExactValueType.Reference(nullability, heap);
        }

        private void ValidateCastRelation(ExactValueType target, ExactValueType source)
        {
            if (!_subtype.IsSubtype(target, source))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Cast target {target.Key} is not a subtype of source {source.Key}");
        }

        private void EnsureValidValueType(ExactValueType type)
        {
            if (type is ExactValueType.Reference reference && reference.HeapType is DefinedHeapType defined)
                _ = TypeDefinition((uint)defined.TypeIndex);
        }

        private ExactValueType.Reference NonNullableDefinedRef(uint typeIndex)
        {
            _ = TypeDefinition(typeIndex);
            return new ExactValueType.Reference(Nullability.NonNullable, new DefinedHeapType(checked((int)typeIndex)));
        }

        private ExactValueType.Reference NullableDefinedRef(uint typeIndex)
        {
            _ = TypeDefinition(typeIndex);
            return new ExactValueType.Reference(Nullability.Nullable, new DefinedHeapType(checked((int)typeIndex)));
        }

        private static ExactValueType StorageOperandType(StorageType storage) => storage switch
        {
            StorageType.Value value => value.Type,
            StorageType.Packed => I32,
            _ => throw new InvalidOperationException($"Unknown storage type {storage.GetType().Name}"),
        };

        private static ExactValueType StorageResultType(StorageType storage, bool? signed)
        {
            return storage switch
            {
                StorageType.Value value when signed is null => value.Type,
                StorageType.Value => throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, "Signed/unsigned field access requires a packed storage type"),
                StorageType.Packed when signed is not null => I32,
                StorageType.Packed => throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, "Packed field access requires signed or unsigned form"),
                _ => throw new InvalidOperationException($"Unknown storage type {storage.GetType().Name}"),
            };
        }

        private static void ValidatePackedAccess(StorageType storage, bool? signed)
        {
            _ = StorageResultType(storage, signed);
        }

        private static void EnsureDefaultable(IReadOnlyList<FieldType> fields)
        {
            for (int i = 0; i < fields.Count; i++)
                EnsureDefaultable(fields[i]);
        }

        private static void EnsureDefaultable(FieldType field)
        {
            if (!IsDefaultableStorage(field.StorageType))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "Default allocation requires defaultable field types");
        }

        private static bool IsDefaultableStorage(StorageType storage) => storage switch
        {
            StorageType.Packed => true,
            StorageType.Value value => IsDefaultable(value.Type),
            _ => false,
        };

        private static void EnsureMutableArray(FieldType field, uint typeIndex)
        {
            if (field.Mutability != Mutability.Var)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, $"Array type {typeIndex} element is immutable");
        }

        private static void EnsurePackedArrayForData(StorageType storage, uint typeIndex)
        {
            // The spec permits any numeric, vector or packed element type here — the restriction
            // is only that a data segment cannot initialise a *reference* element, because raw
            // bytes carry no reference identity. Requiring i8/i16 rejected every valid
            // `array.new_data $t` on an i32/i64/f32/f64/v128 array.
            bool ok = storage switch
            {
                StorageType.Packed => true,
                StorageType.Value value => value.Type is ExactValueType.Number or ExactValueType.Vector,
                _ => false,
            };

            if (!ok)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Array type {typeIndex} must have a numeric, vector or packed element type " +
                    "for data segment initialization");
        }

        private void EnsureElementAssignable(Element element, StorageType storage, uint typeIndex)
        {
            if (storage is not StorageType.Value value || value.Type is not ExactValueType.Reference)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Array type {typeIndex} must have a reference element type for element segment initialization");
            if (!_subtype.IsSubtype(element.ElementType, value.Type))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Element segment type {element.ElementType.Key} is not assignable to array type {typeIndex} element type {value.Type.Key}");
        }

        private bool StorageAssignable(StorageType source, StorageType destination)
        {
            return (source, destination) switch
            {
                (StorageType.Value s, StorageType.Value d) => _subtype.IsSubtype(s.Type, d.Type),
                (StorageType.Packed s, StorageType.Packed d) => s.Type == d.Type,
                _ => false,
            };
        }

        private void ApplyNumeric(byte opcode)
        {
            var rule = LookupRule(NumericRuleTable, opcode);
            if (rule is null)
                throw Failure(WasmValidationCode.InternalRuleMissing, $"No numeric rule for opcode 0x{opcode:X2}");
            ApplySignature(rule);
        }

        private void ApplySignature(SignatureRule rule)
        {
            PopMany(rule.Inputs);
            PushMany(rule.Outputs);
        }

        private static void ValidateMemArg(DecodedInstruction instruction, MemoryType memory, uint naturalAlignment)
        {
            if (instruction.Index0 > naturalAlignment)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate,
                    $"Memory alignment {instruction.Index0} exceeds natural alignment {naturalAlignment}");

            // The binary memarg offset is a u64 for every memory; the bound is the memory's address type (validation, not decoding).
            if (!memory.Is64 && instruction.MemOffset > uint.MaxValue)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate,
                    $"Memory offset {instruction.MemOffset} is out of range for a 32-bit memory");
        }

        private static void ValidateLane(byte lane, uint laneCount)
        {
            if (lane >= laneCount)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate,
                    $"Lane index {lane} is out of range for {laneCount} lanes");
        }

        private static void ValidateShuffle(DecodedInstruction instruction)
        {
            var bytes = instruction.ImmediateBytes;
            if (bytes.Length < 16)
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, "SIMD shuffle immediate is truncated");
            foreach (byte lane in bytes[^16..])
            {
                if (lane >= 32)
                    throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate,
                        $"Shuffle lane index {lane} is out of range");
            }
        }

        private void Push(ExactValueType type) => _values.Add(new StackValue(type));

        private void Push(StackValue value) => _values.Add(value);

        private void PushMany(IReadOnlyList<ExactValueType> types)
        {
            for (int i = 0; i < types.Count; i++)
                Push(types[i]);
        }

        // Array overloads: identical order and per-element work, without the
        // interface dispatch that IReadOnlyList forces on every element.
        private void PushMany(ExactValueType[] types)
        {
            for (int i = 0; i < types.Length; i++)
                Push(types[i]);
        }

        private void PushPreserved(IReadOnlyList<StackValue> actuals, IReadOnlyList<ExactValueType> expected)
        {
            for (int i = 0; i < expected.Count; i++)
                Push(actuals[i].IsBottom ? new StackValue(expected[i]) : actuals[i]);
        }

        private StackValue PopReference()
        {
            var actual = Pop();
            if (!actual.IsBottom && actual.Type is not ExactValueType.Reference)
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Expected reference type, found {actual.Type!.Key}");
            return actual;
        }

        private StackValue Pop()
        {
            var frame = Current;
            if (_values.Count == frame.ValueHeight)
            {
                if (frame.Unreachable)
                    return StackValue.Bottom;
                throw Failure(WasmValidationCode.FunctionBodyStackUnderflow, "Operand stack underflow");
            }

            var value = _values[^1];
            _values.RemoveAt(_values.Count - 1);
            return value;
        }

        private StackValue Pop(ExactValueType expected)
        {
            var actual = Pop();
            if (!actual.IsBottom && !_subtype.IsSubtype(actual.Type!, expected))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"Expected {expected.Key}, found {actual.Type!.Key}");
            return actual;
        }

        private void PopMany(IReadOnlyList<ExactValueType> expected)
        {
            for (int i = expected.Count - 1; i >= 0; i--)
                _ = Pop(expected[i]);
        }

        private void PopMany(ExactValueType[] expected)
        {
            for (int i = expected.Length - 1; i >= 0; i--)
                _ = Pop(expected[i]);
        }

        private void MarkUnreachable()
        {
            var frame = Current;
            if (_values.Count > frame.ValueHeight)
                _values.RemoveRange(frame.ValueHeight, _values.Count - frame.ValueHeight);
            frame.Unreachable = true;
        }

        private ControlFrame Current
            => _controls.Count == 0
                ? throw Failure(WasmValidationCode.FunctionBodyControlMismatch, "Control stack is empty")
                : _controls[^1];

        private ControlFrame Target(uint depth)
        {
            if (depth >= _controls.Count)
                throw Failure(WasmValidationCode.FunctionBodyUnknownLabel, $"Label depth {depth} is out of range");
            return _controls[_controls.Count - 1 - (int)depth];
        }

        private void ValidateDelegateDepth(uint depth)
        {
            int delegateTargets = _controls.Count - 1; // all enclosing labels outside the current try
            if (depth >= delegateTargets)
                throw Failure(WasmValidationCode.FunctionBodyUnknownLabel, $"Delegate label depth {depth} is out of range");
        }

        private void ValidateTryTableHandlers(DecodedInstruction instruction)
        {
            ReadOnlySpan<byte> bytes = instruction.ImmediateBytes;
            int offset = 0;
            _ = ReadBlockTypeFromBytes(bytes, ref offset);
            uint handlerCount = WasmLeb128.ReadU32(bytes, ref offset);
            for (uint h = 0; h < handlerCount; h++)
            {
                byte catchKind = bytes[offset++];
                ExactValueType[] payload;
                if (catchKind is 0x00 or 0x01)
                {
                    uint tagIndex = WasmLeb128.ReadU32(bytes, ref offset);
                    var tag = ExceptionTagType(tagIndex);
                    payload = catchKind == 0x01
                        ? tag.Parameters.Concat([NonNullableExnRef]).ToArray()
                        : tag.Parameters;
                }
                else if (catchKind == 0x02)
                {
                    payload = [];
                }
                else if (catchKind == 0x03)
                {
                    payload = [NonNullableExnRef];
                }
                else
                {
                    throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, $"Unknown try_table catch kind 0x{catchKind:X2}");
                }

                uint labelDepth = WasmLeb128.ReadU32(bytes, ref offset);
                var target = Target(labelDepth);
                if (payload.Length != target.BranchTypes.Length)
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"try_table handler payload arity {payload.Length} does not match label depth {labelDepth} arity {target.BranchTypes.Length}");
                for (int i = 0; i < payload.Length; i++)
                {
                    if (!_subtype.IsSubtype(payload[i], target.BranchTypes[i]))
                        throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                            $"try_table handler payload {payload[i].Key} is not assignable to label type {target.BranchTypes[i].Key}");
                }
            }
        }

        private void ValidateResumeHandlers(DecodedInstruction instruction, FunctionSignature resumeContinuation)
        {
            ReadOnlySpan<byte> bytes = instruction.ImmediateBytes;
            int offset = 0;
            _ = WasmLeb128.ReadU32(bytes, ref offset); // sub-opcode
            _ = WasmLeb128.ReadU32(bytes, ref offset); // continuation type index
            uint handlerCount = WasmLeb128.ReadU32(bytes, ref offset);
            for (uint h = 0; h < handlerCount; h++)
            {
                uint tagIndex = WasmLeb128.ReadU32(bytes, ref offset);
                uint labelDepth = WasmLeb128.ReadU32(bytes, ref offset);
                var tag = StackSwitchingTagType(tagIndex);
                var target = Target(labelDepth);
                if (target.BranchTypes.Length != tag.Parameters.Length + 1)
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        $"resume handler label depth {labelDepth} must accept tag payload plus captured continuation");

                for (int i = 0; i < tag.Parameters.Length; i++)
                {
                    if (!_subtype.IsSubtype(tag.Parameters[i], target.BranchTypes[i]))
                        throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                            $"resume handler payload {tag.Parameters[i].Key} is not assignable to label type {target.BranchTypes[i].Key}");
                }

                var captured = target.BranchTypes[^1];
                if (!IsContinuationReferenceType(captured))
                    throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                        "resume handler label's final payload must be a continuation reference");

                if (captured is ExactValueType.Reference { HeapType: DefinedHeapType defined })
                {
                    var capturedSignature = ContinuationSignature((uint)defined.TypeIndex);
                    if (!SameTypes(capturedSignature.Parameters, tag.Results) ||
                        !SameTypes(capturedSignature.Results, resumeContinuation.Results))
                    {
                        throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                            "resume handler captured continuation type must accept tag results and produce resume results");
                    }
                }
            }
        }

        private void ResetToFrameEntry(ControlFrame frame)
        {
            if (_values.Count > frame.ValueHeight)
                _values.RemoveRange(frame.ValueHeight, _values.Count - frame.ValueHeight);
            ResetInitialization(frame.InitHeight);
        }

        private void MarkInitialized(int index)
        {
            if (!_initialized[index])
            {
                _initialized[index] = true;
                _initStack.Add(index);
            }
        }

        private void ResetInitialization(int height)
        {
            while (_initStack.Count > height)
            {
                int localIndex = _initStack[^1];
                _initStack.RemoveAt(_initStack.Count - 1);
                _initialized[localIndex] = false;
            }
        }

        private StackValue MergeSelectTypes(StackValue left, StackValue right)
        {
            if (left.IsBottom)
                return right;
            if (right.IsBottom)
                return left;
            if (_subtype.IsSubtype(left.Type!, right.Type!) && _subtype.IsSubtype(right.Type!, left.Type!))
                return left;
            throw Failure(WasmValidationCode.FunctionBodyTypeMismatch,
                $"Select operand types differ: {left.Type!.Key} and {right.Type!.Key}");
        }

        private bool ResultTypesCompatible(ReadOnlySpan<ExactValueType> actual, ReadOnlySpan<ExactValueType> expected)
        {
            if (actual.Length != expected.Length)
                return false;
            for (int i = 0; i < actual.Length; i++)
            {
                if (!_subtype.IsSubtype(actual[i], expected[i]))
                    return false;
            }

            return true;
        }

        private bool IsFunctionReferenceType(ExactValueType type)
            => _subtype.IsSubtype(type, new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func)));

        private bool IsContinuationReferenceType(ExactValueType type)
            => _subtype.IsSubtype(type, new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Cont)));

        private bool SameTypes(ReadOnlySpan<ExactValueType> left, ReadOnlySpan<ExactValueType> right)
        {
            if (left.Length != right.Length)
                return false;

            for (int i = 0; i < left.Length; i++)
            {
                if (!_subtype.AreEquivalent(left[i], right[i]))
                    return false;
            }

            return true;
        }

        private static ValidationFailure Missing(DecodedInstruction instruction)
            => Failure(WasmValidationCode.InternalRuleMissing, $"No function-body validation rule for opcode {instruction.OpcodeId}");

        private static ValidationFailure Failure(WasmValidationCode code, string message) => new(code, message);
    }

    private sealed class ControlFrame
    {
        public ControlKind Kind { get; private set; }
        public ExactValueType[] StartTypes { get; private set; } = [];
        public ExactValueType[] EndTypes { get; private set; } = [];
        public ExactValueType[] BranchTypes { get; private set; } = [];
        public int ValueHeight { get; private set; }
        public int InitHeight { get; private set; }
        public bool Unreachable { get; set; }
        public bool HadElse { get; set; }
        public bool HadCatch { get; set; }
        public bool HadCatchAll { get; set; }
        public bool CanRethrow { get; set; }

        public void Reset(
            ControlKind kind,
            ExactValueType[] startTypes,
            ExactValueType[] endTypes,
            ExactValueType[] branchTypes,
            int valueHeight,
            int initHeight)
        {
            Kind = kind;
            StartTypes = startTypes;
            EndTypes = endTypes;
            BranchTypes = branchTypes;
            ValueHeight = valueHeight;
            InitHeight = initHeight;
            Unreachable = false;
            HadElse = false;
            HadCatch = false;
            HadCatchAll = false;
            CanRethrow = false;
        }
    }

    private readonly record struct StackValue(ExactValueType? Type)
    {
        public bool IsBottom => Type is null;
        public static StackValue Bottom => new(null);
    }

    private sealed record FunctionSignature(ExactValueType[] Parameters, ExactValueType[] Results);
    private readonly record struct BlockSignature(ExactValueType[] Inputs, ExactValueType[] Outputs);
    private sealed record SignatureRule(ExactValueType[] Inputs, ExactValueType[] Outputs);
    /// <summary>A context cannot decide a check while the code section is decoded; the body is validated afterwards.</summary>
    private sealed class DeferredBodyException : Exception;

    /// <summary>
    /// Validates function bodies from the decoder's instruction walk, so a validated load reads each body once
    /// (docs/design/validator.md §7.1, §11.2) instead of walking it again in <see cref="ValidateModule(WasmModule, WasmFeatures)"/>.
    /// </summary>
    /// <remarks>
    /// <para>Results are recorded, never thrown: the module is decoded to the end first, so a malformed module is still
    /// reported as malformed, and the diagnostics are merged in the same place and order as the separate pass.</para>
    /// <para>A body is left undecided, and validated afterwards exactly as before, whenever its result could differ
    /// from that pass: the validator threw something other than a validation failure, or it needed a data index
    /// without a data count section (the data section has not been read yet).</para>
    /// </remarks>
    internal sealed class FusedBodyValidation(WasmModule module, IReadOnlyList<int> definedFunctionTypeIndices, uint? dataCount)
    {
        private readonly List<WasmValidationDiagnostic?> _results = [];
        private readonly List<bool> _decided = [];
        private FunctionValidator? _validator;
        private bool _unavailable;
        private int _current;
        private bool _active;

        public int FunctionCount => _results.Count;

        public void Begin(int definedIndex, WasmFunction function)
        {
            while (_results.Count <= definedIndex)
            {
                _results.Add(null);
                _decided.Add(false);
            }

            _current = definedIndex;
            _active = false;
            if (_unavailable)
                return;

            try
            {
                _validator ??= new FunctionValidator(
                    new ModuleValidationContext(module, definedFunctionTypeIndices, dataCount), WasmFeatures.All);
                _validator.Initialize(definedIndex, function);
                _active = true;
            }
            catch (Exception) when (_validator is null)
            {
                _unavailable = true;
            }
            catch (Exception)
            {
            }
        }

        public void Observe(in DecodedInstruction instruction, int nextPosition)
        {
            if (!_active)
                return;

            try
            {
                if (_validator!.Step(instruction, nextPosition))
                    Decide(null);
            }
            catch (ValidationFailure failure)
            {
                Decide(_validator!.ToDiagnostic(failure));
            }
            catch (Exception)
            {
                _active = false;
            }
        }

        public void End()
        {
            if (_active)
                Decide(_validator!.ToDiagnostic(FunctionValidator.MissingFinalEnd()));
        }

        public bool TryGetResult(int definedIndex, out WasmValidationDiagnostic? diagnostic)
        {
            diagnostic = null;
            if ((uint)definedIndex >= (uint)_results.Count || !_decided[definedIndex])
                return false;
            diagnostic = _results[definedIndex];
            return true;
        }

        private void Decide(WasmValidationDiagnostic? diagnostic)
        {
            _results[_current] = diagnostic;
            _decided[_current] = true;
            _active = false;
        }
    }

    private sealed class ValidationFailure(WasmValidationCode code, string message) : Exception(message)
    {
        public WasmValidationCode Code { get; } = code;
    }

    private enum ControlKind
    {
        Function,
        Block,
        Loop,
        If,
        Try,
        TryTable
    }

    private static IEnumerable<OpcodeId> PrimaryRuleOpcodes()
    {
        byte[] implemented =
        [
            0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
            0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x0F,
            0x10, 0x11, 0x12, 0x13, 0x14, 0x15, 0x18, 0x19,
            0x1F,
            0x1A, 0x1B, 0x1C, 0x20, 0x21, 0x22, 0x23, 0x24,
            0x25, 0x26, 0x3F, 0x40, 0x41, 0x42, 0x43, 0x44,
            0xD0, 0xD1, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6,
        ];
        for (byte opcode = 0x28; opcode <= 0x3E; opcode++)
            yield return new OpcodeId(opcode);
        foreach (byte opcode in implemented)
            yield return new OpcodeId(opcode);
    }

    private static Dictionary<byte, SignatureRule> BuildNumericRules()
    {
        var rules = new Dictionary<byte, SignatureRule>();
        void Add(byte opcode, ExactValueType[] inputs, ExactValueType[] outputs) => rules.Add(opcode, new SignatureRule(inputs, outputs));
        void AddRange(int start, int end, ExactValueType[] inputs, ExactValueType[] outputs)
        {
            for (int opcode = start; opcode <= end; opcode++)
                Add((byte)opcode, inputs, outputs);
        }

        Add(0x45, [I32], [I32]);
        AddRange(0x46, 0x4F, [I32, I32], [I32]);
        Add(0x50, [I64], [I32]);
        AddRange(0x51, 0x5A, [I64, I64], [I32]);
        AddRange(0x5B, 0x60, [F32, F32], [I32]);
        AddRange(0x61, 0x66, [F64, F64], [I32]);

        AddRange(0x67, 0x69, [I32], [I32]);
        AddRange(0x6A, 0x78, [I32, I32], [I32]);
        AddRange(0x79, 0x7B, [I64], [I64]);
        AddRange(0x7C, 0x8A, [I64, I64], [I64]);

        AddRange(0x8B, 0x91, [F32], [F32]);
        AddRange(0x92, 0x98, [F32, F32], [F32]);
        AddRange(0x99, 0x9F, [F64], [F64]);
        AddRange(0xA0, 0xA6, [F64, F64], [F64]);

        Add(0xA7, [I64], [I32]);
        AddRange(0xA8, 0xA9, [F32], [I32]);
        AddRange(0xAA, 0xAB, [F64], [I32]);
        AddRange(0xAC, 0xAD, [I32], [I64]);
        AddRange(0xAE, 0xAF, [F32], [I64]);
        AddRange(0xB0, 0xB1, [F64], [I64]);

        AddRange(0xB2, 0xB3, [I32], [F32]);
        AddRange(0xB4, 0xB5, [I64], [F32]);
        Add(0xB6, [F64], [F32]);
        AddRange(0xB7, 0xB8, [I32], [F64]);
        AddRange(0xB9, 0xBA, [I64], [F64]);
        Add(0xBB, [F32], [F64]);
        Add(0xBC, [F32], [I32]);
        Add(0xBD, [F64], [I64]);
        Add(0xBE, [I32], [F32]);
        Add(0xBF, [I64], [F64]);
        AddRange(0xC0, 0xC1, [I32], [I32]);
        AddRange(0xC2, 0xC4, [I64], [I64]);
        return rules;
    }

    private static Dictionary<uint, SignatureRule> BuildFcSignatureRules() => new()
    {
        [0] = new SignatureRule([F32], [I32]),
        [1] = new SignatureRule([F32], [I32]),
        [2] = new SignatureRule([F64], [I32]),
        [3] = new SignatureRule([F64], [I32]),
        [4] = new SignatureRule([F32], [I64]),
        [5] = new SignatureRule([F32], [I64]),
        [6] = new SignatureRule([F64], [I64]),
        [7] = new SignatureRule([F64], [I64]),
        [19] = new SignatureRule([I64, I64, I64, I64], [I64, I64]),
        [20] = new SignatureRule([I64, I64, I64, I64], [I64, I64]),
        [21] = new SignatureRule([I64, I64], [I64, I64]),
        [22] = new SignatureRule([I64, I64], [I64, I64]),
    };

    private static Dictionary<uint, SignatureRule> BuildSimdSignatureRules()
    {
        var rules = new Dictionary<uint, SignatureRule>();
        void Add(uint sub, ExactValueType[] inputs, ExactValueType[] outputs) => rules.Add(sub, new SignatureRule(inputs, outputs));
        void AddRange(uint start, uint end, ExactValueType[] inputs, ExactValueType[] outputs)
        {
            for (uint sub = start; sub <= end; sub++)
            {
                if (InstructionCursor.IsKnownSimdSubOpcode(sub))
                    Add(sub, inputs, outputs);
            }
        }

        Add(0x0C, [], [V128]);
        Add(0x0D, [V128, V128], [V128]);
        Add(0x0E, [V128, V128], [V128]);
        Add(0x0F, [I32], [V128]);
        Add(0x10, [I32], [V128]);
        Add(0x11, [I32], [V128]);
        Add(0x12, [I64], [V128]);
        Add(0x13, [F32], [V128]);
        Add(0x14, [F64], [V128]);

        AddRange(0x15, 0x16, [V128], [I32]);
        Add(0x17, [V128, I32], [V128]);
        AddRange(0x18, 0x19, [V128], [I32]);
        Add(0x1A, [V128, I32], [V128]);
        Add(0x1B, [V128], [I32]);
        Add(0x1C, [V128, I32], [V128]);
        Add(0x1D, [V128], [I64]);
        Add(0x1E, [V128, I64], [V128]);
        Add(0x1F, [V128], [F32]);
        Add(0x20, [V128, F32], [V128]);
        Add(0x21, [V128], [F64]);
        Add(0x22, [V128, F64], [V128]);

        AddRange(0x23, 0x4C, [V128, V128], [V128]);
        Add(0x4D, [V128], [V128]);
        AddRange(0x4E, 0x51, [V128, V128], [V128]);
        Add(0x52, [V128, V128, V128], [V128]);
        Add(0x53, [V128], [I32]);

        AddRange(0x5E, 0x5F, [V128], [V128]);
        AddRange(0x60, 0x62, [V128], [V128]);
        AddRange(0x63, 0x64, [V128], [I32]);
        AddRange(0x65, 0x66, [V128, V128], [V128]);
        AddRange(0x67, 0x6A, [V128], [V128]);
        AddRange(0x6B, 0x6D, [V128, I32], [V128]);
        AddRange(0x6E, 0x73, [V128, V128], [V128]);
        AddRange(0x74, 0x75, [V128], [V128]);
        AddRange(0x76, 0x79, [V128, V128], [V128]);
        Add(0x7A, [V128], [V128]);
        Add(0x7B, [V128, V128], [V128]);
        AddRange(0x7C, 0x7F, [V128], [V128]);

        AddRange(0x80, 0x81, [V128], [V128]);
        Add(0x82, [V128, V128], [V128]);
        AddRange(0x83, 0x84, [V128], [I32]);
        AddRange(0x85, 0x86, [V128, V128], [V128]);
        AddRange(0x87, 0x8A, [V128], [V128]);
        AddRange(0x8B, 0x8D, [V128, I32], [V128]);
        AddRange(0x8E, 0x93, [V128, V128], [V128]);
        Add(0x94, [V128], [V128]);
        AddRange(0x95, 0x99, [V128, V128], [V128]);
        Add(0x9B, [V128, V128], [V128]);
        AddRange(0x9C, 0x9F, [V128, V128], [V128]);

        AddRange(0xA0, 0xA1, [V128], [V128]);
        AddRange(0xA3, 0xA4, [V128], [I32]);
        AddRange(0xA7, 0xAA, [V128], [V128]);
        AddRange(0xAB, 0xAD, [V128, I32], [V128]);
        Add(0xAE, [V128, V128], [V128]);
        AddRange(0xB1, 0xB9, [V128, V128], [V128]);
        Add(0xBA, [V128, V128], [V128]);
        AddRange(0xBC, 0xBF, [V128, V128], [V128]);

        AddRange(0xC0, 0xC1, [V128], [V128]);
        AddRange(0xC3, 0xC4, [V128], [I32]);
        AddRange(0xC7, 0xCA, [V128], [V128]);
        AddRange(0xCB, 0xCD, [V128, I32], [V128]);
        Add(0xCE, [V128, V128], [V128]);
        AddRange(0xD1, 0xD1, [V128, V128], [V128]);
        AddRange(0xD5, 0xDB, [V128, V128], [V128]);
        AddRange(0xDC, 0xDF, [V128, V128], [V128]);

        AddRange(0xE0, 0xE1, [V128], [V128]);
        Add(0xE3, [V128], [V128]);
        AddRange(0xE4, 0xEB, [V128, V128], [V128]);
        AddRange(0xEC, 0xED, [V128], [V128]);
        Add(0xEF, [V128], [V128]);
        AddRange(0xF0, 0xF7, [V128, V128], [V128]);
        AddRange(0xF8, 0xFF, [V128], [V128]);
        return rules;
    }

    private static Dictionary<uint, SignatureRule> BuildRelaxedSimdSignatureRules() => new()
    {
        [0x100] = new SignatureRule([V128, V128], [V128]),
        [0x101] = new SignatureRule([V128], [V128]),
        [0x102] = new SignatureRule([V128], [V128]),
        [0x103] = new SignatureRule([V128], [V128]),
        [0x104] = new SignatureRule([V128], [V128]),
        [0x105] = new SignatureRule([V128, V128, V128], [V128]),
        [0x106] = new SignatureRule([V128, V128, V128], [V128]),
        [0x107] = new SignatureRule([V128, V128, V128], [V128]),
        [0x108] = new SignatureRule([V128, V128, V128], [V128]),
        [0x109] = new SignatureRule([V128, V128, V128], [V128]),
        [0x10A] = new SignatureRule([V128, V128, V128], [V128]),
        [0x10B] = new SignatureRule([V128, V128, V128], [V128]),
        [0x10C] = new SignatureRule([V128, V128, V128], [V128]),
        [0x10D] = new SignatureRule([V128, V128], [V128]),
        [0x10E] = new SignatureRule([V128, V128], [V128]),
        [0x10F] = new SignatureRule([V128, V128], [V128]),
        [0x110] = new SignatureRule([V128, V128], [V128]),
        [0x111] = new SignatureRule([V128, V128], [V128]),
        [0x112] = new SignatureRule([V128, V128], [V128]),
        [0x113] = new SignatureRule([V128, V128, V128], [V128]),
    };

    private static uint LaneCount(uint subOpcode) => subOpcode switch
    {
        0x15 or 0x16 or 0x17 or 0x54 or 0x58 => 16,
        0x18 or 0x19 or 0x1A or 0x55 or 0x59 => 8,
        0x1B or 0x1C or 0x1F or 0x20 or 0x56 or 0x5A => 4,
        0x1D or 0x1E or 0x21 or 0x22 or 0x57 or 0x5B => 2,
        _ => throw new InvalidOperationException($"Opcode 0xFD:0x{subOpcode:X} has no lane immediate"),
    };

    private static uint SimdNaturalAlignment(uint subOpcode) => subOpcode switch
    {
        0x00 or 0x0B => 4,
        0x01 or 0x02 or 0x03 or 0x04 or 0x05 or 0x06 => 3,
        0x07 or 0x54 or 0x58 => 0,
        0x08 or 0x55 or 0x59 => 1,
        0x09 or 0x56 or 0x5A or 0x5C => 2,
        0x0A or 0x57 or 0x5B or 0x5D => 3,
        _ => throw new InvalidOperationException($"Opcode 0xFD:0x{subOpcode:X} is not a SIMD memory opcode"),
    };

    private static ExactValueType ScalarLoadResultType(byte opcode) => opcode switch
    {
        0x28 or 0x2C or 0x2D or 0x2E or 0x2F => I32,
        0x29 or 0x30 or 0x31 or 0x32 or 0x33 or 0x34 or 0x35 => I64,
        0x2A => F32,
        0x2B => F64,
        _ => throw new InvalidOperationException($"Opcode 0x{opcode:X2} is not a scalar load"),
    };

    private static ExactValueType ScalarStoreValueType(byte opcode) => opcode switch
    {
        0x36 or 0x3A or 0x3B => I32,
        0x37 or 0x3C or 0x3D or 0x3E => I64,
        0x38 => F32,
        0x39 => F64,
        _ => throw new InvalidOperationException($"Opcode 0x{opcode:X2} is not a scalar store"),
    };

    private static uint ScalarMemoryNaturalAlignment(byte opcode) => opcode switch
    {
        0x2C or 0x2D or 0x30 or 0x31 or 0x3A or 0x3C => 0,
        0x2E or 0x2F or 0x32 or 0x33 or 0x3B or 0x3D => 1,
        0x28 or 0x2A or 0x34 or 0x35 or 0x36 or 0x38 or 0x3E => 2,
        0x29 or 0x2B or 0x37 or 0x39 => 3,
        _ => throw new InvalidOperationException($"Opcode 0x{opcode:X2} is not a scalar memory opcode"),
    };

    private static bool IsUntypedSelectOperand(ExactValueType type)
        => type is ExactValueType.Number or ExactValueType.Vector;

    private static bool IsDefaultable(ExactValueType type)
        => type is not ExactValueType.Reference { Nullability: Nullability.NonNullable };

    private static ExactValueType FromStorage(ValType type) => type switch
    {
        ValType.I32 => I32,
        ValType.I64 => I64,
        ValType.F32 => F32,
        ValType.F64 => F64,
        ValType.V128 => V128,
        ValType.FuncRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func)),
        ValType.ExternRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Extern)),
        ValType.ExnRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Exn)),
        ValType.ContRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Cont)),
        _ => throw new InvalidOperationException($"Unknown storage type {type}"),
    };

    private static ExactValueType? ValueTypeFromSignedByte(long type) => type switch
    {
        < 0 when (byte)(type & 0x7F) == 0x7F => I32,
        < 0 when (byte)(type & 0x7F) == 0x7E => I64,
        < 0 when (byte)(type & 0x7F) == 0x7D => F32,
        < 0 when (byte)(type & 0x7F) == 0x7C => F64,
        < 0 when (byte)(type & 0x7F) == 0x7B => V128,
        < 0 when TryAbstractHeapTypeFromCode((byte)(type & 0x7F), out var kind)
            => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(kind)),
        _ => null,
    };

    private static HeapType HeapTypeFromSigned(long heapType)
        => AbstractHeapTypeCodes.FromSigned(heapType);

    private static bool TryAbstractHeapTypeFromCode(byte code, out HeapTypeKind kind)
        => AbstractHeapTypeCodes.TryFromCode(code, out kind);

    private static uint[] DecodeBrTableLabels(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        uint count = WasmLeb128.ReadU32(bytes, ref offset);
        var labels = new uint[checked((int)count + 1)];
        for (int i = 0; i < labels.Length; i++)
            labels[i] = WasmLeb128.ReadU32(bytes, ref offset);
        return labels;
    }

    private static long ReadBlockTypeFromBytes(ReadOnlySpan<byte> bytes, ref int offset)
    {
        long blockType = WasmLeb128.ReadS33(bytes, ref offset);
        if (blockType is -0x1D or -0x1C)
            _ = WasmLeb128.ReadS33(bytes, ref offset);
        return blockType;
    }

    private static ExactValueType[] DecodeSelectTypes(ReadOnlySpan<byte> bytes)
    {
        int offset = 0;
        uint count = WasmLeb128.ReadU32(bytes, ref offset);
        var types = new ExactValueType[checked((int)count)];
        for (int i = 0; i < types.Length; i++)
            types[i] = ReadValueType(bytes, ref offset);
        return types;
    }

    private static ExactValueType ReadValueType(ReadOnlySpan<byte> bytes, ref int offset)
    {
        byte valueType = bytes[offset++];
        if (valueType is 0x63 or 0x64)
        {
            var nullability = valueType == 0x63 ? Nullability.Nullable : Nullability.NonNullable;
            return new ExactValueType.Reference(nullability, HeapTypeFromSigned(WasmLeb128.ReadS33(bytes, ref offset)));
        }

        long signedValueType = valueType >= 0x40 ? valueType - 0x80 : valueType;
        return ValueTypeFromSignedByte(signedValueType)
               ?? throw new WasmFormatException($"Unknown value type 0x{valueType:X2}");
    }
}
