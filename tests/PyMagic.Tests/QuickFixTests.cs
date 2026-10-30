using System.Text.Json.Nodes;
using PyMagic.Server;

namespace PyMagic.Tests;

public class QuickFixTests
{
    const string Main = "file:///c:/proj/main.py";
    const string Lib = "file:///c:/proj/lib.py";

    static PyMagic.Server.Range Whole(string text)
    {
        var lm = new LineMap(text);
        return lm.RangeOf(0, text.Length);
    }

    static string Apply(string text, WorkspaceEdit edit, string uri)
    {
        var lines = new LineMap(text);
        foreach (var e in edit.Changes[uri].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    static List<CodeAction> Actions(string src, params (string Uri, string Text)[] others)
    {
        var svc = new LanguageService();
        foreach (var (u, t) in others) svc.Open(u, t, 1);
        svc.Open(Main, src, 1);
        return svc.CodeActions(Main, Whole(src)).ToList();
    }

    /// <summary>The text after applying the first action whose title starts with `title`.</summary>
    static string Fix(string title, string src, params (string Uri, string Text)[] others)
    {
        var all = Actions(src, others);
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        return Apply(src, a.Edit, Main);
    }

    static bool Offers(string title, string src, params (string Uri, string Text)[] others) =>
        Actions(src, others).Any(x => x.Title.StartsWith(title));

    // ------------------------------------------------------------ imports

    [Fact]
    public void ImportsAProjectSymbol()
    {
        Assert.Equal("from lib import make\nmake()\n\n".TrimEnd('\n') + "\n",
            Fix("Import 'make' from 'lib'", "make()\n", (Lib, "def make(): ...\n")).Replace("\n\n", "\n"));
    }

    [Fact]
    public void ImportIsPlacedAfterExistingImportsAndDocstring()
    {
        Assert.Equal("\"\"\"Doc.\"\"\"\nimport sys\nfrom lib import make\n\nprint(sys)\nmake()\n",
            Fix("Import 'make' from 'lib'", "\"\"\"Doc.\"\"\"\nimport sys\n\nprint(sys)\nmake()\n", (Lib, "def make(): ...\n")));
    }

    [Fact]
    public void ImportIsMergedIntoAnExistingFromImport()
    {
        Assert.Equal("from lib import other, make\nmake()\nother()\n",
            Fix("Import 'make' from 'lib'", "from lib import other\nmake()\nother()\n", (Lib, "def make(): ...\ndef other(): ...\n")));
    }

    [Fact]
    public void ImportsFromCommonStandardLibraryModules()
    {
        string r = Fix("Import 'Optional' from 'typing'", "def f(x: Optional[int]): ...\n");
        Assert.Contains("from typing import Optional", r);
        Assert.True(r.IndexOf("from typing") < r.IndexOf("def f"), "import goes above the code");
    }

    [Fact]
    public void ImportsAModuleByName()
    {
        string r = Fix("Import module 'collections'", "x = collections.OrderedDict()\n");
        Assert.True(r.StartsWith("import collections\n"), r);
    }

    [Fact]
    public void NoImportOfAnUnknownName()
    {
        Assert.True(!Offers("Import 'zzz_nothing'", "zzz_nothing()\n"));
    }

    // ------------------------------------------------------------ other unresolved-name fixes

    [Fact]
    public void FixesATypoOfAVisibleName()
    {
        Assert.Equal("pritn_all = 1\nprint(1)\n", Fix("Change to 'print'", "pritn_all = 1\npritn(1)\n").Replace("pritn_all", "pritn_all"));
        Assert.Equal("value = 1\nprint(value)\n", Fix("Change to 'value'", "value = 1\nprint(vlaue)\n"));
    }

    [Fact]
    public void AddsSelfToAMemberUsedBare()
    {
        const string src = "class C:\n    def __init__(self):\n        self.count = 0\n    def bump(self):\n        count += 1\n";
        Assert.Equal("class C:\n    def __init__(self):\n        self.count = 0\n    def bump(self):\n        self.count += 1\n",
            Fix("Add 'self.'", src));
        Assert.True(!Offers("Add 'self.'", "class C:\n    @staticmethod\n    def f():\n        count\n"));
    }

    [Fact]
    public void CreatesAFunctionFromACall()
    {
        string r = Fix("Create function 'compute'", "x = 1\nprint(compute(x, 2, key=x))\n");
        Assert.Equal("x = 1\n\n\ndef compute(x, arg1, key):\n    raise NotImplementedError\n\n\nprint(compute(x, 2, key=x))\n", r);
        Assert.True(!Offers("Create function", "x = foo\n"), "only calls can become functions");
    }

    [Fact]
    public void CreatesAClassFromItsFirstUse()
    {
        // a capitalised call: a class whose __init__ keeps what it is given
        Assert.Equal("class Point:\n    def __init__(self, x, y):\n        self.x = x\n        self.y = y\n\n\np = Point(x=1, y=2)\n",
            Fix("Create class 'Point'", "p = Point(x=1, y=2)\n"));
        // a base class, an exception and an isinstance check, whatever the name looks like
        Assert.Equal("class base:\n    pass\n\n\nclass A(base):\n    pass\n", Fix("Create class 'base'", "class A(base):\n    pass\n"));
        Assert.Equal("class NotFound(Exception):\n    pass\n\n\ntry:\n    pass\nexcept NotFound:\n    pass\n",
            Fix("Create class 'NotFound'", "try:\n    pass\nexcept NotFound:\n    pass\n"));
        Assert.True(Offers("Create class 'kind'", "x = 1\nisinstance(x, kind)\n"));
        Assert.True(!Offers("Create class", "x = compute(1)\n"), "a lower-case call is a function");
        Assert.True(!Offers("Create class", "x = URL\n"), "an all-capital name is a constant");
    }

    [Fact]
    public void CreatesAMethodOrAnAttributeInTheProjectsClass()
    {
        const string shapes = "class Shape:\n    def __init__(self, name):\n        self.name = name\n\n    def area(self):\n        return 0\n";
        var actions = Actions("from lib import Shape\ns = Shape('a')\ns.scale(2, factor=3)\nprint(s.colour)\nShape.make(1)\n", (Lib, shapes));
        string Owner(string title) => Apply(shapes, actions.First(a => a.Title == title).Edit!, Lib);
        Assert.Equal(shapes + "\n    def scale(self, arg0, factor):\n        raise NotImplementedError\n", Owner("Create method 'scale' in 'Shape'"));
        Assert.Equal("class Shape:\n    def __init__(self, name):\n        self.name = name\n        self.colour = None\n\n    def area(self):\n        return 0\n",
            Owner("Create attribute 'colour' in 'Shape'"));
        Assert.Equal(shapes + "\n    @classmethod\n    def make(cls, arg0):\n        raise NotImplementedError\n", Owner("Create method 'make' in 'Shape'"));
        // a class with only `pass` loses it; a class of the standard library is not changed
        Assert.Equal("class C:\n    def go(self):\n        raise NotImplementedError\nC().go()\n", Fix("Create method 'go' in 'C'", "class C:\n    pass\nC().go()\n"));
        Assert.True(!Offers("Create method", "'a'.shout()\n"));
    }

    // ------------------------------------------------------------ attributes

    [Fact]
    public void FixesAMisspelledAttribute()
    {
        Assert.Equal("class C:\n    def bump(self): ...\nC().bump()\n",
            Fix("Change to 'bump'", "class C:\n    def bump(self): ...\nC().bumb()\n"));
        Assert.Equal("class C:\n    def __init__(self):\n        self.count = 0\nC().count\n",
            Fix("Change to 'count'", "class C:\n    def __init__(self):\n        self.count = 0\nC().cuont\n"));
    }

    // ------------------------------------------------------------ unreachable code, unused imports

    [Fact]
    public void RemovesUnreachableCode()
    {
        Assert.Equal("def f():\n    return 1\n", Fix("Remove unreachable code", "def f():\n    return 1\n    x = 2\n"));
    }

    [Fact]
    public void ReportsAndRemovesUnusedImports()
    {
        var svc = new LanguageService();
        svc.Open(Main, "import os\nimport sys\nprint(sys)\n", 1);
        var d = svc.Diagnostics(Main).Single();
        Assert.Equal("unused-import", d.Code);
        Assert.Equal(Severity.Hint, d.Severity);
        Assert.Equal(0, d.Range.Start.Line);

        Assert.Equal("import sys\nprint(sys)\n", Fix("Remove unused import 'os'", "import os\nimport sys\nprint(sys)\n"));
        Assert.Equal("from lib import a\nprint(a)\n", Fix("Remove unused import 'b'", "from lib import a, b\nprint(a)\n"));
        Assert.Equal("from lib import b\nprint(b)\n", Fix("Remove unused import 'a'", "from lib import a, b\nprint(b)\n"));
        Assert.Equal("import os\nprint(os)\n", Fix("Remove unused import 'sys'", "import os, sys\nprint(os)\n").Replace("import os\nprint", "import os\nprint"));
    }

    [Fact]
    public void LastStatementOfABlockBecomesPass()
    {
        Assert.Equal("try:\n    pass\nexcept ImportError:\n    pass\n",
            Fix("Remove unused import 'x'", "try:\n    import x\nexcept ImportError:\n    pass\n"));
    }

    [Fact]
    public void OptimizeImportsRemovesEverythingUnused()
    {
        const string src = "import os\nfrom a import b, c, d\nimport sys\nprint(c, sys)\n";
        var all = Actions(src);
        var opt = all.Single(a => a.Title == "Optimize imports");
        Assert.Equal(LanguageService.OrganizeImportsKind, opt.Kind);
        // the unused ones go and the rest is sorted into sections (standard library first)
        Assert.Equal("import sys\n\nfrom a import c\nprint(c, sys)\n", Apply(src, opt.Edit, Main));
    }

    [Fact]
    public void ImportsUsedInStringsOrReExportedAreKept()
    {
        var svc = new LanguageService();
        svc.Open(Main, "from typing import TYPE_CHECKING\nif TYPE_CHECKING:\n    from lib import Thing\ndef f(x: \"Thing\"): ...\n__all__ = [\"TYPE_CHECKING\"]\nfrom __future__ import annotations\n", 1);
        Assert.True(!svc.Diagnostics(Main).Any(d => d.Code == "unused-import"), "string annotations, __all__ and __future__ count as uses");

        svc.Open("file:///c:/proj/pkg/__init__.py", "from .mod import thing\n", 1);
        Assert.True(!svc.Diagnostics("file:///c:/proj/pkg/__init__.py").Any(d => d.Code == "unused-import"), "package modules re-export");
    }

    // ------------------------------------------------------------ protocol

    [Fact]
    public void CodeActionRequestReturnsEdits()
    {
        var sent = new List<JsonNode>();
        var server = new PyMagic.Server.Server(new LanguageService(), sent.Add);
        server.Handle(JsonNode.Parse("""{"jsonrpc":"2.0","method":"textDocument/didOpen","params":{"textDocument":{"uri":"file:///c:/proj/main.py","languageId":"python","version":1,"text":"import os\nprint(1)\n"}}}""")!);
        server.Handle(JsonNode.Parse("""{"jsonrpc":"2.0","id":9,"method":"textDocument/codeAction","params":{"textDocument":{"uri":"file:///c:/proj/main.py"},"range":{"start":{"line":0,"character":7},"end":{"line":0,"character":9}},"context":{"diagnostics":[]}}}""")!);
        var result = sent.Last(m => m["id"]?.GetValue<int>() == 9)["result"]!.AsArray();
        var fix = result.First(a => a!["title"]!.GetValue<string>().StartsWith("Remove unused import"))!;
        Assert.Equal("quickfix", fix["kind"]!.GetValue<string>());
        Assert.True(fix["isPreferred"]!.GetValue<bool>());
        Assert.Equal(1, fix["edit"]!["changes"]!["file:///c:/proj/main.py"]!.AsArray().Count);

        var diags = sent.First(m => m["method"]?.GetValue<string>() == "textDocument/publishDiagnostics")["params"]!["diagnostics"]!.AsArray();
        Assert.Equal("unused-import", diags[0]!["code"]!.GetValue<string>());
    }
}
