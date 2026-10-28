using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The project folders of a service: the host adds one and the client's initialize names it again (item 123).</summary>
public class SourceRootTests
{
    [Fact]
    public void TheSameFolderIsNotAddedTwiceInAnySpelling()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_roots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var svc = new LanguageService();
            Assert.True(svc.AddSourceRoot(dir));
            Assert.True(!svc.AddSourceRoot(dir));
            Assert.True(!svc.AddSourceRoot(dir + Path.DirectorySeparatorChar));
            Assert.True(!svc.AddSourceRoot(dir.ToUpperInvariant()));
            Assert.True(!svc.AddSourceRoot(new Uri(dir + Path.DirectorySeparatorChar).LocalPath));
            Assert.Equal(1, svc.SourceRoots.Count);
            string other = Path.Combine(dir, "sub");
            Directory.CreateDirectory(other);
            Assert.True(svc.AddSourceRoot(other));
            Assert.Equal(2, svc.SourceRoots.Count);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void IndexingWhileTheClientNamesTheRootAgainCompletes()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_roots_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < 40; i++)
            {
                string sub = Path.Combine(dir, "pkg" + i);
                Directory.CreateDirectory(sub);
                for (int j = 0; j < 5; j++) File.WriteAllText(Path.Combine(sub, $"m{j}.py"), $"def f{i}_{j}():\n    pass\n");
            }
            var svc = new LanguageService();
            svc.AddSourceRoot(dir);
            var indexing = svc.StartIndexing()!;
            // what initialize does a moment later, over and over while the index is being read
            for (int i = 0; i < 2000; i++) svc.AddSourceRoot(dir + Path.DirectorySeparatorChar);
            indexing.GetAwaiter().GetResult();
            Assert.Equal(1, svc.SourceRoots.Count);
            Assert.Equal(200, svc.Index!.Files);
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch (IOException) { }
        }
    }
}
