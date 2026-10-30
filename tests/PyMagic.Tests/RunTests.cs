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
            string reply = type switch { "scopes" => "scopes", "variables" => "variables", "setVariable" => "variableSet", "values" => "values", "jump" => "jumped", _ => "evaluated" };
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
}
