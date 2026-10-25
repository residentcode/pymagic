using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// Runs a Python script, optionally under the bundled debugger (pm_debug.py), and relays everything as JSON messages:
/// <c>started</c>, <c>output</c> (stdout/stderr text as it arrives), <c>exited</c>, and for debug sessions the helper's
/// <c>stopped</c>/<c>running</c>/<c>scopes</c>/<c>variables</c>/<c>evaluated</c> messages.
/// </summary>
public sealed class RunSession : IDisposable
{
    static readonly HashSet<string> DebugCommands = new()
    {
        "setBreakpoints", "setExceptionBreak", "continue", "next", "stepIn", "stepOut", "scopes", "variables", "evaluate", "setVariable",
    };

    readonly Action<JsonNode> _emit;
    readonly object _gate = new();
    Process? _process;
    TcpListener? _listener;
    Stream? _channel;                       // the debugger socket once it connected
    readonly List<string> _pending = new(); // debugger messages sent before it connected
    bool _disposed;
    string? _root;

    /// <summary>Interpreter to run, as its target names it; falls back to the PYMAGIC_PYTHON environment variable, then "python".</summary>
    public string? Python { get; set; }

    /// <summary>Where the interpreter runs (this computer, WSL or Docker).</summary>
    public ExecutionTarget Target { get; set; } = ExecutionTarget.Local;
    public bool IsDebug { get; private set; }

    public RunSession(Action<JsonNode> emit) { _emit = emit; }

    // ------------------------------------------------------------ start

    /// <param name="root">Working directory and project root (also put on PYTHONPATH).</param>
    /// <param name="script">The script path, absolute or relative to <paramref name="root"/>.</param>
    /// <param name="breakpoints">Initial breakpoints: absolute or root-relative file to line numbers (debug only).</param>
    public void Start(string root, string script, bool debug, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, List<int>>? breakpoints = null) =>
        StartWithBreakpoints(root, script, debug, args,
            breakpoints?.ToDictionary(kv => kv.Key, kv => new JsonArray(kv.Value.Select(l => (JsonNode)l).ToArray())));

    /// <param name="breakpoints">Per file, an array of line numbers or of objects {line, condition, hitCondition, logMessage}.</param>
    public void StartWithBreakpoints(string root, string script, bool debug, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        string scriptPath = Path.GetFullPath(Path.Combine(root, script));
        if (!File.Exists(scriptPath)) { Error($"script not found: {script}"); return; }
        IsDebug = debug;

        if (debug) Launch(root, true, new[] { "--script", scriptPath, "--" }, args, breakpoints);
        else Launch(root, false, new[] { scriptPath }, args, null);
    }

    /// <summary>
    /// Runs the tests with ids like <c>tests/test_a.py::TestX::test_y</c> (a whole file or class is a prefix of those;
    /// no ids means everything). Results arrive as <c>testStarted</c> / <c>testResult</c> / <c>collectError</c> messages.
    /// </summary>
    /// <summary>Where a coverage run leaves the lines that ran.</summary>
    public static string CoveragePath(string root) => Path.Combine(root, ".pymagic", "coverage.json");

    public void StartTests(string root, IReadOnlyList<string> ids, string framework = "auto", bool coverage = false)
    {
        var helperArgs = new List<string> { "--framework", framework };
        if (coverage) { helperArgs.Add("--coverage"); helperArgs.Add(CoveragePath(root)); }
        helperArgs.Add("--");
        Launch(root, true, helperArgs, ids, null, helper: "pm_tests.py");
    }

    /// <summary>Where a profiler run leaves what it measured.</summary>
    public static string ProfilePath(string root) => Path.Combine(root, ".pymagic", "profile.json");

    /// <summary>Runs the script under cProfile (pm_profile.py): its output streams like a normal run, the timings land in <see cref="ProfilePath"/>.</summary>
    public void StartProfile(string root, string script, IReadOnlyList<string> args)
    {
        string scriptPath = Path.GetFullPath(Path.Combine(root, script));
        if (!File.Exists(scriptPath)) { Error($"script not found: {script}"); return; }
        try { File.Delete(ProfilePath(root)); } catch (IOException) { }
        // a profile made in WSL or Docker names its files as that system does: the panel needs them as paths of this machine
        _afterExit = () => { if (!Target.IsLocal) MapProfilePaths(ProfilePath(root)); };
        Launch(root, false, new[] { ExtractHelper("pm_profile.py"), "--out", ProfilePath(root), "--script", scriptPath, "--" }, args, null);
    }

