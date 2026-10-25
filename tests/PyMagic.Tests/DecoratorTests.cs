using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A decorated function is what its decorators make of it, as far as they say what they return.</summary>
public class DecoratorTests
{
    const string Head = "from typing import Callable, Iterator, TypeVar, Any\n\nR = TypeVar('R')\nF = TypeVar('F')\n\n\n";

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void ADecoratorThatChangesTheReturnType()
    {
        Assert.Equal("list[int]", Last(Head + "def deco(f: Callable[..., R]) -> Callable[..., list[R]]: ...\n\n\n@deco\ndef g() -> int: ...\n\n\ng()"));
    }

    [Fact]
    public void ADecoratorThatReturnsWhatItGets()
    {
        Assert.Equal("int", Last(Head + "def deco(f: F) -> F: ...\n\n\n@deco\ndef g(x: int) -> int: ...\n\n\ng(1)"));
    }

    [Fact]
    public void ADecoratorWithoutAnnotationsLeavesTheFunctionAlone()
    {
        Assert.Equal("int", Last(Head + "def deco(f):\n    def w(*a, **k):\n        return f(*a, **k)\n    return w\n\n\n@deco\ndef g() -> int: ...\n\n\ng()"));
    }

    [Fact]
    public void ADecoratorReturningAnUnknownLeavesTheFunctionAlone()
    {
        Assert.Equal("int", Last(Head + "from mystery import wrap\n\n\ndef deco(f: Callable[..., R]) -> Any: ...\n\n\n@deco\ndef g() -> int: ...\n\n\ng()"));
    }

    [Fact]
    public void ADecoratorWithArguments()
    {
        Assert.Equal("list[str]", Last(Head + "def times(n: int) -> Callable[[Callable[..., R]], Callable[..., list[R]]]: ...\n\n\n@times(3)\ndef g() -> str: ...\n\n\ng()"));
    }

    [Fact]
    public void StackedDecoratorsApplyNearestFirst()
    {
        string src = Head + "def box(f: Callable[..., R]) -> Callable[..., list[R]]: ...\n\n\ndef pair(f: Callable[..., R]) -> Callable[..., tuple[R, R]]: ...\n\n\n@box\n@pair\ndef g() -> int: ...\n\n\ng()";
        Assert.Equal("list[tuple[int, int]]", Last(src));
    }

    [Fact]
    public void AClassUsedAsADecorator()
    {
        Assert.Equal("Wrap", Last(Head + "class Wrap:\n    def __init__(self, f: Callable[..., Any]) -> None: ...\n\n\n@Wrap\ndef g() -> int: ...\n\n\ng"));
    }

    [Fact]
    public void MethodsAndTheBuiltInDecoratorsStillWork()
    {
        Assert.Equal("int", Last(Head + "class C:\n    @staticmethod\n    def s() -> int: ...\n\n    @property\n    def p(self) -> int: ...\n\n\nC.s()"));
        Assert.Equal("int", Last(Head + "class C:\n    @property\n    def p(self) -> int: ...\n\n\nC().p"));
    }

    // typeshed's contextlib and functools, where they are available

    static bool HasStdlib() => new Workspace().GetModule("contextlib") != null && new Workspace().GetModule("functools") != null;

    [Fact]
    public void ContextManagerGivesTheYieldedType()
    {
        if (!HasStdlib()) return;
        Assert.Equal("int", Last("import contextlib\nfrom typing import Iterator\n\n\n@contextlib.contextmanager\ndef cm() -> Iterator[int]:\n    yield 1\n\n\nwith cm() as v:\n    v"));
        Assert.Equal("str", Last("import contextlib\nfrom typing import Iterator\n\n\nclass C:\n    @contextlib.contextmanager\n    def m(self, n: int) -> Iterator[str]:\n        yield 'a'\n\n\nwith C().m(1) as w:\n    w"));
    }

    [Fact]
    public void LruCacheKeepsTheReturnType()
    {
        if (!HasStdlib()) return;
        Assert.Equal("int", Last("import functools\n\n\n@functools.lru_cache(maxsize=None)\ndef g(x: int) -> int: ...\n\n\ng(1)"));
        Assert.Equal("int", Last("import functools\n\n\n@functools.lru_cache\ndef g(x: int) -> int: ...\n\n\ng(1)"));
        Assert.Equal("int", Last("import functools\n\n\n@functools.cache\ndef g(x: int) -> int: ...\n\n\ng(1)"));
    }
}
