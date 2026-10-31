using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's pytest inspections (item 194): fixtures nobody asks for, parametrize arguments, parameters for @patch.</summary>
public class PytestInspectionTests
{
    const string Root = "file:///c:/proj/";

    static LanguageService Project(params (string File, string Text)[] files)
    {
        var svc = new LanguageService();
        foreach (var (f, t) in files) svc.Open(Root + f, t, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(LanguageService svc, string file, string code) => svc.Diagnostics(Root + file).Where(d => d.Code == code).ToList();

    const string Conftest = "import pytest\n\n\n@pytest.fixture\ndef db():\n    return 1\n\n\n@pytest.fixture\ndef user(db):\n    return 2\n\n\n@pytest.fixture\ndef spare():\n    return 3\n\n\n@pytest.fixture(autouse=True)\ndef setup():\n    yield\n\n\n@pytest.fixture\ndef by_name():\n    return 4\n\n\n@pytest.fixture\ndef fetched():\n    return 5\n";

    [Fact]
    public void AFixtureNobodyAsksFor()
    {
        string tests = "import pytest\n\n\n@pytest.mark.usefixtures('by_name')\ndef test_user(user):\n    assert user\n\n\ndef test_dynamic(request):\n    request.getfixturevalue('fetched')\n";
        var svc = Project(("conftest.py", Conftest), ("test_a.py", tests));
        var d = Found(svc, "conftest.py", "fixture-unused");
        // db is asked for by user, user by the test, by_name by usefixtures, fetched by getfixturevalue; setup is autouse
        Assert.Equal("Fixture 'spare' is not requested by any test or fixture", d.Single().Message);
    }

    [Fact]
    public void NothingIsSaidWhenFixturesAreHandedOutAtRunTime()
    {
        var svc = Project(("conftest.py", Conftest + "\n\ndef pytest_generate_tests(metafunc):\n    pass\n"), ("test_a.py", "def test_x(user):\n    pass\n"));
        Assert.Equal(0, Found(svc, "conftest.py", "fixture-unused").Count);
    }

    [Fact]
    public void ParametrizeNamesTheTestsParametersAndEachRowFits()
    {
        string tests = "import pytest\n\n\n" +
            "@pytest.mark.parametrize('a, b', [(1, 2), (3,), pytest.param(4, 5, 6), [7, 8]])\ndef test_pair(a, b):\n    pass\n\n\n" +
            "@pytest.mark.parametrize('x, y', [(1, 2)])\ndef test_missing(x):\n    pass\n\n\n" +
            "@pytest.mark.parametrize('n,n', [(1, 1)])\ndef test_twice(n):\n    pass\n\n\n" +
            "@pytest.mark.parametrize('db', [1, 2])\ndef test_override(user):\n    pass\n\n\n" +
            "@pytest.mark.parametrize(('p', 'q'), [(1, 2)])\ndef test_tuple(p, q):\n    pass\n";
        var d = Found(Project(("conftest.py", Conftest), ("test_p.py", tests)), "test_p.py", "parametrize-arguments").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        Assert.Equal(
            "3: 1 value for 2 names (a, b): pytest stops with \"the number of names must be equal to the number of values\" | 3: 3 values for 2 names (a, b): pytest stops with \"the number of names must be equal to the number of values\" | " +
            "8: 'test_missing' has no parameter 'y': pytest stops with \"function uses no argument 'y'\" | 13: 'n' is named twice: pytest stops with \"duplicate parametrization of 'n'\"",
            string.Join(" | ", d));
    }

    [Fact]
    public void PatchHandsAMockToEachParameter()
    {
        string code = "from unittest import mock\nfrom unittest.mock import patch\n\n\n" +
            "@patch('os.remove')\n@patch('os.rename')\ndef test_two(rename):\n    pass\n\n\n" +
            "@patch('os.remove')\ndef test_fine(remove):\n    pass\n\n\n" +
            "@patch('os.remove', new=lambda p: None)\ndef test_new():\n    pass\n\n\n" +
            "class TestIt:\n    @mock.patch.object(dict, 'get')\n    def test_method(self):\n        pass\n";
        var d = Found(Project(("test_m.py", code)), "test_m.py", "patch-arguments").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        Assert.Equal("6: The @patch decorators hand 'test_two' 2 mocks, and it takes 1: the call fails with TypeError | 22: The @patch decorators hand 'test_method' 1 mock, and it takes 0: the call fails with TypeError", string.Join(" | ", d));
    }
}
