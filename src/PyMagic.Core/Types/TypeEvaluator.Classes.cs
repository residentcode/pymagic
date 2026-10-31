using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>Outcome of looking up an attribute on a type.</summary>
/// <param name="Type">Union of the types found (Unknown when anything was dynamic).</param>
/// <param name="Missing">Receiver types on which the attribute does not exist (and which are fully known).</param>
/// <param name="Declarations">Class-level declarations the attribute resolved to (for go-to-definition).</param>
/// <param name="InstanceSites">self.attr assignments that define the attribute.</param>
public sealed record AttributeResult(PyType Type, IReadOnlyList<PyType> Missing,
    IReadOnlyList<Declaration> Declarations, IReadOnlyList<Node> InstanceSites);

public sealed partial class TypeEvaluator
{
    sealed class ClassData
    {
        /// <summary>TypeVarType, ParamSpecType or TypeVarTupleType, in the order the class's type arguments are written.</summary>
        public List<PyType> TypeParams { get; } = new();
        public List<PyType> Bases { get; } = new();   // ClassType (class object form) or Unknown
        public bool IsProtocol { get; set; }
        public bool HasUnknownBase { get; set; }
        /// <summary>Class given as metaclass=... on this class (not inherited).</summary>
        public ClassType? Metaclass { get; set; }
        public bool UnknownMetaclass { get; set; }
        /// <summary>The class lists TypedDict (the special form) among its bases.</summary>
        public bool IsTypedDict { get; set; }
    }

    sealed record InstanceAttr(ModuleInfo Module, Expr Target, FunctionInfo Method);
    sealed record MemberHit(ClassType Owner, Symbol? ClassSymbol, IReadOnlyList<InstanceAttr> InstanceAttrs);

    readonly Dictionary<ClassInfo, HashSet<string>> _slots = new(ReferenceEqualityComparer.Instance);

    readonly Dictionary<ClassInfo, ClassData> _classData = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<ClassInfo> _classDataActive = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, List<PyType>> _mros = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<ClassInfo> _mroActive = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<ClassInfo, Dictionary<string, List<InstanceAttr>>> _instanceAttrs = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<object, PyType> _targetCache = new(ReferenceEqualityComparer.Instance);

    // ------------------------------------------------------------ class structure

    public IReadOnlyList<PyType> TypeParametersOf(ClassInfo c) => ClassDataOf(c).TypeParams;
    public bool IsProtocol(ClassInfo c) => ClassDataOf(c).IsProtocol;

    /// <summary>The variable a PEP 695 type parameter declares: `**P` a ParamSpec, `*Ts` a TypeVarTuple, the others a TypeVar.</summary>
    static PyType TypeParamType(TypeParam tp) => tp.Kind switch
    {
        TypeParamKind.ParamSpec => new ParamSpecType(tp.Name),
        TypeParamKind.TypeVarTuple => new TypeVarTupleType(tp.Name),
        _ => new TypeVarType(tp.Name),
    };

    /// <summary>The name a type parameter of a class (a TypeVar, a ParamSpec or a TypeVarTuple) is substituted by.</summary>
    static string TypeParamName(PyType tp) => tp switch { TypeVarType tv => tv.Name, ParamSpecType p => p.Name, TypeVarTupleType ts => ts.Name, _ => "" };

    ClassData ClassDataOf(ClassInfo c)
    {
        if (_classData.TryGetValue(c, out var d)) return d;
        d = new ClassData();
        if (!_classDataActive.Add(c)) return d; // inheritance cycle: give an empty answer, do not cache
        try
        {
            var m = c.Module;
            bool explicitParams = false;

            foreach (var tp in c.Node.TypeParams) { d.TypeParams.Add(TypeParamType(tp)); explicitParams = true; }

            foreach (var arg in c.Node.Bases)
            {
                if (arg.Name == "metaclass")
                {
                    if (ValueOf(m, arg.Value, null) is ClassType { IsInstance: false } meta) d.Metaclass = meta;
                    else d.UnknownMetaclass = true;
                    continue;
                }
                if (arg.Name != null) continue; // total=False, ...
                if (arg.Stars != 0) { d.HasUnknownBase = true; continue; }

                var expr = arg.Value;
                if (expr is SubscriptExpr sub)
                {
                    var head = ValueOf(m, sub.Value, null);
                    if (head is SpecialFormType { Name: "Generic" or "Protocol" } sf)
                    {
                        if (sf.Name == "Protocol") d.IsProtocol = true;
                        IReadOnlyList<Expr> targs = sub.Index is TupleExpr { Parenthesized: false } t ? t.Items : new[] { sub.Index };
                        if (!explicitParams)
                        {
                            foreach (var a in targs)
                                if (Annotation(m, a, null) is var tv and (TypeVarType or ParamSpecType or TypeVarTupleType) && !d.TypeParams.Contains(tv)) d.TypeParams.Add(tv);
                            explicitParams = true;
                        }
                        continue;
                    }
                    var spec = AnnotationToValue(AnnotationSubscript(m, sub, null));
                    AddBase(d, spec);
                    continue;
                }

                var v = ValueOf(m, expr, null);
                if (v is SpecialFormType { Name: "Protocol" }) { d.IsProtocol = true; continue; }
                if (v is SpecialFormType { Name: "Generic" }) continue;
                if (v is SpecialFormType { Name: "TypedDict" })
                {
                    d.IsTypedDict = true;
                    // typing._TypedDict is the real class behind the special form (a Mapping[str, object])
                    var td = TypingClass("_TypedDict");
                    if (td != null) d.Bases.Add(td);
                    continue;
                }
                AddBase(d, v);
            }

            if (d.Bases.Count == 0 && !(c.Name == "object" && c.Module.Name == "builtins"))
            {
                var obj = BuiltinClass("object");
                if (obj != null && !d.HasUnknownBase) d.Bases.Add(obj);
            }

            if (!explicitParams)
                foreach (var b in d.Bases)
                    if (b is ClassType bc) foreach (var a in bc.Args) CollectTypeVars(a, d.TypeParams);

            return _classData[c] = d;
        }
        finally { _classDataActive.Remove(c); }
    }

