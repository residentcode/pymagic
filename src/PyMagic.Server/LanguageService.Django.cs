using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>Django in the editor: names of URLs and templates written as strings (go to, usages, rename, completion, inspections) and the keywords of queries.</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ string arguments that name something

    enum DjangoNamed { UrlName, Template, QueryField, Endpoint, RouteUrl }

    /// <summary>A plain string literal that is an argument of a call Django gives a meaning to: `reverse("name")`, `render(request, "page.html")`, `order_by("title")`.</summary>
    sealed record NamedString(ModuleInfo Module, DjangoNamed Kind, string Value, int Start, int End, CallExpr Call);

    static readonly HashSet<string> UrlReaders = new(StringComparer.Ordinal) { "reverse", "reverse_lazy", "redirect", "resolve_url" };
    static readonly HashSet<string> TemplateReaders = new(StringComparer.Ordinal) { "render_to_string", "get_template", "select_template" };
    static readonly HashSet<string> QueryStringMethods = new(StringComparer.Ordinal) { "order_by", "values", "values_list", "select_related", "prefetch_related", "only", "defer", "distinct" };
    static readonly Regex EndpointShape = new(@"^[\w.:\-]+$", RegexOptions.Compiled);
    static readonly Regex UrlNameShape = new(@"^[\w:\-]+$", RegexOptions.Compiled);

    static string? CalleeName(CallExpr call) => call.Func switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };

    /// <summary>The string in the positional slot or keyword of a call, with its span between the quotes.</summary>
    static (string Value, int Start, int End)? StringIn(CallExpr call, int position, params string[] keywords)
    {
        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
        Argument? arg = position < positional.Count ? positional[position] : call.Args.FirstOrDefault(a => a.Name != null && keywords.Contains(a.Name));
        if (arg?.Value is not ConstExpr lit || FixtureModel.StringValue(lit) is not { } value || FixtureModel.StringSpan(lit) is not { } span) return null;
        return (value, span.Start, span.End);
    }

    /// <summary>Every such string of a module.</summary>
    IEnumerable<NamedString> NamedStringsIn(ModuleInfo m, TypeEvaluator? ev)
    {
        if (m.IsStub) yield break;
        foreach (var n in AstChildren.Descendants(m.Ast))
        {
            if (n is AssignStmt { Targets.Count: 1 } assign && assign.Targets[0] is NameExpr { Name: "template_name" } && assign.Value is ConstExpr tlit
                && FixtureModel.StringValue(tlit) is { } tname && FixtureModel.StringSpan(tlit) is { } tspan)
            {
                yield return new NamedString(m, DjangoNamed.Template, tname, tspan.Start, tspan.End, null!);
                continue;
            }
            if (n is not CallExpr call || CalleeName(call) is not { } callee) continue;

            if (UrlReaders.Contains(callee) && StringIn(call, 0, "viewname", "to") is { } url && UrlNameShape.IsMatch(url.Value))
                yield return new NamedString(m, DjangoNamed.UrlName, url.Value, url.Start, url.End, call);
            else if (callee is "render" or "TemplateResponse" or "SimpleTemplateResponse" && StringIn(call, 1, "template_name", "template") is { } t1)
                yield return new NamedString(m, DjangoNamed.Template, t1.Value, t1.Start, t1.End, call);
            else if (callee == "TemplateResponse" && StringIn(call, 0, "name") is { } t2)
                yield return new NamedString(m, DjangoNamed.Template, t2.Value, t2.Start, t2.End, call);
            else if (callee is "render_template" or "stream_template" && StringIn(call, 0, "template_name_or_list") is { } t3)
                yield return new NamedString(m, DjangoNamed.Template, t3.Value, t3.Start, t3.End, call);
            else if (callee is "url_for" or "url_path_for" && StringIn(call, 0, "endpoint", "name") is { } ep && EndpointShape.IsMatch(ep.Value))
                yield return new NamedString(m, DjangoNamed.Endpoint, ep.Value, ep.Start, ep.End, call);
            else if (RouteVerbs.Contains(callee) && call.Func is AttributeExpr client && ReceiverName(client.Value) is { } holder && holder.Contains("client", StringComparison.OrdinalIgnoreCase)
                && StringIn(call, 0, "url", "path") is { } route && route.Value.StartsWith('/'))
                yield return new NamedString(m, DjangoNamed.RouteUrl, route.Value, route.Start, route.End, call);
            else if (TemplateReaders.Contains(callee) && StringIn(call, 0, "template_name", "template_name_list") is { } t0)
                yield return new NamedString(m, DjangoNamed.Template, t0.Value, t0.Start, t0.End, call);
            else if (FieldExpressions.Contains(callee) || FieldListExpressions.Contains(callee))
            {
                foreach (var (value, start, end) in FieldStringsOf(call, callee))
                    if (LooksLikeFieldPath(value.Replace("__", "_"))) yield return new NamedString(m, DjangoNamed.QueryField, value, start, end, call);
            }
            else if (QueryStringMethods.Contains(callee) && call.Func is AttributeExpr)
                foreach (var arg in call.Args.Where(a => a.Name == null && a.Stars == 0))
                    if (arg.Value is ConstExpr lit && FixtureModel.StringValue(lit) is { } field && FixtureModel.StringSpan(lit) is { } span)
                        yield return new NamedString(m, DjangoNamed.QueryField, field, span.Start, span.End, call);
        }
    }

    // ------------------------------------------------------------ the project's URLs

    sealed record UrlDef(string Qualified, string Plain, ModuleInfo Module, int Start, int End, string Route, CallExpr Call);

    sealed class DjangoIndex
    {
        public List<UrlDef> Urls { get; } = new();
    }

    readonly ConditionalWeakTable<Snapshot, DjangoIndex> _djangoIndexes = new();

    DjangoIndex DjangoIndexOf(Snapshot snap) => _djangoIndexes.GetValue(snap, s =>
    {
        var index = new DjangoIndex();
        foreach (var module in s.Project)
        {
            if (module.IsStub || module.Source.IndexOf("name", StringComparison.Ordinal) < 0) continue;
            string? appName = module.Ast.Body.OfType<AssignStmt>()
                .Where(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr { Name: "app_name" })
                .Select(a => FixtureModel.StringValue(a.Value)).FirstOrDefault(v => v != null);
            foreach (var call in AstChildren.Descendants(module.Ast).OfType<CallExpr>())
            {
                if (CalleeName(call) is not ("path" or "re_path" or "url")) continue;
                var nameArg = call.Args.FirstOrDefault(a => a.Name == "name");
                if (nameArg?.Value is not ConstExpr lit || FixtureModel.StringValue(lit) is not { } name || FixtureModel.StringSpan(lit) is not { } span) continue;
                string route = call.Args.FirstOrDefault(a => a.Name == null)?.Value is ConstExpr r ? FixtureModel.StringValue(r) ?? "" : "";
                index.Urls.Add(new UrlDef(appName != null ? $"{appName}:{name}" : name, name, module, span.Start, span.End, route, call));
            }
        }
        return index;
    });

    /// <summary>The definitions a name written in `reverse(...)` can mean: the exact name, or the plain name when the namespace is not known.</summary>
    static IEnumerable<UrlDef> UrlDefsFor(DjangoIndex index, string used)
    {
        var exact = index.Urls.Where(u => u.Qualified == used).ToList();
        if (exact.Count > 0) return exact;
        string plain = used.Contains(':') ? used[(used.LastIndexOf(':') + 1)..] : used;
        return index.Urls.Where(u => u.Plain == plain);
    }

    // ------------------------------------------------------------ the project's templates

    (DateTime At, List<(string Name, string Path)> Items)? _templates;

    /// <summary>Files below any folder called `templates` in the workspace: name (relative to that folder) and path.</summary>
    List<(string Name, string Path)> Templates()
    {
        if (_templates is { } cached && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Items;
        var found = new List<(string, string)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in SourceRoots)
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && found.Count < 5000)
            {
                string dir = stack.Pop();
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var d in subdirs)
                {
                    string name = System.IO.Path.GetFileName(d);
                    if (SkippedDirectories.Contains(name) || name.StartsWith('.')) continue;
                    if (name == "templates")
                    {
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories))
                            {
                                string ext = System.IO.Path.GetExtension(f).ToLowerInvariant();
                                if (ext is not (".html" or ".htm" or ".txt" or ".xml" or ".jinja" or ".jinja2" or ".j2" or ".md")) continue;
                                string rel = System.IO.Path.GetRelativePath(d, f).Replace('\\', '/');
                                if (seen.Add(rel + "|" + f)) found.Add((rel, f));
                            }
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* skipped */ }
                    }
                    stack.Push(d);
                }
            }
        }
        _templates = (DateTime.UtcNow, found);
        return found;
    }

    static bool IsLibraryTemplate(string name) => name.StartsWith("admin/") || name.StartsWith("registration/") || name.StartsWith("django/") || name.StartsWith("debug_toolbar/");

    // ------------------------------------------------------------ go to, hover

    NamedString? NamedStringAt(Snapshot snap, ModuleInfo m, int offset) =>
        NamedStringsIn(m, snap.Workspace.Evaluator).FirstOrDefault(s => offset >= s.Start && offset <= s.End);

    IReadOnlyList<Location> DjangoDefinition(string uri, Position pos)
    {
        var snap = Full();
        if (!snap.ByUri.TryGetValue(uri, out var m)) return Array.Empty<Location>();
        int offset = Lines(m).OffsetOf(pos);
        if (NamedStringAt(snap, m, offset) is not { } s) return MetaFieldTarget(snap, m, offset)?.Locations ?? (IReadOnlyList<Location>)Array.Empty<Location>();
        switch (s.Kind)
        {
            case DjangoNamed.UrlName:
                return UrlDefsFor(DjangoIndexOf(snap), s.Value).Select(u => FixtureLocation(u.Module, u.Start, u.End)).OfType<Location>().ToList();
            case DjangoNamed.Template:
                return Templates().Where(t => t.Name == s.Value).Select(t => new Location(UriOfPath(t.Path), new Range(new Position(0, 0), new Position(0, 0)))).ToList();
            case DjangoNamed.QueryField:
                return QueryFieldTargets(snap, s);
            case DjangoNamed.Endpoint or DjangoNamed.RouteUrl:
                return RouteTargets(snap, s);
        }
        return Array.Empty<Location>();
    }

    Hover? DjangoHover(Snapshot snap, ModuleInfo m, int offset)
    {
        if (NamedStringAt(snap, m, offset) is not { } s)
        {
            if (MetaFieldTarget(snap, m, offset) is { } meta && meta.Hover.Length > 0) return new Hover(new MarkupContent("markdown", meta.Hover), Lines(m).RangeOf(meta.Start, meta.End));
            return RouteDeclarationHover(snap, m, offset);
        }
        var range = Lines(m).RangeOf(s.Start, s.End);
        switch (s.Kind)
        {
            case DjangoNamed.Endpoint or DjangoNamed.RouteUrl:
                return RouteHover(snap, s, range);
            case DjangoNamed.UrlName:
                var defs = UrlDefsFor(DjangoIndexOf(snap), s.Value).ToList();
                if (defs.Count == 0) return null;
                return new Hover(new MarkupContent("markdown", "```python\n(url) " + s.Value + "\n```\n\n" + string.Join("\n\n", defs.Select(d => $"`{d.Route}` — {System.IO.Path.GetFileName(d.Module.Path ?? d.Module.Name)}"))), range);
            case DjangoNamed.Template:
                var files = Templates().Where(t => t.Name == s.Value).ToList();
                return files.Count == 0 ? null : new Hover(new MarkupContent("markdown", "```python\n(template) " + s.Value + "\n```\n\n" + string.Join("\n\n", files.Select(f => $"`{f.Path}`"))), range);
        }
        return null;
    }

    // ------------------------------------------------------------ usages and rename of URL names

    IReadOnlyList<Location>? DjangoReferences(string uri, Position pos, bool includeDeclaration)
    {
        var snap = Full();
        if (TemplateUrlAt(uri, pos) is { } inTemplate) return UrlSites(snap, PlainUrlName(inTemplate.Value), includeDeclaration);
        if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
        if (NamedStringAt(snap, m, Lines(m).OffsetOf(pos)) is not { Kind: DjangoNamed.UrlName } s) return NameDeclarationAt(snap, m, Lines(m).OffsetOf(pos)) is { } d ? UrlSites(snap, d.Plain, includeDeclaration) : null;
        var plain = s.Value.Contains(':') ? s.Value[(s.Value.LastIndexOf(':') + 1)..] : s.Value;
        return UrlSites(snap, plain, includeDeclaration);
    }

    /// <summary>The `name="..."` literal of a path() the cursor is on.</summary>
    UrlDef? NameDeclarationAt(Snapshot snap, ModuleInfo m, int offset) =>
        DjangoIndexOf(snap).Urls.FirstOrDefault(u => ReferenceEquals(u.Module, m) && offset >= u.Start && offset <= u.End);

    List<Location> UrlSites(Snapshot snap, string plain, bool includeDeclaration)
    {
        var result = new List<Location>();
        if (includeDeclaration)
            foreach (var u in DjangoIndexOf(snap).Urls.Where(u => u.Plain == plain))
                if (FixtureLocation(u.Module, u.Start, u.End) is { } l) result.Add(l);
        foreach (var module in snap.Project)
            foreach (var use in NamedStringsIn(module, null).Where(x => x.Kind == DjangoNamed.UrlName))
            {
                string p = use.Value.Contains(':') ? use.Value[(use.Value.LastIndexOf(':') + 1)..] : use.Value;
                if (p == plain && FixtureLocation(module, use.Start, use.End) is { } l) result.Add(l);
            }
        foreach (var use in TemplateUrlUses())
            if (PlainUrlName(use.Value) == plain) result.Add(new Location(use.Uri, new LineMap(use.Text).RangeOf(use.Start, use.End)));
        return result;
    }

    WorkspaceEdit? DjangoRename(string uri, Position pos, string newName)
    {
        var snap = Full();
        string? plain = null;
        if (TemplateUrlAt(uri, pos) is { } inTemplate) plain = PlainUrlName(inTemplate.Value);
        else
        {
            if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
            int offset = Lines(m).OffsetOf(pos);
            if (NamedStringAt(snap, m, offset) is { Kind: DjangoNamed.UrlName } s) plain = s.Value.Contains(':') ? s.Value[(s.Value.LastIndexOf(':') + 1)..] : s.Value;
            else if (NameDeclarationAt(snap, m, offset) is { } d) plain = d.Plain;
        }
        if (plain == null) return null;
        if (!Regex.IsMatch(newName, @"^[\w\-]+$")) throw new ServiceException($"'{newName}' is not a valid URL name");

        var changes = new Dictionary<string, List<TextEdit>>();
        void Edit(ModuleInfo module, int start, int end, string text)
        {
            var target = UriOfModule(module) ?? throw new ServiceException("a usage lies in a file that cannot be edited");
            if (!changes.TryGetValue(target, out var list)) changes[target] = list = new List<TextEdit>();
            list.Add(new TextEdit(Lines(module).RangeOf(start, end), text));
        }
        foreach (var u in DjangoIndexOf(snap).Urls.Where(u => u.Plain == plain)) Edit(u.Module, u.Start, u.End, newName);
        foreach (var module in snap.Project)
            foreach (var use in NamedStringsIn(module, null).Where(x => x.Kind == DjangoNamed.UrlName))
            {
                int colon = use.Value.LastIndexOf(':');
                string p = colon >= 0 ? use.Value[(colon + 1)..] : use.Value;
                if (p == plain) Edit(module, use.Start, use.End, colon >= 0 ? use.Value[..(colon + 1)] + newName : newName);   // `blog:old` keeps its namespace
            }
        foreach (var use in TemplateUrlUses())
        {
            if (PlainUrlName(use.Value) != plain) continue;
            if (!changes.TryGetValue(use.Uri, out var list)) changes[use.Uri] = list = new List<TextEdit>();
            int colon = use.Value.LastIndexOf(':');
            list.Add(new TextEdit(new LineMap(use.Text).RangeOf(use.Start, use.End), colon >= 0 ? use.Value[..(colon + 1)] + newName : newName));
        }
        return new WorkspaceEdit(changes);
    }

    static string PlainUrlName(string used) => used.Contains(':') ? used[(used.LastIndexOf(':') + 1)..] : used;

    /// <summary>The name in `{% url 'name' %}` the cursor is on, in an open template.</summary>
    (string Value, int Start, int End)? TemplateUrlAt(string uri, Position pos)
    {
        if (!_templateDocs.TryGetValue(uri, out var doc)) return null;
        int offset = new LineMap(doc.Text).OffsetOf(pos);
        foreach (var tag in DjangoTemplateParser.Parse(doc.Text).Tags.Where(t => t.Name == "url" && offset >= t.Start && offset <= t.End))
        {
            var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
            if (args.Count == 0 || DjangoTemplateParser.Unquote(args[0].Text) is not { } name) continue;
            int start = args[0].Offset + 1, end = args[0].Offset + args[0].Text.Length - 1;
            if (offset >= start && offset <= end) return (name, start, end);
        }
        return null;
    }

    // ------------------------------------------------------------ queries

    static readonly string[] QueryLookups =
    {
        "exact", "iexact", "contains", "icontains", "in", "gt", "gte", "lt", "lte", "startswith", "istartswith", "endswith", "iendswith",
        "range", "isnull", "regex", "iregex", "year", "month", "day", "week_day", "week", "quarter", "hour", "minute", "second", "date", "time",
        "iso_year", "iso_week_day", "contained_by", "has_key", "has_keys", "has_any_keys",
    };

    static readonly HashSet<string> LookupMethods = new(StringComparer.Ordinal) { "filter", "exclude", "get", "get_or_create", "update_or_create" };
    static readonly HashSet<string> FieldOnlyMethods = new(StringComparer.Ordinal) { "create", "update", "bulk_create" };
    /// <summary>Calls in a chain after which names may exist that are not fields.</summary>
    static readonly HashSet<string> NameAddingMethods = new(StringComparer.Ordinal) { "annotate", "alias", "extra", "values", "values_list", "raw", "aggregate", "union", "intersection", "difference" };

    /// <summary>The model a query call works on, when the receiver is a plain chain from a manager (`Book.objects.filter(...)`): otherwise names added along the way cannot be told apart from typos.</summary>
    ClassInfo? QueryModelOf(Snapshot snap, ModuleInfo m, CallExpr call) => QueryModelOf(snap.Workspace.Evaluator, m, call);

    ClassInfo? QueryModelOf(TypeEvaluator ev, ModuleInfo m, CallExpr call)
    {
        if (call.Func is not AttributeExpr access) return null;
        for (Expr e = access.Value; ;)
        {
            if (e is CallExpr inner && inner.Func is AttributeExpr ia)
            {
                if (NameAddingMethods.Contains(ia.Attr)) return null;
                e = ia.Value;
                continue;
            }
            if (e is AttributeExpr { Attr: "objects" or "_default_manager" } or NameExpr or AttributeExpr) break;
            return null;
        }
        // a variable may hold a queryset built with annotations somewhere else: only chains that start at the model's manager are checked
        Expr root = access.Value;
        while (root is CallExpr rc && rc.Func is AttributeExpr ra) root = ra.Value;
        if (root is not AttributeExpr { Attr: "objects" or "_default_manager" }) return null;
        var model = ev.DjangoModelOfQuery(ev.TypeOf(m, access.Value));
        if (model == null || ev.MroOf(model).Any(t => t is not ClassType)) return null;
        return model;
    }

    /// <summary>Walks a lookup path (`author__name__icontains`): the part that is not a field or lookup, with what would have been valid there.</summary>
    static (string Bad, IReadOnlyList<string> Choices)? CheckLookup(TypeEvaluator ev, ClassInfo model, string path, bool allowLookups)
    {
        var parts = path.Split("__");
        var current = model;
        for (int i = 0; i < parts.Length; i++)
        {
            string part = parts[i];
            var names = ev.DjangoQueryNames(current);
            if (part == "pk" || names.Contains(part))
            {
                var field = ev.DjangoFieldNamed(current, part);
                var next = ev.DjangoRelatedModel(current, part);
                if (next != null && i < parts.Length - 1) { current = next; continue; }
                // a plain field (or a relation compared as a whole): what is left must be lookups
                if (field?.Kind == "any") return null;
                for (int j = i + 1; j < parts.Length; j++)
                    if (!allowLookups || !QueryLookups.Contains(parts[j]))
                        return (parts[j], allowLookups ? QueryLookups : Array.Empty<string>());
                return null;
            }
            // a model Django or a library ships may have relations from other installed apps that are not known here (`auth_token` on the user): only a name
            // that is a near miss of a field it has is a mistake
            return IsLibraryModel(current) && !names.Any(n => Near(part, n)) ? null : (part, names);
        }
        return null;
    }

    static bool IsLibraryModel(ClassInfo c) => c.Module.Kind is not (ResolutionKind.InMemory or ResolutionKind.UserCode);

    /// <summary>Whether two names differ by at most two edits (a typo away).</summary>
    static bool Near(string a, string b)
    {
        if (Math.Abs(a.Length - b.Length) > 2) return false;
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (int j = 0; j <= b.Length; j++) previous[j] = j;
        for (int i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (int j = 1; j <= b.Length; j++)
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
            (previous, current) = (current, previous);
        }
        return previous[b.Length] <= 2;
    }

    IEnumerable<Diag> DjangoDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub || m.Source.IndexOf("django", StringComparison.OrdinalIgnoreCase) < 0 && m.Source.IndexOf("objects", StringComparison.Ordinal) < 0
            && m.Source.IndexOf("render_template", StringComparison.Ordinal) < 0 && m.Source.IndexOf("TemplateResponse", StringComparison.Ordinal) < 0) yield break;
        // the open buffers are the project here (the page opens every file): rebuilding the whole project after each keystroke would be too much
        var snap = Current();
        var fullModule = m;
        var fev = ev;
        var index = DjangoIndexOf(snap);
        bool hasUrls = index.Urls.Count > 0;
        bool hasTemplates = Templates().Count > 0;

        foreach (var s in NamedStringsIn(fullModule, fev))
        {
            Finding? finding = null;
            switch (s.Kind)
            {
                case DjangoNamed.UrlName when hasUrls && !UrlDefsFor(index, s.Value).Any():
                    finding = new Finding(s.Start, s.End - s.Start, $"No URL is named '{s.Value}'", "django-url-name", FindingLevel.Weak);
                    break;
                case DjangoNamed.Template when hasTemplates && !IsLibraryTemplate(s.Value) && !Templates().Any(t => t.Name == s.Value):
                    finding = new Finding(s.Start, s.End - s.Start, $"Template '{s.Value}' is not in any templates folder of the project", "django-template", FindingLevel.Weak);
                    break;
                case DjangoNamed.QueryField when s.Call != null && QueryModelOf(snap, fullModule, s.Call) is { } model && s.Call.Func is AttributeExpr { Attr: "order_by" or "values" or "values_list" or "only" or "defer" or "select_related" or "prefetch_related" }:
                {
                    string path = s.Value.TrimStart('-', '?');
                    if (path.Length == 0 || path == "*" || s.Call.Func is AttributeExpr { Attr: "order_by" } && path == "pk" ) break;
                    if (CheckLookup(fev, model, path, allowLookups: false) is { } bad)
                        finding = new Finding(s.Start, s.End - s.Start, $"Cannot resolve '{bad.Bad}' into a field of {model.Name}. Choices are: {string.Join(", ", bad.Choices.Take(12))}", "django-field", FindingLevel.Warning);
                    break;
                }
            }
            if (finding != null && !Suppressed(m.Source, finding)) yield return AsDiag(finding);
        }

        foreach (var expressionFinding in DjangoExpressionDiagnostics(fullModule, fev)) yield return expressionFinding;

        foreach (var call in AstChildren.Descendants(fullModule.Ast).OfType<CallExpr>())
        {
            if (CalleeName(call) is not { } name || !(LookupMethods.Contains(name) || FieldOnlyMethods.Contains(name))) continue;
            if (QueryModelOf(snap, fullModule, call) is not { } model) continue;
            foreach (var arg in call.Args)
            {
                if (arg.Name == null || arg.Name is "defaults" or "using") continue;
                if (CheckLookup(fev, model, arg.Name, allowLookups: LookupMethods.Contains(name)) is not { } bad) continue;
                int start = arg.Start;
                var finding = new Finding(start, arg.Name.Length, $"Cannot resolve keyword '{bad.Bad}' into a field of {model.Name}. Choices are: {string.Join(", ", bad.Choices.Take(12))}", "django-field", FindingLevel.Warning);
                if (!Suppressed(m.Source, finding)) yield return AsDiag(finding);
            }
        }
    }

    List<Location> QueryFieldTargets(Snapshot snap, NamedString s)
    {
        var result = new List<Location>();
        var ev = snap.Workspace.Evaluator;
        // an expression (F, Sum, ...) takes its model from the query around it; a query method from its own receiver
        var found = s.Call == null ? null : CalleeName(s.Call) is { } callee && IsExpression(callee) ? ModelAround(ev, s.Module, s.Call) : QueryModelOf(snap, s.Module, s.Call);
        if (found is not { } model) return result;
        var current = model;
        foreach (var part in s.Value.TrimStart('-', '?').Split("__"))
        {
            var field = ev.DjangoFieldNamed(current, part);
            if (field == null) break;
            var decl = field.Owner.Scope.Symbols.GetValueOrDefault(part)?.Declarations.FirstOrDefault();
            if (decl != null && FixtureLocation(field.Owner.Module, decl.Node.Start, decl.Node.End) is { } loc) result.Add(loc);
            var next = ev.DjangoRelatedModel(current, part);
            if (next == null) break;
            current = next;
        }
        return result.Count > 0 ? new List<Location> { result[^1] } : result;
    }

    // ------------------------------------------------------------ completion

    /// <summary>Names of URLs and templates, and fields of a model, inside the strings that take them.</summary>
    IReadOnlyList<CompletionItem>? DjangoStringCompletion(Doc doc, int start, int offset)
    {
        string text = doc.Text;
        int lineStart = LineStart(text, offset);
        string before = text[lineStart..offset];
        // the opening quote of a string argument: how much of it has been typed
        var m = Regex.Match(before, @"(?<callee>\b(?:reverse|reverse_lazy|redirect|resolve_url|render|render_to_string|get_template|select_template|TemplateResponse|template_name|order_by|values|values_list|only|defer|select_related|prefetch_related|F|Sum|Avg|Min|Max|Count|StdDev|Variance|Lower|Upper|Length|Coalesce|Greatest|Least)\b)\s*[(=][^""']*?[""'](?<typed>[-\w:/.\\]*)$");
        if (!m.Success) return null;
        string callee = m.Groups["callee"].Value;
        string typed = m.Groups["typed"].Value;
        var snap = Full();
        var items = new List<CompletionItem>();

        if (UrlReaders.Contains(callee))
        {
            foreach (var u in DjangoIndexOf(snap).Urls.Select(u => u.Qualified).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                if (u.StartsWith(typed, StringComparison.Ordinal)) items.Add(new CompletionItem(u, CompletionKind.Constant, "URL name", "1" + u));
            return items;
        }
        if (callee is "render" or "render_to_string" or "get_template" or "select_template" or "TemplateResponse" or "template_name")
        {
            foreach (var t in Templates().Select(t => t.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal))
                if (t.StartsWith(typed, StringComparison.Ordinal)) items.Add(new CompletionItem(t, CompletionKind.Module, "template", "1" + t));
            return items;
        }

        // field names in order_by("-ti|")
        var (ws, patched) = PatchedModule(doc, start, offset);
        var pev = ws.Evaluator;
        var literal = AstChildren.Descendants(patched.Ast).OfType<ConstExpr>().FirstOrDefault(c => c.Kind == ConstKind.Str && c.Text.Contains(CursorMark));
        var call = literal == null ? null : patched.ParentOf(literal) is Argument a ? patched.ParentOf(a) as CallExpr : null;
        if (call == null) return null;
        ClassInfo? model;
        if (CalleeName(call) is { } expression && IsExpression(expression))
            model = ModelAround(pev, patched, call);
        else if (call.Func is AttributeExpr access)
            model = pev.DjangoModelOfQuery(pev.TypeOf(patched, access.Value));
        else
            return null;
        if (model == null) return null;
        string pathTyped = typed.TrimStart('-', '?');
        string sign = typed[..(typed.Length - pathTyped.Length)];
        // walk the relations already typed: `author__na` offers the author's fields
        var parts = pathTyped.Split("__");
        var current = model;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var next = pev.DjangoRelatedModel(current, parts[i]);
            if (next == null) return items;
            current = next;
        }
        string stem = parts.Length > 1 ? string.Join("__", parts[..^1]) + "__" : "";
        foreach (var name in pev.DjangoQueryNames(current).Where(n => n.StartsWith(parts[^1], StringComparison.Ordinal)).Distinct())
        {
            var field = pev.DjangoFieldNamed(current, name);
            items.Add(new CompletionItem(stem + name, CompletionKind.Field, field != null ? field.Kind : "relation", "1" + name));
        }
        return items;
    }

    /// <summary>`title=`, `author__name=`, `pages__gt=` among the keywords offered where a filter(...) argument starts.</summary>
    IEnumerable<CompletionItem> DjangoKeywordItems(ModuleInfo patched, TypeEvaluator ev, NameExpr placeholder, string prefix)
    {
        if (patched.ParentOf(placeholder) is not Argument { Name: null } arg || patched.ParentOf(arg) is not CallExpr call) yield break;
        string? method = CalleeName(call);
        if (method == null) yield break;
        ClassInfo? model;
        bool lookups;
        if (method == "Q")
        {
            model = ModelAround(ev, patched, call);
            lookups = true;
        }
        else
        {
            if (!(LookupMethods.Contains(method) || FieldOnlyMethods.Contains(method)) || call.Func is not AttributeExpr access) yield break;
            model = ev.DjangoModelOfQuery(ev.TypeOf(patched, access.Value));
            lookups = LookupMethods.Contains(method);
        }
        if (model == null) yield break;

        var parts = prefix.Split("__");
        var current = model;
        for (int i = 0; i < parts.Length - 1; i++)
        {
            var field = ev.DjangoFieldNamed(current, parts[i]);
            var next = ev.DjangoRelatedModel(current, parts[i]);
            if (next != null) { current = next; continue; }
            // after a plain field the lookups follow: title__icontains
            if (lookups && field != null)
                foreach (var l in QueryLookups.Where(l => l.StartsWith(parts[i + 1], StringComparison.Ordinal)))
                    yield return new CompletionItem(string.Join("__", parts[..(i + 1)]) + "__" + l, CompletionKind.Property, "lookup", "0" + l, InsertText: string.Join("__", parts[..(i + 1)]) + "__" + l + "=");
            yield break;
        }
        string stem = parts.Length > 1 ? string.Join("__", parts[..^1]) + "__" : "";
        foreach (var name in ev.DjangoQueryNames(current).Distinct().Where(n => n.StartsWith(parts[^1], StringComparison.Ordinal)))
        {
            var field = ev.DjangoFieldNamed(current, name);
            bool relation = field?.Target != null || field == null && name != "pk" && name != "id";
            yield return new CompletionItem(stem + name, CompletionKind.Field, field?.Kind ?? "relation", "0" + name, InsertText: stem + name + "=");
            if (relation && lookups) yield return new CompletionItem(stem + name + "__", CompletionKind.Field, "follow the relation", "0" + name + "_", InsertText: stem + name + "__");
        }
    }

    PrepareRenameResult? UrlNameRenameTarget(string uri, Position pos)
    {
        var snap = Full();
        if (TemplateUrlAt(uri, pos) is { } inTemplate)
        {
            int cut = inTemplate.Value.LastIndexOf(':');
            return new PrepareRenameResult(new LineMap(_templateDocs[uri].Text).RangeOf(inTemplate.Start + cut + 1, inTemplate.End), inTemplate.Value[(cut + 1)..]);
        }
        if (!snap.ByUri.TryGetValue(uri, out var m)) return null;
        int offset = Lines(m).OffsetOf(pos);
        if (NamedStringAt(snap, m, offset) is { Kind: DjangoNamed.UrlName } s)
        {
            int colon = s.Value.LastIndexOf(':');
            return new PrepareRenameResult(Lines(m).RangeOf(s.Start + colon + 1, s.End), s.Value[(colon + 1)..]);
        }
        return NameDeclarationAt(snap, m, offset) is { } d ? new PrepareRenameResult(Lines(m).RangeOf(d.Start, d.End), d.Plain) : null;
    }
}
