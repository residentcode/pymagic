using PyMagic.Core.Syntax;

namespace PyMagic.Tests;

public class ParserTests
{
    static Module Ok(string src)
    {
        var r = Parser.Parse(src);
        Assert.True(r.Diagnostics.Count == 0, "unexpected diagnostics: " + string.Join("; ", r.Diagnostics.Select(d => $"{d.Start}:{d.Message}")));
        return r.Module;
    }

    static Expr E(string src) => ((ExprStmt)Ok(src).Body[0]).Value;

    /// <summary>Compact S-expression rendering for asserting tree shape.</summary>
    static string S(Node? n) => n switch
    {
        null => "_",
        NameExpr x => x.Name,
        ConstExpr c => c.Text,
        UnaryExpr u => $"({u.Op} {S(u.Operand)})",
        BinaryExpr b => $"({b.Op} {S(b.Left)} {S(b.Right)})",
        CompareExpr c => $"(cmp {S(c.Left)} {string.Join(" ", c.Ops.Zip(c.Comparators, (o, e) => o + " " + S(e)))})",
        CallExpr c => $"(call {S(c.Func)}{string.Concat(c.Args.Select(a => " " + new string('*', a.Stars) + (a.Name != null ? a.Name + "=" : "") + S(a.Value)))})",
        AttributeExpr a => $"(. {S(a.Value)} {a.Attr})",
        SubscriptExpr s => $"(idx {S(s.Value)} {S(s.Index)})",
        SliceExpr s => $"(slice {S(s.Lower)} {S(s.Upper)} {S(s.Step)})",
        TupleExpr t => $"(tuple{string.Concat(t.Items.Select(i => " " + S(i)))})",
        ListExpr l => $"(list{string.Concat(l.Items.Select(i => " " + S(i)))})",
        SetExpr l => $"(set{string.Concat(l.Items.Select(i => " " + S(i)))})",
        DictExpr d => $"(dict{string.Concat(d.Keys.Zip(d.Values, (k, v) => " " + (k == null ? "**" : S(k) + ":") + S(v)))})",
        CompExpr c => $"({c.Kind}comp {S(c.Element)}{(c.Value != null ? ":" + S(c.Value) : "")} {string.Join(" ", c.Generators.Select(g => $"for {S(g.Target)} in {S(g.Iter)}{string.Concat(g.Ifs.Select(i => " if " + S(i)))}"))})",
        LambdaExpr l => $"(lambda [{string.Join(",", l.Params.Select(p => p.Name))}] {S(l.Body)})",
        IfExpr i => $"(ifexp {S(i.Test)} {S(i.Body)} {S(i.Orelse)})",
        StarExpr s => $"(*{S(s.Value)})",
        AwaitExpr a => $"(await {S(a.Value)})",
        YieldExpr y => $"(yield{(y.IsFrom ? "-from" : "")} {S(y.Value)})",
        NamedExpr w => $"(:= {S(w.Target)} {S(w.Value)})",
        _ => n.GetType().Name,
    };

    [Fact]
    [Case("1 + 2 * 3", "(+ 1 (* 2 3))")]
    [Case("(1 + 2) * 3", "(* (+ 1 2) 3)")]
    [Case("-a ** 2", "(- (** a 2))")]
    [Case("a ** -b ** c", "(** a (- (** b c)))")]
    [Case("not a and b or c", "(or (and (not a) b) c)")]
    [Case("a < b <= c", "(cmp a < b <= c)")]
    [Case("a not in b", "(cmp a not in b)")]
    [Case("a is not None", "(cmp a is not None)")]
    [Case("a | b ^ c & d", "(| a (^ b (& c d)))")]
    [Case("a << 1 + 2", "(<< a (+ 1 2))")]
    [Case("x if c else y", "(ifexp c x y)")]
    [Case("f(a, *b, k=1, **kw)", "(call f a *b k=1 **kw)")]
    [Case("a.b.c(1)[2]", "(idx (call (. (. a b) c) 1) 2)")]
    [Case("a[1:2, ::3]", "(idx a (tuple (slice 1 2 _) (slice _ _ 3)))")]
    [Case("a[:]", "(idx a (slice _ _ _))")]
    [Case("lambda x, y=1: x + y", "(lambda [x,y] (+ x y))")]
    [Case("(1,)", "(tuple 1)")]
    [Case("()", "(tuple)")]
    [Case("{1: 2, **d}", "(dict 1:2 **d)")]
    [Case("{1, 2}", "(set 1 2)")]
    [Case("{}", "(dict)")]
    [Case("[x for x in y if x]", "(Listcomp x for x in y if x)")]
    [Case("{k: v for k, v in d}", "(Dictcomp k:v for (tuple k v) in d)")]
    [Case("(x for x in y)", "(Generatorcomp x for x in y)")]
    [Case("f(x for x in y)", "(call f (Generatorcomp x for x in y))")]
    [Case("await x", "(await x)")]
    [Case("(y := 1)", "(:= y 1)")]
    [Case("[*a, b]", "(list (*a) b)")]
    [Case("'a' 'b'", "'a' 'b'")]
    public void Expressions(string src, string expected) => Assert.Equal(expected, S(E(src)));

