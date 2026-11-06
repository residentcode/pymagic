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
/// Debug Cell on a Jupyter server on another computer: the debugger in the kernel talks to the IDE over the kernel's own channels (display data
/// on IOPub one way, answers to input requests on the stdin channel the other), so the kernel never connects to the IDE. Against a small server
/// written here whose "kernel" is a real Python with the parts of ipykernel's kernel object that pm_jdebug.py uses (session.send on IOPub,
/// _input_request, the parent of the message it works on); the debugger (pm_debug.py) and the IDE's debug server are the real ones. It has NOT
/// been run against a real ipykernel (none is installed here); LiveJupyterTests does when PYMAGIC_TEST_JUPYTER names one.
/// </summary>
public class JupyterDebugChannelTests
{
    // the kernel: cells run on its main thread one after the other; what it sends and is answered goes over its standard streams as JSON lines
    const string Kernel = """
        import sys, json, threading, queue, io, traceback
        lock = threading.Lock()
        real_out = sys.stdout
        def emit(obj):
            with lock:
                real_out.write(json.dumps(obj) + "\n")
                real_out.flush()
        replies, jobs, current = queue.Queue(), queue.Queue(), {"id": None}
        class Session:
            def send(self, socket, msg_type, content, parent=None, ident=None):
                emit({"op": "iopub", "type": msg_type, "content": content, "parent": (parent or {}).get("header", {}).get("msg_id")})
        class Kernel:
            session = Session()
            iopub_socket = object()
            # ipykernel 7 keeps the idents per channel in a LazyDict: a Mapping that is not a dict
            class LazyDict:
                def __init__(self, d): self._d = d
                def __getitem__(self, key): return self._d[key]
                def __iter__(self): return iter(self._d)
                def __len__(self): return len(self._d)
            _parent_ident = LazyDict({"shell": b"ident"})
            def get_parent(self, channel="shell"):
                return {"header": {"msg_id": current["id"]}}
            def _input_request(self, prompt, ident, parent, password=False):
                if ident != b"ident":
                    raise TypeError("the shell's ident was not given: %r" % (ident,))
                emit({"op": "input_request", "prompt": prompt, "parent": parent["header"]["msg_id"]})
                return replies.get()
        class Shell:
            kernel = Kernel()
            def transform_cell(self, code):
                return code
            def showtraceback(self):
                traceback.print_exc(file=sys.stdout)
        shell = Shell()
        class Out(io.TextIOBase):
            def write(self, s):
                if s:
                    emit({"op": "iopub", "type": "stream", "content": {"name": "stdout", "text": s}, "parent": current["id"]})
                return len(s)
        def reader():
            for line in sys.stdin:
                m = json.loads(line)
                (replies if m["op"] == "input_reply" else jobs).put(m.get("value") if m["op"] == "input_reply" else m)
        # the thread that moves the messages is the kernel's IOPub thread here (ipykernel has kernel.iopub_thread.thread)
        mover = threading.Thread(target=reader, daemon=True, name="IOPub")
        mover.start()
        class Holder:
            thread = mover
        Kernel.iopub_thread = Holder()
        sys.stdout = Out()
        ns = {"__name__": "__main__", "get_ipython": lambda: shell}
        while True:
            job = jobs.get()
            current["id"] = job["id"]
            error = None
            try:
                exec(compile(job["code"], "<cell>", "exec"), ns)
            except BaseException as e:
                error = type(e).__name__ + ": " + str(e)
            emit({"op": "done", "id": job["id"], "error": error})
        """;

    sealed class Server : IDisposable
    {
        readonly HttpListener _listener = new();
        public readonly string Url;
        readonly Process _py;
        readonly string _script;
        readonly SemaphoreSlim _sending = new(1, 1);
        WebSocket? _ws;
        int _count;
        public readonly List<string> Seen = new();   // what the kernel sent and was sent, by kind

