using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Django REST framework as the real package describes itself (item 127). Skipped where Django or djangorestframework is not installed.</summary>
public class RealDrfTests
{
    static readonly string? DjangoSite = Find("PYMAGIC_DJANGO_SITE", Path.Combine("PycharmProjects", "django_chat", ".venv", "Lib", "site-packages"), Path.Combine("django", "db", "models", "__init__.py"));
    static readonly string? DrfSite = Find("PYMAGIC_DRF_SITE", null, Path.Combine("rest_framework", "viewsets.py"));

    static string? Find(string variable, string? underProfile, string marker)
    {
        var candidates = new[] { Environment.GetEnvironmentVariable(variable), underProfile == null ? null : Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", underProfile) };
        return candidates.FirstOrDefault(c => !string.IsNullOrEmpty(c) && File.Exists(Path.Combine(c, marker)));
    }

    static bool Missing => DjangoSite == null || DrfSite == null;

    const string Models =
        "from django.db import models\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n\n\n" +
        "class Book(models.Model):\n    title = models.CharField(max_length=200)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE)\n";

    const string Serializers =
        "from rest_framework import serializers\n\nfrom shop.models import Author, Book\n\n\nclass AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = '__all__'\n\n\n" +
        "class BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = '__all__'\n";

    const string Views =
        "from rest_framework import generics, viewsets\nfrom rest_framework.decorators import action, api_view\nfrom rest_framework.permissions import IsAuthenticated\nfrom rest_framework.response import Response\nfrom rest_framework.views import APIView\n\n" +
        "from shop.models import Author, Book\nfrom shop.serializers import AuthorSerializer, BookSerializer\n\n\n" +
        "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n    permission_classes = [IsAuthenticated]\n    filterset_fields = ['title', 'author']\n    search_fields = ['title']\n\n" +
        "    @action(detail=True, methods=['post'])\n    def publish(self, request, pk=None):\n        book = self.get_object()\n        serializer = self.get_serializer(book)\n        return Response(serializer.data)\n\n" +
        "    @action(detail=False, url_path='recent')\n    def latest(self, request):\n        return Response(request.query_params)\n\n\n" +
        "class ReadOnlyBooks(viewsets.ReadOnlyModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n\n\n" +
        "class AuthorList(generics.ListCreateAPIView):\n    queryset = Author.objects.all()\n    serializer_class = AuthorSerializer\n\n\n" +
        "class AuthorDetail(generics.RetrieveUpdateDestroyAPIView):\n    queryset = Author.objects.all()\n    serializer_class = AuthorSerializer\n\n\n" +
        "class Ping(APIView):\n    def get(self, request):\n        return Response('pong')\n\n\n" +
        "@api_view(['GET', 'POST'])\ndef status(request):\n    return Response({})\n\n\ndef archive(request, year):\n    ...\n";

    const string Urls =
        "from django.urls import path, include\nfrom rest_framework.routers import DefaultRouter\n\nfrom shop import views\n\nrouter = DefaultRouter()\nrouter.register(r'books', views.BookViewSet, basename='book')\nrouter.register(r'readonly', views.ReadOnlyBooks)\n\n" +
        "urlpatterns = [\n    path('authors/', views.AuthorList.as_view()),\n    path('authors/<int:pk>/', views.AuthorDetail.as_view()),\n    path('ping/', views.Ping.as_view()),\n    path('status/', views.status),\n    path('archive/<int:year>/', views.archive),\n] + router.urls\n";

    static LanguageService Project(string? serializers = null)
    {
        var svc = new LanguageService();
        string dir = Path.Combine(Path.GetTempPath(), "pm_realdrf_" + Guid.NewGuid().ToString("N"));
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
        Put("shop/serializers.py", serializers ?? Serializers);
        Put("shop/views.py", Views);
        Put("shop/urls.py", Urls);
        Put("config/urls.py", "from django.urls import path, include\n\nurlpatterns = [path('api/v1/', include('shop.urls'))]\n");
        Put("config/settings.py", "ROOT_URLCONF = 'config.urls'\nINSTALLED_APPS = ['shop']\n");
        return svc;
    }

    static string Line(RouteEntry r) => $"{r.Methods} {r.Path} {r.Name}";

    [Fact]
    public void EveryRouteOfARealDrfProjectIsListed()
    {
        if (Missing) return;
        var lines = Project().DjangoRoutes().Select(Line).OrderBy(x => x, StringComparer.Ordinal).ToList();
        var expected = new[]
        {
            "ANY /api/v1/archive/{year}/ archive", "GET /api/v1/ api-root (DefaultRouter)", "DELETE /api/v1/books/{pk}/ BookViewSet.destroy", "GET /api/v1/books/ BookViewSet.list",
            "GET /api/v1/books/{pk}/ BookViewSet.retrieve", "GET /api/v1/books/recent/ BookViewSet.latest", "GET /api/v1/ping/ Ping",
            "GET /api/v1/readonly/ ReadOnlyBooks.list", "GET /api/v1/readonly/{pk}/ ReadOnlyBooks.retrieve", "GET, PUT, PATCH, DELETE /api/v1/authors/{pk}/ AuthorDetail",
            "GET, POST /api/v1/authors/ AuthorList", "GET, POST /api/v1/status/ status", "PATCH /api/v1/books/{pk}/ BookViewSet.partial_update",
            "POST /api/v1/books/ BookViewSet.create", "POST /api/v1/books/{pk}/publish/ BookViewSet.publish", "PUT /api/v1/books/{pk}/ BookViewSet.update",
        }.OrderBy(x => x, StringComparer.Ordinal).ToList();
        Assert.Equal(string.Join("\n", expected), string.Join("\n", lines));
    }

    [Fact]
    public void ARealViewSetCarriesItsSerializerFiltersAndPermissions()
    {
        if (Missing) return;
        var routes = Project().DjangoRoutes();
        var list = routes.Single(r => r.Name == "BookViewSet.list");
        Assert.Equal("list[BookSerializer]", list.Response);
        Assert.Equal("title,author,search", string.Join(",", list.Params!.Where(p => p.Kind == "query").Select(p => p.Name)));
        Assert.Equal("permission IsAuthenticated", list.Tags![0]);
        var create = routes.Single(r => r.Name == "BookViewSet.create");
        Assert.Equal("201", create.Status);
        Assert.Equal("BookSerializer", create.Params!.Single(p => p.Kind == "body").Type);
        Assert.Equal("204", routes.Single(r => r.Name == "BookViewSet.destroy").Status);
    }

    [Fact]
    public void TheRealGenericViewsBringTheirOwnMethodsAndABody()
    {
        if (Missing) return;
        var detail = Project().DjangoRoutes().Single(r => r.Name == "AuthorDetail");
        Assert.Equal("GET, PUT, PATCH, DELETE", detail.Methods);
        Assert.Equal("pk", detail.Params!.Single(p => p.Kind == "path").Name);
        Assert.Equal("AuthorSerializer", detail.Params!.Single(p => p.Kind == "body").Type);
    }

    [Fact]
    public void TheViewsOfARealDrfProjectHaveNoWarnings()
    {
        if (Missing) return;
        var svc = Project();
        string uri = LanguageService.UriOfPath(Directory.GetDirectories(Path.GetTempPath(), "pm_realdrf_*").OrderByDescending(Directory.GetCreationTimeUtc).First() + "/shop/views.py");
        var problems = svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => $"{d.Range.Start.Line + 1}: {d.Message}").ToList();
        Assert.Equal("", string.Join("\n", problems));
    }

