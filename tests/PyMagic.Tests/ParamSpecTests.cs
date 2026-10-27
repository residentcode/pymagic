using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>ParamSpec (PEP 612): a decorator typed `(f: Callable[P, R]) -> Callable[P, R]` keeps the parameters of what it decorates, so calls through it are checked.</summary>
public class ParamSpecTests
{
    const string Head = "from typing import Callable, Concatenate, Iterator, ParamSpec, TypeVar, overload\n\nP = ParamSpec(\"P\")\nR = TypeVar(\"R\")\nT = TypeVar(\"T\")\n\n\ndef deco(f: Callable[P, R]) -> Callable[P, R]: ...\n\n\n";

    static string Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return string.Join("; ", ws.Evaluator.Inspect(m, true).Select(d => d.Message));
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    /// <summary>The hover text with the cursor where <paramref name="needle"/> is written last.</summary>
    static string HoverAt(string src, string needle)
    {
        const string uri = "file:///c:/paramspec/main.py";
        var svc = new LanguageService();
        svc.Open(uri, src, 1);
        int at = src.LastIndexOf(needle, StringComparison.Ordinal);
        int line = src.Take(at).Count(c => c == '\n');
        int column = at - (src.LastIndexOf('\n', Math.Max(at - 1, 0)) + 1);
        return svc.Hover(uri, new Position(line, column))?.Contents.Value ?? "";
    }

    static bool HasStdlib() => new Workspace().GetModule("contextlib") != null && new Workspace().GetModule("functools") != null;

    [Fact]
    public void ParamSpecStandsForAParameterList()
    {
        Assert.Equal("P", Last(Head + "P\n"));
        Assert.Equal("(**P) -> int", Last(Head + "def f(x: Callable[P, int]):\n    x\n"));
        Assert.Equal("(int, str, **P) -> int", Last(Head + "def f(x: Callable[Concatenate[int, str, P], int]):\n    x\n"));
    }

