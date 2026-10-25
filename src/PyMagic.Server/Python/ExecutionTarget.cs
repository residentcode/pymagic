using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// Where a Python interpreter runs: on this machine, in a WSL distribution, or in a Docker container. The target decides how
/// a process is started there, how a path on this machine looks from there and back, and how a helper that runs there
/// (debugger, test runner, notebook kernel) reaches this program. An interpreter of another target is named by a
/// <i>spec</i>: <c>wsl:Ubuntu-24.04|/usr/bin/python3</c> or <c>docker:python:3.12|python</c>.
/// </summary>
public abstract class ExecutionTarget
{
    public static readonly ExecutionTarget Local = new LocalTarget();

    /// <summary>"local", "wsl" or "docker".</summary>
    public abstract string Kind { get; }
    public bool IsLocal => Kind == "local";

    /// <summary>A short name for lists: "WSL: Ubuntu-24.04", "Docker: python:3.12".</summary>
    public abstract string Name { get; }

    /// <summary>The name an interpreter of this target has in settings and in the interpreter list.</summary>
    public abstract string Spec(string python);

    /// <summary>The target and the interpreter path inside it, from a spec; null for an ordinary path of this machine.</summary>
    public static (ExecutionTarget Target, string Python)? Parse(string spec)
    {
        int bar = spec.IndexOf('|');
        if (bar < 0) return null;
        string head = spec[..bar], python = spec[(bar + 1)..];
        if (python.Length == 0) return null;
        if (head.StartsWith("wsl:", StringComparison.Ordinal) && head.Length > 4) return (new WslTarget(head[4..]), python);
        if (head.StartsWith("docker:", StringComparison.Ordinal) && head.Length > 7) return (new DockerTarget(head[7..]), python);
        if (head.StartsWith("ssh:", StringComparison.Ordinal) && head.Length > 4 && SshTarget.FromHead(head[4..]) is { } ssh) return (ssh, python);
        return null;
    }

    public static bool IsSpec(string path) => Parse(path) != null;

    /// <summary>
    /// A process that runs <paramref name="python"/> (as the target names it) with the arguments, in <paramref name="workDir"/>
    /// (a folder of this machine). Arguments that are paths of this machine are translated; <paramref name="mounts"/> are
    /// further folders of this machine the program needs to read (helper scripts).
    /// </summary>
    public abstract ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null);

    /// <summary>How the target sees a path of this machine (paths it cannot see are returned unchanged).</summary>
    public abstract string ToTarget(string hostPath);

    /// <summary>How this machine sees a path of the target (a path that is not shared is returned unchanged).</summary>
    public abstract string ToHost(string targetPath);

    /// <summary>
    /// A path of this machine under which a file of the target can be read (its libraries), or null when it cannot be read
    /// from here. For WSL that is the distribution's network path.
    /// </summary>
    public abstract string? Readable(string targetPath);

    /// <summary>
    /// The address a helper in the target connects to and the interface this program listens on for it.
    /// Loopback for the local machine; the address of the virtual switch for WSL.
    /// </summary>
    public abstract (IPAddress Listen, string Connect) HelperAddress();

    // ---- what a target that does not share the project's files has to do (this computer, WSL and Docker share them: nothing)

    /// <summary>Gets the project (and the folders of helper scripts) to where the interpreter can read them. Nothing to do when the files are shared.</summary>
    /// <exception cref="ServiceException">The copy failed.</exception>
    public virtual void Prepare(string projectRoot, IReadOnlyList<string>? mounts, Action<string>? progress) { }

    /// <summary>Stops a program that <see cref="Start"/> began (<paramref name="runId"/> is the value it put in PYMAGIC_RUN_ID), where killing the local process is not enough.</summary>
    public virtual void Stop(string runId) { }

    /// <summary>Brings a file the program made (a profile, a coverage result) back to this machine. False when it is not there or the target shares the files anyway.</summary>
    public virtual bool Fetch(string targetPath, string hostPath) => false;

    /// <summary>Whether a file of the target exists.</summary>
    public virtual bool Exists(string targetPath) => File.Exists(ToHost(targetPath));

    /// <summary>Makes folders of the target readable from here (a copy of their source files); returns those that could not be copied.</summary>
    public virtual IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders) => Array.Empty<string>();

    protected static readonly Regex HostPath = new(@"^(?:[A-Za-z]:[\\/]|\\\\)", RegexOptions.Compiled);

    // ------------------------------------------------------------ this machine

    sealed class LocalTarget : ExecutionTarget
    {
        public override string Kind => "local";
        public override string Name => "this computer";
        public override string Spec(string python) => python;

        public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
        {
            var psi = new ProcessStartInfo(python) { WorkingDirectory = workDir };
            foreach (var a in args) psi.ArgumentList.Add(a);
            if (env != null) foreach (var (k, v) in env) psi.Environment[k] = v;
            return psi;
        }

        public override string ToTarget(string hostPath) => hostPath;
        public override string ToHost(string targetPath) => targetPath;
        public override string? Readable(string targetPath) => targetPath;
        public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Loopback, "127.0.0.1");
    }
}

