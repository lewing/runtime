// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

// Ported from LLVM's llvm/lib/Demangle/RustDemangle.cpp
// (llvm-project 895aa2c896ada719451be2e3673c83da8ddf1141, LLVM 23), which is part of the LLVM Project,
// under the Apache License v2.0 with LLVM Exceptions. See https://llvm.org/LICENSE.txt for license information.
// SPDX-License-Identifier: Apache-2.0 WITH LLVM-exception

// SA1136: the compact enum lists mirror RustDemangle.cpp's enum declarations, one value per line would break
// that mapping. This file is a line-for-line port kept diffable against LLVM.
#pragma warning disable SA1136

using System.Globalization;
using System.Text;

namespace Microsoft.NET.WebAssembly.Linker.Demangling;

/// <summary>
/// LLVM's Rust v0 demangler (<c>rustDemangle</c>, https://rust-lang.github.io/rfcs/2603-rust-symbol-name-mangling-v0.html).
/// Input and output are byte strings, one char per byte.
/// </summary>
internal sealed class RustDemangler
{
    private enum BasicType
    {
        Bool, Char, I8, I16, I32, I64, I128, ISize, U8, U16, U32, U64, U128, USize, F32, F64, Str, Placeholder, Unit,
        Variadic, Never,
    }

    private readonly record struct Identifier(string Name, bool Punycode)
    {
        public bool IsEmpty => Name.Length == 0;
    }

    // LLVM's maximum recursion level, which keeps deep inputs from overflowing the stack.
    private const int MaxRecursionLevel = 500;

    private readonly StringBuilder _output = new();
    private int _recursionLevel;
    private ulong _boundLifetimes;
    private string _input = "";
    private int _position;
    private bool _print = true;
    private bool _error;

    /// <summary>The demangled bytes, or null when <paramref name="mangled"/> is not a valid Rust v0 symbol.</summary>
    public static string? Demangle(string mangled)
    {
        if (!mangled.StartsWith("_R", StringComparison.Ordinal))
            return null;
        var demangler = new RustDemangler();
        return demangler.Run(mangled[2..]) ? demangler._output.ToString() : null;
    }

    // <symbol-name> = "_R" <path> [<instantiating-crate>]
    private bool Run(string mangled)
    {
        int dot = mangled.IndexOf('.');
        _input = dot < 0 ? mangled : mangled[..dot];

        DemanglePath(inType: false);

        if (_position != _input.Length)
        {
            bool savedPrint = _print;
            _print = false;
            DemanglePath(inType: false);
            _print = savedPrint;
        }

        if (_position != _input.Length)
            _error = true;

        if (dot >= 0)
        {
            Print(" (");
            Print(mangled[dot..]);
            Print(")");
        }

        return !_error;
    }

    // <path> = "C" <identifier>               // crate root
    //        | "M" <impl-path> <type>         // <T> (inherent impl)
    //        | "X" <impl-path> <type> <path>  // <T as Trait> (trait impl)
    //        | "Y" <type> <path>              // <T as Trait> (trait definition)
    //        | "N" <ns> <path> <identifier>   // ...::ident (nested path)
    //        | "I" <path> {<generic-arg>} "E" // ...<T, U> (generic args)
    //        | <backref>
    // Returns whether the generic arguments were left open (no closing '>').
    private bool DemanglePath(bool inType, bool leaveOpen = false)
    {
        if (_error || _recursionLevel >= MaxRecursionLevel)
        {
            _error = true;
            return false;
        }

        _recursionLevel++;
        try
        {
            switch (Consume())
            {
                case 'C':
                    ParseOptionalBase62Number('s');
                    PrintIdentifier(ParseIdentifier());
                    break;
                case 'M':
                    DemangleImplPath(inType);
                    Print("<");
                    DemangleType();
                    Print(">");
                    break;
                case 'X':
                    DemangleImplPath(inType);
                    Print("<");
                    DemangleType();
                    Print(" as ");
                    DemanglePath(inType: true);
                    Print(">");
                    break;
                case 'Y':
                    Print("<");
                    DemangleType();
                    Print(" as ");
                    DemanglePath(inType: true);
                    Print(">");
                    break;
                case 'N':
                {
                    char ns = Consume();
                    if (!IsLower(ns) && !IsUpper(ns))
                    {
                        _error = true;
                        break;
                    }

                    DemanglePath(inType);
                    ulong disambiguator = ParseOptionalBase62Number('s');
                    Identifier ident = ParseIdentifier();
                    if (IsUpper(ns))
                    {
                        // Special namespaces.
                        Print("::{");
                        if (ns == 'C')
                            Print("closure");
                        else if (ns == 'S')
                            Print("shim");
                        else
                            Print(ns);
                        if (!ident.IsEmpty)
                        {
                            Print(":");
                            PrintIdentifier(ident);
                        }

                        Print('#');
                        PrintDecimalNumber(disambiguator);
                        Print('}');
                    }
                    else if (!ident.IsEmpty)
                    {
                        // Implementation-internal namespaces.
                        Print("::");
                        PrintIdentifier(ident);
                    }

                    break;
                }
                case 'I':
                    DemanglePath(inType);
                    // "::" is optional in a type, and omitted there.
                    if (!inType)
                        Print("::");
                    Print("<");
                    for (int i = 0; !_error && !ConsumeIf('E'); ++i)
                    {
                        if (i > 0)
                            Print(", ");
                        DemangleGenericArg();
                    }

                    if (leaveOpen)
                        return true;
                    Print(">");
                    break;
                case 'B':
                {
                    bool isOpen = false;
                    DemangleBackref(() => isOpen = DemanglePath(inType, leaveOpen));
                    return isOpen;
                }
                default:
                    _error = true;
                    break;
            }

            return false;
        }
        finally
        {
            _recursionLevel--;
        }
    }

