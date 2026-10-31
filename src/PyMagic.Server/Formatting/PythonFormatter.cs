using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;

namespace PyMagic.Server.Formatting;

/// <summary>
/// A formatter in the style of black that works on the token stream: it normalises spaces around operators, commas and
/// brackets, indentation (four spaces), blank lines between definitions, comment spacing, quotes and the end of the file.
/// It never moves a token to another line, so it does not wrap or join lines (a trailing comma does not explode a
/// call). After formatting, the token streams of the old and the new text are compared; if they differ in anything but
/// spacing and quote style, nothing is returned.
/// </summary>
public static class PythonFormatter
{
    /// <summary>The formatted text, or null when the text has syntax errors, or the check failed (<c># fmt: off</c> ... <c># fmt: on</c> regions and <c># fmt: skip</c> lines stay as they are).</summary>
    public static string? Format(string source, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        LastMismatch = null;
        if (source.Length == 0) return source;
        var tokens = Lexer.Tokenize(source);
        if (tokens.Any(t => t.Kind == TokenKind.Error)) return null;
        var parsed = Parser.Parse(source);
        if (parsed.Diagnostics.Count > 0) return null;
        // like black, a file that already needs Python 3.9 may have its context managers in parentheses
        if (!options.ParenthesizedWith && NeedsPython39(source, parsed.Module)) options = options with { ParenthesizedWith = true };

        string result;
        try { result = new Renderer(source, tokens, options, parsed.Module).Run(); }
        catch (ArgumentException) { return null; }
        catch (InvalidOperationException) { return null; }

        return SameCode(source, result) ? Finish(result, options) : null;
    }

    /// <summary>
    /// Syntax only Python 3.9 and later reads, which black takes to mean the code is for those (its target versions inferred from the
    /// file): with-items in parentheses, a decorator that is any expression, match, except*, type parameters and the type statement.
    /// </summary>
    static bool NeedsPython39(string source, Module module)
    {
        foreach (var node in AstChildren.Descendants(module))
        {
            switch (node)
            {
                case MatchStmt or TypeAliasStmt or TryStmt { IsStar: true }:
                case ClassDef { TypeParams.Count: > 0 } or FunctionDef { TypeParams.Count: > 0 }:
                    return true;
                case WithStmt w when w.Items.Count > 0 && Regex.IsMatch(source[w.Start..w.Items[0].Start], @"\(\s*$") && (w.Items.Count > 1 || w.Items[0].Target != null)
                                     && source.IndexOf(':', w.Items[^1].End) is var colon && colon > 0 && Regex.IsMatch(source[w.Items[^1].End..colon], @"^\s*,?\s*\)\s*$"):
                    return true;
            }
            var decorators = node switch { FunctionDef f => f.Decorators, ClassDef c => c.Decorators, _ => null };
            if (decorators != null && decorators.Any(d => !DottedOrCall(d))) return true;
        }
        return false;

        static bool DottedOrCall(Expr e) => e switch
        {
            NameExpr => true,
            AttributeExpr a => DottedOrCall(a.Value) && a.Value is not CallExpr,
            CallExpr c => c.Func is NameExpr or AttributeExpr && DottedOrCall(c.Func),
            _ => false,
        };
    }

    /// <summary>The rest of what the options ask for: tabs for indentation, the line ending.</summary>
    static string Finish(string result, FormatOptions o)
    {
        if (o.UseTabs) result = IndentWithTabs(result, o.IndentWidth);
        return o.Endings switch
        {
            LineEnding.Lf => result.Replace("\r\n", "\n"),
            LineEnding.Crlf => result.Replace("\r\n", "\n").Replace("\n", "\r\n"),
            _ => result,
        };
    }

    /// <summary>Leading spaces become one tab per indentation level, except on lines that start inside a string.</summary>
    static string IndentWithTabs(string text, int width)
    {
        var insideString = new HashSet<int>();
        foreach (var t in Lexer.Tokenize(text))
            if (t.Kind == TokenKind.String)
                for (int i = t.Start; i < t.End; i++)
                    if (text[i] == '\n' && i + 1 < t.End) insideString.Add(i + 1);
        var sb = new StringBuilder(text.Length);
        int pos = 0;
        while (pos < text.Length)
        {
            int end = text.IndexOf('\n', pos);
            end = end < 0 ? text.Length : end + 1;
            int spaces = 0;
            if (!insideString.Contains(pos))
                while (pos + spaces < end && text[pos + spaces] == ' ') spaces++;
            sb.Append('\t', spaces / width).Append(' ', spaces % width).Append(text, pos + spaces, end - pos - spaces);
            pos = end;
        }
        return sb.ToString();
    }

    /// <summary>
    /// Formats the logical lines that touch the selection and leaves the rest as it is. Formatting only some lines can
    /// change the meaning (a re-indented header over an old-indented body), so the result is checked and, when it
    /// does not hold, the selection grows to the top-level statements it touches.
    /// </summary>
    public static string? FormatRange(string source, int start, int end, FormatOptions? options = null)
    {
        options ??= FormatOptions.Default;
        // tabs and a fixed line ending are decided for the whole file
        if (options.UseTabs || options.Endings != LineEnding.Auto) return Format(source, options);
        LastMismatch = null;
        if (source.Length == 0) return source;
        var tokens = Lexer.Tokenize(source);
        if (tokens.Any(t => t.Kind == TokenKind.Error)) return null;
        var parsed = Parser.Parse(source);
        if (parsed.Diagnostics.Count > 0) return null;
        if (!options.ParenthesizedWith && NeedsPython39(source, parsed.Module)) options = options with { ParenthesizedWith = true };

        string? Attempt(int a, int b)
        {
            string result;
            try { result = new Renderer(source, tokens, options, parsed.Module).RunRange(a, b); }
            catch (ArgumentException) { return null; }
            catch (InvalidOperationException) { return null; }
            return SameCode(source, result) ? result : null;
        }

        var first = Attempt(start, end);
        if (first != null) return first;

        int wideStart = start, wideEnd = end;
        foreach (var stmt in parsed.Module.Body)
        {
            if (stmt.End <= start || stmt.Start > end) continue;
            int stmtEnd = stmt.End;
            while (stmtEnd > stmt.Start && (source[stmtEnd - 1] == '\n' || source[stmtEnd - 1] == '\r')) stmtEnd--;   // a compound statement's End includes its last line break
            wideStart = Math.Min(wideStart, stmt.Start);
            wideEnd = Math.Max(wideEnd, stmtEnd);
        }
        return wideStart == start && wideEnd == end ? null : Attempt(wideStart, wideEnd);
    }

    // ------------------------------------------------------------ the safety check

    /// <summary>The comments are the same, in the same order, and the syntax trees are equal (positions, quote style and redundant parentheses aside).</summary>
    static bool SameCode(string before, string after)
    {
        if (!Comments(before).SequenceEqual(Comments(after))) { LastMismatch ??= "comments"; return false; }
        var a = Parser.Parse(before);
        var b = Parser.Parse(after);
        if (b.Diagnostics.Count > 0) { LastMismatch ??= "syntax: " + b.Diagnostics[0]; return false; }
        return AstEqual(a.Module, b.Module);
    }

