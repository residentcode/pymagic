using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Findings of <see cref="CodeInspections"/> as diagnostics, `# noqa` and `# noinspection` comments, and the fixes for them.</summary>
public sealed partial class LanguageService
{
    static readonly Regex NoQa = new(@"#\s*noqa\b(?:\s*:\s*(?<codes>[\w\-]+(?:\s*,\s*[\w\-]+)*))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NoInspection = new(@"#\s*noinspection\b", RegexOptions.Compiled);

    IEnumerable<Diag> InspectionDiagnostics(ModuleInfo m)
    {
        foreach (var f in CodeInspections.Run(m))
        {
            if (Suppressed(m.Source, f)) continue;
            yield return new Diag(new Diagnostic(f.Start, f.Length, f.Message),
                f.Level switch { FindingLevel.Error => Severity.Error, FindingLevel.Warning => Severity.Warning, _ => Severity.Hint },
                f.Level == FindingLevel.Unused ? new[] { DiagnosticTag.Unnecessary } : null, f.Code);
        }
    }

    /// <summary>A comment on the line says to leave it alone: `# noqa`, `# noqa: code, code` or `# noinspection ...`.</summary>
    static bool Suppressed(string text, Finding f)
    {
        int ls = LineStart(text, Math.Min(f.Start, text.Length));
        int le = ls;
        while (le < text.Length && text[le] != '\n' && text[le] != '\r') le++;
        string line = text[ls..le];
        int hash = line.IndexOf('#');
        if (hash < 0) return false;
        string comment = line[hash..];
        if (NoInspection.IsMatch(comment)) return true;
        var m = NoQa.Match(comment);
        if (!m.Success) return false;
        return !m.Groups["codes"].Success
            || m.Groups["codes"].Value.Split(',').Any(c => string.Equals(c.Trim(), f.Code, StringComparison.OrdinalIgnoreCase));
    }

    void InspectionFixes(ModuleInfo m, string code, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        switch (code)
        {
            case "none-comparison" or "is-literal":
            {
                var cmp = AstChildren.Descendants(m.Ast).OfType<CompareExpr>().FirstOrDefault(c => c.Start == start);
                if (cmp == null) break;
                var edits = new List<Edit>();
                for (int i = 0; i < cmp.Ops.Count; i++)
                {
                    var left = i == 0 ? cmp.Left : cmp.Comparators[i - 1];
                    var right = cmp.Comparators[i];
                    string op = cmp.Ops[i];
                    string? replacement = code == "none-comparison"
                        ? op switch { "==" when IsNoneConst(left) || IsNoneConst(right) => "is", "!=" when IsNoneConst(left) || IsNoneConst(right) => "is not", _ => null }
                        : op switch { "is" => "==", "is not" => "!=", _ => null };
                    if (replacement == null || CodeInspections.OperatorSpan(m, cmp, i) is not { } span) continue;
                    edits.Add(new Edit(span.Start, span.End, replacement));
                }
                if (edits.Count > 0)
                    add(code == "none-comparison" ? "Replace equality comparison with 'is' / 'is not'" : "Replace 'is' with '=='", true, edits.ToArray());
                break;
            }
            case "bare-except":
                if (start + 6 <= text.Length && text.AsSpan(start, 6).SequenceEqual("except"))
                    add("Replace with 'except Exception:'", true, new[] { new Edit(start + 6, start + 6, " Exception") });
                break;
            case "assert-tuple":
                if (start < text.Length && text[start] == '(' && end <= text.Length && text[end - 1] == ')')
                    add("Remove the parentheses", true, new[] { new Edit(start, start + 1, ""), new Edit(end - 1, end, "") });
                break;
            case "self-assignment":
            {
                var stmt = AstChildren.Descendants(m.Ast).OfType<AssignStmt>().FirstOrDefault(s => s.Start == start);
                if (stmt != null) add("Remove the assignment", true, new[] { DeleteStatement(m, stmt) });
                break;
            }
            case "missing-self":
            {
                var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => d.NameStart == start);
                if (f == null) break;
                int open = text.IndexOf('(', f.NameStart + f.Name.Length);
                if (open < 0) break;
                add("Add 'self' parameter", true, new[] { new Edit(open + 1, open + 1, f.Params.Count == 0 ? "self" : "self, ") });
                break;
            }
            case "mutable-default":
                MutableDefaultFix(m, start, add);
                break;
            case "unused-variable":
                UnusedVariableFix(m, start, add);
                break;
            case "redeclared":
            {
                Stmt? second = AstChildren.Descendants(m.Ast).OfType<Stmt>().FirstOrDefault(s =>
                    s is FunctionDef f && f.NameStart == start || s is ClassDef c && c.NameStart == start);
                if (second == null || m.ParentOf(second) is not { } parent || BodyContaining(parent, second) is not { } body) break;
                string name = second is FunctionDef sf ? sf.Name : ((ClassDef)second).Name;
                var first = body.TakeWhile(s => !ReferenceEquals(s, second)).LastOrDefault(s =>
                    s is FunctionDef f && f.Name == name || s is ClassDef c && c.Name == name);
                if (first != null) add($"Remove the first definition of '{name}'", false, new[] { DeleteStatement(m, first) });
                break;
            }
        }
    }

