using PyMagic.Core.Syntax;
using PyMagic.Core.Types;

namespace PyMagic.Tests;

public class TypeTests
{
    /// <summary>Type of the last expression statement of the source, printed.</summary>
    static string T(string src, params (string Name, string Source)[] others)
    {
        var (ws, m) = Load(src, others);
        // the last expression statement anywhere (so tests can look at names inside blocks)
        var last = AstChildren.Descendants(m.Ast).OfType<ExprStmt>().LastOrDefault()
                   ?? throw new AssertionException("source needs an expression statement");
        return ws.Evaluator.TypeOf(m, last.Value).ToString();
    }

    static (Workspace Ws, ModuleInfo M) Load(string src, params (string Name, string Source)[] others)
    {
        var ws = new Workspace();
        foreach (var (n, s) in others) ws.AddModule(n, s, isPackage: n == "pkg");
        var m = ws.AddModule("main", src);
        Assert.True(m.SyntaxErrors.Count == 0, "syntax errors in test source: " + string.Join("; ", m.SyntaxErrors.Select(d => d.Message)));
        return (ws, m);
    }

    /// <summary>Real typeshed is more precise than the bundled stubs; accept either answer.</summary>
    internal static void OneOf(string[] accepted, string actual) =>
        Assert.True(accepted.Contains(actual), $"expected one of [{string.Join(", ", accepted)}] but got {actual}");

    // ---------------------------------------------------------------- stubs themselves

    [Fact]
    public void BundledStubsParseCleanly()
    {
        var ws = new Workspace();
        foreach (var name in new[] { "builtins", "typing" })
        {
            var mod = ws.GetModule(name)!;
            Assert.True(mod.SyntaxErrors.Count == 0, name + ": " + string.Join("; ", mod.SyntaxErrors.Select(d => $"{d.Start}:{d.Message}")));
        }
    }

    // ---------------------------------------------------------------- literals and containers

    [Fact]
    [Case("1", "int")]
    [Case("1.5", "float")]
    [Case("2j", "complex")]
    [Case("'a'", "str")]
    [Case("b'a'", "bytes")]
    [Case("True", "bool")]
    [Case("None", "None")]
    [Case("f'{1}'", "str")]
    [Case("[1, 2]", "list[int]")]
    [Case("[1, 'a']", "list[int | str]")]
    [Case("[]", "list[Unknown]")]
    [Case("{1: 'a'}", "dict[int, str]")]
    [Case("{}", "dict[Unknown, Unknown]")]
    [Case("{1, 2}", "set[int]")]
    [Case("(1, 'a')", "tuple[int, str]")]
    [Case("()", "tuple[()]")]
    [Case("[i for i in [1, 2]]", "list[int]")]
    [Case("{k: str(k) for k in [1]}", "dict[int, str]")]
    [Case("1 if x else 'a'", "int | str")]
    [Case("not 1", "bool")]
    [Case("1 < 2", "bool")]
    public void Literals(string src, string expected) => Assert.Equal(expected, T(src));

    // ---------------------------------------------------------------- operators

    [Fact]
    [Case("1 + 2", "int")]
    [Case("1 + 2.5", "float")]
    [Case("2.5 + 1", "float")]
    [Case("1 / 2", "float")]
    [Case("1 // 2", "int")]
    [Case("'a' + 'b'", "str")]
    [Case("'a' * 3", "str")]
    [Case("3 * 'a'", "str")]
    [Case("[1] + [2]", "list[int]")]
    [Case("-1", "int")]
    [Case("-1.5", "float")]
    [Case("x = None\nx or 1", "int")]
    public void Operators(string src, string expected) => Assert.Equal(expected, T(src));

    // ---------------------------------------------------------------- variables and flow

    [Fact]
    public void VariablesFollowAssignments()
    {
        Assert.Equal("int", T("x = 1\nx"));
        Assert.Equal("str", T("x = 1\nx = 'a'\nx"));
        Assert.Equal("int | str", T("if c:\n    x = 1\nelse:\n    x = 'a'\nx"));
        Assert.Equal("int", T("x = 0\nx += 1\nx"));
    }

