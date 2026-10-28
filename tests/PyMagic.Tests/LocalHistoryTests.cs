using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The versions a file had at its saves: kept newest first, not repeated, limited, and kept out of version control.</summary>
public class LocalHistoryTests
{
    static string Root() => Path.Combine(Path.GetTempPath(), "pm_hist_" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void EachDifferentTextIsAVersionAndTheNewestComesFirst()
    {
        var h = new LocalHistory(Root());
        Assert.True(h.Record("a.py", "one\n", new DateTime(2026, 1, 1, 10, 0, 0, DateTimeKind.Utc)));
        Assert.True(h.Record("a.py", "two\n", new DateTime(2026, 1, 1, 10, 5, 0, DateTimeKind.Utc)));
        Assert.True(!h.Record("a.py", "two\n"), "the same text again is not a new version");
        var list = h.List("a.py");
        Assert.Equal(2, list.Count);
        Assert.Equal("two\n", h.Read("a.py", list[0].Id));
        Assert.Equal("one\n", h.Read("a.py", list[1].Id));
        Assert.Equal(new DateTime(2026, 1, 1, 10, 5, 0, DateTimeKind.Utc), list[0].Time);
        Assert.True(h.Record("a.py", "one\n"), "going back to an old text is a version too");
        Assert.Equal(3, h.List("a.py").Count);
    }

    [Fact]
    public void TheFirstSaveCanBeUndoneBecauseTheOldTextIsKeptFirst()
    {
        var h = new LocalHistory(Root());
        h.Baseline("a.py", "before\n");
        h.Record("a.py", "after\n");
        h.Baseline("a.py", "ignored: there is a history already\n");
        var list = h.List("a.py");
        Assert.Equal(2, list.Count);
        Assert.Equal("before\n", h.Read("a.py", list[1].Id));
    }

    [Fact]
    public void OnlyTheLatestVersionsAreKept()
    {
        var h = new LocalHistory(Root(), perFile: 5);
        for (int i = 0; i < 12; i++) h.Record("a.py", "v" + i + "\n");
        var list = h.List("a.py");
        Assert.Equal(5, list.Count);
        Assert.Equal("v11\n", h.Read("a.py", list[0].Id));
        Assert.Equal("v7\n", h.Read("a.py", list[4].Id));
    }

    [Fact]
    public void TheSameFileUnderOtherSpellingsOfItsPathIsOneHistory()
    {
        var h = new LocalHistory(Root());
        h.Record("shop/models.py", "x = 1\n");
        h.Record("Shop\\Models.py", "x = 2\n");
        Assert.Equal(2, h.List("shop/models.py").Count);
        Assert.Equal(0, h.List("shop/other.py").Count);
    }

    [Fact]
    public void BinaryHugeAndInternalFilesAreNotKept()
    {
        var h = new LocalHistory(Root());
        Assert.True(!h.Record("data.bin", "a\0b"));
        Assert.True(!h.Record("big.txt", new string('x', LocalHistory.MaxFileBytes + 1)));
        Assert.True(!h.Record(".pymagic/bookmarks.json", "[]"));
        Assert.True(!h.Record(".git/config", "x"));
        Assert.Equal(0, h.List("data.bin").Count);
    }

    [Fact]
    public void AnIdThatIsNotOneIsRefusedAndTheHistoryStaysOutOfGit()
    {
        string root = Root();
        var h = new LocalHistory(root);
        h.Record("a.py", "x\n");
        Assert.True(h.Read("a.py", "../../secret") == null);
        Assert.True(h.Read("a.py", "0000000000000000001") == null);
        string ignore = File.ReadAllText(Path.Combine(root, ".pymagic", ".gitignore"));
        Assert.Contains("history/", ignore);
        // the index's own lines are added beside it, whichever came first
        PymagicFolder.IgnoreInGit(Path.Combine(root, ".pymagic"), "index.json", "history/");
        string again = File.ReadAllText(Path.Combine(root, ".pymagic", ".gitignore"));
        Assert.Contains("index.json", again);
        Assert.Equal(1, again.Split('\n').Count(l => l.Trim() == "history/"));
    }
}
