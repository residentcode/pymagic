using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Formatting a piece of text that is not a document (a notebook cell), item 120.</summary>
public class FormatTextTests
{
    const string Uri = "file:///c:/proj/main.py";

    static string Format(string text) => new LanguageService().FormatText(text, Uri);

    [Fact]
    public void ACellWithoutANewlineAtTheEndGetsNone()
    {
        Assert.Equal("x = 1\ny = [1, 2]", Format("x=1\ny  =  [1,2]"));
    }

    [Fact]
    public void ATextWithANewlineKeepsIt()
    {
        Assert.Equal("x = 1\n", Format("x=1\n"));
    }

    [Fact]
    public void FormattedCodeIsLeftAlone()
    {
        Assert.Equal("def f(a, b):\n    return a + b", Format("def f(a, b):\n    return a + b"));
    }

    [Fact]
    public void ImportsAreNotSorted()
    {
        Assert.Equal("import sys\nimport os", Format("import sys\nimport os"));
    }

    [Fact]
    public void CodeThatDoesNotParseIsRefused()
    {
        string said = "";
        try { Format("def f(:\n    pass"); }
        catch (ServiceException e) { said = e.Message; }
        Assert.Contains("syntax errors", said);
    }
}
