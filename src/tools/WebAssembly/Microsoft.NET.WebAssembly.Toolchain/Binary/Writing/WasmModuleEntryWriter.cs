// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Buffers;
using System.Runtime.CompilerServices;
using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// Writes the entries of a module's type, import, function, table, memory, global, export, element and tag sections to
/// an <see cref="IBufferWriter{T}"/>; the section's entry count and framing are the caller's (see
/// <see cref="WasmModuleWriter"/>). Names are UTF-8 bytes, written as given. Value types and table element types are
/// <see cref="WasmValType"/>s, so GC and function references types such as <c>(ref null $t)</c> can be written; a
/// <see cref="ValType"/> converts implicitly. An index that may be relocated takes a
/// width it is padded to, and its writer-relative position (bytes since this writer was created) is returned so the
/// caller can record a relocation against it. Start the writer at the section's content and write the entry count with
/// <see cref="Count"/> first, and positions are also section-relative offsets into the section's content, as
/// <c>reloc.*</c> entries need; otherwise convert them with <see cref="RelocationSectionEncoder.SectionOffset"/>.
/// </summary>
public sealed class WasmModuleEntryWriter
{
    private const byte ExceptionTagAttribute = 0x00;

    private readonly IBufferWriter<byte> _output;

    /// <summary>Creates a writer that appends to <paramref name="output"/>.</summary>
    public WasmModuleEntryWriter(IBufferWriter<byte> output)
    {
        ArgumentNullException.ThrowIfNull(output);
        _output = output;
    }

    /// <summary>The number of bytes this writer has written: the writer-relative position of the next entry.</summary>
    public long Position { get; private set; }

    /// <summary>Writes a section's entry count (a minimal LEB128), which comes before its first entry.</summary>
    public void Count(uint count) => WriteU(count, 0);

    // ── Type section ─────────────────────────────────────────────────────────

    /// <summary>Writes a function type: <c>0x60</c>, the parameter types and the result types.</summary>
    public void FuncType(ReadOnlySpan<WasmValType> parameters, ReadOnlySpan<WasmValType> results)
    {
        CheckValueTypes(parameters, nameof(parameters));
        CheckValueTypes(results, nameof(results));
        WriteByte((byte)CompositeTypeCode.Func);
        WriteTypes(parameters);
        WriteTypes(results);
    }

    /// <summary>Writes a function type with single-byte parameter and result types.</summary>
    // Preferred where both apply (an empty or all-ValType collection expression), which would otherwise be ambiguous.
    [OverloadResolutionPriority(1)]
    public void FuncType(ReadOnlySpan<ValType> parameters, ReadOnlySpan<ValType> results)
    {
        CheckValueTypes(parameters, nameof(parameters));
        CheckValueTypes(results, nameof(results));
        WriteByte((byte)CompositeTypeCode.Func);
        WriteTypes(parameters);
        WriteTypes(results);
    }

    // ── Import section ───────────────────────────────────────────────────────

    /// <summary>Writes a function import and returns the type index's position.</summary>
    /// <param name="module">The module name.</param>
    /// <param name="name">The field name.</param>
    /// <param name="typeIndex">The function's type.</param>
    /// <param name="typeIndexWidth">0 for a minimal LEB128, or the width it is padded to (1–5).</param>
    public long FunctionImport(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, uint typeIndex, int typeIndexWidth = 0)
    {
        CheckWidth(typeIndex, typeIndexWidth);
        ImportHeader(module, name, WasmExternalKind.Function);
        return WriteU(typeIndex, typeIndexWidth);
    }

    /// <summary>Writes a table import: the reference type and limits.</summary>
    public void TableImport(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, WasmValType elementType, ulong min, ulong? max = null, bool is64 = false)
    {
        CheckRefType(elementType);
        CheckLimits(min, max, is64, shared: false);
        ImportHeader(module, name, WasmExternalKind.Table);
        WriteValueType(elementType);
        WriteLimits(min, max, is64, shared: false);
    }

    /// <summary>Writes a memory import: its limits (64-bit with memory64, shared with threads).</summary>
    public void MemoryImport(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, ulong min, ulong? max = null, bool is64 = false, bool shared = false)
    {
        CheckLimits(min, max, is64, shared);
        ImportHeader(module, name, WasmExternalKind.Memory);
        WriteLimits(min, max, is64, shared);
    }

    /// <summary>Writes a global import: the value type and mutability.</summary>
    public void GlobalImport(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, WasmValType valueType, Mutability mutability)
    {
        valueType.Check(nameof(valueType));
        CheckMutability(mutability);
        ImportHeader(module, name, WasmExternalKind.Global);
        WriteValueType(valueType);
        WriteByte((byte)mutability);
    }

