using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// Intention actions (item 236): rewrites offered at the cursor without a diagnostic, as PyCharm's Alt+Enter lists them. Each is a text
/// edit built from the syntax tree; the page shows them with the quick fixes under "Rewrite".
/// </summary>
public sealed partial class LanguageService
{
    public const string RewriteKind = "refactor.rewrite";

    void IntentionActions(Workspace workspace, IEnumerable<ModuleInfo> projectModules, ModuleInfo m, TypeEvaluator ev, int rs, int re, Action<string, Edit[]> add)
    {
        string text = m.Source;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        string Slice(Node n) => text[n.Start..n.End];
        var all = AstChildren.Descendants(m.Ast).ToList();
        bool Covers(Node n) => n.Start <= rs && re <= n.End;
        // the innermost nodes that contain the cursor, innermost first
        var around = all.Where(Covers).OrderBy(n => n.End - n.Start).ToList();
        T? Nearest<T>() where T : Node => around.OfType<T>().FirstOrDefault();
        string Indent(int offset) { int ls = LineStart(text, offset), i = ls; while (i < text.Length && text[i] is ' ' or '\t') i++; return text[ls..i]; }
        string UnitOf(Stmt s, IReadOnlyList<Stmt> body) => body.Count > 0 && Indent(body[0].Start).Length > Indent(s.Start).Length ? Indent(body[0].Start)[Indent(s.Start).Length..] : "    ";
        FString(m, around, text, Slice, add);
        Comparisons(around, text, Slice, add);
        Conditionals(m, around, text, nl, Slice, Indent, UnitOf, rs, add);
        DictCalls(around, text, Slice, add);
        ComprehensionLoop(m, around, text, nl, Slice, Indent, add);
        Imports(workspace, m, all, around, text, Slice, add);
        TypeHints(m, ev, around, text, rs, add);
        DocstringStub(projectModules, m, around, text, nl, Indent, rs, add);
        NumberBases(around, text, add);
    }

    static bool IsPlainString(Expr e, out string prefix, out char quote, out string body)
    {
        prefix = ""; quote = '"'; body = "";
        if (e is not ConstExpr { Kind: ConstKind.Str, IsFString: false } c) return false;
        var m = Regex.Match(c.Text, "^([rRuU]?)(\"\"\"|'''|\"|')(.*)\\2$", RegexOptions.Singleline);
        if (!m.Success || m.Groups[2].Value.Length == 3) return false;          // one literal, one quote
        prefix = m.Groups[1].Value;
        quote = m.Groups[2].Value[0];
        body = m.Groups[3].Value;
        return !prefix.Contains('r', StringComparison.OrdinalIgnoreCase) && !body.Contains('\\');   // escapes would change meaning when braces are doubled
    }

    /// <summary>`{expr}` field of an f-string: the expression may not hold the string's own quote, a backslash or a comment.</summary>
    static bool FitsInFString(string expr, char quote) => !expr.Contains(quote) && !expr.Contains('\\') && !expr.Contains('#') && !expr.Contains('\n');

    static string FieldOf(string expr, string conversion = "", string spec = "")
    {
        string e = expr.StartsWith('{') ? " " + expr : expr;          // `{{` would open a literal brace
        return "{" + e + (conversion.Length > 0 ? "!" + conversion : "") + (spec.Length > 0 ? ":" + spec : "") + "}";
    }

    static bool NeedsFieldParens(Expr e) => e is LambdaExpr or NamedExpr or YieldExpr;

    static string Braces(string literal) => literal.Replace("{", "{{").Replace("}", "}}");

    // ------------------------------------------------------------ `%`, `.format()` and `+` made an f-string

    static readonly Regex PercentSpec = new(@"%(\((?<key>[^)]*)\))?(?<flags>[-#0 +]*)(?<width>\*|\d+)?(\.(?<prec>\*|\d+))?(?<len>[hlL])?(?<type>[diouxXeEfFgGcrsa%])", RegexOptions.Compiled);

