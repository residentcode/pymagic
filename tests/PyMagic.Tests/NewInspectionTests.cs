using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The inspections of item 77: what they report and what they leave alone.</summary>
public class NewInspectionTests
{
    const string Main = "file:///c:/proj/main.py";

    static LanguageService Open(string src)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(string src, string code) => Open(src).Diagnostics(Main).Where(d => d.Code == code).ToList();

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Fix(string title, string src)
    {
        var all = Open(src).CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).ToList();
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        var lines = new LineMap(src);
        string text = src;
        foreach (var e in a.Edit!.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    // ------------------------------------------------------------ broad exceptions

    [Fact]
    public void BroadExceptionIsReportedUnlessItRaisesAgain()
    {
        Assert.Equal(1, Found(Src("try:", "    pass", "except Exception:", "    pass"), "broad-exception").Count);
        Assert.Equal(1, Found(Src("try:", "    pass", "except (ValueError, BaseException) as e:", "    print(e)"), "broad-exception").Count);
        Assert.Equal(0, Found(Src("try:", "    pass", "except Exception:", "    cleanup()", "    raise"), "broad-exception").Count);
        Assert.Equal(0, Found(Src("try:", "    pass", "except ValueError:", "    pass"), "broad-exception").Count);
        Assert.Equal(0, Found(Src("try:", "    pass", "except:", "    pass"), "broad-exception").Count);   // bare-except's own
    }

    // ------------------------------------------------------------ shadowing

    [Fact]
    public void ANameThatHidesAnOuterOne()
    {
        var found = Found(Src("limit = 3", "", "def f(limit):", "    return limit", "", "def g():", "    limit = 4", "    return limit"), "shadowing-names");
        Assert.Equal(2, found.Count);
        Assert.Contains("'limit'", found[0].Message);
        Assert.Equal(0, Found(Src("def f(limit):", "    return limit"), "shadowing-names").Count);
        Assert.Equal(0, Found(Src("limit = 3", "", "class A:", "    def f(self, other):", "        return other"), "shadowing-names").Count);
        Assert.Equal(0, Found(Src("x = 1", "", "def f():", "    global x", "    x = 2"), "shadowing-names").Count);
        Assert.Equal(0, Found(Src("def f(list):", "    return list"), "shadowing-names").Count);   // a built-in has its own inspection
    }

    [Fact]
    public void AnEnclosingFunctionsNamesAreShadowedToo()
    {
        Assert.Equal(1, Found(Src("def outer(data):", "    def inner(data):", "        return data", "    return inner(data)"), "shadowing-names").Count);
    }

    // ------------------------------------------------------------ first parameter

    [Fact]
    public void TheFirstParameterOfAMethod()
    {
        Assert.Equal(1, Found(Src("class A:", "    def f(this):", "        pass"), "method-parameters").Count);
        Assert.Equal(1, Found(Src("class A:", "    @classmethod", "    def f(self):", "        pass"), "method-parameters").Count);
        Assert.Equal(1, Found(Src("class A:", "    def __new__(self):", "        pass"), "method-parameters").Count);
        Assert.Equal(0, Found(Src("class A:", "    def f(self):", "        pass", "    @classmethod", "    def g(cls):", "        pass", "    @staticmethod", "    def h(x):", "        pass"), "method-parameters").Count);
        Assert.Equal(0, Found(Src("class M(type):", "    def f(cls):", "        pass"), "method-parameters").Count);
        Assert.Equal(0, Found(Src("class A:", "    def f(_self):", "        pass"), "method-parameters").Count);
        Assert.Equal(0, Found(Src("class A:", "    @other", "    def f(x):", "        pass"), "method-parameters").Count);
        Assert.Equal(0, Found(Src("def f(this):", "    pass"), "method-parameters").Count);
    }

    // ------------------------------------------------------------ __slots__

    [Fact]
    public void ASlotThatIsAlsoAClassVariable()
    {
        var found = Found(Src("class A:", "    __slots__ = ('x', 'y')", "    x = 1"), "dunder-slots");
        Assert.Equal(1, found.Count);
        Assert.Contains("'x'", found[0].Message);
        Assert.Equal(0, Found(Src("class A:", "    __slots__ = ('x', 'y')", "    z = 1"), "dunder-slots").Count);
    }

    [Fact]
    public void AnAttributeNoSlotProvides()
    {
        string src = Src("class A:", "    __slots__ = ('x',)", "    def __init__(self):", "        self.x = 1", "        self.y = 2");
        var found = Found(src, "dunder-slots");
        Assert.Equal(1, found.Count);
        Assert.Contains("'y'", found[0].Message);
        Assert.Equal(0, Found(Src("class A:", "    __slots__ = ('x', '__dict__')", "    def __init__(self):", "        self.y = 2"), "dunder-slots").Count);
        Assert.Equal(0, Found(Src("class B: pass", "class A(B):", "    __slots__ = ('x',)", "    def __init__(self):", "        self.y = 2"), "dunder-slots").Count);   // B may have a __dict__
    }

    // ------------------------------------------------------------ from __future__

    [Fact]
    public void FutureImportsBelongAtTheTop()
    {
        Assert.Equal(1, Found(Src("import os", "from __future__ import annotations"), "from-future").Count);
        Assert.Equal(0, Found(Src("\"\"\"Doc.\"\"\"", "from __future__ import annotations", "from __future__ import division", "import os"), "from-future").Count);
        Assert.Equal(1, Found(Src("\"\"\"Doc.\"\"\"", "x = 1", "from __future__ import annotations"), "from-future").Count);
    }

    // ------------------------------------------------------------ indentation

    [Fact]
    public void TabsAndSpacesInOneBlock()
    {
        Assert.Equal(1, Found("def f():\n    a = 1\n\tb = 2\n", "inconsistent-indentation").Count);
        Assert.Equal(0, Found("def f():\n\ta = 1\n\tb = 2\n", "inconsistent-indentation").Count);
        Assert.Equal(0, Found("def f():\n    a = 1\n    b = 2\n", "inconsistent-indentation").Count);
        Assert.Equal(1, Found("def f():\n \t a = 1\n", "inconsistent-indentation").Count);
    }

    // ------------------------------------------------------------ bytes

    [Fact]
    public void NonAsciiInABytesLiteral()
    {
        Assert.Equal(1, Found("x = b'caf\u00e9'\n", "byte-literal").Count);
        Assert.Equal(0, Found("x = 'caf\u00e9'\ny = b'cafe'\n", "byte-literal").Count);
    }

    // ------------------------------------------------------------ properties

    [Fact]
    public void PropertyAccessorsHaveTheirShape()
    {
        Assert.Equal(1, Found(Src("class A:", "    @property", "    def x(self, extra):", "        return 1"), "property-definition").Count);
        Assert.Equal(1, Found(Src("class A:", "    @property", "    def x(self):", "        print(1)"), "property-definition").Count);
        Assert.Equal(1, Found(Src("class A:", "    @property", "    def x(self):", "        return 1", "    @x.setter", "    def x(self):", "        pass"), "property-definition").Count);
        Assert.Equal(1, Found(Src("class A:", "    @property", "    def x(self):", "        return 1", "    @x.setter", "    def x(self, v):", "        return v"), "property-definition").Count);
        Assert.Equal(0, Found(Src("class A:", "    @property", "    def x(self):", "        return 1", "    @x.setter", "    def x(self, v):", "        self._x = v", "    @x.deleter", "    def x(self):", "        del self._x"), "property-definition").Count);
        Assert.Equal(0, Found(Src("class A:", "    @property", "    def x(self):", "        raise NotImplementedError"), "property-definition").Count);
        Assert.Equal(0, Found(Src("class A:", "    @property", "    @abstractmethod", "    def x(self):", "        pass"), "property-definition").Count);
    }

    // ------------------------------------------------------------ __new__ and __init__

    [Fact]
    public void NewAndInitMustAcceptTheSameArguments()
    {
        Assert.Equal(1, Found(Src("class A:", "    def __new__(cls, a):", "        return super().__new__(cls)", "    def __init__(self, a, b):", "        pass"), "init-new-signature").Count);
        Assert.Equal(0, Found(Src("class A:", "    def __new__(cls, a):", "        return super().__new__(cls)", "    def __init__(self, a):", "        pass"), "init-new-signature").Count);
        Assert.Equal(0, Found(Src("class A:", "    def __new__(cls, *args, **kwargs):", "        return super().__new__(cls)", "    def __init__(self, a, b):", "        pass"), "init-new-signature").Count);
        Assert.Equal(0, Found(Src("class A:", "    def __init__(self, a, b):", "        pass"), "init-new-signature").Count);
    }

    // ------------------------------------------------------------ docstrings

    [Fact]
    public void ADocstringNamesAParameterTheFunctionDoesNotHave()
    {
        string sphinx = Src("def f(a, b):", "    \"\"\"Do it.", "", "    :param a: the first", "    :param c: gone", "    \"\"\"", "    return a + b");
        var found = Found(sphinx, "docstring-parameter");
        Assert.Equal(1, found.Count);
        Assert.Contains("'c'", found[0].Message);
        var missing = Found(sphinx, "docstring-missing-parameter");
        Assert.Equal(1, missing.Count);
        Assert.Contains("'b'", missing[0].Message);

        string google = Src("def f(a, b):", "    \"\"\"Do it.", "", "    Args:", "        a (int): the first", "        b: the second", "        z: not there", "", "    Returns:", "        int", "    \"\"\"", "    return a + b");
        var g = Found(google, "docstring-parameter");
        Assert.Equal(1, g.Count);
        Assert.Contains("'z'", g[0].Message);
        Assert.Equal(0, Found(google, "docstring-missing-parameter").Count);
    }

    [Fact]
    public void ADocstringWithoutParametersOrWithAllOfThemIsFine()
    {
        Assert.Equal(0, Found(Src("def f(a, b):", "    \"\"\"Add.\"\"\"", "    return a + b"), "docstring-missing-parameter").Count);
        Assert.Equal(0, Found(Src("class A:", "    def f(self, a):", "        \"\"\":param a: x\"\"\"", "        return a"), "docstring-parameter").Count);
        Assert.Equal(0, Found(Src("def f(a, *args, **kw):", "    \"\"\":param a: x", "    :param args: y", "    :param kw: z\"\"\"", "    return a"), "docstring-parameter").Count);
        Assert.Equal(0, Found(Src("def f(a, *args, **kw):", "    \"\"\":param a: x", "    :param *args: y", "    :param **kw: z\"\"\"", "    return a"), "docstring-parameter").Count);
    }

    // ------------------------------------------------------------ coroutines

    [Fact]
    public void ACoroutineNobodyAwaits()
    {
        string src = Src("async def work():", "    return 1", "", "async def main():", "    work()", "    await work()", "    x = work()", "    return x");
        var found = Found(src, "async-call");
        Assert.Equal(1, found.Count);
        Assert.Contains("'work'", found[0].Message);
        Assert.Equal(Src("async def work():", "    return 1", "", "async def main():", "    await work()", "    await work()", "    x = work()", "    return x"), Fix("Add 'await'", src));
        Assert.Equal(0, Found(Src("def work():", "    return 1", "", "def main():", "    work()"), "async-call").Count);
    }

    [Fact]
    public void NoAwaitIsOfferedWhereItWouldNotRun()
    {
        string src = Src("async def work():", "    return 1", "", "def main():", "    work()");
        Assert.Equal(1, Found(src, "async-call").Count);
        Assert.True(!Open(src).CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).Any(a => a.Title.StartsWith("Add 'await'")), "await is a syntax error in a plain function");
    }

