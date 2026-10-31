using System.Text;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Server;

/// <summary>
/// Docstrings the way `inspect.getdoc` finds them, for hover, completion and the signature popup: a def's or class's own; for a stub (which has
/// none), the one in the library's source the stub describes; for a method without one, that of the method it overrides in a base class.
/// </summary>
public sealed partial class LanguageService
{
    readonly Dictionary<string, (DateTime Stamp, PyMagic.Core.Syntax.Module Ast)> _sourceAsts = new(StringComparer.OrdinalIgnoreCase);

    static string? NameOf(Node n) => n switch { FunctionDef f => f.Name, ClassDef c => c.Name, _ => null };

    /// <summary>The docstring of a def, a class or a module (<paramref name="def"/> is then the module's AST), with what a base class's method says when it has none.</summary>
    string? DocOf(TypeEvaluator ev, ModuleInfo module, Node def, ClassInfo? owner)
    {
        if (OwnDoc(module, def) is { } own) return own;
        if (def is not FunctionDef f || owner == null) return null;
        foreach (var t in ev.MroOf(owner).Skip(1))
        {
            if (t is not ClassType { Info: var c } || c.Name == "object" && c.Module.Name == "builtins") continue;
            if (c.Scope.Symbols.GetValueOrDefault(f.Name) is not { } sym) continue;
            if (sym.Declarations.Select(d => d.Node).OfType<FunctionDef>().FirstOrDefault() is not { } overridden) break;   // an attribute, not a method
            if (OwnDoc(c.Module, overridden) is { } inherited) return inherited;
        }
        return null;
    }

    /// <summary>The docstring written in the def itself, or for a stub the one of the same def in the library's source.</summary>
    string? OwnDoc(ModuleInfo module, Node def)
    {
        var body = def switch { FunctionDef f => f.Body, ClassDef c => c.Body, PyMagic.Core.Syntax.Module m => m.Body, _ => null };
        if (body == null) return null;
        if (Docstrings.Of(body) is { } doc) return doc;
        return module.IsStub ? SourceDoc(module, def) : null;
    }

    /// <summary>The docstring of the def a stub describes, read from the module's .py in the library roots (C modules have none to read).</summary>
    string? SourceDoc(ModuleInfo stub, Node def)
    {
        var chain = def is PyMagic.Core.Syntax.Module ? new List<string>()
            : AstChildren.Descendants(stub.Ast).Where(n => n is FunctionDef or ClassDef && n.Start <= def.Start && def.End <= n.End).OrderBy(n => n.Start).Select(n => NameOf(n)!).ToList();
        var parts = stub.Name.Split('.');
        foreach (var root in EffectiveLibraryRoots())
        {
            string basePath = System.IO.Path.Combine(new[] { root }.Concat(parts).ToArray());
            foreach (var file in new[] { basePath + ".py", System.IO.Path.Combine(basePath, "__init__.py") })
            {
                if (SourceAst(file) is not { } ast) continue;
                var found = chain.Count == 0 ? ast : FindDef(ast, chain, 0);
                if (found != null) return OwnDocOfSource(found);
            }
        }
        return null;
    }

    static string? OwnDocOfSource(Node n) => Docstrings.Of(n switch { FunctionDef f => f.Body, ClassDef c => c.Body, PyMagic.Core.Syntax.Module m => m.Body, _ => Array.Empty<Stmt>() });

    /// <summary>The def or class named by <paramref name="chain"/> from <paramref name="k"/> on, looked for inside `if`/`try` blocks too; the first that has a docstring.</summary>
    static Node? FindDef(Node scope, List<string> chain, int k)
    {
        foreach (var n in AstChildren.Of(scope))
        {
            if (n is FunctionDef or ClassDef)
            {
                if (NameOf(n) != chain[k]) continue;
                if (k == chain.Count - 1) { if (OwnDocOfSource(n) != null) return n; continue; }
                if (FindDef(n, chain, k + 1) is { } inner) return inner;
                continue;
            }
            if (n is Stmt && FindDef(n, chain, k) is { } nested) return nested;
        }
        return null;
    }