    // <impl-path> = [<disambiguator>] <path>
    private void DemangleImplPath(bool inType)
    {
        bool savedPrint = _print;
        _print = false;
        ParseOptionalBase62Number('s');
        DemanglePath(inType);
        _print = savedPrint;
    }

    // <generic-arg> = <lifetime> | <type> | "K" <const>
    private void DemangleGenericArg()
    {
        if (ConsumeIf('L'))
            PrintLifetime(ParseBase62Number());
        else if (ConsumeIf('K'))
            DemangleConst();
        else
            DemangleType();
    }

    private static bool ParseBasicType(char c, out BasicType type)
    {
        type = c switch
        {
            'a' => BasicType.I8,
            'b' => BasicType.Bool,
            'c' => BasicType.Char,
            'd' => BasicType.F64,
            'e' => BasicType.Str,
            'f' => BasicType.F32,
            'h' => BasicType.U8,
            'i' => BasicType.ISize,
            'j' => BasicType.USize,
            'l' => BasicType.I32,
            'm' => BasicType.U32,
            'n' => BasicType.I128,
            'o' => BasicType.U128,
            'p' => BasicType.Placeholder,
            's' => BasicType.I16,
            't' => BasicType.U16,
            'u' => BasicType.Unit,
            'v' => BasicType.Variadic,
            'x' => BasicType.I64,
            'y' => BasicType.U64,
            'z' => BasicType.Never,
            _ => (BasicType)(-1),
        };
        return type != (BasicType)(-1);
    }

    private void PrintBasicType(BasicType type) => Print(type switch
    {
        BasicType.Bool => "bool",
        BasicType.Char => "char",
        BasicType.I8 => "i8",
        BasicType.I16 => "i16",
        BasicType.I32 => "i32",
        BasicType.I64 => "i64",
        BasicType.I128 => "i128",
        BasicType.ISize => "isize",
        BasicType.U8 => "u8",
        BasicType.U16 => "u16",
        BasicType.U32 => "u32",
        BasicType.U64 => "u64",
        BasicType.U128 => "u128",
        BasicType.USize => "usize",
        BasicType.F32 => "f32",
        BasicType.F64 => "f64",
        BasicType.Str => "str",
        BasicType.Placeholder => "_",
        BasicType.Unit => "()",
        BasicType.Variadic => "...",
        _ => "!",
    });

