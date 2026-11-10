using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Laravel Sail as the PHP of a project (Settings > PHP: <c>sail</c>): its compose file's laravel.test service, whose container has the project
/// at /var/www/html (Sail mounts it), so nothing is copied. PHP runs there with <c>docker compose exec -T laravel.test php …</c> (each PHP
/// process marked <c>-d pymagic=ID</c>, which Stop ends there: stopping docker on this side would leave it running), its paths are the
/// project's files here, and Xdebug there reaches PyMagic at host.docker.internal. Sail's own variables (WWWUSER, WWWGROUP) are 1000 when
/// unset, as Sail's script gives them on Linux.
/// </summary>
public sealed class SailTarget : ExecutionTarget
{
    public const string Service = "laravel.test";
    public const string Home = "/var/www/html";

    /// <summary>The project folder here.</summary>
    public string Root { get; }

    /// <summary>The compose file Sail installed (compose.yaml, docker-compose.yml…), relative to the project.</summary>
    public string ComposeFile { get; }

    public SailTarget(string root, string composeFile)
    {
        Root = Path.GetFullPath(root);
        ComposeFile = composeFile;
    }

    /// <summary>Whether Settings > PHP names Sail.</summary>
    public static bool IsSetting(string? setting) => string.Equals(setting?.Trim(), "sail", StringComparison.OrdinalIgnoreCase);

    /// <summary>The compose file of the project that has Sail's laravel.test service, or null.</summary>
    public static string? ComposeFileOf(string root)
    {
        foreach (var name in new[] { "compose.yaml", "compose.yml", "docker-compose.yml", "docker-compose.yaml" })
        {
            string file = Path.Combine(root, name);
            try { if (File.Exists(file) && Regex.IsMatch(File.ReadAllText(file), @"^\s{2,}laravel\.test\s*:", RegexOptions.Multiline)) return name; }
            catch (IOException) { }
        }
        return null;
    }

    /// <summary>Sail when Settings > PHP names it and the project has it; a ServiceException when it names Sail and the project has not.</summary>
    public static SailTarget? Of(string root, string? setting)
    {
        if (!IsSetting(setting)) return null;
        return ComposeFileOf(root) is { } file ? new SailTarget(root, file)
            : throw new ServiceException("Settings > PHP names Laravel Sail, and the project has no compose file with Sail's laravel.test service: install it (composer require laravel/sail --dev, then php artisan sail:install).");
    }

    public override string Kind => "sail";
    public override string Name => "Sail: " + Service;
    public override string Spec(string python) => "sail";

    // ------------------------------------------------------------ docker compose

