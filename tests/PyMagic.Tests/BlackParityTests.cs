using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

/// <summary>Docstrings, redundant parentheses and comments inside brackets; the expected texts are what black produces.</summary>
public class BlackParityTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";

    static string Fmt(string text) =>
        PythonFormatter.Format(text) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):\n" + text);

    static void Same(string expected, string input)
    {
        string once = Fmt(input);
        Assert.Equal(expected, once);
        Assert.Equal(expected, Fmt(once));
    }

    // ------------------------------------------------------------ docstrings

    [Fact]
    public void DocstringsLoseTheirOuterWhitespaceAndUseDoubleQuotes()
    {
        Same(Src("def f():", "    \"\"\"Hello world\"\"\"", "    pass"), Src("def f():", "    '''   Hello world   '''", "    pass"));
        Same(Src("def f():", "    \"doc\"", "    pass"), Src("def f():", "    'doc'", "    pass"));
        Same(Src("def f():", "    \"\"\" \"\"\"", "    pass"), Src("def f():", "    '''      '''", "    pass"));
        Same(Src("def f():", "    \"\"\" \"\"\"", "    pass"), Src("def f():", "    '''", "    ", "    '''", "    pass"));
    }

    [Fact]
    public void MultilineDocstringsAreReindentedAndKeepTheirClosingQuotes()
    {
        Same(Src("def f():", "    \"\"\"Multiline", "    docstring", "    \"\"\"", "    pass"),
            Src("def f():", "    '''Multiline", "    docstring", "    '''", "    pass"));
        Same(Src("class A:", "    def f(self):", "        \"\"\"Summary.", "", "          Indented block.", "        Less indented.", "        \"\"\"", "        pass"),
            Src("class A:", "    def f(self):", "        \"\"\"Summary.", "", "            Indented block.", "          Less indented.", "        \"\"\"", "        pass"));
        Same(Src("def f():", "    \"\"\"", "    Starts on the next line.", "", "    Trailing spaces go.", "    \"\"\"", "    pass"),
            Src("def f():", "    \"\"\"", "      Starts on the next line.   ", "", "      Trailing spaces go.  ", "      \"\"\"", "    pass"));
    }

    [Fact]
    public void ModuleAndClassDocstringsAreFormattedToo()
    {
        Same(Src("\"\"\"Module doc.\"\"\"", "", "x = 1"), Src("'''   Module doc.  '''", "x = 1"));
        Same(Src("class A:", "    \"\"\"Doc.\"\"\"", "", "    x = 1"), Src("class A:", "    '''  Doc.'''", "    x = 1"));
    }

    [Fact]
    public void QuotesAtTheEdgesOfADocstringGetAPaddingSpace()
    {
        Same(Src("def f():", "    \"\"\" \"quoted\" \"\"\"", "    pass"), Src("def f():", "    \"\"\"\"quoted\"\"\"\"", "    pass").Replace("\"\"\"\"quoted\"\"\"\"", "\"\"\" \"quoted\" \"\"\""));
        Same(Src("def f():", "    '''\"hey yah\"'''", "    pass"), Src("def f():", "    '''\"hey yah\"'''", "    pass"));
    }

    [Fact]
    public void AClosingQuoteThatDoesNotFitMovesToItsOwnLine()
    {
        string last = new string('x', 82);
        Same(Src("def f():", "    \"\"\"Summary.", "", "    " + last, "    \"\"\"", "    pass"),
            Src("def f():", "    \"\"\"Summary.", "", "    " + last + "\"\"\"", "    pass"));
        string shortLast = new string('x', 40);
        Same(Src("def f():", "    \"\"\"Summary.", "", "    " + shortLast + "\"\"\"", "    pass"),
            Src("def f():", "    \"\"\"Summary.", "", "    " + shortLast + "\"\"\"", "    pass"));
    }

    [Fact]
    public void DocstringsWithBackslashContinuationsAndNonDocstringStringsAreKept()
    {
        string keep = "def f():\n    \"\"\"a => \\\n    b\"\"\"\n    pass\n";
        Same(keep, keep);
        Same(Src("def f():", "    x = 1", "    '''  not a docstring  '''".Replace("'''", "\"\"\"")), Src("def f():", "    x = 1", "    '''  not a docstring  '''"));
    }

    // ------------------------------------------------------------ redundant parentheses

    [Fact]
    public void ParenthesesAroundAWholeValueAreRemoved()
    {
        Same(Src("def f():", "    return x", "", "", "if a:", "    pass", "while a and b:", "    pass", "x = 1", "assert x, y", "del a", "del (a, b)"),
            Src("def f():", "    return (x)", "if (a):", "    pass", "while (a and b):", "    pass", "x = (1)", "assert (x), (y)", "del (a)", "del (a, b)"));
        Same(Src("for x in y:", "    pass", "for a, b in c:", "    pass", "x = 1"), Src("for (x) in (y):", "    pass", "for (a, b) in c:", "    pass", "x = ((1))"));
    }

    [Fact]
    public void ParenthesesThatMatterStay()
    {
        string keep = Src("x = (1, 2)", "y = (i for i in z)", "def f():", "    return (yield)", "if (n := g()):", "    pass", "print((1))", "assert (x, \"message\")", "t = (a) + (b)");
        Assert.Equal(Fmt(keep), Fmt(Fmt(keep)));
        string result = Fmt(keep);
        Assert.Contains("x = (1, 2)", result);
        Assert.Contains("y = (i for i in z)", result);
        Assert.Contains("return (yield)", result);
        Assert.Contains("if (n := g()):", result);
        Assert.Contains("print((1))", result);
        Assert.Contains("assert (x, \"message\")", result);
        Assert.Contains("t = (a) + (b)", result);
    }

    [Fact]
    public void RemovedParenthesesComeBackWhenTheLineIsTooLong()
    {
        string value = "first_value_that_is_long + second_value_that_is_long + third_value_that_is_longer";
        Same(Src("total = (", "    " + value, ")"), Src("total = (" + value + ")"));
        Same(Src("total = " + "a + b"), Src("total = (a + b)"));
    }

    // ------------------------------------------------------------ comments inside brackets

    [Fact]
    public void CommentsAfterItemsStayWithThemInAnExplodedCollection()
    {
        string exploded = Src("foo(", "    a,  # first", "    b,", ")");
        Same(exploded, exploded);
        string dict = Src("d = {", "    \"a\": 1,  # one", "    \"b\": 2,  # two", "}");
        Same(dict, dict);
    }

    [Fact]
    public void CommentsAreCollectedAtTheEndOfAJoinedLine()
    {
        Same(Src("foo(a, b)  # first"), Src("foo(a,  # first", "    b)"));
        Same(Src("foo(a, b)  # why"), Src("foo(  # why", "    a, b)"));
        Same(Src("foo(a, b)  # last"), Src("foo(", "    a,", "    b  # last", ")"));
        Same(Src("x = a + b  # first  # second"), Src("x = (", "    a  # first", "    + b  # second", ")"));
    }

    [Fact]
    public void ACommentOnALineOfItsOwnKeepsTheBracketsExploded()
    {
        Same(Src("result = foo(", "    # leading", "    a,", "    b,", ")"), Src("result = foo(", "    # leading", "    a,", "    b", ")"));
        string between = Src("x = [", "    1,", "    # two", "    2,", "]");
        Same(between, between);
        string last = Src("foo(", "    a,", "    b,", "    # trailing note", ")");
        Same(last, last);
    }

    [Fact]
    public void ALongTrailingCommentCountsTowardsTheLineLength()
    {
        Same(Src("result = some_function_name(", "    a_long_argument_one, a_long_argument_two", ")  # explains why this is done"),
            Src("result = some_function_name(a_long_argument_one, a_long_argument_two)  # explains why this is done"));
        string plain = "value = 1  # " + new string('c', 100);
        Same(Src(plain), Src(plain));
    }

    [Fact]
    public void CommentsAreNeverLostOrReordered()
    {
        string messy = Src("def f(a,  # first", "      b,", "      # between", "      c):  # last", "    return [", "        a,  # one", "        # two", "        b, c", "    ]  # three");
        string result = Fmt(messy);
        var before = messy.Split('\n').SelectMany(l => l.Contains('#') ? new[] { l[l.IndexOf('#')..].Trim() } : Array.Empty<string>()).ToList();
        var after = result.Split('\n').SelectMany(l => l.Contains('#') ? new[] { l[l.IndexOf('#')..].Trim() } : Array.Empty<string>()).ToList();
        Assert.Equal(string.Join("|", before), string.Join("|", after));
        Assert.Equal(result, Fmt(result));
    }
}
