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
        // a .php file's tests, or a folder of PHP test files (a PHP tests configuration's tests/Unit)
        if (ids.Count > 0) return ids.All(i => i.Split("::")[0] is var f && (f.EndsWith(".php", StringComparison.OrdinalIgnoreCase) || IsPhpTestFolder(root, f)));
        return PhpTests.Applies(root) && TestDiscovery.Discover(root).Count == 0;
    }

    /// <summary>
    /// Runs PHP's tests with these ids (none: all of them) with PHPUnit or Pest ("auto": Pest when the project has it); under the debugger
    /// (Xdebug, as a PHP script's Debug) when <paramref name="debug"/>, their breakpoints stopping; with <paramref name="coverage"/>, the lines
    /// they ran measured (Xdebug's coverage mode, or PCOV) and kept as a coverage run of the Tests panel (.pymagic/coverage.json and a suite).
    /// </summary>
    public void StartPhpTests(string root, IReadOnlyList<string> ids, string framework, bool debug = false, IReadOnlyDictionary<string, JsonArray>? breakpoints = null, bool coverage = false)
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
        if (coverage && !debug)
        {
            args.AddRange(PhpTests.CoverageArguments(root));
            try { Directory.CreateDirectory(Path.Combine(root, ".pymagic")); File.Delete(Path.Combine(root, PhpTests.CloverFile)); } catch (IOException) { }
            // Xdebug in its coverage mode (its variable wins over php.ini, and over Sail's XDEBUG_MODE=off); PCOV needs nothing
            ExtraEnvironment = new Dictionary<string, string>(ExtraEnvironment ?? new Dictionary<string, string>()) { ["XDEBUG_MODE"] = "coverage" };
        }

        // Sail: the tests in its container (the project shared), their places /var/www/html/… the project's files here
        SailTarget? sail;
        try { sail = SailTarget.Of(root, Php); }
        catch (ServiceException e) { Error(e.Message); return; }
        if (sail != null)
        {
            _teamCity = new TeamCityReader(path => RelOfTest(root, path.StartsWith('/') ? sail.ToHost(path) : path));
            if (coverage && !debug) KeepPhpCoverage(root, ids, path => RelOfTest(root, path.StartsWith('/') ? sail.ToHost(path) : path), null);
            StartSailPhp(root, sail, null, null, debug, Array.Empty<string>(), breakpoints, program: args);
            return;
        }
        if (PhpTools.Remote(Php) is { } remote)
        {
            var ssh = remote.Target;
            _teamCity = new TeamCityReader(path => RelOfTest(root, path.StartsWith('/') ? ssh.ToHost(path) : path));
            if (coverage && !debug) KeepPhpCoverage(root, ids, path => RelOfTest(root, path.StartsWith('/') ? ssh.ToHost(path) : path), ssh);
            StartRemotePhp(root, ssh, remote.Php, null, null, debug, Array.Empty<string>(), breakpoints, program: args);
            return;
        }
        if (!Target.IsLocal) Target = ExecutionTarget.Local;
        string? php = PhpTools.FindPhp(Php);
        if (php == null) { Error("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP."); return; }
        _teamCity = new TeamCityReader(path => RelOfTest(root, path));
        if (debug) { StartPhpDebug(root, php, args, null, breakpoints); return; }
        if (coverage)
        {
            // Xdebug loaded for the run when php.ini does not load it (its DLL in PHP's ext folder, or Settings > PHP's), else PCOV when loaded
            var xdebug = PhpTools.FindXdebug(php, Xdebug);
            if (xdebug.Error != null && !PhpTools.Run(php, new[] { "-r", "echo extension_loaded('pcov') ? 'pcov' : '';" }, root, 20).Out.Contains("pcov"))
            {
                Error("Coverage of PHP's tests needs Xdebug or PCOV, which this PHP has neither of: get the php_xdebug DLL made for your PHP from xdebug.org/download, "
                    + "put it in PHP's ext folder (or name it in Settings > PHP), and run with coverage again.");
                return;
            }
            if (xdebug.Error == null && !xdebug.Loaded && xdebug.Dll != null) args.InsertRange(0, new[] { "-d", "zend_extension=" + xdebug.Dll });
            KeepPhpCoverage(root, ids, path => RelOfTest(root, path), null);
        }
        var psi = new ProcessStartInfo(php) { WorkingDirectory = root };
        foreach (var a in args) psi.ArgumentList.Add(a);
        foreach (var (key, value) in ExtraEnvironment ?? new Dictionary<string, string>()) psi.Environment[key] = value;
        psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + (psi.Environment.TryGetValue("PATH", out var p) ? p : Environment.GetEnvironmentVariable("PATH"));
        _runId = null;
        StartProcess(psi, root, null, null);
    }

    static bool IsPhpTestFolder(string root, string rel)
    {
        if (rel.Contains("..") || Path.IsPathRooted(rel)) return false;
        string dir = Path.Combine(root, rel.TrimEnd('/', '\\'));
        try { return Directory.Exists(dir) && Directory.EnumerateFiles(dir, "*Test.php", SearchOption.AllDirectories).Any(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// When the run ends: its Clover report (fetched from the SSH machine first) made the Tests panel's coverage, kept as a suite; a run that
    /// wrote none (stopped, or a test that failed to start) leaves the last coverage as it was.
    /// </summary>
    void KeepPhpCoverage(string root, IReadOnlyList<string> ids, Func<string, string?> relOf, SshTarget? ssh)
    {
        string clover = Path.Combine(root, PhpTests.CloverFile);
        string label = CoverageSuites.LabelOf(ids);
        _afterExit = () =>
        {
            try
            {
                if (ssh != null) ssh.Fetch(ssh.ToTarget(clover), clover);
                if (!File.Exists(clover)) { Say("No coverage was written: PHPUnit says why above (a driver it has not, or no code to measure).\n"); return; }
                var data = PhpTests.CloverToCoverage(File.ReadAllText(clover), relOf);
                File.WriteAllText(CoveragePath(root), data.ToJsonString());
                CoverageSuites.Keep(root, label);
            }
            catch (Exception e) when (e is IOException or System.Xml.XmlException or ServiceException or UnauthorizedAccessException) { Say($"The coverage could not be read: {e.Message}\n"); }
        };
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
