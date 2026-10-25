using PyMagic.Core.Types;

namespace PyMagic.Tests;

/// <summary>The inspections of StyleInspections: each is found where it should be and left alone where it should not.</summary>
public class StyleInspectionTests
{
    static List<Finding> Run(string src)
    {
        var ws = new Workspace();
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        var found = new List<Finding>();
        StyleInspections.Run(m, found);
        return found;
    }

    static int Count(string src, string code) => Run(src).Count(f => f.Code == code);

    // ------------------------------------------------------------ statements without effect

    [Fact]
    public void AStatementWithoutEffect()
    {
        Assert.Equal(4, Count("x = 1\nx\n1 + 2\n-x\nx == 3\n", "statement-effect"));
        Assert.Equal(0, Count("def f(): ...\nf()\n'doc'\n...\nawait_x = [print(1)]\nx = 1\nx and f()\n", "statement-effect"));
    }

    [Fact]
    public void SemicolonsAtTheEnd()
    {
        Assert.Equal(1, Count("x = 1;\n", "trailing-semicolon"));
        Assert.Equal(0, Count("x = 1; y = 2\n", "trailing-semicolon"));
        Assert.Equal(1, Count("x = 1; y = 2\n", "pep8-statements"));
    }

    [Fact]
    public void RedundantParentheses()
    {
        Assert.Equal(3, Count("def f(a):\n    if (a):\n        pass\n    while (a > 1):\n        pass\n    return (a)\n", "redundant-parentheses"));
        Assert.Equal(0, Count("def f(a, b):\n    if (a) and (b):\n        pass\n    if (a, b):\n        pass\n    return (a, b)\n    return ()\n    return (yield)\n", "redundant-parentheses"));
    }

    [Fact]
    public void ChainedComparisons()
    {
        Assert.Equal(1, Count("a = b = c = 1\nif a < b and b < c:\n    pass\n", "chained-comparison"));
        Assert.Equal(1, Count("a = b = c = 1\nx = a >= b and b > c\n", "chained-comparison"));
        Assert.Equal(0, Count("a = b = c = 1\nx = a < b and c < b\ny = a < b and b > c\n", "chained-comparison"));
    }

    [Fact]
    public void ComparisonWithTrueOrFalse()
    {
        Assert.Equal(2, Count("x = 1\nif x == True:\n    pass\nif x != False:\n    pass\n", "simplify-boolean"));
        Assert.Equal(0, Count("x = 1\nif x is True:\n    pass\nif True == False:\n    pass\n", "simplify-boolean"));
    }

    [Fact]
    public void ListAndDictCreation()
    {
        Assert.Equal(1, Count("items = []\nitems.append(1)\n", "list-creation"));
        Assert.Equal(0, Count("items = []\nitems.append(len(items))\nother = []\nprint(1)\nother.append(1)\n", "list-creation"));
        Assert.Equal(1, Count("d = {}\nd['a'] = 1\n", "dict-creation"));
        Assert.Equal(0, Count("d = {}\nd['a'] = d\ne = {}\nprint(1)\ne['x'] = 1\n", "dict-creation"));
    }

    [Fact]
    public void SetOfAList()
    {
        Assert.Equal(2, Count("a = set([1, 2])\nb = set((1, 2))\n", "set-literal"));
        Assert.Equal(0, Count("a = set()\nb = set([])\nc = set(range(3))\n", "set-literal"));
    }

    [Fact]
    public void AugmentableAssignment()
    {
        Assert.Equal(2, Count("x = 1\nx = x + 1\nx = x * 2\n", "augment-assignment"));
        Assert.Equal(0, Count("x = 1\ny = 2\nx = y + x\nx = x == 1\n", "augment-assignment"));
    }

    [Fact]
    public void UnhashableKeysAndItems()
    {
        Assert.Equal(3, Count("a = {[1]: 2, {}: 3}\nb = {[1], 2}\n", "unhashable"));
        Assert.Equal(0, Count("a = {(1, 2): 3, 'k': [1]}\n", "unhashable"));
    }

    [Fact]
    public void GlobalOfAnUndefinedName()
    {
        Assert.Equal(1, Count("def f():\n    global missing\n    print(missing)\n", "global-undefined"));
        Assert.Equal(0, Count("known = 1\n\n\ndef f():\n    global known\n    known = 2\n", "global-undefined"));
    }

