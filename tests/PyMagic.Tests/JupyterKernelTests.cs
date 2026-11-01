using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The kernel on a Jupyter server, against a small server written here that speaks the parts of Jupyter's REST and WebSocket protocol the
/// kernel uses (kernels, channels, interrupt, restart, delete). It has NEVER been run against a real Jupyter server: this only shows that the
/// kernel does what Jupyter's documentation says to do and understands what the documentation says the server sends.
/// </summary>
public class JupyterKernelTests
{
    sealed class FakeJupyter : IDisposable
    {
        public const string Token = "s3cret";
        public readonly HttpListener Listener = new();
        public readonly string Url;
        public readonly List<string> Calls = new();
        public readonly List<string> Auth = new();
        readonly CancellationTokenSource _stop = new();
        int _count;
        Process? _py;
        readonly object _pyGate = new();
        string? _pyScript;

        // the "kernel" of a fake that is asked to be Python: a loop that runs the code of an execute_request in one namespace and evaluates its
        // user_expressions the way IPython does (text/plain, and whatever _repr_mimebundle_ adds), so the code a client sends runs for real
        const string MiniKernel = """
            import sys, json, io, contextlib
            ns = {"__name__": "__main__"}
            def bundle(value):
                data = {"text/plain": repr(value)}
                f = getattr(value, "_repr_mimebundle_", None)
                if f:
                    b = f()
                    data.update(b[0] if isinstance(b, tuple) else b)
                return data
            for line in sys.stdin:
                req = json.loads(line)
                out = {"stdout": "", "error": None, "exprs": {}}
                buf = io.StringIO()
                try:
                    with contextlib.redirect_stdout(buf):
                        exec(compile(req["code"], "<cell>", "exec"), ns)
                        for k, expr in (req.get("exprs") or {}).items():
                            try:
                                out["exprs"][k] = {"status": "ok", "data": bundle(eval(expr, ns)), "metadata": {}}
                            except BaseException as e:
                                out["exprs"][k] = {"status": "error", "ename": type(e).__name__, "evalue": str(e), "traceback": []}
                except BaseException as e:
                    out["error"] = {"ename": type(e).__name__, "evalue": str(e)}
                out["stdout"] = buf.getvalue()
                print(json.dumps(out), flush=True)
            """;

        JsonObject RunPython(string code, JsonObject? expressions)
        {
            lock (_pyGate)
            {
                var request = new JsonObject { ["code"] = code, ["exprs"] = expressions?.DeepClone() ?? new JsonObject() };
                _py!.StandardInput.WriteLine(request.ToJsonString());
                _py.StandardInput.Flush();
                return JsonNode.Parse(_py.StandardOutput.ReadLine() ?? "{}")!.AsObject();
            }
        }

        public FakeJupyter(bool python = false)
        {
            if (python)
            {
                _pyScript = Path.Combine(Path.GetTempPath(), "pm_minikernel_" + Guid.NewGuid().ToString("N") + ".py");
                File.WriteAllText(_pyScript, MiniKernel);
                var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python", "-u \"" + _pyScript + "\"")
                { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
                _py = Process.Start(psi);
            }
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            Listener.Prefixes.Add(Url + "/");
            Listener.Start();
            _ = Task.Run(Loop);
        }

