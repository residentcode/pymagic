using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A comprehension or generator sees the narrowing of the scope it is written in, as at the point where it starts.</summary>
public class ComprehensionEnclosingNarrowingTests
{
    const string Classes = "class Parameter:\n    name: str\n\n\nclass Option(Parameter):\n    help: str\n\n\nclass Argument(Parameter):\n    nargs: int\n\n\n";

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Classes + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Classes + src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AGeneratorAfterAContinueSeesTheNarrowedName()
    {
        // found in the library scan: click's shell completion, `if not isinstance(param, Option) or ...: continue`
        var found = Found("def f(ps: list[Parameter], incomplete: str):\n    out = []\n    for p in ps:\n        if not isinstance(p, Option) or p.name:\n            continue\n        out.extend(n + p.help for n in ['a'] if n.startswith(incomplete))\n    return out\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void ListSetAndDictComprehensionsSeeTheNarrowedName()
    {
        Assert.Equal("list[Option]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        [p for n in ['a']]\n"));
        Assert.Equal("set[Option]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        {p for n in ['a']}\n"));
        Assert.Equal("dict[str, Option]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        {n: p for n in ['a']}\n"));
        var found = Found("def f(p: Parameter):\n    if isinstance(p, Option):\n        return [p.help for n in ['a']], {n: p.help for n in ['a']}\n    return None\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AMemberNarrowedOutsideIsNarrowedInside()
    {
        var found = Found("class Holder:\n    def __init__(self, x: Parameter):\n        self.x = x\n\n    def m(self):\n        if isinstance(self.x, Option):\n            return [n + self.x.help for n in ['a']]\n        return []\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("list[Option]", Last("class Holder:\n    def __init__(self, x: Parameter):\n        self.x = x\n\n    def m(self):\n        if isinstance(self.x, Option):\n            [self.x for n in ['a']]\n"));
    }

    [Fact]
    public void NestedComprehensionsSeeTheNarrowingOfTheFunction()
    {
        Assert.Equal("list[list[Option]]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        [[p for q in n] for n in ['a']]\n"));
        var found = Found("def f(p: Parameter):\n    if isinstance(p, Option):\n        return [[p.help + q for q in n] for n in ['a']]\n    return []\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AConditionOfTheComprehensionNarrowsFurther()
    {
        Assert.Equal("list[Option]", Last("def f(p: Parameter, names: list[str]):\n    if isinstance(p, (Option, Argument)):\n        [p for n in names if isinstance(p, Option)]\n"));
        Assert.Equal("list[Argument]", Last("def f(p: Parameter, names: list[str]):\n    if isinstance(p, (Option, Argument)):\n        [p for n in names if not isinstance(p, Option)]\n"));
    }

    [Fact]
    public void ANameReassignedInTheFirstIterableIsNoLongerNarrowed()
    {
        // the first iterable runs before the comprehension starts: the walrus there rebinds the name
        Assert.Equal("list[Parameter]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        [p for n in (p := Parameter(),)]\n"));
        var found = Found("def f(p: Parameter):\n    if isinstance(p, Option):\n        return [p.help for n in (p := Parameter(),)]\n    return []\n");
        Assert.Equal(1, found.Count(x => x == "Cannot find reference 'help' in 'Parameter'"));
    }

    [Fact]
    public void ALocalAssignedBeforeTheComprehensionKeepsItsNarrowing()
    {
        Assert.Equal("list[Option]", Last("def make() -> Option | None:\n    return None\n\n\ndef f():\n    o = make()\n    if o is not None:\n        [o for n in ['a']]\n"));
        Assert.Equal("list[Option]", Last("def make() -> Option | None:\n    return None\n\n\ndef f():\n    o = make()\n    if o is None:\n        return\n    [o for n in ['a']]\n"));
    }

    [Fact]
    public void ADeclaredNameAssignedAgainBeforeTheComprehensionHasTheAssignedType()
    {
        Assert.Equal("list[Option]", Last("def f(v: Parameter | str):\n    x: Parameter | str = v\n    x = Option()\n    [x for n in ['a']]\n"));
        var found = Found("def f(v: Parameter | str):\n    x: Parameter | str = v\n    x = Option()\n    return [x.help for n in ['a']]\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AComprehensionInAFinallyBlockIsNarrowed()
    {
        // a finally block is built twice: both copies of the comprehension's start count
        Assert.Equal("list[Option]", Last("def g():\n    pass\n\n\ndef f(p: Parameter):\n    try:\n        g()\n    finally:\n        if isinstance(p, Option):\n            [p for n in ['a']]\n"));
    }

    [Fact]
    public void WithoutNarrowingOutsideNothingChanges()
    {
        Assert.Equal("list[Parameter]", Last("def f(p: Parameter):\n    [p for n in ['a']]\n"));
        Assert.Equal("list[Parameter]", Last("def g(p: Parameter):\n    if isinstance(p, Option):\n        pass\n\n\ndef f(p: Parameter):\n    [p for n in ['a']]\n"));
        var found = Found("def f(p: Parameter):\n    return [p.help for n in ['a']]\n");
        Assert.Equal(1, found.Count(x => x == "Cannot find reference 'help' in 'Parameter'"));
    }

    [Fact]
    public void AfterTheIfTheComprehensionSeesWhatTheNameIsThere()
    {
        Assert.Equal("Option | Parameter", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        pass\n    p\n"));
        Assert.Equal("list[Option | Parameter]", Last("def f(p: Parameter):\n    if isinstance(p, Option):\n        pass\n    [p for n in ['a']]\n"));
    }

    [Fact]
    public void LambdasAndNestedFunctionsDoNotSeeTheNarrowing()
    {
        // they run later, when the name may be bound to something else
        var found = Found("def f(p: Parameter):\n    if isinstance(p, Option):\n        h = lambda: p.help\n\n        def inner():\n            return p.help\n        return h, inner\n    return None\n");
        Assert.Equal(2, found.Count(x => x == "Cannot find reference 'help' in 'Parameter'"));
    }

    [Fact]
    public void AComprehensionInsideALambdaSeesTheLambdasNarrowing()
    {
        var found = Found("def f(ps: list[Parameter]):\n    return [lambda: [p.help for n in ['a']] if isinstance(p, Option) else None for p in ps]\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }
}
