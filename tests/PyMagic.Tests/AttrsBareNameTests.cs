using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>attrs classes declared with a bare `@attrs(...)` / `@define` (attrs' own modules do this), and what their `x = attrib()` fields hold.</summary>
public class AttrsBareNameTests
{
    // attr/_make.py in miniature: a plain def named attrs, and attrib returning the counting object
    const string Make =
        "class _CountingAttr:\n    counter = 0\n\n\n" +
        "def attrib(default=None, validator=None, repr=True, init=True, type=None, factory=None, kw_only=False):\n    return _CountingAttr()\n\n\n" +
        "def attrs(maybe_cls=None, repr=None, slots=False, frozen=False, unsafe_hash=None, init=None, kw_only=False):\n    def wrap(cls):\n        return cls\n    return wrap if maybe_cls is None else wrap(maybe_cls)\n\n\n" +
        "define = attrs\nfrozen = attrs\nmutable = attrs\nattributes = attrs\n";

    static Workspace Setup(string src, string module)
    {
        var ws = new Workspace();
        ws.AddModule("attr", "", isPackage: true);
        ws.AddModule("attr._make", module == "attr._make" ? Make + "\n\n" + src : Make);
        ws.AddModule("attrs", "from attr._make import attrib as field, define, frozen, mutable\n", isPackage: true);
        ws.AddModule("mylib", Make);
        return ws;
    }

    static string Last(string src, string module = "main")
    {
        var ws = Setup(src, module);
        var m = module == "attr._make" ? ws.GetModule("attr._make")! : ws.AddModule(module, src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src, string module = "main")
    {
        var ws = Setup(src, module);
        var m = module == "attr._make" ? ws.GetModule("attr._make")! : ws.AddModule(module, src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    const string V = "@attrs(repr=False, slots=True)\nclass V:\n    pattern = attrib()\n    n = attrib(type=int)\n\n\n";

    [Fact]
    public void ABareAttrsImportedFromAttrsMakesTheInit()
    {
        Assert.Equal(0, Found("from attr._make import attrib, attrs\n\n\n" + V + "V(1, 2)\n").Count);
    }

    [Fact]
    public void ARelativeImportInsideTheAttrPackageCounts()
    {
        Assert.Equal(0, Found("from ._make import attrib, attrs\n\n\n" + V + "V(1, 2)\n", "attr.validators").Count);
    }

    [Fact]
    public void TheAttrsDefOfAttrsOwnModuleDecoratesItsClasses()
    {
        Assert.Equal(0, Found(V + "V(1, 2)\n", "attr._make").Count);
    }

    [Fact]
    public void TooManyArgumentsAreStillFound()
    {
        var found = Found("from attr._make import attrib, attrs\n\n\n" + V + "V(1, 2, 3)\n");
        Assert.Equal(1, found.Count);
        Assert.True(found[0].StartsWith("Unexpected argument"), found[0]);
    }

    [Fact]
    public void AFieldWithoutATypeIsAnyNotTheCountingAttr()
    {
        // as attrs' own stubs say: a bare attrib() is Any (RealAttrsTests checks the same with attrs installed)
        Assert.Equal(0, Found("from attr._make import attrib, attrs\n\n\n" + V + "def f(v: V):\n    return v.pattern.pattern\n").Count);
        Assert.Equal("Any", Last("from attr._make import attrib, attrs\n\n\n" + V + "V(1, 2).pattern\n"));
    }

    [Fact]
    public void TheTypeKeywordTypesTheFieldRead()
    {
        Assert.Equal("int", Last("from attr._make import attrib, attrs\n\n\n" + V + "V(1, 2).n\n"));
    }

    [Fact]
    public void ADefaultOrAFactoryTypesTheFieldRead()
    {
        const string W = "from attr._make import attrib, attrs\n\n\n@attrs\nclass W:\n    label = attrib(default=\"x\")\n    items = attrib(factory=list)\n\n\n";
        Assert.Equal("str", Last(W + "W().label\n"));
        Assert.True(Last(W + "W().items\n").StartsWith("list"));
    }

    [Fact]
    public void DefineAndFieldFromAttrsWork()
    {
        Assert.Equal("int", Last("from attrs import define, field\n\n\n@define\nclass P:\n    n = field(type=int)\n\n\nP(1).n\n"));
        Assert.Equal("int", Last("import attrs\n\n\n@attrs.define\nclass P:\n    n = attrs.field(type=int)\n\n\nP(1).n\n"));
    }

    [Fact]
    public void FrozenMakesTheClassFrozen()
    {
        Assert.Equal(1, Found("from attrs import frozen, field\n\n\n@frozen\nclass P:\n    n = field(type=int)\n\n\nP(1).n = 2\n").Count);
        Assert.Equal(1, Found("import attrs\n\n\n@attrs.frozen\nclass P:\n    n = attrs.field(type=int)\n\n\nP(1).n = 2\n").Count);
        Assert.Equal(0, Found("from attrs import mutable, field\n\n\n@mutable\nclass P:\n    n = field(type=int)\n\n\nP(1).n = 2\n").Count);
    }

    [Fact]
    public void AnAttrsOfAnotherLibraryIsNotTakenForAttrs()
    {
        Assert.Equal("_CountingAttr", Last("from mylib import attrib, attrs\n\n\n@attrs\nclass W:\n    x = attrib(type=int)\n\n\nW().x\n"));
    }

    [Fact]
    public void AttribInAPlainClassKeepsItsReturnType()
    {
        Assert.Equal("_CountingAttr", Last("from attr._make import attrib\n\n\nclass W:\n    x = attrib(type=int)\n\n\nW().x\n"));
    }
}
