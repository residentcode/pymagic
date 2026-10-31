using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>The endpoints of a Django project, read from its urls.py files without running anything: `path()` and `re_path()` with their `include()`s, class and function views, and the routes a REST framework router makes of a viewset.</summary>
public sealed partial class LanguageService
{
    static readonly Regex DjangoConverter = new(@"<(?:(?<c>\w+):)?(?<n>\w+)>", RegexOptions.Compiled);
    static readonly Regex DjangoRegexGroup = new(@"\(\?P<(?<n>\w+)>[^)]*\)", RegexOptions.Compiled);

    static readonly string[] HandlerVerbs = { "get", "post", "put", "patch", "delete", "head", "options" };

    /// <summary>What the generic views of Django and of REST framework answer, by the name of the class that brings it.</summary>
    static readonly Dictionary<string, string[]> GenericMethods = new(StringComparer.Ordinal)
    {
        ["ListAPIView"] = new[] { "GET" }, ["CreateAPIView"] = new[] { "POST" }, ["ListCreateAPIView"] = new[] { "GET", "POST" }, ["RetrieveAPIView"] = new[] { "GET" },
        ["UpdateAPIView"] = new[] { "PUT", "PATCH" }, ["DestroyAPIView"] = new[] { "DELETE" }, ["RetrieveUpdateAPIView"] = new[] { "GET", "PUT", "PATCH" },
        ["RetrieveDestroyAPIView"] = new[] { "GET", "DELETE" }, ["RetrieveUpdateDestroyAPIView"] = new[] { "GET", "PUT", "PATCH", "DELETE" },
        ["ListView"] = new[] { "GET" }, ["DetailView"] = new[] { "GET" }, ["TemplateView"] = new[] { "GET" }, ["RedirectView"] = new[] { "GET" },
        ["CreateView"] = new[] { "GET", "POST" }, ["UpdateView"] = new[] { "GET", "POST" }, ["FormView"] = new[] { "GET", "POST" }, ["DeleteView"] = new[] { "GET", "POST" },
    };

    // ------------------------------------------------------------ reading urlpatterns

    sealed record DjangoRouter(ModuleInfo Module, string Name);

    /// <summary>The url patterns a module (or a name or list in it) holds: the `path(...)` calls and the `router.urls` it adds, with lists, `+`, names and `format_suffix_patterns(...)` followed.</summary>
    IEnumerable<Expr> PatternItems(ModuleInfo m, Expr e, int depth = 0)
    {
        if (depth > 8) yield break;
        switch (e)
        {
            case ListExpr l:
                foreach (var item in l.Items) foreach (var x in PatternItems(m, item, depth + 1)) yield return x;
                break;
            case TupleExpr t:
                foreach (var item in t.Items) foreach (var x in PatternItems(m, item, depth + 1)) yield return x;
                break;
            case BinaryExpr { Op: "+" } b:
                foreach (var x in PatternItems(m, b.Left, depth + 1)) yield return x;
                foreach (var x in PatternItems(m, b.Right, depth + 1)) yield return x;
                break;
            case CallExpr c when CalleeName(c) is "format_suffix_patterns" or "i18n_patterns":
                foreach (var a in c.Args.Where(a => a.Name == null)) foreach (var x in PatternItems(m, a.Value, depth + 1)) yield return x;
                break;
            case CallExpr c when CalleeName(c) is "path" or "re_path" or "url":
                yield return c;
                break;
            case AttributeExpr { Attr: "urls" }:
                yield return e;
                break;
            case NameExpr n:
                foreach (var st in m.Ast.Body)
                {
                    if (st is AssignStmt { Targets.Count: 1 } a && a.Targets[0] is NameExpr t2 && t2.Name == n.Name) foreach (var x in PatternItems(m, a.Value, depth + 1)) yield return x;
                    else if (st is AugAssignStmt { Op: "+=" } aug && aug.Target is NameExpr t3 && t3.Name == n.Name) foreach (var x in PatternItems(m, aug.Value, depth + 1)) yield return x;
                }
                break;
        }
    }

    /// <summary>The patterns of a urls module: whatever `urlpatterns` is assigned and extended with.</summary>
    IEnumerable<Expr> PatternsOf(ModuleInfo m) => PatternItems(m, new NameExpr("urlpatterns"));

