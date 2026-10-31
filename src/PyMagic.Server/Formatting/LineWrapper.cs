using System.Text;
using PyMagic.Core.Syntax;

namespace PyMagic.Server.Formatting;

/// <summary>A token of a logical line with the space that precedes it when the line is written on one row.</summary>
internal sealed class Piece
{
    public string Text = "";
    public bool Space;
    public TokenKind Kind;
    public bool Unary;                  // a prefix operator
    public bool KwargEquals;            // the `=` of a keyword argument or default
    public bool IsOpen, IsClose;
    public int Match = -1;
    public List<string> Comments = new();   // comments that follow this token on its line
    public bool Standalone;                 // a comment on a line of its own inside brackets (Text is the comment)
}

/// <summary>One output line: its indentation, the code, and the comments that end it.</summary>
internal readonly record struct OutLine(int Indent, string Text, string Comment = "", bool IsComment = false);

/// <summary>
/// Splits a logical line that is too long, or has a magic trailing comma or a comment of its own inside brackets, the
/// way black does: at the last bracket pair (the parameters of a <c>def</c>), the content of the brackets on its own
/// indented lines, one item per line at the strongest delimiter (commas, then boolean operators, comparisons, arithmetic,
/// dots of a call chain), a trailing comma after the items, and invisible parentheses around the value of an assignment,
/// a return or a condition. Redundant parentheses around such a value are removed first. Comments stay with the token
/// they follow; comments on their own lines stay between the items.
/// </summary>
internal sealed class LineWrapper
{
    readonly FormatOptions _options;
    int Width => _options.LineLength;
    int Indent => _options.IndentWidth;
    const int Comma = 18, Comprehension = 20, Ternary = 16, Logic = 14, StringConcat = 12, Comparator = 10, Dot = 1;

    readonly List<Piece> _p;
    int[] _depth = Array.Empty<int>();
    int[] _before = Array.Empty<int>();      // priority of the delimiter that starts a new item at piece i (0: none)
    bool[] _magic = Array.Empty<bool>();     // an opening bracket whose group must be exploded (trailing comma, comment inside)
    bool[] _atom = Array.Empty<bool>();      // an opening bracket that is not a call, subscript or parameter list
    bool[] _lambdaComma = Array.Empty<bool>();   // a comma between a lambda and its colon
    int _preferred = -1;                     // a bracket pair chosen by the invisible-parenthesis logic for the next right-hand split
    Piece? _statementEnd;                    // the last token as the source has the statement: its comment ends the statement

    public LineWrapper(List<Piece> pieces, FormatOptions options)
    {
        _p = pieces;
        _options = options;
        _statementEnd = pieces.Count > 0 ? pieces[^1] : null;
        if (!options.MagicTrailingComma) RemoveTrailingCommas();
        RemoveRedundantParentheses();
        ParenthesiseSingleTuples();
        int n = _p.Count;
        _depth = new int[n];
        _before = new int[n];
        _magic = new bool[n];
        _atom = new bool[n];
        _lambdaComma = new bool[n];
        Analyse();
    }

    // ------------------------------------------------------------ trailing commas

    /// <summary>With skip-magic-trailing-comma a trailing comma is only kept in a one-element tuple or subscript; wrapping adds it back where lines are split.</summary>
    void RemoveTrailingCommas()
    {
        var match = new int[_p.Count];
        Array.Fill(match, -1);
        var stack = new Stack<int>();
        for (int i = 0; i < _p.Count; i++)
        {
            if (_p[i].IsOpen) stack.Push(i);
            else if (_p[i].IsClose && stack.Count > 0) { int o = stack.Pop(); match[o] = i; match[i] = o; }
        }

        // backwards, so removing a comma does not move the pieces still to be looked at
        for (int i = _p.Count - 2; i >= 1; i--)
        {
            var comma = _p[i];
            if (comma.Standalone || comma.Kind != TokenKind.Operator || comma.Text != "," || !_p[i + 1].IsClose) continue;
            int open = match[i + 1];
            if (open < 0) continue;

            int depth = 0, commas = 0;
            for (int k = open + 1; k < i + 1; k++)
            {
                if (_p[k].IsOpen) depth++;
                else if (_p[k].IsClose) depth--;
                else if (depth == 0 && !_p[k].Standalone && _p[k].Kind == TokenKind.Operator && _p[k].Text == ",") commas++;
            }
            bool atom = open == 0 || !IsValueEnd(_p[open - 1]);
            if (commas == 1 && ((_p[open].Text == "(" && atom) || (_p[open].Text == "[" && !atom))) continue;

            _p[i - 1].Comments.AddRange(comma.Comments);
            _p.RemoveAt(i);
        }
    }

    // ------------------------------------------------------------ redundant parentheses

