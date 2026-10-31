using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// Jinja templates (Flask, Starlette / FastAPI, `.jinja` files): Jinja's own tags (`set`, `macro`, `call`, `import`, `raw`), its filters and tests and the ones the
/// project registers, the names a view hands over with `render_template`, `url_for` in a template, and the mistakes Jinja refuses (Django tags, `|filter:arg`).
/// </summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ which templates are Jinja

    static readonly Regex DjangoImport = new(@"^[ \t]*(?:from|import)[ \t]+django\b", RegexOptions.Multiline | RegexOptions.Compiled);
    static readonly Regex JinjaUser = new(@"^[ \t]*(?:from|import)[ \t]+(?:flask|quart|jinja2|sanic_ext)\b|\bJinja2Templates\b", RegexOptions.Multiline | RegexOptions.Compiled);

    readonly ConditionalWeakTable<Snapshot, StrongBox<bool>> _jinjaProjects = new();

    /// <summary>A project that renders with Jinja and does not use Django.</summary>
    bool IsJinjaProject(Snapshot snap) => _jinjaProjects.GetValue(snap, s =>
    {
        bool django = false, jinja = false;
        foreach (var m in s.Project)
        {
            if (m.IsStub) continue;
            if (!django && m.Source.Contains("django", StringComparison.Ordinal) && DjangoImport.IsMatch(m.Source)) django = true;
            if (!jinja && JinjaUser.IsMatch(m.Source)) jinja = true;
        }
        return new StrongBox<bool>(jinja && !django);
    }).Value;

    /// <summary>`.jinja` / `.j2` files, templates below a `jinja2` folder (Django's Jinja2 backend), and every template of a Flask or Starlette project.</summary>
    bool IsJinjaTemplate(string uri, Snapshot snap)
    {
        if (IsJinjaFile(uri)) return true;
        string? path = PathOfUri(uri)?.Replace('\\', '/');
        if (path != null && path.Contains("/jinja2/", StringComparison.OrdinalIgnoreCase)) return true;
        return IsJinjaProject(snap);
    }

    /// <summary>Where the name of a tag starts: after `{%`, a whitespace mark and the spaces.</summary>
    static int TagNameStart(string text, TemplateTag tag)
    {
        int i = tag.Start + 2;
        if (i < tag.End && text[i] is '-' or '+') i++;
        while (i < tag.End && char.IsWhiteSpace(text[i])) i++;
        return i;
    }

    // ------------------------------------------------------------ what a view hands over

    const string UnknownContextName = "*";

    /// <summary>A context the editor cannot read (`**build()`, a variable it cannot follow): no name may be called missing then.</summary>
    static ContextEntry UnknownContext(ModuleInfo module, CallExpr call) => new(UnknownContextName, module, call.Start, call.Start, null, null, "unknown");

    /// <summary>`render_template("x.html", title=..., **context)`: the keywords and the keys of the dicts spread into the call.</summary>
    void FlaskRenderContext(ModuleInfo module, CallExpr call, List<ContextEntry> into)
    {
        foreach (var a in call.Args)
        {
            if (a.Name != null)
            {
                if (a.Name != "template_name_or_list") into.Add(new ContextEntry(a.Name, module, a.Start, a.Start + a.Name.Length, a.Value, null, "view"));
            }
            else if (a.Stars == 2)
            {
                if (a.Value is DictExpr or NameExpr or CallExpr { Func: NameExpr { Name: "dict" } }) CollectDictEntries(module, a.Value, call, "view", into);
                if (a.Value is not (DictExpr or CallExpr { Func: NameExpr { Name: "dict" } }) && !TraceableDict(module, a.Value, call)) into.Add(UnknownContext(module, call));
            }
        }
    }

    /// <summary>True when a dict held in a variable is built where the editor can read it: `ctx = {...}` or `ctx = dict(...)` in the same function.</summary>
    static bool TraceableDict(ModuleInfo module, Expr value, Node at)
    {
        if (value is not NameExpr name) return false;
        Node? scope = module.ParentOf(at);
        while (scope != null && scope is not FunctionDef && scope is not Module) scope = module.ParentOf(scope);
        scope ??= module.Ast;
        bool literal = false;
        foreach (var n in AstChildren.Descendants(scope))
        {
            if (n is AssignStmt { Targets.Count: 1 } assign && assign.Targets[0] is NameExpr t && t.Name == name.Name)
            {
                if (assign.Value is DictExpr or CallExpr { Func: NameExpr { Name: "dict" } }) literal = true;
                else return false;
            }
            if (n is CallExpr { Func: AttributeExpr { Attr: "update", Value: NameExpr h } } upd && h.Name == name.Name && upd.Args.Any(x => x.Name == null && x.Value is not (DictExpr or CallExpr { Func: NameExpr { Name: "dict" } })))
                return false;
        }
        return literal;
    }

    // ------------------------------------------------------------ what Jinja knows

    static readonly HashSet<string> JinjaTags = new(StringComparer.Ordinal)
    {
        "if", "elif", "else", "endif", "for", "endfor", "block", "endblock", "extends", "include", "import", "from", "macro", "endmacro", "call", "endcall",
        "filter", "endfilter", "set", "endset", "with", "endwith", "autoescape", "endautoescape", "raw", "endraw",
    };

    /// <summary>Tags of Jinja's own extensions, which an environment has only when they are enabled, and the text in the project that says they are.</summary>
    static readonly (string[] Tags, string Extension, string[] Evidence)[] ExtensionTags =
    {
        (new[] { "break", "continue" }, "jinja2.ext.loopcontrols", new[] { "loopcontrols", "LoopControlExtension" }),
        (new[] { "do" }, "jinja2.ext.do", new[] { "jinja2.ext.do", "ext.do'", "ext.do\"", "ExprStmtExtension" }),
        (new[] { "trans", "pluralize", "endtrans" }, "jinja2.ext.i18n", new[] { "jinja2.ext.i18n", "ext.i18n", "InternationalizationExtension", "flask_babel", "install_gettext" }),
        (new[] { "debug" }, "jinja2.ext.debug", new[] { "jinja2.ext.debug", "DebugExtension" }),
    };

    /// <summary>Django's tags, which Jinja refuses ("Encountered unknown tag"), and what to write instead.</summary>
    static readonly Dictionary<string, string> DjangoOnlyTags = new(StringComparer.Ordinal)
    {
        ["load"] = "Jinja has no {% load %}: filters and globals are registered on the environment",
        ["url"] = "write {{ url_for('name') }}",
        ["static"] = "write {{ url_for('static', filename='...') }}",
        ["csrf_token"] = "write {{ csrf_token() }} (Flask-WTF)",
        ["comment"] = "write {# ... #}", ["endcomment"] = "write {# ... #}",
        ["verbatim"] = "write {% raw %} ... {% endraw %}", ["endverbatim"] = "write {% raw %} ... {% endraw %}",
        ["empty"] = "write {% else %} inside the for loop",
        ["cycle"] = "write {{ loop.cycle('a', 'b') }}",
        ["firstof"] = "write {{ a or b }}",
        ["ifchanged"] = "write {% if loop.changed(value) %}", ["endifchanged"] = "write {% if loop.changed(value) %}",
        ["now"] = "pass the time from the view",
        ["spaceless"] = "use whitespace control: {%- ... -%}", ["endspaceless"] = "use whitespace control: {%- ... -%}",
        ["regroup"] = "use the groupby filter",
        ["lorem"] = "write {{ lipsum() }}",
        ["widthratio"] = "compute it in the expression",
        ["templatetag"] = "write {{ '{%' }}",
        ["blocktrans"] = "write {% trans %} (jinja2.ext.i18n)", ["blocktranslate"] = "write {% trans %} (jinja2.ext.i18n)", ["translate"] = "write {{ _('text') }}",
    };

    static readonly Dictionary<string, string> JinjaTagDocs = new(StringComparer.Ordinal)
    {
        ["if"] = "Evaluates a condition: `{% if a %}` ... `{% elif b %}` ... `{% else %}` ... `{% endif %}`.",
        ["for"] = "Loops over a sequence: `{% for x in items if x.shown %}`. `loop` gives index, first, last, length; `{% else %}` runs when nothing was looped.",
        ["block"] = "Defines a block child templates can override. `{{ super() }}` renders the parent's.", ["extends"] = "Makes this template a child of another.",
        ["include"] = "Renders another template here, with the current context: `{% include 'x.html' ignore missing %}`.",
        ["import"] = "Imports the macros of a template as a module: `{% import 'forms.html' as forms %}`.", ["from"] = "Imports macros by name: `{% from 'forms.html' import input, textarea %}`.",
        ["macro"] = "Defines a reusable piece of template: `{% macro input(name, value='') %}` ... `{% endmacro %}`.", ["call"] = "Calls a macro with a body it renders through `caller()`.",
        ["filter"] = "Applies filters to the whole block: `{% filter upper %}`.", ["set"] = "Assigns a variable: `{% set x = 1 %}`, or a block: `{% set x %}...{% endset %}`.",
        ["with"] = "Defines names for the block: `{% with a=1, b=2 %}`.", ["autoescape"] = "Turns autoescaping on or off for the block.", ["raw"] = "Outputs the contents as they are, without template syntax.",
        ["break"] = "Leaves the loop (jinja2.ext.loopcontrols).", ["continue"] = "Skips to the next item (jinja2.ext.loopcontrols).", ["do"] = "Evaluates an expression without output (jinja2.ext.do).",
        ["trans"] = "Translates a block (jinja2.ext.i18n).",
    };

    static readonly Dictionary<string, string> JinjaTagSnippets = new(StringComparer.Ordinal)
    {
        ["if"] = "if ${1:condition} %}\n    $0\n{% endif %}", ["for"] = "for ${1:item} in ${2:items} %}\n    $0\n{% endfor %}", ["block"] = "block ${1:name} %}\n    $0\n{% endblock %}",
        ["macro"] = "macro ${1:name}(${2}) %}\n    $0\n{% endmacro %}", ["call"] = "call ${1:macro}() %}\n    $0\n{% endcall %}", ["with"] = "with ${1:name} = ${2:value} %}\n    $0\n{% endwith %}",
        ["filter"] = "filter ${1:upper} %}\n    $0\n{% endfilter %}", ["raw"] = "raw %}\n    $0\n{% endraw %}", ["autoescape"] = "autoescape ${1:false} %}\n    $0\n{% endautoescape %}",
        ["set"] = "set ${1:name} = ${2:value} %}", ["extends"] = "extends \"${1:base.html}\" %}", ["include"] = "include \"${1:partial.html}\" %}",
        ["import"] = "import \"${1:macros.html}\" as ${2:macros} %}", ["from"] = "from \"${1:macros.html}\" import ${2:name} %}",
        ["else"] = "else %}", ["elif"] = "elif ${1:condition} %}",
    };

    static readonly Dictionary<string, string> JinjaFilterDocs = new(StringComparer.Ordinal)
    {
        ["abs"] = "The absolute value.", ["attr"] = "An attribute of an object: `x|attr('name')`.", ["batch"] = "Lists of the given size.", ["capitalize"] = "First character upper, the rest lower.",
        ["center"] = "Centers in a field of the given width.", ["count"] = "The number of items (length).", ["d"] = "Same as default.", ["default"] = "A value used when the variable is undefined: `x|default('none')`.",
        ["dictsort"] = "Sorts a dict and yields (key, value) pairs.", ["e"] = "Same as escape.", ["escape"] = "Escapes &, <, >, ' and \" for HTML.", ["filesizeformat"] = "A number of bytes as 13 kB, 4.1 MB, ...",
        ["first"] = "The first item.", ["float"] = "Converts to a float.", ["forceescape"] = "Escapes even what is marked safe.", ["format"] = "printf-style formatting: `'%s - %s'|format(a, b)`.",
        ["groupby"] = "Groups a list of objects by an attribute.", ["indent"] = "Indents each line.", ["int"] = "Converts to an int.", ["items"] = "The (key, value) pairs of a mapping.",
        ["join"] = "Joins the items with a string.", ["last"] = "The last item.", ["length"] = "The number of items.", ["list"] = "Converts to a list.", ["lower"] = "Converts to lowercase.",
        ["map"] = "Applies a filter or takes an attribute of each item.", ["max"] = "The largest item.", ["min"] = "The smallest item.", ["pprint"] = "Pretty-prints (debugging).",
        ["random"] = "A random item.", ["reject"] = "Leaves out the items a test passes.", ["rejectattr"] = "Leaves out the items whose attribute passes a test.", ["replace"] = "Replaces a substring.",
        ["reverse"] = "Reverses the sequence.", ["round"] = "Rounds a number.", ["safe"] = "Marks the value as safe HTML (no escaping).", ["select"] = "The items a test passes.",
        ["selectattr"] = "The items whose attribute passes a test.", ["slice"] = "Slices into the given number of lists.", ["sort"] = "Sorts: `items|sort(attribute='name')`.", ["string"] = "Converts to a string.",
        ["striptags"] = "Strips HTML tags.", ["sum"] = "The sum of the items.", ["title"] = "Title-cases the value.", ["tojson"] = "Dumps the value as JSON, safe for HTML.", ["trim"] = "Strips whitespace.",
        ["truncate"] = "Truncates to the given length.", ["unique"] = "The unique items.", ["upper"] = "Converts to uppercase.", ["urlencode"] = "Escapes for use in a URL.", ["urlize"] = "Turns URLs into links.",
        ["wordcount"] = "The number of words.", ["wordwrap"] = "Wraps at the given width.", ["xmlattr"] = "Builds an HTML/XML attribute string from a dict.",
    };

    static readonly HashSet<string> JinjaTests = new(StringComparer.Ordinal)
    {
        "boolean", "callable", "defined", "divisibleby", "eq", "equalto", "escaped", "even", "false", "filter", "float", "ge", "greaterthan", "gt", "in", "integer", "iterable",
        "le", "lessthan", "lower", "lt", "mapping", "ne", "none", "number", "odd", "sameas", "sequence", "string", "test", "true", "undefined", "upper",
    };

    static readonly Dictionary<string, string> JinjaGlobalDocs = new(StringComparer.Ordinal)
    {
        ["range"] = "range(start, stop, step), as in Python", ["dict"] = "dict(a=1) makes a dict", ["lipsum"] = "lipsum(n=5) gives placeholder text", ["cycler"] = "cycler('odd', 'even') cycles among values",
        ["joiner"] = "joiner(', ') gives the separator from the second call on", ["namespace"] = "namespace(x=0) holds values a {% set ns.x = ... %} can change in a loop",
        ["request"] = "the request being answered", ["session"] = "the session of the user", ["g"] = "flask.g, the data of this request", ["config"] = "the configuration of the app",
        ["url_for"] = "url_for('endpoint', **values) builds the URL of a route", ["get_flashed_messages"] = "the messages given to flash()", ["self"] = "the blocks of this template: self.title()",
        ["current_user"] = "the user signed in (Flask-Login)", ["csrf_token"] = "csrf_token() gives the token of a form (Flask-WTF)", ["_"] = "translates a string (gettext)", ["gettext"] = "translates a string",
        ["ngettext"] = "translates a string with a plural",
    };

    static readonly string[] JinjaBuiltinGlobals = { "range", "dict", "lipsum", "cycler", "joiner", "namespace" };
    static readonly string[] FlaskGlobals = { "request", "session", "g", "config", "url_for", "get_flashed_messages" };

    static readonly HashSet<string> JinjaKeywords = new(StringComparer.Ordinal)
    {
        "and", "or", "not", "in", "is", "if", "else", "true", "false", "none", "True", "False", "None", "recursive", "with", "without", "context", "import", "as", "ignore", "missing", "scoped", "required",
    };

    /// <summary>Flask extensions known not to add filters, tests or globals, and those whose additions are known.</summary>
    static readonly HashSet<string> QuietFlaskExtensions = new(StringComparer.Ordinal)
    {
        "flask_sqlalchemy", "flask_migrate", "flask_cors", "flask_restful", "flask_restx", "flask_marshmallow", "flask_caching", "flask_limiter", "flask_mail", "flask_jwt_extended",
        "flask_session", "flask_socketio", "flask_bcrypt", "flask_login", "flask_wtf", "flask_babel", "flask_compress", "flask_talisman", "flask_admin", "flask_debugtoolbar", "flask_testing",
    };

    // ------------------------------------------------------------ what the project registers on the environment

    sealed class JinjaEnvironment
    {
        public Dictionary<string, (ModuleInfo Module, int Start, int End)?> Filters { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (ModuleInfo Module, int Start, int End)?> Tests { get; } = new(StringComparer.Ordinal);
        public List<ContextEntry> Globals { get; } = new();
        public HashSet<string> CustomTags { get; } = new(StringComparer.Ordinal);
        public HashSet<string> EnabledExtensions { get; } = new(StringComparer.Ordinal);
        public bool FiltersOpaque, GlobalsOpaque, TagsOpaque, Flask, Starlette;
        /// <summary>`line_statement_prefix` and `line_comment_prefix` of the environment (`app.jinja_env.line_statement_prefix = "#"`, `Environment(line_comment_prefix="##")`).</summary>
        public string? LineStatement, LineComment;
    }

    readonly ConditionalWeakTable<Snapshot, JinjaEnvironment> _jinjaEnvironments = new();

    static readonly Regex FlaskExtensionImport = new(@"^[ \t]*(?:from|import)[ \t]+(?<m>flask_\w+)", RegexOptions.Multiline | RegexOptions.Compiled);

    JinjaEnvironment JinjaEnvironmentOf(Snapshot snap) => _jinjaEnvironments.GetValue(snap, s =>
    {
        var env = new JinjaEnvironment();
        var ev = s.Workspace.Evaluator;
        foreach (var m in s.Project)
        {
            if (m.IsStub) continue;
            string src = m.Source;
            if (Regex.IsMatch(src, @"^[ \t]*(?:from|import)[ \t]+(?:flask|quart)\b", RegexOptions.Multiline)) env.Flask = true;
            if (src.Contains("Jinja2Templates", StringComparison.Ordinal)) env.Starlette = true;
            if (src.Contains("line_", StringComparison.Ordinal))
            {
                if (Regex.Match(src, @"line_statement_prefix['""]?\s*[=:]\s*['""](?<p>[^'""\n]+)['""]") is { Success: true } ls) env.LineStatement = ls.Groups["p"].Value;
                if (Regex.Match(src, @"line_comment_prefix['""]?\s*[=:]\s*['""](?<p>[^'""\n]+)['""]") is { Success: true } lc) env.LineComment = lc.Groups["p"].Value;
            }
            foreach (var (tags, ext, evidence) in ExtensionTags)
                if (evidence.Any(e => src.Contains(e, StringComparison.Ordinal))) env.EnabledExtensions.Add(ext);
            foreach (Match fx in FlaskExtensionImport.Matches(src))
            {
                string name = fx.Groups["m"].Value;
                if (!QuietFlaskExtensions.Contains(name)) { env.FiltersOpaque = true; env.GlobalsOpaque = true; }
                void Global(string g) => env.Globals.Add(new ContextEntry(g, m, fx.Index, fx.Index, null, null, name));
                if (name == "flask_login") Global("current_user");
                if (name == "flask_wtf") Global("csrf_token");
                if (name == "flask_babel")
                {
                    foreach (var g in new[] { "_", "gettext", "ngettext", "pgettext", "npgettext", "lazy_gettext" }) Global(g);
                    foreach (var f in new[] { "datetimeformat", "dateformat", "timeformat", "timedeltaformat", "numberformat", "decimalformat", "currencyformat", "percentformat", "scientificformat" }) env.Filters[f] = null;
                }
            }
            if (env.EnabledExtensions.Contains("jinja2.ext.i18n")) foreach (var g in new[] { "_", "gettext", "ngettext" }) env.Globals.Add(new ContextEntry(g, m, 0, 0, null, null, "jinja2.ext.i18n"));
            if (!src.Contains("filter", StringComparison.Ordinal) && !src.Contains("global", StringComparison.Ordinal) && !src.Contains("test", StringComparison.Ordinal)
                && !src.Contains("context_processor", StringComparison.Ordinal) && !src.Contains("Extension", StringComparison.Ordinal)) continue;

            foreach (var n in AstChildren.Descendants(m.Ast))
            {
                switch (n)
                {
                    // @app.template_filter("name"), @app.template_global(), @app.context_processor
                    case FunctionDef def:
                        foreach (var d in def.Decorators)
                        {
                            var callee = d is CallExpr c ? c.Func : d;
                            if (callee is not AttributeExpr { Attr: var attr }) continue;
                            string registered = d is CallExpr call ? StringKeyword(call, "name") ?? FixtureModel.StringValue(call.Args.FirstOrDefault(x => x.Name == null)?.Value) ?? def.Name : def.Name;
                            (ModuleInfo, int, int)? at = (m, def.NameStart, def.NameStart + def.Name.Length);
                            switch (attr)
                            {
                                case "template_filter" or "app_template_filter": env.Filters[registered] = at; break;
                                case "template_test" or "app_template_test": env.Tests[registered] = at; break;
                                case "template_global" or "app_template_global": env.Globals.Add(new ContextEntry(registered, m, def.NameStart, def.NameStart + def.Name.Length, null, null, "@" + attr)); break;
                                case "context_processor" or "app_context_processor": ContextProcessor(m, def, env); break;
                            }
                        }
                        break;
                    // app.add_template_filter(f, "name"), env.filters["name"] = f, env.globals.update(x=...)
                    case CallExpr call when call.Func is AttributeExpr { Attr: var attr } fa:
                    {
                        var positional = call.Args.Where(a => a.Name == null && a.Stars == 0).ToList();
                        string? NameOf() => StringKeyword(call, "name") ?? (positional.Count > 1 ? FixtureModel.StringValue(positional[1].Value) : null) ?? (positional.FirstOrDefault()?.Value is NameExpr f ? f.Name : null);
                        (ModuleInfo, int, int)? Here() => (m, call.Start, call.End);
                        switch (attr)
                        {
                            case "add_template_filter" or "add_app_template_filter" when NameOf() is { } fname: env.Filters[fname] = Here(); break;
                            case "add_template_test" or "add_app_template_test" when NameOf() is { } tname: env.Tests[tname] = Here(); break;
                            case "add_template_global" or "add_app_template_global" when NameOf() is { } gname: env.Globals.Add(new ContextEntry(gname, m, call.Start, call.End, positional.FirstOrDefault()?.Value, null, attr)); break;
                            case "update" when fa.Value is AttributeExpr { Attr: "filters" or "tests" or "globals" } bucket:
                                foreach (var a in call.Args)
                                {
                                    if (a.Name != null) Register(bucket.Attr, a.Name, a.Value, a.Start, a.Start + a.Name.Length);
                                    else if (a.Value is DictExpr dict)
                                        for (int i = 0; i < dict.Keys.Count; i++)
                                            if (FixtureModel.StringValue(dict.Keys[i]) is { } key) Register(bucket.Attr, key, dict.Values[i], dict.Keys[i]!.Start, dict.Keys[i]!.End);
                                            else Opaque(bucket.Attr);
                                    else Opaque(bucket.Attr);
                                }
                                break;
                            case "add_extension" when positional.FirstOrDefault()?.Value is { } extArg:
                                if (FixtureModel.StringValue(extArg) is not { } extName || !ExtensionTags.Any(x => x.Extension == extName)) env.TagsOpaque = true;
                                break;
                        }
                        break;
                    }
                    case AssignStmt { Targets.Count: 1 } assign when assign.Targets[0] is SubscriptExpr { Value: AttributeExpr { Attr: "filters" or "tests" or "globals" } bucket, Index: var key }:
                        if (FixtureModel.StringValue(key) is { } k) Register(bucket.Attr, k, assign.Value, key.Start, key.End); else Opaque(bucket.Attr);
                        break;
                    // a class of the project that adds tags: class CacheExtension(Extension): tags = {"cache"}
                    case ClassDef cls when cls.Bases.Any(b => ReceiverName(b.Value)?.EndsWith("Extension", StringComparison.Ordinal) == true):
                    {
                        var tagsAssign = cls.Body.OfType<AssignStmt>().FirstOrDefault(a => a.Targets.Count == 1 && a.Targets[0] is NameExpr { Name: "tags" });
                        var items = tagsAssign?.Value switch { SetExpr se => se.Items, ListExpr le => le.Items, TupleExpr te => te.Items, _ => null };
                        if (items == null) { env.TagsOpaque = true; break; }
                        foreach (var item in items)
                            if (FixtureModel.StringValue(item) is { } tag) env.CustomTags.Add(tag); else env.TagsOpaque = true;
                        break;
                    }
                }
            }
            foreach (Match ex in Regex.Matches(src, @"extensions\s*=\s*\[(?<list>[^\]]*)\]"))
                foreach (Match item in Regex.Matches(ex.Groups["list"].Value, @"[""'](?<e>[\w.]+)[""']|(?<n>\b[A-Za-z_]\w*\b)"))
                {
                    if (item.Groups["e"].Success && ExtensionTags.Any(x => x.Extension == item.Groups["e"].Value)) continue;
                    if (item.Groups["n"].Success && ExtensionTags.Any(x => x.Evidence.Contains(item.Groups["n"].Value))) continue;
                    env.TagsOpaque = true;
                }

            void Register(string bucket, string name, Expr? value, int start, int end)
            {
                (ModuleInfo, int, int)? at = (m, start, end);
                if (bucket == "filters") env.Filters[name] = at;
                else if (bucket == "tests") env.Tests[name] = at;
                else env.Globals.Add(new ContextEntry(name, m, start, end, value, null, "environment globals"));
            }
            void Opaque(string bucket)
            {
                if (bucket == "globals") env.GlobalsOpaque = true; else env.FiltersOpaque = true;
            }
        }
        return env;
    });

    /// <summary>`@app.context_processor` returning `{"now": ...}` or `dict(now=...)`: its keys are in every template.</summary>
    void ContextProcessor(ModuleInfo m, FunctionDef def, JinjaEnvironment env)
    {
        var returns = AstChildren.Descendants(def).OfType<ReturnStmt>().Where(r => OwnerFunction(m, r) == def).ToList();
        if (returns.Count == 0) { env.GlobalsOpaque = true; return; }
        foreach (var r in returns)
        {
            var found = new List<ContextEntry>();
            if (r.Value is DictExpr or CallExpr { Func: NameExpr { Name: "dict" } } || r.Value is NameExpr && TraceableDict(m, r.Value, r)) CollectDictEntries(m, r.Value!, r, "@context_processor " + def.Name, found);
            else { env.GlobalsOpaque = true; continue; }
            env.Globals.AddRange(found);
        }
    }

    // ------------------------------------------------------------ names in a template

    /// <summary>Splits at a separator outside strings and brackets: `a=1, b=f(2, 3)` gives `a=1` and ` b=f(2, 3)`, with the offset of each part.</summary>
    static List<(string Text, int Offset)> SplitTopLevel(string text, char separator)
    {
        var parts = new List<(string, int)>();
        int depth = 0, start = 0;
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == separator && depth == 0) { parts.Add((text[start..i], start)); start = i + 1; }
        }
        parts.Add((text[start..], start));
        return parts;
    }

    static (string Text, int Offset) Trimmed((string Text, int Offset) part)
    {
        int lead = part.Text.Length - part.Text.TrimStart().Length;
        return (part.Text.Trim(), part.Offset + lead);
    }

    static readonly Regex JinjaFor = new(@"^(?<vars>[\w\s,()]+?)\s+in\s+(?<iter>.+?)(?:\s+if\s+(?<cond>.+?))?(?:\s+recursive)?$", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JinjaSet = new(@"^(?<targets>[\w\s,().]+?)\s*=\s*(?<expr>.+)$", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JinjaMacro = new(@"^(?<name>\w+)\s*\((?<params>.*)\)\s*$", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JinjaCall = new(@"^(?:\((?<params>[^)]*)\)\s*)?(?<expr>.+)$", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JinjaImport = new(@"^(?<tpl>.+?)\s+as\s+(?<name>\w+)(?:\s+(?:with|without)\s+context)?$", RegexOptions.Singleline | RegexOptions.Compiled);
    static readonly Regex JinjaFrom = new(@"^(?<tpl>.+?)\s+import\s+(?<names>.+?)(?:\s+(?:with|without)\s+context)?$", RegexOptions.Singleline | RegexOptions.Compiled);

    /// <summary>The names a tag defines, with where each is written: `set`, `for`, `with`, `macro` and its parameters, `call`, `import`, `from ... import`.</summary>
    static List<(string Name, int Start, string Origin, string? Value)> NamesDefinedBy(TemplateTag tag)
    {
        var names = new List<(string, int, string, string?)>();
        string args = tag.Args;
        int at = tag.ArgsStart;
        switch (tag.Name)
        {
            case "set":
            {
                var m = JinjaSet.Match(args);
                string targets = m.Success ? m.Groups["targets"].Value : args.Split('|')[0];
                string? value = m.Success ? m.Groups["expr"].Value : null;
                var parts = SplitTopLevel(targets, ',');
                foreach (var p in parts.Select(Trimmed))
                {
                    string name = p.Text.Trim('(', ')', ' ');
                    if (Regex.IsMatch(name, @"^[A-Za-z_]\w*$")) names.Add((name, at + p.Offset + p.Text.IndexOf(name, StringComparison.Ordinal), "set", parts.Count == 1 ? value : null));
                }
                break;
            }
            case "for":
            {
                var m = JinjaFor.Match(args);
                if (!m.Success) break;
                var vars = m.Groups["vars"];
                foreach (Match v in Regex.Matches(vars.Value, @"[A-Za-z_]\w*")) names.Add((v.Value, at + vars.Index + v.Index, "for", null));
                break;
            }
            case "with":
                foreach (var p in SplitTopLevel(args, ',').Select(Trimmed))
                {
                    int eq = p.Text.IndexOf('=');
                    if (eq > 0 && Regex.IsMatch(p.Text[..eq].Trim(), @"^[A-Za-z_]\w*$")) names.Add((p.Text[..eq].Trim(), at + p.Offset, "with", p.Text[(eq + 1)..]));
                }
                break;
            case "macro":
            {
                var m = JinjaMacro.Match(args);
                if (!m.Success) break;
                names.Add((m.Groups["name"].Value, at + m.Groups["name"].Index, "macro", null));
                foreach (var p in ParamsOf(m.Groups["params"].Value, at + m.Groups["params"].Index)) names.Add((p.Name, p.Start, "param", null));
                break;
            }
            case "call":
            {
                var m = JinjaCall.Match(args);
                if (m.Success && m.Groups["params"].Success)
                    foreach (var p in ParamsOf(m.Groups["params"].Value, at + m.Groups["params"].Index)) names.Add((p.Name, p.Start, "param", null));
                break;
            }
            case "import":
            {
                var m = JinjaImport.Match(args);
                if (m.Success) names.Add((m.Groups["name"].Value, at + m.Groups["name"].Index, "import", null));
                break;
            }
            case "from":
            {
                var m = JinjaFrom.Match(args);
                if (!m.Success) break;
                var list = m.Groups["names"];
                foreach (var p in SplitTopLevel(list.Value, ',').Select(Trimmed))
                {
                    var alias = Regex.Match(p.Text, @"^(?<src>\w+)(?:\s+as\s+(?<as>\w+))?$");
                    if (!alias.Success) continue;
                    var g = alias.Groups["as"].Success ? alias.Groups["as"] : alias.Groups["src"];
                    names.Add((g.Value, at + list.Index + p.Offset + g.Index, "from", alias.Groups["src"].Value));
                }
                break;
            }
        }
        return names;
    }

    static List<(string Name, int Start)> ParamsOf(string text, int offset)
    {
        var list = new List<(string, int)>();
        foreach (var p in SplitTopLevel(text, ',').Select(Trimmed))
        {
            var m = Regex.Match(p.Text, @"^[A-Za-z_]\w*");
            if (m.Success) list.Add((m.Value, offset + p.Offset));
        }
        return list;
    }

    /// <summary>The names a Jinja template can use at an offset: the frames of `for`, `with`, `macro` and `call` around it, what `set`, `import` and `macro` defined before it, the context and the globals.</summary>
    TemplateScope JinjaScopeAt(TemplateAnalysis a, int offset)
    {
        var scope = new TemplateScope();
        var ev = a.Snap.Workspace.Evaluator;
        var env = JinjaEnvironmentOf(a.Snap);
        foreach (var e in a.Context.Where(e => e.Name != UnknownContextName).GroupBy(e => e.Name).Select(g => g.First()))
            scope.Frames[0].Vars.Add(new TemplateVar(e.Name, TypeOfEntry(a, e), e.Start, e.End, "context", e));
        foreach (var e in env.Globals.Where(e => scope.Find(e.Name) == null))
            scope.Frames[0].Vars.Add(new TemplateVar(e.Name, TypeOfEntry(a, e), e.Start, e.End, "context", e));
        IEnumerable<string> builtins = JinjaBuiltinGlobals.Append("self");
        if (env.Flask) builtins = builtins.Concat(FlaskGlobals);
        if (env.Starlette) builtins = builtins.Append("url_for").Append("request");
        foreach (var n in builtins.Where(n => scope.Find(n) == null))
        {
            PyType? t = n == "request" ? (env.Flask ? ev.InstanceOf("flask.wrappers", "Request") ?? ev.InstanceOf("flask", "Request") : ev.InstanceOf("starlette.requests", "Request"))
                : n == "session" ? ev.InstanceOf("flask.sessions", "SecureCookieSession") : null;
            scope.Frames[0].Vars.Add(new TemplateVar(n, t is UnknownType ? null : t, 0, 0, "builtin", null));
        }

        void Define(TemplateVar v)
        {
            // if, filter and autoescape do not make a scope: what they set is seen after them
            for (int i = scope.Frames.Count - 1; i >= 0; i--)
                if (scope.Frames[i].Closer is not ("endif" or "endfilter" or "endautoescape")) { scope.Frames[i].Vars.Add(v); return; }
        }

        foreach (var tag in a.Parsed.Tags)
        {
            if (tag.Start >= offset || offset < tag.End && tag.Start < offset) break;
            var defined = NamesDefinedBy(tag);
            switch (tag.Name)
            {
                case "for":
                {
                    var m = JinjaFor.Match(tag.Args);
                    var element = m.Success && TypeOfTemplateExpression(a, scope, m.Groups["iter"].Value) is { } it ? ev.IterElement(it) : null;
                    if (element is UnknownType) element = null;
                    var vars = defined.Select(d => new TemplateVar(d.Name, defined.Count == 1 ? element : null, d.Start, d.Start + d.Name.Length, "for", null)).ToList();
                    vars.Add(new TemplateVar("loop", null, tag.Start, tag.Start + 2, "loop", null));
                    scope.Frames.Add(("endfor", vars));
                    break;
                }
                case "with":
                    scope.Frames.Add(("endwith", defined.Select(d => new TemplateVar(d.Name, d.Value == null ? null : TypeOfTemplateExpression(a, scope, d.Value), d.Start, d.Start + d.Name.Length, "with", null)).ToList()));
                    break;
                case "macro":
                {
                    if (defined.Count == 0) break;
                    var head = defined[0];
                    Define(new TemplateVar(head.Name, null, head.Start, head.Start + head.Name.Length, "macro", null));
                    var vars = defined.Skip(1).Select(d => new TemplateVar(d.Name, null, d.Start, d.Start + d.Name.Length, "set", null)).ToList();
                    foreach (var special in new[] { "varargs", "kwargs", "caller" }) vars.Add(new TemplateVar(special, null, tag.Start, tag.Start + 2, "macro-special", null));
                    scope.Frames.Add(("endmacro", vars));
                    break;
                }
                case "call":
                    scope.Frames.Add(("endcall", defined.Select(d => new TemplateVar(d.Name, null, d.Start, d.Start + d.Name.Length, "set", null)).ToList()));
                    break;
                case "block":
                    scope.Frames.Add(("endblock", new List<TemplateVar> { new("super", null, tag.Start, tag.Start + 2, "block", null) }));
                    break;
                case "if":
                    scope.Frames.Add(("endif", new List<TemplateVar>()));
                    break;
                case "filter":
                    scope.Frames.Add(("endfilter", new List<TemplateVar>()));
                    break;
                case "autoescape":
                    scope.Frames.Add(("endautoescape", new List<TemplateVar>()));
                    break;
                case "set" or "import" or "from":
                    foreach (var d in defined)
                        Define(new TemplateVar(d.Name, d.Value == null ? null : TypeOfTemplateExpression(a, scope, d.Value), d.Start, d.Start + d.Name.Length, tag.Name == "set" ? "set" : tag.Name, null));
                    break;
                case "else" or "elif":
                    // the else of a for loop runs when nothing was looped: its names are gone
                    if (scope.Frames[^1].Closer == "endfor" && tag.Name == "else") scope.Frames[^1].Vars.Clear();
                    break;
                case "endfor" or "endwith" or "endmacro" or "endcall" or "endblock" or "endif" or "endfilter" or "endautoescape":
                {
                    int i = scope.Frames.FindLastIndex(f => f.Closer == tag.Name);
                    if (i > 0) scope.Frames.RemoveRange(i, scope.Frames.Count - i);
                    break;
                }
            }
        }
        return scope;
    }

    /// <summary>The identifiers an expression reads, with their offsets: not attributes, filters, tests, keyword argument names or Jinja's own words.</summary>
    static List<(string Name, int Offset)> NamesReadBy(string expr, int start)
    {
        var found = new List<(string, int)>();
        int i = 0;
        string prevWord = "", prevPrevWord = "";
        char prevChar = '\0';
        while (i < expr.Length)
        {
            char c = expr[i];
            if (c is '"' or '\'')
            {
                char q = c;
                i++;
                while (i < expr.Length && expr[i] != q) { if (expr[i] == '\\') i++; i++; }
                i++;
                prevChar = q; prevPrevWord = prevWord; prevWord = "";
                continue;
            }
            if (char.IsLetter(c) || c == '_')
            {
                int s = i;
                while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '_')) i++;
                string word = expr[s..i];
                int j = i;
                while (j < expr.Length && expr[j] == ' ') j++;
                bool keyword = j < expr.Length && expr[j] == '=' && (j + 1 >= expr.Length || expr[j + 1] != '=');
                bool test = prevWord == "is" || prevWord == "not" && prevPrevWord == "is";
                if (prevChar is not ('.' or '|') && !test && !keyword && !JinjaKeywords.Contains(word)) found.Add((word, start + s));
                prevPrevWord = prevWord; prevWord = word; prevChar = 'a';
                continue;
            }
            if (char.IsDigit(c)) { while (i < expr.Length && (char.IsLetterOrDigit(expr[i]) || expr[i] == '.')) i++; prevChar = '0'; prevPrevWord = prevWord; prevWord = ""; continue; }
            if (!char.IsWhiteSpace(c)) { prevChar = c; prevPrevWord = prevWord; prevWord = ""; }
            i++;
        }
        return found;
    }

    /// <summary>The parts of a tag that are expressions read at render time, with their offsets.</summary>
    static IEnumerable<(string Text, int Offset)> ExpressionsOf(TemplateTag tag)
    {
        string args = tag.Args;
        int at = tag.ArgsStart;
        switch (tag.Name)
        {
            case "if" or "elif" or "include" or "extends":
                yield return (args, at);
                break;
            case "for":
            {
                var m = JinjaFor.Match(args);
                if (!m.Success) break;
                yield return (m.Groups["iter"].Value, at + m.Groups["iter"].Index);
                if (m.Groups["cond"].Success) yield return (m.Groups["cond"].Value, at + m.Groups["cond"].Index);
                break;
            }
            case "set":
            {
                var m = JinjaSet.Match(args);
                if (m.Success) yield return (m.Groups["expr"].Value, at + m.Groups["expr"].Index);
                break;
            }
            case "with":
                foreach (var p in SplitTopLevel(args, ','))
                {
                    int eq = p.Text.IndexOf('=');
                    if (eq > 0) yield return (p.Text[(eq + 1)..], at + p.Offset + eq + 1);
                }
                break;
            case "call":
            {
                var m = JinjaCall.Match(args);
                if (m.Success) yield return (m.Groups["expr"].Value, at + m.Groups["expr"].Index);
                break;
            }
            case "do":
                yield return (args, at);
                break;
        }
    }

    // ------------------------------------------------------------ checks

    static readonly Regex UrlForStart = new(@"\burl_for\s*\(", RegexOptions.Compiled);

    /// <summary>`url_for('endpoint', a=1)` in an expression: the endpoint and its span, and the keywords with their values.</summary>
    static IEnumerable<(string Endpoint, int Start, int End, List<(string Name, string Value, int Start)> Keywords, bool Spread)> UrlForCalls(string expr, int offset)
    {
        foreach (Match m in UrlForStart.Matches(expr))
        {
            int open = m.Index + m.Length - 1;
            int depth = 0, close = -1;
            char quote = '\0';
            for (int i = open; i < expr.Length; i++)
            {
                char c = expr[i];
                if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
                if (c is '"' or '\'') quote = c;
                else if (c == '(') depth++;
                else if (c == ')' && --depth == 0) { close = i; break; }
            }
            string inner = close < 0 ? expr[(open + 1)..] : expr[(open + 1)..close];
            var parts = SplitTopLevel(inner, ',').Select(Trimmed).ToList();
            if (parts.Count == 0 || DjangoTemplateParser.Unquote(parts[0].Text) is not { } endpoint) continue;
            var keywords = new List<(string, string, int)>();
            bool spread = false;
            foreach (var p in parts.Skip(1))
            {
                if (p.Text.StartsWith("**", StringComparison.Ordinal)) { spread = true; continue; }
                var kw = Regex.Match(p.Text, @"^(?<n>\w+)\s*=(?!=)\s*(?<v>.*)$", RegexOptions.Singleline);
                if (kw.Success) keywords.Add((kw.Groups["n"].Value, kw.Groups["v"].Value.Trim(), offset + open + 1 + p.Offset + kw.Groups["v"].Index));
            }
            int s = offset + open + 1 + parts[0].Offset + 1;
            yield return (endpoint, s, s + endpoint.Length, keywords, spread);
        }
    }

    /// <summary>What Jinja cannot read in an expression: an empty one, a bracket or string left open, a closer without its opener, and Django's `|filter:argument`.</summary>
    static (string Message, int Start, int End)? JinjaSyntaxProblem(string expr, int start, bool required)
    {
        if (expr.Trim().Length == 0) return required ? ("Jinja expects an expression here", start, start) : null;
        var stack = new Stack<(char, int)>();
        char quote = '\0';
        int quoteAt = 0;
        for (int i = 0; i < expr.Length; i++)
        {
            char c = expr[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; continue; }
            if (c is '"' or '\'') { quote = c; quoteAt = i; }
            else if (c is '(' or '[' or '{') stack.Push((c, i));
            else if (c is ')' or ']' or '}')
            {
                char want = c == ')' ? '(' : c == ']' ? '[' : '{';
                if (stack.Count == 0 || stack.Peek().Item1 != want) return ($"Unexpected '{c}': nothing opened it", start + i, start + i + 1);
                stack.Pop();
            }
            else if (c == '|')
            {
                var filter = Regex.Match(expr[(i + 1)..], @"^\s*(?<f>\w+)\s*:");
                if (filter.Success && stack.Count == 0)
                    return ($"Jinja does not take Django's '|{filter.Groups["f"].Value}:argument': write {filter.Groups["f"].Value}(argument)", start + i + 1, start + i + 1 + filter.Length);
            }
        }
        if (quote != '\0') return ("The string is not closed", start + quoteAt, start + quoteAt + 1);
        if (stack.Count > 0) { var (open, at) = stack.Peek(); return ($"'{open}' is not closed", start + at, start + at + 1); }
        return null;
    }

    List<LspDiagnostic> JinjaDiagnostics(TemplateAnalysis a)
    {
        var result = new List<LspDiagnostic>();
        var lines = new LineMap(a.Text);
        var snap = a.Snap;
        var env = JinjaEnvironmentOf(snap);
        void Add(int start, int end, string message, int severity, string code) =>
            result.Add(new LspDiagnostic(lines.RangeOf(start, Math.Max(end, start + 1)), severity, message, Code: code));

        foreach (var p in a.Parsed.Problems) Add(p.Start, p.End, p.Message, Severity.Error, p.Code);

        // tags
        var custom = env.CustomTags;
        foreach (var tag in a.Parsed.Tags)
        {
            if (tag.Name.Length == 0 || JinjaTags.Contains(tag.Name) || custom.Contains(tag.Name) || tag.Name.StartsWith("end", StringComparison.Ordinal) && custom.Contains(tag.Name[3..])) continue;
            int ns = TagNameStart(a.Text, tag);
            var ext = ExtensionTags.FirstOrDefault(x => x.Tags.Contains(tag.Name));
            if (ext.Tags != null)
            {
                if (!env.EnabledExtensions.Contains(ext.Extension) && !env.TagsOpaque)
                    Add(ns, ns + tag.Name.Length, $"'{tag.Name}' needs the {ext.Extension} extension, which the environment does not load", Severity.Warning, "jinja-tag");
                continue;
            }
            if (DjangoOnlyTags.TryGetValue(tag.Name, out var instead))
                Add(ns, ns + tag.Name.Length, $"'{tag.Name}' is a Django tag that Jinja does not have: {instead}", Severity.Error, "jinja-tag");
            else
                Add(ns, ns + tag.Name.Length, $"Jinja does not know the tag '{tag.Name}'", env.TagsOpaque ? Severity.Information : Severity.Warning, "jinja-tag");
        }

        // expressions Jinja cannot read
        foreach (var v in a.Parsed.Variables)
            if ((JinjaSyntaxProblem(v.Expression, v.ExprStart, true) ?? (v.Expression.Trim().Length > 0 ? JinjaGrammarProblem(v.Expression, v.ExprStart, true) : null)) is { } p)
                Add(p.Start, p.End, p.Message, Severity.Error, "jinja-syntax");
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is not ("raw" or "endraw") && t.Args.Length > 0))
        {
            if (JinjaSyntaxProblem(tag.Args, tag.ArgsStart, false) is { } p) { Add(p.Start, p.End, p.Message, Severity.Error, "jinja-syntax"); continue; }
            // what Jinja reads as one expression, token by token: `{% if a b %}` stops at b
            foreach (var (text, at) in WholeExpressionsOf(tag))
                if (text.Trim().Length > 0 && JinjaGrammarProblem(text, at, false) is { } g) { Add(g.Start, g.End, g.Message, Severity.Error, "jinja-syntax"); break; }
        }
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "if" or "elif" or "for" or "set" or "include" or "extends" or "import" or "from" or "macro" or "call" or "filter" && t.Args.Trim().Length == 0))
            Add(tag.Start, tag.End, $"'{tag.Name}' needs {(tag.Name is "if" or "elif" ? "a condition" : "its arguments")}", Severity.Error, "jinja-syntax");

        // filters and tests
        var filters = new HashSet<string>(JinjaFilterDocs.Keys.Concat(env.Filters.Keys), StringComparer.Ordinal);
        var tests = new HashSet<string>(JinjaTests.Concat(env.Tests.Keys), StringComparer.Ordinal);
        int unknownLevel = env.FiltersOpaque ? Severity.Information : Severity.Warning;
        void CheckFiltersAndTests(string expression, int start, bool filterTag)
        {
            string masked = MaskStrings(expression);
            foreach (Match m in Regex.Matches(masked, filterTag ? @"(?:^|\|)\s*(?<f>[A-Za-z_]\w*)" : @"\|\s*(?<f>[A-Za-z_]\w*)"))
            {
                string f = m.Groups["f"].Value;
                if (!filters.Contains(f)) Add(start + m.Groups["f"].Index, start + m.Groups["f"].Index + f.Length, $"No filter named '{f}': neither Jinja nor the project registers it", unknownLevel, "jinja-filter");
            }
            foreach (Match m in Regex.Matches(masked, @"\bis\s+(?:not\s+)?(?<t>[A-Za-z_]\w*)"))
            {
                string t = m.Groups["t"].Value;
                if (!tests.Contains(t)) Add(start + m.Groups["t"].Index, start + m.Groups["t"].Index + t.Length, $"No test named '{t}': neither Jinja nor the project registers it", unknownLevel, "jinja-filter");
            }
        }
        foreach (var v in a.Parsed.Variables) CheckFiltersAndTests(v.Expression, v.ExprStart, false);
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is not ("raw" or "macro")))
            CheckFiltersAndTests(tag.Args, tag.ArgsStart, tag.Name == "filter");

        // macro calls against the macros' parameters
        CheckMacroCalls(a, Add);

        // url_for in the template
        var index = RouteIndexOf(snap);
        var statics = new Lazy<List<(string Name, string Path)>>(() => FilesBelowFolder("static"));
        foreach (var (expr, at) in AllExpressions(a))
            foreach (var call in UrlForCalls(expr, at))
            {
                if (call.Endpoint == "static" || call.Endpoint.EndsWith(".static", StringComparison.Ordinal))
                {
                    var file = call.Keywords.FirstOrDefault(k => k.Name is "filename" or "path");
                    if (file.Name != null && DjangoTemplateParser.Unquote(file.Value) is { } name && statics.Value.Count > 0 && !statics.Value.Any(f => f.Name == name))
                        Add(file.Start + 1, file.Start + 1 + name.Length, $"Static file '{name}' is not in any static folder of the project", Severity.Information, "template-static");
                    continue;
                }
                if (call.Endpoint.StartsWith('.') || index.Routes.Count == 0) continue;
                var routes = EndpointRoutes(index, call.Endpoint).ToList();
                if (routes.Count == 0) { Add(call.Start, call.End, $"No route has the endpoint name '{call.Endpoint}'", Severity.Information, "route-endpoint"); continue; }
                if (routes.Count != 1 || !routes[0].Flask || call.Spread) continue;
                var route = routes[0];
                if (route.Def.Decorators.OfType<CallExpr>().Any(d => d.Args.Any(x => x.Name == "defaults"))) continue;
                foreach (var (name, converter, _) in PathParameters(route))
                {
                    var given = call.Keywords.FirstOrDefault(k => k.Name == name);
                    if (given.Name == null)
                        Add(call.Start, call.End, $"url_for('{call.Endpoint}') needs '{name}': the route '{route.Own}' has it as a parameter and Flask cannot build the URL without it", Severity.Warning, "flask-url-for-arguments");
                    else if (converter == "int" && DjangoTemplateParser.Unquote(given.Value) is { } text && !text.All(char.IsAsciiDigit))
                        Add(given.Start, given.Start + given.Value.Length, $"'{name}' is an int in the route '{route.Own}', and {given.Value} is not a number", Severity.Warning, "flask-url-for-arguments");
                }
            }

        // templates that are not there
        bool hasTemplates = Templates().Count > 0;
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "extends" or "include" or "import" or "from"))
        {
            var first = SplitTopLevel(tag.Args, ' ').Select(Trimmed).FirstOrDefault(p => p.Text.Length > 0);
            if (first.Text == null || DjangoTemplateParser.Unquote(first.Text) is not { } quoted || !hasTemplates || tag.Args.Contains("ignore missing", StringComparison.Ordinal)) continue;
            if (!IsLibraryTemplate(quoted) && !Templates().Any(t => t.Name == quoted))
                Add(tag.ArgsStart + first.Offset + 1, tag.ArgsStart + first.Offset + 1 + quoted.Length, $"Template '{quoted}' is not in any templates folder of the project", Severity.Information, "django-template");
        }

        IncludeListProblems(a, Add);

        // blocks no parent has
        if (a.Parsed.Tags.Any(t => t.Name == "extends") && ParentChainKnown(a) == true)
            foreach (var block in a.Parsed.Tags.Where(t => t.Name == "block"))
            {
                var parts = DjangoTemplateParser.SplitArguments(block.Args, block.ArgsStart);
                if (parts.Count > 0 && ParentBlock(a, parts[0].Text) == null)
                    Add(parts[0].Offset, parts[0].Offset + parts[0].Text.Length, $"No parent template has a block '{parts[0].Text}': its content is never shown", Severity.Information, "template-block");
            }

        // attributes a class of the project does not have
        foreach (var (expr, at) in AllExpressions(a)) CheckTemplateAttributes(a, expr, at, Add);

        UndefinedNames(a, env, Add);
        return result;
    }

    /// <summary>Strings replaced by spaces of the same length, so a pattern does not match inside them.</summary>
    static string MaskStrings(string text)
    {
        var chars = text.ToCharArray();
        char quote = '\0';
        for (int i = 0; i < chars.Length; i++)
        {
            char c = chars[i];
            if (quote != '\0')
            {
                if (c == '\\' && i + 1 < chars.Length) { chars[i] = ' '; chars[++i] = ' '; continue; }
                if (c == quote) quote = '\0'; else chars[i] = ' ';
                continue;
            }
            if (c is '"' or '\'') quote = c;
        }
        return new string(chars);
    }

    /// <summary>True when the text ends inside a string that is not closed yet.</summary>
    static bool InsideString(string text)
    {
        char quote = '\0';
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quote != '\0') { if (c == '\\') i++; else if (c == quote) quote = '\0'; }
            else if (c is '"' or '\'') quote = c;
        }
        return quote != '\0';
    }

    /// <summary>Every expression of the template: the variables and the expression parts of the tags.</summary>
    static IEnumerable<(string Text, int Offset)> AllExpressions(TemplateAnalysis a) =>
        a.Parsed.Variables.Select(v => (v.Expression, v.ExprStart)).Concat(a.Parsed.Tags.SelectMany(ExpressionsOf));

    /// <summary>
    /// A name no view gives to the template and nothing in it defines: Jinja prints it as an empty string. Only for a template the views render themselves, that no other
    /// template extends, includes or imports, whose context and globals are all known; names used only as a condition, or with `is defined` / `|default`, are left alone.
    /// </summary>
    void UndefinedNames(TemplateAnalysis a, JinjaEnvironment env, Action<int, int, string, int, string> add)
    {
        if (a.Name == null || env.GlobalsOpaque || a.Context.Any(e => e.Name == UnknownContextName)) return;
        if (!RenderedDirectly(a) || UsedByOtherTemplates(a.Name)) return;
        var known = new HashSet<string>(a.Context.Select(e => e.Name).Concat(env.Globals.Select(g => g.Name)).Concat(JinjaBuiltinGlobals).Concat(new[] { "self", "super", "loop", "varargs", "kwargs", "caller" }), StringComparer.Ordinal);
        if (env.Flask) known.UnionWith(FlaskGlobals);
        if (env.Starlette) { known.Add("url_for"); known.Add("request"); }
        foreach (var tag in a.Parsed.Tags) foreach (var d in NamesDefinedBy(tag)) known.Add(d.Name);

        string masked = MaskStrings(a.Text);
        var guarded = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(masked, @"\b(?<n>[A-Za-z_]\w*)\s+is\s+(?:not\s+)?(?:defined|undefined)\b|\b(?<n>[A-Za-z_]\w*)\s*\|\s*(?:default|d)\b")) guarded.Add(m.Groups["n"].Value);
        foreach (var tag in a.Parsed.Tags.Where(t => t.Name is "if" or "elif"))
        {
            var bare = Regex.Match(tag.Args, @"^(?:not\s+)?(?<n>[A-Za-z_]\w*)$");
            if (bare.Success) guarded.Add(bare.Groups["n"].Value);
        }
        var reported = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (expr, at) in AllExpressions(a))
            foreach (var (name, offset) in NamesReadBy(expr, at))
            {
                if (known.Contains(name) || guarded.Contains(name)) continue;
                add(offset, offset + name.Length, reported.Add(name)
                    ? $"'{name}' is not given to '{a.Name}' by any view that renders it, and nothing in the template defines it: Jinja prints it as an empty string"
                    : $"'{name}' is not defined", Severity.Information, "jinja-undefined");
            }
    }

    bool RenderedDirectly(TemplateAnalysis a) =>
        a.Name != null && a.Snap.Project.Any(m => !m.IsStub && m.Source.Contains(a.Name, StringComparison.Ordinal)
            && NamedStringsIn(m, null).Any(s => s.Kind == DjangoNamed.Template && s.Value == a.Name && s.Call != null && CalleeName(s.Call) is "render_template" or "stream_template" or "TemplateResponse" or "get_template"));

    bool UsedByOtherTemplates(string name)
    {
        foreach (var t in Templates())
        {
            if (t.Name == name) continue;
            string? text = TemplateTextOf(t.Path);
            if (text != null && (text.Contains("\"" + name + "\"", StringComparison.Ordinal) || text.Contains("'" + name + "'", StringComparison.Ordinal))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ hover and go to definition

    TemplateTarget? JinjaTagTarget(TemplateAnalysis a, TemplateTag tag, int nameStart, int offset)
    {
        string Code(string s) => "```python\n" + s + "\n```";
        if (offset >= nameStart && offset <= nameStart + tag.Name.Length)
        {
            string? doc = JinjaTagDocs.GetValueOrDefault(tag.Name) ?? (DjangoOnlyTags.TryGetValue(tag.Name, out var instead) ? "A Django tag: " + instead + "." : null);
            return doc == null ? null : new TemplateTarget(Code("{% " + tag.Name + " %}") + "\n\n" + doc, nameStart, nameStart + tag.Name.Length, new());
        }
        if (tag.Name is "extends" or "include" or "import" or "from")
        {
            // any template named in it: the one of `{% include 'x.html' %}`, each of `{% include ['a.html', 'b.html'] %}`
            foreach (Match q in QuotedTemplate.Matches(tag.Args))
            {
                int qs = tag.ArgsStart + q.Index;
                if (offset < qs || offset > qs + q.Length) continue;
                string quoted = q.Groups["n"].Value;
                var files = Templates().Where(t => t.Name == quoted).ToList();
                if (files.Count == 0) return null;
                return new TemplateTarget(Code("(template) " + quoted) + "\n\n" + string.Join("\n\n", files.Select(f => $"`{f.Path}`")), qs + 1, qs + q.Length - 1, files.Select(f => InFile(f.Path, 0, 0)).OfType<Location>().ToList());
            }
        }
        if (tag.Name is "block" or "endblock")
        {
            var parts = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
            if (parts.Count > 0 && offset >= parts[0].Offset && offset <= parts[0].Offset + parts[0].Text.Length)
            {
                var parent = ParentBlock(a, parts[0].Text);
                return parent == null ? null : new TemplateTarget(Code("(block) " + parts[0].Text) + $"\n\nOverrides the block of `{parent.Value.Template}`.", parts[0].Offset, parts[0].Offset + parts[0].Text.Length, new() { parent.Value.Where });
            }
            return null;
        }
        // a name the tag defines: hover shows where, the expressions behave like {{ }}
        foreach (var d in NamesDefinedBy(tag))
            if (offset >= d.Start && offset <= d.Start + d.Name.Length)
                return new TemplateTarget(Code($"({(d.Origin == "param" ? "parameter" : d.Origin)}) {d.Name}" + (tag.Name == "macro" && d.Origin == "macro" ? MacroSignature(tag) : "")), d.Start, d.Start + d.Name.Length, new());
        return ExpressionTarget(a, tag.Args, tag.ArgsStart, offset);
    }

    static string MacroSignature(TemplateTag tag) => JinjaMacro.Match(tag.Args) is { Success: true } m ? "(" + Regex.Replace(m.Groups["params"].Value.Trim(), @"\s+", " ") + ")" : "";

    /// <summary>The endpoint of `url_for('profile')` (its route) and the file of `url_for('static', filename='app.css')`.</summary>
    TemplateTarget? JinjaStringTarget(TemplateAnalysis a, int offset)
    {
        foreach (var (expr, at) in AllExpressions(a))
        {
            if (offset < at || offset > at + expr.Length) continue;
            foreach (var call in UrlForCalls(expr, at))
            {
                if (offset >= call.Start && offset <= call.End && call.Endpoint != "static")
                {
                    var routes = EndpointRoutes(RouteIndexOf(a.Snap), call.Endpoint).ToList();
                    if (routes.Count == 0) return null;
                    string body = string.Join("\n\n", routes.Select(r => $"`{RouteLine(r)}` — `{r.Def.Name}()` in {System.IO.Path.GetFileName(r.Module.Path ?? r.Module.Name)}"));
                    return new TemplateTarget("```python\n(endpoint) " + call.Endpoint + "\n```\n\n" + body, call.Start, call.End,
                        routes.Select(r => FixtureLocation(r.Module, r.Def.NameStart, r.Def.NameStart + r.Def.Name.Length)).OfType<Location>().ToList());
                }
                foreach (var k in call.Keywords.Where(k => k.Name is "filename" or "path"))
                    if (offset >= k.Start && offset <= k.Start + k.Value.Length && DjangoTemplateParser.Unquote(k.Value) is { } name)
                    {
                        var files = FilesBelowFolder("static").Where(f => f.Name == name).ToList();
                        if (files.Count == 0) return null;
                        return new TemplateTarget("```python\n(static file) " + name + "\n```\n\n" + string.Join("\n\n", files.Select(f => $"`{f.Path}`")), k.Start + 1, k.Start + 1 + name.Length,
                            files.Select(f => new Location(UriOfPath(f.Path), new Range(new Position(0, 0), new Position(0, 0)))).ToList());
                    }
            }
        }
        return null;
    }

    /// <summary>A macro of this template, or of the template an `import` / `from` names: hover shows its parameters and go to definition leads to it.</summary>
    TemplateTarget? JinjaMacroTarget(TemplateAnalysis a, TemplateVar variable, string[] chain, int index, int start, int end)
    {
        string Code(string s) => "```python\n" + s + "\n```";
        if (variable.Origin == "macro" && index == 0)
        {
            var tag = a.Parsed.Tags.FirstOrDefault(t => t.Name == "macro" && t.Start <= variable.Start && variable.Start < t.End);
            if (tag == null) return null;
            return new TemplateTarget(Code($"(macro) {variable.Name}{MacroSignature(tag)}"), start, end, new[] { InTemplate(a, variable.Start, variable.End) }.OfType<Location>().ToList());
        }
        if (variable.Origin is not ("import" or "from")) return null;
        var owner = a.Parsed.Tags.FirstOrDefault(t => t.Name == variable.Origin && t.Start <= variable.Start && variable.Start < t.End);
        if (owner == null) return null;
        var first = SplitTopLevel(owner.Args, ' ').Select(Trimmed).FirstOrDefault(p => p.Text.Length > 0);
        string? templateName = first.Text == null ? null : DjangoTemplateParser.Unquote(first.Text);
        if (variable.Origin == "import" && index == 0)
            return new TemplateTarget(Code($"(import) {variable.Name}") + (templateName != null ? $"\n\nThe macros of `{templateName}`." : ""), start, end, new[] { InTemplate(a, variable.Start, variable.End) }.OfType<Location>().ToList());
        string macroName = variable.Origin == "import" ? (index == 1 ? chain[1] : "") : NamesDefinedBy(owner).FirstOrDefault(d => d.Name == variable.Name).Value ?? variable.Name;
        if (templateName == null || macroName.Length == 0 || index > 1) return null;
        foreach (var file in Templates().Where(t => t.Name == templateName))
        {
            string? text = TemplateTextOf(file.Path);
            if (text == null) continue;
            var macro = DjangoTemplateParser.Parse(text, true).Tags.FirstOrDefault(t => t.Name == "macro" && JinjaMacro.Match(t.Args) is { Success: true } mm && mm.Groups["name"].Value == macroName);
            if (macro == null) continue;
            int at = macro.ArgsStart;
            return new TemplateTarget(Code($"(macro) {macroName}{MacroSignature(macro)}") + $"\n\nFrom `{templateName}`.", start, end, new[] { InFile(file.Path, at, at + macroName.Length) }.OfType<Location>().ToList());
        }
        return null;
    }

    TemplateTarget? JinjaFilterTarget(TemplateAnalysis a, string filter, int start, int end)
    {
        var env = JinjaEnvironmentOf(a.Snap);
        var locations = new List<Location>();
        if (env.Filters.TryGetValue(filter, out var at) && at is { } where && FixtureLocation(where.Module, where.Start, where.End) is { } l) locations.Add(l);
        string? doc = JinjaFilterDocs.GetValueOrDefault(filter) ?? (env.Filters.ContainsKey(filter) ? "A filter the project registers." : null);
        return doc == null ? null : new TemplateTarget("```python\n(filter) " + filter + "\n```\n\n" + doc, start, end, locations);
    }

    // ------------------------------------------------------------ completion

    static readonly string[] LoopMembers = { "index", "index0", "revindex", "revindex0", "first", "last", "length", "cycle", "depth", "depth0", "previtem", "nextitem", "changed" };

    IReadOnlyList<CompletionItem> JinjaCompletion(TemplateAnalysis a, bool inTag, string inside, int offset, bool closedAfter)
    {
        var items = new List<CompletionItem>();
        var env = JinjaEnvironmentOf(a.Snap);
        if (inTag)
        {
            var head = Regex.Match(inside, @"^\s*(?<name>\w*)$");
            if (head.Success)
            {
                string typed = head.Groups["name"].Value;
                var names = new SortedSet<string>(JinjaTags.Where(t => !t.StartsWith("end", StringComparison.Ordinal)), StringComparer.Ordinal);
                foreach (var (tags, ext, _) in ExtensionTags) if (env.EnabledExtensions.Contains(ext)) foreach (var t in tags.Where(t => !t.StartsWith("end", StringComparison.Ordinal))) names.Add(t);
                foreach (var t in env.CustomTags) names.Add(t);
                foreach (var n in names.Where(n => n.StartsWith(typed, StringComparison.Ordinal)))
                {
                    bool snippet = !closedAfter && JinjaTagSnippets.ContainsKey(n);
                    items.Add(new CompletionItem(n, CompletionKind.Keyword, JinjaTagDocs.GetValueOrDefault(n), "1" + n, snippet ? JinjaTagSnippets[n] : n, snippet));
                }
                // the closer of the innermost open block
                int depth = 0;
                foreach (var t in a.Parsed.Tags.Where(t => t.End <= offset).Reverse())
                {
                    if (DjangoTemplateParser.JinjaClosers.ContainsValue(t.Name)) depth++;
                    else if (DjangoTemplateParser.JinjaClosers.TryGetValue(t.Name, out var closer) && !(t.Name == "set" && t.Args.Contains('=')))
                    {
                        if (depth > 0) { depth--; continue; }
                        if (closer.StartsWith(typed, StringComparison.Ordinal)) items.Add(new CompletionItem(closer, CompletionKind.Keyword, $"closes {{% {t.Name} %}}", "0" + closer));
                        break;
                    }
                }
                return items;
            }
            var tagName = Regex.Match(inside, @"^\s*(?<name>\w+)\s+(?<rest>.*)$", RegexOptions.Singleline);
            if (!tagName.Success) return items;
            string name = tagName.Groups["name"].Value, rest = tagName.Groups["rest"].Value;
            // the template's name, or one of a list: `{% include ['custom.html', 'de`
            var quote = Regex.Match(rest, @"^(?:[\[(](?:\s*[""'][^""']*[""']\s*,)*\s*)?[""'](?<typed>[^""']*)$");
            if (quote.Success && name is "extends" or "include" or "import" or "from")
            {
                string typed = quote.Groups["typed"].Value;
                foreach (var t in Templates().Select(t => t.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(t => t.StartsWith(typed, StringComparison.Ordinal)))
                    items.Add(new CompletionItem(t, CompletionKind.Module, "template", "1" + t));
                return items;
            }
            if (name == "filter")
                return FilterItems(Regex.Match(rest, @"(?:^|\|)\s*(?<typed>\w*)$").Groups["typed"].Value);
            if (name is "block" or "endblock" or "macro" or "endmacro" or "raw") return items;
            return JinjaExpressionCompletion(a, rest, offset);
        }
        return JinjaExpressionCompletion(a, inside, offset);

        List<CompletionItem> FilterItems(string typed)
        {
            var list = new List<CompletionItem>();
            foreach (var f in JinjaFilterDocs.Keys.Concat(env.Filters.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(f => f.StartsWith(typed, StringComparison.Ordinal)))
                list.Add(new CompletionItem(f, CompletionKind.Function, "filter", "1" + f, Documentation: JinjaFilterDocs.GetValueOrDefault(f) ?? "A filter the project registers."));
            return list;
        }
    }

    IReadOnlyList<CompletionItem> JinjaExpressionCompletion(TemplateAnalysis a, string expression, int offset)
    {
        var items = new List<CompletionItem>();
        var env = JinjaEnvironmentOf(a.Snap);

        // inside a string: endpoints of url_for and static files
        var endpoint = Regex.Match(expression, @"\burl_for\(\s*[""'](?<typed>[^""']*)$");
        if (endpoint.Success)
        {
            string typed = endpoint.Groups["typed"].Value;
            foreach (var e in RouteIndexOf(a.Snap).Routes.Select(r => r.Endpoint).Append("static").Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(e => e.StartsWith(typed, StringComparison.Ordinal)))
                items.Add(new CompletionItem(e, CompletionKind.Constant, "endpoint", "1" + e));
            return items;
        }
        var file = Regex.Match(expression, @"\burl_for\(\s*[""'](?:[\w.]*\.)?static[""'][^)]*\b(?:filename|path)\s*=\s*[""'](?<typed>[^""']*)$");
        if (file.Success)
        {
            string typed = file.Groups["typed"].Value;
            foreach (var f in FilesBelowFolder("static").Select(f => f.Name).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(f => f.StartsWith(typed, StringComparison.Ordinal)).Take(300))
                items.Add(new CompletionItem(f, CompletionKind.Module, "static file", "1" + f));
            return items;
        }
        if (InsideString(expression)) return items;

        // after a pipe: filters; after is: tests
        var pipe = Regex.Match(expression, @"\|\s*(?<typed>\w*)$");
        if (pipe.Success)
        {
            string typed = pipe.Groups["typed"].Value;
            foreach (var f in JinjaFilterDocs.Keys.Concat(env.Filters.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(f => f.StartsWith(typed, StringComparison.Ordinal)))
                items.Add(new CompletionItem(f, CompletionKind.Function, "filter", "1" + f, Documentation: JinjaFilterDocs.GetValueOrDefault(f) ?? "A filter the project registers."));
            return items;
        }
        var test = Regex.Match(expression, @"\bis\s+(?:not\s+)?(?<typed>\w*)$");
        if (test.Success)
        {
            string typed = test.Groups["typed"].Value;
            foreach (var t in JinjaTests.Concat(env.Tests.Keys).Distinct().OrderBy(x => x, StringComparer.Ordinal).Where(t => t.StartsWith(typed, StringComparison.Ordinal)))
                items.Add(new CompletionItem(t, CompletionKind.Function, "test", "1" + t));
            return items;
        }

        // loop. and the macros of an imported template
        var member = Regex.Match(expression, @"(?<![\w.])(?<owner>[A-Za-z_]\w*)\.(?<typed>\w*)$");
        if (member.Success)
        {
            var scope = ScopeAt(a, offset);
            var owner = scope.Find(member.Groups["owner"].Value);
            string typed = member.Groups["typed"].Value;
            if (owner?.Origin == "loop")
            {
                foreach (var m in LoopMembers.Where(m => m.StartsWith(typed, StringComparison.Ordinal))) items.Add(new CompletionItem(m, CompletionKind.Property, "loop", "0" + m));
                return items;
            }
            if (owner?.Origin == "import")
            {
                foreach (var (macro, signature) in MacrosImportedBy(a, owner).Where(m => m.Name.StartsWith(typed, StringComparison.Ordinal)))
                    items.Add(new CompletionItem(macro, CompletionKind.Function, "macro" + signature, "0" + macro));
                return items;
            }
        }
        return ExpressionCompletion(a, expression, offset);
    }

    IEnumerable<(string Name, string Signature)> MacrosImportedBy(TemplateAnalysis a, TemplateVar import)
    {
        var owner = a.Parsed.Tags.FirstOrDefault(t => t.Name == "import" && t.Start <= import.Start && import.Start < t.End);
        var first = owner == null ? default : SplitTopLevel(owner.Args, ' ').Select(Trimmed).FirstOrDefault(p => p.Text.Length > 0);
        string? templateName = first.Text == null ? null : DjangoTemplateParser.Unquote(first.Text);
        if (templateName == null) yield break;
        var file = Templates().FirstOrDefault(t => t.Name == templateName);
        if (file.Path == null || TemplateTextOf(file.Path) is not { } text) yield break;
        foreach (var tag in DjangoTemplateParser.Parse(text, true).Tags.Where(t => t.Name == "macro"))
            if (JinjaMacro.Match(tag.Args) is { Success: true } m) yield return (m.Groups["name"].Value, MacroSignature(tag));
    }
}
