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

    [Fact]
    public void APackageComingOrGoingInNodeModulesIsKnownByItsNodeModules()
    {
        Assert.Equal("node_modules", ProjectWatcher.NodeModulesOf("node_modules"));
        Assert.Equal("node_modules", ProjectWatcher.NodeModulesOf("node_modules/express"));
        Assert.Equal("node_modules", ProjectWatcher.NodeModulesOf("node_modules/.package-lock.json"));
        Assert.Equal("web/node_modules", ProjectWatcher.NodeModulesOf("web\\node_modules\\@types\\node"));
        Assert.True(ProjectWatcher.NodeModulesOf("node_modules/express/lib/router.js") == null, "inside a package");
        Assert.True(ProjectWatcher.NodeModulesOf("node_modules/a/node_modules/b") == null, "a package's own node_modules");
        Assert.True(ProjectWatcher.NodeModulesOf("src/app.js") == null);
    }

    [Fact]
    public void AnNpmInstallIsToldOnceItSettles()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_watch_node_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "package.json"), "{\"dependencies\": {\"express\": \"^4\", \"@types/node\": \"^20\"}}");
            Assert.True(NodePackages.Installed(dir) == null, "no node_modules yet");
            using var watcher = new ProjectWatcher(dir) { NodeSettle = TimeSpan.FromMilliseconds(500) };
            var reader = watcher.Subscribe(out var handle);
            // what npm writes: the packages, the files inside them, and its record
            foreach (var (name, version) in new[] { ("express", "4.18.2"), ("@types/node", "20.1.0") })
            {
                string pkg = Path.Combine(new[] { dir, "node_modules" }.Concat(name.Split('/')).ToArray());
                Directory.CreateDirectory(Path.Combine(pkg, "lib"));
                File.WriteAllText(Path.Combine(pkg, "package.json"), "{\"name\": \"" + name + "\", \"version\": \"" + version + "\"}");
                File.WriteAllText(Path.Combine(pkg, "lib", "index.js"), "module.exports = 1;\n");
            }
            File.WriteAllText(Path.Combine(dir, "node_modules", ".package-lock.json"), "{}");
            var seen = new List<string>();
            var until = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < until)
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
                try { seen.Add(reader.ReadAsync(cts.Token).AsTask().GetAwaiter().GetResult()); }
                catch (OperationCanceledException) { if (seen.Contains("node|node_modules")) break; }
            }
            Assert.Equal(1, seen.Count(s => s == "node|node_modules"));   // one notice for the whole install
            Assert.True(!seen.Any(s => s.Contains("lib/index.js")), "the files inside a package are not the user's");
            var installed = NodePackages.Installed(dir)!;
            Assert.Equal("4.18.2", installed["express"]);
            Assert.Equal("20.1.0", installed["@types/node"]);
            Assert.Equal(2, installed.Count);
            watcher.Unsubscribe(handle);
        }
        finally { try { Directory.Delete(dir, true); } catch (IOException) { } }
    }
}
