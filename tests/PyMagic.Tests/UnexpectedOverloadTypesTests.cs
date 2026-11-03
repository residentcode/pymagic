using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>
/// Arguments of the wrong type for an overloaded function, functions given for a Callable, and parameters without annotations:
/// what PyCharm's "Incorrect type" reports there, and what it does not.
/// </summary>
public class UnexpectedOverloadTypesTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    const string Store = "from typing import overload, Any\n\n\nclass Store:\n"
        + "    @overload\n    def setdefault(self, key: str, default: None = None) -> Any: ...\n"
        + "    @overload\n    def setdefault(self, key: str, default: int) -> int: ...\n"
        + "    def setdefault(self, key, default=None): ...\n\n\ns = Store()\n";

    [Fact]
    public void NoOverloadTakingTheTypesIsOneReportWithEachOverloadsTypes()
    {
        var found = Found(Store + "s.setdefault(1, 2)\n");
        Assert.Equal(1, found.Count);
        Assert.Equal("Unexpected type(s): (int, int) Possible type(s): (str, None) (str, int)", found[0]);
    }

    [Fact]
    public void TheFirstOverloadsOwnMismatchesAreNotReported()
    {
        // before, the first overload's "Expected type 'None', got 'int'" came for the default, which the second overload takes
        var found = Found(Store + "s.setdefault(1, 2)\n");
        Assert.True(!found.Any(f => f.StartsWith("Expected type")), string.Join("; ", found));
    }

    [Fact]
    public void AnOverloadThatFitsIsStillPickedAndNothingIsReported()
    {
        Assert.Equal(0, Found(Store + "s.setdefault('k', 2)\ns.setdefault('k')\n").Count);
    }

    [Fact]
    public void OneOverloadOfTheRightCountKeepsItsOwnReport()
    {
        // the other overload takes no second argument: only one is left to judge the types, and its mismatch is said as it is
        var src = "from typing import overload\n\n\nclass C:\n    @overload\n    def f(self, a: int) -> int: ...\n    @overload\n    def f(self, a: str, b: str) -> str: ...\n"
            + "    def f(self, a, b=None): ...\n\n\nC().f(1, 2)\n";
        Assert.Equal("Expected type 'str', got 'int' instead", string.Join("; ", Found(src).Distinct()));
    }

    [Fact]
    public void FunctionsAndAUnionOfFunctionsFitACallable()
    {
        var src = "from typing import Any, Callable, TypeVar\n\nF = TypeVar('F', bound=Callable[..., Any])\n\n\n"
            + "def from_obj(obj: F) -> F:\n    return obj\n\n\ndef take(c: Callable[..., Any]) -> None: ...\n\n\n"
            + "def a(x: int) -> int:\n    return x\n\n\ndef b(s: str, n: int = 1) -> str:\n    return s\n\n\n"
            + "FILTERS = {'a': a, 'b': b, 'abs': abs, 'len': len}\ng = FILTERS['a']\nfrom_obj(g)\ntake(g)\ntake(a if g else b)\nfrom_obj(abs)\n";
        Assert.Equal(0, Found(src).Count);
    }

    [Fact]
    public void AParameterWithoutAnAnnotationWhoseDefaultIsNoneTakesAnything()
    {
        var src = "def f(x=None, y=None):\n    return x, y\n\n\nclass Opt:\n    def __init__(self, dest=None, const=None):\n        self.dest = dest\n\n\n"
            + "f([], 1)\nf(x='s', y=[1])\nOpt('d', [])\n";
        Assert.Equal(0, Found(src).Count);
    }
}
