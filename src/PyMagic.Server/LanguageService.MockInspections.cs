using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// unittest.mock's mistakes that only show when the test runs: a `patch("pkg.mod.name")` target whose last part the module (or class) does
/// not have, `patch.object(Class, "name")` for a name the class does not have (both raise AttributeError unless create=True), and an
/// attribute a mock with a spec does not have (`Mock(spec=Client).sendd`, `create_autospec(f)`, `patch(..., autospec=True)`), or an
/// assert_ method that is not one (`m.assert_called_once_wiht()`), which mock refuses with AttributeError instead of passing silently.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> MockDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !m.Source.Contains("mock", StringComparison.Ordinal) && !m.Source.Contains("mocker", StringComparison.Ordinal)) yield break;
        foreach (var f in MockFindings(m, ev))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    static readonly Regex PatchCallee = new(@"(^|\.)patch$", RegexOptions.Compiled);
    static readonly Regex PatchObjectCallee = new(@"(^|\.)patch\.object$", RegexOptions.Compiled);
    static readonly Regex SpecMockCallee = new(@"(^|\.)(Mock|MagicMock|AsyncMock|NonCallableMock|NonCallableMagicMock)$", RegexOptions.Compiled);
    static readonly Regex AutospecCallee = new(@"(^|\.)create_autospec$", RegexOptions.Compiled);

    /// <summary>What a mock has of its own, whatever its spec: its assertions, its records of calls and its configuration.</summary>
    static readonly HashSet<string> MockOwnAttributes = new(StringComparer.Ordinal)
    {
        "assert_called", "assert_called_once", "assert_called_with", "assert_called_once_with", "assert_any_call", "assert_has_calls", "assert_not_called",
        "assert_awaited", "assert_awaited_once", "assert_awaited_with", "assert_awaited_once_with", "assert_any_await", "assert_has_awaits", "assert_not_awaited",
        "attach_mock", "configure_mock", "mock_add_spec", "reset_mock", "return_value", "side_effect", "called", "call_count", "call_args", "call_args_list",
        "method_calls", "mock_calls", "await_count", "await_args", "await_args_list",
    };

    /// <summary>The prefixes mock takes for a misspelt assertion (Python 3.12: "assert", "assret", "asert", "aseert", "assrt").</summary>
    static bool AssertLike(string name) => name.StartsWith("assert", StringComparison.Ordinal) || name.StartsWith("assret", StringComparison.Ordinal)
        || name.StartsWith("asert", StringComparison.Ordinal) || name.StartsWith("aseert", StringComparison.Ordinal) || name.StartsWith("assrt", StringComparison.Ordinal);

    IEnumerable<Finding> MockFindings(ModuleInfo m, TypeEvaluator ev)
    {
        // the mocks of this module whose spec is known: the symbol of the name -> the object they imitate (null: no spec, only the assertions are checked)
        var mocks = new Dictionary<Symbol, PyType?>(ReferenceEqualityComparer.Instance);
        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case CallExpr call when PatchCallee.IsMatch(FixtureModel.CalleeText(m, call.Func)):
                {
                    if (call.Args.Any(a => a.Name == "create" && a.Value is not ConstExpr { Kind: ConstKind.False })) break;
                    var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "target")?.Value;
                    if (arg == null || FixtureModel.StringValue(arg) is not { } target) break;
                    if (PatchTargetProblem(ev, target) is { } problem) yield return new Finding(arg.Start, arg.End - arg.Start, problem, "mock-patch-target", FindingLevel.Warning);
                    break;
                }
                case CallExpr call when PatchObjectCallee.IsMatch(FixtureModel.CalleeText(m, call.Func)):
                {
                    if (call.Args.Any(a => a.Name == "create" && a.Value is not ConstExpr { Kind: ConstKind.False })) break;
                    var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
                    var owner = positional.ElementAtOrDefault(0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "target")?.Value;
                    var nameArg = positional.ElementAtOrDefault(1)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "attribute")?.Value;
                    if (owner == null || nameArg == null || FixtureModel.StringValue(nameArg) is not { } attr) break;
                    var t = ev.TypeOf(m, owner);
                    if (Lacks(ev, t, attr))
                        yield return new Finding(nameArg.Start, nameArg.End - nameArg.Start, $"{MockOwnerName(t)} has no attribute '{attr}': patch.object raises AttributeError", "mock-patch-target", FindingLevel.Warning);
                    break;
                }
                // m = Mock(spec=Client) / create_autospec(Client) / with patch("a.Client", autospec=True) as m
                case AssignStmt { Targets: [NameExpr name], Value: CallExpr call }:
                    if (MockSpec(m, ev, call) is { IsMock: true } made && SymbolOf(m, name) is { } sym) mocks[sym] = made.Spec;
                    break;
                case WithStmt w:
                    foreach (var item in w.Items)
                        if (item is { Target: NameExpr name, Context: CallExpr call } && PatchCallee.IsMatch(FixtureModel.CalleeText(m, call.Func))
                            && !call.Args.Any(a => a.Name == "new") && call.Args.Count(a => a.Name == null && a.Stars == 0) == 1 && SymbolOf(m, name) is { } withSym)
                            mocks[withSym] = PatchSpec(m, ev, call);
                    break;
                case FunctionDef f when f.Decorators.Any(d => d is CallExpr):
                {
                    // the mocks @patch decorators hand the function: the bottom decorator's first, after self
                    var patches = f.Decorators.OfType<CallExpr>().Reverse().Where(c => (PatchCallee.IsMatch(FixtureModel.CalleeText(m, c.Func)) || PatchObjectCallee.IsMatch(FixtureModel.CalleeText(m, c.Func)))
                        && !c.Args.Any(a => a.Name == "new") && c.Args.Count(a => a.Name == null && a.Stars == 0) == (PatchObjectCallee.IsMatch(FixtureModel.CalleeText(m, c.Func)) ? 2 : 1)).ToList();
                    if (patches.Count == 0) break;
                    var ps = f.Params.Where(p => p.Kind == ParamKind.Normal).ToList();
                    int first = ps.Count > 0 && ps[0].Name is "self" or "cls" ? 1 : 0;
                    var scope = m.Semantics.ScopeOf(f);
                    for (int i = 0; i < patches.Count && first + i < ps.Count; i++)
                        if (scope?.Symbols.GetValueOrDefault(ps[first + i].Name) is { } paramSym)
                            mocks[paramSym] = PatchObjectCallee.IsMatch(FixtureModel.CalleeText(m, patches[i].Func)) ? null : PatchSpec(m, ev, patches[i]);
                    break;
                }
            }
        }
        if (mocks.Count == 0) yield break;

        foreach (var attr in AstChildren.Descendants(m.Ast).OfType<AttributeExpr>())
        {
            if (attr.Value is not NameExpr recv || SymbolOf(m, recv) is not { } sym || !mocks.TryGetValue(sym, out var spec)) continue;
            // only a name bound once is followed: `m = Mock(spec=A)` then `m = other` is not the mock any more
            if (sym.Declarations.Count != 1) continue;
            string name = attr.Attr;
            if (MockOwnAttributes.Contains(name) || name.StartsWith("__", StringComparison.Ordinal) && name.EndsWith("__", StringComparison.Ordinal)) continue;
            bool store = m.ParentOf(attr) is AssignStmt a && a.Targets.Contains(attr) || m.ParentOf(attr) is AugAssignStmt or AnnAssignStmt or DelStmt;
            int at = attr.End - name.Length;
            if (AssertLike(name) && !store)
                yield return new Finding(at, name.Length, $"'{name}' is not an assertion of mock: it raises AttributeError", "mock-attribute", FindingLevel.Warning);
            else if (spec != null && !store && Lacks(ev, spec, name))
                yield return new Finding(at, name.Length, $"The mock's spec '{spec.Display()}' has no attribute '{name}': it raises AttributeError", "mock-attribute", FindingLevel.Warning);
        }
    }

    /// <summary>module 'pkg.mod', class 'Client' or 'Client' (an instance), as the messages name what lacks an attribute.</summary>
    static string MockOwnerName(PyType t) => t switch
    {
        ModuleType mt => $"module '{mt.Module.Name}'",
        ClassType { IsInstance: false } c => $"class '{c.Info.Name}'",
        _ => $"'{t.Display()}'",
    };

    static Symbol? SymbolOf(ModuleInfo m, NameExpr n)
    {
        for (Node? p = m.ParentOf(n); p != null; p = m.ParentOf(p))
            if (p is ClassDef) return null;   // a class body's names are not followed
            else if (p is FunctionDef or LambdaExpr or Module) return m.Semantics.ScopeOf(p)?.Symbols.GetValueOrDefault(n.Name);
        return m.Semantics.ModuleScope.Symbols.GetValueOrDefault(n.Name);
    }

    /// <summary>Whether a call makes a mock, and its spec (null when it has none).</summary>
    static (bool IsMock, PyType? Spec) MockSpec(ModuleInfo m, TypeEvaluator ev, CallExpr call)
    {
        string callee = FixtureModel.CalleeText(m, call.Func);
        if (AutospecCallee.IsMatch(callee))
        {
            var spec = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? call.Args.FirstOrDefault(a => a.Name == "spec")?.Value;
            return (true, spec == null ? null : SpecType(ev.TypeOf(m, spec)));
        }
        if (!SpecMockCallee.IsMatch(callee)) return (false, null);
        var given = call.Args.FirstOrDefault(a => a.Name is "spec" or "spec_set")?.Value ?? call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value;
        // a list of names as the spec, or something unknown: only the assertions are checked
        return (true, given == null ? null : SpecType(ev.TypeOf(m, given)));
    }

    /// <summary>The spec of what patch(..., autospec=True) makes: the patched object; null without autospec or when it cannot be found.</summary>
    static PyType? PatchSpec(ModuleInfo m, TypeEvaluator ev, CallExpr call)
    {
        if (!call.Args.Any(a => a.Name == "autospec" && a.Value is ConstExpr { Kind: ConstKind.True })) return null;
        var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value;
        return arg != null && FixtureModel.StringValue(arg) is { } target ? SpecType(ResolveDotted(ev, target)) : null;
    }

    /// <summary>
    /// A class, an instance or a module as a spec; anything else (a list of names, a function, unknown) gives none. A class is taken as its
    /// instances: mock itself allows only what dir(Class) lists, but PyCharm does not report the attributes __init__ sets either.
    /// </summary>
    static PyType? SpecType(PyType? t) => t is ClassType c ? c.AsInstance() : t is ModuleType ? t : null;

    /// <summary>What a dotted name such as "pkg.mod.Class.attr" stands for, read the way mock imports it; null when any part is not known.</summary>
    static PyType? ResolveDotted(TypeEvaluator ev, string target)
    {
        var parts = target.Split('.');
        for (int n = parts.Length; n >= 1; n--)
        {
            if (ev.Workspace.GetModule(string.Join('.', parts[..n])) is not { } mod) continue;
            PyType t = new ModuleType(mod);
            for (int i = n; i < parts.Length; i++)
            {
                var r = ev.AttributeType(t, parts[i]);
                if (r.Missing.Count > 0 || r.Type is UnknownType) return null;
                t = r.Type;
            }
            return t;
        }
        return null;
    }

    /// <summary>Why mock cannot patch this target, or null when it can or when that cannot be known.</summary>
    static string? PatchTargetProblem(TypeEvaluator ev, string target)
    {
        if (!target.Contains('.')) return $"'{target}' is not a dotted path: patch raises TypeError (\"Need a valid target to patch\")";
        var parts = target.Split('.');
        if (parts.Any(p => p.Length == 0 || !p.All(c => char.IsLetterOrDigit(c) || c == '_'))) return null;
        for (int n = parts.Length - 1; n >= 1; n--)
        {
            if (ev.Workspace.GetModule(string.Join('.', parts[..n])) is not { } mod) continue;
            PyType t = new ModuleType(mod);
            for (int i = n; i < parts.Length; i++)
            {
                // a submodule is an attribute of its package once imported, which patch does
                if (t is ModuleType pm && ev.Workspace.GetModule(pm.Module.Name + "." + parts[i]) is { } sub) { t = new ModuleType(sub); continue; }
                if (Lacks(ev, t, parts[i])) return $"Cannot find '{parts[i]}' in '{string.Join('.', parts[..i])}': patch raises AttributeError";
                var r = ev.AttributeType(t, parts[i]);
                if (r.Type.Members.Any(x => x is UnknownType)) return null;
                t = r.Type;
            }
            return null;
        }
        return null;   // not even the package is found: it may not be installed here, which another inspection says
    }

    /// <summary>The value surely has no such attribute: a module, class or instance that is fully known, without dynamic attributes.</summary>
    static bool Lacks(TypeEvaluator ev, PyType t, string name)
    {
        if (t.Members.Count == 0 || t.Members.Any(x => x is not (ClassType or ModuleType))) return false;
        foreach (var x in t.Members)
        {
            if (x is ModuleType mt && (mt.Module.IsBundledStub || mt.Module.Source.Contains("__getattr__") || mt.Module.Source.Contains("globals()") || mt.Module.Source.Contains("setattr(")))
                return false;
            if (x is ClassType c && (c.Info.Module.IsBundledStub || name.StartsWith('_') && c.Info.Module.IsStub)) return false;
        }
        var r = ev.AttributeType(t, name);
        return r.Missing.Count == t.Members.Count;
    }
}
