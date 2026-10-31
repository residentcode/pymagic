using PyMagic.Core.Syntax;

namespace PyMagic.Core.Types;

/// <summary>
/// Call-site return inference, as pyright does it: a call of a project function whose parameters are unannotated and
/// whose return is not declared gets the return type its body gives with the arguments' types (`add(1, 2)` is int,
/// `add("a", "b")` is str). The body is evaluated again by an evaluator of its own, made for that call: the types of
/// the expressions in it depend on the arguments, and the caches of this evaluator hold them for the function as it is
/// written, which its hover and its reports keep showing.
/// </summary>
public sealed partial class TypeEvaluator
{
    /// <summary>Calls inside calls followed, statements a body may have, argument-type combinations kept per function.</summary>
    const int CallSiteDepth = 2, CallSiteStatements = 40, CallSiteVariants = 4;

    /// <summary>On an evaluator made for one call: the types the arguments give the function's parameters.</summary>
    readonly IReadOnlyDictionary<Parameter, PyType>? _callSiteParams;
    readonly int _callSiteLevel;
    /// <summary>The functions being inferred for a call, outermost first: a call of one of them again is recursion and is cut.</summary>
    readonly IReadOnlyList<FunctionDef> _callSiteChain = Array.Empty<FunctionDef>();
    readonly Dictionary<FunctionDef, List<(PyType?[] Args, PyType Returns)>> _callSites = new(ReferenceEqualityComparer.Instance);

    /// <summary>
    /// An evaluator for one call of <paramref name="def"/>. It shares the class and function identities (types it gives
    /// back are compared by them) and works out everything else afresh.
    /// </summary>
    TypeEvaluator(TypeEvaluator parent, FunctionDef def, IReadOnlyDictionary<Parameter, PyType> parameters)
    {
        _ws = parent._ws;
        _classes = parent._classes;
        _functions = parent._functions;
        _callSiteParams = parameters;
        _callSiteLevel = parent._callSiteLevel + 1;
        _callSiteChain = parent._callSiteChain.Append(def).ToList();
    }

    /// <summary>What a call of the function returns: the call-site inferred type where that applies and says more, else <see cref="ReturnTypeOf"/>.</summary>
    PyType ReturnAtCallSite(FunctionType f, IReadOnlyList<ParamInfo> ps, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        var plain = ReturnTypeOf(f);
        // fully known already, or a call of the function inside its own inference
        if (!ContainsDynamic(plain) || plain is UnknownType { FromCycle: true }) return plain;
        if (CallSiteParameters(f, ps, args, callNode) is not { } given) return plain;

        var def = (FunctionDef)f.Info.Node;
        var key = def.Params.Select(p => given.TryGetValue(p, out var t) ? t : null).ToArray();
        if (!_callSites.TryGetValue(def, out var variants)) _callSites[def] = variants = new();
        foreach (var (k, r) in variants)
            if (k.SequenceEqual(key)) return r;
        if (variants.Count >= CallSiteVariants) return plain;

        var inferred = new TypeEvaluator(this, def, given).InferReturn(f.Info);
        inferred = Substitute(inferred, f.Subst);
        // nothing learnt, or a class only the other evaluator knows (a namedtuple made in the body): the plain answer
        var result = inferred is UnknownType || !KnownHere(inferred) ? plain : inferred;
        // a result that leaned on a cut cycle may change: kept only when nothing was cut
        if (_open.Count == 0) variants.Add((key, result));
        return result;
    }

    /// <summary>
    /// The types a call gives the unannotated parameters of a project function (not a library's or a stub's), or null
    /// where call-site inference does not apply: a decorated function, a generator or coroutine, a body that is large
    /// or defines a class, `*` / `**` arguments, a call that does not fit the parameters, or no argument of a known type.
    /// </summary>
    Dictionary<Parameter, PyType>? CallSiteParameters(FunctionType f, IReadOnlyList<ParamInfo> ps, IReadOnlyList<ArgInfo> args, Node? callNode)
    {
        if (f.Info.Node is not FunctionDef { Returns: null } def || _callSiteLevel >= CallSiteDepth || _callSiteChain.Contains(def)) return null;
        var m = f.Info.Module;
        if (m.IsStub || m.Kind is not (ResolutionKind.InMemory or ResolutionKind.UserCode)) return null;
        if ((f.Info.Flags & (FuncFlags.Generator | FuncFlags.Async)) != 0) return null;
        if (!def.Decorators.All(d => d is NameExpr { Name: "staticmethod" or "classmethod" })) return null;
        if (args.Any(a => a.Stars > 0)) return null;
        int statements = 0;
        foreach (var n in AstChildren.Descendants(def))
        {
            if (n is ClassDef || n is Stmt && ++statements > CallSiteStatements) return null;
        }

        var (map, problems) = MapArguments(ps, args, callNode);
        if (problems.Count > 0) return null;
        var given = new Dictionary<Parameter, PyType>(ReferenceEqualityComparer.Instance);
        foreach (var (arg, param) in map)
        {
            if (param == null || param.Category is ParamCategory.VarPositional or ParamCategory.VarKeyword) continue;
            var node = def.Params.FirstOrDefault(p => p.Kind == ParamKind.Normal && p.Name == param.Name);
            if (node == null || node.Annotation != null) continue;
            var t = arg.Type();
            if (!ContainsDynamic(t)) given[node] = t;
        }
        return given.Count == 0 ? null : given;
    }

    /// <summary>Whether every class and function a type names is the one this evaluator knows by that name.</summary>
    bool KnownHere(PyType t) => t switch
    {
        ClassType c => _classes.TryGetValue(c.Info.Node, out var ci) && ReferenceEquals(ci, c.Info)
            && c.Args.All(KnownHere) && (c.TupleItems?.All(KnownHere) ?? true),
        UnionType u => u.MemberList.All(KnownHere),
        FunctionType f => _functions.TryGetValue(f.Info.Node, out var fi) && ReferenceEquals(fi, f.Info),
        OverloadedType o => o.Overloads.All(KnownHere),
        SignatureType s => KnownHere(s.Return) && (s.Params?.All(p => KnownHere(p.Type)) ?? true),
        SuperType => false,
        _ => true,
    };
}
