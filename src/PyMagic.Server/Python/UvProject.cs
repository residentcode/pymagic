using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// A uv project: a pyproject.toml that uv keeps (a uv.lock beside it, or a [tool.uv] table), used with the project's own .venv. Its packages are the
/// project's dependencies, so the Packages panel adds and removes them with <c>uv add</c> and <c>uv remove</c> (pyproject.toml and uv.lock follow);
/// what is not one of them (a package another one pulls in) is handled with uv pip as in any environment uv made.
/// </summary>
public static class UvProject
{
    /// <summary>Whether the folder is a uv project.</summary>
    public static bool IsUvProject(string root)
    {
        try
        {
            string pyproject = Path.Combine(root, "pyproject.toml");
            if (!File.Exists(pyproject)) return false;
            return File.Exists(Path.Combine(root, "uv.lock")) || File.ReadAllText(pyproject).Contains("[tool.uv]");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return false; }
    }

    /// <summary>
    /// uv, when the folder is a uv project (one uv keeps: uv.lock, or a [tool.uv] table) and the interpreter is that of its own .venv; null otherwise
    /// (a pyproject.toml of poetry or setuptools beside a .venv uv made is not one: its dependencies are not uv's to change).
    /// </summary>
    public static string? For(string root, string python, Func<string, string?>? which = null)
    {
        if (!IsUvProject(root) || UvPip.PrefixOf(python) is not { } prefix) return null;
        string own = Path.GetFullPath(Path.Combine(root, ".venv"));
        if (!string.Equals(Path.GetFullPath(prefix).TrimEnd('\\', '/'), own.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return null;
        return (which ?? LockFiles.Find)("uv");
    }

    /// <summary>The names of the project's direct dependencies ([project] dependencies of pyproject.toml), normalized as PyPI does.</summary>
    public static List<string> Dependencies(string root)
    {
        string text;
        try { text = File.ReadAllText(Path.Combine(root, "pyproject.toml")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) { return new(); }
        return DependenciesIn(text);
    }

    /// <summary>The direct dependencies written in a pyproject.toml: the strings of the [project] table's dependencies list, by name.</summary>
    public static List<string> DependenciesIn(string pyproject)
    {
        var project = Regex.Match(pyproject, @"(?ms)^\[project\]\s*$(.*?)(?=^\[|\z)");
        if (!project.Success) return new();
        // the list ends at the ] that ends a line (an extra such as uvicorn[standard] has its own brackets)
        var list = Regex.Match(project.Groups[1].Value, @"(?ms)^dependencies\s*=\s*\[(.*?)\]\s*$");
        if (!list.Success) return new();
        return Regex.Matches(list.Groups[1].Value, @"[""']([^""']+)[""']")
            .Select(m => Regex.Match(m.Groups[1].Value.Trim(), @"^[A-Za-z0-9][A-Za-z0-9._-]*").Value)
            .Where(n => n.Length > 0).Select(PackageManager.Normalize).Distinct().ToList();
    }

    /// <summary><c>uv add</c> of packages (an upgrade of names: each also given as --upgrade-package, so uv takes its newest).</summary>
    public static List<string> AddArgs(IReadOnlyList<string> specs, bool upgrade)
    {
        var args = new List<string> { "add", "--no-progress" };
        if (upgrade)
            foreach (var s in specs)
                if (Regex.Match(s, @"^[A-Za-z0-9][A-Za-z0-9._-]*").Value is { Length: > 0 } name) { args.Add("--upgrade-package"); args.Add(name); }
        args.AddRange(specs);
        return args;
    }

    /// <summary><c>uv remove</c> of packages that are direct dependencies of the project; null when one is not (uv pip uninstalls those).</summary>
    public static List<string>? RemoveArgs(IReadOnlyList<string> names, IReadOnlyList<string> dependencies)
    {
        if (names.Count == 0 || names.Any(n => !dependencies.Contains(PackageManager.Normalize(n)))) return null;
        var args = new List<string> { "remove", "--no-progress" };
        args.AddRange(names);
        return args;
    }

    /// <summary>What a new project of a kind depends on (what New Project adds with uv add).</summary>
    public static List<string> PackagesOf(string kind, bool jinja) => kind switch
    {
        "django" => jinja ? new() { "django", "jinja2" } : new() { "django" },
        "fastapi" => new() { "fastapi", "uvicorn[standard]" },
        "flask" => new() { "flask" },
        "jupyter" => new() { "jupyter" },
        _ => new(),
    };
}
