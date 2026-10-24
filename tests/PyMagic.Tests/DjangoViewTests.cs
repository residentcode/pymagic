using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoViewTests
{
    const string Shop = "file:///c:/proj/shop/";

    const string Models =
        "from django.db import models\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n";

    const string Serializers =
        "from rest_framework import serializers\nfrom shop.models import Book, Author\n\n\nclass BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = [\"title\"]\n\n\n" +
        "class AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = [\"name\"]\n";

    static (LanguageService Svc, string Uri, Position Pos) Open(string marked)
    {
        int i = marked.IndexOf('§');
        string text = i < 0 ? marked : marked.Remove(i, 1);
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", Models).With("shop/serializers.py", Serializers)
            .With("rest_framework/__init__.py", "")
            .With("rest_framework/serializers.py", "class Serializer:\n    def save(self, **kwargs): ...\n\n\nclass ModelSerializer(Serializer): ...\n")
            .With("rest_framework/generics.py", "class GenericAPIView:\n    queryset = None\n    serializer_class = None\n\n\nclass ListAPIView(GenericAPIView): ...\n")
            .With("rest_framework/viewsets.py", "class GenericViewSet:\n    queryset = None\n\n\nclass ModelViewSet(GenericViewSet): ...\n")
            .With("shop/views.py", text);
        return (b.Service, Shop + "views.py", i < 0 ? new Position(0, 0) : new LineMap(text).PositionOf(i));
    }

    const string Head = "from rest_framework import viewsets, generics\nfrom shop.models import Book\nfrom shop.serializers import BookSerializer, AuthorSerializer\n\n\n";

    [Fact]
    public void FieldsOfAViewAreCheckedAgainstTheQuerysetModel()
    {
        var (svc, uri, _) = Open(Head + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n" +
            "    filterset_fields = [\"title\", \"author__name\", \"gone\"]\n    search_fields = [\"^title\", \"author__name\", \"bogus\"]\n    ordering_fields = [\"pages\", \"nothing\"]\n    ordering = [\"-title\"]\n    lookup_field = \"pk\"\n");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "django-meta-field").Select(d => d.Message).ToList();
        Assert.Equal(3, found.Count);
        Assert.True(found.Any(m => m.Contains("gone")));
        Assert.True(found.Any(m => m.Contains("bogus")));
        Assert.True(found.Any(m => m.Contains("'nothing'")));
    }

    [Fact]
    public void TheModelCanComeFromTheSerializer()
    {
        var (svc, uri, _) = Open(Head + "class BookList(generics.ListAPIView):\n    serializer_class = BookSerializer\n    filterset_fields = {\"title\": [\"exact\"], \"missing\": [\"exact\"]}\n");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "django-meta-field").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("missing", found[0].Message);
    }

    [Fact]
    public void ASerializerForAnotherModelIsReported()
    {
        var (svc, uri, _) = Open(Head + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = AuthorSerializer\n");
        svc.ResetInspections();   // the profile file lives in the fixture folder, shared by every run
        var found = svc.Diagnostics(uri).Where(d => d.Code == "django-view-serializer").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("Author", found[0].Message);
        svc.SetInspection("django-view-serializer", "off");
        int left = svc.Diagnostics(uri).Count(d => d.Code == "django-view-serializer");
        svc.ResetInspections();
        Assert.Equal(0, left);
    }

    [Fact]
    public void ViewFieldNamesAreCompleted()
    {
        var (svc, uri, pos) = Open(Head + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    filterset_fields = [\"ti§\"]\n");
        var items = svc.Completion(uri, pos).Select(i => i.Label).ToList();
        Assert.True(items.Contains("title"), "title offered: " + string.Join(",", items.Take(8)));
    }

    [Fact]
    public void AViewNotFromRestFrameworkIsLeftAlone()
    {
        var (svc, uri, _) = Open("class Plain:\n    queryset = 1\n    filterset_fields = [\"zzz\"]\n");
        Assert.Equal(0, svc.Diagnostics(uri).Count(d => d.Code == "django-meta-field"));
    }
}
