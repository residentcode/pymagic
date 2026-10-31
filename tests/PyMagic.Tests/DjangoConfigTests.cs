using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The Django page of the settings, template folders and TEMPLATES['DIRS'] (DjangoConfig.cs, DjangoProject.cs, ProjectFolders.cs).</summary>
public class DjangoConfigTests
{
    static string Folder()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_djcfg_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    static void Write(string root, string rel, string text)
    {
        string full = Path.Combine(root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, text);
    }

    [Fact]
    public void TheTemplateLanguageOfEachFileType()
    {
        var plain = new DjangoConfig();
        Assert.Equal("django", plain.LanguageOf(".html"));
        Assert.Equal("jinja", plain.LanguageOf(".J2"));
        Assert.Equal("none", plain.LanguageOf(".py"));
        var jinjaHtml = new DjangoConfig(TemplateLanguages: new() { [".html"] = "jinja", [".htm"] = "none" });
        Assert.Equal("jinja", jinjaHtml.LanguageOf(".html"));
        Assert.Equal("none", jinjaHtml.LanguageOf(".htm"));
        Assert.Equal("none", new DjangoConfig(Enabled: false).LanguageOf(".html"));   // Django off: HTML is plain
        Assert.Equal("jinja", new DjangoConfig(Enabled: false).LanguageOf(".jinja"));
    }

    [Fact]
    public void TheSettingsAreKeptCleanAndUsedToFindTheProject()
    {
        string root = Folder();
        try
        {
            Write(root, "site/manage.py", "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'cfg.settings')\nimport django\n");
            Write(root, "other/manage.py", "import django\n");
            var saved = DjangoConfig.Save(root, new DjangoConfig(Settings: "cfg.dev; rm", Manage: "../x/manage.py", Env: new() { ["GOOD"] = "1", ["bad name"] = "2" }, TemplateLanguages: new() { [".HTML"] = "jinja", [".py"] = "jinja", [".htm"] = "erb" }));
            Assert.True(saved.Settings == null && saved.Manage == null, "a module name and a path inside the project only");
            Assert.Equal("GOOD", string.Join(",", saved.Env!.Keys));
            Assert.Equal(".html=jinja", string.Join(",", saved.TemplateLanguages!.Select(kv => kv.Key + "=" + kv.Value)));
            Assert.Equal("jinja", DjangoConfig.Load(root).TemplateLanguages![".html"]);

            Assert.Equal("cfg.settings", DjangoProject.Scan(root, new DjangoConfig(Root: "site")).SettingsModule);
            var chosen = DjangoProject.Scan(root, new DjangoConfig(Manage: "other/manage.py", Settings: "mine.settings"));
            Assert.Equal("other/manage.py", chosen.Manage);
            Assert.Equal("mine.settings", chosen.SettingsModule);
            Assert.True(!DjangoProject.Scan(root, new DjangoConfig(Enabled: false)).Found, "Django support off");
        }
        finally { DjangoConfig.Current = new(); Directory.Delete(root, true); }
    }

