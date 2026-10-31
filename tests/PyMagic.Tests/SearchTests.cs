using PyMagic.Server;

namespace PyMagic.Tests;

public class SearchTests
{
    static string MakeProject(params (string Path, string Text)[] files)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_search_" + Guid.NewGuid().ToString("N"));
        foreach (var (p, t) in files)
        {
            string full = Path.Combine(dir, p);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, t);
        }
        Directory.CreateDirectory(dir);
        return dir;
    }

    static T With<T>(string dir, Func<string, T> body)
    {
        try { return body(dir); }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void FindsPlainTextWithPositions()
    {
        var r = With(MakeProject(("a.py", "x = 1\nprint(total)\n"), ("pkg/b.py", "def total(): pass\n# Total again\n")),
            d => WorkspaceSearch.Run(d, new SearchOptions("total")));
        Assert.Equal(3, r.Total);
        Assert.Equal("a.py", r.Files[0].Path);
        Assert.Equal(1, r.Files[0].Matches[0].Line);
        Assert.Equal(6, r.Files[0].Matches[0].Column);
        Assert.Equal("pkg/b.py", r.Files[1].Path);
        Assert.Equal(2, r.Files[1].Matches.Count);
    }

    [Fact]
    public void CaseWordAndRegexOptions()
    {
        string dir = MakeProject(("a.py", "Total total totals subtotal\nx12 x345\n"));
        var caseSensitive = WorkspaceSearch.Run(dir, new SearchOptions("total", CaseSensitive: true));
        var word = WorkspaceSearch.Run(dir, new SearchOptions("total", WholeWord: true));
        var regex = WorkspaceSearch.Run(dir, new SearchOptions(@"x\d{3}", Regex: true));
        Directory.Delete(dir, true);
        Assert.Equal(3, caseSensitive.Total);   // total, totals, subtotal
        Assert.Equal(2, word.Total);            // Total, total
        Assert.Equal(1, regex.Total);
        Assert.Equal(4, regex.Files[0].Matches[0].Column);
    }

    [Fact]
    public void AnInvalidRegexIsExplained()
    {
        string dir = MakeProject(("a.py", "x\n"));
        try { WorkspaceSearch.Run(dir, new SearchOptions("(", Regex: true)); }
        catch (ServiceException e) { Assert.Contains("regular expression", e.Message); return; }
        finally { Directory.Delete(dir, true); }
        throw new AssertionException("expected an error");
    }

    [Fact]
    public void IncludeAndExcludeGlobs()
    {
        string dir = MakeProject(("a.py", "needle\n"), ("src/b.py", "needle\n"), ("tests/test_b.py", "needle\n"), ("notes.md", "needle\n"));
        var py = WorkspaceSearch.Run(dir, new SearchOptions("needle", Include: "*.py"));
        var src = WorkspaceSearch.Run(dir, new SearchOptions("needle", Include: "src"));
        var noTests = WorkspaceSearch.Run(dir, new SearchOptions("needle", Exclude: "tests/**, *.md"));
        Directory.Delete(dir, true);
        Assert.Equal("a.py,src/b.py,tests/test_b.py", string.Join(",", py.Files.Select(f => f.Path)));
        Assert.Equal("src/b.py", string.Join(",", src.Files.Select(f => f.Path)));
        Assert.Equal("a.py,src/b.py", string.Join(",", noTests.Files.Select(f => f.Path)));
    }

    [Fact]
    public void SkipsEnvironmentsBinariesAndHugeResults()
    {
        string dir = MakeProject((".venv/lib/x.py", "needle\n"), ("__pycache__/a.py", "needle\n"), ("a.py", "needle\n"));
        File.WriteAllBytes(Path.Combine(dir, "blob.txt"), new byte[] { 110, 101, 101, 100, 108, 101, 0, 1 });
        File.WriteAllText(Path.Combine(dir, "many.py"), string.Concat(Enumerable.Repeat("needle\n", 50)));
        var all = WorkspaceSearch.Run(dir, new SearchOptions("needle"));
        var capped = WorkspaceSearch.Run(dir, new SearchOptions("needle", MaxResults: 10));
        Directory.Delete(dir, true);
        Assert.Equal(51, all.Total);
        Assert.Equal("a.py,many.py", string.Join(",", all.Files.Select(f => f.Path)));
        Assert.True(capped.Truncated && capped.Total == 10, "truncated at the limit");
    }

    [Fact]
    public void UnsavedTextTakesThePlaceOfTheFile()
    {
        string dir = MakeProject(("a.py", "old\n"));
        var r = WorkspaceSearch.Run(dir, new SearchOptions("new"), new Dictionary<string, string> { ["a.py"] = "new\n", ["fresh.py"] = "a new file\n" });
        Directory.Delete(dir, true);
        Assert.Equal("a.py,fresh.py", string.Join(",", r.Files.Select(f => f.Path)));
    }

    [Fact]
    public void ReplacementsExpandGroupsOnlyForRegex()
    {
        string dir = MakeProject(("a.py", "get_user(1)\nprice$1\n"));
        var regex = WorkspaceSearch.Run(dir, new SearchOptions(@"get_(\w+)", Regex: true, Replace: "fetch_$1"));
        var literal = WorkspaceSearch.Run(dir, new SearchOptions("price$1", Replace: "cost$1"));
        Directory.Delete(dir, true);
        Assert.Equal("fetch_user", regex.Files[0].Matches[0].Replacement!);
        Assert.Equal("cost$1", literal.Files[0].Matches[0].Replacement!);
    }

    [Fact]
    public void HandlesAllLineEndings()
    {
        string dir = MakeProject(("a.py", "one\r\ntwo needle\rthree\nneedle\n"));
        var r = WorkspaceSearch.Run(dir, new SearchOptions("needle"));
        Directory.Delete(dir, true);
        Assert.Equal("1,3", string.Join(",", r.Files[0].Matches.Select(m => m.Line)));
    }

    [Fact]
    public void FindsSymbolsAcrossTheOpenFiles()
    {
        var svc = new LanguageService();
        svc.Open("file:///c:/proj/shop.py", "class Cart:\n    def add_item(self): pass\n\ndef total(): pass\nLIMIT = 3\n", 1);
        svc.Open("file:///c:/proj/util.py", "def totally_other(): pass\n", 1);
        var names = svc.WorkspaceSymbols("tot").Select(s => s.Name).ToList();
        Assert.Equal("total,totally_other", string.Join(",", names));
        var fuzzy = svc.WorkspaceSymbols("addi").Select(s => s.ContainerName + "/" + s.Name).ToList();
        Assert.Equal("shop.Cart/add_item", string.Join(",", fuzzy));
        Assert.Equal(5, svc.WorkspaceSymbols("").Count);
    }

    [Fact]
    public void APatternOverSeveralLines()
    {
        // item 212: `\n` in a regex, or text with a line break, matches across lines, whatever line breaks the file has
        var r = With(MakeProject(("a.py", "try:\r\n    run()\r\nexcept:\r\n    pass\r\n"), ("b.py", "try:\n    go()\nexcept ValueError:\n    pass\n")),
            d => WorkspaceSearch.Run(d, new SearchOptions(@"except:\n\s+pass", Regex: true, Replace: "except Exception:\n    pass")));
        Assert.Equal(1, r.Total);
        var m = r.Files[0].Matches[0];
        Assert.Equal("a.py", r.Files[0].Path);
        Assert.Equal(2, m.Line);
        Assert.Equal(0, m.Column);
        Assert.Equal(7, m.Length);              // what the match covers of its first line
        Assert.Equal(3, m.EndLine);
        Assert.Equal(8, m.EndColumn);
        Assert.Equal("except:", m.Text);
        var plain = With(MakeProject(("c.py", "x = 1\ny = 2\nz = 3\n")), d => WorkspaceSearch.Run(d, new SearchOptions("x = 1\ny")));
        Assert.Equal(1, plain.Total);
        Assert.Equal(1, plain.Files[0].Matches[0].EndLine);
        Assert.Equal(1, plain.Files[0].Matches[0].EndColumn);
        // a match on one line has no end of its own
        Assert.True(With(MakeProject(("d.py", "a = 1\n")), d => WorkspaceSearch.Run(d, new SearchOptions(@"a\s=", Regex: true))).Files[0].Matches[0].EndLine == null);
    }

    [Fact]
    public void LibraryFilesAreSearchedWhenAsked()
    {
        string lib = MakeProject(("requests/api.py", "def get(url):\n    return request('get', url)\n"), ("requests/__pycache__/api.cpython-314.pyc", "request"), ("requests/README.md", "request"));
        try
        {
            var project = MakeProject(("main.py", "import requests\nrequests.get('x')\n"));
            var only = With(project, d => WorkspaceSearch.Run(d, new SearchOptions("request"), libraryRoots: new[] { lib }));
            Assert.True(only.Files.All(f => !f.Library));
            project = MakeProject(("main.py", "import requests\nrequests.get('x')\n"));
            var both = With(project, d => WorkspaceSearch.Run(d, new SearchOptions("request", Libraries: true), libraryRoots: new[] { lib }));
            var libFile = both.Files.Single(f => f.Library);
            Assert.True(libFile.Path.EndsWith("requests/api.py"));   // a full path, the library's Python files only
            Assert.True(Path.IsPathRooted(libFile.Path));
            Assert.Equal(1, libFile.Matches.Count);
        }
        finally { Directory.Delete(lib, true); }
    }
}
