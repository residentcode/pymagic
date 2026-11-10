using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHP run in the Run panel: a .php file (`php file`) or an Artisan command of a Laravel project (`php artisan serve`, `php artisan migrate`, …),
/// in the project folder, with the PHP of Settings > PHP or the one found (PhpTools.FindPhp). Under the debugger it is run with Xdebug
/// (RunSession.PhpDebug.cs).
/// </summary>
public sealed partial class RunSession
{
    /// <summary>The php.exe a run uses (Settings > PHP), or null for the one found.</summary>
    public string? Php { get; set; }

    /// <summary>Whether a file is PHP that runs as it is (not a Blade template).</summary>
    public static bool IsPhpFile(string file) => file.EndsWith(".php", StringComparison.OrdinalIgnoreCase) && !file.EndsWith(".blade.php", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Runs a PHP file of the project (<paramref name="script"/>) or Artisan (<paramref name="artisan"/>: the command and its arguments), with its
    /// arguments; under the debugger with Xdebug, the breakpoints set first.
    /// </summary>
    public void StartPhp(string root, string? script, IReadOnlyList<string>? artisan, bool debug, IReadOnlyList<string> args, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        _root = root;
        IsDebug = debug;
        SailTarget? sail;
        try { sail = SailTarget.Of(root, Php); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (sail != null) { StartSailPhp(root, sail, script, artisan, debug, args, breakpoints); return; }
        if (PhpTools.Remote(Php) is { } remote) { StartRemotePhp(root, remote.Target, remote.Php, script, artisan, debug, args, breakpoints); return; }
        if (!Target.IsLocal) { Error("PHP runs on this computer, or on a machine reached by SSH that Settings > PHP names."); return; }
        string? php = PhpTools.FindPhp(Php);
        if (php == null) { Error("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP."); return; }
        var phpArgs = new List<string>();
        if (artisan != null)
        {
            if (!File.Exists(Path.Combine(root, "artisan"))) { Error("There is no artisan file in the project folder: it is not a Laravel project."); return; }
            if (artisan.Count == 0 || !System.Text.RegularExpressions.Regex.IsMatch(artisan[0], @"^[\w:.-]+$")) { Error("an Artisan command: serve, migrate, make:model, …"); return; }
            phpArgs.Add("artisan");
            phpArgs.AddRange(artisan);
        }
        else
        {
            string path = Path.GetFullPath(Path.Combine(root, script ?? ""));
            if (!File.Exists(path)) { Error($"script not found: {script}"); return; }
            phpArgs.Add(path);
        }
        phpArgs.AddRange(args);
        if (debug) { StartPhpDebug(root, php, phpArgs, artisan, breakpoints); return; }
        var psi = new ProcessStartInfo(php) { WorkingDirectory = WorkingDirectory ?? root };
        foreach (var a in phpArgs) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        // the PHP found first on the PATH of what it starts (artisan serve starts PHP's own server with PHP_BINARY, Composer's scripts with php)
        psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH"));
        _runId = null;
        StartProcess(psi, root, null, null);
    }

    /// <summary>
    /// PHP on another machine (Settings > PHP names it as <c>ssh:user@host|php</c>): the project copied there (its changed files), PHP run in its
    /// folder there, Laravel's server brought to the same port here, under the debugger Xdebug's connection brought back here; files an Artisan
    /// command made there (make:model, make:migration, …) are fetched into the project when it ends.
    /// </summary>
    /// <param name="program">PHP's own arguments as they are (a test run's vendor/bin/phpunit …), instead of a script or an Artisan command.</param>
    void StartRemotePhp(string root, SshTarget ssh, string php, string? script, IReadOnlyList<string>? artisan, bool debug, IReadOnlyList<string> args, IReadOnlyDictionary<string, JsonArray>? breakpoints, IReadOnlyList<string>? program = null)
    {
        ssh.Project = root;
        Target = ssh;
        _bridged = debug;
        var phpArgs = new List<string>();
        if (program != null) phpArgs.AddRange(program);
        else if (artisan != null)
        {
            if (!File.Exists(Path.Combine(root, "artisan"))) { Error("There is no artisan file in the project folder: it is not a Laravel project."); return; }
            if (artisan.Count == 0 || !System.Text.RegularExpressions.Regex.IsMatch(artisan[0], @"^[\w:.-]+$")) { Error("an Artisan command: serve, migrate, make:model, …"); return; }
            phpArgs.Add("artisan");
            phpArgs.AddRange(artisan);
        }
        else
        {
            string path = Path.GetFullPath(Path.Combine(root, script ?? ""));
            if (!File.Exists(path) || !IsInside(path, root)) { Error($"script not found in the project: {script}"); return; }
            phpArgs.Add(path);
        }
        phpArgs.AddRange(args);

        (string? Version, bool Xdebug, string? XdebugVersion, string? Composer) facts;
        try { facts = PhpTools.RemoteFacts(ssh, php); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (facts.Version == null) { Error($"{php} does not run PHP on {ssh.Destination}: install PHP there (sudo apt install php-cli) or name its path in Settings > PHP."); return; }
        if (debug && !facts.Xdebug) { Error($"Debugging PHP needs Xdebug, which the PHP of {ssh.Destination} has not: install it there (sudo apt install php-xdebug) and debug again."); return; }

        try { ssh.Prepare(root, null, text => _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = text })); }
        catch (ServiceException e) { Error(e.Message); return; }

        // Laravel's server listens there: the same port here reaches it (artisan serve's own default is 8000)
        var forward = new List<int>();
        if (artisan is { Count: > 0 } && artisan[0] == "serve") forward.Add(ServePort(phpArgs) ?? 8000);

        var env = new Dictionary<string, string>(ExtraEnvironment ?? new Dictionary<string, string>());
        var reverse = new List<int>();
        XdebugBridge? bridge = null;
        string? iniDir = null;
        string? token = null;
        if (debug)
        {
            // a Laravel server: its Blade templates compiled there first, as on this computer (pm_bladecompile.php), for the first request's breakpoints
            if (artisan is { Count: > 0 } && artisan[0] == "serve" && PhpTools.Laravel(root) != null)
                try
                {
                    string code = File.ReadAllText(ExtractHelper("pm_bladecompile.php"));
                    code = code.StartsWith("<?php") ? code[5..] : code;
                    // as base64: ssh's command line on Windows changes the backslashes before quotes that the code has
                    string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(code));
                    ssh.Remote($"cd {SshTarget.Quote(ssh.ProjectDir)} && {SshTarget.Quote(php)} -r {SshTarget.Quote($"eval(base64_decode('{b64}'));")}", 120_000);
                }
                catch (Exception e) when (e is ServiceException or IOException) { }
            bridge = new XdebugBridge(root, AllCode, ssh);
            // Pause: the PHP there connects to Xdebug's control socket there
            bridge.Control = XdebugBridge.RemoteControl(php, command => { var (code, output, _) = ssh.Remote(command, 15_000); return (code, output); });
            // the settings in a folder of their own there, which PHP_INI_SCAN_DIR adds to the system's own (":dir" keeps those: php-xdebug's own .ini loads it)
            try
            {
                iniDir = $"{ssh.Home}/.pymagic/xdebug-{Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(6)).ToLowerInvariant()}";
                var (code, _, err) = ssh.Remote($"mkdir -p {SshTarget.Quote(iniDir)} && printf %s {SshTarget.Quote(bridge.IniText(null))} > {SshTarget.Quote(iniDir + "/zz-pymagic-xdebug.ini")}", 30_000);
                if (code != 0) throw new ServiceException(err.Trim());
            }
            catch (ServiceException e) { bridge.Dispose(); Error("The Xdebug settings could not be written there: " + e.Message); return; }
            env["PHP_INI_SCAN_DIR"] = ":" + iniDir;
            reverse.Add(bridge.Port);
            token = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
            _listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
            _listener.Start();
        }

        var psi = ssh.StartProgram(php, WorkingDirectory ?? root, id => new[] { "-d", "pymagic=" + id }, phpArgs, env, reverse, forward,
            new[] { "XDEBUG_MODE", "XDEBUG_CONFIG", "XDEBUG_SESSION", "XDEBUG_TRIGGER" });
        _runId = psi.Environment["PYMAGIC_RUN_ID"];
        if (bridge != null) bridge.Connect(((System.Net.IPEndPoint)_listener!.LocalEndpoint).Port, token!);

        // what an Artisan command made there comes back into the project
        if (artisan is { Count: > 0 } && artisan[0] != "serve")
        {
            var started = DateTime.UtcNow;
            _afterExit = () => FetchMade(ssh, root, started);
        }
        StartProcess(psi, root, token, breakpoints);
        var process = _process;
        if (bridge != null || iniDir != null)
            Task.Run(async () =>
            {
                try { if (process != null) await process.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
                bridge?.Dispose();
                if (iniDir != null) try { ssh.Remote($"rm -rf {SshTarget.Quote(iniDir)}", 20_000); } catch (ServiceException) { }
            });
    }

    /// <summary>Composer as Settings > PHP names it (composer.bat, composer.phar, …), or null for the one found.</summary>
    public string? Composer { get; set; }

    /// <summary>
    /// A Composer command of the project (install, require laravel/sanctum, dump-autoload, a script of composer.json) in the Run panel, in the
    /// project folder, with the PHP of Settings > PHP first on its PATH. With PHP on another machine it runs there (the project copied first), and
    /// composer.json and composer.lock come back when it ends.
    /// </summary>
    public void StartComposer(string root, IReadOnlyList<string> args)
    {
        _root = root;
        IsDebug = false;
        if (args.Count == 0 || !System.Text.RegularExpressions.Regex.IsMatch(args[0], @"^[\w:.-]+$")) { Error("a Composer command: install, require, update, …"); return; }
        if (!File.Exists(Path.Combine(root, "composer.json"))) { Error("There is no composer.json in the project folder."); return; }
        var all = new List<string>(args) { "--no-interaction" };
        // Sail: the container's Composer, in the project it shares
        SailTarget? sail;
        try { sail = SailTarget.Of(root, Php); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (sail != null)
        {
            Target = sail;
            try { if (!sail.Running()) { Say("Starting Sail's container (docker compose up -d)…\n"); sail.Up(); } }
            catch (ServiceException e) { Error(e.Message); return; }
            var spsi = sail.Start("php", root, new[] { "/usr/bin/composer" }.Concat(all));
            _runId = spsi.Environment["PYMAGIC_RUN_ID"];
            StartProcess(spsi, root, null, null);
            return;
        }
        if (PhpTools.Remote(Php) is { } remote)
        {
            var ssh = remote.Target;
            ssh.Project = root;
            Target = ssh;
            (string? Version, bool Xdebug, string? XdebugVersion, string? Composer) facts;
            try { facts = PhpTools.RemoteFacts(ssh, remote.Php); }
            catch (ServiceException e) { Error(e.Message); return; }
            if (facts.Composer == null) { Error($"Composer is not on {ssh.Destination}: install it there (getcomposer.org)."); return; }
            try { ssh.Prepare(root, null, text => _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = text })); }
            catch (ServiceException e) { Error(e.Message); return; }
            var rpsi = ssh.StartProgram(remote.Php, root, id => new[] { "-d", "pymagic=" + id }, new[] { facts.Composer }.Concat(all));
            _runId = rpsi.Environment["PYMAGIC_RUN_ID"];
            _afterExit = () =>
            {
                foreach (var f in new[] { "composer.json", "composer.lock" })
                    if (ssh.Fetch(ssh.ProjectDir + "/" + f, Path.Combine(root, f)))
                        _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = $"Copied into the project: {f}\n" });
            };
            StartProcess(rpsi, root, null, null);
            return;
        }
        if (!Target.IsLocal) Target = ExecutionTarget.Local;
        string? php = PhpTools.FindPhp(Php);
        var composer = PhpTools.FindComposer(Composer, php);
        if (composer == null) { Error("Composer was not found: install Composer (getcomposer.org), or name it in Settings > PHP."); return; }
        var psi = new ProcessStartInfo(composer.Value.Program) { WorkingDirectory = root };
        foreach (var a in composer.Value.Prefix.Concat(all)) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        if (php != null) psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH"));
        psi.Environment["COMPOSER_NO_INTERACTION"] = "1";
        _runId = null;
        StartProcess(psi, root, null, null);
    }

    /// <summary>The port of `artisan serve --port=N` / `--port N`, or null for its default.</summary>
    static int? ServePort(IReadOnlyList<string> args)
    {
        for (int i = 0; i < args.Count; i++)
        {
            string a = args[i];
            string? value = a.StartsWith("--port=") ? a[7..] : a == "--port" && i + 1 < args.Count ? args[i + 1] : null;
            if (value != null && int.TryParse(value, out int p) && p is > 0 and < 65536) return p;
        }
        return null;
    }

    /// <summary>The files an Artisan command made or changed in the project's folder there since it started, copied into the project here.</summary>
    void FetchMade(SshTarget ssh, string root, DateTime since)
    {
        try
        {
            string dirs = string.Join(" ", new[] { "app", "bootstrap/app.php", "config", "database", "lang", "public", "resources", "routes", "tests", "stubs" }.Select(SshTarget.Quote));
            long seconds = Math.Max(1, (long)(DateTime.UtcNow - since).TotalSeconds + 2);
            var (_, output, _) = ssh.Remote($"cd {SshTarget.Quote(ssh.ProjectDir)} && find {dirs} -type f -newermt {SshTarget.Quote(seconds + " seconds ago")} ! -name '*.sqlite*' 2>/dev/null; true", 30_000);
            var made = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(f => !f.StartsWith('/') && !f.Contains("..")).ToList();
            foreach (var rel in made)
            {
                string local = Path.GetFullPath(Path.Combine(root, rel));
                if (!IsInside(local, root)) continue;
                if (ssh.Fetch(ssh.ProjectDir + "/" + rel, local))
                    _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = $"Copied into the project: {rel}\n" });
            }
            // copied back, they are what is there: the next copy does not send them again
            if (made.Count > 0) ssh.Prepare(root, null, null);
        }
        catch (ServiceException e) { _emit(new JsonObject { ["type"] = "output", ["stream"] = "stderr", ["text"] = $"The files the command made there were not copied back: {e.Message}\n" }); }
    }
}
