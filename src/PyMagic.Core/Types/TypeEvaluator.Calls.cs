using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

public enum CallProblemKind { Arity, Type, NotCallable }

public sealed record CallProblem(CallProblemKind Kind, Node? At, string Message);

/// <summary>Which parameter an argument of a call fills (null when it has nowhere to go).</summary>
public sealed record ArgumentMapping(Argument Arg, ParamInfo? Param);

/// <summary>The outcome of calling one possible callee.</summary>
public sealed record CallTarget(PyType Callee, PyType Returns, IReadOnlyList<ArgumentMapping> Mappings,
    IReadOnlyList<CallProblem> Problems, IReadOnlyDictionary<string, PyType>? Solved = null);

/// <summary>Resolution of a call expression; Targets has one entry per possible callee type (unions are expanded).</summary>
public sealed record CallResolution(PyType Returns, IReadOnlyList<CallTarget> Targets);

/// <summary>One argument of a call. Type is evaluated lazily, only when a parameter actually needs it.</summary>
/// <param name="Form">The type the argument spells as a type expression, for a `TypeForm[T]` parameter.</param>
sealed record ArgInfo(Argument? Node, string? Name, int Stars, Func<PyType> Type, Func<PyType?>? Literal = null, Func<bool>? Fresh = null, Func<PyType>? Form = null);

public sealed partial class TypeEvaluator
{
    // ------------------------------------------------------------ signatures

    readonly Dictionary<FunctionInfo, IReadOnlyList<ParamInfo>> _params = new(ReferenceEqualityComparer.Instance);

