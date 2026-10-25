using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A script run under the profiler (pm_profile.py): output as usual, timings in .pymagic/profile.json.</summary>
public class ProfilerTests
{
    static string PythonExe => Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

    static readonly bool HavePython = CanRun();

    static bool CanRun()
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(PythonExe, "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(20000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    static string Project(string script)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_prof_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "app.py"), script.Replace("\r\n", "\n"));
        return root;
    }

    static (JsonObject? Profile, string Output, int Code) Profile(string root, params string[] args)
    {
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add) { Python = PythonExe };
        session.StartProfile(root, "app.py", args);
        var output = new System.Text.StringBuilder();
        int code = -1;
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(60))) throw new AssertionException("timed out");
            string type = e["type"]!.GetValue<string>();
            if (type == "output") output.Append(e["text"]!.GetValue<string>());
            if (type == "exited") { code = e["code"]!.GetValue<int>(); break; }
            if (type == "error") throw new AssertionException(e["message"]!.GetValue<string>());
        }
        string path = RunSession.ProfilePath(root);
        return (File.Exists(path) ? (JsonObject)JsonNode.Parse(File.ReadAllText(path))! : null, output.ToString(), code);
    }

    static JsonObject? Function(JsonObject profile, string name) =>
        profile["functions"]!.AsArray().Select(f => (JsonObject)f!).FirstOrDefault(f => f["name"]!.GetValue<string>() == name && !f["builtin"]!.GetValue<bool>());

    const string Busy =
        "import sys\n\n\n" +
        "def inner(n):\n    return sum(i * i for i in range(n))\n\n\n" +
        "def outer():\n    total = 0\n    for _ in range(30):\n        total += inner(2000)\n    return total\n\n\n" +
        "print('result', outer(), sys.argv[1:])\n";

    [Fact]
    public void TheFunctionsOfTheScriptAreMeasured()
    {
        if (!HavePython) return;
        var (profile, output, code) = Profile(Project(Busy), "x", "y");
        Assert.Equal(0, code);
        Assert.Contains("result", output);
        Assert.Contains("['x', 'y']", output);          // the arguments reach the script
        Assert.True(profile != null);
        var outer = Function(profile!, "outer");
        var inner = Function(profile!, "inner");
        Assert.True(outer != null);
        Assert.True(inner != null);
        Assert.Equal(1, outer!["calls"]!.GetValue<int>());
        Assert.Equal(30, inner!["calls"]!.GetValue<int>());
        Assert.True(outer["cumulative"]!.GetValue<double>() >= inner["cumulative"]!.GetValue<double>());
        Assert.True(inner["own"]!.GetValue<double>() <= inner["cumulative"]!.GetValue<double>());
        Assert.True(inner["file"]!.GetValue<string>().EndsWith("app.py"));
        Assert.Equal(4, inner["line"]!.GetValue<int>());
        Assert.True(profile!["total"]!.GetValue<double>() > 0);
    }

    [Fact]
    public void CallersAreRecorded()
    {
        if (!HavePython) return;
        var (profile, _, _) = Profile(Project(Busy));
        var callers = Function(profile!, "inner")!["callers"]!.AsArray().Select(c => c!["name"]!.GetValue<string>()).ToList();
        Assert.True(callers.Contains("outer"), string.Join(",", callers));
    }

    [Fact]
    public void ARaisedErrorStillGivesAProfileAndExitCodeOne()
    {
        if (!HavePython) return;
        var (profile, output, code) = Profile(Project("def boom():\n    raise ValueError('bad')\n\n\nboom()\n"));
        Assert.Equal(1, code);
        Assert.Contains("ValueError", output);
        Assert.True(profile != null);
        Assert.True(Function(profile!, "boom") != null);
    }

    [Fact]
    public void SysExitKeepsItsCode()
    {
        if (!HavePython) return;
        var (profile, _, code) = Profile(Project("import sys\n\n\ndef f():\n    sys.exit(3)\n\n\nf()\n"));
        Assert.Equal(3, code);
        Assert.True(Function(profile!, "f") != null);
    }

    [Fact]
    public void AMissingScriptIsReported()
    {
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        session.StartProfile(Path.GetTempPath(), "no_such_script_" + Guid.NewGuid().ToString("N") + ".py", new List<string>());
        Assert.True(events.TryTake(out var e, TimeSpan.FromSeconds(5)));
        Assert.Equal("error", e!["type"]!.GetValue<string>());
    }
}
