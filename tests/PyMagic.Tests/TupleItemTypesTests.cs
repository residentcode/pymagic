using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Tuples of the same length with other item types (`tuple[int, str]` for `tuple[int, int]`), reported as PyCharm does.</summary>
public class TupleItemTypesTests
{
    static List<string> Reports(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).Where(r => r.Contains("Expected type")).ToList();
    }

    const string Want2 = "def want2(p: tuple[int, int]) -> None: ...\n\n\n";

    [Fact]
    public void ATupleOfOtherItemTypesIsReportedInACall()
    {
        var found = Reports(Want2 + "def f(u: tuple[int, str], ok: tuple[int, int], b: tuple[bool, int]):\n    want2(u)\n    want2(ok)\n    want2(b)\n    want2((1, \"a\"))\n    want2((1, 2))\n");
        Assert.Equal("Expected type 'tuple[int, int]', got 'tuple[int, str]' instead|Expected type 'tuple[int, int]', got 'tuple[int, str]' instead", string.Join("|", found));
    }

    [Fact]
    public void AnAnnotatedAssignmentAndAReturnAreReported()
    {
        // these two checks are the server's (LanguageService.TypeChecks.cs)
        const string uri = "file:///c:/proj/main.py";
        var svc = new PyMagic.Server.LanguageService();
        svc.Open(uri, "x: tuple[int, int] = (1, \"a\")\ny: tuple[int, int] = (1, 2)\n\n\ndef g() -> tuple[int, int]:\n    return 1, \"a\"\n\n\ndef h() -> tuple[int, int]:\n    return 1, 2\n", 1);
        var found = svc.Diagnostics(uri).Where(d => d.Code is "assignment-type" or "return-type").Select(d => d.Message).ToList();
        Assert.Equal(2, found.Count);
        Assert.True(found.All(r => r.Contains("tuple[int, int]") && r.Contains("tuple[int, str]")), string.Join(" | ", found));
    }

    [Fact]
    public void NestedTuplesAndATupleOfAnyLengthAreReported()
    {
        var found = Reports("def n(p: tuple[int, tuple[int, int]]) -> None: ...\n\n\ndef any_(p: tuple[int, ...]) -> None: ...\n\n\n"
            + "def f(t: tuple[int, tuple[int, str]], s: tuple[int, str]):\n    n(t)\n    n((1, (2, \"b\")))\n    n((1, (2, 3)))\n    any_(s)\n    any_((1, \"a\"))\n    any_((1, 2, 3))\n");
        Assert.Equal(4, found.Count);
        Assert.Equal(2, found.Count(r => r.Contains("got 'tuple[int, tuple[int, str]]'")));
        Assert.Equal(2, found.Count(r => r.StartsWith("Expected type 'tuple[int, ...]', got 'tuple[int, str]'")));
    }

    [Fact]
    public void AnUnknownOrAnyItemIsNotReported()
    {
        var src = "from typing import Any\n\n\n" + Want2 + "def f(a: Any, t: tuple[int, Any], v):\n    want2((1, a))\n    want2(t)\n    want2((v, 1))\n    want2((1, unknown_name()))\n";
        Assert.Equal(0, Reports(src).Count);
    }

    [Fact]
    public void ItemsMayBeSubclassesOrPromoted()
    {
        Assert.Equal(0, Reports("def w(p: tuple[float, object]) -> None: ...\n\n\ndef f(t: tuple[int, str]):\n    w(t)\n    w((1, None))\n").Count);
    }
}