    [Fact]
    public void ASerializerFieldThatIsNotTheModelsIsReportedOnTheRealDrf()
    {
        if (Missing) return;
        string serializers = Serializers.Replace("class BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = '__all__'", "class BookSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ['title', 'titel', 'author']");
        var svc = Project(serializers);
        string uri = LanguageService.UriOfPath(Directory.GetDirectories(Path.GetTempPath(), "pm_realdrf_*").OrderByDescending(Directory.GetCreationTimeUtc).First() + "/shop/serializers.py");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "django-meta-field").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("titel", found[0].Message);
    }

    /// <summary>Each class has the mistakes the real DRF 3.18.3 fails on (AssertionError, AttributeError, TypeError) or serializes wrongly (a manager without many=True).</summary>
    [Fact]
    public void DeclaredAndNestedFieldsAreCheckedOnTheRealDrf()
    {
        if (Missing) return;
        string serializers = Serializers + "\n\nfrom django.contrib.auth.models import User, Group\n\n\n" +
            "class GroupSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Group\n        fields = ['id', 'name']\n\n\n" +
            "class Bad(serializers.ModelSerializer):\n    extra = serializers.CharField(source='title')\n    nickname = serializers.CharField()\n    secret = serializers.CharField(write_only=True)\n" +
            "    shout = serializers.SerializerMethodField()\n    author = AuthorSerializer(many=True)\n\n    class Meta:\n        model = Book\n        fields = ['id', 'nickname', 'secret', 'shout', 'author']\n\n\n" +
            "class AuthorBooks(serializers.ModelSerializer):\n    books = BookSerializer(many=True)\n    book_set = BookSerializer(many=True)\n\n    class Meta:\n        model = Author\n        fields = ['id', 'books', 'book_set']\n\n\n" +
            "class UserGroups(serializers.ModelSerializer):\n    groups = GroupSerializer()\n    full = serializers.ReadOnlyField(source='get_full_name')\n    staff = GroupSerializer(source='groups', many=True)\n\n" +
            "    class Meta:\n        model = User\n        fields = ['id', 'groups', 'full', 'staff']\n";
        var svc = Project(serializers);
        string uri = LanguageService.UriOfPath(Directory.GetDirectories(Path.GetTempPath(), "pm_realdrf_*").OrderByDescending(Directory.GetCreationTimeUtc).First() + "/shop/serializers.py");
        var found = svc.Diagnostics(uri).Where(d => d.Code == "drf-serializer-field").Select(d => $"{d.Range.Start.Line + 1}: {d.Message.Split(':')[0]}").ToList();
        Assert.Equal(string.Join("\n", new[]
        {
            "28: 'extra' is declared on Bad but not listed in Meta.fields", "29: Book has no attribute 'nickname'", "31: 'shout' is a SerializerMethodField and Bad has no method 'get_shout'",
            "32: 'author' is one Author", "40: Author has no attribute 'books'", "49: 'groups' holds many Groups",
        }), string.Join("\n", found));
    }

    [Fact]
    public void AViewWhoseSerializerIsForAnotherModelIsReportedOnTheRealDrf()
    {
        if (Missing) return;
        var svc = Project();
        string dir = Directory.GetDirectories(Path.GetTempPath(), "pm_realdrf_*").OrderByDescending(Directory.GetCreationTimeUtc).First();
        string wrong = Views.Replace("class AuthorList(generics.ListCreateAPIView):\n    queryset = Author.objects.all()\n    serializer_class = AuthorSerializer", "class AuthorList(generics.ListCreateAPIView):\n    queryset = Author.objects.all()\n    serializer_class = BookSerializer");
        string uri = LanguageService.UriOfPath(dir + "/shop/views.py");
        svc.Open(uri, wrong, 2);
        Assert.Equal(1, svc.Diagnostics(uri).Count(d => d.Code == "django-view-serializer"));
    }

    // ------------------------------------------------------------ plain serializers and depth in the views (item 149); each case run with Django 6.1 and DRF 3.18.3

    const string PlainAndDeep =
        "\n\nclass PlainBook(serializers.Serializer):\n    title = serializers.CharField()\n    titel = serializers.CharField()\n    secret = serializers.CharField(write_only=True)\n" +
        "    shout = serializers.SerializerMethodField()\n\n    def get_shout(self, obj):\n        return obj.title.upper()\n\n\n" +
        "class SavingBook(serializers.Serializer):\n    title = serializers.CharField()\n\n    def create(self, validated_data):\n        return Book.objects.create(**validated_data)\n\n\n" +
        "class DeepBook(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ['id', 'title', 'author']\n        depth = 1\n";

    const string MoreViews =
        "\n\nfrom shop.serializers import PlainBook, SavingBook, DeepBook\n\n\n" +
        "class PlainList(generics.ListCreateAPIView):\n    queryset = Book.objects.all()\n    serializer_class = PlainBook\n\n\n" +
        "class SavingDetail(generics.RetrieveUpdateAPIView):\n    queryset = Book.objects.all()\n    serializer_class = SavingBook\n\n\n" +
        "class SavingList(generics.ListCreateAPIView):\n    queryset = Book.objects.all()\n    serializer_class = SavingBook\n\n\n" +
        "class Custom(generics.CreateAPIView):\n    queryset = Author.objects.all()\n    serializer_class = SavingBook\n\n    def perform_create(self, serializer):\n        serializer.save()\n\n\n" +
        "class DeepList(generics.ListCreateAPIView):\n    queryset = Book.objects.all()\n    serializer_class = DeepBook\n\n\n" +
        "class DeepRead(generics.ListAPIView):\n    queryset = Book.objects.all()\n    serializer_class = DeepBook\n";

    [Fact]
    public void PlainSerializersAndDepthAreCheckedAgainstTheViewOnTheRealDrf()
    {
        if (Missing) return;
        var svc = Project(Serializers + PlainAndDeep);
        string dir = Directory.GetDirectories(Path.GetTempPath(), "pm_realdrf_*").OrderByDescending(Directory.GetCreationTimeUtc).First();
        string uri = LanguageService.UriOfPath(dir + "/shop/views.py");
        string views = Views + MoreViews;
        svc.Open(uri, views, 2);
        var lines = views.Split('\n');
        var found = svc.Diagnostics(uri).Where(d => d.Code == "drf-view-serializer")
            .Select(d => $"{lines[d.Range.Start.Line - 2].Trim().TrimEnd(':')}: {d.Message.Split(':')[0]}").ToList();
        Assert.Equal(string.Join("\n", new[]
        {
            "class PlainList(generics.ListCreateAPIView): PlainBook is a plain Serializer without create()",
            "class PlainList(generics.ListCreateAPIView): PlainBook reads 'titel' from each Book (its field 'titel'), and Book has no such attribute",
            "class SavingDetail(generics.RetrieveUpdateAPIView): SavingBook is a plain Serializer without update()",
            "class DeepList(generics.ListCreateAPIView): With depth = 1 in the Meta of DeepBook, 'author' is nested and read-only",
        }), string.Join("\n", found.OrderBy(x => views.IndexOf(x.Split(':')[0], StringComparison.Ordinal))));
        // the view of the depth serializer: author is not in what a POST may send
        svc.Open(LanguageService.UriOfPath(dir + "/shop/urls.py"), Urls.Replace("    path('ping/'", "    path('deep/', views.DeepList.as_view()),\n    path('ping/'"), 2);
        var body = svc.DjangoRoutes().Single(r => r.Name == "DeepList").Params!.Single(p => p.Kind == "body");
        Assert.Equal("title", string.Join(",", body.Children!.Select(c => c.Name)));
    }
}