    PyMagic.Core.Syntax.Module? SourceAst(string file)
    {
        try
        {
            if (!File.Exists(file)) return null;
            var stamp = File.GetLastWriteTimeUtc(file);
            lock (_sourceAsts)
                if (_sourceAsts.TryGetValue(file, out var cached) && cached.Stamp == stamp) return cached.Ast;
            var ast = Parser.Parse(File.ReadAllText(file)).Module;
            lock (_sourceAsts) _sourceAsts[file] = (stamp, ast);
            return ast;
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    /// <summary>The function a def is, through the name it binds (a plain def, one kept by a decorator, one overload).</summary>
    static FunctionInfo? FunctionInfoOf(TypeEvaluator ev, ModuleInfo m, FunctionDef fd)
    {
        if (m.Semantics.ScopeOf(fd)?.Parent?.Symbols.GetValueOrDefault(fd.Name) is not { } sym) return null;
        return ev.TypeOfSymbol(sym) switch
        {
            FunctionType f => f.Info,
            SignatureType { Origin: { } o } => o.Info,
            OverloadedType o => o.Overloads.FirstOrDefault(x => ReferenceEquals(x.Info.Node, fd))?.Info ?? o.Overloads.FirstOrDefault()?.Info,
            _ => null,
        };
    }

    /// <summary>The documentation of what a name's type is: a function's or method's docstring, a class's (or its `__init__`'s), a module's.</summary>
    string? DocOfType(TypeEvaluator ev, PyType type) => type switch
    {
        FunctionType f => DocOf(ev, f.Info.Module, f.Info.Node, f.Info.Owner),
        SignatureType { Origin: { } o } => DocOf(ev, o.Info.Module, o.Info.Node, o.Info.Owner),
        OverloadedType { Overloads: [var first, ..] } => DocOf(ev, first.Info.Module, first.Info.Node, first.Info.Owner),
        ClassType { IsInstance: false } c => ClassDoc(ev, c.Info),
        ModuleType mt => DocOf(ev, mt.Module, mt.Module.Ast, null),
        _ => null,
    };

    /// <summary>A class's docstring, or its `__init__`'s when the class has none.</summary>
    string? ClassDoc(TypeEvaluator ev, ClassInfo c)
    {
        if (DocOf(ev, c.Module, c.Node, null) is { } doc) return doc;
        var init = c.Node.Body.OfType<FunctionDef>().FirstOrDefault(f => f.Name == "__init__");
        return init != null ? DocOf(ev, c.Module, init, c) : null;
    }

    /// <summary>What the docstrings say about a parameter of a function: its own docstring, then (for `__init__`) the class's, where the Google style puts `Args:`.</summary>
    string? ParameterDoc(TypeEvaluator ev, FunctionInfo? f, ClassInfo? cls, string name)
    {
        if (f != null && Docstrings.Parameters(DocOf(ev, f.Module, f.Node, f.Owner)).GetValueOrDefault(name) is { } own) return own;
        cls ??= f is { Name: "__init__" } ? f.Owner : null;
        return cls != null ? Docstrings.Parameters(DocOf(ev, cls.Module, cls.Node, null)).GetValueOrDefault(name) : null;
    }

    /// <summary>
    /// Hover on a parameter where it is declared (`def area(width: int)`) or given by keyword (`area(width=3)`): the parameter, its type and
    /// what the docstring says about it; null when the cursor is on neither.
    /// </summary>
    Hover? ParameterHover(TypeEvaluator ev, ModuleInfo m, int offset, LineMap lines)
    {
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is FunctionDef fd && offset >= fd.Start && offset <= fd.End)
                foreach (var p in fd.Params)
                {
                    if (p.Name.Length == 0) continue;
                    int at = m.Source.IndexOf(p.Name, p.Start, StringComparison.Ordinal);
                    if (at < 0 || offset < at || offset > at + p.Name.Length) continue;
                    var f = FunctionInfoOf(ev, m, fd);
                    var info = f != null ? ev.ParametersOf(f).FirstOrDefault(x => x.Name == p.Name) : null;
                    string text = "(parameter) " + ParameterText(ev, p.Name, info);
                    return MakeHover(text, lines.RangeOf(at, at + p.Name.Length), ParameterDoc(ev, f, null, p.Name));
                }
            if (n is CallExpr call)
                foreach (var a in call.Args)
                {
                    if (a.Name == null || offset < a.Start || offset > a.Start + a.Name.Length) continue;
                    var calleeType = ev.TypeOf(m, call.Func);
                    var cls = calleeType is ClassType { IsInstance: false } c ? c.Info : null;
                    foreach (var sig in ev.CallSignatures(calleeType))
                    {
                        if (sig.Params.FirstOrDefault(x => x.Name == a.Name && x.Category is ParamCategory.Normal or ParamCategory.KeywordOnly) is not { } info) continue;
                        return MakeHover("(parameter) " + ParameterText(ev, a.Name, info), lines.RangeOf(a.Start, a.Start + a.Name.Length), ParameterDoc(ev, sig.Function, cls, a.Name));
                    }
                    return null;
                }
        }
        return null;
    }

    static string ParameterText(TypeEvaluator ev, string name, ParamInfo? p) =>
        p == null || p.Type is UnknownType { Explicit: false } ? name : name + ": " + FormatType(ev, p.Type);

    static readonly Regex CodeSpan = new("``(?<a>[^`]+)``|`(?<b>[^`\n]+)`", RegexOptions.Compiled);

    /// <summary>A docstring as Markdown that shows it as written: the line breaks and indentation kept, `*`, `_` and the like not read as markup, ``code`` as code.</summary>
    public static string DocMarkdown(string doc)
    {
        doc = doc.Replace("\r\n", "\n");
        // a ``span`` may go on over a line break: it is put on one line first
        doc = CodeSpan.Replace(doc, m => m.Groups["a"].Success ? "``" + Regex.Replace(m.Groups["a"].Value, @"\s*\n\s*", " ") + "``" : m.Value);
        var lines = doc.Split('\n');
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i].TrimEnd();
            if (line.Length == 0) { sb.Append("\n\n"); continue; }
            int indent = line.Length - line.TrimStart().Length;
            for (int s = 0; s < indent; s++) sb.Append("&nbsp;");
            string text = line.TrimStart();
            int at = 0;
            foreach (Match m in CodeSpan.Matches(text))
            {
                sb.Append(Escape(text[at..m.Index]));
                sb.Append('`').Append(m.Groups["a"].Success ? m.Groups["a"].Value : m.Groups["b"].Value).Append('`');
                at = m.Index + m.Length;
            }
            sb.Append(Escape(text[at..]));
            if (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0) sb.Append("  \n");
        }
        return Regex.Replace(sb.ToString(), "\n{3,}", "\n\n").Trim();
    }

    static string Escape(string text) => Regex.Replace(text, @"([\\*_\[\]<>#|~])", @"\$1");
}
