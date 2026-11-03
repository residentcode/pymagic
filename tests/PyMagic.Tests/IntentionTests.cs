using PyMagic.Server;

namespace PyMagic.Tests;

/// <summary>Intention actions (item 236): rewrites offered at the cursor (`|` in the source) without a diagnostic.</summary>
public class IntentionTests
{
    const string Main = "file:///c:/proj/main.py";

    static string Apply(string text, WorkspaceEdit edit)
    {
        var lines = new LineMap(text);
        foreach (var e in edit.Changes[Main].OrderByDescending(e => lines.OffsetOf(e.Range.Start)).ThenByDescending(e => lines.OffsetOf(e.Range.End)))
            text = text[..lines.OffsetOf(e.Range.Start)] + e.NewText + text[lines.OffsetOf(e.Range.End)..];
        return text;
    }

    static List<CodeAction> Offered(string marked)
    {
        int at = marked.IndexOf('|');
        string src = marked.Remove(at, 1);
        var svc = new LanguageService();
        svc.Open(Main, src, 1);
        var lines = new LineMap(src);
        return svc.CodeActions(Main, lines.RangeOf(at, at)).Where(a => a.Kind == LanguageService.RewriteKind).ToList();
    }

    static string Do(string title, string marked)
    {
        var all = Offered(marked);
        var a = all.FirstOrDefault(x => x.Title.StartsWith(title));
        if (a == null) throw new AssertionException($"no action '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
        return Apply(marked.Replace("|", ""), a.Edit!);
    }

    static bool Has(string title, string marked) => Offered(marked).Any(x => x.Title.StartsWith(title));

    // ------------------------------------------------------------ f-strings

    [Fact]
    public void APercentFormatBecomesAnFString()
    {
        Assert.Equal("name = 'x'\nprint(f'Hello {name}!')\n", Do("Convert to an f-string", "name = 'x'\nprint('Hello %s!' %| name)\n"));
        Assert.Equal("a = 1\nb = 2\nprint(f'{a} and {b}')\n", Do("Convert to an f-string", "a = 1\nb = 2\nprint('%d and %s' %| (a, b))\n"));
        Assert.Equal("x = 3.14159\nprint(f'{x:.2f} {x!r}')\n", Do("Convert to an f-string", "x = 3.14159\nprint('%.2f %r' %| (x, x))\n"));
        Assert.Equal("n = 5\nprint(f'{n:>8}/{n:<8}/{n:05}')\n", Do("Convert to an f-string", "n = 5\nprint('%8s/%-8s/%05d' %| (n, n, n))\n"));
        Assert.Equal("a = 1\nprint(f'100% of {a}')\n", Do("Convert to an f-string", "a = 1\nprint('100%% of %s' %| a)\n"));
        Assert.Equal("a = 1\nprint(f'{{x}} {a}')\n", Do("Convert to an f-string", "a = 1\nprint('{x} %s' %| a)\n"));
        Assert.Equal("print(f'{1 + 2}')\n", Do("Convert to an f-string", "print('%s' %| (1 + 2))\n"));
        // a named format reads its values from a dict literal
        Assert.Equal("print(f'{1} {2}')\n", Do("Convert to an f-string", "print('%(a)s %(b)s' %| {'a': 1, 'b': 2})\n"));
        // too few arguments, or an expression with the string's own quote: not offered
        Assert.True(!Has("Convert to an f-string", "print('%s %s' %| (1,))\n"));
        Assert.True(!Has("Convert to an f-string", "d = {}\nprint('%s' %| d['k'])\n"));
    }

    [Fact]
    public void AFormatCallBecomesAnFString()
    {
        Assert.Equal("a = 1\nb = 2\nprint(f'{a} {b}')\n", Do("Convert to an f-string", "a = 1\nb = 2\nprint('{} {}'.format|(a, b))\n"));
        Assert.Equal("a = 1\nb = 2\nprint(f'{b} {a} {b}')\n", Do("Convert to an f-string", "a = 1\nb = 2\nprint('{1} {0} {1}'.format|(a, b))\n"));
        Assert.Equal("name = 'x'\nprint(f'{name!r:>10}')\n", Do("Convert to an f-string", "name = 'x'\nprint('{n!r:>10}'.format|(n=name))\n"));
        Assert.Equal("p = (1, 2)\nprint(f'{p.real} {{}}')\n".Replace("p.real", "p[0]"), Do("Convert to an f-string", "p = (1, 2)\nprint('{0[0]} {{}}'.format|(p))\n"));
        Assert.True(!Has("Convert to an f-string", "print('{} {}'.format|(1))\n"));
        Assert.True(!Has("Convert to an f-string", "print('{}'.format|(*args))\n"));
        Assert.True(!Has("Convert to an f-string", "print('{} {0}'.format|(1))\n"));
    }

    [Fact]
    public void AConcatenationBecomesAnFString()
    {
        Assert.Equal("name = 'x'\nprint(f'Hello {name}!')\n", Do("Convert to an f-string", "name = 'x'\nprint('Hello ' +| name + '!')\n"));
        Assert.Equal("n = 1\nprint(f'n is {n}')\n", Do("Convert to an f-string", "n = 1\nprint('n is ' +| str(n))\n"));
        Assert.Equal("a = 1\nb = 2\nprint(f'{a + b} total')\n", Do("Convert to an f-string", "a = 1\nb = 2\nprint(str(a + b) +| ' total')\n"));
        Assert.True(!Has("Convert to an f-string", "x = 1 +| 2\n"));
        Assert.True(!Has("Convert to an f-string", "x = a +| b\n"));
    }

    // ------------------------------------------------------------ comparisons and boolean logic

    [Fact]
    public void AComparisonIsFlippedOrNegated()
    {
        Assert.Equal("a = b = 1\nif b > a:\n    pass\n", Do("Flip the comparison", "a = b = 1\nif a <| b:\n    pass\n"));
        Assert.Equal("a = b = 1\nif b == a:\n    pass\n", Do("Flip the comparison", "a = b = 1\nif a ==| b:\n    pass\n"));
        Assert.Equal("a = b = 1\nif a >= b:\n    pass\n", Do("Negate the comparison", "a = b = 1\nif a <| b:\n    pass\n"));
        Assert.Equal("a = b = 1\nif a not in b:\n    pass\n", Do("Negate the comparison", "a = b = 1\nif a in| b:\n    pass\n"));
        Assert.Equal("a = b = 1\nif a is not b:\n    pass\n", Do("Negate the comparison", "a = b = 1\nif a is| b:\n    pass\n"));
        // `in` has no mirror
        Assert.True(!Has("Flip the comparison", "a = b = 1\nif a in| b:\n    pass\n"));
        // a chain of comparisons is left alone
        Assert.True(!Has("Flip the comparison", "a = b = c = 1\nif a <| b < c:\n    pass\n"));
    }

    [Fact]
    public void DeMorgansLawIsAppliedBothWays()
    {
        Assert.Equal("a = b = True\nx = not a or not b\n", Do("Apply De Morgan's law", "a = b = True\nx = not| (a and b)\n"));
        Assert.Equal("a = b = True\nx = not a and not b\n", Do("Apply De Morgan's law", "a = b = True\nx = not| (a or b)\n"));
        Assert.Equal("a = b = 1\nx = a >= b or a != b\n".Replace("a >= b or a != b", "a >= b or a != b"), Do("Apply De Morgan's law", "a = b = 1\nx = not| (a < b and a == b)\n"));
        Assert.Equal("a = b = True\nx = not (a or b)\n", Do("Apply De Morgan's law", "a = b = True\nx = not a and| not b\n"));
        Assert.Equal("a = b = True\nx = not (a and b)\n", Do("Apply De Morgan's law", "a = b = True\nx = not a or| not b\n"));
        Assert.True(!Has("Apply De Morgan's law", "a = b = True\nx = a and| b\n"));
    }

    // ------------------------------------------------------------ if statements

    [Fact]
    public void AnIfIsInvertedByTheSwapOfItsBranches()
    {
        Assert.Equal("def f(a):\n    if not a:\n        return 2\n    else:\n        return 1\n", Do("Invert the if", "def f(a):\n    i|f a:\n        return 1\n    else:\n        return 2\n"));
        Assert.Equal("def f(a, b):\n    if a >= b:\n        x = 2\n        y = 3\n    else:\n        x = 1\n", Do("Invert the if", "def f(a, b):\n    if a <| b:\n        x = 1\n    else:\n        x = 2\n        y = 3\n"));
        // an elif chain, or no else: not offered; nor from inside the body
        Assert.True(!Has("Invert the if", "def f(a):\n    i|f a:\n        return 1\n"));
        Assert.True(!Has("Invert the if", "def f(a):\n    i|f a:\n        return 1\n    elif a > 1:\n        return 2\n"));
        Assert.True(!Has("Invert the if", "def f(a):\n    if a:\n        ret|urn 1\n    else:\n        return 2\n"));
    }

    [Fact]
    public void NestedIfsAreJoinedAndAnAndIsSplit()
    {
        Assert.Equal("def f(a, b):\n    if a and b:\n        print(1)\n        print(2)\n", Do("Merge the nested if", "def f(a, b):\n    i|f a:\n        if b:\n            print(1)\n            print(2)\n"));
        Assert.Equal("def f(a, b):\n    if (a or b) and c:\n        pass\n".Replace(" and c", " and b"), Do("Merge the nested if", "def f(a, b):\n    i|f a or b:\n        if b:\n            pass\n"));
        Assert.True(!Has("Merge the nested if", "def f(a, b):\n    i|f a:\n        if b:\n            pass\n    else:\n        pass\n"));
        Assert.Equal("def f(a, b):\n    if a:\n        if b:\n            print(1)\n            print(2)\n", Do("Split the condition", "def f(a, b):\n    i|f a and b:\n        print(1)\n        print(2)\n"));
        Assert.True(!Has("Split the condition", "def f(a, b):\n    i|f a and b:\n        pass\n    else:\n        pass\n"));
    }

    [Fact]
    public void AnIfExpressionAndAnIfStatementConvertIntoEachOther()
    {
        Assert.Equal("def f(c):\n    if c:\n        x = 1\n    else:\n        x = 2\n    return x\n", Do("Convert the conditional expression", "def f(c):\n    x =| 1 if c else 2\n    return x\n"));
        Assert.Equal("def f(c):\n    if c:\n        return 1\n    else:\n        return 2\n", Do("Convert the conditional expression", "def f(c):\n    ret|urn 1 if c else 2\n"));
        Assert.Equal("def f(c):\n    x = 1 if c else 2\n    return x\n", Do("Convert the if statement", "def f(c):\n    i|f c:\n        x = 1\n    else:\n        x = 2\n    return x\n"));
        Assert.Equal("def f(c):\n    return 1 if c else 2\n", Do("Convert the if statement", "def f(c):\n    i|f c:\n        return 1\n    else:\n        return 2\n"));
        // different targets: not offered
        Assert.True(!Has("Convert the if statement", "def f(c):\n    i|f c:\n        x = 1\n    else:\n        y = 2\n"));
    }

    // ------------------------------------------------------------ dict, comprehension

    [Fact]
    public void ADictLiteralAndADictCallConvertIntoEachOther()
    {
        Assert.Equal("x = dict(a=1, b=[2])\n", Do("Convert the dict literal", "x = {|'a': 1, 'b': [2]}\n"));
        Assert.Equal("x = {'a': 1, 'b': 2}\n", Do("Convert the dict() call", "x = dict|(a=1, b=2)\n"));
        // keys that are not names, a ** entry or a positional argument: not offered
        Assert.True(!Has("Convert the dict literal", "x = {|'a b': 1}\n"));
        Assert.True(!Has("Convert the dict literal", "x = {|1: 1}\n"));
        Assert.True(!Has("Convert the dict literal", "x = {|'class': 1}\n"));
        Assert.True(!Has("Convert the dict literal", "y = {}\nx = {|**y}\n"));
        Assert.True(!Has("Convert the dict() call", "x = dict|(a, b=2)\n"));
    }

    [Fact]
    public void AComprehensionBecomesAForLoop()
    {
        Assert.Equal("xs = [1, 2]\nr = []\nfor x in xs:\n    r.append(x * 2)\n", Do("Convert the comprehension", "xs = [1, 2]\nr =| [x * 2 for x in xs]\n"));
        Assert.Equal("xs = [1, 2]\nr = []\nfor x in xs:\n    if x > 1:\n        r.append(x)\n", Do("Convert the comprehension", "xs = [1, 2]\nr =| [x for x in xs if x > 1]\n"));
        Assert.Equal("xs = [1]\ns = set()\nfor x in xs:\n    for y in xs:\n        s.add((x, y))\n", Do("Convert the comprehension", "xs = [1]\ns =| {(x, y) for x in xs for y in xs}\n"));
        Assert.Equal("xs = [1]\nd = {}\nfor x in xs:\n    d[x] = str(x)\n", Do("Convert the comprehension", "xs = [1]\nd =| {x: str(x) for x in xs}\n"));
        Assert.Equal("def f(xs):\n    r = []\n    for x in xs:\n        r.append(x)\n    return r\n", Do("Convert the comprehension", "def f(xs):\n    r =| [x for x in xs]\n    return r\n"));
        // a generator expression is not a collection
        Assert.True(!Has("Convert the comprehension", "xs = [1]\nr =| (x for x in xs)\n"));
    }

    // ------------------------------------------------------------ imports

    /// <summary>A project folder on disk with its sources registered, so module names follow the folders.</summary>
    sealed class Project : IDisposable
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "pm_intent_" + Guid.NewGuid().ToString("N"));
        public readonly LanguageService Service = new();
        public Project() { Directory.CreateDirectory(Root); Service.SourceRoots.Add(Root); }

