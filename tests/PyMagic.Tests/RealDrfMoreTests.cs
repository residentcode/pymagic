using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The REST framework line of item 205, on the real package (skipped where Django or djangorestframework is not installed): a DefaultRouter's
/// extras, the URLs of installed apps, the serializer of each method of an @action, to_representation, what a serializer's methods are given,
/// and a writable nested serializer without create() / update().
/// </summary>
public class RealDrfMoreTests
{
    static readonly string? DjangoSite = Find("PYMAGIC_DJANGO_SITE", Path.Combine("django", "db", "models", "__init__.py"));
    static readonly string? DrfSite = Find("PYMAGIC_DRF_SITE", Path.Combine("rest_framework", "viewsets.py"));

    static string? Find(string variable, string marker) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } c && File.Exists(Path.Combine(c, marker)) ? c : null;

    static bool Missing => DjangoSite == null || DrfSite == null;

    const string Models =
        "from django.db import models\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    pages = models.IntegerField(default=0)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n";

    const string Serializers =
        "from rest_framework import serializers\n\nfrom shop.models import Author, Book\n\n\nclass AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = ['id', 'name']\n\n\n" +
        "class BookSerializer(serializers.ModelSerializer):\n    author = AuthorSerializer()\n\n    class Meta:\n        model = Book\n        fields = ['id', 'title', 'pages', 'author']\n\n" +
        "    def update(self, instance, validated_data):\n        instance.title = validated_data['titel']\n        print(instance.pagez)\n        return instance\n\n\n" +
        "class BookWriteSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ['title', 'pages', 'author']\n\n" +
        "    def create(self, validated_data):\n        return Book.objects.create(**validated_data)\n\n    def validate(self, attrs):\n        if attrs['pages'] < 0:\n            raise serializers.ValidationError('pages')\n        return attrs\n\n\n" +
        "class BookCardSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ['title']\n\n    def to_representation(self, instance):\n        return {'card': instance.title}\n";

    const string Views =
        "from rest_framework import viewsets\nfrom rest_framework.decorators import action\nfrom rest_framework.response import Response\n\n" +
        "from shop.models import Book\nfrom shop.serializers import BookCardSerializer, BookSerializer, BookWriteSerializer\n\n\n" +
        "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n\n" +
        "    def get_serializer_class(self):\n        if self.request.method == 'POST':\n            return BookWriteSerializer\n        if self.action == 'cards':\n            return BookCardSerializer\n        return BookSerializer\n\n" +
        "    @action(detail=False, methods=['get', 'post'])\n    def bulk(self, request):\n        return Response({})\n\n" +
        "    @action(detail=False)\n    def cards(self, request):\n        return Response({})\n";

    const string Urls =
        "from django.urls import include, path\nfrom rest_framework.authtoken.views import obtain_auth_token\nfrom rest_framework.routers import DefaultRouter\n\nfrom shop import views\n\n" +
        "router = DefaultRouter(trailing_slash=False)\nrouter.register(r'books', views.BookViewSet)\n\n" +
        "urlpatterns = [\n    path('api/', include(router.urls)),\n    path('api-token-auth/', obtain_auth_token),\n    path('api-auth/', include('rest_framework.urls')),\n]\n";

    static (LanguageService Svc, string Dir) Project()
    {
        var svc = new LanguageService();
        string dir = Path.Combine(Path.GetTempPath(), "pm_realdrf2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shop"));
        Directory.CreateDirectory(Path.Combine(dir, "config"));
        void Put(string rel, string text)
        {
            string full = Path.Combine(dir, rel);
            File.WriteAllText(full, text);
            svc.Open(LanguageService.UriOfPath(full), text, 1);
        }
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(DjangoSite!);
        svc.LibraryRoots.Add(DrfSite!);
        Put("shop/__init__.py", "");
        Put("config/__init__.py", "");
        Put("shop/models.py", Models);
        Put("shop/serializers.py", Serializers);
        Put("shop/views.py", Views);
        Put("config/urls.py", Urls);
        Put("config/settings.py", "ROOT_URLCONF = 'config.urls'\nINSTALLED_APPS = ['rest_framework', 'rest_framework.authtoken', 'shop']\n");
        return (svc, dir);
    }

    static string Line(RouteEntry r) => $"{r.Methods} {r.Path} {r.Name}";

    [Fact]
    public void TheRoutesOfTheRouterAndOfInstalledApps()
    {
        if (Missing) return;
        var routes = Project().Svc.DjangoRoutes();
        var lines = routes.Select(Line).ToList();
        // trailing_slash=False, the API root of a DefaultRouter, an @action whose methods get different serializers split in two
        Assert.True(lines.Contains("GET /api/books BookViewSet.list") && lines.Contains("GET /api/books/{pk} BookViewSet.retrieve"), string.Join("\n", lines));
        Assert.True(lines.Contains("GET /api/ api-root (DefaultRouter)"), string.Join("\n", lines));
        Assert.True(lines.Contains("GET /api/books/bulk BookViewSet.bulk") && lines.Contains("POST /api/books/bulk BookViewSet.bulk"), string.Join("\n", lines));
        // obtain_auth_token is ObtainAuthToken.as_view() in rest_framework.authtoken; rest_framework.urls gives login and logout
        Assert.True(lines.Contains("POST /api-token-auth/ ObtainAuthToken"), string.Join("\n", lines));
        Assert.True(lines.Any(l => l.EndsWith("/api-auth/login/ LoginView", StringComparison.Ordinal)), string.Join("\n", lines));
        var post = routes.Single(r => r.Name == "BookViewSet.bulk" && r.Methods == "POST");
        Assert.Equal("BookWriteSerializer", post.Params!.Single(p => p.Kind == "body").Type);
        Assert.Equal("BookSerializer", routes.Single(r => r.Name == "BookViewSet.bulk" && r.Methods == "GET").Response);
        Assert.Equal("BookCardSerializer (as its to_representation() makes it)", routes.Single(r => r.Name == "BookViewSet.cards").Response);
        Assert.True(routes.Single(r => r.Name == "BookViewSet.list").Tags!.Any(t => t.Contains(".{format}")));
    }

    [Fact]
    public void ASerializersMethodsKnowWhatTheyAreGiven()
    {
        if (Missing) return;
        var (svc, dir) = Project();
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "shop", "serializers.py"));
        var found = svc.Diagnostics(uri).Select(d => d.Message).ToList();
        // validated_data of update holds what BookSerializer writes; instance is a Book
        Assert.True(found.Any(m => m.Contains("BookSerializer writes no field 'titel', so validated_data has no such key")), string.Join(" | ", found));
        Assert.True(found.Any(m => m.Contains("Cannot find reference 'pagez' in 'Book'")), string.Join(" | ", found));
        // a writable nested serializer of a serializer a ModelViewSet writes with, which has update() but no create()
        Assert.True(found.Any(m => m.Contains("'author' is a writable nested serializer and BookSerializer has no create()")), string.Join(" | ", found));
        Assert.True(!found.Any(m => m.Contains("BookWriteSerializer") || m.Contains("BookCardSerializer")), string.Join(" | ", found));
        var text = File.ReadAllText(Path.Combine(dir, "shop", "serializers.py"));
        int at = text.IndexOf("attrs['pages']", StringComparison.Ordinal);
        Assert.Contains("validated_data of BookWriteSerializer", svc.Hover(uri, new LineMap(text).PositionOf(at + 1))!.Contents.Value);
    }
}
