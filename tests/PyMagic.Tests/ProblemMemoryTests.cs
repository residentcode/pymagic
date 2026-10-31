using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The Problems tab remembers the findings of files nothing changed for, and its answers stay those of a run that works every file out.</summary>
public class ProblemMemoryTests
{
    static string Key(ProblemEntry p) => $"{p.Uri}|{p.Line}|{p.Column}|{p.Severity}|{p.Code}|{p.Message}";

    static void Write(string root, string relative, string text)
    {
        string path = Path.Combine(root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
    }

    static string Project()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_mem_" + Guid.NewGuid().ToString("N"));
        Write(root, "pkg/__init__.py", "");
        Write(root, "pkg/base.py", "class Base:\n    size: int = 1\n    def grow(self, n: int) -> int:\n        return self.size + n\n");
        Write(root, "pkg/user.py", "from pkg.base import Base\n\ndef use():\n    b = Base()\n    b.grow('a')\n    return b.size + b.weight\n");
        Write(root, "pkg/star.py", "from pkg.base import *\n\nvalue = Base().grow(1)\nother = Base().missing\n");
        Write(root, "pkg/chain.py", "from pkg import user\n\ndef again():\n    return user.use() + user.nothing\n");
        Write(root, "pkg/sub/__init__.py", "");
        Write(root, "pkg/sub/leaf.py", "def f(x):\n    return x\n");
        Write(root, "pkg/through.py", "import pkg.sub\n\nresult = pkg.sub.leaf.f(1, 2)\n");
        Write(root, "pkg/alone.py", "import os\n\ndef g():\n    return undefined_name\n");
        Write(root, "pkg/old.py", "def h(a, b):\n    return a\n");
        Write(root, "pkg/uses_old.py", "from pkg.old import h\n\nh(1)\n");
        Write(root, "tests/conftest.py", "import pytest\n\n@pytest.fixture\ndef number() -> int:\n    return 1\n");
        Write(root, "tests/test_it.py", "def test_number(number):\n    assert number.upper()\n");
        for (int i = 0; i < 6; i++) Write(root, $"pkg/many{i}.py", $"from pkg.base import Base\n\nclass Many{i}(Base):\n    def grow(self, n):\n        return self.nope{i}\n");
        return root;
    }

    /// <summary>What a service that remembers nothing finds, with the same open documents.</summary>
    static string FullRun(string root, Dictionary<string, string> open)
    {
        var svc = new LanguageService { RememberProblems = false };
        svc.SourceRoots.Add(root);
        foreach (var (uri, text) in open) svc.Open(uri, text, 1);
        return string.Join("\n", svc.Problems(4, 100000).Select(Key));
    }

    [Fact]
    public void RememberedProblemsAreThoseOfAFullRunAfterEditsARenameAndADeletion()
    {
        string root = Project();
        try
        {
            var open = new Dictionary<string, string>();
            var service = new LanguageService();
            service.SourceRoots.Add(root);
            // each run as the server makes it: a copy of the service that reads the project again, sharing what the earlier runs found
            (string Answer, int WorkedOut) Run()
            {
                var copy = service.ForkForAnalysis();
                string answer = string.Join("\n", copy.Problems(4, 100000).Select(Key));
                return (answer, copy.ProblemsWorkedOut);
            }

            var first = Run();
            Assert.Equal(FullRun(root, open), first.Answer);
            Assert.True(first.Answer.Contains("weight"), "findings that need another module's types");
            int files = first.WorkedOut;
            Assert.Equal(0, Run().WorkedOut);   // nothing changed: nothing is worked out again

            // a module others import: its importers, theirs and the star import are worked out again, the others are not
            Write(root, "pkg/base.py", "class Base:\n    weight: int = 2\n    def grow(self, n: str) -> int:\n        return self.weight\n");
            var edited = Run();
            Assert.Equal(FullRun(root, open), edited.Answer);
            Assert.True(edited.WorkedOut > 3 && edited.WorkedOut < files, $"{edited.WorkedOut} of {files} files worked out");

            // a submodule reached as an attribute of its package
            Assert.True(edited.Answer.Contains("through.py"), "the call through the package is reported");
            Write(root, "pkg/sub/leaf.py", "def f(x, y):\n    return x + y\n");
            var leaf = Run();
            Assert.Equal(FullRun(root, open), leaf.Answer);
            Assert.True(!leaf.Answer.Contains("through.py"), "and no longer once the function takes two arguments");

            // the conftest.py the test asks for a fixture from
            Write(root, "tests/conftest.py", "import pytest\n\n@pytest.fixture\ndef other() -> str:\n    return 'one'\n");
            var fixtures = Run();
            Assert.Equal(FullRun(root, open), fixtures.Answer);
            string OfTheTest(string answer) => string.Join("\n", answer.Split('\n').Where(l => l.Contains("test_it.py")));
            Assert.True(OfTheTest(fixtures.Answer) != OfTheTest(leaf.Answer), "the test's findings change with the fixture");

            // an unsaved edit, then a file renamed (its importer no longer finds it) and one deleted
            string user = LanguageService.UriOfPath(Path.Combine(root, "pkg", "user.py"));
            open[user] = "from pkg.base import Base\n\ndef use():\n    return Base().size\n";
            service.Open(user, open[user], 1);
            Assert.Equal(FullRun(root, open), Run().Answer);
            File.Move(Path.Combine(root, "pkg", "old.py"), Path.Combine(root, "pkg", "renamed.py"));
            var renamed = Run();
            Assert.Equal(FullRun(root, open), renamed.Answer);
            Assert.True(!renamed.Answer.Contains("old.py"), "the old name's findings are gone");
            File.Delete(Path.Combine(root, "pkg", "base.py"));
            Assert.Equal(FullRun(root, open), Run().Answer);
            Assert.Equal(0, Run().WorkedOut);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ADjangoFileIsAlwaysWorkedOutAgain()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_mem_" + Guid.NewGuid().ToString("N"));
        Write(root, "app/views.py", "from django.shortcuts import render\n\ndef index(request):\n    return render(request, 'index.html')\n");
        Write(root, "app/plain.py", "def f():\n    return 1\n");
        try
        {
            var service = new LanguageService();
            service.SourceRoots.Add(root);
            service.ForkForAnalysis().Problems(4, 100000);
            var again = service.ForkForAnalysis();
            again.Problems(4, 100000);
            Assert.Equal(1, again.ProblemsWorkedOut);
        }
        finally { Directory.Delete(root, true); }
    }
}
