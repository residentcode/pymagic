using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>setdefault and pop with a literal key on a TypedDict give that key's declared type, not the stubs' object.</summary>
public class TypedDictMethodTests
{
    const string Head = "from typing import TypedDict\n\n\nclass Item:\n    def use(self) -> int: ...\n\n\nclass Meta(TypedDict, total=False):\n    funcs: list[int]\n    item: Item\n\n\n";

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
    public void SetdefaultGivesTheKeysType()
    {
        Assert.Equal("Item", Last(Head + "def f(m: Meta):\n    m.setdefault('item', Item())\n"));
    }

    [Fact]
    public void AMethodOfTheSetdefaultResultIsFound()
    {
        Assert.Equal(0, Found(Head + "def f(m: Meta, extra: list[int]) -> None:\n    m.setdefault('funcs', []).extend(extra)\n    m.setdefault('item', Item()).use()\n").Count);
    }

    [Fact]
    public void PopGivesTheKeysType()
    {
        Assert.Equal("Item", Last(Head + "def f(m: Meta):\n    m.pop('item')\n"));
        Assert.Equal(0, Found(Head + "def f(m: Meta) -> None:\n    m.pop('item').use()\n").Count);
    }

    [Fact]
    public void PopWithADefaultAddsTheDefaultsType()
    {
        Assert.Equal("Item | None", Last(Head + "def f(m: Meta):\n    m.pop('item', None)\n"));
    }

    [Fact]
    public void AnUnknownKeyIsLeftToTheStubs()
    {
        Assert.True(Last(Head + "def f(m: Meta):\n    m.pop('nothing')\n") != "Item");
        Assert.True(Last(Head + "def f(m: Meta, k: str):\n    m.setdefault(k, Item())\n") != "Item");
    }

    [Fact]
    public void GetIsUnchanged()
    {
        Assert.Equal("Item | None", Last(Head + "def f(m: Meta):\n    m.get('item')\n"));
    }
}