    static bool IsNoneConst(Expr e) => e is ConstExpr { Kind: ConstKind.None };

    /// <summary>`def f(a=[])` becomes `def f(a=None)` with `if a is None: a = []` at the top of the body.</summary>
    void MutableDefaultFix(ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var p = AstChildren.Descendants(m.Ast).OfType<Parameter>().FirstOrDefault(x => x.Default != null && x.Default.Start == start);
        if (p?.Default == null || m.ParentOf(p) is not FunctionDef f || f.Body.Count == 0) return;
        string text = m.Source;

        // after a docstring, before anything else; the statement has to begin its line (no `def f(a=[]): return a`)
        int index = f.Body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } } && f.Body.Count > 1 ? 1 : 0;
        var anchor = f.Body[index];
        int at = index == 1 ? LineEndInclusive(text, f.Body[0].End) : LineStart(text, anchor.Start);
        if (text[LineStart(text, anchor.Start)..anchor.Start].Any(c => c is not (' ' or '\t'))) return;
        if (index == 0 && at != LineStart(text, anchor.Start)) return;

        string indent = LineIndent(text, anchor.Start);
        string step = indent.Contains('\t') ? "\t" : "    ";
        string original = text[p.Default.Start..p.Default.End];
        string guard = $"{indent}if {p.Name} is None:\n{indent}{step}{p.Name} = {original}\n";
        add($"Use None as the default of '{p.Name}'", true, new[] { new Edit(p.Default.Start, p.Default.End, "None"), new Edit(at, at, guard) });
    }

    /// <summary>`x = f()` becomes `f()` (the call still happens); `x = 1` goes away.</summary>
    void UnusedVariableFix(ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var name = AstChildren.Descendants(m.Ast).OfType<NameExpr>().FirstOrDefault(n => n.Start == start);
        if (name == null) return;
        Stmt? stmt; Expr? value;
        switch (m.ParentOf(name))
        {
            case AssignStmt { Targets.Count: 1 } a: stmt = a; value = a.Value; break;
            case AnnAssignStmt { Value: not null } a: stmt = a; value = a.Value; break;
            default: return;
        }
        bool effect = HasSideEffect(value);
        string title = $"Remove unused variable '{name.Name}'";
        if (!effect) add(title, true, new[] { DeleteStatement(m, stmt) });
        else add(title, true, new[] { new Edit(stmt.Start, stmt.End, m.Source[value.Start..value.End]) });
    }

    static bool HasSideEffect(Expr value) =>
        value is CallExpr or AwaitExpr or YieldExpr or NamedExpr
        || AstChildren.Descendants(value).Any(n => n is CallExpr or AwaitExpr or YieldExpr or NamedExpr);
}
