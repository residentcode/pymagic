using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A call of a project function with unannotated parameters and no declared return gets what its body gives with the arguments' types.</summary>
public class CallSiteInferenceTests
{
    /// <summary>The types of the module's expression statements, in order, separated by "; ".</summary>
    static string Types(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return string.Join("; ", m.Ast.Body.OfType<ExprStmt>().Select(s => ws.Evaluator.TypeOf(m, s.Value).ToString()));
    }

    const string Add = "def add(a, b):\n    return a + b\n\n";

    [Fact]
    public void TheReturnTypeFollowsTheArguments()
    {
        Assert.Equal("int; str", Types(Add + "add(1, 2)\nadd('a', 'b')\n"));
    }

    [Fact]
    public void AParameterWithADefaultTakesTheArgumentsTypeWhenOneIsGiven()
    {
        Assert.Equal("float; int", Types("def scale(x, factor=1.0):\n    return x * factor\n\nscale(2)\nscale(2, factor=3)\n"));
    }

    [Fact]
    public void MethodsAndCallsInsideTheBodyAreFollowed()
    {
        const string src = "def pair(x):\n    return [x, x]\n\nclass C:\n    def twice(self, x):\n        return pair(x)\n\nC().twice('a')\npair(1)\n";
        Assert.Equal("list[str]; list[int]", Types(src));
    }

    [Fact]
    public void TheFunctionsOwnBodyKeepsItsTypes()
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Add + "add(1, 2)\n");
        var call = m.Ast.Body.OfType<ExprStmt>().Single().Value;
        Assert.Equal("int", ws.Evaluator.TypeOf(m, call).ToString());
        var ret = AstChildren.Descendants(m.Ast).OfType<ReturnStmt>().Single();
        Assert.Equal("Unknown", ws.Evaluator.TypeOf(m, ret.Value!).ToString());
        Assert.Equal("Unknown", ws.Evaluator.TypeOf(m, ((BinaryExpr)ret.Value!).Left).ToString());
    }

    [Fact]
    public void RecursionIsCutAndAKnownReturnIsKept()
    {
        Assert.Equal("int", Types("def fact(n):\n    if n <= 1:\n        return 1\n    return n * fact(n - 1)\n\nfact(5)\n"));
        // a declared return, or one that is fully known without the arguments, is what it was
        Assert.Equal("list; int", Types("def typed(a) -> list:\n    return a\n\ndef size(a):\n    return len(a)\n\ntyped(1)\nsize('ab')\n"));
    }

    [Fact]
    public void AtMostFourArgumentTypesAreKeptPerFunction()
    {
        Assert.Equal("int; str; bytes; float; Unknown; int",
            Types("def ident(v):\n    return v\n\nident(1)\nident('s')\nident(b'x')\nident(1.5)\nident(None)\nident(2)\n"));
    }

    [Fact]
    public void StarredArgumentsAndDecoratedFunctionsAreLeftAlone()
    {
        const string src = "def deco(f):\n    return f\n\n@deco\ndef wrapped(v):\n    return v\n\ndef ident(v):\n    return v\n\nwrapped(1)\nident(*[1])\n";
        Assert.Equal("Unknown; Unknown", Types(src));
    }
}
