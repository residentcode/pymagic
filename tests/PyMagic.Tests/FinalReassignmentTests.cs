using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class FinalReassignmentTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m).Select(d => d.Message).Where(x => x.StartsWith("Cannot assign to final")).ToList();
    }

    [Fact]
    public void AModuleNameDeclaredFinalCannotBeAssignedAgain()
    {
        var f = Found("from typing import Final\n\nMAX: Final = 3\nMAX = 4\nMAX += 1\nOTHER = 1\nOTHER = 2\n");
        Assert.Equal(2, f.Count);
        Assert.Contains("'MAX'", f[0]);
    }

    [Fact]
    public void FinalWithAnArgumentAndTheTypingSpelling()
    {
        var f = Found("import typing\nfrom typing import Final\n\nA: Final[int] = 1\nB: typing.Final = 2\nA = 3\nB = 4\n");
        Assert.Equal(2, f.Count);
    }

    [Fact]
    public void AFinalClassAttributeCannotBeAssigned()
    {
        var f = Found("from typing import Final\n\n\nclass K:\n    LIMIT: Final = 5\n\n    def go(self):\n        self.LIMIT = 6\n\n\nK.LIMIT = 7\nK().LIMIT = 8\n");
        Assert.Equal(3, f.Count);
        Assert.Contains("attribute 'LIMIT'", f[0]);
    }

    [Fact]
    public void FinalWithoutAValueMayBeAssignedOnce()
    {
        Assert.Equal(0, Found("from typing import Final\n\n\nclass K:\n    LIMIT: Final[int]\n\n    def __init__(self):\n        self.LIMIT = 6\n").Count);
    }

    [Fact]
    public void OrdinaryNamesAndOtherScopesAreLeftAlone()
    {
        Assert.Equal(0, Found("from typing import Final\n\nX: Final = 1\n\n\ndef f():\n    X = 2\n    return X\n").Count);
    }
}
