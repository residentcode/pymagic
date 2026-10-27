using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Variance: a generic class's arguments compare by its type parameters' covariant= / contravariant=, and a callable's parameters the other way round.</summary>
public class VarianceTests
{
    static List<string> Found(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).Where(s => s.StartsWith("Expected type")).ToList();
    }

    static void Quiet(string src)
    {
        var found = Found(src);
        Assert.True(found.Count == 0, string.Join("; ", found));
    }

    static void Reported(string src, string message)
    {
        var found = Found(src);
        Assert.True(found.Count == 1 && found[0] == message, string.Join("; ", found));
    }

    // an invariant, a covariant and a contravariant class, and two classes to put in them
    const string Classes = "from typing import Generic, TypeVar\n\n"
        + "T = TypeVar('T')\nCo = TypeVar('Co', covariant=True)\nContra = TypeVar('Contra', contravariant=True)\n\n\n"
        + "class Base: ...\n\n\nclass Der(Base): ...\n\n\n"
        + "class Box(Generic[T]): ...\n\n\nclass Ro(Generic[Co]): ...\n\n\nclass Sink(Generic[Contra]): ...\n\n\n";

    [Fact]
    public void AnInvariantArgumentMustBeTheSameType()
    {
        Reported(Classes + "def f(b: Box[Base]): ...\n\n\nf(Box[Der]())\n", "Expected type 'Box[Base]', got 'Box[Der]' instead");
        Reported(Classes + "def f(b: Box[Der]): ...\n\n\nf(Box[Base]())\n", "Expected type 'Box[Der]', got 'Box[Base]' instead");
        Quiet(Classes + "def f(b: Box[Base]): ...\n\n\nf(Box[Base]())\n");
    }

    [Fact]
    public void ACovariantArgumentMayBeASubtype()
    {
        Quiet(Classes + "def f(b: Ro[Base]): ...\n\n\nf(Ro[Der]())\n");
        Reported(Classes + "def f(b: Ro[Der]): ...\n\n\nf(Ro[Base]())\n", "Expected type 'Ro[Der]', got 'Ro[Base]' instead");
    }

    [Fact]
    public void AContravariantArgumentMayBeASupertype()
    {
        Quiet(Classes + "def f(b: Sink[Der]): ...\n\n\nf(Sink[Base]())\n");
        Reported(Classes + "def f(b: Sink[Base]): ...\n\n\nf(Sink[Der]())\n", "Expected type 'Sink[Base]', got 'Sink[Der]' instead");
    }

    [Fact]
    public void TheVarianceFlagsAreReadFromTypingExtensionsToo()
    {
        var src = Classes.Replace("from typing import Generic, TypeVar", "from typing import Generic\nfrom typing_extensions import TypeVar");
        Quiet(src + "def f(b: Ro[Base]): ...\n\n\nf(Ro[Der]())\n");
        Reported(src + "def f(b: Box[Base]): ...\n\n\nf(Box[Der]())\n", "Expected type 'Box[Base]', got 'Box[Der]' instead");
    }

    [Fact]
    public void IntToFloatPromotionDoesNotMakeAnInvariantArgumentFit()
    {
        Reported(Classes + "def f(b: Box[float]): ...\n\n\ndef g(x: Box[int]):\n    f(x)\n", "Expected type 'Box[float]', got 'Box[int]' instead");
        Quiet(Classes + "def f(b: Ro[float]): ...\n\n\ndef g(x: Ro[int]):\n    f(x)\n");
    }

    [Fact]
    public void ASubclassIsComparedByTheArgumentsItGivesTheWantedClass()
    {
        Reported(Classes + "class IntBox(Box[Der]): ...\n\n\ndef f(b: Box[Base]): ...\n\n\nf(IntBox())\n", "Expected type 'Box[Base]', got 'IntBox' instead");
        Quiet(Classes + "class DerRo(Ro[Der]): ...\n\n\ndef f(b: Ro[Base]): ...\n\n\nf(DerRo())\n");
    }

    [Fact]
    public void APep695ParameterWhoseVarianceIsInferredComparesEitherWay()
    {
        Quiet("class Base: ...\n\n\nclass Der(Base): ...\n\n\nclass P[X]: ...\n\n\n"
            + "def f(a: P[Base], b: P[Der]): ...\n\n\nf(P[Der](), P[Base]())\n");
    }

    [Fact]
    public void UnknownAndAnyArgumentsAndUnknownBasesStayQuiet()
    {
        Quiet(Classes + "from typing import Any\nfrom nowhere import Thing, make\n\n\n"
            + "class Mixed(Thing, Box[Der]): ...\n\n\n"
            + "def f(b: Box[Base]): ...\n\n\n"
            + "def g(a: Box[Any], u: Box[make()]):\n    f(a)\n    f(u)\n    f(Mixed())\n    f(make())\n");
    }

    [Fact]
    public void AnInferredUnionArgumentStaysLenient()
    {
        Quiet(Classes + "def f(b: Box[Base]): ...\n\n\ndef g(x: Box[Base | Der]):\n    f(x)\n");
    }

    [Fact]
    public void AValueJustBuiltTakesTheTypeWanted()
    {
        // a list display or a generic call is inferred against the parameter, so its items only have to fit
        Quiet("def f(xs: list[float], d: dict[str, object]): ...\n\n\nf([1, 2], {'a': 1})\n");
        Quiet("def f(xs: list[float]): ...\n\n\nf([])\n");
        Quiet("def f(xs: list[list[float]]): ...\n\n\nf([[1]])\n");
        Quiet(Classes + "def make(x: T) -> Box[T]: ...\n\n\ndef f(b: Box[Base]): ...\n\n\nf(make(Der()))\n");
        Quiet("def f(xs: list[float]): ...\n\n\ndef g(ys: list[int]):\n    f([y for y in ys])\n");
    }

    [Fact]
    public void TypeshedsListAndDictAreInvariantAndSequenceAndTupleCovariant()
    {
        // the bundled stubs are small; the case needs typeshed's containers
        if (Workspace.DefaultTypeshedPath == null) return;
        var src = "from typing import Mapping, Sequence\n\n\n"
            + "def lf(x: list[float]): ...\n\n\ndef dso(x: dict[str, object]): ...\n\n\ndef sf(x: Sequence[float]): ...\n\n\n"
            + "def tf(x: tuple[float, ...]): ...\n\n\ndef mso(x: Mapping[str, object]): ...\n\n\ndef moi(x: Mapping[object, int]): ...\n\n\n"
            + "def g(li: list[int], d: dict[str, int], t: tuple[int, ...], m: Mapping[str, int]):\n";
        Reported(src + "    lf(li)\n", "Expected type 'list[float]', got 'list[int]' instead");
        Reported(src + "    dso(d)\n", "Expected type 'dict[str, object]', got 'dict[str, int]' instead");
        Reported(src + "    moi(m)\n", "Expected type 'Mapping[object, int]', got 'Mapping[str, int]' instead");
        Quiet(src + "    sf(li)\n    tf(t)\n    mso(m)\n    mso(d)\n    lf([1, 2])\n    lf(list(li))\n    lf(sorted(li))\n");
    }

    [Fact]
    public void ACallbacksParametersAreContravariantAndItsResultCovariant()
    {
        var src = "from typing import Callable\n\n\n"
            + "def wants(f: Callable[[int], object]): ...\n\n\ndef wants_int(f: Callable[[int], int]): ...\n\n\n"
            + "def on_str(s: str) -> None: ...\n\n\ndef on_float(s: float) -> None: ...\n\n\n"
            + "def to_int(s: int) -> int: ...\n\n\ndef to_str(s: int) -> str: ...\n\n\n";
        Reported(src + "wants(on_str)\n", "Expected type '(int) -> object', got 'function on_str' instead");
        Reported(src + "wants_int(to_str)\n", "Expected type '(int) -> int', got 'function to_str' instead");
        Quiet(src + "wants(on_float)\nwants_int(to_int)\nwants_int(lambda v: v)\n");
    }

    [Fact]
    public void ACallbackWithAnUnknownParameterListStaysQuiet()
    {
        Quiet("from typing import Any, Callable\n\n\n"
            + "def wants(f: Callable[..., int]): ...\n\n\ndef wants_kw(f: Callable[[int], Any]): ...\n\n\n"
            + "def anything(*a: Any) -> int: ...\n\n\ndef untyped(x): ...\n\n\n"
            + "wants(anything)\nwants(untyped)\nwants_kw(untyped)\n");
    }
}
