using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Jinja templates (item 129): Jinja's own syntax, what Flask and the project register on the environment, url_for in a template, and the names a view gives.</summary>
public class JinjaTemplateTests
{
    const string App =
        "from flask import Flask, render_template\n\napp = Flask(__name__)\n\n\nclass Book:\n    def __init__(self, title: str, id: int):\n        self.title = title\n        self.id = id\n\n\n" +
        "@app.template_filter('money')\ndef money(v):\n    return f'${v}'\n\n\n@app.template_test('cheap')\ndef cheap(v):\n    return v < 5\n\n\n" +
        "@app.context_processor\ndef inject():\n    return {'site_name': 'Shop'}\n\n\n" +
        "@app.route('/')\ndef index():\n    books = [Book('a', 1)]\n    return render_template('index.html', books=books, title='Home')\n\n\n" +
        "@app.route('/book/<int:book_id>')\ndef book(book_id):\n    return render_template('book.html', book=Book('x', book_id))\n";

    const string Base = "<html><title>{% block title %}{% endblock %}</title><body>{% block content %}{% endblock %}</body></html>\n";

    const string Macros = "{% macro input(name, value='', type='text') %}<input type=\"{{ type }}\" name=\"{{ name }}\" value=\"{{ value }}\">{% endmacro %}\n" +
        "{% macro card(title) %}<div>{{ title }}{{ caller() }}</div>{% endmacro %}\n";

    sealed record Rig(LanguageService Svc, string Uri, Position Pos, string Dir);

