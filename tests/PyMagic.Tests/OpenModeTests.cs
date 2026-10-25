using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>open() by its mode, with the bundled builtins and with typeshed's (whose modes are TypeAlias unions of literals).</summary>
public class OpenModeTests
{
    static string Last(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static void IsOneOf(string actual, params string[] expected) =>
        Assert.True(expected.Contains(actual), $"{actual} is not one of {string.Join(", ", expected)}");

    [Fact]
    public void TextModesGiveATextFile()
    {
        IsOneOf(Last("f = open('a')\nf"), "TextIO", "TextIOWrapper");
        IsOneOf(Last("f = open('a', 'w')\nf"), "TextIO", "TextIOWrapper");
        IsOneOf(Last("f = open('a', mode='r+', encoding='utf-8')\nf"), "TextIO", "TextIOWrapper");
    }

    [Fact]
    public void BinaryModesGiveABinaryFile()
    {
        IsOneOf(Last("f = open('a', 'rb')\nf"), "BinaryIO", "BufferedReader");
        IsOneOf(Last("f = open('a', 'wb')\nf"), "BinaryIO", "BufferedWriter");
        Assert.Equal("bytes", Last("with open('a', 'rb') as f:\n    data = f.read()\ndata"));
        Assert.Equal("str", Last("with open('a') as f:\n    data = f.read()\ndata"));
    }

    [Fact]
    public void AModeNotKnownGivesSomeFile()
    {
        string t = Last("def g(mode: str):\n    f = open('a', mode)\n    f");
        Assert.True(t.Contains("IO") || t == "Unknown", t);   // which of the overloads a plain str picks is left open
    }
}
