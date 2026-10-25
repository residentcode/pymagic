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
public sealed class NotebookKernel : IDisposable
{
    readonly Action<JsonNode> _emit;
    readonly object _gate = new();
    Process? _process;
    TcpListener? _listener;
    Stream? _channel;
    readonly List<string> _pending = new();   // messages sent before the kernel connected
    bool _disposed;

    /// <summary>Interpreter to run; falls back to PYMAGIC_PYTHON, then "python".</summary>
    public string? Python { get; set; }
    public bool Running => _process is { HasExited: false };

    public NotebookKernel(Action<JsonNode> emit) { _emit = emit; }

    public void Start(string root)
    {
        string python = Python ?? Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = root,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-u");
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        string? existing = Environment.GetEnvironmentVariable("PYTHONPATH");
        psi.Environment["PYTHONPATH"] = string.IsNullOrEmpty(existing) ? root : root + Path.PathSeparator + existing;
        psi.ArgumentList.Add(RunSession.ExtractHelper("pm_kernel.py"));
        psi.ArgumentList.Add("--port"); psi.ArgumentList.Add(port.ToString());
        psi.ArgumentList.Add("--token"); psi.ArgumentList.Add(token);
        psi.ArgumentList.Add("--root"); psi.ArgumentList.Add(root);

        try { _process = Process.Start(psi); }
        catch (System.ComponentModel.Win32Exception e) { Fail($"cannot start '{python}': {e.Message}"); return; }
        if (_process == null) { Fail($"cannot start '{python}'"); return; }
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
        }
        catch (IOException) { }
        catch (ObjectDisposedException) { }
        catch (System.Text.Json.JsonException) { }
    }

    // ------------------------------------------------------------ commands

    public void Execute(string id, string code) =>
        Send(new JsonObject { ["type"] = "execute", ["id"] = id, ["code"] = code });

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
        Cleanup();
    }
}
