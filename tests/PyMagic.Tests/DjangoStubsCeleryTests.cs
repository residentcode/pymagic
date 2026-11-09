using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// `Address.objects` on a model built on django-stubs (whose ModelBase gives `objects` through a `__getattr__` typed `Literal["objects"]`),
/// the async query methods (`await Address.objects.afirst()`), TypeVar defaults (PEP 696, `_Row = TypeVar("_Row", default=_Model)`), and
/// Celery's `@shared_task` / `@app.task`, whose functions are tasks with `.delay`. Small stand-ins for the libraries, as in DjangoFixture.
/// </summary>
public class DjangoStubsCeleryTests
{
    const string Root = "file:///c:/proj/";
    const string Main = Root + "shop/views.py";

    /// <summary>The project's files (shop/...) opened in the editor; the libraries' files written to a folder read as site-packages.</summary>
    static LanguageService Service(params (string Path, string Text)[] files)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        string site = Path.Combine(Path.GetTempPath(), "pm_stubsite_" + Guid.NewGuid().ToString("N"));
        foreach (var (p, t) in files)
        {
            if (p.StartsWith("shop/", StringComparison.Ordinal)) { svc.Open(Root + p, t, 1); continue; }
            string path = Path.Combine(site, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, t);
        }
        svc.LibraryRoots.Add(site);
        return svc;
    }

    /// <summary>The type the hover shows for the name at the `|`.</summary>
    static string TypeAt(LanguageService svc, string marked, out string text)
    {
        int i = marked.IndexOf('|');
        text = marked.Remove(i, 1);
        svc.Open(Main, text, 1);
        var hover = svc.Hover(Main, new LineMap(text).PositionOf(i));
        Assert.True(hover != null, "hover at the cursor");
        string line = hover!.Contents.Value.Split('\n')[1];
        int colon = line.IndexOf(": ", StringComparison.Ordinal);
        return colon < 0 ? line : line[(colon + 2)..];
    }

    static List<string> Problems(LanguageService svc) =>
        svc.Diagnostics(Main).Where(d => d.Severity <= Severity.Warning).Select(d => d.Message).ToList();

    // ------------------------------------------------------------ django-stubs

    /// <summary>What django-stubs 6 says of Model, Manager and QuerySet, cut down: `objects` only through the metaclass.</summary>
    static readonly (string, string)[] DjangoStubs =
    {
        ("django/__init__.pyi", ""),
        ("django/db/__init__.pyi", ""),
        ("django/db/models/__init__.pyi",
            "from django.db.models.base import Model as Model\nfrom django.db.models.fields import CharField as CharField\nfrom django.db.models.manager import Manager as Manager\n"),
        ("django/db/models/fields.pyi", "from typing import Any\n\n\nclass CharField:\n    def __init__(self, *args: Any, **kwargs: Any) -> None: ...\n"),
        ("django/db/models/base.pyi",
            "from typing import Any, Literal, TypeVar\nfrom django.db.models.manager import Manager\n\n_Self = TypeVar(\"_Self\", bound=Model)\n\n\n" +
            "class ModelBase(type):\n    @property\n    def _default_manager(cls: type[_Self]) -> Manager[_Self]: ...\n" +
            "    def __getattr__(cls: type[_Self], name: Literal[\"objects\"]) -> Manager[_Self]: ...\n\n\n" +
            "class Model(metaclass=ModelBase):\n    pk: Any\n    def save(self) -> None: ...\n"),
        ("django/db/models/manager.pyi",
            "from typing import Any, Generic, TypeVar\nfrom django.db.models.query import QuerySet\n\n_T = TypeVar(\"_T\")\n\n\n" +
            "class Manager(Generic[_T]):\n    def filter(self, *args: Any, **kwargs: Any) -> QuerySet[_T]: ...\n"),
        ("django/db/models/query.pyi",
            "from typing import Any, Generic\nfrom typing_extensions import TypeVar\n\n_Model = TypeVar(\"_Model\", covariant=True)\n_Row = TypeVar(\"_Row\", covariant=True, default=_Model)\n\n\n" +
            "class QuerySet(Generic[_Model, _Row]):\n    def first(self) -> _Row | None: ...\n    async def afirst(self) -> _Row | None: ...\n    def filter(self, *args: Any, **kwargs: Any) -> QuerySet[_Model, _Row]: ...\n"),
        ("shop/__init__.py", ""),
        ("shop/models.py", "from django.db import models\n\n\nclass Address(models.Model):\n    user = models.CharField(max_length=20)\n    line_id = models.CharField(max_length=20)\n"),
    };

    [Fact]
    public void ObjectsOnAModelOfDjangoStubsIsItsManager()
    {
        var svc = Service(DjangoStubs);
        Assert.Equal("Manager[Address]", TypeAt(svc, "from shop import models\n\nm = models.Address.obj|ects\n", out _));
        Assert.Equal("", string.Join(" / ", Problems(svc)));
    }

    [Fact]
    public void AwaitedAfirstOfDjangoStubsIsTheModelOrNone()
    {
        var svc = Service(DjangoStubs);
        string code = "from shop import models\n\n\nasync def view(request, line_id):\n    a| = await models.Address.objects.filter(user=request.user, line_id=line_id).afirst()\n    return a\n";
        Assert.Equal("Address | None", TypeAt(svc, code, out _));
        Assert.Equal("", string.Join(" / ", Problems(svc)));
    }

    [Fact]
    public void TheMetaclassGetattrAnswersOnlyTheNamesItsLiteralSays()
    {
        var svc = Service(DjangoStubs);
        TypeAt(svc, "from shop import models\n\nm = models.Address.obj|ects\nn = models.Address.nothing\n", out _);
        Assert.Equal("Cannot find reference 'nothing' in 'type[Address]'", string.Join(" / ", Problems(svc)));
    }

    // ------------------------------------------------------------ Django's own source: the async query methods

    [Fact]
    public void TheAsyncQueryMethodsGiveWhatTheirPlainFormsGive()
    {
        var b = DjangoFixture.Service().With("shop/__init__.py", "").With("shop/models.py", DjangoFixture.Models);
        var svc = b.Service;
        string head = "from shop.models import Book\n\n\nasync def view():\n";
        Assert.Equal("Book | None", TypeAt(svc, head + "    b| = await Book.objects.filter(pages=1).afirst()\n", out _));
        Assert.Equal("Book", TypeAt(svc, head + "    b| = await Book.objects.aget(pk=1)\n", out _));
        Assert.Equal("int", TypeAt(svc, head + "    b| = await Book.objects.acount()\n", out _));
        Assert.Equal("bool", TypeAt(svc, head + "    b| = await Book.objects.aexists()\n", out _));
        Assert.Equal("tuple[Book, bool]", TypeAt(svc, head + "    b| = await Book.objects.aget_or_create(pages=1)\n", out _));
    }

    // ------------------------------------------------------------ TypeVar defaults

    [Fact]
    public void ATypeVarDefaultFillsWhatTheSubscriptLeavesOut()
    {
        var svc = Service(("q.py", "from typing import Generic, TypeVar\n\nT = TypeVar('T')\nR = TypeVar('R', default=T)\nS = TypeVar('S', default=str)\n\n\n" +
                                   "class Q(Generic[T, R]):\n    def first(self) -> R: ...\n\n\nclass P(Generic[T, S]):\n    def second(self) -> S: ...\n"));
        Assert.Equal("int", TypeAt(svc, "from q import Q, P\n\n\ndef f(q: Q[int]):\n    x| = q.first()\n", out _));
        Assert.Equal("bytes", TypeAt(svc, "from q import Q, P\n\n\ndef f(q: Q[int, bytes]):\n    x| = q.first()\n", out _));
        Assert.Equal("str", TypeAt(svc, "from q import Q, P\n\n\ndef f(p: P[int]):\n    x| = p.second()\n", out _));
    }

    [Fact]
    public void TypeVarOfTheRealTypingExtensionsIsATypeVar()
    {
        // the real typing_extensions.py binds TypeVar in both arms of a version check: typing's, or a backport class of its own
        var svc = Service(
            ("typing_extensions.py", "import sys\nimport typing\n\nif sys.version_info >= (3, 13):\n    from typing import TypeVar\nelse:\n    class TypeVar:\n        def __new__(cls, name, *constraints, bound=None, default=None): ...\n"),
            ("q.py", "from typing import Generic\nfrom typing_extensions import TypeVar\n\nT = TypeVar('T')\nR = TypeVar('R', default=T)\n\n\nclass Q(Generic[T, R]):\n    def first(self) -> R: ...\n"));
        Assert.Equal("int", TypeAt(svc, "from q import Q\n\n\ndef f(q: Q[int]):\n    x| = q.first()\n", out _));
    }

    // ------------------------------------------------------------ Celery

    /// <summary>Celery's own source, cut down: shared_task hands out a Proxy the engine cannot follow, as the real one does.</summary>
    static readonly (string, string)[] Celery =
    {
        ("celery/__init__.py", "from celery.app import shared_task\nfrom celery.app.base import Celery\n"),
        ("celery/app/__init__.py",
            "from celery.local import Proxy\n\n\ndef shared_task(*args, **kwargs):\n    def create_shared_task(**options):\n        def __inner(fun):\n" +
            "            return Proxy(lambda: fun)\n        return __inner\n    if len(args) == 1 and callable(args[0]):\n        return create_shared_task(**kwargs)(args[0])\n" +
            "    return create_shared_task(*args, **kwargs)\n"),
        ("celery/local.py", "class Proxy:\n    def __init__(self, local):\n        self._local = local\n\n    def __getattr__(self, name):\n        return getattr(self._local(), name)\n"),
        ("celery/app/base.py", "class Celery:\n    def task(self, *args, **opts):\n        def inner(fun):\n            return fun\n        return inner\n"),
        ("celery/app/task.py",
            "class Task:\n    def delay(self, *args, **kwargs):\n        \"\"\"Star argument version of apply_async.\"\"\"\n        return self.apply_async(args, kwargs)\n\n" +
            "    def apply_async(self, args=None, kwargs=None, **options): ...\n"),
        ("shop/__init__.py", ""),
        ("shop/tools.py",
            "from celery import shared_task, Celery\n\napp = Celery()\n\n\n@shared_task\ndef send_to_admin(subject, message):\n    return subject\n\n\n" +
            "@shared_task(bind=True)\ndef retry_me(self, n):\n    return n\n\n\n@app.task\ndef on_app(x):\n    return x\n"),
    };

    [Fact]
    public void ASharedTaskIsACeleryTask()
    {
        var svc = Service(Celery);
        Assert.Contains("def delay", HoverAt(svc, "from shop import tools\n\ntools.send_to_admin.de|lay('a', 'b')\n"));
        Assert.Contains("def delay", HoverAt(svc, "from shop import tools\n\ntools.retry_me.de|lay(1)\n"));
        Assert.Contains("def delay", HoverAt(svc, "from shop import tools\n\ntools.on_app.de|lay(1)\n"));
        Assert.Equal("", string.Join(" / ", Problems(svc)));
    }

    [Fact]
    public void AFunctionUnderADecoratorNotUnderstoodHasAnyAttribute()
    {
        // celery not installed: what the decorator makes of the function is not known, so `.delay` is not reported
        var svc = Service(("shop/__init__.py", ""),
            ("shop/tools.py", "from celery import shared_task\n\n\n@shared_task\ndef send_to_admin(subject, message):\n    return subject\n\n\ndef plain():\n    pass\n"));
        // the full typeshed, as PyCharm's is on most machines: its `function` class is what a miss on a def is reported against
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
            if (File.Exists(Path.Combine(d.FullName, "typeshed", "stdlib", "VERSIONS"))) { svc.TypeshedPath = Path.Combine(d.FullName, "typeshed"); break; }
        HoverAt(svc, "from shop import tools\n\ntools.send_to_admin.de|lay('a', 'b')\ntools.plain.delay()\n");
        Assert.Equal("Cannot find reference 'delay' in 'function'", string.Join(" / ", Problems(svc).Where(p => p.StartsWith("Cannot find"))));
    }

    static string HoverAt(LanguageService svc, string marked)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        svc.Open(Main, text, 1);
        return svc.Hover(Main, new LineMap(text).PositionOf(i))?.Contents.Value ?? "(nothing)";
    }
}
