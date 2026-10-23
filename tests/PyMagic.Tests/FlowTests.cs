using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

public class FlowTests
{
    sealed record Ctx(Module Mod, SemanticModel Sem, FlowModel Flow);

    static Ctx Load(string src)
    {
        var r = Parser.Parse(src);
        Assert.True(r.Diagnostics.Count == 0, "syntax errors: " + string.Join("; ", r.Diagnostics.Select(d => d.Message)));
        var sem = SemanticModel.Build(r.Module);
        return new Ctx(r.Module, sem, new FlowModel(sem));
    }

    /// <summary>The n-th read of a name (in source order).</summary>
    static NameExpr Read(Ctx c, string name, int n = 0) =>
        AstChildren.Descendants(c.Mod).OfType<NameExpr>()
            .Where(x => x.Name == name && (c.Sem.ReferenceOf(x)!.Access & Access.Read) != 0).ElementAt(n);

    static ReachingResult Reach(Ctx c, string name, int n = 0) => c.Flow.ReachingDefinitions(Read(c, name, n))!;

    /// <summary>Source line (1-based) of each reaching definition, sorted, e.g. "2,4".</summary>
    static string Lines(string src, ReachingResult r) =>
        string.Join(",", r.Definitions.Select(d => src.Take(d.Node.Start).Count(ch => ch == '\n') + 1).OrderBy(x => x));

    [Fact]
    public void LaterAssignmentKillsEarlier()
    {
        const string src = "def f():\n    x = 1\n    x = 2\n    return x\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("3", Lines(src, r));
        Assert.True(!r.MaybeUnbound);
    }

    [Fact]
    public void BranchesMerge()
    {
        const string src = "def f(c):\n    if c:\n        x = 1\n    else:\n        x = 2\n    return x\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("3,5", Lines(src, r));
        Assert.True(!r.MaybeUnbound);
    }

    [Fact]
    public void MissingElseLeavesNameMaybeUnbound()
    {
        const string src = "def f(c):\n    if c:\n        x = 1\n    return x\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("3", Lines(src, r));
        Assert.True(r.MaybeUnbound);
    }

    [Fact]
    public void LoopCarriesDefinitionsAround()
    {
        const string src = "def f(c):\n    x = 0\n    while c:\n        x = x + 1\n    return x\n";
        var ctx = Load(src);
        Assert.Equal("2,4", Lines(src, Reach(ctx, "x", 0)));   // read inside the loop
        Assert.Equal("2,4", Lines(src, Reach(ctx, "x", 1)));   // read after the loop
    }

    [Fact]
    public void ForTargetIsMaybeUnboundAfterLoop()
    {
        const string src = "def f(xs):\n    for i in xs:\n        pass\n    return i\n";
        var r = Reach(Load(src), "i");
        Assert.True(r.MaybeUnbound && r.Definitions.Count == 1);
    }

    [Fact]
    public void TryExceptDefinesOnBothPaths()
    {
        const string src = "def f():\n    try:\n        x = g()\n    except ValueError:\n        x = 0\n    return x\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("3,5", Lines(src, r));
        Assert.True(!r.MaybeUnbound);
    }

    [Fact]
    public void TryFinallyDoesNotLeakExceptionalPathIntoFollowingCode()
    {
        const string src = "def f():\n    try:\n        x = g()\n    finally:\n        h()\n    return x\n";
        var r = Reach(Load(src), "x");
        Assert.True(!r.MaybeUnbound, "an exception skips the code after try/finally");
        Assert.Equal("3", Lines(src, r));
    }

    [Fact]
    public void HandlerCanSeeBothStatesOfVariableAssignedInTry()
    {
        const string src = "def f():\n    x = 0\n    try:\n        x = g()\n    except E:\n        return x\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("2,4", Lines(src, r));
    }

