using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A declared name assigned a container of unknown items takes the container's class, with the items the declaration gives.</summary>
public class DeclaredNarrowingDynamicTests
{
    const string Classes =
        "from typing import Any, Generic, TypeVar\n\nT = TypeVar(\"T\")\nK = TypeVar(\"K\")\nV = TypeVar(\"V\")\n\n\n" +
        "class Seq(Generic[T]):\n    def get(self) -> T: ...\n\n\n" +
        "class MyList(Seq[T]):\n    def __init__(self, *items: T) -> None: ...\n    def push(self, x: T) -> None: ...\n\n\n" +
        "class Other(Generic[T]):\n    def __init__(self, x: T) -> None: ...\n\n\n" +
        "class Base(Generic[K, V]):\n    pass\n\n\n" +
        "class Pair(Base[K, V]):\n    def __init__(self, k: K, v: V) -> None: ...\n    def swap(self) -> None: ...\n\n\n";

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AParameterReassignedWithUnknownItemsTakesTheAssignedClass()
    {
        // found in the library scan: jinja2's `args: Sequence[Any]`, then `args = [value, *args]` and `args.insert(0, ...)`
        var found = Found(Classes + "def f(xs: Seq[Any], v):\n    xs = MyList(v, *xs)\n    xs.push(1)\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("MyList[Any]", Last(Classes + "def f(xs: Seq[Any], v):\n    xs = MyList(v, *xs)\n    xs\n"));
    }

    [Fact]
    public void TheUnknownItemsComeFromTheDeclaration()
    {
        Assert.Equal("MyList[str]", Last(Classes + "def g(v):\n    x: Seq[str] = Seq()\n    x = MyList(v)\n    x\n"));
        Assert.Equal("str", Last(Classes + "def g(v):\n    x: Seq[str] = Seq()\n    x = MyList(v)\n    x.get()\n"));
        var found = Found(Classes + "def g(v):\n    x: Seq[str] = Seq()\n    x = MyList(v)\n    x.push(\"a\")\n    x.get().upper()\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void KnownArgumentsAreKeptAndOnlyTheUnknownOnesFilled()
    {
        Assert.Equal("Pair[str, int]", Last(Classes + "def i(v):\n    p: Base[str, int] = Base()\n    p = Pair(\"a\", v)\n    p\n"));
        var found = Found(Classes + "def i(v):\n    p: Base[str, int] = Base()\n    p = Pair(\"a\", v)\n    p.swap()\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AnArgumentTheDeclarationDoesNotGiveStaysUnknown()
    {
        Assert.Equal("MyList[Unknown]", Last(Classes + "def j(v):\n    o: object = 1\n    o = MyList(v)\n    o\n"));
    }

    [Fact]
    public void AValueThatDoesNotFitKeepsTheDeclaredType()
    {
        Assert.Equal("Seq[str]", Last(Classes + "def h(v):\n    x: Seq[str] = Seq()\n    x = Other(v)\n    x\n"));
        Assert.Equal(1, Found(Classes + "def h(v):\n    x: Seq[str] = Seq()\n    x = Other(v)\n    x.push(1)\n").Count);
    }

    [Fact]
    public void AValueNothingIsKnownAboutStillGivesNoReports()
    {
        var found = Found("class A:\n    def a(self): ...\n\n\nclass B(A):\n    def b(self): ...\n\n\ndef f(v):\n    q: A = B()\n    q = v.make()\n    q.b()\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AListOfUnknownItemsAssignedToADeclaredSequence()
    {
        // the jinja2 case itself, with the real list and Sequence (bundled stubs or typeshed)
        var found = Found("from typing import Any, Sequence\n\n\ndef f(args: Sequence[Any], value):\n    args = [value, *args]\n    args.insert(0, 1)\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }
}
