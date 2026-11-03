using System.Diagnostics;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>A Docker Compose service as the interpreter: spec, command line, paths, and listing the services. No Docker engine is involved.</summary>
public class ComposeTargetTests
{
    static string Args(ProcessStartInfo psi) => string.Join(" ", psi.ArgumentList);

    [Fact]
    public void ASpecNamesTheServiceAndItsOptions()
    {
        var plain = ExecutionTarget.Parse("compose:web|python");
        Assert.True(plain != null && plain.Value.Target is ComposeTarget { Service: "web", File: null, Exec: false } && plain.Value.Python == "python");
        var full = ExecutionTarget.Parse("compose:api?f=deploy/dev.yml&exec=1&w=/app|/usr/local/bin/python");
        var t = (ComposeTarget)full!.Value.Target;
        Assert.Equal(("api", "deploy/dev.yml", true, "/app"), (t.Service, t.File, t.Exec, t.WorkDirInService));
        Assert.Equal("/usr/local/bin/python", full.Value.Python);
        Assert.Equal("compose:api?f=deploy/dev.yml&exec=1&w=/app|/usr/local/bin/python", t.Spec("/usr/local/bin/python"));
        Assert.Equal("compose:web|python", new ComposeTarget("web").Spec("python"));
        Assert.Equal("compose", t.Kind);
        Assert.True(ExecutionTarget.IsSpec("compose:web|python"));
        Assert.True((ExecutionTarget.Parse("compose:|python")) == null);
        Assert.True((ExecutionTarget.Parse("compose:web")) == null);
        Assert.True((ExecutionTarget.Parse("compose:-x|python")) == null);
        Assert.True((ExecutionTarget.Parse("compose:web?f=-evil.yml|python")) == null);
        Assert.True((ExecutionTarget.Parse("compose:a b|python")) == null);
    }

    [Fact]
    public void ARunStartsANewContainerOfTheServiceWithTheProjectShared()
    {
        var t = new ComposeTarget("web", "dev.yml");
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        t.Project = root;
        var psi = t.Start("python", Path.Combine(root, "src"), new[] { "-u", Path.Combine(helpers, "pm_kernel.py"), "--root", root },
            new Dictionary<string, string> { ["PYTHONPATH"] = root + ";" + helpers }, new[] { helpers });
        Assert.Equal("docker", psi.FileName);
        string line = Args(psi);
        Assert.True(line.StartsWith("compose -f dev.yml run --rm -T -v ", StringComparison.Ordinal), line);
        Assert.Contains($"-v {Path.GetFullPath(root)}:/workspace", line);
        Assert.Contains($"-v {Path.GetFullPath(helpers)}:/pymagic/0:ro", line);
        Assert.Contains("-w /workspace/src", line);
        Assert.Contains("-e PYTHONPATH=/workspace:/pymagic/0", line);
        Assert.True(line.EndsWith(" web python -u /pymagic/0/pm_kernel.py --root /workspace", StringComparison.Ordinal), line);
        // options of compose come before the service, the program after it
        Assert.True(line.IndexOf("-e PYTHONPATH", StringComparison.Ordinal) < line.IndexOf(" web python", StringComparison.Ordinal));
    }

    [Fact]
    public void WithoutAFileComposeFindsItsOwn()
    {
        var psi = new ComposeTarget("db").Start("python", Path.GetTempPath(), new[] { "a.py" });
        Assert.True(Args(psi).StartsWith("compose run --rm -T ", StringComparison.Ordinal));
    }