    // ------------------------------------------------------------ @override

    [Fact]
    public void AnOverrideThatOverridesNothing()
    {
        string src = Src("from typing import override", "", "class A:", "    def f(self): ...", "", "class B(A):", "    @override", "    def f(self): ...", "    @override", "    def g(self): ...");
        var found = Found(src, "override-missing");
        Assert.Equal(1, found.Count);
        Assert.Contains("'g'", found[0].Message);
        Assert.Equal(Src("from typing import override", "", "class A:", "    def f(self): ...", "", "class B(A):", "    @override", "    def f(self): ...", "    def g(self): ..."), Fix("Remove the @override", src));
    }

    // ------------------------------------------------------------ equal defaults

    [Fact]
    public void AnArgumentThatRepeatsTheDefault()
    {
        string src = Src("def connect(host, port=80, secure=False, name='a'):", "    return host", "", "connect('h', port=80)", "connect('h', 80)", "connect('h', secure=True, name=\"a\")", "connect('h', 81)");
        var found = Found(src, "argument-equal-default");
        Assert.Equal(3, found.Count);
        Assert.Equal("connect('h')", Fix("Remove the argument", src).Split('\n')[3]);
    }

    [Fact]
    public void DefaultsAreNotReportedWhereTheyCannotBeSure()
    {
        Assert.Equal(0, Found(Src("def f(a, b=1, c=2):", "    return a", "", "f(1, 1, 5)"), "argument-equal-default").Count);   // b is followed by another positional argument
        Assert.Equal(0, Found(Src("def f(a, b=(1, 2)):", "    return a", "", "f(1, b=(1, 2))"), "argument-equal-default").Count);   // only plain constants
        Assert.Equal(0, Found(Src("def f(a, b=1):", "    return a", "", "f(*args, b=1)"), "argument-equal-default").Count);
        Assert.Equal(1, Found(Src("class A:", "    def m(self, n=3):", "        return n", "", "A().m(n=3)"), "argument-equal-default").Count);
        Assert.Equal(0, Found(Src("from typing import overload", "", "@overload", "def f(a: int, mode: str = \"r\") -> int: ...", "@overload", "def f(a: str, mode: bytes = b\"x\") -> str: ...", "def f(a, mode=None): return a", "", "f(1, \"r\")"), "argument-equal-default").Count);   // overloads
    }

