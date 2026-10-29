using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Field names inside the ORM's objects (item 124): Q(...) keywords, F("...") and aggregates, checked, completed and followed.</summary>
public class DjangoExpressionTests
{
    const string Views = "file:///c:/proj/shop/views.py";
    const string Models = "file:///c:/proj/shop/models.py";
    const string Head = "from django.db.models import Q, F, Sum, Count, Coalesce\nfrom shop.models import Book\n\n";

    static (LanguageService Svc, Position Pos) Project(string marked, string? models = null)
    {
        int i = marked.IndexOf('|');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", models ?? DjangoFixture.Models).With("shop/views.py", text);
        return (b.Service, new LineMap(text).PositionOf(Math.Max(i, 0)));
    }

    static List<LspDiagnostic> Found(string code, string? models = null) =>
        Project(code, models).Svc.Diagnostics(Views).Where(d => d.Code == "django-field").ToList();

    // ------------------------------------------------------------ Q

    [Fact]
    public void AKeywordOfQIsCheckedAgainstTheModelOfTheQuery()
    {
        Assert.Equal(1, Found(Head + "Book.objects.filter(Q(titel='x'))\n").Count);
        Assert.Equal(1, Found(Head + "Book.objects.filter(Q(autor__name='y'))\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.filter(Q(title__icontains='x') | Q(author__name='y') & ~Q(pages__gt=3))\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.exclude(Q(tags__label='x'))\n").Count);
    }

    [Fact]
    public void ANestedQAndAQInAGetAreChecked()
    {
        Assert.Equal(1, Found(Head + "Book.objects.get(Q(Q(title='a'), pagez=3))\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.get(Q(Q(title='a'), pages=3))\n").Count);
    }

    [Fact]
    public void ThePointedOutWordIsTheKeyword()
    {
        var d = Found(Head + "Book.objects.filter(Q(titel='x'))\n").Single();
        Assert.Equal(3, d.Range.Start.Line);
        Assert.Equal(22, d.Range.Start.Character);
        Assert.Contains("Cannot resolve keyword 'titel'", d.Message);
    }

    // ------------------------------------------------------------ F and aggregates

    [Fact]
    public void AnAggregateOrFNamesAField()
    {
        Assert.Equal(1, Found(Head + "Book.objects.annotate(total=Sum('pricee'))\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.annotate(total=Sum('price'), n=Count('tags'), a=Count('author'))\n").Count);
    }

    [Fact]
    public void AFieldAcrossARelationIsFollowed()
    {
        Assert.Equal(0, Found(Head + "Book.objects.aggregate(Sum('author__born'))\n").Count);
        Assert.Equal(1, Found(Head + "Book.objects.aggregate(Sum('author__nam'))\n").Count);
    }

    [Fact]
    public void FInAFilterAndInAnUpdate()
    {
        Assert.Equal(1, Found(Head + "Book.objects.filter(price__gt=F('pagez'))\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.filter(price__gt=F('pages'))\n").Count);
        Assert.Equal(1, Found(Head + "Book.objects.update(pages=F('pagez') + 1)\n").Count);
        Assert.Equal(0, Found(Head + "Book.objects.update(pages=F('pages') + 1)\n").Count);
    }

    [Fact]
    public void EveryStringOfCoalesceIsAField()
    {
        var found = Found(Head + "Book.objects.annotate(x=Coalesce('title', 'nope'))\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'nope'", found[0].Message);
    }

    [Fact]
    public void WhatTheSameCallNamedIsNotAMistake()
    {
        Assert.Equal(0, Found(Head + "Book.objects.annotate(total=Sum('price'), half=F('total') / 2)\n").Count);
    }

    [Fact]
    public void StarAndPkAreFine()
    {
        Assert.Equal(0, Found(Head + "Book.objects.aggregate(Count('*'), Count('pk'))\n").Count);
    }

    // ------------------------------------------------------------ Meta of a model

