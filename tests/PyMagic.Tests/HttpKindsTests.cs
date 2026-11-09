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

    // ------------------------------------------------------------ gRPC against Python's grpcio

    const string Proto = """
        syntax = "proto3";
        package demo;
        service Greeter {
          rpc SayHello (HelloRequest) returns (HelloReply);
          rpc Count (CountRequest) returns (stream Number);
          rpc Fail (HelloRequest) returns (HelloReply);
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

            string Why(string service, string method, string json)
            {
                try { GrpcRequests.Call(address, service, method, json, Array.Empty<KeyValuePair<string, string>>()).GetAwaiter().GetResult(); return "(no error)"; }
                catch (ServiceException e) { return e.Message; }
            }
            Assert.Equal("demo.Greeter has no method Hello (it has SayHello, Count, Fail)", Why("demo.Greeter", "Hello", "{}"));
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
}
