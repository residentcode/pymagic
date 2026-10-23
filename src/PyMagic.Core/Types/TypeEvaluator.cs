using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>
/// Computes types of expressions, declarations, classes and functions on demand and caches the results.
/// Evaluation is lazy and may be re-entrant (a variable can depend on itself through a loop); such cycles are cut
/// to Unknown and the incomplete intermediate results are not cached.
/// </summary>
public sealed partial class TypeEvaluator
{
    readonly Workspace _ws;

    readonly Dictionary<object, PyType> _cache = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> _active = new(ReferenceEqualityComparer.Instance);
    readonly HashSet<object> _open = new(ReferenceEqualityComparer.Instance);   // active keys that some cut result depends on

    readonly Dictionary<Node, ClassInfo> _classes = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<Node, FunctionInfo> _functions = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<string, ClassType?> _builtinClasses = new();
    readonly Dictionary<string, ClassType?> _typingClasses = new();

    internal TypeEvaluator(Workspace ws) { _ws = ws; }

    public Workspace Workspace => _ws;

    /// <summary>Memoises a computation with cycle cutting; see class comment.</summary>
    T Memo<T>(Dictionary<object, T> cache, object key, T cycleValue, Func<T> compute)
    {
        if (cache.TryGetValue(key, out var hit)) return hit;
        if (!_active.Add(key)) { _open.Add(key); return cycleValue; }
        T result;
        try { result = compute(); }
        finally { _active.Remove(key); }
        _open.Remove(key);
        if (_open.Count == 0) cache[key] = result;
        return result;
    }

    PyType Memo(object key, Func<PyType> compute) => Memo(_cache, key, PyType.CycleUnknown, compute);

    // ------------------------------------------------------------ builtin lookups

    PyType NoneInstance => Inst("NoneType");

    /// <summary>Instance of a class from the builtins stub, e.g. Inst("list", elem). Unknown if the stub lacks it.</summary>
    PyType Inst(string name, params PyType[] args)
    {
        var c = BuiltinClass(name);
        return c == null ? PyType.Unknown : new ClassType(c.Info, args, true);
    }

    ClassType? BuiltinClass(string name) => ModuleClass(_builtinClasses, _ws.Builtins, name);
    ClassType? TypingClass(string name) => ModuleClass(_typingClasses, _ws.Typing, name);

    ClassType? ModuleClass(Dictionary<string, ClassType?> cache, ModuleInfo module, string name)
    {
        if (cache.TryGetValue(name, out var c)) return c;
        ClassType? result = null;
        if (module.Semantics.ModuleScope.Symbols.TryGetValue(name, out var sym))
            foreach (var d in sym.Declarations)
                if (d.Kind == DeclKind.Class && d.Node is ClassDef cd) { result = new ClassType(ClassOf(module, cd), false); break; }
        cache[name] = result;
        return result;
    }

    internal ClassInfo ClassOf(ModuleInfo m, ClassDef node)
    {
        if (_classes.TryGetValue(node, out var ci)) return ci;
        var scope = m.Semantics.ScopeOf(node)!;
        return _classes[node] = new ClassInfo(node.Name, m, node, scope);
    }

    internal FunctionInfo FunctionOf(ModuleInfo m, FunctionDef node)
    {
        if (_functions.TryGetValue(node, out var fi)) return fi;
        var scope = m.Semantics.ScopeOf(node)!;
        fi = new FunctionInfo(node.Name, m, node, scope) { Flags = DecoratorFlags(node) };
        if (node.IsAsync) fi.Flags |= FuncFlags.Async;
        if (ContainsYield(node.Body)) fi.Flags |= FuncFlags.Generator;
        // owner: nearest enclosing class without crossing another function
        for (var p = m.ParentOf(node); p != null; p = m.ParentOf(p))
        {
            if (p is ClassDef cd) { fi.Owner = ClassOf(m, cd); break; }
            if (p is FunctionDef or LambdaExpr) break;
        }
        return _functions[node] = fi;
    }

