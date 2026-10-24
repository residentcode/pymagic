using PyMagic.Server;

namespace PyMagic.Tests;

public class InspectionTests
{
    const string Main = "file:///c:/proj/main.py";

    static LanguageService Open(string src)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(string src, string code) =>
        Open(src).Diagnostics(Main).Where(d => d.Code == code).ToList();

    static string Apply(string text, WorkspaceEdit edit)
    {
        var lines = new LineMap(text);
        foreach (var e in edit.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    static string Fix(string title, string src)
    {
        var all = Open(src).CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).ToList();
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        return Apply(src, a.Edit!);
    }

    static bool Clean(string src, string code) => Found(src, code).Count == 0;

    // ------------------------------------------------------------ comparisons

    [Fact]
    public void ComparisonWithNoneUsingEquality()
    {
        Assert.Equal(1, Found("x = 1\nif x == None:\n    pass\n", "none-comparison").Count);
        Assert.Equal(1, Found("x = 1\nif None != x:\n    pass\n", "none-comparison").Count);
        Assert.True(Clean("x = 1\nif x is None:\n    pass\n", "none-comparison"));
        Assert.True(Clean("x = 1\nif x == 'None':\n    pass\n", "none-comparison"));
    }

    [Fact]
    public void NoneComparisonFixUsesIs()
    {
        Assert.Equal("x = 1\nif x is None:\n    pass\n", Fix("Replace equality", "x = 1\nif x == None:\n    pass\n"));
        Assert.Equal("x = 1\nif x is not None:\n    pass\n", Fix("Replace equality", "x = 1\nif x != None:\n    pass\n"));
        Assert.Equal("x = 1\nif None is x:\n    pass\n", Fix("Replace equality", "x = 1\nif None == x:\n    pass\n"));
    }

    [Fact]
    public void ChainedComparisonsFixOnlyTheNoneOnes()
    {
        Assert.Equal("a = b = 1\nr = a == b is None\n", Fix("Replace equality", "a = b = 1\nr = a == b == None\n"));
    }

    [Fact]
    public void IsWithALiteral()
    {
        Assert.Equal(1, Found("x = 1\nr = x is 5\n", "is-literal").Count);
        Assert.Equal(1, Found("x = ''\nr = x is not 'a'\n", "is-literal").Count);
        Assert.True(Clean("x = 1\nr = x is None\nq = x is True\n", "is-literal"));
        Assert.Equal("x = 1\nr = x == 5\n", Fix("Replace 'is'", "x = 1\nr = x is 5\n"));
        Assert.Equal("x = 1\nr = x != 5\n", Fix("Replace 'is'", "x = 1\nr = x is not 5\n"));
    }

    // ------------------------------------------------------------ except

    [Fact]
    public void BareExcept()
    {
        string src = "try:\n    pass\nexcept:\n    pass\n";
        Assert.Equal(1, Found(src, "bare-except").Count);
        Assert.Equal("try:\n    pass\nexcept Exception:\n    pass\n", Fix("Replace with 'except Exception:'", src));
        Assert.True(Clean("try:\n    pass\nexcept ValueError:\n    pass\n", "bare-except"));
    }

    // ------------------------------------------------------------ defaults

    [Fact]
    public void MutableDefaultArguments()
    {
        Assert.Equal(1, Found("def f(a=[]): ...\n", "mutable-default").Count);
        Assert.Equal(1, Found("def f(a={}): ...\n", "mutable-default").Count);
        Assert.Equal(1, Found("def f(a=list()): ...\n", "mutable-default").Count);
        Assert.Equal(1, Found("def f(a=[x for x in ()]): ...\n", "mutable-default").Count);
        Assert.True(Clean("def f(a=(), b=None, c=1, d=frozenset()): ...\n", "mutable-default"));
    }

    [Fact]
    public void MutableDefaultFixInsertsAGuard()
    {
        Assert.Equal("def f(a=None):\n    if a is None:\n        a = []\n    return a\n",
            Fix("Use None as the default", "def f(a=[]):\n    return a\n"));
    }

    [Fact]
    public void MutableDefaultFixGoesAfterTheDocstring()
    {
        Assert.Equal("def f(a=None, b=1):\n    \"\"\"Doc.\"\"\"\n    if a is None:\n        a = {}\n    return a\n",
            Fix("Use None as the default", "def f(a={}, b=1):\n    \"\"\"Doc.\"\"\"\n    return a\n"));
    }

    [Fact]
    public void MutableDefaultFixIsNotOfferedForOneLiners()
    {
        var all = Open("def f(a=[]): return a\n").CodeActions(Main, new LineMap("def f(a=[]): return a\n").RangeOf(0, 21));
        Assert.True(!all.Any(a => a.Title.StartsWith("Use None")));
    }

    // ------------------------------------------------------------ assert / self assignment

    [Fact]
    public void AssertOnATupleIsAlwaysTrue()
    {
        string src = "x = 1\nassert (x > 0, 'positive')\n";
        Assert.Equal(1, Found(src, "assert-tuple").Count);
        Assert.Equal("x = 1\nassert x > 0, 'positive'\n", Fix("Remove the parentheses", src));
        Assert.True(Clean("x = 1\nassert x > 0, 'positive'\n", "assert-tuple"));
    }

    [Fact]
    public void SelfAssignment()
    {
        Assert.Equal(1, Found("x = 1\nx = x\n", "self-assignment").Count);
        Assert.Equal(1, Found("class A:\n    def f(self):\n        self.a = self.a\n", "self-assignment").Count);
        Assert.True(Clean("x = 1\ny = x\n", "self-assignment"));
        Assert.True(Clean("BlockingIOError = BlockingIOError\n", "self-assignment"));
        Assert.True(Clean("t = 1\nclass A:\n    t = t\n", "self-assignment"));
        Assert.Equal("x = 1\n", Fix("Remove the assignment", "x = 1\nx = x\n"));
    }

    // ------------------------------------------------------------ methods

    [Fact]
    public void MethodWithoutSelf()
    {
        string src = "class A:\n    def f():\n        pass\n";
        Assert.Equal(1, Found(src, "missing-self").Count);
        Assert.Equal("class A:\n    def f(self):\n        pass\n", Fix("Add 'self'", src));
    }

    [Fact]
    public void MethodsThatNeedNoSelf()
    {
        Assert.True(Clean("class A:\n    @staticmethod\n    def f():\n        pass\n", "missing-self"));
        Assert.True(Clean("class A:\n    def __new__(cls):\n        pass\n    def g(*args):\n        pass\n    def h(self):\n        pass\n", "missing-self"));
        Assert.True(Clean("def f():\n    pass\n", "missing-self"));
    }

    // ------------------------------------------------------------ dict literals

    [Fact]
    public void DuplicateDictKeys()
    {
        Assert.Equal(1, Found("d = {'a': 1, 'a': 2}\n", "duplicate-key").Count);
        Assert.Equal(1, Found("d = {1: 1, 2: 2, 1: 3}\n", "duplicate-key").Count);
        Assert.Equal(1, Found("d = {'a': 1, \"a\": 2}\n", "duplicate-key").Count);
        Assert.True(Clean("d = {'a': 1, 'b': 2, 1: 3, '1': 4}\n", "duplicate-key"));
        Assert.True(Clean("d = {**x, **x}\n", "duplicate-key"));
    }

    // ------------------------------------------------------------ out of place

    [Fact]
    public void ReturnAndYieldOutsideAFunction()
    {
        Assert.Equal(1, Found("return 1\n", "return-outside").Count);
        Assert.Equal(1, Found("class A:\n    return 1\n", "return-outside").Count);
        Assert.Equal(1, Found("yield 1\n", "yield-outside").Count);
        Assert.True(Clean("def f():\n    return 1\n", "return-outside"));
        Assert.True(Clean("def f():\n    yield 1\n", "yield-outside"));
        Assert.True(Clean("def f():\n    class A:\n        def g(self):\n            return 1\n", "return-outside"));
        Assert.True(Clean("f = lambda: (yield)\n", "yield-outside"));
    }

    [Fact]
    public void BreakAndContinueOutsideALoop()
    {
        Assert.Equal(1, Found("break\n", "break-outside").Count);
        Assert.Equal(1, Found("continue\n", "continue-outside").Count);
        Assert.Equal(1, Found("for x in []:\n    def f():\n        break\n", "break-outside").Count);
        Assert.True(Clean("for x in []:\n    if x:\n        break\n    continue\n", "break-outside"));
        Assert.True(Clean("while True:\n    try:\n        continue\n    finally:\n        pass\n", "continue-outside"));
    }

    [Fact]
    public void BreakInTheElseOfALoopBelongsToTheOuterOne()
    {
        Assert.Equal(1, Found("for x in []:\n    pass\nelse:\n    break\n", "break-outside").Count);
        Assert.True(Clean("while True:\n    for x in []:\n        pass\n    else:\n        break\n", "break-outside"));
    }

    // ------------------------------------------------------------ unused variables

    [Fact]
    public void UnusedLocalVariable()
    {
        var found = Found("def f():\n    x = 1\n    return 2\n", "unused-variable");
        Assert.Equal(1, found.Count);
        Assert.Contains("'x'", found[0].Message);
        Assert.True(found[0].Tags!.Contains(DiagnosticTag.Unnecessary), "greyed out");
    }

    [Fact]
    public void VariablesThatAreUsedOrExempt()
    {
        Assert.True(Clean("def f():\n    x = 1\n    return x\n", "unused-variable"));
        Assert.True(Clean("def f():\n    x = 1\n    x += 1\n", "unused-variable"));
        Assert.True(Clean("def f():\n    _ = 1\n    _tmp = 2\n", "unused-variable"));
        Assert.True(Clean("def f():\n    a, b = 1, 2\n", "unused-variable"));
        Assert.True(Clean("def f():\n    for i in range(3):\n        pass\n", "unused-variable"));
        Assert.True(Clean("def f():\n    x = 1\n    def g():\n        return x\n    return g\n", "unused-variable"));
        Assert.True(Clean("def f():\n    global g\n    g = 1\n", "unused-variable"));
        Assert.True(Clean("def f():\n    n = 0\n    def g():\n        nonlocal n\n        n = 1\n    return g\n", "unused-variable"));
        Assert.True(Clean("def f():\n    x = 1\n    return f'{x}'\n", "unused-variable"));
        Assert.True(Clean("x = 1\n", "unused-variable"));
        Assert.True(Clean("class A:\n    x = 1\n", "unused-variable"));
        Assert.True(Clean("def f():\n    x = 1\n    return locals()\n", "unused-variable"));
    }

    [Fact]
    public void UnusedVariableFixKeepsCallsWithEffects()
    {
        Assert.Equal("def f():\n    return 2\n", Fix("Remove unused variable", "def f():\n    x = 1\n    return 2\n"));
        Assert.Equal("def f():\n    g()\n    return 2\n", Fix("Remove unused variable", "def f():\n    x = g()\n    return 2\n"));
        Assert.Equal("def f():\n    pass\n", Fix("Remove unused variable", "def f():\n    x = 1\n"));
    }

    // ------------------------------------------------------------ redeclaration

    [Fact]
    public void RedeclaredFunction()
    {
        string src = "def f():\n    pass\n\n\ndef f():\n    pass\n";
        Assert.Equal(1, Found(src, "redeclared").Count);
        Assert.Equal("def f():\n    pass\n", Fix("Remove the first definition", src).Trim('\n') + "\n");
    }

    [Fact]
    public void RedeclarationsThatAreFine()
    {
        Assert.True(Clean("def f():\n    pass\nf()\ndef f():\n    pass\n", "redeclared"));
        Assert.True(Clean("if True:\n    def f(): ...\nelse:\n    def f(): ...\n", "redeclared"));
        Assert.True(Clean("class A:\n    @property\n    def x(self): ...\n    @x.setter\n    def x(self, v): ...\n", "redeclared"));
        Assert.True(Clean("from typing import overload\n@overload\ndef f(a: int): ...\n@overload\ndef f(a: str): ...\ndef f(a): ...\n", "redeclared"));
    }

    // ------------------------------------------------------------ suppression

    [Fact]
    public void NoqaSilencesFindingsOnItsLine()
    {
        Assert.True(Clean("x = 1\nif x == None:  # noqa\n    pass\n", "none-comparison"));
        Assert.True(Clean("x = 1\nif x == None:  # noqa: none-comparison\n    pass\n", "none-comparison"));
        Assert.True(Clean("x = 1\nif x == None:  # noinspection PyComparisonWithNone\n    pass\n", "none-comparison"));
        Assert.Equal(1, Found("x = 1\nif x == None:  # noqa: bare-except\n    pass\n", "none-comparison").Count);
        Assert.Equal(1, Found("x = 1\nif x == None:  # a noted comment\n    pass\n", "none-comparison").Count);
    }
}
