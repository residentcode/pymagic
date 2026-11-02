using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A Docker container that is already running as the interpreter, and a terminal inside the project's interpreter target.</summary>
public class ContainerTargetTests
{
    static string Args(ProcessStartInfo psi) => string.Join(" ", psi.ArgumentList);

    [Fact]
    public void ASpecNamesTheContainerAndWhereItHasTheProject()
    {
        var plain = ExecutionTarget.Parse("docker:container=web|python");
        Assert.True(plain != null && plain.Value.Target is ContainerTarget { Container: "web", WorkDirInContainer: "/workspace" } && plain.Value.Python == "python");
        var full = ExecutionTarget.Parse("docker:container=my-app.1?w=/app|/usr/local/bin/python");
        var t = (ContainerTarget)full!.Value.Target;
        Assert.Equal(("my-app.1", "/app"), (t.Container, t.WorkDirInContainer));
        Assert.Equal("docker:container=my-app.1?w=/app|/usr/local/bin/python", t.Spec("/usr/local/bin/python"));
        Assert.Equal("docker:container=web|python", new ContainerTarget("web").Spec("python"));
        Assert.Equal("container", t.Kind);
        Assert.True(ExecutionTarget.IsSpec("docker:container=web|python"));
        Assert.True(ExecutionTarget.Parse("docker:container=|python") == null);
        Assert.True(ExecutionTarget.Parse("docker:container=-x|python") == null);
        Assert.True(ExecutionTarget.Parse("docker:container=a b|python") == null);
        Assert.True(ExecutionTarget.Parse("docker:container=web?w=app|python") == null);
        // an image is still an image
        Assert.True(ExecutionTarget.Parse("docker:python:3.12|python") is { Target: DockerTarget { Image: "python:3.12" } });
    }

    [Fact]
    public void ARunIsDockerExecInTheContainerAndStopKnowsItsProcess()
    {
        var t = new ContainerTarget("web", "/app");
        string root = Path.Combine(Path.GetTempPath(), "proj");
        t.Project = root;
        var psi = t.Start("python", Path.Combine(root, "src"), new[] { "-u", Path.Combine(root, "main.py") }, new Dictionary<string, string> { ["PYTHONPATH"] = root });
        Assert.Equal("docker", psi.FileName);
        string line = Args(psi);
        Assert.True(line.StartsWith("exec -i -w /app/src -e PYTHONPATH=/app web sh -c echo $$ > /tmp/.pymagic-", StringComparison.Ordinal), line);
        Assert.True(line.EndsWith("exec \"$@\" sh python -u /app/main.py", StringComparison.Ordinal), line);
        Assert.True(System.Text.RegularExpressions.Regex.IsMatch(psi.Environment["PYMAGIC_RUN_ID"]!, "^[0-9a-f]{12}$"));
    }