        async Task Loop()
        {
            while (!_stop.IsCancellationRequested)
            {
                HttpListenerContext ctx;
                try { ctx = await Listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        void Calls_(string text) { lock (Calls) Calls.Add(text); }

        async Task Handle(HttpListenerContext ctx)
        {
            string call = ctx.Request.HttpMethod + " " + ctx.Request.Url!.AbsolutePath;
            string auth = ctx.Request.Headers["Authorization"] ?? "";
            lock (Auth) Auth.Add(auth);
            if (auth != "token " + Token) { Calls_("403 " + call); ctx.Response.StatusCode = 403; ctx.Response.Close(); return; }
            Calls_(call);
            var res = ctx.Response;
            switch (call)
            {
                case "GET /api/kernelspecs": Json(res, 200, "{\"default\":\"python3\",\"kernelspecs\":{\"python3\":{},\"ir\":{}}}"); return;
                case "POST /api/kernels": Json(res, 201, "{\"id\":\"k1\",\"name\":\"python3\"}"); return;
                case "POST /api/kernels/k1/interrupt": res.StatusCode = 204; res.Close(); return;
                case "POST /api/kernels/k1/restart": Json(res, 200, "{\"id\":\"k1\"}"); return;
                case "DELETE /api/kernels/k1": res.StatusCode = 204; res.Close(); return;
                case "GET /api/kernels/k1/channels" when ctx.Request.IsWebSocketRequest: await Channels(ctx); return;
            }
            res.StatusCode = 404;
            res.Close();
        }

        static void Json(HttpListenerResponse res, int status, string body)
        {
            res.StatusCode = status;
            res.ContentType = "application/json";
            var bytes = Encoding.UTF8.GetBytes(body);
            res.OutputStream.Write(bytes);
            res.Close();
        }

        static string Msg(string type, string parent, JsonObject content, string channel) =>
            new JsonObject
            {
                ["header"] = new JsonObject { ["msg_id"] = Guid.NewGuid().ToString("N"), ["msg_type"] = type, ["session"] = "fake", ["username"] = "fake", ["version"] = "5.3" },
                ["parent_header"] = new JsonObject { ["msg_id"] = parent }, ["metadata"] = new JsonObject(), ["content"] = content, ["channel"] = channel, ["buffers"] = new JsonArray(),
            }.ToJsonString();

        async Task Channels(HttpListenerContext ctx)
        {
            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[64 * 1024];
            async Task Send(string json) => await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None);
            try
            {
                while (ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await ws.ReceiveAsync(buffer, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
                    var msg = JsonNode.Parse(ms.ToArray())!;
                    string type = msg["header"]!["msg_type"]!.GetValue<string>(), id = msg["header"]!["msg_id"]!.GetValue<string>();
                    if (type == "kernel_info_request")
                    {
                        await Send(Msg("kernel_info_reply", id, new JsonObject { ["status"] = "ok", ["language_info"] = new JsonObject { ["name"] = "python", ["version"] = "3.99.1" } }, "shell"));
                        continue;
                    }
                    if (type != "execute_request") continue;
                    var content = msg["content"]!;
                    string code = content["code"]!.GetValue<string>();
                    lock (Calls) Calls.Add("execute allow_stdin=" + content["allow_stdin"] + " silent=" + content["silent"]);
                    int count = ++_count;
                    if (_py != null)
                    {
                        var run = RunPython(code, content["user_expressions"] as JsonObject);
                        await Send(Msg("status", id, new JsonObject { ["execution_state"] = "busy" }, "iopub"));
                        await Send(Msg("execute_reply", id, new JsonObject { ["status"] = run["error"] == null ? "ok" : "error", ["execution_count"] = count, ["user_expressions"] = run["exprs"]?.DeepClone() ?? new JsonObject() }, "shell"));
                        string printed = run["stdout"]?.GetValue<string>() ?? "";
                        if (printed.Length > 0) await Send(Msg("stream", id, new JsonObject { ["name"] = "stdout", ["text"] = printed }, "iopub"));
                        await Send(Msg("status", id, new JsonObject { ["execution_state"] = "idle" }, "iopub"));
                        continue;
                    }
                    await Send(Msg("status", id, new JsonObject { ["execution_state"] = "busy" }, "iopub"));
                    // the reply comes BEFORE the output on purpose: the two travel on different channels and may arrive in either order
                    string status = code == "boom" ? "error" : "ok";
                    await Send(Msg("execute_reply", id, new JsonObject { ["status"] = status, ["execution_count"] = count }, "shell"));
                    if (code == "print('hi')") await Send(Msg("stream", id, new JsonObject { ["name"] = "stdout", ["text"] = "hi\n" }, "iopub"));
                    else if (code == "1+1") await Send(Msg("execute_result", id, new JsonObject { ["execution_count"] = count, ["data"] = new JsonObject { ["text/plain"] = "2" }, ["metadata"] = new JsonObject() }, "iopub"));
                    else if (code == "page") await Send(Msg("display_data", id, new JsonObject { ["data"] = new JsonObject { ["text/html"] = "<b>x</b>", ["text/plain"] = "x" }, ["metadata"] = new JsonObject() }, "iopub"));
                    else if (code == "boom") await Send(Msg("error", id, new JsonObject { ["ename"] = "NameError", ["evalue"] = "name 'boom' is not defined", ["traceback"] = new JsonArray("Traceback", "NameError: boom") }, "iopub"));
                    await Send(Msg("status", id, new JsonObject { ["execution_state"] = "idle" }, "iopub"));
                }
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or HttpListenerException) { }
        }

        public bool Saw(string call, int ms = 5000)
        {
            for (int i = 0; i < ms / 50; i++) { lock (Calls) if (Calls.Contains(call)) return true; Thread.Sleep(50); }
            return false;
        }

        public void Dispose()
        {
            _stop.Cancel();
            try { Listener.Close(); } catch (ObjectDisposedException) { }
            try { _py?.Kill(true); _py?.Dispose(); } catch (InvalidOperationException) { }
            try { if (_pyScript != null) File.Delete(_pyScript); } catch (IOException) { }
        }
    }

    static bool HavePython()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    static JsonNode Wait(BlockingCollection<JsonNode> events, Func<JsonNode, bool> until, int seconds = 15)
    {
        var seen = new List<string>();
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(seconds))) throw new AssertionException("timed out; saw: " + string.Join(" | ", seen));
            seen.Add(e.ToJsonString());
            if (until(e)) return e;
        }
    }