    // ------------------------------------------------------------ fixes of the syntax-level ones

    [Fact]
    public void FuturesMoveToTheTop()
    {
        Assert.Equal(Src("\"\"\"Doc.\"\"\"", "from __future__ import annotations", "import os", "x = 1"),
            Fix("Move to the top", Src("\"\"\"Doc.\"\"\"", "import os", "from __future__ import annotations", "x = 1")));
    }

    [Fact]
    public void ParametersAreRenamed()
    {
        Assert.Equal(Src("class A:", "    def f(self, x):", "        return self.y + x"), Fix("Rename to 'self'", Src("class A:", "    def f(this, x):", "        return this.y + x")));
        Assert.Equal(Src("def f(limit_):", "    return limit_", "", "limit = 1"), Fix("Rename to 'limit_'", Src("def f(limit):", "    return limit", "", "limit = 1")));
    }

    [Fact]
    public void DocstringFixes()
    {
        string src = Src("def f(a, b):", "    \"\"\"Do it.", "", "    :param a: the first", "    :type a: int", "    :param c: gone", "    \"\"\"", "    return a + b");
        Assert.Equal(Src("def f(a, b):", "    \"\"\"Do it.", "", "    :param a: the first", "    :type a: int", "    \"\"\"", "    return a + b"), Fix("Remove 'c'", src));
        Assert.Equal(Src("def f(a, b):", "    \"\"\"Do it.", "", "    :param a: the first", "    :type a: int", "    :param c: gone", "    :param b:", "    \"\"\"", "    return a + b"),
            Fix("Document parameter 'b'", src));
    }
}
