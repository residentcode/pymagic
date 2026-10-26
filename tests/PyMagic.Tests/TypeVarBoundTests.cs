using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>TypeVar bounds and constraints: an argument outside them does not solve the variable, so an overload that needs them does not match.</summary>
public class TypeVarBoundTests
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

    const string Classes = "from typing import TypeVar, overload\n\n\nclass Base:\n    body: list\n\n\nclass Sub(Base):\n    pass\n\n\nclass Other:\n    pass\n\n\n";

    // ast.parse in typeshed: the first overload takes a node (`_T` bound to AST) and gives it back, the second parses a str
    const string Parse = Classes + "_T = TypeVar(\"_T\", bound=Base)\n\n\n@overload\ndef parse(source: _T) -> _T: ...\n@overload\ndef parse(source: str) -> Base: ...\ndef parse(source):\n    return source\n\n\n";

    [Fact]
    public void AnOverloadWithABoundVariableDoesNotTakeAnArgumentOutsideTheBound()
    {
        // found in the library scan: pydantic's `ast.parse(source).body` was "Cannot find reference 'body' in 'str'"
        var found = Found(Parse + "def f(src: str):\n    parse(src).body\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("Base", Last(Parse + "def f(src: str):\n    parse(src)\n"));
    }

    [Fact]
    public void AnArgumentWithinTheBoundStillSolvesTheVariable()
    {
        Assert.Equal("Sub", Last(Classes + "B = TypeVar(\"B\", bound=Base)\n\n\ndef ident(v: B) -> B:\n    return v\n\n\nident(Sub())\n"));
    }

    [Fact]
    public void AnArgumentOutsideTheBoundIsAMismatch()
    {
        var src = Classes + "B = TypeVar(\"B\", bound=Base)\n\n\ndef ident(v: B) -> B:\n    return v\n\n\nident(Other())\n";
        Assert.Equal("Expected type 'Base', got 'Other' instead", string.Join("; ", Found(src)));
    }

    [Fact]
    public void AQuotedBoundIsReadAsAType()
    {
        var src = Classes + "B = TypeVar(\"B\", bound=\"Base\")\n\n\ndef ident(v: B) -> B:\n    return v\n\n\nident(Other())\n";
        Assert.Equal("Expected type 'Base', got 'Other' instead", string.Join("; ", Found(src)));
    }

    [Fact]
    public void AConstrainedVariableIsSolvedToTheConstraintTheArgumentMatches()
    {
        var pick = Classes + "class Named(Base):\n    pass\n\n\nC = TypeVar(\"C\", Base, Other)\n\n\ndef pick(v: C) -> C:\n    return v\n\n\n";
        Assert.Equal("Base", Last(pick + "pick(Named())\n"));
        Assert.Equal("Other", Last(pick + "pick(Other())\n"));
        Assert.Equal("Base | Other", Last(pick + "def f(x: Sub | Other):\n    pick(x)\n"));
    }

    [Fact]
    public void AnArgumentOutsideTheConstraintsIsAMismatch()
    {
        var src = Classes + "class Third:\n    pass\n\n\nC = TypeVar(\"C\", Base, Other)\n\n\ndef pick(v: C) -> C:\n    return v\n\n\npick(Third())\n";
        Assert.Equal("Expected type 'Base | Other', got 'Third' instead", string.Join("; ", Found(src)));
    }

    [Fact]
    public void AnUnknownArgumentStillMatches()
    {
        var bound = Classes + "from mystery import thing\n\nB = TypeVar(\"B\", bound=Base)\nC = TypeVar(\"C\", Base, Other)\n\n\ndef ident(v: B) -> B:\n    return v\n\n\ndef pick(v: C) -> C:\n    return v\n\n\n";
        var found = Found(bound + "ident(thing)\npick(thing)\nparse_it = ident(thing).anything\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("Unknown", Last(bound + "pick(thing)\n"));
    }

    [Fact]
    public void AnUnresolvedOrNoneBoundSaysNothing()
    {
        var src = Classes + "from mystery import Shape\n\nB = TypeVar(\"B\", bound=Shape)\nN = TypeVar(\"N\", bound=None)\n\n\ndef ident(v: B) -> B:\n    return v\n\n\ndef same(v: N) -> N:\n    return v\n\n\nident(Other())\nsame(Other())\n";
        var found = Found(src);
        Assert.True(found.Count == 0, string.Join("; ", found));
        Assert.Equal("Other", Last(src));
    }

    [Fact]
    public void OrdinaryGenericFunctionsSolveAsBefore()
    {
        var first = Classes + "T = TypeVar(\"T\")\n\n\ndef first(xs: list[T]) -> T:\n    return xs[0]\n\n\n";
        Assert.Equal("Other", Last(first + "first([Other()])\n"));
        var bounded = Classes + "B = TypeVar(\"B\", bound=Base)\n\n\ndef first(xs: list[B]) -> B:\n    return xs[0]\n\n\n";
        Assert.Equal("Sub", Last(bounded + "first([Sub()])\n"));
    }

    [Fact]
    public void ABoundOnAGenericClassChecksItsConstructor()
    {
        var src = Classes + "from typing import Generic\n\nB = TypeVar(\"B\", bound=Base)\n\n\nclass Box(Generic[B]):\n    def __init__(self, item: B):\n        self.item = item\n\n\n";
        Assert.Equal("Box[Sub]", Last(src + "Box(Sub())\n"));
        Assert.Equal("Expected type 'Base', got 'Other' instead", string.Join("; ", Found(src + "Box(Other())\n")));
    }
}