    /// <summary>The module an `include("shop.urls")` names, among the project's.</summary>
    static ModuleInfo? IncludedModule(IReadOnlyList<ModuleInfo> modules, string name) =>
        modules.FirstOrDefault(x => x.Name == name) ?? modules.FirstOrDefault(x => name.EndsWith("." + x.Name, StringComparison.Ordinal) || x.Name.EndsWith("." + name, StringComparison.Ordinal));

    /// <summary>The path as it reads in the list: `{pk}` for `<int:pk>` and for a `(?P<pk>...)` group, regex anchors taken off, one slash in front.</summary>
    static string DisplayRoute(string raw)
    {
        string p = DjangoRegexGroup.Replace(raw, m => "{" + m.Groups["n"].Value + "}");
        p = DjangoConverter.Replace(p, m => "{" + m.Groups["n"].Value + "}").Replace("^", "").Replace("$", "");
        return p.StartsWith('/') ? p : "/" + p;
    }

    static IEnumerable<RouteParam> PathParamsOf(string raw)
    {
        foreach (Match g in DjangoRegexGroup.Matches(raw)) yield return new RouteParam(g.Groups["n"].Value, "path", "str", true, "regex group");
        // the `<year>` of a regex group is not a converter
        foreach (Match c in DjangoConverter.Matches(DjangoRegexGroup.Replace(raw, "")))
        {
            string conv = c.Groups["c"].Success ? c.Groups["c"].Value : "str";
            yield return new RouteParam(c.Groups["n"].Value, "path", conv switch { "int" => "int", "uuid" => "UUID", _ => "str" }, true, conv is "slug" or "path" ? conv : null);
        }
    }

    // ------------------------------------------------------------ the whole tree

    /// <summary>Every endpoint the project's urls.py files define, for the Endpoints view.</summary>
    public IReadOnlyList<RouteEntry> DjangoRoutes()
    {
        var snap = Full();
        var ev = snap.Workspace.Evaluator;
        var modules = snap.Project.Where(x => !x.IsStub).ToList();
        var urlModules = modules.Where(x => x.Source.Contains("urlpatterns", StringComparison.Ordinal) && PatternsOf(x).Any()).ToList();
        if (urlModules.Count == 0) return Array.Empty<RouteEntry>();

        // where to start: the module ROOT_URLCONF names, else those no other urls module includes
        string? root = modules.Select(x => Regex.Match(x.Source, @"^ROOT_URLCONF\s*=\s*['""](?<n>[\w.]+)['""]", RegexOptions.Multiline)).FirstOrDefault(x => x.Success)?.Groups["n"].Value;
        var starts = new List<ModuleInfo>();
        if (root != null && IncludedModule(modules, root) is { } rootModule && urlModules.Contains(rootModule)) starts.Add(rootModule);
        else
        {
            var named = urlModules.SelectMany(x => PatternsOf(x).OfType<CallExpr>()).SelectMany(c => c.Args).Select(a => a.Value).OfType<CallExpr>()
                .Where(c => CalleeName(c) == "include").SelectMany(c => c.Args.Take(1)).Select(a => FixtureModel.StringValue(a.Value)).Where(s => s != null).ToHashSet();
            starts.AddRange(urlModules.Where(x => !named.Any(n => IncludedModule(new[] { x }, n!) != null)));
            if (starts.Count == 0) starts.AddRange(urlModules);
        }

        var found = new List<RouteEntry>();
        foreach (var start in starts) WalkUrls(modules, ev, start, "", 0, new HashSet<ModuleInfo>(ReferenceEqualityComparer.Instance), found);
        return found.Where(e => e.Uri.Length > 0).ToList();
    }

    void WalkUrls(IReadOnlyList<ModuleInfo> modules, TypeEvaluator ev, ModuleInfo m, string prefix, int depth, HashSet<ModuleInfo> stack, List<RouteEntry> found)
    {
        if (depth > 6 || !stack.Add(m)) return;
        try { WalkPatterns(modules, ev, m, PatternsOf(m).ToList(), prefix, depth, stack, found); }
        finally { stack.Remove(m); }
    }

