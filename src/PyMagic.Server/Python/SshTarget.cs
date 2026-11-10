using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PyMagic.Server;

/// <summary>
/// An interpreter on another machine, reached with the ssh program. Nothing is shared with it, so: the project (changed files only) is
/// copied there before a program starts, the debugger, test runner and notebook kernel reach back through a reverse tunnel, the
/// libraries it imports are copied here once to be read by the editor, and the results a program leaves in the project that this machine
/// needs (profile, coverage) are fetched when it ends. Authentication is by key or agent (nothing can type a password); a host seen for the
/// first time is trusted and remembered, a host whose key changed is refused. Spec: <c>ssh:user@host:port?i=KEYFILE|python3</c>.
/// </summary>
public sealed class SshTarget : ExecutionTarget
{
    public string Host { get; }
    public string? User { get; }
    public int Port { get; }
    public string? Identity { get; }
    public string? KnownHosts { get; }
    /// <summary>The project folder on the remote machine; by default a folder of its own under ~/.pymagic/projects.</summary>
    public string? RemoteDir { get; }

    /// <summary>The project folder of this machine that is copied there.</summary>
    public string? Project { get; set; }
    public static string? DefaultProject { get; set; }

    static readonly Regex NamePattern = new(@"^[A-Za-z0-9][A-Za-z0-9._\-]*$", RegexOptions.Compiled);

    public SshTarget(string host, string? user = null, int port = 22, string? identity = null, string? knownHosts = null, string? remoteDir = null)
    {
        if (!NamePattern.IsMatch(host)) throw new ServiceException($"'{host}' is not a host name");
        if (user != null && !NamePattern.IsMatch(user)) throw new ServiceException($"'{user}' is not a user name");
        if (port is < 1 or > 65535) throw new ServiceException("the port is a number from 1 to 65535");
        foreach (var path in new[] { identity, knownHosts, remoteDir })
            if (path != null && (path.StartsWith('-') || path.Contains('|') || path.Contains('?') || path.Contains('\0'))) throw new ServiceException($"'{path}' cannot be used here");
        Host = host; User = user; Port = port; Identity = identity; KnownHosts = knownHosts; RemoteDir = remoteDir;
    }

    /// <summary>From "user@host:port" with the options of a spec; null when it is not an address.</summary>
    public static SshTarget? FromHead(string head)
    {
        try
        {
            string address = head, query = "";
            int q = head.IndexOf('?');
            if (q >= 0) { address = head[..q]; query = head[(q + 1)..]; }
            string? user = null;
            int at = address.IndexOf('@');
            if (at >= 0) { user = address[..at]; address = address[(at + 1)..]; }
            int port = 22;
            int colon = address.LastIndexOf(':');
            if (colon >= 0) { port = int.Parse(address[(colon + 1)..]); address = address[..colon]; }
            string? Option(string key) => query.Split('&', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Split('=', 2)).FirstOrDefault(p => p[0] == key && p.Length == 2)?[1];
            return new SshTarget(address, user, port, Option("i"), Option("kh"), Option("dir"));
        }
        catch (Exception e) when (e is ServiceException or FormatException or OverflowException) { return null; }
    }

    public override string Kind => "ssh";
    public string Destination => User != null ? $"{User}@{Host}" : Host;
    public override string Name => $"SSH: {Destination}{(Port != 22 ? ":" + Port : "")}";

    public override string Spec(string python)
    {
        var options = new List<string>();
        if (Identity != null) options.Add("i=" + Identity);
        if (KnownHosts != null) options.Add("kh=" + KnownHosts);
        if (RemoteDir != null) options.Add("dir=" + RemoteDir);
        return $"ssh:{Destination}{(Port != 22 ? ":" + Port : "")}{(options.Count > 0 ? "?" + string.Join("&", options) : "")}|{python}";
    }

    public static bool Installed() => File.Exists(Path.Combine(Environment.SystemDirectory, "OpenSSH", "ssh.exe")) || OnPath("ssh");
    static bool OnPath(string program) => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Any(d => { try { return File.Exists(Path.Combine(d, program + ".exe")) || File.Exists(Path.Combine(d, program)); } catch { return false; } });

    // ------------------------------------------------------------ running ssh

    /// <summary>A string that a POSIX shell reads back as exactly this text.</summary>
    public static string Quote(string s) => "'" + s.Replace("'", "'\\''") + "'";

