using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The endpoints of a Django project, read from its urls.py files (item 127): path() and include(), class and function views, and what a router makes of a viewset.</summary>
public class DjangoRoutesTests
{
    const string Rest =
        "class APIView: ...\n";

    static IReadOnlyList<RouteEntry> RoutesOf(string urls, string? rootUrls = null, string? views = null, string? settings = "ROOT_URLCONF = 'config.urls'\n")
    {
        var b = DjangoFixture.Service()
            .With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models)
            .With("rest_framework/__init__.py", "")
            .With("rest_framework/views.py", Rest)
            .With("rest_framework/viewsets.py", "class GenericViewSet: ...\n\n\nclass ModelViewSet(GenericViewSet): ...\n\n\nclass ReadOnlyModelViewSet(GenericViewSet): ...\n\n\nclass ViewSet: ...\n")
            .With("rest_framework/generics.py", "class ListCreateAPIView: ...\n\n\nclass RetrieveUpdateDestroyAPIView: ...\n\n\nclass ListAPIView: ...\n")
            .With("rest_framework/decorators.py", "def api_view(methods=None): ...\n\n\ndef action(**kw): ...\n")
            .With("rest_framework/routers.py", "class DefaultRouter:\n    urls = []\n\n    def register(self, prefix, viewset, basename=None): ...\n")
            .With("rest_framework/serializers.py", "class ModelSerializer: ...\n")
            .With("rest_framework/permissions.py", "class IsAuthenticated: ...\n")
            .With("shop/serializers.py", "from rest_framework import serializers\nfrom shop.models import Book\n\n\nclass BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = '__all__'\n")
            .With("shop/views.py", views ?? Views)
            .With("shop/urls.py", urls)
            .With("config/__init__.py", "")
            .With("config/urls.py", rootUrls ?? "from django.urls import path, include\n\nurlpatterns = [path('api/', include('shop.urls'))]\n");
        if (settings != null) b.With("config/settings.py", settings);
        return b.Service.DjangoRoutes();
    }

    const string Views =
        "from rest_framework import viewsets, generics, serializers\nfrom rest_framework.decorators import action, api_view\nfrom rest_framework.views import APIView\nfrom rest_framework.permissions import IsAuthenticated\n" +
        "from shop.models import Book\nfrom shop.serializers import BookSerializer\n\n\n" +
        "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n    filterset_fields = ['title', 'author']\n    search_fields = ['title']\n    permission_classes = [IsAuthenticated]\n\n" +
        "    @action(detail=True, methods=['post'], url_path='publish')\n    def publish(self, request, pk=None): ...\n\n    @action(detail=False)\n    def recent(self, request): ...\n\n\n" +
        "class ReadOnlyBooks(viewsets.ReadOnlyModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n\n\n" +
        "class BookList(generics.ListCreateAPIView):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n\n\n" +
        "class Ping(APIView):\n    def get(self, request): ...\n\n\n" +
        "@api_view(['GET', 'POST'])\ndef status(request): ...\n\n\ndef plain(request, year): ...\n";

    const string Urls =
        "from django.urls import path, include\nfrom rest_framework.routers import DefaultRouter\nfrom shop import views\n\nrouter = DefaultRouter()\nrouter.register(r'books', views.BookViewSet, basename='book')\nrouter.register(r'readonly', views.ReadOnlyBooks)\n\n" +
        "urlpatterns = [\n    path('', include(router.urls)),\n    path('list/', views.BookList.as_view()),\n    path('ping/', views.Ping.as_view(), name='ping'),\n    path('status/', views.status),\n    path('year/<int:year>/', views.plain),\n]\n";

    static RouteEntry Find(IReadOnlyList<RouteEntry> routes, string name, string? path = null) =>
        routes.Single(r => r.Name == name && (path == null || r.Path == path));

    // ------------------------------------------------------------ plain views

    [Fact]
    public void IncludeAddsItsPrefixAndNothingIsCountedTwice()
    {
        var routes = RoutesOf(Urls);
        Assert.True(routes.Any(r => r.Path == "/api/ping/"));
        Assert.Equal(1, routes.Count(r => r.Name == "Ping"));
    }

    [Fact]
    public void AClassViewAnswersTheMethodsItDefines()
    {
        Assert.Equal("GET", Find(RoutesOf(Urls), "Ping").Methods);
    }

    [Fact]
    public void AGenericViewAnswersWhatItsBaseBrings()
    {
        var e = Find(RoutesOf(Urls), "BookList");
        Assert.Equal("GET, POST", e.Methods);
        Assert.Equal("/api/list/", e.Path);
        Assert.Equal("drf", e.Framework);
        Assert.Equal("BookSerializer", e.Response);
        Assert.True(e.Params!.Any(p => p.Kind == "body" && p.Type == "BookSerializer"));
    }

    [Fact]
    public void AFunctionViewTakesItsMethodsFromApiViewOrIsAny()
    {
        var routes = RoutesOf(Urls);
        Assert.Equal("GET, POST", Find(routes, "status").Methods);
        Assert.Equal("drf", Find(routes, "status").Framework);
        Assert.Equal("ANY", Find(routes, "plain").Methods);
        Assert.Equal("django", Find(routes, "plain").Framework);
    }

    [Fact]
    public void ConvertersBecomePathParametersWithTheirTypes()
    {
        var e = Find(RoutesOf(Urls), "plain");
        Assert.Equal("/api/year/{year}/", e.Path);
        var year = e.Params!.Single();
        Assert.Equal("path", year.Kind);
        Assert.Equal("int", year.Type);
        Assert.True(year.Required);
    }

    // ------------------------------------------------------------ routers and viewsets

    [Fact]
    public void AModelViewSetMakesTheStandardRoutes()
    {
        var routes = RoutesOf(Urls).Where(r => r.Name.StartsWith("BookViewSet.")).ToList();
        string Line(string action) { var r = Find(routes, "BookViewSet." + action); return r.Methods + " " + r.Path; }
        Assert.Equal("GET /api/books/", Line("list"));
        Assert.Equal("POST /api/books/", Line("create"));
        Assert.Equal("GET /api/books/{pk}/", Line("retrieve"));
        Assert.Equal("PUT /api/books/{pk}/", Line("update"));
        Assert.Equal("PATCH /api/books/{pk}/", Line("partial_update"));
        Assert.Equal("DELETE /api/books/{pk}/", Line("destroy"));
    }

    [Fact]
    public void ActionsMakeTheirOwnRoutes()
    {
        var routes = RoutesOf(Urls);
        var publish = Find(routes, "BookViewSet.publish");
        Assert.Equal("POST", publish.Methods);
        Assert.Equal("/api/books/{pk}/publish/", publish.Path);
        var recent = Find(routes, "BookViewSet.recent");
        Assert.Equal("GET", recent.Methods);
        Assert.Equal("/api/books/recent/", recent.Path);
    }

    [Fact]
    public void AReadOnlyViewSetOnlyReadsAndTakesItsPrefixAsWritten()
    {
        var routes = RoutesOf(Urls).Where(r => r.Name.StartsWith("ReadOnlyBooks.")).ToList();
        Assert.Equal("ReadOnlyBooks.list,ReadOnlyBooks.retrieve", string.Join(",", routes.Select(r => r.Name).OrderBy(n => n)));
        Assert.Equal("/api/readonly/", Find(routes, "ReadOnlyBooks.list").Path);
    }

    [Fact]
    public void TheListTakesTheViewSetsFiltersAndSearch()
    {
        var list = Find(RoutesOf(Urls), "BookViewSet.list");
        Assert.Equal("title,author,search", string.Join(",", list.Params!.Where(p => p.Kind == "query").Select(p => p.Name)));
        Assert.Equal("list[BookSerializer]", list.Response);
        Assert.Equal("permission IsAuthenticated", list.Tags![0]);
    }

    [Fact]
    public void CreateTakesABodyAndAnswersCreated()
    {
        var create = Find(RoutesOf(Urls), "BookViewSet.create");
        Assert.Equal("201", create.Status);
        var body = create.Params!.Single(p => p.Kind == "body");
        Assert.Equal("BookSerializer", body.Type);
        Assert.True(body.Required);
        Assert.True(!Find(RoutesOf(Urls), "BookViewSet.partial_update").Params!.Single(p => p.Kind == "body").Required);
    }

    [Fact]
    public void TheDetailRoutesTakeTheLookup()
    {
        var retrieve = Find(RoutesOf(Urls), "BookViewSet.retrieve");
        Assert.Equal("pk", retrieve.Params!.Single(p => p.Kind == "path").Name);
        Assert.Equal("204", Find(RoutesOf(Urls), "BookViewSet.destroy").Status);
    }

    [Fact]
    public void ALookupFieldOfTheViewSetIsTheParameter()
    {
        string views = Views.Replace("    permission_classes = [IsAuthenticated]\n", "    permission_classes = [IsAuthenticated]\n    lookup_field = 'slug'\n");
        var retrieve = Find(RoutesOf(Urls, views: views), "BookViewSet.retrieve");
        Assert.Equal("/api/books/{slug}/", retrieve.Path);
    }

    // ------------------------------------------------------------ the tree

    [Fact]
    public void ASecondIncludeInANestedUrlsModuleComposesThePrefixes()
    {
        string shop = "from django.urls import path, include\nfrom shop import views\n\nurlpatterns = [path('v1/', include('shop.v1')), path('ping/', views.Ping.as_view())]\n";
        var routes = RoutesOf(shop, views: Views);
        Assert.True(routes.Any(r => r.Path == "/api/ping/"));
    }

    [Fact]
    public void ARegexRouteIsShownWithoutItsAnchorsAndGroups()
    {
        string shop = "from django.urls import re_path\nfrom shop import views\n\nurlpatterns = [re_path(r'^archive/(?P<year>[0-9]{4})/$', views.plain)]\n";
        var e = Find(RoutesOf(shop), "plain");
        Assert.Equal("/api/archive/{year}/", e.Path);
        Assert.Equal("year", e.Params!.Single().Name);
    }

    [Fact]
    public void WithoutASettingsModuleTheUrlsNoOtherIncludesAreTheRoots()
    {
        var routes = RoutesOf(Urls, settings: null);
        Assert.True(routes.Any(r => r.Path == "/api/ping/"));
    }

    [Fact]
    public void AdminAndThingsThatAreNotViewsAreLeftOut()
    {
        string root = "from django.urls import path, include\nfrom django.contrib import admin\n\nurlpatterns = [path('admin/', admin.site.urls), path('api/', include('shop.urls'))]\n";
        var routes = RoutesOf(Urls, rootUrls: root);
        // the admin is one entry of a library (item 180), not views of the project; item 127 left it out
        Assert.Equal("library:/admin/…", string.Join(",", routes.Where(r => r.Path.StartsWith("/admin")).Select(r => r.Framework + ":" + r.Path)));
    }

    [Fact]
    public void AProjectWithoutUrlsHasNoDjangoRoutes()
    {
        Assert.Equal(0, DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models).Service.DjangoRoutes().Count);
    }

    [Fact]
    public void ViewsAreRecognisedByTheirBaseNamesWhenTheLibraryIsNotResolved()
    {
        // no rest_framework package at all: the bases are only names
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models)
            .With("shop/views.py", "from rest_framework import viewsets, generics\n\n\nclass BookViewSet(viewsets.ModelViewSet):\n    pass\n\n\nclass Things(generics.ListCreateAPIView):\n    pass\n")
            .With("shop/urls.py", "from django.urls import path, include\nfrom rest_framework.routers import DefaultRouter\nfrom shop import views\n\nrouter = DefaultRouter()\nrouter.register('books', views.BookViewSet)\n\nurlpatterns = [path('', include(router.urls)), path('things/', views.Things.as_view())]\n");
        var routes = b.Service.DjangoRoutes();
        Assert.Equal(6, routes.Count(r => r.Name.StartsWith("BookViewSet.")));
        Assert.Equal("GET, POST", routes.Single(r => r.Name == "Things").Methods);
        Assert.Equal("drf", routes.Single(r => r.Name == "Things").Framework);
    }
}