    static void AddBase(ClassData d, PyType v)
    {
        if (v is ClassType { IsInstance: false } c) d.Bases.Add(c);
        else { d.Bases.Add(PyType.Unknown); d.HasUnknownBase = true; }
    }

    static void CollectTypeVars(PyType t, List<PyType> into)
    {
        switch (t)
        {
            case TypeVarType tv when tv.Name != "Self": if (!into.Contains(tv)) into.Add(tv); break;
            case ParamSpecType p: if (!into.Contains(p)) into.Add(p); break;
            case TypeVarTupleType ts: if (!into.Contains(ts)) into.Add(ts); break;
            case ClassType c:
                foreach (var a in c.Args) CollectTypeVars(a, into);
                if (c.TupleItems != null) foreach (var a in c.TupleItems) CollectTypeVars(a, into);
                break;
            case UnionType u: foreach (var x in u.MemberList) CollectTypeVars(x, into); break;
            case SignatureType s:
                if (s.Params != null) foreach (var p in s.Params) CollectTypeVars(p.Type, into);
                if (s.Spec != null) CollectTypeVars(s.Spec, into);
                CollectTypeVars(s.Return, into);
                break;
        }
    }

    IReadOnlyList<PyType> ClassTypeParamsAsArgs(ClassInfo c) => ClassDataOf(c).TypeParams.ToList();

    /// <summary>Linearised ancestors, most specific first. Entries are class objects; an Unknown entry marks unresolvable ancestors.</summary>
    public IReadOnlyList<PyType> MroOf(ClassInfo c) => Mro(c);

    List<PyType> Mro(ClassInfo c)
    {
        if (_mros.TryGetValue(c, out var cached)) return cached;
        var self = new ClassType(c, ClassTypeParamsAsArgs(c), false);
        if (!_mroActive.Add(c)) return new List<PyType> { self };
        try
        {
            var data = ClassDataOf(c);
            bool unknown = data.HasUnknownBase;
            var seqs = new List<List<ClassType>>();
            var direct = new List<ClassType>();
            foreach (var b in data.Bases)
            {
                if (b is not ClassType bc) { unknown = true; continue; }
                var map = TypeParamMap(bc.Info, bc.Args);
                var seq = new List<ClassType>();
                foreach (var e in Mro(bc.Info))
                {
                    if (e is not ClassType ec) { unknown = true; continue; }
                    var entry = (ClassType)Substitute(ec, map);
                    // a tuple[int, str] base keeps its per-position items, which the base class's own MRO cannot know
                    seq.Add(seq.Count == 0 && bc.TupleItems != null ? entry with { TupleItems = bc.TupleItems } : entry);
                }
                seqs.Add(seq);
                direct.Add(bc);
            }
            seqs.Add(direct);

            var result = new List<PyType> { self };
            result.AddRange(MergeC3(seqs));
            if (unknown) result.Add(PyType.Unknown);
            return _mros[c] = result;
        }
        finally { _mroActive.Remove(c); }
    }

    static List<ClassType> MergeC3(List<List<ClassType>> seqs)
    {
        var result = new List<ClassType>();
        seqs = seqs.Select(s => new List<ClassType>(s)).Where(s => s.Count > 0).ToList();
        while (seqs.Count > 0)
        {
            ClassType? pick = null;
            foreach (var s in seqs)
            {
                var head = s[0];
                if (!seqs.Any(o => o.Skip(1).Any(x => ReferenceEquals(x.Info, head.Info)))) { pick = head; break; }
            }
            pick ??= seqs[0][0]; // inconsistent hierarchy: fall back to first head
            result.Add(pick);
            foreach (var s in seqs) s.RemoveAll(x => ReferenceEquals(x.Info, pick.Info));
            seqs = seqs.Where(s => s.Count > 0).ToList();
        }
        return result;
    }

