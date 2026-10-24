using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A Final name keeps the literal it is assigned: MODE: Final = "r" is Literal['r'].</summary>
public class FinalLiteralTests
{
    const string Pre = "from typing import Literal, Final\n\n";

    static (Workspace Ws, ModuleInfo M) Load(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    static string T(string src)
    {
        var (ws, m) = Load(src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void ABareFinalKeepsTheLiteral()
    {
        Assert.Equal("Literal['r']", T("MODE: Final = \"r\"\nMODE"));
        Assert.Equal("Literal[3]", T("N: Final = 3\nN"));
        Assert.Equal("Literal[True]", T("DEBUG: Final = True\nDEBUG"));
    }

    [Fact]
    public void AFinalWithATypeKeepsThatType()
    {
        Assert.Equal("str", T("MODE: Final[str] = \"r\"\nMODE"));
    }

    [Fact]
    public void APlainAssignmentStaysWide()
    {
        Assert.Equal("str", T("mode = \"r\"\nmode"));
    }

    [Fact]
    public void AFinalIsCheckedAgainstALiteralParameter()
    {
        var (ws, m) = Load("MODE: Final = \"r\"\nBAD: Final = \"q\"\n\n\ndef open_it(mode: Literal[\"r\", \"w\"]): ...\n\n\nopen_it(MODE)\nopen_it(BAD)\n");
        var found = ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
        Assert.Equal(1, found.Count);
        Assert.True(found[0].Contains("Literal['q']"), found[0]);
    }
}