    /// <summary>Declared parameters of a def or lambda (annotation types, Unknown when unannotated).</summary>
    public IReadOnlyList<ParamInfo> ParametersOf(FunctionInfo info)
    {
        if (_params.TryGetValue(info, out var cached)) return cached;
        var ps = info.Node switch
        {
            FunctionDef fd => fd.Params,
            LambdaExpr l => l.Params,
            _ => Array.Empty<Parameter>(),
        };
        var result = new List<ParamInfo>();
        int slash = -1;
        for (int i = 0; i < ps.Count; i++) if (ps[i].Kind == ParamKind.Slash) slash = i;
        bool afterStar = false;
        int dunderOnlyUntil = DunderPositionalOnlyEnd(ps);
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            PyType Ty() => p.Annotation != null ? AnnotationType(info.Module, p.Annotation) : PyType.Unknown;
            switch (p.Kind)
            {
                case ParamKind.Slash: break;
                case ParamKind.KwOnlySeparator: afterStar = true; break;
                case ParamKind.VarArgs:
                    result.Add(new ParamInfo(p.Name, Ty(), ParamCategory.VarPositional, false));
                    afterStar = true;
                    break;
                case ParamKind.KwArgs:
                    // **kwargs: Unpack[Options] stands for the keyword-only parameters the TypedDict lists
                    if (p.Annotation is SubscriptExpr { Value: NameExpr { Name: "Unpack" } or AttributeExpr { Attr: "Unpack" } } unpack
                        && AnnotationType(info.Module, unpack.Index) is ClassType { Info: var td } && TypedDictFieldsOf(td) is { } tdFields)
                    {
                        foreach (var (field, required) in tdFields)
                            result.Add(new ParamInfo(field.Name, field.Type, ParamCategory.KeywordOnly, !required));
                        break;
                    }
                    result.Add(new ParamInfo(p.Name, Ty(), ParamCategory.VarKeyword, false));
                    break;
                default:
                {
                    var cat = afterStar ? ParamCategory.KeywordOnly : (i < slash || i <= dunderOnlyUntil ? ParamCategory.PositionalOnly : ParamCategory.Normal);
                    result.Add(new ParamInfo(p.Name, Ty(), cat, p.Default != null));
                    break;
                }
            }
        }
        _params[info] = result;
        return result;
    }

    /// <summary>
    /// Old-style positional-only parameters: names starting with two underscores (and not ending with them) that come
    /// before any other parameter, apart from a leading self/cls. Returns the index of the last such parameter, or -1.
    /// </summary>
    static int DunderPositionalOnlyEnd(IReadOnlyList<Parameter> ps)
    {
        int last = -1;
        for (int i = 0; i < ps.Count; i++)
        {
            var p = ps[i];
            if (p.Kind != ParamKind.Normal) break;
            bool dunder = p.Name.StartsWith("__") && !p.Name.EndsWith("__");
            if (dunder) last = i;
            else if (i == 0 && IsReceiverName(p.Name)) continue;
            else break;
        }
        return last;
    }

    /// <summary>The parameters a call must supply for this function value (bound receiver removed, substitution applied).</summary>
    IReadOnlyList<ParamInfo> CallableParameters(FunctionType f)
    {
        var ps = ParametersOf(f.Info).ToList();
        if (f.BoundFirst && ps.Count > 0 && ps[0].Category is ParamCategory.PositionalOnly or ParamCategory.Normal) ps.RemoveAt(0);
        if (f.Subst != null && f.Subst.Count > 0) ps = SubstituteParams(ps, f.Subst);
        return ps;
    }

    PyType? DeclaredReturn(FunctionInfo info)
    {
        if (info.Node is not FunctionDef fd || fd.Returns == null) return null;
        var r = AnnotationType(info.Module, fd.Returns);
        if ((info.Flags & FuncFlags.Async) != 0 && (info.Flags & FuncFlags.Generator) == 0)
            return TypingInst("Coroutine", PyType.Any, PyType.Any, r);
        return r;
    }

    /// <summary>What calling the function returns: the annotation, or a type inferred from its return statements.</summary>
    public PyType ReturnTypeOf(FunctionType f)
    {
        var ret = DeclaredReturn(f.Info) ?? InferReturn(f.Info);
        return Substitute(ret, f.Subst);
    }

    PyType InferReturn(FunctionInfo info) => _cache.TryGetValue(info, out var known) ? known : InferReturnMemo(info);

    PyType InferReturnMemo(FunctionInfo info) => Memo(info, () =>
    {
        // a function's body is where long work spends its time: a cancelled request stops here
        RequestCancellation.Check();
        var m = info.Module;
        if (info.Node is LambdaExpr l) return TypeOf(m, l.Body);
        if (info.Node is not FunctionDef fd || m.IsStub) return PyType.Unknown;

        // a call that never returns ends a path like raise; a with block that swallows exceptions can end one at the with
        var flow = TypedFlow(m, info.Scope);
        if (flow == null) return PyType.Unknown;

        var returns = new List<PyType>();
        foreach (var ins in flow.Graph.Instructions)
            if (ins.Kind == Flow.InstrKind.Return && flow.Reachable[ins.Id] && ins.Element is ReturnStmt r)
                returns.Add(r.Value == null ? NoneInstance : TypeOf(m, r.Value));
        if (flow.EndReachable) returns.Add(NoneInstance);

        PyType ret;
        if (returns.Count == 0)
            ret = RaisesNotImplemented(fd) ? PyType.Unknown : PyType.Never;
        else ret = PyType.Union(returns);

        bool isGen = (info.Flags & FuncFlags.Generator) != 0, isAsync = (info.Flags & FuncFlags.Async) != 0;
        if (isGen)
        {
            var ys = new List<PyType>();
            foreach (var y in FindYields(fd.Body))
                ys.Add(y.Value == null ? NoneInstance : y.IsFrom ? IterElement(TypeOf(m, y.Value)) : TypeOf(m, y.Value));
            var yield = ys.Count == 0 ? PyType.Unknown : PyType.Union(ys);
            return isAsync ? TypingInst("AsyncGenerator", yield, PyType.Any) : TypingInst("Generator", yield, PyType.Any, ret is NeverType ? NoneInstance : ret);
        }
        if (isAsync) return TypingInst("Coroutine", PyType.Any, PyType.Any, ret);
        return ret;
    });

    static bool RaisesNotImplemented(FunctionDef fd) =>
        AstChildren.Descendants(fd).OfType<RaiseStmt>().Any(r =>
            r.Exc is NameExpr { Name: "NotImplementedError" } or CallExpr { Func: NameExpr { Name: "NotImplementedError" } });

    static IEnumerable<YieldExpr> FindYields(IEnumerable<Node> nodes)
    {
        foreach (var n in nodes)
        {
            if (n is FunctionDef or LambdaExpr or ClassDef) continue;
            if (n is YieldExpr y) yield return y;
            foreach (var inner in FindYields(AstChildren.Of(n))) yield return inner;
        }
    }

    // ------------------------------------------------------------ argument mapping

    (List<(ArgInfo Arg, ParamInfo? Param)> Map, List<CallProblem> Problems) MapArguments(
        IReadOnlyList<ParamInfo> ps, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var map = new List<(ArgInfo, ParamInfo?)>();
        var problems = new List<CallProblem>();
        var positional = ps.Where(p => p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal).ToList();
        var varPos = ps.FirstOrDefault(p => p.Category == ParamCategory.VarPositional);
        var varKw = ps.FirstOrDefault(p => p.Category == ParamCategory.VarKeyword);
        var filled = new HashSet<ParamInfo>();
        bool unpackedPos = false, unpackedKw = false;
        int next = 0;

        foreach (var a in args)
        {
            Node? at = (Node?)a.Node ?? callNode;
            if (a.Stars == 1) { unpackedPos = true; map.Add((a, varPos)); continue; }
            if (a.Stars == 2) { unpackedKw = true; map.Add((a, varKw)); continue; }

            if (a.Name == null)
            {
                if (next < positional.Count) { var p = positional[next++]; filled.Add(p); map.Add((a, p)); }
                else if (varPos != null) map.Add((a, varPos));
                else
                {
                    map.Add((a, null));
                    if (!unpackedPos) problems.Add(new CallProblem(CallProblemKind.Arity, at, "Unexpected argument"));
                }
            }
            else
            {
                var p = ps.FirstOrDefault(x => x.Name == a.Name && x.Category is ParamCategory.Normal or ParamCategory.KeywordOnly);
                if (p != null)
                {
                    if (!filled.Add(p)) problems.Add(new CallProblem(CallProblemKind.Arity, at, $"Multiple values for parameter '{p.Name}'"));
                    map.Add((a, p));
                }
                else if (varKw != null) map.Add((a, varKw));
                else
                {
                    map.Add((a, null));
                    if (!unpackedKw) problems.Add(new CallProblem(CallProblemKind.Arity, at, $"Unexpected argument '{a.Name}'"));
                }
            }
        }

        foreach (var p in ps)
        {
            if (p.Category is ParamCategory.VarPositional or ParamCategory.VarKeyword || p.HasDefault || filled.Contains(p)) continue;
            bool positionalParam = p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal;
            if (unpackedPos && positionalParam) continue;
            if (unpackedKw && p.Category is ParamCategory.Normal or ParamCategory.KeywordOnly) continue;
            problems.Add(new CallProblem(CallProblemKind.Arity, callNode, $"Parameter '{p.Name}' unfilled"));
        }
        return (map, problems);
    }

    // ------------------------------------------------------------ calls

    /// <summary>Resolves a call expression: which callee(s), how arguments map to parameters, and what it returns.</summary>
    public CallResolution ResolveCall(ModuleInfo m, CallExpr call)
    {
        var callee = TypeOf(m, call.Func);
        var args = call.Args.Select(a => new ArgInfo(a, a.Name, a.Stars, () => TypeOf(m, a.Value), () => ArgumentLiteral(m, a.Value), () => IsFreshValue(m, a.Value), () => TypeFormOf(m, a.Value))).ToList();

        // super() needs the enclosing method, not just the callee's type
        // super is a function in the bundled stub and a class in typeshed
        if (callee is FunctionType { Info: { Name: "super" } si } && si.Module.Name == "builtins"
            || callee is ClassType { IsInstance: false, Info: { Name: "super" } sc } && sc.Module.Name == "builtins")
            return Single(new CallTarget(callee, SuperOfCall(m, call, args), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        if (callee is FunctionType { Info: { Name: "namedtuple" } nti } && nti.Module.Name == "collections")
            return Single(new CallTarget(callee, NamedTupleFromCall(m, call), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        if (callee is ClassType { IsInstance: false } ntc && ntc.Info.QualifiedName == "typing.NamedTuple" && call.Args.Count >= 2)
            return Single(new CallTarget(callee, NamedTupleFromTypingCall(m, call), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        if (callee is ClassType { IsInstance: false } enumBase && IsEnumFunctionalCall(enumBase, call))
            return Single(new CallTarget(callee, EnumFromCall(m, call, enumBase), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        if (IsTypedDictFunctionalCall(callee, call))
            return Single(new CallTarget(callee, TypedDictFromCall(m, call), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        // typing.cast(T, value) is T, with T read as a type (a quoted one included)
        if ((callee as FunctionType ?? (callee as OverloadedType)?.Overloads[0]) is { Info: { Name: "cast" } ci } && ci.Module.Name is "typing" or "typing_extensions"
            && call.Args is [{ Name: null, Stars: 0 } castType, { Stars: 0 }])
        {
            var target = Annotation(m, castType.Value, null);
            if (!target.IsDynamic)
                return Single(new CallTarget(callee, target, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));
        }

        if (callee is ClassType { IsInstance: false } tvc && tvc.Info.QualifiedName is "typing.TypeVar" or "typing_extensions.TypeVar"
            && call.Args.FirstOrDefault() is { Name: null, Stars: 0, Value: ConstExpr tvName })
            return Single(new CallTarget(callee, TypeVarFromCall(m, call, tvName), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        if (TypedDictGet(m, call) is PyType tdGet)
            return Single(new CallTarget(callee, tdGet, Array.Empty<ArgumentMapping>(), TypedDictSetdefaultProblems(m, call, tdGet)));

        if (DjangoSpecialCall(m, call, callee) is { } djangoResult)
            return Single(new CallTarget(callee, djangoResult, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        var targets = CallValue(callee, args, call);
        return new CallResolution(targets.Count == 0 ? PyType.Unknown : PyType.Union(targets.Select(t => t.Returns)), targets);
    }

    /// <summary><c>movie.get("year")</c> on a TypedDict: the field's type (or None / the default's type);
    /// <c>setdefault("year", 0)</c> is the field's type and <c>pop("year")</c> the field's (or it | the default's).</summary>
    PyType? TypedDictGet(ModuleInfo m, CallExpr call)
    {
        // the stubs give these methods of typing._TypedDict object returns
        if (call.Func is not AttributeExpr { Attr: "get" or "setdefault" or "pop" } ga || call.Args.Count is < 1 or > 2) return null;
        if (ga.Attr == "setdefault" && call.Args.Count != 2) return null;
        if (call.Args[0] is not { Name: null, Stars: 0, Value: var keyExpr } || StringValue(keyExpr) is not string key) return null;
        var receiver = TypeOf(m, ga.Value);
        if (receiver is UnionType ru) return TypedDictUnionGet(m, call, ga.Attr, key, ru);
        if (receiver is not ClassType { IsInstance: true } rc) return null;
        var fields = TypedDictFieldsOf(rc.Info);
        var field = fields?.FirstOrDefault(f => f.Field.Name == key);
        if (field == null || field.Value.Field == null) return null;
        return TypedDictKeyResult(m, call, ga.Attr, field.Value.Field.Type);
    }

    /// <summary>`movie.setdefault("year", "1999")`: the default goes into the TypedDict, so it must be of the key's type (the call's type, from TypedDictGet).</summary>
    IReadOnlyList<CallProblem> TypedDictSetdefaultProblems(ModuleInfo m, CallExpr call, PyType fieldType)
    {
        if (call.Func is not AttributeExpr { Attr: "setdefault" } || call.Args.Count != 2 || fieldType is UnknownType || fieldType.Members.Any(x => x is UnknownType))
            return Array.Empty<CallProblem>();
        var value = call.Args[1].Value;
        var given = TypeOf(m, value);
        if (IsAssignable(fieldType, given, IsFreshValue(m, value))) return Array.Empty<CallProblem>();
        return new[] { new CallProblem(CallProblemKind.Type, value, $"Expected type '{fieldType}', got '{given}' instead") };
    }

    PyType TypedDictKeyResult(ModuleInfo m, CallExpr call, string method, PyType fieldType)
    {
        if (method == "setdefault") return fieldType;
        if (call.Args.Count == 2) return PyType.Union(fieldType, TypeOf(m, call.Args[1].Value));
        return method == "pop" ? fieldType : PyType.Union(fieldType, NoneInstance);
    }

    /// <summary>
    /// The same on a union of TypedDicts (pydantic's CoreSchema) where some of them have the key: each one that has it gives its field's type, and
    /// one that does not gives Unknown (a TypedDict may hold keys it does not list, so their value is not known: as pyright, and PyCharm does not report it).
    /// Null when a member is not a TypedDict, or none has the key: the stubs' Mapping methods then answer.
    /// </summary>
    PyType? TypedDictUnionGet(ModuleInfo m, CallExpr call, string method, string key, UnionType receiver)
    {
        var parts = new List<PyType>();
        bool anyHas = false;
        foreach (var member in receiver.MemberList)
        {
            if (member is not ClassType { IsInstance: true } mc || TypedDictFieldsOf(mc.Info) is not { } fields) return null;
            var field = fields.FirstOrDefault(f => f.Field.Name == key);
            if (field.Field == null) { parts.Add(PyType.Unknown); continue; }
            anyHas = true;
            parts.Add(TypedDictKeyResult(m, call, method, field.Field.Type));
        }
        return anyHas ? PyType.Union(parts) : null;
    }

    /// <summary>TypeVar('T', str, bytes) or TypeVar('T', bound=X): the variable with its constraints or bound, read as types (where they stand: TypeVar runs).</summary>
    TypeVarType TypeVarFromCall(ModuleInfo m, CallExpr call, ConstExpr name)
    {
        var constraints = call.Args.Skip(1).Where(a => a is { Name: null, Stars: 0 }).Select(a => Annotation(m, a.Value, null)).ToList();
        var bound = call.Args.FirstOrDefault(a => a.Name == "bound") is { Value: not ConstExpr { Kind: ConstKind.None } } b ? Annotation(m, b.Value, null) : null;
        // an unresolved bound or constraint says nothing about the arguments
        if (bound is UnknownType) bound = null;
        if (constraints.Any(c => c is UnknownType)) constraints.Clear();
        var flags = call.Args.Where(a => a.Name is "covariant" or "contravariant" or "infer_variance" && a.Value is not ConstExpr { Kind: ConstKind.False }).ToList();
        // infer_variance=True, or a flag that is not a plain True or False, leaves the variance unknown
        var variance = flags.Count == 0 ? Variance.Invariant
            : flags is [{ Name: not "infer_variance", Value: ConstExpr { Kind: ConstKind.True } } f] ? (f.Name == "covariant" ? Variance.Covariant : Variance.Contravariant)
            : Variance.Unknown;
        return new TypeVarType(name.Text.Trim('"', '\''), bound, constraints.Count > 0 ? constraints : null, variance);
    }

    static CallResolution Single(CallTarget t) => new(t.Returns, new[] { t });

    PyType TypeOfCall(ModuleInfo m, CallExpr call) => ResolveCall(m, call).Returns;

    List<CallTarget> CallValue(PyType callee, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var result = new List<CallTarget>();
        foreach (var c in callee.Members)
        {
            switch (c)
            {
                case FunctionType f: result.Add(CallFunction(f, args, callNode)); break;
                case OverloadedType o: result.Add(CallOverloaded(o, args, callNode)); break;
                case ClassType { IsInstance: false } cls: result.Add(CallClass(cls, args, callNode)); break;
                case ClassType inst: result.Add(CallInstance(inst, args, callNode)); break;
                case SignatureType s: result.Add(CallSignature(s, args, callNode)); break;
                case UnknownType: result.Add(new CallTarget(c, c, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>())); break;
                case NeverType: break;
                case ModuleType:
                    result.Add(new CallTarget(c, PyType.Unknown, Array.Empty<ArgumentMapping>(),
                        new[] { new CallProblem(CallProblemKind.NotCallable, callNode, "Module is not callable") }));
                    break;
                default:
                    result.Add(new CallTarget(c, PyType.Unknown, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));
                    break;
            }
        }
        return result;
    }

    CallTarget CallFunction(FunctionType f, IReadOnlyList<ArgInfo> args, Node? callNode, List<CallProblem>? loose = null)
    {
        f = NewSelfFromCls(f, args);
        var ps = CallableParameters(f);
        return CallParams(f, ps, () => ReturnAtCallSite(f, ps, args, callNode), args, callNode, loose);
    }

    /// <summary>
    /// An explicit <c>X.__new__(cls, ...)</c>: Self is the class passed as cls, not X (<c>object.__new__(self.__class__)</c>
    /// makes an instance of that class). Only a subclass of X is taken; anything else keeps X.
    /// </summary>
    FunctionType NewSelfFromCls(FunctionType f, IReadOnlyList<ArgInfo> args)
    {
        if (f.Info.Name != "__new__" || f.BoundFirst || args.Count == 0 || args[0] is not { Name: null, Stars: 0 } first) return f;
        if (f.Subst == null || !f.Subst.TryGetValue("Self", out var self) || self is not ClassType { IsInstance: true } x) return f;
        var classes = first.Type().Members.ToList();
        if (classes.Count == 0 || !classes.All(t => t is ClassType { IsInstance: false } c && IsSubclass(c.Info, x.Info))) return f;
        var subst = new Dictionary<string, PyType>(f.Subst)
        {
            ["Self"] = PyType.Union(classes.Select(t => ((ClassType)t).AsInstance())),
            ["type[Self]"] = PyType.Union(classes),
        };
        return f with { Subst = subst };
    }

    /// <summary>The literal type a written constant has: "a" is Literal['a'], -1 is Literal[-1], True is Literal[True].</summary>
    PyType? ConstantLiteral(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.Int or ConstKind.Str or ConstKind.Bytes or ConstKind.True or ConstKind.False } or UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } }
            => LiteralType(null!, e, null) is ClassType { LiteralValue: not null } lit ? lit : null,
        _ => null,
    };

    /// <summary>
    /// A value just built, whose type would be inferred from the one wanted (as a type checker does): a list, set or dict
    /// display, a comprehension or generator, a call that solved type variables (`list(xs)`, `sorted(xs)`), or a choice of two such values.
    /// </summary>
    bool IsFreshValue(ModuleInfo m, Expr e) => e switch
    {
        ListExpr or SetExpr or DictExpr or CompExpr => true,
        IfExpr i => IsFreshValue(m, i.Body) && IsFreshValue(m, i.Orelse),
        CallExpr c => ResolveCall(m, c).Targets.Any(t => t.Solved is { Count: > 0 }),
        _ => false,
    };

    /// <summary>
    /// The literal an argument is: a written constant, a name every assignment of which is a literal (`mode = "r"`, then `mode = "rb"`:
    /// Literal['r', 'rb']), or arithmetic of literals (`1 << 3`, `"r" + "b"`). Null when any part is not known as a literal.
    /// </summary>
    PyType? ArgumentLiteral(ModuleInfo m, Expr e) => ArgumentLiteral(m, e, new HashSet<Declaration>());

    PyType? ArgumentLiteral(ModuleInfo m, Expr e, HashSet<Declaration> path)
    {
        // ("r", 1), ["a", "b"]: the display with its constants as literals (and its other items as they are)
        var items = e switch { TupleExpr t => t.Items, ListExpr l => l.Items, _ => null };
        if (items == null) return LiteralOf(m, e, path);
        if (items.Count == 0 || items.Any(i => i is StarExpr)) return null;
        var literals = items.Select(i => ArgumentLiteral(m, i, path)).ToList();
        if (literals.All(l => l == null)) return null;
        var types = literals.Select((l, i) => l ?? TypeOf(m, items[i])).ToList();
        return e is TupleExpr ? TupleOf(types) : Inst("list", PyType.Union(types));
    }

    /// <summary>A Literal[...] wanted directly, or as an item of a tuple or list that is wanted.</summary>
    static bool WantsLiteral(PyType t) => t.Members.Any(x => x is ClassType { IsInstance: true } c
        && (c.LiteralValue != null || IsBuiltinSequence(c, "tuple", "list") && (c.TupleItems ?? c.Args).Any(WantsLiteral)));

    static bool IsBuiltinSequence(ClassType c, params string[] names) => c.Info.Module.Name == "builtins" && names.Contains(c.Info.Name);

    /// <summary>
    /// The type of a tuple or list display with the literals of its constants kept where the wanted type wants them
    /// (`("r", 1)` for `tuple[Literal["r", "w"], int]` is `tuple[Literal['r'], int]`); null when nothing wanted fits its shape.
    /// </summary>
    PyType? WithWantedLiterals(PyType expected, PyType literal, PyType plain)
    {
        foreach (var e in expected.Members)
        {
            if (e is not ClassType { IsInstance: true } ec || !WantsLiteral(ec)) continue;
            if (literal is ClassType { TupleItems: { } lits } && plain is ClassType { TupleItems: { } plains } && lits.Count == plains.Count && IsBuiltinSequence(ec, "tuple"))
            {
                bool Wanted(int i) => ec.TupleItems is { } want ? want.Count == lits.Count && WantsLiteral(want[i]) : ec.Args.Count == 1 && WantsLiteral(ec.Args[0]);
                return TupleOf(lits.Select((l, i) => Wanted(i) ? l : plains[i]).ToList());
            }
            if (literal is ClassType { TupleItems: null } && IsBuiltinSequence(ec, "list") && ec.Args.Count == 1 && WantsLiteral(ec.Args[0])) return literal;
        }
        return null;
    }

    /// <summary>A tuple of literals is checked item by item against a wanted tuple of its length (the elements' union alone lets `("x", "a")` pass for `tuple[Literal["r"], str]`).</summary>
    bool TupleItemsFit(PyType expected, PyType at)
    {
        if (at is not ClassType { TupleItems: { } items }) return true;
        return expected.Members.Any(e => e is ClassType { IsInstance: true, TupleItems: { } want } && want.Count == items.Count && want.All(w => w is not (TypeVarTupleType or UnboundedItemsType))
            ? want.Zip(items).All(p => IsAssignable(p.First, p.Second))
            : IsAssignable(e, at));
    }

    PyType? LiteralOf(ModuleInfo m, Expr e, HashSet<Declaration> path)
    {
        if (ConstantLiteral(e) is { } direct) return direct;
        switch (e)
        {
            case NameExpr n when m.Semantics.Resolve(n) is { Declarations.Count: > 0 } sym:
            {
                // a local: only the assignments that reach this use; a name of an outer scope: all of them
                var decls = m.Flow.ReachingDefinitions(n) is { Definitions.Count: > 0 } reach ? reach.Definitions : sym.Declarations;
                if (path.Count > 16 || decls.Count > 16) return null;
                var parts = new List<PyType>();
                foreach (var d in decls)
                {
                    var value = d.Kind != DeclKind.Variable ? null : m.ParentOf(d.Node) switch
                    {
                        AssignStmt a when a.Targets.Any(t => ReferenceEquals(t, d.Node)) => a.Value,
                        AnnAssignStmt { Annotation: NameExpr { Name: "Final" } or AttributeExpr { Attr: "Final" } } an when ReferenceEquals(an.Target, d.Node) => an.Value,
                        _ => null,
                    };
                    // `x = x + 1` in a loop is no literal: a value that needs itself is not known
                    if (value == null || !path.Add(d)) return null;
                    // `pair = ("x", 1)` and then `open_pair(pair)`: a tuple cannot change, so its constants are the literals it was given (a list can)
                    var lit = value is TupleExpr ? ArgumentLiteral(m, value, path) : LiteralOf(m, value, path);
                    path.Remove(d);
                    if (lit == null) return null;
                    parts.Add(lit);
                }
                return PyType.Union(parts);
            }
            case UnaryExpr u when LiteralOf(m, u.Operand, path) is { } operand:
                return LiteralMap(operand, null, (x, _) => LiteralUnary(u.Op, x));
            case BinaryExpr b when LiteralOf(m, b.Left, path) is { } left && LiteralOf(m, b.Right, path) is { } right:
                return LiteralMap(left, right, (x, y) => LiteralBinary(b.Op, x, y!));
            case CompareExpr c when c.Comparators.Count == 1 && LiteralOf(m, c.Left, path) is { } cl && LiteralOf(m, c.Comparators[0], path) is { } cr:
                return LiteralMap(cl, cr, (x, y) => LiteralCompare(c.Ops[0], x, y!) is bool r ? r : null);
            default: return null;
        }
    }

    /// <summary>An operation on each value of one or two literal unions (a few at most); null when any result is not a literal.</summary>
    PyType? LiteralMap(PyType left, PyType? right, Func<object, object?, object?> op)
    {
        static List<object>? Values(PyType t) => t.Members.All(x => x is ClassType { LiteralValue: not null, EnumMember: null })
            ? t.Members.Select(x => ((ClassType)x).LiteralValue!).ToList() : null;
        var xs = Values(left);
        var ys = right == null ? new List<object?> { null } : Values(right)?.Cast<object?>().ToList();
        if (xs == null || ys == null || xs.Count * ys.Count > 32) return null;
        var results = new List<PyType>();
        foreach (var x in xs)
            foreach (var y in ys)
            {
                var r = op(x, y);
                string? cls = r switch { bool => "bool", long => "int", string => "str", LiteralBytes => "bytes", _ => null };
                if (cls == null || Inst(cls) is not ClassType c) return null;
                results.Add(c with { LiteralValue = r });
            }
        return PyType.Union(results);
    }

    static bool Truthy(object v) => v switch { bool b => b, long l => l != 0, string s => s.Length > 0, LiteralBytes by => by.Raw.Length > 0, _ => true };

    static object? LiteralUnary(string op, object x) => (op, x) switch
    {
        ("not", _) => !Truthy(x),
        ("-", long l) => l == long.MinValue ? null : -l,
        ("+", long l) => l,
        ("~", long l) => ~l,
        _ => null,
    };

    /// <summary>`a op b` on two literal values, as Python computes it; null past 64-bit ints or a few KB of text, and for what Python would raise.</summary>
    static object? LiteralBinary(string op, object x, object y)
    {
        if (op == "and") return Truthy(x) ? y : x;
        if (op == "or") return Truthy(x) ? x : y;
        switch (x, y)
        {
            case (string s, string t) when op == "+": return s.Length + t.Length <= 4096 ? s + t : null;
            case (LiteralBytes s, LiteralBytes t) when op == "+": return s.Raw.Length + t.Raw.Length <= 4096 ? new LiteralBytes(s.Raw + t.Raw) : null;
            case (long a, long b):
            {
                System.Numerics.BigInteger p = a, q = b, r;
                switch (op)
                {
                    case "+": r = p + q; break;
                    case "-": r = p - q; break;
                    case "*": r = p * q; break;
                    // Python rounds the quotient down, and the remainder takes the divisor's sign
                    case "//" when b != 0: r = System.Numerics.BigInteger.Divide(p - (((p % q) + q) % q), q); break;
                    case "%" when b != 0: r = ((p % q) + q) % q; break;
                    case "**" when b is >= 0 and <= 64: r = System.Numerics.BigInteger.Pow(p, (int)b); break;
                    case "<<" when b is >= 0 and <= 64: r = p << (int)b; break;
                    case ">>" when b >= 0: r = p >> (int)Math.Min(b, 64); break;
                    case "&": r = p & q; break;
                    case "|": r = p | q; break;
                    case "^": r = p ^ q; break;
                    default: return null;
                }
                return r >= long.MinValue && r <= long.MaxValue ? (long)r : null;
            }
            default: return null;
        }
    }

    static bool? LiteralCompare(string op, object x, object y)
    {
        // values of different classes (1 == True, "a" == b"a") are left to the plain types
        if (x.GetType() != y.GetType()) return null;
        if (op == "==") return x.Equals(y);
        if (op == "!=") return !x.Equals(y);
        int? order = (x, y) switch { (long a, long b) => a.CompareTo(b), (string a, string b) => string.CompareOrdinal(a, b), _ => null };
        return order is not int o ? null : op switch { "<" => o < 0, "<=" => o <= 0, ">" => o > 0, ">=" => o >= 0, _ => null };
    }

    /// <summary>
    /// Maps arguments to parameters, solves type variables from them and computes the call's result.
    /// With <paramref name="loose"/>, a plain str or bool taken where only a Literal of it is wanted is noted there (see <see cref="IsLooseLiteral"/>).
    /// </summary>
    CallTarget CallParams(PyType callee, IReadOnlyList<ParamInfo> ps, Func<PyType> returnType, IReadOnlyList<ArgInfo> args, Node? callNode,
        List<CallProblem>? loose = null)
    {
        var (map, problems) = MapArguments(ps, args, callNode);

        var solved = new Dictionary<string, PyType>();
        var evaluated = new List<(ArgInfo Arg, ParamInfo Param, PyType Type)>();
        // `*args: *Ts`: Ts is the types of the positional arguments it takes, in order (not known past a `*xs`)
        var variadic = ps.FirstOrDefault(p => p is { Category: ParamCategory.VarPositional, Type: TypeVarTupleType });
        var variadicArgs = map.Where(x => x.Param != null && ReferenceEquals(x.Param, variadic)).Select(x => x.Arg).ToList();
        if (variadic != null)
            SolveVariadic((TypeVarTupleType)variadic.Type, args.Any(a => a.Stars == 1) ? null : variadicArgs.Select(a => a.Type()).ToList(), solved);
        foreach (var (arg, param) in map)
        {
            if (param == null || arg.Stars != 0 || ReferenceEquals(param, variadic)) continue;
            var at = arg.Type();
            evaluated.Add((arg, param, at));
            // TypeForm[T]: T is the type the argument spells (`list[int]`, `int | None`, `"Model"`), not the type of its value
            if (IsTypeForm(param.Type, out var formOf) && arg.Form != null) Unify(new TypeVarType(formOf), arg.Form(), solved);
            else Unify(param.Type, at, solved);
        }
        // what a callable argument solved a Ts to counts when no *args or tuple did; those are checked against it below
        var exact = new HashSet<string>(solved.Keys);
        foreach (var key in solved.Keys.Where(k => k.StartsWith('*')).ToList())
        {
            solved.TryAdd(key[1..], solved[key]);
            solved.Remove(key);
        }
        // type[T] in a result or another parameter follows what T was solved to
        foreach (var (name, value) in solved.ToList())
            if (!name.StartsWith("type[", StringComparison.Ordinal) && !solved.ContainsKey($"type[{name}]"))
                solved[$"type[{name}]"] = MapMembers(value, t => t is ClassType { IsInstance: true } c ? c.AsClassObject() : PyType.Unknown);
        foreach (var (arg, param, at0) in evaluated)
        {
            var at = at0;
            if (IsTypeForm(param.Type, out _)) continue;   // any type expression is a TypeForm
            if (CheckVariadicCallback(param.Type, at, arg, solved, exact, variadic, variadicArgs) is { } callbackProblems)
            {
                problems.AddRange(callbackProblems);
                continue;
            }
            var expected = Substitute(param.Type, solved);
            // a constant written in the call is the literal it spells where a Literal[...] is wanted, in a tuple or list display too
            bool itemwise = false;
            if (WantsLiteral(expected) && arg.Literal?.Invoke() is { } literal)
            {
                if (expected.Members.Any(x => x is ClassType { IsInstance: true, LiteralValue: not null })) at = literal;
                else if (WithWantedLiterals(expected, literal, at) is { } withLiterals) { at = withLiterals; itemwise = true; }
            }
            if (!IsAssignable(expected, at) && !(arg.Fresh?.Invoke() == true && IsAssignable(expected, at, fresh: true)) || itemwise && !TupleItemsFit(expected, at))
                problems.Add(new CallProblem(CallProblemKind.Type, (Node?)arg.Node ?? callNode,
                    $"Expected type '{expected}', got '{at}' instead"));
            else if (loose != null && IsLooseLiteral(expected, at))
                loose.Add(new CallProblem(CallProblemKind.Type, (Node?)arg.Node ?? callNode, $"Expected type '{expected}', got '{at}' instead"));
        }

        var ret = EraseTypeVars(Substitute(returnType(), solved));
        var mappings = map.Where(x => x.Arg.Node != null).Select(x => new ArgumentMapping(x.Arg.Node!, x.Param)).ToList();
        return new CallTarget(callee, ret, mappings, problems, solved);
    }

    /// <summary>
    /// A str, int or bool that is no literal, taken where the parameter wants literals of its class (Literal['r', 'w'], Literal[True])
    /// and nothing else that would take it: lenient for a plain call, not enough to pick an overload.
    /// </summary>
    bool IsLooseLiteral(PyType expected, PyType arg)
    {
        var literals = expected.Members.OfType<ClassType>().Where(c => c is { IsInstance: true, LiteralValue: not null }).ToList();
        if (literals.Count == 0) return false;
        var rest = expected.Members.Where(x => x is not ClassType { LiteralValue: not null }).ToList();
        return arg.Members.Any(a => a is ClassType { IsInstance: true, LiteralValue: null, EnumMember: null } c
            && literals.Any(l => ReferenceEquals(l.Info, c.Info)) && !rest.Any(r => IsAssignable(r, c)));
    }

    /// <summary>Records what a Ts is solved to (null: types of unknown length); two different solutions leave it unknown.</summary>
    void SolveVariadic(TypeVarTupleType ts, IReadOnlyList<PyType>? types, Dictionary<string, PyType> solved, bool fromCallable = false)
    {
        // a callable's parameters solve it under a key of their own, taken only when nothing else solved it
        string key = fromCallable ? "*" + ts.Name : ts.Name;
        PyType value = types != null && BuiltinClass("tuple") is { } tuple ? TupleOf(tuple, types) : PyType.Unknown;
        solved[key] = solved.TryGetValue(key, out var prev) && !prev.Equals(value) ? PyType.Unknown : value;
    }

    /// <summary>
    /// A callback for a `Callable[[*Ts], R]` parameter whose Ts the `*args` or a tuple argument solved (`loop.call_soon(cb, 1)`):
    /// it must take those arguments, so calling it with them is checked, with the problems shown where the arguments are.
    /// Null when the parameter is not such a callable, or Ts is not known.
    /// </summary>
    List<CallProblem>? CheckVariadicCallback(PyType paramType, PyType callback, ArgInfo arg, Dictionary<string, PyType> solved,
        HashSet<string> exact, ParamInfo? variadic, List<ArgInfo> variadicArgs)
    {
        if (paramType is not SignatureType { Params: { } sps } || sps.Count == 0 || sps[^1] is not { Category: ParamCategory.VarPositional, Type: TypeVarTupleType ts }) return null;
        if (!exact.Contains(ts.Name) || !solved.TryGetValue(ts.Name, out var value) || value is not ClassType { TupleItems: { } types } || types.Any(t => t is TypeVarTupleType)) return null;
        var callArgs = sps.Take(sps.Count - 1).Select(p => Substitute(p.Type, solved)).Select(t => new ArgInfo(null, null, 0, () => t)).ToList();
        // the arguments the *args took stand for themselves; a tuple's items only by their types
        if (variadic?.Type is TypeVarTupleType own && own.Name == ts.Name && variadicArgs.Count == types.Count) callArgs.AddRange(variadicArgs);
        else callArgs.AddRange(types.Select(t => new ArgInfo(null, null, 0, () => t)));
        var problems = new List<CallProblem>();
        foreach (var c in callback.Members)
            if (c is FunctionType or SignatureType { Params: not null })
                foreach (var t in CallValue(c, callArgs, arg.Node)) problems.AddRange(t.Problems);
        return problems;
    }

    CallTarget CallOverloaded(OverloadedType o, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var targets = PickOverload(o, args, callNode, out var pick);
        if (pick != null) pick = AmbiguousThroughUnknown(o, args, callNode, targets.Count, pick);
        // a union argument no overload takes as a whole: each member goes to the overload that takes it (os.path.basename(str | PathLike[str]))
        pick ??= SplitUnionArgument(o, args, callNode)
                 ?? targets.FirstOrDefault(t => t.Problems.Count == 0);
        if (pick != null) return pick;
        var typed = targets.Where(t => t.Problems.All(p => p.Kind != CallProblemKind.Arity)).ToList();
        if (typed.Count >= 2) return UnexpectedTypes(typed, args, callNode);
        if (typed.Count == 1) return typed[0];
        return new CallTarget(o, PyType.Unknown, Array.Empty<ArgumentMapping>(),
            new[] { new CallProblem(CallProblemKind.Arity, callNode, $"No overload of '{o.Overloads[0].Info.Name}' matches these arguments") });
    }

    /// <summary>
    /// Two or more overloads take the arguments' count but none their types: as PyCharm, one report on the call with the arguments'
    /// types and what each of those overloads wants ("Unexpected type(s): (str | None, list[int]) Possible type(s): (str, None)
    /// (str, Any)"), not the first overload's own mismatches (which would say a `[]` given for `default` should be None). The call
    /// still returns what the first of them does.
    /// </summary>
    static CallTarget UnexpectedTypes(List<CallTarget> typed, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var given = args.Where(a => a.Node != null).ToList();
        string Wanted(CallTarget t) => "(" + string.Join(", ", given.Select(a =>
            t.Mappings.FirstOrDefault(x => ReferenceEquals(x.Arg, a.Node))?.Param?.Type.ToString() ?? "Any")) + ")";
        string message = $"Unexpected type(s): ({string.Join(", ", given.Select(a => a.Type().ToString()))}) Possible type(s): "
            + string.Join(" ", typed.Select(Wanted).Distinct());
        return typed[0] with { Problems = new[] { new CallProblem(CallProblemKind.Type, callNode, message) } };
    }

    /// <summary>
    /// An argument whose type is unknown, or has unknown parts (`sum(s.area() for s in shapes)` with unknown shapes), may fit
    /// the first overload only because nothing is known about it: when a later overload takes the arguments too and returns
    /// something else, the call returns Unknown, as in pyright and mypy. The pick still maps and checks the arguments.
    /// </summary>
    CallTarget AmbiguousThroughUnknown(OverloadedType o, IReadOnlyList<ArgInfo> args, Node? callNode, int tried, CallTarget pick)
    {
        if (tried >= o.Overloads.Count || pick.Returns is UnknownType || !args.Any(a => PartlyUnknown(a.Type(), 0))) return pick;
        foreach (var f in o.Overloads.Skip(tried))
        {
            var loose = new List<CallProblem>();
            var t = CallFunction(f, args, callNode, loose);
            if (t.Problems.Count == 0 && loose.Count == 0 && !t.Returns.Equals(pick.Returns)) return pick with { Returns = PyType.Unknown };
        }
        return pick;
    }

    static bool PartlyUnknown(PyType t, int depth) => depth < 4 && t switch
    {
        UnknownType => true,
        UnionType u => u.MemberList.Any(x => PartlyUnknown(x, depth + 1)),
        ClassType c => c.Args.Any(x => PartlyUnknown(x, depth + 1)) || (c.TupleItems?.Any(x => PartlyUnknown(x, depth + 1)) ?? false),
        _ => false,
    };

    /// <summary>
    /// Calls every overload; <paramref name="pick"/> is the first that takes the arguments as they are. A plain str or bool
    /// where an overload wants a Literal does not count there, so a later overload taking it is picked (open(p, mode) with mode: str).
    /// </summary>
    List<CallTarget> PickOverload(OverloadedType o, IReadOnlyList<ArgInfo> args, Node? callNode, out CallTarget? pick)
    {
        var targets = new List<CallTarget>();
        pick = null;
        foreach (var f in o.Overloads)
        {
            var loose = new List<CallProblem>();
            var t = CallFunction(f, args, callNode, loose);
            targets.Add(t);
            if (t.Problems.Count == 0 && loose.Count == 0) { pick = t; break; }
        }
        return targets;
    }

    /// <summary>
    /// The first union argument whose members each go to some overload: the call returns what those overloads return, joined.
    /// Null when there is no such argument (one member no overload takes, or no union at all).
    /// </summary>
    CallTarget? SplitUnionArgument(OverloadedType o, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        for (int i = 0; i < args.Count; i++)
        {
            if (args[i].Stars != 0 || args[i].Type() is not UnionType u) continue;
            var picks = new List<CallTarget>();
            foreach (var member in u.MemberList)
            {
                var split = args.ToList();
                split[i] = args[i] with { Type = () => member, Literal = null };
                var targets = PickOverload(o, split, callNode, out var pick);
                pick ??= targets.FirstOrDefault(t => t.Problems.Count == 0);
                if (pick == null) break;
                picks.Add(pick);
            }
            if (picks.Count < u.MemberList.Count) continue;
            // a type variable counts when every member solved it, as the union of what they solved it to
            var solved = picks[0].Solved?.Keys.Where(k => picks.All(p => p.Solved?.ContainsKey(k) == true))
                .ToDictionary(k => k, k => PyType.Union(picks.Select(p => p.Solved![k])));
            return new CallTarget(picks[0].Callee, PyType.Union(picks.Select(p => p.Returns)), picks[0].Mappings, Array.Empty<CallProblem>(), solved);
        }
        return null;
    }

    CallTarget CallSignature(SignatureType s, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        if (s.Params == null) return new CallTarget(s, s.Return, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>());
        // a ParamSpec not solved here (`f: Callable[P, R]` in the body of a decorator) takes any arguments after the leading ones
        return CallParams(s, s.Spec == null ? s.Params : s.Params.Concat(AnyArguments).ToList(), () => s.Return, args, callNode);
    }

    CallTarget CallInstance(ClassType inst, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var hit = FindMember(inst, "__call__", true, null, out bool unknownBase);
        if (hit == null)
        {
            var problems = unknownBase ? Array.Empty<CallProblem>()
                : new[] { new CallProblem(CallProblemKind.NotCallable, callNode, $"'{inst}' object is not callable") };
            return new CallTarget(inst, PyType.Unknown, Array.Empty<ArgumentMapping>(), problems);
        }
        var bound = BoundMember(hit, inst, true);
        var targets = CallValue(bound, args, callNode);
        return targets.Count > 0 ? targets[0] with { Callee = inst } : new CallTarget(inst, PyType.Unknown, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>());
    }

    CallTarget CallClass(ClassType cls, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var none = Array.Empty<ArgumentMapping>();
        var noProblems = Array.Empty<CallProblem>();
        string qn = cls.Info.QualifiedName;

        if (qn == "builtins.type" && args.Count == 1 && args[0].Stars == 0 && args[0].Name == null)
        {
            var arg = args[0].Type();
            return new CallTarget(cls, MapMembers(arg, t => t switch
            {
                ClassType { IsInstance: true } c => c.Widened().AsClassObject(),
                ClassType { IsInstance: false } co => MetaclassOf(co.Info, out bool unknownMeta) ?? (unknownMeta ? PyType.Unknown : BuiltinClass("type") ?? PyType.Unknown),
                _ => PyType.Unknown,
            }), none, noProblems);
        }
        if (qn is "typing.ParamSpec" or "typing_extensions.ParamSpec")
            return new CallTarget(cls, new ParamSpecType(args.FirstOrDefault()?.Node?.Value is ConstExpr p ? p.Text.Trim('"', '\'') : "?"), none, noProblems);
        if (qn is "typing.TypeVarTuple" or "typing_extensions.TypeVarTuple")
            return new CallTarget(cls, new TypeVarTupleType(args.FirstOrDefault()?.Node?.Value is ConstExpr ts ? ts.Text.Trim('"', '\'') : "?"), none, noProblems);
        if (qn is "typing.TypeVar" or "typing_extensions.TypeVar")
        {
            var first = args.FirstOrDefault()?.Node?.Value as ConstExpr;
            string name = first != null ? first.Text.Trim('"', '\'') : "?";
            return new CallTarget(cls, new TypeVarType(name), none, noProblems);
        }

        var data = ClassDataOf(cls.Info);
        var selfArgs = cls.Args.Count > 0 ? cls.Args : ClassTypeParamsAsArgs(cls.Info);
        var receiver = new ClassType(cls.Info, selfArgs, true);

        // library features with their own construction rules
        if (IsEnumClass(cls.Info))
            return new CallTarget(cls, cls.AsInstance(), none, noProblems);   // Color(1) looks a member up by value

        var synthesized = SynthesizedInit(receiver);

        var init = FindMember(receiver, "__init__", true, null, out bool unkInit);
        var @new = FindMember(receiver, "__new__", true, null, out bool unkNew);
        // a base that is not known, before the __init__ found, may have its own: the call is not checked against the later one
        // (class D(httpx.Response, Base) where httpx is not installed: D(status_code=1) is not Base's to judge)
        bool initHidden = init != null && UnknownBaseBefore(cls.Info, init.Owner.Info);
        bool userInit = synthesized == null && init != null && init.Owner.Info.QualifiedName != "builtins.object" && !initHidden;
        bool userNew = synthesized == null && @new != null && @new.Owner.Info.QualifiedName != "builtins.object" && !initHidden && !UnknownBaseBefore(cls.Info, @new.Owner.Info);

        CallTarget? inner = null;
        if (synthesized != null)
            inner = CallSignature(synthesized, args, callNode);
        else if (userInit)
            inner = Pick(CallValue(BoundMember(init!, receiver, true), args, callNode));
        else if (userNew)
        {
            // __new__ is an implicit static method; when constructing, the class is supplied automatically
            var withoutCls = MapMembers(BoundMember(@new!, receiver, true), t => t switch
            {
                FunctionType f => f with { BoundFirst = true },
                OverloadedType o => new OverloadedType(o.Overloads.Select(f => f with { BoundFirst = true }).ToList()),
                SignatureType { Origin: not null, Params: [{ Category: ParamCategory.PositionalOnly or ParamCategory.Normal }, ..] } s => s with { Params = s.Params.Skip(1).ToList() },
                _ => t,
            });
            inner = Pick(CallValue(withoutCls, args, callNode));
        }
        else if (!unkInit && !unkNew && !data.HasUnknownBase && args.Count > 0 && !HasUnknownAncestor(cls.Info))
            inner = new CallTarget(cls, PyType.Unknown, none,
                new[] { new CallProblem(CallProblemKind.Arity, args[0].Node ?? callNode, $"'{cls.Info.Name}()' takes no arguments") });

        PyType[] resultArgs;
        if (cls.Args.Count > 0) resultArgs = cls.Args.ToArray();
        else
            resultArgs = data.TypeParams.SelectMany(tp =>
            {
                var v = inner?.Solved != null && inner.Solved.TryGetValue(TypeParamName(tp), out var found) && !ContainsTypeVar(found) ? found : PyType.Unknown;
                // a Ts gives the class its types one by one: Array(1, "a") is Array[int, str]
                if (tp is TypeVarTupleType) return v is ClassType { TupleItems: { } types } ? types : new[] { PyType.Unknown };
                return new[] { v };
            }).ToArray();

        PyType instance = new ClassType(cls.Info, resultArgs, true);
        // __new__ may return something else (e.g. a subclass or Self)
        if (userNew && !userInit && inner != null && inner.Returns is ClassType { IsInstance: true } nr)
        {
            // another class (a subclass), or this class with arguments __new__ spells out (`-> count[int]`)
            if (!ReferenceEquals(nr.Info, cls.Info) && nr.Info.Name != "Self" && IsSubclass(nr.Info, cls.Info)) instance = nr;
            else if (ReferenceEquals(nr.Info, cls.Info) && cls.Args.Count == 0 && nr.Args.Any(a => a is not UnknownType)) instance = nr;
        }

        return new CallTarget(cls, instance, inner?.Mappings ?? none, inner?.Problems ?? noProblems, inner?.Solved);
    }

    bool HasUnknownAncestor(ClassInfo c) => Mro(c).Any(e => e is UnknownType);

    /// <summary>
    /// Whether a base that is not known comes before <paramref name="owner"/> in the order Python looks members up in (the bases in
    /// their order, depth first as the MRO goes): a member found in owner may be the unknown base's instead. The MRO itself keeps
    /// unknown bases at its end, so it cannot say.
    /// </summary>
    bool UnknownBaseBefore(ClassInfo c, ClassInfo owner, int depth = 0)
    {
        if (ReferenceEquals(c, owner) || depth > 20) return false;
        foreach (var b in ClassDataOf(c).Bases)
        {
            if (b is UnknownType) return true;
            if (b is not ClassType bc) continue;   // Generic[P], Protocol and the like: no members of their own to hide anything
            if (ReferenceEquals(bc.Info, owner) || Mro(bc.Info).Any(e => e is ClassType ec && ReferenceEquals(ec.Info, owner)))
                return UnknownBaseBefore(bc.Info, owner, depth + 1);
        }
        return false;
    }

    static CallTarget? Pick(List<CallTarget> targets) => targets.Count == 0 ? null : targets[0];

    static bool ContainsTypeVar(PyType t) => t switch
    {
        TypeVarType or ParamSpecType or TypeVarTupleType => true,
        ClassType c => c.Args.Any(ContainsTypeVar) || (c.TupleItems?.Any(ContainsTypeVar) ?? false),
        UnionType u => u.MemberList.Any(ContainsTypeVar),
        SignatureType sig => sig.Spec != null || ContainsTypeVar(sig.Return) || (sig.Params?.Any(p => ContainsTypeVar(p.Type)) ?? false),
        _ => false,
    };

    PyType SuperOfCall(ModuleInfo m, CallExpr call, IReadOnlyList<ArgInfo> args)
    {
        if (args.Count == 2)
        {
            var cls = args[0].Type();
            var obj = args[1].Type();
            if (cls is ClassType { IsInstance: false } c) return new SuperType(c.Info, obj);
            return PyType.Unknown;
        }
        for (Node? p = call; p != null; p = m.ParentOf(p))
        {
            if (p is not FunctionDef fd) continue;
            var fi = FunctionOf(m, fd);
            if (fi.Owner == null) return PyType.Unknown;
            var first = fd.Params.FirstOrDefault(x => x.Kind == ParamKind.Normal);
            if (first == null) return PyType.Unknown;
            var selfType = new ClassType(fi.Owner, ClassTypeParamsAsArgs(fi.Owner), !fi.IsClassMethod);
            return new SuperType(fi.Owner, selfType);
        }
        return PyType.Unknown;
    }

    // ------------------------------------------------------------ calling dunder methods from the evaluator

    static IReadOnlyList<ArgInfo> ArgsOf(IReadOnlyList<PyType> types) =>
        types.Select(t => new ArgInfo(null, null, 0, () => t)).ToList();

    /// <summary>
    /// Calls <c>receiver.method(*args)</c>. Returns null when no member of the receiver has the method;
    /// <paramref name="ok"/> says whether the arguments fit without any problem.
    /// </summary>
    PyType? CallMethod(PyType receiver, string method, IReadOnlyList<PyType> args, out bool ok)
    {
        ok = true;
        var results = new List<PyType>();
        bool anyFound = false;
        foreach (var r in receiver.Members)
        {
            if (r is UnknownType) { results.Add(r); anyFound = true; continue; }
            if (r is not ClassType c) continue;
            var hit = FindMember(c, method, c.IsInstance, null, out bool unknownBase);
            if (hit == null)
            {
                if (unknownBase) { results.Add(PyType.Unknown); anyFound = true; }
                continue;
            }
            anyFound = true;
            var bound = BoundMember(hit, c, c.IsInstance);
            var targets = CallValue(bound, ArgsOf(args), null);
            foreach (var t in targets)
            {
                if (t.Problems.Count > 0) ok = false;
                results.Add(t.Returns);
            }
        }
        return anyFound ? (results.Count == 0 ? PyType.Unknown : PyType.Union(results)) : null;
    }

    PyType CallResultNoArgs(PyType callee)
    {
        var targets = CallValue(callee, Array.Empty<ArgInfo>(), null);
        return targets.Count == 0 ? PyType.Unknown : PyType.Union(targets.Select(t => t.Returns));
    }

    static readonly Dictionary<string, (string Fwd, string Rev)> BinaryDunders = new()
    {
        ["+"] = ("__add__", "__radd__"), ["-"] = ("__sub__", "__rsub__"), ["*"] = ("__mul__", "__rmul__"),
        ["/"] = ("__truediv__", "__rtruediv__"), ["//"] = ("__floordiv__", "__rfloordiv__"),
        ["%"] = ("__mod__", "__rmod__"), ["**"] = ("__pow__", "__rpow__"), ["@"] = ("__matmul__", "__rmatmul__"),
        ["&"] = ("__and__", "__rand__"), ["|"] = ("__or__", "__ror__"), ["^"] = ("__xor__", "__rxor__"),
        ["<<"] = ("__lshift__", "__rlshift__"), [">>"] = ("__rshift__", "__rrshift__"),
    };

    PyType BinaryOp(PyType left, string op, PyType right)
    {
        if (!BinaryDunders.TryGetValue(op, out var names)) return PyType.Unknown;
        var results = new List<PyType>();
        foreach (var l in left.Members)
            foreach (var r in right.Members)
            {
                if (l is UnknownType || r is UnknownType)
                {
                    // keep the "depends on itself" marker so enclosing unions can still discard it
                    results.Add(l is UnknownType { FromCycle: true } || r is UnknownType { FromCycle: true } ? PyType.CycleUnknown : PyType.Unknown);
                    continue;
                }
                var fwd = CallMethod(l, names.Fwd, new[] { r }, out bool ok1);
                if (fwd != null && ok1) { results.Add(fwd); continue; }
                var rev = CallMethod(r, names.Rev, new[] { l }, out bool ok2);
                if (rev != null && ok2) { results.Add(rev); continue; }
                results.Add(fwd ?? rev ?? PyType.Unknown);
            }
        return results.Count == 0 ? PyType.Unknown : PyType.Union(results);
    }

    /// <summary>Type of the items produced by iterating a value.</summary>
    public PyType IterElement(PyType iterable) => MapMembers(iterable, t =>
    {
        if (t is UnknownType) return t;
        if (t is ClassType { IsInstance: false } enumClass && IsEnumClass(enumClass.Info)) return enumClass.AsInstance();   // for c in Color
        if (t is not ClassType { IsInstance: true } c) return PyType.Unknown;
        if (TupleItemsOf(c) is { } items) return items.Count == 0 ? PyType.Unknown : PyType.Union(items);
        var it = CallMethod(c, "__iter__", Array.Empty<PyType>(), out _);
        if (it != null)
        {
            var next = CallMethod(it, "__next__", Array.Empty<PyType>(), out _);
            if (next != null) return next;
        }
        return CallMethod(c, "__getitem__", new[] { Inst("int") }, out _) ?? PyType.Unknown;
    });

    PyType ContextEnterType(PyType manager) =>
        MapMembers(manager, t => CallMethod(t, "__enter__", Array.Empty<PyType>(), out _) ?? PyType.Unknown);

    PyType TypeOfSubscript(ModuleInfo m, SubscriptExpr s)
    {
        var recv = TypeOf(m, s.Value);
        return MapMembers(recv, r =>
        {
            switch (r)
            {
                case ClassType { IsInstance: false } enumClass when IsEnumClass(enumClass.Info):
                    return enumClass.AsInstance();                                   // Color["RED"]

                case ClassType { IsInstance: false }:
                case SpecialFormType:
                    return AnnotationToValue(AnnotationSubscript(m, s, null));

                case ClassType { IsInstance: true } td when StringValue(s.Index) is string key && TypedDictFieldsOf(td.Info) is { } tdFields:
                    return tdFields.FirstOrDefault(f => f.Field.Name == key).Field?.Type ?? PyType.Unknown;

                case ClassType { IsInstance: true } tupleLike when TupleItemsOf(tupleLike) is { } items:
                {
                    if (ConstIndex(s.Index) is int i)
                    {
                        int idx = i < 0 ? items.Count + i : i;
                        if (idx >= 0 && idx < items.Count) return items[idx];
                        return PyType.Unknown;
                    }
                    if (s.Index is SliceExpr) return TupleHomogeneous(tupleLike.Args.Count > 0 ? tupleLike.Args[0] : PyType.Union(items));
                    break;
                }

                // tuple[int, str, *tuple[bytes, ...]]: the items before the star by their index, those after it from the end;
                // past them, the star's item or one of the items on its other side
                case ClassType { IsInstance: true } atLeast when ConstIndex(s.Index) is int i && AtLeastParts(atLeast) is { } parts:
                    if (i >= 0 && i < parts.Before.Count) return parts.Before[i];
                    if (i < 0 && -i <= parts.After.Count) return parts.After[parts.After.Count + i];
                    if (i >= 0) return PyType.Union(parts.After.Take(i - parts.Before.Count + 1).Prepend(parts.Item));
                    return PyType.Union(parts.Before.Skip(Math.Max(0, parts.Before.Count - (-i - parts.After.Count))).Prepend(parts.Item));
            }
            if (r is ClassType { IsInstance: true } inst)
                return CallMethod(inst, "__getitem__", new[] { TypeOf(m, s.Index) }, out _) ?? PyType.Unknown;
            return PyType.Unknown;
        });
    }

    /// <summary>A parameter written `TypeForm[T]` (PEP 747), with the name of its variable.</summary>
    static bool IsTypeForm(PyType t, out string variable)
    {
        variable = t is TypeVarType { Name: var n } && n.StartsWith("TypeForm[", StringComparison.Ordinal) ? n[9..^1] : "";
        return variable.Length > 0;
    }

    /// <summary>The type an argument spells as a type expression (`list[int]` is a list of ints); for a value that is not one, the instance of the class it holds.</summary>
    PyType TypeFormOf(ModuleInfo m, Expr e)
    {
        var form = AnnotationType(m, e);
        if (!form.IsDynamic) return form;
        return MapMembers(TypeOf(m, e), t => t is ClassType { IsInstance: false } c ? c.AsInstance() : PyType.Unknown);
    }

    static int? ConstIndex(Expr e) => e switch
    {
        ConstExpr { Kind: ConstKind.Int } c when int.TryParse(c.Text.Replace("_", ""), out var v) => v,
        UnaryExpr { Op: "-", Operand: ConstExpr { Kind: ConstKind.Int } c } when int.TryParse(c.Text.Replace("_", ""), out var v) => -v,
        _ => null,
    };

    // ------------------------------------------------------------ type variable solving and assignability

    /// <summary>Binds type variables in <paramref name="param"/> by matching it against an argument type.</summary>
    void Unify(PyType param, PyType arg, Dictionary<string, PyType> solved)
    {
        switch (param)
        {
            case TypeVarType { Name: var wrapped } when wrapped.StartsWith("type[", StringComparison.Ordinal) && wrapped != "type[Self]":
                // a parameter written type[T]: the class an argument is gives T its instance
                foreach (var a in arg.Members)
                    if (a is ClassType { IsInstance: false } cls) Unify(new TypeVarType(wrapped[5..^1]), cls with { IsInstance = true }, solved);
                return;

            case var _ when IsTypeForm(param, out var formOf):
                // TypeForm[T] matched with a value, the expression unknown: a class gives T its instance, as type[T] does
                foreach (var a in arg.Members)
                    if (a is ClassType { IsInstance: false } cls) Unify(new TypeVarType(formOf), cls with { IsInstance = true }, solved);
                return;

            case TypeVarType tv:
                if (tv.Name == "Self" || arg is NeverType) return;
                arg = WithinTypeVar(tv, arg);
                solved[tv.Name] = solved.TryGetValue(tv.Name, out var prev) && !prev.Equals(arg) ? PyType.Union(prev, arg) : arg;
                return;

            case ParamSpecType p:
                // the ParamSpec argument of a class (`_Wrapper[P, R]`): the parameter list given for it
                if (arg is SignatureType { Params: not null } list) solved[p.Name] = list;
                return;

            case ClassType pc when pc.Args.Count > 0 || pc.TupleItems != null:
                foreach (var a in arg.Members)
                {
                    if (a is not ClassType ac) continue;
                    var spec = SpecializeAs(ac, pc.Info);
                    if (spec == null)
                    {
                        // a protocol is satisfied structurally: solve its variables from the matching members
                        if (ClassDataOf(pc.Info).IsProtocol && ac.IsInstance) UnifyProtocol(pc, ac, solved);
                        continue;
                    }
                    // tuple[int, *Ts] matched with a tuple's items, Array[*Ts] with a class's arguments
                    if (pc.TupleItems != null && pc.TupleItems.Any(x => x is TypeVarTupleType))
                        UnifyVariadic(pc.TupleItems, spec.TupleItems, solved);
                    else if (pc.TupleItems == null && pc.Args.Any(x => x is TypeVarTupleType))
                        UnifyVariadic(pc.Args, spec.Args.Count == 0 ? null : spec.Args, solved, unknownInTheMiddle: true);
                    else if (pc.TupleItems != null && spec.TupleItems != null && pc.TupleItems.Count == spec.TupleItems.Count)
                        for (int i = 0; i < pc.TupleItems.Count; i++) Unify(pc.TupleItems[i], spec.TupleItems[i], solved);
                    else
                        for (int i = 0; i < pc.Args.Count && i < spec.Args.Count; i++) Unify(pc.Args[i], spec.Args[i], solved);
                }
                return;

            case UnionType u:
            {
                // match concrete alternatives first (e.g. None), then bind variables against what is left
                var rest = arg.Members.Where(a => !u.MemberList.Any(pm => !ContainsTypeVar(pm) && IsAssignable(pm, a))).ToList();
                var open = u.MemberList.Where(ContainsTypeVar).ToList();
                if (rest.Count == 0 || open.Count == 0) return;
                foreach (var pm in open) foreach (var a in rest) Unify(pm, a, solved);
                return;
            }

            case SignatureType ps:
                foreach (var a in arg.Members)
                {
                    PyType? ret = a switch
                    {
                        FunctionType f => ReturnTypeOf(f),
                        SignatureType s => s.Return,
                        ClassType { IsInstance: false } made => made with { IsInstance = true },   // a class called makes an instance of it
                        _ => null,
                    };
                    if (ret != null) Unify(ps.Return, ret, solved);
                }
                if (ps.Spec != null && arg.Members.Count == 1 && ParametersOfValue(arg) is { Params: not null } given) SolveParamSpec(ps, given, solved);
                if (ps.Params is [.., { Category: ParamCategory.VarPositional, Type: TypeVarTupleType ts }]) SolveVariadicFromCallable(ps, ts, arg, solved);
                return;
        }
    }

    /// <summary>
    /// `[A, *Ts, B]` matched with the given items (a tuple's, a class's arguments): the ends first, Ts the items between them.
    /// Ts stays unknown when the length is not known (null items, a `*Us` outside the middle, too few items, or with
    /// <paramref name="unknownInTheMiddle"/> the one Unknown a class keeps where an unsolved Ts stands).
    /// </summary>
    void UnifyVariadic(IReadOnlyList<PyType> pattern, IReadOnlyList<PyType>? given, Dictionary<string, PyType> solved, bool unknownInTheMiddle = false)
    {
        int v = pattern.ToList().FindIndex(x => x is TypeVarTupleType);
        var ts = (TypeVarTupleType)pattern[v];
        int after = pattern.Count - v - 1;
        if (given == null || given.Count < v + after) { SolveVariadic(ts, null, solved); return; }
        for (int i = 0; i < v; i++) Unify(pattern[i], given[i], solved);
        for (int j = 0; j < after; j++) Unify(pattern[v + 1 + j], given[given.Count - after + j], solved);
        var middle = given.Skip(v).Take(given.Count - v - after).ToList();
        bool known = !given.Take(v).Concat(given.Skip(given.Count - after)).Any(x => x is TypeVarTupleType)
            && !(unknownInTheMiddle && middle is [UnknownType]);
        SolveVariadic(ts, known ? middle : null, solved);
    }

    /// <summary>
    /// `Callable[[A, *Ts], R]` matched with what is passed: A takes its first positional parameter and Ts the rest. Left
    /// unknown for a `*args` or a parameter with a default (how many it is given is not fixed) or a value of unknown shape.
    /// </summary>
    void SolveVariadicFromCallable(SignatureType param, TypeVarTupleType ts, PyType arg, Dictionary<string, PyType> solved)
    {
        var lead = param.Params!.Take(param.Params!.Count - 1).ToList();
        var given = arg.Members.Count == 1 ? ParametersOfValue(arg)?.Params : null;
        var positional = given?.TakeWhile(p => p.Category is ParamCategory.PositionalOnly or ParamCategory.Normal).ToList();
        if (given == null || positional!.Count < lead.Count || given.Skip(positional.Count).Any(p => p.Category == ParamCategory.VarPositional)
            || positional.Skip(lead.Count).Any(p => p.HasDefault))
        {
            SolveVariadic(ts, null, solved, fromCallable: true);
            return;
        }
        for (int i = 0; i < lead.Count; i++) Unify(lead[i].Type, positional[i].Type, solved);
        SolveVariadic(ts, positional.Skip(lead.Count).Select(p => p.Type).ToList(), solved, fromCallable: true);
    }

    /// <summary>
    /// What an argument solves a bounded or constrained variable to: a constrained one takes the constraint the argument
    /// matches; an argument outside the bound or the constraints solves it to them, so the argument check reports it
    /// (and an overload such as ast.parse's `(source: _T) -> _T`, _T bound to AST, does not match a str).
    /// </summary>
    PyType WithinTypeVar(TypeVarType tv, PyType arg)
    {
        if (tv.Constraints is { Count: > 0 } cs)
        {
            var matched = new List<PyType>();
            foreach (var a in arg.Members)
            {
                if (a is UnknownType or TypeVarType) { matched.Add(a); continue; }
                var c = cs.FirstOrDefault(x => IsAssignable(x, a));
                if (c == null) return PyType.Union(cs);
                matched.Add(c);
            }
            return matched.Count == 0 ? arg : PyType.Union(matched);
        }
        return tv.Bound != null && !IsAssignable(tv.Bound, arg) ? tv.Bound : arg;
    }

    /// <summary>
    /// `Callable[Concatenate[X, P], R]` matched with what is passed: X takes its first positional parameter (a *args
    /// takes them all), and P the parameters after it, with their names, kinds and defaults. Left unsolved when they do
    /// not fit (X and nothing positional), and made "any arguments" when two arguments solve one P differently.
    /// </summary>
    void SolveParamSpec(SignatureType param, SignatureType given, Dictionary<string, PyType> solved)
    {
        var rest = given.Params!.ToList();
        foreach (var lead in param.Params!)
        {
            if (rest.Count == 0 || rest[0].Category is ParamCategory.KeywordOnly or ParamCategory.VarKeyword) return;
            Unify(lead.Type, rest[0].Type, solved);
            if (rest[0].Category != ParamCategory.VarPositional) rest.RemoveAt(0);
        }
        var value = given with { Params = rest, Return = PyType.Unknown };
        string name = param.Spec!.Name;
        solved[name] = solved.TryGetValue(name, out var prev) && !prev.Equals(value) ? new SignatureType(null, PyType.Unknown) : value;
    }

    /// <summary>
    /// The parameters calling a value takes, to solve a ParamSpec from: a def's (without a bound receiver, the def kept as
    /// the origin), a Callable's, a class's constructor's, an instance's `__call__`'s. Null when not known, as for an
    /// overloaded function, whose parameters depend on the overload a call picks.
    /// </summary>
    SignatureType? ParametersOfValue(PyType value)
    {
        switch (value)
        {
            case FunctionType f: return new SignatureType(CallableParameters(f), PyType.Unknown, null, f);
            case SignatureType { Params: not null } s: return s;
            case ClassType { IsInstance: false } cls:
                return ConstructorParameters(cls) is { } ctor ? new SignatureType(ctor, PyType.Unknown) : null;
            case ClassType inst when FindMember(inst, "__call__", true, null, out _) is { } call:
                return BoundMember(call, inst, true) is FunctionType bound ? ParametersOfValue(bound) : null;
            default: return null;
        }
    }

    /// <summary>What calling a class takes, chosen as <see cref="CallClass"/> does: the synthesized __init__, else the class's own __init__, else its __new__.</summary>
    IReadOnlyList<ParamInfo>? ConstructorParameters(ClassType cls)
    {
        if (IsEnumClass(cls.Info) || cls.Info.QualifiedName is "builtins.type" || cls.Info.Module.Name is "typing" or "typing_extensions") return null;
        var receiver = new ClassType(cls.Info, cls.Args.Count > 0 ? cls.Args : ClassTypeParamsAsArgs(cls.Info), true);
        if (SynthesizedInit(receiver) is { } synthesized) return synthesized.Params;
        var init = FindMember(receiver, "__init__", true, null, out bool unkInit);
        if (init != null && UnknownBaseBefore(cls.Info, init.Owner.Info)) return null;   // a base that is not known comes first: its __init__ is not known either
        if (init != null && init.Owner.Info.QualifiedName != "builtins.object")
            return BoundMember(init, receiver, true) is FunctionType i ? CallableParameters(i) : null;
        var @new = FindMember(receiver, "__new__", true, null, out bool unkNew);
        if (@new != null && @new.Owner.Info.QualifiedName != "builtins.object")
            return BoundMember(@new, receiver, true) is FunctionType n ? CallableParameters(n with { BoundFirst = true }) : null;
        return unkInit || unkNew || HasUnknownAncestor(cls.Info) ? null : Array.Empty<ParamInfo>();
    }

    /// <summary>
    /// Solves the type arguments of a protocol parameter (SupportsNext[_T]) from an argument that merely has the right
    /// members: each protocol member is matched with the argument's and their result types are unified.
    /// </summary>
    readonly HashSet<(ClassInfo, ClassInfo)> _protocolUnifying = new();

    void UnifyProtocol(ClassType proto, ClassType arg, Dictionary<string, PyType> solved)
    {
        var protoParams = ClassDataOf(proto.Info).TypeParams;
        if (protoParams.Count == 0) return;
        // protocols refer to each other (an iterator's __iter__ returns an iterator): visit each pair once
        if (!_protocolUnifying.Add((proto.Info, arg.Info))) return;
        try { UnifyProtocolCore(proto, arg, protoParams, solved); }
        finally { _protocolUnifying.Remove((proto.Info, arg.Info)); }
    }

    void UnifyProtocolCore(ClassType proto, ClassType arg, IReadOnlyList<PyType> protoParams, Dictionary<string, PyType> solved)
    {
        var self = new ClassType(proto.Info, protoParams.ToList(), true);
        var inner = new Dictionary<string, PyType>();

        foreach (var e in Mro(proto.Info))
        {
            if (e is not ClassType pe || !ClassDataOf(pe.Info).IsProtocol) continue;
            foreach (var kv in pe.Info.Scope.Symbols)
            {
                if (kv.Value.Declarations.Count == 0 || NotProtocolMember.Contains(kv.Key)) continue;
                var pHit = FindMember(self, kv.Key, true, null, out _);
                var aHit = FindMember(arg, kv.Key, true, null, out _);
                if (pHit == null || aHit == null) continue;
                var pType = BoundMember(pHit, self, true);
                var aType = BoundMember(aHit, arg, true);
                UnifyResults(pType, aType, inner);
            }
        }
        for (int i = 0; i < proto.Args.Count && i < protoParams.Count; i++)
            if (inner.TryGetValue(TypeParamName(protoParams[i]), out var found)) Unify(proto.Args[i], found, solved);
    }

    /// <summary>Unifies what two members produce: for methods their return types, for plain attributes their types.</summary>
    void UnifyResults(PyType protocolMember, PyType argMember, Dictionary<string, PyType> inner)
    {
        PyType? Result(PyType t) => t switch
        {
            FunctionType f => ReturnTypeOf(f),
            OverloadedType o when o.Overloads.Count > 0 => ReturnTypeOf(o.Overloads[0]),
            SignatureType s => s.Return,
            _ => t,
        };
        var p = Result(protocolMember);
        var a = Result(argMember);
        if (p != null && a != null) Unify(p, a, inner);
    }

    /// <summary>
    /// Lenient assignability: only reports false when both sides are well known and clearly incompatible. A generic class's
    /// arguments compare by the variance of its parameters; <paramref name="fresh"/> says the source is a value just built
    /// (a list display, a generic call), which takes the type wanted, so an invariant argument only has to fit.
    /// </summary>
    public bool IsAssignable(PyType dest, PyType src, bool fresh = false)
    {
        if (dest is UnknownType || src is UnknownType || src is NeverType) return true;
        if (dest is TypeVarType or ParamSpecType or TypeVarTupleType || src is TypeVarType or ParamSpecType or TypeVarTupleType) return true;
        if (src is ClassType anyClass && IsClassOfAny(anyClass)) return true;   // type[Any] may be whichever class is wanted
        // (loops of their own, not lambdas: a lambda here would make every call allocate the parameters it captures)
        if (src is UnionType su && LenientUnions && _strictUnions == 0 && !su.MemberList.Any(x => x is ClassType { LiteralValue: not null } or ClassType { EnumMember: not null })) return LenientUnionAssignable(dest, su, fresh);
        if (src is UnionType su2) return EachAssignable(dest, MergeInstancesOfOneClass(su2.MemberList), fresh);
        if (dest is UnionType du) return AnyAssignable(du.MemberList, src, fresh);

        switch (dest)
        {
            case SignatureType ds:
                if (src is FunctionType or SignatureType) return CallbackFits(ds, src);
                return src is OverloadedType or ClassType { IsInstance: false }
                    || (src is ClassType inst && FindMember(inst, "__call__", true, null, out _) != null);

            case ClassType { IsInstance: false } dc:
                return src is ClassType { IsInstance: false } sc && IsSubclass(sc.Info, dc.Info);

            case ClassType dc:
            {
                if (dc.Info.QualifiedName == "builtins.object") return true;
                if (src is FunctionType or OverloadedType or SignatureType)
                    return dc.Info.Name is "function" or "Callable" or "object";
                if (src is ModuleType) return dc.Info.Name == "module";
                if (src is not ClassType s) return true; // unknown shapes: do not complain

                if (!s.IsInstance) return dc.Info.Name is "type" or "object" || IsProtocolSatisfiedByClassObject(dc);

                // one literal is not another: Literal['a'] does not go where Literal['b'] or Color.GREEN is wanted
                if (dc.LiteralValue != null && s.LiteralValue != null && ReferenceEquals(dc.Info, s.Info) && !Equals(dc.LiteralValue, s.LiteralValue)) return false;
                if (dc.EnumMember != null && s.EnumMember != null && ReferenceEquals(dc.Info, s.Info) && dc.EnumMember != s.EnumMember) return false;

                // numeric tower promotions
                if (dc.Info.Module.Name == "builtins" && s.Info.Module.Name == "builtins")
                {
                    if (dc.Info.Name == "float" && s.Info.Name is "int" or "bool") return true;
                    if (dc.Info.Name == "complex" && s.Info.Name is "float" or "int" or "bool") return true;
                    // bytes accepts the other byte buffers, as the typing spec's old promotion (and PyCharm) does
                    if (dc.Info.Name == "bytes" && s.Info.Name is "bytearray" or "memoryview") return true;
                }

                // a dict literal where a TypedDict is expected: its keys are not checked here
                if (s.Info.QualifiedName == "builtins.dict" && TypedDictFieldsOf(dc.Info) != null) return true;

                if (TupleLengthsDiffer(dc, s)) return false;
                if (TupleItemsDiffer(dc, s, fresh)) return false;

                var spec = SpecializeAs(s, dc.Info);
                if (spec != null)
                {
                    var ps = ClassDataOf(dc.Info).TypeParams;
                    // with a *Ts the arguments do not line up with the parameters: compared either way, as for an unknown variance
                    // so does a source with an unknown base, which may give the class other arguments
                    bool aligned = ps.Count == dc.Args.Count && !ps.Any(p => p is TypeVarTupleType) && !HasUnknownAncestor(s.Info);
                    for (int i = 0; i < dc.Args.Count && i < spec.Args.Count; i++)
                    {
                        var variance = aligned && ps[i] is TypeVarType tv ? tv.Variance : Variance.Unknown;
                        // a value just built takes the type wanted (`[1, 2]` is a list[float] there), so only its items have to fit
                        if (fresh && variance == Variance.Invariant) variance = Variance.Covariant;
                        // an inferred union is often wider than what flows there (str-or-bytes code, a None ruled out): either way, as before
                        if (spec.Args[i] is UnionType) variance = Variance.Unknown;
                        bool fits = variance switch
                        {
                            // both ways, so int-to-float promotion does not make a list[int] a list[float]
                            Variance.Invariant => IsAssignable(dc.Args[i], spec.Args[i]) && IsAssignable(spec.Args[i], dc.Args[i]),
                            Variance.Covariant => IsAssignable(dc.Args[i], spec.Args[i], fresh),
                            Variance.Contravariant => IsAssignable(spec.Args[i], dc.Args[i]),
                            _ => IsAssignable(dc.Args[i], spec.Args[i], fresh) || IsAssignable(spec.Args[i], dc.Args[i]),
                        };
                        if (!fits) return false;
                    }
                    return true;
                }
                if (ClassDataOf(dc.Info).IsProtocol) return Satisfies(dc, s);
                if (HasUnknownAncestor(s.Info)) return true;
                return false;
            }
            default: return true;
        }
    }

    /// <summary>
    /// PyCharm's union rule before 2026.1, which its Advanced Settings still offer ("python.typing.strict.unions" off): a union goes
    /// where one of its members does (`str | None` where a str is wanted). PyCharm 2026.1 and later want every member to fit, as
    /// mypy does, and so does PyMagic unless this is set; a union with a literal member is checked member by member either way,
    /// as in PyCharm (PyTypeChecker's "checking strictly separately until PY-24834"). Set it before the evaluator is used: types it
    /// has worked out stay cached.
    /// </summary>
    public bool LenientUnions { get; set; }

    int _strictUnions;

    /// <summary>
    /// <see cref="IsAssignable"/> with every member of a union checked whatever <see cref="LenientUnions"/> says: for the narrowing
    /// of a declared name to what is assigned to it, where a list[None] must not pass for a list[Expr | None].
    /// </summary>
    internal bool IsAssignableEachMember(PyType dest, PyType src)
    {
        _strictUnions++;
        try { return IsAssignable(dest, src); }
        finally { _strictUnions--; }
    }

    bool LenientUnionAssignable(PyType dest, UnionType src, bool fresh)
    {
        foreach (var m in src.MemberList)
            if (IsAssignable(dest, m, fresh)) return true;
        return false;
    }

    bool EachAssignable(PyType dest, IEnumerable<PyType> sources, bool fresh)
    {
        foreach (var m in sources)
            if (!IsAssignable(dest, m, fresh)) return false;
        return true;
    }

    bool AnyAssignable(IReadOnlyList<PyType> dests, PyType src, bool fresh)
    {
        foreach (var m in dests)
            if (IsAssignable(m, src, fresh)) return true;
        return false;
    }

    /// <summary>
    /// A tuple whose length is known where a tuple of another fixed length is wanted (`tuple[int, int, int]` for `tuple[int, int]`, as PyCharm
    /// reports it), or one of "at least n" items (item 188: `tuple[int, int, *tuple[int, ...]]` after `len(t) >= 2`) where fewer are wanted.
    /// A tuple of any length (`tuple[int, ...]`) may have the length wanted, as in PyCharm.
    /// </summary>
    static bool TupleLengthsDiffer(ClassType dest, ClassType src)
    {
        if (dest.TupleItems is not { } want || src.TupleItems is not { } have || !IsBuiltinSequence(dest, "tuple") || !IsBuiltinSequence(src, "tuple")) return false;
        if (want.Any(w => w is TypeVarTupleType or UnboundedItemsType) || have.Any(h => h is TypeVarTupleType)) return false;
        int least = have.Count(h => h is not UnboundedItemsType);
        return have.Any(h => h is UnboundedItemsType) ? least > want.Count : least != want.Count;
    }

    /// <summary>
    /// A tuple of known items compared item by item, as PyCharm does (`tuple[int, str]` for `tuple[int, int]`: "Expected type
    /// 'tuple[int, int]', got 'tuple[int, str]' instead"): with a wanted tuple of the same length each item against its own, with
    /// `tuple[int, ...]` each item against int. The union of the items alone (the tuple's one argument) lets them pass either way.
    /// An Unknown item fits whatever is wanted.
    /// </summary>
    bool TupleItemsDiffer(ClassType dest, ClassType src, bool fresh)
    {
        if (src.TupleItems is not { } have || !IsBuiltinSequence(dest, "tuple") || !IsBuiltinSequence(src, "tuple") || have.Any(h => h is TypeVarTupleType)) return false;
        if (dest.TupleItems is { } want)
        {
            if (want.Count != have.Count || want.Any(w => w is TypeVarTupleType or UnboundedItemsType) || have.Any(h => h is UnboundedItemsType)) return false;
            for (int i = 0; i < want.Count; i++)
                if (!IsAssignable(want[i], have[i], fresh)) return true;
            return false;
        }
        if (dest.Args is not [var element]) return false;
        foreach (var h in have)
            if (!IsAssignable(element, h is UnboundedItemsType u ? u.Item : h, fresh)) return true;
        return false;
    }

    /// <summary>
    /// Instances of one generic class in a union as one instance of it, with the union of their arguments: a variable assigned
    /// `[Table(...)]` here and a list[ConsoleRenderable] there is a list of either, not two lists to check one by one.
    /// </summary>
    static IEnumerable<PyType> MergeInstancesOfOneClass(IReadOnlyList<PyType> members)
    {
        static bool Generic(PyType t) => t is ClassType { IsInstance: true, Args.Count: > 0, TupleItems: null, LiteralValue: null };
        foreach (var group in members.GroupBy(t => Generic(t) ? ((ClassType)t).Info : null))
        {
            var list = group.ToList();
            if (group.Key == null || list.Count == 1 || list.Any(t => ((ClassType)t).Args.Count != ((ClassType)list[0]).Args.Count)) { foreach (var t in list) yield return t; continue; }
            var first = (ClassType)list[0];
            yield return first with { Args = first.Args.Select((_, i) => PyType.Union(list.Select(t => ((ClassType)t).Args[i]))).ToList() };
        }
    }

    /// <summary>
    /// A def or a signature where a Callable is wanted: its declared result goes where the wanted one does (covariant), and the
    /// wanted parameters' types go into its parameters (contravariant), checked by calling it with them. Only the types are
    /// compared, and nothing when either parameter list is unknown or not plain positional.
    /// </summary>
    bool CallbackFits(SignatureType want, PyType src)
    {
        // an inferred result is a guess, a lambda's included
        var ret = src switch
        {
            FunctionType f when DeclaredReturn(f.Info) != null => ReturnTypeOf(f),
            SignatureType s => s.Return,
            _ => null,
        };
        if (ret != null && !IsAssignable(want.Return, ret)) return false;
        if (want.Params == null || want.Spec != null || src is SignatureType { Params: null } or SignatureType { Spec: not null }) return true;
        if (want.Params.Any(p => p.Category is not (ParamCategory.PositionalOnly or ParamCategory.Normal))) return true;
        var args = want.Params.Select(p => new ArgInfo(null, null, 0, () => p.Type)).ToList();
        return CallValue(src, args, null).All(t => t.Problems.All(p => p.Kind != CallProblemKind.Type));
    }

    bool IsProtocolSatisfiedByClassObject(ClassType proto) => ClassDataOf(proto.Info).IsProtocol;

    /// <summary>Bookkeeping names in a class body that are not part of the interface a protocol requires.</summary>
    static readonly HashSet<string> NotProtocolMember = new()
    {
        "__slots__", "__doc__", "__module__", "__qualname__", "__dict__", "__annotations__", "__init__", "__new__",
        "__init_subclass__", "__class_getitem__", "__weakref__", "__match_args__", "__parameters__", "__orig_bases__",
        "__abstractmethods__", "_is_protocol", "_is_runtime_protocol",
    };

    /// <summary>Structural check: the source has every member the protocol declares in its own body.</summary>
    bool Satisfies(ClassType proto, ClassType src)
    {
        foreach (var e in Mro(proto.Info))
        {
            if (e is not ClassType pe || !ClassDataOf(pe.Info).IsProtocol) continue;
            foreach (var kv in pe.Info.Scope.Symbols)
            {
                if (kv.Value.Declarations.Count == 0) continue;
                if (NotProtocolMember.Contains(kv.Key)) continue;
                var hit = FindMember(src, kv.Key, true, null, out bool unk);
                // the decorator gives a dataclass its __dataclass_fields__ (what typeshed's DataclassInstance asks for)
                if (hit == null && kv.Key == "__dataclass_fields__" && HasDataclassFields(src.Info)) continue;
                if (hit == null) { if (!unk) return false; continue; }
                if (hit.InstanceAttrs.Count == 0 && !MethodFits(kv.Value, hit.ClassSymbol)) return false;
            }
        }
        return true;
    }

    /// <summary>
    /// A class made by @dataclass (dataclasses' or pydantic's, `@dataclass(...)` and `@dataclasses.dataclass` too), or derived from one: it has
    /// __dataclass_fields__. attrs classes and dataclass_transform ones (a pydantic model) do not.
    /// </summary>
    bool HasDataclassFields(ClassInfo c) => Mro(c).Any(e => e is ClassType ec && DataclassOf(ec.Info) is { Attrs: false }
        && ec.Info.Node.Decorators.Any(d => (d is CallExpr call ? call.Func : d) is NameExpr { Name: "dataclass" } or AttributeExpr { Attr: "dataclass" }));

    /// <summary>A method of the source fits the protocol's when a call written for the protocol's signature works on it (plain defs only).</summary>
    bool MethodFits(Symbol proto, Symbol? src)
    {
        if (src == null || proto.Declarations.Count != 1 || src.Declarations.Count != 1) return true;
        if (proto.Declarations[0].Node is not FunctionDef pd || src.Declarations[0].Node is not FunctionDef sd) return true;
        if (!IsPlainMethod(pd) || !IsPlainMethod(sd)) return true;
        var pi = FunctionOf(_ws.ModuleOf(proto) ?? throw new InvalidOperationException(), pd);
        var si = FunctionOf(_ws.ModuleOf(src) ?? throw new InvalidOperationException(), sd);
        if (pi.IsStatic != si.IsStatic || pi.IsClassMethod != si.IsClassMethod) return true;
        var (pp, pk, pVar, pKw) = Shape(pd, !pi.IsStatic);
        var (sp, sk, sVar, sKw) = Shape(sd, !si.IsStatic);
        if (!sVar && sp.Count < pp.Count) return false;
        if (!pVar && sp.Count(p => p.Default == null) > pp.Count) return false;
        if (!sKw && pk.Any(k => !sk.Any(x => x.Name == k.Name) && !sp.Any(x => x.Name == k.Name))) return false;
        if (!pKw && sk.Any(k => k.Default == null && !pk.Any(x => x.Name == k.Name))) return false;
        return true;
    }
}
