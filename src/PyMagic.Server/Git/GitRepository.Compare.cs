namespace PyMagic.Server.Git;

/// <summary>Two branches side by side: where they split, the commits only each has, and the files that differ between their tips (hashes, so a file can be read at each).</summary>
public sealed record GitBranchComparison(string A, string B, string HashA, string HashB, string? MergeBase, List<GitCommit> OnlyA, List<GitCommit> OnlyB, List<GitChangedFile> Files);

/// <summary>Tags sent to a remote, rebasing onto another branch, and comparing branches.</summary>
public sealed partial class GitRepository
{
    string DefaultRemote()
    {
        var remotes = Remotes();
        if (remotes.Count == 0) throw new GitException("there is no remote to push to: add one first");
        return (remotes.FirstOrDefault(x => x.Name == "origin") ?? remotes[0]).Name;
    }

    /// <summary>Sends one tag to a remote (origin, or the first remote, when none is named). Nothing is forced: a remote that has the name already refuses.</summary>
    public string PushTag(string name, string? remote)
    {
        CheckBranch(name);
        if (Run("rev-parse", "--verify", "--quiet", "refs/tags/" + name).Code != 0) throw new GitException($"'{name}' is not a tag");
        string rm = string.IsNullOrEmpty(remote) ? DefaultRemote() : CheckRemoteName(remote);
        var r = RunNetwork("push", rm, $"refs/tags/{name}:refs/tags/{name}");
        if (r.Code != 0) throw new GitException(Message(r));
        return Combined(r);
    }

    /// <summary>Sends every tag the remote does not have yet.</summary>
    public string PushAllTags(string? remote)
    {
        string rm = string.IsNullOrEmpty(remote) ? DefaultRemote() : CheckRemoteName(remote);
        var r = RunNetwork("push", rm, "--tags");
        if (r.Code != 0) throw new GitException(Message(r));
        return Combined(r);
    }

    /// <summary>Takes a tag off a remote (the local tag stays).</summary>
    public string DeleteRemoteTag(string name, string? remote)
    {
        CheckBranch(name);
        string rm = string.IsNullOrEmpty(remote) ? DefaultRemote() : CheckRemoteName(remote);
        var r = RunNetwork("push", rm, "--delete", $"refs/tags/{name}");
        if (r.Code != 0) throw new GitException(Message(r));
        return Combined(r);
    }

    /// <summary>The names of the tags a remote has (read from the server).</summary>
    public List<string> RemoteTags(string? remote)
    {
        string rm = string.IsNullOrEmpty(remote) ? DefaultRemote() : CheckRemoteName(remote);
        var r = RunNetwork("ls-remote", "--tags", "--refs", rm);
        if (r.Code != 0) throw new GitException(Message(r));
        return r.Out.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim().Split('\t')).Where(p => p.Length == 2 && p[1].StartsWith("refs/tags/")).Select(p => p[1]["refs/tags/".Length..]).ToList();
    }

    string ResolveBranch(string branch)
    {
        if (Branches().Local.Contains(branch)) return RunOrThrow("rev-parse", "--verify", "refs/heads/" + branch + "^{commit}").Trim();
        if (RemoteBranches().Contains(branch)) return RunOrThrow("rev-parse", "--verify", "refs/remotes/" + branch + "^{commit}").Trim();
        throw new GitException($"'{branch}' is not a branch");
    }

    /// <summary>
    /// Replays the commits of the current branch on top of another branch (<c>git rebase &lt;branch&gt;</c>). A conflict is a state to resolve, as in any rebase.
    /// <paramref name="keepMerges"/> keeps the merge commits of the branch (<c>--rebase-merges</c>) instead of flattening them.
    /// </summary>
    public GitResult RebaseOnto(string branch, bool keepMerges)
    {
        string target = ResolveBranch(branch);
        if (Operation() != null) throw new GitException("finish or abort the operation that is in progress first");
        if (RunOrThrow("rev-parse", "--abbrev-ref", "HEAD").Trim() == "HEAD") throw new GitException("a branch has to be checked out (HEAD is detached)");
        var args = new List<string> { "rebase" };
        if (keepMerges) args.Add("--rebase-merges");
        args.Add(target);
        return Outcome(Run(args.ToArray()));
    }

    List<GitCommit> CommitsIn(string range)
    {
        var r = RunOrThrow("log", "--max-count=300", "--date=iso-strict", "--format=" + CommitFormat + "%x1e", range);
        return r.Split('\x1e', StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim('\n', '\r')).Where(x => x.Length > 0).Select(ParseCommit).ToList();
    }

    /// <summary>What differs between two branches (local ones, or "origin/x"): the commits only one has, and the files that differ between the two tips.</summary>
    public GitBranchComparison CompareBranches(string a, string b)
    {
        string ha = ResolveBranch(a), hb = ResolveBranch(b);
        var baseR = Run("merge-base", ha, hb);
        string? mergeBase = baseR.Code == 0 ? baseR.Out.Trim() : null;
        var onlyA = CommitsIn($"{hb}..{ha}");
        var onlyB = CommitsIn($"{ha}..{hb}");
        var files = new List<GitChangedFile>();
        var parts = RunOrThrow("diff", "--name-status", "-M", "-z", ha, hb).Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            string status = parts[i].Trim('\n', '\r');
            if (status.Length == 0) continue;
            if (status[0] is 'R' or 'C')
            {
                if (i + 2 < parts.Length) files.Add(new GitChangedFile(status[..1], parts[i + 2], parts[i + 1]));
                i += 2;
            }
            else if (i + 1 < parts.Length) { files.Add(new GitChangedFile(status[..1], parts[i + 1], null)); i += 1; }
        }
        return new GitBranchComparison(a, b, ha, hb, mergeBase, onlyA, onlyB, files);
    }
}