        public void File_(string rel, string text)
        {
            string p = Path.Combine(Root, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(p)!);
            File.WriteAllText(p, text);
        }

        /// <summary>Opens a file with this text and applies the first rewrite titled so at the cursor (`|`).</summary>
        public string Do(string file, string marked, string title)
        {
            int at = marked.IndexOf('|');
            string src = marked.Remove(at, 1);
            string uri = LanguageService.UriOfPath(Path.Combine(Root, file));
            Service.Open(uri, src, 1);
            var lines = new LineMap(src);
            var all = Service.CodeActions(uri, lines.RangeOf(at, at)).Where(a => a.Kind == LanguageService.RewriteKind).ToList();
            var action = all.FirstOrDefault(x => x.Title.StartsWith(title)) ?? throw new AssertionException($"no '{title}'; offered: {string.Join(" | ", all.Select(x => x.Title))}");
            string result = src;
            foreach (var e in action.Edit!.Changes[uri].OrderByDescending(e => lines.OffsetOf(e.Range.Start)))
                result = result[..lines.OffsetOf(e.Range.Start)] + e.NewText + result[lines.OffsetOf(e.Range.End)..];
            return result;
        }

        public bool Has(string file, string marked, string title)
        {
            int at = marked.IndexOf('|');
            string src = marked.Remove(at, 1);
            string uri = LanguageService.UriOfPath(Path.Combine(Root, file));
            Service.Open(uri, src, 1);
            return Service.CodeActions(uri, new LineMap(src).RangeOf(at, at)).Any(a => a.Kind == LanguageService.RewriteKind && a.Title.StartsWith(title));
        }