    [Fact]
    public void WrongFirstArgumentOfSuper()
    {
        Assert.Equal(1, Count("class A:\n    pass\n\n\nclass B(A):\n    def f(self):\n        super(A, self).f()\n", "super-arguments"));
        Assert.Equal(0, Count("class A:\n    pass\n\n\nclass B(A):\n    def f(self):\n        super(B, self).f()\n        super().f()\n", "super-arguments"));
    }

    // ------------------------------------------------------------ strings

    [Fact]
    public void InvalidEscapeSequences()
    {
        Assert.Equal(2, Count("a = \"\\d+\"\nb = '\\.' + \"ok\\n\"\n", "invalid-escape"));
        Assert.Equal(0, Count("a = r\"\\d+\"\nb = \"\\\\d\\x41\\N{BULLET}\\u0041\\101\"\n", "invalid-escape"));
        Assert.Equal(1, Count("a = b\"\\u0041\"\n", "invalid-escape"));
    }

    [Fact]
    public void BackslashInsideBrackets()
    {
        Assert.Equal(1, Count("x = (1 + \\\n     2)\n", "unnecessary-backslash"));
        Assert.Equal(0, Count("x = 1 + \\\n    2\n", "unnecessary-backslash"));
    }

    [Fact]
    public void PercentFormats()
    {
        Assert.Equal(1, Count("a = 1\nx = \"%s %s\" % (a,)\n", "string-format"));
        Assert.Equal(1, Count("a = 1\nx = \"%s\" % (a, a)\n", "string-format"));
        Assert.Equal(0, Count("a = 1\nx = \"%s %s\" % (a, a)\ny = \"%d%%\" % a\nz = \"%(k)s\" % {\"k\": 1}\nw = \"%s\" % (a,)\n", "string-format"));
    }

    [Fact]
    public void StrFormat()
    {
        Assert.Equal(1, Count("x = \"{} {}\".format(1)\n", "string-format"));
        Assert.Equal(1, Count("x = \"{}\".format(1, 2)\n", "string-format"));
        Assert.Equal(1, Count("x = \"{name}\".format(other=1)\n", "string-format"));
        Assert.Equal(0, Count("x = \"{} {0.real} {{}}\".format(1)\ny = \"{a}{b:>{w}}\".format(a=1, b=2, w=3)\nz = \"{}\".format(*[1])\n", "string-format"));
    }

    // ------------------------------------------------------------ classes and functions

    [Fact]
    public void AMethodThatNeverUsesSelf()
    {
        Assert.Equal(1, Count("class A:\n    def f(self, x):\n        return x + 1\n", "method-may-be-static"));
        Assert.Equal(0, Count("class A:\n    def f(self):\n        return self.x\n    def g(self):\n        pass\n    @staticmethod\n    def h(x):\n        return x\n    def __repr__(self):\n        return 'A'\n", "method-may-be-static"));
        Assert.Equal(0, Count("class B(Base):\n    def f(self, x):\n        return x\n", "method-may-be-static"));
    }

    [Fact]
    public void AttributeFirstSetOutsideInit()
    {
        Assert.Equal(1, Count("class A:\n    def __init__(self):\n        self.a = 1\n\n    def run(self):\n        self.b = 2\n        self.b = 3\n        self.a = 4\n", "attribute-outside-init"));
        Assert.Equal(0, Count("class A:\n    x = 0\n\n    def run(self):\n        self.x = 2\n", "attribute-outside-init"));
        Assert.Equal(0, Count("class A(Base):\n    def run(self):\n        self.b = 2\n", "attribute-outside-init"));
    }

    [Fact]
    public void InitReturningAValue()
    {
        Assert.Equal(1, Count("class A:\n    def __init__(self):\n        return 1\n", "return-from-init"));
        Assert.Equal(0, Count("class A:\n    def __init__(self):\n        return\n    def f(self):\n        return 1\n", "return-from-init"));
    }

    [Fact]
    public void AssignmentToSelf()
    {
        Assert.Equal(1, Count("class A:\n    def f(self):\n        self = None\n", "first-arg-assignment"));
    }

