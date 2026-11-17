// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

namespace Microsoft.NET.WebAssembly.Wat;

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
public readonly record struct WatToken(WatTokenKind Kind, string Value, int Line, int Column)
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
                }
                else
                {
                    Advance();
                    sb.Append(esc switch
                    {
                        'n' => '\n',
                        't' => '\t',
                        'r' => '\r',
                        '"' => '"',
                        '\\' => '\\',
                        _ => esc,
                    });
                }
            }
            else
            {
                sb.Append(_source[_pos]);
                Advance();
            }
        }

        if (_pos < _source.Length)
            Advance(); // skip closing quote

        return new WatToken(WatTokenKind.String, sb.ToString(), startLine, startCol);
    }

    private WatToken ReadIdentifier(int startLine, int startCol)
    {
        int start = _pos;
        Advance(); // skip $
        while (_pos < _source.Length && IsIdChar(_source[_pos]))
            Advance();

        return new WatToken(WatTokenKind.Identifier, _source[start.._pos], startLine, startCol);
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

    private static bool IsHexDigit(char c) =>
        char.IsAsciiDigit(c) || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

    private static int HexVal(char c) =>
        c >= 'a' ? c - 'a' + 10 : c >= 'A' ? c - 'A' + 10 : c - '0';
}
