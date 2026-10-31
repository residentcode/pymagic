using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>PyCharm's inspections that need types (item 193): assert_type, cast, NewType, ClassVar, Final, type aliases, str() without __str__.</summary>
public class PyCharmTypeInspectionTests
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
    public void AssertTypeHoldsOrSaysWhatItGot()
    {
        string code = "from typing import assert_type\n\n\ndef f(a: int, b: str | None):\n    assert_type(a, int)\n    assert_type(b, str)\n    assert_type(b, None | str)\n    x = 1\n    assert_type(x, int)\n    assert_type(a, str)\n";
        var d = Found(Make(code), "assert-type");
        Assert.Equal("5,9", Lines(d));
        Assert.Equal("Expected type 'str', got 'int' instead", d[1].Message);
    }

    [Fact]
    public void ACastToItsOwnTypeOrToAnUnrelatedOne()
    {
        string code = "from typing import cast\n\n\nclass A: ...\nclass B(A): ...\nclass C: ...\n\n\ndef f(a: A, n: int, o: object):\n    cast(B, a)\n    cast(A, a)\n    cast(C, a)\n    cast(float, n)\n    cast(str, o)\n    cast(int, 'text')\n";
        var svc = Make(code);
        Assert.Equal("10", Lines(Found(svc, "unnecessary-cast")));
        Assert.Equal("11,14", Lines(Found(svc, "invalid-cast")));
        Assert.Equal("Cast of 'str' to 'int': the types are unrelated", Found(svc, "invalid-cast")[1].Message);
        var fix = svc.CodeActions(Uri, Found(svc, "unnecessary-cast")[0].Range).Single(a => a.Title == "Remove the cast");
        Assert.Equal("a", fix.Edit!.Changes[Uri].Single().NewText);
    }

    [Fact]
    public void NewTypeNamedRightOnAClassAndNotSubclassed()
    {
        string code = "from typing import NewType, Literal, Any\n\nUserId = NewType('UserId', int)\nOrderId = NewType('Order', int)\nBoth = NewType('Both', int | str)\nOne = NewType('One', Literal[1])\n\n\nclass Admin(UserId):\n    pass\n";
        var d = Found(Make(code), "newtype").Select(x => x.Message).ToList();
        Assert.Equal(4, d.Count);
        Assert.True(d[0].Contains("('Order') is not the variable's ('OrderId')"), d[0]);
        Assert.True(d[1].Contains("a union") && d[2].Contains("a Literal"), string.Join(" | ", d));
        Assert.Equal("'UserId' is a NewType: it cannot be subclassed", d[3]);
    }

    [Fact]
    public void ClassVarOnlyInAClassBodyAndNotThroughAnInstance()
    {
        string code = "from typing import ClassVar\n\ncount: ClassVar[int] = 0\n\n\nclass Shop:\n    total: ClassVar[int] = 0\n    items: list[ClassVar[int]] = []\n\n    def add(self, n: ClassVar[int]) -> None:\n        self.total = 1\n        Shop.total = 2\n\n\nclass Branch(Shop):\n    def reset(self):\n        self.total = 0\n";
        var d = Found(Make(code), "classvar");
        Assert.Equal("2,7,9,10,16", Lines(d));
        Assert.Equal("Cannot assign to class variable 'total' of Shop through an instance", d[3].Message);
    }

    [Fact]
    public void FinalWhereItCannotBeAndFinalOverridden()
    {
        string code = "from typing import Final, final\n\nLIMIT: Final\nOK: Final = 3\n\n\n@final\nclass Leaf:\n    pass\n\n\nclass Twig(Leaf):\n    pass\n\n\nclass Base:\n    RATE: Final = 2\n\n    @final\n    def run(self) -> None: ...\n\n    def walk(self) -> None: ...\n\n\nclass Sub(Base):\n    RATE = 3\n\n    def run(self) -> None: ...\n\n    def walk(self) -> None: ...\n\n\ndef f(x: Final[int]) -> None: ...\n";
        var svc = Make(code);
        Assert.Equal("2,32", Lines(Found(svc, "final-misuse")));
        var over = Found(svc, "final-override").Select(x => x.Message).ToList();
        Assert.Equal("'Leaf' is marked @final: it cannot be subclassed | 'Base.RATE' is Final: it cannot be overridden | 'Base.run' is marked @final: it cannot be overridden", string.Join(" | ", over));
    }

    [Fact]
    public void ATypeAliasDeclaredTwice()
    {
        string code = "from typing import TypeAlias\n\ntype Pair = tuple[int, int]\nIds: TypeAlias = list[int]\ntype Pair = tuple[str, str]\nIds: TypeAlias = set[int]\n";
        Assert.Equal("4,5", Lines(Found(Make(code), "type-alias-redeclared")));
    }

    [Fact]
    public void StrOfAClassWithoutStrOrRepr()
    {
        string code = "from dataclasses import dataclass\n\n\nclass Plain:\n    pass\n\n\nclass Shown:\n    def __repr__(self) -> str:\n        return 'Shown'\n\n\nclass Child(Shown):\n    pass\n\n\n@dataclass\nclass Point:\n    x: int\n\n\nstr(Plain())\nstr(Child())\nstr(Point(1))\nstr(3)\n";
        var d = Found(Make(code), "str-without-dunder");
        Assert.Equal("21", Lines(d));
        Assert.Contains("<Plain object at 0x...>", d[0].Message);
    }
}
