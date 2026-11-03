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

    [Fact]
    public void AQuotedAliasIsTheAnnotationItSpells()
    {
        // pydantic_core: `SerSchema: TypeAlias = 'SimpleSerSchema | ... | CoreSchema'`, with names bound further down
        const string src = "from typing import TypeAlias, Union\n\n\nclass A: ...\n\n\nS: TypeAlias = 'A | Later'\nU: TypeAlias = 'Union[A, Later]'\n\n\nclass Later: ...\n\n\n";
        Assert.Equal("A | Later", Last(src + "def f(s: S):\n    s\n"));
        Assert.Equal("A | Later", Last(src + "def f(u: U):\n    u\n"));
    }

    [Fact]
    public void AGenericAliasTakesItsArgumentsForItsTypeVariables()
    {
        Assert.Equal("dict[str, int]", Last("from typing import TypeVar\n\nT = TypeVar('T')\nPair = dict[str, T]\n\n\ndef f(p: Pair[int]):\n    p\n"));
        // typeshed's ctypes: `_CIntLikeField: TypeAlias = _CField[_CIntLikeT, int, _CIntLikeT | int]`
        const string field = "from typing import TypeVar, TypeAlias, Generic\n\nA = TypeVar('A')\nB = TypeVar('B')\nC = TypeVar('C')\n\n\nclass Field(Generic[A, B, C]): ...\n\n\nX = TypeVar('X')\nIntLike: TypeAlias = Field[X, int, X | int]\n\n\n";
        Assert.Equal("Field[bytes, int, bytes | int]", Last(field + "def f(x: IntLike[bytes]):\n    x\n"));
        // the class itself subscripted is as before
        Assert.Equal("Field[str, int, bytes]", Last(field + "def f(x: Field[str, int, bytes]):\n    x\n"));
    }
}
