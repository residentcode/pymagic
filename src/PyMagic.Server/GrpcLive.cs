using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PyMagic.Server;

/// <summary>
/// A GRPC request of the HTTP client sent from the editor, kept open while it streams: the request's message (or for a client-streaming method
/// its JSON array, one message each) goes first, then each message the person types (JSON) is sent as it is typed, and each one the server
/// sends shows as it comes; Close ends what the client sends (the server then finishes the call, as client-streaming methods do), Stop of a
/// server-streaming call cancels it. A unary method is called once, as /api/grpc calls it. The service is described as GrpcRequests describes
/// it (reflection, else the project's .proto files). The page's messages: {type:"send", text}, {type:"close"}. Its answers: {type:"open",
/// streaming: "client"|"server"|"bidi", input, output}, {type:"sent"|"received", text, ms}, {type:"note", text} (a typed message that was not
/// sent, and why), {type:"closed", status, reason, message, headers, ms}, {type:"result", …} (a unary call's answer), {type:"error", error}.
/// </summary>
public static class GrpcLive
{
    public sealed record Start(string Address, string Service, string Method, string Body, List<KeyValuePair<string, string>> Metadata);

    public static Start? StartOf(JsonNode? m)
    {
        if (!Uri.TryCreate(m?["address"]?.GetValue<string>() ?? "", UriKind.Absolute, out var a) || a.Scheme is not ("http" or "https")) return null;
        string service = m?["service"]?.GetValue<string>() ?? "", method = m?["method"]?.GetValue<string>() ?? "";
        if (service.Length == 0 || method.Length == 0) return null;
        var metadata = (m?["metadata"] as JsonObject ?? new JsonObject()).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.GetValue<string>() ?? "")).ToList();
        return new Start(a.GetLeftPart(UriPartial.Authority), service, method, m?["body"]?.GetValue<string>() ?? "", metadata);
    }

    /// <summary>A request body written as it is given: each frame the channel gets, flushed at once; the end of the channel ends the stream.</summary>
    sealed class Frames(ChannelReader<byte[]> frames) : HttpContent
    {
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context) => await SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancel)
        {
            await foreach (var f in frames.ReadAllAsync(cancel))
            {
                await stream.WriteAsync(f, cancel);
                await stream.FlushAsync(cancel);
            }
        }
        protected override bool TryComputeLength(out long length) { length = -1; return false; }
    }

    static byte[] Frame(byte[] message)
    {
        var f = new byte[5 + message.Length];
        BinaryPrimitives.WriteUInt32BigEndian(f.AsSpan(1, 4), (uint)message.Length);
        message.CopyTo(f, 5);
        return f;
    }

    static readonly JsonSerializerOptions Compact = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Calls the method and relays between the page's socket and the call until the call ends (or the page goes).</summary>
    public static async Task Relay(WebSocket page, Start start, string? root, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Tell(JsonObject o)
        {
            o["ms"] ??= watch.ElapsedMilliseconds;
            await sendLock.WaitAsync(CancellationToken.None);
            try { if (page.State == WebSocketState.Open) await page.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, CancellationToken.None); }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
            finally { sendLock.Release(); }
        }

        using var client = new HttpClient(new SocketsHttpHandler { ConnectTimeout = TimeSpan.FromSeconds(10) }) { Timeout = Timeout.InfiniteTimeSpan };
        Protobuf.Pool pool;
        Protobuf.Method m;
        try
        {
            using var describing = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            describing.CancelAfter(TimeSpan.FromSeconds(30));
            pool = await GrpcRequests.Describe(client, start.Address, start.Service, root).WaitAsync(describing.Token);
            if (!pool.Services.TryGetValue(start.Service, out var svc))
                throw new ServiceException($"the server has no service {start.Service}" + (pool.Services.Count > 0 ? $" (it has {string.Join(", ", pool.Services.Keys)})" : ""));
            m = svc.Methods.FirstOrDefault(x => x.Name == start.Method)
                ?? throw new ServiceException($"{start.Service} has no method {start.Method} (it has {string.Join(", ", svc.Methods.Select(x => x.Name))})");
        }
        catch (ServiceException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = e.Message }); return; }
        catch (OperationCanceledException) { await Tell(new JsonObject { ["type"] = "error", ["error"] = "no answer in 30 seconds" }); return; }

        // a unary method: one call, its answer as /api/grpc gives it
        if (!m.ClientStreaming && !m.ServerStreaming)
        {
            try
            {
                var r = await GrpcRequests.Call(start.Address, start.Service, start.Method, start.Body, start.Metadata, root);
                await Tell(new JsonObject
                {
                    ["type"] = "result", ["status"] = r.Status, ["reason"] = r.StatusName, ["message"] = r.Message,
                    ["body"] = r.Message != null && r.Body.Length == 0 ? r.Message : r.Body,
                    ["headers"] = new JsonObject(r.Headers.Select(h => new KeyValuePair<string, JsonNode?>(h.Key, h.Value))), ["ms"] = r.Ms,
                });
            }
            catch (ServiceException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = e.Message }); }
            return;
        }

        // the request's own messages: none for an empty body of a client-streaming method, a JSON array's each, else the one
        List<JsonNode?> first;
        try
        {
            var json = string.IsNullOrWhiteSpace(start.Body) ? null : JsonNode.Parse(start.Body);
            first = json == null ? (m.ClientStreaming ? new() : new() { new JsonObject() }) : m.ClientStreaming && json is JsonArray many ? many.ToList() : new() { json };
        }
        catch (JsonException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = "the body is not JSON: " + e.Message }); return; }
        var encoded = new List<(byte[] Bytes, string Text)>();
        try { foreach (var j in first) encoded.Add((Protobuf.Encode(pool, m.Input, j), j?.ToJsonString(Compact) ?? "null")); }
        catch (FormatException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = e.Message }); return; }

        using var call = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        var outgoing = Channel.CreateUnbounded<byte[]>();
        var request = new HttpRequestMessage(HttpMethod.Post, start.Address.TrimEnd('/') + $"/{start.Service}/{start.Method}")
        {
            Version = HttpVersion.Version20, VersionPolicy = HttpVersionPolicy.RequestVersionExact, Content = new Frames(outgoing.Reader),
        };
        request.Content.Headers.TryAddWithoutValidation("Content-Type", "application/grpc");
        request.Headers.TryAddWithoutValidation("TE", "trailers");
        request.Headers.TryAddWithoutValidation("grpc-accept-encoding", "gzip, deflate, identity");
        foreach (var (k, v) in start.Metadata)
            if (!k.Equals("grpc-encoding", StringComparison.OrdinalIgnoreCase)) request.Headers.TryAddWithoutValidation(k, v);

        string streaming = m.ClientStreaming && m.ServerStreaming ? "bidi" : m.ClientStreaming ? "client" : "server";
        await Tell(new JsonObject { ["type"] = "open", ["streaming"] = streaming, ["input"] = m.Input, ["output"] = m.Output });
        var sending = client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, call.Token);
        foreach (var (bytes, text) in encoded)
        {
            outgoing.Writer.TryWrite(Frame(bytes));
            await Tell(new JsonObject { ["type"] = "sent", ["text"] = text });
        }
        // a server-streaming call sends its one message and ends what it sends
        if (!m.ClientStreaming) outgoing.Writer.TryComplete();

        // what the person types: a message to send, Close (the end of what is sent), Stop (a server-streaming call cancelled)
        var pageBuffer = new byte[256 * 1024];
        var fromPage = Task.Run(async () =>
        {
            try
            {
                while (page.State == WebSocketState.Open)
                {
                    using var whole = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await page.ReceiveAsync(pageBuffer, cancel); whole.Write(pageBuffer, 0, r.Count); }
                    while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                    if (r.MessageType == WebSocketMessageType.Close) { call.Cancel(); return; }
                    var msg = JsonNode.Parse(Encoding.UTF8.GetString(whole.ToArray()));
                    switch (msg?["type"]?.GetValue<string>())
                    {
                        case "send" when msg["text"]?.GetValue<string>() is { } text:
                            if (!m.ClientStreaming) { await Tell(new JsonObject { ["type"] = "note", ["text"] = "not sent: this method takes one message, sent already" }); break; }
                            try
                            {
                                var j = JsonNode.Parse(text);
                                var bytes = Protobuf.Encode(pool, m.Input, j);
                                if (!outgoing.Writer.TryWrite(Frame(bytes))) { await Tell(new JsonObject { ["type"] = "note", ["text"] = "not sent: the sending is closed" }); break; }
                                await Tell(new JsonObject { ["type"] = "sent", ["text"] = j?.ToJsonString(Compact) ?? "null" });
                            }
                            catch (JsonException e) { await Tell(new JsonObject { ["type"] = "note", ["text"] = "not sent: not JSON: " + e.Message }); }
                            catch (FormatException e) { await Tell(new JsonObject { ["type"] = "note", ["text"] = "not sent: " + e.Message }); }
                            break;
                        case "close":
                            if (m.ClientStreaming) outgoing.Writer.TryComplete();
                            else call.Cancel();
                            break;
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or JsonException) { call.Cancel(); }
        });

        int? status = null;
        string? message = null;
        var headers = new JsonObject();
        try
        {
            using var response = await sending;
            foreach (var h in response.Headers.Concat(response.Content.Headers)) headers[h.Key] = string.Join(", ", h.Value);
            string? encoding = response.Headers.TryGetValues("grpc-encoding", out var enc) ? enc.First().Trim().ToLowerInvariant() : null;
            // an answer that is only trailers (an error at once) has its status in its headers
            if (response.Headers.TryGetValues("grpc-status", out var hs) && int.TryParse(hs.First(), out var hcode))
            {
                status = hcode;
                message = response.Headers.TryGetValues("grpc-message", out var hm) ? Uri.UnescapeDataString(hm.First()) : null;
            }
            else if (!response.IsSuccessStatusCode)
                throw new ServiceException($"{start.Address} answered HTTP {(int)response.StatusCode} {response.ReasonPhrase}: not a gRPC server here?");
            await using var body = await response.Content.ReadAsStreamAsync(call.Token);
            var pending = new MemoryStream();
            var buffer = new byte[64 * 1024];
            int n;
            while ((n = await body.ReadAsync(buffer, call.Token)) > 0)
            {
                pending.Write(buffer, 0, n);
                // every whole message so far
                var all = pending.GetBuffer();
                int at = 0, length = (int)pending.Length;
                while (at + 5 <= length)
                {
                    int len = (int)BinaryPrimitives.ReadUInt32BigEndian(all.AsSpan(at + 1, 4));
                    if (at + 5 + len > length) break;
                    var data = all.AsSpan(at + 5, len).ToArray();
                    if (all[at] == 1) data = GrpcRequests.Decompress(data, encoding);
                    string text;
                    try { text = Protobuf.DecodeMessage(pool, m.Output, data)?.ToJsonString(Indented) ?? "null"; }
                    catch (FormatException e) { text = $"(not read as {m.Output}: {e.Message})"; }
                    await Tell(new JsonObject { ["type"] = "received", ["text"] = text });
                    at += 5 + len;
                }
                var rest = all.AsSpan(at, length - at).ToArray();
                pending.SetLength(0);
                pending.Write(rest);
            }
            foreach (var h in response.TrailingHeaders) headers[h.Key] = string.Join(", ", h.Value);
            if (response.TrailingHeaders.TryGetValues("grpc-status", out var t) && int.TryParse(t.First(), out var code))
            {
                status = code;
                message = response.TrailingHeaders.TryGetValues("grpc-message", out var tm) ? Uri.UnescapeDataString(tm.First()) : null;
            }
            status ??= 2;   // no status: UNKNOWN, as gRPC's clients take it
        }
        catch (OperationCanceledException) { status = 1; message = "cancelled by PyMagic (Stop)"; }
        catch (HttpRequestException e) when (call.IsCancellationRequested) { _ = e; status = 1; message = "cancelled by PyMagic (Stop)"; }
        catch (HttpRequestException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = $"{start.Address}: {e.Message} (is the gRPC server running, and does it speak plain-text HTTP/2? grpcs:// for TLS)" }); }
        catch (ServiceException e) { await Tell(new JsonObject { ["type"] = "error", ["error"] = e.Message }); }
        catch (IOException e) { status = 14; message = e.Message; }
        outgoing.Writer.TryComplete();
        if (status is { } s)
            await Tell(new JsonObject { ["type"] = "closed", ["status"] = s, ["reason"] = GrpcRequests.CodeName(s), ["message"] = message, ["headers"] = headers });
        call.Cancel();
        await Task.WhenAny(fromPage, Task.Delay(1000));
    }
}
