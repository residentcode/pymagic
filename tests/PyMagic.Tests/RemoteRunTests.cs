using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Programs, the debugger, the test runner, the profiler and a notebook kernel running in a real WSL distribution (skipped without one).</summary>
public class RemoteRunTests
{
    static readonly string? Distro = FindDistro();

    static string? FindDistro()
    {
        try
        {
            foreach (var name in WslTarget.Distros())
            {
                var psi = new WslTarget(name).Start("python3", Path.GetTempPath(), new[] { "--version" });
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(60000) && p.ExitCode == 0) return name;
            }
        }
        catch { }
        return null;
    }

    sealed class Harness : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_rr_" + Guid.NewGuid().ToString("N"));
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;

        public Harness()
        {
            Directory.CreateDirectory(Root);
            Session = new RunSession(Events.Add) { Python = "python3", Target = new WslTarget(Distro!) };
        }

        public string Script(string name, string text)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n"));
            return path;
        }

        public JsonNode Next(Func<JsonNode, bool> match, int timeoutMs = 90000)
        {
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);
            while (true)
            {
                var left = deadline - DateTime.UtcNow;
                if (left <= TimeSpan.Zero || !Events.TryTake(out var e, left))
                    throw new AssertionException("timed out; saw: " + string.Join(" ", Seen.Select(s => s.ToJsonString())));
                Seen.Add(e);
                if (match(e)) return e;
            }
        }

        public JsonNode Type(string type) => Next(e => e["type"]?.GetValue<string>() == type);
        public string AllOutput() => string.Concat(Seen.Where(e => e["type"]?.GetValue<string>() == "output").Select(e => e["text"]!.GetValue<string>()));
        public void Dispose() { Session.Dispose(); try { Directory.Delete(Root, true); } catch (IOException) { } }
    }

    [Fact]
    public void AProgramRunsInWslWithItsArgumentsStdinAndProjectOnThePath()
    {
        if (Distro == null) return;
        using var h = new Harness();
        h.Script("pkg/__init__.py", "VALUE = 41\n");
        h.Script("main.py", "import sys\nfrom pkg import VALUE\nname = input('name? ')\nprint(name.upper(), VALUE + 1, sys.argv[1:], sys.platform)\nsys.exit(4)\n");
        h.Session.Start(h.Root, "main.py", false, new[] { "a", "b c" });
        h.Next(e => e["type"]?.GetValue<string>() == "output" && e["text"]!.GetValue<string>().Contains("name?"));
        h.Session.Handle(new JsonObject { ["type"] = "stdin", ["text"] = "bob\n" });
        Assert.Equal(4, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("BOB 42 ['a', 'b c'] linux", h.AllOutput());
    }

    [Fact]
    public void TheDebuggerStopsAtABreakpointAndNamesTheFileAsThisMachineDoes()
    {
        if (Distro == null) return;
        using var h = new Harness();
        string script = h.Script("prog.py", "def add(a, b):\n    total = a + b\n    return total\n\n\nx = add(1, 2)\nprint('done', x)\n");
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 3 } });
        var stopped = h.Type("stopped");
        Assert.Equal("breakpoint", stopped["reason"]!.GetValue<string>());
        var top = stopped["frames"]![0]!;
        Assert.Equal(3, top["line"]!.GetValue<int>());
        Assert.True(string.Equals(Path.GetFullPath(script), Path.GetFullPath(top["file"]!.GetValue<string>()), StringComparison.OrdinalIgnoreCase), top["file"]!.GetValue<string>());
        h.Session.Handle(new JsonObject { ["type"] = "continue" });
        h.Type("exited");
        Assert.Contains("done 3", h.AllOutput());
    }

    [Fact]
    public void ATestRunWorksInWsl()
    {
        if (Distro == null) return;
        using var h = new Harness();
        h.Script("test_math.py", "import unittest\n\n\nclass T(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n\n    def test_bad(self):\n        self.assertEqual(1, 2)\n");
        h.Session.StartTests(h.Root, new List<string>(), "unittest");
        h.Type("exited");
        var results = h.Seen.Where(e => e["type"]?.GetValue<string>() == "testResult").ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results.Any(r => r["outcome"]?.GetValue<string>() == "passed"));
        Assert.True(results.Any(r => r["outcome"]?.GetValue<string>() == "failed"));
        Assert.True(results.All(r => r["id"]!.GetValue<string>().StartsWith("test_math.py")), "ids stay relative to the project");
    }

    [Fact]
    public void AProfileMadeInWslNamesItsFilesAsThisMachineDoes()
    {
        if (Distro == null) return;
        using var h = new Harness();
        string script = h.Script("app.py", "def work():\n    return sum(i * i for i in range(20000))\n\n\nwork()\n");
        h.Session.StartProfile(h.Root, "app.py", Array.Empty<string>());
        h.Type("exited");
        var profile = (JsonObject)JsonNode.Parse(File.ReadAllText(RunSession.ProfilePath(h.Root)))!;
        var work = profile["functions"]!.AsArray().Select(f => (JsonObject)f!).First(f => f["name"]!.GetValue<string>() == "work");
        Assert.True(string.Equals(Path.GetFullPath(script), Path.GetFullPath(work["file"]!.GetValue<string>()), StringComparison.OrdinalIgnoreCase), work["file"]!.GetValue<string>());
        Assert.True(string.Equals(Path.GetFullPath(script), Path.GetFullPath(profile["script"]!.GetValue<string>()), StringComparison.OrdinalIgnoreCase), profile["script"]!.GetValue<string>());
    }

    [Fact]
    public void ANotebookKernelRunsInWsl()
    {
        if (Distro == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_rrk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var events = new BlockingCollection<JsonNode>();
        using var kernel = new NotebookKernel(events.Add) { Python = "python3", Target = new WslTarget(Distro) };
        kernel.Start(root);
        JsonNode Wait(Func<JsonNode, bool> until)
        {
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(90))) throw new AssertionException("timed out");
                if (e["type"]!.GetValue<string>() == "kernelError") throw new AssertionException(e["message"]!.GetValue<string>());
                if (until(e)) return e;
            }
        }
        Wait(e => e["type"]!.GetValue<string>() == "ready");
        kernel.Execute("1", "import sys, os\nx = 6 * 7\nprint('cwd', os.getcwd())\nsys.platform");
        var result = Wait(e => e["type"]!.GetValue<string>() == "result");
        Assert.Equal("'linux'", result["data"]!["text/plain"]!.GetValue<string>());
        Wait(e => e["type"]!.GetValue<string>() == "done");
        kernel.Execute("2", "x");
        Assert.Equal("42", Wait(e => e["type"]!.GetValue<string>() == "result")["data"]!["text/plain"]!.GetValue<string>());
    }

    [Fact]
    public void AVirtualEnvironmentCanBeMadeInWslAndUsed()
    {
        if (Distro == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_rrv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new InterpreterManager(root);
        string spec;
        try { spec = manager.CreateVenv(new WslTarget(Distro).Spec("python3"), ".venv", withPip: false); }
        catch (ServiceException e) when (e.Message.Contains("ensurepip") || e.Message.Contains("venv failed")) { return; }   // python3-venv is not installed in this distribution
        Assert.True(ExecutionTarget.IsSpec(spec), spec);
        var r = InterpreterInspector.Inspect(spec);
        Assert.True(r.Interpreter != null, r.Error);
        Assert.True(r.Interpreter!.IsVenv);
        Assert.Equal("wsl", r.Interpreter.Kind);
    }
}
