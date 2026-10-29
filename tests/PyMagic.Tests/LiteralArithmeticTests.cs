using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Where a Literal is wanted, arithmetic of literals is the literal it computes, and a name assigned several constants is their union.</summary>
public class LiteralArithmeticTests
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

    const string Size = "from typing import Literal\n\n\ndef size(n: Literal[8, 16, 64]) -> None: ...\n\n\n";
    const string Mode = "from typing import Literal\n\n\ndef mode(m: Literal['r', 'rb', 'w']) -> None: ...\n\n\n";
    const string Flag = "from typing import Literal\n\n\ndef flag(b: Literal[True]) -> None: ...\n\n\n";

    // a literal for each of the first two overloads, any int for the last
    const string Pick = "from typing import Literal, overload\n\n\n"
        + "@overload\ndef pick(a: Literal[1]) -> int: ...\n"
        + "@overload\ndef pick(a: Literal[2]) -> str: ...\n"
        + "@overload\ndef pick(a: int) -> object: ...\n"
        + "def pick(a):\n    return a\n\n\n";

    // open() in typeshed: the text modes first, as literals, and a plain str last
    const string Open = "from typing import Literal, overload\n\n\n"
        + "@overload\ndef op(mode: Literal['r', 'w'] = 'r') -> int: ...\n"
        + "@overload\ndef op(mode: Literal['rb', 'wb']) -> bytes: ...\n"
        + "@overload\ndef op(mode: str) -> str: ...\n"
        + "def op(mode='r'):\n    return mode\n\n\n";

    static void None(List<string> found) => Assert.True(found.Count == 0, string.Join("; ", found));

    [Fact]
    public void ArithmeticOfIntLiteralsIsTheLiteralItComputes()
    {
        None(Found(Size + "KB = 8\nsize(1 << 3)\nsize(KB * 2)\nsize(2 ** 6)\nsize(KB ^ 24)\nsize(-~7)\n"));
        Assert.Equal("Expected type 'Literal[8, 16, 64]', got 'Literal[9]' instead", string.Join("; ", Found(Size + "KB = 8\nsize(KB + 1)\n")));
    }

    [Fact]
    public void FloorDivisionAndModuloRoundAsPythonDoes()
    {
        None(Found(Size + "size(-7 // 2 + 12)\nsize(-7 % 4 + 7)\nsize(7 % -4 + 9)\n"));
    }

    [Fact]
    public void ArithmeticPicksTheOverloadOfTheLiteralItComputes()
    {
        Assert.Equal("int", Last(Pick + "def f():\n    pick(3 - 2)\n"));
        Assert.Equal("str", Last(Pick + "def f():\n    one = 1\n    pick(one + one)\n"));
    }

    [Fact]
    public void DivisionByZeroANegativeShiftOrAnOverflowIsNoLiteral()
    {
        Assert.Equal("object", Last(Pick + "def f():\n    pick(1 // 0)\n"));
        Assert.Equal("object", Last(Pick + "def f():\n    pick(1 << -1)\n"));
        Assert.Equal("object", Last(Pick + "def f():\n    pick(9223372036854775807 + 1 - 9223372036854775807)\n"));
    }

    [Fact]
    public void StringsJoinedWithPlusAreTheLiteralTheySpell()
    {
        None(Found(Mode + "READ = 'r'\nmode(READ + 'b')\n"));
        Assert.Equal("Expected type 'Literal['r', 'rb', 'w']', got 'Literal['rx']' instead", string.Join("; ", Found(Mode + "mode('r' + 'x')\n")));
    }

    [Fact]
    public void ComparisonsAndNotAndOrOfLiteralsAreBoolLiterals()
    {
        None(Found(Flag + "N = 3\nflag(not False)\nflag(N > 2)\nflag(N == 3 and 'a' < 'b')\nflag(0 or N != 4)\n"));
        Assert.Equal("Expected type 'Literal[True]', got 'Literal[False]' instead", string.Join("; ", Found(Flag + "flag(1 > 2)\n")));
    }

    [Fact]
    public void ANameAssignedSeveralConstantsIsTheUnionOfThem()
    {
        None(Found(Mode + "def f(b: bool):\n    m = 'r'\n    if b:\n        m = 'rb'\n    mode(m)\n"));
        Assert.Equal("Expected type 'Literal['r', 'rb', 'w']', got 'Literal['r', 'x']' instead",
            string.Join("; ", Found(Mode + "def f(b: bool):\n    m = 'r'\n    if b:\n        m = 'x'\n    mode(m)\n")));
    }

    [Fact]
    public void OnlyTheAssignmentsThatReachTheCallCount()
    {
        Assert.Equal("bytes", Last(Open + "def f():\n    m = 'x'\n    m = 'rb'\n    op(m)\n"));
    }

    [Fact]
    public void AnAssignmentThatIsNoConstantLeavesThePlainType()
    {
        Assert.Equal("str", Last(Open + "def f(s: str):\n    m = 'r'\n    if s:\n        m = s\n    op(m)\n"));
        Assert.Equal("str", Last(Open + "def f():\n    m = 'r'\n    m += 'b'\n    op(m)\n"));
        Assert.Equal("object", Last(Pick + "def f(xs: list):\n    n = 1\n    for _ in xs:\n        n = n + 1\n    pick(n)\n"));
    }

    [Fact]
    public void WhereNoLiteralIsWantedArithmeticKeepsItsPlainType()
    {
        Assert.Equal("int", Last("x = 1 + 2\nx\n"));
        Assert.Equal("str", Last("s = 'r'\ns = 'rb'\ns\n"));
    }
}
