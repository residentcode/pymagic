using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>isinstance and issubclass with a class that is not known: the true branch is an instance of something unknown, not what it was before.</summary>
public class IsinstanceUnknownClassTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AnUnresolvedClassDoesNotLeaveTheDeclaredType()
    {
        // found in the library scan: pydantic's `d: str | Discriminator` with Discriminator not resolved
        var found = Found("from mystery import Disc\n\n\ndef f(d: str | Disc):\n    if isinstance(d, Disc):\n        d.discriminator\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("Unknown", Last("from mystery import Disc\n\n\ndef f(d: str | int):\n    if isinstance(d, Disc):\n        d\n"));
    }

    [Fact]
    public void AClassReboundByACallIsNotKnown()
    {
        // attrs: `Factory = _add_hash(Factory)`, then `isinstance(default, Factory)` with default=NOTHING
        var found = Found("import enum\n\n\nclass _Nothing(enum.Enum):\n    NOTHING = enum.auto()\n\n\nNOTHING = _Nothing.NOTHING\n\n\ndef _add_hash(cls):\n    return cls\n\n\nclass Factory:\n    def __init__(self, factory):\n        self.factory = factory\n\n\nFactory = _add_hash(Factory)\n\n\ndef d(default=NOTHING):\n    if isinstance(default, Factory):\n        return default.factory\n    return default\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void KnownClassesNextToAnUnknownOneStillCount()
    {
        Assert.Equal("int | Unknown", Last("from mystery import Other\n\n\ndef f(x: int | str):\n    if isinstance(x, (int, Other)):\n        x\n"));
        // the false branch rules out the known class even though the other one is not known
        Assert.Equal("str", Last("from mystery import Other\n\n\ndef f(x: int | str):\n    if not isinstance(x, (int, Other)):\n        x\n"));
        Assert.Equal("int | str", Last("from mystery import Other\n\n\ndef f(x: int | str):\n    if not isinstance(x, Other):\n        x\n"));
    }

    [Fact]
    public void IssubclassWithAnUnknownClass()
    {
        var found = Found("from mystery import Base\n\n\nclass K:\n    pass\n\n\ndef f(c: type[K]):\n    if issubclass(c, Base):\n        c.only_on_base\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AKnownClassNarrowsAsBefore()
    {
        Assert.Equal("int", Last("def f(x: int | str):\n    if isinstance(x, int):\n        x\n"));
        Assert.Equal("str", Last("def f(x: int | str):\n    if not isinstance(x, int):\n        x\n"));
        Assert.Equal(1, Found("class A:\n    def a(self): ...\n\n\nclass B:\n    def b(self): ...\n\n\ndef f(x: A | B):\n    if isinstance(x, A):\n        x.b()\n").Count);
    }
}
