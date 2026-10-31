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
