using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// A WEBSOCKET request of the HTTP client kept open: the page's own WebSocket to PyMagic carries what the person types to the server and what
/// the server sends back, as it comes, until either side closes. The request's messages go first (one after <c>=== wait-for-server</c> waits for
/// the server). A GRAPHQL subscription goes the same way, its protocol spoken here: graphql-transport-ws (the graphql-ws library's) or the older
/// graphql-ws (subscriptions-transport-ws), as the server chooses; each event of the subscription shows as a message, Stop completes it.
/// The page's messages: {type:"send", text}, {type:"close"}. Its answers: {type:"open", headers, protocol}, {type:"sent"|"received", text, ms},
/// {type:"closed", status, description, ms}, {type:"error", error}.
/// </summary>
public static class LiveSockets
{
    /// <summary>What the live connection is: its address and headers, the request's messages, and for a subscription its query and variables.</summary>
    public sealed record Start(Uri Url, List<KeyValuePair<string, string>> Headers, List<WebSocketRequests.Message> Messages, string? Query, JsonNode? Variables);

    public static Start? StartOf(JsonNode? m)
    {
        if (!Uri.TryCreate(m?["url"]?.GetValue<string>() ?? "", UriKind.Absolute, out var url)) return null;
        // a subscription's http(s) address is its WebSocket's (the same server and path)
        if (m?["graphql"] is JsonObject && url.Scheme is "http" or "https") url = new UriBuilder(url) { Scheme = url.Scheme == "https" ? "wss" : "ws", Port = url.IsDefaultPort ? -1 : url.Port }.Uri;
        if (url.Scheme is not ("ws" or "wss")) return null;
        var headers = (m?["headers"] as JsonObject ?? new JsonObject()).Select(kv => new KeyValuePair<string, string>(kv.Key, kv.Value?.GetValue<string>() ?? "")).ToList();
        var messages = (m?["messages"] as JsonArray ?? new JsonArray()).Select(x => new WebSocketRequests.Message(x?["text"]?.GetValue<string>() ?? "", x?["wait"]?.GetValue<bool>() == true)).ToList();
        return new Start(url, headers, messages, m?["graphql"]?["query"]?.GetValue<string>(), m?["graphql"]?["variables"]?.DeepClone());
    }

    /// <summary>Relays between the page's socket and the server's until one of them closes (or <paramref name="cancel"/>).</summary>
    public static async Task Relay(WebSocket page, Start start, CancellationToken cancel)
    {
        var watch = Stopwatch.StartNew();
        var sendLock = new SemaphoreSlim(1, 1);
        async Task Tell(JsonObject o)
        {
            o["ms"] ??= watch.ElapsedMilliseconds;
            await sendLock.WaitAsync(cancel);
            try { if (page.State == WebSocketState.Open) await page.SendAsync(Encoding.UTF8.GetBytes(o.ToJsonString()), WebSocketMessageType.Text, true, cancel); }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException) { }
            finally { sendLock.Release(); }
        }

