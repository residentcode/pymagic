using PyMagic.Core.Syntax;

namespace PyMagic.Core.Semantics;

/// <summary>
/// Builds the scope tree in two steps per scope: first a shallow "declare" pass that finds every binding in the
/// scope (Python decides locality statically, so this must be complete before any use is resolved), then a
/// "visit" pass that creates nested scopes and resolves every name reference.
/// </summary>
internal sealed class Binder
{
    readonly Module _module;
    readonly Dictionary<Node, Scope> _scopes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<NameExpr, Reference> _refs = new(ReferenceEqualityComparer.Instance);
    readonly List<Diagnostic> _diags = new();
    Scope _scope = null!;

    public Binder(Module module) { _module = module; }

    public SemanticModel Run()
    {
        var root = new Scope(ScopeKind.Module, _module, null);
        _scopes[_module] = root;
        _scope = root;
        Setup(root, _module.Body);
        VisitStmts(_module.Body);
        return new SemanticModel(root, _scopes, _refs, _diags);
    }

    void Diag(Node n, string msg) => _diags.Add(new Diagnostic(n.Start, Math.Max(n.End - n.Start, 1), msg));

    // ================================================================ declare pass

    void Setup(Scope scope, IReadOnlyList<Stmt> body)
    {
        foreach (var s in body) CollectGlobalDecls(scope, s);
        foreach (var s in body) DeclareStmt(scope, s);
    }

    void Bind(Scope scope, string name, DeclKind kind, Node node)
    {
        var sym = scope.GetOrAdd(name);
        (sym.Redirect ?? sym).Declarations.Add(new Declaration(name, kind, node));
    }

    void CollectGlobalDecls(Scope scope, Node n)
    {
        switch (n)
        {
            case FunctionDef or ClassDef: return;
            case GlobalStmt g:
                foreach (var name in g.Names)
                {
                    scope.GlobalNames.Add(name);
                    if (scope.Kind != ScopeKind.Module)
                        scope.GetOrAdd(name).Redirect = scope.ModuleScope.GetOrAdd(name);
                }
                return;
            case NonlocalStmt nl:
                foreach (var name in nl.Names)
                {
                    if (scope.Kind == ScopeKind.Module) { Diag(nl, "nonlocal declaration not allowed at module level"); continue; }
                    var target = FindNonlocal(scope.Parent, name);
                    if (target == null) { Diag(nl, $"no binding for nonlocal '{name}' found"); continue; }
                    scope.NonlocalNames.Add(name);
                    scope.GetOrAdd(name).Redirect = target;
                }
                return;
        }
        foreach (var c in AstChildren.Of(n))
            if (c is Stmt or ExceptHandler or MatchCase) CollectGlobalDecls(scope, c);
    }

    static Symbol? FindNonlocal(Scope? from, string name)
    {
        for (var s = from; s != null; s = s.Parent)
        {
            if (s.Kind is not (ScopeKind.Function or ScopeKind.Lambda)) continue;
            if (!s.Symbols.TryGetValue(name, out var sym)) continue;
            var target = sym.Redirect ?? sym;
            if (target.Declarations.Count > 0) return target;
        }
        return null;
    }

