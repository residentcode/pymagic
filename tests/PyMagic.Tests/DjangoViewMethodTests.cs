using PyMagic.Server;

namespace PyMagic.Tests;

public class DjangoViewMethodTests
{
    const string Models =
        "from django.db import models\n\n\nclass Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n";

    const string Serializers =
        "from rest_framework import serializers\nfrom shop.models import Book\n\n\nclass BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = [\"title\"]\n";

    const string Head = "from rest_framework import viewsets\nfrom shop.models import Book\nfrom shop.serializers import BookSerializer\n\n\n";

    static (LanguageService Svc, string Uri) Open(string code)
    {
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", Models).With("shop/serializers.py", Serializers)
            .With("rest_framework/__init__.py", "")
            .With("rest_framework/serializers.py", "class Serializer:\n    def save(self, **kwargs): ...\n\n\nclass ModelSerializer(Serializer): ...\n")
            .With("rest_framework/viewsets.py", "class GenericViewSet:\n    queryset = None\n\n    def get_object(self): ...\n\n    def get_serializer(self, *args, **kwargs): ...\n\n\nclass ModelViewSet(GenericViewSet): ...\n")
            .With("shop/views.py", Head + code);
        return (b.Service, "file:///c:/proj/shop/views.py");
    }

    [Fact]
    public void GetObjectAndGetSerializerAreTyped()
    {
        var (svc, uri) = Open("class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n\n    def retrieve(self, request):\n        book = self.get_object()\n        book.title\n        book.nope\n        self.get_serializer().save()\n        self.get_serializer().bogus\n");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "missing-attribute").Select(d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.Contains("'nope'") && m.Contains("Book")), string.Join(" / ", found));
        Assert.True(found.Any(m => m.Contains("'bogus'") && m.Contains("BookSerializer")), string.Join(" / ", found));
    }

    [Fact]
    public void AViewWithItsOwnGetObjectKeepsIt()
    {
        var (svc, uri) = Open("class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n\n    def get_object(self):\n        return 5\n\n    def retrieve(self, request):\n        self.get_object().nope\n");
        Assert.Equal(0, svc.Diagnostics(uri).Count(d => d.Message.Contains("Book'")));
    }

    [Fact]
    public void TheModelCanComeFromGetQueryset()
    {
        var (svc, uri) = Open("class BookViewSet(viewsets.ModelViewSet):\n    search_fields = [\"title\", \"zzz\"]\n\n    def get_queryset(self):\n        return Book.objects.filter(pages__gt=1)\n");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "django-meta-field").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("zzz", found[0].Message);
    }
}
