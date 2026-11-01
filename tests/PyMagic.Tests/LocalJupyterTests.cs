using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A Jupyter server started on this computer (LocalJupyter). The Python here has no Jupyter, so the server is a stand-in written in the test:
/// a package called jupyter_server whose <c>python -m jupyter_server</c> answers <c>/api/kernelspecs</c> for the token in JUPYTER_TOKEN. That shows what PyMagic does
/// (the command line, the token's way in, waiting, reusing, stopping, reporting a failure); a real Jupyter server was NOT started by any test.
/// </summary>
public class LocalJupyterTests
{
    static string PythonExe => Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

    static bool HavePython()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(PythonExe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            p!.WaitForExit(15000);
            return p.ExitCode == 0;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    const string StubMain = """
        import sys, os, json
        from http.server import BaseHTTPRequestHandler, HTTPServer
        port = 0
        for a in sys.argv[1:]:
            if a.startswith("--port="):
                port = int(a.split("=", 1)[1])
        log = os.environ.get("STUB_LOG")
        if log:
            with open(log, "w") as f:
                json.dump({"argv": sys.argv[1:], "token_in_env": bool(os.environ.get("JUPYTER_TOKEN")), "cwd": os.getcwd()}, f)
        if os.environ.get("STUB_DIE"):
            print("stub: refusing to start")
            sys.exit(3)
        token = os.environ.get("JUPYTER_TOKEN", "")
        class H(BaseHTTPRequestHandler):
            def log_message(self, *a):
                pass
            def do_GET(self):
                if self.headers.get("Authorization") != "token " + token:
                    self.send_response(403)
                    self.end_headers()
                    return
                if self.path == "/api/kernelspecs":
                    body = json.dumps({"default": "python3", "kernelspecs": {"python3": {}}}).encode()
                    self.send_response(200)
                    self.send_header("Content-Type", "application/json")
                    self.end_headers()
                    self.wfile.write(body)
                    return
                self.send_response(404)
                self.end_headers()
        HTTPServer(("127.0.0.1", port), H).serve_forever()
        """;

    sealed class Stub : IDisposable
    {
        public readonly string Base = Path.Combine(Path.GetTempPath(), "pm_localjupyter_" + Guid.NewGuid().ToString("N"));
        public readonly string Packages, Root, Log;

        public Stub()
        {
            Packages = Path.Combine(Base, "site");
            Root = Path.Combine(Base, "project");
            Log = Path.Combine(Base, "stub.log");
            Directory.CreateDirectory(Path.Combine(Packages, "jupyter_server"));
            Directory.CreateDirectory(Root);
            File.WriteAllText(Path.Combine(Packages, "jupyter_server", "__init__.py"), "");
            File.WriteAllText(Path.Combine(Packages, "jupyter_server", "__main__.py"), StubMain);
        }

        public Dictionary<string, string> Env(bool die = false)
        {
            var env = new Dictionary<string, string> { ["PYTHONPATH"] = Packages, ["STUB_LOG"] = Log };
            if (die) env["STUB_DIE"] = "1";
            return env;
        }

        public void Dispose()
        {
            LocalJupyter.Stop();
            try { Directory.Delete(Base, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void ATokenAndALocalPortAreHandedToTheServerAndItIsReusedUntilItIsStopped()
    {
        if (!HavePython()) return;
        using var stub = new Stub();
        var started = LocalJupyter.Start(PythonExe, stub.Root, stub.Env(), 30).GetAwaiter().GetResult();
        Assert.True(started["ok"]!.GetValue<bool>(), started["message"]?.GetValue<string>() ?? "");
        string url = started["url"]!.GetValue<string>(), token = started["token"]!.GetValue<string>();
        Assert.True(url.StartsWith("http://127.0.0.1:", StringComparison.Ordinal), url);
        Assert.Equal(32, token.Length);
        Assert.True(LocalJupyter.Running);
        // the server really takes that token, and the Jupyter probe (the servers window's Test) is satisfied
        Assert.True(JupyterKernel.Probe(url, token).GetAwaiter().GetResult()["ok"]!.GetValue<bool>());
        Assert.True(!JupyterKernel.Probe(url, "wrong").GetAwaiter().GetResult()["ok"]!.GetValue<bool>());

        // what it was started with: this computer only, the port, the project folder; the token in the environment and not in the arguments
        var log = JsonNode.Parse(File.ReadAllText(stub.Log))!;
        var args = log["argv"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.True(args.Contains("--ip=127.0.0.1") && args.Contains("--no-browser") && args.Contains("--port=" + url[(url.LastIndexOf(':') + 1)..]), string.Join(" ", args));
        Assert.True(args.Any(a => a == "--ServerApp.root_dir=" + stub.Root));
        Assert.True(!string.Join(" ", args).Contains(token), "the token is not on the command line");
        Assert.True(log["token_in_env"]!.GetValue<bool>());

        // asked again, it answers with the one that runs
        var again = LocalJupyter.Start(PythonExe, stub.Root, stub.Env(), 30).GetAwaiter().GetResult();
        Assert.Equal(url, again["url"]!.GetValue<string>());
        Assert.Equal(token, again["token"]!.GetValue<string>());
        Assert.True(again["running"]!.GetValue<bool>());
        Assert.True(LocalJupyter.Status()["running"]!.GetValue<bool>());

        LocalJupyter.Stop();
        Assert.True(!LocalJupyter.Running);
        Assert.True(!LocalJupyter.Status()["running"]!.GetValue<bool>());
        Assert.True(!JupyterKernel.Probe(url, token).GetAwaiter().GetResult()["ok"]!.GetValue<bool>(), "nothing answers after Stop");
    }

    [Fact]
    public void APythonWithoutAJupyterServerSaysSoAndInstallsNothing()
    {
        if (!HavePython()) return;
        using var stub = new Stub();
        // no PYTHONPATH to the stand-in: the interpreter has no jupyter_server
        var none = LocalJupyter.Start(PythonExe, stub.Root, new Dictionary<string, string> { ["PYTHONPATH"] = Path.Combine(stub.Base, "empty") }, 5).GetAwaiter().GetResult();
        Assert.True(!none["ok"]!.GetValue<bool>());
        Assert.Contains("no Jupyter server", none["message"]!.GetValue<string>());
        Assert.Contains("Packages window", none["message"]!.GetValue<string>());
        Assert.True(!LocalJupyter.Running);
        Assert.Contains("no Python", LocalJupyter.Start("", stub.Root).GetAwaiter().GetResult()["message"]!.GetValue<string>());
    }

    [Fact]
    public void AServerThatStopsAtOnceIsReportedWithWhatItSaid()
    {
        if (!HavePython()) return;
        using var stub = new Stub();
        var failed = LocalJupyter.Start(PythonExe, stub.Root, stub.Env(die: true), 30).GetAwaiter().GetResult();
        Assert.True(!failed["ok"]!.GetValue<bool>());
        string message = failed["message"]!.GetValue<string>();
        Assert.Contains("stopped at once", message);
        Assert.Contains("exit code 3", message);
        Assert.Contains("refusing to start", message);
        Assert.True(!LocalJupyter.Running);
    }
}
