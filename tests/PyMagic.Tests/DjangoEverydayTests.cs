using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// What an everyday Django project is made of, with the real Django (PYMAGIC_DJANGO_SITE; skipped where it is not installed): shortcuts, generic views,
/// custom QuerySets and managers through every chain, `relation__in` lookups, `_meta`, request.GET.
/// </summary>
public class DjangoEverydayTests
{
    const string Models = "from datetime import timedelta\n\nfrom django.conf import settings\nfrom django.db import models\nfrom django.urls import reverse\nfrom django.utils import timezone\n\n\ndef get_time(minutes: int = 0):\n    return timezone.now() + timedelta(minutes=minutes)\n\n\nclass Tag(models.Model):\n    name = models.CharField(max_length=50, unique=True)\n\n    def __str__(self):\n        return self.name\n\n\nclass AuthorQuerySet(models.QuerySet):\n    def active(self):\n        return self.filter(active=True)\n\n\nclass Author(models.Model):\n    name = models.CharField(max_length=100)\n    active = models.BooleanField(default=True)\n    user = models.OneToOneField(settings.AUTH_USER_MODEL, on_delete=models.CASCADE, null=True, blank=True)\n\n    objects = AuthorQuerySet.as_manager()\n\n\nclass Book(models.Model):\n    title = models.CharField(max_length=200)\n    author = models.ForeignKey(Author, on_delete=models.CASCADE, related_name='books')\n    tags = models.ManyToManyField(Tag, blank=True)\n    published = models.DateField(null=True)\n    price = models.DecimalField(max_digits=8, decimal_places=2)\n\n    def get_absolute_url(self):\n        return reverse('book', args=[self.pk])\n\n\nclass PublishedManager(models.Manager):\n    def get_queryset(self):\n        return super().get_queryset().filter(published__isnull=False)\n\n    def recent(self, days: int = 7):\n        return self.filter(published__gte=get_time())\n\n    def newest(self):\n        return self.order_by('-published').first()\n\n\nclass Article(models.Model):\n    title = models.CharField(max_length=100)\n    published = models.DateTimeField(null=True)\n\n    objects = models.Manager()\n    live = PublishedManager()\n";

    static (LanguageService Svc, string Uri)? Open(string code)
    {
        if (RealDjangoTests.Site is not { } site) return null;
        string dir = Path.Combine(Path.GetTempPath(), "pm_djeveryday_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "shop"));
        File.WriteAllText(Path.Combine(dir, "shop", "__init__.py"), "");
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(site);
        foreach (var (name, text) in new[] { ("models.py", Models), ("views.py", code) })
        {
            string path = Path.Combine(dir, "shop", name);
            File.WriteAllText(path, text);
            svc.Open(LanguageService.UriOfPath(path), text, 1);
        }
        return (svc, LanguageService.UriOfPath(Path.Combine(dir, "shop", "views.py")));
    }

    /// <summary>Each `V_name = ...` of the code as "V_name: type", from the hover.</summary>
    static string Types(LanguageService svc, string uri, string code)
    {
        var map = new LineMap(code);
        var found = new List<string>();
        int offset = 0;
        foreach (var line in code.Split('\n'))
        {
            int at = line.IndexOf("V_", StringComparison.Ordinal);
            if (at >= 0 && line.TrimStart().StartsWith("V_", StringComparison.Ordinal))
                found.Add(svc.Hover(uri, map.PositionOf(offset + at + 1))!.Contents.Value.Split('\n')[1]);
            offset += line.Length + 1;
        }
        return string.Join("\n", found);
    }

    static string Problems(LanguageService svc, string uri) =>
        string.Join(" / ", svc.Diagnostics(uri).Where(d => d.Severity <= 3 && !d.Message.Contains("should be lowercase") && !d.Message.StartsWith("PEP 8")).Select(d => d.Message));

    [Fact]
    public void ShortcutsAndGenericViewsGiveTheModel()
    {
        const string code = "from django.http import HttpRequest\nfrom django.shortcuts import aget_object_or_404, get_list_or_404, get_object_or_404\nfrom django.urls import reverse\nfrom django.views.generic import DetailView, ListView\n\nfrom .models import Author, Book\n\n\ndef show(request: HttpRequest):\n    V_book = get_object_or_404(Book, pk=1)\n    V_books = get_list_or_404(Author.objects.active(), name='x')\n    V_q = request.GET.get('q')\n    V_page = request.GET.get('page', '1')\n    V_ids = request.GET.getlist('id')\n    V_url = reverse('book', args=[1])\n    meta = Book._meta\n    names = (meta.model_name, meta.app_label, meta.verbose_name, meta.verbose_name_plural, meta.db_table, meta.object_name, meta.label, meta.label_lower, meta.pk, meta.concrete_model)\n    fields = [f.name for f in meta.get_fields()] + [f.name for f in meta.fields] + [f.name for f in meta.many_to_many] + [meta.get_field('title').name]\n    return V_book, V_books, V_q, V_page, V_ids, V_url, names, fields\n\n\nasync def ashow(request: HttpRequest):\n    V_abook = await aget_object_or_404(Book.objects.filter(price__gt=1), pk=1)\n    return V_abook\n\n\nclass BookDetail(DetailView):\n    model = Book\n\n    def get_context_data(self, **kwargs):\n        V_object = self.object\n        V_user = self.request.user\n        V_got = self.get_object()\n        return {'o': V_object, 'u': V_user, 'g': V_got}\n\n\nclass Books(ListView):\n    queryset = Book.objects.select_related('author')\n\n    def get_queryset(self):\n        V_super = super().get_queryset()\n        return V_super.filter(author__in=Author.objects.active(), author__isnull=False)\n";
        if (Open(code) is not var (svc, uri)) return;
        Assert.Equal("", Problems(svc, uri));
        Assert.Equal(
            "V_book: Book\nV_books: list[Author]\nV_q: str | None\nV_page: str\nV_ids: list[str]\nV_url: str\n" +
            "V_abook: Book\nV_object: Book\nV_user: User | AnonymousUser\nV_got: Book\nV_super: QuerySet[Book]", Types(svc, uri, code));
    }

    [Fact]
    public void CustomQuerySetsAndManagersKeepTheModelThroughChains()
    {
        const string code = "from .models import Article, Author\n\n\ndef run():\n    V_a = Author.objects.active()\n    V_b = Author.objects.filter(name='x').active()\n    V_c = Author.objects.active().filter(name='x').first()\n    V_d = Author.objects.active().active()\n    for author in Author.objects.active():\n        V_e = author\n    V_live = Article.live.all()\n    V_recent = Article.live.recent(3)\n    V_newest = Article.live.newest()\n    V_first = Article.live.recent().first()\n";
        if (Open(code) is not var (svc, uri)) return;
        Assert.Equal("", Problems(svc, uri));
        Assert.Equal("V_a: QuerySet[Author]\nV_b: QuerySet[Author]\nV_c: Author | None\nV_d: QuerySet[Author]\nV_e: Author\n" +
                     "V_live: QuerySet[Article]\nV_recent: QuerySet[Article]\nV_newest: Article | None\nV_first: Article | None", Types(svc, uri, code));
        // a custom method shows its def with what it gives for this model
        var hover = svc.Hover(uri, new LineMap(code).PositionOf(code.IndexOf("recent(3)", StringComparison.Ordinal) + 1))!;
        Assert.Equal("def recent(days: int = ...) -> QuerySet[Article]", hover.Contents.Value.Split('\n')[1]);
    }
}
