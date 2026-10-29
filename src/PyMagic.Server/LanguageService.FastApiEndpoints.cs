using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>What an endpoint takes and gives, read from its code the way FastAPI reads it: where each parameter comes from, the response, the dependencies behind it.</summary>
public sealed partial class LanguageService
{
    static readonly HashSet<string> ParameterMarkers = new(StringComparer.Ordinal) { "Query", "Path", "Body", "Header", "Cookie", "Form", "File", "Depends", "Security" };

    /// <summary>Types FastAPI hands a function by themselves, which are not part of the request's data.</summary>
    static readonly HashSet<string> InjectedTypes = new(StringComparer.Ordinal) { "Request", "Response", "BackgroundTasks", "WebSocket", "HTTPConnection", "SecurityScopes" };

    /// <summary>The `Annotated[...]` a name stands for when it is written once at the top of the module (`DB = Annotated[Session, Depends(get_db)]`).</summary>
    static Expr ResolvedAlias(ModuleInfo m, Expr? e)
    {
        if (e is NameExpr n)
            foreach (var st in m.Ast.Body.OfType<AssignStmt>())
                if (st.Targets.Count == 1 && st.Targets[0] is NameExpr { } t && t.Name == n.Name && st.Value is SubscriptExpr { Value: var av } && ReceiverName(av) == "Annotated")
                    return st.Value;
        return e!;
    }

    static string? ExpressionText(ModuleInfo m, Expr? e) => e == null ? null : SourceText(m, e);

    /// <summary>The marker (`Query(...)`, `Depends(...)`) of a parameter, from its default or from the metadata of its `Annotated`, and the type it is annotated with.</summary>
    static (Expr? Type, CallExpr? Marker, bool DefaultInMarker) Unwrap(ModuleInfo m, Parameter p)
    {
        Expr? type = ResolvedAlias(m, p.Annotation);
        CallExpr? marker = p.Default is CallExpr d && CalleeName(d) is { } dn && ParameterMarkers.Contains(dn) ? d : null;
        bool inDefault = marker != null;
        if (type is SubscriptExpr { Value: var av, Index: var index } && ReceiverName(av) == "Annotated")
        {
            var items = index is TupleExpr t ? t.Items : new[] { index };
            type = items.Count > 0 ? items[0] : null;
            marker ??= items.Skip(1).OfType<CallExpr>().FirstOrDefault(c => CalleeName(c) is { } cn && ParameterMarkers.Contains(cn));
        }
        return (type, marker, inDefault);
    }

    /// <summary>Whether a `Query(...)` / `Header(...)` gives the parameter a value of its own, which makes it optional (`Query(None)`, `Query(default=3)`; `Query(...)` and `Query()` do not).</summary>
    static bool MarkerHasDefault(CallExpr marker) =>
        marker.Args.Any(a => a.Name is "default" or "default_factory" || a.Name == null && a.Stars == 0 && a.Value is not ConstExpr { Kind: ConstKind.Ellipsis });