        public Server(string cwd)
        {
            _script = Path.Combine(Path.GetTempPath(), "pm_jdbgkernel_" + Guid.NewGuid().ToString("N") + ".py");
            File.WriteAllText(_script, Kernel);
            _py = Process.Start(new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python", "-u \"" + _script + "\"")
            { RedirectStandardInput = true, RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = cwd, StandardOutputEncoding = Encoding.UTF8 })!;
            var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            int port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();
            Url = $"http://127.0.0.1:{port}";
            _listener.Prefixes.Add(Url + "/");
            _listener.Start();
            _ = Task.Run(Loop);
            _ = Task.Run(FromKernel);
        }

        async Task Loop()
        {
            while (true)
            {
                HttpListenerContext ctx;
                try { ctx = await _listener.GetContextAsync(); }
                catch (Exception e) when (e is HttpListenerException or ObjectDisposedException or InvalidOperationException) { return; }
                _ = Task.Run(() => Handle(ctx));
            }
        }

        async Task Handle(HttpListenerContext ctx)
        {
            string call = ctx.Request.HttpMethod + " " + ctx.Request.Url!.AbsolutePath;
            Note("http " + call);
            if (call == "GET /api/kernels/k1/channels" && ctx.Request.IsWebSocketRequest) { await Channels(ctx); return; }
            ctx.Response.StatusCode = call switch { "POST /api/kernels" => 201, "DELETE /api/kernels/k1" => 204, _ => 404 };
            if (call == "POST /api/kernels") { var b = Encoding.UTF8.GetBytes("{\"id\":\"k1\"}"); ctx.Response.ContentType = "application/json"; ctx.Response.OutputStream.Write(b); }
            ctx.Response.Close();
        }

        static string Msg(string type, string? parent, JsonObject content, string channel) => new JsonObject
        {
            ["header"] = new JsonObject { ["msg_id"] = Guid.NewGuid().ToString("N"), ["msg_type"] = type, ["session"] = "fake", ["username"] = "fake", ["version"] = "5.3" },
            ["parent_header"] = new JsonObject { ["msg_id"] = parent }, ["metadata"] = new JsonObject(), ["content"] = content, ["channel"] = channel, ["buffers"] = new JsonArray(),
        }.ToJsonString();

