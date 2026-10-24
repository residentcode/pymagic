using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Constants written in a call are checked against Literal[...] parameters.</summary>
public class LiteralArgumentTests
{
    const string Pre = "from typing import Literal, Final\n\n";

    static List<string> Mismatches(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AConstantArgumentIsCheckedAgainstALiteralParameter()
    {
        var found = Mismatches("def open_it(mode: Literal[\"r\", \"w\"]): ...\n\n\nopen_it(\"r\")\nopen_it(\"w\")\nopen_it(\"x\")\nopen_it(mode=\"zz\")\n");
        Assert.Equal(2, found.Count);
        Assert.True(found.All(m => m.Contains("Expected type")), string.Join(" / ", found));
        Assert.True(found.Any(m => m.Contains("Literal['x']")), string.Join(" / ", found));
    }

    [Fact]
    public void NumbersAndBooleansAreCheckedToo()
    {
        var found = Mismatches("def level(n: Literal[1, 2, -1]): ...\n\n\ndef flag(b: Literal[True]): ...\n\n\nlevel(1)\nlevel(-1)\nlevel(3)\nflag(True)\nflag(False)\n");
        Assert.Equal(2, found.Count);
    }

    [Fact]
    public void ANonConstantArgumentIsNotGuessed()
    {
        var found = Mismatches("def open_it(mode: Literal[\"r\", \"w\"]): ...\n\n\ndef g(name: str):\n    open_it(name)\n    open_it(name + \"x\")\n");
        Assert.Equal(0, found.Count);
    }

    [Fact]
    public void ALiteralAlsoGoesWhereItsClassIsWanted()
    {
        var found = Mismatches("def put(text: str, n: int, f: float): ...\n\n\nput(\"a\", 1, 2)\n");
        Assert.Equal(0, found.Count);
    }
}