    void WalkPatterns(IReadOnlyList<ModuleInfo> modules, TypeEvaluator ev, ModuleInfo m, IReadOnlyList<Expr> patterns, string prefix, int depth, HashSet<ModuleInfo> stack, List<RouteEntry> found)
    {
        foreach (var pattern in patterns)
        {
            if (pattern is AttributeExpr { Attr: "urls", Value: var routerExpr })
            {
                ExpandRouter(modules, ev, m, routerExpr, prefix, found);
                continue;
            }
            if (pattern is not CallExpr call) continue;
            var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
            string route = positional.Count > 0 ? FixtureModel.StringValue(positional[0].Value) ?? "" : "";
            var view = positional.Count > 1 ? positional[1].Value : call.Args.FirstOrDefault(a => a.Name == "view")?.Value;
            if (view == null) continue;
            string joined = prefix + route;

            if (view is CallExpr { } inc && CalleeName(inc) == "include")
            {
                var arg = inc.Args.FirstOrDefault(a => a.Name == null)?.Value;
                if (arg is TupleExpr { Items.Count: > 0 } pair) arg = pair.Items[0];
                switch (arg)
                {
                    case ConstExpr lit when FixtureModel.StringValue(lit) is { } name && IncludedModule(modules, name) is { } target:
                        WalkUrls(modules, ev, target, joined, depth + 1, stack, found);
                        break;
                    case AttributeExpr { Attr: "urls", Value: var r }:
                        ExpandRouter(modules, ev, m, r, joined, found);
                        break;
                    case ConstExpr lit when FixtureModel.StringValue(lit) is { } library:
                        // the URLs of an app that is not the project's (a library): one entry for all of them
                        found.Add(EntryOf("ANY", joined + "…", library, "library", m, call.Start, new()));
                        break;
                    case { } list:
                        WalkPatterns(modules, ev, m, PatternItems(m, list).ToList(), joined, depth + 1, stack, found);
                        break;
                }
                continue;
            }
            // admin.site.urls and the like: a library's URLs under this prefix
            if (view is AttributeExpr { Attr: "urls" } site)
            {
                found.Add(EntryOf("ANY", joined + "…", m.Source[site.Start..site.End], "library", m, call.Start, new()));
                continue;
            }

            if (view is CallExpr { Func: AttributeExpr { Attr: "as_view", Value: var classRef } } && ev.TypeOf(m, classRef) is ClassType { IsInstance: false } cls)
            {
                found.Add(DescribeClassView(ev, cls.Info, joined, null));
                continue;
            }
            switch (ev.TypeOf(m, view))
            {
                case FunctionType { Info.Node: FunctionDef fd } f when !f.Info.Module.IsStub:
                    found.Add(DescribeFunctionView(ev, f.Info.Module, fd, joined));
                    break;
                case ClassType { IsInstance: false } direct when !direct.Info.Module.IsStub:
                    found.Add(DescribeClassView(ev, direct.Info, joined, null));
                    break;
            }
        }
    }

    // ------------------------------------------------------------ views

    RouteEntry EntryOf(string methods, string rawPath, string name, string framework, ModuleInfo module, int nameStart, List<RouteParam> parameters, string? response = null, string? status = null, List<string>? tags = null) =>
        new(methods, DisplayRoute(rawPath), name, framework, UriOfModule(module) ?? "", Lines(module).PositionOf(nameStart).Line, parameters, response, status, tags is { Count: > 0 } ? tags : null);

    RouteEntry DescribeFunctionView(TypeEvaluator ev, ModuleInfo module, FunctionDef def, string rawPath)
    {
        var methods = new List<string>();
        foreach (var d in def.Decorators.OfType<CallExpr>().Where(d => CalleeName(d) == "api_view"))
            if (d.Args.FirstOrDefault(a => a.Name == null || a.Name == "http_method_names")?.Value is ListExpr list)
                methods.AddRange(list.Items.Select(x => FixtureModel.StringValue(x)?.ToUpperInvariant()).Where(x => x != null)!);
        bool api = def.Decorators.OfType<CallExpr>().Any(d => CalleeName(d) == "api_view");
        var tags = new List<string>();
        foreach (var d in def.Decorators.OfType<CallExpr>().Where(d => CalleeName(d) == "permission_classes"))
            if (d.Args.FirstOrDefault()?.Value is ListExpr perms) tags.AddRange(perms.Items.Select(p => "permission " + SourceText(module, p)));
        return EntryOf(methods.Count > 0 ? string.Join(", ", methods) : "ANY", rawPath, def.Name, api ? "drf" : "django", module, def.NameStart, PathParamsOf(rawPath).ToList(), tags: tags);
    }