    void FString(ModuleInfo m, List<Node> around, string text, Func<Node, string> slice, Action<string, Edit[]> add)
    {
        // "..." % x
        if (around.OfType<BinaryExpr>().FirstOrDefault(b => b.Op == "%" && IsPlainString(b.Left, out _, out _, out _)) is { } pct
            && IsPlainString(pct.Left, out var pp, out var pq, out var pbody) && !pp.Contains('u', StringComparison.OrdinalIgnoreCase))
        {
            var args = pct.Right is TupleExpr t ? t.Items.ToList() : new List<Expr> { pct.Right };
            var named = pct.Right as DictExpr;
            var sb = new StringBuilder();
            int next = 0, last = 0;
            bool ok = true;
            foreach (Match s in PercentSpec.Matches(pbody))
            {
                sb.Append(Braces(pbody[last..s.Index]));
                last = s.Index + s.Length;
                string type = s.Groups["type"].Value;
                if (type == "%") { sb.Append('%'); continue; }
                if (s.Groups["width"].Value == "*" || s.Groups["prec"].Value == "*") { ok = false; break; }
                Expr? value;
                if (s.Groups["key"].Success)
                {
                    int k = named == null ? -1 : named.Keys.ToList().FindIndex(key => key is ConstExpr { Kind: ConstKind.Str, IsFString: false } kc && kc.Text.Trim('"', '\'') == s.Groups["key"].Value);
                    value = k < 0 ? null : named!.Values[k];
                }
                else value = next < args.Count && named == null ? args[next++] : null;
                if (value == null) { ok = false; break; }
                string expr = slice(value);
                if (NeedsFieldParens(value)) expr = "(" + expr + ")";
                if (!FitsInFString(expr, pq)) { ok = false; break; }
                string flags = s.Groups["flags"].Value, width = s.Groups["width"].Value, prec = s.Groups["prec"].Value;
                string conv = type is "r" or "a" ? type : "";
                string kind = type is "d" or "i" or "u" ? (prec.Length > 0 ? "d" : "") : type is "s" or "r" or "a" ? "" : type == "c" ? "c" : type;
                // zero fill and the sign go in front; `-` is `<`, which strings and numbers need alike
                string align = flags.Contains('-') ? "<" : type is "s" or "r" or "a" && width.Length > 0 ? ">" : "";
                string spec = (flags.Contains('#') ? "#" : "") + (flags.Contains('+') ? "+" : flags.Contains(' ') ? " " : "") + (flags.Contains('0') && !flags.Contains('-') ? "0" : "") + width + (prec.Length > 0 ? "." + prec : "") + kind;
                if (align != "") spec = align + spec;
                else if (flags.Contains('0') && width.Length == 0) { ok = false; break; }
                if (type is "d" or "i" or "u" && prec.Length > 0) { ok = false; break; }
                sb.Append(FieldOf(expr, conv, spec));
            }
            sb.Append(Braces(pbody[last..]));
            if (ok && (named != null || next == args.Count) && !(named == null && next == 0 && !PercentSpec.IsMatch(pbody) && args.Count > 0))
                add("Convert to an f-string", new[] { new Edit(pct.Start, pct.End, "f" + pq + sb + pq) });
        }

        // "...".format(...)
        if (around.OfType<CallExpr>().FirstOrDefault(c => c.Func is AttributeExpr { Attr: "format", Value: ConstExpr { Kind: ConstKind.Str } }) is { } call
            && call.Func is AttributeExpr { Value: ConstExpr fc } && IsPlainString(fc, out var fp, out var fq, out var fbody) && !call.Args.Any(a => a.Stars > 0))
        {
            var positional = call.Args.Where(a => a.Name == null).Select(a => a.Value).ToList();
            var keywords = call.Args.Where(a => a.Name != null).ToDictionary(a => a.Name!, a => a.Value);
            var sb = new StringBuilder();
            int auto = 0, i = 0;
            bool ok = true, usedNumbers = false;
            var used = new HashSet<int>();
            while (i < fbody.Length && ok)
            {
                char ch = fbody[i];
                if (ch == '{' && i + 1 < fbody.Length && fbody[i + 1] == '{') { sb.Append("{{"); i += 2; continue; }
                if (ch == '}' && i + 1 < fbody.Length && fbody[i + 1] == '}') { sb.Append("}}"); i += 2; continue; }
                if (ch != '{') { if (ch == '}') ok = false; sb.Append(ch); i++; continue; }
                int close = fbody.IndexOf('}', i);
                if (close < 0) { ok = false; break; }
                var field = Regex.Match(fbody[(i + 1)..close], @"^(?<name>[A-Za-z_]\w*|\d*)(?<rest>(\.[A-Za-z_]\w*|\[\w+\])*)(!(?<conv>[rsa]))?(:(?<spec>[^{}]*))?$");
                if (!field.Success) { ok = false; break; }
                string name = field.Groups["name"].Value;
                Expr? value;
                if (name.Length == 0) { value = auto < positional.Count ? positional[auto] : null; used.Add(auto); auto++; }
                else if (char.IsDigit(name[0])) { int n = int.Parse(name, CultureInfo.InvariantCulture); usedNumbers = true; value = n < positional.Count ? positional[n] : null; used.Add(n); }
                else value = keywords.GetValueOrDefault(name);
                if (value == null) { ok = false; break; }
                string expr = slice(value);
                if (NeedsFieldParens(value) || field.Groups["rest"].Length > 0 && value is BinaryExpr or CompareExpr or IfExpr or TupleExpr) expr = "(" + expr + ")";
                expr += field.Groups["rest"].Value;
                if (!FitsInFString(expr, fq) || field.Groups["spec"].Value.Contains(fq)) { ok = false; break; }
                sb.Append(FieldOf(expr, field.Groups["conv"].Value == "s" ? "" : field.Groups["conv"].Value, field.Groups["spec"].Value));
                i = close + 1;
            }
            if (ok && positional.Count == used.Count && (auto == 0 || !usedNumbers))
                add("Convert to an f-string", new[] { new Edit(call.Start, call.End, "f" + fq + sb + fq) });
        }

        // "a" + x + "b" (the outermost chain around the cursor)
        var chain = around.OfType<BinaryExpr>().Where(b => b.Op == "+").LastOrDefault(b => ParentIsNotPlus(m, b));
        if (chain != null)
        {
            var parts = new List<Expr>();
            void Flatten(Expr e) { if (e is BinaryExpr { Op: "+" } b) { Flatten(b.Left); Flatten(b.Right); } else parts.Add(e); }
            Flatten(chain);
            var strings = parts.Where(p => p is ConstExpr { Kind: ConstKind.Str } c && !c.IsFString).ToList();
            if (strings.Count > 0 && strings.All(p => IsPlainString(p, out _, out _, out _)) && parts.Count > 1 && parts.Count(p => p is ConstExpr { Kind: ConstKind.Str }) == strings.Count)
            {
                IsPlainString(strings[0], out _, out var quote, out _);
                var sb = new StringBuilder();
                bool ok = true;
                foreach (var p in parts)
                {
                    if (p is ConstExpr c && IsPlainString(p, out _, out var q, out var body))
                    {
                        if (q != quote && (body.Contains(quote) || q == quote)) { ok = false; break; }
                        sb.Append(Braces(body));
                        continue;
                    }
                    // str(x) is x in a field
                    Expr inner = p is CallExpr { Func: NameExpr { Name: "str" }, Args: [{ Name: null, Stars: 0 } only] } ? only.Value : p;
                    string expr = slice(inner);
                    if (NeedsFieldParens(inner)) expr = "(" + expr + ")";
                    if (!FitsInFString(expr, quote)) { ok = false; break; }
                    sb.Append(FieldOf(expr));
                }
                if (ok) add("Convert to an f-string", new[] { new Edit(chain.Start, chain.End, "f" + quote + sb + quote) });
            }
        }
    }

