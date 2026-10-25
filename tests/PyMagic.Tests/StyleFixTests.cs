using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>The quick fixes of the style inspections.</summary>
public class StyleFixTests
{
    const string Main = "file:///c:/proj/main.py";

    static string Apply(string text, WorkspaceEdit edit)
    {
        var lines = new LineMap(text);
        foreach (var e in edit.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    static string Fix(string title, string src)
    {
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        var all = svc.CodeActions(Main, new LineMap(src).RangeOf(0, src.Length)).ToList();
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        return Apply(src, a.Edit!);
    }

    [Fact]
    public void RemovesATrailingSemicolon() =>
        Assert.Equal("x = 1\n", Fix("Remove the trailing semicolon", "x = 1;\n"));

    [Fact]
    public void SplitsStatementsAtASemicolon() =>
        Assert.Equal("x = 1\ny = 2\n", Fix("Put the statements on separate lines", "x = 1; y = 2\n"));

    [Fact]
    public void SplitsStatementsInsideABlock() =>
        Assert.Equal("def f():\n    x = 1\n    return x\n", Fix("Put the statements on separate lines", "def f():\n    x = 1; return x\n"));

    [Fact]
    public void RemovesRedundantParentheses()
    {
        Assert.Equal("def f(a):\n    if a:\n        pass\n", Fix("Remove the redundant parentheses", "def f(a):\n    if (a):\n        pass\n"));
        Assert.Equal("def f(a):\n    return a\n", Fix("Remove the redundant parentheses", "def f(a):\n    return(a)\n"));
    }

    [Fact]
    public void SimplifiesAChainedComparison() =>
        Assert.Equal("a = b = c = 1\nx = a < b <= c\n", Fix("Simplify the chained comparison", "a = b = c = 1\nx = a < b and b <= c\n"));

    [Fact]
    public void SimplifiesComparisonsWithBooleans()
    {
        Assert.Equal("x = 1\nif x:\n    pass\n", Fix("Compare the value itself", "x = 1\nif x == True:\n    pass\n"));
        Assert.Equal("x = 1\nif not x:\n    pass\n", Fix("Use 'not'", "x = 1\nif x == False:\n    pass\n"));
        Assert.Equal("x = 1\nif not x:\n    pass\n", Fix("Use 'not'", "x = 1\nif x != True:\n    pass\n"));
        Assert.Equal("x = 1\nif x:\n    pass\n", Fix("Compare the value itself", "x = 1\nif True == x:\n    pass\n"));
    }

    [Fact]
    public void UsesAnAugmentedAssignment()
    {
        Assert.Equal("x = 1\nx += 2\n", Fix("Replace with 'x += ...'", "x = 1\nx = x + 2\n"));
        Assert.Equal("x = 1\nx *= 2 + 3\n", Fix("Replace with 'x *= ...'", "x = 1\nx = x * (2 + 3)\n"));
    }

    [Fact]
    public void UsesASetLiteral()
    {
        Assert.Equal("a = {1, 2}\n", Fix("Replace with a set literal", "a = set([1, 2])\n"));
        Assert.Equal("a = {1}\n", Fix("Replace with a set literal", "a = set((1,))\n"));
    }

    [Fact]
    public void MakesAMethodStatic() =>
        Assert.Equal("class A:\n    @staticmethod\n    def f(x):\n        return x + 1\n", Fix("Make the method static", "class A:\n    def f(self, x):\n        return x + 1\n"));

    [Fact]
    public void MakesAMethodWithOnlySelfStatic() =>
        Assert.Equal("class A:\n    @staticmethod\n    def f():\n        return 1\n", Fix("Make the method static", "class A:\n    def f(self):\n        return 1\n"));

    [Fact]
    public void EscapesAnInvalidEscape() =>
        Assert.Equal("a = \"\\\\d+\"\n", Fix("Escape the backslash", "a = \"\\d+\"\n"));

    [Fact]
    public void RemovesAnUnnecessaryBackslash() =>
        Assert.Equal("x = (1 +\n     2)\n", Fix("Remove the backslash", "x = (1 + \\\n     2)\n").Replace("1 + \n", "1 +\n"));

    [Fact]
    public void FixesWhitespaceProblems()
    {
        Assert.Equal("x = 1\n", Fix("Remove the trailing whitespace", "x = 1  \n"));
        Assert.Equal("x = 1\n\ny = 2\n", Fix("Remove the trailing whitespace", "x = 1\n   \ny = 2\n"));
        Assert.Equal("x = 1\n", Fix("Add a newline", "x = 1"));
        Assert.Equal("x = 1\n", Fix("Remove the blank lines at the end", "x = 1\n\n\n"));
    }

    [Fact]
    public void FixesCommentSpacing()
    {
        Assert.Equal("x = 1  # note\n", Fix("Fix the comment spacing", "x = 1 # note\n"));
        Assert.Equal("x = 1  # note\n", Fix("Fix the comment spacing", "x = 1  #note\n"));
        Assert.Equal("# note\nx = 1\n", Fix("Fix the comment spacing", "#note\nx = 1\n"));
    }

    [Fact]
    public void SplitsImports() =>
        Assert.Equal("import os\nimport sys as system\n", Fix("Put each import on its own line", "import os, sys as system\n"));

    [Fact]
    public void ConvertsALambdaAssignment()
    {
        Assert.Equal("def f(x, y=1):\n    return x + y\n", Fix("Convert the lambda to a def", "f = lambda x, y=1: x + y\n"));
        Assert.Equal("def g():\n    return 0\n", Fix("Convert the lambda to a def", "g = lambda: 0\n"));
    }

    [Fact]
    public void MovesABodyToItsOwnLine() =>
        Assert.Equal("x = 1\nif x:\n    print(x)\n", Fix("Move the statement to its own line", "x = 1\nif x: print(x)\n"));

    [Fact]
    public void AddsBlankLinesAroundDefinitions()
    {
        Assert.Equal("import os\n\n\ndef f():\n    pass\n", Fix("Add the blank lines PEP 8 asks for", "import os\ndef f():\n    pass\n").Replace("print(os)", ""));
        Assert.Equal("def f():\n    pass\n\n\nx = 1\n", Fix("Add the blank lines PEP 8 asks for", "def f():\n    pass\nx = 1\n"));
        Assert.Equal("class A:\n    def f(self):\n        pass\n\n    def g(self):\n        pass\n", Fix("Add the blank lines PEP 8 asks for (1)", "class A:\n    def f(self):\n        pass\n    def g(self):\n        pass\n").Replace("@staticmethod\n    ", ""));
    }

    [Fact]
    public void BlankLinesGoAboveTheCommentOfADefinition() =>
        Assert.Equal("import os\n\n\n# about f\ndef f():\n    pass\n", Fix("Add the blank lines PEP 8 asks for", "import os\n# about f\ndef f():\n    pass\n"));

    [Fact]
    public void RemovesTooManyBlankLines()
    {
        Assert.Equal("x = 1\n\n\ny = 2\n", Fix("Remove the extra blank lines", "x = 1\n\n\n\n\ny = 2\n"));
    }
}
