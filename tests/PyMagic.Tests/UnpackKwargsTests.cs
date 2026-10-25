using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>**kwargs: Unpack[Options] stands for the keyword-only parameters of the TypedDict.</summary>
public class UnpackKwargsTests
{
    const string Head = "from typing import TypedDict, Unpack, NotRequired\n\n\nclass Opts(TypedDict, total=False):\n    le: int\n    repr: bool\n\n\nclass Strict(TypedDict):\n    name: str\n    size: NotRequired[int]\n\n\ndef f(**kwargs: Unpack[Opts]) -> None: ...\n\n\ndef g(a: int, **kwargs: Unpack[Strict]) -> None: ...\n\n\n";

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void KeywordsOfTheTypedDictAreAccepted()
    {
        Assert.Equal(0, Found(Head + "f()\nf(le=1)\nf(le=1, repr=True)\n").Count);
    }

    [Fact]
    public void AWrongTypeAndAnUnknownKeywordAreReported()
    {
        var f = Found(Head + "f(le='x')\nf(other=1)\n");
        Assert.Equal(2, f.Count);
        Assert.True(f.Any(x => x.Contains("'int'") && x.Contains("'str'")), string.Join(" / ", f));
        Assert.True(f.Any(x => x.Contains("'other'")), string.Join(" / ", f));
    }

    [Fact]
    public void RequiredKeysMustBeGiven()
    {
        var f = Found(Head + "g(1)\ng(1, name='n')\ng(1, name='n', size=2)\n");
        Assert.Equal(1, f.Count);
        Assert.Contains("'name'", f[0]);
    }
}
