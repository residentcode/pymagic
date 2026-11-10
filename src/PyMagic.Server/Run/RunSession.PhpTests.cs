using System.Diagnostics;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// PHP's tests in the Tests panel: the project's PHPUnit or Pest (vendor/bin) run with --teamcity by the PHP of Settings > PHP, on this computer
/// or on the machine reached by SSH that it names; their messages become the panel's (PhpTests.cs).
/// </summary>
public sealed partial class RunSession
{
    /// <summary>Whether a run of the Tests panel is PHP's: PHPUnit or Pest chosen, or (auto) tests of .php files, or a PHP project without Python tests.</summary>
    public static bool IsPhpTestRun(string root, IReadOnlyList<string> ids, string framework)
    {
        if (framework is "phpunit" or "pest") return true;
        if (framework != "auto") return false;
        if (ids.Count > 0) return ids.All(i => i.Split("::")[0].EndsWith(".php", StringComparison.OrdinalIgnoreCase));
        return PhpTests.Applies(root) && TestDiscovery.Discover(root).Count == 0;
    }

    /// <summary>
    /// Runs PHP's tests with these ids (none: all of them) with PHPUnit or Pest ("auto": Pest when the project has it); under the debugger
    /// (Xdebug, as a PHP script's Debug) when <paramref name="debug"/>, their breakpoints stopping.
    /// </summary>
    public void StartPhpTests(string root, IReadOnlyList<string> ids, string framework, bool debug = false, IReadOnlyDictionary<string, JsonArray>? breakpoints = null)
    {
        _root = root;
        IsDebug = debug;
        string runner = framework is "phpunit" or "pest" ? framework : PhpTests.Runner(root);
        if (!File.Exists(Path.Combine(root, "vendor", "bin", runner)))
        {
            Error($"{(runner == "pest" ? "Pest" : "PHPUnit")} is not in the project (vendor/bin/{runner}): run Composer's install, or require it (composer require --dev {(runner == "pest" ? "pestphp/pest" : "phpunit/phpunit")}).");
            return;
        }
        if (ids.Any(i => i.Split("::")[0] is var f && (f.Contains("..") || Path.IsPathRooted(f)))) { Error("a test outside the project"); return; }
        var args = new List<string> { "vendor/bin/" + runner };
        args.AddRange(PhpTests.Arguments(runner, ids));

        // Sail: the tests in its container (the project shared), their places /var/www/html/… the project's files here
        SailTarget? sail;
        try { sail = SailTarget.Of(root, Php); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (sail != null)
        {
            _teamCity = new TeamCityReader(path => RelOfTest(root, path.StartsWith('/') ? sail.ToHost(path) : path));
            StartSailPhp(root, sail, null, null, debug, Array.Empty<string>(), breakpoints, program: args);
            return;
        }
        if (PhpTools.Remote(Php) is { } remote)
        {
            var ssh = remote.Target;
            _teamCity = new TeamCityReader(path => RelOfTest(root, path.StartsWith('/') ? ssh.ToHost(path) : path));
            StartRemotePhp(root, ssh, remote.Php, null, null, debug, Array.Empty<string>(), breakpoints, program: args);
            return;
        }
        if (!Target.IsLocal) Target = ExecutionTarget.Local;
        string? php = PhpTools.FindPhp(Php);
        if (php == null) { Error("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP."); return; }
        _teamCity = new TeamCityReader(path => RelOfTest(root, path));
        if (debug) { StartPhpDebug(root, php, args, null, breakpoints); return; }
        var psi = new ProcessStartInfo(php) { WorkingDirectory = root };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH"));
        _runId = null;
        StartProcess(psi, root, null, null);
    }

    /// <summary>A path a test names (absolute, or relative to the project) as the project's relative path, or null when it is not the project's.</summary>
    static string? RelOfTest(string root, string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(root, path));
            return IsInside(full, root) && File.Exists(full) ? Path.GetRelativePath(root, full).Replace('\\', '/') : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
