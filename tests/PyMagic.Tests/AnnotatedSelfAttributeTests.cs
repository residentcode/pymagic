using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>An instance attribute declared with <c>self.x: T</c> has the type T; other assignments to it do not widen it.</summary>
public class AnnotatedSelfAttributeTests
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

    const string Classes =
        "class Response:\n    def read1(self, n: int) -> bytes: ...\n\n\n" +
        "class Stream:\n    def read(self) -> bytes: ...\n\n\n";

    [Fact]
    public void TheDeclaredTypeIsTheAttributeType()
    {
        Assert.Equal("Response | None", Last(Classes +
            "class Reader:\n    def __init__(self, body: Stream):\n        self._fp: Response | None = None\n        self._fp = body\n\n\nReader(Stream())._fp"));
    }

    [Fact]
    public void AnAssignmentInAnotherMethodDoesNotWidenIt()
    {
        Assert.Equal("Response | None", Last(Classes +
            "class Reader:\n    def __init__(self):\n        self._fp: Response | None = None\n\n    def reset(self, body: Stream):\n        self._fp = body\n\n\nReader()._fp"));
    }

    [Fact]
    public void AMemberOfTheDeclaredTypeIsFoundInAnotherMethod()
    {
        var found = Found(Classes +
            "class Reader:\n    def __init__(self, body: Stream):\n        self._fp: Response | None = None\n        self._fp = body\n\n" +
            "    def peek(self) -> bytes:\n        assert self._fp\n        return self._fp.read1(1)\n");
        Assert.Equal(0, found.Count(s => s.Contains("read1")));
    }

    [Fact]
    public void WithoutADeclarationTheAssignmentsAreStillUnioned()
    {
        Assert.Equal("Response | Stream", Last(Classes +
            "class Reader:\n    def __init__(self, body: Stream):\n        self._fp = Response()\n        self._fp = body\n\n\nReader(Stream())._fp"));
    }

    [Fact]
    public void AClassLevelDeclarationStillWins()
    {
        Assert.Equal("Response | None", Last(Classes +
            "class Reader:\n    _fp: Response | None\n\n    def __init__(self, body: Stream):\n        self._fp = body\n\n\nReader(Stream())._fp"));
    }
}
