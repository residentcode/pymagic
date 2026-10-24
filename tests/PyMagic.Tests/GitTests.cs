using System.Diagnostics;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

public class GitTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    /// <summary>A throw-away repository with a committer identity of its own.</summary>
    sealed class TempRepo : IDisposable
    {
        public string Dir { get; } = Path.Combine(Path.GetTempPath(), "pymagic_git_" + Guid.NewGuid().ToString("N"));

        public TempRepo(bool init = true)
        {
            Directory.CreateDirectory(Dir);
            if (!init) return;
            G("init", "-q", "-b", "main");
            G("config", "user.name", "Tester");
            G("config", "user.email", "tester@example.com");
            G("config", "core.autocrlf", "false");
        }

        public string G(params string[] args) => Run(Dir, args);

        public static string Run(string dir, params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Write(string rel, string text)
        {
            string full = Path.Combine(Dir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }

        public void Commit(string message, params string[] paths)
        {
            G(new[] { "add", "--" }.Concat(paths.Length == 0 ? new[] { "." } : paths).ToArray());
            G("commit", "-q", "-m", message);
        }

        public GitRepository Open() => GitRepository.Locate(Dir, out _) ?? throw new Exception("not a repository");

        public void Dispose()
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal);   // git objects are read-only
                Directory.Delete(Dir, true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ------------------------------------------------------------ finding the repository

    [Fact]
    public void AFolderThatIsNotARepository()
    {
        if (!HasGit) return;
        using var dir = new TempRepo(init: false);
        Assert.True(GitRepository.Locate(dir.Dir, out var reason) == null);
        Assert.Contains("not a git repository", reason ?? "");
    }

    [Fact]
    public void GitThatIsMissing()
    {
        var old = Environment.GetEnvironmentVariable("PYMAGIC_GIT");
        try
        {
            Environment.SetEnvironmentVariable("PYMAGIC_GIT", Path.Combine(Path.GetTempPath(), "no_such_git_program"));
            Assert.True(GitRepository.Locate(Path.GetTempPath(), out var reason) == null);
            Assert.Contains("not installed", reason ?? "");
        }
        finally { Environment.SetEnvironmentVariable("PYMAGIC_GIT", old); }
    }

    [Fact]
    public void InitCreatesARepository()
    {
        if (!HasGit) return;
        using var dir = new TempRepo(init: false);
        var repo = GitRepository.Init(dir.Dir);
        Assert.True(repo.Status().Files.Count == 0 && !repo.Status().HasCommits);
    }

    // ------------------------------------------------------------ status

    [Fact]
    public void StatusOfChangedAddedAndNewFiles()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "a = 1\n");
        r.Write("b.py", "b = 1\n");
        r.Write("gone.py", "x = 1\n");
        r.Commit("first");
        r.Write("a.py", "a = 2\n");                 // modified, not staged
        r.Write("b.py", "b = 2\n"); r.G("add", "b.py");   // modified, staged
        File.Delete(Path.Combine(r.Dir, "gone.py"));      // deleted
        r.Write("new.py", "n = 1\n");                // untracked
        r.Write("staged_new.py", "s = 1\n"); r.G("add", "staged_new.py");

        var status = r.Open().Status();
        Assert.Equal("main", status.Branch);
        Assert.True(status.HasCommits);
        var byPath = status.Files.ToDictionary(f => f.Path);
        Assert.Equal(' ', byPath["a.py"].Index); Assert.Equal('M', byPath["a.py"].Worktree);
        Assert.Equal('M', byPath["b.py"].Index); Assert.Equal(' ', byPath["b.py"].Worktree);
        Assert.Equal('D', byPath["gone.py"].Worktree);
        Assert.True(byPath["new.py"].IsUntracked);
        Assert.Equal('A', byPath["staged_new.py"].Index);
    }

    [Fact]
    public void StatusBeforeTheFirstCommitAndOfRenames()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "a = 1\n");
        var fresh = r.Open().Status();
        Assert.True(!fresh.HasCommits);
        Assert.Equal("main", fresh.Branch);
        r.Commit("first");
        r.G("mv", "a.py", "renamed.py");
        var file = r.Open().Status().Files.Single();
        Assert.Equal("renamed.py", file.Path);
        Assert.Equal("a.py", file.OriginalPath);
        Assert.Equal('R', file.Index);
    }

    [Fact]
    public void AWorkspaceInsideTheRepositorySeesOnlyItsOwnFiles()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("outside.py", "o = 1\n");
        r.Write("app/inside.py", "i = 1\n");
        r.Commit("first");
        r.Write("outside.py", "o = 2\n");
        r.Write("app/inside.py", "i = 2\n");
        var repo = GitRepository.Locate(Path.Combine(r.Dir, "app"), out _)!;
        Assert.Equal("inside.py", repo.Status().Files.Single().Path);
        Assert.Equal("i = 1\n", repo.FileAt("HEAD", "inside.py"));
    }

    // ------------------------------------------------------------ contents

    [Fact]
    public void FileAtARevision()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        Assert.True(r.Open().FileAt("HEAD", "a.py") == null, "no commit yet");
        r.Write("a.py", "version 1\n");
        r.Commit("one");
        r.Write("a.py", "version 2\n");
        r.Commit("two");
        var repo = r.Open();
        Assert.Equal("version 2\n", repo.FileAt("HEAD", "a.py"));
        Assert.Equal("version 1\n", repo.FileAt("HEAD~1", "a.py"));
        Assert.Equal("version 1\n", repo.FileAt("HEAD^", "a.py"));
        Assert.True(repo.FileAt("HEAD", "missing.py") == null);
        Assert.Equal("version 1\n", repo.FileAt(r.G("rev-parse", "HEAD~1").Trim(), "a.py"));
    }

    [Fact]
    public void UnicodePathsAndContents()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("daten/größe.py", "name = 'Müller'\n");
        r.Commit("umlauts");
        r.Write("daten/größe.py", "name = 'Müller2'\n");
        var repo = r.Open();
        Assert.Equal("daten/größe.py", repo.Status().Files.Single().Path);
        Assert.Equal("name = 'Müller'\n", repo.FileAt("HEAD", "daten/größe.py"));
    }

    // ------------------------------------------------------------ history

    [Fact]
    public void LogAndPagingAndFileHistory()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "1\n"); r.Commit("add a");
        r.Write("b.py", "1\n"); r.Commit("add b");
        r.Write("a.py", "2\n"); r.Commit("change a");
        var repo = r.Open();
        var all = repo.Log(null, 10, 0);
        Assert.Equal("change a,add b,add a", string.Join(",", all.Select(c => c.Subject)));
        Assert.Equal("Tester", all[0].Author);
        Assert.Equal(7, all[0].ShortHash.Length);
        Assert.Equal(40, all[0].Hash.Length);
        Assert.Equal("add b", repo.Log(null, 1, 1).Single().Subject);
        Assert.Equal("change a,add a", string.Join(",", repo.Log("a.py", 10, 0).Select(c => c.Subject)));
        Assert.Equal(0, new TempRepo().Open().Log(null, 10, 0).Count);   // no commits yet
    }

    [Fact]
    public void FileHistoryFollowsARename()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("old.py", "some content that stays the same\nline two\nline three\n"); r.Commit("create");
        r.G("mv", "old.py", "new.py"); r.Commit("rename");
        r.Write("new.py", "some content that stays the same\nline two\nline three\nline four\n"); r.Commit("extend");
        Assert.Equal("extend,rename,create", string.Join(",", r.Open().Log("new.py", 10, 0).Select(c => c.Subject)));
    }

    [Fact]
    public void ACommitWithItsFiles()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("keep.py", "a\nb\nc\nd\ne\nf\n"); r.Write("drop.py", "x\n"); r.Commit("first");
        r.G("mv", "keep.py", "moved.py");
        File.Delete(Path.Combine(r.Dir, "drop.py"));
        r.Write("added.py", "n\n");
        r.G("add", "-A");
        r.G("commit", "-q", "-m", "mixed changes", "-m", "second paragraph");
        var detail = r.Open().Show(r.G("rev-parse", "HEAD").Trim());
        Assert.Equal("mixed changes", detail.Commit.Subject);
        Assert.Equal("second paragraph", detail.Body);
        var byPath = detail.Files.ToDictionary(f => f.Path);
        Assert.Equal("A", byPath["added.py"].Status);
        Assert.Equal("D", byPath["drop.py"].Status);
        Assert.Equal("R", byPath["moved.py"].Status);
        Assert.Equal("keep.py", byPath["moved.py"].OldPath);
        Assert.Equal(1, detail.Commit.Parents.Count);
    }

    // ------------------------------------------------------------ blame

    [Fact]
    public void BlameNamesTheCommitOfEveryLine()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "line one\nline two\n"); r.Commit("first");
        r.Write("a.py", "line one\nline two changed\nline three\n"); r.Commit("second");
        r.Write("a.py", "line one\nline two changed\nline three\nuncommitted\n");
        var lines = r.Open().Blame("a.py");
        Assert.Equal(4, lines.Count);
        Assert.Equal("first", lines[0].Summary);
        Assert.Equal("second", lines[1].Summary);
        Assert.Equal("second", lines[2].Summary);
        Assert.Equal("Tester", lines[0].Author);
        Assert.True(lines[0].Time > 0);
        Assert.Equal(new string('0', 40), lines[3].Hash);
        Assert.Equal(lines[1].Hash, lines[2].Hash);
    }

    // ------------------------------------------------------------ committing

    [Fact]
    public void CommitOnlyWhatIsSelected()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "a1\n"); r.Write("b.py", "b1\n"); r.Commit("first");
        r.Write("a.py", "a2\n"); r.Write("b.py", "b2\n"); r.Write("c.py", "new\n");
        var repo = r.Open();
        string hash = repo.Commit(new[] { "a.py", "c.py" }, "selected", amend: false);
        Assert.Equal(r.G("rev-parse", "HEAD").Trim(), hash);
        var after = repo.Status().Files;
        Assert.Equal("b.py", after.Single().Path);
        var detail = repo.Show(hash);
        Assert.Equal("a.py,c.py", string.Join(",", detail.Files.Select(f => f.Path).OrderBy(x => x)));
    }

    [Fact]
    public void ACommitOfADeletionAndTheFirstCommit()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "a\n");
        var repo = r.Open();
        repo.Commit(new[] { "a.py" }, "initial", amend: false);
        Assert.True(repo.Status().HasCommits);
        File.Delete(Path.Combine(r.Dir, "a.py"));
        repo.Commit(new[] { "a.py" }, "remove", amend: false);
        Assert.Equal("remove,initial", string.Join(",", repo.Log(null, 10, 0).Select(c => c.Subject)));
        Assert.Equal(0, repo.Status().Files.Count);
    }

    [Fact]
    public void AmendReplacesTheLastCommit()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "1\n"); r.Commit("first");
        r.Write("a.py", "2\n");
        var repo = r.Open();
        repo.Commit(new[] { "a.py" }, "better message", amend: true);
        var log = repo.Log(null, 10, 0);
        Assert.Equal(1, log.Count);
        Assert.Equal("better message", log[0].Subject);
        Assert.Equal("2\n", repo.FileAt("HEAD", "a.py"));
        repo.Commit(Array.Empty<string>(), "", amend: true);   // amend with the message as it is
        Assert.Equal("better message", repo.Log(null, 1, 0).Single().Subject);
    }

    [Fact]
    public void CommitRefusesWithoutAMessageOrFiles()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "1\n");
        var repo = r.Open();
        Assert.Throws<GitException>(() => repo.Commit(new[] { "a.py" }, "  ", amend: false));
        Assert.Throws<GitException>(() => repo.Commit(Array.Empty<string>(), "message", amend: false));
    }

    [Fact]
    public void MessageTextIsNeverReadAsAnOption()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "1\n");
        var repo = r.Open();
        repo.Commit(new[] { "a.py" }, "--amend --no-verify; echo hi && rm -rf x", amend: false);
        Assert.Equal("--amend --no-verify; echo hi && rm -rf x", repo.Log(null, 1, 0).Single().Subject);
    }

    // ------------------------------------------------------------ rolling back

    [Fact]
    public void DiscardRestoresModifiedFilesAndRemovesNewOnes()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "original\n"); r.Write("gone.py", "x\n"); r.Commit("first");
        r.Write("a.py", "changed\n");
        File.Delete(Path.Combine(r.Dir, "gone.py"));
        r.Write("fresh.py", "untracked\n");
        r.Write("staged.py", "staged\n"); r.G("add", "staged.py");
        r.Open().Discard(new[] { "a.py", "gone.py", "fresh.py", "staged.py" });
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(r.Dir, "a.py")));
        Assert.True(File.Exists(Path.Combine(r.Dir, "gone.py")));
        Assert.True(!File.Exists(Path.Combine(r.Dir, "fresh.py")));
        Assert.True(!File.Exists(Path.Combine(r.Dir, "staged.py")));
        Assert.Equal(0, r.Open().Status().Files.Count);
    }

    [Fact]
    public void DiscardOfAStagedChangeAndOfAFileGitDoesNotKnow()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "original\n"); r.Commit("first");
        r.Write("a.py", "staged change\n"); r.G("add", "a.py");
        r.Write("a.py", "and more\n");
        var repo = r.Open();
        repo.Discard(new[] { "a.py", "unknown.py" });
        Assert.Equal("original\n", File.ReadAllText(Path.Combine(r.Dir, "a.py")));
    }

    // ------------------------------------------------------------ branches

    [Fact]
    public void BranchesCreateAndSwitch()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "1\n"); r.Commit("first");
        var repo = r.Open();
        Assert.Equal("main", repo.Branches().Current);
        repo.CreateBranch("feature/login");
        var b = repo.Branches();
        Assert.Equal("feature/login", b.Current);
        Assert.Equal("feature/login,main", string.Join(",", b.Local.OrderBy(x => x)));
        repo.Checkout("main");
        Assert.Equal("main", repo.Status().Branch);
        Assert.Equal("main", new TempRepo().Open().Branches().Current);   // a branch without commits is listed too
    }

    [Fact]
    public void ASwitchThatWouldLoseWorkIsRefusedWithGitsWords()
    {
        if (!HasGit) return;
        using var r = new TempRepo();
        r.Write("a.py", "base\n"); r.Commit("first");
        r.G("switch", "-q", "-c", "other");
        r.Write("a.py", "on other\n"); r.Commit("other change");
        r.G("switch", "-q", "main");
        r.Write("a.py", "local edit\n");
        var repo = r.Open();
        string message = "";
        try { repo.Checkout("other"); }
        catch (GitException e) { message = e.Message; }
        Assert.Contains("a.py", message);
        Assert.Equal("main", repo.Branches().Current);
    }

    // ------------------------------------------------------------ what a caller may pass

    [Fact]
    public void PathsThatCouldEscapeOrBeOptionsAreRefused()
    {
        foreach (var bad in new[] { "../outside.py", "a/../../b.py", "-rf", "--exec=x", "/etc/passwd", "C:/Windows/x", "C:\\x", ":(top)x", ".git/config", "a/.git/x", "" })
            Assert.Throws<GitException>(() => GitRepository.CheckPath(bad));
        Assert.Equal("pkg/mod.py", GitRepository.CheckPath("pkg\\mod.py"));
        Assert.Equal("ünï/a b.py", GitRepository.CheckPath("ünï/a b.py"));
    }

    [Fact]
    public void RevisionsAndBranchNamesAreChecked()
    {
        foreach (var good in new[] { "HEAD", "HEAD~2", "HEAD^", "abcdef1", "0123456789abcdef0123456789abcdef01234567", "abcdef1^" })
            Assert.Equal(good, GitRepository.CheckRevision(good));
        foreach (var bad in new[] { "--help", "HEAD..main", "main", "abc", "HEAD~x", "$(id)", "HEAD;ls", "-1" })
            Assert.Throws<GitException>(() => GitRepository.CheckRevision(bad));
        foreach (var good in new[] { "main", "feature/login", "release-1.2", "fix_bug" })
            Assert.Equal(good, GitRepository.CheckBranch(good));
        foreach (var bad in new[] { "-d", "--force", "a..b", "a b", "x/", "a.lock", "a//b", "a@{1}", "", "$(id)", "a;b" })
            Assert.Throws<GitException>(() => GitRepository.CheckBranch(bad));
    }
}
