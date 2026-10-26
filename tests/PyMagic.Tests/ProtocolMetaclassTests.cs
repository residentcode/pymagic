using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A Protocol class, and every class below one (Iterable, Mapping, ...), has ABCMeta as its metaclass, so register and the like are found on the class object.</summary>
public class ProtocolMetaclassTests
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
    public void AUserProtocolHasRegister()
    {
        Assert.Equal(0, Found("from typing import Protocol\n\n\nclass P(Protocol):\n    def run(self) -> None: ...\n\n\nclass C:\n    def run(self) -> None: ...\n\n\nP.register(C)\n").Count);
    }

    [Fact]
    public void AGenericProtocolHasRegister()
    {
        Assert.Equal(0, Found("from typing import Protocol, TypeVar\n\nT = TypeVar('T')\n\n\nclass P(Protocol[T]):\n    def get(self) -> T: ...\n\n\nclass C:\n    pass\n\n\nP.register(C)\n").Count);
    }

    [Fact]
    public void AClassBelowAProtocolHasRegister()
    {
        Assert.Equal(0, Found("from typing import Protocol\n\n\nclass P(Protocol):\n    def run(self) -> None: ...\n\n\nclass Base(P):\n    def run(self) -> None: ...\n\n\nclass Sub(Base):\n    pass\n\n\nclass C:\n    pass\n\n\nSub.register(C)\n").Count);
    }

    [Fact]
    public void RegisterAsADecoratorOnMapping()
    {
        Assert.Equal(0, Found("from typing import Mapping\n\n\n@Mapping.register\nclass C:\n    pass\n").Count);
    }

    [Fact]
    public void RegisterOnASubclassOfMapping()
    {
        Assert.Equal(0, Found("from typing import Mapping\n\n\nclass M(Mapping[str, int]):\n    pass\n\n\nclass C:\n    pass\n\n\nM.register(C)\n").Count);
    }

    [Fact]
    public void AnOrdinaryClassHasNoRegister()
    {
        Assert.Equal(1, Found("class A:\n    pass\n\n\nclass C:\n    pass\n\n\nA.register(C)\n").Count);
    }

    [Fact]
    public void AGenericClassThatIsNoProtocolHasNoRegister()
    {
        Assert.Equal(1, Found("from typing import Generic, TypeVar\n\nT = TypeVar('T')\n\n\nclass A(Generic[T]):\n    pass\n\n\nclass C:\n    pass\n\n\nA.register(C)\n").Count);
    }

    [Fact]
    public void AnExplicitMetaclassStillWins()
    {
        const string src = "from abc import ABCMeta\nfrom typing import Protocol\n\n\nclass Meta(ABCMeta):\n    def hello(cls) -> int: ...\n\n\nclass P(Protocol):\n    def run(self) -> None: ...\n\n\nclass Q(P, metaclass=Meta):\n    def run(self) -> None: ...\n\n\n";
        Assert.Equal(0, Found(src + "Q.hello()\nQ.register(int)\n").Count);
        Assert.Equal("int", Last(src + "Q.hello()"));
        Assert.Equal(1, Found(src + "P.hello()\n").Count);
    }

    [Fact]
    public void TheTypeOfAProtocolClassIsABCMeta()
    {
        Assert.Equal("type[ABCMeta]", Last("from typing import Protocol\n\n\nclass P(Protocol):\n    pass\n\n\ntype(P)"));
        Assert.Equal("type[type]", Last("class A:\n    pass\n\n\ntype(A)"));
    }
}
