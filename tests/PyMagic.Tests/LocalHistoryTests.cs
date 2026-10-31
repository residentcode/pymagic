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

    [Fact]
    public void AMarkedSourcesRootTakesEffectWithoutAStart()
    {
        string dir = Root();
        Directory.CreateDirectory(Path.Combine(dir, "code", "pkg"));
        File.WriteAllText(Path.Combine(dir, "code", "pkg", "__init__.py"), "");
        File.WriteAllText(Path.Combine(dir, "code", "pkg", "mod.py"), "def f():\n    return 1\n");
        string main = "import pkg.mod\npkg.mod.f()\n";
        File.WriteAllText(Path.Combine(dir, "main.py"), main);
        if (ImportTests.RealPython == null) return;   // imports are checked against an interpreter
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.ConfigureInterpreter(InterpreterInspector.Inspect(ImportTests.RealPython, "system", "test").Interpreter!);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "main.py"));
        svc.Open(uri, main, 1);
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message.Contains("pkg")), "not found before code/ is marked: " + string.Join(" | ", svc.Diagnostics(uri).Select(d => d.Message)));
        svc.SetMarkedSourceRoots(new[] { Path.Combine(dir, "code") });
        Assert.True(!svc.Diagnostics(uri).Any(d => d.Message.Contains("pkg")), "found once it is: " + string.Join(" | ", svc.Diagnostics(uri).Select(d => d.Message)));
        svc.SetMarkedSourceRoots(Array.Empty<string>());
        Assert.True(svc.Diagnostics(uri).Any(d => d.Message.Contains("pkg")), "and lost when it is unmarked");
        Assert.Equal(1, svc.SourceRoots.Count);
    }

    [Fact]
    public void AFoldersHistoryHasTheVersionsOfItsFilesNewestFirst()
    {
        var h = new LocalHistory(Root());
        var t0 = new DateTime(2026, 10, 31, 10, 0, 0, DateTimeKind.Utc);
        h.Record("app/models.py", "a\n", t0);
        h.Record("app/views.py", "b\n", t0.AddMinutes(1));
        h.Record("app/models.py", "a2\n", t0.AddMinutes(2));
        h.Record("main.py", "m\n", t0.AddMinutes(3));
        h.Record("application.py", "x\n", t0.AddMinutes(4));   // not in app/
        Assert.Equal("app/models.py,app/views.py,app/models.py", string.Join(",", h.ListFolder("app").Select(x => x.Path)));
        Assert.Equal(5, h.ListFolder("").Count);
        Assert.Equal(0, h.ListFolder("nothing").Count);
    }
}
