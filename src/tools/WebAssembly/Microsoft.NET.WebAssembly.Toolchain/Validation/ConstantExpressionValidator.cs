// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

using Microsoft.NET.WebAssembly.Binary;
using Microsoft.NET.WebAssembly.Module;
using Microsoft.NET.WebAssembly.Types;
using ExactValueType = Microsoft.NET.WebAssembly.Types.ValueType;

internal static class ConstantExpressionValidator
{
    private static readonly ExactValueType I32 = new ExactValueType.Number(NumberType.I32);
    private static readonly ExactValueType I64 = new ExactValueType.Number(NumberType.I64);
    private static readonly ExactValueType F32 = new ExactValueType.Number(NumberType.F32);
    private static readonly ExactValueType F64 = new ExactValueType.Number(NumberType.F64);
    private static readonly ExactValueType V128 = new ExactValueType.Vector(VectorType.V128);
    private static readonly ExactValueType ExternRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Extern));
    private static readonly ExactValueType I31Ref = new ExactValueType.Reference(Nullability.NonNullable, new AbstractHeapType(HeapTypeKind.I31));
    private static readonly ExactValueType AnyRef = new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Any));

    public static WasmValidationResult ValidateModule(WasmModule module, WasmFeatures features = WasmFeatures.All)
    {
        var diagnostics = new List<WasmValidationDiagnostic>();
        ValidateModule(module, features, diagnostics);
        return diagnostics.Count == 0
            ? WasmValidationResult.Valid
            : new WasmValidationResult(diagnostics);
    }

    internal static void ValidateModule(
        WasmModule module,
        WasmFeatures features,
        List<WasmValidationDiagnostic> diagnostics)
    {
        var context = new ModuleContext(module);
        for (int i = 0; i < module.Globals.Count; i++)
        {
            ValidateExpression(
                module.Globals[i].InitExpr,
                GlobalContentType(module.Globals[i].Type),
                context.ImportedGlobalCount + i,
                $"global {i} initializer",
                context,
                features,
                diagnostics);
        }

        for (int i = 0; i < module.Tables.Count; i++)
        {
            var table = module.Tables[i];
            if (table.InitExpr.Length == 0)
                continue;
            ValidateExpression(
                table.InitExpr,
                TableElementType(table),
                context.ImportedGlobalCount,
                $"table {i} initializer",
                context,
                features,
                diagnostics);
        }

        for (int i = 0; i < module.Elements.Count; i++)
        {
            var element = module.Elements[i];
            if (element.TableIndex >= 0 && context.Table((uint)element.TableIndex) is { } table)
            {
                ValidateExpression(
                    element.OffsetExpr,
                    IndexType(table),
                    context.TotalGlobalCount,
                    $"element {i} offset",
                    context,
                    features,
                    diagnostics);
            }

            for (int expressionIndex = 0; expressionIndex < element.ElementExpressions.Count; expressionIndex++)
            {
                ValidateExpression(
                    element.ElementExpressions[expressionIndex],
                    element.ElementType,
                    context.TotalGlobalCount,
                    $"element {i} expression {expressionIndex}",
                    context,
                    features,
                    diagnostics);
            }
        }

        for (int i = 0; i < module.DataSegments.Count; i++)
        {
            var data = module.DataSegments[i];
            if (data.IsPassive)
                continue;
            if (context.Memory((uint)data.MemoryIndex) is not { } memory)
                continue;
            ValidateExpression(
                data.OffsetExpr,
                IndexType(memory),
                context.TotalGlobalCount,
                $"data {i} offset",
                context,
                features,
                diagnostics);
        }
    }

    private static void ValidateExpression(
        byte[] expression,
        ExactValueType expectedType,
        int availableGlobalCount,
        string contextName,
        ModuleContext context,
        WasmFeatures features,
        List<WasmValidationDiagnostic> diagnostics)
    {
        var validator = new ExpressionValidator(expression, expectedType, availableGlobalCount, contextName, context, features);
        var diagnostic = validator.Validate();
        if (diagnostic is not null)
            diagnostics.Add(diagnostic);
    }

    private sealed class ExpressionValidator(
        byte[] expression,
        ExactValueType expectedType,
        int availableGlobalCount,
        string contextName,
        ModuleContext context,
        WasmFeatures features)
    {
        private readonly List<ExactValueType> _values = [];

        public WasmValidationDiagnostic? Validate()
        {
            if (expression.Length == 0)
                return Diagnostic(WasmValidationCode.InvalidGlobalInitExpr, $"{contextName}: constant expression is empty");

            var walker = new ConstExpressionWalker(expression, features);
            try
            {
                while (walker.MoveNext(out ConstOp op, out var instruction))
                {
                    if (op == ConstOp.End)
                    {
                        if (walker.Position != expression.Length)
                            return Diagnostic(WasmValidationCode.InvalidGlobalInitExpr, $"{contextName}: bytes follow constant-expression end");
                        return Finish();
                    }

                    Apply(op, instruction);
                }
            }
            catch (WasmFormatException ex)
            {
                return Diagnostic(WasmValidationCode.InvalidGlobalInitExpr, $"{contextName}: {ex.Message}");
            }
            catch (ValidationFailure failure)
            {
                return Diagnostic(failure.Code, $"{contextName}: {failure.Message}");
            }

            return Diagnostic(WasmValidationCode.InvalidGlobalInitExpr, $"{contextName}: constant expression is missing end");
        }

        private WasmValidationDiagnostic? Finish()
        {
            if (_values.Count == 0)
                return Diagnostic(WasmValidationCode.FunctionBodyStackUnderflow,
                    $"{contextName}: expected {expectedType.Key} but nothing is on the constant-expression stack");
            if (_values.Count != 1)
                return Diagnostic(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"{contextName}: constant expression leaves {_values.Count} values; expected exactly 1");
            var actual = _values[^1];
            if (!context.Subtype.IsSubtype(actual, expectedType))
                return Diagnostic(WasmValidationCode.FunctionBodyTypeMismatch,
                    $"{contextName}: expected {expectedType.Key}, found {actual.Key}");
            return null;
        }

        private void Apply(ConstOp op, DecodedInstruction instruction)
        {
            switch (op)
            {
                case ConstOp.GlobalGet:
                    GlobalGet(instruction.Index0);
                    return;
                case ConstOp.I32Const:
                    Push(I32);
                    return;
                case ConstOp.I64Const:
                    Push(I64);
                    return;
                case ConstOp.F32Const:
                    Push(F32);
                    return;
                case ConstOp.F64Const:
                    Push(F64);
                    return;
                case ConstOp.I32Add:
                case ConstOp.I32Sub:
                case ConstOp.I32Mul:
                    Pop(I32);
                    Pop(I32);
                    Push(I32);
                    return;
                case ConstOp.I64Add:
                case ConstOp.I64Sub:
                case ConstOp.I64Mul:
                    Pop(I64);
                    Pop(I64);
                    Push(I64);
                    return;
                case ConstOp.RefNull:
                    Push(CastType(instruction.HeapType0, Nullability.Nullable));
                    return;
                case ConstOp.RefFunc:
                    RefFunc(instruction.Index0);
                    return;
                case ConstOp.V128Const:
                    Push(V128);
                    return;
                case ConstOp.StructNew:
                    StructNew(instruction.Index0, defaultValues: false);
                    return;
                case ConstOp.StructNewDefault:
                    StructNew(instruction.Index0, defaultValues: true);
                    return;
                case ConstOp.ArrayNew:
                    ArrayNew(instruction.Index0, defaultValues: false);
                    return;
                case ConstOp.ArrayNewDefault:
                    ArrayNew(instruction.Index0, defaultValues: true);
                    return;
                case ConstOp.ArrayNewFixed:
                    ArrayNewFixed(instruction.Index0, instruction.Index1);
                    return;
                case ConstOp.AnyConvertExtern:
                    Pop(ExternRef);
                    Push(AnyRef);
                    return;
                case ConstOp.ExternConvertAny:
                    Pop(AnyRef);
                    Push(ExternRef);
                    return;
                case ConstOp.RefI31:
                    Pop(I32);
                    Push(I31Ref);
                    return;
                default:
                    throw Failure(WasmValidationCode.InvalidGlobalInitExpr,
                        instruction.OpcodeId.Opcode == WasmOpcode.GcPrefix
                            ? $"GC opcode {instruction.OpcodeId} is not permitted in a constant expression"
                            : $"opcode {instruction.OpcodeId} is not permitted in a constant expression");
            }
        }

        private void GlobalGet(uint index)
        {
            if (index >= availableGlobalCount)
                throw Failure(WasmValidationCode.GlobalIndexOutOfRange,
                    $"global.get references global index {index}, but only {availableGlobalCount} global(s) are in scope");
            var global = context.Global(index)
                ?? throw Failure(WasmValidationCode.GlobalIndexOutOfRange, $"global index {index} is out of range");
            if (global.Mutability != Mutability.Const)
                throw Failure(WasmValidationCode.FunctionBodyImmutableGlobal, $"global index {index} is mutable");
            Push(GlobalContentType(global));
        }

        private void RefFunc(uint functionIndex)
        {
            int typeIndex = context.FunctionTypeIndexByAbsoluteIndex(functionIndex)
                ?? throw Failure(WasmValidationCode.FunctionIndexOutOfRange, $"function index {functionIndex} is out of range");
            if (!context.IsDeclaredFunctionReference(functionIndex))
                throw Failure(WasmValidationCode.FunctionBodyInvalidImmediate, $"function index {functionIndex} is not declared for ref.func");
            Push(new ExactValueType.Reference(Nullability.NonNullable, new DefinedHeapType(typeIndex)));
        }

        private void StructNew(uint typeIndex, bool defaultValues)
        {
            var fields = StructFields(typeIndex);
            if (defaultValues)
            {
                foreach (var field in fields)
                    EnsureDefaultable(field);
            }
            else
            {
                for (int i = fields.Count - 1; i >= 0; i--)
                    Pop(StorageOperandType(fields[i].StorageType));
            }

            Push(NonNullableDefinedRef(typeIndex));
        }

        private void ArrayNew(uint typeIndex, bool defaultValues)
        {
            var field = ArrayField(typeIndex);
            // Length first — see the same correction in FunctionBodyValidator.ArrayNew.
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

        private IReadOnlyList<FieldType> StructFields(uint typeIndex)
        {
            var definition = TypeDefinition(typeIndex);
            if (definition.CompositeType is not CompositeType.Struct structure)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"type index {typeIndex} is not a struct type");
            return structure.Fields;
        }

        private FieldType ArrayField(uint typeIndex)
        {
            var definition = TypeDefinition(typeIndex);
            if (definition.CompositeType is not CompositeType.Array array)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"type index {typeIndex} is not an array type");
            return array.Field;
        }

        private TypeDefinition TypeDefinition(uint index)
        {
            if (index >= context.TypeDefinitions.Count)
                throw Failure(WasmValidationCode.TypeIndexOutOfRange, $"type index {index} is out of range");
            return context.TypeDefinitions[(int)index];
        }

        private ExactValueType.Reference CastType(long heapType, Nullability nullability)
        {
            var heap = HeapTypeFromSigned(heapType);
            if (heap is DefinedHeapType defined)
                _ = TypeDefinition((uint)defined.TypeIndex);
            return new ExactValueType.Reference(nullability, heap);
        }

        private ExactValueType.Reference NonNullableDefinedRef(uint typeIndex)
        {
            _ = TypeDefinition(typeIndex);
            return new ExactValueType.Reference(Nullability.NonNullable, new DefinedHeapType(checked((int)typeIndex)));
        }

        private static void EnsureDefaultable(FieldType field)
        {
            if (field.StorageType is StorageType.Value value && value.Type is ExactValueType.Reference { Nullability: Nullability.NonNullable })
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, "default allocation requires defaultable field types");
        }

        private void Push(ExactValueType type) => _values.Add(type);

        private void Pop(ExactValueType expected)
        {
            if (_values.Count == 0)
                throw Failure(WasmValidationCode.FunctionBodyStackUnderflow, "constant-expression stack underflow");
            var actual = _values[^1];
            _values.RemoveAt(_values.Count - 1);
            if (!context.Subtype.IsSubtype(actual, expected))
                throw Failure(WasmValidationCode.FunctionBodyTypeMismatch, $"expected {expected.Key}, found {actual.Key}");
        }

        private static WasmValidationDiagnostic Diagnostic(WasmValidationCode code, string message)
            => new(code, message);

        private static ValidationFailure Failure(WasmValidationCode code, string message) => new(code, message);
    }

    private sealed class ModuleContext
    {
        private readonly WasmModule _module;
        private readonly List<int> _functionTypeIndices = [];
        private readonly List<MemoryType> _memoryTypes = [];
        private readonly List<TableType> _tableTypes = [];
        private readonly List<GlobalType> _globalTypes = [];
        private readonly HashSet<int> _declaredFunctionReferences = [];

        public ModuleContext(WasmModule module)
        {
            _module = module;
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
                }
            }

            ImportedGlobalCount = _globalTypes.Count;
            foreach (var function in module.Functions)
                _functionTypeIndices.Add(function.TypeIndex);
            foreach (var memory in module.Memories)
                _memoryTypes.Add(memory);
            foreach (var table in module.Tables)
                _tableTypes.Add(table);
            foreach (var global in module.Globals)
                _globalTypes.Add(global.Type);

            foreach (var export in module.Exports.Values)
            {
                if (export.Descriptor is ExportDescriptor.Func function && IsFunctionIndex(function.FunctionIndex))
                    _declaredFunctionReferences.Add(function.FunctionIndex);
            }

            foreach (var element in module.Elements)
            {
                foreach (int functionIndex in element.FunctionIndices)
                {
                    if (IsFunctionIndex(functionIndex))
                        _declaredFunctionReferences.Add(functionIndex);
                }

                foreach (int functionIndex in element.DeclaredFunctionIndices)
                {
                    if (IsFunctionIndex(functionIndex))
                        _declaredFunctionReferences.Add(functionIndex);
                }
            }

            foreach (var global in module.Globals)
                AddRefFuncs(global.InitExpr);
            foreach (var table in module.Tables)
                AddRefFuncs(table.InitExpr);
            foreach (var element in module.Elements)
                foreach (var expr in element.ElementExpressions)
                    AddRefFuncs(expr);

            Subtype = new SubtypeRelation(TypeDefinitions);
        }

        public int ImportedGlobalCount { get; }

        public int TotalGlobalCount => _globalTypes.Count;

        public List<TypeDefinition> TypeDefinitions => _module.TypeDefinitions;

        public SubtypeRelation Subtype { get; }

        public MemoryType? Memory(uint index) => index < _memoryTypes.Count ? _memoryTypes[(int)index] : null;

        public TableType? Table(uint index) => index < _tableTypes.Count ? _tableTypes[(int)index] : null;

        public GlobalType? Global(uint index) => index < _globalTypes.Count ? _globalTypes[(int)index] : null;

        public int? FunctionTypeIndexByAbsoluteIndex(uint index)
            => index < _functionTypeIndices.Count ? _functionTypeIndices[(int)index] : null;

        public bool IsDeclaredFunctionReference(uint index)
            => index <= int.MaxValue && _declaredFunctionReferences.Contains((int)index);

        private bool IsFunctionIndex(int index)
            => index >= 0 && index < _functionTypeIndices.Count;

        private void AddRefFuncs(byte[] expression)
        {
            var cursor = new InstructionCursor(expression, WasmFeatures.All);
            try
            {
                while (cursor.MoveNext(out var instruction))
                {
                    if (instruction.OpcodeId.Opcode == WasmOpcode.RefFunc && IsFunctionIndex(unchecked((int)instruction.Index0)))
                        _declaredFunctionReferences.Add((int)instruction.Index0);
                    if (instruction.OpcodeId.Opcode == WasmOpcode.End)
                        return;
                }
            }
            catch (WasmFormatException)
            {
            }
        }
    }

    private sealed class ValidationFailure(WasmValidationCode code, string message) : Exception(message)
    {
        public WasmValidationCode Code { get; } = code;
    }

    private static ExactValueType IndexType(MemoryType memory) => memory.Is64 ? I64 : I32;

    private static ExactValueType IndexType(TableType table) => table.Is64 ? I64 : I32;

    private static ExactValueType TableElementType(TableType table)
        => table.ExactElementType ?? table.ElementType switch
        {
            RefType.ExternRef => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Extern)),
            _ => new ExactValueType.Reference(Nullability.Nullable, new AbstractHeapType(HeapTypeKind.Func)),
        };

    private static ExactValueType GlobalContentType(GlobalType global)
        => global.ExactContentType ?? FromStorage(global.ContentType);

    private static ExactValueType StorageOperandType(StorageType storage) => storage switch
    {
        StorageType.Value value => value.Type,
        StorageType.Packed => I32,
        _ => throw new InvalidOperationException($"Unknown storage type {storage.GetType().Name}"),
    };

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

    private static HeapType HeapTypeFromSigned(long heapType)
        => AbstractHeapTypeCodes.FromSigned(heapType);
}
