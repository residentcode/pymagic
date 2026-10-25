using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A real SSH server to test against: set PYMAGIC_SSH_TEST to "user@host:port|keyfile|knownhostsfile" (a container running sshd and Python
/// will do; the key must be allowed, the user's home writable). Without it the tests that need a server pass without running.
/// </summary>
static class SshTestServer
{
    public static SshTarget? Make()
    {
        string? setting = Environment.GetEnvironmentVariable("PYMAGIC_SSH_TEST");
        if (string.IsNullOrWhiteSpace(setting)) return null;
        var parts = setting.Split('|');
        if (parts.Length < 2 || !SshTarget.Installed()) return null;
        var head = SshTarget.FromHead(parts[0] + "?i=" + parts[1] + (parts.Length > 2 ? "&kh=" + parts[2] : ""));
        return head;
    }
}

public class SshTargetTests
{
    // ------------------------------------------------------------ names and quoting (no server needed)

    [Fact]
    public void ASpecNamesTheMachineTheKeyAndTheInterpreter()
    {
        var parsed = ExecutionTarget.Parse(@"ssh:dev@build.example.org:2222?i=C:\Users\me\.ssh\id_ed25519&kh=C:\k\known|/usr/bin/python3");
        Assert.True(parsed != null && parsed.Value.Target is SshTarget, "not an ssh spec");
        var t = (SshTarget)parsed!.Value.Target;
        Assert.Equal("build.example.org", t.Host);
        Assert.Equal("dev", t.User);
        Assert.Equal(2222, t.Port);
        Assert.Equal(@"C:\Users\me\.ssh\id_ed25519", t.Identity);
        Assert.Equal(@"C:\k\known", t.KnownHosts);
        Assert.Equal("/usr/bin/python3", parsed.Value.Python);
        Assert.Equal(@"ssh:dev@build.example.org:2222?i=C:\Users\me\.ssh\id_ed25519&kh=C:\k\known|/usr/bin/python3", t.Spec("/usr/bin/python3"));
        Assert.Equal("SSH: dev@build.example.org:2222", t.Name);
        Assert.Equal("ssh:host|python", new SshTarget("host").Spec("python"));
    }

    [Fact]
    public void ANameThatCouldBeAnOptionOrACommandIsRefused()
    {
        foreach (var bad in new[] { "ssh:-oProxyCommand=calc|python", "ssh:-F/etc/passwd|python", "ssh:host;rm|python", "ssh:host name|python", "ssh:@host|python", "ssh:host:0|python", "ssh:host:99999|python", "ssh:host?i=-oX|python", "ssh:|python" })
            Assert.True(ExecutionTarget.Parse(bad) == null, bad);
        Assert.True(Throws(() => new SshTarget("-oProxyCommand=x")));
        Assert.True(Throws(() => new SshTarget("host", "-l")));
        Assert.True(Throws(() => new SshTarget("host", null, 22, "-oFoo")));
    }

    static bool Throws(Action a) { try { a(); return false; } catch (ServiceException) { return true; } }

    [Fact]
    public void QuotingMakesAnyTextOneWordForTheRemoteShell()
    {
        Assert.Equal("'plain'", SshTarget.Quote("plain"));
        Assert.Equal("'it'\\''s'", SshTarget.Quote("it's"));
        Assert.Equal("'a b \"c\" $(x) ; `y` \\n'", SshTarget.Quote("a b \"c\" $(x) ; `y` \\n"));
        Assert.Equal("''", SshTarget.Quote(""));
    }

    [Fact]
    public void ACommandIsOneExecOfTheInterpreterWithQuotedArguments()
    {
        var t = new SshTarget("h", "u", 2200, "key file", "known") { Home = "/home/u" };
        var psi = t.Start("python3", Path.GetTempPath(), new[] { "-u", "-c", "print('hi')", "a b" }, new Dictionary<string, string> { ["K"] = "v w" });
        Assert.Equal("ssh", psi.FileName);
        var a = psi.ArgumentList.ToList();
        Assert.True(a.Contains("-T") && a.Contains("BatchMode=yes") && a.Contains("StrictHostKeyChecking=accept-new"));
        Assert.Equal("2200", a[a.IndexOf("-p") + 1]);
        Assert.Equal("key file", a[a.IndexOf("-i") + 1]);
        Assert.Equal("UserKnownHostsFile=known", a[a.IndexOf("UserKnownHostsFile=known")]);
        Assert.Equal("u@h", a[^2]);
        string command = a[^1];
        Assert.Contains("exec env 'K=v w' 'python3' -X 'pymagic=", command);
        Assert.True(command.EndsWith("'-u' '-c' 'print('\\''hi'\\'')' 'a b'"), command);
        Assert.True(!a.Contains("-R"), "no tunnel without a helper port");
        Assert.True(psi.Environment.ContainsKey("PYMAGIC_RUN_ID"));
    }

    [Fact]
    public void AHelperPortGetsAReverseTunnel()
    {
        var t = new SshTarget("h") { Home = "/home/h" };
        var psi = t.Start("python3", Path.GetTempPath(), new[] { "-u", "helper.py", "--port", "50123", "--token", "x" });
        var a = psi.ArgumentList.ToList();
        Assert.Equal("50123:127.0.0.1:50123", a[a.IndexOf("-R") + 1]);
        Assert.True(a.Contains("ExitOnForwardFailure=yes"));
    }

    // ------------------------------------------------------------ a real server

    static SshTarget? Server => SshTestServer.Make();

    [Fact]
    public void TextWithEveryAwkwardCharacterComesBackExactly()
    {
        var ssh = Server;
        if (ssh == null) return;
        string nasty = "it's \"quoted\" $HOME `id` ; | & > < * ? \\ ü 日本";
        var psi = ssh.Start("python3", Path.GetTempPath(), new[] { "-c", "import sys; print(sys.argv[1])", nasty }, new Dictionary<string, string>());
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.StandardOutputEncoding = new System.Text.UTF8Encoding(false);
        using var p = Process.Start(psi)!;
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000));
        Assert.Equal(nasty, output.TrimEnd('\r', '\n'));
    }

    [Fact]
    public void OnlyChangedFilesAreCopiedAndDeletedOnesRemoved()
    {
        var ssh = Server;
        if (ssh == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_sync_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        Directory.CreateDirectory(Path.Combine(root, ".git"));
        Directory.CreateDirectory(Path.Combine(root, "dir with space"));
        File.WriteAllText(Path.Combine(root, "main.py"), "print('v1')\n");
        File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "X = 1\n");
        File.WriteAllText(Path.Combine(root, "dir with space", "ü ñ.txt"), "odd name\n");
        File.WriteAllText(Path.Combine(root, ".git", "config"), "never copied\n");
        ssh.Project = root;
        var messages = new List<string>();
        ssh.Prepare(root, null, messages.Add);
        string remote = ssh.ProjectDir;
        string Cat(string rel) => ssh.Remote($"cat {SshTarget.Quote(remote + "/" + rel)} 2>&1").Out;
        Assert.Equal("print('v1')\n", Cat("main.py"));
        Assert.Equal("odd name\n", Cat("dir with space/ü ñ.txt"));
        Assert.True(ssh.Remote($"test -e {SshTarget.Quote(remote + "/.git/config")}").Code != 0, ".git was copied");
        Assert.True(messages.Count == 1 && messages[0].Contains("3 files"), string.Join("|", messages));

        // nothing changed: nothing is sent
        messages.Clear();
        ssh.Prepare(root, null, messages.Add);
        Assert.Equal(0, messages.Count);

        // one file changed, one removed, one added
        File.WriteAllText(Path.Combine(root, "main.py"), "print('v2 is longer')\n");
        File.Delete(Path.Combine(root, "pkg", "__init__.py"));
        File.WriteAllText(Path.Combine(root, "new.py"), "n = 1\n");
        ssh.Prepare(root, null, messages.Add);
        Assert.True(messages.Count == 1 && messages[0].Contains("2 files"), string.Join("|", messages));
        Assert.Equal("print('v2 is longer')\n", Cat("main.py"));
        Assert.Equal("n = 1\n", Cat("new.py"));
        Assert.True(ssh.Remote($"test -e {SshTarget.Quote(remote + "/pkg/__init__.py")}").Code != 0, "a deleted file is still there");

        // the remote folder is wiped: everything is sent again
        ssh.Remote($"rm -rf {SshTarget.Quote(remote)}");
        messages.Clear();
        ssh.Prepare(root, null, messages.Add);
        Assert.True(messages.Count == 1, string.Join("|", messages));
        Assert.Equal("n = 1\n", Cat("new.py"));
    }

    [Fact]
    public void StoppingAProgramStopsItOnTheOtherMachineToo()
    {
        var ssh = Server;
        if (ssh == null) return;
        string root = Path.Combine(Path.GetTempPath(), "pm_stop_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "loop.py"), "import time\nprint('up', flush=True)\nwhile True:\n    time.sleep(1)\n");
        ssh.Project = root;
        var events = new System.Collections.Concurrent.BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add) { Python = "python", Target = ssh };
        session.Start(root, "loop.py", false, Array.Empty<string>());
        while (true)
        {
            Assert.True(events.TryTake(out var e, TimeSpan.FromSeconds(90)), "no output");
            if (e!["text"]?.GetValue<string>().Contains("up") == true) break;
        }
        Assert.True(ssh.Remote("pgrep -f 'pymagic[=]'").Code == 0, "the program is not running there");
        session.Handle(new JsonObject { ["type"] = "stop" });
        for (int i = 0; i < 40 && ssh.Remote("pgrep -f 'loop[.]py'").Code == 0; i++) Thread.Sleep(500);
        Assert.True(ssh.Remote("pgrep -f 'loop[.]py'").Code != 0, "the program went on running on the other machine");
    }

    [Fact]
    public void AMachineThatCannotBeReachedIsAnErrorNotAHang()
    {
        if (!SshTarget.Installed()) return;
        var t = new SshTarget("127.0.0.1", "nobody", 1);   // nothing listens on port 1
        var ex = Record(() => t.Remote("true", 30000));
        Assert.True(ex != null && ex.Message.Contains("cannot connect"), ex?.Message);
    }

    static ServiceException? Record(Action a) { try { a(); } catch (ServiceException e) { return e; } return null; }
}
