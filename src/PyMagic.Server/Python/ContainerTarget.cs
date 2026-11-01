using System.Diagnostics;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// A Docker container that is already running as the interpreter: <c>docker:container=web|python</c>, with <c>?w=/app</c> after
/// the name when the container has the project somewhere else than /workspace. Every process is <c>docker exec -i</c> in that
/// container; nothing is started, mounted or removed, so the container has to have the project folder mounted itself (a bind
/// mount of the project, at <c>w</c>), and the programs that need helper scripts mounted (debugger, test runner, notebook
/// kernel) cannot use it. Its libraries are copied out with <c>docker cp</c> once, for the editor.
/// </summary>
public sealed class ContainerTarget : ExecutionTarget
{
    public string Container { get; }
    /// <summary>Where the container has the project.</summary>
    public string WorkDirInContainer { get; }

    // the /workspace and /pymagic/<n> bookkeeping is the Docker target's
    readonly DockerTarget _paths;

    public ContainerTarget(string container, string? workDir = null)
    {
        Container = container;
        WorkDirInContainer = string.IsNullOrWhiteSpace(workDir) ? DockerTarget.Workspace : workDir.TrimEnd('/') is { Length: > 0 } w ? w : "/";
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

    public override string Spec(string python) =>
        $"docker:container={Container}{(WorkDirInContainer != DockerTarget.Workspace ? "?w=" + WorkDirInContainer : "")}|{python}";

    /// <summary>The project folder (what is mounted in the container); see <see cref="DockerTarget.Project"/>.</summary>
    public string? Project { get => _paths.Project; set => _paths.Project = value; }

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        var (share, _) = _paths.PrepareShare(workDir, mounts);
        if (_paths.Mounts.Count > 0)
            throw new ServiceException($"{Name} is a container that is already running, so the helper scripts of the debugger, test runner or notebook cannot be shared with it: use an image or a Compose service for those.");

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
        DockerTarget.Docker("exec", Container, "sh", "-c", $"kill $(cat {PidFile(runId)}) 2>/dev/null; rm -f {PidFile(runId)}");
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
