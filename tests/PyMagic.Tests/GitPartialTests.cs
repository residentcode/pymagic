using System.Diagnostics;
using System.Text;
using PyMagic.Server.Git;

namespace PyMagic.Tests;

/// <summary>Committing single hunks and lines: the patch that is built, and what ends up in the commit, the index and the working tree.</summary>
public class GitPartialTests
{
    static readonly bool HasGit = GitRepository.IsInstalled();

    // ------------------------------------------------------------ the patch (no git needed)

    const string Sample =
        "diff --git a/f.txt b/f.txt\n" +
        "index 1111111..2222222 100644\n" +
        "--- a/f.txt\n" +
        "+++ b/f.txt\n" +
        "@@ -1,5 +1,5 @@ section\n" +
        " one\n" +
        "-two\n" +
        "+TWO\n" +
        "+extra\n" +
        " three\n" +
        "-four\n" +
        " five\n";

    static LineSelection Select(string id, params (int Hunk, int[] Lines)[] chosen) =>
        new() { Id = id, Hunks = chosen.ToDictionary(c => c.Hunk, c => c.Lines.ToHashSet()) };

    [Fact]
    public void ADiffIsReadIntoHunksAndLines()
    {
        var d = DiffParser.Parse("f.txt", Sample);
        Assert.Equal(1, d.Hunks.Count);
        Assert.Equal("section", d.Hunks[0].Section);
        Assert.Equal(" -++ - ", string.Concat(d.Hunks[0].Lines.Select(l => l.Kind)));
        Assert.Equal(4, d.Header.Count);
        Assert.True(!d.IsNew && !d.IsDeleted && !d.IsBinary);
    }

    [Fact]
    public void OnlyTheChosenLinesAreInThePatchAndAnUnchosenRemovalStaysAContextLine()
    {
        var d = DiffParser.Parse("f.txt", Sample);
        string patch = PatchBuilder.Build(d, Select(d.Id, (0, new[] { 1, 2 })))!;
        Assert.Equal(
            "diff --git a/f.txt b/f.txt\nindex 1111111..2222222 100644\n--- a/f.txt\n+++ b/f.txt\n" +
            "@@ -1,5 +1,5 @@ section\n one\n-two\n+TWO\n three\n four\n five\n", patch);
    }

    [Fact]
    public void ChoosingEverythingGivesTheWholeChange()
    {
        var d = DiffParser.Parse("f.txt", Sample);
        string patch = PatchBuilder.Build(d, Select(d.Id, (0, new[] { 1, 2, 3, 5 })))!;
        Assert.Equal(Sample.Replace("@@ -1,5 +1,5 @@", "@@ -1,5 +1,5 @@"), patch.Replace("@@ -1,5 +1,5 @@", "@@ -1,5 +1,5 @@"));
    }

    [Fact]
    public void NothingChosenGivesNoPatch()
    {
        var d = DiffParser.Parse("f.txt", Sample);
        Assert.True(PatchBuilder.Build(d, Select(d.Id)) == null);
        Assert.True(PatchBuilder.Build(d, Select(d.Id, (0, new int[0]))) == null);
        Assert.True(PatchBuilder.Build(d, Select(d.Id, (0, new[] { 0, 4 }))) == null, "unchanged lines are not changes to choose");
    }

    [Fact]
    public void TheMarkerForAMissingFinalNewlineTravelsWithItsLine()
    {
        var d = DiffParser.Parse("f.txt", "diff --git a/f.txt b/f.txt\n--- a/f.txt\n+++ b/f.txt\n@@ -1,2 +1,2 @@\n a\n-b\n\\ No newline at end of file\n+B\n\\ No newline at end of file\n");
        Assert.True(d.Hunks[0].Lines[1].NoNewline && d.Hunks[0].Lines[2].NoNewline);
        string patch = PatchBuilder.Build(d, Select(d.Id, (0, new[] { 1, 2 })))!;
        Assert.True(patch.EndsWith("-b\n\\ No newline at end of file\n+B\n\\ No newline at end of file\n"), patch);
    }

    [Fact]
    public void BinaryAndDeletedFilesCanOnlyGoWhole()
    {
        var bin = DiffParser.Parse("x.bin", "diff --git a/x.bin b/x.bin\nBinary files a/x.bin and b/x.bin differ\n");
        Assert.True(bin.IsBinary);
        Assert.True(Throws(() => PatchBuilder.Build(bin, Select(bin.Id))));
        var del = DiffParser.Parse("d.txt", "diff --git a/d.txt b/d.txt\ndeleted file mode 100644\n--- a/d.txt\n+++ /dev/null\n@@ -1 +0,0 @@\n-gone\n");
        Assert.True(del.IsDeleted);
        Assert.True(Throws(() => PatchBuilder.Build(del, Select(del.Id, (0, new[] { 0 })))));
    }

    static bool Throws(Action a) { try { a(); return false; } catch (GitException) { return true; } }

    // ------------------------------------------------------------ real repositories

    sealed class Repo : IDisposable
    {
        public readonly string Dir = Path.Combine(Path.GetTempPath(), "pm_gitpart_" + Guid.NewGuid().ToString("N"));

