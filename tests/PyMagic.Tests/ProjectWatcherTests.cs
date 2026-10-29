using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The changes made to the project's files from outside the page (item 136).</summary>
public class ProjectWatcherTests
{
    [Fact]
    public void WhatIsNotTheUsersWorkIsLeftOut()
    {
        foreach (var rel in new[] { ".git/index", "pkg/__pycache__/a.cpython-313.pyc", ".venv/Lib/x.py", "node_modules/a/b.js", ".pymagic/index.json", "a.py~", "notes.tmp", "web/.#draft.py", "__pycache__" })
            Assert.True(ProjectWatcher.Ignored(rel), rel);
        foreach (var rel in new[] { "main.py", "pkg/a.py", "docs/venv-guide.md", "templates/index.html", "data/cache.json" })
            Assert.True(!ProjectWatcher.Ignored(rel), rel);
    }

    [Fact]
    public void AFileWrittenOnDiskIsTold()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_watch_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "pkg"));
        Directory.CreateDirectory(Path.Combine(dir, "__pycache__"));
        using var watcher = new ProjectWatcher(dir);
        var reader = watcher.Subscribe(out var handle);
        File.WriteAllText(Path.Combine(dir, "__pycache__", "x.pyc"), "skipped");
        File.WriteAllText(Path.Combine(dir, "pkg", "new.py"), "x = 1\n");
        var seen = new HashSet<string>();
        var until = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < until && !seen.Contains("created|pkg/new.py"))
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
            try { seen.Add(reader.ReadAsync(cts.Token).AsTask().GetAwaiter().GetResult()); }
            catch (OperationCanceledException) { }
        }
        Assert.True(seen.Contains("created|pkg/new.py"), string.Join(",", seen));
        Assert.True(!seen.Any(s => s.Contains("__pycache__")), string.Join(",", seen));
        watcher.Unsubscribe(handle);
    }
}
