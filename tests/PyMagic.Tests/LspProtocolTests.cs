using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The protocol itself (item 216): UTF-8 positions for a client that offers them, and cancelled requests.</summary>
public class LspProtocolTests
{
    const string Uri = "file:///c:/proj/main.py";

    static JsonNode Msg(string json) => JsonNode.Parse(json)!;

    [Fact]
    public void LineMapCountsBytesWhenAsked()
    {
        string text = "s = 'héllo'  # 😀 x\ny = 1\n";
        var lm = new LineMap(text);
        int x = text.IndexOf(" x", StringComparison.Ordinal) + 1;
        Assert.Equal(new Position(0, x), lm.PositionOf(x));          // UTF-16: é is one unit, 😀 two
        LineMap.Utf8.Value = true;
        try
        {
            Assert.Equal(new Position(0, x + 1 + 2), lm.PositionOf(x)); // UTF-8: é is two bytes, 😀 four
            Assert.Equal(x, lm.OffsetOf(new Position(0, x + 3)));
            Assert.Equal(new Position(1, 4), lm.PositionOf(text.IndexOf('1')));
            // a position inside a character's bytes goes to the end of that character
            int e = text.IndexOf('é');
            Assert.Equal(e + 1, lm.OffsetOf(new Position(0, e + 1)));
        }
        finally { LineMap.Utf8.Value = false; }
    }

