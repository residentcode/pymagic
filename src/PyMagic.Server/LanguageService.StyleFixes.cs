using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Quick fixes for the inspections of <see cref="StyleInspections"/>.</summary>
public sealed partial class LanguageService
{
    void StyleFix(string code, ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        string nl = text.Contains("\r\n") ? "\r\n" : "\n";
        T? Find<T>(Func<T, bool> pick) where T : Node => AstChildren.Descendants(m.Ast).OfType<T>().FirstOrDefault(pick);
        string Slice(Node n) => text[n.Start..n.End];
        string IndentOf(int offset)
        {
            int ls = LineStart(text, offset), i = ls;
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            return text[ls..i];
        }

        switch (code)
        {
            case "trailing-semicolon":
                if (start < text.Length && text[start] == ';')
                {
                    int from = start;
                    while (from > 0 && text[from - 1] == ' ') from--;
                    add("Remove the trailing semicolon", true, new[] { new Edit(from, start + 1, "") });
                }
                break;

            case "statement-effect":
            {
                var stmt = Find<ExprStmt>(s => s.Value.Start == start);
                if (stmt != null) add("Remove the statement", false, new[] { DeleteStatement(m, stmt) });
                break;
            }

            case "redundant-parentheses":
                if (start < text.Length && text[start] == '(' && end <= text.Length && text[end - 1] == ')')
                {
                    string gap = start > 0 && !char.IsWhiteSpace(text[start - 1]) ? " " : "";
                    add("Remove the redundant parentheses", true, new[] { new Edit(start, start + 1, gap), new Edit(end - 1, end, "") });
                }
                break;

            case "chained-comparison":
            {
                var conj = Find<BinaryExpr>(b => b.Start == start && b.Op == "and");
                if (conj is { Left: CompareExpr l, Right: CompareExpr r })
                    add("Simplify the chained comparison", true, new[] { new Edit(conj.Start, conj.End, $"{Slice(l.Left)} {l.Ops[0]} {Slice(l.Comparators[0])} {r.Ops[0]} {Slice(r.Comparators[0])}") });
                break;
            }

            case "simplify-boolean":
            {
                var cmp = Find<CompareExpr>(c => c.Start == start && c.Ops.Count == 1);
                if (cmp == null) break;
                bool leftIsBool = cmp.Left is ConstExpr { Kind: ConstKind.True or ConstKind.False };
                var value = leftIsBool ? cmp.Comparators[0] : cmp.Left;
                var constant = (ConstExpr)(leftIsBool ? cmp.Left : cmp.Comparators[0]);
                bool wantsTrue = (constant.Kind == ConstKind.True) == (cmp.Ops[0] == "==");
                string inner = Slice(value);
                bool simple = value is NameExpr or AttributeExpr or CallExpr or SubscriptExpr;
                string result = wantsTrue ? inner : "not " + (simple ? inner : "(" + inner + ")");
                if (wantsTrue && value is BinaryExpr or CompareExpr or IfExpr or LambdaExpr && m.ParentOf(cmp) is not (IfStmt or WhileStmt or AssertStmt)) result = "(" + inner + ")";
                add(wantsTrue ? "Compare the value itself" : "Use 'not'", true, new[] { new Edit(cmp.Start, cmp.End, result) });
                break;
            }

            case "augment-assignment":
            {
                var assign = Find<AssignStmt>(a => a.Start == start);
                if (assign is { Targets: [NameExpr target], Value: BinaryExpr { Op: var op } bin })
                    add($"Replace with '{target.Name} {op}= ...'", true, new[] { new Edit(assign.Start, assign.End, $"{target.Name} {op}= {Slice(bin.Right)}") });
                break;
            }

            case "set-literal":
            {
                var call = Find<CallExpr>(c => c.Start == start);
                if (call is { Args: [{ Value: var arg }] })
                {
                    string slice = Slice(arg).Trim();
                    if (slice.Length >= 2 && slice[0] is '[' or '(' && slice[^1] is ']' or ')')
                    {
                        string inner = slice[1..^1];
                        if (!inner.Contains('\n')) inner = inner.TrimEnd().TrimEnd(',');
                        add("Replace with a set literal", true, new[] { new Edit(call.Start, call.End, "{" + inner + "}") });
                    }
                }
                break;
            }

            case "method-may-be-static":
            {
                var f = Find<FunctionDef>(d => d.NameStart == start);
                if (f == null || f.Params.Count == 0 || f.Params[0].Name != "self" || f.Decorators.Count > 0) break;
                var self = f.Params[0];
                int selfEnd = self.End;
                int next = selfEnd;
                while (next < text.Length && text[next] == ' ') next++;
                if (next < text.Length && text[next] == ',') { next++; while (next < text.Length && text[next] == ' ') next++; selfEnd = next; }
                int lineStart = LineStart(text, f.Start);
                add("Make the method static", true, new[]
                {
                    new Edit(lineStart, lineStart, IndentOf(f.Start) + "@staticmethod" + nl),
                    new Edit(self.Start, selfEnd, ""),
                });
                break;
            }

            case "invalid-escape":
                if (start < text.Length && text[start] == '\\')
                    add("Escape the backslash", true, new[] { new Edit(start, start, "\\") });
                break;

            case "unnecessary-backslash":
                if (start < text.Length && text[start] == '\\')
                    add("Remove the backslash", true, new[] { new Edit(start, start + 1, "") });
                break;

            case "pep8-whitespace":
                if (start == text.Length - 1 && text[start] != '\n' && text[start] != '\r') add("Add a newline at the end of the file", true, new[] { new Edit(text.Length, text.Length, nl) });
                else if (end <= text.Length && text[start..end].All(ch => ch is '\n' or '\r') && end == text.Length) add("Remove the blank lines at the end of the file", true, new[] { new Edit(start, end, nl) });
                else if (end <= text.Length && text[start..end].All(ch => ch is ' ' or '\t')) add("Remove the trailing whitespace", true, new[] { new Edit(start, end, "") });
                break;

            case "pep8-comments":
            {
                if (start >= text.Length || text[start] != '#') break;
                int spaces = 0;
                while (start - 1 - spaces >= 0 && text[start - 1 - spaces] == ' ') spaces++;
                bool inline = text[LineStart(text, start)..start].Trim().Length > 0;
                var edits = new List<Edit>();
                if (inline && spaces < 2) edits.Add(new Edit(start, start, new string(' ', 2 - spaces)));
                if (start + 1 < text.Length && text[start + 1] is not (' ' or '#' or ':' or '!' or '\n' or '\r')) edits.Add(new Edit(start + 1, start + 1, " "));
                if (edits.Count > 0) add("Fix the comment spacing", true, edits.ToArray());
                break;
            }

            case "pep8-statements":
                PepStatementFix(m, text, nl, start, end, add);
                break;

            case "pep8-blank-lines":
                BlankLinesFix(text, nl, start, add);
                break;
        }
    }

