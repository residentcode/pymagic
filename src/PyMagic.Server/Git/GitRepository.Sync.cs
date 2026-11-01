using System.Text.RegularExpressions;

namespace PyMagic.Server.Git;

public sealed record GitRemote(string Name, string FetchUrl, string PushUrl);

public sealed record GitStash(int Index, string Message, string Branch, string Age);

public sealed record GitTag(string Name, string Hash, string Subject);

/// <summary>What a merge, pull, cherry-pick, revert or stash apply did: its output, whether it stopped on conflicts, and the operation still waiting for you.</summary>
public sealed record GitResult(string Output, bool Conflicts, string? Operation);

public sealed record GitConflictStages(string? Base, string? Ours, string? Theirs)
{
    /// <summary>The file is binary: the texts above are empty and the only way to settle it is to take a side.</summary>
    public bool Binary { get; init; }
    public bool HasBase { get; init; }
    public bool HasOurs { get; init; }
    public bool HasTheirs { get; init; }
}

/// <summary>Remotes, branch management, merges, conflicts, stashes, tags and commit actions.</summary>
public sealed partial class GitRepository
{
    /// <summary>Fetching and pushing talk to a server: more patience than the 60 seconds of local commands.</summary>
    const int NetworkTimeoutMs = 300_000;

    (int Code, string Out, string Err) RunNetwork(params string[] args) => Execute(_git, _dir, args, null, NetworkTimeoutMs);

    static string Combined((int Code, string Out, string Err) r) => (r.Out.Trim() + "\n" + r.Err.Trim()).Trim();

    // ------------------------------------------------------------ names callers may pass

    static readonly Regex RemoteNamePattern = new(@"^[A-Za-z0-9_][A-Za-z0-9._\-]*$", RegexOptions.Compiled);

    public static string CheckRemoteName(string name)
    {
        if (!RemoteNamePattern.IsMatch(name) || name.Contains("..") || name.EndsWith(".lock")) throw new GitException($"'{name}' is not a valid remote name");
        return name;
    }

    /// <summary>
    /// A remote's address: https, http, ssh, git and file URLs, `user@host:path`, or a folder. Never an option, and never one of git's
    /// "transport helpers" (`ext::`, `fd::`), which run a program.
    /// </summary>
    public static string CheckRemoteUrl(string url)
    {
        string u = url.Trim();
        if (u.Length == 0 || u.StartsWith('-') || u.Contains("::") || u.Any(c => char.IsControl(c) || c == ' ')) throw new GitException($"'{url}' is not an address a remote can have");
        bool known = Regex.IsMatch(u, @"^((https?|ssh|git)://[^/]|file://)", RegexOptions.IgnoreCase)
            || Regex.IsMatch(u, @"^[A-Za-z0-9._\-]+@[A-Za-z0-9._\-]+:[^:]")
            || Regex.IsMatch(u, @"^[A-Za-z]:[\\/]") || u.StartsWith('/') || u.StartsWith(@"\\");
        if (!known) throw new GitException($"'{url}' is not an address a remote can have");
        return u;
    }

    static readonly Regex RemoteBranchPattern = new(@"^[A-Za-z0-9_][A-Za-z0-9._\-]*/[A-Za-z0-9_][A-Za-z0-9._/\-]*$", RegexOptions.Compiled);

    // ------------------------------------------------------------ remotes

    public List<GitRemote> Remotes()
    {
        var map = new Dictionary<string, (string Fetch, string Push)>();
        foreach (var line in RunOrThrow("remote", "-v").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var m = Regex.Match(line.TrimEnd('\r'), @"^(\S+)\t(.+) \((fetch|push)\)$");
            if (!m.Success) continue;
            var cur = map.GetValueOrDefault(m.Groups[1].Value);
            map[m.Groups[1].Value] = m.Groups[3].Value == "fetch" ? (m.Groups[2].Value, cur.Push ?? m.Groups[2].Value) : (cur.Fetch ?? m.Groups[2].Value, m.Groups[2].Value);
        }
        return map.Select(kv => new GitRemote(kv.Key, kv.Value.Fetch, kv.Value.Push)).OrderBy(r => r.Name, StringComparer.Ordinal).ToList();
    }

    public void AddRemote(string name, string url) => RunOrThrow("remote", "add", CheckRemoteName(name), CheckRemoteUrl(url));

