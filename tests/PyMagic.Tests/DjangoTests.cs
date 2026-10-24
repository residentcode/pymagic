using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoTests
{
    const string Main = "file:///c:/proj/shop/views.py";

    static (LanguageService Svc, string Text, Position Pos) Open(string marked, string models = DjangoFixture.Models, params (string Path, string Text)[] more)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", models).With("shop/views.py", text);
        foreach (var (p, t) in more) b.With(p, t);
        return (b.Service, text, new LineMap(text).PositionOf(i));
    }

    /// <summary>The type the hover shows for the name at the cursor ("title: str" gives "str").</summary>
    static string TypeAt(string marked, string models = DjangoFixture.Models)
    {
        var (svc, _, pos) = Open(marked, models);
        var hover = svc.Hover(Main, pos);
        Assert.True(hover != null, "hover at the cursor");
        string line = hover!.Contents.Value.Split('\n')[1];
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? line : line[(colon + 2)..];
    }

    static List<string> Problems(string code, string models = DjangoFixture.Models)
    {
        var (svc, _, _) = Open(code + "|");
        return svc.Diagnostics(Main).Where(d => d.Severity <= Severity.Warning).Select(d => d.Message).ToList();
    }

    static List<string> Members(string marked, string models = DjangoFixture.Models)
    {
        var (svc, _, pos) = Open(marked, models);
        return svc.Completion(Main, pos).Select(i => i.Label).ToList();
    }

    const string Imports = "from shop.models import Author, Book, Profile, Tag\n\n\n";

    // ------------------------------------------------------------ fields

    [Fact]
    public void FieldsAreTheirValuesOnAnInstance()
    {
        string head = Imports + "book = Book()\n";
        Assert.Equal("str", TypeAt(head + "book.ti|tle"));
        Assert.Equal("int", TypeAt(head + "book.pa|ges"));
        Assert.Equal("bool", TypeAt(head + "book.in_|print"));
        Assert.Equal("str", TypeAt(head + "book.sta|tus"));
        Assert.Contains("float", TypeAt(head + "book.rat|ing"));
    }

    [Fact]
    public void NullableFieldsMayBeNone()
    {
        string head = Imports + "book = Book()\n";
        Assert.Equal("float | None", TypeAt(head + "book.rat|ing"));
        Assert.Equal("Author | None", TypeAt(head + "book.edi|tor"));
    }

    [Fact]
    public void FieldsOfStandardTypes()
    {
        string head = Imports + "book = Book()\nauthor = Author()\n";
        // these types come from the standard library: not every configuration can read it
        string price = TypeAt(head + "book.pri|ce");
        Assert.True(price is "Decimal" or "Unknown", price);
        string published = TypeAt(head + "book.publi|shed");
        Assert.True(published is "datetime" or "Unknown", published);
        string born = TypeAt(head + "author.bo|rn");
        Assert.True(born is "date | None" or "Unknown", born);
    }

    [Fact]
    public void RelationsAreTheModelsTheyPointAt()
    {
        string head = Imports + "book = Book()\n";
        Assert.Equal("Author", TypeAt(head + "book.auth|or"));
        Assert.Equal("str", TypeAt(head + "book.author.na|me"));
        Assert.Equal("Author | None", TypeAt(head + "book.edi|tor"));   // written as "Author"
        Assert.Equal("Author", TypeAt(head + "book.edi|tor", DjangoFixture.Models.Replace("null=True, on_delete=models.SET_NULL, ", "on_delete=models.SET_NULL, ")));
    }

    [Fact]
    public void SelfReferencesAndKeywordTargets()
    {
        string models = DjangoFixture.Models + "\n\nclass Category(models.Model):\n    parent = models.ForeignKey('self', null=True, on_delete=models.CASCADE)\n    owner = models.ForeignKey(to=Author, on_delete=models.CASCADE)\n";
        Assert.Equal("Category | None", TypeAt("from shop.models import Category\n\nc = Category()\nc.par|ent", models));
        Assert.Equal("Author", TypeAt("from shop.models import Category\n\nc = Category()\nc.own|er", models));
    }

    [Fact]
    public void ForeignKeysAlsoHaveAnIdAndEveryModelAPk()
    {
        string head = Imports + "book = Book()\n";
        Assert.Equal("int", TypeAt(head + "book.author_|id"));
        Assert.Equal("int", TypeAt(head + "book.|pk"));
        Assert.Equal("int", TypeAt(head + "book.|id"));
    }

    [Fact]
    public void AnExplicitPrimaryKeyIsThePk()
    {
        string models = "from django.db import models\n\n\nclass Code(models.Model):\n    key = models.CharField(max_length=5, primary_key=True)\n";
        Assert.Equal("str", TypeAt("from shop.models import Code\n\nc = Code()\nc.|pk", models));
        var members = Members("from shop.models import Code\n\nc = Code()\nc.|", models);
        Assert.True(!members.Contains("id"), "no automatic id next to an explicit key");
    }

    [Fact]
    public void ChoicesAddTheDisplayMethod()
    {
        Assert.True(TypeAt(Imports + "book = Book()\nbook.get_status_di|splay()").EndsWith("-> str"));
        Assert.True(Members(Imports + "book = Book()\nbook.|").Contains("get_status_display"));
    }

    [Fact]
    public void FieldsOfAbstractBasesAreInherited()
    {
        string models = "from django.db import models\n\n\nclass Stamped(models.Model):\n    created = models.CharField(max_length=5)\n\n    class Meta:\n        abstract = True\n\n\nclass Post(Stamped):\n    body = models.TextField()\n";
        Assert.Equal("str", TypeAt("from shop.models import Post\n\np = Post()\np.crea|ted", models));
        Assert.Equal("str", TypeAt("from shop.models import Post\n\np = Post()\np.bo|dy", models));
    }

    // ------------------------------------------------------------ managers and querysets

    [Fact]
    public void ObjectsIsAManagerOfTheModel()
    {
        Assert.Equal("Book", TypeAt(Imports + "book = Book.objects.get(pk=1)\nbo|ok"));
        Assert.Equal("Book | None", TypeAt(Imports + "book = Book.objects.first()\nbo|ok"));
        Assert.Equal("Book", TypeAt(Imports + "book = Book.objects.create(title='x')\nbo|ok"));
        Assert.Equal("int", TypeAt(Imports + "n = Book.objects.count()\n|n"));
        Assert.Equal("bool", TypeAt(Imports + "e = Book.objects.filter(pages=1).exists()\n|e"));
    }

    [Fact]
    public void QuerySetMethodsKeepTheModel()
    {
        Assert.Equal("Book", TypeAt(Imports + "b = Book.objects.filter(pages=1).exclude(title='x').order_by('title').get()\n|b"));
        Assert.Equal("str", TypeAt(Imports + "t = Book.objects.all().get(pk=1).ti|tle"));
        Assert.Equal("str", TypeAt(Imports + "for b in Book.objects.all():\n    b.ti|tle"));
        Assert.True(TypeAt(Imports + "pair = Book.objects.get_or_create(title='x')\n|pair").StartsWith("tuple[Book"), "get_or_create gives (book, created)");
    }

    [Fact]
    public void NoFalseAlarmsOnWhatDjangoAdds()
    {
        var problems = Problems(Imports + "Book.objects.all()\nBook.DoesNotExist\nBook.MultipleObjectsReturned\nbook = Book()\nbook.pk\nbook.id\nbook.author_id\n");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.True(Problems(Imports + "Book.objectss\n").Any(p => p.Contains("objectss")), "a typo is still found");
        Assert.True(Problems(Imports + "Book().nonsense\n").Any(p => p.Contains("nonsense")));
    }

    [Fact]
    public void DoesNotExistCanBeCaught()
    {
        var problems = Problems(Imports + "try:\n    Book.objects.get(pk=1)\nexcept Book.DoesNotExist as e:\n    print(e)\n");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    [Fact]
    public void CustomManagersKeepTheirMethods()
    {
        string models = DjangoFixture.Models + "\n\nclass PublishedManager(models.Manager):\n    def published(self):\n        return self.filter(in_print=True)\n\n\n" +
            "class Edition(models.Model):\n    year = models.IntegerField()\n    objects = PublishedManager()\n";
        var problems = Problems("from shop.models import Edition\n\nEdition.objects.published()\nEdition.objects.filter(year=1)\n", models);
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal("int", TypeAt("from shop.models import Edition\n\ne = Edition.objects.get(pk=1)\ne.ye|ar", models));
    }

    // ------------------------------------------------------------ reverse relations

    [Fact]
    public void ReverseNamesOfRelations()
    {
        string head = Imports + "author = Author()\n";
        var problems = Problems(head + "author.book_set.all()\nauthor.edited.filter(pages=1)\nauthor.profile\n");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
        Assert.Equal("Book", TypeAt(head + "b = author.book_set.first()\nb2 = author.book_set.get(pk=1)\n|b2"));
        Assert.Equal("Profile", TypeAt(head + "author.pro|file"));
        Assert.Equal("Book", TypeAt(Imports + "tag = Tag()\nb = tag.books.get(pk=1)\n|b"));
    }

    [Fact]
    public void ManyToManyIsAManagerBothWays()
    {
        Assert.Equal("Tag", TypeAt(Imports + "book = Book()\nt = book.tags.get(pk=1)\n|t"));
        Assert.True(Problems(Imports + "book = Book()\nbook.tags.add(Tag())\nbook.tags.set([])\nbook.tags.clear()\n").Count == 0);
    }

    [Fact]
    public void ReverseNamesAreOfferedAfterTheDot()
    {
        var author = Members(Imports + "author = Author()\nauthor.|");
        Assert.True(author.Contains("book_set") && author.Contains("edited") && author.Contains("profile") && author.Contains("name"), string.Join(",", author));
        var cls = Members(Imports + "Book.|");
        Assert.True(cls.Contains("objects") && cls.Contains("DoesNotExist"), string.Join(",", cls));
        var book = Members(Imports + "book = Book()\nbook.|");
        Assert.True(book.Contains("title") && book.Contains("author_id") && book.Contains("pk") && book.Contains("id") && book.Contains("tags"), string.Join(",", book));
        Assert.True(!book.Contains("objects"), "a manager is not reachable from an instance");
    }

    // ------------------------------------------------------------ settings

    [Fact]
    public void SettingsComeFromTheProjectsSettingsModule()
    {
        var settings = ("mysite/settings.py", "DEBUG = True\nINSTALLED_APPS = ['shop']\nROOT_URLCONF = 'mysite.urls'\n");
        var manage = ("manage.py", "import os\n\nos.environ.setdefault('DJANGO_SETTINGS_MODULE', 'mysite.settings')\n");
        string code = "from django.conf import settings\n\nsettings.DE|BUG\n";
        var (svc, _, pos) = Open(code, DjangoFixture.Models, settings, manage, ("mysite/__init__.py", ""));
        Assert.Contains("bool", svc.Hover(Main, pos)!.Contents.Value);
        var def = svc.Definition(Main, pos);
        Assert.Equal(1, def.Count);
        Assert.True(def[0].Uri.EndsWith("mysite/settings.py"), def[0].Uri);
    }

    [Fact]
    public void SettingsFallBackToDjangosDefaults()
    {
        var settings = ("mysite/settings.py", "DEBUG = True\n");
        var (svc, _, pos) = Open("from django.conf import settings\n\nsettings.LANGUAGE_|CODE\n", DjangoFixture.Models, settings, ("mysite/__init__.py", ""));
        Assert.Contains("str", svc.Hover(Main, pos)!.Contents.Value);
        var (svc2, _, pos2) = Open("from django.conf import settings\n\nsettings.|\n", DjangoFixture.Models, settings, ("mysite/__init__.py", ""));
        var names = svc2.Completion(Main, pos2).Select(i => i.Label).ToList();
        Assert.True(names.Contains("DEBUG") && names.Contains("LANGUAGE_CODE") && names.Contains("ALLOWED_HOSTS"), string.Join(",", names.Take(12)));
    }

    [Fact]
    public void AnUnknownSettingStaysQuiet()
    {
        var problems = Problems("from django.conf import settings\n\nsettings.MY_OWN_SETTING\n");
        Assert.True(problems.Count == 0, string.Join("; ", problems));
    }

    // ------------------------------------------------------------ what stays as it was

    [Fact]
    public void ClassesThatAreNotModelsAreNotTouched()
    {
        var problems = Problems("class Plain:\n    pass\n\n\nPlain.objects\nPlain().pk\n");
        Assert.True(problems.Count == 2, string.Join("; ", problems));
    }

    [Fact]
    public void ModelsDescribedByStubsAreLeftToTheStubs()
    {
        // a django-stubs style declaration: the module is a stub, so nothing is synthesised on top of it
        var (svc, _, pos) = Open("from shop.models import Item\n\nitem = Item()\nitem.tit|le\n",
            "from django.db import models\n\n\nclass Item(models.Model):\n    title: str\n");
        Assert.Contains("str", svc.Hover(Main, pos)!.Contents.Value);
    }
}