    [Fact]
    public void TheHelperScriptsAreCopiedIntoTheContainerOnceAndNotWhenItIsGone()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        var calls = new List<string>();
        string id = "abc123";
        string? Docker(string[] a)
        {
            string line = string.Join(" ", a);
            calls.Add(line);
            return line.StartsWith("inspect --format {{.Id}}", StringComparison.Ordinal) ? id + "\n" : line.StartsWith("inspect", StringComparison.Ordinal) ? "[]" : "";
        }
        var t = new ContainerTarget("web", "/app", Docker) { Project = root };
        var psi = t.Start("python", root, new[] { Path.Combine(helpers, "pm_kernel.py") }, null, new[] { helpers });
        Assert.True(Args(psi).EndsWith("exec \"$@\" sh python /pymagic/0/pm_kernel.py", StringComparison.Ordinal), Args(psi));
        Assert.Equal(string.Join(" | ", "inspect --format {{.Id}} web", "exec -u 0 web sh -c rm -rf /pymagic/0 && mkdir -p /pymagic", $"cp {Path.Combine(Path.GetFullPath(helpers), ".")} web:/pymagic/0"), string.Join(" | ", calls));
        // the same container: not copied again; a new one (another id): copied again
        t.Start("python", root, new[] { "x" }, null, new[] { helpers });
        Assert.Equal(1, calls.Count(c => c.StartsWith("cp ", StringComparison.Ordinal)));
        id = "def456";
        t.Start("python", root, new[] { "x" }, null, new[] { helpers });
        Assert.Equal(2, calls.Count(c => c.StartsWith("cp ", StringComparison.Ordinal)));
        // a container docker does not know, or one where nothing can be copied: said so
        Assert.Throws<ServiceException>(() => new ContainerTarget("gone", "/app", _ => null) { Project = root }.Start("python", root, new[] { "x" }, null, new[] { helpers }));
        Assert.Throws<ServiceException>(() => new ContainerTarget("web", "/app", a => a[0] == "inspect" ? "x1\n" : null) { Project = root }.Start("python", root, new[] { "x" }, null, new[] { helpers }));
    }

    [Fact]
    public void WhereTheContainerHasTheProjectIsReadFromItsMounts()
    {
        // the forms Docker Desktop writes a Windows folder in, the deepest mount above the project, and what is not a bind mount
        const string project = @"C:\Users\me\proj";
        Assert.Equal("/app", HelperCopies.MountPointIn("""[{"Type":"bind","Source":"C:\\Users\\me\\proj","Destination":"/app"}]""", project));
        Assert.Equal("/app", HelperCopies.MountPointIn("""[{"Type":"bind","Source":"/run/desktop/mnt/host/c/Users/me/proj","Destination":"/app/"}]""", project));
        Assert.Equal("/code/proj", HelperCopies.MountPointIn("""[{"Type":"bind","Source":"/host_mnt/c/Users/me","Destination":"/code"}]""", project));
        Assert.Equal("/deep", HelperCopies.MountPointIn("""[{"Type":"bind","Source":"/mnt/c/Users","Destination":"/top"},{"Type":"bind","Source":"c:/users/ME/proj","Destination":"/deep"}]""", project));
        Assert.True(HelperCopies.MountPointIn("""[{"Type":"volume","Source":"/var/lib/docker/volumes/x","Destination":"/data"},{"Type":"bind","Source":"C:\\Users\\me\\other","Destination":"/o"}]""", project) == null);
        Assert.True(HelperCopies.MountPointIn("""[{"Type":"bind","Source":"C:\\Users\\me\\proj2","Destination":"/p2"}]""", project) == null);   // a sibling with a longer name
        Assert.True(HelperCopies.MountPointIn("not json", project) == null);
        Assert.True(HelperCopies.MountPointIn("null", project) == null);
        // a Linux host's folders as they are
        Assert.Equal("/w/src", HelperCopies.MountPointIn("""[{"Type":"bind","Source":"/home/me/proj","Destination":"/w"}]""", "/home/me/proj/src"));

        // a target without w asks docker once the project is known, and keeps the answer; a spec does not get it
        int asked = 0;
        var t = new ContainerTarget("web", null, a => { asked++; return a[2] == "{{json .Mounts}}" ? """[{"Type":"bind","Source":"C:\\Users\\me\\proj","Destination":"/srv"}]""" : null; });
        t.Project = project;
        Assert.Equal("/srv/a.py", t.ToTarget(@"C:\Users\me\proj\a.py"));
        Assert.Equal(@"C:\Users\me\proj\a.py", t.ToHost("/srv/a.py"));
        Assert.Equal("/srv", t.WorkDirInContainer);
        Assert.Equal(1, asked);
        Assert.Equal("docker:container=web|python", t.Spec("python"));
        // nothing found: /workspace, as before
        Assert.Equal("/workspace", new ContainerTarget("web", null, _ => "[]") { Project = project }.WorkDirInContainer);
    }

    [Fact]
    public void PathsFollowWhereTheContainerHasTheProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj");
        var def = new ContainerTarget("web") { Project = root };
        Assert.Equal("/workspace/a/b.py", def.ToTarget(Path.Combine(root, "a", "b.py")));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a", "b.py"), def.ToHost("/workspace/a/b.py"));
        var app = new ContainerTarget("web", "/srv/app/") { Project = root };
        Assert.Equal("/srv/app/a/b.py", app.ToTarget(Path.Combine(root, "a", "b.py")));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "a", "b.py"), app.ToHost("/srv/app/a/b.py"));
        Assert.Equal(Path.GetFullPath(root), app.ToHost("/srv/app"));
        // a path in the container outside the project stays where it is
        Assert.Equal("/usr/lib/python3/x.py", app.ToHost("/usr/lib/python3/x.py"));
    }

    [Fact]
    public void EveryRemoteTargetHasATerminalInItAndThisComputerDoesNot()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj");
        Assert.True(ExecutionTarget.Local.ShellCommand(root) == null);

        var container = new ContainerTarget("web", "/app") { Project = root };
        var c = container.ShellCommand(Path.Combine(root, "src"))!.Value;
        Assert.Equal("docker exec -it -w /app/src web sh", c.Program + " " + string.Join(" ", c.Args));

        var wsl = new WslTarget("Ubuntu-24.04").ShellCommand(@"C:\Users\me\proj")!.Value;
        Assert.Equal("wsl.exe -d Ubuntu-24.04 --cd /mnt/c/Users/me/proj", wsl.Program + " " + string.Join(" ", wsl.Args));

        var docker = new DockerTarget("python:3.12-slim") { Project = root };
        var d = docker.ShellCommand(root)!.Value;
        string dline = d.Program + " " + string.Join(" ", d.Args);
        Assert.True(dline.StartsWith("docker run --rm -it ", StringComparison.Ordinal), dline);
        Assert.Contains($"-v {Path.GetFullPath(root)}:/workspace -w /workspace python:3.12-slim sh", dline);

        var run = new ComposeTarget("web", "dev.yml") { Project = root }.ShellCommand(root)!.Value;
        Assert.Equal($"docker compose -f dev.yml run --rm -v {Path.GetFullPath(root)}:/workspace -w /workspace web sh", run.Program + " " + string.Join(" ", run.Args));
        var exec = new ComposeTarget("web", null, true, "/app") { Project = root }.ShellCommand(Path.Combine(root, "x"))!.Value;
        Assert.Equal("docker compose exec -w /app/x web sh", exec.Program + " " + string.Join(" ", exec.Args));
    }

    [Fact]
    public void TheTerminalShellIsAWindowsCommandLineThatKeepsEachArgument()
    {
        Assert.Equal("docker exec -it -w /app web sh", ConsoleShells.CommandLineOf("docker", new[] { "exec", "-it", "-w", "/app", "web", "sh" }));
        Assert.Equal("ssh -t host \"cd 'a b' && exec ${SHELL:-sh} -l\"", ConsoleShells.CommandLineOf("ssh", new[] { "-t", "host", "cd 'a b' && exec ${SHELL:-sh} -l" }));
        Assert.Equal("p \"say \\\"hi\\\"\" \"\" \"C:\\a b\\\\\"", ConsoleShells.CommandLineOf("p", new[] { "say \"hi\"", "", @"C:\a b\" }));
        var shell = ConsoleShells.InTarget(new ContainerTarget("web") { Project = Path.GetTempPath() }, Path.GetTempPath())!;
        Assert.Equal((ConsoleShells.InterpreterId, "Docker container: web"), (shell.Id, shell.Name));
        Assert.True(ConsoleShells.InTarget(ExecutionTarget.Local, Path.GetTempPath()) == null);
    }

    static string? Docker(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Result : null;
    }

    /// <summary>With PYMAGIC_TEST_COMPOSE=1 and a running Docker engine (python:3.12-slim present): a script runs in a container that was started before, and Stop ends it.</summary>
    [Fact]
    public void AScriptRunsInARunningContainerOfARealEngine()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_COMPOSE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "pm_ctr_" + Guid.NewGuid().ToString("N"));
        string name = "pm_ctr_" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "hello.py"), "import os, sys\nprint('hi from', os.getcwd(), sys.argv[1:])\nprint(sys.stdin.readline().strip().upper())\n");
            File.WriteAllText(Path.Combine(root, "slow.py"), "import time\nprint('started', flush=True)\ntime.sleep(300)\n");
            Assert.True(Docker("run", "-d", "--name", name, "-v", $"{root}:/app", "python:3.12-slim", "sleep", "600") != null);

            var t = new ContainerTarget(name, "/app") { Project = root };
            var psi = t.Start("python", root, new[] { Path.Combine(root, "hello.py"), "one" });
            psi.RedirectStandardOutput = psi.RedirectStandardError = psi.RedirectStandardInput = true;
            psi.UseShellExecute = false;
            using (var p = Process.Start(psi)!)
            {
                p.StandardInput.WriteLine("abc");
                p.StandardInput.Close();
                string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
                p.WaitForExit();
                Assert.True(output.Contains("hi from /app ['one']") && output.Contains("ABC"), output + error);
            }

            // Stop ends the program inside the container, not only the local docker client
            var slow = t.Start("python", root, new[] { Path.Combine(root, "slow.py") });
            slow.RedirectStandardOutput = true;
            slow.UseShellExecute = false;
            using (var p = Process.Start(slow)!)
            {
                Assert.Contains("started", p.StandardOutput.ReadLine() ?? "");
                string Processes() => Docker("exec", name, "sh", "-c", "for d in /proc/[0-9]*; do tr '\\0' ' ' < $d/cmdline; echo; done") ?? "";
                Assert.Contains("slow.py", Processes());
                t.Stop(slow.Environment["PYMAGIC_RUN_ID"]!);
                Assert.True(p.WaitForExit(20000), "the program did not end");
                Assert.True(!Processes().Contains("slow.py"), "the program still runs in the container");
            }
        }
        finally
        {
            Docker("rm", "-f", name);
            try { Directory.Delete(root, true); } catch { }
        }
    }

    /// <summary>
    /// With PYMAGIC_TEST_COMPOSE=1 and a running Docker engine (python:3.12-slim present): a container started with the project mounted at
    /// /src/proj, used without saying where; the place is read from docker inspect, and a helper script is copied in and runs there.
    /// </summary>
    [Fact]
    public void ARealContainerSaysWhereItHasTheProjectAndTakesTheHelperScripts()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_COMPOSE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "pm_ctr_" + Guid.NewGuid().ToString("N"));
        string helpers = Path.Combine(Path.GetTempPath(), "pm_hlp_" + Guid.NewGuid().ToString("N"));
        string name = "pm_ctr_" + Guid.NewGuid().ToString("N")[..8];
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(Path.Combine(helpers, "sub"));
        try
        {
            File.WriteAllText(Path.Combine(root, "data.txt"), "from the project\n");
            File.WriteAllText(Path.Combine(helpers, "helper.py"), "import os, sys\nsys.path.insert(0, os.path.join(os.path.dirname(__file__), 'sub'))\nimport more\nprint('helper in', os.getcwd(), more.WORD, open(sys.argv[1]).read().strip())\n");
            File.WriteAllText(Path.Combine(helpers, "sub", "more.py"), "WORD = 'copied'\n");
            // a user that is not root, as many images have: the copy is made as root and can still be read
            Assert.True(Docker("run", "-d", "--name", name, "-u", "1000", "-v", $"{root}:/src/proj", "python:3.12-slim", "sleep", "600") != null);

            var t = new ContainerTarget(name) { Project = root };
            Assert.Equal("/src/proj", t.WorkDirInContainer);
            var psi = t.Start("python", root, new[] { Path.Combine(helpers, "helper.py"), Path.Combine(root, "data.txt") }, null, new[] { helpers });
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(output.Contains("helper in /src/proj copied from the project"), output + error);
        }
        finally
        {
            Docker("rm", "-f", name);
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(helpers, true); } catch { }
        }
    }
}
