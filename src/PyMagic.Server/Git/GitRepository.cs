using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace PyMagic.Server.Git;

/// <param name="Index">Porcelain status of the index ('M', 'A', 'D', 'R', ' ' for none); '?' for an untracked file.</param>
/// <param name="Worktree">Porcelain status of the working tree.</param>
public sealed record GitFileStatus(string Path, char Index, char Worktree, string? OriginalPath)
{
    public bool IsUntracked => Index == '?';
    public bool IsConflict => Index == 'U' || Worktree == 'U' || Index == 'A' && Worktree == 'A' || Index == 'D' && Worktree == 'D';
}

public sealed record GitStatus(string? Branch, string? Upstream, int Ahead, int Behind, bool Detached, bool HasCommits, List<GitFileStatus> Files);

public sealed record GitCommit(string Hash, string ShortHash, string Author, string Email, string Date, string Subject, List<string> Parents, string Refs);

public sealed record GitChangedFile(string Status, string Path, string? OldPath);

public sealed record GitCommitDetail(GitCommit Commit, string Body, List<GitChangedFile> Files);

public sealed record GitBlameLine(string Hash, string Author, long Time, string Summary);

public sealed record GitBranches(string? Current, bool Detached, List<string> Local)
{
    /// <summary>Branches of the remotes as last fetched, "origin/main".</summary>
    public List<string> Remote { get; init; } = new();
}

/// <summary>Git said no, or git is not there; the message is meant for the user.</summary>
public sealed class GitException(string message) : Exception(message);

/// <summary>
/// The Git of a workspace folder, by way of the git program: status, file contents at a revision, history, blame, commit, rollback, branches.
/// Arguments always go as a list (never through a shell), and what a caller supplies (paths, revisions, branch names) is checked before it can
/// reach a command line.
/// </summary>
public sealed partial class GitRepository
{
    readonly string _dir;
    readonly string _git;
    /// <summary>The workspace folder relative to the repository root, with a trailing slash ("" when they are the same).</summary>
    readonly string _prefix;

    public string WorkingDirectory => _dir;

    GitRepository(string dir, string git, string prefix) { _dir = dir; _git = git; _prefix = prefix; }

    /// <summary>The git program: PYMAGIC_GIT, else the one on the PATH.</summary>
    static string GitProgram => System.Environment.GetEnvironmentVariable("PYMAGIC_GIT") is { Length: > 0 } g ? g : "git";

