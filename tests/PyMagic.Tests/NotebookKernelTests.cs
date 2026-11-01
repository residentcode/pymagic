using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The notebook kernel (pm_kernel.py) driven the way the notebook view drives it.</summary>
public class NotebookKernelTests
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

    sealed class Kernel : IDisposable
    {
        public readonly BlockingCollection<JsonNode> Events = new();
        public readonly NotebookKernel Inner;
        public readonly string Root;
        int _next = 1;

        public Kernel()
        {
            string root = Root = Path.Combine(Path.GetTempPath(), "pm_nb_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            Inner = new NotebookKernel(Events.Add) { Python = PythonExe };
            Inner.Start(root);
            Wait(e => e["type"]!.GetValue<string>() == "ready");
        }

        public JsonNode Wait(Func<JsonNode, bool> until, int seconds = 60)
        {
            while (true)
            {
                if (!Events.TryTake(out var e, TimeSpan.FromSeconds(seconds))) throw new AssertionException("timed out");
                if (e["type"]!.GetValue<string>() is "kernelError") throw new AssertionException(e["message"]!.GetValue<string>());
                if (until(e)) return e;
            }
        }

        /// <summary>Runs a cell and returns everything it produced up to and including "done".</summary>
        public List<JsonNode> Run(string code, Action? whileRunning = null)
        {
            string id = "c" + _next++;
            Inner.Execute(id, code);
            whileRunning?.Invoke();
            var got = new List<JsonNode>();
            Wait(e =>
            {
                if (e["id"]?.GetValue<string>() == id) got.Add(e);
                return e["type"]!.GetValue<string>() == "done" && e["id"]?.GetValue<string>() == id;
            });
            return got;
        }

        /// <summary>The members of a value of the Variables window; <c>path</c> is JSON, e.g. [["v","d"],["i",0]].</summary>
        public JsonNode Inspect(string path)
        {
            string id = "i" + _next++;
            Inner.Inspect(id, JsonNode.Parse(path));
            return Wait(e => e["type"]!.GetValue<string>() == "children" && e["id"]?.GetValue<string>() == id);
        }

        /// <summary>The words that complete a dotted name, as the kernel offers them.</summary>
        public List<string> Complete(string prefix)
        {
            string id = "c" + _next++;
            Inner.Complete(id, prefix);
            var answer = Wait(e => e["type"]!.GetValue<string>() == "completions" && e["id"]?.GetValue<string>() == id);
            return answer["items"]!.AsArray().Select(x => x!.GetValue<string>()).ToList();
        }

        public void Dispose() => Inner.Dispose();
    }

    static string Text(List<JsonNode> got, string stream) =>
        string.Concat(got.Where(e => e["type"]!.GetValue<string>() == "stream" && e["name"]!.GetValue<string>() == stream).Select(e => e["text"]!.GetValue<string>()));

    static JsonNode? Of(List<JsonNode> got, string type) => got.FirstOrDefault(e => e["type"]!.GetValue<string>() == type);

    [Fact]
    public void StateIsKeptFromCellToCell()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        k.Run("x = 40");
        var got = k.Run("print('sum', x + 2)");
        Assert.Equal("sum 42\n", Text(got, "stdout"));
        Assert.Equal("ok", Of(got, "done")!["status"]!.GetValue<string>());
    }

    [Fact]
    public void TheNamesACellLeavesAreSentAfterEachRun()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("count = 3\nitems = [1, 2, 3]\n_hidden = 1\nlabel = 'a' * 200");
        var vars = Of(got, "variables")!["vars"]!.AsArray();
        var names = vars.Select(v => v!["name"]!.GetValue<string>()).ToList();
        Assert.Equal("count,items,label", string.Join(",", names));
        var items = vars.First(v => v!["name"]!.GetValue<string>() == "items")!;
        Assert.Equal("list", items["type"]!.GetValue<string>());
        Assert.Equal("[1, 2, 3]", items["value"]!.GetValue<string>());
        Assert.Equal("3", items["size"]!.GetValue<string>());
        Assert.True(vars.First(v => v!["name"]!.GetValue<string>() == "label")!["value"]!.GetValue<string>().Length <= 120);
        // the message comes before "done", also after an error
        var failed = k.Run("boom = 1\n1/0");
        Assert.True(failed.FindIndex(e => e["type"]!.GetValue<string>() == "variables") < failed.FindIndex(e => e["type"]!.GetValue<string>() == "done"));
    }

    [Fact]
    public void TheValueOfALastExpressionIsShown()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("a = 6\nb = 7\na * b");
        var result = Of(got, "result");
        Assert.True(result != null);
        Assert.Equal("42", result!["data"]!["text/plain"]!.GetValue<string>());
        Assert.Equal(1, result["execution_count"]!.GetValue<int>());
        Assert.True(Of(k.Run("y = 1"), "result") == null, "an assignment shows nothing");
        Assert.True(Of(k.Run("None"), "result") == null, "None shows nothing");
        Assert.Equal("42", k.Run("_")[0]["data"]!["text/plain"]!.GetValue<string>());
    }

    [Fact]
    public void AnErrorGivesATracebackWithTheCellsLines()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("def f(n):\n    return 1 / n\n\n\nf(0)");
        var error = Of(got, "error");
        Assert.True(error != null);
        Assert.Equal("ZeroDivisionError", error!["ename"]!.GetValue<string>());
        var trace = string.Join("\n", error["traceback"]!.AsArray().Select(l => l!.GetValue<string>()));
        Assert.Contains("<cell-1>", trace);
        Assert.Contains("return 1 / n", trace);
        Assert.True(!trace.Contains("pm_kernel"), trace);
        Assert.Equal("error", Of(got, "done")!["status"]!.GetValue<string>());
        Assert.Equal("ok", Of(k.Run("1 + 1"), "done")!["status"]!.GetValue<string>());   // the kernel goes on
    }

    [Fact]
    public void ASyntaxErrorIsAnError()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var error = Of(k.Run("def broken(:\n    pass"), "error");
        Assert.True(error != null);
        Assert.Equal("SyntaxError", error!["ename"]!.GetValue<string>());
    }

    [Fact]
    public void StderrAndStdoutAreSeparate()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("import sys\nprint('out')\nprint('err', file=sys.stderr)");
        Assert.Equal("out\n", Text(got, "stdout"));
        Assert.Equal("err\n", Text(got, "stderr"));
    }

    [Fact]
    public void DisplayAndRichRepresentations()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("class T:\n    def _repr_html_(self):\n        return '<b>bold</b>'\n\n\ndisplay(T())\ndisplay(5)");
        var shown = got.Where(e => e["type"]!.GetValue<string>() == "display").ToList();
        Assert.Equal(2, shown.Count);
        Assert.Equal("<b>bold</b>", shown[0]["data"]!["text/html"]!.GetValue<string>());
        Assert.Equal("5", shown[1]["data"]!["text/plain"]!.GetValue<string>());
    }

    [Fact]
    public void ALoopCanBeInterrupted()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("import time\nprint('start', flush=True)\nwhile True:\n    time.sleep(0.01)", whileRunning: () =>
        {
            k.Wait(e => e["type"]!.GetValue<string>() == "stream");     // it is running
            k.Inner.Interrupt();
        });
        var error = Of(got, "error");
        Assert.True(error != null);
        Assert.Equal("KeyboardInterrupt", error!["ename"]!.GetValue<string>());
        Assert.Equal("ok", Of(k.Run("2 + 2"), "done")!["status"]!.GetValue<string>());
    }

    [Fact]
    public void AnInterruptWhileIdleDoesNothing()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        k.Inner.Interrupt();
        Thread.Sleep(300);
        Assert.Equal("5", Of(k.Run("2 + 3"), "result")!["data"]!["text/plain"]!.GetValue<string>());
    }

    [Fact]
    public void ShellAndPipLinesAreRunAndDisplayMagicsAreIgnored()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("%matplotlib inline\n!python -c \"print('from the shell')\"");
        Assert.Contains("from the shell", Text(got, "stdout"));
        Assert.Equal("ok", Of(got, "done")!["status"]!.GetValue<string>());
    }

    [Fact]
    public void InputIsRefusedNotWaitedFor()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var error = Of(k.Run("input('name? ')"), "error");
        Assert.True(error != null);
        Assert.Contains("input()", error!["evalue"]!.GetValue<string>());
    }

    [Fact]
    public void AKernelStopsByItselfWhenTheHostIsGone()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        k.Run("x = 1");
        k.Inner.DropConnection();
        for (int i = 0; i < 100 && k.Inner.Running; i++) Thread.Sleep(100);
        Assert.True(!k.Inner.Running, "the kernel kept running without its host");
    }

    [Fact]
    public void AKernelThatCannotStartIsReported()
    {
        var events = new BlockingCollection<JsonNode>();
        using var kernel = new NotebookKernel(events.Add) { Python = Path.Combine(Path.GetTempPath(), "no_such_python_" + Guid.NewGuid().ToString("N")) };
        kernel.Start(Path.GetTempPath());
        Assert.True(events.TryTake(out var e, TimeSpan.FromSeconds(5)));
        Assert.Equal("kernelError", e!["type"]!.GetValue<string>());
    }

    static List<(string Name, string Type, string Value, bool Expandable)> Members(JsonNode children) =>
        children["children"]!.AsArray().Select(c => (c!["name"]!.GetValue<string>(), c["type"]!.GetValue<string>(), c["value"]!.GetValue<string>(), c["expandable"]!.GetValue<bool>())).ToList();

    [Fact]
    public void ANameIsCompletedFromTheCellsAndWhatTheyHold()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        k.Run("import math\nclass Point:\n    def __init__(self):\n        self.x = 3\n        self._hidden = 1\n    def move(self): return 1\n\n\ncounter = 0\ncart = Point()\n"
            + "def boom():\n    global counter\n    counter += 1\n    return Point()\n\n\nclass Trap:\n    @property\n    def bad(self): raise RuntimeError('no')\n\n\ntrap = Trap()\n");
        Assert.Equal("cart", string.Join(",", k.Complete("car")));
        Assert.True(k.Complete("pri").Contains("print"));
        Assert.True(k.Complete("imp").Contains("import"));
        Assert.Equal("move,x", string.Join(",", k.Complete("cart.")));
        Assert.Equal("move", string.Join(",", k.Complete("cart.mo")));
        Assert.True(k.Complete("cart._h").Contains("_hidden"));
        Assert.True(!k.Complete("cart.").Contains("_hidden"), "private names wait for an underscore");
        Assert.True(k.Complete("math.sq").Contains("sqrt"));
        // nothing is called to find the words: a call before the dot is no name, and a property that raises only has no words
        Assert.Equal(0, k.Complete("boom().").Count);
        Assert.Equal(0, k.Complete("trap.bad.").Count);
        Assert.True(k.Complete("trap.b").Contains("bad"));
        Assert.Equal(0, k.Complete("nothing.").Count);
        // and boom() was not run by asking
        var printed = string.Join("", k.Run("print(counter)").Select(e => e.ToJsonString()));
        Assert.Contains("0", printed);
    }

    [Fact]
    public void ObjectsOfTheVariablesWindowAreOpenedOneLevelAtATime()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var got = k.Run("class Point:\n    def __init__(self):\n        self.x = 3\n        self.tags = ['a', 'b']\n        self._hidden = None\n\n\n"
            + "p = Point()\nd = {'one': [1, 2], 'two': {'k': 'v'}, 'n': 7}\nitems = [10, [20, 30]]\nempty = []\nname = 'text'\n");
        var vars = Of(got, "variables")!["vars"]!.AsArray();
        bool Expandable(string name) => vars.First(v => v!["name"]!.GetValue<string>() == name)!["expandable"]!.GetValue<bool>();
        Assert.True(Expandable("p") && Expandable("d") && Expandable("items"));
        Assert.True(!Expandable("empty") && !Expandable("name"), "an empty list and a string have nothing to open");

        var dict = Members(k.Inspect("[[\"v\",\"d\"]]"));
        Assert.Equal(3, dict.Count);
        Assert.Equal("'one'", dict[0].Name);
        Assert.Equal("list", dict[0].Type);
        Assert.True(dict[0].Expandable && dict[1].Expandable && !dict[2].Expandable);
        // a member is reached again by the key it was listed with, one level further down
        var inner = Members(k.Inspect("[[\"v\",\"d\"],[\"i\",1]]"));
        Assert.Equal("'k'", inner[0].Name);
        Assert.Equal("'v'", inner[0].Value);

        var obj = Members(k.Inspect("[[\"v\",\"p\"]]"));
        Assert.Equal("x,tags,_hidden", string.Join(",", obj.Select(m => m.Name)));
        Assert.Equal("3", obj[0].Value);
        var tags = Members(k.Inspect("[[\"v\",\"p\"],[\"a\",\"tags\"]]"));
        Assert.Equal("'a','b'", string.Join(",", tags.Select(m => m.Value)));

        var items = Members(k.Inspect("[[\"v\",\"items\"]]"));
        Assert.Equal("[20, 30]", items[1].Value);
        Assert.Equal("30", Members(k.Inspect("[[\"v\",\"items\"],[\"i\",1]]"))[1].Value);
        var gone = k.Inspect("[[\"v\",\"nope\"]]");
        Assert.True(gone["error"] != null, "a name that is gone is an error, not a crash");
    }

    [Fact]
    public void ATableIsOpenedByItsColumnsAndShowsItsShapeAsSize()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        // stand-ins found by what they have (columns, dtypes, shape): pandas is not needed here
        var got = k.Run("class Col:\n    shape = (3,)\n    dtype = 'int64'\n    def __init__(self, v): self.v = v\n    def __len__(self): return 3\n"
            + "    def __getitem__(self, i): return self.v[i]\n    def __repr__(self): return 'Col(%r)' % (self.v,)\n\n\n"
            + "class Frame:\n    shape = (3, 2)\n    dtypes = {}\n    columns = ['a', 'b']\n    def __getitem__(self, c): return Col({'a': [1, 2, 3], 'b': [4, 5, 6]}[c])\n    def __repr__(self): return 'Frame'\n\n\n"
            + "df = Frame()\n");
        var row = Of(got, "variables")!["vars"]!.AsArray().First(v => v!["name"]!.GetValue<string>() == "df")!;
        Assert.Equal("3x2", row["size"]!.GetValue<string>());
        Assert.True(row["expandable"]!.GetValue<bool>());
        var columns = Members(k.Inspect("[[\"v\",\"df\"]]"));
        Assert.Equal("a,b", string.Join(",", columns.Select(c => c.Name)));
        Assert.Equal("Col([1, 2, 3])", columns[0].Value);
        var cells = Members(k.Inspect("[[\"v\",\"df\"],[\"c\",1]]"));
        Assert.Equal("4,5,6", string.Join(",", cells.Select(c => c.Value)));
    }

    [Fact]
    public void ACellRunsUnderTheDebuggerAndStopsAtTheBreakpointOfItsLine()
    {
        if (!HavePython) return;
        using var k = new Kernel();
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        string file = ".pymagic/notebooks/nb.cell1.py";
        session.Listen(k.Root, "127.0.0.1", 0, "", Array.Empty<(string, string)>(), new Dictionary<string, JsonArray> { [file] = new JsonArray(2) });
        var seen = new List<string>();
        JsonNode Next(Func<JsonNode, bool> match)
        {
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(30))) throw new AssertionException("timed out waiting for the debugger; the debugger said: " + string.Join(" | ", seen) + "; the kernel said: " + string.Join(" | ", k.Events.ToArray().Select(x => x.ToJsonString())));
                seen.Add(e.ToJsonString());
                if (match(e)) return e;
            }
        }
        var listening = Next(e => e["type"]!.GetValue<string>() == "listening");
        k.Run("seed = 100");
        var debug = new JsonObject { ["host"] = "127.0.0.1", ["port"] = listening["port"]!.GetValue<int>(), ["token"] = listening["token"]!.GetValue<string>(), ["file"] = file };
        k.Inner.Execute("dbg1", "x = 5\ny = x * 2 + seed\nz = y + 1\nprint('z', z)\n", debug);
        var stopped = Next(e => e["type"]!.GetValue<string>() == "stopped");
        var top = stopped["frames"]![0]!;
        Assert.Equal(2, top["line"]!.GetValue<int>());
        Assert.True(top["file"]!.GetValue<string>().Replace('\\', '/').EndsWith(file), "the stop is in the file that stands for the cell");
        session.Handle(new JsonObject { ["type"] = "evaluate", ["id"] = 1, ["frameId"] = 0, ["expression"] = "x + seed" });
        Assert.Equal("105", Next(e => e["type"]!.GetValue<string>() == "evaluated")["result"]!.GetValue<string>());
        session.Handle(new JsonObject { ["type"] = "next" });
        Assert.Equal(3, Next(e => e["type"]!.GetValue<string>() == "stopped")["frames"]![0]!["line"]!.GetValue<int>());
        session.Handle(new JsonObject { ["type"] = "continue" });
        var done = new List<JsonNode>();
        k.Wait(e => { if (e["id"]?.GetValue<string>() == "dbg1") done.Add(e); return e["type"]!.GetValue<string>() == "done" && e["id"]?.GetValue<string>() == "dbg1"; });
        Assert.Equal("z 111\n", Text(done, "stdout"));
        Next(e => e["type"]!.GetValue<string>() == "disconnected");
        // the namespace is the notebook's own, and the next cell runs without the debugger
        var after = k.Run("y");
        Assert.Equal("110", Of(after, "result")!["data"]!["text/plain"]!.GetValue<string>());
    }
}
