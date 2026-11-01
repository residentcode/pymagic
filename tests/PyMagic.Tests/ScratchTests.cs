using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Scratch files (item 238): kept in a folder of their own, reached by a made-up path, never in the project.</summary>
public class ScratchTests
{
    sealed class Folder : IDisposable
    {
        public readonly string Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pm_scratch_" + Guid.NewGuid().ToString("N"));
        readonly string? _was = Environment.GetEnvironmentVariable("PYMAGIC_SCRATCHES");
        public Folder() { Environment.SetEnvironmentVariable("PYMAGIC_SCRATCHES", Path); }
        public void Dispose()
        {
            Environment.SetEnvironmentVariable("PYMAGIC_SCRATCHES", _was);
            try { Directory.Delete(Path, true); } catch (IOException) { }
        }
    }

    [Fact]
    public void AScratchIsMadeWithAStarterAndANameTakenGetsANumber()
    {
        using var f = new Folder();
        Assert.Equal("scratch.py", Scratches.Create("scratch.py"));
        Assert.Equal("# scratch\n", File.ReadAllText(System.IO.Path.Combine(f.Path, "scratch.py")));
        Assert.Equal("scratch_1.py", Scratches.Create("scratch.py"));
        Assert.Equal("scratch_2.py", Scratches.Create("scratch.py", "print(1)\n"));
        Assert.Equal("print(1)\n", File.ReadAllText(System.IO.Path.Combine(f.Path, "scratch_2.py")));
        Assert.Equal("{\n}\n", File.ReadAllText(System.IO.Path.Combine(f.Path, Scratches.Create("data.json"))));
        Assert.Equal("### scratch\nGET https://example.org/\n", File.ReadAllText(System.IO.Path.Combine(f.Path, Scratches.Create("r.http"))));
        Assert.Equal("", File.ReadAllText(System.IO.Path.Combine(f.Path, Scratches.Create("notes.xyz"))));
        Assert.Equal(6, Scratches.List().Count);
    }

    [Fact]
    public void ANameIsNeverAPath()
    {
        using var f = new Folder();
        foreach (var bad in new[] { "../x.py", "a/b.py", "..", "", "a:b.py", "x\\y.py" })
            Assert.Throws<UnauthorizedAccessException>(() => Scratches.Create(bad));
        Assert.Equal(0, Scratches.List().Count);
        // the made-up folder is stripped, nothing else is
        Assert.Equal(System.IO.Path.Combine(f.Path, "a.py"), Scratches.PathOf(".scratches/a.py"));
        Assert.Throws<UnauthorizedAccessException>(() => Scratches.PathOf(".scratches/../a.py"));
        Assert.True(Scratches.IsScratchPath(".scratches/a.py") && Scratches.IsScratchPath(".SCRATCHES\\a.py"));
        Assert.True(!Scratches.IsScratchPath("src/.scratches/a.py") && !Scratches.IsScratchPath("scratches/a.py"));
    }

    [Fact]
    public void AScratchIsRenamedAndDeleted()
    {
        using var f = new Folder();
        Scratches.Create("a.py");
        Scratches.Create("b.py");
        Assert.True(!Scratches.Rename("a.py", "b.py"), "the new name is taken");
        Assert.True(!Scratches.Rename("nope.py", "c.py"));
        Assert.True(Scratches.Rename("a.py", "c.py"));
        Assert.Equal("b.py,c.py", string.Join(",", Scratches.List().Select(s => s.Name).OrderBy(n => n)));
        Assert.True(Scratches.Delete("c.py"));
        Assert.True(!Scratches.Delete("c.py"));
        Assert.Equal(1, Scratches.List().Count);
    }

    [Fact]
    public void AScratchOfTheProjectsFolderIsNotInTheProject()
    {
        using var f = new Folder();
        string project = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "pm_scratch_proj_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(project);
        try
        {
            Scratches.Create("only_here.py");
            Assert.Equal(0, Directory.GetFiles(project, "*", SearchOption.AllDirectories).Length);
            Assert.True(File.Exists(System.IO.Path.Combine(f.Path, "only_here.py")));
        }
        finally { Directory.Delete(project, true); }
    }
}
