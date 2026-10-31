using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// FastAPI beyond the routes: what a request may carry (a body on a GET, a JSON body beside form fields), `Security` scopes the OAuth2 scheme does
/// not declare, and go to definition from a parameter to the dependency behind it (`user: Annotated[User, Depends(get_user)]`).
/// </summary>
public sealed partial class LanguageService
{
    static readonly HashSet<string> MethodsWithoutBody = new(StringComparer.Ordinal) { "GET", "HEAD" };

    IEnumerable<Diag> FastApiRequestDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("fastapi", StringComparison.Ordinal) < 0 && m.Source.IndexOf("APIRouter", StringComparison.Ordinal) < 0 && m.Source.IndexOf("Security", StringComparison.Ordinal) < 0) yield break;
        var snap = ReferenceEquals(Current().Workspace.Evaluator, ev) ? Current() : Full();
        var findings = new List<Finding>();

        foreach (var r in RouteIndexOf(snap).Routes.Where(r => !r.Flask && ReferenceEquals(r.Module, m)))
        {
            var pathNames = PathParameters(r).Select(x => x.Name).ToHashSet();
            var described = DescribeParameters(m, ev, r.Def, pathNames, 0).ToList();
            // each parameter of the route, with what it and the dependencies behind it read from the body
            var bodies = new List<(Parameter At, string Name, string Kind)>();
            foreach (var p in r.Def.Params.Where(p => p.Kind == ParamKind.Normal))
            {
                var d = described.FirstOrDefault(x => x.Name == p.Name);
                if (d == null) continue;
                foreach (var (name, kind) in BodyParts(d)) bodies.Add((p, name, kind));
            }
            if (bodies.Count == 0) continue;

            // a body on a request that does not have one
            var plain = r.Methods.Where(MethodsWithoutBody.Contains).ToList();
            if (plain.Count > 0 && plain.Count == r.Methods.Count)
                foreach (var b in bodies.GroupBy(b => b.At).Select(g => g.First()))
                {
                    string what = b.Name == b.At.Name ? $"'{b.Name}'" : $"'{b.At.Name}' (through its dependency, '{b.Name}')";
                    findings.Add(new Finding(b.At.Start, b.At.Name.Length,
                        $"A {plain[0]} request with a body: FastAPI reads {what} from the {(b.Kind == "body" ? "JSON body" : "form")}, which browsers, fetch() and most clients and proxies do not send with {plain[0]}; take query parameters, or make the route a POST",
                        "fastapi-request-body", FindingLevel.Warning));
                }

            // a JSON body beside form fields: a request has one body, and FastAPI then reads all of them from the form
            var json = bodies.Where(b => b.Kind == "body").ToList();
            var form = bodies.Where(b => b.Kind is "form" or "file").ToList();
            if (json.Count > 0 && form.Count > 0)
                foreach (var b in json.GroupBy(b => b.At).Select(g => g.First()))
                    findings.Add(new Finding(b.At.Start, b.At.Name.Length,
                        $"'{b.Name}' is a JSON body while '{form[0].Name}' is a form field: a request has one body, and with Form or File FastAPI reads every body parameter from the form, so '{b.Name}' fails validation; send it as Form fields, or take the file in another request",
                        "fastapi-request-body", FindingLevel.Warning));
        }

        // Security(..., scopes=["itemz"]) against the scopes the OAuth2 schemes declare
        if (m.Source.Contains("scopes", StringComparison.Ordinal) && DeclaredScopes(snap) is { Count: > 0 } declared)
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>().Where(c => CalleeName(c) == "Security"))
                if (call.Args.FirstOrDefault(a => a.Name == "scopes")?.Value is ListExpr or TupleExpr && call.Args.First(a => a.Name == "scopes").Value is var list)
                    foreach (var item in (list is ListExpr l ? l.Items : ((TupleExpr)list).Items).OfType<ConstExpr>())
                        if (FixtureModel.StringValue(item) is { } scope && !declared.Contains(scope))
                            findings.Add(new Finding(item.Start, item.End - item.Start,
                                $"The OAuth2 scheme declares no scope '{scope}' (it has {string.Join(", ", declared.Order(StringComparer.Ordinal))}): no token is granted it, so a request that needs it is always refused",
                                "fastapi-security-scopes", FindingLevel.Warning));

        foreach (var f in findings) if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    /// <summary>What a described parameter reads from the request body, its own or through the dependencies under it: (name, body / form / file).</summary>
    static IEnumerable<(string Name, string Kind)> BodyParts(RouteParam p)
    {
        if (p.Kind is "body" or "form" or "file") yield return (p.Name, p.Kind);
        foreach (var child in p.Children ?? new List<RouteParam>())
            foreach (var part in BodyParts(child)) yield return part;
    }

    /// <summary>The scopes the project's OAuth2 schemes declare: the keys of `scopes={...}` of OAuth2PasswordBearer, OAuth2AuthorizationCodeBearer and the like.</summary>
    static HashSet<string> DeclaredScopes(Snapshot snap)
    {
        var scopes = new HashSet<string>(StringComparer.Ordinal);
        foreach (var module in snap.Project)
        {
            if (module.IsStub || module.Source.IndexOf("OAuth2", StringComparison.Ordinal) < 0) continue;
            foreach (var call in AstChildren.Descendants(module.Ast).OfType<CallExpr>())
                if (CalleeName(call) is { } name && name.StartsWith("OAuth2", StringComparison.Ordinal) && call.Args.FirstOrDefault(a => a.Name == "scopes")?.Value is DictExpr d)
                    foreach (var key in d.Keys.OfType<ConstExpr>()) if (FixtureModel.StringValue(key) is { } k) scopes.Add(k);
        }
        return scopes;
    }

    /// <summary>
    /// Go to definition on the name of a parameter FastAPI fills from a dependency (`user: Annotated[User, Depends(get_current_user)]`, a module alias of
    /// such an Annotated, `db=Depends(get_db)`, `Security(...)`): the dependency function, or the class of `Depends()`.
    /// </summary>
    List<Location>? FastApiDependencyDefinition(ModuleInfo m, TypeEvaluator ev, int offset)
    {
        if (m.Source.IndexOf("Depends", StringComparison.Ordinal) < 0 && m.Source.IndexOf("Security", StringComparison.Ordinal) < 0) return null;
        foreach (var def in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
        {
            if (offset < def.Start || offset > def.End) continue;
            foreach (var p in def.Params)
            {
                if (offset < p.Start || offset > p.Start + p.Name.Length) continue;
                var (type, marker, _) = Unwrap(m, p);
                if (marker == null || CalleeName(marker) is not ("Depends" or "Security")) return null;
                var dependency = marker.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value ?? marker.Args.FirstOrDefault(a => a.Name == "dependency")?.Value;
                // Depends() takes the annotated class itself
                var target = dependency != null ? ev.TypeOf(m, dependency) : type != null ? ev.AnnotatedType(m, type) : null;
                Node? node = target switch
                {
                    FunctionType { Info: var f } when f.Node is FunctionDef => f.Node,
                    ClassType { Info: var c } => c.Node,
                    _ => null,
                };
                ModuleInfo? where = target switch { FunctionType ft => ft.Info.Module, ClassType ct => ct.Info.Module, _ => null };
                if (node == null || where == null || UriOfModule(where) is not { } uri) return null;
                var (s, e) = NameSpan(where, node);
                return new List<Location> { new(uri, Lines(where).RangeOf(s, e)) };
            }
        }
        return null;
    }
}
