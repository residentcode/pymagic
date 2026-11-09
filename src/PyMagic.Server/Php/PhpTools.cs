using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// PHP for Laravel projects: where PHP and Composer are (the person's setting first, then PYMAGIC_PHP / PYMAGIC_COMPOSER, the PATH, and the places
/// the usual Windows installs put them: C:\php, XAMPP, Laragon, WAMP, Herd, Scoop, Chocolatey, Composer's own setup), whether a folder is a
/// Laravel project and of which version, Artisan's commands (`php artisan list --format=json`), and a new Laravel project made by Composer
/// (`composer create-project laravel/laravel`), as Laravel's documentation makes one.
/// </summary>
public static class PhpTools
{
    /// <summary>PHP: the path given (a php.exe, or a folder that has one), else the first PHP found; null when there is none.</summary>
    public static string? FindPhp(string? configured)
    {
        foreach (var c in Candidates(configured, "PYMAGIC_PHP", "php"))
            if (Works(c)) return c;
        return null;
    }

    static IEnumerable<string> Candidates(string? configured, string variable, string program)
    {
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string c = configured.Trim().Trim('"');
            yield return Directory.Exists(c) ? Path.Combine(c, program + ".exe") : c;
        }
        if (Environment.GetEnvironmentVariable(variable) is { Length: > 0 } fromEnv) yield return fromEnv;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var ext in OperatingSystem.IsWindows() ? new[] { ".exe", ".bat", ".cmd" } : new[] { "" })
                yield return Path.Combine(dir.Trim('"'), program + ext);
        if (!OperatingSystem.IsWindows() || program != "php") yield break;
        string home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        string programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        yield return @"C:\php\php.exe";
        yield return @"C:\xampp\php\php.exe";
        foreach (var dir in Newest(@"C:\laragon\bin\php")) yield return Path.Combine(dir, "php.exe");
        foreach (var dir in Newest(@"C:\wamp64\bin\php")) yield return Path.Combine(dir, "php.exe");
        foreach (var dir in Newest(Path.Combine(home, ".config", "herd", "bin"))) yield return Path.Combine(dir, "php.exe");
        yield return Path.Combine(home, "scoop", "apps", "php", "current", "php.exe");
        foreach (var dir in Newest(@"C:\tools")) if (Path.GetFileName(dir).StartsWith("php", StringComparison.OrdinalIgnoreCase)) yield return Path.Combine(dir, "php.exe");
        yield return Path.Combine(programData, "chocolatey", "bin", "php.exe");
    }

    /// <summary>The folders under a folder, the newest version first (php-8.4.1 before php-8.3.9).</summary>
    static IEnumerable<string> Newest(string folder)
    {
        try { return Directory.Exists(folder) ? Directory.GetDirectories(folder).OrderByDescending(d => Regex.Replace(Path.GetFileName(d), @"\d+", m => m.Value.PadLeft(6, '0'))).ToList() : new List<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return new List<string>(); }
    }

    static bool Works(string path) => File.Exists(path);

    /// <summary>
    /// Composer as it is started: a composer.phar is run by PHP, a composer.bat / composer.exe by itself. The setting, PYMAGIC_COMPOSER, the PATH,
    /// Composer-Setup's folder, then a composer.phar beside PHP. Null when there is none.
    /// </summary>
    public static (string Program, string[] Prefix)? FindComposer(string? configured, string? php)
    {
        var tried = new List<string>();
        if (!string.IsNullOrWhiteSpace(configured)) tried.Add(configured.Trim().Trim('"'));
        if (Environment.GetEnvironmentVariable("PYMAGIC_COMPOSER") is { Length: > 0 } env) tried.Add(env);
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
            foreach (var name in OperatingSystem.IsWindows() ? new[] { "composer.bat", "composer.exe", "composer.cmd", "composer.phar" } : new[] { "composer", "composer.phar" })
                tried.Add(Path.Combine(dir.Trim('"'), name));
        if (OperatingSystem.IsWindows())
            tried.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ComposerSetup", "bin", "composer.bat"));
        if (php != null) tried.Add(Path.Combine(Path.GetDirectoryName(php)!, "composer.phar"));
        foreach (var c in tried)
        {
            if (!File.Exists(c)) continue;
            if (c.EndsWith(".phar", StringComparison.OrdinalIgnoreCase)) return php == null ? null : (php, new[] { c });
            return (c, Array.Empty<string>());
        }
        return null;
    }

    /// <summary>A Laravel project: its artisan file and laravel/framework among composer.json's requirements. Null when the folder is not one.</summary>
    public static JsonObject? Laravel(string root)
    {
        if (!File.Exists(Path.Combine(root, "artisan")) || !File.Exists(Path.Combine(root, "composer.json"))) return null;
        try
        {
            var composer = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "composer.json")));
            if (composer?["require"]?["laravel/framework"] is null) return null;
            string? version = null;
            // the version installed: Application::VERSION of vendor's framework, else what composer.json asks for
            string app = Path.Combine(root, "vendor", "laravel", "framework", "src", "Illuminate", "Foundation", "Application.php");
            if (File.Exists(app) && Regex.Match(File.ReadAllText(app), @"const VERSION = '([^']+)'") is { Success: true } m) version = m.Groups[1].Value;
            return new JsonObject
            {
                ["version"] = version,
                ["requires"] = composer["require"]!["laravel/framework"]!.ToString(),
                ["installed"] = Directory.Exists(Path.Combine(root, "vendor")),
                ["views"] = "resources/views",
                ["routes"] = new JsonArray(Directory.Exists(Path.Combine(root, "routes")) ? Directory.GetFiles(Path.Combine(root, "routes"), "*.php").Select(f => (JsonNode)("routes/" + Path.GetFileName(f))).ToArray() : Array.Empty<JsonNode>()),
            };
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { return null; }
    }

    static readonly Dictionary<string, (long Stamp, bool Loaded, string? Version, string ExtDir)> XdebugCache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Xdebug for a PHP: whether php.ini loads it already (and its version), else its DLL: the path given (Settings > PHP), else a php_xdebug*.dll
    /// in PHP's extension folder (as Xdebug's own install steps put it). Error says what to do when there is none.
    /// </summary>
    public static (bool Loaded, string? Dll, string? Version, string? Error) FindXdebug(string php, string? configured)
    {
        string? phpDir = Path.GetDirectoryName(Path.GetFullPath(php));
        long stamp = File.Exists(php) ? File.GetLastWriteTimeUtc(php).Ticks : 0;
        string iniFile = Path.Combine(phpDir ?? "", "php.ini");
        if (File.Exists(iniFile)) stamp ^= File.GetLastWriteTimeUtc(iniFile).Ticks;
        (long Stamp, bool Loaded, string? Version, string ExtDir) info;
        lock (XdebugCache) XdebugCache.TryGetValue(php, out info);
        if (info.Stamp != stamp || info.ExtDir == null)
        {
            var (_, output, _) = Run(php, new[] { "-r", "echo extension_loaded('xdebug') ? 'loaded ' . phpversion('xdebug') : 'none', \"\\n\", ini_get('extension_dir');" }, phpDir ?? ".", 20);
            var lines = output.Replace("\r", "").Split('\n');
            bool loaded = lines[0].StartsWith("loaded");
            info = (stamp, loaded, loaded ? lines[0][7..].Trim() : null, lines.Length > 1 ? lines[1].Trim() : "");
            lock (XdebugCache) XdebugCache[php] = info;
        }
        if (info.Loaded) return (true, null, info.Version, null);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string c = configured.Trim().Trim('"');
            return File.Exists(c) ? (false, Path.GetFullPath(c), null, null) : (false, null, null, $"The Xdebug of Settings > PHP is not there: {c}");
        }
        var folders = new List<string>();
        if (info.ExtDir != "") folders.Add(Path.IsPathRooted(info.ExtDir) ? info.ExtDir : Path.Combine(phpDir ?? "", info.ExtDir));
        if (phpDir != null) folders.Add(Path.Combine(phpDir, "ext"));
        foreach (var folder in folders.Where(Directory.Exists))
        {
            var dll = Directory.GetFiles(folder, "php_xdebug*.dll").OrderByDescending(f => f, StringComparer.OrdinalIgnoreCase).FirstOrDefault();
            if (dll != null) return (false, dll, null, null);
        }
        return (false, null, null, "Debugging PHP needs Xdebug, which this PHP does not have. Get the php_xdebug DLL made for your PHP from xdebug.org/download "
            + $"(its wizard says which one), put it in {folders.FirstOrDefault() ?? "PHP's ext folder"}, and debug again; or name it in Settings > PHP.");
    }

    /// <summary>Runs a program and gives its exit code, standard output and standard error (in at most <paramref name="seconds"/>).</summary>
    public static (int Code, string Out, string Err) Run(string program, IEnumerable<string> args, string workDir, int seconds = 60)
    {
        var psi = new ProcessStartInfo(program) { WorkingDirectory = workDir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        using var p = Process.Start(psi) ?? throw new ServiceException($"cannot start {program}");
        var output = p.StandardOutput.ReadToEndAsync();
        var error = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(seconds * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException($"{Path.GetFileName(program)} did not finish in {seconds} seconds"); }
        return (p.ExitCode, output.Result, error.Result);
    }

    /// <summary>
    /// Artisan's commands, as `php artisan list --format=json` gives them: [{ name, description, usage, arguments, options }], the hidden ones
    /// left out. Kept until composer.lock or the app's commands change.
    /// </summary>
    public static JsonArray ArtisanCommands(string php, string root)
    {
        string stamp = string.Join("|", new[] { Path.Combine(root, "composer.lock"), Path.Combine(root, "app", "Console"), Path.Combine(root, "routes", "console.php") }
            .Select(p => File.Exists(p) ? File.GetLastWriteTimeUtc(p).Ticks : Directory.Exists(p) ? Directory.GetLastWriteTimeUtc(p).Ticks : 0));
        lock (Cache)
            if (Cache.TryGetValue(root, out var cached) && cached.Stamp == stamp) return (JsonArray)cached.List.DeepClone();
        var (code, output, error) = Run(php, new[] { "artisan", "list", "--format=json" }, root, 120);
        if (code != 0) throw new ServiceException("php artisan list: " + (error.Trim().Split('\n').LastOrDefault() ?? $"exit code {code}"));
        var list = new JsonArray();
        var node = JsonNode.Parse(output[output.IndexOf('{')..]);
        foreach (var c in node?["commands"] as JsonArray ?? new JsonArray())
        {
            if (c?["hidden"]?.GetValue<bool>() == true) continue;
            var args = new JsonArray();
            foreach (var (name, def) in c?["definition"]?["arguments"] as JsonObject ?? new JsonObject())
                args.Add(new JsonObject { ["name"] = name, ["required"] = def?["is_required"]?.GetValue<bool>() == true, ["description"] = def?["description"]?.ToString() });
            var opts = new JsonArray();
            foreach (var (name, def) in c?["definition"]?["options"] as JsonObject ?? new JsonObject())
            {
                if (name is "help" or "quiet" or "verbose" or "version" or "ansi" or "no-ansi" or "no-interaction" or "env") continue;
                opts.Add(new JsonObject { ["name"] = "--" + name, ["value"] = def?["accept_value"]?.GetValue<bool>() == true, ["description"] = def?["description"]?.ToString() });
            }
            list.Add(new JsonObject { ["name"] = c?["name"]?.ToString(), ["description"] = c?["description"]?.ToString(), ["usage"] = (c?["usage"] as JsonArray)?.FirstOrDefault()?.ToString(), ["arguments"] = args, ["options"] = opts });
        }
        lock (Cache) Cache[root] = (stamp, list);
        return (JsonArray)list.DeepClone();
    }

    static readonly Dictionary<string, (string Stamp, JsonArray List)> Cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A new Laravel project in <paramref name="folder"/> (a folder not there yet, or empty): `composer create-project laravel/laravel`, which also
    /// makes its .env and app key and, with Laravel 11 and later, its SQLite database. <paramref name="say"/> gets Composer's lines as they come.
    /// </summary>
    public static void CreateLaravel(string folder, string? phpSetting, string? composerSetting, Action<string>? say = null)
    {
        string php = FindPhp(phpSetting) ?? throw new ServiceException("PHP was not found: install PHP 8.2 or later, or name its php.exe in Settings > PHP.");
        var composer = FindComposer(composerSetting, php) ?? throw new ServiceException("Composer was not found: install Composer (getcomposer.org), or name it in Settings > PHP.");
        if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any()) throw new ServiceException($"{folder} is there already and not empty.");
        string parent = Path.GetDirectoryName(Path.GetFullPath(folder))!;
        Directory.CreateDirectory(parent);
        var psi = new ProcessStartInfo(composer.Program) { WorkingDirectory = parent, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in composer.Prefix) psi.ArgumentList.Add(a);
        foreach (var a in new[] { "create-project", "laravel/laravel", Path.GetFileName(Path.GetFullPath(folder)), "--prefer-dist", "--no-interaction", "--no-ansi" }) psi.ArgumentList.Add(a);
        // Composer runs Laravel's scripts with PHP: the one found, first on the PATH
        psi.Environment["PATH"] = Path.GetDirectoryName(php) + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH");
        using var p = Process.Start(psi) ?? throw new ServiceException("Composer could not be started.");
        var tail = new System.Collections.Concurrent.ConcurrentQueue<string>();
        void Read(StreamReader r) { string? line; while ((line = r.ReadLine()) != null) { say?.Invoke(line); tail.Enqueue(line); while (tail.Count > 30) tail.TryDequeue(out _); } }
        var a1 = Task.Run(() => Read(p.StandardOutput));
        var a2 = Task.Run(() => Read(p.StandardError));
        if (!p.WaitForExit(20 * 60 * 1000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("composer create-project did not finish in 20 minutes"); }
        Task.WaitAll(a1, a2);
        if (p.ExitCode != 0)
            throw new ServiceException("composer create-project laravel/laravel failed: " + string.Join(" ", tail.Where(l => l.Trim().Length > 0).TakeLast(3)));
    }
}
