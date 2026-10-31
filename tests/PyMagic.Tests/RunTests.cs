using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Runs real Python processes; skipped (passing) when no interpreter is on the PATH.</summary>
public class RunTests
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

    sealed class Harness : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_run_" + Guid.NewGuid().ToString("N"));
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly List<JsonNode> Seen = new();
        public readonly RunSession Session;
        int _id;

        public Harness()
        {
            Directory.CreateDirectory(Root);
            Session = new RunSession(Events.Add);
        }

        public void Script(string name, string text) => File.WriteAllText(Path.Combine(Root, name), text.Replace("\r\n", "\n"));

        /// <summary>Waits for the next event matching the predicate (earlier non-matching events are kept in Seen).</summary>
        public JsonNode Next(Func<JsonNode, bool> match, int timeoutMs = 20000)
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

        public JsonNode Request(string type, JsonObject body)
        {
            int id = ++_id;
            body["type"] = type; body["id"] = id;
            Session.Handle(body);
            string reply = type switch { "scopes" => "scopes", "variables" => "variables", "setVariable" => "variableSet", "values" => "values", "jump" => "jumped", "table" => "table", "setName" => "setName", "reprOf" => "reprOf", "typeSource" => "typeSource", _ => "evaluated" };
            return Next(e => e["type"]?.GetValue<string>() == reply && e["id"]?.GetValue<int>() == id);
        }

        public void Command(string type) => Session.Handle(new JsonObject { ["type"] = type });

        public void Dispose()
        {
            Session.Dispose();
            try { Directory.Delete(Root, true); } catch (IOException) { }
        }
    }

    static int Line(JsonNode stopped) => stopped["frames"]![0]!["line"]!.GetValue<int>();

    [Fact]
    public void RunCapturesOutputAndExitCode()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("hello.py", "import sys\nprint('hello')\nprint('oops', file=sys.stderr)\nsys.exit(3)\n");
        h.Session.Start(h.Root, "hello.py", false, Array.Empty<string>());
        var exited = h.Type("exited");
        Assert.Equal(3, exited["code"]!.GetValue<int>());
        Assert.Contains("hello", h.AllOutput());
        Assert.True(h.Seen.Any(e => e["stream"]?.GetValue<string>() == "stderr" && e["text"]!.GetValue<string>().Contains("oops")), "stderr is reported separately");
    }

    [Fact]
    public void ARunConfigurationAddsVariablesAndChoosesTheFolder()
    {
        if (!HavePython) return;
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Root, "data"));
        h.Script("show.py", string.Join('\n', "import os", "print('MODE=' + os.environ.get('PM_MODE', '-'))", "print('CWD=' + os.path.basename(os.getcwd()))", "print('HOME_OF=' + os.environ.get('PYTHONPATH', '').split(os.pathsep)[0].split(os.sep)[-1])", ""));
        h.Session.ExtraEnvironment = new Dictionary<string, string> { ["PM_MODE"] = "fast" };
        h.Session.WorkingDirectory = Path.Combine(h.Root, "data");
        h.Session.Start(h.Root, "show.py", false, Array.Empty<string>());
        h.Type("exited");
        string output = h.AllOutput();
        Assert.Contains("MODE=fast", output);
        Assert.Contains("CWD=data", output);
        Assert.Contains("HOME_OF=" + Path.GetFileName(h.Root), output);   // the project is still on PYTHONPATH
    }

    [Fact]
    public void AModuleRunsAsPythonDashMAndStopsAtItsBreakpoints()
    {
        if (!HavePython) return;
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Root, "tool"));
        File.WriteAllText(Path.Combine(h.Root, "tool", "__init__.py"), "");
        File.WriteAllText(Path.Combine(h.Root, "tool", "core.py"), "def greet(who):\n    return 'hi ' + who\n");
        File.WriteAllText(Path.Combine(h.Root, "tool", "__main__.py"), "import sys\nfrom .core import greet\nmsg = greet(sys.argv[1])\nprint(msg, __name__)\n");
        h.Session.StartModule(h.Root, "tool", false, new[] { "ann" }, null);
        h.Type("exited");
        Assert.Contains("hi ann __main__", h.AllOutput());

        using var d = new Harness();
        Directory.CreateDirectory(Path.Combine(d.Root, "tool"));
        File.WriteAllText(Path.Combine(d.Root, "tool", "__init__.py"), "");
        File.WriteAllText(Path.Combine(d.Root, "tool", "core.py"), "def greet(who):\n    return 'hi ' + who\n");
        File.WriteAllText(Path.Combine(d.Root, "tool", "__main__.py"), "import sys\nfrom .core import greet\nmsg = greet(sys.argv[1])\nprint(msg, __name__)\n");
        d.Session.StartModule(d.Root, "tool", true, new[] { "bob" },
            new Dictionary<string, JsonArray> { ["tool/core.py"] = new JsonArray(2) });
        var stopped = d.Type("stopped");
        Assert.Equal(2, Line(stopped));
        Assert.True(stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("core.py"));
        d.Command("continue");
        d.Type("exited");
        Assert.Contains("hi bob __main__", d.AllOutput());

        using var bad = new Harness();
        bad.Session.StartModule(bad.Root, "-c print(1)", false, Array.Empty<string>(), null);
        Assert.True(bad.Type("error")["message"]!.GetValue<string>().Contains("not a module name"));
    }

    [Fact]
    public void DotEnvFilesAreReadAndPythonPathFollowsTheRoots()
    {
        var env = RunConfigurations.ParseDotEnv("# settings\nDEBUG=1\nexport NAME = Ann \nQUOTED=\"a\\nb\"\nSINGLE='x # y'\nCOMMENTED=v # note\nbroken line\n=nothing\n");
        Assert.Equal("DEBUG=1|NAME=Ann|QUOTED=a\nb|SINGLE=x # y|COMMENTED=v", string.Join("|", env.Select(kv => kv.Key + "=" + kv.Value)));
        Assert.True(RunConfigurations.IsModuleName("uvicorn") && RunConfigurations.IsModuleName("a.b_c") && !RunConfigurations.IsModuleName("-m x") && !RunConfigurations.IsModuleName("a..b"));

        if (!HavePython) return;
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Root, "src"));
        h.Script("show.py", "import os\nprint('PP=' + '|'.join(os.path.basename(p) for p in os.environ.get('PYTHONPATH', '').split(os.pathsep) if p))\n");
        h.Session.PythonPath = new[] { Path.Combine(h.Root, "src") };
        h.Session.Start(h.Root, "show.py", false, Array.Empty<string>());
        h.Type("exited");
        Assert.Contains("PP=src", h.AllOutput());
    }

    [Fact]
    public void OnlyHarmlessVariablesAreTakenFromAConfiguration()
    {
        var env = (JsonObject)JsonNode.Parse("""{ "OK_NAME": "1", "PATH": "x", "pythonpath": "y", "bad name": "z", "1ABC": "z", "NUM": 5, "LONG": "%LONG%" }""".Replace("%LONG%", new string('a', RunConfigurations.MaxValueLength + 1)))!;
        var clean = RunConfigurations.CleanEnvironment(env);
        Assert.Equal("OK_NAME", string.Join(",", clean.Keys));
        Assert.Equal(0, RunConfigurations.CleanEnvironment(null).Count);
        var many = new JsonObject();
        for (int i = 0; i < 80; i++) many["V" + i] = "x";
        Assert.Equal(RunConfigurations.MaxVariables, RunConfigurations.CleanEnvironment(many).Count);
    }

    [Fact]
    public void RunPassesArgumentsAndStdinAndSetsPythonPath()
    {
        if (!HavePython) return;
        using var h = new Harness();
        Directory.CreateDirectory(Path.Combine(h.Root, "pkg"));
        File.WriteAllText(Path.Combine(h.Root, "pkg", "__init__.py"), "VALUE = 41\n");
        h.Script("main.py", "import sys\nfrom pkg import VALUE\nname = input('name? ')\nprint(name.upper(), VALUE + 1, sys.argv[1:])\n");
        h.Session.Start(h.Root, "main.py", false, new[] { "a", "b" });
        h.Next(e => e["type"]?.GetValue<string>() == "output" && e["text"]!.GetValue<string>().Contains("name?"));
        h.Session.Handle(new JsonObject { ["type"] = "stdin", ["text"] = "bob\n" });
        h.Type("exited");
        Assert.Contains("BOB 42 ['a', 'b']", h.AllOutput());
    }

    [Fact]
    public void StopKillsARunningProgram()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("loop.py", "import time\nprint('up', flush=True)\ntime.sleep(60)\n");
        h.Session.Start(h.Root, "loop.py", false, Array.Empty<string>());
        h.Next(e => e["text"]?.GetValue<string>().Contains("up") == true);
        h.Session.Handle(new JsonObject { ["type"] = "stop" });
        h.Type("exited");
    }

    [Fact]
    public void MissingScriptAndInterpreterAreReported()
    {
        using var h = new Harness();
        h.Session.Start(h.Root, "nope.py", false, Array.Empty<string>());
        Assert.Contains("script not found", h.Type("error")["message"]!.GetValue<string>());

        h.Script("a.py", "print(1)\n");
        h.Session.Python = "definitely-not-an-interpreter";
        h.Session.Start(h.Root, "a.py", false, Array.Empty<string>());
        Assert.Contains("cannot start", h.Type("error")["message"]!.GetValue<string>());
    }

    const string Program =
        "def add(a, b):\n" +          // 1
        "    total = a + b\n" +       // 2
        "    return total\n" +        // 3
        "\n" +                        // 4
        "x = 10\n" +                  // 5
        "items = [1, 2, {'k': 'v'}]\n" +  // 6
        "y = add(x, 5)\n" +           // 7
        "print('y =', y)\n";          // 8

    [Fact]
    public void DebugStopsAtBreakpointAndInspectsVariables()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("prog.py", Program);
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 7 } });

        var stopped = h.Type("stopped");
        Assert.Equal("breakpoint", stopped["reason"]!.GetValue<string>());
        Assert.Equal(7, Line(stopped));
        Assert.True(stopped["frames"]![0]!["file"]!.GetValue<string>().EndsWith("prog.py"));

        var scopes = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]!.AsArray();
        Assert.Equal("Globals", scopes[^1]!["name"]!.GetValue<string>());
        var vars = h.Request("variables", new JsonObject { ["ref"] = scopes[^1]!["ref"]!.GetValue<int>() })["items"]!.AsArray();
        var x = vars.Single(v => v!["name"]!.GetValue<string>() == "x")!;
        Assert.Equal("10", x["value"]!.GetValue<string>());
        Assert.Equal("int", x["type"]!.GetValue<string>());
        var items = vars.Single(v => v!["name"]!.GetValue<string>() == "items")!;
        int itemsRef = items["ref"]!.GetValue<int>();
        Assert.True(itemsRef > 0, "lists are expandable");
        var elements = h.Request("variables", new JsonObject { ["ref"] = itemsRef })["items"]!.AsArray();
        Assert.Equal(3, elements.Count);
        Assert.Equal("{'k': 'v'}", elements[2]!["value"]!.GetValue<string>());

        var eval = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "x * 2 + len(items)" });
        Assert.Equal("23", eval["result"]!.GetValue<string>());
        Assert.Equal("int", eval["valueType"]!.GetValue<string>());
        var bad = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "nope + 1" });
        Assert.Contains("NameError", bad["error"]!.GetValue<string>());

        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("y = 15", h.AllOutput());
    }

    [Fact]
    public void SteppingInAndOverAndOut()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("prog.py", Program);
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 7 } });
        Assert.Equal(7, Line(h.Type("stopped")));

        h.Command("stepIn");
        var inside = h.Type("stopped");
        Assert.Equal("step", inside["reason"]!.GetValue<string>());
        Assert.Equal(2, Line(inside));
        Assert.Equal("add", inside["frames"]![0]!["name"]!.GetValue<string>());
        Assert.Equal(2, inside["frames"]!.AsArray().Count);   // add and <module>

        var locals = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]![0]!;
        Assert.Equal("Locals", locals["name"]!.GetValue<string>());
        var names = h.Request("variables", new JsonObject { ["ref"] = locals["ref"]!.GetValue<int>() })["items"]!.AsArray()
            .Select(v => v!["name"]!.GetValue<string>()).ToList();
        Assert.True(names.Contains("a") && names.Contains("b"), string.Join(",", names));

        h.Command("next");
        Assert.Equal(3, Line(h.Type("stopped")));

        h.Command("stepOut");
        var back = h.Type("stopped");
        Assert.Equal(7, Line(back));   // returned into the caller (still on the assignment line)
        Assert.Equal("<module>", back["frames"]![0]!["name"]!.GetValue<string>());

        h.Command("next");
        Assert.Equal(8, Line(h.Type("stopped")));
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void BreakpointsCanBeChangedWhileStopped()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("prog.py", Program);
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 5 } });
        Assert.Equal(5, Line(h.Type("stopped")));

        // move the breakpoint into the function: it is only hit when add() is called
        h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "prog.py"), ["lines"] = new JsonArray(3) });
        h.Command("continue");
        var inFunc = h.Type("stopped");
        Assert.Equal(3, Line(inFunc));
        Assert.Equal("breakpoint", inFunc["reason"]!.GetValue<string>());

        h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "prog.py"), ["lines"] = new JsonArray() });
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void UncaughtExceptionStopsForInspection()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("boom.py", "def f(n):\n    items = [1]\n    return items[n]\n\nf(5)\n");
        h.Session.Start(h.Root, "boom.py", true, Array.Empty<string>());

        var stopped = h.Type("stopped");
        Assert.Equal("exception", stopped["reason"]!.GetValue<string>());
        Assert.Contains("IndexError", stopped["message"]!.GetValue<string>());
        Assert.Equal(3, Line(stopped));
        Assert.Equal("f", stopped["frames"]![0]!["name"]!.GetValue<string>());

        var eval = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "n" });
        Assert.Equal("5", eval["result"]!.GetValue<string>());

        h.Command("continue");
        Assert.Equal(1, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("IndexError", h.AllOutput());
    }

    [Fact]
    public void DebugRunsWithoutBreakpointsAndKeepsExitCodes()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("quick.py", "import sys\nprint('done')\nsys.exit(4)\n");
        h.Session.Start(h.Root, "quick.py", true, Array.Empty<string>());
        Assert.Equal(4, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("done", h.AllOutput());
    }

    [Fact]
    public void DebuggerDoesNotStopInLibraryCode()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("lib_call.py", "import json\ndata = json.dumps({'a': 1})\nprint(data)\n");
        h.Session.Start(h.Root, "lib_call.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["lib_call.py"] = new() { 2 } });
        Assert.Equal(2, Line(h.Type("stopped")));
        h.Command("stepIn");   // json.dumps is library code: stepping goes straight to the next user line
        Assert.Equal(3, Line(h.Type("stopped")));
        h.Command("continue");
        h.Type("exited");
    }

    // ------------------------------------------------------------ breakpoint conditions, hit counts and log points

    const string Loop =
        "total = 0\n" +                      // 1
        "for i in range(6):\n" +             // 2
        "    total += i\n" +                 // 3
        "print('total', total)\n";           // 4

    static Dictionary<string, JsonArray> Spec(string file, JsonObject spec) => new() { [file] = new JsonArray(spec) };

    string EvalInt(Harness h, string expression) => h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = expression })["result"]!.GetValue<string>();

    [Fact]
    public void ConditionalBreakpointStopsOnlyWhenTheConditionHolds()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("loop.py", Loop);
        h.Session.StartWithBreakpoints(h.Root, "loop.py", true, Array.Empty<string>(), Spec("loop.py", new JsonObject { ["line"] = 3, ["condition"] = "i == 3" }));
        var stopped = h.Type("stopped");
        Assert.Equal(3, Line(stopped));
        Assert.Equal("3", EvalInt(h, "i"));
        Assert.Equal("3", EvalInt(h, "total"));   // 0 + 1 + 2 before this iteration
        h.Command("continue");
        h.Type("exited");   // it did not stop again for i == 4 or 5
    }

    [Fact]
    public void HitCountsSelectTheIterations()
    {
        if (!HavePython) return;
        using (var h = new Harness())
        {
            h.Script("loop.py", Loop);
            h.Session.StartWithBreakpoints(h.Root, "loop.py", true, Array.Empty<string>(), Spec("loop.py", new JsonObject { ["line"] = 3, ["hitCondition"] = "3" }));
            h.Type("stopped");
            Assert.Equal("2", EvalInt(h, "i"));   // the third time the line is reached
            h.Command("continue");
            h.Type("exited");
        }
        using (var h = new Harness())
        {
            h.Script("loop.py", Loop);
            h.Session.StartWithBreakpoints(h.Root, "loop.py", true, Array.Empty<string>(), Spec("loop.py", new JsonObject { ["line"] = 3, ["hitCondition"] = "%2" }));
            h.Type("stopped"); Assert.Equal("1", EvalInt(h, "i"));
            h.Command("continue");
            h.Type("stopped"); Assert.Equal("3", EvalInt(h, "i"));
            h.Command("continue");
            h.Type("stopped"); Assert.Equal("5", EvalInt(h, "i"));
            h.Command("continue");
            h.Type("exited");
        }
    }

    const string Threaded =
        "import threading\n" +                       // 1
        "\n" +                                        // 2
        "def work(n):\n" +                            // 3
        "    result = n * 10\n" +                     // 4
        "    return result\n" +                       // 5
        "\n" +                                        // 6
        "t = threading.Thread(target=work, args=(7,), name='worker-1')\n" +  // 7
        "t.start()\n" +                               // 8
        "t.join()\n" +                                // 9
        "print('joined')\n";                          // 10

    [Fact]
    public void ABreakpointInAThreadStopsThere()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("threads.py", Threaded);
        h.Session.StartWithBreakpoints(h.Root, "threads.py", true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["threads.py"] = new JsonArray(4) });
        var stopped = h.Type("stopped");
        Assert.Equal(4, Line(stopped));
        Assert.Equal("worker-1", stopped["thread"]!.GetValue<string>());
        Assert.Equal("7", EvalInt(h, "n"));
        h.Command("next");
        Assert.Equal(5, Line(h.Type("stopped")));
        Assert.Equal("70", EvalInt(h, "result"));
        h.Command("continue");
        h.Type("exited");
        Assert.Contains("joined", h.AllOutput());
    }

    [Fact]
    public void ThreadsThatHitABreakpointAtTheSameTimeAreStoppedOneAfterTheOther()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("pool.py",
            "import threading\n\ndef work(n):\n    value = n\n    return value\n\n" +
            "ts = [threading.Thread(target=work, args=(i,), name=f'w{i}') for i in range(3)]\n" +
            "for t in ts:\n    t.start()\nfor t in ts:\n    t.join()\nprint('done')\n");
        h.Session.StartWithBreakpoints(h.Root, "pool.py", true, Array.Empty<string>(), new Dictionary<string, JsonArray> { ["pool.py"] = new JsonArray(4) });
        var seen = new List<string>();
        for (int i = 0; i < 3; i++)
        {
            var stopped = h.Type("stopped");
            Assert.Equal(4, Line(stopped));
            seen.Add(EvalInt(h, "n"));
            h.Command("continue");
        }
        h.Type("exited");
        Assert.Equal("0,1,2", string.Join(",", seen.OrderBy(x => x)));
    }

    [Fact]
    public void ALogPointPrintsAndDoesNotStop()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("loop.py", Loop);
        h.Session.StartWithBreakpoints(h.Root, "loop.py", true, Array.Empty<string>(),
            Spec("loop.py", new JsonObject { ["line"] = 3, ["condition"] = "i % 2 == 0", ["logMessage"] = "i={i} total={total} bad={nope}" }));
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        string output = h.AllOutput();
        Assert.Contains("i=0 total=0 bad=<NameError>", output);
        Assert.Contains("i=4 total=6", output);
        Assert.True(!output.Contains("i=1 "), "the condition filters the log point too");
        Assert.Contains("total 15", output);
    }

    [Fact]
    public void VariablesCanBeChangedWhilePaused()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("vars.py", "def f(a):\n    b = a + 1\n    return b\n\nx = 10\nprint('result', f(x), x)\n");
        h.Session.Start(h.Root, "vars.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["vars.py"] = new() { 3 } });
        h.Type("stopped");
        var scopes = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]!.AsArray();
        int locals = scopes.Single(sc => sc!["name"]!.GetValue<string>() == "Locals")!["ref"]!.GetValue<int>();
        int globals = scopes.Single(sc => sc!["name"]!.GetValue<string>() == "Globals")!["ref"]!.GetValue<int>();
        h.Request("variables", new JsonObject { ["ref"] = locals });   // the scope has to be listed once, as the page does
        h.Request("variables", new JsonObject { ["ref"] = globals });

        var local = h.Request("setVariable", new JsonObject { ["ref"] = locals, ["name"] = "b", ["expression"] = "a * 100" });
        Assert.Equal("1000", local["value"]!.GetValue<string>());
        var global = h.Request("setVariable", new JsonObject { ["ref"] = globals, ["name"] = "x", ["expression"] = "'changed'" });
        Assert.Equal("'changed'", global["value"]!.GetValue<string>());
        var bad = h.Request("setVariable", new JsonObject { ["ref"] = locals, ["name"] = "missing", ["expression"] = "1" });
        Assert.True(bad["error"] != null, "unknown names are refused");
        var bad2 = h.Request("setVariable", new JsonObject { ["ref"] = locals, ["name"] = "b", ["expression"] = "1 +" });
        Assert.True(bad2["error"] != null, "bad expressions are reported");

        h.Command("continue");
        h.Type("exited");
        Assert.Contains("result 1000 changed", h.AllOutput());
    }

    [Fact]
    public void BreakingOnRaisedExceptionsStopsAtCaughtOnes()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("raised.py", "def risky(n):\n    if n > 1:\n        raise ValueError('too big')\n    return n\n\ntry:\n    risky(5)\nexcept ValueError:\n    print('handled')\nprint('end')\n");
        h.Session.Start(h.Root, "raised.py", true, Array.Empty<string>());
        // the setting arrives before the program runs far enough to matter: send it with the (empty) breakpoint list
        h.Session.Handle(new JsonObject { ["type"] = "setExceptionBreak", ["mode"] = "raised" });
        var stopped = h.Type("stopped");
        Assert.Equal("exception", stopped["reason"]!.GetValue<string>());
        Assert.Contains("ValueError: too big", stopped["message"]!.GetValue<string>());
        Assert.Equal(3, Line(stopped));
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.Contains("handled", h.AllOutput());
    }

    // ---------------------------------------------------------------- item 158: the debugger's places and values

    [Fact]
    public void ValuesOfTheNamesOfALineAreGivenForTheEditor()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("prog.py", Program);
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 2 } });
        var stopped = h.Type("stopped");
        Assert.Equal(1, stopped["frames"]![0]!["first"]!.GetValue<int>());   // where the function starts: the lines that get values
        var values = h.Request("values", new JsonObject { ["frameId"] = 0, ["names"] = new JsonArray("a", "b", "x", "add", "nope") })["values"]!.AsObject();
        Assert.Equal("10", values["a"]!.GetValue<string>());
        Assert.Equal("5", values["b"]!.GetValue<string>());
        Assert.Equal("10", values["x"]!.GetValue<string>());   // a global the function sees
        Assert.True(!values.ContainsKey("add") && !values.ContainsKey("nope"), values.ToJsonString());   // functions and unknown names are left out
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void StepOverKeepsTheReturnValuesOfTheCalls()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("prog.py", Program);
        h.Session.Start(h.Root, "prog.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["prog.py"] = new() { 7 } });
        h.Type("stopped");
        h.Command("next");
        Assert.Equal(8, Line(h.Type("stopped")));
        var scopes = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]!.AsArray();
        Assert.Equal("Return values", scopes[0]!["name"]!.GetValue<string>());
        var returned = h.Request("variables", new JsonObject { ["ref"] = scopes[0]!["ref"]!.GetValue<int>() })["items"]!.AsArray();
        Assert.Equal("add()", returned[0]!["name"]!.GetValue<string>());
        Assert.Equal("15", returned[0]!["value"]!.GetValue<string>());
        // the next step starts afresh: line 8 calls nothing of the project
        h.Command("next");
        h.Type("exited");
    }

    const string CountLoop =
        "total = 0\n" +               // 1
        "for i in range(3):\n" +      // 2
        "    total += i\n" +          // 3
        "done = total\n" +            // 4
        "print('done', done)\n";      // 5

    [Fact]
    public void RunToCursorStopsAtABreakpointOnTheWayAndForceRunsPastIt()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("loop.py", CountLoop);
        h.Session.Start(h.Root, "loop.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["loop.py"] = new() { 1, 3 } });
        Assert.Equal(1, Line(h.Type("stopped")));
        h.Session.Handle(new JsonObject { ["type"] = "runTo", ["file"] = "loop.py", ["line"] = 4 });
        Assert.Equal(3, Line(h.Type("stopped")));   // the breakpoint on the way stops first
        h.Session.Handle(new JsonObject { ["type"] = "runTo", ["file"] = "loop.py", ["line"] = 4, ["force"] = true });
        var at = h.Type("stopped");
        Assert.Equal(4, Line(at));                  // the two other rounds of the loop are passed
        var total = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "total" });
        Assert.Equal("3", total["result"]!.GetValue<string>());
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void SmartStepIntoEntersTheChosenCallOnly()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("calls.py", "def inner():\n    return 2\n\ndef outer(x):\n    return x * 3\n\nr = outer(inner())\nprint(r)\n");
        h.Session.Start(h.Root, "calls.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["calls.py"] = new() { 7 } });
        h.Type("stopped");
        h.Session.Handle(new JsonObject { ["type"] = "stepInto", ["target"] = "outer" });
        var inside = h.Type("stopped");
        Assert.Equal("outer", inside["frames"]![0]!["name"]!.GetValue<string>());   // inner() ran first without stopping
        Assert.Equal(5, Line(inside));
        h.Command("continue");
        h.Type("exited");

        // a call the line does not make: the step ends on the next line
        using var h2 = new Harness();
        h2.Script("calls.py", "def inner():\n    return 2\n\ndef outer(x):\n    return x * 3\n\nr = outer(inner())\nprint(r)\n");
        h2.Session.Start(h2.Root, "calls.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["calls.py"] = new() { 7 } });
        h2.Type("stopped");
        h2.Session.Handle(new JsonObject { ["type"] = "stepInto", ["target"] = "missing" });
        var next = h2.Type("stopped");
        Assert.Equal(8, Line(next));
        Assert.Equal("<module>", next["frames"]![0]!["name"]!.GetValue<string>());
        h2.Command("continue");
        h2.Type("exited");
    }

    [Fact]
    public void JumpToCursorSkipsTheLinesInBetween()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("jump.py", "a = 1\na = a + 10\na = a + 100\nprint('a is', a)\n");
        h.Session.Start(h.Root, "jump.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["jump.py"] = new() { 2 } });
        h.Type("stopped");
        var jumped = h.Request("jump", new JsonObject { ["line"] = 4 });
        Assert.True(jumped["error"] is null, jumped.ToJsonString());
        Assert.Equal(4, jumped["frames"]![0]!["line"]!.GetValue<int>());
        h.Command("continue");
        h.Type("exited");
        Assert.Contains("a is 1", h.AllOutput());
    }

    [Fact]
    public void MutedBreakpointsNeitherStopNorLog()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("loop.py", CountLoop);
        h.Session.Start(h.Root, "loop.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["loop.py"] = new() { 3 } });
        h.Session.Handle(new JsonObject { ["type"] = "mute", ["on"] = true });
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
        Assert.True(!h.Seen.Any(e => e["type"]?.GetValue<string>() == "stopped"));
        Assert.Contains("done 3", h.AllOutput());
    }

    [Fact]
    public void BreakingOnRaisedExceptionsCanBeLimitedToSomeClasses()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("raised.py", "class Mine(ValueError):\n    pass\n\ntry:\n    {}['k']\nexcept KeyError:\n    pass\ntry:\n    raise Mine('x')\nexcept ValueError:\n    pass\nprint('end')\n");
        h.Session.Start(h.Root, "raised.py", true, Array.Empty<string>());
        h.Session.Handle(new JsonObject { ["type"] = "setExceptionBreak", ["mode"] = "raised", ["classes"] = new JsonArray("ValueError") });
        var stopped = h.Type("stopped");
        Assert.Equal(9, Line(stopped));   // the KeyError is passed; Mine is a ValueError
        Assert.Contains("Mine: x", stopped["message"]!.GetValue<string>());
        h.Command("continue");
        Assert.Equal(0, h.Type("exited")["code"]!.GetValue<int>());
    }

    // ---------------------------------------------------------------- item 159: more of the debugger

    const string Calls =
        "import textwrap\n" +                 // 1
        "\n" +                                // 2
        "def deco(f):\n" +                    // 3
        "    return f\n" +                    // 4
        "\n" +                                // 5
        "@deco\n" +                           // 6
        "def work(n):\n" +                    // 7
        "    inner = n * 2\n" +               // 8
        "    return inner\n" +                // 9
        "\n" +                                // 10
        "a = work(1)\n" +                     // 11
        "b = textwrap.dedent('  x')\n" +      // 12
        "c = work(2)\n" +                     // 13
        "print(a, b, c)\n";                   // 14

    static JsonNode Bp(int line, JsonObject? extra = null)
    {
        var o = extra ?? new JsonObject();
        o["line"] = line;
        return o;
    }

    void SetBreakpoints(Harness h, params JsonNode[] lines) =>
        h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "calls.py"), ["lines"] = new JsonArray(lines) });

    [Fact]
    public void ABreakpointOnADefLineStopsWhenTheFunctionIsCalled()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("calls.py", Calls);
        h.Session.Start(h.Root, "calls.py", true, Array.Empty<string>());
        // decorated: its code starts on the decorator's line, which the page sends as codeLine
        SetBreakpoints(h, Bp(7, new JsonObject { ["function"] = "work", ["codeLine"] = 6 }));
        var first = h.Type("stopped");
        Assert.Equal("work", first["frames"]![0]!["name"]!.GetValue<string>());   // not when the def statement runs
        Assert.Equal(8, Line(first));
        h.Command("continue");
        Assert.Equal(8, Line(h.Type("stopped")));   // the second call
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void ABreakpointCanWaitUntilAnotherOneIsHit()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("calls.py", Calls);
        h.Session.Start(h.Root, "calls.py", true, Array.Empty<string>());
        // line 8 does nothing until line 12 is hit: the first call of work() passes, the second stops
        SetBreakpoints(h, Bp(8, new JsonObject { ["after"] = new JsonObject { ["file"] = "calls.py", ["line"] = 12 } }), Bp(12));
        Assert.Equal(12, Line(h.Type("stopped")));
        h.Command("continue");
        Assert.Equal(8, Line(h.Type("stopped")));
        Assert.Equal("2", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "n" })["result"]!.GetValue<string>());
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void ForceStepOverPassesTheBreakpointsOfTheCalls()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("calls.py", Calls);
        h.Session.Start(h.Root, "calls.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["calls.py"] = new() { 8, 11 } });
        Assert.Equal(11, Line(h.Type("stopped")));
        h.Session.Handle(new JsonObject { ["type"] = "next", ["force"] = true });
        Assert.Equal(12, Line(h.Type("stopped")));   // the breakpoint in work() was passed
        h.Command("next");
        Assert.Equal(13, Line(h.Type("stopped")));
        h.Command("next");
        Assert.Equal(8, Line(h.Type("stopped")));    // a plain step over still stops at the breakpoint of the call
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void ForceStepIntoStopsInLibraryCode()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("calls.py", Calls);
        h.Session.Start(h.Root, "calls.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["calls.py"] = new() { 12 } });
        Assert.Equal(12, Line(h.Type("stopped")));
        h.Session.Handle(new JsonObject { ["type"] = "stepIn", ["force"] = true });
        var inside = h.Type("stopped");
        Assert.Equal("dedent", inside["frames"]![0]!["name"]!.GetValue<string>());
        Assert.True(inside["frames"]![0]!["file"]!.GetValue<string>().EndsWith("textwrap.py"));
        h.Command("continue");
        h.Type("exited");
    }

    [Fact]
    public void ValuesAreGivenAsTablesForTheDataView()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("data.py", "rows = [{'a': 1, 'b': 'x'}, {'a': 2, 'c': None}]\npairs = [(1, 2), (3, 4, 5)]\nd = {'k': [1]}\nn = 3\nprint(n)\n");
        h.Session.Start(h.Root, "data.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["data.py"] = new() { 5 } });
        h.Type("stopped");
        var rows = h.Request("table", new JsonObject { ["frameId"] = 0, ["expression"] = "rows" });
        Assert.Equal("a,b,c", string.Join(",", rows["columns"]!.AsArray().Select(c => c!.GetValue<string>())));
        Assert.Equal("2,,None", string.Join(",", rows["rows"]![1]!.AsArray().Select(c => c!.GetValue<string>())));
        var pairs = h.Request("table", new JsonObject { ["frameId"] = 0, ["expression"] = "pairs" });
        Assert.Equal(3, pairs["columns"]!.AsArray().Count);
        var d = h.Request("table", new JsonObject { ["frameId"] = 0, ["expression"] = "d" });
        Assert.Equal("'k',[1]", string.Join(",", d["rows"]![0]!.AsArray().Select(c => c!.GetValue<string>())));
        Assert.Contains("not shown as a table", h.Request("table", new JsonObject { ["frameId"] = 0, ["expression"] = "n" })["error"]!.GetValue<string>());
        // the whole value, past the panes' 200 characters
        var full = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "'x' * 5000", ["evalOnly"] = true, ["limit"] = 100000 });
        Assert.Equal(5002, full["result"]!.GetValue<string>().Length);
        h.Command("continue");
        h.Type("exited");
    }

    // ---------------------------------------------------------------- item 160: the rest of the debugger

    const string Spinner =
        "import threading, time\n" +            // 1
        "count = 0\n" +                         // 2
        "def spin():\n" +                       // 3
        "    global count\n" +                  // 4
        "    while True:\n" +                   // 5
        "        count += 1\n" +                // 6
        "        if count % 500 == 0:\n" +      // 7
        "            time.sleep(0.001)\n" +     // 8
        "threading.Thread(target=spin, daemon=True).start()\n" +   // 9
        "time.sleep(0.3)\n" +                   // 10
        "here = 1\n" +                          // 11
        "print('done')\n";                      // 12

    /// <summary>How far the spinning thread counts while the main thread is stopped for 400 ms.</summary>
    static long CountsWhileStopped(Harness h)
    {
        long Count() => long.Parse(h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "count", ["evalOnly"] = true })["result"]!.GetValue<string>());
        long before = Count();
        Thread.Sleep(400);
        return Count() - before;
    }

    [Fact]
    public void AStopSuspendsTheOtherThreadsUnlessTheBreakpointSaysThisThreadOnly()
    {
        if (!HavePython) return;
        using (var h = new Harness())
        {
            h.Script("spin.py", Spinner);
            h.Session.Start(h.Root, "spin.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["spin.py"] = new() { 11 } });
            var stopped = h.Type("stopped");
            Assert.Equal("all", stopped["suspended"]!.GetValue<string>());
            Assert.Equal(0L, CountsWhileStopped(h));   // the spinning thread waits at its next line
            h.Command("continue");
            h.Type("exited");
        }
        using (var h = new Harness())
        {
            h.Script("spin.py", Spinner);
            h.Session.Start(h.Root, "spin.py", true, Array.Empty<string>());
            h.Session.Handle(new JsonObject { ["type"] = "setBreakpoints", ["file"] = Path.Combine(h.Root, "spin.py"),
                ["lines"] = new JsonArray(new JsonObject { ["line"] = 11, ["suspend"] = "thread" }) });
            Assert.Equal("thread", h.Type("stopped")["suspended"]!.GetValue<string>());
            Assert.True(CountsWhileStopped(h) > 0, "the other thread goes on");
            h.Command("continue");
            h.Type("exited");
        }
    }

    const string BoxProgram =
        "class Box:\n" +                 // 1
        "    def __init__(self):\n" +    // 2
        "        self.size = 3\n" +      // 3
        "def run():\n" +                 // 4
        "    box = Box()\n" +            // 5
        "    n = 1\n" +                  // 6
        "    print(n)\n" +               // 7
        "run()\n";                       // 8

    [Fact]
    public void ANameIsSetAndATypeIsFoundAndValuesLoadOnDemand()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("box.py", BoxProgram);
        h.Session.Start(h.Root, "box.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["box.py"] = new() { 7 } });
        h.Type("stopped");
        // Set Value from the end of a line: a local of the frame
        Assert.Equal("42", h.Request("setName", new JsonObject { ["frameId"] = 0, ["name"] = "n", ["expression"] = "41 + 1" })["value"]!.GetValue<string>());
        Assert.Equal("42", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "n" })["result"]!.GetValue<string>());
        // Jump to Type Source
        var type = h.Request("typeSource", new JsonObject { ["frameId"] = 0, ["expression"] = "box" });
        Assert.True(type["file"]!.GetValue<string>().EndsWith("box.py"), type.ToJsonString());
        Assert.Equal(1, type["line"]!.GetValue<int>());
        Assert.Equal("Box", type["name"]!.GetValue<string>());
        // on demand: objects come without their value until it is asked for; numbers come as they are
        h.Session.Handle(new JsonObject { ["type"] = "setLoadPolicy", ["policy"] = "onDemand" });
        var locals = h.Request("scopes", new JsonObject { ["frameId"] = 0 })["scopes"]![0]!;
        int localsRef = locals["ref"]!.GetValue<int>();
        var items = h.Request("variables", new JsonObject { ["ref"] = localsRef })["items"]!.AsArray();
        int boxIndex = items.Select((v, i) => (v, i)).Single(t => t.v!["name"]!.GetValue<string>() == "box").i;
        Assert.True(items[boxIndex]!["lazy"]?.GetValue<bool>() == true && items[boxIndex]!["value"] is null, items.ToJsonString());
        Assert.Equal("42", items.Single(v => v!["name"]!.GetValue<string>() == "n")!["value"]!.GetValue<string>());
        Assert.Contains("Box object", h.Request("reprOf", new JsonObject { ["ref"] = localsRef, ["index"] = boxIndex })["value"]!.GetValue<string>());
        h.Command("continue");
        h.Type("exited");
        Assert.Contains("42", h.AllOutput());   // the program printed the value it was given
    }

    [Fact]
    public void TheConsoleShowsWhatWasPrintedAndTheValueDialogGetsJson()
    {
        if (!HavePython) return;
        using var h = new Harness();
        h.Script("box.py", BoxProgram);
        h.Session.Start(h.Root, "box.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["box.py"] = new() { 7 } });
        h.Type("stopped");
        var printed = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "for k in range(2):\n    print('line', k)" });
        Assert.Equal("line 0\nline 1\n", printed["output"]!.GetValue<string>().Replace("\r\n", "\n"));
        var call = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "print('hi') or 5" });
        Assert.Equal("5", call["result"]!.GetValue<string>());
        Assert.Equal("hi\n", call["output"]!.GetValue<string>().Replace("\r\n", "\n"));
        var json = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "{'a': [1, 2], 'b': None}", ["evalOnly"] = true, ["detail"] = true });
        Assert.Equal("{\"a\": [1, 2], \"b\": null}", json["json"]!.GetValue<string>());
        var text = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "'{\"k\": 1}'", ["evalOnly"] = true, ["detail"] = true });
        Assert.Equal("{\"k\": 1}", text["text"]!.GetValue<string>());
        h.Command("continue");
        h.Type("exited");
    }

    /// <summary>A Python 3.14 or later on this computer (Attach to Process needs sys.remote_exec), or null.</summary>
    static string? Python314()
    {
        foreach (var (file, args) in new[] { ("py", "-3.14 -c \"import sys; print(sys.executable)\""), ("python", "-c \"import sys; print(sys.executable if sys.version_info >= (3, 14) else '')\"") })
        {
            try
            {
                var p = Process.Start(new ProcessStartInfo(file, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
                if (p == null) continue;
                string path = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(10000);
                if (p.ExitCode == 0 && File.Exists(path)) return path;
            }
            catch (System.ComponentModel.Win32Exception) { }
        }
        return null;
    }

    [Fact]
    public void TheDebuggerIsAttachedToARunningProgramAndDetachedWithoutEndingIt()
    {
        if (!HavePython || Python314() is not { } python) return;
        using var h = new Harness();
        h.Script("target.py", "import time\ndef tick(i):\n    x = i * 2\n    return x\nfor i in range(1200):\n    tick(i)\n    time.sleep(0.05)\n");
        using var target = Process.Start(new ProcessStartInfo(python, Path.Combine(h.Root, "target.py")) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Thread.Sleep(500);   // the program is running before the debugger comes
            h.Session.Python = python;
            h.Session.Attach(h.Root, target.Id, new Dictionary<string, JsonArray> { ["target.py"] = new JsonArray(3) });
            Assert.True(h.Type("started")["attached"]!.GetValue<bool>());
            var stopped = h.Type("stopped");
            Assert.Equal("tick", stopped["frames"]![0]!["name"]!.GetValue<string>());
            Assert.Equal(3, Line(stopped));
            Assert.Equal("int", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "i" })["valueType"]!.GetValue<string>());
            h.Command("stop");   // detaches
            Assert.True(h.Type("exited")["detached"]!.GetValue<bool>());
            Thread.Sleep(500);
            Assert.True(!target.HasExited, "the program goes on running after the debugger is taken out");
        }
        finally
        {
            try { target.Kill(); } catch (InvalidOperationException) { }
        }
    }

    // ---------------------------------------------------------------- item 164: IPython in the debug console

    [Fact]
    public void TheConsoleTakesIPythonSyntaxWhenTheProgramsPythonHasIPython()
    {
        // a Python with IPython installed, named by PYMAGIC_TEST_IPYTHON (none on a plain machine: the test then passes without running)
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_IPYTHON") is not { Length: > 0 } python || !File.Exists(python)) return;
        using var h = new Harness();
        h.Script("box.py", BoxProgram);
        h.Session.Python = python;
        h.Session.Start(h.Root, "box.py", true, Array.Empty<string>(), new Dictionary<string, List<int>> { ["box.py"] = new() { 7 } });
        h.Type("stopped");
        // a line magic, run with the names of the frame
        var timed = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "%timeit -n 3 -r 1 n + 1" });
        Assert.True(timed["error"] is null, timed.ToJsonString());
        Assert.Contains("per loop", timed["output"]!.GetValue<string>());
        Assert.True(timed["ipython"]!.GetValue<bool>());
        // results are shown by IPython's pretty printer, and the frame's names still work as Python
        var pretty = h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "{'name': 'x' * 30, 'items': list(range(30))}" });
        Assert.Contains("\n", pretty["result"]!.GetValue<string>());   // pretty() breaks a long dict over lines
        Assert.Equal("1", h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "n" })["result"]!.GetValue<string>());
        // the watches (eval only) are plain Python: no IPython there
        Assert.True(h.Request("evaluate", new JsonObject { ["frameId"] = 0, ["expression"] = "n", ["evalOnly"] = true })["ipython"] is null);
        h.Command("continue");
        h.Type("exited");
        Assert.Contains("1", h.AllOutput());
    }
}
