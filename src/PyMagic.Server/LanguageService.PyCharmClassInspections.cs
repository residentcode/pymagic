using PyMagic.Core.Semantics;
using PyMagic.Core.Syntax;
using PyMagic.Core.Types;
using SyntaxPattern = PyMagic.Core.Syntax.Pattern;

namespace PyMagic.Server;

/// <summary>
/// PyCharm's inspections of protocols, variance, class layouts, type parameters, match patterns and boolean conditions: a protocol with
/// a base that is not one, isinstance() with a protocol that is not @runtime_checkable, a covariant type variable taken as a parameter,
/// bases whose instance layouts conflict, Generic[...] or a TypeVar beside type parameter syntax, patterns Python refuses, a conditional
/// that only repeats its condition, and a condition that is always true (a method not called, a coroutine not awaited).
/// </summary>
public sealed partial class LanguageService
{
    IEnumerable<Diag> PyCharmClassDiagnostics(ModuleInfo m, TypeEvaluator ev)
    {
        if (m.IsStub) yield break;
        foreach (var f in ClassAndPatternFindings(m, ev))
            if (!Suppressed(m.Source, f)) yield return AsDiag(f);
    }

    IEnumerable<Finding> ClassAndPatternFindings(ModuleInfo m, TypeEvaluator ev)
    {
        Finding At(Node n, string message, string code, FindingLevel level = FindingLevel.Error) => new(n.Start, Math.Max(n.End - n.Start, 1), message, code, level);

        foreach (var node in AstChildren.Descendants(m.Ast))
        {
            switch (node)
            {
                case ClassDef cd:
                    foreach (var f in ProtocolBases(m, cd, ev)) yield return f;
                    foreach (var f in VarianceFindings(m, cd)) yield return f;
                    if (DisjointBases(m, cd, ev) is { } layout) yield return At(cd.Bases[layout.Index].Value, layout.Message, "disjoint-bases");
                    if (cd.TypeParams.Count > 0)
                        foreach (var b in cd.Bases.Where(b => b.Name == null && b.Value is SubscriptExpr { Value: var g } && TypingName(g) is "Generic"))
                            yield return At(b.Value, "Generic[...] cannot be a base of a class with type parameters (class C[T]): the parameters make the class generic already, and Python raises TypeError", "type-params");
                    foreach (var f in TypeParamFindings(m, cd, cd.TypeParams)) yield return f;
                    break;
                case FunctionDef fd:
                    foreach (var f in TypeParamFindings(m, fd, fd.TypeParams)) yield return f;
                    break;
                case TypeAliasStmt ta:
                    foreach (var f in TypeParamFindings(m, ta, ta.TypeParams)) yield return f;
                    break;
                case CallExpr call when TypingName(call.Func) is "isinstance" or "issubclass" && call.Args.Count == 2:
                {
                    var classes = call.Args[1].Value is TupleExpr t ? t.Items : new[] { call.Args[1].Value };
                    foreach (var c in classes)
                        // the project's protocols only: collections.abc's Iterable and the like are ABCs at run time, though the stubs write them as protocols
                        if (ev.TypeOf(m, c) is ClassType { IsInstance: false } pc && !pc.Info.Module.IsStub && ev.IsProtocol(pc.Info) && pc.Info.Node is ClassDef pd
                            && !pd.Decorators.Any(d => DecoratorName(d) is "runtime_checkable" or "runtime") && pc.Info.Name != "Protocol")
                            yield return At(c, $"{TypingName(call.Func)}() with the protocol '{pc.Info.Name}', which is not @runtime_checkable: Python raises TypeError", "protocol");
                    break;
                }
                case MatchStmt ms:
                    foreach (var f in PatternFindings(m, ms, ev)) yield return f;
                    break;
                case IfExpr te when RedundantConditional(te) is { } plain:
                    yield return At(te, $"The conditional only repeats its condition: write {plain}", "redundant-boolean", FindingLevel.Weak);
                    break;
                case IfStmt ifs when AlwaysTrue(m, ifs.Test, ev) is { } why:
                    yield return At(ifs.Test, why, "suspicious-boolean", FindingLevel.Warning);
                    break;
                case WhileStmt ws when AlwaysTrue(m, ws.Test, ev) is { } why:
                    yield return At(ws.Test, why, "suspicious-boolean", FindingLevel.Warning);
                    break;
            }
        }
    }

