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
    public bool Break;                      // the source has a line break before it
    public int Row = -1;                    // its line in the source, counted from the statement's first (-1: added by the formatter)
    public bool Invisible;                  // an optional parenthesis, as black has them: written only once a split uses it
    public bool Visible;                    // an optional parenthesis a split has used

    public string Shown => Invisible && !Visible ? "" : Text;
}

/// <summary>One output line: its indentation, the code, and the comments that end it.</summary>
internal readonly record struct OutLine(int Indent, string Text, string Comment = "", bool IsComment = false);

/// <summary>
/// Splits a logical line that is too long, or has a magic trailing comma or a comment of its own inside brackets, as black 24-26 does
/// (a port of its <c>transform_line</c>): optional parentheses, invisible until a split uses them, around the value of an assignment, a
/// return, a condition, an annotation and every conditional expression; a right-hand split at the last bracket pair, leaving out
/// optional parentheses and trailing brackets when the rest reads well without them (<c>right_hand_split_with_omits</c>,
/// <c>can_omit_invisible_parens</c>, <c>_prefer_split_rhs_oop_over_rhs</c>) and taking a second opinion with them when the first line
/// stays too long; the parameters of a <c>def</c> split at its first bracket pair; inside brackets, one item per line at the strongest
/// delimiter (commas, then boolean operators, comparisons, arithmetic, dots of a call chain) with a trailing comma after the items.
/// Redundant parentheses are made invisible or removed first. Comments stay with the token they follow; comments on their own lines
/// stay between the items.
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
    bool[] _magic = Array.Empty<bool>();     // an opening bracket whose group has a magic trailing comma
    bool[] _atom = Array.Empty<bool>();      // an opening bracket that is not a call, subscript or parameter list
    bool[] _notDelimiter = Array.Empty<bool>();  // a comma between a lambda and its colon, or in the target of a comprehension's for
    bool[] _lambdaParam = Array.Empty<bool>();   // a token between a lambda and its colon
    bool[] _annotation = Array.Empty<bool>();    // a token of a parameter's or the return annotation of a def

    public LineWrapper(List<Piece> pieces, FormatOptions options)
    {
        _p = pieces;
        _options = options;
        if (!options.MagicTrailingComma) RemoveTrailingCommas();
        RemoveRedundantParentheses();
        ParenthesiseSingleTuples();
        AddOptionalParentheses();
        Reanalyse();
    }

    void Reanalyse()
    {
        int n = _p.Count;
        _depth = new int[n];
        _before = new int[n];
        _magic = new bool[n];
        _atom = new bool[n];
        _notDelimiter = new bool[n];
        _lambdaParam = new bool[n];
        _annotation = new bool[n];
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

    /// <summary>The parentheses around with-items and the operand of an await; those around values are made optional (<see cref="AddOptionalParentheses"/>).</summary>
    void RemoveRedundantParentheses()
    {
        if (_p.Count < 3) return;
        string first = _p[0].Text;
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
    /// gets them back from the wrapping when the Pythons the file is for allow them (<see cref="ParenthesiseWithItems"/>).
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

    /// <summary>
    /// The parentheses that enclose [start, end) as a whole, as often as they do: removed, or with <paramref name="optional"/> the
    /// innermost pair kept as black's optional parentheses (invisible, with their comments). Returns the new end.
    /// </summary>
    int StripRange(int start, int end, bool allowTuple, bool allowYield = false, bool optional = false, bool allowStar = false, bool keepMultiline = false)
    {
        bool Strippable(int s, int e) => e - s >= 3 && _p[s].IsOpen && _p[s].Text == "(" && !_p[s].Invisible && Closer(s) == e - 1
                                         && Removable(s, e - 1, allowTuple, allowYield, allowStar) && !(keepMultiline && Multiline(s, e - 1));
        while (Strippable(start, end))
        {
            if (optional && !Strippable(start + 1, end - 1))
            {
                _p[start].Invisible = true;
                _p[end - 1].Invisible = true;
                if (end - start == 3) KeepCommentsOnSingleToken(start + 1, end - 1);
                return end;
            }
            _p[start + 1].Space = _p[start].Space;
            // the comments of the brackets stay, on the tokens next to them
            _p[start + 1].Comments.InsertRange(0, _p[start].Comments);
            _p[end - 2].Comments.AddRange(_p[end - 1].Comments);
            _p.RemoveAt(end - 1);
            _p.RemoveAt(start);
            end -= 2;
        }
        return end;
    }

    /// <summary>Black hangs a comment that follows optional parentheses around one token on that token, a type comment aside.</summary>
    void KeepCommentsOnSingleToken(int token, int close)
    {
        if (_p.Any(p => p.Standalone)) return;
        var moved = _p[close].Comments.Where(c => !c.StartsWith("# type:", StringComparison.Ordinal)).ToList();
        _p[close].Comments.RemoveAll(c => !c.StartsWith("# type:", StringComparison.Ordinal));
        _p[token].Comments.AddRange(moved);
    }

    /// <summary>A bracket pair written over several lines in the source.</summary>
    bool Multiline(int open, int close)
    {
        for (int i = open + 1; i <= close; i++) if (_p[i].Break || _p[i].Standalone) return true;
        return false;
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

    bool Removable(int open, int close, bool allowTuple, bool allowYield = false, bool allowStar = false)
    {
        int depth = 0, commas = 0, lastComma = -1;
        for (int i = open + 1; i < close; i++)
        {
            var t = _p[i];
            if (t.Standalone) return false;
            if (t.IsOpen) { depth++; continue; }
            if (t.IsClose) { depth--; continue; }
            if (depth != 0) continue;
            if (t.Kind == TokenKind.Operator && t.Text is ",") { if (!allowTuple) return false; commas++; lastComma = i; }
            if (t.Kind == TokenKind.Operator && t.Text == ":=") return false;
            // generators and yield need their parentheses, but the whole value of an assignment may be a yield
            if (t.Kind == TokenKind.Keyword && t.Text == "for") return false;
            if (t.Kind == TokenKind.Keyword && t.Text == "yield" && !(allowYield && i == open + 1)) return false;
            if (t.Kind == TokenKind.Keyword && t.Text == "async") return false;
        }
        // a one-element tuple keeps its parentheses
        if (commas == 1 && lastComma == close - 1) return false;
        var firstInner = _p[open + 1];
        if (firstInner.Kind == TokenKind.Keyword && firstInner.Text == "yield" && !allowYield) return false;
        if (firstInner.Kind == TokenKind.Operator && firstInner.Text is "*" or "**" && !allowStar) return false;
        return true;
    }

    // ------------------------------------------------------------ optional parentheses

    readonly record struct Slot(int Start, int End, bool AllowTuple = false, bool AllowYield = false, bool Target = false);

    /// <summary>
    /// Black's invisible parentheses: around the value after `return`, `if`/`elif`/`while`, `for ... in`, `del`, `assert` (and after its
    /// comma), `except`, `=` and the other assignments, the annotation of an assignment, of a parameter and the return annotation of a
    /// def, a tuple target of an assignment, and every conditional expression not already alone in parentheses. Redundant parentheses
    /// there become such optional ones.
    /// </summary>
    void AddOptionalParentheses()
    {
        foreach (var slot in StatementSlots().OrderByDescending(s => s.Start))
        {
            int s = slot.Start;
            int e = StripRange(s, slot.End, slot.AllowTuple, slot.AllowYield, optional: true, allowStar: slot.Target);
            if (_p[s].IsOpen && _p[s].Text == "(" && Closer(s) == e - 1) continue;
            if (slot.Target && !HasCommaAtDepth0(s, e)) continue;
            InsertPair(s, e);
        }
        ParenthesiseConditionals();
    }

    bool HasCommaAtDepth0(int s, int e)
    {
        int depth = 0;
        for (int i = s; i < e; i++)
        {
            if (_p[i].IsOpen) depth++;
            else if (_p[i].IsClose) depth--;
            else if (depth == 0 && IsOp(i, ",")) return true;
        }
        return false;
    }

    /// <summary>Optional parentheses around [s, e); the comments that follow its last token then follow them (black attaches a comment to the last leaf).</summary>
    void InsertPair(int s, int e)
    {
        var close = new Piece { Text = ")", Kind = TokenKind.Operator, IsClose = true, Invisible = true };
        close.Comments.AddRange(_p[e - 1].Comments);
        _p[e - 1].Comments.Clear();
        _p.Insert(e, close);
        var open = new Piece { Text = "(", Kind = TokenKind.Operator, IsOpen = true, Invisible = true, Space = _p[s].Space };
        _p[s].Space = false;
        _p.Insert(s, open);
        if (e - s == 1) KeepCommentsOnSingleToken(s + 1, s + 2);
    }

    bool IsOp(int i, string text) => i >= 0 && i < _p.Count && !_p[i].Standalone && _p[i].Kind == TokenKind.Operator && _p[i].Text == text;
    bool IsKw(int i, string text) => i >= 0 && i < _p.Count && _p[i].Kind == TokenKind.Keyword && _p[i].Text == text;

    int[] Depths()
    {
        var dep = new int[_p.Count];
        int d = 0;
        for (int i = 0; i < _p.Count; i++)
        {
            if (_p[i].IsOpen) { dep[i] = d; d++; }
            else if (_p[i].IsClose) { d--; dep[i] = d; }
            else dep[i] = d;
        }
        return dep;
    }

    List<Slot> StatementSlots()
    {
        var slots = new List<Slot>();
        int n = _p.Count;
        if (n < 2 || _p[0].Standalone) return slots;
        var dep = Depths();
        bool colonEnd = IsOp(n - 1, ":") && dep[n - 1] == 0;
        int lead = IsKw(0, "async") ? 1 : 0;
        bool kw = _p[lead].Kind == TokenKind.Keyword;
        string head = _p[lead].Text;

        void Add(int s, int e, bool tuple = false, bool yield = false, bool target = false)
        {
            while (s < e && _p[s].Standalone) s++;
            while (e > s && _p[e - 1].Standalone) e--;
            if (s < e) slots.Add(new Slot(s, e, tuple, yield, target));
        }
        int AtDepth0(Func<int, bool> test, int from)
        {
            for (int i = from; i < n; i++) if (dep[i] == 0 && test(i)) return i;
            return -1;
        }

        if (!kw)
        {
            if (head is "match" or "case" or "type" && colonEnd || head == "type" && n > 1 && _p[1].Kind == TokenKind.Name) return slots;
            AssignmentSlots(dep, Add);
            return slots;
        }
        switch (head)
        {
            case "return": Add(1, n); break;
            case "if" or "elif" or "while" when colonEnd: Add(1, n - 1); break;
            case "for" when colonEnd:
                int inAt = AtDepth0(i => IsKw(i, "in"), lead + 1);
                if (inAt > 0) { Add(lead + 1, inAt, tuple: true); Add(inAt + 1, n - 1); }
                break;
            case "del": Add(1, n); break;
            case "assert":
                int comma = AtDepth0(i => IsOp(i, ","), 1);
                if (comma > 0) { Add(1, comma); Add(comma + 1, n); } else Add(1, n);
                break;
            case "except" when colonEnd:
                int from = IsOp(1, "*") ? 2 : 1;
                int asAt = AtDepth0(i => IsKw(i, "as"), from);
                Add(from, asAt > 0 ? asAt : n - 1);
                break;
            case "def":
                foreach (var (s, e) in AnnotationRanges(dep, lead)) Add(s, e);
                break;
            case "None" or "True" or "False" or "not" or "await" or "lambda":
                AssignmentSlots(dep, Add);
                break;
        }
        return slots;
    }

    /// <summary>`x = value`, `x += value`, `a = b = value`, `x: T = value`; a target that is a tuple.</summary>
    void AssignmentSlots(int[] dep, Action<int, int, bool, bool, bool> add)
    {
        var ops = new List<int>();
        bool lambda = false, annotated = false;
        for (int i = 0; i < _p.Count; i++)
        {
            if (dep[i] != 0 || _p[i].Standalone) continue;
            if (IsKw(i, "lambda")) lambda = true;
            else if (IsOp(i, ":"))
            {
                if (lambda) lambda = false;
                else if (ops.Count == 0) { ops.Add(i); annotated = true; }
            }
            else if (_p[i].Kind == TokenKind.Operator && !_p[i].KwargEquals && IsAssignOperator(_p[i].Text)) ops.Add(i);
        }
        if (ops.Count == 0) return;
        if (!annotated) add(0, ops[0], true, false, true);
        for (int k = 0; k < ops.Count; k++) add(ops[k] + 1, k + 1 < ops.Count ? ops[k + 1] : _p.Count, false, !annotated, false);
    }

    /// <summary>The annotations of a def's parameters (`name: annotation`, up to a default) and its return annotation.</summary>
    List<(int Start, int End)> AnnotationRanges(int[] dep, int lead)
    {
        var ranges = new List<(int, int)>();
        int n = _p.Count;
        int open = lead + 2;
        if (open < n && _p[open].IsOpen && _p[open].Text == "[") open = Closer(open) + 1;
        if (open <= 0 || open >= n || !_p[open].IsOpen || _p[open].Text != "(") return ranges;
        int close = Closer(open);
        if (close < 0) return ranges;

        void Parameter(int a, int b)
        {
            int k = a;
            while (k < b && _p[k].Standalone) k++;
            if (k < b && _p[k].Kind == TokenKind.Operator && _p[k].Text is "*" or "**") k++;
            if (!(k + 1 < b && _p[k].Kind == TokenKind.Name && IsOp(k + 1, ":"))) return;
            int s = k + 2, e = b;
            for (int i = s; i < b; i++) if (dep[i] == dep[open] + 1 && IsOp(i, "=")) { e = i; break; }
            while (e > s && _p[e - 1].Standalone) e--;
            if (s < e) ranges.Add((s, e));
        }
        int start = open + 1;
        for (int j = open + 1; j <= close; j++)
        {
            if (j == close || dep[j] == dep[open] + 1 && IsOp(j, ",")) { Parameter(start, j); start = j + 1; }
        }
        for (int j = close + 1; j < n; j++)
            if (dep[j] == 0 && IsOp(j, "->")) { if (IsOp(n - 1, ":") && j + 1 < n - 1) ranges.Add((j + 1, n - 1)); break; }
        return ranges;
    }

    /// <summary>`a if b else c` gets optional parentheses wherever it is not alone in parentheses already (black's <c>visit_test</c>).</summary>
    void ParenthesiseConditionals()
    {
        for (int i = _p.Count - 1; i > 0; i--)
        {
            if (!IsKw(i, "if")) continue;
            var extent = ConditionalExtent(i);
            if (extent == null) continue;
            var (s, e) = extent.Value;
            // alone in parentheses (comments of their own around it aside): black sees the bracket as its neighbour
            int before = PreviousReal(s), after = e;
            while (after < _p.Count && _p[after].Standalone) after++;
            if (before >= 0 && _p[before].IsOpen && _p[before].Text == "(" && Closer(before) == after) continue;
            InsertPair(s, e);
            i++;   // the `if` moved one on; the token before it is now at i
        }
    }

    (int Start, int End)? ConditionalExtent(int at)
    {
        var dep = Depths();
        int d = dep[at], n = _p.Count;
        bool statementLead = (_p[0].Text is "match" or "case") && IsOp(n - 1, ":");

        // its else, at the same depth, before anything that would end it
        int elseAt = -1;
        for (int j = at + 1; j < n; j++)
        {
            if (_p[j].Standalone || dep[j] > d) continue;
            if (dep[j] < d || EndsConditional(j)) break;
            if (IsKw(j, "else")) { elseAt = j; break; }
        }
        if (elseAt < 0) return null;

        int s = 0;
        for (int k = at - 1; k >= 0; k--)
        {
            if (_p[k].Standalone || dep[k] > d) continue;
            if (dep[k] < d || StartsConditional(k, dep) || (k == 0 && statementLead)) { s = k + 1; break; }
        }
        int e = n;
        for (int j = elseAt + 1; j < n; j++)
        {
            if (_p[j].Standalone || dep[j] > d) continue;
            if (dep[j] < d) { e = j; break; }
            if (IsKw(j, "lambda"))
            {
                // a lambda in the else branch: its colon is its own
                while (j + 1 < n && !(dep[j + 1] == d && IsOp(j + 1, ":"))) j++;
                j++;
                continue;
            }
            if (EndsConditional(j)) { e = j; break; }
        }
        while (s < e && _p[s].Standalone) s++;
        while (e > s && _p[e - 1].Standalone) e--;
        return s < at && e > elseAt + 1 ? (s, e) : null;
    }

    bool StartsConditional(int k, int[] dep)
    {
        var t = _p[k];
        if (t.Kind == TokenKind.Operator)
            return t.Text is "," or ":" or "->" or ":=" || IsAssignOperator(t.Text) || t.Unary && t.Text is "*" or "**";
        if (t.Kind != TokenKind.Keyword) return false;
        if (t.Text == "in")
        {
            // the in of a for, not a comparison
            for (int j = k - 1; j >= 0; j--)
            {
                if (dep[j] > dep[k]) continue;
                if (dep[j] < dep[k]) return false;
                if (IsKw(j, "for")) return true;
                if (IsOp(j, ",") || IsKw(j, "in")) continue;
                if (_p[j].Kind == TokenKind.Keyword) return false;
            }
            return false;
        }
        return t.Text is "return" or "yield" or "else" or "if" or "elif" or "while" or "assert" or "del" or "with" or "except" or "raise" or "from" or "lambda" or "for";
    }

    bool EndsConditional(int j)
    {
        var t = _p[j];
        if (t.Kind == TokenKind.Operator) return t.Text is "," or ":" or ":=" || IsAssignOperator(t.Text);
        return t.Kind == TokenKind.Keyword && t.Text is "for" or "async" or "as" or "from";
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
            _atom[i] = _p[i].Invisible || i == 0 || !IsValueEnd(_p[i - 1]);

            int last = LastReal(c - 1, i);
            if (last > i && IsCommaPiece(last))
            {
                int commas = 0;
                for (int k = i + 1; k < c; k++) if (_depth[k] == _depth[i] + 1 && IsCommaPiece(k)) commas++;
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
            if (t.IsOpen) { if (inLambda.GetValueOrDefault(dep)) _lambdaParam[i] = true; sawFor[dep + 1] = false; afterFor[dep + 1] = false; inLambda[dep + 1] = false; continue; }
            if (t.IsClose) { if (inLambda.GetValueOrDefault(dep)) _lambdaParam[i] = true; continue; }
            if (inLambda.GetValueOrDefault(dep) && op && x == ":") { inLambda[dep] = false; continue; }
            // the commas of a lambda's parameters are not items: `return lambda a, b: (...)` split there would be a tuple
            if (inLambda.GetValueOrDefault(dep)) { _lambdaParam[i] = true; if (op && x == ",") _notDelimiter[i] = true; continue; }
            if (kw && x == "lambda") { inLambda[dep] = true; continue; }
            // nor are those of a comprehension's target
            if (op && x == "," && afterFor.GetValueOrDefault(dep)) { _notDelimiter[i] = true; continue; }
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

        int lead = IsKw(0, "async") ? 1 : 0;
        if (_p.Count > lead + 2 && IsKw(lead, "def"))
            foreach (var (s, e) in AnnotationRanges(_depth, lead))
                for (int i = s; i < e; i++) _annotation[i] = true;
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

    bool IsCommaPiece(int i) => !_p[i].Standalone && _p[i].Kind == TokenKind.Operator && _p[i].Text == ",";
    bool IsComma(int i) => IsCommaPiece(i) && !_notDelimiter[i];

    // ------------------------------------------------------------ text of a range

    string Join(int a, int b)
    {
        var sb = new StringBuilder();
        bool any = false;
        for (int i = a; i < b; i++)
        {
            if (_p[i].Standalone) continue;
            if (any && _p[i].Space) sb.Append(' ');
            sb.Append(_p[i].Shown);
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

    /// <summary>Black measures a line together with its comments.</summary>
    bool FitsRange(int indent, int a, int b)
    {
        string text = Join(a, b), comment = CommentsOf(a, b);
        return indent + text.Length + (comment.Length > 0 ? 2 + comment.Length : 0) <= Width;
    }

    bool FitsText(int indent, string text) => indent + text.Length <= Width;

    bool HasReal(int a, int b)
    {
        for (int i = a; i < b; i++) if (!_p[i].Standalone) return true;
        return false;
    }

    bool HasStandalone(int a, int b)
    {
        for (int i = a; i < b; i++) if (_p[i].Standalone) return true;
        return false;
    }

    bool HasMagic(int a, int b)
    {
        for (int i = a; i < b; i++) if (_magic[i] && _p[i].Match < b) return true;
        return false;
    }

    // ------------------------------------------------------------ output

    public List<OutLine> Wrap(int indent)
    {
        ParenthesiseWithItems(indent);
        return ImportLine(0, _p.Count, indent) ?? Transform(new Ln(0, _p.Count, indent, false, LineKind.Statement), force: false).Select(d => d.Out).ToList();
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
        Reanalyse();
    }

    /// <summary>
    /// Black's contains_unsplittable_type_ignore: a line that was one line in the source and ends with `# type: ignore` (or
    /// `# type: ignore[...]`) is not split, since the comment silences what is on its line. `# type: int` and `# noqa` do not count.
    /// </summary>
    bool UnsplittableTypeIgnore(Ln l)
    {
        if (l.Kind == LineKind.Comment || l.B <= l.A) return false;
        int first = -1, last = -1;
        for (int i = l.A; i < l.B; i++)
        {
            if (_p[i].Row < 0) continue;
            if (first < 0) first = _p[i].Row;
            last = _p[i].Row;
        }
        if (first < 0 || first != last) return false;
        for (int i = Math.Max(l.A, l.B - (l.Comma ? 1 : 2)); i < l.B; i++)
            if (_p[i].Comments.Any(c => c.StartsWith("# type: ignore", StringComparison.Ordinal))) return true;
        return false;
    }

    // ------------------------------------------------------------ lines (black's Line)

    enum LineKind { Statement, Head, Body, Tail, Item, Comment }

    /// <summary>
    /// A line being laid out: pieces [A, B) at an indentation, inside brackets or not, to be split whatever its length (black's
    /// should_split_rhs), and with a trailing comma the split adds after its last piece.
    /// </summary>
    readonly record struct Ln(int A, int B, int Indent, bool Inside, LineKind Kind, bool Forced = false, bool Comma = false)
    {
        public bool IsEmpty => A >= B && !Comma;
    }

    readonly record struct Done(OutLine Out, Ln Line);

    delegate List<Ln>? Transformer(Ln line, bool force);

    OutLine Render(Ln l) => l.Kind == LineKind.Comment
        ? new OutLine(l.Indent, _p[l.A].Text, "", IsComment: true)
        : new OutLine(l.Indent, Join(l.A, l.B) + (l.Comma ? "," : ""), CommentsOf(l.A, l.B));

    /// <summary>A line as black's str(line) has it, comments of their own included: what a split must change.</summary>
    string Key(Ln l)
    {
        var sb = new StringBuilder().Append(l.Indent).Append('|');
        for (int i = l.A; i < l.B; i++) sb.Append(_p[i].Standalone ? "\n" + _p[i].Text + "\n" : (_p[i].Space ? " " : "") + _p[i].Shown);
        return sb.Append(l.Comma ? "," : "").Append('|').Append(CommentsOf(l.A, l.B)).ToString();
    }

    bool Short(Ln l, int width = -1)
    {
        if (l.Kind == LineKind.Comment || HasStandalone(l.A, l.B)) return false;
        return Fits(Render(l), width < 0 ? Width : width);
    }

    static bool Fits(OutLine o, int width) => !o.IsComment && o.Indent + o.Text.Length + (o.Comment.Length > 0 ? 2 + o.Comment.Length : 0) <= width;

    /// <summary>Black's magic trailing comma of a line: a bracket pair of it with a trailing comma (a tail line tracks no brackets).</summary>
    bool Magic(Ln l) => l.Kind != LineKind.Tail && l.Kind != LineKind.Comment && HasMagic(l.A, l.B);

    /// <summary>The pieces black's bracket tracker sees: all of a line, those inside its complete bracket pairs for a head, none for a tail.</summary>
    bool[] Tracked(Ln l)
    {
        var mask = new bool[Math.Max(0, l.B - l.A)];
        if (l.Kind == LineKind.Tail) return mask;
        if (l.Kind != LineKind.Head) { Array.Fill(mask, true); return mask; }
        for (int i = l.A; i < l.B; i++)
        {
            if (!_p[i].IsOpen || _p[i].Match <= i || _p[i].Match >= l.B) continue;
            for (int k = i; k <= _p[i].Match; k++) mask[k - l.A] = true;
            i = _p[i].Match;
        }
        return mask;
    }

    int TrackedBase(Ln l, bool[] mask)
    {
        int min = int.MaxValue;
        for (int i = l.A; i < l.B; i++) if (mask[i - l.A]) min = Math.Min(min, _depth[i]);
        return min == int.MaxValue ? 0 : min;
    }

    /// <summary>The delimiters at the line's own depth with their priorities; a split-before delimiter that starts the line does not count, and l.B stands for an added comma.</summary>
    List<(int Index, int Priority)> Delimiters(Ln l)
    {
        var list = new List<(int, int)>();
        var mask = Tracked(l);
        int baseDepth = TrackedBase(l, mask);
        bool first = true;
        for (int i = l.A; i < l.B; i++)
        {
            if (!mask[i - l.A]) continue;
            bool isFirst = first;
            first = false;
            if (_p[i].Standalone || _depth[i] != baseDepth) continue;
            if (IsComma(i)) list.Add((i, Comma));
            else if (_before[i] > 0 && !isFirst) list.Add((i, _before[i]));
        }
        if (l.Comma && l.Kind != LineKind.Tail) list.Add((l.B, Comma));
        return list;
    }

    // ------------------------------------------------------------ transform_line

    List<Done> Transform(Ln l, bool force)
    {
        if (l.Kind == LineKind.Comment) return new() { new Done(Render(l), l) };
        var rendered = Render(l);
        if (!l.Forced && !Magic(l) && (Short(l) || UnsplittableTypeIgnore(l)) && !(l.Inside && HasStandalone(l.A, l.B))) return new() { new Done(rendered, l) };

        var transformers = IsDefLine(l)
            ? new (Transformer, bool)[] { (LeftHandSplit, false) }
            : l.Inside
                ? new (Transformer, bool)[] { (DelimiterSplit, false), (StandaloneSplit, false), (RightHandSplitWithOmits, true) }
                : new (Transformer, bool)[] { (RightHandSplitWithOmits, true) };
        foreach (var (t, isRhs) in transformers)
        {
            var result = Run(l, t, isRhs, rendered, force);
            if (result != null) return result;
        }
        return HasStandalone(l.A, l.B) ? ForcedStandaloneSplit(l) : new() { new Done(rendered, l) };
    }

    bool IsDefLine(Ln l) => l.Kind != LineKind.Comment && l.B > l.A && (IsKw(l.A, "def") || IsKw(l.A, "async") && IsKw(l.A + 1, "def"));

    /// <summary>
    /// Black's run_transformer: each line a split gives is transformed in turn; a right-hand split that kept optional parentheses
    /// invisible and still left its first line too long gets a second opinion that uses them, taken when all its lines fit.
    /// </summary>
    List<Done>? Run(Ln l, Transformer t, bool isRhs, OutLine rendered, bool force)
    {
        var before = SaveVisible();
        var produced = t(l, force);
        if (produced == null) return null;
        string key = Key(l);
        foreach (var x in produced) if (x.Kind != LineKind.Comment && Key(x) == key) return null;
        var result = new List<Done>();
        foreach (var x in produced) result.AddRange(Transform(x, force));
        if (result.Count == 0) return null;

        if (force || !isRhs) return result;
        var mask = Tracked(l);
        int baseDepth = TrackedBase(l, mask);
        bool anyOptional = false, optionalUsed = false;
        for (int i = l.A; i < l.B; i++)
        {
            if (!mask[i - l.A] || !_p[i].Invisible) continue;
            anyOptional = true;
            if (_depth[i] == baseDepth && _p[i].Visible) optionalUsed = true;
        }
        if (!anyOptional || optionalUsed || Fits(result[0].Out, Width) || UnsplittableTypeIgnore(result[0].Line) || OverOnlyBySubscriptComment(result[0].Line)) return result;

        var after = SaveVisible();
        RestoreVisible(before);
        var second = Run(l, t, isRhs, rendered, force: true);
        RestoreVisible(after);
        if (second == null) return null;
        return second.All(d => Fits(d.Out, Width)) ? second : result;
    }

    bool[] SaveVisible() => _p.Select(p => p.Visible).ToArray();

    void RestoreVisible(bool[] state)
    {
        for (int i = 0; i < _p.Count && i < state.Length; i++) _p[i].Visible = state[i];
    }

    /// <summary>A line whose code fits and whose comments, all on opening square brackets, make it too long.</summary>
    bool OverOnlyBySubscriptComment(Ln l)
    {
        if (l.Kind == LineKind.Comment || !FitsText(l.Indent, Join(l.A, l.B) + (l.Comma ? "," : ""))) return false;
        for (int i = l.A; i < l.B; i++)
            if (_p[i].Comments.Count > 0 && !(_p[i].IsOpen && _p[i].Text == "[")) return false;
        return true;
    }

    /// <summary>Black's last resort for a line it could not split that has comments of their own: each on its own line.</summary>
    List<Done> ForcedStandaloneSplit(Ln l)
    {
        var lines = new List<Done>();
        int start = l.A;
        for (int i = l.A; i < l.B; i++)
        {
            if (!_p[i].Standalone) continue;
            if (HasReal(start, i)) { var code = new Ln(start, i, l.Indent, l.Inside, LineKind.Item); lines.Add(new Done(Render(code), code)); }
            var comment = new Ln(i, i + 1, l.Indent, l.Inside, LineKind.Comment);
            lines.Add(new Done(Render(comment), comment));
            start = i + 1;
        }
        if (HasReal(start, l.B) || l.Comma) { var code = new Ln(start, l.B, l.Indent, l.Inside, LineKind.Item, false, l.Comma); lines.Add(new Done(Render(code), code)); }
        return lines;
    }

    // ---------------------------------------------------------- one item per line (delimiter_split)

    List<Ln>? DelimiterSplit(Ln l, bool force)
    {
        if (l.IsEmpty) return null;
        var delimiters = Delimiters(l);
        int lastLeaf = l.Comma ? l.B : l.B - 1;
        int max = 0;
        foreach (var (index, priority) in delimiters) if (!(index == lastLeaf && priority == Comma)) max = Math.Max(max, priority);
        if (max == 0) return null;
        if (max == Dot && delimiters.Count(x => x.Priority == Dot) == 1) return null;
        var at = delimiters.Where(x => x.Priority == max).Select(x => x.Index).ToHashSet();
        bool safe = TrailingCommaSafe(l);
        int baseDepth = TrackedBase(l, Tracked(l));

        var lines = new List<Ln>();
        int start = l.A;
        void Flush(int end, bool comma)
        {
            if (end > start || comma) lines.Add(new Ln(start, end, l.Indent, l.Inside, LineKind.Item, false, comma));
            start = end;
        }
        for (int i = l.A; i < l.B; i++)
        {
            if (_p[i].Standalone)
            {
                if (_depth[i] != baseDepth) continue;
                // a comment of its own at the line's depth is a line of its own
                Flush(i, false);
                lines.Add(new Ln(i, i + 1, l.Indent, l.Inside, LineKind.Comment));
                start = i + 1;
                continue;
            }
            if (max != Comma && at.Contains(i) && i > start)
            {
                // the comments after an operator the line is split before stay on the line it ends (black: all but commas and strings)
                if (max != StringConcat && _p[i].Comments.Count > 0 && PreviousReal(i) >= start)
                {
                    _p[PreviousReal(i)].Comments.AddRange(_p[i].Comments);
                    _p[i].Comments.Clear();
                }
                Flush(i, false);
            }
            if (max == Comma && at.Contains(i)) Flush(i + 1, false);
        }
        if (start < l.B || l.Comma)
        {
            bool add = max == Comma && safe && !l.Comma && !IsCommaPiece(LastReal(l.B - 1, start - 1));
            Flush(l.B, l.Comma || add);
        }
        else if (max == Comma && safe && _p[l.B - 1].Standalone)
        {
            // the line ends with comments of their own: the comma goes after the last code
            int last = lines.FindLastIndex(x => x.Kind != LineKind.Comment);
            if (last >= 0 && !lines[last].Comma && !IsCommaPiece(LastReal(lines[last].B - 1, lines[last].A - 1)))
                lines[last] = lines[last] with { Comma = true };
        }
        return lines;
    }

    /// <summary>
    /// A trailing comma after `*args` or `**kwargs` of a call or def (or a bare `*` or `/` of a def) is only valid from Python 3.5 and
    /// 3.6 on; black adds one only when the code is for those (<see cref="FormatOptions.CommaAfterStar"/>).
    /// </summary>
    bool TrailingCommaSafe(Ln l)
    {
        if (_options.CommaAfterStar) return true;
        int open = l.A - 1;
        while (open >= 0 && !(_p[open].IsOpen && _p[open].Match >= l.B)) open--;
        if (open < 0 || _p[open].Text != "(" || _p[open].Invisible || _atom[open]) return true;
        var mask = Tracked(l);
        int baseDepth = TrackedBase(l, mask);
        for (int i = l.A; i < l.B; i++)
        {
            if (_p[i].Standalone || _depth[i] != baseDepth || _lambdaParam[i] || _p[i].Kind != TokenKind.Operator) continue;
            if (_p[i].Text is "*" or "**" && (_p[i].Unary || IsOp(i + 1, ",") || i + 1 == l.B)) return false;
            if (_p[i].Text == "/" && (IsOp(i + 1, ",") || i + 1 == l.B) && (i == l.A || IsOp(i - 1, ","))) return false;
        }
        return true;
    }

    /// <summary>Black's standalone_comment_split: the comments of their own at the line's depth end the code before them.</summary>
    List<Ln>? StandaloneSplit(Ln l, bool force)
    {
        if (!HasStandalone(l.A, l.B)) return null;
        int baseDepth = TrackedBase(l, Tracked(l));
        var lines = new List<Ln>();
        int start = l.A;
        for (int i = l.A; i < l.B; i++)
        {
            if (!(_p[i].Standalone && _depth[i] == baseDepth)) continue;
            if (i > start) lines.Add(new Ln(start, i, l.Indent, l.Inside, LineKind.Item));
            lines.Add(new Ln(i, i + 1, l.Indent, l.Inside, LineKind.Comment));
            start = i + 1;
        }
        if (start < l.B || l.Comma) lines.Add(new Ln(start, l.B, l.Indent, l.Inside, LineKind.Item, false, l.Comma));
        return lines;
    }

    // ---------------------------------------------------------- a def: its first bracket pair (left_hand_split)

    List<Ln>? LeftHandSplit(Ln l, bool force)
    {
        int matching = -1, close = -1;
        foreach (string kind in new[] { "(", "[" })
        {
            matching = -1;
            close = -1;
            int typeParams = 0;
            bool inBody = false, bodyEmpty = true;
            for (int i = l.A; i < l.B; i++)
            {
                if (i - l.A == 2 && _p[i].IsOpen && _p[i].Text == "[") typeParams++;
                else if (typeParams > 0) { if (_p[i].IsOpen && _p[i].Text == "[") typeParams++; else if (_p[i].IsClose && _p[i].Text == "]") typeParams--; }
                if (inBody && _p[i].IsClose && _p[i].Match == matching && !(kind == "(" && typeParams > 0))
                {
                    if (!bodyEmpty) { close = i; break; }
                    inBody = false;   // empty brackets stay in the head
                    continue;
                }
                if (inBody) { bodyEmpty = false; continue; }
                if (_p[i].IsOpen && _p[i].Text == kind && !(kind == "(" && typeParams > 0)) { matching = i; inBody = true; bodyEmpty = true; }
            }
            if (matching >= 0 && close >= 0 && close < l.B) break;
        }
        if (matching < 0 || close < 0) return null;
        Show(matching);
        Show(close);
        var body = BodyLine(l, matching, close);
        return NonEmpty(new Ln(l.A, matching + 1, l.Indent, false, LineKind.Head), body, new Ln(close, l.B, l.Indent, false, LineKind.Tail, false, l.Comma));
    }

    static List<Ln> NonEmpty(params Ln[] lines) => lines.Where(x => !x.IsEmpty).ToList();

    void Show(int i)
    {
        if (_p[i].Invisible) _p[i].Visible = true;
    }

    /// <summary>The content of a bracket pair as a line of its own: one level in, to be split further when black's should_split_line says so.</summary>
    Ln BodyLine(Ln original, int open, int close)
    {
        bool comma = EnsureTrailingComma(original, open, close);
        var body = new Ln(open + 1, close, original.Indent + Indent, true, LineKind.Body, false, comma);
        return body with { Forced = ShouldSplitBody(body, open) };
    }

    /// <summary>The parameters of a def get a trailing comma when they have no comma of their own (black's _ensure_trailing_comma).</summary>
    bool EnsureTrailingComma(Ln original, int open, int close)
    {
        if (close <= open + 1 || !IsDefLine(original) || _p[open].Shown != "(") return false;
        for (int i = open + 1; i < close; i++) if (IsCommaPiece(i) && !_annotation[i]) return false;
        // not in parentheses around a return annotation
        int prev = PreviousReal(open);
        return !(prev >= 0 && IsOp(prev, "->"));
    }

    /// <summary>Black's should_split_line: the items of a collection, or of anything with a magic trailing comma, one to a line.</summary>
    bool ShouldSplitBody(Ln body, int open)
    {
        bool trailing = body.Comma || (body.B > body.A && IsCommaPiece(body.B - 1));
        int trailingAt = body.Comma ? body.B : body.B - 1;
        int max = 0;
        foreach (var (index, priority) in Delimiters(body)) if (!(trailing && index == trailingAt)) max = Math.Max(max, priority);
        return max == Comma && ((_options.MagicTrailingComma && trailing) || _atom[open]);
    }

    // ---------------------------------------------------------- the last bracket pair (right_hand_split)

    readonly record struct Rhs(Ln Head, Ln Body, Ln Tail, int Open, int Close);

    List<Ln>? RightHandSplitWithOmits(Ln l, bool force)
    {
        List<Ln>? first = null;
        foreach (var omit in TrailersToOmit(l))
        {
            if (omit.Count > 0)
            {
                int target = -1;
                for (int i = l.B - 1; i >= l.A; i--) if (_p[i].IsClose && !omit.Contains(i)) { target = _p[i].Match; break; }
                // the head up to that bracket is too long whatever follows
                if (target >= l.A && _p[target].Shown.Length > 0 && PrefixLength(l, target) > Width) continue;
            }
            var lines = RightHandSplit(l, omit, force);
            if (lines == null) return null;
            if (first == null && omit.Count == 0) first = lines;
            if (Short(lines[0]) || (omit.Count > 0 && OverOnlyBySubscriptComment(lines[0]))) return lines;
        }
        return first ?? RightHandSplit(l, new HashSet<int>(), force);
    }

    int PrefixLength(Ln l, int at)
    {
        int length = l.Indent;
        for (int i = l.A; i < at; i++) if (!_p[i].Standalone) length += (i > l.A && _p[i].Space ? 1 : 0) + _p[i].Shown.Length;
        return length;
    }

    /// <summary>A piece's length as black counts it in enumerate_with_length: its space, its text and its comments.</summary>
    int LeafLength(Ln l, int i)
    {
        if (i == l.B) return 1;   // the added comma
        if (_p[i].Standalone) return _p[i].Text.Length;
        int length = (i > l.A && _p[i].Space ? 1 : 0) + _p[i].Shown.Length;
        foreach (var c in _p[i].Comments) length += c.Length;
        return length;
    }

    /// <summary>
    /// Black's generate_trailers_to_omit: the closing brackets at the end of the line that may be left on the last line (a call or
    /// subscript after the bracket the split uses), growing one trailer at a time while they fit.
    /// </summary>
    IEnumerable<HashSet<int>> TrailersToOmit(Ln l)
    {
        var omit = new HashSet<int>();
        if (!Magic(l)) yield return new HashSet<int>(omit);
        int length = l.Indent;
        int opening = -1, closing = -1;
        var inner = new HashSet<int>();
        int end = l.Comma ? l.B : l.B - 1;
        for (int i = end; i >= l.A; i--)
        {
            length += LeafLength(l, i);
            if (length > Width) yield break;
            if (i < l.B && (_p[i].Standalone || _p[i].Comments.Count > 0)) yield break;
            if (i == l.B) continue;
            int prev = i - 1 >= l.A ? i - 1 : -1;
            if (opening >= 0)
            {
                if (i == opening) opening = -1;
                else if (_p[i].IsClose)
                {
                    if (prev >= 0 && IsCommaPiece(prev) && !OneSequence(_p[i].Match, i)) yield break;
                    inner.Add(i);
                }
            }
            else if (_p[i].IsClose)
            {
                if (prev >= 0 && _p[prev].IsOpen) { inner.Add(i); continue; }
                if (closing >= 0)
                {
                    omit.Add(closing);
                    omit.UnionWith(inner);
                    inner.Clear();
                    yield return new HashSet<int>(omit);
                }
                if (prev >= 0 && IsCommaPiece(prev) && !OneSequence(_p[i].Match, i)) yield break;
                if (_p[i].Shown.Length > 0) { opening = _p[i].Match; closing = i; }
            }
        }
    }

    /// <summary>Black's is_one_sequence_between: parentheses holding at most one item (a one-tuple); the commas of a call or def count twice.</summary>
    bool OneSequence(int open, int close)
    {
        if (open < 0 || _p[open].Text != "(" || _p[close].Text != ")") return false;
        int commas = 0;
        for (int i = open + 1; i < close; i++)
            if (_depth[i] == _depth[open] + 1 && IsCommaPiece(i)) commas += _atom[open] ? 1 : 2;
        return commas < 2;
    }

    List<Ln>? RightHandSplit(Ln l, HashSet<int> omit, bool force)
    {
        var rhs = FirstRightHandSplit(l, omit);
        return rhs == null ? null : MaybeSplitOmittingOptionalParens(rhs.Value, l, omit, force);
    }

    /// <summary>Head, body and tail at the last closing bracket not omitted; empty brackets stay in the tail.</summary>
    Rhs? FirstRightHandSplit(Ln l, HashSet<int> omit)
    {
        int open = -1, close = -1;
        for (int i = l.B - 1; i >= l.A; i--)
        {
            if (!_p[i].IsClose || omit.Contains(i)) continue;
            int o = _p[i].Match;
            if (o == i - 1) { i--; continue; }
            if (o < l.A) return null;
            open = o;
            close = i;
            break;
        }
        if (close < 0) return null;
        var head = new Ln(l.A, open + 1, l.Indent, false, LineKind.Head);
        var body = BodyLine(l, open, close);
        var tail = new Ln(close, l.B, l.Indent, false, LineKind.Tail, false, l.Comma);
        return new Rhs(head, body, tail, open, close);
    }

    List<Ln>? MaybeSplitOmittingOptionalParens(Rhs rhs, Ln l, HashSet<int> omit, bool force)
    {
        if (!force && Unshown(rhs.Open) && Unshown(rhs.Close) && !IsImportLine(l) && CanOmitInvisibleParens(rhs))
        {
            var omitted = new HashSet<int>(omit) { rhs.Close };
            bool failed;
            var oop = FirstRightHandSplit(l, omitted);
            if (oop == null) failed = true;
            else if (PreferSplitWithoutParens(oop.Value, rhs))
            {
                var lines = MaybeSplitOmittingOptionalParens(oop.Value, l, omitted, force);
                if (lines != null) return lines;
                failed = true;
            }
            else failed = false;

            if (failed && !IsChainedAssignment(l) && !CanBeSplit(rhs.Body) && !Short(rhs.Body)) return null;
        }
        Show(rhs.Open);
        Show(rhs.Close);
        return NonEmpty(rhs.Head, rhs.Body, rhs.Tail);
    }

    bool Unshown(int i) => _p[i].Invisible && !_p[i].Visible;

    bool IsImportLine(Ln l) => IsKw(l.A, "import") || IsKw(l.A, "from");

    bool IsChainedAssignment(Ln l)
    {
        int count = 0;
        for (int i = l.A; i < l.B; i++) if (IsOp(i, "=")) count++;
        return count > 1;
    }

    /// <summary>Black's can_be_split: false only for a line that surely cannot be split (one token, a string's short method chain).</summary>
    bool CanBeSplit(Ln l)
    {
        int count = l.B - l.A + (l.Comma ? 1 : 0);
        if (count < 2) return false;
        if (_p[l.A].Kind == TokenKind.String && l.A + 1 < l.B && IsOp(l.A + 1, "."))
        {
            int calls = 0, dots = 0;
            int next = l.B - 1;
            for (int i = l.B - 2; i >= l.A; i--)
            {
                var t = _p[i];
                if (t.IsOpen) { if (!_p[next].IsClose) return false; calls++; }
                else if (IsOp(i, ".")) dots++;
                else if (t.Kind == TokenKind.Name) { if (!(IsOp(next, ".") || _p[next].IsOpen)) return false; }
                else if (!t.IsClose) return false;
                if (dots > 1 && calls > 1) return false;
            }
        }
        return true;
    }

    /// <summary>Black's can_omit_invisible_parens: whether the body reads well without its optional parentheses.</summary>
    bool CanOmitInvisibleParens(Rhs rhs)
    {
        var body = rhs.Body;
        if (_p[rhs.Open].Comments.Count > 0) return false;

        // comments of their own need the parentheses, unless brackets inside hold them
        int closing = -1;
        for (int i = body.B - 1; i >= body.A; i--)
        {
            if (closing >= 0 && i == _p[closing].Match) closing = -1;
            if (_p[i].Standalone && closing < 0) return false;
            if (closing < 0 && _p[i].IsClose && _p[i].Match >= body.A && _p[i].Shown.Length > 0) closing = i;
        }

        var delimiters = Delimiters(body);
        if (delimiters.Count == 0) return true;
        int max = delimiters.Max(x => x.Priority);
        int count = delimiters.Count(x => x.Priority == max);
        if (count > 1) return false;
        if (count == 1 && max == Comma && (IsKw(rhs.Head.A, "with") || IsKw(rhs.Head.A, "async") && IsKw(rhs.Head.A + 1, "with"))) return false;
        if (max == Dot) return true;

        int firstAt = body.A, lastAt = body.Comma ? -1 : body.B - 1;
        if (body.B - body.A < 2) return false;
        if (_p[firstAt].IsOpen && !_p[firstAt + 1].IsClose && CanOmitOpeningParen(body, firstAt)) return true;
        if (lastAt > body.A && _p[lastAt].IsClose && (_p[lastAt].Text is ")" or "}" || _p[lastAt].Text == "]" && _atom[_p[lastAt].Match]))
        {
            if (_p[lastAt - 1].IsOpen) return false;
            if (CanOmitClosingParen(body, lastAt)) return true;
        }
        return false;
    }

    /// <summary>The line starts with a bracket and what follows its closing bracket fits (up to further brackets to split at).</summary>
    bool CanOmitOpeningParen(Ln line, int first)
    {
        bool remainder = false;
        int length = line.Indent;
        int end = line.Comma ? line.B : line.B - 1;
        for (int i = line.A; i <= end; i++)
        {
            if (i < line.B && _p[i].IsClose && _p[i].Match == first) remainder = true;
            if (!remainder) continue;
            length += LeafLength(line, i);
            if (length > Width) return false;
            if (i < line.B && _p[i].IsOpen) remainder = false;
        }
        return true;
    }

    /// <summary>The line ends with a bracket whose opening one comes early enough (or after other brackets to split at).</summary>
    bool CanOmitClosingParen(Ln line, int last)
    {
        int length = line.Indent;
        bool seenOther = false;
        int end = line.Comma ? line.B : line.B - 1;
        for (int i = line.A; i <= end; i++)
        {
            length += LeafLength(line, i);
            if (i == _p[last].Match) { if (seenOther || length <= Width) return true; }
            else if (i < line.B && _p[i].IsOpen) seenOther = true;
        }
        return false;
    }

    /// <summary>Black's _prefer_split_rhs_oop_over_rhs: whether to split without the optional parentheses around an assigned value.</summary>
    bool PreferSplitWithoutParens(Rhs oop, Rhs rhs)
    {
        if (UnsplittableTypeIgnore(oop.Head) || UnsplittableTypeIgnore(oop.Body) || UnsplittableTypeIgnore(oop.Tail)) return true;
        var head = rhs.Head;
        if (!(head.B - head.A >= 2 && IsOp(head.B - 2, "="))) return true;
        bool brackets = false;
        for (int i = head.A; i < head.B - 1; i++) if (_p[i].IsOpen || _p[i].IsClose) { brackets = true; break; }
        if (!brackets) return true;
        if (!Short(head, Width - 1)) return true;
        if (Magic(head)) return true;

        int headEquals = 0, oopEquals = 0;
        for (int i = head.A; i < head.B; i++) if (IsOp(i, "=")) headEquals++;
        for (int i = oop.Head.A; i < oop.Head.B; i++) if (IsOp(i, "=")) oopEquals++;
        if (headEquals > 1 && headEquals > oopEquals) return false;

        bool closingAfterAssign = false;
        for (int i = oop.Head.B - 1; i >= oop.Head.A; i--)
        {
            if (IsOp(i, "=")) break;
            if (_p[i].IsClose) { closingAfterAssign = true; break; }
        }
        return closingAfterAssign || (oopEquals > 0 && Short(oop.Head));
    }

    static bool IsAssignOperator(string t) => t is "=" or "+=" or "-=" or "*=" or "/=" or "//=" or "%=" or "**=" or ">>=" or "<<=" or "&=" or "|=" or "^=" or "@=";

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
        if (!magic && !standalone && pending.Count == 0 && FitsText(indent, single + (JoinComments(headComment, allComments) is { Length: > 0 } all ? "  " + all : "")))
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

    static string JoinComments(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "  " + b;
}