    [Fact]
    public void LoopVariablesAndUnpacking()
    {
        Assert.Equal("int", T("for i in [1, 2]:\n    pass\ni"));
        Assert.Equal("int", T("for k, v in {1: 'a'}.items():\n    pass\nk"));
        Assert.Equal("str", T("for k, v in {1: 'a'}.items():\n    pass\nv"));
        Assert.Equal("int", T("for i, s in enumerate(['a']):\n    pass\ni"));
        Assert.Equal("str", T("for i, s in enumerate(['a']):\n    pass\ns"));
        Assert.Equal("str", T("a, b = 1, 's'\nb"));
        Assert.Equal("list[int]", T("a, *rest = [1, 2, 3]\nrest"));
        Assert.Equal("str", T("for c in 'abc':\n    pass\nc"));
    }

    [Fact]
    public void ExceptionAndWithTargets()
    {
        Assert.Equal("ValueError", T("try:\n    pass\nexcept ValueError as e:\n    e"));
        Assert.Equal("OSError | KeyError", T("try:\n    pass\nexcept (OSError, KeyError) as e:\n    e"));
        OneOf(new[] { "TextIO", "TextIOWrapper" }, T("with open('f') as f:\n    f"));   // the bundled stub is coarser than typeshed
        Assert.Equal("str", T("with open('f') as f:\n    f.read()"));
    }

    // ---------------------------------------------------------------- functions

    [Fact]
    public void FunctionReturnTypes()
    {
        Assert.Equal("int", T("def f():\n    return 1\nf()"));
        Assert.Equal("None", T("def f():\n    pass\nf()"));
        Assert.Equal("str", T("def f(x: int) -> str: ...\nf(1)"));
        Assert.Equal("int | None", T("def f(c):\n    if c:\n        return 1\nf(0)"));
        Assert.Equal("int | str", T("def f(c):\n    if c:\n        return 1\n    return 's'\nf(0)"));
        Assert.Equal("int", T("f = lambda x: 1\nf(2)"));
        Assert.Equal("int", T("def f(x=1):\n    return x\nf()"));
    }

    [Fact]
    public void GeneratorsAndCoroutines()
    {
        Assert.Equal("Generator[int, Any, None]", T("def g():\n    yield 1\ng()"));
        Assert.Equal("Coroutine[Any, Any, int]", T("async def f() -> int: ...\nf()"));
        Assert.Equal("Coroutine[Any, Any, int]", T("async def f():\n    return 1\nf()"));
        Assert.Equal("int", T("async def f() -> int: ...\nasync def g():\n    x = await f()\n    x"));
        Assert.Equal("int", T("def g():\n    yield 1\nfor x in g():\n    pass\nx"));
    }

    [Fact]
    public void ParametersInsideBodies()
    {
        Assert.Equal("int", T("def f(a: int):\n    a"));
        Assert.Equal("int", T("def f(a=1):\n    a"));
        Assert.Equal("tuple[int, ...]", T("def f(*a: int):\n    a"));
        Assert.Equal("dict[str, str]", T("def f(**kw: str):\n    kw"));
        Assert.Equal("int | None", T("from typing import Optional\ndef f(a: Optional[int]):\n    a"));
        Assert.Equal("int | str", T("def f(a: int | str):\n    a"));
        Assert.Equal("list[int]", T("from typing import List\ndef f(a: List[int]):\n    a"));
        Assert.Equal("dict[str, list[int]]", T("def f(a: dict[str, list[int]]):\n    a"));
        Assert.Equal("tuple[int, str]", T("from typing import Tuple\ndef f(a: Tuple[int, str]):\n    a"));
        Assert.Equal("int", T("def f(a: 'int'):\n    a"));
    }

    // ---------------------------------------------------------------- classes

    [Fact]
    public void ClassesAndInstances()
    {
        Assert.Equal("type[A]", T("class A: pass\nA"));
        Assert.Equal("A", T("class A: pass\nA()"));
        Assert.Equal("int", T("class A:\n    x = 1\nA.x"));
        Assert.Equal("int", T("class A:\n    x = 1\nA().x"));
        Assert.Equal("int", T("class A:\n    def __init__(self):\n        self.x = 1\nA().x"));
        Assert.Equal("int", T("class A:\n    def m(self) -> int: ...\nA().m()"));
        Assert.Equal("int", T("class A:\n    @property\n    def p(self) -> int: ...\nA().p"));
        Assert.Equal("int", T("class A:\n    @staticmethod\n    def s() -> int: ...\nA.s()"));
        Assert.Equal("A", T("class A:\n    @classmethod\n    def c(cls) -> 'A': ...\nA.c()"));
        Assert.Equal("A", T("class A:\n    def m(self):\n        return self\nA().m()"));
        Assert.Equal("type[int]", T("type(1)"));
    }

