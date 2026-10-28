using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The inspections of how classes and functions are put together (item 102): what they report and what they leave alone.</summary>
public class StructureInspectionTests
{
    const string Main = "file:///c:/proj/main.py";

    static List<LspDiagnostic> Found(string src, string code)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        return svc.Diagnostics(Main).Where(d => d.Code == code).ToList();
    }

    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    // ------------------------------------------------------------ dataclass

    [Fact]
    public void ADataclassFieldWithoutDefaultAfterOneWithDefault()
    {
        var found = Found(Src("from dataclasses import dataclass", "", "@dataclass", "class P:", "    x: int = 0", "    y: int"), "dataclass-fields");
        Assert.Equal(1, found.Count);
        Assert.Contains("'y'", found[0].Message);
        Assert.Contains("'x'", found[0].Message);
    }

    [Fact]
    public void DataclassFieldsInOrderOrOutsideTheInitAreFine()
    {
        string head = "import dataclasses\nfrom dataclasses import dataclass, field, KW_ONLY\nfrom typing import ClassVar\n\n";
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    x: int", "    y: int = 0"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    x: int = 0", "    count: ClassVar[int]"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    x: int = 0", "    y: int = field(init=False)"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    x: int = 0", "    _: KW_ONLY", "    y: int"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass(kw_only=True)", "class A:", "    x: int = 0", "    y: int"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    x: int = 0", "    y: int = field(kw_only=True)"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclasses.dataclass", "class A:", "    x: int", "    y: list = field(default_factory=list)"), "dataclass-fields").Count);
        Assert.Equal(0, Found(Src("class A:", "    x: int = 0", "    y: int"), "dataclass-fields").Count);   // not a dataclass
    }

    [Fact]
    public void ADataclassAfterABaseWithDefaultsMustHaveDefaultsToo()
    {
        string src = Src("from dataclasses import dataclass", "", "@dataclass", "class Base:", "    a: int = 1", "", "@dataclass", "class Child(Base):", "    b: int");
        var found = Found(src, "dataclass-fields");
        Assert.Equal(1, found.Count);
        Assert.Contains("'b'", found[0].Message);
        // redefining the base field keeps its place
        Assert.Equal(0, Found(Src("from dataclasses import dataclass", "", "@dataclass", "class Base:", "    a: int", "    b: int = 1", "", "@dataclass", "class Child(Base):", "    a: int = 3"), "dataclass-fields").Count);
    }

    [Fact]
    public void AMutableDataclassDefault()
    {
        string head = "from dataclasses import dataclass, field\n\n";
        Assert.Equal(1, Found(head + Src("@dataclass", "class A:", "    items: list = []"), "dataclass-fields").Count);
        Assert.Equal(1, Found(head + Src("@dataclass", "class A:", "    d: dict = {}"), "dataclass-fields").Count);
        Assert.Equal(1, Found(head + Src("@dataclass", "class A:", "    s: set = set()"), "dataclass-fields").Count);
        Assert.Equal(1, Found(head + Src("@dataclass", "class A:", "    items: list = field(default=[])"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    items: list = field(default_factory=list)"), "dataclass-fields").Count);
        Assert.Equal(0, Found(head + Src("@dataclass", "class A:", "    items: tuple = ()"), "dataclass-fields").Count);
        Assert.Equal(0, Found(Src("class A:", "    items: list = []"), "dataclass-fields").Count);   // an ordinary class
    }

    // ------------------------------------------------------------ named tuple

    [Fact]
    public void ANamedTupleFieldAfterADefault()
    {
        var found = Found(Src("from typing import NamedTuple", "", "class P(NamedTuple):", "    x: int = 0", "    y: int"), "namedtuple-fields");
        Assert.Equal(1, found.Count);
        Assert.Contains("'y'", found[0].Message);
        Assert.Equal(0, Found(Src("from typing import NamedTuple", "", "class P(NamedTuple):", "    x: int", "    y: int = 0"), "namedtuple-fields").Count);
    }

    // ------------------------------------------------------------ enum

    [Fact]
    public void AnEnumerationWithMembersCannotBeExtended()
    {
        string head = "from enum import Enum\n\nclass Color(Enum):\n    RED = 1\n    BLUE = 2\n\n";
        var found = Found(head + Src("class More(Color):", "    GREEN = 3"), "enum-extend");
        Assert.Equal(1, found.Count);
        Assert.Contains("'Color'", found[0].Message);
        // an enumeration without members is a base for others
        string open = "from enum import Enum\n\nclass Base(Enum):\n    def label(self):\n        return self.name\n\n";
        Assert.Equal(0, Found(open + Src("class Color(Base):", "    RED = 1"), "enum-extend").Count);
        Assert.Equal(0, Found(Src("from enum import Enum", "", "class Color(Enum):", "    RED = 1"), "enum-extend").Count);
        Assert.Equal(0, Found(Src("class Color:", "    RED = 1", "", "class More(Color):", "    GREEN = 2"), "enum-extend").Count);
    }

    // ------------------------------------------------------------ abstract classes and protocols

    [Fact]
    public void AnAbstractClassCannotBeInstantiated()
    {
        string abc = Src("from abc import ABC, abstractmethod", "", "class Shape(ABC):", "    @abstractmethod", "    def area(self): ...", "", "class Square(Shape):", "    def area(self):", "        return 1", "");
        var found = Found(abc + Src("s = Shape()"), "abstract-instantiate");
        Assert.Equal(1, found.Count);
        Assert.Contains("area", found[0].Message);
        Assert.Equal(0, Found(abc + Src("s = Square()"), "abstract-instantiate").Count);
        // a class method that makes instances of its own class (cls) is not looked at
        Assert.Equal(0, Found(Src("from abc import ABC, abstractmethod", "", "class Shape(ABC):", "    @abstractmethod", "    def area(self): ...", "", "    @classmethod", "    def make(cls):", "        return cls()"), "abstract-instantiate").Count);
        // without ABCMeta the decorator does nothing at run time
        Assert.Equal(0, Found(Src("from abc import abstractmethod", "", "class Shape:", "    @abstractmethod", "    def area(self): ...", "", "s = Shape()"), "abstract-instantiate").Count);
    }

    [Fact]
    public void AProtocolClassCannotBeInstantiated()
    {
        string src = Src("from typing import Protocol", "", "class Reader(Protocol):", "    def read(self) -> str: ...", "", "class File(Reader):", "    def read(self) -> str:", "        return ''", "", "a = Reader()", "b = File()");
        var found = Found(src, "abstract-instantiate");
        Assert.Equal(1, found.Count);
        Assert.Contains("'Reader'", found[0].Message);
    }

    // ------------------------------------------------------------ TypedDict

    [Fact]
    public void TypedDictKeysThatDoNotExist()
    {
        string head = Src("from typing import TypedDict, NotRequired", "", "class Movie(TypedDict):", "    name: str", "    year: NotRequired[int]", "");
        var unknown = Found(head + Src("m = Movie(name='x', rating=3)"), "typeddict-keys");
        Assert.Equal(1, unknown.Count);
        Assert.Contains("'rating'", unknown[0].Message);
        var missing = Found(head + Src("m = Movie(year=3)"), "typeddict-keys");
        Assert.Equal(1, missing.Count);
        Assert.Contains("'name'", missing[0].Message);
        Assert.Equal(0, Found(head + Src("m = Movie(name='x')", "n = Movie(name='y', year=1)"), "typeddict-keys").Count);

        var read = Found(head + Src("def f(m: Movie):", "    return m['title'] + m['name']"), "typeddict-keys");
        Assert.Equal(1, read.Count);
        Assert.Contains("'title'", read[0].Message);
        Assert.Equal(0, Found(head + Src("def f(m: Movie):", "    return m['year'], m['name']"), "typeddict-keys").Count);
        Assert.Equal(0, Found(Src("def f(d: dict[str, int]):", "    return d['anything']"), "typeddict-keys").Count);
    }

    // ------------------------------------------------------------ overloads

    [Fact]
    public void OverloadsNeedASecondOneAndAnImplementation()
    {
        string two = Src("from typing import overload", "", "@overload", "def f(x: int) -> int: ...", "@overload", "def f(x: str) -> str: ...", "def f(x):", "    return x");
        Assert.Equal(0, Found(two, "overload-structure").Count);

        string alone = Src("from typing import overload", "", "@overload", "def f(x: int) -> int: ...", "def f(x):", "    return x");
        var lone = Found(alone, "overload-structure");
        Assert.Equal(1, lone.Count);
        Assert.Contains("only one", lone[0].Message);

        string none = Src("from typing import overload", "", "@overload", "def f(x: int) -> int: ...", "@overload", "def f(x: str) -> str: ...");
        var missing = Found(none, "overload-structure");
        Assert.Equal(1, missing.Count);
        Assert.Contains("no implementation", missing[0].Message);
    }

    [Fact]
    public void OverloadsInAClassAndInAProtocol()
    {
        string method = Src("from typing import overload", "", "class C:", "    @overload", "    def f(self, x: int) -> int: ...", "    @overload", "    def f(self, x: str) -> str: ...", "    def f(self, x):", "        return x");
        Assert.Equal(0, Found(method, "overload-structure").Count);
        string proto = Src("from typing import Protocol, overload", "", "class P(Protocol):", "    @overload", "    def f(self, x: int) -> int: ...", "    @overload", "    def f(self, x: str) -> str: ...");
        Assert.Equal(0, Found(proto, "overload-structure").Count);
        string abstractClass = Src("from abc import ABC, abstractmethod", "from typing import overload", "", "class C(ABC):", "    @abstractmethod", "    def g(self): ...", "    @overload", "    @abstractmethod", "    def f(self, x: int) -> int: ...", "    @overload", "    @abstractmethod", "    def f(self, x: str) -> str: ...");
        Assert.Equal(0, Found(abstractClass, "overload-structure").Count);
    }

    [Fact]
    public void TheNewInspectionsCanBeSwitchedOffWithNoqa()
    {
        string src = Src("from dataclasses import dataclass", "", "@dataclass", "class P:", "    x: int = 0", "    y: int  # noqa: dataclass-fields");
        Assert.Equal(0, Found(src, "dataclass-fields").Count);
    }

    [Fact]
    public void WhatTheStandardLibraryShowedToBeFalseAlarms()
    {
        // init=False: no __init__ is generated, so the order of the fields does not matter
        Assert.Equal(0, Found(Src("from dataclasses import dataclass", "", "@dataclass(init=False)", "class P:", "    x: int = 0", "    y: int"), "dataclass-fields").Count);
        // a name that holds "some subclass" (declared type[Base], then bound by imports) is not the abstract class itself
        string src = Src("from abc import ABC, abstractmethod", "", "class Base(ABC):", "    @abstractmethod", "    def run(self): ...", "", "class Impl(Base):", "    def run(self): ...", "", "Console: type[Base]", "Console = Impl", "", "c = Console()");
        Assert.Equal(0, Found(src, "abstract-instantiate").Count);
    }
}