    Action? _afterExit;

    void MapProfilePaths(string file)
    {
        try
        {
            if (!File.Exists(file) || JsonNode.Parse(File.ReadAllText(file)) is not JsonObject data) return;
            void Fix(JsonNode? n)
            {
                if (n is JsonObject o && o["builtin"]?.GetValue<bool>() != true && o["file"]?.GetValue<string>() is { } f && f.StartsWith('/')) o["file"] = Target.ToHost(f);
            }
            Fix(data);
            if (data["script"]?.GetValue<string>() is { } script) data["script"] = Target.ToHost(script);
            foreach (var fn in data["functions"]?.AsArray() ?? new JsonArray())
            {
                Fix(fn);
                foreach (var c in fn?["callers"]?.AsArray() ?? new JsonArray()) Fix(c);
            }
            File.WriteAllText(file, data.ToJsonString());
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or InvalidOperationException) { /* the profile keeps the paths it has */ }
    }

    /// <summary>Runs <c>python -m module args</c> (pip, ensurepip, venv...) and streams its output like a script run.</summary>
    public void StartModule(string root, string module, IReadOnlyList<string> args) =>
        Launch(root, false, new[] { "-m", module }, args, null);

    /// <param name="channel">Start the helper script and accept its socket (debugger and test runner); otherwise run the script directly.</param>
    void Launch(string root, bool channel, IReadOnlyList<string> leading, IReadOnlyList<string> args,
        IReadOnlyDictionary<string, JsonArray>? breakpoints, string helper = "pm_debug.py")
    {
        _root = root;
        string python = Python ?? Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

        string? token = null;
        var args2 = new List<string> { "-u" };
        var helperDirs = new List<string>();
        if (channel)
        {
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            // a helper in WSL or Docker reaches this program at another address than 127.0.0.1
            var (listen, connect) = Target.HelperAddress();
            _listener = new TcpListener(listen, 0);
            _listener.Start();
            int port = ((IPEndPoint)_listener.LocalEndpoint).Port;
            string helperPath = ExtractHelper(helper);
            helperDirs.Add(Path.GetDirectoryName(helperPath)!);
            args2.Add(helperPath);
            args2.Add("--port"); args2.Add(port.ToString());
            if (connect != "127.0.0.1") { args2.Add("--host"); args2.Add(connect); }
            args2.Add("--token"); args2.Add(token);
            args2.Add("--root"); args2.Add(root);
        }
        foreach (var a in leading) { args2.Add(a); if (a.EndsWith(".py", StringComparison.OrdinalIgnoreCase) && Path.GetDirectoryName(a) is { Length: > 0 } d && d.StartsWith(Path.Combine(Path.GetTempPath(), "pymagic"), StringComparison.OrdinalIgnoreCase)) helperDirs.Add(d); }
        args2.AddRange(args);

        var env = new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUNBUFFERED"] = "1" };
        string? existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        env["PYTHONPATH"] = Target.IsLocal && !string.IsNullOrEmpty(existing) ? root + Path.PathSeparator + existing : root;

        var psi = Target.Start(python, root, args2, env, helperDirs.Distinct().ToList());
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardInputEncoding = new UTF8Encoding(false);
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        try { _process = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) { Error($"cannot start '{psi.FileName}': {e.Message}"); return; }
        if (_process == null) { Error($"cannot start '{psi.FileName}'"); return; }

        _emit(new JsonObject { ["type"] = "started", ["pid"] = _process.Id, ["debug"] = IsDebug });

        var readers = new[]
        {
            Pump(_process.StandardOutput, "stdout"),
            Pump(_process.StandardError, "stderr"),
        };
        Task? channelTask = channel ? Task.Run(() => AcceptDebugger(token!, root, breakpoints)) : null;

        Task.Run(() =>
        {
            _process.WaitForExit();
            Task.WaitAll(readers.Select(r => r).ToArray(), TimeSpan.FromSeconds(2));
            // messages the helper sent just before exiting must reach the UI before the exit does
            try { channelTask?.Wait(TimeSpan.FromSeconds(3)); } catch (AggregateException) { }
            try { _afterExit?.Invoke(); } catch (Exception e) when (e is IOException or InvalidOperationException) { }
            _emit(new JsonObject { ["type"] = "exited", ["code"] = _process.ExitCode });
            Cleanup();
        });
    }

