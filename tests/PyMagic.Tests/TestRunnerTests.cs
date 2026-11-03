using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class TestRunnerTests
{
    static string PythonExe => Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python";

    static bool CanRun(string args)
    {
        try
        {
            var p = Process.Start(new ProcessStartInfo(PythonExe, args) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true });
            return p != null && p.WaitForExit(20000) && p.ExitCode == 0;
        }
        catch { return false; }
    }

    static readonly bool HavePython = CanRun("--version");
    static readonly bool HavePytest = HavePython && CanRun("-c \"import pytest\"");

    // ------------------------------------------------------------ discovery

    static TestNode Discover(string text) => TestDiscovery.DiscoverFile("tests/test_x.py", text)!;
    static string Ids(TestNode n) => string.Join(",", n.Children.SelectMany(c => c.Children.Count == 0 ? new[] { c.Id } : c.Children.Select(k => k.Id)));

    [Fact]
    public void FindsFunctionsClassesAndTestCases()
    {
        var f = Discover(
            "import unittest\n" +
            "def test_a(): ...\n" +
            "def helper(): ...\n" +
            "async def test_b(): ...\n" +
            "class TestGroup:\n" +
            "    def test_c(self): ...\n" +
            "    def not_a_test(self): ...\n" +
            "class Plain:\n" +
            "    def test_hidden(self): ...\n" +
            "class WithInit:\n" +
            "    def __init__(self): ...\n" +
            "    def test_skipped(self): ...\n" +
            "class Cases(unittest.TestCase):\n" +
            "    def testOld(self): ...\n" +
            "    def test_new(self): ...\n");
        Assert.Equal("tests/test_x.py::test_a,tests/test_x.py::test_b,tests/test_x.py::TestGroup::test_c," +
                     "tests/test_x.py::Cases::testOld,tests/test_x.py::Cases::test_new", Ids(f));
        Assert.Equal("file", f.Kind);
        var group = f.Children.Single(c => c.Name == "TestGroup");
        Assert.Equal("class", group.Kind);
        Assert.Equal(5, group.Line);
        Assert.Equal("method", group.Children[0].Kind);
        Assert.Equal(6, group.Children[0].Line);
        Assert.Equal("function", f.Children[0].Kind);
        Assert.Equal(2, f.Children[0].Line);
    }

    [Fact]
    public void TestCaseSubclassesAndInheritedMethodsAreFollowed()
    {
        var f = Discover(
            "from unittest import TestCase\n" +
            "import django.test\n" +
            "class Base(TestCase):\n" +
            "    def test_shared(self): ...\n" +
            "class Child(Base):\n" +
            "    def test_own(self): ...\n" +
            "class Web(django.test.TestCase):\n" +
            "    def test_page(self): ...\n" +
            "class Mixin:\n" +
            "    def test_not_collected(self): ...\n");
        Assert.Equal("tests/test_x.py::Base::test_shared,tests/test_x.py::Child::test_shared,tests/test_x.py::Child::test_own,tests/test_x.py::Web::test_page", Ids(f));
    }

    [Fact]
    public void FilesWithoutTestsAreOmittedAndDiscoveryWalksTheFolder()
    {
        Assert.True(TestDiscovery.DiscoverFile("tests/test_none.py", "def helper(): ...\n") == null);
        string root = Path.Combine(Path.GetTempPath(), "pm_disc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests"));
        Directory.CreateDirectory(Path.Combine(root, ".venv"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tests", "test_a.py"), "def test_one(): ...\n");
            File.WriteAllText(Path.Combine(root, "b_test.py"), "def test_two(): ...\n");
            File.WriteAllText(Path.Combine(root, "helpers.py"), "def test_not_in_a_test_file(): ...\n");
            File.WriteAllText(Path.Combine(root, ".venv", "test_ignored.py"), "def test_venv(): ...\n");
            var files = TestDiscovery.Discover(root);
            Assert.Equal("b_test.py,tests/test_a.py", string.Join(",", files.Select(x => x.Id)));
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ running

    sealed class Project : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_tr_" + Guid.NewGuid().ToString("N"));
        public Project() { Directory.CreateDirectory(Root); }
        public void File(string rel, string text)
        {
            string full = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            System.IO.File.WriteAllText(full, text.Replace("\r\n", "\n"));
        }
        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } }

        /// <summary>Runs and returns every message up to the process exit.</summary>
        public List<JsonNode> Run(string framework, params string[] ids)
        {
            var events = new BlockingCollection<JsonNode>();
            using var session = new RunSession(events.Add);
            session.StartTests(Root, ids, framework);
            var all = new List<JsonNode>();
            while (true)
            {
                if (!events.TryTake(out var e, TimeSpan.FromSeconds(60))) throw new AssertionException("timed out; saw: " + string.Join(" ", all.Select(x => x.ToJsonString())));
                all.Add(e);
                if (e["type"]!.GetValue<string>() is "exited" or "error") return all;
            }
        }
    }

    static Dictionary<string, JsonNode> Results(List<JsonNode> events) => events.Any(e => e["type"]!.GetValue<string>() == "testResult") ? ResultsCore(events) : throw new AssertionException("no results; events: " + string.Join(" ", events.Select(e => e.ToJsonString())));
    static Dictionary<string, JsonNode> ResultsCore(List<JsonNode> events) =>
        events.Where(e => e["type"]!.GetValue<string>() == "testResult").ToDictionary(e => e["id"]!.GetValue<string>(), e => e);

    static string Outcome(Dictionary<string, JsonNode> r, string id) => r[id]["outcome"]!.GetValue<string>();

    const string UnitTests =
        "import unittest\n" +
        "class Cart(unittest.TestCase):\n" +
        "    def test_ok(self):\n" +                  // 3
        "        self.assertEqual(1 + 1, 2)\n" +
        "    def test_bad(self):\n" +                 // 5
        "        self.assertEqual(1 + 1, 3)\n" +      // 6
        "    def test_error(self):\n" +               // 7
        "        raise KeyError('boom')\n" +          // 8
        "    @unittest.skip('later')\n" +
        "    def test_skipped(self):\n" +
        "        pass\n";

    [Fact]
    public void UnittestResultsCarryOutcomesMessagesAndLocations()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("tests/test_cart.py", UnitTests);
        var events = p.Run("unittest");
        var r = Results(events);
        const string id = "tests/test_cart.py::Cart::";
        Assert.Equal("passed", Outcome(r, id + "test_ok"));
        Assert.Equal("failed", Outcome(r, id + "test_bad"));
        Assert.Equal("error", Outcome(r, id + "test_error"));
        Assert.Equal("skipped", Outcome(r, id + "test_skipped"));

        var bad = r[id + "test_bad"];
        Assert.Contains("AssertionError", bad["message"]!.GetValue<string>());
        Assert.Contains("2 != 3", bad["traceback"]!.GetValue<string>());
        Assert.Equal("tests/test_cart.py", bad["file"]!.GetValue<string>());
        Assert.Equal(6, bad["line"]!.GetValue<int>());
        Assert.Equal("later", r[id + "test_skipped"]["message"]!.GetValue<string>());
        Assert.Equal("KeyError: 'boom'", r[id + "test_error"]["message"]!.GetValue<string>());

        // started is reported before the result
        int started = events.FindIndex(e => e["type"]!.GetValue<string>() == "testStarted" && e["id"]!.GetValue<string>() == id + "test_ok");
        int done = events.FindIndex(e => e["type"]!.GetValue<string>() == "testResult" && e["id"]!.GetValue<string>() == id + "test_ok");
        Assert.True(started >= 0 && started < done);
        Assert.Equal(1, events.Last()["code"]!.GetValue<int>());
    }

    [Fact]
    public void UnittestRunsOnlyTheSelectedIds()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("tests/test_cart.py", UnitTests);
        Assert.Equal(1, Results(p.Run("unittest", "tests/test_cart.py::Cart::test_ok")).Count);
        Assert.Equal(4, Results(p.Run("unittest", "tests/test_cart.py::Cart")).Count);
        Assert.Equal(4, Results(p.Run("unittest", "tests/test_cart.py")).Count);
        Assert.Equal(4, Results(p.Run("unittest")).Count);   // no ids: discover everything
    }

    [Fact]
    public void ImportErrorsAreCollectErrors()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("tests/test_broken.py", "import module_that_does_not_exist\ndef test_x(): ...\n");
        var events = p.Run("unittest", "tests/test_broken.py");
        var err = events.First(e => e["type"]!.GetValue<string>() == "collectError");
        Assert.Contains("module_that_does_not_exist", err["message"]!.GetValue<string>());
        Assert.Equal(1, events.Last()["code"]!.GetValue<int>());
    }

    [Fact]
    public void AutoFallsBackToUnittestWithoutPytest()
    {
        if (!HavePython || HavePytest) return;
        using var p = new Project();
        p.File("tests/test_cart.py", UnitTests);
        var events = p.Run("auto");
        Assert.Equal(4, Results(events).Count);
        Assert.True(events.Any(e => e["type"]!.GetValue<string>() == "output" && e["text"]!.GetValue<string>().Contains("pytest is not installed")));
    }

    const string PytestFile =
        "import pytest\n" +
        "def test_ok():\n" +                       // 2
        "    assert 1 + 1 == 2\n" +
        "def test_bad():\n" +                      // 4
        "    total = 5\n" +
        "    assert total == 6\n" +                // 6
        "@pytest.mark.skip(reason='later')\n" +
        "def test_skipped(): ...\n" +
        "@pytest.mark.parametrize('n', [1, 2, 3])\n" +
        "def test_many(n):\n" +
        "    assert n < 3\n" +
        "@pytest.fixture\n" +
        "def broken():\n" +
        "    raise RuntimeError('no fixture')\n" +
        "def test_fixture_error(broken): ...\n" +
        "class TestGroup:\n" +
        "    def test_method(self):\n" +
        "        print('hello from the test')\n" +
        "        assert True\n";

    [Fact]
    public void PytestReportsEveryOutcome()
    {
        if (!HavePytest) return;
        using var p = new Project();
        p.File("tests/test_p.py", PytestFile);
        var events = p.Run("pytest", "tests/test_p.py");
        var r = Results(events);
        const string f = "tests/test_p.py::";
        Assert.Equal("passed", Outcome(r, f + "test_ok"));
        Assert.Equal("failed", Outcome(r, f + "test_bad"));
        Assert.Equal("skipped", Outcome(r, f + "test_skipped"));
        Assert.Equal("passed", Outcome(r, f + "test_many[1]"));
        Assert.Equal("failed", Outcome(r, f + "test_many[3]"));
        Assert.Equal("error", Outcome(r, f + "test_fixture_error"));
        Assert.Equal("passed", Outcome(r, f + "TestGroup::test_method"));

        var bad = r[f + "test_bad"];
        Assert.Contains("assert 5 == 6", bad["message"]!.GetValue<string>());
        Assert.Equal("tests/test_p.py", bad["file"]!.GetValue<string>());
        Assert.Equal(6, bad["line"]!.GetValue<int>());
        Assert.Contains("assert total == 6", bad["traceback"]!.GetValue<string>());
        Assert.Equal("later", r[f + "test_skipped"]["message"]!.GetValue<string>());
        Assert.Contains("hello from the test", r[f + "TestGroup::test_method"]["output"]!.GetValue<string>());
        Assert.Contains("no fixture", r[f + "test_fixture_error"]["message"]!.GetValue<string>());

        var collected = events.First(e => e["type"]!.GetValue<string>() == "collected")["tests"]!.AsArray();
        Assert.True(collected.Any(t => t!["id"]!.GetValue<string>() == f + "test_many[2]"), "parametrized cases are listed");
    }

    [Fact]
    public void PytestRunsSelectedIdsAndRunsUnittestToo()
    {
        if (!HavePytest) return;
        using var p = new Project();
        p.File("tests/test_p.py", PytestFile);
        p.File("tests/test_u.py", UnitTests);
        Assert.Equal(1, Results(p.Run("pytest", "tests/test_p.py::test_ok")).Count);
        Assert.Equal(4, Results(p.Run("pytest", "tests/test_u.py")).Count);
        var r = Results(p.Run("auto", "tests/test_u.py::Cart::test_bad"));
        Assert.Equal("failed", Outcome(r, "tests/test_u.py::Cart::test_bad"));
    }

    [Fact]
    public void PytestCollectionErrorsAreReported()
    {
        if (!HavePytest) return;
        using var p = new Project();
        p.File("tests/test_broken.py", "def test_x(:\n");
        var events = p.Run("pytest");
        Assert.True(events.Any(e => e["type"]!.GetValue<string>() == "collectError"), "syntax error in a test file");
    }

    [Fact]
    public void RunsDjangosTestsWithItsRunnerAndATestDatabase()
    {
        // needs a Python with Django: PYMAGIC_TEST_DJANGO names it
        if (Environment.GetEnvironmentVariable("PYMAGIC_TEST_DJANGO") is not { Length: > 0 } python) return;
        using var p = new Project();
        p.File("manage.py", "import os, sys\n\nif __name__ == '__main__':\n    os.environ.setdefault('DJANGO_SETTINGS_MODULE', 'cfg.settings')\n    from django.core.management import execute_from_command_line\n    execute_from_command_line(sys.argv)\n");
        p.File("cfg/__init__.py", "");
        p.File("cfg/settings.py", "SECRET_KEY = 'x'\nINSTALLED_APPS = ['django.contrib.contenttypes', 'django.contrib.auth', 'shop']\nDATABASES = {'default': {'ENGINE': 'django.db.backends.sqlite3', 'NAME': 'db.sqlite3'}}\nUSE_TZ = True\n");
        p.File("shop/__init__.py", "");
        p.File("shop/models.py", "from django.db import models\n\n\nclass Item(models.Model):\n    name = models.CharField(max_length=20)\n");
        p.File("shop/migrations/__init__.py", "");
        p.File("shop/migrations/0001_initial.py", "from django.db import migrations, models\n\n\nclass Migration(migrations.Migration):\n    initial = True\n    dependencies = []\n    operations = [migrations.CreateModel(name='Item', fields=[('id', models.BigAutoField(primary_key=True)), ('name', models.CharField(max_length=20))])]\n");
        p.File("shop/tests.py", "from django.test import TestCase\n\nfrom .models import Item\n\n\nclass ItemTests(TestCase):\n    def test_create(self):\n        Item.objects.create(name='pen')\n        self.assertEqual(Item.objects.count(), 1)\n\n    def test_wrong(self):\n        self.assertEqual(Item.objects.count(), 5)\n");
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add) { Python = python };
        session.StartTests(p.Root, new List<string>(), "django");
        var all = new List<JsonNode>();
        while (true)
        {
            if (!events.TryTake(out var e, TimeSpan.FromSeconds(90))) throw new AssertionException("timed out: " + string.Join(" ", all.Select(x => x.ToJsonString())));
            all.Add(e);
            if (e["type"]!.GetValue<string>() is "exited" or "error") break;
        }
        var r = Results(all);
        Assert.Equal("passed", Outcome(r, "shop/tests.py::ItemTests::test_create"));
        Assert.Equal("failed", Outcome(r, "shop/tests.py::ItemTests::test_wrong"));
        Assert.True(!File.Exists(Path.Combine(p.Root, "db.sqlite3")) || new FileInfo(Path.Combine(p.Root, "db.sqlite3")).Length == 0, "the tests ran on a test database");
    }

    [Fact]
    public void RunsTheDoctestsOfTheModules()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("shapes.py", "def area(r):\n    \"\"\"\n    >>> area(1)\n    3\n    \"\"\"\n    return 3 * r * r\n\n\ndef twice(x):\n    \"\"\"\n    >>> twice(2)\n    5\n    \"\"\"\n    return 2 * x\n\n\ndef plain():\n    return 1\n");
        p.File("notes.py", "x = 1\n");
        var r = Results(p.Run("doctest"));
        Assert.Equal("shapes.py::area,shapes.py::twice", string.Join(",", r.Keys.OrderBy(k => k)));
        Assert.Equal("passed", Outcome(r, "shapes.py::area"));
        Assert.Equal("failed", Outcome(r, "shapes.py::twice"));
        Assert.Equal("1 of 1 examples failed", r["shapes.py::twice"]["message"]!.GetValue<string>());
        Assert.Equal(11, r["shapes.py::twice"]["line"]!.GetValue<int>());
        // one docstring asked for
        Assert.Equal("shapes.py::area", string.Join(",", Results(p.Run("doctest", "shapes.py::area")).Keys));
    }

    [Fact]
    public void NoseRunsPlainTestFunctionsBesideTestCases()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("test_nose.py", "import unittest\n\n\ndef test_plain():\n    assert 1 + 1 == 2\n\n\ndef test_wrong():\n    assert 1 == 2\n\n\ndef helper():\n    pass\n\n\nclass Box(unittest.TestCase):\n    def test_in_class(self):\n        pass\n");
        var r = Results(p.Run("nose"));
        Assert.Equal("test_nose.py::Box::test_in_class,test_nose.py::test_plain,test_nose.py::test_wrong", string.Join(",", r.Keys.OrderBy(k => k, StringComparer.Ordinal)));
        Assert.Equal("failed", Outcome(r, "test_nose.py::test_wrong"));
        Assert.Equal("test_nose.py::test_plain", string.Join(",", Results(p.Run("nose", "test_nose.py::test_plain")).Keys));
        // Trial needs Twisted; without it the run says so
        var trial = p.Run("trial");
        Assert.True(trial.Any(e => e["type"]!.GetValue<string>() == "testResult") || trial.Any(e => e.ToJsonString().Contains("twisted")),
            "trial without Twisted says why: " + string.Join(" ", trial.Select(e => e.ToJsonString())));
    }

    // ------------------------------------------------------------ item 221: bases in other files, subtests, Stop, Debug of tests

    [Fact]
    public void BaseClassesImportedFromOtherFilesBringTheirTests()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_disc_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "tests", "shared"));
        try
        {
            File.WriteAllText(Path.Combine(root, "tests", "base.py"),
                "import unittest\nclass ApiCase(unittest.TestCase):\n    def test_status(self): ...\n    def helper(self): ...\n");
            File.WriteAllText(Path.Combine(root, "tests", "shared", "__init__.py"), "from .checks import Checks\n");
            File.WriteAllText(Path.Combine(root, "tests", "shared", "checks.py"), "class Checks:\n    def test_shape(self): ...\n");
            File.WriteAllText(Path.Combine(root, "tests", "test_api.py"),
                "from .base import ApiCase\nfrom tests.shared import Checks\nimport tests.base as b\n" +
                "class UserApi(ApiCase):\n    def test_user(self): ...\n" +              // a TestCase through the other file
                "class TestShapes(Checks):\n    def test_round(self): ...\n" +             // pytest's naming, the base re-exported by a package
                "class OrderApi(b.ApiCase):\n    pass\n");                                  // module.Class
            var file = TestDiscovery.Discover(root).Single(f => f.Id == "tests/test_api.py");
            string Ids(string cls) => string.Join(",", file.Children.Single(c => c.Name == cls).Children.Select(m => m.Name));
            Assert.Equal("test_user,test_status", Ids("UserApi"));
            Assert.Equal("test_round,test_shape", Ids("TestShapes"));
            Assert.Equal("test_status", Ids("OrderApi"));
            var inherited = file.Children.Single(c => c.Name == "UserApi").Children[1];
            Assert.Equal("tests/test_api.py::UserApi::test_status", inherited.Id);
            Assert.Equal(4, inherited.Line);   // placed on its class's line: it is written in another file
            // which it names, with the method's line there (going to the test opens it)
            Assert.Equal("tests/base.py", inherited.WrittenIn);
            Assert.Equal(3, inherited.WrittenAt);
            var shape = file.Children.Single(c => c.Name == "TestShapes").Children[1];
            Assert.Equal("tests/shared/checks.py", shape.WrittenIn);
            Assert.Equal(2, shape.WrittenAt);
            // a test written in the file names no other
            Assert.True(file.Children.Single(c => c.Name == "UserApi").Children[0].WrittenIn == null);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void UnittestSubtestsAreReportedOneByOne()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("test_sub.py",
            "import unittest\nclass Numbers(unittest.TestCase):\n    def test_even(self):\n" +
            "        for i in range(3):\n            with self.subTest(i=i):\n                self.assertEqual(i % 2, 0)\n" +
            "    def test_named(self):\n        with self.subTest('first', n=1):\n            pass\n");
        var r = Results(p.Run("unittest"));
        const string id = "test_sub.py::Numbers::test_even";
        Assert.Equal("passed", Outcome(r, id + "[i=0]"));
        Assert.Equal("failed", Outcome(r, id + "[i=1]"));
        Assert.Equal("passed", Outcome(r, id + "[i=2]"));
        Assert.Equal("failed", Outcome(r, id));   // the test fails with its subtest
        Assert.Equal(6, r[id + "[i=1]"]["line"]!.GetValue<int>());
        Assert.Contains("1 != 0", r[id + "[i=1]"]["message"]!.GetValue<string>());
        Assert.Equal("passed", Outcome(r, "test_sub.py::Numbers::test_named[first n=1]"));
    }

    [Fact]
    public void PytestSubtestsAreReportedOneByOne()
    {
        // pytest 9's subtests (the subtests fixture, and unittest's subTest run by pytest)
        if (!HavePytest || !CanRun("-c \"import _pytest.subtests\"")) return;
        using var p = new Project();
        p.File("test_psub.py",
            "import unittest\n\n\ndef test_even(subtests):\n    for i in range(3):\n        with subtests.test(i=i):\n            assert i % 2 == 0\n\n\n" +
            "def test_named(subtests):\n    with subtests.test('first', n=1):\n        pass\n\n\n" +
            "class Numbers(unittest.TestCase):\n    def test_odd(self):\n        for i in range(2):\n            with self.subTest(i=i):\n                self.assertEqual(i % 2, 1)\n");
        var r = Results(p.Run("pytest", "test_psub.py"));
        const string id = "test_psub.py::test_even";
        Assert.Equal("passed", Outcome(r, id + "[i=0]"));
        Assert.Equal("failed", Outcome(r, id + "[i=1]"));
        Assert.Equal("passed", Outcome(r, id + "[i=2]"));
        Assert.Equal("failed", Outcome(r, id));   // the test fails with its subtest
        Assert.Equal(7, r[id + "[i=1]"]["line"]!.GetValue<int>());
        Assert.Equal("passed", Outcome(r, "test_psub.py::test_named[first n=1]"));
        Assert.Equal("passed", Outcome(r, "test_psub.py::test_named"));
        Assert.Equal("failed", Outcome(r, "test_psub.py::Numbers::test_odd[i=0]"));
        Assert.Equal("passed", Outcome(r, "test_psub.py::Numbers::test_odd[i=1]"));
        Assert.Equal("failed", Outcome(r, "test_psub.py::Numbers::test_odd"));
    }

    [Fact]
    public void StopEndsATestRun()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("test_slow.py", "import time, unittest\nclass Slow(unittest.TestCase):\n    def test_wait(self):\n        time.sleep(60)\n");
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        session.StartTests(p.Root, Array.Empty<string>(), "unittest");
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline && events.TryTake(out var e, TimeSpan.FromSeconds(30)))
            if (e["type"]!.GetValue<string>() == "testStarted") break;
        var asked = DateTime.UtcNow;
        session.Handle(new JsonObject { ["type"] = "stop" });
        while (events.TryTake(out var e, TimeSpan.FromSeconds(20)))
            if (e["type"]!.GetValue<string>() == "exited") { Assert.True(DateTime.UtcNow - asked < TimeSpan.FromSeconds(15), "stopped at once"); return; }
        throw new AssertionException("the run did not end");
    }

    [Fact]
    public void DebuggedTestsStopAtTheirBreakpointsAndReportTheirResults()
    {
        if (!HavePython) return;
        using var p = new Project();
        p.File("tests/test_cart.py", UnitTests);
        var events = new BlockingCollection<JsonNode>();
        using var session = new RunSession(events.Add);
        session.StartDebugTests(p.Root, new[] { "tests/test_cart.py::Cart::test_bad", "tests/test_cart.py::Cart::test_ok" }, "unittest",
            new Dictionary<string, JsonArray> { ["tests/test_cart.py"] = new JsonArray(6) });
        var seen = new List<JsonNode>();
        JsonNode Next(string type)
        {
            while (events.TryTake(out var e, TimeSpan.FromSeconds(60)))
            {
                seen.Add(e);
                if (e["type"]!.GetValue<string>() == type) return e;
            }
            throw new AssertionException("timed out waiting for " + type + "; saw: " + string.Join(" ", seen.Select(x => x.ToJsonString())));
        }
        var collected = Next("collected");
        Assert.Equal(2, collected["tests"]!.AsArray().Count);
        var stopped = Next("stopped");
        Assert.Equal(6, stopped["frames"]![0]!["line"]!.GetValue<int>());
        Assert.Equal("test_bad", stopped["frames"]![0]!["name"]!.GetValue<string>());
        session.Handle(new JsonObject { ["type"] = "continue" });
        Next("exited");
        var r = ResultsCore(seen);
        Assert.Equal("failed", Outcome(r, "tests/test_cart.py::Cart::test_bad"));
        Assert.Equal("passed", Outcome(r, "tests/test_cart.py::Cart::test_ok"));
    }
}
