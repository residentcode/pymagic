using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The HTTP client's WEBSOCKET and GRPC requests (PyCharm's newer kinds of request): the protobuf of gRPC written from JSON and read back by
/// descriptors as reflection gives them, a WebSocket talk against a server of the test's own, and gRPC calls against a real Python grpcio
/// server with reflection (only when PYMAGIC_TEST_GRPC_PYTHON names a Python with grpcio, grpcio-reflection and grpcio-tools).
/// </summary>
public class HttpKindsTests
{
    // ------------------------------------------------------------ descriptors written by hand, as a server's reflection sends them

    static byte[] Msg(params byte[][] fields) => fields.SelectMany(x => x).ToArray();
    static byte[] Key(int number, int wire) => Varint(((ulong)number << 3) | (uint)wire);
    static byte[] Varint(ulong v)
    {
        var list = new List<byte>();
        while (v >= 0x80) { list.Add((byte)(v | 0x80)); v >>= 7; }
        list.Add((byte)v);
        return list.ToArray();
    }
    static byte[] Str(int number, string s) => Bytes(number, Encoding.UTF8.GetBytes(s));
    static byte[] Bytes(int number, byte[] data) => Msg(Key(number, 2), Varint((ulong)data.Length), data);
    static byte[] Int(int number, long v) => Msg(Key(number, 0), Varint((ulong)v));

    /// <summary>FieldDescriptorProto: name 1, number 3, label 4 (1 optional, 3 repeated), type 5, type_name 6, json_name 10.</summary>
    static byte[] FieldDesc(string name, int number, int type, bool repeated = false, string? typeName = null, string? json = null) =>
        Msg(Str(1, name), Int(3, number), Int(4, repeated ? 3 : 1), Int(5, type), typeName != null ? Str(6, typeName) : Array.Empty<byte>(), json != null ? Str(10, json) : Array.Empty<byte>());

    static byte[] DemoFile()
    {
        var mapEntry = Msg(Str(1, "MetaEntry"), Bytes(2, FieldDesc("key", 1, 9)), Bytes(2, FieldDesc("value", 2, 5)), Bytes(7, Int(7, 1)));
        var item = Msg(Str(1, "Item"),
            Bytes(2, FieldDesc("name", 1, 9)),
            Bytes(2, FieldDesc("count", 2, 5)),
            Bytes(2, FieldDesc("big_number", 3, 3, json: "bigNumber")),
            Bytes(2, FieldDesc("tags", 4, 9, repeated: true)),
            Bytes(2, FieldDesc("scores", 5, 5, repeated: true)),
            Bytes(2, FieldDesc("kind", 6, 14, typeName: ".demo.Kind")),
            Bytes(2, FieldDesc("meta", 7, 11, repeated: true, typeName: ".demo.Item.MetaEntry")),
            Bytes(2, FieldDesc("child", 8, 11, typeName: ".demo.Item")),
            Bytes(2, FieldDesc("data", 9, 12)),
            Bytes(2, FieldDesc("ratio", 10, 1)),
            Bytes(2, FieldDesc("flag", 11, 8)),
            Bytes(2, FieldDesc("neg", 12, 17)),
            Bytes(3, mapEntry));
        var kind = Msg(Str(1, "Kind"), Bytes(2, Msg(Str(1, "KIND_NONE"), Int(2, 0))), Bytes(2, Msg(Str(1, "KIND_BIG"), Int(2, 1))));
        var service = Msg(Str(1, "Store"), Bytes(2, Msg(Str(1, "Put"), Str(2, ".demo.Item"), Str(3, ".demo.Item"))));
        return Msg(Str(1, "demo.proto"), Str(2, "demo"), Bytes(4, item), Bytes(5, kind), Bytes(6, service));
    }

    [Fact]
    public void AMessageIsWrittenFromJsonAndReadBackByTheDescriptors()
    {
        var pool = new Protobuf.Pool();
        pool.AddFile(DemoFile());
        Assert.True(pool.Services.ContainsKey("demo.Store"), "the service");
        Assert.True(pool.Messages["demo.Item.MetaEntry"].MapEntry, "the map's entry");
        string json = "{\"name\":\"box\",\"count\":-3,\"bigNumber\":\"9007199254740993\",\"tags\":[\"a\",\"b\"],\"scores\":[1,300,-2],\"kind\":\"KIND_BIG\","
            + "\"meta\":{\"x\":1,\"y\":2},\"child\":{\"name\":\"inner\",\"flag\":true},\"data\":\"AAEC\",\"ratio\":0.5,\"flag\":true,\"neg\":-7}";
        var bytes = Protobuf.Encode(pool, "demo.Item", JsonNode.Parse(json));
        var back = Protobuf.Decode(pool, "demo.Item", bytes);
        Assert.Equal(JsonNode.Parse(json)!.ToJsonString(), back.ToJsonString());
        // the original field names are taken too, and a number for an enum
        var named = Protobuf.Decode(pool, "demo.Item", Protobuf.Encode(pool, "demo.Item", JsonNode.Parse("{\"big_number\":5,\"kind\":1}")));
        Assert.Equal("{\"bigNumber\":\"5\",\"kind\":\"KIND_BIG\"}", named.ToJsonString());
    }