    [Fact]
    public void ADecoratedFunctionKeepsItsParameters()
    {
        string src = Head + "@deco\ndef g(x: int, y: str = 'a', *, z: bool = False) -> int: ...\n\n\n";
        Assert.Equal("Parameter 'x' unfilled", Found(src + "g()\n"));
        Assert.Equal("", Found(src + "g(1)\ng(1, 'b', z=True)\ng(x=1)\n"));
        Assert.Equal("Unexpected argument 'w'", Found(src + "g(1, w=2)\n"));
        Assert.Equal("Unexpected argument", Found(src + "g(1, 'b', 3)\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(src + "g('no')\n"));
        Assert.Equal("(int, str, bool) -> int", Last(src + "g\n"));
        Assert.Equal("int", Last(src + "g(1)\n"));
    }

    [Fact]
    public void Pep695ParamSpecParameters()
    {
        Assert.Equal("Parameter 'x' unfilled", Found("from typing import Callable\n\n\ndef deco[**P, R](f: Callable[P, R]) -> Callable[P, R]: ...\n\n\n@deco\ndef g(x: int) -> int: ...\n\n\ng()\n"));
        // a class over a parameter list keeps the places of its other type parameters
        Assert.Equal("int", Last("from typing import Callable\n\n\nclass Box[**P, R]:\n    def __init__(self, f: Callable[P, R]) -> None: ...\n    def get(self) -> R: ...\n\n\ndef g(x: int) -> int: ...\n\n\nBox(g).get()\n"));
    }

    [Fact]
    public void TypingExtensionsParamSpec()
    {
        Assert.Equal("Parameter 'x' unfilled", Found("import typing_extensions as te\nfrom typing import Callable, TypeVar\n\nP = te.ParamSpec(\"P\")\nR = TypeVar(\"R\")\n\n\ndef deco(f: te.Callable[P, R]) -> \"Callable[P, R]\": ...\n\n\n@deco\ndef g(x: int) -> int: ...\n\n\ng()\n"));
    }

    [Fact]
    public void ParametersComeFromABoundMethodWithoutItsReceiver()
    {
        string src = Head + "class C:\n    def meth(self, a: int) -> int: ...\n\n\n";
        Assert.Equal("Parameter 'a' unfilled", Found(src + "deco(C().meth)()\n"));
        Assert.Equal("", Found(src + "deco(C().meth)(1)\n"));
        Assert.Equal("int", Last(src + "deco(C().meth)(1)\n"));
    }

    [Fact]
    public void ParametersComeFromACallableWithAParameterList()
    {
        Assert.Equal("Parameter '_1' unfilled", Found(Head + "def use(cb: Callable[[int, str], bool]):\n    d = deco(cb)\n    d(1, 'a')\n    d(1)\n"));
    }

    [Fact]
    public void ParametersComeFromAClassConstructor()
    {
        string src = Head + "class Point:\n    def __init__(self, x: int, y: int = 0) -> None: ...\n\n\n";
        Assert.Equal("Parameter 'x' unfilled", Found(src + "deco(Point)()\n"));
        Assert.Equal("Unexpected argument", Found(src + "deco(Point)(1, 2, 3)\n"));
        Assert.Equal("Point", Last(src + "deco(Point)(1)\n"));
        // the __init__ a dataclass is given
        Assert.Equal("Parameter 'name' unfilled", Found("from dataclasses import dataclass\n" + Head + "@dataclass\nclass Item:\n    name: str\n\n\ndeco(Item)()\n"));
    }

    [Fact]
    public void ParametersComeFromAnInstanceThatIsCalled()
    {
        string src = Head + "class Caller:\n    def __call__(self, q: float, *, flag: bool = False) -> str: ...\n\n\n";
        Assert.Equal("Parameter 'q' unfilled", Found(src + "deco(Caller())()\n"));
        Assert.Equal("", Found(src + "deco(Caller())(1.0, flag=True)\n"));
    }

    [Fact]
    public void AGenericFunctionStaysGeneric()
    {
        Assert.Equal("str", Last(Head + "def ident(x: T) -> T: ...\n\n\ndeco(ident)('s')\n"));
    }

    [Fact]
    public void AnOverloadedFunctionLeavesTheParametersOpen()
    {
        // which overload a call through the decorator would pick is not known yet: it takes any arguments
        string src = Head + "@overload\ndef ov(a: int) -> int: ...\n@overload\ndef ov(a: str) -> str: ...\n\n\n";
        Assert.Equal("", Found(src + "deco(ov)(1, 2, 3)\n"));
        Assert.Equal("(...) -> Unknown", Last(src + "deco(ov)\n"));
    }

    [Fact]
    public void ConcatenateStripsTheLeadingParameters()
    {
        string src = Head + "def strip_int(f: Callable[Concatenate[int, P], R]) -> Callable[P, R]: ...\n\n\n@strip_int\ndef h(n: int, label: str) -> str: ...\n\n\n";
        Assert.Equal("Parameter 'label' unfilled", Found(src + "h()\n"));
        Assert.Equal("", Found(src + "h('a')\nh(label='a')\n"));
        Assert.Equal("(str) -> str", Last(src + "h\n"));
    }

    [Fact]
    public void ConcatenatePrependsParameters()
    {
        string src = Head + "def add_str(f: Callable[P, R]) -> Callable[Concatenate[str, P], R]: ...\n\n\n@add_str\ndef k(n: int) -> float: ...\n\n\n";
        Assert.Equal("(str, int) -> float", Last(src + "k\n"));
        Assert.Equal("", Found(src + "k('s', 1)\nk('s', n=1)\n"));
        Assert.Equal("Parameter 'n' unfilled", Found(src + "k('s')\n"));
    }

    [Fact]
    public void ALeadingTypeVariableIsSolvedFromTheFirstParameter()
    {
        string src = Head + "def first(f: Callable[Concatenate[T, P], R]) -> Callable[P, T]: ...\n\n\ndef f(a: int, b: str) -> None: ...\n\n\n";
        Assert.Equal("int", Last(src + "first(f)('b')\n"));
        Assert.Equal("Parameter 'b' unfilled", Found(src + "first(f)()\n"));
    }

    [Fact]
    public void ArgsAndKwargsOfAParamSpecAreAccepted()
    {
        string src = Head + "def logged(f: Callable[P, R]) -> Callable[P, R]:\n    def inner(*args: P.args, **kwargs: P.kwargs) -> R:\n        print(args, kwargs)\n        f(1, 2, x=3)\n        return f(*args, **kwargs)\n    return inner\n\n\n"
            + "def pre(f: Callable[Concatenate[int, P], R]) -> Callable[P, R]:\n    def inner(*args: P.args, **kwargs: P.kwargs) -> R:\n        return f(1, *args, **kwargs)\n    return inner\n";
        Assert.Equal("", Found(src));
        // the leading parameters are known even while P is not
        Assert.Equal("Parameter '_0' unfilled", Found(Head + "def pre(f: Callable[Concatenate[int, P], R]) -> None:\n    f()\n"));
    }

    [Fact]
    public void DecoratedMethodsAreBoundLikeTheirDefs()
    {
        string src = Head + "class C:\n    @deco\n    def plain(self, q: int) -> int: ...\n\n    @classmethod\n    @deco\n    def make(cls, w: int) -> int: ...\n\n    @staticmethod\n    @deco\n    def st(v: int) -> int: ...\n\n\n";
        Assert.Equal("", Found(src + "C().plain(1)\nC.plain(C(), 1)\nC.make(1)\nC().make(1)\nC.st(1)\nC().st(1)\n"));
        Assert.Equal("Parameter 'q' unfilled", Found(src + "C().plain()\n"));
        Assert.Equal("Parameter 'w' unfilled", Found(src + "C.make()\n"));
        Assert.Equal("Parameter 'v' unfilled", Found(src + "C.st()\n"));
        // a decorated function made a class attribute is bound as a def there would be
        Assert.Equal("Parameter 'n' unfilled", Found(Head + "def helper(self, n: int) -> int: ...\n\n\nclass H:\n    h = deco(helper)\n\n\nH().h(1)\nH().h()\n"));
    }

    [Fact]
    public void DecoratedConstructorsCallsAndPropertiesWorkAsTheirDefs()
    {
        string src = Head + "class K:\n    @deco\n    def __new__(cls, x: int) -> 'K': ...\n\n\nclass I:\n    @deco\n    def __init__(self, y: str) -> None: ...\n\n\n"
            + "class Pr:\n    @property\n    @deco\n    def p(self) -> int: ...\n\n    @deco\n    def __call__(self, z: int) -> str: ...\n\n\n";
        Assert.Equal("", Found(src + "K(1)\nI('a')\nPr().p.bit_length()\nPr()(1)\n"));
        Assert.Equal("Parameter 'x' unfilled; Parameter 'y' unfilled; Parameter 'z' unfilled", Found(src + "K()\nI()\nPr()()\n"));
        Assert.Equal("int", Last(src + "Pr().p\n"));
    }

    [Fact]
    public void ADecoratorFactoryKeepsTheParameters()
    {
        string src = Head + "def retry(n: int) -> Callable[[Callable[P, R]], Callable[P, R]]: ...\n\n\n@retry(3)\ndef r(a: int, /, b: str, *, c: bool = True) -> bytes: ...\n\n\n";
        Assert.Equal("", Found(src + "r(1, 'b', c=False)\n"));
        Assert.Equal("Parameter 'b' unfilled", Found(src + "r(1)\n"));
        Assert.Equal("Unexpected argument 'a'; Parameter 'a' unfilled", Found(src + "r(a=1, b='b')\n"));
        Assert.Equal("bytes", Last(src + "r(1, 'b')\n"));
    }

    [Fact]
    public void ParametersNothingSolvesTakeAnyArguments()
    {
        string src = Head + "from mystery import unknown_thing\n\n\ndef anything(f: Callable[..., R]) -> Callable[..., R]: ...\n\n\ndef a(x: int) -> int: ...\n\n\ndef b(y: str) -> int: ...\n\n\n";
        Assert.Equal("", Found(src + "deco(unknown_thing)(1, 2)\ndeco(anything(a))(1, 2, 3)\n"));
        Assert.Equal("(...) -> int", Last(src + "deco(anything(a))\n"));
        // one P given two different parameter lists, or a value that may be either
        string both = src + "def both(f: Callable[P, R], g: Callable[P, R]) -> Callable[P, R]: ...\n\n\n";
        Assert.Equal("", Found(both + "both(a, b)()\n"));
        Assert.Equal("Parameter 'x' unfilled", Found(both + "both(a, a)()\n"));
        Assert.Equal("", Found(src + "def pick(flag: bool):\n    deco(a if flag else b)()\n"));
    }

    [Fact]
    public void CodeWithoutParamSpecIsUnchanged()
    {
        string src = "from typing import Callable, TypeVar\n\nR = TypeVar('R')\nF = TypeVar('F')\n\n\ndef loose(f: Callable[..., R]) -> Callable[..., R]: ...\n\n\ndef same(f: F) -> F: ...\n\n\n@loose\ndef g(x: int) -> int: ...\n\n\n@same\ndef h(x: int) -> int: ...\n\n\nclass C:\n    cb: Callable[[int], int]\n\n    @loose\n    def m(self, n: int) -> int: ...\n\n\n";
        Assert.Equal("", Found(src + "g()\nC().cb(1)\nC().m()\n"));
        Assert.Equal("Parameter 'x' unfilled", Found(src + "h()\n"));
        Assert.Equal("(...) -> int", Last(src + "g\n"));
    }

    [Fact]
    public void ContextManagerKeepsTheParameters()
    {
        if (!HasStdlib()) return;
        string src = "import contextlib\nfrom typing import Generator, Iterator\n\n\n@contextlib.contextmanager\ndef cm(x: int) -> Iterator[int]:\n    yield x\n\n\n";
        Assert.Equal("Parameter 'x' unfilled", Found(src + "cm()\n"));
        Assert.Equal("", Found(src + "with cm(1) as v:\n    v\n"));
        Assert.Equal("int", Last(src + "with cm(1) as v:\n    v\n"));
        Assert.Equal("Parameter 'y' unfilled", Found(src + "@contextlib.contextmanager\ndef gen(y: str) -> Generator[str, None, None]:\n    yield y\n\n\ngen()\n"));
        // a method: the receiver is bound as it is for the def
        string cls = "import contextlib\nfrom typing import Iterator\n\n\nclass C:\n    @contextlib.contextmanager\n    def m(self, n: int) -> Iterator[str]:\n        yield 'a'\n\n\n";
        Assert.Equal("Parameter 'n' unfilled", Found(cls + "C().m()\n"));
        Assert.Equal("str", Last(cls + "with C().m(1) as w:\n    w\n"));
    }

    [Fact]
    public void AsyncContextManagerKeepsTheParameters()
    {
        if (!HasStdlib()) return;
        string src = "import contextlib\nfrom typing import AsyncIterator\n\n\n@contextlib.asynccontextmanager\nasync def acm(n: int) -> AsyncIterator[str]:\n    yield 'a'\n\n\n";
        Assert.Equal("Parameter 'n' unfilled", Found(src + "acm()\n"));
        Assert.Equal("", Found(src + "acm(1)\n"));
    }

    [Fact]
    public void WrapsKeepsTheWrappersOwnParameters()
    {
        if (!HasStdlib()) return;
        // typeshed's `_Wrapped.__call__` takes the parameters of the function written below `@wraps(...)`
        string src = "import functools\n\n\ndef target(x: int, y: str) -> float: ...\n\n\n@functools.wraps(target)\ndef w(z: int) -> int: ...\n\n\n";
        Assert.Equal("Parameter 'z' unfilled", Found(src + "w()\n"));
        Assert.Equal("", Found(src + "w(1)\n"));
        // a decorator typed with P that returns such a wrapper: the decorated function's parameters
        string logged = "import functools\nfrom typing import Callable, ParamSpec, TypeVar\n\nP = ParamSpec('P')\nR = TypeVar('R')\n\n\ndef logged(f: Callable[P, R]) -> Callable[P, R]:\n    @functools.wraps(f)\n    def inner(*args: P.args, **kwargs: P.kwargs) -> R:\n        return f(*args, **kwargs)\n    return inner\n\n\n@logged\ndef lg(q: int) -> str: ...\n\n\n";
        Assert.Equal("Parameter 'q' unfilled", Found(logged + "lg()\n"));
        Assert.Equal("str", Last(logged + "lg(1)\n"));
    }

    [Fact]
    public void HoverShowsTheParametersADecoratorKept()
    {
        string src = Head + "class C:\n    @deco\n    def plain(self, q: int) -> int: ...\n\n\n@deco\ndef g(x: int, y: str = 'a') -> int: ...\n\n\ng(1)\nC().plain(1)\n";
        Assert.Contains("def g(x: int, y: str = ...) -> int", HoverAt(src, "g(1)"));
        Assert.Contains("def plain(q: int) -> int", HoverAt(src, "plain(1)"));
        if (!HasStdlib()) return;
        Assert.Contains("def cm(x: int) -> _GeneratorContextManager[int]", HoverAt("import contextlib\nfrom typing import Iterator\n\n\n@contextlib.contextmanager\ndef cm(x: int) -> Iterator[int]:\n    yield x\n\n\ncm(1)\n", "cm(1)"));
    }
}
