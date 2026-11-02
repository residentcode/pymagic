using System.Diagnostics;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>Pushing tags, rebasing onto another branch, interactive rebase's edit, comparing two branches and binary conflicts, against real repositories.</summary>
public class GitBranchWorkTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    sealed class Box : IDisposable
    {
        public readonly string Base = Path.Combine(Path.GetTempPath(), "pm_gitbw_" + Guid.NewGuid().ToString("N"));
        public readonly string Dir;
        public Box()
        {
            Directory.CreateDirectory(Base);
            Dir = Path.Combine(Base, "work");
            Directory.CreateDirectory(Dir);
            GIn(Dir, "init", "-q", "-b", "main");
            GIn(Dir, "config", "user.name", "Tester");
            GIn(Dir, "config", "user.email", "tester@example.com");
            GIn(Dir, "config", "core.autocrlf", "false");
        }

        public static string GIn(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public string G(string first, params string[] rest) => GIn(Dir, new[] { first }.Concat(rest).ToArray());
        public void Commit(string file, string text, string message) { File.WriteAllText(Path.Combine(Dir, file), text); G("add", "-A"); G("commit", "-q", "-m", message); }
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

    [Fact]
    public void TagsArePushedAndTakenOffTheRemote()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("f.txt", "one\n", "first");
        string remote = Path.Combine(b.Base, "remote.git");
        Directory.CreateDirectory(remote);
        Box.GIn(remote, "init", "-q", "--bare", "-b", "main");
        var git = b.Git();
        git.AddRemote("origin", remote);
        git.Push(false);
        git.CreateTag("v1", null, null);
        git.CreateTag("v2", null, "release two");
        Assert.True(git.RemoteTags(null).Count == 0);
        git.PushTag("v1", null);
        Assert.Equal("v1", string.Join(",", git.RemoteTags(null)));
        git.PushAllTags(null);
        Assert.Equal("v1,v2", string.Join(",", git.RemoteTags("origin").OrderBy(x => x)));
        git.DeleteRemoteTag("v1", null);
        Assert.Equal("v2", string.Join(",", git.RemoteTags(null)));
        Assert.True(git.Tags().Any(t => t.Name == "v1"));
        // a tag that does not exist, option-looking names and an unknown remote are refused
        Assert.True(Throws(() => git.PushTag("nope", null)));
        Assert.True(Throws(() => git.PushTag("--all", null)));
        Assert.True(Throws(() => git.PushTag("v1", "--mirror")));
        // moving a tag locally and pushing it again is refused by the remote: nothing is forced
        b.Commit("g.txt", "x\n", "second");
        b.G("tag", "-f", "v2");
        Assert.True(Throws(() => git.PushTag("v2", null)));
    }

    [Fact]
    public void ABranchIsRebasedOntoAnother()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("base.txt", "base\n", "base");
        b.G("switch", "-q", "-c", "feature");
        b.Commit("f.txt", "f\n", "feature work");
        b.G("switch", "-q", "main");
        b.Commit("m.txt", "m\n", "main work");
        b.G("switch", "-q", "feature");
        var git = b.Git();
        var result = git.RebaseOnto("main", false);
        Assert.True(!result.Conflicts);
        Assert.Equal("feature work|main work|base", string.Join("|", b.G("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.True(git.Operation() == null);
        Assert.True(Throws(() => git.RebaseOnto("nobranch", false)));
        Assert.True(Throws(() => git.RebaseOnto("--onto", false)));
    }

    [Fact]
    public void ARebaseOntoAnotherBranchStopsOnAConflictAndCanBeAborted()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("f.txt", "one\n", "base");
        b.G("switch", "-q", "-c", "feature");
        b.Commit("f.txt", "feature\n", "feature edit");
        b.G("switch", "-q", "main");
        b.Commit("f.txt", "main\n", "main edit");
        b.G("switch", "-q", "feature");
        var git = b.Git();
        var result = git.RebaseOnto("main", false);
        Assert.True(result.Conflicts);
        Assert.Equal("rebase", git.Operation());
        git.Resolve("f.txt", "theirs");
        var done = git.Continue();
        Assert.True(!done.Conflicts);
        Assert.Equal("feature\n", File.ReadAllText(Path.Combine(b.Dir, "f.txt")));
        Assert.Equal("feature edit|main edit|base", string.Join("|", b.G("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries)));
    }

    [Fact]
    public void MergesAreKeptWhenAskedTo()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("base.txt", "base\n", "base");
        b.G("switch", "-q", "-c", "feature");
        b.Commit("f.txt", "f\n", "f1");
        b.G("switch", "-q", "-c", "side");
        b.Commit("s.txt", "s\n", "s1");
        b.G("switch", "-q", "feature");
        b.Commit("f2.txt", "f2\n", "f2");
        b.G("merge", "-q", "--no-ff", "-m", "merge side", "side");
        b.G("switch", "-q", "main");
        b.Commit("m.txt", "m\n", "main work");
        b.G("switch", "-q", "feature");
        var git = b.Git();
        git.RebaseOnto("main", true);
        Assert.True(b.G("log", "--merges", "--format=%s").Contains("merge side"));
        Assert.Equal(2, b.G("rev-list", "--parents", "-n1", "HEAD").Trim().Split(' ').Length - 1);
    }

    [Fact]
    public void AnInteractiveRebaseStopsAtAnEditedCommitAndGoesOn()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("base.txt", "base\n", "base");
        string baseHash = b.G("rev-parse", "HEAD").Trim();
        b.Commit("a.txt", "a\n", "add a");
        b.Commit("b.txt", "b\n", "add b");
        b.Commit("c.txt", "c\n", "add c");
        var git = b.Git();
        var plan = git.RebasePlan(baseHash);
        var stop = git.RebaseInteractive(baseHash, new[]
        {
            new GitRebaseStep(plan[0].Hash, "pick", null), new GitRebaseStep(plan[1].Hash, "edit", null), new GitRebaseStep(plan[2].Hash, "pick", null),
        });
        Assert.True(!stop.Conflicts);
        Assert.Equal("rebase", stop.Operation);
        Assert.Equal("rebase", git.Operation());
        // the branch stands at the edited commit; change it, then go on
        Assert.Equal("add b", b.G("log", "-1", "--format=%s").Trim());
        File.WriteAllText(Path.Combine(b.Dir, "extra.txt"), "extra\n");
        b.G("add", "-A");
        b.G("commit", "-q", "--amend", "--no-edit");
        var done = git.Continue();
        Assert.True(!done.Conflicts);
        Assert.True(git.Operation() == null);
        Assert.Equal("add c|add b|add a|base", string.Join("|", b.G("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("extra.txt", b.G("show", "--name-only", "--format=", "HEAD~1").Trim().Split('\n').Single(x => x.StartsWith("extra")));
    }

    [Fact]
    public void AtAnEditStopTheChangesAreAmendedAndTheRebaseGoesOn()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("base.txt", "base\n", "base");
        string baseHash = b.G("rev-parse", "HEAD").Trim();
        b.Commit("a.txt", "a\n", "add a");
        b.Commit("b.txt", "b\n", "add b");
        b.Commit("c.txt", "c\n", "add c");
        var git = b.Git();
        A.Null(git.EditStop());
        var plan = git.RebasePlan(baseHash);
        git.RebaseInteractive(baseHash, new[]
        {
            new GitRebaseStep(plan[0].Hash, "edit", null), new GitRebaseStep(plan[1].Hash, "edit", null), new GitRebaseStep(plan[2].Hash, "pick", null),
        });
        var first = git.EditStop()!;
        Assert.Equal(b.G("rev-parse", "HEAD").Trim(), first.Hash);
        Assert.Equal("add a", first.Message);

        // nothing changed and no new message: the commit is kept as it is, and the rebase stops at the next edit
        string before = first.Hash;
        var r = git.AmendAndContinue(null);
        Assert.Equal("rebase", r.Operation);
        Assert.Equal(before, b.G("rev-parse", "HEAD~1").Trim());
        Assert.Equal("add b", git.EditStop()!.Message);

        // a changed file, a new one and a new message all go into the edited commit
        File.WriteAllText(Path.Combine(b.Dir, "b.txt"), "b changed\n");
        File.WriteAllText(Path.Combine(b.Dir, "new.txt"), "new\n");
        var done = git.AmendAndContinue("add b, changed");
        Assert.True(!done.Conflicts);
        A.Null(git.Operation());
        A.Null(git.EditStop());
        Assert.Equal("add c|add b, changed|add a|base", string.Join("|", b.G("log", "--format=%s").Split('\n', StringSplitOptions.RemoveEmptyEntries)));
        Assert.Equal("b.txt|new.txt", string.Join("|", b.G("show", "--name-only", "--format=", "HEAD~1").Trim().Split('\n').Select(x => x.Trim()).OrderBy(x => x)));
        Assert.Equal("", b.G("status", "--porcelain").Trim());
    }

    [Fact]
    public void AmendAndContinueIsRefusedWhenTheRebaseIsNotAtAnEditStop()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("base.txt", "base\n", "base");
        string baseHash = b.G("rev-parse", "HEAD").Trim();
        b.Commit("a.txt", "a\n", "add a");
        b.Commit("b.txt", "b\n", "add b");
        var git = b.Git();
        Assert.Throws<GitException>(() => git.AmendAndContinue(null));
        var plan = git.RebasePlan(baseHash);
        git.RebaseInteractive(baseHash, new[] { new GitRebaseStep(plan[0].Hash, "edit", null), new GitRebaseStep(plan[1].Hash, "pick", null) });
        // a commit made after the stop: amending would change that one, so the stop is no longer offered
        b.Commit("other.txt", "o\n", "made at the stop");
        A.Null(git.EditStop());
        Assert.Throws<GitException>(() => git.AmendAndContinue(null));
        git.Abort();
    }

    [Fact]
    public void TwoBranchesAreCompared()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("same.txt", "same\n", "base");
        b.Commit("edit.txt", "1\n", "add edit");
        b.G("switch", "-q", "-c", "other");
        b.Commit("edit.txt", "2\n", "change edit");
        b.Commit("new.txt", "n\n", "add new");
        b.G("switch", "-q", "main");
        b.Commit("onlymain.txt", "m\n", "main only");
        var git = b.Git();
        var c = git.CompareBranches("main", "other");
        Assert.Equal("main only", string.Join("|", c.OnlyA.Select(x => x.Subject)));
        Assert.Equal("add new|change edit", string.Join("|", c.OnlyB.Select(x => x.Subject)));
        Assert.Equal(b.G("merge-base", "main", "other").Trim(), c.MergeBase);
        Assert.Equal(b.G("rev-parse", "main").Trim(), c.HashA);
        var files = c.Files.ToDictionary(f => f.Path, f => f.Status);
        // from main's tip to other's tip: edit.txt changed, new.txt appears, onlymain.txt is gone
        Assert.Equal("M", files["edit.txt"]);
        Assert.Equal("A", files["new.txt"]);
        Assert.Equal("D", files["onlymain.txt"]);
        Assert.True(!files.ContainsKey("same.txt"));
        Assert.True(Throws(() => git.CompareBranches("main", "nobranch")));
        Assert.True(Throws(() => git.CompareBranches("--all", "other")));
        Assert.Equal(0, git.CompareBranches("main", "main").Files.Count);
    }

    [Fact]
    public void ABinaryConflictIsSettledByTakingASide()
    {
        if (!HasGit) return;
        using var b = new Box();
        File.WriteAllBytes(Path.Combine(b.Dir, "img.bin"), new byte[] { 0, 1, 2, 3, 0, 9 });
        b.G("add", "-A");
        b.G("commit", "-q", "-m", "base");
        b.G("switch", "-q", "-c", "feature");
        File.WriteAllBytes(Path.Combine(b.Dir, "img.bin"), new byte[] { 0, 7, 7, 7, 0 });
        b.G("commit", "-q", "-am", "feature image");
        b.G("switch", "-q", "main");
        File.WriteAllBytes(Path.Combine(b.Dir, "img.bin"), new byte[] { 0, 5, 5, 0, 0, 0 });
        b.G("commit", "-q", "-am", "main image");
        var git = b.Git();
        var m = git.Merge("feature", false);
        Assert.True(m.Conflicts);
        var stages = git.ConflictStages("img.bin");
        Assert.True(stages.Binary);
        Assert.True(stages.HasOurs && stages.HasTheirs && stages.HasBase);
        Assert.True(stages.Ours == null);
        git.Resolve("img.bin", "theirs");
        git.Continue();
        Assert.Equal("00-07-07-07-00", BitConverter.ToString(File.ReadAllBytes(Path.Combine(b.Dir, "img.bin"))));
        Assert.True(git.Operation() == null);
    }

    [Fact]
    public void ATextConflictIsNotBinary()
    {
        if (!HasGit) return;
        using var b = new Box();
        b.Commit("f.txt", "one\n", "base");
        b.G("switch", "-q", "-c", "feature");
        b.Commit("f.txt", "feature\n", "feature edit");
        b.G("switch", "-q", "main");
        b.Commit("f.txt", "main\n", "main edit");
        var git = b.Git();
        git.Merge("feature", false);
        var stages = git.ConflictStages("f.txt");
        Assert.True(!stages.Binary);
        Assert.Equal("main\n", stages.Ours);
        Assert.Equal("feature\n", stages.Theirs);
    }
}
