using System.Collections;
using System.Text;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The real console of the Terminal panel (item 137): a Windows pseudo console. Skipped where there is none.</summary>
public class ConsoleSessionTests
{
    static Dictionary<string, string?> CurrentEnvironment()
    {
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables()) env[(string)e.Key] = (string?)e.Value;
        return env;
    }

    /// <summary>Runs a command line in a console and gives what it wrote and its exit code (or null after the timeout).</summary>
    static (string Output, int? Code) Run(string commandLine, Action<ConsoleSession>? afterStart = null, int seconds = 15)
    {
        using var console = new ConsoleSession();
        var output = new StringBuilder();
        int? code = null;
        using var done = new ManualResetEventSlim();
        console.Start(commandLine, Path.GetTempPath(), CurrentEnvironment(), 100, 30, text => { lock (output) output.Append(text); }, c => { code = c; done.Set(); });
        afterStart?.Invoke(console);
        done.Wait(TimeSpan.FromSeconds(seconds));
        lock (output) return (output.ToString(), code);
    }

    static string Plain(string text) => System.Text.RegularExpressions.Regex.Replace(text, @"\x1b\[[0-9;?]*[ -/]*[@-~]|\x1b\][^\x07]*\x07", "");

    [Fact]
    public void AProgramWritesToTheConsoleAndItsExitCodeComesBack()
    {
        if (!OperatingSystem.IsWindows()) return;
        var (output, code) = Run("cmd.exe /c echo hello from the console & exit 3");
        Assert.Contains("hello from the console", Plain(output));
        Assert.Equal(3, code);
    }

    [Fact]
    public void KeystrokesReachTheProgram()
    {
        if (!OperatingSystem.IsWindows()) return;
        // cmd's own prompt reads a line from the console; Enter is a carriage return as a terminal sends it
        var (output, code) = Run("cmd.exe /q /k prompt $G", c =>
        {
            Thread.Sleep(800);
            c.Write("echo typed in\r");
            Thread.Sleep(600);
            c.Write("exit 0\r");
        });
        Assert.Contains("typed in", Plain(output));
        Assert.Equal(0, code);
    }

    [Fact]
    public void ThePythonPromptIsInteractive()
    {
        if (!OperatingSystem.IsWindows()) return;
        string? python = Environment.GetEnvironmentVariable("PATH")?.Split(Path.PathSeparator).Select(d => Path.Combine(d, "python.exe")).FirstOrDefault(File.Exists);
        if (python == null) return;
        // with a console, python starts its prompt by itself (it would wait for a script without one)
        var (output, code) = Run($"\"{python}\" -q", c =>
        {
            Thread.Sleep(1500);
            c.Write("print(6 * 7)\r");
            Thread.Sleep(800);
            c.Write("exit()\r");
        }, 20);
        string plain = Plain(output);
        Assert.Contains(">>>", plain);
        Assert.Contains("42", plain);
        Assert.Equal(0, code);
    }

    [Fact]
    public void ResizingAndClosingAreSafe()
    {
        if (!OperatingSystem.IsWindows()) return;
        var console = new ConsoleSession();
        console.Start("cmd.exe /q /k", Path.GetTempPath(), CurrentEnvironment(), 80, 24, _ => { }, _ => { });
        console.Resize(120, 40);
        console.Resize(1, 1);
        console.Dispose();
        console.Dispose();
        console.Write("after close\r");
    }

    [Fact]
    public void UvInitTypedInTheTerminalMakesAnApplicationInEachShell()
    {
        if (!OperatingSystem.IsWindows()) return;
        // a uv that says what it was given, first on the PATH
        string bin = Path.Combine(Path.GetTempPath(), "pm_uvinit_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(bin);
        try
        {
            File.WriteAllText(Path.Combine(bin, "uv.cmd"), "@echo ARGS: %*\r\n");
            File.WriteAllText(Path.Combine(bin, "uv"), "#!/bin/sh\necho \"ARGS: $*\"\n");
            int tried = 0;
            foreach (var shell in ConsoleShells.Found().Where(s => s.Id is "powershell" or "pwsh" or "cmd" or "gitbash"))
            {
                var (line, shellEnv) = shell.WithVenv(null);
                var env = CurrentEnvironment();
                string pathKey = env.Keys.FirstOrDefault(k => k.Equals("PATH", StringComparison.OrdinalIgnoreCase)) ?? "PATH";
                env[pathKey] = bin + Path.PathSeparator + env.GetValueOrDefault(pathKey);
                foreach (var (k, v) in shellEnv) env[k] = v;
                using var console = new ConsoleSession();
                var output = new StringBuilder();
                using var done = new ManualResetEventSlim();
                console.Start(line, Path.GetTempPath(), env, 200, 30, text => { lock (output) output.Append(text); }, _ => done.Set());
                Thread.Sleep(shell.Id == "gitbash" ? 4000 : 2500);
                console.Write("uv init\r");
                Thread.Sleep(1500);
                console.Write("uv run app.py\r");
                Thread.Sleep(1500);
                console.Write("exit\r");
                done.Wait(TimeSpan.FromSeconds(15));
                string plain;
                lock (output) plain = Plain(output.ToString());
                Assert.True(plain.Contains("ARGS: init --no-package --no-readme --author-from none"), shell.Id + ": " + plain);
                Assert.True(plain.Contains("ARGS: run app.py"), shell.Id + ": other commands as typed: " + plain);
                tried++;
            }
            Assert.True(tried > 0, "no shell found");
        }
        finally { try { Directory.Delete(bin, true); } catch (IOException) { } }
    }

    [Fact]
    public void TheEnvironmentBlockIsSortedAndEndsWithTwoNuls()
    {
        string block = ConsoleSession.EnvironmentBlock(new Dictionary<string, string?> { ["b"] = "2", ["A"] = "1", ["skip"] = null, ["=C:"] = "x" });
        Assert.Equal("A=1\0b=2\0\0", block);
    }

    [Fact]
    public void TheInterpreterComesFirstOnThePath()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_console_venv_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "Scripts"));
        File.WriteAllText(Path.Combine(dir, "pyvenv.cfg"), "home = x\n");
        string python = Path.Combine(dir, "Scripts", "python.exe");
        File.WriteAllText(python, "");
        var env = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase) { ["Path"] = "C:\\Windows", ["PYTHONHOME"] = "x" };
        TerminalSession.WithInterpreter(env, python);
        Assert.Equal(Path.Combine(dir, "Scripts") + Path.PathSeparator + "C:\\Windows", env["Path"]);
        Assert.Equal(dir, env["VIRTUAL_ENV"]);
        Assert.True(!env.ContainsKey("PYTHONHOME"));
    }
}
