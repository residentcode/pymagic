using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class ProtocolSignatureTests
{
    const string Head = "from typing import Protocol\n\n\nclass Closer(Protocol):\n    def close(self, force: bool = False) -> None: ...\n\n    def write(self, data: str) -> int: ...\n\n\ndef use(c: Closer) -> None: ...\n\n\n";

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).Where(x => x.StartsWith("Expected type")).ToList();
    }

    [Fact]
    public void AClassWithMatchingMethodsFits()
    {
        Assert.Equal(0, Found(Head + "class F:\n    def close(self, force=False): ...\n\n    def write(self, data): ...\n\n\nuse(F())\n").Count);
        Assert.Equal(0, Found(Head + "class G:\n    def close(self, *args, **kw): ...\n\n    def write(self, data, extra=1): ...\n\n\nuse(G())\n").Count);
    }

    [Fact]
    public void FewerParametersDoNotFit()
    {
        var f = Found(Head + "class F:\n    def close(self): ...\n\n    def write(self, data): ...\n\n\nuse(F())\n");
        Assert.Equal(1, f.Count);
        Assert.Contains("Closer", f[0]);
    }

    [Fact]
    public void ARequiredExtraParameterDoesNotFit()
    {
        Assert.Equal(1, Found(Head + "class F:\n    def close(self, force, mode): ...\n\n    def write(self, data): ...\n\n\nuse(F())\n").Count);
    }

    [Fact]
    public void AMissingMethodStillDoesNotFit()
    {
        Assert.Equal(1, Found(Head + "class F:\n    def close(self): ...\n\n\nuse(F())\n").Count);
    }
}