    Dictionary<string, PyType> TypeParamMap(ClassInfo info, IReadOnlyList<PyType> args)
    {
        var map = new Dictionary<string, PyType>();
        var ps = ClassDataOf(info).TypeParams;
        int v = ps.FindIndex(p => p is TypeVarTupleType);
        if (v < 0 || args.Count == 0)
        {
            for (int i = 0; i < ps.Count; i++) map[TypeParamName(ps[i])] = i < args.Count ? args[i] : PyType.Unknown;
            return map;
        }
        // Array[int, str, bytes] for (T, *Ts, U): T and U take the ends, Ts the arguments between them
        int after = ps.Count - v - 1, middle = args.Count - v - after;
        for (int i = 0; i < v; i++) map[TypeParamName(ps[i])] = i < args.Count ? args[i] : PyType.Unknown;
        for (int j = 0; j < after; j++) map[TypeParamName(ps[v + 1 + j])] = middle >= 0 ? args[v + middle + j] : PyType.Unknown;
        // an Unknown where Ts stands (an unsolved Ts, `Array[Any]`) is a Ts of unknown length
        var tuple = BuiltinClass("tuple");
        bool unknown = middle < 0 || (middle == 1 && args[v] is UnknownType) || tuple == null;
        map[TypeParamName(ps[v])] = unknown ? PyType.Unknown : TupleOf(tuple!, args.Skip(v).Take(middle).ToList());
        return map;
    }

    /// <summary>The given instance viewed as an instance of one of its ancestors, with that ancestor's type arguments filled in.</summary>
    public ClassType? SpecializeAs(ClassType t, ClassInfo target)
    {
        if (ReferenceEquals(t.Info, target)) return t;
        var map = TypeParamMap(t.Info, t.Args);
        foreach (var e in Mro(t.Info))
            if (e is ClassType ec && ReferenceEquals(ec.Info, target))
                return ((ClassType)Substitute(ec, map)) with { IsInstance = t.IsInstance };
        return null;
    }

    public bool IsSubclass(ClassInfo sub, ClassInfo ancestor) =>
        Mro(sub).Any(e => e is ClassType c && ReferenceEquals(c.Info, ancestor));

    // ------------------------------------------------------------ substitution

    /// <summary>Replaces type variables by name. Unmapped variables stay.</summary>
    public PyType Substitute(PyType t, IReadOnlyDictionary<string, PyType>? map)
    {
        if (map == null || map.Count == 0) return t;
        switch (t)
        {
            case TypeVarType tv: return map.TryGetValue(tv.Name, out var r) ? r : t;
            case ParamSpecType p: return map.TryGetValue(p.Name, out var given) ? given : t;
            case TypeVarTupleType ts: return map.TryGetValue(ts.Name, out var types) ? types : t;
            case ClassType c:
            {
                if (c.Args.Count == 0 && c.TupleItems == null) return c;
                PyType Solved(TypeVarTupleType ts) => map.TryGetValue(ts.Name, out var types) ? types : ts;
                var args = SpliceVariadic(c.Args, a => Substitute(a, map), Solved, classArgs: true)!;
                if (c.TupleItems == null) return new ClassType(c.Info, args, c.IsInstance);
                var items = SpliceVariadic(c.TupleItems, a => Substitute(a, map), Solved);
                // tuple[int, *Ts] with Ts of unknown length is a tuple of unknown length
                if (items == null) return new ClassType(c.Info, new[] { PyType.Unknown }, c.IsInstance);
                return c.TupleItems.Any(x => x is TypeVarTupleType) ? TupleOf(c, items) with { IsInstance = c.IsInstance } : new ClassType(c.Info, args, c.IsInstance, items);
            }
            case UnionType u: return PyType.Union(u.MemberList.Select(x => Substitute(x, map)));
            case SignatureType s:
            {
                var ps = s.Params == null ? null : SubstituteParams(s.Params, map);
                var ret = Substitute(s.Return, map);
                if (s.Spec == null || !map.TryGetValue(s.Spec.Name, out var spec)) return s with { Params = ps, Return = ret };
                // P solved: its parameters follow the leading ones (Concatenate), and the def they came from stays known
                return spec switch
                {
                    SignatureType { Params: { } solved } list => new SignatureType((ps ?? new List<ParamInfo>()).Concat(solved).ToList(), ret, list.Spec, list.Origin),
                    ParamSpecType other => s with { Params = ps, Return = ret, Spec = other },
                    _ => new SignatureType(null, ret),    // solved to anything: takes any arguments
                };
            }
            case FunctionType f:
                return f with { Subst = MergeSubst(f.Subst, map) };
            default: return t;
        }
    }