    [Fact]
    public void ExecUsesTheRunningContainerAndWhereItMountsTheProject()
    {
        var t = new ComposeTarget("api", null, exec: true, workDirInService: "/app");
        string root = Path.Combine(Path.GetTempPath(), "proj");
        t.Project = root;
        var psi = t.Start("python", Path.Combine(root, "pkg"), new[] { "-m", "pytest", Path.Combine(root, "tests", "t.py") }, new Dictionary<string, string> { ["X"] = "1" });
        Assert.Equal("compose exec -T -w /app/pkg -e X=1 api python -m pytest /app/tests/t.py", Args(psi));
        Assert.Equal("/app/tests/t.py", t.ToTarget(Path.Combine(root, "tests", "t.py")));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "tests", "t.py"), t.ToHost("/app/tests/t.py"));
        Assert.Equal(Path.GetFullPath(root), t.ToHost("/app"));
    }

    [Fact]
    public void ExecFindsWhereTheServiceMountsTheProjectAndCopiesTheHelperScriptsIn()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        string id = "0123456789abcdef0123";
        var calls = new List<string>();
        string? Docker(string[] a)
        {
            string line = string.Join(" ", a);
            calls.Add(line);
            if (line == "compose -f dev.yml ps -q api") return id + "\n";
            if (line == $"inspect --format {{{{json .Mounts}}}} {id}") return $$"""[{"Type":"bind","Source":"{{Path.GetFullPath(root).Replace("\\", "\\\\")}}","Destination":"/srv/code"}]""";
            if (line == $"inspect --format {{{{.Id}}}} {id}") return id + "\n";
            return "";
        }
        var t = new ComposeTarget("api", "dev.yml", exec: true, docker: Docker) { Project = root };
        Assert.Equal("/srv/code", t.WorkDirInService);
        Assert.Equal("compose:api?f=dev.yml&exec=1|python", t.Spec("python"));   // a found place is not written into the spec
        var psi = t.Start("python", root, new[] { Path.Combine(helpers, "pm_tests.py") }, null, new[] { helpers });
        Assert.Equal("compose -f dev.yml exec -T -w /srv/code api python /pymagic/0/pm_tests.py", Args(psi));
        Assert.True(calls.Contains($"cp {Path.Combine(Path.GetFullPath(helpers), ".")} {id}:/pymagic/0"), string.Join(" | ", calls));
        Assert.True(calls.Contains($"exec -u 0 {id} sh -c rm -rf /pymagic/0 && mkdir -p /pymagic"), string.Join(" | ", calls));

        // no running container: said so
        var stopped = new ComposeTarget("api", null, exec: true, docker: _ => "") { Project = root };
        Assert.Equal("/workspace", stopped.WorkDirInService);
        Assert.Throws<ServiceException>(() => stopped.Start("python", root, new[] { "x" }, null, new[] { helpers }));
    }

    [Fact]
    public void PathsMapLikeTheDockerTarget()
    {
        var t = new ComposeTarget("web");
        string root = Path.Combine(Path.GetTempPath(), "proj"), helpers = Path.Combine(Path.GetTempPath(), "helpers");
        t.Project = root;
        t.Start("python", root, new string[0], null, new[] { helpers });
        Assert.Equal("/workspace/src/a.py", t.ToTarget(Path.Combine(root, "src", "a.py")));
        Assert.Equal("/pymagic/0/x.py", t.ToTarget(Path.Combine(helpers, "x.py")));
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "src", "a.py"), t.ToHost("/workspace/src/a.py"));
        Assert.Equal("/usr/lib/x", t.ToHost("/usr/lib/x"));
        Assert.Equal("host.docker.internal", t.HelperAddress().Connect);
        // the project's files are read in place, a library from its copy
        Assert.Equal(Path.Combine(Path.GetFullPath(root), "src", "a.py"), t.Readable("/workspace/src/a.py"));
        Assert.True(t.Readable("/usr/lib/python3/site-packages")!.EndsWith(Path.Combine("usr", "lib", "python3", "site-packages"), StringComparison.Ordinal));
        Assert.True(t.Readable("relative") == null);
    }

    [Fact]
    public void TheLibrariesOfTheServiceAreCopiedOutForTheEditor()
    {
        string root = Path.Combine(Path.GetTempPath(), "proj"), mirrors = Path.Combine(Path.GetTempPath(), "pm_compose_mirror_" + Guid.NewGuid().ToString("N"));
        const string lib = "/usr/local/lib/python3.12", site = "/usr/local/lib/python3.12/site-packages";
        try
        {
            // the run form: a container is made of the service's image (one that is only built is "project-service"), copied from, and removed
            var calls = new List<string>();
            string? Run(string[] a)
            {
                string line = string.Join(" ", a);
                calls.Add(line);
                if (line == "compose -f dev.yml config --format json") return """{"name":"demo","services":{"web":{"build":{"context":"."}},"db":{"image":"postgres:16"}}}""";
                if (line == "create demo-web") return "c0ffee0000c0ffee\n";
                if (line.StartsWith("cp c0ffee0000c0ffee:", StringComparison.Ordinal)) { Directory.CreateDirectory(a[2]); return ""; }
                return "";
            }
            var t = new ComposeTarget("web", "dev.yml", docker: Run, mirrors: mirrors) { Project = root };
            Assert.Equal(0, t.Mirror(new[] { lib, site, "/workspace", "/workspace/src" }).Count);
            Assert.True(calls.Contains($"cp c0ffee0000c0ffee:{lib} {t.Readable(lib)}"), string.Join(" | ", calls));
            Assert.True(calls.Contains("rm c0ffee0000c0ffee"), "the container made for the copy is removed");
            Assert.True(!calls.Any(c => c.Contains(":/workspace")), "the project is read in place, not copied");
            Assert.True(t.Readable(site)!.StartsWith(mirrors, StringComparison.Ordinal) && Directory.Exists(t.Readable(lib)));
            // once copied it is kept: nothing is asked of docker again
            calls.Clear();
            Assert.Equal(0, t.Mirror(new[] { lib }).Count);
            Assert.Equal(0, calls.Count);
            // a service with an image of its own
            var db = new List<string>();
            new ComposeTarget("db", "dev.yml", docker: a => { db.Add(string.Join(" ", a)); return Run(a); }, mirrors: mirrors) { Project = root }.Mirror(new[] { lib });
            Assert.True(db.Contains("create postgres:16"), string.Join(" | ", db));

            // the exec form: from the service's running container, which stays
            string id = "0123456789abcdef0123";
            var exec = new List<string>();
            string? Exec(string[] a)
            {
                string line = string.Join(" ", a);
                exec.Add(line);
                if (line == "compose ps -q api") return id + "\n";
                if (line.StartsWith($"cp {id}:", StringComparison.Ordinal)) { Directory.CreateDirectory(a[2]); return ""; }
                return "";
            }
            var e = new ComposeTarget("api", null, exec: true, workDirInService: "/app", docker: Exec, mirrors: mirrors) { Project = root };
            Assert.Equal(0, e.Mirror(new[] { lib, "/app" }).Count);
            Assert.True(exec.Contains($"cp {id}:{lib} {e.Readable(lib)}"), string.Join(" | ", exec));
            Assert.True(!exec.Any(c => c.StartsWith("rm ", StringComparison.Ordinal) || c.StartsWith("create ", StringComparison.Ordinal)));
            Assert.True(e.Readable(lib) != t.Readable(lib), "each service has its own copy");
            Assert.Equal(Path.GetFullPath(root), e.Readable("/app"));

            // no container to copy from (compose does not answer, the service is not running): every folder is told as not copied
            Assert.Equal(1, new ComposeTarget("web", docker: _ => null, mirrors: mirrors) { Project = root }.Mirror(new[] { lib }).Count);
            Assert.Equal(1, new ComposeTarget("worker", exec: true, docker: _ => "", mirrors: mirrors) { Project = root }.Mirror(new[] { lib }).Count);
        }
        finally { if (Directory.Exists(mirrors)) Directory.Delete(mirrors, true); }
    }

    const string Sample = """
        # a project
        name: demo
        x-common: &common
          restart: always
        services:
          web:            # the app
            build: .
            ports: ["8000:8000"]
            depends_on:
              - db
          "db":
            image: postgres:16
          'worker-1':
            <<: *common
            image: python:3.12

          cache.v2:
            image: redis
        volumes:
          data: {}
        """;

    [Fact]
    public void ServicesAreReadFromTheFile()
    {
        Assert.Equal(string.Join(",", new[] { "web", "db", "worker-1", "cache.v2" }), string.Join(",", ComposeTarget.ParseServices(Sample)));
        Assert.Equal("", string.Join(",", ComposeTarget.ParseServices("name: x\nvolumes:\n  data: {}\n")));
        Assert.Equal(string.Join(",", new[] { "a", "b" }), string.Join(",", ComposeTarget.ParseServices("services:\r\n    a:\r\n        image: x\r\n    b:\r\n        image: y\r\nnetworks:\r\n    c: {}\r\n")));
    }

    [Fact]
    public void ServicesPreferWhatComposeItselfSays()
    {
        string dir = Directory.CreateTempSubdirectory("compose").FullName;
        try
        {
            Assert.Equal("", string.Join(",", ComposeTarget.Services(dir)));   // no file
            File.WriteAllText(Path.Combine(dir, "docker-compose.yml"), Sample);
            Assert.Equal(Path.Combine(dir, "docker-compose.yml"), ComposeTarget.FindFile(dir));
            string[]? asked = null;
            var fromDocker = ComposeTarget.Services(dir, null, a => { asked = a; return "web\ndb\nfrom-include\n"; });
            Assert.Equal(string.Join(",", new[] { "compose", "config", "--services" }), string.Join(",", asked));
            Assert.Equal(string.Join(",", new[] { "web", "db", "from-include" }), string.Join(",", fromDocker));
            // docker missing or failing: the file is read here
            Assert.Equal(string.Join(",", new[] { "web", "db", "worker-1", "cache.v2" }), string.Join(",", ComposeTarget.Services(dir, null, _ => null)));
            Assert.Equal(string.Join(",", new[] { "web", "db", "worker-1", "cache.v2" }), string.Join(",", ComposeTarget.Services(dir, null, _ => throw new System.ComponentModel.Win32Exception())));
            File.WriteAllText(Path.Combine(dir, "other.yml"), "services:\n  only:\n    image: x\n");
            Assert.Equal(string.Join(",", new[] { "only" }), string.Join(",", ComposeTarget.Services(dir, "other.yml", a => { asked = a; return null; })));
            Assert.Equal(string.Join(",", new[] { "compose", "-f", "other.yml", "config", "--services" }), string.Join(",", asked));
            Assert.Equal("", string.Join(",", ComposeTarget.Services(dir, "missing.yml", _ => "x\n")));
        }
        finally { Directory.Delete(dir, true); }
    }

    /// <summary>With PYMAGIC_TEST_COMPOSE=1 and a running Docker engine (python:3.12-slim present), a script really runs in a service.</summary>
    [Fact]
    public void AScriptRunsInAServiceOfARealEngine()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_COMPOSE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "pm_compose_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            string nl = Environment.NewLine;
            File.WriteAllText(Path.Combine(root, "compose.yaml"), string.Join(nl, "services:", "  web:", "    image: python:3.12-slim", "    command: sleep 600", ""));
            File.WriteAllText(Path.Combine(root, "hello.py"), string.Join(nl, "import os", "print('hi from', os.getcwd())", ""));
            var t = new ComposeTarget("web", null);
            t.Project = root;
            var psi = t.Start("python", root, new[] { Path.Combine(root, "hello.py") }, new Dictionary<string, string>(), Array.Empty<string>());
            psi.WorkingDirectory = root;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.Contains("hi from /workspace", output + error);
        }
        finally { try { Directory.Delete(root, true); } catch { } }
    }

    static string? ComposeIn(string folder, params string[] args)
    {
        var psi = new System.Diagnostics.ProcessStartInfo("docker") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("compose");
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = System.Diagnostics.Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Result : "FAILED " + error;
    }

    /// <summary>
    /// With PYMAGIC_TEST_COMPOSE=1 and a running Docker engine (python:3.12-slim present): a service that is up and mounts the project at /code
    /// is used with exec and without saying where; the place comes from docker inspect of its container, and a helper script is copied in and runs.
    /// </summary>
    [Fact]
    public void ARunningServiceSaysWhereItHasTheProjectAndTakesTheHelperScripts()
    {
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_COMPOSE") != "1") return;
        string root = Path.Combine(Path.GetTempPath(), "pm_compose_" + Guid.NewGuid().ToString("N"));
        string helpers = Path.Combine(Path.GetTempPath(), "pm_hlp_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(helpers);
        try
        {
            string nl = Environment.NewLine;
            File.WriteAllText(Path.Combine(root, "compose.yaml"), string.Join(nl, "services:", "  web:", "    image: python:3.12-slim", "    command: sleep 600", "    volumes:", "      - ./:/code", ""));
            File.WriteAllText(Path.Combine(helpers, "helper.py"), "import os\nprint('helper in', os.getcwd(), sorted(os.listdir('.')))\n");
            string? up = ComposeIn(root, "up", "-d");
            Assert.True(up != null && !up.StartsWith("FAILED", StringComparison.Ordinal), up);

            var t = new ComposeTarget("web", null, exec: true) { Project = root };
            Assert.Equal("/code", t.WorkDirInService);
            var psi = t.Start("python", root, new[] { Path.Combine(helpers, "helper.py") }, null, new[] { helpers });
            psi.WorkingDirectory = root;
            psi.RedirectStandardOutput = psi.RedirectStandardError = true;
            psi.UseShellExecute = false;
            using var p = System.Diagnostics.Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd(), error = p.StandardError.ReadToEnd();
            p.WaitForExit();
            Assert.True(output.Contains("helper in /code ['compose.yaml']"), output + error);
        }
        finally
        {
            ComposeIn(root, "down", "-t", "1");
            try { Directory.Delete(root, true); } catch { }
            try { Directory.Delete(helpers, true); } catch { }
        }
    }
}
