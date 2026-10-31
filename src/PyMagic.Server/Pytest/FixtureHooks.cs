using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server.Pytest;

/// <summary>
/// What pytest gets from code that runs while it collects: `parametrize(..., indirect=True)` hands its values to a fixture's `request.param`,
/// `metafunc.parametrize(...)` in `pytest_generate_tests` gives tests arguments, and `config.pluginmanager.register(Helpers())` adds that object's fixtures.
/// </summary>
internal static class FixtureHooks
{
    /// <summary>The argument names of one `parametrize(...)` call, in order.</summary>
    public static List<string> ArgNames(CallExpr call)
    {
        var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argnames")?.Value;
        if (FixtureModel.StringValue(arg) is { } list) return list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList();
        var items = arg switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
        return items?.Select(FixtureModel.StringValue).OfType<string>().ToList() ?? new List<string>();
    }

    /// <summary>The names a `parametrize(...)` call hands to fixtures instead of the test: all with `indirect=True`, those listed with `indirect=[...]`.</summary>
    public static HashSet<string> IndirectNames(CallExpr call)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        switch (call.Args.FirstOrDefault(a => a.Name == "indirect")?.Value)
        {
            case ConstExpr { Kind: ConstKind.True }:
                names.UnionWith(ArgNames(call));
                break;
            case ListExpr l:
                foreach (var i in l.Items) if (FixtureModel.StringValue(i) is { } s) names.Add(s);
                break;
            case TupleExpr t:
                foreach (var i in t.Items) if (FixtureModel.StringValue(i) is { } s) names.Add(s);
                break;
        }
        return names;
    }

    /// <summary>The values a `parametrize(...)` call gives the name at <paramref name="index"/>: the items of its list, or that element of each tuple when it names several.</summary>
    public static IEnumerable<Expr> ValuesFor(CallExpr call, int index, int count)
    {
        var values = call.Args.Where(a => a.Name == null && a.Stars == 0).Skip(1).FirstOrDefault()?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argvalues")?.Value;
        var items = values switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
        if (items == null) yield break;
        foreach (var item in items)
        {
            // pytest.param(value, id=...) carries the value
            var value = item is CallExpr { Func: var f } p && f is AttributeExpr { Attr: "param" } or NameExpr { Name: "param" } ? (count == 1 ? p.Args.FirstOrDefault()?.Value : null) ?? item : item;
            if (count == 1) yield return value;
            else if (value is TupleExpr { Items.Count: var n } tuple && n == count) yield return tuple.Items[index];
            else if (value is ListExpr { Items.Count: var m } list && m == count) yield return list.Items[index];
        }
    }

    static bool IsParametrize(ModuleInfo m, CallExpr call) => FixtureModel.CalleeText(m, call.Func).EndsWith("parametrize", StringComparison.Ordinal);

    /// <summary>The values tests of these modules hand to the fixture <paramref name="fixture"/> with `indirect`: what its `request.param` is.</summary>
    public static List<(ModuleInfo Module, Expr Value)> IndirectValues(IEnumerable<ModuleInfo> modules, string fixture)
    {
        var found = new List<(ModuleInfo, Expr)>();
        foreach (var m in modules)
        {
            if (m.IsStub || m.Source.IndexOf("indirect", StringComparison.Ordinal) < 0) continue;
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
            {
                if (!IsParametrize(m, call) || !IndirectNames(call).Contains(fixture)) continue;
                var names = ArgNames(call);
                int at = names.IndexOf(fixture);
                foreach (var v in ValuesFor(call, at, names.Count)) found.Add((m, v));
            }
        }
        return found;
    }

    /// <summary>
    /// The arguments `pytest_generate_tests` gives with `metafunc.parametrize("name", [...])` in a module, with their values; <c>Complete</c> is false
    /// when a call's names are not written out (it may then give anything).
    /// </summary>
    public static (List<(string Name, CallExpr Call, int Index, int Count)> Names, bool Complete) GeneratedArguments(ModuleInfo m)
    {
        var names = new List<(string, CallExpr, int, int)>();
        bool complete = true;
        if (m.IsStub || m.Source.IndexOf("pytest_generate_tests", StringComparison.Ordinal) < 0) return (names, true);
        foreach (var def in m.Ast.Body.OfType<FunctionDef>().Where(d => d.Name == "pytest_generate_tests"))
        {
            string metafunc = def.Params.FirstOrDefault()?.Name ?? "metafunc";
            int given = 0;
            foreach (var call in AstChildren.Descendants(def).OfType<CallExpr>())
            {
                // metafunc handed to a helper may be parametrized there with anything
                if (call.Args.Any(a => a.Value is NameExpr n && n.Name == metafunc)) complete = false;
                if (call.Func is not AttributeExpr { Attr: "parametrize" }) continue;
                var listed = ArgNames(call);
                if (listed.Count == 0) { complete = false; continue; }
                given++;
                for (int i = 0; i < listed.Count; i++) names.Add((listed[i], call, i, listed.Count));
            }
            // a hook that parametrizes nothing it shows is not known to give nothing
            if (given == 0) complete = false;
        }
        return (names, complete);
    }

    /// <summary>The classes whose instances a module registers as plugins (`config.pluginmanager.register(Helpers())`): their fixture methods are fixtures.</summary>
    public static IEnumerable<(ModuleInfo Module, ClassDef Class)> RegisteredPluginClasses(Workspace ws, ModuleInfo m)
    {
        if (m.IsStub || m.Source.IndexOf("pluginmanager", StringComparison.Ordinal) < 0) yield break;
        foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
        {
            if (call.Func is not AttributeExpr { Attr: "register", Value: AttributeExpr { Attr: "pluginmanager" } }) continue;
            if (call.Args.FirstOrDefault(a => a.Name == null)?.Value is not CallExpr made) continue;
            if (ws.Evaluator.TypeOf(m, made.Func) is ClassType { IsInstance: false } cls && !cls.Info.Module.IsStub) yield return (cls.Info.Module, cls.Info.Node);
        }
    }
}
