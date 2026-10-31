using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// More of Jinja: the line statements and line comments the environment may set, an expression read token by token as Jinja's parser reads it
/// (`{{ a b }}`), and `{% include %}` of a list of templates or of a name the view gives.
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>A Jinja template parsed with the line statement and line comment prefixes of the project's environment.</summary>
    ParsedTemplate ParseJinja(Snapshot snap, string text)
    {
        var env = JinjaEnvironmentOf(snap);
        return DjangoTemplateParser.Parse(text, true, env.LineStatement, env.LineComment);
    }

    // ------------------------------------------------------------ {% include %} of a list or a variable

    static readonly Regex QuotedTemplate = new(@"[""'](?<n>[^""']+)[""']", RegexOptions.Compiled);

    /// <summary>
    /// `{% include ['custom.html', 'default.html'] %}` is refused only when none is there (Jinja takes the first it finds); `{% include partial %}`
    /// with `partial` given by the view as a string: the template that string names.
    /// </summary>
    void IncludeListProblems(TemplateAnalysis a, Action<int, int, string, int, string> add)
    {
        if (Templates().Count == 0) return;
        bool Missing(string name) => !IsLibraryTemplate(name) && !Templates().Any(t => t.Name == name);
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "include" or "extends" or "import" or "from"))
        {
            if (tag.Args.Contains("ignore missing", StringComparison.Ordinal)) continue;
            string args = tag.Args.TrimStart();
            int lead = tag.Args.Length - args.Length;
            if (args.StartsWith('[') || args.StartsWith('('))
            {
                int close = args.IndexOf(args[0] == '[' ? ']' : ')');
                if (close < 0) continue;
                var names = QuotedTemplate.Matches(args[..close]).Select(m => (m.Groups["n"].Value, Start: tag.ArgsStart + lead + m.Groups["n"].Index)).ToList();
                if (names.Count == 0 || !names.All(n => Missing(n.Value))) continue;
                foreach (var (name, start) in names)
                    add(start, start + name.Length, $"None of the templates of this list is in a templates folder of the project ('{name}' is not): Jinja raises TemplatesNotFound", Severity.Information, "django-template");
                continue;
            }
            // a name the views give as a string
            var word = Regex.Match(args, @"^[A-Za-z_]\w*(?=\s|$)");
            if (!word.Success || word.Value is "ignore" or "with" or "without") continue;
            var given = a.Context.Where(e => e.Name == word.Value && e.Value is PyMagic.Core.Syntax.ConstExpr).Select(e => (Entry: e, Value: PyMagic.Server.Pytest.FixtureModel.StringValue((PyMagic.Core.Syntax.ConstExpr)e.Value!))).Where(x => x.Value != null).ToList();
            if (given.Count == 0 || !given.All(g => Missing(g.Value!))) continue;
            add(tag.ArgsStart + lead, tag.ArgsStart + lead + word.Length, $"'{word.Value}' is '{given[0].Value}' (given by {given[0].Entry.Source}), which is not in any templates folder of the project", Severity.Information, "django-template");
        }
    }

    // ------------------------------------------------------------ expressions, as Jinja's parser reads them

    /// <summary>What is wrong with an expression where Jinja's parser would stop (`{{ a b }}`: "expected token 'end of print statement', got 'b'"); null when it reads.</summary>
    static (string Message, int Start, int End)? JinjaGrammarProblem(string expr, int start, bool print) => new JinjaExpressionReader(expr, start, print).Problem();

    /// <summary>The parts of a tag Jinja reads as one expression: an if or elif's condition, a set's value, the template of an include or extends.</summary>
    static IEnumerable<(string Text, int Offset)> WholeExpressionsOf(TemplateTag tag)
    {
        switch (tag.Name)
        {
            case "if" or "elif":
                yield return (tag.Args, tag.ArgsStart);
                break;
            case "set" when JinjaSet.Match(tag.Args) is { Success: true } m:
                yield return (m.Groups["expr"].Value, tag.ArgsStart + m.Groups["expr"].Index);
                break;
            case "include" or "extends":
            {
                // the modifiers that may follow the template
                string text = Regex.Replace(tag.Args, @"\s+(ignore\s+missing|with\s+context|without\s+context)(?=\s|$)", m => new string(' ', m.Length));
                yield return (text.TrimEnd(), tag.ArgsStart);
                break;
            }
        }
    }
}

