using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Against a REAL Jupyter server. They run only when PYMAGIC_TEST_JUPYTER names the Python of an environment that has jupyter_server and ipykernel
/// (nothing here installs them); the server is started by LocalJupyter, the way the servers window starts one.
/// </summary>
public class LiveJupyterTests
{
    static string? Python => Environment.GetEnvironmentVariable("PYMAGIC_TEST_JUPYTER") is { Length: > 0 } p && File.Exists(p) ? p : null;

    static JsonNode Wait(BlockingCollection<JsonNode> events, Func<JsonNode, bool> until, int seconds = 60)
    {
        var seen = new List<string>();
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(seconds))) throw new AssertionException("timed out; saw: " + string.Join(" | ", seen.TakeLast(12)));
            seen.Add(e.ToJsonString().Length > 300 ? e.ToJsonString()[..300] : e.ToJsonString());
            if (until(e)) return e;
        }
    }

    static bool Is(JsonNode e, string type) => e["type"]!.GetValue<string>() == type;

    [Fact]
    public void ARealJupyterServerRunsCellsAndAnswersTheQuestionsOfTheVariablesWindow()
    {
        if (Python == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_livejupyter_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var started = LocalJupyter.Start(Python, root, null, 90).GetAwaiter().GetResult();
            Assert.True(started["ok"]!.GetValue<bool>(), started["message"]?.GetValue<string>() ?? "");
            Assert.True((started["kernels"] as JsonArray)!.Count > 0, "the server lists a kernel: " + started.ToJsonString());
            var events = new BlockingCollection<JsonNode>();
            using var k = new JupyterKernel(events.Add);
            k.Start(started["url"]!.GetValue<string>(), started["token"]!.GetValue<string>(), null);
            Wait(events, e => Is(e, "ready"));

            k.Execute("c1", "class Point:\n    def __init__(self):\n        self.x = 3\n    def move(self): return 1\n\n\np = Point()\nd = {'one': [1, 2], 'n': 7}\nprint('hi')\n");
            var vars = Wait(events, e => Is(e, "variables"))["vars"]!.AsArray();
            var names = string.Join(",", vars.Select(v => v!["name"]!.GetValue<string>()));
            Assert.Equal("Point,d,p", names);                 // none of IPython's own names, none of the helper's

            k.Inspect("i1", JsonNode.Parse("[[\"v\",\"d\"]]"));
            var children = Wait(events, e => Is(e, "children") && e["id"]!.GetValue<string>() == "i1");
            Assert.Equal("'one','n'", string.Join(",", children["children"]!.AsArray().Select(c => c!["name"]!.GetValue<string>())));
            k.Complete("t1", "p.mo");
            Assert.Equal("move", string.Join(",", Wait(events, e => Is(e, "completions") && e["id"]!.GetValue<string>() == "t1")["items"]!.AsArray().Select(x => x!.GetValue<string>())));
        }
        finally
        {
            LocalJupyter.Stop();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    /// <summary>
    /// A real Jupyter server and ipykernel on another computer (the Open work's "against a real ipykernel" of item 326): Debug Cell over the kernel's
    /// own channels, as the page asks for a server that is not this computer's. Runs only when PYMAGIC_CT_HOST names the machine, whose Jupyter
    /// server listens on port 8888 with the token PYMAGIC_CT_SECRET.
    /// </summary>
    [Fact]
    public void DebugCellOnARealJupyterServerOnAnotherComputer()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_CT_HOST") is not { Length: > 0 } host || Environment.GetEnvironmentVariable("PYMAGIC_CT_SECRET") is not { Length: > 0 } token) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_ctjdebug_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var kernelEvents = new BlockingCollection<JsonNode>();
            using var k = new JupyterKernel(kernelEvents.Add);
            k.Start($"http://{host}:8888", token, null);
            Wait(kernelEvents, e => Is(e, "ready"));

            var ide = new BlockingCollection<JsonNode>();
            using var session = new RunSession(ide.Add);
            string file = ".pymagic/notebooks/nb.cell1.py";
            session.Listen(root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file] = new JsonArray(2, 6) });
            var listening = Wait(ide, e => Is(e, "listening"), 30);

            k.Execute("seed", "import sys\nseed = 100\nprint(sys.version_info[:2], sys.platform)");
            var seeded = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "seed") seeded.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "seed"; });
            Assert.Contains("linux", string.Concat(seeded.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));   // it is the other computer's kernel
            // what the page sends for a server elsewhere, with the project's folder that Program.cs adds
            var debug = new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening["port"]!.GetValue<int>(), ["token"] = listening["token"]!.GetValue<string>(), ["file"] = file, ["via"] = "kernel", ["root"] = root };
            k.Execute("dbg1", "x = 5\ny = x * 2 + seed\nz = y + 1\ndef twice(v):\n    w = v * 2\n    return w\nprint('z', twice(z))\n", debug);

            Wait(ide, e => Is(e, "connected"), 60);
            JsonNode stopped;
            try { stopped = Wait(ide, e => Is(e, "stopped"), 60); }
            catch (AssertionException e) { throw new AssertionException(e.Message + "; the kernel said: " + string.Join(" | ", kernelEvents.Select(x => x.ToJsonString()))); }
            var top = stopped["frames"]![0]!;
            Assert.Equal(2, top["line"]!.GetValue<int>());
            // named as this computer has it, though the kernel has the project in a folder of its own (/home/...)
            Assert.Equal(Path.GetFullPath(Path.Combine(root, file)), top["file"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "x + seed" });
            Assert.Equal("105", Wait(ide, e => Is(e, "evaluated"), 30)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "next" });
            Assert.Equal(3, Wait(ide, e => Is(e, "stopped"), 30)["frames"]![0]!["line"]!.GetValue<int>());
            // on to the breakpoint inside the function: two frames, the function's over the cell's
            session.Handle(new JsonObject { ["type"] = "continue" });
            var inner = Wait(ide, e => Is(e, "stopped"), 30)["frames"]!.AsArray();
            Assert.Equal(6, inner[0]!["line"]!.GetValue<int>());
            Assert.Equal("twice", inner[0]!["name"]!.GetValue<string>());
            Assert.True(inner.Count >= 2 && inner[1]!["line"]!.GetValue<int>() == 7, "the cell's frame under it: " + inner.ToJsonString());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 2, ["frameId"] = 0, ["expression"] = "w" });
            Assert.Equal("222", Wait(ide, e => Is(e, "evaluated") && e["id"]!.GetValue<int>() == 2, 30)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "continue" });

            var got = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "dbg1") got.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "dbg1"; }, 60);
            Assert.Equal("z 222\n", string.Concat(got.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
            Assert.True(!got.Any(e => Is(e, "display")), "the debugger's messages are not output of the cell");
            Assert.Equal("ok", got.Last()["status"]!.GetValue<string>());
            Wait(ide, e => Is(e, "disconnected"), 30);

            // the namespace is the notebook's own, and the next cell runs without the debugger
            k.Execute("after", "y");
            var after = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "after") after.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "after"; }, 60);
            Assert.Equal("110", after.First(e => Is(e, "result"))["data"]!["text/plain"]!.GetValue<string>());

            // Stop of the Run panel on a second debugged cell: the debugger leaves, the cell runs to its end
            var ide2 = new BlockingCollection<JsonNode>();
            using var session2 = new RunSession(ide2.Add);
            string file2 = ".pymagic/notebooks/nb.cell2.py";
            session2.Listen(root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file2] = new JsonArray(1) });
            var listening2 = Wait(ide2, e => Is(e, "listening"), 30);
            k.Execute("dbg2", "a = 1\nprint('after', a + 1)\n", new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening2["port"]!.GetValue<int>(), ["token"] = listening2["token"]!.GetValue<string>(), ["file"] = file2, ["via"] = "kernel", ["root"] = root });
            Wait(ide2, e => Is(e, "stopped"), 60);
            session2.Handle(new JsonObject { ["type"] = "stop" });
            var got2 = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "dbg2") got2.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "dbg2"; }, 60);
            Assert.Equal("after 2\n", string.Concat(got2.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void ACellRunsUnderTheDebuggerOfTheIdeOnARealJupyterServer()
    {
        if (Python == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_livejdebug_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var started = LocalJupyter.Start(Python, root, null, 90).GetAwaiter().GetResult();
            Assert.True(started["ok"]!.GetValue<bool>(), started["message"]?.GetValue<string>() ?? "");
            var kernelEvents = new BlockingCollection<JsonNode>();
            using var k = new JupyterKernel(kernelEvents.Add);
            k.Start(started["url"]!.GetValue<string>(), started["token"]!.GetValue<string>(), null);
            Wait(kernelEvents, e => Is(e, "ready"));

            var ide = new BlockingCollection<JsonNode>();
            using var session = new RunSession(ide.Add);
            string file = ".pymagic/notebooks/nb.cell1.py";
            session.Listen(root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file] = new JsonArray(2) });
            var listening = Wait(ide, e => Is(e, "listening"), 30);

            k.Execute("seed", "seed = 100");
            Wait(kernelEvents, e => Is(e, "done") && e["id"]!.GetValue<string>() == "seed");
            var debug = new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening["port"]!.GetValue<int>(), ["token"] = listening["token"]!.GetValue<string>(), ["file"] = file };
            k.Execute("dbg1", "x = 5\ny = x * 2 + seed\nz = y + 1\nprint('z', z)\n", debug);

            var stopped = Wait(ide, e => Is(e, "stopped"), 60);
            var top = stopped["frames"]![0]!;
            Assert.Equal(2, top["line"]!.GetValue<int>());
            Assert.True(top["file"]!.GetValue<string>().Replace('\\', '/').EndsWith(file), "the stop is in the file that stands for the cell: " + top["file"]);
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "x + seed" });
            Assert.Equal("105", Wait(ide, e => Is(e, "evaluated"), 30)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "next" });
            Assert.Equal(3, Wait(ide, e => Is(e, "stopped"), 30)["frames"]![0]!["line"]!.GetValue<int>());
            session.Handle(new JsonObject { ["type"] = "continue" });

            var got = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "dbg1") got.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "dbg1"; }, 60);
            Assert.Equal("z 111\n", string.Concat(got.Where(e => Is(e, "stream")).Select(e => e["text"]!.GetValue<string>())));
            Wait(ide, e => Is(e, "disconnected"), 30);

            // the namespace is the notebook's own, and the next cell runs without the debugger
            k.Execute("after", "y");
            var after = new List<JsonNode>();
            Wait(kernelEvents, e => { if (e["id"]?.GetValue<string>() == "after") after.Add(e); return Is(e, "done") && e["id"]!.GetValue<string>() == "after"; }, 60);
            Assert.Equal("110", after.First(e => Is(e, "result"))["data"]!["text/plain"]!.GetValue<string>());
        }
        finally
        {
            LocalJupyter.Stop();
            try { Directory.Delete(root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
