// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

using System.Text;

/// <summary>Token types produced by the WAT lexer.</summary>
public enum WatTokenKind
{
    LParen,
    RParen,
    Keyword,
    Identifier,
    Integer,
    Float,
    String,
    EOF,
}

/// <summary>A single token from WAT source text.</summary>
public readonly record struct WatToken(
    WatTokenKind Kind,
    string Value,
    int Line,
    int Column,
    byte[]? StringBytes = null)
{
    public override string ToString() => Kind == WatTokenKind.EOF ? "EOF" : $"{Kind}({Value})";
}

/// <summary>S-expression tokenizer for the WebAssembly text format.</summary>
public sealed class WatLexer
{
    private readonly string _source;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public WatLexer(string source)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
    }

    /// <summary>Create a lexer that reads WAT source from a <see cref="TextReader"/>.</summary>
    public WatLexer(TextReader reader)
        : this((reader ?? throw new ArgumentNullException(nameof(reader))).ReadToEnd())
    {
    }

    public List<WatToken> TokenizeAll()
    {
        var tokens = new List<WatToken>();
        while (true)
        {
            var tok = NextToken();
            tokens.Add(tok);
            if (tok.Kind == WatTokenKind.EOF)
                break;
        }
        return tokens;
    }

    public WatToken NextToken()
    {
        SkipWhitespaceAndComments();

        if (_pos >= _source.Length)
            return new WatToken(WatTokenKind.EOF, "", _line, _col);

        int startLine = _line, startCol = _col;
        char c = _source[_pos];

        if (c == '(')
        {
            Advance();
            return new WatToken(WatTokenKind.LParen, "(", startLine, startCol);
        }

        if (c == ')')
        {
            Advance();
            return new WatToken(WatTokenKind.RParen, ")", startLine, startCol);
        }

        if (c == '"')
            return ReadString(startLine, startCol);

        if (c == '$')
            return ReadIdentifier(startLine, startCol);

        // Numbers: optional sign followed by digit or 0x
        if (IsNumberStart(c))
            return ReadNumber(startLine, startCol);

        // Keywords and other bare words
        return ReadKeyword(startLine, startCol);
    }

    private void SkipWhitespaceAndComments()
    {
        while (_pos < _source.Length)
        {
            char c = _source[_pos];

            if (c == ' ' || c == '\t' || c == '\r')
            {
                Advance();
                continue;
            }

            if (c == '\n')
            {
                _pos++;
                _line++;
                _col = 1;
                continue;
            }

            // Line comment: ;; to end of line
            if (c == ';' && _pos + 1 < _source.Length && _source[_pos + 1] == ';')
            {
                while (_pos < _source.Length && _source[_pos] != '\n')
                    _pos++;
                continue;
            }

            // Block comment: (; ... ;)
            if (c == '(' && _pos + 1 < _source.Length && _source[_pos + 1] == ';')
            {
                _pos += 2;
                _col += 2;
                int depth = 1;
                while (_pos < _source.Length && depth > 0)
                {
                    if (_source[_pos] == '(' && _pos + 1 < _source.Length && _source[_pos + 1] == ';')
                    {
                        depth++;
                        Advance();
                        Advance();
                    }
                    else if (_source[_pos] == ';' && _pos + 1 < _source.Length && _source[_pos + 1] == ')')
                    {
                        depth--;
                        Advance();
                        Advance();
                    }
                    else if (_source[_pos] == '\n')
                    {
                        _pos++;
                        _line++;
                        _col = 1;
                    }
                    else
                    {
                        Advance();
                    }
                }
                continue;
            }

            break;
        }
    }

    private WatToken ReadString(int startLine, int startCol)
    {
        Advance(); // skip opening quote
        var sb = new System.Text.StringBuilder();
        var bytes = new List<byte>();
        Span<byte> encoded = stackalloc byte[4];

        while (_pos < _source.Length && _source[_pos] != '"')
        {
            if (_source[_pos] == '\\' && _pos + 1 < _source.Length)
            {
                Advance();
                char esc = _source[_pos];

                // Two-digit hex escape: \XX
                if (IsHexDigit(esc) && _pos + 1 < _source.Length && IsHexDigit(_source[_pos + 1]))
                {
                    char hi = esc;
                    Advance();
                    char lo = _source[_pos];
                    Advance();
                    int val = (HexVal(hi) << 4) | HexVal(lo);
                    sb.Append((char)val);
                    bytes.Add((byte)val);
                }
                else
                {
                    Advance();
                    char value = esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        _ => esc,
                    };
                    sb.Append(value);
                    bytes.Add((byte)value);
                }
            }
            else
            {
                var rune = System.Text.Rune.GetRuneAt(_source, _pos);
                sb.Append(rune);
                int length = rune.EncodeToUtf8(encoded);
                for (int i = 0; i < length; i++)
                    bytes.Add(encoded[i]);
                for (int i = 0; i < rune.Utf16SequenceLength; i++)
                    Advance();
            }
        }

        if (_pos < _source.Length)
            Advance(); // skip closing quote

        return new WatToken(WatTokenKind.String, sb.ToString(), startLine, startCol, bytes.ToArray());
    }

    private WatToken ReadIdentifier(int startLine, int startCol)
    {
        int start = _pos;
        Advance(); // skip $

        if (_pos < _source.Length && _source[_pos] == '"')
            return ReadQuotedIdentifier(startLine, startCol);

        while (_pos < _source.Length && IsIdChar(_source[_pos]))
            Advance();

        if (_pos == start + 1)
            throw new WatException("Lex error: empty identifier after '$'", startLine, startCol);

        return new WatToken(WatTokenKind.Identifier, _source[start.._pos], startLine, startCol);
    }

    private WatToken ReadQuotedIdentifier(int startLine, int startCol)
    {
        Advance(); // skip opening quote
        var bytes = new List<byte>();
        Span<byte> encoded = stackalloc byte[4];

        while (_pos < _source.Length)
        {
            char c = _source[_pos];
            if (c == '"')
            {
                Advance();
                if (bytes.Count == 0)
                    throw new WatException("Lex error: empty quoted identifier", startLine, startCol);

                try
                {
                    return new WatToken(
                        WatTokenKind.Identifier,
                        "$" + StrictUtf8.GetString([.. bytes]),
                        startLine,
                        startCol);
                }
                catch (DecoderFallbackException ex)
                {
                    throw new WatException(
                        $"Lex error: quoted identifier is not valid UTF-8: {ex.Message}",
                        startLine,
                        startCol);
                }
            }

            if (c is '\n' or '\r')
                throw new WatException("Lex error: newline in quoted identifier", _line, _col);
            if (c == '\t')
                throw new WatException("Lex error: tab in quoted identifier", _line, _col);
            if (c < ' ' || c == '\x7F')
                throw new WatException("Lex error: control character in quoted identifier", _line, _col);

            if (c == '\\')
            {
                ReadQuotedIdentifierEscape(bytes, startLine, startCol, encoded);
                continue;
            }

            if (!Rune.TryGetRuneAt(_source, _pos, out Rune rune))
                throw new WatException("Lex error: invalid Unicode in quoted identifier", _line, _col);

            int length = rune.EncodeToUtf8(encoded);
            for (int i = 0; i < length; i++)
                bytes.Add(encoded[i]);
            for (int i = 0; i < rune.Utf16SequenceLength; i++)
                Advance();
        }

        throw new WatException("Lex error: unterminated quoted identifier", startLine, startCol);
    }

    private void ReadQuotedIdentifierEscape(
        List<byte> bytes,
        int startLine,
        int startCol,
        Span<byte> encoded)
    {
        Advance(); // skip backslash
        if (_pos >= _source.Length)
            throw new WatException("Lex error: unterminated quoted identifier", startLine, startCol);

        char esc = _source[_pos];
        if (IsHexDigit(esc))
        {
            if (_pos + 1 >= _source.Length || !IsHexDigit(_source[_pos + 1]))
                throw new WatException($"Lex error: invalid escape '\\{esc}' in quoted identifier", _line, _col);
            char hi = esc;
            Advance();
            char lo = _source[_pos];
            Advance();
            bytes.Add((byte)((HexVal(hi) << 4) | HexVal(lo)));
            return;
        }

        if (esc == 'u')
        {
            int escapeLine = _line, escapeColumn = _col;
            Advance();
            if (_pos >= _source.Length || _source[_pos] != '{')
                throw new WatException("Lex error: invalid Unicode escape in quoted identifier", escapeLine, escapeColumn);
            Advance();

            int scalarValue = 0;
            int digits = 0;
            while (_pos < _source.Length && _source[_pos] != '}')
            {
                if (!IsHexDigit(_source[_pos]) || digits == 6)
                    throw new WatException("Lex error: invalid Unicode escape in quoted identifier", escapeLine, escapeColumn);
                scalarValue = checked((scalarValue << 4) | HexVal(_source[_pos]));
                digits++;
                Advance();
            }
            if (digits == 0 || _pos >= _source.Length)
                throw new WatException("Lex error: invalid Unicode escape in quoted identifier", escapeLine, escapeColumn);
            Advance(); // skip }

            if (!Rune.TryCreate(scalarValue, out Rune rune))
                throw new WatException("Lex error: invalid Unicode scalar value in quoted identifier", escapeLine, escapeColumn);
            int length = rune.EncodeToUtf8(encoded);
            for (int i = 0; i < length; i++)
                bytes.Add(encoded[i]);
            return;
        }

        byte value = esc switch
        {
            't' => 0x09,
            'n' => 0x0A,
            'r' => 0x0D,
            '"' => 0x22,
            '\'' => 0x27,
            '\\' => 0x5C,
            _ => throw new WatException(
                $"Lex error: invalid escape '\\{esc}' in quoted identifier",
                _line,
                _col),
        };
        Advance();
        bytes.Add(value);
    }

    private WatToken ReadNumber(int startLine, int startCol)
    {
        int start = _pos;

        if (_source[_pos] == '+' || _source[_pos] == '-')
            Advance();

        bool isHex = false;
        if (_pos < _source.Length && _source[_pos] == '0' && _pos + 1 < _source.Length &&
            (_source[_pos + 1] == 'x' || _source[_pos + 1] == 'X'))
        {
            isHex = true;
            Advance();
            Advance();
        }

        bool hasDecimalPoint = false;
        bool hasExponent = false;

        while (_pos < _source.Length)
        {
            char ch = _source[_pos];
            if (ch == '_')
            {
                Advance();
                continue;
            }
            if (ch == '.' && !hasDecimalPoint)
            {
                hasDecimalPoint = true;
                Advance();
                continue;
            }
            if (!isHex && (ch == 'e' || ch == 'E') && !hasExponent)
            {
                hasExponent = true;
                Advance();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                    Advance();
                continue;
            }
            if (isHex && (ch == 'p' || ch == 'P') && !hasExponent)
            {
                hasExponent = true;
                Advance();
                if (_pos < _source.Length && (_source[_pos] == '+' || _source[_pos] == '-'))
                    Advance();
                continue;
            }
            if (isHex ? IsHexDigit(ch) : char.IsAsciiDigit(ch))
            {
                Advance();
                continue;
            }
            break;
        }

        string text = _source[start.._pos];
        var kind = (hasDecimalPoint || hasExponent) ? WatTokenKind.Float : WatTokenKind.Integer;
        return new WatToken(kind, text, startLine, startCol);
    }

    private WatToken ReadKeyword(int startLine, int startCol)
    {
        int start = _pos;
        while (_pos < _source.Length && IsIdChar(_source[_pos]))
            Advance();

        if (_pos == start)
            throw new WatException(
                $"Lex error: unexpected character U+{(int)_source[_pos]:X4}",
                startLine,
                startCol);

        string text = _source[start.._pos];
        return new WatToken(WatTokenKind.Keyword, text, startLine, startCol);
    }

    private bool IsNumberStart(char c)
    {
        if (char.IsAsciiDigit(c)) return true;
        if ((c == '+' || c == '-') && _pos + 1 < _source.Length)
        {
            char next = _source[_pos + 1];
            return char.IsAsciiDigit(next) || next == '0';
        }
        return false;
    }

    private void Advance()
    {
        _pos++;
        _col++;
    }

    private static bool IsIdChar(char c) =>
        c != '(' && c != ')' && c != '"' && c != ';' && c > ' ';

    internal static bool IsBareIdentifierChar(char c) =>
        char.IsAsciiLetterOrDigit(c)
        || c is '!' or '#' or '$' or '%' or '&' or '\''
            or '*' or '+' or '-' or '.' or '/' or ':' or '<'
            or '=' or '>' or '?' or '@' or '\\' or '^' or '_'
            or '`' or '|' or '~';

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexVal(char c) =>
        c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0';

    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
}
