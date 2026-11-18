// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

// Component-model binary encodings (component model Binary.md). Each is a closed set that readers
// switch on, so each is an enum. Section ids live in ComponentSectionId.

/// <summary><c>sort</c>: what an index space entry is. <see cref="Core"/> is followed by a <see cref="CoreSort"/>.</summary>
internal enum ComponentSort : byte
{
    Core = 0x00,
    Func = 0x01,
    Value = 0x02,
    Type = 0x03,
    Component = 0x04,
    Instance = 0x05,
}

/// <summary><c>core:sort</c>.</summary>
internal enum CoreSort : byte
{
    Func = 0x00,
    Table = 0x01,
    Memory = 0x02,
    Global = 0x03,
    Tag = 0x04,
    Type = 0x10,
    Module = 0x11,
    Instance = 0x12,
}

/// <summary><c>externdesc</c> discriminant.</summary>
internal enum ComponentExternKind : byte
{
    CoreModule = 0x00,
    Func = 0x01,
    Value = 0x02,
    Type = 0x03,
    Component = 0x04,
    Instance = 0x05,
}

/// <summary><c>aliastarget</c> discriminant.</summary>
internal enum ComponentAliasTarget : byte
{
    Export = 0x00,
    CoreExport = 0x01,
    Outer = 0x02,
}

/// <summary><c>instancedecl</c>/<c>componentdecl</c> discriminant.</summary>
internal enum ComponentDeclKind : byte
{
    CoreType = 0x00,
    Type = 0x01,
    Alias = 0x02,
    Import = 0x03,
    Export = 0x04,
}

/// <summary><c>core:moduledecl</c> discriminant.</summary>
internal enum CoreModuleDeclKind : byte
{
    Import = 0x00,
    Type = 0x01,
    Alias = 0x02,
    Export = 0x03,
}

/// <summary><c>instanceexpr</c>/<c>core:instanceexpr</c> discriminant.</summary>
internal enum InstanceExprKind : byte
{
    Instantiate = 0x00,
    FromExports = 0x01,
}

/// <summary>The leading byte of a <c>deftype</c>: primitive and defined value types, then the other type forms.</summary>
internal enum ComponentTypeCode : byte
{
    Bool = 0x7f,
    S8 = 0x7e,
    U8 = 0x7d,
    S16 = 0x7c,
    U16 = 0x7b,
    S32 = 0x7a,
    U32 = 0x79,
    S64 = 0x78,
    U64 = 0x77,
    F32 = 0x76,
    F64 = 0x75,
    Char = 0x74,
    String = 0x73,
    Record = 0x72,
    Variant = 0x71,
    List = 0x70,
    Tuple = 0x6f,
    Flags = 0x6e,
    Enum = 0x6d,
    Option = 0x6b,
    Result = 0x6a,
    Own = 0x69,
    Borrow = 0x68,
    FixedList = 0x67,
    Stream = 0x66,
    Future = 0x65,
    ErrorContext = 0x64,
    Map = 0x63,
    Func = 0x40,
    Component = 0x41,
    Instance = 0x42,
    AsyncFunc = 0x43,
    Resource = 0x3f,
    AsyncResource = 0x3e,
}

/// <summary><c>canon</c> opcode.</summary>
internal enum CanonOpcode : byte
{
    Lift = 0x00,
    Lower = 0x01,
    ResourceNew = 0x02,
    ResourceDrop = 0x03,
    ResourceRep = 0x04,
    TaskCancel = 0x05,
    SubtaskCancel = 0x06,
    ResourceDropAsync = 0x07,
    BackpressureSet = 0x08,
    TaskReturn = 0x09,
    ContextGet = 0x0a,
    ContextSet = 0x0b,
    ThreadYield = 0x0c,
    SubtaskDrop = 0x0d,
    StreamNew = 0x0e,
    StreamRead = 0x0f,
    StreamWrite = 0x10,
    StreamCancelRead = 0x11,
    StreamCancelWrite = 0x12,
    StreamDropReadable = 0x13,
    StreamDropWritable = 0x14,
    FutureNew = 0x15,
    FutureRead = 0x16,
    FutureWrite = 0x17,
    FutureCancelRead = 0x18,
    FutureCancelWrite = 0x19,
    FutureDropReadable = 0x1a,
    FutureDropWritable = 0x1b,
    ErrorContextNew = 0x1c,
    ErrorContextDebugMessage = 0x1d,
    ErrorContextDrop = 0x1e,
    WaitableSetNew = 0x1f,
    WaitableSetWait = 0x20,
    WaitableSetPoll = 0x21,
    WaitableSetDrop = 0x22,
    WaitableJoin = 0x23,
    BackpressureInc = 0x24,
    BackpressureDec = 0x25,
    ThreadIndex = 0x26,
    ThreadNewIndirect = 0x27,
    ThreadResumeLater = 0x28,
    ThreadSuspend = 0x29,
    ThreadSuspendThenResume = 0x2a,
    ThreadYieldThenResume = 0x2b,
    ThreadSuspendThenPromote = 0x2c,
    ThreadYieldThenPromote = 0x2d,
    ThreadSpawnRef = 0x40,
    ThreadSpawnIndirect = 0x41,
    ThreadAvailableParallelism = 0x42,
}

/// <summary><c>canonopt</c> discriminant.</summary>
internal enum CanonOption : byte
{
    StringUtf8 = 0x00,
    StringUtf16 = 0x01,
    StringLatin1Utf16 = 0x02,
    Memory = 0x03,
    Realloc = 0x04,
    PostReturn = 0x05,
    Async = 0x06,
    Callback = 0x07,
}

/// <summary><c>typebound</c> discriminant.</summary>
internal enum TypeBoundKind : byte
{
    Eq = 0x00,
    SubResource = 0x01,
}

/// <summary><c>valuebound</c> discriminant.</summary>
internal enum ValueBoundKind : byte
{
    Value = 0x00,
    ValType = 0x01,
}
