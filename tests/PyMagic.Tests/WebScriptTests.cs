using System.ComponentModel;
using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PyMagic.Tests;

/// <summary>
/// Runs the checks of the page's own scripts: every <c>web/test/*.test.mjs</c>, with Node's test runner.
/// Skipped when Node is not installed (PYMAGIC_NODE, else <c>node</c> on the PATH).
/// </summary>
public class WebScriptTests
{
    static readonly string NodeExe = Environment.GetEnvironmentVariable("PYMAGIC_NODE") ?? "node";
    static readonly bool HasNode = NodeRuns();

    static bool NodeRuns()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo(NodeExe, "--version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true })!;
            p.StandardOutput.ReadToEnd();
            p.WaitForExit();
            return p.ExitCode == 0;
        }
        catch (Win32Exception) { return false; }
    }

    static string RepoDir(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, relative);
            if (Directory.Exists(candidate)) return candidate;
        }
        throw new AssertionException("not found: " + relative);
    }

    /// <summary>Runs one file and requires it to report at least one check and no failures.</summary>
    static void RunChecks(string file)
    {
        // the file is run directly, not through --test, which reads its arguments as glob patterns:
        // under a folder like "pymagic [old]" it would match nothing and pass with no checks run
        var psi = new ProcessStartInfo(NodeExe) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("--test-reporter=tap");
        psi.ArgumentList.Add(file);
        using var p = Process.Start(psi)!;
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        string name = Path.GetFileName(file);
        if (!p.WaitForExit(60_000)) { p.Kill(true); throw new AssertionException($"{name} did not finish in 60 s"); }
        string output = stdout.Result;
        var passed = Regex.Match(output, @"^# pass (\d+)", RegexOptions.Multiline);
        bool ok = p.ExitCode == 0 && passed.Success && int.Parse(passed.Groups[1].Value) > 0 && Regex.IsMatch(output, @"^# fail 0\b", RegexOptions.Multiline);
        Assert.True(ok, $"{name} failed (exit {p.ExitCode}):\n{output}\n{stderr.Result}");
    }

    [Fact]
    public void PageScripts()
    {
        if (!HasNode) return;
        var files = Directory.GetFiles(RepoDir("web/test"), "*.test.mjs").OrderBy(f => f, StringComparer.Ordinal).ToList();
        Assert.True(files.Count > 0, "no web/test/*.test.mjs files");
        foreach (var file in files) RunChecks(file);
    }
}
