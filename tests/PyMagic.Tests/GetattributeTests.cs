using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A class with a __getattribute__ of its own can answer any attribute name, like one with __getattr__.</summary>
public class GetattributeTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void AnyNameIsFoundAndHasTheReturnType()
    {
        const string src = "class Proxy:\n    def __getattribute__(self, name: str) -> str:\n        return name\n\n\np = Proxy()\np.anything\n";
        Assert.Equal(0, Found(src).Count);
        Assert.Equal("str", Last(src));
        Assert.Equal("int", Last("class Dyn:\n    def __getattribute__(self, name):\n        return 1\n\n\nDyn().whatever\n"));
    }

    [Fact]
    public void ASubclassInheritsIt()
    {
        var found = Found("class Base:\n    def __getattribute__(self, name):\n        return 1\n\n\nclass Sub(Base):\n    pass\n\n\nSub().anything\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void ThreadLocalTakesAnyAttribute()
    {
        // found in the library scan: requests' digest auth keeps its state on a threading.local (12 reports), click 2 more
        var found = Found("import threading\n\n\nstate = threading.local()\nstate.chal = {}\nstate.chal.get('realm')\nstate.nonce_count += 1\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void ObjectsOwnDoesNotCount()
    {
        var found = Found("class K:\n    x = 1\n\n\nK().x\nK().missing\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("missing", found[0]);
    }
}
