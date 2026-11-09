using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace PyMagic.Server;

/// <summary>
/// The HTTP client's WEBSOCKET requests (PyCharm's format): the connection is opened with the request's headers, its messages are sent in turn
/// (one after <c>=== wait-for-server</c> waits for a message from the server first), and what the server sends is kept until it has been quiet
/// for a moment (or closed the connection), as the talk shown in the HTTP tab.
/// </summary>
public static class WebSocketRequests
{
    public sealed record Message(string Text, bool Wait);

    /// <param name="Dir">sent or received.</param>
    /// <param name="Ms">When, from the connection's start.</param>
    public sealed record Line(string Dir, string Text, long Ms);

    public sealed record Result(List<Line> Transcript, int? CloseStatus, string? CloseDescription, bool Closed, Dictionary<string, string> Headers, long Ms);

    /// <param name="quiet">How long the server may be quiet after the last message before the connection is closed.</param>
    /// <param name="waitLimit">How long a message after <c>wait-for-server</c> waits at most.</param>
    /// <exception cref="WebSocketException">The connection could not be made.</exception>
    public static async Task<Result> Run(Uri url, IEnumerable<KeyValuePair<string, string>> headers, IReadOnlyList<Message> messages,
        TimeSpan quiet, TimeSpan waitLimit, TimeSpan limit)
    {
        using var ws = new ClientWebSocket();
        ws.Options.CollectHttpResponseDetails = true;
        foreach (var (k, v) in headers)
        {
            if (k.Equals("Sec-WebSocket-Protocol", StringComparison.OrdinalIgnoreCase))
                foreach (var p in v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) ws.Options.AddSubProtocol(p);
            else ws.Options.SetRequestHeader(k, v);
        }
        using var overall = new CancellationTokenSource(limit);
        var watch = Stopwatch.StartNew();
        await ws.ConnectAsync(url, overall.Token);

        var transcript = new List<Line>();
        int received = 0;
        long lastHeard = 0;
        var heard = new SemaphoreSlim(0);
        var reading = Task.Run(async () =>
        {
            var buffer = new byte[64 * 1024];
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var whole = new MemoryStream();
                    WebSocketReceiveResult r;
                    do
                    {
                        r = await ws.ReceiveAsync(buffer, overall.Token);
                        whole.Write(buffer, 0, r.Count);
                    } while (!r.EndOfMessage && r.MessageType != WebSocketMessageType.Close);
                    if (r.MessageType == WebSocketMessageType.Close) break;
                    string text = r.MessageType == WebSocketMessageType.Text ? Encoding.UTF8.GetString(whole.ToArray()) : $"[binary, {whole.Length} bytes]";
                    lock (transcript) { transcript.Add(new Line("received", text, watch.ElapsedMilliseconds)); received++; lastHeard = watch.ElapsedMilliseconds; }
                    heard.Release();
                }
            }
            catch (Exception e) when (e is WebSocketException or OperationCanceledException) { /* the connection ended */ }
        });

        int atLastSend = 0;   // how many messages the server had sent when the last message went out
        foreach (var m in messages)
        {
            if (ws.State != WebSocketState.Open) break;
            if (m.Wait)
            {
                // wait-for-server: a message from the server since the last one sent
                var until = DateTime.UtcNow + waitLimit;
                while (true)
                {
                    lock (transcript) if (received > atLastSend || ws.State != WebSocketState.Open) break;
                    var left = until - DateTime.UtcNow;
                    if (left <= TimeSpan.Zero || !await heard.WaitAsync(left)) break;
                }
            }
            if (ws.State != WebSocketState.Open) break;
            await ws.SendAsync(Encoding.UTF8.GetBytes(m.Text), WebSocketMessageType.Text, true, overall.Token);
            lock (transcript) { transcript.Add(new Line("sent", m.Text, watch.ElapsedMilliseconds)); lastHeard = watch.ElapsedMilliseconds; atLastSend = received; }
        }

        // what the server still sends: until it has been quiet for a moment, it closes, or the time is up
        while (ws.State == WebSocketState.Open && !overall.IsCancellationRequested)
        {
            long since;
            lock (transcript) since = watch.ElapsedMilliseconds - lastHeard;
            if (since >= quiet.TotalMilliseconds) break;
            await heard.WaitAsync(TimeSpan.FromMilliseconds(Math.Max(20, quiet.TotalMilliseconds - since)));
        }
        bool closedByServer = ws.State is WebSocketState.CloseReceived or WebSocketState.Closed;
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (Exception e) when (e is WebSocketException or TimeoutException or OperationCanceledException) { /* gone already */ }
        await Task.WhenAny(reading, Task.Delay(1000));

        var responseHeaders = new Dictionary<string, string>();
        if (ws.HttpResponseHeaders != null)
            foreach (var (k, v) in ws.HttpResponseHeaders) responseHeaders[k] = string.Join(", ", v);
        List<Line> lines;
        lock (transcript) lines = transcript.ToList();
        return new Result(lines, closedByServer ? (int?)ws.CloseStatus : null, closedByServer ? ws.CloseStatusDescription : null, closedByServer, responseHeaders, watch.ElapsedMilliseconds);
    }
}
