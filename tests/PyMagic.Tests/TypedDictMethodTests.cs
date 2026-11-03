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

    const string Union = "from typing import TypedDict, Literal, Required\n\n\nclass A(TypedDict, total=False):\n    type: Required[Literal['a']]\n    ret: str\n\n\nclass B(TypedDict, total=False):\n    type: Required[Literal['b']]\n    ret: int\n\n\nclass C(TypedDict, total=False):\n    type: Required[Literal['c']]\n\n\n";

    [Fact]
    public void GetOnAUnionOfTypedDictsGivesEachKeysType()
    {
        // pydantic's json_schema.py: `schema.get('return_schema')` on a union of core schemas was the stubs' object
        Assert.Equal("str | None | int", Last(Union + "def f(s: A | B):\n    s.get('ret')\n"));
        // one without the key may hold it anyway: its value is not known, and nothing is reported
        Assert.Equal("str | None | Unknown", Last(Union + "def f(s: A | C):\n    s.get('ret')\n"));
        Assert.Equal(0, Found(Union + "def need(x: str) -> None: ...\n\n\ndef f(s: A | C) -> None:\n    if (r := s.get('ret')) is not None:\n        need(r)\n").Count);
        // a key none of them has is left to the stubs
        Assert.True(Last(Union + "def f(s: A | B):\n    s.get('nothing')\n").Contains("object"));
    }

    [Fact]
    public void AComparedLiteralKeyNarrowsAUnionOfTypedDicts()
    {
        Assert.Equal("A", Last(Union + "def f(s: A | B | C):\n    if s['type'] == 'a':\n        s\n"));
        Assert.Equal("B | C", Last(Union + "def f(s: A | B | C):\n    if s['type'] != 'a':\n        s\n"));
        Assert.Equal("B", Last(Union + "def f(s: A | B):\n    if 'a' == s['type']:\n        return\n    s\n"));
        // pydantic's types.py: `if s['type'] != 'union': s = union_schema(...)`, then s is the one kind
        Assert.Equal("A", Last(Union + "def f(s: A | B | C):\n    if s['type'] != 'a':\n        s = A(type='a')\n    s\n"));
        // a key whose type is not a literal says nothing
        Assert.Equal("A | B", Last(Union + "def f(s: A | B):\n    if s['ret'] == 'x':\n        s\n"));
    }

    [Fact]
    public void GetIsUnchanged()
    {
        Assert.Equal("Item | None", Last(Head + "def f(m: Meta):\n    m.get('item')\n"));
    }
}
