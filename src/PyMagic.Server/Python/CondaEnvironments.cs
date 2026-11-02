using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Conda environments of this computer (Miniforge, Miniconda, Anaconda; a folder with <c>conda-meta</c> in it): found, made in the project
/// (<c>conda create -p .conda</c>), and their packages listed, installed, updated and removed with conda from its own channels. What conda
/// cannot do is left to the environment's pip: a package pip installed (channel "pypi"), a path or git address, a requirements file,
/// a requirement with extras or markers. conda is used where it already is; PyMagic installs nothing of it.
/// </summary>
public static class Conda
{
    public sealed record Package(string Name, string Version, string Channel);

    /// <summary>Whether this folder is a conda environment (the base one of an installation too).</summary>
    public static bool IsCondaEnv(string prefix)
    {
        try { return Directory.Exists(Path.Combine(prefix, "conda-meta")); }
        catch (Exception e) when (e is ArgumentException or IOException) { return false; }
    }

    /// <summary>The conda environment an interpreter is the Python of (python.exe in it on Windows, bin/python elsewhere), or null.</summary>
    public static string? PrefixOf(string python)
    {
        try
        {
            string? dir = Path.GetDirectoryName(Path.GetFullPath(python));
            if (dir == null) return null;
            if (IsCondaEnv(dir)) return dir;
            string? up = Path.GetDirectoryName(dir);
            return up != null && Path.GetFileName(dir) == "bin" && IsCondaEnv(up) ? up : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }

    /// <summary>The installations PyMagic looks in for conda, in the user's folder, its local app data and (Windows) ProgramData.</summary>
    public static readonly string[] RootNames = { "miniforge3", "miniconda3", "anaconda3", "mambaforge" };

    public static IEnumerable<string> InstallRoots(string home, string? localAppData, string? programData)
    {
        foreach (var parent in new[] { home, localAppData, programData })
            if (!string.IsNullOrEmpty(parent))
                foreach (var name in RootNames) yield return Path.Combine(parent, name);
    }

    static string ToolIn(string root) => OperatingSystem.IsWindows() ? Path.Combine(root, "Scripts", "conda.exe") : Path.Combine(root, "bin", "conda");

    /// <summary>conda itself: CONDA_EXE, then the PATH, then the usual installations (null when there is none).</summary>
    public static string? FindTool()
    {
        if (Environment.GetEnvironmentVariable("CONDA_EXE") is { Length: > 0 } set && File.Exists(set)) return set;
        if (LockFiles.Find("conda") is { } onPath) return onPath;
        foreach (var root in InstallRoots(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), Environment.GetEnvironmentVariable("LOCALAPPDATA"), Environment.GetEnvironmentVariable("ProgramData")))
            if (File.Exists(ToolIn(root))) return ToolIn(root);
        return null;
    }

    /// <summary>(conda, the environment) when this interpreter is that of a conda environment and conda is there; null otherwise (pip is used).</summary>
    public static (string Tool, string Prefix)? For(string python, Func<string?>? find = null) =>
        PrefixOf(python) is { } prefix && (find ?? FindTool)() is { } tool ? (tool, prefix) : null;

