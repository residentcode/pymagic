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

    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> KeywordLookup = Keywords.GetAlternateLookup<ReadOnlySpan<char>>();

    // Longest first so greedy matching works.
    static readonly string[] Operators =
    {
        "**=", "//=", ">>=", "<<=", "...",
        "->", ":=", "==", "!=", "<=", ">=", "**", "//", "<<", ">>", "+=", "-=", "*=", "/=", "%=",
        "&=", "|=", "^=", "@=", "<>",
        "+", "-", "*", "/", "%", "@", "&", "|", "^", "~", "<", ">", "(", ")", "[", "]", "{", "}",
        ",", ":", ".", ";", "=", "!",
    };

    /// <summary>The operators that start with each ASCII character, longest first (the list above filtered by first character).</summary>
    static readonly string[][] OperatorsByFirstChar = Enumerable.Range(0, 128)
        .Select(ch => Operators.Where(o => o[0] == ch).ToArray()).ToArray();

    readonly string _s;
    readonly List<Token> _tokens;
    readonly Stack<int> _indents = new();
    int _pos;
    int _brackets;
    bool _atLineStart = true;

    Lexer(string source)
    {
        _s = source;
        _tokens = new List<Token>(source.Length / 4 + 8);   // about one token for four characters of Python
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
            Add(KeywordLookup.Contains(_s.AsSpan(s, _pos - s)) ? TokenKind.Keyword : TokenKind.Name, s, _pos);
            return;
        }

        foreach (var op in c < OperatorsByFirstChar.Length ? OperatorsByFirstChar[c] : Array.Empty<string>())
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
        bool ok = ScanString(quotePos, PrefixIsF(quotePos), out int end);
        Add(ok ? TokenKind.String : TokenKind.Error, start, end);
        _pos = end;
    }

    /// <summary>True when the 1-2 letters directly before the quote form an f-/t-string prefix.</summary>
    bool PrefixIsF(int quotePos)
    {
        int i = quotePos, n = 0;
        while (i > 0 && n < 2 && char.IsAsciiLetter(_s[i - 1])) { i--; n++; }
        if (n == 0 || (i > 0 && IsIdentPart(_s[i - 1]))) return false;
        var prefix = _s.AsSpan(i, n);
        return IsStringPrefix(prefix) && (prefix.IndexOfAny("fFtT") >= 0);
    }

    /// <summary>
    /// Scans a string literal starting at its opening quote. f-/t-strings track replacement fields so that
    /// nested strings (PEP 701: same quote kind inside {...}) and format specs are handled.
    /// Returns false (with end = where scanning stopped) for an unterminated literal.
    /// </summary>
    bool ScanString(int quotePos, bool fstr, out int end)
    {
        char q = _s[quotePos];
        bool triple = quotePos + 2 < _s.Length && _s[quotePos + 1] == q && _s[quotePos + 2] == q;
        int p = quotePos + (triple ? 3 : 1);
        // One entry per open replacement field: bracket depth inside the expression, and whether we are in its format spec.
        var fields = fstr ? new List<(int Paren, bool Spec)>() : null;

        while (p < _s.Length)
        {
            char c = _s[p];
            bool newline = c == '\n' || c == '\r';

            if (fields is { Count: > 0 })
            {
                var (paren, spec) = fields[^1];
                if (!spec)
                {
                    if (c == '"' || c == '\'')
                    {
                        if (!ScanString(p, PrefixIsF(p), out int e)) { end = e; return false; }
                        p = e;
                        continue;
                    }
                    switch (c)
                    {
                        case '(' or '[' or '{': fields[^1] = (paren + 1, false); break;
                        case ')' or ']': if (paren > 0) fields[^1] = (paren - 1, false); break;
                        case '}':
                            if (paren > 0) fields[^1] = (paren - 1, false);
                            else fields.RemoveAt(fields.Count - 1);
                            break;
                        case ':': if (paren == 0) fields[^1] = (0, true); break;
                    }
                    p++;
                    continue;
                }

                // format spec: literal text, nested fields allowed
                if (c == '\\') { p += p + 1 < _s.Length ? 2 : 1; continue; }
                if (c == '{') { fields.Add((0, false)); p++; continue; }
                if (c == '}') { fields.RemoveAt(fields.Count - 1); p++; continue; }
                if (newline && !triple) { end = p; return false; }
                if (c == q && !triple) { p++; end = p; return true; }
                if (c == q && p + 2 < _s.Length && _s[p + 1] == q && _s[p + 2] == q) { end = p + 3; return true; }
                p++;
                continue;
            }

            if (c == '\\')
            {
                // Also in raw strings a backslash keeps the next char (e.g. a quote) from ending the literal.
                p += p + 1 < _s.Length ? 2 : 1;
                continue;
            }
            if (newline && !triple) { end = p; return false; }
            if (fields != null && c == '{')
            {
                if (p + 1 < _s.Length && _s[p + 1] == '{') p += 2;
                else { fields.Add((0, false)); p++; }
                continue;
            }
            if (c == q)
            {
                if (!triple) { end = p + 1; return true; }
                if (p + 2 < _s.Length && _s[p + 1] == q && _s[p + 2] == q) { end = p + 3; return true; }
            }
            p++;
        }
        end = _s.Length;
        return false;
    }
}