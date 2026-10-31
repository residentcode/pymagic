using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>pytest (item 209): request.param of an indirect parametrize, the arguments pytest_generate_tests gives, fixtures of plugin objects registered at run time, and the list of fixtures.</summary>
public class PytestHooksTests
{
    const string Root = "file:///c:/proj/";
    const string Conftest = Root + "conftest.py";
    const string TestA = Root + "test_a.py";

    static readonly string? Site = new[] { Path.Combine(Environment.GetEnvironmentVariable("USERPROFILE") ?? "", ".unsloth", "studio", "unsloth_studio", "Lib", "site-packages") }
        .FirstOrDefault(p => File.Exists(Path.Combine(p, "_pytest", "fixtures.py")));

    static LanguageService Project(params (string Uri, string Text)[] files)
    {
        var svc = new LanguageService();
        // the real pytest, where it is installed: what request is comes from it
        if (Site != null) svc.LibraryRoots.Add(Site);
        foreach (var (u, t) in files) svc.Open(u, t, 1);
        return svc;
    }

    static string HoverAt(LanguageService svc, string uri, string text, string needle, int delta = 1) =>
        svc.Hover(uri, new LineMap(text).PositionOf(text.IndexOf(needle, StringComparison.Ordinal) + delta))?.Contents.Value ?? "(nothing)";

    const string ConftestText =
        "import pytest\n\n\n@pytest.fixture\ndef db(request):\n    kind = request.param\n    return kind\n\n\n" +
        "def pytest_generate_tests(metafunc):\n    if 'number' in metafunc.fixturenames:\n        metafunc.parametrize('number', [1, 2, 3])\n\n\n" +
        "class Helpers:\n    @pytest.fixture\n    def helper(self):\n        return 'h'\n\n\n" +
        "def pytest_configure(config):\n    config.pluginmanager.register(Helpers(), 'helpers')\n";

    const string TestText =
        "import pytest\n\n\n@pytest.mark.parametrize('db', ['sqlite', 'postgres'], indirect=True)\ndef test_db(db):\n    assert db\n\n\n" +
        "@pytest.mark.parametrize('store, size', [('redis', 1)], indirect=['store'])\ndef test_store(store, size):\n    assert store\n\n\n" +
        "def test_number(number, helper, missing):\n    total = number\n    word = helper\n    assert total and word\n";

    [Fact]
    public void RequestParamOfAnIndirectParametrize()
    {
        var svc = Project((Conftest, ConftestText), (TestA, TestText));
        var hover = HoverAt(svc, Conftest, ConftestText, "kind = ");
        Assert.True(Site == null || hover.Contains("str"), hover);
        // the test still asks for the fixture: it leads to it, and an indirect name no fixture has is not found (pytest: "function uses no fixture 'store'")
        var to = svc.Definition(TestA, new LineMap(TestText).PositionOf(TestText.IndexOf("def test_db(db", StringComparison.Ordinal) + 12));
        Assert.True(to.Count == 1 && to[0].Uri == Conftest, string.Join(", ", to.Select(l => l.Uri)));
        var missing = svc.Diagnostics(TestA).Where(d => d.Code == "fixture-not-found").Select(d => d.Message).ToList();
        Assert.True(missing.Contains("Fixture 'store' not found") && !missing.Contains("Fixture 'size' not found"), string.Join(" | ", missing));
    }

    [Fact]
    public void WhatHooksGiveTests()
    {
        var svc = Project((Conftest, ConftestText), (TestA, TestText));
        var missing = svc.Diagnostics(TestA).Where(d => d.Code == "fixture-not-found").Select(d => d.Message).ToList();
        // number comes from pytest_generate_tests and helper from the registered Helpers(): only missing is not found
        Assert.True(missing.Contains("Fixture 'missing' not found") && !missing.Any(m => m.Contains("'number'") || m.Contains("'helper'")), string.Join(" | ", missing));
        Assert.Contains("int", HoverAt(svc, TestA, TestText, "total = "));
        Assert.Contains("str", HoverAt(svc, TestA, TestText, "word = "));
        var to = svc.Definition(TestA, new LineMap(TestText).PositionOf(TestText.IndexOf("helper, missing", StringComparison.Ordinal) + 2));
        Assert.True(to.Count == 1 && to[0].Uri == Conftest && to[0].Range.Start.Line == ConftestText[..ConftestText.IndexOf("def helper", StringComparison.Ordinal)].Count(c => c == '\n'));
    }

    [Fact]
    public void TheListOfFixtures()
    {
        var svc = Project((Conftest, ConftestText), (TestA, TestText));
        var list = svc.Fixtures();
        var db = list.Single(f => f.Name == "db");
        Assert.Equal("conftest", db.Where);
        Assert.Equal(1, db.Uses);
        var helper = list.Single(f => f.Name == "helper");
        Assert.Equal("plugin", helper.Where);
        Assert.Contains("Helpers", helper.Origin);
        Assert.True(list.Any(f => f.Name == "tmp_path" && f.Where == "pytest"));
    }
}
