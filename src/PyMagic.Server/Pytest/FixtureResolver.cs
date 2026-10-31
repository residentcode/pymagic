using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server.Pytest;

/// <summary>What a fixture name means for one test: a fixture of the project, one of pytest's, or nothing known.</summary>
internal sealed record FixtureResolution(FixtureDef? Def, BuiltinFixture? Builtin)
{
    public static readonly FixtureResolution None = new(null, null);
    public bool Found => Def != null || Builtin != null;
}

/// <summary>
/// pytest's lookup rules: a fixture is visible from the class of the test, then its module (also what the module imports),
/// then the conftest.py files from the test's directory upwards, then the plugins (installed ones and those `pytest_plugins` names);
/// the nearest definition wins, and a fixture that asks for its own name gets the definition it overrides.
/// </summary>
internal sealed class FixtureResolver
{
    readonly Func<Workspace, ModuleInfo, IReadOnlyList<ModuleInfo>> _conftests;
    readonly Func<Workspace, ModuleInfo, IReadOnlyList<ModuleInfo>> _plugins;
    readonly Dictionary<object, List<FixtureDef>> _defined = new(ReferenceEqualityComparer.Instance);   // keyed by the class, or by the module for module level

    /// <param name="conftests">The conftest.py modules that apply to a module, nearest first, not including the module itself.</param>
    /// <param name="plugins">The plugin modules pytest loads for a module: installed plugins and those its conftest files name.</param>
    public FixtureResolver(Func<Workspace, ModuleInfo, IReadOnlyList<ModuleInfo>> conftests, Func<Workspace, ModuleInfo, IReadOnlyList<ModuleInfo>>? plugins = null)
    {
        _conftests = conftests;
        _plugins = plugins ?? ((_, _) => Array.Empty<ModuleInfo>());
    }

    /// <summary>The plugin modules that apply to a module.</summary>
    public IReadOnlyList<ModuleInfo> PluginsOf(Workspace ws, ModuleInfo m) => _plugins(ws, m);

    // ------------------------------------------------------------ lookup

    /// <summary>The classes around a node, innermost first (the node itself counts when it is a class).</summary>
    static IEnumerable<ClassDef> ClassesAround(ModuleInfo m, Node? context)
    {
        for (var p = context; p != null; p = m.ParentOf(p))
            if (p is ClassDef c) yield return c;
    }

    /// <summary>The fixture <paramref name="name"/> means where <paramref name="context"/> asks for it.</summary>
    /// <param name="exclude">The fixture doing the asking: asking for one's own name reaches the definition it overrides.</param>
    public FixtureResolution Resolve(Workspace ws, ModuleInfo m, Node? context, string name, FunctionDef? exclude = null)
    {
        foreach (var scope in Chain(ws, m, context))
            foreach (var f in scope)
                if (f.Name == name && !ReferenceEquals(f.Def, exclude)) return new FixtureResolution(f, null);
        return FixtureModel.Builtins.TryGetValue(name, out var b) ? new FixtureResolution(null, b) : FixtureResolution.None;
    }

