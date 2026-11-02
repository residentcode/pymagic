using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// A Docker container that is already running as the interpreter: <c>docker:container=web|python</c>, with <c>?w=/app</c> after
/// the name when the container has the project somewhere else than /workspace. Every process is <c>docker exec -i</c> in that
/// container; nothing is started, mounted or removed, so the container has to have the project folder mounted itself (a bind
/// mount of the project). Without <c>w</c>, where it is mounted is read from <c>docker inspect</c> (the bind mount whose source is
/// the project, or a folder above it); /workspace when none is. The helper scripts of the debugger, test runner and notebook
/// kernel are copied in with <c>docker cp</c> (to /pymagic/&lt;n&gt;, as the Docker target mounts them). Its libraries are copied
/// out with <c>docker cp</c> once, for the editor.
/// </summary>
public sealed class ContainerTarget : ExecutionTarget
{
    public string Container { get; }
    /// <summary>Where the container has the project: <c>w</c> when the spec names it, else what <c>docker inspect</c> says, else /workspace.</summary>
    public string WorkDirInContainer
    {
        get
        {
            if (_workDir != null) return _workDir;
            // asked once the project is known ("" when docker says nothing useful)
            if (_found == null && (_paths.Project ?? DockerTarget.DefaultProject) is { } project)
                _found = HelperCopies.FindMountOf(_docker, Container, project) ?? "";
            return _found is { Length: > 0 } f ? f : DockerTarget.Workspace;
        }
    }

    readonly string? _workDir;
    string? _found;
    readonly Func<string[], string?> _docker;
    readonly HelperCopies _copies;

    // the /workspace and /pymagic/<n> bookkeeping is the Docker target's
    readonly DockerTarget _paths;

    /// <param name="docker">Runs the docker program and gives its output (null when it fails); for tests.</param>
    public ContainerTarget(string container, string? workDir = null, Func<string[], string?>? docker = null)
    {
        Container = container;
        _workDir = string.IsNullOrWhiteSpace(workDir) ? null : workDir.TrimEnd('/') is { Length: > 0 } w ? w : "/";
        _docker = docker ?? DockerTarget.Docker;
        _copies = new HelperCopies(_docker);
        _paths = new DockerTarget("container:" + container);
    }

    static readonly Regex ContainerName = new(@"^[A-Za-z0-9][A-Za-z0-9_.-]*$", RegexOptions.Compiled);

