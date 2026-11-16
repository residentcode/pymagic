using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A receiver that may be something unknown has any attribute: no missing attribute is reported on a union with an Unknown member (#8, #17).</summary>
public class UnionWithUnknownTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AConditionalWithAnUnknownBranchIsNotReported()
    {
        var found = Found("class A:\n    pass\n\n\ndef f(c, x):\n    y = A() if c else x\n    y.foo\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void TheTypeItselfIsUnchanged()
    {
        // only the report goes: hover and completion still see both members
        Assert.Equal("A | Unknown", Last("class A:\n    pass\n\n\ndef f(c, x):\n    y = A() if c else x\n    y\n"));
    }

    [Fact]
    public void AnUnannotatedParameterNarrowedByIsinstanceIsNotReported()
    {
        // PyYAML's set_python_instance_state: `if isinstance(state, tuple) ...: ...` then `state.keys()`
        var found = Found("class T:\n    pass\n\n\ndef f(state):\n    if isinstance(state, T):\n        pass\n    state.keys()\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AnUnannotatedParameterComparedWithASentinelIsNotReported()
    {
        // attrs: `if x is NOTHING: ...` leaves `_Nothing.NOTHING | Unknown` after the if
        var found = Found("import enum\n\n\nclass _Nothing(enum.Enum):\n    NOTHING = enum.auto()\n\n\nNOTHING = _Nothing.NOTHING\n\n\ndef f(x):\n    if x is NOTHING:\n        pass\n    x.factory\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AnExplicitAnyInTheUnionIsNotReported()
    {
        var found = Found("from typing import Any\n\n\nclass A:\n    pass\n\n\ndef f(x: A | Any):\n    x.foo\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AUnionOfKnownTypesIsStillReported()
    {
        var found = Found("class A:\n    pass\n\n\nclass B:\n    pass\n\n\ndef f(x: A | B):\n    x.foo\n");
        Assert.True(found.Count == 1 && found[0] == "Cannot find reference 'foo' in 'A | B'", string.Join("; ", found));
    }

    [Fact]
    public void AnAttributeOneMemberLacksFollowsTheUnionRule()
    {
        // p = Empty() before `for p in items`: after the loop p may still be the Empty, which has no text
        string src = "class Empty:\n    pass\n\n\nclass Para:\n    text: str = ''\n\n\ndef f(items: list[Para]):\n    p = Empty()\n    for p in items:\n        p.text\n    p.text\n";
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var strict = ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
        Assert.True(strict.Count == 1 && strict[0] == "Cannot find reference 'text' in 'Empty'", string.Join("; ", strict));
        var lenient = new Workspace();
        lenient.Evaluator.LenientUnions = true;
        var m2 = lenient.AddModule("main", src);
        Assert.Equal("", string.Join("; ", lenient.Evaluator.Inspect(m2, true).Select(d => d.Message)));
    }

    [Fact]
    public void ALoopCarriedVariableIsStillReported()
    {
        // the placeholder for a value that depends on itself is not an Unknown member: it leaves the union
        var found = Found("class A:\n    pass\n\n\nx = A()\nfor i in range(3):\n    x = x\nx.foo\n");
        Assert.True(found.Count == 1 && found[0] == "Cannot find reference 'foo' in 'A'", string.Join("; ", found));
    }
}