    /// <summary>Writes an exception tag import and returns the type index's position.</summary>
    /// <param name="module">The module name.</param>
    /// <param name="name">The field name.</param>
    /// <param name="typeIndex">The tag's function type.</param>
    /// <param name="typeIndexWidth">0 for a minimal LEB128, or the width it is padded to (1–5).</param>
    public long TagImport(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, uint typeIndex, int typeIndexWidth = 0)
    {
        CheckWidth(typeIndex, typeIndexWidth);
        ImportHeader(module, name, WasmExternalKind.Tag);
        WriteByte(ExceptionTagAttribute);
        return WriteU(typeIndex, typeIndexWidth);
    }

    // ── Function, table, memory, global and tag sections ─────────────────────

    /// <summary>Writes a function section entry (the function's type index) and returns its position.</summary>
    public long Function(uint typeIndex, int width = 0)
    {
        CheckWidth(typeIndex, width);
        return WriteU(typeIndex, width);
    }

    /// <summary>
    /// Writes a table section entry: the reference type and limits. The element type must be nullable, since this
    /// form has no initializer expression to fill a table of non-nullable references.
    /// </summary>
    public void Table(WasmValType elementType, ulong min, ulong? max = null, bool is64 = false)
    {
        CheckRefType(elementType);
        if (!elementType.IsNullable)
            throw new ArgumentException($"A table of {elementType} needs an initializer expression, which this entry does not have.", nameof(elementType));
        CheckLimits(min, max, is64, shared: false);
        WriteValueType(elementType);
        WriteLimits(min, max, is64, shared: false);
    }

    /// <summary>Writes a memory section entry: its limits.</summary>
    public void Memory(ulong min, ulong? max = null, bool is64 = false, bool shared = false)
    {
        CheckLimits(min, max, is64, shared);
        WriteLimits(min, max, is64, shared);
    }

    /// <summary>Writes a global section entry: the value type, mutability and initializer, copied as given.</summary>
    /// <param name="valueType">The global's value type.</param>
    /// <param name="mutability">Whether the global is mutable.</param>
    /// <param name="initExpression">A constant expression including its <c>end</c>.</param>
    public void Global(WasmValType valueType, Mutability mutability, ReadOnlySpan<byte> initExpression)
    {
        valueType.Check(nameof(valueType));
        CheckMutability(mutability);
        DataSegmentEncoder.CheckExpression(initExpression, "An initializer expression", nameof(initExpression));
        WriteValueType(valueType);
        WriteByte((byte)mutability);
        WriteBytes(initExpression);
    }

    /// <summary>Writes a tag section entry and returns the type index's position.</summary>
    public long Tag(uint typeIndex, int width = 0)
    {
        CheckWidth(typeIndex, width);
        WriteByte(ExceptionTagAttribute);
        return WriteU(typeIndex, width);
    }

    // ── Export and element sections ──────────────────────────────────────────

    /// <summary>Writes an export and returns the index's position.</summary>
    /// <param name="name">The export name.</param>
    /// <param name="kind">What is exported.</param>
    /// <param name="index">The exported item's index.</param>
    /// <param name="indexWidth">0 for a minimal LEB128, or the width it is padded to (1–5).</param>
    public long Export(ReadOnlySpan<byte> name, WasmExternalKind kind, uint index, int indexWidth = 0)
    {
        CheckKind(kind);
        CheckWidth(index, indexWidth);
        WriteVector(name);
        WriteByte((byte)kind);
        return WriteU(index, indexWidth);
    }

    /// <summary>
    /// Writes an active element segment for table 0 with function indices (flag 0, which implies <c>funcref</c>).
    /// The indices are minimal LEB128s: wasm-ld doesn't apply relocations to the element section, so a relocatable
    /// object reaches functions through <c>R_WASM_TABLE_INDEX_*</c> relocations in code and data instead.
    /// </summary>
    /// <param name="offsetExpression">A constant expression including its <c>end</c>.</param>
    /// <param name="functionIndices">The functions, in table order.</param>
    public void ActiveElementSegment(ReadOnlySpan<byte> offsetExpression, ReadOnlySpan<uint> functionIndices)
    {
        DataSegmentEncoder.CheckExpression(offsetExpression, "An offset expression", nameof(offsetExpression));
        WriteByte((byte)ElementSegmentKind.ActiveFunctions);
        WriteBytes(offsetExpression);
        WriteU((ulong)functionIndices.Length, 0);
        foreach (uint functionIndex in functionIndices)
            WriteU(functionIndex, 0);
    }

