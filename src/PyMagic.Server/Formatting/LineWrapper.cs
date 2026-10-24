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
}

/// <summary>
/// Splits a logical line that is too long, or has a magic trailing comma, the way black does: at the last bracket pair
/// (the parameters of a <c>def</c>), the content of the brackets on its own indented lines, one item per line at the
/// strongest delimiter (commas, then boolean operators, comparisons, arithmetic, dots of a call chain), a trailing comma
/// after the items, and invisible parentheses around the value of an assignment, a return or a condition.
/// </summary>
internal sealed class LineWrapper
{
    const int Width = 88;
    const int Indent = 4;
    const int Comma = 18, Comprehension = 20, Ternary = 16, Logic = 14, StringConcat = 12, Comparator = 10, Dot = 1;

    readonly List<Piece> _p;
    readonly int[] _depth;
    readonly int[] _before;          // priority of the delimiter that starts a new item at piece i (0: none)
    readonly bool[] _magic;          // an opening bracket whose group ends with a trailing comma that must be kept
    readonly bool[] _atom;           // an opening bracket that is not a call, subscript or parameter list
    int _preferred = -1;             // a bracket pair chosen by the invisible-parenthesis logic for the next right-hand split

    public LineWrapper(List<Piece> pieces)
    {
        _p = pieces;
        int n = pieces.Count;
        _depth = new int[n];
        _before = new int[n];
        _magic = new bool[n];
        _atom = new bool[n];
        Analyse();
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
            if (c - 1 > i && _p[c - 1].Text == ",")
            {
                int commas = 0;
                for (int k = i + 1; k < c; k++) if (_depth[k] == _depth[i] + 1 && IsComma(k)) commas++;
                // a one-element tuple `(1,)` and a one-element subscript `x[1,]` are not magic; `f(a,)`, `[1,]` and `{1,}` are
                _magic[i] = commas > 1 || !((_p[i].Text == "(" && _atom[i]) || (_p[i].Text == "[" && !_atom[i]));
            }
        }