    static List<string> Comments(string text) =>
        Lexer.Tokenize(text).Where(t => t.Kind == TokenKind.Comment)
            .SelectMany(t => System.Text.RegularExpressions.Regex.Split(t.Text(text), @"\s{2,}#").Select(c => c.TrimStart('#').Trim())).ToList();

    [ThreadStatic] public static string? LastMismatch;
    static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, System.Reflection.PropertyInfo[]> Properties = new();

    static bool AstEqual(object? x, object? y)
    {
        if (ReferenceEquals(x, y)) return true;
        if (x == null || y == null) return false;
        if (x is string sx) { if (y is string sy && sx == sy) return true; LastMismatch ??= "string " + sx; return false; }
        if (x is ConstExpr cx)
        {
            if (y is not ConstExpr cy || cx.Kind != cy.Kind || cx.IsFString != cy.IsFString) return false;
            return cx.Kind is ConstKind.Str or ConstKind.Bytes ? SameStrings(cx.Text, cy.Text) : cx.Kind is ConstKind.Int or ConstKind.Float or ConstKind.Imaginary ? NormaliseNumber(cx.Text) == NormaliseNumber(cy.Text) : cx.Text == cy.Text;
        }
        if (x is ExprStmt sx2 && y is ExprStmt sy2 && sx2.Value is ConstExpr dx && sy2.Value is ConstExpr dy && dx.Kind == ConstKind.Str && dy.Kind == ConstKind.Str && !dx.IsFString && !dy.IsFString)
            return SameDocstrings(dx.Text, dy.Text);
        if (x is DelStmt delX && y is DelStmt delY)
            return AstEqual(FlatTargets(delX.Targets), FlatTargets(delY.Targets));
        if (x is Node)
        {
            if (x.GetType() != y.GetType()) { LastMismatch ??= x.GetType().Name + " vs " + y.GetType().Name; return false; }
            foreach (var prop in Properties.GetOrAdd(x.GetType(), t => t.GetProperties().Where(p => p.GetIndexParameters().Length == 0 && p.Name != "Parenthesized" && !p.Name.EndsWith("Start") && !p.Name.EndsWith("End")).ToArray()))
                if (!AstEqual(prop.GetValue(x), prop.GetValue(y))) { LastMismatch ??= x.GetType().Name + "." + prop.Name; return false; }
            return true;
        }
        if (x is System.Collections.IEnumerable ex && y is System.Collections.IEnumerable ey)
        {
            var ia = ex.Cast<object?>().ToList();
            var ib = ey.Cast<object?>().ToList();
            if (ia.Count != ib.Count) { LastMismatch ??= "list length " + ia.Count + " vs " + ib.Count; return false; }
            for (int i = 0; i < ia.Count; i++) if (!AstEqual(ia[i], ib[i])) return false;
            return true;
        }
        return x.Equals(y);
    }

    static List<object> FlatTargets(IReadOnlyList<Expr> targets) =>
        targets.SelectMany(t => t is TupleExpr tuple ? tuple.Items.Cast<object>() : new object[] { t }).ToList();

    /// <summary>A string statement (a docstring): the same text once indentation and trailing spaces are cleaned away.</summary>
    static bool SameDocstrings(string a, string b)
    {
        var ta = Lexer.Tokenize(a).Where(t => t.Kind == TokenKind.String).Select(t => t.Text(a)).ToList();
        var tb = Lexer.Tokenize(b).Where(t => t.Kind == TokenKind.String).Select(t => t.Text(b)).ToList();
        if (ta.Count != tb.Count) return false;
        for (int i = 0; i < ta.Count; i++)
        {
            var (pa, ba) = Parts(ta[i]);
            var (pb, bb) = Parts(tb[i]);
            if (pa != pb || Docstrings.Clean(Canonical(pa, ba)) != Docstrings.Clean(Canonical(pb, bb))) return false;
        }
        return true;
    }

    static (string Prefix, string Body) Parts(string s)
    {
        int q = 0;
        while (q < s.Length && s[q] != '"' && s[q] != '\'') q++;
        string rest = s[q..];
        int n = rest.Length >= 6 && (rest.StartsWith("\"\"\"") || rest.StartsWith("'''")) ? 3 : 1;
        return (CanonPrefix(s[..q]), rest.Length >= 2 * n ? rest[n..^n] : rest);
    }

    /// <summary>Adjacent string literals: the same literals, whatever lies between them and whichever quotes they use.</summary>
    static bool SameStrings(string a, string b)
    {
        var ta = Lexer.Tokenize(a).Where(t => t.Kind == TokenKind.String).Select(t => StringCore(t.Text(a))).ToList();
        var tb = Lexer.Tokenize(b).Where(t => t.Kind == TokenKind.String).Select(t => StringCore(t.Text(b))).ToList();
        return ta.SequenceEqual(tb);
    }

    /// <summary>A string literal without its quote style: prefix (lower case) and the text between the quotes.</summary>
    static string StringCore(string s)
    {
        int q = 0;
        while (q < s.Length && s[q] != '"' && s[q] != '\'') q++;
        string prefix = CanonPrefix(s[..q]);
        string rest = s[q..];
        int n = rest.Length >= 6 && (rest.StartsWith("\"\"\"") || rest.StartsWith("'''")) ? 3 : 1;
        if (rest.Length < 2 * n) return s;
        return prefix + "|" + Canonical(prefix, rest[n..^n]);
    }

    static string CanonPrefix(string prefix) => new string(prefix.ToLowerInvariant().Where(c => c != 'u').OrderBy(c => c).ToArray());

    static readonly Regex EscapeSequence = new(@"(?<bs>\\+)(?:u(?<u>[a-fA-F0-9]{4})|U(?<U>[a-fA-F0-9]{8})|x(?<x>[a-fA-F0-9]{2})|N\{(?<N>[^}]+)\})", RegexOptions.Compiled);

    /// <summary>
    /// Black's spelling of the prefix (raw first, <c>f</c> and <c>b</c> in lower case, no <c>u</c>) and of the escapes (hex digits
    /// of <c>\x</c>, <c>\u</c> and <c>\U</c> in lower case, names of <c>\N{...}</c> in upper case); raw strings keep their text.
    /// </summary>
    internal static string NormalisePrefixAndEscapes(string s)
    {
        int q = 0;
        while (q < s.Length && s[q] != '"' && s[q] != '\'') q++;
        if (q >= s.Length) return s;
        string prefix = s[..q];
        string raw = new(prefix.Where(c => c is 'r' or 'R').ToArray());
        string rest = new(prefix.Where(c => c is not ('r' or 'R' or 'u' or 'U')).Select(char.ToLowerInvariant).ToArray());
        string body = s[q..];
        if (raw.Length == 0) body = NormaliseEscapes(prefix, body);
        return raw + rest + body;
    }

    /// <summary>The escapes of a non-raw string in black's spelling; in bytes only <c>\x</c> is an escape.</summary>
    static string NormaliseEscapes(string prefix, string text)
    {
        if (!text.Contains('\\') || prefix.Contains('r', StringComparison.OrdinalIgnoreCase)) return text;
        bool bytes = prefix.Contains('b', StringComparison.OrdinalIgnoreCase);
        return EscapeSequence.Replace(text, m =>
        {
            string slashes = m.Groups["bs"].Value;
            if (slashes.Length % 2 == 0) return m.Value;
            if (m.Groups["x"].Success) return slashes + "x" + m.Groups["x"].Value.ToLowerInvariant();
            if (bytes) return m.Value;
            if (m.Groups["u"].Success) return slashes + "u" + m.Groups["u"].Value.ToLowerInvariant();
            if (m.Groups["U"].Success) return slashes + "U" + m.Groups["U"].Value.ToLowerInvariant();
            return slashes + "N{" + m.Groups["N"].Value.ToUpperInvariant() + "}";
        });
    }

