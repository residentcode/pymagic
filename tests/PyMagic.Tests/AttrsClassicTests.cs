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
}
