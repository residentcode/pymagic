using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>One inlay hint: Kind 1 is a type (`: int`, `-> str`), 2 a parameter name (`x:` before an argument).</summary>
public sealed record InlayHintItem(Position Position, string Label, int Kind, bool PaddingLeft, bool PaddingRight);

/// <summary>
/// Inlay hints (`pymagic/inlayHints`), as PyCharm shows them: the parameter names of positional arguments, the names of
/// `@pytest.mark.parametrize` values, the attribute names of positional class patterns in `match`, the inferred return
/// type of a function without one, the inferred type of a variable assigned something whose type does not show, and the
/// type of each call of a method chain written over several lines.
/// </summary>
public sealed partial class LanguageService
{
    public List<InlayHintItem> InlayHints(string uri, Range range)
    {
        var hints = new List<InlayHintItem>();
        var m = ModuleFor(uri);
        if (m == null) return hints;
        var lines = Lines(m);
        int from = lines.OffsetOf(range.Start), to = lines.OffsetOf(range.End);
        var ev = Current().Workspace.Evaluator;
        var seenNames = new HashSet<Symbol>();   // a variable gets its type once, where it is first assigned
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n.End < from || n.Start > to) continue;
            try
            {
                switch (n)
                {
                    case CallExpr call:
                        ParameterNameHints(ev, m, lines, call, hints);
                        ChainHint(ev, m, lines, call, hints);
                        break;
                    case FunctionDef fd:
                        ReturnTypeHint(ev, m, lines, fd, hints);
                        ParametrizeHints(m, lines, fd, hints);
                        break;
                    case AssignStmt { Targets: [NameExpr target], Value: var value }:
                        VariableTypeHint(ev, m, lines, target, value, seenNames, hints);
                        break;
                    case MatchClass mc:
                        PatternHints(ev, m, lines, mc, hints);
                        break;
                }
            }
            catch (Exception e) when (e is InvalidOperationException or ArgumentException or NullReferenceException or IndexOutOfRangeException)
            {
                // a hint that cannot be worked out is left out, never the others
            }
        }
        return hints.Where(h => lines.OffsetOf(h.Position) >= from && lines.OffsetOf(h.Position) <= to)
            .DistinctBy(h => (h.Position, h.Label)).OrderBy(h => h.Position.Line).ThenBy(h => h.Position.Character).ToList();
    }

    static bool HintableType(PyType t) => t is not UnknownType && t is not ModuleType;

    /// <summary>`name:` before each positional argument, for a callee with one signature and more than one positional parameter;
    /// not when the argument already says it (a name or attribute of that name).</summary>
    static void ParameterNameHints(TypeEvaluator ev, ModuleInfo m, LineMap lines, CallExpr call, List<InlayHintItem> hints)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        if (positional.Count == 0) return;
        var sigs = ev.CallSignatures(ev.TypeOf(m, call.Func));
        if (sigs.Count != 1) return;   // overloads: which one is meant is not certain
        var byPosition = sigs[0].Params.Where(p => p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal).ToList();
        if (byPosition.Count < 2) return;
        if (call.Args.Any(a => a.Stars == 1)) return;   // *args: the positions after it are not known
        for (int i = 0; i < positional.Count && i < byPosition.Count; i++)
        {
            string name = byPosition[i].Name;
            var value = positional[i].Value;
            if (name.StartsWith('_') || value is NameExpr ne && ne.Name == name || value is AttributeExpr ae && ae.Attr == name) continue;
            hints.Add(new InlayHintItem(lines.PositionOf(value.Start), name + ":", 2, false, true));
        }
    }

    /// <summary>`-> T` after the parameters of a function that has no return annotation (not `__init__`), when its return type is inferred.</summary>
    void ReturnTypeHint(TypeEvaluator ev, ModuleInfo m, LineMap lines, FunctionDef fd, List<InlayHintItem> hints)
    {
        if (fd.Returns != null || fd.Name == "__init__") return;
        var sym = m.Semantics.ScopeOf(fd)?.Parent?.Symbols.GetValueOrDefault(fd.Name);
        if (sym == null) return;
        PyType ret = ev.TypeOfSymbol(sym) switch
        {
            FunctionType f => ev.ReturnTypeOf(f),
            _ => new UnknownType(false, false),
        };
        if (!HintableType(ret)) return;
        int close = CloseOfParameters(m.Source, fd.NameStart + fd.Name.Length);
        if (close < 0) return;
        hints.Add(new InlayHintItem(lines.PositionOf(close + 1), "-> " + FormatType(ev, ret), 1, true, false));
    }

    /// <summary>Where the `)` that ends a def's parameters is, from the end of its name.</summary>
    static int CloseOfParameters(string source, int from)
    {
        int depth = 0;
        for (int i = from; i < source.Length; i++)
        {
            char c = source[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}')
            {
                depth--;
                if (depth == 0) return i;
            }
            else if (c == ':' && depth == 0) return -1;
        }
        return -1;
    }

    /// <summary>`: T` after a variable assigned for the first time, unless what is assigned shows its type already (a literal, a
    /// constructor call `Point(...)`, a string, a comprehension of a known kind).</summary>
    static void VariableTypeHint(TypeEvaluator ev, ModuleInfo m, LineMap lines, NameExpr target, Expr value, HashSet<Symbol> seen, List<InlayHintItem> hints)
    {
        var sym = m.Semantics.Resolve(target);
        if (sym == null || !seen.Add(sym)) return;
        if (value is ConstExpr or ListExpr or TupleExpr or DictExpr or SetExpr or LambdaExpr) return;
        if (value is CallExpr { Func: NameExpr or AttributeExpr } c && ev.TypeOf(m, c.Func) is ClassType { IsInstance: false }) return;
        var t = ev.TypeOf(m, target);
        if (!HintableType(t) || t is FunctionType or OverloadedType or ClassType { IsInstance: false }) return;
        hints.Add(new InlayHintItem(lines.PositionOf(target.End), ": " + FormatType(ev, t), 1, false, false));
    }

    /// <summary>The type of a call that ends a line of a method chain going on below (`.filter(...)\n    .order_by(...)`), at the end of that line.</summary>
    static void ChainHint(TypeEvaluator ev, ModuleInfo m, LineMap lines, CallExpr call, List<InlayHintItem> hints)
    {
        if (call.Func is not AttributeExpr) return;
        // the next thing after the call is a dot on a later line
        int i = call.End;
        bool newline = false;
        while (i < m.Source.Length && (char.IsWhiteSpace(m.Source[i]) || m.Source[i] == '\\'))
        {
            if (m.Source[i] == '\n') newline = true;
            i++;
        }
        if (!newline || i >= m.Source.Length || m.Source[i] != '.') return;
        var t = ev.TypeOf(m, call);
        if (!HintableType(t)) return;
        hints.Add(new InlayHintItem(lines.PositionOf(call.End), " " + FormatType(ev, t), 1, true, false));
    }

    /// <summary>The names of `@pytest.mark.parametrize("a, b", [(1, 2), ...])` before each value of the rows.</summary>
    static void ParametrizeHints(ModuleInfo m, LineMap lines, FunctionDef fd, List<InlayHintItem> hints)
    {
        foreach (var d in fd.Decorators)
        {
            if (d is not CallExpr { Func: AttributeExpr { Attr: "parametrize" } } c || c.Args.Count < 2) continue;
            var names = ParametrizeNames(c.Args[0].Value);
            if (names.Count == 0 || c.Args[1].Value is not (ListExpr or TupleExpr)) continue;
            var rows = c.Args[1].Value is ListExpr l ? l.Items : ((TupleExpr)c.Args[1].Value).Items;
            foreach (var row in rows)
            {
                var values = names.Count == 1 ? new[] { row } : row is TupleExpr t ? t.Items : row is ListExpr rl ? rl.Items : Array.Empty<Expr>();
                for (int k = 0; k < values.Count && k < names.Count; k++)
                    hints.Add(new InlayHintItem(lines.PositionOf(values[k].Start), names[k] + ":", 2, false, true));
            }
        }
    }

    /// <summary>The argument names of parametrize: "a, b" or ["a", "b"] or ("a", "b").</summary>
    static List<string> ParametrizeNames(Expr e)
    {
        if (e is ConstExpr { Kind: ConstKind.Str } s)
            return StringValue(s.Text).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
        var items = e is ListExpr l ? l.Items : e is TupleExpr t ? t.Items : null;
        return items?.OfType<ConstExpr>().Where(c => c.Kind == ConstKind.Str).Select(c => StringValue(c.Text).Trim()).ToList() ?? new List<string>();
    }

    /// <summary>The text of a plain string literal as written (`'a, b'` gives `a, b`).</summary>
    static string StringValue(string raw)
    {
        int q = raw.IndexOfAny(new[] { '\'', '"' });
        if (q < 0) return raw;
        string quote = raw.Length >= q + 3 && raw.Substring(q, 3) is "'''" or "\"\"\"" ? raw.Substring(q, 3) : raw[q].ToString();
        int start = q + quote.Length, end = raw.LastIndexOf(quote, StringComparison.Ordinal);
        return end > start ? raw[start..end] : "";
    }

    /// <summary>`case Point(1, 2):` shows `x:` and `y:` from the class's `__match_args__` (or a dataclass's fields, in order).</summary>
    static void PatternHints(TypeEvaluator ev, ModuleInfo m, LineMap lines, MatchClass mc, List<InlayHintItem> hints)
    {
        if (mc.Positional.Count == 0 || ev.TypeOf(m, mc.Cls) is not ClassType { IsInstance: false } cls) return;
        var names = MatchArgs(cls.Info.Node);
        for (int i = 0; i < mc.Positional.Count && i < names.Count; i++)
            hints.Add(new InlayHintItem(lines.PositionOf(mc.Positional[i].Start), names[i] + ":", 2, false, true));
    }

    static List<string> MatchArgs(ClassDef c)
    {
        foreach (var s in c.Body)
            if (s is AssignStmt { Targets: [NameExpr { Name: "__match_args__" }], Value: var v } && (v is TupleExpr or ListExpr))
                return (v is TupleExpr t ? t.Items : ((ListExpr)v).Items).OfType<ConstExpr>().Select(x => StringValue(x.Text)).ToList();
        bool fields = c.Decorators.Any(d => (d is CallExpr dc ? dc.Func : d) is NameExpr { Name: "dataclass" } or AttributeExpr { Attr: "dataclass" })
            || c.Bases.Any(b => b.Value is NameExpr { Name: "NamedTuple" } or AttributeExpr { Attr: "NamedTuple" });
        if (!fields) return new List<string>();
        return c.Body.OfType<AnnAssignStmt>().Where(a => a.Target is NameExpr && !(a.Annotation is SubscriptExpr { Value: NameExpr { Name: "ClassVar" } } or NameExpr { Name: "ClassVar" }))
            .Select(a => ((NameExpr)a.Target).Name).ToList();
    }
}
