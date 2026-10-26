using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A subclass of tuple[int, str, ...] indexes and unpacks by position (typeshed: `class CodecInfo(tuple[Any, Any, Any, Any])`).</summary>
public class TupleSubclassItemsTests
{
    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    const string AB = "class A:\n    def a(self) -> None: ...\n\n\nclass B:\n    def b(self) -> None: ...\n\n\nclass T(tuple[A, B]):\n    pass\n\n\n";

    const string T = "class T(tuple[int, str, bytes]):\n    pass\n\n\n";

    [Fact]
    public void IndexingASubclassGivesTheItemAtThatPosition()
    {
        Assert.Equal("str", Last(T + "def f(t: T):\n    t[1]"));
        Assert.Equal("bytes", Last(T + "def f(t: T):\n    t[-1]"));
    }

    [Fact]
    public void ASubclassOfTheSubclassKeepsTheItems()
    {
        Assert.Equal("int", Last(T + "class U(T):\n    pass\n\n\ndef f(u: U):\n    u[0]"));
    }

    [Fact]
    public void UnpackingASubclassGivesEachItem()
    {
        Assert.Equal("str", Last(T + "def f(t: T):\n    a, b, c = t\n    b"));
    }

    [Fact]
    public void IteratingASubclassGivesTheUnionOfItems()
    {
        Assert.Equal("int | str | bytes", Last(T + "def f(t: T):\n    for x in t:\n        x"));
    }

    [Fact]
    public void AMethodOfTheItemIsNotReportedMissing()
    {
        Assert.Equal(0, Found(AB + "def f(t: T):\n    t[0].a()\n    t[1].b()\n").Count);
    }

    [Fact]
    public void AMethodTheItemLacksIsStillReported()
    {
        Assert.Equal(1, Found(AB + "def f(t: T):\n    t[0].b()\n").Count);
    }

    [Fact]
    public void ASubclassWithItsOwnGetitemIsAskedThroughIt()
    {
        Assert.Equal("float", Last("class T(tuple[int, str]):\n    def __getitem__(self, i) -> float: ...\n\n\ndef f(t: T):\n    t[0]"));
    }

    [Fact]
    public void NamedTuplesAndPlainTuplesAreUnchanged()
    {
        Assert.Equal("str", Last("from typing import NamedTuple\n\n\nclass P(NamedTuple):\n    x: int\n    y: str\n\n\ndef f(p: P):\n    p[1]"));
        Assert.Equal("str", Last("def f(t: tuple[int, str]):\n    t[1]"));
        Assert.Equal("int", Last("def f(t: tuple[int, str]):\n    a, b = t\n    a"));
    }
}
