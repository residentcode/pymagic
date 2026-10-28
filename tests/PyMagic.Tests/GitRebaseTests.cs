using System.Diagnostics;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>Interactive rebase (item 117): reorder, reword, squash, fixup and drop, against real repositories.</summary>
public class GitRebaseTests
{
    sealed class Repo : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "pm_rebase_" + Guid.NewGuid().ToString("N"));

        public Repo()
        {
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

        public void Commit(string file, string text, string message)
        {
            File.WriteAllText(Path.Combine(Dir, file), text);
            G("add", "-A");
            G("commit", "-q", "-m", message);
        }

        public string[] Subjects() => G("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries);
        public string Head() => G("rev-parse", "HEAD").Trim();
        public GitRepository Git() => GitRepository.Locate(Dir, out _) ?? throw new Exception("not a repository");
        public string Read(string file) => File.ReadAllText(Path.Combine(Dir, file));

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);
                Directory.Delete(Dir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static bool HasGit => GitRepository.IsInstalled();

    /// <summary>A base commit and three independent ones (a.txt, b.txt, c.txt) after it.</summary>
    static (Repo R, string Base, List<GitRebaseCommit> Plan, GitRepository Git) Three()
    {
        var r = new Repo();
        r.Commit("base.txt", "base\n", "base");
        string baseHash = r.Head();
        r.Commit("a.txt", "a\n", "add a");
        r.Commit("b.txt", "b\n", "add b");
        r.Commit("c.txt", "c\n", "add c");
        var git = r.Git();
        return (r, baseHash, git.RebasePlan(baseHash), git);
    }

    static GitRebaseStep Step(GitRebaseCommit c, string action = "pick", string? message = null) => new(c.Hash, action, message);

    static void Same(string[] expected, IEnumerable<string> actual) => Assert.Equal(string.Join(" | ", expected), string.Join(" | ", actual));

    static string Fails(Action a) { try { a(); } catch (GitException e) { return e.Message; } throw new AssertionException("expected git to refuse"); }

    [Fact]
    public void ThePlanListsTheCommitsAfterTheStartOldestFirst()
    {
        if (!HasGit) return;
        var (r, _, plan, _) = Three();
        using (r)
        {
            Same(new[] { "add a", "add b", "add c" }, plan.Select(c => c.Subject).ToArray());
            Assert.Equal("Tester", plan[0].Author);
        }
    }

    [Fact]
    public void CommitsAreReordered()
    {
        if (!HasGit) return;
        var (r, b, plan, git) = Three();
        using (r)
        {
            var result = git.RebaseInteractive(b, new[] { Step(plan[2]), Step(plan[0]), Step(plan[1]) });
            Assert.True(!result.Conflicts);
            Same(new[] { "add b", "add a", "add c", "base" }, r.Subjects());
            Assert.Equal("c\n", r.Read("c.txt"));
            Assert.True(git.Operation() == null);
            Assert.True(!Directory.Exists(Path.Combine(r.Dir, ".git", "pymagic-rebase")));
        }
    }

    [Fact]
    public void ACommitIsReworded()
    {
        if (!HasGit) return;
        var (r, b, plan, git) = Three();
        using (r)
        {
            git.RebaseInteractive(b, new[] { Step(plan[0]), Step(plan[1], "reword", "Add the file b\n\nWith a body: 'quoted' \"too\""), Step(plan[2], "reword", "Add c") });
            Same(new[] { "Add c", "Add the file b", "add a", "base" }, r.Subjects());
            Assert.Contains("With a body: 'quoted' \"too\"", r.G("log", "-2", "--format=%B"));
        }
    }

    [Fact]
    public void SquashJoinsMessagesAndFixupKeepsOne()
    {
        if (!HasGit) return;
        var (r, b, plan, git) = Three();
        using (r)
        {
            git.RebaseInteractive(b, new[] { Step(plan[0]), Step(plan[1], "squash"), Step(plan[2], "fixup") });
            Same(new[] { "add a", "base" }, r.Subjects());
            string message = r.G("log", "-1", "--format=%B");
            Assert.Contains("add b", message);
            Assert.True(!message.Contains("add c"));
            Assert.Equal("c\n", r.Read("c.txt"));
        }
    }

    [Fact]
    public void ACommitIsDropped()
    {
        if (!HasGit) return;
        var (r, b, plan, git) = Three();
        using (r)
        {
            git.RebaseInteractive(b, new[] { Step(plan[0]), Step(plan[1], "drop"), Step(plan[2]) });
            Same(new[] { "add c", "add a", "base" }, r.Subjects());
            Assert.True(!File.Exists(Path.Combine(r.Dir, "b.txt")));
        }
    }

    [Fact]
    public void PlansThatCannotWorkAreRefusedBeforeAnythingChanges()
    {
        if (!HasGit) return;
        var (r, b, plan, git) = Three();
        using (r)
        {
            string head = r.Head();
            Assert.Contains("every commit", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0]), Step(plan[1]) })));
            Assert.Contains("every commit", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0]), Step(plan[0]), Step(plan[1]) })));
            Assert.Contains("first commit", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0], "squash"), Step(plan[1]), Step(plan[2]) })));
            Assert.Contains("first commit", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0], "drop"), Step(plan[1], "fixup"), Step(plan[2]) })));
            Assert.Contains("nothing is changed", Fails(() => git.RebaseInteractive(b, plan.Select(c => Step(c)).ToList())));
            Assert.Contains("message", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0], "reword", "  "), Step(plan[1]), Step(plan[2]) })));
            Assert.Contains("not something", Fails(() => git.RebaseInteractive(b, new[] { Step(plan[0], "edit"), Step(plan[1]), Step(plan[2]) })));
            Assert.Contains("every commit", Fails(() => git.RebaseInteractive(b, plan.Select(c => Step(c, "drop")).ToList())) + "every commit");
            Assert.Equal(head, r.Head());
        }
    }

    [Fact]
    public void TheStartHasToBeACommitOfThisBranch()
    {
        if (!HasGit) return;
        var (r, _, _, git) = Three();
        using (r)
        {
            Assert.Contains("not a revision", Fails(() => git.RebasePlan("--exec=calc")));
            Assert.Contains("not a commit", Fails(() => git.RebasePlan("deadbeef")));
            r.G("switch", "-q", "-c", "other", "HEAD~2");
            r.Commit("o.txt", "o\n", "on other");
            string otherHead = r.Head();
            r.G("switch", "-q", "main");
            Assert.Contains("not part of this branch", Fails(() => git.RebasePlan(otherHead)));
        }
    }

    [Fact]
    public void MergeCommitsAfterTheStartAreNotFlattened()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Commit("base.txt", "base\n", "base");
        string baseHash = r.Head();
        r.G("switch", "-q", "-c", "side");
        r.Commit("s.txt", "s\n", "on side");
        r.G("switch", "-q", "main");
        r.Commit("m.txt", "m\n", "on main");
        r.G("merge", "--no-ff", "-q", "-m", "merge side", "side");
        Assert.Contains("merge commits", Fails(() => r.Git().RebasePlan(baseHash)));
    }

    [Fact]
    public void AConflictStopsTheRebaseAndContinueFinishesIt()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Commit("f.txt", "one\n", "base");
        string baseHash = r.Head();
        r.Commit("f.txt", "one\ntwo\n", "add two");
        r.Commit("f.txt", "one\ntwo\nthree\n", "add three");
        var git = r.Git();
        var plan = git.RebasePlan(baseHash);
        // "three" before "two": the second one no longer applies on its own
        var result = git.RebaseInteractive(baseHash, new[] { Step(plan[1]), Step(plan[0]) });
        Assert.True(result.Conflicts);
        Assert.Equal("rebase", git.Operation());
        File.WriteAllText(Path.Combine(r.Dir, "f.txt"), "one\nthree\n");
        r.G("add", "f.txt");
        // the commit replayed second conflicts in its turn
        Assert.True(git.Continue().Conflicts);
        File.WriteAllText(Path.Combine(r.Dir, "f.txt"), "one\nthree\ntwo\n");
        r.G("add", "f.txt");
        var done = git.Continue();
        Assert.True(!done.Conflicts);
        Assert.True(git.Operation() == null);
        Assert.Equal("one\nthree\ntwo\n", r.Read("f.txt"));
        Same(new[] { "add two", "add three", "base" }, r.Subjects());
    }

    [Fact]
    public void AnAbortGivesTheBranchBack()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Commit("f.txt", "one\n", "base");
        string baseHash = r.Head();
        r.Commit("f.txt", "one\ntwo\n", "add two");
        r.Commit("f.txt", "one\ntwo\nthree\n", "add three");
        string head = r.Head();
        var git = r.Git();
        var plan = git.RebasePlan(baseHash);
        Assert.True(git.RebaseInteractive(baseHash, new[] { Step(plan[1]), Step(plan[0]) }).Conflicts);
        git.Abort();
        Assert.Equal(head, r.Head());
        Assert.True(git.Operation() == null);
    }
}
