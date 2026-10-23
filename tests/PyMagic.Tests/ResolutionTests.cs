using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class ResolutionTests
{
    /// <summary>A throwaway directory tree: files given as (relative path, content).</summary>
    sealed class Tree : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "pm_res_" + Guid.NewGuid().ToString("N")[..8]);
        public Tree(params (string Path, string Content)[] files)
        {
            Directory.CreateDirectory(Root);
            foreach (var (p, c) in files)
            {
                var full = Path.Combine(Root, p.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                File.WriteAllText(full, c);
            }
        }
        public string Dir(string rel) => Path.Combine(Root, rel.Replace('/', Path.DirectorySeparatorChar));
        public void Dispose() { try { Directory.Delete(Root, true); } catch { /* best effort */ } }
    }

    /// <summary>A typeshed checkout, from PYMAGIC_TYPESHED or PyCharm's bundled copy; null when none is available.</summary>
    static string? Typeshed()
    {
        var env = Workspace.DefaultTypeshedPath;
        if (env != null && Directory.Exists(env)) return env;
        const string pycharm = @"C:\Program Files\JetBrains\PyCharm 2026.2.0.1\plugins\python-ce\helpers\typeshed";
        return Directory.Exists(pycharm) ? pycharm : null;
    }

    static string TypeOfLast(Workspace ws, string src)
    {
        var m = ws.AddModule("__test__", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors in test source");
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().Last();
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static Workspace With(string? typeshed = null, string[]? src = null, string[]? lib = null, PythonEnvironment? env = null) =>
        new(new WorkspaceOptions
        {
            SourceRoots = src ?? Array.Empty<string>(),
            LibraryRoots = lib ?? Array.Empty<string>(),
            TypeshedPath = typeshed,
            Environment = env ?? PythonEnvironment.Default,
        });

    // ---------------------------------------------------------------- search order

    [Fact]
    public void UserCodeShadowsLibrariesAndStubsWinOverSourcesInTheSameRoot()
    {
        using var t = new Tree(
            ("src/helper.py", "def f() -> int: ...\n"),
            ("src/helper.pyi", "def f() -> str: ...\n"),
            ("src/plain.py", "X = 1\n"));
        var ws = With(src: new[] { t.Dir("src") });
        Assert.Equal("str", TypeOfLast(ws, "import helper\nhelper.f()"));
        Assert.Equal(ResolutionKind.UserCode, ws.GetModule("helper")!.Kind);
        Assert.True(ws.GetModule("helper")!.IsStub);
        Assert.Equal("int", TypeOfLast(ws, "import plain\nplain.X"));
    }

    [Fact]
    public void StubPackagesDescribeInstalledLibraries()
    {
        using var t = new Tree(
            ("site/foo/__init__.py", "def f():\n    return 1\n"),
            ("site/foo-stubs/__init__.pyi", "def f() -> str: ...\n"));
        var ws = With(lib: new[] { t.Dir("site") });
        Assert.Equal("str", TypeOfLast(ws, "import foo\nfoo.f()"));
        Assert.Equal(ResolutionKind.StubPackage, ws.GetModule("foo")!.Kind);
    }

    [Fact]
    public void PartialStubPackagesFallBackToTheLibrary()
    {
        using var t = new Tree(
            ("site/foo/__init__.py", ""),
            ("site/foo/bar.py", "def g() -> int: ...\n"),
            ("site/foo/baz.py", "def h() -> int: ...\n"),
            ("site/foo-stubs/__init__.pyi", ""),
            ("site/foo-stubs/baz.pyi", "def h() -> str: ...\n"),
            ("site/foo-stubs/py.typed", "partial\n"));
        var ws = With(lib: new[] { t.Dir("site") });
        Assert.Equal("str", TypeOfLast(ws, "import foo.baz\nfoo.baz.h()"));            // covered by the stubs
        Assert.Equal("int", TypeOfLast(ws, "import foo.bar\nfoo.bar.g()"));            // not stubbed: the real module
        Assert.Equal(ResolutionKind.StubPackage, ws.GetModule("foo.baz")!.Kind);
        Assert.Equal(ResolutionKind.LibrarySource, ws.GetModule("foo.bar")!.Kind);
    }

    [Fact]
    public void InlineTypedPackagesAreMarkedByPyTyped()
    {
        using var t = new Tree(
            ("site/typed/__init__.py", "def f() -> int: ...\n"),
            ("site/typed/py.typed", ""),
            ("site/untyped/__init__.py", "def f() -> int: ...\n"));
        var ws = With(lib: new[] { t.Dir("site") });
        Assert.Equal(ResolutionKind.InlinePackage, ws.GetModule("typed")!.Kind);
        Assert.Equal(ResolutionKind.LibrarySource, ws.GetModule("untyped")!.Kind);
        Assert.Equal("int", TypeOfLast(ws, "import typed\ntyped.f()"));
    }

    [Fact]
    public void InlineTypedPackagesBeatTypeshedStubs()
    {
        var ts = Typeshed();
        if (ts == null) return;
        // a library that ships py.typed is trusted over the third-party stubs of the same name in typeshed
        using var t = new Tree(("site/requests/__init__.py", "def get(url) -> int: ...\n"), ("site/requests/py.typed", ""));
        var typed = With(ts, lib: new[] { t.Dir("site") });
        Assert.Equal(ResolutionKind.InlinePackage, typed.GetModule("requests")!.Kind);
        var bare = With(ts);
        Assert.Equal(ResolutionKind.Typeshed, bare.GetModule("requests")!.Kind);
    }

    [Fact]
    public void NamespacePackagesAndRelativeImports()
    {
        using var t = new Tree(
            ("src/ns/mod.py", "def f() -> int: ...\n"),
            ("src/pkg/__init__.py", "from .sub import g\nfrom . import sub\n"),
            ("src/pkg/sub.py", "def g() -> str: ...\nfrom .other import h\n"),
            ("src/pkg/other.py", "def h() -> float: ...\n"));
        var ws = With(src: new[] { t.Dir("src") });
        Assert.Equal(ResolutionKind.NamespacePackage, ws.GetModule("ns")!.Kind);
        Assert.Equal("int", TypeOfLast(ws, "import ns.mod\nns.mod.f()"));
        Assert.Equal("int", TypeOfLast(ws, "from ns import mod\nmod.f()"));
        Assert.Equal("str", TypeOfLast(ws, "import pkg\npkg.g()"));
        Assert.Equal("float", TypeOfLast(ws, "import pkg\npkg.sub.h()"));
        Assert.Equal("float", TypeOfLast(ws, "from pkg.sub import h\nh()"));
    }

    // ---------------------------------------------------------------- typeshed

    [Fact]
    public void StandardLibraryComesFromTypeshed()
    {
        var ts = Typeshed();
        if (ts == null) return;
        var ws = With(ts);
        Assert.Equal(ResolutionKind.Typeshed, ws.GetModule("os")!.Kind);
        Assert.True(ws.GetModule("os")!.IsStub);
        Assert.Equal("str", TypeOfLast(ws, "import os\nos.getcwd()"));
        Assert.Equal("str", TypeOfLast(ws, "import os.path\nos.path.join('a', 'b')"));
        Assert.Equal("str", TypeOfLast(ws, "import json\njson.dumps({})"));
        Assert.Equal("list[str]", TypeOfLast(ws, "import sys\nsys.argv"));
        Assert.Equal("int", TypeOfLast(ws, "import datetime\ndatetime.datetime.now().year"));
        Assert.Equal("Path", TypeOfLast(ws, "from pathlib import Path\nPath('a') / 'b'"));
    }

    [Fact]
    public void VersionsFileGatesModulesByPythonVersion()
    {
        var ts = Typeshed();
        if (ts == null) return;
        var py39 = With(ts, env: new PythonEnvironment(3, 9, "linux"));
        var py313 = With(ts, env: new PythonEnvironment(3, 13, "linux"));
        Assert.True(py39.GetModule("imp") != null);              // removed in 3.12
        Assert.True(py313.GetModule("imp") == null);
        Assert.True(py39.GetModule("tomllib") == null);          // added in 3.11
        Assert.True(py313.GetModule("tomllib") != null);
        Assert.True(py39.GetModule("asyncio.taskgroups") == null);   // submodules inherit or override their package's range
        Assert.True(py313.GetModule("asyncio.taskgroups") != null);
    }

    [Fact]
    public void StaticConditionsPickTheRightDefinition()
    {
        var ts = Typeshed();
        if (ts == null) return;
        const string src = "import sys\nif sys.version_info >= (3, 12):\n    X = 1\nelse:\n    X = 'old'\nif sys.platform == 'win32':\n    Y = 1\nelse:\n    Y = 'posix'\n";
        var win313 = With(ts, env: new PythonEnvironment(3, 13, "win32"));
        Assert.Equal("int", TypeOfLast(win313, src + "X"));
        Assert.Equal("int", TypeOfLast(win313, src + "Y"));
        var linux39 = With(ts, env: new PythonEnvironment(3, 9, "linux"));
        Assert.Equal("str", TypeOfLast(linux39, src + "X"));
        Assert.Equal("str", TypeOfLast(linux39, src + "Y"));
        // typeshed itself is full of such branches: os.O_BINARY only exists on Windows
        Assert.Equal("int", TypeOfLast(win313, "import os\nos.O_BINARY"));
    }

    [Fact]
    public void ThirdPartyStubsFromTypeshed()
    {
        var ts = Typeshed();
        if (ts == null) return;
        var ws = With(ts);
        var req = ws.GetModule("requests");
        Assert.True(req != null && req.Kind == ResolutionKind.Typeshed && req.IsStub);
        Assert.True(ws.GetModule("yaml") != null);
        Assert.True(ws.GetModule("definitely_not_a_module") == null);
    }

    [Fact]
    public void TypeshedStdlibTypesAreUseful()
    {
        var ts = Typeshed();
        if (ts == null) return;
        var ws = With(ts);
        Assert.Equal("str", TypeOfLast(ws, "import re\nre.sub('a', 'b', 'c')"));
        Assert.Equal("Match[str] | None", TypeOfLast(ws, "import re\nre.match('a', 'b')"));
        Assert.Equal("Pattern[str]", TypeOfLast(ws, "import re\nre.compile('a')"));
        Assert.Equal("list[str]", TypeOfLast(ws, "import os\nos.listdir('.')"));
        Assert.Equal("int", TypeOfLast(ws, "import time\nint(time.time())"));
        Assert.Equal("float", TypeOfLast(ws, "import math\nmath.sqrt(2)"));
        Assert.Equal("int", TypeOfLast(ws, "import itertools\nnext(itertools.count())"));
        Assert.Equal("Counter[str]", TypeOfLast(ws, "from collections import Counter\nCounter(['a'])"));
        Assert.Equal("defaultdict[str, list[int]]", TypeOfLast(ws, "from collections import defaultdict\nd: defaultdict[str, list[int]] = defaultdict(list)\nd"));
        Assert.Equal("Iterable[int]", TypeOfLast(ws, "from collections.abc import Iterable\ndef f(x: Iterable[int]):\n    x"));
        Assert.Equal("CompletedProcess[bytes]", TypeOfLast(ws, "import subprocess\nsubprocess.run(['ls'])"));
    }

    // ---------------------------------------------------------------- star imports

    [Fact]
    public void StarImportsRespectAll()
    {
        using var t = new Tree(
            ("src/b.py", "__all__ = ['x', 'z']\nx = 1\ny = 2\nz = 'z'\n"),
            ("src/a.py", "from b import *\n"),
            ("src/c.py", "x = 1\n_hidden = 2\nname = 's'\n"),
            ("src/d.py", "from c import *\n"));
        var ws = With(src: new[] { t.Dir("src") });
        Assert.Equal("int", TypeOfLast(ws, "import a\na.x"));
        Assert.Equal("str", TypeOfLast(ws, "import a\na.z"));
        Assert.Equal("Unknown", TypeOfLast(ws, "import a\na.y"));              // not in __all__
        Assert.Equal("int", TypeOfLast(ws, "import d\nd.x"));
        Assert.Equal("Unknown", TypeOfLast(ws, "import d\nd._hidden"));         // underscore names are not exported without __all__
        Assert.Equal("int", TypeOfLast(ws, "from a import x\nx"));
        Assert.Equal("int", TypeOfLast(ws, "from b import *\nx"));              // used directly in the importing module
    }

    [Fact]
    public void StaticConditionsEvaluateTheNeededForms()
    {
        PythonEnvironment env = new(3, 11, "linux");
        bool? Eval(string cond)
        {
            var r = Parser.Parse(cond + "\n");
            return StaticConditions.Evaluate(((ExprStmt)r.Module.Body[0]).Value, env);
        }
        Assert.Equal(true, Eval("sys.version_info >= (3, 9)"));
        Assert.Equal(false, Eval("sys.version_info >= (3, 12)"));
        Assert.Equal(true, Eval("sys.version_info < (3, 12)"));
        Assert.Equal(true, Eval("sys.version_info >= (3,)"));
        Assert.Equal(false, Eval("sys.version_info == (3, 11)"));          // a 5-tuple never equals a 2-tuple
        Assert.Equal(true, Eval("sys.version_info[:2] == (3, 11)"));
        Assert.Equal(true, Eval("sys.version_info[0] == 3"));
        Assert.Equal(true, Eval("sys.version_info.minor > 8"));
        Assert.Equal(true, Eval("sys.platform == 'linux'"));
        Assert.Equal(false, Eval("sys.platform == 'win32'"));
        Assert.Equal(true, Eval("sys.platform != 'win32'"));
        Assert.Equal(true, Eval("sys.platform.startswith('lin')"));
        Assert.Equal(true, Eval("TYPE_CHECKING"));
        Assert.Equal(false, Eval("not TYPE_CHECKING"));
        Assert.Equal(true, Eval("sys.version_info >= (3, 9) and sys.platform == 'linux'"));
        Assert.Equal(false, Eval("sys.version_info >= (3, 12) and unknown_call()"));    // false and anything
        Assert.Equal(true, Eval("sys.version_info >= (3, 9) or unknown_call()"));
        Assert.Equal(null, Eval("some_variable"));
        Assert.Equal(null, Eval("sys.version_info >= (3, 9) and flag"));
    }
}
