using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Findings of <see cref="CodeInspections"/> as diagnostics, `# noqa` and `# noinspection` comments, and the fixes for them.</summary>
public sealed partial class LanguageService
{
    static readonly Regex NoQa = new(@"#\s*noqa\b(?:\s*:\s*(?<codes>[\w\-]+(?:\s*,\s*[\w\-]+)*))?", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly Regex NoInspection = new(@"#\s*noinspection\b", RegexOptions.Compiled);

    IEnumerable<Diag> InspectionDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var f in CodeInspections.Run(m))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        foreach (var d in ClassDiagnostics(m, ev)) yield return d;
        foreach (var d in MoreTypedDiagnostics(m, ev)) yield return d;
        foreach (var d in StructureDiagnostics(m, ev)) yield return d;
        foreach (var d in TypeCheckDiagnostics(m, ev)) yield return d;
        foreach (var d in FixtureDiagnostics(m, ev)) yield return d;
        foreach (var d in DjangoDiagnostics(m, ev)) yield return d;
        foreach (var d in WebRouteDiagnostics(m, ev)) yield return d;
        foreach (var d in FastApiDiagnostics(m, ev)) yield return d;
        foreach (var d in MetaDiagnostics(m, ev, Current())) yield return d;
        foreach (var d in ViewDiagnostics(m, ev)) yield return d;
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

    void InspectionFixes(Snapshot snap, ModuleInfo m, string uri, string code, int start, int end, Action<string, bool, Edit[]> add, List<CodeAction> actions)
    {
        string text = m.Source;
        switch (code)
        {
            case "naming-class" or "naming-function" or "naming-argument" or "naming-variable" or "shadowed-builtin":
            {
                string name = text[start..end];
                string renamed = code switch
                {
                    "naming-class" => CodeInspections.ToCapWords(name),
                    "shadowed-builtin" => name + "_",
                    _ => CodeInspections.ToSnakeCase(name),
                };
                if (renamed != name) RenameAction(m, uri, start, $"Rename to '{renamed}'", renamed, actions);
                break;
            }
            case "unused-argument":
            {
                string name = text[start..end];
                var def = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(f => f.Params.Any(p => p.Start == start && p.Name == name));
                if (def == null) break;
                try
                {
                    var pos = Lines(m).PositionOf(def.NameStart);
                    var keep = GetSignature(uri, pos).Parameters.Where(p => p.Name != name).ToList();
                    var result = ChangeSignature(uri, pos, null, keep);
                    actions.Add(new CodeAction($"Remove parameter '{name}'", QuickFixKind, new WorkspaceEdit(result.Changes), false));
                }
                catch (ServiceException) { /* the signature cannot be changed safely: no fix is offered */ }
                break;
            }
            case "missing-super-init":
            {
                var own = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(f => f.NameStart == start);
                if (own == null || m.ParentOf(own) is not ClassDef cd || ClassInfoOf(m, cd, snap.Workspace.Evaluator) is not { } info) break;
                if (SuperInitMissing(m, cd, info, snap.Workspace.Evaluator) is not { } baseClass) break;
                if (BodyStart(m, own) is not { } spot) break;
                add("Add a call to super().__init__()", true, new[] { new Edit(spot.At, spot.At, $"{spot.Indent}{SuperInitCall(baseClass, own)}\n") });
                break;
            }
            case "unimplemented-abstract":
                ImplementAbstractFix(snap, m, start, add);
                break;
            case "route-path-parameter":
                AddPathParameterFix(m, start, end, add);
                break;
            case "fixture-not-found":
                CreateFixtureFix(m, start, add);
                break;

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
            case "depends-called" or "async-blocking-call":
                FastApiFix(code, m, start, end, add);
                break;
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
            default:
                if (!MoreFix(code, m, uri, start, end, add, actions)) StyleFix(code, m, start, end, add);
                break;
        }
    }

