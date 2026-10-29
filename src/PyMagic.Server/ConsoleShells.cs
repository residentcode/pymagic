using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A shell the Terminal panel can start: its id (what the settings keep), its name, the command line, and variables it needs.</summary>
public sealed record ConsoleShell(string Id, string Name, string CommandLine, IReadOnlyDictionary<string, string>? Environment = null);

/// <summary>
/// An entry of the shell list (Run/shells.json): where the program may be, first match wins. A place is a path with %VARIABLES%,
/// "path:name.exe" (searched on the PATH), or "path:name.exe>relative" (a file relative to the folder of one found on the PATH).
/// </summary>
public sealed record ShellProfile(string Id, string Name, string[] Find, string? Args = null, Dictionary<string, string>? Env = null);

/// <summary>
/// The shells found on this PC for the Terminal panel, from the list in Run/shells.json. The page names one by its id and the engine starts
/// the command line it found itself, so the page never sends a command line to run.
/// </summary>
public static class ConsoleShells
{
    public const string Auto = "auto";

    static List<ShellProfile>? _catalogue;

    /// <summary>The shell list shipped with PyMagic, in the order the settings show it.</summary>
    public static List<ShellProfile> Catalogue()
    {
        if (_catalogue != null) return _catalogue;
        using var stream = typeof(ConsoleShells).Assembly.GetManifestResourceStream("shells.json")
            ?? throw new InvalidOperationException("the shell list is missing from the engine");
        return _catalogue = JsonSerializer.Deserialize<List<ShellProfile>>(stream, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new();
    }

    /// <summary>The shells of <paramref name="catalogue"/> (the shipped list by default) that are on this PC.</summary>
    /// <param name="exists">Whether a file is there (tests give their own).</param>
    /// <param name="variable">Reads an environment variable (tests give their own).</param>
    public static List<ConsoleShell> Found(IEnumerable<ShellProfile>? catalogue = null, Func<string, bool>? exists = null, Func<string, string?>? variable = null)
    {
        exists ??= File.Exists;
        variable ??= System.Environment.GetEnvironmentVariable;
        var path = (variable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        string? OnPath(string name)
        {
            foreach (var dir in path)
            {
                try { var p = Path.Combine(dir.Trim('"'), name); if (exists(p)) return p; }
                catch (ArgumentException) { /* a broken PATH entry */ }
            }
            return null;
        }
        string? Place(string place)
        {
            try
            {
                if (place.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
                {
                    var parts = place[5..].Split('>', 2);
                    var found = OnPath(parts[0]);
                    if (found == null || parts.Length == 1) return found;
                    var beside = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(found)!, parts[1]));
                    return exists(beside) ? beside : null;
                }
                bool missing = false;
                var expanded = Regex.Replace(place, @"%(\w+)%", m =>
                {
                    if (variable(m.Groups[1].Value) is { Length: > 0 } v) return v;
                    missing = true;
                    return "";
                });
                return !missing && exists(expanded) ? expanded : null;
            }
            catch (ArgumentException) { return null; }
        }

        var shells = new List<ConsoleShell>();
        foreach (var profile in catalogue ?? Catalogue())
        {
            if (profile.Find.Select(Place).FirstOrDefault(p => p != null) is not { } program) continue;
            var command = Quote(program) + (string.IsNullOrEmpty(profile.Args) ? "" : " " + profile.Args);
            shells.Add(new(profile.Id, profile.Name, command, profile.Env));
        }
        return shells;
    }

    /// <summary>The shell an id names; for "auto", an unknown id or a shell not on this PC: `PYMAGIC_SHELL` when set, else the first found.</summary>
    public static ConsoleShell Choose(string? id, List<ConsoleShell> found, Func<string, string?>? variable = null)
    {
        variable ??= System.Environment.GetEnvironmentVariable;
        if (id != null && id != Auto && found.FirstOrDefault(s => s.Id == id) is { } chosen) return chosen;
        if (variable("PYMAGIC_SHELL") is { Length: > 0 } own) return new("env", own, own);
        return found.FirstOrDefault() ?? new("powershell", "Windows PowerShell", "powershell.exe -NoLogo");
    }

    static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;
}