    /// <summary>The attribute a class or its project bases assign (`serializer_class = ...`), with the module it is written in.</summary>
    static (ModuleInfo Module, Expr Value)? ClassAttribute(TypeEvaluator ev, ClassInfo cls, string name)
    {
        foreach (var t in ev.MroOf(cls).OfType<ClassType>())
        {
            if (t.Info.Module.IsStub || t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal) || t.Info.Module.Name.StartsWith("django.", StringComparison.Ordinal)) continue;
            foreach (var st in t.Info.Node.Body.OfType<AssignStmt>())
                if (st.Targets.Count == 1 && st.Targets[0] is NameExpr n && n.Name == name) return (t.Info.Module, st.Value);
        }
        return null;
    }

    static IEnumerable<string> NamesIn(ModuleInfo module, Expr e) => e switch
    {
        ListExpr l => l.Items.Select(i => FixtureModel.StringValue(i)).Where(s => s != null)!,
        TupleExpr t => t.Items.Select(i => FixtureModel.StringValue(i)).Where(s => s != null)!,
        DictExpr d => d.Keys.Select(i => i is { } k ? FixtureModel.StringValue(k) : null).Where(s => s != null)!,
        _ => Array.Empty<string>(),
    };

    /// <summary>
    /// The names of the classes a view is built on: those the evaluator resolves and, for the ones it cannot (the library is not in the interpreter the project
    /// uses), the names written in the `class X(Base)` lines of the project's own classes.
    /// </summary>
    static HashSet<string> BaseNamesOf(TypeEvaluator ev, ClassInfo cls)
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var t in ev.MroOf(cls).OfType<ClassType>())
        {
            names.Add(t.Info.Name);
            foreach (var b in t.Info.Node.Bases.Where(a => a.Name == null && a.Stars == 0)) names.Add(LastSegment(SourceText(t.Info.Module, b.Value)));
        }
        return names;
    }

    /// <summary>What a class view answers: the handler methods it (or a project base) defines, and what its generic bases bring.</summary>
    static List<string> HandlerMethodsOf(TypeEvaluator ev, ClassInfo cls)
    {
        var methods = new List<string>();
        foreach (var name in BaseNamesOf(ev, cls))
            if (GenericMethods.TryGetValue(name, out var generic)) methods.AddRange(generic);
        foreach (var t in ev.MroOf(cls).OfType<ClassType>())
        {
            if (t.Info.Module.IsStub || t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal) || t.Info.Module.Name.StartsWith("django.", StringComparison.Ordinal)) continue;
            methods.AddRange(t.Info.Node.Body.OfType<FunctionDef>().Where(f => HandlerVerbs.Contains(f.Name)).Select(f => f.Name.ToUpperInvariant()));
        }
        return methods.Distinct().OrderBy(x => Array.IndexOf(new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }, x)).ToList();
    }

    static bool IsRestFramework(TypeEvaluator ev, ClassInfo cls) =>
        ev.MroOf(cls).OfType<ClassType>().Any(t => t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal))
        || BaseNamesOf(ev, cls).Any(n => n is "APIView" or "GenericAPIView" or "ViewSet" or "GenericViewSet" or "ModelViewSet" or "ReadOnlyModelViewSet" || n.EndsWith("APIView", StringComparison.Ordinal));

    /// <summary>What a REST framework view takes in a request body, filters on, and requires: from its serializer, its filter attributes and its permission classes.</summary>
    (List<RouteParam> Query, SerializerChoice Serializers, List<string> Tags) DrfFacts(TypeEvaluator ev, ClassInfo cls)
    {
        var query = new List<RouteParam>();
        var serializers = SerializersOf(ev, cls);
        if (ClassAttribute(ev, cls, "filterset_fields") is { } fields)
            foreach (var name in NamesIn(fields.Module, fields.Value)) query.Add(new RouteParam(name, "query", null, false, "filter"));
        if (ClassAttribute(ev, cls, "search_fields") is not null) query.Add(new RouteParam("search", "query", "str", false, "search_fields"));
        if (ClassAttribute(ev, cls, "ordering_fields") is not null || ClassAttribute(ev, cls, "ordering") is not null) query.Add(new RouteParam("ordering", "query", "str", false, null));
        if (ClassAttribute(ev, cls, "pagination_class") is { Value: not ConstExpr { Kind: ConstKind.None } }) query.Add(new RouteParam("page", "query", "int", false, "pagination"));
        var tags = new List<string>();
        if (ClassAttribute(ev, cls, "permission_classes") is { } perms) tags.AddRange(perms.Value is ListExpr or TupleExpr ? (perms.Value is ListExpr pl ? pl.Items : ((TupleExpr)perms.Value).Items).Select(p => "permission " + SourceText(perms.Module, p)) : Array.Empty<string>());
        return (query, serializers, tags);
    }

    /// <summary>The request body of a view that writes with these serializers: the serializer, and (when there is one) its writable fields.</summary>
    RouteParam BodyOf(TypeEvaluator ev, SerializerChoice choice, string? action, string method, bool required, string detail)
    {
        string type = choice.For(action, method)!;
        var picked = choice.All.Select(x => x.Class).Distinct().Where(c => c.Name == type).ToList();
        var fields = picked.Count == 1 ? SerializerFieldParams(ev, picked[0], body: true) : null;
        return new RouteParam("data", "body", type, required, detail, fields is { Count: > 0 } ? fields : null);
    }

    RouteEntry DescribeClassView(TypeEvaluator ev, ClassInfo cls, string rawPath, string? actionName)
    {
        var methods = HandlerMethodsOf(ev, cls);
        bool rest = IsRestFramework(ev, cls);
        var parameters = PathParamsOf(rawPath).ToList();
        string? response = null;
        List<string>? tags = null;
        if (rest)
        {
            var (query, serializers, permissions) = DrfFacts(ev, cls);
            if (methods.Contains("GET")) parameters.AddRange(query);
            string? writer = methods.FirstOrDefault(x => x is "POST" or "PUT" or "PATCH");
            if (writer != null && serializers.For(null, writer) != null) parameters.Add(BodyOf(ev, serializers, null, writer, true, "request body"));
            response = serializers.For(null, methods.Contains("GET") ? "GET" : methods.FirstOrDefault() ?? "GET");
            tags = permissions;
        }
        return EntryOf(methods.Count > 0 ? string.Join(", ", methods) : "ANY", rawPath, cls.Name, rest ? "drf" : "django", cls.Module, cls.Node.NameStart, parameters, response, null, tags);
    }

    // ------------------------------------------------------------ routers and viewsets

    static readonly (string Action, string Method, bool Detail, string Mixin)[] StandardActions =
    {
        ("list", "GET", false, "ListModelMixin"), ("create", "POST", false, "CreateModelMixin"), ("retrieve", "GET", true, "RetrieveModelMixin"),
        ("update", "PUT", true, "UpdateModelMixin"), ("partial_update", "PATCH", true, "UpdateModelMixin"), ("destroy", "DELETE", true, "DestroyModelMixin"),
    };

    void ExpandRouter(IReadOnlyList<ModuleInfo> modules, TypeEvaluator ev, ModuleInfo at, Expr routerExpr, string prefix, List<RouteEntry> found)
    {
        string? name = routerExpr switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        if (name == null) return;
        // the router of this module first, then one of that name in another module (`from .routers import router`)
        foreach (var module in new[] { at }.Concat(modules.Where(x => !ReferenceEquals(x, at))))
        {
            if (!module.Ast.Body.OfType<AssignStmt>().Any(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr t && t.Name == name && a.Value is CallExpr c && CalleeName(c) is { } cn && cn.EndsWith("Router", StringComparison.Ordinal))) continue;
            foreach (var reg in AstChildren.Descendants(module.Ast).OfType<CallExpr>().Where(c => c.Func is AttributeExpr { Attr: "register", Value: NameExpr rv } && rv.Name == name))
            {
                var args = reg.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
                string route = (args.Count > 0 ? FixtureModel.StringValue(args[0].Value) : reg.Args.FirstOrDefault(a => a.Name == "prefix") is { } pa ? FixtureModel.StringValue(pa.Value) : null) ?? "";
                var viewExpr = args.Count > 1 ? args[1].Value : reg.Args.FirstOrDefault(a => a.Name == "viewset")?.Value;
                if (viewExpr == null || ev.TypeOf(module, viewExpr) is not ClassType { IsInstance: false } vs) continue;
                ExpandViewSet(ev, vs.Info, prefix + route.TrimStart('^').TrimEnd('$'), found);
            }
            return;
        }
    }

    void ExpandViewSet(TypeEvaluator ev, ClassInfo vs, string rawPrefix, List<RouteEntry> found)
    {
        var mro = ev.MroOf(vs).OfType<ClassType>().ToList();
        var names = BaseNamesOf(ev, vs);
        var own = mro.Where(t => !t.Info.Module.IsStub && !t.Info.Module.Name.StartsWith("rest_framework", StringComparison.Ordinal)).SelectMany(t => t.Info.Node.Body.OfType<FunctionDef>()).ToList();
        string lookup = ClassAttribute(ev, vs, "lookup_url_kwarg") is { } kw && FixtureModel.StringValue(kw.Value) is { } k1 ? k1
            : ClassAttribute(ev, vs, "lookup_field") is { } lf && FixtureModel.StringValue(lf.Value) is { } k2 ? k2 : "pk";
        var (query, serializers, permissions) = DrfFacts(ev, vs);
        bool model = names.Contains("ModelViewSet");
        bool readOnly = names.Contains("ReadOnlyModelViewSet");
        string basePath = rawPrefix.TrimEnd('/');

        foreach (var (action, method, detail, mixin) in StandardActions)
        {
            bool offered = own.Any(f => f.Name == action) || model || names.Contains(mixin) || readOnly && action is "list" or "retrieve";
            if (!offered) continue;
            var parameters = detail ? new List<RouteParam> { new(lookup, "path", "str", true, "lookup") } : new List<RouteParam>();
            parameters.AddRange(PathParamsOf(basePath));
            if (action == "list") parameters.AddRange(query);
            string? serializer = serializers.For(action, method);
            if (serializer != null && action is "create" or "update" or "partial_update") parameters.Add(BodyOf(ev, serializers, action, method, action != "partial_update", action == "partial_update" ? "any of the fields" : "request body"));
            string? response = serializer == null || action == "destroy" ? null : action == "list" ? $"list[{serializer}]" : serializer;
            string? status = action == "create" ? "201" : action == "destroy" ? "204" : null;
            int at = own.FirstOrDefault(f => f.Name == action)?.NameStart ?? vs.Node.NameStart;
            var module = own.FirstOrDefault(f => f.Name == action) != null ? mro.First(t => t.Info.Node.Body.Contains(own.First(f => f.Name == action))).Info.Module : vs.Module;
            found.Add(EntryOf(method, detail ? $"{basePath}/{{{lookup}}}/" : basePath + "/", $"{vs.Name}.{action}", "drf", module, at, parameters, response, status, permissions));
        }

        // @action(detail=True, methods=["post"], url_path="publish")
        foreach (var f in own)
            foreach (var d in f.Decorators.OfType<CallExpr>().Where(d => CalleeName(d) == "action"))
            {
                bool detail = d.Args.FirstOrDefault(a => a.Name == "detail")?.Value is ConstExpr { Kind: ConstKind.True };
                var methods = d.Args.FirstOrDefault(a => a.Name == "methods")?.Value is ListExpr ml ? ml.Items.Select(x => FixtureModel.StringValue(x)?.ToUpperInvariant()).Where(x => x != null).ToList() : new List<string?> { "GET" };
                string urlPath = d.Args.FirstOrDefault(a => a.Name == "url_path")?.Value is { } up && FixtureModel.StringValue(up) is { } custom ? custom : f.Name;
                var parameters = detail ? new List<RouteParam> { new(lookup, "path", "str", true, "lookup") } : new List<RouteParam>();
                parameters.AddRange(PathParamsOf(basePath));
                string path = detail ? $"{basePath}/{{{lookup}}}/{urlPath}/" : $"{basePath}/{urlPath}/";
                var module = ev.MroOf(vs).OfType<ClassType>().First(t => t.Info.Node.Body.Contains(f)).Info.Module;
                // @action(serializer_class=X), or what get_serializer_class gives for this action by name
                string? actionSerializer = d.Args.FirstOrDefault(a => a.Name == "serializer_class")?.Value is { } se && ev.TypeOf(module, se) is ClassType { IsInstance: false } sc ? sc.Info.Name
                    : serializers.ByAction.ContainsKey(f.Name) ? serializers.For(f.Name, methods.FirstOrDefault() ?? "GET") : null;
                found.Add(EntryOf(string.Join(", ", methods), path, $"{vs.Name}.{f.Name}", "drf", module, f.NameStart, parameters, actionSerializer, tags: permissions));
            }
    }
}
