using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Applies refactorings to real programs and checks that they print the same thing before and after (needs python on the PATH).</summary>
public class RefactorBehaviourTests
{
    const string Main = "file:///c:/proj/main.py";

    static readonly string? Python = Find();

    static string? Find()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            return p!.WaitForExit(20000) && p.ExitCode == 0 ? (Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python") : null;
        }
        catch { return null; }
    }

    static string RunPython(string code)
    {
        string path = Path.Combine(Path.GetTempPath(), "pm_beh_" + Guid.NewGuid().ToString("N") + ".py");
        File.WriteAllText(path, code);
        try
        {
            var psi = new ProcessStartInfo(Python!, "\"" + path + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            var err = p.StandardError.ReadToEndAsync();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(30000) || p.ExitCode != 0) throw new AssertionException("the program failed:\n" + err.Result + "\n" + code);
            return output.Replace("\r\n", "\n");
        }
        finally { File.Delete(path); }
    }

    /// <summary>Applies the refactoring to the marked program and returns the new text.</summary>
    static string Refactor(string marked, string kind, string? name)
    {
        int a = marked.IndexOf('⟦');
        string open = a >= 0 ? marked.Remove(a, 1) : marked;
        int b = a >= 0 ? open.IndexOf('⟧') : -1;
        string text = a >= 0 ? open.Remove(b, 1) : marked.Replace("|", "");
        int cursor = marked.IndexOf('|');
        var lm = new LineMap(text);
        var range = a >= 0 ? lm.RangeOf(a, b) : lm.RangeOf(cursor, cursor);

        var svc = new LanguageService();
        svc.Open(Main, text, 1);
        var edit = svc.Refactor(Main, kind, range, name);
        foreach (var e in edit.Changes[Main].OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
            text = text[..lm.OffsetOf(e.Range.Start)] + e.NewText + text[lm.OffsetOf(e.Range.End)..];
        return text;
    }

    static void Same(string marked, string kind, string? name)
    {
        if (Python == null) return;
        string before = marked.Replace("⟦", "").Replace("⟧", "").Replace("|", "");
        string after = Refactor(marked, kind, name);
        Assert.Equal(RunPython(before), RunPython(after));
    }

    [Fact]
    public void ExtractedStatementsBehaveTheSame()
    {
        Same(string.Join("\n",
            "def process(items, factor):",
            "    total = 0",
            "    ⟦for item in items:",
            "        total += item * factor",
            "    print('looked at', len(items))⟧",
            "    return total",
            "",
            "print(process([1, 2, 3], 10))",
            "print(process([], 5))"), "extractMethod", "tally");

        Same(string.Join("\n",
            "def f(a):",
            "    ⟦x = a + 1",
            "    y = x * 2⟧",
            "    return x + y",
            "",
            "print(f(3), f(10))"), "extractMethod", "compute");
    }

    [Fact]
    public void ExtractedMethodsKeepWorkingOnTheirObject()
    {
        Same(string.Join("\n",
            "class Cart:",
            "    def __init__(self):",
            "        self.items = [3, 4, 5]",
            "",
            "    def report(self, prefix):",
            "        ⟦lines = []",
            "        for item in self.items:",
            "            lines.append(prefix + str(item))⟧",
            "        return ','.join(lines)",
            "",
            "print(Cart().report('#'))"), "extractMethod", "collect");

        Same(string.Join("\n",
            "class K:",
            "    @staticmethod",
            "    def run(a):",
            "        return ⟦a * 2 + 1⟧",
            "",
            "print(K.run(5))"), "extractMethod", "twice");
    }

    [Fact]
    public void ExtractedCodeWithStringsAndFStringsBehavesTheSame()
    {
        Same(string.Join("\n",
            "def f(count, name):",
            "    ⟦text = '''first",
            "second",
            "    third'''",
            "    print(text, f'{name} has {count}')⟧",
            "    return count",
            "",
            "print(f(2, 'ann'))"), "extractMethod", "show");
    }

    [Fact]
    public void ExtractedVariablesAndConstantsBehaveTheSame()
    {
        Same(string.Join("\n",
            "def f(x):",
            "    a = ⟦x * 2⟧",
            "    b = x * 2 + 1",
            "    return a + b",
            "",
            "print(f(4))"), "extractVariable", "double");

        Same(string.Join("\n",
            "def area(r):",
            "    return ⟦3.5⟧ * r * r",
            "",
            "print(area(2))"), "extractConstant", "FACTOR");
    }

    [Fact]
    public void InlinedVariablesBehaveTheSame()
    {
        Same(string.Join("\n",
            "def f(a, b):",
            "    total| = a + b",
            "    return total * 2 - total",
            "",
            "print(f(3, 4))"), "inlineVariable", null);

        Same(string.Join("\n",
            "def load():",
            "    print('loading')",
            "    return [1, 2, 3]",
            "",
            "data| = load()",
            "print(sum(data))"), "inlineVariable", null);
    }
}
