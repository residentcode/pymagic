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
