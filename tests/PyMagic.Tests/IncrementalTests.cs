using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using PyMagic.Core.Semantics;
using PyMagic.Core.Types;
using PyMagic.Server;

namespace PyMagic.Tests;

public class IncrementalTests
{
    // ------------------------------------------------------------ the module cache

    [Fact]
    public void LibraryModulesAreSharedBetweenWorkspaces()
    {
        var cache = new AnalysisCache();
        var first = new Workspace(new WorkspaceOptions(), cache);
        var builtins = first.Builtins;
        int missesAfterFirst = cache.Misses;
        Assert.True(missesAfterFirst > 0);

        var second = new Workspace(new WorkspaceOptions(), cache);
        Assert.True(ReferenceEquals(builtins, second.Builtins), "builtins is parsed once");
        Assert.Equal(missesAfterFirst, cache.Misses);
        Assert.True(cache.Hits > 0);

        // each workspace still has its own evaluator: nothing evaluated in one leaks into the other
        Assert.True(!ReferenceEquals(first.Evaluator, second.Evaluator));
    }

    [Fact]
    public void BufferIsReusedUntilItsTextChanges()
    {
        var cache = new AnalysisCache();
        var ws = new Workspace(new WorkspaceOptions(), cache);
        var a = ws.AddModule("m", "x = 1\n");
        Assert.True(ReferenceEquals(a, new Workspace(new WorkspaceOptions(), cache).AddModule("m", "x = 1\n")));
        var b = new Workspace(new WorkspaceOptions(), cache).AddModule("m", "x = 2\n");
        Assert.True(!ReferenceEquals(a, b));
        Assert.Equal("x = 2\n", b.Source);

        var uncached = new Workspace(new WorkspaceOptions(), cache).AddModule("m", "x = 3\n", cached: false);
        var again = new Workspace(new WorkspaceOptions(), cache).AddModule("m", "x = 2\n");
        Assert.True(ReferenceEquals(b, again), "a throw-away variant does not evict the cached buffer");
        Assert.Equal("x = 3\n", uncached.Source);
    }

    [Fact]
    public void FilesAreReparsedWhenTheyChangeOnDisk()
    {
        string dir = Path.Combine(Path.GetTempPath(), "pm_cache_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string file = Path.Combine(dir, "lib.py");
            File.WriteAllText(file, "VALUE = 1\n");
            var cache = new AnalysisCache();
            var options = new WorkspaceOptions { SourceRoots = new[] { dir } };

            var first = new Workspace(options, cache).GetModule("lib")!;
            Assert.True(ReferenceEquals(first, new Workspace(options, cache).GetModule("lib")), "unchanged file: same module");

            File.WriteAllText(file, "VALUE = 22\n");
            File.SetLastWriteTimeUtc(file, DateTime.UtcNow.AddMinutes(1));
            var changed = new Workspace(options, cache).GetModule("lib")!;
            Assert.True(!ReferenceEquals(first, changed));
            Assert.Equal("VALUE = 22\n", changed.Source);
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void ChangingTheTargetVersionDropsTheCache()
    {
        var cache = new AnalysisCache();
        var modern = new Workspace(new WorkspaceOptions { Environment = new PythonEnvironment(3, 13, "win32") }, cache).Builtins;
        var old = new Workspace(new WorkspaceOptions { Environment = new PythonEnvironment(3, 8, "linux") }, cache).Builtins;
        Assert.True(!ReferenceEquals(modern, old), "static conditions differ per version, so the parsed module must too");
    }

    // ------------------------------------------------------------ the language service on top of it

    const string A = "file:///c:/proj/a.py";
    const string B = "file:///c:/proj/b.py";
    const string C = "file:///c:/proj/c.py";
    const string D = "file:///c:/proj/d.py";

    [Fact]
    public void AnEditReparsesOnlyTheEditedFile()
    {
        var svc = new LanguageService();
        svc.Open(A, "def f() -> int:\n    return 1\n", 1);
        svc.Open(B, "from a import f\nx = f()\n", 1);
        _ = svc.Diagnostics(B);
        _ = svc.Hover(B, new PyMagic.Server.Position(1, 0));
        int before = svc.Cache.Misses;

        svc.Change(A, "def f() -> str:\n    return ''\n", 2);
        _ = svc.Diagnostics(B);
        var hover = svc.Hover(B, new PyMagic.Server.Position(1, 0))!;
        Assert.Equal(1, svc.Cache.Misses - before);                       // only a.py was parsed again
        Assert.Contains("x: str", hover.Contents.Value);                  // and the dependent sees the new type, not a stale one
    }

    [Fact]
    public void EvaluationNeverUsesTypesFromBeforeAnEdit()
    {
        var svc = new LanguageService();
        svc.Open(A, "class K:\n    size: int = 1\n", 1);
        svc.Open(B, "from a import K\nK().size\nK().name\n", 1);
        Assert.True(svc.Diagnostics(B).Any(d => d.Message.Contains("name")), "name does not exist yet");

        svc.Change(A, "class K:\n    size: int = 1\n    name: str = ''\n", 2);
        Assert.True(!svc.Diagnostics(B).Any(d => d.Message.Contains("name")), "the new attribute is visible immediately");
    }

    [Fact]
    public void DependentsFollowImportsThroughUnopenedFiles()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dep_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "mid.py"), "from leaf import g\n");   // not open
            var svc = new LanguageService();
            svc.SourceRoots.Add(root);
            string top = LanguageService.UriOfPath(Path.Combine(root, "top.py"));
            string leaf = LanguageService.UriOfPath(Path.Combine(root, "leaf.py"));
            string other = LanguageService.UriOfPath(Path.Combine(root, "other.py"));
            svc.Open(top, "from mid import g\n", 1);
            svc.Open(leaf, "def g(): ...\n", 1);
            svc.Open(other, "x = 1\n", 1);

