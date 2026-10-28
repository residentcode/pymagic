using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Parameter info while a call is typed (item 118): the callee's signature, the parameter the cursor is on, and the places that have none.</summary>
public class SignatureHelpTests
{
    const string Main = "file:///c:/proj/main.py";

    /// <summary>Help at the `|` in the source (the mark is not part of the text).</summary>
    static SignatureHelp? At(string marked)
    {
        int at = marked.IndexOf('|');
        string src = marked.Remove(at, 1);
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.SignatureHelp(Main, new LineMap(src).PositionOf(at));
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Lit(SignatureHelp h, int signature = 0, int parameter = -1)
    {
        var s = h.Signatures[signature];
        if (parameter < 0) return s.Label;
        var l = s.Parameters[parameter].Label;
        return s.Label[l[0]..l[1]];
    }

    [Fact]
    public void AFunctionWithTheParameterTheCursorIsOn()
    {
        string def = Src("def f(a: int, b: str = 'x'):", "    pass");
        var h = At(def + "f(|")!;
        Assert.Equal("f(a: int, b: str = ...) -> None", Lit(h));
        Assert.Equal(0, h.ActiveParameter);
        Assert.Equal("a: int", Lit(h, 0, 0));
        h = At(def + "f(1, |")!;
        Assert.Equal(1, h.ActiveParameter);
        Assert.Equal("b: str = ...", Lit(h, 0, 1));
        h = At(def + "f(1, 'a', |)")!;
        Assert.Equal(2, h.ActiveParameter);   // past the end: nothing is lit
    }

    [Fact]
    public void AKeywordArgumentLightsItsParameter()
    {
        string def = Src("def f(a, b, *, c=1):", "    pass");
        Assert.Equal(2, At(def + "f(1, c=|)")!.ActiveParameter);
        Assert.Equal(1, At(def + "f(b=|")!.ActiveParameter);
        Assert.Equal("f(a, b, *, c = ...) -> None", Lit(At(def + "f(|")!));
    }

    [Fact]
    public void AMethodDoesNotShowSelf()
    {
        string src = Src("class C:", "    def m(self, x: int, y: int) -> str:", "        return ''", "", "c = C()", "c.m(1, |");
        var h = At(src)!;
        Assert.Equal("m(x: int, y: int) -> str", Lit(h));
        Assert.Equal(1, h.ActiveParameter);
    }

    [Fact]
    public void AClassShowsItsConstructor()
    {
        string src = Src("class P:", "    def __init__(self, x: int, y: int):", "        pass", "", "P(1, |");
        var h = At(src)!;
        Assert.Equal("P(x: int, y: int)", Lit(h));
        Assert.Equal(1, h.ActiveParameter);
        Assert.Equal("P(a: int, b: str = ...)", Lit(At(Src("from dataclasses import dataclass", "", "@dataclass", "class P:", "    a: int", "    b: str = 'x'", "", "P(|"))!));
    }

    [Fact]
    public void StarParametersTakeTheRestOfTheArguments()
    {
        string def = Src("def g(a, *rest, **options):", "    pass");
        Assert.Equal(1, At(def + "g(1, 2, 3, |")!.ActiveParameter);
        Assert.Equal(2, At(def + "g(1, color=|")!.ActiveParameter);
        Assert.Equal("*rest", Lit(At(def + "g(|")!, 0, 1));
    }

    [Fact]
    public void TheInnermostCallWins()
    {
        string def = Src("def f(a, b):", "    pass", "", "def g(x):", "    pass");
        var h = At(def + "f(g(1), |)")!;
        Assert.Equal("f(a, b) -> None", Lit(h));
        Assert.Equal(1, h.ActiveParameter);
        Assert.Equal("g(x) -> None", Lit(At(def + "f(1, g(|")!));
        // a bracket or a tuple inside the call is not a call, and its commas are not the call's
        h = At(def + "f((1, 2, |")!;
        Assert.Equal("f(a, b) -> None", Lit(h));
        Assert.Equal(0, h.ActiveParameter);
        h = At(def + "f([1, 2], x[1, |")!;
        Assert.Equal(1, h.ActiveParameter);
    }

    [Fact]
    public void OverloadsAreOfferedAndTheOneThatFitsIsChosen()
    {
        string def = Src("from typing import overload", "", "@overload", "def a(x: int) -> int: ...", "@overload", "def a(x: str, y: int) -> str: ...");
        var h = At(def + "a(1, |")!;
        Assert.Equal(2, h.Signatures.Count);
        Assert.Equal(1, h.ActiveSignature);
        Assert.Equal(0, At(def + "a(|")!.ActiveSignature);
    }

    [Fact]
    public void LibraryFunctionsHaveSignatures()
    {
        var h = At("print(|")!;
        Assert.Contains("print(", Lit(h));
        Assert.Contains("sep", Lit(h));
    }

    [Fact]
    public void WhereThereIsNoCallThereIsNoHelp()
    {
        Assert.True(At("x = (1, |") == null);
        Assert.True(At("x = 1 + |") == null);
        Assert.True(At(Src("def f(a): pass", "f('a, |')")) == null);          // inside a string
        Assert.True(At(Src("def f(a): pass", "f(1)  # f(|")) == null);        // inside a comment
        Assert.True(At(Src("def f(a): pass", "f(1)|")) == null);              // the call is closed
        Assert.True(At("nothing_known(|") == null);
        Assert.True(At(Src("if (|")) == null);
    }
}