    /// <summary>Every fixture visible from a place, nearest definition per name, then pytest's own.</summary>
    public IReadOnlyList<FixtureResolution> Visible(Workspace ws, ModuleInfo m, Node? context)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<FixtureResolution>();
        foreach (var scope in Chain(ws, m, context))
            foreach (var f in scope)
                if (seen.Add(f.Name)) result.Add(new FixtureResolution(f, null));
        foreach (var b in FixtureModel.Builtins.Values)
            if (seen.Add(b.Name)) result.Add(new FixtureResolution(null, b));
        return result;
    }

    IEnumerable<IReadOnlyList<FixtureDef>> Chain(Workspace ws, ModuleInfo m, Node? context)
    {
        foreach (var cls in ClassesAround(m, context))
            foreach (var (module, c) in Lineage(ws, m, cls).Classes) yield return DefinedIn(ws, module, c);
        yield return DefinedIn(ws, m, null);
        foreach (var conf in _conftests(ws, m)) yield return DefinedIn(ws, conf, null);
        // objects the module or a conftest registers as plugins at run time: config.pluginmanager.register(Helpers())
        foreach (var source in new[] { m }.Concat(_conftests(ws, m)))
            foreach (var (module, cls) in FixtureHooks.RegisteredPluginClasses(ws, source)) yield return DefinedIn(ws, module, cls);
        foreach (var plugin in _plugins(ws, m)) yield return DefinedIn(ws, plugin, null);
    }

    /// <summary>A class and its base classes, nearest first; <c>Complete</c> is false when a base could not be found.</summary>
    public (List<(ModuleInfo Module, ClassDef Class)> Classes, bool Complete) Lineage(Workspace ws, ModuleInfo m, ClassDef cls)
    {
        var result = new List<(ModuleInfo, ClassDef)>();
        bool complete = true;
        void Walk(ModuleInfo module, ClassDef c, int depth)
        {
            if (depth > 8 || result.Any(x => ReferenceEquals(x.Item2, c))) return;
            result.Add((module, c));
            foreach (var b in c.Bases)
            {
                if (b.Name != null || b.Stars != 0) continue;
                if (b.Value is NameExpr { Name: "object" }) continue;
                if (ws.Evaluator.TypeOf(module, b.Value) is ClassType { IsInstance: false } bt && !bt.Info.Module.IsStub) Walk(bt.Info.Module, bt.Info.Node, depth + 1);
                else complete = false;
            }
        }
        Walk(m, cls, 0);
        return (result, complete);
    }

    /// <summary>
    /// True when everything a function can ask for is visible: it is not in a class, or every class around it is a test class
    /// (named Test*) whose bases are all known. Helper and base classes get their fixtures from the classes that derive from them.
    /// </summary>
    public bool IsClosedWorld(Workspace ws, ModuleInfo m, FunctionDef f)
    {
        foreach (var cls in ClassesAround(m, f))
            if (!cls.Name.StartsWith("Test") || !Lineage(ws, m, cls).Complete) return false;
        return true;
    }

    // ------------------------------------------------------------ definitions

    /// <summary>The fixtures a module (or one class of it) defines or imports.</summary>
    public IReadOnlyList<FixtureDef> DefinedIn(Workspace ws, ModuleInfo module, ClassDef? cls, int depth = 0)
    {
        object key = (object?)cls ?? module;
        if (_defined.TryGetValue(key, out var cached)) return cached;
        var found = new List<FixtureDef>();
        _defined[key] = found;   // a module that imports itself through a cycle sees what is found so far

        var scope = cls == null ? module.Semantics.ModuleScope : module.Semantics.ScopeOf(cls);
        if (scope != null)
        {
            foreach (var sym in scope.Symbols.Values)
            {
                if (sym.Redirect != null) continue;
                (FunctionDef Def, ModuleInfo Module)? hit = null;
                bool alias = false;
                for (int i = sym.Declarations.Count - 1; i >= 0 && hit == null; i--)
                {
                    var d = sym.Declarations[i];
                    if (d.Kind == DeclKind.Function && d.Node is FunctionDef fd) hit = (fd, module);
                    else if (d.Kind == DeclKind.Import && cls == null && d.Node is ImportAlias import) hit = Follow(ws, module, import);
                    else if (d.Kind == DeclKind.Variable && d.Node is NameExpr target && module.ParentOf(target) is AssignStmt { Value: NameExpr source }
                             && scope.Symbols.TryGetValue(source.Name, out var original) && original != sym)
                    {
                        // `second = first`: pytest registers a fixture under the name of the attribute that holds it
                        foreach (var od in original.Declarations)
                            if (od.Kind == DeclKind.Function && od.Node is FunctionDef ofd) { hit = (ofd, module); alias = true; }
                    }
                }
                if (hit is { } h && FixtureModel.Parse(h.Module, h.Def) is { } fx)
                {
                    if (alias && fx.NameArg == null) fx = fx with { Name = sym.Name };
                    if (!found.Any(x => ReferenceEquals(x.Def, fx.Def) && x.Name == fx.Name)) found.Add(fx);
                }
            }

            // `from fixtures import *` brings in whatever fixtures that module defines
            if (cls == null && depth < 3)
                foreach (var star in module.Ast.Body.OfType<ImportFromStmt>().Where(s => s.IsStar))
                {
                    var tm = ws.GetModule(Workspace.AbsoluteName(module, star.Level, star.Module) ?? "");
                    if (tm == null || ReferenceEquals(tm, module)) continue;
                    foreach (var fx in DefinedIn(ws, tm, null, depth + 1))
                        if (!found.Any(x => ReferenceEquals(x.Def, fx.Def))) found.Add(fx);
                }
        }
        return found;
    }

    /// <summary>`from conftest_helpers import db`: the function the imported name ends up at.</summary>
    static (FunctionDef, ModuleInfo)? Follow(Workspace ws, ModuleInfo module, ImportAlias alias)
    {
        var current = module;
        var node = alias;
        for (int hops = 0; hops < 6; hops++)
        {
            if (current.ParentOf(node) is not ImportFromStmt { IsStar: false } from) return null;
            var tm = ws.GetModule(Workspace.AbsoluteName(current, from.Level, from.Module) ?? "");
            var sym = tm == null ? null : ws.Evaluator.FindModuleSymbol(tm, node.Name);
            if (sym == null) return null;
            sym = sym.Redirect ?? sym;
            var owner = ws.ModuleOf(sym) ?? tm;
            for (int i = sym.Declarations.Count - 1; i >= 0; i--)
            {
                var d = sym.Declarations[i];
                if (d.Kind == DeclKind.Function && d.Node is FunctionDef fd) return (fd, owner);
                if (d.Kind == DeclKind.Import && d.Node is ImportAlias next) { current = owner; node = next; goto again; }
            }
            return null;
        again:;
        }
        return null;
    }

    // ------------------------------------------------------------ for the evaluator

    /// <summary>The type a test or fixture parameter has inside the function: what the fixture it names provides.</summary>
    public PyType? TypeOfParameter(Workspace ws, ModuleInfo m, Parameter p)
    {
        if (m.ParentOf(p) is not FunctionDef f || m.IsStub || !FixtureModel.RequestsFixtures(m, f)) return null;
        if (!FixtureModel.RequestedParameters(m, f).Any(x => ReferenceEquals(x, p))) return null;
        var res = Resolve(ws, m, f, p.Name, FixtureModel.IsFixture(f) ? f : null);
        var ev = ws.Evaluator;
        if (res.Def != null)
        {
            var t = ev.FixtureValueType(res.Def.Module, res.Def.Def);
            return t is UnknownType ? null : t;
        }
        if (res.Builtin is { Module: not null, Class: not null } b)
        {
            var t = ev.InstanceOf(b.Module, b.Class, b.StrArgument ? new[] { ev.BuiltinInstance("str") } : Array.Empty<PyType>());
            // request in a fixture with params=[...], or that tests parametrize with indirect=True: request.param is one of those values
            if (b.Name == "request" && t is ClassType request && FixtureModel.IsFixture(f))
            {
                var values = (ParamsOf(f) ?? Array.Empty<Expr>()).Select(v => (m, v)).ToList();
                if (FixtureModel.Parse(m, f) is { } self) values.AddRange(FixtureHooks.IndirectValues(ws.UserModules, self.Name));
                if (values.Count > 0)
                {
                    var param = PyType.Union(values.Select(v => ev.TypeOf(v.Item1, v.Item2)).Select(x => x is ClassType { LiteralValue: not null } lit ? lit.Widened() : x));
                    if (!param.Members.Any(x => x is UnknownType)) return ev.WithAttributes(m, f, request, new[] { ("param", param) });
                }
            }
            return t;
        }
        // an argument pytest_generate_tests gives: the values of its metafunc.parametrize
        if (FixtureModel.IsTestFunction(m, f))
        {
            var given = new[] { m }.Concat(_conftests(ws, m)).SelectMany(x => FixtureHooks.GeneratedArguments(x).Names.Select(n => (Module: x, n.Name, n.Call, n.Index, n.Count)))
                .Where(n => n.Name == p.Name).ToList();
            var types = given.SelectMany(g => FixtureHooks.ValuesFor(g.Call, g.Index, g.Count).Select(v => ev.TypeOf(g.Module, v))).Select(x => x is ClassType { LiteralValue: not null } lit ? lit.Widened() : x).ToList();
            if (types.Count > 0 && !types.Any(x => x is UnknownType)) return PyType.Union(types);
        }
        return null;
    }

    /// <summary>The values of `@pytest.fixture(params=[...])` written as a list or tuple; null when the fixture has none (or they are not written out).</summary>
    static IReadOnlyList<Expr>? ParamsOf(FunctionDef f)
    {
        foreach (var d in f.Decorators.OfType<CallExpr>())
            if (d.Args.FirstOrDefault(a => a.Name == "params")?.Value is { } v)
                return v switch { ListExpr l when l.Items.Count > 0 => l.Items, TupleExpr t when t.Items.Count > 0 => t.Items, _ => null };
        return null;
    }
}