    bool ParentIsNotPlus(ModuleInfo m, BinaryExpr b) => m.ParentOf(b) is not BinaryExpr { Op: "+" };

    // ------------------------------------------------------------ a comparison flipped or negated, De Morgan's laws

    static readonly Dictionary<string, string> Mirror = new() { ["<"] = ">", [">"] = "<", ["<="] = ">=", [">="] = "<=", ["=="] = "==", ["!="] = "!=", ["is"] = "is", ["is not"] = "is not" };
    static readonly Dictionary<string, string> Opposite = new() { ["<"] = ">=", [">"] = "<=", ["<="] = ">", [">="] = "<", ["=="] = "!=", ["!="] = "==", ["is"] = "is not", ["is not"] = "is", ["in"] = "not in", ["not in"] = "in" };

    static bool NeedsParens(Expr e) => e is BinaryExpr or CompareExpr or IfExpr or LambdaExpr or NamedExpr or YieldExpr or UnaryExpr { Op: "not" };

    /// <summary>The negation of an expression as text: `not a` becomes `a`, a comparison its opposite, anything else gets `not`.</summary>
    static string Negated(Expr e, Func<Node, string> slice)
    {
        if (e is UnaryExpr { Op: "not", Operand: var inner }) return slice(inner);
        if (e is CompareExpr { Ops.Count: 1 } c && Opposite.TryGetValue(c.Ops[0], out var opp))
            return $"{slice(c.Left)} {opp} {slice(c.Comparators[0])}";
        return "not " + (NeedsParens(e) ? "(" + slice(e) + ")" : slice(e));
    }