    FunctionInfo LambdaOf(ModuleInfo m, LambdaExpr node)
    {
        if (_functions.TryGetValue(node, out var fi)) return fi;
        var scope = m.Semantics.ScopeOf(node)!;
        return _functions[node] = new FunctionInfo("<lambda>", m, node, scope);
    }

    static FuncFlags DecoratorFlags(FunctionDef f)
    {
        var flags = FuncFlags.None;
        // implicit static/class methods
        if (f.Name == "__new__") flags |= FuncFlags.Static;
        if (f.Name is "__init_subclass__" or "__class_getitem__") flags |= FuncFlags.ClassMethod;
        foreach (var d in f.Decorators)
        {
            string? name = d switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
            switch (name)
            {
                case "staticmethod": flags |= FuncFlags.Static; break;
                case "classmethod": flags |= FuncFlags.ClassMethod; break;
                case "property": case "cached_property": flags |= FuncFlags.Property; break;
                case "setter": case "deleter": flags |= FuncFlags.PropertySetter; break;
                case "getter": flags |= FuncFlags.Property; break;
                case "abstractmethod": flags |= FuncFlags.Abstract; break;
                case "overload": flags |= FuncFlags.Overload; break;
            }
        }
        return flags;
    }

    static bool ContainsYield(IEnumerable<Node> nodes)
    {
        foreach (var n in nodes)
        {
            if (n is FunctionDef or LambdaExpr or ClassDef) continue;
            if (n is YieldExpr) return true;
            if (ContainsYield(AstChildren.Of(n))) return true;
        }
        return false;
    }

    // ------------------------------------------------------------ expressions

    /// <summary>The type of an expression.</summary>
    public PyType TypeOf(ModuleInfo m, Expr e) => Memo(e, () => Compute(m, e));

    PyType Compute(ModuleInfo m, Expr e)
    {
        switch (e)
        {
            case ConstExpr c:
                return c.Kind switch
                {
                    ConstKind.Int => Inst("int"),
                    ConstKind.Float => Inst("float"),
                    ConstKind.Imaginary => Inst("complex"),
                    ConstKind.Str => Inst("str"),
                    ConstKind.Bytes => Inst("bytes"),
                    ConstKind.True or ConstKind.False => Inst("bool"),
                    ConstKind.None => NoneInstance,
                    _ => Inst("ellipsis"),
                };

            case NameExpr n: return TypeOfName(m, n);
            case AttributeExpr a: return NarrowedAttribute(m, a);
            case CallExpr c: return TypeOfCall(m, c);
            case SubscriptExpr s: return TypeOfSubscript(m, s);

            case TupleExpr t: return TupleOf(t.Items.Select(i => i is StarExpr ? PyType.Unknown : TypeOf(m, i)).ToList());
            case ListExpr l: return Inst("list", ElementUnion(m, l.Items));
            case SetExpr s: return Inst("set", ElementUnion(m, s.Items));
            case DictExpr d:
            {
                var ks = new List<PyType>();
                var vs = new List<PyType>();
                for (int i = 0; i < d.Values.Count; i++)
                {
                    if (d.Keys[i] == null) { ks.Add(PyType.Unknown); vs.Add(PyType.Unknown); }
                    else { ks.Add(TypeOf(m, d.Keys[i]!)); vs.Add(TypeOf(m, d.Values[i])); }
                }
                return Inst("dict", ks.Count == 0 ? PyType.Unknown : PyType.Union(ks), vs.Count == 0 ? PyType.Unknown : PyType.Union(vs));
            }
            case CompExpr c:
            {
                var el = TypeOf(m, c.Element);
                return c.Kind switch
                {
                    CompKind.List => Inst("list", el),
                    CompKind.Set => Inst("set", el),
                    CompKind.Dict => Inst("dict", el, c.Value == null ? PyType.Unknown : TypeOf(m, c.Value)),
                    _ => TypingInst("Generator", el, NoneInstance, NoneInstance),
                };
            }

            case LambdaExpr l: return new FunctionType(LambdaOf(m, l), false);
            case IfExpr i: return PyType.Union(TypeOf(m, i.Body), TypeOf(m, i.Orelse));

            case BinaryExpr { Op: "and" } b: return PyType.Union(TypeOf(m, b.Left), TypeOf(m, b.Right));
            case BinaryExpr { Op: "or" } b: return PyType.Union(WithoutNone(TypeOf(m, b.Left)), TypeOf(m, b.Right));
            case BinaryExpr b: return BinaryOp(TypeOf(m, b.Left), b.Op, TypeOf(m, b.Right));

            case UnaryExpr { Op: "not" }: return Inst("bool");
            case UnaryExpr u:
            {
                var operand = TypeOf(m, u.Operand);
                string method = u.Op switch { "-" => "__neg__", "+" => "__pos__", _ => "__invert__" };
                return MapMembers(operand, t => CallMethod(t, method, Array.Empty<PyType>(), out var ok) ?? PyType.Unknown);
            }

            case CompareExpr: return Inst("bool");
            case AwaitExpr aw: return AwaitedType(TypeOf(m, aw.Value));
            case NamedExpr w: return TypeOf(m, w.Value);
            case SliceExpr: return Inst("slice");
            default: return PyType.Unknown; // yield, star, error expressions
        }
    }

