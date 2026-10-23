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
        public List<TypeVarType> TypeParams { get; } = new();
        public List<PyType> Bases { get; } = new();   // ClassType (class object form) or Unknown
        public bool IsProtocol { get; set; }
        public bool HasUnknownBase { get; set; }
        /// <summary>Class given as metaclass=... on this class (not inherited).</summary>
        public ClassType? Metaclass { get; set; }
        public bool UnknownMetaclass { get; set; }
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

    public IReadOnlyList<TypeVarType> TypeParametersOf(ClassInfo c) => ClassDataOf(c).TypeParams;
    public bool IsProtocol(ClassInfo c) => ClassDataOf(c).IsProtocol;

    ClassData ClassDataOf(ClassInfo c)
    {
        if (_classData.TryGetValue(c, out var d)) return d;
        d = new ClassData();
        if (!_classDataActive.Add(c)) return d; // inheritance cycle: give an empty answer, do not cache
        try
        {
            var m = c.Module;
            bool explicitParams = false;

            foreach (var tp in c.Node.TypeParams) { d.TypeParams.Add(new TypeVarType(tp.Name)); explicitParams = true; }

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
                                if (Annotation(m, a, null) is TypeVarType tv && !d.TypeParams.Contains(tv)) d.TypeParams.Add(tv);
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

    static void CollectTypeVars(PyType t, List<TypeVarType> into)
    {
        switch (t)
        {
            case TypeVarType tv when tv.Name != "Self": if (!into.Contains(tv)) into.Add(tv); break;
            case ClassType c:
                foreach (var a in c.Args) CollectTypeVars(a, into);
                break;
            case UnionType u: foreach (var x in u.MemberList) CollectTypeVars(x, into); break;
            case SignatureType s:
                if (s.Params != null) foreach (var p in s.Params) CollectTypeVars(p.Type, into);
                CollectTypeVars(s.Return, into);
                break;
        }
    }

    IReadOnlyList<PyType> ClassTypeParamsAsArgs(ClassInfo c) => ClassDataOf(c).TypeParams.Cast<PyType>().ToList();

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
                    if (e is ClassType ec) seq.Add((ClassType)Substitute(ec, map));
                    else unknown = true;
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
        for (int i = 0; i < ps.Count; i++) map[ps[i].Name] = i < args.Count ? args[i] : PyType.Unknown;
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
            case ClassType c:
            {
                if (c.Args.Count == 0 && c.TupleItems == null) return c;
                var args = c.Args.Select(a => Substitute(a, map)).ToList();
                var items = c.TupleItems?.Select(a => Substitute(a, map)).ToList();
                return new ClassType(c.Info, args, c.IsInstance, items);
            }
            case UnionType u: return PyType.Union(u.MemberList.Select(x => Substitute(x, map)));
            case SignatureType s:
                return new SignatureType(s.Params?.Select(p => p with { Type = Substitute(p.Type, map) }).ToList(), Substitute(s.Return, map));
            case FunctionType f:
                return f with { Subst = MergeSubst(f.Subst, map) };
            default: return t;
        }
    }

    static IReadOnlyDictionary<string, PyType> MergeSubst(IReadOnlyDictionary<string, PyType>? a, IReadOnlyDictionary<string, PyType> b)
    {
        if (a == null || a.Count == 0) return b;
        var d = new Dictionary<string, PyType>(a);
        foreach (var kv in b) d.TryAdd(kv.Key, kv.Value);
        return d;
    }

    /// <summary>Type variables left unsolved become Unknown.</summary>
    PyType EraseTypeVars(PyType t) => t switch
    {
        TypeVarType => PyType.Unknown,
        ClassType c when c.Args.Count > 0 || c.TupleItems != null =>
            new ClassType(c.Info, c.Args.Select(EraseTypeVars).ToList(), c.IsInstance, c.TupleItems?.Select(EraseTypeVars).ToList()),
        UnionType u => PyType.Union(u.MemberList.Select(EraseTypeVars)),
        SignatureType s => new SignatureType(s.Params?.Select(p => p with { Type = EraseTypeVars(p.Type) }).ToList(), EraseTypeVars(s.Return)),
        _ => t,
    };

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

    /// <summary>self.attr targets assigned anywhere in a method body (not in nested defs/classes).</summary>
    static IEnumerable<AttributeExpr> AssignedAttributes(IEnumerable<Node> nodes, string self)
    {
        foreach (var n in nodes)
        {
            if (n is FunctionDef or LambdaExpr or ClassDef) continue;
            IEnumerable<Expr> targets = n switch
            {
                AssignStmt a => a.Targets,
                AugAssignStmt a => new[] { a.Target },
                AnnAssignStmt a => new[] { a.Target },
                ForStmt f => new[] { f.Target },
                WithItem w when w.Target != null => new[] { w.Target },
                _ => Array.Empty<Expr>(),
            };
            foreach (var t in targets)
                foreach (var attr in FlattenTargets(t))
                    if (attr is AttributeExpr { Value: NameExpr v } ae && v.Name == self) yield return ae;
            foreach (var inner in AssignedAttributes(AstChildren.Of(n), self)) yield return inner;
        }
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

    /// <summary>The metaclass in effect for a class: the nearest one declared along its MRO.</summary>
    ClassType? MetaclassOf(ClassInfo c, out bool unknown)
    {
        unknown = false;
        foreach (var e in Mro(c))
        {
            if (e is not ClassType ec) { unknown = true; continue; }
            var d = ClassDataOf(ec.Info);
            if (d.Metaclass != null) return d.Metaclass;
            if (d.UnknownMetaclass) { unknown = true; return null; }
        }
        return null;
    }

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
        PyType? classLevel = hit.ClassSymbol != null ? TypeOfSymbol(hit.ClassSymbol) : null;
        if (classLevel != null && hit.ClassSymbol != null && IsStrongSymbol(hit.Owner.Info.Module, hit.ClassSymbol))
            return BindMember(classLevel, hit.Owner, receiver, receiverIsInstance);

        var parts = new List<PyType>();
        if (classLevel != null) parts.Add(BindMember(classLevel, hit.Owner, receiver, receiverIsInstance));
        foreach (var ia in hit.InstanceAttrs)
            parts.Add(BindMember(TypeOfTarget(ia.Module, ia.Target), hit.Owner, receiver, receiverIsInstance, bindFunctions: false));
        return parts.Count == 0 ? PyType.Unknown : PyType.Union(parts);
    }

    PyType TypeOfTarget(ModuleInfo m, Expr target) =>
        Memo(_targetCache, target, PyType.CycleUnknown, () => InferTargetType(m, target));

    bool IsStrongSymbol(ModuleInfo m, Symbol s) => s.Declarations.Any(d =>
        d.Kind is DeclKind.Function or DeclKind.Class or DeclKind.Parameter
        || (d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AnnAssignStmt));

    Dictionary<string, PyType> SubstFor(ClassType owner, PyType receiver)
    {
        var map = TypeParamMap(owner.Info, owner.Args);
        map["Self"] = receiver is ClassType rc ? rc.AsInstance() : PyType.Unknown;
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
                    return new OverloadedType(o.Overloads.Select(f => (FunctionType)BindFunction(f, subst, receiverIsInstance, allowProperty: false)).ToList());
                default: return Substitute(t, subst);
            }
        });
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
                case UnknownType: types.Add(PyType.Unknown); break;

                case ClassType c:
                    LookupOnClass(c, name, c.IsInstance, types, missing, decls, sites);
                    break;

                case ModuleType mt: ModuleMember(mt, name, types, missing, decls); break;

                case FunctionType or OverloadedType or SignatureType:
                {
                    var fn = BuiltinClass("function");
                    if (fn != null) LookupOnClass(fn.AsInstance(), name, true, types, missing, decls, sites);
                    else types.Add(PyType.Unknown);
                    break;
                }

                case SuperType sup:
                {
                    var recvClass = sup.Receiver is ClassType rc ? rc : null;
                    if (recvClass == null) { types.Add(PyType.Unknown); break; }
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
        var hit = FindMember(c, name, instance, null, out bool unknownBase);
        if (hit != null)
        {
            types.Add(BoundMember(hit, c, instance));
            if (hit.ClassSymbol != null) decls.AddRange(hit.ClassSymbol.Declarations);
            sites.AddRange(hit.InstanceAttrs.Select(a => (Node)a.Target));
            return;
        }
        if (unknownBase) { types.Add(PyType.Unknown); return; }

        if (instance)
        {
            var ga = FindMember(c, "__getattr__", true, null, out _);
            if (ga != null)
            {
                var bound = BoundMember(ga, c, true);
                types.Add(CallResultNoArgs(bound));
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
        if (scope.Symbols.TryGetValue(name, out var s) && (s.Redirect ?? s).Declarations.Count > 0)
        {
            var sym = s.Redirect ?? s;
            types.Add(TypeOfSymbol(sym));
            decls.AddRange(sym.Declarations);
            return;
        }
        var sub = _ws.GetModule(mt.Module.Name + "." + name);
        if (sub != null) { types.Add(new ModuleType(sub)); return; }
        if (scope.Symbols.ContainsKey("__getattr__") || scope.HasStarImport) { types.Add(PyType.Unknown); return; }
        var modClass = BuiltinClass("module");
        if (modClass != null)
        {
            var hit = FindMember(modClass.AsInstance(), name, true, null, out _);
            if (hit != null) { types.Add(BoundMember(hit, modClass.AsInstance(), true)); return; }
        }
        missing.Add(mt);
    }
}
