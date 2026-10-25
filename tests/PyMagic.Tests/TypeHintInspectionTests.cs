using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class TypeHintInspectionTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return CodeInspections.Run(m).Where(f => f.Code == "type-hints").Select(f => f.Message).ToList();
    }

    [Fact]
    public void ATypeVarMustBeNamedLikeItsVariable()
    {
        var f = Found("from typing import TypeVar, ParamSpec\n\nT = TypeVar('U')\nP = ParamSpec(\"Q\")\nOK = TypeVar('OK')\nB = TypeVar('B', bound=int)\n");
        Assert.Equal(2, f.Count);
        Assert.Contains("TypeVar()", f[0]);
        Assert.Contains("ParamSpec()", f[1]);
    }

    [Fact]
    public void OptionalTakesOneArgument()
    {
        var f = Found("from typing import Optional\n\nx: Optional[int, str]\ny: Optional[int]\n");
        Assert.Equal(1, f.Count);
        Assert.Contains("Optional", f[0]);
    }

    [Fact]
    public void CallableNeedsAnArgumentList()
    {
        var f = Found("from typing import Callable, ParamSpec\n\nP = ParamSpec('P')\na: Callable[int, str]\nb: Callable[[int], str]\nc: Callable[..., str]\nd: Callable[P, str]\n");
        Assert.Equal(1, f.Count);
        Assert.Contains("Callable", f[0]);
    }

    [Fact]
    public void LiteralTakesLiteralValues()
    {
        var f = Found("from typing import Literal\n\na: Literal[int]\nb: Literal['x', 3, None, True]\nc: Literal[1.5]\n");
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void GenericTakesTypeVariables()
    {
        var f = Found("from typing import Generic, TypeVar\n\nT = TypeVar('T')\n\n\nclass A(Generic[T]):\n    pass\n\n\nclass B(Generic[int]):\n    pass\n");
        Assert.Equal(1, f.Count);
    }

    [Fact]
    public void TheNumberOfTypeArguments()
    {
        var f = Found("from typing import Dict, List\n\na: list[int, str]\nb: dict[str]\nc: Dict[str, int]\nd: List[int, int]\ne: dict[str, list[int]]\nf: tuple[int, str, float]\n");
        Assert.Equal(3, f.Count);
        Assert.True(f.Any(x => x.StartsWith("Too few")));
        Assert.True(f.Count(x => x.StartsWith("Too many")) == 2);
    }

    [Fact]
    public void ANameDefinedInTheFileIsNotTheGenericOfThatName()
    {
        Assert.Equal(0, Found("class Dict:\n    def __class_getitem__(cls, k): ...\n\n\nx = Dict[1]\ny = Dict[1, 2, 3]\n").Count);
        Assert.Equal(0, Found("from mylib import Set\n\nx: Set[int, str]\n").Count);
    }
}
