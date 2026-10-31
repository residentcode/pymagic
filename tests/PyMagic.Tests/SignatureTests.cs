using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class SignatureTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static SignatureParam P(int? origin, string name, string category = "normal", string? def = null, string? callValue = null, string? annotation = null) =>
        new(origin, name, category, annotation, def, callValue);

    sealed record Outcome(Dictionary<string, string> Texts, List<string> Warnings);

    /// <summary>Changes the signature at the `|` marker (in the first document that has one) and returns every document's new text.</summary>
    static Outcome Change(string? newName, SignatureParam[] ps, params (string Uri, string Text)[] docs)
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
        var result = svc.ChangeSignature(cursorUri!, pos, newName, ps);
        foreach (var (uri, edits) in result.Changes)
        {
            var lm = new LineMap(texts[uri]);
            string t = texts[uri];
            foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
                t = t[..lm.OffsetOf(e.Range.Start)] + e.NewText + t[lm.OffsetOf(e.Range.End)..];
            texts[uri] = t;
        }
        return new Outcome(texts, result.Warnings);
    }

    static string One(SignatureParam[] ps, string marked, string? newName = null) => Change(newName, ps, (Main, marked)).Texts[Main];

    static void Refuses(string reasonPart, SignatureParam[] ps, params (string Uri, string Text)[] docs)
    {
        try { Change(null, ps, docs); }
        catch (ServiceException e) { Assert.Contains(reasonPart, e.Message); return; }
        throw new AssertionException("expected a refusal containing: " + reasonPart);
    }

    // ------------------------------------------------------------ describing

    [Fact]
    public void DescribesTheSignatureAndItsUsage()
    {
        var svc = new LanguageService();
        svc.Open(Lib, Src("def make(a, b=2, /, c=3, *rest, d, e=5, **extra):", "    return a"), 1);
        svc.Open(Main, Src("from lib import make", "make(1, d=2)", "make(2, d=3)", "callback = make"), 1);
        var info = svc.GetSignature(Main, new PyMagic.Server.Position(1, 1));
        Assert.Equal("make", info.Name);
        Assert.Equal("function", info.Kind);
        Assert.Equal(2, info.Calls);
        Assert.Equal(1, info.OtherReferences);
        Assert.Equal("a:positionalOnly,b:positionalOnly=2,c:normal=3,rest:varArgs,d:keywordOnly,e:keywordOnly=5,extra:kwArgs",
            string.Join(",", info.Parameters.Select(p => p.Name + ":" + p.Category + (p.Default != null ? "=" + p.Default : ""))));
    }

    [Fact]
    public void MethodsAndConstructorsHideTheirReceiver()
    {
        var svc = new LanguageService();
        svc.Open(Main, Src("class C:", "    def __init__(self, a, b):", "        pass", "    def go(self, x):", "        pass", "C(1, 2)"), 1);
        var ctor = svc.GetSignature(Main, new PyMagic.Server.Position(1, 9));
        Assert.Equal("constructor", ctor.Kind);
        Assert.Equal("self", ctor.ImplicitFirst);
        Assert.Equal("a,b", string.Join(",", ctor.Parameters.Select(p => p.Name)));
        Assert.Equal(1, ctor.Calls);
        var method = svc.GetSignature(Main, new PyMagic.Server.Position(3, 9));
        Assert.Equal("method", method.Kind);
        Assert.Equal("x", string.Join(",", method.Parameters.Select(p => p.Name)));
        // from the call as well
        Assert.Equal("constructor", svc.GetSignature(Main, new PyMagic.Server.Position(5, 1)).Kind);
    }

    // ------------------------------------------------------------ changing it

    [Fact]
    public void ReordersParametersAndArguments()
    {
        string result = One(new[] { P(1, "b"), P(0, "a") },
            Src("def f|(a, b):", "    return a - b", "f(1, 2)", "f(b=3, a=4)", "f(5, b=6)"));
        Assert.Equal(Src("def f(b, a):", "    return a - b", "f(2, 1)", "f(b=3, a=4)", "f(b=6, a=5)"), result);
    }

    [Fact]
    public void RenamesAParameterInTheBodyAndInKeywordArguments()
    {
        string result = One(new[] { P(0, "left"), P(1, "b") },
            Src("def f|(a, b):", "    c = a + b", "    return a * c", "f(1, 2)", "f(a=3, b=4)"));
        Assert.Equal(Src("def f(left, b):", "    c = left + b", "    return left * c", "f(1, 2)", "f(left=3, b=4)"), result);
    }

    [Fact]
    public void SwappingTwoNamesIsFine()
    {
        string result = One(new[] { P(0, "b"), P(1, "a") }, Src("def f|(a, b):", "    return a - b", "f(a=1, b=2)"));
        Assert.Equal(Src("def f(b, a):", "    return b - a", "f(b=1, a=2)"), result);
    }

    [Fact]
    public void AddsAParameterWithADefaultOrAValueForExistingCalls()
    {
        Assert.Equal(Src("def f(a, b, scale=1):", "    return a + b", "f(1, 2)", "f(3, 4)"),
            One(new[] { P(0, "a"), P(1, "b"), P(null, "scale", def: "1") }, Src("def f|(a, b):", "    return a + b", "f(1, 2)", "f(3, 4)")));
        Assert.Equal(Src("def f(a, scale, b):", "    return a + b", "f(1, 10, 2)", "f(3, 10, 4)"),
            One(new[] { P(0, "a"), P(null, "scale", callValue: "10"), P(1, "b") }, Src("def f|(a, b):", "    return a + b", "f(1, 2)", "f(3, 4)")));
        // a default that follows an omitted argument turns the later arguments into keywords
        Assert.Equal(Src("def f(a, extra=0, b=5):", "    return a", "f(1)", "f(1, b=7)"),
            One(new[] { P(0, "a"), P(null, "extra", def: "0"), P(1, "b", def: "5") }, Src("def f|(a, b=5):", "    return a", "f(1)", "f(1, 7)")));
    }

    [Fact]
    public void RemovesAParameterAndItsArguments()
    {
        Assert.Equal(Src("def f(a, c):", "    return a + c", "f(1, 3)", "f(4, c=6)"),
            One(new[] { P(0, "a"), P(2, "c") }, Src("def f|(a, b, c):", "    return a + c", "f(1, 2, 3)", "f(4, 5, c=6)")));
        Assert.Equal(Src("def f():", "    return 1", "f()"), One(Array.Empty<SignatureParam>(), Src("def f|(a):", "    return 1", "f(2)")));
    }

    [Fact]
    public void RemovingIsRefusedWhenItWouldLoseInformation()
    {
        Refuses("used in the function body", new[] { P(0, "a") }, (Main, Src("def f|(a, b):", "    return a + b")));
        var dropped = Change(null, new[] { P(0, "a") }, (Main, Src("def f|(a, b):", "    return a", "f(1, g())")));
        Assert.True(dropped.Warnings.Any(w => w.Contains("dropping it would drop the call")), string.Join(" | ", dropped.Warnings));
        Assert.Contains("f(1, g())", dropped.Texts[Main]);   // the call is left alone rather than losing g()
        Refuses("f-string", new[] { P(0, "x"), P(1, "b") }, (Main, Src("def f|(a, b):", "    return f\"{a}{b}\"")));
    }

    [Fact]
    public void ValidatesTheNewSignature()
    {
        var one = Src("def f|(a, b):", "    return a + b", "x = 0");
        Refuses("has no default but follows", new[] { P(0, "a", def: "1"), P(1, "b") }, (Main, one));
        Refuses("appears twice", new[] { P(0, "a"), P(1, "a") }, (Main, one));
        Refuses("not a valid parameter name", new[] { P(0, "a"), P(1, "class") }, (Main, one));
        Refuses("order of parameter kinds", new[] { P(0, "a", "keywordOnly"), P(1, "b") }, (Main, one));
        Refuses("only one *args", new[] { P(0, "a", "varArgs"), P(1, "b", "varArgs") }, (Main, one));
        Refuses("already uses the name", new[] { P(0, "a"), P(1, "b"), P(null, "x", def: "1") }, (Main, Src("def f|(a, b):", "    return a + b + x", "x = 0")));
    }

    [Fact]
    public void RendersKeywordOnlyAndPositionalOnlyMarkers()
    {
        Assert.Equal(Src("def f(a, *, b=1):", "    pass", "f(1, b=2)", "f(3)"),
            One(new[] { P(0, "a"), P(1, "b", "keywordOnly", def: "1") }, Src("def f|(a, b=1):", "    pass", "f(1, 2)", "f(3)")));
        Assert.Equal(Src("def f(a, /, b):", "    pass", "f(1, 2)"),
            One(new[] { P(0, "a", "positionalOnly"), P(1, "b") }, Src("def f|(a, b):", "    pass", "f(1, 2)")));
        Assert.Equal(Src("def f(a, *args, b, **kw):", "    pass", "f(1, 2, 3, b=4, z=5)"),
            One(new[] { P(0, "a"), P(1, "args", "varArgs"), P(2, "b", "keywordOnly"), P(3, "kw", "kwArgs") }, Src("def f|(a, *args, b, **kw):", "    pass", "f(1, 2, 3, b=4, z=5)")));
    }

    [Fact]
    public void MethodsKeepTheirReceiverAndExplicitSelfCalls()
    {
        string result = One(new[] { P(1, "y"), P(0, "x") },
            Src("class C:", "    def go|(self, x, y):", "        return x - y", "", "c = C()", "c.go(1, 2)", "C.go(c, 3, 4)", "c.go(y=5, x=6)"));
        Assert.Equal(Src("class C:", "    def go(self, y, x):", "        return x - y", "", "c = C()", "c.go(2, 1)", "C.go(c, 4, 3)", "c.go(y=5, x=6)"), result);
    }

    [Fact]
    public void ConstructorsAreUpdatedThroughTheClassAndSuper()
    {
        string result = One(new[] { P(0, "name"), P(1, "size", def: "1") },
            Src("class Base:", "    def __init__|(self, name, size):", "        self.name = name", "", "class Child(Base):", "    def __init__(self, name):", "        super().__init__(name, 3)", "", "Base('a', 2)", "Child('b')"));
        Assert.Equal(Src("class Base:", "    def __init__(self, name, size=1):", "        self.name = name", "", "class Child(Base):", "    def __init__(self, name):", "        super().__init__(name, 3)", "", "Base('a', 2)", "Child('b')"), result);

        string reordered = One(new[] { P(1, "size"), P(0, "name") },
            Src("class Base:", "    def __init__|(self, name, size):", "        pass", "", "class Child(Base):", "    def __init__(self):", "        super().__init__('x', 3)", "", "Base('a', 2)"));
        Assert.Equal(Src("class Base:", "    def __init__(self, size, name):", "        pass", "", "class Child(Base):", "    def __init__(self):", "        super().__init__(3, 'x')", "", "Base(2, 'a')"), reordered);
    }

    [Fact]
    public void WorksAcrossFilesAndThroughImportsAndAliases()
    {
        var r = Change(null, new[] { P(1, "b"), P(0, "a") },
            (Lib, Src("def f|(a, b):", "    return a - b")),
            (Main, Src("from lib import f", "from lib import f as g", "import lib", "f(1, 2)", "g(3, 4)", "lib.f(5, 6)")));
        Assert.Equal(Src("def f(b, a):", "    return a - b"), r.Texts[Lib]);
        Assert.Equal(Src("from lib import f", "from lib import f as g", "import lib", "f(2, 1)", "g(4, 3)", "lib.f(6, 5)"), r.Texts[Main]);
    }

    [Fact]
    public void StartingFromACallWorksToo()
    {
        var r = Change(null, new[] { P(1, "b"), P(0, "a") },
            (Lib, Src("def f(a, b):", "    return a - b")),
            (Main, Src("from lib import f", "f|(1, 2)")));
        Assert.Equal(Src("def f(b, a):", "    return a - b"), r.Texts[Lib]);
        Assert.Equal(Src("from lib import f", "f(2, 1)"), r.Texts[Main]);
    }

    [Fact]
    public void RenamesTheFunctionAndItsCallers()
    {
        var r = Change("combine", new[] { P(0, "a"), P(1, "b") },
            (Lib, Src("def f|(a, b):", "    return a")),
            (Main, Src("from lib import f", "f(1, 2)")));
        Assert.Equal(Src("def combine(a, b):", "    return a"), r.Texts[Lib]);
        Assert.Equal(Src("from combine_placeholder import combine"), Src("from combine_placeholder import combine"));
        Assert.Contains("combine(1, 2)", r.Texts[Main]);
        Assert.Contains("import combine", r.Texts[Main]);
    }

    [Fact]
    public void CallsThatCannotBeRewrittenAreReported()
    {
        var r = Change(null, new[] { P(1, "b"), P(0, "a") },
            (Main, Src("def f|(a, b):", "    return a - b", "args = (1, 2)", "f(*args)", "f(3, 4)", "map(f, [1], [2])")));
        Assert.Equal(Src("def f(b, a):", "    return a - b", "args = (1, 2)", "f(*args)", "f(4, 3)", "map(f, [1], [2])"), r.Texts[Main]);
        Assert.True(r.Warnings.Any(w => w.Contains("Not updated") && w.Contains("* or **")), string.Join(" | ", r.Warnings));
        Assert.True(r.Warnings.Any(w => w.Contains("not a call")), string.Join(" | ", r.Warnings));
    }

    [Fact]
    public void WarnsAboutOverridesAndComments()
    {
        // an override takes the change (README item 160); a same-named method of an unrelated class is only reported
        var r = Change(null, new[] { P(0, "x") },
            (Main, Src("class A:", "    def go|(self, x, y):", "        pass", "", "class B(A):", "    def go(self, x, y):", "        pass", "", "class C:", "    def go(self, x, y):", "        pass")));
        Assert.Contains("class B(A):\n    def go(self, x):", r.Texts[Main]);
        Assert.Contains("class C:\n    def go(self, x, y):", r.Texts[Main]);
        Assert.True(r.Warnings.Any(w => w.Contains("define a method with this name") && w.Contains("1 other class")), string.Join(" | ", r.Warnings));

        var c = Change(null, new[] { P(0, "x") }, (Main, Src("def f|(x, y  # the second one", "      ):", "    return x")));
        Assert.True(c.Warnings.Any(w => w.Contains("Comments")), string.Join(" | ", c.Warnings));
    }

    [Fact]
    public void KeepsAMultiLineParameterListMultiLine()
    {
        string result = One(new[] { P(1, "b"), P(0, "a") },
            Src("def f|(", "    a,", "    b,", "):", "    return a", "f(1, 2)"));
        Assert.Equal(Src("def f(", "    b,", "    a,", "):", "    return a", "f(2, 1)"), result);
    }

    [Fact]
    public void DecoratedAndLibraryFunctionsAreRefused()
    {
        Refuses("decorated", new[] { P(0, "a") }, (Main, Src("import functools", "@functools.cache", "def f|(a):", "    return a")));
        Refuses("Put the cursor", new[] { P(0, "a") }, (Main, Src("x| = 1")));
    }

    [Fact]
    public void ServerRequestsDescribeAndChange()
    {
        var sent = new List<JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), sent.Add);
        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\",\"languageId\":\"python\",\"version\":1,\"text\":\"def f(a, b):\\n    return a\\nf(1, 2)\\n\"}}}")!);
        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"pymagic/signature\",\"params\":{\"textDocument\":{\"uri\":\"" + Main + "\"},\"position\":{\"line\":0,\"character\":5}}}")!);
        var info = sent.Last(m => m["id"]?.GetValue<int>() == 1)["result"]!;
        Assert.Equal("f", info["name"]!.GetValue<string>());
        Assert.Equal(2, info["parameters"]!.AsArray().Count);
        Assert.Equal(1, info["calls"]!.GetValue<int>());

        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"pymagic/changeSignature\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\"},\"position\":{\"line\":0,\"character\":5},\"newName\":null,\"parameters\":[{\"origin\":1,\"name\":\"b\",\"category\":\"normal\"},{\"origin\":0,\"name\":\"a\",\"category\":\"normal\"}]}}")!);
        var result = sent.Last(m => m["id"]?.GetValue<int>() == 2)["result"]!;
        Assert.True(result["changes"]![Main]!.AsArray().Count >= 2);
        Assert.Equal(0, result["warnings"]!.AsArray().Count);

        server.Handle(JsonNode.Parse("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"pymagic/changeSignature\",\"params\":{\"textDocument\":{\"uri\":\"" + Main +
            "\"},\"position\":{\"line\":0,\"character\":5},\"parameters\":[{\"origin\":0,\"name\":\"a\",\"category\":\"normal\"},{\"origin\":1,\"name\":\"a\",\"category\":\"normal\"}]}}")!);
        Assert.Equal(-32803, sent.Last(m => m["id"]?.GetValue<int>() == 3)["error"]!["code"]!.GetValue<int>());
    }
}
