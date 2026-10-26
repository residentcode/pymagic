using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Every module object has the attributes of types.ModuleType (__file__, __spec__, ...), unless it defines them itself.</summary>
public class ModuleTypeAttributeTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        ws.AddModule("helper", "VALUE = 1\n");
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    static string Last(string helper, string src)
    {
        var ws = new Workspace();
        ws.AddModule("helper", helper);
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void TheFileOfAModuleIsFound()
    {
        Assert.Equal(0, Found("import helper\n\nprint(helper.__file__)\n").Count);
    }

    [Fact]
    public void TheSpecLoaderPathAndPackageOfAModuleAreFound()
    {
        Assert.Equal(0, Found("import helper\n\nprint(helper.__spec__, helper.__loader__, helper.__path__, helper.__package__)\n").Count);
    }

    [Fact]
    public void TheNameDocAndDictOfAModuleAreFound()
    {
        Assert.Equal(0, Found("import helper\n\nprint(helper.__name__, helper.__doc__, helper.__dict__)\n").Count);
    }

    [Fact]
    public void TheFileOfAModuleIsAnOptionalString()
    {
        Assert.Equal("str | None", Last("VALUE = 1\n", "import helper\n\nhelper.__file__"));
    }

    [Fact]
    public void WhatTheModuleDefinesItselfWins()
    {
        Assert.Equal("int", Last("__doc__ = 3\n", "import helper\n\nhelper.__doc__"));
    }

    [Fact]
    public void ANameThatNoModuleHasIsStillReported()
    {
        Assert.Equal(1, Found("import helper\n\nprint(helper.__version__)\n").Count);
        Assert.Equal(1, Found("import helper\n\nprint(helper.missing)\n").Count);
    }

    [Fact]
    public void AModuleDefinedNameIsStillFound()
    {
        Assert.Equal(0, Found("import helper\n\nprint(helper.VALUE)\n").Count);
    }
}
