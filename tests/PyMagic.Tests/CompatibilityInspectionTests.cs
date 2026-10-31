using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's version, deprecation and docstring-type inspections (item 196).</summary>
public class CompatibilityInspectionTests
{
    static (LanguageService Svc, string Uri) Make(string code, string? pyproject = null)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_compat_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        if (pyproject != null) File.WriteAllText(Path.Combine(dir, "pyproject.toml"), pyproject);
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "app.py"));
        File.WriteAllText(Path.Combine(dir, "app.py"), code);
        svc.Open(uri, code, 1);
        return (svc, uri);
    }

    static List<LspDiagnostic> Found(LanguageService svc, string uri, string code) => svc.Diagnostics(uri).Where(d => d.Code == code).ToList();

    const string Modern =
        "import tomllib\nfrom typing import Self, Optional\n\n\n" +
        "def area(x: int | None, rows: list[int]) -> float:\n    match x:\n        case 1:\n            return 1.0\n    return 0.0\n\n\n" +
        "type Pair = tuple[int, int]\n\n\nclass Box[T]:\n    pass\n";

    [Fact]
    public void SyntaxAndNamesNewerThanRequiresPython()
    {
        var (svc, uri) = Make(Modern, "[project]\nname = \"shop\"\nrequires-python = \">=3.8\"\n");
        var d = Found(svc, uri, "python-version").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        // tomllib, Self, int | None, list[int], match, type, class Box[T]
        Assert.Equal(7, d.Count);
        Assert.True(d[0].StartsWith("0: 'tomllib' needs Python 3.11, and the project supports 3.8 (requires-python >=3.8)"), d[0]);
        Assert.True(d.Any(x => x.StartsWith("4:") && x.Contains("X | Y in an annotation evaluated at run time needs Python 3.10")));
        Assert.True(d.Any(x => x.StartsWith("4:") && x.Contains("list[...] in an annotation evaluated at run time needs Python 3.9")));
        Assert.True(d.Any(x => x.StartsWith("5:") && x.Contains("The match statement needs Python 3.10")));
        Assert.True(d.Any(x => x.StartsWith("14:") && x.Contains("Type parameter syntax (class C[T])")));
    }

    [Fact]
    public void PostponedAnnotationsAndANewerRequirementAreFine()
    {
        var (svc, uri) = Make("from __future__ import annotations\n\n\ndef f(x: int | None) -> list[int]:\n    return []\n", "[project]\nrequires-python = \">=3.8\"\n");
        Assert.Equal(0, Found(svc, uri, "python-version").Count);
        var (svc2, uri2) = Make(Modern, "[project]\nrequires-python = \">=3.12\"\n");
        Assert.Equal(0, Found(svc2, uri2, "python-version").Count);
        var (svc3, uri3) = Make(Modern, "[tool.poetry.dependencies]\npython = \"^3.10\"\n");
        Assert.Equal("0,1,11,14", string.Join(",", Found(svc3, uri3, "python-version").Select(x => x.Range.Start.Line)));
    }

    [Fact]
    public void DeprecatedCallsAndModulesAreStruckThrough()
    {
        string code = "import imghdr\nfrom warnings import deprecated\n\n\n@deprecated('Use total() instead')\ndef old_total(x):\n    return x\n\n\n@deprecated('Gone soon')\nclass Legacy:\n    pass\n\n\nold_total(1)\nLegacy()\n";
        var (svc, uri) = Make(code);
        var d = Found(svc, uri, "deprecated");
        Assert.Equal("0,14,15", string.Join(",", d.Select(x => x.Range.Start.Line)));
        Assert.Equal("'old_total' is deprecated: Use total() instead", d[1].Message);
        Assert.Contains("'imghdr' is deprecated since Python 3.11 and removed in 3.13", d[0].Message);
        Assert.True(d.All(x => x.Tags?.Contains(DiagnosticTag.Deprecated) == true));
    }

    [Fact]
    public void ADocstringTypeThatIsNotTheAnnotations()
    {
        string code = "def price(count: int, name: str | None) -> float:\n    \"\"\"The price.\n\n    :type count: str\n    :type name: str or None\n    :rtype: int\n    \"\"\"\n    return 1.0\n\n\n" +
            "def label(text: str) -> str:\n    \"\"\"A label.\n\n    Args:\n        text (bytes): the text.\n    \"\"\"\n    return text\n\n\n" +
            "def fine(x: list[int]) -> 'Shape':\n    \"\"\"Fine.\n\n    :type x: list\n    :rtype: Shape\n    \"\"\"\n";
        var (svc, uri) = Make(code);
        var d = Found(svc, uri, "docstring-type").Select(x => x.Message).ToList();
        Assert.Equal("The docstring says 'count' is str, and its annotation says int | The docstring says 'price' returns int, and its annotation says float | The docstring says 'text' is bytes, and its annotation says str", string.Join(" | ", d));
    }
}
