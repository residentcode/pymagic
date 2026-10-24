using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Literal[...] types: values, display, narrowing by comparisons, membership, truthiness and match.</summary>
public class LiteralTests
{
    const string Pre = "from typing import Literal, Optional\nfrom enum import Enum\n\nclass Color(Enum):\n    RED = 1\n    GREEN = 2\n\nMode = Literal[\"r\", \"w\", \"a\"]\n\n";

    static (Workspace Ws, ModuleInfo M) Load(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    static string T(string src)
    {
        var (ws, m) = Load(src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    /// <summary>The type an annotation denotes, for the annotation written in `x: ...`.</summary>
    static PyType Ann(Workspace ws, ModuleInfo m, string annotation)
    {
        var holder = ws.AddModule("holder" + Math.Abs(annotation.GetHashCode()), Pre + "x: " + annotation + "\n");
        return ws.Evaluator.AnnotationType(holder, ((AnnAssignStmt)holder.Ast.Body[^1]).Annotation);
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    // ------------------------------------------------------------ annotations and display

    [Fact]
    public void LiteralAnnotationsKeepTheirValues()
    {
        Assert.Equal("Literal['r', 'w']", T("def f(m: Literal[\"r\", \"w\"]):\n    m"));
        Assert.Equal("Literal[1, 2, 3]", T("def f(n: Literal[1, 2, 3]):\n    n"));
        Assert.Equal("Literal[-1]", T("def f(n: Literal[-1]):\n    n"));
        Assert.Equal("Literal[True]", T("def f(b: Literal[True]):\n    b"));
        Assert.Equal("Literal[b'x']", T("def f(b: Literal[b\"x\"]):\n    b"));
        Assert.Equal("Literal['a'] | None", T("def f(x: Optional[Literal[\"a\"]]):\n    x"));
        Assert.Equal("Literal['a', 'b']", T("def f(x: Literal[Literal[\"a\"], \"b\"]):\n    x"));
        Assert.Equal("Literal['a', 1]", T("def f(x: Literal[\"a\", 1]):\n    x"));
    }

    [Fact]
    public void AliasesAndMembers()
    {
        Assert.Equal("Literal['r', 'w', 'a']", T("def f(m: Mode):\n    m"));
        Assert.Equal("Color.RED", T("def f(c: Literal[Color.RED]):\n    c"));
        Same("Color.RED | Color.GREEN", T("def f(c: Literal[Color.RED, Color.GREEN]):\n    c"));
    }

    [Fact]
    public void ALiteralBehavesLikeItsClass()
    {
        Assert.Equal("str", T("def f(m: Literal[\"r\", \"w\"]):\n    m.upper()"));
        Assert.Equal("str", T("def f(m: Literal[\"r\", \"w\"]):\n    m + \"x\""));
        Assert.Equal("int", T("def f(m: Literal[\"r\", \"w\"]):\n    len(m)"));
        Assert.Equal("int", T("def f(n: Literal[1, 2]):\n    n + 1"));
        Assert.Equal("type[str]", T("def f(m: Literal[\"r\"]):\n    type(m)"));
        Assert.Equal("list[str]", T("def f(m: Literal[\"r\"]):\n    [m.lower()]"));
    }

    [Fact]
    public void ReturnTypesAndFields()
    {
        Assert.Equal("Literal['up', 'down']", T("def direction() -> Literal[\"up\", \"down\"]: ...\ndirection()"));
        Assert.Equal("Literal['a']", T("class C:\n    kind: Literal[\"a\"]\nC().kind"));
        Assert.Equal("str", T("x = \"a\"\nx"));       // an ordinary assignment is still a plain str
    }

    // ------------------------------------------------------------ assignability

    [Fact]
    public void DifferentLiteralsAreNotInterchangeable()
    {
        var (ws, m) = Load("");
        var a = Ann(ws, m, "Literal['a']");
        var b = Ann(ws, m, "Literal['b']");
        var s = Ann(ws, m, "str");
        var e = ws.Evaluator;
        Assert.True(e.IsAssignable(s, a), "a literal goes where its class is wanted");
        Assert.True(e.IsAssignable(a, s), "a plain str is accepted (constants are not typed as literals)");
        Assert.True(e.IsAssignable(a, a), "the same literal");
        Assert.True(!e.IsAssignable(a, b), "Literal['b'] where Literal['a'] is wanted");
        Assert.True(e.IsAssignable(Ann(ws, m, "Literal['a', 'b']"), b), "one of the alternatives");
    }

    // ------------------------------------------------------------ narrowing

    [Fact]
    public void EqualityNarrowsLiterals()
    {
        Assert.Equal("Literal['r']", T("def f(m: Mode):\n    if m == \"r\":\n        m"));
        Same("Literal['w', 'a']", T("def f(m: Mode):\n    if m != \"r\":\n        m"));
        Same("Literal['w', 'a']", T("def f(m: Mode):\n    if m == \"r\":\n        return\n    m"));
        Assert.Equal("Literal['r']", T("def f(m: Mode):\n    if \"r\" == m:\n        m"));
        Assert.Equal("Literal['a']", T("def f(m: Mode):\n    if m == \"r\":\n        return\n    if m == \"w\":\n        return\n    m"));
        Assert.Equal("Literal[2]", T("def f(n: Literal[1, 2, 3]):\n    if n == 2:\n        n"));
        Same("Literal[1, 3]", T("def f(n: Literal[1, 2, 3]):\n    if n != 2:\n        n"));
    }

    [Fact]
    public void EqualityWithOtherTypesLeavesThemAlone()
    {
        Assert.Equal("str", T("def f(m: str):\n    if m == \"r\":\n        m"));
        Same("Literal['a'] | int", T("def f(x: Literal[\"a\"] | int):\n    if x == \"a\":\n        x"));
        Assert.Equal("Literal['a']", T("def f(x: Optional[Literal[\"a\", \"b\"]]):\n    if x == \"a\":\n        x"));
        Same("Literal['b'] | None", T("def f(x: Optional[Literal[\"a\", \"b\"]]):\n    if x != \"a\":\n        x"));
        Same("Literal['r', 'w', 'a']", T("def f(m: Mode, other: str):\n    if m == other:\n        m"));
    }

    [Fact]
    public void MembershipNarrowsLiterals()
    {
        Same("Literal['r', 'w']", T("def f(m: Mode):\n    if m in (\"r\", \"w\"):\n        m"));
        Assert.Equal("Literal['a']", T("def f(m: Mode):\n    if m not in (\"r\", \"w\"):\n        m"));
        Assert.Equal("Literal['a']", T("def f(m: Mode):\n    if m in [\"r\", \"w\"]:\n        return\n    m"));
        Same("Literal[1, 3]", T("def f(n: Literal[1, 2, 3]):\n    if n in {1, 3}:\n        n"));
    }

    [Fact]
    public void BoolsAndTruthiness()
    {
        Assert.Equal("Literal[True]", T("def f(x: bool):\n    if x is True:\n        x"));
        Assert.Equal("Literal[False]", T("def f(x: bool):\n    if x is True:\n        pass\n    else:\n        x"));
        Assert.Equal("Literal[False]", T("def f(x: bool):\n    if x == False:\n        x"));
        Assert.Equal("Literal[1]", T("def f(n: Literal[0, 1]):\n    if n:\n        n"));
        Assert.Equal("Literal[0]", T("def f(n: Literal[0, 1]):\n    if not n:\n        n"));
        Assert.Equal("Literal['a']", T("def f(s: Literal[\"\", \"a\"]):\n    if s:\n        s"));
    }

    [Fact]
    public void MatchOnLiteralValues()
    {
        Assert.Equal("Literal['r']", T("def f(m: Mode):\n    match m:\n        case \"r\":\n            m"));
        Same("Literal['w', 'a']", T("def f(m: Mode):\n    match m:\n        case \"w\" | \"a\":\n            m"));
        Same("Literal['w', 'a']", T("def f(m: Mode):\n    match m:\n        case \"r\":\n            pass\n        case _:\n            m"));
        Assert.Equal("Literal['a']", T("def f(m: Mode):\n    match m:\n        case \"r\" | \"w\":\n            return\n    m"));
    }

    // ------------------------------------------------------------ hover

    static string HoverText(string marked)
    {
        var svc = new LanguageService();
        const string uri = "file:///c:/proj/main.py";
        int at = marked.IndexOf('|');
        string text = marked.Replace("|", "");
        svc.Open(uri, text, 1);
        var h = svc.Hover(uri, new LineMap(text).PositionOf(at));
        return h == null ? "<none>" : h.Contents.Value.Replace("```python\n", "").Replace("\n```", "");
    }

    [Fact]
    public void HoverShowsTheLiteralTypes()
    {
        Assert.Equal("(parameter) mode: Literal['r', 'w']", HoverText("from typing import Literal\ndef f(mode: Literal['r', 'w']):\n    print(mo|de)\n").Replace("mode: Literal['r', 'w']", "mode: Literal['r', 'w']"));
    }
}