    ProcessStartInfo Compose(IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null)
    {
        var psi = new ProcessStartInfo("docker") { WorkingDirectory = Root, UseShellExecute = false, CreateNoWindow = true };
        psi.ArgumentList.Add("compose");
        psi.ArgumentList.Add("-f");
        psi.ArgumentList.Add(ComposeFile);
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var name in new[] { "WWWUSER", "WWWGROUP" })
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(name))) psi.Environment[name] = "1000";
        if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
        return psi;
    }

    /// <summary>Runs docker compose with these arguments and waits: (exit code, output, errors).</summary>
    public (int Code, string Out, string Err) Docker(IEnumerable<string> args, int timeoutMs = 60_000, IReadOnlyDictionary<string, string>? env = null, string? input = null)
    {
        var psi = Compose(args, env);
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false); psi.StandardInputEncoding = new UTF8Encoding(false);
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("docker did not start");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (input != null) p.StandardInput.Write(input);
            p.StandardInput.Close();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("Docker did not answer in time"); }
            p.WaitForExit();
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException("Docker was not found: install Docker Desktop (Sail runs in its containers) and start it."); }
    }

    /// <summary>A shell command in the container, in the project's folder there: (exit code, output, errors).</summary>
    public (int Code, string Out, string Err) Exec(string command, int timeoutMs = 60_000, string? input = null) =>
        Docker(new[] { "exec", "-T", "-w", Home, Service, "sh", "-c", command }, timeoutMs, null, input);

    /// <summary>The container's log from now on, as it goes on (Laravel's server writes its requests there).</summary>
    public ProcessStartInfo FollowLog() => Compose(new[] { "logs", "-f", "--no-log-prefix", "--since", "1s", Service });

    /// <summary>
    /// Xdebug of Laravel's server in the container for PyMagic: an .ini of the container's own PHP (not the project's) with what XDEBUG_CONFIG
    /// cannot set (a session for every request, the control socket Pause needs), and the server started again by supervisord to read it (ended
    /// with SIGKILL, it and its php -S workers: artisan serve ends well on SIGTERM, which supervisord does not start again).
    /// The container made again from its image has none.
    /// </summary>
    public void DebugServer()
    {
        const string Ini = "xdebug.start_with_request=yes\\nxdebug.control_socket=time\\nxdebug.idekey=PYMAGIC\\n";
        var (code, output, err) = Docker(new[] { "exec", "-T", "-u", "root", Service, "sh", "-c",
            "d=$(php --ini | sed -n 's/^Scan for additional .ini files in: *//p' | tr -d '\"'); [ -d \"$d\" ] && printf '" + Ini + "' > \"$d/99-pymagic-xdebug.ini\" && (pkill -9 -f 'artisan[ ]serve'; pkill -9 -f '[ ]-S 0.0.0.0:80'; true)"
            // supervisord starts it again a second later: until it listens (a PHP without its settings asks, so it is not debugged itself)
            + " && sleep 2 && for i in $(seq 1 60); do php -n -r 'exit(@fsockopen(\"127.0.0.1\", 80) ? 0 : 1);' && exit 0; sleep 0.5; done; exit 1" }, 90_000);
        if (code != 0) throw new ServiceException("Xdebug of Sail's server could not be set: " + LastLines(output + err));
    }

    /// <summary>Whether the laravel.test container runs.</summary>
    public bool Running()
    {
        var (code, output, _) = Docker(new[] { "ps", "--status", "running", "-q", Service }, 30_000);
        return code == 0 && output.Trim().Length > 0;
    }

    /// <summary>The container brought up (made again with <paramref name="recreate"/>), the variables of <paramref name="env"/> over the .env's.</summary>
    public void Up(bool recreate = false, IReadOnlyDictionary<string, string>? env = null)
    {
        var args = new List<string> { "up", "-d" };
        if (recreate) args.Add("--force-recreate");
        args.Add(Service);
        var (code, output, err) = Docker(args, 15 * 60_000, env);
        if (code != 0) throw new ServiceException("Sail's container did not start: " + LastLines(output + err));
    }

    static string LastLines(string text) => string.Join(" ", text.Replace("\r", "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).TakeLast(3));

    /// <summary>Where Sail serves the app here: http://localhost:APP_PORT (the variable, else the .env's, else 80).</summary>
    public string AppUrl()
    {
        string? port = Environment.GetEnvironmentVariable("APP_PORT");
        if (string.IsNullOrEmpty(port))
            try { if (Regex.Match(File.ReadAllText(Path.Combine(Root, ".env")), @"^\s*APP_PORT\s*=\s*(\d+)", RegexOptions.Multiline) is { Success: true } m) port = m.Groups[1].Value; }
            catch (IOException) { }
        return port is { Length: > 0 } and not "80" ? $"http://localhost:{port}" : "http://localhost";
    }

    // ------------------------------------------------------------ a program there

    /// <summary>
    /// <c>docker compose exec -T</c> of a program in the container, in the folder of <paramref name="workDir"/> there; PHP marked with this run's
    /// id (PYMAGIC_RUN_ID of the result). Paths of the project in the arguments and the variables are the container's.
    /// </summary>
    public override ProcessStartInfo Start(string program, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        string runId = Guid.NewGuid().ToString("N")[..12];
        var all = new List<string> { "exec", "-T" };
        if (env != null) foreach (var (k, v) in env) { all.Add("-e"); all.Add($"{k}={(HostPath.IsMatch(v) ? ToTarget(v) : v)}"); }
        string w = ToTarget(workDir);
        all.Add("-w"); all.Add(w.StartsWith('/') ? w : Home);
        all.Add(Service);
        all.Add(program);
        if (program == "php") { all.Add("-d"); all.Add("pymagic=" + runId); }
        foreach (var a in args) all.Add(HostPath.IsMatch(a) ? ToTarget(a) : a);
        var psi = Compose(all);
        psi.Environment["PYMAGIC_RUN_ID"] = runId;
        return psi;
    }

    public override void Stop(string runId)
    {
        if (!Regex.IsMatch(runId, "^[0-9a-f]{12}$")) return;
        try { Exec($"pkill -f 'pymagic[=]{runId}' || true", 30_000); } catch (ServiceException) { /* the container is gone, so is the program */ }
    }

    public override string ToTarget(string hostPath)
    {
        if (!HostPath.IsMatch(hostPath)) return hostPath;
        string full = Path.GetFullPath(hostPath);
        if (string.Equals(full.TrimEnd('\\', '/'), Root, StringComparison.OrdinalIgnoreCase)) return Home;
        return full.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ? Home + "/" + full[(Root.Length + 1)..].Replace('\\', '/') : hostPath;
    }

    public override string ToHost(string targetPath)
    {
        if (targetPath == Home) return Root;
        return targetPath.StartsWith(Home + "/", StringComparison.Ordinal) ? Path.Combine(Root, targetPath[(Home.Length + 1)..].Replace('/', Path.DirectorySeparatorChar)) : targetPath;
    }

    public override string? Readable(string targetPath) => targetPath.StartsWith(Home + "/", StringComparison.Ordinal) ? ToHost(targetPath) : null;

    /// <summary>A debugger here is reached from the container at host.docker.internal: it listens on every address.</summary>
    public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Any, "host.docker.internal");
}
