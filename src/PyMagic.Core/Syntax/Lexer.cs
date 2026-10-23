using System.Globalization;

namespace PyMagic.Core.Syntax;

/// <summary>
/// Python tokenizer. Emits Indent/Dedent/Newline tokens like CPython's tokenizer, keeps comments
/// as tokens (so tools can see them) and never throws: bad input becomes Error tokens.
/// Soft keywords (match, case, type, _) are lexed as Name; the parser decides.
/// </summary>
public sealed class Lexer
{
    public static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class",
        "continue", "def", "del", "elif", "else", "except", "finally", "for", "from", "global",
        "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise",
        "return", "try", "while", "with", "yield",
    };

    // Longest first so greedy matching works.
    static readonly string[] Operators =
    {
        "**=", "//=", ">>=", "<<=", "...",
        "->", ":=", "==", "!=", "<=", ">=", "**", "//", "<<", ">>", "+=", "-=", "*=", "/=", "%=",
        "&=", "|=", "^=", "@=", "<>",
        "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">", "(", ")", "[", "]", "{", "}",
        ",", ":", ".", ";", "=", "!",
    };

    readonly string _s;
    readonly List<Token> _tokens = new();
    readonly Stack<int> _indents = new();
    int _pos;
    int _brackets;
    bool _atLineStart = true;

    Lexer(string source)
    {
        _s = source;
        _indents.Push(0);
    }

    public static List<Token> Tokenize(string source)
    {
        var lx = new Lexer(source);
        lx.Run();
        return lx._tokens;
    }

    void Add(TokenKind k, int start, int end) => _tokens.Add(new Token(k, start, end - start));

    void Run()
    {
        while (_pos < _s.Length)
        {
            if (_atLineStart && _brackets == 0)
            {
                if (!HandleLineStart()) continue;
            }
            LexOne();
        }

        // Close last logical line, then all open blocks.
        if (_tokens.Count > 0 && _tokens[^1].Kind is not (TokenKind.Newline or TokenKind.Dedent or TokenKind.Indent)
            && LastSignificant() is not null)
            Add(TokenKind.Newline, _s.Length, _s.Length);
        while (_indents.Count > 1) { _indents.Pop(); Add(TokenKind.Dedent, _s.Length, _s.Length); }
        Add(TokenKind.EndOfFile, _s.Length, _s.Length);
    }

    Token? LastSignificant()
    {
        for (int i = _tokens.Count - 1; i >= 0; i--)
            if (_tokens[i].Kind != TokenKind.Comment) return _tokens[i];
        return null;
    }

    /// <summary>Measures indentation. Returns false when the line was blank/comment-only and was consumed.</summary>
    bool HandleLineStart()
    {
        int col = 0, p = _pos;
        while (p < _s.Length)
        {
            char c = _s[p];
            if (c == ' ') col++;
            else if (c == '\t') col = (col / 8 + 1) * 8;
            else if (c == '\f') col = 0;
            else break;
            p++;
        }

        if (p >= _s.Length) { _pos = p; return false; }

        char ch = _s[p];
        if (ch == '#')
        {
            int e = LineEnd(p);
            Add(TokenKind.Comment, p, e);
            _pos = SkipNewline(e);
            return false;
        }
        if (ch == '\r' || ch == '\n') { _pos = SkipNewline(p); return false; }
        if (ch == '\\' && p + 1 < _s.Length && (_s[p + 1] == '\n' || _s[p + 1] == '\r'))
        {
            // continuation line at start: treat as part of previous construct, no indent change
            _pos = p;
            _atLineStart = false;
            return true;
        }

        _atLineStart = false;
        _pos = p;
        int cur = _indents.Peek();
        if (col > cur)
        {
            _indents.Push(col);
            Add(TokenKind.Indent, p, p);
        }
        else if (col < cur)
        {
            while (col < _indents.Peek()) { _indents.Pop(); Add(TokenKind.Dedent, p, p); }
            if (col != _indents.Peek())
                Add(TokenKind.Error, p, p); // inconsistent dedent
        }
        return true;
    }

    int LineEnd(int p)
    {
        while (p < _s.Length && _s[p] != '\n' && _s[p] != '\r') p++;
        return p;
    }

    int SkipNewline(int p)
    {
        if (p < _s.Length && _s[p] == '\r') p++;
        if (p < _s.Length && _s[p] == '\n') p++;
        return p;
    }

    void LexOne()
    {
        char c = _s[_pos];

        if (c == ' ' || c == '\t' || c == '\f') { _pos++; return; }

        if (c == '\r' || c == '\n')
        {
            int s = _pos;
            _pos = SkipNewline(_pos);
            if (_brackets == 0)
            {
                Add(TokenKind.Newline, s, _pos);
                _atLineStart = true;
            }
            return;
        }

        if (c == '\\' && _pos + 1 < _s.Length && (_s[_pos + 1] == '\n' || _s[_pos + 1] == '\r'))
        {
            _pos = SkipNewline(_pos + 1);
            return;
        }

        if (c == '#')
        {
            int e = LineEnd(_pos);
            Add(TokenKind.Comment, _pos, e);
            _pos = e;
            return;
        }

        if (char.IsAsciiDigit(c) || (c == '.' && _pos + 1 < _s.Length && char.IsAsciiDigit(_s[_pos + 1])))
        {
            LexNumber();
            return;
        }

        if (c == '"' || c == '\'') { LexString(_pos, _pos); return; }

        if (IsIdentStart(c))
        {
            int s = _pos;
            _pos++;
            while (_pos < _s.Length && IsIdentPart(_s[_pos])) _pos++;
            if (_pos < _s.Length && (_s[_pos] == '"' || _s[_pos] == '\'') && IsStringPrefix(_s.AsSpan(s, _pos - s)))
            {
                LexString(s, _pos);
                return;
            }
            string word = _s.Substring(s, _pos - s);
            Add(Keywords.Contains(word) ? TokenKind.Keyword : TokenKind.Name, s, _pos);
            return;
        }

        foreach (var op in Operators)
        {
            if (string.CompareOrdinal(_s, _pos, op, 0, op.Length) == 0)
            {
                if (op is "(" or "[" or "{") _brackets++;
                else if (op is ")" or "]" or "}") { if (_brackets > 0) _brackets--; }
                Add(TokenKind.Operator, _pos, _pos + op.Length);
                _pos += op.Length;
                return;
            }
        }

        // Unknown character (including a stray '$' or '?')
        int len = char.IsHighSurrogate(c) && _pos + 1 < _s.Length ? 2 : 1;
        Add(TokenKind.Error, _pos, _pos + len);
        _pos += len;
    }

    static bool IsIdentStart(char c) => c == '_' || char.IsLetter(c) || (c > 127 && char.GetUnicodeCategory(c) == UnicodeCategory.LetterNumber);

    static bool IsIdentPart(char c)
    {
        if (c == '_' || char.IsLetterOrDigit(c)) return true;
        if (c < 128) return false;
        var cat = char.GetUnicodeCategory(c);
        return cat is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.ConnectorPunctuation or UnicodeCategory.LetterNumber;
    }

    static bool IsStringPrefix(ReadOnlySpan<char> p)
    {
        if (p.Length is 0 or > 2) return false;
        Span<char> lower = stackalloc char[2];
        for (int i = 0; i < p.Length; i++) lower[i] = char.ToLowerInvariant(p[i]);
        var l = lower[..p.Length];
        return l switch
        {
            "r" or "u" or "b" or "f" or "t" => true,
            "rb" or "br" or "rf" or "fr" or "rt" or "tr" => true,
            _ => false,
        };
    }

    void LexNumber()
    {
        int s = _pos;
        if (_s[_pos] == '0' && _pos + 1 < _s.Length && "xXoObB".Contains(_s[_pos + 1]))
        {
            char k = char.ToLowerInvariant(_s[_pos + 1]);
            _pos += 2;
            while (_pos < _s.Length && (_s[_pos] == '_' || IsDigitOfBase(_s[_pos], k))) _pos++;
            Add(TokenKind.Number, s, _pos);
            return;
        }

        SkipDigits();
        if (_pos < _s.Length && _s[_pos] == '.')
        {
            _pos++;
            SkipDigits();
        }
        if (_pos < _s.Length && (_s[_pos] == 'e' || _s[_pos] == 'E'))
        {
            int save = _pos;
            _pos++;
            if (_pos < _s.Length && (_s[_pos] == '+' || _s[_pos] == '-')) _pos++;
            if (_pos < _s.Length && char.IsAsciiDigit(_s[_pos])) SkipDigits();
            else _pos = save; // not an exponent
        }
        if (_pos < _s.Length && (_s[_pos] == 'j' || _s[_pos] == 'J')) _pos++;
        Add(TokenKind.Number, s, _pos);
    }

    void SkipDigits()
    {
        while (_pos < _s.Length && (char.IsAsciiDigit(_s[_pos]) || _s[_pos] == '_')) _pos++;
    }

    static bool IsDigitOfBase(char c, char b) => b switch
    {
        'x' => char.IsAsciiHexDigit(c),
        'o' => c >= '0' && c <= '7',
        _ => c == '0' || c == '1',
    };

    /// <param name="start">token start (prefix start)</param>
    /// <param name="quotePos">position of opening quote</param>
    void LexString(int start, int quotePos)
    {
        char q = _s[quotePos];
        bool triple = quotePos + 2 < _s.Length && _s[quotePos + 1] == q && _s[quotePos + 2] == q;
        int p = quotePos + (triple ? 3 : 1);

        while (p < _s.Length)
        {
            char c = _s[p];
            if (c == '\\')
            {
                // Also in raw strings a backslash keeps the next char (e.g. a quote) from ending the literal.
                p += p + 1 < _s.Length ? 2 : 1;
                continue;
            }
            if (!triple && (c == '\n' || c == '\r'))
            {
                Add(TokenKind.Error, start, p); // unterminated
                _pos = p;
                return;
            }
            if (c == q)
            {
                if (!triple) { p++; Add(TokenKind.String, start, p); _pos = p; return; }
                if (p + 2 < _s.Length &&_s[p + 1] == q && _s[p + 2] == q)
                {
                    p += 3; Add(TokenKind.String, start, p); _pos = p; return;
                }
            }
            p++;
        }
        Add(TokenKind.Error, start, _s.Length); // unterminated at EOF
        _pos = _s.Length;
    }
}
