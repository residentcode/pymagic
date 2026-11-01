using System.Diagnostics;
using System.Net;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// A service of a Docker Compose file as the interpreter. Spec: <c>compose:web|python</c>, with options after a question mark
/// like the ssh spec has them: <c>compose:web?f=docker-compose.dev.yml&amp;exec=1&amp;w=/app|/usr/local/bin/python</c>.
/// <list type="bullet">
/// <item><c>f</c> is the compose file, relative to the project (without it compose looks for compose.yaml and the like).</item>
/// <item>Without <c>exec</c> every process is <c>docker compose run --rm -T</c>: a new container of the service (and the services
/// it depends on), with the project folder shared as /workspace and helper scripts as /pymagic/&lt;n&gt;, like the Docker target.</item>
/// <item>With <c>exec</c> the process is <c>docker compose exec -T</c> in the service's running container. Nothing can be mounted
/// into a running container, so the service has to mount the project itself: <c>w</c> is where (default /workspace). Programs
/// that need helper scripts (debugger, test runner, notebook kernel) cannot use this form.</item>
/// </list>
/// Not tried against a real Docker engine yet.
/// </summary>
public sealed class ComposeTarget : ExecutionTarget
{
    public string Service { get; }
    /// <summary>The compose file as the person wrote it (relative to the project, or absolute); null for the default names.</summary>
    public string? File { get; }
    public bool Exec { get; }
    /// <summary>Where the project is inside the container of an exec service.</summary>
    public string WorkDirInService { get; }

    // /workspace and /pymagic/<n> bookkeeping is the Docker target's
    readonly DockerTarget _paths;

    public ComposeTarget(string service, string? file = null, bool exec = false, string? workDirInService = null)
    {
        Service = service;
        File = string.IsNullOrWhiteSpace(file) ? null : file;
        Exec = exec;
        WorkDirInService = string.IsNullOrWhiteSpace(workDirInService) ? DockerTarget.Workspace : workDirInService.TrimEnd('/') is { Length: > 0 } w ? w : "/";
        _paths = new DockerTarget("compose:" + service);
    }

