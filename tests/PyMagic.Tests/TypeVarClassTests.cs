using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>type[T] parameters, classes passed where a factory is wanted, and names that stand for a whole overload set.</summary>
public class TypeVarClassTests
{
    const string Head = "from typing import overload, TypeVar, Any, Callable\nT = TypeVar('T')\n";

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void ATypeParameterIsSolvedFromTheClass()
    {
        Assert.Equal("int", Last(Head + "def make(cls: type[T]) -> T: ...\nmake(int)"));
        Assert.Equal("str", Last(Head + "def make(cls: type[T]) -> T: ...\nmake(str)"));
        Assert.Equal("int", Last(Head + "def make(cls: type[T] | None = None) -> T: ...\nmake(cls=int)"));
    }

    [Fact]
    public void ATypeParameterAlsoWorksForUserClasses()
    {
        Assert.Equal("Box", Last(Head + "class Box:\n    pass\n\n\ndef make(cls: type[T]) -> T: ...\nmake(Box)"));
    }

    [Fact]
    public void TypeOfTheSolvedVariableCanComeBack()
    {
        Assert.Equal("type[int]", Last(Head + "def same(value: T) -> type[T]: ...\nsame(3)").Replace("type[int]", "type[int]"));
    }

    [Fact]
    public void AClassIsAFactoryReturningItsInstance()
    {
        Assert.Equal("list", Last(Head + "def build(factory: Callable[[], T]) -> T: ...\nbuild(list)").Split('[')[0]);
        Assert.Equal("Box", Last(Head + "class Box:\n    pass\n\n\ndef build(factory: Callable[[], T] | None = None) -> T: ...\nbuild(factory=Box)"));
    }

    [Fact]
    public void ANameThatIsAnAliasOfAnOverloadSetKeepsEveryOverload()
    {
        string src = Head + "@overload\ndef field(default: None = ...) -> Any: ...\n@overload\ndef field(default: T) -> T: ...\nfield2 = field\n";
        Assert.Equal("int", Last(src + "field2(default=3)"));
        Assert.Equal("str", Last(src + "field(default='x')"));
    }

    [Fact]
    public void TheOverloadWithTheClassIsPicked()
    {
        string src = Head + "@overload\ndef f(type: None = ...) -> Any: ...\n@overload\ndef f(type: type[T] | None = ...) -> T: ...\n";
        Assert.Equal("int", Last(src + "f(type=int)"));
        Assert.Equal("Any", Last(src + "f()"));
    }

    // ------------------------------------------------------------ frozen classes

    static List<string> Problems(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AssigningToAFrozenDataclass()
    {
        var found = Problems("from dataclasses import dataclass\n\n\n@dataclass(frozen=True)\nclass P:\n    x: int\n\n\np = P(1)\np.x = 2\np.x += 1\np.other = 3\n");
        Assert.Equal(3, found.Count(m => m.EndsWith("is frozen")));
        Assert.Contains("'x'", found.First(m => m.EndsWith("is frozen")));
    }

    [Fact]
    public void ADataclassThatIsNotFrozenMayBeAssigned()
    {
        var found = Problems("from dataclasses import dataclass\n\n\n@dataclass\nclass P:\n    x: int\n\n\np = P(1)\np.x = 2\n");
        Assert.Equal(0, found.Count(m => m.EndsWith("is frozen")));
    }

    [Fact]
    public void ASubclassOfAFrozenDataclassIsFrozenToo()
    {
        var found = Problems("from dataclasses import dataclass\n\n\n@dataclass(frozen=True)\nclass A:\n    x: int\n\n\n@dataclass(frozen=True)\nclass B(A):\n    y: int = 0\n\n\nb = B(1)\nb.y = 2\n");
        Assert.Equal(1, found.Count(m => m.EndsWith("is frozen")));
    }
}