    /// <summary>The body of a string without the backslashes that are only there for a quote: they do not change the string.</summary>
    static string Canonical(string prefix, string body)
    {
        if (prefix.Contains('r', StringComparison.OrdinalIgnoreCase)) return body;
        var sb = new StringBuilder();
        for (int i = 0; i < body.Length; i++)
        {
            if (body[i] == '\\' && i + 1 < body.Length)
            {
                if (body[i + 1] is '"' or '\'') sb.Append(body[i + 1]); else sb.Append(body[i]).Append(body[i + 1]);
                i++;
            }
            else sb.Append(body[i]);
        }
        return NormaliseEscapes(prefix, sb.ToString());
    }

    // ------------------------------------------------------------ quotes

    /// <summary>
    /// Double quotes (or single ones, or as written) unless that needs more escapes: only literals without backslashes
    /// and without the other quote are touched. Triple-quoted strings always use double quotes unless the style preserves them.
    /// </summary>
    /// <summary>Black's number style: prefixes and exponents in lower case, hexadecimal digits in upper case, 1. is 1.0 and .5 is 0.5.</summary>
    internal static string NormaliseNumber(string text)
    {
        if (text.Length > 1 && text[0] == '0' && char.ToLowerInvariant(text[1]) == 'x') return "0x" + text[2..].ToUpperInvariant();
        string lower = text.ToLowerInvariant();
        if (lower.Length > 1 && lower[0] == '0' && lower[1] is 'b' or 'o') return lower;
        string suffix = lower.EndsWith('j') ? "j" : "";
        string body = suffix.Length > 0 ? lower[..^1] : lower;
        int e = body.IndexOf('e');
        string mantissa = e >= 0 ? body[..e] : body, exponent = e >= 0 ? body[e..] : "";
        if (mantissa.Contains('.'))
        {
            if (mantissa.StartsWith('.')) mantissa = "0" + mantissa;
            if (mantissa.EndsWith('.')) mantissa += "0";
        }
        return mantissa + exponent + suffix;
    }

    internal static string NormaliseString(string s, QuoteStyle style = QuoteStyle.Double)
    {
        if (style == QuoteStyle.Preserve) return s;
        int q = 0;
        while (q < s.Length && s[q] != '"' && s[q] != '\'') q++;
        if (q >= s.Length) return s;
        if (style == QuoteStyle.Single && s[q] == '"')
        {
            string r = s[q..];
            bool isTriple = r.Length >= 6 && r.StartsWith("\"\"\"");
            if (isTriple || r.Length < 2 || !r.EndsWith("\"")) return s;
            string content = r[1..^1];
            if (content.Contains('\'') || content.Contains('\\') || content.Contains('"')) return s;
            return s[..q] + "'" + content + "'";
        }
        if (style == QuoteStyle.Double) return BlackQuotes(s, q);
        if (s[q] != '\'') return s;
        // single quotes are what the style wants: only triple single quotes still become triple double quotes
        if (style == QuoteStyle.Single && !s[q..].StartsWith("'''")) return s;
        string prefix = s[..q];
        string rest = s[q..];
        bool triple = rest.Length >= 6 && rest.StartsWith("'''") && rest.EndsWith("'''");
        int n = triple ? 3 : 1;
        if (rest.Length < 2 * n || !rest.EndsWith("'")) return s;
        string inner = rest[n..^n];
        if (inner.Contains('"') || inner.Contains('\\')) return s;
        if (triple && inner.EndsWith("'")) return s;
        if (!triple && inner.Contains('\'')) return s;   // a stray quote means the token is not what it looks like
        string quote = triple ? "\"\"\"" : "\"";
        return prefix + quote + inner + quote;
    }

    /// <summary>
    /// Black's quote normalisation: the other quote is used when it needs fewer escapes (equal counts prefer double quotes), escapes of
    /// the quote that is going away are dropped, and an f-string whose replacement field has a backslash is left alone.
    /// </summary>
    static string BlackQuotes(string s, int q)
    {
        string prefix = s[..q], v = s[q..];
        if (v.StartsWith("\"\"\"")) return s;
        string origQ, newQ;
        if (v.StartsWith("'''")) { origQ = "'''"; newQ = "\"\"\""; }
        else if (v[0] == '"') { origQ = "\""; newQ = "'"; }
        else { origQ = "'"; newQ = "\""; }
        if (v.Length < 2 * origQ.Length || !v.EndsWith(origQ)) return s;
        string body = v[origQ.Length..^origQ.Length];
        string newBody;
        if (prefix.Contains('r') || prefix.Contains('R'))
        {
            if (Regex.IsMatch(body, @"(([^\\]|^)(\\\\)*)" + Regex.Escape(newQ))) return s;   // a quote that is not escaped (an even number of backslashes before it)
            newBody = body;
        }
        else
        {
            string eq = Regex.Escape(newQ), eo = Regex.Escape(origQ);
            newBody = SubTwice(@"([^\\]|^)\\((?:\\\\)*)" + eq, "${1}${2}" + newQ, body);
            if (body != newBody) { body = newBody; s = prefix + origQ + body + origQ; }
            newBody = SubTwice(@"([^\\]|^)\\((?:\\\\)*)" + eo, "${1}${2}" + origQ, newBody);
            newBody = SubTwice(@"(([^\\]|^)(\\\\)*)" + eq, "${1}\\" + newQ, newBody);
        }
        if ((prefix.Contains('f') || prefix.Contains('F')) && Regex.IsMatch(v[origQ.Length..^origQ.Length], @"(([^\\]|^)(\\\\)*)" + Regex.Escape(origQ))) return s;   // a nested f-string with the same quotes
        if (prefix.Contains('f') || prefix.Contains('F'))
            foreach (Match m in Regex.Matches(newBody, @"(?:(?<!\{)|^)\{([^{].*?)\}(?:(?!\})|$)"))
                if (m.Groups[1].Value.Contains('\\')) return s;
        if (newQ == "\"\"\"" && newBody.EndsWith('"')) newBody = newBody[..^1] + "\\\"";
        int origEscapes = CountOf(body, "\\"), newEscapes = CountOf(newBody, "\\");
        if (newEscapes > origEscapes) return s;
        if (newEscapes == origEscapes && origQ == "\"") return s;
        return prefix + newQ + newBody + newQ;
    }

    static string SubTwice(string pattern, string replacement, string input) =>
        Regex.Replace(Regex.Replace(input, pattern, replacement), pattern, replacement);

