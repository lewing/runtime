// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Binary;

// DWARF encodings (DWARF 5 §7, plus the GNU and LLVM values nesm meets in wasm producers).
// DW_FORM is a closed set that readers switch on, so it is an enum. Tags, attributes, operations and
// line-program opcodes have vendor ranges and are compared as numbers, so they are static classes of
// constants. Member names drop the DW_xxx_ prefix: DwAt.LowPc is DW_AT_low_pc.

/// <summary>Attribute forms (<c>DW_FORM_*</c>).</summary>
internal enum DwForm : uint
{
    Addr = 0x01,
    Block2 = 0x03,
    Block4 = 0x04,
    Data2 = 0x05,
    Data4 = 0x06,
    Data8 = 0x07,
    String = 0x08,
    Block = 0x09,
    Block1 = 0x0a,
    Data1 = 0x0b,
    Flag = 0x0c,
    Sdata = 0x0d,
    Strp = 0x0e,
    Udata = 0x0f,
    RefAddr = 0x10,
    Ref1 = 0x11,
    Ref2 = 0x12,
    Ref4 = 0x13,
    Ref8 = 0x14,
    RefUdata = 0x15,
    Indirect = 0x16,
    SecOffset = 0x17,
    Exprloc = 0x18,
    FlagPresent = 0x19,
    Strx = 0x1a,
    Addrx = 0x1b,
    RefSup4 = 0x1c,
    StrpSup = 0x1d,
    Data16 = 0x1e,
    LineStrp = 0x1f,
    RefSig8 = 0x20,
    ImplicitConst = 0x21,
    Loclistx = 0x22,
    Rnglistx = 0x23,
    RefSup8 = 0x24,
    Strx1 = 0x25,
    Strx2 = 0x26,
    Strx3 = 0x27,
    Strx4 = 0x28,
    Addrx1 = 0x29,
    Addrx2 = 0x2a,
    Addrx3 = 0x2b,
    Addrx4 = 0x2c,
    GnuAddrIndex = 0x1f01,
    GnuStrIndex = 0x1f02,
    GnuRefAlt = 0x1f20,
    GnuStrpAlt = 0x1f21,
}

/// <summary>Debugging-information entry tags (<c>DW_TAG_*</c>).</summary>
internal static class DwTag
{
    public const ushort ArrayType = 0x01;
    public const ushort ClassType = 0x02;
    public const ushort EnumerationType = 0x04;
    public const ushort FormalParameter = 0x05;
    public const ushort Member = 0x0d;
    public const ushort PointerType = 0x0f;
    public const ushort ReferenceType = 0x10;
    public const ushort CompileUnit = 0x11;
    public const ushort StructureType = 0x13;
    public const ushort SubroutineType = 0x15;
    public const ushort Typedef = 0x16;
    public const ushort UnionType = 0x17;
    public const ushort InlinedSubroutine = 0x1d;
    public const ushort Enumerator = 0x28;
    public const ushort BaseType = 0x24;
    public const ushort ConstType = 0x26;
    public const ushort Subprogram = 0x2e;
    public const ushort Variable = 0x34;
    public const ushort VolatileType = 0x35;
    public const ushort RestrictType = 0x37;
    public const ushort PartialUnit = 0x3c;
}

/// <summary>Attribute names (<c>DW_AT_*</c>).</summary>
internal static class DwAt
{
    public const ushort Location = 0x02;
    public const ushort Name = 0x03;
    public const ushort ByteSize = 0x0b;
    public const ushort StmtList = 0x10;
    public const ushort LowPc = 0x11;
    public const ushort HighPc = 0x12;
    public const ushort Language = 0x13;
    public const ushort Producer = 0x25;
    public const ushort ReturnAddr = 0x2a;
    public const ushort AbstractOrigin = 0x31;
    public const ushort ConstValue = 0x1c;
    public const ushort StringLength = 0x19;
    public const ushort DataMemberLocation = 0x38;
    public const ushort Declaration = 0x3c;
    public const ushort FrameBase = 0x40;
    public const ushort MacroInfo = 0x43;
    public const ushort Segment = 0x46;
    public const ushort StaticLink = 0x48;
    public const ushort UseLocation = 0x4a;
    public const ushort VtableElemLocation = 0x4d;
    public const ushort Specification = 0x47;
    public const ushort Type = 0x49;
    public const ushort EntryPc = 0x52;
    public const ushort Ranges = 0x55;
    public const ushort CallLine = 0x59;
    public const ushort LinkageName = 0x6e;
    public const ushort StrOffsetsBase = 0x72;
    public const ushort AddrBase = 0x73;
    public const ushort MipsLinkageName = 0x2007;
    public const ushort GnuMacros = 0x2119;
}

