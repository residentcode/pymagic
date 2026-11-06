using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// A Compose service's libraries copied out of a real Docker engine for the editor (the Open work's "against a real Docker engine" of item 325):
/// the run form from a container made of the image compose builds for the service (and removed again), the exec form from the service's running
/// container. The engine is the test machine's, reached through an SSH tunnel to its docker socket made here (DOCKER_HOST for this process only;
/// nothing of the user's Docker or SSH settings is used or changed). Runs only when PYMAGIC_CT_HOST, PYMAGIC_CT_KEY and PYMAGIC_CT_KNOWN_HOSTS
/// name the machine and docker is on the PATH; skipped without.
/// </summary>
public class ComposeLiveTests
{
    const string Lib = "/usr/local/lib/python3.12", Site = "/usr/local/lib/python3.12/site-packages";

    static string? Docker(string folder, params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        return p.ExitCode == 0 ? output.Result : "FAILED " + error;
    }

    static int FreePort() { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); int port = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return port; }

    [Fact]
    public void AServicesLibrariesAreCopiedOutOfARealEngine()
    {
        string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        if (V("PYMAGIC_CT_HOST") is not { } host || V("PYMAGIC_CT_KEY") is not { } key || V("PYMAGIC_CT_KNOWN_HOSTS") is not { } known || SshTunnels.FindSsh() is not { } ssh) return;
        try { if (Docker(Path.GetTempPath(), "--version") is not { } v || v.StartsWith("FAILED")) return; } catch (System.ComponentModel.Win32Exception) { return; }

        int port = FreePort();
        var tunnel = Process.Start(new ProcessStartInfo(ssh)
        {
            ArgumentList = { "-N", "-T", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "UserKnownHostsFile=" + known, "-i", key, "-L", $"127.0.0.1:{port}:/var/run/docker.sock", "root@" + host },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        string? before = Environment.GetEnvironmentVariable("DOCKER_HOST"), beforeContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        string root = Path.Combine(Path.GetTempPath(), "pmcompose" + Guid.NewGuid().ToString("N")[..10]);
        string mirrors = Path.Combine(Path.GetTempPath(), "pm_compose_mirror_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", $"tcp://127.0.0.1:{port}");
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", null);
            var until = DateTime.UtcNow.AddSeconds(20);
            string? info = null;
            while (DateTime.UtcNow < until && ((info = Docker(root, "version", "--format", "{{.Server.Os}}")) == null || info.StartsWith("FAILED"))) Thread.Sleep(500);
            Assert.True(info?.Trim() == "linux", "the engine answers through the tunnel: " + info + tunnel.StandardError.ReadToEndAsync().Wait(0));

            // a service that is only built (its image is "<project>-web"), with a package of its own, and one that is up for the exec form
            File.WriteAllText(Path.Combine(root, "Dockerfile"), "FROM python:3.12-slim\nRUN pip install --no-cache-dir six==1.16.0\n");
            File.WriteAllText(Path.Combine(root, "compose.yaml"), "services:\n  web:\n    build: .\n    command: sleep 600\n");
            File.WriteAllText(Path.Combine(root, "app.py"), "import six\n");
            string? built = Docker(root, "compose", "build");
            Assert.True(built != null && !built.StartsWith("FAILED"), "compose build: " + built);
            int containers = (Docker(root, "ps", "-aq") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;

            // the run form: a container made of the service's image, copied from, removed again
            var run = new ComposeTarget("web", mirrors: mirrors) { Project = root };
            Assert.Equal(0, run.Mirror(new[] { Lib, Site, "/workspace" }).Count);
            string copied = run.Readable(Site)!;
            Assert.True(copied.StartsWith(mirrors, StringComparison.Ordinal), copied);
            Assert.True(File.Exists(Path.Combine(copied, "six.py")), "the service's own package is there for the editor: " + copied);
            Assert.True(File.Exists(Path.Combine(run.Readable(Lib)!, "os.py")), "and the standard library");
            Assert.Equal(Path.GetFullPath(root), run.Readable("/workspace"));   // the project is read in place
            Assert.Equal(containers, (Docker(root, "ps", "-aq") ?? "").Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);   // the one made for the copy is gone

            // the exec form: from the service's running container, which stays up
            string? up = Docker(root, "compose", "up", "-d");
            Assert.True(up != null && !up.StartsWith("FAILED"), "compose up: " + up);
            var exec = new ComposeTarget("web", exec: true, workDirInService: "/app", mirrors: mirrors) { Project = root };
            Assert.Equal(0, exec.Mirror(new[] { Site }).Count);
            Assert.True(File.Exists(Path.Combine(exec.Readable(Site)!, "six.py")), "copied from the running container");
            Assert.True(exec.Readable(Site) != run.Readable(Site), "each form has its own copy");
            Assert.True((Docker(root, "compose", "ps", "-q", "web") ?? "").Trim().Length > 0, "the service is still running");
        }
        finally
        {
            Docker(root, "compose", "down", "-t", "1", "--rmi", "local");
            Environment.SetEnvironmentVariable("DOCKER_HOST", before);
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", beforeContext);
            try { tunnel.Kill(); tunnel.Dispose(); } catch (InvalidOperationException) { }
            try { Directory.Delete(root, true); } catch (IOException) { }
            try { if (Directory.Exists(mirrors)) Directory.Delete(mirrors, true); } catch (IOException) { }
        }
    }
}