    static int CountOf(string text, string part)
    {
        int n = 0;
        for (int i = text.IndexOf(part, StringComparison.Ordinal); i >= 0; i = text.IndexOf(part, i + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    internal static string NormaliseComment(string c, bool firstLine)
    {
        c = c.TrimEnd();
        if (c.Length <= 1) return "#";
        char next = c[1];
        if (next is ' ' or '!' or ':' or '#' or '\'') return c;
        return "# " + c[1..];
    }

    // ------------------------------------------------------------ rendering

    sealed class Line
    {
        public bool IsComment;
        public List<Token> Tokens = new();
        public int Level;
        public int OriginalColumn;
        public int BlankBefore;
        public int Start;               // source offset of the first token
        public int PrevEnd;             // where the previous line ended (after its line break): the blank lines before this one start here
        public int EndPos;              // end of the last token (a comment after the code included)
        public string Text = "";
        public bool Verbatim;           // inside # fmt: off ... # fmt: on, or marked # fmt: skip: written as it is
        public int OrigBlank;           // the blank lines before it in the source
    }

    sealed class Renderer
    {
        readonly string _s;
        readonly List<Token> _t;
        readonly string _nl;
        readonly FormatOptions _o;
        int IndentWidth => _o.IndentWidth;

        public Renderer(string source, List<Token> tokens, FormatOptions options, Module? module = null)
        {
            _o = options;
            _s = source; _t = tokens;
            _nl = source.Contains("\r\n") ? "\r\n" : "\n";
            if (module != null) _softKeywords = SoftKeywordStarts(source, module);
        }

        /// <summary>Where `match`, `case` and `type` are keywords: a bracket after them is a pattern or a subject, not a call or a subscript (`case ["x"]:`).</summary>
        readonly HashSet<int> _softKeywords = new();

        static HashSet<int> SoftKeywordStarts(string source, Module module)
        {
            var starts = new HashSet<int>();
            foreach (var node in AstChildren.Descendants(module))
            {
                if (node is MatchStmt ms)
                {
                    starts.Add(ms.Start);
                    foreach (var c in ms.Cases)
                    {
                        int at = source.LastIndexOf("case", c.Pattern.Start, Math.Min(c.Pattern.Start + 1, 200), StringComparison.Ordinal);
                        if (at >= 0) starts.Add(at);
                    }
                }
                else if (node is TypeAliasStmt ta) starts.Add(ta.Start);
            }
            return starts;
        }

        public string Run()
        {
            var lines = BuildLines();
            if (lines.Count == 0) return "";
            MarkVerbatim(lines);
            lines = SplitStatements(lines);
            lines = JoinDummyBodies(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Verbatim) { l.Text = _s[LineStart(l.Start)..l.EndPos]; continue; }
                l.Text = l.IsComment ? NormaliseComment(_s[l.Tokens[0].Start..l.Tokens[0].End], l.Start == FirstNonBlank())
                    : IsDocstringStatement(lines, i) ? RenderDocstring(l)
                    : RenderCode(l);
            }
            ApplyBlankLines(lines);
            foreach (var v in lines.Where(x => x.Verbatim)) v.BlankBefore = v.OrigBlank;

            var sb = new StringBuilder();
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (i > 0) for (int b = 0; b < l.BlankBefore; b++) sb.Append(_nl);
                if (!l.Verbatim) sb.Append(new string(' ', l.Level * IndentWidth));
                sb.Append(l.Text).Append(_nl);
            }
            return sb.ToString();
        }

        /// <summary>The text with only the logical lines that touch [selStart, selEnd] formatted; the others stay as they were.</summary>
        public string RunRange(int selStart, int selEnd)
        {
            var lines = BuildLines();
            if (lines.Count == 0) return _s;
            MarkVerbatim(lines);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (l.Verbatim) continue;
                l.Text = l.IsComment ? NormaliseComment(_s[l.Tokens[0].Start..l.Tokens[0].End], l.Start == FirstNonBlank())
                    : IsDocstringStatement(lines, i) ? RenderDocstring(l)
                    : RenderCode(l);
            }
            ApplyBlankLines(lines);

            bool Selected(Line l) => !l.Verbatim && LineStart(l.Start) <= selEnd && l.EndPos >= selStart;
            var sb = new StringBuilder();
            int afterLast = 0;
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                // the line break that follows the line in the source
                int p = l.EndPos;
                if (p < _s.Length && _s[p] == '\r') p++;
                if (p < _s.Length && _s[p] == '\n') p++;
                afterLast = p;

                if (Selected(l))
                {
                    if (i > 0) for (int b = 0; b < l.BlankBefore; b++) sb.Append(_nl);
                    sb.Append(new string(' ', l.Level * IndentWidth)).Append(l.Text).Append(_nl);
                }
                else sb.Append(_s, l.PrevEnd, p - l.PrevEnd);
            }
            // blank lines after the last line are not part of any line: they stay
            sb.Append(_s, afterLast, _s.Length - afterLast);
            return sb.ToString();
        }

        /// <summary>The string that is the first statement of a module, class or function.</summary>
        bool IsDocstringStatement(List<Line> lines, int i)
        {
            var l = lines[i];
            if (l.IsComment || l.Tokens.Count == 0 || l.Tokens[0].Kind != TokenKind.String) return false;
            if (l.Tokens.Skip(1).Any(t => t.Kind != TokenKind.Comment)) return false;
            int prev = i - 1;
            while (prev >= 0 && lines[prev].IsComment) prev--;
            if (prev < 0) return l.Level == 0;
            var header = lines[prev];
            return header.Level == l.Level - 1 && IsDef(header) && EndsWithColon(header);
        }

        bool IsClassDocstring(List<Line> lines, int i)
        {
            if (i < 0 || !IsDocstringStatement(lines, i)) return false;
            int header = i - 1;
            while (header >= 0 && lines[header].IsComment) header--;
            return header >= 0 && FirstWord(lines[header]) == "class";
        }

        string RenderDocstring(Line l)
        {
            string text = Docstrings.Format(l.Tokens[0].Text(_s), l.Level * IndentWidth, _nl, _o);
            if (l.Tokens.Count > 1) text += "  " + NormaliseComment(l.Tokens[1].Text(_s), false);
            return text;
        }

        int FirstNonBlank()
        {
            int i = 0;
            while (i < _s.Length && char.IsWhiteSpace(_s[i])) i++;
            return i;
        }

        // ---------------------------------------------------------- lines

        /// <summary>`# fmt: off` on a line of its own up to `# fmt: on` (or the end), and lines ending in `# fmt: skip`, are left exactly as they are.</summary>
        void MarkVerbatim(List<Line> lines)
        {
            static bool Is(string comment, string word)
            {
                string c = comment.TrimStart('#').Trim();
                return c == "fmt: " + word || c == "fmt:" + word;
            }
            bool off = false;
            foreach (var l in lines)
            {
                l.OrigBlank = l.BlankBefore;
                string? comment = l.IsComment ? _s[l.Tokens[0].Start..l.Tokens[0].End] : null;
                if (comment != null && Is(comment, "on")) { off = false; continue; }
                if (off) { l.Verbatim = true; continue; }
                if (comment != null && Is(comment, "off")) { off = true; continue; }
                if (!l.IsComment && l.Tokens.Count > 0 && l.Tokens[^1].Kind == TokenKind.Comment)
                {
                    string trailing = _s[l.Tokens[^1].Start..l.Tokens[^1].End];
                    if (trailing.Contains("fmt: skip") || trailing.Contains("fmt:skip")) l.Verbatim = true;
                    else if (trailing.Contains("fmt: off") || trailing.Contains("fmt:off")) throw new InvalidOperationException("a trailing fmt: off");   // black reads it in ways we do not follow
                }
            }
        }

