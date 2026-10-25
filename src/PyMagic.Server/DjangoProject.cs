using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A Django app of the project: its folder, whether it has models, its migrations and the management commands it adds.</summary>
public sealed record DjangoApp(string Name, string Path, bool HasModels, IReadOnlyList<DjangoMigration> Migrations, IReadOnlyList<string> Commands);

/// <summary>One migration file of an app (<c>0002_auto.py</c>): the number and the name that follows it.</summary>
public sealed record DjangoMigration(string Name, string Path, int Number);

/// <summary>What the folder tells about a Django project without running it: <c>manage.py</c>, the settings module, the apps with their migrations and commands.</summary>
public sealed record DjangoProject(bool Found, string? Manage, string? SettingsModule, IReadOnlyList<DjangoApp> Apps)
{
    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".venv", "venv", "env", "node_modules", "__pycache__", ".tox", ".idea", ".pymagic", "site-packages", "build", "dist", "static", "staticfiles", "media",
    };

    static readonly Regex Settings = new(@"DJANGO_SETTINGS_MODULE['""]\s*,\s*['""](?<m>[\w.]+)['""]", RegexOptions.Compiled);
    static readonly Regex MigrationFile = new(@"^(?<n>\d{4,})_(?<rest>\w+)\.py$", RegexOptions.Compiled);

    public static readonly DjangoProject None = new(false, null, null, Array.Empty<DjangoApp>());

    /// <summary>Looks for <c>manage.py</c> in the folder and the ones below it (two levels), then for the apps: folders with an <c>apps.py</c> or models next to an <c>__init__.py</c>.</summary>
    public static DjangoProject Scan(string root)
    {
        string? manage = null;
        foreach (var dir in Folders(root, 2))
        {
            string candidate = System.IO.Path.Combine(dir, "manage.py");
            if (File.Exists(candidate) && Mentions(candidate, "django")) { manage = candidate; break; }
        }
        if (manage == null) return None;

        string? settingsModule = null;
        try { settingsModule = Settings.Match(File.ReadAllText(manage)) is { Success: true } m ? m.Groups["m"].Value : null; }
        catch (IOException) { }

        var apps = new List<DjangoApp>();
        foreach (var dir in Folders(root, 4))
        {
            if (!File.Exists(System.IO.Path.Combine(dir, "__init__.py"))) continue;
            bool hasModels = File.Exists(System.IO.Path.Combine(dir, "models.py")) || Directory.Exists(System.IO.Path.Combine(dir, "models"));
            bool config = File.Exists(System.IO.Path.Combine(dir, "apps.py")) && Mentions(System.IO.Path.Combine(dir, "apps.py"), "AppConfig");
            if (!hasModels && !config) continue;
            apps.Add(new DjangoApp(System.IO.Path.GetFileName(dir), Rel(root, dir), hasModels, MigrationsOf(root, dir), CommandsOf(dir)));
        }
        return new DjangoProject(true, Rel(root, manage), settingsModule, apps.OrderBy(a => a.Path, StringComparer.OrdinalIgnoreCase).ToList());
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
