using System.Diagnostics;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>The parts of the machine that discovery looks at; replaceable so tests can describe a fake machine.</summary>
public sealed record DiscoveryEnvironment(
    string Root,
    IReadOnlyList<string> PathDirs,
    string? VirtualEnv,
    string? CondaPrefix,
    string Home,
    string? LocalAppData,
    string? AppData,
    bool IsWindows,
    bool UsePyLauncher)
{
    public static DiscoveryEnvironment FromProcess(string root) => new(
        root,
        (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries),
        Environment.GetEnvironmentVariable("VIRTUAL_ENV"),
        Environment.GetEnvironmentVariable("CONDA_PREFIX"),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        Environment.GetEnvironmentVariable("APPDATA"),
        OperatingSystem.IsWindows(),
        OperatingSystem.IsWindows());
}

/// <summary>
/// Finds interpreters without running them (except the Windows <c>py</c> launcher's listing): the project's virtual
/// environments, VIRTUAL_ENV and CONDA_PREFIX, the launcher, PATH, and the usual install locations of python.org, uv,
/// pyenv and conda. Order is the order of preference: what belongs to the project comes first.
/// </summary>
public static class InterpreterDiscovery
{
    static readonly string[] VenvNames = { ".venv", "venv", "env", ".env", "virtualenv" };