    // <type> = <basic-type> | <path> | "A" <type> <const> | "S" <type> | "T" {<type>} "E" | "R" [<lifetime>] <type>
    //        | "Q" [<lifetime>] <type> | "P" <type> | "O" <type> | "F" <fn-sig> | "D" <dyn-bounds> <lifetime> | <backref>
    private void DemangleType()
    {
        if (_error || _recursionLevel >= MaxRecursionLevel)
        {
            _error = true;
            return;
        }

        _recursionLevel++;
        try
        {
            int start = _position;
            char c = Consume();
            if (ParseBasicType(c, out BasicType basic))
            {
                PrintBasicType(basic);
                return;
            }

            switch (c)
            {
                case 'A':
                    Print("[");
                    DemangleType();
                    Print("; ");
                    DemangleConst();
                    Print("]");
                    break;
                case 'S':
                    Print("[");
                    DemangleType();
                    Print("]");
                    break;
                case 'T':
                {
                    Print("(");
                    int i = 0;
                    for (; !_error && !ConsumeIf('E'); ++i)
                    {
                        if (i > 0)
                            Print(", ");
                        DemangleType();
                    }

                    if (i == 1)
                        Print(",");
                    Print(")");
                    break;
                }
                case 'R':
                case 'Q':
                    Print('&');
                    if (ConsumeIf('L'))
                    {
                        ulong lifetime = ParseBase62Number();
                        if (lifetime != 0)
                        {
                            PrintLifetime(lifetime);
                            Print(' ');
                        }
                    }

                    if (c == 'Q')
                        Print("mut ");
                    DemangleType();
                    break;
                case 'P':
                    Print("*const ");
                    DemangleType();
                    break;
                case 'O':
                    Print("*mut ");
                    DemangleType();
                    break;
                case 'F':
                    DemangleFnSig();
                    break;
                case 'D':
                    DemangleDynBounds();
                    if (ConsumeIf('L'))
                    {
                        ulong lifetime = ParseBase62Number();
                        if (lifetime != 0)
                        {
                            Print(" + ");
                            PrintLifetime(lifetime);
                        }
                    }
                    else
                    {
                        _error = true;
                    }

                    break;
                case 'B':
                    DemangleBackref(DemangleType);
                    break;
                default:
                    _position = start;
                    DemanglePath(inType: true);
                    break;
            }
        }
        finally
        {
            _recursionLevel--;
        }
    }

    // <fn-sig> := [<binder>] ["U"] ["K" <abi>] {<type>} "E" <type>
    // <abi> = "C" | <undisambiguated-identifier>
    private void DemangleFnSig()
    {
        ulong savedBoundLifetimes = _boundLifetimes;
        DemangleOptionalBinder();

        if (ConsumeIf('U'))
            Print("unsafe ");

        if (ConsumeIf('K'))
        {
            Print("extern \"");
            if (ConsumeIf('C'))
            {
                Print("C");
            }
            else
            {
                Identifier ident = ParseIdentifier();
                if (ident.Punycode)
                    _error = true;
                // The mangling replaces '-' in an ABI string with '_'.
                foreach (char c in ident.Name)
                    Print(c == '_' ? '-' : c);
            }

            Print("\" ");
        }

        Print("fn(");
        for (int i = 0; !_error && !ConsumeIf('E'); ++i)
        {
            if (i > 0)
                Print(", ");
            DemangleType();
        }

        Print(")");

        // The unit return type is omitted.
        if (!ConsumeIf('u'))
        {
            Print(" -> ");
            DemangleType();
        }

        _boundLifetimes = savedBoundLifetimes;
    }

    // <dyn-bounds> = [<binder>] {<dyn-trait>} "E"
    private void DemangleDynBounds()
    {
        ulong savedBoundLifetimes = _boundLifetimes;
        Print("dyn ");
        DemangleOptionalBinder();
        for (int i = 0; !_error && !ConsumeIf('E'); ++i)
        {
            if (i > 0)
                Print(" + ");
            DemangleDynTrait();
        }

        _boundLifetimes = savedBoundLifetimes;
    }

    // <dyn-trait> = <path> {<dyn-trait-assoc-binding>}
    // <dyn-trait-assoc-binding> = "p" <undisambiguated-identifier> <type>
    private void DemangleDynTrait()
    {
        bool isOpen = DemanglePath(inType: true, leaveOpen: true);
        while (!_error && ConsumeIf('p'))
        {
            if (!isOpen)
            {
                isOpen = true;
                Print('<');
            }
            else
            {
                Print(", ");
            }

            Print(ParseIdentifier().Name);
            Print(" = ");
            DemangleType();
        }

        if (isOpen)
            Print(">");
    }

    // <binder> = "G" <base-62-number>
    private void DemangleOptionalBinder()
    {
        ulong binder = ParseOptionalBase62Number('G');
        if (_error || binder == 0)
            return;

        // Each bound lifetime is referenced later, which takes at least one byte of input: a binder too large for the
        // input is invalid (and would otherwise print excessive output).
        if (binder >= unchecked((ulong)_input.Length - _boundLifetimes))
        {
            _error = true;
            return;
        }

        Print("for<");
        for (ulong i = 0; i != binder; ++i)
        {
            _boundLifetimes += 1;
            if (i > 0)
                Print(", ");
            PrintLifetime(1);
        }

        Print("> ");
    }