    /// <summary>`return (x)`, `x = (y)`, `if (a):`, `for (a, b) in (c):`, `del (a)`, `assert (a), (b)`: the parentheses go when they only wrap the value.</summary>
    void RemoveRedundantParentheses()
    {
        if (_p.Count < 3) return;
        string first = _p[0].Text;
        bool colonEnd = _p[^1].Kind == TokenKind.Operator && _p[^1].Text == ":";

        if (first == "return") StripRange(1, _p.Count, allowTuple: false);
        else if (first is "if" or "elif" or "while" && colonEnd) StripRange(1, _p.Count - 1, allowTuple: false);
        else if (first == "for" && colonEnd)
        {
            int inAt = IndexAtDepth0("in", 1);
            if (inAt > 0)
            {
                // the iterable first, so the target's index stays valid
                StripRange(inAt + 1, _p.Count - 1, allowTuple: false);
                StripRange(1, inAt, allowTuple: true);
            }
        }
        else if (first == "del") StripRange(1, _p.Count, allowTuple: false);
        else if (first == "assert")
        {
            int comma = -1, depth = 0;
            for (int i = 1; i < _p.Count; i++)
            {
                if (_p[i].IsOpen) depth++;
                else if (_p[i].IsClose) depth--;
                else if (depth == 0 && _p[i].Kind == TokenKind.Operator && _p[i].Text == ",") { comma = i; break; }
            }
            if (comma > 0) { StripRange(comma + 1, _p.Count, allowTuple: false); StripRange(1, comma, allowTuple: false); }
            else StripRange(1, _p.Count, allowTuple: false);
        }
        else if (first is not ("def" or "class" or "from" or "import" or "with" or "raise" or "lambda" or "async" or "try" or "except" or "else" or "finally" or "global" or "nonlocal" or "pass" or "print" or "yield" or "await"))
        {
            int depth = 0, assign = -1;
            for (int i = 0; i < _p.Count; i++)
            {
                if (_p[i].IsOpen) depth++;
                else if (_p[i].IsClose) depth--;
                else if (depth == 0 && _p[i].Kind == TokenKind.Operator && !_p[i].KwargEquals && IsAssignOperator(_p[i].Text)) assign = i;
            }
            // `x = (yield)` is `x = yield`: as the whole value of an assignment a yield needs no parentheses
            if (assign > 0) StripRange(assign + 1, _p.Count, allowTuple: false, allowYield: true);
        }
        if (first == "with" || first == "async" && _p.Count > 1 && _p[1].Text == "with") StripWithItems(first == "with" ? 1 : 2);
        StripAwaitOperands();
    }

    /// <summary>
    /// `await (x.y())` is `await x.y()`: the parentheses go around a name, an attribute, a call or a subscript; they stay around anything
    /// with an operator, a unary sign, `not`, a conditional, a lambda or another await, as in black.
    /// </summary>
    void StripAwaitOperands()
    {
        for (int i = _p.Count - 3; i >= 0; i--)
        {
            if (_p[i].Kind != TokenKind.Keyword || _p[i].Text != "await" || !_p[i + 1].IsOpen || _p[i + 1].Text != "(") continue;
            int open = i + 1, close = Closer(open);
            if (close < 0 || close - open < 2 || !PlainOperand(open + 1, close)) continue;
            _p[open + 1].Space = true;
            _p[open + 1].Comments.InsertRange(0, _p[open].Comments);
            _p[close - 1].Comments.AddRange(_p[close].Comments);
            _p.RemoveAt(close);
            _p.RemoveAt(open);
        }
    }

    /// <summary>A name or literal followed only by trailers: `.attr`, `(args)`, `[index]`.</summary>
    bool PlainOperand(int start, int end)
    {
        var head = _p[start];
        if (head.Standalone || head.Kind is not (TokenKind.Name or TokenKind.Number or TokenKind.String)) return false;
        int depth = 0;
        for (int i = start + 1; i < end; i++)
        {
            var t = _p[i];
            if (t.Standalone) return false;
            if (t.IsOpen) { if (depth == 0 && t.Text == "{") return false; depth++; continue; }
            if (t.IsClose) { depth--; continue; }
            if (depth > 0) continue;
            if (t.Kind == TokenKind.Operator && t.Text == ".") continue;
            if (t.Kind == TokenKind.Name && _p[i - 1].Text == ".") continue;
            return false;
        }
        return true;
    }

    /// <summary>
    /// `with (open(a)) as f:` is `with open(a) as f:`, and `with (a as f, b as g):` loses its parentheses too; a with that is too long
    /// gets them back from the wrapping when the Pythons the file is for allow them (<see cref="WrapWith"/>).
    /// </summary>
    void StripWithItems(int start)
    {
        int end = _p.Count - 1;
        if (end <= start || _p[end].Kind != TokenKind.Operator || _p[end].Text != ":") return;
        // the parentheses around all the items
        if (_p[start].IsOpen && _p[start].Text == "(" && Closer(start) == end - 1 && ParenthesisedItems(start, end - 1))
        {
            _p[start + 1].Space = _p[start].Space;
            _p[start + 1].Comments.InsertRange(0, _p[start].Comments);
            int close = end - 1;
            // a trailing comma inside goes with them
            if (_p[close - 1].Kind == TokenKind.Operator && _p[close - 1].Text == ",") { _p[close - 2].Comments.AddRange(_p[close - 1].Comments); _p.RemoveAt(close - 1); close--; }
            _p[close - 1].Comments.AddRange(_p[close].Comments);
            _p.RemoveAt(close);
            _p.RemoveAt(start);
            end = _p.Count - 1;
        }
        // then each item's own: `(open(a)) as f`
        var items = new List<int> { start };
        int depth = 0;
        for (int i = start; i < end; i++)
        {
            if (_p[i].IsOpen) depth++;
            else if (_p[i].IsClose) depth--;
            else if (depth == 0 && _p[i].Kind == TokenKind.Operator && _p[i].Text == ",") items.Add(i + 1);
        }
        for (int k = items.Count - 1; k >= 0; k--)
        {
            int s = items[k];
            if (!_p[s].IsOpen || _p[s].Text != "(") continue;
            int close = Closer(s);
            int itemEnd = k + 1 < items.Count ? items[k + 1] - 1 : end;
            int asAt = -1;
            for (int i = close + 1; i < itemEnd; i++) if (_p[i].Kind == TokenKind.Keyword && _p[i].Text == "as") { asAt = i; break; }
            if (close < 0 || !(close + 1 == itemEnd || close + 1 == asAt)) continue;
            StripRange(s, close + 1, allowTuple: false);
        }
    }

