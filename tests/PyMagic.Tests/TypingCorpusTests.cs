using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>False reports found by checking well-known working libraries (flask, click, httpx, bs4, jinja2, rich, attrs) as project files.</summary>
public class TypingCorpusTests
{
    [Fact]
    public void AClassWithoutInitTakesStarArgumentsThatMayBeEmpty()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", "class View:\n    pass\n\n\ndef make(*args, **kwargs):\n    return View(*args, **kwargs)\n\n\ndef bad():\n    return View(1)\n");
        var found = ws.Evaluator.Inspect(m).Where(d => d.Message.EndsWith("takes no arguments")).ToList();
        Assert.Equal(1, found.Count);
        Assert.Equal(m.Source.IndexOf("View(1)") + 5, found[0].Start);
    }

    [Fact]
    public void AStandInClassForAMissingDependencyIsNotTheTypeWhenTheImportIsFound()
    {
        var ws = new Workspace();
        ws.AddModule("pkg.real", "class Pool:\n    def close(self) -> None: ...\n\n\nclass Proxy(Pool):\n    pass\n");
        ws.AddModule("pkg", "try:\n    from pkg.real import Proxy\nexcept ImportError:\n    class Proxy:\n        def __init__(self, *args, **kwargs):\n            raise RuntimeError('install socksio')\n");
        var m = ws.AddModule("main", "import pkg\n\np = pkg.Proxy()\np.close()\nV = p\n");
        Assert.Equal("Proxy", ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Last().Value).Display());
        Assert.Equal(0, ws.Evaluator.Inspect(m).Count(d => d.Message.StartsWith("Cannot find reference")));
        // a missing import: the stand-in is all there is
        var ws2 = new Workspace();
        ws2.AddModule("pkg", "try:\n    from pkg.absent import Proxy\nexcept ImportError:\n    class Proxy:\n        def close(self) -> None: ...\n");
        var m2 = ws2.AddModule("main", "import pkg\n\nV = pkg.Proxy()\n");
        Assert.Equal("Proxy", ws2.Evaluator.TypeOf(m2, AstChildren.Descendants(m2.Ast).OfType<AssignStmt>().Last().Value).Display());
    }

    [Fact]
    public void AModuleAFunctionAndAPlainTypeGoWhereTheyFit()
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open("file:///c:/proj/helpers.py", "x = 1\n", 1);
        svc.Open("file:///c:/proj/main.py", "import types\nfrom typing import Hashable\n\nimport helpers\n\n\ndef takes_module(m: types.ModuleType) -> None: ...\ndef takes_key(k: Hashable) -> None: ...\ndef takes_class(c: type[Exception]) -> None: ...\ndef takes_str(s: str) -> None: ...\ndef f() -> None: ...\n\n\ndef run(t: type) -> None:\n    takes_module(helpers)\n    takes_key(f)\n    takes_class(t)\n    for cls in ValueError.__mro__:\n        takes_class(cls)\n    takes_str(1)\n", 1);
        var found = svc.Diagnostics("file:///c:/proj/main.py").Where(d => d.Code == "argument-type").Select(d => d.Message).ToList();
        Assert.Equal("Expected type 'str', got 'int' instead", string.Join(" | ", found));
    }

    [Fact]
    public void AClassmethodSubclassStaysACallableClassMethod()
    {
        // Django's classonlymethod (View.as_view): a classmethod whose __get__ refuses instances
        var ws = new Workspace();
        var m = ws.AddModule("main", "class classonlymethod(classmethod):\n    def __get__(self, instance, cls=None):\n        if instance is not None:\n            raise AttributeError('class only')\n        return super().__get__(instance, cls)\n\n\nclass View:\n    @classonlymethod\n    def as_view(cls, **initkwargs):\n        def view(request):\n            return cls()\n        return view\n\n\nclass ListView(View):\n    pass\n\n\nV = ListView.as_view(template_name='x.html')\n");
        Assert.Equal("", string.Join(" | ", ws.Evaluator.Inspect(m).Select(d => d.Message)));
        Assert.Equal("function view", ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Last().Value).Display());
    }

    [Fact]
    public void AsyncWithAndAsyncForUseTheAsyncProtocol()
    {
        // niquests.AsyncSession: __enter__ refuses (NoReturn), __aenter__ gives the session; an async iterable without __iter__
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "from typing import NoReturn\n\n\nclass Session:\n    def __enter__(self) -> NoReturn:\n        raise SyntaxError('use async with')\n\n    async def __aenter__(self) -> 'Session':\n        return self\n\n    async def __aexit__(self, *a) -> None: ...\n\n    async def post(self, url: str) -> int: ...\n\n\n" +
            "class Rows:\n    def __aiter__(self) -> 'Rows':\n        return self\n\n    async def __anext__(self) -> str: ...\n\n\n" +
            "async def run():\n    async with Session() as s:\n        A = s\n        B = await s.post('x')\n    async for row in Rows():\n        C = row\n    D = [r async for r in Rows()]\n    with open('f') as f:\n        E = f\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("Session", Of("A"));
        Assert.Equal("int", Of("B"));
        Assert.Equal("str", Of("C"));
        Assert.Equal("list[str]", Of("D"));
        Assert.True(Of("E") != "Never", Of("E"));
        Assert.Equal("", string.Join(" | ", ws.Evaluator.Inspect(m).Where(d => d.Message.Contains("iterable")).Select(d => d.Message)));
    }

    [Fact]
    public void ADefWrappedByAHandMadeDescriptorReadsAsWhatItReturns()
    {
        // keyring's classproperty, aiohttp's reify, a lazy attribute: a decorator class with __get__ that says nothing of what it gives
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "class classproperty:\n    def __init__(self, fget):\n        self.fget = classmethod(fget)\n\n    def __get__(self, instance, owner=None):\n        return self.fget.__get__(None, owner)()\n\n\n" +
            "class lazy:\n    def __init__(self, func):\n        self.func = func\n\n    def __get__(self, obj, cls):\n        return self.func(obj)\n\n\n" +
            "class typed:\n    def __init__(self, func):\n        self.func = func\n\n    def __get__(self, obj, cls) -> int:\n        return 1\n\n\n" +
            "class Backend:\n    @classproperty\n    def priority(cls) -> float:\n        return 1.0\n\n    @lazy\n    def name(self) -> str:\n        return 'x'\n\n    @typed\n    def size(self) -> str:\n        return 'x'\n\n\n" +
            "A = Backend().priority\nB = Backend.priority\nC = Backend().name\nD = Backend().size\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("float", Of("A"));
        Assert.Equal("float", Of("B"));
        Assert.Equal("str", Of("C"));
        // a __get__ that says what it gives is believed
        Assert.Equal("int", Of("D"));
    }

    [Fact]
    public void AnAliasOfACallableIsThatCallableInAnnotations()
    {
        // starlette's ASGIApp = Callable[[Scope, Receive, Send], Awaitable[None]], fastapi's build_middleware_stack() -> ASGIApp
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "import typing\nfrom typing import Callable\n\nHandler = typing.Callable[[int], str]\nNested = Callable[[Handler], str]\n\n\n" +
            "def make() -> Handler: ...\ndef wrap() -> Nested: ...\n\n\nA = make()\nB = wrap()\nC = make()(1)\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("(int) -> str", Of("A"));
        Assert.Equal("((int) -> str) -> str", Of("B"));
        Assert.Equal("str", Of("C"));
    }

    [Fact]
    public void RuntimeUnionsPep695AliasesAndAnIterableIterAreTypes()
    {
        // uvicorn's `ASGIReceiveEvent = (A | B | ...)`, numpy's `type NDArray[S: np.generic] = np.ndarray[...]`, rich's `def __iter__(self) -> Iterable[...]`
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "from typing import Iterable, TypedDict\n\n\nclass A(TypedDict):\n    type: str\n\n\nclass B(TypedDict):\n    size: int\n\n\n" +
            "Event = (\n    A\n    | B\n)\nMaybe = A | None\n\ntype Arr[S: int] = list[S]\ntype Pair[K, V] = dict[K, V]\n\n\n" +
            "class Bag:\n    def __iter__(self) -> Iterable[str]: ...\n\n\n" +
            "def f1() -> Event: ...\ndef f2() -> Maybe: ...\ndef f3() -> Arr[bool]: ...\ndef f4() -> Pair[str, int]: ...\n\n\n" +
            "P = f1()\nQ = f2()\nR = f3()\nS = f4()\nfor item in Bag():\n    T = item\nU = 1 | 2\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("A | B", Of("P"));
        Assert.Equal("A | None", Of("Q"));
        Assert.Equal("list[bool]", Of("R"));
        Assert.Equal("dict[str, int]", Of("S"));
        Assert.Equal("str", Of("T"));
        // `|` of values is still the operator
        Assert.Equal("int", Of("U"));
    }

    [Fact]
    public void ASelfTypedByATypeVarIsTheReceiver()
    {
        // httpx's `async def __aenter__(self: T) -> T`, SQLAlchemy's and pydantic v1's `def create(cls: type[T]) -> T`
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "import typing\nfrom typing import TypeVar\n\nT = typing.TypeVar('T', bound='Client')\nM = TypeVar('M')\n\n\n" +
            "class Client:\n    async def __aenter__(self: T) -> T:\n        return self\n\n    async def __aexit__(self, *a) -> None: ...\n\n    def __enter__(self: T) -> T:\n        return self\n\n    def __exit__(self, *a) -> None: ...\n\n" +
            "    def copy(self: M) -> list[M]: ...\n\n    @classmethod\n    def create(cls: type[M]) -> M: ...\n\n\nclass AsyncClient(Client):\n    pass\n\n\n" +
            "async def run():\n    async with AsyncClient() as a:\n        A = a\n    with Client() as b:\n        B = b\n    C = AsyncClient().copy()\n    D = AsyncClient.create()\n    E = AsyncClient().create()\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("A:AsyncClient B:Client C:list[AsyncClient] D:AsyncClient E:AsyncClient", string.Join(" ", new[] { "A", "B", "C", "D", "E" }.Select(n => n + ":" + Of(n))));
        Assert.Equal("AsyncClient", Of("A"));
        Assert.Equal("Client", Of("B"));
        Assert.Equal("list[AsyncClient]", Of("C"));
        Assert.Equal("AsyncClient", Of("D"));
        Assert.Equal("AsyncClient", Of("E"));
    }

    [Fact]
    public void ANewTypeIsAClassOfItsName()
    {
        // websockets' `Subprotocol = NewType("Subprotocol", str)`, used in annotations and called to make one
        var svc = new PyMagic.Server.LanguageService();
        svc.SourceRoots.Add("c:/proj");
        const string uri = "file:///c:/proj/main.py";
        svc.Open(uri, "from typing import NewType, Optional\n\nUserId = NewType('UserId', int)\n\n\ndef find(user: UserId) -> Optional[UserId]: ...\ndef takes_int(n: int) -> None: ...\n\n\n" +
            "A = UserId(5)\nB = find(A)\ntakes_int(A)\n", 1);
        var ws = new Workspace();
        var m = ws.AddModule("main", "from typing import NewType, Optional\n\nUserId = NewType('UserId', int)\n\n\ndef find(user: UserId) -> Optional[UserId]: ...\n\n\nA = UserId(5)\nB = find(A)\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("UserId", Of("A"));
        Assert.Equal("UserId | None", Of("B"));
        // it is an int where an int is asked for
        Assert.Equal("", string.Join(" | ", svc.Diagnostics(uri).Where(d => d.Code == "argument-type").Select(d => d.Message)));
    }

    [Fact]
    public void AnAttributeAClassmethodSetsOnClsIsTheClasss()
    {
        // Django's TestCase.setUpTestData: `cls.author = ...`, then `self.author` in the tests
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "class Author:\n    pass\n\n\nclass Tests:\n    @classmethod\n    def setUpTestData(cls) -> None:\n        cls.author = Author()\n        cls.count = 3\n\n" +
            "    def test_it(self) -> None:\n        A = self.author\n        B = type(self).count\n\n\nC = Tests.author\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("Author", Of("A"));
        Assert.Equal("int", Of("B"));
        Assert.Equal("Author", Of("C"));
        Assert.Equal("", string.Join(" | ", ws.Evaluator.Inspect(m).Where(d => d.Message.StartsWith("Cannot find reference")).Select(d => d.Message)));
    }

    [Fact]
    public void AnEmptyClassItsModuleFillsWithSetattrHasWhatItIsGiven()
    {
        // Django's `class Style: pass`, given SUCCESS, ERROR, ... by make_style() with setattr
        var ws = new Workspace();
        ws.AddModule("color", "class Style:\n    pass\n\n\ndef make_style():\n    style = Style()\n    for role in ('SUCCESS', 'ERROR'):\n        setattr(style, role, str)\n    return style\n\n\nclass Plain:\n    pass\n");
        var m = ws.AddModule("main", "from color import Plain, Style\n\n\ndef run(s: Style, p: Plain) -> None:\n    s.SUCCESS('ok')\n    p.missing\n");
        // a class that is not filled that way is still checked
        Assert.Equal("Cannot find reference 'missing' in 'Plain'", string.Join(" | ", ws.Evaluator.Inspect(m).Where(d => d.Message.StartsWith("Cannot find reference")).Select(d => d.Message)));
    }

    [Fact]
    public void OverloadsLikeAsyncioGatherPickByArityAndStarredArguments()
    {
        // typeshed's asyncio.gather: fixed-arity overloads first, then *args ones; coroutine arguments are Coroutine[Any, Any, T]
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "from collections.abc import Awaitable, Coroutine\nfrom typing import Any, Literal, TypeVar, overload\n\nT = TypeVar('T')\nT1 = TypeVar('T1')\nT2 = TypeVar('T2')\n\n\n" +
            "@overload\ndef gather(a: Awaitable[T1], /, *, return_exceptions: Literal[False] = False) -> tuple[T1]: ...\n" +
            "@overload\ndef gather(a: Awaitable[T1], b: Awaitable[T2], /, *, return_exceptions: Literal[False] = False) -> tuple[T1, T2]: ...\n" +
            "@overload\ndef gather(*xs: Awaitable[T], return_exceptions: Literal[False] = False) -> list[T]: ...\n" +
            "@overload\ndef gather(*xs: Awaitable[T], return_exceptions: bool) -> list[T | BaseException]: ...\n" +
            "def gather(*xs: Any, return_exceptions: bool = False) -> Any: ...\n\n\n" +
            "def one() -> Coroutine[Any, Any, str]: ...\n\n\n" +
            "coros = [one(), one()]\nA = gather(one(), one())\nB = gather(*coros)\nC = gather(*(one() for _ in range(3)), return_exceptions=True)\nD = gather(one())\n");
        string Of(string name) => ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().First(a => a.Targets[0] is NameExpr { Name: var n } && n == name).Value).Display();
        Assert.Equal("A:tuple[str, str] B:list[str] C:list[str | BaseException] D:tuple[str]", string.Join(" ", new[] { "A", "B", "C", "D" }.Select(n => n + ":" + Of(n))));
    }

    [Fact]
    public void AValueSetInAWithBlockIsSetAfterItEvenWhenItsExitMaySwallow()
    {
        // anyio's `with CancelScope(): retval = await ...` then `else: future.set_result(retval)`; PyCharm does not count the exit swallowing
        var ws = new Workspace();
        var m = ws.AddModule("main",
            "from contextlib import suppress\n\n\nclass Scope:\n    def __enter__(self) -> 'Scope':\n        return self\n\n    def __exit__(self, *a) -> bool:\n        return True\n\n\n" +
            "def a() -> int:\n    try:\n        with Scope():\n            value = 1\n    except ValueError:\n        pass\n    else:\n        return value\n    return 0\n\n\n" +
            "def b(d: dict[str, int]) -> int:\n    with suppress(KeyError):\n        v = d['x']\n    return v\n\n\n" +
            "def c() -> None:\n    with Scope():\n        raise ValueError()\n    print('reached')\n\n\n" +
            "def e(flag: bool) -> int:\n    if flag:\n        w = 1\n    return w\n");
        var found = ws.Evaluator.FlowDiagnostics(m).Select(d => d.Message).ToList();
        // only the read a path really skips; the line after a swallowed raise is reached
        Assert.Equal("Local variable 'w' might be referenced before assignment", string.Join(" | ", found));
    }

    [Fact]
    public void AClassBodyUnderAnIfIsStillTheClassBody()
    {
        // pydantic v1's `if TYPE_CHECKING: __fields__: ClassVar[...] = {}`, anyio's `if sys.version_info >= (3, 13): @classmethod def from_uri`
        var svc = new PyMagic.Server.LanguageService();
        svc.SourceRoots.Add("c:/proj");
        const string uri = "file:///c:/proj/main.py";
        svc.Open(uri, "import sys\nfrom typing import TYPE_CHECKING, ClassVar\n\n\nclass Model:\n    if TYPE_CHECKING:\n        fields: ClassVar[dict[str, int]] = {}\n\n" +
            "    if sys.version_info >= (3, 13):\n        @classmethod\n        def make(cls) -> 'Model':\n            return cls()\n    else:\n        try:\n            @staticmethod\n            def other() -> int:\n                return 1\n        except ImportError:\n            pass\n\n\n" +
            "def outside() -> None:\n    loose: ClassVar[int] = 1\n", 1);
        var found = svc.Diagnostics(uri).Where(d => d.Code is "classvar" or "decorator-outside-class").Select(d => d.Range.Start.Line + 1 + ": " + d.Message).ToList();
        // only the ClassVar in a function is out of place
        Assert.Equal("23: ClassVar is allowed only in a class body", string.Join(" | ", found));
    }

    [Fact]
    public void ValidLibraryIdiomsAreNotReported()
    {
        // attrs' super(TypeError, self), anyio's `except get_cancelled_exc_class():` before `except BaseException:`, urllib3's
        // `__version__ = __version__` after importing it, anyio's `backend[0]` after `isinstance(backend, tuple)`
        var svc = new PyMagic.Server.LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open("file:///c:/proj/ver.py", "__version__ = '1'\n", 1);
        const string uri = "file:///c:/proj/main.py";
        svc.Open(uri, "from ver import __version__\n\n__version__ = __version__\n\n\ndef cancelled() -> type[BaseException]:\n    return KeyboardInterrupt\n\n\n" +
            "class NotCallable(TypeError):\n    def __init__(self, msg: str) -> None:\n        super(TypeError, self).__init__(msg)\n\n\nclass Other:\n    def __init__(self) -> None:\n        super(NotCallable, self).__init__()\n\n\n" +
            "def run(backend: object, n: int) -> None:\n    try:\n        pass\n    except cancelled():\n        pass\n    except BaseException:\n        raise\n    if isinstance(backend, tuple):\n        print(backend[0])\n    print(n[0])\n", 1);
        var found = svc.Diagnostics(uri).Where(d => d.Severity <= 2).Select(d => d.Range.Start.Line + 1 + ": " + d.Message).ToList();
        // what is left is wrong: a super() argument that is not a base, an int read as a sequence
        Assert.Equal("17: Wrong first argument passed to 'super()' | 29: 'int' object is not subscriptable", string.Join(" | ", found));
    }

    [Fact]
    public void TypeIgnoreOnADefSilencesItsOverrideAndMissingReturn()
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        const string uri = "file:///c:/proj/app.py";
        svc.Open(uri, "import sys\nfrom types import TracebackType\n\n\nclass B:\n    def run(self, a, b):\n        pass\n\n\nclass C(B):\n    def run(self, a=None):  # type: ignore\n        pass\n\n\nclass D(B):\n    def run(self, a=None):\n        pass\n\n\ndef tb(code) -> TracebackType:  # type: ignore\n    try:\n        exec(code)\n    except BaseException:\n        return sys.exc_info()[2]\n", 1);
        var d = svc.Diagnostics(uri);
        Assert.Equal("15", string.Join(",", d.Where(x => x.Code == "method-override").Select(x => x.Range.Start.Line)));
        Assert.Equal(0, d.Count(x => x.Code == "missing-return"));
    }
}