/// <summary>A small reader of Jinja expressions with the grammar of jinja2.parser: tuples, conditionals, logic, comparisons, arithmetic, `~`, filters, tests, calls, subscripts.</summary>
sealed class JinjaExpressionReader
{
    readonly List<(string Kind, string Text, int At)> _tokens = new();
    readonly string _expr;
    readonly int _start;
    readonly bool _print;
    int _i;
    (string Message, int Start, int End)? _problem;

    static readonly string[] Operators = { "**", "//", "==", "!=", "<=", ">=", "+", "-", "*", "/", "%", "~", "<", ">", "=", "(", ")", "[", "]", "{", "}", ",", ".", ":", "|" };

    public JinjaExpressionReader(string expr, int start, bool print)
    {
        _expr = expr;
        _start = start;
        _print = print;
        Tokenize();
    }

    void Tokenize()
    {
        int i = 0;
        while (i < _expr.Length && _problem == null)
        {
            char c = _expr[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c is '"' or '\'')
            {
                int j = i + 1;
                while (j < _expr.Length && _expr[j] != c) j += _expr[j] == '\\' ? 2 : 1;
                _tokens.Add(("string", _expr[i..Math.Min(j + 1, _expr.Length)], i));
                i = j + 1;
                continue;
            }
            if (char.IsDigit(c))
            {
                var m = Regex.Match(_expr[i..], @"^\d[\d_]*(\.\d[\d_]*)?([eE][+-]?\d+)?");
                _tokens.Add(("number", m.Value, i));
                i += m.Length;
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int j = i;
                while (j < _expr.Length && (char.IsLetterOrDigit(_expr[j]) || _expr[j] == '_')) j++;
                _tokens.Add(("name", _expr[i..j], i));
                i = j;
                continue;
            }
            var op = Operators.FirstOrDefault(o => string.CompareOrdinal(_expr, i, o, 0, o.Length) == 0);
            if (op == null) { _problem = ($"Jinja cannot read '{c}' here", _start + i, _start + i + 1); return; }
            _tokens.Add(("op", op, i));
            i += op.Length;
        }
    }

    (string Kind, string Text, int At)? Peek(int ahead = 0) => _i + ahead < _tokens.Count ? _tokens[_i + ahead] : null;
    bool IsOp(string text, int ahead = 0) => Peek(ahead) is { Kind: "op" } t && t.Text == text;
    bool IsName(string text, int ahead = 0) => Peek(ahead) is { Kind: "name" } t && t.Text == text;

    void Fail(string expected)
    {
        if (_problem != null) return;
        if (Peek() is { } t) _problem = ($"Jinja expected {expected}, got '{t.Text}'", _start + t.At, _start + t.At + t.Text.Length);
        else _problem = ($"Jinja expected {expected}, and the expression ends", _start + _expr.Length, _start + _expr.Length);
    }

    bool Expect(string op)
    {
        if (IsOp(op)) { _i++; return true; }
        Fail($"'{op}'");
        return false;
    }

    public (string Message, int Start, int End)? Problem()
    {
        if (_problem != null || _tokens.Count == 0) return _problem;
        Tuple(true);
        if (_problem == null && Peek() is { } extra)
            _problem = ($"Jinja expected the end of the {(_print ? "print statement" : "statement block")}, got '{extra.Text}'{(extra.Kind is "name" or "number" or "string" ? ": an operator or a comma is missing before it" : "")}",
                _start + extra.At, _start + extra.At + extra.Text.Length);
        return _problem;
    }

    static bool StartsExpression((string Kind, string Text, int At)? t) =>
        t is { } x && (x.Kind is "string" or "number" || x.Kind == "name" && x.Text is not ("and" or "or" or "if" or "else" or "in" or "is") || x.Kind == "op" && x.Text is "(" or "[" or "{" or "-" or "+");

    void Tuple(bool conditional)
    {
        Expression(conditional);
        while (_problem == null && IsOp(","))
        {
            _i++;
            if (!StartsExpression(Peek())) break;   // a trailing comma
            Expression(conditional);
        }
    }

    void Expression(bool conditional = true)
    {
        Or();
        if (!conditional) return;
        while (_problem == null && IsName("if"))
        {
            _i++;
            Or();
            if (IsName("else")) { _i++; Expression(); }
        }
    }

    void Or() { And(); while (_problem == null && IsName("or")) { _i++; And(); } }
    void And() { Not(); while (_problem == null && IsName("and")) { _i++; Not(); } }

    void Not()
    {
        if (IsName("not")) { _i++; Not(); return; }
        Compare();
    }

