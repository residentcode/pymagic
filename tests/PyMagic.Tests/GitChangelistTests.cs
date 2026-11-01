using System.Diagnostics;
using System.Text;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>Changelists (.pymagic/changelists.json) and the shelf (.pymagic/shelf/), against real repositories.</summary>
public class GitChangelistTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    sealed class Repo : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "pm_gitcl_" + Guid.NewGuid().ToString("N"));

        public Repo()
        {
            Directory.CreateDirectory(Dir);
            G("init", "-q", "-b", "main");
            G("config", "user.name", "Tester");
            G("config", "user.email", "t@e.com");
            G("config", "core.autocrlf", "false");
            // PyMagic's own files are not what these tests are about
            File.AppendAllText(Path.Combine(Dir, ".git", "info", "exclude"), ".pymagic/\n");
        }

        public string G(params string[] args)
        {
            var psi = new ProcessStartInfo("git") { WorkingDirectory = Dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true, StandardOutputEncoding = new UTF8Encoding(false) };
            foreach (var a in args) psi.ArgumentList.Add(a);
            using var p = Process.Start(psi)!;
            string output = p.StandardOutput.ReadToEnd();
            p.StandardError.ReadToEnd();
            p.WaitForExit();
            return output;
        }

        public void Write(string rel, string text) { Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(Dir, rel))!); File.WriteAllBytes(Path.Combine(Dir, rel), new UTF8Encoding(false).GetBytes(text)); }
        public string Read(string rel) => new UTF8Encoding(false).GetString(File.ReadAllBytes(Path.Combine(Dir, rel)));
        public bool Has(string rel) => File.Exists(Path.Combine(Dir, rel));
        public void Commit(string msg) { G("add", "-A"); G("commit", "-q", "-m", msg); }
        public GitRepository Open() => GitRepository.Locate(Dir, out _) ?? throw new Exception("not a repository");

        public void Dispose()
        {
            try { foreach (var f in Directory.EnumerateFiles(Dir, "*", SearchOption.AllDirectories)) File.SetAttributes(f, FileAttributes.Normal); Directory.Delete(Dir, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    static string Numbered(int n, params (int Line, string Text)[] changes)
    {
        var lines = Enumerable.Range(1, n).Select(i => $"line {i}").ToList();
        foreach (var (line, text) in changes) lines[line - 1] = text;
        return string.Join("\n", lines) + "\n";
    }

    static bool Throws(Action a) { try { a(); return false; } catch (GitException) { return true; } }
    static string Names(IEnumerable<string> items) => string.Join(",", items.OrderBy(x => x, StringComparer.Ordinal));
    static Changelist List(ChangelistState s, string name) => s.Lists.First(l => l.Name == name);

    // ------------------------------------------------------------ changelists

    [Fact]
    public void NewChangesGoIntoTheActiveListAndListsCanBeMadeRenamedAndDeleted()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("a.txt", "a\n"); r.Write("b.txt", "b\n");
        r.Commit("base");
        r.Write("a.txt", "a2\n");
        var repo = r.Open();

        var s = repo.Changelists();
        Assert.Equal("Changes", s.Active);
        Assert.Equal("a.txt", Names(List(s, "Changes").Files));
        Assert.True(!r.Has(".pymagic/changelists.json"), "nothing is written for a project that did not use the feature");

        s = repo.CreateChangelist("Fix", makeActive: true);
        Assert.Equal("Fix", s.Active);
        Assert.True(r.Has(".pymagic/changelists.json"));
        r.Write("b.txt", "b2\n");
        s = repo.Changelists();
        Assert.Equal("a.txt", Names(List(s, "Changes").Files));
        Assert.Equal("b.txt", Names(List(s, "Fix").Files));       // the new change went into the active list

        s = repo.MoveToChangelist(new[] { "a.txt" }, "Fix");
        Assert.Equal("", Names(List(s, "Changes").Files));
        Assert.Equal("a.txt,b.txt", Names(List(s, "Fix").Files));

        // it is kept in the file: a new repository object reads the same
        Assert.Equal("a.txt,b.txt", Names(List(r.Open().Changelists(), "Fix").Files));

        s = repo.RenameChangelist("Fix", "Bug fix");
        Assert.Equal("Bug fix", s.Active);
        Assert.Equal("a.txt,b.txt", Names(List(s, "Bug fix").Files));

        s = repo.DeleteChangelist("Bug fix");
        Assert.Equal("Changes", s.Active);
        Assert.Equal("a.txt,b.txt", Names(List(s, "Changes").Files));
        Assert.Equal(1, s.Lists.Count);
    }

    [Fact]
    public void TheDefaultListStaysNamesAreUniqueAndCommittedFilesLeaveTheirList()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("a.txt", "a\n"); r.Write("b.txt", "b\n");
        r.Commit("base");
        var repo = r.Open();
        Assert.True(Throws(() => repo.DeleteChangelist("Changes")));
        Assert.True(Throws(() => repo.RenameChangelist("Changes", "Other")));
        Assert.True(Throws(() => repo.CreateChangelist("  ", false)));
        repo.CreateChangelist("Work", false);
        Assert.True(Throws(() => repo.CreateChangelist("work", false)));
        Assert.True(Throws(() => repo.MoveToChangelist(new[] { "a.txt" }, "Nope")));
        Assert.True(Throws(() => repo.SetActiveChangelist("Nope")));

        r.Write("a.txt", "a2\n"); r.Write("b.txt", "b2\n");
        repo.Changelists();
        repo.MoveToChangelist(new[] { "a.txt" }, "Work");
        // only the files of a list may go into a commit of that list
        Assert.True(Throws(() => repo.RequireInChangelist("Work", new[] { "a.txt", "b.txt" })));
        repo.RequireInChangelist("Work", new[] { "a.txt" });
        repo.RequireInChangelist("Changes", new[] { "b.txt" });
        repo.Commit(new[] { "a.txt" }, "work", false);
        var s = repo.Changelists();
        Assert.Equal("", Names(List(s, "Work").Files));           // committed: no longer a change
        Assert.Equal("b.txt", Names(List(s, "Changes").Files));
        Assert.Equal("b.txt", Names(repo.Status().Files.Select(f => f.Path)));
    }

    // ------------------------------------------------------------ the shelf

    [Fact]
    public void ShelvingTakesSomeFilesOutOfTheWorkingTreeAndUnshelvingBringsThemBack()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("keep.txt", "keep\n"); r.Write("mod.txt", Numbered(10)); r.Write("gone.txt", "gone\n");
        r.Commit("base");
        r.Write("mod.txt", Numbered(10, (3, "line 3 SHELVED")));
        r.Write("new.txt", "brand new\n");
        File.Delete(Path.Combine(r.Dir, "gone.txt"));
        r.Write("keep.txt", "keep changed\n");
        var repo = r.Open();
        repo.Changelists();
        repo.CreateChangelist("Idea", false);
        repo.MoveToChangelist(new[] { "mod.txt", "new.txt", "gone.txt" }, "Idea");

        var set = repo.Shelve(List(repo.Changelists(), "Idea").Files, "my idea", "Idea");
        Assert.Equal("my idea", set.Name);
        Assert.Equal("gone.txt,mod.txt,new.txt", Names(set.Files.Select(f => f.Path)));
        Assert.Equal("D", set.Files.First(f => f.Path == "gone.txt").Status);
        Assert.Equal("A", set.Files.First(f => f.Path == "new.txt").Status);
        // only those files went; the other change is still there
        Assert.Equal("keep.txt", Names(repo.Status().Files.Select(f => f.Path)));
        Assert.Equal(Numbered(10), r.Read("mod.txt"));
        Assert.True(!r.Has("new.txt"));
        Assert.True(r.Has("gone.txt"));
        Assert.Equal("keep changed\n", r.Read("keep.txt"));

        var listed = repo.ShelvedSets();
        Assert.Equal(1, listed.Count);
        Assert.Equal(set.Id, listed[0].Id);
        Assert.True(repo.ShelfPatch(set.Id).Contains("line 3 SHELVED"));
        Assert.True(repo.ShelfPatch(set.Id).Contains("+brand new"));
        Assert.True(r.Has(".pymagic/shelf/" + set.Id + ".patch"));

        var back = repo.Unshelve(set.Id, null, delete: true);
        Assert.True(!back.Conflicts);
        Assert.Equal(Numbered(10, (3, "line 3 SHELVED")), r.Read("mod.txt"));
        Assert.Equal("brand new\n", r.Read("new.txt"));
        Assert.True(!r.Has("gone.txt"));
        Assert.Equal(0, repo.ShelvedSets().Count);
        Assert.Equal("", r.G("diff", "--cached", "--name-only").Trim());       // nothing was staged
        var lists = repo.Changelists();
        Assert.Equal("mod.txt,new.txt,gone.txt".Split(',').OrderBy(x => x, StringComparer.Ordinal).Aggregate((a, b) => a + "," + b), Names(List(lists, "Idea").Files));
    }

    [Fact]
    public void AShelvedSetThatDoesNotFitAnymoreIsMergedThreeWaysAndAClashIsReported()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", Numbered(12));
        r.Commit("base");
        r.Write("f.txt", Numbered(12, (2, "line 2 SHELVED")));
        var repo = r.Open();
        var set = repo.Shelve(new[] { "f.txt" }, "two", null);
        Assert.Equal(Numbered(12), r.Read("f.txt"));

        // a neighbouring line changed in the meantime: the patch's context no longer matches, a three-way merge is needed
        r.Write("f.txt", Numbered(12, (5, "line 5 COMMITTED")));
        r.Commit("neighbour");
        // and a change that is not committed, in another place
        r.Write("f.txt", Numbered(12, (5, "line 5 COMMITTED"), (11, "line 11 LOCAL")));
        var merged = repo.Unshelve(set.Id, null, delete: false);
        Assert.True(!merged.Conflicts, "unexpected conflicts: " + string.Join(",", merged.ConflictFiles));
        Assert.Equal(Numbered(12, (2, "line 2 SHELVED"), (5, "line 5 COMMITTED"), (11, "line 11 LOCAL")), r.Read("f.txt"));
        Assert.Equal(1, repo.ShelvedSets().Count);       // kept: not asked to delete

        // the very same line changed: markers and a report, and the set stays even though a delete was asked for
        r.G("checkout", "--", "f.txt");
        r.Write("f.txt", Numbered(12, (2, "line 2 OTHER")));
        r.Commit("same line");
        var clash = repo.Unshelve(set.Id, null, delete: true);
        Assert.True(clash.Conflicts);
        Assert.Equal("f.txt", Names(clash.ConflictFiles));
        Assert.True(r.Read("f.txt").Contains("<<<<<<<"));
        Assert.Equal(1, repo.ShelvedSets().Count);
        Assert.Equal("", r.G("diff", "--cached", "--name-only").Trim());
    }

    [Fact]
    public void ShelvingRefusesWhatCannotBeShelvedAndADeletedSetIsGone()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", "one\n");
        r.Commit("base");
        var repo = r.Open();
        Assert.True(Throws(() => repo.Shelve(new[] { "f.txt" }, "nothing", null)));          // no change
        Assert.True(Throws(() => repo.Shelve(new[] { "../x" }, "bad", null)));
        Assert.True(Throws(() => repo.ShelfPatch("../../etc")));
        Assert.True(Throws(() => repo.Unshelve("20200101-000000-aaaaaa", null, true)));
        Assert.True(Throws(() => repo.DeleteShelf("nope")));

        r.Write("f.txt", "two\n");
        var set = repo.Shelve(new[] { "f.txt" }, "", "Changes");
        Assert.Equal("Changes", set.Name);        // no name given: the list it came from
        repo.DeleteShelf(set.Id);
        Assert.Equal(0, repo.ShelvedSets().Count);
        Assert.True(!r.Has(".pymagic/shelf/" + set.Id + ".patch"));
    }

    [Fact]
    public void ShelvingInARepositoryWithoutCommitsAndABinaryFileWork()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("first.txt", "hello\n");
        File.WriteAllBytes(Path.Combine(r.Dir, "data.bin"), new byte[] { 0, 1, 2, 3, 255, 0, 7 });
        var repo = r.Open();
        var set = repo.Shelve(new[] { "first.txt", "data.bin" }, "start", null);
        Assert.True(!r.Has("first.txt") && !r.Has("data.bin"));
        repo.Unshelve(set.Id, null, true);
        Assert.Equal("hello\n", r.Read("first.txt"));
        Assert.Equal("00-01-02-03-FF-00-07", BitConverter.ToString(File.ReadAllBytes(Path.Combine(r.Dir, "data.bin"))));
    }
}