    /// <summary>
    /// Items with a `*Ts` among them (tuple items, a class's arguments), each Ts replaced by the types <paramref name="solved"/>
    /// gives it (a tuple of them, or another Ts). A Ts that stands for types of unknown length makes it null, or with
    /// <paramref name="classArgs"/> is one Unknown where it stands (how a class keeps an unsolved Ts).
    /// </summary>
    static List<PyType>? SpliceVariadic(IReadOnlyList<PyType> items, Func<PyType, PyType> each, Func<TypeVarTupleType, PyType> solved, bool classArgs = false)
    {
        var result = new List<PyType>();
        foreach (var x in items)
        {
            if (x is not TypeVarTupleType ts) { result.Add(each(x)); continue; }
            switch (solved(ts))
            {
                case TypeVarTupleType other: result.Add(other); break;
                case ClassType { TupleItems: { } types }: result.AddRange(types); break;
                default: if (!classArgs) return null; result.Add(PyType.Unknown); break;
            }
        }
        return result;
    }

    /// <summary>Parameters with the variables substituted; a `*args: *Ts` with Ts solved becomes one positional-only parameter per type.</summary>
    List<ParamInfo> SubstituteParams(IEnumerable<ParamInfo> ps, IReadOnlyDictionary<string, PyType> map)
    {
        var result = new List<ParamInfo>();
        foreach (var p in ps)
        {
            if (p is not { Category: ParamCategory.VarPositional, Type: TypeVarTupleType ts } || !map.TryGetValue(ts.Name, out var given))
            {
                result.Add(p with { Type = Substitute(p.Type, map) });
                continue;
            }
            // solved to something of unknown length: any positional arguments
            if (given is not ClassType { TupleItems: { } types }) { result.Add(p with { Type = given is TypeVarTupleType ? given : PyType.Unknown }); continue; }
            foreach (var t in types)
                result.Add(t is TypeVarTupleType more ? p with { Type = more } : new ParamInfo($"_{result.Count}", t, ParamCategory.PositionalOnly, false));
        }
        return result;
    }

    static IReadOnlyDictionary<string, PyType> MergeSubst(IReadOnlyDictionary<string, PyType>? a, IReadOnlyDictionary<string, PyType> b)
    {
        if (a == null || a.Count == 0) return b;
        var d = new Dictionary<string, PyType>(a);
        foreach (var kv in b) d.TryAdd(kv.Key, kv.Value);
        return d;
    }

    /// <summary>Type variables left unsolved become Unknown.</summary>
    PyType EraseTypeVars(PyType t) => EraseTypeVars(t, null);

    /// <summary>
    /// A callable that is returned and takes type variables as parameters keeps them (`lru_cache(maxsize=None)` is
    /// `(Callable[..., T]) -> _lru_cache_wrapper[T]`, solved when it is applied to the function it decorates).
    /// </summary>
    PyType EraseTypeVars(PyType t, IReadOnlySet<string>? keep) => t switch
    {
        TypeVarType tv => keep != null && keep.Contains(tv.Name) ? tv : PyType.Unknown,
        ParamSpecType p => keep != null && keep.Contains(p.Name) ? p : PyType.Unknown,
        TypeVarTupleType ts => keep != null && keep.Contains(ts.Name) ? ts : PyType.Unknown,
        ClassType c when c.Args.Count > 0 || c.TupleItems != null => EraseClassArgs(c, keep),
        UnionType u => PyType.Union(u.MemberList.Select(x => EraseTypeVars(x, keep))),
        SignatureType s => EraseSignature(s, keep),
        _ => t,
    };

    PyType EraseClassArgs(ClassType c, IReadOnlySet<string>? keep)
    {
        PyType Kept(TypeVarTupleType ts) => keep != null && keep.Contains(ts.Name) ? ts : PyType.Unknown;
        var args = SpliceVariadic(c.Args, a => EraseTypeVars(a, keep), Kept, classArgs: true)!;
        if (c.TupleItems == null) return new ClassType(c.Info, args, c.IsInstance);
        // a tuple[int, *Ts] whose Ts nothing solved has a length that is not known
        var items = SpliceVariadic(c.TupleItems, a => EraseTypeVars(a, keep), Kept);
        return items == null ? new ClassType(c.Info, new[] { PyType.Unknown }, c.IsInstance) : new ClassType(c.Info, args, c.IsInstance, items);
    }

    PyType EraseSignature(SignatureType s, IReadOnlySet<string>? keep)
    {
        var own = new List<PyType>();
        if (s.Params != null) foreach (var p in s.Params) CollectTypeVars(p.Type, own);
        var all = new HashSet<string>(keep ?? (IReadOnlySet<string>)new HashSet<string>());
        foreach (var tv in own) all.Add(TypeParamName(tv));
        var ret = EraseTypeVars(s.Return, all);
        // a ParamSpec nothing solved: the callable takes any arguments, as Callable[..., R] does
        if (s.Spec != null && !all.Contains(s.Spec.Name)) return new SignatureType(null, ret);
        return s with { Params = s.Params?.Select(p => p with { Type = EraseTypeVars(p.Type, all) }).ToList(), Return = ret };
    }

    // ------------------------------------------------------------ members

