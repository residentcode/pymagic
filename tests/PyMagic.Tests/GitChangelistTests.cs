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

    // ------------------------------------------------------------ the changes of a file, one by one

    /// <summary>A file of 30 lines with three changes far apart: lines 3, 15 and 28.</summary>
    static GitRepository ThreeChanges(Repo r)
    {
        r.Write("a.txt", Numbered(30));
        r.Commit("base");
        r.Write("a.txt", Numbered(30, (3, "three"), (15, "fifteen"), (28, "twenty-eight")));
        return r.Open();
    }

    static string KeyAt(IEnumerable<ChangePart> parts, int newLine) => parts.First(c => c.NewLine == newLine).Key;

    [Fact]
    public void ChangesOfAFileAreMovedToAnotherListOneByOneAndStayThereWhileTheFileChangesElsewhere()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        var parts = repo.ChangeParts("a.txt");
        Assert.Equal("3,15,28", string.Join(",", parts.Select(c => c.NewLine)));
        Assert.True(parts.All(c => c.List == "Changes"));
        Assert.Equal("three", parts[0].Preview);
        repo.CreateChangelist("Fix", false);

        var state = repo.MoveChanges("a.txt", new[] { KeyAt(parts, 15) }, "Fix");
        Assert.Equal("Changes", string.Join(",", state.Lists.Where(l => l.Files.Contains("a.txt")).Select(l => l.Name)));   // the file stays where it was
        Assert.Equal(2, state.PartCounts!["a.txt"]["Changes"]);
        Assert.Equal(1, state.PartCounts["a.txt"]["Fix"]);
        Assert.Equal("Changes,Fix,Changes", string.Join(",", repo.ChangeParts("a.txt").Select(c => c.List)));

        // the file is edited somewhere else and one of the other changes moves down: the moved change keeps its list
        r.Write("a.txt", Numbered(30, (3, "three"), (10, "ten"), (15, "fifteen"), (28, "twenty-eight")));
        var after = repo.ChangeParts("a.txt");
        Assert.Equal("3,10,15,28", string.Join(",", after.Select(c => c.NewLine)));
        Assert.Equal("Changes,Changes,Fix,Changes", string.Join(",", after.Select(c => c.List)));

        // taking a change back to the list of its file removes the mark
        var back = repo.MoveChanges("a.txt", new[] { KeyAt(after, 15) }, "Changes");
        Assert.True(back.PartCounts == null, "nothing is in part any more");
        Assert.True(!File.ReadAllText(Path.Combine(r.Dir, ".pymagic", "changelists.json")).Contains("\"parts\""));
    }

    [Fact]
    public void WhenEveryChangeOfAFileHasGoneToOneListTheFileIsThatListsAndAChangeThatIsGoneForgetsItsList()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        repo.CreateChangelist("Fix", false);
        var keys = repo.ChangeParts("a.txt").Select(c => c.Key).ToArray();
        var state = repo.MoveChanges("a.txt", keys, "Fix");
        Assert.Equal("Fix", string.Join(",", state.Lists.Where(l => l.Files.Contains("a.txt")).Select(l => l.Name)));
        Assert.True(state.PartCounts == null);

        // one change to Changes, then that change is reverted by hand: nothing is left of the mark
        var one = repo.MoveChanges("a.txt", new[] { keys[0] }, "Changes");
        Assert.Equal(1, one.PartCounts!["a.txt"]["Changes"]);
        r.Write("a.txt", Numbered(30, (15, "fifteen"), (28, "twenty-eight")));
        Assert.True(repo.Changelists().PartCounts == null);
        Assert.True(Throws(() => repo.MoveChanges("a.txt", new[] { "no-such-change" }, "Fix")));
        Assert.True(Throws(() => repo.MoveChanges("a.txt", keys.Take(1).ToArray(), "No such list")));
    }

    [Fact]
    public void RenamingAndDeletingAListCarryItsChangesWithIt()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        repo.CreateChangelist("Fix", false);
        repo.MoveChanges("a.txt", new[] { KeyAt(repo.ChangeParts("a.txt"), 28) }, "Fix");
        var renamed = repo.RenameChangelist("Fix", "Fixes");
        Assert.Equal(1, repo.Changelists().PartCounts!["a.txt"]["Fixes"]);
        Assert.Equal("Changes,Changes,Fixes", string.Join(",", repo.ChangeParts("a.txt").Select(c => c.List)));
        repo.DeleteChangelist("Fixes");
        Assert.True(repo.Changelists().PartCounts == null, "the change is back with its file");
        Assert.Equal("Changes,Changes,Changes", string.Join(",", repo.ChangeParts("a.txt").Select(c => c.List)));
        // a whole file moved takes its changes along
        repo.CreateChangelist("Other", false);
        repo.CreateChangelist("Third", false);
        repo.MoveChanges("a.txt", new[] { KeyAt(repo.ChangeParts("a.txt"), 3) }, "Other");
        repo.MoveToChangelist(new[] { "a.txt" }, "Third");
        Assert.True(repo.Changelists().PartCounts == null);
        Assert.Equal("Third,Third,Third", string.Join(",", repo.ChangeParts("a.txt").Select(c => c.List)));
        Assert.Equal(1, renamed.Lists.Count(l => l.Name == "Fixes"));
    }

    [Fact]
    public void ACommitOfAListTakesJustTheChangesThatAreInIt()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        repo.CreateChangelist("Fix", false);
        repo.MoveChanges("a.txt", new[] { KeyAt(repo.ChangeParts("a.txt"), 15) }, "Fix");
        r.Write("b.txt", "new file\n");

        // the list Fix holds one change of a.txt; the list Changes holds the other two and b.txt
        var (whole, partial) = repo.ResolveForList("Fix", new[] { "a.txt" }, new Dictionary<string, LineSelection>());
        Assert.Equal(0, whole.Count);
        Assert.Equal(1, partial.Count);
        repo.CommitSelection(whole, partial, "only the fifteen", false);
        string shown = r.G("show", "--format=", "HEAD");
        Assert.Contains("+fifteen", shown);
        Assert.True(!shown.Contains("+three") && !shown.Contains("+twenty-eight"), shown);
        Assert.Equal(Numbered(30, (3, "three"), (15, "fifteen"), (28, "twenty-eight")), r.Read("a.txt"));
        // the other two are still changes, and the mark of the one that was committed is gone
        Assert.Equal("3,28", string.Join(",", repo.ChangeParts("a.txt").Select(c => c.NewLine)));
        Assert.True(repo.Changelists().PartCounts == null);

        // a file that is whole in the list goes whole; one with no change in the list is left out
        var again = repo.ResolveForList("Changes", new[] { "a.txt", "b.txt" }, new Dictionary<string, LineSelection>());
        Assert.Equal("a.txt,b.txt", Names(again.Whole));
        Assert.Equal(0, again.Partial.Count);
    }

    [Fact]
    public void AFileOfWhichOnlySomeChangesAreInAListCanBeNamedInThatListsCommitAndNotInAnothers()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        repo.CreateChangelist("Fix", false);
        repo.CreateChangelist("Empty", false);
        repo.MoveChanges("a.txt", new[] { KeyAt(repo.ChangeParts("a.txt"), 3) }, "Fix");
        repo.RequireInChangelist("Fix", new[] { "a.txt" });
        repo.RequireInChangelist("Changes", new[] { "a.txt" });
        Assert.True(Throws(() => repo.RequireInChangelist("Empty", new[] { "a.txt" })));
        // a list with no change of the file leaves it out of its commit
        var none = repo.ResolveForList("Empty", new[] { "a.txt" }, new Dictionary<string, LineSelection>());
        Assert.True(none.Whole.Count == 0 && none.Partial.Count == 0);
    }

    [Fact]
    public void SomeChangesOfAFileAreShelvedAndTheRestStaysAndTheyComeBack()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        var parts = repo.ChangeParts("a.txt");
        var set = repo.ShelveChanges("a.txt", new[] { KeyAt(parts, 3), KeyAt(parts, 28) }, "two of three", null);
        Assert.Equal("two of three", set.Name);
        Assert.Equal("a.txt", string.Join(",", set.Files.Select(f => f.Path)));
        Assert.Equal(Numbered(30, (15, "fifteen")), r.Read("a.txt"));
        string patch = repo.ShelfPatch(set.Id);
        Assert.True(patch.Contains("+three") && patch.Contains("+twenty-eight") && !patch.Contains("fifteen"), patch);

        var back = repo.Unshelve(set.Id, null, true);
        Assert.True(!back.Conflicts);
        Assert.Equal(Numbered(30, (3, "three"), (15, "fifteen"), (28, "twenty-eight")), r.Read("a.txt"));
        Assert.Equal(0, repo.ShelvedSets().Count);

        // the rest of the cases: all of them is just a smaller shelve, a change that is not there is refused, a new file is shelved whole
        Assert.True(Throws(() => repo.ShelveChanges("a.txt", new[] { "no-such-change" }, null, null)));
        Assert.True(Throws(() => repo.ShelveChanges("a.txt", Array.Empty<string>(), null, null)));
        r.Write("new.txt", "x\ny\n");
        Assert.True(Throws(() => repo.ShelveChanges("new.txt", new[] { "any" }, null, null)));
    }

    [Fact]
    public void ShelvingAListTakesOnlyTheChangesOfAFileThatAreInIt()
    {
        if (!HasGit) return;
        using var r = new Repo();
        var repo = ThreeChanges(r);
        r.Write("b.txt", "keep\n");
        repo.CreateChangelist("Fix", false);
        repo.MoveChanges("a.txt", new[] { KeyAt(repo.ChangeParts("a.txt"), 15) }, "Fix");
        repo.MoveToChangelist(new[] { "b.txt" }, "Fix");
        var set = repo.Shelve(new[] { "a.txt", "b.txt" }, null, "Fix");
        Assert.Equal("a.txt,b.txt", Names(set.Files.Select(f => f.Path)));
        Assert.True(!r.Has("b.txt"), "the whole file went");
        Assert.Equal(Numbered(30, (3, "three"), (28, "twenty-eight")), r.Read("a.txt"));      // from a.txt only the change of the list
        string patch = repo.ShelfPatch(set.Id);
        Assert.True(patch.Contains("+fifteen") && patch.Contains("+keep") && !patch.Contains("+three"), patch);
        Assert.True(!repo.Unshelve(set.Id, null, true).Conflicts);
        Assert.Equal(Numbered(30, (3, "three"), (15, "fifteen"), (28, "twenty-eight")), r.Read("a.txt"));
        Assert.Equal("keep\n", r.Read("b.txt"));
    }

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
