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
sealed record ArgInfo(Argument? Node, string? Name, int Stars, Func<PyType> Type);

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
                    result.Add(new ParamInfo(p.Name, Ty(), ParamCategory.VarKeyword, false));
                    break;
                default:
                {
                    var cat = afterStar ? ParamCategory.KeywordOnly : (i < slash ? ParamCategory.PositionalOnly : ParamCategory.Normal);
                    result.Add(new ParamInfo(p.Name, Ty(), cat, p.Default != null));
                    break;
                }
            }
        }
        _params[info] = result;
        return result;
    }

    /// <summary>The parameters a call must supply for this function value (bound receiver removed, substitution applied).</summary>
    IReadOnlyList<ParamInfo> CallableParameters(FunctionType f)
    {
        var ps = ParametersOf(f.Info).ToList();
        if (f.BoundFirst && ps.Count > 0 && ps[0].Category is ParamCategory.PositionalOnly or ParamCategory.Normal) ps.RemoveAt(0);
        if (f.Subst != null && f.Subst.Count > 0) ps = ps.Select(p => p with { Type = Substitute(p.Type, f.Subst) }).ToList();
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

    PyType InferReturn(FunctionInfo info) => Memo(info, () =>
    {
        var m = info.Module;
        if (info.Node is LambdaExpr l) return TypeOf(m, l.Body);
        if (info.Node is not FunctionDef fd || m.IsStub) return PyType.Unknown;

        var flow = m.Flow.For(info.Scope);
        if (flow == null) return PyType.Unknown;

        var returns = new List<PyType>();
        foreach (var ins in flow.Graph.Instructions)
            if (ins.Kind == Flow.InstrKind.Return && flow.Graph.Reachable[ins.Id] && ins.Element is ReturnStmt r)
                returns.Add(r.Value == null ? NoneInstance : TypeOf(m, r.Value));
        if (flow.Graph.EndReachable) returns.Add(NoneInstance);

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
        var args = call.Args.Select(a => new ArgInfo(a, a.Name, a.Stars, () => TypeOf(m, a.Value))).ToList();

        // super() needs the enclosing method, not just the callee's type
        if (callee is FunctionType { Info: { Name: "super" } si } && si.Module.Name == "builtins")
            return Single(new CallTarget(callee, SuperOfCall(m, call, args), Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>()));

        var targets = CallValue(callee, args, call);
        return new CallResolution(targets.Count == 0 ? PyType.Unknown : PyType.Union(targets.Select(t => t.Returns)), targets);
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
                case UnknownType: result.Add(new CallTarget(c, PyType.Unknown, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>())); break;
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

    CallTarget CallFunction(FunctionType f, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var ps = CallableParameters(f);
        var (map, problems) = MapArguments(ps, args, callNode);

        var solved = new Dictionary<string, PyType>();
        var evaluated = new List<(ArgInfo Arg, ParamInfo Param, PyType Type)>();
        foreach (var (arg, param) in map)
        {
            if (param == null || arg.Stars != 0) continue;
            var at = arg.Type();
            evaluated.Add((arg, param, at));
            Unify(param.Type, at, solved);
        }
        foreach (var (arg, param, at) in evaluated)
        {
            var expected = Substitute(param.Type, solved);
            if (!IsAssignable(expected, at))
                problems.Add(new CallProblem(CallProblemKind.Type, (Node?)arg.Node ?? callNode,
                    $"Expected type '{expected}', got '{at}' instead"));
        }

        var ret = EraseTypeVars(Substitute(ReturnTypeOf(f), solved));
        var mappings = map.Where(x => x.Arg.Node != null).Select(x => new ArgumentMapping(x.Arg.Node!, x.Param)).ToList();
        return new CallTarget(f, ret, mappings, problems, solved);
    }

    CallTarget CallOverloaded(OverloadedType o, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var targets = o.Overloads.Select(f => CallFunction(f, args, callNode)).ToList();
        var pick = targets.FirstOrDefault(t => t.Problems.Count == 0)
                   ?? targets.FirstOrDefault(t => t.Problems.All(p => p.Kind != CallProblemKind.Arity));
        if (pick != null) return pick;
        return new CallTarget(o, PyType.Unknown, Array.Empty<ArgumentMapping>(),
            new[] { new CallProblem(CallProblemKind.Arity, callNode, $"No overload of '{o.Overloads[0].Info.Name}' matches these arguments") });
    }

    CallTarget CallSignature(SignatureType s, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        if (s.Params == null) return new CallTarget(s, s.Return, Array.Empty<ArgumentMapping>(), Array.Empty<CallProblem>());
        var (map, problems) = MapArguments(s.Params, args, callNode);
        var mappings = map.Where(x => x.Arg.Node != null).Select(x => new ArgumentMapping(x.Arg.Node!, x.Param)).ToList();
        return new CallTarget(s, s.Return, mappings, problems);
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
            return new CallTarget(cls, MapMembers(arg, t => t is ClassType { IsInstance: true } c ? c.AsClassObject() : PyType.Unknown), none, noProblems);
        }
        if (qn is "typing.TypeVar" or "typing.ParamSpec" or "typing.TypeVarTuple")
        {
            var first = args.FirstOrDefault()?.Node?.Value as ConstExpr;
            string name = first != null ? first.Text.Trim('"', '\'') : "?";
            return new CallTarget(cls, new TypeVarType(name), none, noProblems);
        }

        var data = ClassDataOf(cls.Info);
        var selfArgs = cls.Args.Count > 0 ? cls.Args : ClassTypeParamsAsArgs(cls.Info);
        var receiver = new ClassType(cls.Info, selfArgs, true);

        var init = FindMember(receiver, "__init__", true, null, out bool unkInit);
        var @new = FindMember(receiver, "__new__", true, null, out bool unkNew);
        bool userInit = init != null && init.Owner.Info.QualifiedName != "builtins.object";
        bool userNew = @new != null && @new.Owner.Info.QualifiedName != "builtins.object";

        CallTarget? inner = null;
        if (userInit)
            inner = Pick(CallValue(BoundMember(init!, receiver, true), args, callNode));
        else if (userNew)
        {
            // __new__ is an implicit static method; when constructing, the class is supplied automatically
            var withoutCls = MapMembers(BoundMember(@new!, receiver, true), t => t switch
            {
                FunctionType f => f with { BoundFirst = true },
                OverloadedType o => new OverloadedType(o.Overloads.Select(f => f with { BoundFirst = true }).ToList()),
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
            resultArgs = data.TypeParams.Select(tp =>
                inner?.Solved != null && inner.Solved.TryGetValue(tp.Name, out var v) && !ContainsTypeVar(v) ? v : PyType.Unknown).ToArray();

        PyType instance = new ClassType(cls.Info, resultArgs, true);
        // __new__ may return something else (e.g. a subclass or Self)
        if (userNew && !userInit && inner != null && inner.Returns is ClassType { IsInstance: true } nr
            && !ReferenceEquals(nr.Info, cls.Info) && nr.Info.Name != "Self") instance = nr;

        return new CallTarget(cls, instance, inner?.Mappings ?? none, inner?.Problems ?? noProblems, inner?.Solved);
    }

    bool HasUnknownAncestor(ClassInfo c) => Mro(c).Any(e => e is UnknownType);

    static CallTarget? Pick(List<CallTarget> targets) => targets.Count == 0 ? null : targets[0];

    static bool ContainsTypeVar(PyType t) => t switch
    {
        TypeVarType => true,
        ClassType c => c.Args.Any(ContainsTypeVar),
        UnionType u => u.MemberList.Any(ContainsTypeVar),
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
            if (r is UnknownType) { results.Add(PyType.Unknown); anyFound = true; continue; }
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
                if (l is UnknownType || r is UnknownType) { results.Add(PyType.Unknown); continue; }
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
        if (t is not ClassType { IsInstance: true } c) return PyType.Unknown;
        if (c.TupleItems != null) return c.TupleItems.Count == 0 ? PyType.Unknown : PyType.Union(c.TupleItems);
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
                case ClassType { IsInstance: false }:
                case SpecialFormType:
                    return AnnotationToValue(AnnotationSubscript(m, s, null));

                case ClassType { IsInstance: true, TupleItems: not null } tup:
                {
                    if (ConstIndex(s.Index) is int i)
                    {
                        int idx = i < 0 ? tup.TupleItems.Count + i : i;
                        if (idx >= 0 && idx < tup.TupleItems.Count) return tup.TupleItems[idx];
                        return PyType.Unknown;
                    }
                    if (s.Index is SliceExpr) return TupleHomogeneous(tup.Args.Count > 0 ? tup.Args[0] : PyType.Unknown);
                    break;
                }
            }
            if (r is ClassType { IsInstance: true } inst)
                return CallMethod(inst, "__getitem__", new[] { TypeOf(m, s.Index) }, out _) ?? PyType.Unknown;
            return PyType.Unknown;
        });
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
            case TypeVarType tv:
                if (tv.Name == "Self" || arg is NeverType) return;
                solved[tv.Name] = solved.TryGetValue(tv.Name, out var prev) && !prev.Equals(arg) ? PyType.Union(prev, arg) : arg;
                return;

            case ClassType pc when pc.Args.Count > 0 || pc.TupleItems != null:
                foreach (var a in arg.Members)
                {
                    if (a is not ClassType ac) continue;
                    var spec = SpecializeAs(ac, pc.Info);
                    if (spec == null) continue;
                    if (pc.TupleItems != null && spec.TupleItems != null && pc.TupleItems.Count == spec.TupleItems.Count)
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
                        _ => null,
                    };
                    if (ret != null) Unify(ps.Return, ret, solved);
                }
                return;
        }
    }

    /// <summary>Lenient assignability: only reports false when both sides are well known and clearly incompatible.</summary>
    public bool IsAssignable(PyType dest, PyType src)
    {
        if (dest is UnknownType || src is UnknownType || src is NeverType) return true;
        if (dest is TypeVarType || src is TypeVarType) return true;
        if (src is UnionType su) return su.MemberList.All(m => IsAssignable(dest, m));
        if (dest is UnionType du) return du.MemberList.Any(m => IsAssignable(m, src));

        switch (dest)
        {
            case SignatureType:
                return src is FunctionType or OverloadedType or SignatureType or ClassType { IsInstance: false }
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

                // numeric tower promotions
                if (dc.Info.Module.Name == "builtins" && s.Info.Module.Name == "builtins")
                {
                    if (dc.Info.Name == "float" && s.Info.Name is "int" or "bool") return true;
                    if (dc.Info.Name == "complex" && s.Info.Name is "float" or "int" or "bool") return true;
                }

                var spec = SpecializeAs(s, dc.Info);
                if (spec != null)
                {
                    for (int i = 0; i < dc.Args.Count && i < spec.Args.Count; i++)
                        if (!IsAssignable(dc.Args[i], spec.Args[i]) && !IsAssignable(spec.Args[i], dc.Args[i])) return false;
                    return true;
                }
                if (ClassDataOf(dc.Info).IsProtocol) return Satisfies(dc, s);
                if (HasUnknownAncestor(s.Info)) return true;
                return false;
            }
            default: return true;
        }
    }

    bool IsProtocolSatisfiedByClassObject(ClassType proto) => ClassDataOf(proto.Info).IsProtocol;

    /// <summary>Structural check: the source has every member the protocol declares in its own body.</summary>
    bool Satisfies(ClassType proto, ClassType src)
    {
        foreach (var e in Mro(proto.Info))
        {
            if (e is not ClassType pe || !ClassDataOf(pe.Info).IsProtocol) continue;
            foreach (var kv in pe.Info.Scope.Symbols)
            {
                if (kv.Value.Declarations.Count == 0) continue;
                if (FindMember(src, kv.Key, true, null, out bool unk) == null && !unk) return false;
            }
        }
        return true;
    }
}
