using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>len(x) &lt; n, &lt;=, &gt; and &gt;=, either way round.</summary>
public class LengthComparisonNarrowingTests
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

    const string Shapes = "def f(x: tuple[int] | tuple[int, str] | tuple[int, str, bool]):\n";
    const string Homogeneous = "def f(x: tuple[int, ...]):\n";

    [Fact]
    public void LenLessThanKeepsTheShorterTuples()
    {
        Same("tuple[int] | tuple[int, str]", T(Shapes + "    if len(x) < 3:\n        x\n"));
    }

    [Fact]
    public void TheOtherBranchOfLenLessThanKeepsTheLongerOnes()
    {
        Same("tuple[int, str, bool]", T(Shapes + "    if len(x) < 3:\n        pass\n    else:\n        x\n"));
    }

    [Fact]
    public void LenAtMostKeepsTheTuplesUpToThatLength()
    {
        Same("tuple[int] | tuple[int, str]", T(Shapes + "    if len(x) <= 2:\n        x\n"));
    }

    [Fact]
    public void LenGreaterThanKeepsTheLongerTuples()
    {
        Same("tuple[int, str] | tuple[int, str, bool]", T(Shapes + "    if len(x) > 1:\n        x\n"));
    }

    [Fact]
    public void LenAtLeastKeepsTheTuplesFromThatLength()
    {
        Same("tuple[int, str, bool]", T(Shapes + "    if len(x) >= 3:\n        x\n"));
    }

    [Fact]
    public void TheLengthCanBeOnTheRight()
    {
        Same("tuple[int]", T(Shapes + "    if 2 > len(x):\n        x\n"));
    }

    [Fact]
    public void TheLengthOnTheRightNarrowsTheOtherBranchToo()
    {
        Same("tuple[int] | tuple[int, str]", T(Shapes + "    if 3 <= len(x):\n        pass\n    else:\n        x\n"));
    }

    [Fact]
    public void NotLenAtMostKeepsTheLongerTuples()
    {
        Same("tuple[int, str] | tuple[int, str, bool]", T(Shapes + "    if not len(x) <= 1:\n        x\n"));
    }

    [Fact]
    public void AnEarlyReturnOnLenAtLeastNarrowsTheRest()
    {
        Same("tuple[int]", T(Shapes + "    if len(x) >= 2:\n        return\n    x\n"));
    }

    [Fact]
    public void TwoLengthComparisonsJoinedByAndNarrowBoth()
    {
        Same("tuple[int, str]", T(Shapes + "    if len(x) > 1 and len(x) < 3:\n        x\n"));
    }

    [Fact]
    public void TwoLengthComparisonsJoinedByOrKeepEitherSide()
    {
        Same("tuple[int] | tuple[int, str, bool]", T(Shapes + "    if len(x) < 2 or len(x) > 2:\n        x\n"));
    }

    [Fact]
    public void AWhileLoopOnTheLengthNarrowsItsBodyAndWhatFollows()
    {
        Same("tuple[int, str] | tuple[int, str, bool]", T(Shapes + "    while len(x) > 1:\n        x\n"));
        Same("tuple[int]", T(Shapes + "    while len(x) > 1:\n        pass\n    x\n"));
    }

    [Fact]
    public void AHomogeneousTupleBecomesTheShortFixedTuples()
    {
        Same("tuple[()] | tuple[int] | tuple[int, int]", T(Homogeneous + "    if len(x) < 3:\n        x\n"));
    }

    [Fact]
    public void AHomogeneousTupleHasAtLeastSoManyItemsWhenTheLengthHasNoUpperBound()
    {
        Same("tuple[int, int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(x) > 2:\n        x\n"));
        Same("tuple[int, int, int, *tuple[int, ...]]", T(Homogeneous + "    if len(x) < 3:\n        pass\n    else:\n        x\n"));
        Same("tuple[int, ...]", T(Homogeneous + "    if len(x) > 9:\n        x\n"));
    }

    [Fact]
    public void AHomogeneousTupleStaysWhenTooManyLengthsFit()
    {
        Same("tuple[int, ...]", T(Homogeneous + "    if len(x) <= 9:\n        x\n"));
    }

    [Fact]
    public void ANegativeLengthLeavesNothing()
    {
        Same("Unknown", T(Shapes + "    if len(x) < 0:\n        x\n"));
        Same("Unknown", T(Homogeneous + "    if len(x) < 0:\n        x\n"));
    }

    [Fact]
    public void AnAttributeIsNarrowedToo()
    {
        Same("tuple[()] | tuple[int]", T("class K:\n    p: tuple[int, ...]\ndef f(o: K):\n    if len(o.p) < 2:\n        o.p\n"));
    }

    [Fact]
    public void ListsAndOtherTypesAreLeftAlone()
    {
        Same("list[int]", T("def f(x: list[int]):\n    if len(x) < 2:\n        x\n"));
        Same("tuple[()] | tuple[int] | list[str] | None", T("def f(x: tuple[int, ...] | list[str] | None):\n    if len(x) < 2:\n        x\n"));
    }

    [Fact]
    public void ATupleWithAnUnpackedTypeVarTupleIsLeftAlone()
    {
        Same("tuple[int, *Ts]", T("from typing import TypeVarTuple\nTs = TypeVarTuple(\"Ts\")\ndef f(x: tuple[int, *Ts]):\n    if len(x) < 2:\n        x\n"));
    }

    [Fact]
    public void ALengthThatIsNotAConstantNarrowsNothing()
    {
        Same("tuple[int] | tuple[int, str] | tuple[int, str, bool]", T("def f(x: tuple[int] | tuple[int, str] | tuple[int, str, bool], n: int):\n    if len(x) < n:\n        x\n"));
    }

    [Fact]
    public void TheLengthOfACallNarrowsNothing()
    {
        Same("tuple[int, ...]", T("def g() -> tuple[int, ...]: ...\ndef f(x: tuple[int, ...]):\n    if len(g()) < 2:\n        x\n"));
    }
}