    /// <summary>The target from what follows "docker:container=" in a spec; null when the name is not a container name.</summary>
    public static ContainerTarget? FromHead(string head)
    {
        string name = head, query = "";
        int q = head.IndexOf('?');
        if (q >= 0) { name = head[..q]; query = head[(q + 1)..]; }
        if (!ContainerName.IsMatch(name)) return null;
        string? w = query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == "w" && p.Length == 2)?[1];
        if (w != null && !w.StartsWith('/')) return null;
        return new ContainerTarget(name, w);
    }

    public override string Kind => "container";
    public override string Name => $"Docker container: {Container}";

    // only a place the spec named is written back: a found one is found again
    public override string Spec(string python) =>
        $"docker:container={Container}{(_workDir != null && _workDir != DockerTarget.Workspace ? "?w=" + _workDir : "")}|{python}";

    /// <summary>The project folder (what is mounted in the container); see <see cref="DockerTarget.Project"/>.</summary>
    public string? Project { get => _paths.Project; set => _paths.Project = value; }

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        var (share, _) = _paths.PrepareShare(workDir, mounts);
        // nothing can be mounted into a running container: the helper scripts are copied in where the Docker target would mount them
        _copies.CopyInto(Container, _paths.Mounts, Name);

        string runId = Guid.NewGuid().ToString("N")[..12];
        string cwd = ToTarget(workDir);
        if (!IsUnderProject(cwd)) cwd = WorkDirInContainer;
        var psi = new ProcessStartInfo("docker") { WorkingDirectory = Directory.Exists(share) ? share : Directory.Exists(workDir) ? workDir : Path.GetTempPath() };
        psi.ArgumentList.Add("exec"); psi.ArgumentList.Add("-i");
        psi.ArgumentList.Add("-w"); psi.ArgumentList.Add(cwd);
        if (env != null)
            foreach (var (k, v) in env) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add($"{k}={(HostPath.IsMatch(v) ? string.Join(":", v.Split(';').Select(ToTarget)) : v)}"); }
        psi.ArgumentList.Add(Container);
        // the shell writes its process id where Stop finds it, then becomes the program (same id): killing the docker client alone would leave it running
        psi.ArgumentList.Add("sh"); psi.ArgumentList.Add("-c"); psi.ArgumentList.Add($"echo $$ > {PidFile(runId)}; exec \"$@\""); psi.ArgumentList.Add("sh");
        psi.ArgumentList.Add(python);
        foreach (var a in args) psi.ArgumentList.Add(HostPath.IsMatch(a) ? ToTarget(a) : a);
        psi.Environment["PYMAGIC_RUN_ID"] = runId;
        return psi;
    }

    static string PidFile(string runId) => $"/tmp/.pymagic-{runId}";

    bool IsUnderProject(string targetPath)
    {
        string root = WorkDirInContainer.TrimEnd('/');
        return targetPath == root || targetPath.StartsWith(root + "/", StringComparison.Ordinal);
    }

    public override void Stop(string runId)
    {
        if (!Regex.IsMatch(runId, "^[0-9a-f]{12}$")) return;
        _docker(new[] { "exec", Container, "sh", "-c", $"kill $(cat {PidFile(runId)}) 2>/dev/null; rm -f {PidFile(runId)}" });
    }

    public override string ToTarget(string hostPath)
    {
        string t = _paths.ToTarget(hostPath);
        if (WorkDirInContainer != DockerTarget.Workspace && (t == DockerTarget.Workspace || t.StartsWith(DockerTarget.Workspace + "/", StringComparison.Ordinal)))
            return WorkDirInContainer.TrimEnd('/') + t[DockerTarget.Workspace.Length..];
        return t;
    }

    public override string ToHost(string targetPath)
    {
        if (WorkDirInContainer != DockerTarget.Workspace)
        {
            string root = WorkDirInContainer.TrimEnd('/');
            if (targetPath == WorkDirInContainer || targetPath == root) return _paths.ToHost(DockerTarget.Workspace);
            if (targetPath.StartsWith(root + "/", StringComparison.Ordinal)) return _paths.ToHost(DockerTarget.Workspace + targetPath[root.Length..]);
        }
        return _paths.ToHost(targetPath);
    }

    public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Any, "host.docker.internal");

    // ------------------------------------------------------------ the terminal

    public override (string Program, IReadOnlyList<string> Args)? ShellCommand(string workDir)
    {
        string cwd = ToTarget(workDir);
        if (!IsUnderProject(cwd)) cwd = WorkDirInContainer;
        return ("docker", new[] { "exec", "-it", "-w", cwd, Container, "sh" });
    }

    // ------------------------------------------------------------ libraries

    string MirrorRoot => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "mirrors",
        "c" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Container)))[..11]);

    string MirrorPathOf(string targetPath) => Path.Combine(MirrorRoot, targetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    public override string? Readable(string targetPath) => targetPath.StartsWith('/') ? MirrorPathOf(targetPath) : null;

    /// <summary>Copies the container's library folders out with <c>docker cp</c> (once; a copy that exists is kept); returns those that could not be copied.</summary>
    public override IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders)
    {
        var failed = new List<string>();
        foreach (var folder in targetFolders.Where(f => f.StartsWith('/') && !Directory.Exists(MirrorPathOf(f))).Distinct())
        {
            string dest = MirrorPathOf(folder);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            if (DockerTarget.Docker("cp", $"{Container}:{folder}", dest) == null) failed.Add(folder);
        }
        return failed;
    }

    /// <summary>The names of the running containers (empty when Docker does not answer).</summary>
    public static IReadOnlyList<string> Running() =>
        (DockerTarget.Docker("ps", "--format", "{{.Names}}") ?? "").Split('\n').Select(l => l.Trim()).Where(l => ContainerName.IsMatch(l)).ToList();
}

/// <summary>
/// What the two forms that run in a container that is already running (a container, a Compose service with exec) share: finding where the
/// container has the project from <c>docker inspect</c>, and copying the helper folders in (they cannot be mounted into a running container).
/// </summary>
public sealed class HelperCopies
{
    readonly Func<string[], string?> _docker;
    // what was copied where, so a folder is copied once a container (copied again when the container is a new one: its id changes)
    readonly HashSet<string> _done = new(StringComparer.Ordinal);

    public HelperCopies(Func<string[], string?> docker) { _docker = docker; }