    PyType ElementUnion(ModuleInfo m, IReadOnlyList<Expr> items)
    {
        if (items.Count == 0) return PyType.Unknown;
        return PyType.Union(items.Select(i => i is StarExpr s ? IterElement(TypeOf(m, s.Value)) : TypeOf(m, i)));
    }

    PyType TupleOf(IReadOnlyList<PyType> items)
    {
        var c = BuiltinClass("tuple");
        if (c == null) return PyType.Unknown;
        return new ClassType(c.Info, new[] { items.Count == 0 ? PyType.Unknown : PyType.Union(items) }, true, items);
    }

    PyType TypingInst(string name, params PyType[] args)
    {
        var c = TypingClass(name);
        return c == null ? PyType.Unknown : new ClassType(c.Info, args, true);
    }

    static PyType WithoutNone(PyType t) => PyType.Union(t.Members.Where(x => !IsNone(x)));

    static bool IsNone(PyType t) => t is ClassType { IsInstance: true, Info: { Name: "NoneType" } } c && c.Info.Module.Name == "builtins";

    /// <summary>Applies f to every member of a union and unions the results.</summary>
    PyType MapMembers(PyType t, Func<PyType, PyType> f) =>
        t is UnionType u ? PyType.Union(u.MemberList.Select(f)) : f(t);

    // ------------------------------------------------------------ names, symbols, declarations

    PyType TypeOfName(ModuleInfo m, NameExpr n)
    {
        var r = m.Semantics.ReferenceOf(n);
        if (r == null) return PyType.Unknown;
        if (r.Symbol == null)
        {
            // a star import may shadow the builtin of the same name (os.py: `from posix import *` provides open)
            if (r.IsBuiltin && !m.Semantics.ModuleScope.HasStarImport) return BuiltinNameType(n.Name);
            return PyType.Unknown;
        }

        var sym = r.Symbol;
        var symModule = _ws.ModuleOf(sym) ?? m;

        if ((r.Access & Access.Read) != 0)
        {
            // flow-sensitive: only the assignments that reach this use, narrowed by the conditions on the way
            var flowType = FlowTypeOfName(m, n, r, sym);
            if (flowType != null) return flowType;
        }
        else
        {
            foreach (var d in sym.Declarations)
                if (ReferenceEquals(d.Node, n)) return TypeOfDeclaration(symModule, d);
        }

        var declared = DeclaredType(symModule, sym);
        if (declared != null) return declared;

        // locals: only the assignments that can reach this use
        if ((r.Access & Access.Read) != 0 && ReferenceEquals(sym.Scope, r.UseScope))
        {
            var reach = m.Flow.ReachingDefinitions(n);
            if (reach != null)
                return reach.Definitions.Count == 0 ? PyType.Unknown : PyType.Union(reach.Definitions.Select(d => TypeOfDeclaration(symModule, d)));
        }
        return TypeOfSymbol(sym);
    }