    // <const> = <basic-type> <const-data> | "p" | <backref>
    private void DemangleConst()
    {
        if (_error || _recursionLevel >= MaxRecursionLevel)
        {
            _error = true;
            return;
        }

        _recursionLevel++;
        try
        {
            char c = Consume();
            if (ParseBasicType(c, out BasicType type))
            {
                switch (type)
                {
                    case BasicType.I8:
                    case BasicType.I16:
                    case BasicType.I32:
                    case BasicType.I64:
                    case BasicType.I128:
                    case BasicType.ISize:
                    case BasicType.U8:
                    case BasicType.U16:
                    case BasicType.U32:
                    case BasicType.U64:
                    case BasicType.U128:
                    case BasicType.USize:
                        DemangleConstInt();
                        break;
                    case BasicType.Bool:
                        DemangleConstBool();
                        break;
                    case BasicType.Char:
                        DemangleConstChar();
                        break;
                    case BasicType.Placeholder:
                        Print('_');
                        break;
                    default:
                        _error = true;
                        break;
                }
            }
            else if (c == 'B')
            {
                DemangleBackref(DemangleConst);
            }
            else
            {
                _error = true;
            }
        }
        finally
        {
            _recursionLevel--;
        }
    }

    // <const-data> = ["n"] <hex-number>
    private void DemangleConstInt()
    {
        if (ConsumeIf('n'))
            Print('-');

        ulong value = ParseHexNumber(out string hexDigits);
        if (hexDigits.Length <= 16)
        {
            PrintDecimalNumber(value);
        }
        else
        {
            Print("0x");
            Print(hexDigits);
        }
    }

    // <const-data> = "0_" | "1_"
    private void DemangleConstBool()
    {
        ParseHexNumber(out string hexDigits);
        if (hexDigits == "0")
            Print("false");
        else if (hexDigits == "1")
            Print("true");
        else
            _error = true;
    }

    // <const-data> = <hex-number>
    private void DemangleConstChar()
    {
        ulong codePoint = ParseHexNumber(out string hexDigits);
        if (_error || hexDigits.Length > 6)
        {
            _error = true;
            return;
        }

        Print("'");
        switch (codePoint)
        {
            case '\t':
                Print(@"\t");
                break;
            case '\r':
                Print(@"\r");
                break;
            case '\n':
                Print(@"\n");
                break;
            case '\\':
                Print(@"\\");
                break;
            case '"':
                Print("\"");
                break;
            case '\'':
                Print(@"\'");
                break;
            default:
                if (codePoint is >= 0x20 and <= 0x7e)
                {
                    Print((char)codePoint);
                }
                else
                {
                    Print(@"\u{");
                    Print(hexDigits);
                    Print('}');
                }

                break;
        }

        Print('\'');
    }

    // <undisambiguated-identifier> = ["u"] <decimal-number> ["_"] <bytes>
    private Identifier ParseIdentifier()
    {
        bool punycode = ConsumeIf('u');
        ulong bytes = ParseDecimalNumber();

        // The underscore separates a length from an identifier that starts with a digit or an underscore.
        ConsumeIf('_');

        if (_error || bytes > (ulong)(_input.Length - _position))
        {
            _error = true;
            return new Identifier("", false);
        }

        string name = _input.Substring(_position, (int)bytes);
        _position += (int)bytes;

        foreach (char c in name)
        {
            if (!IsValid(c))
            {
                _error = true;
                return new Identifier("", false);
            }
        }

        return new Identifier(name, punycode);
    }

    // Disambiguators and binders: 0 when the tag is absent, otherwise the number + 1.
    private ulong ParseOptionalBase62Number(char tag)
    {
        if (!ConsumeIf(tag))
            return 0;

        ulong n = ParseBase62Number();
        if (_error || !AddAssign(ref n, 1))
            return 0;
        return n;
    }

