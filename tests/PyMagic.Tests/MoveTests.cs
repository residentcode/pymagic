using PyMagic.Server;

namespace PyMagic.Tests;

public class MoveTests
{
    const string A = "file:///c:/proj/a.py";
    const string B = "file:///c:/proj/b.py";
    const string C = "file:///c:/proj/c.py";

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Moves the definition at the `|` marker (in the first document that has one) to the target and returns every document's new text.</summary>
    static Dictionary<string, string> Move(string target, params (string Uri, string Text)[] docs)
    {
        var svc = new LanguageService();
        string? cursorUri = null; PyMagic.Server.Position pos = new(0, 0);
        var texts = new Dictionary<string, string>();
        foreach (var (uri, marked) in docs)
        {
            string clean = marked.Replace("|", "");
            if (cursorUri == null && marked.Contains('|')) { cursorUri = uri; pos = new LineMap(clean).PositionOf(marked.IndexOf('|')); }
            svc.Open(uri, clean, 1);
            texts[uri] = clean;
        }
        var result = svc.MoveSymbol(cursorUri!, pos, target);
        foreach (var (uri, edits) in result.Changes)
        {
            var lm = new LineMap(texts[uri]);
            string t = texts[uri];
            foreach (var e in edits.OrderByDescending(e => lm.OffsetOf(e.Range.Start)).ThenByDescending(e => lm.OffsetOf(e.Range.End)))
                t = t[..lm.OffsetOf(e.Range.Start)] + e.NewText + t[lm.OffsetOf(e.Range.End)..];
            texts[uri] = t;
        }
        return texts;
    }

    static void Refuses(string reasonPart, string target, params (string Uri, string Text)[] docs)
    {
        try { Move(target, docs); }
        catch (ServiceException e) { Assert.Contains(reasonPart, e.Message); return; }
        throw new AssertionException("expected a refusal containing: " + reasonPart);
    }

    [Fact]
    public void MovesAFunctionAndRewritesImports()
    {
        var r = Move(B,
            (A, Src("import math", "", "", "def |area(r):", "    return math.pi * r * r", "", "", "def other():", "    return math.e")),
            (B, Src("x = 1")),
            (C, Src("from a import area, other", "", "print(area(1), other())")));
        Assert.Equal(Src("import math", "", "", "def other():", "    return math.e"), r[A]);
        Assert.Equal(Src("import math", "x = 1", "", "", "def area(r):", "    return math.pi * r * r"), r[B]);
        Assert.Equal(Src("from a import other", "from b import area", "", "print(area(1), other())"), r[C]);
    }

    [Fact]
    public void ImportsWhatTheMovedCodeNeeds()
    {
        var r = Move(B,
            (A, Src("from os import path", "import sys", "", "LIMIT = 3", "", "", "def |show(p):", "    print(path.join(p, 'x'), LIMIT)", "    return sys.argv", "")),
            (B, Src("")));
        Assert.Contains("from os import path", r[B]);
        Assert.Contains("import sys", r[B]);
        Assert.Contains("from a import LIMIT", r[B]);
        Assert.True(!(r[A]).Contains("from os import path"), "unexpected: " + "from os import path");
        Assert.True(!(r[A]).Contains("import sys"), "unexpected: " + "import sys");
        Assert.True(!(r[A]).Contains("def show"), "unexpected: " + "def show");
    }

    [Fact]
    public void KeepsAnImportThatOtherCodeStillUses()
    {
        var r = Move(B,
            (A, Src("import json", "", "def |dump(x):", "    return json.dumps(x)", "", "def load(s):", "    return json.loads(s)")),
            (B, Src("y = 2")));
        Assert.Contains("import json", r[A]);
        Assert.Contains("import json", r[B]);
    }

    [Fact]
    public void TheOldFileImportsTheNameItStillUses()
    {
        var r = Move(B,
            (A, Src("def |helper():", "    return 1", "", "def user():", "    return helper() + 1")),
            (B, Src("z = 0")));
        Assert.Contains("from b import helper", r[A]);
        Assert.Contains("return helper() + 1", r[A]);
    }

    [Fact]
    public void MovesAClassWithItsDecoratorsAndKeepsAliases()
    {
        var r = Move(B,
            (A, Src("from dataclasses import dataclass", "", "", "@dataclass", "class |Point:", "    x: int = 0", "")),
            (B, Src("")),
            (C, Src("from a import Point as P", "print(P())")));
        Assert.Contains("@dataclass\nclass Point:", r[B]);
        Assert.Contains("from dataclasses import dataclass", r[B]);
        Assert.Equal(Src("from b import Point as P", "print(P())"), r[C]);
    }

    [Fact]
    public void RewritesOnlyTheMovedNameInAMultiImport()
    {
        var r = Move(B,
            (A, Src("def |one(): return 1", "def two(): return 2")),
            (B, Src("")),
            (C, Src("from a import one, two", "print(one(), two())")));
        Assert.Equal(Src("from a import two", "from b import one", "print(one(), two())"), r[C]);
    }

    [Fact]
    public void MovingIntoTheFileItImportsFromNeedsNoImport()
    {
        var r = Move(B,
            (A, Src("from b import Base", "", "", "def |make():", "    return Base()")),
            (B, Src("class Base:", "    pass")));
        Assert.Equal(Src("class Base:", "    pass", "", "", "def make():", "    return Base()"), r[B]);
        Assert.True(!r[A].Contains("Base"), "the import went");
    }

    [Fact]
    public void RefusesNamesThatExistInTheTarget()
    {
        Refuses("already exists", B, (A, Src("def |f(): pass")), (B, Src("def f(): pass")));
    }

    [Fact]
    public void RefusesAnAttributeUse()
    {
        Refuses("module attribute", B, (A, Src("def |f(): pass")), (B, Src("")), (C, Src("import a", "a.f()")));
    }

    [Fact]
    public void RefusesStarImports()
    {
        Refuses("import *", B, (A, Src("def |f(): pass")), (B, Src("")), (C, Src("from a import *", "f()")));
    }

    [Fact]
    public void RefusesCircularImports()
    {
        // the moved code needs `base` from a, and a still uses the moved name
        Refuses("circular", B,
            (A, Src("def base(): return 1", "", "def |child():", "    return base()", "", "print(child())")),
            (B, Src("")));
    }

    [Fact]
    public void RefusesNamesInStringsAndGlobals()
    {
        Refuses("string", B, (A, Src("__all__ = ['f']", "def |f(): pass")), (B, Src("")));
        Refuses("global", B, (A, Src("n = 0", "def |f():", "    global n", "    n = 1")), (B, Src("")));
    }

    [Fact]
    public void OnlyTopLevelDefinitionsCanMove()
    {
        Refuses("top-level", B, (A, Src("class K:", "    def |m(self): pass")), (B, Src("")));
    }

    [Fact]
    public void TheMenuOffersTheMove()
    {
        var svc = new LanguageService();
        svc.Open(A, Src("def f(): pass"), 1);
        svc.Open(B, Src(""), 1);
        var options = svc.RefactorOptions(A, new PyMagic.Server.Range(new PyMagic.Server.Position(0, 5), new PyMagic.Server.Position(0, 5)));
        Assert.True(options.Any(o => o.Kind == "moveSymbol"), "menu entry");
        Assert.Equal(B, string.Join(",", svc.MoveTargets(A).Select(t => t.Uri)));
    }
}
