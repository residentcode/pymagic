using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

public class ScopeTests
{
    static (SemanticModel M, Module Mod) Sem(string src)
    {
        var r = Parser.Parse(src);
        Assert.True(r.Diagnostics.Count == 0, "syntax errors: " + string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        return (SemanticModel.Build(r.Module), r.Module);
    }

    static List<NameExpr> Names(Module m, string name) =>
        AstChildren.Descendants(m).OfType<NameExpr>().Where(n => n.Name == name).ToList();

    static string Unresolved(SemanticModel m) => string.Join(",", m.Diagnostics.Select(d => d.Message));

    [Fact]
    public void LocalShadowsGlobal()
    {
        var (m, mod) = Sem("x = 1\ndef f():\n    x = 2\n    return x\n");
        var xs = Names(mod, "x");                       // module x, local x, return x
        var modSym = m.Resolve(xs[0])!;
        var localSym = m.Resolve(xs[2])!;
        Assert.True(modSym.Scope.Kind == ScopeKind.Module);
        Assert.True(localSym.Scope.Kind == ScopeKind.Function);
        Assert.True(!ReferenceEquals(modSym, localSym));
    }

    [Fact]
    public void LocalityIsStaticEvenWhenAssignedLater()
    {
        var (m, mod) = Sem("x = 1\ndef f():\n    print(x)\n    x = 2\n");
        var use = Names(mod, "x")[1];
        Assert.True(m.Resolve(use)!.Scope.Kind == ScopeKind.Function);
    }

    [Fact]
    public void ClosuresSeeEnclosingFunctions()
    {
        var (m, mod) = Sem("def outer():\n    v = 1\n    def inner():\n        return v\n    return inner\n");
        var use = Names(mod, "v")[1];
        var sym = m.Resolve(use)!;
        Assert.True(sym.Scope.Node is FunctionDef { Name: "outer" });
    }

    [Fact]
    public void GlobalDeclarationBindsModuleSymbol()
    {
        var (m, mod) = Sem("def f():\n    global g\n    g = 1\n\ndef h():\n    return g\n");
        var gs = Names(mod, "g");
        var assigned = m.Resolve(gs[0])!;
        var used = m.Resolve(gs[1])!;
        Assert.True(assigned.Scope.Kind == ScopeKind.Module);
        Assert.True(ReferenceEquals(assigned, used));
        Assert.Equal(1, assigned.Declarations.Count);
    }

    [Fact]
    public void NonlocalRebindsEnclosingVariable()
    {
        var (m, mod) = Sem("def outer():\n    n = 0\n    def inc():\n        nonlocal n\n        n += 1\n    return inc\n");
        var ns = Names(mod, "n");
        var sym = m.Resolve(ns[1])!;
        Assert.True(sym.Scope.Node is FunctionDef { Name: "outer" });
        Assert.True(ReferenceEquals(sym, m.Resolve(ns[0])));
    }

    [Fact]
    public void InvalidNonlocalIsReported()
    {
        var (m1, _) = Sem("nonlocal x\n");
        Assert.Contains("module level", Unresolved(m1));
        var (m2, _) = Sem("def f():\n    nonlocal y\n");
        Assert.Contains("no binding for nonlocal 'y'", Unresolved(m2));
    }

    [Fact]
    public void ClassScopeIsNotVisibleFromMethods()
    {
        var (m, _) = Sem("class A:\n    x = 1\n    def m(self):\n        return x\n");
        Assert.Contains("Unresolved reference 'x'", Unresolved(m));
    }

    [Fact]
    public void ClassBodySeesItsOwnNames()
    {
        var (m, _) = Sem("class A:\n    x = 1\n    y = x + 1\n");
        Assert.Equal("", Unresolved(m));
    }

    [Fact]
    public void ComprehensionVariablesDoNotLeak()
    {
        var (m, _) = Sem("[i for i in range(3)]\nprint(i)\n");
        Assert.Contains("Unresolved reference 'i'", Unresolved(m));
    }

    [Fact]
    public void ComprehensionFirstIterableUsesEnclosingScope()
    {
        // In a class body the first iterable may use class variables, later parts may not.
        var (m, _) = Sem("class A:\n    xs = [1]\n    ys = [x for x in xs]\n");
        Assert.Equal("", Unresolved(m));
        var (m2, _) = Sem("class A:\n    k = 2\n    ys = [k for x in range(3)]\n");
        Assert.Contains("Unresolved reference 'k'", Unresolved(m2));
    }

    [Fact]
    public void WalrusInComprehensionBindsEnclosingScope()
    {
        var (m, mod) = Sem("def f(a):\n    [y := i for i in a]\n    return y\n");
        Assert.Equal("", Unresolved(m));
        var y = Names(mod, "y")[1];
        Assert.True(m.Resolve(y)!.Scope.Kind == ScopeKind.Function);
    }

    [Fact]
    public void BuiltinsAndUnresolvedNames()
    {
        var (m, mod) = Sem("len(missing)\n");
        var len = m.ReferenceOf(Names(mod, "len")[0])!;
        Assert.True(len.IsBuiltin && len.Symbol == null);
        Assert.Equal("Unresolved reference 'missing'", Unresolved(m));
    }

    [Fact]
    public void ImportsBindNames()
    {
        var (m, _) = Sem("import os.path\nimport numpy as np\nfrom a.b import c, d as e\nos.getcwd(); np.x; c; e\n");
        Assert.Equal("", Unresolved(m));
        Assert.True(m.ModuleScope.Symbols.ContainsKey("os") && !m.ModuleScope.Symbols.ContainsKey("os.path"));
    }

    [Fact]
    public void StarImportSuppressesUnresolvedReports()
    {
        var (m, _) = Sem("from os import *\nwhatever\n");
        Assert.Equal("", Unresolved(m));
    }

    [Fact]
    public void DefaultsAndAnnotationsUseEnclosingScope()
    {
        var (m, mod) = Sem("d = 1\nT = int\ndef f(a: T = d, *, b=a):\n    return a\n");
        // 'a' in "b=a" is evaluated outside f, where it does not exist
        Assert.Contains("Unresolved reference 'a'", Unresolved(m));
        Assert.True(Unresolved(m).Split(',').Length == 1);
    }

    [Fact]
    public void LambdaParametersAreLocalToLambda()
    {
        var (m, mod) = Sem("f = lambda x, y=1: x + y\nx\n");
        Assert.Contains("Unresolved reference 'x'", Unresolved(m));
        var lam = AstChildren.Descendants(mod).OfType<LambdaExpr>().First();
        Assert.Equal(2, m.ScopeOf(lam)!.Symbols.Count);
    }

    [Fact]
    public void MatchCapturesBindNames()
    {
        var (m, _) = Sem("match p:\n    case [a, *rest]:\n        a, rest\n    case {'k': v, **kw}:\n        v, kw\n    case C(x=q) as whole:\n        q, whole\n");
        Assert.Contains("Unresolved reference 'p'", Unresolved(m));
        Assert.Contains("Unresolved reference 'C'", Unresolved(m));
        Assert.Equal(2, m.Diagnostics.Count);
    }

    [Fact]
    public void ExceptAndWithTargets()
    {
        var (m, _) = Sem("try:\n    pass\nexcept ValueError as err:\n    err\nwith open('f') as fh:\n    fh\n");
        Assert.Equal("", Unresolved(m));
    }

    [Fact]
    public void TypeParametersGetTheirOwnScope()
    {
        var (m, mod) = Sem("def f[T](a: T) -> T:\n    x: T = a\n    return x\n");
        Assert.Equal("", Unresolved(m));
        var t = Names(mod, "T")[0];
        Assert.True(m.Resolve(t)!.Declarations[0].Kind == DeclKind.TypeParam);
        Assert.Equal("", Unresolved(Sem("class C[T]:\n    def m(self, v: T): ...\n").M));
    }

    [Fact]
    public void ReferencesAreCollectedOnSymbols()
    {
        var (m, _) = Sem("x = 1\nx += 1\nprint(x)\ndel x\n");
        var x = m.ModuleScope.Symbols["x"];
        Assert.Equal(4, x.References.Count);
        Assert.True(x.References.Count(r => (r.Access & Access.Write) != 0) == 2);
        Assert.True(x.References.Any(r => r.Access == Access.Delete));
    }

    [Fact]
    public void InnermostScopeAtOffset()
    {
        const string src = "def f():\n    a = 1\n    return a\n";
        var (m, _) = Sem(src);
        Assert.True(m.InnermostScopeAt(src.IndexOf("return")).Kind == ScopeKind.Function);
        Assert.True(m.InnermostScopeAt(0).Kind == ScopeKind.Function || m.InnermostScopeAt(0).Kind == ScopeKind.Module);
        Assert.True(m.InnermostScopeAt(src.Length + 5).Kind == ScopeKind.Module);
    }

    [Fact]
    public void VisibleSymbolsForCompletion()
    {
        var (m, _) = Sem("g = 1\nclass A:\n    c = 1\n    def m(self, p):\n        loc = 2\n");
        var fn = m.ModuleScope.Children[0].Children[0];
        var names = fn.VisibleSymbols().Select(s => s.Name).ToHashSet();
        Assert.True(names.IsSupersetOf(new[] { "self", "p", "loc", "g", "A" }));
        Assert.True(!names.Contains("c"));
    }
}
