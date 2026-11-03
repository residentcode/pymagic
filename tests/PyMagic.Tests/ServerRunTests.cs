using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The three server run configurations (Django, FastAPI with uvicorn, Flask) and uv, started as the page starts them (runconfig-model.js
/// startOf: the same files, modules and arguments) by an interpreter that has the packages: PYMAGIC_SERVERS_PYTHON names it (an environment
/// with django, fastapi, uvicorn and flask); without it these return at once. Each server must say where it listens and answer a request there.
/// </summary>
public class ServerRunTests
{
    static string? Python => Environment.GetEnvironmentVariable("PYMAGIC_SERVERS_PYTHON") is { Length: > 0 } p && File.Exists(p) ? p : null;

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        int port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    static bool Listening(int port)
    {
        try { using var c = new TcpClient(); c.Connect(IPAddress.Loopback, port); return true; }
        catch (SocketException) { return false; }
    }

    /// <summary>Starts the run, waits for the server to say it listens on the port, and gives what a GET of the path answers.</summary>
    static string Serve(Action<RunSession, string> start, Dictionary<string, string> files, int port, string path, string? uv = null, Dictionary<string, string>? env = null)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_srv_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, rel))!);
            File.WriteAllText(Path.Combine(root, rel), text);
        }
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<string>();
        using var session = new RunSession(events.Add) { Python = Python, UvProgram = uv, ExtraEnvironment = env };
        try
        {
            start(session, root);
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(1)))
                {
                    if (DateTime.UtcNow > deadline) throw new AssertionException("the server did not say it listens; saw: " + string.Join("", seen));
                    continue;
                }
                string type = e["type"]?.GetValue<string>() ?? "";
                if (type is "exited" or "error") throw new AssertionException("the server ended: " + e.ToJsonString() + " after: " + string.Join("", seen));
                if (type == "output") seen.Add(e["text"]!.GetValue<string>());
                if (string.Join("", seen).Contains($"127.0.0.1:{port}")) break;
            }
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            // the server may print its address a while before it accepts (uvicorn's reloader starts its worker after it)
            string body;
            for (int i = 0; ; i++)
            {
                try { body = http.GetStringAsync($"http://127.0.0.1:{port}{path}").GetAwaiter().GetResult(); break; }
                catch (HttpRequestException) when (i < 80) { Thread.Sleep(250); }
            }
            // stopped, nothing serves any more: not the reloader's child, nor the interpreter a virtual environment's python.exe started
            session.Dispose();
            var closed = DateTime.UtcNow.AddSeconds(10);
            while (Listening(port))
            {
                if (DateTime.UtcNow > closed) throw new AssertionException($"something still serves on port {port} after the run was stopped");
                Thread.Sleep(200);
            }
            return body;
        }
        finally
        {
            session.Dispose();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TheDjangoServerConfigurationServes()
    {
        if (Python == null) return;
        int port = FreePort();
        var files = new Dictionary<string, string>
        {
            ["manage.py"] = "import os\nimport sys\n\nif __name__ == '__main__':\n    os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'shop.settings')\n    from django.core.management import execute_from_command_line\n    execute_from_command_line(sys.argv)\n",
            ["shop/__init__.py"] = "",
            ["shop/settings.py"] = "SECRET_KEY = 'x'\nDEBUG = True\nALLOWED_HOSTS = ['*']\nROOT_URLCONF = 'shop.urls'\nINSTALLED_APPS = []\nDATABASES = {}\n",
            ["shop/urls.py"] = "from django.http import HttpResponse\nfrom django.urls import path\n\nurlpatterns = [path('', lambda request: HttpResponse('django here'))]\n",
        };
        // as startOf gives a Django server: manage.py runserver 127.0.0.1:<port> (the reloader left on, as the configuration has it)
        Assert.Equal("django here", Serve((s, root) => s.Start(root, "manage.py", false, new[] { "runserver", $"127.0.0.1:{port}" }), files, port, "/"));
    }

    [Fact]
    public void TheFastApiConfigurationServesThroughUvicorn()
    {
        if (Python == null) return;
        int port = FreePort();
        var files = new Dictionary<string, string> { ["main.py"] = "from fastapi import FastAPI\n\napp = FastAPI()\n\n\n@app.get('/')\ndef index():\n    return {'hello': 'fastapi'}\n" };
        // python -m uvicorn main:app --host 127.0.0.1 --port <port> --reload
        string body = Serve((s, root) => s.StartModule(root, "uvicorn", false, new[] { "main:app", "--host", "127.0.0.1", "--port", port.ToString(), "--reload" }, null), files, port, "/");
        Assert.Equal("{\"hello\":\"fastapi\"}", body);
    }

    [Fact]
    public void TheFlaskConfigurationServes()
    {
        if (Python == null) return;
        int port = FreePort();
        var files = new Dictionary<string, string> { ["app.py"] = "from flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route('/')\ndef index():\n    return 'flask here'\n" };
        // python -m flask --app app run --host 127.0.0.1 --port <port> --debug
        Assert.Equal("flask here", Serve((s, root) => s.StartModule(root, "flask", false, new[] { "--app", "app", "run", "--host", "127.0.0.1", "--port", port.ToString(), "--debug" }, null), files, port, "/"));
    }

    [Fact]
    public void AConfigurationRunThroughUvUsesTheProjectsEnvironment()
    {
        // uv run makes the project's own environment from pyproject.toml (its packages from uv's cache, which the ones above are in)
        if (Python == null) return;
        string? uv = Environment.GetEnvironmentVariable("PYMAGIC_UV") is { Length: > 0 } u ? u : "uv";
        int port = FreePort();
        var files = new Dictionary<string, string>
        {
            ["pyproject.toml"] = "[project]\nname = \"shop\"\nversion = \"0.1.0\"\nrequires-python = \">=3.10\"\ndependencies = [\"flask\"]\n",
            ["app.py"] = "import sys\nfrom flask import Flask\n\napp = Flask(__name__)\n\n\n@app.route('/')\ndef index():\n    return sys.prefix\n",
        };
        string prefix = Serve((s, root) => s.StartModule(root, "flask", false, new[] { "--app", "app", "run", "--host", "127.0.0.1", "--port", port.ToString() }, null),
            files, port, "/", uv);
        // the interpreter is the one uv made in the project, not the one named
        Assert.True(prefix.Replace('\\', '/').EndsWith("/.venv"), prefix);
    }
}
