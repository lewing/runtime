// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

/// <summary>Identifies a specific Wasm spec validation rule (§3).</summary>
public enum WasmValidationCode
{
    TypeIndexOutOfRange,
    TagResultTypeNotEmpty,
    FunctionIndexOutOfRange,
    TableIndexOutOfRange,
    MemoryIndexOutOfRange,
    GlobalIndexOutOfRange,
    StartFunctionIndexOutOfRange,
    StartFunctionTypeMismatch,
    DuplicateExportName,
    LimitsMaxLessThanMin,
    MemoryMaxExceedsLimit,
    MultipleMemories,
    MultipleTables,
    InvalidGlobalInitExpr,
    InvalidSubtypeDeclaration,
    TypeMetadataMismatch,
    ElementTableIndexInvalid,
    ElementTableTypeMismatch,
    ElementFunctionIndexOutOfRange,
    TableElementTypeNotDefaultable,
    DataMemoryIndexInvalid,
    FunctionBodyTypeMismatch,
    FunctionBodyStackUnderflow,
    FunctionBodyStackOverflow,
    FunctionBodyControlMismatch,
    FunctionBodyUnknownLabel,
    FunctionBodyUnknownLocal,
    FunctionBodyUninitializedLocal,
    FunctionBodyImmutableGlobal,
    FunctionBodyInvalidImmediate,
    InternalRuleMissing,
    TableSizeExceedsLimit,
}
