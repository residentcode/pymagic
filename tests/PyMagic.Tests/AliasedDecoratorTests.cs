using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>A flag decorator under another name is still that decorator (typeshed's enum.pyi: `@_builtins_property`, `@_magic_enum_attr`).</summary>
public class AliasedDecoratorTests
{
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
    public void APropertyImportedUnderAnotherNameIsAProperty()
    {
        Assert.Equal("int", Last("from builtins import property as _builtins_property\n\n\nclass C:\n    @_builtins_property\n    def a(self) -> int: return 1\n\n\nC().a"));
    }

    [Fact]
    public void APropertyAssignedToAVariableIsAProperty()
    {
        Assert.Equal("str", Last("_attr = property\n\n\nclass C:\n    @_attr\n    def a(self) -> str: return ''\n\n\nC().a"));
    }

    [Fact]
    public void AnAliasBoundInBothArmsOfAVersionCheckIsAProperty()
    {
        var src = "import sys\nimport types\n\nif sys.version_info >= (3, 11):\n    _magic_enum_attr = types.DynamicClassAttribute\nelse:\n    _magic_enum_attr = property\n\n\n"
                + "class Member:\n    @_magic_enum_attr\n    def name(self) -> str: return ''\n\n\nclass Color(Member): ...\n\n\n";
        Assert.Equal("str", Last(src + "Color().name"));
        Assert.Equal(0, Found(src + "Color().name.casefold()\n").Count);
    }

    [Fact]
    public void AnAliasOfAnAliasIsFollowed()
    {
        Assert.Equal("bytes", Last("_a = property\n_b = _a\n\n\nclass C:\n    @_b\n    def a(self) -> bytes: return b''\n\n\nC().a"));
    }

    [Fact]
    public void ACachedPropertyImportedUnderAnotherNameIsAProperty()
    {
        Assert.Equal("float", Last("from functools import cached_property as cp\n\n\nclass C:\n    @cp\n    def a(self) -> float: return 1.0\n\n\nC().a"));
    }

    [Fact]
    public void DynamicClassAttributeIsAProperty()
    {
        Assert.Equal("int", Last("import types\n\n\nclass C:\n    @types.DynamicClassAttribute\n    def a(self) -> int: return 1\n\n\nC().a"));
    }

    [Fact]
    public void AStaticmethodUnderAnotherNameTakesNoSelf()
    {
        Assert.Equal(0, Found("_static = staticmethod\n\n\nclass C:\n    @_static\n    def f(x: int) -> int: return x\n\n\nC().f(1)\nC.f(1)\n").Count);
    }

    [Fact]
    public void AVariableBoundToSomethingElseIsNotAFlagDecorator()
    {
        Assert.Equal("int", Last("def keep(f): return f\n\n\n_d = keep\n\n\nclass C:\n    @_d\n    def a(self) -> int: return 1\n\n\nC().a()"));
    }

    [Fact]
    public void AliasesThatReferToEachOtherEnd()
    {
        Assert.Equal("int", Last("_a = _b\n_b = _a\n\n\nclass C:\n    @_a\n    def a(self) -> int: return 1\n\n\nC().a()"));
    }
}
