using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHP in Laravel Sail's container (SailTarget): a script, an Artisan command, Composer or the tests run there with docker compose exec, under
/// the debugger with Xdebug turned on for that process (XDEBUG_MODE and -d settings, PyMagic at host.docker.internal). Laravel's server is the
/// container's own (artisan serve under supervisord, on APP_PORT): Run brings the container up and shows its log; Debug makes it again with
/// Xdebug pointed at PyMagic (SAIL_XDEBUG_MODE and SAIL_XDEBUG_CONFIG over the .env's), and its end makes it again as the .env says. A
/// container PyMagic started is stopped again when the run ends.
/// </summary>
public sealed partial class RunSession
{
    static string XdebugConfig(int port) => $"client_host=host.docker.internal client_port={port} start_with_request=yes idekey=PYMAGIC";

    static IEnumerable<string> XdebugSettings(int port) => new[]
    {
        "-d", "xdebug.mode=debug", "-d", "xdebug.start_with_request=yes", "-d", "xdebug.client_host=host.docker.internal",
        "-d", $"xdebug.client_port={port}", "-d", "xdebug.idekey=PYMAGIC", "-d", "xdebug.control_socket=time", "-d", "xdebug.connect_timeout_ms=2000",
    };

    void Say(string text) => _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = text });

    /// <summary>
    /// PHP in Sail's container: a script or an Artisan command (<paramref name="program"/>: PHP's own arguments as they are, a test run's), under
    /// the debugger when <paramref name="debug"/>. artisan serve is the container's own server (<see cref="StartSailServer"/>).
    /// </summary>
    void StartSailPhp(string root, SailTarget sail, string? script, IReadOnlyList<string>? artisan, bool debug, IReadOnlyList<string> args, IReadOnlyDictionary<string, JsonArray>? breakpoints, IReadOnlyList<string>? program = null)
    {
        Target = sail;
        _bridged = debug;
        var phpArgs = new List<string>();
        if (program != null) phpArgs.AddRange(program);
        else if (artisan != null)
        {
            if (!File.Exists(Path.Combine(root, "artisan"))) { Error("There is no artisan file in the project folder: it is not a Laravel project."); return; }
            if (artisan.Count == 0 || !System.Text.RegularExpressions.Regex.IsMatch(artisan[0], @"^[\w:.-]+$")) { Error("an Artisan command: serve, migrate, make:model, …"); return; }
            if (artisan[0] == "serve") { StartSailServer(root, sail, debug, breakpoints); return; }
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
        try { if (!sail.Running()) { Say("Starting Sail's container (docker compose up -d)…\n"); sail.Up(); } }
        catch (ServiceException e) { Error(e.Message); return; }

        var env = new Dictionary<string, string>(ExtraEnvironment ?? new Dictionary<string, string>());
        XdebugBridge? bridge = null;
        string? token = null;
        if (debug)
        {
            bridge = new XdebugBridge(root, AllCode, sail);
            // Pause: the PHP there connects to Xdebug's control socket there
            bridge.Control = XdebugBridge.RemoteControl("php", command => { var (code, output, _) = sail.Exec(command, 15_000); return (code, output); });
            env["XDEBUG_MODE"] = "debug";
            env["XDEBUG_CONFIG"] = XdebugConfig(bridge.Port);
            phpArgs.InsertRange(0, XdebugSettings(bridge.Port));
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        var psi = sail.Start("php", WorkingDirectory ?? root, phpArgs, env);
        _runId = psi.Environment["PYMAGIC_RUN_ID"];
        if (bridge != null) bridge.Connect(((IPEndPoint)_listener!.LocalEndpoint).Port, token!);
        StartProcess(psi, root, token, breakpoints);
        var process = _process;
        if (bridge != null)
            Task.Run(async () =>
            {
                try { if (process != null) await process.WaitForExitAsync(); }
                catch (InvalidOperationException) { }
                bridge.Dispose();
            });
    }

    /// <summary>
    /// Laravel's server of Sail: the container's own. Its log is the run's output; under the debugger the container is made again with Xdebug
    /// pointed at PyMagic for every request, and made again as the .env says when the run ends.
    /// </summary>
    void StartSailServer(string root, SailTarget sail, bool debug, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        bool wasRunning;
        try { wasRunning = sail.Running(); }
        catch (ServiceException e) { Error(e.Message); return; }
        XdebugBridge? bridge = null;
        string? token = null;
        var env = new Dictionary<string, string>();
        if (debug)
        {
            bridge = new XdebugBridge(root, AllCode, sail);
            bridge.Control = XdebugBridge.RemoteControl("php", command => { var (code, output, _) = sail.Exec(command, 15_000); return (code, output); });
            env["SAIL_XDEBUG_MODE"] = "debug";
            env["SAIL_XDEBUG_CONFIG"] = XdebugConfig(bridge.Port);
            token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
            _listener = new TcpListener(IPAddress.Loopback, 0);
            _listener.Start();
        }
        try
        {
            if (debug) { Say("Making Sail's container again with Xdebug for PyMagic (docker compose up -d)…\n"); sail.Up(recreate: true, env); }
            else if (!wasRunning) { Say("Starting Sail's container (docker compose up -d)…\n"); sail.Up(); }
        }
        catch (ServiceException e) { bridge?.Dispose(); Error(e.Message); return; }

        // its Blade templates compiled first as the server compiles them, for the first request's breakpoints (as on this computer)
        if (debug && PhpTools.Laravel(root) != null)
            try
            {
                string code = File.ReadAllText(ExtractHelper("pm_bladecompile.php"));
                code = code.StartsWith("<?php") ? code[5..] : code;
                string b64 = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes(code));
                sail.Exec($"php -r \"eval(base64_decode('{b64}'));\"", 120_000);
            }
            catch (Exception e) when (e is ServiceException or IOException) { }

        var psi = sail.FollowLog();
        _runId = null;
        _afterExit = () =>
        {
            try
            {
                if (!wasRunning) { sail.Docker(new[] { "stop", SailTarget.Service }, 120_000); Say("Sail's container stopped.\n"); }
                else if (debug) { sail.Up(recreate: true); Say("Sail's container made again without PyMagic's Xdebug.\n"); }
            }
            catch (ServiceException e) { Say("Sail's container: " + e.Message + "\n"); }
        };
        if (bridge != null) bridge.Connect(((IPEndPoint)_listener!.LocalEndpoint).Port, token!);
        StartProcess(psi, root, token, breakpoints);
        var process = _process;
        if (bridge == null) { Say($"Laravel runs in Sail's container: {sail.AppUrl()}\n"); return; }
        Task.Run(() =>
        {
            // a session for every request of the server (which XDEBUG_CONFIG cannot ask for): the container's own PHP settings and the server
            // started again, once the debugger takes its sessions (the server's start is one); said when it listens
            try { sail.DebugServer(); Say($"Laravel runs in Sail's container: {sail.AppUrl()}\n"); }
            catch (ServiceException e) { Say(e.Message + "\n"); }
        });
        Task.Run(async () =>
        {
            try { if (process != null) await process.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
            bridge.Dispose();
        });
    }
}