    [Fact]
    public void AssignmentToTheLoopVariable()
    {
        Assert.Equal(1, Count("for i in range(3):\n    i = i + 1\n", "loop-variable-assignment"));
        Assert.Equal(1, Count("with open('f') as fh:\n    fh = None\n", "loop-variable-assignment"));
        Assert.Equal(0, Count("for i in range(3):\n    j = i\n", "loop-variable-assignment"));
    }

    [Fact]
    public void InconsistentReturns()
    {
        Assert.Equal(1, Count("def f(a):\n    if a:\n        return 1\n    return\n", "inconsistent-returns"));
        Assert.Equal(1, Count("def f(a):\n    if a:\n        return 1\n    print(a)\n", "inconsistent-returns"));
        Assert.Equal(0, Count("def f(a):\n    if a:\n        return 1\n    else:\n        raise ValueError\n", "inconsistent-returns"));
        Assert.Equal(0, Count("def f(a):\n    while True:\n        if a:\n            return 1\n", "inconsistent-returns"));
        Assert.Equal(0, Count("def f(a):\n    print(a)\n    return\n", "inconsistent-returns"));
        Assert.Equal(0, Count("def f(a):\n    if a:\n        return 1\n    yield 2\n", "inconsistent-returns"));
    }

    // ------------------------------------------------------------ PEP 8

    [Fact]
    public void LinesAreTooLong()
    {
        string longLine = "x = '" + new string('a', 60) + " " + new string('b', 70) + "'\n";
        Assert.Equal(1, Count(longLine, "pep8-line-length"));
        Assert.Equal(0, Count("# " + new string('a', 150) + "\n", "pep8-line-length"));
        Assert.Equal(0, Count("x = 1\n", "pep8-line-length"));
    }

    [Fact]
    public void WhitespaceAtTheEnds()
    {
        Assert.Equal(1, Count("x = 1  \n", "pep8-whitespace"));
        Assert.Equal(1, Count("x = 1\n    \ny = 2\n", "pep8-whitespace"));
        Assert.Equal(1, Count("x = 1", "pep8-whitespace"));
        Assert.Equal(1, Count("x = 1\n\n\n", "pep8-whitespace"));
        Assert.Equal(0, Count("x = '''a  \nb'''\n", "pep8-whitespace"));
    }

    [Fact]
    public void BlankLinesAroundDefinitions()
    {
        Assert.Equal(1, Count("import os\ndef f():\n    pass\n", "pep8-blank-lines"));
        Assert.Equal(1, Count("def f():\n    pass\nx = 1\n", "pep8-blank-lines"));
        Assert.Equal(1, Count("class A:\n    def f(self):\n        pass\n    def g(self):\n        pass\n", "pep8-blank-lines"));
        Assert.Equal(1, Count("x = 1\n\n\n\ny = 2\n", "pep8-blank-lines"));
        Assert.Equal(0, Count("import os\n\n\ndef f():\n    pass\n\n\nx = 1\n\n\n@dec\ndef g():\n    pass\n", "pep8-blank-lines"));
        Assert.Equal(0, Count("class A:\n    \"\"\"Doc.\"\"\"\n    def f(self):\n        pass\n\n    # about g\n    def g(self):\n        pass\n", "pep8-blank-lines"));
    }

    [Fact]
    public void CommentSpacing()
    {
        Assert.Equal(1, Count("x = 1 # note\n", "pep8-comments"));
        Assert.Equal(1, Count("x = 1  #note\n", "pep8-comments"));
        Assert.Equal(1, Count("#note\nx = 1\n", "pep8-comments"));
        Assert.Equal(0, Count("#!/usr/bin/env python\n# fine\n#: marker\nx = 1  # fine\n", "pep8-comments"));
    }

    [Fact]
    public void StatementsOnOneLine()
    {
        Assert.Equal(1, Count("import os, sys\n", "pep8-statements"));
        Assert.Equal(1, Count("f = lambda x: x\n", "pep8-statements"));
        Assert.Equal(1, Count("x = 1\nif x: print(x)\n", "pep8-statements"));
        Assert.Equal(0, Count("def f(): ...\nclass A: ...\n", "pep8-statements"));
    }

    [Fact]
    public void AmbiguousNames()
    {
        Assert.Equal(3, Count("l = 1\nfor O in range(2):\n    pass\n\n\ndef f(I):\n    pass\n", "pep8-ambiguous-name"));
        Assert.Equal(0, Count("line = 1\n", "pep8-ambiguous-name"));
    }
}