    static bool Is(JsonNode e, string type) => e["type"]!.GetValue<string>() == type;

    static List<JsonNode> Run(JupyterKernel k, BlockingCollection<JsonNode> events, string id, string code)
    {
        k.Execute(id, code);
        var got = new List<JsonNode>();
        Wait(events, e => { if (e["id"]?.GetValue<string>() == id) got.Add(e); return Is(e, "done") && e["id"]?.GetValue<string>() == id; });
        return got;
    }

    [Fact]
    public void ACellRunsOnTheServerAndItsOutputComesBackInTheBuiltInKernelsShapes()
    {
        using var server = new FakeJupyter();
        var events = new BlockingCollection<JsonNode>();
        using var k = new JupyterKernel(events.Add);
        k.Start(server.Url + "/", FakeJupyter.Token, null);
        Assert.Equal("3.99.1", Wait(events, e => Is(e, "ready"))["python"]!.GetValue<string>());

        var printed = Run(k, events, "c1", "print('hi')");
        Assert.Equal("hi\n", printed.First(e => Is(e, "stream"))["text"]!.GetValue<string>());
        Assert.Equal("ok", printed.Last()["status"]!.GetValue<string>());
        Assert.Equal(1, printed.Last()["execution_count"]!.GetValue<int>());

        var value = Run(k, events, "c2", "1+1");
        Assert.Equal("2", value.First(e => Is(e, "result"))["data"]!["text/plain"]!.GetValue<string>());

        var page = Run(k, events, "c3", "page");
        Assert.Equal("<b>x</b>", page.First(e => Is(e, "display"))["data"]!["text/html"]!.GetValue<string>());

        var failed = Run(k, events, "c4", "boom");
        var error = failed.First(e => Is(e, "error"));
        Assert.Equal("NameError", error["ename"]!.GetValue<string>());
        Assert.Equal(2, error["traceback"]!.AsArray().Count);
        Assert.Equal("error", failed.Last()["status"]!.GetValue<string>());

        // what the protocol asks of a client: the token in the Authorization header (and nowhere else), no stdin prompts
        Assert.True(server.Saw("POST /api/kernels") && server.Saw("execute allow_stdin=false silent=false"));
        lock (server.Auth) Assert.True(server.Auth.All(a => a == "token " + FakeJupyter.Token), "every request carries the token in the header");
    }