    [Fact]
    public void InheritanceAndSuper()
    {
        const string src = "class A:\n    def __init__(self, v: int):\n        self.v = v\n    def get(self) -> int: ...\nclass B(A):\n    def __init__(self):\n        super().__init__(1)\n";
        Assert.Equal("int", T(src + "B().get()"));
        Assert.Equal("int", T(src + "B().v"));
        // both orders of the diamond resolve through the MRO
        const string diamond = "class A:\n    def f(self) -> int: ...\nclass B(A): pass\nclass C(A):\n    def f(self) -> str: ...\nclass D(B, C): pass\n";
        Assert.Equal("str", T(diamond + "D().f()"));
    }

    [Fact]
    public void MroIsC3()
    {
        var (ws, m) = Load("class O: pass\nclass A(O): pass\nclass B(O): pass\nclass C(A, B): pass\n");
        var scope = m.Semantics.ModuleScope.Symbols["C"];
        var cls = (ClassType)ws.Evaluator.TypeOfSymbol(scope);
        var names = ws.Evaluator.MroOf(cls.Info).OfType<ClassType>().Select(c => c.Info.Name);
        Assert.Equal("C,A,B,O,object", string.Join(",", names));
    }

    [Fact]
    public void UnknownBasesSuppressErrors()
    {
        var (ws, m) = Load("class B(Unresolved):\n    pass\nB().anything");
        var last = (ExprStmt)m.Ast.Body[^1];
        var attr = (AttributeExpr)last.Value;
        var res = ws.Evaluator.AttributeType(ws.Evaluator.TypeOf(m, attr.Value), attr.Attr);
        Assert.Equal(0, res.Missing.Count);
        Assert.Equal("Unknown", res.Type.ToString());
    }

    [Fact]
    public void MissingAttributesAreReported()
    {
        var (ws, m) = Load("class A: pass\nA().foo");
        var attr = (AttributeExpr)((ExprStmt)m.Ast.Body[^1]).Value;
        var res = ws.Evaluator.AttributeType(ws.Evaluator.TypeOf(m, attr.Value), attr.Attr);
        Assert.Equal(1, res.Missing.Count);
    }

    // ---------------------------------------------------------------- generics and stdlib shapes

    [Fact]
    public void GenericFunctionsSolveTypeVariables()
    {
        const string pre = "from typing import TypeVar, List, Generic\nT = TypeVar('T')\n";
        Assert.Equal("int", T(pre + "def first(xs: List[T]) -> T: ...\nfirst([1, 2])"));
        Assert.Equal("str", T(pre + "def ident(x: T) -> T: ...\nident('s')"));
        Assert.Equal("list[int]", T(pre + "def wrap(x: T) -> List[T]: ...\nwrap(1)"));
    }

    [Fact]
    public void GenericClasses()
    {
        const string pre = "from typing import TypeVar, Generic\nT = TypeVar('T')\nclass Box(Generic[T]):\n    def __init__(self, v: T):\n        self.v = v\n    def get(self) -> T: ...\n";
        Assert.Equal("int", T(pre + "Box(1).get()"));
        Assert.Equal("int", T(pre + "Box(1).v"));
        Assert.Equal("str", T(pre + "b: Box[str] = Box('x')\nb.get()"));
        Assert.Equal("Box[int]", T(pre + "Box(1)"));
        Assert.Equal("int", T("class P[T]:\n    def get(self) -> T: ...\nx: P[int]\nx.get()"));
    }

