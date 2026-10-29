using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoEditorTests
{
    const string Views = "file:///c:/proj/shop/views.py";
    const string Urls = "file:///c:/proj/shop/urls.py";

    const string UrlsSource =
        "from django.urls import path\nfrom shop import views\n\napp_name = \"shop\"\n\nurlpatterns = [\n" +
        "    path(\"books/\", views.index, name=\"book-list\"),\n    path(\"books/<int:pk>/\", views.detail, name=\"book-detail\"),\n]\n";

    static (LanguageService Svc, Position Pos) Project(string marked, string? templates = null, params (string Path, string Text)[] more)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models).With("shop/urls.py", UrlsSource).With("shop/views.py", text);
        foreach (var (p, t) in more) b.With(p, t);
        if (templates != null) b.Service.SourceRoots.Add(templates);
        return (b.Service, new LineMap(text).PositionOf(i));
    }

    static string TempTemplates(params string[] names)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_tpl_" + Guid.NewGuid().ToString("N"));
        foreach (var n in names)
        {
            string full = Path.Combine(dir, "shop", "templates", n.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, "<html></html>");
        }
        return dir;
    }

    static List<LspDiagnostic> Diagnostics(string code, string? templates = null) =>
        Project(code + "|", templates).Svc.Diagnostics(Views).Where(d => d.Message.StartsWith("No URL") || d.Message.StartsWith("Template") || d.Message.StartsWith("Cannot resolve") || d.Message.StartsWith("Cannot find")).ToList();

    // ------------------------------------------------------------ URL names

    [Fact]
    public void ANameLeadsToItsPath()
    {
        var (svc, pos) = Project("from django.urls import reverse\n\nreverse('shop:book-det|ail', args=[1])\n");
        var found = svc.Definition(Views, pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(Urls, found[0].Uri);
        Assert.Equal(7, found[0].Range.Start.Line);
    }

    [Fact]
    public void ANameWithoutItsNamespaceStillFindsThePath()
    {
        var (svc, pos) = Project("from django.shortcuts import redirect\n\nredirect('book-li|st')\n");
        Assert.Equal(Urls, svc.Definition(Views, pos).Single().Uri);
    }

    [Fact]
    public void HoverShowsTheRoute()
    {
        var (svc, pos) = Project("from django.urls import reverse\n\nreverse('shop:book-det|ail')\n");
        string text = svc.Hover(Views, pos)!.Contents.Value;
        Assert.Contains("books/<int:pk>/", text);
    }

    [Fact]
    public void UsagesOfAnUrlName()
    {
        string code = "from django.urls import reverse\nfrom django.shortcuts import redirect\n\nreverse('shop:book-det|ail')\nredirect('book-detail')\nreverse('shop:book-list')\n";
        var (svc, pos) = Project(code);
        var refs = svc.References(Views, pos, includeDeclaration: true);
        Assert.Equal(3, refs.Count);
        Assert.Equal(1, refs.Count(r => r.Uri == Urls));
        Assert.Equal(2, svc.References(Views, pos, includeDeclaration: false).Count);
        // from the definition
        var (svc2, _) = Project(code);
        var fromDefinition = svc2.References(Urls, new Position(7, 50), includeDeclaration: true);
        Assert.Equal(3, fromDefinition.Count);
    }

    [Fact]
    public void RenamingAnUrlNameKeepsNamespaces()
    {
        var (svc, pos) = Project("from django.urls import reverse\nfrom django.shortcuts import redirect\n\nreverse('shop:book-det|ail')\nredirect('book-detail')\nreverse('shop:book-list')\n");
        var edit = svc.Rename(Views, pos, "volume");
        var inViews = edit.Changes[Views].Select(e => e.NewText).OrderBy(x => x).ToList();
        Assert.Equal("shop:volume,volume", string.Join(",", inViews));
        Assert.Equal("volume", edit.Changes[Urls].Single().NewText);
        var prepared = svc.PrepareRename(Views, pos)!;
        Assert.Equal("book-detail", prepared.Placeholder);
        Assert.Throws<ServiceException>(() => svc.Rename(Views, pos, "not valid!"));
    }

    [Fact]
    public void UrlNamesComplete()
    {
        var (svc, pos) = Project("from django.urls import reverse\n\nreverse('|')\n");
        var labels = svc.Completion(Views, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("shop:book-list") && labels.Contains("shop:book-detail"), string.Join(",", labels));
        var (svc2, pos2) = Project("from django.urls import reverse\n\nreverse('shop:book-d|')\n");
        Assert.Equal("shop:book-detail", svc2.Completion(Views, pos2).Single().Label);
    }

    [Fact]
    public void AnUnknownUrlNameIsReported()
    {
        var found = Diagnostics("from django.urls import reverse\n\nreverse('shop:book-lst')\nreverse('shop:book-list')\nreverse('book-detail')\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("book-lst", found[0].Message);
        Assert.Equal(Severity.Information, found[0].Severity);
    }

    [Fact]
    public void NothingIsReportedWithoutUrlPatternsOrForPathsAndDottedViews()
    {
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/views.py", "from django.urls import reverse\nfrom django.shortcuts import redirect\n\nreverse('anything')\nredirect('/books/')\nredirect('shop.views.index')\n");
        Assert.Equal(0, b.Service.Diagnostics(Views).Count(d => d.Message.StartsWith("No URL")));
        var found = Diagnostics("from django.shortcuts import redirect\n\nredirect('/books/')\nredirect('https://example.com/x')\nredirect('shop.views.index')\n");
        Assert.Equal(0, found.Count);
    }

    // ------------------------------------------------------------ templates

    [Fact]
    public void ATemplateNameLeadsToItsFile()
    {
        string root = TempTemplates("shop/index.html", "shop/detail.html");
        try
        {
            var (svc, pos) = Project("from django.shortcuts import render\n\ndef index(request):\n    return render(request, 'shop/inde|x.html')\n", root);
            var found = svc.Definition(Views, pos).Single();
            Assert.True(found.Uri.EndsWith("shop/templates/shop/index.html"), found.Uri);
            var (svc2, pos2) = Project("from django.views.generic import TemplateView\n\nclass V(TemplateView):\n    template_name = 'shop/det|ail.html'\n", root);
            Assert.True(svc2.Definition(Views, pos2).Single().Uri.EndsWith("detail.html"));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TemplateNamesComplete()
    {
        string root = TempTemplates("shop/index.html", "shop/detail.html", "base.html");
        try
        {
            var (svc, pos) = Project("from django.shortcuts import render\n\nrender(request, 'shop/|')\n", root);
            var labels = svc.Completion(Views, pos).Select(i => i.Label).OrderBy(x => x).ToList();
            Assert.Equal("shop/detail.html,shop/index.html", string.Join(",", labels));
            var (svc2, pos2) = Project("from django.template.loader import render_to_string\n\nrender_to_string('b|')\n", root);
            Assert.Equal("base.html", svc2.Completion(Views, pos2).Single().Label);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AMissingTemplateIsReportedButLibraryOnesAreNot()
    {
        string root = TempTemplates("shop/index.html");
        try
        {
            var found = Diagnostics("from django.shortcuts import render\n\nrender(request, 'shop/index.html')\nrender(request, 'shop/missing.html')\nrender(request, 'admin/base.html')\n", root);
            Assert.Equal(1, found.Count);
            Assert.Contains("shop/missing.html", found[0].Message);
            Assert.Equal(0, Diagnostics("from django.shortcuts import render\n\nrender(request, 'x.html')\n").Count);   // no templates folder at all
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ query keywords

    const string Qimports = "from shop.models import Author, Book\n\n\n";

    [Fact]
    public void KeywordsThatAreFieldsAndLookupsPass()
    {
        var found = Diagnostics(Qimports + "Book.objects.filter(title='x', pages__gt=1, title__icontains='a', author__name='n', author=1, pk=3, id=4)\n" +
            "Book.objects.exclude(in_print=True).get(price=1)\nAuthor.objects.filter(book__title='x', edited__pages=1, profile__bio='b')\nBook.objects.filter(tags__label='x', author__born__year=2000)\n");
        Assert.True(found.Count == 0, string.Join("; ", found.Select(f => f.Message)));
    }

    [Fact]
    public void UnknownKeywordsAreReportedWithTheChoices()
    {
        var found = Diagnostics(Qimports + "Book.objects.filter(titel='x')\nBook.objects.filter(author__nam='x')\nBook.objects.get(pages__bogus=1)\n");
        Assert.Equal(3, found.Count);
        Assert.Contains("titel", found[0].Message);
        Assert.Contains("title", found[0].Message);   // the choices
        Assert.Contains("nam", found[1].Message);
        Assert.Contains("bogus", found[2].Message);
        Assert.Equal(Severity.Warning, found[0].Severity);
    }

    [Fact]
    public void CreateAndUpdateTakeFieldsNotLookups()
    {
        var found = Diagnostics(Qimports + "Book.objects.create(title='x', pages=1)\nBook.objects.create(titel='x')\nBook.objects.filter(pk=1).update(pages=2)\nBook.objects.filter(pk=1).update(pages__gt=2)\n");
        Assert.Equal(2, found.Count);
        Assert.Contains("titel", found[0].Message);
        Assert.Contains("gt", found[1].Message);
    }

    [Fact]
    public void NamesAddedByAnnotateAreNotChecked()
    {
        var found = Diagnostics("from django.db.models import Count\n" + Qimports + "Book.objects.annotate(n=Count('tags')).filter(n__gt=1)\nqs = Book.objects.all()\nqs.filter(whatever=1)\n");
        Assert.True(found.Count == 0, string.Join("; ", found.Select(f => f.Message)));
    }

    [Fact]
    public void FieldNamesInOrderByAndFriendsAreChecked()
    {
        var found = Diagnostics(Qimports + "Book.objects.order_by('-title', 'author__name', 'pk', '?')\nBook.objects.order_by('autor')\nBook.objects.select_related('author').only('title')\nBook.objects.prefetch_related('tagz')\n");
        Assert.Equal(2, found.Count);
        Assert.Contains("autor", found[0].Message);
        Assert.Contains("tagz", found[1].Message);
    }

    [Fact]
    public void QueryKeywordsComplete()
    {
        var (svc, pos) = Project(Qimports + "Book.objects.filter(ti|\n");
        var items = svc.Completion(Views, pos);
        var title = items.Single(i => i.Label == "title" && i.InsertText == "title=");
        Assert.Equal(CompletionKind.Field, title.Kind);
        Assert.True(!items.Any(i => i.Label == "name"), "fields of other models are not offered");
        var (svc2, pos2) = Project(Qimports + "Book.objects.filter(au|\n");
        var relation = svc2.Completion(Views, pos2);
        Assert.True(relation.Any(i => i.Label == "author" && i.InsertText == "author="));
        Assert.True(relation.Any(i => i.Label == "author__" && i.InsertText == "author__"), "relations can be followed");
    }

    [Fact]
    public void QueryKeywordsCompleteThroughRelationsAndLookups()
    {
        var (svc, pos) = Project(Qimports + "Book.objects.filter(author__na|\n");
        Assert.True(svc.Completion(Views, pos).Any(i => i.Label == "author__name" && i.InsertText == "author__name="));
        var (svc2, pos2) = Project(Qimports + "Book.objects.filter(title__ic|\n");
        Assert.True(svc2.Completion(Views, pos2).Any(i => i.Label == "title__icontains" && i.InsertText == "title__icontains="));
        var (svc3, pos3) = Project(Qimports + "Author.objects.filter(boo|\n");
        Assert.True(svc3.Completion(Views, pos3).Any(i => i.Label == "book"), "reverse query names");
    }

    [Fact]
    public void FieldNamesCompleteInOrderBy()
    {
        var (svc, pos) = Project(Qimports + "Book.objects.order_by('-ti|')\n");
        Assert.Equal("title", svc.Completion(Views, pos).Single(i => i.Label == "title").Label);
        var (svc2, pos2) = Project(Qimports + "Book.objects.order_by('author__|')\n");
        var labels = svc2.Completion(Views, pos2).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("author__name") && labels.Contains("author__born"), string.Join(",", labels));
    }

    [Fact]
    public void AFieldNameInAStringLeadsToTheField()
    {
        var (svc, pos) = Project(Qimports + "Book.objects.order_by('-ti|tle')\n");
        var found = svc.Definition(Views, pos).Single();
        Assert.True(found.Uri.EndsWith("shop/models.py"), found.Uri);
        var (svc2, pos2) = Project(Qimports + "Book.objects.order_by('author__na|me')\n");
        Assert.Equal(4, svc2.Definition(Views, pos2).Single().Range.Start.Line);   // Author.name
    }

    [Fact]
    public void SuppressionCommentsWork()
    {
        var found = Diagnostics(Qimports + "Book.objects.filter(titel='x')  # noqa\nBook.objects.filter(titel='y')\n");
        Assert.Equal(1, found.Count);
    }
}
