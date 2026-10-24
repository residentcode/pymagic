using PyMagic.Server;

namespace PyMagic.Tests;

public class FixtureTests
{
    const string Root = "file:///c:/proj/";
    const string Conftest = Root + "conftest.py";
    const string TestA = Root + "test_a.py";
    const string SubConftest = Root + "sub/conftest.py";
    const string TestB = Root + "sub/test_b.py";

    /// <summary>`|` marks the cursor; returns the text without it and the position.</summary>
    static (string Text, Position Pos) Cursor(string marked)
    {
        int i = marked.IndexOf('|');
        string text = marked.Remove(i, 1);
        return (text, new LineMap(text).PositionOf(i));
    }

    static LanguageService Project(params (string Uri, string Text)[] files)
    {
        var svc = new LanguageService();
        foreach (var (u, t) in files) svc.Open(u, t.Replace("|", ""), 1);
        return svc;
    }

    static (LanguageService Svc, Position Pos) ProjectAt(string uri, string marked, params (string Uri, string Text)[] others)
    {
        var (text, pos) = Cursor(marked);
        var svc = Project(others.Append((uri, text)).ToArray());
        return (svc, pos);
    }

    const string DbConftest = "import pytest\n\n\n@pytest.fixture\ndef db():\n    '''A database connection.\\n\\nMore.'''\n    return 1\n";

    // ------------------------------------------------------------ definition

