using System.Diagnostics;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

public class FormatterTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";
    static string Fmt(string text) => PythonFormatter.Format(text) ?? throw new AssertionException("the formatter declined:\n" + text);

    [Fact]
    public void SpacesAroundOperatorsCommasAndBrackets()
    {
        Assert.Equal(Src("x = 1", "print(x, y)", "z = [1, 2, 3]", "t = (1,)", "e = ()", "d = {1: 2, 3: 4}"),
            Fmt(Src("x=1", "print (x,y )", "z = [ 1,2 , 3 ]", "t = (1 ,)", "e = ( )", "d = { 1:2,3 :4 }")));
    }

    [Fact]
    public void UnaryKeywordArgumentsAndPowers()
    {
        Assert.Equal(Src("a = -1", "b = x**2", "c = f(a=1, *args, **kw)", "d = x if y else -1", "g = lambda x=1: x", "h = a[-1]", "i = y**-x", "j = (a + b) ** 2", "k = f(x) ** 2", "m = a.b**c.d"),
            Fmt(Src("a=- 1", "b = x ** 2", "c=f(a = 1,* args,** kw)", "d = x if y else-1", "g = lambda x = 1 : x", "h = a[ -1 ]", "i = y ** - x", "j = (a+b)**2", "k = f(x) ** 2", "m = a.b ** c.d")));
    }

    [Fact]
    public void ParametersAndAnnotations()
    {
        Assert.Equal(Src("def f(a: int = 1, b=2, *args, c: str, **kw) -> int:", "    return a", "", "", "async def g(x: int = None):", "    await h(x)"),
            Fmt(Src("def f(a:int=1,b = 2,*args,c:str,**kw)->int:", "    return a", "async def g(x:int=None):", "    await h(x)")));
    }

    [Fact]
    public void BlankLinesAroundDefinitions()
    {
        string input = Src("import os", "def f():", "  pass", "class A:", "  def m(self):", "    pass", "  def n(self):", "    pass", "x=1");
        Assert.Equal(Src("import os", "", "", "def f():", "    pass", "", "", "class A:", "    def m(self):", "        pass", "", "    def n(self):", "        pass", "", "", "x = 1"),
            Fmt(input));
    }

    [Fact]
    public void DecoratorsAndAttachedCommentsStayTogether()
    {
        string input = Src("x = 1", "# about f", "@decorator", "def f():", "    pass", "", "", "", "", "y = 2");
        Assert.Equal(Src("x = 1", "", "", "# about f", "@decorator", "def f():", "    pass", "", "", "y = 2"), Fmt(input));
    }

    [Fact]
    public void BlankLinesAreCappedAndRemovedAtTheStartOfBlocks()
    {
        Assert.Equal(Src("x = 1", "", "", "y = 2", "", "", "def f():", "    a = 1", "", "    b = 2"),
            Fmt(Src("", "", "x = 1", "", "", "", "", "y = 2", "", "def f():", "", "", "    a = 1", "", "", "    b = 2")));
    }

    [Fact]
    public void BlankLineAfterImportsAndModuleDocstring()
    {
        Assert.Equal(Src("\"\"\"Doc.\"\"\"", "", "import os", "", "x = os.sep"), Fmt(Src("\"\"\"Doc.\"\"\"", "import os", "x = os.sep")));
    }

    [Fact]
    public void CommentsGetTheirSpaces()
    {
        Assert.Equal(Src("x = 1  # note", "# comment", "#!shebang-like stays", "#: sphinx", "### banner", "y = 2"),
            Fmt(Src("x = 1 # note", "#comment", "#!shebang-like stays", "#: sphinx", "### banner", "y = 2")));
    }

    [Fact]
    public void CommentsBeforeADedentFollowTheirColumn()
    {
        string input = Src("def f():", "    x = 1", "    # still in f", "# top", "y = 2");
        Assert.Equal(Src("def f():", "    x = 1", "    # still in f", "", "", "# top", "y = 2"), Fmt(input));
    }

    [Fact]
    public void QuotesBecomeDoubleWhenThatNeedsNoEscapes()
    {
        Assert.Equal(Src("a = \"a\"", "b = 'it\"s'", "c = f\"{x}\"", "d = b\"x\"", "e = \"\"\"doc\"\"\"", "f = 'it\\'s'", "g = r'\\d'"),
            Fmt(Src("a = 'a'", "b = 'it\"s'", "c = f'{x}'", "d = b'x'", "e = '''doc'''", "f = 'it\\'s'", "g = r'\\d'")));
    }

    [Fact]
    public void SlicesKeepTheirChosenSpacing()
    {
        string input = Src("a[1:2]", "a[x + 1 : y]", "a[::2]", "a[:, 0]", "b = {k: v for k, v in z}");
        Assert.Equal(input, Fmt(input));
        Assert.Equal(Src("a[1:2]"), Fmt(Src("a[1 :2]")));
    }

    [Fact]
    public void TabsAndOddIndentationBecomeFourSpaces()
    {
        Assert.Equal(Src("if x:", "    if y:", "        z = 1", "    else:", "        z = 2"), Fmt("if x:\n\tif y:\n\t\tz=1\n\telse:\n\t\tz=2\n"));
        Assert.Equal(Src("def f():", "    return 1"), Fmt(Src("def f():", "  return 1")));
    }

    [Fact]
    public void ContinuationLinesMoveWithTheirStatement()
    {
        string input = Src("def f():", "  x = foo(1,", "          2)", "  return x");
        Assert.Equal(Src("def f():", "    x = foo(1,", "            2)", "    return x"), Fmt(input));
    }

    [Fact]
    public void MultilineStringsAndLineEndingsAreKept()
    {
        string doc = "def f():\n    \"\"\"Doc\n      indented text\n    \"\"\"\n    return 1\n";
        Assert.Equal(doc, Fmt(doc));
        Assert.Equal("x = 1\r\ny = 2\r\n", Fmt("x=1\r\ny=2\r\n"));
        Assert.Equal("x = 1\n", Fmt("x = 1\n\n\n"));
        Assert.Equal("x = 1\n", Fmt("x = 1"));
    }

    [Fact]
    public void RelativeImportsAndDottedNames()
    {
        Assert.Equal(Src("from . import a", "from .. import b", "from ...pkg import c", "from a.b import (d, e)", "import x.y as z"),
            Fmt(Src("from  .  import a", "from ..import b", "from ... pkg import c", "from a . b import ( d,e )", "import x . y  as z")));
    }

    [Fact]
    public void RefusesBrokenCodeAndOptOuts()
    {
        Assert.True(PythonFormatter.Format("def f(:\n") == null, "syntax error");
        Assert.True(PythonFormatter.Format("x=1  # fmt: off\n") == null, "fmt: off");
    }

    [Fact]
    public void FormattingTwiceChangesNothing()
    {
        string messy = Src("import   os,sys", "class  A( object ) :", "  x=1", "  def  f( self,a,b = 2 ):", "    '''doc'''", "    return {'a':a,'b':[1,2,3]}[ 'a' ]",
            "def g(*,a):", "  print (a if a else-1) # c", "if x :", "  pass", "elif( y ):", "  pass");
        string once = Fmt(messy);
        Assert.Equal(once, Fmt(once));
    }

    // ------------------------------------------------------------ real programs

    static string? Python = Find();

    static string? Find()
    {
        try
        {
            var psi = new ProcessStartInfo(Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python", "--version") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            return p!.WaitForExit(20000) && p.ExitCode == 0 ? (Environment.GetEnvironmentVariable("PYMAGIC_PYTHON") ?? "python") : null;
        }
        catch { return null; }
    }

    static string RepoFile(string relative)
    {
        for (var d = new DirectoryInfo(AppContext.BaseDirectory); d != null; d = d.Parent)
        {
            string candidate = Path.Combine(d.FullName, relative);
            if (File.Exists(candidate)) return candidate;
        }
        throw new AssertionException("not found: " + relative);
    }

    [Fact]
    public void RealFilesFormatIdempotentlyAndKeepTheirSyntaxTree()
    {
        string[] files =
        {
            "src/PyMagic.Server/Run/pm_debug.py", "src/PyMagic.Server/Run/pm_tests.py", "examples/demo/main.py", "examples/demo/checkout.py",
            "examples/demo/shop/models.py", "examples/demo/config_loader.py", "examples/demo/tests/test_cart.py",
        };
        var pairs = new List<(string Before, string After)>();
        foreach (var rel in files)
        {
            string before = File.ReadAllText(RepoFile(rel));
            string? after = PythonFormatter.Format(before);
            Assert.True(after != null, "the formatter declined " + rel);
            Assert.Equal(after!, PythonFormatter.Format(after!)!);
            pairs.Add((before, after!));
        }
        if (Python == null) return;

        string dir = Path.Combine(Path.GetTempPath(), "pm_fmt_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            for (int i = 0; i < pairs.Count; i++)
            {
                File.WriteAllText(Path.Combine(dir, $"a{i}.py"), pairs[i].Before);
                File.WriteAllText(Path.Combine(dir, $"b{i}.py"), pairs[i].After);
            }
            string check = "import ast, sys\nbad = []\nfor i in range(" + pairs.Count + "):\n    a = ast.dump(ast.parse(open(f'a{i}.py', encoding='utf-8').read()))\n    b = ast.dump(ast.parse(open(f'b{i}.py', encoding='utf-8').read()))\n    if a != b: bad.append(i)\nprint('same' if not bad else 'different %s' % bad)\n";
            File.WriteAllText(Path.Combine(dir, "check.py"), check);
            var psi = new ProcessStartInfo(Python, "check.py") { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var proc = Process.Start(psi)!;
            var err = proc.StandardError.ReadToEndAsync();
            string output = proc.StandardOutput.ReadToEnd().Trim();
            proc.WaitForExit(30000);
            Assert.Equal("same", output + (output == "same" ? "" : " " + err.Result));
        }
        finally { Directory.Delete(dir, true); }
    }
}
