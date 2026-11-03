using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// A union where one type is wanted (`Expected type 'str', got 'str | None'`): PyCharm 2026.1 and later report it unless every member
/// fits (its strict unions, PY-24834), so what matters is that the narrowing before the call is followed: an assignment to `self.x`,
/// `x or {}`, a bool that is truthy, `flag and x`, a `TextIO | Any` value given to a declared name. The rule of the earlier PyCharm
/// (one member fitting is enough) is there too, as TypeEvaluator.LenientUnions.
/// </summary>
public class UnionArgumentTests
{
    const string Pre = "from typing import Any, Optional, Dict\n\n\nclass Stream:\n    def write(self, s: str) -> None: ...\n\n\n" +
                       "def need_str(s: str) -> None: ...\ndef need_stream(s: Stream) -> None: ...\ndef need_dict(d: Dict[str, Any]) -> None: ...\n\n\n";

    static List<string> Mismatches(string src, bool lenient = false)
    {
        var ws = new Workspace();
        ws.Evaluator.LenientUnions = lenient;
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AnOptionalArgumentIsReportedAsPyCharmDoesNow()
    {
        var found = Mismatches("def f(s: Optional[str]):\n    need_str(s)\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("got 'str | None'", found[0]);
    }

    [Fact]
    public void TheEarlierPyCharmRuleTakesAUnionWhenOneMemberFits()
    {
        Assert.Equal(0, Mismatches("def f(s: Optional[str], t: str | int):\n    need_str(s)\n    need_str(t)\n", lenient: true).Count);
        // a union of literals is checked member by member either way
        var found = Mismatches("from typing import Literal\n\n\ndef g(m: Literal['r']) -> None: ...\n\n\ndef f(x: Literal['r', 'w']):\n    g(x)\n", lenient: true);
        Assert.Equal(1, found.Count);
    }

    [Fact]
    public void AnAssignmentToAMemberGivesItTheValuesType()
    {
        const string cls = "class C:\n    def __init__(self) -> None:\n        self.name: str | None = None\n\n    def make(self) -> str:\n        return ''\n\n";
        Assert.Equal(0, Mismatches(cls + "    def f(self) -> None:\n        if self.name is None:\n            self.name = 'a'\n        need_str(self.name)\n").Count);
        Assert.Equal(0, Mismatches(cls + "    def g(self) -> None:\n        self.name = self.make()\n        self.other = 1\n        need_str(self.name)\n").Count);
        // a value that does not fit the member, or one assigned before the member is set to None again, does not narrow it
        Assert.Equal(1, Mismatches(cls + "    def h(self) -> None:\n        self.name = 'a'\n        self.name = None\n        need_str(self.name)\n").Count);
    }

    [Fact]
    public void ABoolIsTrueWhereItIsTruthyAndFalseWhereItIsFalsy()
    {
        Assert.Equal("Literal[True] | str", T("def f(flag: bool | str):\n    if not flag:\n        return\n    flag\n"));
        // click's prompt(): a confirmation of True is replaced by a text, a falsy one ends the function
        Assert.Equal(0, Mismatches("def f(confirm: bool | str):\n    if confirm:\n        if confirm is True:\n            confirm = 'again'\n" +
                                   "        need_str(confirm)\n    if not confirm:\n        return\n    need_str(confirm)\n").Count);
    }

    [Fact]
    public void AndGivesTheFalsyPartOfItsLeftSide()
    {
        Assert.Equal("Literal[False] | str | None", T("def f(redirect: bool, loc: str | None):\n    redirect and loc\n"));
        // urllib3's `redirect_location = redirect and response.get_redirect_location()`, then `if not redirect_location: return`
        Assert.Equal(0, Mismatches("def f(redirect: bool, loc: str | None):\n    r = redirect and loc\n    if not r:\n        return\n    need_str(r)\n").Count);
    }

    [Fact]
    public void AUnionWithAnyAssignedToADeclaredNameKeepsItsKnownPart()
    {
        // typeshed's sys.stdin is `TextIO | MaybeNone`, MaybeNone being Any
        Assert.Equal(0, Mismatches("stdin: Stream | Any\n\n\ndef f(s: Stream | None = None):\n    if s is None:\n        s = stdin\n    need_stream(s)\n").Count);
        // starlette's `context = context or {}`
        Assert.Equal(0, Mismatches("def f(context: Optional[Dict[str, Any]] = None):\n    context = context or {}\n    need_dict(context)\n").Count);
    }

    [Fact]
    public void ANameDeclaredAnyStaysAny()
    {
        // jinja2's lexer: `value: t.Any = value_str`, then an int or a str assigned to it
        Assert.Equal(0, Mismatches("def f(text: str, n: int):\n    value: Any = text\n    if n:\n        value = n\n    need_str(value)\n").Count);
        Assert.Equal("Any", T("def f(text: str):\n    value: Any = text\n    value\n"));
    }

    [Fact]
    public void LaterUsesInALongFunctionAreNarrowedToo()
    {
        // yaml's emitter: `if self.encoding:` before each use, many times in one loop; the walk's budget is for each question
        var body = string.Concat(Enumerable.Range(0, 40).Select(i =>
            $"            if end > {i}:\n                data = 'x'\n                self.column += 1\n                if self.encoding:\n                    data.encode(self.encoding)\n                self.stream.write(data)\n"));
        var src = "class E:\n    def __init__(self, s: Stream, e: str | None) -> None:\n        self.encoding = e\n        self.column = 0\n        self.stream = s\n\n" +
                  "    def w(self, text: str) -> None:\n        end = 0\n        while end <= len(text):\n" + body + "            end += 1\n";
        Assert.Equal(0, Mismatches(src).Count);
    }
}
