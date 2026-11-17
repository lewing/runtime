// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Validation;

/// <summary>Identifies a specific Wasm spec validation rule (§3).</summary>
public enum WasmValidationCode
{
    TypeIndexOutOfRange,
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
    ElementTableIndexInvalid,
    ElementFunctionIndexOutOfRange,
    DataMemoryIndexInvalid,
}
