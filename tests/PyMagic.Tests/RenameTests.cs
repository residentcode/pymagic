using PyMagic.Server;

namespace PyMagic.Tests;

public class RenameTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    static string Strip(string marked) => marked.Replace("|", "");

    static PyMagic.Server.Position PosOf(string marked)
    {
        int i = marked.IndexOf('|');
        return new LineMap(marked.Remove(i, 1)).PositionOf(i);
    }

    /// <summary>Opens the documents (the one containing `|` is the cursor document) and renames at the cursor; returns the new text per document.</summary>
    static Dictionary<string, string> Rename(string newName, params (string Uri, string Text)[] docs)
    {
        var svc = new LanguageService();
        string? cursorUri = null; PyMagic.Server.Position pos = new(0, 0);
        var texts = new Dictionary<string, string>();
        foreach (var (uri, text) in docs)
        {
            if (text.Contains('|')) { cursorUri = uri; pos = PosOf(text); }
            svc.Open(uri, Strip(text), 1);
            texts[uri] = Strip(text);
        }
        var edit = svc.Rename(cursorUri!, pos, newName);
        foreach (var (uri, edits) in edit.Changes)
        {
            var lines = new LineMap(texts[uri]);
            string t = texts[uri];
            foreach (var e in edits.OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
            {
                int s = lines.OffsetOf(e.Range.Start), end = lines.OffsetOf(e.Range.End);
                t = t[..s] + e.NewText + t[end..];
            }
            texts[uri] = t;
        }
        return texts;
    }

    static string Rename1(string newName, string marked) => Rename(newName, (Main, marked))[Main];

    static int RefCount(string marked, bool includeDeclaration = true)
    {
        var svc = new LanguageService();
        svc.Open(Main, Strip(marked), 1);
        return svc.References(Main, PosOf(marked), includeDeclaration).Count;
    }

    static void Throws(Action a, string messagePart)
    {
        try { a(); }
        catch (ServiceException e) { Assert.Contains(messagePart, e.Message); return; }
        throw new AssertionException("expected a ServiceException containing: " + messagePart);
    }

    [Fact]
    public void ReferencesOfLocalVariable()
    {
        const string src = "val|ue = 1\nprint(value)\nvalue += 1\n";
        Assert.Equal(3, RefCount(src));
        Assert.True(RefCount(src, false) < RefCount(src), "declaration should be excluded");
    }

    [Fact]
    public void RenameFunctionLeavesShadowingParametersAlone()
    {
        Assert.Equal("def run(): ...\ndef other(helper):\n    return helper\nrun()\n",
            Rename1("run", "def hel|per(): ...\ndef other(helper):\n    return helper\nhelper()\n"));
    }

    [Fact]
    public void RenameParameterOnlyInsideItsFunction()
    {
        Assert.Equal("def f(count):\n    return count + 1\ncount = 2\n",
            Rename1("count", "def f(nu|m):\n    return num + 1\ncount = 2\n"));
    }

    [Fact]
    public void RenameMethodFollowsTheReceiverType()
    {
        const string src = "class C:\n    def bu|mp(self): ...\nclass D:\n    def bump(self): ...\nC().bump()\nD().bump()\nc = C()\nc.bump()\n";
        Assert.Equal("class C:\n    def step(self): ...\nclass D:\n    def bump(self): ...\nC().step()\nD().bump()\nc = C()\nc.step()\n",
            Rename1("step", src));
    }

    [Fact]
    public void RenameFromAUseOfTheMethod()
    {
        const string src = "class C:\n    def bump(self): ...\nc = C()\nc.bu|mp()\n";
        Assert.Equal("class C:\n    def step(self): ...\nc = C()\nc.step()\n", Rename1("step", src));
    }

    [Fact]
    public void RenameInstanceAttributeAcrossMethodsAndUses()
    {
        const string src = "class C:\n    def __init__(self):\n        self.cou|nt = 0\n    def bump(self):\n        self.count += 1\nc = C()\nprint(c.count)\n";
        Assert.Equal("class C:\n    def __init__(self):\n        self.total = 0\n    def bump(self):\n        self.total += 1\nc = C()\nprint(c.total)\n",
            Rename1("total", src));
    }

    [Fact]
    public void RenameAcrossModulesThroughImports()
    {
        var r = Rename("build",
            (Lib, "def make():\n    return 1\n"),
            (Main, "from lib import ma|ke\nmake()\nprint(make)\n"));
        Assert.Equal("def build():\n    return 1\n", r[Lib]);
        Assert.Equal("from lib import build\nbuild()\nprint(build)\n", r[Main]);
    }

    [Fact]
    public void RenameFromAUseOfAnImportedNameReachesTheDefinition()
    {
        var r = Rename("build",
            (Lib, "def make():\n    return 1\n"),
            (Main, "from lib import make\nma|ke()\nprint(make)\n"));
        Assert.Equal("def build():\n    return 1\n", r[Lib]);
        Assert.Equal("from lib import build\nbuild()\nprint(build)\n", r[Main]);

        // an alias keeps its own name: renaming the alias does not touch the original
        var alias = Rename("shorter",
            (Lib, "def make():\n    return 1\n"),
            (Main, "from lib import make as mk\nm|k()\n"));
        Assert.True(!alias.ContainsKey(Lib) || alias[Lib] == "def make():\n    return 1\n");
        Assert.Equal("from lib import make as shorter\nshorter()\n", alias[Main]);
    }

    [Fact]
    public void RenameFromTheDefinitionReachesImporters()
    {
        var r = Rename("build",
            (Lib, "def ma|ke():\n    return 1\n"),
            (Main, "from lib import make\nimport lib\nmake()\nlib.make()\n"));
        Assert.Equal("def build():\n    return 1\n", r[Lib]);
        Assert.Equal("from lib import build\nimport lib\nbuild()\nlib.build()\n", r[Main]);
    }

    [Fact]
    public void ImportAliasKeepsItsLocalName()
    {
        var r = Rename("build",
            (Lib, "def ma|ke():\n    return 1\n"),
            (Main, "from lib import make as mk\nmk()\n"));
        Assert.Equal("def build():\n    return 1\n", r[Lib]);
        Assert.Equal("from lib import build as mk\nmk()\n", r[Main]);
    }

    [Fact]
    public void RenamingAnAliasOnlyTouchesTheAlias()
    {
        var r = Rename("shorter",
            (Lib, "def make():\n    return 1\n"),
            (Main, "from lib import make as m|k\nmk()\n"));
        Assert.True(!r.ContainsKey(Lib) || r[Lib] == "def make():\n    return 1\n");
        Assert.Equal("from lib import make as shorter\nshorter()\n", r[Main]);
    }

    [Fact]
    public void RenameClassUpdatesAnnotationsAndBases()
    {
        Assert.Equal("class Node: ...\nclass Leaf(Node): ...\ndef f(n: Node) -> Node:\n    return Node()\n",
            Rename1("Node", "class Ba|se: ...\nclass Leaf(Base): ...\ndef f(n: Base) -> Base:\n    return Base()\n"));
    }

    [Fact]
    public void ReferencesSpanAllOpenFiles()
    {
        var svc = new LanguageService();
        svc.Open(Lib, "def make(): ...\n", 1);
        svc.Open(Main, "from lib import make\nmake()\n", 1);
        var refs = svc.References(Lib, new PyMagic.Server.Position(0, 5), true);
        Assert.Equal(3, refs.Count);   // the def, the import name and the call
        Assert.True(refs.Any(r => r.Uri == Lib) && refs.Any(r => r.Uri == Main));
    }

    [Fact]
    public void RefusesLibrarySymbolsKeywordsAndClashes()
    {
        var svc = new LanguageService();
        svc.Open(Main, "len([1])\nx = 1\ny = 2\n", 1);
        Assert.True(svc.PrepareRename(Main, new PyMagic.Server.Position(0, 1)) == null, "builtins resolve to nothing renameable");
        Throws(() => svc.Rename(Main, new PyMagic.Server.Position(1, 0), "class"), "valid identifier");
        Throws(() => svc.Rename(Main, new PyMagic.Server.Position(1, 0), "not valid"), "valid identifier");
        Throws(() => svc.Rename(Main, new PyMagic.Server.Position(1, 0), "y"), "already defined");
    }

    [Fact]
    public void PrepareRenameReturnsTheIdentifier()
    {
        var svc = new LanguageService();
        svc.Open(Main, "def helper(): ...\nhelper()\n", 1);
        var p = svc.PrepareRename(Main, new PyMagic.Server.Position(1, 3))!;
        Assert.Equal("helper", p.Placeholder);
        Assert.Equal(new PyMagic.Server.Range(new PyMagic.Server.Position(1, 0), new PyMagic.Server.Position(1, 6)), p.Range);
    }

    [Fact]
    public void ProjectFilesOnDiskAreSearchedToo()
    {
        string root = Path.Combine(Path.GetTempPath(), "pm_ref_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "other.py"), "from main import compute\nprint(compute())\n");
            var svc = new LanguageService();
            svc.SourceRoots.Add(root);
            string uri = LanguageService.UriOfPath(Path.Combine(root, "main.py"));
            svc.Open(uri, "def compute():\n    return 1\n", 1);
            var edit = svc.Rename(uri, new PyMagic.Server.Position(0, 5), "calc");
            Assert.Equal(2, edit.Changes.Count);
            Assert.True(edit.Changes.Keys.Any(k => k.EndsWith("other.py")), "the unopened file is part of the rename");
        }
        finally { Directory.Delete(root, true); }
    }
}