    [Fact]
    public void ExceptVariableIsUnboundAfterHandler()
    {
        const string src = "def f():\n    try:\n        pass\n    except E as e:\n        pass\n    return e\n";
        var r = Reach(Load(src), "e");
        Assert.True(r.MaybeUnbound && r.Definitions.Count == 0);
    }

    [Fact]
    public void DelUnbindsName()
    {
        const string src = "def f():\n    x = 1\n    del x\n    return x\n";
        var r = Reach(Load(src), "x", 0);
        Assert.True(r.MaybeUnbound && r.Definitions.Count == 0);
    }

    [Fact]
    public void ParametersAreDefinedOnEntry()
    {
        const string src = "def f(a, b=1, *c, **d):\n    return a, b, c, d\n";
        var ctx = Load(src);
        foreach (var n in new[] { "a", "b", "c", "d" })
        {
            var r = Reach(ctx, n);
            Assert.True(!r.MaybeUnbound && r.Definitions.Count == 1 && r.Definitions[0].Kind == DeclKind.Parameter, n);
        }
    }

    [Fact]
    public void UseBeforeAssignmentIsDefinitelyUnbound()
    {
        const string src = "def f():\n    print(x)\n    x = 1\n";
        var r = Reach(Load(src), "x");
        Assert.True(r.MaybeUnbound && r.Definitions.Count == 0);
        var diags = Load(src).Flow.Analyse();
        Assert.True(diags.Any(d => d.Message == "Local variable 'x' referenced before assignment"));
    }

    [Fact]
    public void AugmentedAssignmentReadsThenWrites()
    {
        const string src = "def f():\n    x = 1\n    x += 1\n    return x\n";
        var ctx = Load(src);
        Assert.Equal("2", Lines(src, Reach(ctx, "x", 0)));
        Assert.Equal("3", Lines(src, Reach(ctx, "x", 1)));
    }

    [Fact]
    public void CodeAfterReturnIsUnreachable()
    {
        var d = Load("def f():\n    return 1\n    x = 2\n    y = 3\n").Flow.Analyse();
        Assert.Equal(1, d.Count(x => x.Message == "Unreachable code")); // one report per run, not per statement
    }

    [Fact]
    public void WhileTrueOnlyExitsThroughBreak()
    {
        var noBreak = Load("def f():\n    while True:\n        pass\n    x = 1\n").Flow.Analyse();
        Assert.True(noBreak.Any(d => d.Message == "Unreachable code"));
        var withBreak = Load("def f():\n    while True:\n        break\n    x = 1\n").Flow.Analyse();
        Assert.True(!withBreak.Any(d => d.Message == "Unreachable code"));
    }

    [Fact]
    public void FinallyAfterRaiseOrReturnIsStillReachable()
    {
        Assert.Equal(0, Load("def f():\n    try:\n        raise E\n    finally:\n        cleanup()\n").Flow.Analyse().Count);
        Assert.Equal(0, Load("def f():\n    try:\n        return g()\n    finally:\n        cleanup()\n").Flow.Analyse().Count);
    }

    [Fact]
    public void ConstantFalseBranchIsUnreachable()
    {
        var d = Load("def f():\n    if False:\n        x = 1\n    y = 2\n").Flow.Analyse();
        Assert.Equal(1, d.Count(x => x.Message == "Unreachable code"));
    }

    [Fact]
    public void MatchWithWildcardCoversAllPaths()
    {
        const string withWild = "def f(v):\n    match v:\n        case 1:\n            x = 1\n        case _:\n            x = 2\n    return x\n";
        Assert.True(!Reach(Load(withWild), "x").MaybeUnbound);
        const string noWild = "def f(v):\n    match v:\n        case 1:\n            x = 1\n        case 2:\n            x = 2\n    return x\n";
        Assert.True(Reach(Load(noWild), "x").MaybeUnbound);
    }

    [Fact]
    public void ModuleLevelFlowWorks()
    {
        const string src = "x = 1\nif cond:\n    x = 2\nprint(x)\n";
        var r = Reach(Load(src), "x");
        Assert.Equal("1,3", Lines(src, r));
    }