        public Repo(bool commitFirst = true)
        {
            Directory.CreateDirectory(Dir);
            G("init", "-q", "-b", "main");
            G("config", "user.name", "Tester");
            G("config", "user.email", "t@e.com");
            G("config", "core.autocrlf", "false");
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
        public string Committed(string rel) => G("show", "HEAD:" + rel);
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

    /// <summary>The selection that takes the given lines of the given hunk of a file as it is now.</summary>
    static LineSelection Take(GitRepository repo, string path, int hunk, params int[] lines)
    {
        var d = repo.DiffOf(path);
        return Select(d.Id, (hunk, lines));
    }

    /// <summary>All the changed lines of one hunk.</summary>
    static int[] Changed(GitRepository repo, string path, int hunk) =>
        repo.DiffOf(path).Hunks[hunk].Lines.Select((l, i) => (l, i)).Where(x => x.l.Kind != ' ').Select(x => x.i).ToArray();

    [Fact]
    public void OneHunkOfTwoIsCommittedAndTheOtherStaysAChange()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", Numbered(30));
        r.Commit("base");
        r.Write("f.txt", Numbered(30, (2, "line 2 CHANGED"), (25, "line 25 CHANGED")));
        var repo = r.Open();
        Assert.Equal(2, repo.DiffOf("f.txt").Hunks.Count);

        var sel = Take(repo, "f.txt", 0, Changed(repo, "f.txt", 0));
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = sel }, "only the first", false);

        Assert.Equal(Numbered(30, (2, "line 2 CHANGED")), r.Committed("f.txt"));
        Assert.Equal(Numbered(30, (2, "line 2 CHANGED"), (25, "line 25 CHANGED")), r.Read("f.txt"));
        Assert.Equal("", r.G("diff", "--cached").Trim());                    // the index is level with the commit
        var left = repo.DiffOf("f.txt");
        Assert.Equal(1, left.Hunks.Count);
        Assert.True(left.Hunks[0].Lines.Any(l => l.Kind == '+' && l.Text == "line 25 CHANGED"));
        Assert.Equal("only the first", repo.Log(null, 1, 0)[0].Subject);