        public void Dispose() { try { Directory.Delete(Root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    }

    [Fact]
    public void AnImportAndAFromImportConvertIntoEachOther()
    {
        using var p = new Project();
        p.File_("pkg/__init__.py", "");
        p.File_("pkg/mod.py", "def f():\n    pass\n\n\nx = 1\n");
        Assert.Equal("from pkg import mod\nprint(mod.f())\n", p.Do("main.py", "import pk|g.mod\nprint(pkg.mod.f())\n", "Convert to 'from pkg import mod'"));
        Assert.Equal("import pkg.mod\nprint(pkg.mod.f())\n", p.Do("main.py", "from pkg| import mod\nprint(mod.f())\n", "Convert to 'import pkg.mod'"));
        // a name that is not a module cannot be imported that way
        Assert.True(!p.Has("main.py", "from pkg.mod| import x\n", "Convert to 'import pkg.mod.x'"));
        // another use of the new name stops it
        Assert.True(!p.Has("main.py", "mod = 1\nimport pk|g.mod\n", "Convert to 'from pkg import mod'"));
    }

    [Fact]
    public void APlainImportBecomesAFromImportOfTheNamesItIsUsedFor()
    {
        Assert.Equal("from os import path, getcwd\nprint(path.join(getcwd(), 'x'), path.sep)\n",
            Do("Convert to 'from os import path, getcwd'", "import o|s\nprint(os.path.join(os.getcwd(), 'x'), os.path.sep)\n"));
        // an alias: its uses are the alias's
        Assert.Equal("from numpy import array, zeros\na = array([1])\nb = zeros(2)\n",
            Do("Convert to 'from numpy import array, zeros'", "import numpy a|s np\na = np.array([1])\nb = np.zeros(2)\n"));
        // the module used alone, an attribute assigned or deleted, or a name that is bound already: not offered
        Assert.True(!Has("Convert to 'from os import", "import o|s\nprint(os)\nos.getcwd()\n"));
        Assert.True(!Has("Convert to 'from os import", "import o|s\nos.sep = '/'\n"));
        Assert.True(!Has("Convert to 'from os import", "import o|s\ndel os.environ\n"));
        Assert.True(!Has("Convert to 'from os import", "import o|s\ndef f(path):\n    return os.path.join(path)\n"));
        Assert.True(!Has("Convert to 'from os import", "import o|s\ngetcwd = 1\nos.getcwd()\n"));
        Assert.True(!Has("Convert to 'from os import", "import o|s\n"));
    }

    [Fact]
    public void AnFStringIsMadeFromAStringWithEscapes()
    {
        Assert.Equal("n = 1\nprint(f'a\\tb {n}\\n')\n", Do("Convert to an f-string", "n = 1\nprint('a\\tb %s\\n' %| n)\n"));
        Assert.Equal("n = 1\nprint(f'it\\'s {n}')\n", Do("Convert to an f-string", "n = 1\nprint('it\\'s {}'.format|(n))\n"));
        Assert.Equal("n = 1\nprint(f'\\\\{n}')\n", Do("Convert to an f-string", "n = 1\nprint('\\\\' +| str(n))\n"));
        // \N{...} keeps its braces, which the f-string would take for a field: not offered
        Assert.True(!Has("Convert to an f-string", "n = 1\nprint('\\N{BULLET} %s' %| n)\n"));
    }

    [Fact]
    public void APlainImportOfAModuleOfTheSamePackageBecomesRelative()
    {
        using var p = new Project();
        p.File_("pkg/__init__.py", "");
        p.File_("pkg/util.py", "def helper():\n    pass\n");
        p.File_("pkg/sub/__init__.py", "");
        p.File_("pkg/sub/util.py", "def helper():\n    pass\n");
        Assert.Equal("from . import util\nutil.helper()\n", p.Do("pkg/sub/mod.py", "import pkg.sub.ut|il\npkg.sub.util.helper()\n", "Convert to a relative import"));
        Assert.Equal("from .. import util\nutil.helper()\n", p.Do("pkg/sub/mod.py", "import pkg.ut|il\npkg.util.helper()\n", "Convert to a relative import"));
        // the package used for something else, another package, or a top-level file: not offered
        Assert.True(!p.Has("pkg/sub/mod.py", "import pkg.ut|il\npkg.util.helper()\nprint(pkg)\n", "Convert to a relative import"));
        Assert.True(!p.Has("pkg/sub/mod.py", "import os.pa|th\n", "Convert to a relative import"));
        Assert.True(!p.Has("main.py", "import pkg.ut|il\n", "Convert to a relative import"));
    }

    [Fact]
    public void ARelativeImportAndAnAbsoluteOneConvertIntoEachOther()
    {
        using var p = new Project();
        p.File_("pkg/__init__.py", "");
        p.File_("pkg/util.py", "def helper():\n    pass\n");
        p.File_("pkg/sub/__init__.py", "");
        p.File_("pkg/sub/util.py", "def helper():\n    pass\n");
        Assert.Equal("from pkg.sub.util import helper\n", p.Do("pkg/sub/mod.py", "from .ut|il import helper\n", "Convert to an absolute import"));
        Assert.Equal("from pkg.util import helper\n", p.Do("pkg/sub/mod.py", "from ..ut|il import helper\n", "Convert to an absolute import"));
        Assert.Equal("from pkg.sub import helper\n", p.Do("pkg/sub/mod.py", "from .| import helper\n", "Convert to an absolute import"));
        Assert.Equal("from .util import helper\n", p.Do("pkg/sub/mod.py", "from pkg.sub.ut|il import helper\n", "Convert to a relative import"));
        Assert.Equal("from ..util import helper\n", p.Do("pkg/sub/mod.py", "from pkg.ut|il import helper\n", "Convert to a relative import"));
        Assert.True(!p.Has("pkg/sub/mod.py", "from os.pa|th import join\n", "Convert to a relative import"));
        Assert.True(!p.Has("main.py", "from .ut|il import helper\n", "Convert to an absolute import"));
    }

    // ------------------------------------------------------------ type hints, docstrings, numbers

    [Fact]
    public void ATypeHintIsAddedFromTheInferredType()
    {
        Assert.Equal("x: int = 5\n", Do("Add the type hint ': int'", "x| = 5\n"));
        Assert.Equal("def f(a: int = 1):\n    return a\n", Do("Add the type hint ': int'", "def f(a|=1):\n    return a\n"));
        Assert.Equal("def f() -> int:\n    return 1\n", Do("Add the return type '-> int'", "def |f():\n    return 1\n"));
        Assert.Equal("def f() -> str:\n    return 'a'\n", Do("Add the return type '-> str'", "def f|():\n    return 'a'\n"));
        // already annotated, or nothing known
        Assert.True(!Has("Add the return type", "def |f() -> int:\n    return 1\n"));
        Assert.True(!Has("Add the type hint", "def f(a|, b=None):\n    return a\n"));
    }

    [Fact]
    public void ADocstringStubIsInsertedInTheProjectsStyle()
    {
        Assert.Equal("def f(a, b):\n    \"\"\"TODO: describe f.\n\n    Args:\n        a: \n        b: \n\n    Returns:\n        \n    \"\"\"\n    return a + b\n",
            Do("Insert a docstring stub", "def |f(a, b):\n    return a + b\n").Replace("\r", ""));
        Assert.Equal("class A:\n    def m(self, x):\n        \"\"\"TODO: describe m.\n\n        Args:\n            x: \n        \"\"\"\n        pass\n", Do("Insert a docstring stub", "class A:\n    def |m(self, x):\n        pass\n"));
        Assert.Equal("def f():\n    \"\"\"TODO: describe f.\"\"\"\n    pass\n", Do("Insert a docstring stub", "def |f():\n    pass\n"));
        Assert.True(!Has("Insert a docstring stub", "def |f():\n    \"\"\"Has one.\"\"\"\n    pass\n"));
        // inside the body: no
        Assert.True(!Has("Insert a docstring stub", "def f():\n    pa|ss\n"));
    }

    [Fact]
    public void TheStubFollowsTheStyleTheProjectUses()
    {
        var svc = new LanguageService();
        svc.Open("file:///c:/proj/other.py", "def g(a):\n    \"\"\"Does g.\n\n    :param a: the thing\n    :return: nothing\n    \"\"\"\n    return None\n", 1);
        string src = "def f(a):\n    return a\n";
        svc.Open(Main, src, 1);
        var lines = new LineMap(src);
        var a = svc.CodeActions(Main, lines.RangeOf(4, 4)).First(x => x.Title.StartsWith("Insert a docstring stub"));
        Assert.Contains("reST", a.Title);
        Assert.Contains(":param a: ", Apply(src, a.Edit!));
        Assert.Contains(":return: ", Apply(src, a.Edit!));
    }

    [Fact]
    public void ANumberIsWrittenInAnotherBase()
    {
        Assert.Equal("x = 0xFF\n", Do("Convert to hexadecimal", "x = 25|5\n"));
        Assert.Equal("x = 0o377\n", Do("Convert to octal", "x = 25|5\n"));
        Assert.Equal("x = 0b11111111\n", Do("Convert to binary", "x = 25|5\n"));
        Assert.Equal("x = 255\n", Do("Convert 0xff to decimal", "x = 0x|ff\n"));
        Assert.Equal("x = 8\n", Do("Convert 0b1000 to decimal", "x = 0b|1000\n"));
        Assert.Equal("x = 0x0\n".Replace("0x0", "0x0"), Do("Convert to hexadecimal", "x = |0\n"));
        Assert.True(!Has("Convert to hexadecimal", "x = 0x|ff\n"));
        Assert.True(!Has("Convert to hexadecimal", "x = 1|.5\n"));
    }
}
