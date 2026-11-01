using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace PyMagic.Server;

/// <summary>What a notebook needs of its kernel: the built-in one (<see cref="NotebookKernel"/>) or a Jupyter server (<see cref="JupyterKernel"/>).</summary>
public interface INotebookKernel : IDisposable
{
    void Execute(string id, string code, JsonNode? debug = null);
    void Interrupt();
    void Inspect(string id, JsonNode? path);
    void Complete(string id, string prefix);
}

/// <summary>
/// A kernel on a Jupyter server the user started (here or on another computer), reached by Jupyter's own protocol: a kernel is made
/// with <c>POST /api/kernels</c>, code is sent over the WebSocket <c>/api/kernels/{id}/channels</c> as <c>execute_request</c> messages,
/// and what comes back (<c>stream</c>, <c>execute_result</c>, <c>display_data</c>, <c>error</c>, <c>execute_reply</c>) is turned into the
/// messages the built-in kernel sends, so the notebook view cannot tell the two apart. The token goes in the <c>Authorization</c> header,
/// never in an address. Interrupt and restart are the server's <c>/interrupt</c> and <c>/restart</c>; Dispose deletes the kernel it made.
/// </summary>
public sealed class JupyterKernel : INotebookKernel
{
    readonly Action<JsonNode> _emit;
    readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(30) };
    readonly CancellationTokenSource _cts = new();
    readonly Channel<string> _outgoing = Channel.CreateUnbounded<string>();
    readonly ConcurrentDictionary<string, Pending> _pending = new();   // msg_id of an execute_request -> what the cell is waiting for
    readonly string _session = Guid.NewGuid().ToString("N");
    ClientWebSocket? _ws;
    string _base = "";
    string _token = "";
    string? _kernelId;
    bool _disposed;
    bool _readySent;
    string? _infoRequest;

    sealed class Pending(string id)
    {
        public readonly string Id = id; public bool Idle; public bool Replied; public string Status = "ok"; public int? Count;
        /// <summary>Set for a silent question of ours (the variables, the members of a value, the words of a name): its reply is handed on and nothing is shown.</summary>
        public Action<JsonObject>? Answer;
    }

    // the Variables window and the console's completion work on a Jupyter server by asking its kernel with silent execute_requests that carry
    // user_expressions: the same Python code as the built-in kernel's (the shared part of pm_kernel.py) is run there once, as a module of its own
    bool _python;            // the kernel says it runs Python
    bool _noQuestions;       // it did not answer a question of ours: it is not an IPython kernel, so none is asked again

    static readonly Lazy<string> SharedSource = new(() =>
    {
        using var s = typeof(JupyterKernel).Assembly.GetManifestResourceStream("pm_kernel.py") ?? throw new InvalidOperationException("the kernel helper is missing from the assembly");
        using var reader = new StreamReader(s, Encoding.UTF8);
        string text = reader.ReadToEnd().Replace("\r\n", "\n");
        int a = text.IndexOf("# <shared:begin>", StringComparison.Ordinal), b = text.IndexOf("# <shared:end>", StringComparison.Ordinal);
        if (a < 0 || b < a) throw new InvalidOperationException("the shared part of the kernel helper is not marked");
        return text[a..b];
    });

    /// <summary>Python that makes the module pm_jvars in the kernel, once (its names start with an underscore, so the Variables window does not list them).</summary>
    public static string SetupCode()
    {
        string b64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(SharedSource.Value));
        return "import sys as _pm_sys, types as _pm_types, base64 as _pm_b64\n"
            + "if 'pm_jvars' not in _pm_sys.modules:\n"
            + "    _pm_mod = _pm_types.ModuleType('pm_jvars')\n"
            + "    _pm_mod.namespace = globals()\n"
            + $"    exec(compile(_pm_b64.b64decode('{b64}').decode('utf-8'), 'pm_jvars.py', 'exec'), _pm_mod.__dict__)\n"
            + "    _pm_sys.modules['pm_jvars'] = _pm_mod\n";
    }

    /// <summary>A question to the kernel: a silent execute_request whose user_expressions hold one call of the shared code; <paramref name="answer"/> gets the JSON that came back (null when there was none).</summary>
    void Ask(string expression, Action<JsonObject?> answer)
    {
        if (!_python || _noQuestions || _disposed) { answer(null); return; }
        var msg = Message("execute_request", new JsonObject
        {
            ["code"] = SetupCode(), ["silent"] = true, ["store_history"] = false, ["allow_stdin"] = false, ["stop_on_error"] = false,
            ["user_expressions"] = new JsonObject { ["r"] = expression },
        });
        string id = msg["header"]!["msg_id"]!.GetValue<string>();
        _pending[id] = new Pending(id)
        {
            Answer = content =>
            {
                var r = content["user_expressions"]?["r"] as JsonObject;
                var data = r?["status"]?.GetValue<string>() == "ok" ? r["data"]?["application/json"] as JsonObject : null;
                if (data == null) _noQuestions = true;   // an answer that is not ours: this kernel cannot be asked
                answer(data);
            },
        };
        _outgoing.Writer.TryWrite(msg.ToJsonString());
    }

    static string Pass(JsonNode? value) => Convert.ToBase64String(Encoding.UTF8.GetBytes(value?.ToJsonString() ?? "null"));

    /// <summary>Asks for the names the cells left, and sends them as the built-in kernel does (<c>variables</c>).</summary>
    void AskVariables() =>
        Ask("__import__('pm_jvars').jupyter_variables()", data =>
        {
            if (data?["vars"] is JsonArray vars) _emit(new JsonObject { ["type"] = "variables", ["id"] = "", ["vars"] = vars.DeepClone() });
        });

    public JupyterKernel(Action<JsonNode> emit) { _emit = emit; }

    /// <summary>The address without a trailing slash (a server may live under a path: https://host/user/me).</summary>
    public static string BaseOf(string url) => url.Trim().TrimEnd('/');

    static HttpRequestMessage Request(HttpMethod method, string url, string token, JsonNode? body = null)
    {
        var req = new HttpRequestMessage(method, url);
        if (token.Length > 0) req.Headers.Authorization = new AuthenticationHeaderValue("token", token);
        if (body != null) req.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        return req;
    }

    static string Describe(HttpStatusCode code) => code switch
    {
        HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "the server refused the token (check it, or the address)",
        HttpStatusCode.NotFound => "nothing answers there as a Jupyter server (check the address, and the path if the server has a base URL)",
        _ => $"the server answered {(int)code} {code}",
    };

    /// <summary>Asks a server whether it is a Jupyter server that takes the token: { ok, message, kernels }.</summary>
    public static async Task<JsonObject> Probe(string url, string token)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        try
        {
            using var res = await http.SendAsync(Request(HttpMethod.Get, BaseOf(url) + "/api/kernelspecs", token));
            if (!res.IsSuccessStatusCode) return new JsonObject { ["ok"] = false, ["message"] = Describe(res.StatusCode) };
            var body = JsonNode.Parse(await res.Content.ReadAsStringAsync());
            var names = (body?["kernelspecs"] as JsonObject)?.Select(kv => kv.Key).ToList() ?? new List<string>();
            return new JsonObject
            {
                ["ok"] = true,
                ["message"] = names.Count > 0 ? $"Connected. Kernels on this server: {string.Join(", ", names)}" : "Connected, but the server lists no kernels",
                ["kernels"] = new JsonArray(names.Select(n => (JsonNode)n).ToArray()),
            };
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException)
        {
            return new JsonObject { ["ok"] = false, ["message"] = e is TaskCanceledException ? "the server did not answer in time" : $"cannot reach the server: {e.Message}" };
        }
    }

    // ------------------------------------------------------------ starting

    /// <summary>Makes a kernel on the server and connects to it; <c>ready</c> is sent when the kernel answers. Failures are sent as <c>kernelError</c>.</summary>
    public void Start(string url, string token, string? kernelName)
    {
        _base = BaseOf(url);
        _token = token;
        Task.Run(async () =>
        {
            try { await ConnectAsync(kernelName); }
            catch (Exception e) when (e is HttpRequestException or WebSocketException or TaskCanceledException or InvalidOperationException or System.Text.Json.JsonException or UriFormatException)
            {
                if (!_disposed) Fail(e is TaskCanceledException ? "the server did not answer in time" : $"cannot reach the Jupyter server: {e.Message}");
            }
        });
    }

    void Fail(string message) => _emit(new JsonObject { ["type"] = "kernelError", ["message"] = message });

    async Task ConnectAsync(string? kernelName)
    {
        var body = new JsonObject();
        if (!string.IsNullOrWhiteSpace(kernelName)) body["name"] = kernelName;
        using (var res = await _http.SendAsync(Request(HttpMethod.Post, _base + "/api/kernels", _token, body), _cts.Token))
        {
            if (!res.IsSuccessStatusCode) { Fail("Jupyter: " + Describe(res.StatusCode)); return; }
            _kernelId = JsonNode.Parse(await res.Content.ReadAsStringAsync(_cts.Token))?["id"]?.GetValue<string>();
        }
        if (_kernelId == null) { Fail("Jupyter: the server did not say which kernel it made"); return; }

        var uri = new UriBuilder(_base + $"/api/kernels/{_kernelId}/channels") { Query = "session_id=" + _session };
        uri.Scheme = uri.Scheme == "https" ? "wss" : "ws";
        var ws = new ClientWebSocket();
        if (_token.Length > 0) ws.Options.SetRequestHeader("Authorization", "token " + _token);
        await ws.ConnectAsync(uri.Uri, _cts.Token);
        _ws = ws;
        _ = Task.Run(() => WriteLoop(ws));
        _ = Task.Run(() => ReadLoop(ws));
        AskInfo();
    }

    /// <summary>kernel_info_request: its reply is how we know the kernel is up (and which Python it runs).</summary>
    void AskInfo()
    {
        _infoRequest = Guid.NewGuid().ToString("N");
        _outgoing.Writer.TryWrite(Message("kernel_info_request", new JsonObject(), _infoRequest).ToJsonString());
    }

    JsonObject Message(string type, JsonObject content, string? id = null) => new()
    {
        ["header"] = new JsonObject
        {
            ["msg_id"] = id ?? Guid.NewGuid().ToString("N"), ["username"] = "pymagic", ["session"] = _session,
            ["msg_type"] = type, ["date"] = DateTime.UtcNow.ToString("o"), ["version"] = "5.3",
        },
        ["parent_header"] = new JsonObject(), ["metadata"] = new JsonObject(), ["content"] = content, ["buffers"] = new JsonArray(),
        ["channel"] = "shell",
    };

    // ------------------------------------------------------------ commands

    public void Execute(string id, string code, JsonNode? debug = null)
    {
        var msg = Message("execute_request", new JsonObject
        {
            ["code"] = code, ["silent"] = false, ["store_history"] = true, ["user_expressions"] = new JsonObject(), ["allow_stdin"] = false, ["stop_on_error"] = true,
        });
        _pending[msg["header"]!["msg_id"]!.GetValue<string>()] = new Pending(id);
        _outgoing.Writer.TryWrite(msg.ToJsonString());
    }

    public void Inspect(string id, JsonNode? path) =>
        Ask($"__import__('pm_jvars').jupyter_children('{Pass(path)}')", data =>
        {
            var children = data?["children"] as JsonArray ?? new JsonArray();
            var reply = new JsonObject { ["type"] = "children", ["id"] = id, ["path"] = path?.DeepClone(), ["children"] = children.DeepClone() };
            if (data == null) reply["error"] = "This Jupyter kernel cannot tell what its variables hold (it is not an IPython kernel)";
            else if (data["error"] is { } e) reply["error"] = e.DeepClone();
            _emit(reply);
        });

    public void Complete(string id, string prefix) =>
        Ask($"__import__('pm_jvars').jupyter_complete('{Pass(prefix)}')", data =>
            _emit(new JsonObject { ["type"] = "completions", ["id"] = id, ["items"] = (data?["items"] as JsonArray ?? new JsonArray()).DeepClone() }));

    /// <summary>POST /api/kernels/{id}/interrupt.</summary>
    public void Interrupt() => Post("interrupt");

    /// <summary>POST /api/kernels/{id}/restart: the server starts the kernel again (its variables are gone); <c>ready</c> follows when it answers.</summary>
    public void Restart()
    {
        foreach (var (_, p) in _pending) _emit(new JsonObject { ["type"] = "done", ["id"] = p.Id, ["status"] = "error", ["execution_count"] = null });
        _pending.Clear();
        _readySent = false;
        Task.Run(async () =>
        {
            await PostAsync("restart");
            if (!_disposed) AskInfo();
        });
    }

    void Post(string what) => Task.Run(() => PostAsync(what));

    async Task PostAsync(string what)
    {
        if (_kernelId == null) return;
        try { using var res = await _http.SendAsync(Request(HttpMethod.Post, $"{_base}/api/kernels/{_kernelId}/{what}", _token), _cts.Token); }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { if (!_disposed) Fail($"Jupyter: {what} failed: {e.Message}"); }
    }

    // ------------------------------------------------------------ the socket

    async Task WriteLoop(ClientWebSocket ws)
    {
        try
        {
            await foreach (var text in _outgoing.Reader.ReadAllAsync(_cts.Token))
                await ws.SendAsync(Encoding.UTF8.GetBytes(text), WebSocketMessageType.Text, true, _cts.Token);
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
    }

    async Task ReadLoop(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                using var ms = new MemoryStream();
                WebSocketReceiveResult r;
                do
                {
                    r = await ws.ReceiveAsync(buffer, _cts.Token);
                    if (r.MessageType == WebSocketMessageType.Close) goto closed;
                    ms.Write(buffer, 0, r.Count);
                } while (!r.EndOfMessage);
                try { if (JsonNode.Parse(ms.ToArray()) is JsonObject msg) Handle(msg); }
                catch (System.Text.Json.JsonException) { }
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException) { }
        closed:
        if (!_disposed) _emit(new JsonObject { ["type"] = "kernelExited", ["code"] = 0 });
    }

    /// <summary>One message of the server, turned into what the built-in kernel would have said.</summary>
    void Handle(JsonObject msg)
    {
        string type = msg["header"]?["msg_type"]?.GetValue<string>() ?? msg["msg_type"]?.GetValue<string>() ?? "";
        string parent = msg["parent_header"]?["msg_id"]?.GetValue<string>() ?? "";
        var content = msg["content"] as JsonObject ?? new JsonObject();

        if (type == "kernel_info_reply" && parent == _infoRequest && !_readySent)
        {
            _readySent = true;
            string? version = content["language_info"]?["version"]?.GetValue<string>();
            _python = string.Equals(content["language_info"]?["name"]?.GetValue<string>(), "python", StringComparison.OrdinalIgnoreCase);
            _emit(new JsonObject { ["type"] = "ready", ["python"] = version ?? "" });
            return;
        }
        if (!_pending.TryGetValue(parent, out var p)) return;
        if (p.Answer != null)
        {
            if (type == "execute_reply" && _pending.TryRemove(parent, out _)) p.Answer(content);
            return;
        }
        switch (type)
        {
            case "stream":
                _emit(new JsonObject { ["type"] = "stream", ["id"] = p.Id, ["name"] = content["name"]?.GetValue<string>() ?? "stdout", ["text"] = content["text"]?.GetValue<string>() ?? "" });
                break;
            case "execute_result":
                _emit(new JsonObject { ["type"] = "result", ["id"] = p.Id, ["data"] = content["data"]?.DeepClone() ?? new JsonObject(), ["execution_count"] = content["execution_count"]?.DeepClone() });
                break;
            case "display_data":
            case "update_display_data":
                _emit(new JsonObject { ["type"] = "display", ["id"] = p.Id, ["data"] = content["data"]?.DeepClone() ?? new JsonObject() });
                break;
            case "error":
                _emit(new JsonObject { ["type"] = "error", ["id"] = p.Id, ["ename"] = content["ename"]?.GetValue<string>() ?? "Error", ["evalue"] = content["evalue"]?.GetValue<string>() ?? "", ["traceback"] = content["traceback"]?.DeepClone() ?? new JsonArray() });
                break;
            case "status":
                if (content["execution_state"]?.GetValue<string>() == "idle") p.Idle = true;
                break;
            case "execute_reply":
                p.Replied = true;
                p.Status = content["status"]?.GetValue<string>() == "ok" ? "ok" : "error";
                p.Count = content["execution_count"] is JsonValue v && v.TryGetValue<int>(out var n) ? n : null;
                break;
        }
        // the cell is done when the reply and the idle state have both come: output can still arrive after the reply
        if (p.Idle && p.Replied && _pending.TryRemove(parent, out _))
        {
            _emit(new JsonObject { ["type"] = "done", ["id"] = p.Id, ["status"] = p.Status, ["execution_count"] = p.Count });
            AskVariables();
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _cts.Cancel();
        _outgoing.Writer.TryComplete();
        var ws = _ws;
        string? kernel = _kernelId;
        string url = _base, token = _token;
        Task.Run(async () =>
        {
            try { if (ws is { State: WebSocketState.Open }) await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "", new CancellationTokenSource(2000).Token); } catch (Exception e) when (e is WebSocketException or OperationCanceledException) { }
            // the kernel was made for this notebook: it is shut down with it
            if (kernel != null)
                try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(5) }; using var res = await http.SendAsync(Request(HttpMethod.Delete, $"{url}/api/kernels/{kernel}", token)); }
                catch (Exception e) when (e is HttpRequestException or TaskCanceledException) { }
            ws?.Dispose();
            _http.Dispose();
        });
    }
}