/// <summary>A distribution of Windows Subsystem for Linux: files of this machine are under /mnt/&lt;drive&gt;, its own files are shared as \\wsl.localhost\&lt;distro&gt;.</summary>
public sealed class WslTarget : ExecutionTarget
{
    public string Distro { get; }
    public WslTarget(string distro) { Distro = distro; }

    public override string Kind => "wsl";
    public override string Name => $"WSL: {Distro}";
    public override string Spec(string python) => $"wsl:{Distro}|{python}";

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        var psi = new ProcessStartInfo("wsl.exe") { WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetTempPath() };
        psi.ArgumentList.Add("-d"); psi.ArgumentList.Add(Distro);
        psi.ArgumentList.Add("--cd"); psi.ArgumentList.Add(ToTarget(workDir));
        psi.ArgumentList.Add("--exec");
        // no shell: variables are set with env, and nothing in an argument is interpreted
        if (env is { Count: > 0 })
        {
            psi.ArgumentList.Add("env");
            foreach (var (k, v) in env) psi.ArgumentList.Add($"{k}={(HostPath.IsMatch(v) ? string.Join(":", v.Split(';').Select(ToTarget)) : v)}");
        }
        psi.ArgumentList.Add(python);
        foreach (var a in args) psi.ArgumentList.Add(HostPath.IsMatch(a) ? ToTarget(a) : a);
        return psi;
    }

    /// <summary>C:\a\b is /mnt/c/a/b; \\wsl.localhost\&lt;this distro&gt;\home\x is /home/x.</summary>
    public override string ToTarget(string hostPath)
    {
        var unc = Regex.Match(hostPath, @"^\\\\(?:wsl\.localhost|wsl\$)\\([^\\]+)(\\.*)?$", RegexOptions.IgnoreCase);
        if (unc.Success && string.Equals(unc.Groups[1].Value, Distro, StringComparison.OrdinalIgnoreCase))
            return unc.Groups[2].Success ? unc.Groups[2].Value.Replace('\\', '/') : "/";
        var drive = Regex.Match(hostPath, @"^([A-Za-z]):[\\/]?(.*)$");
        if (drive.Success) return "/mnt/" + char.ToLowerInvariant(drive.Groups[1].Value[0]) + (drive.Groups[2].Value.Length > 0 ? "/" + drive.Groups[2].Value.Replace('\\', '/') : "");
        return hostPath;
    }

    public override string ToHost(string targetPath)
    {
        var mnt = Regex.Match(targetPath, @"^/mnt/([a-z])(?:/(.*))?$");
        if (mnt.Success) return char.ToUpperInvariant(mnt.Groups[1].Value[0]) + @":\" + (mnt.Groups[2].Success ? mnt.Groups[2].Value.Replace('/', '\\') : "");
        return targetPath.StartsWith('/') ? $@"\\wsl.localhost\{Distro}" + targetPath.Replace('/', '\\') : targetPath;
    }

    public override string? Readable(string targetPath) => targetPath.StartsWith('/') ? ToHost(targetPath) : null;

    string? _hostAddress;

    /// <summary>The distribution reaches this machine at its default gateway (the virtual switch), which is also where this program listens for it.</summary>
    public override (IPAddress Listen, string Connect) HelperAddress()
    {
        _hostAddress ??= FindGateway();
        if (_hostAddress != null && IPAddress.TryParse(_hostAddress, out var ip) && OwnsAddress(ip)) return (ip, _hostAddress);
        return (IPAddress.Loopback, "127.0.0.1");     // mirrored networking: the distribution shares the loopback
    }

    string? FindGateway()
    {
        var psi = new ProcessStartInfo("wsl.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in new[] { "-d", Distro, "--exec", "ip", "route", "show", "default" }) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd();
            p.WaitForExit(15000);
            var m = Regex.Match(output, @"default via (\d+\.\d+\.\d+\.\d+)");
            return m.Success ? m.Groups[1].Value : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    static bool OwnsAddress(IPAddress ip)
    {
        try { return NetworkInterface.GetAllNetworkInterfaces().SelectMany(n => n.GetIPProperties().UnicastAddresses).Any(a => a.Address.Equals(ip)); }
        catch (NetworkInformationException) { return false; }
    }

    /// <summary>The names of the installed distributions (wsl.exe answers in UTF-16); empty when WSL is not installed.</summary>
    public static IReadOnlyList<string> Distros()
    {
        var psi = new ProcessStartInfo("wsl.exe", "-l -q") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = Encoding.Unicode };
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return Array.Empty<string>();
            string output = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(15000) || p.ExitCode != 0) return Array.Empty<string>();
            return output.Split('\n').Select(l => l.Trim().Trim('\0', '\uFEFF')).Where(l => l.Length > 0 && !l.StartsWith("docker-desktop", StringComparison.OrdinalIgnoreCase)).ToList();
        }
        catch (System.ComponentModel.Win32Exception) { return Array.Empty<string>(); }
    }
}

