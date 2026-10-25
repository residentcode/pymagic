using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Find usages and rename read only the project files that mention the name.</summary>
public class FilteredUsagesTests
{
    static (LanguageService Svc, string Dir) Project(Dictionary<string, string> files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_usages_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        foreach (var (name, text) in files) File.WriteAllText(Path.Combine(dir, name), text);
        var svc = new LanguageService();
        svc.SourceRoots.Add(dir);
        return (svc, dir);
    }

    static Position At(string text, string marker, int inside = 1)
    {
        var lines = text.Split('\n');
        int line = Array.FindIndex(lines, l => l.Contains(marker));
        return new Position(line, lines[line].IndexOf(marker, StringComparison.Ordinal) + inside);
    }

    const string A = "def rarely_used_name(x):\n    return x\n";
    const string B = "from a import rarely_used_name\n\n\nrarely_used_name(1)\nrarely_used_name(2)\n";
    const string C = "def something_else():\n    return 3\n\n\nsomething_else()\n";

    [Fact]
    public void UsagesAreFoundInTheFilesThatMentionTheName()
    {
        var (svc, dir) = Project(new() { ["a.py"] = A, ["b.py"] = B, ["c.py"] = C });
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "a.py"));
        svc.Open(uri, A, 1);
        var refs = svc.References(uri, At(A, "rarely_used_name"), true);
        Assert.Equal(4, refs.Count);          // the definition, the import and two calls
        Assert.True(refs.Any(r => r.Uri.EndsWith("b.py")));
        Assert.True(refs.All(r => !r.Uri.EndsWith("c.py")));
    }

    [Fact]
    public void AFileWithoutTheNameIsNeverParsed()
    {
        var files = new Dictionary<string, string> { ["a.py"] = A, ["b.py"] = B };
        for (int i = 0; i < 20; i++) files[$"other{i}.py"] = $"def f{i}():\n    return {i}\n";
        var (svc, dir) = Project(files);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "a.py"));
        svc.Open(uri, A, 1);
        svc.References(uri, At(A, "rarely_used_name"), true);
        Assert.True(svc.Cache.Misses < 10, $"{svc.Cache.Misses} files were parsed for a name that two of 22 files mention");
    }

    [Fact]
    public void RenameStillChangesEveryUsage()
    {
        var (svc, dir) = Project(new() { ["a.py"] = A, ["b.py"] = B, ["c.py"] = C });
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "a.py"));
        svc.Open(uri, A, 1);
        var edit = svc.Rename(uri, At(A, "rarely_used_name"), "renamed_fn");
        Assert.Equal(2, edit.Changes.Count);
        Assert.Equal(4, edit.Changes.Values.Sum(l => l.Count));
    }

    [Fact]
    public void AnEditIsSeenByTheNextSearch()
    {
        var (svc, dir) = Project(new() { ["a.py"] = A, ["b.py"] = B });
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "a.py"));
        svc.Open(uri, A, 1);
        Assert.Equal(4, svc.References(uri, At(A, "rarely_used_name"), true).Count);
        string edited = A + "\n\nrarely_used_name(5)\n";
        svc.Change(uri, edited, 2);
        Assert.Equal(5, svc.References(uri, At(edited, "rarely_used_name"), true).Count);
    }
}
