using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server.Pytest;

namespace PyMagic.Server;

/// <summary>
/// More of what a Django template sees and is checked for: the context `{% include ... with a=b %}` gives the included file, the
/// project's context processors, `{% blocktranslate %}` bodies, the tag libraries of installed packages (`crispy_forms_tags`), and the
/// HTML itself.
/// </summary>
public sealed partial class LanguageService
{
    // ------------------------------------------------------------ {% include %}

    /// <summary>The variables the templates that include this one give it: all of theirs at the include (unless `only`), and the `with` names.</summary>
    List<TemplateVar> IncludeContext(string name, int depth)
    {
        var result = new List<TemplateVar>();
        foreach (var (otherName, path) in Templates())
        {
            if (otherName == name || TemplateTextOf(path) is not { } text || text.IndexOf(name, StringComparison.Ordinal) < 0 || text.IndexOf("include", StringComparison.Ordinal) < 0) continue;
            string uri = UriOfPath(path);
            if (IsJinjaTemplate(uri, Full())) continue;
            var parsed = DjangoTemplateParser.Parse(text, false);
            var includes = parsed.Tags.Where(t => t.Name == "include" && DjangoTemplateParser.SplitArguments(t.Args, t.ArgsStart) is { Count: > 0 } args
                && DjangoTemplateParser.Unquote(args[0].Text) is { } q && TemplateWithoutPartial(q) == name).ToList();
            if (includes.Count == 0) continue;
            var includer = AnalyzeTemplate(uri, depth + 1);
            foreach (var tag in includes)
            {
                var scope = ScopeAt(includer, tag.Start);
                var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
                bool only = args.Any(x => x.Text == "only");
                int with = args.FindIndex(x => x.Text == "with");
                if (with > 0)
                    foreach (var (arg, off) in args.Skip(with + 1))
                    {
                        int eq = arg.IndexOf('=');
                        if (eq <= 0) continue;
                        result.Add(new TemplateVar(arg[..eq], TypeOfTemplateExpression(includer, scope, arg[(eq + 1)..]), off, off + eq, "include", null, uri));
                    }
                if (!only)
                    foreach (var v in scope.All().Where(v => v.Origin != "builtin"))
                        result.Add(v with { Origin = "include", Uri = v.Uri ?? uri, Entry = null });
            }
        }
        return result;
    }

    // ------------------------------------------------------------ context processors

    /// <summary>What Django's own context processors add, by their dotted path.</summary>
    static readonly Dictionary<string, string[]> BuiltinProcessors = new(StringComparer.Ordinal)
    {
        ["django.template.context_processors.debug"] = new[] { "debug", "sql_queries" },
        ["django.template.context_processors.request"] = new[] { "request" },
        ["django.contrib.auth.context_processors.auth"] = new[] { "user", "perms" },
        ["django.contrib.messages.context_processors.messages"] = new[] { "messages", "DEFAULT_MESSAGE_LEVELS" },
        ["django.template.context_processors.i18n"] = new[] { "LANGUAGES", "LANGUAGE_CODE", "LANGUAGE_BIDI" },
        ["django.template.context_processors.tz"] = new[] { "TIME_ZONE" },
        ["django.template.context_processors.static"] = new[] { "STATIC_URL" },
        ["django.template.context_processors.media"] = new[] { "MEDIA_URL" },
        ["django.template.context_processors.csrf"] = new[] { "csrf_token" },
    };

    readonly ConditionalWeakTable<Snapshot, List<ContextEntry>> _processorEntries = new();

