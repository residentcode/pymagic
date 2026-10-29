using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>What goes wrong in a FastAPI app without a message: a route nothing reaches, a dependency passed as its result, a call that freezes the event loop.</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ a route that an earlier one covers

    static readonly Regex StarletteParam = new(@"^\{(?<n>\w+)(?::(?<c>\w+))?\}$", RegexOptions.Compiled);

    static string[] SegmentsOf(string path) => path.Trim('/').Split('/');

    /// <summary>
    /// Whether the route path <paramref name="general"/> answers every URL that <paramref name="specific"/> would, and <paramref name="specific"/> is not the same path:
    /// `/items/{id}` covers `/items/me`, `/files/{p:path}` covers `/files/a/b`. A segment with a parameter and a literal around it (`/v{n}/x`) is not compared.
    /// </summary>
    public static bool PathCovers(string general, string specific)
    {
        var g = SegmentsOf(general);
        var s = SegmentsOf(specific);
        bool narrower = false;
        for (int i = 0; i < g.Length; i++)
        {
            var gp = StarletteParam.Match(g[i]);
            if (!gp.Success && g[i].Contains('{')) return false;
            if (gp.Success && gp.Groups["c"].Value == "path") return i < s.Length && (narrower || s.Length > i + 1 || s[i] != g[i]);
            if (i >= s.Length) return false;
            var sp = StarletteParam.Match(s[i]);
            if (!sp.Success && s[i].Contains('{')) return false;
            if (!gp.Success)
            {
                if (sp.Success || !string.Equals(g[i], s[i], StringComparison.Ordinal)) return false;
                continue;
            }
            string conv = gp.Groups["c"].Value;
            if (sp.Success)
            {
                // the same kind of parameter on both sides is the same path; a plain one covers a converted one
                string sconv = sp.Groups["c"].Value;
                if (conv == sconv) continue;
                if (conv is "" or "str" && sconv is not "path") { narrower = true; continue; }
                return false;
            }
            bool fits = conv switch
            {
                "" or "str" => s[i].Length > 0,
                "int" => s[i].Length > 0 && s[i].All(char.IsAsciiDigit),
                "float" => double.TryParse(s[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _),
                "uuid" => Guid.TryParse(s[i], out _),
                _ => false,
            };
            if (!fits) return false;
            narrower = true;
        }
        return s.Length == g.Length && narrower;
    }

    /// <summary>The earlier route that answers the requests of <paramref name="r"/> before it can (FastAPI and Starlette try routes in the order they were declared), or null.</summary>
    RouteDef? ShadowingRoute(RouteIndex index, RouteDef r, ModuleInfo here, Func<RouteDef, int> orderOf)
    {
        if (r.Flask) return null;   // Flask ranks fixed rules before dynamic ones whatever the order
        return index.Routes.FirstOrDefault(o => !o.Flask && !(SameModule(o.Module, here) && o.PathStart == r.PathStart) && o.Methods.Intersect(r.Methods).Any()
            && (orderOf(o) < orderOf(r) || orderOf(o) == orderOf(r) && o.PathStart < r.PathStart) && PathCovers(o.Path, r.Path));
    }

    // ------------------------------------------------------------ dependencies and blocking calls

    static readonly Dictionary<string, string[]> BlockingCalls = new(StringComparer.Ordinal)
    {
        ["time"] = new[] { "sleep" },
        ["requests"] = new[] { "get", "post", "put", "patch", "delete", "head", "options", "request" },
        ["urllib.request"] = new[] { "urlopen" },
        ["subprocess"] = new[] { "run", "call", "check_call", "check_output" },
        ["os"] = new[] { "system" },
    };

    /// <summary>`module.name` of a call that blocks the thread it runs on, or null: from where the function is defined, or from the module an attribute is read off.</summary>
    static string? BlockingNameOf(ModuleInfo m, TypeEvaluator ev, CallExpr call)
    {
        string? module = null, name = null;
        switch (ev.TypeOf(m, call.Func))
        {
            case FunctionType f:
                module = f.Info.Module.Name;
                name = f.Info.Name;
                break;
            case var other when call.Func is AttributeExpr a && ev.TypeOf(m, a.Value) is ModuleType mt:
                module = mt.Module.Name;
                name = a.Attr;
                break;
        }
        if (module == null || name == null)
        {
            // where the evaluator knows nothing of the module (no stub for it), the import lines still say what `time` and `sleep` are
            if (QualifiedCalleeOf(m, call.Func) is not { } full || !full.Contains('.')) return null;
            module = full[..full.LastIndexOf('.')];
            name = full[(full.LastIndexOf('.') + 1)..];
        }
        foreach (var (prefix, names) in BlockingCalls)
            if ((module == prefix || module.StartsWith(prefix + ".", StringComparison.Ordinal) && prefix == "requests") && names.Contains(name))
                return prefix + "." + name;
        if (module == "httpx._api" && BlockingCalls["requests"].Contains(name)) return "httpx." + name;
        return null;
    }

    /// <summary>`time.sleep` for `import time` ... `time.sleep`, and for `from time import sleep` ... `sleep`: the dotted name an import gives the callee, or null.</summary>
    static string? QualifiedCalleeOf(ModuleInfo m, Expr callee)
    {
        var tail = new List<string>();
        var e = callee;
        while (e is AttributeExpr a) { tail.Insert(0, a.Attr); e = a.Value; }
        if (e is not NameExpr root) return null;
        foreach (var st in m.Ast.Body)
        {
            if (st is ImportStmt imp)
                foreach (var alias in imp.Names)
                {
                    if (alias.AsName == null && alias.Name.Split('.')[0] == root.Name) return string.Join(".", new[] { root.Name }.Concat(tail));
                    if (alias.AsName == root.Name) return string.Join(".", new[] { alias.Name }.Concat(tail));
                }
            else if (st is ImportFromStmt { Level: 0, Module: { } module, IsStar: false } from)
                foreach (var alias in from.Names)
                    if ((alias.AsName ?? alias.Name) == root.Name) return string.Join(".", new[] { module, alias.Name }.Concat(tail));
        }
        return null;
    }

    /// <summary>The calls in the body of <paramref name="def"/>, leaving out those inside a function, lambda or class nested in it (they run later, elsewhere).</summary>
    static IEnumerable<CallExpr> CallsOfOwnBody(ModuleInfo m, FunctionDef def)
    {
        foreach (var call in AstChildren.Descendants(def).OfType<CallExpr>())
        {
            bool nested = false;
            for (Node? p = m.ParentOf(call); p != null && !ReferenceEquals(p, def); p = m.ParentOf(p))
                if (p is FunctionDef or LambdaExpr or ClassDef) { nested = true; break; }
            if (!nested) yield return call;
        }
    }

    IEnumerable<Diag> FastApiDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        string source = m.Source;

        // Depends(get_db()) hands over what the function returned, not the function
        if (source.Contains("Depends", StringComparison.Ordinal) || source.Contains("Security", StringComparison.Ordinal))
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
            {
                if (CalleeName(call) is not ("Depends" or "Security")) continue;
                var arg = call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0) ?? call.Args.FirstOrDefault(a => a.Name == "dependency");
                if (arg?.Value is not CallExpr produced || ev.TypeOf(m, produced.Func) is not FunctionType f || f.Info.Module.IsStub) continue;
                if (ev.ReturnTypeOf(f) is not ClassType { IsInstance: true } result || ev.MemberNames(result).Any(x => x.Name == "__call__")) continue;
                var finding = new Finding(produced.Start, produced.End - produced.Start, $"{CalleeName(call)}() takes the function itself: '{SourceText(m, produced.Func)}()' is what it returned, not a dependency", "depends-called", FindingLevel.Warning);
                if (!Suppressed(source, finding)) yield return AsDiag(finding);
            }

        // time.sleep(1) in an async function stops every other request until it is over
        if (source.Contains("async ", StringComparison.Ordinal))
            foreach (var def in AstChildren.Descendants(m.Ast).OfType<FunctionDef>().Where(d => d.IsAsync))
                foreach (var call in CallsOfOwnBody(m, def))
                {
                    if (m.ParentOf(call) is AwaitExpr || BlockingNameOf(m, ev, call) is not { } name) continue;
                    var finding = new Finding(call.Func.Start, call.Func.End - call.Func.Start,
                        $"'{name}' blocks the event loop in the async function '{def.Name}': every other request waits for it. " + (name == "time.sleep" ? "Use 'await asyncio.sleep()'." : "Run it in a thread (asyncio.to_thread) or use an async library."),
                        "async-blocking-call", FindingLevel.Warning);
                    if (!Suppressed(source, finding)) yield return AsDiag(finding);
                }
    }

    /// <summary>Quick fixes of these inspections.</summary>
    bool FastApiFix(string code, ModuleInfo m, int start, int end, Action<string, bool, Edit[]> add)
    {
        switch (code)
        {
            case "depends-called":
            {
                var call = AstChildren.Descendants(m.Ast).OfType<CallExpr>().FirstOrDefault(c => c.Start == start && c.End == end);
                if (call != null && call.Args.Count == 0) add("Pass the function itself, without calling it", true, new[] { new Edit(call.Func.End, call.End, "") });
                return true;
            }
            case "async-blocking-call":
            {
                // time.sleep(n) -> await asyncio.sleep(n)
                var access = AstChildren.Descendants(m.Ast).OfType<Expr>().FirstOrDefault(e => e.Start == start && e.End == end && e is AttributeExpr { Attr: "sleep" } or NameExpr { Name: "sleep" });
                if (access == null) return true;
                var edits = new List<Edit> { new(start, end, "await asyncio.sleep") };
                if (!m.Source.Contains("import asyncio", StringComparison.Ordinal)) edits.Add(ImportEdit(m, null, "asyncio"));
                add("Use 'await asyncio.sleep(...)'", true, edits.ToArray());
                return true;
            }
        }
        return false;
    }
}
