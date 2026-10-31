using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The "at least n" form of a tuple, `tuple[int, int, *tuple[int, ...]]`: what `len(t) >= n` makes of a `tuple[int, ...]`, and how it is used.</summary>
public class AtLeastTupleTests
{
    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    const string Homogeneous = "def f(t: tuple[int, ...]):\n";
    const string Mixed = "def f(s: tuple[str, *tuple[int, ...]]):\n";

    [Fact]
    public void LenAtLeastGivesTheAtLeastForm()
    {
        Same("tuple[int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) >= 2:\n        t\n"));
    }

    [Fact]
    public void LenGreaterThanAndTheElseOfLenLessThanGiveItToo()
    {
        Same("tuple[int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) > 1:\n        t\n"));
        Same("tuple[int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) <= 1:\n        return\n    t\n"));
    }

    [Fact]
    public void LenNotZeroGivesAtLeastOneItem()
    {
        Same("tuple[int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) != 0:\n        t\n"));
    }

    [Fact]
    public void IndexingIterationAndUnpackingUseTheItemType()
    {
        Same("int", T(Homogeneous + "    if len(t) >= 2:\n        t[1]\n"));
        Same("int", T(Homogeneous + "    if len(t) >= 2:\n        for x in t:\n            x\n"));
        Same("list[int]", T(Homogeneous + "    if len(t) >= 2:\n        a, b, *c = t\n        c\n"));
        Same("int", T(Homogeneous + "    if len(t) >= 2:\n        len(t)\n"));
    }

    [Fact]
    public void AnotherLengthTestNarrowsTheAtLeastFormFurther()
    {
        Same("tuple[int, int, int]", T(Homogeneous + "    if len(t) >= 2:\n        if len(t) == 3:\n            t\n"));
        Same("tuple[int, int] | tuple[int, int, int]", T(Homogeneous + "    if len(t) >= 2:\n        if len(t) < 4:\n            t\n"));
        Same("tuple[int, int, int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) >= 2:\n        if len(t) > 3:\n            t\n"));
    }

    [Fact]
    public void ALengthBelowTheLeastLeavesNothing()
    {
        Same("Unknown", T(Homogeneous + "    if len(t) >= 3:\n        if len(t) == 1:\n            t\n"));
    }

    [Fact]
    public void TheFormCanBeWrittenInAnAnnotation()
    {
        Same("tuple[str, *tuple[int, ...]]", T(Mixed + "    s\n"));
        Same("str", T(Mixed + "    s[0]\n"));
        Same("tuple[str, int, int, *tuple[int, ...]]", T(Mixed + "    if len(s) >= 3:\n        s\n"));
        Same("tuple[str, int]", T(Mixed + "    if len(s) == 2:\n        s\n"));
    }

    [Fact]
    public void ASequencePatternFixesTheLengthOrKeepsTheForm()
    {
        Same("tuple[str, int]", T(Mixed + "    match s:\n        case (a, b):\n            s\n"));
        Same("str", T(Mixed + "    match s:\n        case (head, *rest):\n            head\n"));
        Same("list[int]", T(Mixed + "    match s:\n        case (head, *rest):\n            rest\n"));
    }

    [Fact]
    public void AnIndexPastTheFixedItemsIsTheStarsItemOrOneOnItsOtherSide()
    {
        Same("int", T(Mixed + "    s[1]\n"));
        Same("int | str", T(Mixed + "    s[-1]\n"));
        Same("bytes | int", T("def f(e: tuple[str, *tuple[int, ...], bytes]):\n    e[1]\n"));
    }

    [Fact]
    public void AStarPatternOfCapturesTakesTheWholeFormOutOfTheFollowingCases()
    {
        Same("Unknown", T(Homogeneous + "    if len(t) >= 2:\n        match t:\n            case (a, b, *c):\n                pass\n            case _:\n                t\n"));
        Same("tuple[int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(t) >= 2:\n        match t:\n            case (a, b, c, *d):\n                pass\n            case _:\n                t\n"));
    }

    [Fact]
    public void TheAtLeastFormGoesWhereAHomogeneousTupleIsWanted()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", "def take(x: tuple[int, ...]) -> None: ...\n\n\ndef f(t: tuple[int, ...]):\n    if len(t) >= 2:\n        take(t)\n");
        Assert.Equal(0, ws.Evaluator.Inspect(m, true).Count());
    }
}