            var dependents = svc.DependentsOf(new[] { leaf });
            Assert.Equal(top, dependents.Single());
            Assert.Equal(0, svc.DependentsOf(new[] { other }).Count);
            Assert.Equal(0, svc.DependentsOf(new[] { top }).Count);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public void DependentsFromTheIndexAreTheSameAsFromTheWholeProject()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_dep2_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "pkg"));
        try
        {
            File.WriteAllText(Path.Combine(root, "pkg", "__init__.py"), "");
            File.WriteAllText(Path.Combine(root, "pkg", "mid.py"), "from . import leaf\nfrom .leaf import g\n");        // relative imports, not open
            File.WriteAllText(Path.Combine(root, "pkg", "far.py"), "import pkg.mid\n");                                   // not open, imports the one above
            File.WriteAllText(Path.Combine(root, "pkg", "alone.py"), "x = 1\n");
            string Uri(string rel) => LanguageService.UriOfPath(Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar)));

            LanguageService Make(bool indexed)
            {
                var svc = new LanguageService();
                svc.SourceRoots.Add(root);
                if (indexed) svc.StartIndexing()!.Wait();
                svc.Open(Uri("pkg/leaf.py"), "def g(): ...\n", 1);
                svc.Open(Uri("top.py"), "import pkg.far\n", 1);
                svc.Open(Uri("pkg/other.py"), "from .alone import x\n", 1);
                return svc;
            }
            var plain = Make(false);
            var indexed = Make(true);
            foreach (var changed in new[] { "pkg/leaf.py", "top.py", "pkg/other.py" })
            {
                var a = plain.DependentsOf(new[] { Uri(changed) }).OrderBy(x => x).ToList();
                var b = indexed.DependentsOf(new[] { Uri(changed) }).OrderBy(x => x).ToList();
                Assert.Equal(string.Join("|", a), string.Join("|", b));
            }
            Assert.Equal(Uri("top.py"), indexed.DependentsOf(new[] { Uri("pkg/leaf.py") }).Single());   // top -> far -> mid -> leaf, through three files nobody has open
        }
        finally { Directory.Delete(root, true); }
    }

    // ------------------------------------------------------------ the server loop

    sealed class Wire
    {
        public readonly List<JsonNode> Sent = new();
        public readonly PyMagic.Server.Server Server;
        public Wire() { Server = new PyMagic.Server.Server(new LanguageService(), m => { lock (Sent) Sent.Add(m); }); }
        public void Send(string json) => Server.Handle(JsonNode.Parse(json)!);
        public List<JsonNode> Published(string uri) => Sent.Where(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics"
            && m["params"]!["uri"]!.GetValue<string>() == uri).ToList();
        public void Open(string uri, string text) => Send(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + uri +
            "\",\"languageId\":\"python\",\"version\":1,\"text\":" + Quote(text) + "}}}");
        public void Change(string uri, string text, int version = 2) => Send(
            "{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri +
            "\",\"version\":" + version + "},\"contentChanges\":[{\"text\":" + Quote(text) + "}]}}");
        static string Quote(string s) => JsonValue.Create(s)!.ToJsonString();
    }

    static string ChangeJson(string uri, int version, string text = "x") =>
        "{\"method\":\"textDocument/didChange\",\"params\":{\"textDocument\":{\"uri\":\"" + uri + "\",\"version\":" + version +
        "},\"contentChanges\":[{\"text\":" + JsonValue.Create(text)!.ToJsonString() + "}]}}";

    [Fact]
    public void CoalesceDropsOnlySupersededEditsWithNoRequestBetween()
    {
        JsonNode Change(string uri, int v) => JsonNode.Parse(ChangeJson(uri, v))!;
        JsonNode Request(int id) => JsonNode.Parse("{\"id\":" + id + ",\"method\":\"textDocument/hover\",\"params\":{}}")!;

        var kept = PyMagic.Server.Server.Coalesce(new[] { Change(A, 1), Change(A, 2), Request(1), Change(A, 3), Change(B, 1), Change(A, 4) });
        string Describe(JsonNode m) => m["id"] != null ? "req"
            : m["params"]!["textDocument"]!["uri"]!.GetValue<string>()[^4..] + m["params"]!["textDocument"]!["version"];
        Assert.Equal("a.py2,req,b.py1,a.py4", string.Join(",", kept.Select(Describe)));
    }

    [Fact]
    public void ABurstOfEditsIsAnalysedOnce()
    {
        var wire = new Wire();
        var inbox = new BlockingCollection<JsonNode?>();
        void Queue(string json) => inbox.Add(JsonNode.Parse(json));
        Queue("{\"method\":\"textDocument/didOpen\",\"params\":{\"textDocument\":{\"uri\":\"" + A + "\",\"languageId\":\"python\",\"version\":1,\"text\":\"x = 1\\n\"}}}");
        for (int i = 2; i <= 6; i++) Queue(ChangeJson(A, i, "print(missing" + i + ")\n"));
        Queue("{\"id\":1,\"method\":\"textDocument/hover\",\"params\":{\"textDocument\":{\"uri\":\"" + A + "\"},\"position\":{\"line\":0,\"character\":1}}}");
        inbox.Add(null);

        wire.Server.Pump(inbox);
        var published = wire.Published(A);
        Assert.Equal(1, published.Count);
        Assert.Contains("missing6", published[0]["params"]!["diagnostics"]![0]!["message"]!.GetValue<string>());
        Assert.Equal(6, published[0]["params"]!["version"]!.GetValue<int>());

        // the hover was answered before the diagnostics were computed and sent
        int reply = wire.Sent.FindIndex(m => m["id"]?.GetValue<int>() == 1);
        int diag = wire.Sent.FindIndex(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics");
        Assert.True(reply >= 0 && reply < diag, "reply at " + reply + ", diagnostics at " + diag);
    }

    [Fact]
    public void OnlyChangedDiagnosticsAreRepublished()
    {
        var wire = new Wire();
        wire.Open(C, "def f(): ...\n");
        wire.Open(B, "from c import f\nf()\n");
        wire.Open(D, "print(1)\n");
        Assert.Equal(1, wire.Published(C).Count);
        Assert.Equal(1, wire.Published(B).Count);
        Assert.Equal(1, wire.Published(D).Count);

        // the change makes the call in b wrong: b is re-analysed and republished, the unrelated d is not touched
        wire.Change(C, "def f(x): ...\n");
        Assert.Equal(2, wire.Published(B).Count);
        Assert.True(wire.Published(B).Last()["params"]!["diagnostics"]!.AsArray().Count > 0);
        Assert.Equal(1, wire.Published(D).Count);

        // an edit that leaves the diagnostics as they were publishes nothing
        int before = wire.Sent.Count;
        wire.Change(D, "print(2)\n");
        Assert.Equal(before, wire.Sent.Count);

        // closing c makes b see the (missing) file on disk again
        wire.Send("{\"jsonrpc\":\"2.0\",\"method\":\"textDocument/didClose\",\"params\":{\"textDocument\":{\"uri\":\"" + C + "\"}}}");
        Assert.True(wire.Published(B).Count >= 3, "dependents are refreshed when a file is closed");
    }
}
