using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// One Python process (pm_kernel.py) that runs the code cells of a notebook in a shared namespace. Everything the
/// kernel says (<c>ready</c>, <c>stream</c>, <c>result</c>, <c>display</c>, <c>error</c>, <c>done</c>) is passed to
/// <c>emit</c> as it arrives; the host adds <c>kernelError</c> (it could not start) and <c>kernelExited</c>.
/// </summary>
public sealed class NotebookKernel : INotebookKernel
{
    readonly Action<JsonNode> _emit;
    readonly object _gate = new();
    Process? _process;
    TcpListener? _listener;
    Stream? _channel;
    readonly List<string> _pending = new();   // messages sent before the kernel connected
    bool _disposed;
    string? _runId;

    /// <summary>Interpreter to run; falls back to PYMAGIC_PYTHON, then "python".</summary>
    public string? Python { get; set; }

    /// <summary>Where the interpreter runs (this computer, WSL or Docker).</summary>
    public ExecutionTarget Target { get; set; } = ExecutionTarget.Local;

    public bool Running => _process is { HasExited: false };

    public NotebookKernel(Action<JsonNode> emit) { _emit = emit; }

    public void Start(string root)
    {
        string python = Python ?? Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        var (listen, connect) = Target.HelperAddress();
        _listener = new TcpListener(listen, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        string helper = RunSession.ExtractHelper("pm_kernel.py");
        var args = new List<string> { "-u", helper, "--port", port.ToString() };
        if (connect != "127.0.0.1") { args.Add("--host"); args.Add(connect); }
        args.Add("--token"); args.Add(token);
        args.Add("--root"); args.Add(root);
        var env = new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8", ["PYTHONUNBUFFERED"] = "1" };
        string? existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        env["PYTHONPATH"] = Target.IsLocal && !string.IsNullOrEmpty(existing) ? root + Path.PathSeparator + existing : root;

        try { Target.Prepare(root, new[] { Path.GetDirectoryName(helper)! }, null); }
        catch (ServiceException e) { _listener.Stop(); Fail(e.Message); return; }

        var psi = Target.Start(python, root, args, env, new[] { Path.GetDirectoryName(helper)! });
        _runId = psi.Environment.TryGetValue("PYMAGIC_RUN_ID", out var runId) ? runId : null;
        psi.RedirectStandardInput = true;
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        psi.UseShellExecute = false;
        psi.CreateNoWindow = true;
        psi.StandardOutputEncoding = Encoding.UTF8;
        psi.StandardErrorEncoding = Encoding.UTF8;

        try { _process = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) { Fail($"cannot start '{psi.FileName}': {e.Message}"); return; }
        if (_process == null) { Fail($"cannot start '{psi.FileName}'"); return; }
        _process.StandardInput.Close();

        var pumps = new[] { Pump(_process.StandardOutput), Pump(_process.StandardError) };
        var channel = Task.Run(() => Accept(token));
        Task.Run(() =>
        {
            _process.WaitForExit();
            Task.WaitAll(pumps, TimeSpan.FromSeconds(2));
            try { channel.Wait(TimeSpan.FromSeconds(2)); } catch (AggregateException) { }
            if (!_disposed) _emit(new JsonObject { ["type"] = "kernelExited", ["code"] = _process.ExitCode });
            Cleanup();
        });
    }

    void Fail(string message) => _emit(new JsonObject { ["type"] = "kernelError", ["message"] = message });

    /// <summary>What the kernel process prints outside the cells (a crash, a native library's noise) is passed on as a log line.</summary>
    Task Pump(StreamReader reader) => Task.Run(() =>
    {
        var buf = new char[4096];
        int n;
        try
        {
            while ((n = reader.Read(buf, 0, buf.Length)) > 0)
                _emit(new JsonObject { ["type"] = "kernelLog", ["text"] = new string(buf, 0, n) });
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
    });

    void Accept(string token)
    {
        try
        {
            var accept = _listener!.AcceptTcpClientAsync();
            if (!accept.Wait(TimeSpan.FromSeconds(30))) { Fail("the kernel did not connect"); return; }
            var client = accept.Result;
            var stream = client.GetStream();
            var reader = new StreamReader(stream, new UTF8Encoding(false));
            string? hello = reader.ReadLine();
            if (hello == null || JsonNode.Parse(hello)?["token"]?.GetValue<string>() != token)
            {
                client.Dispose();
                Fail("the kernel's handshake failed");
                return;
            }
            lock (_gate)
            {
                _channel = stream;
                foreach (var line in _pending) WriteRawLocked(line);
                _pending.Clear();
            }
            string? text;
            while ((text = reader.ReadLine()) != null)
            {
                var msg = JsonNode.Parse(text);
                if (msg != null) _emit(msg);
            }
            lock (_gate) { try { _channel?.Dispose(); } catch (IOException) { } _channel = null; }
            client.Dispose();
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (System.Text.Json.JsonException) { }
    }

    // ------------------------------------------------------------ commands

    /// <param name="debug">Debug Cell: where the debugger of the IDE listens ({host, port, token, file}); the cell runs under it.</param>
    public void Execute(string id, string code, JsonNode? debug = null)
    {
        var msg = new JsonObject { ["type"] = "execute", ["id"] = id, ["code"] = code };
        if (debug is JsonObject d)
        {
            // pm_debug.py is a file of its own beside the other helpers: the kernel imports it from where the host put it
            var copy = (JsonObject)d.DeepClone();
            copy["helper"] = Path.GetDirectoryName(RunSession.ExtractHelper("pm_debug.py"));
            msg["debug"] = copy;
        }
        Send(msg);
    }

    /// <summary>Asks for the members of a value of the Variables window (<c>inspect</c>); the kernel answers with <c>children</c>.</summary>
    public void Inspect(string id, JsonNode? path) =>
        Send(new JsonObject { ["type"] = "inspect", ["id"] = id, ["path"] = path?.DeepClone() });

    /// <summary>Raises KeyboardInterrupt in the running cell (a cell blocked inside native code stops when it returns to Python).</summary>
    public void Interrupt() => Send(new JsonObject { ["type"] = "interrupt" });

    /// <summary>Closes the connection without killing the process: the kernel must notice and stop by itself (used when the host is gone, and by tests).</summary>
    public void DropConnection()
    {
        lock (_gate)
        {
            try { _channel?.Dispose(); } catch (IOException) { }
            _channel = null;
        }
    }

    void Send(JsonNode msg)
    {
        lock (_gate)
        {
            string json = msg.ToJsonString();
            if (_channel == null) _pending.Add(json); else WriteRawLocked(json);
        }
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
        try { if (_process is { HasExited: false }) _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception) { }
        if (_runId is { } id) { var target = Target; Task.Run(() => target.Stop(id)); }
        Cleanup();
    }
}
