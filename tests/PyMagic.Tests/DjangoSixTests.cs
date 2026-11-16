using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Django 6.0 / 6.1 with the real Django (PYMAGIC_DJANGO_SITE; skipped where it is not installed): request.user, CompositePrimaryKey,
/// GeneratedField, `_id` and create_defaults in queries, what aggregate()/in_bulk()/dates() give, Choices.label, django.tasks, as_view().
/// </summary>
public class DjangoSixTests
{
    const string Models = "import uuid\n\nfrom django.conf import settings\nfrom django.db import models\n\n\nclass Category(models.Model):\n    name = models.CharField(max_length=100)\n    slug = models.SlugField()\n\n\nclass Product(models.Model):\n    class Status(models.TextChoices):\n        DRAFT = 'draft', 'Draft'\n        LIVE = 'live', 'Live'\n\n    id = models.UUIDField(primary_key=True, default=uuid.uuid4)\n    category = models.ForeignKey(Category, on_delete=models.CASCADE, related_name='products')\n    price = models.DecimalField(max_digits=10, decimal_places=2)\n    stock = models.PositiveIntegerField(default=0)\n    status = models.CharField(max_length=10, choices=Status, default=Status.DRAFT)\n    total = models.GeneratedField(expression=models.F('stock') * 2, output_field=models.IntegerField(), db_persist=True)\n\n\nclass Order(models.Model):\n    user = models.ForeignKey(settings.AUTH_USER_MODEL, on_delete=models.CASCADE, related_name='orders')\n    placed = models.DateTimeField(auto_now_add=True)\n\n\nclass OrderLine(models.Model):\n    pk = models.CompositePrimaryKey('order_id', 'product_id')\n    order = models.ForeignKey(Order, on_delete=models.CASCADE)\n    product = models.ForeignKey(Product, on_delete=models.PROTECT)\n";

    static (LanguageService Svc, string Uri)? Open(string code)
    {
        if (RealDjangoTests.Site is not { } site || !File.Exists(Path.Combine(site, "django", "tasks", "base.py"))) return null;   // Django 6.0 or later
        string dir = Path.Combine(Path.GetTempPath(), "pm_dj6_" + Guid.NewGuid().ToString("N"));
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

    /// <summary>What the hover says the name assigned on the line holding <paramref name="name"/> is.</summary>
    static string TypeOf(LanguageService svc, string uri, string code, string name)
    {
        int at = code.IndexOf(name + " = ", StringComparison.Ordinal);
        var hover = svc.Hover(uri, new LineMap(code).PositionOf(at));
        string line = hover!.Contents.Value.Split('\n')[1];
        return line[(line.IndexOf(": ", StringComparison.Ordinal) + 2)..];
    }

    [Fact]
    public void QueriesAndTheirValuesAreTyped()
    {
        const string code = "from django.db.models import Sum\n\nfrom .models import Order, OrderLine, Product\n\n\ndef run():\n    products = Product.objects.filter(category_id=1)\n    agg = Product.objects.aggregate(total=Sum('stock'))\n    by_id = Product.objects.in_bulk(['a'])\n    months = Order.objects.dates('placed', 'month')\n    label = Product.Status.LIVE.label\n    key = OrderLine.objects.get(order_id=1, product_id='x').pk\n    total = Product.objects.get(pk='x').total\n    made = Product.objects.update_or_create(id='x', defaults={'stock': 1}, create_defaults={'stock': 2})\n";
        if (Open(code) is not var (svc, uri)) return;
        Assert.Equal("", string.Join(" / ", svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => d.Message)));
        Assert.Equal("QuerySet[Product]", TypeOf(svc, uri, code, "products"));
        Assert.Equal("dict[str, Any]", TypeOf(svc, uri, code, "agg"));
        Assert.Equal("dict[Any, Product]", TypeOf(svc, uri, code, "by_id"));
        // date and UUID where the standard library is described (typeshed), unknown with the small bundled stubs
        string months = TypeOf(svc, uri, code, "months");
        Assert.True(months is "QuerySet[Order, date]" or "Unknown", months);
        Assert.Equal("str", TypeOf(svc, uri, code, "label"));
        string key = TypeOf(svc, uri, code, "key");
        Assert.True(key is "tuple[int, UUID]" or "tuple[int, Unknown]", key);
        Assert.Equal("int", TypeOf(svc, uri, code, "total"));
    }

