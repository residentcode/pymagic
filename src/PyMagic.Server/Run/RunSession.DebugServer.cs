using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

// The debug server: the IDE listens, and a program started elsewhere (another computer, a container, a shell of its own)
// calls pm_debug.connect(host, port, token) to be debugged here, as PyCharm's "Python Debug Server" and pydevd.settrace do.
public sealed partial class RunSession
{
    PathMap? _map;           // the path mappings of the debug server (null: the files are named alike on both sides)
    bool _serving;
    bool _servingEnded;
    readonly Dictionary<string, string> _sticky = new();   // what the page set for the debugger (breakpoints, exception stops...), told to each program that connects

    /// <summary>A program connected to the debug server; <c>Stop</c> is its last <c>stopped</c> message while it is stopped.</summary>
    sealed class Debuggee
    {
        public required int Number { get; init; }
        public required string From { get; init; }
        public required Stream Stream { get; init; }
        public JsonNode? Stop;
    }

    // the programs connected now, and the one whose stop the page shows (the commands of a stopped program go to it)
    readonly List<Debuggee> _programs = new();
    Debuggee? _shown;
    int _connections;

    /// <summary>
    /// A command of the page while serving: what holds for every program (breakpoints, exception stops, Pause) goes to each; the rest
    /// (stepping, variables, evaluate) to the program whose stop is shown (with <c>_gate</c> held).
    /// </summary>
    void SendToProgramsLocked(string type, string json)
    {
        if (type is "setBreakpoints" or "setExceptionBreak" or "setFunctionBreaks" or "mute" or "setLoadPolicy" or "pause")
            foreach (var p in _programs) WriteToLocked(p, json);
        else if ((_shown ?? _programs.FirstOrDefault()) is { } one) WriteToLocked(one, json);
    }

