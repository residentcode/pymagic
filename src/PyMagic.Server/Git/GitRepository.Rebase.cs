namespace PyMagic.Server.Git;

public sealed record GitRebaseCommit(string Hash, string ShortHash, string Author, string Subject);

/// <summary>One line of the plan: what to do with a commit. <paramref name="Action"/> is pick, reword (with <paramref name="Message"/>), squash, fixup or drop.</summary>
public sealed record GitRebaseStep(string Hash, string Action, string? Message);

/// <summary>Interactive rebase: the commits of the branch after a chosen one, reordered, reworded, squashed or dropped by a plan the caller gives.</summary>
public sealed partial class GitRepository
{
    static readonly string[] RebaseActions = { "pick", "reword", "squash", "fixup", "drop" };

    /// <summary>The commits after <paramref name="baseHash"/> up to the current one, oldest first: what a rebase onto it would replay.</summary>
    public List<GitRebaseCommit> RebasePlan(string baseHash)
    {
        string b = CheckRevision(baseHash);
        if (Operation() != null) throw new GitException("finish or abort the operation that is in progress first");
        if (RunOrThrow("rev-parse", "--abbrev-ref", "HEAD").Trim() == "HEAD") throw new GitException("a branch has to be checked out (HEAD is detached)");
        if (Run("rev-parse", "--verify", "--quiet", b + "^{commit}").Code != 0) throw new GitException($"'{baseHash}' is not a commit");
        if (Run("merge-base", "--is-ancestor", b, "HEAD").Code != 0) throw new GitException("that commit is not part of this branch");
        if (!string.IsNullOrWhiteSpace(RunOrThrow("rev-list", "--merges", $"{b}..HEAD"))) throw new GitException("there are merge commits after it; a rebase would flatten them");
        var list = new List<GitRebaseCommit>();
        foreach (var line in RunOrThrow("log", "--reverse", "--format=%H%x09%h%x09%an%x09%s", $"{b}..HEAD").Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            var p = line.TrimEnd('\r').Split('\t', 4);
            if (p.Length == 4) list.Add(new GitRebaseCommit(p[0], p[1], p[2], p[3]));
        }
        return list;
    }

    /// <summary>
    /// Replays the commits after <paramref name="baseHash"/> as <paramref name="steps"/> say (every commit of <see cref="RebasePlan"/> exactly once, in the order
    /// to apply them). A conflict is a state to resolve, as with any rebase: the result says so and Continue / Abort finish it.
    /// </summary>
    public GitResult RebaseInteractive(string baseHash, IReadOnlyList<GitRebaseStep> steps)
    {
        var plan = RebasePlan(baseHash);
        if (plan.Count == 0) throw new GitException("there is nothing after that commit");
        if (steps.Count != plan.Count || !steps.Select(s => s.Hash).OrderBy(h => h, StringComparer.Ordinal).SequenceEqual(plan.Select(c => c.Hash).OrderBy(h => h, StringComparer.Ordinal)))
            throw new GitException("the plan has to name every commit after the starting point exactly once");
        foreach (var s in steps)
        {
            if (!RebaseActions.Contains(s.Action)) throw new GitException($"'{s.Action}' is not something to do with a commit");
            if (s.Action == "reword" && string.IsNullOrWhiteSpace(s.Message)) throw new GitException("a reworded commit needs a message");
        }
        var first = steps.FirstOrDefault(s => s.Action != "drop");
        if (first == null) throw new GitException("dropping every commit is not a rebase; reset the branch instead");
        if (first.Action is "squash" or "fixup") throw new GitException("the first commit cannot be squashed into the one before it");
        if (steps.Select(s => s.Hash).SequenceEqual(plan.Select(c => c.Hash)) && steps.All(s => s.Action == "pick")) throw new GitException("nothing is changed by that plan");

        // the plan and the messages live in the git folder until the rebase is over: a stop for conflicts runs the later steps from there
        string gitDir = RunOrThrow("rev-parse", "--absolute-git-dir").Trim();
        string work = Path.Combine(gitDir, "pymagic-rebase");
        if (Directory.Exists(work)) Directory.Delete(work, true);
        Directory.CreateDirectory(work);
        string Quote(string path) => "'" + path.Replace('\\', '/').Replace("'", "'\\''") + "'";
        var subjects = plan.ToDictionary(c => c.Hash, c => c.Subject);
        var todo = new System.Text.StringBuilder();
        int n = 0;
        foreach (var s in steps)
        {
            string subject = subjects[s.Hash].Replace('\n', ' ').Replace('\r', ' ');
            if (s.Action == "reword")
            {
                string file = Path.Combine(work, $"message-{++n}.txt");
                File.WriteAllText(file, s.Message!.Trim() + "\n", new System.Text.UTF8Encoding(false));
                todo.Append($"pick {s.Hash} {subject}\n");
                todo.Append($"exec git commit --amend --no-verify -q -F {Quote(file)}\n");
            }
            else todo.Append($"{s.Action} {s.Hash} {subject}\n");
        }
        string todoFile = Path.Combine(work, "todo.txt");
        File.WriteAllText(todoFile, todo.ToString(), new System.Text.UTF8Encoding(false));

        // git hands its own todo list to the "sequence editor"; ours puts the plan in its place
        var env = new Dictionary<string, string> { ["GIT_SEQUENCE_EDITOR"] = "cp " + Quote(todoFile) };
        var result = Outcome(Execute(_git, _dir, new[] { "rebase", "-i", "--no-autosquash", CheckRevision(baseHash) }, null, 120_000, env));
        if (Operation() == null) { try { Directory.Delete(work, true); } catch (IOException) { } }
        return result;
    }
}