    IEnumerable<RouteParam> DescribeParameters(ModuleInfo m, TypeEvaluator ev, FunctionDef def, HashSet<string> pathNames, int depth)
    {
        foreach (var p in def.Params)
        {
            if (p.Kind != ParamKind.Normal || p.Name is "self" or "cls") continue;
            var (typeExpr, marker, markerIsDefault) = Unwrap(m, p);
            PyType? type = typeExpr == null ? null : ev.AnnotatedType(m, typeExpr);
            string? typeText = type == null || type is UnknownType ? ExpressionText(m, typeExpr) : FormatType(ev, type);
            string? marked = marker == null ? null : CalleeName(marker);

            if (marked is "Depends" or "Security")
            {
                var dependency = marker!.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0) ?? marker.Args.FirstOrDefault(a => a.Name == "dependency");
                string name = dependency == null ? "(the annotated type)" : SourceText(m, dependency.Value);
                var children = new List<RouteParam>();
                if (dependency != null && depth < 3 && ev.TypeOf(m, dependency.Value) is FunctionType { } f && f.Info.Node is FunctionDef fd && !f.Info.Module.IsStub)
                    children.AddRange(DescribeParameters(f.Info.Module, ev, fd, pathNames, depth + 1));
                var scopes = marker.Args.FirstOrDefault(a => a.Name == "scopes");
                yield return new RouteParam(p.Name, "dependency", typeText, true, name + (scopes != null ? $" · scopes {SourceText(m, scopes.Value)}" : ""), children.Count > 0 ? children : null);
                continue;
            }

            string kind;
            if (marked != null) kind = marked.ToLowerInvariant();
            else if (pathNames.Contains(p.Name)) kind = "path";
            else if (type is ClassType { IsInstance: true } ct && InjectedTypes.Contains(ct.Info.Name)) continue;
            else if (type is ClassType { IsInstance: true } up && up.Info.Name == "UploadFile") kind = "file";
            else if (type is ClassType { IsInstance: true } model && ev.MroOf(model.Info).OfType<ClassType>().Any(c => c.Info.Name == "BaseModel" && c.Info.Module.Name.StartsWith("pydantic", StringComparison.Ordinal))) kind = "body";
            else kind = "query";

            // a marker's own default makes it optional, and so does a default written after an `Annotated[...]`; `Query(...)` and `Query()` are required
            bool required = kind == "path" || (marker != null ? !MarkerHasDefault(marker) && (markerIsDefault || p.Default == null) : p.Default == null);
            string? detail = marker == null ? null : string.Join(", ", marker.Args.Where(a => a.Name != null && a.Name is not ("default" or "default_factory")).Select(a => $"{a.Name}={SourceText(m, a.Value)}"));
            yield return new RouteParam(p.Name, kind, typeText, required, string.IsNullOrEmpty(detail) ? null : detail);
        }
    }

    /// <summary>The decorator call (`@app.get("/x", response_model=...)`) that makes a function the route <paramref name="r"/>.</summary>
    static CallExpr? RouteDecorator(RouteDef r) =>
        r.Def.Decorators.OfType<CallExpr>().FirstOrDefault(c => c.Func is AttributeExpr a && (RouteVerbs.Contains(a.Attr) || a.Attr is "api_route" or "websocket") && a.Start <= r.PathStart);

    RouteEntry Describe(RouteDef r, TypeEvaluator ev)
    {
        var entry = new RouteEntry(string.Join(", ", r.Methods), r.Path, r.Def.Name, r.Flask ? "flask" : "fastapi", UriOfModule(r.Module) ?? "", Lines(r.Module).PositionOf(r.Def.NameStart).Line);
        if (r.Flask) return DescribeFlask(r, entry);

        var pathNames = PathParameters(r).Select(x => x.Name).ToHashSet();
        var parameters = DescribeParameters(r.Module, ev, r.Def, pathNames, 0).ToList();
        var decorator = RouteDecorator(r);

        // dependencies of the whole route: `dependencies=[Depends(auth)]`
        if (decorator?.Args.FirstOrDefault(a => a.Name == "dependencies")?.Value is ListExpr shared)
            foreach (var dep in shared.Items.OfType<CallExpr>().Where(c => CalleeName(c) is "Depends" or "Security"))
            {
                var target = dep.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value;
                var children = target != null && ev.TypeOf(r.Module, target) is FunctionType { } f && f.Info.Node is FunctionDef fd && !f.Info.Module.IsStub
                    ? DescribeParameters(f.Info.Module, ev, fd, pathNames, 1).ToList() : new List<RouteParam>();
                parameters.Insert(0, new RouteParam("(every request)", "dependency", null, true, target == null ? "" : SourceText(r.Module, target), children.Count > 0 ? children : null));
            }

        string? response = ExpressionText(r.Module, decorator?.Args.FirstOrDefault(a => a.Name == "response_model")?.Value) ?? ExpressionText(r.Module, r.Def.Returns);
        string? status = ExpressionText(r.Module, decorator?.Args.FirstOrDefault(a => a.Name == "status_code")?.Value);
        var tags = decorator?.Args.FirstOrDefault(a => a.Name == "tags")?.Value is ListExpr tagList
            ? tagList.Items.OfType<ConstExpr>().Select(c => FixtureModel.StringValue(c) ?? "").Where(t => t.Length > 0).ToList() : null;
        return entry with { Params = parameters, Response = response, Status = status, Tags = tags is { Count: > 0 } ? tags : null };
    }
}
