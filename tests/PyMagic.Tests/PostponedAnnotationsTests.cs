using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// Under `from __future__ import annotations`, and in stubs, an annotation is not evaluated where it stands, so a class bound later
/// in the module is what it names (pydantic: `def check(model_cls: type[BaseModel])` above `class BaseModel`).
/// </summary>
public class PostponedAnnotationsTests
{
    const string Future = "from __future__ import annotations\n\n";
    const string Later = "\n\nclass Later:\n    x: int = 0\n";

    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    [Fact]
    public void AParameterAnnotatedWithAClassDefinedLaterHasThatClass()
    {
        Assert.Equal("Later", Last(Future + "def f(p: Later):\n    p\n" + Later));
        Assert.Equal("list[Later] | None", Last(Future + "def f(p: list[Later] | None):\n    p\n" + Later));
    }

    [Fact]
    public void TheClassObjectOfALaterClassHasItsAttributes()
    {
        Assert.Equal("type[Later]", Last(Future + "def f(c: type[Later]):\n    c\n" + Later));
        Assert.Equal(0, Found(Future + "def f(c: type[Later]) -> None:\n    print(c.x)\n" + Later).Count);
    }

    [Fact]
    public void AMissingAttributeOfALaterClassIsReported()
    {
        Assert.Equal(1, Found(Future + "def f(p: Later) -> None:\n    print(p.nope)\n" + Later).Count);
    }

    [Fact]
    public void AReturnAnnotationNamesALaterClass()
    {
        Assert.Equal("Later", Last(Future + "def make() -> Later: ...\n" + Later + "\n\nmake()"));
    }

    [Fact]
    public void AClassBodyAnnotationNamesALaterClass()
    {
        Assert.Equal("Later", Last(Future + "class Box:\n    item: Later\n" + Later + "\n\nBox().item"));
    }

    [Fact]
    public void AParameterOfTheSameNameDoesNotShadowTheClass()
    {
        Assert.Equal("Foo", Last(Future + "class Foo:\n    x: int = 0\n\n\ndef f(x: Foo, Foo=1):\n    x"));
    }

    [Fact]
    public void WithoutTheFutureImportAnAnnotationIsReadWhereItStands()
    {
        Assert.Equal("Unknown", Last("def f(p: Later):\n    p\n" + Later));
        Assert.Equal(0, Found("def f(p: Later) -> None:\n    print(p.nope)\n" + Later).Count);
    }

    [Fact]
    public void ACastIsStillEvaluatedWhereItStands()
    {
        Assert.Equal("A", Last(Future + "from typing import cast\n\n\nclass A:\n    a: int = 0\n\n\nclass B:\n    b: int = 0\n\n\nK = A\nv = cast(K, object())\nK = B\nv"));
    }

    [Fact]
    public void AStubNamesAClassDefinedLaterInIt()
    {
        var root = Path.Combine(Path.GetTempPath(), "pm_pa_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            File.WriteAllText(Path.Combine(root, "helper.pyi"), "def make() -> Later: ...\n\nclass Later:\n    x: int\n");
            var ws = new Workspace(new WorkspaceOptions { SourceRoots = new[] { root } });
            var m = ws.AddModule("main", "import helper\nhelper.make()");
            var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
            Assert.Equal("Later", ws.Evaluator.TypeOf(m, last.Value).ToString());
        }
        finally { try { Directory.Delete(root, true); } catch { /* best effort */ } }
    }
}
