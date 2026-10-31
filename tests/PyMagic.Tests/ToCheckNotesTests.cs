using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The README's "To check" notes looked at again (item 198): assignment targets Python refuses, brackets never closed, `__all__` and
/// star imports, an import with an ImportError fallback, a class defined two ways under a condition, overloads chosen by an explicit
/// self, and names a stub does not re-export.
/// </summary>
public class ToCheckNotesTests
{
    const string Root = "file:///c:/proj/";

    static List<LspDiagnostic> Diags(string code, params (string Path, string Text)[] more)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        foreach (var (p, t) in more) svc.Open(Root + p, t, 1);
        svc.Open(Root + "app.py", code, 1);
        return svc.Diagnostics(Root + "app.py").ToList();
    }

    static string Hover(string code, int line, int ch, params (string Path, string Text)[] more)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        foreach (var (p, t) in more) svc.Open(Root + p, t, 1);
        svc.Open(Root + "app.py", code, 1);
        return svc.Hover(Root + "app.py", new Position(line, ch))?.Contents.Value ?? "";
    }

    static string Syntax(string code) => string.Join(" | ", Diags(code).Where(d => d.Code == "syntax-error").Select(d => $"{d.Range.Start.Character}: {d.Message}"));

    [Fact]
    public void TargetsPythonRefusesAreSyntaxErrorsInPythonsWords()
    {
        // each message checked against Python 3.14's compile()
        Assert.Equal("0: cannot assign to literal here. Maybe you meant '==' instead of '='?", Syntax("1 = x\n"));
        Assert.Equal("0: cannot assign to function call here. Maybe you meant '==' instead of '='?", Syntax("f() = 3\n"));
        Assert.Equal("0: cannot assign to expression here. Maybe you meant '==' instead of '='?", Syntax("a + b = 1\n"));
        Assert.Equal("0: 'tuple' is an illegal expression for augmented assignment", Syntax("x, y += 1\n"));
        Assert.Equal("4: cannot delete literal", Syntax("del 1\n"));
        Assert.Equal("4: cannot assign to literal", Syntax("(a, 1) = (2, 3)\n"));
        Assert.Equal("4: cannot assign to literal", Syntax("for 1 in []:\n    pass\n"));
        Assert.Equal("", Syntax("a, *b = c = [1, 2]\nx.y[0] += 1\ndel a, x.y\nfor (i, j) in []:\n    pass\nwith open('f') as (p, q):\n    pass\n"));
    }

    [Fact]
    public void ABracketNeverClosedIsReportedAtTheBracket()
    {
        Assert.Equal("5: '(' was never closed", Syntax("print((1, 2)\nx = 3\n"));
        Assert.Equal("3: closing parenthesis ']' does not match opening parenthesis '('", Syntax("f(1]\n"));
        Assert.Equal("", Syntax("x = f'{a[0]}' + str((1, {2: [3]}))\n"));
    }

    [Fact]
    public void StarImportsFollowAllAndAllMustBeDefined()
    {
        var d = Diags("from helpers import *\nfrom more import *\nprint(a, b, One, Two, Three)\n",
            ("helpers.py", "__all__ = ['a']\na = 1\nb = 2\n"),
            ("more.py", "__all__ = ['One']\n__all__ += ['Two']\nOne = 1\nTwo = 2\nThree = 3\n"));
        Assert.Equal("Unresolved reference 'b' | Unresolved reference 'Three'", string.Join(" | ", d.Where(x => x.Code == "unresolved-reference").Select(x => x.Message)));
        // a star import of a module that is not found may bring anything
        Assert.Equal(0, Diags("from nowhere import *\nprint(anything)\n").Count(x => x.Code == "unresolved-reference"));
        var all = Diags("__all__ = ['gone', 'here']\nhere = 1\n").Single(x => x.Code == "all-undefined");
        Assert.Equal(11, all.Range.Start.Character);
    }

    [Fact]
    public void AnImportWithAFallbackTakesTheFallbacksType()
    {
        var helpers = ("helpers.py", "def make() -> int:\n    return 1\n");
        Assert.Contains("def make() -> int", Hover("try:\n    import nosuchlib as h\nexcept ImportError:\n    import helpers as h\n\nh.make()\n", 5, 2, helpers));
        Assert.True(!Hover("try:\n    import nosuchlib as h\nexcept ImportError:\n    import helpers as h\n\nh.make()\n", 5, 2, helpers).Contains("Unknown"));
    }

    [Fact]
    public void AClassDefinedTwoWaysOnlyMissesWhatNeitherHas()
    {
        var d = Diags("import random\nif random.random() > 0.5:\n    class P:\n        a = 1\nelse:\n    class P:\n        b = 2\n\nP.a\nP.b\nP.c\n").Where(x => x.Code == "missing-attribute").ToList();
        Assert.Equal("Cannot find reference 'c' in 'type[P] | type[P]'", d.Single().Message);
    }

    [Fact]
    public void AnOverloadIsChosenByItsExplicitSelf()
    {
        var boxes = ("boxes.pyi", "from typing import Generic, TypeVar, overload\nT = TypeVar('T')\nclass Box(Generic[T]):\n    @overload\n    def get(self: Box[int]) -> int: ...\n    @overload\n    def get(self: Box[str]) -> str: ...\n");
        Assert.Contains("x: int", Hover("from boxes import Box\n\nx = Box[int]().get()\n", 2, 0, boxes));
        Assert.Contains("x: str", Hover("from boxes import Box\n\nx = Box[str]().get()\n", 2, 0, boxes));
    }

    [Fact]
    public void ANameAStubOnlyImportsForItselfIsNotExported()
    {
        string lib = Path.Combine(Path.GetTempPath(), "pm_stub_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(lib, "pkg"));
        File.WriteAllText(Path.Combine(lib, "pkg", "__init__.pyi"), "from pkg._impl import Thing as Thing\nfrom pkg._impl import Hidden\nfrom pkg._impl import Listed\n__all__ = ['Listed']\n");
        File.WriteAllText(Path.Combine(lib, "pkg", "_impl.pyi"), "class Thing: ...\nclass Hidden: ...\nclass Listed: ...\n");
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.LibraryRoots.Add(lib);
        svc.Open(Root + "app.py", "from pkg import Thing, Hidden, Listed\nprint(Thing, Hidden, Listed)\n", 1);
        var d = svc.Diagnostics(Root + "app.py").Where(x => x.Code == "stub-not-exported").ToList();
        Assert.Equal(23, d.Single().Range.Start.Character);
        Assert.Contains("'Hidden' is not exported from 'pkg'", d[0].Message);
    }
}
