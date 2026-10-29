using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Moving or renaming a module or package in Explorer (item 132): the imports of the project follow it.</summary>
public class MoveModuleTests
{
    /// <summary>A project on disk with these files; the plan of moving `from` to `to` (paths relative to the project), applied to the texts.</summary>
    static (Dictionary<string, string> After, ModuleMovePlan Plan) Move((string Rel, string Text)[] files, string from, string to)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_movemod_" + Guid.NewGuid().ToString("N"));
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        var texts = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (rel, text) in files)
        {
            string full = Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
            svc.Open(LanguageService.UriOfPath(full), text, 1);
            texts[rel] = text;
        }
        string Full(string rel) => Path.Combine(dir, rel.Replace('/', Path.DirectorySeparatorChar));
        var plan = svc.PlanModuleMove(LanguageService.UriOfPath(Full(from)), LanguageService.UriOfPath(Full(to)));
        foreach (var (uri, edits) in plan.Changes)
        {
            string rel = Path.GetRelativePath(dir, new Uri(uri).LocalPath).Replace('\\', '/');
            var lines = new LineMap(texts[rel]);
            string text = texts[rel];
            foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
                text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
            texts[rel] = text;
        }
        return (texts, plan);
    }

    static void Starts(string expected, string actual) => Assert.Equal(expected, actual[..Math.Min(expected.Length, actual.Length)]);

    static readonly (string, string)[] Project =
    {
        ("pkg/__init__.py", "from .b import VALUE\n"),
        ("pkg/a.py", "from . import b\n\n\ndef greet():\n    return b.VALUE\n"),
        ("pkg/b.py", "from .a import greet\nfrom . import a\n\nVALUE = 1\n"),
        ("other/__init__.py", ""),
        ("main.py",
            "import pkg.b\nimport pkg.b as pb\nfrom pkg.b import VALUE\nfrom pkg import a, b\nfrom unittest import mock\n\n" +
            "print(pkg.b.VALUE, pb.VALUE, VALUE, a, b)\nmock.patch('pkg.b.VALUE')\n"),
    };

    [Fact]
    public void AFileMovedToAnotherPackage()
    {
        var (after, _) = Move(Project, "pkg/b.py", "other/b.py");
        Assert.Equal(
            "import other.b\nimport other.b as pb\nfrom other.b import VALUE\nfrom pkg import a\nfrom other import b\nfrom unittest import mock\n\n" +
            "print(other.b.VALUE, pb.VALUE, VALUE, a, b)\nmock.patch('other.b.VALUE')\n", after["main.py"]);
        Assert.Equal("from other.b import VALUE\n", after["pkg/__init__.py"]);
        Assert.Equal("from other import b\n\n\ndef greet():\n    return b.VALUE\n", after["pkg/a.py"]);
    }

    [Fact]
    public void TheMovedFilesOwnRelativeImportsKeepPointingWhereTheyDid()
    {
        var (after, _) = Move(Project, "pkg/b.py", "other/b.py");
        Assert.Equal("from pkg.a import greet\nfrom pkg import a\n\nVALUE = 1\n", after["pkg/b.py"]);
    }

    [Fact]
    public void ARenamedModuleKeepsTheNameItIsUsedBy()
    {
        var (after, _) = Move(Project, "pkg/b.py", "pkg/c.py");
        Starts("import pkg.c\nimport pkg.c as pb\nfrom pkg.c import VALUE\nfrom pkg import a\nfrom pkg import c as b\n", after["main.py"]);
        Assert.Contains("print(pkg.c.VALUE", after["main.py"]);
        // a relative import stays relative
        Assert.Equal("from .c import VALUE\n", after["pkg/__init__.py"]);
        Assert.Equal("from . import c as b\n\n\ndef greet():\n    return b.VALUE\n", after["pkg/a.py"]);
        // the module's own relative imports still reach the same package
        Assert.Equal("from .a import greet\nfrom . import a\n\nVALUE = 1\n", after["pkg/b.py"]);
    }

    [Fact]
    public void AFolderMovedIntoAnother()
    {
        var files = Project.Append(("lib/__init__.py", "")).Append(("app.py", "import pkg\n\nprint(pkg.VALUE)\n")).ToArray();
        var (after, plan) = Move(files, "pkg", "lib/pkg");
        Assert.Equal("import lib.pkg as pkg\n\nprint(pkg.VALUE)\n", after["app.py"]);
        Starts("import lib.pkg.b\nimport lib.pkg.b as pb\nfrom lib.pkg.b import VALUE\nfrom lib.pkg import a, b\n", after["main.py"]);
        Assert.Contains("print(lib.pkg.b.VALUE", after["main.py"]);
        // the imports inside the folder move with it and need nothing
        Assert.Equal("from .b import VALUE\n", after["pkg/__init__.py"]);
        Assert.Equal("from .a import greet\nfrom . import a\n\nVALUE = 1\n", after["pkg/b.py"]);
        Assert.Equal("pkg", plan.From);
        Assert.Equal("lib.pkg", plan.To);
    }

    [Fact]
    public void AModuleMovedToTheTopOfTheProject()
    {
        var (after, _) = Move(Project, "pkg/b.py", "b.py");
        Starts("import b\nimport b as pb\nfrom b import VALUE\nfrom pkg import a\nimport b\n", after["main.py"]);
        Assert.Contains("print(b.VALUE", after["main.py"]);
        Assert.Equal("from b import VALUE\n", after["pkg/__init__.py"]);
    }

    [Fact]
    public void ANameThatCannotBeImportedIsAWarningAndChangesNothing()
    {
        var (_, plan) = Move(Project, "pkg/b.py", "my-dir/b.py");
        Assert.Equal(0, plan.Changes.Count);
        Assert.Contains("would not be importable", plan.Warnings.Single());
    }

    [Fact]
    public void NothingToDoForAFileThatIsNotPython()
    {
        var (_, plan) = Move(Project.Append(("notes/readme.md", "# hi\n")).ToArray(), "notes", "other/notes");
        Assert.Equal("", string.Join("|", plan.Changes.Keys) + string.Join("|", plan.Warnings));
    }
}
