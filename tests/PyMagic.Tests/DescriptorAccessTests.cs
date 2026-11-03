using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A class attribute holding a descriptor reads as what its `__get__` returns, through an instance and through the class.</summary>
public class DescriptorAccessTests
{
    const string Head = "from typing import Any, Generic, TypeVar, overload, Self\n\nT = TypeVar('T')\n\n\n"
        + "class Field(Generic[T]):\n    @overload\n    def __get__(self, instance: None, owner: Any) -> Self: ...\n    @overload\n    def __get__(self, instance: object, owner: Any) -> T: ...\n    def __get__(self, instance, owner): ...\n\n\n"
        + "class Plain:\n    def __get__(self, instance, owner) -> int:\n        return 1\n\n\n"
        + "class Row:\n    a: Field[str]\n    b = Field[bytes]()\n    c = Plain()\n    d: int\n\n    def m(self) -> None: ...\n\n\n";

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void ThroughAnInstanceTheGetResult()
    {
        Assert.Equal("str", Last(Head + "Row().a\n"));
        Assert.Equal("bytes", Last(Head + "Row().b\n"));
        Assert.Equal("int", Last(Head + "Row().c\n"));
    }

    [Fact]
    public void ThroughTheClassTheOverloadForNone()
    {
        Assert.Equal("Field[str]", Last(Head + "Row.a\n"));
        Assert.Equal("int", Last(Head + "Row.c\n"));
    }

    [Fact]
    public void OtherMembersAreAsBefore()
    {
        Assert.Equal("int", Last(Head + "Row().d\n"));
        Assert.Equal("function m", Last(Head + "Row().m\n"));
    }

    [Fact]
    public void AnArgumentReadFromADescriptorIsItsValue()
    {
        Assert.Equal(0, Found(Head + "def need(x: str) -> None: ...\n\n\nneed(Row().a)\n").Count);
        Assert.Equal(1, Found(Head + "def need(x: int) -> None: ...\n\n\nneed(Row().a)\n").Count);
    }

    [Fact]
    public void ACtypesStructureFieldIsItsValue()
    {
        // rich's _win32_console.py: `WindowsCoordinates(row=coord.Y, col=coord.X)` was "Expected type 'int', got '_CField[c_short]'"
        if (Workspace.DefaultTypeshedPath == null) return;
        const string src = "from ctypes import wintypes\n\n\ndef need(x: int) -> None: ...\n\n\ndef f(c: wintypes._COORD) -> None:\n    need(c.X)\n";
        Assert.Equal(0, Found(src).Count);
        Assert.Equal("int", Last(src + "    c.Y\n"));
        Assert.Equal("_CField[c_short, int, c_short | int]", Last(src + "    wintypes._COORD.Y\n"));
    }
}
