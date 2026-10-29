using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>REST framework serializers (item 130): declared and nested fields against the model and the Meta, and the serializers a view picks in get_serializer_class.</summary>
public class DrfSerializerTests
{
    /// <summary>A stand-in for the REST framework with the real package's modules and class names.</summary>
    static DjangoFixture.LanguageServiceBuilder Service() => DjangoFixture.Service()
        .With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models)
        .With("rest_framework/__init__.py", "")
        .With("rest_framework/fields.py",
            "class Field:\n    def __init__(self, *, read_only=False, write_only=False, required=None, default=None, source=None, **kwargs): ...\n\n\n" +
            "class CharField(Field): ...\n\n\nclass IntegerField(Field): ...\n\n\nclass ReadOnlyField(Field): ...\n\n\nclass HiddenField(Field): ...\n\n\n" +
            "class SerializerMethodField(Field):\n    def __init__(self, method_name=None, **kwargs): ...\n")
        .With("rest_framework/relations.py", "from rest_framework.fields import Field\n\n\nclass RelatedField(Field): ...\n\n\nclass PrimaryKeyRelatedField(RelatedField): ...\n\n\nclass StringRelatedField(RelatedField): ...\n")
        .With("rest_framework/serializers.py",
            "from rest_framework.fields import Field, CharField, IntegerField, ReadOnlyField, HiddenField, SerializerMethodField\nfrom rest_framework.relations import RelatedField, PrimaryKeyRelatedField, StringRelatedField\n\n\n" +
            "class BaseSerializer(Field):\n    def __init__(self, instance=None, data=None, many=False, **kwargs): ...\n\n\nclass Serializer(BaseSerializer): ...\n\n\nclass ModelSerializer(Serializer): ...\n")
        .With("rest_framework/viewsets.py", "class GenericViewSet: ...\n\n\nclass ModelViewSet(GenericViewSet): ...\n")
        .With("rest_framework/generics.py", "class GenericAPIView: ...\n\n\nclass ListCreateAPIView(GenericAPIView): ...\n")
        .With("rest_framework/routers.py", "class DefaultRouter:\n    urls = []\n\n    def register(self, prefix, viewset, basename=None): ...\n");

    const string Head = "from rest_framework import serializers\nfrom shop.models import Author, Book, Tag, Profile\n\n\n" +
        "class AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = ['id', 'name']\n\n\n" +
        "class TagSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Tag\n        fields = ['id', 'label']\n\n\n";

    static List<LspDiagnostic> Found(string code, string? only = "drf-serializer-field")
    {
        var svc = Service().With("shop/serializers.py", Head + code).Service;
        return svc.Diagnostics(DjangoFixture.Root + "shop/serializers.py").Where(d => only == null || d.Code == only).ToList();
    }

    static string Messages(IEnumerable<LspDiagnostic> d) => string.Join("\n", d.Select(x => x.Message));

    static string Serializer(string name, string model, string fields, string body) =>
        $"class {name}(serializers.ModelSerializer):\n{body}\n    class Meta:\n        model = {model}\n        fields = {fields}\n\n\n";

    // ------------------------------------------------------------ declared fields and the Meta

    [Fact]
    public void ADeclaredFieldMustBeInFields()
    {
        var d = Found(Serializer("BookSerializer", "Book", "['id', 'title']", "    shout = serializers.CharField(source='title')\n"));
        Assert.Equal(1, d.Count);
        Assert.Contains("'shout' is declared on BookSerializer but not listed in Meta.fields", d[0].Message);
    }

    [Fact]
    public void ADeclaredFieldMustNotBeExcluded()
    {
        var d = Found("class BookSerializer(serializers.ModelSerializer):\n    title = serializers.CharField()\n\n    class Meta:\n        model = Book\n        exclude = ['title']\n");
        Assert.Contains("listed in Meta.exclude", Messages(d));
    }

    [Fact]
    public void ASubclassMayLeaveOutTheFieldsItsParentDeclares()
    {
        string code = Serializer("BookSerializer", "Book", "['id', 'shout']", "    shout = serializers.CharField(source='title')\n") +
            "class ShortBookSerializer(BookSerializer):\n    class Meta(BookSerializer.Meta):\n        fields = ['id']\n";
        Assert.Equal("", Messages(Found(code)));
    }

    // ------------------------------------------------------------ what DRF reads from the object

    [Fact]
    public void AFieldTheModelDoesNotHave()
    {
        var d = Found(Serializer("BookSerializer", "Book", "['nickname', 'secret', 'who', 'deep', 'fine', 'everything', 'state']",
            "    nickname = serializers.CharField()\n    secret = serializers.CharField(write_only=True)\n    who = serializers.CharField(source='titel')\n" +
            "    deep = serializers.CharField(source='author.nme')\n    fine = serializers.CharField(source='author.name')\n    everything = serializers.CharField(source='*')\n    state = serializers.CharField(source='get_status_display')\n"));
        Assert.Equal("Book has no attribute 'nickname': DRF raises AttributeError when it reads 'nickname'\nBook has no attribute 'titel': DRF raises AttributeError when it reads 'who'\nAuthor has no attribute 'nme': DRF raises AttributeError when it reads 'deep'", Messages(d));
    }

    [Fact]
    public void AMethodFieldNeedsItsMethod()
    {
        string code = Serializer("BookSerializer", "Book", "['shout', 'whisper', 'other']",
            "    shout = serializers.SerializerMethodField()\n    whisper = serializers.SerializerMethodField()\n    other = serializers.SerializerMethodField(method_name='compute')\n\n" +
            "    def get_whisper(self, obj):\n        return obj.title.lower()\n\n    def compute(self, obj):\n        return 1\n");
        var d = Found(code);
        Assert.Equal(1, d.Count);
        Assert.Contains("has no method 'get_shout'", d[0].Message);
    }

    // ------------------------------------------------------------ nested serializers

    [Fact]
    public void NestedSerializersThatFit()
    {
        string code = Serializer("BookSerializer", "Book", "['id', 'author', 'tags', 'editor']",
            "    author = AuthorSerializer(read_only=True)\n    tags = TagSerializer(many=True, read_only=True)\n    editor = AuthorSerializer(allow_null=True)\n") +
            Serializer("AuthorWithBooks", "Author", "['id', 'book_set', 'edited', 'profile']",
            "    book_set = BookSerializer(many=True)\n    edited = serializers.StringRelatedField(many=True)\n    profile = serializers.PrimaryKeyRelatedField(read_only=True)\n");
        Assert.Equal("", Messages(Found(code)));
    }

    [Fact]
    public void ManyMustMatchTheRelation()
    {
        var d = Found(Serializer("BookSerializer", "Book", "['author', 'tags']", "    author = AuthorSerializer(many=True)\n    tags = TagSerializer()\n"));
        Assert.Equal("'author' is one Author: many=True makes DRF iterate over it and fail with \"'Author' object is not iterable\"\n'tags' holds many Tags: give it many=True, without it DRF serializes the manager instead of each Tag", Messages(d));
    }

    [Fact]
    public void ANestedSerializerForAnotherModel()
    {
        var d = Found(Serializer("BookSerializer", "Book", "['author']", "    author = TagSerializer()\n"));
        Assert.Equal(1, d.Count);
        Assert.Contains("TagSerializer is for Tag, but 'author' gives Authors", d[0].Message);
    }

    [Fact]
    public void AReverseRelationGetsTheNameOfItsAccessor()
    {
        string code = Serializer("BookSerializer", "Book", "['id']", "") + Serializer("AuthorWithBooks", "Author", "['id', 'books']", "    books = BookSerializer(many=True)\n");
        var d = Found(code);
        Assert.Equal(1, d.Count);
        Assert.Contains("Author has no attribute 'books'", d[0].Message);
        Assert.Contains("write source='book_set'", d[0].Message);
    }

    // ------------------------------------------------------------ get_serializer_class

    const string Serializers = "from rest_framework import serializers\nfrom shop.models import Author, Book\n\n\n" +
        "class AuthorSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Author\n        fields = ['id', 'name']\n\n\n" +
        "class BookSerializer(serializers.ModelSerializer):\n    author = AuthorSerializer(read_only=True)\n    author_id = serializers.PrimaryKeyRelatedField(source='author', write_only=True)\n\n" +
        "    class Meta:\n        model = Book\n        fields = ['id', 'title', 'pages', 'author', 'author_id', 'tags', 'published']\n\n\n" +
        "class BookListSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Book\n        fields = ['id', 'title']\n";

    static IReadOnlyList<RouteEntry> RoutesOf(string views)
    {
        var svc = Service().With("shop/serializers.py", Serializers).With("shop/views.py", views)
            .With("shop/urls.py", "from rest_framework.routers import DefaultRouter\nfrom shop import views\n\nrouter = DefaultRouter()\nrouter.register(r'books', views.BookViewSet)\n\nurlpatterns = router.urls\n")
            .With("config/__init__.py", "").With("config/urls.py", "from django.urls import path, include\n\nurlpatterns = [path('api/', include('shop.urls'))]\n")
            .With("config/settings.py", "ROOT_URLCONF = 'config.urls'\n").Service;
        return svc.DjangoRoutes();
    }

    const string ViewHead = "from rest_framework import viewsets\nfrom shop.models import Book\nfrom shop.serializers import BookSerializer, BookListSerializer, AuthorSerializer\n\n\n";

    [Fact]
    public void GetSerializerClassByAction()
    {
        var routes = RoutesOf(ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n\n    def get_serializer_class(self):\n        if self.action == 'list':\n            return BookListSerializer\n        return BookSerializer\n");
        Assert.Equal("list[BookListSerializer]", routes.Single(r => r.Name == "BookViewSet.list").Response);
        Assert.Equal("BookSerializer", routes.Single(r => r.Name == "BookViewSet.retrieve").Response);
        Assert.Equal("BookSerializer", routes.Single(r => r.Name == "BookViewSet.create").Params!.Single(p => p.Kind == "body").Type);
    }

    [Fact]
    public void GetSerializerClassByMethodAndByADict()
    {
        var byMethod = RoutesOf(ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookListSerializer\n\n    def get_serializer_class(self):\n        if self.request.method in ('POST', 'PUT', 'PATCH'):\n            return BookSerializer\n        return super().get_serializer_class()\n");
        Assert.Equal("list[BookListSerializer]", byMethod.Single(r => r.Name == "BookViewSet.list").Response);
        Assert.Equal("BookSerializer", byMethod.Single(r => r.Name == "BookViewSet.update").Response);

        var byDict = RoutesOf(ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n    serializer_classes = {'list': BookListSerializer}\n\n    def get_serializer_class(self):\n        return self.serializer_classes.get(self.action, self.serializer_class)\n");
        Assert.Equal("list[BookListSerializer]", byDict.Single(r => r.Name == "BookViewSet.list").Response);
        Assert.Equal("BookSerializer", byDict.Single(r => r.Name == "BookViewSet.retrieve").Response);
    }

    [Fact]
    public void AConditionTheEditorCannotReadGivesBoth()
    {
        var routes = RoutesOf(ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n\n    def get_serializer_class(self):\n        if self.request.user.is_staff:\n            return BookSerializer\n        return BookListSerializer\n");
        Assert.Equal("BookSerializer | BookListSerializer", routes.Single(r => r.Name == "BookViewSet.retrieve").Response);
    }

    [Fact]
    public void ASerializerForAnotherModelInGetSerializerClass()
    {
        string views = ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n\n    def get_serializer_class(self):\n        if self.action == 'list':\n            return AuthorSerializer\n        return BookSerializer\n";
        var svc = Service().With("shop/serializers.py", Serializers).With("shop/views.py", views).Service;
        var d = svc.Diagnostics(DjangoFixture.Root + "shop/views.py").Where(x => x.Code == "django-view-serializer").ToList();
        Assert.Equal(1, d.Count);
        Assert.Contains("AuthorSerializer is for Author, but the queryset of BookViewSet is Book", d[0].Message);
    }

    // ------------------------------------------------------------ the fields in the Endpoints tab

    [Fact]
    public void TheBodyListsTheWritableFields()
    {
        var routes = RoutesOf(ViewHead + "class BookViewSet(viewsets.ModelViewSet):\n    queryset = Book.objects.all()\n    serializer_class = BookSerializer\n");
        var body = routes.Single(r => r.Name == "BookViewSet.create").Params!.Single(p => p.Kind == "body");
        Assert.Equal("title: CharField!, pages: IntegerField, author_id: PrimaryKeyRelatedField!, tags: list[pk of Tag]!",
            string.Join(", ", body.Children!.Select(c => $"{c.Name}: {c.Type}{(c.Required ? "!" : "")}")));
    }

    [Fact]
    public void ANestedWritableSerializerShowsItsOwnFields()
    {
        string serializers = Serializers + "\n\nclass ProfileSerializer(serializers.ModelSerializer):\n    class Meta:\n        model = Profile\n        fields = ['id', 'bio']\n\n\n" +
            "class AuthorWithProfile(serializers.ModelSerializer):\n    profile = ProfileSerializer()\n\n    class Meta:\n        model = Author\n        fields = ['id', 'name', 'profile']\n";
        serializers = serializers.Replace("from shop.models import Author, Book", "from shop.models import Author, Book, Profile");
        var svc = Service().With("shop/serializers.py", serializers)
            .With("shop/views.py", "from rest_framework import viewsets\nfrom shop.models import Author\nfrom shop.serializers import AuthorWithProfile\n\n\nclass BookViewSet(viewsets.ModelViewSet):\n    queryset = Author.objects.all()\n    serializer_class = AuthorWithProfile\n")
            .With("shop/urls.py", "from rest_framework.routers import DefaultRouter\nfrom shop import views\n\nrouter = DefaultRouter()\nrouter.register(r'authors', views.BookViewSet)\n\nurlpatterns = router.urls\n")
            .With("config/__init__.py", "").With("config/urls.py", "from django.urls import path, include\n\nurlpatterns = [path('api/', include('shop.urls'))]\n")
            .With("config/settings.py", "ROOT_URLCONF = 'config.urls'\n").Service;
        var body = svc.DjangoRoutes().Single(r => r.Name == "BookViewSet.create").Params!.Single(p => p.Kind == "body");
        var profile = body.Children!.Single(c => c.Name == "profile");
        Assert.Equal("ProfileSerializer", profile.Type);
        Assert.Equal("bio", string.Join(",", profile.Children!.Select(c => c.Name)));
    }
}
