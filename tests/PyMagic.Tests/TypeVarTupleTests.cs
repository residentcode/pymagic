using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>TypeVarTuple (PEP 646): `*args: *Ts`, `tuple[*Ts]` and `Callable[[*Ts], R]` take the types a call gives them, so callbacks such as loop.call_soon's are checked.</summary>
public class TypeVarTupleTests
{
    const string Head = "from typing import Callable, Generic, TypeVar, TypeVarTuple, Unpack\n\nTs = TypeVarTuple(\"Ts\")\nT = TypeVar(\"T\")\n\n\n";

    const string Call = Head + "def call(cb: Callable[[Unpack[Ts]], object], *args: Unpack[Ts]) -> None: ...\n\n\ndef work(a: int, b: str = '') -> None: ...\n\n\n";

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

    /// <summary>The stub of <paramref name="module"/> types its callbacks with a TypeVarTuple (typeshed's does, the bundled stubs may not have it).</summary>
    static bool StubUsesTypeVarTuple(string module)
    {
        var m = new Workspace().GetModule(module);
        return m != null && m.Source.Contains("Unpack[_Ts]");
    }

    [Fact]
    public void TypeVarTupleIsAVariadicVariable()
    {
        Assert.Equal("Ts", Last(Head + "Ts\n"));
        Assert.Equal("tuple[*Ts]", Last(Head + "def f(*args: *Ts):\n    args\n"));
        Assert.Equal("tuple[*Ts]", Last(Head + "def f(*args: Unpack[Ts]):\n    args\n"));
        Assert.Equal("Ts", Last("import typing_extensions\n\nTs = typing_extensions.TypeVarTuple('Ts')\nTs\n"));
        // a TypeVarTuple is not a TypeVar of the same name
        Assert.True(!new TypeVarTupleType("Ts").Equals(new TypeVarType("Ts")));
    }

    [Fact]
    public void StarredAndUnpackedItemsAreReadInTuples()
    {
        Assert.Equal("tuple[int, *Ts]", Last(Head + "def f(x: tuple[int, *Ts]):\n    x\n"));
        Assert.Equal("tuple[*Ts, str]", Last(Head + "def f(x: tuple[Unpack[Ts], str]):\n    x\n"));
        // a tuple with a Ts among its items has a length that is not known: indexing it gives no wrong item
        Assert.Equal("Unknown", Last(Head + "def f(x: tuple[int, *Ts]):\n    x[1]\n"));
        Assert.Equal("", Found(Head + "def f(x: tuple[int, *Ts]):\n    a, b, c = x\n"));
    }

    [Fact]
    public void StarredAndUnpackedItemsAreReadInCallableParameters()
    {
        Assert.Equal("(int, *Ts) -> str", Last(Head + "def f(x: Callable[[int, *Ts], str]):\n    x\n"));
        Assert.Equal("(*Ts) -> str", Last(Head + "def f(x: Callable[[Unpack[Ts]], str]):\n    x\n"));
        // parameters after the Ts are left for later: it takes any arguments
        Assert.Equal("", Found(Head + "def f(x: Callable[[*Ts, int], str]):\n    x()\n    x(1, 2, 3)\n"));
    }

    [Fact]
    public void ArgsSolveTheTypesInOrder()
    {
        string src = Head + "def f(*args: *Ts) -> tuple[*Ts]: ...\n\n\ndef g(*args: Unpack[Ts]) -> tuple[int, Unpack[Ts]]: ...\n\n\n";
        Assert.Equal("tuple[int, str]", Last(src + "f(1, 'a')\n"));
        Assert.Equal("tuple[()]", Last(src + "f()\n"));
        Assert.Equal("tuple[int, bytes, float]", Last(src + "g(b'', 1.0)\n"));
        // the leading parameters take their arguments first
        Assert.Equal("tuple[str]", Last(Head + "def h(first: int, *rest: *Ts) -> tuple[*Ts]: ...\n\n\nh(1, 'a')\n"));
    }

