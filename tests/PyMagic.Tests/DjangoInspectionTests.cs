using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's Django inspections left after the others (item 183): ORM errors, block and partial names, broken comments, {% if %} spacing, django-constance.</summary>
public class DjangoInspectionTests
{
    const string Views = "file:///c:/proj/shop/views.py";
    const string Settings = "file:///c:/proj/shop/settings.py";

    static LanguageService Project(string views, string? settings = null)
    {
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models).With("shop/views.py", views);
        if (settings != null) b.With("shop/settings.py", settings);
        return b.Service;
    }

    static List<LspDiagnostic> Found(LanguageService svc, string uri, string code) => svc.Diagnostics(uri).Where(d => d.Code == code).ToList();

    // ------------------------------------------------------------ ORM

    const string Head = "from shop.models import Author, Book\n\n\ndef run(author: Author, book: Book):\n";

    [Fact]
    public void RemoveAndClearNeedANullableForeignKey()
    {
        var d = Found(Project(Head + "    author.book_set.remove(book)\n    author.book_set.clear()\n"), Views, "django-orm");
        Assert.Equal(2, d.Count);
        Assert.Contains("Book.author is not null=True", d[0].Message);
        Assert.Equal(20, d[0].Range.Start.Character);
    }

    [Fact]
    public void ANullableForeignKeyAndAManyToManyHaveRemove()
    {
        Assert.Equal(0, Found(Project(Head + "    author.edited.remove(book)\n    author.edited.clear()\n    book.tags.remove(1)\n    author.book_set.add(book)\n"), Views, "django-orm").Count);
    }

    [Fact]
    public void AManagerIsNotReachedThroughAnInstance()
    {
        var d = Found(Project(Head + "    book.objects.all()\n    Book.objects.all()\n"), Views, "django-orm");
        Assert.Equal("Manager isn't accessible via Book instances: use Book.objects", d.Single().Message);
    }

    // ------------------------------------------------------------ django-constance

    const string Constance =
        "CONSTANCE_CONFIG = {\n    \"SITE_NAME\": (\"Shop\", \"Name\"),\n    \"MAX_ITEMS\": (10, \"Limit\"),\n    \"HIDDEN\": (1, \"Not in a fieldset\"),\n}\n" +
        "CONSTANCE_CONFIG_FIELDSETS = {\n    \"General\": (\"SITE_NAME\", \"MAX_ITEMS\"),\n    \"Other\": {\"fields\": (\"GONE\",), \"collapse\": True},\n}\n";

    [Fact]
    public void AConstanceKeyMustBeInTheConfig()
    {
        var svc = Project("from constance import config\n\nprint(config.SITE_NAME, config.SITE_NAM)\n", Constance);
        var d = Found(svc, Views, "django-constance").Single();
        Assert.Contains("'SITE_NAM' is not a key of CONSTANCE_CONFIG", d.Message);
    }

    [Fact]
    public void FieldsetsAndTheConfigMustAgree()
    {
        var d = Found(Project("x = 1\n", Constance), Settings, "django-constance").Select(x => x.Message).ToList();
        Assert.Equal(2, d.Count);
        Assert.True(d.Any(m => m.Contains("'HIDDEN' is in no fieldset")));
        Assert.True(d.Any(m => m.Contains("'GONE' is not a key")));
    }

    [Fact]
    public void ConfigWithoutFieldsetsNeedsNone()
    {
        string settings = "CONSTANCE_CONFIG = {\"A\": (1, \"a\")}\n";
        Assert.Equal(0, Found(Project("x = 1\n", settings), Settings, "django-constance").Count);
    }
}