    PyType BuiltinNameType(string name)
    {
        var scope = _ws.Builtins.Semantics.ModuleScope;
        return scope.Symbols.TryGetValue(name, out var s) ? TypeOfSymbol(s) : PyType.Unknown;
    }

    /// <summary>Type of a symbol seen from outside its own flow: an annotation if there is one, else all its declarations.</summary>
    public PyType TypeOfSymbol(Symbol sym)
    {
        var s = sym.Redirect ?? sym;
        return Memo(s, () =>
        {
            var m = _ws.ModuleOf(s);
            if (m == null)
            {
                // symbols of synthetic classes (namedtuple fields) carry their types directly
                var known = s.Declarations.Where(d => _declOverride.ContainsKey(d)).Select(d => _declOverride[d]).ToList();
                return known.Count == 0 ? PyType.Unknown : PyType.Union(known);
            }
            if (s.Declarations.Count == 0) return PyType.Unknown;
            return DeclaredType(m, s) ?? CombineDeclarations(m, s.Declarations);
        });
    }

    /// <summary>Declarations of one symbol combined into one type; overloaded defs become an OverloadedType.</summary>
    PyType CombineDeclarations(ModuleInfo m, IReadOnlyList<Declaration> decls)
    {
        var types = decls.Select(d => TypeOfDeclaration(m, d)).ToList();
        var overloads = types.OfType<FunctionType>().Where(f => (f.Info.Flags & FuncFlags.Overload) != 0).ToList();
        if (overloads.Count > 0) return new OverloadedType(overloads);
        // property: prefer the getter over setters/deleters
        var getter = types.OfType<FunctionType>().FirstOrDefault(f => f.Info.IsProperty && (f.Info.Flags & FuncFlags.PropertySetter) == 0);
        if (getter != null) return getter;
        // a def or class that appears once wins over weaker evidence
        return PyType.Union(types);
    }

    /// <summary>A variable's annotation (or the special form it stands for in typing), if it has one.</summary>
    PyType? DeclaredType(ModuleInfo m, Symbol sym)
    {
        foreach (var d in sym.Declarations)
        {
            if (d.Kind == DeclKind.Parameter && d.Node is Parameter { Annotation: not null })
                return TypeOfDeclaration(m, d);
            if (d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AnnAssignStmt a && ReferenceEquals(a.Target, d.Node))
            {
                if (m.Name == "typing" && a.Annotation is NameExpr { Name: "_SpecialForm" })
                    return new SpecialFormType(sym.Name);
                return AnnotationType(m, a.Annotation);
            }
        }
        return null;
    }

    PyType TypeOfDeclaration(ModuleInfo m, Declaration d) => Memo(d, () =>
    {
        if (_declOverride.TryGetValue(d, out var overridden)) return overridden;
        switch (d.Kind)
        {
            case DeclKind.Class: return new ClassType(ClassOf(m, (ClassDef)d.Node), false);
            case DeclKind.Function: return new FunctionType(FunctionOf(m, (FunctionDef)d.Node), false);
            case DeclKind.Parameter: return ParameterType(m, (Parameter)d.Node);
            case DeclKind.Variable: return d.Node is Expr target ? InferTargetType(m, target) : PyType.Unknown;
            case DeclKind.Import: return ImportType(m, (ImportAlias)d.Node);
            case DeclKind.TypeParam: return new TypeVarType(((TypeParam)d.Node).Name);
            case DeclKind.ExceptionVar:
            {
                var h = (ExceptHandler)d.Node;
                return h.Type == null ? PyType.Unknown : ExceptionInstance(TypeOf(m, h.Type));
            }
            default: return PyType.Unknown;
        }
    });

    PyType ExceptionInstance(PyType classOrTuple) => MapMembers(classOrTuple, t => t switch
    {
        ClassType { IsInstance: false } c => c.AsInstance(),
        ClassType { IsInstance: true, TupleItems: not null } tup => PyType.Union(tup.TupleItems.Select(ExceptionInstance)),
        _ => PyType.Unknown,
    });

