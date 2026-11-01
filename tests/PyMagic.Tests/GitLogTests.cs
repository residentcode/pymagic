using System.Diagnostics;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>The Log tab's filters, branches made at a commit and detached checkouts, against real repositories.</summary>
public class GitLogTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    sealed class Box : IDisposable
    {
        public readonly string Base = Path.Combine(Path.GetTempPath(), "pm_gitlog_" + Guid.NewGuid().ToString("N"));
        public readonly string Dir;
        public Box()
        {
            Directory.CreateDirectory(Base);
            Dir = Path.Combine(Base, "work");
            Directory.CreateDirectory(Dir);
            G("init", "-q", "-b", "main");
            G("config", "user.name", "Tester");
            G("config", "user.email", "tester@example.com");
            G("config", "core.autocrlf", "false");
        }

        public string G(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Commit(string file, string text, string message, string? author = null)
        {
            File.WriteAllText(Path.Combine(Dir, file), text);
            G("add", "-A");
            if (author != null) G("commit", "-q", "-m", message, "--author=" + author);
            else G("commit", "-q", "-m", message);
        }

        public GitRepository Git() => GitRepository.Locate(Dir, out _) ?? throw new Exception("not a repository");

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Base, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Base, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool Throws(Action a) { try { a(); } catch (GitException) { return true; } return false; }
    static string Subjects(IEnumerable<GitCommit> c) => string.Join("|", c.Select(x => x.Subject));

    [Fact]
    public void TheLogFiltersByBranchAuthorTextAndDate()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("a.txt", "1\n", "first");
        b.Commit("b.txt", "2\n", "Fix the parser", "Ann Writer <ann@example.com>");
        b.G("switch", "-q", "-c", "side");
        b.Commit("c.txt", "3\n", "side work");
        b.G("switch", "-q", "main");
        b.Commit("d.txt", "4\n", "main 100% done");
        var git = b.Git();

        Assert.Equal("main 100% done|Fix the parser|first", Subjects(git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, null, null))));
        Assert.Equal("side work|Fix the parser|first", Subjects(git.Log(null, 50, 0, new GitLogFilter(false, "side", null, null, null, null))));
        Assert.Equal(4, git.Log(null, 50, 0, new GitLogFilter(true, null, null, null, null, null)).Count);
        Assert.Equal("Fix the parser", Subjects(git.Log(null, 50, 0, new GitLogFilter(false, null, "ann", null, null, null))));
        Assert.Equal("Fix the parser", Subjects(git.Log(null, 50, 0, new GitLogFilter(false, null, null, "PARSER", null, null))));
        // the text is not a pattern: a percent sign or a dot means itself
        Assert.Equal("main 100% done", Subjects(git.Log(null, 50, 0, new GitLogFilter(false, null, null, "100%", null, null))));
        Assert.Equal(0, git.Log(null, 50, 0, new GitLogFilter(false, null, null, "m.in", null, null)).Count);
        // dates
        Assert.Equal(3, git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, "2000-01-01", null)).Count);
        Assert.Equal(0, git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, null, "2000-01-01")).Count);
        Assert.Equal(0, git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, DateTime.Now.AddDays(2).ToString("yyyy-MM-dd"), null)).Count);
        // nothing that is not a branch name or a date gets to git
        Assert.True(Throws(() => git.Log(null, 50, 0, new GitLogFilter(false, "--all", null, null, null, null))));
        Assert.True(Throws(() => git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, "yesterday", null))));
        // the commits come with their parents, newest first and every child before its parent: what a graph needs
        var all = git.Log(null, 50, 0, new GitLogFilter(true, null, null, null, null, null));
        for (int i = 0; i < all.Count; i++)
            foreach (var p in all[i].Parents) Assert.True(all.FindIndex(x => x.Hash == p) > i);
    }

    [Fact]
    public void ANarrowedLogJoinsEachCommitToTheNearestOneThatIsShown()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("a.txt", "1", "ann one", "Ann <ann@example.com>");
        b.Commit("b.txt", "1", "bob one", "Bob <bob@example.com>");
        b.Commit("a.txt", "2", "ann two", "Ann <ann@example.com>");
        b.Commit("b.txt", "2", "bob two", "Bob <bob@example.com>");
        var git = b.Git();
        string H(string subject) => git.Log(null, 50, 0, null).First(c => c.Subject == subject).Hash;

        // by author: Ann's second commit has Bob's first as its parent, which is not in the list, so it points at Ann's first
        var ann = git.Log(null, 50, 0, new GitLogFilter(false, null, "Ann", null, null, null));
        Assert.Equal("ann two|ann one", Subjects(ann));
        Assert.Equal(H("ann one"), string.Join(",", ann[0].Parents));
        Assert.Equal(0, ann[1].Parents.Count);
        // by path (a file, no rename following): the same for the commits that changed b.txt
        var bFile = git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, null, null, "b.txt"));
        Assert.Equal("bob two|bob one", Subjects(bFile));
        Assert.Equal(H("bob one"), string.Join(",", bFile[0].Parents));
        // a page of the list keeps the parents it would have had in one piece
        var page = git.Log(null, 1, 0, new GitLogFilter(false, null, "Ann", null, null, null));
        Assert.Equal(H("ann one"), string.Join(",", page[0].Parents));
        // an unfiltered log keeps its real parents
        Assert.Equal(H("bob one"), string.Join(",", git.Log(null, 50, 0, null).First(c => c.Subject == "ann two").Parents));
        // a path must be a path
        Assert.True(Throws(() => git.Log(null, 50, 0, new GitLogFilter(false, null, null, null, null, null, "--all"))));
    }

    [Fact]
    public void ABranchStartsAtACommitAndACommitCanBeCheckedOut()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("a.txt", "1\n", "first");
        b.Commit("a.txt", "2\n", "second");
        b.Commit("a.txt", "3\n", "third");
        var git = b.Git();
        string first = b.G("rev-parse", "HEAD~2").Trim();

        git.CreateBranchAt("old", first, false);
        Assert.Equal(first, b.G("rev-parse", "old").Trim());
        Assert.Equal("main", b.G("branch", "--show-current").Trim());

        git.CreateBranchAt("work", first, true);
        Assert.Equal("work", b.G("branch", "--show-current").Trim());
        Assert.Equal("1\n", File.ReadAllText(Path.Combine(b.Dir, "a.txt")).Replace("\r\n", "\n"));

        git.Checkout("main");
        git.CheckoutCommit(first);
        Assert.Equal("", b.G("branch", "--show-current").Trim());
        Assert.Equal(first, b.G("rev-parse", "HEAD").Trim());

        Assert.True(Throws(() => git.CreateBranchAt("--bad", first, false)));
        Assert.True(Throws(() => git.CreateBranchAt("x", "--all", false)));
        Assert.True(Throws(() => git.CheckoutCommit("-f")));
    }
}
