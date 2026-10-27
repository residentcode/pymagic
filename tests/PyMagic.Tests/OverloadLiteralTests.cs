using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Overload selection: a plain str or bool does not pick an overload that wants a Literal, and a union argument is split across the overloads.</summary>
public class OverloadLiteralTests
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

    // open() in typeshed: the text modes first, as literals, and a plain str last
    const string Open = "from typing import Literal, overload\n\n\n"
        + "@overload\ndef op(mode: Literal['r', 'w'] = 'r') -> int: ...\n"
        + "@overload\ndef op(mode: Literal['rb', 'wb']) -> bytes: ...\n"
        + "@overload\ndef op(mode: str) -> str: ...\n"
        + "def op(mode='r'):\n    return mode\n\n\n";

    // subprocess.run in typeshed: text=True, then text left out or False, then any bool
    const string Run = "from typing import Literal, overload\n\n\n"
        + "@overload\ndef run(text: Literal[True]) -> str: ...\n"
        + "@overload\ndef run(text: Literal[False] | None = None) -> bytes: ...\n"
        + "@overload\ndef run(text: bool | None = None) -> object: ...\n"
        + "def run(text=None):\n    return text\n\n\n";

    const string Pick = "from typing import overload\n\n\n"
        + "@overload\ndef pick(a: int) -> int: ...\n"
        + "@overload\ndef pick(a: str) -> str: ...\n"
        + "def pick(a):\n    return a\n\n\n";

    [Fact]
    public void APlainStrWhereTheFirstOverloadWantsALiteralPicksTheOverloadThatTakesAStr()
    {
        Assert.Equal("str", Last(Open + "def f(m: str):\n    op(m)\n"));
    }

    [Fact]
    public void ALiteralArgumentStillPicksTheOverloadOfItsLiteral()
    {
        Assert.Equal("bytes", Last(Open + "def f():\n    op('rb')\n"));
        Assert.Equal("int", Last(Open + "def f():\n    mode = 'w'\n    op(mode)\n"));
    }

    [Fact]
    public void AParameterLeftToItsDefaultKeepsPickingTheFirstOverload()
    {
        Assert.Equal("int", Last(Open + "def f():\n    op()\n"));
    }

    [Fact]
    public void APlainBoolDoesNotPickTheOverloadsForTrueOrFalse()
    {
        Assert.Equal("object", Last(Run + "def f(t: bool):\n    run(t)\n"));
        Assert.Equal("str", Last(Run + "def f():\n    run(True)\n"));
        Assert.Equal("bytes", Last(Run + "def f():\n    run(False)\n"));
        Assert.Equal("bytes", Last(Run + "def f():\n    run()\n"));
    }

    [Fact]
    public void WhenOnlyLiteralOverloadsTakeAStrTheFirstOfThemIsStillPickedWithoutAProblem()
    {
        var src = "from typing import Literal, overload\n\n\n"
            + "@overload\ndef op(mode: Literal['r']) -> int: ...\n"
            + "@overload\ndef op(mode: Literal['rb']) -> bytes: ...\n"
            + "def op(mode):\n    return mode\n\n\n"
            + "def f(m: str):\n    op(m)\n";
        Assert.Equal("int", Last(src));
        var found = Found(src);
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AnOrdinaryFunctionStaysLenientAboutAStrForALiteral()
    {
        var found = Found("from typing import Literal\n\n\ndef h(a: Literal['a', 'b']) -> int:\n    return 1\n\n\ndef f(m: str):\n    h(m)\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AnUnknownArgumentKeepsPickingTheFirstOverloadThatFits()
    {
        Assert.Equal("int", Last(Open + "def f(m):\n    op(m)\n"));
        Assert.Equal("int", Last(Pick + "def f(a):\n    pick(a)\n"));
    }

    [Fact]
    public void AUnionArgumentIsSplitAcrossTheOverloadsAndTheResultsAreJoined()
    {
        var src = Pick + "def f(u: int | str):\n    pick(u)\n";
        Assert.Equal("int | str", Last(src));
        var found = Found(src);
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void AUnionWithAMemberNoOverloadTakesIsStillAMismatch()
    {
        var found = Found(Pick + "def f(u: int | str | None):\n    pick(u)\n");
        Assert.True(found.Count == 1, string.Join("; ", found));
    }

    [Fact]
    public void TypeshedsOpenWithAPlainStrModeIsIoOfAny()
    {
        // the bundled stubs are small; the case needs typeshed's open()
        if (Workspace.DefaultTypeshedPath == null) return;
        Assert.Equal("IO[Any]", Last("def f(p: str, m: str):\n    open(p, m)\n"));
        Assert.Equal("BufferedReader", Last("def f(p: str):\n    open(p, 'rb')\n"));
        Assert.Equal("TextIOWrapper", Last("def f(p: str):\n    open(p)\n"));
    }

    [Fact]
    public void TypeshedsSubprocessRunWithAPlainBoolTextIsCompletedProcessOfAny()
    {
        if (Workspace.DefaultTypeshedPath == null) return;
        Assert.Equal("CompletedProcess[Any]", Last("import subprocess\n\n\ndef f(t: bool):\n    subprocess.run(['a'], text=t)\n"));
        Assert.Equal("CompletedProcess[str]", Last("import subprocess\n\n\nsubprocess.run(['a'], text=True)\n"));
    }

    [Fact]
    public void OsPathBasenameOfAnyPathIsStrOrBytesWithoutAProblem()
    {
        // found in the library scan (root cause 21): basename of `str | bytes | PathLike[str] | PathLike[bytes]` was a mismatch
        if (Workspace.DefaultTypeshedPath == null) return;
        var src = "import os\n\n\ndef f(x: str | bytes | os.PathLike[str] | os.PathLike[bytes]):\n    os.path.basename(x)\n";
        Assert.Equal("str | bytes", Last(src));
        var found = Found(src);
        Assert.True(found.Count == 0, string.Join("; ", found));
    }
}
