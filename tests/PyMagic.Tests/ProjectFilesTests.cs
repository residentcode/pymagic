using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// What the menus of the project tree and the tabs do to files (item 155): Clean Python Compiled Files leaves virtual environments and .git alone,
/// and Add to .gitignore writes one anchored line, once.
/// </summary>
public class ProjectFilesTests
{
    [Fact]
    public void CleaningRemovesCompiledFilesButNotThoseOfAnEnvironment()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pymagic-clean-" + Guid.NewGuid().ToString("N"));
        try
        {
            void Make(string rel) { string f = Path.Combine(dir, rel); Directory.CreateDirectory(Path.GetDirectoryName(f)!); File.WriteAllText(f, ""); }
            Make("app/__pycache__/a.cpython-312.pyc");
            Make("app/b.py");
            Make("app/old.pyc");
            Make("app/sub/__pycache__/c.cpython-312.pyc");
            Make(".venv/pyvenv.cfg");
            Make(".venv/Lib/__pycache__/os.cpython-312.pyc");
            Make(".git/hooks/x.pyc");
            Assert.Equal(3, ProjectFiles.CleanCompiled(dir));
            Assert.True(!Directory.Exists(Path.Combine(dir, "app", "__pycache__")));
            Assert.True(!File.Exists(Path.Combine(dir, "app", "old.pyc")));
            Assert.True(File.Exists(Path.Combine(dir, "app", "b.py")));
            Assert.True(File.Exists(Path.Combine(dir, ".venv", "Lib", "__pycache__", "os.cpython-312.pyc")));
            Assert.True(File.Exists(Path.Combine(dir, ".git", "hooks", "x.pyc")));
            Assert.Equal(0, ProjectFiles.CleanCompiled(dir));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void AnIgnoreLineIsAnchoredAndAddedOnce()
    {
        Assert.Equal("/build/", ProjectFiles.IgnoreLine("build", true));
        Assert.Equal("/app/local.py", ProjectFiles.IgnoreLine("app/local.py", false));
        Assert.Equal("/a.txt\n", ProjectFiles.WithIgnoreLine("", "/a.txt"));
        Assert.Equal("*.pyc\n/a.txt\n", ProjectFiles.WithIgnoreLine("*.pyc", "/a.txt"));
        Assert.Equal("*.pyc\r\n/a.txt\r\n", ProjectFiles.WithIgnoreLine("*.pyc\r\n", "/a.txt"));
        Assert.True(ProjectFiles.WithIgnoreLine("x\n/a.txt\n", "/a.txt") is null);
        // a path with a slash inside means the same with or without the leading one
        Assert.True(ProjectFiles.WithIgnoreLine("app/local.py\n", "/app/local.py") is null);
        Assert.True(ProjectFiles.WithIgnoreLine("a.txt\n", "/a.txt") is not null);
    }
}