    static void WriteToLocked(Debuggee p, string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            p.Stream.Write(bytes);
            p.Stream.Flush();
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException) { /* it went away: its reader says so */ }
    }

    /// <summary>
    /// What the page is told of a program's message: a stop is shown when no other program's stop is (else it waits for that one to go on),
    /// and a program that goes on brings the next waiting stop up. Of a program whose stop is not shown, only output and answers are told.
    /// </summary>
    List<JsonNode> ArrangeLocked(Debuggee p, JsonNode msg)
    {
        var tell = new List<JsonNode>();
        string? type = msg["type"]?.GetValue<string>();
        if (type == "stopped")
        {
            p.Stop = msg;
            if (_programs.Count > 1) msg["program"] = p.Number;
            if (_shown == null || _shown == p) { _shown = p; tell.Add(msg); }
            else tell.Add(Note($"Program {p.Number} ({p.From}) stopped too; it is shown when program {_shown.Number} goes on.\n"));
        }
        else if (type == "running")
        {
            p.Stop = null;
            if (_shown == p) { _shown = null; tell.Add(msg); tell.AddRange(NextStopLocked()); }
        }
        else tell.Add(msg);
        return tell;
    }

    /// <summary>The stop of another program that waits to be shown, if one does (with <c>_gate</c> held).</summary>
    List<JsonNode> NextStopLocked()
    {
        if (_programs.FirstOrDefault(x => x.Stop != null) is not { } next) return new();
        _shown = next;
        if (_programs.Count > 1) next.Stop!["program"] = next.Number;
        return new() { Note($"Program {next.Number} ({next.From}) is stopped:\n"), JsonNode.Parse(next.Stop!.ToJsonString())! };
    }

    static JsonNode Note(string text) => new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = text };

    string ToRemote(string local) => _map != null ? _map.ToRemote(local) : Target.ToTarget(local);
    string ToLocal(string remote) => _map != null ? _map.ToLocal(remote) : Target.ToHost(remote);

    /// <summary>The settings a debug server keeps for the programs that connect later (with <c>_gate</c> held).</summary>
    void RememberLocked(string type, JsonNode msg, string json)
    {
        if (type == "setBreakpoints" && msg["file"]?.GetValue<string>() is { } file) _sticky["bp:" + file] = json;
        else if (type is "setExceptionBreak" or "mute" or "setLoadPolicy" or "setFunctionBreaks") _sticky[type] = json;
    }

    /// <summary>The address to listen on: an IP address, "localhost", or "0.0.0.0" / "*" for every network interface.</summary>
    public static IPAddress? ParseListenHost(string host)
    {
        host = host.Trim();
        if (host.Length == 0 || host.Equals("localhost", StringComparison.OrdinalIgnoreCase)) return IPAddress.Loopback;
        if (host == "*") return IPAddress.Any;
        return IPAddress.TryParse(host, out var ip) ? ip : null;
    }

    /// <summary>
    /// Starts the debug server: listens on <paramref name="host"/>:<paramref name="port"/> (0: a free port) and debugs every
    /// program that connects with the right token, several at once, until stopped. Announces itself as <c>listening</c>
    /// with the snippet to put in the program; then <c>connected</c> / <c>disconnected</c> per program (with its number and how many
    /// are connected) and the debugger's own messages. The breakpoints hold for every program; one program's stop is shown at a
    /// time, and another's comes up when it goes on. Stop detaches from the connected programs (they go on running).
    /// </summary>
    /// <param name="connectHost">The address the program reaches this computer by, for the snippet; empty: this computer's name.</param>
    public void Listen(string root, string host, int port, string connectHost, IReadOnlyList<(string Local, string Remote)> mappings,
        IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        IsDebug = true;
        var address = ParseListenHost(host);
        if (address == null) { Error($"'{host}' is not an address to listen on (an IP address, localhost, or * for every interface)"); return; }
        if (port is < 0 or > 65535) { Error($"'{port}' is not a port"); return; }
        _map = mappings.Count > 0 ? PathMap.Parse(root, mappings) : null;
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(12)).ToLowerInvariant();
        try
        {
            _listener = new TcpListener(address, port);
            _listener.Start();
        }
        catch (SocketException e)
        {
            _listener = null;
            Error($"cannot listen on {address}:{port}: {e.Message}");
            return;
        }
        int actual = ((IPEndPoint)_listener.LocalEndpoint).Port;
        string helperDir = Path.GetDirectoryName(ExtractHelper("pm_debug.py"))!;
        string reach = connectHost.Trim().Length > 0 ? connectHost.Trim() : address.Equals(IPAddress.Loopback) ? "127.0.0.1" : Dns.GetHostName();
        // the program's own project folder: the first mapping's, else (the files are the same on both sides) the project
        string remoteRoot = _map?.Entries.Count > 0 ? _map.Entries[0].Remote : root;

        lock (_gate)
        {
            _serving = true;
            if (breakpoints != null)
                foreach (var (file, lines) in breakpoints)
                {
                    string full = Path.GetFullPath(Path.Combine(root, file));
                    var m = new JsonObject { ["type"] = "setBreakpoints", ["file"] = ToRemote(full), ["lines"] = JsonNode.Parse(lines.ToJsonString()) };
                    _sticky["bp:" + (string)m["file"]!] = m.ToJsonString();
                }
        }
        _emit(new JsonObject
        {
            ["type"] = "listening", ["host"] = address.ToString(), ["port"] = actual, ["token"] = token, ["helper"] = helperDir,
            ["snippet"] = Snippet(reach, actual, token, remoteRoot, helperDir), ["mapped"] = _map?.Entries.Count ?? 0,
        });
        _emit(new JsonObject { ["type"] = "started", ["pid"] = 0, ["debug"] = true, ["listening"] = true });
        var listener = _listener;
        Task.Run(() =>
        {
            while (!_servingEnded)
            {
                TcpClient client;
                try { client = listener.AcceptTcpClient(); }
                catch (Exception e) when (e is SocketException or ObjectDisposedException or InvalidOperationException) { break; }
                Task.Run(() => Serve(client, token));
            }
        });
    }

    /// <summary>What to put in the program, with the folder of pm_debug.py.</summary>
    public static string Snippet(string host, int port, string token, string remoteRoot, string helperDir)
    {
        string Q(string s) => PyString(s);
        return "# copy pm_debug.py (from " + helperDir + ") next to the program, or into a folder on its PYTHONPATH\n" +
            "import pm_debug\n" +
            $"pm_debug.connect({Q(host)}, {port}, {Q(token)}, root={Q(remoteRoot)})";
    }

    void Serve(TcpClient client, string token)
    {
        string who = client.Client.RemoteEndPoint?.ToString() ?? "a program";
        Debuggee? me = null;
        try
        {
            var stream = client.GetStream();
            stream.ReadTimeout = 5000;
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            string? hello;
            try { hello = reader.ReadLine(); } catch (IOException) { hello = null; }
            bool ok = false;
            try { ok = hello != null && JsonNode.Parse(hello)?["token"]?.GetValue<string>() == token; } catch (System.Text.Json.JsonException) { }
            if (!ok)
            {
                client.Dispose();
                Output($"A connection from {who} was refused: it did not give this debug server's token.\n");
                return;
            }
            int count;
            lock (_gate)
            {
                if (_servingEnded) { client.Dispose(); return; }
                stream.ReadTimeout = Timeout.Infinite;
                me = new Debuggee { Number = ++_connections, From = who, Stream = stream };
                _programs.Add(me);
                count = _programs.Count;
                foreach (var json in _sticky.Values) WriteToLocked(me, json);
                WriteToLocked(me, "{\"type\":\"go\"}");
            }
            _emit(new JsonObject { ["type"] = "connected", ["from"] = who, ["program"] = me.Number, ["programs"] = count });

            string? text;
            while ((text = reader.ReadLine()) != null)
            {
                var msg = JsonNode.Parse(text);
                if (msg == null) continue;
                // the program names its files as its machine does: the editor knows them as files of this computer
                var all = new List<JsonNode?>();
                if (msg["frames"] is JsonArray frames) all.AddRange(frames);
                if (msg["threads"] is JsonArray threads)
                    foreach (var t in threads) if (t?["frames"] is JsonArray tf) all.AddRange(tf);
                foreach (var f in all) if (f is JsonObject fo && fo["file"]?.GetValue<string>() is { } file) fo["file"] = ToLocal(file);
                List<JsonNode> tell;
                lock (_gate) tell = ArrangeLocked(me, msg);
                foreach (var m in tell) _emit(m);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (System.Text.Json.JsonException) { }
        client.Dispose();
        if (me == null) return;
        var after = new List<JsonNode>();
        int left;
        lock (_gate)
        {
            _programs.Remove(me);
            left = _programs.Count;
            // its stop was the one shown: the page lets it go, and the next waiting stop comes up
            if (_shown == me) { _shown = null; after.Add(new JsonObject { ["type"] = "running" }); after.AddRange(NextStopLocked()); }
        }
        if (_servingEnded) return;
        _emit(new JsonObject { ["type"] = "disconnected", ["program"] = me.Number, ["programs"] = left });
        foreach (var m in after) _emit(m);
    }

    void Output(string text) => _emit(new JsonObject { ["type"] = "output", ["stream"] = "stderr", ["text"] = text });

    /// <summary>Stop: detaches from the programs that are connected (they run on), stops listening, and ends the session.</summary>
    void StopServing()
    {
        lock (_gate)
        {
            if (_servingEnded) return;
            _servingEnded = true;
            foreach (var p in _programs) WriteToLocked(p, "{\"type\":\"detach\"}");
            try { _listener?.Stop(); } catch (SocketException) { }
        }
        _emit(new JsonObject { ["type"] = "exited", ["code"] = 0, ["detached"] = true });
    }
}