    [Fact]
    public void ATupleArgumentSolvesTheItemsBetweenItsEnds()
    {
        string src = Head + "def pre(t: tuple[int, *Ts, str]) -> tuple[*Ts]: ...\n\n\ndef ident(t: tuple[*Ts]) -> tuple[*Ts]: ...\n\n\n";
        Assert.Equal("tuple[float, bytes]", Last(src + "pre((1, 2.0, b'', 's'))\n"));
        Assert.Equal("tuple[()]", Last(src + "pre((1, 's'))\n"));
        Assert.Equal("tuple[int, str]", Last(src + "ident((1, 'a'))\n"));
        // a prefix TypeVar is solved from its item
        Assert.Equal("bytes", Last(Head + "def first(t: tuple[T, *Ts]) -> T: ...\n\n\nfirst((b'', 1, 2))\n"));
    }

    [Fact]
    public void ACallableArgumentSolvesItsPositionalParameters()
    {
        string src = Head + "def wrap(cb: Callable[[*Ts], T]) -> Callable[[*Ts], T]: ...\n\n\ndef lead(cb: Callable[[int, *Ts], T]) -> Callable[[*Ts], T]: ...\n\n\ndef work(a: int, b: str) -> bytes: ...\n\n\n";
        Assert.Equal("(int, str) -> bytes", Last(src + "wrap(work)\n"));
        Assert.Equal("bytes", Last(src + "wrap(work)(1, 'a')\n"));
        Assert.Equal("Parameter '_1' unfilled", Found(src + "wrap(work)(1)\n"));
        Assert.Equal("Expected type 'str', got 'int' instead", Found(src + "wrap(work)(1, 2)\n"));
        Assert.Equal("(str) -> bytes", Last(src + "lead(work)\n"));
        Assert.Equal("(int, str) -> bytes", Last(src + "def use(c: Callable[[int, str], bytes]):\n    wrap(c)\n"));
    }

    [Fact]
    public void GenericClassesOverATypeVarTuple()
    {
        string src = Head + "class Array(Generic[*Ts]):\n    def get(self) -> tuple[*Ts]: ...\n\n\n"
            + "class Arr2(Generic[T, Unpack[Ts]]):\n    def __init__(self, first: T, *rest: *Ts) -> None: ...\n    def items(self) -> tuple[T, *Ts]: ...\n    def call(self, *a: *Ts) -> None: ...\n\n\n";
        Assert.Equal("tuple[int, str]", Last(src + "Array[int, str]().get()\n"));
        Assert.Equal("type[Array[int, str]]", Last(src + "Array[int, str]\n"));
        Assert.Equal("Arr2[int, str, float]", Last(src + "Arr2(1, 'a', 2.0)\n"));
        Assert.Equal("tuple[int, str, float]", Last(src + "Arr2(1, 'a', 2.0).items()\n"));
        Assert.Equal("tuple[int, bytes]", Last(src + "def f(a: Arr2[int, bytes]):\n    a.items()\n"));
        Assert.Equal("", Found(src + "Arr2(1, 'a', 2.0).call('x', 1.0)\n"));
        Assert.Equal("Parameter '_1' unfilled", Found(src + "Arr2(1, 'a', 2.0).call('x')\n"));
        Assert.Equal("Expected type 'float', got 'str' instead", Found(src + "Arr2(1, 'a', 2.0).call('x', 'y')\n"));
        // an Array whose types are not known takes anything
        Assert.Equal("", Found(src + "def f(a: Arr2, b: Arr2[int]):\n    a.call(1, 2, 3)\n    b.call()\n"));
        Assert.Equal("Unexpected argument", Found(src + "def f(b: Arr2[int]):\n    b.call(1)\n"));
    }

    [Fact]
    public void Pep695TypeVarTuples()
    {
        Assert.Equal("tuple[int, bytes]", Last("class P[*Us]:\n    def m(self) -> tuple[*Us]: ...\n\n\nP[int, bytes]().m()\n"));
        Assert.Equal("tuple[int, str]", Last("def f[*Us](*args: *Us) -> tuple[*Us]: ...\n\n\nf(1, 'a')\n"));
        Assert.Equal("Parameter 'a' unfilled", Found("from typing import Callable\n\n\ndef call[*Us](cb: Callable[[*Us], object], *args: *Us) -> None: ...\n\n\ndef work(a: int) -> None: ...\n\n\ncall(work)\n"));
    }

