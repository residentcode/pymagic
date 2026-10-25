using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Interpreters in WSL and Docker: how they are named, how a process is started there, how paths map; and a real WSL run.</summary>
public class ExecutionTargetTests
{
    static string Args(ProcessStartInfo psi) => string.Join(" ", psi.ArgumentList);

    // ------------------------------------------------------------ names

    [Fact]
    public void ASpecNamesTheTargetAndTheInterpreter()
    {
        var wsl = ExecutionTarget.Parse("wsl:Ubuntu-24.04|/usr/bin/python3");
        Assert.True(wsl != null && wsl.Value.Target is WslTarget { Distro: "Ubuntu-24.04" } && wsl.Value.Python == "/usr/bin/python3");
        var docker = ExecutionTarget.Parse("docker:python:3.12-slim|python");
        Assert.True(docker != null && docker.Value.Target is DockerTarget { Image: "python:3.12-slim" } && docker.Value.Python == "python");
        Assert.Equal("wsl:Ubuntu-24.04|/usr/bin/python3", new WslTarget("Ubuntu-24.04").Spec("/usr/bin/python3"));
        Assert.Equal("docker:python:3.12-slim|python", new DockerTarget("python:3.12-slim").Spec("python"));
    }

    [Fact]
    public void OrdinaryPathsAreNotSpecs()
    {
        Assert.True(ExecutionTarget.Parse(@"C:\Python313\python.exe") == null);
        Assert.True(ExecutionTarget.Parse("/usr/bin/python3") == null);
        Assert.True(ExecutionTarget.Parse("wsl:|python") == null);
        Assert.True(ExecutionTarget.Parse("wsl:Ubuntu|") == null);
        Assert.True(!ExecutionTarget.IsSpec(@"C:\x\python.exe"));
    }

    // ------------------------------------------------------------ WSL