    /// <summary>
    /// The keys the context processors of `TEMPLATES[...]["OPTIONS"]["context_processors"]` add to every template: the dict a project's
    /// own processor returns (`def cart(request): return {"cart": Cart(request)}`), and STATIC_URL / MEDIA_URL of Django's.
    /// </summary>
    List<ContextEntry> ContextProcessorEntries(Snapshot snap) => _processorEntries.GetValue(snap, s =>
    {
        var result = new List<ContextEntry>();
        foreach (var settings in s.Project)
        {
            if (settings.IsStub || settings.Source.IndexOf("context_processors", StringComparison.Ordinal) < 0 || settings.Source.IndexOf("TEMPLATES", StringComparison.Ordinal) < 0) continue;
            foreach (var dict in AstChildren.Descendants(settings.Ast).OfType<DictExpr>())
                for (int i = 0; i < dict.Keys.Count; i++)
                {
                    if (dict.Keys[i] is not ConstExpr key || FixtureModel.StringValue(key) != "context_processors" || dict.Values[i] is not (ListExpr or TupleExpr)) continue;
                    var items = dict.Values[i] is ListExpr l ? l.Items : ((TupleExpr)dict.Values[i]).Items;
                    foreach (var item in items)
                    {
                        if (item is not ConstExpr literal || FixtureModel.StringValue(literal) is not { } dotted || FixtureModel.StringSpan(literal) is not { } span) continue;
                        if (BuiltinProcessors.TryGetValue(dotted, out var names))
                        {
                            foreach (var n in names.Where(n => n is "STATIC_URL" or "MEDIA_URL"))
                                result.Add(new ContextEntry(n, settings, span.Start, span.End, null, s.Workspace.Evaluator.BuiltinInstance("str"), dotted));
                            continue;
                        }
                        int dot = dotted.LastIndexOf('.');
                        if (dot <= 0) continue;
                        string moduleName = dotted[..dot], function = dotted[(dot + 1)..];
                        var module = s.Project.FirstOrDefault(m => !m.IsStub && m.Name == moduleName);
                        if (module?.Semantics.ModuleScope.Symbols.GetValueOrDefault(function)?.Declarations.FirstOrDefault()?.Node is not FunctionDef def) continue;
                        foreach (var ret in AstChildren.Descendants(def).OfType<ReturnStmt>())
                            if (ret.Value != null) CollectDictEntries(module, ret.Value, ret, $"context processor {dotted}", result);
                    }
                }
        }
        return result;
    });

    // ------------------------------------------------------------ {% blocktranslate %}

    static readonly Regex KeywordArgument = new(@"^(?<name>\w+)=(?<value>.+)$", RegexOptions.Compiled);

    /// <summary>The names a {% blocktranslate with a=b count n=c asvar x %} gives its body (and `asvar` the rest of the template).</summary>
    List<TemplateVar> BlockTranslateVars(TemplateAnalysis a, TemplateScope scope, TemplateTag tag)
    {
        var vars = new List<TemplateVar>();
        var args = DjangoTemplateParser.SplitArguments(tag.Args, tag.ArgsStart);
        for (int i = 0; i < args.Count; i++)
        {
            var (text, off) = args[i];
            if (KeywordArgument.Match(text) is { Success: true } kw)
                vars.Add(new TemplateVar(kw.Groups["name"].Value, TypeOfTemplateExpression(a, scope, kw.Groups["value"].Value), off, off + kw.Groups["name"].Length, tag.Name, null));
            else if (text == "as" && i > 0 && i + 1 < args.Count)
                vars.Add(new TemplateVar(args[i + 1].Text, TypeOfTemplateExpression(a, scope, args[i - 1].Text), args[i + 1].Offset, args[i + 1].Offset + args[i + 1].Text.Length, tag.Name, null));
            else if (text == "asvar" && i + 1 < args.Count)
                scope.Frames[^1].Vars.Add(new TemplateVar(args[i + 1].Text, a.Snap.Workspace.Evaluator.BuiltinInstance("str"), args[i + 1].Offset, args[i + 1].Offset + args[i + 1].Text.Length, tag.Name, null));
        }
        return vars;
    }

    /// <summary>
    /// What Django refuses or gets wrong in a {% blocktranslate %}: an option it does not know, any tag inside it but {% plural %} (with
    /// `count`), and a variable that is more than a name: `{{ book.title }}` and `{{ name|upper }}` are looked up as the whole text and show nothing.
    /// </summary>
    void BlockTranslateProblems(TemplateAnalysis a, Action<int, int, string, int, string> add)
    {
        var tags = a.Parsed.Tags;
        for (int t = 0; t < tags.Count; t++)
        {
            var open = tags[t];
            if (open.Name is not ("blocktrans" or "blocktranslate")) continue;
            var args = DjangoTemplateParser.SplitArguments(open.Args, open.ArgsStart);
            bool count = false;
            for (int i = 0; i < args.Count; i++)
            {
                string text = args[i].Text;
                if (text is "with" or "count")
                {
                    count |= text == "count";
                    // name=value pairs, or the old `value as name`
                    while (i + 1 < args.Count)
                    {
                        if (KeywordArgument.IsMatch(args[i + 1].Text)) i++;
                        else if (i + 3 < args.Count && args[i + 2].Text == "as") i += 3;
                        else break;
                    }
                    continue;
                }
                if (text is "trimmed") continue;
                if (text is "context" or "asvar") { i++; continue; }
                // Django stops at the first option it does not know
                add(args[i].Offset, args[i].Offset + text.Length, $"Unknown option '{text}' of {{% {open.Name} %}}: it takes with, count, context, trimmed and asvar", Severity.Error, "template-blocktrans");
                break;
            }
            string end = "end" + open.Name;
            int close = t + 1;
            bool strayReported = false;
            for (; close < tags.Count && tags[close].Name != end; close++)
            {
                var inner = tags[close];
                if (inner.Name == "plural" && count || strayReported) continue;
                // and at the first tag inside the body
                add(inner.Start, inner.End, $"{{% {open.Name} %}} allows no tags inside it{(count ? " but {% plural %}" : "")}: Django fails with \"doesn't allow other block tags\"; put {{% {inner.Name} %}} outside, or bind the value with `with`", Severity.Error, "template-blocktrans");
                strayReported = true;
            }
            int bodyEnd = close < tags.Count ? tags[close].Start : a.Text.Length;
            foreach (var v in a.Parsed.Variables.Where(v => v.Start > open.End && v.End <= bodyEnd))
            {
                string expr = v.Expression.Trim();
                if (Regex.IsMatch(expr, @"^\w+$")) continue;
                string suggestion = Regex.Replace(FirstOfFilters(expr).Trim().Split('.').Last(), @"\W", "_");
                add(v.ExprStart, v.ExprStart + v.Expression.TrimEnd().Length,
                    $"{{% {open.Name} %}} takes only plain names: '{expr}' is looked up as a whole and shows nothing; bind it first: {{% {open.Name} with {suggestion}={expr} %}}", Severity.Warning, "template-blocktrans");
            }
        }
    }