    [Fact]
    public void ARequestHasItsUserAndAViewItsAsView()
    {
        const string code = "from django.http import HttpRequest\nfrom django.views.generic import ListView\n\nfrom .models import Order\n\n\nclass Orders(ListView):\n    model = Order\n\n\nview = Orders.as_view()\n\n\ndef mine(request: HttpRequest):\n    user = request.user\n    return Order.objects.filter(user=request.user, user_id=1).count()\n";
        if (Open(code) is not var (svc, uri)) return;
        Assert.Equal("", string.Join(" / ", svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => d.Message)));
        Assert.Equal("User | AnonymousUser", TypeOf(svc, uri, code, "user"));
    }

    [Fact]
    public void ManagerMethodsShowDjangosParameters()
    {
        const string code = "from .models import Product\n\n\nasync def run():\n    await Product.objects.aupdate_or_create(id='x', defaults={'stock': 1})\n    Product.objects.bulk_create([Product()], batch_size=10)\n    Product.objects.filter(stock=1).order_by('-stock').first()\n    Product.objects.get_or_create(id='x', defaults={'stock': 1}, bad=1)\n";
        if (Open(code) is not var (svc, uri)) return;
        string HoverAt(string name)
        {
            var hover = svc.Hover(uri, new LineMap(code).PositionOf(code.IndexOf(name, StringComparison.Ordinal) + 1));
            return hover!.Contents.Value.Split('\n')[1];
        }
        Assert.Equal("async def aupdate_or_create(defaults: Mapping[str, Any] | None = ..., create_defaults: Mapping[str, Any] | None = ..., **kwargs: Any) -> tuple[Product, bool]", HoverAt("aupdate_or_create"));
        Assert.Equal("def bulk_create(objs: Iterable[Product], batch_size: int | None = ..., ignore_conflicts: bool = ..., update_conflicts: bool = ..., update_fields: Sequence[str] | None = ..., unique_fields: Sequence[str] | None = ...) -> list[Product]", HoverAt("bulk_create"));
        Assert.Equal("def first() -> Product | None", HoverAt("first"));
        // only the unknown field is a problem: defaults is a parameter, not a lookup
        Assert.Equal("Cannot resolve keyword 'bad' into a field of Product. Choices are: pk, id, category, category_id, price, stock, status, total, orderline",
            string.Join(" / ", svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => d.Message)));
        var help = svc.SignatureHelp(uri, new LineMap(code).PositionOf(code.IndexOf("batch_size", StringComparison.Ordinal)));
        Assert.Equal(1, help!.ActiveParameter);
        Assert.Contains("batch_size: int | None", help.Signatures[0].Label);
        // the methods after `objects` are coloured as methods, as a def's are
        var data = svc.SemanticTokens(uri);
        var lines = code.Split('\n');
        var kinds = new Dictionary<string, string>();
        for (int i = 0, line = 0, ch = 0; i < data.Length; i += 5)
        {
            line += data[i];
            ch = data[i] == 0 ? ch + data[i + 1] : data[i + 1];
            kinds.TryAdd(lines[line].Substring(ch, data[i + 2]), LanguageService.SemanticTokenTypes[data[i + 3]]);
        }
        foreach (var name in new[] { "aupdate_or_create", "bulk_create", "filter", "order_by", "first", "get_or_create" }) Assert.Equal(name + ":method", name + ":" + kinds[name]);
        Assert.Equal("property", kinds["objects"]);
    }

    [Fact]
    public void ATaskOfDjangoTasksEnqueuesAResultOfItsReturn()
    {
        const string code = "from django.tasks import task\n\n\n@task(priority=10)\ndef restock(product_id: str, amount: int) -> int:\n    return amount\n\n\ndef run():\n    queued = restock.enqueue('x', 1)\n    value = queued.return_value\n    status = queued.status\n    later = restock.using(priority=1)\n";
        if (Open(code) is not var (svc, uri)) return;
        Assert.Equal("TaskResult", TypeOf(svc, uri, code, "queued"));
        Assert.Equal("int", TypeOf(svc, uri, code, "value"));
        Assert.Equal("TaskResultStatus", TypeOf(svc, uri, code, "status"));
        Assert.Equal("Task", TypeOf(svc, uri, code, "later"));
    }
}
