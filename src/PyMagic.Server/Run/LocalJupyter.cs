using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// A Jupyter server started by PyMagic on this computer, with the Python of the project: <c>python -m jupyter_server</c> on a free port of this
/// computer only (127.0.0.1), with a token made here and handed over in the environment (<c>JUPYTER_TOKEN</c>, so it is not in the process list),
/// and the project folder as its root. Nothing is installed: when the Python has no <c>jupyter_server</c>, the answer says so. Only one is kept;
/// it stops with PyMagic, or when asked.
/// </summary>
public static class LocalJupyter
{
    static readonly object Gate = new();
    static Process? _process;
    static string _url = "";
    static string _token = "";
    static string _python = "";

    public static bool Running { get { lock (Gate) return _process is { HasExited: false }; } }

    static int FreePort()
    {
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return port;
    }

    static (int Code, string Out) Quick(string python, string arguments, IReadOnlyDictionary<string, string>? env)
    {
        var psi = new ProcessStartInfo(python, arguments) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(30000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, "did not answer"); }
        return (p.ExitCode, (output.Result + error.Result).Trim());
    }

    /// <summary>Starts the server (or answers with the one that runs): { ok, url, token, message, kernels }. <paramref name="env"/> adds to the environment of the server (tests).</summary>
    public static async Task<JsonObject> Start(string python, string root, IReadOnlyDictionary<string, string>? env = null, int waitSeconds = 45)
    {
        lock (Gate)
        {
            if (_process is { HasExited: false } && _python == python)
                return new JsonObject { ["ok"] = true, ["url"] = _url, ["token"] = _token, ["message"] = "The server that PyMagic started is still running", ["running"] = true };
        }
        Stop();
        if (python.Length == 0) return Fail("There is no Python interpreter for this project: choose one first");
        (int code, string text) check;
        try { check = Quick(python, "-c \"import jupyter_server\"", env); }
        catch (System.ComponentModel.Win32Exception e) { return Fail($"Python could not be started: {e.Message}"); }
        if (check.code != 0)
            return Fail("This Python has no Jupyter server. Install jupyter_server and ipykernel into the project's environment (the Packages window does it with pip), then try again.");

        int port = FreePort();
        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();
        var psi = new ProcessStartInfo(python)
        {
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
        };
        foreach (var a in new[] { "-m", "jupyter_server", "--no-browser", "--ip=127.0.0.1", $"--port={port}", "--ServerApp.port_retries=0", $"--ServerApp.root_dir={root}" }) psi.ArgumentList.Add(a);
        psi.Environment["JUPYTER_TOKEN"] = token;
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        var log = new StringBuilder();
        Process process;
        try { process = Process.Start(psi)!; }
        catch (System.ComponentModel.Win32Exception e) { return Fail($"The server could not be started: {e.Message}"); }
        void Collect(string? line) { if (line == null) return; lock (log) { log.AppendLine(line); if (log.Length > 8000) log.Remove(0, log.Length - 6000); } }
        process.OutputDataReceived += (_, e) => Collect(e.Data);
        process.ErrorDataReceived += (_, e) => Collect(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        string url = $"http://127.0.0.1:{port}";
        var end = DateTime.UtcNow.AddSeconds(waitSeconds);
        while (DateTime.UtcNow < end)
        {
            if (process.HasExited)
            {
                string tail;
                lock (log) tail = string.Join(" ", log.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries).TakeLast(4).Select(l => l.Trim()));
                return Fail($"The server stopped at once (exit code {process.ExitCode}): {tail}");
            }
            var probe = await JupyterKernel.Probe(url, token);
            if (probe["ok"]!.GetValue<bool>())
            {
                lock (Gate) { _process = process; _url = url; _token = token; _python = python; }
                bool none = (probe["kernels"] as JsonArray)?.Count == 0;
                return new JsonObject
                {
                    ["ok"] = true, ["url"] = url, ["token"] = token, ["kernels"] = probe["kernels"]?.DeepClone(), ["running"] = false,
                    ["message"] = none ? "The server runs, but it lists no kernels: install ipykernel into the same environment" : $"A Jupyter server runs on this computer ({url}). {probe["message"]!.GetValue<string>()}",
                };
            }
            await Task.Delay(300);
        }
        try { process.Kill(true); } catch (InvalidOperationException) { }
        return Fail($"The server did not answer within {waitSeconds} seconds");
    }

    static JsonObject Fail(string message) => new() { ["ok"] = false, ["message"] = message };

    /// <summary>What runs now: { running, url } (the token is not given again; the page keeps the one it was given).</summary>
    public static JsonObject Status()
    {
        lock (Gate) return new JsonObject { ["running"] = _process is { HasExited: false }, ["url"] = _process is { HasExited: false } ? _url : "" };
    }

    public static void Stop()
    {
        Process? process;
        lock (Gate) { process = _process; _process = null; _url = ""; _token = ""; }
        if (process == null) return;
        try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { }
        process.Dispose();
    }
}