    [Fact]
    public void AConstraintInAModelsMetaIsCheckedAgainstThatModel()
    {
        string models = DjangoFixture.Models.Replace("from django.db import models", "from django.db import models\nfrom django.db.models import Q")
            + "\n\nclass Order(models.Model):\n    qty = models.IntegerField()\n\n    class Meta:\n        constraints = [models.CheckConstraint(check=Q(qty__gt=0), name='a'), models.CheckConstraint(check=Q(qtty__gt=0), name='b')]\n";
        var d = Project("", models).Svc.Diagnostics(Models).Where(x => x.Code == "django-field").ToList();
        Assert.Equal(1, d.Count);
        Assert.Contains("'qtty'", d[0].Message);
    }

    // ------------------------------------------------------------ what is left alone

    [Fact]
    public void ChainsThatAddNamesOfTheirOwnAreNotChecked()
    {
        Assert.Equal(0, Found(Head + "Book.objects.annotate(t=Sum('price')).filter(Q(t__gt=1), Q(whatever=1))\n").Count);
    }

    [Fact]
    public void AQuerysetInAVariableIsNotChecked()
    {
        Assert.Equal(0, Found(Head + "def f(qs):\n    return qs.filter(Q(whatever=1))\n").Count);
    }

    [Fact]
    public void ANameOfTheProjectsOwnIsNotTheORMs()
    {
        string code = "from shop.models import Book\n\n\nclass Q:\n    def __init__(self, **kwargs): ...\n\n\nBook.objects.filter(Q(whatever=1))\n";
        Assert.Equal(0, Found(code).Count);
    }

    // ------------------------------------------------------------ completion and navigation

    static List<string> Labels(string marked)
    {
        var (svc, pos) = Project(marked);
        return svc.Completion(Views, pos).Select(i => i.Label).ToList();
    }

    [Fact]
    public void FieldNamesComeInsideTheStringOfAnAggregate()
    {
        var names = Labels(Head + "Book.objects.annotate(t=Sum('pr|'))\n");
        Assert.True(names.Contains("price"));
        Assert.True(!names.Contains("title"));
        Assert.True(Labels(Head + "Book.objects.annotate(t=Count('author__|'))\n").Contains("author__name"));
    }

    [Fact]
    public void KeywordsOfAFilterComeInsideQ()
    {
        Assert.True(Labels(Head + "Book.objects.filter(Q(tit|))\n").Contains("title"));
        Assert.True(Labels(Head + "Book.objects.filter(Q(author__|))\n").Contains("author__name"));
    }

    [Fact]
    public void AFieldInFLeadsToTheModel()
    {
        var (svc, pos) = Project(Head + "Book.objects.filter(price__gt=F('pa|ges'))\n");
        var found = svc.Definition(Views, pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(Models, found[0].Uri);
    }

    // ------------------------------------------------------------ the rest of a QuerySet

    [Fact]
    public void WhatARealQuerySetOffersIsNotReportedMissing()
    {
        string code = Head + string.Join("\n", new[]
        {
            "Book.objects.aggregate(Sum('price'))", "Book.objects.values('title')", "Book.objects.values_list('title', flat=True)", "Book.objects.filter(pages=1).delete()",
            "Book.objects.in_bulk([1])", "Book.objects.all().iterator()", "Book.objects.bulk_update([], ['title'])", "Book.objects.all().query", "Book.objects.all().ordered",
            "async def f():\n    await Book.objects.aget(pk=1)\n    await Book.objects.acount()",
        }) + "\n";
        var missing = Project(code).Svc.Diagnostics(Views).Where(d => d.Message.StartsWith("Cannot find reference")).ToList();
        Assert.Equal(0, missing.Count);
    }

    [Fact]
    public void ANameThatIsNotAQuerySetMethodIsStillReported()
    {
        var missing = Project(Head + "Book.objects.aggregat(Sum('price'))\n").Svc.Diagnostics(Views).Where(d => d.Message.StartsWith("Cannot find reference")).ToList();
        Assert.Equal(1, missing.Count);
    }
}
