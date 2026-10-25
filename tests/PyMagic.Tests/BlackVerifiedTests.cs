using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Cases whose expected text was produced by black 26.10 itself (run on the input of each pair), not worked out by hand.</summary>
public class BlackVerifiedTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Fmt(string text) =>
        PythonFormatter.Format(text) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):\n" + text);

    /// <summary>Each pair is one statement: what is written, and what black makes of it.</summary>
    static void Each(params string[] pairs)
    {
        for (int i = 0; i < pairs.Length; i += 2)
        {
            string once = Fmt(pairs[i] + "\n");
            Assert.Equal(pairs[i + 1] + "\n", once);
            Assert.Equal(once, Fmt(once));
        }
    }

    [Fact]
    public void SlicesWithComplexBoundsAreSpacedLikeOperators()
    {
        Each(
            "a = x[1:2]", "a = x[1:2]",
            "a = x[i+1:]", "a = x[i + 1 :]",
            "a = x[:i+1]", "a = x[: i + 1]",
            "a = x[i+1:j+1]", "a = x[i + 1 : j + 1]",
            "a = x[i+1:j+1:k]", "a = x[i + 1 : j + 1 : k]",
            "a = x[i:j:k+1]", "a = x[i : j : k + 1]",
            "a = x[i::k+1]", "a = x[i :: k + 1]",
            "a = x[::k+1]", "a = x[:: k + 1]",
            "a = x[a.b:c]", "a = x[a.b : c]",
            "a = x[f(a):]", "a = x[f(a) :]",
            "a = x[-1:]", "a = x[-1:]",
            "a = x[-i:]", "a = x[-i:]",
            "a = x[:-i]", "a = x[:-i]",
            "a = x[not a:]", "a = x[not a :]",
            "a = x[a:b, c+1:d]", "a = x[a:b, c + 1 : d]",
            "a = x[a+1:b, c:d]", "a = x[a + 1 : b, c:d]",
            "a = x[(a+1):]", "a = x[(a + 1) :]",
            "a = x[a[1]:]", "a = x[a[1] :]",
            "a = x[x:y if z else w]", "a = x[x : y if z else w]",
            "a = x[1:2, ::3]", "a = x[1:2, ::3]",
            "a = x[a**2:]", "a = x[a**2 :]",
            "a = x[a:b, :]", "a = x[a:b, :]",
            "a = x[~a:]", "a = x[~a:]",
            "a = x[1 : 2]", "a = x[1:2]",
            "a = x[len(a):len(b)]", "a = x[len(a) : len(b)]",
            "a = x[a:b][c+1:]", "a = x[a:b][c + 1 :]",
            "a = x[[1,2]:]", "a = x[[1, 2]:]");
    }

    [Fact]
    public void ABlankLineIsKeptAtTheStartOfABlockExceptBeforeADocstring()
    {
        Assert.Equal(Src("class A:", "", "    x = 1", "", "    def f(self):", "", "        # c", "        y = 1"),
            Fmt(Src("class A:", "", "", "    x = 1", "", "    def f(self):", "", "", "        # c", "        y = 1")));
        Assert.Equal(Src("def a():", "    \"\"\"doc\"\"\"", "", "    return 1"), Fmt(Src("def a():", "", "    '''doc'''", "", "    return 1")));
        Assert.Equal(Src("def c(x):", "    if x:", "", "        return 1", "    else:", "", "        return 2"),
            Fmt(Src("def c(x):", "    if x:", "", "        return 1", "    else:", "", "        return 2")));
        Assert.Equal(Src("@dec", "def h():", "    pass"), Fmt(Src("@dec", "", "def h(): pass")));
    }

    [Fact]
    public void OneBlankLineFollowsTheImportsAndADefInsideABlockGetsOne()
    {
        Assert.Equal(Src("import a", "", "x = 1"), Fmt(Src("import a", "", "", "x = 1")));
        Assert.Equal(Src("import a", "", "# c", "x = 1"), Fmt(Src("import a", "", "", "# c", "x = 1")));
        Assert.Equal(Src("if x:", "    y = 1", "    if z:", "", "        def f():", "            pass"),
            Fmt(Src("if x:", "    y = 1", "    if z:", "        def f(): pass")));
        // a comment that opens the block belongs to the def below it
        Assert.Equal(Src("if x:", "    # use this", "    def f():", "        pass"), Fmt(Src("if x:", "    # use this", "    def f(): pass")));
    }

    [Fact]
    public void TwoBlankLinesFollowTheModuleDocstringBeforeADefButOneBeforeADecorator()
    {
        Assert.Equal(Src("\"\"\"d\"\"\"", "", "", "def f():", "    pass"), Fmt(Src("\"\"\"d\"\"\"", "def f(): pass")));
        Assert.Equal(Src("\"\"\"d\"\"\"", "", "", "# c", "def f():", "    pass"), Fmt(Src("\"\"\"d\"\"\"", "# c", "def f(): pass")));
        Assert.Equal(Src("\"\"\"d\"\"\"", "", "", "class A:", "    pass"), Fmt(Src("\"\"\"d\"\"\"", "class A: pass")));
        Assert.Equal(Src("\"\"\"d\"\"\"", "", "@x", "def f():", "    pass"), Fmt(Src("\"\"\"d\"\"\"", "@x", "def f(): pass")));
        Assert.Equal(Src("\"\"\"d\"\"\"", "", "x = 1"), Fmt(Src("\"\"\"d\"\"\"", "", "", "x = 1")));
    }

    [Fact]
    public void ADependentClauseAfterAnIndentedDefIsOneBlankLineAway()
    {
        Assert.Equal(Src("if a:", "", "    def f():", "        pass", "", "else:", "", "    def g():", "        pass"),
            Fmt(Src("if a:", "    def f():", "        pass", "else:", "    def g(): pass")));
    }

    [Fact]
    public void QuotesFollowBlacksEscapeCount()
    {
        Each(
            "f = 'it\\'s'", "f = \"it's\"",
            "b = 'it\"s'", "b = 'it\"s'",
            "c = 'say \"hi\" and it\\'s'", "c = 'say \"hi\" and it\\'s'",
            "d = \"a \\\"quoted\\\" word\"", "d = 'a \"quoted\" word'",
            "e = '\\''", "e = \"'\"",
            "i = 'a\\\"b'", "i = 'a\"b'",
            "h = f'{x[\"a\"]}'", "h = f'{x[\"a\"]}'",
            "r = r'[\\x00-\\x1f\\\\\"\\b]'", "r = r'[\\x00-\\x1f\\\\\"\\b]'");
    }

    [Fact]
    public void StringPrefixesAndEscapesAreSpelledLikeBlack()
    {
        Each(
            "a = br\"x\"", "a = rb\"x\"",
            "a = bR\"x\"", "a = Rb\"x\"",
            "a = BR\"x\"", "a = Rb\"x\"",
            "a = U\"x\"", "a = \"x\"",
            "a = F\"x\"", "a = f\"x\"",
            "a = fR\"x\"", "a = Rf\"x\"",
            "a = B\"x\"", "a = b\"x\"",
            "a = \"\\xAB\\N{em dash}\\\\xAB\"", "a = \"\\xab\\N{EM DASH}\\\\xAB\"",
            "a = b\"\\xAB\\\\xAB\"", "a = b\"\\xab\\\\xAB\"",
            "a = rb\"\\xAB\"", "a = rb\"\\xAB\"",
            "a = f\"\\xAB{x}\"", "a = f\"\\xab{x}\"");
    }

    [Fact]
    public void OneElementTuplesGetTheirParentheses()
    {
        Each(
            "x = \"a\",", "x = (\"a\",)",
            "x = y = 1,", "x = y = (1,)",
            "x += 1,", "x += (1,)",
            "a, = y", "(a,) = y",
            "a, = b, = y", "(a,) = (b,) = y",
            "x: tuple = 1,", "x: tuple = (1,)",
            "a, b = 1,", "a, b = (1,)",
            "x = 1, 2", "x = 1, 2",
            "del a,", "del (a,)",
            "del (a, b)", "del (a, b)",
            "del (a)", "del a");
        Assert.Equal(Src("def f():", "    return (1,)"), Fmt(Src("def f():", "    return 1,")));
        Assert.Equal(Src("def g():", "    yield 1,"), Fmt(Src("def g():", "    yield 1,")));
        Assert.Equal(Src("for (x,) in (1,):", "    pass"), Fmt(Src("for x, in 1,: pass")));
        Assert.Equal(Src("t = (", "    1,", "    2,", ")"), Fmt(Src("t = 1, 2,")));
    }
}
