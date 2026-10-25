using System.Text;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The command line of the terminal panel: output, exit codes, folders, input and killing.</summary>
public class TerminalTests
{
    static bool Windows => OperatingSystem.IsWindows();

    static string TempFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic_term_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    sealed class Outcome
    {
        public readonly StringBuilder Out = new(), Err = new();
        public int Code = int.MinValue;
        public string Cwd = "";
        public readonly ManualResetEventSlim Done = new();
    }

    static Outcome Start(TerminalSession t, string line)
    {
        var o = new Outcome();
        t.Run(line, (stream, text) => { lock (o) (stream == "out" ? o.Out : o.Err).Append(text); }, (code, cwd) => { o.Code = code; o.Cwd = cwd; o.Done.Set(); });
        return o;
    }

    static Outcome Run(TerminalSession t, string line)
    {
        var o = Start(t, line);
        Assert.True(o.Done.Wait(TimeSpan.FromSeconds(30)), "the command did not finish");
        return o;
    }

    [Fact]
    public void OutputAndExitCode()
    {
        using var t = new TerminalSession(TempFolder(), () => null);
        var ok = Run(t, "echo hello");
        Assert.Equal(0, ok.Code);
        Assert.Contains("hello", ok.Out.ToString());
        var fail = Run(t, "exit 3");
        Assert.Equal(3, fail.Code);
    }

    [Fact]
    public void TheFolderIsKeptBetweenLines()
    {
        string root = TempFolder();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        using var t = new TerminalSession(root, () => null);
        var cd = Run(t, "cd sub");
        Assert.Equal(0, cd.Code);
        Assert.Equal(Path.Combine(root, "sub"), t.Cwd);
        Assert.Equal(Path.Combine(root, "sub"), cd.Cwd);
        Assert.Contains("sub", Run(t, "pwd").Out.ToString());
        Assert.Equal(0, Run(t, "cd ..").Code);
        Assert.Equal(root, t.Cwd);
        Assert.Equal(0, Run(t, "cd sub").Code);
        Assert.Equal(0, Run(t, "cd").Code);
        Assert.Equal(root, t.Cwd);
    }

    [Fact]
    public void ChangingToAFolderThatIsNotThere()
    {
        using var t = new TerminalSession(TempFolder(), () => null);
        var o = Run(t, "cd nowhere");
        Assert.Equal(1, o.Code);
        Assert.Contains("no such folder", o.Err.ToString());
    }

    [Fact]
    public void ALineIsSentToTheRunningCommand()
    {
        using var t = new TerminalSession(TempFolder(), () => null);
        string command = Windows ? "$x = [Console]::In.ReadLine(); echo \"got $x\"" : "read x; echo got $x";
        var o = Start(t, command);
        Thread.Sleep(Windows ? 1500 : 300);
        t.WriteLine("abc");
        Assert.True(o.Done.Wait(TimeSpan.FromSeconds(20)), "no answer");
        Assert.Contains("got abc", o.Out.ToString());
    }

    [Fact]
    public void ACommandCanBeKilled()
    {
        using var t = new TerminalSession(TempFolder(), () => null);
        var o = Start(t, Windows ? "Start-Sleep 60" : "sleep 60");
        Thread.Sleep(Windows ? 1500 : 300);
        Assert.True(t.IsRunning, "still running");
        t.Kill();
        Assert.True(o.Done.Wait(TimeSpan.FromSeconds(20)), "killed");
        Assert.True(o.Code != 0, "a killed command does not succeed");
        Assert.True(!t.IsRunning);
    }

    [Fact]
    public void TerminalsOnOneProjectAreIndependent()
    {
        // each terminal tab of the panel is a session of its own: its own folder, and its command holds up no other
        string root = TempFolder();
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        using var a = new TerminalSession(root, () => null);
        using var b = new TerminalSession(root, () => null);
        Assert.Equal(0, Run(a, "cd sub").Code);
        Assert.Equal(Path.Combine(root, "sub"), a.Cwd);
        Assert.Equal(root, b.Cwd);
        var slow = Start(a, Windows ? "Start-Sleep 60" : "sleep 60");
        Thread.Sleep(Windows ? 1500 : 300);
        var quick = Run(b, "echo still-here");
        Assert.Equal(0, quick.Code);
        Assert.Contains("still-here", quick.Out.ToString());
        Assert.Equal(root, quick.Cwd);
        Assert.True(a.IsRunning && !b.IsRunning, "only the first terminal is busy");
        a.Kill();
        Assert.True(slow.Done.Wait(TimeSpan.FromSeconds(20)), "killed");
    }

    [Fact]
    public void OnlyOneCommandAtATime()
    {
        using var t = new TerminalSession(TempFolder(), () => null);
        var o = Start(t, Windows ? "Start-Sleep 60" : "sleep 60");
        Thread.Sleep(Windows ? 1500 : 300);
        Assert.Throws<InvalidOperationException>(() => t.Run("echo two", (_, _) => { }, (_, _) => { }));
        t.Kill();
        o.Done.Wait(TimeSpan.FromSeconds(20));
    }

    [Fact]
    public void TheInterpreterComesFirstOnThePath()
    {
        string tools = TempFolder();
        string python = Path.Combine(tools, Windows ? "python.exe" : "python");
        File.WriteAllText(python, "");
        using var t = new TerminalSession(TempFolder(), () => python);
        var o = Run(t, Windows ? "$env:PATH" : "echo $PATH");
        Assert.True(o.Out.ToString().TrimStart().StartsWith(tools), o.Out.ToString());
    }

    [Fact]
    public void AVirtualEnvironmentIsActivated()
    {
        string env = TempFolder();
        Directory.CreateDirectory(Path.Combine(env, "Scripts"));
        File.WriteAllText(Path.Combine(env, "pyvenv.cfg"), "home = x\n");
        string python = Path.Combine(env, "Scripts", Windows ? "python.exe" : "python");
        File.WriteAllText(python, "");
        using var t = new TerminalSession(TempFolder(), () => python);
        var o = Run(t, Windows ? "$env:VIRTUAL_ENV" : "echo $VIRTUAL_ENV");
        Assert.Contains(env, o.Out.ToString());
    }
}
