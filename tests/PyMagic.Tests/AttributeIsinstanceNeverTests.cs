using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>An isinstance check that rules out every type of an attribute leaves it unknown, as for names, not its declared type.</summary>
public class AttributeIsinstanceNeverTests
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

    const string Classes = "class A:\n    def a(self) -> None: ...\n\n\nclass B:\n    def b(self) -> None: ...\n\n\n";

    [Fact]
    public void AnAttributeCheckedAgainstAnUnrelatedClassIsNotReported()
    {
        Assert.Equal(0, Found(Classes + "class C:\n    x: A\n\n    def m(self):\n        if isinstance(self.x, B):\n            self.x.b()\n").Count);
    }

    [Fact]
    public void ANameCheckedAgainstAnUnrelatedClassIsNotReportedEither()
    {
        Assert.Equal(0, Found(Classes + "def f(x: A):\n    if isinstance(x, B):\n        x.b()\n").Count);
    }

    [Fact]
    public void ADottedChainCheckedAgainstAnUnrelatedClassIsNotReported()
    {
        Assert.Equal(0, Found(Classes + "class C:\n    x: A\n\n\nclass D:\n    c: C\n\n    def m(self):\n        if isinstance(self.c.x, B):\n            self.c.x.b()\n").Count);
    }

    [Fact]
    public void TheAttributeKeepsItsDeclaredTypeAfterABranchThatReturns()
    {
        Assert.Equal(0, Found(Classes + "class C:\n    x: A\n\n    def m(self):\n        if isinstance(self.x, B):\n            return\n        self.x.a()\n").Count);
        Assert.Equal(1, Found(Classes + "class C:\n    x: A\n\n    def m(self):\n        if isinstance(self.x, B):\n            return\n        self.x.b()\n").Count);
    }

    [Fact]
    public void TheAttributeKeepsItsDeclaredTypeOutsideTheBranch()
    {
        Assert.Equal("A", Last(Classes + "class C:\n    x: A\n\n\nc = C()\nif isinstance(c.x, B):\n    pass\nc.x"));
    }

    [Fact]
    public void AnIsinstanceCheckThatMatchesStillNarrows()
    {
        Assert.Equal("B", Last(Classes + "class C:\n    x: A | B\n\n\nc = C()\nif isinstance(c.x, B):\n    c.x"));
        Assert.Equal(1, Found(Classes + "class C:\n    x: A | B\n\n    def m(self):\n        if isinstance(self.x, B):\n            self.x.a()\n").Count);
    }

    [Fact]
    public void NoneChecksOnAnAttributeStillNarrow()
    {
        Assert.Equal("A", Last(Classes + "class C:\n    x: A | None\n\n\nc = C()\nif c.x is not None:\n    c.x"));
        Assert.Equal("None", Last(Classes + "class C:\n    x: A | None\n\n\nc = C()\nif c.x is None:\n    c.x"));
    }

    [Fact]
    public void AnEqualityCheckOnAnAttributeStillNarrows()
    {
        Assert.Equal("Literal['a']", Last("from typing import Literal\n\n\nclass C:\n    x: Literal['a', 'b']\n\n\nc = C()\nif c.x == 'a':\n    c.x"));
    }
}
