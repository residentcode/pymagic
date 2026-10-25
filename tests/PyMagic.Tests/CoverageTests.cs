using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A test run that records which lines of the project ran.</summary>
public class CoverageTests
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

    static JsonObject RunWithCoverage(string root)
    {
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        session.StartTests(root, new List<string>(), "unittest", coverage: true);
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(90))) throw new AssertionException("timed out");
            if (e["type"]!.GetValue<string>() is "exited" or "error") break;
        }
        string path = RunSession.CoveragePath(root);
        Assert.True(File.Exists(path), "no coverage file");
        return (JsonObject)JsonNode.Parse(File.ReadAllText(path))!;
    }

    static string Project(Dictionary<string, string> files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_cov_" + Guid.NewGuid().ToString("N"));
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text.Replace("\r\n", "\n"));
        }
        return root;
    }

    static int[] Lines(JsonObject data, string file, string kind) =>
        data["files"]![file]![kind]!.AsArray().Select(n => n!.GetValue<int>()).ToArray();

    const string Calc =
        "\"\"\"Calculator.\"\"\"\n" +                       // 1
        "\n" +                                             // 2
        "\n" +                                             // 3
        "def sign(x):\n" +                                 // 4
        "    \"\"\"Docstring.\"\"\"\n" +                   // 5
        "    if x > 0:\n" +                                // 6
        "        return 1\n" +                             // 7
        "    if x < 0:\n" +                                // 8
        "        return -1\n" +                            // 9
        "    return 0\n" +                                 // 10
        "\n" +                                             // 11
        "\n" +                                             // 12
        "def never():\n" +                                 // 13
        "    return 99  # pragma: no cover\n";             // 14

    const string Tests =
        "import unittest\n" +
        "import calc\n" +
        "\n" +
        "\n" +
        "class T(unittest.TestCase):\n" +
        "    def test_positive(self):\n" +
        "        self.assertEqual(calc.sign(5), 1)\n" +
        "\n" +
        "    def test_zero(self):\n" +
        "        self.assertEqual(calc.sign(0), 0)\n";

    [Fact]
    public void TheLinesThatRanAndTheOnesThatDidNot()
    {
        if (!HavePython) return;
        string root = Project(new() { ["calc.py"] = Calc, ["test_calc.py"] = Tests, ["unused.py"] = "def f():\n    return 1\n\n\nx = f()\n" });
        try
        {
            var data = RunWithCoverage(root);
            Assert.Equal("4,6,7,8,9,10,13", string.Join(",", Lines(data, "calc.py", "executable")));
            // sign(5) and sign(0) ran: the negative branch (line 9) did not; `never` is defined (line 13) but not called
            Assert.Equal("4,6,7,8,10,13", string.Join(",", Lines(data, "calc.py", "executed")));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void FilesThatNothingImportedCountAsMissed()
    {
        if (!HavePython) return;
        string root = Project(new() { ["calc.py"] = Calc, ["test_calc.py"] = Tests, ["unused.py"] = "def f():\n    return 1\n\n\nx = f()\n" });
        try
        {
            var data = RunWithCoverage(root);
            Assert.Equal(0, Lines(data, "unused.py", "executed").Length);
            Assert.True(Lines(data, "unused.py", "executable").Length > 0);
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void FoldersOfOtherPeoplesCodeAreLeftOut()
    {
        if (!HavePython) return;
        string root = Project(new()
        {
            ["calc.py"] = Calc, ["test_calc.py"] = Tests,
            [".venv/lib/site.py"] = "x = 1\n", ["node_modules/a.py"] = "y = 1\n", ["pkg/__pycache__/c.py"] = "z = 1\n",
        });
        try
        {
            var data = RunWithCoverage(root);
            var names = data["files"]!.AsObject().Select(kv => kv.Key).ToList();
            Assert.True(names.Contains("calc.py") && names.Contains("test_calc.py"), string.Join(",", names));
            Assert.True(!names.Any(n => n.Contains(".venv") || n.Contains("node_modules") || n.Contains("__pycache__")), string.Join(",", names));
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }

    [Fact]
    public void TheOlderTraceHookGivesTheSameLines()
    {
        if (!HavePython) return;
        string root = Project(new() { ["calc.py"] = Calc, ["test_calc.py"] = Tests });
        Environment.SetEnvironmentVariable("PYMAGIC_COVERAGE_TRACE", "1");
        try
        {
            var data = RunWithCoverage(root);
            Assert.Equal("4,6,7,8,9,10,13", string.Join(",", Lines(data, "calc.py", "executable")));
            Assert.Equal("4,6,7,8,10,13", string.Join(",", Lines(data, "calc.py", "executed")));
        }
        finally
        {
            Environment.SetEnvironmentVariable("PYMAGIC_COVERAGE_TRACE", null);
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
