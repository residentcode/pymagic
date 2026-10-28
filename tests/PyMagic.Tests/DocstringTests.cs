using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>What a docstring says about its parameters (item 121), in the three styles, and where that shows in the signature popup.</summary>
public class DocstringTests
{
    static string Doc(params string[] lines) => string.Join("\n", lines);

    static string? P(string doc, string name) => Docstrings.Parameters(doc).GetValueOrDefault(name);

    [Fact]
    public void SphinxParamLines()
    {
        string doc = Doc(":param width: How wide", "    it is, over two lines.", ":param int height: How tall.", ":param str* unit: ignored", ":returns: the area", "", ":param late: after a blank line");
        Assert.Equal("How wide it is, over two lines.", P(doc, "width"));
        Assert.Equal("How tall.", P(doc, "height"));
        Assert.Equal("after a blank line", P(doc, "late"));
        Assert.True(P(doc, "returns") == null);
    }

    [Fact]
    public void GoogleArgsBlock()
    {
        string doc = Doc("Area of a box.", "", "Args:", "    width (int): How wide,", "        and how.", "    height: How tall.", "    *rest: The others.", "    **options (dict): Settings.", "", "Returns:", "    width: not a parameter here");
        Assert.Equal("How wide, and how.", P(doc, "width"));
        Assert.Equal("How tall.", P(doc, "height"));
        Assert.Equal("The others.", P(doc, "rest"));
        Assert.Equal("Settings.", P(doc, "options"));
        Assert.Equal(4, Docstrings.Parameters(doc).Count);
    }

    [Fact]
    public void NumpyParametersSection()
    {
        string doc = Doc("Area of a box.", "", "Parameters", "----------", "width : int", "    How wide", "    it is.", "height, depth : float, optional", "    How tall and deep.", "", "Returns", "-------", "int", "    The area.");
        Assert.Equal("How wide it is.", P(doc, "width"));
        Assert.Equal("How tall and deep.", P(doc, "height"));
        Assert.Equal("How tall and deep.", P(doc, "depth"));
        Assert.True(P(doc, "int") == null);
    }

    [Fact]
    public void ADocstringWithoutParametersHasNone()
    {
        Assert.Equal(0, Docstrings.Parameters("Just a sentence: with a colon.").Count);
        Assert.Equal(0, Docstrings.Parameters(null).Count);
    }

    [Fact]
    public void TheCleanedTextAndItsFirstParagraph()
    {
        Assert.Equal("Line one\nmore\n  indented", Docstrings.Clean("Line one\n    more\n      indented\n    "));
        Assert.Equal("Line one still one.", Docstrings.Summary("Line one\nstill one.\n\nSecond paragraph."));
        Assert.True(Docstrings.Summary("  ") == null);
    }

    // ------------------------------------------------------------ in the popup

    const string Main = "file:///c:/proj/main.py";

    static SignatureHelp? At(string marked)
    {
        int at = marked.IndexOf('|');
        string src = marked.Remove(at, 1);
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.SignatureHelp(Main, new LineMap(src).PositionOf(at));
    }

    [Fact]
    public void EachParameterCarriesItsDocumentation()
    {
        string src = "def area(width, height):\n    \"\"\"Area of a box.\n\n    Args:\n        width: How wide.\n    \"\"\"\n    return 1\n\narea(|";
        var h = At(src)!;
        Assert.Equal("Area of a box.", h.Signatures[0].Documentation);
        Assert.Equal("How wide.", h.Signatures[0].Parameters[0].Documentation);
        Assert.True(h.Signatures[0].Parameters[1].Documentation == null);
    }

    [Fact]
    public void ACallOfAClassShowsTheClassDocstringAndItsArgs()
    {
        string src = "class Box:\n    \"\"\"A box you can fill.\n\n    Args:\n        width: How wide.\n    \"\"\"\n\n    def __init__(self, width, height=1):\n        pass\n\nBox(|";
        var h = At(src)!;
        Assert.Equal("A box you can fill.", h.Signatures[0].Documentation);
        Assert.Equal("How wide.", h.Signatures[0].Parameters[0].Documentation);
    }

    [Fact]
    public void TheInitDocstringIsUsedWhenTheClassHasNone()
    {
        string src = "class Box:\n    def __init__(self, width):\n        \"\"\"Make a box.\n\n        :param width: How wide.\n        \"\"\"\n\nBox(|";
        var h = At(src)!;
        Assert.Equal("Make a box.", h.Signatures[0].Documentation);
        Assert.Equal("How wide.", h.Signatures[0].Parameters[0].Documentation);
    }
}
