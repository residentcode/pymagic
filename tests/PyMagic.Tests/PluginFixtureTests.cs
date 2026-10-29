using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Fixtures of installed pytest plugins (item 134): read from the `pytest11` entry points of the packages, and from `pytest_plugins`.</summary>
public class PluginFixtureTests
{
    const string Plugin =
        "import pytest\n\nfrom .fixtures import thing\n\n\n@pytest.fixture\ndef widget() -> int:\n    \"\"\"A widget for every test.\"\"\"\n    return 1\n\n\n" +
        "@pytest.fixture(name='gadget')\ndef _gadget() -> str:\n    return 'x'\n";

    sealed record Rig(LanguageService Svc, string Dir, string Test);

    /// <summary>A library folder with the plugin package `myplug` (its entry point says `myplug = myplug.plugin`) and a project with one test file.</summary>
    static Rig Make(string test, (string Rel, string Text)[]? project = null, bool registered = true)
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_plugfx_" + Guid.NewGuid().ToString("N"));
        string lib = Path.Combine(dir, "lib"), proj = Path.Combine(dir, "proj");
        void Put(string root, string rel, string text)
        {
            string full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllText(full, text);
        }
        Put(lib, "pytest/__init__.py", "def fixture(*args, **kwargs): ...\n");
        Put(lib, "myplug/__init__.py", "");
        Put(lib, "myplug/plugin.py", Plugin);
        Put(lib, "myplug/fixtures.py", "import pytest\n\n\n@pytest.fixture\ndef thing():\n    return [1]\n");
        if (registered) Put(lib, "myplug-1.0.dist-info/entry_points.txt", "[console_scripts]\nmy = myplug:main\n\n[pytest11]\nmyplug = myplug.plugin\n");
        Put(proj, "pytest.ini", "[pytest]\n");
        var svc = new LanguageService();
        svc.AddSourceRoot(proj);
        svc.LibraryRoots.Add(lib);
        foreach (var (rel, text) in project ?? Array.Empty<(string, string)>())
        {
            Put(proj, rel, text);
            svc.Open(LanguageService.UriOfPath(Path.Combine(proj, rel.Replace('/', Path.DirectorySeparatorChar))), text, 1);
        }
        Put(proj, "tests/test_a.py", test);
        string uri = LanguageService.UriOfPath(Path.Combine(proj, "tests", "test_a.py"));
        svc.Open(uri, test, 1);
        return new Rig(svc, dir, uri);
    }

    static List<string> NotFound(Rig r) => r.Svc.Diagnostics(r.Test).Where(d => d.Code == "fixture-not-found").Select(d => d.Message).ToList();

    static Position At(string text, string marker) => new LineMap(text).PositionOf(text.IndexOf(marker, StringComparison.Ordinal) + 1);

    const string Test = "import pytest\n\n\ndef test_it(widget, gadget, thing, nothing):\n    assert widget\n";

    [Fact]
    public void TheFixturesOfAnInstalledPluginAreFound()
    {
        Assert.Equal("Fixture 'nothing' not found", string.Join("|", NotFound(Make(Test))));
    }

    [Fact]
    public void WithoutItsEntryPointThePluginIsNotLoaded()
    {
        Assert.Equal(4, NotFound(Make(Test, registered: false)).Count);
    }

    [Fact]
    public void HoverSaysWhichPluginAndGivesTheType()
    {
        var r = Make(Test);
        string hover = r.Svc.Hover(r.Test, At(Test, "widget,"))!.Contents.Value;
        Assert.Contains("(fixture) widget: int", hover);
        Assert.Contains("from the pytest plugin `myplug.plugin`", hover);
        Assert.Contains("A widget for every test.", hover);
        // brought by an import in the plugin module
        Assert.Contains("from the pytest plugin `myplug.plugin`", r.Svc.Hover(r.Test, At(Test, "thing,"))!.Contents.Value);
    }

    [Fact]
    public void GoToDefinitionOpensThePlugin()
    {
        var r = Make(Test);
        var loc = r.Svc.Definition(r.Test, At(Test, "gadget")).Single();
        Assert.True(loc.Uri.EndsWith("myplug/plugin.py", StringComparison.Ordinal), loc.Uri);
    }

    [Fact]
    public void TheParameterHasTheTypeAndCompletionOffersThem()
    {
        string test = "def test_it(widget, gad):\n    pass\n";
        var r = Make(test);
        var labels = r.Svc.Completion(r.Test, new LineMap(test).PositionOf(test.IndexOf("gad", StringComparison.Ordinal) + 3)).Select(c => c.Label).ToList();
        Assert.True(labels.Contains("gadget"), string.Join(",", labels));
    }

    [Fact]
    public void ThePluginsAConftestNamesAreRead()
    {
        var project = new[]
        {
            ("tests/conftest.py", "pytest_plugins = ['helpers.fixtures']\n"),
            ("helpers/__init__.py", ""),
            ("helpers/fixtures.py", "import pytest\n\n\n@pytest.fixture\ndef helper_fx():\n    return 1\n"),
        };
        var r = Make("def test_it(helper_fx, widget, nothing):\n    pass\n", project, registered: false);
        // pytest_plugins used to silence the whole file; now the plugin is read and only what nobody provides is reported
        Assert.Equal("Fixture 'widget' not found|Fixture 'nothing' not found", string.Join("|", NotFound(r)));
    }

    [Fact]
    public void APluginThatCannotBeFoundStillSilencesTheFile()
    {
        var project = new[] { ("tests/conftest.py", "pytest_plugins = ['not_installed.plugin']\n") };
        Assert.Equal(0, NotFound(Make("def test_it(nothing):\n    pass\n", project, registered: false)).Count);
    }

    [Fact]
    public void EntryPointsAreReadFromTheirSection()
    {
        string text = "[console_scripts]\nx = a:b\n\n[pytest11]\nanyio = anyio.pytest_plugin\nfaker=faker.contrib.pytest.plugin\n# comment\nbad = not a module\ncolon = pkg.mod:attr\n\n[other]\ny = z\n";
        Assert.Equal("anyio.pytest_plugin,faker.contrib.pytest.plugin,pkg.mod", string.Join(",", LanguageService.PytestEntryPoints(text)));
    }

    // ------------------------------------------------------------ the plugins installed on this machine

    static readonly string? Unsloth = new[] { Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages") }
        .FirstOrDefault(p => File.Exists(Path.Combine(p, "anyio", "pytest_plugin.py")) && File.Exists(Path.Combine(p, "faker", "contrib", "pytest", "plugin.py")));

    /// <summary>anyio and Faker register `anyio.pytest_plugin` and `faker.contrib.pytest.plugin`; their fixtures are found there. Skipped where they are not installed.</summary>
    [Fact]
    public void TheRealAnyioAndFakerPlugins()
    {
        if (Unsloth == null) return;
        string dir = Path.Combine(Path.GetTempPath(), "pm_plugfx_real_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "pytest.ini"), "[pytest]\n");
        string test = "import pytest\n\n\ndef test_it(faker, anyio_backend, free_tcp_port, free_udp_port_factory):\n    assert free_tcp_port\n";
        File.WriteAllText(Path.Combine(dir, "test_real.py"), test);
        var svc = new LanguageService();
        svc.AddSourceRoot(dir);
        svc.LibraryRoots.Add(Unsloth);
        string uri = LanguageService.UriOfPath(Path.Combine(dir, "test_real.py"));
        svc.Open(uri, test, 1);
        foreach (var (name, plugin) in new[] { ("faker,", "faker.contrib.pytest.plugin"), ("anyio_backend", "anyio.pytest_plugin"), ("free_tcp_port,", "anyio.pytest_plugin") })
            Assert.Contains($"from the pytest plugin `{plugin}`", svc.Hover(uri, At(test, name))?.Contents.Value ?? "(no hover)");
        Assert.Contains("(fixture) free_tcp_port: int", svc.Hover(uri, At(test, "free_tcp_port,"))!.Contents.Value);
        Assert.Equal(0, svc.Diagnostics(uri).Count(d => d.Code == "fixture-not-found"));
    }
}
