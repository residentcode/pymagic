using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The inspections that compare types (item 115): what they report, and the many shapes of code they must leave alone.</summary>
public class TypeCheckTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<LspDiagnostic> Found(string src, string code)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.Diagnostics(Main).Where(d => d.Code == code).ToList();
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    // ------------------------------------------------------------ reported

    [Fact]
    public void ReturnOfAnotherType()
    {
        Assert.Equal(1, Found(Src("def f() -> int:", "    return 'a'"), "return-type").Count);
    }

    [Fact]
    public void AssignmentOfAnotherType()
    {
        Assert.Equal(1, Found(Src("x: int = 'a'"), "assignment-type").Count);
    }

    [Fact]
    public void NoneAssignedToAnnotatedNameIsOnlyNoted()
    {
        var d = Found(Src("x: int = None"), "assignment-type");
        Assert.Equal(1, d.Count);
        Assert.True(d[0].Severity != Severity.Warning);
    }

    [Fact]
    public void FunctionThatCanEndWithoutReturning()
    {
        Assert.Equal(1, Found(Src("def f(a) -> int:", "    if a:", "        return 1"), "missing-return").Count);
    }

    [Fact]
    public void NotIterable()
    {
        Assert.Equal(1, Found(Src("for i in 5:", "    pass"), "not-iterable").Count);
    }

    [Fact]
    public void NotSubscriptable()
    {
        Assert.Equal(1, Found(Src("x = 5", "y = x[0]"), "not-subscriptable").Count);
    }

    [Fact]
    public void UnsupportedOperand()
    {
        Assert.Equal(1, Found(Src("x = 1 + 'a'"), "unsupported-operand").Count);
    }

    // ------------------------------------------------------------ left alone

    [Fact]
    public void MatchingReturnsAndAssignments()
    {
        Assert.Equal(0, Found(Src("def f() -> int:", "    return 1", "x: str = 'a'", "y: float = 1"), "return-type").Count);
        Assert.Equal(0, Found(Src("x: str = 'a'", "y: float = 1", "z: list[int] = []"), "assignment-type").Count);
    }

    [Fact]
    public void FunctionsThatAlwaysExit()
    {
        Assert.Equal(0, Found(Src("def a(x) -> int:", "    if x:", "        return 1", "    else:", "        return 2"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def b() -> int:", "    raise ValueError()"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def c() -> str:", "    while 1:", "        pass"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def d() -> str:", "    while True:", "        return 'a'"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def e() -> int:", "    ..."), "missing-return").Count);
        Assert.Equal(0, Found(Src("def g() -> None:", "    pass"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def h() -> int:", "    yield 1"), "missing-return").Count);
        // the typed flow: a call that never returns ends the path, a return in finally ends the function
        Assert.Equal(0, Found(Src("from typing import NoReturn", "def fail() -> NoReturn:", "    raise ValueError()", "def i(x) -> int:", "    if x:", "        return 1", "    fail()"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def boom():", "    raise ValueError()", "def j(x) -> int:", "    if x:", "        return 1", "    boom()"), "missing-return").Count);
        Assert.Equal(0, Found(Src("def k() -> int:", "    try:", "        pass", "    finally:", "        return 2", "    print('dead')"), "missing-return").Count);
    }

    [Fact]
    public void UnknownTypesAreNotJudged()
    {
        Assert.Equal(0, Found(Src("import json", "def f() -> int:", "    return json.loads('1')", "def g(a):", "    return a + 1"), "return-type").Count);
        Assert.Equal(0, Found(Src("def f(a: int | None):", "    for i in a:", "        pass"), "not-iterable").Count);
        Assert.Equal(0, Found(Src("def f(a: int | None = None):", "    return a[0]"), "not-subscriptable").Count);
        Assert.Equal(0, Found(Src("def f(a):", "    return 3 * a * ' '"), "unsupported-operand").Count);
    }

    [Fact]
    public void NamesAssignedMoreThanOnceAreNotJudged()
    {
        Assert.Equal(0, Found(Src("x = 5", "x = [1]", "y = x[0]"), "not-subscriptable").Count);
        Assert.Equal(0, Found(Src("x = 1", "x = 'a'", "y = x + 'b'"), "unsupported-operand").Count);
    }

    [Fact]
    public void OperatorsThatWork()
    {
        Assert.Equal(0, Found(Src("a = 3 * 'ab'", "b = 'a' % 5", "c = [1] * 2", "d = 1 + 2.5", "e = 'a' + 'b'"), "unsupported-operand").Count);
    }

    [Fact]
    public void MissingReturnIsFixedInTheAnnotation()
    {
        string src = Src("def f(a) -> int:", "    if a:", "        return 1");
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        var action = svc.CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).First(x => x.Title.StartsWith("Make the return type optional"));
        var e = action.Edit!.Changes[Main].Single();
        var lines = new LineMap(src);
        Assert.Equal(Src("def f(a) -> int | None:", "    if a:", "        return 1"), src[..lines.OffsetOf(e.Range.Start)] + e.NewText + src[lines.OffsetOf(e.Range.End)..]);
    }
}