    // ------------------------------------------------------------ protocols

    IEnumerable<Finding> ProtocolBases(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        bool isProtocol = cd.Bases.Any(b => b.Name == null && TypingName(b.Value is SubscriptExpr s ? s.Value : b.Value) == "Protocol");
        if (cd.Decorators.Any(d => DecoratorName(d) == "runtime_checkable") && !isProtocol && !(ClassInfoOf(m, cd, ev) is { } own && ev.IsProtocol(own)))
            yield return new Finding(cd.NameStart, cd.Name.Length, $"@runtime_checkable applies to protocol classes only, and '{cd.Name}' is not one: Python raises TypeError", "protocol", FindingLevel.Error);
        if (!isProtocol) yield break;
        foreach (var b in cd.Bases.Where(b => b.Name == null))
        {
            var head = b.Value is SubscriptExpr s ? s.Value : b.Value;
            if (TypingName(head) is "Protocol" or "Generic" or "object") continue;
            if (ev.TypeOf(m, head) is ClassType { IsInstance: false } bc && !ev.IsProtocol(bc.Info))
                yield return new Finding(b.Value.Start, b.Value.End - b.Value.Start, $"A protocol's bases must be protocols, and '{bc.Info.Name}' is not one: Python raises TypeError", "protocol", FindingLevel.Error);
        }
    }

    // ------------------------------------------------------------ variance

    /// <summary>`T_co = TypeVar("T_co", covariant=True)`: "covariant" / "contravariant" for a name bound that way, else null.</summary>
    static string? VarianceOf(ModuleInfo m, NameExpr name)
    {
        if (m.Semantics.Resolve(name) is not { } sym) return null;
        foreach (var d in sym.Declarations)
            if (d.Node is NameExpr t && m.ParentOf(t) is AssignStmt { Value: CallExpr c } && TypingName(c.Func) == "TypeVar")
            {
                if (c.Args.Any(a => a.Name == "covariant" && a.Value is ConstExpr { Kind: ConstKind.True })) return "covariant";
                if (c.Args.Any(a => a.Name == "contravariant" && a.Value is ConstExpr { Kind: ConstKind.True })) return "contravariant";
            }
        return null;
    }

    /// <summary>The names an annotation is made of at its top: `T`, `T | None`, `Optional[T]` (not inside other types, where variance turns).</summary>
    static IEnumerable<NameExpr> TopNames(Expr annotation) => annotation switch
    {
        NameExpr n => new[] { n },
        BinaryExpr { Op: "|" } b => TopNames(b.Left).Concat(TopNames(b.Right)),
        SubscriptExpr { Value: var v, Index: var i } when TypingName(v) is "Optional" or "Union" => i is TupleExpr t ? t.Items.SelectMany(TopNames) : TopNames(i),
        _ => Array.Empty<NameExpr>(),
    };

    IEnumerable<Finding> VarianceFindings(ModuleInfo m, ClassDef cd)
    {
        foreach (var fd in cd.Body.OfType<FunctionDef>())
        {
            if (fd.Name is "__init__" or "__new__") continue;
            foreach (var p in fd.Params.Skip(fd.Decorators.Any(d => DecoratorName(d) == "staticmethod") ? 0 : 1).Where(p => p.Annotation != null))
                foreach (var n in TopNames(p.Annotation!).Where(n => VarianceOf(m, n) == "covariant"))
                    yield return new Finding(n.Start, n.Name.Length, $"The covariant type variable '{n.Name}' cannot be a parameter's type: use an invariant or contravariant one", "variance", FindingLevel.Warning);
            if (fd.Returns != null)
                foreach (var n in TopNames(fd.Returns).Where(n => VarianceOf(m, n) == "contravariant"))
                    yield return new Finding(n.Start, n.Name.Length, $"The contravariant type variable '{n.Name}' cannot be a return type: use an invariant or covariant one", "variance", FindingLevel.Warning);
        }
    }

