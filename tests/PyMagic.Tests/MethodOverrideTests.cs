using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class MethodOverrideTests
{
    const string Base = "class B:\n    def f(self, a, b=1):\n        pass\n\n    def g(self, a):\n        pass\n\n\n";

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).Where(x => x.StartsWith("Signature of method")).ToList();
    }

    [Fact]
    public void FewerParametersIsAMismatch()
    {
        var f = Found(Base + "class C(B):\n    def f(self, a):\n        pass\n");
        Assert.Equal(1, f.Count);
        Assert.Contains("'C.f()'", f[0]);
        Assert.Contains("class 'B'", f[0]);
    }

    [Fact]
    public void AnExtraRequiredParameterIsAMismatch()
    {
        Assert.Equal(1, Found(Base + "class C(B):\n    def g(self, a, extra):\n        pass\n").Count);
    }

    [Fact]
    public void AnExtraParameterWithADefaultIsFine()
    {
        Assert.Equal(0, Found(Base + "class C(B):\n    def g(self, a, extra=0):\n        pass\n").Count);
    }

    [Fact]
    public void ARenamedParameterIsAMismatch()
    {
        Assert.Equal(1, Found(Base + "class C(B):\n    def g(self, other):\n        pass\n").Count);
    }

    [Fact]
    public void StarArgumentsAcceptAnything()
    {
        Assert.Equal(0, Found(Base + "class C(B):\n    def f(self, *args, **kwargs):\n        pass\n").Count);
        Assert.Equal(0, Found("class B:\n    def f(self, *args, **kwargs):\n        pass\n\n\nclass C(B):\n    def f(self, a, b, c):\n        pass\n").Count);
    }

    [Fact]
    public void ConstructorsPrivateNamesAndDecoratedMethodsAreSkipped()
    {
        Assert.Equal(0, Found("class B:\n    def __init__(self, a):\n        pass\n\n    def __p(self, a):\n        pass\n\n    @property\n    def v(self):\n        return 1\n\n\nclass C(B):\n    def __init__(self):\n        pass\n\n    def __p(self):\n        pass\n\n    @property\n    def v(self):\n        return 2\n").Count);
    }

    [Fact]
    public void ClassMethodsAreComparedWithoutCls()
    {
        Assert.Equal(0, Found("class B:\n    @classmethod\n    def make(cls, a):\n        pass\n\n\nclass C(B):\n    @classmethod\n    def make(cls, a):\n        pass\n").Count);
        Assert.Equal(1, Found("class B:\n    @classmethod\n    def make(cls, a):\n        pass\n\n\nclass C(B):\n    @classmethod\n    def make(cls):\n        pass\n").Count);
    }

    [Fact]
    public void ASameSignatureAndANearestBaseAreChecked()
    {
        Assert.Equal(0, Found(Base + "class C(B):\n    def f(self, a, b=1):\n        pass\n").Count);
        Assert.Equal(1, Found(Base + "class C(B):\n    def f(self, a, b=1):\n        pass\n\n\nclass D(C):\n    def f(self):\n        pass\n").Count);
    }

    [Fact]
    public void KeywordOnlyParametersOfTheBaseMustStay()
    {
        Assert.Equal(1, Found("class B:\n    def f(self, *, key):\n        pass\n\n\nclass C(B):\n    def f(self):\n        pass\n").Count);
        Assert.Equal(0, Found("class B:\n    def f(self, *, key):\n        pass\n\n\nclass C(B):\n    def f(self, *, key, more=1):\n        pass\n").Count);
    }
}
