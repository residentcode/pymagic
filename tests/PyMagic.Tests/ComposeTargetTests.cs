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
        // a folder of helper scripts cannot be mounted into a running container
        Assert.Throws<ServiceException>(() => t.Start("python", root, new[] { "x" }, null, new[] { Path.Combine(Path.GetTempPath(), "helpers") }));
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
        Assert.True((t.Readable("/usr/lib/python3/site-packages")) == null);
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
}
