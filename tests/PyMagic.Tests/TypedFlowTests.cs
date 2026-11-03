using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Calls that never return (NoReturn) and with blocks that swallow exceptions, in narrowing, reachability and return types.</summary>
public class TypedFlowTests
{
    const string Fail = "from typing import NoReturn\n\ndef fail(msg: str) -> NoReturn:\n    raise RuntimeError(msg)\n\n";
    const string Suppress = "class Suppress:\n    def __enter__(self) -> None: ...\n    def __exit__(self, *exc) -> bool: ...\n\n";

    static (Workspace Ws, ModuleInfo M) Load(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    static string T(string src)
    {
        var (ws, m) = Load(src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    /// <summary>The flow findings as "line: message", in order, separated by "; ".</summary>
    static string Flow(string src)
    {
        var (ws, m) = Load(src);
        return string.Join("; ", ws.Evaluator.FlowDiagnostics(m).Select(d => $"{src.Take(d.Start).Count(c => c == '\n') + 1}: {d.Message}"));
    }

    [Fact]
    public void ANameIsNarrowedAfterABranchThatCallsANoReturnFunction()
    {
        Assert.Equal("int", T(Fail + "def f(x: int | None):\n    if x is None:\n        fail('no')\n    x\n"));
        Assert.Equal("str", T(Fail + "def f(x: int | str):\n    if isinstance(x, int):\n        print(x)\n        fail('no')\n    x\n"));
    }

    [Fact]
    public void CodeAfterACallThatNeverReturnsIsUnreachable()
    {
        Assert.Equal("8: Unreachable code", Flow(Fail + "def f():\n    fail('x')\n    print('after')\n"));
        // both branches end: the code after the if is unreachable, and a name bound in neither is not reported
        var both = Flow(Fail + "def f(c):\n    if c:\n        fail('a')\n    else:\n        fail('b')\n    print(v)\n");
        Assert.Equal("11: Unreachable code", both);
    }

    [Fact]
    public void NeverAnnotationsMethodsAndCallableObjectsCountToo()
    {
        const string src = "from typing import Never\n\nclass Checker:\n    def stop(self) -> Never: ...\n    def __call__(self) -> Never: ...\n\n"
            + "def f(c: Checker):\n    c.stop()\n    print('a')\n\ndef g(c: Checker):\n    c()\n    print('b')\n";
        Assert.Equal("9: Unreachable code; 13: Unreachable code", Flow(src));
    }

    [Fact]
    public void AFunctionThatOnlyRaisesIsTheEndWithoutAnAnnotation()
    {
        // as pyright infers it: every path of the body raises
        Assert.Equal("6: Unreachable code", Flow("def boom():\n    raise ValueError()\n\ndef f():\n    boom()\n    print('after')\n"));
        Assert.Equal("int", T("def boom(m):\n    raise ValueError(m)\n\ndef f(x: int | None):\n    if x is None:\n        boom('no')\n    x\n"));
        Assert.Equal("9: Unreachable code", Flow("class C:\n    def stop(self):\n        if self:\n            raise ValueError()\n        raise KeyError()\n\n"
            + "def f(c: C):\n    c.stop()\n    print('after')\n"));
    }

    [Fact]
    public void AFunctionThatRaisesNotImplementedOrIsAbstractIsNotTheEnd()
    {
        Assert.Equal("", Flow("def todo():\n    raise NotImplementedError\n\ndef f():\n    todo()\n    print('after')\n"));
        Assert.Equal("", Flow("from abc import ABC, abstractmethod\n\nclass B(ABC):\n    @abstractmethod\n    def run(self):\n        raise ValueError()\n\n"
            + "def f(b: B):\n    b.run()\n    print('after')\n"));
        // a function that may return, and a generator, are not the end either
        Assert.Equal("", Flow("def maybe(x):\n    if x:\n        return 1\n    raise ValueError()\n\ndef f():\n    maybe(1)\n    print('after')\n"));
        Assert.Equal("", Flow("def gen():\n    raise ValueError()\n    yield 1\n\ndef f():\n    gen()\n    print('after')\n").Replace("3: Unreachable code", ""));
        // an async def annotated NoReturn, or one that only raises, only makes a coroutine when called
        Assert.Equal("", Flow("from typing import NoReturn\n\nasync def stop() -> NoReturn: ...\n\ndef f():\n    stop()\n    print('after')\n"));
        Assert.Equal("", Flow("async def stop():\n    raise ValueError()\n\ndef f():\n    stop()\n    print('after')\n"));
    }

    [Fact]
    public void ANameWithoutAConditionTakesOnlyTheAssignmentsThatPassCallsThatNeverReturn()
    {
        // no condition narrows x: what reaches the use is worked out on the flow the types decide
        Assert.Equal("int", T(Fail + "def f(a):\n    x = None\n    if a:\n        x = 1\n    else:\n        fail('no')\n    x\n"));
        Assert.Equal("int", T("def boom():\n    raise ValueError()\n\ndef f(a):\n    x = 'a'\n    if a:\n        x = 1\n    else:\n        boom()\n    x\n"));
    }

    [Fact]
    public void AModuleLevelReadBeforeTheModuleRebindsABuiltinIsTheBuiltin()
    {
        // requests/compat.py: `builtin_str = str` and two lines later `str = str`
        Assert.Equal("type[str]", T("builtin_str = str\nstr = str\nbuiltin_str\n"));
        var ws = new Workspace();
        ws.AddModule("compat", "builtin_str = str\nstr = str\n");
        var u = ws.AddModule("u", "from compat import builtin_str\n\ndef f(s: str | bytes) -> str:\n    if isinstance(s, builtin_str):\n        out = s\n    else:\n        out = s.decode()\n    return out\n");
        Assert.Equal(0, ws.Evaluator.Inspect(u).Count(d => d.Message.Contains("decode")));
        // a name the module never binds before, and that is no builtin, stays unknown
        Assert.Equal("Unknown", T("x = undefined_thing\nundefined_thing = 1\nx\n"));
    }

    [Fact]
    public void TheEndOfAFunctionFollowsTheTypedFlow()
    {
        var (ws, m) = Load(Fail + "def a(x):\n    if x:\n        return 1\n    fail('no')\n\ndef b(x):\n    if x:\n        return 1\n    print(x)\n\n"
            + "def c():\n    try:\n        return 1\n    finally:\n        return 2\n    print('dead')\n");
        var defs = AstChildren.Descendants(m.Ast).OfType<FunctionDef>().ToDictionary(f => f.Name);
        Assert.True(!ws.Evaluator.EndReachable(m, defs["a"]), "a ends in a NoReturn call");
        Assert.True(ws.Evaluator.EndReachable(m, defs["b"]), "b can run off its end");
        Assert.True(!ws.Evaluator.EndReachable(m, defs["c"]), "c returns in its finally block");
    }

    [Fact]
    public void AReturnTypeLeavesOutThePathsThatEndInANoReturnCall()
    {
        Assert.Equal("int", T(Fail + "def g(x):\n    if x:\n        return len(x)\n    fail('empty')\n\ng('a')\n"));
    }

    [Fact]
    public void TheCodeAfterAWithBlockWhoseExitReturnsBoolIsReachable()
    {
        Assert.Equal("", Flow(Suppress + "def f() -> int:\n    with Suppress():\n        return int('x')\n    return 0\n"));
        // a name bound only in the body may be unbound after it
        Assert.Equal("8: Local variable 'v' might be referenced before assignment",
            Flow(Suppress + "def f():\n    with Suppress():\n        v = int('x')\n    print(v)\n"));
    }

    [Fact]
    public void AWithBlockWhoseExitReturnsNoneOrOptionalBoolLetsExceptionsThrough()
    {
        foreach (var returns in new[] { "None", "bool | None", "Literal[False]" })
        {
            string src = "from typing import Literal\n\nclass Cm:\n    def __enter__(self) -> None: ...\n    def __exit__(self, *exc) -> " + returns + ": ...\n\n"
                + "def f():\n    with Cm():\n        v = 1\n        return\n    print(v)\n";
            Assert.Equal("11: Unreachable code", Flow(src));
        }
    }

    [Fact]
    public void AsyncWithLooksAtAexit()
    {
        const string src = "class Cm:\n    async def __aenter__(self) -> None: ...\n    async def __aexit__(self, *exc) -> bool: ...\n    def __enter__(self) -> None: ...\n    def __exit__(self, *exc) -> None: ...\n\n"
            + "async def f():\n    async with Cm():\n        return\n    print('a')\n\ndef g():\n    with Cm():\n        return\n    print('b')\n";
        Assert.Equal("15: Unreachable code", Flow(src));
    }

    [Fact]
    public void SysExitAndContextlibSuppressFromTypeshed()
    {
        if (Workspace.DefaultTypeshedPath == null) return;
        Assert.Equal("str", T("import sys\n\ndef f(x: str | None):\n    if x is None:\n        sys.exit(1)\n    x\n"));
        var found = Flow("import contextlib, os, sys\nfrom typing import assert_never\n\n"
            + "def a():\n    sys.exit()\n    print(1)\n\ndef b():\n    os._exit(1)\n    print(2)\n\ndef c(x: int):\n    assert_never(x)\n    print(3)\n\n"
            + "def d():\n    with contextlib.suppress(ValueError):\n        return int('x')\n    print(4)\n\ndef e():\n    with open('f') as fh:\n        return fh\n    print(5)\n");
        Assert.Equal("6: Unreachable code; 10: Unreachable code; 14: Unreachable code; 24: Unreachable code", found);
    }
}
