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
        if (_provisional.Count > 0 && _provisional.TryGetValue((cache, key), out var soft)) return (T)soft!;
        if (!_active.Add(key)) { _open.Add(key); return cycleValue; }
        T result;
        try { result = compute(); }
        finally { _active.Remove(key); }
        _open.Remove(key);
        if (_open.Count == 0) cache[key] = result;
        // a result that leaned on a cut cycle is not final, but within this one query it is as good as any other
        // cut: keeping it stops mutually dependent attributes (self.line, self.column, ...) from being recomputed exponentially
        else _provisional[(cache, key)] = result;
        if (_active.Count == 0) { _provisional.Clear(); _open.Clear(); }
        return result;
    }

    readonly Dictionary<(object, object), object?> _provisional = new(new PairReferenceComparer());

    sealed class PairReferenceComparer : IEqualityComparer<(object, object)>
    {
        public bool Equals((object, object) a, (object, object) b) => ReferenceEquals(a.Item1, b.Item1) && ReferenceEquals(a.Item2, b.Item2);
        public int GetHashCode((object, object) p) => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item1) * 31 + System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(p.Item2);
    }

    PyType Memo(object key, Func<PyType> compute) => Memo(_cache, key, PyType.CycleUnknown, compute);

    // ------------------------------------------------------------ builtin lookups

    PyType NoneInstance
    {
        get
        {
            // typeshed defines NoneType in `types`; the bundled builtins stub defines it directly
            var c = BuiltinClass("NoneType") ?? ModuleClass(_typesClasses, _ws.GetModule("types"), "NoneType");
            return c == null ? PyType.Unknown : new ClassType(c.Info, Array.Empty<PyType>(), true);
        }
    }
    readonly Dictionary<string, ClassType?> _typesClasses = new();

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
        fi = new FunctionInfo(node.Name, m, node, scope) { Flags = DecoratorFlags(m, node) };
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

    /// <summary>Decorators the engine models through flags, not by calling them.</summary>
    static readonly HashSet<string> FlagDecorators = new()
    {
        "staticmethod", "classmethod", "property", "cached_property", "setter", "getter", "deleter", "abstractmethod",
        "overload", "final", "override", "abstractproperty", "abstractclassmethod", "abstractstaticmethod", "DynamicClassAttribute",
    };

    /// <summary>
    /// The name a decorator is spelled with, or the flag decorator an alias of it stands for (`from builtins import property as p`,
    /// `p = property`, as typeshed's enum.pyi does). Purely syntactic: FunctionOf caches the flags outside the cycle handling.
    /// </summary>
    static string? DecoratorBaseName(ModuleInfo m, Expr d)
    {
        string? name = d switch { NameExpr n => n.Name, AttributeExpr a => a.Attr, _ => null };
        if (name == null || FlagDecorators.Contains(name) || d is not NameExpr ne) return name;
        return AliasedFlagName(m, ne, 3, new HashSet<Symbol>()) ?? name;
    }

    static string? AliasedFlagName(ModuleInfo m, NameExpr n, int depth, HashSet<Symbol> seen)
    {
        if (depth == 0 || m.Semantics.ReferenceOf(n)?.Symbol is not { } sym || !seen.Add(sym)) return null;
        // every declaration counts: `_magic_enum_attr` is bound in both arms of a version check
        foreach (var decl in sym.Declarations)
        {
            if (decl.Kind == DeclKind.Import && decl.Node is ImportAlias ia && m.ParentOf(ia) is ImportFromStmt)
            {
                if (FlagDecorators.Contains(ia.Name)) return ia.Name;
            }
            else if (decl.Kind == DeclKind.Variable && m.ParentOf(decl.Node) is AssignStmt { Targets.Count: 1 } a && ReferenceEquals(a.Targets[0], decl.Node))
            {
                if (a.Value is AttributeExpr av && FlagDecorators.Contains(av.Attr)) return av.Attr;
                if (a.Value is NameExpr nv && (FlagDecorators.Contains(nv.Name) ? nv.Name : AliasedFlagName(m, nv, depth - 1, seen)) is { } r) return r;
            }
        }
        return null;
    }

    /// <summary>
    /// What a name bound by `@deco def f` is: f passed through each decorator, the nearest first. Only decorators that say
    /// what they return are applied (a typed `Callable[..., R]`, a class), and only when the result is known; otherwise f stays as written.
    /// </summary>
    PyType ApplyDecorators(ModuleInfo m, FunctionDef def, PyType fn)
    {
        var current = fn;
        for (int i = def.Decorators.Count - 1; i >= 0; i--)
        {
            var dec = def.Decorators[i];
            string? name = dec switch { CallExpr { Func: NameExpr cn } => cn.Name, CallExpr { Func: AttributeExpr ca } => ca.Attr, _ => DecoratorBaseName(m, dec) };
            if (name != null && FlagDecorators.Contains(name)) continue;
            var decType = TypeOf(m, dec);
            if (decType.IsDynamic || !(decType.Members.All(x => x is FunctionType { Info: var fi } && DeclaredReturn(fi) != null
                                                              || x is OverloadedType || x is ClassType { IsInstance: false } || x is SignatureType
                                                              || x is ClassType { IsInstance: true } inst && TypedCall(inst))))
                continue;
            var arg = current;
            var targets = CallValue(decType, new[] { new ArgInfo(null, null, 0, () => arg) }, null);
            if (targets.Count == 0 || targets.Any(t => t.Problems.Count > 0)) continue;
            var result = PyType.Union(targets.Select(t => t.Returns));
            if (result.IsDynamic || result.Members.Any(x => x.IsDynamic)) continue;
            // an instance decorator counts when it gives a function or a callable (mock.patch); one that gives another object, as functools.wraps
            // gives a `_Wrapped` whose `__call__` takes `*args: P.args`, leaves the function as written, whose parameters say more
            if (decType.Members.Any(x => x is ClassType { IsInstance: true }) && result.Members.Any(x => x is not (FunctionType or OverloadedType or SignatureType))) continue;
            current = result;
        }
        return current;
    }

    /// <summary>
    /// An instance used as a decorator whose `__call__` says what it returns (`@patch("os.getcwd")`, whose `_patch_pass_arg.__call__` gives a
    /// `Callable[..., R]`, as the mock it passes in is one more argument).
    /// </summary>
    bool TypedCall(ClassType inst)
    {
        var call = AttributeType(inst, "__call__").Type;
        return !call.IsDynamic && call.Members.All(c => c is FunctionType { Info: var fi } && DeclaredReturn(fi) != null || c is OverloadedType || c is SignatureType);
    }

    static FuncFlags DecoratorFlags(ModuleInfo m, FunctionDef f)
    {
        var flags = FuncFlags.None;
        // implicit static/class methods
        if (f.Name == "__new__") flags |= FuncFlags.Static;
        if (f.Name is "__init_subclass__" or "__class_getitem__") flags |= FuncFlags.ClassMethod;
        foreach (var d in f.Decorators)
        {
            switch (DecoratorBaseName(m, d))
            {
                case "staticmethod": flags |= FuncFlags.Static; break;
                case "classmethod": flags |= FuncFlags.ClassMethod; break;
                case "property": case "cached_property": case "DynamicClassAttribute": flags |= FuncFlags.Property; break;
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
    /// <remarks>A known answer is returned before any closure is made (a lambda would be allocated on every call, found or not).</remarks>
    public PyType TypeOf(ModuleInfo m, Expr e) => _cache.TryGetValue(e, out var known) ? known : ComputeMemo(m, e);

    PyType ComputeMemo(ModuleInfo m, Expr e) => Memo(e, () => Compute(m, e));

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

    static bool IsNone(PyType t) => t is ClassType { IsInstance: true, Info: { Name: "NoneType" } } c && c.Info.Module.Name is "builtins" or "types";

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
            if (m.Semantics.ModuleScope.HasStarImport && FindModuleSymbol(m, n.Name) is { } viaStar) return TypeOfSymbol(viaStar);
            if (r.IsBuiltin) return BuiltinNameType(n.Name);
            return PyType.Unknown;
        }

        var sym = r.Symbol;
        var symModule = _ws.ModuleOf(sym) ?? m;

        if ((r.Access & Access.Read) != 0)
        {
            // flow-sensitive: only the assignments that reach this use, narrowed by the conditions on the way
            var flowType = FlowTypeOfName(m, n, r, sym);
            if (flowType != null) return WholeOverloads(flowType, sym);
        }
        else
        {
            foreach (var d in sym.Declarations)
                if (ReferenceEquals(d.Node, n)) return TypeOfDeclaration(symModule, d);
        }

        var declared = DeclaredType(symModule, sym);
        if (declared != null) return declared;

        // locals: only the assignments that can reach this use, past calls that never return. Only in functions: in a module or
        // class body the gates are its many top-level calls (`SafeConstructor.add_constructor(...)` dozens of times), and deciding
        // them from every read there runs into the classes being built
        if ((r.Access & Access.Read) != 0 && ReferenceEquals(sym.Scope, r.UseScope))
        {
            var reach = r.UseScope is { Kind: ScopeKind.Function or ScopeKind.Lambda } ? TypedFlow(m, r.UseScope)?.ReachingFor(n) : m.Flow.ReachingDefinitions(n);
            // a module-level read before the module binds the name (`builtin_str = str` and later `str = str`) still finds the builtin
            if (reach is { Definitions.Count: 0, Reachable: true, MaybeUnbound: true } && r.UseScope?.Kind == ScopeKind.Module
                && _ws.Builtins.Semantics.ModuleScope.Symbols.ContainsKey(n.Name))
                return BuiltinNameType(n.Name);
            if (reach != null)
                return reach.Definitions.Count == 0 ? PyType.Unknown : WholeOverloads(PyType.Union(reach.Definitions.Select(d => TypeOfDeclaration(symModule, d))), sym);
        }
        return TypeOfSymbol(sym);
    }

    /// <summary>`ib = attrib` after four `@overload def attrib`: each def replaces the one before it in the flow, but the name stands for all of them.</summary>
    PyType WholeOverloads(PyType t, Symbol sym) =>
        t is FunctionType { Info.Flags: var flags } && (flags & FuncFlags.Overload) != 0 && TypeOfSymbol(sym) is OverloadedType all ? all : t;

    PyType BuiltinNameType(string name)
    {
        var scope = _ws.Builtins.Semantics.ModuleScope;
        return scope.Symbols.TryGetValue(name, out var s) ? TypeOfSymbol(s) : PyType.Unknown;
    }

    /// <summary>Type of a symbol seen from outside its own flow: an annotation if there is one, else all its declarations.</summary>
    public PyType TypeOfSymbol(Symbol sym)
    {
        var key = sym.Redirect ?? sym;
        return _cache.TryGetValue(key, out var known) ? known : SymbolTypeMemo(key);
    }

    PyType SymbolTypeMemo(Symbol s)
    {
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
    /// <summary>Names in typing that mean something to the type system rather than being ordinary classes or aliases.</summary>
    static readonly HashSet<string> SpecialFormNames = new()
    {
        "Any", "Union", "Optional", "Callable", "Literal", "ClassVar", "Final", "Annotated", "Type", "Tuple",
        "List", "Dict", "Set", "FrozenSet", "DefaultDict", "Counter", "Deque", "ChainMap", "OrderedDict",
        "Generic", "Protocol", "NoReturn", "Never", "Self", "LiteralString", "TypeAlias", "Concatenate",
        "TypeGuard", "TypeIs", "Unpack", "Required", "NotRequired", "ReadOnly", "TypedDict", "TypeForm",
    };

    PyType? DeclaredType(ModuleInfo m, Symbol sym)
    {
        if (m.Name is "typing" or "typing_extensions" && SpecialFormNames.Contains(sym.Name)) return new SpecialFormType(sym.Name);
        foreach (var d in sym.Declarations)
        {
            if (d.Kind == DeclKind.Parameter && d.Node is Parameter { Annotation: not null })
                return TypeOfDeclaration(m, d);
            if (d.Kind == DeclKind.Variable && m.ParentOf(d.Node) is AnnAssignStmt a && ReferenceEquals(a.Target, d.Node))
            {
                if (m.Name == "typing" && a.Annotation is NameExpr { Name: "_SpecialForm" })
                    return new SpecialFormType(sym.Name);
                if (a.Annotation is NameExpr { Name: "TypeAlias" } or AttributeExpr { Attr: "TypeAlias" })
                    return null;   // `M: TypeAlias = Literal[...]` names the value's type, like `M = Literal[...]`
                if (a.Annotation is NameExpr { Name: "Final" } or AttributeExpr { Attr: "Final" })
                    return a.Value == null ? null : ConstantLiteral(a.Value);   // no type of its own: the value's, a constant keeping its literal
                return AnnotationType(m, a.Annotation);
            }
        }
        return null;
    }

    PyType TypeOfDeclaration(ModuleInfo m, Declaration d) => _cache.TryGetValue(d, out var known) ? known : DeclarationTypeMemo(m, d);

    PyType DeclarationTypeMemo(ModuleInfo m, Declaration d) => Memo(d, () =>
    {
        if (_declOverride.TryGetValue(d, out var overridden)) return overridden;
        switch (d.Kind)
        {
            case DeclKind.Class: return new ClassType(ClassOf(m, (ClassDef)d.Node), false);
            case DeclKind.Function:
            {
                var def = (FunctionDef)d.Node;
                PyType fn = new FunctionType(FunctionOf(m, def), false);
                return def.Decorators.Count == 0 ? fn : ApplyDecorators(m, def, fn);
            }
            case DeclKind.Parameter: return ParameterType(m, (Parameter)d.Node);
            case DeclKind.Variable: return d.Node is Expr target ? InferTargetType(m, target) : PyType.Unknown;
            case DeclKind.Import: return ImportType(m, (ImportAlias)d.Node);
            case DeclKind.TypeParam: return TypeParamType((TypeParam)d.Node);
            case DeclKind.MatchCapture: return PatternBindingType(m, d);
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
        // an evaluator made for one call site: the argument's type (see ReturnAtCallSite)
        if (_callSiteParams != null && _callSiteParams.TryGetValue(p, out var given)) return given;
        var owner = m.ParentOf(p);
        FunctionInfo? fn = owner switch
        {
            FunctionDef fd => FunctionOf(m, fd),
            LambdaExpr l => LambdaOf(m, l),
            _ => null,
        };

        // self / cls (the cls of __new__ too: it is static only in that the class is passed explicitly)
        bool isNew = fn?.Name == "__new__";
        if (fn?.Owner != null && owner is FunctionDef def && (!fn.IsStatic || isNew) && p.Annotation == null && p.Kind == ParamKind.Normal)
        {
            var first = def.Params.FirstOrDefault(x => x.Kind is ParamKind.Normal or ParamKind.VarArgs or ParamKind.KwArgs);
            // by convention only these names mean "the receiver"; a def in a class body with another first parameter
            // is usually a helper called while the class is being built (fractions._operator_fallbacks)
            if (ReferenceEquals(first, p) && p.Kind == ParamKind.Normal && (fn.IsClassMethod || isNew || IsReceiverName(p.Name)))
            {
                var self = new ClassType(fn.Owner, ClassTypeParamsAsArgs(fn.Owner), true);
                return fn.IsClassMethod || isNew ? self.AsClassObject() : self;
            }
        }

        if (p.Annotation == null && (p.Default == null || p.Default is CallExpr) && p.Kind == ParamKind.Normal && owner is FunctionDef
            && _ws.Options.ParameterTypes is { } hook && hook(_ws, m, p) is { } provided)
            return provided;

        PyType t;
        if (p.Annotation != null) t = AnnotationType(m, p.Annotation);
        else if (p.Default is ConstExpr { Kind: ConstKind.None }) t = PyType.Unknown; // `x=None` conventionally means "optional anything"
        else if (p.Default != null) t = TypeOf(m, p.Default);
        else t = PyType.Unknown;

        return p.Kind switch
        {
            // `*args: *Ts` is the tuple of the types Ts stands for
            ParamKind.VarArgs when t is TypeVarTupleType && BuiltinClass("tuple") is { } tuple => TupleOf(tuple, new[] { t }),
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
                // `x = attrib()` in an attrs class declares a field: instances hold the field's value, not the _CountingAttr
                if (ReferenceEquals(child, target) && a.Value is CallExpr { Func: NameExpr { Name: "ib" or "attrib" or "field" } or AttributeExpr { Attr: "ib" or "attrib" or "field" } } fc
                    && m.ParentOf(a) is ClassDef owner && DataclassOf(ClassOf(m, owner)) is { Attrs: true })
                    return AttribFieldType(m, fc);
                return Destructure(m, TypeOf(m, a.Value), (Expr)child, target);

            case AugAssignStmt au when ReferenceEquals(au.Target, target):
            {
                var left = TypeOfAugTarget(m, target);
                var op = au.Op.Substring(0, au.Op.Length - 1);
                return BinaryOp(left, op, TypeOf(m, au.Value));
            }

            case AnnAssignStmt an when ReferenceEquals(an.Target, target):
                // `MODE: Final = "r"`: the type is what the value is, a constant keeping its literal
                if (an.Value != null && an.Annotation is NameExpr { Name: "Final" } or AttributeExpr { Attr: "Final" })
                    return ConstantLiteral(an.Value) ?? TypeOf(m, an.Value);
                // `M: TypeAlias = Literal["r", "w"]` is the alias of its value, as `M = Literal["r", "w"]` is
                if (an.Value != null && an.Annotation is NameExpr { Name: "TypeAlias" } or AttributeExpr { Attr: "TypeAlias" })
                    return an.Value is BinaryExpr { Op: "|" } ? AnnotationToValue(Annotation(m, an.Value, null)) : TypeOf(m, an.Value);
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
            else if (fixedItems != null) return PyType.Unknown;    // a tuple of another size: unpacking it would fail, so say nothing
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
        var t = ImportTypeOf(m, a);
        // `try: import ujson as json` / `except ImportError: import json`: an import that cannot be found never binds the name
        // there, so the fallback alone gives its type
        return t is UnknownType && IsGuardedByFallback(m, a) ? PyType.Never : t;
    }

    /// <summary>An import in the body of a try whose ImportError handler binds the same name again.</summary>
    static bool IsGuardedByFallback(ModuleInfo m, ImportAlias a)
    {
        string local = a.AsName ?? a.Name.Split('.')[0];
        Node child = a;
        for (var p = m.ParentOf(a); p != null; child = p, p = m.ParentOf(p))
        {
            if (p is FunctionDef or ClassDef or Module) return false;
            if (p is not TryStmt t || !t.Body.Contains(child)) continue;
            foreach (var h in t.Handlers)
            {
                bool catches = h.Type == null || HandlerNames(h.Type).Any(n => n is "ImportError" or "ModuleNotFoundError" or "Exception" or "BaseException");
                if (catches && h.Body.Any(s => Binds(s, local))) return true;
            }
        }
        return false;

        static IEnumerable<string> HandlerNames(Expr e) => e switch
        {
            NameExpr n => new[] { n.Name },
            AttributeExpr x => new[] { x.Attr },
            TupleExpr tu => tu.Items.SelectMany(HandlerNames),
            _ => Array.Empty<string>(),
        };

        static bool Binds(Stmt s, string name) => s switch
        {
            ImportStmt i => i.Names.Any(n => (n.AsName ?? n.Name.Split('.')[0]) == name),
            ImportFromStmt f => f.Names.Any(n => (n.AsName ?? n.Name) == name),
            AssignStmt asg => asg.Targets.Any(x => x is NameExpr nx && nx.Name == name),
            AnnAssignStmt ann => ann.Target is NameExpr nx && nx.Name == name,
            _ => false,
        };
    }

    PyType ImportTypeOf(ModuleInfo m, ImportAlias a)
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
                var sub = _ws.GetModule(abs.Length == 0 ? a.Name : abs + "." + a.Name);
                var sym = mod == null ? null : FindModuleSymbol(mod, a.Name);
                // `from . import x` inside a package's own __init__ imports the submodule: the attribute of the same
                // name (often defined from this very import: `path = _path`) cannot exist yet
                if (sub != null && ReferenceEquals(mod, m)) return new ModuleType(sub);
                // a name that only this import statement defines is not evidence of anything
                if (sym != null && !sym.Declarations.All(d => ReferenceEquals(d.Node, a))) return TypeOfSymbol(sym);
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
