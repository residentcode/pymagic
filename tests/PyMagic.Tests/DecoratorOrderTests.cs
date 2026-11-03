using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// The type of a call of a decorated function does not depend on what was asked first: the module's inspections before the call's
/// type gave Unknown where the call's type first gave the right one (a decorator `Callable[..., R] -> Callable[..., R]`, R from the
/// function's own returns).
/// </summary>
public class DecoratorOrderTests
{
    const string Src = "from typing import Callable, TypeVar\n\nR = TypeVar('R')\n\n\n"
        + "def deco(func: Callable[..., R]) -> Callable[..., R]:\n    return func\n\n\n"
        + "@deco\ndef test_one():\n    return 1\n\n\n"
        + "test_one()\n";

    // the same through an instance whose __call__ is typed (as `@mock.patch(...)` is)
    const string InstanceSrc = "from typing import Callable, TypeVar\n\nR = TypeVar('R')\n\n\n"
        + "class PassArg:\n    def __call__(self, func: Callable[..., R]) -> Callable[..., R]: ...\n\n\n"
        + "@PassArg()\ndef test_one(mocked):\n    return 1\n\n\n"
        + "test_one()\n";

    static string TypeOfLast(bool inspectFirst, string src = Src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        if (inspectFirst) ws.Evaluator.Inspect(m, true);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    [Fact]
    public void TheHoverAfterTheProblemsGivesTheCallsType()
    {
        // as in the app: the Problems tab, then the hover of the call (what G saw); and the hover without the Problems first
        foreach (bool problemsFirst in new[] { false, true })
        {
            var svc = new PyMagic.Server.LanguageService();
            const string uri = "file:///c:/proj/main.py";
            svc.Open(uri, Src, 1);
            if (problemsFirst) svc.Problems();
            var lines = Src.Split('\n');
            int line = Array.FindLastIndex(lines, l => l.StartsWith("test_one()"));
            var hover = svc.Hover(uri, new PyMagic.Server.Position(line, 3));
            string text = hover?.Contents.Value ?? "(none)";
            Assert.True(text.Contains("int"), $"problems first: {problemsFirst}: {text}");
        }
    }

    [Fact]
    public void TheCallsTypeIsTheSameWhicheverIsAskedFirst()
    {
        Assert.Equal("int", TypeOfLast(inspectFirst: false));
        Assert.Equal("int", TypeOfLast(inspectFirst: true));
        Assert.Equal("int", TypeOfLast(inspectFirst: false, InstanceSrc));
        Assert.Equal("int", TypeOfLast(inspectFirst: true, InstanceSrc));
    }
}