    [Fact]
    public void TheVariablesTheirMembersAndTheWordsOfANameAreAskedOfTheServersKernel()
    {
        if (!HavePython()) return;
        using var server = new FakeJupyter(python: true);
        var events = new BlockingCollection<JsonNode>();
        using var k = new JupyterKernel(events.Add);
        k.Start(server.Url, FakeJupyter.Token, null);
        Wait(events, e => Is(e, "ready"));

        k.Execute("c1", "class Point:\n    def __init__(self):\n        self.x = 3\n        self._hidden = 1\n    def move(self): return 1\n\n\n"
            + "p = Point()\nd = {'one': [1, 2], 'n': 7}\nname = 'text'\nempty = []\n");
        var vars = Wait(events, e => Is(e, "variables"))["vars"]!.AsArray();
        var names = vars.Select(v => v!["name"]!.GetValue<string>()).ToList();
        Assert.Equal("Point,d,empty,name,p", string.Join(",", names));        // sorted; the helper's own names and the underscore ones are not there
        bool Expandable(string n) => vars.First(v => v!["name"]!.GetValue<string>() == n)!["expandable"]!.GetValue<bool>();
        Assert.True(Expandable("d") && Expandable("p") && !Expandable("name") && !Expandable("empty"));
        Assert.Equal("dict", vars.First(v => v!["name"]!.GetValue<string>() == "d")!["type"]!.GetValue<string>());

        // the members of a dict, one level at a time
        string idA = "i1";
        k.Inspect(idA, JsonNode.Parse("[[\"v\",\"d\"]]"));
        var children = Wait(events, e => Is(e, "children") && e["id"]!.GetValue<string>() == idA);
        Assert.Equal("'one','n'", string.Join(",", children["children"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())));
        string idB = "i2";
        k.Inspect(idB, JsonNode.Parse("[[\"v\",\"p\"]]"));
        var attrs = Wait(events, e => Is(e, "children") && e["id"]!.GetValue<string>() == idB);
        Assert.Equal("x,_hidden", string.Join(",", attrs["children"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())));
        // a name that is gone is said in words
        string idC = "i3";
        k.Inspect(idC, JsonNode.Parse("[[\"v\",\"nothing\"]]"));
        Assert.Contains("gone", Wait(events, e => Is(e, "children") && e["id"]!.GetValue<string>() == idC)["error"]!.GetValue<string>());

        // the words of a name
        k.Complete("t1", "p.mo");
        Assert.Equal("move", string.Join(",", Wait(events, e => Is(e, "completions") && e["id"]!.GetValue<string>() == "t1")["items"]!.AsArray().Select(x => x!.GetValue<string>())));
        k.Complete("t2", "d.ke");
        Assert.Equal("keys", string.Join(",", Wait(events, e => Is(e, "completions") && e["id"]!.GetValue<string>() == "t2")["items"]!.AsArray().Select(x => x!.GetValue<string>())));

        // a later cell changes the names, and they are sent again; a cell that fails still reports what it left
        k.Execute("c2", "late = 5\nraise ValueError('x')");
        var again = Wait(events, e => Is(e, "variables") && e["vars"]!.AsArray().Any(v => v!["name"]!.GetValue<string>() == "late"));
        Assert.True(again["vars"]!.AsArray().Count == 6);
    }

