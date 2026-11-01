using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PyMagic.Server;

/// <summary>
/// The interpreter a project uses: remembered in <c>.pymagic/settings.json</c> inside the project folder, otherwise the
/// best one found (the project's own virtual environment first). Listing inspects every candidate in parallel.
/// </summary>
public sealed class InterpreterManager
{
    readonly string _root;
    readonly DiscoveryEnvironment _env;
    readonly object _gate = new();
    PythonInterpreter? _current;

    public InterpreterManager(string root, DiscoveryEnvironment? env = null)
    {
        _root = Path.GetFullPath(root);
        DockerTarget.DefaultProject = _root;
        SshTarget.DefaultProject = _root;
        _env = env ?? DiscoveryEnvironment.FromProcess(_root);
    }

    public string SettingsFile => Path.Combine(_root, ".pymagic", "settings.json");
    public PythonInterpreter? Current { get { lock (_gate) return _current; } }

    /// <summary>Raised after the selection changed (also for the first automatic choice).</summary>
    public event Action<PythonInterpreter?>? Changed;

    // ------------------------------------------------------------ choosing

    /// <summary>Uses the saved choice when it still works, otherwise the best discovered interpreter.</summary>
    public PythonInterpreter? InitializeDefault()
    {
        var saved = ReadSavedPath();
        if (saved != null && Inspect(saved, "system", "saved") is { Interpreter: { } s })
            return Set(s, save: false);

        foreach (var c in InterpreterDiscovery.Discover(_env))
            if (InterpreterInspector.Inspect(c.Path, c.Kind, c.Source) is { Interpreter: { } found })
                return Set(found, save: false);
        return Set(null, save: false);
    }

    /// <summary>Makes this interpreter the project's and remembers it.</summary>
    /// <exception cref="ServiceException">The path is not a working Python interpreter.</exception>
    public PythonInterpreter Select(string path, bool save = true)
    {
        var known = ExecutionTarget.IsSpec(path) ? null : InterpreterDiscovery.Discover(_env).FirstOrDefault(c => SamePath(c.Path, path));
        var r = Inspect(path, known?.Kind ?? "system", known?.Source ?? "selected");
        if (r.Interpreter == null) throw new ServiceException($"'{path}' is not a usable Python interpreter: {r.Error}");
        return Set(r.Interpreter, save)!;
    }

    /// <summary>
    /// Looks at the current interpreter again (packages or .pth files may have been added) and announces it, so editors
    /// pick up new libraries without being restarted.
    /// </summary>
    public PythonInterpreter? Reload()
    {
        var current = Current;
        if (current == null) return null;
        var r = InterpreterInspector.Inspect(current.Path, current.Kind, current.Source);
        return Set(r.Interpreter ?? current, save: false);
    }

    InspectResult Inspect(string path, string kind, string source)
    {
        var known = ExecutionTarget.IsSpec(path) ? null : InterpreterDiscovery.Discover(_env).FirstOrDefault(c => SamePath(c.Path, path));
        return InterpreterInspector.Inspect(path, known?.Kind ?? kind, known?.Source ?? source);
    }

    PythonInterpreter? Set(PythonInterpreter? interpreter, bool save)
    {
        if (interpreter?.Target is DockerTarget docker) docker.Project = _root;   // what a container sees as /workspace
        if (interpreter?.Target is ComposeTarget compose) compose.Project = _root;
        if (interpreter?.Target is SshTarget ssh) ssh.Project = _root;            // what is copied to the remote machine
        lock (_gate) _current = interpreter;
        if (save && interpreter != null) Save(interpreter.Path);
        Changed?.Invoke(interpreter);
        return interpreter;
    }

    static bool SamePath(string a, string b) =>
        ExecutionTarget.IsSpec(a) || ExecutionTarget.IsSpec(b)
            ? string.Equals(a, b, StringComparison.OrdinalIgnoreCase)
            : string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------ listing

    /// <summary>Every interpreter that could be found and answers, the current one first, then newest versions first.</summary>
    public async Task<IReadOnlyList<PythonInterpreter>> ListAsync()
    {
        var candidates = InterpreterDiscovery.Discover(_env);
        var gate = new SemaphoreSlim(4);
        var tasks = candidates.Select(async c =>
        {
            await gate.WaitAsync();
            try { return await Task.Run(() => InterpreterInspector.Inspect(c.Path, c.Kind, c.Source).Interpreter); }
            finally { gate.Release(); }
        }).ToList();
        // interpreters in WSL or Docker that were added to this project
        var remoteTasks = RemoteSpecs().Select(spec => Task.Run(() => InterpreterInspector.Inspect(spec, "remote", "added").Interpreter)).ToList();
        var inspected = (await Task.WhenAll(tasks.Concat(remoteTasks))).Where(i => i != null).Select(i => i!).ToList();
        // python.exe, python3.exe and launcher shims of one installation are one interpreter (the first found stays)
        var current = Current;
        var found = inspected.OrderBy(i => current != null && SamePath(i.Path, current.Path) ? 0 : 1)
            .GroupBy(i => (Path.GetFullPath(i.Prefix).TrimEnd('\\', '/').ToLowerInvariant(), i.Version, i.Implementation))
            .Select(g => g.First()).ToList();

        if (current != null && !found.Any(f => SamePath(f.Path, current.Path))) found.Insert(0, current);
        int Rank(PythonInterpreter i) => current != null && SamePath(i.Path, current.Path) ? 0 : i.Kind == "venv" ? 1 : 2;
        return found.Select((i, idx) => (i, idx)).OrderBy(x => Rank(x.i)).ThenByDescending(x => x.i.Major * 1000 + x.i.Minor).ThenBy(x => x.idx)
            .Select(x => x.i).ToList();
    }