    // ── Encoding ─────────────────────────────────────────────────────────────

    private void ImportHeader(ReadOnlySpan<byte> module, ReadOnlySpan<byte> name, WasmExternalKind kind)
    {
        WriteVector(module);
        WriteVector(name);
        WriteByte((byte)kind);
    }

    private void WriteLimits(ulong min, ulong? max, bool is64, bool shared)
    {
        WasmLimitsFlags flags = (max.HasValue ? WasmLimitsFlags.HasMaximum : WasmLimitsFlags.None) |
            (shared ? WasmLimitsFlags.Shared : WasmLimitsFlags.None) |
            (is64 ? WasmLimitsFlags.Is64 : WasmLimitsFlags.None);
        WriteByte((byte)flags);
        WriteU(min, 0);
        if (max.HasValue)
            WriteU(max.Value, 0);
    }

    private static void CheckLimits(ulong min, ulong? max, bool is64, bool shared)
    {
        if (!is64 && min > uint.MaxValue)
            throw new ArgumentException("32-bit limits must fit in 32 bits; pass is64 for memory64/table64.", nameof(min));
        if (!is64 && max > uint.MaxValue)
            throw new ArgumentException("32-bit limits must fit in 32 bits; pass is64 for memory64/table64.", nameof(max));
        if (max < min)
            throw new ArgumentException($"The maximum {max} is less than the minimum {min}.", nameof(max));
        if (shared && max is null)
            throw new ArgumentException("A shared memory must have a maximum.", nameof(max));
    }

    private static void CheckMutability(Mutability mutability)
    {
        if (mutability is not (Mutability.Const or Mutability.Var))
            throw new ArgumentOutOfRangeException(nameof(mutability), mutability, "Unknown mutability.");
    }

    private static void CheckValueTypes(ReadOnlySpan<WasmValType> types, string paramName)
    {
        foreach (WasmValType type in types)
            type.Check(paramName);
    }

    private static void CheckValueTypes(ReadOnlySpan<ValType> types, string paramName)
    {
        foreach (ValType type in types)
            WasmCodeWriter.CheckValueType(type, paramName);
    }

    // Tables hold references, not numbers or vectors.
    private static void CheckRefType(WasmValType elementType)
    {
        elementType.Check(nameof(elementType));
        if (!elementType.IsReference)
            throw new ArgumentException($"{elementType} is not a reference type.", nameof(elementType));
    }

    private void WriteTypes(ReadOnlySpan<WasmValType> types)
    {
        WriteU((ulong)types.Length, 0);
        foreach (WasmValType type in types)
            WriteValueType(type);
    }

    private void WriteTypes(ReadOnlySpan<ValType> types)
    {
        WriteU((ulong)types.Length, 0);
        foreach (ValType type in types)
            WriteByte((byte)type);
    }

    private void WriteValueType(WasmValType type) => Advance(type.Encode(_output.GetSpan(1 + WasmLeb128.MaxLength)));

    private static void CheckKind(WasmExternalKind kind)
    {
        if (kind > WasmExternalKind.Tag)
            throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown external kind.");
    }

    private static void CheckWidth(uint value, int width)
    {
        if (width is < 0 or > WasmLeb128.MaxLength32)
            throw new ArgumentOutOfRangeException(nameof(width), width, "A width is 1 to 5 bytes (0 for minimal).");
        if (width != 0 && WasmLeb128.GetULeb128Size(value) > width)
            throw new ArgumentException($"{value} does not fit a {width}-byte LEB128.", nameof(width));
    }

    private void WriteVector(ReadOnlySpan<byte> bytes)
    {
        WriteU((ulong)bytes.Length, 0);
        WriteBytes(bytes);
    }

    private long WriteU(ulong value, int width)
    {
        long position = Position;
        Span<byte> span = _output.GetSpan(WasmLeb128.MaxLength);
        int length;
        if (width == 0)
        {
            length = WasmLeb128.WriteULeb128(span, value);
        }
        else
        {
            WasmLeb128.WritePaddedULeb128(span[..width], value);
            length = width;
        }

        Advance(length);
        return position;
    }

    private void WriteByte(byte value)
    {
        _output.GetSpan(1)[0] = value;
        Advance(1);
    }

    private void WriteBytes(ReadOnlySpan<byte> bytes)
    {
        bytes.CopyTo(_output.GetSpan(bytes.Length));
        Advance(bytes.Length);
    }

    private void Advance(int count)
    {
        _output.Advance(count);
        Position += count;
    }
}
