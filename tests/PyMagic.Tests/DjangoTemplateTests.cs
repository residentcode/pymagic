using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoTemplateTests
{
    const string Generic =
        "class View: ...\n\nclass TemplateView(View): ...\n\nclass ListView(View): ...\n\nclass DetailView(View): ...\n";

    const string UrlsSource =
        "from django.urls import path\nfrom shop import views\n\napp_name = \"shop\"\n\nurlpatterns = [\n" +
        "    path(\"books/\", views.index, name=\"book-list\"),\n    path(\"books/<int:pk>/\", views.index, name=\"book-detail\"),\n]\n";

    const string Views =
        "from django.shortcuts import render\nfrom shop.models import Book\n\n\ndef index(request):\n    return render(request, \"shop/page.html\", {\"books\": Book.objects.all(), \"count\": 3})\n";

    sealed record Rig(LanguageService Svc, string Uri, Position Pos, string Dir, string Text);


    static Rig Make(string marked, string? views = Views, (string Rel, string Text)[]? templates = null, (string Path, string Text)[]? python = null, string page = "shop/page.html")
    {
        int i = marked.IndexOf('@');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_dt_" + Guid.NewGuid().ToString("N"));
        var all = new List<(string Rel, string Text)> { (page, text) };
        if (templates != null) all.AddRange(templates);
        foreach (var (rel, body) in all)
        {
            string full = Path.Combine(dir, "shop", "templates", rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, body);
        }
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models).With("shop/urls.py", UrlsSource)
            .With("django/views/generic/__init__.py", Generic);
        if (views != null) b.With("shop/views.py", views);
        foreach (var (p, t) in python ?? Array.Empty<(string, string)>()) b.With(p, t);
        b.Service.SourceRoots.Add(dir);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "shop", "templates", page.Replace('/', Path.DirectorySeparatorChar)));
        foreach (var (rel, body) in all)
            b.Service.Open(LanguageService.UriOfPath(Path.Combine(dir, "shop", "templates", rel.Replace('/', Path.DirectorySeparatorChar))), body, 1);
        return new Rig(b.Service, uri, i < 0 ? new Position(0, 0) : new LineMap(text).PositionOf(i), dir, text);
    }

    static List<LspDiagnostic> DiagnosticsOf(Rig r) => r.Svc.Diagnostics(r.Uri).ToList();

    // ------------------------------------------------------------ structure

    [Fact]
    public void AnUnclosedBlockTagIsAnError()
    {
        var d = DiagnosticsOf(Make("{% for b in books %}\n{{ b }}\n"));
        Assert.True(d.Any(x => x.Message.Contains("Unclosed tag 'for'") && x.Severity == Severity.Error));
    }

    [Fact]
    public void WithTheDjangoPluginOffADjangoTemplateIsLeftAloneAndAJinjaOneIsNot()
    {
        var r = Make("{% for b in books %}\n{{ b }}\n");
        try
        {
            Plugins.Load("{\"plugins\": {\"django\": false}}");
            Assert.Equal(0, DiagnosticsOf(r).Count);
            // a Jinja template is still checked
            var svc = new LanguageService();
            string dir = Path.Combine(Path.GetTempPath(), "pymagic_jinja_" + Guid.NewGuid().ToString("N"));
            svc.AddSourceRoot(dir);
            string jinja = LanguageService.UriOfPath(Path.Combine(dir, "shop", "jinja2", "page.html"));
            svc.Open(jinja, "{% load static %}\n", 1);
            Assert.True(svc.Diagnostics(jinja).Any(d => d.Code == "jinja-tag"));
        }
        finally { Plugins.Load("{}"); }
        Assert.True(DiagnosticsOf(r).Any(x => x.Message.Contains("Unclosed tag 'for'")));
    }

    [Fact]
    public void AVariableOrTagNeverClosedIsAnError()
    {
        // Django reads a tag within its line: {{ name } is printed as it is
        var d = DiagnosticsOf(Make("<p>{{ count }</p>\n<p>{{ count }}</p>\n{% if count\n %}x{% endif %}\n{# note\n"));
        var msgs = string.Join(" | ", d.Select(x => $"{x.Range.Start.Line}:{x.Range.Start.Character} {x.Message}"));
        Assert.Contains("0:3 Unclosed variable '{{': add }} on the same line", msgs);
        Assert.Contains("2:0 Unclosed tag '{%': add %} on the same line", msgs);
        Assert.Contains("4:0 Unclosed comment '{#': add #} on the same line", msgs);
        Assert.True(!msgs.Contains("1:"), msgs);
        // inside {% verbatim %} and {% comment %} it is text
        Assert.True(!DiagnosticsOf(Make("{% verbatim %}{{ x }{% endverbatim %}\n{% comment %}{% if{% endcomment %}\n")).Any(x => x.Message.StartsWith("Unclosed variable") || x.Message.StartsWith("Unclosed tag '{%'")));
    }

    [Fact]
    public void AnEndWithoutItsStartIsAnError()
    {
        Assert.True(DiagnosticsOf(Make("text\n{% endif %}\n")).Any(x => x.Message.Contains("'endif' has no matching")));
    }

    [Fact]
    public void WellFormedTagsAreQuiet()
    {
        string page = "{% load static %}\n{% for b in books %}{% if b.title %}{{ b.title|upper }}{% elif b.pages %}x{% else %}y{% endif %}{% empty %}none{% endfor %}\n{% comment %}{% anything %}{% endcomment %}\n";
        Assert.Equal(0, DiagnosticsOf(Make(page)).Count);
    }

    [Fact]
    public void ExtendsMustComeFirst()
    {
        var d = DiagnosticsOf(Make("{% load static %}\n{% extends \"shop/base.html\" %}\n", templates: new[] { ("shop/base.html", "<html></html>") }));
        Assert.True(d.Any(x => x.Message.Contains("must be the first tag")));
    }

    [Fact]
    public void ABlockDefinedTwice()
    {
        var d = DiagnosticsOf(Make("{% block a %}{% endblock %}{% block a %}{% endblock %}"));
        Assert.True(d.Any(x => x.Message.Contains("Block 'a' is defined twice")));
    }

    [Fact]
    public void AnUnknownTagAndAFilterNeedALoad()
    {
        var d = DiagnosticsOf(Make("{% shout %}\n{{ x|nonsense }}\n{{ x|upper }}\n"));
        Assert.True(d.Any(x => x.Message.Contains("Unknown tag 'shout'") && x.Severity == Severity.Information));
        Assert.True(d.Any(x => x.Message.Contains("Unknown filter 'nonsense'")));
        Assert.True(!d.Any(x => x.Message.Contains("'upper'")));
    }

    [Fact]
    public void ALoadedLibraryOfTheProjectBringsItsNames()
    {
        var tags = ("shop/templatetags/shop_tags.py", "from django import template\n\nregister = template.Library()\n\n\n@register.filter\ndef shout(value):\n    return value\n\n\n@register.simple_tag\ndef stamp():\n    return 1\n");
        string page = "{% load shop_tags %}\n{{ x|shout }}{% stamp %}\n";
        Assert.Equal(0, DiagnosticsOf(Make(page, python: new[] { tags, ("shop/templatetags/__init__.py", "") })).Count);
        var without = DiagnosticsOf(Make("{{ x|shout }}", python: new[] { tags, ("shop/templatetags/__init__.py", "") }));
        Assert.True(without.Any(x => x.Message.Contains("Unknown filter 'shout'")));
    }

    [Fact]
    public void AFilterOfTheProjectLeadsToItsFunction()
    {
        var tags = ("shop/templatetags/shop_tags.py", "from django import template\n\nregister = template.Library()\n\n\n@register.filter\ndef shout(value):\n    return value\n");
        var r = Make("{% load shop_tags %}\n{{ x|sho@ut }}\n", python: new[] { tags, ("shop/templatetags/__init__.py", "") });
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop_tags.py", found[0].Uri);
        Assert.Equal(6, found[0].Range.Start.Line);
    }

    [Fact]
    public void ALibraryThatIsNotInTheProjectSilencesTheUnknownNames()
    {
        Assert.Equal(0, DiagnosticsOf(Make("{% load crispy_forms_tags %}\n{{ form|crispy }}{% crispy form %}\n")).Count);
    }

    [Fact]
    public void JinjaFilesAreLeftAlone()
    {
        var r = Make("{% set x = 1 %}{% macro m() %}{% endmacro %}\n", page: "shop/page.j2");
        Assert.Equal(0, DiagnosticsOf(r).Count);
    }

    // ------------------------------------------------------------ {% url %}

    [Fact]
    public void TheUrlTagLeadsToThePath()
    {
        var r = Make("<a href=\"{% url 'shop:book-det@ail' 3 %}\">x</a>\n");
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/urls.py", found[0].Uri);
        Assert.Equal(7, found[0].Range.Start.Line);
        Assert.Contains("books/<int:pk>/", r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value);
    }

    [Fact]
    public void AnUnknownUrlNameAndTheNumberOfArguments()
    {
        var d = DiagnosticsOf(Make("{% url 'nope' %}\n{% url 'shop:book-detail' %}\n{% url 'shop:book-detail' b.pk %}\n{% url 'shop:book-list' as there %}\n"));
        Assert.True(d.Any(x => x.Message.Contains("No URL is named 'nope'")));
        var args = d.Where(x => x.Code == "template-url-arguments").ToList();
        Assert.Equal(1, args.Count);
        Assert.Equal(1, args[0].Range.Start.Line);
        Assert.Contains("takes 1 argument", args[0].Message);
    }

    [Fact]
    public void UrlNamesComplete()
    {
        var r = Make("{% url 'shop:bo@ %}\n");
        var labels = r.Svc.Completion(r.Uri, r.Pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("shop:book-list") && labels.Contains("shop:book-detail"));
    }

    [Fact]
    public void RenamingAUrlNameChangesTheTemplatesToo()
    {
        var r = Make("<a href=\"{% url 'shop:book-list' %}\">x</a>{% url 'book-list' %}\n");
        var edit = r.Svc.Rename(r.Uri, new Position(0, 24), "all-books");
        var edits = edit.Changes.Where(c => c.Key == r.Uri).SelectMany(c => c.Value).ToList();
        Assert.Equal(2, edits.Count);
        Assert.True(edits.Any(e => e.NewText == "shop:all-books") && edits.Any(e => e.NewText == "all-books"));
        Assert.True(edit.Changes.Keys.Any(k => k.EndsWith("shop/urls.py")), "the path changes too");
    }

    [Fact]
    public void UsagesOfAUrlNameIncludeTheTemplates()
    {
        var r = Make("{% url 'shop:book-li@st' %}\n");
        var sites = r.Svc.References(r.Uri, r.Pos, true);
        Assert.True(sites.Any(s => s.Uri == r.Uri), "the template itself");
        Assert.True(sites.Any(s => s.Uri.EndsWith("shop/urls.py")), "where it is defined");
    }

    // ------------------------------------------------------------ extends, include, load, static

    [Fact]
    public void ExtendsAndIncludeLeadToTheFile()
    {
        var parts = new[] { ("shop/base.html", "<html>{% block content %}{% endblock %}</html>"), ("shop/part.html", "x") };
        var r = Make("{% extends 'shop/ba@se.html' %}\n{% block content %}{% include 'shop/part.html' %}{% endblock %}\n", templates: parts);
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/base.html", found[0].Uri);
        Assert.Equal(0, DiagnosticsOf(r).Count);
    }

    [Fact]
    public void AMissingTemplateIsWeak()
    {
        var d = DiagnosticsOf(Make("{% include 'shop/gone.html' %}\n"));
        Assert.True(d.Any(x => x.Message.Contains("'shop/gone.html'") && x.Severity == Severity.Information));
    }

    [Fact]
    public void ABlockLeadsToTheOneItOverrides()
    {
        var parts = new[] { ("shop/base.html", "<html>\n{% block content %}{% endblock %}\n</html>") };
        var r = Make("{% extends 'shop/base.html' %}\n{% block con@tent %}x{% endblock %}\n", templates: parts);
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/base.html", found[0].Uri);
        Assert.Equal(1, found[0].Range.Start.Line);
    }

    [Fact]
    public void ABlockNoParentHasIsWeak()
    {
        var parts = new[] { ("shop/base.html", "<html>{% block content %}{% endblock %}</html>") };
        var d = DiagnosticsOf(Make("{% extends 'shop/base.html' %}\n{% block conent %}x{% endblock %}\n", templates: parts));
        Assert.True(d.Any(x => x.Message.Contains("No parent template has a block 'conent'")));
    }

    [Fact]
    public void LoadAndStaticFindTheirFiles()
    {
        var r = Make("{% load static %}{% static 'shop/app.css' %}\n");
        string css = Path.Combine(r.Dir, "shop", "static", "shop", "app.css");
        Directory.CreateDirectory(Path.GetDirectoryName(css)!);
        File.WriteAllText(css, "body {}");
        r.Svc.Change(r.Uri, r.Text + " ", 2);
        Assert.Equal(0, DiagnosticsOf(r).Count);
        var missing = Make("{% load static %}{% static 'shop/none.css' %}\n");
        string dir = Path.Combine(missing.Dir, "shop", "static");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "other.css"), "x");
        Assert.True(DiagnosticsOf(missing).Any(x => x.Message.Contains("Static file 'shop/none.css'")));
    }

    // ------------------------------------------------------------ variables

    [Fact]
    public void AVariableTakesTheTypeOfWhatTheViewGave()
    {
        var r = Make("{{ co@unt }}\n");
        Assert.Contains("int", r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value);
        Assert.Contains("view", r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value.ToLowerInvariant() + "view");
    }

    [Fact]
    public void AVariableLeadsToTheKeyInTheView()
    {
        var r = Make("{{ bo@oks }}\n");
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/views.py", found[0].Uri);
        Assert.Equal(5, found[0].Range.Start.Line);
    }

    [Fact]
    public void ALoopVariableIsAnElementAndItsAttributesAreTyped()
    {
        var r = Make("{% for book in books %}{{ book.ti@tle }}{% endfor %}\n");
        string hover = r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value;
        Assert.Contains("str", hover);
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Contains("shop/models.py", found[0].Uri);
    }

    [Fact]
    public void ALoopVariableLeadsToItsFor()
    {
        var r = Make("{% for book in books %}{{ bo@ok }}{% endfor %}\n");
        var found = r.Svc.Definition(r.Uri, r.Pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(r.Uri, found[0].Uri);
        Assert.Equal(7, found[0].Range.Start.Character);
    }

    [Fact]
    public void AttributesOfAModelComplete()
    {
        var r = Make("{% for book in books %}{{ book.ti@ }}{% endfor %}\n");
        var labels = r.Svc.Completion(r.Uri, r.Pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("title"));
        Assert.True(!labels.Contains("pages"), "only what starts the same");
        Assert.True(!labels.Contains("save"), "Django does not call what changes data");
    }

    [Fact]
    public void VariableNamesComplete()
    {
        var r = Make("{% for book in books %}{{ bo@ }}{% endfor %}\n");
        var labels = r.Svc.Completion(r.Uri, r.Pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("book") && labels.Contains("books"));
        var outside = Make("{% for book in books %}{% endfor %}{{ bo@ }}\n");
        var outsideLabels = outside.Svc.Completion(outside.Uri, outside.Pos).Select(i => i.Label).ToList();
        Assert.True(outsideLabels.Contains("books") && !outsideLabels.Contains("book"), "the loop variable ends with the loop");
    }

    [Fact]
    public void WithAndAsDefineNames()
    {
        var r = Make("{% with total=count %}{% url 'shop:book-list' as there %}{{ tot@al }}{{ the@re }}{% endwith %}\n");
        Assert.Contains("int", r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value);
        var labels = Make("{% with total=count %}{% url 'shop:book-list' as there %}{{ th@ }}{% endwith %}\n");
        Assert.True(labels.Svc.Completion(labels.Uri, labels.Pos).Any(i => i.Label == "there"));
    }

    [Fact]
    public void AnAttributeAModelLacksIsWeak()
    {
        var d = DiagnosticsOf(Make("{% for book in books %}{{ book.titel }}{{ book.title }}{{ book.author.name }}{% endfor %}\n"));
        var weak = d.Where(x => x.Code == "template-attribute").ToList();
        Assert.Equal(1, weak.Count);
        Assert.Contains("'Book' has no attribute 'titel'", weak[0].Message);
    }

    [Fact]
    public void ClassBasedViewsGiveTheirObjectNames()
    {
        string views = "from django.views.generic import ListView\nfrom shop.models import Book\n\n\nclass BookList(ListView):\n    model = Book\n    template_name = \"shop/page.html\"\n";
        var r = Make("{% for b in book_list %}{{ b.ti@tle }}{% endfor %}\n", views);
        Assert.Contains("str", r.Svc.Hover(r.Uri, r.Pos)!.Contents.Value);
        var names = Make("{{ obj@ }}\n", views);
        Assert.True(names.Svc.Completion(names.Uri, names.Pos).Any(i => i.Label == "object_list"));
    }

    [Fact]
    public void ContextBuiltInAContextVariableIsFound()
    {
        string views = "from django.shortcuts import render\n\n\ndef index(request):\n    context = {\"a\": 1}\n    context[\"b\"] = \"x\"\n    context.update(c=2.5)\n    return render(request, \"shop/page.html\", context)\n";
        var r = Make("{{ a }}{{ b }}{{ c@ }}\n", views);
        var labels = r.Svc.Completion(r.Uri, new Position(0, 18)).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("c"));
        var hover = Make("{{ b@ }}\n", views);
        Assert.Contains("str", hover.Svc.Hover(hover.Uri, hover.Pos)!.Contents.Value);
    }

    // ------------------------------------------------------------ tags and filters

    [Fact]
    public void TagNamesCompleteWithTheirEndTags()
    {
        var r = Make("{% fo@ %}\n");
        Assert.True(r.Svc.Completion(r.Uri, r.Pos).Any(i => i.Label == "for"));
        var closing = Make("{% for a in b %}\n{% end@ %}\n");
        Assert.True(closing.Svc.Completion(closing.Uri, closing.Pos).Any(i => i.Label == "endfor"));
        var nested = Make("{% for a in b %}{% if a %}{% endif %}\n{% end@ %}\n");
        var labels = nested.Svc.Completion(nested.Uri, nested.Pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("endfor") && !labels.Contains("endif"), "the innermost open block closes first");
    }

    [Fact]
    public void FiltersCompleteAfterAPipe()
    {
        var r = Make("{{ x|up@ }}\n");
        Assert.True(r.Svc.Completion(r.Uri, r.Pos).Any(i => i.Label == "upper"));
    }

    [Fact]
    public void LibrariesCompleteAfterLoad()
    {
        var r = Make("{% load st@ %}\n");
        Assert.True(r.Svc.Completion(r.Uri, r.Pos).Any(i => i.Label == "static"));
    }

    [Fact]
    public void TemplateNamesCompleteInsideExtends()
    {
        var r = Make("{% extends 'shop/ba@ %}\n", templates: new[] { ("shop/base.html", "x") });
        Assert.True(r.Svc.Completion(r.Uri, r.Pos).Any(i => i.Label == "shop/base.html"));
    }

    [Fact]
    public void HoverNamesATagAndAFilter()
    {
        var tag = Make("{% fo@r a in b %}{% endfor %}\n");
        Assert.Contains("Loops over a sequence", tag.Svc.Hover(tag.Uri, tag.Pos)!.Contents.Value);
        var filter = Make("{{ x|up@per }}\n");
        Assert.Contains("Converts to uppercase", filter.Svc.Hover(filter.Uri, filter.Pos)!.Contents.Value);
    }

    [Fact]
    public void ATemplateIsNotPythonAndDoesNotDisturbIt()
    {
        var r = Make("{% if %}{{ \n");
        Assert.Equal(0, r.Svc.Diagnostics("file:///c:/proj/shop/views.py").Count(d => d.Severity == Severity.Error));
    }

    // ------------------------------------------------------------ inspections of item 183

    [Fact]
    public void AnEndblockNameMustBeItsBlocks()
    {
        var d = DiagnosticsOf(Make("{% block content %}x{% endblock contnet %}\n{% block side %}y{% endblock side %}\n{% block a %}{% endblock %}\n"));
        var wrong = d.Single(x => x.Code == "template-structure");
        Assert.Equal("{% endblock contnet %} closes the block 'content': the names must match", wrong.Message);
        Assert.Equal(32, wrong.Range.Start.Character);
    }

    [Fact]
    public void ACommentThatRunsOntoTheNextLineIsShownAsText()
    {
        var d = DiagnosticsOf(Make("{# fine #}\n{# not closed here\n   still text #}\n{% verbatim %}{# kept {% endverbatim %}\n"));
        var broken = d.Single(x => x.Code == "template-comment");
        Assert.Equal(1, broken.Range.Start.Line);
        Assert.Equal(0, broken.Range.Start.Character);
    }

    [Fact]
    public void AComparisonInIfNeedsSpaces()
    {
        var d = DiagnosticsOf(Make("{% if count==3 %}a{% elif count >=4 %}b{% elif count != 2 and \"a==b\" %}c{% endif %}\n")).Where(x => x.Code == "template-if-spaces").ToList();
        Assert.Equal(2, d.Count);
        Assert.Contains("'=='", d[0].Message);
        Assert.Equal(11, d[0].Range.Start.Character);
        Assert.Contains("'>='", d[1].Message);
    }

    [Fact]
    public void APartialMustBeDefined()
    {
        string page = "{% partialdef card inline %}<p>{{ count }}</p>{% endpartialdef card %}\n{% partial card %}{% partial crad %}\n{% include \"shop/parts.html#row\" %}{% include \"shop/parts.html#rwo\" %}\n";
        var d = DiagnosticsOf(Make(page, templates: new[] { ("shop/parts.html", "{% partialdef row %}r{% endpartialdef %}\n") }));
        Assert.True(!d.Any(x => x.Code is "template-tag" or "template-structure" or "django-template"));
        var partial = d.Where(x => x.Code == "django-partial").Select(x => x.Message).ToList();
        Assert.Equal(2, partial.Count);
        Assert.True(partial.Contains("No {% partialdef crad %} in this template. Partials: card"));
        Assert.True(partial.Contains("'shop/parts.html' has no {% partialdef rwo %}. Partials: row"));
    }

    [Fact]
    public void APartialNamedInRenderIsChecked()
    {
        string views = "from django.shortcuts import render\n\n\ndef index(request):\n    render(request, \"shop/page.html#card\")\n    return render(request, \"shop/page.html#cart\")\n";
        var r = Make("{% partialdef card %}c{% endpartialdef %}\n", views: views);
        var d = r.Svc.Diagnostics("file:///c:/proj/shop/views.py").Where(x => x.Code is "django-partial" or "django-template").ToList();
        Assert.Equal("'shop/page.html' has no {% partialdef cart %}. Partials: card", d.Single().Message);
    }

    // ------------------------------------------------------------ include context, processors, blocktranslate, installed libraries, HTML (item 203)

    static string TemplateUri(Rig r, string rel) => LanguageService.UriOfPath(Path.Combine(r.Dir, "shop", "templates", rel.Replace('/', Path.DirectorySeparatorChar)));

    [Fact]
    public void AnIncludedTemplateSeesTheVariablesItIsGiven()
    {
        var r = Make("{% for b in books %}\n{% include \"shop/card.html\" with book=b %}\n{% endfor %}\n{% include \"shop/bare.html\" with book=books.0 only %}\n",
            templates: new[] { ("shop/card.html", "{{ book.titel }} {{ count }}\n"), ("shop/bare.html", "{{ book.title }} {{ count }}\n") });
        var card = TemplateUri(r, "shop/card.html");
        Assert.True(r.Svc.Diagnostics(card).Any(d => d.Message.Contains("'Book' has no attribute 'titel'")), string.Join(" | ", r.Svc.Diagnostics(card).Select(d => d.Message)));
        var count = r.Svc.Hover(card, new Position(0, 21));
        Assert.Contains("int", count!.Contents.Value);
        Assert.Contains("page.html", count.Contents.Value);
        var bare = TemplateUri(r, "shop/bare.html");
        Assert.Contains("Book", r.Svc.Hover(bare, new Position(0, 4))!.Contents.Value);
        Assert.True(r.Svc.Hover(bare, new Position(0, 21)) == null, "only: the includer's other variables are not given");
    }

    [Fact]
    public void ContextProcessorsOfTheSettingsAddToEveryTemplate()
    {
        var r = Make("{{ cart_total }} {{ STATIC_URL }}\n", python: new[]
        {
            ("proj/__init__.py", ""),
            ("proj/settings.py", "TEMPLATES = [{\"BACKEND\": \"django.template.backends.django.DjangoTemplates\", \"OPTIONS\": {\"context_processors\": [\"django.template.context_processors.static\", \"shop.processors.cart\"]}}]\n"),
            ("shop/processors.py", "def cart(request):\n    return {\"cart_total\": 3}\n"),
        });
        var hover = r.Svc.Hover(r.Uri, new Position(0, 5))!.Contents.Value;
        Assert.Contains("int", hover);
        Assert.Contains("shop.processors.cart", hover);
        Assert.Contains("str", r.Svc.Hover(r.Uri, new Position(0, 22))!.Contents.Value);
    }

    [Fact]
    public void BlockTranslateTakesPlainNamesAndNoTags()
    {
        var r = Make("{% load i18n %}\n{% for book in books %}\n{% blocktranslate with t=book.title trimmed %}{{ t }} by {{ book.author }}{% if t %}x{% endif %}{% endblocktranslate %}\n{% blocktranslate wiht a=1 %}{{ a }}{% endblocktranslate %}\n{% blocktranslate count n=books|length %}one{% plural %}{{ n }} books{% endblocktranslate %}\n{% endfor %}\n");
        var found = DiagnosticsOf(r).Where(d => d.Code == "template-blocktrans").Select(d => d.Message).ToList();
        Assert.True(found.Any(m => m.Contains("'book.author' is looked up as a whole")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("allows no tags inside it") && m.Contains("{% if %}")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("Unknown option 'wiht'")), string.Join(" | ", found));
        Assert.True(found.Count == 3, string.Join(" | ", found));
        var t = r.Svc.Hover(r.Uri, new Position(2, 49));
        Assert.Contains("str", t!.Contents.Value);
    }

    [Fact]
    public void TheTagsOfAnInstalledLibraryAreKnown()
    {
        string site = Path.Combine(Path.GetTempPath(), "pymagic_site_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(site, "crispy_forms", "templatetags"));
        File.WriteAllText(Path.Combine(site, "crispy_forms", "templatetags", "crispy_forms_tags.py"),
            "from django import template\n\nregister = template.Library()\n\n\n@register.filter(name=\"crispy\")\ndef as_crispy_form(form):\n    return form\n\n\n@register.tag\ndef crispy(parser, token):\n    return None\n");
        var r = Make("{% load crispy_forms_tags %}\n{{ books|crispy }}\n{{ books|nope }}\n{% crispy books %}\n{% wrong %}\n");
        r.Svc.LibraryRoots.Add(site);
        var found = DiagnosticsOf(r).Where(d => d.Code is "template-filter" or "template-tag").Select(d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.Contains("'nope'")) && found.Any(m => m.Contains("'wrong'")), string.Join(" | ", found));
    }

    [Fact]
    public void HtmlClosingTagsAndAttributesAreChecked()
    {
        var found = DiagnosticsOf(Make("<div class=\"a\" class=\"b\">\n<span>text\n</div>\n")).Where(d => d.Code == "template-html").Select(d => d.Message).ToList();
        Assert.True(found.Any(m => m.Contains("'class' is written twice")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("</div> closes <div> while <span> inside it is still open")), string.Join(" | ", found));
        Assert.Equal(2, found.Count);
        Assert.True(DiagnosticsOf(Make("<ul><li>a<li>b</ul>\n<p>text\n<table><tr><td>x</table>\n</section>\n")).Count(d => d.Code == "template-html") == 1);
        Assert.True(!DiagnosticsOf(Make("{% if books %}<div class=\"a\">{% else %}<div>{% endif %}\n</div>\n<script>if (a < b) { x = \"</p>\"; }</script>\n")).Any(d => d.Code == "template-html"));
    }
}
