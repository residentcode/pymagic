using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>FastAPI, Starlette and Flask in the editor: the routes of the project (with the prefixes routers and blueprints add), checks between a path and the function behind it, endpoint names and test-client URLs that lead to a route, and dependency types.</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ the routes

    /// <summary>One `@app.get("/items/{id}")` or `@bp.route("/items/<int:id>")` decorator: the route a function answers.</summary>
    sealed record RouteDef(ModuleInfo Module, FunctionDef Def, bool Flask, IReadOnlyList<string> Methods, string Path, string Own, int PathStart, int PathEnd, string Endpoint, string Plain, string Receiver, string Prefix, RouterInfo? Info);

    sealed record RouterInfo(string Kind, string Prefix, string? BlueprintName);

    sealed class RouteIndex
    {
        public List<RouteDef> Routes { get; } = new();
    }

    static readonly Regex WebFramework = new(@"\b(?:fastapi|starlette|flask|quart)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    static readonly HashSet<string> RouteVerbs = new(StringComparer.Ordinal) { "get", "post", "put", "delete", "patch", "head", "options", "trace" };
    static readonly HashSet<string> RouterConstructors = new(StringComparer.Ordinal) { "FastAPI", "APIRouter", "Starlette", "Router", "Flask", "Blueprint", "Quart" };
    static readonly HashSet<string> FlaskConstructors = new(StringComparer.Ordinal) { "Flask", "Blueprint", "Quart" };
    static readonly Regex FastApiParam = new(@"\{(?<n>\w+)(?::(?<c>\w+))?\}", RegexOptions.Compiled);
    static readonly Regex FlaskParam = new(@"<(?:(?<c>[\w.]+)(?:\([^)]*\))?:)?(?<n>\w+)>", RegexOptions.Compiled);

    readonly ConditionalWeakTable<Snapshot, RouteIndex> _routeIndexes = new();

    RouteIndex RouteIndexOf(Snapshot snap) => _routeIndexes.GetValue(snap, BuildRouteIndex);

    static string LastSegment(string name) => name.Contains('.') ? name[(name.LastIndexOf('.') + 1)..] : name;

    static string? ReceiverName(Expr e) => e switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };

    /// <summary>`prefix` + `path`, with the slash between them once.</summary>
    static string JoinPath(string prefix, string path)
    {
        if (path.Length == 0) return prefix;
        if (prefix.Length == 0) return path.StartsWith('/') ? path : "/" + path;
        return prefix.TrimEnd('/') + (path.StartsWith('/') ? path : "/" + path);
    }

    /// <summary>Where the router named in `app.include_router(users.router)` is defined: the module and the variable.</summary>
    static (ModuleInfo, string)? RouterTarget(ModuleInfo at, Expr e, Dictionary<(ModuleInfo, string), RouterInfo> routers, IReadOnlyList<ModuleInfo> modules)
    {
        if (e is NameExpr n)
        {
            if (routers.ContainsKey((at, n.Name))) return (at, n.Name);
            foreach (var imp in AstChildren.Descendants(at.Ast).OfType<ImportFromStmt>())
                foreach (var alias in imp.Names.Where(a => (a.AsName ?? a.Name) == n.Name))
                {
                    if (imp.Module == null) continue;
                    var target = modules.FirstOrDefault(mm => LastSegment(mm.Name) == LastSegment(imp.Module) && routers.ContainsKey((mm, alias.Name)));
                    if (target != null) return (target, alias.Name);
                }
            var all = routers.Keys.Where(k => k.Item2 == n.Name).ToList();
            return all.Count == 1 ? all[0] : null;
        }
        if (e is AttributeExpr { Value: var q } a && ReceiverName(q) is { } qualifier)
        {
            var candidates = new List<string> { qualifier };
            foreach (var imp in AstChildren.Descendants(at.Ast).OfType<ImportFromStmt>())
                foreach (var alias in imp.Names.Where(x => x.AsName == qualifier)) candidates.Add(alias.Name);
            foreach (var imp in AstChildren.Descendants(at.Ast).OfType<ImportStmt>())
                foreach (var alias in imp.Names.Where(x => x.AsName == qualifier)) candidates.Add(LastSegment(alias.Name));
            var target = modules.FirstOrDefault(mm => candidates.Contains(LastSegment(mm.Name)) && routers.ContainsKey((mm, a.Attr)));
            if (target != null) return (target, a.Attr);
            var all = routers.Keys.Where(k => k.Item2 == a.Attr).ToList();
            return all.Count == 1 ? all[0] : null;
        }
        return null;
    }

    static string? StringKeyword(CallExpr call, params string[] names)
    {
        var arg = call.Args.FirstOrDefault(a => a.Name != null && names.Contains(a.Name));
        return arg == null ? null : FixtureModel.StringValue(arg.Value);
    }

    IEnumerable<string> StringItems(Expr? e) =>
        e switch
        {
            ListExpr l => l.Items.Select(i => FixtureModel.StringValue(i)).OfType<string>(),
            TupleExpr t => t.Items.Select(i => FixtureModel.StringValue(i)).OfType<string>(),
            SetExpr s => s.Items.Select(i => FixtureModel.StringValue(i)).OfType<string>(),
            _ => Enumerable.Empty<string>(),
        };

    RouteIndex BuildRouteIndex(Snapshot snap)
    {
        var index = new RouteIndex();
        var modules = snap.Project.Where(m => !m.IsStub && WebFramework.IsMatch(m.Source)).ToList();

        // the routers and applications, by module and variable
        var routers = new Dictionary<(ModuleInfo, string), RouterInfo>();
        foreach (var m in modules)
            foreach (var stmt in AstChildren.Descendants(m.Ast))
            {
                (Expr? target, Expr? value) = stmt switch
                {
                    AssignStmt { Targets.Count: 1 } a => (a.Targets[0], a.Value),
                    AnnAssignStmt a => (a.Target, a.Value),
                    _ => (null, null),
                };
                if (target is not NameExpr name || value is not CallExpr call || CalleeName(call) is not { } ctor || !RouterConstructors.Contains(ctor)) continue;
                string prefix = StringKeyword(call, "prefix", "url_prefix") ?? "";
                string? blueprint = ctor == "Blueprint" ? FixtureModel.StringValue(call.Args.FirstOrDefault(a => a.Name == null)?.Value) : null;
                routers[(m, name.Name)] = new RouterInfo(ctor, prefix, blueprint);
            }

        // what is included where: `app.include_router(users.router, prefix="/users")`
        var included = new Dictionary<(ModuleInfo, string), (string Prefix, (ModuleInfo, string)? Container)>();
        foreach (var m in modules)
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
            {
                if (call.Func is not AttributeExpr { Attr: "include_router" or "register_blueprint" or "mount" } access) continue;
                var first = call.Args.FirstOrDefault(a => a.Name == null || a.Name is "router" or "blueprint" or "app");
                if (first == null || RouterTarget(m, first.Value, routers, modules) is not { } child) continue;
                var container = RouterTarget(m, access.Value, routers, modules);
                string prefix = StringKeyword(call, "prefix", "url_prefix") ?? (access.Attr == "mount" ? FixtureModel.StringValue(call.Args.FirstOrDefault(a => a.Name == null)?.Value) ?? "" : "");
                included.TryAdd(child, (prefix, container));
            }

        string PrefixOf((ModuleInfo, string) key, int depth = 0)
        {
            string own = routers.TryGetValue(key, out var r) ? r.Prefix : "";
            if (depth < 6 && included.TryGetValue(key, out var inc))
                return JoinPath(JoinPath(inc.Container is { } c ? PrefixOf(c, depth + 1) : "", inc.Prefix), own);
            return own;
        }

        foreach (var module in modules)
        {
            (string Prefix, RouterInfo? Info) Resolve((Expr? Expr, string? Name) key)
            {
                (ModuleInfo, string)? resolved = null;
                if (key.Name != null && routers.ContainsKey((module, key.Name))) resolved = (module, key.Name);
                else if (key.Expr != null) resolved = RouterTarget(module, key.Expr, routers, modules);
                return resolved is { } k ? (PrefixOf(k), routers.GetValueOrDefault(k)) : ("", null);
            }
            index.Routes.AddRange(RoutesIn(module, Resolve));
        }
        return index;
    }

    /// <summary>The routes the functions of a module declare; `resolve` turns the receiver of a decorator into its prefix and kind.</summary>
    IEnumerable<RouteDef> RoutesIn(ModuleInfo m, Func<(Expr? Expr, string? Name), (string Prefix, RouterInfo? Info)> resolve)
    {
        foreach (var def in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
            foreach (var decorator in def.Decorators)
            {
                if (decorator is not CallExpr { Func: AttributeExpr access } call) continue;
                string verb = access.Attr;
                bool isVerb = RouteVerbs.Contains(verb);
                if (!isVerb && verb is not ("route" or "api_route" or "websocket")) continue;
                if (StringIn(call, 0, "path", "rule") is not { } path) continue;
                if (isVerb && path.Value.Length > 0 && !path.Value.StartsWith('/')) continue;

                var methods = verb switch
                {
                    "websocket" => new List<string> { "WS" },
                    "route" or "api_route" => StringItems(call.Args.FirstOrDefault(a => a.Name == "methods")?.Value).Select(x => x.ToUpperInvariant()).ToList(),
                    _ => new List<string> { verb.ToUpperInvariant() },
                };
                if (methods.Count == 0) methods.Add("GET");

                string? receiver = ReceiverName(access.Value);
                var (prefix, info) = resolve((access.Value, receiver));
                bool flask = info != null ? FlaskConstructors.Contains(info.Kind) : path.Value.Contains('<');
                string plain = StringKeyword(call, flask ? "endpoint" : "name") ?? def.Name;
                string endpoint = info?.BlueprintName != null ? $"{info.BlueprintName}.{plain}" : plain;
                yield return new RouteDef(m, def, flask, methods, JoinPath(prefix, path.Value) is { Length: > 0 } full ? full : "/", path.Value, path.Start, path.End, endpoint, plain, receiver ?? "", prefix, info);
            }
    }

    static IEnumerable<(string Name, string? Converter, int Index)> PathParameters(RouteDef r) =>
        (r.Flask ? FlaskParam : FastApiParam).Matches(r.Own).Select(x => (x.Groups["n"].Value, x.Groups["c"].Success ? x.Groups["c"].Value : null, x.Groups["n"].Index));

    static string NormalizedPath(RouteDef r) => (r.Flask ? FlaskParam : FastApiParam).Replace(r.Path, "{}").TrimEnd('/');

    static Regex PatternOf(RouteDef r)
    {
        var sb = new System.Text.StringBuilder("^");
        var rx = r.Flask ? FlaskParam : FastApiParam;
        int at = 0;
        foreach (Match x in rx.Matches(r.Path))
        {
            sb.Append(Regex.Escape(r.Path[at..x.Index]));
            sb.Append(x.Groups["c"].Success && x.Groups["c"].Value == "path" ? ".+" : "[^/]+");
            at = x.Index + x.Length;
        }
        sb.Append(Regex.Escape(r.Path[at..])).Append("/?$");
        return new Regex(sb.ToString(), RegexOptions.Compiled);
    }

    /// <summary>The routes a concrete URL such as "/items/3?x=1" (written in a test client call) reaches.</summary>
    static List<RouteDef> RoutesForUrl(RouteIndex index, string method, string url)
    {
        int cut = url.IndexOfAny(new[] { '?', '#' });
        string path = cut >= 0 ? url[..cut] : url;
        var matching = index.Routes.Where(r => PatternOf(r).IsMatch(path)).ToList();
        // a fixed path beats one with parameters (as the frameworks match them in order, the first declared wins otherwise)
        var withMethod = matching.Where(r => r.Methods.Contains(method)).ToList();
        var chosen = withMethod.Count > 0 ? withMethod : matching;
        if (chosen.Count == 0) return chosen;
        int fewest = chosen.Min(r => NormalizedPath(r).Count(c => c == '{'));
        return chosen.Where(r => NormalizedPath(r).Count(c => c == '{') == fewest).ToList();
    }

    static string RouteLine(RouteDef r) => $"{string.Join("|", r.Methods)} {r.Path}";

    // ------------------------------------------------------------ the list of endpoints

    /// <summary>Every route of the project, for the Endpoints view.</summary>
    public IReadOnlyList<RouteEntry> Routes()
    {
        var snap = Full();
        return RouteIndexOf(snap).Routes
            .Select(r => new RouteEntry(string.Join(", ", r.Methods), r.Path, r.Def.Name, r.Flask ? "flask" : "fastapi",
                UriOfModule(r.Module) ?? "", Lines(r.Module).PositionOf(r.Def.NameStart).Line))
            .Where(e => e.Uri.Length > 0)
            .ToList();
    }

    // ------------------------------------------------------------ go to, hover

    IReadOnlyList<Location> RouteTargets(Snapshot snap, NamedString s)
    {
        var index = RouteIndexOf(snap);
        IEnumerable<RouteDef> routes = s.Kind == DjangoNamed.Endpoint
            ? EndpointRoutes(index, s.Value)
            : RoutesForUrl(index, MethodOfClientCall(s.Call), s.Value);
        return routes.Select(r => FixtureLocation(r.Module, r.Def.NameStart, r.Def.NameStart + r.Def.Name.Length)).OfType<Location>().ToList();
    }

    static IEnumerable<RouteDef> EndpointRoutes(RouteIndex index, string used)
    {
        var exact = index.Routes.Where(r => r.Endpoint == used).ToList();
        return exact.Count > 0 ? exact : index.Routes.Where(r => r.Plain == used.TrimStart('.'));
    }

    static string MethodOfClientCall(CallExpr? call) => call?.Func is AttributeExpr a ? a.Attr.ToUpperInvariant() : "GET";

    Hover? RouteHover(Snapshot snap, NamedString s, Range range)
    {
        var index = RouteIndexOf(snap);
        var routes = s.Kind == DjangoNamed.Endpoint ? EndpointRoutes(index, s.Value).ToList() : RoutesForUrl(index, MethodOfClientCall(s.Call), s.Value);
        if (routes.Count == 0) return null;
        string title = s.Kind == DjangoNamed.Endpoint ? "(endpoint) " + s.Value : "(route) " + s.Value;
        return new Hover(new MarkupContent("markdown", "```python\n" + title + "\n```\n\n" + string.Join("\n\n", routes.Select(r => $"`{RouteLine(r)}` — `{r.Def.Name}()` in {System.IO.Path.GetFileName(r.Module.Path ?? r.Module.Name)}"))), range);
    }

    /// <summary>The decorator's own path string: the full path with the prefixes of its routers.</summary>
    Hover? RouteDeclarationHover(Snapshot snap, ModuleInfo m, int offset)
    {
        var route = RouteIndexOf(snap).Routes.FirstOrDefault(r => SameModule(r.Module, m) && offset >= r.PathStart && offset <= r.PathEnd);
        if (route == null) return null;
        var parameters = PathParameters(route).Select(p => "`" + p.Name + "`" + (p.Converter != null ? $" ({p.Converter})" : "")).ToList();
        string body = $"`{RouteLine(route)}` → `{route.Def.Name}()`" + (route.Path != route.Own ? $"\n\nDeclared as `{route.Own}`; the full path comes from the prefixes of its routers." : "")
            + (parameters.Count > 0 ? "\n\nPath parameters: " + string.Join(", ", parameters) : "")
            + $"\n\nEndpoint name: `{route.Endpoint}`";
        return new Hover(new MarkupContent("markdown", "```python\n(route) " + RouteLine(route) + "\n```\n\n" + body), Lines(m).RangeOf(route.PathStart, route.PathEnd));
    }

    static bool SameModule(ModuleInfo a, ModuleInfo b) => ReferenceEquals(a, b) || (a.Path != null && string.Equals(a.Path, b.Path, StringComparison.OrdinalIgnoreCase)) || (a.Path == null && b.Path == null && a.Name == b.Name);

    // ------------------------------------------------------------ inspections

    IEnumerable<Diag> WebRouteDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || !WebFramework.IsMatch(m.Source)) yield break;
        var snap = Current();
        var index = RouteIndexOf(snap);
        // the routes of this very text: positions must be exact, prefixes come from the project
        (string Prefix, RouterInfo? Info) Resolve((Expr? Expr, string? Name) key)
        {
            var known = index.Routes.FirstOrDefault(r => SameModule(r.Module, m) && key.Name != null && r.Receiver == key.Name);
            return known != null ? (known.Prefix, known.Info) : ("", null);
        }
        var local = RoutesIn(m, Resolve).ToList();

        var project = snap.Project.ToList();
        int OrderOf(RouteDef r) => project.FindIndex(p => SameModule(p, r.Module));

        foreach (var r in local)
        {
            var findings = new List<Finding>();
            var def = r.Def;
            bool takesAny = def.Params.Any(p => p.Kind == ParamKind.KwArgs);
            bool injected = !r.Flask && (def.Params.Any(p => p.Default is CallExpr c && CalleeName(c) is "Depends" or "Security") || def.Params.Any(p => p.Annotation != null && m.Source.AsSpan(p.Annotation.Start, p.Annotation.End - p.Annotation.Start).Contains("Depends".AsSpan(), StringComparison.Ordinal))
                || def.Decorators.OfType<CallExpr>().Any(d => d.Args.Any(a => a.Name == "dependencies")));
            if (!takesAny && !injected)
                foreach (var (name, converter, at) in PathParameters(r))
                    if (!def.Params.Any(p => p.Name == name))
                        findings.Add(new Finding(r.PathStart + at, name.Length, $"Path parameter '{name}' is not a parameter of {def.Name}()", "route-path-parameter", r.Flask ? FindingLevel.Warning : FindingLevel.Weak));

            if (r.Flask && !takesAny && m.ParentOf(def) is not ClassDef && def.Decorators.All(d => d is CallExpr { Func: AttributeExpr { Attr: "route" or "get" or "post" or "put" or "delete" or "patch" } }))
            {
                var inPath = local.Where(x => ReferenceEquals(x.Def, def)).SelectMany(PathParameters).Select(p => p.Name).ToHashSet();
                foreach (var p in def.Params)
                    if (p.Kind == ParamKind.Normal && p.Default == null && !inPath.Contains(p.Name))
                        findings.Add(new Finding(p.Start, p.Name.Length, $"Parameter '{p.Name}' is not a part of the route '{r.Own}': Flask passes only the path's values", "route-unknown-parameter", FindingLevel.Warning));
            }

            string norm = NormalizedPath(r);
            var other = index.Routes.FirstOrDefault(o => !(SameModule(o.Module, m) && o.PathStart == r.PathStart) && NormalizedPath(o) == norm && o.Methods.Intersect(r.Methods).Any()
                && (OrderOf(o) < OrderOf(r) || OrderOf(o) == OrderOf(r) && o.PathStart < r.PathStart));
            if (other != null)
                findings.Add(new Finding(r.PathStart, r.PathEnd - r.PathStart, $"Route '{r.Methods.First(x => other.Methods.Contains(x))} {r.Path}' is already handled by {other.Def.Name}() in {System.IO.Path.GetFileName(other.Module.Path ?? other.Module.Name)}", "duplicate-route", FindingLevel.Warning));

            foreach (var f in findings)
                if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }

        if (index.Routes.Count == 0) yield break;
        foreach (var s in NamedStringsIn(m, ev).Where(x => x.Kind == DjangoNamed.Endpoint))
        {
            if (s.Value.StartsWith('.') || s.Value == "static" || s.Value.EndsWith(".static") || EndpointRoutes(index, s.Value).Any()) continue;
            var f = new Finding(s.Start, s.End - s.Start, $"No route has the endpoint name '{s.Value}'", "route-endpoint", FindingLevel.Weak);
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
        }
    }

    // ------------------------------------------------------------ quick fix: add the missing path parameter

    void AddPathParameterFix(ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add)
    {
        string text = m.Source;
        string name = text[start..end];
        var def = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(f => f.Decorators.Any(d => d.Start <= start && start < d.End));
        if (def == null) return;
        var route = RoutesIn(m, _ => ("", null)).FirstOrDefault(r => ReferenceEquals(r.Def, def) && r.PathStart <= start && start < r.PathEnd);
        string converter = route != null ? PathParameters(route).FirstOrDefault(p => p.Name == name).Converter ?? "" : "";
        string type = converter switch { "int" => "int", "float" => "float", _ => "str" };
        string decl = $"{name}: {type}";

        var normal = def.Params.Where(p => p.Kind is ParamKind.Normal or ParamKind.VarArgs or ParamKind.KwArgs or ParamKind.KwOnlySeparator or ParamKind.Slash).ToList();
        var before = normal.FirstOrDefault(p => p.Default != null || p.Kind is ParamKind.VarArgs or ParamKind.KwArgs or ParamKind.KwOnlySeparator);
        Edit edit;
        if (before != null) edit = new Edit(before.Start, before.Start, decl + ", ");
        else if (normal.Count > 0) edit = new Edit(normal[^1].End, normal[^1].End, ", " + decl);
        else
        {
            int open = text.IndexOf('(', def.NameStart);
            if (open < 0) return;
            edit = new Edit(open + 1, open + 1, decl);
        }
        add($"Add parameter '{decl}'", true, new[] { edit });
    }

    // ------------------------------------------------------------ dependencies

    /// <summary>`db = Depends(get_db)` without an annotation: what the dependency returns.</summary>
    static PyType? DependencyParameterType(Workspace ws, ModuleInfo m, Parameter p)
    {
        if (p.Default is not CallExpr call || m.IsStub || CalleeName(call) is not ("Depends" or "Security")) return null;
        var dep = call.Args.FirstOrDefault(a => a.Name == null || a.Name == "dependency");
        if (dep == null) return null;
        var t = ws.Evaluator.DependencyValueType(m, dep.Value);
        return t is UnknownType ? null : t;
    }

    // ------------------------------------------------------------ completion

    /// <summary>Endpoint names inside url_for("..."), and the parameters of the function inside the braces of a route path.</summary>
    IReadOnlyList<CompletionItem>? WebStringCompletion(Doc doc, int start, int offset)
    {
        string text = doc.Text;
        int lineStart = LineStart(text, offset);
        string before = text[lineStart..offset];
        var items = new List<CompletionItem>();

        var endpoint = Regex.Match(before, @"\b(?:url_for|url_path_for)\s*\(\s*[""'](?<typed>[\w.\-]*)$");
        if (endpoint.Success)
        {
            string typed = endpoint.Groups["typed"].Value;
            foreach (var r in RouteIndexOf(Full()).Routes.GroupBy(r => r.Endpoint).Select(g => g.First()).OrderBy(r => r.Endpoint, StringComparer.Ordinal))
                if (r.Endpoint.StartsWith(typed, StringComparison.Ordinal))
                    items.Add(new CompletionItem(r.Endpoint, CompletionKind.Function, RouteLine(r), "1" + r.Endpoint));
            return items;
        }

        // `@app.get("/items/{it|")`, `@bp.route("/items/<int:it|")`
        var path = Regex.Match(before, @"^\s*@\w[\w.]*\.(?:get|post|put|delete|patch|head|options|trace|route|api_route|websocket)\s*\(\s*[""'][^""']*?(?:\{|<(?:\w+(?:\([^)]*\))?:)?)(?<typed>\w*)$");
        if (path.Success)
        {
            string typed = path.Groups["typed"].Value;
            var def = FunctionAfter(text, offset);
            if (def == null) return items;
            var used = Regex.Matches(text[LineStart(text, offset)..LineEndOf(text, offset)], @"[{<](?:\w+(?:\([^)]*\))?:)?(\w+)[}>]").Select(x => x.Groups[1].Value).ToHashSet();
            foreach (var p in def.Value.Params)
                if (p.StartsWith(typed, StringComparison.Ordinal) && !used.Contains(p))
                    items.Add(new CompletionItem(p, CompletionKind.Variable, "parameter", "1" + p));
            return items;
        }
        return null;
    }

    static int LineEndOf(string text, int offset)
    {
        int i = offset;
        while (i < text.Length && text[i] != '\n' && text[i] != '\r') i++;
        return i;
    }

    /// <summary>The names of the parameters of the first `def` after the offset (the function a decorator belongs to), read from the text.</summary>
    static (string Name, List<string> Params)? FunctionAfter(string text, int offset)
    {
        var m = Regex.Match(text[offset..], @"\bdef\s+(?<name>\w+)\s*\((?<params>[^)]*)\)");
        if (!m.Success) return null;
        var names = Regex.Matches(m.Groups["params"].Value, @"(?:^|,)\s*\**(\w+)").Select(x => x.Groups[1].Value).ToList();
        return (m.Groups["name"].Value, names);
    }
}