    static (PyMagic.Server.Server Server, List<JsonNode> Sent) Start(bool utf8)
    {
        var sent = new List<JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), m => { lock (sent) sent.Add(m); });
        string caps = utf8 ? "{\"general\":{\"positionEncodings\":[\"utf-8\",\"utf-16\"]}}" : "{}";
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"capabilities\":" + caps + "}}"));
        return (server, sent);
    }

    [Fact]
    public void AClientThatOffersUtf8GetsIt()
    {
        var (server, sent) = Start(utf8: true);
        Assert.Equal("utf-8", sent.Last(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!["positionEncoding"]!.GetValue<string>());
        // `naïve` before the name: what the client says is byte 13 is the 12th character
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\",\"languageId\":\"python\",\"version\":1,\"text\":\"naïve = 1; total = naïve\\n\"}}}"));
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"textDocument/definition\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"},\"position\":{\"line\":0,\"character\":22}}}"));
        var loc = sent.Last(m => m["id"]?.GetValue<int>() == 2)["result"]!.AsArray()[0]!["range"]!;
        Assert.Equal(0, loc["start"]!["character"]!.GetValue<int>());
        Assert.Equal(6, loc["end"]!["character"]!.GetValue<int>());   // naïve is 5 characters, 6 bytes
        Assert.True(!LineMap.Utf8.Value);                              // nothing is left set on this thread
        // a client that does not offer it keeps UTF-16
        var (_, plain) = Start(utf8: false);
        Assert.Equal("utf-16", plain.Last(m => m["id"]?.GetValue<int>() == 1)["result"]!["capabilities"]!["positionEncoding"]!.GetValue<string>());
    }

    [Fact]
    public void ARequestCancelledWhileItRunsStops()
    {
        var (server, sent) = Start(utf8: false);
        // references of a function used in many modules: long enough to be cancelled while it goes through them
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///c:/proj/target.py\",\"languageId\":\"python\",\"version\":1,\"text\":\"def target(x):\\n    return x\\n\"}}}"));
        for (int i = 0; i < 1500; i++)
        {
            // each module calls it in many functions, whose types are worked out to find the calls
            string text = "from target import target\\n" + string.Concat(Enumerable.Range(0, 20).Select(f =>
                "\\n\\ndef use" + f + "(a):\\n    b = [target(a), target(a + 1)]\\n    if a:\\n        return target(b[0]) + use" + f + "(a - 1)\\n    return {k: target(k) for k in b}\\n"));
            server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"file:///c:/proj/m" + i + ".py\",\"languageId\":\"python\",\"version\":1,\"text\":\"" + text + "\"}}}"));
        }
        string References(int id) => "{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"textDocument/references\",\"params\":{\"textDocument\":{\"uri\":\"file:///c:/proj/target.py\"},\"position\":{\"line\":0,\"character\":5},\"context\":{\"includeDeclaration\":true}}}";
        // an edit of target.py: the next request works everything out again
        int version = 1;
        void Edit() => server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"file:///c:/proj/target.py\",\"version\":" + ++version + "},\"contentChanges\":[{\"text\":\"def target(x):\\n    return x  # " + version + "\\n\"}]}}"));
        // the whole run, for comparison
        server.Handle(Msg(References(40)));
        Edit();
        var whole = System.Diagnostics.Stopwatch.StartNew();
        server.Handle(Msg(References(41)));
        whole.Stop();
        lock (sent) Assert.True(sent.Single(m => m["id"]?.GetValue<int>() == 41)["result"]!.AsArray().Count > 1500 * 20);
        Edit();
        var worker = new Thread(() => server.Handle(Msg(References(42))), 256 * 1024 * 1024);
        worker.Start();
        var waited = System.Diagnostics.Stopwatch.StartNew();
        while (server.RunningRequest != "42" && worker.IsAlive && waited.ElapsedMilliseconds < 10000) Thread.SpinWait(50);
        Assert.True(server.RunningRequest == "42", "the request was not seen running");
        var stopping = System.Diagnostics.Stopwatch.StartNew();
        server.NoteCancel(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":42}}"));
        Assert.True(worker.Join(10000), "the cancelled request did not stop");
        stopping.Stop();
        // it stopped at the next module, long before the whole run would have ended
        Assert.True(stopping.ElapsedMilliseconds * 3 < whole.ElapsedMilliseconds, $"stopped after {stopping.ElapsedMilliseconds} ms; the whole run takes {whole.ElapsedMilliseconds} ms");
        JsonNode answer;
        lock (sent) answer = sent.Single(m => m["id"]?.GetValue<int>() == 42);
        Assert.Equal(-32800, answer["error"]!["code"]!.GetValue<int>());
        Assert.True(server.RunningRequest == null);
        Assert.True(PyMagic.Core.RequestCancellation.Current == default);   // nothing is left set on this thread
        // the next request is worked out as usual
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"id\":43,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"file:///c:/proj/target.py\"},\"position\":{\"line\":0,\"character\":5}}}"));
        lock (sent) Assert.True(sent.Single(m => m["id"]?.GetValue<int>() == 43)["result"] != null);
    }

    [Fact]
    public void ACancelledRequestIsAnsweredAsCancelled()
    {
        var (server, sent) = Start(utf8: false);
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\",\"languageId\":\"python\",\"version\":1,\"text\":\"x = 1\\n\"}}}"));
        // the request and its cancel wait in the queue together: the request is not worked out
        var inbox = new BlockingCollection<JsonNode?>
        {
            Msg("{\"jsonrpc\":\"2.0\",\"id\":7,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"},\"position\":{\"line\":0,\"character\":0}}}"),
            Msg("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":7}}"),
            Msg("{\"jsonrpc\":\"2.0\",\"id\":8,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"},\"position\":{\"line\":0,\"character\":0}}}"),
            null,
        };
        server.Pump(inbox);
        var seven = sent.Single(m => m["id"]?.GetValue<int>() == 7);
        Assert.Equal(-32800, seven["error"]!["code"]!.GetValue<int>());
        Assert.True(sent.Single(m => m["id"]?.GetValue<int>() == 8)["result"] != null);
        // a cancel noted by the reader while the request is queued behind others has the same effect
        server.NoteCancel(Msg("{\"jsonrpc\":\"2.0\",\"method\":\"$/cancelRequest\",\"params\":{\"id\":\"s9\"}}"));
        server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"id\":\"s9\",\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + Uri + "\"},\"position\":{\"line\":0,\"character\":0}}}"));
        Assert.Equal(-32800, sent.Last(m => m["id"]?.ToJsonString() == "\"s9\"")["error"]!["code"]!.GetValue<int>());
    }

    // ------------------------------------------------------------ answers whose places were counted without the line map (UTF-8 clients)

    static string Caps(bool utf8) => utf8 ? """{"general":{"positionEncodings":["utf-8","utf-16"]}}""" : "{}";

    static JsonNode Answer(List<JsonNode> sent, int id) { lock (sent) return sent.Last(m => m["id"]?.GetValue<int>() == id)["result"]!; }

    [Fact]
    public void ASymbolOfAFileNobodyOpenedIsPlacedInTheClientsUnits()
    {
        // workspace/symbol for a file only the index knows: the index keeps UTF-16 columns, a UTF-8 client gets bytes
        string root = Path.Combine(Path.GetTempPath(), "pm_lsp_ws_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "other.py"), "def café_tool():\n    pass\n");
            foreach (bool utf8 in new[] { true, false })
            {
                var sent = new List<JsonNode>();
                var service = new LanguageService();
                var server = new PyMagic.Server.Server(service, m => { lock (sent) sent.Add(m); });
                string rootUri = new System.Uri(root + Path.DirectorySeparatorChar).AbsoluteUri;
                server.Handle(Msg("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"rootUri\":\"" + rootUri + "\",\"capabilities\":" + Caps(utf8) + "}}"));
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (service.Index is not { Ready: true } && waited.ElapsedMilliseconds < 20000) Thread.Sleep(50);
                Assert.True(service.Index is { Ready: true }, "the index was not built");
                server.Handle(Msg("""{"jsonrpc":"2.0","id":2,"method":"workspace/symbol","params":{"query":"café_tool"}}"""));
                var range = Answer(sent, 2).AsArray().Single(s => s!["name"]!.GetValue<string>() == "café_tool")!["location"]!["range"]!;
                Assert.Equal(4, range["start"]!["character"]!.GetValue<int>());
                // café_tool is 9 characters, 10 bytes
                Assert.Equal(utf8 ? 14 : 13, range["end"]!["character"]!.GetValue<int>());
                Assert.True(!LineMap.Utf8.Value);
            }
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void AParameterOfASignatureIsPlacedInTheClientsUnits()
    {
        // signatureHelp: a parameter's place in the label counts bytes for a UTF-8 client (LSP: as positions do)
        foreach (bool utf8 in new[] { true, false })
        {
            var (server, sent) = Start(utf8);
            server.Handle(Msg("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/main.py","languageId":"python","version":1,"text":"def f(naïve, b):\n    pass\nf(1, \n"}}}"""));
            server.Handle(Msg("""{"jsonrpc":"2.0","id":2,"method":"textDocument/signatureHelp","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"position":{"line":2,"character":5}}}"""));
            var signature = Answer(sent, 2)["signatures"]![0]!;
            string label = signature["label"]!.GetValue<string>();
            var b = signature["parameters"]![1]!["label"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            int at = label.IndexOf(", b", StringComparison.Ordinal) + 2;
            int expected = utf8 ? System.Text.Encoding.UTF8.GetByteCount(label[..at]) : at;
            Assert.Equal(expected, b[0]);
            Assert.Equal(expected + 1, b[1]);
            Assert.True(!utf8 || expected == at + 1, label);
        }
    }

    [Fact]
    public void ASemanticTokensLengthIsInTheClientsUnits()
    {
        // the length of a token counts bytes for a UTF-8 client, as its column does
        foreach (bool utf8 in new[] { true, false })
        {
            var (server, sent) = Start(utf8);
            server.Handle(Msg("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/main.py","languageId":"python","version":1,"text":"def naïve():\n    pass\n"}}}"""));
            server.Handle(Msg("""{"jsonrpc":"2.0","id":2,"method":"pymagic/semanticTokens","params":{"textDocument":{"uri":"file:///c:/proj/main.py"}}}"""));
            var data = Answer(sent, 2)["data"]!.AsArray().Select(x => x!.GetValue<int>()).ToArray();
            // the function's name: line 0, column 4, 5 characters or 6 bytes
            int i = Enumerable.Range(0, data.Length / 5).Select(k => k * 5).First(k => data[k] == 0 && data[k + 1] == 4);
            Assert.Equal(utf8 ? 6 : 5, data[i + 2]);
        }
    }
}
