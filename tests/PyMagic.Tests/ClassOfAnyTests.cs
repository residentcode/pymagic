using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>type[Any], and the class of something unresolved, is still a class: what `type` has keeps its type, and nothing else is missing.</summary>
public class ClassOfAnyTests
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
    public void AnyAttributeOfTypeOfAnyIsFound()
    {
        var found = Found("from typing import Any, Type\n\n\ndef f(a: type[Any], b: Type[Any]):\n    a.model_config\n    b.__pydantic_fields__\n    a.anything.deeper\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void TheClassOfAnUnresolvedImportIsNotAPlainType()
    {
        // found in the library scan: `type[BaseModel]` with the import of BaseModel unresolved was reported 51 times in pydantic
        var found = Found("from mystery import Base\n\n\ndef f(cls: type[Base]):\n    cls.model_fields\n    cls.__pydantic_root_model__\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void WhatTypeHasKeepsItsType()
    {
        Assert.Equal("str", Last("from typing import Any\n\n\ndef f(a: type[Any]):\n    a.__name__\n"));
        Assert.Equal("Any", Last("from typing import Any\n\n\ndef f(a: type[Any]):\n    a.not_on_type\n"));
        Assert.Equal("type[Any]", Last("from typing import Any\n\n\ndef f(a: type[Any]):\n    a\n"));
        Assert.Equal("type[Unknown]", Last("from mystery import Base\n\n\ndef f(cls: type[Base]):\n    cls\n"));
    }

    [Fact]
    public void AKnownClassStillReportsWhatItLacks()
    {
        var found = Found("class K:\n    x = 1\n\n\ndef f(c: type[K]):\n    c.x\n    c.no_such_thing\n");
        Assert.Equal(1, found.Count);
        Assert.Contains("no_such_thing", found[0]);
    }

    [Fact]
    public void ADeclaredVariableFollowsATypeOfAny()
    {
        var found = Found("from typing import Any, Optional\n\n\ndef f(a: type[Any]):\n    c: Optional[type] = None\n    c = a\n    c.model_fields\n    d: object\n    d = a\n    d.model_fields\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void TheMetaclassOfATypeOfAnyIsNotKnownEither()
    {
        var found = Found("from typing import Any\n\n\ndef f(a: type[Any]):\n    type(a).registry\n    a.__class__.registry\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    [Fact]
    public void ATypeOfAnyCanBeCalledAndPassedOn()
    {
        var found = Found("from typing import Any\n\n\ndef need(c: type) -> None: ...\n\n\ndef need_int(c: type[int]) -> None: ...\n\n\ndef f(a: type[Any]) -> type[int]:\n    need(a)\n    need_int(a)\n    a().anything\n    return a\n");
        Assert.True(found.Count == 0, string.Join("; ", found));
    }
}