    // <base-62-number> = {<0-9a-zA-Z>} "_", offset by one: "_" is 0, "0_" is 1.
    private ulong ParseBase62Number()
    {
        if (ConsumeIf('_'))
            return 0;

        ulong value = 0;
        while (true)
        {
            ulong digit;
            char c = Consume();
            if (c == '_')
                break;
            if (IsDigit(c))
                digit = (ulong)(c - '0');
            else if (IsLower(c))
                digit = 10 + (ulong)(c - 'a');
            else if (IsUpper(c))
                digit = 10 + 26 + (ulong)(c - 'A');
            else
            {
                _error = true;
                return 0;
            }

            if (!MulAssign(ref value, 62) || !AddAssign(ref value, digit))
                return 0;
        }

        return AddAssign(ref value, 1) ? value : 0;
    }

    // <decimal-number> = "0" | <1-9> {<0-9>}
    private ulong ParseDecimalNumber()
    {
        char c = Look();
        if (!IsDigit(c))
        {
            _error = true;
            return 0;
        }

        if (c == '0')
        {
            Consume();
            return 0;
        }

        ulong value = 0;
        while (IsDigit(Look()))
        {
            if (!MulAssign(ref value, 10))
            {
                _error = true;
                return 0;
            }

            if (!AddAssign(ref value, (ulong)(Consume() - '0')))
                return 0;
        }

        return value;
    }

    // <hex-number> = "0_" | <1-9a-f> {<0-9a-f>} "_". The value is unspecified beyond 16 digits.
    private ulong ParseHexNumber(out string hexDigits)
    {
        int start = _position;
        ulong value = 0;

        if (!IsHexDigit(Look()))
            _error = true;

        if (ConsumeIf('0'))
        {
            if (!ConsumeIf('_'))
                _error = true;
        }
        else
        {
            while (!_error && !ConsumeIf('_'))
            {
                char c = Consume();
                value = unchecked(value * 16);
                if (IsDigit(c))
                    value = unchecked(value + (ulong)(c - '0'));
                else if (c is >= 'a' and <= 'f')
                    value = unchecked(value + 10 + (ulong)(c - 'a'));
                else
                    _error = true;
            }
        }

        if (_error)
        {
            hexDigits = "";
            return 0;
        }

        hexDigits = _input[start..(_position - 1)];
        return value;
    }

    private void Print(char c)
    {
        if (!_error && _print)
            _output.Append(c);
    }

    private void Print(string s)
    {
        if (!_error && _print)
            _output.Append(s);
    }

    private void PrintDecimalNumber(ulong n)
    {
        if (!_error && _print)
            _output.Append(CultureInfo.InvariantCulture, $"{n}");
    }

    // Index 0 is an erased lifetime; from 1 they are De Bruijn indices of lifetimes bound by enclosing binders.
    private void PrintLifetime(ulong index)
    {
        if (index == 0)
        {
            Print("'_");
            return;
        }

        if (index - 1 >= _boundLifetimes)
        {
            _error = true;
            return;
        }

        ulong depth = _boundLifetimes - index;
        Print('\'');
        if (depth < 26)
        {
            Print((char)('a' + (int)depth));
        }
        else
        {
            Print('z');
            PrintDecimalNumber(depth - 26 + 1);
        }
    }

    private void PrintIdentifier(Identifier ident)
    {
        if (_error || !_print)
            return;

        if (!ident.Punycode)
            Print(ident.Name);
        else if (!DecodePunycode(ident.Name, _output))
            _error = true;
    }

    private void DemangleBackref(Action demangle)
    {
        ulong backref = ParseBase62Number();
        if (_error || backref >= (ulong)_position)
        {
            _error = true;
            return;
        }

        if (!_print)
            return;

        int savedPosition = _position;
        _position = (int)backref;
        demangle();
        _position = savedPosition;
    }

    private char Look() => _error || _position >= _input.Length ? '\0' : _input[_position];

    private char Consume()
    {
        if (_error || _position >= _input.Length)
        {
            _error = true;
            return '\0';
        }

        return _input[_position++];
    }

    private bool ConsumeIf(char prefix)
    {
        if (_error || _position >= _input.Length || _input[_position] != prefix)
            return false;
        _position++;
        return true;
    }

    private bool AddAssign(ref ulong a, ulong b)
    {
        if (a > ulong.MaxValue - b)
        {
            _error = true;
            return false;
        }

        a += b;
        return true;
    }

    private bool MulAssign(ref ulong a, ulong b)
    {
        if (b != 0 && a > ulong.MaxValue / b)
        {
            _error = true;
            return false;
        }

        a *= b;
        return true;
    }

    private static bool IsDigit(char c) => c is >= '0' and <= '9';