    /// <summary>The type a parameter has inside its function body.</summary>
    PyType ParameterType(ModuleInfo m, Parameter p)
    {
        var owner = m.ParentOf(p);
        FunctionInfo? fn = owner switch
        {
            FunctionDef fd => FunctionOf(m, fd),
            LambdaExpr l => LambdaOf(m, l),
            _ => null,
        };

        // self / cls
        if (fn?.Owner != null && owner is FunctionDef def && !fn.IsStatic && p.Annotation == null && p.Kind == ParamKind.Normal)
        {
            var first = def.Params.FirstOrDefault(x => x.Kind is ParamKind.Normal or ParamKind.VarArgs or ParamKind.KwArgs);
            // by convention only these names mean "the receiver"; a def in a class body with another first parameter
            // is usually a helper called while the class is being built (fractions._operator_fallbacks)
            if (ReferenceEquals(first, p) && p.Kind == ParamKind.Normal && (fn.IsClassMethod || IsReceiverName(p.Name)))
            {
                var self = new ClassType(fn.Owner, ClassTypeParamsAsArgs(fn.Owner), true);
                return fn.IsClassMethod ? self.AsClassObject() : self;
            }
        }

        PyType t;
        if (p.Annotation != null) t = AnnotationType(m, p.Annotation);
        else if (p.Default is ConstExpr { Kind: ConstKind.None }) t = PyType.Unknown; // `x=None` conventionally means "optional anything"
        else if (p.Default != null) t = TypeOf(m, p.Default);
        else t = PyType.Unknown;

        return p.Kind switch
        {
            ParamKind.VarArgs => TupleHomogeneous(p.Annotation != null ? t : PyType.Unknown),
            ParamKind.KwArgs => Inst("dict", Inst("str"), p.Annotation != null ? t : PyType.Unknown),
            _ => t,
        };
    }

    static bool IsReceiverName(string name) =>
        name is "self" or "cls" or "mcs" or "mcls" or "metacls" or "klass" or "meta" or "this" or "_self" or "_cls";

    PyType TupleHomogeneous(PyType element)
    {
        var c = BuiltinClass("tuple");
        return c == null ? PyType.Unknown : new ClassType(c.Info, new[] { element }, true);
    }

    // ------------------------------------------------------------ variables

    /// <summary>Type of the value bound to an assignment-like target (a name or, for instance attributes, self.attr).</summary>
    PyType InferTargetType(ModuleInfo m, Expr target)
    {
        Node child = target;
        var p = m.ParentOf(target);
        while (p is TupleExpr or ListExpr or StarExpr) { child = p; p = m.ParentOf(p); }

        switch (p)
        {
            case AssignStmt a when a.Targets.Any(t => ReferenceEquals(t, child)):
                return Destructure(m, TypeOf(m, a.Value), (Expr)child, target);

            case AugAssignStmt au when ReferenceEquals(au.Target, target):
            {
                var left = TypeOfAugTarget(m, target);
                var op = au.Op.Substring(0, au.Op.Length - 1);
                return BinaryOp(left, op, TypeOf(m, au.Value));
            }

            case AnnAssignStmt an when ReferenceEquals(an.Target, target):
                return AnnotationType(m, an.Annotation);

            case ForStmt f when ReferenceEquals(f.Target, child):
                return Destructure(m, IterElement(TypeOf(m, f.Iter)), (Expr)child, target);

            case Comprehension c when ReferenceEquals(c.Target, child):
                return Destructure(m, IterElement(TypeOf(m, c.Iter)), (Expr)child, target);

            case WithItem w when ReferenceEquals(w.Target, child):
                return Destructure(m, ContextEnterType(TypeOf(m, w.Context)), (Expr)child, target);

            case NamedExpr ne when ReferenceEquals(ne.Target, target):
                return TypeOf(m, ne.Value);
        }
        return PyType.Unknown;
    }

    PyType TypeOfAugTarget(ModuleInfo m, Expr target) => target switch
    {
        NameExpr n => TypeOfName(m, n),
        AttributeExpr a => AttributeType(TypeOf(m, a.Value), a.Attr).Type,
        _ => PyType.Unknown,
    };

