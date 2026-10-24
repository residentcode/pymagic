using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

public class MoreInspectionTests
{
    const string Main = "file:///c:/proj/main.py";

    static LanguageService Open(string src)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(string src, string code) => Open(src).Diagnostics(Main).Where(d => d.Code == code).ToList();

    static bool Clean(string src, string code) => Found(src, code).Count == 0;

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

    static bool Offers(string title, string src) =>
        Open(src).CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).Any(x => x.Title.StartsWith(title));

    // ------------------------------------------------------------ unused parameters

    [Fact]
    public void UnusedParameter()
    {
        var found = Found("def f(a, b):\n    return a\n", "unused-argument");
        Assert.Equal(1, found.Count);
        Assert.Contains("'b'", found[0].Message);
        Assert.True(found[0].Tags!.Contains(DiagnosticTag.Unnecessary), "greyed out");
        Assert.Equal(1, Found("def f(*args, **kw):\n    return kw\n", "unused-argument").Count);
        Assert.Equal(1, Found("def f(a, *, key):\n    return a\n", "unused-argument").Count);
    }

    [Fact]
    public void ParametersThatAreUsedOrExempt()
    {
        Assert.True(Clean("def f(a, b):\n    return a + b\n", "unused-argument"));
        Assert.True(Clean("def f(a, b):\n    return f'{a}{b}'\n", "unused-argument"));
        Assert.True(Clean("def f(a, _b, _):\n    return a\n", "unused-argument"));
        Assert.True(Clean("def f(a):\n    pass\n", "unused-argument"), "a stub");
        Assert.True(Clean("def f(a):\n    '''Doc.'''\n    raise NotImplementedError\n", "unused-argument"), "a stub");
        Assert.True(Clean("class A:\n    def m(self, a):\n        return a\n", "unused-argument"), "self");
        Assert.True(Clean("class A:\n    @classmethod\n    def m(cls, a):\n        return a\n", "unused-argument"), "cls");
        Assert.True(Clean("class B: ...\nclass A(B):\n    def m(self, a, b):\n        return a\n", "unused-argument"), "an override");
        Assert.True(Clean("import functools\n@functools.cache\ndef f(a, b):\n    return a\n", "unused-argument"), "decorated");
        Assert.True(Clean("class A:\n    def __exit__(self, t, v, tb):\n        return False\n", "unused-argument"), "dunder");
        Assert.True(Clean("def f(a, b):\n    def g():\n        return b\n    return a, g\n", "unused-argument"), "used by a closure");
        Assert.True(Clean("def f(a, b):\n    return locals()\n", "unused-argument"));
    }

    [Fact]
    public void RemovingAnUnusedParameterUpdatesTheCalls()
    {
        Assert.Equal("def f(a):\n    return a\n\n\nf(1)\n", Fix("Remove parameter 'b'", "def f(a, b):\n    return a\n\n\nf(1, 2)\n"));
    }

    // ------------------------------------------------------------ naming

    [Fact]
    public void ClassNames()
    {
        Assert.Equal(1, Found("class my_class:\n    pass\n", "naming-class").Count);
        Assert.Equal(1, Found("class myClass:\n    pass\n", "naming-class").Count);
        Assert.True(Clean("class MyClass:\n    pass\nclass HTTPError:\n    pass\nclass _Private:\n    pass\nclass A1:\n    pass\n", "naming-class"));
        Assert.Equal("class MyClass:\n    pass\n\n\nx = MyClass()\n", Fix("Rename to 'MyClass'", "class my_class:\n    pass\n\n\nx = my_class()\n"));
    }

    [Fact]
    public void FunctionNames()
    {
        Assert.Equal(1, Found("def DoThing():\n    pass\n", "naming-function").Count);
        Assert.Equal(1, Found("def doThing():\n    pass\n", "naming-function").Count);
        Assert.True(Clean("def do_thing():\n    pass\ndef _private():\n    pass\ndef __dunder__():\n    pass\ndef f2():\n    pass\n", "naming-function"));
        Assert.True(Clean("class Base: ...\nclass T(Base):\n    def setUp(self):\n        pass\n", "naming-function"), "overrides keep their names");
        Assert.Equal(1, Found("class T:\n    def setUp(self):\n        pass\n", "naming-function").Count);
        Assert.Equal("def do_thing():\n    pass\n\n\ndo_thing()\n", Fix("Rename to 'do_thing'", "def doThing():\n    pass\n\n\ndoThing()\n"));
    }

    [Fact]
    public void ArgumentAndVariableNames()
    {
        Assert.Equal(1, Found("def f(Value):\n    return Value\n", "naming-argument").Count);
        Assert.Equal(1, Found("def f():\n    Total = 1\n    return Total\n", "naming-variable").Count);
        Assert.True(Clean("def f(value, _x):\n    total = 1\n    return value, _x, total\n", "naming-variable"));
        Assert.True(Clean("from collections import namedtuple\ndef f():\n    Point = namedtuple('Point', 'x y')\n    return Point\n", "naming-variable"), "an alias for a class");
        Assert.True(Clean("Total = 1\n", "naming-variable"), "module level is for constants");
        Assert.Equal("def f():\n    total = 1\n    return total\n", Fix("Rename to 'total'", "def f():\n    Total = 1\n    return Total\n"));
        Assert.Equal("def f(my_value):\n    return my_value\n", Fix("Rename to 'my_value'", "def f(myValue):\n    return myValue\n"));
    }

    [Fact]
    public void CaseConversions()
    {
        Assert.Equal("http_server_error", CodeInspections.ToSnakeCase("HTTPServerError"));
        Assert.Equal("do_thing", CodeInspections.ToSnakeCase("doThing"));
        Assert.Equal("total", CodeInspections.ToSnakeCase("Total"));
        Assert.Equal("_private_name", CodeInspections.ToSnakeCase("_privateName"));
        Assert.Equal("value2_x", CodeInspections.ToSnakeCase("Value2X"));
        Assert.Equal("MyClass", CodeInspections.ToCapWords("my_class"));
        Assert.Equal("_MyClass", CodeInspections.ToCapWords("_my_class"));
        Assert.Equal("Http", CodeInspections.ToCapWords("http"));
    }

    // ------------------------------------------------------------ shadowed built-ins

    [Fact]
    public void ShadowedBuiltins()
    {
        Assert.Equal(1, Found("def f(list):\n    return list\n", "shadowed-builtin").Count);
        Assert.Equal(1, Found("id = 5\n", "shadowed-builtin").Count);
        Assert.Equal(1, Found("def input():\n    pass\n", "shadowed-builtin").Count);
        Assert.Equal(1, Found("class type:\n    pass\n", "shadowed-builtin").Count);
        Assert.Equal(1, Found("def f():\n    max = 1\n    return max\n", "shadowed-builtin").Count);
    }

    [Fact]
    public void NamesThatDoNotShadowAnything()
    {
        Assert.True(Clean("class A:\n    id = 1\n    def type(self):\n        pass\n", "shadowed-builtin"), "class members are reached through the class");
        Assert.True(Clean("class A:\n    def f(self):\n        self.id = 1\n", "shadowed-builtin"));
        Assert.True(Clean("exit = 1\ncopyright = 2\n", "shadowed-builtin"), "interactive helpers");
        Assert.True(Clean("input = input\n", "shadowed-builtin"), "a compatibility rebinding");
        Assert.True(Clean("for list in []:\n    pass\n", "shadowed-builtin"), "loop variables are left alone");
        Assert.True(Clean("def f(items):\n    return [id for id in items]\n", "shadowed-builtin"), "comprehension variables are left alone");
    }

    [Fact]
    public void ShadowFixAppendsAnUnderscore()
    {
        Assert.Equal("def f(list_):\n    return list_\n", Fix("Rename to 'list_'", "def f(list):\n    return list\n"));
        Assert.Equal("id_ = 5\nprint(id_)\n", Fix("Rename to 'id_'", "id = 5\nprint(id)\n"));
    }

    // ------------------------------------------------------------ __init__ of the base class

    const string BaseInit = "class Base:\n    def __init__(self, a, b=1):\n        self.a = a\n";

    [Fact]
    public void MissingCallToSuperInit()
    {
        string src = BaseInit + "\n\nclass Child(Base):\n    def __init__(self, a, c):\n        self.c = c\n";
        var found = Found(src, "missing-super-init");
        Assert.Equal(1, found.Count);
        Assert.Contains("'Base'", found[0].Message);
        Assert.Equal(BaseInit + "\n\nclass Child(Base):\n    def __init__(self, a, c):\n        super().__init__(a)\n        self.c = c\n", Fix("Add a call to super().__init__()", src));
    }

    [Fact]
    public void SuperInitGoesAfterTheDocstringAndPassesKeywordOnlyArguments()
    {
        string src = "class Base:\n    def __init__(self, *, k):\n        self.k = k\n\n\nclass Child(Base):\n    def __init__(self, k):\n        \"\"\"Doc.\"\"\"\n        self.x = 1\n";
        Assert.Equal("class Base:\n    def __init__(self, *, k):\n        self.k = k\n\n\nclass Child(Base):\n    def __init__(self, k):\n        \"\"\"Doc.\"\"\"\n        super().__init__(k=k)\n        self.x = 1\n",
            Fix("Add a call to super().__init__()", src));
    }

    [Fact]
    public void SuperInitCallsThatCount()
    {
        Assert.True(Clean(BaseInit + "class C(Base):\n    def __init__(self, a):\n        super().__init__(a)\n", "missing-super-init"));
        Assert.True(Clean(BaseInit + "class C(Base):\n    def __init__(self, a):\n        Base.__init__(self, a)\n", "missing-super-init"));
        Assert.True(Clean(BaseInit + "class C(Base):\n    def __init__(self, a):\n        super(C, self).__init__(a)\n", "missing-super-init"));
        Assert.True(Clean("class Base:\n    pass\nclass C(Base):\n    def __init__(self):\n        self.x = 1\n", "missing-super-init"), "the base has no __init__");
        Assert.True(Clean(BaseInit + "class C(Base):\n    pass\n", "missing-super-init"), "no __init__ of its own");
        Assert.True(Clean("class C:\n    def __init__(self):\n        self.x = 1\n", "missing-super-init"), "no base");
    }

    [Fact]
    public void SuperInitIsFoundThroughTheWholeHierarchy()
    {
        string src = BaseInit + "class Mid(Base):\n    pass\nclass C(Mid):\n    def __init__(self):\n        self.x = 1\n";
        var found = Found(src, "missing-super-init");
        Assert.Equal(1, found.Count);
        Assert.Contains("'Base'", found[0].Message);
    }

    // ------------------------------------------------------------ abstract methods

    const string Shapes =
        "from abc import ABC, abstractmethod\n\n\n" +
        "class Shape(ABC):\n" +
        "    @abstractmethod\n    def area(self): ...\n\n" +
        "    @abstractmethod\n    def name(self, x: int) -> str: ...\n\n\n";

    [Fact]
    public void ClassMissingAbstractMethods()
    {
        var found = Found(Shapes + "class Square(Shape):\n    pass\n", "unimplemented-abstract");
        Assert.Equal(1, found.Count);
        Assert.Contains("area, name", found[0].Message);
        Assert.Contains("'Square'", found[0].Message);
    }

    [Fact]
    public void ImplementingSomeLeavesTheRest()
    {
        var found = Found(Shapes + "class Square(Shape):\n    def area(self):\n        return 1\n", "unimplemented-abstract");
        Assert.Equal(1, found.Count);
        Assert.Contains(": name", found[0].Message);
        Assert.True(!found[0].Message.Contains("area"), found[0].Message);
    }

    [Fact]
    public void ClassesThatAreFine()
    {
        Assert.True(Clean(Shapes + "class Square(Shape):\n    def area(self): ...\n    def name(self, x): ...\n", "unimplemented-abstract"));
        Assert.True(Clean(Shapes + "class Square(Shape):\n    area = None\n    name = None\n", "unimplemented-abstract"), "overridden by assignment");
        Assert.True(Clean(Shapes + "class Mid(Shape, ABC):\n    pass\n", "unimplemented-abstract"), "says ABC again: stays abstract");
        Assert.True(Clean(Shapes + "class Mid(Shape):\n    @abstractmethod\n    def more(self): ...\n", "unimplemented-abstract"), "has abstract methods of its own");
        Assert.True(Clean("from abc import abstractmethod\nclass Plain:\n    @abstractmethod\n    def f(self): ...\nclass Sub(Plain):\n    pass\n", "unimplemented-abstract"), "no ABCMeta, no effect");
        Assert.True(Clean(Shapes, "unimplemented-abstract"));
    }

    [Fact]
    public void MetaclassAbcMetaCounts()
    {
        string src = "import abc\nclass Base(metaclass=abc.ABCMeta):\n    @abc.abstractmethod\n    def run(self): ...\nclass Impl(Base):\n    pass\n";
        Assert.Equal(1, Found(src, "unimplemented-abstract").Count);
    }

    [Fact]
    public void ImplementAbstractMethodsFix()
    {
        string src = Shapes + "class Square(Shape):\n    pass\n";
        string expected = Shapes + "class Square(Shape):\n    pass\n\n    def area(self):\n        raise NotImplementedError\n\n    def name(self, x: int) -> str:\n        raise NotImplementedError\n";
        Assert.Equal(expected, Fix("Implement abstract methods", src));
    }

    [Fact]
    public void ImplementOneAbstractMethodKeepsDecoratorsAndAsync()
    {
        string src = "from abc import ABC, abstractmethod\nclass B(ABC):\n    @property\n    @abstractmethod\n    def size(self) -> int: ...\n    @abstractmethod\n    async def go(self, a, *, k=1): ...\nclass C(B):\n    async def go(self, a, *, k=1):\n        pass\n";
        string fixedText = Fix("Implement abstract method 'size'", src);
        Assert.Contains("    @property\n    def size(self) -> int:\n        raise NotImplementedError\n", fixedText);
        Assert.True(!fixedText[fixedText.IndexOf("class C")..].Contains("abstractmethod"), fixedText);
    }

    [Fact]
    public void NoFixForAOneLineClass()
    {
        Assert.True(!Offers("Implement abstract", Shapes + "class Square(Shape): pass\n"));
        Assert.Equal(1, Found(Shapes + "class Square(Shape): pass\n", "unimplemented-abstract").Count);
    }

    // ------------------------------------------------------------ levels and suppression

    [Fact]
    public void NamingFindingsAreWeak()
    {
        var d = Found("def DoThing():\n    pass\n", "naming-function")[0];
        Assert.Equal(Severity.Information, d.Severity);
    }

    [Fact]
    public void NoqaSilencesTheNewOnesToo()
    {
        Assert.True(Clean("def DoThing():  # noqa\n    pass\n", "naming-function"));
        Assert.True(Clean("def f(list):  # noqa: shadowed-builtin\n    return list\n", "shadowed-builtin"));
        Assert.Equal(1, Found("def f(list):  # noqa: naming-function\n    return list\n", "shadowed-builtin").Count);
        Assert.True(Clean(Shapes + "class Square(Shape):  # noqa\n    pass\n", "unimplemented-abstract"));
    }
}
