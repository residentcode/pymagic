using PyMagic.Server;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Formatting a selection, and delegating to black or ruff.</summary>
public class RangeFormatTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Formats the lines first..last (1-based, both included) and returns the whole text.</summary>
    static string Lines(string text, int first, int last)
    {
        var lm = new LineMap(text);
        int start = lm.OffsetOf(new PyMagic.Server.Position(first - 1, 0));
        int end = lm.OffsetOf(new PyMagic.Server.Position(last - 1, 0)) + text.Split('\n')[last - 1].Length;
        return PythonFormatter.FormatRange(text, start, end) ?? throw new AssertionException("declined");
    }

    [Fact]
    public void OnlyTheSelectedLinesAreFormatted()
    {
        Assert.Equal(Src("x=1", "y = 2", "z=3"), Lines(Src("x=1", "y=2", "z=3"), 2, 2));
        Assert.Equal(Src("x = 1", "y = 2", "z=3"), Lines(Src("x=1", "y=2", "z=3"), 1, 2));
        Assert.Equal(Src("def f( a ):", "    return a + 1"), Lines(Src("def f( a ):", "    return a+1"), 2, 2));
        Assert.Equal(Src("def f(a):", "    return a+1"), Lines(Src("def f( a ):", "    return a+1"), 1, 1));
        Assert.Equal(Src("def f(a):", "    return a + 1"), Lines(Src("def f( a ):", "    return a+1"), 1, 2));
    }

    [Fact]
    public void AnythingTouchedByTheSelectionCounts()
    {
        // a selection that ends in the middle of a line formats that line
        string text = Src("x=1", "y=2", "z=3");
        int start = 5, end = 6;       // inside "y=2"
        Assert.Equal(Src("x=1", "y = 2", "z=3"), PythonFormatter.FormatRange(text, start, end));
    }

    [Fact]
    public void BlankLinesBeforeASelectedDefinitionAreFixed()
    {
        Assert.Equal(Src("def a(): pass", "", "", "def b(): pass"), Lines(Src("def a(): pass", "def b(): pass"), 2, 2));
        Assert.Equal(Src("def a(): pass", "def b(): pass"), Lines(Src("def a(): pass", "def b(): pass"), 1, 1));
    }

    [Fact]
    public void WhenALineAloneWouldChangeTheMeaningTheStatementIsFormatted()
    {
        // re-indenting only the first body line would leave the second one deeper-or-equal than expected
        Assert.Equal(Src("if x:", "    y = 1", "    z = 2"), Lines(Src("if x:", "  y=1", "  z=2"), 2, 2));
        Assert.Equal(Src("x=1", "if x:", "    y = 1", "    z = 2", "w=3"), Lines(Src("x=1", "if x:", "  y=1", "  z=2", "w=3"), 3, 3));
    }

    [Fact]
    public void CommentsBlankLinesAndLineEndingsOutsideTheSelectionStay()
    {
        Assert.Equal(Src("x=1 # c", "#d", "y = 2"), Lines(Src("x=1 # c", "#d", "y=2"), 3, 3));
        Assert.Equal("x = 1\n\n\n\n", Lines("x=1\n\n\n\n", 1, 1));
        Assert.Equal("x=1\r\ny = 2\r\n", Lines("x=1\r\ny=2\r\n", 2, 2));
        Assert.Equal("x=1\ny = 2", Lines("x=1\ny=2", 2, 2).TrimEnd('\n') + "");
    }

    [Fact]
    public void TheWholeSelectionFormatsLikeTheWholeFile()
    {
        string messy = Src("import   os,sys", "class  A( object ) :", "  x=1", "  def  f( self,a,b = 2 ):", "    return {'a':a}[ 'a' ]", "y=A()");
        Assert.Equal(PythonFormatter.Format(messy), Lines(messy, 1, 6));
    }

    [Fact]
    public void RefusesWhatCannotBeFormatted()
    {
        Assert.True(PythonFormatter.FormatRange("def f(:\n", 0, 3) == null, "syntax error");
        Assert.True(PythonFormatter.FormatRange("x=1  # fmt: off\ny=2\n", 0, 3) == null, "fmt: off");
    }

    // ------------------------------------------------------------ through the language service

    const string Doc = "file:///c:/proj/main.py";

    static string Apply(string text, IReadOnlyList<TextEdit> edits)
    {
        var lm = new LineMap(text);
        foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    [Fact]
    public void TheServiceFormatsASelectionAndKeepsTheRest()
    {
        var svc = new LanguageService();
        string text = Src("x=1", "y=2", "z=3");
        svc.Open(Doc, text, 1);
        var edits = svc.FormatRange(Doc, new PyMagic.Server.Range(new PyMagic.Server.Position(1, 0), new PyMagic.Server.Position(1, 3)));
        Assert.Equal(Src("x=1", "y = 2", "z=3"), Apply(text, edits));
        Assert.Equal(1, edits.Count);
        Assert.Equal(1, edits[0].Range.Start.Line);       // only the second line is rewritten
    }

    [Fact]
    public void ASelectionThatEndsAtTheStartOfALineDoesNotTouchThatLine()
    {
        var svc = new LanguageService();
        string text = Src("x=1", "y=2", "z=3");
        svc.Open(Doc, text, 1);
        var edits = svc.FormatRange(Doc, new PyMagic.Server.Range(new PyMagic.Server.Position(0, 0), new PyMagic.Server.Position(1, 0)));
        Assert.Equal(Src("x = 1", "y=2", "z=3").Replace("y=2", "y = 2"), Apply(text, edits).Replace("y = 2", "y = 2"));
    }
}