    // ------------------------------------------------------------ tag libraries of installed packages

    readonly Dictionary<string, (HashSet<string> Tags, HashSet<string> Filters)?> _installedLibraries = new(StringComparer.Ordinal);

    /// <summary>
    /// `{% load crispy_forms_tags %}` when the library is in an installed package (`site-packages/crispy_forms/templatetags/crispy_forms_tags.py`):
    /// the tags and filters it registers, read from its source; null when no installed package has it.
    /// </summary>
    (HashSet<string> Tags, HashSet<string> Filters)? InstalledTagLibrary(string name)
    {
        if (_installedLibraries.TryGetValue(name, out var known)) return known;
        (HashSet<string>, HashSet<string>)? found = null;
        foreach (var root in EffectiveLibraryRoots())
        {
            IEnumerable<string> packages;
            try { packages = Directory.Exists(root) ? Directory.EnumerateDirectories(root).ToList() : Enumerable.Empty<string>(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var package in packages)
            {
                string file = System.IO.Path.Combine(package, "templatetags", name + ".py");
                if (!File.Exists(file)) continue;
                string source;
                try { source = File.ReadAllText(file); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                var tags = new HashSet<string>(StringComparer.Ordinal);
                var filters = new HashSet<string>(StringComparer.Ordinal);
                var ast = Parser.Parse(source).Module;
                foreach (var def in AstChildren.Descendants(ast).OfType<FunctionDef>())
                    foreach (var d in def.Decorators)
                    {
                        var callee = d is CallExpr c ? c.Func : d;
                        if (callee is not AttributeExpr { Attr: var kind } || kind is not ("filter" or "simple_tag" or "inclusion_tag" or "tag" or "simple_block_tag")) continue;
                        string registered = d is CallExpr call ? StringKeyword(call, "name") ?? FixtureModel.StringValue(call.Args.FirstOrDefault(x => x.Name == null)?.Value) ?? def.Name : def.Name;
                        (kind == "filter" ? filters : tags).Add(registered);
                    }
                // register.tag("name", function), register.filter("name", function), register.tag(SomeNode)
                foreach (var call in AstChildren.Descendants(ast).OfType<CallExpr>())
                {
                    if (call.Func is not AttributeExpr { Attr: "filter" or "tag" or "simple_tag", Value: NameExpr } acc) continue;
                    var args = call.Args.Where(x => x.Name == null).ToList();
                    if (args.Count == 0) continue;
                    string? registered = args.Count > 1 ? FixtureModel.StringValue(args[0].Value) : args[0].Value is NameExpr n ? n.Name : null;
                    if (registered != null) (acc.Attr == "filter" ? filters : tags).Add(registered);
                }
                // a block tag's end
                foreach (var t in tags.ToList()) tags.Add("end" + t);
                found = (tags, filters);
                break;
            }
            if (found != null) break;
        }
        return _installedLibraries[name] = found;
    }

    // ------------------------------------------------------------ the HTML itself

    static readonly HashSet<string> VoidElements = new(StringComparer.OrdinalIgnoreCase) { "area", "base", "br", "col", "embed", "hr", "img", "input", "link", "meta", "param", "source", "track", "wbr", "keygen", "command" };
    // elements whose end tag HTML lets you leave out: a closing tag of an element around them closes them too
    static readonly HashSet<string> OptionalEnd = new(StringComparer.OrdinalIgnoreCase) { "p", "li", "dt", "dd", "option", "optgroup", "tr", "td", "th", "thead", "tbody", "tfoot", "colgroup", "caption", "rt", "rp", "html", "head", "body" };
    static readonly Regex HtmlTag = new(@"<(?<close>/)?(?<name>[a-zA-Z][\w:-]*)(?<attrs>(?:[^<>""']|""[^""]*""|'[^']*')*?)(?<self>/)?>", RegexOptions.Compiled);
    static readonly Regex HtmlAttribute = new(@"(?<=\s)(?<name>[^\s=/""'<>{}%]+)(?:\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+))?", RegexOptions.Compiled);

    /// <summary>
    /// The HTML of a template, with its tags and variables taken out: an attribute written twice on one element, and a closing tag that closes
    /// nothing or another element than the one open. Elements opened and closed in different branches of an {% if %} or across {% block %}s
    /// are not judged: only a mismatch with no template tag between the two tags is reported, and only the first.
    /// </summary>
    void HtmlProblems(TemplateAnalysis a, Action<int, int, string, int, string> add)
    {
        string text = a.Text;
        // template syntax, comments, scripts and styles are not HTML to look at
        var blank = text.ToCharArray();
        void Blank(int s, int e) { for (int i = Math.Max(0, s); i < e && i < blank.Length; i++) if (blank[i] != '\n') blank[i] = ' '; }
        foreach (var t in a.Parsed.Tags) Blank(t.Start, t.End);
        foreach (var v in a.Parsed.Variables) Blank(v.Start, v.End);
        foreach (Match m in Regex.Matches(text, @"\{#.*?#\}|<!--.*?-->|<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.Singleline | RegexOptions.IgnoreCase))
        {
            int bodyStart = m.Value.StartsWith("<s", StringComparison.OrdinalIgnoreCase) ? m.Index + m.Value.IndexOf('>') + 1 : m.Index;
            int bodyEnd = m.Value.StartsWith("<s", StringComparison.OrdinalIgnoreCase) ? m.Index + m.Value.LastIndexOf('<') : m.Index + m.Length;
            Blank(bodyStart, bodyEnd);
        }
        // {% verbatim %} and {% comment %} bodies
        var tags = a.Parsed.Tags;
        for (int i = 0; i < tags.Count; i++)
            if (tags[i].Name is "comment" or "verbatim" && tags.Skip(i + 1).FirstOrDefault(x => x.Name == "end" + tags[i].Name) is { } end) Blank(tags[i].End, end.Start);
        string html = new string(blank);

        bool TemplateTagBetween(int from, int to) => tags.Any(t => t.Start >= from && t.Start < to);
        var stack = new List<(string Name, int Start, int End)>();
        bool reported = false;
        foreach (Match m in HtmlTag.Matches(html))
        {
            string name = m.Groups["name"].Value.ToLowerInvariant();
            int nameStart = m.Groups["name"].Index, nameEnd = nameStart + m.Groups["name"].Length;
            if (!m.Groups["close"].Success)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (Match at in HtmlAttribute.Matches(m.Groups["attrs"].Value))
                {
                    string attr = at.Groups["name"].Value;
                    if (!seen.Add(attr))
                    {
                        int s = m.Groups["attrs"].Index + at.Index;
                        add(s, s + attr.Length, $"The attribute '{attr}' is written twice on <{name}>: browsers keep only the first", Severity.Warning, "template-html");
                    }
                }
                if (!VoidElements.Contains(name) && !m.Groups["self"].Success) stack.Add((name, nameStart, nameEnd));
                continue;
            }
            if (reported || VoidElements.Contains(name)) continue;
            int match = stack.FindLastIndex(e => e.Name == name);
            if (match >= 0 && stack.Skip(match + 1).All(e => OptionalEnd.Contains(e.Name)))
            {
                stack.RemoveRange(match, stack.Count - match);
                continue;
            }
            // a template tag between them (an {% if %} branch, a {% block %}) leaves it open what the HTML is
            var top = stack.Count > 0 ? stack[^1] : default;
            if (TemplateTagBetween(stack.Count > 0 ? top.Start : 0, m.Index) || stack.Count == 0 && tags.Any(t => t.Start < m.Index)) { reported = true; continue; }
            if (match < 0)
                add(nameStart - 2, nameEnd + 1, stack.Count == 0 ? $"</{name}> closes nothing: no <{name}> is open" : $"</{name}> closes nothing: no <{name}> is open (<{top.Name}> is)", Severity.Warning, "template-html");
            else
                add(nameStart - 2, nameEnd + 1, $"</{name}> closes <{name}> while <{top.Name}> inside it is still open", Severity.Warning, "template-html");
            reported = true;
        }
    }
}
