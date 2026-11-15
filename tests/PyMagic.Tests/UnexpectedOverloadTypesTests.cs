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
    public void OverloadsOfAFunctionInAPyFileAreChecked()
    {
        // the name reads as its implementation in the flow; a call is still checked against the overloads, and gets their return types
        var src = "from typing import overload\n\n\n@overload\ndef f(a: int) -> int: ...\n@overload\ndef f(a: str) -> str: ...\ndef f(a):\n    return a\n\n\n"
            + "f(1.5)\nf('s')\nf(1)\n";
        Assert.Equal("Unexpected type(s): (float) Possible type(s): (int) (str)", string.Join("; ", Found(src)));
        var ws = new Workspace();
        var m = ws.AddModule("main", src + "x = f('s')\n");
        var x = PyMagic.Core.Syntax.AstChildren.Descendants(m.Ast).OfType<PyMagic.Core.Syntax.NameExpr>().Last(n => n.Name == "x");
        Assert.Equal("str", ws.Evaluator.TypeOf(m, x).ToString());
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

    // bs4's Tag.find (4.13 and later, inline types): find(attrs={...}) with no name fits none of its overloads (the one for attrs wants a name)
    const string Find = "from typing import Optional, Union, overload\n\n\nclass Tag:\n"
        + "    @overload\n    def find(self, name: None = None, attrs: None = None, *, string: str) -> str | None: ...\n"
        + "    @overload\n    def find(self, name: None = None, attrs: None = None, string: None = None) -> 'Tag | None': ...\n"
        + "    @overload\n    def find(self, name: None, attrs: dict[str, Union[str, bool]], string: Optional[str] = None) -> 'Tag | None': ...\n"
        + "    @overload\n    def find(self, name: str, attrs: Optional[dict[str, Union[str, bool]]] = None) -> 'Tag | None': ...\n"
        + "    def find(self, name: Optional[str] = None, attrs: Optional[dict[str, Union[str, bool]]] = None, string: Optional[str] = None) -> 'Tag | str | None':\n"
        + "        return None\n\n\nsoup = Tag()\n";

    [Fact]
    public void ACallNoOverloadTakesButTheTypedFunctionItselfDoesIsNotTold()
    {
        // as PyCharm: the call is valid for the function; it returns what the overload for attrs does
        Assert.Equal(0, Found(Find + "r = soup.find(attrs={'data-listing-view': True})\nr.find('a')\n").Count);
        // a mistake the function itself does not take either is still told
        Assert.True(Found(Find + "soup.find(attrs={'data-listing-view': 1.5})\n").Count > 0);
    }
}
