using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// Convert to class-based view (Alt+Enter on a view function's name): a function that only renders a template becomes a TemplateView
/// (`template_name`, its context in get_context_data), one split on `if request.method == "POST":` a View with get() and post();
/// its decorators go on the class through method_decorator, and the path() entries that name it take `NameView.as_view()`.
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<CodeAction> ClassBasedViewActions(Snapshot snap, ModuleInfo m, string uri, int offset)
    {
        if (m.IsStub || !m.Source.Contains("request", StringComparison.Ordinal)) yield break;
        var fd = m.Ast.Body.OfType<FunctionDef>().FirstOrDefault(f => offset >= f.NameStart - 4 && offset <= f.NameStart + f.Name.Length);
        if (fd == null || fd.IsAsync || fd.Params.Count == 0 || fd.Params[0].Name != "request" || !LooksLikeView(snap, fd)) yield break;
        var plan = ClassViewText(m, fd);
        if (plan == null) yield break;
        var (text, baseName, baseModule) = plan.Value;
        string className = ViewClassName(fd.Name);
        if (m.Semantics.ModuleScope.Symbols.ContainsKey(className)) yield break;

        var edits = new Dictionary<string, List<TextEdit>>();
        var lines = Lines(m);
        var own = new List<Edit>();
        int start = LineStart(m.Source, fd.Start), end = LineEndInclusive(m.Source, fd.End);
        own.Add(new Edit(start, end, text.Replace("{Name}", className)));
        var imports = new List<Edit>();
        if (!m.Semantics.ModuleScope.Symbols.ContainsKey(baseName)) imports.Add(ImportEdit(m, baseModule, baseName));
        if (fd.Decorators.Count > 0 && !m.Semantics.ModuleScope.Symbols.ContainsKey("method_decorator")) imports.Add(ImportEdit(m, "django.utils.decorators", "method_decorator"));
        // two new import lines at one place go in one edit, in order
        foreach (var group in imports.GroupBy(e => (e.Start, e.End)))
            own.Add(group.Count() == 1 ? group.First() : new Edit(group.Key.Start, group.Key.End, group.First().Text + string.Concat(group.Skip(1).Select(g => g.Text.TrimStart('\n')))));
        edits[uri] = own.Select(e => new TextEdit(lines.RangeOf(e.Start, e.End), e.Text)).ToList();

        // urls: views.index -> views.IndexView.as_view(), index -> IndexView.as_view() (and its import)
        var ev = snap.Workspace.Evaluator;
        foreach (var um in snap.Project.Where(x => !x.IsStub && x.Source.Contains(fd.Name, StringComparison.Ordinal) && (x.Source.Contains("path(", StringComparison.Ordinal) || x.Source.Contains("url(", StringComparison.Ordinal))))
        {
            var changes = new List<Edit>();
            bool byName = false;
            foreach (var call in AstChildren.Descendants(um.Ast).OfType<CallExpr>())
            {
                if (CalleeName(call) is not ("path" or "re_path" or "url") || call.Args.Count < 2 || call.Args[1].Name != null) continue;
                var view = call.Args[1].Value;
                if (ev.TypeOf(um, view) is not FunctionType { Info.Node: FunctionDef target } || !ReferenceEquals(target, fd)) continue;
                if (view is AttributeExpr a) changes.Add(new Edit(a.End - a.Attr.Length, a.End, className + ".as_view()"));
                else if (view is NameExpr) { changes.Add(new Edit(view.Start, view.End, className + ".as_view()")); byName = true; }
            }
            if (byName)
                foreach (var imp in um.Ast.Body.OfType<ImportFromStmt>())
                    foreach (var alias in imp.Names.Where(n => n.Name == fd.Name && n.AsName == null))
                        changes.Add(new Edit(alias.Start, alias.End, className));
            if (changes.Count == 0) continue;
            var ul = Lines(um);
            string key = um.Path != null ? UriOfPath(um.Path) : snap.ByUri.FirstOrDefault(kv => ReferenceEquals(kv.Value, um)).Key ?? "";
            if (key.Length == 0) continue;
            if (!edits.TryGetValue(key, out var list)) edits[key] = list = new();
            list.AddRange(changes.Select(e => new TextEdit(ul.RangeOf(e.Start, e.End), e.Text)));
        }
        yield return new CodeAction($"Convert to class-based view ({baseName})", RefactorActionKind("class-based-view"), new WorkspaceEdit(edits));
    }

    /// <summary>A function of a views module, or one a path() names.</summary>
    bool LooksLikeView(Snapshot snap, FunctionDef fd) =>
        snap.Project.Any(m => !m.IsStub && m.Ast.Body.Contains(fd) && (m.Name.EndsWith("views", StringComparison.Ordinal) || m.Name.Contains(".views.", StringComparison.Ordinal)))
        || DjangoIndexOf(snap).Urls.Count > 0 && snap.Project.Any(m => m.Source.Contains(fd.Name, StringComparison.Ordinal) && m.Source.Contains("path(", StringComparison.Ordinal));

    static string ViewClassName(string function)
    {
        string name = function.EndsWith("_view", StringComparison.Ordinal) ? function[..^5] : function;
        return string.Concat(name.Split('_', StringSplitOptions.RemoveEmptyEntries).Select(p => char.ToUpperInvariant(p[0]) + p[1..])) + "View";
    }

    /// <summary>The class's text with `{Name}` for its name, and the base class with its module; null when the function has no shape this converts safely.</summary>
    (string Text, string Base, string Module)? ClassViewText(ModuleInfo m, FunctionDef fd)
    {
        string src = m.Source;
        var body = fd.Body.ToList();
        string? doc = null;
        if (body.Count > 0 && body[0] is ExprStmt { Value: ConstExpr { Kind: ConstKind.Str } } d) { doc = Block(src, new[] { d }, "    "); body.RemoveAt(0); }
        if (body.Count == 0) return null;
        var head = new System.Text.StringBuilder();
        foreach (var deco in fd.Decorators) head.Append($"@method_decorator({src[deco.Start..deco.End]}, name=\"dispatch\")\n");
        string parameters = string.Join(", ", fd.Params.Select(p => src[p.Start..p.End]));

        // TemplateView: ...; return render(request, "page.html", context)
        if (body[^1] is ReturnStmt { Value: CallExpr call } && CalleeName(call) == "render" && call.Args.Count is >= 2 and <= 3
            && call.Args[0] is { Name: null, Value: NameExpr { Name: "request" } } && call.Args[1] is { Name: null or "template_name", Value: ConstExpr template } && FixtureModel.StringValue(template) != null
            && call.Args.Skip(2).All(a => a.Name is null or "context")
            && !AstChildren.Descendants(fd).OfType<AttributeExpr>().Any(a => a.Value is NameExpr { Name: "request" } && a.Attr is "method" or "POST" or "FILES"))
        {
            var before = body.Take(body.Count - 1).ToList();
            var context = call.Args.Count == 3 ? call.Args[2].Value : null;
            var cls = new System.Text.StringBuilder(head.ToString()).Append("class {Name}(TemplateView):\n");
            if (doc != null) cls.Append(doc);
            cls.Append($"    template_name = {src[template.Start..template.End]}\n");
            if (before.Count > 0 || context != null)
            {
                cls.Append("\n    def get_context_data(self, **kwargs):\n        context = super().get_context_data(**kwargs)\n");
                var used = before.SelectMany(AstChildren.Descendants).Concat(context == null ? Array.Empty<Node>() : AstChildren.Descendants(context).Prepend(context))
                    .OfType<NameExpr>().Select(n => n.Name).ToHashSet(StringComparer.Ordinal);
                if (used.Contains("request")) cls.Append("        request = self.request\n");
                foreach (var p in fd.Params.Skip(1).Where(p => used.Contains(p.Name)))
                    cls.Append($"        {p.Name} = self.kwargs[\"{p.Name}\"]\n");
                if (before.Count > 0) cls.Append(Block(src, before, "        "));
                if (context != null) cls.Append($"        context.update({src[context.Start..context.End]})\n");
                cls.Append("        return context\n");
            }
            return (cls.ToString(), "TemplateView", "django.views.generic");
        }

        // View: if request.method == "POST": ... else: ...
        if (body[0] is IfStmt { Test: CompareExpr { Ops: ["=="] } test } split && IsPostTest(test)
            && !(split.Orelse.Count == 1 && split.Orelse[0] is IfStmt && src.AsSpan(split.Orelse[0].Start).StartsWith("elif")))
        {
            var post = split.Body;
            var get = split.Orelse.Count > 0 ? split.Orelse : body.Skip(1).ToList();
            if (split.Orelse.Count > 0 && body.Count > 1 || get.Count == 0) return null;
            var cls = new System.Text.StringBuilder(head.ToString()).Append("class {Name}(View):\n");
            if (doc != null) cls.Append(doc).Append('\n');
            cls.Append($"    def get(self, {parameters}):\n").Append(Block(src, get, "        "));
            cls.Append($"\n    def post(self, {parameters}):\n").Append(Block(src, post, "        "));
            return (cls.ToString(), "View", "django.views");
        }

        // View with get() only, when the function never looks at the method
        if (AstChildren.Descendants(fd).OfType<AttributeExpr>().Any(a => a.Value is NameExpr { Name: "request" } && a.Attr is "method" or "POST" or "FILES")) return null;
        var plain = new System.Text.StringBuilder(head.ToString()).Append("class {Name}(View):\n");
        if (doc != null) plain.Append(doc).Append('\n');
        plain.Append($"    def get(self, {parameters}):\n").Append(Block(src, body, "        "));
        return (plain.ToString(), "View", "django.views");
    }

    static bool IsPostTest(CompareExpr test)
    {
        static bool IsMethod(Expr e) => e is AttributeExpr { Attr: "method", Value: NameExpr { Name: "request" } };
        static bool IsPost(Expr e) => FixtureModel.StringValue(e) == "POST";
        return IsMethod(test.Left) && IsPost(test.Comparators[0]) || IsPost(test.Left) && IsMethod(test.Comparators[0]);
    }

    /// <summary>The lines of these statements (comments between them kept), moved from their own indentation to `indent`.</summary>
    static string Block(string src, IReadOnlyList<Stmt> stmts, string indent)
    {
        int from = LineStart(src, stmts[0].Start), to = LineEndInclusive(src, stmts[^1].End);
        string own = src[from..stmts[0].Start];
        // comment lines just above the first statement go with it
        while (from > 0)
        {
            int previous = LineStart(src, from - 1);
            string line = src[previous..from].TrimEnd('\r', '\n');
            if (!line.StartsWith(own, StringComparison.Ordinal) || !line.TrimStart().StartsWith('#')) break;
            from = previous;
        }
        var sb = new System.Text.StringBuilder();
        foreach (var raw in src[from..to].Replace("\r\n", "\n").Split('\n'))
        {
            if (raw.Trim().Length == 0) { sb.Append('\n'); continue; }
            sb.Append(indent).Append(raw.StartsWith(own, StringComparison.Ordinal) ? raw[own.Length..] : raw.TrimStart()).Append('\n');
        }
        string text = sb.ToString();
        while (text.EndsWith("\n\n", StringComparison.Ordinal)) text = text[..^1];
        return text;
    }
}