    [Fact]
    public void ParameterGoesToTheFixtureInConftest()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(d|b):\n    pass\n", (Conftest, DbConftest));
        var found = svc.Definition(TestA, pos);
        Assert.Equal(1, found.Count);
        Assert.Equal(Conftest, found[0].Uri);
        Assert.Equal(4, found[0].Range.Start.Line);   // def db
        Assert.Equal(4, found[0].Range.Start.Character);
    }

    [Fact]
    public void TheNearestConftestWins()
    {
        string outer = "import pytest\n@pytest.fixture\ndef db(): return 1\n";
        string inner = "import pytest\n@pytest.fixture\ndef db(): return 'x'\n";
        var (svc, pos) = ProjectAt(TestB, "def test_x(d|b):\n    pass\n", (Conftest, outer), (SubConftest, inner));
        Assert.Equal(SubConftest, svc.Definition(TestB, pos)[0].Uri);
        var (svc2, pos2) = ProjectAt(TestA, "def test_x(d|b):\n    pass\n", (Conftest, outer), (SubConftest, inner));
        Assert.Equal(Conftest, svc2.Definition(TestA, pos2)[0].Uri);
    }

    [Fact]
    public void AFixtureInTheModuleBeatsConftest()
    {
        string src = "import pytest\n\n\n@pytest.fixture\ndef db(): return 2\n\n\ndef test_x(d|b):\n    pass\n";
        var (svc, pos) = ProjectAt(TestA, src, (Conftest, DbConftest));
        var loc = svc.Definition(TestA, pos)[0];
        Assert.Equal(TestA, loc.Uri);
        Assert.Equal(4, loc.Range.Start.Line);
    }

    [Fact]
    public void AFixtureAskingForItsOwnNameGetsTheOneItOverrides()
    {
        string inner = "import pytest\n\n\n@pytest.fixture\ndef db(d|b):\n    return db\n";
        var (svc, pos) = ProjectAt(SubConftest, inner, (Conftest, DbConftest));
        var loc = svc.Definition(SubConftest, pos)[0];
        Assert.Equal(Conftest, loc.Uri);
    }

    [Fact]
    public void FixturesImportedIntoAModuleOrConftestAreVisible()
    {
        string helpers = "import pytest\n\n\n@pytest.fixture\ndef shared(): return 1\n";
        var (svc, pos) = ProjectAt(TestA, "from helpers import shared\n\n\ndef test_x(sh|ared):\n    pass\n", (Root + "helpers.py", helpers));
        Assert.Equal(Root + "helpers.py", svc.Definition(TestA, pos)[0].Uri);

        var (svc2, pos2) = ProjectAt(TestA, "def test_x(sh|ared):\n    pass\n", (Root + "helpers.py", helpers), (Conftest, "from helpers import *\n"));
        Assert.Equal(Root + "helpers.py", svc2.Definition(TestA, pos2)[0].Uri);
    }

    [Fact]
    public void AClassFixtureIsOnlyForItsOwnTests()
    {
        string src = "import pytest\n\n\nclass TestA:\n    @pytest.fixture\n    def thing(self): return 1\n\n    def test_x(self, th|ing):\n        pass\n\n\nclass TestB:\n    def test_y(self, thing):\n        pass\n";
        var (svc, pos) = ProjectAt(TestA, src);
        Assert.Equal(1, svc.Definition(TestA, pos).Count);
        var diagnostics = svc.Diagnostics(TestA).Where(d => d.Code == "fixture-not-found").ToList();
        Assert.Equal(1, diagnostics.Count);
        Assert.Equal(12, diagnostics[0].Range.Start.Line);
    }

    [Fact]
    public void ANameGivenToTheFixtureIsWhatTestsAskFor()
    {
        string conf = "import pytest\n\n\n@pytest.fixture(name='database')\ndef _database_fixture(): return 1\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(data|base):\n    pass\n", (Conftest, conf));
        var loc = svc.Definition(TestA, pos)[0];
        Assert.Equal(Conftest, loc.Uri);
        Assert.Equal(3, loc.Range.Start.Line);   // the literal on the decorator line
    }

    [Fact]
    public void UsefixturesAndGetfixturevalueLiteralsLeadToTheFixture()
    {
        var (svc, pos) = ProjectAt(TestA, "import pytest\n\n\n@pytest.mark.usefixtures('d|b')\ndef test_x():\n    pass\n", (Conftest, DbConftest));
        Assert.Equal(Conftest, svc.Definition(TestA, pos)[0].Uri);
        var (svc2, pos2) = ProjectAt(TestA, "def test_x(request):\n    request.getfixturevalue('d|b')\n", (Conftest, DbConftest));
        Assert.Equal(Conftest, svc2.Definition(TestA, pos2)[0].Uri);
    }

    [Fact]
    public void ParametersThatAreNotFixtureRequests()
    {
        string src = "import pytest\nfrom unittest import mock\n\n\n@pytest.mark.parametrize('a, b', [(1, 2)])\ndef test_p(a, b, d|b):\n    pass\n\n\ndef test_d(x=1, db=2):\n    pass\n\n\n@mock.patch('os.getcwd')\ndef test_m(getcwd, db):\n    pass\n\n\ndef helper(db):\n    pass\n\n\nclass TestK:\n    def test_k(self, db):\n        pass\n";
        var (svc, pos) = ProjectAt(TestA, src, (Conftest, DbConftest));
        Assert.Equal(1, svc.Definition(TestA, pos).Count);   // the real request
        var (text, _) = Cursor(src);
        // a, b are parametrized; x and db have defaults; getcwd is the mock; `helper` is no test; self is the receiver
        var unknown = svc.Diagnostics(TestA).Where(d => d.Code == "fixture-not-found").ToList();
        Assert.Equal(0, unknown.Count);
        Assert.Equal(0, svc.Definition(TestA, new LineMap(text).PositionOf(text.IndexOf("x=1") + 0)).Count);
        Assert.Equal(0, svc.Definition(TestA, new LineMap(text).PositionOf(text.IndexOf("def helper(db") + 12)).Count);
    }

    [Fact]
    public void FixturesOfPytestItselfHaveNoDefinitionButAHover()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(tmp_|path):\n    pass\n");
        Assert.Equal(0, svc.Definition(TestA, pos).Count);
        var hover = svc.Hover(TestA, pos);
        Assert.True(hover != null, "hover");
        Assert.Contains("tmp_path", hover!.Contents.Value);
        Assert.Contains("provided by pytest", hover.Contents.Value);
    }

    // ------------------------------------------------------------ usages

    [Fact]
    public void UsagesOfAFixtureFromItsDefinition()
    {
        string conf = "import pytest\n\n\n@pytest.fixture\ndef d|b():\n    return 1\n\n\n@pytest.fixture\ndef other(db):\n    return db\n";
        var (svc, pos) = ProjectAt(Conftest, conf, (TestA, "def test_x(db):\n    assert db\n\n\ndef test_y(other):\n    pass\n"),
            (TestB, "def test_z(db):\n    pass\n"));
        var refs = svc.References(Conftest, pos, includeDeclaration: true);
        // definition, `other(db)` and its use in the body, and the two tests (one in a subfolder); nothing for `other`
        Assert.True(refs.Any(l => l.Uri == Conftest && l.Range.Start.Line == 4), "the definition");
        Assert.True(refs.Any(l => l.Uri == Conftest && l.Range.Start.Line == 9), "other(db)");
        Assert.True(refs.Any(l => l.Uri == TestA && l.Range.Start.Line == 0), "test_x");
        Assert.True(refs.Any(l => l.Uri == TestB), "test_z");
        Assert.True(!refs.Any(l => l.Uri == TestA && l.Range.Start.Line == 4), "test_y asks for other");
        Assert.Equal(0, svc.References(Conftest, pos, includeDeclaration: false).Count(l => l.Range.Start.Line == 4 && l.Uri == Conftest));
    }

    [Fact]
    public void UsagesOfAFixtureFromARequest()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(d|b):\n    pass\n\n\ndef test_y(db):\n    pass\n", (Conftest, DbConftest));
        var refs = svc.References(TestA, pos, includeDeclaration: true);
        Assert.Equal(3, refs.Count);
        Assert.Equal(2, refs.Count(l => l.Uri == TestA));
    }

    [Fact]
    public void AnOverridingFixtureHasItsOwnUsages()
    {
        string outer = "import pytest\n\n\n@pytest.fixture\ndef d|b(): return 1\n";
        string inner = "import pytest\n\n\n@pytest.fixture\ndef db(): return 2\n";
        var (svc, pos) = ProjectAt(Conftest, outer, (SubConftest, inner), (TestA, "def test_a(db):\n    pass\n"), (TestB, "def test_b(db):\n    pass\n"));
        var refs = svc.References(Conftest, pos, includeDeclaration: false);
        Assert.True(refs.Any(l => l.Uri == TestA), "tests above the override use the outer fixture");
        Assert.True(!refs.Any(l => l.Uri == TestB), "tests below it use the override");
    }

    // ------------------------------------------------------------ rename

    [Fact]
    public void RenamingAFixtureFromItsDefinition()
    {
        string conf = "import pytest\n\n\n@pytest.fixture\ndef d|b():\n    return 1\n";
        var (svc, pos) = ProjectAt(Conftest, conf, (TestA, "def test_x(db):\n    assert db\n\n\n@pytest.mark.usefixtures('db')\ndef test_y():\n    pass\n"));
        var edit = svc.Rename(Conftest, pos, "database");
        Assert.Equal(1, edit.Changes[Conftest].Count);
        Assert.Equal(3, edit.Changes[TestA].Count);   // the parameter, its use in the body, the usefixtures literal
        Assert.True(edit.Changes[TestA].All(e => e.NewText == "database"));
    }

    [Fact]
    public void RenamingAFixtureFromARequest()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(d|b):\n    assert db\n", (Conftest, DbConftest), (TestB, "def test_z(db):\n    pass\n"));
        var edit = svc.Rename(TestA, pos, "database");
        Assert.Equal(1, edit.Changes[Conftest].Count);
        Assert.Equal(2, edit.Changes[TestA].Count);
        Assert.Equal(1, edit.Changes[TestB].Count);
    }

    [Fact]
    public void RenamingAFixtureWithAName()
    {
        string conf = "import pytest\n\n\n@pytest.fixture(name='database')\ndef _database(): return 1\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(data|base):\n    assert database\n", (Conftest, conf));
        var edit = svc.Rename(TestA, pos, "store");
        Assert.Equal(1, edit.Changes[Conftest].Count);   // only the literal: the function keeps its name
        Assert.Equal(3, edit.Changes[Conftest][0].Range.Start.Line);
    }

    [Fact]
    public void RenameRefusesClashesAndPytestsOwnFixtures()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(d|b, other):\n    pass\n", (Conftest, DbConftest));
        Assert.Throws<ServiceException>(() => svc.Rename(TestA, pos, "other"));
        var (svc2, pos2) = ProjectAt(TestA, "def test_x(tmp_|path):\n    pass\n");
        Assert.Throws<ServiceException>(() => svc2.PrepareRename(TestA, pos2));
        Assert.Throws<ServiceException>(() => svc2.Rename(TestA, pos2, "mine"));
    }

    [Fact]
    public void RenamingAParameterThatIsNotAFixtureStaysLocal()
    {
        var svc = Project((TestA, "def helper(a):\n    return a\n"));
        var edit = svc.Rename(TestA, new Position(0, 12), "value");
        Assert.Equal(2, edit.Changes[TestA].Count);
    }

    // ------------------------------------------------------------ types and hover

    [Fact]
    public void AParameterHasTheTypeItsFixtureProvides()
    {
        string conf = "import pytest\n\n\nclass Database:\n    def query(self): ...\n\n\n@pytest.fixture\ndef db() -> Database:\n    return Database()\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(db):\n    d|b.query()\n", (Conftest, conf));
        var hover = svc.Hover(TestA, pos);
        Assert.True(hover != null, "hover");
        Assert.Contains("Database", hover!.Contents.Value);
    }

    [Fact]
    public void GeneratorFixturesProvideWhatTheyYield()
    {
        string conf = "import pytest\nfrom typing import Iterator\n\n\nclass Conn:\n    def close(self): ...\n\n\n@pytest.fixture\ndef conn() -> Iterator[Conn]:\n    c = Conn()\n    yield c\n    c.close()\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(conn):\n    c|onn\n", (Conftest, conf));
        Assert.Contains("Conn", svc.Hover(TestA, pos)!.Contents.Value);
        string inferred = "import pytest\n\n\n@pytest.fixture\ndef number():\n    yield 5\n";
        var (svc2, pos2) = ProjectAt(TestA, "def test_x(number):\n    n|umber\n", (Conftest, inferred));
        Assert.Contains("int", svc2.Hover(TestA, pos2)!.Contents.Value);
    }

    [Fact]
    public void AttributesOfAFixtureValueCompleteAndAreChecked()
    {
        string conf = "import pytest\n\n\nclass Database:\n    def query(self): ...\n\n\n@pytest.fixture\ndef db():\n    return Database()\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(db):\n    db.qu|\n", (Conftest, conf));
        Assert.True(svc.Completion(TestA, pos).Any(i => i.Label == "query"));
        var svc2 = Project((Conftest, conf), (TestA, "def test_x(db):\n    db.nothing()\n"));
        Assert.True(svc2.Diagnostics(TestA).Any(d => d.Message.Contains("Cannot find reference 'nothing'")));
    }

    [Fact]
    public void HoverOnARequestShowsScopeAndWhereItIsDefined()
    {
        var (svc, pos) = ProjectAt(TestA, "def test_x(d|b):\n    pass\n", (Conftest, DbConftest.Replace("@pytest.fixture", "@pytest.fixture(scope='session')")));
        string text = svc.Hover(TestA, pos)!.Contents.Value;
        Assert.Contains("(fixture) db", text);
        Assert.Contains("session", text);
        Assert.Contains("conftest.py:5", text);
        Assert.Contains("A database connection.", text);
    }

    // ------------------------------------------------------------ completion

    [Fact]
    public void ParameterListsOfTestsOfferFixtures()
    {
        string conf = DbConftest + "\n\n@pytest.fixture\ndef user(): return 1\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(|):\n    pass\n", (Conftest, conf));
        var labels = svc.Completion(TestA, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("db") && labels.Contains("user"), string.Join(",", labels));
        Assert.True(labels.Contains("tmp_path") && labels.Contains("monkeypatch"), "pytest's own");
        Assert.True(labels.IndexOf("db") < labels.IndexOf("tmp_path"), "the project's own first");
    }

    [Fact]
    public void FixtureCompletionFiltersAndSkipsWhatIsAlreadyThere()
    {
        string conf = DbConftest + "\n\n@pytest.fixture\ndef user(): return 1\n";
        var (svc, pos) = ProjectAt(TestA, "def test_x(db, u|):\n    pass\n", (Conftest, conf));
        var labels = svc.Completion(TestA, pos).Select(i => i.Label).ToList();
        Assert.True(labels.Contains("user"), string.Join(",", labels));
        Assert.True(!labels.Contains("db"), "db is already asked for");
        Assert.True(!labels.Contains("tmp_path"), "the prefix is u");

        var (svc2, pos2) = ProjectAt(TestA, "def test_x(db, |):\n    pass\n", (Conftest, conf));
        Assert.True(!svc2.Completion(TestA, pos2).Any(i => i.Label == "db"));
    }

    [Fact]
    public void FixturesAreOfferedToFixturesAndNotToOtherFunctions()
    {
        var (svc, pos) = ProjectAt(Conftest, "import pytest\n\n\n@pytest.fixture\ndef a(): return 1\n\n\n@pytest.fixture\ndef b(|):\n    pass\n");
        Assert.True(svc.Completion(Conftest, pos).Any(i => i.Label == "a"));
        var (svc2, pos2) = ProjectAt(TestA, "def helper(|):\n    pass\n", (Conftest, DbConftest));
        Assert.True(!svc2.Completion(TestA, pos2).Any(i => i.Label == "db"));
        var (svc3, pos3) = ProjectAt(TestA, "def test_x(a=|):\n    pass\n", (Conftest, DbConftest));
        Assert.True(!svc3.Completion(TestA, pos3).Any(i => i.Label == "db"), "a default value is not a fixture name");
    }

    [Fact]
    public void FixtureNamesInUsefixturesLiterals()
    {
        var (svc, pos) = ProjectAt(TestA, "import pytest\n\n\n@pytest.mark.usefixtures('d|')\ndef test_x():\n    pass\n", (Conftest, DbConftest));
        Assert.True(svc.Completion(TestA, pos).Any(i => i.Label == "db"));
    }

    // ------------------------------------------------------------ inspection

    [Fact]
    public void AnUnknownFixtureIsReportedWeakly()
    {
        var svc = Project((Conftest, DbConftest), (TestA, "def test_x(db, nope):\n    pass\n"));
        var found = svc.Diagnostics(TestA).Where(d => d.Code == "fixture-not-found").ToList();
        Assert.Equal(1, found.Count);
        Assert.Contains("'nope'", found[0].Message);
        Assert.Equal(Severity.Information, found[0].Severity);
    }

    [Fact]
    public void NoReportWhenFixturesMayComeFromPlugins()
    {
        Assert.Equal(0, Project((Conftest, DbConftest), (TestA, "def test_x(mocker, client, django_db_setup):\n    pass\n")).Diagnostics(TestA).Count(d => d.Code == "fixture-not-found"));
        Assert.Equal(0, Project((Conftest, "pytest_plugins = ['myplugin']\n"), (TestA, "def test_x(nope):\n    pass\n")).Diagnostics(TestA).Count(d => d.Code == "fixture-not-found"));
        Assert.Equal(0, Project((TestA, "def test_x(nope):\n    pass\n")).Diagnostics(TestA).Count(d => d.Code == "fixture-not-found"));   // nothing says this is pytest
        Assert.Equal(0, Project((Conftest, DbConftest), (Root + "helpers.py", "def test_x(nope):\n    pass\n")).Diagnostics(Root + "helpers.py").Count(d => d.Code == "fixture-not-found"));   // not a test file
        Assert.Equal(0, Project((Conftest, DbConftest), (TestA, "def test_x(nope):  # noqa\n    pass\n")).Diagnostics(TestA).Count(d => d.Code == "fixture-not-found"));
    }

    // what real suites (numpy, pandas, scipy, fsspec) taught

    static int NotFound(string testSource, params (string Uri, string Text)[] others) =>
        Project(others.Append((TestA, testSource)).ToArray()).Diagnostics(TestA).Count(d => d.Code == "fixture-not-found");

    [Fact]
    public void ParametrizeOnTheClassAppliesToItsMethods()
    {
        Assert.Equal(0, NotFound("import pytest\n\n\n@pytest.mark.parametrize('cls', [int, str])\nclass TestX:\n    def test_a(self, cls):\n        pass\n", (Conftest, DbConftest)));
    }

    [Fact]
    public void ImplicitlyConcatenatedParametrizeNames()
    {
        Assert.Equal(0, NotFound("import pytest\n\n\n@pytest.mark.parametrize('a,b,'\n                         'c', [(1, 2, 3)])\ndef test_a(a, b, c):\n    pass\n", (Conftest, DbConftest)));
    }

    [Fact]
    public void DecoratorsThatSupplyArgumentsMakeTheInspectionQuiet()
    {
        Assert.Equal(0, NotFound("import pytest\nfrom hypothesis import given\n\n\n@given(x=1)\ndef test_a(x):\n    pass\n", (Conftest, DbConftest)));
        Assert.Equal(1, NotFound("import pytest\n\n\n@pytest.mark.slow\ndef test_a(x):\n    pass\n", (Conftest, DbConftest)));
    }

    [Fact]
    public void AFixtureMayAskForANameTheTestsParametrize()
    {
        string src = "import pytest\n\n\n@pytest.fixture\ndef path(ext):\n    return ext\n\n\n@pytest.mark.parametrize('ext', ['.a', '.b'])\ndef test_a(path):\n    pass\n";
        Assert.Equal(0, NotFound(src, (Conftest, DbConftest)));
        Assert.Equal(0, NotFound("import pytest\n\n\n@pytest.fixture\ndef path(other):\n    return 1\n\n\ndef pytest_generate_tests(metafunc):\n    pass\n", (Conftest, DbConftest)));
    }

    [Fact]
    public void ABaseClassesFixturesAreInheritedAndHelperClassesAreNotChecked()
    {
        string inherited = "import pytest\n\n\nclass Base:\n    @pytest.fixture\n    def obj(self):\n        return 1\n\n\nclass TestX(Base):\n    def test_a(self, obj):\n        pass\n";
        var (svc, pos) = ProjectAt(TestA, inherited.Replace("self, obj", "self, ob|j"));
        Assert.Equal(1, svc.Definition(TestA, pos).Count);
        Assert.Equal(0, NotFound(inherited));
        // a base class asks for what its subclasses will provide
        Assert.Equal(0, NotFound("import pytest\n\n\nclass Base:\n    @pytest.fixture\n    def expected(self, obj):\n        return obj\n"));
        Assert.Equal(0, NotFound("import pytest\n\n\nclass TestY(Unknown):\n    def test_a(self, thing):\n        pass\n"));   // an unknown base may provide fixtures
    }

    [Fact]
    public void ANameBoundToAFixtureIsAFixtureOfThatName()
    {
        string conf = "import pytest\n\n\n@pytest.fixture(params=[1, 2])\ndef nulls(request):\n    return request.param\n\n\nnulls2 = nulls\n";
        var (svc, pos) = ProjectAt(TestA, "def test_a(nulls, nul|ls2):\n    pass\n", (Conftest, conf));
        Assert.Equal(1, svc.Definition(TestA, pos).Count);
        Assert.Equal(0, NotFound("def test_a(nulls, nulls2):\n    pass\n", (Conftest, conf)));
    }

    [Fact]
    public void CreateFixtureFixAddsTheFixtureAndTheImport()
    {
        string src = "def test_x(nope):\n    pass\n";
        var svc = Project((Conftest, DbConftest), (TestA, src));
        var action = svc.CodeActions(TestA, new LineMap(src).RangeOf(0, src.Length)).First(a => a.Title.StartsWith("Create fixture 'nope'"));
        var lines = new LineMap(src);
        string text = src;
        foreach (var e in action.Edit!.Changes[TestA].OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        Assert.Equal("import pytest\n\n@pytest.fixture\ndef nope():\n    raise NotImplementedError\n\n\ndef test_x(nope):\n    pass\n", text);
    }

    [Fact]
    public void RequestingAFixtureForItsEffectIsNotAnUnusedParameter()
    {
        var svc = Project((Conftest, DbConftest), (TestA, "def test_x(db):\n    pass\n\n\ndef test_y(db):\n    return 1\n"));
        Assert.Equal(0, svc.Diagnostics(TestA).Count(d => d.Code == "unused-argument"));
    }
}
