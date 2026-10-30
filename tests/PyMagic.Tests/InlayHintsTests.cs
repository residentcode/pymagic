using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Inlay hints (item 161): parameter names, parametrize names, class pattern names, return and variable types, method chains.</summary>
public class InlayHintsTests
{
    static List<(int Line, int Character, string Label, int Kind)> Hints(string text)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_hints_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, "m.py");
        File.WriteAllText(path, text);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        string uri = LanguageService.UriOfPath(path);
        svc.Open(uri, text, 1);
        int lines = text.Split('\n').Length;
        return svc.InlayHints(uri, new PyMagic.Server.Range(new Position(0, 0), new Position(lines, 0)))
            .Select(h => (h.Position.Line, h.Position.Character, h.Label, h.Kind)).ToList();
    }

    static bool Has(List<(int Line, int Character, string Label, int Kind)> hints, int line, string label) => hints.Any(h => h.Line == line && h.Label == label);

    [Fact]
    public void PositionalArgumentsGetTheirParameterNames()
    {
        var hints = Hints("def move(point, dx, dy):\n    return point\n\ndx = 1\nmove(5, dx, 3)\nmove(point=1, dx=2, dy=3)\n\ndef one(a):\n    return a\none(1)\n");
        Assert.True(Has(hints, 4, "point:") && Has(hints, 4, "dy:"), string.Join(", ", hints));
        Assert.True(!Has(hints, 4, "dx:"), "an argument named like its parameter needs no hint");
        Assert.True(!hints.Any(h => h.Line == 5 && h.Kind == 2), "keyword arguments say it already");
        Assert.True(!hints.Any(h => h.Line == 9 && h.Kind == 2), "a function of one parameter gets none");
    }

    [Fact]
    public void ReturnAndVariableTypesAreShownWhereTheyDoNotShowAlready()
    {
        var hints = Hints("def name():\n    return 'a'\n\ndef typed() -> int:\n    return 1\n\nclass P:\n    pass\n\nn = name()\nk = 3\np = P()\nn = name()\n");
        Assert.True(Has(hints, 0, "-> str"), string.Join(", ", hints));
        Assert.True(!hints.Any(h => h.Line == 3 && h.Kind == 1), "an annotated return");
        Assert.True(Has(hints, 9, ": str"), string.Join(", ", hints));
        Assert.True(!hints.Any(h => h.Line is 10 or 11 && h.Kind == 1), "a literal and a constructor call show their type");
        Assert.True(!hints.Any(h => h.Line == 12), "only the first assignment of a name");
    }

    [Fact]
    public void ParametrizeValuesAndClassPatternsGetNames()
    {
        var hints = Hints("import pytest\nfrom dataclasses import dataclass\n\n@pytest.mark.parametrize('a, b', [(1, 2), (3, 4)])\ndef test_x(a, b):\n    pass\n\n@dataclass\nclass Pt:\n    x: int\n    y: int\n\ndef f(p):\n    match p:\n        case Pt(0, 1):\n            pass\n");
        Assert.Equal(4, hints.Count(h => h.Line == 3 && h.Kind == 2));
        Assert.True(Has(hints, 3, "a:") && Has(hints, 3, "b:"));
        Assert.True(Has(hints, 14, "x:") && Has(hints, 14, "y:"), string.Join(", ", hints));
    }

    [Fact]
    public void EachLineOfAMethodChainGetsItsType()
    {
        var hints = Hints("label = (\n    ' hi '\n    .strip()\n    .upper()\n)\n");
        Assert.True(Has(hints, 2, " str"), string.Join(", ", hints));
        Assert.True(!Has(hints, 3, " str"), "the last call of the chain shows nothing: nothing follows it");
    }
}
