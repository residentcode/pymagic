using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Values nothing is known about, and typing.cast, must not bring back a declared type's None.</summary>
public class AssignedUnknownTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AnUnresolvedCallFillingAnOptionalParameterIsNotNone()
    {
        Assert.Equal(0, Found("from mystery import fmt\n\n\ndef need(x: str) -> None: ...\n\n\ndef f(message: str | None = None):\n    if message is None:\n        message = fmt('x')\n    need(message)\n").Count);
    }

    [Fact]
    public void AKnownValueStillNarrows()
    {
        Assert.Equal(0, Found("def need(x: str) -> None: ...\n\n\ndef f(message: str | None = None):\n    if message is None:\n        message = 'x'\n    need(message)\n").Count);
        Assert.Equal(1, Found("def need(x: str) -> None: ...\n\n\ndef f(message: str | None = None):\n    need(message)\n").Count);
    }

    [Fact]
    public void CastGivesTheTypeItNames()
    {
        Assert.Equal("int", Last("from typing import cast\n\ncast(int, '3')"));
        Assert.Equal("list[int]", Last("from typing import cast\n\ncast('list[int]', 3)"));
        Assert.Equal("str", Last("import typing as t\n\nt.cast(str, 3)"));
    }

    [Fact]
    public void CastInTheOptionalPattern()
    {
        Assert.Equal(0, Found("import typing as t\nfrom typing import Iterable\n\n\ndef need(x: Iterable[int]) -> None: ...\n\n\ndef f(it: Iterable[int] | None = None, n: int = 3):\n    if it is None:\n        it = t.cast('Iterable[int]', range(n))\n    need(it)\n").Count);
    }
}
