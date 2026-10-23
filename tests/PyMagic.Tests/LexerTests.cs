using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

public class LexerTests
{
    static string Dump(string src) =>
        string.Join(" ", Lexer.Tokenize(src).Select(t => t.Kind switch
        {
            TokenKind.Newline => "NL",
            TokenKind.Indent => "INDENT",
            TokenKind.Dedent => "DEDENT",
            TokenKind.EndOfFile => "EOF",
            TokenKind.Comment => "#",
            TokenKind.Error => "ERR",
            _ => t.Text(src),
        }));

    [Fact]
    public void SimpleAssignment() =>
        Assert.Equal("x = 1 NL EOF", Dump("x = 1"));

    [Fact]
    public void IndentAndDedent() =>
        Assert.Equal("if x : NL INDENT pass NL DEDENT EOF", Dump("if x:\n    pass\n"));

    [Fact]
    public void NestedDedentsAtEof() =>
        Assert.Equal("def f ( ) : NL INDENT if a : NL INDENT pass NL DEDENT DEDENT EOF",
            Dump("def f():\n  if a:\n    pass"));

    [Fact]
    public void BlankAndCommentLinesDoNotAffectIndent() =>
        Assert.Equal("if a : NL # INDENT b NL DEDENT EOF", Dump("if a:\n\n# c\n    b\n"));

    [Fact]
    public void NewlinesInsideBracketsAreIgnored() =>
        Assert.Equal("f ( 1 , 2 ) NL EOF", Dump("f(1,\n  2)\n"));

    [Fact]
    public void BackslashContinuation() =>
        Assert.Equal("a = 1 + 2 NL EOF", Dump("a = 1 + \\\n 2\n"));

    [Fact]
    public void Keywords()
    {
        var toks = Lexer.Tokenize("def match");
        Assert.True(toks[0].Kind == TokenKind.Keyword);
        Assert.True(toks[1].Kind == TokenKind.Name); // soft keyword stays a Name
    }

    [Fact]
    [Case("0x_FF")]
    [Case("1_000")]
    [Case("3.14")]
    [Case(".5")]
    [Case("1e-3")]
    [Case("2j")]
    [Case("0b101")]
    [Case("1.")]
    public void Numbers(string n) => Assert.Equal($"{n} NL EOF", Dump(n));

    [Fact]
    [Case("'abc'")]
    [Case("\"a\\\"b\"")]
    [Case("rb'\\d'")]
    [Case("f\"{x}\"")]
    [Case("'''multi\nline'''")]
    [Case("r'\\''")]
    public void Strings(string lit)
    {
        var toks = Lexer.Tokenize(lit);
        Assert.True(toks[0].Kind == TokenKind.String);
        Assert.Equal(lit.Length, toks[0].Length);
    }

    [Fact]
    public void UnterminatedStringIsError() =>
        Assert.Equal("ERR NL EOF", Dump("'abc"));

    [Fact]
    public void LongestOperatorWins() =>
        Assert.Equal("a **= b // c -> d := e ... NL EOF", Dump("a **= b // c -> d := e ..."));

    [Fact]
    public void InconsistentDedentIsReported() =>
        Assert.Contains("ERR", Dump("if a:\n    b\n  c\n"));

    [Fact]
    public void TabsCountToEight() =>
        Assert.Equal("if a : NL INDENT b NL c NL DEDENT EOF", Dump("if a:\n\tb\n        c\n"));

    [Fact]
    public void CrLfLineEndings() =>
        Assert.Equal("a NL b NL EOF", Dump("a\r\nb\r\n"));

    [Fact]
    public void UnicodeIdentifiers() =>
        Assert.Equal("é = 1 NL EOF", Dump("é = 1"));
}

