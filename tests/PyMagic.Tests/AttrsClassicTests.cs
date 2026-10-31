using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Classic attrs: @attr.s with fields written `x = attr.ib()`, no annotations.</summary>
public class AttrsClassicTests
{
    const string Attr =
        "def s(maybe_cls=None, **kwargs):\n    def wrap(cls):\n        return cls\n    return wrap if maybe_cls is None else wrap(maybe_cls)\n\n\nattrs = s\nattributes = s\n\n\n" +
        "def ib(default=None, validator=None, repr=True, init=True, type=None, converter=None, factory=None, kw_only=False, **kwargs): ...\n\n\nattrib = ib\n";

    static List<string> Problems(string src)
    {
        var ws = new Workspace();
        ws.AddModule("attr", Attr);
        var m = ws.AddModule("main", "import attr\n\n\n" + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    const string Point = "@attr.s\nclass Point:\n    x = attr.ib()\n    y = attr.ib(default=0)\n    secret = attr.ib(init=False, default=1)\n\n\n";

    [Fact]
    public void FieldsWithoutAnnotationsFormTheInit()
    {
        Assert.Equal(0, Problems(Point + "Point(1)\nPoint(1, 2)\nPoint(x=1, y=2)\n").Count);
    }

    [Fact]
    public void ARequiredFieldCannotBeLeftOut()
    {
        var found = Problems(Point + "Point()\n");
        Assert.True(found.Any(m => m.Contains("'x'") && m.Contains("unfilled")), string.Join(" / ", found));
    }

    [Fact]
    public void TooManyArgumentsAndInitFalseFields()
    {
        var found = Problems(Point + "Point(1, 2, 3)\nPoint(1, secret=5)\n");
        Assert.Equal(2, found.Count);
        Assert.True(found.All(m => m.StartsWith("Unexpected argument")), string.Join(" / ", found));
    }

    [Fact]
    public void TheTypeKeywordTypesTheParameter()
    {
        var found = Problems("@attr.s\nclass P:\n    n = attr.ib(type=int)\n\n\nP(1)\nP(\"a\")\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("Expected type 'int'", found[0]);
    }

    [Fact]
    public void ADefaultGivesTheTypeWhenThereIsNone()
    {
        var found = Problems("@attr.s\nclass P:\n    label = attr.ib(default=\"x\")\n\n\nP(\"a\")\nP(3)\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("Expected type 'str'", found[0]);
    }

    [Fact]
    public void BaseClassFieldsComeFirst()
    {
        var found = Problems("@attr.s\nclass A:\n    a = attr.ib()\n\n\n@attr.s\nclass B(A):\n    b = attr.ib()\n\n\nB(1, 2)\nB(1)\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'b'", found[0]);
    }

    [Fact]
    public void AnAttrsAliasAndAttribWork()
    {
        Assert.Equal(0, Problems("@attr.attrs\nclass P:\n    v = attr.attrib()\n\n\nP(1)\n").Count);
    }

    const string WithThese =
        "@attr.s(these={\"x\": attr.ib(), \"y\": attr.ib(default=0, type=int)})\nclass C:\n    z = attr.ib()\n\n" +
        "    def total(self) -> int:\n        return self.x + self.y\n\n\n";

    [Fact]
    public void TheseGivesTheFieldsInsteadOfTheClassBody()
    {
        var found = Problems(WithThese + "C(1)\nC(1, 2)\nC(x=1, y=2)\nC(z=1)\n");
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.Contains("'z'")) && found.Any(m => m.Contains("'x'") && m.Contains("unfilled")), string.Join(" / ", found));
    }

    [Fact]
    public void TheseFieldsAreAttributesOfTheInstances()
    {
        var found = Problems(WithThese + "def f(s: str): ...\n\n\nC(1).x\nf(C(1).y)\nC(1).w\n");
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.Contains("Expected type 'str'")) && found.Any(m => m.Contains("'w'")), string.Join(" / ", found));
    }

    [Fact]
    public void TheseKeepsKwOnlyAndInitFalseAndBaseFieldsComeFirst()
    {
        var found = Problems("@attr.s(these={\"a\": attr.ib(), \"k\": attr.ib(init=False, default=3), \"n\": attr.ib(kw_only=True)})\nclass A:\n    pass\n\n\n" +
            "@attr.s\nclass B(A):\n    b = attr.ib()\n\n\nB(1, 2, n=3)\nB(1, 2, 3)\n");
        Assert.Equal(2, found.Count);
        Assert.True(found.Any(m => m.StartsWith("Unexpected argument")) && found.Any(m => m.Contains("'n'")), string.Join(" / ", found));
    }

    [Fact]
    public void ATheseThatCannotBeReadAcceptsAnyArgumentsAndAttributes()
    {
        var found = Problems("SPEC = {\"a\": attr.ib()}\n\n\n@attr.s(these=SPEC)\nclass E:\n    pass\n\n\n" +
            "@attr.s(these=dict(a=attr.ib()))\nclass F:\n    pass\n\n\nE(1, 2, q=3)\nE().anything\nF(1).a\n");
        Assert.Equal(0, found.Count);
    }
}