        async Task Send(string json)
        {
            await _sending.WaitAsync();
            try { if (_ws is { State: WebSocketState.Open } ws) await ws.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, true, CancellationToken.None); }
            finally { _sending.Release(); }
        }

        void ToKernel(JsonObject m) { lock (_py) { _py.StandardInput.WriteLine(m.ToJsonString()); _py.StandardInput.Flush(); } }

        void Note(string what) { lock (Seen) Seen.Add(what); }

        async Task Channels(HttpListenerContext ctx)
        {
            _ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            var buffer = new byte[256 * 1024];
            try
            {
                while (_ws.State == WebSocketState.Open)
                {
                    using var ms = new MemoryStream();
                    WebSocketReceiveResult r;
                    do { r = await _ws.ReceiveAsync(buffer, CancellationToken.None); if (r.MessageType == WebSocketMessageType.Close) return; ms.Write(buffer, 0, r.Count); } while (!r.EndOfMessage);
                    var msg = JsonNode.Parse(ms.ToArray())!;
                    string type = msg["header"]!["msg_type"]!.GetValue<string>(), id = msg["header"]!["msg_id"]!.GetValue<string>();
                    if (type == "kernel_info_request")
                        await Send(Msg("kernel_info_reply", id, new JsonObject { ["status"] = "ok", ["language_info"] = new JsonObject { ["name"] = "python", ["version"] = "3.99.1" } }, "shell"));
                    else if (type == "execute_request")
                    {
                        Note("execute allow_stdin=" + msg["content"]!["allow_stdin"]);
                        await Send(Msg("status", id, new JsonObject { ["execution_state"] = "busy" }, "iopub"));
                        ToKernel(new JsonObject { ["op"] = "execute", ["id"] = id, ["code"] = msg["content"]!["code"]!.GetValue<string>() });
                    }
                    else if (type == "input_reply")
                    {
                        Note("input_reply on " + msg["channel"]);
                        ToKernel(new JsonObject { ["op"] = "input_reply", ["value"] = msg["content"]!["value"]!.GetValue<string>() });
                    }
                }
            }
            catch (Exception e) when (e is WebSocketException or ObjectDisposedException or HttpListenerException) { }
        }

        async Task FromKernel()
        {
            string? line;
            while ((line = await _py.StandardOutput.ReadLineAsync()) != null)
            {
                JsonNode? m;
                try { m = JsonNode.Parse(line); } catch (System.Text.Json.JsonException) { continue; }
                string op = m?["op"]?.GetValue<string>() ?? "";
                string? parent = m?["parent"]?.GetValue<string>() ?? m?["id"]?.GetValue<string>();
                switch (op)
                {
                    case "iopub":
                        string type = m!["type"]!.GetValue<string>();
                        Note(type == "display_data" && m["content"]?["data"]?[JupyterKernel.DebugMime] != null ? "debug data" : type);
                        await Send(Msg(type, parent, (JsonObject)m["content"]!.DeepClone(), "iopub"));
                        break;
                    case "input_request":
                        Note("input_request");
                        await Send(Msg("input_request", parent, new JsonObject { ["prompt"] = m!["prompt"]!.GetValue<string>(), ["password"] = false }, "stdin"));
                        break;
                    case "done":
                        string? error = m!["error"]?.GetValue<string>();
                        if (error != null) await Send(Msg("stream", parent, new JsonObject { ["name"] = "stderr", ["text"] = error }, "iopub"));
                        await Send(Msg("execute_reply", parent, new JsonObject { ["status"] = error == null ? "ok" : "error", ["execution_count"] = ++_count }, "shell"));
                        await Send(Msg("status", parent, new JsonObject { ["execution_state"] = "idle" }, "iopub"));
                        break;
                }
            }
        }

        public void Dispose()
        {
            try { _listener.Close(); } catch (ObjectDisposedException) { }
            try { _py.Kill(true); _py.Dispose(); } catch (InvalidOperationException) { }
            try { File.Delete(_script); } catch (IOException) { }
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

    static JsonNode Wait(BlockingCollection<JsonNode> events, Func<JsonNode, bool> until, int seconds = 30)
    {
        var seen = new List<string>();
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(seconds))) { string kernel; lock (Current?.Seen ?? new()) kernel = string.Join(", ", Current?.Seen ?? new()); throw new AssertionException("timed out; saw: " + string.Join(" | ", seen) + "; the server saw: " + kernel); }
            seen.Add(e.ToJsonString());
            if (until(e)) return e;
        }
    }

    static Server? Current;

    static bool Is(JsonNode e, string type) => e["type"]?.GetValue<string>() == type;

    [Fact]
    public void ACellOnAServerElsewhereIsDebuggedOverTheKernelsOwnChannels()
    {
        if (!HavePython()) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_jdbgchan_" + Guid.NewGuid().ToString("N"));
        // the kernel has the project in a folder of its own, as one on another computer does
        string kernelRoot = root + "_kernel";
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(kernelRoot);
        try
        {
            using var server = new Server(kernelRoot);
            Current = server;
            var kernelEvents = new BlockingCollection<JsonNode>();
            using var k = new JupyterKernel(kernelEvents.Add);
            k.Start(server.Url, "", null);
            try { Wait(kernelEvents, e => Is(e, "ready")); } catch (AssertionException) { lock (server.Seen) throw new AssertionException("no ready; the server saw: " + string.Join(", ", server.Seen)); }

            var ide = new BlockingCollection<JsonNode>();
            using var session = new RunSession(ide.Add);
            string file = ".pymagic/notebooks/nb.cell1.py";
            session.Listen(root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file] = new JsonArray(2) });
            var listening = Wait(ide, e => Is(e, "listening"));

            k.Execute("seed", "seed = 100");
            Wait(kernelEvents, e => Is(e, "done") && e["id"]!.GetValue<string>() == "seed");
            var debug = new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening["port"]!.GetValue<int>(), ["token"] = listening["token"]!.GetValue<string>(), ["file"] = file, ["via"] = "kernel", ["root"] = root };
            k.Execute("dbg1", "x = 5\ny = x * 2 + seed\nz = y + 1\nprint('z', z)\n", debug);

            // the debug server sees an ordinary program, which stops at the breakpoint of the cell: the breakpoint named as the kernel
            // has the file, the stop as this side has it
            Wait(ide, e => Is(e, "connected"));
            var stopped = Wait(ide, e => Is(e, "stopped"), 60);
            var top = stopped["frames"]![0]!;
            Assert.Equal(2, top["line"]!.GetValue<int>());
            Assert.Equal(Path.GetFullPath(Path.Combine(root, file)), top["file"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "x + seed" });
            Assert.Equal("105", Wait(ide, e => Is(e, "evaluated"))["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "next" });
            Assert.Equal(3, Wait(ide, e => Is(e, "stopped"))["frames"]![0]!["line"]!.GetValue<int>());
            session.Handle(new JsonObject { ["type"] = "continue" });

            var got = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "dbg1") got.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "dbg1"; }, 60);
            Assert.Equal("z 111\n", string.Concat(got.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
            // the debugger's messages are not output of the cell
            Assert.True(!got.Any(e => Is(e, "display")), "the debugger's display data was shown: " + string.Join(" ", got.Select(e => e.ToJsonString())));
            Assert.Equal("ok", got.Last()["status"]!.GetValue<string>());
            Wait(ide, e => Is(e, "disconnected"));
            lock (server.Seen)
            {
                Assert.True(server.Seen.Contains("execute allow_stdin=true"), string.Join(", ", server.Seen.Distinct()));
                Assert.True(server.Seen.Contains("debug data") && server.Seen.Contains("input_reply on stdin"), string.Join(", ", server.Seen.Distinct()));
            }

            // the namespace is the notebook's own, and the next cell runs without the debugger (and without stdin)
            k.Execute("after", "print(y)");
            var after = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "after") after.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "after"; }, 60);
            Assert.Equal("110\n", string.Concat(after.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
            lock (server.Seen) Assert.Equal("execute allow_stdin=false", server.Seen.Last(s => s.StartsWith("execute", StringComparison.Ordinal)));
        }
        finally
        {
            foreach (var dir in new[] { root, kernelRoot })
                try { Directory.Delete(dir, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void StopEndsTheDebuggerInTheKernelAndTheCellGoesOn()
    {
        if (!HavePython()) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_jdbgstop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            using var server = new Server(root);
            Current = server;
            var kernelEvents = new BlockingCollection<JsonNode>();
            using var k = new JupyterKernel(kernelEvents.Add);
            k.Start(server.Url, "", null);
            Wait(kernelEvents, e => Is(e, "ready"));
            var ide = new BlockingCollection<JsonNode>();
            using var session = new RunSession(ide.Add);
            string file = ".pymagic/notebooks/nb.cell2.py";
            session.Listen(root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file] = new JsonArray(1) });
            var listening = Wait(ide, e => Is(e, "listening"));
            var debug = new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening["port"]!.GetValue<int>(), ["token"] = listening["token"]!.GetValue<string>(), ["file"] = file, ["via"] = "kernel" };
            k.Execute("dbg2", "a = 1\nprint('after', a + 1)\n", debug);
            Wait(ide, e => Is(e, "stopped"), 60);
            // Stop of the Run panel: the debugger detaches, and the cell runs to its end
            session.Handle(new JsonObject { ["type"] = "stop" });
            var got = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "dbg2") got.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "dbg2"; }, 60);
            Assert.Equal("after 2\n", string.Concat(got.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }
}
