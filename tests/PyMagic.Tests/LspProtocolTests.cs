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
}
