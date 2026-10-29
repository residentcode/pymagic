using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Django as the real package describes itself (its own source, not the small stand-in): `User`, `get_user_model()`, `values()` (item 125). Skipped where Django is not installed.</summary>
public class RealDjangoTests
{
    internal static readonly string? Site = FindSite();

    static string? FindSite()
    {
        var candidates = new[]
        {
            Environment.GetEnvironmentVariable("PYMAGIC_DJANGO_SITE"),
            Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", "PycharmProjects", "django_chat", ".venv", "Lib", "site-packages"),
        };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, "django", "db", "models", "__init__.py")));
    }

    const string Models =
        "from django.db import models\nfrom django.contrib.auth.models import AbstractUser\n\n\n" +
        "class Member(AbstractUser):\n    bio = models.TextField()\n\n\n" +
        "class Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n    rating = models.FloatField(null=True)\n" +
        "    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n\n\n" +
        "class Review(models.Model):\n    owner = models.ForeignKey(\"auth.User\", on_delete=models.CASCADE)\n    text = models.TextField()\n";

    sealed class Project
    {
        public LanguageService Svc = null!;
        public string Uri = "";
        public string Code = "";

        /// <summary>The hover text at <paramref name="delta"/> characters after the first <paramref name="needle"/>, on one line.</summary>
        public string Hover(string needle, int delta)
        {
            int at = Code.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) throw new Exception("not in the code: " + needle);
            return (Svc.Hover(Uri, new LineMap(Code).PositionOf(at + delta))?.Contents.Value ?? "(nothing)").Replace("\n", " ");
        }

        /// <summary>The hover of the name a line assigns: `name = ...`.</summary>
        public string Var(string name) => Hover("\n" + name + " =", 1);

        public List<LspDiagnostic> Problems() => Svc.Diagnostics(Uri).ToList();
    }

    /// <param name="userModel">What the project's settings say AUTH_USER_MODEL is; null for no settings module.</param>
    static Project Open(string code, string? userModel = null, string? models = null)
    {
        models ??= Models;
        string dir = Path.Combine(Path.GetTempPath(), "pm_realdjango_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shop"));
        File.WriteAllText(Path.Combine(dir, "shop", "__init__.py"), "");
        File.WriteAllText(Path.Combine(dir, "shop", "models.py"), models);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(Site!);
        svc.Open(LanguageService.UriOfPath(Path.Combine(dir, "shop", "models.py")), models, 1);
        if (userModel != null)
        {
            string settings = $"AUTH_USER_MODEL = \"{userModel}\"\nINSTALLED_APPS = []\n";
            File.WriteAllText(Path.Combine(dir, "settings.py"), settings);
            svc.Open(LanguageService.UriOfPath(Path.Combine(dir, "settings.py")), settings, 1);
        }
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "shop", "views.py"));
        svc.Open(uri, code, 1);
        return new Project { Svc = svc, Uri = uri, Code = code };
    }

    const string Head = "from django.contrib.auth import get_user_model\nfrom django.db.models import F\nfrom django.contrib.auth.models import User\nfrom django.conf import settings\nfrom shop.models import Book, Review\n";

    // ------------------------------------------------------------ User

    [Fact]
    public void TheUserModelHasItsFieldsAndItsManager()
    {
        if (Site == null) return;
        var p = Open(Head + "\nu = User.objects.get(pk=1)\nname = u.username\njoined = u.date_joined\nstaff = u.is_staff\ncreated = User.objects.create_user('a')\n");
        Assert.Contains("u: User", p.Var("u"));
        Assert.Contains("name: str", p.Var("name"));
        // a datetime where the standard library is described (typeshed), unknown with the small bundled stubs
        Assert.True(p.Var("joined").Contains("joined: datetime") || p.Var("joined").Contains("joined: Unknown"), p.Var("joined"));
        Assert.Contains("staff: bool", p.Var("staff"));
        Assert.Equal(0, p.Problems().Count(d => d.Message.StartsWith("Cannot find reference")));
    }

    [Fact]
    public void ATypoOnAUserIsReported()
    {
        if (Site == null) return;
        var p = Open(Head + "\nu = User.objects.get(pk=1)\nu.emial\n");
        Assert.Equal(1, p.Problems().Count(d => d.Message.Contains("Cannot find reference 'emial'")));
    }

    [Fact]
    public void AQueryOnTheUserIsCheckedForTyposOnly()
    {
        if (Site == null) return;
        var p = Open(Head + "\nUser.objects.filter(usernam='x')\nUser.objects.filter(username='x', groups__name='g', is_staff=True, email__icontains='@')\nUser.objects.filter(auth_token__key='x')\n");
        var found = p.Problems().Where(d => d.Code == "django-field").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("'usernam'", found[0].Message);
    }

    [Fact]
    public void ARelationToAuthUserKnowsTheUser()
    {
        if (Site == null) return;
        var p = Open(Head + "\nr = Review.objects.get(pk=1)\nowner = r.owner\nlogin = r.owner.username\nu = User.objects.get(pk=1)\nreviews = u.review_set\n");
        Assert.Contains("owner: User", p.Var("owner"));
        Assert.Contains("login: str", p.Var("login"));
        Assert.True(!p.Var("reviews").Contains("Unknown"));
        Assert.Equal(0, p.Problems().Count(d => d.Message.StartsWith("Cannot find reference")));
    }

    // ------------------------------------------------------------ get_user_model

    [Fact]
    public void GetUserModelIsDjangosUserWithoutASetting()
    {
        if (Site == null) return;
        var p = Open(Head + "\nM = get_user_model()\nm = M.objects.first()\nM.objects.filter(usernamee='a')\n");
        Assert.Contains("m: User | None", p.Var("m"));
        Assert.Equal(1, p.Problems().Count(d => d.Code == "django-field"));
    }

    [Fact]
    public void GetUserModelIsTheModelTheSettingsName()
    {
        if (Site == null) return;
        var p = Open(Head + "\nM = get_user_model()\nm = M.objects.get(pk=1)\nbio = m.bio\nlogin = m.username\n", userModel: "shop.Member");
        Assert.Contains("m: Member", p.Var("m"));
        Assert.Contains("bio: str", p.Var("bio"));
        Assert.Contains("login: str", p.Var("login"));
    }

    [Fact]
    public void AForeignKeyToTheSettingsUserModelFollowsIt()
    {
        if (Site == null) return;
        string models = Models.Replace("owner = models.ForeignKey(\"auth.User\"", "owner = models.ForeignKey(settings.AUTH_USER_MODEL").Replace("from django.db import models\n", "from django.db import models\nfrom django.conf import settings\n");
        var p = Open(Head + "\nr = Review.objects.get(pk=1)\nbio = r.owner.bio\n", userModel: "shop.Member", models: models);
        Assert.Contains("bio: str", p.Var("bio"));
    }

    // ------------------------------------------------------------ values() and values_list()

    [Fact]
    public void ValuesGivesDictsAndValuesListTuples()
    {
        if (Site == null) return;
        var p = Open(Head + "\nrows = Book.objects.values('title')\nfirst = rows.first()\npairs = Book.objects.values_list('title', 'pages')\nflat = Book.objects.values_list('title', flat=True)\nfor t, n in pairs:\n    pass\nfor f in flat:\n    pass\n");
        Assert.Contains("BookRow(title)", p.Var("first"));
        Assert.Contains("t: str", p.Hover("for t, n", 4));
        Assert.Contains("n: int", p.Hover("for t, n", 7));
        Assert.Contains("f: str", p.Hover("for f in", 4));
    }

    // values() rows typed per key (item 150); the keys checked with Django 6.1

    [Fact]
    public void EachKeyOfAValuesRowHasItsType()
    {
        if (Site == null) return;
        var p = Open(Head + "\nfor row in Book.objects.values('title', 'author', 'author__name', 'pk', 'rating'):\n" +
            "    title = row['title']\n    author = row['author']\n    name = row['author__name']\n    pk = row['pk']\n    rating = row['rating']\n" +
            "every = Book.objects.values().first()\nauthor_id = every['author_id'] if every else 0\n");
        Assert.Contains("title: str", p.Hover("    title =", 5));
        Assert.Contains("author: int", p.Hover("    author =", 5));
        Assert.Contains("name: str", p.Hover("    name =", 5));
        Assert.Contains("pk: int", p.Hover("    pk =", 5));
        Assert.Contains("rating: float | None", p.Hover("    rating =", 5));
        // without arguments: every field, a relation as author_id
        Assert.Contains("BookRow(id, title, pages, rating, author_id)", p.Var("every"));
        Assert.Contains("author_id: int", p.Var("author_id"));
        Assert.Equal("", string.Join("\n", p.Problems().Where(d => d.Severity <= 2).Select(d => d.Message)));
    }

    [Fact]
    public void AKeyTheValuesCallDidNotAskForIsReported()
    {
        if (Site == null) return;
        var p = Open(Head + "\nfor row in Book.objects.values('title'):\n    print(row['pages'])\n\nfor row in Book.objects.values('title', n=F('pages')):\n    print(row['n'])\n");
        var found = p.Problems().Where(d => d.Severity <= 2).Select(d => $"{d.Range.Start.Line + 1}: {d.Code} {d.Message}").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("pages", found[0]);
    }
}
