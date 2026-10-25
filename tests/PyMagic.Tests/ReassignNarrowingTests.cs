using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A declared name that is assigned again has the assigned type from there on (httpx: `url: URL | str` ... `url = URL(url)`).</summary>
public class ReassignNarrowingTests
{
    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AParameterAssignedAgainTakesTheAssignedType()
    {
        Assert.Equal("K", Last("class K: ...\n\n\ndef f(p: K | str):\n    p = K()\n    p"));
        Assert.Equal("int", Last("def f(x: int | None):\n    x = 3\n    x"));
    }

    [Fact]
    public void TheAttributesOfTheAssignedClassAreFound()
    {
        Assert.Equal(0, Found("class URL:\n    scheme: str = ''\n    def __init__(self, url: 'URL | str') -> None: ...\n\n\ndef f(url: URL | str):\n    url = URL(url)\n    url.scheme\n").Count);
    }

    [Fact]
    public void AnAnnotatedVariableAssignedAgain()
    {
        Assert.Equal("str", Last("v: int | str = 1\nv = 'a'\nv"));
    }

    [Fact]
    public void WithoutAReassignmentTheDeclaredTypeStays()
    {
        Assert.Equal("int | str", Last("def f(p: int | str):\n    p"));
    }

    [Fact]
    public void ABranchThatAssignsJoinsWithTheOtherOne()
    {
        Assert.Equal("int | str", Last("def f(p: int | str, c: bool):\n    if c:\n        p = 1\n    p"));
    }

    [Fact]
    public void BytearrayAndMemoryviewGoWhereBytesIsWanted()
    {
        Assert.Equal(0, Found("def need(b: bytes) -> None: ...\n\n\nneed(bytearray(b'a'))\nneed(memoryview(b'a'))\nneed(b'a')\n").Count);
        Assert.Equal(1, Found("def need(b: bytes) -> None: ...\n\n\nneed('a')\n").Count);
    }
}
