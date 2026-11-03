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
/// into a running container, so the service has to mount the project itself: <c>w</c> is where (without it, what <c>docker inspect</c>
/// of the service's container says; else /workspace). The helper scripts of the debugger, test runner and notebook kernel are copied
/// into that container with <c>docker cp</c>.</item>
/// </list>
/// The libraries of the service are copied out for the editor (<see cref="Mirror"/>): from the running container in the exec form, from
/// a container made of the service's image (and removed again) in the run form.
/// </summary>
public sealed class ComposeTarget : ExecutionTarget
{
    public string Service { get; }
    /// <summary>The compose file as the person wrote it (relative to the project, or absolute); null for the default names.</summary>
    public string? File { get; }
    public bool Exec { get; }
    /// <summary>Where the project is inside the container of an exec service: <c>w</c> when the spec names it, else what <c>docker inspect</c> of the service's container says, else /workspace.</summary>
    public string WorkDirInService
    {
        get
        {
            if (_workDir != null) return _workDir;
            if (!Exec) return DockerTarget.Workspace;
            // asked once the project is known ("" when docker says nothing useful)
            if (_found == null && (_paths.Project ?? DockerTarget.DefaultProject) is { } project)
                _found = (ContainerId() is { } id ? HelperCopies.FindMountOf(_docker, id, project) : null) ?? "";
            return _found is { Length: > 0 } f ? f : DockerTarget.Workspace;
        }
    }

    readonly string? _workDir;
    string? _found;
    readonly Func<string[], string?> _docker;
    readonly HelperCopies _copies;

    // /workspace and /pymagic/<n> bookkeeping is the Docker target's
    readonly DockerTarget _paths;

    readonly string? _mirrors;

    /// <param name="docker">Runs the docker program in the project folder and gives its output (null when it fails); for tests.</param>
    /// <param name="mirrors">The folder the libraries are copied to (for tests); else PyMagic's mirrors folder in LOCALAPPDATA.</param>
    public ComposeTarget(string service, string? file = null, bool exec = false, string? workDirInService = null, Func<string[], string?>? docker = null, string? mirrors = null)
    {
        _mirrors = mirrors;
        Service = service;
        File = string.IsNullOrWhiteSpace(file) ? null : file;
        Exec = exec;
        _workDir = string.IsNullOrWhiteSpace(workDirInService) ? null : workDirInService.TrimEnd('/') is { Length: > 0 } w ? w : "/";
        _paths = new DockerTarget("compose:" + service);
        _docker = docker ?? (a => RunIn(_paths.Project ?? DockerTarget.DefaultProject ?? Path.GetTempPath(), a));
        _copies = new HelperCopies(_docker);
    }

    /// <summary>The id of the service's running container (<c>docker compose ps -q</c>), or null.</summary>
    string? ContainerId()
    {
        var args = new List<string> { "compose" };
        if (File != null) { args.Add("-f"); args.Add(File); }
        args.AddRange(new[] { "ps", "-q", Service });
        string id = (_docker(args.ToArray()) ?? "").Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        return Regex.IsMatch(id, "^[0-9a-f]{12,64}$") ? id : null;
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
        if (Exec && _workDir != null && _workDir != DockerTarget.Workspace) options.Add("w=" + _workDir);
        return $"compose:{Service}{(options.Count > 0 ? "?" + string.Join("&", options) : "")}|{python}";
    }

    /// <summary>The project folder (what is /workspace); see <see cref="DockerTarget.Project"/>.</summary>
    public string? Project { get => _paths.Project; set => _paths.Project = value; }

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        var (share, cwd) = _paths.PrepareShare(workDir, mounts);
        // a running container cannot have anything mounted: the helper scripts are copied in where the run form mounts them
        if (Exec && _paths.Mounts.Count > 0)
            _copies.CopyInto(ContainerId() ?? throw new ServiceException($"{Name}: the service has no running container (docker compose ps)"), _paths.Mounts, Name);

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

    public override (string Program, IReadOnlyList<string> Args)? ShellCommand(string workDir)
    {
        var (share, cwd) = _paths.PrepareShare(workDir, null);
        var args = new List<string> { "compose" };
        if (File != null) { args.Add("-f"); args.Add(File); }
        if (Exec)
        {
            string w = Translate(workDir);
            args.AddRange(new[] { "exec", "-w", w.StartsWith('/') ? w : WorkDirInService, Service, "sh" });
        }
        else
            args.AddRange(new[] { "run", "--rm", "-v", $"{share}:{DockerTarget.Workspace}", "-w", cwd, Service, "sh" });
        return ("docker", args);
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

    // ------------------------------------------------------------ libraries of the service

    /// <summary>One folder per service of a compose file of a project (a service of another project, or of another file, is another one).</summary>
    string MirrorRoot => Path.Combine(_mirrors ?? Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "mirrors"),
        "k" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(
            $"{Path.GetFullPath(_paths.Project ?? DockerTarget.DefaultProject ?? ".")}|{File}|{Service}|{Exec}")))[..11]);

    string MirrorPathOf(string targetPath) => Path.Combine(MirrorRoot, targetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>The project's own files are read where they are; the libraries of the service from their copy (see <see cref="Mirror"/>).</summary>
    public override string? Readable(string targetPath)
    {
        if (!targetPath.StartsWith('/')) return null;
        string host = ToHost(targetPath);
        return host != targetPath ? host : MirrorPathOf(targetPath);
    }

    /// <summary>
    /// Copies the service's library folders out with <c>docker cp</c> (once; a copy that exists is kept), so the editor can read the packages
    /// the code imports: from the service's running container in the exec form; in the run form from a container made (not started) of the
    /// service's image, which is removed again. Returns the folders that could not be copied.
    /// </summary>
    public override IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders)
    {
        // the project's own folders are read in place
        var wanted = targetFolders.Where(f => f.StartsWith('/') && ToHost(f) == f && !Directory.Exists(MirrorPathOf(f))).Distinct().ToList();
        if (wanted.Count == 0) return wanted;
        string? made = null, from;
        if (Exec) from = ContainerId();
        else
        {
            from = ImageOf() is { } image ? (_docker(new[] { "create", image }) ?? "").Trim() : null;
            if (from is { Length: 0 }) from = null;
            made = from;
        }
        if (from == null) return wanted;
        var failed = new List<string>();
        try
        {
            foreach (var folder in wanted)
            {
                string dest = MirrorPathOf(folder);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (_docker(new[] { "cp", $"{from}:{folder}", dest }) == null) failed.Add(folder);
            }
        }
        finally { if (made != null) _docker(new[] { "rm", made }); }
        return failed;
    }

    /// <summary>
    /// The image of the service, from <c>docker compose config --format json</c>: its <c>image</c>, else (a service that is only built) the name compose
    /// gives what it builds, "project-service". Null when compose does not answer.
    /// </summary>
    string? ImageOf()
    {
        var args = new List<string> { "compose" };
        if (File != null) { args.Add("-f"); args.Add(File); }
        args.AddRange(new[] { "config", "--format", "json" });
        if (_docker(args.ToArray()) is not { Length: > 0 } json) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("services", out var services) || !services.TryGetProperty(Service, out var service)) return null;
            if (service.TryGetProperty("image", out var image) && image.GetString() is { Length: > 0 } named) return named;
            return root.TryGetProperty("name", out var project) && project.GetString() is { Length: > 0 } p ? $"{p}-{Service}" : null;
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

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