/// <summary>
/// An image run with <c>docker run --rm -i</c> for every process, with the project folder shared as /workspace and helper
/// scripts as /pymagic/&lt;n&gt;. The container is not kept: what the code writes outside /workspace is gone afterwards.
/// </summary>
public sealed class DockerTarget : ExecutionTarget
{
    public const string Workspace = "/workspace";
    public string Image { get; }
    public DockerTarget(string image) { Image = image; }

    public override string Kind => "docker";
    public override string Name => $"Docker: {Image}";
    public override string Spec(string python) => $"docker:{Image}|{python}";

    /// <summary>The project folder, shared with the container as /workspace. Set by whoever owns the project; until then the first folder a process is started in.</summary>
    public string? Project { get; set; }

    /// <summary>The project of this program when a target was made from a spec (one project per host), used while <see cref="Project"/> is not set.</summary>
    public static string? DefaultProject { get; set; }

    // folders shared read-only besides the project (helper scripts): /pymagic/0, /pymagic/1, ... in the order they were first needed, for good
    readonly List<string> _mounts = new();

    string Workspace_ => (Project ?? DefaultProject) is { } project ? Path.GetFullPath(project) : (_workspace ??= Path.GetTempPath());
    string? _workspace;

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        if (Project == null && DefaultProject == null && _workspace == null) _workspace = Path.GetFullPath(workDir);
        string share = Workspace_;
        if (mounts != null)
            foreach (var m in mounts.Select(Path.GetFullPath))
                if (!_mounts.Contains(m, StringComparer.OrdinalIgnoreCase)) _mounts.Add(m);

        // a folder outside the project (a throw-away one for asking pip) cannot be entered: work in /workspace
        string cwd = ToTarget(workDir);
        if (!cwd.StartsWith(DockerTarget.Workspace, StringComparison.Ordinal)) cwd = DockerTarget.Workspace;