    /// <summary>The part of a value assigned to a (possibly nested) target pattern that lands on <paramref name="name"/>.</summary>
    PyType Destructure(ModuleInfo m, PyType value, Expr pattern, Expr name)
    {
        if (ReferenceEquals(pattern, name)) return value;
        if (pattern is StarExpr s) return Inst("list", Destructure(m, value, s.Value, name));

        IReadOnlyList<Expr>? items = pattern switch { TupleExpr t => t.Items, ListExpr l => l.Items, _ => null };
        if (items == null) return PyType.Unknown;

        int idx = -1;
        for (int i = 0; i < items.Count; i++)
            if (Contains(items[i], name)) { idx = i; break; }
        if (idx < 0) return PyType.Unknown;
        int starAt = -1;
        for (int i = 0; i < items.Count; i++) if (items[i] is StarExpr) starAt = i;

        return MapMembers(value, member =>
        {
            PyType element;
            var fixedItems = member is ClassType mc ? TupleItemsOf(mc) : null;
            if (fixedItems != null && starAt < 0 && fixedItems.Count == items.Count)
                element = fixedItems[idx];
            else if (fixedItems != null && starAt >= 0 && fixedItems.Count >= items.Count - 1)
            {
                int after = items.Count - 1 - starAt;
                if (idx < starAt) element = fixedItems[idx];
                else if (idx == starAt) element = PyType.Union(fixedItems.Skip(starAt).Take(fixedItems.Count - starAt - after));
                else element = fixedItems[fixedItems.Count - (items.Count - idx)];
                if (idx == starAt) return Destructure(m, Inst("list", element), items[idx], name);
            }
            else element = IterElement(member);
            return Destructure(m, element, items[idx], name);
        });
    }

    static bool Contains(Node haystack, Node needle)
    {
        if (ReferenceEquals(haystack, needle)) return true;
        foreach (var c in AstChildren.Of(haystack)) if (Contains(c, needle)) return true;
        return false;
    }

    // ------------------------------------------------------------ imports

    PyType ImportType(ModuleInfo m, ImportAlias a)
    {
        switch (m.ParentOf(a))
        {
            case ImportStmt:
            {
                string name = a.AsName != null ? a.Name : a.Name.Split('.')[0];
                var mod = _ws.GetModule(name);
                return mod == null ? PyType.Unknown : new ModuleType(mod);
            }
            case ImportFromStmt s:
            {
                var abs = Workspace.AbsoluteName(m, s.Level, s.Module);
                if (abs == null) return PyType.Unknown;
                var mod = _ws.GetModule(abs);
                if (mod != null && mod.Semantics.ModuleScope.Symbols.TryGetValue(a.Name, out var sym) && sym.Declarations.Count > 0)
                    return TypeOfSymbol(sym);
                var sub = _ws.GetModule(abs.Length == 0 ? a.Name : abs + "." + a.Name);
                return sub == null ? PyType.Unknown : new ModuleType(sub);
            }
        }
        return PyType.Unknown;
    }

    // ------------------------------------------------------------ awaiting

    PyType AwaitedType(PyType t) => MapMembers(t, member =>
    {
        if (member is ClassType { IsInstance: true } c)
        {
            var co = TypingClass("Coroutine");
            var spec = co == null ? null : SpecializeAs(c, co.Info);
            if (spec is { Args.Count: 3 }) return spec.Args[2];
            var aw = TypingClass("Awaitable");
            var spec2 = aw == null ? null : SpecializeAs(c, aw.Info);
            if (spec2 is { Args.Count: 1 }) return spec2.Args[0];
        }
        return PyType.Unknown;
    });
}

/// <summary>Several @overload signatures of one function.</summary>
public sealed record OverloadedType(IReadOnlyList<FunctionType> Overloads) : PyType
{
    public bool Equals(OverloadedType? o) => o != null && Overloads.SequenceEqual(o.Overloads);
    public override int GetHashCode() => Overloads.Count;
    public override string Display(int depth = 0) => $"overloaded {Overloads[0].Info.Name} ({Overloads.Count})";
}