    [Fact]
    public void WslPathsMapBothWays()
    {
        var w = new WslTarget("Ubuntu-24.04");
        Assert.Equal("/mnt/c/Users/me/proj/a.py", w.ToTarget(@"C:\Users\me\proj\a.py"));
        Assert.Equal("/mnt/d", w.ToTarget(@"D:\"));
        Assert.Equal("/home/me/x", w.ToTarget(@"\\wsl.localhost\Ubuntu-24.04\home\me\x"));
        Assert.Equal("/home/me/x", w.ToTarget(@"\\wsl$\ubuntu-24.04\home\me\x"));
        Assert.Equal("already/relative", w.ToTarget("already/relative"));
        Assert.Equal(@"C:\Users\me\proj\a.py", w.ToHost("/mnt/c/Users/me/proj/a.py"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\usr\lib\python3", w.ToHost("/usr/lib/python3"));
        Assert.Equal(@"\\wsl.localhost\Ubuntu-24.04\usr\lib\python3", w.Readable("/usr/lib/python3"));
    }

    [Fact]
    public void WslStartsAProgramWithoutAShell()
    {
        var w = new WslTarget("Ubuntu-24.04");
        var psi = w.Start("/usr/bin/python3", @"C:\proj", new[] { "-u", @"C:\Temp\helper.py", "--port", "5", "plain arg" },
            new Dictionary<string, string> { ["PYTHONUNBUFFERED"] = "1", ["PYTHONPATH"] = @"C:\proj" });
        Assert.Equal("wsl.exe", psi.FileName);
        Assert.Equal("-d Ubuntu-24.04 --cd /mnt/c/proj --exec env PYTHONUNBUFFERED=1 PYTHONPATH=/mnt/c/proj /usr/bin/python3 -u /mnt/c/Temp/helper.py --port 5 plain arg", Args(psi));
    }

    [Fact]
    public void ALocalInterpreterIsStartedDirectly()
    {
        var psi = ExecutionTarget.Local.Start(@"C:\Python\python.exe", @"C:\proj", new[] { "-u", "a.py" }, new Dictionary<string, string> { ["X"] = "1" });
        Assert.Equal(@"C:\Python\python.exe", psi.FileName);
        Assert.Equal("-u a.py", Args(psi));
        Assert.Equal("1", psi.Environment["X"]);
        Assert.Equal("127.0.0.1", ExecutionTarget.Local.HelperAddress().Connect);
    }

    // ------------------------------------------------------------ Docker

    [Fact]
    public void DockerSharesTheProjectAndTheHelperFolders()
    {
        var d = new DockerTarget("python:3.12-slim");
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        var psi = d.Start("python", root, new[] { "-u", Path.Combine(helpers, "pm_kernel.py"), "--root", root, "--port", "9" },
            new Dictionary<string, string> { ["PYTHONIOENCODING"] = "utf-8" }, new[] { helpers });
        Assert.Equal("docker", psi.FileName);
        string line = Args(psi);
        Assert.True(line.StartsWith("run --rm -i --add-host host.docker.internal:host-gateway -v "), line);
        Assert.Contains($"-v {Path.GetFullPath(root)}:/workspace -w /workspace", line);
        Assert.Contains($"-v {Path.GetFullPath(helpers)}:/pymagic/0:ro", line);
        Assert.Contains("-e PYTHONIOENCODING=utf-8", line);
        Assert.True(line.EndsWith("python:3.12-slim python -u /pymagic/0/pm_kernel.py --root /workspace --port 9"), line);
    }

    [Fact]
    public void DockerPathsMapBothWays()
    {
        var d = new DockerTarget("img");
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        d.Start("python", root, new string[0], null, new[] { helpers });
        Assert.Equal("/workspace/src/a.py", d.ToTarget(Path.Combine(root, "src", "a.py")));
        Assert.Equal("/workspace", d.ToTarget(root));
        Assert.Equal("/pymagic/0/x.py", d.ToTarget(Path.Combine(helpers, "x.py")));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "src", "a.py"), d.ToHost("/workspace/src/a.py"));
        Assert.Equal(Path.Combine(Path.GetFullPath(helpers), "x.py"), d.ToHost("/pymagic/0/x.py"));
        Assert.Equal("/usr/lib/x", d.ToHost("/usr/lib/x"));       // not shared: unchanged
        Assert.Equal("host.docker.internal", d.HelperAddress().Connect);
    }

    // ------------------------------------------------------------ a real WSL distribution

    static readonly string? Distro = FindDistro();

    static string? FindDistro()
    {
        try
        {
            foreach (var name in WslTarget.Distros())
            {
                var psi = new WslTarget(name).Start("python3", Path.GetTempPath(), new[] { "--version" });
                psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
                using var p = Process.Start(psi);
                if (p != null && p.WaitForExit(60000) && p.ExitCode == 0) return name;
            }
        }
        catch { }
        return null;
    }

    [Fact]
    public void AScriptRunsInWslWithItsArgumentsEnvironmentAndExitCode()
    {
        if (Distro == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_wsl_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string script = Path.Combine(dir, "t.py");
        File.WriteAllText(script, "import os, sys\nprint('cwd', os.getcwd())\nprint('args', sys.argv[1:])\nprint('env', os.environ.get('PM_X'))\nprint('line', sys.stdin.readline().strip())\nprint('os', sys.platform)\nsys.exit(5)\n");
        var w = new WslTarget(Distro);
        var psi = w.Start("python3", dir, new[] { "-u", script, "a b", "c" }, new Dictionary<string, string> { ["PM_X"] = "val" });
        psi.RedirectStandardInput = true; psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
        using var p = Process.Start(psi)!;
        p.StandardInput.WriteLine("typed");
        p.StandardInput.Close();
        string output = p.StandardOutput.ReadToEnd();
        Assert.True(p.WaitForExit(60000));
        Assert.Equal(5, p.ExitCode);
        Assert.Contains("cwd " + w.ToTarget(dir), output);
        Assert.Contains("args ['a b', 'c']", output);
        Assert.Contains("env val", output);
        Assert.Contains("typed", output);
        Assert.Contains("os linux", output);
    }

    [Fact]
    public void AnInterpreterInWslIsInspectedAndItsLibrariesCanBeRead()
    {
        if (Distro == null) return;
        var r = InterpreterInspector.Inspect(new WslTarget(Distro).Spec("python3"));
        Assert.True(r.Interpreter != null, r.Error);
        var i = r.Interpreter!;
        Assert.Equal("linux", i.Platform);
        Assert.Equal("wsl", i.Kind);
        Assert.Equal(3, i.Major);
        Assert.Equal("python3", i.Python);
        Assert.Contains("WSL: " + Distro, i.Label);
        Assert.True(i.Target is WslTarget);
        Assert.True(i.SysPath.Any(p => p.StartsWith(@"\\wsl.localhost\") && Directory.Exists(p)), "no library folder can be read from here: " + string.Join(", ", i.SysPath));
        Assert.True(i.Stdlib != null && Directory.Exists(i.Stdlib), "the standard library cannot be read from here: " + i.Stdlib);
        Assert.True(i.StdlibModules.Contains("os"));
    }

    [Fact]
    public void AnInterpreterThatDoesNotExistIsAnErrorNotACrash()
    {
        if (Distro == null) return;
        var r = InterpreterInspector.Inspect(new WslTarget(Distro).Spec("/no/such/python"));
        Assert.True(r.Interpreter == null && !string.IsNullOrEmpty(r.Error));
    }
}
