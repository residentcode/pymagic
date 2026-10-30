using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Type Info (Ctrl+Shift+P) and External Documentation (Shift+F1) (item 162).</summary>
public class EditorInfoTests
{
    static (LanguageService Svc, string Uri) Open(string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_einfo_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "m.py");
        File.WriteAllText(path, text);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        string uri = LanguageService.UriOfPath(path);
        svc.Open(uri, text, 1);
        return (svc, uri);
    }

    static PyMagic.Server.Range On(int line, int from, int to) => new(new Position(line, from), new Position(line, to));

    [Fact]
    public void TheSmallestExpressionOverTheSelectionGivesItsType()
    {
        var (svc, uri) = Open("names = ['a', 'b']\ncount = len(names) + 1\n");
        var whole = svc.ExpressionType(uri, On(1, 8, 22))!;   // len(names) + 1
        Assert.Equal("int", whole.Type);
        Assert.Equal("len(names) + 1", whole.Text);
        var inner = svc.ExpressionType(uri, On(1, 12, 12))!;   // the cursor on "names"
        Assert.Equal("names", inner.Text);
        Assert.Equal("list[str]", inner.Type);
    }

    [Fact]
    public void TheStandardLibraryAndTheBuiltinsHaveTheirPagesOnDocsPythonOrg()
    {
        var (svc, uri) = Open("import json\nimport os.path\nfrom collections import OrderedDict\n\njson.dumps({})\nos.path.join('a', 'b')\nOrderedDict()\nlen([])\n'x'.split()\n\ndef mine():\n    pass\nmine()\n");
        // the standard library is there only when a typeshed or an interpreter is (the test modes with PYMAGIC_TYPESHED)
        if (svc.ExternalDoc(uri, new Position(4, 6)) is { } dumps)
        {
            Assert.Equal("https://docs.python.org/3/library/json.html#json.dumps", dumps.Url);
            var join = svc.ExternalDoc(uri, new Position(5, 9));   // os.path is posixpath or ntpath, as the stubs say
            Assert.True(join != null && join.Url.StartsWith("https://docs.python.org/3/library/") && join.Url.EndsWith("path.join"), join?.Url ?? "null");
            Assert.Equal("https://docs.python.org/3/library/collections.html#collections.OrderedDict", svc.ExternalDoc(uri, new Position(6, 2))?.Url ?? "null");
        }
        Assert.Equal("https://docs.python.org/3/library/functions.html#len", svc.ExternalDoc(uri, new Position(7, 1))?.Url ?? "null");
        Assert.Equal("https://docs.python.org/3/library/stdtypes.html#str.split", svc.ExternalDoc(uri, new Position(8, 5))?.Url ?? "null");
        Assert.True(svc.ExternalDoc(uri, new Position(12, 1)) is null, "the project's own function has no page on the web");
    }
}
