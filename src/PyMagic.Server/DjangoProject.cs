using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A Django app of the project: its folder, whether it has models, its migrations and the management commands it adds.</summary>
/// <param name="Module">Its import name from the folder of manage.py (`shop`, `apps.shop`).</param>
/// <param name="Installed">Whether INSTALLED_APPS lists it (its module, or an AppConfig in its apps module); null when the settings do not say.</param>
public sealed record DjangoApp(string Name, string Path, bool HasModels, IReadOnlyList<DjangoMigration> Migrations, IReadOnlyList<string> Commands, string? Module = null, bool? Installed = null);

/// <summary>One migration file of an app (<c>0002_auto.py</c>): the number and the name that follows it.</summary>
public sealed record DjangoMigration(string Name, string Path, int Number);

/// <summary>What the folder tells about a Django project without running it: <c>manage.py</c>, the settings module, the apps with their migrations and commands.</summary>
/// <param name="TemplateDirs">The folders TEMPLATES['DIRS'] names in the settings (paths from the project folder), found as written there.</param>
/// <param name="SettingsChoices">The settings modules of the project (`proj.settings`, `proj.settings.dev`, ...), for commands to be run with another one.</param>
/// <param name="InstalledApps">The entries of INSTALLED_APPS, read from the settings files as written (with `*_APPS` lists added together); null when none is found.</param>
public sealed record DjangoProject(bool Found, string? Manage, string? SettingsModule, IReadOnlyList<DjangoApp> Apps, IReadOnlyList<string>? TemplateDirs = null,
    IReadOnlyList<string>? SettingsChoices = null, IReadOnlyList<string>? InstalledApps = null)
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".venv", "venv", "env", "node_modules", "__pycache__", ".tox", ".idea", ".pymagic", "site-packages", "build", "dist", "static", "staticfiles", "media",
    };

    static readonly Regex Settings = new(@"DJANGO_SETTINGS_MODULE['""]\s*,\s*['""](?<m>[\w.]+)['""]", RegexOptions.Compiled);
    static readonly Regex MigrationFile = new(@"^(?<n>\d{4,})_(?<rest>\w+)\.py$", RegexOptions.Compiled);

    public static readonly DjangoProject None = new(false, null, null, Array.Empty<DjangoApp>());

    /// <summary>Looks for <c>manage.py</c> in the folder and the ones below it (two levels), then for the apps: folders with an <c>apps.py</c> or models next to an <c>__init__.py</c>.</summary>
    /// <remarks>The Django page of the settings (<see cref="DjangoConfig"/>) can turn Django off, name the folder to look in, the manage.py and the settings module.</remarks>
    public static DjangoProject Scan(string root, DjangoConfig? config = null)
    {
        config ??= DjangoConfig.Current;
        if (!config.Enabled) return None;
        string? manage = null;
        if (config.Manage != null && File.Exists(System.IO.Path.Combine(root, config.Manage))) manage = System.IO.Path.Combine(root, config.Manage);
        else
        {
            string start = config.Root != null && Directory.Exists(System.IO.Path.Combine(root, config.Root)) ? System.IO.Path.Combine(root, config.Root) : root;
            foreach (var dir in Folders(start, 2))
            {
                string candidate = System.IO.Path.Combine(dir, "manage.py");
                if (File.Exists(candidate) && Mentions(candidate, "django")) { manage = candidate; break; }
            }
        }
        if (manage == null) return None;

        string? settingsModule = config.Settings;
        if (settingsModule == null)
            try { settingsModule = Settings.Match(File.ReadAllText(manage)) is { Success: true } m ? m.Groups["m"].Value : null; }
            catch (IOException) { }

        string baseDir = System.IO.Path.GetDirectoryName(manage)!;
        var choices = SettingsModulesOf(baseDir);
        // the one manage.py names first
        if (settingsModule != null) { choices.Remove(settingsModule); choices.Insert(0, settingsModule); }
        var installed = InstalledAppsOf(baseDir, choices);
        var apps = new List<DjangoApp>();
        foreach (var dir in Folders(root, 4))
        {
            if (!File.Exists(System.IO.Path.Combine(dir, "__init__.py"))) continue;
            bool hasModels = File.Exists(System.IO.Path.Combine(dir, "models.py")) || Directory.Exists(System.IO.Path.Combine(dir, "models"));
            bool appConfig = File.Exists(System.IO.Path.Combine(dir, "apps.py")) && Mentions(System.IO.Path.Combine(dir, "apps.py"), "AppConfig");
            if (!hasModels && !appConfig) continue;
            string module = Rel(baseDir, dir).Replace('/', '.');
            bool? isInstalled = installed == null || module.StartsWith("..", StringComparison.Ordinal) ? null : installed.Any(e => e == module || e.StartsWith(module + ".apps.", StringComparison.Ordinal));
            apps.Add(new DjangoApp(System.IO.Path.GetFileName(dir), Rel(root, dir), hasModels, MigrationsOf(root, dir), CommandsOf(dir), module, isInstalled));
        }
        return new DjangoProject(true, Rel(root, manage), settingsModule, apps.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList(),
            TemplateDirsOf(root, baseDir, settingsModule), choices, installed);
    }

    /// <summary>The settings modules below the folder of manage.py: each `settings.py`, and each module of a `settings` package.</summary>
    static List<string> SettingsModulesOf(string baseDir)
    {
        var found = new List<string>();
        foreach (var dir in Folders(baseDir, 3))
        {
            if (File.Exists(System.IO.Path.Combine(dir, "settings.py"))) found.Add((Rel(baseDir, dir) is "." ? "" : Rel(baseDir, dir).Replace('/', '.') + ".") + "settings");
            if (System.IO.Path.GetFileName(dir) == "settings" && File.Exists(System.IO.Path.Combine(dir, "__init__.py")))
            {
                string package = Rel(baseDir, dir).Replace('/', '.');
                IEnumerable<string> files;
                try { files = Directory.EnumerateFiles(dir, "*.py").ToList(); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
                foreach (var f in files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
                {
                    string stem = System.IO.Path.GetFileNameWithoutExtension(f);
                    found.Add(stem == "__init__" ? package : package + "." + stem);
                }
            }
        }
        return found.Where(m => !m.StartsWith('.')).Distinct().ToList();
    }

    // `INSTALLED_APPS = [`, `LOCAL_APPS += (`, ...: the assignment and the indented lines that go on with it
    static readonly Regex AppsList = new(@"^(?<name>\w*APPS)\s*\+?=\s*(?<value>[^\n]*(?:\n[ \t]+[^\n]*|\n[\]\)][^\n]*)*)", RegexOptions.Compiled | RegexOptions.Multiline);

    /// <summary>
    /// The strings of INSTALLED_APPS in the settings files (and of the `*_APPS` lists it is often built from: DJANGO_APPS + LOCAL_APPS), with `+=` of
    /// split settings; null when no settings file assigns INSTALLED_APPS.
    /// </summary>
    static List<string>? InstalledAppsOf(string baseDir, List<string> settingsModules)
    {
        var entries = new List<string>();
        bool any = false;
        foreach (var module in settingsModules)
        {
            string file = System.IO.Path.Combine(new[] { baseDir }.Concat(module.Split('.')).ToArray()) + ".py";
            if (!File.Exists(file)) file = System.IO.Path.Combine(System.IO.Path.Combine(new[] { baseDir }.Concat(module.Split('.')).ToArray()), "__init__.py");
            string text;
            try { text = File.ReadAllText(file).Replace("\r\n", "\n"); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (Match m in AppsList.Matches(text))
            {
                if (m.Groups["name"].Value == "INSTALLED_APPS") any = true;
                foreach (Match s in StringLiteral.Matches(m.Groups["value"].Value)) entries.Add(s.Groups["s"].Value);
            }
        }
        return any ? entries.Distinct().ToList() : null;
    }

    static readonly Regex DirsList = new(@"['""]DIRS['""]\s*:\s*\[(?<items>[^\]]*)\]", RegexOptions.Compiled | RegexOptions.Singleline);
    static readonly Regex StringLiteral = new(@"['""](?<s>[^'""\r\n]+)['""]", RegexOptions.Compiled);

    /// <summary>
    /// The folders TEMPLATES['DIRS'] names, read from the settings file as written: `BASE_DIR / "templates"`, `os.path.join(BASE_DIR, "site", "tpl")`
    /// and plain strings, each from the folder of manage.py (Django's BASE_DIR); only the ones that exist.
    /// </summary>
    public static List<string> TemplateDirsOf(string root, string baseDir, string? settingsModule)
    {
        var found = new List<string>();
        if (settingsModule == null) return found;
        string file = System.IO.Path.Combine(new[] { baseDir }.Concat(settingsModule.Split('.')).ToArray()) + ".py";
        if (!File.Exists(file)) file = System.IO.Path.Combine(System.IO.Path.Combine(new[] { baseDir }.Concat(settingsModule.Split('.')).ToArray()), "__init__.py");
        string text;
        try { text = File.ReadAllText(file); } catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return found; }
        foreach (Match list in DirsList.Matches(text))
            foreach (var item in TopLevelItems(list.Groups["items"].Value))
            {
                // the strings of one entry are the parts of one path: BASE_DIR / "a" / "b", or os.path.join(BASE_DIR, "a", "b")
                var parts = StringLiteral.Matches(item).Select(m => m.Groups["s"].Value).ToList();
                if (parts.Count == 0) continue;
                string full = System.IO.Path.GetFullPath(System.IO.Path.Combine(new[] { baseDir }.Concat(parts).ToArray()));
                if (Directory.Exists(full) && full.StartsWith(System.IO.Path.GetFullPath(root), StringComparison.OrdinalIgnoreCase)) found.Add(Rel(root, full));
            }
        return found.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The entries of a list's text, split at its own commas (not those inside a call such as os.path.join(a, b)).</summary>
    static IEnumerable<string> TopLevelItems(string text)
    {
        int depth = 0, start = 0;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') depth--;
            else if (c == ',' && depth == 0) { yield return text[start..i]; start = i + 1; }
        }
        yield return text[start..];
    }

    static IReadOnlyList<DjangoMigration> MigrationsOf(string root, string app)
    {
        string dir = System.IO.Path.Combine(app, "migrations");
        if (!Directory.Exists(dir)) return Array.Empty<DjangoMigration>();
        return Directory.EnumerateFiles(dir, "*.py")
            .Select(f => (File: f, Match: MigrationFile.Match(System.IO.Path.GetFileName(f))))
            .Where(x => x.Match.Success)
            .Select(x => new DjangoMigration(System.IO.Path.GetFileNameWithoutExtension(x.File), Rel(root, x.File), int.Parse(x.Match.Groups["n"].Value)))
            .OrderBy(m => m.Number).ThenBy(m => m.Name, StringComparer.Ordinal).ToList();
    }

    static IReadOnlyList<string> CommandsOf(string app)
    {
        string dir = System.IO.Path.Combine(app, "management", "commands");
        if (!Directory.Exists(dir)) return Array.Empty<string>();
        return Directory.EnumerateFiles(dir, "*.py").Select(System.IO.Path.GetFileNameWithoutExtension)
            .Where(n => n != null && !n.StartsWith('_')).Select(n => n!).OrderBy(n => n, StringComparer.Ordinal).ToList();
    }

    static bool Mentions(string file, string word)
    {
        try { return File.ReadAllText(file).Contains(word, StringComparison.OrdinalIgnoreCase); }
        catch (IOException) { return false; }
    }

    static string Rel(string root, string path) => System.IO.Path.GetRelativePath(root, path).Replace('\\', '/');

    /// <summary>The folder and the ones below it down to a depth, shallow first, without the folders that hold no project code.</summary>
    static IEnumerable<string> Folders(string root, int depth)
    {
        var level = new List<string> { root };
        for (int d = 0; d <= depth && level.Count > 0; d++)
        {
            foreach (var dir in level) yield return dir;
            var next = new List<string>();
            foreach (var dir in level)
            {
                try
                {
                    foreach (var sub in Directory.EnumerateDirectories(dir).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
                    {
                        string name = System.IO.Path.GetFileName(sub);
                        if (!Skipped.Contains(name) && !name.StartsWith('.') && !name.EndsWith(".egg-info", StringComparison.Ordinal)) next.Add(sub);
                    }
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
            level = next;
        }
    }
}