    static Rig Make(string marked, string page = "index.html", string app = App, (string Rel, string Text)[]? templates = null)
    {
        int i = marked.IndexOf('@');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_jinja_" + Guid.NewGuid().ToString("N"));
        var all = new List<(string Rel, string Text)> { (page, text), ("base.html", Base), ("macros.html", Macros) };
        if (templates != null) all.AddRange(templates);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        Directory.CreateDirectory(Path.Combine(dir, "static", "css"));
        File.WriteAllText(Path.Combine(dir, "static", "css", "app.css"), "body {}");
        string appPath = Path.Combine(dir, "app.py");
        File.WriteAllText(appPath, app);
        svc.Open(LanguageService.UriOfPath(appPath), app, 1);
        foreach (var (rel, body) in all.GroupBy(t => t.Rel).Select(g => g.First()))
        {
            string full = Path.Combine(dir, "templates", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
            svc.Open(LanguageService.UriOfPath(full), body, 1);
        }
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "templates", page.Replace('/', Path.DirectorySeparatorChar)));
        return new Rig(svc, uri, i < 0 ? new Position(0, 0) : new LineMap(text).PositionOf(i), dir);
    }

    static List<LspDiagnostic> DiagnosticsOf(Rig r, string? code = null) => r.Svc.Diagnostics(r.Uri).Where(d => code == null || d.Code == code).ToList();

    static string Messages(IEnumerable<LspDiagnostic> d) => string.Join("\n", d.Select(x => $"{x.Range.Start.Line + 1}: {x.Code} {x.Message}"));

    /// <summary>A template that uses most of Jinja, written to compile in the real Jinja (checked with jinja2 3.1.6): nothing is reported.</summary>
    const string Clean =
        "{% extends 'base.html' %}\n{% import 'macros.html' as forms %}\n{% from 'macros.html' import card %}\n" +
        "{% block title %}{{ title }} - {{ site_name }}{% endblock %}\n" +
        "{% block content %}\n{#- a comment\n   over two lines -#}\n{% set count = books|length %}\n{% set ns = namespace(total=0) %}\n" +
        "{%- for b in books if b.title recursive %}\n  <a href=\"{{ url_for('book', book_id=b.id) }}\">{{ loop.index }}. {{ b.title|upper }} {{ b.id|money }}</a>\n" +
        "  {% set ns.total = ns.total + b.id %}\n  {% if b.id is cheap %}cheap{% elif b.id is divisibleby 2 %}even{% else %}other{% endif %}\n{%- else %}\n  none\n{% endfor -%}\n" +
        "{% with a=1, b=2 %}{{ a ~ b }}{% endwith %}\n{{ forms.input('q', type='search') }}\n{% call card('Books') %}{{ count }} books{% endcall %}\n" +
        "{% for i in range(3) %}{{ i }}{% endfor %}\n{{ maybe|default('none') }}{% if missing_flag %}x{% endif %}\n" +
        "<link href=\"{{ url_for('static', filename='css/app.css') }}\">{{ request.path }} {{ session.get('x') }} {{ get_flashed_messages()|tojson }}\n" +
        "{% raw %}{% load static %}{{ }}{% endraw %}\n{% filter upper %}shout{% endfilter %}\n{% autoescape false %}{{ title|safe }}{% endautoescape %}\n" +
        "{{ super() }}{{ self.title() }}\n{% endblock %}\n";

    [Fact]
    public void ATemplateThatUsesMostOfJinjaHasNoFindings()
    {
        Assert.Equal("", Messages(DiagnosticsOf(Make(Clean))));
    }

    [Fact]
    public void DjangoTagsAreErrorsInJinja()
    {
        var d = DiagnosticsOf(Make("{% load static %}\n{% url 'index' %}\n{% for b in books %}{% empty %}{% endfor %}\n{% comment %}x{% endcomment %}\n"), "jinja-tag");
        Assert.True(d.All(x => x.Severity == Severity.Error));
        Assert.Contains("Jinja has no {% load %}", d[0].Message);
        Assert.Contains("url_for('name')", d[1].Message);
        Assert.Contains("{% else %}", d[2].Message);
        Assert.Contains("{# ... #}", d[3].Message);
    }

    [Fact]
    public void ExtensionTagsNeedTheirExtension()
    {
        string page = "{% for b in books %}{% if b.id %}{% break %}{% endif %}{% endfor %}\n";
        var d = DiagnosticsOf(Make(page), "jinja-tag");
        Assert.Equal(1, d.Count);
        Assert.Contains("jinja2.ext.loopcontrols", d[0].Message);
        Assert.Equal(0, DiagnosticsOf(Make(page, app: App + "\napp.jinja_env.add_extension('jinja2.ext.loopcontrols')\n"), "jinja-tag").Count);
    }

    [Fact]
    public void ATagOfAnExtensionOfTheProjectIsKnown()
    {
        string app = App + "\n\nfrom jinja2.ext import Extension\n\n\nclass CacheExtension(Extension):\n    tags = {'cache'}\n";
        Assert.Equal(0, DiagnosticsOf(Make("{% cache 60 %}x{% endcache %}\n", app: app), "jinja-tag").Count);
    }

    [Fact]
    public void ExpressionsJinjaCannotRead()
    {
        var d = DiagnosticsOf(Make("{{ }}\n{{ money(1 }}\n{{ title|default:\"a\" }}\n{% if (a %}x{% endif %}\n{{ 'open }}\n"), "jinja-syntax").OrderBy(x => x.Range.Start.Line);
        Assert.Equal("1: jinja-syntax Jinja expects an expression here\n2: jinja-syntax '(' is not closed\n3: jinja-syntax Jinja does not take Django's '|default:argument': write default(argument)\n4: jinja-syntax '(' is not closed\n5: jinja-syntax The string is not closed", Messages(d));
    }

    [Fact]
    public void JinjasStructure()
    {
        Assert.True(DiagnosticsOf(Make("{% macro m() %}x\n")).Any(x => x.Message.Contains("Unclosed tag 'macro'")));
        Assert.True(DiagnosticsOf(Make("{% set x %}abc\n")).Any(x => x.Message.Contains("Unclosed tag 'set'")));
        Assert.True(DiagnosticsOf(Make("{% for b in books %}{% elif x %}{% endfor %}\n")).Any(x => x.Message.Contains("'elif' is not inside")));
        Assert.True(DiagnosticsOf(Make("{% block a %}{% endblock %}{% block a %}{% endblock %}\n")).Any(x => x.Message.Contains("defined twice")));
        // Jinja prints what comes before extends
        Assert.Equal(0, DiagnosticsOf(Make("hello\n{% extends 'base.html' %}\n"), "template-structure").Count);
    }

    [Fact]
    public void UnknownFiltersAndTests()
    {
        var d = DiagnosticsOf(Make("{{ title|nope }}{{ title|money }}{% if title is weird %}{% endif %}{% if title is cheap %}{% endif %}{% filter shout %}x{% endfilter %}\n"), "jinja-filter");
        Assert.Equal("1: jinja-filter No filter named 'nope': neither Jinja nor the project registers it\n1: jinja-filter No filter named 'shout': neither Jinja nor the project registers it\n1: jinja-filter No test named 'weird': neither Jinja nor the project registers it", Messages(d.OrderBy(x => x.Message.Contains("test"))));
        Assert.True(d.All(x => x.Severity == Severity.Warning));
    }

    [Fact]
    public void FiltersRegisteredOnTheEnvironmentAreKnown()
    {
        string app = App + "\n\ndef slug(v):\n    return v\n\n\napp.jinja_env.filters['slug'] = slug\napp.add_template_filter(slug, 'slug2')\napp.jinja_env.filters.update(slug3=slug)\n";
        Assert.Equal(0, DiagnosticsOf(Make("{{ title|slug }}{{ title|slug2 }}{{ title|slug3 }}\n", app: app), "jinja-filter").Count);
    }

    [Fact]
    public void AFlaskExtensionThatMayAddFiltersMakesThemWeak()
    {
        string app = "import flask_moment\n" + App;
        Assert.True(DiagnosticsOf(Make("{{ title|nope }}\n", app: app), "jinja-filter").All(x => x.Severity == Severity.Information));
    }

    [Fact]
    public void UrlForInATemplate()
    {
        var d = DiagnosticsOf(Make("{{ url_for('book') }}\n{{ url_for('book', book_id='abc') }}\n{{ url_for('book', book_id=3) }}\n{{ url_for('nowhere') }}\n{{ url_for('static', filename='css/missing.css') }}\n"));
        Assert.Equal("1: flask-url-for-arguments url_for('book') needs 'book_id': the route '/book/<int:book_id>' has it as a parameter and Flask cannot build the URL without it\n" +
            "2: flask-url-for-arguments 'book_id' is an int in the route '/book/<int:book_id>', and 'abc' is not a number\n" +
            "4: route-endpoint No route has the endpoint name 'nowhere'\n5: template-static Static file 'css/missing.css' is not in any static folder of the project", Messages(d));
    }

    [Fact]
    public void ANameNoViewGivesIsReported()
    {
        var d = DiagnosticsOf(Make("{{ titel }}{{ title }}{{ site_name }}{{ maybe|default('x') }}{% if flag %}{% endif %}{% set y = 1 %}{{ y }}{{ titel }}\n"), "jinja-undefined");
        Assert.Equal(2, d.Count);
        Assert.Contains("'titel' is not given to 'index.html'", d[0].Message);
        Assert.True(d.All(x => x.Severity == Severity.Information));
    }

    [Fact]
    public void NamesAreNotCheckedWhereTheContextIsNotKnown()
    {
        // a template no view renders (it is extended), and a view that spreads a dict it builds elsewhere
        Assert.Equal(0, DiagnosticsOf(Make("{{ anything }}\n", "base.html"), "jinja-undefined").Count);
        string app = App.Replace("return render_template('index.html', books=books, title='Home')", "return render_template('index.html', **context_of(books))");
        Assert.Equal(0, DiagnosticsOf(Make("{{ anything }}\n", app: app), "jinja-undefined").Count);
    }

    [Fact]
    public void TheKeysOfASpreadDictAreGiven()
    {
        string app = App.Replace("return render_template('index.html', books=books, title='Home')", "context = {'books': books}\n    context['title'] = 'Home'\n    return render_template('index.html', **context)");
        Assert.Equal(0, DiagnosticsOf(Make("{{ title }}{{ books }}\n", app: app), "jinja-undefined").Count);
    }

    [Fact]
    public void AttributesOfProjectClasses()
    {
        var d = DiagnosticsOf(Make("{{ book.titel }}{{ book.title }}\n", "book.html"), "template-attribute");
        Assert.Equal(1, d.Count);
        Assert.Contains("'Book' has no attribute 'titel'", d[0].Message);
    }

    [Fact]
    public void ATemplateMissing()
    {
        var d = DiagnosticsOf(Make("{% include 'nothere.html' %}{% include 'gone.html' ignore missing %}{% import 'macros.html' as m %}\n"), "django-template");
        Assert.Equal(1, d.Count);
        Assert.Contains("nothere.html", d[0].Message);
    }

    // ------------------------------------------------------------ hover and go to definition

    static string? HoverOf(Rig r) => r.Svc.Hover(r.Uri, r.Pos)?.Contents.Value;

    [Fact]
    public void HoverShowsTypesFromTheView()
    {
        Assert.Contains("str", HoverOf(Make("{{ book.ti@tle }}\n", "book.html")) ?? "");
        Assert.Contains("(loop) loop", HoverOf(Make("{% for b in books %}{{ lo@op.index }}{% endfor %}\n")) ?? "");
        Assert.Contains("Book", HoverOf(Make("{% for b in books %}{{ @b }}{% endfor %}\n")) ?? "");
    }

    [Fact]
    public void AnEndpointLeadsToItsView()
    {
        var r = Make("{{ url_for('bo@ok', book_id=1) }}\n");
        Assert.Contains("(endpoint) book", HoverOf(r) ?? "");
        var loc = r.Svc.Definition(r.Uri, r.Pos).Single();
        Assert.True(loc.Uri.EndsWith("app.py", StringComparison.Ordinal));
    }

    [Fact]
    public void AnImportedMacroLeadsToItsTemplate()
    {
        var r = Make("{% import 'macros.html' as forms %}{{ forms.in@put('q') }}\n");
        Assert.Contains("(macro) input(name, value='', type='text')", HoverOf(r) ?? "");
        var loc = r.Svc.Definition(r.Uri, r.Pos).Single();
        Assert.True(loc.Uri.EndsWith("macros.html", StringComparison.Ordinal));
        Assert.Equal(0, loc.Range.Start.Line);

        var from = Make("{% from 'macros.html' import card as box %}{{ b@ox('x') }}\n");
        Assert.Contains("(macro) card(title)", HoverOf(from) ?? "");
    }

    [Fact]
    public void AProjectFilterLeadsToItsFunction()
    {
        var r = Make("{{ 3|mon@ey }}\n");
        Assert.True(r.Svc.Definition(r.Uri, r.Pos).Single().Uri.EndsWith("app.py", StringComparison.Ordinal));
        Assert.Contains("Escapes", HoverOf(Make("{{ title|e@scape }}\n")) ?? "");
    }

    // ------------------------------------------------------------ completion

    static List<string> Labels(Rig r) => r.Svc.Completion(r.Uri, r.Pos).Select(c => c.Label).ToList();

    [Fact]
    public void JinjasTagsAreOffered()
    {
        var labels = Labels(Make("{% @"));
        Assert.True(labels.Contains("macro") && labels.Contains("set") && labels.Contains("import") && !labels.Contains("load"));
        Assert.Equal("endmacro", Labels(Make("{% macro m() %}x{% end@")).First());
    }

    [Fact]
    public void FiltersTestsAndLoopMembers()
    {
        var filters = Labels(Make("{{ title|@ }}"));
        Assert.True(filters.Contains("tojson") && filters.Contains("money") && !filters.Contains("date"));
        Assert.True(Labels(Make("{% if x is @ %}")).Contains("cheap"));
        Assert.True(Labels(Make("{% for b in books %}{{ loop.@ }}{% endfor %}")).Contains("revindex"));
    }

    [Fact]
    public void EndpointsMacrosAndStaticFiles()
    {
        Assert.True(Labels(Make("{{ url_for('@') }}")).Contains("book"));
        Assert.True(Labels(Make("{{ url_for('static', filename='@') }}")).Contains("css/app.css"));
        Assert.True(Labels(Make("{% import 'macros.html' as forms %}{{ forms.@ }}")).Contains("input"));
        Assert.True(Labels(Make("{{ ti@ }}")).Contains("title"));
    }

    // ------------------------------------------------------------ which templates are Jinja

    [Fact]
    public void ADjangoProjectsJinja2FolderIsJinja()
    {
        var svc = new LanguageService();
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_jinja_" + Guid.NewGuid().ToString("N"));
        svc.AddSourceRoot(dir);
        svc.Open(LanguageService.UriOfPath(Path.Combine(dir, "views.py")), "from django.shortcuts import render\n", 1);
        string jinja = LanguageService.UriOfPath(Path.Combine(dir, "shop", "jinja2", "page.html"));
        string plain = LanguageService.UriOfPath(Path.Combine(dir, "shop", "templates", "page.j2"));
        svc.Open(jinja, "{% load static %}\n", 1);
        svc.Open(plain, "{% set x = 1 %}{{ x }}\n", 1);
        Assert.True(svc.Diagnostics(jinja).Any(d => d.Code == "jinja-tag"));
        Assert.Equal(0, svc.Diagnostics(plain).Count);
    }

    // ------------------------------------------------------------ macro calls (item 145); each case was rendered with jinja2 3.1.6

    [Fact]
    public void MacroCallsThatJinjaRejectsAreErrors()
    {
        var r = Make(
            "{% import 'macros.html' as forms %}\n{% from 'macros.html' import input as field, card %}\n" +
            "{% macro m(a, b=2) %}[{{ a }}|{{ b }}]{% endmacro %}\n" +
            "{{ m(1, 2, 3) }}\n{{ m(1, c=3) }}\n{{ m(1, a=5) }}\n{{ forms.input('q', 'v', 'text', 'x') }}\n{{ field('q', size=3) }}\n{% call m(1) %}body{% endcall %}\n");
        var d = DiagnosticsOf(r, "jinja-macro-call");
        Assert.Equal(
            "4: jinja-macro-call macro 'm' takes not more than 2 argument(s), and 3 are given\n" +
            "5: jinja-macro-call macro 'm' has no parameter 'c' (it takes a, b)\n" +
            "6: jinja-macro-call 'a' is already given as the first argument: Jinja stops with \"macro 'm' takes no keyword argument 'a'\"\n" +
            "7: jinja-macro-call macro 'input' takes not more than 3 argument(s), and 4 are given\n" +
            "8: jinja-macro-call macro 'input' has no parameter 'size' (it takes name, value, type)\n" +
            "9: jinja-macro-call macro 'm' never calls caller(), so {% call %} cannot give it a body: Jinja stops with \"invoked with two values for the special caller argument\"",
            Messages(d.OrderBy(x => x.Range.Start.Line)));
        Assert.True(d.All(x => x.Severity == Severity.Error));
    }

    [Fact]
    public void ALeftOutParameterIsAWarningAndWhatJinjaAcceptsIsNotReported()
    {
        var r = Make(
            "{% from 'macros.html' import card %}\n" +
            "{% macro m(a, b=2) %}[{{ a }}|{{ b }}]{% endmacro %}\n{% macro v(a) %}{{ varargs }}{{ kwargs }}{% endmacro %}\n" +
            "{{ m() }}\n{{ m(1) }}{{ m(a=1, b=3) }}{{ m(*[1, 2]) }}{{ v(1, 2, 3, z=4) }}\n{% call card('t') %}x{% endcall %}\n{{ 'a,b'.split(',') }}{{ [1]|join(',') }}\n");
        var d = DiagnosticsOf(r, "jinja-macro-call");
        Assert.Equal("4: jinja-macro-call 'a' of macro 'm' is not given: Jinja passes it undefined, which prints as nothing", Messages(d));
        Assert.Equal(Severity.Warning, d[0].Severity);
    }

    // ------------------------------------------------------------ item 208: expressions read whole, include lists and variables, line statements

    [Fact]
    public void ExpressionsAreReadAsJinjasParserReadsThem()
    {
        var d = DiagnosticsOf(Make("{{ title books }}\n{% if title books %}x{% endif %}\n{{ title + }}\n{% set x = 1 2 %}\n{{ \"}}\" }}\n"), "jinja-syntax").OrderBy(x => x.Range.Start.Line).ToList();
        Assert.True(d.Count == 4, Messages(d));
        Assert.Contains("Jinja expected the end of the print statement, got 'books': an operator or a comma is missing before it", d[0].Message);
        Assert.Contains("Jinja expected the end of the statement block, got 'books'", d[1].Message);
        Assert.Contains("Jinja expected an expression", d[2].Message);
        Assert.Equal(3, d[3].Range.Start.Line);
        // what Jinja does read: slices, tests with an argument, conditionals without else, calls with keywords, filters with arguments, dicts and tuples
        string fine = "{{ books[1:] }}{{ books[::-1] }}{{ title is sameas false }}{{ 3 is divisibleby 3 }}{{ title if title }}{{ title|default('x', true) }}" +
            "{{ {'a': 1, 'b': [1, 2]}['a'] }}{{ (1, 2) }}{{ 1, 2 }}{{ -1 }}{{ not title }}{{ title not in books }}{{ 'a' 'b' }}{{ books.0.title }}{{ range(3)|list|join(', ') }}" +
            "{{ dict(a=1, **{}) }}{{ title is not none }}{{ 1 < 2 < 3 }}{{ 2 ** 3 // 1 % 2 }}{{ title ~ '!' }}\n";
        Assert.Equal(0, DiagnosticsOf(Make(fine), "jinja-syntax").Count);
    }

    [Fact]
    public void AnIncludeOfAListOrAVariable()
    {
        var app = App.Replace("render_template('index.html', books=books, title='Home')", "render_template('index.html', books=books, title='Home', partial='nothere.html', other='macros.html')");
        var r = Make("{% include ['custom.html', 'gone.html'] %}\n{% include ['custom.html', 'base.html'] %}\n{% include partial %}\n{% include other %}\n", app: app);
        var d = DiagnosticsOf(r, "django-template").OrderBy(x => x.Range.Start.Line).ToList();
        Assert.True(d.Count == 3, Messages(d));
        Assert.True(d[0].Message.Contains("None of the templates of this list") && d[1].Message.Contains("'gone.html' is not"), Messages(d));
        Assert.Contains("'partial' is 'nothere.html' (given by", d[2].Message);
        // each name of a list leads to its template, and completes
        var r2 = Make("{% include ['custom.html', 'base@.html'] %}\n", app: app);
        Assert.True(r2.Svc.Definition(r2.Uri, r2.Pos).Single().Uri.EndsWith("base.html", StringComparison.Ordinal));
        var r3 = Make("{% include ['custom.html', 'ma@\n", app: app);
        Assert.True(r3.Svc.Completion(r3.Uri, r3.Pos).Any(i => i.Label == "macros.html"));
    }

    [Fact]
    public void LineStatementsOfTheEnvironment()
    {
        var app = App.Replace("app = Flask(__name__)\n", "app = Flask(__name__)\napp.jinja_env.line_statement_prefix = '#'\napp.jinja_env.line_comment_prefix = '##'\n");
        var r = Make("<ul>\n# for b in books:\n  <li>{{ b.titel }}</li>   ## the title\n# endfor\n</ul>\n# if title\nx\n", app: app);
        var all = DiagnosticsOf(r);
        Assert.True(all.Any(x => x.Message.Contains("Unclosed tag 'if'")), Messages(all));
        Assert.True(all.Any(x => x.Message.Contains("'Book' has no attribute 'titel'")), Messages(all));
        Assert.True(!all.Any(x => x.Message.Contains("'for'")), Messages(all));
    }
}
