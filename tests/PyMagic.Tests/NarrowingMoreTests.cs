using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Narrowing by enum members, `type(x) is C`, `in`, `callable`, `hasattr`, `issubclass`, TypeGuard/TypeIs functions and walrus tests.</summary>
public class NarrowingMoreTests
{
    const string Pre = "from typing import Optional, Callable, TypeGuard, TypeIs\nfrom enum import Enum\n\nclass Color(Enum):\n    RED = 1\n    GREEN = 2\n    BLUE = 3\n\n";

    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    [Fact]
    public void EnumMembersNarrowWithIsAndEquals()
    {
        Assert.Equal("Color.RED", T("def f(c: Color):\n    if c is Color.RED:\n        c"));
        Assert.Equal("Color.RED", T("def f(c: Color):\n    if c == Color.RED:\n        c"));
        Same("Color.GREEN | Color.BLUE", T("def f(c: Color):\n    if c is Color.RED:\n        pass\n    else:\n        c"));
        Same("Color.GREEN | Color.BLUE", T("def f(c: Color):\n    if c is not Color.RED:\n        c"));
        Assert.Equal("Color.RED", T("def f(c: Optional[Color]):\n    if c is Color.RED:\n        c"));
        Assert.Equal("Color.BLUE", T("def f(c: Color):\n    if c is Color.RED:\n        return\n    if c is Color.GREEN:\n        return\n    c"));
        Assert.Equal("Color.RED", T("class A:\n    c: Color\n    def f(self):\n        if self.c is Color.RED:\n            self.c"));
    }

    [Fact]
    public void ExactTypeComparisons()
    {
        Assert.Equal("int", T("def f(x: int | str):\n    if type(x) is int:\n        x"));
        Assert.Equal("str", T("def f(x: int | str):\n    if type(x) == str:\n        x"));
        Assert.Equal("int", T("def f(x: object):\n    if type(x) is int:\n        x"));
        Same("int | str", T("def f(x: int | str):\n    if type(x) is not int:\n        pass\n    x"));
    }

    [Fact]
    public void InNarrowing()
    {
        Assert.Equal("str", T("def f(x: Optional[str]):\n    if x in (\"a\", \"b\"):\n        x"));
        Assert.Equal("str", T("def f(x: Optional[str], names: list[str]):\n    if x in names:\n        x"));
        Same("str | None", T("def f(x: Optional[str], names: list[str]):\n    if x not in names:\n        x"));
        Same("Color.RED | Color.GREEN", T("def f(c: Color):\n    if c in (Color.RED, Color.GREEN):\n        c"));
        Same("str | None", T("def f(x: Optional[str], names: list[Optional[str]]):\n    if x in names:\n        x"));
    }

    [Fact]
    public void Callable()
    {
        Assert.Equal("() -> int", T("def f(x: Optional[Callable[[], int]]):\n    if callable(x):\n        x"));
        Assert.Equal("None", T("def f(x: Optional[Callable[[], int]]):\n    if not callable(x):\n        x"));
        Assert.Equal("Unknown", T("def f(x: int | str):\n    if callable(x):\n        x"));
        Assert.Equal("function g", T("def g(): pass\ndef f(x: int):\n    y = g if x else None\n    if callable(y):\n        y"));
    }

    [Fact]
    public void HasAttr()
    {
        Assert.Equal("Unknown", T("class A: pass\ndef f(x: A):\n    if hasattr(x, \"zzz\"):\n        x"));
        Assert.Equal("A", T("class A:\n    zzz = 1\ndef f(x: A):\n    if hasattr(x, \"zzz\"):\n        x"));
        Same("Unknown | A", T("class A: pass\ndef f(x: A):\n    if hasattr(x, \"zzz\"):\n        pass\n    x"));   // the branch that has the attribute is Unknown, joined with the other
    }

    [Fact]
    public void IsSubclass()
    {
        Assert.Equal("type[int]", T("def f(c: type[int] | type[str]):\n    if issubclass(c, int):\n        c"));
        Assert.Equal("type[str]", T("def f(c: type[int] | type[str]):\n    if not issubclass(c, int):\n        c"));
    }

    [Fact]
    public void TypeGuardsAndTypeIs()
    {
        Assert.Equal("str", T("def is_str(x: object) -> TypeGuard[str]: ...\ndef f(v: int | str):\n    if is_str(v):\n        v"));
        Same("int | str", T("def is_str(x: object) -> TypeGuard[str]: ...\ndef f(v: int | str):\n    if not is_str(v):\n        v"));
        Assert.Equal("int", T("def is_int(x: object) -> TypeIs[int]: ...\ndef f(v: int | str):\n    if is_int(v):\n        v"));
        Assert.Equal("str", T("def is_int(x: object) -> TypeIs[int]: ...\ndef f(v: int | str):\n    if not is_int(v):\n        v"));
        Assert.Equal("int", T("def is_int(x: object) -> TypeIs[int]: ...\ndef f(v: object):\n    if is_int(v):\n        v"));
    }

    [Fact]
    public void WalrusInConditions()
    {
        Assert.Equal("int", T("def get() -> Optional[int]: ...\ndef f():\n    if (n := get()) is not None:\n        n"));
        Assert.Equal("int", T("def get() -> Optional[int]: ...\ndef f():\n    if (n := get()):\n        n"));
        Assert.Equal("None", T("def get() -> Optional[int]: ...\ndef f():\n    if (n := get()) is None:\n        n"));
    }

    [Fact]
    public void OtherComparisonsLeaveTheTypeAlone()
    {
        Same("int | None", T("def f(x: Optional[int]):\n    if x == 5:\n        x"));
        Same("int | None", T("def f(x: Optional[int], y: int):\n    if x == y:\n        x"));
        Same("int | str", T("def f(x: int | str):\n    if x is Color.RED:\n        pass\n    x"));
    }

    [Fact]
    public void MatchStatementsNarrowTheSubject()
    {
        Assert.Equal("int", T("def f(x: int | str | None):\n    match x:\n        case int():\n            x"));
        Assert.Equal("str", T("def f(x: int | str | None):\n    match x:\n        case int():\n            pass\n        case str():\n            x"));
        Assert.Equal("None", T("def f(x: int | str | None):\n    match x:\n        case None:\n            x"));
        Same("int | str", T("def f(x: int | str | None):\n    match x:\n        case None:\n            pass\n        case _:\n            x"));
        Assert.Equal("str", T("def f(x: int | str | None):\n    match x:\n        case int() | None:\n            return\n    x"));
        Assert.Equal("int", T("def f(x: int | str):\n    match x:\n        case int() as n:\n            x"));
    }

    [Fact]
    public void MatchOnEnumMembers()
    {
        Assert.Equal("Color.RED", T("def f(c: Color):\n    match c:\n        case Color.RED:\n            c"));
        Same("Color.GREEN | Color.BLUE", T("def f(c: Color):\n    match c:\n        case Color.RED:\n            pass\n        case _:\n            c"));
        Assert.Equal("Color.BLUE", T("def f(c: Color):\n    match c:\n        case Color.RED:\n            return\n        case Color.GREEN:\n            return\n    c"));
    }

    [Fact]
    public void ClassPatternsWithSubPatternsDoNotExcludeTheClass()
    {
        Same("int | P", T("class P:\n    x: int\ndef f(v: int | P):\n    match v:\n        case P(x=0):\n            pass\n        case _:\n            v"));
        Same("int | P", T("class P:\n    x: int\ndef f(v: int | P):\n    match v:\n        case P(x=0):\n            pass\n        case _:\n            v"));
    }
}
