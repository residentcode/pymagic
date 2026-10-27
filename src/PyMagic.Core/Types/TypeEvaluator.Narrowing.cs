using PyMagic.Core.Flow;
using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public sealed partial class TypeEvaluator
{
    readonly Dictionary<(ScopeFlow, Symbol, string, PyType), FlowWalker> _walkers = new();

    FlowWalker Walker(ModuleInfo m, ScopeFlow flow, Symbol sym, string path, PyType entry)
    {
        var key = (flow, sym, path, entry);
        if (!_walkers.TryGetValue(key, out var w)) _walkers[key] = w = new FlowWalker(this, m, flow, sym, path, entry);
        return w;
    }

    /// <summary>
    /// Computes the type of a reference (a name, or a member path below it) at a point of a scope's control-flow graph
    /// by walking backwards: assignments supply their type, conditions narrow, joins unite the paths.
    /// </summary>
    sealed class FlowWalker
    {
        readonly TypeEvaluator _ev;
        readonly ModuleInfo _m;
        readonly ModuleInfo _symModule;
        readonly ScopeFlow _flow;
        readonly Symbol _sym;
        readonly string _path;
        readonly PyType _entry;
        readonly PyType? _declared;
        readonly Dictionary<Instruction, PyType> _memo = new();
        readonly HashSet<Instruction> _active = new();
        readonly HashSet<Instruction> _open = new();
        int _steps;

        /// <summary>The walk visited more instructions than it is worth: the caller falls back to the unnarrowed answer.</summary>
        public bool Exhausted => _steps > Budget;
        const int Budget = 30_000;

        public FlowWalker(TypeEvaluator ev, ModuleInfo m, ScopeFlow flow, Symbol sym, string path, PyType entry)
        {
            _ev = ev; _m = m; _flow = flow; _sym = sym; _path = path;
            _symModule = ev._ws.ModuleOf(sym) ?? m;
            _declared = path.Length == 0 ? ev.DeclaredType(_symModule, sym) : null;
            // `x: int` with no assignment (and typing's `Any: _SpecialForm`) still has its declared type
            _entry = _declared ?? entry;
        }

        /// <summary>Instructions that can change what we know about the reference.</summary>
        bool Relevant(Instruction p)
        {
            if (!ReferenceEquals(p.Symbol, _sym)) return false;
            switch (p.Kind)
            {
                case InstrKind.Write:
                case InstrKind.Delete:
                    if (_path.Length == 0) return p.Path.Length == 0;
                    return p.Path.Length == 0 || _path == p.Path || _path.StartsWith(p.Path + ".", StringComparison.Ordinal);
                case InstrKind.Assert:
                    return p.Path == _path;
                default: return false;
            }
        }

        PyType After(Instruction p)
        {
            if (!Relevant(p)) return Before(p);
            switch (p.Kind)
            {
                case InstrKind.Write:
                    if (_path.Length == 0)
                    {
                        if (p.Declaration == null) return _declared ?? PyType.Unknown;
                        if (_declared == null) return _ev.TypeOfDeclaration(_symModule, p.Declaration);
                        // a declared variable takes the type of the value assigned to it, if that fits and is fully known
                        var assigned = _ev.AssignedType(_symModule, p.Declaration);
                        // a value nothing is known about (an unresolved call) says nothing against the declaration: no reports from it
                        if (assigned is UnknownType { Explicit: false, FromCycle: false }) return assigned;
                        // a container of unknown items (`args = [value, *args]`) is still that container, with the declared items
                        if (assigned != null && ContainsDynamic(assigned)) assigned = _ev.WithDeclaredArgs(assigned, _declared);
                        return assigned != null && _ev.IsAssignable(_declared, assigned) ? assigned : _declared;
                    }
                    return _entry; // the member (or its owner) was reassigned: back to the unnarrowed type
                case InstrKind.Delete:
                    return _path.Length == 0 ? PyType.Never : _entry;
                case InstrKind.Assert:
                    return _ev.Narrow(_m, p, Before(p));
                default: return Before(p);
            }
        }

        /// <summary>The type of the reference immediately before executing <paramref name="at"/>.</summary>
        public PyType Before(Instruction at)
        {
            var cur = Skip(at);
            if (_memo.TryGetValue(cur, out var hit)) return hit;
            if (cur.Kind == InstrKind.Entry) return _entry;
            if (++_steps > Budget) return _entry;

            if (!_active.Add(cur)) { _open.Add(cur); return PyType.CycleUnknown; }
            PyType result;
            try
            {
                var parts = new List<PyType>();
                foreach (var p in cur.Predecessors) parts.Add(After(p));
                result = parts.Count == 0 ? PyType.Never : PyType.Union(parts);
            }
            finally { _active.Remove(cur); }
            _open.Remove(cur);

            // results computed while a cycle was cut (here or in the evaluator) are incomplete: do not remember them
            if (_open.Count == 0 && _ev._open.Count == 0) _memo[cur] = result;
            return result;
        }

        readonly Dictionary<Instruction, Instruction> _skip = new();

        /// <summary>
        /// Walks up straight-line code that cannot affect the reference, to the first instruction that has to be
        /// looked at (the entry, a join, or something right after a relevant instruction). Independent of types, so cached.
        /// </summary>
        Instruction Skip(Instruction at)
        {
            if (_skip.TryGetValue(at, out var s)) return s;
            var chain = new List<Instruction>();
            var cur = at;
            while (true)
            {
                if (_skip.TryGetValue(cur, out var known)) { cur = known; break; }
                chain.Add(cur);
                if (cur.Kind != InstrKind.Entry && cur.Predecessors.Count == 1 && !Relevant(cur.Predecessors[0]))
                {
                    cur = cur.Predecessors[0];
                    continue;
                }
                break;
            }
            foreach (var c in chain) _skip[c] = cur;
            return cur;
        }
    }

    /// <summary>The type of the value a declaration binds, ignoring any annotation; null if it binds no value (`x: int`).</summary>
    PyType? AssignedType(ModuleInfo m, Declaration d)
    {
        if (d.Kind == DeclKind.Variable && d.Node is Expr target && m.ParentOf(target) is AnnAssignStmt an && ReferenceEquals(an.Target, target))
            return an.Value == null ? null : TypeOf(m, an.Value);
        return TypeOfDeclaration(m, d);
    }

    /// <summary>
    /// An instance of a generic class whose arguments are partly unknown, with those arguments solved from a declaration
    /// it is assigned to (list[Unknown] declared Sequence[str] is list[str]); null for any other dynamic type.
    /// </summary>
    PyType? WithDeclaredArgs(PyType assigned, PyType declared)
    {
        if (assigned is not ClassType { IsInstance: true, TupleItems: null, Args.Count: > 0 } c || IsClassOfAny(c)) return null;
        var ps = ClassDataOf(c.Info).TypeParams;
        if (ps.Count != c.Args.Count) return null;
        var solved = new Dictionary<string, PyType>();
        var generic = new ClassType(c.Info, ps.ToList<PyType>(), true);
        foreach (var d in declared.Members)
            if (d is ClassType { IsInstance: true } dc && SpecializeAs(generic, dc.Info) is { } view) Unify(view, dc, solved);
        // an argument the declaration does not give (`x: object = []`) stays as it was
        return c with { Args = c.Args.Select((a, i) => ContainsDynamic(a) && solved.TryGetValue(TypeParamName(ps[i]), out var s) ? s : a).ToList() };
    }

    static bool ContainsDynamic(PyType t) => t switch
    {
        UnknownType => true,
        ClassType c when IsClassOfAny(c) => false,   // type[Any] is a value worth keeping: a declared `x: object = cls` follows it
        ClassType c => c.Args.Any(ContainsDynamic) || (c.TupleItems?.Any(ContainsDynamic) ?? false),
        UnionType u => u.MemberList.Any(ContainsDynamic),
        _ => false,
    };

    // ------------------------------------------------------------ narrowing rules

    /// <summary>Applies what a branch of a condition tells us (assertion instruction) to the type before it.</summary>
    PyType Narrow(ModuleInfo m, Instruction assertion, PyType t)
    {
        if (t is NeverType) return t;
        bool positive = assertion.AssertPositive;
        if (assertion.Element is Pattern pattern) return NarrowPattern(m, pattern, t, positive);
        if (assertion.Element is not Expr cond) return t;

        switch (cond)
        {
            case NameExpr or AttributeExpr or NamedExpr:
                return Truthiness(t, positive);

            case CompareExpr c:
                return NarrowCompare(m, assertion, c, t, positive);

            case CallExpr call:
                return NarrowCall(m, call, t, positive);
        }
        return t;
    }

    PyType OnlyNone(PyType t) =>
        PyType.Union(t.Members.Where(x => IsNone(x) || x is UnknownType));

    PyType Truthiness(PyType t, bool truthy)
    {
        // Literal[""] and Literal[0] are always falsy, other literals always truthy
        if (t.Members.Any(x => x is ClassType { IsInstance: true, LiteralValue: not null }))
            t = PyType.Union(t.Members.Where(x => x is not ClassType { IsInstance: true, LiteralValue: { } v } || IsTruthyLiteral(v) == truthy));
        if (truthy) return WithoutNone(t);
        // a falsy value: None, or an instance of a class that can be falsy (defines __bool__ or __len__)
        return PyType.Union(t.Members.Where(x => x switch
        {
            UnknownType => true,
            ClassType { IsInstance: true } c => IsNone(c) || CanBeFalsy(c),
            _ => false,   // class objects, functions and modules are always truthy
        }));
    }

    bool CanBeFalsy(ClassType c)
    {
        foreach (var name in new[] { "__bool__", "__len__" })
        {
            var hit = FindMember(c, name, true, null, out bool unknownBase);
            if (unknownBase || hit != null) return true;
        }
        return false;
    }

    /// <summary>
    /// The classes named in the second argument of isinstance, and whether some of it is not a class that is known
    /// (an unresolved import, a class rebound by a call, a type[T]).
    /// </summary>
    (List<ClassType> Classes, bool Unknown) IsinstanceClasses(ModuleInfo m, Expr arg)
    {
        var result = new List<ClassType>();
        bool unknown = false;
        void Collect(PyType t)
        {
            switch (t)
            {
                case ClassType { IsInstance: false } c: result.Add(c); break;
                case ClassType { IsInstance: true, TupleItems: not null } tup: foreach (var item in tup.TupleItems) Collect(item); break;
                case UnionType u: foreach (var member in u.MemberList) Collect(member); break;
                default: unknown = true; break;
            }
        }
        if (arg is BinaryExpr { Op: "|" } b)
        {
            var (l, lu) = IsinstanceClasses(m, b.Left);
            var (r, ru) = IsinstanceClasses(m, b.Right);
            l.AddRange(r);
            return (l, lu || ru);
        }
        Collect(TypeOf(m, arg));
        return (result, unknown);
    }

    PyType IsInstancePositive(PyType t, List<ClassType> classes)
    {
        var result = new List<PyType>();
        foreach (var member in t.Members)
        {
            switch (member)
            {
                case UnknownType:
                case TypeVarType:
                    result.AddRange(classes.Select(c => (PyType)c.AsInstance()));
                    break;

                case ClassType { IsInstance: true } mt:
                    foreach (var fc in classes)
                    {
                        if (IsSubclass(mt.Info, fc.Info)) result.Add(mt);
                        else if (IsSubclass(fc.Info, mt.Info)) result.Add(fc.AsInstance());
                        else if (HasUnknownAncestor(mt.Info) || HasUnknownAncestor(fc.Info)) result.Add(fc.AsInstance());
                    }
                    break;

                case ClassType { IsInstance: false }:
                    if (classes.Any(fc => fc.Info.Name is "type" or "object")) result.Add(member);
                    break;

                case FunctionType or OverloadedType or SignatureType:
                    if (classes.Any(fc => fc.Info.Name is "function" or "Callable" or "object")) result.Add(member);
                    break;
            }
        }
        return result.Count == 0 ? PyType.Never : PyType.Union(result);
    }

    PyType IsInstanceNegative(PyType t, List<ClassType> classes) =>
        PyType.Union(t.Members.Where(member =>
            !(member is ClassType { IsInstance: true } mt && classes.Any(fc => IsSubclass(mt.Info, fc.Info)))));

    // ------------------------------------------------------------ using the walker

    /// <summary>Type of a name use, with narrowing and only the assignments that can reach it. Null if the flow cannot say.</summary>
    /// <summary>
    /// Type of a name use with narrowing applied, for names that some condition in the use's scope narrows.
    /// Null when nothing narrows the name there (the cheaper reaching-definitions answer is used instead), so the
    /// backward walk is only paid for where it can change the result.
    /// </summary>
    PyType? FlowTypeOfName(ModuleInfo m, NameExpr n, Reference r, Symbol sym)
    {
        if (r.UseScope == null) return null;
        var flow = m.Flow.For(r.UseScope);
        // a declared name that is assigned again (`url: URL | str` ... `url = URL(url)`) takes the assigned type from there on
        bool reassigned = sym.Declarations.Count > 1 && DeclaredType(_ws.ModuleOf(sym) ?? m, sym) != null;
        if (flow == null || !(reassigned || flow.HasAssertions(sym, ""))) return null;
        var reads = flow.ReadInstructions(n);
        if (reads == null || reads.Count == 0) return null;

        bool owned = ReferenceEquals(sym.Scope, r.UseScope);
        var entry = owned ? PyType.Never : TypeOfSymbol(sym);
        var w = Walker(m, flow, sym, "", entry);
        var t = PyType.Union(reads.Select(w.Before));
        if (w.Exhausted) return null;   // too tangled to follow: use the plain reaching-definitions answer
        return t is NeverType ? PyType.Unknown : t;
    }
    /// <summary>Type of <c>a.b.c</c> after applying conditions such as <c>if a.b is not None</c>.</summary>
    PyType NarrowedAttribute(ModuleInfo m, AttributeExpr a)
    {
        var baseline = AttributeType(TypeOf(m, a.Value), a.Attr).Type;
        if (!CfgBuilder.TryChain(a, out var root, out var path)) return baseline;
        var r = m.Semantics.ReferenceOf(root);
        if (r?.Symbol == null || r.UseScope == null) return baseline;
        var flow = m.Flow.For(r.UseScope);
        if (flow == null || !flow.HasAssertions(r.Symbol, path)) return baseline;
        var reads = flow.ReadInstructions(root);
        if (reads == null || reads.Count == 0) return baseline;

        var w = Walker(m, flow, r.Symbol, path, baseline);
        var t = PyType.Union(reads.Select(w.Before));
        if (w.Exhausted) return baseline;
        // nothing left (`isinstance(self.reason, bytes)` on a `str` attribute): unknown, as for names, not the declared type
        return t is NeverType ? PyType.Unknown : t;
    }
}
