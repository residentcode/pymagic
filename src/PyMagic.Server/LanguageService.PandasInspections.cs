using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's two pandas inspections (PyCharm Professional's "Pandas" group): `list(s.values)` (PyCharm's own case), `s.values.tolist()` and
/// `s.to_numpy().tolist()` on a Series where `s.to_list()` says it (PyPandasSeriesToListInspection), and a DataFrame, Series or Index in a place that asks for its truth value (`if df:`, `not s`,
/// `s > 0 and ...`), which pandas answers with "ValueError: The truth value of a DataFrame is ambiguous" (PyPandasTruthValueIsAmbiguousInspection).
/// The types come from pandas itself or pandas-stubs, whichever the interpreter has.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> PandasDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains("pandas", StringComparison.Ordinal) && !m.Source.Contains("pd.", StringComparison.Ordinal)) yield break;
        foreach (var f in PandasFindings(m, ev))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>"DataFrame", "Series" or "Index" when the type is an instance of that pandas class (or of a subclass); a union only when every member is one.</summary>
    static string? PandasKind(TypeEvaluator ev, PyType t)
    {
        if (t is UnionType u)
        {
            var kinds = u.MemberList.Select(x => PandasKind(ev, x)).Distinct().ToList();
            return kinds.Count == 1 ? kinds[0] : null;
        }
        if (t is not ClassType { IsInstance: true } c) return null;
        foreach (var b in ev.MroOf(c.Info))
            if (b is ClassType { Info: var i } && i.Name is "DataFrame" or "Series" or "Index" && i.Module.Name.StartsWith("pandas", StringComparison.Ordinal))
                return i.Name;
        return null;
    }

    /// <summary>
    /// The pandas kind of an expression's value. A comparison is bool to the engine, but pandas compares element by element: `s > 1` is a
    /// Series and `df == 0` a DataFrame (identity and membership tests aside), as the stubs' __gt__ and the like say.
    /// </summary>
    static string? PandasKindOf(ModuleInfo m, TypeEvaluator ev, Expr e)
    {
        if (e is CompareExpr c && c.Ops.All(op => op is "<" or ">" or "<=" or ">=" or "==" or "!="))
            return new[] { c.Left }.Concat(c.Comparators).Select(x => PandasKindOf(m, ev, x)).FirstOrDefault(k => k != null);
        return SureCall(m, ev, e) ? PandasKind(ev, ev.TypeOf(m, e)) : null;
    }

    /// <summary>
    /// The calls the value comes from were given arguments of known types. pandas' functions are overloaded on what they are given
    /// (`isna(x)` is a DataFrame for a DataFrame and a bool for a scalar): with an argument of unknown type the first overload says nothing.
    /// </summary>
    static bool SureCall(ModuleInfo m, TypeEvaluator ev, Expr e, int depth = 0) => e switch
    {
        CallExpr c => c.Args.All(a => !ev.TypeOf(m, a.Value).Members.Any(x => x is UnknownType || x is ClassType { Info.Name: "Any" }))
            // an overloaded function (isna, notna, ...) is trusted only when it is given a pandas object: for anything else the engine may pick the wrong overload
            && (ev.TypeOf(m, c.Func) is not OverloadedType || c.Args.Any(a => PandasKindOf(m, ev, a.Value) != null))
            && SureCall(m, ev, c.Func, depth),
        // a private name the stubs do not have is answered by DataFrame.__getattr__ (a column, a Series), which pandas' own code never means
        AttributeExpr a => !(a.Attr.StartsWith('_') && ev.AttributeType(ev.TypeOf(m, a.Value), a.Attr).Declarations.Count == 0) && SureCall(m, ev, a.Value, depth),
        // an index of unknown type may be a scalar position (`idx[i]` is an element) or a slice (another Index)
        // (of a pandas object only a column name, a slice, a list or a pandas mask is trusted; numpy's own overloads may give an array for a position)
        SubscriptExpr s => !ev.TypeOf(m, s.Index).Members.Any(x => x is UnknownType || x is ClassType { Info.Name: "Any" })
            && (s.Value is AttributeExpr { Attr: "loc" or "iloc" or "at" or "iat" } ? s.Index is SliceExpr or ListExpr || PandasKindOf(m, ev, s.Index) != null
                : PandasKind(ev, ev.TypeOf(m, s.Value)) == null || s.Index is SliceExpr or ListExpr || FixtureModel.StringValue(s.Index) != null || PandasKindOf(m, ev, s.Index) != null)
            && SureCall(m, ev, s.Value, depth),
        BinaryExpr b => SureCall(m, ev, b.Left, depth) && SureCall(m, ev, b.Right, depth),
        // a name assigned once: what it was given
        NameExpr n when depth < 4 && AssignedOnce(m, n) is { } value => SureCall(m, ev, value, depth + 1),
        _ => true,
    };

    /// <summary>The value of the one assignment `name = value` that binds the name in its scope, or null.</summary>
    static Expr? AssignedOnce(ModuleInfo m, NameExpr n)
    {
        Node? scopeNode = m.ParentOf(n);
        while (scopeNode != null && scopeNode is not (FunctionDef or Module or LambdaExpr)) scopeNode = m.ParentOf(scopeNode);
        var sym = scopeNode == null ? null : m.Semantics.ScopeOf(scopeNode)?.Symbols.GetValueOrDefault(n.Name);
        if (sym is not { Declarations.Count: 1 }) return null;
        if (sym.Declarations[0].Node is AssignStmt { Targets: [NameExpr], Value: var whole }) return whole;
        if (m.ParentOf(sym.Declarations[0].Node) is not AssignStmt { Targets: [NameExpr] } a) return null;
        return ReferenceEquals(a.Targets[0], sym.Declarations[0].Node) ? a.Value : null;
    }

    IEnumerable<Finding> PandasFindings(ModuleInfo m, TypeEvaluator ev)
    {
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            // s.values.tolist() (and s.to_numpy().tolist()) on a Series: the list comes straight from s.to_list()
            if (node is CallExpr { Func: AttributeExpr { Attr: "tolist", Value: var arr }, Args.Count: 0 } call
                && (arr is AttributeExpr { Attr: "values" } || arr is CallExpr { Func: AttributeExpr { Attr: "to_numpy" }, Args.Count: 0 })
                && PandasKind(ev, ev.TypeOf(m, arr is AttributeExpr va ? va.Value : ((AttributeExpr)((CallExpr)arr).Func).Value)) == "Series")
                yield return new Finding(call.Start, call.End - call.Start, "Method Series.to_list() is recommended", "pandas-series-to-list", FindingLevel.Weak);
            // list(s.values): the case PyCharm's help gives
            else if (node is CallExpr { Func: NameExpr { Name: "list" } lf, Args: [{ Name: null, Stars: 0, Value: AttributeExpr { Attr: "values" } lv }] } lcall
                && IsBuiltinList(m, ev, lf) && PandasKind(ev, ev.TypeOf(m, lv.Value)) == "Series")
                yield return new Finding(lcall.Start, lcall.End - lcall.Start, "Method Series.to_list() is recommended", "pandas-series-to-list", FindingLevel.Weak);

            foreach (var test in TruthTested(m, node))
                if (PandasKindOf(m, ev, test) is { } kind)
                    yield return new Finding(test.Start, Math.Max(test.End - test.Start, 1),
                        $"The truth value of a {kind} is ambiguous: pandas raises ValueError (use a.empty, a.bool(), a.item(), a.any() or a.all())", "pandas-truth-value", FindingLevel.Warning);
        }
    }

    /// <summary>The name is the builtin list (not a name of the project's that hides it).</summary>
    static bool IsBuiltinList(ModuleInfo m, TypeEvaluator ev, NameExpr n) =>
        ev.TypeOf(m, n) is ClassType { IsInstance: false, Info: { Name: "list" } info } && info.Module.Name == "builtins";

    /// <summary>The expressions whose truth value this node asks for: conditions of if, elif, while, assert, a conditional expression and a comprehension, the operand of not, and the operands of and / or that decide.</summary>
    static IEnumerable<Expr> TruthTested(ModuleInfo m, Node node)
    {
        switch (node)
        {
            case IfStmt s: return Operands(s.Test);
            case WhileStmt s: return Operands(s.Test);
            case AssertStmt s: return Operands(s.Test);
            case IfExpr e: return Operands(e.Test);
            case Comprehension c: return c.Ifs.SelectMany(Operands);
            case UnaryExpr { Op: "not" } u: return Operands(u.Operand);
            // `a and b` asks for a's truth value wherever it stands; b's only where the whole is a condition (handled above)
            case BinaryExpr { Op: "and" or "or" } b when !IsBoolOperand(m, b): return Operands(b.Left);
        }
        return Array.Empty<Expr>();

        // a condition made of and / or: each operand is tested (nested ones are reached through this node, not again on their own)
        static IEnumerable<Expr> Operands(Expr e) => e is BinaryExpr { Op: "and" or "or" } b ? Operands(b.Left).Concat(Operands(b.Right)) : new[] { e };
    }

    /// <summary>The node is an operand of an and / or, or itself a condition: its operands are reported from there.</summary>
    static bool IsBoolOperand(ModuleInfo m, BinaryExpr b)
    {
        var parent = m.ParentOf(b);
        return parent is BinaryExpr { Op: "and" or "or" } or IfStmt or WhileStmt or AssertStmt or Comprehension or UnaryExpr { Op: "not" }
            || parent is IfExpr ie && ReferenceEquals(ie.Test, b);
    }

    void PandasFix(string code, ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        if (code == "pandas-series-to-list")
        {
            var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Start == start && c.End == end);
            if (call is { Func: NameExpr, Args: [{ Value: AttributeExpr { Value: var of } }] })
            {
                add("Replace with to_list()", true, new[] { new Edit(call.Start, call.End, text[of.Start..of.End] + ".to_list()") });
                return;
            }
            if (call?.Func is not AttributeExpr { Value: var arr }) return;
            var series = arr is AttributeExpr va ? va.Value : arr is CallExpr { Func: AttributeExpr ta } ? ta.Value : null;
            if (series == null) return;
            add("Replace with to_list()", true, new[] { new Edit(series.End, call.End, ".to_list()") });
        }
        else if (code == "pandas-truth-value")
        {
            var e = AstChildren.Descendants(m.Ast).OfType<Expr>().FirstOrDefault(x => x.Start == start && x.End == end);
            if (e == null) return;
            // `if df:` meant "if there is something in it"; `not df` meant "it is empty"
            string inner = text[start..end];
            bool simple = e is NameExpr or AttributeExpr or SubscriptExpr or CallExpr;
            string operand = simple ? inner : $"({inner})";
            if (m.ParentOf(e) is UnaryExpr { Op: "not" } not)
                add($"Replace with '{operand}.empty'", true, new[] { new Edit(not.Start, not.End, operand + ".empty") });
            else
                add($"Replace with 'not {operand}.empty'", true, new[] { new Edit(start, end, $"not {operand}.empty") });
        }
    }
}
