using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Functions have __call__, and hasattr narrows functions and callables like it narrows instances.</summary>
public class FunctionCallHasattrTests
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
    public void CallingDunderCallOnAFunctionIsNotReported()
    {
        Assert.Equal(0, Found("def f():\n    pass\n\n\nf.__call__()\n").Count);
    }

    [Fact]
    public void DunderCallOfAFunctionReturnsWhatTheFunctionReturns()
    {
        Assert.Equal("Box", Last("class Box:\n    pass\n\n\ndef make() -> Box:\n    return Box()\n\n\nmake.__call__()"));
    }

    [Fact]
    public void DunderCallOfACallableIsNotReported()
    {
        Assert.Equal(0, Found("from typing import Any, Callable\n\n\ndef g(r: Callable[..., Any]):\n    r.__call__(1)\n").Count);
    }

    [Fact]
    public void HasattrOnAnOptionalCallableAllowsTheAttribute()
    {
        Assert.Equal(0, Found("from typing import Any, Callable, Optional\n\n\ndef g(r: Optional[Callable[..., Any]]):\n    assert r is not None\n    if hasattr(r, 'read_eof'):\n        r.read_eof()\n").Count);
    }

    [Fact]
    public void HasattrOnAFunctionAllowsTheAttribute()
    {
        Assert.Equal(0, Found("def f():\n    pass\n\n\nif hasattr(f, 'cache_clear'):\n    f.cache_clear()\n").Count);
    }

    [Fact]
    public void HasattrOnAUnionOfAClassAndACallableAllowsTheAttribute()
    {
        Assert.Equal(0, Found("from typing import Callable, Union\n\n\nclass Reader:\n    def read(self) -> int:\n        return 1\n\n\ndef g(r: Union[Reader, Callable[[], int]]):\n    if hasattr(r, 'close'):\n        r.close()\n").Count);
    }

    [Fact]
    public void AnAttributeTheUnionLacksIsStillReportedWithoutHasattr()
    {
        Assert.True(Found("from typing import Callable, Union\n\n\nclass Reader:\n    def read(self) -> int:\n        return 1\n\n\ndef g(r: Union[Reader, Callable[[], int]]):\n    r.close()\n").Count > 0);
    }

    [Fact]
    public void AnInstanceThatHasTheAttributeKeepsItsTypeUnderHasattr()
    {
        Assert.Equal("int", Last("class Reader:\n    def read(self) -> int:\n        return 1\n\n\ndef g(r: Reader):\n    if hasattr(r, 'read'):\n        r.read()"));
    }

    [Fact]
    public void CompletionOnAFunctionOffersDunderCall()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", "def f():\n    pass\n\n\nf\n");
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        Assert.True(ws.Evaluator.MemberNames(ws.Evaluator.TypeOf(m, last.Value)).Any(n => n.Name == "__call__"));
    }
}
