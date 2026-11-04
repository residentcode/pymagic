using System.Text.Json;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>A shell the Terminal panel can start: its id (what the settings keep), its name, the command line, and variables it needs.</summary>
/// <param name="VenvArgs">Added to the command line while a virtual environment is active, with {prompt} for its name.</param>
/// <param name="VenvEnv">Variables set while one is active, with {prompt} (cmd's PROMPT).</param>
/// <param name="CommandArg">How the shell is given commands to run before the user types (PowerShell's <c>-NoExit -Command</c>, cmd's <c>/k</c>).</param>
/// <param name="StartCommand">Run when it starts (PowerShell's and cmd's <c>uv</c> that makes `uv init` an application, <see cref="UvInit"/>).</param>
/// <param name="VenvCommand">Run when it starts while a virtual environment is active, with {prompt} (PowerShell's prompt function).</param>
public sealed record ConsoleShell(string Id, string Name, string CommandLine, IReadOnlyDictionary<string, string>? Environment = null,
    string? VenvArgs = null, IReadOnlyDictionary<string, string>? VenvEnv = null, string? CommandArg = null, string? StartCommand = null, string? VenvCommand = null)
{
    /// <summary>The command line and the variables for a terminal whose virtual environment is called <paramref name="prompt"/> (null: none is active).</summary>
    public (string CommandLine, Dictionary<string, string> Environment) WithVenv(string? prompt)
    {
        var env = new Dictionary<string, string>(Environment ?? new Dictionary<string, string>(), StringComparer.OrdinalIgnoreCase);
        string line = CommandLine;
        var commands = new List<string>();
        if (!string.IsNullOrEmpty(StartCommand)) commands.Add(StartCommand);
        if (prompt != null)
        {
            foreach (var (k, v) in VenvEnv ?? new Dictionary<string, string>()) env[k] = v.Replace("{prompt}", prompt);
            env["VIRTUAL_ENV_PROMPT"] = prompt;
            if (!string.IsNullOrEmpty(VenvArgs)) line += " " + VenvArgs.Replace("{prompt}", prompt);
            if (!string.IsNullOrEmpty(VenvCommand)) commands.Add(VenvCommand.Replace("{prompt}", prompt));
        }
        if (commands.Count > 0 && !string.IsNullOrEmpty(CommandArg)) line += " " + CommandArg + " \"" + string.Join("; ", commands) + "\"";
        return (line, env);
    }
}

/// <summary>
/// An entry of the shell list (Run/shells.json): where the program may be, first match wins. A place is a path with %VARIABLES%,
/// "path:name.exe" (searched on the PATH), or "path:name.exe>relative" (a file relative to the folder of one found on the PATH).
/// </summary>
public sealed record ShellProfile(string Id, string Name, string[] Find, string? Args = null, Dictionary<string, string>? Env = null,
    string? VenvArgs = null, Dictionary<string, string>? VenvEnv = null, string? CommandArg = null, string? StartCommand = null, string? VenvCommand = null);

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
            shells.Add(new(profile.Id, profile.Name, command, profile.Env, profile.VenvArgs, profile.VenvEnv, profile.CommandArg, profile.StartCommand, profile.VenvCommand));
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

    /// <summary>The id the page uses for the shell inside the project's interpreter (Docker, WSL, ...).</summary>
    public const string InterpreterId = "interpreter";

    /// <summary>A shell inside the target of the project's interpreter, started in <paramref name="workDir"/>; null for this computer.</summary>
    public static ConsoleShell? InTarget(ExecutionTarget target, string workDir)
    {
        if (target.ShellCommand(workDir) is not { } c) return null;
        return new ConsoleShell(InterpreterId, target.Name, CommandLineOf(c.Program, c.Args));
    }

    /// <summary>A Windows command line that starts the program with exactly these arguments (the rules of CommandLineToArgvW).</summary>
    public static string CommandLineOf(string program, IEnumerable<string> args) => string.Join(" ", new[] { program }.Concat(args.Select(QuoteArgument)));

    static string QuoteArgument(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
        var sb = new System.Text.StringBuilder("\"");
        int backslashes = 0;
        foreach (char ch in arg)
        {
            if (ch == '\\') { backslashes++; continue; }
            if (ch == '"') sb.Append('\\', backslashes * 2 + 1).Append('"');
            else sb.Append('\\', backslashes).Append(ch);
            backslashes = 0;
        }
        sb.Append('\\', backslashes * 2).Append('"');
        return sb.ToString();
    }

    /// <summary>
    /// The name a terminal's prompt shows for a virtual environment: the `prompt` of its pyvenv.cfg, else its folder's name; only letters,
    /// digits, spaces, dots, dashes and underscores, since it goes into a command line.
    /// </summary>
    public static string? VenvPrompt(string? venvFolder, Func<string, string?>? readConfig = null)
    {
        if (string.IsNullOrEmpty(venvFolder)) return null;
        readConfig ??= path => File.Exists(path) ? File.ReadAllText(path) : null;
        string? name = null;
        try
        {
            if (readConfig(Path.Combine(venvFolder, "pyvenv.cfg")) is { } cfg)
                foreach (var line in cfg.Split('\n'))
                    if (line.Split('=', 2) is [var k, var v] && k.Trim() == "prompt") name = v.Trim().Trim('\'', '"');
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        name ??= Path.GetFileName(venvFolder.TrimEnd('\\', '/'));
        string clean = new string(name.Where(c => char.IsLetterOrDigit(c) || c is ' ' or '.' or '-' or '_').ToArray()).Trim();
        return clean.Length == 0 ? "venv" : clean;
    }
}
