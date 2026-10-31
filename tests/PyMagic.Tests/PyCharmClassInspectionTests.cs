using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>
/// PyCharm's inspections of protocols, variance, layouts, type parameters, match patterns and conditions (item 195); every runtime
/// failure they name was run under Python 3.14 first.
/// </summary>
public class PyCharmClassInspectionTests
{
    const string Uri = "file:///c:/proj/app.py";

    static LanguageService Make(string code)
    {
        var svc = new LanguageService();
        svc.SourceRoots.Add("c:/proj");
        svc.Open(Uri, code, 1);
        return svc;
    }

    static List<LspDiagnostic> Found(LanguageService svc, string code) => svc.Diagnostics(Uri).Where(d => d.Code == code).ToList();

    static string Lines(List<LspDiagnostic> d) => string.Join(",", d.Select(x => x.Range.Start.Line));

    [Fact]
    public void ProtocolsAndTheirBasesAndIsinstance()
    {
        string code = "from typing import Protocol, runtime_checkable\n\n\nclass Base: ...\n\n\nclass Shape(Protocol):\n    def area(self) -> float: ...\n\n\nclass Bad(Protocol, Base): ...\n\n\n@runtime_checkable\nclass Sized(Protocol):\n    def size(self) -> int: ...\n\n\n@runtime_checkable\nclass Plain: ...\n\n\nisinstance(1, Shape)\nisinstance(1, (Sized, Shape))\nisinstance(1, Sized)\n";
        var d = Found(Make(code), "protocol");
        Assert.Equal("10,19,22,23", Lines(d));
        Assert.Contains("'Base' is not one", d[0].Message);
    }

    [Fact]
    public void ACovariantParameterAndAContravariantReturn()
    {
        string code = "from typing import Generic, TypeVar\n\nT_co = TypeVar('T_co', covariant=True)\nT_contra = TypeVar('T_contra', contravariant=True)\nT = TypeVar('T')\n\n\nclass Box(Generic[T_co]):\n    def __init__(self, item: T_co) -> None: ...\n    def get(self) -> T_co: ...\n    def put(self, item: T_co | None) -> None: ...\n    def items(self, rows: list[T_co]) -> None: ...\n\n\nclass Sink(Generic[T_contra]):\n    def take(self, x: T_contra) -> None: ...\n    def give(self) -> T_contra: ...\n";
        var d = Found(Make(code), "variance");
        Assert.Equal("10,16", Lines(d));
        Assert.Contains("covariant type variable 'T_co'", d[0].Message);
    }

    [Fact]
    public void BasesWhoseLayoutsConflict()
    {
        string code = "from collections import OrderedDict\n\n\nclass A(int, str): ...\nclass B(Exception, list): ...\nclass C(OrderedDict, dict): ...\nclass MyInt(int): ...\nclass D(MyInt, float): ...\nclass E(dict, object): ...\n";
        var d = Found(Make(code), "disjoint-bases");
        Assert.Equal("3,4,7", Lines(d));
        Assert.Contains("'int' and 'str'", d[0].Message);
    }

    [Fact]
    public void TypeParameterSyntaxMixedWithTheOldKind()
    {
        string code = "from typing import Generic, TypeVar\n\nT = TypeVar('T')\n\n\nclass A[U](Generic[T]): ...\n\n\ndef f[V](x: V, y: T) -> V: ...\n\n\ndef g[W, W](x: W) -> W: ...\n\n\ntype Pair[K] = tuple[K, T]\n\n\nclass Fine[X]:\n    def m(self, x: X) -> X: ...\n";
        var d = Found(Make(code), "type-params").Select(x => x.Range.Start.Line + ": " + x.Message[..30]).ToList();
        Assert.Equal(4, d.Count);
        Assert.Equal("5,8,11,14", string.Join(",", d.Select(x => x.Split(':')[0])));
    }

    [Fact]
    public void PatternsPythonRefuses()
    {
        string code = "class P:\n    __match_args__ = ('a',)\n    a = 1\n\n\nclass Q: ...\n\n\nclass MyInt(int): ...\n\n\ndef f(): ...\n\n\ndef run(v):\n    match v:\n        case (x, x):\n            pass\n        case [y] | z:\n            pass\n        case P(a=1, a=2):\n            pass\n        case f():\n            pass\n        case P(1, 2):\n            pass\n        case Q(1):\n            pass\n        case MyInt(n):\n            pass\n        case {'k': 1, 'k': 2}:\n            pass\n        case w:\n            pass\n        case 3:\n            pass\n";
        var d = Found(Make(code), "match-pattern").Select(x => $"{x.Range.Start.Line}: {x.Message}").ToList();
        Assert.Equal(9, d.Count);
        Assert.True(d.Any(x => x.StartsWith("18:") && x.Contains("name capture 'z' makes the remaining patterns unreachable")));
        Assert.True(d.Any(x => x.StartsWith("16:") && x.Contains("'x' is bound twice")), string.Join("\n", d));
        Assert.True(d.Any(x => x.StartsWith("18:") && x.Contains("bind different names")));
        Assert.True(d.Any(x => x.StartsWith("20:") && x.Contains("'a' is repeated")));
        Assert.True(d.Any(x => x.StartsWith("22:") && x.Contains("'f' is not a class")));
        Assert.True(d.Any(x => x.StartsWith("24:") && x.Contains("P() accepts 1 positional sub-pattern (2 given)")));
        Assert.True(d.Any(x => x.StartsWith("26:") && x.Contains("Q() accepts 0 positional sub-patterns (1 given)")));
        Assert.True(d.Any(x => x.StartsWith("30:") && x.Contains("'k' is checked twice") || x.StartsWith("30:") && x.Contains("checked twice")));
        Assert.True(d.Any(x => x.StartsWith("32:") && x.Contains("name capture 'w' makes the remaining patterns unreachable")));
    }

    [Fact]
    public void ConditionalsThatRepeatTheirConditionAndConditionsAlwaysTrue()
    {
        string code = "import asyncio\n\n\nclass Door:\n    def is_open(self) -> bool:\n        return True\n\n\nasync def ready() -> bool:\n    return True\n\n\nasync def main(d: Door, n: int):\n    a = True if n > 1 else False\n    b = False if n else True\n    c = n if n else False\n    if d.is_open:\n        pass\n    while ready():\n        break\n    if not d.is_open() or d.is_open:\n        pass\n    if await ready():\n        pass\n";
        var svc = Make(code);
        Assert.Equal("13,14", Lines(Found(svc, "redundant-boolean")));
        var always = Found(svc, "suspicious-boolean");
        Assert.Equal("16,18,20", Lines(always));
        Assert.Contains("'d.is_open' is a function", always[0].Message);
        Assert.Contains("'ready()' is a coroutine", always[1].Message);
    }
}