/// <summary>Location-expression operations (<c>DW_OP_*</c>).</summary>
internal static class DwOp
{
    public const byte Addr = 0x03;
    public const byte Deref = 0x06;
    public const byte Const1u = 0x08;
    public const byte Const1s = 0x09;
    public const byte Const2u = 0x0a;
    public const byte Const2s = 0x0b;
    public const byte Const4u = 0x0c;
    public const byte Const4s = 0x0d;
    public const byte Const8u = 0x0e;
    public const byte Const8s = 0x0f;
    public const byte Constu = 0x10;
    public const byte Consts = 0x11;
    public const byte Dup = 0x12;
    public const byte Drop = 0x13;
    public const byte Over = 0x14;
    public const byte Pick = 0x15;
    public const byte Swap = 0x16;
    public const byte Plus = 0x22;
    public const byte PlusUconst = 0x23;
    public const byte Shl = 0x24;
    public const byte Xor = 0x27;
    public const byte Bra = 0x28;
    public const byte Eq = 0x29;
    public const byte Ne = 0x2e;
    public const byte Skip = 0x2f;
    public const byte Lit0 = 0x30;
    public const byte Reg31 = 0x6f;
    public const byte Breg0 = 0x70;
    public const byte Breg31 = 0x8f;
    public const byte Regx = 0x90;
    public const byte Fbreg = 0x91;
    public const byte Bregx = 0x92;
    public const byte Piece = 0x93;
    public const byte DerefSize = 0x94;
    public const byte XderefSize = 0x95;
    public const byte Nop = 0x96;
    public const byte PushObjectAddress = 0x97;
    public const byte Call2 = 0x98;
    public const byte Call4 = 0x99;
    public const byte CallRef = 0x9a;
    public const byte FormTlsAddress = 0x9b;
    public const byte CallFrameCfa = 0x9c;
    public const byte BitPiece = 0x9d;
    public const byte ImplicitValue = 0x9e;
    public const byte StackValue = 0x9f;
    public const byte EntryValue = 0xa3;
    public const byte GnuPushTlsAddress = 0xe0;
    public const byte GnuEntryValue = 0xf3;

    /// <summary>The WebAssembly extension: a wasm local, global or operand-stack slot (LLVM).</summary>
    public const byte WasmLocation = 0xed;
}

/// <summary>Unit header types (<c>DW_UT_*</c>, DWARF 5).</summary>
internal static class DwUt
{
    public const byte Compile = 0x01;
    public const byte Partial = 0x03;
}

/// <summary>Standard line-number program opcodes (<c>DW_LNS_*</c>).</summary>
internal static class DwLns
{
    public const byte Copy = 1;
    public const byte AdvancePc = 2;
    public const byte AdvanceLine = 3;
    public const byte SetFile = 4;
    public const byte SetColumn = 5;
    public const byte NegateStmt = 6;
    public const byte SetBasicBlock = 7;
    public const byte ConstAddPc = 8;
    public const byte FixedAdvancePc = 9;
    public const byte SetPrologueEnd = 10;
    public const byte SetEpilogueBegin = 11;
    public const byte SetIsa = 12;
}

/// <summary>Extended line-number program opcodes (<c>DW_LNE_*</c>).</summary>
internal static class DwLne
{
    public const byte EndSequence = 1;
    public const byte SetAddress = 2;
    public const byte DefineFile = 3;
    public const byte SetDiscriminator = 4;
}

/// <summary>Line-table entry content types (<c>DW_LNCT_*</c>).</summary>
internal static class DwLnct
{
    public const uint Path = 1;
    public const uint DirectoryIndex = 2;
}

/// <summary>Special values of a unit's initial length field (DWARF 5 §7.4).</summary>
internal static class DwarfUnitLength
{
    /// <summary>The escape that introduces a 64-bit DWARF unit: an 8-byte length follows.</summary>
    public const uint Dwarf64Escape = 0xFFFF_FFFF;

    /// <summary>The first of the reserved values (<c>0xfffffff0</c>–<c>0xfffffffe</c>).</summary>
    public const uint ReservedMin = 0xFFFF_FFF0;
}
