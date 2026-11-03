using PyMagic.Server;
using Range = PyMagic.Server.Range;

namespace PyMagic.Tests;

/// <summary>Django code insight (item 184): dotted paths of the settings checked and followed, and Convert to class-based view.</summary>
public class DjangoCodeInsightTests
{
    const string Root = "file:///c:/proj/";

    static (LanguageService Svc, Position Pos) Project(string file, string marked, params (string Path, string Text)[] more)
    {
        int i = marked.IndexOf('|');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models)
            .With("shop/apps.py", "class AppConfig: ...\n\n\nclass ShopConfig(AppConfig):\n    name = \"shop\"\n    label = \"store\"\n")
            .With("shop/middleware.py", "def timing(get_response):\n    return get_response\n")
            .With("site_cfg/__init__.py", "").With("site_cfg/urls.py", "urlpatterns = []\n").With("site_cfg/wsgi.py", "application = None\n");
        foreach (var (p, t) in more) b.With(p, t);
        b.With(file, text);
        return (b.Service, new LineMap(text).PositionOf(Math.Max(i, 0)));
    }

    // ------------------------------------------------------------ settings

    const string Settings = "site_cfg/settings.py";

    [Fact]
    public void ThePathsOfTheSettingsAreChecked()
    {
        string settings =
            "INSTALLED_APPS = [\n    \"shop.apps.ShopConfig\",\n    \"shopp\",\n    \"shop.apps.NoConfig\",\n]\n" +
            "MIDDLEWARE = [\"shop.middleware.timing\", \"shop.middleware.timer\", \"shop.middleware\"]\n" +
            "ROOT_URLCONF = \"site_cfg.urls\"\nWSGI_APPLICATION = \"site_cfg.wsgi.application\"\nAUTH_USER_MODEL = \"store.Author\"\n";
        var (svc, _) = Project(Settings, settings);
        var d = svc.Diagnostics(Root + Settings).Where(x => x.Code == "django-settings-path").Select(x => x.Message).ToList();
        Assert.Equal(4, d.Count);
        Assert.True(d.Contains("No module 'shopp': it is neither in the project nor installed"));
        Assert.True(d.Any(m => m.StartsWith("'shop.apps' has no 'NoConfig'")));
        Assert.True(d.Contains("'shop.middleware' has no 'timer'"));
        Assert.True(d.Any(m => m.StartsWith("'shop.middleware' is a module")));
    }

    [Fact]
    public void AWrongUserModelOrUrlconfIsReported()
    {
        string settings = "INSTALLED_APPS = [\"shop\"]\nROOT_URLCONF = \"site_cfg.url\"\nAUTH_USER_MODEL = \"shop.Writer\"\n";
        var d = Project(Settings, settings).Svc.Diagnostics(Root + Settings).Where(x => x.Code == "django-settings-path").Select(x => x.Message).ToList();
        Assert.Equal(2, d.Count);
        Assert.True(d.Contains("No module 'site_cfg.url' (found 'site_cfg')"));
        Assert.True(d.Contains("'shop.models' has no model 'Writer': Django raises ImproperlyConfigured"));
        var other = Project(Settings, "INSTALLED_APPS = [\"shop\"]\nAUTH_USER_MODEL = \"accounts.User\"\n").Svc.Diagnostics(Root + Settings).Single(x => x.Code == "django-settings-path");
        Assert.Contains("No app of INSTALLED_APPS has the label 'accounts'", other.Message);
    }

    [Fact]
    public void APathIsFollowed()
    {
        var (svc, pos) = Project(Settings, "INSTALLED_APPS = [\"shop.apps.Shop|Config\"]\nAUTH_USER_MODEL = \"store.Author\"\n");
        var to = svc.Definition(Root + Settings, pos).Single();
        Assert.Equal(Root + "shop/apps.py", to.Uri.Replace("C:", "c:"));
        Assert.Equal(3, to.Range.Start.Line);

        (svc, pos) = Project(Settings, "INSTALLED_APPS = [\"shop.apps.ShopConfig\"]\nAUTH_USER_MODEL = \"store.Au|thor\"\n");
        to = svc.Definition(Root + Settings, pos).Single();
        Assert.True(to.Uri.EndsWith("shop/models.py"));
        Assert.Equal(3, to.Range.Start.Line);

        (svc, pos) = Project(Settings, "ROOT_URLCONF = \"site_cfg.u|rls\"\n");
        Assert.True(svc.Definition(Root + Settings, pos).Single().Uri.EndsWith("site_cfg/urls.py"));
    }

    // ------------------------------------------------------------ Convert to class-based view

    const string Views = "shop/views.py";
    const string Urls = "shop/urls.py";
    const string UrlsText = "from django.urls import path\nfrom shop import views\nfrom shop.views import about\n\nurlpatterns = [\n    path(\"\", views.index, name=\"index\"),\n    path(\"about/\", about, name=\"about\"),\n    path(\"edit/<int:pk>/\", views.edit_view, name=\"edit\"),\n]\n";

    static Dictionary<string, string> Convert(string marked)
    {
        var (svc, pos) = Project(Views, marked, (Urls, UrlsText));
        var action = svc.CodeActions(Root + Views, new Range(pos, pos)).Single(a => a.Title.StartsWith("Convert to class-based view"));
        var result = new Dictionary<string, string>();
        foreach (var (uri, edits) in action.Edit!.Changes)
        {
            string text = uri.EndsWith("views.py") ? marked.Replace("|", "") : UrlsText;
            var lines = new LineMap(text);
            foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
                text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
            result[uri.EndsWith("views.py") ? "views" : "urls"] = text;
        }
        return result;
    }

    [Fact]
    public void ARenderingFunctionBecomesATemplateView()
    {
        string views = "from django.shortcuts import render\nfrom shop.models import Book\n\n\ndef in|dex(request):\n    \"\"\"The front page.\"\"\"\n    books = Book.objects.filter(author__name=request.GET.get(\"a\"))\n    return render(request, \"shop/index.html\", {\"books\": books})\n";
        var r = Convert(views);
        Assert.Equal(
            "from django.shortcuts import render\nfrom shop.models import Book\nfrom django.views.generic import TemplateView\n\n\n" +
            "class IndexView(TemplateView):\n    \"\"\"The front page.\"\"\"\n    template_name = \"shop/index.html\"\n\n" +
            "    def get_context_data(self, **kwargs):\n        context = super().get_context_data(**kwargs)\n        request = self.request\n" +
            "        books = Book.objects.filter(author__name=request.GET.get(\"a\"))\n        context.update({\"books\": books})\n        return context\n", r["views"]);
        Assert.Contains("path(\"\", views.IndexView.as_view(), name=\"index\")", r["urls"]);
    }

    [Fact]
    public void ATemplateOnlyViewKeepsJustItsTemplateAndTheImportOfItsName()
    {
        string views = "from django.shortcuts import render\nfrom django.contrib.auth.decorators import login_required\n\n\n@login_required\ndef |about(request):\n    return render(request, \"shop/about.html\")\n";
        var r = Convert(views);
        Assert.Contains("@method_decorator(login_required, name=\"dispatch\")\nclass AboutView(TemplateView):\n    template_name = \"shop/about.html\"\n", r["views"]);
        Assert.Contains("from django.views.generic import TemplateView\nfrom django.utils.decorators import method_decorator\n", r["views"]);
        Assert.Contains("from shop.views import AboutView\n", r["urls"]);
        Assert.Contains("path(\"about/\", AboutView.as_view(), name=\"about\")", r["urls"]);
    }

    [Fact]
    public void APostSplitBecomesGetAndPost()
    {
        string views = "from django.shortcuts import render, redirect\n\n\ndef edit_vi|ew(request, pk):\n    if request.method == \"POST\":\n        # save it\n        return redirect(\"index\")\n    else:\n        return render(request, \"shop/edit.html\", {\"pk\": pk})\n";
        var r = Convert(views);
        Assert.Contains(
            "class EditView(View):\n    def get(self, request, pk):\n        return render(request, \"shop/edit.html\", {\"pk\": pk})\n\n" +
            "    def post(self, request, pk):\n        # save it\n        return redirect(\"index\")\n", r["views"]);
        Assert.Contains("from django.views import View\n", r["views"]);
        Assert.Contains("views.EditView.as_view()", r["urls"]);
    }

    [Fact]
    public void TheOtherUsesOfTheFunctionFollow()
    {
        // the urls with an app_name, a test that calls the view, reverse() and redirect() by the function, another view that uses it
        string urls = "from django.urls import path\nfrom shop import views\n\napp_name = \"shop\"\nurlpatterns = [\n    path(\"\", views.index, name=\"index\"),\n]\n";
        string tests = "from django.urls import reverse\nfrom shop import views\nfrom shop.views import index\nfrom shop.views import index as front\n\n\n" +
            "def test_index(rf):\n    assert views.index(rf.get(\"/\")).status_code == 200\n    assert index(rf.get(\"/\"), page=2)\n    assert front(rf.get(\"/\"))\n    assert reverse(views.index) == \"/\"\n";
        string views = "from django.shortcuts import render, redirect\n\n\ndef in|dex(request):\n    return render(request, \"shop/index.html\")\n\n\ndef old(request):\n    return redirect(index)\n";
        var (svc, pos) = Project(Views, views, (Urls, urls), ("shop/tests.py", tests));
        var action = svc.CodeActions(Root + Views, new Range(pos, pos)).Single(a => a.Title.StartsWith("Convert to class-based view"));
        var texts = new Dictionary<string, string> { ["views.py"] = views.Replace("|", ""), ["urls.py"] = urls, ["tests.py"] = tests };
        var r = new Dictionary<string, string>();
        foreach (var (uri, edits) in action.Edit!.Changes)
        {
            string key = texts.Keys.Single(k => uri.EndsWith("/" + k));
            string text = texts[key];
            var lines = new LineMap(text);
            foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
                text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
            r[key] = text;
        }
        Assert.Contains("path(\"\", views.IndexView.as_view(), name=\"index\")", r["urls.py"]);
        Assert.Contains("assert views.IndexView.as_view()(rf.get(\"/\")).status_code == 200\n", r["tests.py"]);
        Assert.Contains("assert IndexView.as_view()(rf.get(\"/\"), page=2)\n", r["tests.py"]);
        Assert.Contains("from shop.views import IndexView\nfrom shop.views import IndexView as front\n", r["tests.py"]);
        Assert.Contains("assert front.as_view()(rf.get(\"/\"))\n", r["tests.py"]);
        Assert.Contains("assert reverse(\"shop:index\") == \"/\"\n", r["tests.py"]);
        Assert.Contains("class IndexView(TemplateView):", r["views.py"]);
        Assert.Contains("    return redirect(\"shop:index\")\n", r["views.py"]);
    }

    [Fact]
    public void AFunctionThatReadsTheMethodOtherwiseIsNotOffered()
    {
        string views = "from django.http import HttpResponse\n\n\ndef in|dex(request):\n    x = 1\n    if request.method == \"PUT\":\n        x = 2\n    return HttpResponse(x)\n";
        var (svc, pos) = Project(Views, views, (Urls, UrlsText));
        Assert.True(!svc.CodeActions(Root + Views, new Range(pos, pos)).Any(a => a.Title.StartsWith("Convert to class-based view")));
    }
}
