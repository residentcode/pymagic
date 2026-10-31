using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>What the page may add to a run through a run configuration: a few environment variables, with names and sizes that cannot do harm to the host.</summary>
public static class RunConfigurations
{
    static readonly Regex Name = new(@"^[A-Za-z_][A-Za-z0-9_]{0,63}$", RegexOptions.Compiled);

    /// <summary>Names that decide which program runs or where it looks for code; a configuration may not change them (the project's own PYTHONPATH is added by the run).</summary>
    static readonly HashSet<string> Reserved = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "PYTHONPATH", "PYTHONHOME", "PYTHONSTARTUP", "PYTHONEXECUTABLE", "COMSPEC", "SYSTEMROOT", "WINDIR", "LD_PRELOAD", "LD_LIBRARY_PATH", "DYLD_INSERT_LIBRARIES",
        "PYMAGIC_PYTHON", "PYMAGIC_RUN_ID",
    };

    static readonly Regex ModuleName = new(@"^[A-Za-z_][A-Za-z0-9_]*(\.[A-Za-z_][A-Za-z0-9_]*)*$", RegexOptions.Compiled);

    /// <summary>A module a configuration may start with -m: dotted names only, no options.</summary>
    public static bool IsModuleName(string? name) => name != null && name.Length <= 200 && ModuleName.IsMatch(name);

    /// <summary>
    /// The variables of a .env file, as python-dotenv reads the common cases: KEY=value lines, `export ` before the name,
    /// # comments, and a value in single quotes (kept as written) or double quotes (with \n and \" read).
    /// </summary>
    public static Dictionary<string, string> ParseDotEnv(string text)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in text.Replace("\r\n", "\n").Split('\n'))
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (line.StartsWith("export ")) line = line[7..].TrimStart();
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line[..eq].Trim(), value = line[(eq + 1)..].Trim();
            if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') value = value[1..^1];
            else if (value.Length >= 2 && value[0] == '"' && value[^1] == '"') value = value[1..^1].Replace("\\n", "\n").Replace("\\\"", "\"");
            else if (value.IndexOf(" #", StringComparison.Ordinal) is var hash and >= 0) value = value[..hash].TrimEnd();
            result[key] = value;
        }
        return result;
    }

    public const int MaxVariables = 50;
    public const int MaxValueLength = 4096;

    /// <summary>The variables of a start message that are fit to pass on: valid names that are not reserved, string values, at most <see cref="MaxVariables"/>.</summary>
    public static Dictionary<string, string> CleanEnvironment(JsonObject? env)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (env == null) return result;
        foreach (var (key, value) in env)
        {
            if (result.Count >= MaxVariables) break;
            if (!Name.IsMatch(key) || Reserved.Contains(key)) continue;
            if (value is not JsonValue v || !v.TryGetValue<string>(out var text) || text.Length > MaxValueLength || text.Contains('\0')) continue;
            result[key] = text;
        }
        return result;
    }
}
