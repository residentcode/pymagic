using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Gaps the library sweep (tools/library-sweep) found: each a construct of every library, not of the one it was seen in.</summary>
public class LibraryGapTests
{
    static string Last(Workspace ws, ModuleInfo m) =>
        ws.Evaluator.TypeOf(m, AstChildren.Descendants(m.Ast).OfType<AssignStmt>().Last().Value).Display();

    [Fact]
    public void AMethodNamedLikeItsAnnotationReadsTheOuterName()
    {
        (string source, string expected)[] cases =
        [
            // pandas' DatetimeArray.tzinfo and BooleanDtype.type: the method's own name in its annotation is the module's (or builtins') name, since
            // the def binds its name only after the annotation is read
            ("from __future__ import annotations\nfrom tz import tzinfo\n\n\nclass A:\n    @property\n    def tzinfo(self) -> tzinfo | None: ...\n\n\ndef f(a: A):\n    V = a.tzinfo\n", "tzinfo | None"),
            ("class A:\n    @property\n    def type(self) -> type: ...\n\n\ndef f(a: A):\n    V = a.type\n", "type"),
            ("from tz import tzinfo\n\n\nclass A:\n    def tzinfo(self) -> tzinfo: ...\n\n\ndef f(a: A):\n    V = a.tzinfo()\n", "tzinfo"),
        ];
        foreach (var (source, expected) in cases)
        {
            var ws = new Workspace();
            ws.AddModule("tz", "class tzinfo:\n    pass\n");
            var m = ws.AddModule("main", source);
            Assert.Equal(expected, Last(ws, m));
        }
    }

    [Fact]
    public void AnEarlierMethodOfTheClassStillShadowsALaterAnnotation()
    {
        // a name the class body bound before the def is the class's: Python reads it from the class namespace
        var ws = new Workspace();
        var m = ws.AddModule("main", "class Node:\n    pass\n\n\nclass A:\n    Node = int\n\n    def get(self) -> Node: ...\n\n\ndef f(a: A):\n    V = a.get()\n");
        Assert.Equal("int", Last(ws, m));
    }

    [Fact]
    public void AMethodThatOnlyRaisesNotImplementedGivesItsAnnotation()
    {
        // the shape of authlib's EndSessionEndpoint.get_server_jwks (seen Unknown in the sweep only because typeshed's Authlib stub, which wins
        // over the source as in PyCharm, leaves it unannotated): a docstring with an example def of the same name, a body that only raises
        var ws = new Workspace();
        ws.AddModule("keys", "class KeySet:\n    pass\n");
        var m = ws.AddModule("main", "from __future__ import annotations\nfrom dataclasses import dataclass\nfrom keys import KeySet\n\n\n@dataclass\nclass R:\n    a: int = 0\n\n\nclass Base:\n    def __init__(self, server=None):\n        self.server = server\n\n\nclass E(Base):\n    \"\"\"Example::\n\n        class Mine(E):\n            def jwks(self):\n                return load()\n    \"\"\"\n\n    def other(self, x: dict) -> dict:\n        return x\n\n    def jwks(self) -> dict | KeySet:\n        \"\"\"Return the keys.\"\"\"\n        raise NotImplementedError()\n\n\nasync def f(x: E):\n    V = x.jwks()\n");
        Assert.Equal("dict | KeySet", Last(ws, m));
    }
}