    void PepStatementFix(ModuleInfo m, string text, string nl, int start, int end, Action<string, bool, Edit[]> add)
    {
        string indent = "";
        {
            int ls = LineStart(text, Math.Min(start, text.Length)), i = ls;
            while (i < text.Length && text[i] is ' ' or '\t') i++;
            indent = text[ls..i];
        }
        string step = indent.Contains('\t') ? "\t" : "    ";

        // E702: `a; b`
        if (start < text.Length && text[start] == ';')
        {
            int after = start + 1;
            while (after < text.Length && text[after] == ' ') after++;
            add("Put the statements on separate lines", true, new[] { new Edit(start, after, nl + indent) });
            return;
        }

        // E401: `import a, b`
        var imp = AstChildren.Descendants(m.Ast).OfType<ImportStmt>().FirstOrDefault(s => s.Start == start);
        if (imp != null)
        {
            var lines = imp.Names.Select(n => "import " + n.Name + (n.AsName != null ? " as " + n.AsName : ""));
            add("Put each import on its own line", true, new[] { new Edit(imp.Start, imp.End, string.Join(nl + indent, lines)) });
            return;
        }

        // E731: `f = lambda x: x + 1`
        var assign = AstChildren.Descendants(m.Ast).OfType<AssignStmt>().FirstOrDefault(s => s.Start == start && s.Value is LambdaExpr);
        if (assign is { Targets: [NameExpr name], Value: LambdaExpr lam })
        {
            string head = text[lam.Start..lam.Body.Start];
            int colon = head.LastIndexOf(':');
            if (colon < 0) return;
            string parameters = head["lambda".Length..colon].Trim();
            string body = text[lam.Body.Start..lam.Body.End];
            add("Convert the lambda to a def", true, new[] { new Edit(assign.Start, assign.End, $"def {name.Name}({parameters}):{nl}{indent}{step}return {body}") });
            return;
        }

        // E701: `if x: y`
        int colonAt = start - 1;
        while (colonAt >= 0 && text[colonAt] == ' ') colonAt--;
        if (colonAt >= 0 && text[colonAt] == ':')
            add("Move the statement to its own line", true, new[] { new Edit(colonAt + 1, start, nl + indent + step) });
    }

    /// <summary>Sets the blank lines above the line at <paramref name="start"/> to what PEP 8 wants: two before top-level definitions and after them, one between methods, at most two (one inside).</summary>
    void BlankLinesFix(string text, string nl, int start, Action<string, bool, Edit[]> add)
    {
        var starts = new List<int> { 0 };
        for (int i = 0; i < text.Length; i++) if (text[i] == '\n') starts.Add(i + 1);
        int line = starts.BinarySearch(start);
        if (line < 0) line = ~line - 1;
        string LineText(int l) => text[starts[l]..(l + 1 < starts.Count ? starts[l + 1] : text.Length)].TrimEnd('\r', '\n');
        bool Blank(int l) => LineText(l).Trim().Length == 0;
        bool Comment(int l) => LineText(l).TrimStart().StartsWith('#');

        string current = LineText(line);
        bool topLevel = current.Length > 0 && !char.IsWhiteSpace(current[0]);
        int allowed = topLevel ? 2 : 1;

        // E303: too many right above the line
        int b = line;
        while (b - 1 >= 0 && Blank(b - 1)) b--;
        if (line - b > allowed)
        {
            add("Remove the extra blank lines", true, new[] { new Edit(starts[b], starts[line], string.Concat(Enumerable.Repeat(nl, allowed))) });
            return;
        }

        // too few: counted above the comments that belong to the line
        int k = line;
        while (k - 1 >= 0 && Comment(k - 1)) k--;
        int top = k;
        while (top - 1 >= 0 && Blank(top - 1)) top--;
        if (top == 0 && k == line && top == k) return;
        int wanted = topLevel ? 2 : 1;
        if (k - top >= wanted) return;
        add($"Add the blank lines PEP 8 asks for ({wanted})", true, new[] { new Edit(starts[top], starts[k], string.Concat(Enumerable.Repeat(nl, wanted))) });
    }
}