    public void RemoveRemote(string name) => RunOrThrow("remote", "remove", CheckRemoteName(name));

    /// <summary>Branches of the remotes as they were last fetched ("origin/main"); the pointer "origin/HEAD" is left out.</summary>
    public List<string> RemoteBranches()
    {
        var r = Run("branch", "-r", "--format=%(refname:short)");
        if (r.Code != 0) return new List<string>();
        return r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())
            .Where(l => l.Contains('/') && !l.EndsWith("/HEAD") && l != "origin").ToList();
    }

    /// <summary>Gets what is new on the remotes (one, or all) without changing any branch of yours.</summary>
    public string Fetch(string? remote, bool prune)
    {
        var args = new List<string> { "fetch" };
        if (prune) args.Add("--prune");
        if (string.IsNullOrEmpty(remote)) args.Add("--all"); else args.Add(CheckRemoteName(remote));
        var r = RunNetwork(args.ToArray());
        if (r.Code != 0) throw new GitException(Message(r));
        return Combined(r);
    }

    /// <summary>Fetches and joins the upstream into the current branch. <paramref name="mode"/>: merge, rebase or ff-only.</summary>
    public GitResult Pull(string mode)
    {
        string flag = mode switch { "rebase" => "--rebase", "ff-only" => "--ff-only", "merge" or "" => "--no-rebase", _ => throw new GitException($"'{mode}' is not a way to pull") };
        var r = RunNetwork("pull", "--no-edit", flag);
        return Outcome(r);
    }

    /// <summary>Sends the current branch to its upstream; a branch without one is published to the first remote (origin by default) and tracks it from then on.</summary>
    public string Push(bool forceWithLease)
    {
        var status = Status();
        if (status.Detached || status.Branch == null) throw new GitException("there is no branch to push (HEAD is detached)");
        var args = new List<string> { "push" };
        if (forceWithLease) args.Add("--force-with-lease");
        if (status.Upstream == null)
        {
            var remotes = Remotes();
            if (remotes.Count == 0) throw new GitException("there is no remote to push to: add one first");
            string remote = (remotes.FirstOrDefault(x => x.Name == "origin") ?? remotes[0]).Name;
            args.Add("-u"); args.Add(remote); args.Add(CheckBranch(status.Branch));
        }
        var r = RunNetwork(args.ToArray());
        if (r.Code != 0) throw new GitException(Message(r));
        return Combined(r);
    }

    // ------------------------------------------------------------ branches

    /// <summary>Removes a local branch; the current one cannot be. Without <paramref name="force"/> git refuses a branch whose work is not merged.</summary>
    public void DeleteBranch(string name, bool force)
    {
        CheckBranch(name);
        if (Branches().Current == name) throw new GitException("the branch you are on cannot be deleted: switch to another one first");
        RunOrThrow("branch", force ? "-D" : "-d", name);
    }

    public void RenameBranch(string name, string newName) => RunOrThrow("branch", "-m", CheckBranch(name), CheckBranch(newName));

    /// <summary>Makes a local branch that follows a remote one (origin/feature) and switches to it.</summary>
    public void CheckoutRemoteBranch(string remoteBranch)
    {
        if (!RemoteBranchPattern.IsMatch(remoteBranch) || remoteBranch.Contains("..") || !RemoteBranches().Contains(remoteBranch))
            throw new GitException($"'{remoteBranch}' is not a branch of a remote (fetch first?)");
        RunOrThrow("switch", "--track", remoteBranch);
    }

    /// <summary>Joins another branch into the current one (a local one, or "origin/x").</summary>
    public GitResult Merge(string branch, bool noFastForward)
    {
        if (!Branches().Local.Contains(branch) && !RemoteBranches().Contains(branch)) throw new GitException($"'{branch}' is not a branch");
        var args = new List<string> { "merge", "--no-edit" };
        if (noFastForward) args.Add("--no-ff");
        args.Add(branch);
        return Outcome(Run(args.ToArray()));
    }

    // ------------------------------------------------------------ an operation that stopped (merge, rebase, cherry-pick, revert)

    /// <summary>"merge", "rebase", "cherry-pick" or "revert" while one is waiting for you (conflicts to resolve), otherwise null.</summary>
    public string? Operation()
    {
        string gitDir = RunOrThrow("rev-parse", "--absolute-git-dir").Trim();
        if (File.Exists(Path.Combine(gitDir, "MERGE_HEAD"))) return "merge";
        if (Directory.Exists(Path.Combine(gitDir, "rebase-merge")) || Directory.Exists(Path.Combine(gitDir, "rebase-apply"))) return "rebase";
        if (File.Exists(Path.Combine(gitDir, "CHERRY_PICK_HEAD"))) return "cherry-pick";
        if (File.Exists(Path.Combine(gitDir, "REVERT_HEAD"))) return "revert";
        return null;
    }

    /// <summary>Gives the operation up: the branch and the files go back to how they were before it started.</summary>
    public void Abort()
    {
        string op = Operation() ?? throw new GitException("nothing is in progress");
        RunOrThrow(op == "merge" ? "merge" : op, "--abort");
    }

    /// <summary>Goes on after the conflicts are resolved: a merge is committed, the others carry on with their next step.</summary>
    public GitResult Continue()
    {
        string op = Operation() ?? throw new GitException("nothing is in progress");
        if (Status().Files.Any(f => f.IsConflict)) throw new GitException("some files still have conflicts");
        return Outcome(op == "merge" ? Run("commit", "--no-edit") : Run(op, "--continue"));
    }

    /// <summary>Turns the result of a command into an answer: conflicts are a state to resolve, not a failure; anything else that went wrong is thrown.</summary>
    GitResult Outcome((int Code, string Out, string Err) r)
    {
        if (r.Code == 0) return new GitResult(Combined(r), false, Operation());
        string? op = Operation();
        if (op != null && Status().Files.Any(f => f.IsConflict)) return new GitResult(Combined(r), true, op);
        throw new GitException(Message(r));
    }

    // ------------------------------------------------------------ conflicts

    public List<string> Conflicts() => Status().Files.Where(f => f.IsConflict).Select(f => f.Path).ToList();

    /// <summary>Takes one side of a conflicted file as it is ("ours": the branch you are on; "theirs": the one coming in; the other way round during a rebase) and marks it resolved.</summary>
    public void Resolve(string path, string side)
    {
        string p = CheckPath(path);
        if (side is not ("ours" or "theirs")) throw new GitException("choose ours or theirs");
        if (!Conflicts().Contains(p)) throw new GitException($"'{p}' has no conflict");
        var take = Run("checkout", "--" + side, "--", p);
        if (take.Code != 0)
        {
            // the side deleted the file: resolving to it means the file goes
            var rm = Run("rm", "-f", "--", p);
            if (rm.Code != 0) throw new GitException(Message(take));
            return;
        }
        RunOrThrow("add", "--", p);
    }

    /// <summary>The file as it stands (conflict markers and all) is what you want: stage it.</summary>
    public void MarkResolved(string path)
    {
        string p = CheckPath(path);
        if (!Conflicts().Contains(p)) throw new GitException($"'{p}' has no conflict");
        RunOrThrow("add", "--", p);
    }

    /// <summary>The three versions of a conflicted file: the common ancestor, ours and theirs (null where a side has no such file).</summary>
    public GitConflictStages ConflictStages(string path)
    {
        string p = CheckPath(path);
        string? Stage(int n)
        {
            var r = Run("show", $":{n}:{_prefix}{p}");
            return r.Code == 0 ? r.Out : null;
        }
        string? b1 = Stage(1), o = Stage(2), t = Stage(3);
        // a file with a NUL in it is binary: its bytes are no text to compare or to merge, only a side to take
        bool binary = (b1?.Contains('\0') ?? false) || (o?.Contains('\0') ?? false) || (t?.Contains('\0') ?? false);
        if (binary) return new GitConflictStages(null, null, null) { Binary = true, HasBase = b1 != null, HasOurs = o != null, HasTheirs = t != null };
        return new GitConflictStages(b1, o, t) { HasBase = b1 != null, HasOurs = o != null, HasTheirs = t != null };
    }

    // ------------------------------------------------------------ stash

    public List<GitStash> Stashes()
    {
        var list = new List<GitStash>();
        var r = Run("stash", "list", "--format=%gd%x09%gs%x09%cr");
        if (r.Code != 0) return list;
        foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length < 3) continue;
            var idx = Regex.Match(parts[0], @"\{(\d+)\}");
            if (!idx.Success) continue;
            // "WIP on main: abc1234 subject" or "On main: your message"
            var m = Regex.Match(parts[1], @"^(?:WIP on|On) ([^:]+): (.*)$");
            list.Add(new GitStash(int.Parse(idx.Groups[1].Value), m.Success ? m.Groups[2].Value : parts[1], m.Success ? m.Groups[1].Value : "", parts[2]));
        }
        return list;
    }

    /// <summary>Puts the changes of the working tree away (and leaves it clean); returns git's words, "No local changes to save" included.</summary>
    public string Stash(string? message, bool includeUntracked)
    {
        var args = new List<string> { "stash", "push" };
        if (includeUntracked) args.Add("-u");
        if (!string.IsNullOrWhiteSpace(message)) { args.Add("-m"); args.Add(message); }
        return Combined(RunOr(args.ToArray()));
    }

    (int Code, string Out, string Err) RunOr(params string[] args)
    {
        var r = Run(args);
        if (r.Code != 0) throw new GitException(Message(r));
        return r;
    }

    static string StashRef(int index) => index is >= 0 and < 1000 ? $"stash@{{{index}}}" : throw new GitException("there is no such stash");

    /// <summary>Brings a stash back; <paramref name="drop"/> removes it when that worked (a pop).</summary>
    public GitResult ApplyStash(int index, bool drop) => Outcome(Run("stash", drop ? "pop" : "apply", StashRef(index)));

    public void DropStash(int index) => RunOrThrow("stash", "drop", StashRef(index));

    /// <summary>The files a stash holds, as status letter and path.</summary>
    public List<GitChangedFile> StashFiles(int index)
    {
        var files = new List<GitChangedFile>();
        // new files that were stashed with -u are listed only when asked for (git 2.32 and later)
        var shown = Run("stash", "show", "--include-untracked", "--name-status", StashRef(index));
        if (shown.Code != 0) shown = Run("stash", "show", "--name-status", StashRef(index));
        if (shown.Code != 0) throw new GitException(Message(shown));
        foreach (var line in shown.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 2) files.Add(new GitChangedFile(parts[0][..1], parts[^1], parts.Length > 2 ? parts[1] : null));
        }
        return files;
    }

    // ------------------------------------------------------------ actions on a commit

    /// <summary>Makes a new commit that undoes one.</summary>
    public GitResult Revert(string hash) => Outcome(Run("revert", "--no-edit", CheckRevision(hash)));

    /// <summary>Applies the change of one commit on top of the current branch.</summary>
    public GitResult CherryPick(string hash) => Outcome(Run("cherry-pick", CheckRevision(hash)));

    /// <summary>Moves the current branch to a commit. soft keeps the changes staged, mixed keeps them as changes, hard throws them away (there is no undo for those).</summary>
    public void Reset(string hash, string mode)
    {
        if (mode is not ("soft" or "mixed" or "hard")) throw new GitException("the mode is soft, mixed or hard");
        RunOrThrow("reset", "--" + mode, CheckRevision(hash));
    }

    // ------------------------------------------------------------ tags

    public List<GitTag> Tags()
    {
        var list = new List<GitTag>();
        var r = Run("tag", "--sort=-creatordate", "--format=%(refname:short)%09%(objectname:short)%09%(contents:subject)");
        if (r.Code != 0) return list;
        foreach (var line in r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = line.TrimEnd('\r').Split('\t');
            if (parts.Length >= 2) list.Add(new GitTag(parts[0], parts[1], parts.Length > 2 ? parts[2] : ""));
        }
        return list;
    }

    /// <summary>A tag on a commit (HEAD by default): annotated when a message is given.</summary>
    public void CreateTag(string name, string? hash, string? message)
    {
        CheckBranch(name);
        var args = new List<string> { "tag" };
        if (!string.IsNullOrWhiteSpace(message)) { args.Add("-a"); args.Add("-m"); args.Add(message); }
        args.Add(name);
        args.Add(string.IsNullOrEmpty(hash) ? "HEAD" : CheckRevision(hash));
        RunOrThrow(args.ToArray());
    }

    public void DeleteTag(string name) => RunOrThrow("tag", "-d", CheckBranch(name));
}
