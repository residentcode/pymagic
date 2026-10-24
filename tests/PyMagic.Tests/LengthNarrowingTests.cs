using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>len(x) == n, and sequence and mapping patterns of match statements.</summary>
public class LengthNarrowingTests
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

    [Fact]
    public void LenEqualKeepsTheTuplesOfThatLength()
    {
        Same("tuple[int, str]", T(Shapes + "    if len(x) == 2:\n        x\n"));
    }

    [Fact]
    public void LenNotEqualDropsThem()
    {
        Same("tuple[int] | tuple[int, str, bool]", T(Shapes + "    if len(x) != 2:\n        x\n"));
    }

    [Fact]
    public void TheOtherBranchOfLenEqual()
    {
        Same("tuple[int] | tuple[int, str, bool]", T(Shapes + "    if len(x) == 2:\n        pass\n    else:\n        x\n"));
    }

    [Fact]
    public void AnEarlyReturnOnLenNarrowsTheRest()
    {
        Same("tuple[int, str, bool]", T(Shapes + "    if len(x) == 1:\n        return\n    if len(x) == 2:\n        return\n    x\n"));
    }

    [Fact]
    public void AHomogeneousTupleBecomesFixed()
    {
        Same("tuple[int, int]", T("def f(x: tuple[int, ...]):\n    if len(x) == 2:\n        x\n"));
    }

    [Fact]
    public void ListsAreLeftAlone()
    {
        Same("list[int]", T("def f(x: list[int]):\n    if len(x) == 2:\n        x\n"));
    }

    [Fact]
    public void ASequencePatternKeepsTuplesOfTheLength()
    {
        Same("tuple[int, str]", T(Shapes + "    match x:\n        case (a, b):\n            x\n"));
    }

    [Fact]
    public void AStarPatternNeedsAtLeastTheFixedItems()
    {
        Same("tuple[int, str] | tuple[int, str, bool]", T(Shapes + "    match x:\n        case (a, b, *rest):\n            x\n"));
    }

    [Fact]
    public void ASequencePatternDropsWhatIsNotASequence()
    {
        Same("list[int]", T("def f(x: int | str | list[int] | dict[str, int] | None):\n    match x:\n        case [a, b]:\n            x\n"));
    }

    [Fact]
    public void ACapturePatternThatCannotFailLeavesTheTupleOut()
    {
        Same("tuple[int] | tuple[int, str, bool]", T(Shapes + "    match x:\n        case (a, b):\n            pass\n        case _:\n            x\n"));
    }

    [Fact]
    public void APatternWithAValueCanFailSoTheTupleStays()
    {
        Same("tuple[int] | tuple[int, str] | tuple[int, str, bool]", T(Shapes + "    match x:\n        case (1, b):\n            pass\n        case _:\n            x\n"));
    }

    [Fact]
    public void AMappingPatternKeepsMappings()
    {
        Same("dict[str, int]", T("def f(x: int | list[int] | dict[str, int] | None):\n    match x:\n        case {\"a\": v}:\n            x\n"));
    }
}