        /// <summary>
        /// A def or class whose whole body is `...` (a dummy implementation: an overload, a protocol) has it on its own line, as black does
        /// since 24: `def f(x: int) -> int: ...`, a comment after the `...` going along. A docstring before it, a comment line or a
        /// comment after the header keep the body where it is; if, for, while and the other blocks are left alone.
        /// </summary>
        List<Line> JoinDummyBodies(List<Line> lines)
        {
            var result = new List<Line>(lines.Count);
            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (i + 1 < lines.Count && !l.IsComment && !l.Verbatim && IsDef(l) && l.Tokens[^1].Kind == TokenKind.Operator && l.Tokens[^1].Text(_s) == ":")
                {
                    var body = lines[i + 1];
                    bool dummy = !body.IsComment && !body.Verbatim && body.Level == l.Level + 1 && body.Tokens.Count >= 1
                        && body.Tokens[0].Kind == TokenKind.Operator && body.Tokens[0].Text(_s) == "..."
                        && body.Tokens.Skip(1).All(t => t.Kind == TokenKind.Comment)
                        && (i + 2 >= lines.Count || lines[i + 2].Level <= l.Level);
                    if (dummy)
                    {
                        result.Add(new Line
                        {
                            Level = l.Level, Tokens = l.Tokens.Concat(body.Tokens).ToList(), Start = l.Start, OriginalColumn = l.OriginalColumn,
                            BlankBefore = l.BlankBefore, PrevEnd = l.PrevEnd, EndPos = body.EndPos, OrigBlank = l.OrigBlank,
                        });
                        i++;
                        continue;
                    }
                }
                result.Add(l);
            }
            return result;
        }

        static readonly HashSet<string> CompoundKeywords = new() { "if", "elif", "else", "for", "while", "try", "except", "finally", "with", "class", "def" };

        /// <summary>`if x: y = 1` becomes a header and a body line, `a = 1; b = 2` two lines (a stub body `...` of a def or class stays where it is).</summary>
        List<Line> SplitStatements(List<Line> lines)
        {
            var result = new List<Line>(lines.Count);
            foreach (var l in lines)
            {
                if (l.IsComment || l.Verbatim || l.Tokens.Count < 2) { result.Add(l); continue; }
                string T(Token t) => t.Text(_s);
                var toks = l.Tokens;
                int first = toks[0].Kind == TokenKind.Name && T(toks[0]) == "async" && toks.Count > 1 ? 1 : 0;
                bool compound = toks[first].Kind == TokenKind.Keyword && CompoundKeywords.Contains(T(toks[first]));

                // the colon that ends the header: at depth 0, not the one of a lambda
                int colon = -1;
                if (compound)
                {
                    int depth = 0, lambdas = 0;
                    for (int k = first; k < toks.Count; k++)
                    {
                        string text = T(toks[k]);
                        if (toks[k].Kind == TokenKind.Operator && text is "(" or "[" or "{") depth++;
                        else if (toks[k].Kind == TokenKind.Operator && text is ")" or "]" or "}") depth--;
                        else if (depth == 0 && toks[k].Kind == TokenKind.Keyword && text == "lambda") lambdas++;
                        else if (depth == 0 && toks[k].Kind == TokenKind.Operator && text == ":")
                        {
                            if (lambdas > 0) lambdas--; else { colon = k; break; }
                        }
                    }
                }

                var head = colon >= 0 ? toks.Take(colon + 1).ToList() : null;
                var rest = colon >= 0 ? toks.Skip(colon + 1).ToList() : toks.ToList();
                if (head != null && rest.All(t => t.Kind == TokenKind.Comment)) { result.Add(l); continue; }   // `if x:  # comment`
                Token? trailingComment = rest.Count > 0 && rest[^1].Kind == TokenKind.Comment ? rest[^1] : null;
                var body = trailingComment != null ? rest.Take(rest.Count - 1).ToList() : rest;
                if (head != null && body.Count == 1 && T(body[0]) == "..." && T(toks[first]) is "def" or "class") { result.Add(l); continue; }

                // statements separated by semicolons at depth 0
                var statements = new List<List<Token>> { new() };
                int d = 0;
                foreach (var t in body)
                {
                    string text = T(t);
                    if (t.Kind == TokenKind.Operator && text is "(" or "[" or "{") d++;
                    else if (t.Kind == TokenKind.Operator && text is ")" or "]" or "}") d--;
                    if (d == 0 && t.Kind == TokenKind.Operator && text == ";") { statements.Add(new List<Token>()); continue; }
                    statements[^1].Add(t);
                }
                statements.RemoveAll(x => x.Count == 0);
                if (head == null && statements.Count <= 1 && !toks.Any(t => t.Kind == TokenKind.Operator && T(t) == ";")) { result.Add(l); continue; }

                int level = l.Level;
                int prevEnd = l.PrevEnd;
                if (head != null)
                {
                    result.Add(new Line { Level = level, Tokens = head, Start = head[0].Start, OriginalColumn = l.OriginalColumn, BlankBefore = l.BlankBefore, PrevEnd = l.PrevEnd, EndPos = head[^1].End, OrigBlank = l.OrigBlank });
                    prevEnd = head[^1].End;
                    level++;
                }
                for (int n = 0; n < statements.Count; n++)
                {
                    var tokens = statements[n];
                    if (n == statements.Count - 1 && trailingComment != null) tokens = tokens.Append(trailingComment.Value).ToList();
                    result.Add(new Line
                    {
                        Level = level, Tokens = tokens, Start = tokens[0].Start, OriginalColumn = l.OriginalColumn, PrevEnd = prevEnd, EndPos = tokens[^1].End,
                        BlankBefore = head == null && n == 0 ? l.BlankBefore : 0, OrigBlank = l.OrigBlank,
                    });
                    prevEnd = tokens[^1].End;
                }
            }
            return result;
        }

        List<Line> BuildLines()
        {
            var lines = new List<Line>();
            var pendingComments = new List<Line>();
            var columnOfLevel = new Dictionary<int, int>();
            int level = 0, prevLevel = 0, prevEnd = 0;
            bool any = false;

            void Resolve(int nextLevel)
            {
                foreach (var c in pendingComments)
                {
                    int chosen = nextLevel;
                    if (nextLevel < prevLevel)
                    {
                        for (int L = prevLevel; L > nextLevel; L--)
                            if (columnOfLevel.TryGetValue(L, out int col) && col <= c.OriginalColumn) { chosen = L; break; }
                    }
                    c.Level = chosen;
                    lines.Add(c);
                }
                pendingComments.Clear();
            }

            int i = 0;
            while (i < _t.Count)
            {
                var tok = _t[i];
                switch (tok.Kind)
                {
                    case TokenKind.Indent: level++; i++; continue;
                    case TokenKind.Dedent: level = Math.Max(0, level - 1); i++; continue;
                    case TokenKind.Newline: i++; continue;
                    case TokenKind.EndOfFile: i++; continue;
                    case TokenKind.Comment:
                    {
                        // a comment that starts its line (comments after code are inside a code line below)
                        var c = new Line { IsComment = true, Start = tok.Start, OriginalColumn = tok.Start - LineStart(tok.Start), Tokens = { tok } };
                        c.BlankBefore = any ? BlankLinesBetween(prevEnd, tok.Start) : 0;
                c.PrevEnd = prevEnd;
                c.EndPos = tok.End;
                        prevEnd = EndOfLine(tok.End);
                        pendingComments.Add(c);
                        any = true;
                        i++;
                        continue;
                    }
                }

                // a code line: up to the logical Newline
                var line = new Line { Level = level, Start = tok.Start, OriginalColumn = tok.Start - LineStart(tok.Start) };
                line.BlankBefore = any ? BlankLinesBetween(prevEnd, tok.Start) : 0;
                while (i < _t.Count && _t[i].Kind != TokenKind.Newline && _t[i].Kind != TokenKind.EndOfFile)
                {
                    if (_t[i].Kind is not (TokenKind.Indent or TokenKind.Dedent)) line.Tokens.Add(_t[i]);
                    i++;
                }
                line.PrevEnd = prevEnd;
                line.EndPos = line.Tokens[^1].End;
                prevEnd = i < _t.Count && _t[i].Kind == TokenKind.Newline ? _t[i].End : line.Tokens[^1].End;
                Resolve(level);
                columnOfLevel[level] = line.OriginalColumn;
                foreach (var key in columnOfLevel.Keys.Where(k => k > level).ToList()) columnOfLevel.Remove(key);
                lines.Add(line);
                prevLevel = level;
                any = true;
            }
            Resolve(0);
            if (pendingComments.Count > 0) throw new InvalidOperationException();
            return lines;
        }

        int LineStart(int pos)
        {
            while (pos > 0 && _s[pos - 1] != '\n' && _s[pos - 1] != '\r') pos--;
            return pos;
        }

        int EndOfLine(int pos)
        {
            while (pos < _s.Length && _s[pos] != '\n' && _s[pos] != '\r') pos++;
            if (pos < _s.Length && _s[pos] == '\r') pos++;
            if (pos < _s.Length && _s[pos] == '\n') pos++;
            return pos;
        }

        int BlankLinesBetween(int from, int tokenStart)
        {
            int end = LineStart(tokenStart), n = 0;
            for (int p = from; p < end; p++) if (_s[p] == '\n') n++;
            return n;
        }

        // ---------------------------------------------------------- blank lines

        string FirstWord(Line l) => l.IsComment || l.Tokens.Count == 0 ? "" : l.Tokens[0].Text(_s);

        bool IsDef(Line l)
        {
            string w = FirstWord(l);
            if (w is "def" or "class") return true;
            return w == "async" && l.Tokens.Count > 1 && l.Tokens[1].Text(_s) == "def";
        }

        bool IsDecorator(Line l) => FirstWord(l) == "@";
        bool IsImport(Line l) => FirstWord(l) is "import" || (FirstWord(l) == "from" && l.Tokens.Any(t => t.Kind == TokenKind.Keyword && t.Text(_s) == "import"));
        bool IsDocstring(Line l) => !l.IsComment && l.Tokens.Count >= 1 && l.Tokens[0].Kind == TokenKind.String
            && l.Tokens.All(t => t.Kind is TokenKind.String or TokenKind.Comment);

        /// <summary>A def, a class or a decorator starts a definition; so do the comments directly above one.</summary>
        bool StartsDefinition(List<Line> lines, int i)
        {
            var l = lines[i];
            if (!l.IsComment) return IsDef(l) || IsDecorator(l);
            for (int j = i + 1; j < lines.Count; j++)
            {
                if (lines[j].Level != l.Level || lines[j].BlankBefore > 0) return false;
                if (!lines[j].IsComment) return IsDef(lines[j]) || IsDecorator(lines[j]);
            }
            return false;
        }

        /// <summary>An else, elif, except or finally (or the like): a clause that belongs to the statement above, not a new one.</summary>
        bool DependentClause(Line l) => EndsWithColon(l) && FirstWord(l) is not ("with" or "try" or "for" or "while" or "if" or "match");

        bool EndsWithColon(Line l) => !l.IsComment && l.Tokens.Count > 0 &&
            l.Tokens.Where(t => t.Kind != TokenKind.Comment).Select(t => t.Text(_s)).LastOrDefault() == ":";

        bool IsStubDef(Line l) => IsDef(l) && !l.IsComment &&
            l.Tokens.Where(t => t.Kind != TokenKind.Comment).Select(t => t.Text(_s)).LastOrDefault() == "...";

        void ApplyBlankLines(List<Line> lines)
        {
            var defs = new Stack<int>();           // levels of the open def/class blocks
            bool moduleDoc = lines.Count > 0 && lines[0].Level == 0 && IsDocstring(lines[0]);

            for (int i = 0; i < lines.Count; i++)
            {
                var l = lines[i];
                if (i == 0) { l.BlankBefore = 0; Track(l); continue; }
                var prev = lines[i - 1];
                int max = l.Level == 0 ? 2 : 1;
                int blank = Math.Min(l.BlankBefore, max);

                bool leftDef = false;
                int poppedLevel = -1;
                while (defs.Count > 0 && defs.Peek() >= l.Level) { poppedLevel = defs.Pop(); leftDef = true; }

                bool afterDecorator = IsDecorator(prev) && !prev.IsComment;
                bool firstInBlock = prev.Level < l.Level;

                bool startsDefinition = StartsDefinition(lines, i);
                bool attachedToComment = l.BlankBefore == 0 && prev.IsComment && prev.Level == l.Level && StartsDefinition(lines, i - 1);

                if (afterDecorator) blank = 0;
                else if (firstInBlock && !(startsDefinition && !l.IsComment && !(IsDef(prev) && !prev.IsComment))) blank = prev.IsComment || IsDocstringStatement(lines, i) ? 0 : Math.Min(l.BlankBefore, 1);   // black keeps one blank line at the start of a block, but not before a docstring
                else if (i == 1 && moduleDoc && (!startsDefinition || (IsDecorator(l) && !l.IsComment))) blank = 1;   // black: one blank line after the module docstring, two before a def or class (but a decorator still gets one)
                else if (startsDefinition)
                {
                    if (attachedToComment) blank = 0;
                    // overload stubs stay together: the next def, or the decorator of the next one, as the source has it
                    else if ((IsDef(l) || IsDecorator(l) && !l.IsComment) && IsStubDef(prev) && prev.Level == l.Level && l.BlankBefore == 0) blank = 0;
                    else blank = l.Level == 0 ? 2 : 1;
                }
                else if (IsClassDocstring(lines, i - 1) && prev.Level == l.Level) blank = 1;
                else if (leftDef) blank = l.Level == 0 && !(poppedLevel > 0 && DependentClause(l)) ? 2 : 1;
                else if (IsImport(prev) && !IsImport(l) && prev.Level == l.Level) blank = 1;   // black: exactly one blank line after the imports, even when there were two

                l.BlankBefore = blank;
                Track(l);
            }

            void Track(Line cur)
            {
                if (!cur.IsComment && IsDef(cur)) defs.Push(cur.Level);
            }
        }

        // ---------------------------------------------------------- code lines

        sealed class Frame
        {
            public char Open;
            public bool SawColon;      // an annotation in the current parameter
        }

        string RenderCode(Line line)
        {
            var toks = line.Tokens;
            var sb = new StringBuilder();
            var frames = new Stack<Frame>();
            var lambdaDepths = new Stack<int>();
            int newIndent = line.Level * IndentWidth;
            int oldIndent = LeadingWidth(line.Start);
            bool startsWithFrom = toks[0].Text(_s) == "from";
            bool inRelativeDots = false;

            bool lastNoSpaceAfter = false;      // the previous token was a unary operator, `=` of a keyword argument, a hugged `**`...
            bool sliceSpaced = false;           // the spacing after the last slice colon
            bool lastWasSliceColon = false;

            var pieces = new List<Piece>();     // the same line as tokens with their spaces, for the line wrapper
            bool legacyOnly = false;            // backslashes and multi-line strings: keep the line breaks as they are

            for (int k = 0; k < toks.Count; k++)
            {
                var t = toks[k];
                string text = t.Text(_s);
                var p = k > 0 ? toks[k - 1] : (Token?)null;
                string pText = p.HasValue ? p.Value.Text(_s) : "";
                string gap = p.HasValue ? _s[p.Value.End..t.Start] : "";
                bool top = frames.Count > 0;

                // the previous token that is not a comment decides the spacing
                Token? ps = null;
                for (int j = k - 1; j >= 0 && ps == null; j--) if (toks[j].Kind != TokenKind.Comment) ps = toks[j];
                string psText = ps.HasValue ? ps.Value.Text(_s) : "";

                // ---- the gap: spaces, or kept line breaks inside brackets
                bool unary = false, kwargEq = false, hugPow = false, sliceColon = false, lambdaColon = false;
                if (t.Kind == TokenKind.Operator)
                {
                    if (text is "+" or "-" or "~" or "*" or "**" or "@") unary = IsUnaryPosition(ps, psText, k);
                    if (text == "=" && ((top && frames.Peek().Open == '(' && !frames.Peek().SawColon) || (lambdaDepths.Count > 0 && lambdaDepths.Peek() == frames.Count))) kwargEq = true;
                    if (text == ":")
                    {
                        if (lambdaDepths.Count > 0 && lambdaDepths.Peek() == frames.Count) lambdaColon = true;
                        else if (top && frames.Peek().Open == '[') sliceColon = true;
                    }
                    if (text == "**" && !unary) hugPow = IsSimplePower(toks, k);
                }

                bool lineBreak = gap.Contains('\n') || gap.Contains('\r');
                bool backslash = gap.Contains('\\');
                if (t.Kind == TokenKind.Comment)
                {
                    // after a token on its line: it follows that token; on a line of its own inside brackets: a piece of its own
                    string comment = NormaliseComment(text, false);
                    if (lineBreak || pieces.Count == 0) pieces.Add(new Piece { Text = comment, Kind = TokenKind.Comment, Standalone = true });
                    else pieces[^1].Comments.Add(comment);
                }
                if (backslash || (t.Kind == TokenKind.String && text.Contains('\n'))) legacyOnly = true;

                bool sliceBefore = false, sliceAfter = false;
                if (sliceColon) (sliceBefore, sliceAfter) = SliceSpacing(toks, k);
                bool space = false;
                if (k > 0 && t.Kind != TokenKind.Comment && !backslash && ps.HasValue)
                    space = NeedSpace(t, text, ps.Value, psText, toks, k, frames, unary, kwargEq, hugPow, sliceColon, lambdaColon,
                        lastNoSpaceAfter, lastWasSliceColon, sliceSpaced, lineBreak ? " " : gap, startsWithFrom, ref inRelativeDots);
                if (sliceColon && !backslash && !lineBreak) space = sliceBefore;
                if (t.Kind != TokenKind.Comment)
                    pieces.Add(new Piece
                    {
                        Text = Normalise(t, text, line), Space = space, Kind = t.Kind, Unary = unary, KwargEquals = kwargEq,
                        IsOpen = t.Kind == TokenKind.Operator && text is "(" or "[" or "{",
                        IsClose = t.Kind == TokenKind.Operator && text is ")" or "]" or "}",
                    });

                if (k == 0)
                {
                    sb.Append(Normalise(t, text, line));
                }
                else if (lineBreak)
                {
                    if (backslash) sb.Append(gap);
                    else
                    {
                        while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
                        int breaks = Math.Min(gap.Count(c => c == '\n'), 2);
                        for (int b = 0; b < breaks; b++) sb.Append(_nl);
                        // continuation lines keep their offset from the statement, which moves with its re-indentation
                        sb.Append(' ', Math.Max(0, LeadingWidth(t.Start) + newIndent - oldIndent));
                    }
                }
                else if (backslash)
                {
                    sb.Append(gap);
                }
                else if (t.Kind == TokenKind.Comment)
                {
                    sb.Append("  ");
                }
                else if (space)
                {
                    sb.Append(' ');
                }

                if (k > 0)
                {
                    if (t.Kind == TokenKind.Comment) sb.Append(NormaliseComment(text, false));
                    else sb.Append(Normalise(t, text, line));
                }

                // ---- bookkeeping after the token
                lastNoSpaceAfter = false;
                lastWasSliceColon = false;
                if (t.Kind == TokenKind.Operator)
                {
                    switch (text)
                    {
                        case "(" or "[" or "{": frames.Push(new Frame { Open = text[0] }); break;
                        case ")" or "]" or "}": if (frames.Count > 0) frames.Pop(); while (lambdaDepths.Count > 0 && lambdaDepths.Peek() > frames.Count) lambdaDepths.Pop(); break;
                        case ",": if (top) frames.Peek().SawColon = false; break;
                        case ":":
                            if (lambdaColon) lambdaDepths.Pop();
                            else if (sliceColon)
                            {
                                sliceSpaced = sliceAfter;
                                lastWasSliceColon = true;
                            }
                            else if (top && frames.Peek().Open == '(') frames.Peek().SawColon = true;
                            break;
                    }
                    if (unary || kwargEq || hugPow) lastNoSpaceAfter = true;
                }
                else if (t.Kind == TokenKind.Keyword && text == "lambda") lambdaDepths.Push(frames.Count);
            }
            if (legacyOnly || pieces.Count == 0) return sb.ToString().TrimEnd();

            // wrap what is too long, join what was wrapped by hand but fits
            var lines = new LineWrapper(pieces, _o).Wrap(newIndent);
            var result = new StringBuilder();
            for (int n = 0; n < lines.Count; n++)
            {
                if (n > 0) result.Append(_nl).Append(' ', lines[n].Indent);
                result.Append(lines[n].Text);
                if (lines[n].Comment.Length > 0) result.Append("  ").Append(lines[n].Comment);
            }
            return result.ToString();
        }

        int LeadingWidth(int pos)
        {
            int ls = LineStart(pos), w = 0;
            for (int i = ls; i < pos && i < _s.Length; i++)
            {
                if (_s[i] == ' ') w++;
                else if (_s[i] == '\t') w += IndentWidth;
                else break;
            }
            return w;
        }

        string Normalise(Token t, string text, Line line) => t.Kind switch
        {
            TokenKind.String => _o.Quotes == QuoteStyle.Preserve ? text : NormaliseString(NormalisePrefixAndEscapes(text), _o.Quotes),
            TokenKind.Number => NormaliseNumber(text),
            TokenKind.Comment => NormaliseComment(text, false),
            _ => text,
        };

        static bool IsValueEnd(Token t, string text) =>
            t.Kind is TokenKind.Name or TokenKind.Number or TokenKind.String
            || (t.Kind == TokenKind.Keyword && text is "True" or "False" or "None")
            || (t.Kind == TokenKind.Operator && text is ")" or "]" or "}" or "...");

        bool IsUnaryPosition(Token? p, string pText, int k)
        {
            if (!p.HasValue) return true;
            if (IsValueEnd(p.Value, pText)) return false;
            return true;
        }

        /// <summary>`x**2`, `a.b**c.d` hug the operator in black; calls, subscripts and other expressions keep the spaces.</summary>
        bool IsSimplePower(List<Token> toks, int k)
        {
            // left operand: name or number, possibly an attribute chain of names
            int j = k - 1;
            if (j < 0) return false;
            if (toks[j].Kind is not (TokenKind.Name or TokenKind.Number)) return false;
            while (j >= 2 && toks[j - 1].Text(_s) == "." && toks[j - 2].Kind == TokenKind.Name) j -= 2;
            if (j >= 1 && toks[j - 1].Text(_s) == ".") return false;
            // right operand: optional unary sign, then a name or number with attributes, but no call or subscript
            int r = k + 1;
            if (r < toks.Count && toks[r].Kind == TokenKind.Operator && toks[r].Text(_s) is "-" or "+" or "~") r++;
            if (r >= toks.Count || toks[r].Kind is not (TokenKind.Name or TokenKind.Number)) return false;
            while (r + 2 < toks.Count && toks[r + 1].Text(_s) == "." && toks[r + 2].Kind == TokenKind.Name) r += 2;
            if (r + 1 < toks.Count && toks[r + 1].Kind == TokenKind.Operator && toks[r + 1].Text(_s) is "(" or "[") return false;
            return true;
        }

        bool NeedSpace(Token t, string text, Token p, string pText, List<Token> toks, int k, Stack<Frame> frames,
            bool unary, bool kwargEq, bool hugPow, bool sliceColon, bool lambdaColon,
            bool lastNoSpaceAfter, bool lastWasSliceColon, bool sliceSpaced, string gap, bool startsWithFrom, ref bool inRelativeDots)
        {
            bool pOp = p.Kind == TokenKind.Operator;
            bool nOp = t.Kind == TokenKind.Operator;

            // `from . import x`, `from ..pkg import y`, `from ... import z`
            if (startsWithFrom)
            {
                bool pDots = pOp && pText is "." or "...";
                if (pText == "from") { inRelativeDots = nOp && text is "." or "..."; return true; }
                if (inRelativeDots && pDots)
                {
                    if (nOp && text is "." or "...") return false;
                    return t.Kind == TokenKind.Keyword;   // `import`
                }
            }

            if (nOp && text is "," or ";") return false;
            if (pOp && pText == "," ) return !(nOp && text is ")" or "]" or "}");
            if (pOp && pText is "(" or "[" or "{") return false;
            if (nOp && text is ")" or "]" or "}") return false;

            if (lastWasSliceColon) return sliceSpaced;
            if (lastNoSpaceAfter) return false;

            if (nOp)
            {
                switch (text)
                {
                    case ".": return p.Kind == TokenKind.Keyword && pText is not ("True" or "False" or "None") ? true : false;
                    case "(" or "[":
                        if (IsValueEnd(p, pText) && !_softKeywords.Contains(p.Start)) return false;
                        return true;
                    case ":":
                        if (sliceColon) return gap.Length > 0 && SpacedAfter(toks, k);
                        return false;
                    case "=":
                        if (kwargEq) return false;
                        return true;
                    case "->" or ":=": return true;
                }
                if (unary) return true;                       // after a value-start token, a space precedes the prefix operator
                if (hugPow) return false;
                return true;                                  // binary operators
            }

            // the token is a word, number or string
            if (pOp)
            {
                if (pText == ".") return t.Kind == TokenKind.Keyword && text is not ("True" or "False" or "None");
                if (pText == ":" ) return true;
                return true;
            }
            return true;
        }

        /// <summary>
        /// Black's slice spacing: a subscript element with anything but names, numbers, strings and signs in it ("complex": arithmetic, a call,
        /// an attribute, a comparison, a condition...) has spaces around its colons like around an operator, as in <c>x[a + 1 : b]</c>;
        /// a colon with nothing before it hugs the bracket or comma, one with nothing after it does not take a space after it,
        /// and a simple element has no spaces at all.
        /// </summary>
        (bool Before, bool After) SliceSpacing(List<Token> toks, int k)
        {
            bool Is(int j, string text) => j >= 0 && j < toks.Count && toks[j].Kind == TokenKind.Operator && toks[j].Text(_s) == text;
            bool Ends(int j) => j >= toks.Count || Is(j, "]") || Is(j, ",");
            int start = k, end = k;
            for (int j = k - 1, depth = 0; j >= 0; j--)
            {
                if (toks[j].Kind == TokenKind.Operator)
                {
                    string x = toks[j].Text(_s);
                    if (x is ")" or "]" or "}") depth++;
                    else if (x is "(" or "[" or "{") { if (depth == 0) break; depth--; }
                    else if (x == "," && depth == 0) break;
                }
                start = j;
            }
            for (int j = k + 1, depth = 0; j < toks.Count; j++)
            {
                if (toks[j].Kind == TokenKind.Operator)
                {
                    string x = toks[j].Text(_s);
                    if (x is "(" or "[" or "{") depth++;
                    else if (x is ")" or "]" or "}") { if (depth == 0) break; depth--; }
                    else if (x == "," && depth == 0) break;
                }
                end = j;
            }
            bool complex = false;
            for (int j = start; j <= end && !complex; j++)
            {
                var tk = toks[j];
                string x = tk.Text(_s);
                bool afterValue = j > start && IsValueEnd(toks[j - 1], toks[j - 1].Text(_s));
                if (tk.Kind == TokenKind.Keyword) complex = x is "and" or "or" or "not" or "in" or "is" or "if" or "else" or "lambda";
                else if (tk.Kind == TokenKind.Operator)
                    complex = x is "." or "<" or ">" or "==" or "!=" or "<=" or ">=" or ":="
                        || (x is "(" or "[" && afterValue)
                        || (x is "+" or "-" or "*" or "/" or "//" or "%" or "**" or "@" or "<<" or ">>" or "&" or "|" or "^" && afterValue);
            }
            if (!complex) return (Is(k - 1, ","), false);   // after a comma the space is the comma's
            bool leading = k == 0 || Is(k - 1, "[") || Is(k - 1, ",");
            bool prevColon = Is(k - 1, ":"), nextColon = Is(k + 1, ":");
            bool before = Is(k - 1, ",") || (!leading && !(prevColon && !Ends(k + 1)));
            bool after = !Ends(k + 1) && !(nextColon && !Ends(k + 2));
            return (before, after);
        }

        bool SpacedAfter(List<Token> toks, int k)
        {
            if (k + 1 >= toks.Count) return false;
            string after = _s[toks[k].End..toks[k + 1].Start];
            return after.Length > 0 && !after.Contains('\n');
        }
    }
}
