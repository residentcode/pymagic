using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>`M: TypeAlias = ...` names its value's type, so a parameter annotated with it is checked and picks overloads.</summary>
public class TypeAliasTests
{
    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static string Pick(string alias, string call) =>
        Last("from typing import Literal, TypeAlias, overload\n" + alias + "\n\n\n@overload\ndef f(m: M) -> int: ...\n@overload\ndef f(m: str) -> str: ...\n" + call);

    [Fact]
    public void ALiteralAliasPicksTheRightOverload()
    {
        const string a = "M: TypeAlias = Literal['a', 'b']";
        Assert.Equal("int", Pick(a, "f('a')"));
        Assert.Equal("str", Pick(a, "f('zz')"));
    }

    [Fact]
    public void AliasesJoinedWithABarWork()
    {
        const string a = "A: TypeAlias = Literal['a']\nB: TypeAlias = Literal['b']\nM: TypeAlias = A | B";
        Assert.Equal("int", Pick(a, "f('b')"));
        Assert.Equal("str", Pick(a, "f('zz')"));
    }

    [Fact]
    public void TheUnannotatedFormStillWorks()
    {
        Assert.Equal("str", Pick("M = Literal['a', 'b']", "f('zz')"));
    }

    [Fact]
    public void AClassAlias()
    {
        Assert.Equal("list[int]", Last("from typing import TypeAlias\n\nL: TypeAlias = list[int]\n\n\ndef g() -> L: ...\n\n\ng()"));
    }
}