        bool graphql = start.Query != null;
        using var server = new ClientWebSocket();
        server.Options.CollectHttpResponseDetails = true;
        foreach (var (k, v) in start.Headers)
        {
            if (k.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                foreach (var p in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) server.Options.AddSubProtocol(p);
            else server.Options.SetRequestHeader(k, v);
        }
        if (graphql && !start.Headers.Any(h => h.Key.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase)))
        {
            server.Options.AddSubProtocol("graphql-transport-ws");
            server.Options.AddSubProtocol("graphql-ws");
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        try
        {
            using var connecting = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            connecting.CancelAfter(TimeSpan.FromSeconds(30));
            await server.ConnectAsync(start.Url, connecting.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ArgumentException)
        {
            await Tell(new JsonObject { ["type"] = "error", ["error"] = (e is OperationCanceledException ? "no connection in 30 seconds" : e.Message) + (start.Url.IsLoopback ? " (is the server running?)" : "") });
            return;
        }
        var headers = new JsonObject();
        if (server.HttpResponseHeaders != null) foreach (var (k, v) in server.HttpResponseHeaders) headers[k] = string.Join(", ", v);
        // the legacy protocol is graphql-ws; graphql-transport-ws is the library graphql-ws's (the names are as they are)
        bool legacy = graphql && server.SubProtocol == "graphql-ws";
        await Tell(new JsonObject { ["type"] = "open", ["headers"] = headers, ["protocol"] = server.SubProtocol });

        async Task ToServer(string text, bool show = true)
        {
            await server.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, linked.Token);
            if (show) await Tell(new JsonObject { ["type"] = "sent", ["text"] = text });
        }

        int received = 0;
        var heard = new SemaphoreSlim(0);
        var acked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        // the server's messages, as they come (a subscription's: its events' payloads)
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (server.State == WebSocketState.Open)
                {
                    using var whole = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await server.ReceiveAsync(buffer, linked.Token); whole.Write(buffer, 0, r.Count); }
                    while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    string text = r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(whole.ToArray()) : $"[binary, {whole.Length} bytes]";
                    if (graphql && JsonNode.Parse(text) is JsonObject g && g["type"]?.GetValue<string>() is { } type)
                    {
                        switch (type)
                        {
                            case "connection_ack": acked.TrySetResult(); continue;
                            case "ping": await ToServer("""{"type":"pong"}""", show: false); continue;
                            case "pong": case "ka": continue;
                            case "next": case "data": text = g["payload"]?.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }) ?? text; break;
                            case "error": case "connection_error": text = "error: " + (g["payload"]?.ToJsonString() ?? text); acked.TrySetResult(); break;
                            case "complete":
                                await Tell(new JsonObject { ["type"] = "received", ["text"] = "(the subscription is complete)" });
                                try { await server.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)); } catch (Exception e) when (e is WebSocketException or TimeoutException) { }
                                return;
                        }
                    }
                    Interlocked.Increment(ref received);
                    heard.Release();
                    await Tell(new JsonObject { ["type"] = "received", ["text"] = text });
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { }
        });

        try
        {
            if (graphql)
            {
                // connection_init, its ack, then the subscription
                await ToServer("""{"type":"connection_init","payload":{}}""", show: false);
                if (await Task.WhenAny(acked.Task, Task.Delay(10_000, linked.Token)) != acked.Task)
                    await Tell(new JsonObject { ["type"] = "received", ["text"] = "(the server did not acknowledge the connection in 10 seconds)" });
                var payload = new JsonObject { ["query"] = start.Query };
                if (start.Variables != null) payload["variables"] = start.Variables.DeepClone();
                await ToServer(new JsonObject { ["id"] = "1", ["type"] = legacy ? "start" : "subscribe", ["payload"] = payload }.ToJsonString());
            }
            else
            {
                int atLastSend = 0;
                foreach (var m in start.Messages)
                {
                    if (server.State != WebSocketState.Open) break;
                    if (m.Wait)
                    {
                        var until = DateTime.UtcNow.AddSeconds(10);
                        while (Volatile.Read(ref received) <= atLastSend && server.State == WebSocketState.Open && until > DateTime.UtcNow)
                            await heard.WaitAsync(until - DateTime.UtcNow, linked.Token);
                    }
                    if (server.State != WebSocketState.Open) break;
                    await ToServer(m.Text);
                    atLastSend = Volatile.Read(ref received);
                }
            }

            // what the person types, until they close it or the server does
            var pageBuffer = new byte[64 * 1024];
            // (not cancelled when the server ends: a cancelled read aborts the page's socket, and its last message would not reach it)
            var fromPage = Task.Run(async () =>
            {
                try
                {
                    while (page.State == WebSocketState.Open && server.State == WebSocketState.Open)
                    {
                        using var whole = new MemoryStream();
                        WebSocketReceiveResult r;
                        do { r = await page.ReceiveAsync(pageBuffer, cancel); whole.Write(pageBuffer, 0, r.Count); }
                        while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                        if (r.MessageType == WebSocketMessageType.Close) return;
                        var msg = JsonNode.Parse(Encoding.UTF8.GetString(whole.ToArray()));
                        switch (msg?["type"]?.GetValue<string>())
                        {
                            case "send" when !graphql && msg["text"]?.GetValue<string>() is { } text: await ToServer(text); break;
                            case "close":
                                if (graphql && server.State == WebSocketState.Open) await ToServer(legacy ? """{"id":"1","type":"stop"}""" : """{"id":"1","type":"complete"}""", show: false);
                                return;
                        }
                    }
                }
                catch (Exception e) when (e is WebSocketException or OperationCanceledException or System.Text.Json.JsonException) { }
            });
            await Task.WhenAny(reading, fromPage);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }

        bool byServer = server.State is WebSocketState.CloseReceived or WebSocketState.Closed;
        try
        {
            if (server.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await server.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or OperationCanceledException) { }
        linked.Cancel();
        await Task.WhenAny(reading, Task.Delay(1000));
        await Tell(new JsonObject { ["type"] = "closed", ["byServer"] = byServer, ["status"] = byServer ? (int?)server.CloseStatus : null, ["description"] = byServer ? server.CloseStatusDescription : null });
    }
}
