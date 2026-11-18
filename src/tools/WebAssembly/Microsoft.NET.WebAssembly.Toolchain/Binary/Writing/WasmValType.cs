// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using Microsoft.NET.WebAssembly.Types;

namespace Microsoft.NET.WebAssembly.Binary.Writing;

/// <summary>
/// A value type: a number (<c>i32</c>, <c>i64</c>, <c>f32</c>, <c>f64</c>), <c>v128</c>, or a reference type. A
/// reference type is either a single-byte shorthand (<c>funcref</c>, <c>externref</c>, <c>exnref</c>, ...) or
/// <c>(ref null ht)</c> / <c>(ref ht)</c> with any heap type, including a concrete type index, as the GC and function
/// references proposals use. A single-byte <see cref="ValType"/> converts implicitly. <c>default</c> is not a value
/// type, and the writers reject it.
/// </summary>
/// <remarks>
/// Each value is written exactly as it was made: <see cref="FuncRef"/> is the shorthand byte <c>0x70</c>, while
/// <c>Ref(WasmHeapType.Func, nullable: true)</c> is the two bytes <c>0x63 0x70</c>. The two denote the same type but
/// are different encodings, and they are not equal.
/// </remarks>
public readonly struct WasmValType : IEquatable<WasmValType>
{
    // The s33 heap type that follows a RefNull or Ref prefix.
    private readonly long _heapType;

    // A single-byte type code, or the RefNull/Ref prefix. 0x00 is no value type, so it marks default.
    private readonly byte _code;

    private WasmValType(byte code, long heapType = 0)
    {
        _code = code;
        _heapType = heapType;
    }

    internal const string DefaultMessage =
        "default(WasmValType) is not a value type; use I32, I64, F32, F64, V128, a reference shorthand, Ref or a ValType.";

    /// <summary><c>i32</c>.</summary>
    public static WasmValType I32 => new((byte)ValType.I32);

    /// <summary><c>i64</c>.</summary>
    public static WasmValType I64 => new((byte)ValType.I64);

    /// <summary><c>f32</c>.</summary>
    public static WasmValType F32 => new((byte)ValType.F32);

    /// <summary><c>f64</c>.</summary>
    public static WasmValType F64 => new((byte)ValType.F64);

    /// <summary><c>v128</c>.</summary>
    public static WasmValType V128 => new((byte)ValType.V128);

    /// <summary><c>funcref</c>, the shorthand <c>0x70</c>.</summary>
    public static WasmValType FuncRef => new((byte)ValType.FuncRef);

    /// <summary><c>externref</c>, the shorthand <c>0x6F</c>.</summary>
    public static WasmValType ExternRef => new((byte)ValType.ExternRef);

    /// <summary><c>exnref</c>, the shorthand <c>0x69</c>.</summary>
    public static WasmValType ExnRef => new((byte)ValType.ExnRef);

    /// <summary>
    /// <c>(ref null ht)</c> when <paramref name="nullable"/>, <c>(ref ht)</c> otherwise, written with its prefix byte
    /// (<c>0x63</c> or <c>0x64</c>) and the heap type.
    /// </summary>
    /// <exception cref="ArgumentException"><paramref name="heapType"/> is <c>default</c>.</exception>
    public static WasmValType Ref(WasmHeapType heapType, bool nullable)
    {
        if (!heapType.IsSet)
            throw new ArgumentException(WasmHeapType.DefaultMessage, nameof(heapType));
        return new(nullable ? RefTypePrefix.RefNull : RefTypePrefix.Ref, heapType.Code);
    }

    /// <summary>The single-byte value type <paramref name="type"/>: a number, <c>v128</c> or a reference shorthand.</summary>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a single-byte value type.</exception>
    public static WasmValType FromValType(ValType type)
    {
        WasmCodeWriter.CheckValueType(type);
        return new((byte)type);
    }

    /// <summary>The single-byte value type <paramref name="type"/>: a number, <c>v128</c> or a reference shorthand.</summary>
    /// <remarks>
    /// Implicit even though it throws for a <see cref="ValType"/> that is no single-byte value type (such as the prefix
    /// <c>0x63</c>), which conversions usually should not: <see cref="ValType"/> is what nesm's model and dotnet/runtime's
    /// ObjectWriter hold, and the writers took <see cref="ValType"/> before they took this type, so an explicit
    /// conversion would break every such call. A <see cref="ValType"/> that is no value type is a caller bug, not data.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="type"/> is not a single-byte value type.</exception>
    public static implicit operator WasmValType(ValType type) => FromValType(type);

    /// <summary>Whether this is a reference type: a shorthand, <c>(ref null ht)</c> or <c>(ref ht)</c>.</summary>
    public bool IsReference => _code != 0 && _code is not (>= (byte)ValType.V128 and <= (byte)ValType.I32);

    /// <summary>Whether this is a nullable reference type: a shorthand or <c>(ref null ht)</c>.</summary>
    public bool IsNullable => IsReference && _code != RefTypePrefix.Ref;

    internal bool IsSet => _code != 0;

    private bool HasHeapType => _code is RefTypePrefix.RefNull or RefTypePrefix.Ref;

    /// <summary>The number of bytes the type is written as.</summary>
    internal int EncodedLength => HasHeapType ? 1 + WasmLeb128.GetSLeb128Size(_heapType) : 1;

    /// <summary>Writes the type to <paramref name="destination"/> (at least <see cref="EncodedLength"/> bytes) and returns its length.</summary>
    internal int Encode(Span<byte> destination)
    {
        destination[0] = _code;
        return HasHeapType ? 1 + WasmLeb128.WriteSLeb128(destination[1..], _heapType) : 1;
    }

    /// <summary>Throws unless this is a value type, not <c>default</c>.</summary>
    internal void Check(string paramName)
    {
        if (!IsSet)
            throw new ArgumentException(DefaultMessage, paramName);
    }

    /// <inheritdoc />
    public bool Equals(WasmValType other) => _code == other._code && _heapType == other._heapType;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is WasmValType other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(_code, _heapType);

    /// <summary>Whether two value types have the same encoding.</summary>
    public static bool operator ==(WasmValType left, WasmValType right) => left.Equals(right);

    /// <summary>Whether two value types have different encodings.</summary>
    public static bool operator !=(WasmValType left, WasmValType right) => !left.Equals(right);

    /// <summary>The type in the text format: <c>i32</c>, <c>anyref</c>, <c>(ref null 3)</c>, ...</summary>
    public override string ToString() => _code switch
    {
        0 => "default",
        RefTypePrefix.RefNull => $"(ref null {HeapTypeText(_heapType)})",
        RefTypePrefix.Ref => $"(ref {HeapTypeText(_heapType)})",
        (byte)ValType.I32 => "i32",
        (byte)ValType.I64 => "i64",
        (byte)ValType.F32 => "f32",
        (byte)ValType.F64 => "f64",
        (byte)ValType.V128 => "v128",
        _ => AbstractHeapTypeCodes.TryFromCode(_code, out HeapTypeKind kind) ? ShorthandText(kind) : $"0x{_code:X2}",
    };

    // The bottom types' shorthands are null*ref (nullref, nullfuncref, ...); the others are the heap type and "ref".
    private static string ShorthandText(HeapTypeKind kind) => kind switch
    {
        HeapTypeKind.None => "nullref",
        HeapTypeKind.NoFunc => "nullfuncref",
        HeapTypeKind.NoExtern => "nullexternref",
        HeapTypeKind.NoExn => "nullexnref",
        HeapTypeKind.NoCont => "nullcontref",
        _ => kind.ToString().ToLowerInvariant() + "ref",
    };

    private static string HeapTypeText(long heapType) =>
        heapType >= 0 ? heapType.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : AbstractHeapTypeCodes.TryFromCode((byte)(heapType + 0x80), out HeapTypeKind kind) ? kind.ToString().ToLowerInvariant()
            : heapType.ToString(System.Globalization.CultureInfo.InvariantCulture);
}