    // ------------------------------------------------------------ virtual environments

    /// <summary>Creates <c>.venv</c> (or the given name) in the project with the given interpreter and returns its python.</summary>
    /// <exception cref="ServiceException">The folder exists already or the interpreter could not create it.</exception>
    public string CreateVenv(string basePython, string name = ".venv", bool withPip = true)
    {
        if (name.Contains('/') || name.Contains('\\') || name.Contains("..")) throw new ServiceException("the name must be a single folder name");
        string dir = Path.Combine(_root, name);
        if (Directory.Exists(dir)) throw new ServiceException($"'{name}' already exists in the project");

        // an interpreter in WSL or Docker creates the environment there: for the project folder, which that system shares
        var remote = ExecutionTarget.Parse(basePython);
        var target = remote?.Target ?? ExecutionTarget.Local;
        string python = remote?.Python ?? basePython;
        var args = new List<string> { "-m", "venv" };
        if (!withPip) args.Add("--without-pip");
        if (!target.IsLocal) args.Add("--copies");       // links do not work on the Windows drive that WSL mounts
        args.Add(dir);
        try { target.Prepare(_root, null, null); }       // a machine that does not share the folder needs it first
        catch (ServiceException) { throw; }
        var psi = target.Start(python, _root, args);
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.UseShellExecute = false; psi.CreateNoWindow = true;
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("cannot start " + basePython);
            var err = p.StandardError.ReadToEndAsync();
            p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(180000)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("creating the environment took too long"); }
            if (p.ExitCode != 0) throw new ServiceException("venv failed: " + (err.Result.Trim().Split('\n').LastOrDefault() ?? "exit code " + p.ExitCode));
        }
        catch (System.ComponentModel.Win32Exception e) { throw new ServiceException(e.Message); }

        if (!target.IsLocal)
        {
            string inside = target.ToTarget(dir) + "/bin/python";
            if (!target.Exists(inside)) throw new ServiceException("the environment was created but has no interpreter");
            return target.Spec(inside);
        }
        string exe = OperatingSystem.IsWindows() ? Path.Combine(dir, "Scripts", "python.exe") : Path.Combine(dir, "bin", "python");
        if (!File.Exists(exe)) throw new ServiceException("the environment was created but has no interpreter");
        return exe;
    }

    // ------------------------------------------------------------ interpreters in WSL and Docker

    /// <summary>The specs of the WSL and Docker interpreters added to this project.</summary>
    public IReadOnlyList<string> RemoteSpecs()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return Array.Empty<string>();
            return (JsonNode.Parse(File.ReadAllText(SettingsFile))?["remote"] as JsonArray)?.Select(n => n?.GetValue<string>() ?? "").Where(x => ExecutionTarget.IsSpec(x)).ToList()
                ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return Array.Empty<string>(); }
    }

    /// <summary>Adds an interpreter of a WSL distribution or a Docker image to the project and makes it the project's.</summary>
    /// <exception cref="ServiceException">It does not answer as a Python interpreter.</exception>
    public PythonInterpreter AddRemote(ExecutionTarget target, string python)
    {
        string spec = target.Spec(python);
        var r = InterpreterInspector.Inspect(spec, "remote", "added");
        if (r.Interpreter == null) throw new ServiceException($"'{python}' in {target.Name} is not a usable Python interpreter: {r.Error}");
        var list = RemoteSpecs().ToList();
        if (!list.Contains(spec, StringComparer.OrdinalIgnoreCase)) list.Add(spec);
        SaveRemote(list);
        return Set(r.Interpreter, save: true)!;
    }

    /// <summary>Takes a WSL or Docker interpreter out of the project's list (it is not uninstalled anywhere).</summary>
    public void ForgetRemote(string spec) => SaveRemote(RemoteSpecs().Where(x => !string.Equals(x, spec, StringComparison.OrdinalIgnoreCase)).ToList());

    void SaveRemote(IReadOnlyList<string> specs)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(SettingsFile)!);
            var settings = new JsonObject();
            try { if (File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject existing) settings = existing; }
            catch (JsonException) { }
            settings["remote"] = new JsonArray(specs.Select(x => (JsonNode)JsonValue.Create(x)!).ToArray());
            File.WriteAllText(SettingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // ------------------------------------------------------------ settings file

    string? ReadSavedPath()
    {
        try
        {
            if (!File.Exists(SettingsFile)) return null;
            return JsonNode.Parse(File.ReadAllText(SettingsFile))?["interpreter"]?.GetValue<string>();
        }
        catch (Exception e) when (e is IOException or JsonException or InvalidOperationException) { return null; }
    }

    void Save(string path)
    {
        try
        {
            string dir = Path.GetDirectoryName(SettingsFile)!;
            Directory.CreateDirectory(dir);
            string ignore = Path.Combine(dir, ".gitignore");
            if (!File.Exists(ignore)) File.WriteAllText(ignore, "# machine-specific settings of PyMagic\n*\n");
            var settings = new JsonObject();
            try { if (File.Exists(SettingsFile) && JsonNode.Parse(File.ReadAllText(SettingsFile)) is JsonObject existing) settings = existing; }
            catch (JsonException) { }
            settings["interpreter"] = path;
            File.WriteAllText(SettingsFile, settings.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { /* the choice just is not remembered */ }
        catch (UnauthorizedAccessException) { }
    }
}