    [Fact]
    public void Assignments()
    {
        var m = Ok("a = b = 1\nx += 2\ny: int = 3\nz: str\nq, *r = t\n");
        var a = (AssignStmt)m.Body[0];
        Assert.Equal(2, a.Targets.Count);
        Assert.Equal("+=", ((AugAssignStmt)m.Body[1]).Op);
        Assert.True(((AnnAssignStmt)m.Body[2]).Value != null);
        Assert.True(((AnnAssignStmt)m.Body[3]).Value == null);
        Assert.Equal("(tuple q (*r))", S(((AssignStmt)m.Body[4]).Targets[0]));
    }

    [Fact]
    public void FunctionDefinition()
    {
        var m = Ok("@dec\nasync def f[T](a, /, b: int = 1, *args, c, **kw) -> T:\n    return a\n");
        var f = (FunctionDef)m.Body[0];
        Assert.True(f.IsAsync);
        Assert.Equal(1, f.Decorators.Count);
        Assert.Equal(1, f.TypeParams.Count);
        Assert.Equal("a,,b,args,c,kw", string.Join(",", f.Params.Select(p => p.Name)));
        Assert.True(f.Params[1].Kind == ParamKind.Slash);
        Assert.True(f.Returns != null);
        Assert.Equal(1, f.Body.Count);
    }

    [Fact]
    public void KeywordOnlySeparator()
    {
        var f = (FunctionDef)Ok("def f(a, *, b): pass\n").Body[0];
        Assert.True(f.Params[1].Kind == ParamKind.KwOnlySeparator);
    }

    [Fact]
    public void ClassDefinition()
    {
        var c = (ClassDef)Ok("class A(B, metaclass=M):\n    x = 1\n    def m(self): ...\n").Body[0];
        Assert.Equal(2, c.Bases.Count);
        Assert.Equal("metaclass", c.Bases[1].Name);
        Assert.Equal(2, c.Body.Count);
    }

    [Fact]
    public void ControlFlowStatements()
    {
        var m = Ok("if a:\n    pass\nelif b:\n    pass\nelse:\n    pass\nwhile x:\n    break\nelse:\n    pass\nfor i, j in z:\n    continue\n");
        var i = (IfStmt)m.Body[0];
        Assert.True(i.Orelse[0] is IfStmt { Orelse.Count: 1 });
        Assert.True(m.Body[1] is WhileStmt { Orelse.Count: 1 });
        Assert.True(m.Body[2] is ForStmt { Target: TupleExpr });
    }

    [Fact]
    public void TryStatement()
    {
        var t = (TryStmt)Ok("try:\n    a\nexcept (A, B) as e:\n    b\nexcept:\n    c\nelse:\n    d\nfinally:\n    e\n").Body[0];
        Assert.Equal(2, t.Handlers.Count);
        Assert.Equal("e", t.Handlers[0].Name);
        Assert.Equal(1, t.Orelse.Count);
        Assert.Equal(1, t.Finally.Count);
    }

    [Fact]
    public void WithStatements()
    {
        var w = (WithStmt)Ok("with open(a) as f, open(b) as g:\n    pass\n").Body[0];
        Assert.Equal(2, w.Items.Count);
        var w2 = (WithStmt)Ok("with (open(a) as f, open(b) as g):\n    pass\n").Body[0];
        Assert.Equal(2, w2.Items.Count);
        var w3 = (WithStmt)Ok("with (yield_ctx()):\n    pass\n").Body[0];
        Assert.Equal(1, w3.Items.Count);
    }

    [Fact]
    public void Imports()
    {
        var m = Ok("import a.b as c, d\nfrom . import x\nfrom ..p.q import (r as s, t,)\nfrom m import *\n");
        Assert.Equal("a.b", ((ImportStmt)m.Body[0]).Names[0].Name);
        var f1 = (ImportFromStmt)m.Body[1];
        Assert.True(f1.Level == 1 && f1.Module == null);
        var f2 = (ImportFromStmt)m.Body[2];
        Assert.True(f2.Level == 2 && f2.Module == "p.q" && f2.Names.Count == 2);
        Assert.True(((ImportFromStmt)m.Body[3]).IsStar);
    }