    [Fact]
    public void OuterAndBuiltinNamesAreNotAnalysedLocally()
    {
        var ctx = Load("g = 1\ndef f():\n    return g, len\n");
        Assert.True(ctx.Flow.ReachingDefinitions(Read(ctx, "g")) == null);
        Assert.True(ctx.Flow.ReachingDefinitions(Read(ctx, "len")) == null);
    }

    [Fact]
    public void ComprehensionVariablesFlow()
    {
        var ctx = Load("def f(xs):\n    return [i * 2 for i in xs if i]\n");
        var r = Reach(ctx, "i", 0);
        Assert.True(!r.MaybeUnbound && r.Definitions.Count == 1);
    }

    [Fact]
    public void ReturnAnalysis()
    {
        var both = Load("def f(c):\n    if c:\n        return 1\n    else:\n        return 2\n");
        var f = both.Sem.ModuleScope.Children[0];
        Assert.True(!both.Flow.For(f)!.Graph.EndReachable);
        Assert.Equal(2, both.Flow.For(f)!.Graph.Returns.Count);

        var some = Load("def f(c):\n    if c:\n        return 1\n");
        Assert.True(some.Flow.For(some.Sem.ModuleScope.Children[0])!.Graph.EndReachable);
    }

    [Fact]
    public void ShortCircuitMakesRightOperandOptional()
    {
        const string src = "def f(a):\n    a and (y := 1)\n    return y\n";
        var r = Reach(Load(src), "y");
        Assert.True(r.MaybeUnbound && r.Definitions.Count == 1);
    }

    [Fact]
    public void ConditionsKnowWhichOperandsRanInEachBranch()
    {
        // body of "a and (y := ..)" runs only when both operands ran
        var r = Reach(Load("def f(a):\n    if a and (y := g()):\n        return y\n"), "y");
        Assert.True(!r.MaybeUnbound && r.Definitions.Count == 1);
        // else-branch of the same condition may not have bound y
        var e = Reach(Load("def f(a):\n    if a and (y := g()):\n        pass\n    else:\n        return y\n"), "y");
        Assert.True(e.MaybeUnbound);
        // "or": the body may be entered without the right operand having run
        var o = Reach(Load("def f(a):\n    if a or (y := g()):\n        return y\n"), "y");
        Assert.True(o.MaybeUnbound);
        // "not" swaps the branches
        var n = Reach(Load("def f(a):\n    if not (a and (y := g())):\n        pass\n    else:\n        return y\n"), "y");
        Assert.True(!n.MaybeUnbound);
        // while-condition walrus is bound in the body
        var w = Reach(Load("def f(it):\n    while (line := it()):\n        use(line)\n"), "line");
        Assert.True(!w.MaybeUnbound);
    }

    [Fact]
    public void TernaryAssignsOnBothBranches()
    {
        const string src = "def f(a):\n    (y := 1) if a else (y := 2)\n    return y\n";
        var r = Reach(Load(src), "y");
        Assert.True(!r.MaybeUnbound && r.Definitions.Count == 2);
    }

    [Fact]
    public void NestedTryFinallyStaysSmall()
    {
        // each finally is built twice; make sure deep nesting is still fast and correct
        var sb = new System.Text.StringBuilder("def f():\n");
        for (int i = 0; i < 8; i++) sb.Append(new string(' ', 4 + i * 4)).Append("try:\n");
        sb.Append(new string(' ', 4 + 8 * 4)).Append("x = 1\n");
        for (int i = 7; i >= 0; i--) sb.Append(new string(' ', 4 + i * 4)).Append("finally:\n").Append(new string(' ', 8 + i * 4)).Append("g()\n");
        sb.Append("    return x\n");
        var r = Reach(Load(sb.ToString()), "x");
        Assert.True(!r.MaybeUnbound);
    }
}
