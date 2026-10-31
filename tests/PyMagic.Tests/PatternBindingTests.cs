using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The names a match pattern binds have the type of the part of the subject they match: captures, `as`, class pattern arguments, sequence items and stars, mapping values.</summary>
public class PatternBindingTests
{
    const string Pre = "from typing import NamedTuple, TypedDict\nfrom dataclasses import dataclass\n\n@dataclass\nclass Point:\n    x: int\n    y: str\n\nclass Pair:\n    __match_args__ = (\"left\", \"right\")\n    def __init__(self, left: int, right: bytes) -> None:\n        self.left = left\n        self.right = right\n\nclass Row(NamedTuple):\n    id: int\n    label: str\n\nclass Movie(TypedDict):\n    title: str\n    year: int\n\n";

    static string T(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static void Same(string expected, string actual)
    {
        string Norm(string s) => string.Join(" | ", s.Split(" | ").OrderBy(x => x, StringComparer.Ordinal));
        Assert.Equal(Norm(expected), Norm(actual));
    }

    static List<string> Reports(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", Pre + src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return ws.Evaluator.Inspect(m, true).Select(d => d.Message).ToList();
    }

    const string Mixed = "def f(v: int | str | list[int] | None):\n    match v:\n";

    [Fact]
    public void AnAsNameHasTheTypeItsPatternNarrowsTo()
    {
        Same("int", T(Mixed + "        case int() as n:\n            n\n"));
    }

    [Fact]
    public void ACaptureHasTheTypeTheEarlierCasesLeave()
    {
        Same("str | list[int]", T(Mixed + "        case int() | None:\n            pass\n        case rest:\n            rest\n"));
    }

    [Fact]
    public void ABuiltinClassPatternBindsTheWholeSubject()
    {
        Same("str", T(Mixed + "        case str(s):\n            s\n"));
    }

    [Fact]
    public void AStarOverAListIsAListOfItsItems()
    {
        Same("list[int]", T(Mixed + "        case [first, *others]:\n            others\n"));
    }

    [Fact]
    public void TheItemsOfAFixedTupleAreBoundByPosition()
    {
        Same("bytes", T("def f(t: tuple[int, str, bytes]):\n    match t:\n        case (a, b, c):\n            c\n"));
    }

    [Fact]
    public void AStarOverAFixedTupleIsAListOfTheItemsItTakes()
    {
        Same("list[str | bytes]", T("def f(t: tuple[int, str, bytes, float]):\n    match t:\n        case (a, *middle, z):\n            middle\n"));
    }

    [Fact]
    public void ItemSubPatternsNarrowTheNamesTheyBind()
    {
        Same("int", T("def f(t: tuple[int | str, bytes]):\n    match t:\n        case (int() as n, _):\n            n\n"));
    }

    [Fact]
    public void KeywordArgumentsOfAClassPatternBindTheAttribute()
    {
        Same("str", T("def f(p: Point | None):\n    match p:\n        case Point(y=label):\n            label\n"));
    }

    [Fact]
    public void PositionalArgumentsFollowTheFieldsOfADataclass()
    {
        Same("str", T("def f(p: Point | None):\n    match p:\n        case Point(px, py):\n            py\n"));
    }

    [Fact]
    public void PositionalArgumentsFollowMatchArgs()
    {
        Same("bytes", T("def f(p: Pair):\n    match p:\n        case Pair(l, r):\n            r\n"));
    }

    [Fact]
    public void PositionalArgumentsFollowTheFieldsOfANamedTuple()
    {
        Same("str", T("def f(r: Row):\n    match r:\n        case Row(i, text):\n            text\n"));
    }

    [Fact]
    public void AMappingValueHasTheValueType()
    {
        Same("int", T("def f(d: dict[str, int] | None):\n    match d:\n        case {\"k\": value}:\n            value\n"));
    }

    [Fact]
    public void TheRestOfAMappingIsADictOfItsKeysAndValues()
    {
        Same("dict[str, int]", T("def f(d: dict[str, int]):\n    match d:\n        case {\"k\": value, **others}:\n            others\n"));
    }

    [Fact]
    public void ATypedDictKeyHasThatKeysType()
    {
        Same("int", T("def f(m: Movie):\n    match m:\n        case {\"year\": y}:\n            y\n"));
    }

    [Fact]
    public void ANameInAlternativesIsTheUnionOfWhatEachGives()
    {
        Same("int | bytes", T("def f(v: int | bytes | str):\n    match v:\n        case int(n) | bytes(n):\n            n\n"));
    }

    [Fact]
    public void ANoneAsNameIsNone()
    {
        Same("None", T("def f(p: Point | None):\n    match p:\n        case Point():\n            pass\n        case None as nothing:\n            nothing\n"));
    }

    [Fact]
    public void ASubjectNothingIsKnownAboutBindsUnknown()
    {
        Same("Unknown", T("def f(v):\n    match v:\n        case int() as n:\n            n\n"));
    }

    [Fact]
    public void AGuardNarrowsTheCaptureAndTheNextCaseGetsAllOfIt()
    {
        Same("int | str | list[int] | None", T(Mixed + "        case x if x is not None:\n            pass\n        case y:\n            y\n"));
    }

    [Fact]
    public void ReportsInTheCaseBodyUseTheBoundTypes()
    {
        var found = Reports("def f(items: list[tuple[Point, Row]]):\n    for item in items:\n        match item:\n            case (point, Row(label=text)):\n                point.x\n                text.upper()\n                point.missing\n");
        Assert.Equal(1, found.Count);
        Assert.True(found[0].Contains("'missing'") && found[0].Contains("Point"), found[0]);
    }
}
