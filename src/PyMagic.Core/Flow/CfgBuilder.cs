using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Flow;

/// <summary>
/// Builds the control-flow graph of one scope by walking its code in evaluation order. The "frontier" is the
/// set of instructions that flow into whatever is emitted next; an empty frontier means the code is unreachable.
/// Nested functions/classes/lambdas/comprehensions contribute only the parts Python evaluates in this scope
/// (decorators, defaults, annotations, base classes, the first comprehension iterable).
/// </summary>
internal sealed class CfgBuilder
{
    readonly SemanticModel _model;
    readonly Scope _scope;
    readonly IReadOnlyDictionary<Node, (Symbol Symbol, Declaration Decl)> _decls;
    readonly List<Instruction> _instrs = new();
    readonly List<ReturnStmt> _returns = new();
    readonly List<Instruction> _returnInstrs = new();
    readonly Stack<(Instruction Head, List<Instruction> Breaks)> _loops = new();
    List<Instruction> _cur = new();

    public CfgBuilder(SemanticModel model, Scope scope, IReadOnlyDictionary<Node, (Symbol, Declaration)> decls)
    {
        _model = model; _scope = scope; _decls = decls!;
    }

    public ControlFlowGraph Build()
    {
        var entry = Emit(InstrKind.Entry);
        switch (_scope.Node)
        {
            case FunctionDef f:
                WriteParameters(f.Params);
                VisitStmts(f.Body);
                break;
            case LambdaExpr l:
                WriteParameters(l.Params);
                VisitExpr(l.Body);
                break;
            case CompExpr c:
                VisitComprehensionBody(c);
                break;
            case ClassDef c:
                VisitStmts(c.Body);
                break;
            case Module m:
                VisitStmts(m.Body);
                break;
        }
        bool endReachable = _cur.Count > 0;
        var exit = Emit(InstrKind.Exit);
        foreach (var r in _returnInstrs) Link(r, exit);
        return new ControlFlowGraph(entry, exit, _instrs, _returns, endReachable);
    }

    // ------------------------------------------------------------ emission helpers

    Instruction Emit(InstrKind kind, Node? element = null, Symbol? sym = null, Declaration? decl = null)
    {
        var i = new Instruction(_instrs.Count, kind, element, sym, decl);
        _instrs.Add(i);
        foreach (var p in _cur) Link(p, i);
        _cur = new List<Instruction> { i };
        return i;
    }

    static void Link(Instruction from, Instruction to)
    {
        if (from.Successors.Contains(to)) return;
        from.Successors.Add(to);
        to.Predecessors.Add(from);
    }

    static List<Instruction> Union(List<Instruction> a, List<Instruction> b)
    {
        var r = new List<Instruction>(a);
        foreach (var x in b) if (!r.Contains(x)) r.Add(x);
        return r;
    }

    void Mark(Stmt s) => Emit(InstrKind.Statement, s);

    void WriteBound(Node declNode)
    {
        if (_decls.TryGetValue(declNode, out var d)) Emit(InstrKind.Write, declNode, d.Symbol, d.Decl);
    }

    void WriteParameters(IReadOnlyList<Parameter> ps)
    {
        foreach (var p in ps)
            if (p.Kind is not (ParamKind.Slash or ParamKind.KwOnlySeparator)) WriteBound(p);
    }