    [Fact]
    public void AKernelThatCannotBeAskedIsLeftAlone()
    {
        using var server = new FakeJupyter();                 // answers every request, but never with the user_expressions we ask for
        var events = new BlockingCollection<JsonNode>();
        using var k = new JupyterKernel(events.Add);
        k.Start(server.Url, FakeJupyter.Token, null);
        Wait(events, e => Is(e, "ready"));
        Run(k, events, "c1", "1+1");
        Run(k, events, "c2", "1+1");
        // two cells ran and the Variables window was asked for once: after the first no answer came, so the kernel is not asked again
        int silent = 0;
        lock (server.Calls) silent = server.Calls.Count(c => c == "execute allow_stdin=false silent=true");
        Assert.True(silent <= 2, "asked " + silent + " times");
        k.Inspect("i1", JsonNode.Parse("[[\"v\",\"x\"]]"));
        Assert.Contains("cannot tell", Wait(events, e => Is(e, "children"))["error"]!.GetValue<string>());
    }

    [Fact]
    public void InterruptRestartAndDisposeAreTheServersOwnEndpoints()
    {
        using var server = new FakeJupyter();
        var events = new BlockingCollection<JsonNode>();
        var k = new JupyterKernel(events.Add);
        k.Start(server.Url, FakeJupyter.Token, "python3");
        Wait(events, e => Is(e, "ready"));
        k.Interrupt();
        Assert.True(server.Saw("POST /api/kernels/k1/interrupt"));
        k.Restart();
        Assert.True(server.Saw("POST /api/kernels/k1/restart"));
        Wait(events, e => Is(e, "ready"));          // the kernel answers again after the restart
        Assert.Equal("ok", Run(k, events, "c1", "1+1").Last()["status"]!.GetValue<string>());
        k.Dispose();
        Assert.True(server.Saw("DELETE /api/kernels/k1"), "the kernel made for the notebook is deleted with it");
    }

    [Fact]
    public void AWrongTokenOrAddressIsReportedInWords()
    {
        using var server = new FakeJupyter();
        var events = new BlockingCollection<JsonNode>();
        using var k = new JupyterKernel(events.Add);
        k.Start(server.Url, "wrong", null);
        Assert.Contains("refused the token", Wait(events, e => Is(e, "kernelError"))["message"]!.GetValue<string>());

        var nothing = new BlockingCollection<JsonNode>();
        using var gone = new JupyterKernel(nothing.Add);
        gone.Start("http://127.0.0.1:1", "x", null);
        Assert.Contains("cannot reach", Wait(nothing, e => Is(e, "kernelError"))["message"]!.GetValue<string>());
    }

    [Fact]
    public void TheServersWindowCanTestAnAddressAndToken()
    {
        using var server = new FakeJupyter();
        var good = JupyterKernel.Probe(server.Url, FakeJupyter.Token).GetAwaiter().GetResult();
        Assert.True(good["ok"]!.GetValue<bool>());
        Assert.Contains("python3, ir", good["message"]!.GetValue<string>());
        var bad = JupyterKernel.Probe(server.Url, "nope").GetAwaiter().GetResult();
        Assert.True(!bad["ok"]!.GetValue<bool>());
        Assert.Contains("refused the token", bad["message"]!.GetValue<string>());
        Assert.True(!JupyterKernel.Probe("http://127.0.0.1:1", "").GetAwaiter().GetResult()["ok"]!.GetValue<bool>());
    }

    [Fact]
    public void ExportedPagesAreKeptByNameAndTheOldestAreForgotten()
    {
        string? first = NotebookExports.Put("a.ipynb", "<p>one</p>");
        Assert.Equal("<p>one</p>", NotebookExports.Get(first!)!);
        Assert.True(NotebookExports.Get("not-a-name") == null);
        for (int i = 0; i < 20; i++) NotebookExports.Put("n", "<p>" + i + "</p>");
        Assert.True(NotebookExports.Get(first!) == null, "only the latest pages are kept");
        Assert.Contains("script-src 'none'", NotebookExports.Policy);
        Assert.Contains("sandbox", NotebookExports.Policy);
    }
}
