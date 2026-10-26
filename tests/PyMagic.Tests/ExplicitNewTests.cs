using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>An explicit <c>X.__new__(cls)</c> makes an instance of cls, not of X (jinja2: `rv = object.__new__(self.__class__)`).</summary>
public class ExplicitNewTests
{
    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void ObjectNewOfAClassIsAnInstanceOfThatClass()
    {
        Assert.Equal("Env", Last("class Env: ...\n\n\nobject.__new__(Env)"));
    }

    [Fact]
    public void ObjectNewOfTheClassOfSelfFindsTheInstanceAttributes()
    {
        Assert.Equal(0, Found("class Env:\n    def __init__(self):\n        self.extensions = {}\n\n    def overlay(self):\n        rv = object.__new__(self.__class__)\n        rv.extensions.update({})\n        return rv\n").Count);
    }

    [Fact]
    public void TheNewOfABaseClassMakesTheSubclassPassed()
    {
        Assert.Equal("B", Last("class A: ...\nclass B(A): ...\n\n\nA.__new__(B)"));
        Assert.Equal("A", Last("class A: ...\n\n\ndef f(c: type[A]):\n    object.__new__(c)"));
    }

    [Fact]
    public void TheClsOfNewIsTheClassObject()
    {
        Assert.Equal("type[P]", Last("class P:\n    def __new__(cls):\n        cls\n        return object.__new__(cls)"));
        Assert.Equal("P", Last("class P:\n    def __new__(cls):\n        return object.__new__(cls)\n\n\nobject.__new__(P)"));
    }

    [Fact]
    public void ObjectNewOfClsInsideNewFindsTheInstanceAttributes()
    {
        Assert.Equal(0, Found("class P:\n    def __new__(cls, a):\n        self = object.__new__(cls)\n        self.b.bit_length()\n        return self\n\n    def __init__(self, a):\n        self.b = 1\n").Count);
    }

    [Fact]
    public void AnArgumentThatIsNotASubclassKeepsTheReceiver()
    {
        Assert.Equal("A", Last("class A: ...\nclass C: ...\n\n\nA.__new__(C)"));
        Assert.Equal("object", Last("def f(k):\n    object.__new__(k)"));
    }

    [Fact]
    public void OrdinaryConstructionIsUnchanged()
    {
        Assert.Equal("Env", Last("class Env:\n    def __init__(self):\n        self.extensions = {}\n\n\nEnv()"));
        Assert.Equal("Sub", Last("class Base:\n    def __new__(cls, *args):\n        return super().__new__(cls)\n\n\nclass Sub(Base):\n    def __new__(cls, x):\n        return super().__new__(cls)\n\n\nSub(1)"));
    }

    [Fact]
    public void SuperNewInsideTheNewOfASubclassIsThatSubclass()
    {
        Assert.Equal("Sub", Last("class Base: ...\n\n\nclass Sub(Base):\n    def __new__(cls):\n        inst = super().__new__(cls)\n        inst\n        return inst"));
    }
}