    // ------------------------------------------------------------ instance layouts

    /// <summary>The builtin classes whose instances have a layout of their own: a class can derive from at most one line of them.</summary>
    static readonly HashSet<string> SolidBuiltins = new(StringComparer.Ordinal)
        { "int", "float", "complex", "str", "bytes", "bytearray", "tuple", "list", "dict", "set", "frozenset", "BaseException", "memoryview", "range", "slice" };

    /// <summary>The nearest class whose instance layout is its own: a builtin of that list, or a class typeshed marks @disjoint_base.</summary>
    static ClassInfo? SolidBaseOf(ClassInfo c, TypeEvaluator ev)
    {
        foreach (var entry in ev.MroOf(c))
        {
            if (entry is not ClassType e) return null;
            if (e.Info.Module.Name == "builtins" && SolidBuiltins.Contains(e.Info.Name)) return e.Info;
            if (e.Info.Node is ClassDef d && d.Decorators.Any(x => DecoratorName(x) == "disjoint_base")) return e.Info;
        }
        return null;
    }

    (int Index, string Message)? DisjointBases(ModuleInfo m, ClassDef cd, TypeEvaluator ev)
    {
        var solids = new List<(int Index, ClassInfo Solid)>();
        for (int i = 0; i < cd.Bases.Count; i++)
        {
            var b = cd.Bases[i];
            if (b.Name != null || b.Stars != 0) continue;
            if (ev.TypeOf(m, b.Value is SubscriptExpr s ? s.Value : b.Value) is ClassType { IsInstance: false } bc && SolidBaseOf(bc.Info, ev) is { } solid)
                solids.Add((i, solid));
        }
        for (int i = 1; i < solids.Count; i++)
            for (int j = 0; j < i; j++)
            {
                var (a, b) = (solids[j].Solid, solids[i].Solid);
                bool related = ev.MroOf(a).OfType<ClassType>().Any(x => ReferenceEquals(x.Info, b)) || ev.MroOf(b).OfType<ClassType>().Any(x => ReferenceEquals(x.Info, a));
                if (!related)
                    return (solids[i].Index, $"A class cannot derive from both '{a.Name}' and '{b.Name}': their instance layouts conflict, and Python raises TypeError");
            }
        return null;
    }

    // ------------------------------------------------------------ type parameter syntax

