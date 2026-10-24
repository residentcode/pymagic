using System.Diagnostics;
using PyMagic.Server.Formatting;

namespace PyMagic.Tests;

public class FormatterTests
{
    static string Src(params string[] lines) => string.Join("\n", lines) + "\n";
    static string Fmt(string text) => PythonFormatter.Format(text) ?? throw new AssertionException("the formatter declined (" + PythonFormatter.LastMismatch + "):" + System.Environment.NewLine + text);

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
        Assert.Equal(Src("def f():", "    x = foo(1, 2)", "    return x"), Fmt(input));   // wrapped by hand but short enough: joined
    }

    [Fact]
    public void MultilineStringsAndLineEndingsAreKept()
    {
        string doc = "def f():\n    \"\"\"Doc\n    indented text\n    \"\"\"\n    return 1\n";
        Assert.Equal(doc, Fmt(doc));
        Assert.Equal("x = 1\r\ny = 2\r\n", Fmt("x=1\r\ny=2\r\n"));
        Assert.Equal("x = 1\n", Fmt("x = 1\n\n\n"));
        Assert.Equal("x = 1\n", Fmt("x = 1"));
    }

    [Fact]
    public void RelativeImportsAndDottedNames()
    {
        Assert.Equal(Src("from . import a", "from .. import b", "from ...pkg import c", "from a.b import d, e", "import x.y as z"),
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

    /// <summary>Compares a{i}.py with b{i}.py in a directory by their syntax trees (docstrings cleaned the way the formatter may rewrite them); "same" or the first difference.</summary>
    static string CompareTrees(string dir, IEnumerable<int> indexes)
    {
        const string script = """
import ast, inspect, sys

def dump(text):
    tree = ast.parse(text)
    for node in ast.walk(tree):
        if isinstance(node, (ast.Module, ast.ClassDef, ast.FunctionDef, ast.AsyncFunctionDef)) and node.body:
            first = node.body[0]
            if isinstance(first, ast.Expr) and isinstance(first.value, ast.Constant) and isinstance(first.value.value, str):
                first.value.value = chr(10).join(line.rstrip() for line in inspect.cleandoc(first.value.value).split(chr(10))).strip(chr(10))
    for node in ast.walk(tree):
        if isinstance(node, ast.Delete):
            node.targets = [e for t in node.targets for e in (t.elts if isinstance(t, ast.Tuple) else [t])]
    return ast.dump(tree)

bad = []
for i in [int(x) for x in sys.argv[1].split(',')]:
    a = dump(open('a%d.py' % i, encoding='utf-8').read())
    b = dump(open('b%d.py' % i, encoding='utf-8').read())
    if a != b:
        k = 0
        while k < min(len(a), len(b)) and a[k] == b[k]:
            k += 1
        bad.append('file %d differs: BEFORE ...%s... AFTER ...%s...' % (i, a[max(0, k - 120):k + 120], b[max(0, k - 120):k + 120]))
print('same' if not bad else chr(10).join(bad))
""";
        File.WriteAllText(Path.Combine(dir, "check.py"), script);
        var psi = new ProcessStartInfo(Python!, "check.py " + string.Join(",", indexes)) { WorkingDirectory = dir, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        using var proc = Process.Start(psi)!;
        var err = proc.StandardError.ReadToEndAsync();
        string output = proc.StandardOutput.ReadToEnd().Trim();
        proc.WaitForExit(60000);
        return output == "same" ? output : output + " " + err.Result;
    }

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
            Assert.True(after != null, "the formatter declined " + rel + " (" + PythonFormatter.LastMismatch + ")");
            string again = PythonFormatter.Format(after!) ?? "(declined: " + PythonFormatter.LastMismatch + ")";
            if (again != after)
            {
                var l1 = after!.Split((char)10);
                var l2 = again.Split((char)10);
                int d = 0;
                while (d < l1.Length && d < l2.Length && l1[d] == l2[d]) d++;
                throw new AssertionException(rel + " is not stable at line " + (d + 1) + ":" + System.Environment.NewLine + string.Join(System.Environment.NewLine, l1.Skip(Math.Max(0, d - 2)).Take(8)) + System.Environment.NewLine + "--- second pass:" + System.Environment.NewLine + string.Join(System.Environment.NewLine, l2.Skip(Math.Max(0, d - 2)).Take(8)));
            }
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
            Assert.Equal("same", CompareTrees(dir, Enumerable.Range(0, pairs.Count)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Fact]
    public void StandardLibraryModulesKeepTheirSyntaxTreeAndFormatStably()
    {
        if (Python == null) return;
        string? lib = RunPython("import sysconfig; print(sysconfig.get_paths()['stdlib'])");
        if (lib == null || !Directory.Exists(lib)) return;
        string[] names = { "argparse.py", "textwrap.py", "shlex.py", "dataclasses.py", "json/encoder.py", "json/decoder.py", "colorsys.py", "fractions.py", "heapq.py", "bisect.py", "base64.py", "copy.py", "contextlib.py", "functools.py", "queue.py", "string.py", "pprint.py", "difflib.py", "csv.py", "enum.py" };
        if (System.Environment.GetEnvironmentVariable("PYMAGIC_STDLIB_ALL") == "1")
            names = Directory.GetFiles(lib, "*.py").Select(Path.GetFileName).Where(n => n != null && !n.StartsWith("_")).Select(n => n!).OrderBy(n => n).ToArray();
        string dir = Path.Combine(Path.GetTempPath(), "pm_std_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var declined = new List<string>();
        var unstable = new List<string>();
        int checkedFiles = 0;
        try
        {
            var written = new List<int>();
            for (int i = 0; i < names.Length; i++)
            {
                string path = Path.Combine(lib, names[i]);
                if (!File.Exists(path)) continue;
                string before = File.ReadAllText(path);
                string? after = PythonFormatter.Format(before);
                if (after == null) { declined.Add(names[i] + " (" + PythonFormatter.LastMismatch + ")"); continue; }
                string? again = PythonFormatter.Format(after);
                if (again != after)
                {
                    var l1 = after.Split((char)10);
                    var l2 = (again ?? "(declined " + PythonFormatter.LastMismatch + ")").Split((char)10);
                    int d = 0;
                    while (d < l1.Length && d < l2.Length && l1[d] == l2[d]) d++;
                    unstable.Add(names[i] + " line " + (d + 1) + ": [" + string.Join(" / ", l1.Skip(d).Take(3)) + "] became [" + string.Join(" / ", l2.Skip(d).Take(3)) + "]");
                }
                File.WriteAllText(Path.Combine(dir, "a" + i + ".py"), before);
                File.WriteAllText(Path.Combine(dir, "b" + i + ".py"), after);
                written.Add(i);
                checkedFiles++;
            }
            Assert.Equal("same", CompareTrees(dir, written));
        }
        finally { Directory.Delete(dir, true); }
        Assert.True(unstable.Count == 0, "not stable: " + string.Join(", ", unstable));
        Assert.True(declined.Count <= (System.Environment.GetEnvironmentVariable("PYMAGIC_STDLIB_ALL") == "1" ? 1000 : 2), "declined " + declined.Count + ": " + string.Join(", ", declined));
        Assert.True(checkedFiles >= 12, "only " + checkedFiles + " files were checked");
    }

    static string? RunPython(string code)
    {
        try
        {
            var psi = new ProcessStartInfo(Python!, "-c \"" + code.Replace("\"", "'") + "\"") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi)!;
            string o = p.StandardOutput.ReadToEnd().Trim();
            return p.WaitForExit(20000) && p.ExitCode == 0 ? o : null;
        }
        catch { return null; }
    }
}