    /// <summary>
    /// Copies each helper folder to /pymagic/&lt;n&gt; in the container (n: its place in <paramref name="mounts"/>), as root, so the programs that
    /// need them find them where the Docker target mounts them. Each folder once per container.
    /// </summary>
    public void CopyInto(string container, IReadOnlyList<string> mounts, string name)
    {
        if (mounts.Count == 0) return;
        string id = (_docker(new[] { "inspect", "--format", "{{.Id}}", container }) ?? "").Trim();
        if (id.Length == 0) throw new ServiceException($"{name}: the container is not running (docker inspect does not know it)");
        for (int i = 0; i < mounts.Count; i++)
        {
            string key = id + "|" + i + "|" + mounts[i];
            if (_done.Contains(key)) continue;
            if (_docker(new[] { "exec", "-u", "0", container, "sh", "-c", $"rm -rf /pymagic/{i} && mkdir -p /pymagic" }) == null)
                throw new ServiceException($"{name}: the helper scripts could not be put in the container (/pymagic could not be made as root)");
            // "folder/." copies what is in it, into /pymagic/<n> (which docker cp makes)
            if (_docker(new[] { "cp", Path.Combine(mounts[i], "."), $"{container}:/pymagic/{i}" }) == null)
                throw new ServiceException($"{name}: the helper scripts could not be copied into the container (docker cp failed)");
            _done.Add(key);
        }
    }

    /// <summary>Where the container has the project (from its bind mounts), or null when docker does not answer or no mount holds the project.</summary>
    public static string? FindMountOf(Func<string[], string?> docker, string container, string? project)
    {
        if (project == null) return null;
        string? json = docker(new[] { "inspect", "--format", "{{json .Mounts}}", container });
        return json == null ? null : MountPointIn(json, project);
    }

    /// <summary>
    /// The place in the container of a project folder of this computer, from the mounts <c>docker inspect --format "{{json .Mounts}}"</c> gives:
    /// the bind mount whose source is the project (or the deepest one above it, with the rest of the path added). Docker Desktop writes a
    /// source as <c>C:\x</c>, <c>/run/desktop/mnt/host/c/x</c>, <c>/host_mnt/c/x</c> or <c>/mnt/c/x</c>; all are read as <c>c:/x</c>.
    /// </summary>
    public static string? MountPointIn(string json, string project)
    {
        System.Text.Json.Nodes.JsonArray? mounts;
        try { mounts = System.Text.Json.Nodes.JsonNode.Parse(json) as System.Text.Json.Nodes.JsonArray; }
        catch (System.Text.Json.JsonException) { return null; }
        if (mounts == null) return null;
        string want = Normal(project);
        string? best = null;
        int bestLength = -1;
        foreach (var m in mounts.OfType<System.Text.Json.Nodes.JsonObject>())
        {
            if (m["Type"]?.GetValue<string>() != "bind") continue;
            if (m["Source"]?.GetValue<string>() is not { } source || m["Destination"]?.GetValue<string>() is not { } destination || !destination.StartsWith('/')) continue;
            string from = Normal(source);
            if (from.Length == 0) continue;
            string? rest = want == from ? "" : want.StartsWith(from + "/", StringComparison.Ordinal) ? want[from.Length..] : null;
            if (rest == null || from.Length <= bestLength) continue;
            best = destination.TrimEnd('/') + rest;
            if (best.Length == 0) best = "/";
            bestLength = from.Length;
        }
        return best;
    }

    /// <summary>A Windows or Docker Desktop path as "c:/a/b", lower case, without a slash at the end; other paths as they are.</summary>
    static string Normal(string path)
    {
        string p = path.Replace('\\', '/').TrimEnd('/');
        // C:/x
        var windows = Regex.Match(p, @"^([A-Za-z]):(/.*)?$");
        if (windows.Success) return (windows.Groups[1].Value + ":" + windows.Groups[2].Value).ToLowerInvariant();
        // /run/desktop/mnt/host/c/x, /host_mnt/c/x, /mnt/c/x, /c/x: a drive as Docker Desktop and WSL write it
        var desktop = Regex.Match(p, @"^(?:/run/desktop/mnt/host|/host_mnt|/mnt)?/([A-Za-z])(/.*)?$");
        if (desktop.Success) return (desktop.Groups[1].Value + ":" + desktop.Groups[2].Value).ToLowerInvariant();
        return p;
    }
}
