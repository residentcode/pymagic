using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Class keywords go to the nearest __init_subclass__ of the bases.</summary>
public class InitSubclassTests
{
    static List<string> Problems(string src, bool types = false)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, types).Select(d => d.Message).ToList();
    }

    const string Plugin = "class Plugin:\n    registry = []\n\n    def __init_subclass__(cls, name, priority=0, **extra):\n        super().__init_subclass__()\n\n\n";

    [Fact]
    public void KeywordsThatTheHookAcceptsAreFine()
    {
        Assert.Equal(0, Problems(Plugin + "class A(Plugin, name=\"a\"):\n    pass\n\n\nclass B(Plugin, name=\"b\", priority=2, anything=1):\n    pass\n").Count);
    }

    [Fact]
    public void ARequiredKeywordCannotBeLeftOut()
    {
        var found = Problems(Plugin + "class A(Plugin):\n    pass\n\n\nclass B(Plugin, priority=1):\n    pass\n");
        Assert.True(found.Any(m => m.Contains("'name'") && m.Contains("unfilled")), string.Join(" / ", found));
    }

    [Fact]
    public void AKeywordTheHookDoesNotTake()
    {
        var found = Problems("class Base:\n    def __init_subclass__(cls, flag=False):\n        pass\n\n\nclass A(Base, flag=True, other=1):\n    pass\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'other'", found[0]);
    }

    [Fact]
    public void TheHookOfAnAncestorIsUsed()
    {
        var found = Problems("class Base:\n    def __init_subclass__(cls, flag=False):\n        pass\n\n\nclass Mid(Base):\n    pass\n\n\nclass A(Mid, flag=True):\n    pass\n\n\nclass B(Mid, bogus=1):\n    pass\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'bogus'", found[0]);
    }

    [Fact]
    public void WithoutAnyHookKeywordsAreNotAccepted()
    {
        var found = Problems("class Base:\n    pass\n\n\nclass A(Base, flag=True):\n    pass\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("'flag'", found[0]);
    }

    [Fact]
    public void AMetaclassTakesTheKeywordsItself()
    {
        Assert.Equal(0, Problems("class Meta(type):\n    def __new__(mcs, name, bases, ns, **kw):\n        return super().__new__(mcs, name, bases, ns)\n\n\nclass A(metaclass=Meta, flag=True):\n    pass\n\n\nclass B(A, other=1):\n    pass\n").Count);
    }

    [Fact]
    public void ABaseWeCannotResolveIsNotGuessed()
    {
        Assert.Equal(0, Problems("from somewhere import Base\n\n\nclass A(Base, flag=True):\n    pass\n").Count);
    }

    const string Typed = "from typing import Literal\n\n\nclass Plugin:\n" +
        "    def __init_subclass__(cls, name: str, priority: int = 0, mode: Literal[\"fast\", \"slow\"] = \"fast\", **kwargs: bool) -> None:\n" +
        "        super().__init_subclass__()\n\n\n" +
        "class A(Plugin, name=\"a\", mode=\"slow\", extra=True):\n    pass\n\n\nclass B(Plugin, name=1):\n    pass\n\n\n" +
        "class C(Plugin, name=\"c\", priority=\"high\"):\n    pass\n\n\nclass D(Plugin, name=\"d\", mode=\"medium\"):\n    pass\n\n\n" +
        "class E(Plugin, name=\"e\", extra=3):\n    pass\n";

    [Fact]
    public void TheKeywordValuesAreCheckedAgainstTheParameterTypes()
    {
        var found = Problems(Typed, types: true);
        Assert.Equal(4, found.Count);
        Assert.True(found.Contains("Expected type 'str', got 'int' instead") && found.Contains("Expected type 'int', got 'str' instead")
            && found.Any(m => m.Contains("Literal") && m.Contains("medium")) && found.Contains("Expected type 'bool', got 'int' instead"), string.Join(" / ", found));
    }

    [Fact]
    public void KeywordTypesAreOnlyReportedWithTheTypeMismatchOption()
    {
        Assert.Equal(0, Problems(Typed).Count);
    }

    [Fact]
    public void AnUnannotatedParameterAcceptsAnything()
    {
        Assert.Equal(0, Problems(Plugin + "class A(Plugin, name=1, priority=\"high\", anything=None):\n    pass\n", types: true).Count);
    }
}