    Task Pump(StreamReader reader, string stream) => Task.Run(() =>
    {
        var buf = new char[4096];
        int n;
        try
        {
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                _emit(new JsonObject { ["type"] = "output", ["stream"] = stream, ["text"] = new string(buf, 0, n) });
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });

    void Error(string message) => _emit(new JsonObject { ["type"] = "error", ["message"] = message });

    internal static string ExtractHelper(string name)
    {
        using var s = typeof(RunSession).Assembly.GetManifestResourceStream(name)
            ?? throw new InvalidOperationException("debug helper missing from the assembly");
        using var ms = new MemoryStream();
        s.CopyTo(ms);
        var bytes = ms.ToArray();
        string dir = Path.Combine(Path.GetTempPath(), "pymagic", Convert.ToHexString(SHA256.HashData(bytes))[..12]);
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        if (!File.Exists(path) || new FileInfo(path).Length != bytes.Length) File.WriteAllBytes(path, bytes);
        return path;
    }

    // ------------------------------------------------------------ the debugger channel

    void AcceptDebugger(string token, string root, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        try
        {
            var accept = _listener!.AcceptTcpClientAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(15))) { Error("the debugger did not connect"); return; }
            var client = accept.Result;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));

            string? hello = reader.ReadLine();
            if (hello == null || JsonNode.Parse(hello)?["token"]?.GetValue<string>() != token)
            {
                client.Dispose();
                Error("debugger handshake failed");
                return;
            }

            lock (_gate)
            {
                _channel = stream;
                if (breakpoints != null)
                    foreach (var (file, lines) in breakpoints)
                        WriteLocked(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.GetFullPath(Path.Combine(root, file)), ["lines"] = JsonNode.Parse(lines.ToJsonString()) });
                foreach (var line in _pending) WriteRawLocked(line);
                _pending.Clear();
                WriteLocked(new JsonObject { ["type"] = "go" });
            }

            string? text;
            while ((text = reader.ReadLine()) != null)
            {
                var msg = JsonNode.Parse(text);
                if (msg == null) continue;
                // the debugger names files as its target does (/mnt/c/proj/a.py in WSL): the editor knows them as paths of this machine
                if (!Target.IsLocal && msg["frames"] is JsonArray frames)
                    foreach (var f in frames) if (f is JsonObject fo && fo["file"]?.GetValue<string>() is { } file) fo["file"] = Target.ToHost(file);
                _emit(msg);
            }
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    }

    void WriteLocked(JsonNode msg)
    {
        if (!Target.IsLocal && msg["type"]?.GetValue<string>() == "setBreakpoints" && msg["file"]?.GetValue<string>() is { } file)
            msg["file"] = Target.ToTarget(file);
        WriteRawLocked(msg.ToJsonString());
    }

    void WriteRawLocked(string json)
    {
        try
        {
            var bytes = Encoding.UTF8.GetBytes(json + "\n");
            _channel!.Write(bytes);
            _channel.Flush();
        }
        catch (IOException) { }
    }

    // ------------------------------------------------------------ messages from the UI

    /// <summary>Handles <c>stdin</c>, <c>stop</c> and (for debug sessions) the debugger commands.</summary>
    public void Handle(JsonNode msg)
    {
        string? type = msg["type"]?.GetValue<string>();
        switch (type)
        {
            case "stdin":
                try
                {
                    _process?.StandardInput.Write(msg["text"]?.GetValue<string>() ?? "");
                    _process?.StandardInput.Flush();
                }
                catch (IOException) { }
                break;
            case "closeStdin":
                try { _process?.StandardInput.Close(); } catch (IOException) { }
                break;
            case "stop":
                Kill();
                break;
            default:
                if (type != null && DebugCommands.Contains(type))
                {
                    lock (_gate)
                    {
                        if (!Target.IsLocal && type == "setBreakpoints" && msg["file"]?.GetValue<string>() is { } bpFile)
                            msg["file"] = Target.ToTarget(Path.GetFullPath(Path.Combine(_root ?? "", bpFile)));
                        string json = msg.ToJsonString();
                        if (_channel == null) _pending.Add(json); else WriteRawLocked(json);
                    }
                }
                break;
        }
    }

    void Kill()
    {
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
    }

    void Cleanup()
    {
        lock (_gate)
        {
            try { _listener?.Stop(); } catch (SocketException) { }
            try { _channel?.Dispose(); } catch (IOException) { }
            _channel = null;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Kill();
        Cleanup();
    }
}