    void Compare()
    {
        Math1();
        while (_problem == null)
        {
            if (Peek() is { Kind: "op" } t && t.Text is "==" or "!=" or "<" or ">" or "<=" or ">=") { _i++; Math1(); }
            else if (IsName("in")) { _i++; Math1(); }
            else if (IsName("not") && IsName("in", 1)) { _i += 2; Math1(); }
            else break;
        }
    }

    void Math1() { Concat(); while (_problem == null && (IsOp("+") || IsOp("-"))) { _i++; Concat(); } }
    void Concat() { Math2(); while (_problem == null && IsOp("~")) { _i++; Math2(); } }
    void Math2() { Pow(); while (_problem == null && (IsOp("*") || IsOp("/") || IsOp("//") || IsOp("%"))) { _i++; Pow(); } }
    void Pow() { Unary(); while (_problem == null && IsOp("**")) { _i++; Unary(); } }

    void Unary(bool withFilter = true)
    {
        if (IsOp("-") || IsOp("+")) { _i++; Unary(false); }
        else Primary();
        if (_problem != null) return;
        Postfix();
        if (withFilter) FilterExpression();
    }

    void Primary()
    {
        var t = Peek();
        if (t == null) { Fail("an expression"); return; }
        switch (t.Value.Kind)
        {
            case "name" when t.Value.Text is "and" or "or" or "if" or "else" or "in" or "is":
                Fail("an expression");
                return;
            case "name" or "number":
                _i++;
                return;
            case "string":
                while (Peek() is { Kind: "string" }) _i++;   // "a" "b" is one string
                return;
        }
        if (IsOp("("))
        {
            _i++;
            if (IsOp(")")) { _i++; return; }
            Tuple(true);
            Expect(")");
            return;
        }
        if (IsOp("["))
        {
            _i++;
            while (_problem == null && !IsOp("]"))
            {
                Expression();
                if (!IsOp(",")) break;
                _i++;
            }
            Expect("]");
            return;
        }
        if (IsOp("{"))
        {
            _i++;
            while (_problem == null && !IsOp("}"))
            {
                Expression();
                if (!Expect(":")) return;
                Expression();
                if (!IsOp(",")) break;
                _i++;
            }
            Expect("}");
            return;
        }
        Fail("an expression");
    }

    void Postfix()
    {
        while (_problem == null)
        {
            if (IsOp("."))
            {
                _i++;
                if (Peek() is { Kind: "name" or "number" }) _i++;
                else { Fail("a name after '.'"); return; }
            }
            else if (IsOp("[")) { _i++; Subscript(); }
            else if (IsOp("(")) { _i++; Arguments(); }
            else return;
        }
    }

    void Subscript()
    {
        while (_problem == null)
        {
            // a slice: a:b:c, any part left out
            if (!IsOp(":")) Expression();
            while (_problem == null && IsOp(":")) { _i++; if (!IsOp(":") && !IsOp("]") && !IsOp(",")) Expression(); }
            if (!IsOp(",")) break;
            _i++;
        }
        Expect("]");
    }

    void Arguments()
    {
        while (_problem == null && !IsOp(")"))
        {
            if (IsOp("**") || IsOp("*")) _i++;
            else if (Peek() is { Kind: "name" } && IsOp("=", 1)) _i += 2;
            Expression();
            if (!IsOp(",")) break;
            _i++;
        }
        Expect(")");
    }

    void FilterExpression()
    {
        while (_problem == null)
        {
            if (IsOp("|"))
            {
                _i++;
                if (Peek() is not { Kind: "name" }) { Fail("a filter name after '|'"); return; }
                _i++;
                while (IsOp(".") && Peek(1) is { Kind: "name" }) _i += 2;
                if (IsOp("(")) { _i++; Arguments(); }
            }
            else if (IsName("is"))
            {
                _i++;
                if (IsName("not")) _i++;
                if (Peek() is not { Kind: "name" }) { Fail("a test name after 'is'"); return; }
                _i++;
                while (IsOp(".") && Peek(1) is { Kind: "name" }) _i += 2;
                if (IsOp("(")) { _i++; Arguments(); }
                // a test takes one argument without parentheses: `is divisibleby 3`, `is sameas false`
                else if (Peek() is { } next && (next.Kind is "string" or "number" || next.Kind == "name" && next.Text is not ("else" or "or" or "and" or "if" or "not" or "in" or "is") || next.Kind == "op" && next.Text is "(" or "[" or "{"))
                {
                    Primary();
                    Postfix();
                }
            }
            else if (IsOp("(")) { _i++; Arguments(); }
            else return;
        }
    }
}