    [Fact]
    public void JsonThatDoesNotFitTheMessageSaysWhy()
    {
        var pool = new Protobuf.Pool();
        pool.AddFile(DemoFile());
        string Why(string json)
        {
            try { Protobuf.Encode(pool, "demo.Item", JsonNode.Parse(json)); return "(no error)"; }
            catch (FormatException e) { return e.Message; }
        }
        string unknown = Why("{\"nme\":\"x\"}");
        Assert.True(unknown.StartsWith("Item has no field nme (it has name, count, bigNumber"), unknown);
        Assert.Equal("Item.count is a whole number", Why("{\"count\":\"many\"}"));
        Assert.Equal("Item.tags is repeated: a JSON array", Why("{\"tags\":\"a\"}"));
        Assert.Equal("Item.kind has no value KIND_HUGE (it has KIND_NONE, KIND_BIG)", Why("{\"kind\":\"KIND_HUGE\"}"));
        Assert.Equal("Item.name is text: a JSON string", Why("{\"name\":5}"));
    }

    // ------------------------------------------------------------ WebSocket

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    [Fact]
    public void AWebSocketRequestSendsItsMessagesAndKeepsTheTalk()
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        string? protocolAsked = null, tokenSent = null;
        var serving = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            protocolAsked = ctx.Request.Headers["Sec-WebSocket-Protocol"];
            tokenSent = ctx.Request.Headers["X-Token"];
            var wsc = await ctx.AcceptWebSocketAsync("chat");
            var ws = wsc.WebSocket;
            // a greeting a moment after the connection, then each message echoed; "bye" ends it
            await Task.Delay(300);
            await ws.SendAsync(Encoding.UTF8.GetBytes("welcome"), WebSocketMessageType.Text, true, CancellationToken.None);
            var buffer = new byte[4096];
            while (ws.State == WebSocketState.Open)
            {
                var r = await ws.ReceiveAsync(buffer, CancellationToken.None);
                if (r.MessageType == WebSocketMessageType.Close) break;
                string text = Encoding.UTF8.GetString(buffer, 0, r.Count);
                if (text == "bye") { await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "see you", CancellationToken.None); break; }
                await ws.SendAsync(Encoding.UTF8.GetBytes("echo: " + text), WebSocketMessageType.Text, true, CancellationToken.None);
            }
        });
        var result = WebSocketRequests.Run(new Uri($"ws://127.0.0.1:{port}/ws"),
            new[] { new KeyValuePair<string, string>("X-Token", "t1"), new KeyValuePair<string, string>("Sec-WebSocket-Protocol", "chat") },
            new[] { new WebSocketRequests.Message("{\"hello\": 1}", Wait: true), new WebSocketRequests.Message("two", Wait: true), new WebSocketRequests.Message("bye", Wait: true) },
            quiet: TimeSpan.FromMilliseconds(800), waitLimit: TimeSpan.FromSeconds(5), limit: TimeSpan.FromSeconds(20)).GetAwaiter().GetResult();
        serving.Wait(5000);
        // wait-for-server before the first message: the greeting came first
        Assert.Equal("received welcome | sent {\"hello\": 1} | received echo: {\"hello\": 1} | sent two | received echo: two | sent bye",
            string.Join(" | ", result.Transcript.Select(l => $"{l.Dir} {l.Text}")));
        Assert.True(result.Closed, "the server closed it");
        Assert.Equal("see you", result.CloseDescription);
        Assert.Equal("chat", protocolAsked);
        Assert.Equal("t1", tokenSent);
        Assert.Equal("chat", result.Headers.FirstOrDefault(h => h.Key.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase)).Value);
    }

    // ------------------------------------------------------------ WebSocket kept open, and GraphQL subscriptions (LiveSockets)

    /// <summary>A server of one WebSocket connection on a free port, with the subprotocol it accepts; <paramref name="talk"/> is its side.</summary>
    static (int Port, Task Serving, HttpListener Listener) WsServer(Func<string?, string?> protocol, Func<WebSocket, Task> talk)
    {
        int port = FreePort();
        var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var serving = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var wsc = await ctx.AcceptWebSocketAsync(protocol(ctx.Request.Headers["Sec-WebSocket-Protocol"]));
            // the other side may close without the handshake once it has what it wanted
            try { await talk(wsc.WebSocket); } catch (WebSocketException) { }
        });
        return (port, serving, listener);
    }

    static async Task<string> Read(WebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var whole = new MemoryStream();
        WebSocketReceiveResult r;
        do { r = await ws.ReceiveAsync(buffer, CancellationToken.None); whole.Write(buffer, 0, r.Count); } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
        return r.MessageType == WebSocketMessageType.Close ? "<close>" : Encoding.UTF8.GetString(whole.ToArray());
    }
    static Task Say(WebSocket ws, string text) => ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, CancellationToken.None);

    /// <summary>PyMagic's side (the page's socket to LiveSockets.Relay) on another port; the test drives the page's side.</summary>
    static async Task<List<JsonNode>> Live(JsonObject start, Func<ClientWebSocket, List<JsonNode>, Task> drive, Func<WebSocket, Task>? relay = null)
    {
        int port = FreePort();
        using var listener = new HttpListener();
        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        listener.Start();
        var relaying = Task.Run(async () =>
        {
            var ctx = await listener.GetContextAsync();
            var wsc = await ctx.AcceptWebSocketAsync(null);
            await (relay ?? (ws => LiveSockets.Relay(ws, LiveSockets.StartOf(start)!, CancellationToken.None)))(wsc.WebSocket);
            try { await wsc.WebSocket.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); } catch (WebSocketException) { }
        });
        using var page = new ClientWebSocket();
        await page.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/"), CancellationToken.None);
        var seen = new List<JsonNode>();
        await drive(page, seen);
        // the page answers the close, as a browser does
        try { await page.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5)); } catch (Exception e) when (e is WebSocketException or TimeoutException) { }
        await relaying.WaitAsync(TimeSpan.FromSeconds(15));
        return seen;
    }

    /// <summary>The page's messages until one of this type (it is kept with the rest).</summary>
    static async Task<JsonNode> Until(ClientWebSocket page, List<JsonNode> seen, string type, Func<JsonNode, bool>? also = null)
    {
        while (true)
        {
            string text;
            try { text = await Read(page).WaitAsync(TimeSpan.FromSeconds(15)); }
            catch (TimeoutException) { throw new AssertionException($"no {type} in 15 s; saw {string.Join(" ", seen.Select(s => s.ToJsonString()))}"); }
            if (text == "<close>") throw new AssertionException($"closed before {type}; saw {string.Join(" ", seen.Select(s => s.ToJsonString()))}");
            var m = JsonNode.Parse(text)!;
            seen.Add(m);
            if (m["type"]!.GetValue<string>() == type && (also == null || also(m))) return m;
        }
    }

    [Fact]
    public void AWebSocketKeptOpenSendsWhatIsTypedUntilItIsClosed()
    {
        var (port, serving, listener) = WsServer(_ => null, async ws =>
        {
            await Say(ws, "welcome");
            while (ws.State == WebSocketState.Open)
            {
                string text = await Read(ws);
                if (text == "<close>") { await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None); break; }
                await Say(ws, "echo: " + text);
            }
        });
        using (listener)
        {
            var start = new JsonObject { ["url"] = $"ws://127.0.0.1:{port}/", ["messages"] = new JsonArray(new JsonObject { ["text"] = "first", ["wait"] = true }) };
            var seen = Live(start, async (page, seen) =>
            {
                await Until(page, seen, "open");
                await Until(page, seen, "received", m => m["text"]!.GetValue<string>() == "echo: first");
                // typed while it is open
                await Say(page, """{"type":"send","text":"typed later"}""");
                await Until(page, seen, "received", m => m["text"]!.GetValue<string>() == "echo: typed later");
                await Say(page, """{"type":"close"}""");
                await Until(page, seen, "closed");
            }).GetAwaiter().GetResult();
            serving.Wait(5000);
            Assert.Equal("received welcome | sent first | received echo: first | sent typed later | received echo: typed later",
                string.Join(" | ", seen.Where(m => m["type"]!.GetValue<string>() is "sent" or "received").Select(m => $"{m["type"]} {m["text"]}")));
        }
    }

    [Fact]
    public void AGraphQlSubscriptionShowsItsEventsInEitherProtocol()
    {
        foreach (string protocol in new[] { "graphql-transport-ws", "graphql-ws" })
        {
            bool legacy = protocol == "graphql-ws";
            string? asked = null, query = null, stopped = null;
            var (port, serving, listener) = WsServer(p => { asked = p; return protocol; }, async ws =>
            {
                Assert.Equal("connection_init", JsonNode.Parse(await Read(ws))!["type"]!.GetValue<string>());
                await Say(ws, """{"type":"connection_ack"}""");
                var sub = JsonNode.Parse(await Read(ws))!;
                Assert.Equal(legacy ? "start" : "subscribe", sub["type"]!.GetValue<string>());
                query = sub["payload"]!["query"]!.GetValue<string>() + " " + sub["payload"]!["variables"]!.ToJsonString();
                if (!legacy) await Say(ws, """{"type":"ping"}""");
                for (int i = 1; i <= 2; i++)
                    await Say(ws, new JsonObject { ["id"] = "1", ["type"] = legacy ? "data" : "next", ["payload"] = new JsonObject { ["data"] = new JsonObject { ["tick"] = i } } }.ToJsonString());
                if (legacy) { await Say(ws, """{"id":"1","type":"complete"}"""); await Read(ws); return; }
                // the pong, then Stop's complete
                string pong = await Read(ws);
                Assert.Equal("pong", JsonNode.Parse(pong)!["type"]!.GetValue<string>());
                stopped = JsonNode.Parse(await Read(ws))!["type"]!.GetValue<string>();
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None);
            });
            using (listener)
            {
                // an http:// address is the subscription's ws:// one
                var start = new JsonObject { ["url"] = $"http://127.0.0.1:{port}/graphql", ["graphql"] = new JsonObject { ["query"] = "subscription { tick }", ["variables"] = new JsonObject { ["every"] = 1 } } };
                var seen = Live(start, async (page, seen) =>
                {
                    var open = await Until(page, seen, "open");
                    Assert.Equal(protocol, open["protocol"]!.GetValue<string>());
                    await Until(page, seen, "received", m => m["text"]!.GetValue<string>().Contains("\"tick\": 2"));
                    if (!legacy) await Say(page, """{"type":"close"}""");
                    await Until(page, seen, "closed");
                }).ContinueWith(t => t.IsFaulted ? throw new AssertionException(protocol + ": " + t.Exception!.InnerException) : t.Result).GetAwaiter().GetResult();
                serving.Wait(5000);
                Assert.Equal("subscription { tick } {\"every\":1}", query);
                Assert.Contains("graphql-transport-ws", asked ?? "");
                var events = seen.Where(m => m["type"]!.GetValue<string>() == "received").Select(m => m["text"]!.GetValue<string>().Replace("\r", "").Replace("\n", "").Replace(" ", "")).ToList();
                Assert.Equal("{\"data\":{\"tick\":1}}|{\"data\":{\"tick\":2}}" + (legacy ? "|(thesubscriptioniscomplete)" : ""), string.Join("|", events));
                if (!legacy) Assert.Equal("complete", stopped);
            }
        }
    }

    // ------------------------------------------------------------ gRPC against Python's grpcio

    const string Proto = """
        syntax = "proto3";
        package demo;
        service Greeter {
          rpc SayHello (HelloRequest) returns (HelloReply);
          rpc Count (CountRequest) returns (stream Number);
          rpc Fail (HelloRequest) returns (HelloReply);
          rpc Sum (stream Number) returns (Number);
          rpc Chat (stream HelloRequest) returns (stream HelloReply);
        }
        enum Mood { MOOD_UNKNOWN = 0; HAPPY = 1; }
        message HelloRequest { string name = 1; int32 times = 2; repeated string tags = 3; map<string, int64> extra = 4; Mood mood = 5; }
        message HelloReply { string message = 1; int64 total = 2; repeated string tags = 3; Mood mood = 4; map<string, int64> extra = 5; }
        message CountRequest { int32 up_to = 1; }
        message Number { int32 value = 1; }
        """;

    const string Server = """
        import sys, os, importlib
        from concurrent import futures
        here = os.path.dirname(os.path.abspath(__file__))
        sys.path.insert(0, here)
        from grpc_tools import protoc
        assert protoc.main(["protoc", "-I" + here, "--python_out=" + here, "--grpc_python_out=" + here, os.path.join(here, "demo.proto")]) == 0
        import grpc
        from grpc_reflection.v1alpha import reflection
        import demo_pb2, demo_pb2_grpc

        class Greeter(demo_pb2_grpc.GreeterServicer):
            def SayHello(self, request, context):
                context.set_trailing_metadata((("x-served-by", "test"),))
                return demo_pb2.HelloReply(message=("hello " + request.name + " ") * request.times, total=2**40 + request.times,
                                           tags=list(request.tags) + ["seen"], mood=request.mood, extra=dict(request.extra))
            def Count(self, request, context):
                for i in range(1, request.up_to + 1):
                    yield demo_pb2.Number(value=i)
            def Fail(self, request, context):
                context.abort(grpc.StatusCode.NOT_FOUND, "no one called " + request.name)
            def Sum(self, requests, context):
                return demo_pb2.Number(value=sum(r.value for r in requests))
            def Chat(self, requests, context):
                for r in requests:
                    yield demo_pb2.HelloReply(message="hi " + r.name)

        server = grpc.server(futures.ThreadPoolExecutor(max_workers=4))
        demo_pb2_grpc.add_GreeterServicer_to_server(Greeter(), server)
        reflection.enable_server_reflection((demo_pb2.DESCRIPTOR.services_by_name["Greeter"].full_name, reflection.SERVICE_NAME), server)
        port = server.add_insecure_port("127.0.0.1:0")
        server.start()
        print("ready", port, flush=True)
        server.wait_for_termination()
        """;

    [Fact]
    public void GrpcCallsGoThroughTheServersReflection()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_GRPC_PYTHON") is not { Length: > 0 } python) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_grpc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "demo.proto"), Proto);
        File.WriteAllText(Path.Combine(dir, "server.py"), Server);
        var psi = new ProcessStartInfo(python, $"-u \"{Path.Combine(dir, "server.py")}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var server = Process.Start(psi)!;
        try
        {
            var ready = server.StandardOutput.ReadLineAsync();
            if (!ready.Wait(60000) || ready.Result is not { } line || !line.StartsWith("ready "))
                throw new AssertionException("the gRPC server did not start: " + server.StandardError.ReadToEnd());
            string address = "http://127.0.0.1:" + line.Split(' ')[1];
            Assert.True(int.Parse(line.Split(' ')[1]) > 0, "a port");

            var r = GrpcRequests.Call(address, "demo.Greeter", "SayHello",
                "{\"name\": \"Ada\", \"times\": 2, \"tags\": [\"x\"], \"extra\": {\"k\": \"12\"}, \"mood\": \"HAPPY\"}",
                new[] { new KeyValuePair<string, string>("x-request-id", "r1") }).GetAwaiter().GetResult();
            Assert.Equal(0, r.Status);
            Assert.Equal("OK", r.StatusName);
            var body = JsonNode.Parse(r.Body)!;
            Assert.Equal("hello Ada hello Ada ", body["message"]!.GetValue<string>());
            Assert.Equal("1099511627778", body["total"]!.GetValue<string>());   // int64 as text, as proto3 JSON writes it
            Assert.Equal("[\"x\",\"seen\"]", body["tags"]!.ToJsonString());
            Assert.Equal("HAPPY", body["mood"]!.GetValue<string>());
            Assert.Equal("{\"k\":\"12\"}", body["extra"]!.ToJsonString());
            Assert.Equal("test", r.Headers["x-served-by"]);

            // server streaming: each message the server sends
            var stream = GrpcRequests.Call(address, "demo.Greeter", "Count", "{\"upTo\": 3}", Array.Empty<KeyValuePair<string, string>>()).GetAwaiter().GetResult();
            Assert.Equal(0, stream.Status);
            Assert.Equal("[{\"value\":1},{\"value\":2},{\"value\":3}]", JsonNode.Parse(stream.Body)!.ToJsonString());

            // an error of the service: its code and message
            var failed = GrpcRequests.Call(address, "demo.Greeter", "Fail", "{\"name\": \"Bob\"}", Array.Empty<KeyValuePair<string, string>>()).GetAwaiter().GetResult();
            Assert.Equal(5, failed.Status);
            Assert.Equal("NOT_FOUND", failed.StatusName);
            Assert.Equal("no one called Bob", failed.Message);

            // kept open (GrpcLive): a client-streaming call's messages, the request's and one typed, then Close and its one answer
            JsonObject Start(string method, string body) => new() { ["address"] = address, ["service"] = "demo.Greeter", ["method"] = method, ["body"] = body };
            Func<WebSocket, Task> Relay(JsonObject start) => ws => GrpcLive.Relay(ws, GrpcLive.StartOf(start)!, null, CancellationToken.None);
            var sum = Start("Sum", "[{\"value\": 1}, {\"value\": 2}]");
            var summed = Live(sum, async (page, seen) =>
            {
                Assert.Equal("client", (await Until(page, seen, "open"))["streaming"]!.GetValue<string>());
                await Say(page, """{"type":"send","text":"{\"value\": 3}"}""");
                await Until(page, seen, "sent", x => x["text"]!.GetValue<string>() == "{\"value\":3}");
                await Say(page, """{"type":"send","text":"{\"valu\": 4}"}""");
                Assert.Contains("Number has no field valu", (await Until(page, seen, "note"))["text"]!.GetValue<string>());
                await Say(page, """{"type":"close"}""");
                await Until(page, seen, "closed");
            }, Relay(sum)).GetAwaiter().GetResult();
            Assert.Equal("sent sent sent note received closed", string.Join(" ", summed.Where(x => x["type"]!.GetValue<string>() != "open").Select(x => x["type"]!.GetValue<string>())));
            Assert.Equal("{\"value\":6}", JsonNode.Parse(summed.First(x => x["type"]!.GetValue<string>() == "received")["text"]!.GetValue<string>())!.ToJsonString());
            Assert.Equal(0, summed.Last()["status"]!.GetValue<int>());

            // bidirectional: each answer comes before the next message is typed
            var chat = Start("Chat", "{\"name\": \"Ada\"}");
            var chatted = Live(chat, async (page, seen) =>
            {
                Assert.Equal("bidi", (await Until(page, seen, "open"))["streaming"]!.GetValue<string>());
                await Until(page, seen, "received", x => x["text"]!.GetValue<string>().Contains("hi Ada"));
                await Say(page, """{"type":"send","text":"{\"name\": \"Bob\"}"}""");
                await Until(page, seen, "received", x => x["text"]!.GetValue<string>().Contains("hi Bob"));
                await Say(page, """{"type":"close"}""");
                await Until(page, seen, "closed");
            }, Relay(chat)).GetAwaiter().GetResult();
            Assert.Equal("open sent received sent received closed", string.Join(" ", chatted.Select(x => x["type"]!.GetValue<string>())));
            Assert.Equal("OK", chatted.Last()["reason"]!.GetValue<string>());

            // server streaming shows each message as it comes; a unary method answers once, as /api/grpc does
            var count = Start("Count", "{\"upTo\": 3}");
            var counted = Live(count, async (page, seen) => await Until(page, seen, "closed"), Relay(count)).GetAwaiter().GetResult();
            Assert.Equal("server", counted[0]["streaming"]!.GetValue<string>());
            Assert.Equal(3, counted.Count(x => x["type"]!.GetValue<string>() == "received"));
            var hello = Start("SayHello", "{\"name\": \"Cy\", \"times\": 1}");
            var once = Live(hello, async (page, seen) => await Until(page, seen, "result"), Relay(hello)).GetAwaiter().GetResult();
            Assert.Contains("hello Cy", once.Last()["body"]!.GetValue<string>());
            var fail = Start("Fail", "{\"name\": \"Dee\"}");
            Assert.Equal(5, Live(fail, async (page, seen) => await Until(page, seen, "result"), Relay(fail)).GetAwaiter().GetResult().Last()["status"]!.GetValue<int>());

            string Why(string service, string method, string json)
            {
                try { GrpcRequests.Call(address, service, method, json, Array.Empty<KeyValuePair<string, string>>()).GetAwaiter().GetResult(); return "(no error)"; }
                catch (ServiceException e) { return e.Message; }
            }
            Assert.Equal("demo.Greeter has no method Hello (it has SayHello, Count, Fail, Sum, Chat)", Why("demo.Greeter", "Hello", "{}"));
            Assert.Equal("HelloRequest has no field nam (it has name, times, tags, extra, mood)", Why("demo.Greeter", "SayHello", "{\"nam\": \"x\"}"));
            string noService = Why("demo.Nothing", "Go", "{}");
            Assert.True(noService.StartsWith("gRPC reflection answered NOT_FOUND"), noService);
            string notJson = Why("demo.Greeter", "SayHello", "{name");
            Assert.True(notJson.StartsWith("the body is not JSON"), notJson);
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* ended */ }
            try { Directory.Delete(dir, true); } catch (IOException) { /* in use a moment longer */ }
        }
    }
    const string WellKnownProto = """
        syntax = "proto3";
        package wk;

        import "google/protobuf/timestamp.proto";
        import "google/protobuf/duration.proto";
        import "google/protobuf/wrappers.proto";
        import "google/protobuf/struct.proto";
        import "google/protobuf/field_mask.proto";
        import "google/protobuf/empty.proto";

        // a clock that answers what it is sent, its duration doubled and its label in capitals
        service Clock {
          rpc Echo (Stamp) returns (Stamp);
          rpc Nothing (google.protobuf.Empty) returns (google.protobuf.Empty) {}
        }

        message Stamp {
          google.protobuf.Timestamp at = 1;
          google.protobuf.Duration took = 2;
          google.protobuf.Int32Value maybe = 3;
          google.protobuf.StringValue label = 4;
          google.protobuf.Struct extra = 5;
          google.protobuf.FieldMask mask = 6;
          google.protobuf.Value anything = 7;
          map<string, Inner> inners = 8;
          repeated Level levels = 9 [json_name = "lv"];
          message Inner {
            oneof pick { string text = 1; int32 n = 2; }
          }
          enum Level { LOW = 0; HIGH = 1; }
        }
        """;

    const string WellKnownJson = """{"at":"2024-05-01T10:00:00.250Z","took":"1.500s","maybe":0,"label":"tick","extra":{"a":1,"b":[true,null,"x"],"c":{"d":2.5}},"mask":"name,address.cityName","anything":"text","inners":{"one":{"text":"t"},"two":{"n":2}},"lv":["HIGH","LOW"]}""";

    [Fact]
    public void ProtoFilesAndWellKnownTypesAreReadAndWrittenInTheirJsonForms()
    {
        var pool = ProtoFiles.PoolOf(WellKnownProto);
        Assert.Equal("Echo:wk.Stamp>wk.Stamp Nothing:google.protobuf.Empty>google.protobuf.Empty", string.Join(" ", pool.Services["wk.Clock"].Methods.Select(m => $"{m.Name}:{m.Input}>{m.Output}")));
        Assert.True(pool.Messages["wk.Stamp.InnersEntry"].MapEntry, "the map's entry");
        Assert.True(pool.Enums.ContainsKey("wk.Stamp.Level"), "the nested enum");
        Assert.Equal("wk.Stamp.Level lv", pool.Messages["wk.Stamp"].Fields.First(f => f.Number == 9) is var lv ? $"{lv.TypeName} {lv.JsonName}" : "");
        // each type there and back: the same JSON
        var bytes = Protobuf.Encode(pool, "wk.Stamp", JsonNode.Parse(WellKnownJson));
        Assert.Equal(JsonNode.Parse(WellKnownJson)!.ToJsonString(), Protobuf.DecodeMessage(pool, "wk.Stamp", bytes)!.ToJsonString());
        // a Duration below a second and negative, a Timestamp with an offset, nanoseconds of 6 digits
        Assert.Equal("{\"at\":\"2024-05-01T08:00:00.000001Z\",\"took\":\"-0.500s\"}",
            Protobuf.DecodeMessage(pool, "wk.Stamp", Protobuf.Encode(pool, "wk.Stamp", JsonNode.Parse("{\"at\":\"2024-05-01T10:00:00.000001+02:00\",\"took\":\"-0.500s\"}")))!.ToJsonString());
        string Why(string json)
        {
            try { Protobuf.Encode(pool, "wk.Stamp", JsonNode.Parse(json)); return "(no error)"; }
            catch (FormatException e) { return e.Message; }
        }
        Assert.Contains("RFC 3339", Why("{\"at\":\"yesterday\"}"));
        Assert.Contains("\"1.5s\"", Why("{\"took\":\"90 minutes\"}"));
        // a .proto file that is not one
        var errors = new List<string>();
        string dir = Path.Combine(Path.GetTempPath(), "pm_protos_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, "protos"));
        Directory.CreateDirectory(Path.Combine(dir, "node_modules", "x"));
        File.WriteAllText(Path.Combine(dir, "protos", "wk.proto"), WellKnownProto);
        File.WriteAllText(Path.Combine(dir, "protos", "broken.proto"), "message { oops");
        File.WriteAllText(Path.Combine(dir, "node_modules", "x", "other.proto"), "service Other {}");
        try
        {
            var found = ProtoFiles.Pool(dir, errors);
            Assert.True(found.Services.ContainsKey("wk.Clock") && !found.Services.ContainsKey("Other"), string.Join(",", found.Services.Keys));
            Assert.True(errors.Count == 1 && errors[0].StartsWith("protos" + Path.DirectorySeparatorChar + "broken.proto"), string.Join(" | ", errors));
        }
        finally { Directory.Delete(dir, true); }
    }

    const string WellKnownServer = """
        import sys, os
        from concurrent import futures
        here = os.path.dirname(os.path.abspath(__file__))
        sys.path.insert(0, here)
        from grpc_tools import protoc
        import grpc_tools
        include = os.path.join(os.path.dirname(grpc_tools.__file__), "_proto")
        assert protoc.main(["protoc", "-I" + here, "-I" + include, "--python_out=" + here, "--grpc_python_out=" + here, os.path.join(here, "protos", "wk.proto")]) == 0
        import grpc
        from protos import wk_pb2, wk_pb2_grpc

        class Clock(wk_pb2_grpc.ClockServicer):
            def Echo(self, request, context):
                reply = wk_pb2.Stamp()
                reply.CopyFrom(request)
                reply.took.FromTimedelta(request.took.ToTimedelta() * 2)
                reply.label.value = request.label.value.upper()
                return reply
            def Nothing(self, request, context):
                return request

        # no reflection; every answer compressed with gzip
        server = grpc.server(futures.ThreadPoolExecutor(max_workers=4), compression=grpc.Compression.Gzip)
        wk_pb2_grpc.add_ClockServicer_to_server(Clock(), server)
        port = server.add_insecure_port("127.0.0.1:0")
        server.start()
        print("ready", port, flush=True)
        server.wait_for_termination()
        """;

    [Fact]
    public void AServerWithoutReflectionIsDescribedByTheProjectsProtoFilesAndItsAnswersAreDecompressed()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_GRPC_PYTHON") is not { Length: > 0 } python) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_grpcwk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "protos"));
        File.WriteAllText(Path.Combine(dir, "protos", "wk.proto"), WellKnownProto);
        File.WriteAllText(Path.Combine(dir, "protos", "__init__.py"), "");
        File.WriteAllText(Path.Combine(dir, "server.py"), WellKnownServer);
        var psi = new ProcessStartInfo(python, $"-u \"{Path.Combine(dir, "server.py")}\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var server = Process.Start(psi)!;
        try
        {
            var ready = server.StandardOutput.ReadLineAsync();
            if (!ready.Wait(60000) || ready.Result is not { } line || !line.StartsWith("ready "))
                throw new AssertionException("the gRPC server did not start: " + server.StandardError.ReadToEnd());
            string address = "http://127.0.0.1:" + line.Split(' ')[1];

            // without the project's files there is nothing to describe it by
            try { GrpcRequests.Call(address, "wk.Clock", "Echo", "{}", Array.Empty<KeyValuePair<string, string>>()).GetAwaiter().GetResult(); throw new AssertionException("no error"); }
            catch (ServiceException e) { Assert.Contains("no gRPC reflection", e.Message); }

            // the project's .proto; the request sent with gzip; the answer gzipped by the server
            var r = GrpcRequests.Call(address, "wk.Clock", "Echo", WellKnownJson, new[] { new KeyValuePair<string, string>("grpc-encoding", "gzip") }, dir).GetAwaiter().GetResult();
            Assert.True(r.Status == 0, r.Message ?? "");
            Assert.Equal("gzip", r.Headers["grpc-encoding"]);
            // (the request went compressed: a compressed message without its grpc-encoding is refused)
            var body = JsonNode.Parse(r.Body)!;
            Assert.Equal("3s", body["took"]!.GetValue<string>());
            Assert.Equal("TICK", body["label"]!.GetValue<string>());
            Assert.Equal("2024-05-01T10:00:00.250Z", body["at"]!.GetValue<string>());
            // (a Struct is a map: its keys in any order)
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse("{\"a\":1,\"b\":[true,null,\"x\"],\"c\":{\"d\":2.5}}"), body["extra"]), body["extra"]!.ToJsonString());
            Assert.Equal("name,address.cityName", body["mask"]!.GetValue<string>());
            Assert.Equal("[\"HIGH\",\"LOW\"]", body["lv"]!.ToJsonString());
            // Empty in and out
            var empty = GrpcRequests.Call(address, "wk.Clock", "Nothing", "{}", Array.Empty<KeyValuePair<string, string>>(), dir).GetAwaiter().GetResult();
            Assert.Equal(0, empty.Status);
            Assert.Equal("{}", JsonNode.Parse(empty.Body)!.ToJsonString());
        }
        finally
        {
            try { server.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }

}
