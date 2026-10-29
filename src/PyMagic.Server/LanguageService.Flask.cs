using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>Flask: what a view must give back, what url_for needs, what a view reads of the request, and the routes that are not decorators (add_url_rule, MethodView, Flask-RESTful).</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ a view that gives no response

    /// <summary>
    /// The findings about the response of a Flask view: it falls off the end, or returns None, and Flask answers "The view function did not return a valid response".
    /// A body that only says what the view will be (`pass`, `...`, a docstring), one that always raises, and one that ends in `abort(...)` are left alone.
    /// </summary>
    IEnumerable<Finding> FlaskResponseFindings(ModuleInfo m, TypeEvaluator ev, RouteDef r)
    {
        var def = r.Def;
        if (!r.Flask || m.ParentOf(def) is ClassDef || HasYield(def.Body) || IsStubBody(def.Body)) yield break;
        var returns = AstChildren.Descendants(def).OfType<ReturnStmt>().Where(x => OwnerFunction(m, x) == def).ToList();
        bool anyValue = returns.Any(x => x.Value != null && x.Value is not ConstExpr { Kind: ConstKind.None });
        bool fallsOff = !AlwaysExits(m, def.Body, ev);
        if (!anyValue && def.Body.LastOrDefault() is ExprStmt { Value: CallExpr { } last } && CalleeName(last) is "abort" or "redirect") yield break;
        if (!anyValue && AlwaysExits(m, def.Body, ev) && returns.Count == 0) yield break;
        if (!anyValue)
            yield return new Finding(def.NameStart, def.Name.Length, $"The view '{def.Name}' never returns a response: Flask will answer \"The view function did not return a valid response\"", "flask-no-response", FindingLevel.Warning);
        else if (fallsOff)
            yield return new Finding(def.NameStart, def.Name.Length, $"The view '{def.Name}' can end without returning a response (a path that falls off the end gives None)", "flask-no-response", FindingLevel.Warning);
    }

    static FunctionDef? OwnerFunction(ModuleInfo m, Node n)
    {
        for (Node? p = m.ParentOf(n); p != null; p = m.ParentOf(p))
            if (p is FunctionDef f) return f;
        return null;
    }

    // ------------------------------------------------------------ url_for needs what the route has

    /// <summary>
    /// What is wrong with the arguments of `url_for("profile", ...)`: a path parameter of the route that is not given (Flask raises BuildError), and an `<int:...>`
    /// parameter given a string that is not a number. Left alone: calls with `**values`, and routes with `defaults=` or declared more than once.
    /// </summary>
    IEnumerable<Finding> UrlForFindings(RouteIndex index, NamedString s)
    {
        if (s.Call == null || CalleeName(s.Call) != "url_for" || s.Value.StartsWith('.')) yield break;
        var routes = EndpointRoutes(index, s.Value).Where(r => r.Flask).ToList();
        if (routes.Count != 1) yield break;
        var route = routes[0];
        if (route.Def.Decorators.OfType<CallExpr>().Any(d => d.Args.Any(a => a.Name == "defaults"))) yield break;
        if (s.Call.Args.Any(a => a.Stars > 0)) yield break;
        var given = s.Call.Args.Where(a => a.Name != null).ToDictionary(a => a.Name!, a => a.Value);
        foreach (var (name, converter, _) in PathParameters(route))
        {
            if (!given.TryGetValue(name, out var value))
            {
                yield return new Finding(s.Start, s.End - s.Start, $"url_for('{s.Value}') needs '{name}': the route '{route.Own}' has it as a parameter and Flask cannot build the URL without it", "flask-url-for-arguments", FindingLevel.Warning);
                continue;
            }
            if (converter == "int" && value is ConstExpr { Kind: ConstKind.Str } text && FixtureModel.StringValue(text) is { } str && !str.All(char.IsAsciiDigit))
                yield return new Finding(value.Start, value.End - value.Start, $"'{name}' is an int in the route '{route.Own}', and {SourceText(s.Module, value)} is not a number", "flask-url-for-arguments", FindingLevel.Warning);
        }
    }

    // ------------------------------------------------------------ what a view reads and gives

    static readonly Dictionary<string, string> RequestBuckets = new(StringComparer.Ordinal)
    {
        ["args"] = "query", ["values"] = "query", ["form"] = "form", ["files"] = "file", ["headers"] = "header", ["cookies"] = "cookie", ["json"] = "body",
    };

    /// <summary>`request.args` and the like: the name of the bucket, when the expression is that attribute of `request`.</summary>
    static string? RequestBucketOf(Expr e) =>
        e is AttributeExpr { Value: var owner } a && RequestBuckets.ContainsKey(a.Attr) && ReceiverName(owner) == "request" ? a.Attr : null;

    /// <summary>What the functions read of the request: `request.args.get("q")`, `request.form["name"]`, `request.files["f"]`, `request.headers.get("X-Token")`, `request.get_json()`.</summary>
    static List<RouteParam> RequestUsage(ModuleInfo m, IEnumerable<FunctionDef> defs)
    {
        var found = new Dictionary<(string Kind, string Name), RouteParam>();
        void Add(string kind, string name, string? type, bool required, string? detail = null)
        {
            if (!found.TryGetValue((kind, name), out var old)) found[(kind, name)] = new RouteParam(name, kind, type, required, detail);
            else if (required && !old.Required) found[(kind, name)] = old with { Required = true };
        }
        foreach (var def in defs)
            foreach (var node in AstChildren.Descendants(def))
            {
                switch (node)
                {
                    case SubscriptExpr { Value: var owner, Index: ConstExpr key } when RequestBucketOf(owner) is { } bucket && FixtureModel.StringValue(key) is { } name:
                        Add(RequestBuckets[bucket], name, null, true);
                        break;
                    case CallExpr { Func: AttributeExpr { Attr: "get" or "getlist", Value: var owner } } call when RequestBucketOf(owner) is { } bucket
                        && call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value is ConstExpr key && FixtureModel.StringValue(key) is { } name:
                    {
                        string? type = call.Args.FirstOrDefault(a => a.Name == "type")?.Value is NameExpr t ? t.Name : null;
                        Add(RequestBuckets[bucket], name, type, false, call.Func is AttributeExpr { Attr: "getlist" } ? "list" : null);
                        break;
                    }
                    case CallExpr { Func: AttributeExpr { Attr: "get_json", Value: var owner } } when ReceiverName(owner) == "request":
                        Add("body", "json", null, true, "request.get_json()");
                        break;
                    case AttributeExpr { Attr: "json", Value: var owner } when ReceiverName(owner) == "request" && m.ParentOf(node) is not AttributeExpr:
                        Add("body", "json", null, true, "request.json");
                        break;
                }
            }
        return found.Values.ToList();
    }

    /// <summary>What a view's `return`s and `abort`s say about its answer: its kind ("template page.html", "json", "redirect"), and the status codes.</summary>
    static (string? Response, string? Status) FlaskAnswers(ModuleInfo m, FunctionDef def)
    {
        var kinds = new List<string>();
        var codes = new List<string>();
        void Kind(Expr? e)
        {
            switch (e)
            {
                case CallExpr c when CalleeName(c) == "render_template":
                    kinds.Add(c.Args.FirstOrDefault(a => a.Name == null)?.Value is ConstExpr t && FixtureModel.StringValue(t) is { } name ? "template " + name : "template");
                    break;
                case CallExpr c when CalleeName(c) == "jsonify": kinds.Add("json"); break;
                case DictExpr or ListExpr: kinds.Add("json"); break;
                case CallExpr c when CalleeName(c) == "redirect": kinds.Add("redirect"); break;
                case CallExpr c when CalleeName(c) is "send_file" or "send_from_directory": kinds.Add("file"); break;
                case CallExpr c when CalleeName(c) is "make_response" or "Response": kinds.Add("response"); break;
                case ConstExpr { Kind: ConstKind.Str }: kinds.Add("text"); break;
                case IfExpr i: Kind(i.Body); Kind(i.Orelse); break;
            }
        }
        foreach (var ret in AstChildren.Descendants(def).OfType<ReturnStmt>().Where(x => OwnerFunction(m, x) == def))
        {
            if (ret.Value is TupleExpr { Items.Count: >= 2 } pair)
            {
                Kind(pair.Items[0]);
                if (pair.Items[1] is ConstExpr { Kind: ConstKind.Int } status) codes.Add(status.Text);
            }
            else Kind(ret.Value);
        }
        foreach (var call in AstChildren.Descendants(def).OfType<CallExpr>().Where(c => CalleeName(c) == "abort" && OwnerFunction(m, c) == def))
            if (call.Args.FirstOrDefault()?.Value is ConstExpr { Kind: ConstKind.Int } code) codes.Add("abort " + code.Text);
        string? response = kinds.Count > 0 ? string.Join(", ", kinds.Distinct()) : null;
        string? statusText = codes.Count > 0 ? string.Join(", ", codes.Distinct()) : null;
        return (response, statusText);
    }

    RouteEntry DescribeFlask(RouteDef r, RouteEntry entry)
    {
        var parameters = new List<RouteParam>();
        foreach (var (name, converter, _) in PathParameters(r))
            parameters.Add(new RouteParam(name, "path", converter switch { "int" => "int", "float" => "float", "uuid" => "UUID", _ => "str" }, true, converter is "path" or "uuid" ? converter : null));
        parameters.AddRange(RequestUsage(r.Module, new[] { r.Def }));
        var (response, status) = FlaskAnswers(r.Module, r.Def);
        var tags = new List<string>();
        if (r.Info?.BlueprintName is { } blueprint) tags.Add("blueprint " + blueprint);
        foreach (var d in r.Def.Decorators)
            if (d is NameExpr n && n.Name is not ("staticmethod" or "classmethod")) tags.Add(n.Name);
            else if (d is AttributeExpr { Attr: var attr } && !RouteVerbs.Contains(attr) && attr is not ("route" or "errorhandler" or "before_request" or "after_request")) tags.Add(attr);
            else if (d is CallExpr { Func: var f } c && !(f is AttributeExpr { Attr: var ca } && (RouteVerbs.Contains(ca) || ca is "route" or "api_route" or "websocket" or "errorhandler")) && CalleeName(c) is { } cn) tags.Add(cn);
        return entry with { Params = parameters, Response = response, Status = status, Tags = tags.Count > 0 ? tags : null };
    }

    // ------------------------------------------------------------ routes that are not decorators

    static readonly string[] Verbs = { "get", "post", "put", "patch", "delete" };

    /// <summary>The handler methods a class defines (`get`, `post`, ...), along the project's own classes.</summary>
    static List<FunctionDef> HandlersOf(TypeEvaluator ev, ClassInfo cls) =>
        ev.MroOf(cls).OfType<ClassType>().Where(t => !t.Info.Module.IsStub && t.Info.Node.Body.Count > 0).SelectMany(t => t.Info.Node.Body.OfType<FunctionDef>()).Where(f => Verbs.Contains(f.Name)).ToList();

    /// <summary>`app.add_url_rule("/users/", view_func=UserAPI.as_view("users"))`, a function given the same way, and Flask-RESTful's `api.add_resource(Users, "/users", "/users/<int:id>")`.</summary>
    IReadOnlyList<RouteEntry> FlaskExtraRoutes()
    {
        var snap = Full();
        var ev = snap.Workspace.Evaluator;
        var found = new List<RouteEntry>();
        foreach (var m in snap.Project.Where(x => !x.IsStub && (x.Source.Contains("add_url_rule", StringComparison.Ordinal) || x.Source.Contains("add_resource", StringComparison.Ordinal)) && WebFramework.IsMatch(x.Source)))
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
            {
                var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
                if (CalleeName(call) == "add_url_rule" && call.Func is AttributeExpr)
                {
                    string? rule = positional.Count > 0 ? FixtureModel.StringValue(positional[0].Value) : FixtureModel.StringValue(call.Args.FirstOrDefault(a => a.Name == "rule")?.Value ?? new NameExpr("_"));
                    var view = call.Args.FirstOrDefault(a => a.Name == "view_func")?.Value ?? (positional.Count > 2 ? positional[2].Value : positional.Count > 1 && FixtureModel.StringValue(positional[1].Value) == null ? positional[1].Value : null);
                    if (rule == null || view == null) continue;
                    var methods = StringItems(call.Args.FirstOrDefault(a => a.Name == "methods")?.Value).Select(x => x.ToUpperInvariant()).ToList();
                    if (view is CallExpr { Func: AttributeExpr { Attr: "as_view", Value: var classRef } } && ev.TypeOf(m, classRef) is ClassType { IsInstance: false } cls)
                        found.Add(FlaskClassEntry(m, ev, cls.Info, rule, methods));
                    else if (ev.TypeOf(m, view) is FunctionType { Info.Node: FunctionDef fd } f && !f.Info.Module.IsStub)
                        found.Add(FlaskFunctionEntry(f.Info.Module, fd, rule, methods));
                }
                else if (CalleeName(call) == "add_resource" && call.Func is AttributeExpr && positional.Count >= 2 && ev.TypeOf(m, positional[0].Value) is ClassType { IsInstance: false } resource)
                    foreach (var url in positional.Skip(1).Select(a => FixtureModel.StringValue(a.Value)).Where(u => u != null))
                        found.Add(FlaskClassEntry(m, ev, resource.Info, url!, new List<string>()));
            }
        return found.Where(e => e.Uri.Length > 0).ToList();
    }

    RouteEntry FlaskClassEntry(ModuleInfo at, TypeEvaluator ev, ClassInfo cls, string rule, List<string> methods)
    {
        var handlers = HandlersOf(ev, cls);
        if (methods.Count == 0) methods = handlers.Select(h => h.Name.ToUpperInvariant()).Distinct().ToList();
        if (methods.Count == 0) methods.Add("GET");
        var parameters = new List<RouteParam>();
        foreach (Match p in FlaskParam.Matches(rule))
            parameters.Add(new RouteParam(p.Groups["n"].Value, "path", p.Groups["c"].Value switch { "int" => "int", "float" => "float", "uuid" => "UUID", _ => "str" }, true, null));
        parameters.AddRange(RequestUsage(cls.Module, handlers));
        var answers = handlers.Select(h => FlaskAnswers(cls.Module, h)).ToList();
        string? response = answers.Select(a => a.Response).Where(x => x != null).Distinct().ToList() is { Count: > 0 } kinds ? string.Join(", ", kinds) : null;
        return new RouteEntry(string.Join(", ", methods), rule, cls.Name, "flask", UriOfModule(cls.Module) ?? "", Lines(cls.Module).PositionOf(cls.Node.NameStart).Line, parameters, response, null, null);
    }

    RouteEntry FlaskFunctionEntry(ModuleInfo module, FunctionDef def, string rule, List<string> methods)
    {
        if (methods.Count == 0) methods.Add("GET");
        var parameters = new List<RouteParam>();
        foreach (Match p in FlaskParam.Matches(rule))
            parameters.Add(new RouteParam(p.Groups["n"].Value, "path", p.Groups["c"].Value switch { "int" => "int", "float" => "float", "uuid" => "UUID", _ => "str" }, true, null));
        parameters.AddRange(RequestUsage(module, new[] { def }));
        var (response, status) = FlaskAnswers(module, def);
        return new RouteEntry(string.Join(", ", methods), rule, def.Name, "flask", UriOfModule(module) ?? "", Lines(module).PositionOf(def.NameStart).Line, parameters, response, status, null);
    }
}
