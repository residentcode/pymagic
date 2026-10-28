using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>Inspections that need types: a coroutine nobody awaits, an @override that overrides nothing, an argument that repeats the default, and the fixes of the new inspections.</summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> MoreTypedDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            IEnumerable<Finding> findings = node switch
            {
                ExprStmt { Value: CallExpr call } => CoroutineNotAwaited(m, call, ev),
                FunctionDef f => OverrideMissing(m, f, ev),
                CallExpr call => EqualDefaults(m, call, ev),
                _ => Array.Empty<Finding>(),
            };
            foreach (var f in findings)
                if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
    }

    // ------------------------------------------------------------ coroutines nobody awaits

    static IEnumerable<Finding> CoroutineNotAwaited(ModuleInfo m, CallExpr call, TypeEvaluator ev)
    {
        if (ev.TypeOf(m, call) is not ClassType { IsInstance: true } ct || ct.Info.Name != "Coroutine") yield break;
        string name = call.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => "coroutine" };
        yield return new Finding(call.Start, Math.Max(call.End - call.Start, 1), $"Coroutine '{name}' is not awaited", "async-call", FindingLevel.Warning);
    }

    // ------------------------------------------------------------ @override

    static IEnumerable<Finding> OverrideMissing(ModuleInfo m, FunctionDef f, TypeEvaluator ev)
    {
        if (!f.Decorators.Any(d => d is NameExpr { Name: "override" } or AttributeExpr { Attr: "override" })) yield break;
        if (m.Semantics.ScopeOf(f)?.Parent is not { Kind: ScopeKind.Class, Node: ClassDef cd } || ClassInfoOf(m, cd, ev) is not { } info) yield break;
        if (KnownMro(info, ev) is not { } mro) yield break;
        if (mro.Skip(1).Any(c => c.Info.Scope.Symbols.ContainsKey(f.Name))) yield break;
        yield return new Finding(f.NameStart, f.Name.Length, $"Method '{f.Name}' is marked as an override but no base class has it", "override-missing", FindingLevel.Warning);
    }

    // ------------------------------------------------------------ arguments that repeat a default

    static bool SimpleConstant(Expr e) => e is ConstExpr { IsFString: false, Kind: ConstKind.Int or ConstKind.Float or ConstKind.Str or ConstKind.None or ConstKind.True or ConstKind.False };

    static string ConstantKey(ConstExpr c)
    {
        if (c.Kind != ConstKind.Str) return c.Kind + ":" + c.Text;
        int q = 0;
        while (q < c.Text.Length && c.Text[q] != '"' && c.Text[q] != '\'') q++;
        string rest = c.Text[q..];
        int n = rest.Length >= 6 && (rest.StartsWith("\"\"\"") || rest.StartsWith("'''")) ? 3 : 1;
        return "Str:" + c.Text[..q].ToLowerInvariant() + "|" + (rest.Length >= 2 * n ? rest[n..^n] : rest);
    }

    /// <summary>`f(x, retries=3)` where `retries` defaults to 3: the argument can go. Only plain constants, for functions and methods the evaluator resolves.</summary>
    static IEnumerable<Finding> EqualDefaults(ModuleInfo m, CallExpr call, TypeEvaluator ev)
    {
        if (call.Args.Any(a => a.Stars > 0)) yield break;
        if (ev.TypeOf(m, call.Func) is not FunctionType { Info.Node: FunctionDef def } ft) yield break;
        if ((ft.Info.Flags & FuncFlags.Overload) != 0) yield break;   // which overload is meant is a question of the arguments
        if (def.Decorators.Count > 0 && !ft.Info.IsStatic && !ft.Info.IsClassMethod) yield break;   // another decorator decides what the call means
        var ps = def.Params.ToList();
        var positional = ps.Where(p => p.Kind == ParamKind.Normal).ToList();
        // parameters after a bare `*` or *args are keyword-only
        int starAt = ps.FindIndex(p => p.Kind is ParamKind.VarArgs or ParamKind.KwOnlySeparator);
        var byPosition = (starAt < 0 ? ps : ps.Take(starAt)).Where(p => p.Kind == ParamKind.Normal).ToList();
        if (ft.BoundFirst && byPosition.Count > 0) byPosition.RemoveAt(0);

        var positionalArgs = call.Args.Where(a => a.Name == null).ToList();
        int lastPositional = positionalArgs.Count - 1;
        for (int i = 0; i < call.Args.Count; i++)
        {
            var arg = call.Args[i];
            Parameter? p = null;
            if (arg.Name != null) p = positional.Concat(ps.Where(x => x.Kind == ParamKind.Normal && !positional.Contains(x))).FirstOrDefault(x => x.Name == arg.Name);
            else
            {
                // a positional argument can only go when no positional one follows it
                int index = positionalArgs.IndexOf(arg);
                if (index != lastPositional || index >= byPosition.Count) continue;
                p = byPosition[index];
            }
            if (p?.Default == null || !SimpleConstant(p.Default) || !SimpleConstant(arg.Value)) continue;
            if (ConstantKey((ConstExpr)p.Default) != ConstantKey((ConstExpr)arg.Value)) continue;
            yield return new Finding(arg.Start, Math.Max(arg.End - arg.Start, 1), "Argument equals the default parameter value", "argument-equal-default", FindingLevel.Weak);
        }
    }

    // ------------------------------------------------------------ fixes

    bool MoreFix(string code, ModuleInfo m, string uri, int start, int end, Action<string, bool, Edit[]> add, List<CodeAction> actions)
    {
        string text = m.Source;
        switch (code)
        {
            case "missing-return":
            {
                // the function may end without a value: say so in the annotation
                var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => d.NameStart == start && d.Returns != null);
                if (f?.Returns is not { } r) return true;
                add("Make the return type optional", true, new[] { new Edit(r.End, r.End, " | None") });
                return true;
            }
            case "shadowing-names":
                RenameAction(m, uri, start, $"Rename to '{text[start..end]}_'", text[start..end] + "_", actions);
                return true;
            case "method-parameters":
            {
                var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => d.Params.Count > 0 && d.Params[0].Start <= start && start < d.Params[0].End + 1 && d.Start <= start);
                if (f == null) return true;
                bool classLevel = f.Decorators.Any(d => d is NameExpr { Name: "classmethod" }) || f.Name is "__new__" or "__init_subclass__" or "__class_getitem__";
                string wanted = classLevel ? "cls" : "self";
                if (f.Params.Skip(1).Any(p => p.Name == wanted)) return true;
                RenameAction(m, uri, start, $"Rename to '{wanted}'", wanted, actions);
                return true;
            }
            case "from-future":
            {
                var stmt = m.Ast.Body.FirstOrDefault(s => s.Start == start);
                if (stmt == null) return true;
                // after the docstring and the future imports that are already there
                int at = 0;
                foreach (var s in m.Ast.Body)
                {
                    if (ReferenceEquals(s, stmt)) break;
                    if (s is ImportFromStmt { Level: 0, Module: "__future__" } || (ReferenceEquals(s, m.Ast.Body[0]) && s is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } }))
                        at = LineEndInclusive(text, s.End);
                    else break;
                }
                string line = text[start..stmt.End].TrimEnd() + "\n";
                var delete = DeleteStatement(m, stmt);
                add("Move to the top of the file", true, new[] { new Edit(at, at, line), delete });
                return true;
            }
            case "override-missing":
            {
                var f = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(d => d.NameStart == start);
                var dec = f?.Decorators.FirstOrDefault(d => d is NameExpr { Name: "override" } or AttributeExpr { Attr: "override" });
                if (dec == null) return true;
                int ls = LineStart(text, dec.Start), le = LineEndInclusive(text, dec.End);
                int at = text.LastIndexOf('@', dec.Start, dec.Start - ls + 1);   // the decorator node starts after the @
                bool whole = at >= ls && text[ls..at].All(c => c is ' ' or '\t') && text[dec.End..le].All(c => c is ' ' or '\t' or '\r' or '\n');
                if (whole) add("Remove the @override decorator", true, new[] { new Edit(ls, le, "") });
                return true;
            }
            case "async-call":
            {
                var stmt = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().FirstOrDefault(s => s.Value.Start == start);
                if (stmt == null) return true;
                bool inAsync = false;
                for (var p = m.ParentOf(stmt); p != null; p = m.ParentOf(p))
                    if (p is FunctionDef fd) { inAsync = fd.IsAsync; break; }
                if (inAsync) add("Add 'await'", true, new[] { new Edit(start, start, "await ") });
                return true;
            }
            case "argument-equal-default":
            {
                var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Args.Any(a => a.Start == start));
                if (call == null) return true;
                int i = call.Args.ToList().FindIndex(a => a.Start == start);
                var arg = call.Args[i];
                Edit edit = call.Args.Count == 1 ? new Edit(arg.Start, arg.End, "")
                    : i + 1 < call.Args.Count ? new Edit(arg.Start, call.Args[i + 1].Start, "")
                    : new Edit(call.Args[i - 1].End, arg.End, "");
                add("Remove the argument", true, new[] { edit });
                return true;
            }
            case "docstring-parameter" or "docstring-missing-parameter":
                DocstringFix(code, m, start, end, add);
                return true;
            case "dataclass-fields" or "namedtuple-fields" or "typeddict-keys" or "overload-structure":
                StructureFix(code, m, start, add);
                return true;
        }
        return false;
    }

    static readonly Regex SphinxLine = new(@"^[ \t]*:(?:param|parameter|arg|argument|key|keyword|type)\b[^\n]*$", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>Sphinx style only: take out the lines of a parameter the function does not have, or add a line for one the docstring leaves out.</summary>
    void DocstringFix(string code, ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        var def = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(f =>
            f.Body.Count > 0 && f.Body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } d } && (code == "docstring-parameter" ? d.Start <= start && start < d.End : f.Params.Any(p => p.Start <= start && start < p.End + 1)));
        if (def == null || def.Body[0] is not ExprStmt { Value: ConstExpr doc }) return;
        string name = text[start..end];
        string raw = text[doc.Start..doc.End];
        var lines = SphinxLine.Matches(raw).Cast<Match>().ToList();

        if (code == "docstring-parameter")
        {
            var mine = lines.Where(l => Regex.IsMatch(l.Value, @":(?:param|parameter|arg|argument|key|keyword|type)\s+(?:[^:\n]*?\s+)?\**" + Regex.Escape(name) + @"\s*:")).ToList();
            if (mine.Count == 0) return;
            add($"Remove '{name}' from the docstring", true, mine.OrderByDescending(l => l.Index).Select(l =>
            {
                int s = doc.Start + l.Index, e = doc.Start + l.Index + l.Length;
                if (e < text.Length && text[e] == '\r') e++;
                if (e < text.Length && text[e] == '\n') e++;
                return new Edit(s, e, "");
            }).ToArray());
            return;
        }

        var parameterLines = lines.Where(l => Regex.IsMatch(l.Value, @":(?:param|parameter|arg|argument)\b")).ToList();
        if (parameterLines.Count == 0) return;
        var last = parameterLines[^1];
        string indent = Regex.Match(last.Value, @"^[ \t]*").Value;
        int insertAt = doc.Start + last.Index + last.Length;
        if (insertAt < text.Length && text[insertAt] == '\r') insertAt++;
        add($"Document parameter '{name}'", true, new[] { new Edit(insertAt, insertAt, $"\n{indent}:param {name}:") });
    }
}
