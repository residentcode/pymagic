using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PHP's lint in Laravel Sail's container (Settings > PHP: sail): the editor's text given to <c>php -l</c> in the running laravel.test
/// service. The engine is the test machine's, reached through an SSH tunnel to its docker socket made here (DOCKER_HOST for this process only;
/// nothing of the user's Docker or SSH settings is used or changed), with the Sail image built there before (sail-8.5/app). Runs only when
/// PYMAGIC_CT_HOST, PYMAGIC_CT_KEY and PYMAGIC_CT_KNOWN_HOSTS name the machine, docker is on the PATH and this computer has no PHP of its own
/// (which the lint would ask first); skipped without.
/// </summary>
public class SailLintLiveTests
{
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
    public void PhpsLintInSailsContainerFindsTheSyntaxErrorOfTheEditorsText()
    {
        string? V(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : null;
        if (V("PYMAGIC_CT_HOST") is not { } host || V("PYMAGIC_CT_KEY") is not { } key || V("PYMAGIC_CT_KNOWN_HOSTS") is not { } known || SshTunnels.FindSsh() is not { } ssh) return;
        if (PhpTools.FindPhp(null) != null) return;
        try { if (Docker(Path.GetTempPath(), "--version") is not { } v || v.StartsWith("FAILED")) return; } catch (System.ComponentModel.Win32Exception) { return; }

        int port = FreePort();
        var tunnel = Process.Start(new ProcessStartInfo(ssh)
        {
            ArgumentList = { "-N", "-T", "-o", "BatchMode=yes", "-o", "ExitOnForwardFailure=yes", "-o", "UserKnownHostsFile=" + known, "-i", key, "-L", $"127.0.0.1:{port}:/var/run/docker.sock", "root@" + host },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true,
        })!;
        string? before = Environment.GetEnvironmentVariable("DOCKER_HOST"), beforeContext = Environment.GetEnvironmentVariable("DOCKER_CONTEXT");
        string root = Path.Combine(Path.GetTempPath(), "pmsaillint" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(root);
        try
        {
            Environment.SetEnvironmentVariable("DOCKER_HOST", $"tcp://127.0.0.1:{port}");
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", null);
            var until = DateTime.UtcNow.AddSeconds(20);
            string? info = null;
            while (DateTime.UtcNow < until && ((info = Docker(root, "version", "--format", "{{.Server.Os}}")) == null || info.StartsWith("FAILED"))) Thread.Sleep(500);
            Assert.True(info?.Trim() == "linux", "the engine answers through the tunnel: " + info);
            if (!(Docker(root, "image", "inspect", "sail-8.5/app") ?? "FAILED").TrimStart().StartsWith("[")) return;   // Sail's image is not built there

            // Sail's laravel.test service, as sail:install writes it (its image; the project is not mounted: lint reads its standard input)
            File.WriteAllText(Path.Combine(root, "compose.yaml"), "services:\n    laravel.test:\n        image: sail-8.5/app\n        entrypoint: [\"sleep\", \"600\"]\n");
            string? up = Docker(root, "compose", "up", "-d");
            Assert.True(up != null && !up.StartsWith("FAILED"), "compose up: " + up);
            Assert.True(SailTarget.Of(root, "sail")!.Running(), "Sail's container runs");

            var found = PhpLint.Lint(root, "sail", "<?php\n$total = 1;\n$x = ;\n$y = 2;\n");
            Assert.True(found != null, "a finding");
            Assert.Equal(3, found!.Line);
            Assert.Contains("syntax error", found.Message);
            Assert.True(PhpLint.Lint(root, "sail", "<?php\n$name = \"Zoë's \\$cost\";\necho '$x' . \"\\n\";\n") == null, "no finding");
        }
        finally
        {
            Docker(root, "compose", "down", "-t", "1");
            Environment.SetEnvironmentVariable("DOCKER_HOST", before);
            Environment.SetEnvironmentVariable("DOCKER_CONTEXT", beforeContext);
            try { tunnel.Kill(); tunnel.Dispose(); } catch (InvalidOperationException) { }
            try { Directory.Delete(root, true); } catch (IOException) { }
        }
    }
}