        // the rest can be committed afterwards
        repo.CommitSelection(new[] { "f.txt" }, new Dictionary<string, LineSelection>(), "the second", false);
        Assert.Equal(Numbered(30, (2, "line 2 CHANGED"), (25, "line 25 CHANGED")), r.Committed("f.txt"));
    }

    [Fact]
    public void SingleLinesOfOneHunk()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", "a\nb\nc\n");
        r.Commit("base");
        r.Write("f.txt", "a\nb\nNEW 1\nNEW 2\nNEW 3\nc\n");
        var repo = r.Open();
        var lines = repo.DiffOf("f.txt").Hunks[0].Lines;
        int middle = lines.FindIndex(l => l.Text == "NEW 2");
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = Take(repo, "f.txt", 0, middle) }, "just the middle one", false);
        Assert.Equal("a\nb\nNEW 2\nc\n", r.Committed("f.txt"));
        Assert.Equal("a\nb\nNEW 1\nNEW 2\nNEW 3\nc\n", r.Read("f.txt"));
    }

    [Fact]
    public void ARemovalThatIsNotChosenIsKept()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", "a\nb\nc\nd\ne\n");
        r.Commit("base");
        r.Write("f.txt", "a\ne\n");           // b, c, d removed
        var repo = r.Open();
        var lines = repo.DiffOf("f.txt").Hunks[0].Lines;
        int b = lines.FindIndex(l => l.Kind == '-' && l.Text == "b");
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = Take(repo, "f.txt", 0, b) }, "remove b only", false);
        Assert.Equal("a\nc\nd\ne\n", r.Committed("f.txt"));
        Assert.Equal("a\ne\n", r.Read("f.txt"));
    }

    [Fact]
    public void LinesOfANewFile()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("keep.txt", "k\n");
        r.Commit("base");
        r.Write("new.txt", "one\ntwo\nthree\n");
        var repo = r.Open();
        var d = repo.DiffOf("new.txt");
        Assert.True(d.IsNew);
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["new.txt"] = Select(d.Id, (0, new[] { 0, 2 })) }, "one and three", false);
        Assert.Equal("one\nthree\n", r.Committed("new.txt"));
        Assert.Equal("one\ntwo\nthree\n", r.Read("new.txt"));
        var left = repo.Status().Files.First(f => f.Path == "new.txt");
        Assert.True(left.Index == ' ' && left.Worktree == 'M', $"{left.Index}{left.Worktree}");
    }

    [Fact]
    public void WholeFilesAndChosenLinesTogetherAndWhatWasStagedElsewhereStaysStaged()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("a.txt", Numbered(20));
        r.Write("b.txt", "b\n");
        r.Write("c.txt", "c\n");
        r.Commit("base");
        r.Write("a.txt", Numbered(20, (2, "A2"), (18, "A18")));
        r.Write("b.txt", "B\n");
        r.Write("c.txt", "C staged\n");
        r.G("add", "c.txt");                       // staged by hand, not part of the commit
        var repo = r.Open();
        repo.CommitSelection(new[] { "b.txt" }, new Dictionary<string, LineSelection> { ["a.txt"] = Take(repo, "a.txt", 1, Changed(repo, "a.txt", 1)) }, "mixed", false);

        Assert.Equal("B\n", r.Committed("b.txt"));
        Assert.Equal(Numbered(20, (18, "A18")), r.Committed("a.txt"));
        Assert.Equal("c\n", r.Committed("c.txt"));
        Assert.Equal("c.txt", r.G("diff", "--cached", "--name-only").Trim());      // still staged
        Assert.Equal("C staged\n", r.Read("c.txt"));
    }

    [Fact]
    public void ASelectionMadeOnAnOlderVersionOfTheFileIsRefused()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", Numbered(10));
        r.Commit("base");
        r.Write("f.txt", Numbered(10, (3, "three")));
        var repo = r.Open();
        var sel = Take(repo, "f.txt", 0, Changed(repo, "f.txt", 0));
        r.Write("f.txt", Numbered(10, (3, "three"), (4, "four")));       // changed after the lines were chosen
        Assert.True(Throws(() => repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = sel }, "x", false)));
        Assert.Equal("base", repo.Log(null, 1, 0)[0].Subject);
    }

    [Fact]
    public void NothingChosenInAFileAndTheSameFileWholeAndPartAreRefused()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", "a\n");
        r.Commit("base");
        r.Write("f.txt", "a\nb\n");
        var repo = r.Open();
        var d = repo.DiffOf("f.txt");
        Assert.True(Throws(() => repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = Select(d.Id) }, "x", false)));
        Assert.True(Throws(() => repo.CommitSelection(new[] { "f.txt" }, new Dictionary<string, LineSelection> { ["f.txt"] = Select(d.Id, (0, new[] { 1 })) }, "x", false)));
        Assert.True(Throws(() => repo.CommitSelection(new string[0], new Dictionary<string, LineSelection>(), "x", false)));
        Assert.True(Throws(() => repo.CommitSelection(new[] { "f.txt" }, new Dictionary<string, LineSelection>(), "  ", false)));
    }

    [Fact]
    public void TheFirstCommitOfARepositoryCanTakeLinesToo()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("first.txt", "1\n2\n3\n");
        var repo = r.Open();
        Assert.True(!repo.Status().HasCommits);
        var d = repo.DiffOf("first.txt");
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["first.txt"] = Select(d.Id, (0, new[] { 0, 1 })) }, "start", false);
        Assert.Equal("1\n2\n", r.Committed("first.txt"));
        Assert.Equal("1\n2\n3\n", r.Read("first.txt"));
    }

    [Fact]
    public void WindowsLineEndsAreKept()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("w.txt", "a\r\nb\r\nc\r\nd\r\n");
        r.Commit("base");
        r.Write("w.txt", "a\r\nB\r\nc\r\nD\r\n");
        var repo = r.Open();
        var d = repo.DiffOf("w.txt");
        Assert.Equal(1, d.Hunks.Count);
        var lines = d.Hunks[0].Lines;
        int plusB = lines.FindIndex(l => l.Kind == '+' && l.Text.StartsWith("B")), minusB = lines.FindIndex(l => l.Kind == '-' && l.Text.StartsWith("b"));
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["w.txt"] = Select(d.Id, (0, new[] { minusB, plusB })) }, "B only", false);
        Assert.Equal("a\r\nB\r\nc\r\nd\r\n", r.Committed("w.txt"));
    }

    [Fact]
    public void AFileWithoutAFinalNewline()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("n.txt", "a\nb");
        r.Commit("base");
        r.Write("n.txt", "a\nb\nc");
        var repo = r.Open();
        var d = repo.DiffOf("n.txt");
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["n.txt"] = Select(d.Id, (0, Enumerable.Range(0, d.Hunks[0].Lines.Count).Where(i => d.Hunks[0].Lines[i].Kind != ' ').ToArray())) }, "all of it", false);
        Assert.Equal("a\nb\nc", r.Committed("n.txt"));
    }

    [Fact]
    public void AmendingWithChosenLines()
    {
        if (!HasGit) return;
        using var r = new Repo();
        r.Write("f.txt", Numbered(20));
        r.Commit("base");
        r.Write("f.txt", Numbered(20, (2, "two")));
        r.Commit("first change");
        r.Write("f.txt", Numbered(20, (2, "two"), (10, "ten"), (19, "nineteen")));
        var repo = r.Open();
        var d = repo.DiffOf("f.txt");
        Assert.Equal(2, d.Hunks.Count);
        repo.CommitSelection(new string[0], new Dictionary<string, LineSelection> { ["f.txt"] = Select(d.Id, (0, Changed(repo, "f.txt", 0))) }, "", amend: true);
        Assert.Equal("first change", repo.Log(null, 1, 0)[0].Subject);
        Assert.Equal(2, repo.Log(null, 5, 0).Count);
        Assert.Equal(Numbered(20, (2, "two"), (10, "ten")), r.Committed("f.txt"));
    }
}