    /// <summary>The target from what follows "compose:" in a spec; null when the service name is not one.</summary>
    public static ComposeTarget? FromHead(string head)
    {
        string name = head, query = "";
        int q = head.IndexOf('?');
        if (q >= 0) { name = head[..q]; query = head[(q + 1)..]; }
        if (!ServiceName.IsMatch(name)) return null;
        string? Option(string key) => query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == key && p.Length == 2)?[1];
        string? file = Option("f");
        if (file != null && (file.Length == 0 || file.StartsWith('-'))) return null;
        return new ComposeTarget(name, file, Option("exec") is "1" or "true", Option("w"));
    }

    static readonly Regex ServiceName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    public override string Kind => "compose";
    public override string Name => $"Compose: {Service}{(File != null ? " (" + File + ")" : "")}{(Exec ? ", exec" : "")}";

    public override string Spec(string python)
    {
        var options = new List<string>();
        if (File != null) options.Add("f=" + File);
        if (Exec) options.Add("exec=1");
        if (Exec && WorkDirInService != DockerTarget.Workspace) options.Add("w=" + WorkDirInService);
        return $"compose:{Service}{(options.Count > 0 ? "?" + string.Join("&", options) : "")}|{python}";
    }

    /// <summary>The project folder (what is /workspace); see <see cref="DockerTarget.Project"/>.</summary>
    public string? Project { get => _paths.Project; set => _paths.Project = value; }

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        var (share, cwd) = _paths.PrepareShare(workDir, mounts);
        if (Exec && _paths.Mounts.Count > 0)
            throw new ServiceException($"{Name} runs in a container that is already running, so the helper scripts of the debugger, test runner or notebook cannot be shared with it: use the run form (without exec).");

        string Map(string v) => HostPath.IsMatch(v) ? string.Join(":", v.Split(';').Select(Translate)) : v;
        var psi = new ProcessStartInfo("docker") { WorkingDirectory = Directory.Exists(share) ? share : Directory.Exists(workDir) ? workDir : Path.GetTempPath() };
        psi.ArgumentList.Add("compose");
        if (File != null) { psi.ArgumentList.Add("-f"); psi.ArgumentList.Add(File); }
        if (Exec)
        {
            psi.ArgumentList.Add("exec"); psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(Translate(workDir) is var w && w.StartsWith('/') ? w : WorkDirInService);
        }
        else
        {
            psi.ArgumentList.Add("run"); psi.ArgumentList.Add("--rm"); psi.ArgumentList.Add("-T");
            psi.ArgumentList.Add("-v"); psi.ArgumentList.Add($"{share}:{DockerTarget.Workspace}");
            for (int i = 0; i < _paths.Mounts.Count; i++) { psi.ArgumentList.Add("-v"); psi.ArgumentList.Add($"{_paths.Mounts[i]}:/pymagic/{i}:ro"); }
            psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(cwd);
        }
        if (env != null)
            foreach (var (k, v) in env) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add($"{k}={Map(v)}"); }
        psi.ArgumentList.Add(Service);
        psi.ArgumentList.Add(python);
        foreach (var a in args) psi.ArgumentList.Add(HostPath.IsMatch(a) ? Translate(a) : a);
        return psi;
    }

    // a running service has the project where it mounted it (WorkDirInService); a new container gets it as /workspace
    string Translate(string hostPath) => ToTarget(hostPath);

    public override string ToTarget(string hostPath)
    {
        string t = _paths.ToTarget(hostPath);
        if (Exec && WorkDirInService != DockerTarget.Workspace && (t == DockerTarget.Workspace || t.StartsWith(DockerTarget.Workspace + "/", StringComparison.Ordinal)))
            return WorkDirInService.TrimEnd('/') + t[DockerTarget.Workspace.Length..];
        return t;
    }

    public override string ToHost(string targetPath)
    {
        if (Exec && WorkDirInService != DockerTarget.Workspace)
        {
            string root = WorkDirInService.TrimEnd('/');
            if (targetPath == WorkDirInService || targetPath == root) return _paths.ToHost(DockerTarget.Workspace);
            if (targetPath.StartsWith(root + "/", StringComparison.Ordinal)) return _paths.ToHost(DockerTarget.Workspace + targetPath[root.Length..]);
        }
        return _paths.ToHost(targetPath);
    }

    /// <summary>The libraries inside the service's image are not copied out (no container of it is created here), so the editor does not read them.</summary>
    public override string? Readable(string targetPath) => null;

    public override IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders) => targetFolders.Where(f => f.StartsWith('/')).Distinct().ToList();

    public override bool Exists(string targetPath) => System.IO.File.Exists(ToHost(targetPath));

    public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Any, "host.docker.internal");

    // ------------------------------------------------------------ the services of a compose file

    static readonly string[] DefaultNames = { "compose.yaml", "compose.yml", "docker-compose.yaml", "docker-compose.yml" };

    /// <summary>The compose file of a project folder, by the names compose itself looks for; null when there is none.</summary>
    public static string? FindFile(string projectFolder) =>
        DefaultNames.Select(n => Path.Combine(projectFolder, n)).FirstOrDefault(System.IO.File.Exists);

    /// <summary>
    /// The names of the services. <c>docker compose config --services</c> first (it needs no running engine, and knows about
    /// includes, extends and overrides); when that is not available the file is read here. <paramref name="docker"/> replaces
    /// the call of the docker program (for tests).
    /// </summary>
    public static IReadOnlyList<string> Services(string projectFolder, string? file = null, Func<string[], string?>? docker = null)
    {
        string? path = file != null ? (Path.IsPathRooted(file) ? file : Path.Combine(projectFolder, file)) : FindFile(projectFolder);
        if (path == null || !System.IO.File.Exists(path)) return Array.Empty<string>();
        var args = new List<string> { "compose" };
        if (file != null) { args.Add("-f"); args.Add(file); }
        args.AddRange(new[] { "config", "--services" });
        string? answer = null;
        try { answer = (docker ?? (a => RunIn(projectFolder, a)))(args.ToArray()); }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or InvalidOperationException) { }
        var listed = (answer ?? "").Split('\n').Select(l => l.Trim()).Where(l => ServiceName.IsMatch(l)).Distinct().ToList();
        if (listed.Count > 0) return listed;
        try { return ParseServices(System.IO.File.ReadAllText(path)); }
        catch (IOException) { return Array.Empty<string>(); }
    }

    static string? RunIn(string folder, string[] args)
    {
        var psi = new ProcessStartInfo("docker") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(20000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>
    /// The keys one level below a top-level <c>services:</c> in compose YAML, read by indentation (block style, which is how
    /// compose files are written). Comments, quotes and blank lines are skipped; a file in flow style gives nothing.
    /// </summary>
    public static IReadOnlyList<string> ParseServices(string yaml)
    {
        var found = new List<string>();
        bool inServices = false;
        int indent = -1;
        foreach (var raw in yaml.Split('\n'))
        {
            string line = raw.TrimEnd('\r');
            string trimmed = line.TrimStart(' ');
            if (trimmed.Length == 0 || trimmed[0] == '#') continue;
            int lead = line.Length - trimmed.Length;
            if (lead == 0)
            {
                if (trimmed == "---" || trimmed == "...") { if (inServices) break; continue; }
                inServices = Regex.IsMatch(trimmed, @"^services\s*:\s*(#.*)?$");
                indent = -1;
                continue;
            }
            if (!inServices) continue;
            if (indent < 0) indent = lead;
            if (lead != indent) continue;
            var m = Regex.Match(trimmed, @"^(?:""([^""]+)""|'([^']+)'|([^\s:#'""][^:#]*?))\s*:(?:\s|$)");
            if (!m.Success) continue;
            string name = (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value).Trim();
            if (ServiceName.IsMatch(name) && !found.Contains(name)) found.Add(name);
        }
        return found;
    }
}