    [Fact]
    public void BuiltinContainerMethods()
    {
        Assert.Equal("list[str]", T("'a b'.split()"));
        Assert.Equal("str", T("','.join(['a'])"));
        Assert.Equal("int", T("len([1])"));
        Assert.Equal("int | None", T("d = {'a': 1}\nd.get('a')"));
        Assert.Equal("int", T("d = {'a': 1}\nd['a']"));
        OneOf(new[] { "KeysView[str]", "dict_keys[str, int]" }, T("{'a': 1}.keys()"));
        Assert.Equal("list[int]", T("sorted([3, 1])"));
        Assert.Equal("int", T("max(1, 2)"));
        Assert.Equal("int", T("max([1, 2])"));
        Assert.Equal("list[int]", T("list(range(3))"));
        Assert.Equal("int", T("[1, 2][0]"));
        Assert.Equal("list[int]", T("[1, 2][0:1]"));
        Assert.Equal("str", T("'abc'[0]"));
        Assert.Equal("str", T("(1, 'a')[1]"));
        Assert.Equal("None", T("print('x')"));
        Assert.Equal("bool", T("isinstance(1, int)"));
        Assert.Equal("None", T("[1].append(2)"));
        Assert.Equal("int", T("[1].pop()"));
    }

    [Fact]
    public void ForwardReferencesAndAliases()
    {
        Assert.Equal("A", T("class A: pass\ndef f() -> 'A': ...\nf()"));
        Assert.Equal("list[int]", T("from typing import List\nIntList = List[int]\ndef f() -> IntList: ...\nf()"));
        Assert.Equal("int | None", T("from typing import Optional\nMaybe = Optional[int]\ndef f() -> Maybe: ...\nf()"));
    }

    // ---------------------------------------------------------------- imports

    [Fact]
    public void ImportsAcrossModules()
    {
        var util = ("util", "def helper() -> int: ...\nX = 5\nclass K:\n    def m(self) -> str: ...\n");
        Assert.Equal("int", T("from util import helper\nhelper()", util));
        Assert.Equal("int", T("from util import X\nX", util));
        Assert.Equal("int", T("import util\nutil.helper()", util));
        Assert.Equal("str", T("import util as u\nu.K().m()", util));
        Assert.Equal("Module('util')", T("import util\nutil", util));
        Assert.Equal("Unknown", T("import nothing_here\nnothing_here.f()"));
    }

    [Fact]
    public void PackagesAndSubmodules()
    {
        var pkg = ("pkg", "from .sub import f\n");
        var sub = ("pkg.sub", "def f() -> int: ...\n");
        Assert.Equal("int", T("import pkg\npkg.f()", pkg, sub));
        Assert.Equal("int", T("import pkg.sub\npkg.sub.f()", pkg, sub));
        Assert.Equal("int", T("from pkg import sub\nsub.f()", pkg, sub));
        Assert.Equal("int", T("from pkg.sub import f\nf()", pkg, sub));
    }

    // ---------------------------------------------------------------- calls

    static List<string> Problems(string src, bool includeTypes = false)
    {
        var (ws, m) = Load(src);
        var call = (CallExpr)((ExprStmt)m.Ast.Body[^1]).Value;   // the outermost call of the last statement
        var res = ws.Evaluator.ResolveCall(m, call);
        return res.Targets.SelectMany(t => t.Problems).Where(p => includeTypes || p.Kind != CallProblemKind.Type).Select(p => p.Message).ToList();
    }

    [Fact]
    public void ArgumentMappingProblems()
    {
        Assert.Equal("Parameter 'a' unfilled", string.Join(";", Problems("def f(a, b=1): ...\nf()")));
        Assert.Equal("", string.Join(";", Problems("def f(a, b=1): ...\nf(1)")));
        Assert.Equal("Unexpected argument", string.Join(";", Problems("def f(a, b=1): ...\nf(1, 2, 3)")));
        Assert.Equal("Unexpected argument 'c'", string.Join(";", Problems("def f(a, b=1): ...\nf(1, c=2)")));
        Assert.Equal("Multiple values for parameter 'a'", string.Join(";", Problems("def f(a, b=1): ...\nf(1, a=2)")));
        Assert.Equal("", string.Join(";", Problems("def f(a, *args, **kw): ...\nf(1, 2, 3, x=4)")));
        Assert.Equal("", string.Join(";", Problems("def f(a, b):\n    pass\nxs = [1, 2]\nf(*xs)")));
        Assert.Equal("", string.Join(";", Problems("def f(a, b):\n    pass\nd = {}\nf(**d)")));
        Assert.Equal("Parameter 'k' unfilled", string.Join(";", Problems("def f(a, *, k): ...\nf(1)")));
        Assert.Equal("", string.Join(";", Problems("def f(a, *, k): ...\nf(1, k=2)")));
        Assert.Equal("Unexpected argument", string.Join(";", Problems("def f(a, *, k): ...\nf(1, 2, k=2)")));
        Assert.Equal("Unexpected argument 'a'", string.Join(";", Problems("def f(a, /): ...\nf(a=1)")).Replace("Parameter 'a' unfilled", "").Trim(';'));
    }

