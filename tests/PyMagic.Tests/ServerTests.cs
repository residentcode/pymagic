using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class ServerTests
{
    const string Main = "file:///c:/proj/main.py";

    /// <summary>Splits text at the `|` marker into the clean text and the marker's position.</summary>
    static (string Text, PyMagic.Server.Position Pos) Mark(string marked)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        return (text, new LineMap(text).PositionOf(i));
    }

    static (LanguageService Svc, PyMagic.Server.Position Pos) Open(string marked, string uri = Main)
    {
        var (text, pos) = Mark(marked);
        var svc = new LanguageService();
        svc.Open(uri, text, 1);
        return (svc, pos);
    }

    static string HoverText(string marked)
    {
        var (svc, pos) = Open(marked);
        var h = svc.Hover(Main, pos);
        return h == null ? "<none>" : h.Contents.Value.Replace("```python\n", "").Replace("\n```", "");
    }

    // ------------------------------------------------------------ line map

    [Fact]
    public void LineMapRoundTripsAcrossLineEndings()
    {
        var lm = new LineMap("ab\r\ncd\nef\rgh");
        Assert.Equal(4, lm.LineCount);
        Assert.Equal(new PyMagic.Server.Position(1, 1), lm.PositionOf(5));
        Assert.Equal(new PyMagic.Server.Position(2, 0), lm.PositionOf(7));
        Assert.Equal(new PyMagic.Server.Position(3, 1), lm.PositionOf(11));
        Assert.Equal(5, lm.OffsetOf(new PyMagic.Server.Position(1, 1)));
        Assert.Equal(12, lm.OffsetOf(new PyMagic.Server.Position(3, 99)));   // clamped to the end
    }

    // ------------------------------------------------------------ diagnostics

    [Fact]
    public void SyntaxErrorIsAnError()
    {
        var svc = new LanguageService();
        svc.Open(Main, "def f(:\n    pass\n", 1);
        var d = svc.Diagnostics(Main);
        Assert.True(d.Any(x => x.Severity == Severity.Error), "expected a syntax error");
    }

    [Fact]
    public void UnresolvedNameAndUnreachableCode()
    {
        var svc = new LanguageService();
        svc.Open(Main, "print(missing)\ndef f():\n    return 1\n    x = 2\n", 1);
        var d = svc.Diagnostics(Main);
        var unresolved = d.Single(x => x.Message.Contains("missing"));
        Assert.Equal(Severity.Warning, unresolved.Severity);
        Assert.Equal(0, unresolved.Range.Start.Line);
        Assert.Equal(6, unresolved.Range.Start.Character);
        var dead = d.Single(x => x.Message == "Unreachable code");
        Assert.Equal(Severity.Hint, dead.Severity);
        Assert.Equal(3, dead.Range.Start.Line);
    }

    [Fact]
    public void CleanFileHasNoDiagnostics()
    {
        var svc = new LanguageService();
        svc.Open(Main, "class A:\n    def __init__(self):\n        self.x = 1\n\n\nprint(A().x)\n", 1);
        Assert.Equal(0, svc.Diagnostics(Main).Count);
    }

    [Fact]
    public void BadCallArityIsReported()
    {
        var svc = new LanguageService();
        svc.Open(Main, "def f(a, b):\n    pass\nf(1)\n", 1);
        Assert.True(svc.Diagnostics(Main).Any(x => x.Range.Start.Line == 2), "expected a call diagnostic on line 3");
    }

    [Fact]
    public void ChangeReplacesTheDocument()
    {
        var svc = new LanguageService();
        svc.Open(Main, "print(missing)\n", 1);
        Assert.True(svc.Diagnostics(Main).Count > 0);
        svc.Change(Main, "print(1)\n", 2);
        Assert.Equal(0, svc.Diagnostics(Main).Count);
        Assert.Equal(2, svc.VersionOf(Main)!.Value);
        svc.Close(Main);
        Assert.Equal(0, svc.Diagnostics(Main).Count);
    }

    // ------------------------------------------------------------ hover

    [Fact]
    public void HoverShowsVariableType()
    {
        Assert.Equal("x: int", HoverText("x = 1\nprint(|x)\n"));
        Assert.Equal("s: str", HoverText("s = 'a'\ns|\n"));
    }

    [Fact]
    public void HoverShowsFunctionSignature()
    {
        Assert.Equal("def f(a: int, b: str = ...) -> bool", HoverText("def |f(a: int, b: str = 'x') -> bool:\n    return True\n"));
        Assert.Equal("def f(a: int) -> int", HoverText("def f(a: int) -> int:\n    return a\nf|(1)\n"));
    }

    [Fact]
    public void HoverShowsClassHeaderAndMethods()
    {
        Assert.Equal("class B(A)", HoverText("class A: ...\nclass |B(A): ...\n"));
        Assert.Equal("def m(self, n: int) -> str",
            HoverText("class C:\n    def m(self, n: int) -> str:\n        return ''\nC.|m\n").Replace("self: C", "self: C"));
        Assert.Equal("def m(n: int) -> str",
            HoverText("class C:\n    def m(self, n: int) -> str:\n        return ''\nC().|m\n"));
    }

    [Fact]
    public void HoverOnAttributeAndOptional()
    {
        Assert.Contains("def upper() -> str", HoverText("s = 'a'\ns.up|per\n"));   // real typeshed adds an overload note
        Assert.Equal("(parameter) v: int | None", HoverText("from typing import Optional\ndef f(v: Optional[int]):\n    v|\n"));
    }

    [Fact]
    public void HoverMarksParametersAndModules()
    {
        Assert.Equal("(parameter) a: int", HoverText("def f(a: int):\n    return |a\n"));
        Assert.Equal("(module) collections", HoverText("import collections\n|collections\n"));
    }

    // ------------------------------------------------------------ definition

    [Fact]
    public void DefinitionOfLocalVariable()
    {
        var (svc, pos) = Open("value = 1\nprint(va|lue)\n");
        var loc = svc.Definition(Main, pos).Single();
        Assert.Equal(Main, loc.Uri);
        Assert.Equal(0, loc.Range.Start.Line);
        Assert.Equal(0, loc.Range.Start.Character);
    }

    [Fact]
    public void DefinitionOfFunctionPointsAtItsName()
    {
        var (svc, pos) = Open("def helper():\n    pass\nhel|per()\n");
        var loc = svc.Definition(Main, pos).Single();
        Assert.Equal(0, loc.Range.Start.Line);
        Assert.Equal(4, loc.Range.Start.Character);
        Assert.Equal(10, loc.Range.End.Character);
    }

    [Fact]
    public void DefinitionOfMethodAndInstanceAttribute()
    {
        const string src = "class C:\n    def __init__(self):\n        self.count = 0\n    def bump(self):\n        pass\nc = C()\nc.bu|mp()\nc.count\n";
        var (svc, pos) = Open(src);
        var loc = svc.Definition(Main, pos).Single();
        Assert.Equal(3, loc.Range.Start.Line);

        var (svc3, pos3) = Open("class C:\n    def __init__(self):\n        self.count = 0\nC().cou|nt\n");
        var loc3 = svc3.Definition(Main, pos3).Single();
        Assert.Equal(2, loc3.Range.Start.Line);
    }

    [Fact]
    public void DefinitionAcrossDocumentsAndImports()
    {
        var svc = new LanguageService();
        svc.Open("file:///c:/proj/lib.py", "class Widget:\n    size = 1\n\ndef make():\n    return Widget()\n", 1);
        var (text, pos) = Mark("from lib import Wid|get, make\nmake().size\n");
        svc.Open(Main, text, 1);

        // on the import itself
        var imported = svc.Definition(Main, pos).Single();
        Assert.Equal("file:///c:/proj/lib.py", imported.Uri);
        Assert.Equal(0, imported.Range.Start.Line);

        // on a use of an imported function
        var (text2, pos2) = Mark("from lib import make\nma|ke()\n");
        svc.Change(Main, text2, 2);
        var viaName = svc.Definition(Main, pos2).Single();
        Assert.Equal("file:///c:/proj/lib.py", viaName.Uri);
        Assert.Equal(3, viaName.Range.Start.Line);

        // attribute on an object that came from another module
        var (text3, pos3) = Mark("from lib import make\nmake().si|ze\n");
        svc.Change(Main, text3, 3);
        var attr = svc.Definition(Main, pos3).Single();
        Assert.Equal("file:///c:/proj/lib.py", attr.Uri);
        Assert.Equal(1, attr.Range.Start.Line);
    }

    [Fact]
    public void DefinitionOfBuiltinsGoesToStubs()
    {
        var (svc, pos) = Open("le|n([1])\n");
        // bundled stubs have no file, so there may be nothing to jump to; it must simply not fail
        _ = svc.Definition(Main, pos);
    }

    // ------------------------------------------------------------ completion

    static List<string> Labels(string marked)
    {
        var (svc, pos) = Open(marked);
        return svc.Completion(Main, pos).Select(c => c.Label).ToList();
    }

    [Fact]
    public void CompletesMembersOfBuiltinTypes()
    {
        var labels = Labels("s = 'abc'\ns.up|\n");
        Assert.True(labels.Contains("upper"), "upper missing");
        Assert.True(!labels.Contains("lower"), "prefix filter not applied");
        Assert.True(Labels("s = 'abc'\ns.|\n").Contains("lower"), "lower missing");
    }

    [Fact]
    public void CompletesClassAndInstanceMembers()
    {
        const string cls = "class C:\n    limit = 3\n    def __init__(self):\n        self.count = 0\n    def bump(self): ...\n";
        var labels = Labels(cls + "C().|\n");
        Assert.True(labels.Contains("count") && labels.Contains("bump") && labels.Contains("limit"), string.Join(",", labels));
        Assert.True(!labels.Contains("__init__"), "dunder names are hidden until asked for");
        var cobj = Labels(cls + "C.|\n");
        Assert.True(cobj.Contains("bump") && cobj.Contains("limit"));
        Assert.True(!cobj.Contains("count"), "instance attributes do not belong to the class object");
        Assert.True(Labels(cls + "C()._|\n").Contains("__init__"));
    }

    [Fact]
    public void CompletesInheritedMembersAndModules()
    {
        var labels = Labels("class A:\n    def a1(self): ...\nclass B(A):\n    def b1(self): ...\nB().|\n");
        Assert.True(labels.Contains("a1") && labels.Contains("b1"));
        Assert.True(Labels("import collections\ncollections.|\n").Contains("OrderedDict"));
    }

    [Fact]
    public void CompletesNamesFromScopesBuiltinsAndKeywords()
    {
        var labels = Labels("alpha = 1\ndef f(beta):\n    gamma = 2\n    |\n");
        foreach (var want in new[] { "alpha", "beta", "gamma", "f", "len", "print", "return", "if" })
            Assert.True(labels.Contains(want), $"{want} missing");
        var filtered = Labels("alpha = 1\nalbum = 2\nbeta = 3\nal|\n");
        Assert.True(filtered.Contains("alpha") && filtered.Contains("album") && !filtered.Contains("beta"));
    }

    [Fact]
    public void ClassScopeNamesAreNotVisibleFromMethods()
    {
        var labels = Labels("class C:\n    hidden = 1\n    def m(self):\n        hid|\n");
        Assert.True(!labels.Contains("hidden"), "class attributes are not in scope inside methods");
        Assert.True(Labels("class C:\n    hidden = 1\n    hid|\n").Contains("hidden"));
    }

    [Fact]
    public void CompletionWorksOnIncompleteCode()
    {
        // `x.` alone is a syntax error; the member list must still come from the receiver's type
        Assert.True(Labels("def f(items: list[int]):\n    items.app|\n").Contains("append"));
        Assert.True(Labels("x = {}\nx.|").Contains("keys"));
    }

    // ------------------------------------------------------------ document symbols

    [Fact]
    public void DocumentSymbolsAreNested()
    {
        var svc = new LanguageService();
        svc.Open(Main, "LIMIT = 3\nclass C:\n    size = 1\n    def __init__(self): ...\n    def run(self): ...\ndef main():\n    def inner(): ...\n", 1);
        var syms = svc.DocumentSymbols(Main);
        Assert.Equal("LIMIT,C,main", string.Join(",", syms.Select(s => s.Name)));
        Assert.Equal(SymbolKind.Constant, syms[0].Kind);
        var c = syms[1];
        Assert.Equal(SymbolKind.Class, c.Kind);
        Assert.Equal("size,__init__,run", string.Join(",", c.Children.Select(s => s.Name)));
        Assert.Equal(SymbolKind.Constructor, c.Children[1].Kind);
        Assert.Equal(SymbolKind.Method, c.Children[2].Kind);
        Assert.Equal("inner", syms[2].Children.Single().Name);
        Assert.Equal(1, c.SelectionRange.Start.Line);
        Assert.Equal(6, c.SelectionRange.Start.Character);
    }

    // ------------------------------------------------------------ protocol

    sealed class Wire
    {
        public readonly List<JsonNode> Sent = new();
        public readonly PyMagic.Server.Server Server;
        public Wire() { Server = new PyMagic.Server.Server(new LanguageService(), Sent.Add); }

        public void Send(string json) => Server.Handle(JsonNode.Parse(json)!);
        public JsonNode Response(int id) => Sent.Last(m => m["id"]?.GetValue<int>() == id);
        public JsonNode? Notification(string method) => Sent.LastOrDefault(m => m["method"]?.GetValue<string>() == method);
    }

    [Fact]
    public void InitializeAdvertisesCapabilities()
    {
        var w = new Wire();
        w.Send("""{"jsonrpc":"2.0","id":1,"method":"initialize","params":{"rootUri":"file:///c:/proj"}}""");
        var caps = w.Response(1)["result"]!["capabilities"]!;
        Assert.True(caps["hoverProvider"]!.GetValue<bool>());
        Assert.True(caps["definitionProvider"]!.GetValue<bool>());
        Assert.Equal(".", caps["completionProvider"]!["triggerCharacters"]![0]!.GetValue<string>());
        Assert.Equal(1, caps["textDocumentSync"]!["change"]!.GetValue<int>());
    }

    [Fact]
    public void DidOpenPublishesDiagnostics()
    {
        var w = new Wire();
        w.Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/main.py","languageId":"python","version":3,"text":"print(nope)\n"}}}""");
        var note = w.Notification("textDocument/publishDiagnostics")!;
        Assert.Equal("file:///c:/proj/main.py", note["params"]!["uri"]!.GetValue<string>());
        Assert.Equal(3, note["params"]!["version"]!.GetValue<int>());
        var d = note["params"]!["diagnostics"]!.AsArray().Single()!;
        Assert.Contains("nope", d["message"]!.GetValue<string>());
        Assert.Equal(2, d["severity"]!.GetValue<int>());
        Assert.Equal(6, d["range"]!["start"]!["character"]!.GetValue<int>());

        w.Send("""{"jsonrpc":"2.0","method":"textDocument/didChange","params":{"textDocument":{"uri":"file:///c:/proj/main.py","version":4},"contentChanges":[{"text":"print(1)\n"}]}}""");
        var after = w.Sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.Equal(0, after["params"]!["diagnostics"]!.AsArray().Count);

        w.Send("""{"jsonrpc":"2.0","method":"textDocument/didClose","params":{"textDocument":{"uri":"file:///c:/proj/main.py"}}}""");
        var closed = w.Sent.Last(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.Equal(0, closed["params"]!["diagnostics"]!.AsArray().Count);
    }

    [Fact]
    public void RequestsReturnLspShapedResults()
    {
        var w = new Wire();
        w.Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/main.py","languageId":"python","version":1,"text":"def f(a: int) -> str:\n    return ''\nf(1)\n"}}}""");

        w.Send("""{"jsonrpc":"2.0","id":2,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"position":{"line":2,"character":0}}}""");
        var hover = w.Response(2)["result"]!;
        Assert.Equal("markdown", hover["contents"]!["kind"]!.GetValue<string>());
        Assert.Contains("def f(a: int) -> str", hover["contents"]!["value"]!.GetValue<string>());

        w.Send("""{"jsonrpc":"2.0","id":3,"method":"textDocument/definition","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"position":{"line":2,"character":0}}}""");
        var def = w.Response(3)["result"]!.AsArray().Single()!;
        Assert.Equal("file:///c:/proj/main.py", def["uri"]!.GetValue<string>());
        Assert.Equal(0, def["range"]!["start"]!["line"]!.GetValue<int>());

        w.Send("""{"jsonrpc":"2.0","id":4,"method":"textDocument/completion","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"position":{"line":2,"character":1}}}""");
        var items = w.Response(4)["result"]!["items"]!.AsArray();
        Assert.True(items.Any(i => i!["label"]!.GetValue<string>() == "f"));

        w.Send("""{"jsonrpc":"2.0","id":5,"method":"textDocument/documentSymbol","params":{"textDocument":{"uri":"file:///c:/proj/main.py"}}}""");
        Assert.Equal("f", w.Response(5)["result"]!.AsArray().Single()!["name"]!.GetValue<string>());

        w.Send("""{"jsonrpc":"2.0","id":6,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"position":{"line":0,"character":1}}}""");
        Assert.True(w.Response(6)["result"] is null || w.Response(6)["result"]!["contents"] != null);
    }

    [Fact]
    public void UnknownMethodsAndShutdown()
    {
        var w = new Wire();
        w.Send("""{"jsonrpc":"2.0","id":7,"method":"workspace/nonsense","params":{}}""");
        Assert.Equal(-32601, w.Response(7)["error"]!["code"]!.GetValue<int>());

        w.Send("""{"jsonrpc":"2.0","method":"$/cancelRequest","params":{"id":7}}""");   // unknown notifications are ignored
        w.Send("""{"jsonrpc":"2.0","id":8,"method":"shutdown"}""");
        Assert.True(w.Response(8)["result"] is null);
        w.Send("""{"jsonrpc":"2.0","method":"exit"}""");
        Assert.True(w.Server.Exited);
        Assert.Equal(0, w.Server.ExitCode);
    }

    [Fact]
    public void ExitWithoutShutdownIsAnError()
    {
        var w = new Wire();
        w.Send("""{"jsonrpc":"2.0","method":"exit"}""");
        Assert.Equal(1, w.Server.ExitCode);
    }

    [Fact]
    public void MessageFramingRoundTrips()
    {
        var wire = new MemoryStream();
        var writer = new MessageChannel(new MemoryStream(), wire);
        writer.Write(JsonNode.Parse("""{"jsonrpc":"2.0","id":1,"method":"héllo"}""")!);
        writer.Write(JsonNode.Parse("""{"jsonrpc":"2.0","method":"two"}""")!);

        string text = Encoding.UTF8.GetString(wire.ToArray());
        Assert.True(text.StartsWith("Content-Length: "), "missing header");

        wire.Position = 0;
        var reader = new MessageChannel(wire, new MemoryStream());
        Assert.Equal("héllo", reader.Read()!["method"]!.GetValue<string>());
        Assert.Equal("two", reader.Read()!["method"]!.GetValue<string>());
        Assert.True(reader.Read() is null, "end of input");
    }

    [Fact]
    public void SourceRootsDecideModuleNames()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_root_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        try
        {
            File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "pkg", "util.py"), "def helper() -> int:\n    return 1\n");
            var svc = new LanguageService();
            svc.SourceRoots.Add(root);
            string uri = LanguageService.UriOfPath(Path.Combine(root, "main.py"));
            var (text, pos) = Mark("from pkg.util import helper\nhel|per()\n");
            svc.Open(uri, text, 1);

            var def = svc.Definition(uri, pos).Single();
            Assert.True(def.Uri.EndsWith("pkg/util.py"), def.Uri);
            Assert.Equal(0, def.Range.Start.Line);
            Assert.Equal(0, svc.Diagnostics(uri).Count);
        }
        finally { Directory.Delete(root, true); }
    }
}
