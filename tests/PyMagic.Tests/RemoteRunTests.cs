using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// Programs, the debugger, the test runner, the profiler and a notebook kernel running in a real WSL distribution and in a real Docker
/// image. Each test runs for every target this computer has (and passes without any): WSL needs a distribution with python3, Docker needs
/// the python:3.12-slim image to be there already.
/// </summary>
public class RemoteRunTests
{
    const string DockerImage = "python:3.12-slim";

    static readonly List<(string Name, Func<ExecutionTarget> Make, string Python)> Targets = FindTargets();

    static List<(string, Func<ExecutionTarget>, string)> FindTargets()
    {
        var found = new List<(string, Func<ExecutionTarget>, string)>();
        try
        {
            foreach (var name in WslTarget.Distros())
            {
                var psi = new WslTarget(name).Start("python3", Path.GetTempPath(), new[] { "--version" });
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(60000) && p.ExitCode == 0) { found.Add(("wsl " + name, () => new WslTarget(name), "python3")); break; }
            }
        }
        catch { }
        try { if (DockerTarget.Available() && DockerTarget.Images().Contains(DockerImage)) found.Add(("docker " + DockerImage, () => new DockerTarget(DockerImage), "python")); }
        catch { }
        return found;
    }

    static void Each(Action<ExecutionTarget, string, string> test)
    {
        foreach (var (name, make, python) in Targets)
        {
            try { test(make(), python, name); }
            catch (Exception e) when (e is not AssertionException) { throw new AssertionException($"[{name}] {e.GetType().Name}: {e.Message}"); }
            catch (AssertionException e) { throw new AssertionException($"[{name}] {e.Message}"); }
        }
    }

    sealed class Harness : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_rr_" + Guid.NewGuid().ToString("N"));
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;

        public Harness(ExecutionTarget target, string python)
        {
            Directory.CreateDirectory(Root);
            if (target is DockerTarget d) d.Project = Root;
            Session = new RunSession(Events.Add) { Python = python, Target = target };
        }

        public string Script(string name, string text)
        {
            string path = Path.Combine(Root, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n"));
            return path;
        }

        public JsonNode Next(Func<JsonNode, bool> match, int timeoutMs = 120000)
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

    static bool Same(string a, string b) => string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    [Fact]
    public void AProgramRunsThereWithItsArgumentsStdinAndProjectOnThePath() => Each((target, python, _) =>
    {
        using var h = new Harness(target, python);
        h.Script("pkg/__init__.py", "VALUE = 41\n");
        h.Script("main.py", "import sys\nfrom pkg import VALUE\nname = input('name? ')\nprint(name.upper(), VALUE + 1, sys.argv[1:], sys.platform)\nsys.exit(4)\n");
        h.Session.Start(h.Root, "main.py", false, new[] { "a", "b c" });
        h.Next(e => e["type"]?.GetValue<string>() == "output" && e["text"]!.GetValue<string>().Contains("name?"));
        h.Session.Handle(new JsonObject { ["type"] = "stdin", ["text"] = "bob\n" });
        Assert.Equal(4, h.Type("exited")["code"]!.GetValue<int>());
        Assert.True(h.AllOutput().Contains("BOB 42 ['a', 'b c'] linux"), h.AllOutput());
    });

    [Fact]
    public void TheDebuggerStopsAtABreakpointAndNamesTheFileAsThisMachineDoes() => Each((target, python, _) =>
    {
        using var h = new Harness(target, python);
        string script = h.Script("prog.py", "def add(a, b):\n    total = a + b\n    return total\n\n\nx = add(1, 2)\nprint('done', x)\n");
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 3 } });
        var stopped = h.Type("stopped");
        Assert.Equal("breakpoint", stopped["reason"]!.GetValue<string>());
        var top = stopped["frames"]![0]!;
        Assert.Equal(3, top["line"]!.GetValue<int>());
        Assert.True(Same(script, top["file"]!.GetValue<string>()), top["file"]!.GetValue<string>());
        h.Session.Handle(new JsonObject { ["type"] = "continue" });
        h.Type("exited");
        Assert.True(h.AllOutput().Contains("done 3"), h.AllOutput());
    });

    [Fact]
    public void ATestRunWorksThere() => Each((target, python, _) =>
    {
        using var h = new Harness(target, python);
        h.Script("test_math.py", "import unittest\n\n\nclass T(unittest.TestCase):\n    def test_ok(self):\n        self.assertEqual(1 + 1, 2)\n\n    def test_bad(self):\n        self.assertEqual(1, 2)\n");
        h.Session.StartTests(h.Root, new List<string>(), "unittest");
        h.Type("exited");
        var results = h.Seen.Where(e => e["type"]?.GetValue<string>() == "testResult").ToList();
        Assert.Equal(2, results.Count);
        Assert.True(results.Any(r => r["outcome"]?.GetValue<string>() == "passed"));
        Assert.True(results.Any(r => r["outcome"]?.GetValue<string>() == "failed"));
        Assert.True(results.All(r => r["id"]!.GetValue<string>().StartsWith("test_math.py")), "ids stay relative to the project");
    });

    [Fact]
    public void AProfileMadeThereNamesItsFilesAsThisMachineDoes() => Each((target, python, _) =>
    {
        using var h = new Harness(target, python);
        string script = h.Script("app.py", "def work():\n    return sum(i * i for i in range(20000))\n\n\nwork()\n");
        h.Session.StartProfile(h.Root, "app.py", Array.Empty<string>());
        h.Type("exited");
        var profile = (JsonObject)JsonNode.Parse(File.ReadAllText(RunSession.ProfilePath(h.Root)))!;
        var work = profile["functions"]!.AsArray().Select(f => (JsonObject)f!).First(f => f["name"]!.GetValue<string>() == "work");
        Assert.True(Same(script, work["file"]!.GetValue<string>()), work["file"]!.GetValue<string>());
        Assert.True(Same(script, profile["script"]!.GetValue<string>()), profile["script"]!.GetValue<string>());
    });

    [Fact]
    public void ANotebookKernelRunsThere() => Each((target, python, _) =>
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_rrk_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        if (target is DockerTarget d) d.Project = root;
        var events = new BlockingCollection<JsonNode>();
        using var kernel = new NotebookKernel(events.Add) { Python = python, Target = target };
        kernel.Start(root);
        JsonNode Wait(Func<JsonNode, bool> until)
        {
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(120))) throw new AssertionException("timed out");
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
    });

    [Fact]
    public void AVirtualEnvironmentCanBeMadeThereAndUsed() => Each((target, python, _) =>
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_rrv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var manager = new InterpreterManager(root);
        string spec;
        try { spec = manager.CreateVenv(target.Spec(python), ".venv", withPip: false); }
        catch (ServiceException e) when (e.Message.Contains("ensurepip") || e.Message.Contains("venv failed")) { return; }   // python3-venv is not installed in this system
        Assert.True(ExecutionTarget.IsSpec(spec), spec);
        var r = InterpreterInspector.Inspect(spec);
        Assert.True(r.Interpreter != null, r.Error);
        Assert.True(r.Interpreter!.IsVenv);
        Assert.Equal(target.Kind, r.Interpreter.Kind);
    });

    [Fact]
    public void AnInterpreterIsInspectedAndItsLibrariesCanBeRead() => Each((target, python, _) =>
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_rri_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        DockerTarget.DefaultProject = root;
        var r = InterpreterInspector.Inspect(target.Spec(python));
        Assert.True(r.Interpreter != null, r.Error);
        var i = r.Interpreter!;
        Assert.Equal("linux", i.Platform);
        Assert.Equal(target.Kind, i.Kind);
        Assert.True(i.Stdlib != null && Directory.Exists(i.Stdlib), "the standard library cannot be read from here: " + i.Stdlib);
        Assert.True(File.Exists(Path.Combine(i.Stdlib!, "os.py")), "os.py is not in " + i.Stdlib);
        Assert.True(i.StdlibModules.Contains("os"));
    });
}
