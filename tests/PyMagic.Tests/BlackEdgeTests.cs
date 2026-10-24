using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Black's habits for one-line compound statements, semicolons and numbers.</summary>
public class BlackEdgeTests
{
    static string Fmt(string code) => PythonFormatter.Format(code) ?? throw new AssertionException("declined: " + PythonFormatter.LastMismatch);

    [Fact]
    public void OneLineCompoundStatementsAreSplit()
    {
        Assert.Equal("if x:\n    y = 1\n", Fmt("if x: y = 1\n"));
        Assert.Equal("while True:\n    pass\n", Fmt("while True: pass\n"));
        Assert.Equal("with a:\n    pass\n", Fmt("with a: pass\n"));
        Assert.Equal("for x in y:\n    print(x)\n", Fmt("for x in y: print(x)\n"));
        Assert.Equal("class A:\n    pass\n", Fmt("class A: pass\n"));
    }

    [Fact]
    public void TryExceptFinallyAndElseAreSplitToo()
    {
        Assert.Equal("try:\n    x\nexcept E:\n    y\nfinally:\n    z\n", Fmt("try: x\nexcept E: y\nfinally: z\n"));
        Assert.Equal("if x:\n    pass\nelse:\n    pass\n", Fmt("if x: pass\nelse: pass\n"));
    }

    [Fact]
    public void ADefWithAStubBodyStaysOnOneLine()
    {
        Assert.Equal("def f(): ...\n", Fmt("def f(): ...\n"));
        Assert.Equal("class A: ...\n", Fmt("class A: ...\n"));
    }

    [Fact]
    public void ALambdaColonIsNotTheHeaderColon()
    {
        Assert.Equal("if x:\n    y = lambda a: a\n", Fmt("if x: y = lambda a: a\n"));
        Assert.Equal("if (lambda: 1)():\n    pass\n", Fmt("if (lambda: 1)(): pass\n"));
        Assert.Equal("f = lambda a: a\ng = 1\n", Fmt("f = lambda a: a; g = 1\n"));
    }

    [Fact]
    public void SemicolonsSeparateLines()
    {
        Assert.Equal("a = 1\nb = 2\n", Fmt("a = 1; b = 2\n"));
        Assert.Equal("a = 1\n", Fmt("a = 1;\n"));
        Assert.Equal("if x:\n    y = 1\n    z = 2\n", Fmt("if x: y = 1; z = 2\n"));
        Assert.Equal("x = {1: 2}\ny = [1, 2]\n", Fmt("x = {1: 2}; y = [1,2]\n"));
    }

    [Fact]
    public void InsideABlockTheBodyIsIndentedOnceMore()
    {
        Assert.Equal("class A:\n    def f(self):\n        return 1\n\n    x = 1\n    y = 2\n", Fmt("class A:\n    def f(self): return 1\n    x = 1; y = 2\n"));
    }

    [Fact]
    public void ATrailingCommentStaysWithTheLastStatement()
    {
        Assert.Equal("if x:\n    y = 1  # why\n", Fmt("if x: y = 1  # why\n"));
        Assert.Equal("if x:  # why\n    y = 1\n", Fmt("if x:  # why\n    y = 1\n"));
    }

    [Fact]
    public void SplittingIsStable()
    {
        string once = Fmt("if x: y = 1; z = 2\nelse: w = 3\n");
        Assert.Equal(once, Fmt(once));
    }

    [Fact]
    public void NumbersGetBlacksSpelling()
    {
        Assert.Equal("x = 0xFF + 1e5 + 0o17 + 1_0j + 0b1\n", Fmt("x = 0XFF + 1E5 + 0O17 + 1_0J + 0B1\n"));
        Assert.Equal("x = 0xABCDEF\n", Fmt("x = 0xabcdef\n"));
        Assert.Equal("x = 1.0 + 0.5 + 1.0e3 + 2.5j\n", Fmt("x = 1. + .5 + 1.E3 + 2.5J\n"));
        Assert.Equal("x = 10 + 1_000\n", Fmt("x = 10 + 1_000\n"));
    }
}
