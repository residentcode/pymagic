using PyMagic.Server;
using Range = PyMagic.Server.Range;

namespace PyMagic.Tests;

/// <summary>PyCharm's inspections that need the shape of the code (item 192): __init__, decorators, relative imports, open(), docstrings, type hints.</summary>
public class PyCharmShapeInspectionTests
{
    static (LanguageService Svc, string Dir, string Uri) Make(string code, string file = "app.py", params (string Rel, string Text)[] more)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_shape_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (rel, text) in more.Append((file, code)))
        {
            string full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, file.Replace('/', Path.DirectorySeparatorChar)));
        svc.Open(uri, code, 1);
        return (svc, dir, uri);
    }

    static List<LspDiagnostic> Found(LanguageService svc, string uri, string code) => svc.Diagnostics(uri).Where(d => d.Code == code).ToList();

    static string Apply(string text, LanguageService svc, string uri, LspDiagnostic d, string title)
    {
        var action = svc.CodeActions(uri, d.Range).First(a => a.Title == title);
        var lines = new LineMap(text);
        foreach (var e in action.Edit!.Changes[uri].OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    [Fact]
    public void ClassmethodOutsideAClassAndBeforeAnotherDecorator()
    {
        string code = "import functools\n\n\n@staticmethod\ndef loose():\n    pass\n\n\nclass A:\n    @functools.cache\n    @classmethod\n    def make(cls):\n        pass\n\n    @classmethod\n    @functools.cache\n    def fine(cls):\n        pass\n\n    @abc_like\n    @staticmethod\n    def other():\n        pass\n";
        var (svc, _, uri) = Make(code.Replace("@abc_like", "@property"));
        var outside = Found(svc, uri, "decorator-outside-class").Single();
        Assert.Equal(3, outside.Range.Start.Line);
        var nesting = Found(svc, uri, "decorator-nesting");
        Assert.Equal(2, nesting.Count);
        Assert.Equal("@classmethod is applied before @cache: @cache receives a classmethod object, not the function; put @classmethod on top", nesting[0].Message);
        Assert.Equal(10, nesting[0].Range.Start.Line);
    }

    [Fact]
    public void ARelativeImportNeedsAPackage()
    {
        var (svc, _, uri) = Make("from .helpers import x\n", "script.py");
        Assert.Contains("no known parent package", Found(svc, uri, "relative-import").Single().Message);
        var (svc2, _, uri2) = Make("from .helpers import x\n", "pkg/mod.py", ("pkg/helpers.py", "x = 1\n"));
        Assert.Contains("a folder without __init__.py", Found(svc2, uri2, "relative-import").Single().Message);
        var (svc3, _, uri3) = Make("from .helpers import x\n", "pkg/mod.py", ("pkg/__init__.py", ""), ("pkg/helpers.py", "x = 1\n"));
        Assert.Equal(0, Found(svc3, uri3, "relative-import").Count);
    }

    [Fact]
    public void OpenInTextModeWantsAnEncoding()
    {
        string code = "open('a.txt')\nopen('a.txt', 'w')\nopen('a.bin', 'rb')\nopen('a.txt', encoding='utf-8')\nopen('a.txt', mode='a')\nopen('a', 'r', -1, 'utf-8')\n";
        var (svc, _, uri) = Make(code);
        var d = Found(svc, uri, "open-encoding");
        Assert.Equal("0,1,4", string.Join(",", d.Select(x => x.Range.Start.Line)));
        Assert.Equal("open('a.txt', 'w', encoding=\"utf-8\")\n", Apply(code, svc, uri, d[1], "Add encoding=\"utf-8\"").Split('\n')[1] + "\n");
        Assert.Equal("open('a.txt', mode='a', encoding=\"utf-8\")", Apply(code, svc, uri, d[2], "Add encoding=\"utf-8\"").Split('\n')[4]);
    }

    [Fact]
    public void ShadowedOpenIsNotTheBuiltin()
    {
        var (svc, _, uri) = Make("def open(path):\n    return path\n\n\nopen('a.txt')\n");
        Assert.Equal(0, Found(svc, uri, "open-encoding").Count);
    }

    [Fact]
    public void DocstringsInTripleDoubleQuotes()
    {
        string code = "'''Module.'''\n\n\ndef f():\n    'One line.'\n\n\ndef g():\n    \"\"\"Fine.\"\"\"\n\n\ndef h():\n    r'''Raw.'''\n";
        var (svc, _, uri) = Make(code);
        var d = Found(svc, uri, "docstring-quotes");
        Assert.Equal("0,4,12", string.Join(",", d.Select(x => x.Range.Start.Line)));
        Assert.Equal("    \"\"\"One line.\"\"\"", Apply(code, svc, uri, d[1], "Use triple double quotes").Split('\n')[4]);
        Assert.Equal("    r\"\"\"Raw.\"\"\"", Apply(code, svc, uri, d[2], "Use triple double quotes").Split('\n')[12]);
    }

    [Fact]
    public void MissingDocstringsAndTypeHintsAreOffUntilTurnedOn()
    {
        string code = "class Shape:\n    def area(self, scale):\n        \"\"\"\"\"\"\n        return 1\n\n    def _private(self):\n        pass\n\n\ndef total(items: list) -> int:\n    \"\"\"Sum.\"\"\"\n    return 0\n";
        var (svc, _, uri) = Make(code);
        Assert.Equal(0, Found(svc, uri, "missing-docstring").Count + Found(svc, uri, "missing-type-hint").Count);
        svc.SetInspection("missing-docstring", "weak");
        svc.SetInspection("missing-type-hint", "weak");
        var docs = Found(svc, uri, "missing-docstring").Select(x => x.Message).ToList();
        Assert.Equal(2, docs.Count);
        Assert.True(docs.Contains("Missing docstring in this class") && docs.Contains("The docstring of this method is empty"), string.Join(" | ", docs));
        var hints = Found(svc, uri, "missing-type-hint").Select(x => x.Message).ToList();
        Assert.Equal("Missing type hints: 'scale' and the return type | Missing type hints: the return type", string.Join(" | ", hints));
    }

    [Fact]
    public void AClassWithoutInitWhenTurnedOn()
    {
        string code = "from dataclasses import dataclass\nfrom enum import Enum\n\n\nclass Plain:\n    x = 1\n\n\nclass Child(Plain):\n    pass\n\n\nclass Built:\n    def __init__(self):\n        pass\n\n\nclass FromBuilt(Built):\n    pass\n\n\n@dataclass\nclass Point:\n    x: int\n\n\nclass Color(Enum):\n    RED = 1\n\n\nclass Oops(ValueError):\n    pass\n";
        var (svc, _, uri) = Make(code);
        Assert.Equal(0, Found(svc, uri, "class-no-init").Count);
        svc.SetInspection("class-no-init", "weak");
        Assert.Equal("Plain,Child", string.Join(",", Found(svc, uri, "class-no-init").Select(d => code.Split('\n')[d.Range.Start.Line][6..^(code.Split('\n')[d.Range.Start.Line].EndsWith("(Plain):") ? 8 : 1)])));
    }
}
