using System.Diagnostics;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>Remotes, branch management, conflicts, stash, tags and commit actions, against real repositories and a bare repository as the remote.</summary>
public class GitSyncTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    sealed class Sandbox : IDisposable
    {
        public readonly string Base = Path.Combine(Path.GetTempPath(), "pm_gitsync_" + Guid.NewGuid().ToString("N"));
        public Sandbox() { Directory.CreateDirectory(Base); }

        public static string G(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        /// <summary>A repository with an identity, on branch main.</summary>
        public string Repo(string name)
        {
            string dir = Path.Combine(Base, name);
            Directory.CreateDirectory(dir);
            G(dir, "init", "-q", "-b", "main");
            Identity(dir);
            return dir;
        }

        public static void Identity(string dir)
        {
            G(dir, "config", "user.name", "Tester");
            G(dir, "config", "user.email", "tester@example.com");
            G(dir, "config", "core.autocrlf", "false");
        }

        public string Bare(string name)
        {
            string dir = Path.Combine(Base, name);
            Directory.CreateDirectory(dir);
            G(dir, "init", "-q", "--bare", "-b", "main");
            return dir;
        }

        public string Clone(string from, string name)
        {
            string dir = Path.Combine(Base, name);
            G(Base, "clone", "-q", from, dir);
            Identity(dir);
            return dir;
        }

        public static void Write(string dir, string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, rel))!); File.WriteAllText(Path.Combine(dir, rel), text); }
        public static string Read(string dir, string rel) => File.ReadAllText(Path.Combine(dir, rel));
        public static void Commit(string dir, string message) { G(dir, "add", "-A"); G(dir, "commit", "-q", "-m", message); }
        public static GitRepository Open(string dir) => GitRepository.Locate(dir, out _) ?? throw new Exception("not a repository");

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

    /// <summary>A repository A with one commit, pushed to a bare remote.</summary>
    static (Sandbox S, string A, string Remote) Linked()
    {
        var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "one\ntwo\nthree\n");
        Sandbox.Commit(a, "first");
        string remote = s.Bare("remote.git");
        var repo = Sandbox.Open(a);
        repo.AddRemote("origin", remote);
        repo.Push(false);
        return (s, a, remote);
    }

    // ------------------------------------------------------------ names

    [Fact]
    public void RemoteAddressesThatRunProgramsOrAreOptionsAreRefused()
    {
        foreach (var bad in new[] { "ext::sh -c touch /tmp/x", "--upload-pack=calc", "-oProxyCommand=x", "has space", "", "fd::3", "notaurl" })
            Assert.True(Throws(() => GitRepository.CheckRemoteUrl(bad)), bad);
        foreach (var good in new[] { "https://github.com/a/b.git", "git@github.com:a/b.git", "ssh://git@host/a/b", @"C:\repos\x.git", "/srv/git/x.git", "file:///srv/x.git" })
            Assert.Equal(good, GitRepository.CheckRemoteUrl(good));
        Assert.True(Throws(() => GitRepository.CheckRemoteName("-x")));
        Assert.True(Throws(() => GitRepository.CheckRemoteName("a..b")));
    }

    static void Empty<T>(IEnumerable<T> items) => Assert.True(!items.Any(), "expected nothing, got: " + string.Join(", ", items));
    static void IsNull(object? o) => Assert.True(o == null, "expected null, got: " + o);
    static void In(string item, IEnumerable<string> list) => Assert.True(list.Contains(item), item + " is not in: " + string.Join(", ", list));
    static void NotIn(string item, IEnumerable<string> list) => Assert.True(!list.Contains(item), item + " is in: " + string.Join(", ", list));

    static string Fails(Action a) { try { a(); } catch (GitException e) { return e.Message; } throw new AssertionException("expected git to refuse"); }

    static bool Throws(Action a) { try { a(); return false; } catch (GitException) { return true; } }

    // ------------------------------------------------------------ remotes

    [Fact]
    public void PushingAFirstBranchSetsItsUpstream()
    {
        if (!HasGit) return;
        var (s, a, _) = Linked();
        using var _s = s;
        var status = Sandbox.Open(a).Status();
        Assert.Equal("origin/main", status.Upstream);
        Assert.Equal(0, status.Ahead);
        var remotes = Sandbox.Open(a).Remotes();
        Assert.Equal(1, remotes.Count);
        Assert.Equal("origin", remotes[0].Name);
        In("origin/main", Sandbox.Open(a).RemoteBranches());
    }

    [Fact]
    public void FetchShowsWhatIsNewAndAFastForwardPullTakesIt()
    {
        if (!HasGit) return;
        var (s, a, remote) = Linked();
        using var _s = s;
        string b = s.Clone(remote, "b");
        Sandbox.Write(b, "g.txt", "from b\n");
        Sandbox.Commit(b, "second");
        Sandbox.Open(b).Push(false);

        var repo = Sandbox.Open(a);
        repo.Fetch(null, true);
        Assert.Equal(1, repo.Status().Behind);
        var pulled = repo.Pull("ff-only");
        Assert.True(!pulled.Conflicts && pulled.Operation == null);
        Assert.Equal("from b\n", Sandbox.Read(a, "g.txt"));
        Assert.Equal(0, repo.Status().Behind);
    }

    [Fact]
    public void ARejectedPushIsReportedWithGitsWords()
    {
        if (!HasGit) return;
        var (s, a, remote) = Linked();
        using var _s = s;
        string b = s.Clone(remote, "b");
        Sandbox.Write(b, "g.txt", "b\n");
        Sandbox.Commit(b, "from b");
        Sandbox.Open(b).Push(false);

        Sandbox.Write(a, "h.txt", "a\n");
        Sandbox.Commit(a, "from a");
        string msg = Fails(() => Sandbox.Open(a).Push(false));
        Assert.True(msg.Contains("rejected") || msg.Contains("failed to push"), msg);
    }

    [Fact]
    public void PushingWithoutARemoteSaysSo()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("lonely");
        Sandbox.Write(a, "f.txt", "x\n");
        Sandbox.Commit(a, "c");
        string msg = Fails(() => Sandbox.Open(a).Push(false));
        Assert.True(msg.Contains("no remote"), msg);
    }

    [Fact]
    public void ARebasePullKeepsTheHistoryStraight()
    {
        if (!HasGit) return;
        var (s, a, remote) = Linked();
        using var _s = s;
        string b = s.Clone(remote, "b");
        Sandbox.Write(b, "remote.txt", "r\n");
        Sandbox.Commit(b, "remote work");
        Sandbox.Open(b).Push(false);
        Sandbox.Write(a, "local.txt", "l\n");
        Sandbox.Commit(a, "local work");

        var result = Sandbox.Open(a).Pull("rebase");
        Assert.True(!result.Conflicts);
        var log = Sandbox.Open(a).Log(null, 10, 0);
        Assert.Equal(string.Join(",", new[] { "local work", "remote work", "first" }), string.Join(",", log.Select(c => c.Subject)));
        Assert.True(log.All(c => c.Parents.Count <= 1), "no merge commit");
    }

    // ------------------------------------------------------------ conflicts

    static (Sandbox S, string A, GitRepository Repo) Conflicted(out GitResult pulled)
    {
        var (s, a, remote) = Linked();
        string b = s.Clone(remote, "b");
        Sandbox.Write(b, "f.txt", "one\nTWO from b\nthree\n");
        Sandbox.Commit(b, "b edits");
        Sandbox.Open(b).Push(false);
        Sandbox.Write(a, "f.txt", "one\ntwo from a\nthree\n");
        Sandbox.Commit(a, "a edits");
        var repo = Sandbox.Open(a);
        pulled = repo.Pull("merge");
        return (s, a, repo);
    }

    [Fact]
    public void AConflictingPullStopsAndSaysWhatToResolve()
    {
        if (!HasGit) return;
        var (s, a, repo) = Conflicted(out var pulled);
        using var _s = s;
        Assert.True(pulled.Conflicts);
        Assert.Equal("merge", pulled.Operation);
        Assert.Equal("merge", repo.Operation());
        Assert.Equal(string.Join(",", new[] { "f.txt" }), string.Join(",", repo.Conflicts()));
        Assert.Contains("<<<<<<<", Sandbox.Read(a, "f.txt"));

        var stages = repo.ConflictStages("f.txt");
        Assert.Equal("one\ntwo\nthree\n", stages.Base);
        Assert.Equal("one\ntwo from a\nthree\n", stages.Ours);
        Assert.Equal("one\nTWO from b\nthree\n", stages.Theirs);
    }

    [Fact]
    public void ChoosingASideAndContinuingFinishesTheMerge()
    {
        if (!HasGit) return;
        var (s, a, repo) = Conflicted(out _);
        using var _s = s;
        Assert.True(Throws(() => repo.Continue()), "continuing with a conflict left is refused");
        repo.Resolve("f.txt", "theirs");
        Empty(repo.Conflicts());
        var done = repo.Continue();
        Assert.True(!done.Conflicts && done.Operation == null);
        Assert.Equal("one\nTWO from b\nthree\n", Sandbox.Read(a, "f.txt"));
        Assert.Equal(2, repo.Log(null, 1, 0)[0].Parents.Count);   // a merge commit
    }

    [Fact]
    public void ResolvingByHandThenMarkingItResolved()
    {
        if (!HasGit) return;
        var (s, a, repo) = Conflicted(out _);
        using var _s = s;
        Sandbox.Write(a, "f.txt", "one\nboth\nthree\n");
        repo.MarkResolved("f.txt");
        repo.Continue();
        Assert.Equal("one\nboth\nthree\n", Sandbox.Read(a, "f.txt"));
        IsNull(repo.Operation());
    }

    [Fact]
    public void AbortingPutsEverythingBack()
    {
        if (!HasGit) return;
        var (s, a, repo) = Conflicted(out _);
        using var _s = s;
        repo.Abort();
        IsNull(repo.Operation());
        Assert.Equal("one\ntwo from a\nthree\n", Sandbox.Read(a, "f.txt"));
        Empty(repo.Conflicts());
    }

    [Fact]
    public void NothingToAbortOrContinueIsAnError()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("calm");
        Sandbox.Write(a, "f.txt", "x\n");
        Sandbox.Commit(a, "c");
        Assert.True(Throws(() => Sandbox.Open(a).Abort()));
        Assert.True(Throws(() => Sandbox.Open(a).Continue()));
        Assert.True(Throws(() => Sandbox.Open(a).Resolve("f.txt", "ours")));
        Assert.True(Throws(() => Sandbox.Open(a).Resolve("f.txt", "middle")));
    }

    // ------------------------------------------------------------ branches

    [Fact]
    public void BranchesCanBeMergedRenamedAndDeleted()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "base\n");
        Sandbox.Commit(a, "base");
        var repo = Sandbox.Open(a);
        repo.CreateBranch("feature");
        Sandbox.Write(a, "feature.txt", "feature work\n");
        Sandbox.Commit(a, "feature work");
        repo.Checkout("main");

        Assert.True(Throws(() => repo.DeleteBranch("feature", false)), "an unmerged branch is not deleted without force");
        Assert.True(Throws(() => repo.DeleteBranch("main", true)), "the current branch is not deleted");
        var merged = repo.Merge("feature", noFastForward: true);
        Assert.True(!merged.Conflicts);
        Assert.Equal(2, repo.Log(null, 1, 0)[0].Parents.Count);
        repo.DeleteBranch("feature", false);
        NotIn("feature", repo.Branches().Local);

        repo.CreateBranch("temp");
        repo.RenameBranch("temp", "renamed");
        In("renamed", repo.Branches().Local);
        Assert.True(Throws(() => repo.Merge("no-such-branch", false)));
    }

    [Fact]
    public void ARemoteBranchCanBeCheckedOutAsALocalOne()
    {
        if (!HasGit) return;
        var (s, a, remote) = Linked();
        using var _s = s;
        string b = s.Clone(remote, "b");
        Sandbox.Open(b).CreateBranch("topic");
        Sandbox.Write(b, "topic.txt", "t\n");
        Sandbox.Commit(b, "topic");
        Sandbox.G(b, "push", "-q", "-u", "origin", "topic");

        var repo = Sandbox.Open(a);
        NotIn("origin/topic", repo.RemoteBranches());
        repo.Fetch("origin", false);
        In("origin/topic", repo.Branches().Remote);
        repo.CheckoutRemoteBranch("origin/topic");
        Assert.Equal("topic", repo.Branches().Current);
        Assert.Equal("origin/topic", repo.Status().Upstream);
        Assert.True(Throws(() => repo.CheckoutRemoteBranch("origin/nothing")));
        Assert.True(Throws(() => repo.CheckoutRemoteBranch("--detach")));
    }

    // ------------------------------------------------------------ stash

    [Fact]
    public void StashPutsChangesAwayAndBringsThemBack()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "base\n");
        Sandbox.Commit(a, "base");
        Sandbox.Write(a, "f.txt", "changed\n");
        Sandbox.Write(a, "new.txt", "untracked\n");
        var repo = Sandbox.Open(a);

        repo.Stash("half done", includeUntracked: true);
        Empty(repo.Status().Files);
        var list = repo.Stashes();
        Assert.Equal(1, list.Count);
        Assert.Equal("half done", list[0].Message);
        Assert.Equal("main", list[0].Branch);
        Assert.Equal(string.Join(",", new[] { "f.txt", "new.txt" }), string.Join(",", repo.StashFiles(0).Select(f => f.Path).OrderBy(x => x)));

        var back = repo.ApplyStash(0, drop: true);
        Assert.True(!back.Conflicts);
        Assert.Equal("changed\n", Sandbox.Read(a, "f.txt"));
        Assert.Equal("untracked\n", Sandbox.Read(a, "new.txt"));
        Empty(repo.Stashes());
    }

    [Fact]
    public void ADroppedStashIsGoneAndAnUnknownOneIsAnError()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "base\n");
        Sandbox.Commit(a, "base");
        Sandbox.Write(a, "f.txt", "x\n");
        var repo = Sandbox.Open(a);
        repo.Stash(null, false);
        Assert.Equal(1, repo.Stashes().Count);
        repo.DropStash(0);
        Empty(repo.Stashes());
        Assert.True(Throws(() => repo.DropStash(0)));
        Assert.True(Throws(() => repo.ApplyStash(-1, false)));
        Assert.Contains("No local changes", repo.Stash(null, false));
    }

    // ------------------------------------------------------------ commit actions

    [Fact]
    public void RevertCherryPickAndReset()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "1\n");
        Sandbox.Commit(a, "c1");
        var repo = Sandbox.Open(a);
        repo.CreateBranch("side");
        Sandbox.Write(a, "side.txt", "s\n");
        Sandbox.Commit(a, "side work");
        string sideHash = repo.Log(null, 1, 0)[0].Hash;
        repo.Checkout("main");
        Sandbox.Write(a, "f.txt", "2\n");
        Sandbox.Commit(a, "c2");
        string c2 = repo.Log(null, 1, 0)[0].Hash;

        var picked = repo.CherryPick(sideHash);
        Assert.True(!picked.Conflicts);
        Assert.Equal("s\n", Sandbox.Read(a, "side.txt"));
        Assert.Equal("side work", repo.Log(null, 1, 0)[0].Subject);

        repo.Revert(c2);
        Assert.Equal("1\n", Sandbox.Read(a, "f.txt"));
        Assert.True(repo.Log(null, 1, 0)[0].Subject.StartsWith("Revert"));

        repo.Reset("HEAD~1", "soft");
        Assert.Equal(1, repo.Status().Files.Count(f => f.Index != ' ' && f.Index != '?'));
        repo.Reset("HEAD", "mixed");
        Assert.Equal("1\n", Sandbox.Read(a, "f.txt"));
        repo.Reset("HEAD~1", "hard");
        Assert.Equal("2\n", Sandbox.Read(a, "f.txt"));
        Assert.True(!File.Exists(Path.Combine(a, "side.txt")) || repo.Log(null, 1, 0)[0].Subject != "side work");
        Assert.True(Throws(() => repo.Reset("--hard", "hard")));
        Assert.True(Throws(() => repo.Reset("HEAD", "everything")));
        Assert.True(Throws(() => repo.Revert("not-a-hash")));
    }

    [Fact]
    public void ARevertThatConflictsStopsForYou()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "1\n");
        Sandbox.Commit(a, "c1");
        Sandbox.Write(a, "f.txt", "2\n");
        Sandbox.Commit(a, "c2");
        string c2 = Sandbox.Open(a).Log(null, 1, 0)[0].Hash;
        Sandbox.Write(a, "f.txt", "3\n");
        Sandbox.Commit(a, "c3");
        var repo = Sandbox.Open(a);
        var r = repo.Revert(c2);
        Assert.True(r.Conflicts);
        Assert.Equal("revert", r.Operation);
        repo.Abort();
        IsNull(repo.Operation());
    }

    // ------------------------------------------------------------ tags

    [Fact]
    public void TagsCanBeMadeListedAndDeleted()
    {
        if (!HasGit) return;
        using var s = new Sandbox();
        string a = s.Repo("a");
        Sandbox.Write(a, "f.txt", "1\n");
        Sandbox.Commit(a, "c1");
        var repo = Sandbox.Open(a);
        repo.CreateTag("v1", null, null);
        repo.CreateTag("v1.1", null, "release 1.1");
        var tags = repo.Tags();
        Assert.Equal(string.Join(",", new[] { "v1", "v1.1" }), string.Join(",", tags.Select(t => t.Name).OrderBy(x => x)));
        Assert.Equal("release 1.1", tags.First(t => t.Name == "v1.1").Subject);
        repo.DeleteTag("v1");
        Assert.Equal(1, repo.Tags().Count);
        Assert.True(Throws(() => repo.CreateTag("bad name", null, null)));
        Assert.True(Throws(() => repo.CreateTag("--force", null, null)));
        Assert.True(Throws(() => repo.CreateTag("v2", "--all", null)));
    }
}
