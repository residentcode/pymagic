using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Mark Directory As (item 156): the marks are kept in .pymagic/folders.json, a folder has one at most, and an excluded folder is left out of the analysis.</summary>
public class ProjectFoldersTests
{
    [Fact]
    public void MarksAreKeptOnePerFolder()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_marks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "src"));
        Directory.CreateDirectory(Path.Combine(dir, "build"));
        try
        {
            ProjectFolders.Mark(dir, "src", "sources");
            ProjectFolders.Mark(dir, "build", "excluded");
            var again = ProjectFolders.Load(dir);
            Assert.Equal("src", string.Join(",", again.Sources));
            Assert.Equal("build", string.Join(",", again.Excluded));
            Assert.True(ProjectFolders.IsExcluded(Path.Combine(dir, "build", "lib")));
            Assert.True(!ProjectFolders.IsExcluded(Path.Combine(dir, "buildtools")));
            // another mark replaces the one it had; "none" takes it away
            var moved = ProjectFolders.Mark(dir, "src", "tests");
            Assert.Equal(0, moved.Sources.Count);
            Assert.Equal("src", string.Join(",", moved.Tests));
            ProjectFolders.Mark(dir, "build", "none");
            Assert.True(!ProjectFolders.IsExcluded(Path.Combine(dir, "build")));
        }
        finally { ProjectFolders.Load(Path.Combine(dir, "gone")); Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnExcludedFolderIsNotAnalysed()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_marks_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(dir, "old"));
        File.WriteAllText(Path.Combine(dir, "old", "legacy.py"), "X = 1\n");
        File.WriteAllText(Path.Combine(dir, "main.py"), "from old.legacy import X\n");
        try
        {
            // the module is part of the project (its import is a usage) until its folder is excluded
            string legacy = LanguageService.UriOfPath(Path.Combine(dir, "old", "legacy.py")), main = LanguageService.UriOfPath(Path.Combine(dir, "main.py"));
            var before = new LanguageService();
            before.AddSourceRoot(dir);
            before.Open(main, File.ReadAllText(Path.Combine(dir, "main.py")), 1);
            Assert.Equal(1, before.FileUsages(legacy).Count);
            ProjectFolders.Mark(dir, "old", "excluded");
            var after = new LanguageService();
            after.AddSourceRoot(dir);
            after.Open(main, File.ReadAllText(Path.Combine(dir, "main.py")), 1);
            Assert.Equal(0, after.FileUsages(legacy).Count);
        }
        finally { ProjectFolders.Load(Path.Combine(dir, "gone")); Directory.Delete(dir, true); }
    }
}
