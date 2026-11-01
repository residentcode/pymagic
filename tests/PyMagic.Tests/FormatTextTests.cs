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

    // ------------------------------------------------------------ item 213: magics, a selection

    [Fact]
    public void MagicLinesAreSetAsideAndPutBack()
    {
        Assert.Equal("%matplotlib inline\nimport os\n\nx = [1, 2]\n!pip install rich\nfiles = !ls -la", Format("%matplotlib inline\nimport os\n\nx=[1,2]\n!pip install rich\nfiles = !ls -la"));
        // inside a block the magic takes the block's indentation
        Assert.Equal("for i in range(3):\n    %time f(i)\n    y = i * 2", Format("for i in range(3):\n  %time f(i)\n  y=i*2"));
        // a magic that goes on over a line break is kept whole
        Assert.Equal("!echo one \\\n  two\nz = 1", Format("!echo one \\\n  two\nz=1"));
        // %%time's body is Python
        Assert.Equal("%%time\ntotal = sum([1, 2])", Format("%%time\ntotal=sum([1,2])"));
    }

    [Fact]
    public void ACellThatIsNotPythonIsLeftAlone()
    {
        string said = "";
        try { Format("%%bash\nls -la | wc -l"); }
        catch (ServiceException e) { said = e.Message; }
        Assert.Contains("%%bash cell is not Python", said);
        // a % inside brackets is code (the modulo of a continued expression), not a magic
        Assert.Equal("x = 10 % 3", Format("x = (10\n%3)"));
    }

    [Fact]
    public void OnlyTheSelectedLinesOfACell()
    {
        var svc = new LanguageService();
        string cell = "a=1\nb=2\n%time c=3\nd=4";
        var range = new PyMagic.Server.Range(new Position(1, 0), new Position(1, 3));
        Assert.Equal("a=1\nb = 2\n%time c=3\nd=4", svc.FormatText(cell, Uri, range: range));
        Assert.Equal("a = 1\nb = 2\n%time c=3\nd = 4", svc.FormatText(cell, Uri));
    }

    [Fact]
    public void ThePagesQuoteSettingIsUsedWhenTheProjectDoesNotNameOne()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_quotes_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "other"));
        try
        {
            File.WriteAllText(Path.Combine(root, "pyproject.toml"), "[project]\nname = 'shop'\n");
            string uri = new Uri(Path.Combine(root, "tests.py")).AbsoluteUri;
            const string code = "r = client.get(\"/shop/about/\")\ns = \"it's\"\n";
            var svc = new LanguageService();
            Assert.Equal(code, svc.FormatText(code, uri));   // black's double quotes without the setting
            svc.DefaultQuotes = PyMagic.Server.Formatting.QuoteStyle.Single;
            Assert.Equal("r = client.get('/shop/about/')\ns = \"it's\"\n", svc.FormatText(code, uri));   // single, unless that needs an escape
            // a project that names its quote style keeps it
            File.WriteAllText(Path.Combine(root, "other", "pyproject.toml"), "[tool.ruff.format]\nquote-style = \"double\"\n");
            string other = new Uri(Path.Combine(root, "other", "a.py")).AbsoluteUri;
            Assert.Equal("x = \"a\"\n", svc.FormatText("x = 'a'\n", other));
        }
        finally { Directory.Delete(root, true); }
    }
}

