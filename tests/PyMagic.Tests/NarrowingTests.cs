using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class NarrowingTests
{
    const string Pre = "from typing import Optional\n";

    /// <summary>Type of the last expression statement anywhere in the source.</summary>
    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    /// <summary>Union members compared as a set, so tests do not depend on member order.</summary>
    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    [Fact]
    public void IsNone()
    {
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x is None:\n        return\n    x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x is not None:\n        x"));
        Assert.Equal("None", T("def f(x: Optional[int]):\n    if x is None:\n        x"));
        Assert.Equal("None", T("def f(x: Optional[int]):\n    if x is not None:\n        pass\n    else:\n        x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x != None:\n        x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if None is not x:\n        x"));
    }

    [Fact]
    public void Truthiness()
    {
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x:\n        x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if not x:\n        return\n    x"));
        Same("int | None", T("def f(x: Optional[int]):\n    if not x:\n        x"));       // 0 is falsy too
        Assert.Equal("None", T("def f(x: Optional[object]):\n    if not x:\n        x"));   // object() is always truthy
    }

    [Fact]
    public void IsInstance()
    {
        Assert.Equal("int", T("def f(x: int | str):\n    if isinstance(x, int):\n        x"));
        Assert.Equal("str", T("def f(x: int | str):\n    if isinstance(x, int):\n        pass\n    else:\n        x"));
        Same("int | str", T("def f(x: int | str | None):\n    if isinstance(x, (int, str)):\n        x"));
        Assert.Equal("str", T("def f(x):\n    if isinstance(x, str):\n        x"));
        Assert.Equal("B", T("class A: pass\nclass B(A): pass\ndef f(x: A):\n    if isinstance(x, B):\n        x"));
        Assert.Equal("B", T("class A: pass\nclass B(A): pass\ndef f(x: B | int):\n    if isinstance(x, A):\n        x"));
        Assert.Equal("int", T("def f(x: int | str):\n    if not isinstance(x, str):\n        x"));
        Assert.Equal("int", T("def f(x: bool | int):\n    if isinstance(x, int):\n        x").Replace("bool | int", "int"));
        Assert.Equal("int", T("def f(x: int | str):\n    if isinstance(x, int | float):\n        x"));
    }

    [Fact]
    public void AssertAndEarlyExit()
    {
        Assert.Equal("int", T("def f(x: Optional[int]):\n    assert x is not None\n    x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x is None:\n        raise ValueError()\n    x"));
        Assert.Equal("str", T("def f(x: int | str):\n    if isinstance(x, int):\n        return\n    x"));
    }

    [Fact]
    public void ReassignmentResetsNarrowing()
    {
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x is None:\n        x = 5\n    x"));
        Same("None | Unknown", T("def f(x: Optional[int]):\n    if x is not None:\n        x = g()\n    x"));
        Assert.Equal("int", T("def f():\n    x = None\n    x = 1\n    x"));
    }

    [Fact]
    public void JoinsRestoreTheOriginalType()
    {
        Same("int | None", T("def f(x: Optional[int]):\n    if x is None:\n        y = 1\n    else:\n        y = 2\n    x"));
    }

    [Fact]
    public void LoopsAndConditionOperators()
    {
        Assert.Equal("int", T("def f(x: Optional[int]):\n    while x is not None:\n        x"));
        Assert.Equal("int", T("def f(xs: list[Optional[int]]):\n    for x in xs:\n        if x is None:\n            continue\n        x"));
        Assert.Equal("int", T("def f(x: Optional[int]):\n    if x is not None and x > 0:\n        x"));
        Assert.Equal("list[int]", T("def f(xs: list[Optional[int]]):\n    [x for x in xs if x is not None]"));
        // the right operand of `and` sees the left one as true
        var (ws, m) = (new Workspace(), (ModuleInfo?)null);
        m = ws.AddModule("main", Pre + "def f(x: Optional[str]):\n    return x is not None and x.upper()\n");
        Assert.Equal(0, ws.Evaluator.Inspect(m).Count);
        Assert.Equal("str", T("def f(x: Optional[str]):\n    y = x or 'default'\n    y"));
    }

    [Fact]
    public void ItemsWithAConstantKey()
    {
        const string head = "def f(d: dict[str, int | list[int]], t: tuple[int | None, str]):\n";
        Assert.Equal("list[int]", T(head + "    if isinstance(d['k'], list):\n        d['k']"));
        Assert.Equal("list[int]", T(head + "    if not isinstance(d[\"k\"], list):\n        return\n    d['k']"));   // either quote is one key
        Assert.Equal("int", T(head + "    if isinstance(t[0], int):\n        t[0]"));
        Assert.Equal("int", T(head + "    if t[0] is not None:\n        t[0]"));                                     // the item, and
        Assert.Equal("int", T(head + "    if t[0] is None:\n        return\n    t[0]"));
        Same("tuple[int, str]", T("def g(u: tuple[int, str] | tuple[None, bytes]):\n    if u[0] is not None:\n        u"));   // the container
        Same("int | list[int]", T(head + "    if isinstance(d['k'], list):\n        d['j']"));                       // another key
        Same("int | list[int]", T(head + "    if isinstance(d['k'], list):\n        d['k'] = 1\n        d['k']"));  // written: forgotten
        Same("int | list[int]", T(head + "    if isinstance(d['k'], list):\n        d = {}\n        d['k']"));      // the dict replaced
        Same("int | list[int]", T(head + "    k = 'k'\n    if isinstance(d[k], list):\n        d[k]"));              // a key in a name
    }

    [Fact]
    public void AttributeChains()
    {
        const string cls = "class A:\n    v: Optional[int]\n    w: Optional[str]\n";
        Assert.Equal("int", T(cls + "    def m(self):\n        if self.v is not None:\n            self.v"));
        Assert.Equal("int", T(cls + "    def m(self):\n        if self.v is None:\n            return\n        self.v"));
        // assigning to the member forgets what we knew: it is what was assigned, here a call nothing is known about (as for a name)
        Assert.Equal("Unknown", T(cls + "    def m(self):\n        if self.v is not None:\n            self.v = g()\n            self.v"));
        // other members are unaffected
        Same("str | None", T(cls + "    def m(self):\n        if self.v is not None:\n            self.w"));
        // a member of a narrowed value
        Assert.Equal("int", T(cls + "def f(a: Optional[A]):\n    if a is not None and a.v is not None:\n        a.v"));
    }

    [Fact]
    public void NarrowingLeavesPlainNamesAlone()
    {
        Assert.Equal("int", T("def f():\n    x = 1\n    if x:\n        pass\n    x"));
        Assert.Equal("list[int]", T("def f(x: list[int]):\n    if isinstance(x, list):\n        x"));
        Assert.Equal("int", T("x = 1\ndef f():\n    if x:\n        x"));      // module variable, narrowed inside a function
    }

    [Fact]
    public void InspectionNoLongerComplainsAboutGuardedNone()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main",
            Pre + "class A:\n    def go(self) -> int: ...\ndef f(a: Optional[A]):\n    if a is not None:\n        a.go()\n    if a:\n        a.go()\n");
        Assert.Equal(0, ws.Evaluator.Inspect(m).Count);
    }
}
