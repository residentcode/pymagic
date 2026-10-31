using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The Django project line of item 204: INSTALLED_APPS read and checked, the settings modules to run commands with, custom managers (of the
/// project and of installed apps), and reverse relations of models no open file imports, in the editor and in the project-wide Problems scan.
/// </summary>
public class DjangoProjectAppsTests
{
    static string Folder(Dictionary<string, string> files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_apps_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        return root;
    }

    /// <summary>A service on a folder holding the stand-in Django and the given files, none of them open.</summary>
    static (LanguageService Svc, string Root) OnDisk(Dictionary<string, string> files)
    {
        foreach (var (u, t) in DjangoFixture.LibraryFiles()) files[u.Replace(DjangoFixture.Root, "")] = t;
        string root = Folder(files);
        var svc = new LanguageService();
        svc.SourceRoots.Add(root);
        return (svc, root);
    }

    static string UriIn(string root, string rel) => LanguageService.UriOfPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

    const string Manage = "import os\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'mysite.settings')\nfrom django.core.management import execute_from_command_line\n";

    [Fact]
    public void TheScanReadsInstalledAppsAndTheSettingsModules()
    {
        string root = Folder(new()
        {
            ["manage.py"] = Manage,
            ["mysite/__init__.py"] = "",
            ["mysite/settings/__init__.py"] = "from .base import *\n",
            ["mysite/settings/base.py"] = "DJANGO_APPS = [\n    \"django.contrib.admin\",\n]\nLOCAL_APPS = [\n    \"shop.apps.ShopConfig\",\n]\nINSTALLED_APPS = DJANGO_APPS + LOCAL_APPS\n",
            ["mysite/settings/dev.py"] = "from .base import *\nINSTALLED_APPS += [\"debug_toolbar\"]\n",
            ["shop/__init__.py"] = "", ["shop/apps.py"] = "from django.apps import AppConfig\n\n\nclass ShopConfig(AppConfig):\n    name = \"shop\"\n", ["shop/models.py"] = "",
            ["blog/__init__.py"] = "", ["blog/models.py"] = "",
        });
        var p = DjangoProject.Scan(root, new DjangoConfig());
        Assert.True(p.SettingsChoices!.SequenceEqual(new[] { "mysite.settings", "mysite.settings.base", "mysite.settings.dev" }), string.Join(", ", p.SettingsChoices!));
        Assert.True(p.InstalledApps!.Contains("shop.apps.ShopConfig") && p.InstalledApps!.Contains("debug_toolbar") && p.InstalledApps!.Contains("django.contrib.admin"));
        Assert.Equal(true, p.Apps.Single(a => a.Name == "shop").Installed);
        Assert.Equal(false, p.Apps.Single(a => a.Name == "blog").Installed);
        Assert.Equal("blog", p.Apps.Single(a => a.Name == "blog").Module);
    }

    [Fact]
    public void InstalledAppsIsChecked()
    {
        var svc = DjangoFixture.Service()
            .With("django/apps/__init__.py", "class AppConfig: ...\n")
            .With("mysite/__init__.py", "").With("mysite/settings.py", "LOCAL_APPS = [\n    \"shop.apps.ShopConfig\",\n    \"shop.apps.ShopConfg\",\n    \"nothere\",\n]\nINSTALLED_APPS = [\"django.apps\"] + LOCAL_APPS\n")
            .With("shop/__init__.py", "").With("shop/apps.py", "from django.apps import AppConfig\n\n\nclass ShopConfig(AppConfig):\n    name = \"shop\"\n")
            .With("shop/models.py", DjangoFixture.Models)
            .With("blog/__init__.py", "").With("blog/models.py", "from django.db import models\n\n\nclass Post(models.Model):\n    title = models.CharField(max_length=10)\n\n\nclass Base(models.Model):\n    class Meta:\n        abstract = True\n")
            .Service;
        var settings = svc.Diagnostics(DjangoFixture.Root + "mysite/settings.py").Where(d => d.Code == "django-installed-apps").Select(d => d.Message).ToList();
        Assert.Equal(2, settings.Count);
        Assert.True(settings.Any(m => m.Contains("'shop.apps.ShopConfg'")) && settings.Any(m => m.Contains("'nothere'")), string.Join(" | ", settings));
        var blog = svc.Diagnostics(DjangoFixture.Root + "blog/models.py").Where(d => d.Code == "django-installed-apps").Select(d => d.Message).ToList();
        Assert.Equal(1, blog.Count);
        Assert.Contains("The app 'blog' of Post is not in INSTALLED_APPS", blog[0]);
        Assert.Equal(0, svc.Diagnostics(DjangoFixture.Root + "shop/models.py").Count(d => d.Code == "django-installed-apps"));
    }

    const string Authors = "from django.db import models\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n";
    const string Books = "from django.db import models\nfrom authors.models import Author\n\n\nclass Book(models.Model):\n    title = models.CharField(max_length=100)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE, related_name=\"books\")\n";
    const string View = "from authors.models import Author\n\n\ndef v():\n    a = Author.objects.prefetch_related(\"bokks\")\n    b = Author.objects.prefetch_related(\"books\")\n    c = Author.objects.filter(books__title=\"x\")\n    return a, b, c\n";

    [Fact]
    public void ReverseRelationsOfModelsNothingOpenImportsAreKnown()
    {
        var (svc, root) = OnDisk(new()
        {
            ["authors/__init__.py"] = "", ["authors/models.py"] = Authors, ["books/__init__.py"] = "", ["books/models.py"] = Books,
            ["front/__init__.py"] = "", ["front/views.py"] = View,
        });
        // the project-wide scan: its Django checks ran on a file that is not open, and know the relation books/models.py adds to Author
        var scan = svc.Problems(4).Where(p => p.Uri.EndsWith("front/views.py", StringComparison.Ordinal) && p.Code == "django-field").Select(p => p.Message).ToList();
        Assert.Equal(1, scan.Count);
        Assert.True(scan[0].Contains("'bokks'") && scan[0].Contains("Choices are: books"), scan[0]);
        // the editor, with only the view open
        string uri = UriIn(root, "front/views.py");
        svc.Open(uri, View, 1);
        var editor = svc.Diagnostics(uri).Where(d => d.Code == "django-field").Select(d => d.Message).ToList();
        Assert.Equal(1, editor.Count);
        Assert.Contains("'bokks'", editor[0]);
    }

    [Fact]
    public void CustomManagersOfAnInstalledApp()
    {
        string site = Folder(new()
        {
            ["taglib/__init__.py"] = "",
            ["taglib/models.py"] = "from django.db import models\n\n\nclass TagQuerySet(models.QuerySet):\n    def popular(self):\n        return self\n\n\nclass TagManager(models.Manager):\n    def most_used(self):\n        return self.get_queryset()\n\n\n" +
                "class Tag(models.Model):\n    name = models.CharField(max_length=50)\n    objects = TagManager()\n    popular_ones = TagQuerySet.as_manager()\n    by_queryset = models.Manager.from_queryset(TagQuerySet)()\n",
        });
        var b = DjangoFixture.Service();
        b.Service.LibraryRoots.Add(site);
        string code = "from taglib.models import Tag\n\n\ndef v():\n    a = Tag.objects.most_used().first()\n    b = Tag.popular_ones.first()\n    c = Tag.by_queryset.popular\n    d = Tag.popular_ones.filter(nmae=1)\n    return a, b, c, d\n";
        b.With("front/views.py", code);
        var svc = b.Service;
        string uri = DjangoFixture.Root + "front/views.py";
        Assert.Contains("Tag | None", svc.Hover(uri, new Position(4, 4))!.Contents.Value);
        Assert.Contains("Tag | None", svc.Hover(uri, new Position(5, 4))!.Contents.Value);
        Assert.Contains("-> TagQuerySet", svc.Hover(uri, new Position(6, 4))!.Contents.Value);
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message.Contains("Cannot resolve keyword 'nmae' into a field of Tag")));
    }
}