    void Comparisons(List<Node> around, string text, Func<Node, string> slice, Action<string, Edit[]> add)
    {
        if (around.OfType<CompareExpr>().FirstOrDefault(c => c.Ops.Count == 1) is { } cmp)
        {
            string op = cmp.Ops[0];
            if (Mirror.TryGetValue(op, out var mirrored))
                add($"Flip the comparison ('{op}' to '{mirrored}')", new[] { new Edit(cmp.Start, cmp.End, $"{slice(cmp.Comparators[0])} {mirrored} {slice(cmp.Left)}") });
            if (Opposite.TryGetValue(op, out var opposite))
                add($"Negate the comparison ('{op}' to '{opposite}')", new[] { new Edit(cmp.Start, cmp.End, $"{slice(cmp.Left)} {opposite} {slice(cmp.Comparators[0])}") });
        }
        // not (a and b)  =>  not a or not b
        if (around.OfType<UnaryExpr>().FirstOrDefault(u => u.Op == "not" && u.Operand is BinaryExpr { Op: "and" or "or" }) is { } not && not.Operand is BinaryExpr bin)
        {
            var terms = new List<Expr>();
            void Flat(Expr e) { if (e is BinaryExpr b && b.Op == bin.Op) { Flat(b.Left); Flat(b.Right); } else terms.Add(e); }
            Flat(bin);
            string other = bin.Op == "and" ? "or" : "and";
            add($"Apply De Morgan's law ('not ({bin.Op})' to '{other}')", new[] { new Edit(not.Start, not.End, string.Join($" {other} ", terms.Select(t => Negated(t, slice)))) });
        }
        // not a or not b  =>  not (a and b)
        if (around.OfType<BinaryExpr>().Where(b => b.Op is "and" or "or").LastOrDefault() is { } top)
        {
            var terms = new List<Expr>();
            void Flat(Expr e) { if (e is BinaryExpr b && b.Op == top.Op) { Flat(b.Left); Flat(b.Right); } else terms.Add(e); }
            Flat(top);
            if (terms.Count > 1 && terms.All(t => t is UnaryExpr { Op: "not" } || t is CompareExpr { Ops.Count: 1 } c && Opposite.ContainsKey(c.Ops[0])) && terms.Any(t => t is UnaryExpr))
            {
                string other = top.Op == "and" ? "or" : "and";
                add($"Apply De Morgan's law ('{top.Op}' to 'not ({other})')", new[] { new Edit(top.Start, top.End, "not (" + string.Join($" {other} ", terms.Select(t => Negated(t, slice))) + ")") });
            }
        }
    }

    // ------------------------------------------------------------ if statements and conditional expressions

    void Conditionals(ModuleInfo m, List<Node> around, string text, string nl, Func<Node, string> slice, Func<int, string> indent, Func<Stmt, IReadOnlyList<Stmt>, string> unit, int cursor, Action<string, Edit[]> add)
    {
        // the if at the cursor is the innermost one whose header (up to the end of its test) holds it, not one the cursor is only inside of
        var ifStmt = around.OfType<IfStmt>().FirstOrDefault(s => cursor <= s.Test.End);
        if (ifStmt != null)
        {
            bool elif = ifStmt.Orelse is [IfStmt e] && IsElif(text, e);
            // an if with an else: the branches swapped, the test negated
            if (ifStmt.Orelse.Count > 0 && !elif && ifStmt.Body.Count > 0)
            {
                string b = text[ifStmt.Body[0].Start..ifStmt.Body[^1].End], o = text[ifStmt.Orelse[0].Start..ifStmt.Orelse[^1].End];
                add("Invert the if (swap its branches)", new[]
                {
                    new Edit(ifStmt.Test.Start, ifStmt.Test.End, Negated(ifStmt.Test, slice)),
                    new Edit(ifStmt.Body[0].Start, ifStmt.Body[^1].End, o),
                    new Edit(ifStmt.Orelse[0].Start, ifStmt.Orelse[^1].End, b),
                });
            }
            // if a: if b: X  =>  if a and b: X
            if (ifStmt.Orelse.Count == 0 && ifStmt.Body is [IfStmt inner] && inner.Orelse.Count == 0 && inner.Body.Count > 0
                && !IsElif(text, inner))
            {
                string pad = indent(inner.Body[0].Start), at = indent(inner.Start);
                int cut = pad.Length - at.Length;
                string body = text[LineStart(text, inner.Body[0].Start)..IntentionLineEnd(text, inner.Body[^1].End)];
                var lines = body.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).Select(l => l.Length >= cut && l[..cut].Trim().Length == 0 ? l[cut..] : l);
                string and(Expr e) => e is BinaryExpr { Op: "or" } or IfExpr or LambdaExpr or NamedExpr ? "(" + slice(e) + ")" : slice(e);
                add("Merge the nested if statements", new[] { new Edit(ifStmt.Start, IntentionLineEnd(text, inner.Body[^1].End), $"if {and(ifStmt.Test)} and {and(inner.Test)}:{nl}{string.Join(nl, lines)}") });
            }
            // if a and b: X  =>  if a: if b: X
            if (ifStmt.Orelse.Count == 0 && ifStmt.Test is BinaryExpr { Op: "and" } conj && ifStmt.Body.Count > 0 && !IsElif(text, ifStmt))
            {
                string step = unit(ifStmt, ifStmt.Body), at = indent(ifStmt.Start);
                string body = text[LineStart(text, ifStmt.Body[0].Start)..IntentionLineEnd(text, ifStmt.Body[^1].End)];
                var lines = body.Split('\n').Select(l => l.EndsWith('\r') ? l[..^1] : l).Select(l => l.Trim().Length == 0 ? l : step + l);
                add("Split the condition into nested if statements", new[] { new Edit(ifStmt.Start, IntentionLineEnd(text, ifStmt.Body[^1].End), $"if {slice(conj.Left)}:{nl}{at}{step}if {slice(conj.Right)}:{nl}{string.Join(nl, lines)}") });
            }
            // if c: x = a else: x = b  =>  x = a if c else b
            if (ifStmt.Orelse.Count == 1 && !elif && ifStmt.Body.Count == 1)
            {
                string? joined = Joined(ifStmt, slice);
                if (joined != null) add("Convert the if statement to a conditional expression", new[] { new Edit(ifStmt.Start, IntentionLineEnd(text, ifStmt.Orelse[^1].End), joined) });
            }
        }