    static bool IsNoneConst(Expr e) => e is ConstExpr { Kind: ConstKind.None };

    /// <summary>`def f(a=[])` becomes `def f(a=None)` with `if a is None: a = []` at the top of the body.</summary>
    void MutableDefaultFix(ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var p = AstChildren.Descendants(m.Ast).OfType<Parameter>().FirstOrDefault(x => x.Default != null && x.Default.Start == start);
        if (p?.Default == null || m.ParentOf(p) is not FunctionDef f || f.Body.Count == 0) return;
        string text = m.Source;
        if (BodyStart(m, f) is not { } spot) return;
        string indent = spot.Indent, step = indent.Contains('\t') ? "\t" : "    ";
        int at = spot.At;
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

    /// <summary>Where a statement can be put at the top of a function body: after the docstring, at the body's indentation.
    /// Null for a body that shares the `def` line (`def f(): return 1`), where inserting a line would not be valid.</summary>
    static (int At, string Indent)? BodyStart(ModuleInfo m, FunctionDef f)
    {
        if (f.Body.Count == 0) return null;
        string text = m.Source;
        var first = f.Body[0];
        if (text[LineStart(text, first.Start)..first.Start].Any(c => c is not (' ' or '\t'))) return null;
        if (first is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str, IsFString: false } })
        {
            int after = LineEndInclusive(text, first.End);
            if (text[first.End..after].Any(c => c is not (' ' or '\t' or '\r' or '\n'))) return null;
            if (after == first.End || text[after - 1] is not ('\n' or '\r')) return null;   // the docstring ends the file: nothing to insert before
            return (after, LineIndent(text, first.Start));
        }
        return (LineStart(text, first.Start), LineIndent(text, first.Start));
    }

    void RenameAction(ModuleInfo m, string uri, int start, string title, string newName, List<CodeAction> actions)
    {
        try { actions.Add(new CodeAction(title, QuickFixKind, Rename(uri, Lines(m).PositionOf(start), newName), false)); }
        catch (ServiceException) { /* clashes with another name, or lives in a library: no fix */ }
    }

    /// <summary>`def name(...): raise NotImplementedError` for every abstract method still open, at the end of the class.</summary>
    void ImplementAbstractFix(Snapshot snap, ModuleInfo m, int start, Action<string, bool, Edit[]> add)
    {
        var ev = snap.Workspace.Evaluator;
        var cd = AstChildren.Descendants(m.Ast).OfType<ClassDef>().FirstOrDefault(c => c.NameStart == start);
        if (cd == null || ClassInfoOf(m, cd, ev) is not { } info || cd.Body.Count == 0) return;
        var missing = UnimplementedAbstract(cd, info, ev);
        if (missing.Count == 0) return;

        string text = m.Source;
        var first = cd.Body[0];
        if (text[LineStart(text, first.Start)..first.Start].Any(c => c is not (' ' or '\t'))) return;   // `class A(B): pass`
        string indent = LineIndent(text, first.Start);
        int last = cd.End;   // a class ends after its last line break: step back so the insertion goes right after that line
        while (last > first.Start && text[last - 1] is '\n' or '\r') last--;
        int at = LineEndInclusive(text, last);
        var sb = new System.Text.StringBuilder();
        if (at == 0 || text[at - 1] is not ('\n' or '\r')) sb.Append('\n');
        foreach (var a in missing)
        {
            sb.Append('\n');
            foreach (var line in StubFor(a).Split('\n')) sb.Append(indent).Append(line).Append('\n');
        }
        add(missing.Count == 1 ? $"Implement abstract method '{missing[0].Name}'" : "Implement abstract methods", true, new[] { new Edit(at, at, sb.ToString()) });
    }

    static bool HasSideEffect(Expr value) =>
        value is CallExpr or AwaitExpr or YieldExpr or NamedExpr
        || AstChildren.Descendants(value).Any(n => n is CallExpr or AwaitExpr or YieldExpr or NamedExpr);
}