    [Fact]
    public void SimpleStatementsOnOneLine()
    {
        var m = Ok("a = 1; b = 2; pass\nif x: return 1\n");
        Assert.Equal(4, m.Body.Count);
        Assert.True(m.Body[3] is IfStmt);
    }

    [Fact]
    public void GlobalDelAssertRaise()
    {
        var m = Ok("global a, b\ndel x, y[0]\nassert c, 'm'\nraise E from F\n");
        Assert.Equal(2, ((GlobalStmt)m.Body[0]).Names.Count);
        Assert.Equal(2, ((DelStmt)m.Body[1]).Targets.Count);
        Assert.True(((AssertStmt)m.Body[2]).Message != null);
        Assert.True(((RaiseStmt)m.Body[3]).Cause != null);
    }

    [Fact]
    public void MatchStatement()
    {
        var m = (MatchStmt)Ok("match p:\n    case [1, x, *rest]:\n        pass\n    case {'k': v, **kw}:\n        pass\n    case Point(x=0, y=yy) | Other():\n        pass\n    case str() as s if s:\n        pass\n    case _:\n        pass\n").Body[0];
        Assert.Equal(5, m.Cases.Count);
        Assert.True(m.Cases[0].Pattern is MatchSequence { Items.Count: 3 });
        Assert.True(m.Cases[1].Pattern is MatchMapping { Rest: "kw" });
        Assert.True(m.Cases[2].Pattern is MatchOr);
        Assert.True(m.Cases[3].Pattern is MatchAs && m.Cases[3].Guard != null);
        Assert.True(m.Cases[4].Pattern is MatchCapture { Name: null });
    }

    [Fact]
    public void MatchAsIdentifier()
    {
        var m = Ok("match = 1\nmatch(x)\nprint(match.group(1))\ncase = 2\n");
        Assert.True(m.Body[0] is AssignStmt);
        Assert.True(m.Body[1] is ExprStmt);
    }

    [Fact]
    public void TypeAliasAndSoftKeywordType()
    {
        var m = Ok("type X[T] = list[T]\ntype = 5\ntype(x)\n");
        Assert.True(m.Body[0] is TypeAliasStmt);
        Assert.True(m.Body[1] is AssignStmt);
        Assert.True(m.Body[2] is ExprStmt);
    }

    [Fact]
    public void ErrorRecoveryContinuesWithNextStatement()
    {
        var r = Parser.Parse("x = 1 +\ny = 2\nz = 3\n");
        Assert.True(r.Diagnostics.Count >= 1, "expected a diagnostic");
        Assert.True(r.Module.Body.OfType<AssignStmt>().Any(), "later statements should survive");

        var r2 = Parser.Parse("def f()\n    pass\nx = 1\n");
        Assert.True(r2.Diagnostics.Count >= 1);
        Assert.True(r2.Module.Body.OfType<AssignStmt>().Any(s => s.Targets[0] is NameExpr { Name: "x" }));
    }

    [Fact]
    public void MissingBlockIsReported()
    {
        var r = Parser.Parse("if x:\ny = 1\n");
        Assert.True(r.Diagnostics.Count >= 1);
    }

    [Fact]
    public void NodeRanges()
    {
        const string src = "foo(1, bar)\n";
        var call = (CallExpr)((ExprStmt)Parser.Parse(src).Module.Body[0]).Value;
        Assert.Equal(0, call.Start);
        Assert.Equal(11, call.End);
        Assert.Equal(4, call.Args[0].Start);
    }

    [Fact]
    public void ParsesRealisticModule()
    {
        const string src = """"
            from __future__ import annotations
            import os, sys
            from typing import Generic, TypeVar

            T = TypeVar("T")

            class Box(Generic[T]):
                """Docstring"""
                def __init__(self, item: T) -> None:
                    self.item = item

                @property
                def value(self) -> T:
                    return self.item

                async def fetch(self, *urls: str, timeout: float = 1.5, **opts):
                    async with session() as s:
                        async for chunk in s.get(urls[0]):
                            yield chunk
                    try:
                        await s.close()
                    except (OSError, ValueError) as e:
                        raise RuntimeError("x") from e
                    finally:
                        print(f"done {self!r}")

            def main():
                data = {k: [v for v in range(10) if v % 2] for k in "abc"}
                total = sum(x * 2 for x in data["a"])
                if (n := len(data)) > 2 and not total:
                    print(n, total, sep=", ")
                return lambda *a, **k: (a, k)

            if __name__ == "__main__":
                main()
            """";
        var m = Ok(src);
        Assert.True(m.Body.Count == 7, "body count " + m.Body.Count);
    }
}