    public static List<InterpreterCandidate> Discover(DiscoveryEnvironment env)
    {
        var result = new List<InterpreterCandidate>();
        var seen = new HashSet<string>(env.IsWindows ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        void Add(string? path, string kind, string source)
        {
            if (path == null || !File.Exists(path)) return;
            // the Windows Store "app execution alias" is an empty file that opens the Store instead of running Python
            if (env.IsWindows && new FileInfo(path).Length == 0 && path.Contains("WindowsApps", StringComparison.OrdinalIgnoreCase)) return;
            string full = Path.GetFullPath(path);
            if (seen.Add(full)) result.Add(new InterpreterCandidate(full, kind, source));
        }

        string Exe(string dir, bool venvLayout) => env.IsWindows
            ? Path.Combine(dir, venvLayout ? "Scripts" : "", "python.exe")
            : Path.Combine(dir, "bin", "python");

        // 1. virtual environments of the project: the root and up to two parent folders, then folders one level down
        var folders = new List<string>();
        for (var d = new DirectoryInfo(env.Root); d != null && folders.Count < 3; d = d.Parent) folders.Add(d.FullName);
        foreach (var folder in folders)
            foreach (var name in VenvNames)
                if (File.Exists(Path.Combine(folder, name, "pyvenv.cfg")))
                    Add(Exe(Path.Combine(folder, name), true), "venv", name);
        try
        {
            foreach (var sub in Directory.EnumerateDirectories(env.Root).OrderBy(s => s, StringComparer.OrdinalIgnoreCase))
            {
                string n = Path.GetFileName(sub);
                if (n.StartsWith('.') && !VenvNames.Contains(n) || n is "node_modules" or "__pycache__") continue;
                if (File.Exists(Path.Combine(sub, "pyvenv.cfg"))) Add(Exe(sub, true), "venv", n);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        // pipenv keeps a project's environment under ~/.virtualenvs (or WORKON_HOME), named after the project folder and a hash of its path
        string workon = Environment.GetEnvironmentVariable("WORKON_HOME") is { Length: > 0 } w ? w : Path.Combine(env.Home, ".virtualenvs");
        if (File.Exists(Path.Combine(env.Root, "Pipfile")))
            foreach (var dir in PipenvEnvironments(workon, env.Root)) Add(Exe(dir, true), "pipenv", "pipenv");

        // 2. the activated environment
        if (!string.IsNullOrEmpty(env.VirtualEnv)) Add(Exe(env.VirtualEnv, true), "venv", "VIRTUAL_ENV");
        if (!string.IsNullOrEmpty(env.CondaPrefix)) Add(env.IsWindows ? Path.Combine(env.CondaPrefix, "python.exe") : Exe(env.CondaPrefix, false), "conda", "CONDA_PREFIX");

        // 3. PATH: what "python" means in a terminal
        string[] names = env.IsWindows ? new[] { "python.exe", "python3.exe" } : new[] { "python3", "python" };
        foreach (var dir in env.PathDirs)
            foreach (var n in names)
                try { Add(Path.Combine(dir, n), Classify(Path.Combine(dir, n), env), "PATH"); }
                catch (ArgumentException) { }

        // 4. the py launcher lists everything it knows
        if (env.UsePyLauncher && env.IsWindows)
            foreach (var path in LauncherList())
                Add(path, Classify(path, env), "py launcher");

        // 5. well-known install locations
        foreach (var (path, kind) in KnownLocations(env)) Add(path, kind, "installed");

        return result;
    }

    /// <summary>The environments pipenv made for a project folder: <c>name-XXXXXXXX</c>, the folder's name (as pipenv cleans it) and eight characters of a hash.</summary>
    public static IEnumerable<string> PipenvEnvironments(string workon, string root)
    {
        string name = System.Text.RegularExpressions.Regex.Replace(Path.GetFileName(Path.TrimEndingDirectorySeparator(root)), @"[ &$`!*@""()\[\]\\\r\n\t]", "_");
        if (name.Length > 42) name = name[..42];
        IEnumerable<string> dirs;
        try { dirs = Directory.Exists(workon) ? Directory.EnumerateDirectories(workon, name + "-*").ToList() : Enumerable.Empty<string>(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { yield break; }
        foreach (var d in dirs.OrderBy(d => d, StringComparer.OrdinalIgnoreCase))
            if (Path.GetFileName(d).Length == name.Length + 9 && File.Exists(Path.Combine(d, "pyvenv.cfg"))) yield return d;
    }

    static string Classify(string path, DiscoveryEnvironment env)
    {
        string p = path.Replace('\\', '/').ToLowerInvariant();
        if (p.Contains("/uv/python/")) return "uv";
        if (p.Contains("/.pyenv/") || p.Contains("/pyenv-win/")) return "pyenv";
        if (p.Contains("/conda/") || p.Contains("/miniconda") || p.Contains("/anaconda") || p.Contains("/miniforge") || p.Contains("/mambaforge") || p.Contains("/envs/")) return "conda";
        return "system";
    }

    static IEnumerable<string> LauncherList()
    {
        string output;
        try
        {
            var psi = new ProcessStartInfo("py", "-0p") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p == null) yield break;
            output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(5000)) { try { p.Kill(); } catch (InvalidOperationException) { } yield break; }
        }
        catch (System.ComponentModel.Win32Exception) { yield break; }

        foreach (var line in output.Split('\n'))
        {
            var m = Regex.Match(line, @"(?<path>[A-Za-z]:\\.*?python[\w.]*\.exe)\s*$", RegexOptions.IgnoreCase);
            if (m.Success) yield return m.Groups["path"].Value.Trim();
        }
    }

    static IEnumerable<(string Path, string Kind)> KnownLocations(DiscoveryEnvironment env)
    {
        static IEnumerable<string> Dirs(string baseDir, string pattern)
        {
            try { return Directory.Exists(baseDir) ? Directory.EnumerateDirectories(baseDir, pattern).OrderByDescending(d => d, StringComparer.OrdinalIgnoreCase).ToList() : new List<string>(); }
            catch (IOException) { return Array.Empty<string>(); }
            catch (UnauthorizedAccessException) { return Array.Empty<string>(); }
        }

        var condaRoots = new[] { "miniconda3", "anaconda3", "miniforge3", "mambaforge", "micromamba", ".conda" };

        if (env.IsWindows)
        {
            if (env.LocalAppData != null)
            {
                foreach (var d in Dirs(Path.Combine(env.LocalAppData, "Programs", "Python"), "Python3*")) yield return (Path.Combine(d, "python.exe"), "system");
                foreach (var d in Dirs(Path.Combine(env.LocalAppData, "Python"), "pythoncore-*")) yield return (Path.Combine(d, "python.exe"), "system");
            }
            if (env.AppData != null)
                foreach (var d in Dirs(Path.Combine(env.AppData, "uv", "python"), "*")) yield return (Path.Combine(d, "python.exe"), "uv");
            foreach (var d in Dirs(Path.Combine(env.Home, ".pyenv", "pyenv-win", "versions"), "*")) yield return (Path.Combine(d, "python.exe"), "pyenv");
            foreach (var c in condaRoots)
            {
                string baseDir = Path.Combine(env.Home, c);
                yield return (Path.Combine(baseDir, "python.exe"), "conda");
                foreach (var d in Dirs(Path.Combine(baseDir, "envs"), "*")) yield return (Path.Combine(d, "python.exe"), "conda");
            }
        }
        else
        {
            foreach (var d in Dirs(Path.Combine(env.Home, ".local", "share", "uv", "python"), "*")) yield return (Path.Combine(d, "bin", "python"), "uv");
            foreach (var d in Dirs(Path.Combine(env.Home, ".pyenv", "versions"), "*")) yield return (Path.Combine(d, "bin", "python"), "pyenv");
            foreach (var c in condaRoots)
            {
                string baseDir = Path.Combine(env.Home, c);
                yield return (Path.Combine(baseDir, "bin", "python"), "conda");
                foreach (var d in Dirs(Path.Combine(baseDir, "envs"), "*")) yield return (Path.Combine(d, "bin", "python"), "conda");
            }
            foreach (var bin in new[] { "/usr/bin", "/usr/local/bin" })
                foreach (var n in new[] { "python3", "python" }) yield return (Path.Combine(bin, n), "system");
        }
    }
}