    [Fact]
    public void TheFoldersOfTemplatesDirsAreFound()
    {
        string root = Folder();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, "site_templates"));
            Directory.CreateDirectory(Path.Combine(root, "web", "tpl"));
            Write(root, "manage.py", "os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'cfg.settings')\nimport django\n");
            Write(root, "cfg/settings.py", "TEMPLATES = [\n    {\n        'BACKEND': 'django.template.backends.django.DjangoTemplates',\n        'DIRS': [BASE_DIR / 'site_templates', os.path.join(BASE_DIR, 'web', 'tpl'), BASE_DIR / 'missing'],\n        'APP_DIRS': True,\n    },\n]\n");
            Assert.Equal("site_templates,web/tpl", string.Join(",", DjangoProject.Scan(root, new DjangoConfig()).TemplateDirs!));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheStructureListsModelsAdminsAndViews()
    {
        var svc = DjangoFixture.Service()
            .With("django/contrib/__init__.py", "")
            .With("django/contrib/admin/__init__.py", "class ModelAdmin:\n    pass\n\n\nclass AdminSite:\n    def register(self, model, admin_class=None): ...\n\n\nsite = AdminSite()\n\n\ndef register(*models):\n    def wrap(cls):\n        return cls\n    return wrap\n")
            .With("django/views/__init__.py", "class View:\n    pass\n")
            .With("django/views/generic/__init__.py", "from django.views import View\n\n\nclass ListView(View):\n    pass\n")
            .With("shop/models.py", DjangoFixture.Models + "\n\nclass Base(models.Model):\n    class Meta:\n        abstract = True\n")
            .With("shop/admin.py", "from django.contrib import admin\n\nfrom .models import Author, Book, Tag\n\n\n@admin.register(Book)\nclass BookAdmin(admin.ModelAdmin):\n    pass\n\n\nclass AuthorAdmin(admin.ModelAdmin):\n    pass\n\n\nadmin.site.register(Author, AuthorAdmin)\nadmin.site.register([Tag])\n")
            .With("shop/views.py", "from django.views.generic import ListView\n\n\ndef index(request):\n    return None\n\n\ndef _helper(request):\n    return None\n\n\ndef util(x):\n    return x\n\n\nclass BookList(ListView):\n    pass\n").Service;
        var items = svc.DjangoStructure();
        string Show(DjangoStructureItem i) => i.Kind + ":" + i.Name + (i.Model != null ? "->" + i.Model : "") + (i.Kind == "model" ? (i.Registered ? "+" : "-") : "");
        Assert.Equal("admin:AuthorAdmin->Author admin:BookAdmin->Book model:Author+ model:Book+ model:Profile- model:Tag+ view:BookList view:index", string.Join(" ", items.Select(Show)));
        Assert.True(items.Single(i => i.Name == "BookAdmin").Uri.EndsWith("shop/admin.py"));
        Assert.Equal(6, items.Single(i => i.Name == "BookAdmin").Line);
    }

    [Fact]
    public void TheLogicalStructureOfAFile()
    {
        string code =
            "from django.db import models\nfrom django.dispatch import receiver\nfrom django.db.models.signals import pre_save\nfrom rest_framework import serializers\nfrom django import forms\nfrom django.views import View\n\n\n" +
            "class Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
            "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n    tags = models.ManyToManyField(\"Tag\")\n\n\n" +
            "class AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = ['name']\n\n\n" +
            "class BookSerializer(serializers.ModelSerializer):\n    author = AuthorSerializer()\n    reviews = ReviewSerializer(many=True)\n    title = serializers.CharField()\n\n    class Meta:\n        model = Book\n\n\n" +
            "class BookForm(forms.ModelForm):\n    note = forms.CharField()\n\n    class Meta:\n        model = Book\n\n\n" +
            "class BookList(View):\n    pass\n\n\n" +
            "@receiver(pre_save, sender=Book)\ndef stamp(sender, instance, **kwargs):\n    pass\n\n\ndef helper():\n    pass\n";
        var svc = DjangoFixture.Service().With("shop/models.py", code)
            .With("shop/admin.py", "from django.contrib import admin\nfrom .models import Author\n\nadmin.site.register(Author)\n")
            .With("shop/urls.py", "from django.urls import path\nfrom .models import BookList\n\nurlpatterns = [path('books/', BookList.as_view(), name='books')]\n").Service;
        var nodes = svc.LogicalStructure(DjangoFixture.Root + "shop/models.py");
        string Show(LogicalNode n) => $"{n.Kind}:{n.Name}" + (n.Detail != null ? $"({n.Detail})" : "") + (n.Children.Count > 0 ? "[" + string.Join(" ", n.Children.Select(Show)) + "]" : "");
        Assert.Equal("model:Author[field:name(CharField)] " +
            "model:Book[field:title(CharField) relation:author(ForeignKey → Author) relation:tags(ManyToManyField → Tag)] " +
            "serializer:AuthorSerializer(of Author) " +
            "serializer:BookSerializer(of Book)[nested:author(AuthorSerializer) nested:reviews(ReviewSerializer (many)) field:title(CharField)] " +
            "form:BookForm(of Book)[field:note(CharField)] view:BookList signal:stamp(pre_save of Book)",
            System.Text.RegularExpressions.Regex.Replace(string.Join(" ", nodes.Select(Show)), @"view:BookList\[[^\]]*\]", "view:BookList"));
        var author = nodes.Single(n => n.Name == "Author");
        Assert.True(author.HasAdmin && author.HasSerializer);
        var book = nodes.Single(n => n.Name == "Book");
        Assert.True(!book.HasAdmin && book.HasSerializer);
    }

    [Fact]
    public void EndpointsOfLibrariesAndOfTheTests()
    {
        var svc = DjangoFixture.Service()
            .With("site_cfg/urls.py", "from django.contrib import admin\nfrom django.urls import include, path\n\nurlpatterns = [\n    path('admin/', admin.site.urls),\n    path('api-auth/', include('rest_framework.urls')),\n]\n")
            .With("shop/tests.py", "from django.test import TestCase\n\n\nclass Pages(TestCase):\n    def test_home(self):\n        self.client.get('/shop/')\n        self.client.post('/shop/items/', {'name': 'x'})\n        self.client.get(reverse('home'))\n").Service;
        var routes = svc.Routes();
        string Show(RouteEntry r) => $"{r.Framework}:{r.Methods} {r.Path} {r.Name}";
        var mine = routes.Where(r => r.Framework is "library" or "test").Select(Show).OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal("library:ANY /admin/… admin.site.urls | library:ANY /api-auth/… rest_framework.urls | test:GET /shop/ test_home | test:POST /shop/items/ test_home", string.Join(" | ", mine));
        Assert.Equal(5, routes.Single(r => r.Path == "/shop/").Line);   // 0-based: the sixth line
    }

    [Fact]
    public void ViewsAndTemplatesAreLinkedBothWaysAndAMissingTemplateCanBeMade()
    {
        string root = Folder();
        try
        {
            Write(root, "shop/templates/shop/list.html", "<ul></ul>\n");
            Write(root, "shop/__init__.py", "");
            string views = "from django.shortcuts import render\nfrom django.views.generic import TemplateView\n\n\ndef items(request):\n    return render(request, 'shop/list.html')\n\n\nclass About(TemplateView):\n    template_name = 'shop/list.html'\n\n\ndef gone(request):\n    return render(request, 'shop/gone.html')\n";
            Write(root, "shop/views.py", views);
            var svc = new LanguageService();
            svc.AddSourceRoot(root);
            foreach (var (u, t) in DjangoFixture.LibraryFiles()) svc.Open(u, t, 1);
            string viewsUri = LanguageService.UriOfPath(Path.Combine(root, "shop", "views.py"));
            string templateUri = LanguageService.UriOfPath(Path.Combine(root, "shop", "templates", "shop", "list.html"));
            svc.Open(viewsUri, views, 1);
            svc.Open(templateUri, "<ul></ul>\n", 1);

            var fromView = svc.TemplateLinks(viewsUri);
            Assert.Equal("5:shop/list.html 9:shop/list.html", string.Join(" ", fromView.Select(l => $"{l.Line}:{l.Label}")));
            Assert.True(fromView[0].Uri.EndsWith("templates/shop/list.html"));
            var fromTemplate = svc.TemplateLinks(templateUri);
            Assert.Equal("items (shop.views)@5 About (shop.views)@9", string.Join(" ", fromTemplate.Select(l => $"{l.Label}@{l.TargetLine}")));

            Assert.Equal(Path.Combine(root, "shop", "templates"), string.Join(",", svc.TemplateFolders()));
            var create = svc.CodeActions(viewsUri, new PyMagic.Server.Range(new Position(13, 25), new Position(13, 25))).Single(a => a.Title.StartsWith("Create template"));
            Assert.Equal("Create template 'shop/gone.html'", create.Title);
            Assert.Equal("pymagic.createTemplate", create.Command!.Command);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TemplatesHaveTheirOwnLiveTemplatesAndTheProjectsOnes()
    {
        string root = Folder();
        try
        {
            Write(root, ".pymagic/snippets.json", "{\"card\": {\"prefix\": \"card\", \"scope\": \"django-html\", \"body\": [\"<div class=\\\"card\\\">$0</div>\"]}, \"main\": {\"prefix\": \"pymain\", \"body\": \"x\"}}");
            Write(root, "templates/page.html", "<div>\n</div>\n");
            var svc = new LanguageService();
            svc.AddSourceRoot(root);
            string uri = LanguageService.UriOfPath(Path.Combine(root, "templates", "page.html"));
            string Labels(string text, int line, int col) { svc.Open(uri, text, 1); return string.Join(",", svc.Completion(uri, new Position(line, col)).Select(i => i.Label).OrderBy(x => x)); }
            Assert.Equal("for,fore", Labels("<div>\nfo\n</div>\n", 1, 2));
            Assert.Equal("card", Labels("<div>\nca\n</div>\n", 1, 2));
            Assert.Equal("", Labels("<div cl\n</div>\n", 0, 7));   // inside an HTML tag: nothing
            Assert.True(!Labels("<div>\npym\n</div>\n", 1, 3).Contains("pymain"), "a Python snippet is not offered in a template");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AFolderMarkedAsTemplatesHoldsTemplates()
    {
        string root = Folder();
        try
        {
            Write(root, "pages/home.html", "<p>hi</p>\n");
            var marks = ProjectFolders.Mark(root, "pages", "templates");
            Assert.Equal("pages", string.Join(",", marks.Templates!));
            Assert.Equal("static", string.Join(",", ProjectFolders.Mark(root, "static", "resources").Resources!));
            Assert.Equal("pages", string.Join(",", ProjectFolders.Load(root).Templates!));   // read back from folders.json
            string view = "from django.shortcuts import render\n\n\ndef home(request):\n    return render(request, 'home.html')\n\n\ndef gone(request):\n    return render(request, 'gone.html')\n";
            Write(root, "views.py", view);
            var svc = new LanguageService();
            svc.AddSourceRoot(root);
            foreach (var (u, t) in DjangoFixture.LibraryFiles()) svc.Open(u, t, 1);
            string uri = LanguageService.UriOfPath(Path.Combine(root, "views.py"));
            svc.Open(uri, view, 1);
            var missing = svc.Diagnostics(uri).Where(d => d.Message.Contains("not in any templates folder")).Select(d => d.Message).ToList();
            Assert.Equal("Template 'gone.html' is not in any templates folder of the project", string.Join(" | ", missing));
        }
        finally { ProjectFolders.Mark(root, "pages", "none"); ProjectFolders.Mark(root, "static", "none"); Directory.Delete(root, true); }
    }
}
