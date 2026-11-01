using System.Text.Json.Serialization;

namespace PyMagic.Server;

/// <summary>An interpreter that was found on disk but not asked anything yet.</summary>
/// <param name="Kind">venv, conda, uv, pyenv or system.</param>
/// <param name="Source">Where it was found: ".venv", "PATH", "py launcher", "VIRTUAL_ENV", ...</param>
public sealed record InterpreterCandidate(string Path, string Kind, string Source);

/// <summary>What an interpreter reported about itself (see <see cref="InterpreterInspector"/>).</summary>
public sealed record PythonInterpreter(
    string Path,
    string Kind,
    string Source,
    int Major, int Minor, int Micro,
    string ReleaseLevel,
    string Implementation,
    string Platform,
    string Prefix,
    string BasePrefix,
    string? Stdlib,
    IReadOnlyList<string> SysPath,
    IReadOnlyList<string> SitePackages,
    IReadOnlySet<string> StdlibModules)
{
    public string Version => $"{Major}.{Minor}.{Micro}";

    /// <summary>Where it runs: this computer, a WSL distribution or a Docker image. For the others <see cref="Path"/> is the spec (see <see cref="ExecutionTarget"/>).</summary>
    [JsonIgnore]
    public ExecutionTarget Target { get; init; } = ExecutionTarget.Local;

    /// <summary>The interpreter's own path as its target names it (what to run there); for a local one the same as <see cref="Path"/>.</summary>
    public string? TargetPython { get; init; }

    [JsonIgnore]
    public string Python => TargetPython ?? Path;

    [JsonIgnore]
    public bool IsVenv => !string.Equals(System.IO.Path.GetFullPath(Prefix), System.IO.Path.GetFullPath(BasePrefix), StringComparison.OrdinalIgnoreCase);

    /// <summary>A name for lists: "Python 3.13.15 (.venv)".</summary>
    public string Label
    {
        get
        {
            string where = Kind switch
            {
                "wsl" or "docker" or "compose" => Target.Name,
                "venv" or "conda" => System.IO.Path.GetFileName(Prefix.TrimEnd('\\', '/')) is { Length: > 0 } n ? n : Source,
                "uv" => "uv",
                "pyenv" => "pyenv",
                _ => Source is "PATH" or "py launcher" ? "system" : Source,
            };
            string impl = Implementation == "cpython" ? "Python" : Implementation;
            return $"{impl} {Version} ({where})";
        }
    }

    /// <summary>
    /// Folders to search for importable code besides the project: the interpreter's site-packages (and whatever its
    /// <c>.pth</c> files added), then other entries of its sys.path, and the standard library last. Compiled-module folders
    /// (DLLs, lib-dynload) hold no source and are left out.
    /// </summary>
    public IReadOnlyList<string> LibraryRoots(bool includeStdlib)
    {
        static string Norm(string p) => System.IO.Path.GetFullPath(p).TrimEnd('\\', '/');
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var roots = new List<string>();
        void Add(string? p)
        {
            if (string.IsNullOrWhiteSpace(p) || !Directory.Exists(p)) return;
            string n = Norm(p);
            string leaf = System.IO.Path.GetFileName(n);
            if (leaf is "DLLs" or "lib-dynload") return;
            if (Stdlib != null && string.Equals(n, Norm(Stdlib), StringComparison.OrdinalIgnoreCase)) return;
            if (seen.Add(n)) roots.Add(n);
        }
        foreach (var p in SitePackages) Add(p);
        foreach (var p in SysPath) Add(p);
        if (includeStdlib && Stdlib != null && Directory.Exists(Stdlib) && seen.Add(Norm(Stdlib))) roots.Add(Norm(Stdlib));
        return roots;
    }
}
