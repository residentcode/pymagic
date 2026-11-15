using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// The Python programs a debugged program starts (as Django's and Flask's reloaders start the server again as a child of their own) run under
/// the debugger too: their breakpoints stop, they are stepped and looked at, and a child started later is given the breakpoints as they are
/// then (skipped when no Python is on the PATH).
/// </summary>
public class ChildProcessDebugTests
{
    static readonly bool HavePython = Probe();

    static bool Probe()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo("python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(10000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    // the reloader: the program again as a child (with an option of Python's own before the file), twice, then its own last line
    const string Parent = "import os, subprocess, sys\n" +
                          "for round in (1, 2):\n" +
                          "    subprocess.run([sys.executable, '-Wignore::DeprecationWarning', 'child.py', str(round)], env={**os.environ, 'RUN_MAIN': 'true'}, check=True)\n" +
                          "print('parent done')\n";
    const string Child = "import os, sys\n" +
                         "def serve(n):\n" +
                         "    answer = n * 10\n" +
                         "    return answer\n" +
                         "print('child', serve(int(sys.argv[1])), os.environ.get('RUN_MAIN'), flush=True)\n";

    [Fact]
    public void AChildPythonProgramStopsAtItsBreakpointsAndALaterOneGetsTheirChanges()
    {
        if (!HavePython) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_child_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), Parent);
        File.WriteAllText(Path.Combine(root, "child.py"), Child);
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        JsonNode Next(Func<JsonNode, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !events.TryTake(out var e, left))
                    throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString())));
                seen.Add(e);
                if (match(e)) return e;
            }
        }
        JsonNode Stopped() => Next(e => e["type"]?.GetValue<string>() == "stopped");
        string Output() => string.Concat(seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]?.GetValue<string>()));
        try
        {
            using var session = new RunSession(events.Add);
            session.StartWithBreakpoints(root, "main.py", true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["child.py"] = new JsonArray(3) });

            // the first child stops at line 3, in its own process
            var stop = Stopped();
            Assert.Equal(3, stop["frames"]![0]!["line"]!.GetValue<int>());
            Assert.Equal("serve", stop["frames"]![0]!["name"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "n" });
            Assert.Equal("1", Next(e => e["type"]?.GetValue<string>() == "evaluated")["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "next" });
            Assert.Equal(4, Stopped()["frames"]![0]!["line"]!.GetValue<int>());
            // the breakpoint moves while the first child runs: the second child is told where it is now
            session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = "child.py", ["lines"] = new JsonArray(4) });
            session.Handle(new JsonObject { ["type"] = "continue" });

            stop = Stopped();
            Assert.Equal(4, stop["frames"]![0]!["line"]!.GetValue<int>());
            session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 2, ["frameId"] = 0, ["expression"] = "answer" });
            Assert.Equal("20", Next(e => e["type"]?.GetValue<string>() == "evaluated" && e["id"]?.GetValue<int>() == 2)["result"]!.GetValue<string>());
            session.Handle(new JsonObject { ["type"] = "continue" });

            Assert.Equal(0, Next(e => e["type"]?.GetValue<string>() == "exited")["code"]!.GetValue<int>());
            Assert.Contains("child 10 true", Output());
            Assert.Contains("child 20 true", Output());
            Assert.Contains("parent done", Output());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // elsewhere than Windows: a child of os.fork, and multiprocessing's fork, forkserver and spawn (checked on Linux with 3.12 and 3.14)
    const string Forks = "import os, multiprocessing as mp\n" +
                         "def work(n):\n" +
                         "    answer = n * 10\n" +
                         "    return answer\n" +
                         "if __name__ == '__main__':\n" +
                         "    pid = os.fork()\n" +
                         "    if pid == 0:\n" +
                         "        print('forked', work(1), flush=True)\n" +
                         "        os._exit(0)\n" +
                         "    os.waitpid(pid, 0)\n" +
                         "    for n, kind in ((2, 'fork'), (3, 'forkserver'), (4, 'spawn')):\n" +
                         "        p = mp.get_context(kind).Process(target=work, args=(n,))\n" +
                         "        p.start()\n" +
                         "        p.join()\n" +
                         "        print(kind, p.exitcode, flush=True)\n";

    [Fact]
    public void AForkedChildAndEachStartMethodsProcessStopAtTheirBreakpoints()
    {
        if (!HavePython || OperatingSystem.IsWindows()) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_fork_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), Forks);
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        JsonNode Next(Func<JsonNode, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !events.TryTake(out var e, left))
                    throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString())));
                seen.Add(e);
                if (match(e)) return e;
            }
        }
        try
        {
            using var session = new RunSession(events.Add);
            session.StartWithBreakpoints(root, "main.py", true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["main.py"] = new JsonArray(3) });
            for (int round = 1; round <= 4; round++)
            {
                Assert.Equal(3, Next(e => e["type"]?.GetValue<string>() == "stopped")["frames"]![0]!["line"]!.GetValue<int>());
                session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = round, ["frameId"] = 0, ["expression"] = "n" });
                Assert.Equal(round.ToString(), Next(e => e["type"]?.GetValue<string>() == "evaluated" && e["id"]?.GetValue<int>() == round)["result"]!.GetValue<string>());
                session.Handle(new JsonObject { ["type"] = "continue" });
            }
            Assert.Equal(0, Next(e => e["type"]?.GetValue<string>() == "exited")["code"]!.GetValue<int>());
            string output = string.Concat(seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]?.GetValue<string>()));
            Assert.Contains("forked 10", output);
            Assert.Contains("spawn 0", output);
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }

    // multiprocessing's processes (uvicorn --reload's server, a pool's workers) are started without subprocess
    const string Pool = "import multiprocessing as mp\n" +
                        "from concurrent.futures import ProcessPoolExecutor\n" +
                        "def work(n):\n" +
                        "    answer = n * 10\n" +
                        "    return answer\n" +
                        "if __name__ == '__main__':\n" +
                        "    p = mp.Process(target=work, args=(1,))\n" +
                        "    p.start()\n" +
                        "    p.join()\n" +
                        "    with ProcessPoolExecutor(max_workers=1) as pool:\n" +
                        "        print('pool', pool.submit(work, 2).result(), flush=True)\n" +
                        "    print('parent done', p.exitcode, flush=True)\n";

    [Fact]
    public void AProcessOfMultiprocessingAndAPoolsWorkerStopAtTheirBreakpoints()
    {
        if (!HavePython) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_mp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "main.py"), Pool);
        var events = new BlockingCollection<JsonNode>();
        var seen = new List<JsonNode>();
        JsonNode Next(Func<JsonNode, bool> match)
        {
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !events.TryTake(out var e, left))
                    throw new AssertionException("timed out; saw: " + string.Join(" ", seen.Select(s => s.ToJsonString())));
                seen.Add(e);
                if (match(e)) return e;
            }
        }
        JsonNode Stopped() => Next(e => e["type"]?.GetValue<string>() == "stopped");
        string Output() => string.Concat(seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]?.GetValue<string>()));
        try
        {
            using var session = new RunSession(events.Add);
            session.StartWithBreakpoints(root, "main.py", true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["main.py"] = new JsonArray(4) });

            for (int round = 1; round <= 2; round++)
            {
                var stop = Stopped();
                Assert.Equal(4, stop["frames"]![0]!["line"]!.GetValue<int>());
                Assert.Equal("work", stop["frames"]![0]!["name"]!.GetValue<string>());
                session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = round, ["frameId"] = 0, ["expression"] = "n" });
                Assert.Equal(round.ToString(), Next(e => e["type"]?.GetValue<string>() == "evaluated" && e["id"]?.GetValue<int>() == round)["result"]!.GetValue<string>());
                session.Handle(new JsonObject { ["type"] = "continue" });
            }

            Assert.Equal(0, Next(e => e["type"]?.GetValue<string>() == "exited")["code"]!.GetValue<int>());
            Assert.Contains("pool 20", Output());
            Assert.Contains("parent done 0", Output());
        }
        finally
        {
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