        // delimiters, tracked per depth: comprehension `for`, the `in` of a `for` target and lambda parameters change what a token means
        var sawFor = new Dictionary<int, bool>();
        var afterFor = new Dictionary<int, bool>();
        var inLambda = new Dictionary<int, bool>();
        for (int i = 0; i < _p.Count; i++)
        {
            var t = _p[i];
            int dep = _depth[i];
            string x = t.Text;
            bool op = t.Kind == TokenKind.Operator, kw = t.Kind == TokenKind.Keyword;
            if (t.IsOpen) { sawFor[dep + 1] = false; afterFor[dep + 1] = false; inLambda[dep + 1] = false; continue; }
            if (t.IsClose) continue;
            if (inLambda.GetValueOrDefault(dep) && op && x == ":") { inLambda[dep] = false; continue; }
            if (inLambda.GetValueOrDefault(dep)) continue;
            if (kw && x == "lambda") { inLambda[dep] = true; continue; }
            if (i == 0) continue;

            int priority = 0;
            if (op)
            {
                if (x == "," ) continue;   // commas end their item; handled by IsComma
                if (!t.Unary)
                    priority = x switch
                    {
                        "|" => 9, "^" => 8, "&" => 7, "<<" or ">>" => 6, "+" or "-" => 5,
                        "*" or "/" or "//" or "%" or "@" => 4, "**" => 3,
                        "<" or ">" or "<=" or ">=" or "==" or "!=" or "<>" => Comparator,
                        _ => 0,
                    };
                if (x == "." && i > 0 && _p[i - 1].IsClose) priority = Dot;
            }
            else if (kw)
            {
                switch (x)
                {
                    case "and" or "or": priority = Logic; break;
                    case "for": priority = dep > 0 ? Comprehension : 0; if (dep > 0) { sawFor[dep] = true; afterFor[dep] = true; } break;
                    case "async" when i + 1 < _p.Count && _p[i + 1].Text == "for": priority = dep > 0 ? Comprehension : 0; break;
                    case "if": priority = dep > 0 && sawFor.GetValueOrDefault(dep) ? Comprehension : Ternary; break;
                    case "else": priority = Ternary; break;
                    case "in":
                        if (afterFor.GetValueOrDefault(dep)) { afterFor[dep] = false; break; }
                        priority = _p[i - 1].Text == "not" ? 0 : Comparator;
                        break;
                    case "not":
                        priority = i + 1 < _p.Count && _p[i + 1].Text == "in" && !afterFor.GetValueOrDefault(dep) ? Comparator : 0;
                        break;
                    case "is": priority = Comparator; break;
                }
            }
            else if (t.Kind == TokenKind.String && _p[i - 1].Kind == TokenKind.String && !_p[i - 1].IsOpen) priority = StringConcat;
            _before[i] = priority;
        }
    }

    static bool IsValueEnd(Piece p) =>
        p.Kind is TokenKind.Name or TokenKind.Number or TokenKind.String
        || (p.Kind == TokenKind.Keyword && p.Text is "True" or "False" or "None")
        || (p.Kind == TokenKind.Operator && (p.IsClose || p.Text == "..."));

    bool IsComma(int i) => _p[i].Kind == TokenKind.Operator && _p[i].Text == ",";

    // ------------------------------------------------------------ output

    public List<(int Indent, string Text)> Wrap(int indent)
    {
        var lines = ImportLine(0, _p.Count, indent) ?? SplitLine(0, _p.Count, indent, inside: false, forced: false);
        return lines;
    }

    string Join(int a, int b)
    {
        var sb = new StringBuilder();
        for (int i = a; i < b; i++)
        {
            if (i > a && _p[i].Space) sb.Append(' ');
            sb.Append(_p[i].Text);
        }
        return sb.ToString();
    }

    bool Fits(int indent, string text) => indent + text.Length <= Width;

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

    List<(int Indent, string Text)> One(int indent, string text) => new() { (indent, text) };

    List<(int Indent, string Text)> SplitLine(int a, int b, int indent, bool inside, bool forced)
    {
        string text = Join(a, b);
        bool magic = HasMagic(a, b);
        if (Fits(indent, text) && !magic && !forced) return One(indent, text);

        if (inside)
        {
            var split = DelimiterSplit(a, b, indent);
            if (split != null) return split;
        }
        return RightHandSplit(a, b, indent, inside) ?? One(indent, text);
    }

    // ---------------------------------------------------------- one item per line

    List<(int Indent, string Text)>? DelimiterSplit(int a, int b, int indent)
    {
        int baseDepth = BaseDepth(a, b);
        int max = 0, count = 0;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth) continue;
            int pr = IsComma(i) ? Comma : _before[i];
            if (pr > max) { max = pr; count = 1; } else if (pr == max && pr > 0) count++;
        }
        if (max == 0) return null;
        if (max == Dot && count < 2) return null;

        var items = new List<(int Start, int End)>();
        int start = a;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth) continue;
            if (max == Comma && IsComma(i)) { items.Add((start, i + 1)); start = i + 1; }
            else if (max != Comma && _before[i] == max && i > start) { items.Add((start, i)); start = i; }
        }
        if (start < b) items.Add((start, b));
        if (items.Count == 0) return null;

        var lines = new List<(int Indent, string Text)>();
        for (int k = 0; k < items.Count; k++)
        {
            // the comma that ends an item is not part of what is laid out; it is put back after the last line of the item
            int itemEnd = items[k].End;
            bool endsWithComma = max == Comma && IsComma(itemEnd - 1);
            if (endsWithComma) itemEnd--;
            if (itemEnd <= items[k].Start) { if (lines.Count > 0) lines[^1] = (lines[^1].Indent, lines[^1].Text + ","); continue; }

            // an item that is the whole range cannot be split by delimiters again (that would not end)
            bool whole = items[k].Start == a && itemEnd == b;
            var part = SplitLine(items[k].Start, itemEnd, indent, inside: !whole, forced: false);
            if (endsWithComma) part[^1] = (part[^1].Indent, part[^1].Text + ",");
            lines.AddRange(part);
        }
        if (max == Comma && !IsComma(b - 1))
        {
            var last = lines[^1];
            lines[^1] = (last.Indent, last.Text + ",");
        }
        return lines;
    }

    // ---------------------------------------------------------- split at a bracket pair

    List<(int Indent, string Text)>? RightHandSplit(int a, int b, int indent, bool inside)
    {
        // the value of an assignment, a return, a condition: optional parentheses
        _preferred = -1;
        if (!inside && a == 0 && b == _p.Count && !HasMagic(a, b))
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
                if (Fits(indent, Join(a, groups[k] + 1))) { chosen = groups[k]; break; }
            }
            if (chosen < 0) chosen = groups[^1];
        }
        return SplitAt(a, b, indent, chosen, isDef && !inside && chosen == groups[0]);
    }

    List<(int Indent, string Text)>? SplitAt(int a, int b, int indent, int open, bool defParameters)
    {
        int close = _p[open].Match;
        var head = SplitLine(a, open + 1, indent, inside: false, forced: false);
        // short lines of the head and the tail are left as they are; splitting them again is only for magic commas and long tails
        var body = BodyOf(open, close, indent + Indent, defParameters);
        var tail = SplitLine(close, b, indent, inside: false, forced: false);

        var lines = new List<(int Indent, string Text)>(head);
        lines.AddRange(body);
        lines.AddRange(tail);
        return lines;
    }

    List<(int Indent, string Text)> BodyOf(int open, int close, int indent, bool defParameters)
    {
        int a = open + 1;
        bool trailingComma = IsComma(close - 1);
        int baseDepth = _depth[open] + 1;
        bool hasCommas = false;
        for (int i = a; i < close - (trailingComma ? 1 : 0); i++) if (_depth[i] == baseDepth && IsComma(i)) { hasCommas = true; break; }
        bool maxIsComma = hasCommas && NoStrongerThanComma(a, close, baseDepth);
        bool forced = maxIsComma && (trailingComma || _atom[open] || IsImportParen(open));

        var lines = SplitLine(a, close, indent, inside: true, forced);
        // a def with a single parameter gets the trailing comma too
        if (defParameters && !hasCommas && !trailingComma)
        {
            var last = lines[^1];
            lines[^1] = (last.Indent, last.Text + ",");
        }
        return lines;
    }

    bool NoStrongerThanComma(int a, int b, int baseDepth)
    {
        for (int i = a; i < b; i++) if (_depth[i] == baseDepth && _before[i] > Comma) return false;
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
    (List<(int Indent, string Text)>? Lines, bool GiveUp) WrapSlot(int a, int b, int indent, Slot slot)
    {
        if (slot.Wrap == SlotAction.Omit) return (null, false);
        int s = slot.Start, e = slot.End;

        // the delimiters of the value decide whether the brackets of a call at its end are enough
        int baseDepth = BaseDepth(s, e), delimiters = 0;
        for (int i = s; i < e; i++)
            if (_depth[i] == baseDepth && (IsComma(i) || _before[i] > 0)) delimiters++;
        var groups = TopGroups(s, e).Where(g => _p[g].Match > g + 1).ToList();
        bool endsWithBracket = _p[e - 1].IsClose;

        if (groups.Count > 0 && (delimiters == 0 || (delimiters == 1 && endsWithBracket)) && Fits(indent, Join(a, groups[^1] + 1)))
            return (null, false);

        // `(a, b, c) = value`: when the rest of the line fits after the target's closing bracket, the target is split instead
        if (s > 1 && _p[s - 1].Kind == TokenKind.Operator && IsAssignOperator(_p[s - 1].Text))
        {
            var targetGroups = TopGroups(a, s - 1).Where(g => _p[g].Match > g + 1).ToList();
            if (targetGroups.Count > 0 && Fits(indent, Join(_p[targetGroups[^1]].Match, b)))
            {
                _preferred = targetGroups[^1];
                return (null, false);
            }
        }

        // wrapping only helps when the value fits in the parentheses or can be split further
        bool fitsInside = Fits(indent + Indent, Join(s, e));
        bool canSplit = groups.Count > 0 || delimiters > 0;
        if (!fitsInside && !canSplit) return (null, true);

        var lines = new List<(int Indent, string Text)> { (indent, Join(a, s) + " (") };
        bool forced = delimiters > 0 && MaxIsComma(s, e, baseDepth);
        lines.AddRange(SplitLine(s, e, indent + Indent, inside: true, forced));
        lines.Add((indent, ")" + (e < b ? (_p[e].Space ? " " : "") + Join(e, b) : "")));
        return (lines, false);
    }

    bool MaxIsComma(int a, int b, int baseDepth)
    {
        bool comma = false;
        for (int i = a; i < b; i++)
        {
            if (_depth[i] != baseDepth) continue;
            if (IsComma(i)) comma = true;
            else if (_before[i] > Comma) return false;
        }
        return comma;
    }

    // ---------------------------------------------------------- imports

    /// <summary>`from m import a, b`: one line when it fits (the brackets go), else the names one per line in brackets.</summary>
    List<(int Indent, string Text)>? ImportLine(int a, int b, int indent)
    {
        if (b <= 3 || _p[0].Text != "from") return null;
        int imp = -1;
        for (int i = 1; i < b; i++) if (_depth[i] == 0 && _p[i].Kind == TokenKind.Keyword && _p[i].Text == "import") { imp = i; break; }
        if (imp < 0 || imp + 1 >= b) return null;

        int s = imp + 1, e = b;
        bool paren = _p[s].IsOpen && _p[s].Text == "(" && _p[s].Match == b - 1;
        if (paren) { s++; e = b - 1; }
        if (s >= e || _p[s].Text == "*") return null;

        bool magic = paren && IsComma(e - 1);
        var names = new List<string>();
        int start = s;
        for (int i = s; i < e; i++)
            if (IsComma(i)) { if (i > start) names.Add(Join(start, i)); start = i + 1; }
        if (start < e) names.Add(Join(start, e));
        if (names.Count == 0) return null;

        string module = Join(a, imp);
        string single = $"{module} import {string.Join(", ", names)}";
        if (!magic && Fits(indent, single)) return One(indent, single);

        var lines = new List<(int Indent, string Text)> { (indent, $"{module} import (") };
        foreach (var n in names) lines.Add((indent + Indent, n + ","));
        lines.Add((indent, ")"));
        return lines;
    }
}