    /// <summary>The environments conda keeps a list of (<c>~/.conda/environments.txt</c>), those that are still there.</summary>
    public static IEnumerable<string> Registered(string home)
    {
        string list = Path.Combine(home, ".conda", "environments.txt");
        string[] lines;
        try { lines = File.Exists(list) ? File.ReadAllLines(list) : Array.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var line in lines.Select(l => l.Trim()).Where(l => l.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
            if (IsCondaEnv(line)) yield return line;
    }

    // ------------------------------------------------------------ what conda answers

    /// <summary>The packages of <c>conda list --json</c>, with the channel each came from ("pypi" for what pip installed).</summary>
    public static List<Package> ParseList(string json)
    {
        var list = new List<Package>();
        if (JsonNode.Parse(json) is not JsonArray rows) return list;
        foreach (var r in rows.OfType<JsonObject>())
            if (r["name"]?.GetValue<string>() is { } name && r["version"]?.GetValue<string>() is { } version)
                list.Add(new Package(name, version, r["channel"]?.GetValue<string>() ?? ""));
        return list.OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The newer versions <c>conda update --all --dry-run --json</c> would put in: (name, installed, newer) for packages that are there.</summary>
    public static List<OutdatedPackage> ParseUpdates(string json, IReadOnlyList<Package> installed)
    {
        var have = installed.ToDictionary(p => p.Name, StringComparer.OrdinalIgnoreCase);
        var list = new List<OutdatedPackage>();
        if (JsonNode.Parse(json)?["actions"]?["LINK"] is not JsonArray links) return list;
        foreach (var l in links.OfType<JsonObject>())
            if (l["name"]?.GetValue<string>() is { } name && l["version"]?.GetValue<string>() is { } version && have.TryGetValue(name, out var now) && now.Version != version)
                list.Add(new OutdatedPackage(now.Name, now.Version, version, l["channel"]?.GetValue<string>()));
        return list;
    }

    /// <summary>The versions of a package in conda's channels, newest first, from <c>conda search name --json</c>.</summary>
    public static List<PackageVersions.Release> ParseSearch(string json, string name)
    {
        var node = JsonNode.Parse(json) as JsonObject;
        var rows = node?.FirstOrDefault(kv => string.Equals(kv.Key, name, StringComparison.OrdinalIgnoreCase)).Value as JsonArray;
        if (rows == null) return new();
        return rows.OfType<JsonObject>().Select(r => r["version"]?.GetValue<string>()).OfType<string>().Distinct()
            .Select(v => new PackageVersions.Release(v, PackageVersions.IsPre(v), false))
            .OrderByDescending(r => r.Version, Comparer<string>.Create(PackageVersions.Compare)).ToList();
    }

    // ------------------------------------------------------------ what conda is asked to do

    static readonly Regex CondaSpec = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]*((==|>=|<=|!=|>|<)[A-Za-z0-9.*+!_\-]+)?(,(==|>=|<=|!=|>|<)[A-Za-z0-9.*+!_\-]+)*$");

    /// <summary>Whether conda can take a requirement as it is written (a name, or a name with ==, >=, <=, !=, >, < parts); extras, ~= and markers go to pip.</summary>
    public static bool IsCondaSpec(string spec) => CondaSpec.IsMatch(spec);

    /// <summary><c>conda install</c> (or <c>conda update</c> for names to bring up to date) into the environment; null when a requirement is not one conda can take.</summary>
    public static List<string>? InstallArgs(string prefix, IReadOnlyList<string> specs, bool upgrade)
    {
        if (specs.Count == 0 || specs.Any(s => !IsCondaSpec(s))) return null;
        bool namesOnly = specs.All(s => Regex.IsMatch(s, @"^[A-Za-z0-9][A-Za-z0-9._\-]*$"));
        var args = new List<string> { upgrade && namesOnly ? "update" : "install", "-p", prefix, "-y", "--quiet" };
        args.AddRange(specs);
        return args;
    }

    /// <summary><c>conda remove</c> of packages conda put there; null when one of them came from pip (conda refuses those: pip removes them).</summary>
    public static List<string>? RemoveArgs(string prefix, IReadOnlyList<string> names, IReadOnlyList<Package> installed)
    {
        var channel = installed.GroupBy(p => PackageManager.Normalize(p.Name)).ToDictionary(g => g.Key, g => g.First().Channel);
        if (names.Count == 0 || names.Any(n => channel.GetValueOrDefault(PackageManager.Normalize(n)) is null or "pypi")) return null;
        var args = new List<string> { "remove", "-p", prefix, "-y", "--quiet" };
        args.AddRange(names);
        return args;
    }

    /// <summary>Whether a list of names to remove mixes packages of conda and of pip (one command cannot remove both).</summary>
    public static bool MixesPip(IReadOnlyList<string> names, IReadOnlyList<Package> installed)
    {
        var channel = installed.GroupBy(p => PackageManager.Normalize(p.Name)).ToDictionary(g => g.Key, g => g.First().Channel);
        var kinds = names.Select(n => channel.GetValueOrDefault(PackageManager.Normalize(n)) == "pypi").Distinct().Count();
        return kinds > 1;
    }

    /// <summary><c>conda create -p</c> a new environment with Python (the version given, else conda's newest), from the installation's channels.</summary>
    public static List<string> CreateArgs(string prefix, string? pythonVersion)
    {
        string python = pythonVersion is { Length: > 0 } v && Regex.IsMatch(v, @"^\d+\.\d+$") ? "python=" + v : "python";
        return new List<string> { "create", "-p", prefix, python, "-y", "--quiet" };
    }

    /// <summary>Runs conda and waits: (exit code, output, error output).</summary>
    public static (int Code, string Output, string Error) Run(string tool, IReadOnlyList<string> args, int timeoutMs)
    {
        var psi = new ProcessStartInfo(tool) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = Path.GetTempPath() };
        psi.StandardOutputEncoding = Encoding.UTF8; psi.StandardErrorEncoding = Encoding.UTF8;
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["NO_COLOR"] = "1";
        psi.Environment["CONDA_QUIET"] = "1";
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return (-1, "", "cannot start conda");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } return (-1, "", "conda did not answer in time"); }
            return (p.ExitCode, output.Result, error.Result);
        }
        catch (System.ComponentModel.Win32Exception e) { return (-1, "", e.Message); }
    }

    /// <summary>The packages of an environment, asked of conda (null when it does not answer).</summary>
    public static List<Package>? List(string tool, string prefix)
    {
        var (code, output, _) = Run(tool, new[] { "list", "-p", prefix, "--json" }, 60000);
        if (code != 0) return null;
        try { return ParseList(output); }
        catch (System.Text.Json.JsonException) { return null; }
    }
}