    void DeclareStmts(Scope sc, IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts) DeclareStmt(sc, s);
    }

    void DeclareStmt(Scope sc, Stmt st)
    {
        switch (st)
        {
            case ExprStmt x: ScanWalrus(sc, x.Value); break;
            case AssignStmt x:
                foreach (var t in x.Targets) { DeclareTarget(sc, t, DeclKind.Variable); ScanWalrus(sc, t); }
                ScanWalrus(sc, x.Value);
                break;
            case AugAssignStmt x: DeclareTarget(sc, x.Target, DeclKind.Variable); ScanWalrus(sc, x.Value); break;
            case AnnAssignStmt x:
                DeclareTarget(sc, x.Target, DeclKind.Variable);
                if (x.Value != null) ScanWalrus(sc, x.Value);
                break;
            case ReturnStmt x: if (x.Value != null) ScanWalrus(sc, x.Value); break;
            case RaiseStmt x:
                if (x.Exc != null) ScanWalrus(sc, x.Exc);
                if (x.Cause != null) ScanWalrus(sc, x.Cause);
                break;
            case AssertStmt x: ScanWalrus(sc, x.Test); break;
            case DelStmt x: foreach (var t in x.Targets) DeclareTarget(sc, t, DeclKind.Variable); break;
            case ImportStmt x:
                foreach (var a in x.Names)
                {
                    string bound = a.AsName ?? a.Name.Split('.')[0];
                    Bind(sc, bound, DeclKind.Import, a);
                }
                break;
            case ImportFromStmt x:
                if (x.IsStar) sc.HasStarImport = true;
                foreach (var a in x.Names) Bind(sc, a.AsName ?? a.Name, DeclKind.Import, a);
                break;
            case IfStmt x:
                ScanWalrus(sc, x.Test);
                DeclareStmts(sc, x.Body); DeclareStmts(sc, x.Orelse);
                break;
            case WhileStmt x:
                ScanWalrus(sc, x.Test);
                DeclareStmts(sc, x.Body); DeclareStmts(sc, x.Orelse);
                break;
            case ForStmt x:
                DeclareTarget(sc, x.Target, DeclKind.Variable);
                ScanWalrus(sc, x.Iter);
                DeclareStmts(sc, x.Body); DeclareStmts(sc, x.Orelse);
                break;
            case TryStmt x:
                DeclareStmts(sc, x.Body);
                foreach (var h in x.Handlers)
                {
                    if (h.Type != null) ScanWalrus(sc, h.Type);
                    if (h.Name != null) Bind(sc, h.Name, DeclKind.ExceptionVar, h);
                    DeclareStmts(sc, h.Body);
                }
                DeclareStmts(sc, x.Orelse); DeclareStmts(sc, x.Finally);
                break;
            case WithStmt x:
                foreach (var i in x.Items)
                {
                    ScanWalrus(sc, i.Context);
                    if (i.Target != null) DeclareTarget(sc, i.Target, DeclKind.Variable);
                }
                DeclareStmts(sc, x.Body);
                break;
            case FunctionDef x:
                Bind(sc, x.Name, DeclKind.Function, x);
                foreach (var d in x.Decorators) ScanWalrus(sc, d);
                foreach (var p in x.Params) if (p.Default != null) ScanWalrus(sc, p.Default);
                break;
            case ClassDef x:
                Bind(sc, x.Name, DeclKind.Class, x);
                foreach (var d in x.Decorators) ScanWalrus(sc, d);
                foreach (var b in x.Bases) ScanWalrus(sc, b.Value);
                break;
            case MatchStmt x:
                ScanWalrus(sc, x.Subject);
                foreach (var c in x.Cases)
                {
                    DeclarePattern(sc, c.Pattern);
                    if (c.Guard != null) ScanWalrus(sc, c.Guard);
                    DeclareStmts(sc, c.Body);
                }
                break;
            case TypeAliasStmt x: Bind(sc, x.Name, DeclKind.TypeAlias, x); break;
        }
    }

    void DeclareTarget(Scope sc, Expr t, DeclKind kind)
    {
        switch (t)
        {
            case NameExpr n: Bind(sc, n.Name, kind, n); break;
            case TupleExpr tu: foreach (var i in tu.Items) DeclareTarget(sc, i, kind); break;
            case ListExpr l: foreach (var i in l.Items) DeclareTarget(sc, i, kind); break;
            case StarExpr s: DeclareTarget(sc, s.Value, kind); break;
        }
    }

    /// <summary>Finds ":=" bindings inside an expression; they bind in the nearest non-comprehension scope.</summary>
    void ScanWalrus(Scope sc, Node n)
    {
        if (n is LambdaExpr) return; // has its own scope, handled when the lambda is visited
        if (n is NamedExpr w && w.Target is NameExpr ne) Bind(sc, ne.Name, DeclKind.Variable, ne);
        foreach (var c in AstChildren.Of(n)) ScanWalrus(sc, c);
    }

    void DeclarePattern(Scope sc, Pattern p)
    {
        switch (p)
        {
            case MatchCapture { Name: not null } c: Bind(sc, c.Name, DeclKind.MatchCapture, c); break;
            case MatchStar { Name: not null } s: Bind(sc, s.Name, DeclKind.MatchCapture, s); break;
            case MatchAs a: DeclarePattern(sc, a.Pattern); Bind(sc, a.Name, DeclKind.MatchCapture, a); break;
            case MatchOr o: foreach (var x in o.Alternatives) DeclarePattern(sc, x); break;
            case MatchSequence q: foreach (var x in q.Items) DeclarePattern(sc, x); break;
            case MatchMapping m:
                foreach (var x in m.Patterns) DeclarePattern(sc, x);
                if (m.Rest != null) Bind(sc, m.Rest, DeclKind.MatchCapture, m);
                break;
            case MatchClass c:
                foreach (var x in c.Positional) DeclarePattern(sc, x);
                foreach (var x in c.KeywordPatterns) DeclarePattern(sc, x);
                break;
        }
    }

    // ================================================================ visit pass

    void VisitStmts(IReadOnlyList<Stmt> stmts)
    {
        foreach (var s in stmts) VisitStmt(s);
    }

    void VisitStmt(Stmt st)
    {
        switch (st)
        {
            case ExprStmt x: VisitExpr(x.Value); break;
            case AssignStmt x:
                VisitExpr(x.Value);
                foreach (var t in x.Targets) VisitTarget(t, Access.Write);
                break;
            case AugAssignStmt x:
                VisitExpr(x.Value);
                VisitTarget(x.Target, Access.Read | Access.Write);
                break;
            case AnnAssignStmt x:
                VisitExpr(x.Annotation);
                if (x.Value != null) { VisitExpr(x.Value); VisitTarget(x.Target, Access.Write); }
                else VisitTarget(x.Target, Access.Write);
                break;
            case ReturnStmt x: VisitExpr(x.Value); break;
            case RaiseStmt x: VisitExpr(x.Exc); VisitExpr(x.Cause); break;
            case AssertStmt x: VisitExpr(x.Test); VisitExpr(x.Message); break;
            case DelStmt x: foreach (var t in x.Targets) VisitTarget(t, Access.Delete); break;
            case IfStmt x: VisitExpr(x.Test); VisitStmts(x.Body); VisitStmts(x.Orelse); break;
            case WhileStmt x: VisitExpr(x.Test); VisitStmts(x.Body); VisitStmts(x.Orelse); break;
            case ForStmt x:
                VisitExpr(x.Iter);
                VisitTarget(x.Target, Access.Write);
                VisitStmts(x.Body); VisitStmts(x.Orelse);
                break;
            case TryStmt x:
                VisitStmts(x.Body);
                foreach (var h in x.Handlers) { VisitExpr(h.Type); VisitStmts(h.Body); }
                VisitStmts(x.Orelse); VisitStmts(x.Finally);
                break;
            case WithStmt x:
                foreach (var i in x.Items)
                {
                    VisitExpr(i.Context);
                    if (i.Target != null) VisitTarget(i.Target, Access.Write);
                }
                VisitStmts(x.Body);
                break;
            case FunctionDef x: VisitFunction(x); break;
            case ClassDef x: VisitClass(x); break;
            case MatchStmt x:
                VisitExpr(x.Subject);
                foreach (var c in x.Cases)
                {
                    VisitPattern(c.Pattern);
                    VisitExpr(c.Guard);
                    VisitStmts(c.Body);
                }
                break;
            case TypeAliasStmt x:
            {
                var outer = _scope;
                var inner = x.TypeParams.Count > 0 ? OpenTypeParams(x, x.TypeParams) : outer;
                _scope = inner;
                VisitExpr(x.Value);
                _scope = outer;
                break;
            }
        }
    }

    /// <summary>PEP 695 type parameters live in their own scope between the def/class and its surroundings.</summary>
    Scope OpenTypeParams(Node owner, IReadOnlyList<TypeParam> tps)
    {
        var ts = new Scope(ScopeKind.TypeParams, owner, _scope);
        foreach (var tp in tps) Bind(ts, tp.Name, DeclKind.TypeParam, tp);
        var outer = _scope;
        _scope = ts;
        foreach (var tp in tps) { VisitExpr(tp.Bound); VisitExpr(tp.Default); }
        _scope = outer;
        return ts;
    }

    void VisitFunction(FunctionDef f)
    {
        foreach (var d in f.Decorators) VisitExpr(d);
        var outer = _scope;
        var ann = f.TypeParams.Count > 0 ? OpenTypeParams(f, f.TypeParams) : outer;

        _scope = ann;
        foreach (var p in f.Params) { VisitExpr(p.Default); VisitExpr(p.Annotation); }
        VisitExpr(f.Returns);

        var fs = new Scope(ScopeKind.Function, f, ann);
        _scopes[f] = fs;
        foreach (var p in f.Params)
            if (p.Kind is not (ParamKind.Slash or ParamKind.KwOnlySeparator)) Bind(fs, p.Name, DeclKind.Parameter, p);
        _scope = fs;
        Setup(fs, f.Body);
        VisitStmts(f.Body);
        _scope = outer;
    }

    void VisitClass(ClassDef c)
    {
        foreach (var d in c.Decorators) VisitExpr(d);
        var outer = _scope;
        var ann = c.TypeParams.Count > 0 ? OpenTypeParams(c, c.TypeParams) : outer;

        _scope = ann;
        foreach (var b in c.Bases) VisitExpr(b.Value);

        var cs = new Scope(ScopeKind.Class, c, ann);
        _scopes[c] = cs;
        _scope = cs;
        Setup(cs, c.Body);
        VisitStmts(c.Body);
        _scope = outer;
    }

    void VisitLambda(LambdaExpr l)
    {
        foreach (var p in l.Params) VisitExpr(p.Default);
        var outer = _scope;
        var ls = new Scope(ScopeKind.Lambda, l, outer);
        _scopes[l] = ls;
        foreach (var p in l.Params)
            if (p.Kind is not (ParamKind.Slash or ParamKind.KwOnlySeparator)) Bind(ls, p.Name, DeclKind.Parameter, p);
        ScanWalrus(ls, l.Body);
        _scope = ls;
        VisitExpr(l.Body);
        _scope = outer;
    }

    void VisitComprehension(CompExpr c)
    {
        if (c.Generators.Count == 0) return;
        // The outermost iterable is evaluated in the enclosing scope.
        VisitExpr(c.Generators[0].Iter);

        var outer = _scope;
        var cs = new Scope(ScopeKind.Comprehension, c, outer);
        _scopes[c] = cs;
        foreach (var g in c.Generators) DeclareTarget(cs, g.Target, DeclKind.Variable);
        _scope = cs;
        for (int i = 0; i < c.Generators.Count; i++)
        {
            var g = c.Generators[i];
            if (i > 0) VisitExpr(g.Iter);
            VisitTarget(g.Target, Access.Write);
            foreach (var cond in g.Ifs) VisitExpr(cond);
        }
        VisitExpr(c.Element);
        VisitExpr(c.Value);
        _scope = outer;
    }

    void VisitPattern(Pattern p)
    {
        switch (p)
        {
            case MatchValue v: VisitExpr(v.Value); break;
            case MatchAs a: VisitPattern(a.Pattern); break;
            case MatchOr o: foreach (var x in o.Alternatives) VisitPattern(x); break;
            case MatchSequence q: foreach (var x in q.Items) VisitPattern(x); break;
            case MatchMapping m:
                foreach (var k in m.Keys) VisitExpr(k);
                foreach (var x in m.Patterns) VisitPattern(x);
                break;
            case MatchClass c:
                VisitExpr(c.Cls);
                foreach (var x in c.Positional) VisitPattern(x);
                foreach (var x in c.KeywordPatterns) VisitPattern(x);
                break;
        }
    }

    void VisitExpr(Expr? e)
    {
        switch (e)
        {
            case null: return;
            case NameExpr n: Record(n, Access.Read); return;
            case LambdaExpr l: VisitLambda(l); return;
            case CompExpr c: VisitComprehension(c); return;
            case NamedExpr w:
                VisitExpr(w.Value);
                VisitTarget(w.Target, Access.Write);
                return;
        }
        foreach (var c in AstChildren.Of(e))
        {
            if (c is Expr x) VisitExpr(x);
            else if (c is Argument a) VisitExpr(a.Value);
        }
    }

    void VisitTarget(Expr t, Access access)
    {
        switch (t)
        {
            case NameExpr n: Record(n, access); break;
            case TupleExpr tu: foreach (var i in tu.Items) VisitTarget(i, access); break;
            case ListExpr l: foreach (var i in l.Items) VisitTarget(i, access); break;
            case StarExpr s: VisitTarget(s.Value, access); break;
            default: VisitExpr(t); break; // attribute/subscript targets only *read* their sub-expressions
        }
    }

    void Record(NameExpr n, Access access)
    {
        var sym = _scope.Lookup(n.Name);
        bool builtin = false;
        if (sym == null)
        {
            builtin = Builtins.Names.Contains(n.Name);
            if (!builtin && (access & Access.Read) != 0 && !_scope.ModuleScope.HasStarImport)
                Diag(n, $"Unresolved reference '{n.Name}'");
        }
        var r = new Reference(n, sym, builtin, access) { UseScope = _scope };
        _refs[n] = r;
        sym?.References.Add(r);
    }
}
