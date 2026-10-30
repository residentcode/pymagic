using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The small gaps of the type engine: removing a key a TypedDict requires, and a constant index past the end of a tuple of known length.</summary>
public class SmallGapsTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<string> Problems(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).ToList();
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    const string Movie = "from typing import TypedDict, NotRequired\n\n\nclass Movie(TypedDict):\n    name: str\n    year: NotRequired[int]\n\n\nclass Loose(TypedDict, total=False):\n    name: str\n\n\n";

    static int Deleted(List<string> found) => found.Count(m => m.EndsWith("cannot be deleted"));

    // ------------------------------------------------------------ required TypedDict keys

    [Fact]
    public void PopAndDelOfARequiredTypedDictKeyAreReported()
    {
        var found = Problems(Movie + Src("def f(m: Movie):", "    m.pop('name')", "    del m['name']"));
        Assert.Equal(2, Deleted(found));
        Assert.True(found.Contains("Key 'name' of TypedDict 'Movie' cannot be deleted"), string.Join(" / ", found));
    }

    [Fact]
    public void KeysThatAreNotRequiredMayBeRemoved()
    {
        Assert.Equal(0, Deleted(Problems(Movie + Src("def f(m: Movie, l: Loose):", "    m.pop('year')", "    m.pop('year', 0)", "    del m['year']", "    l.pop('name')", "    del l['name']"))));
    }

    [Fact]
    public void OnlyAKeyEveryTypedDictOfAUnionRequiresIsReported()
    {
        Assert.Equal(1, Deleted(Problems(Movie + Src("class Film(TypedDict):", "    name: str", "", "", "def f(m: Movie | Film):", "    m.pop('name')"))));
        Assert.Equal(0, Deleted(Problems(Movie + Src("def f(m: Movie | Loose):", "    m.pop('name')"))));
    }

    [Fact]
    public void PlainDictsAndKeysThatAreNotConstantsAreLeftAlone()
    {
        Assert.Equal(0, Deleted(Problems(Movie + Src("def f(m: Movie, d: dict[str, int], k: str):", "    d.pop('name')", "    del d['name']", "    m.pop(k)"))));
    }

    // ------------------------------------------------------------ tuple index

    static int OutOfRange(List<string> found) => found.Count(m => m == "Tuple index out of range");

    [Fact]
    public void AConstantIndexPastEitherEndOfATupleIsReported()
    {
        var found = Problems(Src("def f(t: tuple[int, str]):", "    t[2]", "    t[-3]", "    t[1]", "    t[-2]", "    ()[0]"));
        Assert.Equal(3, OutOfRange(found));
    }

    [Fact]
    public void TupleSubclassesAndNamedTuplesKnowTheirLength()
    {
        var found = Problems(Src("from typing import NamedTuple", "", "", "class Pair(tuple[int, str]):", "    pass", "", "",
            "class Point(NamedTuple):", "    x: int", "    y: int", "", "", "def f(p: Pair, q: Point):", "    p[1]", "    p[5]", "    q[1]", "    q[2]"));
        Assert.Equal(2, OutOfRange(found));
    }

    [Fact]
    public void TuplesOfUnknownLengthAndOwnGetitemAreLeftAlone()
    {
        var found = Problems(Src("class Mine(tuple[int, str]):", "    def __getitem__(self, i):", "        return 0", "", "",
            "def f(t: tuple[int, ...], m: Mine, x: tuple[int, str]):", "    t[5]", "    m[5]", "    y: tuple[int, str] = x",
            "    if len(t) >= 2:", "        t[5]", "        t[-5]"));
        Assert.Equal(0, OutOfRange(found));
    }

    [Fact]
    public void AUnionIsReportedOnlyWhenTheIndexIsPastTheEndOfEveryTuple()
    {
        Assert.Equal(0, OutOfRange(Problems(Src("def f(t: tuple[int] | tuple[int, str]):", "    t[1]"))));
        Assert.Equal(1, OutOfRange(Problems(Src("def f(t: tuple[int] | tuple[int, str]):", "    t[2]"))));
    }

    [Fact]
    public void AnAssignmentToATupleItemHasItsOwnReportOnly()
    {
        var found = Problems(Src("t = (1, 2)", "t[5] = 1"));
        Assert.Equal(0, OutOfRange(found));
        Assert.Equal(1, found.Count(m => m.EndsWith("does not support item assignment")));
    }

    [Fact]
    public void TheServiceGivesBothReportsTheirCodes()
    {
        var svc = new LanguageService();
        svc.Open(Main, Movie + Src("def f(m: Movie, t: tuple[int, str]):", "    m.pop('name')", "    return t[2]"), 1);
        var diags = svc.Diagnostics(Main);
        Assert.Equal(1, diags.Count(d => d.Code == "tuple-index"));
        Assert.Equal(1, diags.Count(d => d.Code == "typeddict-keys" && d.Message.EndsWith("cannot be deleted")));
    }
}
