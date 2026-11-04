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
    /// <summary>The loops around the code being built, with how many try/finally blocks were open where each starts.</summary>
    readonly Stack<(Instruction Head, List<Instruction> Breaks, int Finallies)> _loops = new();
    /// <summary>The try statements with a finally block that the code being built is in: the exits that leave them run it first.</summary>
    readonly List<PendingExits> _finallies = new();
    List<Instruction> _cur = new();

    /// <summary>Where control was going when it left a try body (or its handlers or else block) that has a finally block.</summary>
    sealed class PendingExits
    {
        public readonly List<Instruction> Returns = new(), Breaks = new(), Continues = new();
    }

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
        var end = _cur;
        var exit = Emit(InstrKind.Exit);
        foreach (var r in _returnInstrs) Link(r, exit);
        return new ControlFlowGraph(entry, exit, _instrs, _returns, end);
    }

    // ------------------------------------------------------------ emission helpers

    Instruction Emit(InstrKind kind, Node? element = null, Symbol? sym = null, Declaration? decl = null, string path = "", bool positive = false)
    {
        var i = new Instruction(_instrs.Count, kind, element, sym, decl, path, positive);
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

    /// <summary>
    /// A reference expression: a name followed by attribute accesses (x, self.a, self.a.b). Returns the root name
    /// and the dotted path below it ("" for the name itself).
    /// </summary>
    internal static bool TryChain(Expr e, out NameExpr root, out string path)
    {
        var parts = new List<string>();
        while (true)
        {
            if (e is AttributeExpr a) { parts.Add(a.Attr); e = a.Value; }
            // d["k"], x[0], x[-1]: an item with a constant key is a reference too (`if isinstance(d["k"], list): len(d["k"])`)
            else if (e is SubscriptExpr s && ConstKey(s.Index) is { } key) { parts.Add(key); e = s.Value; }
            else break;
        }
        if (e is NameExpr n)
        {
            root = n;
            parts.Reverse();
            path = string.Join(".", parts);
            return true;
        }
        root = null!; path = "";
        return false;
    }

    /// <summary>
    /// A constant subscript as a part of a member path: `["k"]` for a plain string (spelled with either quote), `[0]` and `[-1]` for an int;
    /// null for anything else (a name, an f-string, escapes), whose value the flow cannot follow.
    /// </summary>
    static string? ConstKey(Expr index) => index switch
    {
        ConstExpr { Kind: ConstKind.Str, IsFString: false, Text: var t } when PlainString(t) is { } s => "[\"" + s + "\"]",
        ConstExpr { Kind: ConstKind.Int, Text: var t } => "[" + t + "]",
        UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int, Text: var t } } => "[-" + t + "]",
        _ => null,
    };

    /// <summary>The value of a string literal in one pair of quotes without prefix or escapes ('k', "k"); null otherwise.</summary>
    static string? PlainString(string text)
    {
        if (text.Length < 2 || text[0] is not ('\'' or '"') || text[^1] != text[0] || text.StartsWith("'''") || text.StartsWith("\"\"\"")) return null;
        var body = text[1..^1];
        return body.Contains('\\') || body.Contains(text[0]) ? null : body;
    }

    /// <summary>`x` or `(x := value)`: the expression whose name or member path a condition can narrow.</summary>
    static Expr Referent(Expr e) => e is NamedExpr { Target: NameExpr t } ? t : e;

    static bool IsChain(Expr e) => TryChain(Referent(e), out _, out _);

    /// <summary>The argument of `len(arg)`; null for anything else.</summary>
    internal static Expr? LenArgument(Expr e) =>
        e is CallExpr { Func: NameExpr { Name: "len" }, Args.Count: 1 } lc && lc.Args[0] is { Name: null, Stars: 0 } ? lc.Args[0].Value : null;

    /// <summary>The reference a condition narrows, if it has one of the recognised shapes.</summary>
    static bool NarrowedReference(Expr cond, out Expr reference)
    {
        reference = cond;
        switch (cond)
        {
            case NamedExpr { Target: NameExpr t }:                                  // if (m := f()):
                reference = t;
                return true;

            case NameExpr or AttributeExpr:
                return TryChain(cond, out _, out _);

            case CompareExpr { Ops.Count: 1 } c:
            {
                string op = c.Ops[0];
                var left = c.Left;
                var right = c.Comparators[0];
                if (op is "in" or "not in")                                         // x in (1, 2), x not in names
                {
                    if (!IsChain(left)) return false;
                    reference = Referent(left);
                    return true;
                }
                // len(x) == 2, len(x) < 3, 3 > len(x)
                if (op is "==" or "!=" or "<" or "<=" or ">" or ">=" && (LenArgument(left) ?? LenArgument(right)) is { } sized && IsChain(sized))
                {
                    reference = Referent(sized);
                    return true;
                }
                if (op is not ("is" or "is not" or "==" or "!=")) return false;

                // type(x) is C
                if (left is CallExpr { Func: NameExpr { Name: "type" }, Args.Count: 1 } tc && tc.Args[0] is { Name: null, Stars: 0 } && IsChain(tc.Args[0].Value))
                {
                    reference = Referent(tc.Args[0].Value);
                    return true;
                }
                // x[0] == "a", "a" != x[-1]: a union of tuples narrowed by one item; x["type"] == "a": a union of TypedDicts by a key
                if ((left is ConstExpr ? right : left) is SubscriptExpr { Index: ConstExpr { Kind: ConstKind.Int } or ConstExpr { Kind: ConstKind.Str, IsFString: false }
                        or UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } } } item
                    && IsChain(item.Value))
                {
                    reference = Referent(item.Value);
                    return true;
                }
                // x is None, x == C.MEMBER, (m := f()) is not None
                if (IsChain(left)) { reference = Referent(left); return true; }
                // None is not x, "a" == x
                if (left is ConstExpr && IsChain(right)) { reference = Referent(right); return true; }
                return false;
            }

            case CallExpr { Func: NameExpr or AttributeExpr } call
                when call.Args.Count >= 1 && call.Args[0] is { Name: null, Stars: 0 } && IsChain(call.Args[0].Value):
                // isinstance, issubclass, callable, hasattr and user functions that return TypeGuard / TypeIs
                reference = Referent(call.Args[0].Value);
                return true;
        }
        return false;
    }

    /// <summary>Python decides some conditions statically; we only treat plain constants as such.</summary>
    bool? Truth(Expr e) => StaticConditions.Evaluate(e, _model.Environment);

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
            case ExprStmt x:
                VisitExpr(x.Value);
                if (x.Value is CallExpr call) Emit(InstrKind.Call, call);   // the evaluator knows whether it returns
                break;

            case AssignStmt x:
                VisitExpr(x.Value);
                foreach (var t in x.Targets) VisitTarget(t);
                break;

            case AugAssignStmt x:
                if (x.Target is NameExpr) EmitRead((NameExpr)x.Target); else VisitExpr(x.Target);
                VisitExpr(x.Value);
                if (x.Target is NameExpr) EmitWrite((NameExpr)x.Target);
                else EmitMemberWrite(x.Target, InstrKind.Write);
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
                LeaveByReturn(new List<Instruction> { r });
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
                LeaveByBreak(_cur);
                _cur = new();
                break;
            case ContinueStmt:
                LeaveByContinue(_cur);
                _cur = new();
                break;

            case IfStmt x: VisitIf(x); break;
            case WhileStmt x: VisitWhile(x); break;
            case ForStmt x: VisitFor(x); break;
            case TryStmt x: VisitTry(x); break;

            case WithStmt x:
            {
                foreach (var i in x.Items)
                {
                    VisitExpr(i.Context);
                    if (i.Target != null) VisitTarget(i.Target);
                }
                int start = _instrs.Count;
                VisitStmts(x.Body);
                // An exception raised anywhere in the body ends the block here; whether the code after it runs then
                // depends on the context managers (WithExit lets nothing through until the evaluator says one swallows it).
                var raisePoints = new List<Instruction>();
                for (int i = start; i < _instrs.Count; i++)
                    if (_instrs[i].Kind != InstrKind.Return) raisePoints.Add(_instrs[i]);
                if (raisePoints.Count > 0)
                {
                    var bodyEnd = _cur;
                    _cur = raisePoints;
                    var swallowed = Emit(InstrKind.WithExit, x);
                    _cur = Union(bodyEnd, new List<Instruction> { swallowed });
                }
                break;
            }

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
                var t = truth == false ? new() : c;
                var f = truth == true ? new() : c;

                // narrowing: after the test, the reference has a known property on each branch
                if (truth == null && c.Count > 0 && NarrowedReference(e, out var reference)
                    && TryChain(reference, out var root, out var path))
                {
                    var sym = _model.ReferenceOf(root)?.Symbol;
                    if (sym != null)
                    {
                        _cur = c; var onTrue = Emit(InstrKind.Assert, e, sym, null, path, true);
                        _cur = c; var onFalse = Emit(InstrKind.Assert, e, sym, null, path, false);
                        return (new List<Instruction> { onTrue }, new List<Instruction> { onFalse });
                    }
                }
                return (t, f);
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
        _loops.Push((head, breaks, _finallies.Count));
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
        _loops.Push((head, breaks, _finallies.Count));
        VisitTarget(x.Target);
        VisitStmts(x.Body);
        foreach (var p in _cur) Link(p, head);
        _loops.Pop();

        _cur = new List<Instruction> { head }; // iterator exhausted
        VisitStmts(x.Orelse);
        _cur = Union(_cur, breaks);
    }

    /// <summary>A return leaves the function, through the finally blocks of the try statements it is in.</summary>
    void LeaveByReturn(List<Instruction> from)
    {
        if (_finallies.Count > 0) _finallies[^1].Returns.AddRange(from);
        else _returnInstrs.AddRange(from);
    }

    void LeaveByBreak(List<Instruction> from)
    {
        if (_loops.Count == 0) return;
        var loop = _loops.Peek();
        if (_finallies.Count > loop.Finallies) _finallies[^1].Breaks.AddRange(from);
        else loop.Breaks.AddRange(from);
    }

    void LeaveByContinue(List<Instruction> from)
    {
        if (_loops.Count == 0) return;
        var loop = _loops.Peek();
        if (_finallies.Count > loop.Finallies) _finallies[^1].Continues.AddRange(from);
        else foreach (var p in from) Link(p, loop.Head);
    }

    void VisitTry(TryStmt x)
    {
        var pre = _cur;
        int start = _instrs.Count;
        PendingExits? exits = null;
        if (x.Finally.Count > 0) _finallies.Add(exits = new PendingExits());
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

        if (exits == null) { _cur = normalEnd; return; }
        _finallies.RemoveAt(_finallies.Count - 1);

        // The finally block runs on the normal path and again on the path where an exception propagates.
        // Building it twice keeps the exceptional path from "falling through" to the code after the try.
        var excSources = new List<Instruction>(pre);
        for (int i = start; i < _instrs.Count; i++) excSources.Add(_instrs[i]);

        _cur = normalEnd;
        VisitStmts(x.Finally);
        var afterNormal = _cur;

        _cur = excSources;
        VisitStmts(x.Finally);

        // A return, break or continue that left the try runs the finally block on its way out too (one more copy
        // for each), then goes on where it was going, unless the finally block itself returns, breaks or raises.
        if (exits.Returns.Count > 0) { _cur = exits.Returns; VisitStmts(x.Finally); LeaveByReturn(_cur); }
        if (exits.Breaks.Count > 0) { _cur = exits.Breaks; VisitStmts(x.Finally); LeaveByBreak(_cur); }
        if (exits.Continues.Count > 0) { _cur = exits.Continues; VisitStmts(x.Finally); LeaveByContinue(_cur); }

        _cur = afterNormal;
    }

    void VisitMatch(MatchStmt x)
    {
        VisitExpr(x.Subject);
        var fail = _cur;
        var ends = new List<Instruction>();

        // a subject that is a name or a member path is narrowed by the patterns that match it, and by those that did not
        Symbol? subject = null;
        string subjectPath = "";
        if (TryChain(Referent(x.Subject), out var subjectRoot, out subjectPath)) subject = _model.ReferenceOf(subjectRoot)?.Symbol;

        foreach (var c in x.Cases)
        {
            _cur = fail;
            var beforePattern = fail;
            VisitPattern(c.Pattern);
            bool refutable = !IsIrrefutable(c.Pattern);
            bool narrows = subject != null && NarrowsSubject(c.Pattern);
            List<Instruction> nextFail = refutable ? fail : new();
            if (narrows)
            {
                Emit(InstrKind.Assert, c.Pattern, subject, null, subjectPath, true);
                var matched = _cur;
                if (refutable)
                {
                    _cur = beforePattern;
                    nextFail = new List<Instruction> { Emit(InstrKind.Assert, c.Pattern, subject, null, subjectPath, false) };
                }
                _cur = matched;
            }
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

    /// <summary>Patterns that say something about the type of the subject.</summary>
    static bool NarrowsSubject(Pattern p) => p switch
    {
        MatchClass => true,
        MatchSequence => true,
        MatchMapping => true,
        MatchValue => true,
        MatchOr o => o.Alternatives.All(NarrowsSubject),
        MatchAs a => NarrowsSubject(a.Pattern),
        _ => false,
    };

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
            default:
                VisitTargetSubexpressions(t);
                EmitMemberWrite(t, InstrKind.Write);
                break;
        }
    }

    /// <summary>Assigning to / deleting self.a.b invalidates what was learned about self.a.b (and below).</summary>
    void EmitMemberWrite(Expr target, InstrKind kind)
    {
        if (target is AttributeExpr or SubscriptExpr && TryChain(target, out var root, out var path) && path.Length > 0)
        {
            var sym = _model.ReferenceOf(root)?.Symbol;
            if (sym != null) Emit(kind, target, sym, null, path);
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
            default: VisitTargetSubexpressions(t); EmitMemberWrite(t, InstrKind.Delete); break;
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
                Emit(InstrKind.Join, c);   // where the comprehension starts: its body sees what is known here
                return;

            case NamedExpr w:
                VisitExpr(w.Value);
                VisitTarget(w.Target);
                return;

            case BinaryExpr { Op: "and" or "or" } b:
            {
                // the right operand runs only when the left one was true (and) / false (or); that carries narrowing
                var (lt, lf) = VisitCond(b.Left);
                bool isAnd = b.Op == "and";
                _cur = isAnd ? lt : lf;
                VisitExpr(b.Right);
                _cur = Union(isAnd ? lf : lt, _cur);
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
            foreach (var cond in g.Ifs)
            {
                var (t, _) = VisitCond(cond);
                _cur = t; // later parts of the comprehension only see items that passed the condition
            }
        }
        VisitExpr(c.Element);
        VisitExpr(c.Value);
    }
}
