using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>An instance used as a decorator is applied when its `__call__` says what it returns: `@mock.patch(...)` hands the decorated function a mock.</summary>
public class InstanceDecoratorTests
{
    static (List<string> Found, string Last) Check(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        string type = ws.Evaluator.TypeOf(m, last.Value).ToString();
        return (ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList(), type);
    }

    static bool HaveTypeshed => Workspace.DefaultTypeshedPath != null && Directory.Exists(Path.Combine(Workspace.DefaultTypeshedPath, "stdlib"));

    [Fact]
    public void AnInstanceWithATypedCallDecorates()
    {
        // as typeshed's unittest.mock: `patch("x")` is a `_patch_pass_arg`, whose __call__ gives a Callable[..., R]
        var (found, last) = Check("from typing import Any, Callable, TypeVar\n\nR = TypeVar('R')\n\n\n"
            + "class PassArg:\n    def __call__(self, func: Callable[..., R]) -> Callable[..., R]: ...\n\n\n"
            + "class Untyped:\n    def __call__(self, func):\n        return func\n\n\n"
            + "@PassArg()\ndef test_one(mocked, other):\n    return 1\n\n\n"
            + "@Untyped()\ndef test_two(a):\n    return 'x'\n\n\n"
            + "test_one()\ntest_two()\ntest_one()\n");
        Assert.True(!found.Any(f => f.Contains("mocked") || f.Contains("other")), string.Join("; ", found));
        Assert.True(found.Any(f => f.Contains("'a'")), "an instance whose __call__ says nothing leaves the function as written: " + string.Join("; ", found));
        Assert.Equal("int", last);
    }

    [Fact]
    public void MockPatchAsADecoratorPassesTheMock()
    {
        if (!HaveTypeshed) return;   // unittest.mock comes from typeshed
        var (found, last) = Check("import os\nfrom unittest import mock\nfrom unittest.mock import patch\n\n\n"
            + "@patch('os.getcwd')\ndef test_one(m_getcwd):\n    return os.getcwd()\n\n\n"
            + "@mock.patch('os.getcwd')\n@mock.patch.object(os, 'listdir')\ndef test_two(m_listdir, m_getcwd):\n    pass\n\n\n"
            + "@patch('os.getcwd', new=lambda: 'y')\ndef test_three(a):\n    pass\n\n\n"
            + "class T:\n    @patch('os.getcwd')\n    def test_m(self, m):\n        m.reset_mock()\n\n\n"
            + "test_two()\ntest_three()\nT().test_m()\ntest_one()\n");
        Assert.True(!found.Any(f => f.Contains("m_getcwd") || f.Contains("m_listdir") || f.Contains("'m'")), string.Join("; ", found));
        Assert.True(found.Any(f => f.Contains("'a'")), "patch with new= passes nothing, so the parameter is still wanted: " + string.Join("; ", found));
        Assert.Equal("str", last);
    }
}