    IEnumerable<string> BaseArgs()
    {
        yield return "-T";                                         // no terminal: output stays as the program wrote it
        foreach (var o in new[] { "BatchMode=yes", "StrictHostKeyChecking=accept-new", "ServerAliveInterval=30", "ConnectTimeout=20" }) { yield return "-o"; yield return o; }
        if (KnownHosts != null) { yield return "-o"; yield return "UserKnownHostsFile=" + KnownHosts; }
        if (Port != 22) { yield return "-p"; yield return Port.ToString(); }
        if (Identity != null) { yield return "-i"; yield return Identity; yield return "-o"; yield return "IdentitiesOnly=yes"; }
    }

    ProcessStartInfo Ssh(string remoteCommand, IEnumerable<string>? extra = null)
    {
        var psi = new ProcessStartInfo("ssh") { CreateNoWindow = true, UseShellExecute = false, WorkingDirectory = Path.GetTempPath() };
        foreach (var a in BaseArgs()) psi.ArgumentList.Add(a);
        if (extra != null) foreach (var a in extra) psi.ArgumentList.Add(a);
        psi.ArgumentList.Add(Destination);
        psi.ArgumentList.Add(remoteCommand);
        return psi;
    }

    /// <summary>Runs a command on the machine and waits for it.</summary>
    public (int Code, string Out, string Err) Remote(string command, int timeoutMs = 60_000)
    {
        var psi = Ssh(command);
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false);
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("ssh did not start");
            p.StandardInput.Close();
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException($"{Destination} did not answer in time"); }
            p.WaitForExit();
            if (p.ExitCode == 255) throw new ServiceException($"cannot connect to {Destination}: {FirstLine(stderr.Result) ?? "ssh failed"}");
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException("the ssh program was not found (it comes with Windows 10 and later as an optional feature)"); }
    }

    /// <summary>Runs a command on the machine with this text as its standard input (which no command line limits), and waits for it.</summary>
    public (int Code, string Out, string Err) RemoteWithInput(string command, string input, int timeoutMs = 60_000)
    {
        var psi = Ssh(command);
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = true;
        psi.StandardOutputEncoding = new UTF8Encoding(false); psi.StandardErrorEncoding = new UTF8Encoding(false); psi.StandardInputEncoding = new UTF8Encoding(false);
        try
        {
            using var p = Process.Start(psi) ?? throw new ServiceException("ssh did not start");
            var stdout = p.StandardOutput.ReadToEndAsync();
            var stderr = p.StandardError.ReadToEndAsync();
            p.StandardInput.Write(input);
            p.StandardInput.Close();
            if (!p.WaitForExit(timeoutMs)) { try { p.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException($"{Destination} did not answer in time"); }
            p.WaitForExit();
            if (p.ExitCode == 255) throw new ServiceException($"cannot connect to {Destination}: {FirstLine(stderr.Result) ?? "ssh failed"}");
            return (p.ExitCode, stdout.Result, stderr.Result);
        }
        catch (System.ComponentModel.Win32Exception) { throw new ServiceException("the ssh program was not found (it comes with Windows 10 and later as an optional feature)"); }
    }

    static string? FirstLine(string text) => text.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.Length > 0);

    static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> Homes = new();
    string HomeKey => Destination + ":" + Port;

    /// <summary>The home folder of the user on the machine: asked once per machine (settable, for tests and for a machine that does not say).</summary>
    public string Home
    {
        get => Homes.TryGetValue(HomeKey, out var known) ? known : Homes[HomeKey] = Remote("printf %s \"$HOME\"").Out.Trim() is { Length: > 0 } h ? h : throw new ServiceException($"{Destination} has no home folder");
        set => Homes[HomeKey] = value;
    }

    static string Short(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToLowerInvariant())))[..10].ToLowerInvariant();

    string Local => Path.GetFullPath(Project ?? DefaultProject ?? Path.GetTempPath());

    /// <summary>Where the project is on the remote machine.</summary>
    public string ProjectDir => RemoteDir ?? $"{Home}/.pymagic/projects/{Short(Local)}";

    // folders shared besides the project (helper scripts), in the order they were first needed
    readonly List<string> _mounts = new();
    string RemoteMount(string hostDir) => $"{Home}/.pymagic/helpers/{Short(hostDir)}";

    public override ProcessStartInfo Start(string python, string workDir, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null, IReadOnlyList<string>? mounts = null)
    {
        if (mounts != null)
            foreach (var m in mounts.Select(Path.GetFullPath))
                if (!_mounts.Contains(m, StringComparer.OrdinalIgnoreCase)) _mounts.Add(m);

        string runId = Guid.NewGuid().ToString("N")[..12];
        string cwd = ToTarget(workDir);
        if (!cwd.StartsWith(ProjectDir, StringComparison.Ordinal)) cwd = ProjectDir;

        var list = args.ToList();
        var cmd = new StringBuilder($"mkdir -p {Quote(cwd)} && cd {Quote(cwd)} && exec ");
        if (env is { Count: > 0 })
        {
            cmd.Append("env ");
            foreach (var (k, v) in env) cmd.Append(Quote($"{k}={(HostPath.IsMatch(v) ? string.Join(":", v.Split(';').Select(ToTarget)) : v)}")).Append(' ');
        }
        // the marker is how a program that has to be stopped is found on the other machine (see Stop)
        cmd.Append(Quote(python)).Append(" -X ").Append(Quote("pymagic=" + runId));
        foreach (var a in list) cmd.Append(' ').Append(Quote(HostPath.IsMatch(a) ? ToTarget(a) : a));

        // a helper (debugger, test runner, kernel) connects to --port on that machine: the tunnel brings it to the same port here
        var extra = new List<string>();
        int at = list.IndexOf("--port");
        if (at >= 0 && at + 1 < list.Count && int.TryParse(list[at + 1], out int port))
        {
            extra.AddRange(new[] { "-o", "ExitOnForwardFailure=yes", "-R", $"{port}:127.0.0.1:{port}" });
        }
        var psi = Ssh(cmd.ToString(), extra);
        psi.Environment["PYMAGIC_RUN_ID"] = runId;
        return psi;
    }

    /// <summary>
    /// A program other than Python (PHP) on the machine, in the project's folder there: <paramref name="marker"/> gives the arguments that put the
    /// run's mark in its command line (PHP's <c>-d pymagic=ID</c>), and it runs in a process group of its own, so <see cref="StopGroup"/> ends what
    /// it started too (artisan serve's server). <paramref name="reverse"/>: ports of this machine it connects to (Xdebug's), brought to the same
    /// port there; <paramref name="forward"/>: ports it serves (artisan serve), brought to the same port here. <paramref name="unset"/>: variables
    /// taken out of its environment.
    /// </summary>
    public ProcessStartInfo StartProgram(string program, string workDir, Func<string, IEnumerable<string>> marker, IEnumerable<string> args, IReadOnlyDictionary<string, string>? env = null,
        IEnumerable<int>? reverse = null, IEnumerable<int>? forward = null, IEnumerable<string>? unset = null)
    {
        string runId = Guid.NewGuid().ToString("N")[..12];
        string cwd = ToTarget(workDir);
        if (!cwd.StartsWith(ProjectDir, StringComparison.Ordinal)) cwd = ProjectDir;
        var cmd = new StringBuilder($"mkdir -p {Quote(cwd)} && cd {Quote(cwd)} && exec setsid -w env");
        foreach (var u in unset ?? Array.Empty<string>()) cmd.Append(" -u ").Append(Quote(u));
        foreach (var (k, v) in env ?? new Dictionary<string, string>()) cmd.Append(' ').Append(Quote($"{k}={(HostPath.IsMatch(v) ? ToTarget(v) : v)}"));
        cmd.Append(' ').Append(Quote(program));
        foreach (var a in marker(runId)) cmd.Append(' ').Append(Quote(a));
        foreach (var a in args) cmd.Append(' ').Append(Quote(HostPath.IsMatch(a) ? ToTarget(a) : a));
        var extra = new List<string>();
        foreach (var p in (reverse ?? Array.Empty<int>()).Distinct()) extra.AddRange(new[] { "-R", $"{p}:127.0.0.1:{p}" });
        foreach (var p in (forward ?? Array.Empty<int>()).Distinct()) extra.AddRange(new[] { "-L", $"127.0.0.1:{p}:127.0.0.1:{p}" });
        if (extra.Count > 0) extra.InsertRange(0, new[] { "-o", "ExitOnForwardFailure=yes" });
        var psi = Ssh(cmd.ToString(), extra);
        psi.Environment["PYMAGIC_RUN_ID"] = runId;
        lock (_groups) _groups.Add(runId);
        return psi;
    }

    readonly HashSet<string> _groups = new();

    /// <summary>Ends a program <see cref="StartProgram"/> began, with every process of its group.</summary>
    public void StopGroup(string runId)
    {
        if (!Regex.IsMatch(runId, "^[0-9a-f]{12}$")) return;
        try { Remote($"for p in $(pgrep -f 'pymagic[=]{runId}'); do kill -TERM -- -$(ps -o pgid= -p $p | tr -d ' ') 2>/dev/null; done; true", 20_000); }
        catch (ServiceException) { /* the machine is gone, so is the program */ }
    }

    /// <summary>Runs a program on the machine in the project's folder there and waits for it (Artisan's list, PHP's version).</summary>
    public (int Code, string Out, string Err) RunInProject(string program, IEnumerable<string> args, int timeoutMs = 120_000)
        => Remote($"mkdir -p {Quote(ProjectDir)} && cd {Quote(ProjectDir)} && {Quote(program)} {string.Join(" ", args.Select(Quote))}", timeoutMs);

    /// <summary>A login shell on the machine in the project's folder there (a password may be typed: this one has a terminal).</summary>
    public override (string Program, IReadOnlyList<string> Args)? ShellCommand(string workDir)
    {
        string cwd = ToTarget(workDir);
        if (!cwd.StartsWith(ProjectDir, StringComparison.Ordinal)) cwd = ProjectDir;
        var args = new List<string> { "-t", "-o", "StrictHostKeyChecking=accept-new", "-o", "ServerAliveInterval=30" };
        if (KnownHosts != null) { args.Add("-o"); args.Add("UserKnownHostsFile=" + KnownHosts); }
        if (Port != 22) { args.Add("-p"); args.Add(Port.ToString()); }
        if (Identity != null) { args.Add("-i"); args.Add(Identity); }
        args.Add(Destination);
        args.Add($"mkdir -p {Quote(cwd)} && cd {Quote(cwd)} && exec ${{SHELL:-sh}} -l");
        return ("ssh", args);
    }

    public override void Stop(string runId)
    {
        if (!Regex.IsMatch(runId, "^[0-9a-f]{12}$")) return;
        bool group;
        lock (_groups) group = _groups.Remove(runId);
        if (group) { StopGroup(runId); return; }
        try { Remote($"pkill -f 'pymagic[=]{runId}' || true", 20_000); } catch (ServiceException) { /* the machine is gone, so is the program */ }
    }

    // ------------------------------------------------------------ paths

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
        foreach (var m in _mounts) if (Under(m, RemoteMount(m)) is { } mounted) return mounted;
        return Under(Local, ProjectDir) ?? hostPath;
    }

    public override string ToHost(string targetPath)
    {
        if (!targetPath.StartsWith('/')) return targetPath;
        string project = ProjectDir;
        if (targetPath == project) return Local;
        if (targetPath.StartsWith(project + "/", StringComparison.Ordinal)) return Path.Combine(Local, targetPath[(project.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
        foreach (var m in _mounts)
        {
            string remote = RemoteMount(m);
            if (targetPath.StartsWith(remote + "/", StringComparison.Ordinal)) return Path.Combine(m, targetPath[(remote.Length + 1)..].Replace('/', Path.DirectorySeparatorChar));
        }
        string mirrored = MirrorPathOf(targetPath);
        return File.Exists(mirrored) || Directory.Exists(mirrored) ? mirrored : targetPath;      // a library file: the copy of it made for the editor
    }

    public override string? Readable(string targetPath) => targetPath.StartsWith('/') ? MirrorPathOf(targetPath) : null;

    public override (IPAddress Listen, string Connect) HelperAddress() => (IPAddress.Loopback, "127.0.0.1");

    public override bool Exists(string targetPath) => Remote($"test -e {Quote(targetPath)}", 20_000).Code == 0;

    public override bool Fetch(string targetPath, string hostPath)
    {
        var psi = Ssh($"cat {Quote(targetPath)}");
        psi.RedirectStandardOutput = true; psi.RedirectStandardError = true; psi.RedirectStandardInput = true;
        using var p = Process.Start(psi);
        if (p == null) return false;
        p.StandardInput.Close();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(hostPath))!);
        string partial = hostPath + ".part";
        using (var file = File.Create(partial)) p.StandardOutput.BaseStream.CopyTo(file);
        p.StandardError.ReadToEnd();
        if (!p.WaitForExit(120_000) || p.ExitCode != 0) { try { File.Delete(partial); } catch (IOException) { } return false; }
        File.Move(partial, hostPath, overwrite: true);
        return true;
    }

    /// <summary>Files of a folder there (plain names) copied into a folder here, in one connection (tar); false when the copy failed.</summary>
    public bool FetchFiles(string targetDir, IReadOnlyCollection<string> names, string hostDir)
    {
        var plain = names.Where(n => n.Length > 0 && !n.Contains('/') && !n.Contains('\'') && n != "." && n != "..").ToList();
        return plain.Count == 0 || Untar($"cd {Quote(targetDir)} && tar -cf - -- {string.Join(" ", plain.Select(Quote))}", hostDir);
    }

    /// <summary>A whole folder there copied into a folder here (its modification times kept); false when the copy failed.</summary>
    public bool FetchFolder(string targetDir, string hostDir) => Untar($"cd {Quote(targetDir)} && tar -cf - .", hostDir);

    bool Untar(string remoteTar, string hostDir)
    {
        Directory.CreateDirectory(hostDir);
        var ssh = Ssh(remoteTar);
        ssh.RedirectStandardOutput = true; ssh.RedirectStandardError = true; ssh.RedirectStandardInput = true;
        var untar = LocalTar("-x", "-f", "-", "-C", hostDir);
        untar.RedirectStandardInput = true; untar.RedirectStandardError = true;
        using var s = Process.Start(ssh);
        using var u = Process.Start(untar);
        if (s == null || u == null) return false;
        s.StandardInput.Close();
        var sErr = s.StandardError.ReadToEndAsync();
        var uErr = u.StandardError.ReadToEndAsync();
        try { s.StandardOutput.BaseStream.CopyTo(u.StandardInput.BaseStream); u.StandardInput.Close(); } catch (IOException) { }
        bool done = s.WaitForExit(30 * 60_000) & u.WaitForExit(30 * 60_000);
        return done && s.ExitCode == 0 && u.ExitCode == 0;
    }

    /// <summary>
    /// The project here and its folder there hold the same files (it was made there and copied here): recorded as sent, so the next copy sends
    /// only what changes after.
    /// </summary>
    public void Adopt(string projectRoot)
    {
        Project = projectRoot;
        if (Remote($"touch {Quote(ProjectDir + "/" + Marker)}", 30_000).Code != 0) return;
        string recordFile = RecordFile(ProjectDir);
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordFile)!);
            File.WriteAllText(recordFile, new JsonObject(Files(Local, SkippedFolders).Select(f => KeyValuePair.Create(f.Rel, (JsonNode?)JsonValue.Create($"{f.Size}|{f.Ticks}")))).ToJsonString());
        }
        catch (IOException) { /* the first copy sends everything */ }
    }

    // ------------------------------------------------------------ copying the project

    static readonly HashSet<string> SkippedFolders = new(StringComparer.OrdinalIgnoreCase)
    {
        ".git", ".hg", ".svn", ".venv", "venv", ".conda", "env", "node_modules", "__pycache__", ".mypy_cache", ".pytest_cache", ".ruff_cache", ".tox", ".idea", ".vscode", ".pymagic", "site-packages",
    };

    const int MaxFiles = 20_000;
    const long MaxFileBytes = 50L * 1024 * 1024;
    const string Marker = ".pymagic-sync";

    public override void Prepare(string projectRoot, IReadOnlyList<string>? mounts, Action<string>? progress)
    {
        Project ??= projectRoot;
        if (mounts != null)
            foreach (var m in mounts.Select(Path.GetFullPath))
                if (!_mounts.Contains(m, StringComparer.OrdinalIgnoreCase)) _mounts.Add(m);

        Sync(Local, ProjectDir, SkippedFolders, progress, "the project");
        foreach (var m in _mounts) Sync(m, RemoteMount(m), null, null, "helpers");
    }

    static IEnumerable<(string Rel, long Size, long Ticks)> Files(string root, HashSet<string>? skipped)
    {
        var stack = new Stack<string>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            string dir = stack.Pop();
            IEnumerable<FileSystemInfo> entries;
            try { entries = new DirectoryInfo(dir).EnumerateFileSystemInfos().ToList(); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var e in entries)
            {
                if ((e.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                if (e is DirectoryInfo d) { if (skipped == null || !skipped.Contains(d.Name)) stack.Push(d.FullName); continue; }
                var f = (FileInfo)e;
                if (f.Length > MaxFileBytes || f.Name.EndsWith(".pyc", StringComparison.OrdinalIgnoreCase)) continue;
                yield return (Path.GetRelativePath(root, f.FullName).Replace('\\', '/'), f.Length, f.LastWriteTimeUtc.Ticks);
            }
        }
    }

    /// <summary>Windows' own tar (not whichever tar a PATH entry such as Git's puts first), which writes the GNU format when asked.</summary>
    static ProcessStartInfo LocalTar(params string[] args)
    {
        string system = Path.Combine(Environment.SystemDirectory, "tar.exe");
        bool own = File.Exists(system);
        var psi = new ProcessStartInfo(own ? system : "tar") { UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        return psi;
    }

    string RecordFile(string remoteDir) => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "ssh", Short(Destination + Port + remoteDir) + ".json");

    void Sync(string localRoot, string remoteDir, HashSet<string>? skipped, Action<string>? progress, string what)
    {
        var local = Files(localRoot, skipped).ToList();
        if (local.Count > MaxFiles) throw new ServiceException($"{what} has {local.Count} files: too many to copy to another machine (the limit is {MaxFiles}); keep big data folders out of the project");

        // what was sent last time; a remote folder that lost its marker (it was deleted) starts again
        var record = new Dictionary<string, string>();
        string recordFile = RecordFile(remoteDir);
        try { if (File.Exists(recordFile) && JsonNode.Parse(File.ReadAllText(recordFile)) is JsonObject o) foreach (var (k, v) in o) record[k] = v?.GetValue<string>() ?? ""; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { record.Clear(); }
        if (record.Count > 0 && Remote($"test -f {Quote(remoteDir + "/" + Marker)}", 30_000).Code != 0) record.Clear();

        string Sig((string Rel, long Size, long Ticks) f) => $"{f.Size}|{f.Ticks}";
        var changed = local.Where(f => !record.TryGetValue(f.Rel, out var s) || s != Sig(f)).Select(f => f.Rel).ToList();
        var now = local.ToDictionary(f => f.Rel, Sig);
        var removed = record.Keys.Where(k => !now.ContainsKey(k)).ToList();
        if (changed.Count == 0 && removed.Count == 0 && record.Count > 0) return;

        if (changed.Count > 0 || record.Count == 0)
        {
            progress?.Invoke($"Copying {changed.Count} file{(changed.Count == 1 ? "" : "s")} of {what} to {Destination}…\n");
            Upload(localRoot, remoteDir, changed);
        }
        for (int i = 0; i < removed.Count; i += 40)
        {
            string names = string.Join(" ", removed.Skip(i).Take(40).Select(r => Quote(remoteDir + "/" + r)));
            Remote($"rm -f -- {names}", 60_000);
        }

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(recordFile)!);
            File.WriteAllText(recordFile, new JsonObject(now.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))).ToJsonString());
        }
        catch (IOException) { /* the next copy sends everything again */ }
    }

    /// <summary>
    /// The files as a tar stream (written here, so any name works, long ones included, and no program of this machine is in the way) into tar on
    /// the other machine. Nothing goes through a shell: the folder and the names reach the remote tar as they are.
    /// </summary>
    void Upload(string localRoot, string remoteDir, IReadOnlyList<string> files)
    {
        var ssh = Ssh($"mkdir -p {Quote(remoteDir)} && tar -x -f - -C {Quote(remoteDir)} && touch {Quote(remoteDir + "/" + Marker)}");
        ssh.RedirectStandardInput = true; ssh.RedirectStandardError = true; ssh.RedirectStandardOutput = true;
        try
        {
            using var s = Process.Start(ssh) ?? throw new ServiceException("ssh did not start");
            var sshErr = s.StandardError.ReadToEndAsync();
            var sshOut = s.StandardOutput.ReadToEndAsync();
            try { TarWriter.Write(s.StandardInput.BaseStream, localRoot, files); s.StandardInput.Close(); }
            catch (IOException) { /* the other side stopped reading: its error says why */ }
            if (!s.WaitForExit(600_000)) { try { s.Kill(true); } catch (InvalidOperationException) { } throw new ServiceException("copying the project took too long"); }
            if (s.ExitCode == 255) throw new ServiceException($"cannot connect to {Destination}: {FirstLine(sshErr.Result)}");
            if (s.ExitCode != 0)
            {
                string why = sshErr.Result.Trim();
                throw new ServiceException($"copying to {Destination} failed: {(why.Length > 400 ? why[..400] : why).Replace('\n', ' ')}");
            }
        }
        catch (System.ComponentModel.Win32Exception e) { throw new ServiceException($"cannot copy the project: {e.Message}"); }
    }

    // ------------------------------------------------------------ libraries, for the editor

    string MirrorRoot => Path.Combine(Environment.GetEnvironmentVariable("LOCALAPPDATA") ?? Path.GetTempPath(), "PyMagic", "mirrors", "ssh-" + Short(Destination + Port));

    string MirrorPathOf(string targetPath) => Path.Combine(MirrorRoot, targetPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

    /// <summary>
    /// Copies the Python and stub files of these folders of the machine (not compiled modules, not tests of nothing: just what the editor
    /// can read) into a folder here. A folder is copied again only when its modification time changed, which installing or removing a package does.
    /// </summary>
    public override IReadOnlyList<string> Mirror(IEnumerable<string> targetFolders)
    {
        var failed = new List<string>();
        var folders = targetFolders.Where(f => f.StartsWith('/') && !f.Contains('\'')).Distinct().ToList();
        if (folders.Count == 0) return failed;
        string stampFile = Path.Combine(MirrorRoot, "stamps.json");
        var stamps = new Dictionary<string, string>();
        try { if (File.Exists(stampFile) && JsonNode.Parse(File.ReadAllText(stampFile)) is JsonObject o) foreach (var (k, v) in o) stamps[k] = v?.GetValue<string>() ?? ""; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { }

        var now = new Dictionary<string, string>();
        foreach (var line in Remote("stat -c '%Y %n' -- " + string.Join(" ", folders.Select(Quote)) + " 2>/dev/null", 60_000).Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            int sp = line.IndexOf(' ');
            if (sp > 0) now[line[(sp + 1)..].Trim()] = line[..sp];
        }
        var stale = folders.Where(f => now.TryGetValue(f, out var stamp) && (!stamps.TryGetValue(f, out var old) || old != stamp || !Directory.Exists(MirrorPathOf(f)))).ToList();
        if (stale.Count > 0)
        {
            Directory.CreateDirectory(MirrorRoot);
            string find = "find " + string.Join(" ", stale.Select(Quote)) + @" -type f \( -name '*.py' -o -name '*.pyi' -o -name '*.pth' -o -name 'py.typed' \) -print0 2>/dev/null | tar -c --null -T - -f - 2>/dev/null";
            var ssh = Ssh(find);
            ssh.RedirectStandardOutput = true; ssh.RedirectStandardError = true; ssh.RedirectStandardInput = true;
            var untar = LocalTar("-x", "-f", "-", "-C", MirrorRoot);
            untar.RedirectStandardInput = true; untar.RedirectStandardError = true;
            using var s = Process.Start(ssh)!;
            using var u = Process.Start(untar)!;
            s.StandardInput.Close();
            var sErr = s.StandardError.ReadToEndAsync();
            var uErr = u.StandardError.ReadToEndAsync();
            try { s.StandardOutput.BaseStream.CopyTo(u.StandardInput.BaseStream); u.StandardInput.Close(); } catch (IOException) { }
            s.WaitForExit(900_000);
            u.WaitForExit(900_000);
            foreach (var f in stale)
            {
                if (Directory.Exists(MirrorPathOf(f))) stamps[f] = now[f];
                else failed.Add(f);
            }
            try { File.WriteAllText(stampFile, new JsonObject(stamps.Select(kv => KeyValuePair.Create(kv.Key, (JsonNode?)JsonValue.Create(kv.Value)))).ToJsonString()); } catch (IOException) { }
        }
        return failed;
    }
}