        var psi = new ProcessStartInfo("docker") { WorkingDirectory = Directory.Exists(workDir) ? workDir : Path.GetTempPath() };
        foreach (var a in new[] { "run", "--rm", "-i", "--add-host", "host.docker.internal:host-gateway", "-v", $"{share}:{Workspace}", "-w", cwd })
            psi.ArgumentList.Add(a);
        for (int i = 0; i < _mounts.Count; i++) { psi.ArgumentList.Add("-v"); psi.ArgumentList.Add($"{_mounts[i]}:/pymagic/{i}:ro"); }
        if (env != null)
            foreach (var (k, v) in env) { psi.ArgumentList.Add("-e"); psi.ArgumentList.Add($"{k}={(HostPath.IsMatch(v) ? string.Join(":", v.Split(';').Select(ToTarget)) : v)}"); }
        psi.ArgumentList.Add(Image);
        psi.ArgumentList.Add(python);
        foreach (var a in args) psi.ArgumentList.Add(HostPath.IsMatch(a) ? ToTarget(a) : a);
        return psi;
    }

    public override string ToTarget(string hostPath)
    {
        if (!HostPath.IsMatch(hostPath)) return hostPath;
        string full = Path.GetFullPath(hostPath);
        string? Under(string folder, string at)
        {
            string prefix = folder.TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            if (string.Equals(full, folder.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) return at;
            return full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? at + "/" + full[prefix.Length..].Replace('\\', '/') : null;
        }
        for (int i = 0; i < _mounts.Count; i++) if (Under(_mounts[i], $"/pymagic/{i}") is { } m) return m;
        return Under(Workspace_, Workspace) ?? hostPath;
    }

    public override string ToHost(string targetPath)
    {
        string share = Workspace_;
        if (targetPath == Workspace) return share;
        if (targetPath.StartsWith(Workspace + "/", StringComparison.Ordinal)) return Path.Combine(share, targetPath[(Workspace.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
        var m = Regex.Match(targetPath, @"^/pymagic/(\d+)(?:/(.*))?$");
        if (m.Success && int.Parse(m.Groups[1].Value) < _mounts.Count)
            return m.Groups[2].Success ? Path.Combine(_mounts[int.Parse(m.Groups[1].Value)], m.Groups[2].Value.Replace('/', Path.DirectorySeparatorChar)) : _mounts[int.Parse(m.Groups[1].Value)];
        return targetPath;
    }

    /// <summary>Libraries inside the image are copied out (see <see cref="Mirror"/>); only then can they be read from here.</summary>
    public override string? Readable(string targetPath) => MirrorPathOf(targetPath);

    /// <summary>Docker Desktop and Docker Engine (with the host-gateway entry Start adds) both reach this machine as host.docker.internal.</summary>
    public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Any, "host.docker.internal");

    // ------------------------------------------------------------ libraries of the image

    string MirrorRoot => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "mirrors",
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(Image)))[..12]);

    string MirrorPathOf(string targetPath) => Path.Combine(MirrorRoot, targetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Copies the image's library folders out with <c>docker create</c> and <c>docker cp</c> (once; a copy that exists is kept),
    /// so the editor can read the packages the code imports. Returns the folders that could not be copied.
    /// </summary>
    public override IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders)
    {
        var failed = new List<string>();
        var wanted = targetFolders.Where(f => f.StartsWith('/') && !Directory.Exists(MirrorPathOf(f))).Distinct().ToList();
        if (wanted.Count == 0) return failed;
        string? id = null;
        try
        {
            id = Docker("create", Image)?.Trim();
            if (string.IsNullOrEmpty(id)) return wanted;
            foreach (var folder in wanted)
            {
                string dest = MirrorPathOf(folder);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (Docker("cp", $"{id}:{folder}", dest) == null) failed.Add(folder);
            }
        }
        finally { if (!string.IsNullOrEmpty(id)) Docker("rm", id!); }
        return failed;
    }

    static string? Docker(params string[] args)
    {
        var psi = new ProcessStartInfo("docker") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        try
        {
            using var p = Process.Start(psi);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            p.StandardError.ReadToEnd();
            if (!p.WaitForExit(300000)) { try { p.Kill(true); } catch (InvalidOperationException) { } return null; }
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    /// <summary>Whether the docker command works and its engine answers.</summary>
    public static bool Available() => Docker("version", "--format", "{{.Server.Version}}") != null;

    /// <summary>Images that are already there (a run would not have to download them), "name:tag".</summary>
    public static IReadOnlyList<string> Images() =>
        (Docker("images", "--format", "{{.Repository}}:{{.Tag}}") ?? "").Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.Contains("<none>")).ToList();
}
