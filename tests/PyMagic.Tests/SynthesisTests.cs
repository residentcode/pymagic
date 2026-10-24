using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>Functional Enum and TypedDict, and dataclass_transform.</summary>
public class SynthesisTests
{
    static (Workspace Ws, ModuleInfo M) Load(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    static string T(string src)
    {
        var (ws, m) = Load(src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

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

    // ------------------------------------------------------------ functional Enum

    [Fact]
    public void EnumFromAStringOfNames()
    {
        const string pre = "from enum import Enum\nColor = Enum(\"Color\", \"RED GREEN BLUE\")\n";
        Assert.Equal("Color.RED", T(pre + "Color.RED"));
        Assert.Equal("int", T(pre + "Color.RED.value"));
        Assert.Equal("str", T(pre + "Color.RED.name"));
        Assert.Equal("Color", T(pre + "def f(c: Color):\n    c"));
        Assert.Equal("Color", T(pre + "for c in Color:\n    c"));
        Assert.Equal("Color.RED", T(pre + "def f(c: Color):\n    if c is Color.RED:\n        c"));
        Same("Color.GREEN | Color.BLUE", T(pre + "def f(c: Color):\n    if c is Color.RED:\n        pass\n    else:\n        c"));
    }

    [Fact]
    public void EnumFromListsPairsAndDicts()
    {
        Assert.Equal("Animal.CAT", T("from enum import IntEnum\nAnimal = IntEnum(\"Animal\", [\"CAT\", \"DOG\"])\nAnimal.CAT"));
        Assert.Equal("str", T("from enum import Enum\nPair = Enum(\"Pair\", [(\"A\", 1), (\"B\", \"x\")])\nPair.B.value"));
        Assert.Equal("int", T("from enum import Enum\nShape = Enum(\"Shape\", {\"SQ\": 4, \"TRI\": 3})\nShape.TRI.value"));
    }

    [Fact]
    public void UnknownMembersAreReportedAndComputedNamesAreNot()
    {
        var (ws, m) = Load("from enum import Enum\nColor = Enum(\"Color\", \"RED GREEN\")\nColor");
        var color = ws.Evaluator.TypeOf(m, ((ExprStmt)m.Ast.Body[^1]).Value);
        Assert.True(ws.Evaluator.AttributeType(color, "PURPLE").Missing.Count > 0, "PURPLE is not a member");
        Assert.True(ws.Evaluator.AttributeType(color, "RED").Missing.Count == 0, "RED is");

        var (ws2, m2) = Load("from enum import Enum\nnames = input().split()\nDyn = Enum(\"Dyn\", names)\nDyn");
        var dyn = ws2.Evaluator.TypeOf(m2, ((ExprStmt)m2.Ast.Body[^1]).Value);
        Assert.True(ws2.Evaluator.AttributeType(dyn, "ANYTHING").Missing.Count == 0, "computed members are not reported");
    }

    [Fact]
    public void CallingAnEnumClassStillLooksUpByValue()
    {
        Assert.Equal("Color", T("from enum import Enum\nclass Color(Enum):\n    RED = 1\nColor(1)"));
    }

    // ------------------------------------------------------------ functional TypedDict

    [Fact]
    public void TypedDictFromADict()
    {
        const string pre = "from typing import TypedDict\nMovie = TypedDict(\"Movie\", {\"name\": str, \"year\": int})\n";
        Assert.Equal("str", T(pre + "def f(m: Movie):\n    m[\"name\"]"));
        Assert.Equal("int", T(pre + "def f(m: Movie):\n    m[\"year\"]"));
        Assert.Equal("Movie", T(pre + "Movie(name=\"a\", year=1)"));
        Assert.Contains("year", Problems(pre + "Movie(name=\"a\")"));
        Assert.Equal("", Problems(pre + "Movie(name=\"a\", year=1)"));
    }

    [Fact]
    public void TypedDictFromKeywordsAndTotalFalse()
    {
        Assert.Equal("str", T("from typing import TypedDict\nM = TypedDict(\"M\", name=str)\ndef f(m: M):\n    m[\"name\"]"));
        Assert.Equal("", Problems("from typing import TypedDict\nOpt = TypedDict(\"Opt\", {\"a\": int}, total=False)\nOpt()"));
        Assert.Contains("a", Problems("from typing import TypedDict\nReq = TypedDict(\"Req\", {\"a\": int})\nReq()"));
    }

    // ------------------------------------------------------------ dataclass_transform

    [Fact]
    public void ADecoratorMarkedWithDataclassTransform()
    {
        const string pre = "from typing import dataclass_transform\n@dataclass_transform(kw_only_default=True)\ndef model(cls):\n    return cls\n@model\nclass User:\n    id: int\n    name: str = \"x\"\n";
        Assert.Equal("", Problems(pre + "User(id=1)"));
        Assert.Equal("", Problems(pre + "User(id=1, name=\"y\")"));
        Assert.True(Problems(pre + "User(1)").Length > 0, "keyword-only fields reject positional arguments");
        Assert.Contains("id", Problems(pre + "User()"));
        Assert.Equal("int", T(pre + "User(id=1).id"));
    }

    [Fact]
    public void DecoratorOptionsOverrideTheDefaults()
    {
        const string pre = "from typing import dataclass_transform\n@dataclass_transform(kw_only_default=True)\ndef model(cls=None, *, kw_only=True):\n    return cls\n@model(kw_only=False)\nclass P:\n    x: int\n";
        Assert.Equal("", Problems(pre + "P(1)"));
    }

    [Fact]
    public void AMetaclassMarkedWithDataclassTransform()
    {
        const string pre = "from typing import dataclass_transform\n@dataclass_transform()\nclass Meta(type): ...\nclass Base(metaclass=Meta): ...\nclass Item(Base):\n    sku: str\n    qty: int = 1\n";
        Assert.Equal("", Problems(pre + "Item(\"a\")"));
        Assert.Equal("", Problems(pre + "Item(\"a\", 2)"));
        Assert.Contains("sku", Problems(pre + "Item()"));
        Assert.Equal("int", T(pre + "Item(\"a\").qty"));
    }

    [Fact]
    public void ABaseClassMarkedWithDataclassTransform()
    {
        const string pre = "from typing import dataclass_transform\n@dataclass_transform()\nclass Base: ...\nclass P(Base):\n    x: int\n    y: int = 0\n";
        Assert.Equal("", Problems(pre + "P(1, 2)"));
        Assert.Contains("x", Problems(pre + "P()"));
    }

    [Fact]
    public void FieldSpecifiersDecideWhichFieldsHaveDefaults()
    {
        const string pre = "from typing import dataclass_transform\ndef Field(*args, **kwargs): ...\n@dataclass_transform(kw_only_default=True, field_specifiers=(Field,))\ndef model(cls):\n    return cls\n@model\nclass M:\n    a: int = Field(default=3)\n    b: int = Field(...)\n    c: int = Field(5)\n";
        Assert.Equal("", Problems(pre + "M(b=1)"));
        Assert.Contains("b", Problems(pre + "M()"));
    }

    [Fact]
    public void ClassesWithoutATransformAreNotDataclasses()
    {
        Assert.Equal("", Problems("class Plain:\n    x: int\nPlain()"));
        Assert.Equal("", Problems("def deco(cls):\n    return cls\n@deco\nclass C:\n    x: int\nC()"));
    }
}
