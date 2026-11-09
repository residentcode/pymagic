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
        if (!Target.IsLocal) { Error("PHP runs on this computer only."); return; }
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
}
