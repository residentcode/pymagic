using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHP under the debugger: run with Xdebug's step debugger on (php.ini's own Xdebug, or the DLL found or named in Settings > PHP), which
/// connects to an XdebugBridge; the bridge is this run's debugger helper, as pm_debug.py is Python's.
/// </summary>
public sealed partial class RunSession
{
    /// <summary>Xdebug's DLL as Settings > PHP names it, or null for the one found.</summary>
    public string? Xdebug { get; set; }

    void StartPhpDebug(string root, string php, List<string> phpArgs, IReadOnlyList<string>? artisan, IReadOnlyDictionary<string, JsonArray>? breakpoints)
    {
        (bool Loaded, string? Dll, string? Version, string? Error) xdebug;
        try { xdebug = PhpTools.FindXdebug(php, Xdebug); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (xdebug.Error != null) { Error(xdebug.Error); return; }

        // a Laravel server: its Blade templates compiled first as the server compiles them (pm_bladecompile.php), so a breakpoint of a template
        // is in its compiled file for the first request too
        if (artisan is { Count: > 0 } && artisan[0] == "serve" && PhpTools.Laravel(root) != null)
            try { PhpTools.Run(php, new[] { ExtractHelper("pm_bladecompile.php") }, root, 120); }
            catch (Exception e) when (e is ServiceException or System.ComponentModel.Win32Exception) { }

        var bridge = new XdebugBridge(root, AllCode);
        // the settings in a folder of their own that PHP_INI_SCAN_DIR adds: every PHP process of the run reads them (artisan serve's server too)
        string iniDir = Path.Combine(Path.GetTempPath(), "pymagic", "xdebug-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(6)));
        Directory.CreateDirectory(iniDir);
        File.WriteAllText(Path.Combine(iniDir, "zz-pymagic-xdebug.ini"), bridge.IniText(xdebug.Dll));

        string token = Convert.ToHexString(RandomNumberGenerator.GetBytes(16));
        _listener = new TcpListener(IPAddress.Loopback, 0);
        _listener.Start();
        int port = ((IPEndPoint)_listener.LocalEndpoint).Port;

        var psi = new ProcessStartInfo(php) { WorkingDirectory = WorkingDirectory ?? root };
        foreach (var a in phpArgs) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH"));
        string? scan = psi.Environment.TryGetValue("PHP_INI_SCAN_DIR", out var s) ? s : null;
        psi.Environment["PHP_INI_SCAN_DIR"] = string.IsNullOrEmpty(scan) ? iniDir : scan + Path.PathSeparator + iniDir;
        // the person's own Xdebug variables would win over the settings
        foreach (var v in new[] { "XDEBUG_MODE", "XDEBUG_CONFIG", "XDEBUG_SESSION", "XDEBUG_TRIGGER" }) psi.Environment.Remove(v);
        _runId = null;
        bridge.Connect(port, token);
        StartProcess(psi, root, token, breakpoints);
        var process = _process;
        Task.Run(async () =>
        {
            try { if (process != null) await process.WaitForExitAsync(); }
            catch (InvalidOperationException) { }
            bridge.Dispose();
            try { Directory.Delete(iniDir, true); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        });
        if (xdebug.Dll != null && xdebug.Version == null)
            _emit(new JsonObject { ["type"] = "output", ["stream"] = "stdout", ["text"] = $"Debugging with Xdebug ({Path.GetFileName(xdebug.Dll)}).\n" });
    }
}
