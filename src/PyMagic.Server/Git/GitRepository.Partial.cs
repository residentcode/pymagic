using System.Text;

namespace PyMagic.Server.Git;

/// <summary>Committing single hunks and lines of a file instead of the whole file.</summary>
public sealed partial class GitRepository
{
    /// <summary>The changes of a file in the working tree against the last commit, as hunks of lines. A file git does not know yet is one hunk of added lines.</summary>
    public FileDiff DiffOf(string path)
    {
        string p = CheckPath(path);
        var status = Status();
        var file = status.Files.FirstOrDefault(f => f.Path == p);
        if (!status.HasCommits || file?.IsUntracked == true)
        {
            string full = Path.Combine(_dir, p);
            if (!File.Exists(full)) throw new GitException($"'{p}' is not a file");
            return DiffParser.ForNewFile(p, _prefix + p, File.ReadAllText(full, new UTF8Encoding(false)));
        }
        var r = Run("diff", "HEAD", "--no-color", "--no-ext-diff", "--no-renames", "-U3", "--", p);
        if (r.Code != 0) throw new GitException(Message(r));
        return DiffParser.Parse(p, r.Out);
    }

    /// <summary>
    /// Commits whole files and chosen lines of others, and nothing else: what you staged elsewhere stays staged and what you did not choose
    /// stays a change in the working tree. It is built in a private copy of the index (the last commit plus what is chosen), committed from
    /// there, and the real index is then brought level for the files that took part.
    /// </summary>
    public string CommitSelection(IReadOnlyList<string> wholeFiles, IReadOnlyDictionary<string, LineSelection> partial, string message, bool amend)
    {
        if (string.IsNullOrWhiteSpace(message) && !amend) throw new GitException("a commit message is needed");
        var whole = wholeFiles.Select(CheckPath).Distinct().ToList();
        var partialPaths = partial.Keys.Select(CheckPath).ToList();
        if (whole.Count == 0 && partialPaths.Count == 0 && !amend) throw new GitException("nothing is selected to commit");
        foreach (var p in partialPaths) if (whole.Contains(p)) throw new GitException($"'{p}' is chosen whole and in part");

        // check every chosen file before touching anything
        var patches = new List<(string Path, string Patch)>();
        foreach (var (rawPath, selection) in partial)
        {
            string p = CheckPath(rawPath);
            var diff = DiffOf(p);
            if (selection.Id != diff.Id) throw new GitException($"'{p}' changed since its lines were chosen: open it again");
            var patch = PatchBuilder.Build(diff, selection);
            if (patch == null) throw new GitException($"no line of '{p}' is chosen");
            patches.Add((p, patch));
        }

        string temp = Path.Combine(Path.GetTempPath(), "pymagic_index_" + Guid.NewGuid().ToString("N"));
        var env = new Dictionary<string, string> { ["GIT_INDEX_FILE"] = temp };
        (int Code, string Out, string Err) InTemp(string? stdin, params string[] args) => Execute(_git, _dir, args, stdin, 60_000, env);
        string Must((int Code, string Out, string Err) r) => r.Code == 0 ? r.Out : throw new GitException(Message(r));
        try
        {
            bool hasCommits = Status().HasCommits;
            Must(InTemp(null, hasCommits ? new[] { "read-tree", "HEAD" } : new[] { "read-tree", "--empty" }));
            if (whole.Count > 0)
            {
                var add = new List<string> { "add", "-A", "--" };
                add.AddRange(whole);
                Must(InTemp(null, add.ToArray()));
            }
            foreach (var (p, patch) in patches)
            {
                var r = InTemp(patch, "apply", "--cached", "--recount", "--whitespace=nowarn", "-");
                if (r.Code != 0) throw new GitException($"the chosen lines of '{p}' could not be staged: {Message(r)}");
            }
            var commit = new List<string> { "commit" };
            if (amend) commit.Add("--amend");
            if (!string.IsNullOrWhiteSpace(message)) { commit.Add("-m"); commit.Add(message); }
            else commit.Add("--no-edit");
            Must(InTemp(null, commit.ToArray()));

            // the real index: the files that took part are what the commit made them, the rest of it is as it was
            var touched = whole.Concat(patches.Select(x => x.Path)).Distinct().ToList();
            if (touched.Count > 0)
            {
                var reset = new List<string> { "reset", "-q", "--" };
                reset.AddRange(touched);
                Run(reset.ToArray());
            }
            return RunOrThrow("rev-parse", "HEAD").Trim();
        }
        finally
        {
            try { File.Delete(temp); } catch (IOException) { }
            try { File.Delete(temp + ".lock"); } catch (IOException) { }
        }
    }
}
