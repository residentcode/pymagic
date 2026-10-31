using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The project's problems computed by several analyses at once are exactly those of one analysis, and the shared parsed modules survive being read from many threads.</summary>
public class ParallelAnalysisTests
{
    static string BigProject(int files)
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_par_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "");
        for (int i = 0; i < files; i++)
        {
            string previous = i == 0 ? "" : $"from pkg.mod{i - 1} import Thing{i - 1}, helper{i - 1}\n";
            string text = previous +
                "import os\n" +                                          // unused import
                $"class Thing{i}:\n" +
                "    size: int = 1\n" +
                "    def grow(self, amount):\n" +
                "        unused = amount\n" +                           // unused variable
                "        return self.size + self.missing_attribute\n" + // unknown attribute
                $"def helper{i}(x, y=1):\n" +
                "    return undefined_name + x\n" +                     // unresolved reference
                (i == 0 ? "" : $"def use{i}():\n    t = Thing{i - 1}()\n    t.nope\n    return helper{i - 1}(1, 2, 3)\n") +   // missing attribute, too many arguments
                (i % 7 == 0 ? "try:\n    pass\nexcept Exception:\n    pass\n" : "") +
                (i % 5 == 0 ? "def f(l): return l\n" : "");
            File.WriteAllText(Path.Combine(root, "pkg", $"mod{i}.py"), text);
        }
        return root;
    }

    static string Key(ProblemEntry p) => $"{p.Uri}|{p.Line}|{p.Column}|{p.Severity}|{p.Code}|{p.Message}";

    [Fact]
    public void ProblemsFromSeveralWorkersAreThoseOfOne()
    {
        string root = BigProject(220);
        try
        {
            var svc = new LanguageService { RememberProblems = false };   // each run works every file out
            svc.SourceRoots.Add(root);
            string open = LanguageService.UriOfPath(Path.Combine(root, "pkg", "mod3.py"));
            svc.Open(open, File.ReadAllText(Path.Combine(root, "pkg", "mod3.py")) + "\nvalue = unresolved_here\n", 1);   // an unsaved edit counts too

            var one = svc.Problems(4, 100000, workers: 1);
            var four = svc.Problems(4, 100000, workers: 4);
            Assert.True(one.Count > 600, "the project has plenty of findings: " + one.Count);
            Assert.Equal(one.Count, four.Count);
            Assert.Equal(string.Join("\n", one.Select(Key)), string.Join("\n", four.Select(Key)));
            Assert.True(four.Any(p => p.Message.Contains("unresolved_here")), "the open buffer's text was analysed, not the file's");
            Assert.True(four.Any(p => p.Code == "unresolved-reference"), "findings that need other modules' types");
            Assert.True(four.Any(p => p.Code == "call-arguments"), "calls checked against another module's function");
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheDefaultUsesWorkersOnlyForBigProjects()
    {
        string root = BigProject(20);
        try
        {
            var svc = new LanguageService { RememberProblems = false };   // each run works every file out
            svc.SourceRoots.Add(root);
            Assert.Equal(string.Join("\n", svc.Problems(4, 100000, 1).Select(Key)), string.Join("\n", svc.Problems(4, 100000).Select(Key)));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void ModulesParsedOnSeveralThreadsAreTheSameAsThoseParsedOnOne()
    {
        string root = BigProject(120);
        try
        {
            var ws1 = new PyMagic.Core.Types.Workspace(new PyMagic.Core.Types.WorkspaceOptions { SourceRoots = new[] { root } });
            var files = Directory.EnumerateFiles(Path.Combine(root, "pkg"), "mod*.py").Select(f => ("pkg." + Path.GetFileNameWithoutExtension(f), f, false)).ToList();
            Assert.Equal(120, ws1.Preload(files));
            Assert.Equal(0, ws1.Preload(files));   // nothing changed: nothing parsed again
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void TheProblemsRequestIsAnsweredBesideTheLoopFromACopyOfTheDocuments()
    {
        var sent = new List<System.Text.Json.Nodes.JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), m => { lock (sent) sent.Add(m); }) { Deferred = true };
        void Send(string json) => server.Handle(System.Text.Json.Nodes.JsonNode.Parse(json)!);
        Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/a.py","languageId":"python","version":1,"text":"print(undefined_here)\n"}}}""");
        Send("""{"jsonrpc":"2.0","id":7,"method":"pymagic/problems","params":{"leastSevere":3}}""");

        // the loop is free at once: a request sent after it is answered while the problems are still being worked out, or at least independently of them
        Send("""{"jsonrpc":"2.0","id":8,"method":"textDocument/hover","params":{"textDocument":{"uri":"file:///c:/proj/a.py"},"position":{"line":0,"character":2}}}""");
        lock (sent) Assert.True(sent.Any(m => m["id"]?.GetValue<int>() == 8), "the hover was answered without waiting");

        for (int i = 0; i < 300; i++)
        {
            lock (sent) if (sent.Any(m => m["id"]?.GetValue<int>() == 7)) break;
            Thread.Sleep(100);
        }
        System.Text.Json.Nodes.JsonNode? answer;
        lock (sent) answer = sent.FirstOrDefault(m => m["id"]?.GetValue<int>() == 7);
        Assert.True(answer != null, "the problems arrived");
        Assert.True(answer!["result"]!.AsArray().Any(e => e!["message"]!.GetValue<string>().Contains("undefined_here")), "with the unsaved document's finding");
    }

    [Fact]
    public void ANewerProblemsRequestSupersedesAnOlderOneThatIsStillWaiting()
    {
        var sent = new List<System.Text.Json.Nodes.JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), m => { lock (sent) sent.Add(m); }) { Deferred = true };
        void Send(string json) => server.Handle(System.Text.Json.Nodes.JsonNode.Parse(json)!);
        Send("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/a.py","languageId":"python","version":1,"text":"print(undefined_here)\n"}}}""");
        for (int id = 20; id < 25; id++) Send("{\"jsonrpc\":\"2.0\",\"id\":" + id + ",\"method\":\"pymagic/problems\",\"params\":{\"leastSevere\":3}}");
        for (int i = 0; i < 300; i++)
        {
            lock (sent) if (sent.Count(m => m["id"]?.GetValue<int>() is >= 20 and < 25) == 5) break;
            Thread.Sleep(100);
        }
        lock (sent)
        {
            var answers = sent.Where(m => m["id"]?.GetValue<int>() is >= 20 and < 25).ToList();
            Assert.Equal(5, answers.Count);
            var last = answers.Single(m => m["id"]!.GetValue<int>() == 24);
            Assert.True(last["result"]!.AsArray().Count > 0, "the newest one has the real answer");
        }
    }
}
