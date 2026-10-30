using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Literals through tuples: constants in a tuple or list display checked against Literal items, and `x[0] == "a"` narrowing a union of tuples.</summary>
public class LiteralTupleTests
{
    const string Pre = "from typing import Literal\nfrom enum import Enum\n\nclass Color(Enum):\n    RED = 1\n    BLUE = 2\n\n";

    static List<string> Mismatches(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

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

    const string OpenPair = "def open_pair(p: tuple[Literal[\"r\", \"w\"], int]) -> None: ...\n\n\n";
    const string Tagged = "def f(x: tuple[Literal[\"a\"], int] | tuple[Literal[\"b\"], str]):\n";

    [Fact]
    public void AConstantInATupleIsCheckedAsTheLiteralItSpells()
    {
        var found = Mismatches(OpenPair + "open_pair((\"r\", 1))\nopen_pair((\"x\", 1))\n");
        Assert.Equal(1, found.Count);
        Assert.True(found[0].Contains("got 'tuple[Literal['x'], int]'"), found[0]);
    }

    [Fact]
    public void TheTupleIsCheckedItemByItem()
    {
        var found = Mismatches("def pick(p: tuple[Literal[\"r\", \"w\"], str]) -> None: ...\n\n\npick((\"w\", \"a\"))\npick((\"x\", \"a\"))\n");
        Assert.Equal(1, found.Count);
    }

    [Fact]
    public void AHomogeneousTupleOfLiteralsChecksEveryItem()
    {
        var found = Mismatches("def many(ms: tuple[Literal[\"r\", \"w\"], ...]) -> None: ...\n\n\nmany((\"r\", \"w\", \"r\"))\nmany((\"r\", \"z\"))\n");
        Assert.Equal(1, found.Count);
    }

    [Fact]
    public void AListDisplayIsCheckedAgainstAListOfLiterals()
    {
        var found = Mismatches("def modes(ms: list[Literal[\"r\", \"w\"]]) -> None: ...\n\n\nmodes([\"r\", \"w\"])\nmodes([\"r\", \"q\"])\n");
        Assert.Equal(1, found.Count);
        Assert.True(found[0].Contains("Literal['r', 'q']"), found[0]);
    }

    [Fact]
    public void NestedTuplesAreCheckedToo()
    {
        var found = Mismatches("def nested(p: tuple[tuple[Literal[\"a\"], int], str]) -> None: ...\n\n\nnested(((\"a\", 1), \"s\"))\nnested(((\"b\", 1), \"s\"))\n");
        Assert.Equal(1, found.Count);
    }

    [Fact]
    public void ATupleWhereNoLiteralIsWantedIsNotChanged()
    {
        Assert.Equal(0, Mismatches("def plain(p: tuple[str, int]) -> None: ...\n\n\nplain((\"r\", 1))\n").Count);
        Assert.Equal(0, Mismatches(OpenPair + "def g(mode: str):\n    open_pair((mode, 1))\n").Count);
    }

    [Fact]
    public void AnItemEqualToALiteralNarrowsAUnionOfTuples()
    {
        Same("tuple[Literal['a'], int]", T(Tagged + "    if x[0] == \"a\":\n        x\n"));
        Same("tuple[Literal['b'], str]", T(Tagged + "    if x[0] == \"a\":\n        pass\n    else:\n        x\n"));
    }

    [Fact]
    public void NotEqualAndTheLiteralOnTheLeftNarrowToo()
    {
        Same("tuple[Literal['a'], int]", T(Tagged + "    if x[0] != \"b\":\n        x\n"));
        Same("tuple[Literal['b'], str]", T(Tagged + "    if \"b\" == x[-2]:\n        x\n"));
    }

    [Fact]
    public void AnItemThatIsNotALiteralKeepsItsTuple()
    {
        Same("tuple[str, int]", T("def f(x: tuple[str, int] | tuple[Literal[\"b\"], str]):\n    if x[0] == \"a\":\n        x\n"));
    }

    [Fact]
    public void AnItemIsNoneAndAnEnumMemberNarrowToo()
    {
        Same("tuple[None, bytes]", T("def f(y: tuple[int, str] | tuple[None, bytes]):\n    if y[0] is None:\n        y\n"));
        Same("tuple[int, str]", T("def f(y: tuple[int, str] | tuple[None, bytes]):\n    if y[0] is not None:\n        y\n"));
        Same("tuple[Color, int]", T("def f(z: tuple[Color, int] | tuple[Literal[Color.RED], str]):\n    if z[0] is Color.BLUE:\n        z\n"));
    }
}
