using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// Flask: what a view reads of a submitted form (`request.form["email"]`, `request.files["photo"]`, and `request.args` when only a GET form sends to
/// it) against the fields of the HTML forms of the templates that submit to it, and those names completed inside `request.form["`.
/// </summary>
public sealed partial class LanguageService
{
    /// <summary>A `&lt;form&gt;` of a template: where it sends (an endpoint of url_for, a path, or nothing: the page's own URL), how, and the fields it sends.</summary>
    sealed record HtmlForm(string Template, string Path, int Start, string? Endpoint, string? Action, string Method, IReadOnlyList<string> Fields, IReadOnlyList<string> Files, bool Dynamic);

    static readonly Regex FormElement = new(@"<form\b(?<attrs>[^>]*)>(?<body>.*?)</form\s*>", RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.IgnoreCase);
    static readonly Regex FieldElement = new(@"<(?<tag>input|select|textarea|button)\b(?<attrs>[^>]*)>", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    static readonly Regex UrlForCall = new(@"url_for\(\s*['""](?<e>[\w.]+)['""](?<rest>[^)]*)\)", RegexOptions.Compiled);
    static readonly Regex QuotedValue = new(@"=\s*(?:""[^""]*""|'[^']*')", RegexOptions.Compiled);

    static string? FormAttribute(string attrs, string name)
    {
        var m = Regex.Match(attrs, @"(?:^|\s)" + name + @"\s*=\s*(?:""(?<v>[^""]*)""|'(?<v>[^']*)'|(?<v>[^\s>]+))", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups["v"].Value : null;
    }

    readonly ConditionalWeakTable<Snapshot, List<HtmlForm>> _htmlForms = new();

    /// <summary>The forms of every template of the project.</summary>
    List<HtmlForm> HtmlForms(Snapshot snap) => _htmlForms.GetValue(snap, _ =>
    {
        var forms = new List<HtmlForm>();
        foreach (var (name, path) in Templates())
        {
            if (TemplateTextOf(path) is not { } text || text.IndexOf("<form", StringComparison.OrdinalIgnoreCase) < 0) continue;
            foreach (Match f in FormElement.Matches(text))
            {
                string attrs = f.Groups["attrs"].Value, body = f.Groups["body"].Value;
                string method = (FormAttribute(attrs, "method") ?? "get").ToUpperInvariant();
                string? action = FormAttribute(attrs, "action");
                string? endpoint = action != null && UrlForCall.Match(action) is { Success: true } u ? u.Groups["e"].Value : null;
                // fields a template expression may write (Flask-WTF's {{ form.email }}, a macro, an include) are not known
                string bare = QuotedValue.Replace(body, "=\"\"");
                bool dynamic = bare.Contains("{{", StringComparison.Ordinal) || Regex.IsMatch(bare, @"\{%-?\s*(include|call|import|from)\b");
                var fields = new List<string>();
                var files = new List<string>();
                foreach (Match field in FieldElement.Matches(body))
                {
                    string fa = field.Groups["attrs"].Value;
                    if (FormAttribute(fa, "name") is not { } fieldName) continue;
                    if (fieldName.Contains('{')) { dynamic = true; continue; }
                    bool file = field.Groups["tag"].Value.Equals("input", StringComparison.OrdinalIgnoreCase) && string.Equals(FormAttribute(fa, "type"), "file", StringComparison.OrdinalIgnoreCase);
                    (file ? files : fields).Add(fieldName);
                }
                forms.Add(new HtmlForm(name, path, f.Index, endpoint, endpoint == null ? action : null, method, fields, files, dynamic));
            }
        }
        return forms;
    });

    /// <summary>The forms that submit to a route: by url_for of its endpoint, by its path, or (no action) from a template the view itself renders.</summary>
    List<HtmlForm> FormsSendingTo(Snapshot snap, RouteIndex index, RouteDef r)
    {
        var rendered = AstChildren.Descendants(r.Def).OfType<CallExpr>().Where(c => CalleeName(c) == "render_template")
            .Select(c => FixtureModel.StringValue(c.Args.FirstOrDefault(a => a.Name == null)?.Value)).OfType<string>().ToHashSet(StringComparer.Ordinal);
        var result = new List<HtmlForm>();
        foreach (var f in HtmlForms(snap))
        {
            if (!r.Methods.Contains(f.Method)) continue;
            bool to = f.Endpoint != null ? EndpointRoutes(index, f.Endpoint).Any(x => ReferenceEquals(x.Def, r.Def))
                : f.Action is { Length: > 0 } action && !action.Contains("{{", StringComparison.Ordinal) ? RoutesForUrl(index, f.Method, action).Any(x => ReferenceEquals(x.Def, r.Def))
                : rendered.Contains(f.Template);
            if (to) result.Add(f);
        }
        return result;
    }

    /// <summary>
    /// `request.form["emial"]` in a view whose forms send `email` and `password`: Flask answers 400 Bad Request (or `.get` gives None). Judged only when
    /// every form that submits to the view is known (no field a template expression writes); `request.args` only when nothing else links to it with values.
    /// </summary>
    IEnumerable<Finding> FlaskFormFindings(ModuleInfo m, TypeEvaluator ev, RouteDef r, RouteIndex index)
    {
        if (!r.Flask || m.Source.IndexOf("request.", StringComparison.Ordinal) < 0) yield break;
        var snap = ReferenceEquals(Current().Workspace.Evaluator, ev) ? Current() : Full();
        var forms = FormsSendingTo(snap, index, r);
        if (forms.Count == 0 || forms.Any(f => f.Dynamic)) yield break;
        foreach (var (node, bucket, key, required) in RequestReads(r.Def))
        {
            var sending = bucket switch
            {
                "form" => forms.Where(f => f.Method != "GET").ToList(),
                "files" => forms.Where(f => f.Method != "GET").ToList(),
                "args" => forms.Where(f => f.Method == "GET").ToList(),
                _ => new List<HtmlForm>(),
            };
            if (sending.Count == 0) continue;
            // a link may carry query values too: url_for('search', q=...) anywhere, or a GET to this view by hand
            if (bucket == "args" && OtherQuerySources(snap, r)) continue;
            var names = sending.SelectMany(f => bucket == "files" ? f.Files : f.Fields).Distinct().ToList();
            if (names.Contains(key)) continue;
            string where = string.Join(", ", sending.Select(f => System.IO.Path.GetFileName(f.Template)).Distinct());
            string outcome = required ? (bucket == "files" ? "Flask answers 400 Bad Request (KeyError)" : "Flask answers 400 Bad Request") : "it is always None";
            yield return new Finding(node.Start, node.End - node.Start,
                $"No form that submits to {r.Def.Name}() sends a{(bucket == "files" ? " file" : "")} field '{key}' (the form{(sending.Count > 1 ? "s" : "")} in {where} send{(sending.Count > 1 ? "" : "s")}: {(names.Count > 0 ? string.Join(", ", names) : "nothing")}): {outcome}",
                "flask-form-field", FindingLevel.Warning);
        }
    }

    /// <summary>The keys a view reads: `request.form["k"]` (required), `request.form.get("k")`, the same of `files` and `args`, with the string written.</summary>
    static IEnumerable<(ConstExpr Key, string Bucket, string Name, bool Required)> RequestReads(FunctionDef def)
    {
        foreach (var node in AstChildren.Descendants(def))
            switch (node)
            {
                case SubscriptExpr { Value: var owner, Index: ConstExpr key } when RequestBucketOf(owner) is "form" or "files" or "args" && FixtureModel.StringValue(key) is { } name:
                    yield return (key, RequestBucketOf(owner)!, name, true);
                    break;
                case CallExpr { Func: AttributeExpr { Attr: "get" or "getlist", Value: var owner } } call when RequestBucketOf(owner) is "form" or "files" or "args"
                    && call.Args.FirstOrDefault(a => a.Name == null && a.Stars == 0)?.Value is ConstExpr key && FixtureModel.StringValue(key) is { } name:
                    yield return (key, RequestBucketOf(owner)!, name, false);
                    break;
            }
    }

    /// <summary>Whether a URL with query values may reach the view besides its GET forms: url_for(endpoint, extra=...) in code or templates, a written "path?".</summary>
    bool OtherQuerySources(Snapshot snap, RouteDef r)
    {
        foreach (var module in snap.Project.Where(x => !x.IsStub && x.Source.Contains(r.Plain, StringComparison.Ordinal)))
            foreach (Match u in UrlForCall.Matches(module.Source))
                if ((u.Groups["e"].Value == r.Endpoint || u.Groups["e"].Value.TrimStart('.') == r.Plain) && u.Groups["rest"].Value.Contains('=')) return true;
        foreach (var (_, path) in Templates())
            if (TemplateTextOf(path) is { } text)
            {
                foreach (Match u in UrlForCall.Matches(text))
                    if ((u.Groups["e"].Value == r.Endpoint || u.Groups["e"].Value.TrimStart('.') == r.Plain) && u.Groups["rest"].Value.Contains('=')) return true;
                if (text.Contains(r.Path.TrimEnd('/') + "?", StringComparison.Ordinal)) return true;
            }
        return false;
    }

    /// <summary>Inside `request.form["` (or `.get("`, of `files` and `args` too) in a Flask view: the fields of the forms that submit to it.</summary>
    IReadOnlyList<CompletionItem>? FlaskFormFieldCompletion(Doc doc, int start, int offset)
    {
        string text = doc.Text;
        string before = text[LineStart(text, offset)..offset];
        var typed = Regex.Match(before, @"request\.(?<b>form|files|args)(?:\[|\.(?:get|getlist)\()\s*[""'](?<k>\w*)$");
        if (!typed.Success) return null;
        var snap = Current();
        if (!snap.ByUri.TryGetValue(doc.Uri, out var m)) return null;
        var index = RouteIndexOf(snap);
        var route = index.Routes.FirstOrDefault(r => r.Flask && ReferenceEquals(r.Module, m) && r.Def.Start <= offset && offset <= r.Def.End);
        if (route == null) return null;
        string bucket = typed.Groups["b"].Value;
        var forms = FormsSendingTo(snap, index, route).Where(f => bucket == "args" ? f.Method == "GET" : f.Method != "GET");
        var names = forms.SelectMany(f => bucket == "files" ? f.Files : f.Fields).Distinct().ToList();
        return names.Count == 0 ? null : names.Select(n => new CompletionItem(n, CompletionKind.Field, "form field", "0" + n)).ToList();
    }
}