    [Fact]
    public void ACallbackMustTakeTheArgumentsGivenForIt()
    {
        Assert.Equal("", Found(Call + "call(work, 1)\ncall(work, 1, 's')\ncall(lambda: None)\ncall(lambda x, y: None, 1, 2)\n"));
        Assert.Equal("Parameter 'a' unfilled", Found(Call + "call(work)\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(Call + "call(work, 's')\n"));
        Assert.Equal("Unexpected argument", Found(Call + "call(work, 1, 's', 3)\n"));
        // a bound method, a Callable value
        Assert.Equal("Parameter 'n' unfilled", Found(Call + "class C:\n    def m(self, n: int) -> None: ...\n\n\ncall(C().m)\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(Call + "def use(c: Callable[[int], None]):\n    call(c, 'x')\n"));
        // the items of a tuple argument
        string take = Call + "def take(cb: Callable[[*Ts], object], args: tuple[*Ts]) -> None: ...\n\n\n";
        Assert.Equal("", Found(take + "take(work, (1,))\ntake(work, (1, 'b'))\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(take + "take(work, ('s',))\n"));
        Assert.Equal("Parameter 'a' unfilled", Found(take + "take(work, ())\n"));
    }

    [Fact]
    public void LeadingParametersOfTheCallbackAreFilledFirst()
    {
        string src = Head + "def run(cb: Callable[[int, *Ts], object], *args: *Ts) -> None: ...\n\n\ndef job(n: int, s: str) -> None: ...\n\n\n";
        Assert.Equal("", Found(src + "run(job, 'a')\n"));
        Assert.Equal("Parameter 's' unfilled", Found(src + "run(job)\n"));
        Assert.Equal("Expected type 'str', got 'int' instead", Found(src + "run(job, 1)\n"));
    }

    [Fact]
    public void AsyncioCallbacksAreChecked()
    {
        if (!StubUsesTypeVarTuple("asyncio.events")) return;
        string src = "import asyncio\n\n\ndef work(a: int, b: str = '') -> None: ...\n\n\nloop = asyncio.get_event_loop()\n";
        Assert.Equal("", Found(src + "loop.call_soon(work, 1)\nloop.call_later(1.0, work, 1, 'b')\nloop.call_at(2.0, work, 1)\nloop.call_soon_threadsafe(work, 1)\nloop.run_in_executor(None, work, 1)\n"));
        Assert.Equal("Parameter 'a' unfilled", Found(src + "loop.call_soon(work)\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(src + "loop.call_soon(work, 's')\n"));
        Assert.Equal("Parameter 'a' unfilled; Parameter 'a' unfilled; Parameter 'a' unfilled; Parameter 'a' unfilled",
            Found(src + "loop.call_later(1.0, work)\nloop.call_at(1.0, work)\nloop.call_soon_threadsafe(work)\nloop.run_in_executor(None, work)\n"));
        Assert.Equal("Unexpected argument", Found(src + "loop.call_soon(work, 1, 'b', 3)\n"));
    }

    [Fact]
    public void TkinterAndThreadCallbacksAreChecked()
    {
        if (StubUsesTypeVarTuple("tkinter"))
        {
            string tk = "import tkinter\n\n\ndef work(a: int) -> None: ...\n\n\nroot = tkinter.Tk()\n";
            Assert.Equal("", Found(tk + "root.after(10, work, 1)\nroot.after_idle(work, 2)\n"));
            Assert.Equal("Parameter 'a' unfilled", Found(tk + "root.after(10, work)\n"));
        }
        if (StubUsesTypeVarTuple("_thread"))
        {
            string th = "import _thread\n\n\ndef work(a: int) -> None: ...\n\n\n";
            Assert.Equal("", Found(th + "_thread.start_new_thread(work, (1,))\n"));
        }
    }

    [Fact]
    public void ContextRunIsLeftAsItWas()
    {
        // contextvars.Context.run is typed with a ParamSpec, not a TypeVarTuple: what `*args: P.args` takes is not checked yet
        string src = "import contextvars\n\n\ndef work(a: int) -> None: ...\n\n\nctx = contextvars.copy_context()\n";
        Assert.Equal("", Found(src + "ctx.run(work, 1)\nctx.run(work)\n"));
    }

    [Fact]
    public void UnsolvedOrAmbiguousTypesTakeAnything()
    {
        string src = Call + "from mystery import unknown_thing\n\n\ndef anything(f: Callable[..., T]) -> Callable[..., T]: ...\n\n\n";
        // a *xs of unknown length, an Unknown callback or argument, a Callable[..., R]
        Assert.Equal("", Found(src + "def g(xs: list[int]):\n    call(work, *xs)\n    call(unknown_thing, 1, 2, 3)\n    call(work, unknown_thing)\n    call(anything(work))\n    call(anything(work), 1, 2)\n"));
        Assert.Equal("tuple[Unknown, ...]", Last(Head + "def f(*args: *Ts) -> tuple[*Ts]: ...\n\n\ndef g(xs: list[int]):\n    f(*xs)\n"));
        // two different solutions
        string two = Head + "def two(a: tuple[*Ts], b: tuple[*Ts]) -> tuple[*Ts]: ...\n\n\n";
        Assert.Equal("tuple[Unknown, ...]", Last(two + "two((1,), ('s', 2))\n"));
        Assert.Equal("tuple[int]", Last(two + "two((1,), (2,))\n"));
        string cbs = Head + "def both(f: Callable[[*Ts], object], g: Callable[[*Ts], object]) -> Callable[[*Ts], None]: ...\n\n\ndef a(x: int) -> None: ...\n\n\ndef b(y: str, z: str) -> None: ...\n\n\n";
        Assert.Equal("", Found(cbs + "both(a, b)()\nboth(a, b)(1, 2, 3)\n"));
        // a parameter with a default or a *args: how many the callable is given is not fixed
        string wrap = Head + "def wrap(cb: Callable[[*Ts], T]) -> Callable[[*Ts], T]: ...\n\n\ndef opt(a: int, b: str = '') -> bytes: ...\n\n\ndef star(*a: int) -> bytes: ...\n\n\n";
        Assert.Equal("", Found(wrap + "wrap(opt)(1)\nwrap(opt)(1, 'b')\nwrap(star)()\nwrap(star)(1, 2)\n"));
        Assert.Equal("bytes", Last(wrap + "wrap(opt)(1)\n"));
    }

    [Fact]
    public void CodeWithoutTypeVarTupleIsUnchanged()
    {
        string src = "from typing import Callable, Generic, TypeVar\n\nT = TypeVar('T')\n\n\nclass Box(Generic[T]):\n    def __init__(self, v: T) -> None: ...\n    def get(self) -> T: ...\n\n\n"
            + "def f(*args: int, cb: Callable[[int, str], bool]) -> tuple[int, str]:\n    args\n\n\n";
        Assert.Equal("tuple[int, ...]", Last(src + "def g(*args: int):\n    args\n"));
        Assert.Equal("tuple[int, str]", Last(src + "f(1, 2, cb=lambda a, b: True)\n"));
        Assert.Equal("Box[int]", Last(src + "Box(1)\n"));
        Assert.Equal("(int, str) -> bool", Last(src + "def h(c: Callable[[int, str], bool]):\n    c\n"));
        Assert.Equal("Expected type 'int', got 'str' instead", Found(src + "f('a', cb=lambda a, b: True)\n"));
    }

    [Fact]
    public void KwargsUnpackOfATypedDictStillWorks()
    {
        string src = "from typing import TypedDict, Unpack\n\n\nclass Options(TypedDict):\n    name: str\n    size: int\n\n\ndef make(**kwargs: Unpack[Options]) -> None: ...\n\n\n";
        Assert.Equal("", Found(src + "make(name='a', size=1)\n"));
        Assert.Equal("Parameter 'size' unfilled", Found(src + "make(name='a')\n"));
        Assert.Equal("Unexpected argument 'other'", Found(src + "make(name='a', size=1, other=2)\n"));
    }
}