    private static bool IsHexDigit(char c) => c is >= '0' and <= '9' or >= 'a' and <= 'f';

    private static bool IsLower(char c) => c is >= 'a' and <= 'z';

    private static bool IsUpper(char c) => c is >= 'A' and <= 'Z';

    private static bool IsValid(char c) => IsDigit(c) || IsLower(c) || IsUpper(c) || c == '_';

    // Decodes Rust's punycode (with '_' as the delimiter) and appends the UTF-8 bytes to output. While decoding, every
    // code point takes four chars, zero-padded; the padding is removed at the end.
    private static bool DecodePunycode(string input, StringBuilder output)
    {
        int outputSize = output.Length;
        int inputIdx = 0;

        int delimiterPos = input.LastIndexOf('_');
        if (delimiterPos >= 0)
        {
            // Basic code points before the last delimiter are copied.
            for (; inputIdx != delimiterPos; ++inputIdx)
            {
                char c = input[inputIdx];
                if (!IsValid(c))
                    return false;
                output.Append(c).Append('\0', 3);
            }

            ++inputIdx;
        }

        const ulong Base = 36;
        const ulong Skew = 38;
        const ulong TMin = 1;
        const ulong TMax = 26;
        ulong bias = 72;
        ulong n = 0x80;
        ulong damp = 700;

        ulong Adapt(ulong delta, ulong numPoints)
        {
            delta /= damp;
            delta += delta / numPoints;
            damp = 2;

            ulong k = 0;
            while (delta > (Base - TMin) * TMax / 2)
            {
                delta /= Base - TMin;
                k += Base;
            }

            return k + ((Base - TMin + 1) * delta / (delta + Skew));
        }

        for (ulong i = 0; inputIdx != input.Length; i += 1)
        {
            ulong oldI = i;
            ulong w = 1;
            const ulong Max = ulong.MaxValue;
            for (ulong k = Base; ; k += Base)
            {
                if (inputIdx == input.Length)
                    return false;
                char c = input[inputIdx++];
                ulong digit;
                if (IsLower(c))
                    digit = (ulong)(c - 'a');
                else if (IsDigit(c))
                    digit = 26 + (ulong)(c - '0');
                else
                    return false;

                if (digit > (Max - i) / w)
                    return false;
                i += digit * w;

                ulong t = k <= bias ? TMin : k >= bias + TMax ? TMax : k - bias;
                if (digit < t)
                    break;

                if (w > Max / (Base - t))
                    return false;
                w *= Base - t;
            }

            ulong numPoints = (ulong)(output.Length - outputSize) / 4 + 1;
            bias = Adapt(i - oldI, numPoints);

            if (i / numPoints > Max - n)
                return false;
            n += i / numPoints;
            i %= numPoints;

            char[] utf8 = new char[4];
            if (!EncodeUtf8(n, utf8))
                return false;
            output.Insert(outputSize + (int)(i * 4), utf8);
        }

        for (int i = output.Length - 1; i >= outputSize; i--)
        {
            if (output[i] == '\0')
                output.Remove(i, 1);
        }

        return true;
    }

    // UTF-8 bytes of a Unicode scalar value, one char per byte; false for surrogates and values beyond U+10FFFF.
    private static bool EncodeUtf8(ulong codePoint, char[] output)
    {
        if (codePoint is >= 0xD800 and <= 0xDFFF)
            return false;

        if (codePoint <= 0x7F)
        {
            output[0] = (char)codePoint;
            return true;
        }

        if (codePoint <= 0x7FF)
        {
            output[0] = (char)(0xC0 | ((codePoint >> 6) & 0x3F));
            output[1] = (char)(0x80 | (codePoint & 0x3F));
            return true;
        }

        if (codePoint <= 0xFFFF)
        {
            output[0] = (char)(0xE0 | (codePoint >> 12));
            output[1] = (char)(0x80 | ((codePoint >> 6) & 0x3F));
            output[2] = (char)(0x80 | (codePoint & 0x3F));
            return true;
        }

        if (codePoint <= 0x10FFFF)
        {
            output[0] = (char)(0xF0 | (codePoint >> 18));
            output[1] = (char)(0x80 | ((codePoint >> 12) & 0x3F));
            output[2] = (char)(0x80 | ((codePoint >> 6) & 0x3F));
            output[3] = (char)(0x80 | (codePoint & 0x3F));
            return true;
        }

        return false;
    }
}
