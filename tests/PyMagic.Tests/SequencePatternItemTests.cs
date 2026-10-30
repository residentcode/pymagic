using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The sub-patterns of a sequence pattern narrow the items of a matched tuple: class patterns, None, literals, enum members, nested sequences.</summary>
public class SequencePatternItemTests
{
    const string Pre = "from typing import Any\nfrom enum import Enum\n\nclass Color(Enum):\n    RED = 1\n    BLUE = 2\n\nclass Point:\n    __match_args__ = (\"x\", \"y\")\n    def __init__(self, x: int, y: int) -> None:\n        self.x = x\n        self.y = y\n\n";

    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    const string Mixed = "def f(t: tuple[Point | None, int | str]):\n    match t:\n";
    const string Swapped = "def f(u: tuple[int, str] | tuple[str, int]):\n    match u:\n";

    [Fact]
    public void ClassPatternsNarrowTheItemsOfATuple()
    {
        Same("tuple[Point, str]", T(Mixed + "        case (Point(), str()):\n            t\n"));
    }

    [Fact]
    public void ATupleWithAnItemThatCannotMatchIsLeftOut()
    {
        Same("tuple[int, str]", T(Swapped + "        case (int(), _):\n            u\n"));
    }

    [Fact]
    public void TheNextCaseNoLongerGetsATupleThatSurelyMatched()
    {
        Same("tuple[str, int]", T(Swapped + "        case (int(), _):\n            pass\n        case _:\n            u\n"));
    }

    [Fact]
    public void WhenOneItemCanFailTheNextCaseGetsItNarrowedTheOtherWay()
    {
        Same("tuple[str, bytes]", T("def f(v: tuple[int | str, bytes]):\n    match v:\n        case (int(), _):\n            pass\n        case _:\n            v\n"));
    }

    [Fact]
    public void WhenTwoItemsCanFailTheNextCaseGetsTheTupleAsItIs()
    {
        Same("tuple[Point | None, int | str]", T(Mixed + "        case (Point(), str()):\n            pass\n        case (a, b):\n            t\n"));
    }

    [Fact]
    public void AClassPatternWithArgumentsCanFailOnItsOwnClass()
    {
        Same("tuple[Point, int | str]", T("def f(p: tuple[Point, int | str]):\n    match p:\n        case (Point(x=0), _):\n            pass\n        case _:\n            p\n"));
    }

    [Fact]
    public void AHomogeneousTupleBecomesFixedWithNarrowedItems()
    {
        Same("tuple[int, str]", T("def f(h: tuple[int | str, ...]):\n    match h:\n        case (int(), str()):\n            h\n"));
    }

    [Fact]
    public void AStarOverAHomogeneousTupleLeavesItAsItIs()
    {
        Same("tuple[int | str, ...]", T("def f(h: tuple[int | str, ...]):\n    match h:\n        case (int(), *rest):\n            h\n"));
    }

    [Fact]
    public void ItemsAfterAStarCountFromTheEnd()
    {
        Same("tuple[int, str, None]", T("def f(w: tuple[int, str, bytes | None]):\n    match w:\n        case (int(), *_, None):\n            w\n"));
    }

    [Fact]
    public void NoneAndLiteralsNarrowItems()
    {
        Same("tuple[None, str]", T("def f(x: tuple[int | None, str]):\n    match x:\n        case (None, \"a\"):\n            x\n"));
    }

    [Fact]
    public void EnumMembersNarrowItemsBothWays()
    {
        Same("tuple[Color.RED, int]", T("def f(k: tuple[Color, int]):\n    match k:\n        case (Color.RED, _):\n            k\n"));
        Same("tuple[Color.BLUE, int]", T("def f(k: tuple[Color, int]):\n    match k:\n        case (Color.RED, _):\n            pass\n        case _:\n            k\n"));
    }

    [Fact]
    public void ANestedSequencePatternNarrowsItemByItem()
    {
        const string Nested = "def f(n: tuple[tuple[int | str, bytes], int]):\n    match n:\n";
        Same("tuple[tuple[str, bytes], int]", T(Nested + "        case ((str(), _), _):\n            n\n"));
        Same("tuple[tuple[int, bytes], int]", T(Nested + "        case ((str(), _), _):\n            pass\n        case _:\n            n\n"));
    }

    [Fact]
    public void AsAndOrPatternsNarrowItems()
    {
        Assert.Equal("tuple[None | Point, str]", T(Mixed + "        case (None | Point(), str() as s):\n            t\n"));
    }

    [Fact]
    public void AnAnyItemIsNarrowedButNeverMakesTheTupleGo()
    {
        const string Loose = "def f(a: tuple[Any, int]):\n    match a:\n";
        Same("tuple[Point, int]", T(Loose + "        case (Point(), _):\n            a\n"));
        Same("tuple[Any, int]", T(Loose + "        case (Point(), _):\n            pass\n        case _:\n            a\n"));
    }

    [Fact]
    public void TheItemsOfAListAreNotNarrowed()
    {
        Same("list[int | str]", T("def f(l: list[int | str]):\n    match l:\n        case [int(), _]:\n            l\n"));
    }

    [Fact]
    public void ATupleDisplayAsTheSubjectDoesNotNarrowItsNames()
    {
        Same("int | str", T("def f(x: int | str, y: int):\n    match x, y:\n        case (int(), _):\n            x\n"));
    }
}
