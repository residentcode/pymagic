using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Moves a definition between real files and runs the program before and after (needs python).</summary>
public class MoveBehaviourTests
{
    [Fact]
    public void AMovedFunctionAndClassKeepTheOutput()
    {
        if (RefactorBehaviourTests.Python == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_move_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var files = new Dictionary<string, string>
            {
                ["a.py"] = "import math\nfrom dataclasses import dataclass\n\nLIMIT = 3\n\n\n@dataclass\nclass Box:\n    side: float = 2.0\n\n    def area(self):\n        return min(self.side ** 2, LIMIT * math.pi)\n\n\ndef describe(b):\n    return f'{b.area():.2f}'\n",
                ["b.py"] = "VALUE = 1\n",
                ["main.py"] = "from a import Box, describe\n\nprint(describe(Box()))\nprint(describe(Box(1.0)))\n",
            };
            foreach (var (n, t) in files) File.WriteAllText(Path.Combine(dir, n), t);
            string before = Run(dir);

            var svc = new LanguageService();
            svc.SourceRoots.Add(dir);
            var uris = files.Keys.ToDictionary(n => n, n => new Uri(Path.Combine(dir, n)).AbsoluteUri);
            foreach (var (n, t) in files) svc.Open(uris[n], t, 1);

            foreach (var symbol in new[] { "Box", "describe" })
            {
                string a = File.ReadAllText(Path.Combine(dir, "a.py"));
                var lm = new LineMap(a);
                int at = a.IndexOf((symbol == "Box" ? "class " : "def ") + symbol, StringComparison.Ordinal) + (symbol == "Box" ? 6 : 4);
                var edit = svc.MoveSymbol(uris["a.py"], lm.PositionOf(at), uris["b.py"]);
                foreach (var (uri, list) in edit.Changes)
                {
                    string path = new Uri(uri).LocalPath;
                    string text = File.ReadAllText(path);
                    var l = new LineMap(text);
                    foreach (var e in list.OrderByDescending(e => l.OffsetOf(e.Range.Start)).ThenByDescending(e => l.OffsetOf(e.Range.End)))
                        text = text[..l.OffsetOf(e.Range.Start)] + e.NewText + text[l.OffsetOf(e.Range.End)..];
                    File.WriteAllText(path, text);
                    svc.Open(uri, text, 2);
                }
                Assert.Equal(before, Run(dir));
            }
            Assert.True(!(File.ReadAllText(Path.Combine(dir, "a.py"))).Contains("class Box"), "unexpected: " + "class Box");
        }
        finally { Directory.Delete(dir, true); }
    }

    static string Run(string dir)
    {
        var psi = new ProcessStartInfo(RefactorBehaviourTests.Python!, "main.py") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.Environment["PYTHONPATH"] = dir;
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEndAsync();
        string output = p.StandardOutput.ReadToEnd();
        if (!p.WaitForExit(30000) || p.ExitCode != 0) throw new AssertionException("the program failed:\n" + err.Result);
        return output.Replace("\r\n", "\n");
    }
}