    /// <summary>Parentheses that hold with-items (`a as f, b as g`, or several managers), not one parenthesised expression.</summary>
    bool ParenthesisedItems(int open, int close)
    {
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            if (_p[i].Standalone) return false;
            if (_p[i].IsOpen) depth++;
            else if (_p[i].IsClose) depth--;
            else if (depth == 0 && (_p[i].Kind == TokenKind.Keyword && _p[i].Text == "as" || _p[i].Kind == TokenKind.Operator && _p[i].Text == ",")) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ one-element tuples

    /// <summary>`x = 1,` is `x = (1,)` (and `x = 1, 2,` is the tuple in parentheses, one item to a line), `a, = y` is `(a,) = y`, `for a, in b` is `for (a,) in b` and `return 1,` is `return (1,)`, as in black.</summary>
    void ParenthesiseSingleTuples()
    {
        int n = _p.Count;
        if (n < 3 || _p.Any(p => p.Standalone)) return;
        string first = _p[0].Text;
        var segments = new List<(int Start, int End)>();
        if (first is "return" or "del") segments.Add((1, n));
        else if (first == "for" && _p[^1].Kind == TokenKind.Operator && _p[^1].Text == ":")
        {
            int inAt = IndexAtDepth0("in", 1);
            if (inAt > 0) { segments.Add((1, inAt)); segments.Add((inAt + 1, n - 1)); }
        }
        else if (_p[0].Kind != TokenKind.Keyword || first is "None" or "True" or "False")
        {
            int depth = 0, from = 0;
            var found = new List<(int Start, int End)>();
            for (int i = 0; i < n; i++)
            {
                if (_p[i].IsOpen) depth++;
                else if (_p[i].IsClose) depth--;
                else if (depth == 0 && _p[i].Kind == TokenKind.Operator && !_p[i].KwargEquals && IsAssignOperator(_p[i].Text))
                {
                    found.Add((from, i));
                    from = i + 1;
                }
            }
            if (found.Count == 0) return;
            found.Add((from, n));
            segments.AddRange(found);
        }

        // from the right, so the positions still to be looked at stay valid
        foreach (var (start, end) in segments.OrderByDescending(x => x.Start))
        {
            if (end - start < 2) continue;
            int depth = 0, commas = 0;
            bool unsuitable = false;
            for (int i = start; i < end; i++)
            {
                if (_p[i].IsOpen) depth++;
                else if (_p[i].IsClose) depth--;
                else if (depth == 0 && _p[i].Kind == TokenKind.Operator && _p[i].Text == ",") commas++;
                else if (depth == 0 && _p[i].Kind == TokenKind.Keyword && _p[i].Text is "lambda" or "yield") unsuitable = true;
            }
            if (unsuitable || commas == 0 || (commas > 1 && !_options.MagicTrailingComma) || _p[end - 1].Kind != TokenKind.Operator || _p[end - 1].Text != ",") continue;   // a trailing comma makes the tuple a magic one
            _p.Insert(end, new Piece { Text = ")", Kind = TokenKind.Operator, IsClose = true });
            if (end + 1 < _p.Count && _p[end + 1].Kind == TokenKind.Operator && _p[end + 1].Text == ":") _p[end + 1].Space = false;
            var open = new Piece { Text = "(", Kind = TokenKind.Operator, IsOpen = true, Space = _p[start].Space };
            _p[start].Space = false;
            _p.Insert(start, open);
        }
    }

    int IndexAtDepth0(string keyword, int from)
    {
        int depth = 0;
        for (int i = from; i < _p.Count; i++)
        {
            if (_p[i].IsOpen) depth++;
            else if (_p[i].IsClose) depth--;
            else if (depth == 0 && _p[i].Kind == TokenKind.Keyword && _p[i].Text == keyword) return i;
        }
        return -1;
    }

    /// <summary>Removes the parentheses that enclose [start, end) as a whole, as often as they do.</summary>
    void StripRange(int start, int end, bool allowTuple, bool allowYield = false)
    {
        while (end - start >= 3 && _p[start].IsOpen && _p[start].Text == "(" && Closer(start) == end - 1 && Removable(start, end - 1, allowTuple, allowYield))
        {
            _p[start + 1].Space = _p[start].Space;
            // the comments of the brackets stay, on the tokens next to them
            _p[start + 1].Comments.InsertRange(0, _p[start].Comments);
            // the comment that ended the statement after these parentheses still ends it (so a second run writes what the first did)
            if (ReferenceEquals(_p[end - 1], _statementEnd) && _p[end - 1].Comments.Count > 0) _statementEnd = _p[end - 2];
            _p[end - 2].Comments.AddRange(_p[end - 1].Comments);
            _p.RemoveAt(end - 1);
            _p.RemoveAt(start);
            end -= 2;
        }
    }

    int Closer(int open)
    {
        int depth = 0;
        for (int i = open; i < _p.Count; i++)
        {
            if (_p[i].IsOpen) depth++;
            else if (_p[i].IsClose) { depth--; if (depth == 0) return i; }
        }
        return -1;
    }

    bool Removable(int open, int close, bool allowTuple, bool allowYield = false)
    {
        int depth = 0;
        for (int i = open + 1; i < close; i++)
        {
            var t = _p[i];
            if (t.Standalone) return false;
            if (t.IsOpen) { depth++; continue; }
            if (t.IsClose) { depth--; continue; }
            if (depth != 0) continue;
            if (t.Kind == TokenKind.Operator && t.Text is "," && !allowTuple) return false;
            if (t.Kind == TokenKind.Operator && t.Text == ":=") return false;
            // generators and yield need their parentheses, but the whole value of an assignment may be a yield
            if (t.Kind == TokenKind.Keyword && t.Text == "for") return false;
            if (t.Kind == TokenKind.Keyword && t.Text == "yield" && !(allowYield && i == open + 1)) return false;
            if (t.Kind == TokenKind.Keyword && t.Text == "async") return false;
        }
        var firstInner = _p[open + 1];
        if (firstInner.Kind == TokenKind.Keyword && firstInner.Text == "yield" && !allowYield) return false;
        if (firstInner.Kind == TokenKind.Operator && firstInner.Text is "*" or "**") return false;
        return true;
    }

    // ------------------------------------------------------------ analysis

    void Analyse()
    {
        var stack = new Stack<int>();
        int d = 0;
        for (int i = 0; i < _p.Count; i++)
        {
            var piece = _p[i];
            if (piece.IsOpen) { _depth[i] = d; stack.Push(i); d++; }
            else if (piece.IsClose)
            {
                d--;
                _depth[i] = d;
                if (stack.Count > 0) { int o = stack.Pop(); _p[o].Match = i; piece.Match = o; }
            }
            else _depth[i] = d;
        }

        for (int i = 0; i < _p.Count; i++)
        {
            if (!_p[i].IsOpen || _p[i].Match < 0) continue;
            int c = _p[i].Match;
            _atom[i] = i == 0 || !IsValueEnd(_p[i - 1]);

            // a comment on a line of its own inside the brackets makes them explode
            for (int k = i + 1; k < c; k++) if (_p[k].Standalone) { _magic[i] = true; break; }

            int last = LastReal(c - 1, i);
            if (last > i && IsComma(last))
            {
                int commas = 0;
                for (int k = i + 1; k < c; k++) if (_depth[k] == _depth[i] + 1 && IsComma(k)) commas++;
                // a one-element tuple `(1,)` and a one-element subscript `x[1,]` are not magic; `f(a,)`, `[1,]` and `{1,}` are
                if (commas > 1 || !((_p[i].Text == "(" && _atom[i]) || (_p[i].Text == "[" && !_atom[i]))) _magic[i] = true;
            }
        }

        // delimiters, tracked per depth: comprehension `for`, the `in` of a `for` target and lambda parameters change what a token means
        var sawFor = new Dictionary<int, bool>();
        var afterFor = new Dictionary<int, bool>();
        var inLambda = new Dictionary<int, bool>();
        for (int i = 0; i < _p.Count; i++)
        {
            var t = _p[i];
            if (t.Standalone) continue;
            int dep = _depth[i];
            string x = t.Text;
            bool op = t.Kind == TokenKind.Operator, kw = t.Kind == TokenKind.Keyword;
            if (t.IsOpen) { sawFor[dep + 1] = false; afterFor[dep + 1] = false; inLambda[dep + 1] = false; continue; }
            if (t.IsClose) continue;
            if (inLambda.GetValueOrDefault(dep) && op && x == ":") { inLambda[dep] = false; continue; }
            // the commas of a lambda's parameters are not items: `return lambda a, b: (...)` split there would be a tuple
            if (inLambda.GetValueOrDefault(dep)) { if (op && x == ",") _lambdaComma[i] = true; continue; }
            if (kw && x == "lambda") { inLambda[dep] = true; continue; }
            int prev = PreviousReal(i);
            if (prev < 0) continue;

            int priority = 0;
            if (op)
            {
                if (x == ",") continue;   // commas end their item; handled by IsComma
                if (!t.Unary)
                    priority = x switch
                    {
                        "|" => 9, "^" => 8, "&" => 7, "<<" or ">>" => 6, "+" or "-" => 5,
                        "*" or "/" or "//" or "%" or "@" => 4, "**" => 3,
                        "<" or ">" or "<=" or ">=" or "==" or "!=" or "<>" => Comparator,
                        _ => 0,
                    };
                if (x == "." && _p[prev].IsClose) priority = Dot;
            }
            else if (kw)
            {
                switch (x)
                {
                    case "and" or "or": priority = Logic; break;
                    case "for": priority = dep > 0 ? Comprehension : 0; if (dep > 0) { sawFor[dep] = true; afterFor[dep] = true; } break;
                    case "async" when NextReal(i) >= 0 && _p[NextReal(i)].Text == "for": priority = dep > 0 ? Comprehension : 0; break;
                    case "if": priority = dep > 0 && sawFor.GetValueOrDefault(dep) ? Comprehension : Ternary; break;
                    case "else": priority = Ternary; break;
                    case "in":
                        if (afterFor.GetValueOrDefault(dep)) { afterFor[dep] = false; break; }
                        priority = _p[prev].Text == "not" ? 0 : Comparator;
                        break;
                    case "not":
                        priority = NextReal(i) >= 0 && _p[NextReal(i)].Text == "in" && !afterFor.GetValueOrDefault(dep) ? Comparator : 0;
                        break;
                    case "is": priority = Comparator; break;
                }
            }
            else if (t.Kind == TokenKind.String && _p[prev].Kind == TokenKind.String) priority = StringConcat;
            _before[i] = priority;
        }
    }

    int PreviousReal(int i)
    {
        for (int k = i - 1; k >= 0; k--) if (!_p[k].Standalone) return k;
        return -1;
    }

    int NextReal(int i)
    {
        for (int k = i + 1; k < _p.Count; k++) if (!_p[k].Standalone) return k;
        return -1;
    }

    /// <summary>The last piece at or before <paramref name="i"/> (not before <paramref name="floor"/>) that is not a comment of its own.</summary>
    int LastReal(int i, int floor)
    {
        while (i > floor && _p[i].Standalone) i--;
        return i;
    }

    static bool IsValueEnd(Piece p) =>
        p.Kind is TokenKind.Name or TokenKind.Number or TokenKind.String
        || (p.Kind == TokenKind.Keyword && p.Text is "True" or "False" or "None")
        || (p.Kind == TokenKind.Operator && (p.IsClose || p.Text == "..."));

    bool IsComma(int i) => !_p[i].Standalone && _p[i].Kind == TokenKind.Operator && _p[i].Text == "," && !_lambdaComma[i];

    // ------------------------------------------------------------ text of a range

    string Join(int a, int b)
    {
        var sb = new StringBuilder();
        bool any = false;
        for (int i = a; i < b; i++)
        {
            if (_p[i].Standalone) continue;
            if (any && _p[i].Space) sb.Append(' ');
            sb.Append(_p[i].Text);
            any = true;
        }
        return sb.ToString();
    }

    /// <summary>The comments that follow tokens of the range, as they are written at the end of a line.</summary>
    string CommentsOf(int a, int b)
    {
        var all = new List<string>();
        for (int i = a; i < b; i++) if (!_p[i].Standalone) all.AddRange(_p[i].Comments);
        return string.Join("  ", all);
    }

    OutLine One(int indent, int a, int b) => new(indent, Join(a, b), CommentsOf(a, b));

    List<OutLine> Single(int indent, int a, int b) => new() { One(indent, a, b) };

    /// <summary>Black measures a line together with its comments.</summary>
    bool FitsRange(int indent, int a, int b)
    {
        string text = Join(a, b), comment = CommentsOf(a, b);
        return indent + text.Length + (comment.Length > 0 ? 2 + comment.Length : 0) <= Width;
    }

    bool FitsText(int indent, string text) => indent + text.Length <= Width;

    // ------------------------------------------------------------ output

    public List<OutLine> Wrap(int indent)
    {
        if (TypeIgnoreKeepsLine(indent)) return Single(indent, 0, _p.Count);
        ParenthesiseWithItems(indent);
        return ImportLine(0, _p.Count, indent) ?? SplitLine(0, _p.Count, indent, inside: false, forced: false);
    }

    /// <summary>
    /// A with of several context managers that does not fit, in code for Python 3.9 and later: the managers go in parentheses, which
    /// are then split as any brackets are (one manager to a line, a trailing comma after the last), as black does. One manager is
    /// split at its own brackets instead.
    /// </summary>
    void ParenthesiseWithItems(int indent)
    {
        if (!_options.ParenthesizedWith || _p.Count < 4 || _p.Any(p => p.Standalone)) return;
        int start = _p[0].Text == "with" ? 1 : _p[0].Text == "async" && _p[1].Text == "with" ? 2 : -1;
        int colon = _p.Count - 1;
        if (start < 0 || _p[colon].Kind != TokenKind.Operator || _p[colon].Text != ":" || FitsRange(indent, 0, _p.Count)) return;
        int commas = 0;
        for (int i = start; i < colon; i++) if (_depth[i] == 0 && IsComma(i)) commas++;
        if (commas == 0) return;
        _p.Insert(colon, new Piece { Text = ")", Kind = TokenKind.Operator, IsClose = true });
        var open = new Piece { Text = "(", Kind = TokenKind.Operator, IsOpen = true, Space = true };
        _p[start].Space = false;
        _p.Insert(start, open);
        int n = _p.Count;
        _depth = new int[n];
        _before = new int[n];
        _magic = new bool[n];
        _atom = new bool[n];
        _lambdaComma = new bool[n];
        Analyse();
    }

    /// <summary>
    /// Black leaves a line alone whose code fits and only its trailing `# type: ignore` (or `# type: ignore[...]`) makes it too long:
    /// splitting it would move the comment away from the code it silences. Other comments, `# type: int` and `# noqa` among them, count.
    /// </summary>
    bool TypeIgnoreKeepsLine(int indent)
    {
        int last = LastReal(_p.Count - 1, -1);
        if (last < 0 || _p[last].Comments.Count == 0 || !_p[last].Comments[^1].StartsWith("# type: ignore", StringComparison.Ordinal)) return false;
        for (int i = 0; i < _p.Count; i++)
            if (_p[i].Standalone || i != last && _p[i].Comments.Count > 0 || _p[i].IsOpen && HasMagic(i, _p.Count)) return false;
        return FitsText(indent, Join(0, _p.Count));
    }

    int BaseDepth(int a, int b)
    {
        int min = int.MaxValue;
        for (int i = a; i < b; i++) min = Math.Min(min, _depth[i]);
        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>Bracket pairs that start and end inside the range and are not nested in another pair of it.</summary>
    List<int> TopGroups(int a, int b)
    {
        int baseDepth = BaseDepth(a, b);
        var groups = new List<int>();
        for (int i = a; i < b; i++)
            if (_p[i].IsOpen && _depth[i] == baseDepth && _p[i].Match > i && _p[i].Match < b) { groups.Add(i); i = _p[i].Match; }
        return groups;
    }

    bool HasMagic(int a, int b)
    {
        for (int i = a; i < b; i++) if (_magic[i] && _p[i].Match < b) return true;
        return false;
    }

    bool ContainsMagic(int open) => HasMagic(open, _p[open].Match + 1);

    bool HasBaseStandalone(int a, int b)
    {
        int baseDepth = BaseDepth(a, b);
        for (int i = a; i < b; i++) if (_p[i].Standalone && _depth[i] == baseDepth) return true;
        return false;
    }

    List<OutLine> SplitLine(int a, int b, int indent, bool inside, bool forced)
    {
        if (FitsRange(indent, a, b) && !HasMagic(a, b) && !forced && !HasBaseStandalone(a, b)) return Single(indent, a, b);

        if (inside)
        {
            var split = DelimiterSplit(a, b, indent);
            if (split != null) return split;
        }
        if (HasBaseStandalone(a, b)) return StandaloneSplit(a, b, indent);
        return RightHandSplit(a, b, indent, inside) ?? Single(indent, a, b);
    }

    /// <summary>A comment on a line of its own ends the line before it and starts a new one after it.</summary>
    List<OutLine> StandaloneSplit(int a, int b, int indent)
    {
        int baseDepth = BaseDepth(a, b);
        var lines = new List<OutLine>();
        int start = a;
        for (int i = a; i < b; i++)
        {
            if (!(_p[i].Standalone && _depth[i] == baseDepth)) continue;
            if (HasReal(start, i)) lines.AddRange(SplitLine(start, i, indent, inside: true, forced: false));
            lines.Add(new OutLine(indent, _p[i].Text, "", IsComment: true));
            start = i + 1;
        }
        if (HasReal(start, b)) lines.AddRange(SplitLine(start, b, indent, inside: true, forced: false));
        return lines;
    }

    bool HasReal(int a, int b)
    {
        for (int i = a; i < b; i++) if (!_p[i].Standalone) return true;
        return false;
    }

    static void AddToLastCode(List<OutLine> lines, string suffix)
    {
        for (int k = lines.Count - 1; k >= 0; k--)
        {
            if (lines[k].IsComment) continue;
            lines[k] = lines[k] with { Text = lines[k].Text + suffix };
            return;
        }
    }

    // ---------------------------------------------------------- one item per line

    List<OutLine>? DelimiterSplit(int a, int b, int indent)
    {
        int baseDepth = BaseDepth(a, b);
        int max = 0, count = 0;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth || _p[i].Standalone) continue;
            int pr = IsComma(i) ? Comma : _before[i];
            if (pr > max) { max = pr; count = 1; } else if (pr == max && pr > 0) count++;
        }
        if (max == 0) return null;
        if (max == Dot && count < 2) return null;

        var items = new List<(int Start, int End)>();
        int start = a;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth || _p[i].Standalone) continue;
            if (max == Comma && IsComma(i)) { items.Add((start, i + 1)); start = i + 1; }
            else if (max != Comma && _before[i] == max && i > start && HasReal(start, i)) { items.Add((start, i)); start = i; }
        }
        if (start < b) items.Add((start, b));
        if (items.Count == 0) return null;

        var lines = new List<OutLine>();
        for (int k = 0; k < items.Count; k++)
        {
            // the comma that ends an item is not part of what is laid out; it is put back after the last line of the item
            int itemEnd = items[k].End;
            bool endsWithComma = max == Comma && IsComma(itemEnd - 1);
            int commaAt = itemEnd - 1;
            if (endsWithComma) itemEnd--;
            if (itemEnd <= items[k].Start || !HasReal(items[k].Start, itemEnd))
            {
                // an item that is only its comma
                if (endsWithComma) AddToLastCode(lines, ",");
                if (endsWithComma && _p[commaAt].Comments.Count > 0) lines[^1] = lines[^1] with { Comment = JoinComments(lines[^1].Comment, string.Join("  ", _p[commaAt].Comments)) };
                // comments of their own in an item without code
                for (int i = items[k].Start; i < items[k].End; i++) if (_p[i].Standalone) lines.Add(new OutLine(indent, _p[i].Text, "", IsComment: true));
                continue;
            }

            // an item that is the whole range cannot be split by delimiters again (that would not end)
            bool whole = items[k].Start == a && itemEnd == b;
            var part = SplitLine(items[k].Start, itemEnd, indent, inside: !whole, forced: false);
            if (endsWithComma)
            {
                AddToLastCode(part, ",");
                var lastCode = part.FindLastIndex(l => !l.IsComment);
                if (lastCode >= 0 && _p[commaAt].Comments.Count > 0)
                    part[lastCode] = part[lastCode] with { Comment = JoinComments(part[lastCode].Comment, string.Join("  ", _p[commaAt].Comments)) };
            }
            lines.AddRange(part);
        }

        int lastReal = LastReal(b - 1, a - 1);
        if (max == Comma && lastReal >= a && !IsComma(lastReal)) AddToLastCode(lines, ",");
        return lines;
    }

    static string JoinComments(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "  " + b;

    // ---------------------------------------------------------- split at a bracket pair

    List<OutLine>? RightHandSplit(int a, int b, int indent, bool inside)
    {
        // the value of an assignment, a return, a condition: optional parentheses
        _preferred = -1;
        if (!inside && a == 0 && b == _p.Count)
        {
            var slot = FindSlot(a, b);
            if (slot != null)
            {
                var (wrapped, giveUp) = WrapSlot(a, b, indent, slot.Value);
                if (wrapped != null) return wrapped;
                if (giveUp) return null;
            }
        }

        var groups = TopGroups(a, b).Where(g => _p[g].Match > g + 1).ToList();
        if (groups.Count == 0) return null;

        int chosen = -1;
        if (_preferred >= 0) { chosen = _preferred; _preferred = -1; }
        bool isDef = a == 0 && (_p[0].Text == "def" || (_p[0].Text == "async" && b > 1 && _p[1].Text == "def"));
        if (chosen >= 0) { }
        else if (isDef && !inside) chosen = groups[0];
        else if (HasMagic(a, b))
        {
            for (int k = groups.Count - 1; k >= 0 && chosen < 0; k--)
                if (ContainsMagic(groups[k])) chosen = groups[k];
        }
        if (chosen < 0)
        {
            for (int k = groups.Count - 1; k >= 0; k--)
            {
                if (FitsRange(indent, a, groups[k] + 1)) { chosen = groups[k]; break; }
            }
            if (chosen < 0) chosen = groups[^1];
        }
        return SplitAt(a, b, indent, chosen, isDef && !inside && chosen == groups[0]);
    }

    List<OutLine>? SplitAt(int a, int b, int indent, int open, bool defParameters)
    {
        int close = _p[open].Match;
        var head = SplitLine(a, open + 1, indent, inside: false, forced: false);
        var body = BodyOf(open, close, indent + Indent, defParameters);
        var tail = SplitLine(close, b, indent, inside: false, forced: false);

        var lines = new List<OutLine>(head);
        lines.AddRange(body);
        lines.AddRange(tail);
        return lines;
    }

    List<OutLine> BodyOf(int open, int close, int indent, bool defParameters)
    {
        int a = open + 1;
        int lastReal = LastReal(close - 1, open);
        bool trailingComma = lastReal > open && IsComma(lastReal);
        int baseDepth = _depth[open] + 1;
        bool hasCommas = false;
        for (int i = a; i < close; i++) if (_depth[i] == baseDepth && IsComma(i) && !(trailingComma && i == lastReal)) { hasCommas = true; break; }
        bool maxIsComma = hasCommas && NoStrongerThanComma(a, close, baseDepth);
        bool forced = maxIsComma && (trailingComma || _atom[open] || IsImportParen(open));

        var lines = SplitLine(a, close, indent, inside: true, forced);
        // a def with a single parameter gets the trailing comma too
        if (defParameters && !hasCommas && !trailingComma && lines.Count > 0) AddToLastCode(lines, ",");
        return lines;
    }

    bool NoStrongerThanComma(int a, int b, int baseDepth)
    {
        for (int i = a; i < b; i++) if (_depth[i] == baseDepth && !_p[i].Standalone && _before[i] > Comma) return false;
        return true;
    }

    bool IsImportParen(int open) => open > 0 && _p[open - 1].Text == "import";

    // ---------------------------------------------------------- invisible parentheses

    enum SlotAction { Omit, Wrap }
    readonly record struct Slot(int Start, int End, SlotAction Wrap);

    /// <summary>The expression after `=`, `return`, `if`/`elif`/`while` or `in` of a `for`, when the statement ends right after it.</summary>
    Slot? FindSlot(int a, int b)
    {
        string first = _p[0].Text;
        int s = -1, e = b;
        bool colonEnd = b > 0 && _p[b - 1].Kind == TokenKind.Operator && _p[b - 1].Text == ":" && _depth[b - 1] == 0;
        if (first == "return" && b > 1) s = 1;
        else if ((first is "if" or "elif" or "while") && colonEnd && b > 2) { s = 1; e = b - 1; }
        else if (first == "for" && colonEnd)
        {
            for (int i = 1; i < b; i++) if (_depth[i] == 0 && _p[i].Kind == TokenKind.Keyword && _p[i].Text == "in") { s = i + 1; e = b - 1; break; }
        }
        else if (first is not ("def" or "class" or "from" or "import" or "with" or "assert" or "del" or "raise" or "lambda" or "async" or "try" or "except" or "else" or "finally"))
        {
            for (int i = b - 1; i > 0; i--)
                if (_depth[i] == 0 && _p[i].Kind == TokenKind.Operator && !_p[i].KwargEquals && IsAssignOperator(_p[i].Text)) { s = i + 1; break; }
        }
        if (s < 0 || s >= e) return null;

        // already parenthesised as a whole: the brackets are split like any others
        if (_p[s].IsOpen && _p[s].Match == e - 1) return new Slot(s, e, SlotAction.Omit);
        return new Slot(s, e, SlotAction.Wrap);
    }

    static bool IsAssignOperator(string t) => t is "=" or "+=" or "-=" or "*=" or "/=" or "//=" or "%=" or "**=" or ">>=" or "<<=" or "&=" or "|=" or "^=" or "@=";

    /// <returns>The wrapped lines; or (null, false) to split at the value's own brackets; or (null, true) to leave the line alone.</returns>
    (List<OutLine>? Lines, bool GiveUp) WrapSlot(int a, int b, int indent, Slot slot)
    {
        if (slot.Wrap == SlotAction.Omit) return (null, false);
        int s = slot.Start, e = slot.End;

        // the delimiters of the value decide whether the brackets of a call at its end are enough
        int baseDepth = BaseDepth(s, e), delimiters = 0;
        for (int i = s; i < e; i++)
            if (_depth[i] == baseDepth && !_p[i].Standalone && (IsComma(i) || _before[i] > 0)) delimiters++;
        var groups = TopGroups(s, e).Where(g => _p[g].Match > g + 1).ToList();
        bool endsWithBracket = _p[LastReal(e - 1, s - 1)].IsClose;

        // splitting at the value's own brackets works when the head up to one of them fits and only short trailers follow it
        if (groups.Count > 0 && (delimiters == 0 || (delimiters == 1 && endsWithBracket)))
        {
            for (int k = groups.Count - 1; k >= 0; k--)
            {
                int close = _p[groups[k]].Match;
                if (FitsRange(indent, a, groups[k] + 1) && OnlyTrailersAfter(close, e, baseDepth) && FitsRange(indent, close, b)) return (null, false);
            }
        }

        // a value that starts with a bracket and has one operator: `[...] + [...]` is split at the first bracket when the rest fits after it
        if (delimiters == 1 && groups.Count > 0 && groups[0] == FirstReal(s, e) && _p[groups[0]].Match < e - 1)
        {
            int close = _p[groups[0]].Match;
            if (FitsRange(indent, a, groups[0] + 1) && FitsRange(indent, close, b))
            {
                _preferred = groups[0];
                return (null, false);
            }
        }

        // `(a, b, c) = value`: when the rest of the line fits after the target's closing bracket, the target is split instead
        if (s > 1 && _p[s - 1].Kind == TokenKind.Operator && IsAssignOperator(_p[s - 1].Text))
        {
            var targetGroups = TopGroups(a, s - 1).Where(g => _p[g].Match > g + 1).ToList();
            if (targetGroups.Count > 0 && FitsRange(indent, _p[targetGroups[^1]].Match, b))
            {
                _preferred = targetGroups[^1];
                return (null, false);
            }
        }

        // wrapping only helps when the value fits in the parentheses or can be split further
        // the comment that ends the statement ends it after the closing parenthesis, as black has it (its last leaf is that parenthesis):
        // not when the parentheses wrap one token (`x = False  # ...` keeps the comment on False, a type comment aside), nor when it was
        // inside parentheses the source had (`(a +  # x` ... `)`); measured without it, the value may now fit inside
        string tail = "";
        int last = LastReal(e - 1, s - 1);
        bool oneToken = FirstReal(s, e) == last;
        if (e == b && last >= s && _p[last].Comments.Count > 0 && ReferenceEquals(_p[last], _statementEnd)
            && (!oneToken || _p[last].Comments[^1].StartsWith("# type:", StringComparison.Ordinal)))
        {
            tail = string.Join("  ", _p[last].Comments);
            _p[last].Comments.Clear();
        }

        bool fitsInside = FitsRange(indent + Indent, s, e);
        bool canSplit = groups.Count > 0 || delimiters > 0;
        if (!fitsInside && !canSplit)
        {
            if (tail.Length > 0) _p[last].Comments.Add(tail);
            return (null, true);
        }

        var lines = new List<OutLine> { new(indent, Join(a, s) + " (", CommentsOf(a, s)) };
        bool forced = delimiters > 0 && MaxIsComma(s, e, baseDepth);
        lines.AddRange(SplitLine(s, e, indent + Indent, inside: true, forced));
        string closing = CommentsOf(e, b);
        lines.Add(new OutLine(indent, ")" + (e < b ? (FirstReal(e, b) is int f && _p[f].Space ? " " : "") + Join(e, b) : ""), closing.Length > 0 && tail.Length > 0 ? closing + "  " + tail : closing + tail));
        return (lines, false);
    }

    /// <summary>Calls, subscripts and attribute accesses only: what black leaves on the closing line of a split.</summary>
    bool OnlyTrailersAfter(int close, int end, int baseDepth)
    {
        for (int i = close + 1; i < end; i++)
        {
            var t = _p[i];
            if (t.Standalone || _depth[i] != baseDepth) continue;
            if (t.IsOpen && _atom[i]) return false;
            if (!(t.IsOpen || t.IsClose || t.Text == "." || t.Kind == TokenKind.Name)) return false;
        }
        return true;
    }

    int? FirstReal(int a, int b)
    {
        for (int i = a; i < b; i++) if (!_p[i].Standalone) return i;
        return null;
    }

    bool MaxIsComma(int a, int b, int baseDepth)
    {
        bool comma = false;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth || _p[i].Standalone) continue;
            if (IsComma(i)) comma = true;
            else if (_before[i] > Comma) return false;
        }
        return comma;
    }

    // ---------------------------------------------------------- imports

    /// <summary>`from m import a, b`: one line when it fits (the brackets go), else the names one per line in brackets.</summary>
    List<OutLine>? ImportLine(int a, int b, int indent)
    {
        if (b <= 3 || _p[0].Text != "from") return null;
        int imp = -1;
        for (int i = 1; i < b; i++) if (_depth[i] == 0 && _p[i].Kind == TokenKind.Keyword && _p[i].Text == "import") { imp = i; break; }
        if (imp < 0 || imp + 1 >= b) return null;

        int s = imp + 1, e = b;
        bool paren = _p[s].IsOpen && _p[s].Text == "(" && _p[s].Match == b - 1;
        if (paren) { s++; e = b - 1; }
        if (s >= e || _p[s].Text == "*") return null;

        int lastReal = LastReal(e - 1, s - 1);
        bool magic = paren && IsComma(lastReal);
        bool standalone = false;
        for (int i = s; i < e; i++) if (_p[i].Standalone) standalone = true;

        // the names, each with its own comments
        var items = new List<(string Text, string Comment, List<string> Before)>();
        var pending = new List<string>();
        int start = s;
        void Flush(int end, int commaAt)
        {
            if (HasReal(start, end))
            {
                string comment = CommentsOf(start, end);
                if (commaAt >= 0 && _p[commaAt].Comments.Count > 0) comment = JoinComments(comment, string.Join("  ", _p[commaAt].Comments));
                items.Add((Join(start, end), comment, new List<string>(pending)));
                pending.Clear();
            }
            for (int i = start; i < end; i++) if (_p[i].Standalone && !pending.Contains(_p[i].Text) && !items.Any(it => it.Before.Contains(_p[i].Text))) pending.Add(_p[i].Text);
        }
        for (int i = s; i < e; i++)
        {
            if (_p[i].Standalone) { if (!HasReal(start, i)) pending.Add(_p[i].Text); continue; }
            if (IsComma(i)) { Flush(i, i); start = i + 1; }
        }
        if (HasReal(start, e)) Flush(e, -1);
        if (items.Count == 0) return null;

        string module = Join(a, imp);
        string headComment = CommentsOf(a, imp + (paren ? 2 : 1)) ;
        string single = $"{module} import {string.Join(", ", items.Select(i => i.Text))}";
        string allComments = string.Join("  ", items.Select(i => i.Comment).Where(c => c.Length > 0));
        if (!magic && !standalone && pending.Count == 0 && FitsText(indent, single + (allComments.Length > 0 ? "  " + allComments : "")))
            return new List<OutLine> { new(indent, single, JoinComments(headComment, allComments)) };

        var lines = new List<OutLine> { new(indent, $"{module} import (", headComment) };
        foreach (var it in items)
        {
            foreach (var c in it.Before) lines.Add(new OutLine(indent + Indent, c, "", IsComment: true));
            lines.Add(new OutLine(indent + Indent, it.Text + ",", it.Comment));
        }
        foreach (var c in pending) lines.Add(new OutLine(indent + Indent, c, "", IsComment: true));
        lines.Add(new OutLine(indent, ")"));
        return lines;
    }
}
