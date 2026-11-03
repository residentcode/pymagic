using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>An argument that is unknown, or has unknown parts, and fits more than one overload: the call returns Unknown when those overloads return different things, as in pyright.</summary>
public class OverloadUnknownTests
{
    const string Over = "from typing import overload, Iterable\n\n@overload\ndef total(xs: Iterable[bool]) -> int: ...\n@overload\ndef total(xs: Iterable[str]) -> str: ...\ndef total(xs): ...\n\n";

    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AnUnknownArgumentThatFitsOverloadsWithDifferentReturnsGivesUnknown()
    {
        Assert.Equal("Unknown", T(Over + "def f(x):\n    total(x)\n"));
        Assert.Equal("Unknown", T(Over + "def f(shapes):\n    total(s.area() for s in shapes)\n"));
        // known arguments pick their overload as before
        Assert.Equal("int", T(Over + "def f():\n    total([True])\n"));
        Assert.Equal("str", T(Over + "def f():\n    total(['a'])\n"));
    }

    [Fact]
    public void OverloadsThatReturnTheSameKeepIt()
    {
        const string same = "from typing import overload\n\n@overload\ndef g(x: int) -> bytes: ...\n@overload\ndef g(x: str) -> bytes: ...\ndef g(x): ...\n\n";
        Assert.Equal("bytes", T(same + "def f(x):\n    g(x)\n"));
        // a later overload the arguments do not fit does not count
        const string arity = "from typing import overload\n\n@overload\ndef h(x: int) -> int: ...\n@overload\ndef h(x: int, y: int) -> str: ...\ndef h(x, y=0): ...\n\n";
        Assert.Equal("int", T(arity + "def f(x):\n    h(x)\n"));
    }

    [Fact]
    public void SumOfUnknownItemsIsUnknownAndHasNoReturnHint()
    {
        if (Workspace.DefaultTypeshedPath == null) return;
        Assert.Equal("Unknown", T("def total(shapes):\n    return sum(s.area() for s in shapes)\n\ntotal([])\n"));
        Assert.Equal("int", T("sum([1, 2])\n"));

        string dir = Path.Combine(Path.GetTempPath(), "pm_sum_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "m.py");
        const string text = "def total(shapes):\n    return sum(s.area() for s in shapes)\n";
        File.WriteAllText(path, text);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        string uri = LanguageService.UriOfPath(path);
        svc.Open(uri, text, 1);
        var hints = svc.InlayHints(uri, new PyMagic.Server.Range(new Position(0, 0), new Position(3, 0)));
        Assert.True(!hints.Any(h => h.Label.StartsWith("->")), string.Join(", ", hints.Select(h => h.Label)));
    }
}