    public static bool IsInstalled()
    {
        try { return Execute(GitProgram, Directory.GetCurrentDirectory(), new[] { "--version" }, null, 10_000).Code == 0; }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException) { return false; }
    }

    /// <summary>The repository the folder belongs to; null (with the reason) when git is missing or the folder is not in one.</summary>
    public static GitRepository? Locate(string dir, out string? reason)
    {
        reason = null;
        string git = GitProgram;
        try
        {
            var r = Execute(git, dir, new[] { "rev-parse", "--show-prefix" }, null, 15_000);
            if (r.Code != 0)
            {
                reason = r.Err.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ? "not a git repository" : r.Err.Trim();
                return null;
            }
            return new GitRepository(Path.GetFullPath(dir), git, r.Out.Trim().Replace('\\', '/'));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or DirectoryNotFoundException)
        {
            reason = "git is not installed (or not on the PATH)";
            return null;
        }
    }

    /// <summary>Creates a repository in the folder.</summary>
    public static GitRepository Init(string dir)
    {
        var r = Execute(GitProgram, dir, new[] { "init" }, null, 20_000);
        if (r.Code != 0) throw new GitException(Message(r));
        return Locate(dir, out var reason) ?? throw new GitException(reason ?? "git init failed");
    }

    // ------------------------------------------------------------ running git

    static string Message((int Code, string Out, string Err) r) =>
        (r.Err.Trim().Length > 0 ? r.Err.Trim() : r.Out.Trim()) is { Length: > 0 } text ? text : $"git failed (exit code {r.Code})";

    static (int Code, string Out, string Err) Execute(string git, string dir, IEnumerable<string> args, string? stdin, int timeoutMs)
    {
        var psi = new ProcessStartInfo(git)
        {
            WorkingDirectory = dir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };
        foreach (var a in new[] { "-c", "core.quotepath=false", "-c", "color.ui=false", "-c", "core.pager=cat" }) psi.ArgumentList.Add(a);
        foreach (var a in args) psi.ArgumentList.Add(a);
        psi.Environment["GIT_TERMINAL_PROMPT"] = "0";     // never wait for a password nobody can type
        psi.Environment["GIT_OPTIONAL_LOCKS"] = "0";      // reading the status must not fight the user's own git for the index lock
        psi.Environment["GIT_EDITOR"] = "true";
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("git did not start");
        var stdout = p.StandardOutput.ReadToEndAsync();
        var stderr = p.StandardError.ReadToEndAsync();
        try
        {
            if (stdin != null) p.StandardInput.Write(stdin);
            p.StandardInput.Close();
        }
        catch (IOException) { /* git exited before reading it */ }
        if (!p.WaitForExit(timeoutMs))
        {
            try { p.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw new GitException("git took too long and was stopped");
        }
        p.WaitForExit();
        return (p.ExitCode, stdout.Result, stderr.Result);
    }

    (int Code, string Out, string Err) Run(params string[] args) => Execute(_git, _dir, args, null, 60_000);

    string RunOrThrow(params string[] args)
    {
        var r = Run(args);
        if (r.Code != 0) throw new GitException(Message(r));
        return r.Out;
    }

    // ------------------------------------------------------------ what callers may pass

    /// <summary>A path inside the workspace folder, relative to it, written with forward slashes; anything else is refused.</summary>
    public static string CheckPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) throw new GitException("a path is needed");
        string p = path.Replace('\\', '/');
        if (p.StartsWith('-') || p.StartsWith('/') || p.StartsWith(':') || Regex.IsMatch(p, @"^[A-Za-z]:") || p.Contains('\0'))
            throw new GitException($"'{path}' is not a path inside the project");
        if (p.Split('/').Any(part => part == ".." || part == ".git")) throw new GitException($"'{path}' is not a path inside the project");
        return p;
    }

    static readonly Regex RevisionPattern = new(@"^(?:[0-9a-fA-F]{4,40}|HEAD)(?:\^|~\d{1,3})?$", RegexOptions.Compiled);

    /// <summary>A commit hash or HEAD, optionally with ^ or ~n: all the client ever needs to ask for.</summary>
    public static string CheckRevision(string rev)
    {
        if (!RevisionPattern.IsMatch(rev)) throw new GitException($"'{rev}' is not a revision");
        return rev;
    }

    static readonly Regex BranchPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9._/\-]*$", RegexOptions.Compiled);

    public static string CheckBranch(string name)
    {
        if (!BranchPattern.IsMatch(name) || name.Contains("..") || name.EndsWith('/') || name.EndsWith(".lock") || name.Contains("//") || name.Contains("@{"))
            throw new GitException($"'{name}' is not a valid branch name");
        return name;
    }

    // ------------------------------------------------------------ status

    public GitStatus Status()
    {
        var r = Run("status", "--porcelain=v1", "-b", "-z", "--untracked-files=all");
        if (r.Code != 0) throw new GitException(Message(r));
        var parts = r.Out.Split('\0');
        string? branch = null, upstream = null;
        int ahead = 0, behind = 0;
        bool detached = false, hasCommits = true;
        var files = new List<GitFileStatus>();

        for (int i = 0; i < parts.Length; i++)
        {
            string entry = parts[i];
            if (entry.Length < 3) continue;
            if (entry.StartsWith("## "))
            {
                string header = entry[3..];
                var m = Regex.Match(header, @"^(?<b>.+?)(?:\.\.\.(?<u>\S+))?(?: \[(?<s>[^\]]*)\])?$");
                if (header.StartsWith("No commits yet on "))
                {
                    hasCommits = false;
                    branch = header["No commits yet on ".Length..];
                }
                else if (header.StartsWith("HEAD (no branch)")) detached = true;
                else if (m.Success)
                {
                    branch = m.Groups["b"].Value;
                    if (m.Groups["u"].Success) upstream = m.Groups["u"].Value;
                    var a = Regex.Match(m.Groups["s"].Value, @"ahead (\d+)");
                    var b = Regex.Match(m.Groups["s"].Value, @"behind (\d+)");
                    if (a.Success) ahead = int.Parse(a.Groups[1].Value);
                    if (b.Success) behind = int.Parse(b.Groups[1].Value);
                }
                continue;
            }
            char x = entry[0], y = entry[1];
            string path = entry[3..];
            string? original = null;
            if (x is 'R' or 'C' && i + 1 < parts.Length) original = parts[++i];   // -z puts the old name in the next field
            string? relative = RelativeToWorkspace(path);
            if (relative == null) continue;
            files.Add(new GitFileStatus(relative, x, y, original == null ? null : RelativeToWorkspace(original) ?? original));
            if (files.Count >= 5000) break;
        }
        return new GitStatus(branch, upstream, ahead, behind, detached, hasCommits, files);
    }

    /// <summary>A repository-root path as a workspace path, or null for a file outside the workspace folder.</summary>
    string? RelativeToWorkspace(string repoPath)
    {
        if (_prefix.Length == 0) return repoPath;
        return repoPath.StartsWith(_prefix, StringComparison.Ordinal) ? repoPath[_prefix.Length..] : null;
    }

    // ------------------------------------------------------------ contents

    /// <summary>The file as committed at a revision, or null when it does not exist there (a new file, or no commit yet).</summary>
    public string? FileAt(string rev, string path)
    {
        string spec = CheckRevision(rev) + ":./" + CheckPath(path);
        var r = Run("show", spec);
        return r.Code == 0 ? r.Out : null;
    }

    // ------------------------------------------------------------ history

    const string CommitFormat = "%H%x1f%h%x1f%an%x1f%ae%x1f%aI%x1f%P%x1f%D%x1f%s";

    static GitCommit ParseCommit(string record)
    {
        var f = record.Split('\x1f');
        return new GitCommit(f[0], f[1], f[2], f[3], f[4], f.Length > 7 ? f[7] : "", f[5].Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList(), f[6]);
    }

    /// <summary>Commits, newest first; of one file when a path is given (following renames).</summary>
    public List<GitCommit> Log(string? path, int limit, int skip)
    {
        limit = Math.Clamp(limit, 1, 500);
        skip = Math.Max(0, skip);
        var args = new List<string> { "log", $"--max-count={limit}", $"--skip={skip}", "--date=iso-strict", "--format=" + CommitFormat + "%x1e" };
        if (path != null) { args.Add("--follow"); args.Add("--"); args.Add(CheckPath(path)); }
        var r = Run(args.ToArray());
        if (r.Code != 0)
        {
            if (r.Err.Contains("does not have any commits yet") || r.Err.Contains("unknown revision")) return new List<GitCommit>();
            throw new GitException(Message(r));
        }
        return r.Out.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('\n', '\r')).Where(x => x.Length > 0).Select(ParseCommit).ToList();
    }

    /// <summary>One commit with its message and the files it changed (against its first parent).</summary>
    public GitCommitDetail Show(string hash)
    {
        CheckRevision(hash);
        string head = RunOrThrow("show", "-s", "--date=iso-strict", "--format=" + CommitFormat + "%x1f%b", hash);
        var fields = head.TrimEnd('\n', '\r').Split('\x1f');
        var commit = ParseCommit(string.Join('\x1f', fields.Take(8)));
        string body = fields.Length > 8 ? string.Join('\x1f', fields.Skip(8)).Trim() : "";

        string names = RunOrThrow("show", "--name-status", "--format=", "--first-parent", "-M", "-z", hash);
        var files = new List<GitChangedFile>();
        var parts = names.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string status = parts[i].Trim('\n', '\r');
            if (status.Length == 0) continue;
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 < parts.Length) files.Add(new GitChangedFile(status[..1], parts[i + 2], parts[i + 1]));
                i += 2;
            }
            else if (i + 1 < parts.Length)
            {
                files.Add(new GitChangedFile(status[..1], parts[i + 1], null));
                i += 1;
            }
        }
        return new GitCommitDetail(commit, body, files);
    }

    // ------------------------------------------------------------ blame

    /// <summary>Who last changed each line of the working copy of a file, and in which commit.</summary>
    public List<GitBlameLine> Blame(string path)
    {
        var r = Run("blame", "--porcelain", "--", CheckPath(path));
        if (r.Code != 0) throw new GitException(Message(r));
        var info = new Dictionary<string, (string Author, long Time, string Summary)>();
        var result = new List<GitBlameLine>();
        string? current = null;
        string author = "", summary = "";
        long time = 0;
        foreach (var line in r.Out.Split('\n'))
        {
            if (line.StartsWith('\t'))
            {
                if (current != null) result.Add(new GitBlameLine(current, info[current].Author, info[current].Time, info[current].Summary));
                continue;
            }
            var header = Regex.Match(line, @"^([0-9a-f]{40}) \d+ \d+(?: \d+)?$");
            if (header.Success)
            {
                current = header.Groups[1].Value;
                if (info.ContainsKey(current)) continue;
                author = ""; summary = ""; time = 0;
                continue;
            }
            if (current == null) continue;
            if (line.StartsWith("author ")) author = line[7..];
            else if (line.StartsWith("author-time ")) long.TryParse(line[12..], out time);
            else if (line.StartsWith("summary "))
            {
                summary = line[8..];
                info[current] = (author, time, summary);
            }
        }
        return result;
    }

    // ------------------------------------------------------------ changing things

    /// <summary>Commits exactly these paths (adding new and removed ones first); other staged changes stay out. Returns the new commit's hash.</summary>
    public string Commit(IReadOnlyList<string> paths, string message, bool amend)
    {
        if (string.IsNullOrWhiteSpace(message) && !amend) throw new GitException("a commit message is needed");
        var checkedPaths = paths.Select(CheckPath).Distinct().ToList();
        if (checkedPaths.Count == 0 && !amend) throw new GitException("nothing is selected to commit");

        if (checkedPaths.Count > 0)
        {
            var add = new List<string> { "add", "-A", "--" };
            add.AddRange(checkedPaths);
            RunOrThrow(add.ToArray());
        }
        var commit = new List<string> { "commit", "--only" };
        if (amend) commit.Add("--amend");
        if (!string.IsNullOrWhiteSpace(message)) { commit.Add("-m"); commit.Add(message); }
        else commit.Add("--no-edit");
        if (checkedPaths.Count > 0) { commit.Add("--"); commit.AddRange(checkedPaths); }
        RunOrThrow(commit.ToArray());
        return RunOrThrow("rev-parse", "HEAD").Trim();
    }

    /// <summary>Throws the changes of these files away: back to the last commit, and new files deleted. There is no undo.</summary>
    public void Discard(IReadOnlyList<string> paths)
    {
        var status = Status().Files.ToDictionary(f => f.Path);
        foreach (var raw in paths)
        {
            string path = CheckPath(raw);
            if (!status.TryGetValue(path, out var file)) continue;
            if (file.IsUntracked) RunOrThrow("clean", "-f", "--", path);
            else if (file.Index == 'A') RunOrThrow("rm", "-f", "--", path);
            else
            {
                var restore = new List<string> { "restore", "--source=HEAD", "--staged", "--worktree", "--" };
                if (file.OriginalPath != null) restore.Add(CheckPath(file.OriginalPath));
                restore.Add(path);
                RunOrThrow(restore.ToArray());
            }
        }
    }

    // ------------------------------------------------------------ branches

    public GitBranches Branches()
    {
        string current = "";
        var local = new List<string>();
        var r = Run("branch", "--format=%(HEAD)%09%(refname:short)");
        if (r.Code == 0)
            foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var parts = line.TrimEnd('\r').Split('\t');
                if (parts.Length < 2 || parts[1].StartsWith('(')) continue;   // "(HEAD detached at ...)"
                local.Add(parts[1]);
                if (parts[0] == "*") current = parts[1];
            }
        var status = Status();
        if (status.Branch != null && current.Length == 0) current = status.Branch;   // a branch without commits is not listed yet
        if (current.Length > 0 && !local.Contains(current)) local.Insert(0, current);
        return new GitBranches(current.Length > 0 ? current : null, status.Detached, local) { Remote = RemoteBranches() };
    }

    public void Checkout(string branch) => RunOrThrow("switch", CheckBranch(branch));

    public void CreateBranch(string name) => RunOrThrow("switch", "-c", CheckBranch(name));
}
