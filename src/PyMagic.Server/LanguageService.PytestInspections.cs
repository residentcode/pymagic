using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's pytest and mock inspections: a fixture no test or fixture asks for, `@pytest.mark.parametrize` whose names the test does not
/// take or whose rows have another number of values, and a function that takes fewer parameters than its `@patch` decorators hand it.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> PytestDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains("pytest", StringComparison.Ordinal) && !m.Source.Contains("patch", StringComparison.Ordinal) && !m.Name.EndsWith("conftest", StringComparison.Ordinal)) yield break;
        foreach (var f in UnusedFixtures(m, ev).Concat(ParametrizeFindings(m, ev)).Concat(PatchFindings(m)))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>A fixture of this module that nothing in the project asks for by parameter, usefixtures or getfixturevalue; autouse ones and plugin hooks aside.</summary>
    IEnumerable<Finding> UnusedFixtures(ModuleInfo m, TypeEvaluator ev)
    {
        var defs = FixtureDefsIn(m).Where(d => !d.Autouse && !FixtureModel.IsPluginFixture(d.Name)).ToList();
        if (defs.Count == 0 || FixturesOf(ev.Workspace) is null) yield break;
        var snap = Current();
        // names pytest hands out at run time: no use of a fixture can be ruled out
        if (snap.Project.Any(x => !x.IsStub && (x.Source.Contains("pytest_generate_tests", StringComparison.Ordinal) || x.Source.Contains("getfixturevalue(") && !FixtureModel.StringRequests(x).Any()))) yield break;
        // a decorator of a library may ask for a fixture where the project cannot see it (matplotlib's @image_comparison(baseline_images=...)
        // calls getfixturevalue('baseline_images')): a name given as a decorator's keyword, or written as a string, may be such a request
        var mentioned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var x in snap.Project.Where(x => !x.IsStub))
            foreach (var n in AstChildren.Descendants(x.Ast))
            {
                if (n is FunctionDef or ClassDef)
                    foreach (var d in n is FunctionDef fd ? fd.Decorators : ((ClassDef)n).Decorators)
                        if (d is CallExpr dc) foreach (var a in dc.Args) if (a.Name != null) mentioned.Add(a.Name);
                if (n is ConstExpr { Kind: ConstKind.Str } c && FixtureModel.StringValue(c) is { Length: < 60 } s) mentioned.Add(s);
            }
        foreach (var def in defs)
        {
            if (mentioned.Contains(def.Name) || UsesOf(snap, def).Count > 0) continue;
            var (s, e) = DefinitionSpan(def);
            yield return new Finding(s, e - s, $"Fixture '{def.Name}' is not requested by any test or fixture", "fixture-unused", FindingLevel.Weak);
        }
    }

    /// <summary>The argument names of a parametrize call and the expression that wrote them; null when they are not plain strings.</summary>
    static (List<string> Names, Expr At)? ParametrizeNames(CallExpr call)
    {
        var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argnames")?.Value;
        if (arg == null) return null;
        if (FixtureModel.StringValue(arg) is { } list) return (list.Split(',').Select(n => n.Trim()).Where(n => n.Length > 0).ToList(), arg);
        var items = arg switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
        if (items == null || items.Any(i => FixtureModel.StringValue(i) == null)) return null;
        return (items.Select(i => FixtureModel.StringValue(i)!).ToList(), arg);
    }

    IEnumerable<Finding> ParametrizeFindings(ModuleInfo m, TypeEvaluator ev)
    {
        var ctx = FixturesOf(ev.Workspace);
        foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            var parameters = f.Params.Where(p => p.Kind == ParamKind.Normal).Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            bool takesAny = f.Params.Any(p => p.Kind is ParamKind.VarArgs or ParamKind.KwArgs);
            foreach (var d in f.Decorators)
            {
                if (d is not CallExpr call || !FixtureModel.CalleeText(m, call.Func).EndsWith("parametrize", StringComparison.Ordinal) || ParametrizeNames(call) is not { } names) continue;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var name in names.Names)
                {
                    if (!seen.Add(name))
                        yield return new Finding(names.At.Start, names.At.End - names.At.Start, $"'{name}' is named twice: pytest stops with \"duplicate parametrization of '{name}'\"", "parametrize-arguments", FindingLevel.Error);
                    // a name the test does not take may still parametrize a fixture it asks for (pytest overrides the fixture's value)
                    else if (!parameters.Contains(name) && !takesAny && !(ctx != null && ctx.Resolver.Resolve(ev.Workspace, m, f, name, null).Found)
                        && !call.Args.Any(a => a.Name == "indirect"))
                        yield return new Finding(names.At.Start, names.At.End - names.At.Start, $"'{f.Name}' has no parameter '{name}': pytest stops with \"function uses no argument '{name}'\"", "parametrize-arguments", FindingLevel.Error);
                }
                if (names.Names.Count < 2) continue;
                var values = call.Args.Where(a => a.Name == null && a.Stars == 0).Skip(1).FirstOrDefault()?.Value ?? call.Args.FirstOrDefault(a => a.Name == "argvalues")?.Value;
                var rows = values switch { ListExpr l => l.Items, TupleExpr t => t.Items, _ => null };
                if (rows == null) continue;
                foreach (var row in rows)
                {
                    int? count = row switch
                    {
                        TupleExpr t when !t.Items.Any(i => i is StarExpr) => t.Items.Count,
                        ListExpr l when !l.Items.Any(i => i is StarExpr) => l.Items.Count,
                        CallExpr { Func: var pf } pc when FixtureModel.CalleeText(m, pf).EndsWith("param", StringComparison.Ordinal) && pc.Args.All(a => a.Stars == 0) => pc.Args.Count(a => a.Name == null),
                        _ => null,
                    };
                    if (count is { } n && n != names.Names.Count)
                        yield return new Finding(row.Start, row.End - row.Start, $"{n} value{(n == 1 ? "" : "s")} for {names.Names.Count} names ({string.Join(", ", names.Names)}): pytest stops with \"the number of names must be equal to the number of values\"", "parametrize-arguments", FindingLevel.Error);
                }
            }
        }
    }

    /// <summary>`@patch("a")` / `@patch.object(A, "b")` without new= each hand a mock to the function's next parameter: it must take them.</summary>
    static IEnumerable<Finding> PatchFindings(ModuleInfo m)
    {
        foreach (var f in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            int mocks = FixtureModel.PatchInjected(m, f);
            if (mocks == 0 || f.Params.Any(p => p.Kind == ParamKind.VarArgs)) continue;
            bool method = m.ParentOf(f) is ClassDef && !f.Decorators.Any(d => DecoratorName(d) == "staticmethod");
            int takes = f.Params.Count(p => p.Kind == ParamKind.Normal) - (method ? 1 : 0);
            if (takes >= mocks) continue;
            yield return new Finding(f.NameStart, f.Name.Length,
                $"The @patch decorators hand '{f.Name}' {mocks} mock{(mocks == 1 ? "" : "s")}, and it takes {Math.Max(takes, 0)}: the call fails with TypeError", "patch-arguments", FindingLevel.Error);
        }
    }
}