    Dictionary<string, List<InstanceAttr>> InstanceAttributes(ClassInfo c)
    {
        if (_instanceAttrs.TryGetValue(c, out var d)) return d;
        d = new Dictionary<string, List<InstanceAttr>>();
        _instanceAttrs[c] = d;
        var m = c.Module;
        foreach (var fd in MethodsOf(c.Node.Body))
        {
            var fi = FunctionOf(m, fd);
            if (fi.IsStatic || fi.IsClassMethod) continue;
            var selfParam = fd.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal);
            if (selfParam == null) continue;
            foreach (var target in AssignedAttributes(fd.Body, selfParam.Name))
            {
                if (!d.TryGetValue(target.Attr, out var list)) d[target.Attr] = list = new();
                list.Add(new InstanceAttr(m, target, fi));
            }
        }
        return d;
    }

    static IEnumerable<FunctionDef> MethodsOf(IEnumerable<Stmt> body)
    {
        foreach (var s in body)
        {
            if (s is FunctionDef f) yield return f;
            else if (s is ClassDef) continue;
            else foreach (var n in AstChildren.Of(s).OfType<Stmt>().Concat(AstChildren.Of(s).OfType<ExceptHandler>().SelectMany(h => h.Body)))
                foreach (var inner in MethodsOf(new[] { n })) yield return inner;
        }
    }

    /// <summary>self.attr targets assigned anywhere in a method body (not in nested defs/classes), in the order of the source.</summary>
    static List<AttributeExpr> AssignedAttributes(IEnumerable<Node> nodes, string self)
    {
        var found = new List<AttributeExpr>();
        foreach (var n in nodes) AddAssignedAttributes(n, self, found);
        return found;
    }

    static void AddAssignedAttributes(Node n, string self, List<AttributeExpr> into)
    {
        if (n is FunctionDef or LambdaExpr or ClassDef) return;
        switch (n)
        {
            case AssignStmt a: foreach (var t in a.Targets) AddSelfAttributes(t, self, into); break;
            case AugAssignStmt a: AddSelfAttributes(a.Target, self, into); break;
            case AnnAssignStmt a: AddSelfAttributes(a.Target, self, into); break;
            case ForStmt f: AddSelfAttributes(f.Target, self, into); break;
            case WithItem { Target: { } target }: AddSelfAttributes(target, self, into); break;
        }
        foreach (var child in AstChildren.Of(n)) AddAssignedAttributes(child, self, into);
    }

    static void AddSelfAttributes(Expr target, string self, List<AttributeExpr> into)
    {
        foreach (var attr in FlattenTargets(target))
            if (attr is AttributeExpr { Value: NameExpr v } ae && v.Name == self) into.Add(ae);
    }

    static IEnumerable<Expr> FlattenTargets(Expr t)
    {
        switch (t)
        {
            case TupleExpr tu: foreach (var i in tu.Items) foreach (var x in FlattenTargets(i)) yield return x; break;
            case ListExpr l: foreach (var i in l.Items) foreach (var x in FlattenTargets(i)) yield return x; break;
            case StarExpr s: foreach (var x in FlattenTargets(s.Value)) yield return x; break;
            default: yield return t; break;
        }
    }

    /// <summary>Names listed in a class's <c>__slots__</c>; they are instance attributes without an assignment site.</summary>
    HashSet<string> SlotsOf(ClassInfo c)
    {
        if (_slots.TryGetValue(c, out var s)) return s;
        s = new HashSet<string>();
        _slots[c] = s;
        void Scan(IEnumerable<Stmt> body)
        {
            foreach (var st in body)
            {
                if (st is AssignStmt { Value: var v } a && a.Targets.Any(t => t is NameExpr { Name: "__slots__" }))
                {
                    IEnumerable<Expr> items = v switch
                    {
                        TupleExpr t => t.Items, ListExpr l => l.Items, ConstExpr => new[] { v }, _ => Array.Empty<Expr>(),
                    };
                    foreach (var it in items)
                        if (it is ConstExpr { Kind: ConstKind.Str } sc) s.Add(sc.Text.Trim('"', '\''));
                }
                else if (st is IfStmt or TryStmt)
                    Scan(AstChildren.Of(st).OfType<Stmt>());
            }
        }
        Scan(c.Node.Body);
        return s;
    }

    /// <summary>The metaclass in effect for a class: the nearest one declared along its MRO, else ABCMeta when a Protocol is in it.</summary>
    ClassType? MetaclassOf(ClassInfo c, out bool unknown)
    {
        unknown = false;
        bool protocol = false;
        foreach (var e in Mro(c))
        {
            if (e is not ClassType ec) { unknown = true; continue; }
            var d = ClassDataOf(ec.Info);
            if (d.Metaclass != null) return d.Metaclass; // an explicit one must derive from ABCMeta anyway when a Protocol is above it
            if (d.UnknownMetaclass) { unknown = true; return null; }
            protocol |= d.IsProtocol;
        }
        // typing._ProtocolMeta derives from ABCMeta; typeshed declares neither on Protocol (nor on Iterable, Mapping, ...)
        if (protocol && _ws.GetModule("abc") is { } abc) return ModuleClass(_abcClasses, abc, "ABCMeta");
        return null;
    }
    readonly Dictionary<string, ClassType?> _abcClasses = new();

    MemberHit? FindMember(ClassType cls, string name, bool instanceAccess, ClassInfo? startAfter, out bool unknownBase)
    {
        unknownBase = false;
        var map = TypeParamMap(cls.Info, cls.Args);
        bool skipping = startAfter != null;
        foreach (var e in Mro(cls.Info))
        {
            if (e is not ClassType entry) { unknownBase = true; continue; }
            if (skipping) { if (ReferenceEquals(entry.Info, startAfter)) skipping = false; continue; }

            Symbol? sym = null;
            if (entry.Info.Scope.Symbols.TryGetValue(name, out var s) && (s.Redirect ?? s).Declarations.Count > 0) sym = s.Redirect ?? s;
            IReadOnlyList<InstanceAttr> inst = Array.Empty<InstanceAttr>();
            if (instanceAccess && InstanceAttributes(entry.Info).TryGetValue(name, out var list)) inst = list;
            bool slot = instanceAccess && sym == null && inst.Count == 0 && SlotsOf(entry.Info).Contains(name);
            if (sym != null || inst.Count > 0 || slot)
                return new MemberHit((ClassType)Substitute(entry, map), sym, inst);
        }
        return null;
    }

    /// <summary>
    /// The type of a member seen through a receiver: class-level declarations (methods get bound, properties evaluated)
    /// plus the instance attributes assigned in methods (stored values, so functions in them are not bound).
    /// </summary>
    PyType BoundMember(MemberHit hit, PyType receiver, bool receiverIsInstance)
    {
        if (receiver is ClassType modelReceiver && DjangoBoundMember(hit, modelReceiver, receiverIsInstance) is { } fromDjango) return fromDjango;
        PyType? classLevel = hit.ClassSymbol != null ? TypeOfSymbol(hit.ClassSymbol) : null;
        if (classLevel != null && hit.ClassSymbol != null && IsStrongSymbol(hit.Owner.Info.Module, hit.ClassSymbol))
            return BindMember(classLevel, hit.Owner, receiver, receiverIsInstance);

        // `self.x: T = ...` declares the attribute: other assignments are checked against T, not unioned into it
        var annotated = hit.InstanceAttrs.Where(ia => ia.Module.ParentOf(ia.Target) is AnnAssignStmt an && ReferenceEquals(an.Target, ia.Target)).ToList();
        if (annotated.Count > 0)
            return PyType.Union(annotated.Select(ia => BindMember(TypeOfTarget(ia.Module, ia.Target), hit.Owner, receiver, receiverIsInstance, bindFunctions: false)).ToList());

        var parts = new List<PyType>();
        if (classLevel != null) parts.Add(BindMember(classLevel, hit.Owner, receiver, receiverIsInstance));
        foreach (var ia in hit.InstanceAttrs)
            parts.Add(BindMember(TypeOfTarget(ia.Module, ia.Target), hit.Owner, receiver, receiverIsInstance, bindFunctions: false));
        return parts.Count == 0 ? PyType.Unknown : PyType.Union(parts);
    }

    /// <summary>An overload whose first parameter has no annotation, or one the receiver is assignable to.</summary>
    bool SelfAccepts(FunctionType f, PyType receiver)
    {
        if (f.Info.Node is not FunctionDef { Params: [{ Annotation: { } ann }, ..] } || (f.Info.Flags & (FuncFlags.Static | FuncFlags.ClassMethod)) != 0) return true;
        var declared = AnnotationType(f.Info.Module, ann);
        return declared is not ClassType || IsAssignable(declared, receiver);
    }

    PyType TypeOfTarget(ModuleInfo m, Expr target) =>
        Memo(_targetCache, target, PyType.CycleUnknown, () => InferTargetType(m, target));

    bool IsStrongSymbol(ModuleInfo m, Symbol s) => s.Declarations.Any(d =>
        d.Kind is DeclKind.Function or DeclKind.Class or DeclKind.Parameter
        || (d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AnnAssignStmt));

    Dictionary<string, PyType> SubstFor(ClassType owner, PyType receiver)
    {
        var map = TypeParamMap(owner.Info, owner.Args);
        // Self is the receiver's class, not the specific enum member it happens to be (Flag.__or__ returns the Flag type)
        map["Self"] = receiver is ClassType rc ? rc.AsInstance().Widened() : PyType.Unknown;
        map["type[Self]"] = receiver is ClassType rc2 ? rc2.AsClassObject() : PyType.Unknown;
        return map;
    }

    /// <summary>Applies descriptor behaviour: methods are bound to the receiver, properties are evaluated, type variables substituted.</summary>
    PyType BindMember(PyType raw, ClassType owner, PyType receiver, bool receiverIsInstance, bool bindFunctions = true)
    {
        var subst = SubstFor(owner, receiver);
        return MapMembers(raw, t =>
        {
            if (!bindFunctions) return Substitute(t, subst);
            switch (t)
            {
                case FunctionType f: return BindFunction(f, subst, receiverIsInstance);
                case OverloadedType o:
                {
                    // overloads that pick by an explicit self (`def get(self: Box[int]) -> int`): those the receiver is not are left out
                    var fitting = receiverIsInstance && receiver is ClassType ? o.Overloads.Where(f => SelfAccepts(f, receiver)).ToList() : o.Overloads.ToList();
                    if (fitting.Count == 0) fitting = o.Overloads.ToList();
                    return new OverloadedType(fitting.Select(f => (FunctionType)BindFunction(f, subst, receiverIsInstance, allowProperty: false)).ToList());
                }
                case SignatureType { Origin.BoundFirst: false } s: return BindSignature(s, subst, receiverIsInstance);
                default: return Substitute(t, subst);
            }
        });
    }

    /// <summary>
    /// A decorated method whose parameters a ParamSpec carried over (`@contextmanager def m(self, n)`): the decorator's
    /// result is bound like the def would be, so through an instance its first parameter is the receiver.
    /// </summary>
    PyType BindSignature(SignatureType s, IReadOnlyDictionary<string, PyType> subst, bool receiverIsInstance)
    {
        var origin = s.Origin!;
        var sig = (SignatureType)Substitute(s, subst);
        if (origin.Info.IsStatic) return sig;
        if (origin.Info.IsProperty && (origin.Info.Flags & FuncFlags.PropertySetter) == 0) return receiverIsInstance ? sig.Return : sig;
        if (!receiverIsInstance && !origin.Info.IsClassMethod) return sig;
        var ps = sig.Params?.ToList();
        if (ps is [{ Category: ParamCategory.PositionalOnly or ParamCategory.Normal }, ..]) ps.RemoveAt(0);
        return sig with { Params = ps, Origin = origin with { BoundFirst = true } };
    }

    PyType BindFunction(FunctionType f, IReadOnlyDictionary<string, PyType> subst, bool receiverIsInstance, bool allowProperty = true)
    {
        var merged = MergeSubst(f.Subst, subst);
        if (f.Info.IsStatic) return f with { Subst = merged };
        if (f.Info.IsClassMethod) return f with { BoundFirst = true, Subst = merged };
        if (allowProperty && f.Info.IsProperty && (f.Info.Flags & FuncFlags.PropertySetter) == 0)
            return receiverIsInstance ? ReturnTypeOf(f with { BoundFirst = true, Subst = merged }) : f;
        return receiverIsInstance ? f with { BoundFirst = true, Subst = merged } : f with { Subst = merged };
    }

    // ------------------------------------------------------------ attribute access

    /// <summary>Type of <c>receiver.name</c> together with details useful for diagnostics and navigation.</summary>
    public AttributeResult AttributeType(PyType receiver, string name)
    {
        var types = new List<PyType>();
        var missing = new List<PyType>();
        var decls = new List<Declaration>();
        var sites = new List<Node>();

        foreach (var member in receiver.Members)
        {
            switch (member)
            {
                case UnknownType: types.Add(member); break;

                case ClassType c:
                    if (TryEnumAttribute(c, name, types)) break;
                    LookupOnClass(c, name, c.IsInstance, types, missing, decls, sites);
                    break;

                case ModuleType mt: ModuleMember(mt, name, types, missing, decls); break;

                case FunctionType { Info.IsProperty: true }:
                {
                    // `@x.setter`: inside the class body x is the property object, not a plain function
                    var prop = BuiltinClass("property");
                    if (prop != null) LookupOnClass(prop.AsInstance(), name, true, types, missing, decls, sites);
                    else types.Add(PyType.Unknown);
                    break;
                }

                case FunctionType or OverloadedType or SignatureType:
                {
                    // builtins.function lacks __call__ (types.FunctionType has it): calling it is calling the function itself
                    if (name == "__call__") { types.Add(member); break; }
                    var fn = BuiltinClass("function");
                    if (fn != null) LookupOnClass(fn.AsInstance(), name, true, types, missing, decls, sites);
                    else types.Add(PyType.Unknown);
                    break;
                }

                case SuperType sup:
                {
                    var recvClass = sup.Receiver is ClassType rc ? rc : null;
                    if (recvClass == null) { types.Add(PyType.Unknown); break; }
                    if (name is "__init__" or "__new__" && SynthesizedMember(recvClass, name, sup.Current) is PyType synth)
                    {
                        types.Add(synth);
                        break;
                    }
                    var hit = FindMember(recvClass, name, recvClass.IsInstance, sup.Current, out bool unk);
                    if (hit == null) { types.Add(PyType.Unknown); break; }
                    types.Add(BoundMember(hit, recvClass, recvClass.IsInstance));
                    if (hit.ClassSymbol != null) decls.AddRange(hit.ClassSymbol.Declarations);
                    break;
                }

                default: types.Add(PyType.Unknown); break;
            }
        }

        var type = types.Count == 0 ? PyType.Unknown : PyType.Union(types);
        return new AttributeResult(type, missing, decls, sites);
    }

    void LookupOnClass(ClassType c, string name, bool instance, List<PyType> types, List<PyType> missing,
        List<Declaration> decls, List<Node> sites)
    {
        if (name is "__init__" or "__new__" && SynthesizedMember(c, name, null) is PyType synthesizedMember)
        {
            types.Add(synthesizedMember);
            return;
        }
        if (name is "instance" or "save" or "get_object" or "get_serializer" && DjangoMetaMember(c, name, instance) is { } formMember) { types.Add(formMember); return; }
        if (name == "cleaned_data" && DjangoCleanedData(c, instance) is { } cleaned) { types.Add(cleaned); return; }
        var hit = FindMember(c, name, instance, null, out bool unknownBase);
        if (hit != null)
        {
            types.Add(BoundMember(hit, c, instance));
            if (hit.ClassSymbol != null) decls.AddRange(hit.ClassSymbol.Declarations);
            sites.AddRange(hit.InstanceAttrs.Select(a => (Node)a.Target));
            return;
        }
        // what Django's metaclass adds to a model (objects, DoesNotExist, id, reverse relations), and the project's settings
        if (IsDjangoModel(c.Info) && DjangoImplicitMember(c, name, instance) is { } djangoMember) { types.Add(djangoMember); return; }
        if (instance && IsDjangoSettingsObject(c.Info) && DjangoSettingSymbol(name) is { } setting)
        {
            types.Add(TypeOfSymbol(setting));
            decls.AddRange(setting.Declarations);
            return;
        }
        // the fields attrs' these={...} gives the class
        if (TheseMember(c, name, instance) is { } theseMember) { types.Add(theseMember); return; }
        if (unknownBase) { types.Add(PyType.Unknown); return; }
        // type[Any]: whatever the class turns out to be may have it
        if (IsClassOfAny(c)) { types.Add(c.Args[0]); return; }

        if (instance)
        {
            var ga = FindMember(c, "__getattr__", true, null, out _);
            if (ga != null)
            {
                var bound = BoundMember(ga, c, true);
                types.Add(CallResultNoArgs(bound));
                return;
            }
            // a __getattribute__ of its own (threading.local, SimpleNamespace, proxies) can answer any name; object's cannot
            var gab = FindMember(c, "__getattribute__", true, null, out _);
            if (gab != null && !(gab.Owner.Info.Name == "object" && gab.Owner.Info.Module.Name == "builtins"))
            {
                types.Add(CallResultNoArgs(BoundMember(gab, c, true)));
                return;
            }
        }
        else
        {
            // class objects expose what their metaclass offers, and what `type` offers (__name__, mro, ...)
            var meta = MetaclassOf(c.Info, out bool unknownMeta);
            if (meta != null)
            {
                var mh = FindMember(meta.AsInstance(), name, true, null, out bool unkMeta);
                if (mh != null) { types.Add(BoundMember(mh, c, true)); return; }
                unknownMeta |= unkMeta;
            }
            if (unknownMeta) { types.Add(PyType.Unknown); return; }

            var typeClass = BuiltinClass("type");
            if (typeClass != null && !ReferenceEquals(c.Info, typeClass.Info))
            {
                var th = FindMember(typeClass.AsInstance(), name, true, null, out _);
                if (th != null) { types.Add(BoundMember(th, typeClass.AsInstance(), true)); return; }
            }
        }
        missing.Add(c);
    }

    void ModuleMember(ModuleType mt, string name, List<PyType> types, List<PyType> missing, List<Declaration> decls)
    {
        var scope = mt.Module.Semantics.ModuleScope;
        var found = FindModuleSymbol(mt.Module, name);
        if (found != null)
        {
            types.Add(TypeOfSymbol(found));
            decls.AddRange(found.Declarations);
            return;
        }
        var sub = _ws.GetModule(mt.Module.Name + "." + name);
        if (sub != null) { types.Add(new ModuleType(sub)); return; }
        if (scope.Symbols.ContainsKey("__getattr__") || scope.HasStarImport) { types.Add(PyType.Unknown); return; }
        // what every module object has (__file__, __path__, __spec__, ...): typeshed keeps it in types.ModuleType, the bundled builtins stub in `module`
        var typesModule = _ws.GetModule("types");
        var modClass = BuiltinClass("module") ?? (typesModule == null ? null : ModuleClass(_typesClasses, typesModule, "ModuleType"));
        if (modClass != null)
        {
            var hit = FindMember(modClass.AsInstance(), name, true, null, out _);
            if (hit != null) { types.Add(BoundMember(hit, modClass.AsInstance(), true)); return; }
        }
        missing.Add(mt);
    }
}