    [Fact]
    public void AnUnknownBaseBeforeTheInitFoundLeavesTheCallUnchecked()
    {
        // starlette's WebSocketDenialResponse(httpx.Response, WebSocketDisconnect) with httpx not found: Response may have its own __init__
        const string src = "from missing_module import Response\nclass Base:\n    def __init__(self, code: int = 1000) -> None: ...\n" +
                           "class D(Response, Base): pass\nD(status_code=1, content=b'')";
        Assert.Equal("", string.Join(";", Problems(src)));
        // an unknown base after the class that has the __init__ does not hide it
        Assert.Equal("Unexpected argument 'nope'", string.Join(";", Problems("from missing_module import Mixin\nclass Base:\n    def __init__(self, code: int = 1000) -> None: ...\nclass D(Base, Mixin): pass\nD(nope=1)")));
    }

    [Fact]
    public void MethodAndConstructorMapping()
    {
        Assert.Equal("Parameter 'x' unfilled", string.Join(";", Problems("class A:\n    def __init__(self, x): ...\nA()")));
        Assert.Equal("", string.Join(";", Problems("class A:\n    def __init__(self, x): ...\nA(1)")));
        Assert.Equal("", string.Join(";", Problems("class A:\n    def m(self, x): ...\nA().m(1)")));
        Assert.Equal("Parameter 'x' unfilled", string.Join(";", Problems("class A:\n    def m(self, x): ...\nA().m()")));
        Assert.Equal("", string.Join(";", Problems("class A:\n    @staticmethod\n    def s(x): ...\nA.s(1)")));
        Assert.Equal("", string.Join(";", Problems("class A:\n    @classmethod\n    def c(cls, x): ...\nA.c(1)")));
        Assert.Equal("'A()' takes no arguments", string.Join(";", Problems("class A: pass\nA(1)")));
        Assert.Equal("", string.Join(";", Problems("class A(Unknown): pass\nA(1)")));
        Assert.Equal("", string.Join(";", Problems("class A:\n    def __init__(self, x): ...\nclass B(A): pass\nB(1)")));
    }

    [Fact]
    public void ArgumentTypeMismatchesAreSeparate()
    {
        Assert.Equal("", string.Join(";", Problems("def f(a: int): ...\nf('s')")));
        Assert.Equal("Expected type 'int', got 'str' instead", string.Join(";", Problems("def f(a: int): ...\nf('s')", includeTypes: true)));
        Assert.Equal("", string.Join(";", Problems("def f(a: float): ...\nf(1)", includeTypes: true)));
        Assert.Equal("", string.Join(";", Problems("def f(a: int | None): ...\nf(None)", includeTypes: true)));
        Assert.Equal("", string.Join(";", Problems("from typing import Iterable\ndef f(a: Iterable[int]): ...\nf([1])", includeTypes: true)));
        Assert.Equal("", string.Join(";", Problems("from typing import Sized\ndef f(a: Sized): ...\nf([1])", includeTypes: true)));
    }

    [Fact]
    public void CallResolutionRecordsTargets()
    {
        var (ws, m) = Load("def f(a, b=2): ...\nf(1, b=3)");
        var call = (CallExpr)((ExprStmt)m.Ast.Body[^1]).Value;
        var res = ws.Evaluator.ResolveCall(m, call);
        Assert.Equal(1, res.Targets.Count);
        var map = res.Targets[0].Mappings;
        Assert.Equal("a", map[0].Param!.Name);
        Assert.Equal("b", map[1].Param!.Name);
    }

    // ---------------------------------------------------------------- recursion and cycles

    [Fact]
    public void SelfReferentialDefinitionsTerminate()
    {
        Assert.Equal("int", T("x = 0\nfor i in range(3):\n    x = x + 1\nx"));
        Assert.Equal("Unknown", T("def f():\n    return f()\nf()"));
        Assert.Equal("int", T("def f(n):\n    if n:\n        return f(n - 1)\n    return 1\nf(3)"));
        Assert.Equal("int", T("class A:\n    def __init__(self):\n        self.n = 0\n    def inc(self):\n        self.n = self.n + 1\nA().n"));
    }
}