    IEnumerable<Finding> TypeParamFindings(ModuleInfo m, Node owner, IReadOnlyList<TypeParam> typeParams)
    {
        if (typeParams.Count == 0) yield break;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var tp in typeParams)
            if (!seen.Add(tp.Name))
                yield return new Finding(tp.Start, Math.Max(tp.End - tp.Start, 1), $"The type parameter '{tp.Name}' is declared twice: Python stops with SyntaxError", "type-params", FindingLevel.Error);
        // a TypeVar of the old kind used where type parameters are declared
        var annotations = owner switch
        {
            FunctionDef fd => fd.Params.Select(p => p.Annotation).Append(fd.Returns),
            TypeAliasStmt ta => new Expr?[] { ta.Value },
            // Generic[...] beside type parameters is reported as a whole
            ClassDef cd => cd.Bases.Where(b => !(b.Value is SubscriptExpr { Value: var g } && TypingName(g) == "Generic")).Select(b => (Expr?)b.Value),
            _ => Array.Empty<Expr?>(),
        };
        foreach (var a in annotations.OfType<Expr>())
            foreach (var n in AstChildren.Descendants(a).OfType<NameExpr>().DistinctBy(n => n.Start))
                if (!seen.Contains(n.Name) && IsOldTypeVar(m, n))
                    yield return new Finding(n.Start, n.Name.Length, $"'{n.Name}' is a TypeVar: it cannot be used beside type parameter syntax; declare it in the brackets instead", "type-params", FindingLevel.Error);
    }

    static bool IsOldTypeVar(ModuleInfo m, NameExpr n) =>
        m.Semantics.Resolve(n) is { } sym && sym.Declarations.Any(d => d.Node is NameExpr t && m.ParentOf(t) is AssignStmt { Value: CallExpr c } && TypingName(c.Func) is "TypeVar" or "ParamSpec" or "TypeVarTuple");

    // ------------------------------------------------------------ match patterns

    static bool Irrefutable(SyntaxPattern p) => p switch
    {
        MatchCapture => true,
        MatchAs a => Irrefutable(a.Pattern),
        MatchOr o => o.Alternatives.Any(Irrefutable),
        _ => false,
    };

    /// <summary>The capture or wildcard that makes a pattern match anything (`case [y] | z` has z).</summary>
    static SyntaxPattern? IrrefutablePart(SyntaxPattern p) => p switch
    {
        MatchCapture c => c,
        MatchAs a => IrrefutablePart(a.Pattern),
        MatchOr o => o.Alternatives.Select(IrrefutablePart).FirstOrDefault(x => x != null),
        _ => null,
    };

    /// <summary>The names a pattern binds, in order (a name twice when it is bound twice).</summary>
    static IEnumerable<(string Name, Node At)> Bound(SyntaxPattern p) => p switch
    {
        MatchCapture { Name: { } n } c => new[] { (n, (Node)c) },
        MatchStar { Name: { } n } s => new[] { (n, (Node)s) },
        MatchAs a => Bound(a.Pattern).Append((a.Name, a)),
        MatchOr o => o.Alternatives.Count > 0 ? Bound(o.Alternatives[0]) : Array.Empty<(string, Node)>(),
        MatchSequence s => s.Items.SelectMany(Bound),
        MatchMapping mp => mp.Patterns.SelectMany(Bound).Concat(mp.Rest is { } r ? new[] { (r, (Node)mp) } : Array.Empty<(string, Node)>()),
        MatchClass c => c.Positional.Concat(c.KeywordPatterns).SelectMany(Bound),
        _ => Array.Empty<(string, Node)>(),
    };

    IEnumerable<Finding> PatternFindings(ModuleInfo m, MatchStmt ms, TypeEvaluator ev)
    {
        for (int i = 0; i < ms.Cases.Count - 1; i++)
        {
            var c = ms.Cases[i];
            if (c.Guard == null && Irrefutable(c.Pattern))
            {
                string what = IrrefutablePart(c.Pattern) is MatchCapture { Name: { } n } ? $"name capture '{n}'" : "wildcard";
                yield return new Finding(c.Pattern.Start, Math.Max(c.Pattern.End - c.Pattern.Start, 1), $"The {what} makes the remaining patterns unreachable: Python stops with SyntaxError", "match-pattern", FindingLevel.Error);
            }
        }
        foreach (var c in ms.Cases)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (name, at) in Bound(c.Pattern))
                if (!names.Add(name))
                    yield return new Finding(at.Start, Math.Max(at.End - at.Start, 1), $"'{name}' is bound twice in one pattern: Python stops with SyntaxError", "match-pattern", FindingLevel.Error);
            foreach (var p in AstChildren.Descendants(c.Pattern).OfType<SyntaxPattern>())
                foreach (var f in OnePattern(m, p, ev)) yield return f;
        }
    }

    IEnumerable<Finding> OnePattern(ModuleInfo m, SyntaxPattern p, TypeEvaluator ev)
    {
        Finding At(Node n, string message) => new(n.Start, Math.Max(n.End - n.Start, 1), message, "match-pattern", FindingLevel.Error);
        switch (p)
        {
            case MatchOr o when o.Alternatives.Count > 1:
            {
                var first = Bound(o.Alternatives[0]).Select(b => b.Name).ToHashSet(StringComparer.Ordinal);
                if (o.Alternatives.Skip(1).Any(a => !Bound(a).Select(b => b.Name).ToHashSet(StringComparer.Ordinal).SetEquals(first)))
                    yield return At(o, "The alternatives bind different names: Python stops with SyntaxError");
                break;
            }
            case MatchClass mc:
            {
                var repeated = mc.KeywordNames.GroupBy(k => k).FirstOrDefault(g => g.Count() > 1);
                if (repeated != null) yield return At(mc, $"The attribute '{repeated.Key}' is repeated in the class pattern: Python stops with SyntaxError");
                var t = ev.TypeOf(m, mc.Cls);
                if (t is FunctionType or OverloadedType || t is ClassType { IsInstance: true })
                {
                    yield return At(mc.Cls, $"'{m.Source[mc.Cls.Start..mc.Cls.End]}' is not a class: Python raises TypeError (called match pattern must be a class)");
                    break;
                }
                // a class of the project, or a builtin that matches itself; a library class without __match_args__ in its stub is not judged
                if (mc.Positional.Count > 0 && t is ClassType { IsInstance: false } cls)
                {
                    var (names, self) = ev.MatchArgsOf(cls);
                    int allowed = names?.Count ?? (self ? 1 : 0);
                    bool known = names != null || self || !cls.Info.Module.IsStub;
                    if (known && mc.Positional.Count > allowed)
                        yield return At(mc, $"{cls.Info.Name}() accepts {allowed} positional sub-pattern{(allowed == 1 ? "" : "s")} ({mc.Positional.Count} given): Python raises TypeError");
                }
                break;
            }
            case MatchMapping mp:
            {
                var keys = new HashSet<string>(StringComparer.Ordinal);
                foreach (var k in mp.Keys)
                    if (k is ConstExpr && !keys.Add(m.Source[k.Start..k.End]))
                        yield return At(k, $"The key {m.Source[k.Start..k.End]} is checked twice: Python stops with SyntaxError");
                break;
            }
        }
    }

    // ------------------------------------------------------------ boolean conditions

    /// <summary>`True if c else False` is c (written bool(c) when c is not a comparison), `False if c else True` is not c.</summary>
    static string? RedundantConditional(IfExpr te)
    {
        bool t = te.Body is ConstExpr { Kind: ConstKind.True }, f = te.Body is ConstExpr { Kind: ConstKind.False };
        bool et = te.Orelse is ConstExpr { Kind: ConstKind.True }, ef = te.Orelse is ConstExpr { Kind: ConstKind.False };
        bool boolean = te.Test is CompareExpr or BinaryExpr { Op: "and" or "or" } or UnaryExpr { Op: "not" };
        if (t && ef) return boolean ? "the condition itself" : "bool(...) of the condition";
        if (f && et) return "not (the condition)";
        return null;
    }

    /// <summary>Why a condition is always true: a function or method not called, a coroutine not awaited; null otherwise.</summary>
    static string? AlwaysTrue(ModuleInfo m, Expr test, TypeEvaluator ev)
    {
        static IEnumerable<Expr> Operands(Expr e) => e switch
        {
            BinaryExpr { Op: "and" or "or" } b => Operands(b.Left).Concat(Operands(b.Right)),
            UnaryExpr { Op: "not" } u => Operands(u.Operand),
            _ => new[] { e },
        };
        foreach (var e in Operands(test))
        {
            if (e is NameExpr or AttributeExpr && ev.TypeOf(m, e) is FunctionType or OverloadedType)
                return $"This condition is always true: '{m.Source[e.Start..e.End]}' is a function, not its result; call it";
            if (e is CallExpr && ev.TypeOf(m, e) is ClassType { Info.Name: "Coroutine" or "CoroutineType" })
                return $"This condition is always true: '{m.Source[e.Start..e.End]}' is a coroutine; await it";
        }
        return null;
    }
}
