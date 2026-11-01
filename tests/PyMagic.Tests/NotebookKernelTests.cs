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
        int _next = 1;

        public Kernel()
        {
            string root = Path.Combine(Path.GetTempPath(), "pm_nb_" + Guid.NewGuid().ToString("N"));
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
}
