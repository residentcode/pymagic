using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Whether a project uses Tailwind CSS, for the Tailwind CSS plugin's class completion (as PyCharm's Tailwind plugin, it is offered only there):
/// tailwindcss in package.json, a tailwind.config file, a stylesheet that imports it (@import "tailwindcss", or @tailwind of version 3), or a page
/// that loads it from its CDN (Django and Flask templates often do). The stylesheet that imports it is given too: its @theme adds the project's
/// own colours and sizes to the classes.
/// </summary>
public static class Tailwind
{
    public record Use(bool Used, string? Css, string? Why);

    static readonly HashSet<string> Skipped = new(StringComparer.OrdinalIgnoreCase) { "node_modules", ".git", ".venv", "venv", "env", "__pycache__", ".pymagic", "dist", "build", ".next", ".idea" };
    static readonly Regex ImportsTailwind = new(@"@import\s+[""']tailwindcss[""']|@tailwind\s+(base|components|utilities)\b", RegexOptions.Compiled);
    static readonly Regex LoadsTailwind = new(@"cdn\.tailwindcss\.com|@tailwindcss/browser|/tailwindcss@|tailwind(\.min)?\.css", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>Looks through the project (at most 5,000 files, skipping node_modules, environments and build output).</summary>
    public static Use Detect(string root)
    {
        string? why = null, css = null;
        string package = Path.Combine(root, "package.json");
        if (File.Exists(package) && SafeRead(package).Contains("\"tailwindcss\"")) why = "package.json";
        int seen = 0;
        foreach (string file in Files(root))
        {
            if (++seen > 5000) break;
            string name = Path.GetFileName(file), ext = Path.GetExtension(file).ToLowerInvariant();
            if (name.StartsWith("tailwind.config.", StringComparison.OrdinalIgnoreCase)) why ??= name;
            else if (ext is ".css" or ".scss" or ".pcss" or ".postcss")
            {
                if (css == null && new FileInfo(file).Length < 512 * 1024 && ImportsTailwind.IsMatch(SafeRead(file)))
                {
                    css = Path.GetRelativePath(root, file).Replace('\\', '/');
                    why ??= css;
                }
            }
            else if (why == null && ext is ".html" or ".htm" or ".jinja" or ".jinja2" or ".j2" or ".hbs" or ".ejs" or ".twig" or ".php" or ".vue" or ".svelte" or ".astro"
                     && new FileInfo(file).Length < 512 * 1024 && LoadsTailwind.IsMatch(SafeRead(file)))
                why = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (why != null && css != null) break;
        }
        return new(why != null, css, why);
    }

    static string SafeRead(string path)
    {
        try { return File.ReadAllText(path); }
        catch (IOException) { return ""; }
        catch (UnauthorizedAccessException) { return ""; }
    }

    static IEnumerable<string> Files(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            string dir = pending.Pop();
            string[] files, dirs;
            try { files = Directory.GetFiles(dir); dirs = Directory.GetDirectories(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }
            foreach (string f in files) yield return f;
            foreach (string d in dirs)
                if (!Skipped.Contains(Path.GetFileName(d))) pending.Push(d);
        }
    }
}
