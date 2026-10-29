using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// pytest's options and fixture scopes (item 151). The project is Projects/pytest, the one pytest 9.1.1 ran: `addopts = -p helpers.fixtures -p no:anyio`,
/// a plugin that loads another with pytest_plugins, a session fixture asking for tmp_path (ScopeMismatch), scope="sesion" (collecting stops) and
/// fixtures with params. The checks that need pytest's own classes use the installed pytest (skipped where it is not).
/// </summary>
public class PytestOptionsTests
{
    static readonly string? Site = new[] { Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages") }
        .FirstOrDefault(p => File.Exists(Path.Combine(p, "_pytest", "fixtures.py")) && File.Exists(Path.Combine(p, "anyio", "pytest_plugin.py")));

    sealed record Rig(LanguageService Svc, string Dir)
    {
        public string Uri(string file) => LanguageService.UriOfPath(Path.Combine(Dir, file));
        public string Text(string file) => File.ReadAllText(Path.Combine(Dir, file));
        public List<LspDiagnostic> Problems(string file, string code) => Svc.Diagnostics(Uri(file)).Where(d => d.Code == code).OrderBy(d => d.Range.Start.Line).ToList();
        public string Hover(string file, string needle, int delta = 1)
        {
            string text = Text(file);
            int at = text.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) throw new Exception("not in " + file + ": " + needle);
            return (Svc.Hover(Uri(file), new LineMap(text).PositionOf(at + delta))?.Contents.Value ?? "(nothing)").Replace("\n", " ");
        }
    }

    /// <summary>A copy of Projects/pytest, every .py file open.</summary>
    static Rig Open()
    {
        string from = Path.Combine(AppContext.BaseDirectory, "Projects", "pytest");
        string dir = Path.Combine(Path.GetTempPath(), "pm_pytestopts_" + Guid.NewGuid().ToString("N"));
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        if (Site != null) svc.LibraryRoots.Add(Site);
        foreach (var file in Directory.EnumerateFiles(from, "*", SearchOption.AllDirectories))
        {
            string to = Path.Combine(dir, Path.GetRelativePath(from, file));
            Directory.CreateDirectory(Path.GetDirectoryName(to)!);
            File.Copy(file, to);
        }
        foreach (var file in Directory.EnumerateFiles(dir, "*.py", SearchOption.AllDirectories)) svc.Open(LanguageService.UriOfPath(file), File.ReadAllText(file), 1);
        return new Rig(svc, dir);
    }

    // ------------------------------------------------------------ -p in the configuration

    [Fact]
    public void TheOptionsOfEachConfigurationFileAreRead()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_addopts_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pyproject.toml"), "[project]\nname = 'x'\n\n[tool.pytest.ini_options]\naddopts = [\n  \"-p\", \"one.plugin\",\n  \"-ptwo\",\n]\n\n[tool.other]\naddopts = \"-p nope\"\n");
        var (enabled, disabled) = LanguageService.PytestOptionsIn(dir)!.Value;
        Assert.Equal("one.plugin,two", string.Join(",", enabled));
        Assert.Equal(0, disabled.Count);
        // pytest.ini comes first, and its value may go on over indented lines
        File.WriteAllText(Path.Combine(dir, "pytest.ini"), "[pytest]\naddopts =\n    -q\n    -p no:anyio -p  three\ntestpaths = tests\n");
        (enabled, disabled) = LanguageService.PytestOptionsIn(dir)!.Value;
        Assert.Equal("three", string.Join(",", enabled));
        Assert.Equal("anyio", string.Join(",", disabled));
        Assert.True(LanguageService.PytestOptionsIn(Path.Combine(dir, "missing")) == null);
    }

    [Fact]
    public void APluginNamedWithPAndThePluginsItLoadsBringTheirFixtures()
    {
        var r = Open();
        Assert.Equal(0, r.Problems("test_p.py", "fixture-not-found").Count(d => d.Message.Contains("shared") || d.Message.Contains("nested")));
        Assert.Contains("helpers.fixtures", r.Hover("test_p.py", "shared,"));
        Assert.Contains("helpers.more", r.Hover("test_p.py", "nested)"));
    }

    [Fact]
    public void APluginSwitchedOffWithNoBringsNothing()
    {
        if (Site == null) return;
        var r = Open();
        // anyio registers anyio.pytest_plugin; -p no:anyio switches it off, so anyio_backend is not its fixture here
        Assert.True(!r.Hover("test_p.py", "anyio_backend").Contains("anyio.pytest_plugin"), r.Hover("test_p.py", "anyio_backend"));
    }

    // ------------------------------------------------------------ scopes

    [Fact]
    public void ANarrowerFixtureAskedForByABroaderOneIsAScopeMismatch()
    {
        var r = Open();
        var found = r.Problems("test_scope.py", "fixture-scope");
        // db (session) asks for tmp_path (function); conn (module) asking for db (session), and fine asking for tmp_path_factory and request, are right
        Assert.Equal(1, found.Count);
        Assert.Equal(5, found[0].Range.Start.Line + 1);
        Assert.True(found[0].Message.StartsWith("'db' is a session fixture and 'tmp_path' a function one: a test that uses 'db' fails with ScopeMismatch", StringComparison.Ordinal), found[0].Message);
        Assert.Contains("Ask for tmp_path_factory instead", found[0].Message);
        Assert.Equal(Severity.Error, found[0].Severity);
    }

    [Fact]
    public void AScopePytestDoesNotKnowStopsCollecting()
    {
        var r = Open();
        var found = r.Problems("test_typo.py", "fixture-scope");
        Assert.Equal(1, found.Count);
        Assert.True(found[0].Message.StartsWith("pytest has no scope 'sesion' (did you mean 'session'?)", StringComparison.Ordinal), found[0].Message);
    }

    [Fact]
    public void AParameterThatAsksForAFixtureDoesNotShadowIt()
    {
        var r = Open();
        // db, conn and fine are asked for by name, as pytest wants; helper's LIMIT really shadows the module's LIMIT
        var found = r.Problems("test_scope.py", "shadowing-names");
        Assert.Equal("Shadows name 'LIMIT' from outer scope", string.Join("\n", found.Select(d => d.Message)));
    }

    // ------------------------------------------------------------ request.param

    [Fact]
    public void RequestParamIsOneOfTheParams()
    {
        if (Site == null) return;
        var r = Open();
        Assert.Contains("param: int", r.Hover("test_param.py", "request.param\n", 9));
        string mixed = r.Hover("test_param.py", "type(request.param)", 14);
        Assert.Contains("param: str | tuple[str, str]", mixed);
        Assert.Equal("", string.Join("\n", r.Svc.Diagnostics(r.Uri("test_param.py")).Where(d => d.Severity <= 2).Select(d => d.Message)));
    }
}
