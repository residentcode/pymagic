using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

public class InlineMethodTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Runs the refactoring at the `|` cursor (first document that has one) and returns each document's new text.</summary>
    static Dictionary<string, string> Inline(string kind, params (string Uri, string Text)[] docs)
    {
        var svc = new LanguageService();
        string? cursorUri = null; PyMagic.Server.Position pos = new(0, 0);
        var texts = new Dictionary<string, string>();
        foreach (var (uri, marked) in docs)
        {
            string clean = marked.Replace("|", "");
            if (cursorUri == null && marked.Contains('|')) { cursorUri = uri; pos = new LineMap(clean).PositionOf(marked.IndexOf('|')); }
            svc.Open(uri, clean, 1);
            texts[uri] = clean;
        }
        var edit = svc.Refactor(cursorUri!, kind, new PyMagic.Server.Range(pos, pos), null);
        foreach (var (uri, edits) in edit.Changes)
        {
            var lm = new LineMap(texts[uri]);
            string t = texts[uri];
            foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
                t = t[..lm.OffsetOf(e.Range.Start)] + e.NewText + t[lm.OffsetOf(e.Range.End)..];
            texts[uri] = t;
        }
        return texts;
    }

    static string Call(string marked) => Inline("inlineMethod", (Main, marked))[Main];
    static string All(string marked) => Inline("inlineMethodAll", (Main, marked))[Main];

    static void Refuses(string kind, string reasonPart, params (string Uri, string Text)[] docs)
    {
        try { Inline(kind, docs); }
        catch (ServiceException e) { Assert.Contains(reasonPart, e.Message); return; }
        throw new AssertionException("expected a refusal containing: " + reasonPart);
    }

    // ------------------------------------------------------------ expression bodies

    [Fact]
    public void InlinesAFunctionThatJustReturnsAnExpression()
    {
        Assert.Equal(Src("def double(x):", "    return x * 2", "", "y = (a + 1) * 2"),
            Call(Src("def double(x):", "    return x * 2", "", "y = dou|ble(a + 1)")));
        Assert.Equal(Src("def total(a, b):", "    return a + b", "print(1 + 2)"), Call(Src("def total(a, b):", "    return a + b", "print(tot|al(1, 2))")));
    }

    [Fact]
    public void ParenthesesAppearOnlyWhereTheContextNeedsThem()
    {
        string def = Src("def add(a, b):", "    return a + b");
        Assert.Equal(def + Src("x = 1 + 2"), Call(def + Src("x = ad|d(1, 2)")));
        Assert.Equal(def + Src("x = 3 * (1 + 2)"), Call(def + Src("x = 3 * ad|d(1, 2)")));
        Assert.Equal(def + Src("x = -(1 + 2)"), Call(def + Src("x = -ad|d(1, 2)")));
        Assert.Equal(def + Src("print(1 + 2)"), Call(def + Src("print(ad|d(1, 2))")));
        Assert.Equal(def + Src("x = (1 + 2).real"), Call(def + Src("x = ad|d(1, 2).real")));
        // an argument that is not a plain name needs parentheses where the body uses it in an operation
        Assert.Equal(def + Src("x = p - q + r"), Call(def + Src("x = ad|d(p - q, r)")));
        Assert.Equal(Src("def sub(a, b):", "    return a - b") + Src("x = p - (q - r)"), Call(Src("def sub(a, b):", "    return a - b") + Src("x = su|b(p, q - r)")));
        Assert.Equal(def + Src("x = (a if c else b) + 1"), Call(def + Src("x = ad|d(a if c else b, 1)")));
    }

    [Fact]
    public void KeywordArgumentsAndDefaultsAreUnderstood()
    {
        string def = Src("def scale(v, factor=2, offset=0):", "    return v * factor + offset");
        Assert.Equal(def + Src("print((n * 2) + 0)").Replace("(n * 2) + 0", "n * 2 + 0"), Call(def + Src("print(sc|ale(n))")));
        Assert.Equal(def + Src("print(n * 3 + 1)"), Call(def + Src("print(sc|ale(n, offset=1, factor=3))")));
    }

    [Fact]
    public void AnArgumentWithACallIsBoundWhenTheBodyUsesItTwice()
    {
        string def = Src("def square(x):", "    return x * x");
        Assert.Equal(def + Src("def f():", "    x = load()", "    return x * x"), Call(def + Src("def f():", "    return squ|are(load())")));
        // used once: it can simply move
        string once = Src("def twice(x):", "    return x + x".Replace("x + x", "x * 2"));
        Assert.Equal(once + Src("print(load() * 2)"), Call(once + Src("print(twi|ce(load()))")));
    }

    [Fact]
    public void SeveralImpureArgumentsKeepTheirOrder()
    {
        string def = Src("def pair(a, b):", "    return (b, a)");
        string result = Call(def + Src("def f():", "    return pa|ir(first(), second())"));
        Assert.Contains("a = first()\n    b = second()\n    return (b, a)", result);
    }

    // ------------------------------------------------------------ statement bodies

    [Fact]
    public void InlinesSeveralStatementsWhereTheCallIsAStatement()
    {
        string def = Src("def show(item):", "    label = item.upper()", "    print(label, len(label))");
        Assert.Equal(def + Src("def run(thing):", "    label = thing.upper()", "    print(label, len(label))", "    return 1"),
            Call(def + Src("def run(thing):", "    sh|ow(thing)", "    return 1")));
    }

    [Fact]
    public void ResultsAreAssignedOrReturned()
    {
        string def = Src("def compute(a, b):", "    s = a + b", "    return s * 2");
        Assert.Equal(def + Src("def f(x):", "    s = x + 1", "    total = s * 2", "    return total"),
            Call(def + Src("def f(x):", "    total = comp|ute(x, 1)", "    return total")));
        Assert.Equal(def + Src("def g(x):", "    s = x + 1", "    return s * 2"),
            Call(def + Src("def g(x):", "    return comp|ute(x, 1)")));
        // a result nobody uses: a pure value disappears
        Assert.Equal(def + Src("def h(x):", "    s = x + 1", "    return 0"), Call(def + Src("def h(x):", "    comp|ute(x, 1)", "    return 0")).Replace("    s = x + 1\n    return 0", "    s = x + 1\n    return 0"));
    }

    [Fact]
    public void LocalNamesThatCollideAreRenamed()
    {
        string def = Src("def compute(a):", "    s = a + 1", "    return s * 2");
        string result = Call(def + Src("def f(x):", "    s = 100", "    r = comp|ute(x)", "    return s + r"));
        Assert.Contains("    s2 = x + 1\n    r = s2 * 2", result);
        Assert.Contains("return s + r", result);
    }

    [Fact]
    public void MethodsGetTheirReceiverSubstituted()
    {
        string cls = Src("class Cart:", "    def __init__(self):", "        self.items = [1, 2]", "", "    def total(self):", "        return sum(self.items)", "");
        Assert.Equal(cls + Src("cart = Cart()", "print(sum(cart.items))"), Call(cls + Src("cart = Cart()", "print(cart.tot|al())")));
        Assert.Equal(cls + Src("print(sum(Cart().items))"), Call(cls + Src("print(Cart().tot|al())")));
        string stmts = Src("class T:", "    def __init__(self):", "        self.n = 0", "", "    def bump(self, by):", "        self.n += by", "        print(self.n)", "");
        Assert.Equal(stmts + Src("t = T()", "t.n += 5", "print(t.n)"), Call(stmts + Src("t = T()", "t.bu|mp(5)")));
    }

    // ------------------------------------------------------------ all calls

    [Fact]
    public void InlineAllRemovesTheFunction()
    {
        string result = All(Src("def double|(x):", "    return x * 2", "", "a = double(1)", "b = double(c) + 1"));
        Assert.Equal(Src("a = 1 * 2", "b = c * 2 + 1").Replace("a = 1 * 2", "a = 1 * 2"), result.Replace("\n\n", "\n").Replace("\n\n", "\n"));
    }

    [Fact]
    public void InlineAllWorksAcrossFiles()
    {
        var r = Inline("inlineMethodAll",
            (Lib, Src("def double|(x):", "    return x * 2", "", "def other():", "    return 1")),
            (Main, Src("from lib import double", "print(double(4))")));
        Assert.Equal(Src("def other():", "    return 1"), r[Lib].Trim('\n') + "\n");
        Assert.Contains("print(4 * 2)", r[Main]);
    }

    [Fact]
    public void BodyNamesFromAnotherFileMustExistThere()
    {
        Refuses("inlineMethod", "not available in main",
            (Lib, Src("FACTOR = 3", "def scaled(x):", "    return x * FACTOR")),
            (Main, Src("from lib import scaled", "print(scal|ed(2))")));
        var r = Inline("inlineMethod",
            (Lib, Src("FACTOR = 3", "def scaled(x):", "    return x * FACTOR")),
            (Main, Src("from lib import scaled, FACTOR", "print(scal|ed(2))")));
        Assert.Contains("print(2 * FACTOR)", r[Main]);
    }

    // ------------------------------------------------------------ refusals

    [Fact]
    public void RefusesFunctionsThatCannotBeInlined()
    {
        Refuses("inlineMethod", "calls itself", (Main, Src("def f(n):", "    return f(n - 1) if n else 0", "f|(3)")));
        Refuses("inlineMethod", "more than one return", (Main, Src("def f(n):", "    if n:", "        return 1", "    return 2", "x = f|(3)")));
        Refuses("inlineMethod", "generator", (Main, Src("def f(n):", "    yield n", "for v in f|(3): pass")));
        Refuses("inlineMethod", "async", (Main, Src("async def f(n):", "    return n", "async def g():", "    return await f|(3)")));
        Refuses("inlineMethod", "decorated", (Main, Src("import functools", "@functools.cache", "def f(n):", "    return n", "f|(3)")));
        Refuses("inlineMethod", "*args", (Main, Src("def f(*a):", "    return a", "f|(3)")));
        Refuses("inlineMethod", "constructor", (Main, Src("class C:", "    def __init__(self, a):", "        self.a = a", "C|(1)")));
        Refuses("inlineMethod", "nested function", (Main, Src("def f(n):", "    def g():", "        return n", "    return g()", "f|(3)")));
    }

    [Fact]
    public void RefusesCallsThatNeedMoreThanTheyCanGet()
    {
        string many = Src("def f(a):", "    b = a + 1", "    return b * 2", "");
        Refuses("inlineMethod", "whole statement", (Main, many + Src("x = f|(1) + 2")));
        Refuses("inlineMethod", "does not pass", (Main, Src("def f(a, b):", "    return a", "f|(1)")));
        Refuses("inlineMethod", "* or **", (Main, Src("def f(a):", "    return a", "args = [1]", "f|(*args)")));
        Refuses("inlineMethod", "plain constant", (Main, Src("def f(a, b=g()):", "    return a", "f|(1)")));
        Refuses("inlineMethod", "Put the cursor on a function", (Main, Src("def f(a):", "    return a", "x| = 1")));
        Refuses("inlineMethod", "lambda", (Main, Src("def f(a):", "    return a * a", "g = lambda v: f|(next(v))")));
    }

    [Fact]
    public void InlineAllIsRefusedWhenTheFunctionIsAlsoUsedAsAValue()
    {
        Refuses("inlineMethodAll", "also used as a value", (Main, Src("def f|(a):", "    return a", "f(1)", "list(map(f, [1]))")));
        Refuses("inlineMethodAll", "never called", (Main, Src("def f|(a):", "    return a")));
        Refuses("inlineMethodAll", "cannot be inlined, so the function stays", (Main, Src("def f|(a):", "    b = a", "    return b", "x = f(1) + 1")));
    }

    [Fact]
    public void NestedCallsMustBeInlinedOneAtATime()
    {
        Refuses("inlineMethodAll", "nested in each other", (Main, Src("def f|(a):", "    return a + 1", "print(f(f(1)))")));
    }

    // ------------------------------------------------------------ the menu

    [Fact]
    public void TheMenuOffersWhatApplies()
    {
        List<string> Titles(string marked, params (string Uri, string Text)[] others)
        {
            string clean = marked.Replace("|", "");
            var lm = new LineMap(clean);
            var pos = lm.PositionOf(marked.IndexOf('|'));
            var svc = new LanguageService();
            foreach (var (u, t) in others) svc.Open(u, t, 1);
            svc.Open(Main, clean, 1);
            return svc.RefactorOptions(Main, new PyMagic.Server.Range(pos, pos)).Select(o => o.Title).ToList();
        }

        var atCall = Titles(Src("def double(x):", "    return x * 2", "y = dou|ble(2)"));
        Assert.True(atCall.Contains("Change signature…") && atCall.Contains("Inline method (this call)"), string.Join(" | ", atCall));
        var atDef = Titles(Src("def double|(x):", "    return x * 2", "y = double(2)"));
        Assert.True(atDef.Contains("Inline method and remove it") && atDef.Contains("Change signature…"), string.Join(" | ", atDef));
        var plain = Titles(Src("x| = 1"));
        Assert.True(!plain.Contains("Change signature…") && !plain.Contains("Inline method (this call)"), string.Join(" | ", plain));
    }
}
