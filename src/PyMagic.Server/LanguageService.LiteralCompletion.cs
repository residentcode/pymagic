using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// Completion of the strings a place takes: the keys of a dictionary in `d["|"]` and `d[|]` (those of its literal, those set with `d["k"] = ...`,
/// a TypedDict's), and the values of a `Literal[...]` where one is expected (an argument, an assignment, a comparison, a `return`).
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>Inside a string being typed: the keys of the dictionary it indexes, or the `Literal` values the place wants; null for any other string.</summary>
    IReadOnlyList<CompletionItem>? StringValueCompletion(Doc doc, int start, int offset)
    {
        string text = doc.Text;
        string before = text[LineStart(text, offset)..offset];
        var open = Regex.Match(before, @"(?<q>[""'])[^""']*$");
        if (!open.Success) return null;
        // a string not closed yet (`d['ho|`) is closed in the copy that is read, so it parses
        int lineEnd = text.IndexOf('\n', offset);
        string rest = text[offset..(lineEnd < 0 ? text.Length : lineEnd)];
        if (!rest.Contains(open.Groups["q"].Value))
        {
            string closed = text[..offset] + open.Groups["q"].Value;
            doc = doc with { Text = closed + MissingClosers(closed) + text[offset..] };
        }
        var (ws, patched) = PatchedModule(doc, start, offset);
        var ev = ws.Evaluator;
        var literal = AstChildren.Descendants(patched.Ast).OfType<ConstExpr>().FirstOrDefault(c => c.Kind == ConstKind.Str && !c.IsFString && c.Text.Contains(CursorMark));
        if (literal == null) return null;
        var values = patched.ParentOf(literal) is SubscriptExpr sub && ReferenceEquals(sub.Index, literal)
            ? KnownKeys(patched, ev, sub.Value)
            : LiteralStrings(WantedAt(patched, ev, literal));
        if (values.Count == 0) return null;
        return values.Select((v, i) => new CompletionItem(v, CompletionKind.Constant, null, "0" + i.ToString("D3"), FilterText: v)).ToList();
    }

    /// <summary>Where an expression begins (`f(|`, `x = |`, `d[|`): the keys or `Literal` values written out with their quotes, to rank first.</summary>
    IReadOnlyList<CompletionItem> QuotedValueItems(ModuleInfo m, TypeEvaluator ev, NameExpr placeholder, PyType? expected, string prefix)
    {
        var values = m.ParentOf(placeholder) is SubscriptExpr sub && ReferenceEquals(sub.Index, placeholder)
            ? KnownKeys(m, ev, sub.Value)
            : LiteralStrings(expected);
        var items = new List<CompletionItem>();
        foreach (var v in values)
        {
            string quoted = "\"" + v.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
            if (prefix.Length > 0 && !v.StartsWith(prefix, StringComparison.Ordinal)) continue;
            items.Add(new CompletionItem(quoted, CompletionKind.Constant, null, "0" + items.Count.ToString("D3"), quoted, FilterText: v));
        }
        return items;
    }

    /// <summary>The type a string at <paramref name="e"/> is wanted to have: what the expected-type lookup says, or for `x == "|"` the type of `x`.</summary>
    static PyType? WantedAt(ModuleInfo m, TypeEvaluator ev, Expr e)
    {
        if (ExpectedTypeOf(m, ev, e) is { } expected) return expected;
        if (m.ParentOf(e) is CompareExpr cmp && cmp.Ops.Count == 1 && cmp.Ops[0] is "==" or "!=")
            return ReferenceEquals(cmp.Comparators[0], e) ? ev.TypeOf(m, cmp.Left) : ev.TypeOf(m, cmp.Comparators[0]);
        return null;
    }

    /// <summary>The string values of the `Literal[...]` members of a type, in their order.</summary>
    static List<string> LiteralStrings(PyType? t)
    {
        var found = new List<string>();
        var members = t switch { UnionType u => u.MemberList, null => Array.Empty<PyType>(), _ => new[] { t } };
        foreach (var x in members)
            if (x is ClassType { IsInstance: true, LiteralValue: string s } && !found.Contains(s)) found.Add(s);
        return found;
    }

    /// <summary>
    /// The keys a dictionary is known to have: a TypedDict's fields, or for a name the string keys of the dict it was made with (`{...}`, `dict(a=1)`)
    /// and of the items set on it (`d["k"] = ...`) in the module.
    /// </summary>
    static List<string> KnownKeys(ModuleInfo m, TypeEvaluator ev, Expr receiver)
    {
        var keys = new List<string>();
        void Add(string? k) { if (k != null && k != CursorMark && !k.Contains(CursorMark) && !keys.Contains(k)) keys.Add(k); }
        if (ev.TypeOf(m, receiver) is ClassType { IsInstance: true } ct && ev.TypedDictFieldsOf(ct.Info) is { } fields)
        {
            foreach (var f in fields) Add(f.Field.Name);
            return keys;
        }
        if (receiver is not NameExpr ne || m.Semantics.Resolve(ne) is not { } sym) return keys;
        foreach (var d in sym.Declarations)
        {
            var value = m.ParentOf(d.Node) switch { AssignStmt a => a.Value, AnnAssignStmt aa => aa.Value, _ => null };
            switch (value)
            {
                case DictExpr dict:
                    foreach (var k in dict.Keys) Add(FixtureModel.StringValue(k));
                    break;
                case CallExpr { Func: NameExpr { Name: "dict" } } call:
                    foreach (var a in call.Args) if (a.Name != null) Add(a.Name);
                    break;
            }
        }
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            var targets = n switch { AssignStmt a => a.Targets, AugAssignStmt au => new[] { au.Target }, AnnAssignStmt aa => new[] { aa.Target }, _ => null };
            if (targets == null) continue;
            foreach (var t in targets)
                if (t is SubscriptExpr { Value: NameExpr owner } s && owner.Name == ne.Name && m.Semantics.Resolve(owner) == sym) Add(FixtureModel.StringValue(s.Index));
        }
        return keys;
    }
}