    /// <summary>Python decides some conditions statically; we only treat plain constants as such.</summary>
    static bool? Truth(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.True } => true,
        ConstExpr { Kind: ConstKind.False or ConstKind.None } => false,
        ConstExpr { Kind: ConstKind.Int } c => !c.Text.All(ch => ch == '0' || ch == '_'),
        UnaryExpr { Op: "not" } u => Truth(u.Operand) is bool b ? !b : null,
        _ => null,
    };

    // ------------------------------------------------------------ statements

    void VisitStmts(IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts) VisitStmt(s);
    }

    void VisitStmt(Stmt st)
    {
        Mark(st);
        switch (st)
        {
            case ExprStmt x: VisitExpr(x.Value); break;

            case AssignStmt x:
                VisitExpr(x.Value);
                foreach (var t in x.Targets) VisitTarget(t);
                break;

            case AugAssignStmt x:
                if (x.Target is NameExpr) EmitRead((NameExpr)x.Target); else VisitExpr(x.Target);
                VisitExpr(x.Value);
                if (x.Target is NameExpr) EmitWrite((NameExpr)x.Target);
                break;

            case AnnAssignStmt x:
                VisitExpr(x.Annotation);
                if (x.Value != null) { VisitExpr(x.Value); VisitTarget(x.Target); }
                else if (x.Target is not NameExpr) VisitTargetSubexpressions(x.Target);
                break;

            case ReturnStmt x:
            {
                VisitExpr(x.Value);
                var r = Emit(InstrKind.Return, x);
                _returns.Add(x);
                _returnInstrs.Add(r);
                _cur = new();
                break;
            }

            case RaiseStmt x:
                VisitExpr(x.Exc); VisitExpr(x.Cause);
                Emit(InstrKind.Raise, x);
                _cur = new();
                break;

            case AssertStmt x:
            {
                var (t, f) = VisitCond(x.Test);
                _cur = f;
                VisitExpr(x.Message); // only evaluated when the assertion fails, and then control leaves
                _cur = t;
                break;
            }

            case DelStmt x:
                foreach (var t in x.Targets) VisitDelete(t);
                break;

            case ImportStmt x: foreach (var a in x.Names) WriteBound(a); break;
            case ImportFromStmt x: foreach (var a in x.Names) WriteBound(a); break;

            case BreakStmt:
                if (_loops.Count > 0) _loops.Peek().Breaks.AddRange(_cur);
                _cur = new();
                break;
            case ContinueStmt:
                if (_loops.Count > 0) foreach (var p in _cur) Link(p, _loops.Peek().Head);
                _cur = new();
                break;

            case IfStmt x: VisitIf(x); break;
            case WhileStmt x: VisitWhile(x); break;
            case ForStmt x: VisitFor(x); break;
            case TryStmt x: VisitTry(x); break;

            case WithStmt x:
                foreach (var i in x.Items)
                {
                    VisitExpr(i.Context);
                    if (i.Target != null) VisitTarget(i.Target);
                }
                VisitStmts(x.Body);
                break;

            case FunctionDef x:
                foreach (var d in x.Decorators) VisitExpr(d);
                foreach (var p in x.Params) { VisitExpr(p.Default); VisitExpr(p.Annotation); }
                VisitExpr(x.Returns);
                WriteBound(x);
                break;

            case ClassDef x:
                foreach (var d in x.Decorators) VisitExpr(d);
                foreach (var b in x.Bases) VisitExpr(b.Value);
                WriteBound(x);
                break;

            case MatchStmt x: VisitMatch(x); break;
            case TypeAliasStmt x: WriteBound(x); break;
        }
    }

    /// <summary>
    /// Evaluates a condition and returns the frontiers for "was true" and "was false". Splitting them lets
    /// `if a and (x := f()):` know that x is bound in the body, and lets constant conditions kill a branch.
    /// </summary>
    (List<Instruction> True, List<Instruction> False) VisitCond(Expr e)
    {
        switch (e)
        {
            case BinaryExpr { Op: "and" } b:
            {
                var (lt, lf) = VisitCond(b.Left);
                _cur = lt;
                var (rt, rf) = VisitCond(b.Right);
                return (rt, Union(lf, rf));
            }
            case BinaryExpr { Op: "or" } b:
            {
                var (lt, lf) = VisitCond(b.Left);
                _cur = lf;
                var (rt, rf) = VisitCond(b.Right);
                return (Union(lt, rt), rf);
            }
            case UnaryExpr { Op: "not" } u:
            {
                var (t, f) = VisitCond(u.Operand);
                return (f, t);
            }
            default:
            {
                VisitExpr(e);
                var truth = Truth(e);
                var c = _cur;
                return (truth == false ? new() : c, truth == true ? new() : c);
            }
        }
    }

    void VisitIf(IfStmt x)
    {
        var (t, f) = VisitCond(x.Test);

        _cur = t;
        VisitStmts(x.Body);
        var thenEnd = _cur;

        _cur = f;
        VisitStmts(x.Orelse);
        _cur = Union(thenEnd, _cur);
    }

    void VisitWhile(WhileStmt x)
    {
        var head = Emit(InstrKind.Join, x);
        var (t, f) = VisitCond(x.Test);

        var breaks = new List<Instruction>();
        _loops.Push((head, breaks));
        _cur = t;
        VisitStmts(x.Body);
        foreach (var p in _cur) Link(p, head);
        _loops.Pop();

        _cur = f;
        VisitStmts(x.Orelse);
        _cur = Union(_cur, breaks);
    }

    void VisitFor(ForStmt x)
    {
        VisitExpr(x.Iter);
        var head = Emit(InstrKind.Join, x);

        var breaks = new List<Instruction>();
        _loops.Push((head, breaks));
        VisitTarget(x.Target);
        VisitStmts(x.Body);
        foreach (var p in _cur) Link(p, head);
        _loops.Pop();

        _cur = new List<Instruction> { head }; // iterator exhausted
        VisitStmts(x.Orelse);
        _cur = Union(_cur, breaks);
    }

    void VisitTry(TryStmt x)
    {
        var pre = _cur;
        int start = _instrs.Count;
        VisitStmts(x.Body);
        var bodyEnd = _cur;

        // Any point in the body may raise, so the handlers can be entered from anywhere in it.
        var raisePoints = new List<Instruction>(pre);
        for (int i = start; i < _instrs.Count; i++) raisePoints.Add(_instrs[i]);

        var handlerEnds = new List<Instruction>();
        if (x.Handlers.Count > 0)
        {
            _cur = raisePoints;
            var entry = Emit(InstrKind.Join, x);
            foreach (var h in x.Handlers)
            {
                _cur = new List<Instruction> { entry };
                VisitExpr(h.Type);
                if (h.Name != null) WriteBound(h);
                VisitStmts(h.Body);
                if (h.Name != null && _cur.Count > 0 && _scope.Symbols.TryGetValue(h.Name, out var s))
                    Emit(InstrKind.Delete, h, s.Redirect ?? s); // "except E as e" unbinds e when the block ends
                handlerEnds = Union(handlerEnds, _cur);
            }
        }

        _cur = bodyEnd;
        VisitStmts(x.Orelse);
        var normalEnd = Union(_cur, handlerEnds);

        if (x.Finally.Count == 0) { _cur = normalEnd; return; }

        // The finally block runs on the normal path and again on the path where an exception propagates.
        // Building it twice keeps the exceptional path from "falling through" to the code after the try.
        var excSources = new List<Instruction>(pre);
        for (int i = start; i < _instrs.Count; i++) excSources.Add(_instrs[i]);

        _cur = normalEnd;
        VisitStmts(x.Finally);
        var afterNormal = _cur;

        _cur = excSources;
        VisitStmts(x.Finally);

        _cur = afterNormal;
    }

    void VisitMatch(MatchStmt x)
    {
        VisitExpr(x.Subject);
        var fail = _cur;
        var ends = new List<Instruction>();
        foreach (var c in x.Cases)
        {
            _cur = fail;
            VisitPattern(c.Pattern);
            bool refutable = !IsIrrefutable(c.Pattern);
            List<Instruction> nextFail = refutable ? fail : new();
            if (c.Guard != null)
            {
                var (gt, gf) = VisitCond(c.Guard);
                nextFail = Union(nextFail, gf);
                _cur = gt;
            }
            VisitStmts(c.Body);
            ends = Union(ends, _cur);
            fail = nextFail;
        }
        _cur = Union(ends, fail);
    }

    static bool IsIrrefutable(Pattern p) => p switch
    {
        MatchCapture => true,
        MatchAs a => IsIrrefutable(a.Pattern),
        MatchOr o => o.Alternatives.Any(IsIrrefutable),
        _ => false,
    };

    void VisitPattern(Pattern p)
    {
        switch (p)
        {
            case MatchValue v: VisitExpr(v.Value); break;
            case MatchCapture { Name: not null } c: WriteBound(c); break;
            case MatchStar { Name: not null } s: WriteBound(s); break;
            case MatchAs a: VisitPattern(a.Pattern); WriteBound(a); break;
            case MatchOr o: foreach (var x in o.Alternatives) VisitPattern(x); break;
            case MatchSequence q: foreach (var x in q.Items) VisitPattern(x); break;
            case MatchMapping m:
                foreach (var k in m.Keys) VisitExpr(k);
                foreach (var x in m.Patterns) VisitPattern(x);
                if (m.Rest != null) WriteBound(m);
                break;
            case MatchClass c:
                VisitExpr(c.Cls);
                foreach (var x in c.Positional) VisitPattern(x);
                foreach (var x in c.KeywordPatterns) VisitPattern(x);
                break;
        }
    }

    // ------------------------------------------------------------ expressions and targets

    void EmitRead(NameExpr n) => Emit(InstrKind.Read, n, _model.ReferenceOf(n)?.Symbol);

    void EmitWrite(NameExpr n)
    {
        if (_decls.TryGetValue(n, out var d)) Emit(InstrKind.Write, n, d.Symbol, d.Decl);
        else Emit(InstrKind.Write, n, _model.ReferenceOf(n)?.Symbol);
    }

    void VisitTarget(Expr t)
    {
        switch (t)
        {
            case NameExpr n: EmitWrite(n); break;
            case TupleExpr tu: foreach (var i in tu.Items) VisitTarget(i); break;
            case ListExpr l: foreach (var i in l.Items) VisitTarget(i); break;
            case StarExpr s: VisitTarget(s.Value); break;
            default: VisitTargetSubexpressions(t); break;
        }
    }

    void VisitTargetSubexpressions(Expr t)
    {
        switch (t)
        {
            case AttributeExpr a: VisitExpr(a.Value); break;
            case SubscriptExpr s: VisitExpr(s.Value); VisitExpr(s.Index); break;
            default: VisitExpr(t); break;
        }
    }

    void VisitDelete(Expr t)
    {
        switch (t)
        {
            case NameExpr n: Emit(InstrKind.Delete, n, _model.ReferenceOf(n)?.Symbol); break;
            case TupleExpr tu: foreach (var i in tu.Items) VisitDelete(i); break;
            case ListExpr l: foreach (var i in l.Items) VisitDelete(i); break;
            default: VisitTargetSubexpressions(t); break;
        }
    }

    void VisitExpr(Expr? e)
    {
        switch (e)
        {
            case null: return;
            case NameExpr n: EmitRead(n); return;

            case LambdaExpr l:
                foreach (var p in l.Params) VisitExpr(p.Default);
                return;

            case CompExpr c:
                if (c.Generators.Count > 0) VisitExpr(c.Generators[0].Iter);
                return;

            case NamedExpr w:
                VisitExpr(w.Value);
                VisitTarget(w.Target);
                return;

            case BinaryExpr { Op: "and" or "or" } b:
            {
                VisitExpr(b.Left);
                var afterLeft = _cur;
                VisitExpr(b.Right);
                _cur = Union(afterLeft, _cur); // right operand may be skipped
                return;
            }

            case IfExpr i:
            {
                var (t, f) = VisitCond(i.Test);
                _cur = t;
                VisitExpr(i.Body);
                var thenEnd = _cur;
                _cur = f;
                VisitExpr(i.Orelse);
                _cur = Union(thenEnd, _cur);
                return;
            }
        }
        foreach (var c in AstChildren.Of(e))
        {
            if (c is Expr x) VisitExpr(x);
            else if (c is Argument a) VisitExpr(a.Value);
        }
    }

    /// <summary>Comprehension: first iterable belongs to the outer scope; the rest is evaluated here in order.</summary>
    void VisitComprehensionBody(CompExpr c)
    {
        for (int i = 0; i < c.Generators.Count; i++)
        {
            var g = c.Generators[i];
            if (i > 0) VisitExpr(g.Iter);
            VisitTarget(g.Target);
            foreach (var cond in g.Ifs) VisitExpr(cond);
        }
        VisitExpr(c.Element);
        VisitExpr(c.Value);
    }
}
