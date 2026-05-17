using System.Text;
using Betl.Core;

namespace Betl.Expressions.SsisExpr;

internal sealed class Lexer
{
    private readonly string _source;
    private int _i;

    public Lexer(string source) { _source = source; _i = 0; }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            SkipWhitespace();
            if (_i >= _source.Length) { tokens.Add(new Token(TokenKind.Eof, "", _i)); return tokens; }

            var start = _i;
            var c = _source[_i];

            switch (c)
            {
                case '(': _i++; tokens.Add(new(TokenKind.LParen, "(", start)); continue;
                case ')': _i++; tokens.Add(new(TokenKind.RParen, ")", start)); continue;
                case ',': _i++; tokens.Add(new(TokenKind.Comma, ",", start)); continue;
                case '+': _i++; tokens.Add(new(TokenKind.Plus, "+", start)); continue;
                case '-': _i++; tokens.Add(new(TokenKind.Minus, "-", start)); continue;
                case '*': _i++; tokens.Add(new(TokenKind.Star, "*", start)); continue;
                case '/': _i++; tokens.Add(new(TokenKind.Slash, "/", start)); continue;
                case '%': _i++; tokens.Add(new(TokenKind.Percent, "%", start)); continue;
                case '=':
                    if (Peek(1) == '=') { _i += 2; tokens.Add(new(TokenKind.EqEq, "==", start)); continue; }
                    throw Err(start, "Bare '=' is not a valid operator; use '=='.");
                case '!':
                    if (Peek(1) == '=') { _i += 2; tokens.Add(new(TokenKind.BangEq, "!=", start)); continue; }
                    _i++; tokens.Add(new(TokenKind.Bang, "!", start)); continue;
                case '<':
                    if (Peek(1) == '=') { _i += 2; tokens.Add(new(TokenKind.LtEq, "<=", start)); continue; }
                    _i++; tokens.Add(new(TokenKind.Lt, "<", start)); continue;
                case '>':
                    if (Peek(1) == '=') { _i += 2; tokens.Add(new(TokenKind.GtEq, ">=", start)); continue; }
                    _i++; tokens.Add(new(TokenKind.Gt, ">", start)); continue;
                case '?': _i++; tokens.Add(new(TokenKind.QMark, "?", start)); continue;
                case ':': _i++; tokens.Add(new(TokenKind.Colon, ":", start)); continue;
                case '&':
                    if (Peek(1) == '&') { _i += 2; tokens.Add(new(TokenKind.AmpAmp, "&&", start)); continue; }
                    throw Err(start, "Bitwise '&' not supported in Phase 1; use '&&' for logical AND.");
                case '|':
                    if (Peek(1) == '|') { _i += 2; tokens.Add(new(TokenKind.PipePipe, "||", start)); continue; }
                    throw Err(start, "Bitwise '|' not supported in Phase 1; use '||' for logical OR.");
                case '"': tokens.Add(LexString(start)); continue;
                case '[': tokens.Add(LexBracketed(start)); continue;
            }

            if (char.IsAsciiDigit(c)) { tokens.Add(LexNumber(start)); continue; }
            if (char.IsAsciiLetter(c) || c == '_') { tokens.Add(LexIdent(start)); continue; }

            throw Err(start, $"Unexpected character '{c}'.");
        }
    }

    private Token LexString(int start)
    {
        _i++; // opening "
        var sb = new StringBuilder();
        while (_i < _source.Length)
        {
            var c = _source[_i];
            if (c == '"')
            {
                // Embedded "" => single " (matches SSIS-EL)
                if (Peek(1) == '"') { sb.Append('"'); _i += 2; continue; }
                _i++; // closing "
                return new Token(TokenKind.StringLit, sb.ToString(), start);
            }
            if (c == '\\' && _i + 1 < _source.Length)
            {
                _i++;
                var esc = _source[_i++];
                sb.Append(esc switch
                {
                    'n' => '\n', 't' => '\t', 'r' => '\r',
                    '"' => '"',  '\\' => '\\',
                    _ => throw Err(_i - 1, $"Invalid string escape '\\{esc}'."),
                });
                continue;
            }
            sb.Append(c);
            _i++;
        }
        throw Err(start, "Unterminated string literal.");
    }

    private Token LexBracketed(int start)
    {
        _i++; // [
        var startIdent = _i;
        while (_i < _source.Length && _source[_i] != ']') _i++;
        if (_i >= _source.Length) throw Err(start, "Unterminated bracketed identifier.");
        var text = _source.Substring(startIdent, _i - startIdent);
        _i++; // ]
        return new Token(TokenKind.BracketedIdent, text, start);
    }

    private Token LexNumber(int start)
    {
        var isFloat = false;
        while (_i < _source.Length && char.IsAsciiDigit(_source[_i])) _i++;
        if (_i < _source.Length && _source[_i] == '.' && _i + 1 < _source.Length && char.IsAsciiDigit(_source[_i + 1]))
        {
            isFloat = true;
            _i++; // .
            while (_i < _source.Length && char.IsAsciiDigit(_source[_i])) _i++;
        }
        if (_i < _source.Length && (_source[_i] == 'e' || _source[_i] == 'E'))
        {
            isFloat = true;
            _i++;
            if (_i < _source.Length && (_source[_i] == '+' || _source[_i] == '-')) _i++;
            while (_i < _source.Length && char.IsAsciiDigit(_source[_i])) _i++;
        }
        var text = _source.Substring(start, _i - start);
        return new Token(isFloat ? TokenKind.FloatLit : TokenKind.IntLit, text, start);
    }

    private Token LexIdent(int start)
    {
        while (_i < _source.Length && (char.IsAsciiLetterOrDigit(_source[_i]) || _source[_i] == '_')) _i++;
        return new Token(TokenKind.Identifier, _source.Substring(start, _i - start), start);
    }

    private void SkipWhitespace()
    {
        while (_i < _source.Length && char.IsWhiteSpace(_source[_i])) _i++;
    }

    private char Peek(int offset)
    {
        var p = _i + offset;
        return p < _source.Length ? _source[p] : '\0';
    }

    private BetlException Err(int pos, string msg) =>
        new BetlException($"SSIS-EL lex error at column {pos + 1}: {msg}");
}
