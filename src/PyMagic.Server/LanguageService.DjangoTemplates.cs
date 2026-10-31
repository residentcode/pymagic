using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>Django template files in the editor: tags, filters, variables typed from the views that render them, `{% url %}`, `{% extends %}`, `{% include %}`, `{% static %}` and `{% load %}` navigation, checks and completion.</summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ documents

    readonly Dictionary<string, (string Text, int Version)> _templateDocs = new();

    /// <summary>The template language of a file by its type, as the Django settings page sets it: django, jinja, or none (plain HTML).</summary>
    static string TemplateLanguageOf(string uri)
    {
        int cut = uri.IndexOfAny(new[] { '?', '#' });
        string path = cut >= 0 ? uri[..cut] : uri;
        int dot = path.LastIndexOf('.');
        return dot < 0 || path.IndexOf('/', dot) >= 0 ? "none" : DjangoConfig.Current.LanguageOf(path[dot..]);
    }

    static bool IsTemplateUri(string uri) => TemplateLanguageOf(uri) != "none";

    static bool IsJinjaFile(string uri) => TemplateLanguageOf(uri) == "jinja";

    /// <summary>The text of a template file: the open buffer, else the file.</summary>
    string? TemplateTextOf(string path)
    {
        string uri = UriOfPath(path);
        foreach (var (u, doc) in _templateDocs)
            if (string.Equals(PathOfUri(u), path, StringComparison.OrdinalIgnoreCase)) return doc.Text;
        try { return File.ReadAllText(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }

    // ------------------------------------------------------------ what Django knows

    static readonly HashSet<string> BuiltinTags = new(StringComparer.Ordinal)
    {
        "autoescape", "block", "comment", "csrf_token", "cycle", "debug", "extends", "filter", "firstof", "for", "if", "ifchanged", "include", "load", "lorem",
        "now", "regroup", "resetcycle", "spaceless", "templatetag", "url", "verbatim", "widthratio", "with", "elif", "else", "empty",
        "endautoescape", "endblock", "endcomment", "endfilter", "endfor", "endif", "endifchanged", "endspaceless", "endverbatim", "endwith",
    };

    /// <summary>Libraries whose tags and filters are known: `{% load static %}`.</summary>
    static readonly Dictionary<string, string[]> KnownLibraries = new(StringComparer.Ordinal)
    {
        ["static"] = new[] { "static", "get_static_prefix", "get_media_prefix" },
        ["staticfiles"] = new[] { "static", "get_static_prefix", "get_media_prefix" },
        ["i18n"] = new[] { "trans", "translate", "blocktrans", "blocktranslate", "endblocktrans", "endblocktranslate", "plural", "language", "endlanguage", "get_available_languages", "get_current_language", "get_current_language_bidi", "get_language_info", "get_language_info_list" },
        ["l10n"] = new[] { "localize", "endlocalize" },
        ["tz"] = new[] { "localtime", "endlocaltime", "timezone", "endtimezone", "get_current_timezone" },
        ["cache"] = new[] { "cache", "endcache" },
        ["humanize"] = Array.Empty<string>(),
    };

    static readonly HashSet<string> LibraryFilters = new(StringComparer.Ordinal) { "apnumber", "intcomma", "intword", "naturalday", "naturaltime", "ordinal", "localize", "unlocalize", "localtime", "utc", "timezone", "language_name", "language_name_local", "language_bidi", "language_name_translated" };

    static readonly Dictionary<string, string> FilterDocs = new(StringComparer.Ordinal)
    {
        ["add"] = "Adds the argument to the value.", ["addslashes"] = "Adds slashes before quotes.", ["capfirst"] = "Capitalizes the first character.", ["center"] = "Centers the value in a field of the given width.",
        ["cut"] = "Removes all occurrences of the argument from the string.", ["date"] = "Formats a date as the format string says.", ["default"] = "Uses the argument when the value is false.",
        ["default_if_none"] = "Uses the argument only when the value is None.", ["dictsort"] = "Sorts a list of dicts by a key.", ["dictsortreversed"] = "Sorts a list of dicts by a key, descending.",
        ["divisibleby"] = "True if the value is divisible by the argument.", ["escape"] = "Escapes HTML characters.", ["escapejs"] = "Escapes characters for use in JavaScript strings.", ["escapeseq"] = "Escapes each item of a sequence.",
        ["filesizeformat"] = "Formats a number of bytes as KB, MB, ...", ["first"] = "The first item of a list.", ["floatformat"] = "Rounds a number to the given decimal places.", ["force_escape"] = "Escapes HTML right away.",
        ["get_digit"] = "The digit at the given position from the right.", ["iriencode"] = "Converts an IRI to a URI-safe string.", ["join"] = "Joins a list with a string.", ["json_script"] = "Outputs the value as JSON in a script tag.",
        ["last"] = "The last item of a list.", ["length"] = "The length of the value.", ["length_is"] = "True if the length is the argument.", ["linebreaks"] = "Newlines become <p> and <br>.", ["linebreaksbr"] = "Newlines become <br>.",
        ["linenumbers"] = "Displays text with line numbers.", ["ljust"] = "Left-aligns in a field of the given width.", ["lower"] = "Converts to lowercase.", ["make_list"] = "Turns the value into a list of characters.",
        ["phone2numeric"] = "Converts a phone number with letters to digits.", ["pluralize"] = "Adds a plural suffix when the value is not 1.", ["pprint"] = "Pretty-prints the value (debugging).", ["random"] = "A random item of a list.",
        ["rjust"] = "Right-aligns in a field of the given width.", ["safe"] = "Marks the string as safe HTML (no escaping).", ["safeseq"] = "Marks each item of a sequence as safe.", ["slice"] = "Returns a slice of a list.",
        ["slugify"] = "Converts to a slug.", ["stringformat"] = "Formats with a printf-style specifier.", ["striptags"] = "Strips HTML tags.", ["time"] = "Formats a time.", ["timesince"] = "How long since a date: '3 days'.",
        ["timeuntil"] = "How long until a date.", ["title"] = "Title-cases the value.", ["truncatechars"] = "Truncates after the given number of characters.", ["truncatechars_html"] = "Truncates HTML after the given number of characters.",
        ["truncatewords"] = "Truncates after the given number of words.", ["truncatewords_html"] = "Truncates HTML after the given number of words.", ["unordered_list"] = "Renders a nested list as <li> items.",
        ["upper"] = "Converts to uppercase.", ["urlencode"] = "Escapes a value for use in a URL.", ["urlize"] = "Turns URLs and emails into links.", ["urlizetrunc"] = "Like urlize, truncating the link text.",
        ["wordcount"] = "The number of words.", ["wordwrap"] = "Wraps words at the given line length.", ["yesno"] = "Maps True/False/None to the words given.",
    };

    static readonly Dictionary<string, string> TagDocs = new(StringComparer.Ordinal)
    {
        ["autoescape"] = "Controls automatic HTML escaping: `{% autoescape off %}`.", ["block"] = "Defines a block child templates can override.", ["comment"] = "Ignores everything up to `{% endcomment %}`.",
        ["csrf_token"] = "Outputs the CSRF token for a POST form.", ["cycle"] = "Cycles among the given values each time it is met.", ["debug"] = "Outputs debugging information.", ["extends"] = "Makes this template a child of another. Must be the first tag.",
        ["filter"] = "Applies filters to the whole block.", ["firstof"] = "Outputs the first argument that is not false.", ["for"] = "Loops over a sequence: `{% for x in items %}`. `forloop` and `{% empty %}` are available.",
        ["if"] = "Evaluates a condition: `{% if a and b %}` ... `{% elif %}` ... `{% else %}` ... `{% endif %}`.", ["ifchanged"] = "Outputs the block only when a value changed since the last pass.", ["include"] = "Renders another template: `{% include \"x.html\" with a=b only %}`.",
        ["load"] = "Loads tag and filter libraries: `{% load static i18n %}`.", ["lorem"] = "Outputs placeholder text.", ["now"] = "Outputs the current date and time in the given format.", ["regroup"] = "Regroups a list by an attribute.",
        ["resetcycle"] = "Resets a `cycle` tag.", ["spaceless"] = "Removes whitespace between HTML tags.", ["templatetag"] = "Outputs one of the characters of template syntax.", ["url"] = "Outputs the URL of a named route: `{% url 'name' arg %}`.",
        ["verbatim"] = "Stops the template engine from rendering the contents.", ["widthratio"] = "Computes a ratio for bar charts.", ["with"] = "Caches a value under a name in the block: `{% with total=a.b.c %}`.",
        ["static"] = "Outputs the URL of a static file.", ["trans"] = "Translates a string.", ["translate"] = "Translates a string.", ["blocktrans"] = "Translates a block with variables.", ["blocktranslate"] = "Translates a block with variables.",
    };

    static readonly Dictionary<string, string> TagSnippets = new(StringComparer.Ordinal)
    {
        ["if"] = "if ${1:condition} %}\n    $0\n{% endif %}", ["for"] = "for ${1:item} in ${2:items} %}\n    $0\n{% endfor %}", ["block"] = "block ${1:name} %}\n    $0\n{% endblock %}",
        ["with"] = "with ${1:name}=${2:value} %}\n    $0\n{% endwith %}", ["comment"] = "comment %}\n    $0\n{% endcomment %}", ["autoescape"] = "autoescape ${1:off} %}\n    $0\n{% endautoescape %}",
        ["spaceless"] = "spaceless %}\n    $0\n{% endspaceless %}", ["filter"] = "filter ${1:lower} %}\n    $0\n{% endfilter %}", ["verbatim"] = "verbatim %}\n    $0\n{% endverbatim %}",
        ["blocktranslate"] = "blocktranslate %}\n    $0\n{% endblocktranslate %}", ["blocktrans"] = "blocktrans %}\n    $0\n{% endblocktrans %}", ["ifchanged"] = "ifchanged %}\n    $0\n{% endifchanged %}",
        ["extends"] = "extends \"${1:base.html}\" %}", ["include"] = "include \"${1:partial.html}\" %}", ["url"] = "url '${1:name}' %}", ["load"] = "load ${1:static} %}", ["static"] = "static '${1:path}' %}",
        ["csrf_token"] = "csrf_token %}", ["trans"] = "trans \"${1:text}\" %}", ["translate"] = "translate \"${1:text}\" %}", ["now"] = "now \"${1:Y-m-d}\" %}", ["empty"] = "empty %}", ["else"] = "else %}", ["elif"] = "elif ${1:condition} %}",
    };

    // ------------------------------------------------------------ the project's tag libraries

    sealed record TagLibrary(string Name, ModuleInfo Module, Dictionary<string, FunctionDef> Tags, Dictionary<string, FunctionDef> Filters);

    readonly ConditionalWeakTable<Snapshot, List<TagLibrary>> _tagLibraries = new();

    List<TagLibrary> TagLibrariesOf(Snapshot snap) => _tagLibraries.GetValue(snap, s =>
    {
        var libs = new List<TagLibrary>();
        foreach (var m in s.Project)
        {
            string? path = s.UriOf.TryGetValue(m, out var moduleUri) ? PathOfUri(moduleUri) : m.Path;
            if (m.IsStub || path == null) continue;
            string norm = path.Replace('\\', '/');
            if (!norm.Contains("/templatetags/") || norm.EndsWith("/__init__.py", StringComparison.Ordinal)) continue;
            var lib = new TagLibrary(System.IO.Path.GetFileNameWithoutExtension(path), m, new(StringComparer.Ordinal), new(StringComparer.Ordinal));
            foreach (var def in AstChildren.Descendants(m.Ast).OfType<FunctionDef>())
                foreach (var d in def.Decorators)
                {
                    var callee = d is CallExpr c ? c.Func : d;
                    string? kind = callee is AttributeExpr { Attr: var a } && a is "filter" or "simple_tag" or "inclusion_tag" or "tag" or "assignment_tag" or "simple_block_tag" ? ((AttributeExpr)callee).Attr : null;
                    if (kind == null) continue;
                    string registered = d is CallExpr call ? StringKeyword(call, "name") ?? FixtureModel.StringValue(call.Args.FirstOrDefault(x => x.Name == null)?.Value) ?? def.Name : def.Name;
                    (kind == "filter" ? lib.Filters : lib.Tags)[registered] = def;
                }
            // register.filter("name", function) written as a call
            foreach (var call in AstChildren.Descendants(m.Ast).OfType<CallExpr>())
            {
                if (call.Func is not AttributeExpr { Attr: "filter" or "tag" or "simple_tag" } acc || m.ParentOf(call) is FunctionDef) continue;
                var args = call.Args.Where(x => x.Name == null).ToList();
                if (args.Count == 0) continue;
                string? nm = args.Count > 1 ? FixtureModel.StringValue(args[0].Value) : args[0].Value is NameExpr n ? n.Name : null;
                var fnName = (args.Count > 1 ? args[1].Value : args[0].Value) as NameExpr;
                var fn = fnName == null ? null : AstChildren.Descendants(m.Ast).OfType<FunctionDef>().FirstOrDefault(f => f.Name == fnName.Name);
                if (nm != null && fn != null) (acc.Attr == "filter" ? lib.Filters : lib.Tags)[nm] = fn;
            }
            libs.Add(lib);
        }
        return libs;
    });

    // ------------------------------------------------------------ static files

    readonly Dictionary<string, (DateTime At, List<(string Name, string Path)> Items)> _folderFiles = new();

    /// <summary>Files below any folder with this name in the workspace: name relative to that folder, and path.</summary>
    List<(string Name, string Path)> FilesBelowFolder(string folderName)
    {
        if (_folderFiles.TryGetValue(folderName, out var cached) && DateTime.UtcNow - cached.At < TimeSpan.FromSeconds(5)) return cached.Items;
        var found = new List<(string, string)>();
        foreach (var root in SourceRoots)
        {
            if (!Directory.Exists(root)) continue;
            var stack = new Stack<string>();
            stack.Push(root);
            while (stack.Count > 0 && found.Count < 20000)
            {
                string dir = stack.Pop();
                IEnumerable<string> subdirs;
                try { subdirs = Directory.EnumerateDirectories(dir).ToList(); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var d in subdirs)
                {
                    string name = System.IO.Path.GetFileName(d);
                    if (SkippedDirectories.Contains(name) || name.StartsWith('.')) continue;
                    if (name == folderName)
                    {
                        try
                        {
                            foreach (var f in Directory.EnumerateFiles(d, "*", SearchOption.AllDirectories).Take(10000))
                                found.Add((System.IO.Path.GetRelativePath(d, f).Replace('\\', '/'), f));
                        }
                        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* skipped */ }
                    }
                    stack.Push(d);
                }
            }
        }
        _folderFiles[folderName] = (DateTime.UtcNow, found);
        return found;
    }

    // ------------------------------------------------------------ the context a view gives a template

    sealed record ContextEntry(string Name, ModuleInfo Module, int Start, int End, Expr? Value, PyType? Fixed, string Source);

    static readonly string[] AlwaysInContext = { "request", "user", "perms", "messages", "csrf_token", "DEFAULT_MESSAGE_LEVELS", "LANGUAGES", "LANGUAGE_CODE", "LANGUAGE_BIDI", "TIME_ZONE", "debug", "sql_queries" };

    List<ContextEntry> ContextOf(Snapshot snap, string templateName)
    {
        var result = new List<ContextEntry>();
        var ev = snap.Workspace.Evaluator;
        foreach (var module in snap.Project)
        {
            if (module.IsStub || module.Source.IndexOf(templateName, StringComparison.Ordinal) < 0) continue;
            foreach (var s in NamedStringsIn(module, ev).Where(x => x.Kind == DjangoNamed.Template && x.Value == templateName))
            {
                if (s.Call != null && CalleeName(s.Call) is "render_template" or "stream_template")
                    FlaskRenderContext(module, s.Call, result);
                else if (s.Call != null)
                {
                    string? callee = CalleeName(s.Call);
                    // the context follows the template name: render(request, name, ctx), TemplateResponse(name, ctx) or TemplateResponse(request, name, ctx)
                    var positional = s.Call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
                    int nameAt = positional.FindIndex(a => a.Value.Start <= s.Start && s.End <= a.Value.End);
                    int index = nameAt >= 0 ? nameAt + 1 : callee == "render_to_string" ? 1 : 2;
                    var arg = positional.Skip(index).FirstOrDefault() ?? s.Call.Args.FirstOrDefault(a => a.Name == "context");
                    if (arg != null) CollectDictEntries(module, arg.Value, s.Call, "view", result);
                    if (arg != null && arg.Value is not (DictExpr or NameExpr or CallExpr { Func: NameExpr { Name: "dict" } })) result.Add(UnknownContext(module, s.Call));
                }
                else ClassContext(module, s, ev, result);
            }
        }
        return result;
    }

    /// <summary>The keys of a dict written in place, built in a variable (`context = {...}`, `context["k"] = v`, `context.update(k=v)`), or made with dict(k=v).</summary>
    void CollectDictEntries(ModuleInfo module, Expr container, Node at, string source, List<ContextEntry> into)
    {
        void FromLiteral(DictExpr d)
        {
            for (int i = 0; i < d.Keys.Count; i++)
                if (d.Keys[i] is ConstExpr k && FixtureModel.StringValue(k) is { } key && FixtureModel.StringSpan(k) is { } span)
                    into.Add(new ContextEntry(key, module, span.Start, span.End, d.Values[i], null, source));
        }
        switch (container)
        {
            case DictExpr dict: FromLiteral(dict); return;
            case CallExpr { Func: NameExpr { Name: "dict" } } call:
                foreach (var a in call.Args.Where(a => a.Name != null))
                    into.Add(new ContextEntry(a.Name!, module, a.Start, a.Start + a.Name!.Length, a.Value, null, source));
                return;
            case NameExpr name:
            {
                Node? scope = module.ParentOf(at);
                while (scope != null && scope is not FunctionDef && scope is not Module) scope = module.ParentOf(scope);
                scope ??= module.Ast;
                foreach (var n in AstChildren.Descendants(scope))
                {
                    if (n is AssignStmt { Targets.Count: 1 } assign)
                    {
                        if (assign.Targets[0] is NameExpr t && t.Name == name.Name && assign.Value is DictExpr or CallExpr { Func: NameExpr { Name: "dict" } }) CollectDictEntries(module, assign.Value, assign, source, into);
                        else if (assign.Targets[0] is SubscriptExpr { Value: NameExpr holder, Index: ConstExpr key } && holder.Name == name.Name
                            && FixtureModel.StringValue(key) is { } k && FixtureModel.StringSpan(key) is { } span)
                            into.Add(new ContextEntry(k, module, span.Start, span.End, assign.Value, null, source));
                    }
                    else if (n is CallExpr { Func: AttributeExpr { Attr: "update", Value: NameExpr h } } upd && h.Name == name.Name)
                    {
                        foreach (var a in upd.Args)
                            if (a.Name != null) into.Add(new ContextEntry(a.Name, module, a.Start, a.Start + a.Name.Length, a.Value, null, source));
                            else if (a.Value is DictExpr or CallExpr { Func: NameExpr { Name: "dict" } }) CollectDictEntries(module, a.Value, upd, source, into);
                    }
                }
                return;
            }
        }
    }

    void ClassContext(ModuleInfo module, NamedString s, TypeEvaluator ev, List<ContextEntry> into)
    {
        var cls = AstChildren.Descendants(module.Ast).OfType<ClassDef>().FirstOrDefault(c => c.Start <= s.Start && s.End <= c.End && c.Body.Any(b => b.Start <= s.Start && s.End <= b.End));
        if (cls == null) return;
        var info = ClassInfoOf(module, cls, ev);
        string BaseNames() => string.Join(" ", cls.Bases.Select(b => ReceiverName(b.Value) ?? ""));
        string bases = BaseNames();
        // the model comes from `model = Book` or a queryset
        ClassInfo? model = null;
        string? objectName = null;
        foreach (var stmt in cls.Body.OfType<AssignStmt>().Where(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr))
        {
            string target = ((NameExpr)stmt.Targets[0]).Name;
            if (target == "model" && ev.TypeOf(module, stmt.Value) is ClassType { IsInstance: false } mc) model = mc.Info;
            else if (target == "queryset" && ev.DjangoModelOfQuery(ev.TypeOf(module, stmt.Value)) is { } qm) model ??= qm;
            else if (target == "context_object_name" && FixtureModel.StringValue(stmt.Value) is { } name) objectName = name;
            else if (target == "extra_context" && stmt.Value is DictExpr) CollectDictEntries(module, stmt.Value, stmt, "extra_context", into);
        }
        PyType? instance = null;
        if (model != null) instance = new ClassType(model, Array.Empty<PyType>(), true);
        var span = (module, cls.NameStart, cls.NameStart + cls.Name.Length);
        void Add(string name, PyType? type) => into.Add(new ContextEntry(name, module, span.Item2, span.Item3, null, type, $"{cls.Name}"));
        PyType? ListOf(PyType? item) => item == null ? null : ev.BuiltinInstance("list") is ClassType lt ? lt with { Args = new[] { item } } : null;
        string lower = model?.Name.ToLowerInvariant() ?? "";
        if (bases.Contains("ListView") || bases.Contains("MultipleObjectMixin"))
        {
            Add("object_list", ListOf(instance));
            if (objectName != null) Add(objectName, ListOf(instance)); else if (lower.Length > 0) Add(lower + "_list", ListOf(instance));
            Add("page_obj", null); Add("paginator", null); Add("is_paginated", ev.BuiltinInstance("bool"));
        }
        if (bases.Contains("DetailView") || bases.Contains("UpdateView") || bases.Contains("DeleteView") || bases.Contains("SingleObjectMixin"))
        {
            Add("object", instance);
            if (objectName != null) Add(objectName, instance); else if (lower.Length > 0) Add(lower, instance);
        }
        if (bases.Contains("FormView") || bases.Contains("CreateView") || bases.Contains("UpdateView")) Add("form", null);
        Add("view", info != null ? new ClassType(info, Array.Empty<PyType>(), true) : null);

        foreach (var def in cls.Body.OfType<FunctionDef>().Where(f => f.Name == "get_context_data"))
        {
            foreach (var n in AstChildren.Descendants(def))
            {
                if (n is AssignStmt { Targets.Count: 1 } a && a.Targets[0] is SubscriptExpr { Value: NameExpr, Index: ConstExpr key } && FixtureModel.StringValue(key) is { } k && FixtureModel.StringSpan(key) is { } ks)
                    into.Add(new ContextEntry(k, module, ks.Start, ks.End, a.Value, null, cls.Name));
                else if (n is CallExpr { Func: AttributeExpr { Attr: "update" } } upd)
                    foreach (var arg in upd.Args.Where(x => x.Name != null))
                        into.Add(new ContextEntry(arg.Name!, module, arg.Start, arg.Start + arg.Name!.Length, arg.Value, null, cls.Name));
            }
        }
    }

    // ------------------------------------------------------------ variables in a template

    sealed record TemplateVar(string Name, PyType? Type, int Start, int End, string Origin, ContextEntry? Entry);

    sealed class TemplateScope
    {
        public List<(string Closer, List<TemplateVar> Vars)> Frames { get; } = new() { ("", new()) };
        public TemplateVar? Find(string name)
        {
            for (int i = Frames.Count - 1; i >= 0; i--)
                foreach (var v in Frames[i].Vars) if (v.Name == name) return v;
            return null;
        }
        public IEnumerable<TemplateVar> All() => Frames.AsEnumerable().Reverse().SelectMany(f => f.Vars);
    }

    sealed class TemplateAnalysis
    {
        public string Uri = "";
        public string Text = "";
        public string? Name;
        public ParsedTemplate Parsed = null!;
        public List<ContextEntry> Context = new();
        public Snapshot Snap = null!;
        /// <summary>Jinja rather than Django: Flask, Starlette and FastAPI templates, `.jinja` / `.j2` files and a Django project's `jinja2` folders.</summary>
        public bool Jinja;
    }

    TemplateAnalysis AnalyzeTemplate(string uri)
    {
        var snap = Full();
        string text = _templateDocs.TryGetValue(uri, out var doc) ? doc.Text : "";
        string? path = PathOfUri(uri);
        string? name = null;
        if (path != null)
        {
            var match = Templates().FirstOrDefault(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase));
            if (match.Name != null) name = match.Name;
        }
        bool jinja = IsJinjaTemplate(uri, snap);
        return new TemplateAnalysis { Uri = uri, Text = text, Name = name, Parsed = DjangoTemplateParser.Parse(text, jinja), Snap = snap, Context = name == null ? new() : ContextOf(snap, name), Jinja = jinja };
    }

    PyType? TypeOfEntry(TemplateAnalysis a, ContextEntry e)
    {
        if (e.Fixed != null) return e.Fixed;
        if (e.Value == null) return null;
        var t = a.Snap.Workspace.Evaluator.TypeOf(e.Module, e.Value);
        return t is UnknownType ? null : t;
    }

    static readonly Regex ForArgs = new(@"^(?<vars>[\w\s,]+?)\s+in\s+(?<iter>.+?)(?:\s+reversed)?$", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex AsVar = new(@"\bas\s+(?<name>\w+)\s*$", RegexOptions.Compiled);

    /// <summary>The names a template can use at an offset: loop and `with` variables of the tags around it, the keys the views give, and what Django always adds.</summary>
    TemplateScope ScopeAt(TemplateAnalysis a, int offset)
    {
        var scope = new TemplateScope();
        var ev = a.Snap.Workspace.Evaluator;
        if (a.Jinja) return JinjaScopeAt(a, offset);
        foreach (var e in a.Context.Where(e => e.Name != UnknownContextName).GroupBy(e => e.Name).Select(g => g.First()))
            scope.Frames[0].Vars.Add(new TemplateVar(e.Name, TypeOfEntry(a, e), e.Start, e.End, "context", e));
        foreach (var n in AlwaysInContext.Where(n => scope.Find(n) == null))
        {
            PyType? t = n == "request" ? (ev.InstanceOf("django.http.request", "HttpRequest") ?? ev.InstanceOf("django.http", "HttpRequest")) : null;
            scope.Frames[0].Vars.Add(new TemplateVar(n, t is UnknownType ? null : t, 0, 0, "builtin", null));
        }

        foreach (var tag in a.Parsed.Tags)
        {
            if (tag.Start >= offset || offset < tag.End && tag.Start < offset) break;
            switch (tag.Name)
            {
                case "for":
                {
                    var m = ForArgs.Match(tag.Args);
                    var vars = new List<TemplateVar>();
                    if (m.Success)
                    {
                        var element = TypeOfTemplateExpression(a, scope, m.Groups["iter"].Value) is { } it ? ev.IterElement(it) : null;
                        if (element is UnknownType) element = null;
                        var names = m.Groups["vars"].Value.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                        int cursor = tag.ArgsStart;
                        foreach (var n in names)
                        {
                            int at = tag.ArgsStart + tag.Args.IndexOf(n, cursor - tag.ArgsStart, StringComparison.Ordinal);
                            vars.Add(new TemplateVar(n, names.Length == 1 ? element : null, at, at + n.Length, "for", null));
                            cursor = at + n.Length;
                        }
                    }
                    vars.Add(new TemplateVar("forloop", null, tag.Start, tag.Start + 2, "forloop", null));
                    scope.Frames.Add(("endfor", vars));
                    break;
                }
                case "with":
                {
                    var vars = new List<TemplateVar>();
                    foreach (var (text, off) in DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart))
                    {
                        int eq = text.IndexOf('=');
                        if (eq > 0 && !text.StartsWith('"')) vars.Add(new TemplateVar(text[..eq], TypeOfTemplateExpression(a, scope, text[(eq + 1)..]), off, off + eq, "with", null));
                    }
                    var legacy = Regex.Match(tag.Args, @"^(?<expr>.+?)\s+as\s+(?<name>\w+)$");
                    if (vars.Count == 0 && legacy.Success)
                        vars.Add(new TemplateVar(legacy.Groups["name"].Value, TypeOfTemplateExpression(a, scope, legacy.Groups["expr"].Value), tag.ArgsStart + legacy.Groups["name"].Index, tag.ArgsStart + legacy.Groups["name"].Index + legacy.Groups["name"].Length, "with", null));
                    scope.Frames.Add(("endwith", vars));
                    break;
                }
                case "empty":
                    if (scope.Frames[^1].Closer == "endfor") scope.Frames[^1].Vars.Clear();
                    break;
                case "block":
                    scope.Frames.Add(("endblock", new List<TemplateVar> { new("block", null, tag.Start, tag.Start + 2, "block", null) }));
                    break;
                case "endfor" or "endwith" or "endblock":
                    if (scope.Frames.Count > 1 && scope.Frames[^1].Closer == tag.Name) scope.Frames.RemoveAt(scope.Frames.Count - 1);
                    break;
                case "url" or "trans" or "translate" or "firstof" or "cycle" or "now" or "regroup" or "blocktrans" or "blocktranslate" or "static" or "get_static_prefix" or "get_media_prefix" or "lorem" or "get_current_language" or "get_available_languages" or "get_language_info" or "get_current_timezone":
                {
                    var asMatch = AsVar.Match(tag.Args);
                    if (asMatch.Success)
                    {
                        var g = asMatch.Groups["name"];
                        scope.Frames[^1].Vars.Add(new TemplateVar(g.Value, tag.Name is "url" or "trans" or "translate" or "static" ? ev.BuiltinInstance("str") : null, tag.ArgsStart + g.Index, tag.ArgsStart + g.Index + g.Length, tag.Name, null));
                    }
                    break;
                }
            }
        }
        return scope;
    }

    static string FirstOfFilters(string expression)
    {
        bool dq = false, sq = false;
        for (int i = 0; i < expression.Length; i++)
        {
            char c = expression[i];
            if (c == '"' && !sq) dq = !dq;
            else if (c == '\'' && !dq) sq = !sq;
            else if (c == '|' && !dq && !sq) return expression[..i];
        }
        return expression;
    }

    /// <summary>The type of an expression like `book.author.name|upper` (what Django looks up: attribute, key, then index), or null when it is not known.</summary>
    PyType? TypeOfTemplateExpression(TemplateAnalysis a, TemplateScope scope, string expression)
    {
        string expr = FirstOfFilters(expression).Trim();
        if (expr.Length == 0) return null;
        var ev = a.Snap.Workspace.Evaluator;
        if (DjangoTemplateParser.Unquote(expr) != null) return ev.BuiltinInstance("str");
        if (Regex.IsMatch(expr, @"^-?\d+$")) return ev.BuiltinInstance("int");
        if (Regex.IsMatch(expr, @"^-?\d+\.\d+$")) return ev.BuiltinInstance("float");
        var parts = expr.Split('.');
        var first = scope.Find(parts[0]);
        PyType? t = first?.Type;
        // Jinja calls with (): d.items() is what items returns
        for (int i = 1; i < parts.Length && t != null; i++) t = TemplateStep(ev, t, parts[i].EndsWith("()", StringComparison.Ordinal) ? parts[i][..^2] : parts[i]);
        return t is UnknownType ? null : t;
    }

    static PyType? TemplateStep(TypeEvaluator ev, PyType t, string segment)
    {
        PyType? next;
        if (segment.Length > 0 && segment.All(char.IsDigit)) next = ev.IterElement(t);
        else if (t is ClassType { IsInstance: true, Info.Name: "dict" or "Mapping" or "OrderedDict" or "defaultdict" } d && d.Args.Count >= 2) next = d.Args[1];
        else
        {
            var r = ev.AttributeType(t, segment);
            if (r.Missing.Count > 0 && r.Type is UnknownType) return null;
            next = r.Type;
        }
        // Django calls what it finds if it can be called
        if (next is FunctionType) next = ev.ReturnOf(next);
        return next is UnknownType ? null : next;
    }

    // ------------------------------------------------------------ what is under the cursor

    sealed record Thing(string Kind, string Text, int Start, int End, TemplateTag? Tag, int ArgIndex, string[] Chain, int ChainIndex, TemplateVariable? Variable);

    /// <summary>The identifier chain around an offset in an expression: `book.author|name` gives ["book","author"] when on `author`.</summary>
    static (string[] Chain, int Index, int Start, int End)? ChainAt(string text, int exprStart, string expr, int offset)
    {
        int rel = offset - exprStart;
        if (rel < 0 || rel > expr.Length) return null;
        // the word under the cursor
        int s = rel, e = rel;
        bool Word(char c) => char.IsLetterOrDigit(c) || c == '_';
        while (s > 0 && Word(expr[s - 1])) s--;
        while (e < expr.Length && Word(expr[e])) e++;
        if (s == e) return null;
        // quoted strings are not variables
        int quotes = expr[..s].Count(c => c == '"') % 2 + expr[..s].Count(c => c == '\'') % 2;
        if (quotes > 0) return null;
        int cs = s;
        while (cs > 1 && expr[cs - 1] == '.' && Word(expr[cs - 2])) { cs--; while (cs > 0 && Word(expr[cs - 1])) cs--; }
        string chainText = expr[cs..e];
        var chain = chainText.Split('.');
        // after a pipe it is a filter, not a chain
        int pipe = expr[..s].LastIndexOf('|');
        if (pipe >= 0 && !expr[pipe..s].Contains(' ') && !expr[pipe..s].Contains('.')) return (new[] { expr[s..e] }, -1, exprStart + s, exprStart + e);
        return (chain, chain.Length - 1, exprStart + s, exprStart + e);
    }

    // ------------------------------------------------------------ hover and go to definition

    Hover? TemplateHover(string uri, Position pos)
    {
        var a = AnalyzeTemplate(uri);
        var lines = new LineMap(a.Text);
        int offset = lines.OffsetOf(pos);
        var found = TemplateTargets(a, offset);
        if (found == null) return null;
        return new Hover(new MarkupContent("markdown", found.Hover), lines.RangeOf(found.Start, found.End));
    }

    IReadOnlyList<Location> TemplateDefinition(string uri, Position pos)
    {
        var a = AnalyzeTemplate(uri);
        var lines = new LineMap(a.Text);
        int offset = lines.OffsetOf(pos);
        return TemplateTargets(a, offset)?.Locations ?? (IReadOnlyList<Location>)Array.Empty<Location>();
    }

    sealed record TemplateTarget(string Hover, int Start, int End, List<Location> Locations);

    Location? InTemplate(TemplateAnalysis a, int start, int end) => new Location(a.Uri, new LineMap(a.Text).RangeOf(start, end));

    Location? InFile(string path, int start, int end)
    {
        string? text = TemplateTextOf(path);
        return text == null ? null : new Location(UriOfPath(path), new LineMap(text).RangeOf(start, end));
    }

    TemplateTarget? TemplateTargets(TemplateAnalysis a, int offset)
    {
        var snap = a.Snap;
        var ev = snap.Workspace.Evaluator;
        string Code(string s) => "```python\n" + s + "\n```";

        // url_for('endpoint'), url_for('static', filename='...'), the template of import / from
        if (a.Jinja && JinjaStringTarget(a, offset) is { } named) return named;

        // {{ expression }}
        var variable = a.Parsed.Variables.FirstOrDefault(v => offset >= v.Start && offset <= v.End);
        if (variable != null) return ExpressionTarget(a, variable.Expression, variable.ExprStart, offset);

        var tag = a.Parsed.Tags.FirstOrDefault(t => offset >= t.Start && offset <= t.End);
        if (tag == null) return null;

        // the tag's own name
        int nameStart = TagNameStart(a.Text, tag);
        if (a.Jinja) return JinjaTagTarget(a, tag, nameStart, offset);
        if (offset >= nameStart && offset <= nameStart + tag.Name.Length)
        {
            var custom = CustomTag(snap, a, tag.Name);
            var locs = new List<Location>();
            if (custom is { } c && FixtureLocation(c.Module, c.Def.NameStart, c.Def.NameStart + c.Def.Name.Length) is { } l) locs.Add(l);
            string doc = TagDocs.GetValueOrDefault(tag.Name) ?? (custom != null ? $"Custom tag from the `{custom.Value.Library}` library." : "");
            return doc.Length == 0 && locs.Count == 0 ? null : new TemplateTarget(Code("{% " + tag.Name + " %}") + "\n\n" + doc, nameStart, nameStart + tag.Name.Length, locs);
        }

        var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
        var arg = args.FirstOrDefault(x => offset >= x.Offset && offset <= x.Offset + x.Text.Length);
        if (arg.Text == null) return null;
        string? quoted = DjangoTemplateParser.Unquote(arg.Text);
        int qs = arg.Offset + 1, qe = arg.Offset + arg.Text.Length - 1;
        bool first = args.Count > 0 && args[0].Offset == arg.Offset;

        switch (tag.Name)
        {
            case "extends" or "include" when quoted != null && first:
            {
                var files = Templates().Where(t => t.Name == quoted).ToList();
                if (files.Count == 0) return null;
                return new TemplateTarget(Code("(template) " + quoted) + "\n\n" + string.Join("\n\n", files.Select(f => $"`{f.Path}`")), qs, qe, files.Select(f => InFile(f.Path, 0, 0)).OfType<Location>().ToList());
            }
            case "url" when quoted != null && first:
            {
                var defs = UrlDefsFor(DjangoIndexOf(snap), quoted).ToList();
                if (defs.Count == 0) return null;
                return new TemplateTarget(Code("(url) " + quoted) + "\n\n" + string.Join("\n\n", defs.Select(d => $"`{d.Route}` — {System.IO.Path.GetFileName(d.Module.Path ?? d.Module.Name)}")), qs, qe,
                    defs.Select(d => FixtureLocation(d.Module, d.Start, d.End)).OfType<Location>().ToList());
            }
            case "static" when quoted != null && first:
            {
                var files = FilesBelowFolder("static").Where(f => f.Name == quoted).ToList();
                if (files.Count == 0) return null;
                return new TemplateTarget(Code("(static file) " + quoted) + "\n\n" + string.Join("\n\n", files.Select(f => $"`{f.Path}`")), qs, qe, files.Select(f => new Location(UriOfPath(f.Path), new Range(new Position(0, 0), new Position(0, 0)))).ToList());
            }
            case "load":
            {
                string lib = arg.Text;
                var project = TagLibrariesOf(snap).Where(l => l.Name == lib).ToList();
                if (project.Count > 0)
                    return new TemplateTarget(Code("(tag library) " + lib) + "\n\n" + string.Join("\n\n", project.Select(l => $"`{l.Module.Path}`")), arg.Offset, arg.Offset + lib.Length,
                        project.Select(l => FixtureLocation(l.Module, 0, 0)).OfType<Location>().ToList());
                if (KnownLibraries.ContainsKey(lib)) return new TemplateTarget(Code("(tag library) " + lib) + "\n\nDjango's own library.", arg.Offset, arg.Offset + lib.Length, new());
                return null;
            }
            case "block" or "endblock" when !arg.Text.StartsWith('"'):
            {
                var parent = ParentBlock(a, arg.Text);
                if (parent == null) return null;
                return new TemplateTarget(Code("(block) " + arg.Text) + $"\n\nOverrides the block of `{parent.Value.Template}`.", arg.Offset, arg.Offset + arg.Text.Length, new() { parent.Value.Where });
            }
        }

        // anything else is an expression (if, for, with, url arguments, ...)
        string text = arg.Text;
        int eq = text.IndexOf('=');
        int exprOffset = arg.Offset;
        if (eq > 0 && !text.StartsWith('"') && !text.StartsWith('\'')) { exprOffset += eq + 1; text = text[(eq + 1)..]; }
        return ExpressionTarget(a, text, exprOffset, offset);
    }

    (string Library, ModuleInfo Module, FunctionDef Def)? CustomTag(Snapshot snap, TemplateAnalysis a, string name)
    {
        foreach (var lib in TagLibrariesOf(snap))
            if (lib.Tags.TryGetValue(name, out var def)) return (lib.Name, lib.Module, def);
        return null;
    }

    (string Template, Location Where)? ParentBlock(TemplateAnalysis a, string block)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = a.Parsed;
        for (int depth = 0; depth < 12; depth++)
        {
            var ext = current.Tags.FirstOrDefault(t => t.Name == "extends");
            string? parentName = ext == null ? null : DjangoTemplateParser.Unquote(DjangoTemplateParser.SplitArguments(ext.Args, ext.ArgsStart).FirstOrDefault().Text ?? "");
            if (parentName == null) return null;
            var file = Templates().FirstOrDefault(t => t.Name == parentName);
            if (file.Path == null || !seen.Add(file.Path)) return null;
            string? text = TemplateTextOf(file.Path);
            if (text == null) return null;
            current = DjangoTemplateParser.Parse(text, a.Jinja);
            foreach (var t in current.Tags.Where(t => t.Name == "block"))
            {
                var parts = DjangoTemplateParser.SplitArguments(t.Args, t.ArgsStart);
                if (parts.Count > 0 && parts[0].Text == block && InFile(file.Path, parts[0].Offset, parts[0].Offset + block.Length) is { } where) return (parentName, where);
            }
        }
        return null;
    }

    TemplateTarget? ExpressionTarget(TemplateAnalysis a, string expression, int exprStart, int offset)
    {
        var ev = a.Snap.Workspace.Evaluator;
        string Code(string s) => "```python\n" + s + "\n```";
        if (ChainAt(a.Text, exprStart, expression, offset) is not { } at) return null;

        // a filter after a pipe
        if (at.Index < 0 && a.Jinja) return JinjaFilterTarget(a, at.Chain[0], at.Start, at.End);
        if (at.Index < 0)
        {
            string filter = at.Chain[0];
            var custom = TagLibrariesOf(a.Snap).Select(l => (l, ok: l.Filters.TryGetValue(filter, out var def), def)).FirstOrDefault(x => x.ok);
            var locs = new List<Location>();
            if (custom.ok && FixtureLocation(custom.l.Module, custom.def.NameStart, custom.def.NameStart + custom.def.Name.Length) is { } l) locs.Add(l);
            string doc = FilterDocs.GetValueOrDefault(filter) ?? (custom.ok ? $"Custom filter from the `{custom.l.Name}` library." : "");
            return doc.Length == 0 && locs.Count == 0 ? null : new TemplateTarget(Code("(filter) " + filter) + "\n\n" + doc, at.Start, at.End, locs);
        }

        var scope = ScopeAt(a, offset);
        var variable = scope.Find(at.Chain[0]);
        if (variable == null) return null;
        if (a.Jinja && JinjaMacroTarget(a, variable, at.Chain, at.Index, at.Start, at.End) is { } macro) return macro;
        var locations = new List<Location>();
        PyType? t = variable.Type;
        string description = variable.Origin switch
        {
            "context" => $"(context) {variable.Name}" + (variable.Entry != null ? $" — from `{variable.Entry.Source}`" : ""),
            "builtin" when a.Jinja => $"(global) {variable.Name} — " + JinjaGlobalDocs.GetValueOrDefault(variable.Name, "a global of the template environment"),
            "builtin" => $"(context) {variable.Name} — added by Django's context processors",
            "loop" => "(loop) loop — index, index0, revindex, first, last, length, cycle(...), depth, previtem, nextitem, changed(...)",
            "forloop" => "(loop) forloop — counter, counter0, revcounter, first, last, parentloop",
            "block" => "(block) block — block.super renders the parent's block",
            _ => $"(variable) {variable.Name} — from `{{% {variable.Origin} %}}`",
        };
        if (at.Index == 0)
        {
            if (variable.Entry != null && FixtureLocation(variable.Entry.Module, variable.Entry.Start, variable.Entry.End) is { } vl) locations.Add(vl);
            else if (variable.Origin is "for" or "with" or "url" or "trans" or "translate" or "firstof" or "cycle" or "now" or "regroup" or "set" or "macro" or "call" && InTemplate(a, variable.Start, variable.End) is { } tl) locations.Add(tl);
        }
        else
        {
            for (int i = 1; i <= at.Index && t != null; i++)
            {
                if (i == at.Index && t is ClassType receiver) locations.AddRange(MemberLocations(ev, receiver, at.Chain[i]));
                t = TemplateStep(ev, t, at.Chain[i]);
            }
            description = $"(attribute) {string.Join(".", at.Chain[..(at.Index + 1)])}";
        }
        string type = t == null ? "" : ": " + t.Display();
        return new TemplateTarget(Code(description.StartsWith("(attribute)") || variable.Origin is "for" or "with" ? $"{description.Split(" — ")[0]}{type}" : description + type), at.Start, at.End, locations);
    }

    List<Location> MemberLocations(TypeEvaluator ev, ClassType receiver, string name)
    {
        var result = new List<Location>();
        foreach (var anc in new PyType[] { receiver }.Concat(ev.MroOf(receiver.Info)))
        {
            if (anc is not ClassType c) continue;
            var decl = c.Info.Scope.Symbols.GetValueOrDefault(name)?.Declarations.FirstOrDefault();
            if (decl == null) continue;
            int start = decl.Node is FunctionDef f ? f.NameStart : decl.Node is ClassDef cd ? cd.NameStart : decl.Node.Start;
            int end = decl.Node is FunctionDef f2 ? f2.NameStart + f2.Name.Length : decl.Node is ClassDef cd2 ? cd2.NameStart + cd2.Name.Length : decl.Node.End;
            if (FixtureLocation(c.Info.Module, start, end) is { } loc) { result.Add(loc); break; }
        }
        return result;
    }

    // ------------------------------------------------------------ checks

    IReadOnlyList<LspDiagnostic> TemplateDiagnostics(string uri)
    {
        var result = new List<LspDiagnostic>();
        var a = AnalyzeTemplate(uri);
        if (a.Jinja) return ApplyTemplateProfile(JinjaDiagnostics(a));
        var lines = new LineMap(a.Text);
        var snap = a.Snap;
        bool django = snap.Project.Any(m => !m.IsStub && m.Source.Contains("django", StringComparison.OrdinalIgnoreCase));
        if (!django) return result;

        void Add(int start, int end, string message, int severity, string code) =>
            result.Add(new LspDiagnostic(lines.RangeOf(start, Math.Max(end, start + 1)), severity, message, Code: code));

        foreach (var p in a.Parsed.Problems) Add(p.Start, p.End, p.Message, Severity.Error, p.Code);

        // libraries loaded: the tags and filters their names allow
        var loaded = new HashSet<string>(StringComparer.Ordinal);
        var tags = new HashSet<string>(BuiltinTags, StringComparer.Ordinal);
        var filters = new HashSet<string>(FilterDocs.Keys, StringComparer.Ordinal);
        bool opaque = false;
        var libraries = TagLibrariesOf(snap);
        foreach (var load in a.Parsed.Tags.Where(t => t.Name == "load"))
        {
            var names = DjangoTemplateParser.SplitArguments(load.Args, load.ArgsStart).Select(x => x.Text).ToList();
            // {% load a b from library %}
            int from = names.IndexOf("from");
            if (from > 0) { foreach (var n in names.Take(from)) { tags.Add(n); filters.Add(n); } if (from + 1 < names.Count) loaded.Add(names[from + 1]); continue; }
            foreach (var lib in names)
            {
                loaded.Add(lib);
                if (KnownLibraries.TryGetValue(lib, out var known)) { foreach (var t in known) tags.Add(t); foreach (var f in LibraryFilters) filters.Add(f); continue; }
                var project = libraries.Where(l => l.Name == lib).ToList();
                if (project.Count == 0) { opaque = true; continue; }
                foreach (var l in project) { foreach (var t in l.Tags.Keys) tags.Add(t); foreach (var f in l.Filters.Keys) filters.Add(f); }
            }
        }

        if (!opaque)
        {
            foreach (var tag in a.Parsed.Tags)
            {
                if (tag.Name.Length == 0 || tags.Contains(tag.Name) || tag.Name.StartsWith("end", StringComparison.Ordinal) && tags.Contains(tag.Name[3..])
                    || DjangoTemplateParser.Closers.ContainsValue(tag.Name) || tag.Name is "plural" or "endcomment" or "endverbatim") continue;
                int nameStart = TagNameStart(a.Text, tag);
                Add(nameStart, nameStart + tag.Name.Length, $"Unknown tag '{tag.Name}': it may need a {{% load %}}", Severity.Information, "template-tag");
            }
            foreach (var v in a.Parsed.Variables) CheckFilters(v.Expression, v.ExprStart);
            foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "if" or "elif" or "for" or "with" or "firstof" or "filter" or "include")) CheckFilters(tag.Args, tag.ArgsStart);
        }

        void CheckFilters(string expression, int start)
        {
            foreach (Match m in Regex.Matches(expression, @"\|\s*(?<f>\w+)"))
            {
                // a pipe inside a quoted string is text
                if (expression[..m.Index].Count(c => c == '"') % 2 == 1 || expression[..m.Index].Count(c => c == '\'') % 2 == 1) continue;
                string f = m.Groups["f"].Value;
                if (!filters.Contains(f)) Add(start + m.Groups["f"].Index, start + m.Groups["f"].Index + f.Length, $"Unknown filter '{f}': it may need a {{% load %}}", Severity.Information, "template-filter");
            }
        }

        bool hasUrls = DjangoIndexOf(snap).Urls.Count > 0;
        bool hasTemplates = Templates().Count > 0;
        bool hasStatic = FilesBelowFolder("static").Count > 0;
        foreach (var tag in a.Parsed.Tags)
        {
            var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
            if (args.Count == 0) continue;
            string? quoted = DjangoTemplateParser.Unquote(args[0].Text);
            if (quoted == null) continue;
            int qs = args[0].Offset + 1, qe = args[0].Offset + args[0].Text.Length - 1;
            switch (tag.Name)
            {
                case "extends" or "include" when hasTemplates && !IsLibraryTemplate(quoted) && !Templates().Any(t => t.Name == quoted):
                    Add(qs, qe, $"Template '{quoted}' is not in any templates folder of the project", Severity.Information, "django-template");
                    break;
                case "static" when hasStatic && !FilesBelowFolder("static").Any(f => f.Name == quoted):
                    Add(qs, qe, $"Static file '{quoted}' is not in any static folder of the project", Severity.Information, "template-static");
                    break;
                case "url" when hasUrls:
                {
                    var defs = UrlDefsFor(DjangoIndexOf(snap), quoted).ToList();
                    if (defs.Count == 0) { Add(qs, qe, $"No URL is named '{quoted}'", Severity.Information, "django-url-name"); break; }
                    if (defs.Count == 1 && CalleeName(defs[0].Call) == "path")
                    {
                        int expected = Regex.Matches(defs[0].Route, @"<[^>]+>").Count;
                        int given = args.Skip(1).TakeWhile(x => x.Text != "as").Count();
                        if (!args.Skip(1).Any(x => x.Text.StartsWith('"') || x.Text.StartsWith('\'') && false) && given != expected && !args.Any(x => x.Text.Contains('*')))
                            Add(args[0].Offset, args[0].Offset + args[0].Text.Length, $"URL '{quoted}' takes {expected} argument{(expected == 1 ? "" : "s")} ({defs[0].Route}), {given} given", Severity.Warning, "template-url-arguments");
                    }
                    break;
                }
            }
        }

        // blocks that no parent has
        var ext = a.Parsed.Tags.FirstOrDefault(t => t.Name == "extends");
        if (ext != null)
            foreach (var block in a.Parsed.Tags.Where(t => t.Name == "block"))
            {
                var parts = DjangoTemplateParser.SplitArguments(block.Args, block.ArgsStart);
                if (parts.Count == 0 || ParentChainKnown(a) != true) continue;
                if (ParentBlock(a, parts[0].Text) == null && !BlockInAncestors(a, parts[0].Text))
                    Add(parts[0].Offset, parts[0].Offset + parts[0].Text.Length, $"No parent template has a block '{parts[0].Text}': its content is never shown", Severity.Information, "template-block");
            }

        // attributes that cannot exist on a class of the project
        foreach (var v in a.Parsed.Variables) CheckTemplateAttributes(a, v.Expression, v.ExprStart, Add);
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "if" or "elif" or "for" or "with")) CheckTemplateAttributes(a, tag.Args, tag.ArgsStart, Add);
        return ApplyTemplateProfile(result);
    }

    /// <summary>`book.titel` where `book` is an instance of a class of the project that has no `titel`.</summary>
    void CheckTemplateAttributes(TemplateAnalysis a, string expression, int start, Action<int, int, string, int, string> add)
    {
        var snap = a.Snap;
        var ev = snap.Workspace.Evaluator;
        foreach (Match m in Regex.Matches(FirstOfFilters(expression), @"(?<![\w.""'])[A-Za-z_]\w*(?:\.\w+)+"))
        {
            int at = start + m.Index;
            var scope = ScopeAt(a, at);
            var parts = m.Value.Split('.');
            var t = scope.Find(parts[0])?.Type;
            int offset = at + parts[0].Length;
            for (int i = 1; i < parts.Length && t != null; i++)
            {
                offset++;
                if (t is ClassType { IsInstance: true } c && IsProjectClass(snap, c.Info) && !parts[i].All(char.IsDigit))
                {
                    var r = ev.AttributeType(t, parts[i]);
                    if (r.Missing.Count > 0 && r.Type is UnknownType) { add(offset, offset + parts[i].Length, $"'{c.Info.Name}' has no attribute '{parts[i]}'", Severity.Information, "template-attribute"); break; }
                }
                t = TemplateStep(ev, t, parts[i]);
                offset += parts[i].Length;
            }
        }
    }

    bool IsProjectClass(Snapshot snap, ClassInfo c) => snap.Project.Any(m => ReferenceEquals(m, c.Module)) || snap.Workspace.UserModules.Any(m => ReferenceEquals(m, c.Module));

    /// <summary>True when every template in the chain of {% extends %} could be found: a block can only be called missing then.</summary>
    bool? ParentChainKnown(TemplateAnalysis a)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var current = a.Parsed;
        for (int depth = 0; depth < 12; depth++)
        {
            var ext = current.Tags.FirstOrDefault(t => t.Name == "extends");
            if (ext == null) return true;
            string? parentName = DjangoTemplateParser.Unquote(DjangoTemplateParser.SplitArguments(ext.Args, ext.ArgsStart).FirstOrDefault().Text ?? "");
            if (parentName == null) return false;
            var file = Templates().FirstOrDefault(t => t.Name == parentName);
            if (file.Path == null || !seen.Add(file.Path)) return false;
            string? text = TemplateTextOf(file.Path);
            if (text == null) return false;
            current = DjangoTemplateParser.Parse(text, a.Jinja);
        }
        return false;
    }

    bool BlockInAncestors(TemplateAnalysis a, string block) => ParentBlock(a, block) != null;

    // ------------------------------------------------------------ completion

    IReadOnlyList<CompletionItem> TemplateCompletion(string uri, Position pos)
    {
        var a = AnalyzeTemplate(uri);
        var lines = new LineMap(a.Text);
        int offset = lines.OffsetOf(pos);
        string text = a.Text;
        var items = new List<CompletionItem>();

        int openTag = text.LastIndexOf("{%", Math.Max(0, offset - 1), StringComparison.Ordinal);
        int openVar = text.LastIndexOf("{{", Math.Max(0, offset - 1), StringComparison.Ordinal);
        int closeTag = openTag >= 0 ? text.IndexOf("%}", openTag + 2, StringComparison.Ordinal) : -1;
        int closeVar = openVar >= 0 ? text.IndexOf("}}", openVar + 2, StringComparison.Ordinal) : -1;
        bool inTag = openTag >= 0 && openTag >= openVar && (closeTag < 0 || closeTag >= offset) && offset >= openTag + 2;
        bool inVar = !inTag && openVar >= 0 && (closeVar < 0 || closeVar >= offset) && offset >= openVar + 2;
        if (!inTag && !inVar)
        {
            // in the text: the live templates, for the word being typed (not inside an HTML tag)
            int w = offset;
            while (w > 0 && (char.IsLetterOrDigit(text[w - 1]) || text[w - 1] == '_')) w--;
            int lt = text.LastIndexOf('<', Math.Max(0, offset - 1)), gt = text.LastIndexOf('>', Math.Max(0, offset - 1));
            if (offset > w && !(lt > gt)) items.AddRange(TemplateLiveItems(text[w..offset]));
            return items;
        }

        string inside = text[((inTag ? openTag : openVar) + 2)..offset];
        if (a.Jinja) return JinjaCompletion(a, inTag, inside.Length > 0 && inside[0] is '-' or '+' ? " " + inside[1..] : inside, offset, closedAfter: inTag ? text[offset..LineEndOf(text, offset)].Contains("%}") : text[offset..LineEndOf(text, offset)].Contains("}}"));
        var snap = a.Snap;
        var ev = snap.Workspace.Evaluator;
        string lineRest = text[offset..LineEndOf(text, offset)];
        bool closedAfter = inTag ? lineRest.Contains("%}") : lineRest.Contains("}}");

        if (inTag)
        {
            var head = Regex.Match(inside, @"^\s*(?<name>\w*)$");
            if (head.Success)
            {
                // the name of a tag
                string typed = head.Groups["name"].Value;
                var names = new SortedSet<string>(BuiltinTags.Where(t => !t.StartsWith("end", StringComparison.Ordinal)), StringComparer.Ordinal);
                foreach (var l in a.Parsed.Tags.Where(t => t.Name == "load"))
                    foreach (var lib in DjangoTemplateParser.SplitArguments(l.Args, l.ArgsStart).Select(x => x.Text))
                    {
                        if (KnownLibraries.TryGetValue(lib, out var known)) foreach (var t in known.Where(t => !t.StartsWith("end", StringComparison.Ordinal))) names.Add(t);
                        foreach (var pl in TagLibrariesOf(snap).Where(x => x.Name == lib)) foreach (var t in pl.Tags.Keys) names.Add(t);
                    }
                foreach (var n in names.Where(n => n.StartsWith(typed, StringComparison.Ordinal)))
                {
                    bool snippet = !closedAfter && TagSnippets.ContainsKey(n);
                    items.Add(new CompletionItem(n, CompletionKind.Keyword, TagDocs.GetValueOrDefault(n), "1" + n, snippet ? TagSnippets[n] : n, snippet));
                }
                // the end of an open block
                var open = a.Parsed.Tags.Where(t => t.End <= offset && DjangoTemplateParser.Closers.ContainsKey(t.Name)).ToList();
                int depth = 0;
                foreach (var t in a.Parsed.Tags.Where(t => t.End <= offset).Reverse())
                {
                    if (DjangoTemplateParser.Closers.ContainsValue(t.Name)) depth++;
                    else if (DjangoTemplateParser.Closers.TryGetValue(t.Name, out var closer))
                    {
                        if (depth > 0) { depth--; continue; }
                        if (closer.StartsWith(typed, StringComparison.Ordinal)) items.Add(new CompletionItem(closer, CompletionKind.Keyword, $"closes {{% {t.Name} %}}", "0" + closer));
                        break;
                    }
                }
                return items;
            }

            var tagName = Regex.Match(inside, @"^\s*(?<name>\w+)\s+(?<rest>.*)$", RegexOptions.Singleline);
            if (tagName.Success)
            {
                string name = tagName.Groups["name"].Value, rest = tagName.Groups["rest"].Value;
                var quote = Regex.Match(rest, @"(?:^|\s)[""'](?<typed>[^""']*)$");
                if (quote.Success)
                {
                    string typed = quote.Groups["typed"].Value;
                    if (name == "url" && Regex.IsMatch(rest, @"^[""'][^""']*$"))
                        foreach (var u in DjangoIndexOf(snap).Urls.Select(u => u.Qualified).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(u => u.StartsWith(typed, StringComparison.Ordinal)))
                            items.Add(new CompletionItem(u, CompletionKind.Constant, "URL name", "1" + u));
                    else if (name is "extends" or "include")
                        foreach (var t in Templates().Select(t => t.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(t => t.StartsWith(typed, StringComparison.Ordinal)))
                            items.Add(new CompletionItem(t, CompletionKind.Module, "template", "1" + t));
                    else if (name == "static")
                        foreach (var f in FilesBelowFolder("static").Select(f => f.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(f => f.StartsWith(typed, StringComparison.Ordinal)).Take(300))
                            items.Add(new CompletionItem(f, CompletionKind.Module, "static file", "1" + f));
                    return items;
                }
                if (name == "load")
                {
                    string typed = Regex.Match(rest, @"\w*$").Value;
                    foreach (var lib in KnownLibraries.Keys.Concat(TagLibrariesOf(snap).Select(l => l.Name)).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(l => l.StartsWith(typed, StringComparison.Ordinal)))
                        items.Add(new CompletionItem(lib, CompletionKind.Module, "tag library", "1" + lib));
                    return items;
                }
            }
            if (tagName.Success && tagName.Groups["name"].Value is "if" or "elif" or "for" or "with" or "firstof" or "url" or "include" or "regroup" or "cycle" or "widthratio" or "filter")
                return ExpressionCompletion(a, tagName.Groups["rest"].Value, offset);
            return items;
        }
        return ExpressionCompletion(a, inside, offset);
    }

    IReadOnlyList<CompletionItem> ExpressionCompletion(TemplateAnalysis a, string expression, int offset)
    {
        var items = new List<CompletionItem>();
        var snap = a.Snap;
        var ev = snap.Workspace.Evaluator;

        // after a pipe: filters
        var pipe = Regex.Match(expression, @"\|\s*(?<typed>\w*)$");
        if (pipe.Success)
        {
            string typed = pipe.Groups["typed"].Value;
            var all = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var kv in FilterDocs) all[kv.Key] = kv.Value;
            foreach (var l in a.Parsed.Tags.Where(t => t.Name == "load"))
                foreach (var lib in DjangoTemplateParser.SplitArguments(l.Args, l.ArgsStart).Select(x => x.Text))
                {
                    if (KnownLibraries.ContainsKey(lib)) foreach (var f in LibraryFilters) all.TryAdd(f, null);
                    foreach (var pl in TagLibrariesOf(snap).Where(x => x.Name == lib)) foreach (var f in pl.Filters.Keys) all.TryAdd(f, $"{lib} library");
                }
            foreach (var (f, doc) in all.OrderBy(k => k.Key, StringComparer.Ordinal).Where(k => k.Key.StartsWith(typed, StringComparison.Ordinal)))
                items.Add(new CompletionItem(f, CompletionKind.Function, "filter", "1" + f, Documentation: doc));
            return items;
        }

        var word = Regex.Match(expression, @"(?<chain>[A-Za-z_]\w*(?:\.\w*)*|\.)$|(?<chain2>[A-Za-z_]\w*(?:\.\w+)*\.)$");
        string? tail = Regex.Match(expression, @"[A-Za-z_][\w]*(?:\.\w*)*$").Value;
        if (Regex.IsMatch(expression, @"[""'][^""']*$")) return items;   // inside a string
        var scope = ScopeAt(a, offset);
        if (string.IsNullOrEmpty(tail) || !tail.Contains('.'))
        {
            string typed = tail ?? "";
            foreach (var v in scope.All().GroupBy(v => v.Name).Select(g => g.First()).Where(v => v.Name.StartsWith(typed, StringComparison.Ordinal)))
                items.Add(new CompletionItem(v.Name, CompletionKind.Variable, v.Type?.Display() ?? v.Origin, (v.Origin == "context" ? "0" : "1") + v.Name));
            if (typed.Length == 0 || "not".StartsWith(typed)) { /* operators are not offered */ }
            return items;
        }
        var chain = tail.Split('.');
        PyType? t = scope.Find(chain[0])?.Type;
        for (int i = 1; i < chain.Length - 1 && t != null; i++) t = TemplateStep(ev, t, chain[i]);
        if (t == null) return items;
        string prefix = chain[^1];
        bool model = t is ClassType { IsInstance: true } mc && ev.IsDjangoModel(mc.Info);
        foreach (var member in ev.MemberNames(t))
        {
            if (member.Name.StartsWith('_') || !member.Name.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (model && member.Name is "objects" or "DoesNotExist" or "MultipleObjectsReturned" or "save" or "delete" or "refresh_from_db" or "full_clean" or "clean" or "clean_fields" or "validate_unique" or "from_db" or "check" or "serializable_value" or "get_deferred_fields") continue;
            var mt = TemplateStep(ev, t, member.Name);
            items.Add(new CompletionItem(member.Name, CompletionKind.Property, mt?.Display() ?? member.Owner, "0" + member.Name));
        }
        return items;
    }

    // ------------------------------------------------------------ url names in templates: usages and rename

    /// <summary>`{% url 'name' %}` in every template file (the open buffers win over the files): file, URI, name span and the name.</summary>
    IEnumerable<(string Uri, string Text, int Start, int End, string Value)> TemplateUrlUses()
    {
        foreach (var (uri, text) in TemplateTexts())
        {
            if (text.IndexOf("url", StringComparison.Ordinal) < 0) continue;
            foreach (var tag in DjangoTemplateParser.Parse(text).Tags.Where(t => t.Name == "url"))
            {
                var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
                if (args.Count == 0 || DjangoTemplateParser.Unquote(args[0].Text) is not { } name) continue;
                yield return (uri, text, args[0].Offset + 1, args[0].Offset + args[0].Text.Length - 1, name);
            }
        }
    }
}
