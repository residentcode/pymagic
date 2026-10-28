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
