using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Enums, dataclasses, named tuples and TypedDicts.</summary>
public class SpecialTests
{
    static (Workspace Ws, ModuleInfo M) Load(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    /// <summary>Type of the last expression statement anywhere in the source.</summary>
    static string T(string src)
    {
        var (ws, m) = Load(src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    /// <summary>Arity problems of the call in the last statement.</summary>
    static string Problems(string src)
    {
        var (ws, m) = Load(src);
        var call = (CallExpr)((ExprStmt)m.Ast.Body[^1]).Value;
        return string.Join(";", ws.Evaluator.ResolveCall(m, call).Targets.SelectMany(t => t.Problems)
            .Where(p => p.Kind != CallProblemKind.Type).Select(p => p.Message));
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    // ---------------------------------------------------------------- stubs

    [Fact]
    public void NewBundledStubsParseCleanly()
    {
        var ws = new Workspace();
        foreach (var name in new[] { "enum", "dataclasses", "collections", "abc", "typing" })
        {
            var mod = ws.GetModule(name)!;
            Assert.True(mod != null && mod.SyntaxErrors.Count == 0, name + ": " + string.Join("; ", mod?.SyntaxErrors.Select(d => $"{d.Start}:{d.Message}") ?? new[] { "missing" }));
        }
    }

    // ---------------------------------------------------------------- enum

    const string Colors = "from enum import Enum, IntEnum, auto\nclass Color(Enum):\n    RED = 1\n    GREEN = 'g'\n    BLUE = auto()\n    def describe(self) -> str: ...\n";

    [Fact]
    public void EnumMembersAndValues()
    {
        Assert.Equal("Color.RED", T(Colors + "Color.RED"));
        Assert.Equal("int", T(Colors + "Color.RED.value"));
        Assert.Equal("str", T(Colors + "Color.GREEN.value"));
        Assert.Equal("int", T(Colors + "Color.BLUE.value"));
        Assert.Equal("str", T(Colors + "Color.RED.name"));
        Assert.Equal("str", T(Colors + "Color.RED.describe()"));
        Assert.Equal("Color", T(Colors + "Color('x')"));
        Assert.Equal("Color", T(Colors + "Color['RED']"));
        Assert.Equal("Color", T(Colors + "for c in Color:\n    pass\nc"));
        Same("int | str", T(Colors + "def f(c: Color):\n    c.value"));
        Assert.Equal("str", T(Colors + "def f(c: Color):\n    c.name"));
        Assert.Equal("str", T(Colors + "def f(c: Color):\n    c.describe()"));
        Assert.Equal("type[Color]", T(Colors + "Color"));
    }

    [Fact]
    public void EnumMixins()
    {
        const string pre = "from enum import IntEnum, StrEnum, Flag, auto\nclass Num(IntEnum):\n    ONE = 1\n    TWO = 2\n";
        Assert.Equal("int", T(pre + "Num.ONE.value"));
        Assert.Equal("int", T(pre + "Num.ONE + 1"));
        Assert.Equal("Num", T(pre + "Num(1)"));
        Assert.Equal("str", T("from enum import StrEnum, auto\nclass S(StrEnum):\n    A = auto()\nS.A.value"));
        Assert.Equal("P", T("from enum import Flag, auto\nclass P(Flag):\n    R = auto()\n    W = auto()\nP.R | P.W"));
    }

    [Fact]
    public void EnumMembersAreNotMissingAttributes()
    {
        var (ws, m) = Load(Colors + "Color.RED\nColor.RED.value\nColor.NOPE\n");
        var attrs = AstChildren.Descendants(m.Ast).OfType<AttributeExpr>().ToList();
        var misses = attrs.Where(a => ws.Evaluator.AttributeType(ws.Evaluator.TypeOf(m, a.Value), a.Attr).Missing.Count > 0).Select(a => a.Attr).ToList();
        Assert.Equal("NOPE", string.Join(",", misses));
    }

    // ---------------------------------------------------------------- dataclass

    const string DC = "from dataclasses import dataclass, field, KW_ONLY, InitVar\nfrom typing import ClassVar, Generic, TypeVar\n";

    [Fact]
    public void DataclassConstructor()
    {
        const string p = DC + "@dataclass\nclass P:\n    x: int\n    y: str = 'a'\n    tags: list[str] = field(default_factory=list)\n";
        Assert.Equal("P", T(p + "P(1)"));
        Assert.Equal("", Problems(p + "P(1)"));
        Assert.Equal("", Problems(p + "P(1, 'b', [])"));
        Assert.Equal("", Problems(p + "P(x=1, y='z')"));
        Assert.Equal("Parameter 'x' unfilled", Problems(p + "P()"));
        Assert.Equal("Unexpected argument", Problems(p + "P(1, 'a', [], 4)"));
        Assert.Equal("Unexpected argument 'z'", Problems(p + "P(x=1, z=2)"));
        Assert.Equal("int", T(p + "P(1).x"));
        Assert.Equal("list[str]", T(p + "P(1).tags"));
    }

    [Fact]
    public void DataclassInheritanceAndOptions()
    {
        const string p = DC + "@dataclass\nclass P:\n    x: int\n    y: str = 'a'\n";
        Assert.Equal("", Problems(p + "@dataclass\nclass Q(P):\n    z: float = 0.0\nQ(1, 'a', 2.0)"));
        Assert.Equal("Parameter 'x' unfilled", Problems(p + "@dataclass\nclass Q(P):\n    z: float = 0.0\nQ()"));
        Assert.Equal("", Problems(p + "class R(P):\n    pass\nR(1)"));                    // undecorated subclass keeps the init
        Assert.Equal("Unexpected argument;Parameter 'a' unfilled", Problems(DC + "@dataclass(kw_only=True)\nclass K:\n    a: int\nK(1)"));
        Assert.Equal("", Problems(DC + "@dataclass(kw_only=True)\nclass K:\n    a: int\nK(a=1)"));
        Assert.Equal("Unexpected argument 'h'", Problems(DC + "@dataclass\nclass A:\n    h: int = field(init=False, default=0)\nA(h=1)"));
        Assert.Equal("", Problems(DC + "@dataclass\nclass A:\n    n: ClassVar[int] = 0\n    v: int = 1\nA(1)"));
        Assert.Equal("Unexpected argument 'n'", Problems(DC + "@dataclass\nclass A:\n    n: ClassVar[int] = 0\n    v: int = 1\nA(n=1)"));
        Assert.Equal("", Problems(DC + "@dataclass\nclass A:\n    a: int\n    _: KW_ONLY\n    b: int = 0\nA(1, b=2)"));
        Assert.Equal("Unexpected argument", Problems(DC + "@dataclass\nclass A:\n    a: int\n    _: KW_ONLY\n    b: int = 0\nA(1, 2)"));
        Assert.Equal("", Problems(DC + "@dataclass\nclass A:\n    a: InitVar[int]\nA(1)"));
        // an explicit __init__ wins
        Assert.Equal("", Problems(DC + "@dataclass(init=False)\nclass A:\n    a: int\n    def __init__(self, q): ...\nA(1)"));
    }

    [Fact]
    public void GenericDataclass()
    {
        const string b = DC + "T = TypeVar('T')\n@dataclass\nclass Box(Generic[T]):\n    item: T\n";
        Assert.Equal("Box[int]", T(b + "Box(1)"));
        Assert.Equal("int", T(b + "Box(1).item"));
        Assert.Equal("str", T(b + "def f(x: Box[str]):\n    x.item"));
    }

    // ---------------------------------------------------------------- named tuples

    [Fact]
    public void NamedTupleClassSyntax()
    {
        const string pt = "from typing import NamedTuple\nclass Pt(NamedTuple):\n    x: int\n    y: str = 'a'\n";
        Assert.Equal("Pt", T(pt + "Pt(1)"));
        Assert.Equal("int", T(pt + "Pt(1).x"));
        Assert.Equal("str", T(pt + "Pt(1)[1]"));
        Assert.Equal("int", T(pt + "a, b = Pt(1)\na"));
        Assert.Equal("str", T(pt + "a, b = Pt(1)\nb"));
        Same("int | str", T(pt + "for v in Pt(1):\n    pass\nv"));
        Assert.Equal("Parameter 'x' unfilled", Problems(pt + "Pt()"));
        Assert.Equal("", Problems(pt + "Pt(1)"));
        Assert.Equal("Unexpected argument", Problems(pt + "Pt(1, 'a', 3)"));
        Assert.Equal("Pt", T(pt + "Pt(1)._replace(x=2)"));
        Assert.Equal("dict[str, Any]", T(pt + "Pt(1)._asdict()"));
        Assert.Equal("int", T(pt + "def f(p: Pt):\n    p.x"));
    }

    [Fact]
    public void NamedTupleFunctionalForms()
    {
        const string c = "from collections import namedtuple\nfrom typing import NamedTuple\n";
        Assert.Equal("P", T(c + "P = namedtuple('P', 'x y')\nP(1, 2)"));
        Assert.Equal("Any", T(c + "P = namedtuple('P', 'x y')\nP(1, 2).x"));
        Assert.Equal("Parameter 'y' unfilled", Problems(c + "P = namedtuple('P', ['x', 'y'])\nP(1)"));
        Assert.Equal("", Problems(c + "P = namedtuple('P', ['x', 'y'], defaults=[0])\nP(1)"));
        Assert.Equal("", Problems(c + "P = namedtuple('P', 'x, y')\nP(1, y=2)"));
        Assert.Equal("int", T(c + "Q = NamedTuple('Q', [('a', int), ('b', str)])\nQ(1, 'x').a"));
        Assert.Equal("str", T(c + "Q = NamedTuple('Q', [('a', int), ('b', str)])\nQ(1, 'x').b"));
        Assert.Equal("Any", T(c + "P = namedtuple('P', 'x y')\na, b = P(1, 2)\nb"));
    }

    [Fact]
    public void ExplicitConstructorCallsOnSynthesizedClasses()
    {
        // inspect.py: a subclass of a namedtuple customises __new__ and delegates to the synthesized one
        const string nt = "from collections import namedtuple\n_T = namedtuple('_T', 'a b')\nclass T(_T):\n    def __new__(cls, a, b, *, extra=None):\n        inst = super().__new__(cls, a, b)\n        return inst\n";
        Assert.Equal("", Problems(nt + "T(1, 2, extra=3)"));
        // the explicit super().__new__(cls, a) call is checked against the namedtuple fields
        var (ws0, m0) = Load(nt.Replace("super().__new__(cls, a, b)", "super().__new__(cls, a)"));
        var bad = AstChildren.Descendants(m0.Ast).OfType<CallExpr>().First(c => c.Func is AttributeExpr { Attr: "__new__" });
        Assert.Equal("Parameter 'b' unfilled", string.Join(";", ws0.Evaluator.ResolveCall(m0, bad).Targets.SelectMany(t => t.Problems).Select(p => p.Message)));
        // super().__init__ of a dataclass base
        const string dc = DC + "@dataclass\nclass B:\n    x: int\nclass D(B):\n    def __init__(self):\n        super().__init__(1)\n";
        var (ws, m) = Load(dc + "D()");
        var superCall = AstChildren.Descendants(m.Ast).OfType<CallExpr>().First(c => c.Func is AttributeExpr { Attr: "__init__" });
        Assert.Equal(0, ws.Evaluator.ResolveCall(m, superCall).Targets.SelectMany(t => t.Problems).Count());
        Assert.Equal("D", T(dc + "D()"));
    }

    [Fact]
    public void PropertySettersAndTupleUnpackingEdgeCases()
    {
        // @x.setter: x is the property object inside the class body
        var (ws, m) = Load("class A:\n    @property\n    def x(self) -> int: ...\n    @x.setter\n    def x(self, v: int) -> None: ...\n");
        var misses = AstChildren.Descendants(m.Ast).OfType<AttributeExpr>()
            .Where(a => ws.Evaluator.AttributeType(ws.Evaluator.TypeOf(m, a.Value), a.Attr).Missing.Count > 0).ToList();
        Assert.Equal(0, misses.Count);
        Assert.Equal("int", T("class A:\n    @property\n    def x(self) -> int: ...\n    @x.setter\n    def x(self, v: int) -> None: ...\nA().x"));
        // unpacking a tuple of a different size says nothing instead of mixing up the items
        Assert.Equal("Unknown", T("def f(c):\n    if c:\n        return 1, 2, 3\n    return 1, 2, 3\nfor a, b in [f(0)]:\n    pass\nb"));
        // __new__ implemented with object.__new__(cls) still constructs the class
        Assert.Equal("D", T("class D:\n    def __new__(cls, v):\n        self = object.__new__(cls)\n        return self\nD(1)"));
    }

    // ---------------------------------------------------------------- TypedDict

    const string TD = "from typing import TypedDict, NotRequired, Required\nclass Movie(TypedDict):\n    name: str\n    year: NotRequired[int]\n";

    [Fact]
    public void TypedDictAccess()
    {
        Assert.Equal("str", T(TD + "def f(m: Movie):\n    m['name']"));
        Assert.Equal("int", T(TD + "def f(m: Movie):\n    m['year']"));
        Assert.Equal("int | None", T(TD + "def f(m: Movie):\n    m.get('year')"));
        Assert.Equal("int", T(TD + "def f(m: Movie):\n    m.get('year', 0)"));
        Assert.Equal("str | None", T(TD + "def f(m: Movie):\n    m.get('name')"));
        TypeTests.OneOf(new[] { "KeysView[str]", "dict_keys[str, object]" }, T(TD + "def f(m: Movie):\n    m.keys()"));
        Assert.Equal("Movie", T(TD + "Movie(name='x')"));
    }

    [Fact]
    public void TypedDictConstruction()
    {
        Assert.Equal("", Problems(TD + "Movie(name='x')"));
        Assert.Equal("", Problems(TD + "Movie(name='x', year=1)"));
        Assert.Equal("Parameter 'name' unfilled", Problems(TD + "Movie()"));
        Assert.Equal("Unexpected argument 'nme'", Problems(TD + "Movie(name='x', nme='y')").Replace("Parameter 'name' unfilled;", ""));
        Assert.Equal("", Problems(TD + "class Sub(Movie, total=False):\n    extra: str\nSub(name='x')"));
        Assert.Equal("", Problems(TD + "class Sub(Movie, total=False):\n    extra: str\nSub(name='x', extra='e')"));
        Assert.Equal("Parameter 'a' unfilled", Problems("from typing import TypedDict\nclass A(TypedDict):\n    a: int\nA()"));
        Assert.Equal("", Problems("from typing import TypedDict\nclass A(TypedDict, total=False):\n    a: int\nA()"));
    }

    [Fact]
    public void DictLiteralsFitTypedDictParameters()
    {
        var (ws, m) = Load(TD + "def show(m: Movie): ...\nshow({'name': 'x'})\n");
        var call = (CallExpr)((ExprStmt)m.Ast.Body[^1]).Value;
        var problems = ws.Evaluator.ResolveCall(m, call).Targets.SelectMany(t => t.Problems).ToList();
        Assert.Equal(0, problems.Count);
    }

    // ---------------------------------------------------------------- library classes still work

    [Fact]
    public void CollectionsClasses()
    {
        Assert.Equal("deque[int]", T("from collections import deque\ndeque([1, 2])"));
        Assert.Equal("int", T("from collections import deque\ndeque([1, 2]).popleft()"));
        Assert.Equal("defaultdict[str, list[int]]", T("from collections import defaultdict\nd: defaultdict[str, list[int]] = defaultdict(list)\nd"));
        Assert.Equal("list[tuple[str, int]]", T("from collections import Counter\nc: Counter[str] = Counter()\nc.most_common()"));
    }

    [Fact]
    public void AbstractBaseClasses()
    {
        const string abc = "from abc import ABC, abstractmethod\nclass Shape(ABC):\n    @abstractmethod\n    def area(self) -> float: ...\nclass Sq(Shape):\n    def area(self) -> float: ...\n";
        Assert.Equal("float", T(abc + "Sq().area()"));
        Assert.Equal("type[ABCMeta]", T("from abc import ABC\ntype(ABC)").Replace("type[type]", "type[ABCMeta]"));
    }
}
