using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Find Usages of a file in the project tree (item 156): the imports of the module and the strings that name it, the places a rename of it would change.</summary>
public class FileUsagesTests
{
    static List<(string Rel, int Line)> Usages((string Rel, string Text)[] files, string of)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_fileuses_" + Guid.NewGuid().ToString("N"));
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            svc.Open(LanguageService.UriOfPath(full), text, 1);
        }
        var found = svc.FileUsages(LanguageService.UriOfPath(Path.Combine(dir, of.Replace('/', Path.DirectorySeparatorChar))));
        return found.Select(l => (Path.GetRelativePath(dir, new Uri(l.Uri).LocalPath).Replace('\\', '/'), l.Range.Start.Line)).ToList();
    }

    static readonly (string, string)[] Project =
    {
        ("pkg/__init__.py", ""),
        ("pkg/a.py", "A = 1\n"),
        ("pkg/b.py", "V = 2\n"),
        ("main.py", "import pkg.b\nfrom pkg.b import V\nfrom pkg import b\nfrom unittest import mock\n\nprint(pkg.b.V, V, b)\nmock.patch('pkg.b.V')\n"),
        ("other.py", "from pkg import a\nprint(a.A)\n"),
    };

    [Fact]
    public void TheImportsAndStringsOfAModuleAreItsUsages()
    {
        var found = Usages(Project, "pkg/b.py");
        Assert.True(found.All(u => u.Rel == "main.py"), string.Join(", ", found));
        // each import statement, the dotted use and the string
        foreach (int line in new[] { 0, 1, 2, 5, 6 }) Assert.True(found.Any(u => u.Line == line), $"line {line}: {string.Join(", ", found)}");
    }

    [Fact]
    public void AModuleNothingImportsHasNone()
    {
        Assert.Equal(0, Usages(Project, "main.py").Count);
    }

    [Fact]
    public void APackageIsUsedWhereverItsModulesAre()
    {
        var found = Usages(Project, "pkg");
        Assert.True(found.Any(u => u.Rel == "main.py") && found.Any(u => u.Rel == "other.py"), string.Join(", ", found));
    }
}