        // x = a if c else b  =>  if c: x = a else: x = b
        foreach (var s in around.OfType<Stmt>().Take(1))
        {
            if (s is AssignStmt { Targets: [var target], Value: IfExpr cond } && !HasLineBreakAround(text, s))
            {
                string at = indent(s.Start);
                add("Convert the conditional expression to an if statement", new[] { new Edit(s.Start, s.End, $"if {slice(cond.Test)}:{nl}{at}    {slice(target)} = {slice(cond.Body)}{nl}{at}else:{nl}{at}    {slice(target)} = {slice(cond.Orelse)}") });
                break;
            }
            if (s is ReturnStmt { Value: IfExpr rc } && !HasLineBreakAround(text, s))
            {
                string at = indent(s.Start);
                add("Convert the conditional expression to an if statement", new[] { new Edit(s.Start, s.End, $"if {slice(rc.Test)}:{nl}{at}    return {slice(rc.Body)}{nl}{at}else:{nl}{at}    return {slice(rc.Orelse)}") });
                break;
            }
        }
    }

    static int IntentionLineEnd(string text, int o) { while (o < text.Length && text[o] != '\n' && text[o] != '\r') o++; return o; }
    static bool HasLineBreakAround(string text, Stmt s) => text[s.Start..s.End].Contains('\n');
    /// <summary>An `elif` is an IfStmt too, whose first token is the keyword `elif`.</summary>
    static bool IsElif(string text, IfStmt s) => string.CompareOrdinal(text, s.Start, "elif", 0, 4) == 0;

    static string? Joined(IfStmt s, Func<Node, string> slice)
    {
        switch (s.Body[0], s.Orelse[0])
        {
            case (AssignStmt { Targets: [var t1], Value: var v1 }, AssignStmt { Targets: [var t2], Value: var v2 }) when slice(t1) == slice(t2):
                return $"{slice(t1)} = {slice(v1)} if {slice(s.Test)} else {slice(v2)}";
            case (ReturnStmt { Value: { } r1 }, ReturnStmt { Value: { } r2 }):
                return $"return {slice(r1)} if {slice(s.Test)} else {slice(r2)}";
        }
        return null;
    }

    // ------------------------------------------------------------ {'a': 1} and dict(a=1)

    static readonly Regex Identifier = new(@"^[A-Za-z_]\w*$", RegexOptions.Compiled);

    void DictCalls(List<Node> around, string text, Func<Node, string> slice, Action<string, Edit[]> add)
    {
        if (around.OfType<DictExpr>().FirstOrDefault() is { } dict && dict.Keys.Count > 0 && dict.Keys.All(k => k is ConstExpr { Kind: ConstKind.Str, IsFString: false } kc && IsPlainString(kc, out _, out _, out var body) && Identifier.IsMatch(body) && !PythonKeywords.Contains(body)))
        {
            var parts = dict.Keys.Select((k, i) => { IsPlainString(k!, out _, out _, out var body); return $"{body}={slice(dict.Values[i])}"; });
            add("Convert the dict literal to a dict() call", new[] { new Edit(dict.Start, dict.End, "dict(" + string.Join(", ", parts) + ")") });
        }
        if (around.OfType<CallExpr>().FirstOrDefault(c => c.Func is NameExpr { Name: "dict" } && c.Args.Count > 0 && c.Args.All(a => a.Name != null && a.Stars == 0)) is { } call)
        {
            var parts = call.Args.Select(a => $"'{a.Name}': {slice(a.Value)}");
            add("Convert the dict() call to a dict literal", new[] { new Edit(call.Start, call.End, "{" + string.Join(", ", parts) + "}") });
        }
    }

    static readonly HashSet<string> PythonKeywords = new()
    {
        "False", "None", "True", "and", "as", "assert", "async", "await", "break", "class", "continue", "def", "del", "elif", "else", "except", "finally", "for",
        "from", "global", "if", "import", "in", "is", "lambda", "nonlocal", "not", "or", "pass", "raise", "return", "try", "while", "with", "yield",
    };

    // ------------------------------------------------------------ a comprehension made a for loop

    void ComprehensionLoop(ModuleInfo m, List<Node> around, string text, string nl, Func<Node, string> slice, Func<int, string> indent, Action<string, Edit[]> add)
    {
        var assign = around.OfType<AssignStmt>().FirstOrDefault(a => a.Targets is [NameExpr] && a.Value is CompExpr { Kind: not CompKind.Generator });
        if (assign is not { Value: CompExpr comp } || assign.Targets[0] is not NameExpr target) return;
        string at = indent(assign.Start);
        var sb = new StringBuilder();
        string init = comp.Kind switch { CompKind.List => "[]", CompKind.Set => "set()", _ => "{}" };
        string body = comp.Kind switch
        {
            CompKind.List => $"{target.Name}.append({slice(comp.Element)})",
            CompKind.Set => $"{target.Name}.add({slice(comp.Element)})",
            _ => $"{target.Name}[{slice(comp.Element)}] = {slice(comp.Value!)}",
        };
        sb.Append($"{target.Name} = {init}");
        string pad = at;
        foreach (var g in comp.Generators)
        {
            sb.Append($"{nl}{pad}{(g.IsAsync ? "async " : "")}for {slice(g.Target)} in {slice(g.Iter)}:");
            pad += "    ";
            foreach (var cond in g.Ifs) { sb.Append($"{nl}{pad}if {slice(cond)}:"); pad += "    "; }
        }
        sb.Append($"{nl}{pad}{body}");
        add("Convert the comprehension to a for loop", new[] { new Edit(assign.Start, assign.End, sb.ToString()) });
    }

    // ------------------------------------------------------------ imports

    void Imports(Workspace workspace, ModuleInfo m, List<Node> all, List<Node> around, string text, Func<Node, string> slice, Action<string, Edit[]> add)
    {
        // import a.b  =>  from a import b (its uses `a.b` become `b`)
        if (around.OfType<ImportStmt>().FirstOrDefault() is { Names: [{ Name: var dotted, AsName: null }] } imp && dotted.Contains('.'))
        {
            int dot = dotted.LastIndexOf('.');
            string pkg = dotted[..dot], leaf = dotted[(dot + 1)..];
            var uses = all.OfType<AttributeExpr>().Where(a => a.Attr == leaf && DottedOf(a.Value) == pkg && !text[a.Start..a.End].Contains('\n')).ToList();
            bool clash = all.OfType<NameExpr>().Any(n => n.Name == leaf);
            if (!clash && !all.OfType<ImportStmt>().Any(i => !ReferenceEquals(i, imp) && i.Names.Any(n => n.Name == pkg || n.Name.StartsWith(pkg + ".", StringComparison.Ordinal))))
            {
                var edits = new List<Edit> { new(imp.Start, imp.End, $"from {pkg} import {leaf}") };
                edits.AddRange(uses.Select(u => new Edit(u.Start, u.End, leaf)));
                add($"Convert to 'from {pkg} import {leaf}'", edits.ToArray());
            }
        }
        // from a import b  =>  import a.b (b has to be a module; its uses `b` become `a.b`)
        if (around.OfType<ImportFromStmt>().FirstOrDefault() is { Level: 0, Module: { } module, IsStar: false, Names: [{ Name: var leaf2, AsName: null }] } from && workspace.GetModule(module + "." + leaf2) != null)
        {
            var uses = all.OfType<NameExpr>().Where(n => n.Name == leaf2 && n.Start >= from.End).ToList();
            bool clash = all.OfType<NameExpr>().Any(n => n.Name == module.Split('.')[0]);
            if (!clash)
            {
                var edits = new List<Edit> { new(from.Start, from.End, $"import {module}.{leaf2}") };
                edits.AddRange(uses.Select(u => new Edit(u.Start, u.End, $"{module}.{leaf2}")));
                add($"Convert to 'import {module}.{leaf2}'", edits.ToArray());
            }
        }
        // relative <=> absolute
        if (around.OfType<ImportFromStmt>().FirstOrDefault() is { } rel)
        {
            string[] package = (m.IsPackage ? m.Name : m.Name.Contains('.') ? m.Name[..m.Name.LastIndexOf('.')] : "").Split('.', StringSplitOptions.RemoveEmptyEntries);
            string headTail = text[rel.Start..rel.End];
            var head = Regex.Match(headTail, @"^from\s+(\.*)([\w.]*)\s+import\b");
            if (head.Success && rel.Level > 0 && rel.Level - 1 <= package.Length)
            {
                var basePackage = package.Take(package.Length - (rel.Level - 1)).ToList();
                if (basePackage.Count > 0)
                {
                    string absolute = string.Join(".", basePackage.Concat((rel.Module ?? "").Split('.', StringSplitOptions.RemoveEmptyEntries)));
                    if (absolute.Length > 0)
                        add("Convert to an absolute import", new[] { new Edit(rel.Start + head.Groups[1].Index, rel.Start + head.Groups[2].Index + head.Groups[2].Length, absolute) });
                }
            }
            else if (head.Success && rel.Level == 0 && rel.Module != null && package.Length > 0)
            {
                string[] target = rel.Module.Split('.');
                int common = 0;
                while (common < package.Length && common < target.Length && package[common] == target[common]) common++;
                if (common > 0 && common < target.Length + (target.Length == common ? 1 : 0))
                {
                    string dots = new('.', 1 + (package.Length - common));
                    string rest = string.Join(".", target.Skip(common));
                    add("Convert to a relative import", new[] { new Edit(rel.Start + head.Groups[1].Index, rel.Start + head.Groups[2].Index + head.Groups[2].Length, dots + rest) });
                }
            }
        }
    }

    static string? DottedOf(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr { Value: var v } a when DottedOf(v) is { } head => head + "." + a.Attr, _ => null };

    // ------------------------------------------------------------ a type hint from the inferred type

    void TypeHints(ModuleInfo m, TypeEvaluator ev, List<Node> around, string text, int cursor, Action<string, Edit[]> add)
    {
        // a variable: `x = 5`  =>  `x: int = 5`
        if (around.OfType<AssignStmt>().FirstOrDefault() is { Targets: [NameExpr target] } assign && m.Semantics.Resolve(target) is { } sym)
        {
            var t = ev.TypeOf(m, target);
            if (HintableType(t) && t is not (FunctionType or OverloadedType) && !(t is ClassType { IsInstance: false }) && cursor <= assign.Value.Start)
                add($"Add the type hint ': {FormatType(ev, t)}'", new[] { new Edit(target.End, target.End, ": " + FormatType(ev, t)) });
        }
        if (around.OfType<FunctionDef>().FirstOrDefault() is { } fd && cursor < (fd.Body.Count > 0 ? fd.Body[0].Start : fd.End))
        {
            // a parameter with a default: `a=1`  =>  `a: int = 1`
            foreach (var p in fd.Params.Where(p => p.Annotation == null && p.Default != null && p.Start <= cursor && cursor <= p.End))
            {
                var t = ev.TypeOf(m, p.Default!);
                if (HintableType(t) && t is not (FunctionType or OverloadedType) && !(t is ClassType { IsInstance: false }) && !(t is ClassType { Info.Name: "NoneType" }))
                {
                    string raw = text[p.Start..p.End];
                    int eq = raw.IndexOf('=');
                    if (eq > 0) add($"Add the type hint ': {FormatType(ev, t)}'", new[] { new Edit(p.Start, p.End, $"{p.Name}: {FormatType(ev, t)} = {text[p.Default!.Start..p.Default.End]}") });
                }
            }
            // the return type: `def f():`  =>  `def f() -> int:`
            if (fd.Returns == null && fd.Name != "__init__")
            {
                var sym2 = m.Semantics.ScopeOf(fd)?.Parent?.Symbols.GetValueOrDefault(fd.Name);
                if (sym2 != null && ev.TypeOfSymbol(sym2) is FunctionType f && ev.ReturnTypeOf(f) is { } ret && HintableType(ret))
                {
                    int close = CloseOfParameters(text, fd.NameStart + fd.Name.Length);
                    if (close >= 0) add($"Add the return type '-> {FormatType(ev, ret)}'", new[] { new Edit(close + 1, close + 1, " -> " + FormatType(ev, ret)) });
                }
            }
        }
    }

    // ------------------------------------------------------------ a docstring stub in the project's format

    void DocstringStub(IEnumerable<ModuleInfo> projectModules, ModuleInfo m, List<Node> around, string text, string nl, Func<int, string> indent, int cursor, Action<string, Edit[]> add)
    {
        var def = around.OfType<FunctionDef>().FirstOrDefault();
        if (def == null || def.Body.Count == 0 || Docstrings.Of(def.Body) != null || cursor >= def.Body[0].Start) return;
        string format = DocstringFormat(projectModules, m);
        var names = def.Params.Where(p => p.Name is not ("self" or "cls") && p.Kind is not (ParamKind.KwOnlySeparator or ParamKind.Slash)).ToList();
        bool returns = AstChildren.Descendants(def).OfType<ReturnStmt>().Any(r => r.Value != null);
        bool raises = AstChildren.Descendants(def).OfType<RaiseStmt>().Any(r => r.Exc != null);
        string pad = indent(def.Body[0].Start);
        var lines = new List<string> { "TODO: describe " + def.Name + "." };
        if (format == "rest")
        {
            lines.Add("");
            foreach (var p in names) lines.Add($":param {p.Name}: ");
            if (returns) lines.Add(":return: ");
        }
        else if (format == "numpy")
        {
            if (names.Count > 0) { lines.Add(""); lines.Add("Parameters"); lines.Add("----------"); foreach (var p in names) { lines.Add($"{p.Name} : "); lines.Add("    "); } }
            if (returns) { lines.Add(""); lines.Add("Returns"); lines.Add("-------"); lines.Add("    "); }
        }
        else
        {
            if (names.Count > 0) { lines.Add(""); lines.Add("Args:"); foreach (var p in names) lines.Add($"    {p.Name}: "); }
            if (returns) { lines.Add(""); lines.Add("Returns:"); lines.Add("    "); }
            if (raises) { lines.Add(""); lines.Add("Raises:"); lines.Add("    "); }
        }
        string doc = lines.Count == 1 ? $"{pad}\"\"\"{lines[0]}\"\"\"{nl}" : $"{pad}\"\"\"{lines[0]}{nl}" + string.Join(nl, lines.Skip(1).Select(l => l.Length == 0 ? "" : pad + l)) + $"{nl}{pad}\"\"\"{nl}";
        add($"Insert a docstring stub ({(format == "rest" ? "reST" : format == "numpy" ? "NumPy" : "Google")} style)", new[] { new Edit(LineStart(text, def.Body[0].Start), LineStart(text, def.Body[0].Start), doc) });
    }

    /// <summary>The docstring style the project already uses (reST `:param`, NumPy `Parameters` underlined, else Google `Args:`).</summary>
    string DocstringFormat(IEnumerable<ModuleInfo> projectModules, ModuleInfo own)
    {
        int rest = 0, numpy = 0, google = 0;
        foreach (var module in new[] { own }.Concat(projectModules.Where(x => !x.IsStub && !ReferenceEquals(x, own)).Take(300)))
            foreach (var f in AstChildren.Descendants(module.Ast).OfType<FunctionDef>())
                if (Docstrings.Of(f.Body) is { } doc)
                {
                    if (doc.Contains(":param ") || doc.Contains(":return")) rest++;
                    else if (Regex.IsMatch(doc, @"^(Parameters|Returns)\n-{3,}", RegexOptions.Multiline)) numpy++;
                    else if (Regex.IsMatch(doc, @"^(Args|Arguments|Returns|Raises):", RegexOptions.Multiline)) google++;
                }
        return rest > numpy && rest > google ? "rest" : numpy > google ? "numpy" : "google";
    }

    // ------------------------------------------------------------ a number in another base

    void NumberBases(List<Node> around, string text, Action<string, Edit[]> add)
    {
        if (around.OfType<ConstExpr>().FirstOrDefault(c => c.Kind == ConstKind.Int) is not { } num) return;
        string literal = text[num.Start..num.End].Replace("_", "");
        System.Numerics.BigInteger value;
        try
        {
            value = literal.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? System.Numerics.BigInteger.Parse("0" + literal[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : literal.StartsWith("0o", StringComparison.OrdinalIgnoreCase) ? literal[2..].Aggregate(System.Numerics.BigInteger.Zero, (a, c) => a * 8 + (c - '0'))
                : literal.StartsWith("0b", StringComparison.OrdinalIgnoreCase) ? literal[2..].Aggregate(System.Numerics.BigInteger.Zero, (a, c) => a * 2 + (c - '0'))
                : System.Numerics.BigInteger.Parse(literal, CultureInfo.InvariantCulture);
        }
        catch (FormatException) { return; }
        string kind = literal.Length > 1 && literal[0] == '0' && char.IsLetter(literal[1]) ? char.ToLowerInvariant(literal[1]).ToString() : "d";
        string Binary(System.Numerics.BigInteger v) { if (v.IsZero) return "0"; var sb = new StringBuilder(); while (v > 0) { sb.Insert(0, (char)('0' + (int)(v % 2))); v /= 2; } return sb.ToString(); }
        string Octal(System.Numerics.BigInteger v) { if (v.IsZero) return "0"; var sb = new StringBuilder(); while (v > 0) { sb.Insert(0, (char)('0' + (int)(v % 8))); v /= 8; } return sb.ToString(); }
        if (kind != "d") add($"Convert {text[num.Start..num.End]} to decimal ({value})", new[] { new Edit(num.Start, num.End, value.ToString(CultureInfo.InvariantCulture)) });
        if (kind != "x") add($"Convert to hexadecimal (0x{value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(1, '0')})", new[] { new Edit(num.Start, num.End, "0x" + value.ToString("X", CultureInfo.InvariantCulture).TrimStart('0').PadLeft(1, '0')) });
        if (kind != "o") add($"Convert to octal (0o{Octal(value)})", new[] { new Edit(num.Start, num.End, "0o" + Octal(value)) });
        if (kind != "b") add($"Convert to binary (0b{Binary(value)})", new[] { new Edit(num.Start, num.End, "0b" + Binary(value)) });
    }
}
