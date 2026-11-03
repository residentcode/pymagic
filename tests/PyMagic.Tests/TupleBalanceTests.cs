using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's tuple assignment balance: more or fewer values than targets in an unpacking assignment, when the number of values is known.</summary>
public class TupleBalanceTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<string> Problems(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).Where(x => x.EndsWith("values to unpack")).ToList();
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    [Fact]
    public void ATupleDisplayWithMoreOrFewerValues()
    {
        Assert.Equal("Too many values to unpack", string.Join("; ", Problems(Src("a, b = 1, 2, 3"))));
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("a, b, c = (1, 2)"))));
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("[a, b, c] = 1, 2"))));
        Assert.Equal(0, Problems(Src("a, b = 1, 2", "(a, b), c = (1, 2), 3")).Count);
    }

    [Fact]
    public void ANameWhoseTypeIsAFixedLengthTuple()
    {
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("t = (1, 2)", "a, b, c = t"))));
        Assert.Equal("Too many values to unpack", string.Join("; ", Problems(Src("def f(t: tuple[int, str, bytes]):", "    a, b = t"))));
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("from typing import NamedTuple", "class P(NamedTuple):", "    x: int", "    y: int", "def f(p: P):", "    a, b, c = p"))));
        // every member of a union has to be wrong the same way
        Assert.Equal("Too many values to unpack", string.Join("; ", Problems(Src("def f(t: tuple[int, int, int] | tuple[str, str, str]):", "    a, b = t"))));
        Assert.Equal(0, Problems(Src("def f(t: tuple[int, int] | tuple[int, int, int]):", "    a, b = t")).Count);
    }

    [Fact]
    public void StarTargetsTakeTheRest()
    {
        Assert.Equal(0, Problems(Src("a, *b = 1, 2, 3", "a, *b, c = 1, 2", "*a, = ()")).Count);
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("a, *b, c = (1,)"))));
        Assert.Equal("Need more values to unpack", string.Join("; ", Problems(Src("def f(t: tuple[int]):", "    a, b, *c = t"))));
    }

    [Fact]
    public void ValuesOfUnknownNumberAreLeftAlone()
    {
        Assert.Equal(0, Problems(Src("def f(t: tuple[int, ...], l: list[int], x, s: str):", "    a, b = t", "    a, b = l", "    a, b = x", "    a, b = s",
            "    a, b = *l, 1", "    a, b = 1, *l, 2, 3")).Count);
        Assert.Equal(0, Problems(Src("def f(t: tuple[int, int] | list[int]):", "    a, b, c = t")).Count);
    }

    [Fact]
    public void TheServiceGivesItsCode()
    {
        var svc = new LanguageService();
        svc.Open(Main, Src("a, b = 1, 2, 3"), 1);
        Assert.Equal(1, svc.Diagnostics(Main).Count(d => d.Code == "tuple-assignment-balance"));
    }
}
